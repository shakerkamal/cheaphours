using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using CheapHours.Providers.Ocpp;
using CheapHours.Providers.Ocpp.Framing;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.ContractTests;

/// <summary>
/// The OCPP provider on the shared suite. OCPP chargers push frames, so it is a translator only: its
/// translation and normalisation rules run, and its polling, command, HTTP and webhook verification rules are
/// reported as skipped.
/// </summary>
public sealed class OcppContractTests : ProviderContractTests<OcppProvider>
{
    [Fact]
    public async Task Polling_rules_are_skipped_naming_the_missing_interaction_style()
    {
        var skip = await Skips.ReasonOfAsync(() => new OcppContractTests().An_unknown_device_is_reported());

        Assert.Contains(nameof(IPollingProvider), skip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_rules_are_skipped_naming_the_missing_interaction_style()
    {
        var skip = await Skips.ReasonOfAsync(() => new OcppContractTests().A_duplicate_key_acts_once());

        Assert.Contains(nameof(ICommandSender), skip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_verification_rules_are_skipped_naming_the_missing_interface()
    {
        var skip = await Skips.ReasonOfAsync(() => new OcppContractTests().A_webhook_with_a_tampered_body_is_refused());

        Assert.Contains(nameof(IWebhookVerifier), skip, StringComparison.Ordinal);
    }

    protected override IProviderContractHarness CreateHarness() => new OcppHarness();

    private sealed class OcppHarness : IProviderContractHarness
    {
        private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 23, 10, 0, 5, TimeSpan.Zero);

        public IReadOnlyDictionary<string, string?> Configuration { get; } = new Dictionary<string, string?>
        {
            ["Providers:Ocpp:HeartbeatIntervalSeconds"] = "60",
        };

        public bool UsesHttp => false;

        public bool UsesOAuth => false;

        public ProviderAccount? Account => null;

        public int VendorRequestCount => 0;

        public int TokenRequestCount => 0;

        public (InboundMessage Input, DeviceReading Expected)? KnownInput =>
        (
            Frame("""[2,"m1","MeterValues",{"connectorId":1,"transactionId":7,"meterValue":[{"timestamp":"2026-10-23T10:00:00.000Z","sampledValue":[{"value":"12.5","measurand":"Energy.Active.Import.Register","unit":"kWh"}]}]}]"""),
            new DeviceReading("CP001", new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero), null, 12_500)
        );

        public InboundMessage? UnhandledInput => Frame("""[2,"m2","DataTransfer",{"vendorId":"acme"}]""");

        public InboundMessage? MalformedInput => Frame("not json");

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<OcppSchemaValidator>();
            services.AddProvider<OcppProvider, OcppOptions>(OcppOptions.SectionName);
        }

        public Task<(string ExternalId, DeviceReading Expected)> ArrangeDeviceWithReadingAsync() => throw new NotSupportedException();

        public Task<string> ArrangeRemovedDeviceAsync() => throw new NotSupportedException();

        public Task ArrangeFailureAsync(int status, TimeSpan? retryAfter = null, string? body = null) => throw new NotSupportedException();

        public Task ArrangeDelayAsync(TimeSpan delay) => throw new NotSupportedException();

        public Task ArrangeAccessTokenRejectedAsync() => throw new NotSupportedException();

        public Task ArrangeRefreshRevokedAsync() => throw new NotSupportedException();

        public IReadOnlyList<string> CommandKeysSentFor(string externalId) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static InboundMessage Frame(string text) =>
            new("CP001", Encoding.UTF8.GetBytes(text), new Dictionary<string, string>(), ReceivedAt);
    }
}
