using System.Collections.Concurrent;
using System.Text;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CheapHours.IntegrationTests.Infrastructure;

/// <summary>
/// An OTLP/HTTP receiver on WireMock: answers every POST with 200 and keeps its path and body. OTLP bodies are
/// protobuf, whose strings are plain UTF-8, so a test can look for a service name in the raw bytes.
/// </summary>
internal sealed class OtlpReceiver : IDisposable
{
    private readonly WireMockServer _server;
    private readonly ConcurrentQueue<(string Path, byte[] Body)> _requests = new();

    private OtlpReceiver()
    {
        _server = WireMockServer.Start();
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithCallback(Record));
    }

    /// <summary>Gets the receiver's base address, without a trailing slash.</summary>
    public string Url => _server.Url!;

    /// <summary>Gets the paths of every request received so far.</summary>
    public IReadOnlyList<string> Paths => [.. _requests.Select(r => r.Path)];

    public static OtlpReceiver Start() => new();

    /// <summary>Returns whether a request to <paramref name="path"/> carried <paramref name="text"/>.</summary>
    public bool Received(string path, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        return _requests.Any(r => r.Path == path && r.Body.AsSpan().IndexOf(needle) >= 0);
    }

    public void Dispose() => _server.Stop();

    private ResponseMessage Record(IRequestMessage request)
    {
        _requests.Enqueue((request.Path, request.BodyAsBytes ?? Encoding.UTF8.GetBytes(request.Body ?? string.Empty)));
        return new ResponseMessage { StatusCode = 200 };
    }
}
