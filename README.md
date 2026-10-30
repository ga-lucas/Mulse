# Mulse

Mulse is an extensible integration platform for building data pipelines ("flows") that fetch, parse,
transform, and deliver data across systems. Author flows in a browser-based visual designer or through
the API, and extend them with .NET modules. It is conceptually similar to BizTalk Server, built on
.NET 10 with an ASP.NET Core runtime, Blazor Server administration, and hot-loadable plugin packages.

Mulse also includes an importer that can analyze real BizTalk Server solutions (`.btproj`/`.odx`/`.btm`/`.btp`)
and turn them into draft Mulse flows, auto-translating what can be translated safely and clearly flagging
what needs human review - to make BizTalk migrations less painful.

## Contents

- [Running locally](#running-locally)
- [Core concepts](#core-concepts)
- [Solution layout](#solution-layout)
- [Module extensibility](#module-extensibility)
- [Execution, triggers and retries](#execution-triggers-and-retries)
- [Stored values in module parameters](#stored-values-in-module-parameters)
- [Source acknowledgement and cleanup](#source-acknowledgement-and-cleanup)
- [File system delivery](#file-system-delivery)
- [Excel files](#excel-files)
- [BizTalk migration](#biztalk-migration)
- [UI workspace](#ui-workspace)
- [API reference](#api-reference)
- [Troubleshooting and limitations](#troubleshooting-and-limitations)

## Core concepts

A **flow** contains a source graph, an ordered augment chain, and delivery routes:

1. **Fetch** acquires raw or already-structured data from files, SFTP, inbound HTTP, a document repository,
   a file watcher, SQL lookup, or another flow.
2. **Parse** interprets one or more fetch outputs as JSON, XML/XSD, delimited/flat-file data, HL7 or Excel,
   depending on the selected module. Fetch and parse are separate nodes, not a fixed one-to-one pair.
3. **Augment** joins/maps sources, applies decisions, promotes metadata, selects routes, debatches
   documents, or suspends/resumes correlated work. Augments run in their configured order.
4. **Render** produces the destination representation. Each delivery route renders once.
5. **Deliver** sends that rendered batch to one or more actions in the route: files, HTTP, SQL commands,
   email, a document repository, logging, or an in-process flow link. There is currently SFTP **fetch**,
   but no shipped SFTP deliver module.

Fetch nodes optionally consume upstream **parse node IDs** through `inputParseIds`; parse nodes name
fetch nodes through `fetchNodeIds`. The combined graph is acyclic and executes in dependency order.
Node IDs must be unique across both lists, every fetch must be parsed, and each parse must reference at
least one fetch. This supports several same-format fetches feeding one parser, or a parsed order feeding
a dependent SQL lookup followed by its own parser. Each parser's output is tagged with `sourceId` equal
to that **parse node's ID**; merged parse outputs become the augment input.

`IntegrationBatch` holds payloads, each with a name, binary content, content type and string metadata.
Mapping/decision modules generally operate on normalized JSON. Transform modules are reusable scalar
operations inside field mappings, not separate top-level pipeline stages.

Flows can run on demand, on an interval, or from a push-capable source. Optional request/response modules
support synchronous replies; the platform is not an exactly-once message broker or a distributed
transaction coordinator.

Fetchers can opt into a final **source acknowledgement** after processing: success when every applicable
delivery succeeds (including zero deliveries), or failure after retries are exhausted. Source cleanup
happens at this boundary, not during Fetch. Notification retries do not resend deliveries; see
[Source acknowledgement and cleanup](#source-acknowledgement-and-cleanup).

## Solution layout

| Project | Purpose |
|---|---|
| `Mulse.Modules` | Core abstractions (`IFetchModule`, optional `IFetchOutcomeModule`, `IParseModule`, `IOrchestrationAugmentModule`, `IRenderModule`, `IDeliverModule`), the typed module SDK, the decision-rule engine, and the built-in module set. |
| `Service` | The ASP.NET Core API/runtime: flow execution engine (`FlowRuntime`), module catalog/plugin loader, flow CRUD + run endpoints, config/secret value store, BizTalk import service, and the hot-reloadable module system. |
| `Mulse.Ui` | Blazor Server dashboard, visual flow designer with sample-file drops and path-based BizTalk import, config values/encryption, module/package administration, and workspace backup/restore. |
| `Mulse.CompatibilityPack` | Additional built-in XML/XSD, flat-file and envelope parsing; XSLT/SOAP rendering; metadata promotion, route selection, SMTP, document repository, file watcher and HTTP inbound/reply modules. |
| `Mulse.Hl7` | Hot-loadable plugin providing HL7 parse/render modules. |
| `Mulse.Dbms.SqlServer` | Hot-loadable plugin providing SQL Server lookup (fetch) and execute (deliver) modules. |
| `Mulse.Excel` | Hot-loadable XLS/XLSX worksheet-to-JSON parser and JSON-to-workbook renderer; no installed Excel required. |
| `Mulse.SamplePlugin` | Reference/example hot-loadable plugin project - the template to follow when authoring a new module package. |
| `Mulse.AppHost` | .NET Aspire orchestration host for local multi-project run/debug. |
| `Mulse.ServiceDefaults` | Shared Aspire service-defaults (telemetry, health checks, service discovery). |
| `Service.Tests` | xUnit coverage for runtime/retries/source outcomes, persistence, API/UI interactions, module packages, sample analysis, Excel, and BizTalk migration; also contains the reference-input JavaScript test. |
| `Mulse.Ui.Tests` | Dependency-free Node.js tests for browser theme, floating panels, and file-drop behavior. |

## Module extensibility

Modules implement one of the core interfaces in `Mulse.Modules` and are registered via an `IModuleInstaller`.
Two ways to ship a module:

- **Built-in**: referenced directly by `Service` (compiled into the host process). Simple, but can't be
  reloaded/unloaded at runtime.
- **Hot-loadable plugin**: built into `Service\plugins\<PackageName>\` (see `Mulse.SamplePlugin`,
  `Mulse.Hl7`, `Mulse.Dbms.SqlServer`, and `Mulse.Excel` for the pattern) and discovered by `ModuleCatalog`
  at startup and every five seconds. New compatible packages appear **unloaded**; activate them from
  **Configuration > Module Packages**. Loading uses a collectible `AssemblyLoadContext` so packages can
  later be reloaded or unloaded without restarting the service, subject to dependency/active-use guards.

### Shipped module families

| Package | Modules and purpose | Availability |
|---|---|---|
| Core (`builtin.core`) | `file-system-fetch`, `sftp-fetch`, `flow-link-fetch`; `json-parse`; JSON envelope, decision, multi-source join/map, stateful orchestration and debatch augments; `json-render`, `xml-render`; `logging-deliver`, `file-system-deliver`, `http-deliver`, `flow-link-deliver`. | Built in. |
| Core transforms | Concat, add/subtract/multiply/divide, coalesce, substring, upper/lower case, trim, round and date formatting, selected within join/map fields. | Built in. |
| Compatibility (`builtin.compatibility`) | `document-repository-fetch`, `file-system-watcher-fetch`, `http-inbound-fetch`; `xml-xsd-parse`, `flat-file-parse`, `xml-envelope-debatch-parse`; metadata-promotion and route-selection augments; `flat-file-render`, `xslt-render`, `soap-envelope-render`; `document-repository-store`, `smtp-email-deliver`, `http-inbound-reply-deliver`. | Built in. |
| `Mulse.Dbms.SqlServer` | `sql-server-lookup-fetch` uses upstream parsed IDs for a parameterized lookup; `sql-server-execute-deliver` executes SQL text/stored procedures with JSON-to-parameter binding per payload. | Build and load plugin. |
| `Mulse.Hl7` | `hl7-parse` and `hl7-render` for HL7 v2 ER7 text/normalized JSON. | Build and load plugin. |
| `Mulse.Excel` | `excel-parse` and `excel-render` for XLS/XLSX worksheets. | Build and load plugin. |
| `Mulse.SamplePlugin` | Reference raw/typed modules illustrating custom fetch, parse, augment, render and deliver implementations. | Build and load plugin; examples, not required for normal flows. |

The **Modules** page and `GET /api/modules` expose the loaded catalog, setting descriptors, defaults,
formats/protocols/capabilities and package ownership. They are the reference for each module's accepted
settings; unloaded packages do not provide their module catalog until loaded.

For custom modules, reference `Mulse.Modules`, implement the relevant contract, and register it in an
`IModuleInstaller` using `AddFetch`, `AddParse`, `AddOrchestrationAugment`, `AddRender`, `AddDeliver` or
`AddTransform`. Optional SDK base classes (`TypedFetchModule<TOut>`, `TypedParseModule<TOut>`,
`TypedAugmentModule<TIn,TOut>`, `TypedRenderModule<TIn>`, `TypedDeliverModule<TIn>`) handle the structured
JSON payload convention. Publish all plugin dependencies beside its DLL and keep SDK/host contracts
compatible. Modules execute with the Service process's permissions; loading is not sandboxing.

## Execution, triggers and retries

### Trigger modes

| Mode | Behavior |
|---|---|
| `OnDemand` | Runs when an enabled flow is invoked from the dashboard or `POST /api/flows/{flowId}/run`. |
| `Interval` | Polls on a positive `trigger.interval` such as `00:05:00`; scheduler checks every five seconds. `runOnStartup` controls the initial interval run, not all trigger modes. |
| `Push` | Registers the first fetch node implementing `IPushTriggeredFetchModule`; other fetches still execute normally. Registrations reconcile about every five seconds. |

Manual Run also works for enabled Interval/Push flows. It does not enable a disabled flow. Execution is
serialized per flow inside one Service instance; this is not a cross-process lock. Interval scheduling
is in-memory and recalculated after restart rather than a durable calendar scheduler.

**The flow owns scheduling; fetch modules own acquisition.** `sftp-fetch` scans once per execution;
it has no internal timer and cannot initiate Push runs. Set the flow to **Interval** for repeated SFTP
polling, or **OnDemand** for manual scans. `maxFiles` limits files per execution, not frequency. An SFTP
fetch can participate in a Push flow triggered by another fetch, but it does not watch the server itself.

For `file-system-watcher-fetch`, save and enable a **Push** flow. The push registration creates the
watcher before the first execution, so no manual bootstrap run is needed. Settled file events (and
existing files when `includeExistingOnFirstRun` is enabled) signal the flow; fetching then drains queued
files rather than rescanning the directory. In OnDemand/Interval modes, the watcher is created only
on the first execution; later executions drain its events, and the settle window can make the first
run empty. Prefer **file-system-fetch + Interval** for periodic directory scans. Only the **first** push-capable
fetch in a flow registers as its event source; multiple watchers are not independent triggers. Other
fetches still execute as part of the source graph. The designer identifies this first source and warns
when Push has no compatible loaded fetch. `/api/modules` exposes `supportsPushTrigger`, derived from
`IPushTriggeredFetchModule` rather than a module's informational `Polling` recommendation label.

For HTTP ingress, select `http-inbound-fetch`, configure `route`, save and enable a **Push** flow, and
allow registration to complete. `POST`/`PUT /api/inbound/{route}` queues requests and returns `202` (not a
promise that downstream processing succeeded). `?awaitResponseSeconds=N`, capped at 120 seconds, waits
for a paired `http-inbound-reply-deliver`; no reply within that window returns `504`. These are hosted
HTTP endpoints, not a full WCF runtime. Outbound `http-deliver` can capture replies when its deliver
settings include `"expectsResponse": "true"`; captured payloads are included in the run API response.

`flow-link-deliver` and `flow-link-fetch` share a named `channel` within the same process. They provide
an in-memory hand-off, not durable messaging or broadcast delivery: consumers drain a shared queue.
Inbound HTTP queues and watcher queues are also process-local; pending retry/checkpoint persistence
does not make queued-but-not-yet-fetched input durable.

### Delivery behavior and retry policy

Each route renders once, then executes its deliver actions sequentially. Without an `atomicScope`, a
permanent **deliver action** failure is recorded while other actions continue; the final result may be
`Completed` with failure details. A render failure without an atomic scope terminates processing.
Always inspect `outcome` **and** `detail`, not just the HTTP status or `Completed` label.

An optional shared `atomicScope` compensates already committed deliveries in reverse order when that
scope fails, skips its remaining actions, and lets unrelated routes proceed before reporting failure.
Configure an explicit `compensation` deliver step per action through the API. Missing/failed
compensation is logged; it is best effort, not an actual rollback of arbitrary external side effects.

Steps inherit the flow retry policy unless they have an override; no policy means one attempt.
`maxAttempts` includes the first attempt; `null` means unlimited. Backoff is `Fixed`, `Linear` or
`Exponential`, with an optional maximum delay. For API create/update requests, use seconds:

```json
{
  "retry": {
    "maxAttempts": 3,
    "delaySeconds": 5,
    "backoff": "Exponential",
    "maxDelaySeconds": 60
  }
}
```

This is a request fragment, not a complete flow. Persisted/runtime definitions instead use TimeSpan
fields `delay` and `maxDelay`; do not copy the state file directly into an API request. The designer
exposes the flow default; the API supports per-step overrides and compensation configuration.

Retryable failures persist the failed stage/input and return `Retrying` with `nextAttemptAt`. The retry
driver checks every two seconds and resumes from that stage, preserving prior resolved source batches
and committed delivery state. Ordinary processing retries use the currently saved flow definition;
avoid editing an executing/retrying flow. A stage can partially affect an external system before it
throws, so retries can repeat those side effects: design destination operations for idempotency.

`Suspended` means a stateful augment stored a checkpoint for a correlated follow-up. Configure
`stateful-orchestration-augment` with `ResumeOrWait`, `Suspend` or `Complete`, a correlation source/key,
and bookmark as appropriate. Completion is explicitly marked by `Complete`; resuming alone does not
remove the checkpoint. This is focused persisted correlation, not general-purpose durable orchestration.
A crash during ordinary processing is not automatically recoverable unless an appropriate retry or
checkpoint snapshot was persisted. Source-notification recovery has its own durable continuation,
described below.

## Stored values in module parameters

Add values under **Configuration > Config values**, then reference them in any module setting with
`{{config:reference}}` or `{{secret:reference}}`. Tokens can fill the whole parameter or be embedded,
including multiple tokens, for example:

```text
Server={{config:db-host}};Database={{config:db-name}};User ID={{config:db-user}};Password={{secret:db-password}}
```

References resolve at execution time in fetch, parse, augment, render, deliver and compensation settings.
The saved flow retains its tokens. Missing references are reported in configuration usages and block
execution; repeated uses of the same reference within a setting appear once in the usage list.
Replacement is single-pass and literal: stored values containing `$`, backslashes or token syntax are
not reinterpreted or recursively expanded. No SQL, JSON, URL or connection-string escaping is performed;
ensure the resulting parameter is valid (store a complete connection string when its values need special
quoting). Reference names cannot contain braces or line breaks. `secret` and `config` share the same
store; the prefix alone does not control encryption or masking.

The flow designer suggests stored reference names after `{{`, including tokens embedded in longer
values and multiline parameters. Type to filter; use Up/Down and Enter/Tab, or click a suggestion.
Escape dismisses the list. Only reference names are fetched for suggestions, never their values.
Boolean/select settings retain their normal controls and offer **Use config/secret reference**.
Numeric parameters also accept reference tokens. Refresh the designer catalog to pick up newly added
references. Password module settings have a Show/Hide button. On **Config values**, all value-entry
fields start masked; **Show config/secret values** reveals them regardless of encryption state.
Masking is visual only: the configuration API and page still have access to the actual values.

### Runtime state encryption

On **Configuration > Config values**, the encryption switch is available only when the Service has a
valid base64-encoded AES key in its environment (16, 24 or 32 decoded bytes). The default variable is
`MULSE_STATE_ENCRYPTION_KEY`; its name can be changed through
`Mulse:Encryption:KeyEnvironmentVariable`. Generate a 256-bit key once in PowerShell:

```powershell
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Store that key securely outside source control and make the environment variable available to the
Service process before starting it (restart Visual Studio/AppHost if they need to inherit a new
variable). Do not generate a different key on each launch. The UI never accepts, returns or stores the
key. A legacy `Mulse:Encryption:KeyBase64` setting alone does not enable the UI switch.

Take a workspace backup first. Turning encryption on atomically migrates the existing state to AES-GCM;
turning it off requires confirmation and rewrites the state without encryption. Changes take effect
immediately, serialize with other state writes, and persist across restarts without manually moving
the state file. The state file includes a versioned envelope recording the selected mode (the disabled
payload is base64-encoded JSON, not encrypted). Saved mode overrides the original
`Mulse:Encryption:Enabled` default. Legacy plaintext and configuration-encrypted files remain readable;
a UI switch migrates them to the new format, which older application versions cannot read.

Keep the same environment key available while encryption is on. A missing/wrong key or damaged
encrypted payload fails startup rather than replacing your data with an empty workspace. Workspace
backup encryption uses its own password, independently of this switch. At-rest encryption protects
the state file, not live API/UI access or other files produced by modules.

This remains a PoC without application access control. Use only in a trusted environment; masking and
encryption are not authentication or authorization. Add access control and HTTPS before exposing it
to untrusted users or networks.

The reference autocomplete interaction test is included in the [validation commands](#validation).

## Source acknowledgement and cleanup

Fetchers can optionally implement `IFetchOutcomeModule` and return an opaque, serializable
`IntegrationBatch.SourceReceipt`. The runtime captures that receipt with the original resolved fetch
settings before parsing, and calls `OnProcessingCompletedAsync` with a `FetchProcessingOutcome`:

- **Success** only when all processing and applicable deliveries succeed, including flows with zero
  delivery actions. Conditionally skipped actions are not failures.
- **Failure** after a processing/delivery stage permanently fails (its retries are exhausted).
  Independent delivery failures retain their existing best-effort `Completed` result with warnings,
  but still produce a **failure** source notification. Failure does **not** mean nothing was delivered;
  some destinations may already have received data.
- Pending processing retries, orchestration suspension and cancellation do not send a terminal
  notification. Receipts survive retry persistence and correlated orchestration checkpoints.

This is a focused fetcher lifecycle hook, not a general event bus or an extra pipeline module type.
Database/custom fetchers can use it to mark records or update source status; existing read-only database
fetchers are unchanged. Modules without the interface or without a receipt retain their old behavior.

### Built-in source actions

**Behavior change:** local filesystem/document-repository `afterProcessing` and SFTP `deleteAfterRead`
settings no longer remove files immediately after reading. They now apply only after successful full
processing/delivery. Defaults still leave sources in place.

- Filesystem and document repository: `afterProcessing` selects `None`, `Delete`, `MoveToArchive` or
  `Rename` on success. `onFailure` independently selects the permanent-failure action (default `None`).
  `archivePath` and `failureArchivePath` choose destinations; their defaults are sibling
  `<source-directory>.processed` and `<source-directory>.failed` folders. Relative subdirectories are
  preserved. Rename adds `.processed` or `.failed`; choose a search pattern that excludes these suffixes
  (a broad `*.*` pattern can read renamed files again). Repository sidecars move/delete with documents.
- SFTP: `deleteAfterRead: true` now means delete on successful processing. Optional `failureRemotePath`
  quarantines failed files in an **existing remote directory**, using an execution-specific filename;
  blank keeps the original. No live SFTP-server integration test is included.

The filesystem, document-repository and SFTP polling fetchers skip unchanged files held by that flow's
suspended checkpoints, while still accepting new files. The separate `file-system-watcher-fetch` does
not implement source-outcome cleanup. An idle poll does not count as a follow-up message for
`ResumeOrWait`. Custom source modules
can consult `FlowExecutionContext.PendingSourceReceipts` to provide the same reservation behavior;
custom orchestration stores must implement `GetPendingSourceReceiptsAsync`.

Receipts identify exact paths, sizes, timestamps and SHA-256 content hashes. Cleanup does not re-scan
the source directory or trust payload metadata paths, does not overwrite archive files, and refuses
changed/replacement sources. Actions tolerate retries after partial cleanup. These checks are not a
filesystem transaction or a distributed source lock: do not let other flows/processes concurrently
replace files being consumed. Sources remain present during delivery retries. Choosing `None` deliberately
allows a later, fresh run to read them again.

### Example: archive successful files and quarantine failures

In the designer, select the **File system fetch** node and set **After successful processing** and
**After permanent failure** to `MoveToArchive`. Set separate archive directories outside the source
folder, then save the flow. Equivalent API fetch-step configuration (not a complete flow):

```json
{
  "module": "file-system-fetch",
  "settings": {
    "path": "C:\\MulseData\\incoming",
    "searchPattern": "*.json",
    "afterProcessing": "MoveToArchive",
    "archivePath": "C:\\MulseData\\processed",
    "onFailure": "MoveToArchive",
    "failureArchivePath": "C:\\MulseData\\failed"
  },
  "retry": {
    "maxAttempts": 3,
    "delaySeconds": 5
  }
}
```

The file remains in `incoming` while processing/delivery retries are pending. It moves to `processed`
only after all applicable deliveries succeed, or to `failed` after permanent processing/delivery failure.
The fetch-step retry policy above also governs its outcome notification; configure delivery-step or
flow-level retries separately. Refresh the designer's module catalog if the new settings are not visible.

### Notification retries and recovery

Source callbacks use the **original fetch step's retry policy** (step override, then flow default,
otherwise one attempt). Notification-only retry state survives restarts; it never re-fetches, re-parses
or resends successful deliveries. Already acknowledged sources are skipped. A callback can still be
repeated after a crash between its side effect and durable acknowledgement: implementations **must be
idempotent**, use the stable receipt ID for deduplication, and not depend on the original module instance.

Exhausted notifications are logged and retained as failed source state, blocking fresh executions of
that flow to prevent accidental redelivery. Fix the source/permissions problem, then explicitly retry
only the failed notifications with `POST /api/flows/{flowId}/source-outcomes/retry` (also available in
OpenAPI). This uses the original settings and outcome, even if the flow was subsequently edited or
disabled, and gives failed callbacks a fresh attempt budget. A processing failure remains a failure;
notification recovery does not turn it into success. Callback errors and the original processing error
are both reported. Source packages cannot be unloaded while receipts still need them.

For example, after correcting filesystem permissions, retry an exhausted notification from PowerShell
using your Service base URL and URL-encoded flow ID (the ID, not its display name):

```powershell
$serviceUrl = 'http://localhost:5210' # Local Service launch profile; use HTTPS remotely.
$flowId = [Uri]::EscapeDataString('orders')
Invoke-RestMethod -Method Post -Uri "$serviceUrl/api/flows/$flowId/source-outcomes/retry"
```

No request body is required. `Completed` means notifications finished; inspect `detail` for any original
independent-delivery failures. `Retrying` means another notification attempt was scheduled. An original
processing exception is still reported after its failure notification succeeds. Calling this endpoint
when there are no exhausted notifications returns a conflict. Recovery is currently API/OpenAPI-based;
there is no dedicated recovery button in the UI. Do not use the normal **Run** action to retry cleanup.

Successful orchestration checkpoints are removed only after successful notifications. Deleting a flow,
replacing the workspace or discarding its runtime state also discards its pending work: reconcile source
files and partial deliveries before doing so. Receipts contain resolved settings (potentially credentials)
and share the runtime state's optional at-rest encryption; protect backups and API access accordingly.

## File system delivery

The **File system deliver** module (`file-system-deliver`) supports these output naming modes:

| `namingMode` | Example output | Behavior |
|---|---|---|
| `Execution` (default) | `orders-runid-20261029150000123-0.xlsx` | Original execution/timestamp/index naming. |
| `Numeric` | `orders-1.xlsx`, `orders-2.xlsx` | First available positive counter per base name and extension, including across runs. |
| `Random` | `orders-<32 hex characters>.xlsx` | Cryptographically random suffix per payload, not a content hash. |
| `Fixed` | `orders.xlsx` | Exact configured filename. |
| `FixedDate` | `orders-20261029-150000123.xlsx` | Configured base name plus UTC date/time, then extension. |

Set `path` to the destination directory. `fileName` is required for Fixed/FixedDate and is an optional
base name for Numeric/Random (otherwise the payload name is used). Include an extension if desired.
`extension` overrides it, accepting `xlsx` or `.xlsx`; otherwise the configured filename extension,
payload extension, or `.bin` is used, in that order. Names must not contain directory paths.
`dateFormat` uses invariant .NET date formatting, default `yyyyMMdd-HHmmssfff`; for daily files use
`yyyyMMdd`. Formats must produce valid filename fragments (no slashes or colons).

`overwriteExisting` defaults to `false`: collisions fail explicitly rather than silently replacing data.
Enable it to truncate and replace the target. For Fixed/FixedDate, repeated payloads targeting the same
name will replace each other when enabled; otherwise a later collision fails (earlier deliveries remain).
Numeric always uses atomic create-new and advances past existing files, even if overwrite is enabled.
It fills gaps in numbering; it is not a permanent monotonic sequence. Existing flows keep their original
execution-based filename pattern. Refresh the designer's module catalog to see the new settings.

## Excel files

Build `Mulse.Excel` (or the solution). Its DLL and dependencies are copied into
`Service\plugins\Mulse.Excel\`. In **Configuration > Module Packages**, refresh and load **Mulse.Excel**;
newly discovered packages start unloaded. No Service project reference or installed Excel is needed.
Deploy the entire plugin output folder, not just `Mulse.Excel.dll`.

Use existing `file-system-fetch` / `file-system-deliver` modules for filesystem access:

- Read: `file-system-fetch` -> `excel-parse` -> downstream processing.
- Write: JSON rows -> `excel-render` -> `file-system-deliver`.

Both Excel modules accept `sheetName` or a **1-based** `sheetNumber` (1-256, default `1`). A supplied name
wins over the number. Reading selects that sheet and fails explicitly if it does not exist. Writing
creates a **new workbook per input payload**, naming its sheet or creating preceding empty sheets to
reach the selected number; it does not edit/append an existing workbook or preserve formatting/macros.

`firstRowIsHeader` defaults to `true`. `excel-parse` detects XLS/XLSX from the content, reads the first
physical row as unique, nonblank column names, and outputs a JSON array of objects, for example:

```json
[{ "OrderId": "00123", "Amount": 12.5, "Active": true }]
```

With `firstRowIsHeader: false`, reading outputs arrays of cell values instead. Numbers/booleans stay
typed, blanks become `null`, date-formatted cells become ISO-8601 strings without an assumed timezone,
and internal blank rows are preserved. Formulas use the workbook's saved cached values (which may be
stale); formulas/external links are not evaluated. Excel error cells fail with sheet/row/column details.
`maxRows` defaults to `100000` data rows and rejects excess rows rather than truncating; loading is
in-memory, so restrict input workbook sizes appropriately.

`excel-render` accepts arrays of objects (columns are the union of property names, in first-seen order)
or arrays of cell values. Object rows write headers unless `firstRowIsHeader` is false; array rows are
written as supplied, allowing a caller-provided header row. `format` is `xlsx` (default) or `xls`.
Cell values must be strings, finite numbers, booleans, or null; nested objects/arrays fail explicitly.
Strings beginning with `=` are literal strings, not executable formulas. Dates supplied as JSON strings
remain text. Excel numbers are double precision (about 15 significant digits); keep identifiers and
precision-sensitive values as strings. XLS limits (65536 rows/256 columns) and XLSX limits
(1048576 rows/16384 columns) include header rows and are checked before writing.

Payload filenames/extensions and MIME types are updated; original metadata is preserved and
`excelSheetName` / `excelSheetNumber` identify the selected sheet. Modules process every payload in a batch.

The plugin pins Apache-2.0-licensed **NPOI 2.7.5** and explicitly patches its transitive cryptography
and image dependencies. **SixLabors.ImageSharp 3.2.0 uses the Six Labors Split License**; review
[its license terms](https://sixlabors.com/pricing/) for your organization's eligibility before deployment.
Newer NPOI versions have different commercial maintenance terms; review licensing before upgrading.

## BizTalk migration

In the designer, choose **Import from BizTalk** and supply a `.sln`, `.btproj`, custom project, or folder
path **accessible to the Service host**. This is path-based analysis, not a browser project upload. Add
semicolon-separated additional solution/project/folder paths to resolve cross-solution references, and
binding file/directory paths when deployment bindings live outside the source tree.

The implementation in `Service\BizTalkImport` can:

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

Apply a candidate to the canvas, review its transport paths/credentials, mappings, retry/compensation
settings and warnings, then explicitly save it. Download the suggested scaffolded modules individually
or as a bundle, implement their business behavior, build/load them, and resolve configuration gaps
before enabling. A scaffold is a migration starting point, not a completed translation or a guarantee of
BizTalk-equivalent behavior. Existing flow edits still require deliberate review before saving.

## Running locally

Prerequisites: **.NET 10 SDK**; **Node.js** only for the browser JavaScript tests. SQL Server and SFTP/SMTP
servers are required only for flows using them. Excel does not require Microsoft Excel to be installed.

From the repository root, build the solution (including plugins into `Service\plugins\`):

```powershell
dotnet build .\Mulse.slnx
```

Start the runtime in one terminal, using the Service directory so relative module/sample paths are
unambiguous:

```powershell
Set-Location .\Service
dotnet run --launch-profile Service
```

In another terminal starting at the repository root, run the UI:

```powershell
Set-Location .\Mulse.Ui
$env:MulseApi__BaseUrl = 'http://localhost:5210'
dotnet run --launch-profile http
```

Open **http://localhost:5203**. The Service profile listens on **http://localhost:5210** and exposes its
Development OpenAPI JSON at **http://localhost:5210/openapi/v1.json**. There is no bundled Swagger UI.
The UI's `https` launch profile also exposes **https://localhost:7021** and needs a trusted development
certificate. `MulseApi:BaseUrl` in the UI configuration defaults to the local Service URL; override it
with `MulseApi__BaseUrl` when the runtime is hosted elsewhere. It is a **UI-server-to-Service** address,
not the browser's URL.

Alternatively, from the repository root, use the Aspire AppHost to launch both projects:

```powershell
dotnet run --project .\Mulse.AppHost\Mulse.AppHost.csproj
```

Use the launch endpoints shown by Aspire and ensure `MulseApi:BaseUrl` still resolves to the actual
Service endpoint. For a single optional plugin, `dotnet build .\Mulse.Excel\Mulse.Excel.csproj` (or its
SQL Server/HL7/sample equivalent) is enough; then load it in **Module Packages**.

### First run and persistence

`Mulse:RuntimeStatePath` defaults to `Data\mulse-runtime.json` relative to the **Service content root**.
On first creation, `Mulse:Pipelines` and `Mulse:PluginDirectories` in `Service\appsettings.json` seed the
runtime state. Once that file exists it is authoritative: changing those seed sections does **not**
update existing flows or the saved plugin directory list. Use the UI/API for flow/package changes.
The state stores definitions, config/secrets, package registrations, retries and orchestration
checkpoints; it is not just a disposable flow cache and may be encrypted rather than plain JSON.

The Service account needs read access to source/plugin paths and write access to its state directory,
`Runtime\PluginCache` shadow-copy directory, output folders, and any archive/quarantine paths it uses.
Cleanup additionally requires delete/move permissions on input files. Module filesystem paths and
BizTalk source paths refer to the **Service machine**, not the browser machine. For deployment, prefer
explicit absolute paths and preserve the state file and external encryption key separately. Do not
delete the state file to refresh examples without backing up/reconciling pending work.

A fresh workspace includes `sample-file-pipeline` and `sample-multi-source-join-pipeline`, both on-demand.
For a basic smoke test, open the first sample in the dashboard, inspect its source/output paths, and
choose **Run**. With the shipped data it reads `Service\SampleData\Inbox\customer-order.json`, parses JSON,
adds an envelope, logs the output and writes to `Service\SampleData\Outbox`. Existing workspaces may not
contain these seed flows. The sample leaves input in place, so running it again produces another output;
it does not demonstrate source cleanup until you configure an after-processing action.

### Validation

From the repository root:

```powershell
dotnet test .\Service.Tests\Service.Tests.csproj
node --test .\Mulse.Ui.Tests\theme.test.cjs .\Mulse.Ui.Tests\floating-panel.test.cjs .\Mulse.Ui.Tests\file-drop.test.cjs .\Mulse.Ui.Tests\node-remove.test.cjs .\Service.Tests\reference-input.test.mjs
```

The JavaScript suites need no npm dependencies.
end-to-end behavior against your live SQL, SFTP, SMTP or HTTP systems; validate those separately before
letting a flow modify source data.

## UI workspace

The admin UI menu links to the dashboard and groups **Config values**, **Modules**, **Module Packages**,
and **Backup & Restore** under Configuration. Open the flow designer using create/configure links from
the dashboard rather than a menu entry. `/modules` hosts the searchable module catalog with kind and source-package filters.
Each module links to its owning package and shows its source kind and assembly path. `/module-packages`
lists the available modules in each loaded package, collapsed by default under **Show/hide modules**,
with links back to a package-filtered catalog and load/unload/reload controls. Unloaded packages do not
execute code to enumerate module details; load one explicitly to inspect its modules.

### Authoring a flow

1. Choose **Create flow** from the dashboard, give it a stable ID and optional display name, and keep it
   disabled while authoring. IDs are technical references; names are for people.
2. Add **Source** for a linked fetch/parse pair, or add fetch/parse nodes separately. Choose modules and
   configure real input locations. Link dependencies in node properties; moving a node is visual layout,
   not a change to execution order or source wiring.
3. Add augments as needed. For `multi-source-join-map-augment`, choose inputs by **parse ID**, join keys,
   `Inner`/`Left`/`Right`/`Full` semantics and `FanOut` or `Nested` output. `*` selects the accumulated
   working document for chained joins. Map Left/Right/Literal values or transform results into targets.
4. Add a render node and at least one deliver node per route; configure paths/endpoints and any
   conditions, default retries or atomic scope. Source graph errors, missing modules and incomplete
   routes must be resolved before saving. Create/update validation requires at least one source and
   one delivery route; "zero deliveries" in lifecycle semantics means none applicable at execution time.
5. Choose **Create flow** or **Save flow** in the bottom bar. There is no automatic save. Resolve missing
   config/secret references on **Config values**, load dependencies, review settings, then enable and
   manually run from the dashboard before switching to automatic triggers.

Sample analysis and canvas editing do not connect to live sources. **Run** does: even a logging-only
output can still execute configured fetch/augment behavior or source cleanup. Use safe test data and
separate input/output locations; don't point a poller back at its own output/archive directory.

Solid arrows carry batches; dashed arrows supply parsed input to a dependent fetch (for example a SQL
lookup). **BATCH xN** means multiple inputs feed one batch, not a row-by-row join. **JOIN BY KEY** marks
an actual join/map augment. **USES** counts consumers, including lookup dependencies: the original data
remains available to downstream processing; the lookup arrow does not consume it.

### Adding or replacing a parser

Use **Parser** in the toolbox to add just a parse node, without another fetch. Click while a fetch is
selected, drop onto a fetch, or use its Properties **Add parser for this fetch** action to link it. A
click with no selected fetch also repairs the sole unparsed fetch automatically; if several fetches
are unparsed, choose one explicitly. Dropping elsewhere creates an unlinked parser: select its inputs
under **Parses raw output of**. Existing fetch settings, other parsers and node positions are retained.
Replacing a deleted parser does not restore its former module/settings or references: reselect the
new parse ID in downstream lookup inputs and join settings before saving.

A parser describes the **fetch's output format**, not the source transport. Excel files use Excel
parse; SFTP can contain different file formats, so choose the matching parser. SQL Server lookup already
returns a JSON array of column/value objects, so use **JSON parse**: it validates/normalizes JSON,
preserves the row array, and the runtime tags it with the parse node's source ID for joins/lookups.
It neither reruns SQL nor joins rows. A parser remains required by the source-graph model even for
already-structured JSON. Adding a parser to a JSON-only fetch preselects `json-parse` when loaded;
otherwise choose the appropriate module explicitly. Changing an existing fetch module does not
silently replace its parser or settings.

### Example: Excel rows enriched from SQL, written to one JSON file

1. Fetch the workbook with a filesystem fetcher and parse it with `excel-parse`; call the parse node
   `primary`. Select the correct worksheet and use headers so `OrgId` is a named column.
2. Add another source. Choose `sql-server-lookup-fetch` and **Input from: primary**. Keep **Parsed by**
   pointing to this second source's parser, not the Excel parser. Change the second parser to
   **`json-parse`**, because the lookup returns JSON, and call that parse node `lookup`.
3. Set SQL `connectionString` (for example `{{secret:sql-connection}}`), `idPaths` to `OrgId`, and a query
   returning the key plus enrichment fields. For example, with string-valued organization IDs:

   ```sql
   SELECT OrgId, OrgName
   FROM dbo.Organizations
   WHERE OrgId IN (SELECT value FROM STRING_SPLIT(@ids, ','));
   ```

   The module extracts distinct IDs across input rows and sends one comma-separated `@ids` parameter
   for a batched query, not one SQL call per Excel row. IDs must be suitable for that comma-separated
   representation; this is not an arbitrary per-row stored-procedure adapter.
4. Add **Join sources** with left `primary`, right `lookup`, and the key pair `OrgId` / `OrgId`.
   Use **Left** to keep unmatched spreadsheet rows, or **Inner** to discard them. Map spreadsheet
   fields from Left and enrichment fields from Right into the output. Duplicate Excel IDs still
   produce their own rows; multiple matching SQL rows fan out unless you choose Nested cardinality.
5. In **JSON render**, enable **Combine into one JSON array** (`combinePayloads: "true"`). In **File system
   deliver**, configure the output directory and optionally Fixed naming with `fileName: "enriched.json"`.
   Review overwrite behavior before repeating runs. Save and test with safe data.

By default JSON render still emits one payload per input payload. Combining emits one `combined.json`
payload per route invocation, so filesystem delivery writes one file containing all joined rows in that
batch (potentially spanning several input workbooks). It does not collect across runs or perform the
join itself. Root arrays contribute their items in order; objects/scalars contribute one item, while
nested arrays/objects remain unchanged. Empty input emits no payload/file; an input containing only
empty arrays emits `[]`. Only metadata shared identically by all inputs is retained, plus render metadata,
so a combined file is not incorrectly tagged as belonging to just the first source. Set `indented` for
readable formatting. Invalid JSON fails rather than producing a partial output. Refresh the designer's
module catalog after updating the Service to see the new setting.

### Draft flows from samples

Use the single **Add sample data** action to select a file or paste sample text, then analyze and add a
source lane. Dropping files onto the canvas is a shortcut for the same additive workflow. Neither path
replaces existing nodes; samples infer only parser/data shape and are not saved or used as runtime input.
The bottom save bar makes **Create flow** / **Save flow** explicit; canvas changes are not saved automatically.

JSON, XML,
CSV/delimited text, plain text, XLS and XLSX are supported. Up to ten files can be added together:
text samples are limited to 1,000,000 bytes each; binary Excel samples to 10 MiB each. All samples in a
selection must analyze successfully before any lanes are added. Unknown/nontext binary samples fail
explicitly. Text files use UTF-8 or a BOM-declared encoding.

Each sample adds a linked fetch placeholder (its module is intentionally unselected) and a compatible
parse node without replacing existing nodes, positions, settings, or routes. The sample filename and
local path are not copied into the flow. Choose the real fetch module and configure its source settings
later. An empty canvas also gets `json-render` -> `logging-deliver`; new flows start disabled and
on-demand. Existing routes may consume the new source's data: review source selections before saving.
No flow is saved or run automatically.

Excel inference checks XLS container signatures / XLSX archive structure, not cell values or full
workbook validity. It selects `excel-parse` with sheet 1 and headers enabled; review `sheetName`,
`sheetNumber`, and `firstRowIsHeader`. Packages are never auto-loaded for inference. If Mulse.Excel
is unloaded, the draft retains an explicit Excel parser node and shows loading instructions instead
of substituting a text parser. Load the package in another tab, then choose **Refresh modules** in
the designer to update availability without discarding the draft. Other unsupported formats leave a
parser selection that needs operator attention.

### Managing flows

Flows have an optional editable **Flow name**, separate from the unique **Flow ID** used in URLs,
API calls and execution references. Set or rename it in the designer's Flow overview and save; the ID
stays fixed after creation. Unnamed existing flows display their ID. Names need not be unique; the
flow list shows the ID as well. Names survive duplication, enable/disable, categorization and backup/restore.

Search flows by name, ID, category, module, or source-node ID. Combine saved user-defined categories with
status, trigger, module, and unresolved-configuration filters, sort by ID, and browse ten flows per page.
Use **Categorize** to assign or clear a category; categories are saved in runtime state and retained
when enabling a flow or saving it in the designer.

**Duplicate** asks for a unique new ID and copies the full definition, including category, settings,
retry policies, and configuration references. Copies start disabled; review shared input locations,
output destinations, and references before enabling. Execution checkpoints and pending retries are
not copied. **Delete** requires confirmation and permanently removes the definition and its saved
checkpoints/retries, but does not undo deliveries or cancel an execution already in progress.

API clients can use `POST /api/flows/{flowId}/duplicate` with `{ "id": "new-flow-id" }`,
`PATCH /api/flows/{flowId}/category` with `{ "category": "Finance" }` (or `null` to clear), and
`DELETE /api/flows/{flowId}`. Full flow create/update requests also accept an optional `category`.

### Workspace backup and bulk deletion

Open **Configuration > Backup & Restore**, also linked from Flows, to download a password-encrypted
`.mulsebackup` file (maximum 10 MB). Use at least 12 characters and keep the password separately;
it cannot be recovered. Backups include all flow definitions/categories, configuration values
(including secrets), and package registrations. Encryption uses PBKDF2-SHA256 (600,000 iterations)
and AES-GCM with a fresh salt and nonce. Use HTTPS when accessing administration remotely.

Select a file, enter its password, choose **Merge** or **Replace**, and preview before restoring.
Merge rejects conflicting flow IDs, managed package IDs, or configuration keys without changing
anything. Replace removes the current flows, config values, managed registrations, retries, and
checkpoints. Both modes import flows and package registrations disabled for review. Preview names
flows and unknown/unloaded modules, accounting for packages that will be disabled by the restore.
The Flows page continues to show dependency warnings and blocks Enable/Run until they are resolved.
The API also rejects enabling or executing flows with missing dependencies (before any input is fetched),
including delivery compensation and join/map transform modules. Load the required package or correct
the module references, then refresh Flows and review before enabling. Package synchronization can take
five seconds; an active execution prevents unloading until a later synchronization attempt.
Backups do not contain DLLs, local discovery directories, pending retries, or execution checkpoints.
Destination discovery directories are retained; unrelated auto-discovered packages may remain loaded.
Assembly paths refer to the API server, so move DLLs separately and update paths as needed.
Restore requires typing `RESTORE WORKSPACE`.

**Delete all flows** requires typing `DELETE ALL FLOWS`. It deletes all definitions, pending retries,
and checkpoints, regardless of dashboard filters, while retaining config values and packages.
Neither deletion nor restoration cancels in-progress runs or reverses completed deliveries; stop
running workloads first when replacing a workspace. These controls manage the current workspace;
multiple isolated workspaces are not yet implemented.

API operations: `POST /api/workspace/backup` takes `{ "password": "..." }` and returns a binary download;
`POST /api/workspace/restore/preview` takes `{ "content": "<base64 file>", "password": "...", "mode": "merge" }`.
Use the same payload plus `"confirmation": "RESTORE WORKSPACE"` at `POST /api/workspace/restore`.
`POST /api/workspace/flows/delete-all` takes `{ "confirmation": "DELETE ALL FLOWS" }`.

### Package load and unload

Copy trusted plugin DLLs into a configured plugin folder on the API server (no browser DLL upload).
Use `plugins\MyPackage.dll` or `plugins\MyPackage\MyPackage.dll`, alongside required dependencies.
Every five seconds, discovery checks for compatible module installer types without constructing installers
or modules. New packages appear **unloaded** in **Configuration > Module Packages**; use **Refresh packages**
to update the list, then **Load package** to activate one. Full initialization/dependency errors are shown
when loading. Enabled registrations remain enabled across restarts; unloaded registrations stay unloaded.
Loading executes code with the server's permissions. **Unload package** shows an inline confirmation;
choose **Confirm unload** to perform the operation. Blazor Server sends clicks over its existing connection,
and the UI server calls the API, so browser DevTools will not show a separate API request.
Unloading is rejected if any saved flow references
its modules, including disabled flows, compensation steps, and join/map transforms. The API lists the
dependent flow IDs; disabling those flows is not enough: remove or replace their references first.
Active executions also block unloading/removal, even if their saved flow has since been deleted.
Pending source receipts in retries or orchestration checkpoints also block unloading/removal, even if
the flow was edited to use a different fetcher. Complete their source notifications before unloading.
These checks apply to PATCH, PUT/POST with `enabled: false`, and package DELETE, without changing
registrations on rejection. Its disabled registration survives restarts and prevents auto-discovery
from reloading the same package ID. **Load** re-enables it; **Reload** refreshes a loaded package after
its DLL changes. Built-in packages cannot be unloaded or replaced.

API clients can `PATCH /api/module-packages/{packageId}/enabled` with `{ "enabled": true }`, or
`{ "enabled": false, "confirmation": "UNLOAD PACKAGE" }` to unload.

### Appearance and designer panels

Appearance follows your system's light/dark preference until you use **Dark mode** in the navigation.
Your selection is saved in this browser and applies to all pages, including the designer canvas.
No external fonts, styles, or theme services are required.

On smaller screens, designer tools stack around the canvas instead of floating over it. Tables keep
their column headers and scroll horizontally inside their panels. Desktop designer panels remain
draggable and collapsible, retaining their dragged positions during canvas interactions for as long
as each panel remains open on the page. Properties starts aligned to the right edge of the designer
viewport, then stays where you drag it.

The initial view and **Fit** use an 80% scale (20% smaller than the original fitted view); zoom controls
and wheel zoom remain available. The workspace is unbounded in every direction: nodes, source groups
and toolbox drops can use negative coordinates without hitting a top/left wall. Pan by dragging empty
canvas space to reach more room, and use **Fit** to bring all nodes and group headers back into view.
Drag a source group's header to move its parse and fetch nodes together,
keeping their relative spacing. Shared fetch nodes move once and other groups containing them resize;
dependent lookup groups are not moved automatically. Individual nodes remain draggable. Group/node
positions are retained during this editing session; saving still stores the flow definition, not custom
canvas layout, and Auto layout deliberately repositions nodes.

Actual key-based joins appear as **Join sources** nodes. The toolbox's **Join steps** list offers
**Configure join** and **Remove join**, and selecting a join opens its keys/mappings in Properties.
Removing a join leaves all sources and delivery routes intact. A **BATCH** badge is automatic wiring,
not another join; render Properties explains this and links to the real joins.

Use the toolbox's **Render** action to add a standalone render. Selecting an orphaned deliver node also
offers **Add render to this route**, preserving its settings and other deliver nodes. Clicking/dropping
Render targets a selected/nearby route missing its render, or the sole incomplete route; otherwise it
creates a new render-only route. Existing renders are not duplicated. Use **Extra deliver** to finish a
new render-only route, or **Delivery route** to create a complete render/deliver pair. Incomplete routes
cannot be saved. **Remove render only** keeps that route's deliver nodes for later repair.

Every canvas node has a top-right **X** button (also keyboard accessible) to remove just that node.
Connected nodes remain; fetch/parse references are detached, and incomplete sources/routes must be
repaired before saving. Use the Properties panel's **Remove route** or **Remove source (with its fetches)**
when you intentionally want grouped removal. Deletions affect the draft until you **Save flow**.

## API reference

Use the Development-only `/openapi/v1.json` document for complete request/response schemas. API bodies
use camelCase property names and string enum values. The configured Service URL is independent of the
UI URL. Main surfaces:

| Area | Endpoints |
|---|---|
| Flow definitions | `GET`/`POST /api/flows`; `GET`/`PUT`/`DELETE /api/flows/{flowId}`. |
| Flow actions | `PATCH /api/flows/{flowId}/enabled`, `PATCH /api/flows/{flowId}/category`, `POST /api/flows/{flowId}/duplicate`, `POST /api/flows/{flowId}/run`. |
| Source recovery | `POST /api/flows/{flowId}/source-outcomes/retry` (no body; exhausted callback recovery only). |
| Catalog/packages | `GET /api/modules`; `GET`/`POST /api/module-packages`; `GET`/`PUT`/`DELETE /api/module-packages/{packageId}`; `PATCH .../enabled`, `POST .../reload`. |
| Sample design | `POST /api/flow-designer/analyze`, `POST /api/flow-designer/flows`. |
| BizTalk import | `POST /api/biztalk-import/analyze`, `POST /api/biztalk-import/flows`, and scaffolded-module download endpoints. |
| Config values | `GET /api/config-values`, `GET /api/config-values/references`, `GET /api/config-values/usages`; `PUT`/`DELETE /api/config-values/{reference}`. |
| Workspace | `GET`/`PUT /api/workspace/encryption`; `POST /api/workspace/backup`, `/api/workspace/restore/preview`, `/api/workspace/restore`, `/api/workspace/flows/delete-all`. |
| Inbound HTTP | `POST`/`PUT /api/inbound/{route}` for a registered HTTP push source. |

For example, from PowerShell while the local Service is running:

```powershell
$serviceUrl = 'http://localhost:5210'
Invoke-RestMethod "$serviceUrl/api/modules"
Invoke-RestMethod "$serviceUrl/api/flows"
# This executes a real enabled flow, not a dry run:
$flowId = [Uri]::EscapeDataString('sample-file-pipeline')
$result = Invoke-RestMethod -Method Post -Uri "$serviceUrl/api/flows/$flowId/run"
$result | Format-List
```

Inspect `outcome`, `detail`, `nextAttemptAt` and captured `responsePayloads`, not just `payloadCount`.
Request/response payload content is base64-encoded in `contentBase64`. API failures use Problem Details;
validation/bad arguments normally return `400`, missing resources `404`, conflicting state `409`, and
unhandled execution failures `500`.

## Troubleshooting and limitations

- **UI cannot reach the runtime:** check the Service launch URL and the UI server's `MulseApi:BaseUrl`.
  A different browser URL does not change the UI server's backend address.
- **Edits to appsettings appear ignored:** existing runtime state overrides seed flow/plugin-directory
  settings. Edit the saved definitions through the UI/API rather than deleting pending state.
- **A module is missing:** build/deploy its complete plugin output, wait for discovery, explicitly load
  the package, then refresh modules in the designer. Check package initialization errors and saved paths.
- **Enable/Run is blocked:** resolve missing module dependencies and config/secret references. Disabled
  flows still protect their referenced packages from unload.
- **Input is fetched again:** default filesystem cleanup is `None`. Configure source acknowledgement,
  narrow the search pattern, and keep outputs/archives outside the watched input. The watcher is a
  separate event-based fetcher without the polling fetcher's cleanup settings.
- **A flow is held after delivery:** inspect logs and pending retry/notification details. Exhausted source
  callbacks require the notification-only recovery endpoint; running the flow again is not recovery.
- **Inbound route returns 404:** check that a Push flow using `http-inbound-fetch` is enabled and registered;
  allow about five seconds after changes. A queued `202` does not mean delivery has finished.
- **Runtime state cannot be decrypted:** verify the configured environment key and encoding before
  changing anything. Losing that key loses access to encrypted state; the UI cannot recover it.

Mulse is a **single-process proof of concept**, not a production-hardened integration service. There is
currently no application authentication/authorization layer: protect **both** UI and Service endpoints
with network/access controls before exposing them beyond a trusted development machine. Masking and
at-rest encryption do not restrict API access, plugin code, logs, network traffic, or external files.

There is no distributed locking, shared-state multi-instance coordination, exactly-once guarantee,
transactional message broker, or general rollback across external systems. Persisted retries/checkpoints
and idempotent callbacks cover specific continuations, not every crash window. Built-in polling modules
read batches into memory; apply sensible input/workbook limits and test external-system behavior before
using destructive cleanup. Workspace backups omit live work and binaries as described above; retain a
separate operational recovery plan for state, keys, plugin files and source/destination data.

## Status

This is an in-development project (not yet deployed to production). Expect breaking changes as the
architecture evolves. Review imported/generated flows and module behavior before using real data.
