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
        { "MeterValues.json", "dc1d80817911a4de2e34d35692447dc13da7ebd4d900472d2438ff7c568b243e" },
        { "MeterValuesResponse.json", "1da13dc43ea5cc888a6a656d942044367b204942acd4f2068a160f037ecb8f0f" },
        { "StartTransaction.json", "d0adc9572cbe968ae3d689f4bf4ef0e27e3097a85a6941875283b27e78836ff5" },
        { "StartTransactionResponse.json", "9330ff054409c0ea8cc9bca1cd40e3b3352e0d8b7f996d765f26fab6d0129540" },
        { "StopTransaction.json", "9473743d54e33d222a4d3dd5042f3c39034ae1d8939fa0fd9aa38d0a561b557f" },
        { "StopTransactionResponse.json", "8cad55b0c51cb044923fee88959698acd1bb68c656d774f48bf578b8379cc0f9" },
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
