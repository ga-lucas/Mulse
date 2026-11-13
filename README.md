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
| `Mulse.Persistence` | Shared EF Core model, transactional workspace persistence, immutable flow revisions and payload encryption. |
| `Mulse.Persistence.Sqlite` | SQLite provider, durability configuration and migrations. |
| `Mulse.Persistence.SqlServer` | SQL Server provider and migrations (separate from the SQL integration plugin). |
| `Mulse.Persistence.PostgreSql` | PostgreSQL provider and migrations. |
| `Service.Tests` | xUnit coverage for runtime/retries/source outcomes, persistence, API/UI interactions, module packages, sample analysis, Excel, and BizTalk migration; also contains the reference-input JavaScript test. |
| `Mulse.Ui.Tests` | Dependency-free Node.js tests for browser theme, floating panels, and file-drop behavior. |

## Module extensibility

See the [Module developer guide](Module-Developer-Guide.md) for SDK objects, typed/raw contracts, helper wrapping,
registration, hot loading, source receipts, retries, generated migration stubs and verification requirements.

Modules implement one of the core interfaces in `Mulse.Modules` and are registered via an `IModuleInstaller`.
Two ways to ship a module:

- **Built-in**: referenced directly by `Service` (compiled into the host process). Simple, but can't be
  reloaded/unloaded at runtime.
- **Hot-loadable plugin**: built into `Service\plugins\<PackageName>\` (see `Mulse.SamplePlugin`,
  `Mulse.Hl7`, `Mulse.Dbms.SqlServer`, and `Mulse.Excel` for the pattern) and discovered by `ModuleCatalog`
  at startup and every five seconds. New compatible packages appear **unloaded**; activate them from
  **Configuration > Module Packages**. Loading uses a collectible `AssemblyLoadContext` so packages can
  later be reloaded or unloaded without restarting the service, subject to dependency/active-use guards.

Field transforms use `ITransformModule` and `AddTransform<T>()`; no UI code or central transform-name list
is required. Their descriptors provide settings, defaults and choices to the mapper. The sample package now
includes `regex-replace-transform`: one text input, bounded pattern evaluation, and literal replacement.
Load the trusted package in **Module Packages**, then **Refresh modules** in the open designer. This updates
transform/renderer choices without replacing the canvas or unsaved mappings. Plugins execute trusted server
code; they are not sandboxed. Built-in implementations still require a host update to change.

Plugin constructors can use shared host services and `ILogger<YourPluginType>` without retaining collectible
types in host DI caches. Construct plugin-local dependencies inside the module; plugin-owned service types
and keyed plugin-owned services are not registered/resolved through the host container. Avoid static host
caches or event subscriptions containing plugin types/delegates, and dispose resources on module release.
For typed modules, use a package-local source-generated `JsonSerializerContext` and override
`SerializerOptions` with its options (see `SamplePluginJsonContext`). Reflection-based System.Text.Json
can retain plugin types in process-wide emission caches until subsequent cache eviction; generated contracts
avoid this root. Include every input/output/nested contract and preserve the SDK's camel-case JSON convention.
`IRenderModuleInvoker` lets modules invoke registered renderers under host-managed leases; value transforms
use `IModuleInvoker`. Loading/unloading stays operator-controlled and active/saved dependencies are protected.

### Shipped module families

| Package | Modules and purpose | Availability |
|---|---|---|
| Core (`builtin.core`) | `file-system-fetch`, `sftp-fetch`, `flow-link-fetch`; `json-parse`; JSON envelope, decision, multi-source join/map, stateful orchestration and debatch augments; `json-render`, `xml-render`; `logging-deliver`, `file-system-deliver`, `http-deliver`, `flow-link-deliver`. | Built in. |
| Core transforms | Concat, add/subtract/multiply/divide, coalesce, substring, upper/lower case, trim, round and date formatting, selected within join/map fields. | Built in. |
| Compatibility (`builtin.compatibility`) | `document-repository-fetch`, `file-system-watcher-fetch`, `http-inbound-fetch`; `xml-xsd-parse`, `flat-file-parse`, `xml-envelope-debatch-parse`; metadata-promotion and route-selection augments; `flat-file-render`, `xslt-render`, `xml-mapping-render`, `soap-envelope-render`; `document-repository-store`, `smtp-email-deliver`, `http-inbound-reply-deliver`. | Built in. |
| `Mulse.Dbms.SqlServer` | `sql-server-lookup-fetch` uses upstream parsed IDs for a parameterized lookup; `sql-server-execute-deliver` executes SQL text/stored procedures with JSON-to-parameter binding per payload. | Build and load plugin. |
| `Mulse.Hl7` | `hl7-parse` and `hl7-render` for HL7 v2 ER7 text/normalized JSON. | Build and load plugin. |
| `Mulse.Excel` | `excel-parse` and `excel-render` for XLS/XLSX worksheets. | Build and load plugin. |
| `Mulse.SamplePlugin` | Reference raw/typed fetch, parse, augment, render and deliver modules, `regex-replace-transform`, and the `sample-text-batch-render` UTF-8 header/body/trailer combiner. | Build and load plugin; examples, not required for normal flows. |

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

With database storage, HTTP acceptance commits the message before returning `202`, and flow links use a
durable named inbox. Pending messages survive restart and are polled every two seconds for enabled Push
flows. Consumers compete for a channel; this is not broadcast delivery or cross-workspace messaging.
Claimed messages are never blindly put back on the queue after interruption: saved continuations resume
where safe, otherwise the execution is held for inspection. Legacy `Json` mode retains the old in-memory
HTTP/flow-link queues. File-watcher events remain process-local; use periodic file scans for rediscovery.

For retryable HTTP acceptance, send `Idempotency-Key` (up to 256 characters). On the same route, identical
requests with the same key are accepted without a second queue entry; conflicting content/ownership is
rejected. Keep request headers/query/body consistent when retrying. Deduplication lasts while the message
record is retained, not forever; without the header, separate requests are separate messages. A lost
acceptance acknowledgement does not prove the request was not committed. Two-way HTTP connections and
reply waiters are still process-local: a restart cannot restore the original client connection.

### Delivery behavior and retry policy

Each route renders once, then executes its deliver actions sequentially. Without an `atomicScope`, a
permanent **deliver action** failure is recorded while other actions continue; the final result may be
`Completed` with failure details. A render failure without an atomic scope terminates processing.
Always inspect `outcome` **and** `detail`, not just the HTTP status or `Completed` label.

**Database delivery safety:** top-level deliver stages persist their input, resolved step and continuation
before invocation. The delivery journal progresses through Prepared, InFlight and Succeeded. An exception,
cancellation or restart during an attempted effect leaves Unknown and holds the execution, instead of
blindly repeating a potentially committed delivery or compensating based on an uncertain outcome. This
intentionally overrides best-effort continuation and automatic deliver retries for these uncertain effects.
History offers **Confirm independently verified delivery** for one-way effects: inspect destination evidence,
enter it and type `CONFIRM DELIVERED`. This records success and releases the saved continuation; it never
resends the confirmed effect. Later deliveries and source acknowledgements can then run. This action rejects
active effects, missing continuations and request-response deliveries whose response data is unavailable.
Native/ODX internal effects continue to use their existing checkpoint/reconciliation protocol.

An optional shared `atomicScope` compensates already committed deliveries in reverse order when that
scope fails, skips its remaining actions, and lets unrelated routes proceed before reporting failure.
Configure an explicit `compensation` deliver step per action in **Delivery Properties > Delivery
compensation** or through the API. Missing/failed compensation is logged; it is best effort, not an
actual rollback of arbitrary external side effects. The host attempts each compensation once; its
retry field is preserved on editing but is not applied by this best-effort cleanup path.

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
exposes the flow default in Flow overview and overrides under each node's **Step retry policy**.
Uncheck **Override flow retry policy** to inherit again. Existing overrides on fetch, parse, augment,
render and deliver steps, and compensation settings, are retained when a flow is reopened and saved.

Retryable failures persist the failed stage/input and return `Retrying` with `nextAttemptAt`. The retry
driver checks every two seconds and resumes from that stage, preserving prior resolved source batches
and committed delivery state. Processing retries also retain the execution's resolved flow definition,
including configuration/secret values and retry policies: later flow edits or configuration changes do
not redirect pending work. Disabling a flow pauses processing retries without deleting them; enabling
it permits resumption. Modules required by a saved retry cannot be unloaded or removed.
Legacy processing retries without a saved definition are retained but cannot resume automatically:
drain pending processing retries before upgrading, or reconcile their original definition and external
outcomes before recovery. Source-notification-only retries still use their saved receipts.
A stage can partially affect an external system before it throws, so retries can repeat those side
effects: design destination operations for idempotency. Snapshots pin definitions, not plugin binaries;
keep compatible plugin versions available. Resolved secrets remain in state, so enable at-rest encryption.

`Suspended` means a stateful augment stored a checkpoint for a correlated follow-up. Configure
`stateful-orchestration-augment` with `ResumeOrWait`, `Suspend` or `Complete`, a correlation source/key,
and bookmark as appropriate. Completion is explicitly marked by `Complete`; resuming alone does not
remove the checkpoint. This is focused persisted correlation, not general-purpose durable orchestration.
A crash during ordinary processing is not automatically recoverable unless an appropriate retry or
checkpoint snapshot was persisted. Source-notification recovery has its own durable continuation,
described below.

In the stateful node's properties, **Checkpoint state to remember** visually adds/removes capture rows:
state key, source (metadata, JSON payload path, literal, payload name or content type), and path/value.
These settings persist as `stateCaptureJson`; advanced JSON remains available without discarding unknown
content. Captures read the first payload; absent/empty values are not stored. Invalid/null definitions,
duplicate or reserved `correlationKey` keys, unknown sources/properties and missing source paths fail before
checkpoint persistence. Limits: 128 captures, 256 characters per state key, 65,536 JSON characters.
This editor does not implement a general loop/listen/timer workflow or Billing batch collection semantics.

### Durable count-triggered collection

Add **Collect correlated batch** (`durable-batch-augment`) from the toolbox and configure its collector name,
correlation metadata key and minimum payload count (1-10,000) in node properties. Promote the client/group key
before this node. Each input execution must contain exactly one nonblank correlation value; values are
case-insensitive. Mixed-client runs fail rather than guessing how to partition a source receipt. Separate
clients into source runs/flows first. Empty input does not create a checkpoint or flush another batch.

The collector persists bodies, metadata and source receipts across executions and disk restarts. Below the
threshold it returns `Suspended`, without acknowledging sources. At or above the threshold it releases the
**entire** accumulated batch, preserving arrival order; it does not split overflow/remainders into exact-size
batches. Released payloads have `batch.instanceId`, `batch.correlationKey` and `batch.count` metadata. A renderer
can combine them into one document, or per-selection dispatch can feed an extensible batch renderer. The
checkpoint is deleted only after successful downstream processing and source acknowledgements.

Use one unconditional collector per flow, without other stateful orchestration nodes. Limits are 10,000 total
payloads, 16,000,000 incoming content bytes per execution, and 24,000,000 serialized checkpoint characters.
Pending batches block processing-definition edits, deletion, bulk deletion, workspace replacement and changes
to the config/secret references used by their flow. Enable/category changes and unrelated configuration remain
available. Avoid config or plugin-code changes during execution; this is not transactional snapshot isolation.

**Pending batches / orchestration checkpoints**, on each dashboard card and existing-flow designer, refreshes
safe counts, correlation, instance, update time and collecting/released status. The backing read-only endpoint
is `GET /api/flows/{flowId}/checkpoints`; it excludes bodies, filenames, receipt contents and source credentials.
A released checkpoint blocks new fetches while delivery/source-notification retries resume existing work. If
retries are exhausted or a crash leaves an ambiguous delivery outcome, the checkpoint stays visible and frozen:
review destination/source outcomes before recovery. There is deliberately no blind replay/discard button.

The default **CountOnly** mode has no automatic timeout. No collection mode provides business-message duplicate
detection, distributed transactions or exactly-once delivery. The same persisted execution retry does not append
twice, including an acknowledged-write failure; independent upstream redelivery can still duplicate messages.
A process crash outside a saved continuation is not automatically replayed. Sources without receipts cannot
receive deferred acknowledgements. Source modules must honor pending receipts to avoid refetching held messages.
Rebuild custom state-store implementations for the updated `IFlowOrchestrationStateStore.GetAllAsync(flowId, ...)`
SDK contract. Imported Billing drafts remain disabled.

### Explicit-timezone batch deadlines

Select **Minimum count or daily cutoff** (`releaseMode=CountOrDailyCutoff`), enter a 24-hour `HH:mm` cutoff,
and provide a supported Windows/IANA timezone ID (`timeZoneId`, for example `UTC`). Timed flows cannot be
enabled or fetch data without a valid explicit clock; the host timezone is never silently assumed. Disabled
drafts can retain incomplete settings for repair. The first arrival persists the next strictly future daily
cutoff as UTC; later arrivals do not slide that deadline. A skipped DST time advances to the first valid minute;
a repeated time uses its later occurrence, once for that date. These are native scheduling choices, not verified
legacy-host DST behavior.

A dedicated driver checks enabled collectors roughly every two seconds, including manual/push-only flows with
no new input. It resumes the saved batch at its collector, without re-fetching or repeating upstream maps. A
normal Run also prioritizes a due batch before fetching. Multiple due groups drain one at a time; retries take
precedence, and released/frozen batches are never blindly replayed. Disabling a flow retains deadlines/data;
re-enabling permits overdue collection release. The UI shows the persisted UTC deadline. Polling, a busy flow,
retries or service downtime can delay release; this is not a hard real-time cutoff or strict transaction boundary.
An in-flight source run may join the batch when its collector executes. No empty daily batch is synthesized.

The fixed daily mode does not read policy intervals or per-message cutoff changes. Imported orchestration
execution remains gated; a separate opt-in message/policy mode is described below.

### Message/policy batch deadlines and counted XML output

Select **Minimum count or message/policy deadline** (`releaseMode=CountOrMetadataDeadline`) and configure
**BatchTime metadata key**, **BatchInterval metadata key** and an explicit **Cutoff timezone**. Promote the
message's BatchTime into metadata before collection; metadata-mode XML rules can supply BatchInterval.
The first message requires a strict `HH:mm` BatchTime and an interval of `NONE` or positive `N:D`, `N:H`,
`N:M` (days/hours/minutes, at most 366 days). Invalid/missing facts fail before changing the checkpoint.

- The first interval is pinned across messages/restarts. A non-NONE interval overrides BatchTime, using
  local wall-clock arithmetic in the configured timezone, not necessarily an elapsed UTC duration across DST.
  Later message times/policy intervals cannot slide or change that override.
- Without an override, the first message selects the next strictly future BatchTime. A later BatchTime
  different from the **first** message replaces the time on the **existing deadline's local date**. It does
  not pick a new date from the current clock. An earlier replacement can therefore release immediately.
  Returning to the original BatchTime does not reset a previous change. These rules reflect the inspected
  trailer orchestration's timing structure, not an arbitrary inactivity timeout.
- The saved UTC deadline, original BatchTime and pinned interval survive retry/restart/manual release.
  Retry does not reapply historical timing changes. Idle release and deferred source acknowledgements use
  the same driver/safety gates as fixed daily collection. The count threshold still applies.

Native parsing deliberately rejects culture-dependent date strings, nonpositive/oversized intervals and
unknown units instead of reproducing legacy fallback guesses. DST uses the documented native gap/fold rules;
legacy-host equivalence remains unverified. Inputs are never split into multiple deadline windows within one
source run. This collector mode is not automatically wired into imported orchestrations or the separate native
ODX interpreter; it does not translate source DateTime expressions.

After collection, add **Wrap XML batch with message count** (`xml-batch-envelope-augment`). It produces one
XML payload with this default shape, retaining document roots in input order and only identical shared metadata:

```xml
<Batch xmlns="urn:mulse:batch">
  <MessageCount>2</MessageCount>
  <Messages>
    <Message><Invoice xmlns="urn:original">first</Invoice></Message>
    <Message><Invoice xmlns="urn:original">second</Invoice></Message>
  </Messages>
