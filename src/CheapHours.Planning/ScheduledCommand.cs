using CheapHours.Core.Commands;
using CheapHours.Core.Goals;

namespace CheapHours.Planning;

/// <summary>One command a confirmed plan version asks for: what, when, and the key that makes it unique.</summary>
/// <param name="Type">Start or stop.</param>
/// <param name="DueAt">When it may be sent (UTC).</param>
/// <param name="KeySlot">The slot its key names: a run's first slot for a start, its last slot for a stop.</param>
/// <param name="IdempotencyKey">The key from <see cref="PlannedCommandKey.For"/>.</param>
public sealed record ScheduledCommand(CommandType Type, DateTimeOffset DueAt, PlanSlot KeySlot, string IdempotencyKey);
