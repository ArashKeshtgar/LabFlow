using System.Security.Claims;
using LabFlow.Api.Auth;
using LabFlow.Api.Data;
using LabFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace LabFlow.Api.Endpoints;

/// <summary>Front-desk endpoints: day sheet, check-in, patient lookup and requisition entry.</summary>
public static class ReceptionEndpoints
{
    public record AppointmentRow(
        int AppointmentId, DateTime StartUtc, string LocalTime, string Status,
        int PatientId, string PatientName, string Mrn, DateOnly DateOfBirth, bool HasHealthCard,
        int? RequisitionId, string? AccessionNumber);

    public record PatientSummary(
        int PatientId, string Mrn, string FirstName, string LastName, DateOnly DateOfBirth, string Sex,
        string? HealthCardNumber, string? HealthCardVersion, string? Email, string? MobilePhone);

    public record PractitionerDto(int PractitionerId, string Name, string LicenceNumber, string? OhipBillingNumber, string? ClinicName, string? City);

    public record CancelRequest(string Reason);

    public record CreateRequisitionRequest(
        int PatientId, int OrderingPractitionerId, short LocationId, int? AppointmentId,
        DateOnly RequisitionDate, string Priority, string? ClinicalNotes,
        bool IsPregnant, byte? PregnancyWeek, byte? FastingHours, int[] TestIds);

    public record RequisitionItemDto(int TestId, string Code, string Name, bool IsInsured, decimal? Price, bool FastingRequired);

    public record RequisitionDto(
        int RequisitionId, string AccessionNumber, int PatientId, string PatientName, string Mrn,
        string Practitioner, string PayerType, string Priority, string Status, DateTime ReceivedAt,
        RequisitionItemDto[] Items, string[] Tubes, decimal PatientPays, int? InvoiceId);

    public static void MapReceptionEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/reception").WithTags("Reception").RequireAuthorization(Policies.FrontDesk);

        api.MapGet("/appointments", async (short locationId, DateOnly date, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var (fromUtc, toUtc) = SlotCalculator.DayRangeUtc(date);
            var rows = await db.Appointments.AsNoTracking()
                .Where(a => a.LocationId == locationId && a.StartUtc >= fromUtc && a.StartUtc < toUtc)
                .OrderBy(a => a.StartUtc).ThenBy(a => a.Patient.LastName)
                .Select(a => new
                {
                    a.AppointmentId, a.StartUtc, a.Status, a.PatientId,
                    a.Patient.FirstName, a.Patient.LastName, a.Patient.Mrn, a.Patient.DateOfBirth,
                    HasHealthCard = a.Patient.HealthCardNumber != null,
                    RequisitionId = (int?)a.Requisition!.RequisitionId,
                    AccessionNumber = a.Requisition!.AccessionNumber,
                })
                .ToListAsync(ct);

            audit.Log("View", "AppointmentDaySheet", $"{locationId}:{date:yyyy-MM-dd}", null, new { count = rows.Count });
            await db.SaveChangesAsync(ct);

            return rows.Select(r => new AppointmentRow(
                r.AppointmentId, r.StartUtc, SlotCalculator.ToToronto(r.StartUtc).ToString("HH:mm"), r.Status,
                r.PatientId, $"{r.LastName}, {r.FirstName}", r.Mrn, r.DateOfBirth, r.HasHealthCard,
                r.RequisitionId, r.AccessionNumber));
        });

