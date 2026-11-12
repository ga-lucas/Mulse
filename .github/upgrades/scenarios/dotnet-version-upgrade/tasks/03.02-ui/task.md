# 03.02-ui: Retarget Mulse.Ui and resolve its API findings

## Objective
Upgrade `Mulse.Ui` from `net10.0` to `net11.0` after `Mulse.ServiceDefaults` supports both frameworks.

## Scope
- Project: `Mulse.Ui\Mulse.Ui.csproj` and affected source files.
- Preserve the reference to `Mulse.ServiceDefaults`; do not change unrelated consumers.
- Resolve the assessment's 1 source-incompatible and 47 behavioral API findings. Do not edit generated files under `obj`; investigate source/package inputs for generated incidents.

## Validation
Build `Mulse.Ui` against `net11.0`, confirm the project reference resolves to the new target, and run any relevant UI tests available before proceeding to the test-project subtask.
