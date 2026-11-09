using System.Diagnostics.Metrics;
using System.Net;
using CheapHours.Providers.Kit;
using CheapHours.Tests.Observability;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using static CheapHours.UnitTests.Kit.HttpKitHarness;

namespace CheapHours.UnitTests.Kit;

/// <summary>
/// The kit reports each HTTP provider's breaker on <c>cheaphours.provider.circuit.state</c> (provider-adapter-kit:
/// "Circuit-breaker state is reported per provider").
/// </summary>
public sealed class CircuitStateMetricTests
{
    private const string State = ProviderMetrics.CircuitStateName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_provider_reports_closed_open_half_open_and_closed_again_shared_by_all_its_clients()
    {
        using var trialStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var healthy = 0;
        var vendorA = new StubVendor((_, _) =>
        {
            if (Volatile.Read(ref healthy) == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            trialStarted.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var sp = Build(vendorA, StubVendor.Always(HttpStatusCode.OK), settings: OpensAfterTwoFailures);
        using var metrics = new MetricRecorder(sp.GetRequiredService<IMeterFactory>(), State);
        var providerA = sp.GetRequiredService<HttpProviderA>();
        sp.GetRequiredService<HttpProviderB>();

        // provider-adapter-kit: "Before the first call".
        Assert.Equal(0, metrics.Latest(State, ("provider.id", "provia")));
        Assert.Equal(0, metrics.Latest(State, ("provider.id", "provib")));

        // provider-adapter-kit: "Vendor goes down". A second client of the provider meets the same open breaker.
        using (await providerA.Client.SendAsync(AccountA, Get, Ct))
        using (await providerA.Client.SendAsync(AccountA, Get, Ct))
        {
        }

        Assert.Equal(2, metrics.Latest(State, ("provider.id", "provia")));
        Assert.Equal(0, metrics.Latest(State, ("provider.id", "provib")));
        using var other = sp.GetRequiredService<IHttpClientFactory>().CreateClient(typeof(HttpProviderA).FullName!);
        var sent = vendorA.Count;
        await Assert.ThrowsAsync<BrokenCircuitException>(() => other.GetAsync(new Uri("api/v1/vehicles", UriKind.Relative), Ct));
        Assert.Equal(sent, vendorA.Count);

        // provider-adapter-kit: "Vendor recovers". Half-open during the trial call, closed after it succeeds.
        await Task.Delay(TimeSpan.FromMilliseconds(700), Ct);
        Volatile.Write(ref healthy, 1);
        var trial = Task.Run(() => providerA.Client.SendAsync(AccountA, Get, Ct), Ct);
        Assert.True(trialStarted.Wait(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(1, metrics.Latest(State, ("provider.id", "provia")));

        release.Set();
        using var response = await trial;
        Assert.IsType<VendorResponse.Succeeded>(response);
        Assert.Equal(0, metrics.Latest(State, ("provider.id", "provia")));
        Assert.Equal(0, metrics.Latest(State, ("provider.id", "provib")));
    }

    [Fact]
    public void Only_providers_with_an_http_pipeline_are_reported()
    {
        var services = new ServiceCollection().AddProviderMetrics().BuildServiceProvider();
        using (services)
        {
            Assert.Empty(services.GetRequiredService<ProviderCircuitStates>().Observe());
        }
    }

    private static void OpensAfterTwoFailures(Dictionary<string, string?> settings)
    {
        settings["Providers:A:Http:MaxRetries"] = "0";
        settings["Providers:A:Http:MinimumThroughput"] = "2";
        settings["Providers:A:Http:FailureRatio"] = "0.5";
        settings["Providers:A:Http:BreakDuration"] = "00:00:00.500";
    }
}
