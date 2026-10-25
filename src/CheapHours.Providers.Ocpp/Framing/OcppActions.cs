namespace CheapHours.Providers.Ocpp.Framing;

/// <summary>The action names OCPP 1.6 defines, including the 1.6 security extension.</summary>
public static class OcppActions
{
    /// <summary>The <c>BootNotification</c> action.</summary>
    public const string BootNotification = nameof(BootNotification);

    /// <summary>The <c>Heartbeat</c> action.</summary>
    public const string Heartbeat = nameof(Heartbeat);

    /// <summary>The <c>StatusNotification</c> action.</summary>
    public const string StatusNotification = nameof(StatusNotification);

    /// <summary>Gets every action name OCPP 1.6 defines (case-sensitive, as on the wire).</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Authorize", BootNotification, "CancelReservation", "ChangeAvailability", "ChangeConfiguration",
        "ClearCache", "ClearChargingProfile", "DataTransfer", "DiagnosticsStatusNotification",
        "FirmwareStatusNotification", "GetCompositeSchedule", "GetConfiguration", "GetDiagnostics",
        "GetLocalListVersion", Heartbeat, "MeterValues", "RemoteStartTransaction", "RemoteStopTransaction",
        "ReserveNow", "Reset", "SendLocalList", "SetChargingProfile", "StartTransaction", StatusNotification,
        "StopTransaction", "TriggerMessage", "UnlockConnector", "UpdateFirmware",
        "CertificateSigned", "DeleteCertificate", "ExtendedTriggerMessage", "GetInstalledCertificateIds",
        "GetLog", "InstallCertificate", "LogStatusNotification", "SecurityEventNotification",
        "SignCertificate", "SignedFirmwareStatusNotification", "SignedUpdateFirmware",
    };

    /// <summary>Returns whether OCPP 1.6 defines <paramref name="action"/>.</summary>
    /// <param name="action">The action name from a Call.</param>
    /// <returns><see langword="true"/> for a defined action.</returns>
    public static bool IsKnown(string action) => All.Contains(action);
}
