using System.Diagnostics.CodeAnalysis;

namespace CheapHours.Core.Ids;

/// <summary>Identifies a charging plan. Its string form starts with <c>plan_</c>.</summary>
public readonly record struct PlanId : IPrefixedId<PlanId>
{
    private readonly string? _value;

    private PlanId(string value) => _value = value;

    /// <inheritdoc/>
    public static string Prefix => "plan_";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The id is <see langword="default"/>, not parsed or created.</exception>
    public string Value => _value ?? throw new InvalidOperationException("default(PlanId) is not a valid id.");

    /// <summary>Parses <paramref name="s"/> as a <see cref="PlanId"/>.</summary>
    /// <param name="s">The candidate id.</param>
    /// <returns>The parsed id.</returns>
    /// <exception cref="FormatException">The prefix is not <c>plan_</c>, or the body is empty or invalid.</exception>
    public static PlanId Parse(string s) => PrefixedId.Parse<PlanId>(s);

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, out PlanId id)
    {
        id = PrefixedId.IsValid(s, Prefix) ? new PlanId(s!) : default;
        return id._value is not null;
    }

    /// <summary>Creates a new, time-ordered id.</summary>
    /// <param name="time">The clock to read the creation time from.</param>
    /// <returns>The new id.</returns>
    public static PlanId New(TimeProvider time) => new(PrefixedId.NewValue(Prefix, time));

    /// <inheritdoc/>
    public override string ToString() => _value ?? string.Empty;
}
