using CheapHours.Core.Goals;
using CheapHours.Core.Ids;

namespace CheapHours.UnitTests.Core;

public sealed class GoalPlanTests
{
    private static readonly GoalId GoalId = GoalId.Parse("goal_1");
    private static readonly PlanId PlanId = PlanId.Parse("plan_8812");
    private static readonly OwnerId Owner = OwnerId.Parse("own_demo");
    private static readonly DeviceId Car = DeviceId.Parse("dev_car_01");
    private static readonly DeviceId Wallbox = DeviceId.Parse("dev_wallbox_01");
    private static readonly DateTimeOffset Departure = new(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OneAm = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Goal_links_a_vehicle_and_a_charger()
    {
        var goal = new Goal(GoalId, Owner, Car, Wallbox, targetSocPercent: 80, Departure);

        Assert.Equal(Car, goal.VehicleDeviceId);
        Assert.Equal(Wallbox, goal.ChargerDeviceId);
        Assert.Equal(80, goal.TargetSocPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Goal_target_out_of_range_is_refused(int target)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Goal(GoalId, Owner, Car, Wallbox, target, Departure));
    }

    [Fact]
    public void Goal_with_the_same_device_twice_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new Goal(GoalId, Owner, Wallbox, Wallbox, 80, Departure));
    }

    [Fact]
    public void Goal_departure_must_be_utc()
    {
        var local = new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => new Goal(GoalId, Owner, Car, Wallbox, 80, local));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    public void Slot_on_the_15_minute_grid_is_created(int minute)
    {
        var slot = new PlanSlot(OneAm.AddMinutes(minute), 7400);

        Assert.Equal(TimeSpan.FromMinutes(15), slot.EndUtc - slot.StartUtc);
    }

    [Theory]
    [InlineData(7, 0)]
    [InlineData(0, 30)]
    public void Slot_off_the_15_minute_grid_is_refused(int minute, int second)
    {
        Assert.Throws<ArgumentException>(() => new PlanSlot(OneAm.AddMinutes(minute).AddSeconds(second), 7400));
    }

    [Fact]
    public void Slot_needs_positive_power_and_utc()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanSlot(OneAm, 0));
        Assert.Throws<ArgumentException>(() => new PlanSlot(new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.FromHours(2)), 7400));
    }

    [Fact]
    public void Savings_are_derived_from_the_two_costs()
    {
        var plan = Draft([new PlanSlot(OneAm, 7400)], expectedCostEur: 1.20m, arrivalCostEur: 2.00m);

        Assert.Equal(0.80m, plan.SavingsEur);
    }

    [Fact]
    public void Draft_needs_an_expiry_and_other_statuses_drop_it()
    {
        Assert.Throws<ArgumentException>(() => new Plan(PlanId, GoalId, Owner, 1, PlanStatus.Draft, [], 0, 0));

        var confirmed = new Plan(PlanId, GoalId, Owner, 1, PlanStatus.Confirmed, [], 0, 0, OneAm);
        Assert.Null(confirmed.ExpiresAtUtc);
    }

    [Fact]
    public void Plan_version_starts_at_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Plan(PlanId, GoalId, Owner, 0, PlanStatus.Confirmed, [], 0, 0));
    }

    [Fact]
    public void Overlapping_or_unordered_slots_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Draft([new PlanSlot(OneAm.AddMinutes(15), 7400), new PlanSlot(OneAm, 7400)]));
        Assert.Throws<ArgumentException>(() => Draft([new PlanSlot(OneAm, 7400), new PlanSlot(OneAm, 3700)]));
    }

    [Fact]
    public void Non_adjacent_ascending_slots_are_allowed()
    {
        var plan = Draft([new PlanSlot(OneAm, 7400), new PlanSlot(OneAm.AddHours(2), 7400)]);

        Assert.Equal(2, plan.Slots.Count);
    }

    [Fact]
    public void Plan_copies_its_slots()
    {
        List<PlanSlot> slots = [new PlanSlot(OneAm, 7400)];
        var plan = Draft(slots);

        slots.Add(new PlanSlot(OneAm.AddHours(1), 7400));

        Assert.Single(plan.Slots);
    }

    private static Plan Draft(IReadOnlyList<PlanSlot> slots, decimal expectedCostEur = 1m, decimal arrivalCostEur = 2m) =>
        new(PlanId, GoalId, Owner, 1, PlanStatus.Draft, slots, expectedCostEur, arrivalCostEur, OneAm.AddMinutes(-50));
}
