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

## User Preferences
### Security Follow-up
- **Networked authentication**: User wants networked authentication without a home-grown password/authentication scheme; retain cross-platform deployment support.
- **Guidance**: Explain concrete authentication options and guide the user step by step; confirm consequential setup choices before implementation (2026-11-12).
- **Provider choice**: Provider-neutral OIDC with self-hosted Keycloak as the initial supported implementation; no initial reliance on remote hosted services. User wording: "provider neutral, with a keycloak implementation (no initial) reliance on remote services?" (2026-11-12).
- **Hosting choice**: User selected containers for Keycloak and its database (2026-11-12).
- **Scope order**: User confirmed securing the UI and management API now, with inbound authentication deferred (2026-11-12).
- **Inbound HTTP flows**:- **Inbound HTTP flows**: Must remain supported as a first-class feature. User chose to defer inbound auth while securing UI/admin API now. Inbound requests remain unauthenticated until separately secured; recommend a firewall/trusted gateway meanwhile. Later inbound auth should be a shared per-route ingress policy with pluggable verifiers, not authentication code duplicated in fetch modules. User suggests remote-address allowlisting as an additional network restriction, not authentication (2026-11-12).
- **File picker**: User accepted the proposed host-path picker direction; address authentication and hosting security before implementing filesystem browsing.
