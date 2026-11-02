using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Providers.HeatPumpCloud;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Kit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CheapHours.ContractTests.HeatPumpCloud;

/// <summary>
/// The heat-pump-cloud provider's harness. The vendor pushes signed webhooks, so the provider is a translator and a
/// verifier: there is no vendor fake, no account and no HTTP. Signatures are computed here, independently of the
/// provider, from the vendor's documented scheme.
/// </summary>
/// <param name="register">Registers another provider on the same vendor scheme (a deliberately broken one); by default the real provider.</param>
internal sealed class HeatPumpCloudHarness(Action<IServiceCollection>? register = null) : IProviderContractHarness
{
    public const string Secret = "contract-heat-pump-signing-secret-01";

    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 2, 10, 0, 2, TimeSpan.Zero);

    public IReadOnlyDictionary<string, string?> Configuration { get; } = new Dictionary<string, string?>
    {
        ["Providers:HeatPumpCloud:SigningSecret"] = Secret,
        ["Providers:HeatPumpCloud:Accounts:0:Owner"] = "own_demo",
        ["Providers:HeatPumpCloud:Accounts:0:ExternalAccountId"] = "acct_demo",
    };

    public bool UsesHttp => false;

    public bool UsesOAuth => false;

    public ProviderAccount? Account => null;

    public int VendorRequestCount => 0;

    public int TokenRequestCount => 0;

    public (InboundMessage Input, DeviceReading Expected)? KnownInput =>
    (
        Unsigned(SampleWebhookBody),
        new DeviceReading("hp_1", new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), 2_400, 1_234_568)
    );

    public InboundMessage? UnhandledInput => Unsigned("""{"eventId":"evt_02","type":"account.updated","occurredAt":"2026-11-02T10:00:00Z","accountId":"acct_demo"}""");

    public InboundMessage? MalformedInput => Unsigned("not json");

    public string? SigningSecret => Secret;

    public string SampleWebhookBody =>
        """{"eventId":"evt_01","type":"device.state","occurredAt":"2026-11-02T10:00:00Z","accountId":"acct_demo","device":{"id":"hp_1","maker":"Kaltwerk","model":"AeroHeat 9"},"state":{"online":true,"mode":"Heating","powerKw":2.4,"energyKwh":1234.5678}}""";

    /// <summary>The vendor's header: <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "&lt;t&gt;.&lt;body&gt;")&gt;</c>.</summary>
    /// <param name="secret">The signing secret.</param>
    /// <param name="signedAt">The time to sign with.</param>
    /// <param name="body">The raw body.</param>
    /// <returns>The header value.</returns>
    public static string Signature(string secret, DateTimeOffset signedAt, ReadOnlySpan<byte> body)
    {
        var t = signedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = Encoding.ASCII.GetBytes(t + ".").Concat(body.ToArray()).ToArray();
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload))}";
    }

    public InboundMessage SignedWebhook(string body, DateTimeOffset receivedAt, DateTimeOffset signedAt, string? secret = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = new Dictionary<string, string> { [HeatPumpCloudProvider.SignatureHeader] = Signature(secret ?? Secret, signedAt, bytes) };
        return new InboundMessage(null, bytes, headers, receivedAt);
    }

    public void Register(IServiceCollection services)
    {
        if (register is not null)
        {
            register(services);
            return;
        }

        services.AddProvider<HeatPumpCloudProvider, HeatPumpCloudOptions>(HeatPumpCloudOptions.SectionName);
    }

    public Task<(string ExternalId, DeviceReading Expected)> ArrangeDeviceWithReadingAsync() => throw new NotSupportedException();

    public Task<string> ArrangeRemovedDeviceAsync() => throw new NotSupportedException();

    public Task ArrangeFailureAsync(int status, TimeSpan? retryAfter = null, string? body = null) => throw new NotSupportedException();

    public Task ArrangeDelayAsync(TimeSpan delay) => throw new NotSupportedException();

    public Task ArrangeAccessTokenRejectedAsync() => throw new NotSupportedException();

    public Task ArrangeRefreshRevokedAsync() => throw new NotSupportedException();

    public IReadOnlyList<string> CommandKeysSentFor(string externalId) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InboundMessage Unsigned(string body) => new(null, Encoding.UTF8.GetBytes(body), new Dictionary<string, string>(), ReceivedAt);
}
