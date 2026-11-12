# Mulse.ServiceDefaults\Mulse.ServiceDefaults.csproj

[← Back to the assessment index](../../assessment.md)

## Project Info

- **Current Target Framework:** net10.0
- **Proposed Target Framework:** net11.0
- **SDK-style**: True
- **Project Kind:** ClassLibrary
- **Dependencies**: 0
- **Dependants**: 2
- **Number of Files**: 1
- **Number of Files with Incidents**: 1
- **Lines of Code**: 127
- **Estimated LOC to modify**: 0+ (at least 0.0% of the project)

## Related Projects

**Depended on by (2)** — projects that reference this one:

- [C:\Users\LXS0610\source\repos\Mulse\Mulse.Ui\Mulse.Ui.csproj](../projects/Mulse.Ui.md)
- [C:\Users\LXS0610\source\repos\Mulse\Service\Service.csproj](../projects/Service.md)

## Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph upstream["Dependants (2)"]
        P10["<b>📦&nbsp;Mulse.Ui.csproj</b><br/><small>net10.0</small>"]
        P12["<b>📦&nbsp;Service.csproj</b><br/><small>net10.0</small>"]
        click P10 "../projects/Mulse.Ui.md"
        click P12 "../projects/Service.md"
    end
    subgraph current["Mulse.ServiceDefaults.csproj"]
        MAIN["<b>📦&nbsp;Mulse.ServiceDefaults.csproj</b><br/><small>net10.0</small>"]
        click MAIN "../projects/Mulse.ServiceDefaults.md"
    end
    P10 --> MAIN
    P12 --> MAIN

```

## API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 121 |  |
| ***Total APIs Analyzed*** | ***121*** |  |

## NuGet Package Issues

| Package | Current Version | Suggested Version | Severity | Issue |
| :--- | :---: | :---: | :---: | :--- |
| Microsoft.Extensions.Http.Resilience | 10.11.0 | 11.0.0 | 🟡 Potential | NuGet package upgrade is recommended |
| Microsoft.Extensions.ServiceDiscovery | 10.11.0 | 11.0.0 | 🟡 Potential | NuGet package upgrade is recommended |
| OpenTelemetry.Instrumentation.AspNetCore | 1.20.1 | 1.21.0 | 🟡 Potential | NuGet package upgrade is recommended |
| OpenTelemetry.Instrumentation.Http | 1.20.1 | 1.21.0 | 🟡 Potential | NuGet package upgrade is recommended |

Every project affected by these packages, and the versions the repository settles on: [aggregate NuGet packages](../nuget/aggregate-packages.md).

