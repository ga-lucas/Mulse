# 05.02-plugin-tfm-selection: Select matching plugin assemblies for the host TFM

## Objective
Make plugin discovery and persisted managed-package loading select each plugin's `net11.0` output for the upgraded Service host while retaining fallback for existing legacy paths.

## Scope
- `Service/Modules/ModuleCatalog.cs`, especially `DiscoverAutoPackages` and the managed-package synchronization path.
- `Service.Tests/ModulePackageLifecycleTests.cs` for regression coverage.
- Plugin projects from task 04.02 emit .NET 11 assemblies under `plugins/<package>/net11.0/` and keep legacy .NET 10 assemblies at `plugins/<package>/`.

## Research Findings
- `DiscoverAutoPackages` currently scans immediate package directories for `<packageId>.dll` and separately scans flat DLLs. It will miss the new `plugins/<package>/net11.0/<package>.dll` output and instead treat `net11.0` as a package directory.
- `SynchronizeAsync` directly loads persisted `ManagedModulePackageDefinition.AssemblyPath`; `RuntimeConfigurationStore` clones that value unchanged. Resolve a target-specific assembly only when the stored path matches the configured plugin-root/package/package.dll convention, and do not rewrite the persisted path or unrelated caller-supplied paths.
- Existing `DiscoveryDuringExecution_ListsOnlyCompatiblePackagesAsUnloaded` coverage verifies both legacy package-directory and flat-DLL discovery. `Service.Tests` targets `net11.0` and references Service plus the plugin projects, so matching-TFM selection and fallback can be tested with the test assembly as a valid installer fixture.

## Steps
1. Preserve current discovery behavior for flat DLLs and legacy package directories, but prefer a matching `net11.0` assembly where the package's TFM-specific output exists.
2. Resolve persisted managed assembly paths using the matching TFM output when appropriate, without rewriting arbitrary user-supplied paths or breaking legacy configurations.
3. Add tests that distinguish the matching-TFM selection from legacy fallback, including persisted package paths.
4. Run the targeted plugin lifecycle tests and build both affected projects.

## Done when
Auto-discovery and persisted package state choose the `net11.0` assembly when present, legacy paths continue to work when it is absent, and the targeted lifecycle tests pass.
