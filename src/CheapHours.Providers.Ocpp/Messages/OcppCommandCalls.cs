using System.Text.Json;
using System.Text.Json.Nodes;
using CheapHours.Core.Commands;
using CheapHours.Providers.Ocpp.Framing;

namespace CheapHours.Providers.Ocpp.Messages;

/// <summary>
/// Turns a device command into the OCPP 1.6J call that carries it out, and the charger's reply into a
/// <see cref="SendResult"/> (ADR-006, ocpp-gateway spec). Pure: the gateway owns the socket and the waiting.
/// </summary>
public static class OcppCommandCalls
{
    /// <summary>The action that starts charging.</summary>
    public const string RemoteStartTransaction = nameof(RemoteStartTransaction);

    /// <summary>The action that stops charging.</summary>
    public const string RemoteStopTransaction = nameof(RemoteStopTransaction);

    /// <summary>The action that limits power.</summary>
    public const string SetChargingProfile = nameof(SetChargingProfile);

    /// <summary>The profile id for a limit on the running transaction; a new limit replaces the old one.</summary>
    public const int TxProfileId = 101;

    /// <summary>The profile id for the limit the next transactions start with; a new limit replaces the old one.</summary>
    public const int TxDefaultProfileId = 102;

    private static readonly HashSet<string> PermanentErrors = new(StringComparer.Ordinal)
    {
        nameof(OcppErrorCode.NotImplemented),
        nameof(OcppErrorCode.NotSupported),
        nameof(OcppErrorCode.FormationViolation),
        nameof(OcppErrorCode.PropertyConstraintViolation),
        nameof(OcppErrorCode.TypeConstraintViolation),
    };

    /// <summary>Builds the call for <paramref name="command"/>.</summary>
    /// <param name="command">The command.</param>
    /// <param name="connectorId">The connector to act on.</param>
    /// <param name="idTag">The id tag a remote start authorises.</param>
    /// <param name="activeTransactionId">The charger's running transaction, if any.</param>
    /// <returns>
    /// <c>RemoteStartTransaction</c> (with a <c>TxProfile</c> when the start carries a power),
    /// <c>RemoteStopTransaction</c> for the running transaction, or <c>SetChargingProfile</c> in W for the running
    /// transaction (<c>TxProfile</c>) or the next ones (<c>TxDefaultProfile</c>).
    /// </returns>
    /// <exception cref="ArgumentException">A stop has no running transaction to stop, or a value is out of range.</exception>
    public static OcppCommandCall For(DeviceCommand command, int connectorId, string idTag, int? activeTransactionId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idTag);

        switch (command.Type)
        {
            case CommandType.StartCharging:
                var start = new JsonObject { ["connectorId"] = connectorId, ["idTag"] = idTag };
                if (command.MaxPowerW is { } startLimit)
                {
                    start["chargingProfile"] = Profile(TxProfileId, "TxProfile", transactionId: null, startLimit);
                }

                return new OcppCommandCall(RemoteStartTransaction, start);

            case CommandType.StopCharging:
                var transactionId = activeTransactionId
                    ?? throw new ArgumentException("A stop needs the charger's running transaction.", nameof(activeTransactionId));
                return new OcppCommandCall(RemoteStopTransaction, new JsonObject { ["transactionId"] = transactionId });

            case CommandType.SetPowerLimit:
                var limit = command.MaxPowerW!.Value;
                var profile = activeTransactionId is { } running
                    ? Profile(TxProfileId, "TxProfile", running, limit)
                    : Profile(TxDefaultProfileId, "TxDefaultProfile", transactionId: null, limit);
                return new OcppCommandCall(SetChargingProfile, new JsonObject { ["connectorId"] = connectorId, ["csChargingProfiles"] = profile });

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Type, "Unknown command type.");
        }
    }

    /// <summary>Reads the charger's reply to a call.</summary>
    /// <param name="uniqueId">The call's unique id, which becomes the vendor reference of an accepted send.</param>
    /// <param name="reply">The charger's <see cref="OcppCallResult"/> or <see cref="OcppCallError"/>.</param>
    /// <returns>
    /// <c>Accepted</c> → accepted; <c>Rejected</c> or <c>NotSupported</c> → rejected with that status; a <c>CallError</c>
    /// → rejected for <c>NotImplemented</c>, <c>NotSupported</c>, <c>FormationViolation</c>,
    /// <c>PropertyConstraintViolation</c> and <c>TypeConstraintViolation</c>, transient otherwise.
    /// </returns>
    public static SendResult Interpret(string uniqueId, OcppFrame reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueId);
        ArgumentNullException.ThrowIfNull(reply);
        return reply switch
        {
            OcppCallResult result => Status(result.Payload) switch
            {
                "Accepted" => new SendResult.Accepted(uniqueId),
                "Rejected" => new SendResult.Rejected("Rejected"),
                "NotSupported" => new SendResult.Rejected("NotSupported"),
                _ => new SendResult.Rejected("the charger's reply has no known status"),
            },
            OcppCallError error => PermanentErrors.Contains(error.ErrorCode)
                ? new SendResult.Rejected($"CallError: {error.ErrorCode}")
                : new SendResult.Transient($"CallError: {error.ErrorCode}"),
            _ => throw new ArgumentException("Only a CallResult or a CallError answers a call.", nameof(reply)),
        };
    }

    // One period from the start, in W, so the profile holds until replaced.
    private static JsonObject Profile(int id, string purpose, int? transactionId, int limitW)
    {
        var profile = new JsonObject
        {
            ["chargingProfileId"] = id,
            ["stackLevel"] = 0,
            ["chargingProfilePurpose"] = purpose,
            ["chargingProfileKind"] = "Relative",
            ["chargingSchedule"] = new JsonObject
            {
                ["chargingRateUnit"] = "W",
                ["chargingSchedulePeriod"] = new JsonArray(new JsonObject { ["startPeriod"] = 0, ["limit"] = limitW }),
            },
        };
        if (transactionId is { } tx)
        {
            profile["transactionId"] = tx;
        }

        return profile;
    }

    private static string? Status(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString()
            : null;
}
