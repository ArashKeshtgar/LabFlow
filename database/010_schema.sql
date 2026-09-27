-- LabFlow schema. Run against a fresh LabFlow database.
--
-- Conventions
--   * All timestamps are UTC (datetime2). The app converts to America/Toronto for display.
--   * Text is nvarchar (English + French).
--   * Money is decimal, never float.
--   * No passwords are stored: authentication is external (ASP.NET Core Identity / Entra ID),
--     sec.AppUser only maps the external subject to a role.
--   * FHIR resource each table corresponds to is noted in its header comment.

SET XACT_ABORT ON;
GO

CREATE SCHEMA ref;      -- test catalog and lookups
GO
CREATE SCHEMA core;     -- patients, practitioners, locations
GO
CREATE SCHEMA sec;      -- application users and roles
GO
CREATE SCHEMA sched;    -- booking
GO
CREATE SCHEMA lab;      -- requisition -> specimen -> result -> report
GO
CREATE SCHEMA notify;   -- outbound email/SMS outbox (drained by an Azure Function)
GO
CREATE SCHEMA billing;  -- OHIP claims and patient invoices
GO
CREATE SCHEMA audit;    -- PHIPA access log
GO
CREATE SCHEMA etl;      -- mapping from the legacy Laboratory database
GO
CREATE SCHEMA history;  -- system-versioned (temporal) history tables
GO

/* =========================================================================
   ref: catalog
   ========================================================================= */

CREATE TABLE ref.Department (
    DepartmentId smallint      IDENTITY CONSTRAINT PK_Department PRIMARY KEY,
    Code         varchar(10)   NOT NULL CONSTRAINT UQ_Department_Code UNIQUE,
    NameEn       nvarchar(100) NOT NULL,
    NameFr       nvarchar(100) NULL,
    SortOrder    smallint      NOT NULL DEFAULT 0,
    IsActive     bit           NOT NULL DEFAULT 1
);

-- UCUM units
CREATE TABLE ref.Unit (
    UnitCode varchar(20)  NOT NULL CONSTRAINT PK_Unit PRIMARY KEY,  -- UCUM, e.g. mmol/L, 10*9/L
    Display  nvarchar(20) NOT NULL                                  -- as printed, e.g. ×10⁹/L
);

-- HL7 v2 table 0487 codes
CREATE TABLE ref.SpecimenType (
    SpecimenTypeCode varchar(10)   NOT NULL CONSTRAINT PK_SpecimenType PRIMARY KEY,
    NameEn           nvarchar(60)  NOT NULL,
    NameFr           nvarchar(60)  NULL,
    Container        nvarchar(100) NULL        -- e.g. "Gold top (SST)"
);

-- FHIR: ActivityDefinition + ObservationDefinition
CREATE TABLE ref.Test (
    TestId               int           IDENTITY CONSTRAINT PK_Test PRIMARY KEY,
    Code                 varchar(20)   NOT NULL CONSTRAINT UQ_Test_Code UNIQUE,
    LoincCode            varchar(10)   NULL,   -- pCLOCD / LOINC; NULL for panels without a code
    NameEn               nvarchar(150) NOT NULL,
    NameFr               nvarchar(150) NULL,
    ShortName            nvarchar(30)  NULL,
    DepartmentId         smallint      NOT NULL CONSTRAINT FK_Test_Department REFERENCES ref.Department,
    SpecimenTypeCode     varchar(10)   NULL     CONSTRAINT FK_Test_SpecimenType REFERENCES ref.SpecimenType,
    ResultType           varchar(10)   NOT NULL CONSTRAINT CK_Test_ResultType CHECK (ResultType IN ('Numeric','Text','Coded','Panel')),
    UnitCode             varchar(20)   NULL     CONSTRAINT FK_Test_Unit REFERENCES ref.Unit,
    DecimalPlaces        tinyint       NULL,
    ApplicableSex        char(1)       NULL     CONSTRAINT CK_Test_ApplicableSex CHECK (ApplicableSex IN ('M','F')),
    IsOrderable          bit           NOT NULL DEFAULT 1,
    OhipFeeCode          varchar(10)   NULL,   -- Schedule of Benefits for Laboratory Services
    IsOhipInsured        bit           NOT NULL DEFAULT 1,
    UninsuredPrice       decimal(10,2) NULL,
    FastingRequired      bit           NOT NULL DEFAULT 0,
    PreparationEn        nvarchar(500) NULL,
    PreparationFr        nvarchar(500) NULL,
    TurnaroundHours      smallint      NULL,
    IsActive             bit           NOT NULL DEFAULT 1,
    CreatedAt            datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt            datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVer               rowversion,
    CONSTRAINT CK_Test_Loinc CHECK (LoincCode IS NULL OR (LoincCode LIKE '%[0-9]-[0-9]' AND LoincCode NOT LIKE '%[^0-9-]%')),
    CONSTRAINT CK_Test_NumericUnit CHECK (ResultType <> 'Numeric' OR UnitCode IS NOT NULL),
    CONSTRAINT CK_Test_UninsuredPrice CHECK (IsOhipInsured = 1 OR UninsuredPrice IS NOT NULL)
);
CREATE INDEX IX_Test_Loinc ON ref.Test (LoincCode) WHERE LoincCode IS NOT NULL;

