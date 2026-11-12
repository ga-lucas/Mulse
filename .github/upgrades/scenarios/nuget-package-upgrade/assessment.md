# NuGet package upgrade assessment

_Mode: **quick assessment** — package API diffs only; no per-project source scan was run._

## Recommended versions

- **Microsoft.Extensions.DependencyInjection**: **11.0.0** (unified across 1 project(s)).
- **Microsoft.Extensions.Http**: **11.0.0** (unified across 1 project(s)).
- **Microsoft.NET.Test.Sdk**: **18.12.0** (unified across 1 project(s)).
- **NPOI**: **2.8.1** (unified across 1 project(s)).
- **OpenTelemetry.Exporter.OpenTelemetryProtocol**: **1.21.0** (unified across 1 project(s)).
- **OpenTelemetry.Extensions.Hosting**: **1.21.0** (unified across 1 project(s)).
- **OpenTelemetry.Instrumentation.Runtime**: **1.21.0** (unified across 1 project(s)).
- **SixLabors.ImageSharp**: **4.1.3** (unified across 1 project(s)).
- **System.Memory.Data**: **11.0.0** (unified across 1 project(s)).
- **System.Security.Cryptography.Pkcs**: **11.0.0** (unified across 1 project(s)).
- **System.Security.Cryptography.Xml**: **11.0.0** (unified across 1 project(s)).

## Public API changes

> **Types moved (namespace changed) are not removals.** A moved type keeps its name and members;
> the fix is a `using`-directive change, not a rewrite. Do not treat a moved type as deleted.

- **Microsoft.Extensions.DependencyInjection**: no source-breaking public API changes detected — see [`apidiff/Microsoft.Extensions.DependencyInjection.apidiff.md`](apidiff/Microsoft.Extensions.DependencyInjection.apidiff.md).
- **Microsoft.Extensions.Http**: no source-breaking public API changes detected — see [`apidiff/Microsoft.Extensions.Http.apidiff.md`](apidiff/Microsoft.Extensions.Http.apidiff.md).
- **System.Memory.Data**: no source-breaking public API changes detected — see [`apidiff/System.Memory.Data.apidiff.md`](apidiff/System.Memory.Data.apidiff.md).
- **OpenTelemetry.Exporter.OpenTelemetryProtocol**: no source-breaking public API changes detected — see [`apidiff/OpenTelemetry.Exporter.OpenTelemetryProtocol.apidiff.md`](apidiff/OpenTelemetry.Exporter.OpenTelemetryProtocol.apidiff.md).
- **OpenTelemetry.Extensions.Hosting**: no source-breaking public API changes detected — see [`apidiff/OpenTelemetry.Extensions.Hosting.apidiff.md`](apidiff/OpenTelemetry.Extensions.Hosting.apidiff.md).
- **OpenTelemetry.Instrumentation.Runtime**: no source-breaking public API changes detected — see [`apidiff/OpenTelemetry.Instrumentation.Runtime.apidiff.md`](apidiff/OpenTelemetry.Instrumentation.Runtime.apidiff.md).
- **System.Security.Cryptography.Xml**: no source-breaking public API changes detected — see [`apidiff/System.Security.Cryptography.Xml.apidiff.md`](apidiff/System.Security.Cryptography.Xml.apidiff.md).
- **NPOI**: 44 type(s) removed, 18 type(s) moved (namespace changed), 159 member(s) removed, 146 signature(s) changed — see [`apidiff/NPOI.apidiff.md`](apidiff/NPOI.apidiff.md).
- **SixLabors.ImageSharp**: 22 type(s) removed, 28 type(s) moved (namespace changed), 149 member(s) removed, 485 signature(s) changed — see [`apidiff/SixLabors.ImageSharp.apidiff.md`](apidiff/SixLabors.ImageSharp.apidiff.md).
- **Microsoft.NET.Test.Sdk**: no source-breaking public API changes detected — see [`apidiff/Microsoft.NET.Test.Sdk.apidiff.md`](apidiff/Microsoft.NET.Test.Sdk.apidiff.md).
- **System.Security.Cryptography.Pkcs**: no source-breaking public API changes detected — see [`apidiff/System.Security.Cryptography.Pkcs.apidiff.md`](apidiff/System.Security.Cryptography.Pkcs.apidiff.md).

## Breaking-change findings

- Version divergence findings (Pkg.0003): 0
- Requested-version-unsupported findings (Pkg.0002): 0

- Quick mode does not scan source, so there are no per-line `PkgApi` usage findings. Review the
  per-package API diffs above and rely on build errors during execution to pinpoint affected code.
- A full code scan can locate the exact source location of every breaking-change usage across the repo.
  It is opt-in and slower — re-run the assessment with `fullScan=true` only if the user requests it.

## Next steps

1. Proceed to planning to triage the API changes above and plan the code fixes (if any).

