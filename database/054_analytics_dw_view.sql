-- What Analytics (the group's reporting) reads from the warehouse: monthly totals per test and kind of payer, so no
-- row, value, patient key or legacy visit number leaves LabFlow. Its login may read this view and nothing else in dw
-- (database/tools/analytics_dw_reader.sql); the view reads the tables through ownership chaining.
-- Safe to run again: LabFlow applies scripts from 050 on to existing databases at start-up. A database without the
-- warehouse (a clinic's own LabFlow database) is left alone.
IF OBJECT_ID(N'dw.FactLabResult', N'U') IS NOT NULL
EXEC (N'CREATE OR ALTER VIEW dw.vw_AnalyticsMonthly AS
SELECT
    d.[Year] AS [Year], d.[Month] AS [Month],                -- the visit''s month, after the warehouse''s date shift
    f.TestKey, p.PayerType,
    COUNT(*)                                       AS Results,
    COUNT(DISTINCT f.LegacyVisitNo)                AS Visits,
    SUM(CAST(f.HasAnswer AS int))                  AS Answered,
    SUM(CAST(f.IsAbnormal AS int))                 AS Abnormal,
    SUM(CAST(f.IsCritical AS int))                 AS Critical,
    SUM(CAST(f.TurnaroundDays AS int))             AS TurnaroundDaysSum,
    COUNT(f.TurnaroundDays)                        AS TurnaroundCount
FROM dw.FactLabResult f
JOIN dw.DimDate d ON d.DateKey = f.VisitDateKey
JOIN dw.DimPayer p ON p.PayerKey = f.PayerKey
GROUP BY d.[Year], d.[Month], f.TestKey, p.PayerType');
GO
