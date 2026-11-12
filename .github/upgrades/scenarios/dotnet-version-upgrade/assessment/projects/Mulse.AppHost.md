# Mulse.AppHost\Mulse.AppHost.csproj

[← Back to the assessment index](../../assessment.md)

## Project Info

- **Current Target Framework:** net10.0
- **Proposed Target Framework:** net11.0
- **SDK-style**: True
- **Project Kind:** DotNetCoreApp
- **Dependencies**: 2
- **Dependants**: 0
- **Number of Files**: 1
- **Number of Files with Incidents**: 1
- **Lines of Code**: 9
- **Estimated LOC to modify**: 0+ (at least 0.0% of the project)

## Related Projects

**Depends on (2)** — projects this one references:

- [C:\Users\LXS0610\source\repos\Mulse\Mulse.Ui\Mulse.Ui.csproj](../projects/Mulse.Ui.md)
- [C:\Users\LXS0610\source\repos\Mulse\Service\Service.csproj](../projects/Service.md)

## Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph current["Mulse.AppHost.csproj"]
        MAIN["<b>📦&nbsp;Mulse.AppHost.csproj</b><br/><small>net10.0</small>"]
        click MAIN "../projects/Mulse.AppHost.md"
    end
    subgraph downstream["Dependencies (2)"]
        P10["<b>📦&nbsp;Mulse.Ui.csproj</b><br/><small>net10.0</small>"]
        P12["<b>📦&nbsp;Service.csproj</b><br/><small>net10.0</small>"]
        click P10 "../projects/Mulse.Ui.md"
        click P12 "../projects/Service.md"
    end
    MAIN --> P10
    MAIN --> P12

```

## API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 41 |  |
| ***Total APIs Analyzed*** | ***41*** |  |

## NuGet Package Issues

| Package | Current Version | Suggested Version | Severity | Issue |
| :--- | :---: | :---: | :---: | :--- |
| Aspire.Hosting.AppHost | 13.6.4 | 13.6.5 | 🟡 Potential | NuGet package upgrade is recommended |
| Aspire.Hosting.AppHost | 13.6.4 | 13.6.5 | 🔵 Optional | NuGet package contains security vulnerability |

Every project affected by these packages, and the versions the repository settles on: [aggregate NuGet packages](../nuget/aggregate-packages.md).

