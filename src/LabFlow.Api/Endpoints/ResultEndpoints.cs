using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using LabFlow.Api.Auth;
using LabFlow.Api.Data;
using LabFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace LabFlow.Api.Endpoints;

/// <summary>
/// Resulting: the technologist enters each analyte's value (the flag comes from the reference range for the
/// patient's sex and age, which is copied onto the result), then releases the requisition. Release makes the
/// results final, issues a report version, and, for an order that came from ClinicHub, queues the results for it.
/// </summary>
public static class ResultEndpoints
{
    public record Analyte(int TestId, string Code, string Name, string ResultType, string? Unit, string? Value, string? Flag, string? Range, string? Status);
    public record ItemRow(int RequisitionItemId, string Code, string Name, string Status, Analyte[] Analytes);
    public record RequisitionRow(int RequisitionId, string AccessionNumber, string Patient, string Mrn, string Sex, int AgeYears, string Priority,
        string Status, DateTime ReceivedAt, string? ClinicalNotes, string Practitioner, string? FromClinic, int ReportVersion, ItemRow[] Items);
    public record EnteredValue(int RequisitionItemId, int TestId, string? Value);
    public record EnterResults(EnteredValue[] Results);

    public static void MapResultEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/lab").WithTags("Results").RequireAuthorization(Policies.ResultsEntry);

        // Requisitions to result (and the ones released today, for corrections).
        api.MapGet("/worklist", async (LabFlowDbContext db, CancellationToken ct) =>
        {
            var today = DateTime.UtcNow.Date;
            var ids = await db.Requisitions.AsNoTracking()
                .Where(r => r.Status != "Cancelled" && (r.Status != "Final" || r.UpdatedAt >= today))
                .OrderByDescending(r => r.Priority == "Stat").ThenBy(r => r.ReceivedAt).Select(r => r.RequisitionId).Take(100).ToListAsync(ct);
            var rows = new List<RequisitionRow>();
            foreach (var id in ids)
                rows.Add((await LoadAsync(db, id, ct))!);
            return rows;
        });

        api.MapGet("/requisitions/{id:int}", async (int id, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var row = await LoadAsync(db, id, ct);
            if (row is null)
                return Results.NotFound();
            audit.Log("View", "Results", id, await db.Requisitions.Where(r => r.RequisitionId == id).Select(r => r.PatientId).SingleAsync(ct));
            await db.SaveChangesAsync(ct);
            return Results.Ok(row);
        });

