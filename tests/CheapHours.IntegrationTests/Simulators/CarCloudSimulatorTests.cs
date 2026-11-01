using System.Text.Json;
using CheapHours.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.IntegrationTests.Simulators;

/// <summary>Every scenario of the <c>car-cloud-simulator</c> spec, on real Kestrel with a fake clock.</summary>
public sealed class CarCloudSimulatorTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero));
    private SimulatorHost _sim = null!;

    public async ValueTask InitializeAsync() => _sim = await SimulatorHost.StartAsync(_time);

    public async ValueTask DisposeAsync() => await _sim.DisposeAsync();

    private static decimal Number(JsonElement body, string name) => body.GetProperty(name).GetDecimal();

    [Fact]
    public async Task Refresh_rotates_the_token_and_the_used_one_stops_working()
    {
        var (status, body) = await _sim.RefreshAsync(SimulatorHost.SeedA);
        var (again, error) = await _sim.RefreshAsync(SimulatorHost.SeedA);

        Assert.Equal(200, status);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString()));
        Assert.Equal(86_400, body.GetProperty("expires_in").GetInt32());
        Assert.NotEqual(SimulatorHost.SeedA, body.GetProperty("refresh_token").GetString());
        Assert.Equal(400, again);
        Assert.Equal("invalid_grant", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrong_client_secret_is_invalid_client()
    {
        var (status, body) = await _sim.RefreshAsync(SimulatorHost.SeedA, clientSecret: "wrong");

        Assert.Equal(401, status);
        Assert.Equal("invalid_client", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_expired_access_token_is_refused()
    {
        var token = await _sim.AccessTokenAsync();
        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        var (status, _, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles", token);

        Assert.Equal(401, status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task Missing_or_unknown_tokens_are_refused(string? token)
    {
        var (status, _, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles", token);

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task The_61st_call_in_a_minute_is_rate_limited()
    {
        var token = await _sim.AccessTokenAsync();
        for (var i = 0; i < 60; i++)
        {
            Assert.Equal(200, (await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles", token)).Status);
        }

        var (status, _, response) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles", token);

        Assert.Equal(429, status);
        Assert.True(response.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles", token)).Status);
    }

    [Fact]
    public async Task The_token_endpoint_does_not_count_against_the_limit()
    {
        var seed = SimulatorHost.SeedA;
        for (var i = 0; i < 70; i++)
        {
            var (status, body) = await _sim.RefreshAsync(seed);
            Assert.Equal(200, status);
            seed = body.GetProperty("refresh_token").GetString()!;
        }
    }

    [Fact]
    public async Task An_account_lists_its_own_vehicles_with_battery_capacity()
    {
        var token = await _sim.AccessTokenAsync();

        var (_, body, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles", token);

        var vehicle = Assert.Single(body.GetProperty("vehicles").EnumerateArray());
        Assert.Equal("veh_a", vehicle.GetProperty("id").GetString());
        Assert.Equal(77m, Number(vehicle, "batteryCapacityKwh"));
        Assert.Equal("Voltwerk", vehicle.GetProperty("make").GetString());
    }

    [Fact]
    public async Task Another_accounts_vehicle_is_not_found()
    {
        var token = await _sim.AccessTokenAsync(SimulatorHost.SeedA);

        Assert.Equal(404, (await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_b/state", token)).Status);
        Assert.Equal(404, (await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_b/charging/start", token, "k1")).Status);
    }

    [Fact]
    public async Task Charging_for_30_minutes_adds_energy_and_state_of_charge()
    {
        var token = await _sim.AccessTokenAsync();
        Assert.Equal(202, (await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token, "start-1")).Status);
        _time.Advance(TimeSpan.FromMinutes(30));

        var (_, state, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_a/state", token);

        Assert.Equal("Charging", state.GetProperty("chargingState").GetString());
        Assert.Equal(5.5m, Number(state, "sessionEnergyKwh"));
        Assert.Equal(47.14m, Number(state, "socPercent"));
        Assert.Equal(11m, Number(state, "chargePowerKw"));
        Assert.True(state.GetProperty("pluggedIn").GetBoolean());
        Assert.Equal("2026-10-28T18:30:00.000Z", state.GetProperty("timestamp").GetString());
    }

    [Fact]
    public async Task A_full_battery_stops_charging_as_complete()
    {
        var token = await _sim.AccessTokenAsync();
        await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token, "start-1");
        _time.Advance(TimeSpan.FromHours(10));

        var (_, state, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_a/state", token);

        Assert.Equal("Complete", state.GetProperty("chargingState").GetString());
        Assert.Equal(100m, Number(state, "socPercent"));
        Assert.False(state.TryGetProperty("chargePowerKw", out _));
        Assert.Equal(46.2m, Number(state, "sessionEnergyKwh"));
    }

    [Fact]
    public async Task The_same_key_twice_starts_one_session()
    {
        var token = await _sim.AccessTokenAsync();

        var (first, firstBody, _) = await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token, "same-key");
        _time.Advance(TimeSpan.FromMinutes(30));
        var (second, secondBody, _) = await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token, "same-key");
        var (_, state, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_a/state", token);

        Assert.Equal(202, first);
        Assert.Equal(202, second);
        Assert.Equal(firstBody.GetProperty("requestId").GetString(), secondBody.GetProperty("requestId").GetString());
        Assert.Equal(5.5m, Number(state, "sessionEnergyKwh")); // not restarted by the repeat
    }

    [Fact]
    public async Task A_start_without_a_key_is_a_bad_request()
    {
        var token = await _sim.AccessTokenAsync();

        Assert.Equal(400, (await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token)).Status);
    }

    [Fact]
    public async Task Starting_an_unplugged_vehicle_is_not_plugged_in()
    {
        var token = await _sim.AccessTokenAsync();
        await _sim.SendAsync(HttpMethod.Post, "admin/vehicles/veh_a/unplug", null);

        var (status, body, _) = await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token, "k-unplugged");

        Assert.Equal(422, status);
        Assert.Equal("not_plugged_in", body.GetProperty("error").GetString());
        var (_, state, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_a/state", token);
        Assert.Equal("Unplugged", state.GetProperty("chargingState").GetString());
    }

    [Fact]
    public async Task Stopping_a_vehicle_that_is_not_charging_is_accepted_and_a_new_session_restarts_the_energy()
    {
        var token = await _sim.AccessTokenAsync();
        Assert.Equal(202, (await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/stop", token, "stop-0")).Status);
        await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token, "start-1");
        _time.Advance(TimeSpan.FromMinutes(30));
        await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/stop", token, "stop-1");
        var (_, stopped, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_a/state", token);

        await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token, "start-2");
        var (_, restarted, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_a/state", token);

        Assert.Equal("Stopped", stopped.GetProperty("chargingState").GetString());
        Assert.Equal(5.5m, Number(stopped, "sessionEnergyKwh"));
        Assert.Equal(0m, Number(restarted, "sessionEnergyKwh"));
        Assert.Equal(47.14m, Number(restarted, "socPercent"));
    }

    [Fact]
    public async Task A_revoked_account_refuses_its_refresh_and_access_tokens()
    {
        var (_, body) = await _sim.RefreshAsync(SimulatorHost.SeedA);
        var access = body.GetProperty("access_token").GetString()!;
        var refresh = body.GetProperty("refresh_token").GetString()!;

        Assert.Equal(204, (await _sim.SendAsync(HttpMethod.Post, "admin/accounts/acct_a/revoke", null)).Status);

        var (status, error) = await _sim.RefreshAsync(refresh);
        Assert.Equal(400, status);
        Assert.Equal("invalid_grant", error.GetProperty("error").GetString());
        Assert.Equal(401, (await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles", access)).Status);
    }

    [Fact]
    public async Task The_admin_start_charges_like_the_cars_own_app()
    {
        var token = await _sim.AccessTokenAsync();

        Assert.Equal(204, (await _sim.SendAsync(HttpMethod.Post, "admin/vehicles/veh_a/start", null)).Status);
        _time.Advance(TimeSpan.FromMinutes(30));
        var (_, state, _) = await _sim.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_a/state", token);
        await _sim.SendAsync(HttpMethod.Post, "admin/vehicles/veh_a/unplug", null);
        var (status, body, _) = await _sim.SendAsync(HttpMethod.Post, "admin/vehicles/veh_a/start", null);

        Assert.Equal("Charging", state.GetProperty("chargingState").GetString());
        Assert.Equal(5.5m, Number(state, "sessionEnergyKwh"));
        Assert.Equal(422, status);
        Assert.Equal("not_plugged_in", body.GetProperty("error").GetString());
        Assert.Equal(404, (await _sim.SendAsync(HttpMethod.Post, "admin/vehicles/veh_nope/start", null)).Status);
    }

    [Fact]
    public async Task Admin_endpoints_are_not_mapped_when_disabled()
    {
        await using var sim = await SimulatorHost.StartAsync(_time, SimulatorHost.DefaultSettings(admin: false));

        Assert.Equal(404, (await sim.SendAsync(HttpMethod.Post, "admin/vehicles/veh_a/plug-in", null)).Status);
        Assert.Equal(404, (await sim.SendAsync(HttpMethod.Post, "admin/accounts/acct_a/revoke", null)).Status);
    }

    [Fact]
    public async Task A_restart_returns_to_the_configured_seed()
    {
        var token = await _sim.AccessTokenAsync(); // rotates the seed
        await _sim.SendAsync(HttpMethod.Post, "api/v1/vehicles/veh_a/charging/start", token, "start-1");
        _time.Advance(TimeSpan.FromMinutes(30));

        await using var restarted = await SimulatorHost.StartAsync(_time);
        var fresh = await restarted.AccessTokenAsync(); // the seed works again
        var (_, state, _) = await restarted.SendAsync(HttpMethod.Get, "api/v1/vehicles/veh_a/state", fresh);

        Assert.Equal(40m, Number(state, "socPercent"));
        Assert.Equal("Stopped", state.GetProperty("chargingState").GetString());
    }

    [Fact]
    public async Task Health_answers_200()
    {
        using var response = await _sim.Client.GetAsync(new Uri("health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public void A_missing_client_secret_stops_the_simulator()
    {
        var settings = SimulatorHost.DefaultSettings();
        settings.Remove("Simulator:ClientSecret");

        var error = Assert.Throws<InvalidOperationException>(() => CheapHours.Simulators.CarCloud.CarCloudSimulator.Build([.. settings.Select(kv => $"--{kv.Key}={kv.Value}")]));

        Assert.Contains("Simulator:ClientSecret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SimulatorHost.SeedA, error.Message, StringComparison.Ordinal);
    }
}
