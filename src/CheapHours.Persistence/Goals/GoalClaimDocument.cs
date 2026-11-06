using MongoDB.Bson.Serialization.Attributes;

namespace CheapHours.Persistence.Goals;

/// <summary>
/// A <c>goalClaims</c> document: a device belongs to one open goal until that goal's departure. A claim is taken
/// by an upsert that matches only a past claim or one of the same goal, so a live claim of another goal makes
/// the upsert hit the existing id.
/// </summary>
internal sealed class GoalClaimDocument
{
    /// <summary>Gets or sets the device id.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string GoalId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the claim lapses: the goal's departure.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime Until { get; set; }
}
