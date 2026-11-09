using System.Diagnostics;
using CheapHours.Host.Observability;
using CheapHours.Messaging;
using CheapHours.Persistence.Commands;

namespace CheapHours.Host.Commands;

/// <summary>
/// The spans of work on one command (distributed-tracing: "A command's later steps join its trace"). A span is a
/// child of the current span when that belongs to the command's trace (a dispatch under its message's span), and
/// otherwise continues the trace stored on the command, however long after the request it runs.
/// </summary>
internal static class CommandTracing
{
    /// <summary>The span tag of the command id.</summary>
    public const string CommandIdTag = "cheaphours.command.id";

    /// <summary>The span tag of the device id.</summary>
    public const string DeviceIdTag = "cheaphours.device.id";

    /// <summary>The span tag of the provider id.</summary>
    public const string ProviderIdTag = "cheaphours.provider.id";

    /// <summary>The span tag of the command type.</summary>
    public const string CommandTypeTag = "cheaphours.command.type";

    /// <summary>The span tag of the classified outcome.</summary>
    public const string OutcomeTag = "cheaphours.outcome";

    /// <summary>The span tag of a state-change reason.</summary>
    public const string ReasonTag = "cheaphours.reason";

    /// <summary>Starts a span about <paramref name="command"/>.</summary>
    /// <param name="name">The span name, for example <c>dispatch StartCharging</c> or <c>command confirm</c>.</param>
    /// <param name="command">The command.</param>
    /// <param name="providerId">The device's provider, once known.</param>
    /// <returns>The scope; dispose it when the work ends.</returns>
    public static SpanScope Start(string name, StoredCommand command, string? providerId)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = Activity.Current;

        // A command stored without a trace context nests under the current span, or starts a trace of its own.
        var parent = SpanScope.TryParse(command.Traceparent, out var stored)
            ? (current is not null && current.TraceId == stored.TraceId ? current.Id : command.Traceparent)
            : current?.Id;
        return SpanScope.Start(CheapHoursDiagnostics.Commands, name, ActivityKind.Internal, parent)
            .Tag(CommandIdTag, command.Id.Value)
            .Tag(DeviceIdTag, command.Command.Request.DeviceId.Value)
            .Tag(ProviderIdTag, providerId)
            .Tag(CommandTypeTag, command.Command.Request.Type.ToString());
    }
}
