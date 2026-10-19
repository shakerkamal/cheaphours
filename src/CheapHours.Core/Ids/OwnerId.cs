using System.Diagnostics.CodeAnalysis;

namespace CheapHours.Core.Ids;

/// <summary>Identifies the owner of devices, goals and plans. Its string form starts with <c>own_</c>.</summary>
public readonly record struct OwnerId : IPrefixedId<OwnerId>
{
    private readonly string? _value;

    private OwnerId(string value) => _value = value;

    /// <inheritdoc/>
    public static string Prefix => "own_";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The id is <see langword="default"/>, not parsed or created.</exception>
    public string Value => _value ?? throw new InvalidOperationException("default(OwnerId) is not a valid id.");

    /// <summary>Parses <paramref name="s"/> as a <see cref="OwnerId"/>.</summary>
    /// <param name="s">The candidate id.</param>
    /// <returns>The parsed id.</returns>
    /// <exception cref="FormatException">The prefix is not <c>own_</c>, or the body is empty or invalid.</exception>
    public static OwnerId Parse(string s) => PrefixedId.Parse<OwnerId>(s);

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, out OwnerId id)
    {
        id = PrefixedId.IsValid(s, Prefix) ? new OwnerId(s!) : default;
        return id._value is not null;
    }

    /// <summary>Creates a new, time-ordered id.</summary>
    /// <param name="time">The clock to read the creation time from.</param>
    /// <returns>The new id.</returns>
    public static OwnerId New(TimeProvider time) => new(PrefixedId.NewValue(Prefix, time));

    /// <inheritdoc/>
    public override string ToString() => _value ?? string.Empty;
}
