# 03.01-service-defaults: Multi-target Mulse.ServiceDefaults and update its packages

## Objective
Prepare the shared `Mulse.ServiceDefaults` library for the `Mulse.Ui` .NET 11 upgrade while preserving its existing .NET 10 consumer.

## Scope
- Project: `Mulse.ServiceDefaults\Mulse.ServiceDefaults.csproj` and its source/configuration as needed.
- Change `TargetFramework` to `TargetFrameworks` with `net11.0` and `net10.0`, keeping the new TFM first.
- Apply the assessment-recommended versions: `Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.ServiceDiscovery` 11.0.0; `OpenTelemetry.Instrumentation.AspNetCore` and `OpenTelemetry.Instrumentation.Http` 1.21.0.
- Resolve any TFM-specific package compatibility with conditional references only if restore requires it.

## Validation
Build the project for both TFMs, confirm the recommended package versions resolve, and verify its existing .NET 10 consumers remain buildable. No API compatibility incidents or test-coverage recommendation were found for this project.

## Research
- The project is an SDK-style class library currently targeting `net10.0`; its `TargetFramework` property is declared directly in the project file.
- The package dependency scan found no central package management or imported package source. All seven package references are direct and defined in `Mulse.ServiceDefaults.csproj`.
- The assessment reports 4 package upgrades and 0 API issues: `Microsoft.Extensions.Http.Resilience` 10.11.0 → 11.0.0; `Microsoft.Extensions.ServiceDiscovery` 10.11.0 → 11.0.0; both OpenTelemetry instrumentation packages 1.20.1 → 1.21.0. Other OpenTelemetry references are assessed as compatible and have no recommended version.
- `Mulse.Ui` and `Service` are its two consumers. The new target is listed first to keep IDE/default builds on `net11.0`; retain `net10.0` until those consumers are upgraded.
- No `// STUB:` markers were found. No source changes are expected; package resolution and both TFM builds are the validation gates.