</Batch>
```

Root name, namespace and output filename are editable in node properties. `MessageCount` and `batch.count`
count **input payloads**, not records nested within a payload; no header/trailer is counted unless supplied as
an input payload. Empty input stays empty. Inputs are bounded to 10,000 payloads / 16,000,000 bytes, output to
24,000,000 characters; XML DTDs/external entities are prohibited, and original XML encodings/namespaces are read.
Map MessageCount into a trailer using a visual scalar map or an explicitly configured XSLT/renderer. A dynamic
allowlisted renderer can consume the whole envelope and produce header, repeated bodies and a counted trailer.
The scalar Mapping Designer still does not support repeated body mappings; use reviewed XSLT or a plugin there.

`MetadataDurableBatchTests` exercises real metadata rules, independent client deadlines, disk restart, counted
XML wrapping, leased dynamic XSLT selection, exact header/body/trailer text, delivery and deferred acknowledgements.
These are synthetic native-flow checks, not automatic translation of Billing's separate maps/custom pipelines
or BizTalk golden-output equivalence. Helpers, response/transaction semantics and imported execution remain gated.

### Durable native ODX programs

Add **Durable ODX program** (`odx-program-augment`) to a native flow for the supported persisted source subset.
Configure private source-model JSON, initialized typed facts, stable source message IDs, instance/correlation
metadata, incoming logical port, explicit timer bindings, visual allowlisted map choices, output message and
optional reply port. Case-distinct instance values remain independent; stored instance keys are digests.
The interpreter retains nested branches/loops/catches, named messages, replies and correlated listen waits
across disk restart. Idle deadlines resume without fetching or repeating upstream work. Downstream retries
reuse saved output/replies instead of repeating maps; acknowledgements wait for downstream completion.

External activities journal intent before invocation. Ambiguous effects/writes are held for reconciliation,
never blindly replayed. Pending checkpoints block unsafe edits and deletion; disabled flows retain data.
There is no operator reconciliation/discard API. Source IDs must remain unique per event within an instance.
Only one ODX node is allowed per flow, without other stateful collector nodes or conditional execution.

Explicit scalar service initializers, receive/send-initialized service and scope correlation sets, exact operations,
AND Equals/Exists filters, map-output promoted-property writes and counted XML-trailer map prefixes are supported.
Referenced source models can supply external correlation types. Leave the instance metadata key blank to route by
source tuples only when all sets initialize on the first receive; other models need an explicit instance key.
Scoped sets reset on re-entry and are removed on exit/unwind. Sends validate correlation before any external intent.
Unmatched new activations create no checkpoint; rejected arrivals never acknowledge retained source receipts.
Uninitialized/scoped scalar/non-scalar declarations and correlation parameters remain blocked.
Configure **Outbound send adapters** (`sendsJson`) for exact source Send IDs, ordered renderer-stage equivalents and
leased delivery modules. Request-port Response sends remain separate native replies. All pipeline/delivery dependencies
must be loaded before fetching. Older interpreter checkpoints require their pinned runtime; no cursor migration is guessed.
This is not automatic Billing import: schema facts, general XML/context operations, DateTime expressions, original
helpers/pipelines and full import wiring remain incomplete. Native timers use explicit seconds; the adapters do not
translate `.btp` components. Atomic/long-running transactions are rejected, not emulated by checkpoints.
Imported flows stay disabled. See [Module-Developer-Guide.md](Module-Developer-Guide.md)
for SDK contracts and limits and [BillingServiceProcess-Migration-Guide.md](BillingServiceProcess-Migration-Guide.md)
for the remaining compatibility work.

### Native control flow: branches and collection loops

Add **Native control flow** (`native-control-flow-augment`) from the augment toolbox. Properties provides
nested **Sequence**, **If** with Then/Else, **ForEach** payload and bounded **While** nodes. Add, name, reorder
and remove nodes; select installed augment/render modules and configure their descriptor settings. Renderer
pipelines, dynamic choices, XML maps and XML rule policies reuse their visual editors. Definitions save with
the flow; no ODX source, source IDs or generated DLL is required. Unsupported native/imported behavior can be
represented by an explicit **Blocked** node with a reason; even an unselected blocked branch prevents execution.

The default **Conditions** mode uses all-match native decision semantics (case-insensitive text comparisons).
These payload predicates require one payload; use ForEach around them for batches. Switch If/While to
**Expression** mode for typed scalar predicates: exact case-sensitive strings, Int32 arithmetic/comparisons,
null equality, and short-circuit And/Or/Not. Add **DeclareVariable** and **SetVariable** nodes; the recursive
operation/type/value/variable pickers require no expression text or JSON. Variables are instance-scoped,
explicitly initialized, and survive durable waits/restarts. They are not inferred message/XML fields.
Choose **MessageValue** for explicit metadata or XML facts from the working payload, or a named durable message.
XML text/count/existence selectors use exact element/attribute names and namespace URIs. The first step may be a descendant step (`//name`) and each element step may have one exact attribute filter (`[@name='value']`); there is no other XPath.
Use **Choose XML path from a sample** and click a discovered path, or add child steps manually; the sample and
its values are not saved. Metadata keys are exact/case-sensitive and missing keys fail. XML count/existence
return zero/false when absent; XML text returns the first matching node's text, or empty when absent. Named
messages must exist and contain exactly one payload; they are never substituted with the working message.
For required typed fields, choose **XmlString**, **XmlInt32** or **XmlBoolean**. These require exactly one simple,
non-nil element at every path step; missing/duplicate/invalid values fail rather than returning defaults.
Int32 and Boolean use XML Schema lexical forms, while String preserves whitespace.
For typed message context choose **MetadataInt32** or **MetadataBoolean** with an exact key; these use the same
XML lexical forms (including Boolean 1/0), not loose coercion. **Metadata** still returns a whitespace-preserving
String. All three are writable with **SetMessageValue**, requiring a matching scalar type; typed metadata writes
serialize canonical XML lexical values. Keys remain case-sensitive and missing values fail.
ForEach preserves original payload order and combines all body
outputs, with a 1-10000 input limit. While evaluates the updated output before every iteration and throws if
its configured 1-10000 limit is exceeded. Definitions allow 1000 unique nodes, 32 nested levels, 100000 node
executions and 1000000 JSON characters. Every nested module in either branch is checked/pinned before fetching.

For a Render node, **Renderer message mode** can select **Ordered named inputs to named output**. Enable
Durable execution, add/reorder 1-32 named input arguments, choose the output name and configure a renderer or
ordered renderer pipeline. Each argument must contain one payload and the renderer must return one payload.
Only the named output changes; the working batch and unrelated messages stay unchanged. Successful results
survive restart, and verified-commit recovery targets that same named output without replaying the renderer.
For multi-input XML maps, **Compose ordered XML input parts** (`xml-parts-render`) can be the first renderer
pipeline stage: configure the aggregate root name/namespace, comma-separated part names in argument order,
and output payload name. Part wrappers have no namespace; source roots/namespaces and internal XML content
are preserved. No input metadata is merged. This builds an explicit native input contract, not a guessed
BizTalk map implementation or automatic import wiring.

Enable **Durable execution** in Properties for persisted Wait/Receive nodes, named SaveMessage/UseMessage,
Reply and leased Deliver operations. Configure stable instance/message metadata keys and incoming channels;
receive filters and pinned correlation keys are editable without source code. Timer polling resumes directly
at the persisted cursor without fetching or replaying upstream nodes. Named batches and source receipts survive
restart. The deadline driver checks enabled native flows every two seconds, including after restart, so
persisted waits and timed receives progress without a new source arrival or manual run. Disabled and
migration-blocked flows are not advanced. Checkpoints expose wait deadlines/recovery holds, not message bodies or credentials. Configuration edits
and flow deletion are prevented while native work is pending. Internal delivery/reply flows need no outer route.
Add **CreateXmlMessage** to build a named XML payload from a String expression; no source metadata is implicitly
inherited. Add **SetMessageValue**, select a named target and a typed XML path (including sample-path clicks)
or exact metadata key, then choose its value expression. Writes require the target's matching scalar type.
They update only that named snapshot, not the working payload or other saved copies. Missing/duplicate/nil XML
targets, null values, unsafe XML and type mismatches fail explicitly. Construction and writes survive disk restart.
For accumulated XML batches, add **CreateXmlRoot** with a named target and String local-name/namespace expressions,
then **AppendXmlMessage** with that target and a named source from the selector. Each append deep-copies the
source's first/root element under the target root in order; namespaces, CDATA and internal comments/PIs survive.
Leading declarations/comments/PIs/whitespace are rejected, not silently skipped. Both messages must contain one
payload. XML size, reader-depth and node limits are checked before replacing the target. Other named copies and
the working payload stay unchanged, and accumulated results survive restart. These operations are generic native
capabilities; Billing-specific transforms/modules remain in the separate `Mulse.Billing` project.
Add **ApplyXmlRules**, choose a named message and use its graphical XML policy editor for typed equality
conditions and literal field assignments. It updates only that one XML snapshot, preserving metadata and other
copies over restart. No matching rule leaves the original payload unchanged; invalid facts or conflicting matched
assignments fail without partial mutation. Standalone rule previews/augments still require a match. This is a
bounded deterministic local operation, not a BRE agenda, helper interpreter or external transport effect.

