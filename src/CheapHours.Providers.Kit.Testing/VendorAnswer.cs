using System.Text.Json;

namespace CheapHours.Providers.Kit.Testing;

/// <summary>How the vendor fake answers a request.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The JSON body, if any.</param>
public sealed record VendorAnswer(int Status, string? Body = null)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>An answer with a JSON body serialised from <paramref name="value"/> (camelCase).</summary>
    /// <param name="value">The body.</param>
    /// <param name="status">The status, 200 by default.</param>
    /// <returns>The answer.</returns>
    public static VendorAnswer Json(object value, int status = 200) => new(status, JsonSerializer.Serialize(value, Web));

    /// <summary>An answer with a raw JSON body.</summary>
    /// <param name="status">The status.</param>
    /// <param name="json">The body.</param>
    /// <returns>The answer.</returns>
    public static VendorAnswer Raw(int status, string json) => new(status, json);
}
