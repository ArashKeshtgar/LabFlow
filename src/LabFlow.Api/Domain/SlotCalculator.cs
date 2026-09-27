using LabFlow.Api.Data;

namespace LabFlow.Api.Domain;

public record Slot(DateTime StartUtc, DateTime EndUtc, TimeOnly LocalTime, int Remaining);

/// <summary>
/// Builds the bookable slots for one location and one local date. Opening hours are stored
/// in local (Toronto) time; appointments are stored in UTC, so every slot is converted.
/// </summary>
public static class SlotCalculator
{
    public static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    public static IReadOnlyList<Slot> GetSlots(
        DateOnly date,
        IEnumerable<LocationHours> hours,
        bool isClosed,
        IReadOnlyDictionary<DateTime, int> bookedByStartUtc,
        DateTime nowUtc,
        TimeZoneInfo? zone = null)
    {
        if (isClosed) return [];
        zone ??= Toronto;

        var slots = new List<Slot>();
        foreach (var h in hours.Where(h => h.DayOfWeek == (byte)date.DayOfWeek).OrderBy(h => h.OpenTime))
        {
            var step = TimeSpan.FromMinutes(h.SlotMinutes);
            for (var t = h.OpenTime; t.Add(step) <= h.CloseTime && t >= h.OpenTime; t = t.Add(step))
            {
                var local = date.ToDateTime(t, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local)) continue;   // skipped hour when clocks go forward

                var startUtc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                if (startUtc <= nowUtc) continue;

                var booked = bookedByStartUtc.GetValueOrDefault(startUtc);
                var remaining = h.CapacityPerSlot - booked;
                if (remaining > 0)
                    slots.Add(new Slot(startUtc, startUtc.Add(step), t, remaining));
            }
        }
        return slots;
    }

    public static DateOnly TodayInToronto(DateTime nowUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Toronto));

    public static DateTime ToToronto(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Toronto);

    /// <summary>UTC range covering one local calendar day.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) DayRangeUtc(DateOnly date) =>
        (TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Toronto),
         TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), Toronto));
}
