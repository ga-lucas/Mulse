# 03.03.02-behavior-tests: Run the Service.Tests suite and resolve .NET 11 behavioral regressions

## Objective
Run the full `Service.Tests` suite on net11.0 and resolve failures caused by .NET 11 behavioral changes identified by the assessment.

## Scope
- Run all tests after `03.03.01-retarget-compile` succeeds.
- Investigate failures against the 274 potential behavioral findings; update production or test code only when evidence shows an actual regression or obsolete expectation.
- Do not edit generated files under `obj` or make unrelated test refactors.

## Research Context
- Visual Studio Test Explorer discovered 2,079 tests in `Service.Tests`; none have run in this task yet.
- Assessment behavior findings are potential and span 49 test-source files plus generated output. Prioritize concrete failing tests and changed runtime behavior rather than blanket edits.

## Validation
Run the complete `Service.Tests` suite and report passed, failed, and skipped counts. Verify any fixes with targeted tests and rerun the complete suite; record any remaining blocker with concrete diagnostics.
