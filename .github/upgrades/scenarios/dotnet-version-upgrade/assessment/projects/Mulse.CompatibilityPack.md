# Mulse.CompatibilityPack\Mulse.CompatibilityPack.csproj

[← Back to the assessment index](../../assessment.md)

## Project Info

- **Current Target Framework:** net10.0
- **Proposed Target Framework:** net11.0
- **SDK-style**: True
- **Project Kind:** ClassLibrary
- **Dependencies**: 1
- **Dependants**: 1
- **Number of Files**: 31
- **Number of Files with Incidents**: 14
- **Lines of Code**: 3857
- **Estimated LOC to modify**: 27+ (at least 0.7% of the project)

## Related Projects

**Depends on (1)** — projects this one references:

- [C:\Users\LXS0610\source\repos\Mulse\Mulse.Modules\Mulse.Modules.csproj](../projects/Mulse.Modules.md)

**Depended on by (1)** — projects that reference this one:

- [C:\Users\LXS0610\source\repos\Mulse\Service\Service.csproj](../projects/Service.md)

## Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph upstream["Dependants (1)"]
        P12["<b>📦&nbsp;Service.csproj</b><br/><small>net10.0</small>"]
        click P12 "../projects/Service.md"
    end
    subgraph current["Mulse.CompatibilityPack.csproj"]
        MAIN["<b>📦&nbsp;Mulse.CompatibilityPack.csproj</b><br/><small>net10.0</small>"]
        click MAIN "../projects/Mulse.CompatibilityPack.md"
    end
    subgraph downstream["Dependencies (1)"]
        P7["<b>📦&nbsp;Mulse.Modules.csproj</b><br/><small>net10.0</small>"]
        click P7 "../projects/Mulse.Modules.md"
    end
    P12 --> MAIN
    MAIN --> P7

```

## API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 26 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 1 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 5253 |  |
| ***Total APIs Analyzed*** | ***5280*** |  |