CREATE TABLE ref.PanelMember (
    PanelTestId  int      NOT NULL CONSTRAINT FK_PanelMember_Panel  REFERENCES ref.Test,
    MemberTestId int      NOT NULL CONSTRAINT FK_PanelMember_Member REFERENCES ref.Test,
    SortOrder    smallint NOT NULL DEFAULT 0,
    CONSTRAINT PK_PanelMember PRIMARY KEY (PanelTestId, MemberTestId),
    CONSTRAINT CK_PanelMember_NotSelf CHECK (PanelTestId <> MemberTestId)
);

-- Sex 'U' = applies to any sex. Age is in days so neonatal ranges fit the same model.
CREATE TABLE ref.ReferenceRange (
    ReferenceRangeId  int           IDENTITY CONSTRAINT PK_ReferenceRange PRIMARY KEY,
    TestId            int           NOT NULL CONSTRAINT FK_ReferenceRange_Test REFERENCES ref.Test,
    Sex               char(1)       NOT NULL DEFAULT 'U' CONSTRAINT CK_ReferenceRange_Sex CHECK (Sex IN ('M','F','U')),
    AgeFromDays       int           NOT NULL DEFAULT 0,
    AgeToDays         int           NOT NULL DEFAULT 54750,   -- 150 years
    PregnancyWeekFrom tinyint       NULL,
    PregnancyWeekTo   tinyint       NULL,
    Low               decimal(18,6) NULL,
    High              decimal(18,6) NULL,
    CriticalLow       decimal(18,6) NULL,
    CriticalHigh      decimal(18,6) NULL,
    DisplayText       nvarchar(100) NULL,   -- e.g. "< 5.20", "Negative"
    Comment           nvarchar(300) NULL,
    EffectiveFrom     date          NOT NULL DEFAULT CAST(SYSUTCDATETIME() AS date),
    EffectiveTo       date          NULL,
    CONSTRAINT CK_ReferenceRange_Age CHECK (AgeFromDays <= AgeToDays),
    CONSTRAINT CK_ReferenceRange_LowHigh CHECK (Low IS NULL OR High IS NULL OR Low <= High),
    CONSTRAINT CK_ReferenceRange_Pregnancy CHECK ((PregnancyWeekFrom IS NULL AND PregnancyWeekTo IS NULL)
                                                  OR PregnancyWeekFrom <= PregnancyWeekTo)
);
CREATE INDEX IX_ReferenceRange_Test ON ref.ReferenceRange (TestId, Sex, AgeFromDays);
GO

/* =========================================================================
   core
   ========================================================================= */

-- FHIR: Location / Organization
CREATE TABLE core.Location (
    LocationId         smallint      IDENTITY CONSTRAINT PK_Location PRIMARY KEY,
    Name               nvarchar(100) NOT NULL,
    AddressLine1       nvarchar(100) NOT NULL,
    AddressLine2       nvarchar(100) NULL,
    City               nvarchar(60)  NOT NULL,
    Province           char(2)       NOT NULL DEFAULT 'ON',
    PostalCode         char(7)       NOT NULL,
    Phone              varchar(20)   NULL,
    Email              nvarchar(254) NULL,
    IsCollectionCentre bit           NOT NULL DEFAULT 1,
    IsLab              bit           NOT NULL DEFAULT 0,
    IsActive           bit           NOT NULL DEFAULT 1,
    CONSTRAINT CK_Location_PostalCode CHECK (PostalCode LIKE '[A-Z][0-9][A-Z] [0-9][A-Z][0-9]')
);

-- FHIR: Patient. Temporal so every demographic change is kept (PHIPA correction requests).
CREATE TABLE core.Patient (
    PatientId                 int           IDENTITY CONSTRAINT PK_Patient PRIMARY KEY,
    Mrn                       varchar(12)   NOT NULL CONSTRAINT UQ_Patient_Mrn UNIQUE,
    HealthCardNumber          char(10)      NULL,   -- Ontario HCN; Luhn check digit validated in the app
    HealthCardVersion         varchar(2)    NULL,
    HealthCardExpiry          date          NULL,
    HealthCardProvince        char(2)       NULL,
    FirstName                 nvarchar(60)  NOT NULL,
    MiddleName                nvarchar(60)  NULL,
    LastName                  nvarchar(60)  NOT NULL,
    DateOfBirth               date          NOT NULL,
    Sex                       char(1)       NOT NULL CONSTRAINT CK_Patient_Sex CHECK (Sex IN ('M','F','X','U')),
    GenderIdentity            nvarchar(50)  NULL,
    Email                     nvarchar(254) NULL,
    MobilePhone               varchar(20)   NULL,
    PreferredLanguage         char(2)       NOT NULL DEFAULT 'en' CONSTRAINT CK_Patient_Language CHECK (PreferredLanguage IN ('en','fr')),
    AddressLine1              nvarchar(100) NULL,
    AddressLine2              nvarchar(100) NULL,
    City                      nvarchar(60)  NULL,
    Province                  char(2)       NULL,
    PostalCode                char(7)       NULL,
    ConsentEmailNotification  bit           NOT NULL DEFAULT 0,
    ConsentAiSummary          bit           NOT NULL DEFAULT 0,
    ConsentRecordedAt         datetime2(0)  NULL,
    IsActive                  bit           NOT NULL DEFAULT 1,
    CreatedAt                 datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVer                    rowversion,
    ValidFrom                 datetime2(0)  GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo                   datetime2(0)  GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),
    CONSTRAINT CK_Patient_HealthCard CHECK (HealthCardNumber IS NULL OR HealthCardNumber NOT LIKE '%[^0-9]%'),
    CONSTRAINT CK_Patient_PostalCode CHECK (PostalCode IS NULL OR PostalCode LIKE '[A-Z][0-9][A-Z] [0-9][A-Z][0-9]'),
    CONSTRAINT CK_Patient_Dob CHECK (DateOfBirth >= '1900-01-01')
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = history.Patient));

