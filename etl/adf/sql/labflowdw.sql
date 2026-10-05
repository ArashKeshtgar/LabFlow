-- Azure SQL database LabFlowDW: the cloud copy of the de-identified warehouse,
-- loaded by the Azure Data Factory pipeline PL_LabFlow_DW_to_Azure.
--
--   on-prem LabFlow.dw.*  --(self-hosted IR, Copy)-->  stg.*  --(etl.usp_MergeFromStage)-->  dw.*
--
-- Only de-identified data ever reaches this database: hashed patient keys,
-- shifted dates, no names, contacts, IDs or birth dates (see etl/README.md).
-- Keys are the on-prem surrogate keys, so nothing here is an IDENTITY.

SET XACT_ABORT ON;
GO
IF SCHEMA_ID('stg') IS NULL EXEC ('CREATE SCHEMA stg');
GO
IF SCHEMA_ID('dw') IS NULL EXEC ('CREATE SCHEMA dw');
GO
IF SCHEMA_ID('etl') IS NULL EXEC ('CREATE SCHEMA etl');
GO

/* ---------------- warehouse ---------------- */
IF OBJECT_ID('dw.DimDate') IS NULL
CREATE TABLE dw.DimDate (
    DateKey int NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY, [Date] date NOT NULL, [Year] smallint NOT NULL,
    [Quarter] tinyint NOT NULL, [Month] tinyint NOT NULL, MonthName varchar(10) NOT NULL, YearMonth char(7) NOT NULL,
    [Day] tinyint NOT NULL, WeekdayNo tinyint NOT NULL, WeekdayName varchar(10) NOT NULL, IsWeekend bit NOT NULL);
IF OBJECT_ID('dw.DimTest') IS NULL
CREATE TABLE dw.DimTest (
    TestKey int NOT NULL CONSTRAINT PK_DimTest PRIMARY KEY, LegacyAzId int NOT NULL, LegacyCode varchar(12) NULL,
    LegacyName nvarchar(50) NULL, LegacyUnit nvarchar(15) NULL, LoincCode varchar(10) NULL, LabFlowCode varchar(20) NULL,
    TestName nvarchar(150) NOT NULL, SiUnit varchar(20) NULL, Factor decimal(18,8) NULL, MapStatus varchar(12) NOT NULL,
    IsPanel bit NOT NULL, Section tinyint NULL);
IF OBJECT_ID('dw.DimPayer') IS NULL
CREATE TABLE dw.DimPayer (
    PayerKey smallint NOT NULL CONSTRAINT PK_DimPayer PRIMARY KEY, LegacyPayerId smallint NOT NULL,
    PayerName nvarchar(200) NOT NULL, PayerType varchar(10) NOT NULL);
IF OBJECT_ID('dw.DimPatient') IS NULL
CREATE TABLE dw.DimPatient (
    PatientKey int NOT NULL CONSTRAINT PK_DimPatient PRIMARY KEY, PatientHash char(16) NOT NULL, Sex char(1) NOT NULL);
IF OBJECT_ID('dw.FactLabResult') IS NULL
CREATE TABLE dw.FactLabResult (
    ResultKey bigint NOT NULL CONSTRAINT PK_FactLabResult PRIMARY KEY,
    LegacyVisitNo bigint NOT NULL, LegacyAzId smallint NOT NULL, LegacyChildAzId smallint NOT NULL,
    RowNo tinyint NOT NULL, RowNo2 smallint NOT NULL,
    TestKey int NOT NULL CONSTRAINT FK_Fact_Test REFERENCES dw.DimTest,
    PanelTestKey int NULL CONSTRAINT FK_Fact_Panel REFERENCES dw.DimTest,
    PatientKey int NOT NULL CONSTRAINT FK_Fact_Patient REFERENCES dw.DimPatient,
    PayerKey smallint NOT NULL CONSTRAINT FK_Fact_Payer REFERENCES dw.DimPayer,
    VisitDateKey int NOT NULL CONSTRAINT FK_Fact_VisitDate REFERENCES dw.DimDate,
    ResultDateKey int NULL CONSTRAINT FK_Fact_ResultDate REFERENCES dw.DimDate,
    AgeYears tinyint NULL, AgeBand varchar(8) NOT NULL, HasAnswer bit NOT NULL, ValueText nvarchar(250) NULL,
    ValueNumeric decimal(18,6) NULL, ValueSi decimal(18,6) NULL, IsAbnormal bit NOT NULL, IsCritical bit NOT NULL,
    TurnaroundDays smallint NULL, LoadRunId int NOT NULL, UpdatedRunId int NULL);
