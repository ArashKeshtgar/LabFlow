# LabFlow ETL — legacy Laboratory → reporting warehouse

An SSIS pipeline that moves results from the legacy Iranian lab system
(`Laboratory`, SQL Server, code page 1256, Jalali dates) into a star schema
(`LabFlow.dw`) for reporting, mapping tests to LOINC and SI units on the way
and de-identifying everything before it leaves the legacy database.

```
Laboratory (legacy)                LabFlow
──────────────────                 ───────────────────────────────────────────────
dbo.AzDefine ─┐                    etl.stg_Test ─┐
dbo.Bime ─────┤  SSIS data flow    etl.stg_Payer ┤   etl.usp_TransformLoad   dw.DimTest / DimPayer
dbo.MRJ+PRV ──┤ ───────────────▶   etl.stg_Visit ┤ ──────────────────────▶   dw.DimPatient / DimDate
dbo.MRJAZ ────┘  (de-identified)   etl.stg_Result┘   (one transaction)       dw.FactLabResult
                                                                             etl.RejectRow, etl.LoadRun
```

## The package (`LabFlowETL/LabFlowETL.dtsx`)

| Step | What it does |
|---|---|
| **SQL Start run** | `etl.usp_StartRun`: opens a row in `etl.LoadRun`, truncates staging, returns the run id, the hash salt and the first visit to extract (high-water mark minus `LookbackVisits`, so answers typed in after the visit are picked up). |
| **DFT Extract legacy** | Four parallel paths into `etl.stg_*`. Tests and payers go through a **Data Conversion** (code page 1256 → Unicode — SSIS will not load one code page into another). Visits and results come from **expression-built queries** that hash the patient code, with a **Row Count** each. Destinations use fast load (`TABLOCK, CHECK_CONSTRAINTS`). |
| **SQL Transform and load** | `etl.usp_TransformLoad`: Jalali → Gregorian, panel handling, LOINC/SI mapping, data-quality rules, `MERGE` into the dimensions and the fact, watermark update — all in one transaction. |
| **OnError → SQL Fail run** | `etl.usp_FailRun` closes the run as Failed with the failing task and message. |

Settings are package variables (set per environment with `dtexec /SET` or in the SQL Agent
job step): `LegacyServer`, `LegacyDatabase`, `LabFlowServer`, `LabFlowDatabase`, `LookbackVisits`.
Connections are built from them by expressions; nothing is hard-coded to this PC. (Variables,
not project parameters, because the package runs from a file and `dtexec /Par` only reaches
catalog-deployed packages.)

The `.dtsx` is generated from [`builder/BuildPackage.cs`](builder/BuildPackage.cs) with the
SSIS object model (`./build.ps1`), so the package is reviewable and reproducible as code;
it opens in Visual Studio's SSIS designer like a hand-drawn one.

## De-identification (PHIPA)

- The extract **never reads** names, father's name, phone, mobile, address, national code,
  ID number, birth date, birth city or the face/fingerprint images — they are not in any query.
- The legacy patient code leaves as `SHA-256(salt | code)`, first 16 hex characters.
  The salt is random, created on the first run, and stored only in `etl.Secret`.
- Every date in `dw` is shifted by one secret offset (±1–180 days, also in `etl.Secret`),
  so turnaround times and weekly patterns stay true while real calendar dates do not.
- Age is kept in years (infants as 0) and in bands; no date of birth anywhere.

## Transform rules

| Legacy quirk | Rule |
|---|---|
| Dates are Jalali `yyyy/mm/dd` text | `etl.fn_JalaliToGregorian` (jdf arithmetic; checked on 8 known dates incl. leap 30 Esfand) |
| A panel is a header row (`ChildAz_ID = Az_ID`, answer `.`) plus one row per member | Header rows are skipped; members load with `PanelTestKey` |
| `''`, `-`, `_`, `.` mean "no answer" — and `''` casts to **0** in T-SQL | Treated as no answer; only plain decimals become numbers |
| Units are conventional (mg/dL, /cumm, gr/dl), one mislabelled (TSH "NMol/l") | `etl.LegacyTestMap` → LabFlow test (LOINC) + factor to SI; each factor checked against the legacy values and printed ranges ([045_seed_legacy_map.sql](../database/045_seed_legacy_map.sql)) |
| Sex is 0/1/2/3 | 0 = F, 1 = M (verified against beta-HCG and semen-analysis orders), else U |
| Payer 0 | Self-pay |

