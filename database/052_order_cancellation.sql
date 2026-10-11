-- A clinic withdrawing a test it ordered (ClinicHub sends the ServiceRequest again with status "revoked").
-- While the order waits, the test is struck from it; once it is a requisition, the requisition item is cancelled
-- as long as no result has been entered. The item keeps its row, so what was ordered and withdrawn stays on record.
-- Safe to run again: LabFlow applies scripts from 050 on to existing databases at start-up.

IF COL_LENGTH(N'lab.ElectronicOrderItem', N'CancelledAt') IS NULL
ALTER TABLE lab.ElectronicOrderItem ADD
    CancelledAt  datetime2(0)  NULL,
    CancelReason nvarchar(200) NULL;
GO
