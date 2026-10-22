using CheapHours.Core.Commands;
using FsCheck.Xunit;
using static CheapHours.UnitTests.Core.Commands.TestCommands;

namespace CheapHours.UnitTests.Core.Commands;

public sealed class CommandPropertyTests
{
    // Applies a random trigger sequence one second apart. Triggers the table refuses before a terminal state
    // are skipped; once terminal, every trigger must throw and leave state and history untouched.
    [Property(MaxTest = 500)]
    public void Terminal_states_are_final(CommandTrigger[] triggers)
    {
        var command = New();

        foreach (var trigger in triggers)
        {
            var at = Later(command);
            if (command.State.IsTerminal())
            {
                var state = command.State;
                var history = command.History.ToArray();

                Assert.False(command.CanFire(trigger));
                Assert.Throws<InvalidCommandTransitionException>(() => Fire(command, trigger, at));
                Assert.Equal(state, command.State);
                Assert.Equal(history, command.History);
            }
            else if (command.CanFire(trigger))
            {
                Fire(command, trigger, at);
            }
        }
    }
}
