# 03-ui: Upgrade Mulse.Ui and its tests

Retarget `Mulse.Ui` and `Service.Tests` to `net11.0`, and temporarily multi-target `Mulse.ServiceDefaults` so both upgraded and not-yet-upgraded consumers can build. Updating the test project with `Mulse.Ui` avoids leaving a `net10.0` test project referencing a `net11.0` app; its existing `Service` and library references can remain on `net10.0` until their upgrades. The assessment reports 48 API findings for `Mulse.Ui` and 528 for `Service.Tests`, including source-incompatible and behavioral-change findings; review affected code and preserve intended behavior.

**Done when**: `Mulse.Ui` and `Service.Tests` build for `net11.0`, `Mulse.ServiceDefaults` supports both `net10.0` and `net11.0`, and the UI-related tests pass against the upgraded app.

## Scope Inventory
- **Projects affected**: `Mulse.ServiceDefaults`, `Mulse.Ui`, and `Service.Tests`.
- **Distinct concerns**: Multi-target the shared library; upgrade its packages; retarget the UI and test project; resolve affected API source incompatibilities and validate behavior with tests.
- **Dependency order**: `Mulse.ServiceDefaults` is a leaf used by `Mulse.Ui` and `Service`; `Service.Tests` references `Mulse.Ui` and multiple still-`net10.0` projects. Preserve the existing TFM for old consumers until their upgrade tasks.
- **Assessment findings**: `Mulse.ServiceDefaults` has no API issues and 4 recommended package updates. `Mulse.Ui` has 1 source-incompatible and 47 behavioral API findings. `Service.Tests` has 254 source-incompatible and 274 behavioral API findings, plus one optional xUnit deprecation notice; assessment does not recommend a newer xUnit version. Test coverage generation is not recommended.
- **Package actions**: In `Mulse.ServiceDefaults`, update `Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.ServiceDiscovery` from 10.11.0 to 11.0.0, and `OpenTelemetry.Instrumentation.AspNetCore` and `OpenTelemetry.Instrumentation.Http` from 1.20.1 to 1.21.0. Keep the assessment-reported xUnit 2.9.3 version unless restore/build evidence requires an update.
- **Package/property sources**: `get_project_dependencies` found no CPM or imported property sources; each `TargetFramework` and the listed package versions are explicit in the respective project files.
- **Generated files**: Assessment findings include generated files under `obj`; do not edit generated artifacts. Address regenerated output through its source/package inputs.
- **Decomposition**: Required by the dependency-ordering hint for a three-project chain; split into library, UI, and test subtasks in dependency order. The test subtask is separate because its assessment scope is large and requires substantial validation.