GO

-- On-prem run log and data-quality rows, mirrored for the report's quality page.
IF OBJECT_ID('etl.SourceLoadRun') IS NULL
CREATE TABLE etl.SourceLoadRun (
    RunId int NOT NULL CONSTRAINT PK_SourceLoadRun PRIMARY KEY, Package varchar(100) NOT NULL,
    StartedAt datetime2(0) NOT NULL, EndedAt datetime2(0) NULL, Status varchar(10) NOT NULL,
    FromVisitNo bigint NOT NULL, VisitsExtracted int NULL, ResultsExtracted int NULL, ResultsInserted int NULL,
    ResultsUpdated int NULL, ResultsSkipped int NULL, ResultsRejected int NULL, Warnings int NULL,
    Message nvarchar(2000) NULL);
IF OBJECT_ID('etl.SourceRejectRow') IS NULL
CREATE TABLE etl.SourceRejectRow (
    RejectRowId bigint NOT NULL CONSTRAINT PK_SourceRejectRow PRIMARY KEY, RunId int NOT NULL,
    Severity varchar(8) NOT NULL, SourceTable varchar(30) NOT NULL, SourceKey varchar(60) NOT NULL,
    Rule_ varchar(40) NOT NULL, RawValue nvarchar(250) NULL);

-- What this copy has seen: the highest on-prem LoadRun already copied.
IF OBJECT_ID('etl.CopyWatermark') IS NULL
CREATE TABLE etl.CopyWatermark (
    Name varchar(30) NOT NULL CONSTRAINT PK_CopyWatermark PRIMARY KEY,
    Value int NOT NULL, UpdatedAt datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM etl.CopyWatermark WHERE Name = 'SourceLoadRunId')
    INSERT etl.CopyWatermark (Name, Value) VALUES ('SourceLoadRunId', 0);

IF OBJECT_ID('etl.CopyRun') IS NULL
CREATE TABLE etl.CopyRun (
    CopyRunId int IDENTITY CONSTRAINT PK_CopyRun PRIMARY KEY,
    PipelineRunId nvarchar(64) NOT NULL, StartedAt datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    FromSourceRunId int NOT NULL, ToSourceRunId int NOT NULL,
    FactsStaged int NOT NULL, FactsInserted int NOT NULL, FactsUpdated int NOT NULL);
GO

/* ---------------- staging (truncated by each copy's pre-copy script) ---------------- */
IF OBJECT_ID('stg.DimDate') IS NULL SELECT * INTO stg.DimDate FROM dw.DimDate WHERE 1 = 0;
IF OBJECT_ID('stg.DimTest') IS NULL SELECT * INTO stg.DimTest FROM dw.DimTest WHERE 1 = 0;
IF OBJECT_ID('stg.DimPayer') IS NULL SELECT * INTO stg.DimPayer FROM dw.DimPayer WHERE 1 = 0;
IF OBJECT_ID('stg.DimPatient') IS NULL SELECT * INTO stg.DimPatient FROM dw.DimPatient WHERE 1 = 0;
IF OBJECT_ID('stg.FactLabResult') IS NULL SELECT * INTO stg.FactLabResult FROM dw.FactLabResult WHERE 1 = 0;
IF OBJECT_ID('stg.LoadRun') IS NULL SELECT * INTO stg.LoadRun FROM etl.SourceLoadRun WHERE 1 = 0;
IF OBJECT_ID('stg.RejectRow') IS NULL SELECT * INTO stg.RejectRow FROM etl.SourceRejectRow WHERE 1 = 0;
GO

