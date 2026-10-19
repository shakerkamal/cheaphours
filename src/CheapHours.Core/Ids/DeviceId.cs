using System.Diagnostics.CodeAnalysis;

namespace CheapHours.Core.Ids;

/// <summary>Identifies a device (a charger, vehicle or other connected appliance). Its string form starts with <c>dev_</c>.</summary>
public readonly record struct DeviceId : IPrefixedId<DeviceId>
{
    private readonly string? _value;

    private DeviceId(string value) => _value = value;

    /// <inheritdoc/>
    public static string Prefix => "dev_";

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The id is <see langword="default"/>, not parsed or created.</exception>
    public string Value => _value ?? throw new InvalidOperationException("default(DeviceId) is not a valid id.");

    /// <summary>Parses <paramref name="s"/> as a <see cref="DeviceId"/>.</summary>
    /// <param name="s">The candidate id.</param>
    /// <returns>The parsed id.</returns>
    /// <exception cref="FormatException">The prefix is not <c>dev_</c>, or the body is empty or invalid.</exception>
    public static DeviceId Parse(string s) => PrefixedId.Parse<DeviceId>(s);

    /// <inheritdoc/>
    public static bool TryParse([NotNullWhen(true)] string? s, out DeviceId id)
    {
        id = PrefixedId.IsValid(s, Prefix) ? new DeviceId(s!) : default;
        return id._value is not null;
    }

    /// <summary>Creates a new, time-ordered id.</summary>
    /// <param name="time">The clock to read the creation time from.</param>
    /// <returns>The new id.</returns>
    public static DeviceId New(TimeProvider time) => new(PrefixedId.NewValue(Prefix, time));

    /// <inheritdoc/>
    public override string ToString() => _value ?? string.Empty;
}
