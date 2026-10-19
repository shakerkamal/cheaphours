namespace CheapHours.Host.Worker;

/// <summary>Builds the worker process (scheduled jobs and message consumers).</summary>
internal static class WorkerRole
{
    /// <summary>Creates the worker host; its background services arrive with telemetry, the cloud providers, prices and command dispatch.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The configured, not yet started, host.</returns>
    public static IHost Build(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        return builder.Build();
    }
}
