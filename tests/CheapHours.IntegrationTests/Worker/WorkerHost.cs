using CheapHours.Host.Worker;
using CheapHours.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CheapHours.IntegrationTests.Worker;

/// <summary>Starts worker hosts on a test's database and broker prefix, with fast polling and captured logs.</summary>
internal static class WorkerHost
{
    /// <summary>Lease and tick settings that keep polling tests short.</summary>
    public static Dictionary<string, string> FastPolling(string replicaId) => new()
    {
        ["Polling:LeaseDuration"] = "00:00:02",
        ["Polling:RenewInterval"] = "00:00:00.500",
        ["Polling:AcquireInterval"] = "00:00:00.500",
        ["Polling:Tick"] = "00:00:00.200",
        ["Worker:ReplicaId"] = replicaId,
    };

    public static async Task<IHost> StartAsync(
        MongoFixture mongo,
        RabbitMqFixture rabbit,
        string database,
        string prefix,
        IReadOnlyDictionary<string, string> settings,
        CapturingLoggerProvider? logs = null,
        Action<IServiceCollection>? configure = null)
    {
        string[] args =
        [
            $"--ConnectionStrings:Mongo={mongo.ConnectionString}",
            $"--Mongo:Database={database}",
            $"--ConnectionStrings:RabbitMq={rabbit.ConnectionString}",
            $"--Messaging:QueuePrefix={prefix}",

            // No test reaches the real price API: the import is off unless a test turns it on (later values win).
            "--Prices:ImportEnabled=false",
            "--Prices:BaseUrl=http://127.0.0.1:9/",
            .. settings.Select(kv => $"--{kv.Key}={kv.Value}"),
        ];
        var host = WorkerRole.Build(args, services =>
        {
            if (logs is not null)
            {
                services.AddLogging(b => b.AddProvider(logs));
            }

            configure?.Invoke(services);
        });

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.Services.GetRequiredService<WorkerState>().Ready.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return host;
    }

    public static async Task StopAsync(IHost host)
    {
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();
    }

    /// <summary>Waits until <paramref name="condition"/> holds, checking every 200 ms.</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        var value = await probe();
        while (!condition(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            value = await probe();
        }

        Assert.True(condition(value), $"Condition not met within {within}; last value: {value}");
        return value;
    }
}
