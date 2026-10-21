-- "Breaking the glass" on an order the clinic has restricted (a patient's consent directive) is written to the audit log
-- as its own action, with the reason in Details. The log's action list is a CHECK constraint, so it gains one value.
-- Safe to run again: LabFlow applies scripts from 050 on to existing databases at start-up.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_AccessLog_Action' AND definition LIKE N'%RestrictedOverride%')
BEGIN
    ALTER TABLE audit.AccessLog DROP CONSTRAINT CK_AccessLog_Action;
    ALTER TABLE audit.AccessLog ADD CONSTRAINT CK_AccessLog_Action CHECK (Action IN
        ('View','Create','Update','Delete','Print','Export','Email','Login','LoginFailed','AiGenerate','RestrictedOverride'));
END
GO
