# 01-prerequisites: Verify .NET 11 toolchain

Confirm the .NET 11 SDK is installed and that repository SDK selection via `global.json`, if present, is compatible with `net11.0`. This is a solution-wide prerequisite for the application-first upgrade.

**Done when**: The selected SDK can build the solution and any required `global.json` change is recorded.

## Research
- The assessment covers 12 SDK-style projects, all currently targeting `net10.0`; the selected target is `net11.0`.
- The .NET 11 SDK is installed and compatible. The solution repository contains no `global.json`, so no SDK pin needs changing.
- No source or project edits are needed for this prerequisite; a full solution build will verify the installed toolchain against the current solution.