Durable effects write intent before invocation. Ambiguous failures hold the instance for review rather than
blindly retrying. Checkpoint UI offers **Reconcile independently verified committed effect**: supply incident
verification evidence and exact observed output files/metadata (or explicitly verified empty output), then
confirm. The API checks the held effect identity/pinned definition and persists its result before resuming
later activities; it does not invoke the held effect again. Unknown outcomes must remain held. There is no
destructive reset/replay UI and no exactly-once/DTC/rollback claim. Synchronous
blocks retain whole-step retries. Nested stateful collectors/interpreters and recursive native blocks are rejected.
Existing ODX checkpoints remain supported and are not migrated/deleted.

The BizTalk import panel defaults to **Generate native control-flow draft** (API opt-in `generateNativeFlow`).
Safe ordered containers, receives, exact named-message copies, uniquely bound sends and replies become native
nodes. Supported scalar declarations/assignments, ordered first-match decisions and bounded loops translate to
native expression trees, not stored source code. Complete constant XPath count/string/boolean reads of declared
messages translate only for absolute child/final-attribute paths with exact expanded names, an optional leading `//` and one exact `[@a='v']` filter per element step; ToInt32(count(...))
is supported. Exact distinguished members now resolve through uniquely owned declared XSDs, including explicit
relative imports of other declared schema files. Supported built-in String/Int32/Boolean members compile to native
typed selectors and writes. Complete XmlDocument.LoadXml/copy/member-assignment blocks compile to native nodes;
Construct-owned message references retain their source identity as structural output declarations. Complete
empty-prefix CreateElement/AppendChild root construction and deep ImportNode(message.FirstChild, true) appends
now compile to native XML composition. An exact temporary XmlElement may be eliminated only inside its owning
service/scope and one complete root-construction block; scope/transaction execution is not inferred. Scalar defaults
are null String, zero Int32 and false Boolean. Comments do not hide unsupported suffixes or final assignments.
Exact message(property) reads/writes now compile from uniquely declared property schemas with explicit built-in
String/Int32/Boolean types and proven generated namespaces, using native `{namespace}property` metadata keys.
Undeclared properties, shadowed messages, ambiguous identities and unsupported types remain
blocked. The exact allowlisted adapter/HL7 assembler symbols (`BTAHL7Schemas.*`, `FTP.*`, `SMTP.*`, `MIME.FileName`, ...)
can be written only, as importer-defined metadata no delivery module reads, and the exact HL7vXGeneric multipart
becomes one native `HL7Multipart` XML message; see the compatibility plan.
XML field or creates MessageBox subscriptions. No source interpreter runs.
Arbitrary XPath, unproven schema/context properties, other CLR calls, helpers, scopes, pipelines and unproven
policy fact wiring remain explicit structured blockers. Imported loop safety bounds are visible
and editable; reaching a bound fails, never silently truncates the source loop.
In the policy report, choose a version and **Use selected exported version as native import test target** to
reanalyze. The API accepts exact policy-name-to-`major.minor` selections in `nativePolicyVersions`; it never
silently selects the latest or substitutes a missing version. A CallRules call compiles only with one exact,
unshadowed message/fact alias, schema-proven direct-child String/Boolean fields, a complete selected proposal
and no potentially conflicting rule pairs. Repeated same-typed literal setters within one source rule preserve
the final action's value; conflicting separate rules remain blocked. Selected policy JSON is pinned in the
native node and remains editable graphically. A selected test target is not evidence of its deployed version.
Unproven calls retain proposal evidence and blockers. Download native JSON with the existing artifact controls.
Imported flows remain disabled; this is not complete Billing translation or verified Billing business parity.

For source configuration getters, optionally select **Host configuration test snapshot** and explicit **Pure
configuration getter source files** in the import panel (API `nativeHostConfigurationPath` and
`nativeConfigurationHelperPaths`). Only entire simple static classes of literal keys and parameterless
`ConfigurationManager.AppSettings` getters returning `string.Empty` when absent are recognized. Referenced
results become pinned, editable native literals; execution never reads the server file or loads the helper DLL.
Absent keys are empty only because the selected getter proves that behavior, not a generic default. External
configuration, overrides, duplicate identities and unsupported source suffixes fail instead of partially compiling.
Use nonsensitive settings only: referenced values are saved in flow JSON. Selection is test evidence, not proof
of deployed assemblies or inherited machine settings. Scratch flows use the existing String literal controls.
Choose **InstanceId** in the native expression editor for the stable durable instance GUID; it survives restart
and is not the run ID or a newly generated value per evaluation. Exact source RootService.InstanceId string reads
translate to this new native identity, never to an alleged legacy BizTalk ID. Durable execution is required.

### Optional Billing helper plugin (CommonBilling field read only)

`Mulse.Billing` is a separate hot-loadable `net10.0` project referencing only the module SDK, not BizTalk or
`Service`. Building the solution places it under `Service\plugins\Mulse.Billing`. In **Modules**, load/enable
that package explicitly; discovery alone does not load it or enable imported flows. Once loaded, its descriptor
controls are available for both scratch flows and native node authoring:

- **Read CommonBilling field**: choose one of 13 nonsensitive source-schema fields and **Custom metadata key**
  (the existing default) or **Declared Billing context property**. The latter explicitly projects to the selected
  field's source `{namespace}property` key; IsBatch is validated/normalized as Boolean. It overwrites that one key;
  do not reproject after intentional context changes. The exact Billing root namespace and unqualified
  section/field names are required. Missing, duplicate, complex, nil/attributed and oversized values fail.
  Username/password/certificate fields are not exposed.
- **Expand filename template** (`filename-template-augment`, built-in Compatibility Pack): choose literal or exact metadata
  template, explicit time zone/culture, and an output key. It changes metadata only, not the payload name, destination or body.
- **Filename template macros** (`filename-template-transform`, built-in): the same expansion as a one-text-input function in
  **Map parsed data** and the native `InvokeTransform` node.
- **Address part** (`address-part-transform`, built-in): splits a URL (`scheme://host[:port]/dir/file`), a file path or a mail
  address into Host/Port/Directory/FileName/MailAddress for the native `InvokeTransform` node; invalid input fails. Together with
  Deliver **Dynamic settings** (computed per-run module settings) it implements BizTalk dynamic send ports natively.
- **XML - remove namespaces** (`xml-remove-namespaces-render`, built-in): select this renderer directly or as an
  **Ordered renderer pipeline** stage. Text, CDATA, comments and processing instructions survive; attribute local-name collisions fail.
- **Set context property** (`set-context-property-augment`, built-in) and **Promote HL7 message type**
  (`hl7-message-type-augment`, `Mulse.Hl7` package; enable it like `hl7-xml-render`) replace the BizTalk `SetProperty` and
  `HL7TypeCaster` pipeline components.

Only **Read CommonBilling field** is Billing-specific. The generic modules above were renamed from `billing-*` IDs with no
alias, so a saved flow that still references an old ID fails closed until it is re-selected.

Filename macros are case-sensitive `[DATE(format)]`, `[DATETIME(format)]`, `[TIME(format)]`,
`[UTCDATETIME(format)]`, `[UTCTIME(format)]`, `[DAY(length)]`, `[GUID]`, `[TICKS]`, `[SECONDS]`.
Templates/output are limited to 4096 characters; date formats accept letters, digits, underscore, dot, comma
and hyphen. DAY uses uppercase English names (0 or greater than 6 = full name); SECONDS is rounded elapsed
local wall-clock seconds since **2001-01-01**, not Unix time. Unlike the original helper, time is one execution-start
snapshot and zone/culture are explicit; invalid known tokens/formats fail rather than returning placeholders.
Identical GUID tokens within one expansion agree. Use the augment inside a durable native block to retain its
successful result across waits/restarts; an independently retried mapping or `InvokeTransform` call is not a deterministic GUID
generator, so checkpoint the result before a Deliver that must be replay-stable.

Synthetic tests verify actual plugin loading/unloading, host mapper invocation, UI controls and persisted native
field-read → GUID filename → wait/restart → namespace-removal composition. This is not imported Billing parity:
helper call-site translation, policy/fact wiring, selected partner maps, remaining pipeline components,
outbound/scoped correlations and transaction semantics remain blocked. No legacy helper assemblies are run.

### Ordered renderer pipelines for scratch flows

Choose **Ordered renderer pipeline** (`render-pipeline`) as a route's renderer, or as a dynamic map/renderer
choice. In Properties, click **Add renderer stage**, choose installed renderers, edit their settings, and use
**Move stage earlier/later** to arrange execution. Each stage receives the entire previous output batch.
One to 32 stages are supported; nested pipelines and dispatch stages are deliberately rejected. These are
ordinary native flows: no Billing source, import report or handwritten flow JSON is required.

The **Durable ODX program** Properties now include **Outbound sends**: select a named source send, choose its
loaded delivery module, configure settings/response capture, and use the same stage editor for its outbound
pipeline. Replies are excluded from that selector. Missing modules, missing sends and malformed definitions
remain visible rather than silently disappearing. Source program/expressions still use private typed JSON;
durable graphical control flow and transaction-reconciliation UI remain future work. The synchronous native
branch/loop editor above is independent of this ODX-specific editor.

Pipeline stage and send settings support stored-reference pickers. JSON string substitutions preserve quotes,
backslashes and nested stage definitions; stored values are not recursively expanded. All configured renderer
and delivery dependencies are checked before a flow fetches data. These native pipelines do not implement
untranslated BizTalk pipeline components or change the imported-flow execution gate.

### Confirmed early release

For an enabled native flow, checkpoint inspection offers **Release collected batch...**, then **Confirm release**.
This releases the inspected collecting instance before its count/deadline without fetching or repeating upstream
maps. The current payload count must still match inspection; new arrivals invalidate confirmation. Other clients'
collections are not released. This makes an undersized count-only batch finishable without deleting its data or
changing a pending flow's settings. The UI displays the real `Completed`/`Retrying` outcome and any failure detail.

The API is `POST /api/flows/{flowId}/batches/{instanceId}/release` with `confirmInstanceId` matching the path and
`expectedPayloadCount` matching inspection. Pending retries, already-released batches, disabled/import-gated flows
and incompatible collector settings block release. Release intent is persisted **before** downstream effects;
an interrupted/ambiguous intent write freezes the instance for review rather than making another click replay it.
Existing delivery/source retries continue normally after release. This is first delivery of a collecting batch,
not recovery/replay of an already-released instance. No blind replay or destructive discard UI is provided.

### Visual metadata promotion

The `metadata-promotion-augment` node now has rows for metadata key, selector, fallback and multi-value join
separator, plus editable XML namespace prefix/URI entries. JSON selectors use the existing `$` dot-path
syntax and `[]` array expansion (for example `$.groups[]`), not full JSONPath. XML uses XPath expressions
with explicit namespace prefixes. This reads each payload and preserves its body. Blank selected values are
filtered out; fallback applies only when no values remain. Blank final values are skipped. Existing keys
are preserved unless **Overwrite existing metadata** is enabled; row order still matters for duplicate keys.

For example, promote `/s:Order/s:Group` into `rules.GroupID`, configure `s` to the input namespace, evaluate
a metadata-mode policy, then render-dispatch using `rules.MapName`. All settings are saved with the flow;
no JSON editing is needed for this chain. Advanced promotion/namespace JSON remains available. Unsupported
shapes/properties are preserved for repair rather than silently replaced. Visual editing is bounded to 512
rows, 1,000,000 promotion JSON characters and 65,536 namespace JSON characters; these are editor limits,
not a claim that arbitrary advanced definitions have been validated by opening the editor.

### Dynamic map / renderer selection

