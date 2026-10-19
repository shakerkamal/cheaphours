using System.Runtime.CompilerServices;
using CheapHours.Core.Ids;

namespace CheapHours.Core;

/// <summary>
/// Invariant checks for domain constructors. A failure here is a programming error: edges validate input
/// first and turn bad input into 400/422 responses, so past the edge these never fire.
/// </summary>
internal static class Guard
{
    public static DateTimeOffset Utc(DateTimeOffset value, [CallerArgumentExpression(nameof(value))] string? paramName = null) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException($"Must be UTC (offset 00:00), but the offset is {value.Offset}.", paramName);

    public static DateTimeOffset? Utc(DateTimeOffset? value, [CallerArgumentExpression(nameof(value))] string? paramName = null) =>
        value is { } v ? Utc(v, paramName) : null;

    public static TId NotDefault<TId>(TId id, [CallerArgumentExpression(nameof(id))] string? paramName = null)
        where TId : struct, IPrefixedId<TId> =>
        id.Equals(default(TId))
            ? throw new ArgumentException($"A default {typeof(TId).Name} is not a valid id.", paramName)
            : id;

    public static string NotBlank(string value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }
}
