# Module and extension developer guide

## Scope and compatibility

Mulse's extension contract is `Mulse.Modules` (currently `net10.0`). A module package is compiled trusted .NET
code, not a sandbox or a BizTalk assembly loader. You can wrap a supported legacy library in a native module
without translating its implementation. Check the library's licensing, dependencies, target framework, bitness,
thread safety and reliance on BizTalk/COM/GAC first. A .NET Framework BizTalk component is not automatically
compatible with this host. Do not copy deployment secrets into source or generated projects.

Use the SDK assembly built with the deployed host; there is no promised cross-version binary compatibility or
published package/version negotiation protocol. `Mulse.SamplePlugin` is the compilable reference project.
The host and SDK exchange ordinary public objects; plugins do not need a reference to the `Service` project.

## Choose the extension seam

| Contract / registration | Developer receives | Developer returns / owns |
|---|---|---|
| `IFetchModule` / `AddFetch<T>()` | Context, upstream input batch, step, cancellation | Acquired `IntegrationBatch`; upstream input permits lookup fetches. |
| `IFetchOutcomeModule` (register as fetch) | The same fetch inputs, then a saved receipt and terminal outcome | Deferred, idempotent source acknowledgement/archive. |
| `IPushTriggeredFetchModule` (register as fetch) | Trigger registration context, including signal callback | Registration lifetime; signal schedules execution, it does not carry the fetched data. |
| `IParseModule` / `AddParse<T>()` | Raw batch, context/settings/cancellation | Parsed or debatched payloads; zero/one/many outputs. |
| `IOrchestrationAugmentModule` / `AddOrchestrationAugment<T>()` | Working batch, execution context/settings/cancellation | Replacement working batch; enrichment, routing, mapping, collection. |
| `IRenderModule` / `AddRender<T>()` | The delivery route's working batch | Output documents/files; zero/one/many outputs. |
| `IDeliverModule` / `AddDeliver<T>()` | Rendered batch, context/settings/cancellation | External side effects; no batch replacement. |
| `IRequestResponseDeliverModule` (register as deliver) | Rendered batch and execution context | Response batch, captured by the runtime. |
| `ITransformModule` / `AddTransform<T>()` | Ordered `JsonNode?` values and context/settings/cancellation | One `JsonNode?` value; null means absent output, not an error fallback. |

See the exact signatures in `Mulse.Modules\Abstractions`. Fetch/parse graph edges, augment order, delivery
routes and retry/compensation boundaries belong to the saved `PipelineDefinition`, not to registration order.
Do not use a deliver extension to hide parsing errors or an augment to perform untracked delivery effects.

## Objects available to conversion code

- `IntegrationPayload`: name, `BinaryData Content`, content type, and string metadata. Read bytes with
  `Content.ToMemory()`/`ToArray()`, or use the shared text extensions where the content is text. Parse XML with
  DTDs prohibited and external resolution disabled. Decode strict encodings when malformed input must fail.
- `IntegrationBatch`: ordered `Payloads`, `Count`, and optional opaque `SourceReceipt`. A receipt can cover the
  whole fetched batch; do not guess which client owns pieces of it. Raw contracts support XML/binary and
  multiple schemas. They do not expose live BizTalk `XLANGMessage`, pipeline context, promoted property bags,
  message parts, orchestration variables or transaction objects. Explicitly model required parts/context.
- `ModuleStepDefinition`: module ID, node name, string settings and optional retry override. Config/secret tokens
  in normal flow settings are resolved by the host before execution. Never log resolved secrets or whole steps.
- `FlowExecutionContext`: flow/execution IDs, start time, response capture, orchestration identity/state,
  checkpoint completion, source receipts/pending receipts, delivery failures and suspension. Context is per
  execution, not a process-wide or automatically durable variable dictionary. Mutating a context is not a commit.
- `IFlowOrchestrationStateStore`: get/upsert/delete flow/correlation checkpoints, inspect flow checkpoints and
  pending source receipts. `FlowOrchestrationPayloadSnapshot.Capture/Restore` preserves payloads for persistence.
  The default store is single-host runtime-state persistence, not a distributed database or transactional outbox.
