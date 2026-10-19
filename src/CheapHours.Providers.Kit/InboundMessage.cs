namespace CheapHours.Providers.Kit;

/// <summary>Raw input the gateway received for a provider: an OCPP frame or a webhook request.</summary>
public sealed record InboundMessage
{
    /// <summary>Creates an inbound message.</summary>
    /// <param name="externalId">The device's vendor id when the transport tells us (the OCPP charge point id in the URL); otherwise <see langword="null"/>.</param>
    /// <param name="body">The raw body, exactly as received (signatures are computed over these bytes).</param>
    /// <param name="headers">The request headers; looked up case-insensitively.</param>
    /// <param name="receivedAt">When the gateway received it, in UTC.</param>
    /// <exception cref="ArgumentException"><paramref name="receivedAt"/> is not UTC.</exception>
    public InboundMessage(string? externalId, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (receivedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Must be UTC.", nameof(receivedAt));
        }

        ExternalId = externalId;
        Body = body;
        Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        ReceivedAt = receivedAt;
    }

    /// <summary>Gets the device's vendor id, if the transport supplied it.</summary>
    public string? ExternalId { get; }

    /// <summary>Gets the raw body.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Gets the headers (case-insensitive keys).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Gets when the gateway received the message (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; }
}
