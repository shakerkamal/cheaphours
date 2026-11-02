using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Simulators.HeatPumpCloud;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Simulators;

/// <summary>Every scenario of the <c>heat-pump-cloud-simulator</c> spec, with a fake clock and a recording receiver.</summary>
public sealed class HeatPumpCloudSimulatorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly RecordingHandler _receiver = new();
    private HeatPumpSimulatorHost _sim = null!;

    public async ValueTask InitializeAsync() => _sim = await HeatPumpSimulatorHost.StartAsync(_time, handler: _receiver);

    public async ValueTask DisposeAsync() => await _sim.DisposeAsync();

    private static JsonElement Json(RecordingHandler.Received received) => JsonDocument.Parse(received.Body).RootElement.Clone();

    /// <summary>Waits (real time) until the receiver has at least <paramref name="count"/> requests.</summary>
    private async Task<IReadOnlyList<RecordingHandler.Received>> ReceivedAsync(int count)
    {
        for (var i = 0; i < 100 && _receiver.Requests.Count < count; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(_receiver.Requests.Count >= count, $"Expected {count} deliveries, got {_receiver.Requests.Count}.");
        return _receiver.Requests;
    }

    /// <summary>Advances the clock a second at a time while fewer than <paramref name="count"/> requests arrived (retry delays run on the fake clock).</summary>
    private async Task AdvanceWhileFewerThanAsync(int count, int maxSeconds)
    {
        for (var i = 0; i < maxSeconds && _receiver.Requests.Count < count; i++)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await Task.Delay(100, TestContext.Current.CancellationToken);
    }

    /// <summary>The first delivery: one interval after start.</summary>
    private async Task<RecordingHandler.Received> FirstDeliveryAsync()
    {
        _time.Advance(TimeSpan.FromHours(1));
        return (await ReceivedAsync(1))[0];
    }

    [Fact]
    public async Task A_delivery_is_signed_so_a_receiver_that_knows_the_secret_computes_the_same_signature()
    {
        var received = await FirstDeliveryAsync();

        Assert.Equal(new Uri("http://receiver.test/webhooks/heatpumpcloud/acct_demo"), received.Uri);
        Assert.Equal("application/json", received.ContentType);
        var parts = received.Signature!.Split(',');
        var t = parts[0]["t=".Length..];
        Assert.Equal(Start.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), t);
        byte[] payload = [.. Encoding.ASCII.GetBytes(t + "."), .. received.Body];
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(HeatPumpSimulatorHost.Secret), payload));
        Assert.Equal("v1=" + expected, parts[1]);

        var body = Json(received);
        Assert.Equal("device.state", body.GetProperty("type").GetString());
        Assert.Equal(received.EventId, body.GetProperty("eventId").GetString());
        Assert.StartsWith("evt_", received.EventId, StringComparison.Ordinal);
        Assert.Equal("acct_demo", body.GetProperty("accountId").GetString());
        Assert.Equal("hp_1", body.GetProperty("device").GetProperty("id").GetString());
        Assert.Equal("Kaltwerk", body.GetProperty("device").GetProperty("maker").GetString());
        Assert.Equal("2026-11-02T11:00:00.000Z", body.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public async Task Every_event_has_its_own_id()
    {
        await FirstDeliveryAsync();
        _time.Advance(TimeSpan.FromHours(1));
        var received = await ReceivedAsync(2);

        Assert.NotEqual(received[0].EventId, received[1].EventId);
    }

    [Fact]
    public async Task One_hour_of_heating_at_3_kW_adds_3_kWh()
    {
        var state = Json(await FirstDeliveryAsync()).GetProperty("state");

        Assert.True(state.GetProperty("online").GetBoolean());
        Assert.Equal("Heating", state.GetProperty("mode").GetString());
        Assert.Equal(3m, state.GetProperty("powerKw").GetDecimal());
        Assert.Equal(1003m, state.GetProperty("energyKwh").GetDecimal());
    }

    [Fact]
    public async Task A_receiver_that_is_briefly_down_gets_three_attempts_with_the_same_body_and_event_id()
    {
        _receiver.Answer = n => n <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted;

        await FirstDeliveryAsync();
        await AdvanceWhileFewerThanAsync(3, maxSeconds: 30);
        await AdvanceWhileFewerThanAsync(4, maxSeconds: 20); // nothing more comes: the third attempt succeeded

        var received = _receiver.Requests;
        Assert.Equal(3, received.Count);
        Assert.All(received, r => Assert.Equal(received[0].Text, r.Text));
        Assert.All(received, r => Assert.Equal(received[0].EventId, r.EventId));
        Assert.NotEqual(received[0].Signature, received[2].Signature); // re-signed at each attempt
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Retryable_answers_are_retried_up_to_five_attempts(HttpStatusCode status)
    {
        _receiver.Answer = _ => status;

        await FirstDeliveryAsync();
        await AdvanceWhileFewerThanAsync(5, maxSeconds: 40);
        await AdvanceWhileFewerThanAsync(6, maxSeconds: 20);

        Assert.Equal(5, _receiver.Requests.Count);
        Assert.Single(_receiver.Requests.Select(r => r.EventId).Distinct());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refused_delivery_is_not_retried(HttpStatusCode status)
    {
        _receiver.Answer = _ => status;

        await FirstDeliveryAsync();
        await AdvanceWhileFewerThanAsync(2, maxSeconds: 20);

        Assert.Single(_receiver.Requests);
    }

    [Fact]
    public async Task Redelivering_sends_the_same_body_and_event_id_again()
    {
        var first = await FirstDeliveryAsync();

        var (status, body) = await _sim.PostAsync("admin/heat-pumps/hp_1/redeliver");

        Assert.Equal(200, status);
        Assert.Equal(first.EventId, body.GetProperty("eventId").GetString());
        Assert.Equal(202, body.GetProperty("statusCode").GetInt32());
        var again = (await ReceivedAsync(2))[1];
        Assert.Equal(first.Text, again.Text);
        Assert.Equal(first.EventId, again.EventId);
    }

    [Fact]
    public async Task Redelivering_before_any_event_is_404()
    {
        var (status, _) = await _sim.PostAsync("admin/heat-pumps/hp_1/redeliver");

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Standby_draws_standby_power_and_a_fault_draws_none()
    {
        Assert.Equal(204, (await _sim.PostAsync("admin/heat-pumps/hp_1/mode/Standby")).Status);
        _time.Advance(TimeSpan.FromHours(1));
        var standby = Json((await ReceivedAsync(1))[0]).GetProperty("state");

        Assert.Equal(204, (await _sim.PostAsync("admin/heat-pumps/hp_1/mode/Fault")).Status);
        _time.Advance(TimeSpan.FromHours(1));
        var fault = Json((await ReceivedAsync(2))[1]).GetProperty("state");

        Assert.Equal(0.05m, standby.GetProperty("powerKw").GetDecimal());
        Assert.Equal(1000.05m, standby.GetProperty("energyKwh").GetDecimal());
        Assert.Equal("Fault", fault.GetProperty("mode").GetString());
        Assert.Equal(0m, fault.GetProperty("powerKw").GetDecimal());
        Assert.Equal(1000.05m, fault.GetProperty("energyKwh").GetDecimal());
    }

    [Fact]
    public async Task An_unknown_mode_or_heat_pump_is_refused()
    {
        Assert.Equal(400, (await _sim.PostAsync("admin/heat-pumps/hp_1/mode/Turbo")).Status);
        Assert.Equal(404, (await _sim.PostAsync("admin/heat-pumps/hp_9/mode/Standby")).Status);
    }

    [Fact]
    public async Task Removing_a_heat_pump_sends_device_deleted_and_stops_reporting_it()
    {
        var (status, body) = await _sim.PostAsync("admin/heat-pumps/hp_1/remove");

        Assert.Equal(200, status);
        var deleted = Json((await ReceivedAsync(1))[0]);
        Assert.Equal("device.deleted", deleted.GetProperty("type").GetString());
        Assert.Equal("hp_1", deleted.GetProperty("device").GetProperty("id").GetString());
        Assert.Equal(body.GetProperty("eventId").GetString(), deleted.GetProperty("eventId").GetString());

        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Single(_receiver.Requests);
        Assert.Equal(404, (await _sim.PostAsync("admin/heat-pumps/hp_1/remove")).Status);
    }

    [Fact]
    public async Task Deliveries_are_listed_with_their_answers()
    {
        _receiver.Answer = n => n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted;
        await FirstDeliveryAsync();
        await AdvanceWhileFewerThanAsync(2, maxSeconds: 20);

        var (status, body) = await _sim.GetAsync("admin/deliveries");

        Assert.Equal(200, status);
        var deliveries = body.GetProperty("deliveries").EnumerateArray().ToList();
        Assert.Equal([503, 202], deliveries.Select(d => d.GetProperty("statusCode").GetInt32()));
        Assert.Equal([1, 2], deliveries.Select(d => d.GetProperty("attempt").GetInt32()));
        Assert.All(deliveries, d => Assert.Equal("hp_1", d.GetProperty("heatPumpId").GetString()));
    }

    [Fact]
    public async Task Health_answers_200()
    {
        Assert.Equal(200, (await _sim.GetAsync("health")).Status);
    }

    [Fact]
    public async Task With_admin_disabled_the_admin_endpoints_answer_404()
    {
        await using var sim = await HeatPumpSimulatorHost.StartAsync(_time, HeatPumpSimulatorHost.DefaultSettings(admin: false), new RecordingHandler());

        Assert.Equal(404, (await sim.PostAsync("admin/heat-pumps/hp_1/redeliver")).Status);
        Assert.Equal(404, (await sim.PostAsync("admin/heat-pumps/hp_1/mode/Standby")).Status);
        Assert.Equal(404, (await sim.GetAsync("admin/deliveries")).Status);
    }

    [Fact]
    public void Without_a_signing_secret_it_does_not_start_and_names_the_setting()
    {
        var settings = HeatPumpSimulatorHost.DefaultSettings();
        settings.Remove("Simulator:SigningSecret");

        var error = Assert.Throws<InvalidOperationException>(() => HeatPumpCloudSimulator.Build([.. settings.Select(kv => $"--{kv.Key}={kv.Value}")], _time));

        Assert.Contains("Simulator:SigningSecret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(HeatPumpSimulatorHost.Secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_callback_url_it_does_not_start_and_names_the_setting_without_the_secret()
    {
        var settings = HeatPumpSimulatorHost.DefaultSettings();
        settings.Remove("Simulator:CallbackUrl");

        var error = Assert.Throws<InvalidOperationException>(() => HeatPumpCloudSimulator.Build([.. settings.Select(kv => $"--{kv.Key}={kv.Value}")], _time));

        Assert.Contains("Simulator:CallbackUrl", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(HeatPumpSimulatorHost.Secret, error.Message, StringComparison.Ordinal);
    }
}
