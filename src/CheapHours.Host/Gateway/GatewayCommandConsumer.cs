using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Host.Commands;
using CheapHours.Host.Observability;
using CheapHours.Messaging;
using CheapHours.Messaging.Contracts;
using CheapHours.Messaging.Contracts.Json;
using CheapHours.Persistence.Devices;
using CheapHours.Providers.Ocpp;
using CheapHours.Providers.Ocpp.Messages;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace CheapHours.Host.Gateway;

/// <summary>
/// Carries out the commands addressed to this replica (command-dispatch design §7, ADR-008). Once the gateway is
/// initialised it declares <c>gateway.{replicaId}.commands</c> and consumes it; when the gateway drains it stops
/// consuming. For each <see cref="GatewayCommand"/> it finds the charger's connection, answers a start for a charger
/// that is charging and a stop for one that is not as already in effect without a call, otherwise sends the OCPP
/// call and waits for the answer, then publishes one <see cref="GatewayCommandResult"/> and acknowledges the command.
/// A result that cannot be published is retried twice, then the command goes back on the queue.
/// </summary>
internal sealed partial class GatewayCommandConsumer(
    RabbitMqConnection broker,
    IMessagePublisher publisher,
    MessagingTopology topology,
    ConnectionTable connections,
    IDeviceCommandStateStore devices,
    GatewayState state,
    GatewayMetrics metrics,
    IOptions<GatewayOptions> gatewayOptions,
    IOptions<GatewayCommandsOptions> options,
    TimeProvider time,
    ILogger<GatewayCommandConsumer> logger) : BackgroundService
{
    /// <summary>The reason for a command whose charger this replica does not hold.</summary>
    public const string NotHeld = "the charger is not connected to this replica";

    /// <summary>The span tag of the OCPP action.</summary>
    public const string OcppActionTag = "ocpp.action";

    /// <summary>The span tag of the charge point id.</summary>
    public const string ChargePointIdTag = "cheaphours.charge_point.id";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PublishRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly GatewayCommandsOptions _options = options.Value;
    private readonly string _replicaId = gatewayOptions.Value.ReplicaId;

    /// <summary>Carries out one command and returns its outcome, without publishing it.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The send result, and when the charger answered if it did.</returns>
    public async Task<(SendResult Result, DateTimeOffset? AnsweredAt)> CarryOutAsync(GatewayCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (connections.Find(command.ChargePointId) is not { } connection)
        {
            return (new SendResult.Transient(NotHeld), null);
        }

        StoredDevice? device;
        try
        {
            device = await devices.FindAsync(OcppProvider.Id, command.ChargePointId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return (new SendResult.Transient("the charger's state could not be read"), null);
        }

        var activeSession = device?.ActiveSessionId;
        var request = command.Command;
        if ((request.Type == CommandType.StartCharging && activeSession is not null)
            || (request.Type == CommandType.StopCharging && activeSession is null))
        {
            return (new SendResult.Accepted(CommandVendorReferences.AlreadyInEffect), null);
        }

        var transactionId = int.TryParse(activeSession, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null;
        var call = OcppCommandCalls.For(request, _options.ConnectorId, _options.IdTag, transactionId);

        // The call to the charger is a client span under the command's handling span.
        using var span = CheapHoursDiagnostics.Gateway.StartActivity($"ocpp {call.Action}", ActivityKind.Client);
        span?.SetTag(OcppActionTag, call.Action)
            .SetTag(ChargePointIdTag, command.ChargePointId)
            .SetTag(CommandTracing.ProviderIdTag, OcppProvider.Id)
            .SetTag(CommandTracing.CommandIdTag, command.CommandId.Value);
        var started = time.GetTimestamp();
        var called = await connection.CallAsync(call.Action, call.Payload, _options.CallTimeout, time, cancellationToken).ConfigureAwait(false);
        metrics.RecordCall(called is ChargerCallOutcome.Replied, time.GetElapsedTime(started));
        switch (called)
        {
            case ChargerCallOutcome.Replied replied:
                span?.SetTag(CommandTracing.OutcomeTag, "success");
                return (OcppCommandCalls.Interpret(replied.UniqueId, replied.Reply), replied.ReceivedAt);
            case ChargerCallOutcome.TimedOut:
                span?.SetTag(CommandTracing.OutcomeTag, "transient").SetStatus(ActivityStatusCode.Error).SetTag("cheaphours.failure", "timed-out");
                LogCallTimedOut(call.Action, command.ChargePointId, _options.CallTimeout);
                return (new SendResult.Transient($"the charger did not answer {call.Action} within {_options.CallTimeout.TotalSeconds:0} s"), null);
            default:
                span?.SetTag(CommandTracing.OutcomeTag, "transient").SetStatus(ActivityStatusCode.Error).SetTag("cheaphours.failure", "connection-closed");
                LogCallClosed(call.Action, command.ChargePointId);
                return (new SendResult.Transient("the charger's connection closed during the call"), null);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!state.IsInitialised)
            {
                await Task.Delay(PollInterval, time, stoppingToken).ConfigureAwait(false);
            }

            // A broker outage must not stop the gateway: wait and declare and consume again.
            while (!state.IsDraining)
            {
                try
                {
                    await ConsumeAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsBrokerFailure(ex) && !stoppingToken.IsCancellationRequested)
                {
                    LogBrokerDown(ex.GetType().Name, RetryDelay);
                    await Task.Delay(RetryDelay, time, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsBrokerFailure(Exception ex) =>
        ex is BrokerUnavailableException or OperationInterruptedException or AlreadyClosedException or BrokerUnreachableException or IOException;

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var queue = topology.GatewayCommandQueueDefinition(_replicaId, _options.QueueExpiry);
        await TopologyDeclarer.DeclareQueueAsync(broker.Current, queue, stoppingToken).ConfigureAwait(false);

        var channel = await broker.Current.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            await channel.BasicQosAsync(0, (ushort)_options.Prefetch, global: false, stoppingToken).ConfigureAwait(false);
            var inFlight = new List<Task>();
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivered) =>
            {
                // Each command runs on its own, so one silent charger does not hold up the others.
                var body = delivered.Body.ToArray();
                var work = HandleDeliveryAsync(channel, delivered.DeliveryTag, body, stoppingToken);
                lock (inFlight)
                {
                    inFlight.RemoveAll(t => t.IsCompleted);
                    inFlight.Add(work);
                }

                return Task.CompletedTask;
            };

            var tag = await channel.BasicConsumeAsync(queue.Name, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);
            LogConsuming(queue.Name);

            try
            {
                while (!state.IsDraining)
                {
                    await Task.Delay(PollInterval, time, stoppingToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // Draining: take no new commands; finish the ones in hand.
                await CancelAsync(channel, tag).ConfigureAwait(false);
                Task[] pending;
                lock (inFlight)
                {
                    pending = [.. inFlight];
                }

                await Task.WhenAll(pending).WaitAsync(_options.CallTimeout + TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                LogStopped(queue.Name);
            }
        }
    }

    private async Task HandleDeliveryAsync(IChannel channel, ulong deliveryTag, byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            GatewayCommand? command;
            try
            {
                command = JsonSerializer.Deserialize<GatewayCommand>(body, CheapHoursJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
            {
                command = null;
            }

            if (command is null)
            {
                LogUnreadable();
                await channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Handled under the command's trace context, with the command named in every log entry.
            using var process = MessagingTracing.StartProcess(topology.GatewayCommandQueue(_replicaId), command);
            using var scope = LogScopes.ForCommand(logger, command.CommandId, command.Command.DeviceId, OcppProvider.Id);
            var (outcome, answeredAt) = await CarryOutAsync(command, cancellationToken).ConfigureAwait(false);
            var kind = Kind(outcome);
            LogOutcome(command.CommandId.Value, command.ChargePointId, kind);

            // An answered call is stamped with the answer's receipt, before any status the charger sent after it.
            var now = answeredAt ?? time.GetUtcNow();
            var result = new GatewayCommandResult(Guid.CreateVersion7(now), now, command.Traceparent, command.CommandId, outcome);
            if (await TryPublishAsync(result, cancellationToken).ConfigureAwait(false))
            {
                await channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The redelivery repeats the precheck, so a start that already began a session is not sent again.
                await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or ObjectDisposedException or OperationCanceledException)
        {
            // The channel went away: the broker redelivers the command, and the precheck keeps it from acting twice.
        }
    }

    private async Task<bool> TryPublishAsync(GatewayCommandResult result, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await publisher.PublishAsync(result, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (BrokerUnavailableException ex)
            {
                var reason = ex.InnerException?.GetType().Name ?? nameof(BrokerUnavailableException);
                LogPublishFailed(result.CommandId.Value, attempt, reason);
                if (attempt < 3)
                {
                    await Task.Delay(PublishRetryDelay, time, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return false;
    }

    private static async Task CancelAsync(IChannel channel, string consumerTag)
    {
        try
        {
            await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException or ObjectDisposedException)
        {
            // The channel is gone already; the broker stopped delivering with it.
        }
    }

    private static string Kind(SendResult result) => result switch
    {
        SendResult.Accepted => "Accepted",
        SendResult.Rejected => "Rejected",
        _ => "Transient",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Charger {ChargePointId} did not answer {Action} within {Timeout}")]
    private partial void LogCallTimedOut(string action, string chargePointId, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Charger {ChargePointId}'s connection closed during {Action}")]
    private partial void LogCallClosed(string action, string chargePointId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming gateway commands from {Queue}")]
    private partial void LogConsuming(string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped consuming gateway commands from {Queue}")]
    private partial void LogStopped(string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandId} for {ChargePointId}: {Outcome}")]
    private partial void LogOutcome(string commandId, string chargePointId, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The broker is not reachable for gateway commands ({Error}); retrying in {Delay}")]
    private partial void LogBrokerDown(string error, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An unreadable gateway command was dropped")]
    private partial void LogUnreadable();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish the result of command {CommandId} (attempt {Attempt}, {Reason})")]
    private partial void LogPublishFailed(string commandId, int attempt, string reason);
}
