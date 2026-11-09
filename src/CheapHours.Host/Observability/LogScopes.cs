using CheapHours.Core.Ids;

namespace CheapHours.Host.Observability;

/// <summary>
/// The log scopes that name what a log entry is about, so an operator can go from a command or a device to its log
/// lines. The keys are spelled here once: <c>commandId</c>, <c>deviceId</c>, <c>providerId</c>, <c>externalId</c>.
/// </summary>
internal static class LogScopes
{
    /// <summary>The scope key of a command id.</summary>
    public const string CommandId = "commandId";

    /// <summary>The scope key of a device id.</summary>
    public const string DeviceId = "deviceId";

    /// <summary>The scope key of a provider id.</summary>
    public const string ProviderId = "providerId";

    /// <summary>The scope key of a device's id at its vendor (charge point id, vehicle id, heat pump id).</summary>
    public const string ExternalId = "externalId";

    /// <summary>Opens the scope of work on one command.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="commandId">The command.</param>
    /// <param name="deviceId">The command's device.</param>
    /// <param name="providerId">The device's provider, once known.</param>
    /// <returns>The scope; dispose it when the work ends.</returns>
    public static IDisposable? ForCommand(ILogger logger, CommandId commandId, DeviceId deviceId, string? providerId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var values = new Dictionary<string, object?>(3, StringComparer.Ordinal)
        {
            [CommandId] = commandId.Value,
            [DeviceId] = deviceId.Value,
        };
        if (providerId is not null)
        {
            values[ProviderId] = providerId;
        }

        return logger.BeginScope(values);
    }

    /// <summary>Opens the scope of handling a device's data (a frame, a webhook, a poll result).</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="externalId">The device's id at its vendor, once known.</param>
    /// <returns>The scope; dispose it when the work ends.</returns>
    public static IDisposable? ForDevice(ILogger logger, string providerId, string? externalId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var values = new Dictionary<string, object?>(2, StringComparer.Ordinal) { [ProviderId] = providerId };
        if (externalId is not null)
        {
            values[ExternalId] = externalId;
        }

        return logger.BeginScope(values);
    }
}
