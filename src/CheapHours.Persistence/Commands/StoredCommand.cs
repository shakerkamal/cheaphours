using CheapHours.Core.Commands;
using CheapHours.Core.Ids;

namespace CheapHours.Persistence.Commands;

/// <summary>
/// A command as stored: its lifecycle, who it belongs to, the plan version that made it (absent for direct
/// commands), and the dispatch bookkeeping. <see cref="Version"/> guards every write: a save succeeds only
/// against the version it was read at.
/// </summary>
/// <param name="Command">The command and its lifecycle.</param>
/// <param name="OwnerId">The owner (D3).</param>
/// <param name="Traceparent">The trace context of the request or message that created it.</param>
/// <param name="PlanId">The plan that made it, or <see langword="null"/> for a direct command.</param>
/// <param name="PlanVersion">The plan version that made it, or <see langword="null"/> for a direct command.</param>
/// <param name="Claim">A dispatcher's hold on it, if any.</param>
/// <param name="Announcements">How many re-announcements counted toward the dispatch limit.</param>
/// <param name="LastAnnouncedAt">When it was last announced, if ever.</param>
/// <param name="AlreadyInEffect">Whether the route answered that the device was already in the requested state.</param>
/// <param name="Version">The document version this copy was read at.</param>
public sealed record StoredCommand(
    Command Command,
    OwnerId OwnerId,
    string? Traceparent,
    PlanId? PlanId = null,
    int? PlanVersion = null,
    CommandClaim? Claim = null,
    int Announcements = 0,
    DateTimeOffset? LastAnnouncedAt = null,
    bool AlreadyInEffect = false,
    long Version = 0)
{
    /// <summary>Gets the command's id.</summary>
    public CommandId Id => Command.Id;

    /// <summary>Gets when the command was created (UTC).</summary>
    public DateTimeOffset CreatedAt => Command.History[0].At;

    /// <summary>Returns whether a dispatcher holds the command at <paramref name="now"/>.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns><see langword="true"/> while a claim is live.</returns>
    public bool IsClaimedAt(DateTimeOffset now) => Claim?.IsLiveAt(now) == true;

    /// <summary>
    /// Returns whether the command was never handed to the device at <paramref name="now"/>: <c>Pending</c>, or
    /// <c>Published</c> with no live claim. Only such a command may be cancelled.
    /// </summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns><see langword="true"/> when unsent.</returns>
    public bool IsUnsentAt(DateTimeOffset now) =>
        Command.State == CommandState.Pending || (Command.State == CommandState.Published && !IsClaimedAt(now));
}
