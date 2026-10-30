# BizTalk Import Compatibility Plan

## Goal

Import BizTalk projects into Mulse and automatically map as much functionality as possible into editable, executable Mulse workflows without running the BizTalk runtime directly.

The target experience is:

```text
BizTalk project -> analyzed artifacts -> canonical Mulse model
			 -> Flow Designer + Mapping Designer -> executable Mulse workflow
```

Target behavior: imported workflows must remain disabled until required configuration and unsupported behavior are resolved. Today imports start disabled, but semantic readiness enforcement is not implemented; users can still enable incomplete drafts. The same Mulse modules, mappings, transforms, routes, and diagnostics must also be available when creating a workflow from scratch in the UI.

## Product surfaces

### Flow Designer

Represents where messages move:

```text
Receive -> Parse -> Map/Transform -> Enrich/Route -> Render -> Deliver
```

It must support both imported and manually-created workflows with the same toolbox and configuration controls.

### Mapping Designer

Represents how a message changes shape. It will be available from the Flow Designer and as a first-class editing surface for manually-created mappings.

It should support source/target schema trees, field mappings, constants, defaults, repetitions, conditions, functoids/functions, script transforms, metadata/context values, XSLT preview, generated-code preview, sample execution, and unsupported-construct diagnostics.

The canonical model is:

```text
BizTalk .btm -> canonical Mulse mapping model -> XSLT, script-transform, or custom transform modules
```

Raw XSLT is an output/runtime representation, not the primary editing format.

## Compatibility workstreams

1. **Canonical migration model and diagnostics**
   - Preserve source paths, project identity, artifact ownership, translation status, dependencies, warnings, and confidence.
   - Use statuses: Fully translated, Translated with review required, Stub generated, Unsupported, Configuration required.

2. **Project and dependency discovery**
   - Use path-based project identity and resolve project references and HintPath values.
   - Avoid whole-project artifact contamination.
   - Handle duplicate assembly names and distinguish deployed artifacts from copies/proofs.

3. **Mulse message abstractions**
   - Add equivalents for BizTalk message body/parts, context properties, promoted routing properties, message type, and correlation metadata.
   - Preserve the concepts of `IBaseMessage` and `XLANGMessage` without exposing BizTalk runtime APIs.

4. **Script and custom-code modules**
   - Add `script-transform` for translated map-level C# script functoids.
   - Add `custom-code-augment` for message-context, property-promotion, dynamic-map, external-assembly, pipeline, and orchestration behavior.
   - Never silently pass through behavior that was not translated.

5. **Mapping translation and Mapping Designer**
   - Translate direct links, constants, namespaces, repeating records, and safe common functoids first.
   - Extract and classify C# script functoids; translate portable code and generate editable stubs for BizTalk-specific or unknown code.
   - Make every mapping tool available in the UI for imported and scratch-built workflows.

6. **Pipeline translation**
   - Map XML, flat-file, HL7, MIME, message type, promotion, dynamic map, preprocessing, and storage behavior to Mulse modules.
   - Generate explicit custom-code modules or stubs for unsupported BizTalk components.

7. **Orchestration translation**
   - Preserve receive/send ordering, decisions, loops, delays, listens, scopes, exceptions, correlation, transactions, compensation, and child workflow calls.
   - Convert simple behavior to executable Mulse stages and scaffold complex behavior with blocking diagnostics.

8. **Bindings and routing**
   - Import receive locations, send ports, filters/subscriptions, transports, pipelines, maps, retries, schedules, request/response behavior, and adapter settings.
   - Convert secrets/endpoints to configuration references, but do not treat secrets as the only migration requirement.

9. **Readiness gates**
   - Separate informational warnings from blocking gaps.
   - Prevent execution when required stubs, unsupported semantics, missing mappings, or unresolved configuration remain.
   - Show source evidence and remediation actions in the UI.

10. **Verified SpaceTRAX pilot**
	- Start with a small real map and exact schemas using synthetic input and local output.
	- Compare Mulse output with BizTalk golden output, including namespaces, repetition, empty values, and failures.
	- Expand to a pipeline and then an orchestration only after map fidelity is demonstrated.

## Testing strategy

- Golden map fixtures for direct links, namespaces, repetitions, common functoids, C# scripts, and unsupported constructs.
- Pipeline fixtures for context, promotions, dynamic maps, HL7, flat files, and multipart messages.
- Orchestration fixtures for ordering, branch effects, loops, correlation, waits, exceptions, and message construction.
- Binding fixtures for transport, filters, retries, and request/response behavior.
- End-to-end tests for import -> Mapping Designer edit -> synthetic execution -> output comparison.
- UI tests proving every supported module and mapping operation is available to scratch-built workflows as well as imported workflows.

## Implementation order

1. Canonical migration models and diagnostics.
2. Path-aware dependency and project resolution.
3. Mulse message/context abstractions.
4. Mapping Designer foundation and scratch workflow parity.
5. Reliable direct-link map translation.
6. `script-transform` and C# script extraction/classification.
7. Pipeline translation.
8. Binding and routing translation.
9. Orchestration translation.
10. Readiness gates and SpaceTRAX pilot golden tests.

## Implementation status (updated 2026-10-30)

**Overall: in progress, not fully implemented.** Completion below refers only to the explicitly listed increment, not to BizTalk-equivalent execution.

