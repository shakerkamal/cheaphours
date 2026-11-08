using CheapHours.Persistence.Commands;

namespace CheapHours.Host.Commands;

/// <summary>What a direct command request gave; the API maps each case to one status code.</summary>
internal abstract record CommandRequestOutcome
{
    private CommandRequestOutcome()
    {
    }

    /// <summary>The command is stored with its outbox entry (202).</summary>
    /// <param name="Command">The command.</param>
    public sealed record Accepted(StoredCommand Command) : CommandRequestOutcome;

    /// <summary>The device does not exist for this owner (404).</summary>
    public sealed record NotFound : CommandRequestOutcome;

    /// <summary>The device does not declare the capability the command needs (422 <c>capability-not-supported</c>).</summary>
    /// <param name="Detail">Why, for the problem's detail.</param>
    public sealed record CapabilityNotSupported(string Detail) : CommandRequestOutcome;

    /// <summary>The device cannot carry out the command: removed, or a power above its maximum (422 <c>unprocessable</c>).</summary>
    /// <param name="Detail">Why, for the problem's detail.</param>
    public sealed record Unprocessable(string Detail) : CommandRequestOutcome;
}
