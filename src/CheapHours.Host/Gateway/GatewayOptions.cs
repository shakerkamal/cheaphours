using System.Text;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Gateway;

/// <summary>Gateway settings, section <c>Gateway</c>.</summary>
internal sealed class GatewayOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Gateway";

    /// <summary>The longest replica id accepted; it becomes part of a queue name.</summary>
    public const int MaxReplicaIdLength = 63;

    /// <summary>
    /// Gets or sets this replica's id, used in the connection registry and the command queue name
    /// (<c>gateway.{replicaId}.commands</c>). Defaults to the machine name (the pod name in Kubernetes).
    /// </summary>
    public string ReplicaId { get; set; } = DefaultReplicaId(Environment.MachineName);

    /// <summary>Gets or sets how long shutdown waits for chargers to complete the close handshake (default 10 s).</summary>
    public int DrainTimeoutSeconds { get; set; } = 10;

    /// <summary>Gets the drain timeout.</summary>
    public TimeSpan DrainTimeout => TimeSpan.FromSeconds(DrainTimeoutSeconds);

    /// <summary>Turns a machine name into a replica id: lower case, other characters as <c>-</c>, at most 63 characters.</summary>
    /// <param name="machineName">The machine name.</param>
    /// <returns>A valid replica id; <c>gateway</c> when nothing usable is left.</returns>
    public static string DefaultReplicaId(string machineName)
    {
        var id = new StringBuilder(machineName.Length);
        foreach (var c in machineName.ToLowerInvariant())
        {
            id.Append(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-');
        }

        var trimmed = id.ToString().Trim('-');
        trimmed = trimmed.Length > MaxReplicaIdLength ? trimmed[..MaxReplicaIdLength].TrimEnd('-') : trimmed;
        return trimmed.Length == 0 ? "gateway" : trimmed;
    }

    /// <summary>Returns whether <paramref name="replicaId"/> is 1–63 characters from <c>[a-z0-9-]</c>.</summary>
    /// <param name="replicaId">The candidate id.</param>
    /// <returns><see langword="true"/> for a valid id.</returns>
    public static bool IsValidReplicaId(string? replicaId) =>
        replicaId is { Length: > 0 and <= MaxReplicaIdLength } && replicaId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}

/// <summary>Validates <see cref="GatewayOptions"/> at startup.</summary>
internal sealed class GatewayOptionsValidator : IValidateOptions<GatewayOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (!GatewayOptions.IsValidReplicaId(options.ReplicaId))
        {
            errors.Add($"{GatewayOptions.SectionName}:ReplicaId must be 1-{GatewayOptions.MaxReplicaIdLength} characters from [a-z0-9-].");
        }

        if (options.DrainTimeoutSeconds is <= 0 or > 60)
        {
            errors.Add($"{GatewayOptions.SectionName}:DrainTimeoutSeconds must be between 1 and 60.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