CREATE UNIQUE INDEX UX_Patient_HealthCard ON core.Patient (HealthCardNumber) WHERE HealthCardNumber IS NOT NULL;
CREATE INDEX IX_Patient_Name ON core.Patient (LastName, FirstName, DateOfBirth);

-- FHIR: Practitioner (ordering / copy-to physicians and nurse practitioners)
CREATE TABLE core.Practitioner (
    PractitionerId    int           IDENTITY CONSTRAINT PK_Practitioner PRIMARY KEY,
    LicenceNumber     varchar(10)   NOT NULL,   -- CPSO (physicians) or CNO (NPs)
    LicenceBody       varchar(6)    NOT NULL DEFAULT 'CPSO' CONSTRAINT CK_Practitioner_Body CHECK (LicenceBody IN ('CPSO','CNO')),
    OhipBillingNumber char(6)       NULL,
    FirstName         nvarchar(60)  NOT NULL,
    LastName          nvarchar(60)  NOT NULL,
    Specialty         nvarchar(80)  NULL,
    ClinicName        nvarchar(150) NULL,
    AddressLine1      nvarchar(100) NULL,
    City              nvarchar(60)  NULL,
    Province          char(2)       NULL,
    PostalCode        char(7)       NULL,
    Phone             varchar(20)   NULL,
    Fax               varchar(20)   NULL,
    Email             nvarchar(254) NULL,
    IsActive          bit           NOT NULL DEFAULT 1,
    CreatedAt         datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Practitioner_Licence UNIQUE (LicenceBody, LicenceNumber)
);
GO

/* =========================================================================
   sec
   ========================================================================= */

CREATE TABLE sec.AppUser (
    UserId         int           IDENTITY CONSTRAINT PK_AppUser PRIMARY KEY,
    ExternalId     nvarchar(128) NOT NULL CONSTRAINT UQ_AppUser_ExternalId UNIQUE,  -- Identity / Entra subject
    Email          nvarchar(254) NOT NULL CONSTRAINT UQ_AppUser_Email UNIQUE,
    DisplayName    nvarchar(120) NOT NULL,
    Role           varchar(20)   NOT NULL CONSTRAINT CK_AppUser_Role CHECK (Role IN
                       ('Admin','Reception','Collector','Technologist','Pathologist','Practitioner','Patient')),
    PatientId      int           NULL CONSTRAINT FK_AppUser_Patient      REFERENCES core.Patient,
    PractitionerId int           NULL CONSTRAINT FK_AppUser_Practitioner REFERENCES core.Practitioner,
    IsActive       bit           NOT NULL DEFAULT 1,
    CreatedAt      datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    LastLoginAt    datetime2(0)  NULL,
    CONSTRAINT CK_AppUser_PatientLink      CHECK (Role <> 'Patient'      OR PatientId IS NOT NULL),
    CONSTRAINT CK_AppUser_PractitionerLink CHECK (Role <> 'Practitioner' OR PractitionerId IS NOT NULL)
);
GO

/* =========================================================================
   sched: booking
   ========================================================================= */

CREATE TABLE sched.LocationHours (
    LocationId      smallint NOT NULL CONSTRAINT FK_LocationHours_Location REFERENCES core.Location,
    DayOfWeek       tinyint  NOT NULL CONSTRAINT CK_LocationHours_Day CHECK (DayOfWeek BETWEEN 0 AND 6),  -- 0 = Sunday
    OpenTime        time(0)  NOT NULL,
    CloseTime       time(0)  NOT NULL,
    SlotMinutes     tinyint  NOT NULL DEFAULT 10,
    CapacityPerSlot tinyint  NOT NULL DEFAULT 2,
    CONSTRAINT PK_LocationHours PRIMARY KEY (LocationId, DayOfWeek, OpenTime),
    CONSTRAINT CK_LocationHours_Range CHECK (OpenTime < CloseTime)
);

-- Statutory holidays and ad-hoc closures
CREATE TABLE sched.LocationClosure (
    LocationId smallint      NOT NULL CONSTRAINT FK_LocationClosure_Location REFERENCES core.Location,
    ClosedOn   date          NOT NULL,
    Reason     nvarchar(100) NULL,
    CONSTRAINT PK_LocationClosure PRIMARY KEY (LocationId, ClosedOn)
);

