# syntax=docker/dockerfile:1.7
# The car-cloud simulator for local development. Build context: the repository root (it needs the shared
# build files); see docker-compose.yml, service car-cloud.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY simulators/CheapHours.Simulators.CarCloud/CheapHours.Simulators.CarCloud.csproj simulators/CheapHours.Simulators.CarCloud/
RUN dotnet restore simulators/CheapHours.Simulators.CarCloud/CheapHours.Simulators.CarCloud.csproj
COPY simulators/CheapHours.Simulators.CarCloud/ simulators/CheapHours.Simulators.CarCloud/
RUN dotnet publish simulators/CheapHours.Simulators.CarCloud/CheapHours.Simulators.CarCloud.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CheapHours.Simulators.CarCloud.dll"]
