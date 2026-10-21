-- From ClinicHub with each electronic order (IntegrationHub's flows 6 and 7): the clinic's MRN of the patient, so that
-- a merge at the clinic can be followed here, and the order's confidentiality label (a consent directive: R restricted,
-- V very restricted), which goes back on its results.
-- Safe to run again: LabFlow applies scripts from 050 on to existing databases at start-up.
IF COL_LENGTH(N'lab.ElectronicOrder', N'SourceMrn') IS NULL
ALTER TABLE lab.ElectronicOrder ADD SourceMrn varchar(40) NULL;
GO
IF COL_LENGTH(N'lab.ElectronicOrder', N'Confidentiality') IS NULL
ALTER TABLE lab.ElectronicOrder ADD Confidentiality char(1) NULL
    CONSTRAINT CK_ElectronicOrder_Confidentiality CHECK (Confidentiality IN ('R', 'V'));
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ElectronicOrder_SourceMrn')
CREATE INDEX IX_ElectronicOrder_SourceMrn ON lab.ElectronicOrder (SourceClinic, SourceMrn) WHERE SourceMrn IS NOT NULL;
GO
