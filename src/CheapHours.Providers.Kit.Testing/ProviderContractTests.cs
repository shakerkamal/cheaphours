using System.Diagnostics;
using System.Globalization;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Providers.Kit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Sdk;

namespace CheapHours.Providers.Kit.Testing;

/// <summary>
/// The shared provider contract suite (design §9): one fact per rule of the failure contract, run against a
/// fake of the provider's vendor. A provider subclasses it and supplies a harness. A rule that does not apply to
/// the provider's interaction style is reported as skipped with the reason, never as passed.
/// </summary>
/// <typeparam name="TProvider">The provider under test.</typeparam>
public abstract class ProviderContractTests<TProvider>
    where TProvider : class, IDeviceProvider
{
    /// <summary>The reason a provider gives for refusing a command it cannot carry out.</summary>
    public const string CapabilityNotSupported = "capability-not-supported";

    private static readonly string Name = typeof(TProvider).Name;

    /// <summary>Gets the test's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Readings come out in W, Wh, percent and UTC, exactly as the harness expects.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task Readings_are_normalised()
    {
        await using var run = await StartAsync();
        if (run.Provider is IPollingProvider polling)
        {
            var (externalId, expected) = await run.Harness.ArrangeDeviceWithReadingAsync();
            var completed = Completed(await PollAsync(run, polling));
            var readings = completed.Events.OfType<DeviceEvent.ReadingReported>().Select(r => r.Reading).Where(r => r.ExternalId == externalId).ToList();
            Assert.True(readings.Contains(expected), $"Expected the reading {Describe(expected)}, got: {string.Join("; ", readings.Select(Describe))}");
            return;
        }

        if (run.Provider is IInboundTranslator translator && run.Harness.KnownInput is { } known)
        {
            var readings = Translate(translator, known.Input).OfType<DeviceEvent.ReadingReported>().Select(r => r.Reading).ToList();
            Assert.True(readings.Contains(known.Expected), $"Expected the reading {Describe(known.Expected)}, got: {string.Join("; ", readings.Select(Describe))}");
            return;
        }

        Assert.Skip($"{Name} implements neither {nameof(IPollingProvider)} nor {nameof(IInboundTranslator)}");
    }

    /// <summary>A device the vendor no longer knows is reported as removed, never dropped.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task An_unknown_device_is_reported()
    {
        await using var run = await StartAsync();
        var polling = RequirePolling(run);

        var externalId = await run.Harness.ArrangeRemovedDeviceAsync();
        var completed = Completed(await PollAsync(run, polling));

        Assert.Contains(completed.Events, e => e is DeviceEvent.DeviceRemoved removed && removed.ExternalId == externalId);
    }

    /// <summary>Sending the same command twice passes the same idempotency key, so the vendor acts once.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task A_duplicate_key_acts_once()
    {
        await using var run = await StartAsync();
        var sender = RequireSender(run);
        var (externalId, _) = await run.Harness.ArrangeDeviceWithReadingAsync();
        var command = DeclaredCommand(sender, "contract:duplicate");

        var first = await SendAsync(run, sender, command, externalId);
        var second = await SendAsync(run, sender, command, externalId);

        Assert.IsType<SendResult.Accepted>(first);
        Assert.IsType<SendResult.Accepted>(second);
        var keys = run.Harness.CommandKeysSentFor(externalId);
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Equal(command.IdempotencyKey, key));
    }

    /// <summary>A 401 makes the kit refresh the token once and retry once.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task Unauthorized_refreshes_once_and_retries_once()
    {
        await using var run = await StartAsync();
        RequireOAuth(run);
        var (externalId, _) = await run.Harness.ArrangeDeviceWithReadingAsync();
        AssertSucceeded(await ActAsync(run, externalId));

        await run.Harness.ArrangeAccessTokenRejectedAsync();
        var tokenRequestsBefore = run.Harness.TokenRequestCount;
        var outcome = await ActAsync(run, externalId);

        AssertSucceeded(outcome);
        Assert.Equal(1, run.Harness.TokenRequestCount - tokenRequestsBefore);
    }

    /// <summary>A revoked refresh token fails clearly as reconnect-needed.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task A_revoked_refresh_token_fails_clearly()
    {
        await using var run = await StartAsync();
        RequireOAuth(run);
        var (externalId, _) = await run.Harness.ArrangeDeviceWithReadingAsync();
        AssertSucceeded(await ActAsync(run, externalId));

        await run.Harness.ArrangeRefreshRevokedAsync();
        var outcome = await ActAsync(run, externalId);

        switch (outcome)
        {
            case PollResult.Failed failed:
                Assert.Equal(VendorFailure.ReconnectNeededReason, failed.Reason);
                break;
            case SendResult.Rejected rejected:
                Assert.Equal(VendorFailure.ReconnectNeededReason, rejected.Reason);
                break;
            default:
                Assert.Fail($"Expected a reconnect-needed failure, got {outcome}.");
                break;
        }
    }

    /// <summary>A 429 is transient and carries the vendor's retry-after.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task Too_many_requests_is_transient_with_retry_after()
    {
        await using var run = await StartAsync();
        RequireHttpStyle(run);
        var (externalId, _) = await run.Harness.ArrangeDeviceWithReadingAsync();
        await run.Harness.ArrangeFailureAsync(429, TimeSpan.FromSeconds(30));

        foreach (var outcome in await ActEveryWayAsync(run, externalId))
        {
            Assert.Equal(TimeSpan.FromSeconds(30), AssertTransient(outcome));
        }
    }

    /// <summary>A 5xx is transient.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task Server_error_is_transient()
    {
        await using var run = await StartAsync();
        RequireHttpStyle(run);
        var (externalId, _) = await run.Harness.ArrangeDeviceWithReadingAsync();
        await run.Harness.ArrangeFailureAsync(503);

        foreach (var outcome in await ActEveryWayAsync(run, externalId))
        {
            AssertTransient(outcome);
        }
    }

    /// <summary>A vendor that does not answer in time is transient.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task A_timeout_is_transient()
    {
        await using var run = await StartAsync();
        RequireHttpStyle(run);
        var (externalId, _) = await run.Harness.ArrangeDeviceWithReadingAsync();
        await run.Harness.ArrangeDelayAsync(TimeSpan.FromSeconds(10)); // well past any harness's total timeout

        foreach (var outcome in await ActEveryWayAsync(run, externalId))
        {
            AssertTransient(outcome);
        }
    }

    /// <summary>A vendor validation error rejects the command with the vendor's reason.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task A_validation_error_is_rejected()
    {
        await using var run = await StartAsync();
        var sender = RequireSender(run);
        RequireHttp(run);
        var (externalId, _) = await run.Harness.ArrangeDeviceWithReadingAsync();
        await run.Harness.ArrangeFailureAsync(400, body: """{"error":"invalid_request"}""");

        var result = await SendAsync(run, sender, DeclaredCommand(sender, "contract:validation"), externalId);

        var rejected = Assert.IsType<SendResult.Rejected>(result);
        Assert.Contains("invalid_request", rejected.Reason, StringComparison.Ordinal);
    }

    /// <summary>A command needing a capability the provider does not declare is refused before any call.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task An_undeclared_capability_makes_no_call()
    {
        await using var run = await StartAsync();
        var sender = RequireSender(run);
        var undeclared = Enum.GetValues<CommandType>().Where(t => !sender.Capabilities.HasFlag(Command(t, "x").RequiredCapability)).ToList();
        if (undeclared.Count == 0)
        {
            Assert.Skip($"{Name} declares every command capability");
        }

        foreach (var type in undeclared)
        {
            var before = run.Harness.VendorRequestCount;
            var result = await SendAsync(run, sender, Command(type, $"contract:undeclared:{type}"), "contract-device");

            Assert.Equal(new SendResult.Rejected(CapabilityNotSupported), result);
            Assert.True(run.Harness.VendorRequestCount == before, $"{type} reached the vendor although {Name} does not declare {Command(type, "x").RequiredCapability}.");
        }
    }

    /// <summary>No vendor failure makes the provider throw.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task Vendor_failures_never_throw()
    {
        await using var run = await StartAsync();
        RequireHttpStyle(run);
        var (externalId, _) = await run.Harness.ArrangeDeviceWithReadingAsync();

        foreach (var status in new[] { 400, 404, 408, 409, 422, 429, 500, 502, 503 })
        {
            await run.Harness.ArrangeFailureAsync(status);
            foreach (var outcome in await ActEveryWayAsync(run, externalId))
            {
                Assert.True(outcome is PollResult or SendResult, $"Unexpected outcome {outcome} for {status}.");
            }
        }
    }

    /// <summary>Translation is pure and total: known input gives events, unhandled input none, malformed input one <see cref="DeviceEvent.MalformedInput"/>.</summary>
    /// <returns>A task that completes when checked.</returns>
    [Fact]
    public async Task Translation_is_pure_and_total()
    {
        await using var run = await StartAsync();
        if (run.Provider is not IInboundTranslator translator)
        {
            Assert.Skip($"{Name} does not implement {nameof(IInboundTranslator)}");
            return;
        }

        var harness = run.Harness;
        var known = harness.KnownInput ?? throw new InvalidOperationException("A translator harness must supply a known input.");
        var first = Translate(translator, known.Input);
        Assert.NotEmpty(first);
        Assert.Equal(first, Translate(translator, known.Input));
        Assert.Empty(Translate(translator, harness.UnhandledInput ?? throw new InvalidOperationException("A translator harness must supply an unhandled input.")));
        var malformed = Translate(translator, harness.MalformedInput ?? throw new InvalidOperationException("A translator harness must supply a malformed input."));
        Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(malformed));
    }

    /// <summary>Creates a fresh harness (and vendor fake) for one fact.</summary>
    /// <returns>The harness.</returns>
    protected abstract IProviderContractHarness CreateHarness();

    private static ServiceCollection Services(IProviderContractHarness harness, CapturingLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(harness.Configuration).Build());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton<IProviderAccountRepository>(new InMemoryProviderAccountRepository());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        harness.Register(services);
        return services;
    }

    private async Task<Run> StartAsync()
    {
        var harness = CreateHarness();
        var logs = new CapturingLoggerProvider();
        var sp = Services(harness, logs).BuildServiceProvider();
        foreach (var hosted in sp.GetServices<IHostedService>())
        {
            await hosted.StartAsync(Ct); // the registry validator: declared capabilities must match the interfaces
        }

        if (sp.GetService<ProviderAccountSeeder>() is { } seeder)
        {
            await seeder.SeedAsync(Ct);
        }

        return new Run(harness, sp, sp.GetRequiredService<TProvider>(), logs);
    }

    private static IPollingProvider RequirePolling(Run run) =>
        run.Provider as IPollingProvider ?? Skip<IPollingProvider>($"{Name} does not implement {nameof(IPollingProvider)}");

    private static ICommandSender RequireSender(Run run) =>
        run.Provider as ICommandSender ?? Skip<ICommandSender>($"{Name} does not implement {nameof(ICommandSender)}");

    private static void RequireHttpStyle(Run run)
    {
        if (run.Provider is not IPollingProvider and not ICommandSender)
        {
            Assert.Skip($"{Name} implements neither {nameof(IPollingProvider)} nor {nameof(ICommandSender)}");
        }

        RequireHttp(run);
    }

    private static void RequireHttp(Run run)
    {
        if (!run.Harness.UsesHttp)
        {
            Assert.Skip($"{Name} does not call a vendor over HTTP");
        }
    }

    private static void RequireOAuth(Run run)
    {
        RequireHttpStyle(run);
        if (!run.Harness.UsesOAuth)
        {
            Assert.Skip($"{Name} does not use OAuth");
        }
    }

    private static T Skip<T>(string reason)
    {
        Assert.Skip(reason);
        throw new UnreachableException();
    }

    private static DeviceCommand Command(CommandType type, string key) =>
        new(CommandId.Parse("cmd_contract"), DeviceId.Parse("dev_contract"), type, $"{key}:{type}", type == CommandType.SetPowerLimit ? 3700 : null);

    private static DeviceCommand DeclaredCommand(ICommandSender sender, string key) =>
        sender.Capabilities.HasFlag(Capabilities.StartStop) ? Command(CommandType.StartCharging, key) : Command(CommandType.SetPowerLimit, key);

    private static async Task<object> ActAsync(Run run, string externalId) =>
        run.Provider is IPollingProvider polling
            ? await PollAsync(run, polling)
            : await SendAsync(run, (ICommandSender)run.Provider, DeclaredCommand((ICommandSender)run.Provider, "contract:act"), externalId);

    private static async Task<List<object>> ActEveryWayAsync(Run run, string externalId)
    {
        var outcomes = new List<object>();
        if (run.Provider is IPollingProvider polling)
        {
            outcomes.Add(await PollAsync(run, polling));
        }

        if (run.Provider is ICommandSender sender)
        {
            outcomes.Add(await SendAsync(run, sender, DeclaredCommand(sender, "contract:every"), externalId));
        }

        return outcomes;
    }

    private static async Task<PollResult> PollAsync(Run run, IPollingProvider polling)
    {
        var account = run.Harness.Account ?? throw new InvalidOperationException("A polling harness must supply an account.");
        try
        {
            return await polling.PollAsync(account, Ct);
        }
        catch (Exception ex) when (ex is not XunitException)
        {
            Assert.Fail($"{Name}.PollAsync threw {ex.GetType().Name}: vendor failures must be returned as PollResult.Failed, never thrown.");
            throw;
        }
    }

    private static async Task<SendResult> SendAsync(Run run, ICommandSender sender, DeviceCommand command, string externalId)
    {
        try
        {
            return await sender.SendAsync(command, new DeviceAddress(externalId, run.Harness.Account), Ct);
        }
        catch (Exception ex) when (ex is not XunitException)
        {
            Assert.Fail($"{Name}.SendAsync threw {ex.GetType().Name}: vendor failures must be returned as a SendResult, never thrown.");
            throw;
        }
    }

    private static IReadOnlyList<DeviceEvent> Translate(IInboundTranslator translator, InboundMessage message)
    {
        try
        {
            return translator.Translate(message);
        }
        catch (Exception ex) when (ex is not XunitException)
        {
            Assert.Fail($"{Name}.Translate threw {ex.GetType().Name}: translation must be total.");
            throw;
        }
    }

    private static PollResult.Completed Completed(PollResult result) =>
        result as PollResult.Completed ?? throw FailException.ForFailure($"Expected a completed poll, got {result}.");

    private static void AssertSucceeded(object outcome) =>
        Assert.True(outcome is PollResult.Completed or SendResult.Accepted, $"Expected success, got {outcome}.");

    private static TimeSpan? AssertTransient(object outcome) => outcome switch
    {
        PollResult.Failed failed => failed.RetryAfter,
        SendResult.Transient transient => transient.RetryAfter,
        _ => throw FailException.ForFailure($"Expected a transient failure, got {outcome}."),
    };

    private static string Describe(DeviceReading r) =>
        $"{r.ExternalId} at {r.MeasurementTime:O}: {r.PowerW?.ToString(CultureInfo.InvariantCulture) ?? "?"} W, {r.EnergyWh} Wh, {r.SocPercent?.ToString(CultureInfo.InvariantCulture) ?? "?"} %";

    private sealed class Run(IProviderContractHarness harness, ServiceProvider services, TProvider provider, CapturingLoggerProvider logs) : IAsyncDisposable
    {
        public IProviderContractHarness Harness { get; } = harness;

        public TProvider Provider { get; } = provider;

        public CapturingLoggerProvider Logs { get; } = logs;

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }
}
