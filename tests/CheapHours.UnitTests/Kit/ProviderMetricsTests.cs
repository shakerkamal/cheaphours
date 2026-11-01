using System.Diagnostics.Metrics;
using System.Net;
using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.DependencyInjection;
using static CheapHours.UnitTests.Kit.HttpKitHarness;

namespace CheapHours.UnitTests.Kit;

public sealed class ProviderMetricsTests
{
    [Fact]
    public async Task Failed_call_counted_once_with_provider_and_outcome()
    {
        await using var sp = Build(StubVendor.Always(HttpStatusCode.ServiceUnavailable));
        var factory = sp.GetRequiredService<IMeterFactory>();
        var measurements = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Meter.Name == "CheapHours.Providers")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "cheaphours.provider.requests")
            {
                lock (measurements)
                {
                    measurements.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
                }
            }
        });
        listener.Start();
        var account = new ProviderAccount("carcloud", OwnerId.Parse("own_demo"), "acct_secret_id");

        using var _ = await sp.GetRequiredService<HttpProviderA>().Client.SendAsync(account, Get, TestContext.Current.CancellationToken);

        var (value, tags) = Assert.Single(measurements);
        Assert.Equal(1, value);
        Assert.Equal("carcloud", tags["provider.id"]);
        Assert.Equal("transient", tags["outcome"]);
        Assert.Equal(["outcome", "provider.id"], tags.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null, "success")]
    [InlineData(VendorFailureKind.Transient, "transient")]
    [InlineData(VendorFailureKind.Rejected, "rejected")]
    [InlineData(VendorFailureKind.UnknownDevice, "unknown-device")]
    [InlineData(VendorFailureKind.ReconnectNeeded, "reconnect-needed")]
    public void Outcome_tags(VendorFailureKind? kind, string outcome)
    {
        Assert.Equal(outcome, ProviderMetrics.Outcome(kind is { } k ? new VendorFailure(k, "x") : null));
    }
}
