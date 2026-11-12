# 06 Library consolidation progress

## Changes
- Retargeted all eight shared libraries to single-target `net11.0`: ServiceDefaults, Modules, CompatibilityPack, Billing, Dbms.SqlServer, Excel, HL7, and SamplePlugin.
- Preserved the five plugin assemblies' `Service/plugins/<package>/net11.0/` output paths and local dependency-copy behavior; removed only the obsolete net10 output paths.
- Consolidated ServiceDefaults' resilience and service-discovery package references to version 11.0.0, removing the net10-only versions and conditions.
- No source-level `NET10_0` compilation branches existed. Removed Modules' explicit `Microsoft.Extensions.Logging.Abstractions` reference after .NET 11 restore reported NU1510; its dependency remains available transitively and the complete build/tests pass.

## Validation
- Verified all 12 solution projects declare only `TargetFramework` `net11.0`; no `net10.0`, `TargetFrameworks`, or `NET10_0` references remain in project, props, targets, or C# source files.
- `dotnet build Mulse.slnx`: succeeded with 0 warnings and 0 errors after resolving NU1510.
- `dotnet test Service.Tests\Service.Tests.csproj`: 2,100 passed, 0 failed, 0 skipped.

## Issues encountered
- The first solution build reported NU1510 for the explicit Logging.Abstractions reference in `Mulse.Modules.csproj`. Removed the unnecessary direct reference; the subsequent solution build was warning-free.
