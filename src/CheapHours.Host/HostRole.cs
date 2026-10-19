namespace CheapHours.Host;

/// <summary>The process role selected with <c>--role</c>; one image runs as any of the three.</summary>
internal enum HostRole
{
    /// <summary>Public REST API under <c>/v1</c>.</summary>
    Api,

    /// <summary>Device gateway: OCPP WebSockets and vendor webhooks.</summary>
    Gateway,

    /// <summary>Scheduled jobs and message consumers.</summary>
    Worker,
}
