using System.Net;
using CheapHours.IntegrationTests.Infrastructure;

namespace CheapHours.IntegrationTests.Gateway;

public sealed class GatewayUpgradeTests(MongoFixture mongo) : IAsyncLifetime
{
    private GatewayFactory _gateway = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = new GatewayFactory(mongo.ConnectionString);
        await _gateway.StartAsync();
        _http = _gateway.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _gateway.DisposeAsync();
    }

    private Task<(HttpStatusCode Status, System.Net.Http.Headers.HttpResponseHeaders Headers, string Body)> Upgrade(string path, string? authorization, params string[] protocols) =>
        OcppTestClient.UpgradeAsync(_http, path, authorization, protocols);

    [Fact]
    public async Task Registered_charger_connects_with_ocpp16()
    {
        using var client = await OcppTestClient.ConnectAsync(_gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password);

        Assert.Equal("ocpp1.6", client.Socket.SubProtocol);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, client.Socket.HttpStatusCode);
    }

    [Fact]
    public async Task Several_subprotocols_select_ocpp16()
    {
        using var client = await OcppTestClient.ConnectAsync(_gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password, "ocpp2.0.1", "ocpp1.6");

        Assert.Equal("ocpp1.6", client.Socket.SubProtocol);
    }

    [Fact]
    public async Task Malformed_charger_id_is_400()
    {
        var (status, _, _) = await Upgrade("/ocpp/bad%20id", OcppTestClient.Basic("bad id", GatewayFactory.Password));

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Plain_http_get_is_400()
    {
        using var response = await _http.GetAsync(new Uri("/ocpp/CP001", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_401_with_a_basic_challenge()
    {
        var (status, headers, _) = await Upgrade("/ocpp/CP001", OcppTestClient.Basic("CP001", "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains(headers.WwwAuthenticate, h => h.Scheme == "Basic");
    }

    [Fact]
    public async Task Unknown_charger_gets_the_same_401_as_a_wrong_password()
    {
        var wrong = await Upgrade("/ocpp/CP001", OcppTestClient.Basic("CP001", "wrong"));
        var unknown = await Upgrade("/ocpp/CP999", OcppTestClient.Basic("CP999", GatewayFactory.Password));

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal(wrong.Body, unknown.Body);
        Assert.Equal(wrong.Headers.WwwAuthenticate.ToString(), unknown.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Username_must_match_the_path()
    {
        var (status, _, _) = await Upgrade("/ocpp/CP001", OcppTestClient.Basic("CP002", GatewayFactory.OtherPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Revoked_registration_is_401()
    {
        await _gateway.Registrations.RevokeAsync("CP002", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var (status, _, _) = await Upgrade("/ocpp/CP002", OcppTestClient.Basic("CP002", GatewayFactory.OtherPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task No_credentials_is_401_with_a_basic_challenge()
    {
        var (status, headers, _) = await Upgrade("/ocpp/CP001", authorization: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains(headers.WwwAuthenticate, h => h.Scheme == "Basic");
    }

    [Fact]
    public async Task Missing_ocpp16_subprotocol_is_400()
    {
        var (status, _, _) = await Upgrade("/ocpp/CP001", OcppTestClient.Basic("CP001", GatewayFactory.Password), "ocpp2.0.1");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Real_client_refused_with_a_wrong_password_never_gets_a_socket()
    {
        var ex = await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => OcppTestClient.ConnectAsync(_gateway.OcppUri("CP001"), "CP001", "wrong"));

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Gateway_role_does_not_serve_the_public_api()
    {
        using var response = await _http.GetAsync(new Uri("/v1/devices", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Secrets_never_reach_the_logs()
    {
        var header = OcppTestClient.Basic("CP001", "wrong-password-7f3a");
        await Upgrade("/ocpp/CP001", header);
        using (var client = await OcppTestClient.ConnectAsync(_gateway.OcppUri("CP001"), "CP001", GatewayFactory.Password))
        {
            await client.CallAsync("""[2,"h1","Heartbeat",{}]""");
        }

        Assert.True(_gateway.Logs.Contains("Refused charger CP001"));
        Assert.False(_gateway.Logs.Contains("wrong-password-7f3a"));
        Assert.False(_gateway.Logs.Contains(header["Basic ".Length..]));
        Assert.False(_gateway.Logs.Contains(GatewayFactory.Password));
        Assert.False(_gateway.Logs.Contains(GatewayFactory.Hash(GatewayFactory.Password)));
        Assert.False(_gateway.Logs.Contains(OcppTestClient.Basic("CP001", GatewayFactory.Password)["Basic ".Length..]));
    }
}
