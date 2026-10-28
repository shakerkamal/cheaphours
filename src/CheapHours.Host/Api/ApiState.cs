namespace CheapHours.Host.Api;

/// <summary>Whether the API role has finished its startup work (indexes created, configured keys seeded).</summary>
internal sealed class ApiState
{
    private volatile bool _ready;

    /// <summary>Gets a value indicating whether startup has finished.</summary>
    public bool IsReady => _ready;

    /// <summary>Marks startup as finished.</summary>
    public void MarkReady() => _ready = true;
}
