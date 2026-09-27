using System.Data;
using LabFlow.Api.Auth;
using LabFlow.Api.Data;
using LabFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace LabFlow.Api.Endpoints;

/// <summary>Public endpoints: locations, test catalog, slots and online booking.</summary>
public static class BookingEndpoints
{
    public const string BookingRateLimit = "booking";

    public record LocationDto(short LocationId, string Name, string Address, string City, string PostalCode, string? Phone);

    public record TestDto(
        int TestId, string Code, string Name, string? NameFr, string Department, bool IsPanel,
        string[] Members, bool IsOhipInsured, decimal? UninsuredPrice, bool FastingRequired,
        string? Preparation, string? ApplicableSex, string? SpecimenTypeCode);

    public record SlotDto(DateTime StartUtc, string LocalTime, int Remaining);

    public record BookingRequest(short LocationId, DateTime StartUtc, PatientDetails Patient, string? Notes);

    public record BookingConfirmation(int AppointmentId, string Location, DateTime StartUtc, string LocalStart, string? ConfirmationSentTo);

    public static void MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Booking");

        api.MapGet("/locations", async (LabFlowDbContext db, CancellationToken ct) =>
            await db.Locations.AsNoTracking()
                .Where(l => l.IsActive && l.IsCollectionCentre)
                .OrderBy(l => l.Name)
                .Select(l => new LocationDto(l.LocationId, l.Name, l.AddressLine1, l.City, l.PostalCode, l.Phone))
                .ToListAsync(ct));

        api.MapGet("/tests", async (LabFlowDbContext db, CancellationToken ct) =>
        {
            var tests = await db.Tests.AsNoTracking()
                .Where(t => t.IsActive && t.IsOrderable)
                .Include(t => t.Department)
                .Include(t => t.Members.OrderBy(m => m.SortOrder)).ThenInclude(m => m.Member)
                .OrderBy(t => t.Department.SortOrder).ThenBy(t => t.NameEn)
                .ToListAsync(ct);
            return tests.Select(t => new TestDto(
                t.TestId, t.Code, t.NameEn, t.NameFr, t.Department.NameEn, t.ResultType == "Panel",
                t.Members.Select(m => m.Member.NameEn).ToArray(), t.IsOhipInsured, t.UninsuredPrice,
                t.FastingRequired, t.PreparationEn, t.ApplicableSex, t.SpecimenTypeCode));
        });

        api.MapGet("/locations/{locationId}/slots", async (short locationId, DateOnly date, LabFlowDbContext db, CancellationToken ct) =>
        {
            var slots = await LoadSlotsAsync(db, locationId, date, ct);
            return slots is null
                ? Results.NotFound()
                : Results.Ok(slots.Select(s => new SlotDto(s.StartUtc, s.LocalTime.ToString("HH:mm"), s.Remaining)));
        });

