# LabFlow

A lab information system for an Ontario community lab, built as a portfolio project:
booking → reception → collection → results → online report → email notification → AI-assisted
interpretation reviewed by a clinician.

Stack: ASP.NET Core 10 minimal API + EF Core, React 19 + Vite + TypeScript, SQL Server; Azure Functions planned
for background work (email outbox, AI interpretation, PDF rendering).

## Status

- [x] Database schema, reference catalog and synthetic demo data (`database/`)
- [x] API: booking, slots, reception day sheet, check-in, patient lookup, requisition entry (`src/LabFlow.Api`)
- [x] React front end: patient booking and front desk (`web/`)
- [ ] Specimen collection, resulting and verification screens
- [ ] Patient portal and PDF report
- [x] ETL from the legacy `Laboratory` database: SSIS package → de-identified staging → `dw` star schema (`etl/`)
- [ ] Azure Data Factory version of the ETL, Power BI report on `dw`
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

## Run the app

```powershell
dotnet run --project src/LabFlow.Api          # API on http://localhost:5180
cd web; npm install; npm run dev              # UI on http://localhost:5174
dotnet test                                   # unit tests
```

Sign-in is a development-only stub: pick a seeded user from the "Demo user" menu in the top bar
(the UI sends it as `X-Demo-User`). It is not registered outside the Development environment.

What the API enforces:

- Slots come from location hours, closures and capacity, in Toronto time (DST-safe); a per-slot
  application lock stops two people taking the last place.
- Online booking with an existing health card must match date of birth and last name, and the
  confirmation goes to the email on file - never to an address typed by an anonymous caller.
- Health card numbers are checked with the Luhn check digit (API and UI).
- Requisitions reject duplicate panel members, sex-specific tests for the wrong sex, and tests with no
  self-pay price for patients without an Ontario health card. Uninsured tests create an invoice.
- Every read or write of patient data writes a row to `audit.AccessLog` in the same transaction.

See [docs/data-model.md](docs/data-model.md) for the model and the design decisions.

All people, numbers and addresses in the demo data are fictional.
