namespace CheapHours.Simulators.CarCloud;

/// <summary>
/// The simulator's configuration, section <c>Simulator</c>. The client secret and the accounts' seed refresh
/// tokens come from the environment (<c>.env</c> in compose), never from a committed file.
/// </summary>
public sealed class SimulatorOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Simulator";

    /// <summary>Gets or sets the client id the token endpoint accepts.</summary>
    public string ClientId { get; set; } = "cheaphours";

    /// <summary>Gets or sets the client secret the token endpoint accepts; required.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Gets or sets how long an access token works.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets or sets how many vehicle and command calls one account may make per minute.</summary>
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Gets the linked accounts.</summary>
    public IList<AccountOptions> Accounts { get; } = [];

    /// <summary>One linked account.</summary>
    public sealed class AccountOptions
    {
        /// <summary>Gets or sets the account id.</summary>
        public string Id { get; set; } = "acct_demo";

        /// <summary>Gets or sets the seed refresh token; required. It works once, then rotates.</summary>
        public string? RefreshToken { get; set; }

        /// <summary>Gets the account's vehicles; an account without any gets the demo vehicle <c>veh_1</c>.</summary>
        public IList<VehicleOptions> Vehicles { get; } = [];
    }

    /// <summary>One vehicle.</summary>
    public sealed class VehicleOptions
    {
        /// <summary>Gets or sets the vehicle id.</summary>
        public string Id { get; set; } = "veh_1";

        /// <summary>Gets or sets the maker.</summary>
        public string Make { get; set; } = "Voltwerk";

        /// <summary>Gets or sets the model.</summary>
        public string Model { get; set; } = "Model E";

        /// <summary>Gets or sets the usable battery capacity in kWh.</summary>
        public decimal BatteryCapacityKwh { get; set; } = 77m;

        /// <summary>Gets or sets the charging power in kW.</summary>
        public decimal MaxChargePowerKw { get; set; } = 11m;

        /// <summary>Gets or sets the state of charge at start-up, in percent.</summary>
        public decimal SocPercent { get; set; } = 40m;
    }
}
