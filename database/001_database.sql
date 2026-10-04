-- LabFlow: Ontario community lab portfolio app
-- Creates the database. Run against master with -v DatabaseName=LabFlow
-- (deploy.ps1 passes it; a :setvar here would override -v).

IF DB_ID(N'$(DatabaseName)') IS NULL
    CREATE DATABASE [$(DatabaseName)] COLLATE Latin1_General_100_CI_AS_SC;
GO

ALTER DATABASE [$(DatabaseName)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO
