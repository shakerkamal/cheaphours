namespace CheapHours.Providers.Kit.Testing;

/// <summary>A request the vendor fake received.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path, without the query.</param>
/// <param name="Headers">The headers (case-insensitive keys, first value).</param>
/// <param name="Body">The body text.</param>
public sealed record VendorRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary>Returns a header's value.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}
