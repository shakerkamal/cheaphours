namespace CheapHours.Host.Worker;

/// <summary>Whether the worker's startup (indexes, broker connection, topology) has finished.</summary>
internal sealed class WorkerState
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets a task that completes once startup has finished.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Marks startup as finished.</summary>
    public void MarkReady() => _ready.TrySetResult();
}
