# 03.03.01-retarget-compile: Retarget Service.Tests and resolve .NET 11 source incompatibilities

## Objective
Retarget `Service.Tests` to `net11.0` and resolve compile-time source incompatibilities identified by the .NET 11 assessment.

## Scope
- Update `Service.Tests\Service.Tests.csproj`; retain xUnit 2.9.3 unless restore/build evidence proves it incompatible.
- Review and fix compile failures in test source files; regenerate generated output under `obj` rather than editing it.
- Preserve project references to `Mulse.Ui` (now net11.0) and the existing net10.0 libraries.

## Research Context
- Confirmed the SDK-style test project explicitly targets net10.0, enables nullable and implicit usings, and directly references coverlet.collector 10.1.0, Microsoft.NET.Test.Sdk 18.11.0, xUnit 2.9.3, and xunit.runner.visualstudio 4.0.1. No central package version was found; retain xUnit because the assessment has no replacement recommendation.
- Project references are `Service`, `Mulse.Ui`, `Mulse.Excel`, `Mulse.SamplePlugin`, `Mulse.Billing`, and `Mulse.Hl7`. `Mulse.Ui` now targets net11.0; the other five still target net10.0 and should remain consumable by this net11.0 test project.
- There are 146 test source files. Test Explorer discovered 2,079 tests (all currently unrun). The project assessment reports 254 potential source-incompatible incidents and 274 potential behavioral incidents across 92 files; generated findings under `obj` must be regenerated rather than edited.
- The largest source-issue examples include `XmlMappingUiTests.cs`, `NativePolicyMigrationTests.cs`, and `MultiSourceJoinMapAugmentModuleTests.cs`. Use restore/build diagnostics to identify actionable source changes rather than attempting blanket edits.

## Validation
Build `Service.Tests` against net11.0 with restore. Confirm the UI project reference resolves to net11.0 and all remaining project references resolve; fix all warnings in this project before completing.
