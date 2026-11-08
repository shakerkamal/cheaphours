using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;

namespace CheapHours.IntegrationTests.Commands;

/// <summary>
/// A deliberately broken provider, as in the contract suite: it breaks the "vendor failures never throw" contract by
/// throwing on every send. It proves that a message whose handling keeps failing ends in the dead-letter queue and
/// that its command is dead-lettered.
/// </summary>
internal sealed class ThrowingCommandSender : ICommandSender
{
    public const string Id = "broken";

    private int _sends;

    public string ProviderId => Id;

    public Capabilities Capabilities => Capabilities.StartStop;

    public int Sends => Volatile.Read(ref _sends);

    public Task<SendResult> SendAsync(DeviceCommand command, DeviceAddress address, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sends);
        throw new InvalidOperationException("This provider is broken on purpose.");
    }
}
