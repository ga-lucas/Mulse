# BizTalk Import Compatibility Plan

## Goal

Import BizTalk projects into Mulse and automatically map as much functionality as possible into editable, executable Mulse workflows without running the BizTalk runtime directly.

The target experience is:

```text
BizTalk project -> analyzed artifacts -> canonical Mulse model
			 -> Flow Designer + Mapping Designer -> executable Mulse workflow
```

Target behavior: imported workflows must remain disabled until required configuration and unsupported behavior are resolved and verified. Flows carrying migration reports now have a fail-closed enable/run gate: they remain editable as disabled drafts, but cannot execute until verified translation support exists. Report statuses and configuration review are not approvals. Older flows without migration metadata cannot be identified retroactively and need reanalysis/re-import to receive the gate. The same Mulse modules, mappings, transforms, routes, and diagnostics must also be available when creating a workflow from scratch in the UI.

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
   - Use statuses: Fully translated, Review required (not verified), Stub generated, Unsupported, Configuration required. Inventory or partial translation must never imply complete translation.

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
| Canonical migration model and diagnostics | Report and scalar XML mapping increments verified; workstream partial | Versioned reports preserve source provenance, statuses, diagnostics, remediation and warnings through save/reopen, copies and backups. A versioned editable scalar XML mapping model now persists in render-node settings. Exact imported artifact ownership, confidence evidence and authoritative/live readiness remain pending. |
| Project and dependency discovery | Path, ODX owner-selection, workspace-map and schema-source evidence increments verified; workstream partial | Full-path identity, explicit paths, conservative HintPath inference, cycles and colliding draft IDs are handled. Reports index separate/unreferenced map projects and case-sensitive schema class candidates from explicit Namespace/TypeName metadata or labeled literal naming conventions. Ambiguous/missing/incomplete source evidence stays explicit. Evaluated MSBuild/deployed identities, out-of-scope discovery, custom-project dependency chains, exact reference-path assertions and proven policy/map selection remain pending. |
| Mulse message abstractions | Pending | Body/parts/context/promotions/correlation model still to be designed and implemented. |
| Script and custom-code modules | Bounded script-evidence inventory verified; runtime translation pending | Reports retain script language, owning functoid ID, source line, size, hash and textual BizTalk API hints, not script bodies. This is not a portability/safety classification or script execution. Existing generated stubs are not completed translations. |
| Mapping Designer and map translation | Schema-browser, scalar .btm proposal, missing-value and XSD-validation increments verified; workstream partial | Scratch and imported drafts share XSD upload/paste, scalar field selection, explicit proposal application, persisted source-root/schema contracts and Error/Omit/Default policies. Preview and actual render share validation/runtime behavior. Source-schema file candidates are visible in migration reports. Repetitions, arbitrary conditions/functions, functoids/scripts, evaluated/deployed schema ownership, automatic orchestration wiring and trusted output parity remain pending. |
| Pipeline translation | Pending beyond existing importer heuristics | No new pipeline behavior translation in this increment. |
| Orchestration translation | Source-evidence and fail-explicit scaffold increments verified; translation partial | Persisted hierarchical designer inventory and coded behavior blockers now include real While shapes, BRE calls, assignments, ports, failures and waits. New generated stubs compile against the SDK and fail explicitly until implemented. Loops, waits, correlations, transactions, exceptions, and complete orchestration semantics still require translation. |
| Bindings and routing | Pending beyond existing heuristics | Exact port/subscription/artifact relationships and transport fidelity remain pending. |
| Readiness gates | Fail-closed increment verified; verified approval pending | Flows carrying migration reports cannot be enabled or run through UI/API/runtime, schedules, push registration, or processing retries. Saved provenance cannot be replaced by flow updates. Disabled editing, duplication and terminal source acknowledgements remain available. No manual override or verified approval/revalidation path exists yet; legacy flows without metadata need re-import. |
| Verified SpaceTRAX pilot | Source/import assessment verified; executable parity pending | BillingServiceProcess now analyzes from its ODX through its exact owning project, with three Billing candidates and specific missing behavior identified. No BizTalk golden-output comparison or production execution has been performed. See BillingServiceProcess-Migration-Guide.md. |

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

