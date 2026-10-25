using CheapHours.Host.Gateway;
using CheapHours.Providers.Ocpp;

namespace CheapHours.UnitTests.Host;

public sealed class GatewayOptionsTests
{
    private static readonly string Hash = new('a', 64);

    private static OcppOptions Ocpp(params ChargePointOptions[] chargePoints)
    {
        var options = new OcppOptions();
        foreach (var cp in chargePoints)
        {
            options.ChargePoints.Add(cp);
        }

        return options;
    }

    private static ChargePointOptions Cp(string id = "CP001", string owner = "own_demo", string? hash = null, int maxPowerW = 11000) =>
        new() { Id = id, OwnerId = owner, PasswordHash = hash ?? Hash, MaxPowerW = maxPowerW };

    private static IEnumerable<string> Errors(OcppOptions options) =>
        new OcppOptionsValidator().Validate(null, options).Failures ?? [];

    [Fact]
    public void Valid_registration_passes()
    {
        Assert.True(new OcppOptionsValidator().Validate(null, Ocpp(Cp())).Succeeded);
    }

    [Fact]
    public void No_registrations_is_valid()
    {
        Assert.True(new OcppOptionsValidator().Validate(null, Ocpp()).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Bad_hash_names_the_charger_but_not_the_hash(string hash)
    {
        var error = Assert.Single(Errors(Ocpp(Cp(hash: hash))));

        Assert.Contains("ChargePoints:0 (CP001)", error, StringComparison.Ordinal);
        Assert.Contains("PasswordHash", error, StringComparison.Ordinal);
        if (hash.Length > 0)
        {
            Assert.DoesNotContain(hash, error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Upper_case_hex_is_accepted()
    {
        Assert.Empty(Errors(Ocpp(Cp(hash: new string('A', 64)))));
    }

    [Fact]
    public void Missing_hash_is_an_error()
    {
        Assert.Single(Errors(Ocpp(Cp(hash: ""))));
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("")]
    [InlineData("CP/1")]
    public void Bad_id_is_named_by_index(string id)
    {
        var error = Assert.Single(Errors(Ocpp(Cp(id: id))));

        Assert.Contains("ChargePoints:0:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_ids_are_errors()
    {
        Assert.Contains(Errors(Ocpp(Cp(), Cp())), e => e.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_owner_and_power_are_errors()
    {
        var errors = Errors(Ocpp(Cp(owner: "demo", maxPowerW: 0))).ToList();

        Assert.Contains(errors, e => e.Contains("OwnerId", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("MaxPowerW", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void Interval_out_of_range_is_an_error(int seconds)
    {
        var options = Ocpp();
        options.HeartbeatIntervalSeconds = seconds;

        Assert.Contains(Errors(options), e => e.Contains("HeartbeatIntervalSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("gw-1", true)]
    [InlineData("cheaphours-gateway-7d9f", true)]
    [InlineData("GW-1", false)]
    [InlineData("gw.1", false)]
    [InlineData("", false)]
    public void Replica_id_rules(string replicaId, bool valid)
    {
        var result = new GatewayOptionsValidator().Validate(null, new GatewayOptions { ReplicaId = replicaId });

        Assert.Equal(valid, result.Succeeded);
    }

    [Theory]
    [InlineData("DESKTOP-ABC123", "desktop-abc123")]
    [InlineData("my_host.local", "my-host-local")]
    [InlineData("___", "gateway")]
    public void Default_replica_id_is_derived_from_the_machine_name(string machine, string expected)
    {
        Assert.Equal(expected, GatewayOptions.DefaultReplicaId(machine));
        Assert.True(GatewayOptions.IsValidReplicaId(GatewayOptions.DefaultReplicaId(machine)));
    }

    [Fact]
    public void Long_machine_name_is_cut_to_63_characters()
    {
        Assert.Equal(63, GatewayOptions.DefaultReplicaId(new string('x', 100)).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Drain_timeout_out_of_range_is_an_error(int seconds)
    {
        Assert.False(new GatewayOptionsValidator().Validate(null, new GatewayOptions { ReplicaId = "gw-1", DrainTimeoutSeconds = seconds }).Succeeded);
    }

    [Theory]
    [InlineData("CP001", true)]
    [InlineData("a.b_c:d-e", true)]
    [InlineData("bad id", false)]
    [InlineData("CP001\n", false)]
    [InlineData("", false)]
    public void Charge_point_id_rules(string id, bool valid)
    {
        Assert.Equal(valid, OcppChargePointId.IsValid(id));
    }

    [Fact]
    public void Charge_point_id_is_at_most_48_characters()
    {
        Assert.True(OcppChargePointId.IsValid(new string('x', 48)));
        Assert.False(OcppChargePointId.IsValid(new string('x', 49)));
    }
}
