# 05.02-plugin-tfm-selection: Select matching plugin assemblies for the host TFM

## Objective
Make plugin discovery and persisted managed-package loading select each plugin's `net11.0` output for the upgraded Service host while retaining fallback for existing legacy paths.

## Scope
- `Service/Modules/ModuleCatalog.cs`, especially `DiscoverAutoPackages` and the managed-package synchronization path.
- `Service.Tests/ModulePackageLifecycleTests.cs` for regression coverage.
- Plugin projects from task 04.02 emit .NET 11 assemblies under `plugins/<package>/net11.0/` and keep legacy .NET 10 assemblies at `plugins/<package>/`.

## Steps
1. Preserve current discovery behavior for flat DLLs and legacy package directories, but prefer a matching `net11.0` assembly where the package's TFM-specific output exists.
2. Resolve persisted managed assembly paths using the matching TFM output when appropriate, without rewriting arbitrary user-supplied paths or breaking legacy configurations.
3. Add tests that distinguish the matching-TFM selection from legacy fallback, including persisted package paths.
4. Run the targeted plugin lifecycle tests and build both affected projects.

## Done when
Auto-discovery and persisted package state choose the `net11.0` assembly when present, legacy paths continue to work when it is absent, and the targeted lifecycle tests pass.
