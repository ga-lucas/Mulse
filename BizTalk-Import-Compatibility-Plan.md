# BizTalk Import Compatibility Plan

## Goal

Build a reusable BizTalk importer that automatically preserves as much behavior and workflow as possible in native Mulse flows, without hosting BizTalk code or runtime. Billing is a worked example, not the product scope; only truly Billing-specific code (the CommonBilling field read) lives in `Mulse.Billing`, and reusable legacy-compat modules live in the built-in Compatibility Pack (or `Mulse.Hl7` for HL7). Make all flow and module settings explicitly editable in the UI, including configuration for unsupported or unknown behavior. Imported and scratch-built flows should share the same tools.

```text
BizTalk project -> analyzed artifacts -> canonical Mulse model
				-> Flow Designer + Mapping Designer -> native Mulse workflow
```

Latest verification: 1,635 service tests passed and the solution built.

Report-bearing imports remain disabled and fail closed until verified translation support exists. Configuration review and report statuses are not approvals. Older imports without migration metadata require reanalysis/re-import to receive the gate.

## Current continuation: isolated testing and Billing migration

- **Partial:** the native control-flow editor now offers a one-shot isolated test with synthetic text input, editable input and mock-output metadata, explicit mock outputs and captured deliveries/replies. Native decisions and message operations use the existing executor; no real source, destination, module catalog or workspace checkpoint is accessed. Unresolved nodes still block, and missing mocks fail explicitly. Each kind/module has one response, with additional dynamic-module mocks available.
- **Timer waits:** an opt-in "advance waits" setting resumes durable timer Waits on a virtual clock (max 32 resumes, no real delay) with the same checkpoint semantics and single-delivery guarantee; Receive nodes still end the test as Suspended because they need a real arrival.
- **String/conversion functions (native, UI-editable):** `Length`, ordinal `IndexOf`/`LastIndexOf`, `Substring`, `Substring(start,length)`, `Regex.IsMatch` (unanchored, no options, 2 s limit), `ToInt32` and `ToText` are expression kinds in the runtime and in the expression editor (labeled operands and hints). The importer translates `variable`/distinguished-field `.Length/.IndexOf("literal")/.LastIndexOf("literal")/.Substring(...)`, static `Regex.IsMatch`, and `Convert.ToInt32/ToString` only when operand types are statically proven; `Convert.ToInt32(xpath(string))`, non-ASCII/empty/non-literal search text, overloads and chained calls stay Blocked. Known deviation, warned at import: the legacy default string search is culture-sensitive, native is ordinal.
- **`if`/`else` blocks (native, UI-editable):** `if (cond) { ... } else if (...) { ... } else { ... }` inside expression shapes becomes a native `If` with an `Otherwise` branch (max depth 8, 64 statements per block). It cannot interrupt an incomplete XML document construction.
- **Native `Try` node (durable flows only, UI-editable):** body plus catch handler, with explicit message/details exception variables (String, truncated to 2,000 characters). It catches body faults only. It does not catch `Terminate`, failed Deliver/Augment/Render effects (ambiguous, need reconciliation), persistence faults, cancellation, or faults that cross a ForEach/Receive/Wait. The details variable holds `TypeName: message`, not a CLR stack trace.
Typed or multiple catches, fault-message catches and assignments to `ex.*` remain Blocked. A `CorrelationDeclaration` that is only initialized by Sends and never followed becomes named "(not enforced)" evidence (see the correlation increment); followed or unproven sets stay visible blockers.
- **Editor fix:** the native flow editor validated expressions against a stale hard-coded kind list and would have refused flows using the string functions; it now shares the expression editor's kind/arity list. A truncated `AppendChild` consumption in the XML-root reader was also restored.
- **Measured Billing effect (re-measured with a stand-in config helper; indicative only):** unresolved nodes BillingServiceProcess 136 -> 126, BatchDoubleBody 54 -> 46, BatchWithTrailer 60 -> 48. The original `GlobalInterfaceAppSettings` source was not found on disk, so helper-derived values differ from a real run. Flows stay disabled; no BizTalk equivalence is claimed.
- **Next measured gaps (Billing):** dynamic transforms needing map choices (28; the map type names come from rule-engine output at run time, appear in no supplied artifact and must be entered as **Native map choices** in the import panel), `//Setting[@Name='X']/@Value` descendant/predicate XPath reads (10; they block every `Create HL7 Message` assignment and need a bounded descendant + attribute-equals fact in the model, executor and UI), part-level `MIME.FileName` (1), followed correlation sets with `StatementRef`/DNFPredicate/Receive filters/`Listen` (batch flows), transmit-pipeline wrappers around translated Sends (the translated delivery is kept as a child; the pipeline component order must still be configured by hand), 9 Sends without a unique physical binding, delivery modules reading the recorded adapter-property metadata (recorded only, not wired), DateTime values and `Subtract(DateTime.Now)` delays, dynamic SFTP/other transports without a native adapter, and Distinguished scalars beyond string/int/boolean.
- **Verification:** 1,550 service tests passed and the solution built.

### Increment: `InvokeTransform` (UDF bridge) and `FilenameMaker`