- `DecisionRuleDefinition`, mapping definitions and `PipelineDefinition` are public SDK models. They describe
  native operations; they are not an evaluator for arbitrary C#, XPath or BizTalk expression syntax.

Copy metadata before changing it: `new Dictionary<string,string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)`.
Return `payload with { Metadata = metadata }` or a new payload instead of mutating another module's dictionary.
The `sourceId` metadata key identifies source-graph origin. Preserve it when downstream maps rely on it.
Do not retain context, payloads, model types or plugin delegates in host-global/static caches.

## A minimal raw module package

Create a class library targeting `net10.0`, reference `Mulse.Modules`, enable nullable/implicit usings and copy
private dependencies beside the plugin DLL. The sample project uses `CopyLocalLockFileAssemblies=true` and
outputs to `Service\plugins\Mulse.SamplePlugin\`; use a separate folder for your package. A minimal module is:

```csharp
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Modules.Sdk;

public sealed class PartnerContextModule : IOrchestrationAugmentModule
{
	public ModuleDescriptor Descriptor { get; } = new(
		"partner-context", "Partner context", ModuleKind.OrchestrationAugment,
		"Adds an explicitly configured partner key without changing the body.",
		[new("partner", "Partner key", "Required routing identifier, not a secret.", true)]);

	public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch,
		ModuleStepDefinition step, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var partner = ModuleSettingReader.GetRequired(step.Settings, "partner", Descriptor.Id);
		var output = new List<IntegrationPayload>(batch.Count);
		foreach (var payload in batch.Payloads)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
			{
				["partner"] = partner
			};
			output.Add(payload with { Metadata = metadata });
		}
		return Task.FromResult(new IntegrationBatch(output));
	}
}

