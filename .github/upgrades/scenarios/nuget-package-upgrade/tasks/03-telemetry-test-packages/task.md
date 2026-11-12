# 03-telemetry-test-packages: Upgrade telemetry and test dependencies

In `Mulse.ServiceDefaults`, update OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Extensions.Hosting, and OpenTelemetry.Instrumentation.Runtime from 1.20.1 to 1.21.0. In `Service.Tests`, update Microsoft.NET.Test.Sdk from 18.11.0 to 18.12.0. Assessment API diffs reported no source-breaking changes.

## Research Findings
- All four references are project-local. ServiceDefaults already uses the 1.21.0 AspNetCore and Http instrumentation packages, so this aligns the other three OpenTelemetry packages to that version.
- Service.Tests uses xUnit and the Visual Studio test runner; upgrade the test SDK without changing the existing runner or collector. Validate discovery and execution with the complete existing suite.
- The quick assessment reports no source-breaking changes; verify package resolution and build the whole solution after editing both projects.

**Done when**: References resolve to the selected versions, the solution builds without warnings, and the complete test suite passes.
