# 05.03-endpoint-compatibility: Verify endpoint and middleware compatibility on .NET 11

## Objective
Review the Service endpoint and middleware compatibility findings against actual .NET 11 behavior and preserve caller-visible API semantics.

## Scope
- Source findings in `Service/Endpoints/` and `Service/Middleware/`, including `BinaryData`, Minimal API response metadata, exception handling, and `Uri` behavior.
- Related endpoint and HTTP tests in `Service.Tests`. Generated OpenAPI source under `obj` is not editable.

## Steps
1. Use the per-file assessment entries as leads; distinguish generated-code and analyzer detections from source incompatibilities.
2. Validate real behavior changes in endpoint response status/body metadata, exceptions, and URI handling on .NET 11.
3. Apply only fixes supported by a reproduced compile/runtime regression, adding or updating focused tests with the production change.
4. Build Service and Service.Tests and run the focused endpoint/HTTP tests.

## Done when
Endpoint and middleware code has no verified unresolved .NET 11 regressions, generated files remain untouched, focused tests pass, and the affected projects build cleanly.
