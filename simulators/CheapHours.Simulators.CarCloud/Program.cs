namespace CheapHours.Simulators.CarCloud;

/// <summary>
/// Runs the simulator. An explicit, namespaced entry point instead of top-level statements: the generated
/// global <c>Program</c> would clash with the host's in test projects that reference both.
/// </summary>
internal static class Program
{
    private static async Task Main(string[] args) => await CarCloudSimulator.Build(args).RunAsync().ConfigureAwait(false);
}
