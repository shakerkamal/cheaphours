using Microsoft.Extensions.Options;

namespace CheapHours.Host.Gateway;

/// <summary>How the gateway carries out commands (section <c>Gateway:Commands</c>, command-dispatch design §7).</summary>
internal sealed class GatewayCommandsOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Gateway:Commands";

    /// <summary>Gets or sets the connector commands act on. Default 1.</summary>
    public int ConnectorId { get; set; } = 1;

    /// <summary>Gets or sets the id tag a remote start authorises (at most 20 characters). Default <c>CHEAPHOURS</c>.</summary>
    public string IdTag { get; set; } = "CHEAPHOURS";

    /// <summary>Gets or sets how long the gateway waits for the charger's answer to one call. Default 30 s.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how long the replica's queue may stay without a consumer before the broker removes it. Default 10 min.</summary>
    public TimeSpan QueueExpiry { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets how many commands the replica works on at once. Default 16.</summary>
    public int Prefetch { get; set; } = 16;
}

/// <summary>Validates <see cref="GatewayCommandsOptions"/> at startup, naming every broken key and never a value.</summary>
internal sealed class GatewayCommandsOptionsValidator : IValidateOptions<GatewayCommandsOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, GatewayCommandsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        const string Section = GatewayCommandsOptions.SectionName;

        if (options.ConnectorId < 1)
        {
            errors.Add($"{Section}:ConnectorId must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(options.IdTag) || options.IdTag.Length > 20)
        {
            errors.Add($"{Section}:IdTag must be 1-20 characters.");
        }

        if (options.CallTimeout <= TimeSpan.Zero || options.CallTimeout > TimeSpan.FromMinutes(2))
        {
            errors.Add($"{Section}:CallTimeout must be positive and at most 2 minutes.");
        }

        if (options.QueueExpiry < TimeSpan.FromSeconds(10))
        {
            errors.Add($"{Section}:QueueExpiry must be at least 10 seconds.");
        }

        if (options.Prefetch is < 1 or > 256)
        {
            errors.Add($"{Section}:Prefetch must be between 1 and 256.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
