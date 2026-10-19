using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using CheapHours.Core.Ids;

namespace CheapHours.Host.Api.OpenApi;

/// <summary>How typed ids appear in the OpenAPI document: a string with a prefix pattern.</summary>
internal static class PrefixedIdSchema
{
    /// <summary>Gets the prefix of a typed id type such as <see cref="DeviceId"/>.</summary>
    /// <param name="type">The candidate type.</param>
    /// <param name="prefix">The id prefix when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="type"/> is a prefixed id type.</returns>
    public static bool TryGetPrefix(Type type, [NotNullWhen(true)] out string? prefix)
    {
        ArgumentNullException.ThrowIfNull(type);

        var isId = type.IsValueType && type.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPrefixedId<>) && i.GetGenericArguments()[0] == type);
        prefix = isId ? type.GetProperty(nameof(IPrefixedId<DeviceId>.Prefix), BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string : null;
        return prefix is not null;
    }

    /// <summary>Returns the regular expression an id with <paramref name="prefix"/> matches.</summary>
    /// <param name="prefix">The id prefix.</param>
    /// <returns>An anchored pattern, for example <c>^dev_[A-Za-z0-9_-]{1,64}$</c>.</returns>
    public static string Pattern(string prefix) => $"^{Regex.Escape(prefix)}[A-Za-z0-9_-]{{1,{PrefixedId.MaxBodyLength}}}$";
}
