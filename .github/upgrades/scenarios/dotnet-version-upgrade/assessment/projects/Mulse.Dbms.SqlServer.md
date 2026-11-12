# Mulse.Dbms.SqlServer\Mulse.Dbms.SqlServer.csproj

[← Back to the assessment index](../../assessment.md)

## Project Info

- **Current Target Framework:** net10.0
- **Proposed Target Framework:** net11.0
- **SDK-style**: True
- **Project Kind:** ClassLibrary
- **Dependencies**: 1
- **Dependants**: 0
- **Number of Files**: 4
- **Number of Files with Incidents**: 3
- **Lines of Code**: 378
- **Estimated LOC to modify**: 4+ (at least 1.1% of the project)

## Related Projects

**Depends on (1)** — projects this one references:

- [C:\Users\LXS0610\source\repos\Mulse\Mulse.Modules\Mulse.Modules.csproj](../projects/Mulse.Modules.md)

## Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph current["Mulse.Dbms.SqlServer.csproj"]
        MAIN["<b>📦&nbsp;Mulse.Dbms.SqlServer.csproj</b><br/><small>net10.0</small>"]
        click MAIN "../projects/Mulse.Dbms.SqlServer.md"
    end
    subgraph downstream["Dependencies (1)"]
        P7["<b>📦&nbsp;Mulse.Modules.csproj</b><br/><small>net10.0</small>"]
        click P7 "../projects/Mulse.Modules.md"
    end
    MAIN --> P7

```

## API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 4 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 337 |  |
| ***Total APIs Analyzed*** | ***341*** |  |

## NuGet Package Issues

| Package | Current Version | Suggested Version | Severity | Issue |
| :--- | :---: | :---: | :---: | :--- |
| System.Security.Cryptography.Pkcs | 10.0.14 | 10.0.15 | 🟡 Potential | NuGet package upgrade is recommended |

Every project affected by these packages, and the versions the repository settles on: [aggregate NuGet packages](../nuget/aggregate-packages.md).

