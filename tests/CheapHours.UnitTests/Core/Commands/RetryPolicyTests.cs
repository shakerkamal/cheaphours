using CheapHours.Core.Commands;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Time.Testing;
using static CheapHours.UnitTests.Core.Commands.TestCommands;

namespace CheapHours.UnitTests.Core.Commands;

public sealed class RetryPolicyTests
{
    private static readonly DateTimeOffset OneAm = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_match_the_design()
    {
        var options = new RetryPolicyOptions();

        Assert.Equal(6, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), options.BaseDelay);
        Assert.Equal(TimeSpan.FromMinutes(2), options.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ConfirmationTimeout);
    }

    [Fact]
    public void Retry_decisions_are_a_closed_set()
    {
        Assert.Equal(
            ["GiveUp", "RetryAt"],
            typeof(RetryDecision).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(RetryDecision))).Select(t => t.Name).Order());
    }

    // Many attempts allowed so the property exercises the cap without hitting the limit.
    [Property(MaxTest = 500)]
    public void Delay_stays_between_zero_and_the_cap(PositiveInt attempt, int seed)
    {
        var options = new RetryPolicyOptions { MaxAttempts = int.MaxValue };
        var policy = new RetryPolicy(options, new Random(seed));

        var decision = Assert.IsType<RetryDecision.RetryAt>(policy.AfterTransientFailure(attempt.Get, null, OneAm));

        var cap = attempt.Get > 30
            ? options.MaxDelay
            : TimeSpan.FromTicks(Math.Min(options.MaxDelay.Ticks, options.BaseDelay.Ticks << (attempt.Get - 1)));
        var delay = decision.At - OneAm;
        Assert.InRange(delay, TimeSpan.Zero, cap);
    }

    [Fact]
    public void Retry_after_is_a_floor_on_the_jittered_delay()
    {
        // 0.8 × 5 s = 4 s of jitter for the first attempt; the vendor asked for 30 s.
        var policy = new RetryPolicy(new RetryPolicyOptions(), new FixedRandom(0.8));

        var decision = policy.AfterTransientFailure(1, TimeSpan.FromSeconds(30), OneAm);

        Assert.Equal(new RetryDecision.RetryAt(OneAm.AddSeconds(30)), decision);
    }

    [Fact]
    public void Jittered_delay_wins_when_it_is_longer_than_retry_after()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions(), new FixedRandom(0.8));

        var decision = policy.AfterTransientFailure(1, TimeSpan.FromSeconds(1), OneAm);

        Assert.Equal(new RetryDecision.RetryAt(OneAm.AddSeconds(4)), decision);
    }

    [Fact]
    public void Same_seed_and_clock_schedule_the_same_attempts()
    {
        DateTimeOffset[] Schedule()
        {
            var time = new FakeTimeProvider(OneAm);
            var policy = new RetryPolicy(new RetryPolicyOptions(), new Random(42));
            var times = new List<DateTimeOffset>();
            for (var attempt = 1; attempt < 6; attempt++)
            {
                var next = Assert.IsType<RetryDecision.RetryAt>(policy.AfterTransientFailure(attempt, null, time.GetUtcNow()));
                times.Add(next.At);
                time.SetUtcNow(next.At);
            }

            return [.. times];
        }

        Assert.Equal(Schedule(), Schedule());
    }

    [Fact]
    public void Last_allowed_attempt_gives_up_and_the_one_before_retries()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions(), new Random(1));

        Assert.IsType<RetryDecision.RetryAt>(policy.AfterTransientFailure(5, null, OneAm));
        Assert.IsType<RetryDecision.GiveUp>(policy.AfterTransientFailure(6, null, OneAm));
        Assert.IsType<RetryDecision.GiveUp>(policy.AfterTransientFailure(7, TimeSpan.FromSeconds(30), OneAm));
    }

    [Fact]
    public void Attempt_below_one_is_refused()
    {
        var policy = new RetryPolicy(new RetryPolicyOptions(), new Random(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.AfterTransientFailure(0, null, OneAm));
    }

    [Theory]
    [InlineData(0, 5, 120, 60)]
    [InlineData(6, 0, 120, 60)]
    [InlineData(6, 5, 4, 60)]
    [InlineData(6, 5, 120, 0)]
    public void Out_of_range_options_are_refused(int maxAttempts, int baseSeconds, int maxSeconds, int timeoutSeconds)
    {
        var options = new RetryPolicyOptions
        {
            MaxAttempts = maxAttempts,
            BaseDelay = TimeSpan.FromSeconds(baseSeconds),
            MaxDelay = TimeSpan.FromSeconds(maxSeconds),
            ConfirmationTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        Assert.ThrowsAny<ArgumentException>(() => new RetryPolicy(options, new Random(1)));
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(61, true)]
    public void Sent_command_is_overdue_once_the_confirmation_timeout_has_passed(int secondsSinceSend, bool overdue)
    {
        var command = InState(CommandState.Published);
        command.Accept(OneAm);
        var time = new FakeTimeProvider(OneAm);
        time.Advance(TimeSpan.FromSeconds(secondsSinceSend));

        Assert.Equal(overdue, new RetryPolicy(new RetryPolicyOptions(), new Random(1)).IsConfirmationOverdue(command, time.GetUtcNow()));
    }

    [Theory]
    [InlineData(CommandState.Published)]
    [InlineData(CommandState.Reconciling)]
    [InlineData(CommandState.Confirmed)]
    public void Only_sent_commands_can_be_overdue(CommandState state)
    {
        var command = InState(state);

        Assert.False(new RetryPolicy(new RetryPolicyOptions(), new Random(1)).IsConfirmationOverdue(command, OneAm.AddHours(1)));
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
