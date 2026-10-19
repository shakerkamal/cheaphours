using CheapHours.Core.Commands;
using CheapHours.Core.Devices;

namespace CheapHours.Providers.Kit;

/// <summary>
/// A provider that can act on devices: start, stop or limit power. Providers implementing this must declare
/// <see cref="Capabilities.StartStop"/> or <see cref="Capabilities.SetPowerLimit"/>.
/// </summary>
public interface ICommandSender : IDeviceProvider
{
    /// <summary>Hands a command to the vendor or device.</summary>
    /// <param name="command">The command; its idempotency key makes repeated sends safe.</param>
    /// <param name="address">Where to send it, in the vendor's terms.</param>
    /// <param name="cancellationToken">Cancels the send, for example on shutdown.</param>
    /// <returns>
    /// <see cref="SendResult.Accepted"/> when the vendor took it (the device has not confirmed yet),
    /// <see cref="SendResult.Rejected"/> for a permanent refusal, or <see cref="SendResult.Transient"/> for one
    /// worth retrying.
    /// </returns>
    /// <remarks>
    /// Failure contract:
    /// <list type="bullet">
    /// <item>Idempotent: sending the same command twice with the same <see cref="DeviceCommand.IdempotencyKey"/> must not make the device act twice.</item>
    /// <item>A command needing a capability the provider does not declare (see <see cref="DeviceCommand.RequiredCapability"/>) returns <see cref="SendResult.Rejected"/> with reason <c>capability-not-supported</c> before any external call.</item>
    /// <item>Permanent vendor refusals (4xx validation errors) return <see cref="SendResult.Rejected"/> with the vendor's reason.</item>
    /// <item>429, 5xx, timeouts and connection failures return <see cref="SendResult.Transient"/>, carrying the vendor's <c>Retry-After</c> when given.</item>
    /// <item>A <c>401</c> leads the kit to refresh the token once and retry once; if that fails the result is <see cref="SendResult.Rejected"/> and the account is marked reconnect-needed.</item>
    /// <item>Throws only <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires, or on a programming error. Vendor failures never throw.</item>
    /// </list>
    /// </remarks>
    Task<SendResult> SendAsync(DeviceCommand command, DeviceAddress address, CancellationToken cancellationToken);
}
