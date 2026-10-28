using System.Text;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using CheapHours.Providers.Ocpp;
using CheapHours.Providers.Ocpp.Framing;
using FsCheck.Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppProviderTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> NoHeaders = [];
    private readonly OcppProvider _provider = new(new OcppSchemaValidator(), Options.Create(new OcppOptions()));

    /// <summary>Frames covering every kind of outcome the handler produces.</summary>
    public static TheoryData<string> SampleFrames => new(
        """[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP","firmwareVersion":"1.0"}]""",
        """[2,"b2","BootNotification",{"chargePointVendor":"","chargePointModel":"M"}]""",
        """[2,"b3","BootNotification",{"chargePointVendor":"V"}]""",
        """[2,"h1","Heartbeat",{}]""",
        """[2,"s1","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Charging","timestamp":"2026-10-23T09:59:00Z"}]""",
        """[2,"s2","StatusNotification",{"connectorId":2,"errorCode":"NoError","status":"Charging"}]""",
        """[2,"s3","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Sleeping"}]""",
        """[2,"a1","Authorize",{"idTag":"ANY"}]""",
        """[2,"m1","MeterValues",{"connectorId":1,"meterValue":[{"timestamp":"2026-10-23T09:59:00Z","sampledValue":[{"value":"12.5","unit":"kWh"},{"value":"7.4","measurand":"Power.Active.Import","unit":"kW"}]},{"timestamp":"2026-10-23T09:59:15Z","sampledValue":[{"value":"12.6","unit":"kWh"}]}]}]""",
        """[2,"m2","MeterValues",{"connectorId":1,"meterValue":[{"timestamp":"2026-10-23T09:59:00Z","sampledValue":[{"value":"twelve"}]}]}]""",
        """[2,"m3","MeterValues",{"connectorId":1,"meterValue":[]}]""",
        """[2,"t1","StartTransaction",{"connectorId":1,"idTag":"ANY","meterStart":0,"timestamp":"2026-10-23T09:58:00Z"}]""",
        """[2,"t2","StopTransaction",{"transactionId":7,"meterStop":15000,"timestamp":"2026-10-23T09:59:59Z"}]""",
        """[2,"f1","FlyToMoon",{}]""",
        """[3,"r1",{}]""",
        "{{{");

    private IReadOnlyList<DeviceEvent> Translate(string text, string? externalId = "CP001") =>
        _provider.Translate(new InboundMessage(externalId, Encoding.UTF8.GetBytes(text), NoHeaders, Received));

    [Theory]
    [MemberData(nameof(SampleFrames))]
    public void Translate_gives_the_same_events_the_gateway_records(string frame)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);

        var recorded = _provider.Handle(OcppProvider.Decode(bytes), "CP001", Received).Events;
        var translated = _provider.Translate(new InboundMessage("CP001", bytes, NoHeaders, Received));

        Assert.Equal(recorded, translated);
    }

    [Fact]
    public void Boot_notification_carries_the_hardware()
    {
        var events = Translate("""[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VirtualChargePoint","firmwareVersion":"1.0.0"}]""");

        var connected = Assert.IsType<DeviceEvent.Connected>(Assert.Single(events));
        Assert.Equal(new DeviceHardware("Solidstudio", "VirtualChargePoint", "1.0.0"), connected.Hardware);
    }

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
    [InlineData("""[2,"a1","Authorize",{"idTag":"ANY"}]""")]
    [InlineData("""[2,"m2","MeterValues",{"connectorId":2,"meterValue":[{"timestamp":"2026-10-23T09:59:00Z","sampledValue":[{"value":"5"}]}]}]""")]
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
            .AddProvider<OcppProvider, OcppOptions>(OcppOptions.SectionName);
        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        foreach (var hosted in sp.GetServices<IHostedService>())
        {
            await hosted.StartAsync(TestContext.Current.CancellationToken);
        }

        Assert.IsType<OcppProvider>(sp.GetRequiredService<IInboundTranslator>());
    }
}