### Verification results: dependency-resolution increment

- **53 targeted tests passed**, including **16 new dependency/provenance/UI regression cases** plus existing BizTalk analyzer and Flow Designer rendering tests.
- **Full solution build succeeded** after the implementation and documentation updates; `git diff --check` reported no patch whitespace errors.
- New coverage includes duplicate project and custom-assembly names, explicit references without reliable name metadata, HintPath inference without compiled binaries, ambiguous/missing/mismatched paths, MSBuild expressions, unique-name fallback, external references, cycles/diamonds, additional/overlapping source scopes, normalized orchestration-name collisions, draft/config ID consistency, transitive warnings, and API-to-UI serialization/rendering.
- Read-only analysis of the complete SpaceTRAX Interface source root succeeded: **52 BizTalk projects**, **17 custom assemblies**, **53 candidates**, **53 unique draft IDs**, **all drafts disabled**, and **2 duplicate project-name groups** retained separately.
- Reference results for that analysis: **7 ProjectPath**, **10 HintPath**, **126 Unresolved**, **441 External**. These counts describe source-project resolution, not translation readiness. Unresolved references need review; External means no matching source project exists in the analyzed workspace, not that an assembly is supported or executable.
- No imported flow was executed, no BizTalk assembly was loaded, no production endpoint was contacted, and the external source repository was not modified. The temporary verification harness was kept outside the repository and removed after use.

### Completed increment: persistent migration reports and artifact diagnostics

- Add a versioned, portable `FlowMigrationReport` to the saved pipeline and API/UI contracts. Preserve source system, source-project path, selected orchestration, artifact paths/statuses, coded diagnostics with severity/remediation, and original import warnings.
- Classify missing/unreadable/unrecognized maps and unsupported functoids explicitly instead of dropping them from the report. Referenced maps use their owning project's analysis. Direct-link starter XSLT remains **review required**, with a blocking **not wired** diagnostic; it is not installed or made executable by this increment.
- Identify generated orchestration modules as **stub generated**, never completed translations. Split candidates retain only their own orchestration report plus shared artifact inventory. Pipeline/custom assembly behavior remains unsupported; schemas and inferred bindings require review.
- Include unresolved dependencies, missing bindings, configuration references, and a mandatory unverified-semantics diagnostic. No generated artifact is classified fully translated. `FullyTranslated` and `ConfigurationRequired` remain available contract statuses; configuration requirements currently appear as report diagnostics.
- Expose an expandable **Migration report** on every candidate in **Import from BizTalk**, and in **BizTalk import follow-up** after loading, saving, or reopening the flow in the designer. Show source evidence, blocking counts, artifact details, and next actions. Clearing/editing the canvas does not silently erase the snapshot.
- Preserve the report through ordinary and designed-flow creation, updates, category/enable copies, duplication, configuration resolution, disk reload, and encrypted workspace backup/restore. Older clients omitting the new property retain the existing report; old/manual flows without it remain compatible.
- Reports are import-time snapshots, not live validation or execution approvals. Resolving config or editing a map does not clear a diagnostic. This initial increment accepted client-editable metadata, not trusted approval evidence; the subsequent readiness increment below protects saved provenance and blocks imports regardless of submitted status.
- Scratch-created flows keep the same existing designer and save paths without requiring import metadata. This increment adds no import-only transformation engine and does not complete the shared Mapping Designer or script tooling.

### Verification results: migration-report increment

- **154 targeted tests passed**, including **14 new migration-report cases**, extended backup coverage, and existing BizTalk, mapping/API, storage, configuration, flow-management, workspace API, and designer rendering regressions.
- New coverage verifies direct/partial/functoid/malformed/missing maps, referenced-map analysis, custom code, unresolved dependencies, schema/pipeline classification, orchestration split/stub isolation, string-enum API/UI round trips, real disk reload, metadata-omitting updates, copies/categories, designed creation, configuration resolution without report secret substitution, designer apply/save/update/reopen/clear, rendered remediation, and old flows without metadata. Encrypted workspace backup/restore also retains the snapshot.
- **Full solution Debug build succeeded** with no compilation errors. Build output includes ImageSharp licensing and test-project validation-source-generator warnings, plus the installed preview SDK notice. Visual Studio's build/error tools were unavailable, so validation used `dotnet build .\Mulse.slnx --no-restore` and targeted `dotnet test` commands.
- Verification used synthetic local fixtures; no BizTalk assembly or imported business workflow was executed, no production endpoint was contacted, and SpaceTRAX source was not modified. The whole-root SpaceTRAX counts above belong to the previous increment, not a new equivalence test.

