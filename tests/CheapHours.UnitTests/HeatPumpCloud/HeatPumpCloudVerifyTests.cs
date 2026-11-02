using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CheapHours.Providers.HeatPumpCloud;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Options;

namespace CheapHours.UnitTests.HeatPumpCloud;

/// <summary>Webhook signatures: HMAC-SHA256 over <c>"&lt;t&gt;.&lt;raw body&gt;"</c>, within the tolerance of the receipt time.</summary>
public sealed class HeatPumpCloudVerifyTests
{
    internal const string Secret = "heat-pump-signing-secret-0123456789";
    private const string Body = """{"eventId":"evt_01","type":"device.state"}""";
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    internal static HeatPumpCloudProvider Provider(TimeSpan? tolerance = null, string? secret = Secret) =>
        new(Options.Create(new HeatPumpCloudOptions { SigningSecret = secret, SignatureTolerance = tolerance ?? TimeSpan.FromMinutes(5) }));

    internal static string Signature(string secret, DateTimeOffset signedAt, string body)
    {
        var t = signedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},v1={Convert.ToHexStringLower(mac)}";
    }

    private static InboundMessage Message(string? header, string body = Body, DateTimeOffset? receivedAt = null, string headerName = HeatPumpCloudProvider.SignatureHeader)
    {
        var headers = new Dictionary<string, string>();
        if (header is not null)
        {
            headers[headerName] = header;
        }

        return new InboundMessage(null, Encoding.UTF8.GetBytes(body), headers, receivedAt ?? ReceivedAt);
    }

    [Fact]
    public void A_body_signed_ten_seconds_before_receipt_verifies()
    {
        Assert.True(Provider().Verify(Message(Signature(Secret, ReceivedAt.AddSeconds(-10), Body))));
    }

    [Fact]
    public void The_header_name_is_case_insensitive()
    {
        Assert.True(Provider().Verify(Message(Signature(Secret, ReceivedAt.AddSeconds(-10), Body), headerName: "heatpump-signature")));
    }

    [Fact]
    public void A_stale_timestamp_six_minutes_old_fails_with_a_five_minute_tolerance()
    {
        Assert.False(Provider().Verify(Message(Signature(Secret, ReceivedAt.AddMinutes(-6), Body))));
    }

    [Fact]
    public void A_timestamp_from_the_future_beyond_the_tolerance_fails()
    {
        Assert.False(Provider().Verify(Message(Signature(Secret, ReceivedAt.AddMinutes(6), Body))));
    }

    [Theory]
    [InlineData(-300, true)]
    [InlineData(-301, false)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void The_tolerance_boundary_is_inclusive(int offsetSeconds, bool verifies)
    {
        Assert.Equal(verifies, Provider().Verify(Message(Signature(Secret, ReceivedAt.AddSeconds(offsetSeconds), Body))));
    }

    [Fact]
    public void A_tampered_body_fails()
    {
        var header = Signature(Secret, ReceivedAt, Body);

        Assert.False(Provider().Verify(Message(header, Body.Replace("evt_01", "evt_02", StringComparison.Ordinal))));
    }

    [Fact]
    public void A_wrong_secret_fails()
    {
        Assert.False(Provider().Verify(Message(Signature("another-secret-of-thirty-two-chars!!", ReceivedAt, Body))));
    }

    [Fact]
    public void One_matching_signature_among_several_verifies()
    {
        var good = Signature(Secret, ReceivedAt, Body);
        var t = good.Split(',')[0];
        var header = $"{t},v1={new string('0', 64)},{good.Split(',')[1]}";

        Assert.True(Provider().Verify(Message(header)));
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        Assert.True(Provider().Verify(Message(Signature(Secret, ReceivedAt, Body) + ",v0=legacy")));
    }

    [Fact]
    public void A_missing_header_fails()
    {
        Assert.False(Provider().Verify(Message(null)));
    }

    [Theory]
    [InlineData("v1=zz,t=abc")]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("t=")]
    [InlineData("t=1793613600")]
    [InlineData("v1=00")]
    [InlineData("t=1793613600,v1=")]
    [InlineData("t=1793613600,v1=xyz")]
    [InlineData("t=-1793613600,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=99999999999999999999,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("garbage")]
    [InlineData("=1,=2")]
    public void A_garbled_header_fails_without_an_exception(string header)
    {
        Assert.False(Provider().Verify(Message(header)));
    }

    [Fact]
    public void A_repeated_timestamp_fails()
    {
        var good = Signature(Secret, ReceivedAt, Body);

        Assert.False(Provider().Verify(Message($"t=1,{good}")));
    }

    [Fact]
    public void Without_a_secret_nothing_verifies()
    {
        Assert.False(Provider(secret: null).Verify(Message(Signature(Secret, ReceivedAt, Body))));
    }

    [Fact]
    public void An_uppercase_hex_signature_verifies()
    {
        var header = Signature(Secret, ReceivedAt, Body);
        var parts = header.Split(',');

        Assert.True(Provider().Verify(Message($"{parts[0]},v1={parts[1][3..].ToUpperInvariant()}")));
    }
}
