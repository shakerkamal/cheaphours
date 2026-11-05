using CheapHours.Host.Prices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CheapHours.UnitTests.Prices;

public sealed class PricesOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_match_the_design()
    {
        var options = new PricesOptions();

        Assert.True(new PricesOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal("DE-LU", options.Zone);
        Assert.Equal(new Uri("https://api.energy-charts.info"), options.BaseUrl);
        Assert.True(options.ImportEnabled);
        Assert.Equal(new TimeOnly(13, 0), options.FirstAttemptAt);
        Assert.Equal(new TimeOnly(20, 0), options.GiveUpAt);
        Assert.Equal(TimeSpan.FromHours(1), options.RetryInterval);
    }

    [Theory]
    [InlineData("Zone", "Zone")]
    [InlineData("BaseUrl", "BaseUrl")]
    [InlineData("FirstAttemptAt", "FirstAttemptAt")]
    [InlineData("RetryInterval-short", "RetryInterval")]
    [InlineData("RetryInterval-long", "RetryInterval")]
    [InlineData("AttemptTimeout", "AttemptTimeout")]
    [InlineData("MaxRetries", "MaxRetries")]
    [InlineData("LeaseDuration", "LeaseDuration")]
    public void Broken_value_is_named_by_key(string change, string key)
    {
        var options = new PricesOptions();
        Action<PricesOptions> breakIt = change switch
        {
            "Zone" => o => o.Zone = " ",
            "BaseUrl" => o => o.BaseUrl = new Uri("ftp://example.org"),
            "FirstAttemptAt" => o => o.FirstAttemptAt = new TimeOnly(21, 0),
            "RetryInterval-short" => o => o.RetryInterval = TimeSpan.FromSeconds(30),
            "RetryInterval-long" => o => o.RetryInterval = TimeSpan.FromHours(7),
            "AttemptTimeout" => o => o.AttemptTimeout = TimeSpan.FromMinutes(1),
            "MaxRetries" => o => o.MaxRetries = 11,
            _ => o => o.LeaseDuration = TimeSpan.FromSeconds(60),
        };
        breakIt(options);

        var result = new PricesOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.StartsWith("Prices: " + key, StringComparison.Ordinal));
    }

    [Fact]
    public void Problems_never_contain_the_value()
    {
        var options = new PricesOptions { BaseUrl = new Uri("ftp://secret-host.example") };

        var result = new PricesOptionsValidator().Validate(null, options);

        Assert.DoesNotContain(result.Failures!, f => f.Contains("secret-host", StringComparison.Ordinal));
    }

    [Fact]
    public void Times_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Prices:FirstAttemptAt"] = "12:30",
            ["Prices:GiveUpAt"] = "21:00",
            ["Prices:RetryInterval"] = "00:30:00",
        }).Build();
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddPriceOptions().BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<PricesOptions>>().Value;

        Assert.Equal(new TimeOnly(12, 30), options.FirstAttemptAt);
        Assert.Equal(new TimeOnly(21, 0), options.GiveUpAt);
        Assert.Equal(TimeSpan.FromMinutes(30), options.RetryInterval);
    }
}