/* ---------------- merge ---------------- */
-- Called by the pipeline after the copies. Dimensions and the logs arrive in
-- full, facts only for runs above the watermark. One transaction.
CREATE OR ALTER PROCEDURE etl.usp_MergeFromStage
    @PipelineRunId  nvarchar(64),
    @SourceMaxRunId int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @from int = (SELECT Value FROM etl.CopyWatermark WHERE Name = 'SourceLoadRunId');
    DECLARE @facts TABLE (Act nvarchar(10));

    BEGIN TRAN;

    MERGE dw.DimDate t USING stg.DimDate s ON t.DateKey = s.DateKey
    WHEN NOT MATCHED THEN INSERT VALUES (s.DateKey, s.[Date], s.[Year], s.[Quarter], s.[Month], s.MonthName,
        s.YearMonth, s.[Day], s.WeekdayNo, s.WeekdayName, s.IsWeekend);   -- dates never change

    MERGE dw.DimTest t USING stg.DimTest s ON t.TestKey = s.TestKey
    WHEN MATCHED THEN UPDATE SET LegacyAzId = s.LegacyAzId, LegacyCode = s.LegacyCode, LegacyName = s.LegacyName,
        LegacyUnit = s.LegacyUnit, LoincCode = s.LoincCode, LabFlowCode = s.LabFlowCode, TestName = s.TestName,
        SiUnit = s.SiUnit, Factor = s.Factor, MapStatus = s.MapStatus, IsPanel = s.IsPanel, Section = s.Section
    WHEN NOT MATCHED THEN INSERT VALUES (s.TestKey, s.LegacyAzId, s.LegacyCode, s.LegacyName, s.LegacyUnit, s.LoincCode,
        s.LabFlowCode, s.TestName, s.SiUnit, s.Factor, s.MapStatus, s.IsPanel, s.Section);

    MERGE dw.DimPayer t USING stg.DimPayer s ON t.PayerKey = s.PayerKey
    WHEN MATCHED THEN UPDATE SET LegacyPayerId = s.LegacyPayerId, PayerName = s.PayerName, PayerType = s.PayerType
    WHEN NOT MATCHED THEN INSERT VALUES (s.PayerKey, s.LegacyPayerId, s.PayerName, s.PayerType);

    MERGE dw.DimPatient t USING stg.DimPatient s ON t.PatientKey = s.PatientKey
    WHEN MATCHED AND t.Sex <> s.Sex THEN UPDATE SET Sex = s.Sex
    WHEN NOT MATCHED THEN INSERT VALUES (s.PatientKey, s.PatientHash, s.Sex);

    MERGE dw.FactLabResult t USING stg.FactLabResult s ON t.ResultKey = s.ResultKey
    WHEN MATCHED THEN UPDATE SET
        TestKey = s.TestKey, PanelTestKey = s.PanelTestKey, PatientKey = s.PatientKey, PayerKey = s.PayerKey,
        VisitDateKey = s.VisitDateKey, ResultDateKey = s.ResultDateKey, AgeYears = s.AgeYears, AgeBand = s.AgeBand,
        HasAnswer = s.HasAnswer, ValueText = s.ValueText, ValueNumeric = s.ValueNumeric, ValueSi = s.ValueSi,
        IsAbnormal = s.IsAbnormal, IsCritical = s.IsCritical, TurnaroundDays = s.TurnaroundDays, UpdatedRunId = s.UpdatedRunId
    WHEN NOT MATCHED THEN INSERT VALUES (s.ResultKey, s.LegacyVisitNo, s.LegacyAzId, s.LegacyChildAzId, s.RowNo, s.RowNo2,
        s.TestKey, s.PanelTestKey, s.PatientKey, s.PayerKey, s.VisitDateKey, s.ResultDateKey, s.AgeYears, s.AgeBand,
        s.HasAnswer, s.ValueText, s.ValueNumeric, s.ValueSi, s.IsAbnormal, s.IsCritical, s.TurnaroundDays,
        s.LoadRunId, s.UpdatedRunId)
    OUTPUT $action INTO @facts;

    -- The logs are small and arrive whole: replace them.
    DELETE etl.SourceRejectRow;
    INSERT etl.SourceRejectRow SELECT * FROM stg.RejectRow;
    DELETE etl.SourceLoadRun;
    INSERT etl.SourceLoadRun SELECT * FROM stg.LoadRun;

    UPDATE etl.CopyWatermark SET Value = @SourceMaxRunId, UpdatedAt = SYSUTCDATETIME()
    WHERE Name = 'SourceLoadRunId' AND Value < @SourceMaxRunId;

    INSERT etl.CopyRun (PipelineRunId, FromSourceRunId, ToSourceRunId, FactsStaged, FactsInserted, FactsUpdated)
    SELECT @PipelineRunId, @from, @SourceMaxRunId, (SELECT COUNT(*) FROM stg.FactLabResult),
           (SELECT COUNT(*) FROM @facts WHERE Act = 'INSERT'), (SELECT COUNT(*) FROM @facts WHERE Act = 'UPDATE');

    COMMIT;
END;
GO