-- FHIR: Appointment
CREATE TABLE sched.Appointment (
    AppointmentId        int           IDENTITY CONSTRAINT PK_Appointment PRIMARY KEY,
    PatientId            int           NOT NULL CONSTRAINT FK_Appointment_Patient  REFERENCES core.Patient,
    LocationId           smallint      NOT NULL CONSTRAINT FK_Appointment_Location REFERENCES core.Location,
    StartUtc             datetime2(0)  NOT NULL,
    EndUtc               datetime2(0)  NOT NULL,
    Status               varchar(12)   NOT NULL DEFAULT 'Booked' CONSTRAINT CK_Appointment_Status CHECK (Status IN
                             ('Booked','CheckedIn','Completed','Cancelled','NoShow')),
    Channel              varchar(8)    NOT NULL DEFAULT 'Web' CONSTRAINT CK_Appointment_Channel CHECK (Channel IN ('Web','Phone','WalkIn')),
    RequisitionImageUri  nvarchar(500) NULL,   -- photo of the paper requisition, stored in Blob Storage
    Notes                nvarchar(500) NULL,
    CancelReason         nvarchar(200) NULL,
    CreatedAt            datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedByUserId      int           NULL CONSTRAINT FK_Appointment_CreatedBy REFERENCES sec.AppUser,
    RowVer               rowversion,
    CONSTRAINT CK_Appointment_Range CHECK (StartUtc < EndUtc)
);
CREATE INDEX IX_Appointment_Slot    ON sched.Appointment (LocationId, StartUtc) INCLUDE (Status);
CREATE INDEX IX_Appointment_Patient ON sched.Appointment (PatientId, StartUtc);
GO

/* =========================================================================
   lab: requisition -> specimen -> result -> report
   ========================================================================= */

-- FHIR: ServiceRequest (one requisition form)
CREATE TABLE lab.Requisition (
    RequisitionId          int            IDENTITY CONSTRAINT PK_Requisition PRIMARY KEY,
    AccessionNumber        varchar(20)    NOT NULL CONSTRAINT UQ_Requisition_Accession UNIQUE,
    PatientId              int            NOT NULL CONSTRAINT FK_Requisition_Patient      REFERENCES core.Patient,
    OrderingPractitionerId int            NOT NULL CONSTRAINT FK_Requisition_Practitioner REFERENCES core.Practitioner,
    LocationId             smallint       NOT NULL CONSTRAINT FK_Requisition_Location     REFERENCES core.Location,
    AppointmentId          int            NULL     CONSTRAINT FK_Requisition_Appointment  REFERENCES sched.Appointment,
    Priority               varchar(8)     NOT NULL DEFAULT 'Routine' CONSTRAINT CK_Requisition_Priority CHECK (Priority IN ('Routine','Stat')),
    Status                 varchar(20)    NOT NULL DEFAULT 'Received' CONSTRAINT CK_Requisition_Status CHECK (Status IN
                               ('Received','InProgress','PartiallyResulted','Final','Cancelled')),
    PayerType              varchar(10)    NOT NULL DEFAULT 'OHIP' CONSTRAINT CK_Requisition_Payer CHECK (PayerType IN ('OHIP','Patient','ThirdParty')),
    RequisitionDate        date           NOT NULL,   -- date signed by the practitioner
    ClinicalNotes          nvarchar(1000) NULL,
    IsPregnant             bit            NOT NULL DEFAULT 0,
    PregnancyWeek          tinyint        NULL,
    FastingHours           tinyint        NULL,
    ReceivedAt             datetime2(0)   NOT NULL DEFAULT SYSUTCDATETIME(),
    ReceivedByUserId       int            NULL CONSTRAINT FK_Requisition_ReceivedBy REFERENCES sec.AppUser,
    UpdatedAt              datetime2(0)   NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVer                 rowversion,
    CONSTRAINT CK_Requisition_Pregnancy CHECK (IsPregnant = 1 OR PregnancyWeek IS NULL)
);
CREATE INDEX IX_Requisition_Patient ON lab.Requisition (PatientId, ReceivedAt);
CREATE INDEX IX_Requisition_Status  ON lab.Requisition (Status, ReceivedAt) WHERE Status IN ('Received','InProgress','PartiallyResulted');
CREATE UNIQUE INDEX UX_Requisition_Appointment ON lab.Requisition (AppointmentId) WHERE AppointmentId IS NOT NULL;

-- Copy-to practitioners ("cc" box on the Ontario requisition)
CREATE TABLE lab.RequisitionCopyTo (
    RequisitionId  int NOT NULL CONSTRAINT FK_RequisitionCopyTo_Requisition  REFERENCES lab.Requisition,
    PractitionerId int NOT NULL CONSTRAINT FK_RequisitionCopyTo_Practitioner REFERENCES core.Practitioner,
    CONSTRAINT PK_RequisitionCopyTo PRIMARY KEY (RequisitionId, PractitionerId)
);

