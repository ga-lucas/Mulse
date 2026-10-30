# BillingServiceProcess migration guide

**Assessment date:** 2026-10-30  
**Scope:** local source only; no BizTalk instance, deployed rule store, production bindings, credentials, or external services were queried. No source was edited for this assessment. The broader BizTalk compatibility plan remains in progress; this guide does not claim that plan is complete.

## Decision

- **Importable as a disabled analysis/draft:** Yes. The current importer accepts an `.odx` path when exactly one sibling `.btproj` explicitly includes that file. Parent-reported completed verification (not rerun in this documentation task): 314 related tests pass, the full solution build succeeds, and actual Billing analysis assertions pass. The local analysis reported 19 BizTalk projects, 17 custom projects, 20 candidates, all three Billing orchestration candidates, and 574 designer elements for `BillingServiceProcess.odx`.
- **Importable as a complete, executable Mulse workflow:** **No.** The importer records evidence and creates a review draft; it does not translate this orchestration’s full behavior. Required BRE, dynamic-map, message/context, routing, pipeline, batch, correlation, response, transaction, and error semantics remain unimplemented or unresolved.
- **Execution is intentionally fail-closed:** A flow with a BizTalk migration report cannot be enabled or run, regardless of report status or edited configuration. There is no approval switch or supported bypass. Keep the imported flow disabled.

The report’s shape table gives each designer element a 1-based report ID derived from designer document order, parent ID, source line, shape type/name, and selected references. It is an inventory—not an executable sequence. The analyzer does not execute ODX/C# expressions. If analysis fails, it records a blocking `BT-ODX-NOT-ANALYZED` diagnostic rather than treating missing analysis as evidence of no behavior.

## Local source and dependency findings

### Project and orchestration files

`Core.Process.Billing\Shs.SpaceTrax2.Interfaces.Core.Process.Billing.sln` contains the single BizTalk project `Core.Process.Billing\Shs.SpaceTrax2.Interfaces.Core.Process.Billing.btproj`. It targets .NET Framework 4.6.1 and imports the BizTalk 2010 MSBuild targets. The project includes these three orchestration files and no map, schema, or pipeline items of its own:

- `BillingServiceProcess.odx`
- `BillingBatchDoubleBodyProcess.odx`
- `BillingBatchWithTrailerProcess.odx`

The main service has 574 designer elements: 31 Decisions, 75 DecisionBranches, 63 Constructs, 63 MessageAssignments, 48 VariableAssignments, 25 Sends, 1 Receive, 6 Whiles, 7 Scopes, 1 Catch, 1 CallRules, 1 Delay, 1 AtomicTransaction, 14 CorrelationDeclarations, 5 CorrelationTypes, and 18 PortDeclarations. These counts describe the source, not a translation result.

### Exact local source projects for four unresolved assembly references

The Billing `.btproj` references these assembly names through `..\Build\<assembly>.dll` HintPaths. Matching source project files exist, but their declared output directories are not `Build`. Literal output-path inspection therefore does **not** justify automatically assigning ownership by assembly name:

