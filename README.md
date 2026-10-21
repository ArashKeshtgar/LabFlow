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
- [x] Resulting screen: result entry, flags from reference ranges, release and corrections (report versions)
- [ ] Specimen collection and verification screens
- [ ] Patient portal and PDF report
- [x] ETL from the legacy `Laboratory` database: SSIS package → de-identified staging → `dw` star schema (`etl/`)
- [ ] Azure Data Factory version of the ETL, Power BI report on `dw`
- [ ] Azure Functions (notifications, AI interpretation)
- [ ] HL7 v2 ORU export (OLIS simulation)
- [x] The lab's money to FinancePowerApp (the group's accounting): patient invoices, online payments, voids and
      refunds; insured work once `ref.Test.OhipFee` holds the Schedule of Benefits amounts (the demo has fee codes only).
      Set `ClinicHub:FinanceUrl`; LabFlow gets a ClinicHub token with the scope `finance`

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

Not connected to ClinicHub, sign-in is a development-only stub: pick a seeded user from the "Demo user" menu in
the top bar (the UI sends it as `X-Demo-User`). It is not registered outside the Development environment.

### Connected to ClinicHub

LabFlow can be one of the applications of [ClinicHub](https://github.com/ArashKeshtgar/ClinicHub), a clinic
platform for Ontario. With a `ClinicHub` section configured, staff sign in through ClinicHub (OpenID Connect, code
flow with PKCE) instead of the development menu, and ClinicHub's permissions decide what they may do here: a
clinic role holding `labflow:frontdesk` opens the reception (`frontdesk`), `labflow:admin` everything. Each
ClinicHub person becomes a `sec.AppUser` (`ExternalId = clinichub:{id}`), so the PHIPA access log names them.
Every five minutes the session is re-checked with ClinicHub; roles taken away or "sign out everywhere" end it.
Signing out here signs out of ClinicHub too.

Where the data lives is a setting:

| `ClinicHub:DatabasePattern` | Database |
|---|---|
| empty (default) | **One lab** (`ConnectionStrings:LabFlow`) for staff of every clinic: the usual Ontario arrangement, a lab as its own business receiving orders from many clinics. |
| contains `{clinic}`, e.g. `Server=.;Database=LabFlow_{clinic};…` | **One database per clinic**, built from `database/*.sql` (schema, sequences, reference catalog) at the clinic's first sign-in. Public booking then names the clinic: `/?clinic=harbourview`. |

```powershell
dotnet run --project src/LabFlow.Api --launch-profile connected              # one lab database
dotnet run --project src/LabFlow.Api --launch-profile connected-per-clinic   # a database per clinic
dotnet run --project src/LabFlow.Api --launch-profile via-hub                # connected, through IntegrationHub
```

`via-hub` is `connected` with released results (`ClinicHub:ResultsUrl`) and the accounting (`ClinicHub:FinanceUrl`)
sent to IntegrationHub's routes on :5700; orders come through it when ClinicHub runs its own `via-hub` profile. To
switch back, run `connected` again.

**Electronic orders.** Tests ordered at a ClinicHub visit arrive at `/fhir` as a FHIR R4 transaction Bundle (Patient,
Practitioner, ServiceRequests coded with LOINC or LabFlow's own panel codes), authenticated with a token ClinicHub signs
for LabFlow. They wait as electronic orders (`database/050_electronic_orders.sql`) until the patient comes to
collection; reception's *Patient is here: make requisition* turns one into a requisition with the usual checks. The
patient is matched by health card or name and birth date and registered if new; the physician by billing or CPSO
number. Scripts from 050 on are safe to re-run and are applied to existing databases at start-up.

**Results back.** Releasing the results of a requisition made from an electronic order sends them to ClinicHub: a
FHIR DiagnosticReport per ordered test with an Observation per analyte (LOINC, value, unit, range, H/L), `final`
first and `amended` for a correction. Messages wait in `lab.OutboundMessage` (`database/051_outbound_messages.sql`)
until delivered; LabFlow authenticates with its own client id and secret (client credentials, scope `results`).
They go to ClinicHub's own `/fhir` unless `ClinicHub:ResultsUrl` names another address, such as IntegrationHub's
`http://localhost:5700/route/clinichub/fhir` (the hub forwards LabFlow's ClinicHub token unchanged).

**Cancellation.** ClinicHub withdraws a test by sending its ServiceRequest again with status `revoked`. A waiting
order loses the test (`database/052_order_cancellation.sql` keeps it, marked cancelled, with the reason); on a
requisition the item is cancelled if no result has been entered. Otherwise LabFlow answers `409` and the clinic is
told to call the lab.

A new clinic database has no collection sites until the clinic adds one (`core.Location` and its hours).
Database tests (`ConnectedTests`) build throw-away databases and run only where a SQL Server is reachable
(`LABFLOW_TEST_SQL`, or the local default instance); without one they are skipped.

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