        api.MapPost("/appointments/{id:int}/check-in", (int id, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
            ChangeStatusAsync(id, AppointmentStatus.Booked, AppointmentStatus.CheckedIn, null, db, audit, ct));

        api.MapPost("/appointments/{id:int}/no-show", (int id, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
            ChangeStatusAsync(id, AppointmentStatus.Booked, AppointmentStatus.NoShow, null, db, audit, ct));

        api.MapPost("/appointments/{id:int}/cancel", (int id, CancelRequest req, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(req.Reason)
                ? Task.FromResult(new Validator().Add("reason", "A reason is required.").Problem())
                : ChangeStatusAsync(id, AppointmentStatus.Booked, AppointmentStatus.Cancelled, req.Reason.Trim(), db, audit, ct));

        api.MapGet("/patients", async (string q, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            q = q.Trim();
            if (q.Length < 2) return Results.Ok(Array.Empty<PatientSummary>());

            var hcn = HealthCard.Normalize(q);
            var query = db.Patients.AsNoTracking().Where(p => p.IsActive);
            query = hcn.Length == 10 && hcn.All(char.IsAsciiDigit)
                ? query.Where(p => p.HealthCardNumber == hcn)
                : q.StartsWith("LF", StringComparison.OrdinalIgnoreCase)
                    ? query.Where(p => p.Mrn == q.ToUpperInvariant())
                    : NameSearch(query, q);

            var found = await query.OrderBy(p => p.LastName).ThenBy(p => p.FirstName).Take(20)
                .Select(p => ToSummary(p)).ToListAsync(ct);

            audit.Log("View", "PatientSearch", null, found.Count == 1 ? found[0].PatientId : null, new { results = found.Count });
            await db.SaveChangesAsync(ct);
            return Results.Ok(found);
        });

        api.MapGet("/patients/{id:int}", async (int id, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var p = await db.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.PatientId == id, ct);
            if (p is null) return Results.NotFound();
            audit.Log("View", "Patient", id, id);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToSummary(p));
        });

        api.MapPost("/patients", async (PatientDetails d, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var v = new Validator();
            d.Validate(v, SlotCalculator.TodayInToronto(DateTime.UtcNow), prefix: "");
            if (!v.IsValid) return v.Problem();

            if (d.NormalizedHealthCard is { } hcn && await db.Patients.AnyAsync(p => p.HealthCardNumber == hcn, ct))
                return Results.Conflict(new { error = "A patient with this health card number already exists." });

            var patient = await BookingEndpoints.NewPatientAsync(db, d, ct);
            db.Patients.Add(patient);
            await db.SaveChangesAsync(ct);
            audit.Log("Create", "Patient", patient.PatientId, patient.PatientId, new { channel = "WalkIn" });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/reception/patients/{patient.PatientId}", ToSummary(patient));
        });

        api.MapGet("/practitioners", async (string q, LabFlowDbContext db, CancellationToken ct) =>
        {
            q = q.Trim();
            if (q.Length < 2) return Results.Ok(Array.Empty<PractitionerDto>());
            return Results.Ok(await db.Practitioners.AsNoTracking()
                .Where(p => p.IsActive && (p.LastName.StartsWith(q) || p.FirstName.StartsWith(q)
                                           || p.LicenceNumber == q || p.OhipBillingNumber == q))
                .OrderBy(p => p.LastName).Take(20)
                .Select(p => new PractitionerDto(p.PractitionerId, "Dr. " + p.FirstName + " " + p.LastName,
                    p.LicenceNumber, p.OhipBillingNumber, p.ClinicName, p.City))
                .ToListAsync(ct));
        });

        api.MapPost("/requisitions", CreateRequisitionAsync);

        api.MapGet("/requisitions/{id:int}", async (int id, LabFlowDbContext db, AuditLogger audit, CancellationToken ct) =>
        {
            var dto = await LoadRequisitionAsync(db, id, ct);
            if (dto is null) return Results.NotFound();
            audit.Log("View", "Requisition", id, dto.PatientId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(dto);
        });
    }

    private static IQueryable<Patient> NameSearch(IQueryable<Patient> query, string q)
    {
        // "Last, First" or "Last First" or just "Last"
        var parts = q.Split([',', ' '], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = parts[0];
        query = query.Where(p => p.LastName.StartsWith(last));
        if (parts.Length > 1)
        {
            var first = parts[1];
            query = query.Where(p => p.FirstName.StartsWith(first));
        }
        return query;
    }

    private static PatientSummary ToSummary(Patient p) => new(
        p.PatientId, p.Mrn, p.FirstName, p.LastName, p.DateOfBirth, p.Sex,
        p.HealthCardNumber, p.HealthCardVersion, p.Email, p.MobilePhone);

    private static async Task<IResult> ChangeStatusAsync(
        int id, string from, string to, string? reason, LabFlowDbContext db, AuditLogger audit, CancellationToken ct)
    {
        var appt = await db.Appointments.SingleOrDefaultAsync(a => a.AppointmentId == id, ct);
        if (appt is null) return Results.NotFound();
        if (appt.Status != from)
            return Results.Conflict(new { error = $"Appointment is {appt.Status}; expected {from}." });

        appt.Status = to;
        if (reason is not null) appt.CancelReason = reason;
        audit.Log("Update", "Appointment", id, appt.PatientId, new { status = to });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateRequisitionAsync(
        CreateRequisitionRequest req, ClaimsPrincipal user, LabFlowDbContext db, AuditLogger audit, CancellationToken ct)
    {
        var v = new Validator();
        var today = SlotCalculator.TodayInToronto(DateTime.UtcNow);

        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.PatientId == req.PatientId && p.IsActive, ct);
        v.Require(patient is not null, "patientId", "Patient not found.");
        v.Require(await db.Practitioners.AnyAsync(p => p.PractitionerId == req.OrderingPractitionerId && p.IsActive, ct),
            "orderingPractitionerId", "Ordering practitioner not found.");
        v.Require(await db.Locations.AnyAsync(l => l.LocationId == req.LocationId && l.IsActive, ct), "locationId", "Location not found.");
        v.Require(req.RequisitionDate <= today && req.RequisitionDate >= today.AddYears(-1),
            "requisitionDate", "Requisition date must be within the last 12 months and not in the future.");
        v.Require(req.Priority is "Routine" or "Stat", "priority", "Priority must be Routine or Stat.");
        v.Require(req.ClinicalNotes is null || req.ClinicalNotes.Length <= 1000, "clinicalNotes", "Clinical notes are limited to 1000 characters.");
        v.Require(req.IsPregnant || req.PregnancyWeek is null, "pregnancyWeek", "Pregnancy week is only valid when pregnant.");
        v.Require(req.PregnancyWeek is null or (>= 1 and <= 45), "pregnancyWeek", "Pregnancy week must be 1-45.");
        v.Require(req.FastingHours is null or <= 72, "fastingHours", "Fasting hours must be 0-72.");
        v.Require(req.TestIds is { Length: > 0 }, "testIds", "Select at least one test.");
        if (patient is not null)
            v.Require(!req.IsPregnant || patient.Sex is "F" or "X" or "U", "isPregnant", "Pregnancy cannot be recorded for this patient.");

        Appointment? appt = null;
        if (req.AppointmentId is int apptId)
        {
            appt = await db.Appointments.Include(a => a.Requisition).SingleOrDefaultAsync(a => a.AppointmentId == apptId, ct);
            v.Require(appt is not null && appt.PatientId == req.PatientId, "appointmentId", "Appointment not found for this patient.");
            v.Require(appt is null || appt.Status is AppointmentStatus.Booked or AppointmentStatus.CheckedIn,
                "appointmentId", "Appointment is not open.");
            v.Require(appt?.Requisition is null, "appointmentId", "This appointment already has a requisition.");
        }

        var testIds = (req.TestIds ?? []).Distinct().ToArray();
        var tests = await db.Tests.AsNoTracking().Include(t => t.Members).ThenInclude(m => m.Member)
            .Where(t => testIds.Contains(t.TestId)).ToListAsync(ct);
        foreach (var id in testIds.Except(tests.Select(t => t.TestId)))
            v.Add("testIds", $"Test {id} not found.");
        foreach (var t in tests.Where(t => !t.IsActive || !t.IsOrderable))
            v.Add("testIds", $"{t.NameEn} can't be ordered.");
        if (patient is not null)
            foreach (var t in tests.Where(t => t.ApplicableSex is not null && patient.Sex != t.ApplicableSex))
                v.Add("testIds", $"{t.NameEn} only applies to sex {t.ApplicableSex}.");

        // Ordering a panel and one of its members is a duplicate order.
        var orderedIds = tests.Select(t => t.TestId).ToHashSet();
        foreach (var panel in tests.Where(t => t.Members.Count > 0))
            foreach (var dup in panel.Members.Where(m => orderedIds.Contains(m.MemberTestId)))
                v.Add("testIds", $"{dup.Member.NameEn} is already part of {panel.NameEn}.");

        // Without an Ontario health card everything is self-pay, so every test needs a price.
        var hasOhip = patient?.HealthCardNumber is not null && patient.HealthCardProvince == "ON";
        if (!hasOhip)
            foreach (var t in tests.Where(t => t.UninsuredPrice is null))
                v.Add("testIds", $"{t.NameEn} has no self-pay price; the patient needs an Ontario health card for it.");

        if (!v.IsValid) return v.Problem();

        var seq = await db.NextSequenceValueAsync(Sequence.Accession, ct);
        var now = DateTime.UtcNow;
        var requisition = new Requisition
        {
            AccessionNumber = $"LF{today:yy}-{seq:D6}",
            PatientId = req.PatientId,
            OrderingPractitionerId = req.OrderingPractitionerId,
            LocationId = req.LocationId,
            AppointmentId = req.AppointmentId,
            Priority = req.Priority,
            Status = "Received",
            PayerType = hasOhip ? "OHIP" : "Patient",
            RequisitionDate = req.RequisitionDate,
            ClinicalNotes = string.IsNullOrWhiteSpace(req.ClinicalNotes) ? null : req.ClinicalNotes.Trim(),
            IsPregnant = req.IsPregnant,
            PregnancyWeek = req.PregnancyWeek,
            FastingHours = req.FastingHours,
            ReceivedAt = now,
            ReceivedByUserId = user.UserId(),
            UpdatedAt = now,
            Items = tests.Select(t =>
            {
                var insured = hasOhip && t.IsOhipInsured;
                return new RequisitionItem { TestId = t.TestId, IsInsured = insured, Price = insured ? null : t.UninsuredPrice };
            }).ToList(),
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Requisitions.Add(requisition);
        if (appt is not null) appt.Status = AppointmentStatus.Completed;
        await db.SaveChangesAsync(ct);

        var patientPays = requisition.Items.Sum(i => i.Price ?? 0);
        if (patientPays > 0)
            db.Invoices.Add(new Invoice
            {
                RequisitionId = requisition.RequisitionId,
                PatientId = requisition.PatientId,
                Subtotal = patientPays,
                Tax = 0,   // HST treatment of uninsured lab services still to be confirmed
                Status = "Open",
                CreatedAt = now,
            });

        audit.Log("Create", "Requisition", requisition.RequisitionId, requisition.PatientId,
            new { accession = requisition.AccessionNumber, tests = tests.Select(t => t.Code) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var dto = await LoadRequisitionAsync(db, requisition.RequisitionId, ct);
        return Results.Created($"/api/reception/requisitions/{requisition.RequisitionId}", dto);
    }

    private static async Task<RequisitionDto?> LoadRequisitionAsync(LabFlowDbContext db, int id, CancellationToken ct)
    {
        var r = await db.Requisitions.AsNoTracking()
            .Include(x => x.Patient)
            .Include(x => x.OrderingPractitioner)
            .Include(x => x.Items).ThenInclude(i => i.Test).ThenInclude(t => t.Members).ThenInclude(m => m.Member)
            .SingleOrDefaultAsync(x => x.RequisitionId == id, ct);
        if (r is null) return null;

        var invoiceId = await db.Invoices.Where(i => i.RequisitionId == id && i.Status != "Void")
            .Select(i => (int?)i.InvoiceId).FirstOrDefaultAsync(ct);

        // Tubes to draw: specimen types of the ordered tests and of panel members.
        var tubes = r.Items
            .SelectMany(i => i.Test.Members.Select(m => m.Member.SpecimenTypeCode).Append(i.Test.SpecimenTypeCode))
            .OfType<string>().Distinct().Order().ToArray();

        return new RequisitionDto(
            r.RequisitionId, r.AccessionNumber, r.PatientId, $"{r.Patient.LastName}, {r.Patient.FirstName}", r.Patient.Mrn,
            $"Dr. {r.OrderingPractitioner.FirstName} {r.OrderingPractitioner.LastName}",
            r.PayerType, r.Priority, r.Status, r.ReceivedAt,
            r.Items.OrderBy(i => i.Test.NameEn)
                .Select(i => new RequisitionItemDto(i.TestId, i.Test.Code, i.Test.NameEn, i.IsInsured, i.Price, i.Test.FastingRequired))
                .ToArray(),
            tubes, r.Items.Sum(i => i.Price ?? 0), invoiceId);
    }
}
