using CheapHours.Host;
using CheapHours.Host.Api;
using CheapHours.Host.Gateway;
using CheapHours.Host.Worker;

if (!RoleParser.TryParse(args, out var role, out var error))
{
    await Console.Error.WriteLineAsync(error);
    return RoleParser.InvalidRoleExitCode;
}

IHost host = role switch
{
    HostRole.Api => ApiRole.Build(args),
    HostRole.Gateway => GatewayRole.Build(args),
    HostRole.Worker => WorkerRole.Build(args),
    _ => throw new InvalidOperationException($"Unhandled role {role}."),
};

await host.RunAsync();
return 0;
