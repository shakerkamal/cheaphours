namespace CheapHours.Core.Commands;

/// <summary>Queries on <see cref="CommandState"/>.</summary>
public static class CommandStateExtensions
{
    /// <summary>Returns whether no further transition can leave <paramref name="state"/>.</summary>
    /// <param name="state">The state to test.</param>
    /// <returns><see langword="true"/> for <see cref="CommandState.Confirmed"/>, <see cref="CommandState.Rejected"/> and <see cref="CommandState.DeadLettered"/>.</returns>
    public static bool IsTerminal(this CommandState state) =>
        state is CommandState.Confirmed or CommandState.Rejected or CommandState.DeadLettered;
}
