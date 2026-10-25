using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>Keeps every log line (message, structured values and exception text) so tests can check what was logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Gets everything logged so far.</summary>
    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    /// <summary>Returns whether any log line contains <paramref name="text"/>.</summary>
    /// <param name="text">The text to look for.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool Contains(string text) => _lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} | {values} | {exception}");
        }
    }
}
