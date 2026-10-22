using CheapHours.Core.Commands;
using static CheapHours.UnitTests.Core.Commands.TestCommands;

namespace CheapHours.UnitTests.Core.Commands;

public sealed class CommandLifecycleTests
{
    private static readonly DateTimeOffset OneAm = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

    public static TheoryData<CommandState, CommandTrigger> AllPairs()
    {
        var data = new TheoryData<CommandState, CommandTrigger>();
        foreach (var state in Enum.GetValues<CommandState>())
        {
            foreach (var trigger in Enum.GetValues<CommandTrigger>())
            {
                data.Add(state, trigger);
            }
        }

        return data;
    }

    [Fact]
    public void New_command_is_pending_with_one_history_entry()
    {
        var command = Command.Create(Request(), CreatedAt);

        Assert.Equal(CommandState.Pending, command.State);
        Assert.Equal(0, command.Attempts);
        Assert.Null(command.NextAttemptAt);
        var entry = Assert.Single(command.History);
        Assert.Equal(CommandState.Pending, entry.State);
        Assert.Equal(CreatedAt, entry.At);
    }

    [Fact]
    public void New_command_with_a_non_utc_time_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Command.Create(Request(), new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void Published_command_that_is_accepted_is_sent()
    {
        var command = InState(CommandState.Published);

        command.Accept(Later(command));

        Assert.Equal(CommandState.Sent, command.State);
    }

    [Fact]
    public void Pending_command_cannot_be_confirmed()
    {
        var command = New();

        var error = Assert.Throws<InvalidCommandTransitionException>(() => command.Confirm(Later(command)));

        Assert.Equal(CommandState.Pending, error.State);
        Assert.Equal(CommandTrigger.Confirm, error.Trigger);
        Assert.Equal(CommandState.Pending, command.State);
        Assert.Single(command.History);
    }

    [Fact]
    public void Transition_table_is_exactly_the_eleven_allowed_pairs()
    {
        var expected = new Dictionary<(CommandState, CommandTrigger), CommandState>
        {
            [(CommandState.Pending, CommandTrigger.Publish)] = CommandState.Published,
            [(CommandState.Published, CommandTrigger.Accept)] = CommandState.Sent,
            [(CommandState.Published, CommandTrigger.Reject)] = CommandState.Rejected,
            [(CommandState.Published, CommandTrigger.RetryLater)] = CommandState.Published,
            [(CommandState.Published, CommandTrigger.GiveUp)] = CommandState.DeadLettered,
            [(CommandState.Sent, CommandTrigger.Confirm)] = CommandState.Confirmed,
            [(CommandState.Sent, CommandTrigger.Reject)] = CommandState.Rejected,
            [(CommandState.Sent, CommandTrigger.TimeOut)] = CommandState.Reconciling,
            [(CommandState.Reconciling, CommandTrigger.Confirm)] = CommandState.Confirmed,
            [(CommandState.Reconciling, CommandTrigger.Resend)] = CommandState.Published,
            [(CommandState.Reconciling, CommandTrigger.GiveUp)] = CommandState.DeadLettered,
        };

        Assert.Equal(
            expected.OrderBy(p => p.Key),
            CommandTransitions.Table.Select(p => KeyValuePair.Create((p.Key.From, p.Key.Trigger), p.Value)).OrderBy(p => p.Key));
        Assert.Equal(10, CommandTransitions.Table.Count(p => p.Key.From != p.Value));
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Every_pair_is_allowed_exactly_when_the_table_lists_it(CommandState state, CommandTrigger trigger)
    {
        var command = InState(state);
        var historyBefore = command.History.Count;
        var attemptsBefore = command.Attempts;

        if (CommandTransitions.Table.TryGetValue((state, trigger), out var next))
        {
            Assert.True(command.CanFire(trigger));
            Fire(command, trigger, Later(command));

            Assert.Equal(next, command.State);
            Assert.Equal(historyBefore + 1, command.History.Count);
            Assert.Equal(next, command.History[^1].State);
            Assert.Equal(attemptsBefore + (state == CommandState.Published ? 1 : 0), command.Attempts);
        }
        else
        {
            Assert.False(command.CanFire(trigger));
            var error = Assert.Throws<InvalidCommandTransitionException>(() => Fire(command, trigger, Later(command)));

            Assert.Equal(state, error.State);
            Assert.Equal(trigger, error.Trigger);
            Assert.Equal(state, command.State);
            Assert.Equal(historyBefore, command.History.Count);
            Assert.Equal(attemptsBefore, command.Attempts);
        }
    }

    [Fact]
    public void Accept_records_the_vendor_reference()
    {
        var command = InState(CommandState.Published);

        command.Accept(OneAm, "ocpp-msg-4471");

        var entry = command.History[^1];
        Assert.Equal(CommandState.Sent, entry.State);
        Assert.Equal(OneAm, entry.At);
        Assert.Equal("ocpp-msg-4471", entry.VendorReference);
        Assert.Equal(OneAm, command.LastSentAt);
    }

    [Fact]
    public void Reject_records_the_reason()
    {
        var command = InState(CommandState.Sent);

        command.Reject(Later(command), "CallError: NotSupported");

        Assert.Equal("CallError: NotSupported", command.History[^1].Reason);
    }

    [Fact]
    public void Trigger_earlier_than_the_last_entry_is_refused_and_changes_nothing()
    {
        var command = InState(CommandState.Published);
        var last = command.History[^1].At;

        Assert.Throws<ArgumentOutOfRangeException>(() => command.Accept(last.AddTicks(-1)));

        Assert.Equal(CommandState.Published, command.State);
        Assert.Equal(2, command.History.Count);
        Assert.Equal(0, command.Attempts);
    }

    [Fact]
    public void Trigger_at_the_same_time_as_the_last_entry_is_allowed()
    {
        var command = InState(CommandState.Published);

        command.Accept(command.History[^1].At);

        Assert.Equal(CommandState.Sent, command.State);
    }

    [Fact]
    public void Trigger_with_a_non_utc_time_is_refused_and_changes_nothing()
    {
        var command = New();

        Assert.Throws<ArgumentException>(() => command.Publish(new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.FromHours(2))));

        Assert.Equal(CommandState.Pending, command.State);
        Assert.Single(command.History);
    }

    [Fact]
    public void Two_transient_failures_then_success_count_three_attempts()
    {
        var command = InState(CommandState.Published);

        command.RetryLater(OneAm, OneAm.AddSeconds(5), "vendor returned 503");
        command.RetryLater(OneAm.AddSeconds(5), OneAm.AddSeconds(15), "vendor returned 503");
        command.Accept(OneAm.AddSeconds(15));

        Assert.Equal(3, command.Attempts);
        Assert.Equal(CommandState.Sent, command.State);
    }

    [Fact]
    public void Retry_later_schedules_the_next_attempt_and_the_next_transition_clears_it()
    {
        var command = InState(CommandState.Published);

        command.RetryLater(OneAm, OneAm.AddSeconds(5), "vendor returned 503");

        Assert.Equal(OneAm.AddSeconds(5), command.NextAttemptAt);
        Assert.Equal("vendor returned 503", command.History[^1].Reason);

        command.Accept(OneAm.AddSeconds(5));

        Assert.Null(command.NextAttemptAt);
    }

    [Fact]
    public void Retry_later_with_a_next_attempt_before_now_is_refused()
    {
        var command = InState(CommandState.Published);

        Assert.Throws<ArgumentOutOfRangeException>(() => command.RetryLater(OneAm, OneAm.AddSeconds(-1), "vendor returned 503"));

        Assert.Equal(0, command.Attempts);
        Assert.Null(command.NextAttemptAt);
    }

    [Fact]
    public void Reject_and_give_up_need_a_reason()
    {
        var published = InState(CommandState.Published);

        Assert.ThrowsAny<ArgumentException>(() => published.Reject(Later(published), " "));
        Assert.ThrowsAny<ArgumentException>(() => published.GiveUp(Later(published), ""));
        Assert.Equal(CommandState.Published, published.State);
    }

    [Fact]
    public void Resend_goes_back_to_published_with_the_same_id_and_key()
    {
        var command = InState(CommandState.Reconciling);
        var id = command.Id;
        var key = command.IdempotencyKey;

        command.Resend(Later(command));

        Assert.Equal(CommandState.Published, command.State);
        Assert.Equal(id, command.Id);
        Assert.Equal(key, command.IdempotencyKey);
    }

    [Fact]
    public void Six_transient_failures_with_the_default_policy_end_dead_lettered()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions(), new Random(7));
        var command = InState(CommandState.Published);
        var now = OneAm;

        for (var i = 0; i < 6; i++)
        {
            var failure = new SendResult.Transient($"vendor returned 503 (attempt {command.Attempts + 1})");
            switch (policy.AfterTransientFailure(command.Attempts + 1, failure.RetryAfter, now))
            {
                case RetryDecision.RetryAt retry:
                    command.RetryLater(now, retry.At, failure.Reason);
                    now = retry.At;
                    break;
                case RetryDecision.GiveUp:
                    command.GiveUp(now, failure.Reason);
                    break;
            }
        }

        Assert.Equal(CommandState.DeadLettered, command.State);
        Assert.Equal(6, command.Attempts);
        Assert.Null(command.NextAttemptAt);
        Assert.Equal("vendor returned 503 (attempt 6)", command.History[^1].Reason);
        Assert.InRange(now - OneAm, TimeSpan.Zero, TimeSpan.FromSeconds(5 + 10 + 20 + 40 + 80));
    }

    [Fact]
    public void Last_sent_time_is_null_before_the_first_send()
    {
        Assert.Null(InState(CommandState.Published).LastSentAt);
    }
}