public sealed class PartnerInstaller : IModuleInstaller
{
	public void Install(IModuleRegistryBuilder builder)
		=> builder.AddOrchestrationAugment<PartnerContextModule>();
}
```

Give every module a stable globally unique ID. Changing an ID breaks saved flow references. Settings keys are
contracts too; preserve defaults or migrate explicitly. A module descriptor supplies generic UI forms, defaults,
choices, formats, protocols and recommendation capabilities. It does not create a custom graphical editor.
`Number`, `Select` and `TextArea` are supported setting kinds; validate every setting again at runtime. Descriptor
recommendations do not enforce schema compatibility. Unknown selectors/inputs must fail, not produce success-shaped
empty batches. Return an empty batch only when dropping/filtering/debatching zero records is intentional.

## Typed models and JSON

Optional bases: `TypedFetchModule<TOut>`, `TypedParseModule<TOut>`, `TypedAugmentModule<TIn,TOut>`,
`TypedRenderModule<TIn>` and `TypedDeliverModule<TIn>`. They deserialize the native JSON payload convention and
surface shape errors with payload/module identity. Typed augment is per-payload one-to-zero/one; use raw interfaces
for envelopes, multi-part documents, batches, streams or one-to-many operations. Native graph edges remain
schema-agnostic even when the code inside a module is strongly typed.

For collectible typed plugins, use a **package-local source-generated** `JsonSerializerContext`, include every
input/output/nested contract and override `SerializerOptions` with its options. Match the SDK's camel-case Web
convention. See `SamplePluginJsonContext` and the typed sample modules. Reflection-based JSON serialization can
retain plugin types in process-wide emission caches and prevent unload. Avoid passing plugin model types to
shared host serialization/caches. Custom CLR objects are not persisted by reference: define a versioned wire
contract and serialize them into bytes/metadata or explicit checkpoint state.

## Calling other extensions

Inject `IModuleInvoker` to call value transforms with ordered JSON values and settings. Inject
`IRenderModuleInvoker` to invoke a renderer over a batch. Both acquire/release host-managed package leases;
never find assemblies or instantiate another package's implementation yourself. Neither currently exposes
arbitrary fetch/parse/augment/deliver invocation or child-workflow orchestration.

Normal module IDs and known mapping/dispatch references participate in saved-flow dependency checks. Arbitrary
module IDs hidden in custom settings or chosen by custom code are **not automatically discovered**. Prefer the
native map/renderer dispatch for configured choices. If you add another dynamic reference format, extend/test
host dependency discovery before claiming saved-flow unload protection; invocation leases protect active calls
only. Nested dispatch is deliberately unsupported. Do not call delivery modules behind renderer invocation.

## Lifecycle and hot loading

1. Build/package a trusted plugin and dependencies into its own plugin directory.
2. Open **Configuration > Module Packages** and register/load the compatible package. Newly discovered packages
   are initially unloaded. The catalog scan runs about every five seconds.
3. In an open designer, use **Refresh modules**; existing canvas/settings are preserved. Descriptors populate
   node and mapping-transform controls. Save ordinary module IDs/settings in the flow.
4. Disable affected flows and wait for active work before planned replacement. Reload/unload uses collectible
   assembly contexts and active/saved dependency guards. Keep packages with pending work available; code-version
   snapshots are not automatically pinned for the lifetime of a persisted batch.

Host DI can supply shared host services, `ILogger<YourModule>`, `TimeProvider` and registered invokers/state store.
Build plugin-local helpers inside your package: the host rejects resolution of plugin-owned service types/keys
through its root container to avoid collectible-type cache roots. Dispose resources and detach subscriptions
when module/registration lifetimes end. Do not start detached tasks/timers that outlive a lease. Loading a package
is permission to execute trusted server code with the Service process's permissions.

## Sources, waits, retries and external effects

An `IFetchOutcomeModule` returns a serializable receipt describing the acquired input. Honor
`context.PendingSourceReceipts` so held messages are not fetched again. The runtime captures receipts before
parse and calls `OnProcessingCompletedAsync` after a terminal outcome, potentially in a new module instance
following restart. Make callbacks idempotent. A failed outcome does not prove that no destination received data.
Keep credentials in settings/config, not opaque receipt values if avoidable; runtime-state storage may be
plaintext unless encryption is configured.

Use native checkpoint/collector modules for their documented bounded waits. Merely awaiting `Task.Delay` inside
a plugin is not a durable wait. There is one orchestration instance/completion slot per context; combining
Timed batch release requires an explicit timezone. Ordinary retries
resume saved stage inputs, not an arbitrary call stack; side effects before an exception can repeat. Do not
swallow cancellation or simulate rollback with a success return. Compensation is not a distributed transaction.
For ambiguous/partial delivery, require receiver idempotency or operator reconciliation before replay.

`DurableBatchSchedule` exposes explicit first-message policy interval/BatchTime deadline calculation and
persisted-state validation. The collector's metadata mode pins the original timing facts and reuses them on
replay; do not rebuild deadlines from a newly created plugin instance's current clock. See the README for its
strict input/DST contract and deliberate differences from legacy invalid-input fallbacks. The native
`xml-batch-envelope-augment` exposes payload count and ordered original XML roots to header/body/trailer
renderers; it does not infer a business-record count or translate legacy maps. The same descriptors are
available to scratch-built and disabled imported flows.

## BizTalk developer migration and generated stubs

The importer analyzes project ownership, maps, policy exports, bindings, ODX shape inventory and pipeline
component names/property names. Existing conservative native proposals include supported XML links/constants,
simple decision expressions and XML field-rule policies. These are reviewable proposals, not automatic executable
orchestration equivalence. Generated C# scaffolds are available from the import UI and scaffold download endpoints.

- Orchestration scaffolds identify untranslated complex decisions/control-flow categories.
- Pipeline scaffolds contain stage/component inventory and property **names**, not deployment property values.
- Untranslated stubs compile against SDK public contracts and deliberately throw `NotSupportedException`.
  Automatically bound ODX source additionally executes the bounded subset below, but unresolved bindings still
  block it before effects. Neither artifact is silently wired/approved by registration.
- Downloading a source stub is not a complete plugin project. Add it to a package, supply an installer, implement
  the appropriate native interface(s), build and test. A receive/send pipeline often needs different parse,
  augment and render modules rather than one augment preserving an opaque component call.

For a legacy helper, explicitly identify body type, metadata/context reads/writes, ordering, external calls,
encoding/culture/timezone, assembly identity, error results and cancellation. Wrap compatible existing code where
appropriate; do not invent its behavior from its name. For every dynamic map, define a finite reviewed mapping
from source identity to registered renderer/settings. For ODX branches/loops/listen/scope, preserve hierarchy,
branch priority, message variables, correlation, durable continuation and exception/transaction semantics; a
flat list of modules does not capture those semantics.

Complete automatic translation remains a stretch goal beyond the bounded compiler below: accept only
constructs whose semantics are represented and tested, retain source locations/unsupported diagnostics,
and generate failing developer stubs otherwise. No source expression is executed simply because import
recognized it. The imported-flow execution gate remains closed until complete translation/verification exists;
editing the report or providing secrets cannot approve it.

## Structured ODX SDK and explicit bindings

`Mulse.Modules.Orchestration` exposes a version-1 source model and a bounded structural executor:

- `OdxProgramReader.Read(sourceText)` / `ReadFile(path)` / `ReadFileAsync(path, cancellationToken)` retain
  ordered children, parent IDs, source lines, OIDs, parent-link names and all direct designer properties,
  including expressions. XML external entities/DTDs are prohibited. Input is limited to 8,000,000 characters,
  10,000 elements and 64 nesting levels. `OdxProgram` / `OdxNode` can round-trip through JSON.
- The import UI's **Download developer control-flow model (.json)** exports the selected orchestration via
  `POST /api/biztalk-import/control-flow/download`, using the same request as the scaffold downloads. This
  is an explicit private-source export: unlike the saved names-only report, it contains literal property
  values and expression bodies. Do not publish it or treat it as sanitized configuration.
- `OdxExecutionFrame` carries the original `FlowExecutionContext`, current `IntegrationBatch`, module step,
  named message batches, JSON variables and current caught CLR exception. Developers explicitly bind node
  IDs to `OdxActivity`, `OdxCondition` and `OdxTransactionScope` delegates in `OdxExecutionBindings`.
- `OdxProgramExecutor.ExecuteAsync` requires the complete selected `ServiceDeclaration`, preserving ordered
  first-match branches/final else, sequential construction/body groups, zero-or-more loops and nested local
  catch scopes. Service declaration bindings initialize before its single body, including port declarations
  stored after that body in source XML. All required bindings, including untaken branches, are checked before
  any bound activity. Declarations, receives, sends, listen/delay, assignments, rules, helper calls and metadata children are
  **not silently skipped**: the corresponding activity binding owns their full semantics.
- Conditions can use the automatic typed compiler below or explicit developer bindings; arbitrary source C#
  is never executed. Transaction markers require an explicit enclosing-scope transaction delegate. Its body
  includes local catch handling, so the adapter must establish and verify appropriate rollback/commit behavior;
  no BizTalk/SQL/distributed transaction semantics are inferred. Exceptions from the transaction adapter itself
  escape that scope.
- Catch matching uses CLR exception inheritance/full names without loading a type from source. Fault-message
  catches are unsupported. Cancellation and execution-limit exceptions bypass source catches. Loops stop
  after 10,000 iterations; one execution has at most 100,000 control-flow operations. Activity code must
  still honor cancellation and enforce its own I/O/resource limits.

For example, inside a raw augment implementation, after selecting a service and preparing complete bindings:

```csharp
var frame = new OdxExecutionFrame(program, serviceContext, inputBatch, step);
await new OdxProgramExecutor().ExecuteAsync(
    program, serviceDeclaration, bindings, frame, cancellationToken);
