using CheapHours.Core.Devices;

namespace CheapHours.Providers.Kit;

/// <summary>
/// A device provider: one vendor integration, in its own project. It declares who it is and what it can do,
/// then implements only the interaction styles it supports (<see cref="IPollingProvider"/>,
/// <see cref="IInboundTranslator"/>, <see cref="ICommandSender"/>, <see cref="IWebhookVerifier"/>).
/// </summary>
public interface IDeviceProvider
{
    /// <summary>
    /// Gets the stable provider id, unique across registered providers: 1–32 lowercase letters or digits,
    /// for example <c>ocpp</c>, <c>carcloud</c> or <c>heatpumpcloud</c>.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets what this provider can do. Startup fails if the declaration does not match the interfaces
    /// implemented: a sender must declare <see cref="Capabilities.StartStop"/> or
    /// <see cref="Capabilities.SetPowerLimit"/>, and a translator <see cref="Capabilities.PushesEvents"/>.
    /// </summary>
    Capabilities Capabilities { get; }
}
