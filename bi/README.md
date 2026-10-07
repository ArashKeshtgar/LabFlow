# LabFlow Power BI report

A Power BI project (PBIP) over the de-identified warehouse in Azure SQL
(`LabFlowDW`). The Azure Data Factory pipeline in `etl/adf` keeps that
database in step with the on-prem `dw` schema that the SSIS package loads.

```
legacy Laboratory DB --SSIS--> LabFlow.dw (on-prem) --ADF--> LabFlowDW (Azure SQL) --> Power BI
```

## Files

- `build_report.py` generates everything below from code. Rerunning it overwrites the generated folders.
- `LabFlow.SemanticModel/` holds the model in TMDL. It has 8 import tables (Results, Test, Payer, Patient, Date, Load Runs, Data Quality Issues, Copy Runs), relationships, 16 DAX measures, and a calculated turnaround band.
- `LabFlow.Report/` holds the report in PBIR, plus a small custom theme.
- `validate_report.py` checks every generated JSON file against the Microsoft schema named in its `$schema`.

## Pages

| Page | Shows |
|---|---|
| Overview | results, visits, patients, abnormal %, critical results, average turnaround; results by date and age band; payer mix; abnormal % by sex |
| Turnaround | turnaround bands, average turnaround by visit date, turnaround by test |
| Abnormal results | abnormal and critical counts and rates by test (with LOINC code), by age band and sex, and by payer type |
| Data quality | SSIS load runs, data-quality rules hit, ADF copy runs, LOINC mapping coverage |

Screenshots: `docs/powerbi/`.

## Open it

1. Run `python bi/build_report.py`, then `python bi/validate_report.py`.
2. Power BI Desktop: Options > Preview features, tick "Store semantic model
   using TMDL format" and "Store reports using enhanced metadata format
   (PBIR)", then restart.
3. Open `bi/LabFlow.pbip`, Apply changes, Refresh. Sign in with a Microsoft
   (Entra) account that has access to `LabFlowDW`. The server takes Entra
   authentication only, and the firewall must allow your IP.

All data is de-identified: patient keys are hashed, dates are shifted, and the
model contains no names, contacts, IDs or birth dates.
