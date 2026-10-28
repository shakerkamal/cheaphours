using CheapHours.Messaging;

namespace CheapHours.UnitTests.Messaging;

public sealed class MessagingOptionsTests
{
    private static readonly MessagingOptionsValidator Validator = new();

    private static bool IsValid(MessagingOptions options) => Validator.Validate(null, options).Succeeded;

    [Fact]
    public void Defaults_follow_the_design_and_are_valid()
    {
        var options = new MessagingOptions();

        Assert.Equal(string.Empty, options.QueuePrefix);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)], options.RetryDelays);
        Assert.Equal(200, options.BatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.BatchWait);
        Assert.Equal(400, options.Prefetch);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PublishTimeout);
        Assert.True(IsValid(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_retry_delay_is_refused(int milliseconds)
    {
        Assert.False(IsValid(new MessagingOptions { SecondRetryDelay = TimeSpan.FromMilliseconds(milliseconds) }));
    }

    [Fact]
    public void A_batch_larger_than_the_prefetch_is_refused()
    {
        Assert.False(IsValid(new MessagingOptions { BatchSize = 500, Prefetch = 400 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70_000)]
    public void A_prefetch_outside_the_protocol_range_is_refused(int prefetch)
    {
        Assert.False(IsValid(new MessagingOptions { Prefetch = prefetch, BatchSize = 1 }));
    }

    [Fact]
    public void Non_positive_waits_and_timeouts_are_refused()
    {
        Assert.False(IsValid(new MessagingOptions { BatchWait = TimeSpan.Zero }));
        Assert.False(IsValid(new MessagingOptions { PublishTimeout = TimeSpan.Zero }));
    }

    [Theory]
    [InlineData("has space.")]
    [InlineData("amq.")]
    public void An_unusable_prefix_is_refused(string prefix)
    {
        Assert.False(IsValid(new MessagingOptions { QueuePrefix = prefix }));
    }
}
