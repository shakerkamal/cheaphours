namespace CheapHours.Providers.Kit;

/// <summary>
/// Checks that a webhook really came from the provider's vendor (D6). Signing schemes differ per vendor, so
/// each push provider that receives webhooks supplies its own.
/// </summary>
public interface IWebhookVerifier : IDeviceProvider
{
    /// <summary>Verifies the message's signature over its raw body.</summary>
    /// <param name="message">The webhook exactly as received.</param>
    /// <returns><see langword="true"/> only when the signature is present and valid.</returns>
    /// <remarks>
    /// Contract:
    /// <list type="bullet">
    /// <item>The gateway calls this before <see cref="IInboundTranslator.Translate"/>. On <see langword="false"/> it answers <c>401</c> and the translator never sees the message.</item>
    /// <item>Signatures are compared in constant time (<c>CryptographicOperations.FixedTimeEquals</c>).</item>
    /// <item>A missing or malformed signature header returns <see langword="false"/>; it never throws.</item>
    /// <item>Performs no I/O; secrets come from configuration at construction.</item>
    /// </list>
    /// </remarks>
    bool Verify(InboundMessage message);
}
