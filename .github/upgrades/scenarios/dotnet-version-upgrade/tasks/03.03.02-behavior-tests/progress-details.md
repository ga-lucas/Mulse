# Progress: 03.03.02-behavior-tests

## Changes
- Updated `Mulse.Modules/BuiltIn/Augment/DurableBatchSchedule.cs` so `ReadTime` enforces exact ASCII `HH:mm` input and the valid 00:00-23:59 range before accepting a time. This rejects `24:00` consistently across .NET 10 and .NET 11 while preserving the existing `ArgumentException` behavior.
- Guarded the .NET 11 Blazor JS interop call sites that produced 17 BL0016 warnings in `Mulse.Ui`. Expected circuit disconnects are handled as teardown; actionable initialization/download errors are surfaced through existing UI error handling or propagated with context.

## Validation
- Focused durable-batch tests: 16 passed, 0 failed.
- Full `Service.Tests` suite: 2,097 passed, 0 failed, 0 skipped.
- `dotnet build Mulse.Modules\\Mulse.Modules.csproj -t:Rebuild`: succeeded with 0 warnings and 0 errors.
- `dotnet build Mulse.Ui\\Mulse.Ui.csproj -t:Rebuild`: succeeded with 0 warnings and 0 errors; all 17 BL0016 warnings are resolved.

## Issues and deviations
- Visual Studio Test Explorer had previously reported stale framework evaluation for the multi-targeted `Mulse.ServiceDefaults` reference. CLI build/test commands were used for reliable validation.
- The UI analyzer warning cleanup was included because `Mulse.Ui` is part of this active .NET 11 upgrade and must build warning-free.
