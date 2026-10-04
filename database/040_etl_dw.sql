-- ETL from the legacy Laboratory database into a reporting warehouse (dw).
--
-- The SSIS package etl/LabFlowETL/LabFlowETL.dtsx runs it:
--   1. etl.usp_StartRun      -> run id, hash salt, first visit number to extract
--   2. data flow             -> legacy rows into etl.stg_* (de-identified on the way)
--   3. etl.usp_TransformLoad -> clean, map to LOINC/SI, load the dw star schema
--   4. etl.usp_EndRun / etl.usp_FailRun
--
-- De-identification (PHIPA): the extract never reads names, phone numbers,
-- addresses, national codes, birth dates or images. The legacy patient code
-- is replaced by a salted SHA-256 hash, and every date in dw is shifted by
-- one secret offset so intervals (turnaround) stay true. The salt and the
-- offset live only in etl.Secret on this server.

SET XACT_ABORT ON;
GO

CREATE SCHEMA dw;       -- star schema for reporting (Power BI)
GO

/* =========================================================================
   etl: run log, secrets, staging
   ========================================================================= */

CREATE TABLE etl.Secret (
    Name      varchar(30)   NOT NULL CONSTRAINT PK_Secret PRIMARY KEY,
    Value     varchar(100)  NOT NULL,
    CreatedAt datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE etl.LoadRun (
    RunId            int           IDENTITY CONSTRAINT PK_LoadRun PRIMARY KEY,
    Package          varchar(100)  NOT NULL,
    StartedAt        datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    EndedAt          datetime2(0)  NULL,
    Status           varchar(10)   NOT NULL DEFAULT 'Running' CONSTRAINT CK_LoadRun_Status CHECK (Status IN ('Running','Succeeded','Failed')),
    FromVisitNo      bigint        NOT NULL,
    VisitsExtracted  int           NULL,
    ResultsExtracted int           NULL,
    ResultsInserted  int           NULL,
    ResultsUpdated   int           NULL,
    ResultsSkipped   int           NULL,   -- panel header rows, cancelled visits
    ResultsRejected  int           NULL,
    Warnings         int           NULL,
    Message          nvarchar(2000) NULL
);

-- Rows the transform could not load (Rejected) or loaded with a data-quality note (Warning).
CREATE TABLE etl.RejectRow (
    RejectRowId  bigint        IDENTITY CONSTRAINT PK_RejectRow PRIMARY KEY,
    RunId        int           NOT NULL CONSTRAINT FK_RejectRow_Run REFERENCES etl.LoadRun,
    Severity     varchar(8)    NOT NULL CONSTRAINT CK_RejectRow_Severity CHECK (Severity IN ('Rejected','Warning')),
    SourceTable  varchar(30)   NOT NULL,
    SourceKey    varchar(60)   NOT NULL,
    Rule_        varchar(40)   NOT NULL,
    RawValue     nvarchar(250) NULL
);
CREATE INDEX IX_RejectRow_Run ON etl.RejectRow (RunId, Severity, Rule_);

CREATE TABLE etl.Watermark (
    Name      varchar(30)   NOT NULL CONSTRAINT PK_Watermark PRIMARY KEY,
    Value     bigint        NOT NULL,
    UpdatedAt datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Staging: one run's extract, truncated at the start of every run. Text is
-- Unicode: the legacy database is code page 1256 (Arabic_CI_AS) and SSIS will
-- not load one code page into another, so the package converts on the way.
CREATE TABLE etl.stg_Test (
    LegacyAzId  int           NOT NULL CONSTRAINT PK_stg_Test PRIMARY KEY,
    LegacyCode  nvarchar(12)  NULL,
    LegacyName  nvarchar(50)  NULL,
    LegacyUnit  nvarchar(15)  NULL,
    Section     tinyint       NULL
);

CREATE TABLE etl.stg_Payer (
    LegacyPayerId smallint      NOT NULL CONSTRAINT PK_stg_Payer PRIMARY KEY,
    PayerName     nvarchar(200) NULL
);

CREATE TABLE etl.stg_Visit (
    LegacyVisitNo  bigint       NOT NULL CONSTRAINT PK_stg_Visit PRIMARY KEY,
    PatientHash    nchar(16)    NOT NULL,
    LegacySex      tinyint      NULL,
    Age            tinyint      NULL,
    AgeKind        tinyint      NULL,   -- 0 = years, otherwise an infant (months/days)
    VisitDateJ     nvarchar(10) NULL,   -- Jalali yyyy/mm/dd
    VisitTime      nvarchar(8)  NULL,
    AnswerDateJ    nvarchar(10) NULL,
    AnswerTime     nvarchar(8)  NULL,
    LegacyPayerId  smallint     NULL,
    Cancelled      bit          NULL,
    PregnancyWeek  tinyint      NULL
);

CREATE TABLE etl.stg_Result (
    LegacyVisitNo   bigint        NOT NULL,
    LegacyAzId      smallint      NOT NULL,
    LegacyChildAzId smallint      NOT NULL,
    RowNo           tinyint       NOT NULL,
    RowNo2          smallint      NOT NULL,
    Answer          nvarchar(250) NULL,
    Unit            nvarchar(15)  NULL,
    NormalRange     nvarchar(200) NULL,
    OutOfRange      tinyint       NULL,
    OutOfCritical   tinyint       NULL,
    NoAnswer        nvarchar(1)   NULL,
    CONSTRAINT PK_stg_Result PRIMARY KEY (LegacyVisitNo, LegacyAzId, LegacyChildAzId, RowNo, RowNo2)
);
GO

/* =========================================================================
   dw: star schema
   ========================================================================= */

CREATE TABLE dw.DimDate (
    DateKey     int          NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY,   -- yyyymmdd
    [Date]      date         NOT NULL CONSTRAINT UQ_DimDate UNIQUE,
    [Year]      smallint     NOT NULL,
    [Quarter]   tinyint      NOT NULL,
    [Month]     tinyint      NOT NULL,
    MonthName   varchar(10)  NOT NULL,
    YearMonth   char(7)      NOT NULL,   -- 2022-02
    [Day]       tinyint      NOT NULL,
    WeekdayNo   tinyint      NOT NULL,   -- 1 = Monday
    WeekdayName varchar(10)  NOT NULL,
    IsWeekend   bit          NOT NULL
);

CREATE TABLE dw.DimTest (
    TestKey        int           IDENTITY CONSTRAINT PK_DimTest PRIMARY KEY,
    LegacyAzId     int           NOT NULL CONSTRAINT UQ_DimTest_Legacy UNIQUE,
    LegacyCode     varchar(12)   NULL,
    LegacyName     nvarchar(50)  NULL,
    LegacyUnit     nvarchar(15)  NULL,
    LoincCode      varchar(10)   NULL,
    LabFlowCode    varchar(20)   NULL,
    TestName       nvarchar(150) NOT NULL,   -- LabFlow name when mapped, else the legacy name
    SiUnit         varchar(20)   NULL,
    Factor         decimal(18,8) NULL,
    MapStatus      varchar(12)   NOT NULL,
    IsPanel        bit           NOT NULL DEFAULT 0,
    Section        tinyint       NULL
);

CREATE TABLE dw.DimPayer (
    PayerKey      smallint      IDENTITY CONSTRAINT PK_DimPayer PRIMARY KEY,
    LegacyPayerId smallint      NOT NULL CONSTRAINT UQ_DimPayer_Legacy UNIQUE,
    PayerName     nvarchar(200) NOT NULL,
    PayerType     varchar(10)   NOT NULL   -- Insured / Self-pay
);

CREATE TABLE dw.DimPatient (
    PatientKey  int       IDENTITY CONSTRAINT PK_DimPatient PRIMARY KEY,
    PatientHash char(16)  NOT NULL CONSTRAINT UQ_DimPatient_Hash UNIQUE,
    Sex         char(1)   NOT NULL CONSTRAINT CK_DimPatient_Sex CHECK (Sex IN ('F','M','U'))
);

CREATE TABLE dw.FactLabResult (
    ResultKey        bigint        IDENTITY CONSTRAINT PK_FactLabResult PRIMARY KEY,
    LegacyVisitNo    bigint        NOT NULL,
    LegacyAzId       smallint      NOT NULL,
    LegacyChildAzId  smallint      NOT NULL,
    RowNo            tinyint       NOT NULL,
    RowNo2           smallint      NOT NULL,
    TestKey          int           NOT NULL CONSTRAINT FK_Fact_Test    REFERENCES dw.DimTest,
    PanelTestKey     int           NULL     CONSTRAINT FK_Fact_Panel   REFERENCES dw.DimTest,
    PatientKey       int           NOT NULL CONSTRAINT FK_Fact_Patient REFERENCES dw.DimPatient,
    PayerKey         smallint      NOT NULL CONSTRAINT FK_Fact_Payer   REFERENCES dw.DimPayer,
    VisitDateKey     int           NOT NULL CONSTRAINT FK_Fact_VisitDate  REFERENCES dw.DimDate,
    ResultDateKey    int           NULL     CONSTRAINT FK_Fact_ResultDate REFERENCES dw.DimDate,
    AgeYears         tinyint       NULL,
    AgeBand          varchar(8)    NOT NULL,
    HasAnswer        bit           NOT NULL,
    ValueText        nvarchar(250) NULL,
    ValueNumeric     decimal(18,6) NULL,   -- as entered, legacy unit
    ValueSi          decimal(18,6) NULL,   -- ValueNumeric * map factor, when the test is mapped
    IsAbnormal       bit           NOT NULL,
    IsCritical       bit           NOT NULL,
    TurnaroundDays   smallint      NULL,
    LoadRunId        int           NOT NULL CONSTRAINT FK_Fact_Run REFERENCES etl.LoadRun,
    UpdatedRunId     int           NULL,
    CONSTRAINT UQ_FactLabResult UNIQUE (LegacyVisitNo, LegacyAzId, LegacyChildAzId, RowNo, RowNo2)
);
CREATE INDEX IX_Fact_Test  ON dw.FactLabResult (TestKey, VisitDateKey);
CREATE INDEX IX_Fact_Visit ON dw.FactLabResult (VisitDateKey) INCLUDE (PatientKey, IsAbnormal, IsCritical, TurnaroundDays);
GO

/* =========================================================================
   Functions
   ========================================================================= */

-- Jalali (Solar Hijri) 'yyyy/mm/dd' to a Gregorian date; NULL when not a valid date.
CREATE FUNCTION etl.fn_JalaliToGregorian (@j varchar(10))
RETURNS date
WITH SCHEMABINDING
AS
BEGIN
    IF @j IS NULL OR @j NOT LIKE '[0-9][0-9][0-9][0-9]/[0-1][0-9]/[0-3][0-9]' RETURN NULL;
    DECLARE @jy int = CAST(LEFT(@j, 4) AS int),
            @jm int = CAST(SUBSTRING(@j, 6, 2) AS int),
            @jd int = CAST(RIGHT(@j, 2) AS int);
    IF @jm NOT BETWEEN 1 AND 12 OR @jd NOT BETWEEN 1 AND 31 OR (@jm > 6 AND @jd > 30) RETURN NULL;

    -- The arithmetic of jdf's jalali_to_gregorian: days since proleptic
    -- Gregorian 0000-01-01 (a leap year of 366 days, so 0001-01-01 is day 366).
    DECLARE @y int = @jy + 1595;
    DECLARE @days int = -355668 + 365 * @y + (@y / 33) * 8 + ((@y % 33) + 3) / 4 + @jd
                      + CASE WHEN @jm < 7 THEN (@jm - 1) * 31 ELSE (@jm - 7) * 30 + 186 END;
    RETURN DATEADD(day, @days - 366, CAST('0001-01-01' AS date));
END;
GO

-- 1 = Monday ... 7 = Sunday, whatever SET DATEFIRST is.
CREATE FUNCTION etl.fn_IsoWeekday (@d date)
RETURNS tinyint
WITH SCHEMABINDING
AS
BEGIN
    RETURN (DATEDIFF(day, '19000101', @d) % 7) + 1;   -- 1900-01-01 was a Monday
END;
GO

/* =========================================================================
   Procedures
   ========================================================================= */

-- Opens a run. Returns RunId, the hash salt and the first legacy visit number
-- to extract (the high-water mark minus a look-back, so answers entered after
-- the visit are picked up on the next runs).
-- EXECUTE AS OWNER: TRUNCATE needs ALTER on the staging tables, which the
-- account running the package (SQL Agent) should not hold itself.
CREATE PROCEDURE etl.usp_StartRun
    @Package        varchar(100),
    @LookbackVisits int = 200
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM etl.Secret WHERE Name = 'PatientHashSalt')
        INSERT etl.Secret (Name, Value) VALUES ('PatientHashSalt', CONVERT(varchar(64), CRYPT_GEN_RANDOM(32), 2));
    IF NOT EXISTS (SELECT 1 FROM etl.Secret WHERE Name = 'DateShiftDays')
        -- a nonzero shift between -180 and +180 days
        INSERT etl.Secret (Name, Value)
        VALUES ('DateShiftDays', CAST((ABS(CHECKSUM(CRYPT_GEN_RANDOM(4))) % 180 + 1)
                                      * CASE WHEN ABS(CHECKSUM(NEWID())) % 2 = 0 THEN 1 ELSE -1 END AS varchar(10)));

    IF EXISTS (SELECT 1 FROM etl.LoadRun WHERE Status = 'Running' AND StartedAt > DATEADD(hour, -2, SYSUTCDATETIME()))
        THROW 50100, 'Another ETL run started in the last two hours is still marked Running.', 1;
    -- A run left Running for over two hours died without reaching usp_FailRun.
    UPDATE etl.LoadRun SET Status = 'Failed', EndedAt = SYSUTCDATETIME(), Message = N'Abandoned (no end recorded)'
    WHERE Status = 'Running';

    DECLARE @mark bigint = ISNULL((SELECT Value FROM etl.Watermark WHERE Name = 'MRJ.FishNo'), 0);
    DECLARE @from bigint = CASE WHEN @mark - @LookbackVisits < 0 THEN 0 ELSE @mark - @LookbackVisits END;

    INSERT etl.LoadRun (Package, FromVisitNo) VALUES (@Package, @from);
    DECLARE @run int = SCOPE_IDENTITY();

    TRUNCATE TABLE etl.stg_Result;
    TRUNCATE TABLE etl.stg_Visit;
    TRUNCATE TABLE etl.stg_Test;
    TRUNCATE TABLE etl.stg_Payer;

    -- int, not bigint: SSIS cannot bind an OLE DB bigint result to a variable.
    SELECT RunId = @run,
           HashSalt = (SELECT Value FROM etl.Secret WHERE Name = 'PatientHashSalt'),
           FromVisitNo = CAST(@from AS int);
END;
GO

CREATE PROCEDURE etl.usp_FailRun
    @RunId   int,
    @Message nvarchar(2000)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE etl.LoadRun SET Status = 'Failed', EndedAt = SYSUTCDATETIME(), Message = LEFT(@Message, 2000)
    WHERE RunId = @RunId AND Status = 'Running';
END;
GO

-- Keeps dw.DimDate covering @From..@To.
CREATE PROCEDURE etl.usp_EnsureDates
    @From date,
    @To   date
AS
BEGIN
    SET NOCOUNT ON;
    IF @From IS NULL OR @To IS NULL RETURN;
    WITH n AS (
        SELECT TOP (DATEDIFF(day, @From, @To) + 1)
               d = DATEADD(day, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, @From)
        FROM sys.all_objects a CROSS JOIN sys.all_objects b
    )
    INSERT dw.DimDate (DateKey, [Date], [Year], [Quarter], [Month], MonthName, YearMonth, [Day], WeekdayNo, WeekdayName, IsWeekend)
    SELECT CONVERT(int, CONVERT(char(8), d, 112)), d, YEAR(d), DATEPART(quarter, d), MONTH(d),
           LEFT(DATENAME(month, d), 10), CONVERT(char(7), d, 126), DAY(d),
           etl.fn_IsoWeekday(d),
           CHOOSE(etl.fn_IsoWeekday(d), 'Monday','Tuesday','Wednesday','Thursday','Friday','Saturday','Sunday'),
           CASE WHEN etl.fn_IsoWeekday(d) >= 6 THEN 1 ELSE 0 END
    FROM n
    WHERE NOT EXISTS (SELECT 1 FROM dw.DimDate x WHERE x.[Date] = n.d);
END;
GO

-- Cleans the staged extract and loads the warehouse. One transaction: a
-- failure leaves dw exactly as it was.
CREATE PROCEDURE etl.usp_TransformLoad
    @RunId int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @shift int = (SELECT CAST(Value AS int) FROM etl.Secret WHERE Name = 'DateShiftDays');
    IF @shift IS NULL THROW 50101, 'etl.Secret has no DateShiftDays; run etl.usp_StartRun first.', 1;

    BEGIN TRAN;

    /* ---- tests: every legacy test gets a mapping row (Unmapped until reviewed) ---- */
    INSERT etl.LegacyTestMap (LegacyAzId, LegacyCode, LegacyName, LegacyUnit, MapStatus)
    SELECT s.LegacyAzId, s.LegacyCode, LEFT(s.LegacyName, 50), LEFT(s.LegacyUnit, 15), 'Unmapped'
    FROM etl.stg_Test s
    WHERE NOT EXISTS (SELECT 1 FROM etl.LegacyTestMap m WHERE m.LegacyAzId = s.LegacyAzId);

    -- Legacy rows: a single test has ChildAz_ID = Az_ID; a panel has one header
    -- row (ChildAz_ID = Az_ID, answer '.') and one row per member (ChildAz_ID
    -- = the member test). So a test is a panel when other tests hang under it.
    SELECT DISTINCT LegacyAzId INTO #panel FROM etl.stg_Result WHERE LegacyChildAzId <> LegacyAzId;

    MERGE dw.DimTest AS t
    USING (
        SELECT s.LegacyAzId, s.LegacyCode, s.LegacyName, s.LegacyUnit, s.Section,
               rt.LoincCode, rt.Code AS LabFlowCode,
               TestName = COALESCE(rt.NameEn, s.LegacyName, CONCAT(N'Legacy test ', s.LegacyAzId)),
               SiUnit = CASE WHEN m.MapStatus IN ('Suggested','Reviewed') THEN rt.UnitCode END,
               Factor = CASE WHEN m.MapStatus IN ('Suggested','Reviewed') THEN m.Factor END,
               m.MapStatus,
               IsPanel = CASE WHEN p.LegacyAzId IS NOT NULL OR rt.ResultType = 'Panel' THEN 1 ELSE 0 END
        FROM etl.stg_Test s
        JOIN etl.LegacyTestMap m ON m.LegacyAzId = s.LegacyAzId
        LEFT JOIN ref.Test rt ON rt.TestId = m.TestId
        LEFT JOIN #panel p ON p.LegacyAzId = s.LegacyAzId
    ) AS s ON t.LegacyAzId = s.LegacyAzId
    WHEN MATCHED THEN UPDATE SET
        LegacyCode = s.LegacyCode, LegacyName = s.LegacyName, LegacyUnit = s.LegacyUnit, Section = s.Section,
        LoincCode = s.LoincCode, LabFlowCode = s.LabFlowCode, TestName = s.TestName, SiUnit = s.SiUnit,
        Factor = s.Factor, MapStatus = s.MapStatus, IsPanel = s.IsPanel
    WHEN NOT MATCHED THEN INSERT
        (LegacyAzId, LegacyCode, LegacyName, LegacyUnit, Section, LoincCode, LabFlowCode, TestName, SiUnit, Factor, MapStatus, IsPanel)
        VALUES (s.LegacyAzId, s.LegacyCode, s.LegacyName, s.LegacyUnit, s.Section, s.LoincCode, s.LabFlowCode, s.TestName, s.SiUnit, s.Factor, s.MapStatus, s.IsPanel);

    /* ---- payers (0 = no insurer in the legacy system) ---- */
    MERGE dw.DimPayer AS t
    USING (
        SELECT LegacyPayerId,
               PayerName = CASE WHEN LegacyPayerId = 0 THEN N'Self-pay' ELSE COALESCE(NULLIF(LTRIM(RTRIM(PayerName)), N''), CONCAT(N'Payer ', LegacyPayerId)) END,
               PayerType = CASE WHEN LegacyPayerId = 0 THEN 'Self-pay' ELSE 'Insured' END
        FROM etl.stg_Payer
    ) AS s ON t.LegacyPayerId = s.LegacyPayerId
    WHEN MATCHED THEN UPDATE SET PayerName = s.PayerName, PayerType = s.PayerType
    WHEN NOT MATCHED THEN INSERT (LegacyPayerId, PayerName, PayerType) VALUES (s.LegacyPayerId, s.PayerName, s.PayerType);

    IF NOT EXISTS (SELECT 1 FROM dw.DimPayer WHERE LegacyPayerId = -1)
        INSERT dw.DimPayer (LegacyPayerId, PayerName, PayerType) VALUES (-1, N'Unknown payer', 'Unknown');

    /* ---- patients: legacy sex 0 = female, 1 = male (verified against
       sex-specific tests: beta-HCG / semen analysis); anything else unknown ---- */
    MERGE dw.DimPatient AS t
    USING (
        SELECT PatientHash, Sex = MAX(CASE LegacySex WHEN 0 THEN 'F' WHEN 1 THEN 'M' ELSE 'U' END)
        FROM etl.stg_Visit GROUP BY PatientHash
    ) AS s ON t.PatientHash = s.PatientHash
    WHEN MATCHED AND t.Sex <> s.Sex THEN UPDATE SET Sex = s.Sex
    WHEN NOT MATCHED THEN INSERT (PatientHash, Sex) VALUES (s.PatientHash, s.Sex);

    /* ---- visits, with dates converted and shifted ---- */
    SELECT v.LegacyVisitNo, v.PatientHash, v.LegacyPayerId, v.Cancelled,
           VisitDate  = DATEADD(day, @shift, etl.fn_JalaliToGregorian(v.VisitDateJ)),
           AnswerDate = DATEADD(day, @shift, etl.fn_JalaliToGregorian(NULLIF(v.AnswerDateJ, ''))),
           AgeYears   = CASE WHEN v.AgeKind = 0 THEN v.Age ELSE 0 END,
           v.VisitDateJ, v.AnswerDateJ, v.Age, v.AgeKind
    INTO #visit
    FROM etl.stg_Visit v;

    DECLARE @minD date, @maxD date;
    SELECT @minD = MIN(d), @maxD = MAX(d)
    FROM (SELECT VisitDate FROM #visit UNION ALL SELECT AnswerDate FROM #visit) x(d);
    EXEC etl.usp_EnsureDates @minD, @maxD;

    -- Visit-level data-quality notes (the rows still load).
    INSERT etl.RejectRow (RunId, Severity, SourceTable, SourceKey, Rule_, RawValue)
    SELECT @RunId, 'Warning', 'MRJ', CAST(LegacyVisitNo AS varchar(20)), 'Implausible age', CAST(Age AS nvarchar(10))
    FROM #visit WHERE AgeKind = 0 AND Age > 110
    UNION ALL
    SELECT @RunId, 'Warning', 'MRJ', CAST(LegacyVisitNo AS varchar(20)), 'Answer date before visit',
           CONCAT(VisitDateJ, N' -> ', AnswerDateJ)
    FROM #visit WHERE AnswerDate < VisitDate
    UNION ALL
    SELECT @RunId, 'Warning', 'MRJ', CAST(LegacyVisitNo AS varchar(20)), 'No answer date', VisitDateJ
    FROM #visit WHERE AnswerDate IS NULL AND VisitDate IS NOT NULL;

    /* ---- results ---- */
    SELECT r.LegacyVisitNo, r.LegacyAzId, r.LegacyChildAzId, r.RowNo, r.RowNo2,
           TestAzId  = r.LegacyChildAzId,
           PanelAzId = CASE WHEN r.LegacyChildAzId <> r.LegacyAzId THEN r.LegacyAzId END,
           -- '', '-', '_' and '.' all mean "no answer" in the legacy screens
           AnswerClean = NULLIF(NULLIF(NULLIF(NULLIF(LTRIM(RTRIM(r.Answer)), N''), N'-'), N'_'), N'.'),
           r.Answer, r.OutOfRange, r.OutOfCritical,
           v.VisitDate, v.AnswerDate, v.AgeYears, v.Cancelled, v.PatientHash, v.LegacyPayerId,
           VisitFound = CASE WHEN v.LegacyVisitNo IS NULL THEN 0 ELSE 1 END
    INTO #res
    FROM etl.stg_Result r
    LEFT JOIN #visit v ON v.LegacyVisitNo = r.LegacyVisitNo;

    ALTER TABLE #res ADD ValueNumeric decimal(18,6) NULL, Outcome varchar(10) NULL, RuleText varchar(40) NULL;

    -- Only plain decimals count as numbers ('' would cast to 0, '1e5' to 100000).
    UPDATE #res SET ValueNumeric = TRY_CONVERT(decimal(18,6), AnswerClean)
    WHERE AnswerClean NOT LIKE N'%[^0-9.]%' AND AnswerClean LIKE N'%[0-9]%' AND AnswerClean NOT LIKE N'%.%.%';

    UPDATE r SET Outcome = 'Skipped', RuleText = 'Panel header row'
    FROM #res r WHERE r.LegacyChildAzId = r.LegacyAzId AND EXISTS (SELECT 1 FROM #panel p WHERE p.LegacyAzId = r.LegacyAzId);
    UPDATE #res SET Outcome = 'Skipped', RuleText = 'Cancelled visit' WHERE Outcome IS NULL AND Cancelled = 1;
    UPDATE #res SET Outcome = 'Rejected', RuleText = 'Visit not in extract' WHERE Outcome IS NULL AND VisitFound = 0;
    UPDATE #res SET Outcome = 'Rejected', RuleText = 'Invalid visit date' WHERE Outcome IS NULL AND VisitDate IS NULL;
    UPDATE r SET Outcome = 'Rejected', RuleText = 'Unknown test'
    FROM #res r WHERE r.Outcome IS NULL AND NOT EXISTS (SELECT 1 FROM dw.DimTest t WHERE t.LegacyAzId = r.TestAzId);
    UPDATE #res SET Outcome = 'Load' WHERE Outcome IS NULL;

    INSERT etl.RejectRow (RunId, Severity, SourceTable, SourceKey, Rule_, RawValue)
    SELECT @RunId, 'Rejected', 'MRJAZ', CONCAT(LegacyVisitNo, '/', LegacyAzId, '/', LegacyChildAzId, '/', RowNo, '/', RowNo2), RuleText, LEFT(Answer, 250)
    FROM #res WHERE Outcome = 'Rejected';

    -- A mapped numeric test whose answer is not a number still loads, as text.
    INSERT etl.RejectRow (RunId, Severity, SourceTable, SourceKey, Rule_, RawValue)
    SELECT @RunId, 'Warning', 'MRJAZ', CONCAT(r.LegacyVisitNo, '/', r.LegacyAzId, '/', r.LegacyChildAzId, '/', r.RowNo, '/', r.RowNo2),
           'Non-numeric answer, numeric test', LEFT(r.AnswerClean, 250)
    FROM #res r JOIN dw.DimTest t ON t.LegacyAzId = r.TestAzId
    WHERE r.Outcome = 'Load' AND t.Factor IS NOT NULL AND r.AnswerClean IS NOT NULL AND r.ValueNumeric IS NULL;

    DECLARE @merge TABLE (Act nvarchar(10));
    MERGE dw.FactLabResult AS f
    USING (
        SELECT r.LegacyVisitNo, r.LegacyAzId, r.LegacyChildAzId, r.RowNo, r.RowNo2,
               TestKey = t.TestKey, PanelTestKey = pt.TestKey,
               PatientKey = dp.PatientKey,
               PayerKey = COALESCE(py.PayerKey, unk.PayerKey),
               VisitDateKey = CONVERT(int, CONVERT(char(8), r.VisitDate, 112)),
               ResultDateKey = CONVERT(int, CONVERT(char(8), r.AnswerDate, 112)),
               AgeYears = r.AgeYears,
               AgeBand = CASE WHEN r.AgeYears IS NULL THEN 'Unknown' WHEN r.AgeYears < 18 THEN '0-17'
                              WHEN r.AgeYears < 40 THEN '18-39' WHEN r.AgeYears < 65 THEN '40-64' ELSE '65+' END,
               HasAnswer = CASE WHEN r.AnswerClean IS NULL THEN 0 ELSE 1 END,
               ValueText = r.AnswerClean,
               r.ValueNumeric,
               ValueSi = CAST(r.ValueNumeric * t.Factor AS decimal(18,6)),
               IsAbnormal = CASE WHEN r.OutOfRange > 0 OR r.OutOfCritical > 0 THEN 1 ELSE 0 END,
               IsCritical = CASE WHEN r.OutOfCritical > 0 THEN 1 ELSE 0 END,
               TurnaroundDays = CASE WHEN r.AnswerDate >= r.VisitDate THEN DATEDIFF(day, r.VisitDate, r.AnswerDate) END
        FROM #res r
        JOIN dw.DimTest t ON t.LegacyAzId = r.TestAzId
        LEFT JOIN dw.DimTest pt ON pt.LegacyAzId = r.PanelAzId
        JOIN dw.DimPatient dp ON dp.PatientHash = r.PatientHash
        LEFT JOIN dw.DimPayer py ON py.LegacyPayerId = r.LegacyPayerId
        CROSS JOIN (SELECT PayerKey FROM dw.DimPayer WHERE LegacyPayerId = -1) unk
        WHERE r.Outcome = 'Load'
    ) AS s
    ON  f.LegacyVisitNo = s.LegacyVisitNo AND f.LegacyAzId = s.LegacyAzId AND f.LegacyChildAzId = s.LegacyChildAzId
    AND f.RowNo = s.RowNo AND f.RowNo2 = s.RowNo2
    WHEN MATCHED AND (
           f.TestKey <> s.TestKey OR ISNULL(f.ValueText, N'') <> ISNULL(s.ValueText, N'')
        OR ISNULL(f.ValueSi, -1) <> ISNULL(s.ValueSi, -1) OR f.IsAbnormal <> s.IsAbnormal OR f.IsCritical <> s.IsCritical
        OR ISNULL(f.ResultDateKey, 0) <> ISNULL(s.ResultDateKey, 0) OR f.PayerKey <> s.PayerKey)
    THEN UPDATE SET
        TestKey = s.TestKey, PanelTestKey = s.PanelTestKey, PatientKey = s.PatientKey, PayerKey = s.PayerKey,
        VisitDateKey = s.VisitDateKey, ResultDateKey = s.ResultDateKey, AgeYears = s.AgeYears, AgeBand = s.AgeBand,
        HasAnswer = s.HasAnswer, ValueText = s.ValueText, ValueNumeric = s.ValueNumeric, ValueSi = s.ValueSi,
        IsAbnormal = s.IsAbnormal, IsCritical = s.IsCritical, TurnaroundDays = s.TurnaroundDays, UpdatedRunId = @RunId
    WHEN NOT MATCHED THEN INSERT
        (LegacyVisitNo, LegacyAzId, LegacyChildAzId, RowNo, RowNo2, TestKey, PanelTestKey, PatientKey, PayerKey,
         VisitDateKey, ResultDateKey, AgeYears, AgeBand, HasAnswer, ValueText, ValueNumeric, ValueSi,
         IsAbnormal, IsCritical, TurnaroundDays, LoadRunId)
        VALUES (s.LegacyVisitNo, s.LegacyAzId, s.LegacyChildAzId, s.RowNo, s.RowNo2, s.TestKey, s.PanelTestKey, s.PatientKey, s.PayerKey,
         s.VisitDateKey, s.ResultDateKey, s.AgeYears, s.AgeBand, s.HasAnswer, s.ValueText, s.ValueNumeric, s.ValueSi,
         s.IsAbnormal, s.IsCritical, s.TurnaroundDays, @RunId)
    OUTPUT $action INTO @merge;

    /* ---- close the run ---- */
    DECLARE @maxVisit bigint = (SELECT MAX(LegacyVisitNo) FROM etl.stg_Visit);
    IF @maxVisit IS NOT NULL
        MERGE etl.Watermark AS w
        USING (SELECT 'MRJ.FishNo' AS Name) s ON w.Name = s.Name
        WHEN MATCHED AND w.Value < @maxVisit THEN UPDATE SET Value = @maxVisit, UpdatedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN INSERT (Name, Value) VALUES ('MRJ.FishNo', @maxVisit);

    UPDATE etl.LoadRun SET
        Status = 'Succeeded', EndedAt = SYSUTCDATETIME(),
        VisitsExtracted  = (SELECT COUNT(*) FROM etl.stg_Visit),
        ResultsExtracted = (SELECT COUNT(*) FROM etl.stg_Result),
        ResultsInserted  = (SELECT COUNT(*) FROM @merge WHERE Act = 'INSERT'),
        ResultsUpdated   = (SELECT COUNT(*) FROM @merge WHERE Act = 'UPDATE'),
        ResultsSkipped   = (SELECT COUNT(*) FROM #res WHERE Outcome = 'Skipped'),
        ResultsRejected  = (SELECT COUNT(*) FROM #res WHERE Outcome = 'Rejected'),
        Warnings         = (SELECT COUNT(*) FROM etl.RejectRow WHERE RunId = @RunId AND Severity = 'Warning')
    WHERE RunId = @RunId;

    COMMIT;

    SELECT * FROM etl.LoadRun WHERE RunId = @RunId;
END;
GO

/* =========================================================================
   Views for reporting
   ========================================================================= */

-- One row per run, newest first: what the nightly job did.
CREATE VIEW etl.vw_RunHistory
AS
    SELECT r.RunId, r.Package, r.StartedAt, r.EndedAt,
           DurationSec = DATEDIFF(second, r.StartedAt, r.EndedAt),
           r.Status, r.FromVisitNo, r.VisitsExtracted, r.ResultsExtracted, r.ResultsInserted,
           r.ResultsUpdated, r.ResultsSkipped, r.ResultsRejected, r.Warnings, r.Message
    FROM etl.LoadRun r;
GO

-- Mapping coverage: how many loaded results have a LOINC code and an SI value.
CREATE VIEW dw.vw_MappingCoverage
AS
    SELECT t.MapStatus,
           Tests   = COUNT(DISTINCT t.TestKey),
           Results = COUNT(f.ResultKey),
           WithAnswer = SUM(CASE WHEN f.HasAnswer = 1 THEN 1 ELSE 0 END)
    FROM dw.DimTest t
    LEFT JOIN dw.FactLabResult f ON f.TestKey = t.TestKey
    WHERE t.IsPanel = 0
    GROUP BY t.MapStatus;
GO
