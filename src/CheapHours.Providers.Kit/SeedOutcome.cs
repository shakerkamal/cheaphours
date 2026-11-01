namespace CheapHours.Providers.Kit;

/// <summary>What seeding an account from configuration did.</summary>
public enum SeedOutcome
{
    /// <summary>The account was missing and was inserted with the seed token.</summary>
    Inserted,

    /// <summary>The account was marked reconnect-needed; it got the seed token again and the mark was cleared.</summary>
    Relinked,

    /// <summary>The account exists and works; its stored (possibly rotated) tokens were kept.</summary>
    Unchanged,
}
