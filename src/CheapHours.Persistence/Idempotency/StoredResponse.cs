namespace CheapHours.Persistence.Idempotency;

/// <summary>A response kept for replay: what the client received the first time.</summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="ContentType">The content type, if any.</param>
/// <param name="Location">The <c>Location</c> header, if any.</param>
/// <param name="Body">The body bytes.</param>
public sealed record StoredResponse(int Status, string? ContentType, string? Location, byte[] Body);