        api.MapPut("/requisitions/{id:int}/results", async (int id, EnterResults req, ClaimsPrincipal user, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var requisition = await db.Requisitions.Include(r => r.Items).ThenInclude(i => i.Test).ThenInclude(t => t.Members).ThenInclude(m => m.Member)
                .Include(r => r.Patient).SingleOrDefaultAsync(r => r.RequisitionId == id, ct);
            if (requisition is null)
                return Results.NotFound();
            if (requisition.Status == "Cancelled")
                return Results.Conflict(new { error = "This requisition was cancelled." });

            var errors = new Dictionary<string, string[]>();
            var today = SlotCalculator.TodayInToronto(DateTime.UtcNow);
            var ageDays = today.DayNumber - requisition.Patient.DateOfBirth.DayNumber;
            var now = DateTime.UtcNow;
            var existing = await db.Results.Where(r => requisition.Items.Select(i => i.RequisitionItemId).Contains(r.RequisitionItemId)).ToListAsync(ct);
            var ranges = await db.ReferenceRanges.AsNoTracking().Where(r => r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today)).ToListAsync(ct);

            foreach (var entry in req.Results ?? [])
            {
                var item = requisition.Items.SingleOrDefault(i => i.RequisitionItemId == entry.RequisitionItemId);
                var test = item is null ? null : Analytes(item.Test).SingleOrDefault(t => t.TestId == entry.TestId);
                if (test is null)
                {
                    errors[$"{entry.RequisitionItemId}:{entry.TestId}"] = ["Not a test on this requisition."];
                    continue;
                }
                var result = existing.SingleOrDefault(r => r.RequisitionItemId == item!.RequisitionItemId && r.TestId == test.TestId);
                if (string.IsNullOrWhiteSpace(entry.Value))
                {
                    if (result is not null && result.Status == "Preliminary")
                        db.Results.Remove(result);
                    continue;
                }
                decimal? number = null;
                if (test.ResultType == "Numeric")
                {
                    if (!decimal.TryParse(entry.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                    {
                        errors[$"{entry.RequisitionItemId}:{entry.TestId}"] = [$"{test.NameEn} needs a number."];
                        continue;
                    }
                    number = n;
                }
                // The most specific range for the patient's sex and age.
                var range = ranges.Where(r => r.TestId == test.TestId && (r.Sex == requisition.Patient.Sex || r.Sex == "U") && ageDays >= r.AgeFromDays && ageDays <= r.AgeToDays)
                    .OrderBy(r => r.Sex == "U").FirstOrDefault();
                if (result is null)
                    db.Results.Add(result = new LabResult { RequisitionItemId = item!.RequisitionItemId, TestId = test.TestId });
                else if (result.Status is "Final" or "Corrected")
                    result.VerifiedAt = null;   // a correction after release: verified again when re-released
                result.ValueNumeric = number;
                result.ValueText = number is null ? entry.Value.Trim() : null;
                result.UnitCode = test.UnitCode;
                result.RefLow = range?.Low;
                result.RefHigh = range?.High;
                result.RefText = range?.DisplayText ?? (range is { Low: { } lo, High: { } hi } ? $"{lo:0.###} - {hi:0.###}" : null);
                result.AbnormalFlag = number is { } v && range is not null ? Flag(v, range) : null;
                result.Status = "Preliminary";
                result.ResultedAt = now;
                result.ResultedByUserId = user.UserId();
            }
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            await db.SaveChangesAsync(ct);
            await UpdateStatusesAsync(db, requisition, ct);
            audit.Log("Update", "Results", id, requisition.PatientId, new { values = (req.Results ?? []).Length });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await LoadAsync(db, id, ct));
        });

