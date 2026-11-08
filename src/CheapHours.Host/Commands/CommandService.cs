using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Persistence.Commands;
using CheapHours.Persistence.Devices;

namespace CheapHours.Host.Commands;

/// <summary>
/// Accepts a direct command for one of an owner's devices (command-api spec, design §11): the device must exist for
/// the owner, not be removed, declare the capability, and not be asked for more power than its maximum. The command
/// is due at once and stored with its outbox entry in one transaction; its id is also its idempotency key, because
/// the HTTP <c>Idempotency-Key</c> already makes one request one command.
/// </summary>
internal sealed class CommandService(IDeviceCommandStateStore devices, ICommandStore commands, TimeProvider time)
{
    /// <summary>Checks and stores a direct command.</summary>
    /// <param name="ownerId">The owner of the calling key (D3).</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="type">What the device must do.</param>
    /// <param name="maxPowerW">The power in W, for a power limit (required) or a start (optional).</param>
    /// <param name="traceparent">The trace context of the request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The stored command, or why not.</returns>
    public async Task<CommandRequestOutcome> CreateAsync(OwnerId ownerId, DeviceId deviceId, CommandType type, int? maxPowerW, string? traceparent, CancellationToken cancellationToken)
    {
        if (await devices.GetAsync(ownerId, deviceId, cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            return new CommandRequestOutcome.NotFound();
        }

        var device = stored.Device;
        if (stored.RemovedAt is not null)
        {
            return new CommandRequestOutcome.Unprocessable("The device was removed by its vendor and cannot take commands.");
        }

        var now = time.GetUtcNow();
        var id = CommandId.New(time);
        var request = new DeviceCommand(id, deviceId, type, id.Value, maxPowerW);
        if (!device.Capabilities.HasFlag(request.RequiredCapability))
        {
            return new CommandRequestOutcome.CapabilityNotSupported($"The device does not support {type}.");
        }

        if (maxPowerW is { } power && device.Attributes.MaxPowerW is { } maximum && power > maximum)
        {
            return new CommandRequestOutcome.Unprocessable($"maxPowerW {power} W is above the charger's maximum of {maximum} W.");
        }

        var command = new StoredCommand(Command.Create(request, now), ownerId, traceparent);
        await commands.CreateWithOutboxAsync(command, CommandOutbox.Announce(command, now), cancellationToken).ConfigureAwait(false);
        return new CommandRequestOutcome.Accepted(command);
    }
}