Add/select the **Select map / renderer** render module (`render-dispatch`). Configure its metadata key and
visual renderer choices: exact selection value, loaded renderer, and descriptor-driven settings. XML mapping
choices reuse the visual XML mapper. A preceding decision, metadata promotion, or metadata-mode XML policy
sets the selection key. By default (`batchMode=PerPayload`), each payload is rendered separately in input
order; renderers that combine inputs therefore combine only that one payload. No implicit batch grouping or
first-match/default route is applied. All selections are checked before invoking any renderer;
missing/unknown values fail explicitly. Renderer failures propagate; delivery remains the responsibility
of the route after rendering.

Optionally choose **Group current batch by selection** (`batchMode=PerSelection`). Each distinct selection
value is passed to its renderer once, with its input messages in original order. Groups execute in first-seen
order, so nonadjacent messages can be reordered in the output. Choices that use the same renderer remain
separate. Use distinct selection values for client groups that must not mix. The renderer determines whether
to combine, drop or expand its inputs; grouping alone does not force a single output document. All inputs
still prevalidate before any group is rendered. Unsupported grouping modes fail explicitly. This only groups
inputs already in the current execution: no cross-run buffering, timer, count threshold or durable batch
completion is implied.

The hot-loadable sample package supplies **Combine UTF-8 text with header / trailer**
(`sample-text-batch-render`) as a concrete combining renderer. Select it with per-selection grouping to
produce one text document per choice, with descriptor-driven literal header/separator/trailer controls.
Bodies must be valid UTF-8; invalid bytes fail, rather than being replaced. Header/trailer include exactly
the configured text (no automatic newline, counts, totals, field expansion or encoding conversion); the
separator appears only between bodies. Each literal is limited to 65,536 characters and total output to
4,000,000 characters, including literals. Empty batches produce no file. An optional single output filename
can be set; otherwise the first input filename gets `.batch.txt`. Use distinct filenames/destinations for
separate groups when needed. Only identically shared metadata is retained; per-message IDs are not copied
from an arbitrary first message. `batch.count` and `renderedBy` identify the actual combination. This example
does not execute the original Billing batch orchestrations or custom pipelines.

Choices persist as `casesJson` with `value`, `module`, and `settings`. Advanced JSON preserves unsupported
content for repair. Changing a renderer explicitly clears that choice's settings. Limits: 1-128 choices,
262,144 JSON characters, 2,048 characters per selection value, 128 settings per choice. Values are ordinal,
case-sensitive. Nested `render-dispatch` is rejected. All renderer choices, including config-backed IDs,
participate in missing-module preflight, package pinning and unload protection, not just the current selection.
This is native configurable dispatch, not automatic translation of BizTalk dynamic map classes or pipelines.

For mapping before later processing, add **Select map during processing** (`map-dispatch-augment`) instead.
It uses the same visual choices editor, leases and dependency protection. Its selected renderer's output
becomes the working payload for subsequent augments; output format, filename and metadata are determined
by that renderer. It performs no delivery. A flow can normalize with one map, promote facts from the new
body, run a metadata policy, and select another map at final rendering. Later nodes must accept the chosen
format. Zero/multiple renderer outputs drop/expand the working batch just as they do at final rendering.
This does not add loops, automatic grouping, transactional message scopes or BizTalk pipeline execution.

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

Take a database backup first (or a state-file backup when explicitly using the legacy JSON provider).
Turning encryption on atomically migrates persisted payloads to AES-GCM; turning it off requires
confirmation and rewrites payloads without encryption. Changes serialize with other state writes and
survive restarts. Saved mode overrides the original `Mulse:Encryption:Enabled` default.
The database stores the mode/key ID in workspace metadata and encrypts all payload records, including
historical revisions. Queryable flow metadata and scheduling indexes remain unencrypted; see
**Encryption and backups** below. In legacy JSON mode, a versioned file envelope records the mode;
its disabled payload is base64-encoded JSON, not encrypted.

Keep the same environment key available while encryption is on. A missing/wrong key or damaged
encrypted payload fails startup rather than replacing your data with an empty workspace. Workspace
export encryption uses its own password, independently of this switch. At-rest encryption does not
protect live API/UI access, logs, or other files produced by modules.

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

In the designer, choose **Import from BizTalk** and supply a `.sln`, `.btproj`, custom project, folder,
or `.odx` path **accessible to the Service host**. An `.odx` must be explicitly included by exactly one
sibling `.btproj`; the complete owning project is analyzed, including its other orchestration candidates.
Missing/ambiguous ownership is rejected instead of guessed. For linked/nested orchestration files,
select the owning project or solution explicitly. This is path-based analysis, not a browser project upload.
Add semicolon-separated additional solution/project/folder paths to resolve cross-solution references,
and binding file/directory paths when deployment bindings live outside the source tree.
Use **Rule policy XML exports** for explicit BRL policy/vocabulary export files (`additionalRulePaths` in the API).
Analyze, import, and scaffold-download requests preserve additional source, binding, and rule paths.

A policy is a collection of IF / THEN rules: conditions read input fields and assignments set output values.
Billing rules select map names and processing settings; they do not themselves run maps or send messages.
The UI separates **Policy name** and **Exported policy version**. A sole named policy is preselected;
choosing a name initially selects its highest numeric major/minor exported version as an explicit starting
assumption, not evidence of what BizTalk deploys. Multiple named policies require a name selection.
Every version remains selectable, including unsupported versions (which cannot be applied and never silently
fall back). Apply a proposal to add a node, or use **Choose an imported policy / version for this node**
to explicitly replace an existing node's rules, including local edits. Each node saves an independent,
pinned editable snapshot; reimporting does not upgrade saved nodes. Reanalyze exports to populate the
selectors after reopening; the saved editor works without the export. Editing a snapshot's version label
does not switch rulesets. The editor includes plain-language help and no-delivery synthetic preview.

The implementation in `Service\BizTalkImport` can:

- Enumerate orchestrations, maps, schemas, pipelines, and custom assemblies.
- Recognize an application-wide binding export by its explicit `ModuleRef` assembly ownership, regardless
  of filename, and scope each orchestration draft to its own referenced physical send/receive ports.
  Direct MessageBox subscriptions, distribution lists and dynamic destinations are not inferred from
  these references. Exports without service metadata retain the older path/name-based analysis heuristics.
- Inventory every named/versioned policy in supplied BRL XML exports without selecting a deployed
  version. One policy version can contain rules for many concurrent clients/groups; versions are not clients.
  Concrete one-document, direct-child XML string/boolean equality predicates and literal assignments
  produce editable **native proposals**; literal-only equalities are folded into an editable constant guard.
  Vocabulary links are labels; executable member definitions must be explicit. Unsupported expressions,
  custom policy configuration, mixed facts, or forward-chaining dependencies produce diagnostics instead
  of partial executable policies. Called policy/version evidence is retained in migration reports.
- Keep same-named projects separate by full path, resolve explicit `ProjectReference` paths and infer
  `HintPath` ownership inside the analyzed workspace, then fall back to names only when unique and no
  path was supplied. Ambiguous/unresolved references are reported, not guessed. The import panel's
  **Project dependencies** report shows the source evidence, resolution method, and candidate paths.
  Include referenced projects in the source paths; projects outside the analyzed scope are not loaded
  automatically. MSBuild expressions and shared/external output directories require manual review.
- Generate distinct draft IDs (and configuration reference prefixes) for colliding project/orchestration
  names, with each candidate showing its original project path. Collision suffixes are stable for the
  same analyzed paths; moving projects or changing the workspace can change them.
- Auto-translate simple orchestration `Decision` branches (including `Regex.IsMatch(...)` conditions) into
  declarative decision rules, and scaffold a starter C# module for anything too complex (correlations,
  convoys, transactions, `Loop`/`While` shapes) rather than guessing. New orchestration stubs compile
  against the SDK but throw `NotSupportedException` until implemented; they never silently pass data
  through. Previously downloaded stubs are not changed; re-download and review them.
- Preserve an **Orchestration source inventory** in each artifact's migration report: designer shape
  counts, IDs/parent IDs, original source lines, names and selected type/port/message references.
  Document order is not executable ordering. Coded blockers identify assignments/transforms, BRE calls,
  loops, ports/calls, failure scopes and waits/correlation. Metadata parse/size failures are explicit;
  source expressions are not executed or copied into this inventory. The inventory is an import-time
  snapshot retained through ordinary save/reopen/duplication; reanalyze to inspect changed source.
- Best-effort translate `.btm` maps into XSLT for direct field links/constants, and clearly call out
  functoid-involved links (with their functoid type IDs) that need manual review instead of silently
  guessing at functoid semantics.
- Extract promoted/written context properties from custom pipeline component source (e.g.
  `context.Promote(...)`/`context.Write(...)` calls) to pre-populate metadata-promotion routing.
- Produce an editable, disabled Mulse draft plus settings/requirements and warnings. Imported drafts
  cannot be enabled or run until verified translation support exists; configuration review alone is not approval.
- Show a **Migration report** in each import candidate and the designer's **BizTalk import follow-up**
  panel, including after saving and reopening. The versioned report preserves source paths, artifact
  statuses, coded blocking/review diagnostics, original warnings, and suggested next actions through
  edits, duplication, disk reload, and workspace backup/restore. It also lists `.btm` maps found in the
  analyzed source scope with their owning project, schema references, direct-link/constant/functoid counts,
  and limitations. This is a workspace candidate inventory, not evidence that the orchestration's deployed
  policy selects a map; no map is automatically wired or approved. Older updates omitting the report do
  not erase existing provenance; manually-created flows need no report.

Reports are **import-time snapshots, not execution approval**. Configuration or canvas edits do not
clear diagnostics automatically. Starter XSLT is not installed/wired, generated C# stubs are not completed
translations, and no artifact is marked fully translated. **Every flow carrying migration metadata is
blocked from enable/run**, regardless of submitted statuses or diagnostic counts. Creating or updating
an enabled import, enabling an import, or running it returns HTTP 409; scheduled processing, push
registration, and processing retries are blocked too. Saved provenance cannot be replaced through a
flow update (409); omitting it preserves the saved report. Dashboard and designer controls explain the
block. There is no manual approval override.

Apply a candidate to the canvas, review its transport paths/credentials, mappings, retry/compensation
settings and warnings, then explicitly save a disabled draft. Suggested scaffolds can be downloaded and
implemented, but that alone does not remove the gate: verified translation/revalidation support is still
pending. Editing, disabling, categorizing disabled drafts, duplication, and backup/restore remain available.
Terminal source acknowledgements for already-completed work may finish without replaying processing or
deliveries; the gate does not cancel an execution already in progress. Scratch-built flows without
migration metadata retain their existing behavior.

Historical imports without migration metadata need reanalysis/re-import to receive this gate. The gate
cannot infer provenance from manually copied definitions or direct workspace/backup edits that remove
metadata; it is a workflow safety policy, not an authorization boundary against workspace editors.

See [the compatibility plan and implementation status](BizTalk-Import-Compatibility-Plan.md) for verified
increments and remaining gaps, including schema-based mapping translation and verified approvals.
The [BillingServiceProcess migration guide](BillingServiceProcess-Migration-Guide.md) separates the
currently available disabled import from implementation prerequisites, BizTalk-team evidence, validation
and eventual cutover. Billing is not currently an executable equivalent merely by importing it.

### Native XML field rules

In the BizTalk import panel, explicitly select a policy version, inspect/edit its client rules, and preview
synthetic XML facts before choosing **Add edited rule proposal to current canvas**. Load the target flow
first; adding a proposal does not construct its fact document or translate an orchestration `CallRules` site.
Configure the new `xml-rules-augment` node's input source and execution order explicitly. The same module
and visual editor are available for scratch-built flows. Edits are saved in `policyJson`; samples and preview
outputs are not saved. Treat imported literal values as potentially sensitive workspace data.

All field conditions use case-sensitive string equality or XML booleans (`true`/`false`/`1`/`0`). Fields must
be single existing simple children with the exact configured namespace. All matching rules contribute
assignments; identical assignments can coexist, but conflicting values/types fail explicitly. Priority only
orders inspection, never silently chooses a winner. No-match, missing/duplicate/complex fields, unsupported
types, condition/write dependencies, unsafe XML, and limit violations fail explicitly. This is a bounded,
single-pass native rule engine, **not** BRE agenda/forward-chaining equivalence. Import diagnostics flag
potential overlapping conflicting rules. A native proposal never clears an imported flow's execution gate.