### Completed increment: fail-closed migration readiness

- User-selected policy: keep imported flows blocked until verified translation support exists, rather than adding a manual review/acceptance override. No report status, empty diagnostic list, unknown report version, or configuration edit grants permission to run.
- Add one server-side migration guard, used by enabled ordinary/designed-flow creation and updates, including the dedicated enable API. Attempts return an explicit HTTP 409 conflict through the standard problem-details handler. Saving a valid disabled draft, disabling an older enabled import, categorizing a disabled draft, and duplicating it remain supported.
- Protect saved import provenance: omitting metadata retains the saved report; supplying a changed report through a flow update returns 409 instead of silently replacing it. Round-tripping the unchanged report still works. Copies, persistence, and backup restoration retain the gate.
- Guard the runtime before module leases, source fetches, transforms, or delivery; scheduled and push-dispatched runs use the same path. Re-read the definition after acquiring the execution lock. Previously enabled imports are blocked even if the stored enabled flag is still true.
- Do not register push sources for flows with migration reports. Existing registrations are removed during reconciliation; blocked registration is logged. Processing retry recovery fails explicitly and retains pending state instead of deleting it as a stale retry.
- Preserve terminal source-outcome notifications for work already completed: notification-only recovery can acknowledge its original receipts without fetching, parsing, transforming, or delivering again. Blocking such cleanup would strand source state or risk replay. Already-running executions are not cancelled by this increment.
- Show blocking guidance and migration reports in dashboard cards, disable imported Enable/Run controls, and prevent checking Enabled/saving enabled imports in the designer. Older enabled imports can still be disabled. Imported drafts remain editable; scratch-built flows without migration metadata use their existing modules and execution paths unchanged.
- Update importer warnings, API endpoint descriptions, README, and this plan to distinguish configuration review from verified translation approval.

### Verification results: readiness increment

- **215 targeted tests passed**, including **24 new readiness cases** and **one new dashboard/designer rendering case**, plus the updated report/designer regression and existing import, API, runtime, source-outcome, configuration, persistence, backup and workspace tests.
- **Full solution build succeeded** through Visual Studio. The targeted CLI test build still reports the existing validation-source-generator warning and installed preview SDK notice; neither prevented compilation or test execution. `git diff --check` found no whitespace errors.
- Coverage includes every translation status, empty/future-version snapshots, enabled ordinary/designed creation, omitted/replaced provenance, disabled edits/copies, legacy enabled definitions, all three trigger modes, scheduled dispatch, push registration without module leasing, all five processing retry stage kinds with retained state, disk reload/raw-store restoration, real HTTP 409 responses, and unchanged scratch-flow execution.
- A synthetic terminal callback test proves that notification-only recovery can finish with exactly one original fetch and one original delivery; no business processing is replayed after applying the migration gate. UI tests verify blocked controls, visible reports, disabled-draft guidance, and rejection before a designer save request is sent.
- No real BizTalk business workflow was executed, no production endpoint was contacted, and SpaceTRAX source was not modified. The gate is not proof that any translation is correct.

### Completed increment: persisted scalar XML Mapping Designer

