using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using CheapHours.Core.Commands;
using CheapHours.Core.Ids;
using CheapHours.Host.Api.Devices;
using CheapHours.Host.Api.Dtos;
using CheapHours.Host.Api.Errors;
using CheapHours.Host.Commands;
using CheapHours.Persistence.Commands;
using Microsoft.AspNetCore.Mvc;

namespace CheapHours.Host.Api.Commands;

/// <summary>
/// Handlers for sending a command directly and reading a command (command-api spec). The body is read and checked
/// here, field by field, so every problem is keyed by its field; the device checks belong to
/// <see cref="CommandService"/>.
/// </summary>
internal static class CommandEndpoints
{
    private const string MaxPowerField = "parameters.maxPowerW";

    /// <summary><c>POST /v1/devices/{id}/commands</c>: accepts one command for asynchronous delivery.</summary>
    /// <param name="id">The device.</param>
    /// <param name="idempotencyKey">The <c>Idempotency-Key</c> header; the idempotency middleware handles it.</param>
    /// <param name="request">The HTTP request; its body is a <see cref="CreateCommandRequest"/>.</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="service">The command service.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>202 with <c>Location</c>, or 400, 404, 422.</returns>
    public static async Task<IResult> SendAsync(
        DeviceId id,
        [FromHeader(Name = V1Endpoints.IdempotencyKeyHeader)] string? idempotencyKey,
        HttpRequest request,
        ClaimsPrincipal user,
        CommandService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (body, errors) = await ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return ErrorCatalogue.ValidationFailed(errors);
        }

        var outcome = await service.CreateAsync(DeviceEndpoints.Owner(user), id, body!.Value.Type, body.Value.MaxPowerW, Traceparent(request), cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            CommandRequestOutcome.Accepted accepted => TypedResults.Accepted(
                $"/v1/commands/{accepted.Command.Id}",
                new CommandAcceptedResponse(accepted.Command.Id, accepted.Command.Command.State)),
            CommandRequestOutcome.NotFound => ErrorCatalogue.NotFound.ToResult("No device with this id exists for this API key."),
            CommandRequestOutcome.CapabilityNotSupported unsupported => ErrorCatalogue.CapabilityNotSupported.ToResult(unsupported.Detail),
            CommandRequestOutcome.Unprocessable unprocessable => ErrorCatalogue.Unprocessable.ToResult(unprocessable.Detail),
            _ => throw new InvalidOperationException("Unknown command outcome."),
        };
    }

    /// <summary><c>GET /v1/commands/{id}</c>: a command, its state and history, within its owner.</summary>
    /// <param name="id">The command.</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="commands">The command store.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 with the command, or 404.</returns>
    public static async Task<IResult> GetAsync(CommandId id, ClaimsPrincipal user, ICommandStore commands, CancellationToken cancellationToken)
    {
        if (await commands.GetAsync(DeviceEndpoints.Owner(user), id, cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            return ErrorCatalogue.NotFound.ToResult("No command with this id exists for this API key.");
        }

        var command = stored.Command;
        var request = command.Request;
        return TypedResults.Ok(new CommandDto(
            request.Id,
            request.DeviceId,
            request.Type,
            request.MaxPowerW is { } power ? new CommandParametersDto(power) : null,
            command.State,
            command.Attempts,
            [.. command.History.Select(h => new CommandHistoryEntryDto(h.State, h.At, h.VendorReference, h.Reason))]));
    }

    /// <summary>Returns the request's trace context: the current activity's, or the caller's header when none runs.</summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>A W3C <c>traceparent</c>, or <see langword="null"/>.</returns>
    public static string? Traceparent(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity)
        {
            return activity.Id;
        }

        var header = request.Headers["traceparent"].ToString();
        return header.Length is > 0 and <= 128 ? header : null;
    }

    // Reads the body as JSON and checks it field by field: an unknown type, a limit without a power, a stop with a
    // power, or a power of zero or less are refused, each keyed by its field.
    private static async Task<(ValidBody? Body, Dictionary<string, string[]> Errors)> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        JsonElement json;
        try
        {
            json = await request.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            errors["body"] = ["Send a JSON object with a type and, for SetPowerLimit, parameters.maxPowerW."];
            return (null, errors);
        }

        if (json.ValueKind != JsonValueKind.Object)
        {
            errors["body"] = ["Send a JSON object with a type and, for SetPowerLimit, parameters.maxPowerW."];
            return (null, errors);
        }

        CommandType? type = json.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            && Enum.TryParse<CommandType>(t.GetString(), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;
        if (type is null)
        {
            errors["type"] = ["Must be StartCharging, StopCharging or SetPowerLimit."];
        }

        int? maxPowerW = null;
        var hasPower = json.TryGetProperty("parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("maxPowerW", out var power)
            && power.ValueKind != JsonValueKind.Null;
        if (hasPower)
        {
            if (parameters.GetProperty("maxPowerW") is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var watts) && watts > 0)
            {
                maxPowerW = watts;
            }
            else
            {
                errors[MaxPowerField] = ["Must be a whole number of watts greater than zero."];
            }
        }

        if (type == CommandType.SetPowerLimit && !hasPower)
        {
            errors[MaxPowerField] = ["Required for SetPowerLimit."];
        }
        else if (type == CommandType.StopCharging && hasPower)
        {
            errors[MaxPowerField] = ["Not allowed for StopCharging."];
        }

        return errors.Count > 0 ? (null, errors) : (new ValidBody(type!.Value, maxPowerW), errors);
    }

    private readonly record struct ValidBody(CommandType Type, int? MaxPowerW);
}
