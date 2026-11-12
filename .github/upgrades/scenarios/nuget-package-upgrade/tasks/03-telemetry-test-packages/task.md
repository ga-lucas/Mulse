# 03-telemetry-test-packages: Upgrade telemetry and test dependencies

In `Mulse.ServiceDefaults`, update OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Extensions.Hosting, and OpenTelemetry.Instrumentation.Runtime from 1.20.1 to 1.21.0. In `Service.Tests`, update Microsoft.NET.Test.Sdk from 18.11.0 to 18.12.0. Assessment API diffs reported no source-breaking changes.

**Done when**: References resolve to the selected versions, the solution builds without warnings, and the complete test suite passes.
