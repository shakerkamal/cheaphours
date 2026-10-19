namespace CheapHours.Core.Devices;

/// <summary>The format of a provider id such as <c>ocpp</c> or <c>carcloud</c>: 1–32 lowercase ASCII letters or digits.</summary>
public static class ProviderIdFormat
{
    /// <summary>The longest provider id allowed.</summary>
    public const int MaxLength = 32;

    /// <summary>Returns whether <paramref name="providerId"/> is a well-formed provider id.</summary>
    /// <param name="providerId">The candidate id.</param>
    /// <returns><see langword="true"/> when the id is 1–32 lowercase ASCII letters or digits.</returns>
    public static bool IsValid(string? providerId) =>
        providerId is { Length: > 0 and <= MaxLength }
        && providerId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));
}
