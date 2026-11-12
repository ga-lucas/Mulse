# Projects Relationship Graph

[← Back to the assessment index](../assessment.md)

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart LR
    P1["<b>📦&nbsp;Mulse.AppHost.csproj</b><br/><small>net10.0</small>"]
    P2["<b>📦&nbsp;Mulse.Billing.csproj</b><br/><small>net10.0</small>"]
    P3["<b>📦&nbsp;Mulse.CompatibilityPack.csproj</b><br/><small>net10.0</small>"]
    P4["<b>📦&nbsp;Mulse.Dbms.SqlServer.csproj</b><br/><small>net10.0</small>"]
    P5["<b>📦&nbsp;Mulse.Excel.csproj</b><br/><small>net10.0</small>"]
    P6["<b>📦&nbsp;Mulse.Hl7.csproj</b><br/><small>net10.0</small>"]
    P7["<b>📦&nbsp;Mulse.Modules.csproj</b><br/><small>net10.0</small>"]
    P8["<b>📦&nbsp;Mulse.SamplePlugin.csproj</b><br/><small>net10.0</small>"]
    P9["<b>📦&nbsp;Mulse.ServiceDefaults.csproj</b><br/><small>net10.0</small>"]
    P10["<b>📦&nbsp;Mulse.Ui.csproj</b><br/><small>net10.0</small>"]
    P11["<b>📦&nbsp;Service.Tests.csproj</b><br/><small>net10.0</small>"]
    P12["<b>📦&nbsp;Service.csproj</b><br/><small>net10.0</small>"]
    P1 --> P10
    P1 --> P12
    P2 --> P7
    P3 --> P7
    P4 --> P7
    P5 --> P7
    P6 --> P7
    P8 --> P7
    P10 --> P9
    P11 --> P10
    P11 --> P5
    P11 --> P8
    P11 --> P12
    P11 --> P6
    P11 --> P2
    P12 --> P7
    P12 --> P9
    P12 --> P3
    click P1 "projects/Mulse.AppHost.md"
    click P2 "projects/Mulse.Billing.md"
    click P3 "projects/Mulse.CompatibilityPack.md"
    click P4 "projects/Mulse.Dbms.SqlServer.md"
    click P5 "projects/Mulse.Excel.md"
    click P6 "projects/Mulse.Hl7.md"
    click P7 "projects/Mulse.Modules.md"
    click P8 "projects/Mulse.SamplePlugin.md"
    click P9 "projects/Mulse.ServiceDefaults.md"
    click P10 "projects/Mulse.Ui.md"
    click P11 "projects/Service.Tests.md"
    click P12 "projects/Service.md"

```

