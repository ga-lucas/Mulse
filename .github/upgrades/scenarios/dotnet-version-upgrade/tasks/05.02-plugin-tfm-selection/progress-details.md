# 05.02 Plugin TFM selection progress

## Changes
- Updated `ModuleCatalog` auto-discovery to prefer a plugin assembly under the host's target framework directory, falling back to the existing package-root or flat DLL layouts.
- Added managed-package path resolution for the configured `<plugin-root>/<package>/<package>.dll` convention. It loads a matching target-framework assembly when present without changing the saved path, and leaves unrelated user-provided paths unchanged.
- Added lifecycle tests for TFM preference plus legacy fallback, persisted managed paths across synchronization, and custom paths with adjacent TFM-looking directories.
- Repaired the upgrade plan/task registry to retain the remaining Service compatibility subtasks and the final library consolidation/validation tasks; the state reconciler had previously dropped them from the visible task list.

## Validation
- `dotnet build Service\Service.csproj`: succeeded with 0 warnings and 0 errors before the final test run; `dotnet test` subsequently rebuilt Service successfully.
- `dotnet test Service.Tests\Service.Tests.csproj --filter FullyQualifiedName~ModulePackageLifecycleTests`: 15 passed, 0 failed, 0 skipped.

## Notes
The first targeted run exposed that `AppContext.TargetFrameworkName` did not resolve the expected plugin folder in this test host. The resolver now derives the TFM from the Service assembly's `TargetFrameworkAttribute`; the targeted suite passes with that source of truth.