Data-quality findings go to `etl.RejectRow`: **Rejected** (not loaded: unknown test, visit
missing, invalid date) or **Warning** (loaded, flagged: implausible age, answer date before
the visit, no answer date, non-numeric answer for a numeric test).

## Results on the legacy copy (SSIS runs, 2026-10-04)

| | |
|---|---|
| Visits extracted | 325 |
| Result rows extracted | 8,378 |
| Loaded into `dw.FactLabResult` | 8,037 |
| Skipped (panel headers) | 341 |
| Rejected | 0 |
| Warnings | 6 (2 implausible ages, 2 answer-before-visit, 1 missing answer date, 1 non-numeric) |
| Tests mapped to LOINC/SI | 13 (881 results); 1,403 legacy tests still `Unmapped` |

Run time: about 3 seconds. A second run is incremental and idempotent: it re-extracted only
visits above 230 (high-water mark 430 minus the 200-visit look-back) — 199 visits, 4,665 rows —
and inserted 0, updated 0, because `MERGE` only touches rows whose values changed. The same
numbers came out of the nightly SQL Agent job running as `NT Service\SQLSERVERAGENT`.

Persian payer names arrive intact (e.g. «بانک تجارت»): the legacy connection uses
`Auto Translate=False` and the sources read varchar as code page 1256, so the Data Conversion
gets real 1256 bytes instead of text already mangled into this machine's ANSI code page.

Problems met on the way, kept here because they are the usual SSIS ones:

| Symptom | Cause | Fix |
|---|---|---|
| `Text was truncated or one or more characters had no match in the target code page` | the driver translated 1256 text to the client's code page (1252) | `Auto Translate=False` + `AlwaysUseDefaultCodePage`/`DefaultCodePage=1256` on the sources |
| `Culture is not supported … 3072 (0x0c00)` only under SQL Agent | service accounts can have the custom default locale | package and tasks pinned to `LocaleID = 1033` |
| `The Parameter option can only be specified with the ISServer option` | `/Par` is catalog-only | settings as package variables, `/SET` |
| A failure inside *SQL Start run* left the run `Running` | the run id was never handed back | `etl.usp_FailRun @RunId = 0` closes the one Running run |

## Run it

```powershell
./etl/build.ps1        # regenerate the .dtsx after changing builder/BuildPackage.cs
./etl/run.ps1          # dtexec + print the run it recorded
```

```sql
SELECT * FROM LabFlow.etl.vw_RunHistory ORDER BY RunId DESC;    -- what each run did
SELECT * FROM LabFlow.etl.RejectRow WHERE RunId = <id>;          -- why rows were flagged
SELECT * FROM LabFlow.dw.vw_MappingCoverage;                      -- how much is LOINC-mapped
```

`dtexec` and SQL Agent need the **Integration Services** feature installed on the SQL Server
instance (Developer/Standard and up); without it the package only runs inside Visual Studio.
Schedule it daily at 20:00 (the host is a desktop PC that is off at night) with
`sqlcmd -S . -E -i etl/agent_job.sql -v PackagePath="<full path to LabFlowETL.dtsx>"`, which also
gives the Agent account only the rights it needs (read legacy, run `etl` procedures, load staging).

## Next

- [ ] Azure Data Factory pipeline: the same load into Azure SQL through a self-hosted
      integration runtime, de-identified data only
- [ ] Power BI report on `dw`: volumes, turnaround, abnormal/critical rates, data quality
