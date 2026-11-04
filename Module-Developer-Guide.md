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
- Scaffolds compile against SDK public contracts and deliberately throw `NotSupportedException`. They are not
  silently passing data through and are not wired/approved by registration.
- Downloading a source stub is not a complete plugin project. Add it to a package, supply an installer, implement
  the appropriate native interface(s), build and test. A receive/send pipeline often needs different parse,
  augment and render modules rather than one augment preserving an opaque component call.

For a legacy helper, explicitly identify body type, metadata/context reads/writes, ordering, external calls,
encoding/culture/timezone, assembly identity, error results and cancellation. Wrap compatible existing code where
appropriate; do not invent its behavior from its name. For every dynamic map, define a finite reviewed mapping
from source identity to registered renderer/settings. For ODX branches/loops/listen/scope, preserve hierarchy,
branch priority, message variables, correlation, durable continuation and exception/transaction semantics; a
flat list of modules does not capture those semantics.

Automatic translation is a stretch goal: accept only constructs whose semantics are represented and tested,
retain source locations/unsupported diagnostics, and generate failing developer stubs otherwise. No source
expression is executed simply because import recognized it. The imported-flow execution gate remains closed
until complete translation/verification exists; editing the report or providing secrets cannot approve it.

## Structured ODX SDK and explicit bindings

`Mulse.Modules.Orchestration` exposes a version-1 source model and a bounded structural executor:

- `OdxProgramReader.Read(sourceText)` / `ReadFileAsync(path, cancellationToken)` retain ordered children,
  parent IDs, source lines, OIDs, parent-link names and all direct designer properties, including expressions.
  XML external entities/DTDs are prohibited. Input is limited to 8,000,000 characters, 10,000 elements and
  64 nesting levels. `OdxProgram` / `OdxNode` can round-trip through JSON.
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
- Conditions are developer-written code; the executor never evaluates source C# strings. Transaction markers
  require an explicit enclosing-scope transaction delegate. Its body includes local catch handling, so the
  adapter must establish and verify appropriate rollback/commit behavior; no BizTalk/SQL/distributed
  transaction semantics are inferred. Exceptions from the transaction adapter itself escape that scope.
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

This executes only an explicitly implemented in-process subset. The frame/program counter are **not durable**;
there is no automatic checkpoint/resume of nested loops, pending listens, correlation subscriptions, parallel
races or transaction recovery. Suspension stops subsequent activities but does not create a resumable ODX
continuation. Use native durable modules or implement and verify an application-owned continuation. This SDK
is not wired to imported drafts and does not change their execution gate. Existing helpers/pipelines have
not been rewritten. `OdxControlFlowTests` exercise these contracts, including a bound program running through
an actual native flow with parse/render/delivery; `OdxControlFlowApiTests` verify source ownership and download.

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
