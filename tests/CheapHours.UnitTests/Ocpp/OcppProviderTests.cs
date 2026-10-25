using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Ocpp;
using CheapHours.Providers.Ocpp.Framing;
using FsCheck.Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppProviderTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> NoHeaders = [];
    private readonly OcppProvider _provider = new(new OcppSchemaValidator());

    private IReadOnlyList<DeviceEvent> Translate(string text, string? externalId = "CP001") =>
        _provider.Translate(new InboundMessage(externalId, Encoding.UTF8.GetBytes(text), NoHeaders, Received));

    [Fact]
    public void Declares_its_identity()
    {
        Assert.Equal("ocpp", _provider.ProviderId);
        Assert.Equal(Capabilities.PushesEvents, _provider.Capabilities);
    }

    [Fact]
    public void Boot_notification_is_a_connection()
    {
        var events = Translate("""[2,"b1","BootNotification",{"chargePointVendor":"V","chargePointModel":"M"}]""");

        var connected = Assert.IsType<DeviceEvent.Connected>(Assert.Single(events));
        Assert.Equal("CP001", connected.ExternalId);
        Assert.Equal(Received, connected.At);
    }

    [Fact]
    public void Status_notification_is_a_status_change()
    {
        var events = Translate("""[2,"s1","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Charging"}]""");

        var changed = Assert.IsType<DeviceEvent.StatusChanged>(Assert.Single(events));
        Assert.Equal("CP001", changed.ExternalId);
        Assert.Equal(DeviceStatus.Charging, changed.Status);
        Assert.Equal(Received, changed.At);
    }

    [Theory]
    [InlineData("""[2,"h1","Heartbeat",{}]""")]
    [InlineData("""[2,"s2","StatusNotification",{"connectorId":2,"errorCode":"NoError","status":"Charging"}]""")]
    [InlineData("""[2,"m1","MeterValues",{"connectorId":1,"meterValue":[]}]""")]
    [InlineData("""[2,"f1","FlyToMoon",{}]""")]
    [InlineData("""[3,"r1",{}]""")]
    [InlineData("""[4,"e1","GenericError","",{}]""")]
    public void Other_valid_frames_give_no_events(string frame)
    {
        Assert.Empty(Translate(frame));
    }

    [Theory]
    [InlineData("{{{")]
    [InlineData("""[2,"b2","BootNotification",{"chargePointVendor":"V"}]""")]
    [InlineData("""[2,"s3","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Sleeping"}]""")]
    public void Malformed_input_gives_one_malformed_event(string frame)
    {
        var malformed = Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(Translate(frame)));

        Assert.Equal(Received, malformed.At);
    }

    [Fact]
    public void Invalid_utf8_is_malformed()
    {
        var events = _provider.Translate(new InboundMessage("CP001", new byte[] { 0x5B, 0xC3, 0x28, 0x5D }, NoHeaders, Received));

        Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(events));
    }

    [Fact]
    public void Missing_charge_point_id_is_malformed()
    {
        var events = Translate("""[2,"b1","BootNotification",{"chargePointVendor":"V","chargePointModel":"M"}]""", externalId: null);

        Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(events));
    }

    [Property(MaxTest = 1000)]
    public void Translating_any_bytes_never_throws(byte[]? body)
    {
        var events = _provider.Translate(new InboundMessage("CP001", body ?? [], NoHeaders, Received));

        Assert.NotNull(events);
    }

    [Fact]
    public async Task Registers_through_the_adapter_kit()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Providers:Ocpp:HeartbeatIntervalSeconds"] = "60" })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<OcppSchemaValidator>()
            .AddProvider<OcppProvider>("Providers:Ocpp");
        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        foreach (var hosted in sp.GetServices<IHostedService>())
        {
            await hosted.StartAsync(TestContext.Current.CancellationToken);
        }

        Assert.IsType<OcppProvider>(sp.GetRequiredService<IInboundTranslator>());
    }
}
