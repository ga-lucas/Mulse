# 04.02-dependent-libraries Progress

## Changes
- Multi-targeted `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin` for `net11.0;net10.0`.
- Updated `System.Security.Cryptography.Pkcs` to `10.0.15` in `Mulse.Dbms.SqlServer` and `System.Security.Cryptography.Xml` to `10.0.15` in `Mulse.Excel`.
- Kept the five plugin projects' net10 assemblies at their existing `Service/plugins/<project>/` paths and placed net11 assemblies in `Service/plugins/<project>/net11.0/`, preventing cross-TFM overwrites. Updated the plan so task 05 adds matching-TFM selection and legacy-path fallback to `ModuleCatalog`.
- Fixed the nullable flow warning in `Mulse.CompatibilityPack/XmlMappingEngine.CustomFunctions.cs` by guarding and storing the optional body before validation and expansion.
- Updated the two generated-code test fixtures to compile and load against `net11.0`, matching the `Mulse.Modules` assembly referenced from the net11 test host.

## Validation
- All six library projects built for both target frameworks with zero warnings and zero errors using `dotnet build`.
- Confirmed the net10 `Service` project builds successfully; it reports five pre-existing warnings in `Service/Mapping/BizTalkNativeMapConverter.cs`, outside this task's source scope. The CompatibilityPack CS8604 warning is gone.
- Verified both legacy net10 and isolated net11 plugin DLLs exist for Billing, Dbms.SqlServer, Excel, HL7, and SamplePlugin.
- Both focused generated-code tests passed; the full `Service.Tests` suite passed: 2,097 passed, 0 failed, 0 skipped.
- The Visual Studio `run_build` tool still uses a stale project graph and reports net10 assets/compatibility errors for projects whose on-disk files and CLI restores include both TFMs. CLI builds and tests succeeded after restore; no project reload was performed.
- `git diff --check` reported no whitespace errors; Git emitted its existing line-ending conversion notice for the task markdown file.

## Issues resolved and follow-up
- The first full test run found two generated-code tests compiling a net11 `Mulse.Modules.dll` against a temporary net10 project, causing CS1705. The fixture target and expected output directory now use net11, and both focused tests plus the full suite pass.
- The net11 plugin layout is prepared but not selected by the current net10 Service host. Task 05 owns TFM-aware `ModuleCatalog` selection for net11 and fallback to persisted/legacy direct assembly paths.