The node's **Policy input / output** defaults to **XML fact document**, preserving existing behavior.
Choose **Metadata facts; preserve message body** to build facts from metadata and return only actual rule
assignments to metadata. With prefix `rules.`, conditions read `rules.GroupID`, `rules.IsBatch`, etc., and
assignments write `rules.MapName`, etc. Blank prefix uses unprefixed field names. Every active condition
field must be present; assignment-only fields are created internally. Field local names must be distinct
case-insensitively, including across namespaces; collisions fail instead of overwriting one another.
Existing assigned keys are overwritten intentionally; unassigned metadata, original bytes, filename and
content type are preserved. Inactive/statically-false rules require no facts. Prefixes are limited to 128
characters; fact limits still apply. Connect `rules.MapName` to `render-dispatch` for a native policy-to-map
chain without replacing the message with the IntegrationRules fact document. Populate input metadata and
configure every allowed renderer/map explicitly; importing alone does not wire the Billing orchestration.

Preview: `POST /api/biztalk-import/rules/preview` with `policyJson` and `inputXml` returns `outputXml`,
`matchedRules` and actual typed `assignments` using the exact runtime evaluator. The editor displays applied
assignments. Preview accepts synthetic XML facts even when the node uses metadata mode; it does not fetch
metadata from live messages. It performs no fetch, delivery, schema-file loading, external code execution,
or configuration/secret resolution. Limits: 32 export paths; 32 million XML
characters and 256 versions per export; 2 million policy JSON characters; 512 rules, 32 conditions and
64 assignments per rule; 1 million XML input/output characters and 4,096 fact-root children.

### Mapping parsed data (Excel, JSON, flat files, HL7, XML and plugin output)

Add **Map parsed data** from the Toolbox or **Add parsed-data map** in the Mapping workspace.
This standalone `parsed-data-map-augment` runs after parsing, in augment order, before rendering;
it needs no join or second source. Choose a parse-node source or the current working batch.
Unselected sources pass through unchanged, so review route conditions when several sources exist.
Renaming a parse node updates its map reference; removing one requires choosing a replacement before saving.

1. Enter input fields manually with **Add input**, or open **Sample / parsed data and preview**.
   Paste/upload already parsed JSON/XML (choose Text for plain parsed text), or select **Raw sample through
   source parser**, choose the source parser, and supply a sample file. Excel samples use the selected
   parser's sheet/header settings; headerless worksheets expose numeric cell paths such as `/0`.
   Raw sample parsing explicitly executes that installed parser with its current settings, including any
   resources it requires; it does not fetch live input, run augments, render or deliver. Config/secret
   references are not resolved for sample parsing. Use trusted parsers and non-sensitive sample data.
2. Choose **Discover input fields**. Fields are unioned across sample records/payloads, not just the first
   row. For nested records, set **Record path** (for example `/data/rows`) and discover again. Mulse's
   `flat-file-parse` wraps its rows in `/records`; Excel/JSON root arrays use an empty record path.
   The selected array yields one output record per item; an object/scalar/null yields one. Empty arrays
   remain empty. Samples with several parsed payloads offer a payload selector for preview.
3. Add output fields manually or discover them from a structured target sample. Click an input (including
   **Whole record**) or drag it to **Connections** to create and select a mapping. Click an output to assign
   the selected mapping, drag a connection onto an output, or drag an output onto a connection. Dragging an
   input directly onto an output creates a mapping too. **Select mapping** selects an existing connection;
   **Add mapping / Add constant** remain available. Constant editors offer Text, Number, Boolean and Null;
   text does not need JSON quotes. Advanced JSON remains available for existing structured constants.
   Choose **Reads from: Transform** to use any installed transform module, including plugin transforms.
   Configure its settings and ordered field/constant inputs; use **Move up / Move down** to reorder.
   Dropping an input onto a transform adds an argument. Use **Transform chain** to compose several transforms.
   Each connection supports missing-input Error (default), Omit or Null. For transforms this applies to
   every field argument: Omit skips the output and remaining chain, while Null passes JSON null.
   Present null/empty values are not missing. Nested output paths create objects; duplicate/overlapping
   assignments within a template fail explicitly. Objects/arrays can be copied intact or reshaped using
   Object template / Repeating array child mappings below.
4. **Preview parsed-data mapping**, review its JSON result, then select an output renderer (Excel, JSON,
   XML or a compatible plugin) and delivery route in Flow. Mapping emits a JSON array per input payload.
   Renderer-specific shape restrictions still apply: Excel requires flat cell values; for a renderer
   expecting one object per payload (such as HL7), add **Debatch** after the map with JSON array mode and
   an empty array path. **Save flow / Create flow** persists fields and connections, not sample values or
   previews. Imported-flow execution blocks remain unchanged.

#### Visual nested templates, conditions and transform chains

All features in this milestone are editable through forms; the Mapping JSON panel is optional.
Existing version-1 field/constant/transform mappings continue to work without conversion.

- **Object template** selects an input value and constructs an object using **Edit child mappings**.
  **Repeating array** selects an array and constructs one object per item, in source order. Templates can
  nest, including arrays inside array items. Empty arrays stay empty; null input stays null without
  running children. A non-array input to Repeating array is an explicit error, not an implicit singleton.
  XML normalization creates arrays only for repeated siblings; a singleton XML element needs an Object
  template or an upstream normalization step. Scalar array items can be mapped via Whole record.
- The **Editing** breadcrumb identifies the output template. **Back to parent mappings** returns one level.
  Input/output paths, manually added fields and target samples apply to the current template. Discovered
  sample paths are projected into child input palettes, removing sample array indices. Source scope
  **Current** reads the selected object/item; **Parent** reads the input of its containing template;
  **Root** reads the original record selected by Record path (not the entire payload). At the root,
  Current, Parent and Root all refer to that record. Target paths are always relative to the output template.
- **Conditions** combine tests using All or Any; no tests means unconditional mapping. Tests run against
  the containing input before selecting the mapping source or invoking transforms/children. Exists includes
  present null/empty values; Missing means the path is absent. Other operators do not match absent paths.
  Equals/NotEquals compare JSON types and structures, text tests are ordinal/case-sensitive, and numeric
  comparisons accept JSON numbers or invariant numeric strings. Invalid comparison types fail explicitly.
  When tests fail, choose Omit, Write null or Write constant; this branch bypasses source reads and chains.
- **Transform chain** runs after the mapped field/constant/transform/object/array value. **Add chain step**
  defaults to a Previous result input. Each step may combine that result with scoped fields and constants,
  using the same installed-module selector and settings as an ordinary transform. Move step up/down changes
  evaluation order; Move up/down changes argument order. Dropping an input onto a chain step adds its argument.
  Inline recursive transform arguments are not supported; compose transforms with ordered chain steps.

This is the first milestone toward an XSLT 1.0-plus visual mapper, not full XSLT parity. General XPath,
recursive/reusable named templates, XML mixed-content preservation, sorting/grouping, multiple result
outputs and full XSLT 3.0 support are not implemented by the parsed-data mapper. Multi-source joins remain
available through the existing join/map augment. Use the native XML mapper for XML-specific contracts.

Preview invokes the selected installed transforms with sample data, just as runtime mapping does, but
never fetches or delivers. Transforms are trusted plugin code; use non-sensitive samples. Config/secret
references in transform settings are resolved during flow execution, not sample preview. Transform module
dependencies, including child templates and every chain stage, are checked before enabling/running a flow
and pinned with the flow's other modules.

For example, a transform field in Mapping JSON can combine a record value and a typed constant:
```json
{
  "kind": "Transform",
  "targetPath": "/label",
  "transformModule": "concat-transform",
  "transformSettings": { "separator": "-" },
  "transformInputs": [
    { "kind": "Field", "sourcePath": "/id" },
    { "kind": "Constant", "constantJson": "\"mapped\"" }
  ]
}
```

Paths are exact, case-sensitive JSON Pointers: `/customer/name` traverses an object; `/0` selects an array
cell; `~1` escapes a slash and `~0` a tilde inside a property name. Dots/spaces in Excel column names are
literal (for example `/Order.Id`). Empty input path means the whole selected record. Numeric target
segments are object property names, not array constructors. XML is normalized into a structured object:
expanded names `{namespace-uri}name` avoid prefix ambiguity, attributes use `@name`, repeated sibling
names form arrays, leaf values are strings and text alongside children/attributes uses `#text`.
Escape slashes in namespace URIs in pointer segments. This is data mapping, not lossless XML round-tripping
(comments, processing instructions and mixed-content ordering are not preserved); use the native XML
mapper below when XML-specific output contracts are required. Binary formats need a parser that emits
JSON, XML or text; raw binary is not treated as fields.

Mapping definitions are versioned in the augment's `mappingJson`. Limits are 256 mappings total across
all templates, 8 mapping levels, 256 palette paths/transform arguments per list, 32 conditions per mapping,
16 chain steps per mapping, 32 path segments (also across nested output templates), 262144 mapping characters,
1000000 parsed-input characters and 4000000 output/intermediate construction characters. Execution is capped
at 100000 evaluation operations per payload, counting template visits, mappings, conditions, transform inputs
and transform invocations; cancellation is checked throughout. Oversized nested expansion fails explicitly
rather than truncating output. Raw binary sample uploads are limited to 10 MiB. Discovery rejects oversized
schemas rather than silently truncating them; use a smaller sample or manual paths. Preview and execution use
the same mapping engine.
API clients can POST `/api/flow-designer/parsed-mapping/inspect` and
`/api/flow-designer/parsed-mapping/preview` without executing a flow.

### Native XML Mapping Designer

In **Flow Designer > Mapping**, choose **Add XML mapping render**. The builder has **Flow / Mapping**
workspace tabs: Mapping uses the full work area instead of floating over the flow canvas. Choose the mapping
step from the selector to work on one map at a time. This adds a render-only route (or fills a route missing
a render); add a deliver node before saving. It works for scratch-built flows and disabled imported drafts.
Existing native map nodes also offer **Edit XML mapping** in Properties, opening that map in Mapping.
Existing join field mappings remain in Flow Properties, linked from the Mapping workspace.
Switching tabs or map selection preserves transient schema inputs, sample XML, previews and toolbox
positions during the editing session; only **Create flow / Save flow** persists flow/mapping definitions.
Use Left/Right or Home/End on the workspace tabs to navigate by keyboard.

Explanatory guidance is behind **?** help controls: hover, focus with Tab, or click/tap to keep help open;
press Escape or click again to dismiss. Errors, import-execution blocks and unsaved status remain visible.
**Map settings**, **Field settings**, **Mapping definition JSON** and **Test mapping** are collapsible.
Selecting a visual connection/operation or adding a field opens its field settings automatically.

The **Visual mapping designer** shows hierarchical source paths on the left, target paths on the right,
connection lines and ordered operation nodes in the middle. Inspect source/target XSDs in the section above
it, select a supported scalar field on each side, then choose **Connect fields**. Repeating/unsupported
fields remain visible but disabled. Duplicate/overlapping targets, incompatible roots and namespace limits
are rejected without changing the saved map; matching namespace URIs reuse existing prefixes.
Optional-source to optional-target links start with Omit; review that policy in field settings.
Select a connection or operation node to show that field's settings below the canvas; use **Add operation**
to extend its chain, **Remove connection** to delete its assignment, or **Show all field settings** to return
to the full editor. Constants and manually entered paths also appear visually. Connections are created by
click selection (keyboard-accessible buttons), not by dragging; canvas layout is automatic, not freeform.
Attached validation XSDs reload on reopen, with visible errors and an explicit retry action. Inspected
schemas are labeled separately and are not attached automatically. Without XSDs, existing saved paths
still appear, but inspect schemas before adding visual links. The scrollable canvas and field forms edit
the same definition; there is no separate unsaved graph or execution-approval switch.

Configure the target root, namespace prefixes and field mappings, then **Create flow / Save flow**.
The versioned model is saved in that node's `mappingJson` setting and survives reopen and duplication.
Source paths are absolute named paths such as `/s:Order/s:Id` or `/s:Order/@id`; target paths are relative
to the target root, such as `t:Header/t:Number` or `@id`. Define `s` and `t` namespace URIs explicitly;
unprefixed names select/create names in **no namespace**. Text constants need no JSON quoting in the
field editor and may be empty. For example, a saved definition can be:

