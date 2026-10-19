namespace CheapHours.Host.Gateway;

/// <summary>Builds the device gateway process (OCPP WebSockets, vendor webhooks).</summary>
internal static class GatewayRole
{
    /// <summary>Creates the gateway application; its endpoints arrive with the OCPP gateway.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The configured, not yet started, application.</returns>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        return builder.Build();
    }
}
