using System.Net;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Time.Testing;

namespace CheapHours.UnitTests.Kit;

public sealed class RetryAfterParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Http_date_is_relative_to_the_clock()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryAddWithoutValidation("Retry-After", "Wed, 28 Oct 2026 18:00:45 GMT");

        Assert.Equal(TimeSpan.FromSeconds(45), RetryAfterParser.From(response, new FakeTimeProvider(Now)));
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", 0)]
    [InlineData(" 120 ", 120)]
    public void Delta_seconds(string value, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryAfterParser.Parse(value, Now));
    }

    [Theory]
    [InlineData("7200")]
    [InlineData("99999999999999999999999")]
    [InlineData("Wed, 28 Oct 2026 21:00:00 GMT")]
    public void Values_above_an_hour_are_capped(string value)
    {
        Assert.Equal(TimeSpan.FromHours(1), RetryAfterParser.Parse(value, Now));
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("1.5")]
    [InlineData("Wed, 28 Oct 2026 17:59:00 GMT")]
    [InlineData("")]
    [InlineData(null)]
    public void Unusable_values_are_ignored(string? value)
    {
        Assert.Null(RetryAfterParser.Parse(value, Now));
    }

    [Fact]
    public void Missing_header_gives_nothing()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.Null(RetryAfterParser.From(response, new FakeTimeProvider(Now)));
    }
}
