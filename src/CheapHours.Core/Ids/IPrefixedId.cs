using System.Diagnostics.CodeAnalysis;

namespace CheapHours.Core.Ids;

/// <summary>
/// An opaque, prefixed string identifier such as <c>dev_wallbox_01</c>. Each kind of entity has its own
/// id type, so one kind can never be passed where another is expected.
/// </summary>
/// <typeparam name="TSelf">The implementing id type.</typeparam>
public interface IPrefixedId<TSelf>
    where TSelf : struct, IPrefixedId<TSelf>
{
    /// <summary>Gets the fixed prefix, including the trailing underscore (for example <c>dev_</c>).</summary>
    static abstract string Prefix { get; }

    /// <summary>Gets the full id, prefix included.</summary>
    string Value { get; }

    /// <summary>Parses <paramref name="s"/>, which must start with <see cref="Prefix"/> and have a valid body.</summary>
    /// <param name="s">The candidate id.</param>
    /// <param name="id">The parsed id when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is a valid id of this kind.</returns>
    static abstract bool TryParse([NotNullWhen(true)] string? s, out TSelf id);
}