- Add canonical `Mulse.Modules.Mapping.XmlMappingDefinition` version 1, saved as a render node's `mappingJson` setting. It models explicit namespace prefixes, a target root, named source/target paths, and text constants (including empty constants). XSLT is generated from this editable model, not stored as the primary model.
- Register `xml-mapping-render` in the built-in compatibility package. The module maps each XML payload independently, preserves metadata, and emits XML. Named source paths must select exactly one scalar element or attribute; missing, repeating, complex, or wrong-namespace values fail explicitly rather than selecting the first match or passing through the original message.
- Support namespace-qualified target roots/elements/attributes, shared target containers, unqualified names in no namespace, escaped literals and empty scalar values. Reject duplicate/overlapping targets, undeclared/reserved prefixes, unsupported model versions/properties, wildcard/predicate/function XPath expressions, and invalid definitions. No script or external document execution is introduced.
- Replace the temporary global mapping table with node-owned editors in **Flow Designer > Mapping Designer**. **Add XML mapping render** adds a render-only route (or fills a route missing a render); add a deliver node before saving. Existing XML mapping renders have an **Edit XML mapping** Properties action. Namespace, root, source-field, constant, add/remove-field and raw-definition controls write directly into that node's settings; normal **Create flow / Save flow** persists them on both new and existing flows.
- Existing join mappings remain in the per-join Properties editor; Mapping Designer links to those editors instead of keeping a separate table that disappeared on reopen or could overwrite every join. The designed-flow API remains available for existing clients.
- Add `POST /api/flow-designer/xml-mapping/preview` and its UI client. **Preview XML mapping** accepts an unsaved synthetic sample, shows output XML and generated XSLT, and uses the exact runtime engine without fetching sources or delivering data. Clear preview output on edits; ignore an in-flight response if the definition/sample changed. Invalid/future JSON is preserved for correction and shown as an editor error, not silently reset.
- Keep all import readiness gates and reports unchanged. Adding/editing/previewing a native map in an imported disabled draft is possible, but does not approve execution or automatically replace its existing BizTalk map stub/stylesheet reference. No existing `.btm` analyzer output is newly claimed as fully translated or automatically installed.
- Bound definitions to 262,144 characters, 128 fields, 32 namespace prefixes and 32 path segments; XML input to 1,000,000 characters and output to 4,000,000 characters. These limits apply to preview and runtime. DTDs, external resolution and XSLT scripts/document access are disabled. Sample XML is not saved with the flow.

### Verification results: scalar XML mapping increment

- **45 new mapping test cases** cover expanded names, nested target containers, attributes, constants/escaping/empty values, explicit cardinality failures, malformed/unsafe inputs, unsupported paths/models/namespaces, overlapping targets, size/output limits and cancellation.
- Tests exercise actual module registration through the API, disk reload and duplication, a complete source -> XML parser -> native mapping render -> captured delivery, and real HTTP preview responses with the UI API client. Preview and runtime output are compared directly.
- UI tests exercise field edits through editor callbacks and designer create/update/reopen for both manual flows and report-bearing disabled drafts, rendered controls/native render creation, malformed/future-definition preservation, and preview output/error handling/invalidation. These are component-rendering tests, not a browser drag/drop test.
- **287 related tests passed; full solution build succeeded** through Visual Studio. The final targeted compilation reported no warnings, only the installed preview SDK notice; CS8785 was treated as an error. `git diff --check` found no whitespace errors. Visual Studio file-error inspection was unavailable, so compilation and test execution supplied validation.
- All samples are synthetic/local; no SpaceTRAX business map or production endpoint was executed. This is a bounded Mapping Designer foundation, not completed BizTalk map translation or a schema-to-schema tree editor.
- Follow-up to the prior increment's CS8785 warning: API test hosts now share one `AddValidation` call site. Generator validation remains enabled, with a two-host request-validation regression test; targeted builds treat CS8785 as an error rather than suppressing it.

### Completed increments: orchestration source evidence, ODX entry point and fail-explicit scaffolds