-- One ordered test or panel
CREATE TABLE lab.RequisitionItem (
    RequisitionItemId int           IDENTITY CONSTRAINT PK_RequisitionItem PRIMARY KEY,
    RequisitionId     int           NOT NULL CONSTRAINT FK_RequisitionItem_Requisition REFERENCES lab.Requisition,
    TestId            int           NOT NULL CONSTRAINT FK_RequisitionItem_Test        REFERENCES ref.Test,
    Status            varchar(12)   NOT NULL DEFAULT 'Ordered' CONSTRAINT CK_RequisitionItem_Status CHECK (Status IN
                          ('Ordered','Collected','InProgress','Resulted','Verified','Cancelled')),
    IsInsured         bit           NOT NULL,
    Price             decimal(10,2) NULL,   -- charged to the patient when not insured
    CancelReason      nvarchar(200) NULL,
    CONSTRAINT UQ_RequisitionItem UNIQUE (RequisitionId, TestId),
    CONSTRAINT CK_RequisitionItem_Price CHECK (IsInsured = 1 OR Price IS NOT NULL)
);

-- FHIR: Specimen (one labelled tube / container)
CREATE TABLE lab.Specimen (
    SpecimenId        int           IDENTITY CONSTRAINT PK_Specimen PRIMARY KEY,
    RequisitionId     int           NOT NULL CONSTRAINT FK_Specimen_Requisition REFERENCES lab.Requisition,
    Barcode           varchar(20)   NOT NULL CONSTRAINT UQ_Specimen_Barcode UNIQUE,
    SpecimenTypeCode  varchar(10)   NOT NULL CONSTRAINT FK_Specimen_Type REFERENCES ref.SpecimenType,
    CollectedAt       datetime2(0)  NOT NULL,
    CollectedByUserId int           NULL CONSTRAINT FK_Specimen_CollectedBy REFERENCES sec.AppUser,
    ReceivedInLabAt   datetime2(0)  NULL,
    Status            varchar(10)   NOT NULL DEFAULT 'Collected' CONSTRAINT CK_Specimen_Status CHECK (Status IN
                          ('Collected','Received','Rejected','Disposed')),
    RejectionReason   nvarchar(200) NULL,
    CONSTRAINT CK_Specimen_Rejection CHECK (Status <> 'Rejected' OR RejectionReason IS NOT NULL)
);
CREATE INDEX IX_Specimen_Requisition ON lab.Specimen (RequisitionId);

-- FHIR: Observation. For a panel, each member test gets its own row under the panel's RequisitionItem.
-- The reference range is copied onto the row at resulting time so a later range change
-- never rewrites a released report. Temporal: corrections keep the previous value.
CREATE TABLE lab.Result (
    ResultId           bigint         IDENTITY CONSTRAINT PK_Result PRIMARY KEY,
    RequisitionItemId  int            NOT NULL CONSTRAINT FK_Result_RequisitionItem REFERENCES lab.RequisitionItem,
    TestId             int            NOT NULL CONSTRAINT FK_Result_Test            REFERENCES ref.Test,
    SpecimenId         int            NULL     CONSTRAINT FK_Result_Specimen        REFERENCES lab.Specimen,
    ValueNumeric       decimal(18,6)  NULL,
    ValueText          nvarchar(1000) NULL,
    UnitCode           varchar(20)    NULL     CONSTRAINT FK_Result_Unit REFERENCES ref.Unit,
    RefLow             decimal(18,6)  NULL,
    RefHigh            decimal(18,6)  NULL,
    RefText            nvarchar(100)  NULL,
    AbnormalFlag       varchar(2)     NULL CONSTRAINT CK_Result_Flag CHECK (AbnormalFlag IN ('N','L','H','LL','HH','A')),  -- HL7 table 0078
    IsCritical         AS CAST(CASE WHEN AbnormalFlag IN ('LL','HH') THEN 1 ELSE 0 END AS bit),
    CriticalCalledAt   datetime2(0)   NULL,   -- critical values must be phoned to the practitioner
    CriticalCalledTo   nvarchar(150)  NULL,
    Status             varchar(12)    NOT NULL DEFAULT 'Preliminary' CONSTRAINT CK_Result_Status CHECK (Status IN
                           ('Preliminary','Final','Corrected','Cancelled')),
    Comment            nvarchar(1000) NULL,
    ResultedAt         datetime2(0)   NOT NULL DEFAULT SYSUTCDATETIME(),
    ResultedByUserId   int            NULL CONSTRAINT FK_Result_ResultedBy REFERENCES sec.AppUser,
    VerifiedAt         datetime2(0)   NULL,
    VerifiedByUserId   int            NULL CONSTRAINT FK_Result_VerifiedBy REFERENCES sec.AppUser,
    LegacyAnswer       varchar(250)   NULL,   -- raw value from the legacy system, ETL rows only
    RowVer             rowversion,
    ValidFrom          datetime2(0)   GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo            datetime2(0)   GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),
    CONSTRAINT UQ_Result UNIQUE (RequisitionItemId, TestId),
    CONSTRAINT CK_Result_HasValue CHECK (Status = 'Cancelled' OR ValueNumeric IS NOT NULL OR ValueText IS NOT NULL),
    CONSTRAINT CK_Result_Verified CHECK (Status NOT IN ('Final','Corrected') OR VerifiedAt IS NOT NULL)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = history.Result));
CREATE INDEX IX_Result_Test ON lab.Result (TestId, ResultedAt);   -- cumulative / trend views

