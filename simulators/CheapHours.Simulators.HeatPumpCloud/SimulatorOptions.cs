namespace CheapHours.Simulators.HeatPumpCloud;

/// <summary>The simulator's configuration, section <c>Simulator</c>. The signing secret comes from <c>.env</c>, never a committed file.</summary>
public sealed class SimulatorOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Simulator";

    /// <summary>Gets or sets where every webhook is posted; required and absolute.</summary>
    public Uri? CallbackUrl { get; set; }

    /// <summary>Gets or sets the secret webhooks are signed with; required, never logged.</summary>
    public string? SigningSecret { get; set; }

    /// <summary>Gets or sets the vendor account the heat pumps belong to.</summary>
    public string AccountId { get; set; } = "acct_demo";

    /// <summary>Gets or sets how often every heat pump's state is delivered.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets or sets how long one delivery attempt may take before it counts as timed out.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the simulated heat pumps; one default heat pump when none is configured.</summary>
    public IList<HeatPumpOptions> HeatPumps { get; } = [];

    /// <summary>One simulated heat pump.</summary>
    public sealed class HeatPumpOptions
    {
        /// <summary>Gets or sets the vendor's device id.</summary>
        public string Id { get; set; } = "hp_1";

        /// <summary>Gets or sets the maker.</summary>
        public string Maker { get; set; } = "Kaltwerk";

        /// <summary>Gets or sets the model.</summary>
        public string Model { get; set; } = "AeroHeat 9";

        /// <summary>Gets or sets the electrical power while heating, in kW.</summary>
        public decimal RatedKw { get; set; } = 3m;

        /// <summary>Gets or sets the electrical power in standby, in kW.</summary>
        public decimal StandbyKw { get; set; } = 0.05m;

        /// <summary>Gets or sets the lifetime meter at start, in kWh.</summary>
        public decimal EnergyKwh { get; set; } = 1000m;

        /// <summary>Gets or sets the operating mode at start: <c>Heating</c>, <c>HotWater</c>, <c>Defrost</c>, <c>Standby</c> or <c>Fault</c>.</summary>
        public string Mode { get; set; } = "Heating";
    }
}
