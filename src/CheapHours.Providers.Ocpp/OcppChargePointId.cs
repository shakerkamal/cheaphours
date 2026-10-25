using System.Text.RegularExpressions;

namespace CheapHours.Providers.Ocpp;

/// <summary>The rules for a charge point id, the last segment of a charger's WebSocket URL.</summary>
public static partial class OcppChargePointId
{
    /// <summary>The longest id accepted.</summary>
    public const int MaxLength = 48;

    /// <summary>Returns whether <paramref name="id"/> is 1–48 characters from <c>[A-Za-z0-9._:-]</c>.</summary>
    /// <param name="id">The candidate id.</param>
    /// <returns><see langword="true"/> for a valid id.</returns>
    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);

    [GeneratedRegex(@"\A[A-Za-z0-9._:-]{1,48}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
