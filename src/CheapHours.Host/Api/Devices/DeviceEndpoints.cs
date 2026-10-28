using System.Security.Claims;
using CheapHours.Core;
using CheapHours.Core.Devices;
using CheapHours.Core.Ids;
using CheapHours.Host.Api.Auth;
using CheapHours.Host.Api.Dtos;
using CheapHours.Host.Api.Errors;
using CheapHours.Persistence.Devices;
using CheapHours.Persistence.Telemetry;

namespace CheapHours.Host.Api.Devices;

/// <summary>
/// Handlers for the device endpoints. Each validates its query first, then reads only the key owner's
/// devices: a device of another owner gets the same 404 as one that does not exist.
/// </summary>
internal static class DeviceEndpoints
{
    private static readonly Capabilities[] SingleCapabilities = Enum.GetValues<Capabilities>().Where(c => c != Capabilities.None).ToArray();

    /// <summary><c>GET /v1/devices</c>: the owner's devices, oldest first, with cursor paging.</summary>
    /// <param name="cursor">The cursor from the previous page, if any.</param>
    /// <param name="limit">The page size, if sent.</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="devices">The device reader.</param>
    /// <param name="time">The clock, for presence.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 with a page, or 400.</returns>
    public static async Task<IResult> ListAsync(
        string? cursor, int? limit, ClaimsPrincipal user, IDeviceReader devices, TimeProvider time, CancellationToken cancellationToken)
    {
        var rules = new QueryRules();
        var take = rules.Limit(limit);
        var after = rules.DeviceCursor(cursor);
        if (!rules.IsValid)
        {
            return ErrorCatalogue.ValidationFailed(rules.Errors);
        }

        // One extra row tells whether another page follows.
        var rows = await devices.ListAsync(Owner(user), after, take + 1, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var page = rows.Take(take).Select(d => ToDto(d, now)).ToList();
        var next = rows.Count > take ? PageCursor.ForDevice(rows[take - 1].Id) : null;
        return TypedResults.Ok(new Page<DeviceDto>(page, next));
    }

    /// <summary><c>GET /v1/devices/{id}</c>: one of the owner's devices.</summary>
    /// <param name="id">The device id.</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="devices">The device reader.</param>
    /// <param name="time">The clock, for presence.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 with the device, or 404.</returns>
    public static async Task<IResult> GetAsync(
        DeviceId id, ClaimsPrincipal user, IDeviceReader devices, TimeProvider time, CancellationToken cancellationToken)
    {
        var device = await devices.GetAsync(Owner(user), id, cancellationToken).ConfigureAwait(false);
        return device is null ? DeviceNotFound() : TypedResults.Ok(ToDto(device, time.GetUtcNow()));
    }

    /// <summary>
    /// <c>GET /v1/devices/{id}/telemetry</c>: the device's readings in 15-minute buckets. The range is widened to
    /// whole slots (<paramref name="from"/> down, <paramref name="to"/> up); slots without readings are left out.
    /// </summary>
    /// <param name="id">The device id.</param>
    /// <param name="from">The start of the range (UTC).</param>
    /// <param name="to">The end of the range (UTC).</param>
    /// <param name="resolution">The bucket size; only <c>15m</c>.</param>
    /// <param name="cursor">The cursor from the previous page, if any.</param>
    /// <param name="limit">The page size, if sent.</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="devices">The device reader.</param>
    /// <param name="telemetry">The telemetry reader.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 with a page of buckets, 400 or 404.</returns>
    public static async Task<IResult> TelemetryAsync(
        DeviceId id,
        DateTimeOffset from,
        DateTimeOffset to,
        string? resolution,
        string? cursor,
        int? limit,
        ClaimsPrincipal user,
        IDeviceReader devices,
        ITelemetryReader telemetry,
        CancellationToken cancellationToken)
    {
        var rules = new QueryRules();
        var take = rules.Limit(limit);
        rules.Range(from, to);
        rules.Resolution(resolution);
        if (!rules.IsValid)
        {
            return ErrorCatalogue.ValidationFailed(rules.Errors);
        }

        // The cursor is checked against the widened range, so the range must be valid first.
        var start = SlotGrid.Floor(from);
        var end = SlotGrid.Ceiling(to);
        var pageStart = rules.TelemetryCursor(cursor, start, end);
        if (!rules.IsValid)
        {
            return ErrorCatalogue.ValidationFailed(rules.Errors);
        }

        if (await devices.GetAsync(Owner(user), id, cancellationToken).ConfigureAwait(false) is null)
        {
            return DeviceNotFound();
        }

        if (pageStart >= end)
        {
            return TypedResults.Ok(new Page<TelemetryBucketDto>([], null));
        }

        // One extra bucket tells whether another page follows.
        var rows = await telemetry.GetBucketsAsync(id, pageStart, end, take + 1, cancellationToken).ConfigureAwait(false);
        var page = rows.Take(take).Select(b => new TelemetryBucketDto(b.Start, b.End, b.AvgPowerW, b.EnergyWh, b.SocPercent)).ToList();
        var next = rows.Count > take ? PageCursor.ForBucket(rows[take - 1].Start) : null;
        return TypedResults.Ok(new Page<TelemetryBucketDto>(page, next));
    }

    /// <summary>The one 404 for a device that is missing or belongs to another owner, so the two look the same.</summary>
    /// <returns>404 Problem Details.</returns>
    public static IResult DeviceNotFound() =>
        ErrorCatalogue.NotFound.ToResult("No device with this id exists for this API key.");

    /// <summary>Returns the owner of the authenticated key.</summary>
    /// <param name="user">The authenticated key.</param>
    /// <returns>The owner id from the key's claim.</returns>
    public static OwnerId Owner(ClaimsPrincipal user) =>
        OwnerId.Parse(user.FindFirstValue(ApiScopes.OwnerClaimType) ?? throw new InvalidOperationException("The API key carries no owner."));

    private static DeviceDto ToDto(Device device, DateTimeOffset now)
    {
        var reading = device.State?.LastReading;
        return new DeviceDto(
            device.Id,
            device.ProviderId,
            device.Type,
            [.. SingleCapabilities.Where(c => device.Capabilities.HasFlag(c))],
            device.StatusAsOf(now),
            device.State?.LastSeenAt,
            reading is null ? null : new ReadingDto(reading.MeasurementTime, reading.PowerW, reading.EnergyWh, reading.SocPercent),
            device.Attributes.BatteryCapacityWh,
            device.Attributes.MaxPowerW);
    }
}
