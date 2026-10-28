using CheapHours.IntegrationTests.Infrastructure;
using CheapHours.Messaging;
using Microsoft.Extensions.Logging;

namespace CheapHours.IntegrationTests.Messaging;

public sealed class RabbitMqConnectionTests(RabbitMqFixture rabbit)
{
    [Fact]
    public async Task Connects_and_reports_open()
    {
        await using var connection = new RabbitMqConnection(rabbit.ConnectionString, "connection-tests", Logger(new CapturingLoggerProvider()));

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.True(connection.IsOpen);
    }

    [Fact]
    public void Nothing_is_open_before_connecting()
    {
        var connection = new RabbitMqConnection(rabbit.ConnectionString, "connection-tests", Logger(new CapturingLoggerProvider()));

        Assert.False(connection.IsOpen);
        Assert.Throws<BrokerUnavailableException>(() => connection.Current);
    }

    [Fact]
    public async Task A_wrong_password_logs_neither_the_password_nor_the_uri()
    {
        var uri = new Uri(rabbit.ConnectionString);
        const string wrongPassword = "not-the-password-7f3a";
        var wrong = $"amqp://{Uri.UnescapeDataString(uri.UserInfo.Split(':')[0])}:{wrongPassword}@{uri.Host}:{uri.Port}/";
        var logs = new CapturingLoggerProvider();
        await using var connection = new RabbitMqConnection(wrong, "connection-tests", Logger(logs));

        var error = await Assert.ThrowsAsync<BrokerUnavailableException>(() => connection.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.True(logs.Contains($"{uri.Host}:{uri.Port}"));
        Assert.False(logs.Contains(wrongPassword));
        Assert.False(logs.Contains("amqp://"));
        Assert.DoesNotContain(wrongPassword, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("amqp://", error.Message, StringComparison.Ordinal);
    }

    private static ILogger<RabbitMqConnection> Logger(CapturingLoggerProvider provider) =>
        LoggerFactory.Create(b => b.AddProvider(provider)).CreateLogger<RabbitMqConnection>();
}
