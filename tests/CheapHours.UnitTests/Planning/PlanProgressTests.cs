using CheapHours.Core.Goals;
using CheapHours.Planning;

namespace CheapHours.UnitTests.Planning;

public sealed class PlanProgressTests
{
    private static readonly DateTimeOffset OneAm = new(2026, 11, 3, 1, 0, 0, TimeSpan.Zero);

    // Two 11 kW slots at 01:00 and 02:00 deliver 5,500 Wh: exactly the energy for 40 % → 50 %.
    private static readonly PlanSlot[] Slots = [new(OneAm, 11_000), new(OneAm.AddHours(1), 11_000)];

    [Fact]
    public void Before_the_first_slot_the_plan_expects_the_start_charge()
    {
        Assert.Equal(40m, PlanProgress.ExpectedSoc(Slots, 40m, 50, 5_500, OneAm.AddMinutes(-30)));
        Assert.Equal(40m, PlanProgress.ExpectedSoc(Slots, 40m, 50, 5_500, OneAm));
    }

    [Fact]
    public void Mid_slot_counts_pro_rata()
    {
        // Half of the first slot: 1,375 Wh of 5,500, a quarter of the 10-point gap.
        Assert.Equal(42.5m, PlanProgress.ExpectedSoc(Slots, 40m, 50, 5_500, OneAm.AddMinutes(7.5)));
    }

    [Fact]
    public void Between_slots_the_charge_holds()
    {
        Assert.Equal(45m, PlanProgress.ExpectedSoc(Slots, 40m, 50, 5_500, OneAm.AddMinutes(40)));
    }

    [Fact]
    public void After_the_last_slot_the_plan_expects_the_target()
    {
        Assert.Equal(50m, PlanProgress.ExpectedSoc(Slots, 40m, 50, 5_500, OneAm.AddHours(3)));
    }

    [Fact]
    public void Expected_charge_is_capped_at_the_target()
    {
        // The plan delivers more than needed (the last slot rounds up): the expectation stops at the target.
        Assert.Equal(50m, PlanProgress.ExpectedSoc(Slots, 40m, 50, 4_000, OneAm.AddHours(3)));
        Assert.Equal(50m, PlanProgress.ExpectedSoc(Slots, 40m, 50, 4_000, OneAm.AddHours(1).AddMinutes(10)));
    }

    [Fact]
    public void No_energy_needed_expects_the_start_charge()
    {
        Assert.Equal(80m, PlanProgress.ExpectedSoc([], 80m, 80, 0, OneAm));
    }
}
