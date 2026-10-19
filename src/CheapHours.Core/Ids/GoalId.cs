using System.Diagnostics.CodeAnalysis;

namespace CheapHours.Core.Ids;

/// <summary>Identifies a charging goal. Its string form starts with <c>goal_</c>.</summary>
public readonly record struct GoalId : IPrefixedId<GoalId>
{
    private readonly string? _value;

    private GoalId(string value) => _value = value;

    /// <inheritdoc/>
    public static string Prefix => "goal_";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The id is <see langword="default"/>, not parsed or created.</exception>
    public string Value => _value ?? throw new InvalidOperationException("default(GoalId) is not a valid id.");

    /// <summary>Parses <paramref name="s"/> as a <see cref="GoalId"/>.</summary>
    /// <param name="s">The candidate id.</param>
    /// <returns>The parsed id.</returns>
    /// <exception cref="FormatException">The prefix is not <c>goal_</c>, or the body is empty or invalid.</exception>
    public static GoalId Parse(string s) => PrefixedId.Parse<GoalId>(s);

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, out GoalId id)
    {
        id = PrefixedId.IsValid(s, Prefix) ? new GoalId(s!) : default;
        return id._value is not null;
    }

    /// <summary>Creates a new, time-ordered id.</summary>
    /// <param name="time">The clock to read the creation time from.</param>
    /// <returns>The new id.</returns>
    public static GoalId New(TimeProvider time) => new(PrefixedId.NewValue(Prefix, time));

    /// <inheritdoc/>
    public override string ToString() => _value ?? string.Empty;
}
