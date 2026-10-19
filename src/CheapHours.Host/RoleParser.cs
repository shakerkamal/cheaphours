using System.Diagnostics.CodeAnalysis;

namespace CheapHours.Host;

/// <summary>Reads the process role from the command line.</summary>
internal static class RoleParser
{
    /// <summary>Exit code used when the role is missing or unknown.</summary>
    public const int InvalidRoleExitCode = 2;

    private const string RoleOption = "--role";

    private static readonly Dictionary<string, HostRole> Roles = new(StringComparer.Ordinal)
    {
        ["api"] = HostRole.Api,
        ["gateway"] = HostRole.Gateway,
        ["worker"] = HostRole.Worker,
    };

    /// <summary>The role names accepted after <c>--role</c>, in display order.</summary>
    public static IReadOnlyList<string> ValidRoles { get; } = ["api", "gateway", "worker"];

    /// <summary>
    /// Finds <c>--role &lt;name&gt;</c> or <c>--role=&lt;name&gt;</c> in <paramref name="args"/>.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="role">The parsed role when this returns <see langword="true"/>.</param>
    /// <param name="error">A message listing the valid roles when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when exactly one known role was given.</returns>
    public static bool TryParse(
        IReadOnlyList<string> args,
        out HostRole role,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var value = FindRoleValue(args);
        if (value is not null && Roles.TryGetValue(value, out role))
        {
            error = null;
            return true;
        }

        role = default;
        var given = value is null ? "no role was given" : $"unknown role '{value}'";
        error = $"CheapHours: {given}. Start with {RoleOption} <role>, where <role> is one of: {string.Join(", ", ValidRoles)}.";
        return false;
    }

    private static string? FindRoleValue(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == RoleOption)
            {
                return i + 1 < args.Count ? args[i + 1] : null;
            }

            if (arg.StartsWith(RoleOption + "=", StringComparison.Ordinal))
            {
                return arg[(RoleOption.Length + 1)..];
            }
        }

        return null;
    }
}
