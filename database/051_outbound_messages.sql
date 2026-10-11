-- Messages LabFlow sends to a clinic system (ClinicHub): results as FHIR DiagnosticReports. Written in the same
-- transaction as the release that produces them, delivered by a worker that retries with growing pauses.
-- Safe to run again: LabFlow applies scripts from 050 on to existing databases at start-up.

IF OBJECT_ID(N'lab.OutboundMessage') IS NULL
CREATE TABLE lab.OutboundMessage (
    OutboundMessageId int            IDENTITY CONSTRAINT PK_OutboundMessage PRIMARY KEY,
    Destination       varchar(20)    NOT NULL CONSTRAINT CK_OutboundMessage_Destination CHECK (Destination IN ('ClinicHub')),
    Kind              varchar(40)    NOT NULL,
    RequisitionId     int            NULL CONSTRAINT FK_OutboundMessage_Requisition REFERENCES lab.Requisition,
    PayloadJson       nvarchar(max)  NOT NULL CONSTRAINT CK_OutboundMessage_Json CHECK (ISJSON(PayloadJson) = 1),
    CreatedAt         datetime2(0)   NOT NULL CONSTRAINT DF_OutboundMessage_Created DEFAULT SYSUTCDATETIME(),
    SentAt            datetime2(0)   NULL,
    Attempts          int            NOT NULL CONSTRAINT DF_OutboundMessage_Attempts DEFAULT 0,
    NextAttemptAt     datetime2(0)   NULL,
    LastError         nvarchar(1000) NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboundMessage_Pending')
CREATE INDEX IX_OutboundMessage_Pending ON lab.OutboundMessage (NextAttemptAt) WHERE SentAt IS NULL;
GO
