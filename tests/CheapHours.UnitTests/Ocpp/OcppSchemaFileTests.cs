using System.Security.Cryptography;
using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.UnitTests.Ocpp;

// The OCPP schemas are third-party files under CC BY-ND 4.0: they must stay exactly as published.
public sealed class OcppSchemaFileTests
{
    public static TheoryData<string, string> Published => new()
    {
        { "BootNotification.json", "57cedc8c00715439587b6ae71efc4ddc9904048c83d152e7f0f1f60283bc525b" },
        { "BootNotificationResponse.json", "abafc062cce4f3d2bad0c7c92ca72c13d69d8f0a7c7f549634848b8cc21d46f9" },
        { "Heartbeat.json", "83c5b67c13328fcafe90d3ca024e50f82d035f6ef481d917074cce728dfe459f" },
        { "HeartbeatResponse.json", "f4df4789381766869c110245854bc47173973684352ed67382ffc9ded018bc21" },
        { "StatusNotification.json", "9db6aefd3af685d52cf26626c99b61909cad10cff233e4b1e0fd71b3ba0a989d" },
        { "StatusNotificationResponse.json", "e0def785787a562c4b33aa81f7f213bcf4146ab74fec40e14143d326db49afcd" },
    };

    [Theory]
    [MemberData(nameof(Published))]
    public void Embedded_schema_is_unchanged(string file, string sha256)
    {
        using var stream = typeof(OcppSchemaValidator).Assembly.GetManifestResourceStream("Ocpp16." + file);

        Assert.NotNull(stream);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
}
