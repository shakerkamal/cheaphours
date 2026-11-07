using CheapHours.Core.Commands;
using FsCheck.Xunit;
using static CheapHours.UnitTests.Core.Commands.TestCommands;

namespace CheapHours.UnitTests.Core.Commands;

public sealed class CommandRestoreTests
{
    private static readonly DateTimeOffset OneAm = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_command_is_due_at_its_creation_time()
    {
        var command = Command.Create(Request(), CreatedAt);

        Assert.Equal(CreatedAt, command.DueAt);
    }

    [Fact]
    public void Planned_start_carries_its_due_time()
    {
        var createdAt = new DateTimeOffset(2026, 11, 7, 22, 0, 0, TimeSpan.Zero);
        var dueAt = new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);

        var command = Command.Create(Request(), createdAt, dueAt);

        Assert.Equal(dueAt, command.DueAt);
    }

    [Fact]
    public void Due_time_that_is_not_utc_fails_creation()
    {
        var dueAt = new DateTimeOffset(2026, 11, 8, 2, 0, 0, TimeSpan.FromHours(1));

        Assert.Throws<ArgumentException>(() => Command.Create(Request(), CreatedAt, dueAt));
    }

    [Theory]
    [InlineData(CommandState.Pending)]
    [InlineData(CommandState.Published)]
    [InlineData(CommandState.Sent)]
    [InlineData(CommandState.Confirmed)]
    [InlineData(CommandState.Reconciling)]
    [InlineData(CommandState.Rejected)]
    [InlineData(CommandState.DeadLettered)]
    [InlineData(CommandState.Cancelled)]
    public void Every_state_restores_to_an_equal_command(CommandState state)
    {
        var original = InState(state);

        AssertEquivalent(original, RestoreFrom(original));
    }

    [Fact]
    public void Retry_schedule_survives_a_restore()
    {
        var original = InState(CommandState.Published);
        original.RetryLater(OneAm, OneAm.AddSeconds(30), "vendor returned 429");

        var restored = RestoreFrom(original);

        Assert.Equal(OneAm.AddSeconds(30), restored.NextAttemptAt);
        Assert.Equal(1, restored.Attempts);
    }

    [Fact]
    public void Restored_command_keeps_following_the_table()
    {
        var restored = RestoreFrom(InState(CommandState.Sent));

        Assert.True(restored.CanFire(CommandTrigger.Confirm));
        Assert.False(restored.CanFire(CommandTrigger.Cancel));
        restored.Confirm(Later(restored));
        Assert.Equal(CommandState.Confirmed, restored.State);
    }

    [Fact]
    public void History_that_skips_a_state_is_refused()
    {
        CommandHistoryEntry[] history =
        [
            new(CommandState.Pending, CreatedAt),
            new(CommandState.Sent, CreatedAt.AddSeconds(1), "vendor-ref"),
        ];

        Assert.Throws<InvalidCommandTransitionException>(() => Command.Restore(Request(), CreatedAt, history, nextAttemptAt: null));
    }

    [Fact]
    public void History_that_leaves_a_terminal_state_is_refused()
    {
        CommandHistoryEntry[] history =
        [
            new(CommandState.Pending, CreatedAt),
            new(CommandState.Cancelled, CreatedAt.AddSeconds(1), reason: "superseded"),
            new(CommandState.Published, CreatedAt.AddSeconds(2)),
        ];

        Assert.Throws<InvalidCommandTransitionException>(() => Command.Restore(Request(), CreatedAt, history, nextAttemptAt: null));
    }

    [Fact]
    public void History_that_does_not_start_pending_is_refused()
    {
        CommandHistoryEntry[] history = [new(CommandState.Published, CreatedAt)];

        Assert.Throws<ArgumentException>(() => Command.Restore(Request(), CreatedAt, history, nextAttemptAt: null));
    }

    [Fact]
    public void Empty_history_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Command.Restore(Request(), CreatedAt, [], nextAttemptAt: null));
    }

    [Fact]
    public void History_that_goes_back_in_time_is_refused()
    {
        CommandHistoryEntry[] history =
        [
            new(CommandState.Pending, CreatedAt),
            new(CommandState.Published, CreatedAt.AddSeconds(-1)),
        ];

        Assert.Throws<ArgumentOutOfRangeException>(() => Command.Restore(Request(), CreatedAt, history, nextAttemptAt: null));
    }

    [Fact]
    public void Cancelled_entry_without_a_reason_is_refused()
    {
        CommandHistoryEntry[] history =
        [
            new(CommandState.Pending, CreatedAt),
            new(CommandState.Cancelled, CreatedAt.AddSeconds(1)),
        ];

        Assert.ThrowsAny<ArgumentException>(() => Command.Restore(Request(), CreatedAt, history, nextAttemptAt: null));
    }

    [Fact]
    public void Next_attempt_without_a_retry_is_refused()
    {
        var original = InState(CommandState.Published);

        Assert.Throws<ArgumentException>(() => Command.Restore(original.Request, original.DueAt, original.History, OneAm));
    }

    [Fact]
    public void Retry_without_a_next_attempt_is_refused()
    {
        var original = InState(CommandState.Published);
        original.RetryLater(OneAm, OneAm.AddSeconds(30), "vendor returned 429");

        Assert.Throws<ArgumentException>(() => Command.Restore(original.Request, original.DueAt, original.History, nextAttemptAt: null));
    }

    // Any command reachable through the public triggers restores to one with the same fields.
    [Property(MaxTest = 500)]
    public void Any_reachable_command_restores_to_an_equal_one(CommandTrigger[] triggers)
    {
        var original = Command.Create(Request(), CreatedAt, CreatedAt.AddMinutes(90));
        foreach (var trigger in triggers)
        {
            if (original.CanFire(trigger))
            {
                Fire(original, trigger, Later(original));
            }
        }

        AssertEquivalent(original, RestoreFrom(original));
    }

    private static Command RestoreFrom(Command command) =>
        Command.Restore(command.Request, command.DueAt, command.History.ToArray(), command.NextAttemptAt);

    private static void AssertEquivalent(Command expected, Command actual)
    {
        Assert.Equal(expected.Request, actual.Request);
        Assert.Equal(expected.DueAt, actual.DueAt);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Attempts, actual.Attempts);
        Assert.Equal(expected.NextAttemptAt, actual.NextAttemptAt);
        Assert.Equal(expected.History, actual.History);
    }
}
