# Mulse

Mulse is a code-first, extensible integration platform for building data pipelines ("flows") that fetch,
parse, transform, and deliver data across systems - conceptually similar to BizTalk Server, but built on
.NET 10 with a modular, hot-loadable plugin architecture and a browser-based visual designer.

Mulse also includes an importer that can analyze real BizTalk Server solutions (`.btproj`/`.odx`/`.btm`/`.btp`)
and turn them into draft Mulse flows, auto-translating what can be translated safely and clearly flagging
what needs human review - to make BizTalk migrations less painful.

## Core concepts

A **flow** is a pipeline made of ordered steps:

1. **Fetch** - pull data in from one or more sources (file system, SFTP, HTTP inbound, SQL lookup, document
   repository, file watcher, etc.). Multiple fetch steps can run and be combined/joined.
2. **Parse** - turn raw bytes into a structured payload (JSON, XML/XSD, flat-file, HL7, envelope debatching).
3. **Augment** - orchestration-style processing: join/merge multiple sources, apply declarative decision
   rules (including regex-based conditions), promote metadata, route selection, stateful/correlated
   processing, debatching.
4. **Render** - turn the processed payload back into an output format (JSON, XML, flat-file, HL7, SOAP
   envelope, XSLT-based transforms).
5. **Deliver** - send the result somewhere (file system, HTTP, SFTP, SQL execute, SMTP, document repository) -
   optionally capturing a synchronous response for request/response (two-way) integrations.

Flows can also be **push-triggered** (e.g. an inbound HTTP request) instead of running on a fetch/schedule
loop, and can synchronously reply to the original caller for two-way (request/response) integrations.

## Solution layout

| Project | Purpose |
|---|---|
| `Mulse.Modules` | Core abstractions (`IFetchModule`, `IParseModule`, `IOrchestrationAugmentModule`, `IRenderModule`, `IDeliverModule`), the typed module SDK, the decision-rule engine, and the built-in module set. |
| `Service` | The ASP.NET Core API/runtime: flow execution engine (`FlowRuntime`), module catalog/plugin loader, flow CRUD + run endpoints, config/secret value store, BizTalk import service, and the hot-reloadable module system. |
| `Mulse.Ui` | Blazor Server admin UI: a canvas/SVG flow designer (supports dragging in a BizTalk project to seed a draft flow, or building one from scratch), a home/administrative page (enable/disable, run, view config gaps), and a config-values page. |
| `Mulse.CompatibilityPack` | Additional built-in modules that round out BizTalk-style migration scenarios (XSD/flat-file/HL7-adjacent parsing, XSLT/SOAP rendering, metadata promotion, route selection, SMTP, document repository, HTTP inbound reply). |
| `Mulse.Hl7` | Hot-loadable plugin providing HL7 parse/render modules. |
| `Mulse.Dbms.SqlServer` | Hot-loadable plugin providing SQL Server lookup (fetch) and execute (deliver) modules. |
| `Mulse.SamplePlugin` | Reference/example hot-loadable plugin project - the template to follow when authoring a new module package. |
| `Mulse.AppHost` | .NET Aspire orchestration host for local multi-project run/debug. |
| `Mulse.ServiceDefaults` | Shared Aspire service-defaults (telemetry, health checks, service discovery). |
| `Service.Tests` | xUnit test suite for the Service project (flow runtime, module catalog, BizTalk import/analyzers, endpoints, etc.). |

## Module extensibility

Modules implement one of the core interfaces in `Mulse.Modules` and are registered via an `IModuleInstaller`.
Two ways to ship a module:

- **Built-in**: referenced directly by `Service` (compiled into the host process). Simple, but can't be
  reloaded/unloaded at runtime.
- **Hot-loadable plugin**: built into `Service/plugins/<PackageName>/` (see `Mulse.SamplePlugin`,
  `Mulse.Hl7`, `Mulse.Dbms.SqlServer` for the pattern) and picked up automatically by `ModuleCatalog`'s
  plugin discovery at startup, using a collectible `AssemblyLoadContext`. These can be reloaded or unloaded
  live via the `/api/module-packages/{id}/reload` API without restarting the service.

## BizTalk migration

`Service/BizTalkImport` can point at a BizTalk `.sln`/`.btproj`/folder and:

- Enumerate orchestrations, maps, schemas, pipelines, and custom assemblies.
- Auto-translate simple orchestration `Decision` branches (including `Regex.IsMatch(...)` conditions) into
  declarative decision rules, and scaffold a starter C# module for anything too complex (correlations,
  convoys, transactions, loops) rather than guessing.
- Best-effort translate `.btm` maps into XSLT for direct field links/constants, and clearly call out
  functoid-involved links (with their functoid type IDs) that need manual review instead of silently
  guessing at functoid semantics.
- Extract promoted/written context properties from custom pipeline component source (e.g.
  `context.Promote(...)`/`context.Write(...)` calls) to pre-populate metadata-promotion routing.
- Produce a draft, disabled Mulse flow plus a list of settings/requirements and warnings that a user must
  review before enabling and running it.

## Running locally

Prerequisites: .NET 10 SDK.

```powershell
# Run the API/runtime service directly
dotnet run --project Service

# Run the Blazor admin/designer UI (separately, or via the Aspire AppHost below)
dotnet run --project Mulse.Ui

# Or run everything together via .NET Aspire
dotnet run --project Mulse.AppHost
```

Run the test suite:

```powershell
dotnet test Service.Tests
```

## Status

This is an in-development project (not yet deployed to production). Expect breaking changes as the
architecture evolves.