- Accept an `.odx` source path when exactly one sibling `.btproj` explicitly includes that full path as an XLang item. Analyze its complete owning project, preserving shared artifacts and sibling candidates. Reject missing/ambiguous/unreadable ownership explicitly; linked/nested files still require selecting their owning project/solution. Import UI explains the contract.
- Persist an optional orchestration inventory on each migration artifact: all designer shape types/counts (including unknown types), stable document-order IDs, parent IDs, original source lines, names and selected type/port/message/policy references. This is designer hierarchy, not a reconstructed executable graph. The original XML expressions are not executed or copied into this inventory.
- Add blocking diagnostic categories for expressions/message assignments and dynamic transforms, BRE policies, loops, ports/child calls/request-reply, failure/transaction scopes, waits and correlation. Counts are evidence of constructs requiring review, not proof they are active at runtime. Unreadable/invalid/empty/oversized designer sections produce `BT-ODX-NOT-ANALYZED`; DTDs and external XML resolution are prohibited, with an 8,000,000-character source limit.
- Recognize BizTalk's `While` shape in control-flow summaries and scaffold generation as well as `Loop`. This fixes a concrete coverage gap exposed by BillingServiceProcess's six While loops.
- Show this inventory under **Migration report > Orchestration > Orchestration source inventory**, both on import candidates and saved/reopened drafts. Reports remain import-time snapshots; reanalysis does not overwrite immutable saved provenance. Scratch-built flows retain the same existing native modules and mapping tools; no new runtime language or imported-only execution tool is introduced.
- Newly downloaded orchestration stubs use the actual SDK namespaces and `AugmentAsync` member, label themselves unimplemented, check cancellation and throw `NotSupportedException` instead of silently returning unchanged input. Existing downloaded files are not changed. Compiling/registering/implementing a stub does not lift the import gate. Import candidate, download API and canvas guidance reflect this restriction.

### Verification results: orchestration increments and Billing assessment

It records owning project/path, schema references, direct links, constants, unsupported links, functoid FIDs, and a conservative limitation/status. UI labels this as an inventory only; it does not assert policy selection or install a map. **15 migration-report tests passed**, covering separate referenced and unreferenced project provenance, report persistence, UI rendering, and the fail-closed gate.
- The portable regression suite covers source lines/hierarchy, namespace-prefix variations, unknown shapes, six blocker categories, malformed/DTD/empty/oversized XML, split-candidate isolation and exact/missing/ambiguous/unreadable ODX ownership. Existing report tests now include orchestration inventory through disk reload, API/client mappings, legacy updates, duplication, designer create/update/reopen and HTML-safe component rendering.
- A dedicated test builds the actual generated C# against Mulse.Modules with implicit/global usings disabled, then loads and invokes it to verify explicit failure and cancellation. The isolated test project has no package references and is cleaned up.
- **27 new portable cases; 314 related tests passed, zero failures/skips. Full solution build succeeded.** CS8785 was treated as an error during test compilation, not suppressed. `git diff --check` found no whitespace errors (only the existing Designer.razor line-ending advisory). Visual Studio file-error inspection was unavailable; actual compilation/tests provided validation. UI coverage is component rendering/callbacks, not a browser gesture test.
- Local-only import verification used the actual `BillingServiceProcess.odx` plus the `Shs.SpaceTrax2.Interfaces` source root: **19 BizTalk projects, 17 custom projects, 20 candidates**, including **three distinct Billing candidates**. BillingServiceProcess's **574 designer elements** include **6 While loops, 1 CallRules, 25 sends, 63 message assignments and 48 variable assignments**. The report retains the original source hierarchy and all six behavior blocker categories.
- This check analyzed files only: no BizTalk code, business map, script or production endpoint was executed. Its temporary repository test driver was removed; sanitized results remain in session artifacts. The broader SpaceTRAX root analyzed in earlier increments contains more projects than this narrower source root.
- **Completion is still blocked by product implementation and external evidence, not just secrets.** Native message/context/multipart semantics, faithful maps/scripts/pipelines/control flow, exact routing and verified revalidation remain unfinished. Deployed BRE policy/version/vocabularies, actual Billing bindings/subscriptions/environment contracts, expected outputs and BizTalk-team acceptance must be supplied before executable parity can be demonstrated. Do not mark the plan complete or remove migration metadata to make the draft runnable.
- The workspace map index is only an inventory. It does not determine which maps `Billing Processing Policy` chooses; the deployed policy/vocabulary export and BizTalk-owner confirmation are still required.
- See [BillingServiceProcess-Migration-Guide.md](BillingServiceProcess-Migration-Guide.md) for the source-backed dependency/behavior assessment, currently available UI steps, required implementation sequence, acceptance fixtures and cutover/rollback prerequisites.