```json
{
  "version": 1,
  "targetRoot": "t:Result",
  "namespaces": { "s": "urn:source", "t": "urn:target" },
  "fields": [
    { "sourcePath": "/s:Order/s:Id", "targetPath": "t:Number" },
    { "targetPath": "@status", "constant": "new" }
  ]
}
```

**Preview XML mapping** shows output XML and generated XSLT using the same engine as `xml-mapping-render`.
Sample XML and preview results are not saved, and edits invalidate results. The preview API is
`POST /api/flow-designer/xml-mapping/preview` with `mappingJson` (a JSON string) and `sampleXml`.
It does not run the flow, fetch data, deliver output, or approve an imported draft.

Version 1 requires exactly one scalar element/attribute per source field, including empty values.
Version 2 adds explicit **Error / Omit / Default** missing-source policies. Omit suppresses an absent
field; Default inserts the supplied literal only when absent. Present empty values are not missing.
Repeated/complex values, duplicate/overlapping targets, unknown versions, undeclared prefixes,
XPath functions/predicates/wildcards and DTDs fail explicitly. These are presence conditions, not
arbitrary expressions or record loops.

Version 2 also supports ordered **Add string operation / Move up / Move down / Remove** controls on
source fields and constants. NormalizeWhitespace trims/collapses XML spaces, tabs and line breaks only;
UppercaseAscii/LowercaseAscii leave non-ASCII letters unchanged. Prefix/Suffix take literal text, never
executable XPath. Operations run top to bottom after selecting the source, constant or default; omitted
fields stay omitted. Adding an operation upgrades to version 2. Each field permits at most 16 operations;
prefix/suffix arguments allow at most 4,096 characters. Unknown/null/invalid operations fail explicitly.
For example, `"operations": [{ "kind": "UppercaseAscii" }, { "kind": "Prefix", "argument": "id:" }]`
produces `id:ABC` from `abc`. Chains persist through normal flow save/reopen and schema attach/remove.
These are explicitly authored native operations, not automatic BizTalk functoid translation.

Every native editor exposes **XSD schema trees and BizTalk map import**:

1. Paste XSD text or choose multiple `.xsd` files. Inspect source and target trees, namespaces,
   cardinality and unsupported shapes; select scalar fields to add editable named-path mappings.
2. Optionally select **Use supplied XSDs for runtime validation**. This explicitly saves both schema
   sets with the map; uploading/inspecting alone does not. Preview and actual rendering validate input
   and output using these saved schemas. Validation can also be explicitly removed in the editor.
3. Paste/upload a `.btm`, provide its exact source/target XSDs, and choose **Create native map proposal**.
   Conservative scalar direct links/constants are converted, ordered by target schema, and checked for
   required target fields. Optional-source/optional-target links use Omit. Unsupported settings,
   ambiguous paths, functoids/scripts and unsupported schema shapes reject the entire proposal.
4. Inspect the JSON, generated XSLT and warnings. Only **Replace this node's mapping with proposal**
   changes the node. Save normally to persist it. Proposals are version 2 and retain the selected source
   root plus exact source/target schemas, including constant-only maps. The source-root assertion is
   editable as a QName. Invalid proposals preserve the existing definition.

The schema API is `POST /api/flow-designer/xml-mapping/schemas` with `schemas` (XSD strings).
The proposal API is `POST /api/flow-designer/xml-mapping/import` with `mapXml`, `sourceSchemas` and
`targetSchemas`. Neither endpoint executes scripts, wires an orchestration or approves migration.
Schema imports resolve only from supplied texts; external resolution, includes and redefine are not
supported. Schema sets allow at most 16 documents / 1,000,000 total characters; inspection allows
16,384 nodes / 32 levels / 32 namespace URIs. The mapping limit also includes embedded schema text.
Preview/runtime limits remain 262,144 mapping characters, 128 fields, 32 prefixes, 32 path segments,
1,000,000 input characters and 4,000,000 output characters.

Workspace reports also retain bounded script metadata (language, FID, line, size, hash and textual
BizTalk API hints), not bodies or execution/portability approval. Each map's source/target schema
reference shows case-sensitive file candidates from explicit project Namespace/TypeName metadata or
labeled literal naming conventions. Candidate project/file paths, metadata method, source line and
file presence are retained. Missing, ambiguous, conditional/dynamic and incomplete evidence stays
explicit; disk filenames alone never establish class ownership. Inspection is capped at 4,096 schema
declarations, 32 saved candidates per reference and 32 shared warning details. Reanalyze source for
fresh evidence; existing saved reports remain original snapshots.

Pipeline artifacts expose **Pipeline source inventory** with declared stage/component order, category,
class/version, source lines and property names only. Values, descriptions and cached display metadata
are excluded. Saved evidence is capped at 64 stages, 256 components, 64 property names per component
and 512 characters per identity/name, with explicit omitted counts and blocking limits/errors.
Unknown/mixed categories remain unknown; direction is parsed from stage categories, not raw text GUIDs.
Fresh import candidates' **Scaffolded modules > Download / Download all (.zip)** includes deterministic
pipeline C# placeholders, alongside orchestration stubs and partial map stylesheets. Pipeline placeholders
contain names-only evidence, compile against the SDK, honor cancellation and throw `NotSupportedException`.
They are not automatically installed/wired; choose and implement the correct native parse/augment/render
stages and configuration/context effects first. Inventory, builds and downloads never approve execution.

Repeated-record mapping, arbitrary functions/conditions, functoid/script translation,
evaluated/deployed schema ownership and automatic orchestration wiring remain unfinished. Existing
imported XSLT/stub references are not replaced automatically. Use an XML-preserving parser such as
`xml-xsd-parse` before this render; it does not map JSON payloads. Preview success is local evidence,
not proof of BizTalk-equivalent behavior.

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

### Local identity-provider sandbox

`compose.auth.yml` starts a local Keycloak instance backed by PostgreSQL. This is a development sandbox,
not a network deployment: Keycloak runs in development mode and its HTTP port is bound to loopback only.
Do not change the port binding or expose this Keycloak setup to a LAN; network deployment needs HTTPS and a
production Keycloak configuration.

Create a local environment file and replace the example values with two different, long random passwords:

```powershell
Copy-Item .env.auth.example .env.auth
# Edit .env.auth; do not commit it or reuse these credentials.
docker compose --env-file .env.auth -f .\compose.auth.yml up -d
docker compose --env-file .env.auth -f .\compose.auth.yml ps
```

Open **http://localhost:8081** and sign in with `KC_BOOTSTRAP_ADMIN_USERNAME` and
`KC_BOOTSTRAP_ADMIN_PASSWORD` from `.env.auth`. The PostgreSQL database is persisted in a named Docker
volume and is not published to the host. Stop the containers with:

```powershell
docker compose --env-file .env.auth -f .\compose.auth.yml down
```

This keeps the database. `down --volumes` deletes the database and all Keycloak accounts. `.env.auth`
contains plaintext local secrets and is excluded from Git.

### Mulse OIDC authentication

Mulse uses OpenID Connect authorization-code login for the administrator UI and OAuth client credentials
for server-to-server UI/API calls. Configure the `mulse-ui` client with its HTTPS callback URLs as described
above, and create the `mulse-service` confidential client with a service account assigned the
`mulse-api-access` realm role. Keep the two client secrets separate.

The Service validates issuer, signature, expiration and the `mulse-api` audience. In Keycloak, open the
`mulse-service` client, add an **Audience** protocol mapper to its dedicated client scope, set the included
custom audience to `mulse-api`, and enable **Add to access token**. The access token must also contain the
assigned `mulse-api-access` realm role. UI sign-in additionally requires the user's `mulse-admin` realm role
in the **ID token**, not just the access token. In **Clients > mulse-ui > Client scopes**, open its dedicated
scope, then **Mappers > Configure a new mapper > User Realm Role**. Set **Name** to `mulse-realm-roles`,
**Token Claim Name** to `realm_access.roles`, **Claim JSON Type** to `String`, **Multivalued** to ON, and
**Add to ID token** to ON. Leave the role prefix empty. Ensure `mulse-admin` is assigned to your user and
allowed by the client's role scope mappings. Sign out and sign in again to issue a fresh session after
changing role assignments or mappers. Missing administrator roles produce a 403 page with sign-out,
not another login redirect. This mapping is Keycloak configuration; Mulse's issuer and role-claim names
remain configurable for other providers.

Store both client secrets in Visual Studio's **Manage User Secrets** for the `Mulse.Ui` project; do not put
them in appsettings, Compose files or source control. Add these entries to its user-secrets JSON, replacing
the placeholders with the values from the matching Keycloak client's **Credentials** tab:

```json
{
  "Authentication": {
    "ClientSecret": "<mulse-ui-client-secret>"
  },
  "MulseApi": {
    "ClientSecret": "<mulse-service-client-secret>"
  }
}
```

Run the UI using its **https** launch profile so its callback is `https://localhost:7021/signin-oidc`.
The management API requires a valid bearer token from the configured issuer, the expected `mulse-api`
audience, `azp` equal to `mulse-service`, and the `mulse-api-access` role. The UI itself requires the
`mulse-admin` role. The local HTTP Keycloak issuer is permitted only in Development; production must use
HTTPS and an appropriately configured secret store, callback URLs, and stable Data Protection keys.

Start sign-in at `https://localhost:7021`; visiting Keycloak's account console first is not required.
The protected UI cookie retains only the ID token needed for Keycloak logout, not the user's access or
refresh tokens. Sign-out clears the UI cookie and requests identity-provider logout. Existing sessions
without an ID token use the client ID and may receive a Keycloak logout confirmation prompt. Ensure
`https://localhost:7021/signout-callback-oidc` is registered as a valid post-logout redirect URI for
`mulse-ui`. After changing authentication configuration, restart the UI and sign in afresh.

The `/api/inbound/{route}` endpoint intentionally remains anonymous for compatibility with existing senders.
It is not protected by the administrator login, and an IP allowlist is not yet implemented. Until inbound
authentication and network restrictions are added, expose that route only behind a firewall or trusted
network gateway; do not publish the Service port to an untrusted network.

### First run and persistence

The shipped application uses **SQLite**, with `ConnectionStrings:Mulse` set to
`Data Source=Data\mulse.db` relative to the **Service content root**. Keep it on local persistent storage,
not a network share. Connections use WAL mode, `synchronous=FULL`, foreign keys and a 30-second busy timeout.
Transactions persist workspace metadata and changed operational records together; failed writes are not
published to the runtime. Only changed records are written, not one growing workspace JSON blob.

On an empty database, the Service imports the file at `Mulse:RuntimeStatePath` (default
`Data\mulse-runtime.json`), including definitions, configuration/secrets, package registrations, pending
retries and checkpoints. The import is one transaction. The original file is **not modified or deleted**;
protect/archive it after validating the database. Existing encrypted files require their original key;
database encryption requires the environment key described below. Invalid input or an unavailable database
fails startup instead of silently seeding a replacement. Legacy processing retries without a pinned
definition remain subject to the recovery hold documented above.

If no legacy file exists, `Mulse:Pipelines` and `Mulse:PluginDirectories` seed the database. Once initialized,
the database is authoritative: later changes to seed sections or the legacy file do not change it.
Use the UI/API for flow/package/configuration changes. Do not run an old JSON-backed Service against the
retained import file: that would create a separate, divergent workspace, not a rollback.

The shared model has `Flows`, append-only `FlowRevisions`, `ConfigurationValues`, `ModulePackages`,
`Checkpoints`, `Retries` and workspace metadata. Each saved flow change creates an immutable definition
revision. Deleted/replaced flows retain historical revisions; the workspace backup UI exports current
configuration, not this revision archive. Processing retries still carry their original resolved definition
snapshots. `Executions`, `ExecutionEvents`, `Messages` and `Deliveries` add connected operational history,
a durable HTTP/flow-link inbox and a delivery intent/outcome journal. Provider-specific
`DurableExecutionJournal` migrations upgrade existing databases without discarding workspace state.

#### Execution and message history

Open **Workspace > History** to inspect retained executions, stage events, inbox messages and delivery
outcomes. Payloads are fetched/decrypted only after **Inspect payload**; metadata/detail lists do not
include message bodies. The management API remains authenticated, and history responses use `no-store`.
Treat event details, headers and payloads as sensitive; these runtime events do not capture every module's
`ILogger` output. Execution IDs connect retries, claimed messages, events and deliveries. An execution
retains its resolved definition, including settings/secrets; queued messages use the flow definition
resolved when first processed, not necessarily the definition at acceptance time.

