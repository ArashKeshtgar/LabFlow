using LabFlow.Api.Data;
using LabFlow.Api.Domain;

namespace LabFlow.Api.Tests;

public class SlotCalculatorTests
{
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<DateTime, int> NoneBooked = [];

    // Mon-Fri 07:00-16:00, Sat 07:00-12:00, 10-minute slots, 2 people per slot (as in the demo seed)
    private static readonly LocationHours[] Hours =
        Enumerable.Range(1, 6).Select(d => new LocationHours
        {
            LocationId = 1,
            DayOfWeek = (byte)d,
            OpenTime = new TimeOnly(7, 0),
            CloseTime = d == 6 ? new TimeOnly(12, 0) : new TimeOnly(16, 0),
            SlotMinutes = 10,
            CapacityPerSlot = 2,
        }).ToArray();

    [Fact]
    public void Weekday_has_slots_from_open_to_last_full_slot_before_close()
    {
        var slots = SlotCalculator.GetSlots(new DateOnly(2026, 9, 28), Hours, false, NoneBooked, LongAgo);

        Assert.Equal(54, slots.Count);   // 9 hours x 6 slots
        Assert.Equal(new TimeOnly(7, 0), slots[0].LocalTime);
        Assert.Equal(new TimeOnly(15, 50), slots[^1].LocalTime);
    }

    [Fact]
    public void Local_time_converts_to_utc_across_the_daylight_saving_change()
    {
        // EDT (UTC-4) before Nov 1 2026, EST (UTC-5) after
        var summer = SlotCalculator.GetSlots(new DateOnly(2026, 9, 28), Hours, false, NoneBooked, LongAgo);
        var winter = SlotCalculator.GetSlots(new DateOnly(2026, 11, 2), Hours, false, NoneBooked, LongAgo);

        Assert.Equal(new DateTime(2026, 9, 28, 11, 0, 0), summer[0].StartUtc);
        Assert.Equal(new DateTime(2026, 11, 2, 12, 0, 0), winter[0].StartUtc);
    }

    [Fact]
    public void Sunday_and_closures_have_no_slots()
    {
        Assert.Empty(SlotCalculator.GetSlots(new DateOnly(2026, 9, 27), Hours, false, NoneBooked, LongAgo));
        Assert.Empty(SlotCalculator.GetSlots(new DateOnly(2026, 10, 12), Hours, isClosed: true, NoneBooked, LongAgo));
    }

    [Fact]
    public void Full_slots_are_dropped_and_partly_booked_ones_show_what_is_left()
    {
        var date = new DateOnly(2026, 9, 28);
        var seven = new DateTime(2026, 9, 28, 11, 0, 0);
        var sevenTen = seven.AddMinutes(10);
        var booked = new Dictionary<DateTime, int> { [seven] = 2, [sevenTen] = 1 };

        var slots = SlotCalculator.GetSlots(date, Hours, false, booked, LongAgo);

        Assert.DoesNotContain(slots, s => s.StartUtc == seven);
        Assert.Equal(1, slots.Single(s => s.StartUtc == sevenTen).Remaining);
    }

    [Fact]
    public void Slots_in_the_past_are_not_offered()
    {
        var now = new DateTime(2026, 9, 28, 14, 5, 0, DateTimeKind.Utc);   // 10:05 in Toronto

        var slots = SlotCalculator.GetSlots(new DateOnly(2026, 9, 28), Hours, false, NoneBooked, now);

        Assert.Equal(new TimeOnly(10, 10), slots[0].LocalTime);
    }

    [Fact]
    public void Saturday_closes_at_noon()
    {
        var slots = SlotCalculator.GetSlots(new DateOnly(2026, 10, 3), Hours, false, NoneBooked, LongAgo);
        Assert.Equal(new TimeOnly(11, 50), slots[^1].LocalTime);
    }
}
