-- Electronic orders received from a clinic system (ClinicHub) as FHIR ServiceRequests.
-- An order waits here until the patient comes to collection, where reception turns it into a
-- requisition (lab.Requisition): the order is the physician's, the requisition is the lab's.
-- Safe to run again: LabFlow applies scripts from 050 on to existing databases at start-up.
-- FHIR: ServiceRequest (one ElectronicOrderItem per ServiceRequest, grouped by requisition).

IF OBJECT_ID(N'lab.ElectronicOrder') IS NULL
CREATE TABLE lab.ElectronicOrder (
    ElectronicOrderId int            IDENTITY CONSTRAINT PK_ElectronicOrder PRIMARY KEY,
    SourceClinic      varchar(40)    NOT NULL,                 -- the sending clinic (ClinicHub clinic short name)
    ExternalId        nvarchar(80)   NOT NULL,                 -- its requisition identifier: a resend is recognised
    PatientId         int            NOT NULL CONSTRAINT FK_ElectronicOrder_Patient      REFERENCES core.Patient,
    PractitionerId    int            NOT NULL CONSTRAINT FK_ElectronicOrder_Practitioner REFERENCES core.Practitioner,
    Priority          varchar(10)    NOT NULL CONSTRAINT CK_ElectronicOrder_Priority CHECK (Priority IN ('Routine','Stat')),
    ClinicalNotes     nvarchar(1000) NULL,
    Status            varchar(20)    NOT NULL CONSTRAINT DF_ElectronicOrder_Status DEFAULT 'Waiting'
                          CONSTRAINT CK_ElectronicOrder_Status CHECK (Status IN ('Waiting','Requisitioned','Cancelled')),
    ReceivedAt        datetime2(0)   NOT NULL CONSTRAINT DF_ElectronicOrder_Received DEFAULT SYSUTCDATETIME(),
    RequisitionId     int            NULL CONSTRAINT FK_ElectronicOrder_Requisition REFERENCES lab.Requisition,
    CONSTRAINT UQ_ElectronicOrder_External UNIQUE (SourceClinic, ExternalId),
    CONSTRAINT CK_ElectronicOrder_Requisitioned CHECK (Status <> 'Requisitioned' OR RequisitionId IS NOT NULL)
);
GO

IF OBJECT_ID(N'lab.ElectronicOrderItem') IS NULL
CREATE TABLE lab.ElectronicOrderItem (
    ElectronicOrderId int          NOT NULL CONSTRAINT FK_ElectronicOrderItem_Order REFERENCES lab.ElectronicOrder ON DELETE CASCADE,
    TestId            int          NOT NULL CONSTRAINT FK_ElectronicOrderItem_Test  REFERENCES ref.Test,
    ExternalId        nvarchar(40) NOT NULL,   -- the sender's ServiceRequest id
    CONSTRAINT PK_ElectronicOrderItem PRIMARY KEY (ElectronicOrderId, TestId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ElectronicOrder_Waiting')
CREATE INDEX IX_ElectronicOrder_Waiting ON lab.ElectronicOrder (ReceivedAt) WHERE Status = 'Waiting';
GO
