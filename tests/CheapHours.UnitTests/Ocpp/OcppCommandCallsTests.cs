using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Providers.Ocpp.Framing;
using CheapHours.Providers.Ocpp.Messages;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppCommandCallsTests
{
    private static readonly OcppSchemaValidator Schemas = new();

    public static TheoryData<CommandType, int?, int?> EveryCall() => new()
    {
        { CommandType.StartCharging, null, null },
        { CommandType.StartCharging, 7400, null },
        { CommandType.StopCharging, null, 4242 },
        { CommandType.SetPowerLimit, 3700, 4242 },
        { CommandType.SetPowerLimit, 3700, null },
    };

    // ocpp-gateway: "Calls match the official schemas".
    [Theory]
    [MemberData(nameof(EveryCall))]
    public void Every_built_payload_validates_against_the_official_request_schema(CommandType type, int? maxPowerW, int? transactionId)
    {
        var call = OcppCommandCalls.For(Command(type, maxPowerW), 1, "CHEAPHOURS", transactionId);

        Assert.True(Schemas.HasRequestSchema(call.Action), $"no schema for {call.Action}");
        Assert.Null(Schemas.ValidateRequest(call.Action, JsonSerializer.SerializeToElement(call.Payload)));
    }

    // ocpp-gateway: "Remote start".
    [Fact]
    public void Start_is_a_remote_start_on_the_connector_with_the_id_tag()
    {
        var call = OcppCommandCalls.For(Command(CommandType.StartCharging), 1, "CHEAPHOURS", null);

        Assert.Equal("RemoteStartTransaction", call.Action);
        Assert.Equal("""{"connectorId":1,"idTag":"CHEAPHOURS"}""", call.Payload.ToJsonString());
    }

    [Fact]
    public void Start_with_a_power_carries_a_tx_profile_in_watts()
    {
        var call = OcppCommandCalls.For(Command(CommandType.StartCharging, 7400), 1, "CHEAPHOURS", null);

        var profile = call.Payload["chargingProfile"]!;
        Assert.Equal("TxProfile", (string?)profile["chargingProfilePurpose"]);
        Assert.Null(profile["transactionId"]);
        Assert.Equal("W", (string?)profile["chargingSchedule"]!["chargingRateUnit"]);
        Assert.Equal(7400, (int)profile["chargingSchedule"]!["chargingSchedulePeriod"]![0]!["limit"]!);
    }

    [Fact]
    public void Stop_names_the_running_transaction()
    {
        var call = OcppCommandCalls.For(Command(CommandType.StopCharging), 1, "CHEAPHOURS", 4242);

        Assert.Equal(("RemoteStopTransaction", """{"transactionId":4242}"""), (call.Action, call.Payload.ToJsonString()));
    }

    [Fact]
    public void Stop_without_a_running_transaction_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => OcppCommandCalls.For(Command(CommandType.StopCharging), 1, "CHEAPHOURS", null));
    }

    [Theory]
    [InlineData(4242, "TxProfile", OcppCommandCalls.TxProfileId)]
    [InlineData(null, "TxDefaultProfile", OcppCommandCalls.TxDefaultProfileId)]
    public void Power_limit_targets_the_running_transaction_or_the_default_profile(int? transactionId, string purpose, int profileId)
    {
        var call = OcppCommandCalls.For(Command(CommandType.SetPowerLimit, 3700), 1, "CHEAPHOURS", transactionId);

        var profile = call.Payload["csChargingProfiles"]!;
        Assert.Equal("SetChargingProfile", call.Action);
        Assert.Equal(purpose, (string?)profile["chargingProfilePurpose"]);
        Assert.Equal(profileId, (int)profile["chargingProfileId"]!);
        Assert.Equal(transactionId, (int?)profile["transactionId"]);
    }

    // ocpp-gateway: "Charger accepts" and "Charger refuses".
    [Theory]
    [InlineData("Accepted", "accepted", null)]
    [InlineData("Rejected", "rejected", "Rejected")]
    [InlineData("NotSupported", "rejected", "NotSupported")]
    public void Call_result_status_maps_to_the_send_result(string status, string kind, string? reason)
    {
        var result = OcppCommandCalls.Interpret("uid-7", new OcppCallResult("uid-7", Json($$"""{"status":"{{status}}"}""")));

        Assert.Equal(kind, Kind(result));
        switch (result)
        {
            case SendResult.Accepted accepted:
                Assert.Equal("uid-7", accepted.VendorReference);
                break;
            case SendResult.Rejected rejected:
                Assert.Equal(reason, rejected.Reason);
                break;
        }
    }

    [Theory]
    [InlineData("NotImplemented", "rejected")]
    [InlineData("NotSupported", "rejected")]
    [InlineData("FormationViolation", "rejected")]
    [InlineData("PropertyConstraintViolation", "rejected")]
    [InlineData("TypeConstraintViolation", "rejected")]
    [InlineData("InternalError", "transient")]
    [InlineData("GenericError", "transient")]
    [InlineData("SecurityError", "transient")]
    [InlineData("ProtocolError", "transient")]
    [InlineData("OccurenceConstraintViolation", "transient")]
    public void Call_error_code_maps_to_rejected_or_transient(string code, string kind)
    {
        var result = OcppCommandCalls.Interpret("uid-8", new OcppCallError("uid-8", code, "details", Json("{}")));

        Assert.Equal(kind, Kind(result));
        Assert.Contains(code, result is SendResult.Rejected r ? r.Reason : ((SendResult.Transient)result).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_without_a_status_is_rejected()
    {
        Assert.IsType<SendResult.Rejected>(OcppCommandCalls.Interpret("uid-9", new OcppCallResult("uid-9", Json("{}"))));
    }

    private static DeviceCommand Command(CommandType type, int? maxPowerW = null) =>
        new(CommandId.Parse("cmd_ocpp"), DeviceId.Parse("dev_wb"), type, $"key_{type}", maxPowerW);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Kind(SendResult result) => result switch
    {
        SendResult.Accepted => "accepted",
        SendResult.Rejected => "rejected",
        _ => "transient",
    };
}
