# Mulse.SamplePlugin\Mulse.SamplePlugin.csproj

[← Back to the assessment index](../../assessment.md)

## Project Info

- **Current Target Framework:** net10.0
- **Proposed Target Framework:** net11.0
- **SDK-style**: True
- **Project Kind:** ClassLibrary
- **Dependencies**: 1
- **Dependants**: 1
- **Number of Files**: 11
- **Number of Files with Incidents**: 4
- **Lines of Code**: 464
- **Estimated LOC to modify**: 10+ (at least 2.2% of the project)

## Related Projects

**Depends on (1)** — projects this one references:

- [C:\Users\LXS0610\source\repos\Mulse\Mulse.Modules\Mulse.Modules.csproj](../projects/Mulse.Modules.md)

**Depended on by (1)** — projects that reference this one:

- [C:\Users\LXS0610\source\repos\Mulse\Service.Tests\Service.Tests.csproj](../projects/Service.Tests.md)

## Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph upstream["Dependants (1)"]
        P11["<b>📦&nbsp;Service.Tests.csproj</b><br/><small>net10.0</small>"]
        click P11 "../projects/Service.Tests.md"
    end
    subgraph current["Mulse.SamplePlugin.csproj"]
        MAIN["<b>📦&nbsp;Mulse.SamplePlugin.csproj</b><br/><small>net10.0</small>"]
        click MAIN "../projects/Mulse.SamplePlugin.md"
    end
    subgraph downstream["Dependencies (1)"]
        P7["<b>📦&nbsp;Mulse.Modules.csproj</b><br/><small>net10.0</small>"]
        click P7 "../projects/Mulse.Modules.md"
    end
    P11 --> MAIN
    MAIN --> P7

```

## API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 10 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 1582 |  |
| ***Total APIs Analyzed*** | ***1592*** |  |

