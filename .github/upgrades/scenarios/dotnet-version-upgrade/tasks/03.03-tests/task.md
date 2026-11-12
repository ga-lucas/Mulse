# 03.03-tests: Retarget Service.Tests and fix .NET 11 test API issues

## Objective
Retarget `Service.Tests` to `net11.0` alongside the `Mulse.Ui` upgrade and preserve its references to still-`net10.0` projects until their planned upgrades.

## Scope
- Project: `Service.Tests\Service.Tests.csproj` and affected test source files.
- Resolve source-incompatible and behavioral API findings from the assessment; generated files under `obj` must be regenerated rather than edited.
- Retain xUnit 2.9.3 unless restore/build evidence establishes a target-framework incompatibility; the assessment marks it deprecated but provides no newer recommended version.

## Validation
Build the test project for `net11.0`, run the complete `Service.Tests` suite against the upgraded UI and compatible existing .NET 10 references, and record any remaining blocker with concrete failing tests or diagnostics.
