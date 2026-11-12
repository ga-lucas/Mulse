# 03.03-tests: Retarget Service.Tests and fix .NET 11 test API issues

## Objective
Retarget `Service.Tests` to `net11.0` alongside the `Mulse.Ui` upgrade and preserve its references to still-`net10.0` projects until their planned upgrades.

## Scope
- Project: `Service.Tests\Service.Tests.csproj` and affected test source files.
- Resolve source-incompatible and behavioral API findings from the assessment; generated files under `obj` must be regenerated rather than edited.
- Retain xUnit 2.9.3 unless restore/build evidence establishes a target-framework incompatibility; the assessment marks it deprecated but provides no newer recommended version.

## Research Findings
- `Service.Tests.csproj` is SDK-style and currently targets `net10.0`; its direct packages are coverlet.collector 10.1.0, Microsoft.NET.Test.Sdk 18.11.0, xUnit 2.9.3, and xunit.runner.visualstudio 4.0.1. No central package management or imported targets were found. xUnit is deprecated in the assessment, but no replacement version is recommended.
- The test project references `Mulse.Ui` (now `net11.0`) plus `Service`, `Mulse.Excel`, `Mulse.SamplePlugin`, `Mulse.Hl7`, and `Mulse.Billing` (still `net10.0`). Retargeting the test project is required to restore the UI reference; the remaining library references must continue to resolve during this phase.
- The assessment reports 530 findings: 1 mandatory TFM change, 254 potential source-incompatible API incidents, 274 potential behavioral incidents, and 1 optional deprecated-package notice across 92 files (including generated `obj` output). Several source files have concentrated findings, including `XmlMappingUiTests.cs`, `RuntimeEncryptionTests.cs`, `OdxControlFlowApiTests.cs`, and `FlowMigrationReadinessTests.cs`; generated OpenAPI output must be regenerated, not edited.
- The assessment has no direct package upgrade recommendation for xUnit. Other package recommendations are transitive and are owned by referenced production projects; do not make unrelated package changes here without restore/build evidence.
- Visual Studio Test Explorer currently discovers 2,079 tests in this project (state not yet run). The test suite is the behavioral validation for this task; no tests have been executed in this task yet.

## Validation
Build the test project for `net11.0`, run the complete `Service.Tests` suite against the upgraded UI and compatible existing .NET 10 references, and record any remaining blocker with concrete failing tests or diagnostics.
