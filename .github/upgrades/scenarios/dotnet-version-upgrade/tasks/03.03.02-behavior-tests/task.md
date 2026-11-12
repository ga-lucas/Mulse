# 03.03.02-behavior-tests: Run the Service.Tests suite and resolve .NET 11 behavioral regressions

## Objective
Run the full `Service.Tests` suite on net11.0 and resolve failures caused by .NET 11 behavioral changes identified by the assessment.

## Scope
- Run all tests after `03.03.01-retarget-compile` succeeds.
- Investigate failures against the 274 potential behavioral findings; update production or test code only when evidence shows an actual regression or obsolete expectation.
- Do not edit generated files under `obj` or make unrelated test refactors.

## Research Context
- Confirmed `Service.Tests` now targets `net11.0`; its `Mulse.Ui` reference also resolves to net11.0 while the project references to `Service` and the other libraries remain net10.0.
- Visual Studio Test Explorer discovers 2,079 tests in `Service.Tests`. The preceding compile subtask ran 100 tests across `NativeMessageFactTests`, `ReferencePickerUiTests`, and `XmlCustomFunctionUiTests`; all passed.
- The assessment's 274 potential behavioral incidents span 49 test-source files plus generated output. The 100 tests run in the preceding subtask passed; prioritize concrete full-suite failures and test evidence rather than blanket edits.
- The first complete CLI run executed 2,097 tests (2,095 passed, 2 failed, 0 skipped). Both failures expected `ArgumentException` for `24:00`: `MetadataDurableBatchTests.InvalidFirstMessageTimingNeverCreatesCheckpoint` and `TimedDurableBatchTests.InvalidClockCannotBeEnabledOrFetchInputs`.
- Both failures originate in `Mulse.Modules/BuiltIn/Augment/DurableBatchSchedule.cs`: `ReadTime` delegates validation to `TimeOnly.TryParseExact(text, "HH:mm", ...)`, which accepts `24:00` under the .NET 11 runtime. Explicitly enforce the application's stated 24-hour clock range (00:00 through 23:59) before accepting the value; preserve the existing exact format and error behavior.
- CLI `dotnet test` successfully builds and runs this SDK-style xUnit project. The Visual Studio test tool aborted because of stale framework evaluation in the multi-targeted `Mulse.ServiceDefaults` reference; use CLI for the full run. Existing warnings occurred in `Mulse.CompatibilityPack`, `Mulse.Ui`, and `Service`, not `Service.Tests`.
- Rebuilding the already-upgraded `Mulse.Ui` exposed 17 BL0016 warnings across its JS interop call sites. These were addressed with targeted catches that surface actionable failures through existing UI error handling or preserve exception propagation; the clean rebuild now reports 0 warnings.

## Validation
The two targeted durable-batch cases passed (16 parameterized tests); the complete suite then passed (2,097 passed, 0 failed, 0 skipped). `Mulse.Modules` and `Mulse.Ui` clean rebuilds both succeeded with 0 warnings.
