using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CheapHours.UnitTests.Kit;

public sealed class ProviderRegistryTests
{
    [Fact]
    public async Task Valid_providers_start_and_resolve_under_each_interface_they_implement()
    {
        using var sp = Build(s => s
            .AddProvider<CarCloudProvider>("Providers:CarCloud")
            .AddProvider<HooksProvider>("Providers:Hooks"));

        await StartAsync(sp);

        Assert.Equal(["carcloud", "hooks"], sp.GetServices<IDeviceProvider>().Select(p => p.ProviderId));
        Assert.Same(sp.GetRequiredService<CarCloudProvider>(), sp.GetRequiredService<ICommandSender>());
        Assert.Single(sp.GetServices<IPollingProvider>());
        Assert.Single(sp.GetServices<IInboundTranslator>());
        Assert.Single(sp.GetServices<IWebhookVerifier>());
    }

    [Fact]
    public async Task Duplicate_provider_id_fails_startup_naming_the_id()
    {
        using var sp = Build(s => s
            .AddProvider<CarCloudProvider>("Providers:CarCloud")
            .AddProvider<SecondCarCloudProvider>("Providers:CarCloud"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(sp));

        Assert.Contains("provider id 'carcloud' is already used", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_configuration_section_fails_startup_naming_the_section()
    {
        using var sp = Build(s => s.AddProvider<CarCloudProvider>("Providers:HomeBattery"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(sp));

        Assert.Contains("'Providers:HomeBattery' does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sender_without_a_command_capability_fails_startup_naming_provider_and_capability()
    {
        using var sp = Build(s => s.AddProvider<MuteSender>("Providers:Mute"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(sp));

        Assert.Contains("MuteSender ('mute')", ex.Message, StringComparison.Ordinal);
        Assert.Contains("neither StartStop nor SetPowerLimit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Translator_without_pushes_events_fails_startup()
    {
        using var sp = Build(s => s.AddProvider<QuietTranslator>("Providers:Quiet"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(sp));

        Assert.Contains("QuietTranslator ('quiet')", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not declare PushesEvents", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_provider_id_fails_startup()
    {
        using var sp = Build(s => s.AddProvider<BadIdProvider>("Providers:Mute"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(sp));

        Assert.Contains("lowercase letters or digits", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_problems_are_reported_together()
    {
        using var sp = Build(s => s
            .AddProvider<MuteSender>("Providers:Nowhere")
            .AddProvider<QuietTranslator>("Providers:Quiet"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(sp));

        Assert.Equal(3, ex.Message.Split(Environment.NewLine).Count(l => l.StartsWith(" - ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Options_overload_binds_the_section()
    {
        using var sp = Build(s => s.AddProvider<CarCloudProvider, CarCloudOptions>("Providers:CarCloud"));

        await StartAsync(sp);

        Assert.Equal("https://carcloud.example", sp.GetRequiredService<IOptions<CarCloudOptions>>().Value.BaseUrl);
    }

    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:CarCloud:BaseUrl"] = "https://carcloud.example",
                ["Providers:Hooks:Secret"] = "s3cret",
                ["Providers:Mute:Enabled"] = "true",
                ["Providers:Quiet:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        register(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task StartAsync(IServiceProvider sp)
    {
        foreach (var hosted in sp.GetServices<IHostedService>())
        {
            await hosted.StartAsync(TestContext.Current.CancellationToken);
        }
    }

    public sealed class CarCloudOptions
    {
        public string BaseUrl { get; set; } = "";
    }

    private class CarCloudProvider : ICommandSender, IPollingProvider
    {
        public string ProviderId => "carcloud";

        public Capabilities Capabilities => Capabilities.ReadTelemetry | Capabilities.StartStop | Capabilities.ReadStateOfCharge;

        public Task<SendResult> SendAsync(DeviceCommand command, DeviceAddress address, CancellationToken cancellationToken) =>
            Task.FromResult<SendResult>(new SendResult.Accepted());

        public Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken) =>
            Task.FromResult<PollResult>(new PollResult.Completed([]));
    }

    private sealed class SecondCarCloudProvider : CarCloudProvider;

    private sealed class HooksProvider : IInboundTranslator, IWebhookVerifier
    {
        public string ProviderId => "hooks";

        public Capabilities Capabilities => Capabilities.ReadTelemetry | Capabilities.PushesEvents;

        public IReadOnlyList<DeviceEvent> Translate(InboundMessage message) => [];

        public bool Verify(InboundMessage message) => false;
    }

    private sealed class MuteSender : ICommandSender
    {
        public string ProviderId => "mute";

        public Capabilities Capabilities => Capabilities.ReadTelemetry;

        public Task<SendResult> SendAsync(DeviceCommand command, DeviceAddress address, CancellationToken cancellationToken) =>
            Task.FromResult<SendResult>(new SendResult.Rejected("capability-not-supported"));
    }

    private sealed class QuietTranslator : IInboundTranslator
    {
        public string ProviderId => "quiet";

        public Capabilities Capabilities => Capabilities.ReadTelemetry;

        public IReadOnlyList<DeviceEvent> Translate(InboundMessage message) => [];
    }

    private sealed class BadIdProvider : IDeviceProvider
    {
        public string ProviderId => "Car-Cloud";

        public Capabilities Capabilities => Capabilities.ReadTelemetry;
    }
}