| Referenced assembly | Matching local source project | Declared `AssemblyName` | Literal `OutputPath` declarations |
|---|---|---|---|
| `Shs.SpaceTrax2.Interfaces.Common.FilenameMaker` | `Shs.SpaceTrax2.Interfaces.Common.FilenameMaker\Shs.SpaceTrax2.Interfaces.Common.FilenameMaker.csproj` | `Shs.SpaceTrax2.Interfaces.Common.FilenameMaker` | `bin\Debug\`; `bin\Release\` |
| `Shs.SpaceTrax2.Interfaces.Common.GlobalInterfaceAppSettings` | `GlobalInterfaceAppSettings\Shs.SpaceTrax2.Interfaces.Common.GlobalInterfaceAppSettings.csproj` | `Shs.SpaceTrax2.Interfaces.Common.GlobalInterfaceAppSettings` | `bin\Debug\`; `bin\Release\` |
| `Shs.SpaceTrax2.Interfaces.Core.Pipelines` | `Shs.SpaceTrax2.InterFaces.Core.Pipelines\Shs.SpaceTrax2.Interfaces.Core.Pipelines.btproj` | `Shs.SpaceTrax2.Interfaces.Core.Pipelines` | `bin\Development\`; `bin\Deployment\` |
| `Shs.SpaceTrax2.Interfaces.Core.Schemas` | `Shs.SpaceTrax2.Interfaces.Core.Schemas\Shs.SpaceTrax2.Interfaces.Core.Schemas.btproj` | `Shs.SpaceTrax2.Interfaces.Core.Schemas` | `bin\Development\`; `bin\Deployment\` |

The parent’s analysis found all four references still **Unresolved** when the wider source root was included. The outputs do not match the HintPaths, and no explicit reference-ownership override exists today. Do not select a project merely because its assembly name looks right. The project also references BizTalk 2010 assemblies, Microsoft HL7 components, and nSoftware FTP/SFTP adapters; these are runtime/build dependencies, not automatically portable Mulse modules.

The schema source project includes relevant local files such as `Services.Billing\CommonBilling.xsd`, `Services.Billing\BillingMessageIndexer.xsd`, `IntegrationRules.xsd`, `ServiceResult.xsd`, `BaseStructures.xsd`, and `Properties\BillingProperties.xsd`. The pipeline project includes source definitions such as `SendBTAHL7.btp`, `SendBTAHL7Email.btp`, `SendXmlEmail.btp`, `SendFlatFileEmail.btp`, and `SendFlatFile.btp`. They must be explicitly resolved and behavior-reviewed before being relied on.

### Business Rules, maps, and bindings

`BillingServiceProcess.odx` contains a `CallRules` shape named **Retrieve Map**, with policy name `Billing Processing Policy` and policy version `-1`. No `.rules` policy export was found in the local `Shs.SpaceTrax2.Interfaces` source tree. The metadata identifies a call; it does not include the deployed policy implementation or establish what `-1` resolves to in the deployed BRE environment. The BizTalk team must supply the approved policy/vocabulary export and explain version-selection behavior.

Message assignments dynamically obtain map types using `System.Type.GetType` and invoke BizTalk `transform` operations. The selection fields include `msgBre.HeaderMapName`, `msgBre.PreprocessingMapName`, `msgBre.MapName`, and `msgBre.TrailerMapName`; the main orchestration contains 29 dynamic transform call sites. Billing’s `.btproj` does not include `.btm` files, and no policy was available to identify the complete set of map names. The migration report now lists `.btm` files found across the analyzed source scope, including maps owned by separate projects, with schema references and analyzer counts. This helps the team inspect available candidates, but does not establish that the deployed policy selects any listed map; nearby maps are not safe substitutes. The existing map analyzer can produce limited starter XSLT for some direct links/constants, but does not translate script/functoid behavior or wire a complete map into this flow.

Fresh source analysis indexes nine workspace maps, including the seven PartnerMaps files. Each source/target reference now shows schema class/file candidates based on explicit Namespace/TypeName metadata or labeled literal naming conventions, with case-sensitive matching and explicit missing/ambiguous/incomplete states. This is source evidence, not evaluated build/deployed identity or policy selection.

Two concrete findings help avoid false ownership assumptions:

- The header target `BTAHL7Schemas.MSH_25_GLO_DEF` is explicitly declared by `Shs.SpaceTrax2.InterFaces.Common.HL7Schemas\Shs.SpaceTrax2.Interfaces.Common.HL7Schemas.btproj`, at `2.X\MSH_25_GLO_DEF.xsd`. Its project Namespace/TypeName metadata explains why filename-derived class lookup was insufficient.
- `Shs.SpaceTrax2.Interfaces.Core.Custom\Schemas\PartnerSchemas\TestSchema.xsd` and `TstTrailerSchema.xsd` exist, but that Custom `Schemas.btproj` includes only two PropertySchemas entries and no PartnerSchemas entries. The trailer's source/target type ownership therefore remains unresolved. Confirm the intended build/source history before claiming these disk files are the deployed schema classes; Mulse does not silently add them to the BizTalk project.

No Billing-specific binding export is in the Billing project folder. The wider local source root contains two binding-info filenames for PatientRegistration, not Billing; their contents were not opened. A deployed Billing binding export is still needed for authoritative endpoint, subscription, transport, retry, and environment configuration. Do not paste unredacted secrets into tickets or this guide.

### Helpers and pipeline behavior

The ODX calls `FilenameMaker.MakeFilename` from `Shs.SpaceTrax2.Interfaces.Common.FilenameMaker\FilenameMaker.cs` and `GlobalInterfaceAppSettings.GetFTPPattern` / `GetSMTPPattern` from `GlobalInterfaceAppSettings\Shs.SpaceTrax2.Interfaces.Common.GlobalInterfaceAppSettings.cs`. The send-pipeline source also references the local `Shs.SpaceTrax2.InterFaces.Core.CPC.HL7TypeCaster\HL7TypeCaster.cs` and `Shs.SpaceTrax2.Interfaces.Core.CPC.PS.SetProp\SetProperty.cs`. These source files are concrete dependencies, but the importer inventories custom assemblies/components rather than translating their behavior. Rewrite and test their behavior as native modules; do not assume the .NET Framework assembly can simply be loaded into the Mulse flow. No app-config values or credential values were inspected.

The orchestration references Billing pipeline names from the local Core.Pipelines project. For example, `SendBTAHL7.btp` contains the local `HL7TypeCaster` component followed by the Microsoft BTAHL7 2.X Assembler; the email variant adds MIME/SMIME encoding. XML-email pipelines include PropertySetter stages and MIME/SMIME encoding; flat-file routes use the Flat File Assembler, and email variants also add MIME/SMIME encoding. These stages can affect serialization, context, HL7 framing, and delivery. The import report explicitly classifies pipeline behavior as untranslated; it does not reproduce those component effects.

### Ports, message context, batching, and failures

The main orchestration has one request Receive (`Submit`) on `prtBillingService` and a response Send (`msgServiceResult`) on that same service port. It also has 25 Sends across multiple delivery choices, including the direct `prtBillingBatchDirect` handoff and HL7 direct/message-box routes. Its metadata declares 18 ports: 6 physical binding attributes, 9 logical binding attributes, and 3 direct binding attributes. The physical attributes are marked dynamic and expose transport/pipeline/URI fields; their URI values are intentionally not reproduced here. The ODX also writes transport and HL7 context properties (including credential-related properties from message configuration). Preserve these as protected configuration and message metadata, not literals in code or tests.

The two batch orchestrations are separate BizTalk orchestrations, not nested calls found in the main ODX. `BillingServiceProcess` sends a message to the direct `prtBillingBatchDirect` port; the batch ODXs receive first/next requisitions on their own direct ports. `BillingBatchDoubleBodyProcess` and `BillingBatchWithTrailerProcess` each have a `Batch Loop`, two Receive shapes, a BRE call, and route-specific Sends; the trailer variant has an additional trailer assignment scope and a second atomic transaction. The main service has six named debatching/processing loops and corresponding scopes. The importer produces separate candidates, but does not recreate the subscription handoff, batch membership, order, trailer/body rules, or per-item failure/retry behavior. Design and test the inter-flow handoff explicitly; do not silently drop either batch variant or assume a direct MessageBox subscription is an ordinary endpoint.

The main process declares correlation sets/types, including batch/service-result/HL7 correlations; declaration counts alone do not prove which correlation is initialized, promoted, or active at each send/receive. It also contains a Delay, Catch, nested Scopes, and an AtomicTransaction. The importer does not preserve wait timing, restart/replay semantics, atomic boundaries, compensation, exceptions, or the exact request/reply failure contract. The simple decision analyzer may translate a narrow set of branch conditions into decision tags, but it does not perform each branch’s side effects.

## What can be done now in Mulse

1. Open **Designer → Import from BizTalk**.
2. Set **Source path** to the local `BillingServiceProcess.odx`. The file must be explicitly included by exactly one sibling `.btproj`; this file is. If ownership is ambiguous or missing, select the owning `.btproj` or solution instead.
3. Set **Additional solutions/projects** to the local `Shs.SpaceTrax2.Interfaces` source root so the importer can inventory sibling schema, pipeline, and helper projects. This makes source available for analysis, but does not resolve the four `Build`-path references described above.
4. If the BizTalk team provides an approved Billing binding export, supply it under **Additional binding export paths**. Do not substitute the unrelated PatientRegistration exports.
5. Click **Analyze source**. Review the dependency report, all three Billing candidates, each blocking diagnostic, the shape inventory/source-line references, the workspace map inventory, and the warnings. Confirm whether sibling project references are resolved or unresolved; inspect each map's owning project and schema references, then ask the BizTalk team to confirm which maps the deployed policy actually selects. Do not treat candidates as runnable workflows.
6. Use **Load onto canvas** only to inspect/edit the disabled draft. Downloaded orchestration modules are explicitly labeled **unimplemented migration stubs** and throw `NotSupportedException`; they are not pass-through implementations, do not implement Billing logic, and do not lift the execution gate. A successful build or test confirms the scaffold shape only, not translated Billing behavior.
7. Save only as a disabled review artifact. Do not edit a `FullyTranslated` status, remove the report, manually enable the flow, or route production messages around the fail-closed guard.

### Native map work available without the BizTalk team

The Mapping Designer now includes XSD schema trees, conservative `.btm` proposals, explicit missing-value policies and persisted runtime XSD validation. These tools are available for both scratch-built workflows and disabled imported drafts. They do not resolve the missing deployed policy or make Billing runnable.

1. In **Flow Designer > Mapping Designer**, add/select an **XML mapping render** node. For a new render route, add a deliver node before saving; do not connect it to a production destination.
2. Open **XSD schema trees and BizTalk map import**. Paste/upload the exact source and target `.xsd` files, including any explicitly supplied import dependencies. **Inspect source schema tree / Inspect target schema tree** shows expanded-name paths, namespaces, cardinality and unsupported shapes. Select scalar fields to add editable links; no arbitrary XPath is required.
3. For the locally verified trailer candidate, select `Maps\PartnerMaps\TestSchema_ADtA31Trailer.btm`, source `Schemas\PartnerSchemas\TestSchema.xsd`, and target `Schemas\PartnerSchemas\TstTrailerSchema.xsd`, relative to `Shs.SpaceTrax2.Interfaces.Core.Custom`. Choose **Create native map proposal**. A local read-only check generated this proposal; that does not prove Billing selects it or that its output is equivalent.
4. Inspect the proposed JSON/XSLT and warnings. Only **Replace this node's mapping with proposal** installs it. Unsupported maps fail as a whole and preserve the existing node, rather than silently dropping functoids or scripts. The source root and both exact XSD texts are retained in a version-2 definition.
5. For manual maps, select **Use supplied XSDs for runtime validation** explicitly to attach schemas. Simply uploading/inspecting does not attach them. Input and output validation run in preview and actual rendering; wrong namespaces, missing required data and invalid datatypes fail before output is returned. External schema files/URLs are never fetched; supply import dependencies as text/files, and do not expect XSD includes/redefine support.
6. Use **Error / Omit / Default** for missing scalar source values. Omit suppresses only that assigned field, not arbitrary parent records; Default inserts a literal only for absence. A present empty value stays empty. Repetition, complex values and arbitrary conditions/functions still require explicit translation.
7. Preview only schema-valid synthetic input and compare against trusted expected output. Save/reopen retains the node, policies, source-root assertion and validation schemas. All report-bearing imported flows remain disabled; preview/proposal success is not execution approval.

The seven local PartnerMaps files were checked without executing source or scripts: the trailer generated a native proposal from explicitly supplied schemas; five other maps were rejected for functoid constructs; the header contains two scripts and now has a declared target-schema file candidate as described above. The preprocessing maps contain one script each, for **four script bodies in total**. Migration reports show bounded source-line/language/FID/size/hash/API-hint evidence, never script bodies or a portability guarantee. These findings do not establish deployed policy selection. The old best-effort XSLT scaffold remains separate and is not automatically substituted into Billing.

In a fresh **Analyze source** result, expand **Maps found in analyzed source** and inspect source/target schema evidence and catalog warnings. Do not replace unresolved classes with nearby filenames. Previously saved reports are unchanged import-time snapshots; saving an existing draft does not recompute them.

**Latest portable verification:** 563 Service.Tests cases passed with zero failures/skips, including mapping UI persistence, HTTP APIs, actual render validation, bounded schema-source evidence and preserved migration gates. This is synthetic/component-level verification, not Billing business-output or production integration acceptance.

## Work required before a complete migration

### Prioritized developer work

1. **Resolve dependency ownership safely.** Add a user-selectable, persisted exact project-path assertion for unresolved references (or align build outputs and HintPaths with verifiable literal paths). Keep ambiguous/missing paths blocking; do not fall back to assembly-name guessing. This is a recommended improvement, not an available feature.
2. **Obtain and translate the actual BRE policy and maps.** Pin the relevant policy/version, input/output vocabulary, all map class names selected by the policy, and their source `.btm`/XSLT. Implement decision/map selection as native code and translate every required map/function/functoid, including headers, preprocessing, body, trailer, and multi-input transforms.
3. **Implement native message and context semantics.** Translate message/variable assignments and dynamic transforms; define the payload model, metadata/property promotion, safe configuration lookup, filename generation, and error representation. BizTalk XLANG `transform`, message context, ports, and runtime types are BizTalk APIs, not portable C# scripts. Only pure helper logic should be reused after review; BizTalk-bound logic needs an explicit native equivalent.
4. **Translate pipeline and transport behavior.** Recreate and validate the HL7 cast/assembly, flat-file assembly, property setters, MIME/SMIME handling, transport-specific behavior, subscriptions, retries, and secure settings using native Mulse modules.
5. **Model all three orchestrations together.** Explicitly map the direct MessageBox handoff into a reviewed Mulse flow-to-flow or equivalent internal route, preserving ordering, correlation, batch/body/trailer behavior and request/reply response completion. Preserve the two batch variants as distinct required behaviors unless the process owner approves a documented replacement.
6. **Keep the fail-closed readiness policy.** A verified activation path must be based on implemented, tested behavior and trusted evidence, not an editable report status or a manual bypass.

### Required BizTalk-team inputs

- Exported Billing bindings for the relevant environments, with a secure source-of-truth for credentials, certificates, host keys, addresses, retry policies, filters, and subscriptions. Keep secret values out of the guide and synthetic fixtures.
- Deployed `Billing Processing Policy`/vocabulary, version-resolution meaning for `-1`, and approved rule outcomes for each relevant transport/map selection.
- Complete map inventory and map implementation/source for every dynamic selector field, plus its source/target schemas and known message samples.
- Exact reference ownership/build provenance for the four unresolved assemblies, and the deployed versions of BizTalk, HL7, nSoftware adapters, and pipeline components.
- Message property/correlation contract: initialized and followed correlations, service-result behavior, required HL7 context, batching keys, ordering, timeout/delay rules, duplicate/idempotency rules, and restart/replay expectations.
- Expected behavior for every send/receive route, including the synchronous `Submit` response, negative responses, exception/compensation behavior, and both batch modes.
- Sanitized synthetic fixtures and trusted golden outputs for each supported scenario, reviewed by the application and BizTalk owners.

## Synthetic golden validation matrix

All tests must use synthetic payloads and mocked/dedicated non-production destinations. Compare not only payload bytes, but also route choice, metadata/context, ordering, response status, error behavior, and side effects. Keep credentials and real endpoints out of fixtures.

| Area | Minimum cases | Required assertions |
|---|---|---|
| BRE and transforms | Every policy outcome; each preprocessing/header/body/trailer map selection; missing/unknown map | Correct selected native transform, output structure, schema validity, and deterministic failure for missing configuration |
| Message formats and routes | Representative XML, HL7 and flat-file messages; each single-message send route | Correct serialized output, HL7 delimiters/context, file naming, email metadata, and route selection |
| Dynamic context/transport | HTTP, FTP, SFTP, SMTP, and direct internal routes in a non-production harness | Correct endpoint/config key selection and protected secret handling; no live side effects |
| Batch variants | Zero, one, and many items; both double-body and trailer forms; ordering and boundary cases | Exact batch count, body/trailer placement, output ordering, and per-item/error semantics |
| Correlation/waits | Interleaved keys, competing arrivals, delay/timeout, restart and replay | No cross-correlation, lost/duplicated messages, or changed timeout behavior |
| Request/reply and failures | Successful `Submit`, business rejection, malformed message, downstream failure, retry and compensation | Correct response payload/status, no silent success, and documented idempotent retry/rollback outcome |
| Transaction boundaries | Fail each delivery/assignment stage inside and outside each atomic scope | Expected commit/rollback/compensation and no partial side effects beyond the agreed contract |
| Readiness gate | Imported draft with any report status; disabled save; attempted enable/run | Every imported report-bearing flow remains blocked until a verified implementation path exists |

A developer-generated “expected” result is not a golden result until the BizTalk/process owner approves it. A passing subset is a pilot only; list every excluded path and do not claim complete migration while any required behavior is excluded.

## Cutover and rollback (approval required; not executable today)

There is no production cutover procedure in this assessment. It requires the application owner, BizTalk operations owner, security/configuration owner, and change authority to approve the exact environment-specific runbook. Before approval: run the full synthetic matrix, then a non-production shadow/parallel pilot with fenced destinations and duplicate detection. Do not point both systems at live destinations without an explicit deduplication and ownership plan.

An approved cutover should define intake ownership, message draining, in-flight instance handling, endpoint/subscription changes, monitoring, stop conditions, and named decision-makers. Preserve the BizTalk deployment and its configuration for the rollback window. Rollback must stop new Mulse intake, restore the approved BizTalk route, and reconcile messages already accepted or delivered by either side using the agreed idempotency ledger. Never use the fail-closed migration gate as a cutover switch or bypass it to test production traffic.

## Completion criteria

Do not call this migration complete until all of the following are true:

1. Every required source/reference is resolved to the correct local/deployed artifact; the BRE policy/version, dynamic maps, bindings, schemas, helpers, and pipeline component versions are identified.
2. Every designer shape and executable assignment/branch/port is accounted for, including inactive/alternate paths; no required operation is silently omitted.
3. Native implementation covers all three Billing orchestrations and preserves dynamic map selection, context, direct handoff, batch variants, correlation, request/reply, transaction, and error behavior—or an explicitly approved replacement contract exists.
4. The complete owner-approved synthetic golden matrix passes, with failure/replay/concurrency tests and reproducible evidence.
5. Non-production operational, security, retry, monitoring, throughput, and rollback reviews pass; secret configuration stays outside source and fixtures.
6. The Mulse fail-closed policy is only relaxed through a reviewed implementation/verification design—not by editing report metadata—and all required change approvals are recorded.

Until these criteria are complete, the supported outcome is **local analysis plus a disabled migration draft**, not a complete or executable Billing migration.
