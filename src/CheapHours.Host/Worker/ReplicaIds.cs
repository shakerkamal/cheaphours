using System.Security.Cryptography;

namespace CheapHours.Host.Worker;

/// <summary>This worker replica's lease holder id.</summary>
internal static class ReplicaIds
{
    /// <summary>Returns <c>Worker:ReplicaId</c>, or the machine name with a random suffix when it is not set.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The holder id.</returns>
    public static string From(IConfiguration configuration) =>
        configuration["Worker:ReplicaId"] is { Length: > 0 } id
            ? id
            : $"{Environment.MachineName}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}";
}
