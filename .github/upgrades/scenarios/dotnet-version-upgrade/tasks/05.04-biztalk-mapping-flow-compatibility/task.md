# 05.04-biztalk-mapping-flow-compatibility: Verify BizTalk, mapping, and flow compatibility on .NET 11

## Objective
Review the remaining Service compatibility findings in BizTalk import, mapping, and flow execution, and fix only verified .NET 11 regressions.

## Scope
- `Service/BizTalkImport/`, `Service/Mapping/`, `Service/Execution/`, and `Service/Flows/` findings, including `BinaryData`, cryptography, `Uri`, JSON, and `NotSupportedException` usage.
- Relevant BizTalk, mapping, backup, and flow tests in `Service.Tests`.

## Research Findings
- Confirmed the source scope contains 32 BizTalk import files, 7 Mapping files, 18 Execution files, and 12 Flows files. The assessment's API warnings are potential analyzer detections; no required replacements are named.
- Current paths include deliberate `NotSupportedException` handling for unsupported migration shapes, `BinaryData` snapshots, URI parsing/escaping, JSON serialization, and PBKDF2/AES-GCM workspace backup and runtime-state protection. Those behaviors must remain unchanged absent a reproduced .NET 11 difference.
- `WorkspaceBackupTests` covers encrypted backup round-trips, wrong passwords/tampering, malformed data, and no-mutation failures. `RuntimeEncryptionTests` covers key availability, encrypted persistence/restarts, failure and cancellation, and live endpoint behavior. Other focused suites include BizTalk import/analyzers, native flow/ODX, mappings, and flow runtime/state.
- Service and Service.Tests are SDK-style projects targeting `net11.0`; Service references Mulse.Modules, Mulse.CompatibilityPack, and Mulse.ServiceDefaults. Their latest builds passed with zero warnings and errors in the prior endpoint task; no package or project-file changes are in this task's scope.

## Steps
1. Check assessment findings against source and the actual .NET 11 runtime; don't mechanically rewrite APIs solely because the scanner lists them.
2. Reproduce any behavioral difference with existing or focused tests before changing production behavior.
3. Fix verified issues, retain intended exception and serialization semantics, and add/adjust focused regression tests where needed.
4. Run the relevant test groups and rebuild Service and Service.Tests.

## Done when
No verified unresolved .NET 11 compatibility regressions remain in the scoped areas, the related tests pass, and both affected projects build without warnings.