### Completed increments: schema trees, native map proposals and version-2 validation

- **UI parity:** every native map editor exposes **XSD schema trees and BizTalk map import**. Paste schemas or upload multiple `.xsd` files; inspect source/target namespaces, roots, named scalar paths, cardinality and unsupported shapes. Select scalar fields to add editable links. This works in scratch workflows and disabled imported drafts, not only the import panel.
- **Complete-subset proposals, not partial conversions:** upload/paste a `.btm` with explicit source/target XSDs, then **Create native map proposal**. Root/link paths are resolved against compiled schema expanded names, target assignments follow schema order, and all required target scalar fields must be covered. Direct scalar links and constants are supported; optional sources require optional targets and become explicit Omit mappings. Unsupported constructs/settings, unresolved/ambiguous paths, functoids/scripts, repeated/complex mappings, target choices/optional records/defaults/nil semantics and other unsupported shapes reject the entire proposal with an error. No ignored script or partial result is labeled complete.
- Show editable JSON, generated XSLT and review warnings before the separate **Replace this node's mapping with proposal** action. Analysis/conversion alone never changes the node. Save/reopen retains the source-root assertion and exact XSD texts. Invalid proposals preserve existing mappings/provenance; stale asynchronous results are discarded after input edits.
- **Version 2:** Error retains the old exactly-one scalar requirement; Omit suppresses an absent field; Default supplies an explicit literal only when absent. Present empty values remain empty. Repeated/complex values still fail. These are presence-based conditions, not arbitrary business expressions or record loops. Version-1 definitions keep their original strict semantics.
- **Persisted XSD validation:** proposals embed the supplied schemas and selected source root, including for constant-only maps. The same engine validates source XML before mapping and target XML before returning output in preview and real renders. Manual maps can explicitly attach compiled XSD sets using **Use supplied XSDs for runtime validation**, or explicitly remove that validation. Merely inspecting/uploading schemas does not attach them. Source-root assertions are visible/editable in the node UI.
- XSD processing never fetches external files/URLs. Imports must resolve from supplied documents; includes/redefine are rejected. DTDs and external XML/XSLT resolution remain prohibited. Schema sets allow 1-16 documents and at most 1,000,000 total characters; inspection is bounded to 2,048 nodes, 32 levels and 32 namespace URIs. The existing 262,144-character mapping JSON cap also applies to embedded schemas, so an oversized proposal fails explicitly.
- **Script evidence:** persist up to 128 script details per map, with total/omitted counts, source lines, raw language, owning FID where present, character count, SHA-256 and textual BizTalk API-reference hints. Reports show these as untranslated evidence, never script bodies or executable/safety-approved code. Script-only maps remain blocking even if they have no FID or starter XSLT.

### Verification results: schema/map/script increments

- **542 Service.Tests cases passed, zero failures/skips.** Coverage includes real HTTP/client errors, schema cardinality/namespaces/import restrictions, all-or-nothing proposals, omission/default semantics, runtime input/output datatype/structure validation, constant-only source-root assertions, unsupported nil handling, real render failures, UI proposal application, save/reopen, retained schemas and preserved migration provenance. UI tests use component rendering/callbacks, not browser file-picker/drag gestures.
- Read-only local verification inspected **all seven PartnerMaps `.btm` files** and found **four script bodies**. The trailer constant map produced a native proposal with explicitly identified source/target XSDs. Five other maps were rejected for functoid constructs; the filename-only check could not locate the header target (the subsequent metadata increment below does). Filename matching used by the temporary check is not a production schema resolver or proof of policy association. The temporary test driver was removed; sanitized results are in session artifacts.
- No BizTalk assembly, extracted script, business map or production endpoint was executed. A generated proposal is not a golden-output comparison or an execution approval. Billing remains blocked.

### Completed increment: schema class/source evidence

