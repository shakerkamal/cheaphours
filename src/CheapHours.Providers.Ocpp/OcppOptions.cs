namespace CheapHours.Providers.Ocpp;

/// <summary>The OCPP provider's configuration, section <c>Providers:Ocpp</c>.</summary>
public sealed class OcppOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Providers:Ocpp";

    /// <summary>Gets or sets the heartbeat interval sent to chargers, in seconds (default 60).</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>Gets the charger registrations seeded at startup.</summary>
    public IList<ChargePointOptions> ChargePoints { get; } = [];

    /// <summary>Gets the heartbeat interval.</summary>
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);
}
