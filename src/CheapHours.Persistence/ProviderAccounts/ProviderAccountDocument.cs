using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.ProviderAccounts;

/// <summary>
/// A <c>providerAccounts</c> document: an owner's link to a vendor cloud. The tokens are one opaque blob
/// protected by the kit with the data protection key ring; no token is ever stored in clear.
/// </summary>
internal sealed class ProviderAccountDocument
{
    /// <summary>Gets or sets the id: <c>{providerId}/{externalAccountId}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    public string ExternalAccountId { get; set; } = string.Empty;

    /// <summary>Gets or sets the protected token blob.</summary>
    public string Tokens { get; set; } = string.Empty;

    /// <summary>Gets or sets the tokens' version, incremented on every write (compare-and-set).</summary>
    public long TokenVersion { get; set; }

    public bool ReconnectNeeded { get; set; }

    /// <summary>Gets or sets when the account is next due; absent means at once.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? NextPollAt { get; set; }

    /// <summary>Gets or sets until when the vendor asked us to pause (its <c>Retry-After</c>); absent when not paused.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? PausedUntil { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }

    public int SchemaVersion { get; set; } = 1;
}
