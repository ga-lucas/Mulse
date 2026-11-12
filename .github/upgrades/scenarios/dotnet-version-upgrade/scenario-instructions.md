# .NET Version Upgrade

## Preferences
- **Flow Mode**: Automatic
- **Target Framework**: net11.0
- **Scope**: Entire solution

## Source Control
- **Source Branch**: master
- **Working Branch**: upgrade-dotnet-11
- **Commit Strategy**: After Each Task
- **Branch Sync**: Auto (Merge)

## Upgrade Options

### Strategy
- Upgrade Strategy: Top-Down

### Compatibility
- Unsupported Packages: Resolve Inline (1 incompatible package)
- Unsupported API Handling: Fix Inline

## Strategy
**Selected**: Top-Down
**Rationale**: The solution has a four-level dependency graph and 12 projects, so staged application upgrades avoid changing every dependency in one atomic pass.

### Execution Constraints
- Upgrade entry-point applications first; add multi-targeting to shared libraries only when an upgraded consumer needs the new target.
- Keep multi-targeted libraries consumable by both net10.0 and net11.0 projects until all applications and their tests have moved to net11.0.
- Consolidate libraries to net11.0 only after all applications are upgraded; remove only target-framework-specific compilation branches.
- Validate each application task and its tests, then run a full solution build and test suite at the end.

## Build Tool Decisions
- **Mulse.ServiceDefaults.csproj**: `dotnet build` (SDK-style multi-target project; validates each TFM from disk).
- **Mulse.Ui.csproj**: `dotnet build` (SDK-style .NET 11 web project; targeted CLI build validates its project reference independently of not-yet-retargeted test consumers).
