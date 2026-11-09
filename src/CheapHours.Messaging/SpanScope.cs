using System.Diagnostics;

namespace CheapHours.Messaging;

/// <summary>
/// A span started under a stored trace context, or as a new root, that puts the caller's current span back when it
/// ends. (A span started with an explicit parent context has no parent object, so stopping it alone would leave no
/// current span behind.) <see cref="Span"/> is <see langword="null"/> when nothing listens.
/// </summary>
public sealed class SpanScope : IDisposable
{
    private readonly Activity? _previous;

    private SpanScope(Activity? span, Activity? previous)
    {
        Span = span;
        _previous = previous;
    }

    /// <summary>Gets the span, or <see langword="null"/> when no listener samples it.</summary>
    public Activity? Span { get; }

    /// <summary>
    /// Starts a span whose parent is <paramref name="traceparent"/>, or a new root when it is absent or unreadable;
    /// never a child of the caller's current span.
    /// </summary>
    /// <param name="source">The activity source.</param>
    /// <param name="name">The span name.</param>
    /// <param name="kind">The span kind.</param>
    /// <param name="traceparent">The W3C trace context to continue, if any.</param>
    /// <param name="links">Links to other traces, if any.</param>
    /// <returns>The scope; dispose it to end the span.</returns>
    public static SpanScope Start(ActivitySource source, string name, ActivityKind kind, string? traceparent, IEnumerable<ActivityLink>? links = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var previous = Activity.Current;

        // Without an explicit parent the source would use the current span, so clear it for a root.
        var parent = TryParse(traceparent, out var context) ? context : default;
        if (parent == default)
        {
            Activity.Current = null;
        }

        var span = source.StartActivity(name, kind, parent, links: links);
        if (span is null)
        {
            Activity.Current = previous;
        }

        return new SpanScope(span, previous);
    }

    /// <summary>Starts a span with no parent: a new trace.</summary>
    /// <param name="source">The activity source.</param>
    /// <param name="name">The span name.</param>
    /// <param name="kind">The span kind.</param>
    /// <param name="links">Links to other traces, if any.</param>
    /// <returns>The scope; dispose it to end the span.</returns>
    public static SpanScope StartRoot(ActivitySource source, string name, ActivityKind kind, IEnumerable<ActivityLink>? links = null) =>
        Start(source, name, kind, traceparent: null, links);

    /// <summary>Reads a W3C <c>traceparent</c>.</summary>
    /// <param name="traceparent">The header value.</param>
    /// <param name="context">The trace context it holds.</param>
    /// <returns><see langword="true"/> when it is a valid W3C trace context.</returns>
    public static bool TryParse(string? traceparent, out ActivityContext context)
    {
        context = default;
        return traceparent is { Length: > 0 } && ActivityContext.TryParse(traceparent, null, isRemote: true, out context);
    }

    /// <summary>
    /// Marks a span as failed with the exception's type only: an exception message can carry vendor text, so it
    /// never goes into a span.
    /// </summary>
    /// <param name="span">The span, if any.</param>
    /// <param name="exception">The exception.</param>
    public static void MarkFailed(Activity? span, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        span?.SetStatus(ActivityStatusCode.Error).SetTag("exception.type", exception.GetType().FullName);
    }

    /// <summary>Marks this span as failed with the exception's type only.</summary>
    /// <param name="exception">The exception.</param>
    public void Fail(Exception exception) => MarkFailed(Span, exception);

    /// <summary>Marks this span as failed with a classified reason (an outcome or a reason code, never vendor text).</summary>
    /// <param name="reason">The reason.</param>
    public void Fail(string reason) => Span?.SetStatus(ActivityStatusCode.Error).SetTag("cheaphours.failure", reason);

    /// <summary>Sets a tag on the span, if there is one.</summary>
    /// <param name="key">The tag key.</param>
    /// <param name="value">The value.</param>
    /// <returns>This scope.</returns>
    public SpanScope Tag(string key, object? value)
    {
        Span?.SetTag(key, value);
        return this;
    }

    /// <summary>Ends the span and makes the caller's span current again.</summary>
    public void Dispose()
    {
        Span?.Dispose();
        Activity.Current = _previous;
    }
}
