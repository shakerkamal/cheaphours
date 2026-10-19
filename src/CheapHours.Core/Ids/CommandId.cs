using System.Diagnostics.CodeAnalysis;

namespace CheapHours.Core.Ids;

/// <summary>Identifies a device command. Its string form starts with <c>cmd_</c>.</summary>
public readonly record struct CommandId : IPrefixedId<CommandId>
{
    private readonly string? _value;

    private CommandId(string value) => _value = value;

    /// <inheritdoc/>
    public static string Prefix => "cmd_";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The id is <see langword="default"/>, not parsed or created.</exception>
    public string Value => _value ?? throw new InvalidOperationException("default(CommandId) is not a valid id.");

    /// <summary>Parses <paramref name="s"/> as a <see cref="CommandId"/>.</summary>
    /// <param name="s">The candidate id.</param>
    /// <returns>The parsed id.</returns>
    /// <exception cref="FormatException">The prefix is not <c>cmd_</c>, or the body is empty or invalid.</exception>
    public static CommandId Parse(string s) => PrefixedId.Parse<CommandId>(s);

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, out CommandId id)
    {
        id = PrefixedId.IsValid(s, Prefix) ? new CommandId(s!) : default;
        return id._value is not null;
    }

    /// <summary>Creates a new, time-ordered id.</summary>
    /// <param name="time">The clock to read the creation time from.</param>
    /// <returns>The new id.</returns>
    public static CommandId New(TimeProvider time) => new(PrefixedId.NewValue(Prefix, time));

    /// <inheritdoc/>
    public override string ToString() => _value ?? string.Empty;
}
