using System.Text.Json;
using CheapHours.Core.Devices;
using CheapHours.Providers.Ocpp.Framing;
using CheapHours.Providers.Ocpp.Messages;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppStatusMappingTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, DeviceStatus> Rows => new()
    {
        { "Available", DeviceStatus.Available },
        { "Preparing", DeviceStatus.Available },
        { "SuspendedEV", DeviceStatus.Available },
        { "SuspendedEVSE", DeviceStatus.Available },
        { "Finishing", DeviceStatus.Available },
        { "Reserved", DeviceStatus.Available },
        { "Charging", DeviceStatus.Charging },
        { "Faulted", DeviceStatus.Faulted },
        { "Unavailable", DeviceStatus.Unknown },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Status_maps_to_device_status(string ocppStatus, DeviceStatus expected)
    {
        Assert.Equal(expected, OcppStatusMapping.Map(ocppStatus));
    }

    [Fact]
    public void Every_status_in_the_official_schema_has_a_row()
    {
        using var stream = typeof(OcppSchemaValidator).Assembly.GetManifestResourceStream("Ocpp16.StatusNotification.json")!;
        using var schema = JsonDocument.Parse(stream);
        var official = schema.RootElement.GetProperty("properties").GetProperty("status").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToArray();

        var mapped = Rows.Select(r => r.Data.Item1).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(official, mapped);
        Assert.Equal(official, OcppStatusMapping.OcppStatuses.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Unmapped_status_throws_because_payloads_are_validated_first()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OcppStatusMapping.Map("Sleeping"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    public void Only_the_charge_point_and_its_first_connector_set_the_device_status(int connectorId, bool applies)
    {
        Assert.Equal(applies, OcppStatusMapping.AppliesToDevice(connectorId));
    }

    [Fact]
    public void Reported_timestamp_is_used_when_not_after_receipt()
    {
        Assert.Equal(Received.AddMinutes(-5), OcppStatusMapping.StatusTime("2026-10-23T09:55:00.000Z", Received));
    }

    [Fact]
    public void Reported_timestamp_with_an_offset_is_converted_to_utc()
    {
        var at = OcppStatusMapping.StatusTime("2026-10-23T11:55:00+02:00", Received);

        Assert.Equal(Received.AddMinutes(-5), at);
        Assert.Equal(TimeSpan.Zero, at.Offset);
    }

    [Fact]
    public void Future_timestamp_falls_back_to_receipt()
    {
        Assert.Equal(Received, OcppStatusMapping.StatusTime("2026-10-23T11:00:00.000Z", Received));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a time")]
    public void Missing_or_unreadable_timestamp_falls_back_to_receipt(string? reported)
    {
        Assert.Equal(Received, OcppStatusMapping.StatusTime(reported, Received));
    }
}
