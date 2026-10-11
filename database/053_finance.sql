-- The lab's money to FinancePowerApp, the group's accounting (Auth/FinanceSender.cs).
-- Safe to run again: LabFlow applies scripts from 050 on to existing databases at start-up.

-- What OHIP pays for an insured test (the Schedule of Benefits for Laboratory Services). Left empty, insured work
-- is not sent to the accounting: the demo has the fee codes, not the amounts.
IF COL_LENGTH(N'ref.Test', N'OhipFee') IS NULL
ALTER TABLE ref.Test ADD OhipFee decimal(10,2) NULL CONSTRAINT CK_Test_OhipFee CHECK (OhipFee IS NULL OR OhipFee >= 0);
GO

-- Financial documents FinancePowerApp has accepted, so each is sent once.
IF OBJECT_ID(N'billing.FinanceExport', N'U') IS NULL
CREATE TABLE billing.FinanceExport (
    ExternalId  varchar(200) NOT NULL CONSTRAINT PK_FinanceExport PRIMARY KEY,
    SentAt      datetime2(0) NOT NULL CONSTRAINT DF_FinanceExport_SentAt DEFAULT SYSUTCDATETIME()
);
GO
