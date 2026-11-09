using CheapHours.Host;
using CheapHours.Host.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace CheapHours.UnitTests.Host;

public sealed class ObservabilitySetupTests
{
    [Theory]
    [InlineData(nameof(HostRole.Api), "cheaphours-api")]
    [InlineData(nameof(HostRole.Gateway), "cheaphours-gateway")]
    [InlineData(nameof(HostRole.Worker), "cheaphours-worker")]
    public void Resource_names_the_role_and_its_instance(string role, string serviceName)
    {
        using var host = Build(Enum.Parse<HostRole>(role), "gw-a");

        var resource = host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal(serviceName, resource["service.name"]);
        Assert.Equal("gw-a", resource["service.instance.id"]);
        Assert.False(string.IsNullOrWhiteSpace(resource["service.version"] as string));
    }

    [Fact]
    public void Service_name_is_the_role_in_lower_case()
    {
        Assert.Equal("cheaphours-worker", ObservabilitySetup.ServiceName(HostRole.Worker));
    }

    [Fact]
    public void Instance_id_is_the_gateway_replica_id_or_the_machine_name()
    {
        var gateway = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:ReplicaId"] = "gw-a" }).Build();
        var empty = new ConfigurationBuilder().Build();

        Assert.Equal("gw-a", ObservabilitySetup.InstanceId(HostRole.Gateway, gateway));
        Assert.Equal(Environment.MachineName, ObservabilitySetup.InstanceId(HostRole.Worker, gateway));
        Assert.Equal(Environment.MachineName, ObservabilitySetup.InstanceId(HostRole.Api, empty));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("http://localhost:4317", true)]
    public void Exports_only_when_an_endpoint_is_configured(string? endpoint, bool exports)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ObservabilitySetup.OtlpEndpointKey] = endpoint })
            .Build();

        Assert.Equal(exports, ObservabilitySetup.ExportsTo(configuration));
    }

    [Fact]
    public void Health_and_ocpp_requests_are_not_traced()
    {
        Assert.False(ObservabilitySetup.TracesRequest("/health/ready"));
        Assert.False(ObservabilitySetup.TracesRequest("/health/live"));
        Assert.False(ObservabilitySetup.TracesRequest("/ocpp/CP001"));
        Assert.True(ObservabilitySetup.TracesRequest("/v1/devices"));
        Assert.True(ObservabilitySetup.TracesRequest("/webhooks/heatpumpcloud/acct_demo"));
        Assert.True(ObservabilitySetup.TracesRequest("/healthy"));
    }

    private static IHost Build(HostRole role, string instanceId)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddCheapHoursObservability(role, instanceId);
        return builder.Build();
    }
}
