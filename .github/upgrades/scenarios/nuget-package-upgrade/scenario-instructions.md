# NuGet Package Upgrade

## Preferences
- **Flow Mode**: Automatic
- **Scope**: Entire solution
- **Version policy**: Latest supported stable versions; preserve existing behavior and avoid unverified breaking upgrades.
- **Packages**: System.Security.Cryptography.Pkcs, System.Security.Cryptography.Xml, Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Http, System.Memory.Data, OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Extensions.Hosting, OpenTelemetry.Instrumentation.Runtime, Microsoft.NET.Test.Sdk, NPOI, SixLabors.ImageSharp.
- **Assessment**: Quick; review ImageSharp major-version licensing before applying it.
- **Version decisions**: Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Http, System.Memory.Data, System.Security.Cryptography.Pkcs and System.Security.Cryptography.Xml 11.0.0; OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Extensions.Hosting and OpenTelemetry.Instrumentation.Runtime 1.21.0; Microsoft.NET.Test.Sdk 18.12.0. All are unified; update project-local references (no central package management).
- **NPOI and ImageSharp**: Defer NPOI 2.8.1 pending user decision about its binary license and dependency changes; separately verify ImageSharp 4.1.3 against the existing NPOI integration before changing it.

## Source Control
- **Source Branch**: upgrade-dotnet-11
- **Working Branch**: nuget-package-upgrade
- **Commit Strategy**: After Each Task
- **Branch Sync**: Auto (Merge)