        api.MapPost("/appointments", BookAsync).RequireRateLimiting(BookingRateLimit);
    }

    private static async Task<IReadOnlyList<Slot>?> LoadSlotsAsync(LabFlowDbContext db, short locationId, DateOnly date, CancellationToken ct)
    {
        var location = await db.Locations.AsNoTracking().Include(l => l.Hours)
            .SingleOrDefaultAsync(l => l.LocationId == locationId && l.IsActive, ct);
        if (location is null) return null;

        var closed = await db.LocationClosures.AnyAsync(c => c.LocationId == locationId && c.ClosedOn == date, ct);
        var (fromUtc, toUtc) = SlotCalculator.DayRangeUtc(date);
        var booked = await db.Appointments
            .Where(a => a.LocationId == locationId && a.StartUtc >= fromUtc && a.StartUtc < toUtc
                        && a.Status != AppointmentStatus.Cancelled)
            .GroupBy(a => a.StartUtc)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return SlotCalculator.GetSlots(date, location.Hours, closed, booked, DateTime.UtcNow);
    }

    private static async Task<IResult> BookAsync(BookingRequest req, LabFlowDbContext db, AuditLogger audit, CancellationToken ct)
    {
        var today = SlotCalculator.TodayInToronto(DateTime.UtcNow);
        var v = new Validator();
        req.Patient.Validate(v, today);
        v.Require(req.Notes is null || req.Notes.Length <= 500, "notes", "Notes are limited to 500 characters.");
        if (!v.IsValid) return v.Problem();

        var startUtc = DateTime.SpecifyKind(req.StartUtc.ToUniversalTime(), DateTimeKind.Utc);
        var localDate = DateOnly.FromDateTime(SlotCalculator.ToToronto(startUtc));

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            // Serialize bookings for this slot so two people can't take the last place.
            var lockName = $"slot:{req.LocationId}:{startUtc:O}";
            await db.Database.ExecuteSqlAsync($"""
                DECLARE @r int;
                EXEC @r = sp_getapplock @Resource = {lockName}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
                IF @r < 0 THROW 50002, 'Could not lock the booking slot.', 1;
                """, ct);

            var slots = await LoadSlotsAsync(db, req.LocationId, localDate, ct);
            if (slots is null) return Results.NotFound();
            var slot = slots.FirstOrDefault(s => s.StartUtc == startUtc);
            if (slot is null)
                return Results.Conflict(new { error = "That time is no longer available. Please pick another slot." });

            var (patient, matchError) = await FindOrCreatePatientAsync(db, req.Patient, ct);
            if (matchError is not null) return matchError;

            var (dayFrom, dayTo) = SlotCalculator.DayRangeUtc(localDate);
            if (patient.PatientId != 0 && await db.Appointments.AnyAsync(a =>
                    a.PatientId == patient.PatientId && a.StartUtc >= dayFrom && a.StartUtc < dayTo
                    && a.Status == AppointmentStatus.Booked, ct))
                return Results.Conflict(new { error = "There is already a booking for this patient on that day." });

            var appointment = new Appointment
            {
                Patient = patient,
                LocationId = req.LocationId,
                StartUtc = slot.StartUtc,
                EndUtc = slot.EndUtc,
                Status = AppointmentStatus.Booked,
                Channel = "Web",
                Notes = req.Notes,
                CreatedAt = DateTime.UtcNow,
            };
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync(ct);

            // Confirmation goes to the address on file, never to one typed by an anonymous caller
            // for an existing patient - otherwise anyone with a health card number could redirect
            // someone else's notifications.
            string? sentTo = null;
            if (patient.ConsentEmailNotification && !string.IsNullOrEmpty(patient.Email))
            {
                db.Notifications.Add(new Notification
                {
                    PatientId = patient.PatientId,
                    AppointmentId = appointment.AppointmentId,
                    Channel = "Email",
                    TemplateCode = "AppointmentConfirmed",
                    Language = patient.PreferredLanguage,
                    Recipient = patient.Email,
                    NextAttemptAt = DateTime.UtcNow,
                    QueuedAt = DateTime.UtcNow,
                });
                sentTo = MaskEmail(patient.Email);
            }

            audit.Log("Create", "Appointment", appointment.AppointmentId, patient.PatientId, new { channel = "Web" });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var location = await db.Locations.AsNoTracking().SingleAsync(l => l.LocationId == req.LocationId, ct);
            var local = SlotCalculator.ToToronto(appointment.StartUtc);
            return Results.Created($"/api/appointments/{appointment.AppointmentId}", new BookingConfirmation(
                appointment.AppointmentId, location.Name, appointment.StartUtc,
                local.ToString("dddd, MMMM d, yyyy 'at' h:mm tt"), sentTo));
        });
    }

    /// <summary>
    /// With a health card number, the booking must match the patient on file (date of birth and
    /// last name); a new card creates a patient. Without one, a new self-pay patient is created.
    /// An existing patient's contact details are never changed from this anonymous endpoint.
    /// </summary>
    private static async Task<(Patient Patient, IResult? Error)> FindOrCreatePatientAsync(
        LabFlowDbContext db, PatientDetails d, CancellationToken ct)
    {
        if (d.NormalizedHealthCard is { } hcn)
        {
            var existing = await db.Patients.SingleOrDefaultAsync(p => p.HealthCardNumber == hcn, ct);
            if (existing is not null)
            {
                var matches = existing.DateOfBirth == d.DateOfBirth
                              && string.Equals(existing.LastName.Trim(), d.LastName.Trim(), StringComparison.OrdinalIgnoreCase);
                return matches
                    ? (existing, null)
                    : (existing, Results.UnprocessableEntity(new { error = "The health card details don't match our records. Please call the location to book." }));
            }
        }

        var patient = await NewPatientAsync(db, d, ct);
        db.Patients.Add(patient);
        return (patient, null);
    }

    public static async Task<Patient> NewPatientAsync(LabFlowDbContext db, PatientDetails d, CancellationToken ct)
    {
        var mrnSeq = await db.NextSequenceValueAsync(Sequence.Mrn, ct);
        var now = DateTime.UtcNow;
        return new Patient
        {
            Mrn = $"LF{mrnSeq:D7}",
            HealthCardNumber = d.NormalizedHealthCard,
            HealthCardVersion = d.NormalizedVersion,
            HealthCardProvince = d.NormalizedHealthCard is null ? null : "ON",
            FirstName = d.FirstName.Trim(),
            LastName = d.LastName.Trim(),
            DateOfBirth = d.DateOfBirth,
            Sex = d.Sex,
            Email = string.IsNullOrWhiteSpace(d.Email) ? null : d.Email.Trim(),
            MobilePhone = string.IsNullOrWhiteSpace(d.MobilePhone) ? null : d.MobilePhone.Trim(),
            PreferredLanguage = d.PreferredLanguage,
            ConsentEmailNotification = d.ConsentEmailNotification,
            ConsentRecordedAt = now,
            CreatedAt = now,
        };
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? email : $"{email[0]}***{email[at..]}";
    }
}