- Match original map schema class references case-sensitively against explicit project `Schema` Namespace/TypeName metadata, or labeled literal RootNamespace/relative-path conventions. Persist candidate project/file paths, class name, method, project line and file-presence evidence beside each source/target reference. No filename-only fallback, assembly loading, XSD-body loading or automatic map wiring is introduced.
- **Migration report > Maps found in analyzed source** exposes both endpoint candidates and catalog warnings. Outcomes distinguish SourceCandidate, Ambiguous, MissingSource, Unresolved and IncompleteCatalog. Conditional/dynamic/target-generated items, unevaluated item defaults, unsupported names, read failures and truncation cannot silently establish uniqueness. Inspection is capped at 4,096 declarations, 32 saved candidates per reference and 32 shared warning details; counts/omissions remain visible. Project/map file reads are bounded before parsing, including files that grow after opening.
- Fresh local analysis found **nine workspace maps**, including the seven PartnerMaps candidates. The header target `BTAHL7Schemas.MSH_25_GLO_DEF` has an explicit Namespace/TypeName match in `Shs.SpaceTrax2.InterFaces.Common.HL7Schemas\2.X\MSH_25_GLO_DEF.xsd`. This resolves the earlier filename-only lookup failure at the source-metadata level, not deployed identity or script translation.
- The trailer's `TestSchema.xsd` and `TstTrailerSchema.xsd` exist on disk, but the checked-in Custom `Schemas.btproj` declares only two PropertySchemas entries and no PartnerSchemas entries. Their class ownership therefore remains Unresolved. Explicitly supplied XSDs can still generate a review proposal; file presence does not prove a built/deployed type. No external source project was edited.
- **563 Service.Tests cases passed, zero failures/skips.** Added coverage includes metadata overrides, conventions, exact-case matching, ambiguity/caps, missing and orphan files, conditional/dynamic/default/target branches, truncation, report/UI round trips, explicit schema attachment/removal, bounded reads and the exact 32-level tree threshold. A separate local-only ownership check passed; its temporary driver was removed. Source reports are import-time snapshots: reanalyze to inspect fresh evidence; saving an old draft does not recompute it.
- Full solution build succeeded. An explicit test-project rebuild with **CS8785 treated as an error** succeeded with **zero warnings/errors** (only the installed preview SDK notice). `git diff --check` found no whitespace errors; the existing Designer.razor LF/CRLF advisory remains. Visual Studio file-error inspection was unavailable, so actual builds/tests supplied validation. No dependency installs or external source edits were needed.

### Known limits / next increments

1. Build verified translation evidence and explicit revalidation tied to the saved definition before introducing any approval path. There is deliberately no manual acceptance override in this increment. Exact artifact ownership and confidence evidence remain pending.
2. The gate applies to flows carrying migration metadata. Historical imports without it, manually copied definitions submitted as new scratch flows, or direct workspace/backup edits removing provenance cannot be identified automatically; reanalyze/re-import historical drafts. This is a workflow safety policy, not an authorization boundary against workspace editors.
3. Extend literal schema-source evidence to evaluated build/deployed identities and resolve missing project declarations, then wire only proven map/orchestration relationships. Explicit Namespace/TypeName evidence, supplied-schema trees and conservative scalar proposals now exist; automatic authoritative ownership/wiring does not. Add explicit record repetition/cardinality, arbitrary conditions and supported functions; compare all selected imported outputs against trusted golden samples.
4. Extend discovery/resolution to projects outside the supplied scopes, C# project dependency chains, MSBuild property/configuration evaluation, shared/deployment output directories and persisted exact reference-path assertions. HintPath ownership remains a labeled inference, not proof of assembly equivalence.
5. Resolve map/schema/pipeline ownership per orchestration and exact binding/port relationships. Path-aware dependency inventory does not eliminate existing project-wide artifact heuristics.
6. Add message/context abstractions and approved script translation/runtime; metadata hashes and textual hints do not establish portability. Extend fail-explicit scaffolding to remaining custom/script/pipeline behavior, then implement pipeline/binding/orchestration translation in the planned order.
7. Complete a small local synthetic-input/golden-output SpaceTRAX pilot before claiming functional migration fidelity. Product work in items 1 and 3-6 can still progress without team input; deployed policy/binding/reference evidence and owner-approved golden outputs cannot be invented.

Record each subsequent increment here with its scope, UI/manual-workflow entry points, verification results, and remaining gaps. Do not mark an entire workstream complete based only on scaffolding or a successful build.
