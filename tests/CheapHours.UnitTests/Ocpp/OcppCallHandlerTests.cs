using System.Text.Json.Nodes;
using CheapHours.Core.Devices;
using CheapHours.Providers.Ocpp.Framing;
using CheapHours.Providers.Ocpp.Messages;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppCallHandlerTests
{
    private const string ChargePoint = "CP001";
    private static readonly OcppSchemaValidator Validator = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 10, 0, 0, 123, TimeSpan.Zero);
    private readonly OcppCallHandler _handler = new(Validator, TimeSpan.FromSeconds(60));

    private OcppOutcome Handle(string frame) => _handler.Handle(OcppFrameCodec.Decode(frame), ChargePoint, Now);

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
    }

    private static void AssertMalformed(OcppOutcome outcome) =>
        Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(outcome.Events));

    [Fact]
    public void Boot_notification_is_accepted_with_time_and_interval()
    {
        var outcome = Handle("""[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP","firmwareVersion":"1.0"}]""");

        var payload = ResultPayload(outcome, "b1");
        Assert.Equal("Accepted", payload["status"]!.GetValue<string>());
        Assert.Equal("2026-10-23T10:00:00.123Z", payload["currentTime"]!.GetValue<string>());
        Assert.Equal(60, payload["interval"]!.GetValue<int>());
        Assert.Null(Validator.ValidateResponse(OcppActions.BootNotification, payload));
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void Boot_notification_is_a_connection_with_hardware()
    {
        var outcome = Handle("""[2,"b1","BootNotification",{"chargePointVendor":"Solidstudio","chargePointModel":"VCP","firmwareVersion":"1.0"}]""");

        var connected = Assert.IsType<DeviceEvent.Connected>(Assert.Single(outcome.Events));
        Assert.Equal(ChargePoint, connected.ExternalId);
        Assert.Equal(Now, connected.At);
        Assert.Equal(new DeviceHardware("Solidstudio", "VCP", "1.0"), connected.Hardware);
    }

    [Fact]
    public void Boot_without_firmware_reports_none()
    {
        var outcome = Handle("""[2,"b2","BootNotification",{"chargePointVendor":"V","chargePointModel":"M"}]""");

        var connected = Assert.IsType<DeviceEvent.Connected>(Assert.Single(outcome.Events));
        Assert.Equal(new DeviceHardware("V", "M"), connected.Hardware);
    }

    [Fact]
    public void Boot_with_an_empty_vendor_connects_without_hardware()
    {
        var outcome = Handle("""[2,"b4","BootNotification",{"chargePointVendor":"","chargePointModel":"M"}]""");

        Assert.Equal("Accepted", ResultPayload(outcome, "b4")["status"]!.GetValue<string>());
        var connected = Assert.IsType<DeviceEvent.Connected>(Assert.Single(outcome.Events));
        Assert.Null(connected.Hardware);
    }

    [Fact]
    public void Heartbeat_answers_the_current_time()
    {
        var outcome = Handle("""[2,"abc-1","Heartbeat",{}]""");

        var payload = ResultPayload(outcome, "abc-1");
        Assert.Equal("2026-10-23T10:00:00.123Z", payload["currentTime"]!.GetValue<string>());
        Assert.Null(Validator.ValidateResponse(OcppActions.Heartbeat, payload));
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public void Status_notification_is_acknowledged_and_sets_the_status()
    {
        var outcome = Handle("""[2,"s1","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Available"}]""");

        var payload = ResultPayload(outcome, "s1");
        Assert.Empty(payload);
        Assert.Null(Validator.ValidateResponse(OcppActions.StatusNotification, payload));
        Assert.Equal(new DeviceEvent.StatusChanged(ChargePoint, Now, DeviceStatus.Available), Assert.Single(outcome.Events));
    }

    [Fact]
    public void Status_notification_uses_a_past_timestamp()
    {
        var outcome = Handle("""[2,"s2","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Charging","timestamp":"2026-10-23T09:59:00.000Z"}]""");

        Assert.Equal(
            new DeviceEvent.StatusChanged(ChargePoint, Now.AddMilliseconds(-123).AddMinutes(-1), DeviceStatus.Charging),
            Assert.Single(outcome.Events));
    }

    [Fact]
    public void Status_for_a_second_connector_is_acknowledged_without_events()
    {
        var outcome = Handle("""[2,"s3","StatusNotification",{"connectorId":2,"errorCode":"NoError","status":"Faulted"}]""");

        Assert.Empty(ResultPayload(outcome, "s3"));
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public void Status_for_an_out_of_range_connector_is_acknowledged_without_events()
    {
        var outcome = Handle("""[2,"s6","StatusNotification",{"connectorId":99999999999,"errorCode":"NoError","status":"Faulted"}]""");

        Assert.Empty(ResultPayload(outcome, "s6"));
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public void Missing_required_field_is_refused_as_malformed()
    {
        var outcome = Handle("""[2,"b3","BootNotification",{"chargePointVendor":"V"}]""");

        AssertCallError(outcome, "b3", OcppErrorCode.OccurenceConstraintViolation);
        AssertMalformed(outcome);
    }

    [Fact]
    public void Wrong_type_is_refused_as_malformed()
    {
        var outcome = Handle("""[2,"s4","StatusNotification",{"connectorId":"one","errorCode":"NoError","status":"Available"}]""");

        AssertCallError(outcome, "s4", OcppErrorCode.TypeConstraintViolation);
        AssertMalformed(outcome);
    }

    [Fact]
    public void Value_outside_the_enum_is_refused_as_malformed()
    {
        var outcome = Handle("""[2,"s5","StatusNotification",{"connectorId":1,"errorCode":"NoError","status":"Sleeping"}]""");

        AssertCallError(outcome, "s5", OcppErrorCode.PropertyConstraintViolation);
        AssertMalformed(outcome);
    }

    [Fact]
    public void Known_but_unhandled_action_is_not_supported()
    {
        var outcome = Handle("""[2,"a1","Authorize",{"idTag":"ANY"}]""");

        AssertCallError(outcome, "a1", OcppErrorCode.NotSupported);
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public void Unknown_action_is_not_implemented()
    {
        var outcome = Handle("""[2,"f1","FlyToMoon",{}]""");

        AssertCallError(outcome, "f1", OcppErrorCode.NotImplemented);
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public void Malformed_frame_is_a_formation_violation()
    {
        var outcome = Handle("hello");

        AssertCallError(outcome, "-1", OcppErrorCode.FormationViolation);
        AssertMalformed(outcome);
    }

    [Fact]
    public void Meter_values_are_acknowledged_and_become_readings()
    {
        var outcome = Handle("""[2,"m1","MeterValues",{"connectorId":1,"transactionId":7,"meterValue":[{"timestamp":"2026-10-23T10:00:00.000Z","sampledValue":[{"value":"12.5","measurand":"Energy.Active.Import.Register","unit":"kWh"}]}]}]""");

        var payload = ResultPayload(outcome, "m1");
        Assert.Empty(payload);
        Assert.Null(Validator.ValidateResponse(OcppActions.MeterValues, payload));
        var reading = Assert.IsType<DeviceEvent.ReadingReported>(Assert.Single(outcome.Events)).Reading;
        Assert.Equal(ChargePoint, reading.ExternalId);
        Assert.Equal(12_500, reading.EnergyWh);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero), reading.MeasurementTime);
    }

    [Fact]
    public void Every_usable_meter_entry_is_a_reading_in_frame_order()
    {
        var outcome = Handle(
            """[2,"m2","MeterValues",{"connectorId":0,"meterValue":[""" +
            """{"timestamp":"2026-10-23T09:59:00Z","sampledValue":[{"value":"100"}]},""" +
            """{"timestamp":"2026-10-23T09:59:15Z","sampledValue":[{"value":"110"}]},""" +
            """{"timestamp":"2026-10-23T09:59:30Z","sampledValue":[{"value":"120"}]}]}]""");

        Assert.Equal([100L, 110L, 120L], outcome.Events.Cast<DeviceEvent.ReadingReported>().Select(e => e.Reading.EnergyWh));
    }

    [Fact]
    public void Meter_values_for_another_connector_are_acknowledged_without_events()
    {
        var outcome = Handle("""[2,"m3","MeterValues",{"connectorId":2,"meterValue":[{"timestamp":"2026-10-23T09:59:00Z","sampledValue":[{"value":"100"}]}]}]""");

        Assert.Empty(ResultPayload(outcome, "m3"));
        Assert.Empty(outcome.Events);
    }

    [Fact]
    public void Meter_values_without_a_usable_entry_are_acknowledged_as_malformed()
    {
        var outcome = Handle("""[2,"m4","MeterValues",{"connectorId":1,"meterValue":[{"timestamp":"2026-10-23T09:59:00Z","sampledValue":[{"value":"twelve"}]}]}]""");

        Assert.Empty(ResultPayload(outcome, "m4"));
        Assert.Null(outcome.Error);
        var malformed = Assert.IsType<DeviceEvent.MalformedInput>(Assert.Single(outcome.Events));
        Assert.Equal(OcppCallHandler.NoUsableMeterValueReason, malformed.Reason);
    }

    [Fact]
    public void Start_transaction_is_accepted_with_a_transaction_id_and_a_reading()
    {
        var outcome = Handle("""[2,"t1","StartTransaction",{"connectorId":1,"idTag":"ANY","meterStart":0,"timestamp":"2026-10-23T09:58:00.000Z"}]""");

        var payload = ResultPayload(outcome, "t1");
        Assert.Null(Validator.ValidateResponse(OcppActions.StartTransaction, payload));
        Assert.Equal("Accepted", payload["idTagInfo"]!["status"]!.GetValue<string>());
        Assert.Equal(OcppCallHandler.TransactionIdAt(Now), payload["transactionId"]!.GetValue<int>());
        Assert.True(payload["transactionId"]!.GetValue<int>() > 0);
        var reading = Assert.IsType<DeviceEvent.ReadingReported>(Assert.Single(outcome.Events)).Reading;
        Assert.Equal(0, reading.EnergyWh);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 9, 58, 0, TimeSpan.Zero), reading.MeasurementTime);
    }

    [Fact]
    public void Transactions_started_a_second_apart_get_different_ids()
    {
        Assert.NotEqual(OcppCallHandler.TransactionIdAt(Now), OcppCallHandler.TransactionIdAt(Now.AddSeconds(1)));
        Assert.Equal(1, OcppCallHandler.TransactionIdAt(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Stop_transaction_is_acknowledged_with_its_final_reading()
    {
        var outcome = Handle("""[2,"t2","StopTransaction",{"transactionId":7,"meterStop":15000,"timestamp":"2026-10-23T09:59:59.000Z"}]""");

        var payload = ResultPayload(outcome, "t2");
        Assert.Null(Validator.ValidateResponse(OcppActions.StopTransaction, payload));
        Assert.Equal("Accepted", payload["idTagInfo"]!["status"]!.GetValue<string>());
        var reading = Assert.IsType<DeviceEvent.ReadingReported>(Assert.Single(outcome.Events)).Reading;
        Assert.Equal(15_000, reading.EnergyWh);
    }

    [Fact]
    public void Stop_transaction_data_become_readings_before_the_final_one()
    {
        var outcome = Handle(
            """[2,"t3","StopTransaction",{"transactionId":7,"meterStop":15000,"timestamp":"2026-10-23T09:59:59Z","transactionData":[""" +
            """{"timestamp":"2026-10-23T09:59:30Z","sampledValue":[{"value":"14.9","unit":"kWh"}]}]}]""");

        Assert.Equal([14_900L, 15_000L], outcome.Events.Cast<DeviceEvent.ReadingReported>().Select(e => e.Reading.EnergyWh));
    }

    [Theory]
    [InlineData("""[3,"x1",{}]""")]
    [InlineData("""[4,"x2","GenericError","",{}]""")]
    public void Results_and_errors_from_the_charger_get_no_answer(string frame)
    {
        var outcome = Handle(frame);

        Assert.Null(outcome.Reply);
        Assert.Empty(outcome.Events);
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
