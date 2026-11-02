using CheapHours.Providers.HeatPumpCloud;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;

namespace CheapHours.ContractTests.HeatPumpCloud;

/// <summary>
/// The heat-pump-cloud provider on the shared suite: its translation, normalisation and webhook verification rules
/// run; its polling, command and HTTP rules are reported as skipped.
/// </summary>
public sealed class HeatPumpCloudContractTests : ProviderContractTests<HeatPumpCloudProvider>
{
    [Fact]
    public async Task Polling_rules_are_skipped_naming_the_missing_interaction_style()
    {
        var skip = await Skips.ReasonOfAsync(() => new HeatPumpCloudContractTests().An_unknown_device_is_reported());

        Assert.Contains(nameof(IPollingProvider), skip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_rules_are_skipped_naming_the_missing_interaction_style()
    {
        var skip = await Skips.ReasonOfAsync(() => new HeatPumpCloudContractTests().A_duplicate_key_acts_once());

        Assert.Contains(nameof(ICommandSender), skip, StringComparison.Ordinal);
    }

    protected override IProviderContractHarness CreateHarness() => new HeatPumpCloudHarness();
}
