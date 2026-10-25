using System.Text.Json;
using System.Text.Json.Nodes;
using CheapHours.Providers.Ocpp.Framing;
using FsCheck;
using FsCheck.Xunit;

namespace CheapHours.UnitTests.Ocpp;

public sealed class OcppFrameCodecTests
{
    [Fact]
    public void Call_is_decoded()
    {
        var frame = OcppFrameCodec.Decode("""[2,"abc-1","Heartbeat",{}]""");

        var call = Assert.IsType<OcppCall>(frame);
        Assert.Equal("abc-1", call.UniqueId);
        Assert.Equal("Heartbeat", call.Action);
        Assert.Equal(JsonValueKind.Object, call.Payload.ValueKind);
    }

    [Fact]
    public void Call_payload_outlives_the_parse()
    {
        var call = Assert.IsType<OcppCall>(OcppFrameCodec.Decode("""[2,"a","StatusNotification",{"connectorId":1}]"""));

        Assert.Equal(1, call.Payload.GetProperty("connectorId").GetInt32());
    }

    [Fact]
    public void Call_result_is_decoded()
    {
        var result = Assert.IsType<OcppCallResult>(OcppFrameCodec.Decode("""[3,"abc-2",{"status":"Accepted"}]"""));

        Assert.Equal("abc-2", result.UniqueId);
        Assert.Equal("Accepted", result.Payload.GetProperty("status").GetString());
    }

    [Fact]
    public void Call_error_is_decoded()
    {
        var error = Assert.IsType<OcppCallError>(OcppFrameCodec.Decode("""[4,"abc-3","NotSupported","nope",{}]"""));

        Assert.Equal("abc-3", error.UniqueId);
        Assert.Equal("NotSupported", error.ErrorCode);
        Assert.Equal("nope", error.ErrorDescription);
    }

    public static TheoryData<string, string?> Malformed => new()
    {
        { "hello", null },
        { "", null },
        { "{}", null },
        { "[]", null },
        { "\"text\"", null },
        { "[2]", null },
        { """[2,"abc-2","Heartbeat"]""", "abc-2" },
        { """[2,"abc-2","Heartbeat",{},{}]""", "abc-2" },
        { """[2,"abc-2","Heartbeat",[]]""", "abc-2" },
        { """[2,"abc-2","Heartbeat","{}"]""", "abc-2" },
        { """[2,"abc-2","",{}]""", "abc-2" },
        { """[2,"abc-2",7,{}]""", "abc-2" },
        { """[7,"abc-3","Heartbeat",{}]""", "abc-3" },
        { """["2","abc-4","Heartbeat",{}]""", "abc-4" },
        { """[2.5,"abc-4","Heartbeat",{}]""", "abc-4" },
        { """[2,"","Heartbeat",{}]""", null },
        { """[2,17,"Heartbeat",{}]""", null },
        { "[2,\"" + new string('x', 37) + "\",\"Heartbeat\",{}]", null },
        { """[3,"abc-5"]""", "abc-5" },
        { """[3,"abc-5",[]]""", "abc-5" },
        { """[4,"abc-6","NotSupported",{}]""", "abc-6" },
        { """[4,"abc-6",1,"d",{}]""", "abc-6" },
        { """[2,"abc-7","Heartbeat",{}""", null },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_frames_are_frame_errors_that_keep_a_readable_id(string text, string? expectedId)
    {
        var error = Assert.IsType<OcppFrameError>(OcppFrameCodec.Decode(text));

        Assert.Equal(expectedId, error.UniqueId);
    }

    [Fact]
    public void Too_deep_nesting_is_a_frame_error()
    {
        var deep = new string('[', 40) + new string(']', 40);

        Assert.IsType<OcppFrameError>(OcppFrameCodec.Decode("[2,\"abc\",\"Heartbeat\",{\"a\":" + deep + "}]"));
    }

    [Fact]
    public void A_36_character_id_is_accepted()
    {
        var id = new string('x', OcppFrameCodec.MaxUniqueIdLength);

        Assert.IsType<OcppCall>(OcppFrameCodec.Decode("[2,\"" + id + "\",\"Heartbeat\",{}]"));
    }

    [Fact]
    public void Formation_violation_echoes_a_readable_id()
    {
        var reply = OcppFrameCodec.WriteFormationViolation(new OcppFrameError("abc-2", "missing payload"));

        var parts = JsonNode.Parse(reply)!.AsArray();
        Assert.Equal(5, parts.Count);
        Assert.Equal(4, parts[0]!.GetValue<int>());
        Assert.Equal("abc-2", parts[1]!.GetValue<string>());
        Assert.Equal("FormationViolation", parts[2]!.GetValue<string>());
        Assert.Equal(JsonValueKind.Object, parts[4]!.GetValueKind());
    }

    [Fact]
    public void Formation_violation_without_an_id_uses_minus_one()
    {
        var reply = OcppFrameCodec.WriteFormationViolation(new OcppFrameError(null, "not JSON"));

        Assert.StartsWith("""[4,"-1","FormationViolation",""", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_description_never_echoes_the_frame()
    {
        const string marker = "SECRET-IDTAG-4711";
        var error = Assert.IsType<OcppFrameError>(OcppFrameCodec.Decode("[2,\"abc\",\"" + marker + "\"]"));

        var reply = OcppFrameCodec.WriteFormationViolation(error);

        Assert.DoesNotContain(marker, reply, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Call_result_is_written()
    {
        var reply = OcppFrameCodec.WriteCallResult("abc-1", new JsonObject { ["currentTime"] = "2026-10-23T10:00:00.000Z" });

        Assert.Equal("""[3,"abc-1",{"currentTime":"2026-10-23T10:00:00.000Z"}]""", reply);
    }

    [Fact]
    public void Call_error_is_written()
    {
        var reply = OcppFrameCodec.WriteCallError("abc-9", OcppErrorCode.OccurenceConstraintViolation, "A required field is missing.");

        Assert.Equal("""[4,"abc-9","OccurenceConstraintViolation","A required field is missing.",{}]""", reply);
    }

    [Property(MaxTest = 1000)]
    public void Decoding_any_text_never_throws_and_errors_have_one_well_formed_reply(string? text)
    {
        var frame = OcppFrameCodec.Decode(text ?? string.Empty);

        if (frame is OcppFrameError error)
        {
            var parts = JsonNode.Parse(OcppFrameCodec.WriteFormationViolation(error))!.AsArray();
            Assert.Equal(5, parts.Count);
            Assert.Equal("FormationViolation", parts[2]!.GetValue<string>());
        }
    }

    [Property(MaxTest = 500)]
    public void A_readable_id_is_echoed_whatever_the_rest_of_the_frame(NonEmptyString id, int type, string? tail)
    {
        var uniqueId = id.Get.Length > OcppFrameCodec.MaxUniqueIdLength ? id.Get[..OcppFrameCodec.MaxUniqueIdLength] : id.Get;
        var text = "[" + type.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + JsonSerializer.Serialize(uniqueId) + "," + JsonSerializer.Serialize(tail) + "]";

        if (OcppFrameCodec.Decode(text) is OcppFrameError error)
        {
            Assert.Equal(uniqueId, error.UniqueId);
        }
    }
}
