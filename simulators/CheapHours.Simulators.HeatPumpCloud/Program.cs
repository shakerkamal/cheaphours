namespace CheapHours.Simulators.HeatPumpCloud;

/// <summary>
/// Runs the simulator. An explicit, namespaced entry point instead of top-level statements: the generated
/// global <c>Program</c> would clash with the host's in test projects that reference both.
/// </summary>
internal static class Program
{
    private static async Task Main(string[] args) => await HeatPumpCloudSimulator.Build(args).RunAsync().ConfigureAwait(false);
}
