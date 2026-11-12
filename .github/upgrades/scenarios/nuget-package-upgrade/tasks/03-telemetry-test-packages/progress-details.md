# Telemetry and test package upgrade results

- Updated the three assessed OpenTelemetry references to 1.21.0 and Microsoft.NET.Test.Sdk to 18.12.0. All four resolved at the requested versions.
- `dotnet build Mulse.slnx`: succeeded, 0 warnings and 0 errors. Visual Studio workspace build also succeeded.
- `dotnet test Service.Tests\Service.Tests.csproj --no-build`: 2,100 passed, 0 failed, 0 skipped.
- A live telemetry collector was not separately exercised.
