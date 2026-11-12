# 03.01-service-defaults Progress

- Changed `Mulse.ServiceDefaults` to target `net11.0;net10.0`, preserving .NET 10 compatibility and making .NET 11 the default target.
- Updated `Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.ServiceDiscovery` to 11.0.0 only for `net11.0`; retained 10.11.0 for `net10.0`. Updated `OpenTelemetry.Instrumentation.AspNetCore` and `OpenTelemetry.Instrumentation.Http` to 1.21.0.
- `dotnet build Mulse.ServiceDefaults\Mulse.ServiceDefaults.csproj` succeeded with 0 warnings and 0 errors and produced assemblies for both TFMs. Per-TFM package listing confirmed the expected versions and both output assemblies exist.
- Full `dotnet build Mulse.slnx` succeeded with 0 errors. It reported existing warnings in `Mulse.CompatibilityPack`, `Service`, and `Service.Tests`; `Mulse.ServiceDefaults` emitted no warnings.
- The Visual Studio `run_build` tool twice reported NU1201/NETSDK1005 for dependents on the old TFM despite the on-disk multi-target declaration. The CLI restore/build resolved both TFMs and the full solution successfully; no project reload was performed.
- Tests were not run in this library-preparation subtask; UI and test validation is scheduled in the subsequent dependency-ordered subtasks.
