using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>Builds and posts heat-pump-cloud webhooks the way the vendor signs them (README of the provider).</summary>
internal static class HeatPumpWebhooks
{
    /// <summary>The signing secret the test gateway is configured with.</summary>
    public const string Secret = "integration-heat-pump-signing-secret";

    /// <summary>The configuration that registers the provider with account <c>acct_demo</c> owned by <c>own_demo</c>.</summary>
    public static Dictionary<string, string?> Settings() => new()
    {
        ["Providers:HeatPumpCloud:SigningSecret"] = Secret,
        ["Providers:HeatPumpCloud:ExpectedReportInterval"] = "00:00:15",
        ["Providers:HeatPumpCloud:Accounts:0:Owner"] = "own_demo",
        ["Providers:HeatPumpCloud:Accounts:0:ExternalAccountId"] = "acct_demo",
    };

    /// <summary>A <c>device.state</c> event.</summary>
    public static string State(string deviceId = "hp_1", string occurredAt = "2026-11-02T10:00:00Z", decimal energyKwh = 1234.5678m, string eventId = "evt_01") =>
        $$$"""{"eventId":"{{{eventId}}}","type":"device.state","occurredAt":"{{{occurredAt}}}","accountId":"acct_demo","device":{"id":"{{{deviceId}}}","maker":"Kaltwerk","model":"AeroHeat 9"},"state":{"online":true,"mode":"Heating","powerKw":2.4,"energyKwh":{{{energyKwh.ToString(CultureInfo.InvariantCulture)}}}}}""";

    /// <summary>The <c>HeatPump-Signature</c> value for <paramref name="body"/>.</summary>
    public static string Signature(string body, DateTimeOffset? signedAt = null, string secret = Secret)
    {
        var t = (signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},v1={Convert.ToHexStringLower(mac)}";
    }

    /// <summary>Posts a webhook: signed now by default, with the given signature, or unsigned when it is <see langword="null"/>.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string path = "/webhooks/heatpumpcloud/acct_demo", string? signature = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        if (signature is not null)
        {
            request.Headers.Add("HeatPump-Signature", signature.Length == 0 ? Signature(body) : signature);
        }

        request.Headers.Add("HeatPump-Event-Id", "evt_test");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
