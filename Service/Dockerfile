# Manual Dockerfile build; SDK container publishing is configured in Service.csproj.
# Windows .NET 11 images require a Windows Server 2025-compatible container host.
FROM mcr.microsoft.com/dotnet/aspnet:11.0-nanoserver-ltsc2025 AS base
WORKDIR /app


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:11.0-windowsservercore-ltsc2025 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Service/Service.csproj", "Service/"]
RUN dotnet restore "./Service/Service.csproj"
COPY . .
WORKDIR "/src/Service"
RUN dotnet build "./Service.csproj" -c %BUILD_CONFIGURATION% -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Service.csproj" -c %BUILD_CONFIGURATION% -o /app/publish /p:UseAppHost=true

# This stage is used in production
FROM mcr.microsoft.com/dotnet/aspnet:11.0-nanoserver-ltsc2025 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["Service.exe"]