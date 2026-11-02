using System.Net;
using System.Text;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>Receives a simulator's webhooks in process: records every request and answers with a scripted status.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly List<Received> _requests = [];
    private readonly Lock _lock = new();

    /// <summary>Gets or sets the answer to the n-th request (1-based); 202 by default.</summary>
    public Func<int, HttpStatusCode> Answer { get; set; } = _ => HttpStatusCode.Accepted;

    /// <summary>Gets the requests received so far, oldest first.</summary>
    public IReadOnlyList<Received> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        int number;
        lock (_lock)
        {
            _requests.Add(new Received(
                request.RequestUri!,
                Header(request, "HeatPump-Signature"),
                Header(request, "HeatPump-Event-Id"),
                request.Content?.Headers.ContentType?.MediaType,
                body));
            number = _requests.Count;
        }

        return new HttpResponseMessage(Answer(number));
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    /// <summary>One received webhook.</summary>
    /// <param name="Uri">Where it was posted.</param>
    /// <param name="Signature">The <c>HeatPump-Signature</c> header.</param>
    /// <param name="EventId">The <c>HeatPump-Event-Id</c> header.</param>
    /// <param name="ContentType">The media type.</param>
    /// <param name="Body">The raw body.</param>
    public sealed record Received(Uri Uri, string? Signature, string? EventId, string? ContentType, byte[] Body)
    {
        /// <summary>Gets the body as text.</summary>
        public string Text => Encoding.UTF8.GetString(Body);
    }
}
