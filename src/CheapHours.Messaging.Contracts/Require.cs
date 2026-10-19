using System.Runtime.CompilerServices;
using CheapHours.Core.Ids;

namespace CheapHours.Messaging.Contracts;

/// <summary>Constructor checks for message payloads.</summary>
internal static class Require
{
    public static TId NotDefault<TId>(TId id, [CallerArgumentExpression(nameof(id))] string? paramName = null)
        where TId : struct, IPrefixedId<TId> =>
        id.Equals(default(TId)) ? throw new ArgumentException($"A default {typeof(TId).Name} is not a valid id.", paramName) : id;

    public static string NotBlank(string value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }
}
