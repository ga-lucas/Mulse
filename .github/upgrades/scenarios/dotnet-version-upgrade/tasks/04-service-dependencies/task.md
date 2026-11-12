# 04-service-dependencies: Prepare Service library dependencies

Temporarily multi-target `Mulse.Modules`, `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin` for `net10.0` and `net11.0`. Resolve package recommendations and the assessment's API incompatibilities as needed for both the existing consumers and `Service`'s new target; the affected libraries include source-incompatible and behavioral-change findings, particularly in `Mulse.Modules` and `Mulse.CompatibilityPack`.

## Scope inventory and research
- All seven listed projects are SDK-style class libraries currently targeting `net10.0`. `get_project_dependencies` found no central package management; package versions are explicit in the owning project when direct.
- Dependency order is `Mulse.Modules` first, then its six direct consumers: `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`. `Service` also consumes `Mulse.Modules` and `Mulse.CompatibilityPack` but remains net10.0 until task 05.
- `Service.Tests` is already net11.0 and references Billing, Excel, SamplePlugin, and HL7. Its full 2,097-test suite passed after the prior UI/test upgrade; re-run it after the dependent libraries move to net11.0.
- Package recommendations: in `Mulse.Modules`, upgrade Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Http, Microsoft.Extensions.Logging.Abstractions, and System.Memory.Data from 10.0.14 to 10.0.15; in `Mulse.Dbms.SqlServer`, upgrade System.Security.Cryptography.Pkcs 10.0.14 to 10.0.15; in `Mulse.Excel`, upgrade System.Security.Cryptography.Xml 8.0.5 to 10.0.15. Dependency analysis confirms the shared Microsoft.Extensions and System.Memory.Data versions are otherwise transitive.
- Assessment findings: Modules has 109 source-incompatible and 56 behavioral findings, largely repeated `System.BinaryData` API usages; CompatibilityPack has 26 source and 1 behavioral finding, also largely BinaryData plus a MemoryStream constructor. Dbms.SqlServer has 4 source findings (BinaryData); Excel has 7 source and 2 behavioral findings (BinaryData, MemoryStream, JsonDocument); Hl7 has 14 source findings and SamplePlugin 10, primarily BinaryData; Billing has no API findings. Treat these as potential findings and verify actual compiler/runtime effects before changing behavior.
- No `// STUB:` markers were found in the seven projects.
- Billing, Dbms.SqlServer, Excel, HL7, and SamplePlugin override `OutDir` into `Service\\plugins\\...` for runtime module loading. Verify multi-target builds preserve the correct per-TFM plugin outputs for the existing net10 host and the upcoming net11 host.

## Planned dependency-order breakdown
1. `04.01-modules`: multi-target and resolve package/API findings in the leaf `Mulse.Modules`; build net10 and net11 and verify existing net10 consumers remain buildable.
2. `04.02-dependent-libraries`: after the leaf is valid, multi-target the six direct dependents, apply their package/API fixes, build all target frameworks, and run `Service.Tests`.

**Done when**: Each listed library builds for both target frameworks, existing `net10.0` consumers remain buildable, and `Service` can resolve its `net11.0` library dependencies.