| Workstream | Status | Current evidence / remaining work |
|---|---|---|
| Canonical migration model and diagnostics | Partial foundation | Structured project-reference resolution and candidate source-project paths are exposed through the API and UI. Canonical migration/mapping models, artifact-level translation statuses, confidence, and persistent provenance remain pending. |
| Project and dependency discovery | Partial; first increment verified | Full-path identity replaces name-keyed lookup. Explicit project paths, conservative HintPath ownership inference, unique-name fallback, cycles, and colliding draft IDs are handled. Automatic out-of-scope discovery, evaluated MSBuild output paths, custom-project dependency chains, and exact orchestration/artifact/binding ownership remain pending. |
| Mulse message abstractions | Pending | Body/parts/context/promotions/correlation model still to be designed and implemented. |
| Script and custom-code modules | Pending | No new script execution or custom-code runtime added. Existing generated stubs are not completed translations. |
| Mapping Designer and map translation | Early UI foundation only | Existing sample-field mapping controls have an explicit Mapping Designer button/panel. This is not yet a schema-to-schema designer or a persisted imported-map editor; scratch-workflow persistence, transforms, schema trees, previews, sample execution, and automatic imported-map wiring remain pending. |
| Pipeline translation | Pending beyond existing importer heuristics | No new pipeline behavior translation in this increment. |
| Orchestration translation | Pending beyond existing decision translation/scaffolding | Loops, waits, correlations, transactions, exceptions, and other workflow semantics still require translation. |
| Bindings and routing | Pending beyond existing heuristics | Exact port/subscription/artifact relationships and transport fidelity remain pending. |
| Readiness gates | Pending | Disabled-by-default is not semantic enforcement. Required stubs and unsupported behavior must eventually block enable/run, including API paths. |
| Verified SpaceTRAX pilot | Pending | Whole-root analysis now works, but no golden-output execution comparison has been performed. |

### Completed increment: path-aware dependency resolution

- Preserve raw reference includes, optional HintPath values, resolution method, selected project path, alternative candidate paths, and an explanation.
- Identify projects by normalized full path rather than assembly name. Duplicate project/assembly names no longer cause the name-keyed dictionary exception or merge dependencies.
- Resolve explicit `ProjectReference` paths within the analyzed workspace, even without `<Name>` metadata or when that metadata disagrees with the path.
- Infer assembly references from a concrete HintPath only when the output name and closest owning source-project directory agree. Compiled assemblies do not need to exist and are not loaded or executed.
- Use a unique-name fallback only when no path evidence was supplied. Ambiguous names, stale/mismatched paths, and unevaluated MSBuild expressions produce explicit diagnostics instead of substituting a namesake.
- Traverse BizTalk dependencies by path, handle cycles/diamond graphs, and deduplicate referenced artifact paths. Custom assembly consumers are identified by source-project path, including consumers reached through BizTalk libraries.
- Keep draft IDs and generated configuration-reference prefixes distinct when project or orchestration names normalize to the same ID. Existing non-colliding IDs retain their format. Collision suffixes are deterministic for the same analyzed paths/order; relocating projects or changing the workspace can change IDs.
- Expose **Project dependencies** in the existing **Import from BizTalk** panel. Each flow candidate shows its source-project path. Direct/transitive unresolved-reference warnings remain visible in the draft's follow-up panel after loading it onto the canvas.
- Update the README with the implemented behavior and its limits.

### Verification results

- **53 targeted tests passed**, including **16 new dependency/provenance/UI regression cases** plus existing BizTalk analyzer and Flow Designer rendering tests.
- **Full solution build succeeded** after the implementation and documentation updates; `git diff --check` reported no patch whitespace errors.
- New coverage includes duplicate project and custom-assembly names, explicit references without reliable name metadata, HintPath inference without compiled binaries, ambiguous/missing/mismatched paths, MSBuild expressions, unique-name fallback, external references, cycles/diamonds, additional/overlapping source scopes, normalized orchestration-name collisions, draft/config ID consistency, transitive warnings, and API-to-UI serialization/rendering.
- Read-only analysis of the complete SpaceTRAX Interface source root succeeded: **52 BizTalk projects**, **17 custom assemblies**, **53 candidates**, **53 unique draft IDs**, **all drafts disabled**, and **2 duplicate project-name groups** retained separately.
- Reference results for that analysis: **7 ProjectPath**, **10 HintPath**, **126 Unresolved**, **441 External**. These counts describe source-project resolution, not translation readiness. Unresolved references need review; External means no matching source project exists in the analyzed workspace, not that an assembly is supported or executable.
- No imported flow was executed, no BizTalk assembly was loaded, no production endpoint was contacted, and the external source repository was not modified. The temporary verification harness was kept outside the repository and removed after use.

### Known limits / next increments

1. Introduce canonical artifact translation statuses and structured blocking/review diagnostics; persist source provenance through save/load instead of only analysis responses.
2. Add readiness enforcement across UI, enable/update APIs, and runtime execution. Existing scaffolds may contain pass-through placeholders; this increment does not make them safe to run or enforce their completion.
3. Turn the Mapping Designer foundation into a real saved mapping model, shared by imported and manually-created flows. Wire imported direct-link maps into executable transformations and verify sample output.
4. Extend discovery/resolution to projects outside the supplied scopes, C# project dependency chains, MSBuild property/configuration evaluation, and shared/deployment output directories. HintPath ownership remains a labeled inference, not proof of assembly equivalence.
5. Resolve map/schema/pipeline ownership per orchestration and exact binding/port relationships. Path-aware dependency inventory does not eliminate existing project-wide artifact heuristics.
6. Add message/context abstractions, approved script translation and explicit non-executable stubs, then pipeline/binding/orchestration translation in the planned order.
7. Complete a small local synthetic-input/golden-output SpaceTRAX pilot before claiming functional migration fidelity.

Record each subsequent increment here with its scope, UI/manual-workflow entry points, verification results, and remaining gaps. Do not mark an entire workstream complete based only on scaffolding or a successful build.