- **Custom C# transforms are supported.** An `ITransformModule` is a hot-loadable, pure value function (`Task<JsonNode?> InvokeAsync(context, inputs, step, ct)`, registered with `AddTransform<T>()`, kind `Transform`). Before this increment only other modules could call one (`IModuleInvoker.InvokeTransformAsync`); a native flow could not.
- **New native node `InvokeTransform` (durable flows only, UI-editable):** module picker (Transform modules only), declared String result variable, 1-8 argument expressions (String/Int32/Boolean/null), module settings. The result must be a String of at most 65,536 characters. It is pure and **not journaled**, so it re-runs after a crash before the next checkpoint; faults are ordinary exceptions that `Try` can catch. A missing package fails closed (dependency discovery reports the transform; the isolated test runner offers a "Transform" mock kind).
- **Importer:** `x = Ns.FilenameMaker.MakeFilename(arg);` with a declared String target and one proven String argument becomes `InvokeTransform` on `filename-template-transform`. Settings `timeZone=UTC` and `culture=Invariant` are **assumptions** (the deployed host's zone and culture are unknown); they are visible, editable and warned about at import. Wrong target type, argument count or argument type stays Blocked. The mapping table (`KnownUdfs`) is explicit; no other helper is translated implicitly.
- **Verification:** 18 new tests (core 9, importer 8, UI 1); 1,568 service tests passed and the solution built. Dynamic port `Address`/`TransportType` assignments that use `MakeFilename` remain Blocked.

### Increment: generic modules moved out of `Mulse.Billing`

Review rule: a module with no hard-coded customer schema is generic. No aliases were added; saved flows that use an old ID fail closed until re-selected.

| Old ID | New ID | Home |
| --- | --- | --- |
| `billing-filename-transform` | `filename-template-transform` | Compatibility Pack (built-in) |
| `billing-filename-augment` | `filename-template-augment` | Compatibility Pack |
| `billing-set-property-augment` | `set-context-property-augment` | Compatibility Pack |
| `billing-remove-namespaces-render` | `xml-remove-namespaces-render` | Compatibility Pack |
| `billing-hl7-message-type-augment` | `hl7-message-type-augment` | `Mulse.Hl7` (hot-loadable; enable it, as for `hl7-xml-render`) |
| `billing-field-augment` | unchanged | `Mulse.Billing` (hard-codes the CommonBilling namespace and field list) |

- Importer IDs, warnings and report hints were updated. `[GUID]` is random per call and `InvokeTransform` is not journaled: checkpoint the result before a Deliver that must be replay-stable (future option: derive the GUID from instance and node IDs).
- **Verification:** 1,570 service tests passed and the solution built.

### Increment: dynamic send ports

- **Core (UI-editable):** a `Deliver` node can carry `SettingExpressions` (1-32 keys, no overlap with static `Settings`, case-insensitive). Each expression is evaluated at run time and overlaid on the static settings (String as is, Int32 as invariant text, Boolean as `true`/`false`; anything else fails and a `Try` can catch it). The node editor has a "Dynamic settings" list; the isolated test runner records the effective settings and redacts secret-like keys (`password`, `secret`, `token`, `key`, `credential`, `passphrase`) as `***`. The native node limit was raised from 1000 to 2000 (runtime, editor, docs) because dynamic dispatch expands each send.
- **New UDF `address-part-transform` (Compatibility Pack):** pure String function. `format` Url (`scheme://host[:port]/dir/file`; user-info rejected; `defaultPort` optional), Path (split on the last `\` or `/`) or Mail (optional `mailto:` removed); `part` Host/Port/Directory/FileName/MailAddress. Invalid input throws (fail closed).
- **Importer:** a `PortDeclaration` whose `PhysicalBindingAttribute` has `IsDynamic=True` (unique name, bound to a send port with `IsStatic="false"`) accepts `port(Microsoft.XLANGs.BaseTypes.Address|TransportType) = <String expression>;`. Values go to hidden String variables `<port>_Address` / `<port>_TransportType` (declared at the top of the flow, visible and editable), so assignment-time semantics are kept; `MakeFilename(...)` uses the `InvokeTransform` bridge. The Send becomes an exact, case-sensitive `If` chain on the transport type for FILE, FTP, HTTP and SMTP (limited to the binding's `DynamicSendHanders` when listed), each branch parsing the address with `address-part-transform` and delivering through the native module with computed settings. Any other, unassigned or differently cased value reaches an explicit `Terminate`; a port that allows only unsupported transports (for example SFTP, AzureBlobStorage) stays Blocked, and unsupported allowed transports are warned about. Two-way dynamic ports and unknown port properties stay Blocked.
- **Reviewed defaults (assumptions, warned at import):** FTP `encryptionMode=Auto`, passive, no overwrite, credentials as Secret requirements; FILE `namingMode=Fixed`, no overwrite; SMTP host/from as configuration requirements, `port=25`, a generated subject; a computed FTP remote file name is still template-expanded by `ftp-deliver`. Adapter-property assignments on the outbound message (`FTP.*`, `SMTP.*`) are **not** translated and remain visible blockers.
- **Static `nsoftware.FTP|SFTP [yyyy]` transports** (exact name match) now import as native `ftp-deliver`/`sftp-deliver` nodes with the existing static mapping and warnings.
- **Measured Billing effect (indicative; stand-in config helper):** unresolved nodes BillingServiceProcess 124 -> 116 (1,011 nodes now), BatchDoubleBody 46 -> 43, BatchWithTrailer 47 -> 43. The remaining Send blockers are transmit-pipeline wrappers and ports without a unique binding. Flows stay disabled; no BizTalk equivalence is claimed.
- **Verification:** 47 new tests (core 4, runner 1, UI 1, UDF 27, importer and end-to-end execution 14); 1,617 service tests passed and the solution built.

### Increment: initialize-only correlation sets

- **Rule:** a `CorrelationDeclaration` is imported as a named "(not enforced)" no-op, with its `StatementRef` children, only when every statement reference initializes the set from a `Send` of the same orchestration, the set is never followed, `ParamDirection` is In, and exactly one `CorrelationType` in the source matches its type. A set referenced by a Receive, followed, initialized by a non-Send, referencing an unknown shape, or with an unresolved type stays a Blocked node ("needs explicit native channel and correlation-key bindings").
- **Assumption (warned at import):** BizTalk's requirement that the sent message carries the set's promoted properties is not enforced, and no later Receive is subscribed. Review any consumer that relied on these sets.
- **Measured effect (indicative):** BillingServiceProcess unresolved nodes 116 -> 88 (all 14 sets and 14 references; its sets are all Send-initialized and never followed). The two batch flows are unchanged at 43: their set is initialized by one Receive and followed by another inside a `Listen`, so it stays blocked.
- **Verification:** 6 new importer tests; 1,623 service tests passed.

### Increment: platform context properties and HL7 multipart construction

- **Platform properties:** an exact allowlist of adapter/assembler symbols (`BTAHL7Schemas.*`, `FTP.*`, `FILE.Username|Password`, `MIME.FileName`, `SMTP.*`, `nsoftware.BizTalk.FTP|SFTP.*`) may be **written** with `msg(Symbol) = value`. Each write becomes a typed `SetMessageValue` (String, Int32, Boolean) on the importer-defined key `{urn:mulse:biztalk-platform-property:<clrNamespace>}<name>`. Reads, null values, unknown types, other namespaces (`EDI.*`, `HTTP.*`, `WCF.*`) and non-empty literals written to password properties stay blocked; secrets must come from a message field.
- **Assumption (warned at import):** the properties are recorded as message metadata only. No delivery module reads them, so the delivery settings must still be configured in the UI. Wiring was deliberately not added because a missing metadata key would fail the send.
- **HL7vXGeneric multipart:** the exact internal type (MSHSegment XmlDocument, body BodySegments XmlDocument, ZSegments String) is imported as one native XML message: an `HL7Multipart` root, then the header root, then the body root. A strict five-statement prelude is required. A whole-message copy from another multipart becomes `UseMessage` + `SaveMessage`, which also copies metadata (BizTalk starts a new context). Non-empty `ZSegments`, mixed sources and part-level context (`msg.BodySegments(MIME.FileName)`) stay blocked. `hl7-xml-render` flattens this shape; BTAHL7 assembler behavior is not claimed.
- **Tokenizer:** verbatim strings (`@"^~\&"`) are now read.
- **Measured effect (indicative):** all 13 ownership blockers in BillingServiceProcess are cleared (88 -> 81 unresolved nodes) but 10 now fail on the descendant/predicate XPath reads above, so the `Create HL7 Message` blocks remain Blocked. The batch flows are unchanged. Flows stay disabled; no BizTalk equivalence is claimed.
- **Verification:** 12 new importer tests; 1,635 service tests passed.

### Placement and UDF recommendations (record; mostly not yet built)

"UDF" = a pure user-defined function we implement as a hot-loadable `ITransformModule` and call through `InvokeTransform`.

| Item | Where it should live | UDF? |
| --- | --- | --- |
| `FilenameMaker` macros (and other pure legacy helpers) | Compatibility Pack transform modules (`filename-template-transform`) | Yes (done for `MakeFilename`) |
| Protocol adapters for bound Sends/Receives (FTP, SFTP, SMTP, others) | Hot-loadable deliver/fetch packages (e.g. `Mulse.Adapters`) | No: modules with side effects |
| Map renderers / pipeline components | Render/Augment modules; importer wires them from explicit user choices | No (modules) |
| Dynamic port `Address` / `TransportType` overrides | Core native `Deliver` setting expressions + `If` per transport | No |
| Correlation sets, `DNFPredicate`, `Listen`/`ListenBranch`, `DateTime`, transactions | Core durable executor | No |

Rules: anything that affects flow correctness, durability or recovery stays in core; hot-loadable packages hold adapters and pure UDFs; a missing package must fail closed; imported call sites must stay explicit in the UI.

**Version pinning (future, not implemented):** today module code versions are not pinned. Plan: add a `Version` to `ModuleDescriptor`; add an optional pinned version to `InvokeTransform` and to module step definitions; record the code version in checkpoints so replay detects a changed UDF; enforce in `FlowModuleDependencies` (refuse to start on a missing or mismatched version). Until then, a replayed `InvokeTransform` may produce a different value after a package update.
and multi-arrival (Receive) continuation. The temporary checkpoint is discarded after each test.

## 2026-11-04 increment: application bindings and BRL exports

- Generic import requests accept explicit external BRL XML exports and preserve all versions/client rules.
- Application-wide bindings are matched by `ModuleRef` assembly evidence and scoped to each orchestration's
  physical port references, rather than relying only on export filenames or assigning every application port.
- Supported direct-child XML string/boolean equality rules and literal assignments become editable native
  proposals with explicit version selection, graphical node editing and a no-I/O runtime-identical preview.
- Unsupported rules are reported without partial executable policies. Potential client-rule assignment conflicts
  are diagnosed; runtime conflicts fail explicitly. Native evaluation is single-pass, not a BRE agenda emulator.
- Called policy/version evidence survives draft persistence. Additional source/binding/rule paths survive API
  import/download reanalysis. No policy proposal auto-wires or approves a CallRules orchestration.
- Actual beta export validation and remaining Billing gaps are recorded in the Billing migration guide.
  Full orchestration, dynamic-map, pipeline and operational compatibility remains unfinished.

## Transport and operational binding increment

New native deliver modules (descriptor-driven, so every setting is editable in the flow UI):

- `sftp-deliver` (SSH.NET): host, port, username, password, privateKey/passphrase, `hostKeyFingerprint` (SHA256 base64) or an explicit
  `acceptAnyHostKey` (default false), remotePath, remoteFileName (`{{payloadName}}`, `{{flowId}}`, `{{executionId}}`, `{{metadata.key}}`),
  overwrite, append, remoteTempPath (upload then rename), timeoutSeconds.
- `ftp-deliver` (FluentFTP): the same plus `encryptionMode` (Auto/None/Explicit/Implicit), `passive`, `acceptAnyCertificate` (default false).

Importer changes (bindings only; flows stay disabled and fail-closed):

- nsoftware `AdapterConfig` nested XML is flattened; masked (`******`) values are never read. `nsoftware.SFTP`/`nsoftware.FTP` send ports map to
  `sftp-deliver`/`ftp-deliver`. `%Variable%` binding values become Config requirements; user names and passwords become Secret requirements;
  `%SourceFileName%` -> `{{payloadName}}`, `%MessageID%` -> `{{executionId}}` (approximation, warned).
- SMTP now reads `SMTPHost` (`host:port`), To/CC/From/Subject/Username/SMTPAuthenticate (previously ignored). FILE send maps `FileName`/`CopyMode`;
  FILE receive splits the address wildcard from the directory, honors `FileMask`, and uses `PollingInterval` as an Interval trigger.
- Send-port subscription filters are translated to a suggested `conditionJson` (operators ==, !=, exists; single group) in the draft warnings; it is
  not auto-applied because the promoted-property metadata keys must be populated first. Other filters are reported.

Not importable (reported as draft warnings, configure in the UI instead):

| BizTalk setting | Mulse option |
| --- | --- |
| SecondaryTransport (backup) | add a second delivery action |
| OrderedDelivery / Priority | no strict ordering; review concurrency |
| StopSendingOnFailure / RouteFailedMessage | flow retry, `onFailure` and failure handling |
| Send/receive service windows, schedules | Interval trigger schedule |
| Enable=false receive location | draft stays disabled; enable deliberately |
| Pipeline-component properties, port-level maps | render/augment/transform steps |
| DeliveryNotification, Tracking | not imported |
| WCF-WSHttp security, timeouts, message sizes | `http-deliver` has no equivalent; use headersJson/proxy |

Unverified assumptions: nsoftware `SSLStartMode` (mapped to Implicit on port 990, otherwise Auto), `SSHAuthMode` (password assumed; set `privateKey`
if keys were used) and BizTalk FILE `CopyMode` (0 append -> unsupported, 1 create new, 2 overwrite).

## Native visual building blocks

- Descriptor-driven external transforms support hot reload; checkpoint state has visual configuration and repair. Render/map dispatch supports allowlisted renderer settings, leased invocation and per-execution grouping, not nested dispatch or durable cross-run batching.
- Metadata-mode XML policies and visual promotion preserve bodies and can select maps. Count-triggered collectors persist correlated batches and receipts, support timezone-aware deadlines and confirmed early release, and freeze ambiguous failures rather than replay blindly. Tests cover persistence, restart, retries, acknowledgement and DST cases; these are not exactly-once delivery or automatic translation of Billing batching policies.
- These controls are shared by imported and scratch-built flows. They neither translate all Billing control flow nor clear its execution gate; dynamic maps, pipelines, correlation/timing, request/reply, transactions and failure equivalence remain open.

## Developer SDK/control-flow increment

- [Module developer guide](Module-Developer-Guide.md) documents payloads/context, native module interfaces,
  hot-loadable packages, leased renderer/transform invocation, callbacks, retries and migration scaffolds.
- Public structured ODX reading preserves the original ordered tree and expressions. An explicit import-UI
  source-model download is scoped to the selected orchestration and kept separate from saved sanitized reports.
- Explicit developer bindings can execute first-match decisions, bounded loops, sequential bodies and local
  exception catches inside native flows. Missing bindings anywhere in the selected service fail before effects;
  declarations, activities/waits and transaction boundaries require implementation, not automatic pass-through.
- The in-process executor does not persist its program counter; the separate native persisted increment below
  supports a bounded continuation subset, not inferred transactions. Existing runtime helpers/pipelines remain
  deliberately untranslated. No imported-flow execution approval is implied.
- An opt-in UI-configurable metadata deadline now pins first-message N:D/H/M policy intervals or updates
  BatchTime on the existing deadline's local date using the source's first-message comparison. Explicit
  timezone, strict parsing, native DST choices and persisted timing state prevent silent clock guesses.
- A native counted XML envelope supplies ordered roots and MessageCount to subsequent maps. A synthetic
  multi-client composed test verifies metadata rules, independent deadlines across disk restart, idle release,
  leased dynamic XSLT selection, exact header/body/trailer output and source acknowledgements. This does not
  install the original separate header/body/trailer maps or implement their custom pipeline/transaction behavior.

## Automatic typed ODX translation increment

- A bounded interpreter now automatically compiles typed scalar assignments and decision/loop conditions;
  ordered nested execution no longer requires hand-written delegates for that supported subset. It does not
  execute arbitrary C#/CLR calls. Types, schema adapters, declarations, maps, transports and transactions are
  not inferred from syntax. Unsupported source shapes remain explicit, source-addressable blockers.
- Existing candidate downloads include automatically bound C# with embedded private source and per-node JSON
  translation reports. Generated source compiles against the public SDK. Invalid/surplus-service input emits
  an explicit blocked report. The saved migration inventory remains separate and sanitized.
- Complete-binding checks share executor validation, honor explicitly supplied adapters and own activity
  metadata correctly. Compilation diagnostics remain the original syntax classification, not an approval.
- Explicit scalar service initializers are type-checked and run before the service body, without reinitializing
  persisted instances. The actual Billing files now compile 14 such initializers (main eight, batch services three
  each); defaults, constructors and scoped declarations are not guessed. Actual source coverage and remaining
  runtime gaps are recorded in the Billing guide; no imported execution gate is relaxed.

## Persisted native ODX increment

- A single-host persisted interpreter retains nested program position, facts/messages/replies, source receipts,
  catches and exact correlated receive/listen choices. UTC deadlines are pinned once; idle continuation uses
  the existing hosted driver without refetching or replaying upstream stages.
- The installed UI-configurable `odx-program-augment` node binds automatic scalar statements, message copies,
  allowlisted GetType/transform maps and request-port responses. Maps use leased renderer invocation; explicit
  renderer-stage pipeline/send adapters remain developer contracts, not translation of original components.
- Effects use persisted intent; ambiguous effects/writes freeze blind replay. Safe persisted running positions
  can resume after restart. Downstream retries retain output/replies and do not re-run the ODX map. Pending
  instances block unsafe edits/deletion. Atomic/long-running transactions remain explicitly unsupported.
- Source receives now compile exact operations, AND Equals/Exists filters, fully qualified correlation types
  (including sibling source models) and receive-initialized service correlation sets. Source-derived tuple routing
  keeps clients independent; rejected activation input creates no empty checkpoint and cannot notify source receipts.
- Complete map shapes support output promoted-property writes and bounded counted XML construction before a
  same-message transform. Read-only actual-source checks bind all 37 transform statements (29 main, four per batch
  service), all five receive filters and both batch services' two-receive correlation sets. These counts establish
  syntax/binding coverage only, not executable original maps, pipeline behavior or business-output equivalence.
- Synthetic disk-restart/XSLT/reply/polling/retry tests verify the native subset. Main-service outbound/scoped
  correlations, DateTime/batch expressions, general schema/context/DOM behavior, actual maps/pipelines, transaction
  recovery, canonical importer wiring and imported end-to-end equivalence remain open. Helpers stay untranslated.
  The existing imported-flow execution gate is unchanged.

## 2026-11-05 increment: pipeline components, HL7 XML render, SMTP attachment name

Implemented (tests: `BizTalkTransportMappingTests`, `BillingHelperModuleTests`, `Hl7XmlRenderTests`, `BillingPluginTests`):

- Pipeline-component equivalents (later renamed; see the module-move increment below): `hl7-message-type-augment` (HL7TypeCaster
  equivalent, `Mulse.Hl7`) and `set-context-property-augment` (SetProperty equivalent, Compatibility Pack). Both are
  descriptor-driven, so all settings are labeled UI fields.
- The importer parses `SendPipelineData` from bindings into `PipelineComponentConfig` (secret-named properties dropped) and inserts
  pre-filled augment steps for `HL7TypeCaster`/`SetProperty`. MIME/S-MIME and BTAHL7 components produce warnings; hints use
  `BT-PIPELINE-COMPONENT-HINT`.
- `hl7-xml-render` (`Mulse.Hl7`) renders HL7 v2 XML to ER7 (`trimTrailingDelimiters`, `segmentTerminator` CR/LF/CRLF dropdown). It is
  selected when a send port has a BTAHL7 `*Asm` component.
- `smtp-email-deliver` has `attachmentNameMetadataKey`; the importer sets it from a SetProperty FileName component.
- UI render test confirms the new modules expose explicit labeled fields (the "Add setting" button is only for undeclared keys).

### Work tracker (status recorded between steps)

| # | Item | Status |
| --- | --- | --- |
| 1 | Importer tests: `hl7-xml-render` selection/trim setting, SMTP `attachmentNameMetadataKey` | done (2 tests in `BizTalkTransportMappingTests`) |
| 2 | Scope pipeline-component augments per send-port route | done: identical pipelines run flow-wide; differing ones get a `targetRoute` = `route-N` condition (test added); 1487 tests pass |
| 3 | Read default pipeline components from `.btp` when binding lacks `SendPipelineData`; map receive pipelines | send side done: `ApplyPipelineFileDefaults` matches the port's TransmitPipeline to a `.btp` artifact by name, reads `<Property><Value>` defaults (secret-named dropped) and warns that deployed overrides are unknown (test added, 1488 pass). Receive pipelines: only evidence/`BT-PIPELINE-COMPONENT-HINT` guidance, no auto-inserted steps (no native equivalents for disassemblers beyond `hl7-parse`/`flat-file-parse`) |
| 4 | Audit ODX translation for the three Billing orchestrations (translated / stubbed / dropped) | done - see "ODX audit" below |
| 5 | Final docs/verification update | done (full suite 1488 passed, 0 failed) |

### ODX audit (real Billing project, 3 orchestrations; analysis run 2026-11-05, bindings sample included)

Shape inventory is complete (nothing dropped from evidence): 192 / 196 / 574 shapes for BillingBatchDoubleBodyProcess / BillingBatchWithTrailerProcess / BillingServiceProcess. Each artifact reports `StubGenerated`.

**Default import path** (draft flow: `route-selection-augment`, `decision-augment`, `stateful-orchestration-augment`, pipeline augments):

| Source construct | Result |
| --- | --- |
| Decisions (10 / 10 / 31) | Simple equality branches become `decision-augment` rules that only tag metadata (`biztalk:<decision>`). 9 / 9 / 24 branches were too complex and are quoted in a scaffolded C# module. |
| Decision branch bodies (sends, map construction, assignments) | NOT executed by the draft flow; the tags are not consumed by anything yet. The behavior still has to be wired by hand, or by the native path below. |
| Listen / correlation / convoy / long-running + atomic scopes | Represented only by a scaffolded `stateful-orchestration-augment` and atomic-scope tagging with a compensation requirement per route. |
| CallRules `Billing Processing Policy` (version -1) | Reported (`BT-RULE-POLICY-MISSING`); policy not in supplied exports; no policy is wired. |
| Dynamic maps (`Create Header & Body`, ...) | Evidence only; require explicit native renderer choices. |
| Delay | Not translated in the draft path (native path supports pinned timers by explicit binding). |

**Opt-in native path** (`GenerateNativeFlow`): replaces the augments with a single `native-control-flow-augment` and produces a fail-closed `BT-NATIVE-UNRESOLVED` blocker for each construct it cannot prove. Blocker counts: 128 / 128 / 338. Dominant causes by shape: decisions/branches (34 / 34 / 91), port declarations and binding attributes (22 / 22 / 33), Sends needing explicit protocol adapters (11 / 11 / 24), message assignments needing exact schema ownership or explicit map choices (10 / 11 / 56), variable assignments without distinguished-field declarations, DateTime/`System.Type` variables, port types/operations/property refs (structural contract translation), correlation declarations, transaction/scope attributes, Listen/Delay.

`GenerateNativeFlow` is exposed in the import panel and enabled by default; browser interaction has not been verified.


### Native-first loop

Billing is only the worked example. Changes below are generic importer rules.

| # | Item | Status |
| --- | --- | --- |
| N1 | Contract/wiring declarations (`Module`, `PortType`, `OperationDeclaration`, `PortDeclaration`, Physical/Logical binding attributes, `PropertyRef`, `CorrelationType`, `PartDeclaration`, `MultipartMessageType`, operation `MessageRef`) no longer produce `BT-NATIVE-UNRESOLVED`; they are kept as named structure nodes. Test: `NativeImport_TreatsContractDeclarationsAsNonBlocking`. | done |
| N2 | Native `Send` accepts FTP / SFTP / SMTP bound ports (settings pre-filled from bindings) in addition to FILE / HTTP | done |
| N3 | Native `Send` with a transmit pipeline: attach imported pipeline components (`hl7-xml-render`, set-property) as ordered native nodes instead of blocking. | partial: durable Augment now supports a single named input and named output with isolated working/source messages, explicit UI configuration and verified effect recovery. Import still blocks transmit pipelines with manual wiring guidance: component identity, complete source order, deployed overrides, unsupported stages and message-copy naming need safe translation before automatic wiring. |
| N4 | `System.DateTime` / `System.Type` scalar variables and `DateTime` expressions in the native expression reader | pending |
Billing's `dtEndTime.Subtract(DateTime.Now)` still needs N4. Native `Try`, Scope-as-Sequence/Try, Catch (single untyped) and "(not enforced)" transaction evidence nodes are done; `Listen`/`ListenBranch` and typed/multiple catches pending |
| N6 | Correlation sets, receive filters (`DNFPredicate`), convoy semantics mapped to metadata-key bindings editable in the UI | pending |
| N7 | `CallRules` policy version selection and fact bindings in the UI; dynamic-map renderer choices in the UI | partial: the import panel already exposes explicit policy test-version selection and dynamic renderer choices. Only a proven single XML fact can be wired automatically; broader fact binding and BRE behavior remain open. |
| N8 | UI page to list and resolve each `BT-NATIVE-UNRESOLVED` blocker (and expose `GenerateNativeFlow`) | partial: native generation is exposed and enabled by default; the native editor now lists unresolved nodes, including nested branches, with links to their editable settings. Historical diagnostics remain unchanged and do not approve execution. A dedicated resolution workflow and full browser verification remain open. |
| N9 | Receive-pipeline components, MIME/S-MIME, BTAHL7 escapes/validation/acks | pending |

Full suite after N5/N8 increments: 1494 tests passed. Visual-editor rendering is covered by tests; browser interaction has not been verified.

### Three-pass continuation (2026-11-06)

1. **Partial:** repeated pipeline augments retain order; unknown components and movement across HL7 assemblers are reported. Full renderer ordering remains open.
2. **Done:** candidate use requires matching successful analysis settings; reanalyze after external source-file changes, which are not detected automatically.
3. **Done:** the editor links nested unresolved nodes by path and ID; historical diagnostics do not grant approval.

Verification: 1,496 service tests passed; UI rendering was tested without a browser. No behavioral equivalence was established.

### Ten-pass continuation: named-message pipeline foundation

All ten passes completed: durable named augments validate one input/output payload, isolate the source and working batch, journal effects and recover verified outputs without blind replay. The editor exposes mode, names and module settings. Tests cover validation, round-trip, sequential Billing property augments, waits and disk-backed ambiguous outcomes. Send-pipeline import remains blocked, with port/pipeline-specific manual wiring guidance and its delivery retained; no component equivalence is assumed.

Verification: 76 targeted tests and 1,508 service tests passed; solution build and `git diff --check` succeeded. UI component rendering was tested, not browser interaction. This does not establish BizTalk/Billing equivalence or automatically translate transmit pipelines; candidates remain disabled and unresolved nodes block execution.

### Ten-pass continuation: exact native delay migration

| Pass | Focus | Status |
| --- | --- | --- |
| 1 | Import exact tick-based TimeSpan constructors. | done: whole-second nonnegative integer ticks, optional `L` suffix. |
| 2 | Import exact TimeSpan.FromTicks calls. | done: same exact literal subset, with optional `System` qualification. |
| 3 | Enforce native duration and source literal bounds. | done: overflow and durations beyond 366 days remain blocked. |
| 4 | Block fractional, zero, negative and dynamic durations. | done: no rounding, parsing of arbitrary expressions or replay assumptions. |
| 5 | Preserve source evidence and diagnostics for blocked durations. | done: `BT-NATIVE-UNRESOLVED` and source expression remain visible. |
| 6 | Preserve and block nested delay behavior. | done: source children are retained under the blocked node. |
| 7 | Bound oversized diagnostic evidence. | done: parsing stops above 4,096 characters and delay expression excerpts are truncated at 1,024. |
| 8 | Verify imported wait editor round-trip. | done: editor loads, persists and reopens the edited wait; component test, not a browser test. |
| 9 | Verify imported wait suspends and resumes without replay. | done: synthetic execution waits until due, delivers once and does not refetch. The import gate remains in force. |
| 10 | Run regression verification and record remaining gaps. | done: 68 targeted tests passed, solution build and `git diff --check` succeeded. |

Dynamic DateTime delays, sub-second timing and the other N4/N5 behavior remain untranslated; no BizTalk equivalence or browser interaction was verified. The full suite was not rerun in this continuation.

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

## Implementation status (baseline updated 2026-10-30; later increments above)

**Overall: in progress, not fully implemented.** Completion below refers only to the explicitly listed increment, not to BizTalk-equivalent execution.

| Workstream | Status | Current evidence / remaining work |
|---|---|---|
| Canonical migration model and diagnostics | Report and scalar XML mapping increments verified; workstream partial | Versioned reports preserve source provenance, statuses, diagnostics, remediation and warnings through save/reopen, copies and backups. A versioned editable scalar XML mapping model now persists in render-node settings. Exact imported artifact ownership, confidence evidence and authoritative/live readiness remain pending. |
| Project and dependency discovery | Path, ODX owner-selection, workspace-map and schema-source evidence increments verified; workstream partial | Full-path identity, explicit paths, conservative HintPath inference, cycles and colliding draft IDs are handled. Reports index separate/unreferenced map projects and case-sensitive schema class candidates from explicit Namespace/TypeName metadata or labeled literal naming conventions. Ambiguous/missing/incomplete source evidence stays explicit. Evaluated MSBuild/deployed identities, out-of-scope discovery, custom-project dependency chains, exact reference-path assertions and proven policy/map selection remain pending. |
| Mulse message abstractions | Pending | Body/parts/context/promotions/correlation model still to be designed and implemented. |
| Script and custom-code modules | Bounded script-evidence inventory verified; runtime translation pending | Reports retain script language, owning functoid ID, source line, size, hash and textual BizTalk API hints, not script bodies. This is not a portability/safety classification or script execution. Existing generated stubs are not completed translations. |
| Mapping Designer and map translation | Schema-browser, scalar .btm proposal, missing-value, XSD-validation and ordered string-operation increments verified; workstream partial | Scratch and imported drafts share XSD upload/paste, scalar field selection, explicit proposal application, persisted source-root/schema contracts, Error/Omit/Default policies and five explicit string operations. Preview and actual render share validation/runtime behavior. Source-schema file candidates are visible in migration reports. Repetitions, arbitrary conditions/functions, functoids/scripts, evaluated/deployed schema ownership, automatic orchestration wiring and trusted output parity remain pending. |
| Pipeline translation | Bounded source inventory and fail-explicit scaffold increments verified; behavior translation pending | Reports retain stage/component order, category, class/version, source lines and property names only. Candidate downloads include compileable pipeline stubs that fail explicitly and are not wired automatically. Component configuration, context effects and runtime behavior remain untranslated. |
| Orchestration translation | Source-evidence and fail-explicit scaffold increments verified; translation partial | Persisted hierarchical designer inventory and coded behavior blockers now include real While shapes, BRE calls, assignments, ports, failures and waits. New generated stubs compile against the SDK and fail explicitly until implemented. Loops, waits, correlations, transactions, exceptions, and complete orchestration semantics still require translation. |
| Bindings and routing | Pending beyond existing heuristics | Exact port/subscription/artifact relationships and transport fidelity remain pending. |
| Readiness gates | Fail-closed increment verified; verified approval pending | Flows carrying migration reports cannot be enabled or run through UI/API/runtime, schedules, push registration, or processing retries. Saved provenance cannot be replaced by flow updates. Disabled editing, duplication and terminal source acknowledgements remain available. No manual override or verified approval/revalidation path exists yet; legacy flows without metadata need re-import. |
| Verified SpaceTRAX pilot | Source/import assessment verified; executable parity pending | BillingServiceProcess now analyzes from its ODX through its exact owning project, with three Billing candidates and specific missing behavior identified. No BizTalk golden-output comparison or production execution has been performed. See BillingServiceProcess-Migration-Guide.md. |

### Completed increments: dependencies and migration reports

- Projects are identified by full path, with explicit project references, conservative HintPath inference, cycle handling, collision-safe draft IDs and diagnostics for ambiguous or unevaluated references. Import UI exposes dependency evidence. Evaluated/deployed identity remains unproven.
- Versioned migration reports preserve source provenance, artifact statuses, blocking diagnostics and remediation across UI/API, saves, copies, reload and backup. Reports are import-time snapshots, not live approval; stubs and unwired mappings are not completed translations.
- Earlier checks passed 53 dependency and 154 report-related tests and solution builds. Read-only SpaceTRAX analysis found 52 BizTalk projects, 17 custom assemblies and 53 disabled candidates; 126 references were unresolved and 441 external to the analyzed workspace. These are inventory counts, not readiness or behavioral evidence.

### Completed increment: fail-closed migration readiness

- Report-bearing imports cannot be enabled or run through UI/API, scheduled/push execution or processing retries; provenance is protected. Disabled drafts remain editable and copyable. Configuration changes or report statuses do not grant approval; there is no manual override or verified approval path yet.
- Existing terminal source acknowledgements can complete without replay, but already-running executions are not cancelled. The gate is a workflow safeguard, not an authorization boundary; legacy imports without metadata need reanalysis/re-import.
- Earlier verification: 215 targeted tests and solution build passed, including disabled editing, retry retention, source acknowledgements and scratch-flow compatibility. No real BizTalk workflow was executed.

### Completed increment: persisted scalar XML Mapping Designer

- Node-owned `mappingJson` models scalar paths, namespaces and constants; `xml-mapping-render` generates XSLT and fails explicitly for unsupported cardinality or invalid mappings. The designer edits and saves the model for scratch and imported drafts; preview uses the same runtime without source fetch or delivery. Preview samples are not saved. Limits, unsafe XML/XSLT restrictions and validation apply to both preview and execution.
- Editing or previewing does not wire an imported map or approve execution. Repeating records, general functoids and business-map parity remain open. Earlier verification: 287 related tests and solution build passed using synthetic fixtures; UI tests covered component callbacks, not browser gestures.

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

### Completed increments: pipeline evidence, pipeline scaffolds and native string operations

- **Pipeline evidence:** parse bounded `.btp` XML instead of searching raw text for direction GUIDs. Reports retain declared stage/component order, category IDs, class/version, original source lines and property names, never property values, descriptions or cached display metadata. Known categories label receive/send stages; unknown/mixed categories, malformed/unreadable XML, misplaced constructs and ambiguous/complex identity metadata remain explicit blockers. Caps are 64 saved stages, 256 components, 64 property names per component and 512 characters per identity/name, with total/omitted counts and blocking truncation diagnostics. DTDs/external resolution are prohibited.
- **UI/manual workflow:** fresh import candidates and saved drafts expose **Migration report > Pipeline source inventory**. Inspect original configuration securely; inventory does not configure or execute components. Parsed categories also drive receive/send project counts, so property/description GUIDs cannot spoof direction. Pipelines retain Unsupported status and `BT-PIPELINE-UNTRANSLATED`.
- **Pipeline starter modules:** the existing single-file and ZIP download controls now include one deterministic, uniquely named C# placeholder per local/referenced pipeline. It contains bounded names-only stage/component evidence, compiles against the actual Mulse SDK without implicit usings, honors cancellation and throws `NotSupportedException`. It is not automatically installed or wired. Developers must choose the correct native parse/augment/render stages and implement every component/configuration/context effect; an augment-shaped placeholder is not pipeline equivalence. Malformed sources still produce a fail-explicit placeholder plus blocking report evidence. Typed UI requests now omit absent optional scopes instead of overwriting server collection defaults with JSON null.
- **Native string operations:** each scalar field or constant exposes ordered Add/Edit/Move up/Move down/Remove controls in Mapping Designer. Adding an operation upgrades the saved definition to version 2. Supported operations are NormalizeWhitespace (only XML whitespace), UppercaseAscii, LowercaseAscii, Prefix and Suffix. ASCII case conversion deliberately leaves non-ASCII letters unchanged; prefix/suffix arguments are literal text, never executable XPath. Operations run after source/constant/default selection; missing Omit assignments stay absent. Maximum 16 operations per field and 4,096 characters per prefix/suffix argument. Null/unknown/oversized definitions fail explicitly. Version-1 definitions without operations retain their previous behavior. This is explicit native authoring, not automatic functoid translation.
- **Read-only SpaceTRAX verification:** all **32 local `.btp` files** were readable: **16 Receive / 16 Send**, containing **63 components**, with no inventory diagnostics. `SendBTAHL7` retained PreAssemble / Assemble / Encode order, with HL7TypeCaster then the BTAHL7 assembler and an empty Encode stage. UTF-16 BOM/source lines were handled. These are workspace-wide source findings, not proof that Billing selects every pipeline or that deployed versions/configuration agree. Sanitized names-only results are retained in a session artifact; the temporary local verification driver was removed. No BizTalk component, script, production endpoint or business map was executed.
- **Verification:** 604 portable Service.Tests cases pass with zero failures/skips. New checks cover saved report/disk/UI round trips, exact caps and omissions, malformed inputs, metadata escaping/value exclusion, string semantics/order/absence/cardinality, UI editing/save/reopen/provenance, real HTTP preview and single/ZIP downloads, deterministic same-name pipeline identities, generated SDK compilation, cancellation and fail-explicit execution. Schema attach/remove retains operations. The rendered import panel exposes download buttons and non-approval warnings.
- **Final build checks:** full solution build succeeded. An explicit test-project rebuild with CS8785 treated as an error produced zero warnings/errors; only the installed preview SDK notice appeared. The complete 604-test suite passed after that rebuild. `git diff --check` found no whitespace errors; the existing Designer.razor LF/CRLF advisory remains. Visual Studio file-error inspection was unavailable/stale, so actual compiler/test results supplied verification. No dependencies were installed and no external source files were changed.

### Completed increments: visual mapping and builder

- Scratch flows and imported drafts share the tabbed Flow/Mapping builder and persisted scalar mapping editor. Schema trees, click/keyboard connections, constants and ordered operations edit the same `mappingJson`; schemas can be inspected separately from runtime validation. Root, namespace, cardinality and overlap guards remain explicit. Repeated records and functoid inference remain unsupported.
- Tabs preserve transient editor state; contextual help and collapsible settings keep errors and migration blockers visible. Component tests cover editing, persistence and navigation; browser layout, file-picker, hover and keyboard-focus acceptance remain open. Prior verification: 612 service tests passed and UI build succeeded.

### Known limits / next increments

1. Build verified translation evidence and explicit revalidation tied to the saved definition before introducing any approval path. There is deliberately no manual acceptance override in this increment. Exact artifact ownership and confidence evidence remain pending.
2. The gate applies to flows carrying migration metadata. Historical imports without it, manually copied definitions submitted as new scratch flows, or direct workspace/backup edits removing provenance cannot be identified automatically; reanalyze/re-import historical drafts. This is a workflow safety policy, not an authorization boundary against workspace editors.
3. Extend literal schema-source evidence to evaluated build/deployed identities and resolve missing project declarations, then wire only proven map/orchestration relationships. Explicit Namespace/TypeName evidence, supplied-schema trees, visual scalar connections and conservative scalar proposals now exist; automatic authoritative ownership/wiring does not. Add explicit record repetition/cardinality and broader conditions/functions beyond presence/defaults and the five supported string operations; compare all selected imported outputs against trusted golden samples.
4. Extend discovery/resolution to projects outside the supplied scopes, C# project dependency chains, MSBuild property/configuration evaluation, shared/deployment output directories and persisted exact reference-path assertions. HintPath ownership remains a labeled inference, not proof of assembly equivalence.
5. Resolve map/schema/pipeline ownership per orchestration and exact binding/port relationships. Path-aware dependency inventory does not eliminate existing project-wide artifact heuristics.
6. Add message/context abstractions and approved script translation/runtime; metadata hashes and textual hints do not establish portability. Whole-pipeline fail-explicit starters now exist; extend scaffolding to remaining custom/script behavior, then implement actual pipeline/binding/orchestration translation in the planned order.
7. Complete a small local synthetic-input/golden-output SpaceTRAX pilot before claiming functional migration fidelity. Product work in items 1 and 3-6 can still progress without team input; deployed policy/binding/reference evidence and owner-approved golden outputs cannot be invented.

Record each subsequent increment here with its scope, UI/manual-workflow entry points, verification results, and remaining gaps. Do not mark an entire workstream complete based only on scaffolding or a successful build.