return frame.Batch;
```

`OdxProgramExecutor` executes an explicitly bound **in-process** subset; its frame/program counter are not
durable. Suspension alone does not create a resumable continuation. For persisted execution use the separate
`OdxDurableExecutor` described below. Neither executor approves imported drafts or infers transactions.
Existing helpers/pipelines have not been rewritten. `OdxControlFlowTests` exercise in-process execution,
including a native flow with parse/render/delivery; `OdxControlFlowApiTests` verify scoped downloads.

## Automatic typed ODX expressions and retained stubs

`OdxAutomaticBindings.Compile(program)` automatically binds supported decision/loop expressions and scalar
assignment shapes to the structural executor. `Diagnostics` records each original node ID/source line as
`Translated`, `Structural` or `RequiresBinding`. These are syntax/structural classifications, not proof that
schema facts, all input types or external behavior have been supplied. Diagnostics retain the original
compilation result even after developers add bindings.

The supported expression subset is string/Boolean/Int32/null literals and typed JSON facts, dotted member
access, checked integer arithmetic, comparisons, same-type equality, short-circuit logic, parentheses,
ordinary C# string escapes, comments, bounded two-argument `Regex.IsMatch` and parameterless `ToString()` on
non-null scalar facts (invariant Int32 formatting, never CLR object invocation). Exact fully qualified promoted
property reads use `frame.Messages[messageName]` and require exactly one payload and an existing metadata key;
metadata values remain strings, not inferred schema types. Statements support scalar `=`, `+=`, `++`, `--`
and semicolon-separated sequences. Unsupported syntax anywhere rejects the whole shape before any statement
in that shape executes. Limits are 65,536 characters, 8,192 tokens, 64 parsing levels, 256 binary operations
and a 100 ms timeout per regex match.

Developers must provide schema/message adapters and explicitly initialize facts not covered by supported
source initializers in `frame.Variables`. Unknown roots, missing promoted properties, invalid object access,
type mismatch and overflow fail explicitly. A missing member of an existing JSON object is null; strings are
never implicitly parsed into numbers/booleans. This is not arbitrary XML distinguished-field support.
Explicit service-level String/Int32/Boolean `InitialValue` expressions are now automatically initialized and
type-checked before the service body, even if metadata lists the body first. These source initializers take
precedence over preloaded facts and persist without reinitialization on resume. Duplicate service variables
are rejected; scoped/shadowed, missing-initializer, constructor-backed and non-scalar declarations remain explicit
bindings, as do DateTime/XML operations and arbitrary CLR/helper calls. No defaults or CLR constructors are inferred.

```csharp
var compilation = OdxAutomaticBindings.Compile(program);
// Supply reviewed activity/schema bindings and transaction adapters here, keyed by source node ID.
compilation.EnsureComplete(serviceDeclaration, cancellationToken);
var frame = new OdxExecutionFrame(program, serviceContext, inputBatch, step);
await new OdxProgramExecutor().ExecuteAsync(
    program, serviceDeclaration, compilation.Bindings, frame, cancellationToken);
