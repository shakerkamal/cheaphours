using System.Diagnostics;
using System.Text.Json;
using CheapHours.Host;
using CheapHours.Host.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CheapHours.UnitTests.Host;

public sealed class LoggingSetupTests
{
    [Fact]
    public void Outside_development_a_log_entry_is_one_json_line_with_a_utc_timestamp_and_scopes()
    {
        using var host = Build(Environments.Production);
        var options = host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;
        var formatter = host.Services.GetServices<ConsoleFormatter>().Single(f => f.Name == options.FormatterName);
        var scopes = new LoggerExternalScopeProvider();
        using var writer = new StringWriter();

        using (scopes.Push(new Dictionary<string, object?> { ["commandId"] = "cmd_1", ["providerId"] = "ocpp" }))
        {
            var entry = new LogEntry<string>(LogLevel.Warning, "CheapHours.Worker", new EventId(7), "charger CP001 did not answer", null, (state, _) => state);
            formatter.Write(entry, scopes, writer);
        }

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal(ConsoleFormatterNames.Json, options.FormatterName);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", root.GetProperty("Timestamp").GetString());
        Assert.Equal("Warning", root.GetProperty("LogLevel").GetString());
        Assert.Equal("CheapHours.Worker", root.GetProperty("Category").GetString());
        Assert.Equal("charger CP001 did not answer", root.GetProperty("Message").GetString());
        var scope = Assert.Single(root.GetProperty("Scopes").EnumerateArray());
        Assert.Equal("cmd_1", scope.GetProperty("commandId").GetString());
        Assert.Equal("ocpp", scope.GetProperty("providerId").GetString());
    }

    [Fact]
    public void In_development_logs_are_plain_text_with_scopes()
    {
        using var host = Build(Environments.Development);

        var options = host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;
        var simple = host.Services.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;

        Assert.Equal(ConsoleFormatterNames.Simple, options.FormatterName);
        Assert.True(simple.IncludeScopes);
    }

    [Fact]
    public void An_entry_written_inside_a_span_carries_its_trace_and_span_ids()
    {
        var recorder = new ScopeRecordingProvider();
        using var host = Build(Environments.Production, recorder);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CheapHours.Commands");
        using var activity = new Activity("dispatch StartCharging").SetIdFormat(ActivityIdFormat.W3C).Start();

        logger.Log(LogLevel.Information, new EventId(1), "dispatching", null, (state, _) => state);

        var scopes = Assert.Single(recorder.Entries);
        Assert.Equal(activity.TraceId.ToString(), scopes["TraceId"]);
        Assert.Equal(activity.SpanId.ToString(), scopes["SpanId"]);
    }

    private static IHost Build(string environment, ILoggerProvider? extra = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = environment });
        builder.AddCheapHoursObservability(HostRole.Worker, "worker-1");
        if (extra is not null)
        {
            builder.Logging.AddProvider(extra);
        }

        return builder.Build();
    }

    /// <summary>Records the scope values active at each log call.</summary>
    private sealed class ScopeRecordingProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public List<Dictionary<string, string?>> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Recorder(ScopeRecordingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = new Dictionary<string, string?>();
                owner._scopes.ForEachScope(
                    (scope, into) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            foreach (var (key, value) in pairs)
                            {
                                into[key] = value?.ToString();
                            }
                        }
                    },
                    values);
                owner.Entries.Add(values);
            }
        }
    }
}
