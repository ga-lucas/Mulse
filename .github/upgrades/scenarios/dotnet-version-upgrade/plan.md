# .NET Version Upgrade Plan

## Overview

**Target**: Upgrade the entire solution from .NET 10 to .NET 11 (`net11.0`).
**Scope**: 12 projects: 3 application/host projects, 8 class libraries, and 1 test project.

### Selected Strategy
**Top-Down (Application-First)** — Applications upgraded first, libraries multi-targeted temporarily.
**Rationale**: The four-level project dependency graph makes a staged upgrade preferable to changing all 12 projects in one atomic pass.

### Application and Library Classification
- **Applications/hosts (priority order)**: `Mulse.AppHost` (security package update and limited dependencies), `Mulse.Ui` (one library dependency), then `Service` (deeper dependency closure and largest application API issue count).
- **Test project**: `Service.Tests`, upgraded with `Mulse.Ui` first because it tests that app and references it, then validated again with `Service` when that app moves to .NET 11.
- **Libraries to multi-target temporarily**: `Mulse.ServiceDefaults`, `Mulse.Modules`, `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`.

### Application Dependency Map
- `Mulse.AppHost` references `Mulse.Ui` and `Service`.
- `Mulse.Ui` references `Mulse.ServiceDefaults`.
- `Service` references `Mulse.Modules`, `Mulse.ServiceDefaults`, and `Mulse.CompatibilityPack`; `Mulse.Modules` references `Mulse.Billing`, `Mulse.CompatibilityPack`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`.
- `Service.Tests` references `Mulse.Ui`, `Service`, and several of the shared libraries.

### Phase 2 Trigger
Begin library consolidation only after all application/host projects and `Service.Tests` target .NET 11 and no consumer needs `net10.0`.

## Upgrade Options

| Option | Selected | Why |
|--------|----------|-----|
| Upgrade Strategy | Top-Down | The four-level dependency graph and 12-project solution favor staged application upgrades. |
| Unsupported Packages | Resolve Inline | The single incompatible package is used by two projects, a small enough scope to resolve during the upgrade. |
| Unsupported API Handling | Fix Inline | The assessment reports 1 binary- and 449 source-incompatible API findings; fixing inline avoids deferred stubs and follow-up work. |

## Tasks

### 01-prerequisites: Verify .NET 11 toolchain

Confirm the .NET 11 SDK is installed and that repository SDK selection via `global.json`, if present, is compatible with `net11.0`. This is a solution-wide prerequisite for the application-first upgrade.

**Done when**: The selected SDK can build the solution and any required `global.json` change is recorded.

### 02-apphost: Upgrade Mulse.AppHost

Retarget `Mulse.AppHost` to `net11.0` and update its Aspire packages, including `Aspire.Hosting.AppHost` from 13.6.4 to the assessment-recommended 13.6.5. The assessment flags this package for a security vulnerability. Keep its references to `Mulse.Ui` and `Service` working while those projects still target `net10.0`.

**Done when**: `Mulse.AppHost` builds for `net11.0`, its package vulnerability finding is addressed, and its project references resolve.

### 03-ui: Upgrade Mulse.Ui and its tests

Retarget `Mulse.Ui` and `Service.Tests` to `net11.0`, and temporarily multi-target `Mulse.ServiceDefaults` so both upgraded and not-yet-upgraded consumers can build. Updating the test project with `Mulse.Ui` avoids leaving a `net10.0` test project referencing a `net11.0` app; its existing `Service` and library references can remain on `net10.0` until their upgrades. The assessment reports 48 API findings for `Mulse.Ui` and 528 for `Service.Tests`, including source-incompatible and behavioral-change findings; review affected code and preserve intended behavior.

**Done when**: `Mulse.Ui` and `Service.Tests` build for `net11.0`, `Mulse.ServiceDefaults` supports both `net10.0` and `net11.0`, and the UI-related tests pass against the upgraded app.

### 04-service-dependencies: Prepare Service library dependencies

Temporarily multi-target `Mulse.Modules`, `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin` for `net10.0` and `net11.0`. Resolve package recommendations and the assessment's API incompatibilities as needed for both the existing consumers and `Service`'s new target; the affected libraries include source-incompatible and behavioral-change findings, particularly in `Mulse.Modules` and `Mulse.CompatibilityPack`.

**Done when**: Each listed library builds for both target frameworks, existing `net10.0` consumers remain buildable, and `Service` can resolve its `net11.0` library dependencies.

### 04.01-modules: Multi-target Mulse.Modules and resolve its package/API findings

Multi-target the leaf library `Mulse.Modules` for `net11.0;net10.0`, apply the assessed package patch recommendations, and resolve only reproducible API incompatibilities while preserving net10 consumers.

**Done when**: `Mulse.Modules` builds for both target frameworks without warnings, the existing net10 Service consumer still builds, and relevant regressions pass.

### 04.02-dependent-libraries: Multi-target libraries that depend on Mulse.Modules

Multi-target `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`; apply package recommendations and resolve verified API issues. Preserve the existing net10 Service host and ensure plugin output remains compatible with its host target.

**Done when**: Each library builds for net10.0 and net11.0 without warnings, the net10 Service consumer still builds, net11 consumers resolve the new library targets, and the full test suite passes.

### 05-service: Upgrade Service

Retarget `Service` to `net11.0` and apply the assessment's recommended package updates. Resolve the incompatible `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` package in `Service` and `Service.Tests`; the test project already targets `net11.0` from the UI upgrade task. Address the 254 API findings reported for `Service`, including behavioral changes, and rerun `Service.Tests` to verify its references now resolve to the upgraded app and libraries.

**Done when**: `Service` builds for `net11.0`, the incompatible package is resolved in both projects, and the full `Service.Tests` suite passes against `Service` and `Mulse.Ui` on `net11.0`.

### 06-library-consolidation: Retarget shared libraries to .NET 11

Once all application and test consumers use `net11.0`, remove `net10.0` from the temporarily multi-targeted libraries: `Mulse.ServiceDefaults`, `Mulse.Modules`, `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`. Remove only framework-specific compilation branches that are no longer needed; preserve symbols serving other behavior or security controls.

**Done when**: All eight shared libraries target only `net11.0`, and no project retains a conditional path required solely for `net10.0`.

### 07-validation: Validate the full solution

Build the complete solution and run all discovered tests after consolidation. Verify that every project in the solution targets `net11.0`, that the reported package upgrades and incompatible package resolution are complete, and that no dependency conflicts or unintended behavioral regressions remain.

**Done when**: The full solution builds without errors, all tests pass, every project targets `net11.0`, and package dependency checks show no unresolved incompatibilities.
