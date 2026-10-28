using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;

namespace CheapHours.UnitTests.Messaging;

public sealed class MessageTests
{
    private static readonly JsonSerializerOptions Options = CheapHoursJson.Options;

    public static TheoryData<string> MessageTypes => new(MessageSamples.All().Select(m => m.MessageType));

    [Fact]
    public void The_message_catalogue_is_exactly_the_specified_set_at_version_one()
    {
        var declared = typeof(Message).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Message)))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["CommandConfirmed", "CommandRequested", "CommandSent", "GatewayCommand", "GatewayCommandResult", "PricesPublished", "TelemetryReceived"],
            declared);
        Assert.Equal(declared, MessageSamples.All().Select(m => m.GetType().Name).Order(StringComparer.Ordinal));
        Assert.All(MessageSamples.All(), m => Assert.Equal(1, m.SchemaVersion));
        Assert.All(MessageSamples.All(), m => Assert.Equal(m.GetType().Name, m.MessageType));
    }

    [Theory]
    [MemberData(nameof(MessageTypes))]
    public void Every_message_carries_the_envelope(string messageType)
    {
        var sample = Sample(messageType);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(sample, sample.GetType(), Options));
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("messageId").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("occurredAt").ValueKind);
        Assert.Equal(messageType, root.GetProperty("messageType").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("traceparent", out _));
    }

    [Theory]
    [MemberData(nameof(MessageTypes))]
    public void Every_message_round_trips_without_loss(string messageType)
    {
        var original = Sample(messageType);
        var type = original.GetType();

        var json = JsonSerializer.Serialize(original, type, Options);
        var read = (Message)JsonSerializer.Deserialize(json, type, Options)!;

        Assert.Equal(json, JsonSerializer.Serialize(read, type, Options));
        if (original is not TelemetryReceived)
        {
            // Records compare lists by reference, so TelemetryReceived is checked through its JSON above.
            Assert.Equal(original, read);
        }
    }

    [Fact]
    public void Telemetry_reading_without_power_round_trips_as_unknown()
    {
        var original = (TelemetryReceived)Sample(TelemetryReceived.TypeName);

        var json = JsonSerializer.Serialize(original, Options);
        var read = JsonSerializer.Deserialize<TelemetryReceived>(json, Options)!;

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("readings")[2].GetProperty("powerW").ValueKind);
        Assert.Null(read.Readings[2].PowerW);
        Assert.Equal(12_467, read.Readings[2].EnergyWh);
    }

    [Fact]
    public void Send_result_round_trips_with_a_kind_discriminator()
    {
        var result = new GatewayCommandResult(MessageSamples.NewId(), MessageSamples.At, null, MessageSamples.Cmd, new SendResult.Rejected("capability-not-supported"));

        var json = JsonSerializer.Serialize(result, Options);
        var read = JsonSerializer.Deserialize<GatewayCommandResult>(json, Options)!;

        Assert.Contains("\"kind\":\"Rejected\"", json, StringComparison.Ordinal);
        Assert.Equal(new SendResult.Rejected("capability-not-supported"), read.Outcome);
    }

    [Fact]
    public void Gateway_command_is_routed_to_the_replica_holding_the_connection()
    {
        var command = (GatewayCommand)Sample(GatewayCommand.TypeName);

        Assert.Equal("gateway.gw-0.commands", command.Destination);
        Assert.Equal(MessageSamples.Cmd, command.CommandId);
        Assert.Equal(MessageSamples.Key, command.IdempotencyKey);
        Assert.DoesNotContain("destination", JsonSerializer.Serialize(command, Options), StringComparison.Ordinal);
    }

    [Fact]
    public void Gateway_result_is_correlated_by_command_id()
    {
        var result = (GatewayCommandResult)Sample(GatewayCommandResult.TypeName);

        Assert.Equal("cmd_01JB7Z8K2Q", result.CommandId.Value);
    }

    [Fact]
    public void Telemetry_needs_at_least_one_reading()
    {
        Assert.Throws<ArgumentException>(() => new TelemetryReceived(MessageSamples.NewId(), MessageSamples.At, null, MessageSamples.Wallbox, "ocpp", []));
    }

    [Fact]
    public void Empty_message_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new CommandConfirmed(Guid.Empty, MessageSamples.At, null, MessageSamples.Cmd, MessageSamples.Wallbox, MessageSamples.Key));
    }

    private static Message Sample(string messageType) => MessageSamples.All().Single(m => m.MessageType == messageType);
}
