# 04.02-dependent-libraries: Multi-target the six libraries that depend on Mulse.Modules

## Objective
After `Mulse.Modules` is valid for both frameworks, multi-target its six direct dependents for `net11.0;net10.0` and keep consumers on each target working.

## Scope
- Projects: `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`.
- Apply package recommendations: System.Security.Cryptography.Pkcs 10.0.14 to 10.0.15 in `Mulse.Dbms.SqlServer`; System.Security.Cryptography.Xml 8.0.5 to 10.0.15 in `Mulse.Excel`. The Microsoft.Extensions and System.Memory.Data recommendations are addressed by the direct `Mulse.Modules` update.
- Resolve only reproduced source incompatibilities and behavioral issues; assessment findings are potential and largely repeated `BinaryData` references.
- Preserve the plugin projects' runtime loading convention and isolate net11 outputs from existing net10 outputs. The Service task will make `ModuleCatalog` select the matching framework output while retaining compatibility with existing plugin paths.
- `Service` remains net10.0 until task 05; `Service.Tests` targets net11.0 and references Billing, Excel, HL7, and SamplePlugin.

## Research Findings
- All six SDK-style libraries currently target only `net10.0` and directly reference `Mulse.Modules`. `Mulse.CompatibilityPack` is also consumed by `Service`; `Mulse.Billing`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin` are referenced by `Service.Tests`; `Mulse.Dbms.SqlServer` is runtime-discovered.
- Assessment findings: CompatibilityPack has 26 potential source-compatibility occurrences (mostly `BinaryData`) and one behavioral finding; Billing has only the required TFM change; Dbms.SqlServer has four potential `BinaryData` occurrences and recommends Pkcs `10.0.14` to `10.0.15`; Excel has seven potential source findings and two behavioral findings (`MemoryStream` and `JsonDocument`) and recommends Xml `8.0.5` to `10.0.15`; HL7 has 14 potential `BinaryData` findings; SamplePlugin has 10 potential `BinaryData` findings. These findings are hypotheses; change source only for reproduced build or test failures.
- Package references are project-local, not centrally managed. The two explicit package updates are in `Mulse.Dbms.SqlServer.csproj` and `Mulse.Excel.csproj`; the remaining assessment package recommendations are inherited through `Mulse.Modules` and were handled in task 04.01.
- `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin` set `OutDir` to `Service\\plugins\\<project>\\` and copy package dependencies locally. This overrides normal TFM-specific output separation and causes both TFMs to resolve the same output path. Keep the existing path for net10 and place net11 artifacts under a `net11.0` child directory.
- `Service/Modules/ModuleCatalog.cs` scans one directory level below the plugin root. `Service/Data/mulse-runtime.json` can persist absolute assembly paths. `Service` remains net10 during this task, so its legacy net10 paths remain valid; task 05 must prefer matching `net11.0` output when the host is retargeted while preserving direct-path fallback.
- The current `Service` build exposed CS8604 in `Mulse.CompatibilityPack/XmlMappingEngine.CustomFunctions.cs:44`: LINQ filtering on `Body` does not preserve nullable flow state. Guard and store the body locally before passing it to `Expand`. Other reported `Service` warnings are in Service source and are outside this task because Service is not modified here.
- `Service.Tests` targets `net11.0` and references Service, UI, Excel, SamplePlugin, Billing, and HL7. No `// STUB:` markers were found in the six library projects.

## Steps
1. Inspect each project's assessment details, package definitions, and custom output settings.
2. Add `net11.0;net10.0` target frameworks (newest first) and update the applicable direct package versions.
3. Build all six projects for both frameworks; fix actual diagnostics and warnings.
4. Confirm the current net10 `Service` project still builds and that net10 plugin paths remain unchanged while net11 plugin outputs are isolated under `net11.0` subdirectories.
5. Run the full `Service.Tests` suite and record results.

## Done when
- All six libraries build for net11.0 and net10.0 without warnings.
- The current net10 `Service` consumer builds, while the net11 test project resolves the new library targets.
- Net10 plugin output remains usable at its existing path, net11 output cannot overwrite it, package recommendations are applied, and the full test suite passes. Matching net11 runtime plugin selection is verified in task 05.
