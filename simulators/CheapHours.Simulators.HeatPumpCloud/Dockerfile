# syntax=docker/dockerfile:1.7
# The heat-pump-cloud simulator for local development. Build context: the repository root (it needs the shared
# build files); see docker-compose.yml, service heat-pump-cloud.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY simulators/CheapHours.Simulators.HeatPumpCloud/CheapHours.Simulators.HeatPumpCloud.csproj simulators/CheapHours.Simulators.HeatPumpCloud/
RUN dotnet restore simulators/CheapHours.Simulators.HeatPumpCloud/CheapHours.Simulators.HeatPumpCloud.csproj
COPY simulators/CheapHours.Simulators.HeatPumpCloud/ simulators/CheapHours.Simulators.HeatPumpCloud/
RUN dotnet publish simulators/CheapHours.Simulators.HeatPumpCloud/CheapHours.Simulators.HeatPumpCloud.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0
WORKDIR /app
COPY --from=build /app .
# The image's app user (APP_UID), by id so a runtime can check it is not root.
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "CheapHours.Simulators.HeatPumpCloud.dll"]