```

`EnsureComplete` uses the executor's side-effect-free `Validate` against the current bindings; explicitly
binding an activity owns its child metadata, and a transaction marker requires a delegate on its containing
scope. It checks all branches before effects, including untaken branches. No-argument `EnsureComplete()` is
available for models containing exactly one service. Explicit bindings can deliberately retain failing helper
stubs; installing such a binding does not prove its implementation or permit imported execution.

Each supported single-service source now exports `TranslatedOdx_*.cs` and `.translation.json` alongside the
existing starter artifacts. Generated C# embeds the private source model, runs the automatic compiler and
requires complete bindings before creating/executing a frame. It compiles against the public SDK without
host global usings. Translation reports omit literal expressions; malformed or multi-service sources produce
an explicit blocked report instead of an empty successful result. Neither artifact is persisted into the
sanitized migration inventory. Keep source-bearing downloads private. Generated code still needs a package,
installer, schema/transport/component implementations and tests; it is not automatically installed in drafts.

`OdxAutomaticTranslationTests` verifies actual automatic nested branch/loop execution, typed syntax, rejection,
limits and binding preflight. `OdxGeneratedTranslationTests` exercises real import/export, compiles generated
C# and verifies retained helpers still fail explicitly. These compiler tests do not establish full imported
Billing equivalence.

## Persisted ODX execution and message adapters

`OdxDurableExecutor` uses `IFlowOrchestrationStateStore` to persist an explicit nested execution stack, typed
facts, named messages, replies, accepted message IDs, source receipts and listen choices. Use it under the
host's per-flow execution lock; the store is single-host, not a distributed lock/CAS service. Store keys are
case-insensitive: SDK callers must encode case-sensitive instance values into distinct keys; the installed native
node uses SHA-256 digests and limits source instance values to 2,048 characters. Supply a stable binding version:
changing the source model, bindings or interpreter semantics blocks pending-instance execution before effects.
The scoped-correlation runtime pins a new interpreter revision; finish existing instances under their previous pinned
runtime/definition rather than rewriting cursor hashes or discarding held work.
`OdxDurableBindings` combines automatic scalar bindings with explicit receive-subscription and UTC-deadline
factories. Receives require one payload and exact ordinal port/correlation matches, plus operation and
Request/Response identities when specified. Listen races persist all deadlines on entry; an already-due timer
wins over a later message, which remains unconsumed. A resumed scope catch exposes the original message/stack
and exception type metadata, not the original CLR object.

`OdxReceiveBindings.Bind` compiles receive/send-initialized correlation declarations local to services and scopes,
using exact source OID `StatementRef` targets and fully qualified correlation types. Supply sibling source models
for external type definitions; their services are never executed. Each set requires exactly one initializer and
lexically contained receive/send uses; correlation parameters are rejected. Scoped declarations initialize before
scope statements on each entry; their snapshots persist across waits and are removed on exit or exception unwind.
Following receives/sends use pinned promoted-property snapshots, not the latest input. Every correlated send requires
one named payload. Missing, mismatched, case-changed, uninitialized or reinitialized properties fail before transport
or its effect intent. Multiple sets are validated before any snapshot changes publish. Filters support up to 128
AND Equals/Exists predicates; Equals accepts string/Boolean/Int32 literals, and promoted keys/string values are ordinal.
Unsupported predicates, ambiguous types and activation topology fail explicitly. Supported activation is one first-body
activating receive followed by explicitly non-activating receives. Deterministic `AcceptedReceives` and `PreparingSends`
callbacks may update only the frame; they must never perform external effects. These hooks belong to the durable
executor; the in-process executor's activity collection alone does not run them.

A rejected new activation creates no checkpoint and accepts no receipt. An unmatched arrival at an existing wait
neither acknowledges nor fails current/retained source receipts and is excluded from generic stage retries.
It must be routed/reconciled explicitly; this is not a general MessageBox subscription router.

Every external activity persists intent before invocation. An unresolved intent freezes automatic replay;
reconcile the destination before attempting recovery. Checkpoint failures bypass source catches and host
stage retries. Only reviewed deterministic frame-local activities belong in `FrameLocalActivities`; arbitrary
plugins, renderers and helpers are not assumed pure. Safely persisted running positions without a wait are
scheduled for idle continuation; completed/faulted/ambiguous positions remain held for downstream completion
or reconciliation. There is no operator reconciliation API yet. Atomic/long-running transactions are rejected:
a local checkpoint is not a BizTalk/DTC transaction.

`OdxMessageBindings` supplies reviewed adapters for complete GetType/transform statements, named-message
copies, explicit ordered renderer-stage pipeline equivalents, logical sends and request-port Response sends.
Dynamic map identities are allowlisted; `Type.GetType` is never executed. Renderers use `IRenderModuleInvoker`
leases, and multi-input argument order is retained. Complete map shapes may append promoted-property writes to
the transform output; all values are evaluated before rendering and stale output-fact reads are rejected.
Billing's XML trailer prefix (`new XmlDocument`, `LoadXml`, message assignment, then a single-input transform of
that same message) is compiled with bounded XML parsing, DTD/external resolution disabled, and no CLR constructors.
The XML temporary and transformed output publish only after rendering succeeds. This is not arbitrary DOM support.
Message replacements invalidate stale typed facts. These adapters do not compile original `.btp` components,
infer XML distinguished fields or translate helpers.

The installed **Durable ODX program** (`odx-program-augment`) exposes the supported subset in the Designer.
Configure private `programJson`, initial typed facts, stable source ID/instance metadata, exact incoming port,
operation/message identity, additional pinned correlation keys, explicit `{nodeId, seconds}` native timers,
visual dynamic-map choices, output message and optional reply port. `referencedProgramsJson` supplies an array of
up to 32 private source models for external correlation types. Set `correlationMetadataKey` blank to derive
instance keys from the exact property tuple of correlation sets initialized by the first receive; otherwise
its default `group` selects the explicit metadata key. Source-derived tuples preserve case and field boundaries
in the case-insensitive host store. This mode does not infer send-initialized/scoped correlation routing.
The node binds scalar expressions, copies, complete maps/trailers, replies, supported source correlations/receives and
explicit scalar service initializers. `sendsJson` supplies up to 128 adapters for exact non-Response source Send IDs:

```json
[{"nodeId":42,"module":"document-repository-store","settings":{"path":"{{config:billing-output}}"},"pipeline":[{"module":"xslt-render","settings":{"xsltPath":"{{config:billing-xslt}}"}}]}]
```

Use the source-model's actual node IDs, verified module IDs/settings and reviewed pipeline equivalents; this example is
not a Billing configuration. Pipeline entries run in order (at most 32) on the outbound batch without replacing the
source named message used by later sends/replies. `IDeliverModuleInvoker` leases delivery modules; setting
`expectsResponse=true` requires `IRequestResponseDeliverModule`, returning responses for native capture rather than
silently dropping them. This is not a named ODX response-receive router. Both renderer and delivery dependencies are
tracked for availability and package unloading, including config-backed plans. Stages/sends run under the existing
external-effect journal; an ambiguous failure freezes replay, not an automatic resend. Setting-level retry/compensation
inside this adapter is not provided. Other declarations, helpers and transactions still require translation.
Native timer seconds do not interpret source DateTime expressions. Keep all source-bearing JSON private.

The host polls persisted deadlines without fetching or replaying upstream steps. Source acknowledgements wait
for downstream completion; delivery/source-notification retries retain their own stages. Pending checkpoints
prevent unsafe definition/configuration edits and destructive maintenance. Stable message IDs must be unique
per source event within an instance; reusing an ID for different content violates the receive contract. One
ODX node is allowed per flow, without other stateful collector nodes or conditional execution. Limits include
64 stack levels, 10,000 loop iterations/accepted messages/payload snapshots, 100,000 persisted operations,
12,000,000 payload bytes and 24,000,000 serialized checkpoint characters.

`OdxDurableContinuationTests`, `OdxSourceReceiveTests`, `OdxMessageBindingTests`, `OdxOutboundRuntimeTests`,
`OdxNativeRuntimeTests` and `OdxNativeUiTests` verify restart, exact source filters/correlations/operations, independent clients, XML trailers,
retained replies, ambiguous writes/effects, real XSLT leases, idle polling, downstream retry and visual map settings.
These are native/synthetic tests, not end-to-end equivalence of the imported Billing project.
Imported drafts remain disabled and original helper stubs remain deliberately untranslated.

## Verification checklist

- Compile against the current SDK without relying on the host's internal/global usings.
- Unit-test real transformations, metadata preservation, schema/encoding mismatch, zero/multiple records,
  invalid settings, cancellation and measurable limits; never use production messages/secrets for tests.
- Run a composed flow through real source/parse/map/policy/render/delivery code with synthetic golden fixtures.
- Test idempotent source callbacks, retry after partial effects, disk restart, disabled flows, and concurrent runs.
- For plugins, test real load/invoke/reload/unload and weak-reference context collection, not just catalog presence.
- Test the UI saves/reopens the same definition and previews match runtime behavior. Test unsupported definitions
  remain repairable and never become a partially executed successful flow.

Run the existing solution build and relevant `Service.Tests` cases; `ModulePackageLifecycleTests`,
`BizTalkOrchestrationScaffoldTests`, `SourceOutcomeTests`, `DurableBatchTests`, `TimedDurableBatchTests` and
`ManualBatchReleaseApiUiTests` provide executable examples of these boundaries.