        // Release: every ordered analyte has a value. A release after a correction issues an amended report.
        api.MapPost("/requisitions/{id:int}/release", async (int id, ClaimsPrincipal user, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var requisition = await db.Requisitions.Include(r => r.Items).ThenInclude(i => i.Test).ThenInclude(t => t.Members).ThenInclude(m => m.Member)
                .Include(r => r.Patient).SingleOrDefaultAsync(r => r.RequisitionId == id, ct);
            if (requisition is null)
                return Results.NotFound();
            var results = await db.Results.Where(r => requisition.Items.Select(i => i.RequisitionItemId).Contains(r.RequisitionItemId)).ToListAsync(ct);
            var missing = requisition.Items.Where(i => i.Status != "Cancelled")
                .SelectMany(i => Analytes(i.Test).Where(t => !results.Any(r => r.RequisitionItemId == i.RequisitionItemId && r.TestId == t.TestId)).Select(t => t.NameEn)).ToList();
            if (missing.Count > 0)
                return Results.Conflict(new { error = $"Enter every result before releasing: {string.Join(", ", missing)}." });
            var pending = results.Where(r => r.Status == "Preliminary").ToList();
            if (pending.Count == 0)
                return Results.Conflict(new { error = "Nothing new to release." });

            var now = DateTime.UtcNow;
            var version = (short)((await db.Reports.Where(r => r.RequisitionId == id).MaxAsync(r => (short?)r.Version, ct) ?? 0) + 1);
            foreach (var r in pending)
            {
                r.Status = version == 1 ? "Final" : "Corrected";
                r.VerifiedAt = now;
                r.VerifiedByUserId = user.UserId();
            }
            foreach (var item in requisition.Items.Where(i => i.Status != "Cancelled"))
                item.Status = "Verified";
            requisition.Status = "Final";
            requisition.UpdatedAt = now;
            db.Reports.Add(new LabReport { RequisitionId = id, Version = version, Status = version == 1 ? "Final" : "Amended", IssuedAt = now, IssuedByUserId = user.UserId() });

            // Ordered from ClinicHub: the results go back to it, one DiagnosticReport per ordered test.
            var order = await db.ElectronicOrders.Include(o => o.Items).SingleOrDefaultAsync(o => o.RequisitionId == id, ct);
            if (order is not null)
                db.OutboundMessages.Add(new OutboundMessage
                {
                    Destination = "ClinicHub", Kind = "DiagnosticReport", RequisitionId = id, CreatedAt = now,
                    PayloadJson = ResultBundle(requisition, order, results, version == 1 ? "final" : "amended", now).ToJsonString(),
                });
            audit.Log("Update", "Results", id, requisition.PatientId, new { released = version, toClinicHub = order is not null });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await LoadAsync(db, id, ct));
        });
    }

    /// <summary>What a test is resulted as: a panel's members, or the test itself.</summary>
    private static IEnumerable<Test> Analytes(Test test) =>
        test.Members.Count > 0 ? test.Members.OrderBy(m => m.SortOrder).Select(m => m.Member) : [test];

    /// <summary>HL7 table 0078 flag from the copied range.</summary>
    public static string Flag(decimal value, ReferenceRange range) =>
        range.CriticalLow is { } cl && value < cl ? "LL"
        : range.CriticalHigh is { } ch && value > ch ? "HH"
        : range.Low is { } lo && value < lo ? "L"
        : range.High is { } hi && value > hi ? "H"
        : "N";

    private static async Task UpdateStatusesAsync(LabFlowDbContext db, Requisition requisition, CancellationToken ct)
    {
        var results = await db.Results.AsNoTracking().Where(r => requisition.Items.Select(i => i.RequisitionItemId).Contains(r.RequisitionItemId)).ToListAsync(ct);
        foreach (var item in requisition.Items.Where(i => i.Status != "Cancelled"))
        {
            var count = results.Count(r => r.RequisitionItemId == item.RequisitionItemId);
            item.Status = count == 0 ? item.Status is "Ordered" or "Collected" ? item.Status : "InProgress"
                : count >= Analytes(item.Test).Count() ? "Resulted" : "InProgress";
        }
        // Values being entered or corrected: in progress until released (Final).
        if (results.Count > 0)
            requisition.Status = "InProgress";
        requisition.UpdatedAt = DateTime.UtcNow;
    }

    private static async Task<RequisitionRow?> LoadAsync(LabFlowDbContext db, int id, CancellationToken ct)
    {
        var r = await db.Requisitions.AsNoTracking().Include(x => x.Patient).Include(x => x.OrderingPractitioner)
            .Include(x => x.Items).ThenInclude(i => i.Test).ThenInclude(t => t.Members).ThenInclude(m => m.Member)
            .AsSplitQuery().SingleOrDefaultAsync(x => x.RequisitionId == id, ct);
        if (r is null)
            return null;
        var results = await db.Results.AsNoTracking().Where(x => r.Items.Select(i => i.RequisitionItemId).Contains(x.RequisitionItemId)).ToListAsync(ct);
        var version = await db.Reports.Where(x => x.RequisitionId == id).MaxAsync(x => (short?)x.Version, ct) ?? 0;
        var fromClinic = await db.ElectronicOrders.Where(o => o.RequisitionId == id).Select(o => o.SourceClinic).FirstOrDefaultAsync(ct);
        var today = SlotCalculator.TodayInToronto(DateTime.UtcNow);
        var age = today.Year - r.Patient.DateOfBirth.Year - (today < r.Patient.DateOfBirth.AddYears(today.Year - r.Patient.DateOfBirth.Year) ? 1 : 0);
        return new RequisitionRow(r.RequisitionId, r.AccessionNumber, $"{r.Patient.LastName}, {r.Patient.FirstName}", r.Patient.Mrn, r.Patient.Sex, age,
            r.Priority, r.Status, r.ReceivedAt, r.ClinicalNotes, $"Dr. {r.OrderingPractitioner.FirstName} {r.OrderingPractitioner.LastName}", fromClinic, version,
            r.Items.Where(i => i.Status != "Cancelled").OrderBy(i => i.RequisitionItemId).Select(i => new ItemRow(i.RequisitionItemId, i.Test.Code, i.Test.NameEn, i.Status,
                Analytes(i.Test).Select(t =>
                {
                    var res = results.SingleOrDefault(x => x.RequisitionItemId == i.RequisitionItemId && x.TestId == t.TestId);
                    return new Analyte(t.TestId, t.Code, t.NameEn, t.ResultType, t.UnitCode,
                        res is null ? null : res.ValueNumeric is { } n ? n.ToString("0.###", CultureInfo.InvariantCulture) : res.ValueText,
                        res?.AbnormalFlag, res?.RefText, res?.Status);
                }).ToArray())).ToArray());
    }

    /// <summary>
    /// The results for ClinicHub: one DiagnosticReport per ordered test, naming ClinicHub's order (basedOn), with an
    /// Observation per analyte (LOINC where known, value, flag, range).
    /// </summary>
    private static JsonObject ResultBundle(Requisition requisition, ElectronicOrder order, List<LabResult> results, string status, DateTime now)
    {
        var entries = new JsonArray();
        foreach (var ordered in order.Items)
        {
            var item = requisition.Items.SingleOrDefault(i => i.TestId == ordered.TestId);
            if (item is null)
                continue;
            var refs = new JsonArray();
            foreach (var t in Analytes(item.Test))
            {
                var r = results.SingleOrDefault(x => x.RequisitionItemId == item.RequisitionItemId && x.TestId == t.TestId);
                if (r is null)
                    continue;
                var url = $"urn:uuid:obs-{item.RequisitionItemId}-{t.TestId}";
                refs.Add(new JsonObject { ["reference"] = url });
                var coding = new JsonArray(new JsonObject { ["system"] = OrderEndpoints.LabFlowTestCode, ["code"] = t.Code, ["display"] = t.NameEn });
                if (t.LoincCode is { } loinc)
                    coding.Insert(0, new JsonObject { ["system"] = OrderEndpoints.Loinc, ["code"] = loinc, ["display"] = t.NameEn });
                entries.Add(new JsonObject
                {
                    ["fullUrl"] = url,
                    ["resource"] = new JsonObject
                    {
                        ["resourceType"] = "Observation", ["status"] = r.Status == "Corrected" ? "corrected" : "final",
                        ["code"] = new JsonObject { ["coding"] = coding, ["text"] = t.NameEn },
                        ["valueQuantity"] = r.ValueNumeric is { } v ? new JsonObject { ["value"] = v, ["unit"] = r.UnitCode, ["system"] = "http://unitsofmeasure.org", ["code"] = r.UnitCode } : null,
                        ["valueString"] = r.ValueNumeric is null ? r.ValueText : null,
                        ["interpretation"] = r.AbnormalFlag is { } f
                            ? new JsonArray(new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation", ["code"] = f }) })
                            : null,
                        ["referenceRange"] = r.RefText is { } range ? new JsonArray(new JsonObject { ["text"] = range }) : null,
                    },
                    ["request"] = new JsonObject { ["method"] = "POST", ["url"] = "Observation" },
                });
            }
            entries.Insert(0, new JsonObject
            {
                ["fullUrl"] = $"urn:uuid:report-{item.RequisitionItemId}",
                ["resource"] = new JsonObject
                {
                    ["resourceType"] = "DiagnosticReport", ["status"] = status,
                    ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:labflow:accession", ["value"] = $"{requisition.AccessionNumber}-{item.Test.Code}" }),
                    ["basedOn"] = new JsonArray(new JsonObject
                    {
                        ["identifier"] = new JsonObject { ["system"] = $"urn:clinichub:{order.SourceClinic}:service-request", ["value"] = ordered.ExternalId },
                    }),
                    ["code"] = new JsonObject { ["text"] = item.Test.NameEn },
                    ["subject"] = new JsonObject { ["display"] = $"{requisition.Patient.LastName}, {requisition.Patient.FirstName}" },
                    ["issued"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["result"] = refs,
                },
                ["request"] = new JsonObject { ["method"] = "POST", ["url"] = "DiagnosticReport" },
            });
        }
        return new JsonObject { ["resourceType"] = "Bundle", ["type"] = "transaction", ["entry"] = entries };
    }
}
