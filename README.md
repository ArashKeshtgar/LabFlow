# LabFlow

A lab information system for an Ontario community lab, built as a portfolio project:
booking → reception → collection → results → online report → email notification → AI-assisted
interpretation reviewed by a clinician.

Planned stack: ASP.NET Core (API + MVC), a React or Angular front end, SQL Server, and Azure Functions
for background work (email outbox, AI interpretation, PDF rendering).

## Status

- [x] Database schema, reference catalog and synthetic demo data (`database/`)
- [ ] ETL from the legacy `Laboratory` database
- [ ] API and front end
- [ ] Azure Functions (notifications, AI interpretation)
- [ ] HL7 v2 ORU export (OLIS simulation)

## Build the database

Requires a local SQL Server and `sqlcmd`.

```powershell
./database/deploy.ps1 -Recreate          # drop, create, schema, reference data, demo data
./database/deploy.ps1 -Recreate -NoDemo  # without synthetic patients
```

Then look at the demo patient's results:

```sql
SELECT OrderedTest, TestName, ValueNumeric, UnitDisplay, RefText, AbnormalFlag
FROM LabFlow.lab.vw_ResultDetail
ORDER BY DepartmentCode, OrderedTest, SortOrder;
```

See [docs/data-model.md](docs/data-model.md) for the model and the design decisions.

All people, numbers and addresses in the demo data are fictional.