-- FHIR: DiagnosticReport. A new version is issued for every amendment.
CREATE TABLE lab.Report (
    ReportId          int           IDENTITY CONSTRAINT PK_Report PRIMARY KEY,
    RequisitionId     int           NOT NULL CONSTRAINT FK_Report_Requisition REFERENCES lab.Requisition,
    Version           smallint      NOT NULL DEFAULT 1,
    Status            varchar(12)   NOT NULL CONSTRAINT CK_Report_Status CHECK (Status IN ('Preliminary','Final','Amended')),
    IssuedAt          datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    IssuedByUserId    int           NULL CONSTRAINT FK_Report_IssuedBy REFERENCES sec.AppUser,
    PdfBlobUri        nvarchar(500) NULL,
    Hl7MessageId      varchar(50)   NULL,   -- ORU^R01 control id when exported (OLIS simulation)
    IsPatientVisible  bit           NOT NULL DEFAULT 0,
    CONSTRAINT UQ_Report_Version UNIQUE (RequisitionId, Version)
);

-- AI-generated interpretation. Decision support only: it reaches the patient only after
-- a clinician approves it (CK_ResultInterpretation_Review).
CREATE TABLE lab.ResultInterpretation (
    InterpretationId int            IDENTITY CONSTRAINT PK_ResultInterpretation PRIMARY KEY,
    RequisitionId    int            NOT NULL CONSTRAINT FK_ResultInterpretation_Requisition REFERENCES lab.Requisition,
    ReportId         int            NULL     CONSTRAINT FK_ResultInterpretation_Report      REFERENCES lab.Report,
    Audience         varchar(10)    NOT NULL CONSTRAINT CK_ResultInterpretation_Audience CHECK (Audience IN ('Patient','Clinician')),
    Language         char(2)        NOT NULL DEFAULT 'en' CONSTRAINT CK_ResultInterpretation_Language CHECK (Language IN ('en','fr')),
    ContentMarkdown  nvarchar(max)  NOT NULL,
    FindingsJson     nvarchar(max)  NULL CONSTRAINT CK_ResultInterpretation_Json CHECK (FindingsJson IS NULL OR ISJSON(FindingsJson) = 1),
    Model            varchar(64)    NOT NULL,
    PromptVersion    varchar(20)    NOT NULL,
    InputTokens      int            NULL,
    OutputTokens     int            NULL,
    Status           varchar(12)    NOT NULL DEFAULT 'Draft' CONSTRAINT CK_ResultInterpretation_Status CHECK (Status IN
                         ('Draft','Approved','Rejected','Superseded')),
    ReviewedByUserId int            NULL CONSTRAINT FK_ResultInterpretation_ReviewedBy REFERENCES sec.AppUser,
    ReviewedAt       datetime2(0)   NULL,
    ReviewComment    nvarchar(500)  NULL,
    CreatedAt        datetime2(0)   NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_ResultInterpretation_Review CHECK (Status NOT IN ('Approved','Rejected')
                                                     OR (ReviewedByUserId IS NOT NULL AND ReviewedAt IS NOT NULL))
);
CREATE INDEX IX_ResultInterpretation_Requisition ON lab.ResultInterpretation (RequisitionId, Status);
GO

/* =========================================================================
   notify: transactional outbox
   Rows are written in the same transaction as the business change; an Azure Function
   picks up Queued rows and sends them. Messages never contain results (PHIPA) -
   only a link to the patient portal.
   ========================================================================= */

CREATE TABLE notify.Notification (
    NotificationId    bigint         IDENTITY CONSTRAINT PK_Notification PRIMARY KEY,
    PatientId         int            NULL CONSTRAINT FK_Notification_Patient      REFERENCES core.Patient,
    PractitionerId    int            NULL CONSTRAINT FK_Notification_Practitioner REFERENCES core.Practitioner,
    RequisitionId     int            NULL CONSTRAINT FK_Notification_Requisition  REFERENCES lab.Requisition,
    AppointmentId     int            NULL CONSTRAINT FK_Notification_Appointment  REFERENCES sched.Appointment,
    Channel           varchar(8)     NOT NULL CONSTRAINT CK_Notification_Channel CHECK (Channel IN ('Email','Sms')),
    TemplateCode      varchar(40)    NOT NULL,   -- AppointmentConfirmed, AppointmentReminder, ResultsReady, CriticalResult ...
    Language          char(2)        NOT NULL DEFAULT 'en',
    Recipient         nvarchar(254)  NOT NULL,
    Status            varchar(10)    NOT NULL DEFAULT 'Queued' CONSTRAINT CK_Notification_Status CHECK (Status IN
                          ('Queued','Sending','Sent','Failed','Cancelled')),
    AttemptCount      tinyint        NOT NULL DEFAULT 0,
    NextAttemptAt     datetime2(0)   NOT NULL DEFAULT SYSUTCDATETIME(),
    QueuedAt          datetime2(0)   NOT NULL DEFAULT SYSUTCDATETIME(),
    SentAt            datetime2(0)   NULL,
    LastError         nvarchar(1000) NULL,
    ProviderMessageId varchar(100)   NULL,
    CONSTRAINT CK_Notification_Target CHECK (PatientId IS NOT NULL OR PractitionerId IS NOT NULL)
);
CREATE INDEX IX_Notification_Pending ON notify.Notification (NextAttemptAt) WHERE Status = 'Queued';
GO

