using System.Text.Json.Nodes;
using CheapHours.Core.Devices;
using CheapHours.Providers.Ocpp.Framing;
using CheapHours.Providers.Ocpp.Messages;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppCallHandlerTests
{
    private static readonly OcppSchemaValidator Validator = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 10, 0, 0, 123, TimeSpan.Zero);
    private readonly OcppCallHandler _handler = new(Validator, TimeSpan.FromSeconds(60));

    private OcppOutcome Handle(string frame) => _handler.Handle(OcppFrameCodec.Decode(frame), Now);

    private static JsonArray Parts(OcppOutcome outcome) => JsonNode.Parse(outcome.Reply!)!.AsArray();

    private static JsonObject ResultPayload(OcppOutcome outcome, string expectedId)
    {
        var parts = Parts(outcome);
        Assert.Equal(3, parts[0]!.GetValue<int>());
        Assert.Equal(expectedId, parts[1]!.GetValue<string>());
        return parts[2]!.AsObject();
    }

    private static void AssertCallError(OcppOutcome outcome, string expectedId, OcppErrorCode code)
    {
        var parts = Parts(outcome);
        Assert.Equal(4, parts[0]!.GetValue<int>());
        Assert.Equal(expectedId, parts[1]!.GetValue<string>());
        Assert.Equal(code.ToString(), parts[2]!.GetValue<string>());
        Assert.Equal(code, outcome.Error);
        Assert.Null(outcome.Effect);
    }

    [Fact]
    public void Boot_notification_is_accepted_with_time_and_interval()
    {
        var outcome = Handle("""[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP","firmwareVersion":"1.0"}]""");

        var payload = ResultPayload(outcome, "b1");
        Assert.Equal("Accepted", payload["status"]!.GetValue<string>());
        Assert.Equal("2026-10-23T10:00:00.123Z", payload["currentTime"]!.GetValue<string>());
        Assert.Equal(60, payload["interval"]!.GetValue<int>());
        Assert.Null(Validator.ValidateResponse(OcppActions.BootNotification, payload));
        Assert.Equal(new BootReported("Solidstudio", "VCP", "1.0"), outcome.Effect);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void Boot_without_firmware_reports_none()
    {
        var outcome = Handle("""[2,"b2","BootNotification",{"chargePointVendor":"V","chargePointModel":"M"}]""");

        Assert.Equal(new BootReported("V", "M", null), outcome.Effect);
    }

    [Fact]
    public void Heartbeat_answers_the_current_time()
    {
        var outcome = Handle("""[2,"abc-1","Heartbeat",{}]""");

        var payload = ResultPayload(outcome, "abc-1");
        Assert.Equal("2026-10-23T10:00:00.123Z", payload["currentTime"]!.GetValue<string>());
        Assert.Null(Validator.ValidateResponse(OcppActions.Heartbeat, payload));
        Assert.Null(outcome.Effect);
    }

    [Fact]
    public void Status_notification_is_acknowledged_and_sets_the_status()
    {
        var outcome = Handle("""[2,"s1","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Available"}]""");

        var payload = ResultPayload(outcome, "s1");
        Assert.Empty(payload);
        Assert.Null(Validator.ValidateResponse(OcppActions.StatusNotification, payload));
        Assert.Equal(new StatusReported(DeviceStatus.Available, Now), outcome.Effect);
    }

    [Fact]
    public void Status_notification_uses_a_past_timestamp()
    {
        var outcome = Handle("""[2,"s2","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Charging","timestamp":"2026-10-23T09:59:00.000Z"}]""");

        Assert.Equal(new StatusReported(DeviceStatus.Charging, Now.AddMilliseconds(-123).AddMinutes(-1)), outcome.Effect);
    }

    [Fact]
    public void Status_for_a_second_connector_is_acknowledged_without_effect()
    {
        var outcome = Handle("""[2,"s3","StatusNotification",{"connectorId":2,"errorCode":"NoError","status":"Faulted"}]""");

        Assert.Empty(ResultPayload(outcome, "s3"));
        Assert.Null(outcome.Effect);
    }

    [Fact]
    public void Status_for_an_out_of_range_connector_is_acknowledged_without_effect()
    {
        var outcome = Handle("""[2,"s6","StatusNotification",{"connectorId":99999999999,"errorCode":"NoError","status":"Faulted"}]""");

        Assert.Empty(ResultPayload(outcome, "s6"));
        Assert.Null(outcome.Effect);
    }

    [Fact]
    public void Missing_required_field_is_refused_without_effect()
    {
        AssertCallError(Handle("""[2,"b3","BootNotification",{"chargePointVendor":"V"}]"""), "b3", OcppErrorCode.OccurenceConstraintViolation);
    }

    [Fact]
    public void Wrong_type_is_refused()
    {
        AssertCallError(Handle("""[2,"s4","StatusNotification",{"connectorId":"one","errorCode":"NoError","status":"Available"}]"""), "s4", OcppErrorCode.TypeConstraintViolation);
    }

    [Fact]
    public void Value_outside_the_enum_is_refused()
    {
        AssertCallError(Handle("""[2,"s5","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Sleeping"}]"""), "s5", OcppErrorCode.PropertyConstraintViolation);
    }

    [Fact]
    public void Known_but_unhandled_action_is_not_supported()
    {
        AssertCallError(Handle("""[2,"m1","MeterValues",{"connectorId":1,"meterValue":[]}]"""), "m1", OcppErrorCode.NotSupported);
    }

    [Fact]
    public void Unknown_action_is_not_implemented()
    {
        AssertCallError(Handle("""[2,"f1","FlyToMoon",{}]"""), "f1", OcppErrorCode.NotImplemented);
    }

    [Fact]
    public void Malformed_frame_is_a_formation_violation()
    {
        AssertCallError(Handle("hello"), "-1", OcppErrorCode.FormationViolation);
    }

    [Theory]
    [InlineData("""[3,"x1",{}]""")]
    [InlineData("""[4,"x2","GenericError","",{}]""")]
    public void Results_and_errors_from_the_charger_get_no_answer(string frame)
    {
        var outcome = Handle(frame);

        Assert.Null(outcome.Reply);
        Assert.Null(outcome.Effect);
    }

    [Fact]
    public void Internal_error_reply_has_no_details()
    {
        var parts = JsonNode.Parse(OcppCallHandler.InternalErrorReply("b9"))!.AsArray();

        Assert.Equal("b9", parts[1]!.GetValue<string>());
        Assert.Equal("InternalError", parts[2]!.GetValue<string>());
        Assert.Empty(parts[4]!.AsObject());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(0.5)]
    public void Interval_must_be_positive_whole_seconds(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcppCallHandler(Validator, TimeSpan.FromSeconds(seconds)));
    }
}
