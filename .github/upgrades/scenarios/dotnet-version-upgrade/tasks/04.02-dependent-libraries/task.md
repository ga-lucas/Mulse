# 04.02-dependent-libraries: Multi-target the six libraries that depend on Mulse.Modules

## Objective
After `Mulse.Modules` is valid for both frameworks, multi-target its six direct dependents for `net11.0;net10.0` and keep consumers on each target working.

## Scope
- Projects: `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`.
- Apply package recommendations: System.Security.Cryptography.Pkcs 10.0.14 to 10.0.15 in `Mulse.Dbms.SqlServer`; System.Security.Cryptography.Xml 8.0.5 to 10.0.15 in `Mulse.Excel`. The Microsoft.Extensions and System.Memory.Data recommendations are addressed by the direct `Mulse.Modules` update.
- Resolve only reproduced source incompatibilities and behavioral issues; assessment findings are potential and largely repeated `BinaryData` references.
- Preserve the plugin projects' runtime loading convention and ensure their custom `OutDir` destinations do not leave a net10 Service host with net11-only plugin assemblies.
- `Service` remains net10.0 until task 05; `Service.Tests` targets net11.0 and references Billing, Excel, HL7, and SamplePlugin.

## Steps
1. Inspect each project's assessment details, package definitions, and custom output settings.
2. Add `net11.0;net10.0` target frameworks (newest first) and update the applicable direct package versions.
3. Build all six projects for both frameworks; fix actual diagnostics and warnings.
4. Confirm the current net10 `Service` project still builds and verify per-TFM plugin output/loading behavior.
5. Run the full `Service.Tests` suite and record results.

## Done when
- All six libraries build for net11.0 and net10.0 without warnings.
- The current net10 `Service` consumer builds, while the net11 test project resolves the new library targets.
- Plugin output remains usable by its matching host TFM, package recommendations are applied, and the full test suite passes.