/* =========================================================================
   billing
   ========================================================================= */

CREATE TABLE billing.OhipClaim (
    ClaimId                     int           IDENTITY CONSTRAINT PK_OhipClaim PRIMARY KEY,
    RequisitionId               int           NOT NULL CONSTRAINT FK_OhipClaim_Requisition REFERENCES lab.Requisition
                                              CONSTRAINT UQ_OhipClaim_Requisition UNIQUE,
    PatientId                   int           NOT NULL CONSTRAINT FK_OhipClaim_Patient REFERENCES core.Patient,
    HealthCardNumber            char(10)      NOT NULL,   -- snapshot at claim time
    HealthCardVersion           varchar(2)    NULL,
    ReferringOhipBillingNumber  char(6)       NULL,
    ServiceDate                 date          NOT NULL,
    Status                      varchar(10)   NOT NULL DEFAULT 'Draft' CONSTRAINT CK_OhipClaim_Status CHECK (Status IN
                                    ('Draft','Submitted','Accepted','Rejected','Paid')),
    BatchNumber                 varchar(20)   NULL,
    SubmittedAt                 datetime2(0)  NULL,
    TotalAmount                 decimal(10,2) NOT NULL DEFAULT 0,
    ErrorCodes                  varchar(50)   NULL,
    CreatedAt                   datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE billing.OhipClaimItem (
    ClaimItemId       int           IDENTITY CONSTRAINT PK_OhipClaimItem PRIMARY KEY,
    ClaimId           int           NOT NULL CONSTRAINT FK_OhipClaimItem_Claim REFERENCES billing.OhipClaim,
    RequisitionItemId int           NOT NULL CONSTRAINT FK_OhipClaimItem_RequisitionItem REFERENCES lab.RequisitionItem,
    FeeCode           varchar(10)   NOT NULL,
    Units             tinyint       NOT NULL DEFAULT 1,
    Amount            decimal(10,2) NOT NULL,
    ExplanatoryCode   varchar(5)    NULL
);

-- Uninsured tests paid by the patient
CREATE TABLE billing.Invoice (
    InvoiceId        int           IDENTITY CONSTRAINT PK_Invoice PRIMARY KEY,
    RequisitionId    int           NOT NULL CONSTRAINT FK_Invoice_Requisition REFERENCES lab.Requisition,
    PatientId        int           NOT NULL CONSTRAINT FK_Invoice_Patient REFERENCES core.Patient,
    Subtotal         decimal(10,2) NOT NULL,
    Tax              decimal(10,2) NOT NULL DEFAULT 0,
    Total            AS (Subtotal + Tax) PERSISTED,
    Status           varchar(10)   NOT NULL DEFAULT 'Open' CONSTRAINT CK_Invoice_Status CHECK (Status IN ('Open','Paid','Void','Refunded')),
    PaymentProvider  varchar(20)   NULL,
    PaymentReference varchar(100)  NULL,
    PaidAt           datetime2(0)  NULL,
    CreatedAt        datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

/* =========================================================================
   audit: PHIPA access log (append-only)
   No foreign keys on purpose: the log must accept any entity and outlive it.
   ========================================================================= */

CREATE TABLE audit.AccessLog (
    AuditId       bigint           IDENTITY CONSTRAINT PK_AccessLog PRIMARY KEY,
    OccurredAt    datetime2(3)     NOT NULL DEFAULT SYSUTCDATETIME(),
    UserId        int              NULL,
    Action        varchar(20)      NOT NULL CONSTRAINT CK_AccessLog_Action CHECK (Action IN
                      ('View','Create','Update','Delete','Print','Export','Email','Login','LoginFailed','AiGenerate')),
    EntityType    varchar(50)      NOT NULL,
    EntityId      varchar(50)      NULL,
    PatientId     int              NULL,   -- lets us answer "who looked at my record?"
    Details       nvarchar(max)    NULL CONSTRAINT CK_AccessLog_Json CHECK (Details IS NULL OR ISJSON(Details) = 1),
    IpAddress     varchar(45)      NULL,
    CorrelationId uniqueidentifier NULL
);
CREATE INDEX IX_AccessLog_Patient ON audit.AccessLog (PatientId, OccurredAt) WHERE PatientId IS NOT NULL;
CREATE INDEX IX_AccessLog_User    ON audit.AccessLog (UserId, OccurredAt);
GO

CREATE TRIGGER audit.TR_AccessLog_AppendOnly ON audit.AccessLog
INSTEAD OF UPDATE, DELETE
AS
    THROW 50001, 'audit.AccessLog is append-only.', 1;
GO

/* =========================================================================
   etl: legacy Laboratory.dbo.AzDefine -> ref.Test mapping worksheet
   ========================================================================= */

CREATE TABLE etl.LegacyTestMap (
    LegacyAzId       int           NOT NULL CONSTRAINT PK_LegacyTestMap PRIMARY KEY,
    LegacyCode       varchar(12)   NULL,
    LegacyName       varchar(50)   NULL,
    LegacyUnit       varchar(15)   NULL,
    TestId           int           NULL CONSTRAINT FK_LegacyTestMap_Test REFERENCES ref.Test,
    Factor           decimal(18,8) NULL,   -- legacy value * Factor = SI value
    MapStatus        varchar(12)   NOT NULL DEFAULT 'Unmapped' CONSTRAINT CK_LegacyTestMap_Status CHECK (MapStatus IN
                         ('Unmapped','Suggested','Reviewed','Excluded')),
    Notes            nvarchar(300) NULL,
    ReviewedAt       datetime2(0)  NULL,
    CONSTRAINT CK_LegacyTestMap_Reviewed CHECK (MapStatus <> 'Reviewed' OR TestId IS NOT NULL)
);
GO

/* =========================================================================
   Functions and views
   ========================================================================= */

-- Best matching reference range for a test, sex, age and pregnancy week.
-- A sex-specific or pregnancy-specific range wins over a generic one.
CREATE FUNCTION ref.fn_ReferenceRangeFor
(
    @TestId        int,
    @Sex           char(1),
    @AgeDays       int,
    @PregnancyWeek tinyint = NULL,
    @OnDate        date
)
RETURNS TABLE
AS
RETURN
    SELECT TOP (1) r.ReferenceRangeId, r.Low, r.High, r.CriticalLow, r.CriticalHigh, r.DisplayText
    FROM ref.ReferenceRange r
    WHERE r.TestId = @TestId
      AND r.Sex IN (@Sex, 'U')
      AND @AgeDays BETWEEN r.AgeFromDays AND r.AgeToDays
      AND (r.PregnancyWeekFrom IS NULL OR @PregnancyWeek BETWEEN r.PregnancyWeekFrom AND r.PregnancyWeekTo)
      AND @OnDate >= r.EffectiveFrom
      AND (r.EffectiveTo IS NULL OR @OnDate < r.EffectiveTo)
    ORDER BY CASE WHEN r.PregnancyWeekFrom IS NOT NULL THEN 0 ELSE 1 END,
             CASE WHEN r.Sex = @Sex THEN 0 ELSE 1 END,
             r.AgeToDays - r.AgeFromDays;
GO

-- HL7 0078 abnormal flag for a numeric value against a range
CREATE FUNCTION lab.fn_AbnormalFlag
(
    @Value        decimal(18,6),
    @Low          decimal(18,6),
    @High         decimal(18,6),
    @CriticalLow  decimal(18,6),
    @CriticalHigh decimal(18,6)
)
RETURNS TABLE
AS
RETURN
    SELECT Flag = CASE
        WHEN @Value IS NULL                                  THEN NULL
        WHEN @CriticalLow  IS NOT NULL AND @Value < @CriticalLow  THEN 'LL'
        WHEN @CriticalHigh IS NOT NULL AND @Value > @CriticalHigh THEN 'HH'
        WHEN @Low  IS NOT NULL AND @Value < @Low             THEN 'L'
        WHEN @High IS NOT NULL AND @Value > @High            THEN 'H'
        WHEN @Low IS NULL AND @High IS NULL                  THEN NULL
        ELSE 'N'
    END;
GO

-- Flat result list used by the report, the portal and the AI prompt builder
CREATE VIEW lab.vw_ResultDetail
AS
SELECT
    q.RequisitionId,
    q.AccessionNumber,
    q.PatientId,
    p.Sex,
    p.DateOfBirth,
    AgeYears      = DATEDIFF(YEAR, p.DateOfBirth, q.ReceivedAt)
                    - CASE WHEN DATEADD(YEAR, DATEDIFF(YEAR, p.DateOfBirth, q.ReceivedAt), p.DateOfBirth) > q.ReceivedAt THEN 1 ELSE 0 END,
    OrderedTest   = ot.NameEn,
    d.Code        AS DepartmentCode,
    d.NameEn      AS DepartmentName,
    t.TestId,
    t.Code        AS TestCode,
    t.LoincCode,
    t.NameEn      AS TestName,
    t.NameFr      AS TestNameFr,
    r.ResultId,
    r.ValueNumeric,
    r.ValueText,
    r.UnitCode,
    u.Display     AS UnitDisplay,
    r.RefLow,
    r.RefHigh,
    r.RefText,
    r.AbnormalFlag,
    r.IsCritical,
    r.Status      AS ResultStatus,
    r.Comment,
    r.ResultedAt,
    r.VerifiedAt,
    SortOrder     = ISNULL(pm.SortOrder, 0)
FROM lab.Requisition q
JOIN core.Patient p        ON p.PatientId = q.PatientId
JOIN lab.RequisitionItem i ON i.RequisitionId = q.RequisitionId
JOIN ref.Test ot           ON ot.TestId = i.TestId
JOIN lab.Result r          ON r.RequisitionItemId = i.RequisitionItemId
JOIN ref.Test t            ON t.TestId = r.TestId
JOIN ref.Department d      ON d.DepartmentId = t.DepartmentId
LEFT JOIN ref.Unit u       ON u.UnitCode = r.UnitCode
LEFT JOIN ref.PanelMember pm ON pm.PanelTestId = i.TestId AND pm.MemberTestId = r.TestId;
GO
