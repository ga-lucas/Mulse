# 05.04-biztalk-mapping-flow-compatibility: Verify BizTalk, mapping, and flow compatibility on .NET 11

## Objective
Review the remaining Service compatibility findings in BizTalk import, mapping, and flow execution, and fix only verified .NET 11 regressions.

## Scope
- `Service/BizTalkImport/`, `Service/Mapping/`, `Service/Execution/`, and `Service/Flows/` findings, including `BinaryData`, cryptography, `Uri`, JSON, and `NotSupportedException` usage.
- Relevant BizTalk, mapping, backup, and flow tests in `Service.Tests`.

## Steps
1. Check assessment findings against source and the actual .NET 11 runtime; don't mechanically rewrite APIs solely because the scanner lists them.
2. Reproduce any behavioral difference with existing or focused tests before changing production behavior.
3. Fix verified issues, retain intended exception and serialization semantics, and add/adjust focused regression tests where needed.
4. Run the relevant test groups and rebuild Service and Service.Tests.

## Done when
No verified unresolved .NET 11 compatibility regressions remain in the scoped areas, the related tests pass, and both affected projects build without warnings.
