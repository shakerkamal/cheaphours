using System.Net;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static CheapHours.UnitTests.Kit.HttpKitHarness;

namespace CheapHours.UnitTests.Kit;

/// <summary>Per-account token buckets (design §3), driven by a fake clock.</summary>
public sealed class AccountRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Burst_over_the_limit()
    {
        var time = new FakeTimeProvider(Start);
        var vendor = StubVendor.Always(HttpStatusCode.OK);
        await using var sp = Build(vendor, settings: s => s["Providers:A:RateLimit:RequestsPerMinute"] = "10", services: s => s.AddSingleton<TimeProvider>(time));
        var client = sp.GetRequiredService<HttpProviderA>().Client;

        var calls = Enumerable.Range(0, 11).Select(_ => client.SendAsync(AccountA, Get, Ct)).ToArray();
        await Task.WhenAll(calls.Take(10));
        time.Advance(AccountRateLimiter.MaxWait);
        var responses = await Task.WhenAll(calls);

        Assert.True(vendor.Count <= 10, $"The vendor received {vendor.Count} requests.");
        var failed = Assert.Single(responses.OfType<VendorResponse.Failed>());
        Assert.Equal("rate-limited", failed.Failure.Reason);
        Assert.Equal(VendorFailureKind.Transient, failed.Failure.Kind);
        Assert.True(failed.Failure.RetryAfter > TimeSpan.Zero);
        Array.ForEach(responses, r => r.Dispose());
    }

    [Fact]
    public async Task Accounts_are_limited_separately()
    {
        using var limiter = new AccountRateLimiter(new FakeTimeProvider(Start));
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct));
        }

        Assert.Null(await limiter.AcquireAsync("carcloud", "acct_b", 10, Ct));
        Assert.Null(await limiter.AcquireAsync("heatpump", "acct_a", 10, Ct));
    }

    [Fact]
    public async Task A_token_returns_each_replenish_period()
    {
        var time = new FakeTimeProvider(Start);
        using var limiter = new AccountRateLimiter(time);
        for (var i = 0; i < 10; i++)
        {
            await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct);
        }

        Assert.Equal(TimeSpan.FromSeconds(6), await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct));

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct));
        Assert.Equal(TimeSpan.FromSeconds(6), await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct));

        time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct));
        }
    }

    [Fact]
    public async Task A_refused_call_reserves_nothing()
    {
        var time = new FakeTimeProvider(Start);
        using var limiter = new AccountRateLimiter(time);
        for (var i = 0; i < 10; i++)
        {
            await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct);
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct));
        }

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(await limiter.AcquireAsync("carcloud", "acct_a", 10, Ct));
    }

    [Fact]
    public async Task A_wait_shorter_than_the_limit_gets_the_next_token()
    {
        var time = new FakeTimeProvider(Start);
        using var limiter = new AccountRateLimiter(time);
        for (var i = 0; i < 60; i++)
        {
            await limiter.AcquireAsync("carcloud", "acct_a", 60, Ct);
        }

        var waiting = limiter.AcquireAsync("carcloud", "acct_a", 60, Ct).AsTask();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(await waiting);
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        var time = new FakeTimeProvider(Start);
        using var limiter = new AccountRateLimiter(time);
        for (var i = 0; i < 60; i++)
        {
            await limiter.AcquireAsync("carcloud", "acct_a", 60, Ct);
        }

        using var cancel = new CancellationTokenSource();

        var waiting = limiter.AcquireAsync("carcloud", "acct_a", 60, cancel.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
