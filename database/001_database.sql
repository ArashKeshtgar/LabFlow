-- LabFlow: Ontario community lab portfolio app
-- Creates the database. Run against master.

IF DB_ID(N'LabFlow') IS NULL
    CREATE DATABASE LabFlow COLLATE Latin1_General_100_CI_AS_SC;
GO

ALTER DATABASE LabFlow SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO
