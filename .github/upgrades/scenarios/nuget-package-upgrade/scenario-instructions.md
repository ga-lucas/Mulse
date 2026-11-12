# NuGet Package Upgrade

## Preferences
- **Flow Mode**: Automatic
- **Scope**: Entire solution
- **Version policy**: Latest supported stable versions; preserve existing behavior and avoid unverified breaking upgrades.
- **Packages**: System.Security.Cryptography.Pkcs, System.Security.Cryptography.Xml, Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Http, System.Memory.Data, OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Extensions.Hosting, OpenTelemetry.Instrumentation.Runtime, Microsoft.NET.Test.Sdk, NPOI, SixLabors.ImageSharp.
- **Assessment**: Quick; review ImageSharp major-version licensing before applying it.
- **Version decisions**: Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Http, System.Memory.Data, System.Security.Cryptography.Pkcs and System.Security.Cryptography.Xml 11.0.0; OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Extensions.Hosting and OpenTelemetry.Instrumentation.Runtime 1.21.0; Microsoft.NET.Test.Sdk 18.12.0. All are unified; update project-local references (no central package management).
- **NPOI and ImageSharp**: User chose to keep both current versions, NPOI 2.7.5 and ImageSharp 3.2.0, due to the NPOI binary's changed terms and the unresolved ImageSharp compatibility risk.

## Source Control
- **Source Branch**: upgrade-dotnet-11
- **Working Branch**: nuget-package-upgrade
- **Commit Strategy**: After Each Task
- **Branch Sync**: Auto (Merge)
