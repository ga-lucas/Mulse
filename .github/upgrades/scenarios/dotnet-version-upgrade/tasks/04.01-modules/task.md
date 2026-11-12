# 04.01-modules: Multi-target Mulse.Modules and resolve its package/API findings

## Objective
Multi-target the leaf project `Mulse.Modules` for `net11.0;net10.0`, preserving its existing net10 behavior while enabling its net11 dependents.

## Scope
- Update `Mulse.Modules/Mulse.Modules.csproj` and only the source files with verified issues.
- Apply the assessment's direct package recommendations: Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Http, Microsoft.Extensions.Logging.Abstractions, and System.Memory.Data from 10.0.14 to 10.0.15.
- Investigate the 109 potential source-incompatible and 56 behavioral findings, especially repeated `System.BinaryData` APIs; make changes only for reproduced build or test failures, preserving runtime semantics.
- Keep `Service` and the six current net10 consumers buildable against the net10 target.

## Research context
- The project is SDK-style, currently targets only `net10.0`, has no project references, and defines its four package versions directly in `Mulse.Modules.csproj` (no CPM/imported package files).
- Assessment reports 170 total findings: 4 package recommendations, one mandatory TFM finding, 109 source-incompatible findings, and 56 behavioral findings. The API findings are potential; `BinaryData` dominates the source findings, while many `NotSupportedException` entries reflect intentional validation/error paths.
- Source search found 33 `BinaryData` usages across 23 C# files; assessment's affected-file list includes `Triggers/IHttpInboundRegistry.cs`, payload helpers, and workflow/control-flow modules. Do not mechanically replace these APIs: first build for net11 and respond only to real compiler/runtime evidence.
- The four direct package references are FluentFTP 55.1.0, Microsoft.Extensions.DependencyInjection 10.0.14, Microsoft.Extensions.Http 10.0.14, Microsoft.Extensions.Logging.Abstractions 10.0.14, SSH.NET 2026.0.0, and System.Memory.Data 10.0.14; only the four Microsoft/System packages listed above are recommended for patch updates.
- `Mulse.Modules` is the leaf for six libraries and is also consumed by the still-net10 `Service`; `Service.Tests` is net11 and its 2,097 tests passed after the prior regression fix. No `// STUB:` markers exist in this project.
- After multi-targeting and package updates, `dotnet build Mulse.Modules.csproj` compiled both net11.0 and net10.0 with 0 warnings/errors. The reported BinaryData source findings did not require source edits.
- `dotnet build Service.csproj` succeeded on net10.0, confirming the existing consumer remains compatible. It reported one existing CS8604 warning in CompatibilityPack and five CS8619/CS1573 warnings in Service; neither project is part of this subtask. All 16 focused durable-batch cases passed.

## Steps
1. Read the Mulse.Modules assessment details and the exact code around issue sites; inspect current package references and existing test coverage.
2. Add `net11.0;net10.0` target frameworks (newest first) and apply package updates.
3. Build the project for both target frameworks; resolve actual compiler diagnostics and warnings without suppressions.
4. Build the current net10 consumer(s) and run relevant regression tests, including the durable-batch cases if affected.
5. Record validation and any potential findings that were false positives.

## Done when
- `Mulse.Modules` builds for net11.0 and net10.0 without warnings.
- At least the existing net10 `Service` consumer builds successfully against the net10 target.
- Verified API changes preserve behavior and relevant regression tests pass.
