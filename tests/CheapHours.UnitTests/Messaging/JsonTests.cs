using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;

namespace CheapHours.UnitTests.Messaging;

public sealed class JsonTests
{
    private static readonly JsonSerializerOptions Options = CheapHoursJson.Options;

    [Fact]
    public void Enum_is_written_as_its_pascal_case_name()
    {
        var command = new DeviceCommand(MessageSamples.Cmd, MessageSamples.Wallbox, CommandType.StartCharging, MessageSamples.Key);

        var json = JsonSerializer.Serialize(command, Options);

        Assert.Contains("\"type\":\"StartCharging\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Enum_given_as_a_number_is_refused()
    {
        const string json = """{"id":"cmd_1","deviceId":"dev_1","type":0,"idempotencyKey":"k"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeviceCommand>(json, Options));
    }

    [Fact]
    public void Fields_are_camel_case_and_ids_are_plain_strings()
    {
        var json = JsonSerializer.Serialize(MessageSamples.CommandRequested(), Options);

        Assert.Contains("\"commandId\":\"cmd_01JB7Z8K2Q\"", json, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Id_with_the_wrong_prefix_is_refused()
    {
        var json = JsonSerializer.Serialize(MessageSamples.CommandRequested(), Options)
            .Replace("\"dev_wallbox_01\"", "\"plan_1\"", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CommandRequested>(json, Options));
    }

    [Fact]
    public void Timestamps_are_written_in_utc_with_a_z_suffix()
    {
        var json = JsonSerializer.Serialize(MessageSamples.CommandRequested(), Options);

        Assert.Contains("\"occurredAt\":\"2026-10-12T01:00:00Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamp_with_another_offset_is_refused_on_read()
    {
        var json = JsonSerializer.Serialize(MessageSamples.CommandRequested(), Options)
            .Replace("2026-10-12T01:00:00Z", "2026-10-12T02:00:00+01:00", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CommandRequested>(json, Options));
    }

    [Fact]
    public void Non_utc_occurrence_time_is_refused_on_creation()
    {
        var local = new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.FromHours(1));

        Assert.Throws<ArgumentException>(() => new CommandRequested(MessageSamples.NewId(), local, null, MessageSamples.Cmd, MessageSamples.Wallbox, MessageSamples.Key));
    }

    [Fact]
    public void Unknown_field_is_ignored_and_known_fields_survive()
    {
        var original = MessageSamples.CommandRequested();
        var json = JsonSerializer.Serialize(original, Options).Insert(1, "\"addedInVersionOnePointFive\":{\"x\":1},");

        var read = JsonSerializer.Deserialize<CommandRequested>(json, Options);

        Assert.Equal(original, read);
    }

    [Fact]
    public void Shared_options_cannot_be_changed()
    {
        Assert.True(Options.IsReadOnly);
    }
}
