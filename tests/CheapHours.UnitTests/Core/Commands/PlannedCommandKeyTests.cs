using CheapHours.Core.Commands;
using CheapHours.Core.Goals;
using CheapHours.Core.Ids;
using FsCheck.Xunit;

namespace CheapHours.UnitTests.Core.Commands;

public sealed class PlannedCommandKeyTests
{
    // A small alphabet with both separators that may appear in an id body, so near-collisions are common.
    private const string BodyAlphabet = "a1_-";

    private static readonly PlanId Plan = PlanId.Parse("plan_8812");
    private static readonly DateTimeOffset OneAm = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Key_has_the_documented_format()
    {
        Assert.Equal(
            "plan_8812:v3:slot_2026-10-12T01:00Z:StartCharging",
            PlannedCommandKey.For(Plan, 3, new PlanSlot(OneAm, 7400), CommandType.StartCharging));
    }

    [Fact]
    public void Same_inputs_give_the_same_key_whatever_the_slot_power()
    {
        Assert.Equal(
            PlannedCommandKey.For(Plan, 3, new PlanSlot(OneAm, 7400), CommandType.SetPowerLimit),
            PlannedCommandKey.For(Plan, 3, new PlanSlot(OneAm, 3700), CommandType.SetPowerLimit));
    }

    [Fact]
    public void Re_planning_one_version_gives_the_same_key_set()
    {
        var slots = Enumerable.Range(0, 8).Select(i => new PlanSlot(OneAm + (PlanSlot.Duration * i), 11000)).ToArray();

        string[] Keys() =>
            [.. slots.SelectMany(s => Enum.GetValues<CommandType>(), (s, t) => PlannedCommandKey.For(Plan, 3, s, t))];

        var first = Keys();

        Assert.Equal(first, Keys());
        Assert.Equal(first.Length, first.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_new_plan_version_gives_a_new_key()
    {
        var slot = new PlanSlot(OneAm, 7400);

        Assert.NotEqual(
            PlannedCommandKey.For(Plan, 3, slot, CommandType.StartCharging),
            PlannedCommandKey.For(Plan, 4, slot, CommandType.StartCharging));
    }

    [Fact]
    public void Version_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlannedCommandKey.For(Plan, 0, new PlanSlot(OneAm, 7400), CommandType.StartCharging));
    }

    [Fact]
    public void Default_plan_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PlannedCommandKey.For(default, 3, new PlanSlot(OneAm, 7400), CommandType.StartCharging));
    }

    // Draws many inputs from small domains (so equal and nearly equal inputs are frequent) and checks that
    // keys are equal exactly when inputs are equal.
    [Property(MaxTest = 300)]
    public void Distinct_inputs_give_distinct_keys((byte[] Body, byte Version, byte Slot, CommandType Type)[] raw)
    {
        var inputs = raw
            .Where(r => r.Body is { Length: > 0 })
            .Select(r => (
                Plan: PlanId.Parse("plan_" + new string([.. r.Body.Take(64).Select(b => BodyAlphabet[b % BodyAlphabet.Length])])),
                Version: (r.Version % 4) + 1,
                SlotStart: OneAm + (PlanSlot.Duration * (r.Slot % 6)),
                r.Type))
            .ToArray();

        var keysByInput = inputs
            .Distinct()
            .ToDictionary(i => i, i => PlannedCommandKey.For(i.Plan, i.Version, new PlanSlot(i.SlotStart, 7400), i.Type));

        Assert.Equal(keysByInput.Count, keysByInput.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(inputs, i => Assert.Equal(keysByInput[i], PlannedCommandKey.For(i.Plan, i.Version, new PlanSlot(i.SlotStart, 3700), i.Type)));
    }
}
