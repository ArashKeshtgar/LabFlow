-- SQL Server Agent job that runs the LabFlow ETL package every night at 02:00.
--
--   sqlcmd -S . -E -i etl\agent_job.sql -v PackagePath="D:\E\Projects\LabFlow\etl\LabFlowETL\LabFlowETL.dtsx"
--
-- The job runs as the Agent service account, so that account gets the least
-- it needs: read the legacy database, run the etl procedures and fast-load
-- the staging tables. Needs the Integration Services feature (SSIS job steps).

:setvar AgentAccount "NT SERVICE\SQLSERVERAGENT"

USE master;
IF SUSER_ID(N'$(AgentAccount)') IS NULL
    CREATE LOGIN [$(AgentAccount)] FROM WINDOWS;
GO

USE Laboratory;
IF USER_ID(N'$(AgentAccount)') IS NULL CREATE USER [$(AgentAccount)] FOR LOGIN [$(AgentAccount)];
ALTER ROLE db_datareader ADD MEMBER [$(AgentAccount)];
GO

USE LabFlow;
IF USER_ID(N'$(AgentAccount)') IS NULL CREATE USER [$(AgentAccount)] FOR LOGIN [$(AgentAccount)];
IF DATABASE_PRINCIPAL_ID(N'etl_runner') IS NULL CREATE ROLE etl_runner;
GRANT EXECUTE ON SCHEMA::etl TO etl_runner;
GRANT INSERT, SELECT ON etl.stg_Test   TO etl_runner;
GRANT INSERT, SELECT ON etl.stg_Payer  TO etl_runner;
GRANT INSERT, SELECT ON etl.stg_Visit  TO etl_runner;
GRANT INSERT, SELECT ON etl.stg_Result TO etl_runner;
ALTER ROLE etl_runner ADD MEMBER [$(AgentAccount)];
GO

USE msdb;
DECLARE @job sysname = N'LabFlow ETL (nightly)';
IF EXISTS (SELECT 1 FROM dbo.sysjobs WHERE name = @job)
    EXEC dbo.sp_delete_job @job_name = @job;

EXEC dbo.sp_add_job @job_name = @job,
    @description = N'Legacy Laboratory -> de-identified staging -> LabFlow dw. Package: etl/LabFlowETL/LabFlowETL.dtsx. Run log: LabFlow.etl.vw_RunHistory.';
EXEC dbo.sp_add_jobstep @job_name = @job, @step_name = N'Run LabFlowETL',
    @subsystem = N'SSIS',
    @command = N'/FILE "$(PackagePath)" /CHECKPOINTING OFF /REPORTING E',
    @retry_attempts = 1, @retry_interval = 10;
EXEC dbo.sp_add_jobschedule @job_name = @job, @name = N'Nightly 02:00',
    @freq_type = 4, @freq_interval = 1, @active_start_time = 020000;
EXEC dbo.sp_add_jobserver @job_name = @job;
GO
