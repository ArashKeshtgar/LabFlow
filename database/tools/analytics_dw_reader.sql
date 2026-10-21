-- Read-only login for Analytics on LabFlow's warehouse: the monthly totals view and the test catalogue's names and
-- codes, nothing else (no rows, values, patients, payer names or legacy numbers).
--
-- Not a numbered script on purpose: it needs a password, and LabFlow applies numbered scripts at start-up. Run it once
-- per server, after 054_analytics_dw_view.sql, and put the password only in Analytics' settings:
--   sqlcmd -S . -E -C -b -v ReaderPassword="<a new password>" -i database/tools/analytics_dw_reader.sql

USE master;
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'analytics_dw_reader')
    CREATE LOGIN analytics_dw_reader WITH PASSWORD = N'$(ReaderPassword)', CHECK_POLICY = ON;
GO

USE LabFlow;
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'analytics_dw_reader')
    CREATE USER analytics_dw_reader FOR LOGIN analytics_dw_reader;
GO

GRANT SELECT ON dw.vw_AnalyticsMonthly TO analytics_dw_reader;
GRANT SELECT ON dw.DimTest (TestKey, LoincCode, LabFlowCode, TestName, Section, IsPanel) TO analytics_dw_reader;
GO
