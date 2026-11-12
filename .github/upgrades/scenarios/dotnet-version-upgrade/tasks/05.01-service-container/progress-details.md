# 05.01 Service container progress

## Changes
- Retargeted `Service` to `net11.0`, updated `Microsoft.AspNetCore.OpenApi` to `11.0.0`, removed the unsupported Visual Studio container-tools package, and configured SDK container publishing for `mcr.microsoft.com/dotnet/aspnet:11.0-nanoserver-ltsc2025` on `win-x64`.
- Updated the manual Dockerfile to .NET 11 Windows Server 2025 images and removed the Visual Studio Build Tools install stage; `Service` has no `PublishAot` configuration.
- Removed the package-dependent Docker launch profile while retaining the normal Service profile.
- Fixed XML parameter documentation and tuple nullability in `BizTalkNativeMapConverter.cs` to resolve the five prior compiler warnings.
- Added `Service/README.md` with SDK archive and manual Dockerfile publishing instructions.

## Validation
- `dotnet build Service\Service.csproj`: succeeded with 0 warnings and 0 errors.
- `dotnet build Service.Tests\Service.Tests.csproj`: succeeded with 0 warnings and 0 errors.
- `dotnet test Service.Tests\Service.Tests.csproj`: 2,097 passed, 0 failed, 0 skipped.
- `dotnet list Service.Tests\Service.Tests.csproj package --include-transitive` showed `Microsoft.AspNetCore.OpenApi 11.0.0`; the unsupported `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` package was absent.
- Ran the documented `dotnet publish Service\Service.csproj -c Release -t:PublishContainer -p:ContainerArchiveOutputPath=bin\Release\net11.0\container\mulse-service.tar.gz` command. The SDK produced a 259,039,744-byte archive using the configured .NET 11 Nano Server image without a local container daemon; the generated archive was removed after verification.
- Parsed `Service/Properties/launchSettings.json` successfully and confirmed only the standard `Service` launch profile remains.

## Notes
The Windows container image requires a Windows Server 2025-compatible container host. The manual Dockerfile was updated and statically reviewed; no local Docker build was needed to validate the SDK archive workflow. Visual Studio's `run_build` check reported NETSDK1005 for stale `net10.0` assets in dependencies; the scenario's required CLI build path was rerun successfully with 0 warnings and 0 errors.
