using System.Diagnostics.Metrics;
using CheapHours.Core.Commands;
using CheapHours.Host.Observability;
using CheapHours.Persistence.Commands;

namespace CheapHours.Host.Commands;

/// <summary>
/// The command metrics (operational-metrics), recorded only after the state change is stored: a write that lost the
/// race records nothing, so a metric never counts a transition that did not happen. Tags are the provider and the
/// command type (and the dead-letter reason), never a command or device id.
/// </summary>
internal sealed class CommandMetrics
{
    /// <summary>The time-to-confirmation histogram's name (seconds).</summary>
    public const string ConfirmationDurationName = "cheaphours.command.confirmation.duration";

    /// <summary>The dead-lettered commands counter's name.</summary>
    public const string DeadLetteredName = "cheaphours.command.dead_lettered";

    /// <summary>The outbox lag gauge's name (seconds).</summary>
    public const string OutboxLagName = "cheaphours.outbox.lag";

    /// <summary>The last allowed send failed.</summary>
    public const string AttemptsExhausted = "attempts-exhausted";

    /// <summary>The monitor announced the command too often without a send.</summary>
    public const string DispatchKeptFailing = "dispatch-kept-failing";

    /// <summary>The reconcile gave up on the command.</summary>
    public const string NotConfirmed = "not-confirmed";

    private const string UnknownProvider = "unknown";

    private readonly Histogram<double> _confirmation;
    private readonly Counter<long> _deadLettered;

    // Seconds, or NaN while this replica does not run the relay: then the gauge reports nothing.
    private double _outboxLag = double.NaN;

    public CommandMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(CheapHoursDiagnostics.CommandsName);
        _confirmation = meter.CreateHistogram<double>(ConfirmationDurationName, unit: "s", description: "Time from a command being due to its confirmation.");
        _deadLettered = meter.CreateCounter<long>(DeadLetteredName, unit: "{command}", description: "Commands dead-lettered, by reason.");
        meter.CreateObservableGauge(OutboxLagName, ObserveOutboxLag, unit: "s", description: "Age of the oldest due, unpublished outbox entry; 0 when there is none.");
    }

    /// <summary>
    /// Sets the outbox lag measured by a relay pass on the lease holder, or clears it (<see langword="null"/>) when
    /// this replica did not get the lease, so only the replica that relays reports it.
    /// </summary>
    /// <param name="lag">Now minus the oldest due entry's time, zero when none is due, or null.</param>
    public void OutboxLag(TimeSpan? lag) =>
        Volatile.Write(ref _outboxLag, lag is { } value ? Math.Max(0, value.TotalSeconds) : double.NaN);

    /// <summary>
    /// Records a stored confirmation: the time from when the command was due (not created: a planned command waits
    /// for its slot) to the confirmation.
    /// </summary>
    /// <param name="command">The command as saved, <c>Confirmed</c>; nothing is recorded when nothing was saved.</param>
    /// <param name="providerId">Its device's provider.</param>
    public void Confirmed(StoredCommand? command, string? providerId)
    {
        if (command?.Command.State != CommandState.Confirmed)
        {
            return;
        }

        var seconds = (command.Command.History[^1].At - command.Command.DueAt).TotalSeconds;
        _confirmation.Record(Math.Max(0, seconds), Tags(command, providerId));
    }

    /// <summary>Records a stored dead-lettering.</summary>
    /// <param name="command">The command as saved, <c>DeadLettered</c>; nothing is recorded when nothing was saved.</param>
    /// <param name="providerId">Its device's provider.</param>
    /// <param name="reason"><see cref="AttemptsExhausted"/>, <see cref="DispatchKeptFailing"/> or <see cref="NotConfirmed"/>.</param>
    public void DeadLettered(StoredCommand? command, string? providerId, string reason)
    {
        if (command?.Command.State != CommandState.DeadLettered)
        {
            return;
        }

        var tags = Tags(command, providerId);
        tags.Add("reason", reason);
        _deadLettered.Add(1, tags);
    }

    private IEnumerable<Measurement<double>> ObserveOutboxLag()
    {
        var lag = Volatile.Read(ref _outboxLag);
        return double.IsNaN(lag) ? [] : [new Measurement<double>(lag)];
    }

    private static System.Diagnostics.TagList Tags(StoredCommand command, string? providerId) => new()
    {
        { "provider.id", providerId ?? UnknownProvider },
        { "command.type", command.Command.Request.Type.ToString() },
    };
}
