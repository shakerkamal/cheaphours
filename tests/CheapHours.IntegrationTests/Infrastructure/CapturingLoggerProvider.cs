using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>One captured log entry with its structured values and the scopes active when it was written.</summary>
public sealed record CapturedLog(
    LogLevel Level,
    string Category,
    string Message,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyDictionary<string, string?> Scopes);

/// <summary>
/// Keeps every log line (message, structured values, scopes and exception text) so tests can check what was logged.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>Gets everything logged so far.</summary>
    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    /// <summary>Gets every entry logged so far, with its scopes.</summary>
    public IReadOnlyCollection<CapturedLog> Entries => _entries.ToArray();

    /// <summary>Returns whether any log line contains <paramref name="text"/>.</summary>
    /// <param name="text">The text to look for.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool Contains(string text) => _lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    /// <inheritdoc/>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private static Dictionary<string, string?> Pairs(object? state)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var (key, value) in pairs)
            {
                values[key] = value?.ToString();
            }
        }

        return values;
    }

    private sealed class Logger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = Pairs(state);
            var scopes = new Dictionary<string, string?>(StringComparer.Ordinal);
            owner._scopes.ForEachScope(
                (scope, into) =>
                {
                    foreach (var (key, value) in Pairs(scope))
                    {
                        into[key] = value;
                    }
                },
                scopes);

            var message = formatter(state, exception);
            owner._entries.Enqueue(new CapturedLog(logLevel, category, message, values, scopes));
            owner._lines.Enqueue(
                $"{logLevel} {category}: {message} | {string.Join(" ", values.Select(p => $"{p.Key}={p.Value}"))}"
                + $" | {string.Join(" ", scopes.Select(p => $"{p.Key}={p.Value}"))} | {exception}");
        }
    }
}
