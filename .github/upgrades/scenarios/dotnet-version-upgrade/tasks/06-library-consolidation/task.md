# 06-library-consolidation: Retarget shared libraries to .NET 11

Once all application and test consumers use `net11.0`, remove `net10.0` from the temporarily multi-targeted libraries: `Mulse.ServiceDefaults`, `Mulse.Modules`, `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`. Remove only framework-specific compilation branches that are no longer needed; preserve symbols serving other behavior or security controls.

## Research Findings
- Confirmed all 12 solution projects. `Mulse.AppHost`, `Mulse.Ui`, `Service`, and `Service.Tests` already target only `net11.0`; the eight scoped libraries each declare `TargetFrameworks` as `net11.0;net10.0` directly in their project files. `get_project_dependencies` found no imported MSBuild targets defining these properties.
- `Mulse.Modules` is the shared dependency for the other six libraries (CompatibilityPack, Billing, Dbms.SqlServer, Excel, HL7, SamplePlugin). `Mulse.ServiceDefaults` is consumed by the upgraded apps; all references can now resolve their single `net11.0` target.
- Only `Mulse.ServiceDefaults.csproj` has framework-conditioned package references: Microsoft.Extensions.Http.Resilience and Microsoft.Extensions.ServiceDiscovery are 10.11.0 for net10 and 11.0.0 for net11. Keep the 11.0.0 versions and remove the obsolete net10 variants/conditions.
- The five plugin project files (Billing, Dbms.SqlServer, Excel, HL7, SamplePlugin) have net10 and net11 `OutDir` conditions. Preserve their current `Service/plugins/<package>/net11.0/` output paths, and remove only the net10 output conditions. Keep `CopyLocalLockFileAssemblies` and the hot-loadable plugin behavior.
- No source-level `NET10_0` or `NET10_0_OR_GREATER` branches were found; no C# conditional compilation changes are needed. Package references are otherwise compatible and already aligned to the .NET 11 task.

**Done when**: All eight shared libraries target only `net11.0`, and no project retains a conditional path required solely for `net10.0`.
