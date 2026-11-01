using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Core.Devices;
using CheapHours.Providers.Kit;
using Microsoft.Extensions.Logging;

namespace CheapHours.Providers.CarCloud;

/// <summary>
/// Electric vehicles reached through the car maker's cloud (id <c>carcloud</c>). Each poll lists the account's
/// vehicles and reads each vehicle's state; start and stop pass the command's idempotency key as the vendor's
/// <c>Idempotency-Key</c>. The kit handles tokens, rate limits, retries and failure classification; this class
/// maps the vendor's JSON to the device model only. See README.md for the vendor API and its quirks.
/// </summary>
public sealed partial class CarCloudProvider : IPollingProvider, ICommandSender
{
    /// <summary>The provider id.</summary>
    public const string Id = "carcloud";

    private readonly ProviderHttpClient<CarCloudProvider> _vendor;
    private readonly TimeProvider _time;
    private readonly ILogger<CarCloudProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="vendor">The kit's client for this provider's vendor.</param>
    /// <param name="time">The clock, for the poll time of removals and malformed states.</param>
    /// <param name="logger">The logger; it never receives tokens or response bodies.</param>
    public CarCloudProvider(ProviderHttpClient<CarCloudProvider> vendor, TimeProvider time, ILogger<CarCloudProvider> logger)
    {
        _vendor = vendor;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string ProviderId => Id;

    /// <inheritdoc/>
    public Capabilities Capabilities => Capabilities.ReadTelemetry | Capabilities.ReadStateOfCharge | Capabilities.StartStop;

    /// <inheritdoc/>
    public async Task<PollResult> PollAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        IReadOnlyList<CarCloudVehicle> vehicles;
        using (var listed = await _vendor.SendAsync(account, () => new HttpRequestMessage(HttpMethod.Get, "api/v1/vehicles"), cancellationToken).ConfigureAwait(false))
        {
            if (listed is VendorResponse.Failed failed)
            {
                return failed.Failure.ToPollFailed();
            }

            var body = await ((VendorResponse.Succeeded)listed).Response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!CarCloudVehicle.TryParseList(body, out vehicles))
            {
                return new PollResult.Failed("vehicle list unreadable");
            }
        }

        var events = new List<DeviceEvent>();
        foreach (var vehicle in vehicles)
        {
            using var state = await _vendor.SendAsync(account, () => new HttpRequestMessage(HttpMethod.Get, $"api/v1/vehicles/{Uri.EscapeDataString(vehicle.Id)}/state"), cancellationToken).ConfigureAwait(false);
            switch (state)
            {
                case VendorResponse.Failed { Failure.Kind: VendorFailureKind.UnknownDevice }:
                    events.Add(new DeviceEvent.DeviceRemoved(vehicle.Id, _time.GetUtcNow()));
                    break;
                case VendorResponse.Failed { Failure.AffectsAccount: true } accountWide:
                    return accountWide.Failure.ToPollFailed();
                case VendorResponse.Failed failed:
                    LogVehicleSkipped(vehicle.Id, failed.Failure.Reason);
                    break;
                case VendorResponse.Succeeded succeeded:
                    var body = await succeeded.Response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    events.AddRange(Map(vehicle, body));
                    break;
            }
        }

        return new PollResult.Completed(events);
    }

    /// <inheritdoc/>
    public async Task<SendResult> SendAsync(DeviceCommand command, DeviceAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(address);
        if (!Capabilities.HasFlag(command.RequiredCapability))
        {
            return new SendResult.Rejected("capability-not-supported");
        }

        var account = address.Account ?? throw new ArgumentException("A car-cloud command needs the vehicle's account.", nameof(address));
        var action = command.Type == CommandType.StartCharging ? "start" : "stop";
        using var response = await _vendor.SendAsync(
            account,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/vehicles/{Uri.EscapeDataString(address.ExternalId)}/charging/{action}");
                request.Headers.Add("Idempotency-Key", command.IdempotencyKey);
                return request;
            },
            cancellationToken).ConfigureAwait(false);

        if (response is VendorResponse.Failed failed)
        {
            return failed.Failure.ToSendResult();
        }

        var body = await ((VendorResponse.Succeeded)response).Response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new SendResult.Accepted(RequestId(body));
    }

    private List<DeviceEvent> Map(CarCloudVehicle vehicle, string body)
    {
        if (!CarCloudVehicleState.TryParse(body, out var state, out var problem))
        {
            LogMalformed(vehicle.Id, problem);
            return [new DeviceEvent.MalformedInput(_time.GetUtcNow(), $"vehicle {vehicle.Id}: {problem}")];
        }

        if (vehicle.BatteryCapacityKwh is not > 0)
        {
            LogMalformed(vehicle.Id, "no battery capacity");
            return [new DeviceEvent.MalformedInput(_time.GetUtcNow(), $"vehicle {vehicle.Id}: no battery capacity")];
        }

        var at = state.Timestamp;
        var events = new List<DeviceEvent>
        {
            new DeviceEvent.Discovered(vehicle.Id, at, DeviceType.Vehicle, new DeviceAttributes(batteryCapacityWh: VendorUnits.KilowattHoursToWattHours(vehicle.BatteryCapacityKwh.Value))),
            new DeviceEvent.Connected(vehicle.Id, at, vehicle.Make is { Length: > 0 } make && vehicle.Model is { Length: > 0 } model ? new DeviceHardware(make, model) : null),
        };

        if (!state.Online)
        {
            events.Add(new DeviceEvent.StatusChanged(vehicle.Id, at, DeviceStatus.Offline));
            return events;
        }

        events.Add(new DeviceEvent.StatusChanged(vehicle.Id, at, state.ChargingState == "Charging" ? DeviceStatus.Charging : DeviceStatus.Available));
        events.Add(new DeviceEvent.ReadingReported(new DeviceReading(
            vehicle.Id,
            at,
            state.ChargePowerKw is { } kw ? VendorUnits.KilowattsToWatts(kw) : null,
            VendorUnits.KilowattHoursToWattHours(state.SessionEnergyKwh),
            state.SocPercent)));
        return events;
    }

    private static string? RequestId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("requestId", out var id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Car cloud vehicle {VehicleId} left out of this poll: {Reason}")]
    private partial void LogVehicleSkipped(string vehicleId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Car cloud vehicle {VehicleId} state unusable: {Problem}")]
    private partial void LogMalformed(string vehicleId, string problem);
}
