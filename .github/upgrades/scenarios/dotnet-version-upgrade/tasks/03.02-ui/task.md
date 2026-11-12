# 03.02-ui: Retarget Mulse.Ui and resolve its API findings

## Objective
Upgrade `Mulse.Ui` from `net10.0` to `net11.0` after `Mulse.ServiceDefaults` supports both frameworks.

## Scope
- Project: `Mulse.Ui\Mulse.Ui.csproj` and affected source files.
- Preserve the reference to `Mulse.ServiceDefaults`; do not change unrelated consumers.
- Resolve the assessment's 1 source-incompatible and 47 behavioral API findings. Do not edit generated files under `obj`; investigate source/package inputs for generated incidents.

## Research Findings
- `Mulse.Ui.csproj` is SDK-style ASP.NET Core Web, targets `net10.0`, and has only a project reference to `Mulse.ServiceDefaults`; its dependency report shows no explicit package references. The assessment lists no package upgrade recommendations for this project.
- `Mulse.ServiceDefaults` now targets `net11.0;net10.0`, ordered newest first, so the UI project reference can resolve to net11 when retargeted while preserving net10 consumers.
- Assessment details: 1 Api.0002 occurrence is generated Razor output for `Task.WhenAll(packagesTask, modulesTask)` at `ModulePackages.razor:145`; fix the source call to avoid overload ambiguity rather than editing `obj`. The 47 Api.0003 occurrences are potential behavior changes, concentrated in URI escaping, HTTP content reads, exception handling, and memory streams. Source files with findings include `Services\MulseApiClient.cs`, `Services\IMulseApiClient.cs`, `Program.cs`, `Components\Pages\Designer.razor.cs`, `Components\Pages\Designer.razor.Sample.cs`, and `Models\XmlMappingGraph.cs`; the remaining incidents are generated artifacts.
- No UI-specific test project is listed, and Visual Studio Test Explorer did not discover tests for `Service.Tests`; the test project is handled in the following subtask. No explicit package changes are planned for the UI project.

## Validation
Build `Mulse.Ui` against `net11.0`, confirm the project reference resolves to the new target, and run any relevant UI tests available before proceeding to the test-project subtask.