`Persistence:History:CaptureStagePayloads` (environment `Persistence__History__CaptureStagePayloads`) defaults
to **false**. Enable it to retain stage input/output snapshots for file and other sources as well as inbox
messages. Required inbox/outbox payloads are always stored for recovery, regardless of optional capture.
`Persistence:History:RetentionDays` (`Persistence__History__RetentionDays`) defaults to **30**, range 1-36500.
Hourly cleanup removes up to 100 eligible terminal histories per pass; pending/held/suspended work, retries,
checkpoint-linked executions and uncertain deliveries are protected. Deduplication of HTTP requests expires
when their terminal message records are purged. The Storage page shows these effective options.

On startup, interrupted executions are marked Held or Retrying according to their saved continuations;
in-flight deliveries become Unknown, and claimed messages stay held until completion/reconciliation. A
persisted Succeeded delivery can be skipped during continuation with its captured response restored.
The inbox and external destination are **not** one distributed transaction: exactly-once external delivery
is not promised. Generic crashes outside a saved continuation are retained for investigation, not guessed
into a replay. For held work with no retry/checkpoint continuation, History offers **Close held execution
without replay** after independent review, evidence and typing `CLOSE WITHOUT REPLAY`. This explicitly
abandons held input; it does not resend or roll back effects, and can lose unprocessed work. It rejects
existing runtime continuations rather than deleting them. Full automatic/manual replay for every arbitrary
stage/plugin is not implemented. Legacy Json mode has no operational history or durable inbox/outbox and
reports that explicitly.

#### Opt-in confirmed delivery and safe stage recovery

Delivery nodes support `RequireConfirmation` (default **false**, preserving legacy behavior). Enable it only
for a module implementing `IConfirmedDeliveryModule` with durable atomic destination deduplication. The
runtime persists a stable delivery GUID and exact input/settings before invoking the destination. After an
ambiguous interruption it queries that ID first: Confirmed skips the effect and restores its response;
NotDelivered permits a stable-ID resend; Unknown remains held. Unsupported modules or legacy JSON storage
are rejected rather than silently pretending to provide confirmation. Required response data must also
be recoverable; a success acknowledgement without that required data does not suffice.

The built-in **flow-link delivery** supports this with the local durable inbox. This guarantees once-only
**enqueue**, not once-only arbitrary effects in the receiving flow. Confirmed hand-off receipts retain their
full input message rows/bodies **permanently**, even when related terminal execution history expires; this
is not a compact tombstone. Plan storage/privacy retention and enable payload encryption where required.
Unchecked flow-link messages retain the normal terminal retention policy. The provider-specific
`ConfirmedDeliveryReceipts` migration adds this distinction to existing databases.
Generic HTTP/SMTP/file destinations do
not gain exactly-once semantics from a checkbox: a plugin must implement the destination-side contract,
including deduplication and the effect in the same atomic boundary, and retain its IDs for the entire replay
lifetime. Native/ODX effect reconciliation retains its existing separate conservative protocols.

`IReplaySafeModule` explicitly opts a parse/render/augment implementation into safe restart replay. Only
built-in JSON parse/render initially opt in; fetches and arbitrary plugins are not assumed safe. Database
continuations pin the resolved definition and retain inputs, responses and source receipts before a safe
stage. Attempt tracking prevents an older safe continuation from replaying an already-started unsafe
stage. Such uncertainty stays held for review. Native/ODX checkpoints are not replaced by this contract;
there is still no universal replay guarantee for arbitrary code, external effects or lost HTTP connections.
See [Module-Developer-Guide.md](Module-Developer-Guide.md) before advertising either capability in a plugin.

#### Maintenance, graceful shutdown and ownership

Use **Configuration > Storage > Pause and drain flows** before maintenance. Admission closes before the
pause is committed. Already admitted executions and HTTP intake finish normally; the status moves from
**Draining** to **Paused** when both counts reach zero. Refresh status to observe completion. The pause
is workspace state and survives restart (including legacy JSON mode). **Resume processing** explicitly
reopens intake. A failed/unacknowledged pause transition remains fail-closed until restart; never assume
that a failed response means the pause rolled back. Provider-specific `PersistentMaintenance` migrations
add the pause flag to existing databases, defaulting to running.

The gate covers manual runs, push/interval triggers, inbox polling, retry/deadline drivers, explicit batch
release and native-effect reconciliation. Inbound requests rejected by maintenance/capacity receive **503**
with **Retry-After: 5**. Maintenance is checked before reading the body; inbox capacity is checked in the
acceptance transaction before commit. Pending database messages remain durable. JSON queues
and file-watcher notifications are still process-local; a pause does not make them durable. **Paused does
not mean database-offline**: management and ownership sessions remain active. Stop the Service after
draining before an external/offline database migration. The Storage wizard instead holds exclusive ownership
of both databases and blocks resume throughout its controlled transfer. Shutdown waits for that operation
within the same drain budget; expiry cancels the copy/finalization and requires explicit interruption recovery.

When the host receives a graceful stop notification (for example Ctrl+C, SIGTERM or a service-manager/OS
notification), it closes admission immediately and allows active work to finish before worker cancellation.
`Processing:ShutdownDrainSeconds` (`Processing__ShutdownDrainSeconds`) defaults to
**90**, range 1-3600. The host cleanup timeout is this budget plus 30 seconds; configure the OS/service
manager/container termination grace period above that. On expiry the Service logs an error and cancels
remaining work; it does **not** report a safe drain. Ordinary host shutdown does not persist a permanent
maintenance pause. Power loss, forced kill, uncooperative plugins and insufficient OS grace time cannot
be made into a guaranteed graceful shutdown; existing journal/reconciliation rules still apply.

`Processing:MaxConcurrentExecutions` (`Processing__MaxConcurrentExecutions`) defaults to **64**, range
1-10000, counting admitted work including per-flow lock waiters. `Persistence:History:MaxPendingMessages`
(`Persistence__History__MaxPendingMessages`) defaults to **10000**, range 1-1000000, and bounds Pending,
Claimed and Held database inbox messages. New acceptance fails explicitly when full; an identical existing
idempotent acceptance still returns its original receipt. The Storage panel shows inbox capacity, held work
and uncertain deliveries, warning at **90%** capacity and whenever recovery attention is required. Refresh
these snapshots and monitor Service error/critical logs. This is backpressure and operator visibility, not
a guarantee of upstream retries or an external alerting service.

Use **one Service process per workspace**. Startup enforces exclusive ownership: JSON/SQLite keep an OS
file handle at `<data-file>.owner.lock`; SQL Server keeps a session-owned application lock; PostgreSQL keeps
an advisory lock on a dedicated non-pooled session. Ownership is checked before work/storage operations and
by a five-second heartbeat. Losing ownership or being unable to verify it blocks admission and cancels work;
there is no silent reconnect/takeover. OS/session locks are released on process exit; leftover local lock
files are harmless and must not be deleted while a process owns them. Server databases must exist before
ownership/migration startup. Local storage must be a single canonical file path, not aliases/network shares.
These are single-owner guards, **not remote destination fencing**: a previously started external request may
still complete during a partition. Use a destination's atomic idempotency contract for confirmed delivery.

The runtime still has an in-process state cache and execution locks; a server database alone does not enable
active-active execution. An application-managed workspace version rejects stale writes from another
instance rather than overwriting its committed work. A detected
stale cache or unacknowledged commit blocks subsequent runtime reads/writes. Stop competing instances and
restart to reload committed state; a lost acknowledgement does not prove a transaction rolled back.
Changing providers points at a different database, not an automatic database-to-database migration. Do not
switch to an empty database with a stale legacy file; transfer/reconcile the complete state first.

#### Selecting a database and applying migrations

The sidebar separates **Modules** (Modules / Module Packages) from **Configuration** (Config values /
Storage / Backup & Restore). **Configuration > Storage** shows the Service's effective startup engine,
resolved local data-file path where applicable, migration policy and payload-encryption status. It lists
configuration keys, environment-variable names and connection-string templates, but never returns the
actual connection string or encryption key. Engine/bootstrap settings are read-only, not a connectivity
check or live engine switch; the separate processing panel provides persistent pause/resume controls.

Configure storage on the **Service process**, using `Service\appsettings.json`, environment-specific
settings, Service user secrets for development, or deployment secret injection. Settings on the UI or
AppHost alone do not configure the Service unless explicitly forwarded. Restart the Service to change
providers, connections or bootstrap options. The page reports the settings selected at startup even if
configuration files are subsequently edited.

#### Storage migration wizard

**Configuration > Storage > Storage migration wizard** transfers a complete database workspace between
any pair of SQLite, SQL Server and PostgreSQL engines, including same-engine moves. It is not a live
engine switch and does not change bootstrap credentials or stop/restart processes.

1. Deploy the current source schema first (including the fifth migration, `StorageMigrationSeals`).
   Pause processing and wait until **Paused**, with zero executions/admissions. Take verified full database,
   external encryption-key and plugin-file backups. Workspace export alone is not a complete backup.
2. Enter the target engine and connection on the Storage page. SQLite requires a durable local file;
   server databases must already exist. The target must have no workspace records and cannot be owned
   by another Service. **Allow applying target schema migrations** is opt-in and only prepares an empty,
   table-free database, including during preview. Existing tables are never automatically upgraded by
   the wizard; deploy their migrations separately before retrying.
3. Save the **migration ID** and original connection details. **Preview and validate target** checks ownership,
   schema, all source payloads, and a consistent source snapshot. It reports counts and SHA-256 fingerprints
   for all 11 workspace tables. Confirm backups and type **MIGRATE STORAGE** to copy, verify and seal the source.
   If source state changes after preview, preview again; the stale snapshot cannot be executed.
4. After success, stop the Service, set its `Persistence__Provider` and `ConnectionStrings__Mulse` through
   Service secrets/deployment configuration, and restart against the destination. Preserve the external AES
   key, key ID/environment-variable setting, history/processing settings and installed plugin binaries.
   The destination remains paused. Inspect the engine, history and recovery backlog before explicitly resuming.
   Keep the retired source offline; do not run both copies.

The transfer includes configuration, retained flow revisions, retry/checkpoint state, executions, events,
messages and deliveries. Every scalar value and row count is verified independently of provider row ordering.
Encrypted payloads are validated with the original key and copied byte-for-byte; this is **not key rotation**.
Rows are copied in batches, but sorted row hashes use memory proportional to each table's row count.
Allow capacity and time for full validation; the UI migration client permits 30 minutes and never automatically
retries POSTs. Reverse proxies may impose shorter limits. Connection details are supplied per request,
not saved to bootstrap configuration, logged or returned in reports. Use protected UI and Service connections.

**Interrupted migration recovery:** the two databases do not share a distributed transaction. The target is
first committed as **TargetStaged**, which refuses runtime startup. Only then is the source committed as
**SourceSealed**, blocking all application writes and flow admission. Finally the target becomes **TargetReady**,
still paused. A sealed source can restart for management/finalization without journal recovery writes, but it
cannot resume. Re-enter the **same migration ID and original destination**, preview, then execute to reverify
and finish. Database/file-name case is significant to the destination fingerprint; retain the original spelling.
A lost commit acknowledgement does not prove rollback; some failures require restarting the source to reload
committed state before retrying. Unrelated or modified targets are rejected, never overwritten. There is no
automatic rollback/unseal, bootstrap update or failover. Do not substitute an empty target for a sealed source's
original staged destination. Restore backups only as an explicit offline recovery operation after establishing
which database is authoritative.

Legacy **JSON** storage does not support this full-database wizard or migration back from a database.
The existing one-time startup JSON import is separate and cannot transfer process-local messages/history.
Merely changing the provider does not migrate state; the workspace backup export is not a substitute.

#### Provider bootstrap settings

Set `Persistence:Provider` to `Sqlite`, `SqlServer` or `PostgreSql`, and supply `ConnectionStrings:Mulse`.
For deployments, use `Persistence__Provider` and `ConnectionStrings__Mulse` environment variables or a
secret store; never commit credentials. SQLite applies migrations automatically by default. Server providers
default to **no automatic migrations**: deploy the schema with a migration account before starting the Service.
`Persistence:ApplyMigrations` explicitly overrides this behavior. Back up before schema upgrades; do not run
migrations concurrently with a live Service. Each provider has its own generated migrations and model snapshot.

