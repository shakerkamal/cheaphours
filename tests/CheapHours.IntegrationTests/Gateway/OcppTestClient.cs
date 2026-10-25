using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CheapHours.IntegrationTests.Gateway;

/// <summary>A minimal OCPP-J charger for tests, on a real <see cref="ClientWebSocket"/>.</summary>
public sealed class OcppTestClient : IDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private OcppTestClient(ClientWebSocket socket) => Socket = socket;

    /// <summary>Gets the socket.</summary>
    public ClientWebSocket Socket { get; }

    /// <summary>Connects with basic auth and the given subprotocols (default <c>ocpp1.6</c>).</summary>
    /// <param name="uri">The charger's WebSocket address.</param>
    /// <param name="username">The basic-auth username; <see langword="null"/> sends no credentials.</param>
    /// <param name="password">The basic-auth password.</param>
    /// <param name="subProtocols">The subprotocols to offer.</param>
    /// <returns>The connected client.</returns>
    public static async Task<OcppTestClient> ConnectAsync(Uri uri, string? username, string? password, params string[] subProtocols)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        foreach (var protocol in subProtocols.Length == 0 ? ["ocpp1.6"] : subProtocols)
        {
            socket.Options.AddSubProtocol(protocol);
        }

        if (username is not null)
        {
            socket.Options.SetRequestHeader("Authorization", Basic(username, password ?? string.Empty));
        }

        try
        {
            await socket.ConnectAsync(uri, TestContext.Current.CancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new OcppTestClient(socket);
    }

    /// <summary>The <c>Authorization</c> header value for basic auth.</summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <returns>The header value.</returns>
    public static string Basic(string username, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));

    /// <summary>
    /// Sends a WebSocket upgrade request with plain HTTP and returns the response, for checking refusals
    /// (status, headers and body). A 101 is not followed.
    /// </summary>
    /// <param name="client">An HTTP client for the gateway.</param>
    /// <param name="path">The path, for example <c>/ocpp/CP001</c>.</param>
    /// <param name="authorization">The <c>Authorization</c> header, if any.</param>
    /// <param name="subProtocols">The <c>Sec-WebSocket-Protocol</c> values.</param>
    /// <returns>The status, headers and body.</returns>
    public static async Task<(HttpStatusCode Status, HttpResponseHeaders Headers, string Body)> UpgradeAsync(HttpClient client, string path, string? authorization, params string[] subProtocols)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Connection.Add("Upgrade");
        request.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        foreach (var protocol in subProtocols.Length == 0 ? ["ocpp1.6"] : subProtocols)
        {
            request.Headers.Add("Sec-WebSocket-Protocol", protocol);
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        var body = response.StatusCode == HttpStatusCode.SwitchingProtocols ? string.Empty : await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, response.Headers, body);
    }

    /// <summary>Sends one text frame.</summary>
    /// <param name="text">The frame.</param>
    /// <returns>A task that completes when sent.</returns>
    public Task SendAsync(string text) =>
        Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, TestContext.Current.CancellationToken);

    /// <summary>Sends a frame and returns the next frame received.</summary>
    /// <param name="frame">The frame to send.</param>
    /// <returns>The reply, parsed.</returns>
    public async Task<JsonArray> CallAsync(string frame)
    {
        await SendAsync(frame);
        return await ReceiveAsync() ?? throw new InvalidOperationException($"The connection closed ({Socket.CloseStatus}) instead of answering.");
    }

    /// <summary>Receives the next frame, or <see langword="null"/> when the server closes the connection.</summary>
    /// <returns>The frame, parsed.</returns>
    public async Task<JsonArray?> ReceiveAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(ReceiveTimeout);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await Socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))!.AsArray();
            }
        }
    }

    /// <summary>Waits for the server to close the connection and returns the status it sent.</summary>
    /// <returns>The close status.</returns>
    public async Task<WebSocketCloseStatus?> WaitForCloseAsync()
    {
        while (await ReceiveAsync() is not null)
        {
        }

        return Socket.CloseStatus;
    }

    /// <summary>Closes the connection normally.</summary>
    /// <returns>A task that completes when closed.</returns>
    public async Task CloseAsync()
    {
        using var timeout = new CancellationTokenSource(ReceiveTimeout);
        await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
    }

    /// <inheritdoc/>
    public void Dispose() => Socket.Dispose();
}
