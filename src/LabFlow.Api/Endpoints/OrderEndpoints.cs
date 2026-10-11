using System.Security.Claims;
using System.Text.Json.Nodes;
using LabFlow.Api.Auth;
using LabFlow.Api.Data;
using LabFlow.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LabFlow.Api.Endpoints;

/// <summary>
/// Electronic orders from ClinicHub (connected mode). ClinicHub posts a FHIR R4 transaction Bundle (the patient,
/// the ordering physician, one ServiceRequest per test) to /fhir. The order waits until the patient comes to
/// collection, where reception turns it into a requisition.
/// </summary>
public static class OrderEndpoints
{
    public const string Loinc = "http://loinc.org";
    public const string LabFlowTestCode = "urn:labflow:test-code";
    public const string OntarioHealthCard = "https://fhir.infoway-inforoute.ca/NamingSystem/ca-on-patient-hcn";
    public const string Cpso = "https://fhir.infoway-inforoute.ca/NamingSystem/ca-on-license-physician";
    public const string OhipBillingNumber = "urn:clinichub:ohip-billing-number";

    public record ElectronicOrderRow(int ElectronicOrderId, string SourceClinic, int PatientId, string Patient, string Mrn, DateOnly DateOfBirth,
        string Practitioner, string Priority, string? ClinicalNotes, DateTime ReceivedAt, string[] Tests, string Status, int? RequisitionId);
    public record RequisitionFromOrder(short LocationId);

    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.IsConnected())
            return;

        app.MapPost("/fhir", async (HttpRequest request, ClaimsPrincipal hub, LabDatabases databases, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var slug = hub.FindFirstValue("clinic_slug")!;
            // The first order for a clinic can come before any of its staff has signed in here.
            await databases.PrepareAsync(slug);

            JsonNode? bundle;
            try { bundle = await JsonNode.ParseAsync(request.Body, cancellationToken: ct); }
            catch (System.Text.Json.JsonException) { return Outcome("The body is not JSON."); }
            if ((string?)bundle?["resourceType"] != "Bundle" || (string?)bundle["type"] != "transaction")
                return Outcome("Expected a FHIR transaction Bundle.");
            var entries = bundle["entry"]?.AsArray().Select(e => e?["resource"]).ToList() ?? [];
            var patient = entries.FirstOrDefault(r => (string?)r?["resourceType"] == "Patient");
            var practitioner = entries.FirstOrDefault(r => (string?)r?["resourceType"] == "Practitioner");
            var requests = entries.Where(r => (string?)r?["resourceType"] == "ServiceRequest").ToList();
            var requisition = (string?)bundle["identifier"]?["value"];
            if (patient is null || practitioner is null || requests.Count == 0 || string.IsNullOrWhiteSpace(requisition))
                return Outcome("The Bundle needs an identifier, a Patient, a Practitioner and at least one ServiceRequest.");

            // Tests: LOINC where the catalog has it, LabFlow's own code for panels.
            var tests = await db.Tests.AsNoTracking().Where(t => t.IsActive && t.IsOrderable).ToListAsync(ct);
            var wanted = new List<(Test Test, string ExternalId)>();
            foreach (var sr in requests)
            {
                var coding = sr!["code"]?["coding"]?.AsArray().Select(c => c!).ToList() ?? [];
                var test = coding.Select(c => (string?)c["system"] switch
                {
                    Loinc => tests.FirstOrDefault(t => t.LoincCode == (string?)c["code"]),
                    LabFlowTestCode => tests.FirstOrDefault(t => t.Code == (string?)c["code"]),
                    _ => null,
                }).FirstOrDefault(t => t is not null);
                if (test is null)
                    return Outcome($"LabFlow can't perform '{(string?)coding.FirstOrDefault()?["display"] ?? (string?)coding.FirstOrDefault()?["code"] ?? "?"}'. Codes use {Loinc} or {LabFlowTestCode}.");
                wanted.Add((test, (string?)sr["identifier"]?[0]?["value"] ?? ""));
            }

            var existing = await db.ElectronicOrders.AsNoTracking().SingleOrDefaultAsync(o => o.SourceClinic == slug && o.ExternalId == requisition, ct);
            int patientId, practitionerId, orderId;
            if (existing is not null)
                // Sent again (ClinicHub retried after a lost answer): accepted once.
                (patientId, practitionerId, orderId) = (existing.PatientId, existing.PractitionerId, existing.ElectronicOrderId);
            else
            {
                if (await MatchPatientAsync(db, patient, ct) is not { } local)
                    return Outcome("The Patient needs a family name, a given name, a birth date and a gender.");
                var doctor = await MatchPractitionerAsync(db, practitioner, hub.FindFirstValue("clinic_name"), ct);
                if (doctor is null)
                    return Outcome("The Practitioner needs a CPSO or OHIP billing number and a name.");
                var order = new ElectronicOrder
                {
                    SourceClinic = slug, ExternalId = requisition, Patient = local, Practitioner = doctor,
                    Priority = requests.Any(r => (string?)r?["priority"] == "stat") ? "Stat" : "Routine",
                    ClinicalNotes = string.Join("; ", requests.SelectMany(r => r?["note"]?.AsArray() ?? []).Select(n => (string?)n?["text"])
                        .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct()) is { Length: > 0 } notes ? notes[..Math.Min(notes.Length, 1000)] : null,
                    ReceivedAt = DateTime.UtcNow,
                    Items = wanted.DistinctBy(w => w.Test.TestId).Select(w => new ElectronicOrderItem { TestId = w.Test.TestId, ExternalId = w.ExternalId }).ToList(),
                };
                db.ElectronicOrders.Add(order);
                await db.SaveChangesAsync(ct);
                audit.Log("Create", "ElectronicOrder", order.ElectronicOrderId, local.PatientId, new { from = slug, requisition, tests = wanted.Select(w => w.Test.Code) });
                await db.SaveChangesAsync(ct);
                (patientId, practitionerId, orderId) = (local.PatientId, doctor.PractitionerId, order.ElectronicOrderId);
            }

            // transaction-response: one entry per request entry, in order.
            var response = new JsonArray();
            foreach (var r in entries)
                response.Add(new JsonObject
                {
                    ["response"] = (string?)r?["resourceType"] switch
                    {
                        "Patient" => Created($"Patient/{patientId}"),
                        "Practitioner" => Created($"Practitioner/{practitionerId}"),
                        "ServiceRequest" => Created($"ServiceRequest/{orderId}"),
                        _ => new JsonObject { ["status"] = "200 OK" },
                    },
                });
            return Results.Text(new JsonObject { ["resourceType"] = "Bundle", ["type"] = "transaction-response", ["entry"] = response }.ToJsonString(),
                "application/fhir+json");
        }).RequireAuthorization(ClinicHubConnection.OrdersPolicy).ExcludeFromDescription();

        var api = app.MapGroup("/api/reception/electronic-orders").WithTags("Reception").RequireAuthorization(Policies.FrontDesk);

        api.MapGet("/", async (int? patientId, LabFlowDbContext db, CancellationToken ct) =>
            await db.ElectronicOrders.AsNoTracking()
                .Where(o => o.Status == "Waiting" && (patientId == null || o.PatientId == patientId))
                .OrderByDescending(o => o.Priority == "Stat").ThenBy(o => o.ReceivedAt)
                .Select(o => new ElectronicOrderRow(o.ElectronicOrderId, o.SourceClinic, o.PatientId, o.Patient.LastName + ", " + o.Patient.FirstName,
                    o.Patient.Mrn, o.Patient.DateOfBirth, "Dr. " + o.Practitioner.FirstName + " " + o.Practitioner.LastName, o.Priority, o.ClinicalNotes,
                    o.ReceivedAt, o.Items.Select(i => i.Test.NameEn).ToArray(), o.Status, o.RequisitionId))
                .ToListAsync(ct));

        // The patient is at the collection desk: the order becomes a requisition, with the same checks as one typed at reception.
        api.MapPost("/{id:int}/requisition", async (int id, RequisitionFromOrder req, ClaimsPrincipal user, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var order = await db.ElectronicOrders.Include(o => o.Items).SingleOrDefaultAsync(o => o.ElectronicOrderId == id, ct);
            if (order is null)
                return Results.NotFound();
            if (order.Status != "Waiting")
                return Results.Conflict(new { error = "This order has already been made into a requisition." });

            var created = await ReceptionEndpoints.CreateRequisitionAsync(new ReceptionEndpoints.CreateRequisitionRequest(
                order.PatientId, order.PractitionerId, req.LocationId, null, SlotCalculator.TodayInToronto(DateTime.UtcNow), order.Priority,
                order.ClinicalNotes, false, null, null, order.Items.Select(i => i.TestId).ToArray()), user, db, audit, ct);
            if (created is not Created<ReceptionEndpoints.RequisitionDto> ok)
                return created;
            order.Status = "Requisitioned";
            order.RequisitionId = ok.Value!.RequisitionId;
            await db.SaveChangesAsync(ct);
            return created;
        });
    }

    /// <summary>By Ontario health card, then by name and date of birth; otherwise registered from the order.</summary>
    private static async Task<Patient?> MatchPatientAsync(LabFlowDbContext db, JsonNode p, CancellationToken ct)
    {
        var card = p["identifier"]?.AsArray().FirstOrDefault(i => (string?)i?["system"] == OntarioHealthCard);
        var hcn = HealthCard.Normalize((string?)card?["value"] ?? "");
        var family = ((string?)p["name"]?[0]?["family"])?.Trim();
        var given = ((string?)p["name"]?[0]?["given"]?[0])?.Trim();
        if (string.IsNullOrEmpty(family) || string.IsNullOrEmpty(given) || !DateOnly.TryParse((string?)p["birthDate"], out var dob))
            return null;
        if (hcn.Length == 10 && await db.Patients.SingleOrDefaultAsync(x => x.HealthCardNumber == hcn && x.IsActive, ct) is { } byCard)
            return byCard;
        if (await db.Patients.FirstOrDefaultAsync(x => x.LastName == family && x.FirstName == given && x.DateOfBirth == dob && x.IsActive, ct) is { } byName)
            return byName;

        var version = (string?)card?["extension"]?.AsArray().FirstOrDefault(e => ((string?)e?["url"])?.EndsWith("health-card-version") == true)?["valueString"];
        var sex = (string?)p["gender"] switch { "male" => "M", "female" => "F", "other" => "X", _ => "U" };
        var patient = await BookingEndpoints.NewPatientAsync(db, new PatientDetails(given, family, dob, sex,
            hcn.Length == 10 ? hcn : null, version, null, null, "en", false), ct);
        db.Patients.Add(patient);
        return patient;
    }

    /// <summary>By OHIP billing number or CPSO number; otherwise added to the lab's list of ordering physicians.</summary>
    private static async Task<Practitioner?> MatchPractitionerAsync(LabFlowDbContext db, JsonNode p, string? clinicName, CancellationToken ct)
    {
        string? Id(string system) => (string?)p["identifier"]?.AsArray().FirstOrDefault(i => (string?)i?["system"] == system)?["value"];
        var billing = Id(OhipBillingNumber);
        var cpso = Id(Cpso);
        var family = (string?)p["name"]?[0]?["family"];
        if ((billing is null && cpso is null) || string.IsNullOrWhiteSpace(family))
            return null;
        var found = await db.Practitioners.FirstOrDefaultAsync(x => (billing != null && x.OhipBillingNumber == billing) || (cpso != null && x.LicenceNumber == cpso), ct);
        if (found is not null)
            return found;
        var added = new Practitioner
        {
            // Nurse practitioners have no billing number and register with the CNO; ClinicHub sends CPSO numbers for physicians.
            LicenceNumber = cpso ?? $"OHIP{billing}"[..Math.Min(10, 4 + billing!.Length)], LicenceBody = "CPSO", OhipBillingNumber = billing,
            FirstName = (string?)p["name"]?[0]?["given"]?[0] ?? "", LastName = family, ClinicName = clinicName, IsActive = true,
        };
        db.Practitioners.Add(added);
        return added;
    }

    private static JsonObject Created(string location) => new() { ["status"] = "201 Created", ["location"] = location };

    private static IResult Outcome(string diagnostics) => Results.Text(new JsonObject
    {
        ["resourceType"] = "OperationOutcome",
        ["issue"] = new JsonArray(new JsonObject { ["severity"] = "error", ["code"] = "invalid", ["diagnostics"] = diagnostics }),
    }.ToJsonString(), "application/fhir+json", statusCode: StatusCodes.Status400BadRequest);
}