```powershell
dotnet tool restore
# Set this process variable securely to the target database connection string first.
$env:MULSE_MIGRATION_CONNECTION = $env:ConnectionStrings__Mulse
dotnet ef database update --project .\Mulse.Persistence.SqlServer --startup-project .\Mulse.Persistence.SqlServer --context SqlServerMulseDbContext
# PostgreSQL: use .\Mulse.Persistence.PostgreSql for both projects and PostgreSqlMulseDbContext
# SQLite:    use .\Mulse.Persistence.Sqlite for both projects and SqliteMulseDbContext
```

Migration factories do not load application settings: SQLite's fallback is a design-time file in the current
directory, not the Service runtime database. Always specify an absolute SQLite path when migrating manually.
To add a shared model change, generate a migration for **each** provider using `dotnet ef migrations add`
with the corresponding project/context. EF Core 10.0.15 is used consistently because the stable PostgreSQL
provider currently targets EF 10; the application's .NET target is unchanged.

`Persistence:Provider=Json` explicitly retains the legacy file store. Unconfigured library/test consumers
also retain JSON for compatibility; the shipped application explicitly selects SQLite. An unknown provider
or missing database connection string is an error, never a fallback to JSON.

#### Encryption and backups

Enable runtime payload encryption in **Config values**, or set `Mulse:Encryption:Enabled=true` for a new
workspace. Encryption mode is subsequently saved in the database. Set `MULSE_STATE_ENCRYPTION_KEY` to a
base64-encoded AES key (prefer 32 random bytes); override its name with
`Mulse:Encryption:KeyEnvironmentVariable`. Keys stay outside the database and source control. Database
writes do not use `Mulse:Encryption:KeyBase64`. `Persistence:Encryption:KeyId` defaults to `primary` and
must match the ID recorded in an encrypted database. Keep both ID and key available after restart.
Missing/wrong keys or modified ciphertext fail explicitly. Key rotation is not yet automated: do not
replace a key in place while encrypted data remains.

AES-GCM protects configuration/package payloads, current and historical flow definitions, plugin directories,
checkpoints, retry snapshots, execution definitions, event details, inbox/history payloads and delivery
intents/responses before they reach any database provider. Authentication binds ciphertext to its record
identity. Flow IDs/names/categories/enabled flags, execution IDs/statuses, stage/channel names, timestamps,
revision identifiers, hashed lookup identities and scheduling indexes remain queryable metadata; this is **payload encryption**, not
whole-database encryption. Enabling/disabling encryption rewrites all payloads and historical revisions in
one transaction. It does not erase older plaintext from database journals, backups or the retained JSON
file. Use OS/disk encryption as additional protection and avoid plaintext payloads in logs.

For disaster recovery, back up the **database**, encryption keys and plugin binaries separately. The UI's
workspace export excludes pending execution state, inbox/outbox records, execution/message history and
historical revisions; it is not a database backup.
Use the SQLite online backup API (or stop the Service and follow SQLite's safe-copy procedure); copying
only a live `.db` file can omit committed WAL data. SQL Server/PostgreSQL use their normal database backup
and restore tooling. Test restore before relying on any backup.

The Service account needs read access to source/plugin paths and write access to its SQLite/state directory,
`Runtime\PluginCache` shadow-copy directory, output folders, and archive/quarantine paths. Cleanup additionally
requires delete/move permissions on input files. Module filesystem paths and BizTalk source paths refer to
the **Service machine**, not the browser machine. Prefer explicit absolute paths and never delete state
or the database to refresh examples without backing up/reconciling pending work.

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
node --test .\Mulse.Ui.Tests\*.test.cjs .\Service.Tests\*.test.mjs
```

The JavaScript suites need no npm dependencies. Workspace persistence tests always exercise SQLite.
To run the same contracts against SQL Server/PostgreSQL, set `MULSE_TEST_SQLSERVER` and/or
`MULSE_TEST_POSTGRESQL` to test-server connections and run:

```powershell
dotnet test .\Service.Tests\Service.Tests.csproj --filter FullyQualifiedName~WorkspacePersistenceTests
```

These tests create and delete uniquely named `mulse_ef_tests_*` databases; the test account needs database
creation/deletion privileges. Only use isolated test servers. Without those variables, server migration
models/scripts are checked without connecting; server integration cases are not included.
Tests do not establish end-to-end behavior against your live SQL, SFTP, SMTP or HTTP systems; validate
those separately before letting a flow modify source data.

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
2. Click or drag **Source** for a linked fetch/parse pair, or add fetch/parse nodes separately. Choose
   modules and configure real input locations. Click an output handle then an input handle, or drag
   between handles, to connect fetch-to-parse or parse-to-lookup-fetch. Cyclic connections are rejected.
   Properties checkboxes also add/remove these links. Moving a node changes layout, not execution order.
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

### Mouse and keyboard editing

The canvas keeps its existing toolbox/node/group drag, pan, zoom and sample-file drop workflows.
Output/input handles provide direct source wiring; click **Cancel connection**, click the selected
output again, or press Escape inside the canvas to cancel. Other stages are wired by the ordered
augment chain and delivery routes, not by arbitrary arrows. Use **Move earlier/later** to change augment
order. Tab to a node and press Enter/Space to select its Properties; native handle and remove buttons
are also keyboard accessible. Source headings are inset within their group; long titles use an ellipsis
(hover for the full title). Batch/use badges sit below nodes, separate from the source heading.

Module settings offer **Insert stored reference...** alongside typing. Search stored names, choose
Config or Secret, and click a token to insert it at the last text selection (or replace an unfinished
`{{...` reference). The picker never retrieves stored values. Boolean/select settings retain their
literal controls and offer **Use config/secret reference**. The existing typed autocomplete still works.
**Advanced: add a module setting** is for a documented custom module key, not for creating mapper fields.

On delivery nodes, **Delivery compensation** selects an installed deliver module and its descriptor-driven
settings. Choosing None removes it. Configure a named atomic scope on the route for it to be used.
Changing the compensation module resets its module-specific settings to the new defaults; changing
other node properties preserves the saved compensation.

### UI access coverage and boundaries

| Task | UI entry point |
|---|---|
| Flow CRUD, categories, copies, enable/disable and manual run | Dashboard cards, their title/configure links, and Flow overview. |
| Source graph, lookup dependencies, stage modules and settings | Toolbox, canvas handles and node Properties. |
| Parsed/XML field maps and transforms | Mapping Designer; click/drag fields and connections, choose transforms and edit their arguments. |
| Joins, decisions, metadata, durable batching/state and renderer dispatch | Specialized node Properties editors and descriptor-driven settings. |
| Ordered renderer pipelines and outbound ODX adapters | Ordered renderer pipeline / Durable ODX program Properties: stage add/remove/reorder, named send selection, module settings and response capture. |
| Native synchronous sequences, branches and bounded loops | Native control flow Properties: nested node editor, condition builder, module selection/settings and iteration limits. |
| Flow/step retry and best-effort delivery compensation | Flow overview and node Properties. |
| Checkpoint inspection and confirmed early batch release | Dashboard > Pending batches / orchestration checkpoints. |
| Config/secrets, encryption, module catalog and hot reload | Configuration pages; refresh modules in an open Designer afterward. |
| Encrypted backup, restore preview and confirmed bulk deletion | Backup & Restore. |

Core authoring and administration are accessible without hand-authoring flow API requests. This is not
universal graphical parity with every SDK language: SQL, XPath/XSLT, expression/program text and advanced
JSON remain typed editors where applicable. Authoring/compiling DLLs, copying plugins and setting host
keys/folders remain developer/deployment tasks. Exhausted source-outcome notification recovery remains
an explicit API operation after operator review; the checkpoint UI does not offer destructive reset or
indiscriminate replay. Import analysis/scaffold download is available in the Designer, but imported
Billing execution remains blocked; this UI pass does not change compatibility or transaction guarantees.

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
when enabling a flow or saving it in the designer. Each dashboard flow has a stronger boundary and
status stripe, a separate header/action area, metadata, labeled Augment/Render/Deliver tiles, and a
checkpoint footer in both light and dark themes. Status remains written in text, not color alone.

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

Use **Collapse menu** beneath the Mulse logo to reduce the desktop navigation to an icon rail.
Hover an icon for its label; all destinations and the dark-mode toggle remain available. **Expand main
menu** restores the labels. The page and Flow Designer reclaim the freed width. On smaller screens,
collapsing hides the navigation below the header instead. The choice lasts while navigating in the
current UI session; reloading the page restores the expanded menu.

Appearance follows your system's light/dark preference until you use **Dark mode** in the navigation.
Your selection is saved in this browser and applies to all pages, including the designer canvas.
No external fonts, styles, or theme services are required.

Toolbox and Flow overview share one pinned left column with matching widths and typography.
Drag their horizontal divider to change the height split (20-80%), or focus it and use Up/Down in
5% steps, Home for 20%, or End for 80%. Each panel scrolls independently; the split survives canvas
interaction and Flow/Mapping tab switches during the editing session. Properties is pinned on the
right, with the canvas occupying the space between the columns instead of being covered by them.
On smaller screens, the left dock, canvas and Properties stack vertically. Sample/import dialogs
remain floating. Tables keep their column headers and scroll horizontally inside their panels.

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
| BizTalk import | `POST /api/biztalk-import/analyze`, `POST /api/biztalk-import/flows`, `POST /api/biztalk-import/rules/preview`, and scaffolded-module download endpoints. |
| Config values | `GET /api/config-values`, `GET /api/config-values/references`, `GET /api/config-values/usages`; `PUT`/`DELETE /api/config-values/{reference}`. |
| Workspace | `GET`/`PUT /api/workspace/encryption`; `POST /api/workspace/backup`, `/api/workspace/restore/preview`, `/api/workspace/restore`, `/api/workspace/flows/delete-all`. |
| Storage diagnostics | `GET /api/workspace/storage` (no connection strings or keys). |
| Storage migration | `GET /api/workspace/storage/migration` (credential-free status); `POST /api/workspace/storage/migration/preview`, `POST /api/workspace/storage/migration/execute`. Authenticated; no-store responses; requires drained maintenance. Request: `provider`, `connectionString`, `prepareSchema`, `migrationId`; execute also requires preview `expectedSourceVersion`, `confirmation: "MIGRATE STORAGE"`, `backupConfirmed: true`. |
| Processing / maintenance | `GET /api/workspace/processing`; `GET /api/workspace/processing/backlog` (metadata counters/capacity); `PUT /api/workspace/processing/maintenance` with `requested: true` to pause/drain or `false` to resume. Authenticated; poll until Paused before maintenance. |
| History | `GET /api/history/executions?flowId=...&take=50`, `GET /api/history/executions/{executionId}`, `GET /api/history/messages?take=50`, `GET /api/history/messages/{id}` (explicit payload inspection). List `take`: 1-200; execution-detail lists are bounded to 1000 each. |
| Delivery reconciliation | `POST /api/history/executions/{executionId}/deliveries/{deliveryId}/confirm-delivered` with `confirmation: "CONFIRM DELIVERED"` and independently verified `evidence` (10-4096 characters). Never resends the effect. |
| Held disposition | `POST /api/history/executions/{executionId}/close` with `confirmation: "CLOSE WITHOUT REPLAY"` and review `evidence` (10-4096 characters); explicitly abandons held input, only when no retry/checkpoint continuation remains. |
| Inbound HTTP | `POST`/`PUT /api/inbound/{route}` for a registered HTTP push source; optional `Idempotency-Key` for database-backed acceptance. |

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

Mulse is a **single-process proof of concept**, not a production-hardened integration service. Management
UI/API access uses the configured OIDC authentication/authorization; the inbound receive route remains
intentionally anonymous and needs appropriate source/network protection. Masking and at-rest encryption
do not restrict authorized API access, plugin code, logs, network traffic, or external files.

Exclusive workspace ownership is not active-active coordination or external-effect fencing. There is no
universal exactly-once guarantee, transactional message broker, or general rollback across external systems.
Confirmed delivery depends on the destination contract; persisted retries/checkpoints and replay-safe
modules cover specific continuations, not every crash window. Built-in polling modules read batches into
memory; apply sensible input/workbook limits and test external-system behavior before using destructive
cleanup. Workspace backups omit live work and binaries as described above; retain a separate operational
recovery plan for state, keys, plugin files and source/destination data.

## Status

This is an in-development project (not yet deployed to production). Expect breaking changes as the
architecture evolves. Review imported/generated flows and module behavior before using real data.
