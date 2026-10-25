using System.Text.Json;
using System.Text.Json.Nodes;
using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppSchemaValidatorTests
{
    private static readonly OcppSchemaValidator Validator = new();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Valid_boot_notification_passes()
    {
        Assert.Null(Validator.ValidateRequest(OcppActions.BootNotification, Json("""{"chargePointVendor":"Solidstudio","chargePointModel":"VCP","firmwareVersion":"1.0.0"}""")));
    }

    [Fact]
    public void Missing_required_field_is_an_occurence_violation()
    {
        Assert.Equal(OcppErrorCode.OccurenceConstraintViolation, Validator.ValidateRequest(OcppActions.BootNotification, Json("""{"chargePointVendor":"Solidstudio"}""")));
    }

    [Fact]
    public void Wrong_type_is_a_type_violation()
    {
        Assert.Equal(OcppErrorCode.TypeConstraintViolation, Validator.ValidateRequest(OcppActions.StatusNotification, Json("""{"connectorId":"one","errorCode":"NoError","status":"Available"}""")));
    }

    [Fact]
    public void Value_outside_the_enum_is_a_property_violation()
    {
        Assert.Equal(OcppErrorCode.PropertyConstraintViolation, Validator.ValidateRequest(OcppActions.StatusNotification, Json("""{"connectorId":1,"errorCode":"NoError","status":"Sleeping"}""")));
    }

    [Fact]
    public void Too_long_string_is_a_property_violation()
    {
        var model = new string('m', 21);

        Assert.Equal(OcppErrorCode.PropertyConstraintViolation, Validator.ValidateRequest(OcppActions.BootNotification, Json("{\"chargePointVendor\":\"v\",\"chargePointModel\":\"" + model + "\"}")));
    }

    [Fact]
    public void Extra_property_is_a_property_violation()
    {
        Assert.Equal(OcppErrorCode.PropertyConstraintViolation, Validator.ValidateRequest(OcppActions.Heartbeat, Json("""{"extra":1}""")));
    }

    [Fact]
    public void Bad_timestamp_format_is_a_property_violation()
    {
        Assert.Equal(OcppErrorCode.PropertyConstraintViolation, Validator.ValidateRequest(OcppActions.StatusNotification, Json("""{"connectorId":1,"errorCode":"NoError","status":"Available","timestamp":"yesterday"}""")));
    }

    [Fact]
    public void Valid_timestamp_passes()
    {
        Assert.Null(Validator.ValidateRequest(OcppActions.StatusNotification, Json("""{"connectorId":1,"errorCode":"NoError","status":"Available","timestamp":"2026-10-23T10:00:00.000Z"}""")));
    }

    [Fact]
    public void Missing_field_wins_over_other_violations()
    {
        Assert.Equal(OcppErrorCode.OccurenceConstraintViolation, Validator.ValidateRequest(OcppActions.StatusNotification, Json("""{"connectorId":"one","status":"Sleeping"}""")));
    }

    [Theory]
    [InlineData(OcppActions.BootNotification)]
    [InlineData(OcppActions.Heartbeat)]
    [InlineData(OcppActions.StatusNotification)]
    public void Handled_actions_have_request_schemas(string action)
    {
        Assert.True(Validator.HasRequestSchema(action));
    }

    [Fact]
    public void Unhandled_actions_have_no_schema()
    {
        Assert.False(Validator.HasRequestSchema("MeterValues"));
    }

    [Fact]
    public void Responses_are_validated()
    {
        Assert.Null(Validator.ValidateResponse(OcppActions.BootNotification, new JsonObject { ["status"] = "Accepted", ["currentTime"] = "2026-10-23T10:00:00.000Z", ["interval"] = 60 }));
        Assert.Equal(OcppErrorCode.OccurenceConstraintViolation, Validator.ValidateResponse(OcppActions.Heartbeat, []));
    }

    [Theory]
    [InlineData("BootNotification", true)]
    [InlineData("MeterValues", true)]
    [InlineData("SignedUpdateFirmware", true)]
    [InlineData("FlyToMoon", false)]
    [InlineData("heartbeat", false)]
    public void Known_action_names(string action, bool known)
    {
        Assert.Equal(known, OcppActions.IsKnown(action));
    }
}
