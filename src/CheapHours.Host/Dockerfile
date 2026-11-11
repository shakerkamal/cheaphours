# syntax=docker/dockerfile:1.7
# The one CheapHours image; the role is chosen at start: docker run <image> --role api | gateway | worker.
# Build context: the repository root. Built and published by CI only (ADR-010).

# The output is framework-dependent IL, the same for every platform, so it is built once on the build machine's
# own platform and copied into each target platform's runtime image (no emulated SDK).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

# Restore first, from the project files and lock files only, so this layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/CheapHours.Core/*.csproj src/CheapHours.Core/packages.lock.json src/CheapHours.Core/
COPY src/CheapHours.Messaging.Contracts/*.csproj src/CheapHours.Messaging.Contracts/packages.lock.json src/CheapHours.Messaging.Contracts/
COPY src/CheapHours.Planning/*.csproj src/CheapHours.Planning/packages.lock.json src/CheapHours.Planning/
COPY src/CheapHours.Persistence/*.csproj src/CheapHours.Persistence/packages.lock.json src/CheapHours.Persistence/
COPY src/CheapHours.Messaging/*.csproj src/CheapHours.Messaging/packages.lock.json src/CheapHours.Messaging/
COPY src/CheapHours.Providers.Kit/*.csproj src/CheapHours.Providers.Kit/packages.lock.json src/CheapHours.Providers.Kit/
COPY src/CheapHours.Providers.Ocpp/*.csproj src/CheapHours.Providers.Ocpp/packages.lock.json src/CheapHours.Providers.Ocpp/
COPY src/CheapHours.Providers.CarCloud/*.csproj src/CheapHours.Providers.CarCloud/packages.lock.json src/CheapHours.Providers.CarCloud/
COPY src/CheapHours.Providers.HeatPumpCloud/*.csproj src/CheapHours.Providers.HeatPumpCloud/packages.lock.json src/CheapHours.Providers.HeatPumpCloud/
COPY src/CheapHours.Host/*.csproj src/CheapHours.Host/packages.lock.json src/CheapHours.Host/
RUN dotnet restore src/CheapHours.Host/CheapHours.Host.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/CheapHours.Host/CheapHours.Host.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

# Ubuntu chiseled: no shell, no package manager, non-root user app (1654). The -extra variant carries tzdata,
# which Europe/Berlin price days need. The runtime is the newest 10.0 patch (the app rolls forward to it); the
# packages' 10.0.12 runtime has fixable HIGH vulnerabilities.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra@sha256:a9298ccc39c6258ae71228479e2f3f8d2ceb6639a9927262a4b0ff7d319a0e7e
WORKDIR /app
COPY --from=build /app .
# The image's app user (APP_UID), by id so a runtime can check it is not root.
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "CheapHours.Host.dll"]
