using System.Text;
using RabbitMQ.Client;

namespace CheapHours.IntegrationTests.Infrastructure;

public sealed class RabbitMqFixtureTests(RabbitMqFixture rabbit)
{
    [Fact]
    public async Task Broker_routes_a_message_to_a_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new ConnectionFactory { Uri = new Uri(rabbit.ConnectionString) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var queue = RabbitMqFixture.NewPrefix(nameof(RabbitMqFixtureTests)) + "smoke";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: ct);

        await channel.BasicPublishAsync(string.Empty, queue, Encoding.UTF8.GetBytes("hello"), ct);

        BasicGetResult? result = null;
        for (var i = 0; i < 50 && result is null; i++)
        {
            result = await channel.BasicGetAsync(queue, autoAck: true, ct);
            if (result is null)
            {
                await Task.Delay(100, ct);
            }
        }

        Assert.NotNull(result);
        Assert.Equal("hello", Encoding.UTF8.GetString(result.Body.Span));
    }
}
