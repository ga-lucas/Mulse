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
| Unsupported Packages | Resolve Inline | Replace the incompatible Visual Studio container-tools package with SDK container publishing, preserving a Windows container path. |
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

Retarget `Service` to `net11.0` and apply its direct package recommendations. Replace the incompatible Visual Studio container-tools dependency with the SDK's `PublishContainer` support, preserving the existing Windows-container workflow and updating the Dockerfile for manual builds. Review the assessment's API findings against the actual .NET 11 build and behavior, fixing verified incompatibilities without editing generated files. Update `ModuleCatalog` to select the matching `net11.0` plugin assemblies while retaining fallback for persisted or legacy paths.

**Done when**: `Service` builds for `net11.0` without warnings; SDK container publishing works with a supported Windows base image and the unsupported package is removed; the Dockerfile uses .NET 11 images; `ModuleCatalog` selects matching plugin assemblies without breaking legacy paths; and the full `Service.Tests` suite passes against `Service` and `Mulse.Ui` on `net11.0`.

### 05.01-service-container: Retarget Service and replace container tooling

Retarget `Service` to `net11.0`, update its supported OpenAPI package, and replace the unsupported Visual Studio container-tools dependency with SDK container publishing while preserving the Windows container path and a manual Dockerfile workflow.

**Done when**: Service and Service.Tests build successfully; Service is warning-free on `net11.0`; SDK container publishing produces an archive using a supported Windows .NET 11 base image; the Dockerfile targets .NET 11; and no broken Docker launch profile remains.

### 05.02-plugin-tfm-selection: Select matching plugin assemblies for the host TFM

Make plugin discovery and persisted managed-package loading select each plugin's `net11.0` output for the upgraded Service host while retaining fallback for existing legacy paths. Add regression coverage for matching-TFM selection and fallback behavior.

**Done when**: Auto-discovery and persisted package state choose the `net11.0` assembly when present, legacy paths continue to work when it is absent, and the targeted lifecycle tests pass.

### 05.03-endpoint-compatibility: Verify endpoint and middleware compatibility on .NET 11

Review Service endpoint and middleware compatibility findings against actual .NET 11 behavior; preserve caller-visible API semantics and edit only verified regressions. Generated OpenAPI source under `obj` is not editable.

**Done when**: Endpoint and middleware code has no verified unresolved .NET 11 regressions, generated files remain untouched, focused tests pass, and the affected projects build cleanly.

### 05.04-biztalk-mapping-flow-compatibility: Verify BizTalk, mapping, and flow compatibility on .NET 11

Review the remaining Service compatibility findings in BizTalk import, mapping, and flow execution, and fix only verified .NET 11 regressions while retaining exception and serialization semantics.

**Done when**: No verified unresolved compatibility regressions remain in the scoped areas, related tests pass, and Service and Service.Tests build without warnings.

### 06-library-consolidation: Retarget shared libraries to .NET 11

Once all application and test consumers use `net11.0`, remove `net10.0` from the temporarily multi-targeted libraries: `Mulse.ServiceDefaults`, `Mulse.Modules`, `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`. Remove only framework-specific compilation branches that are no longer needed; preserve symbols serving other behavior or security controls.

**Done when**: All eight shared libraries target only `net11.0`, and no project retains a conditional path required solely for `net10.0`.

### 07-validation: Validate the full solution

Build the complete solution and run all discovered tests after consolidation. Verify that every project in the solution targets `net11.0`, that the reported package upgrades and incompatible package resolution are complete, and that no dependency conflicts or unintended behavioral regressions remain.

**Done when**: The full solution builds without errors, all tests pass, every project targets `net11.0`, and package dependency checks show no unresolved incompatibilities.
