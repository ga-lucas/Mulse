# BillingServiceProcess migration guide

**Original assessment date:** 2026-10-30

**Original scope:** local source only; no BizTalk instance, deployed rule store, production bindings, credentials, or external services were queried. No source was edited for that assessment. The broader BizTalk compatibility plan remains in progress; this guide does not claim that plan is complete.

## Configuring replacements without BizTalk development tools

BizTalk-generated synthetic input/output pairs, generated XSLT and extension-object files are optional
reference evidence, not prerequisites for importing a disabled draft or authoring a native replacement.
They may be supplied later if a BizTalk development environment becomes available. Their absence means
source-oracle parity is unverified; it does not prevent continued native implementation or synthetic testing.

In the native control-flow editor, **Test native control flow with mocks** accepts synthetic text and
metadata, and explicit mock responses for augments, renders and deliveries. It runs only the native
control-flow definition in isolated memory: no real source, destination, module or workspace checkpoint
is used. The output includes mock calls, output and replies. Unresolved nodes still block execution;
suspended waits/receives cannot yet be resumed in this one-shot test. This is not a full-flow test or
an imported-flow execution approval, and mocked module responses do not verify their real behavior.

Use a native Render node, or **Select map during processing** for metadata-based selection, and configure
**Dynamic map / renderer choices**. Each exact selection value is independently bound to a visual XML mapper,
ordered renderer pipeline or loaded renderer plugin, with editable settings. Separate choices can represent
preprocessing, header, body and trailer maps. Keep Billing-specific implementations in `Mulse.Billing`.
These are native replacement choices, not instructions to load a BizTalk map class or compiled assembly.
For a native Render node, choose **Ordered named inputs to named output** and add the source messages in their
required argument order. Durable execution is required. Each input and the returned output must contain exactly
one payload; the working batch and unrelated named messages are unchanged. Repeated inputs are allowed, and a
named output can replace an input only after successful rendering. **Compose ordered XML input parts** can be
the first stage in an ordered renderer pipeline. For the inspected JPS source map, its explicit input contract
is root `Root` in `http://schemas.microsoft.com/BizTalk/2003/aggschema`, with unqualified parts
`InputMessagePart_0,InputMessagePart_1` holding CommonBilling and BillingMessageIndexer respectively. Configure
those values explicitly; they are not hardcoded Billing defaults in the generic composer. The following renderer
must still implement the actual map; composing its inputs does not implement its scripts/functoids. Current-batch
grouping is not a substitute for source arguments or durable batch behavior. Successful results persist across
restart; ambiguous renders are held, and verified output reconciliation targets the same named output without
replaying the renderer. These controls are available for scratch flows as well as native draft editing.

Leave unknown choices unresolved rather than selecting a passthrough or guessed implementation.
selection values and missing modules fail explicitly. Imported dynamic transform call sites still need native
translation/wiring, and all imported Billing flows remain disabled. Configuring a replacement or adding an
optional fixture neither proves business equivalence nor bypasses the migration execution gate.

## 2026-11-04 update: beta exports and reusable rules import

The supplied Interfaces application binding export contains all three Billing orchestrations; Billing is
not a separate application. The importer now matches explicit assembly/orchestration ownership inside
exports rather than requiring a Billing-named file, and scopes physical ports per orchestration. All three
Billing candidates resolve the supplied export. Direct MessageBox receive subscriptions are still not
inferred from physical port references, so receive placeholders remain explicit where appropriate.

The supplied BRL `rules.xml` contains 47 versions of `Billing Processing Policy` (1.6 through 1.52),
1,270 rules across those versions, and two vocabularies. These are policy versions, not separate clients:
each version contains client/group predicates. No active/deployed version is inferred. All 47 versions
produce native single-pass XML field-rule proposals with explicit version selection, graphical rule/field
editing, saved node settings, and no-delivery preview. This support is generic, not keyed to Billing names.
Called-policy evidence persists in each orchestration's migration report; proposals are not auto-wired.

Local synthetic rule checks matched 1,232 cases and verified their assignments, rejected 37 cases with
conflicting matching assignments, and excluded one statically false constant guard. These are translation
consistency checks, **not BizTalk golden-output equivalence**. Conflicts require explicit review rather
than an invented first-match policy. No real patient/billing messages, credentials, or destinations were used.

Analyzing the broader local source folder inventories 52 BizTalk projects and 228 project-included maps,
including partner sources. Four shared-output assembly references remain unresolved. The original source-only
findings below are historical where they say bindings/policy exports are unavailable; exports are now supplied,
but deployed-version evidence, complete map translation, custom pipeline behavior, and executable orchestration
semantics remain outstanding. Full Billing execution, batching, correlation, response and transaction behavior
are **not implemented by this increment**; the migration gate remains unchanged.

Use the Billing directory (or its `.btproj`/`.sln`/included `.odx`) as Source path, the parent source folder
containing both Interfaces and Partners as Additional sources, and the supplied XML files in their respective
Binding export and Rule policy fields. Load a disabled candidate first, then reopen Import from BizTalk.
The UI separates named policies and versions; choosing a name defaults to its latest numeric exported version.
For this export that is 1.52, an accepted initial assumption, not proof of the deployed version. Review/preview
before explicitly applying. Existing rule nodes can choose another imported policy/version in node properties.
Each node saves its own pinned, editable snapshot; reimporting never silently upgrades it. The editor explains
conditions and assignments. An optional metadata-facts mode now preserves the original message body while
reading/writing prefixed fact fields. Native render-dispatch can use the resulting MapName metadata to choose
an explicitly configured renderer/map. Both are visually configurable for scratch and imported nodes; neither
auto-translates the original message assignments, maps or CallRules site. Checkpoint state capture also has a
visual editor, but Billing batch collection/timers, transaction semantics and full source translation remain
unfinished. These building blocks are not a runnable Billing migration or an execution-gate override.
No external export files or literal rule/configuration values are committed to this repository.

Additional native increments provide visual metadata promotion, mid-flow map dispatch, explicit current-run
per-selection grouping, and a hot-loadable UTF-8 header/body/trailer renderer example. These can compose
scratch workflows without JSON editing and without modifying the host to add another renderer. They do not
replace the source batch orchestration: its trailer flow waits across messages until a calculated local-time
boundary, can override that boundary using policy BatchInterval, handles later BatchTime changes, and maps
a trailer from the accumulated message count. A literal trailer/current-run group is deliberately not labeled
as equivalent. Deployed host timezone/culture and approved synthetic outputs are still needed to verify that
behavior; production messages are not necessary to continue the remaining runtime/translation engineering.
The four shared Build DLL references are still absent at their project HintPaths; helper sources exist, but
source availability alone does not establish a deployed binary identity or verify a replacement pipeline.

A further native increment adds **Collect correlated batch**, configured entirely through descriptor-driven
node properties. It persists one client's messages across executions/restarts, releases the whole batch when
its minimum count is met, and defers source acknowledgements until successful delivery. Different correlation
keys collect independently. One input execution must contain one correlation value, since a source receipt
may cover all fetched messages. Processing/config-reference edits and destructive flow/workspace operations
are blocked while messages are held. Dashboard/designer checkpoint inspection shows counts and collecting or
released/recovery-pending status without exposing message bodies or source settings. Retry tests cover disk
restarts, ambiguous checkpoint-write acknowledgements, disabled flows and source acknowledgement failures
without redelivery. Exhausted/ambiguous delivery failures retain a frozen checkpoint, not an automatic replay.
An opt-in daily cutoff now adds explicit-timezone idle release: the first arrival persists a fixed UTC deadline,
and an enabled-flow driver resumes at the collector without fetching/remapping upstream messages. The legacy
server timezone has not been established, so timed flows require an explicit per-flow timezone rather than
silently inheriting the host clock. DST gaps advance to the first valid minute; repeated times use the later
occurrence. Tests cover these native choices, restart, idle manual flows, competing polls and frozen failures.
Checkpoint inspection also offers confirmed early release of a still-collecting instance at its inspected
payload count, without a new fetch. Stale counts, pending retries, disabled flows and released instances are
rejected. Release intent persists before downstream effects; ambiguous intent/delivery is not replayed blindly.
These capabilities do **not** translate Billing's changing BatchTime, policy BatchInterval, original timer listen
loops, trailer-count maps, transaction scopes or helper pipelines. No business-message deduplication or
exactly-once semantics are implied, and no imported execution gate has changed.

## Developer extension/control-flow update

The [module developer guide](Module-Developer-Guide.md) describes the public SDK and hot-loadable extension
seams. Import UI source-model downloads now expose the selected orchestration's full ordered hierarchy,
properties/expressions and stable source IDs to developers, separately from the names-only saved report.
The SDK can execute explicitly bound ordered decisions, bounded loops, sequential bodies and local CLR
exception handlers, with complete binding preflight before effects. The in-process executor requires explicit
activities/waits; the separate persisted native subset is described below. No BizTalk transaction manager is
inferred. Synthetic composed-flow tests verify supported native behavior, not the full Billing services.
Existing helpers/pipelines have not been rewritten, and imported drafts remain blocked.

The SDK reader was also exercised against all three actual source files: 574 elements for the main service,
192 for double-body batching and 196 for trailer batching. This proves structural source accessibility, not
execution equivalence. **Minimum count or message/policy deadline** now offers a native UI-configurable subset
of trailer timing: the first N:D/H/M policy interval overrides BatchTime; without an interval, later times
that differ from the first message update the time on the existing deadline's local date. Timing facts persist
across restart/retry/manual release. Strict HH:mm, positive bounded intervals and explicit timezone/DST choices
are intentional native constraints, not legacy culture/invalid-input fallback emulation.

**Wrap XML batch with message count** supplies ordered original XML roots and a payload-count field to a
subsequent renderer. Synthetic composed tests exercise metadata rules, multiple clients, persisted intervals,
idle restart release, allowlisted dynamic XSLT maps, exact header/body/trailer counts, delivery and deferred
source acknowledgement. These establish working native composition, not installation/verification of Billing's
original individual maps or custom pipelines. Full translation of Billing's own listen/correlation, replies and
transaction recovery remains incomplete; the native subset below does not prove that translation. Original
helper conversion remains explicitly deferred.

## Automatic expression translation update

The bounded typed compiler was run locally against the three actual ODX source trees. It accepts all 72
non-else decision predicates: 44 in the main service and 14 in each batch service. It also accepts 29 variable
assignments, seven message-assignment-shaped scalar statements and six loops in the main service, two
variable assignments in double-body batching and one in trailer batching. These are syntax counts, not
execution equivalence: source message/schema facts must be supplied with their real types, and a scalar
assignment classification does not implement XML/message-copy semantics. The subsequent initializer increment
was also run against all three actual files: eight explicit scalar service initializers in the main service and
three in each batch service now compile automatically. Missing defaults, XML/type/helper constructors and
scoped declarations remain blocked rather than guessed.

Candidate developer downloads now include automatically bound C# and per-node translation JSON. Unsupported
nodes remain explicit blockers; original helpers are unchanged. Independent synthetic tests verify nested
branches/loops using the automatic bindings, typed operations and rejection, standalone generated-source
compilation, and fail-before-effects preflight. Imported drafts still use conservative proposals and remain
disabled. The compiler increment alone does not complete the requested runnable Billing import.

## Persisted native runtime update

A separate `OdxDurableExecutor` and installed **Durable ODX program** node now persist nested program position,
typed variables, named messages, catches, receive/listen races, fixed native timers, replies and held source
receipts. Idle restart resumes without fetching/upstream replay. Complete GetType/transform statements can
invoke allowlisted real XSLT renderers through host leases; explicit pipeline-stage and logical-port adapters
are available to developers. Failed/ambiguous external effects are not blindly retried, and pending definitions
remain protected against edits. Synthetic host tests cover disk restart, disabled/concurrent/hosted polling,
real XSLT/replies, deferred acknowledgement and downstream retry without map replay.

The next source-binding increment compiles all five actual receive filters (AND Equals/Exists), exact operation
identities, and both batch services' two-receive correlation sets. The trailer service resolves its correlation
type from the sibling source model. Following receives pin the three promoted source properties across restart;
source-derived tuple routing separates clients without requiring an invented group field.

The next increment compiles all 14 main-service correlation declarations, including its outbound initializers and
nine scope-local sets; they resolve to ten distinct send-preparation sites. A local read-only probe compiled all
three actual source models together and retained both batch receive correlations without executing source statements,
helpers, maps or endpoints. Synthetic disk-restart tests verify send-to-following-receive pinning, scope re-entry,
lexical lifetime rejection and fail-before-transport validation. This is correlation binding coverage, not MessageBox
routing or full orchestration execution. Parameter correlations and automatic outbound instance routing remain unsupported.

Native `sendsJson` adapters now wire exact source Send IDs through ordered leased renderer equivalents to a leased
delivery module, alongside separate request-port replies. Dependency checks include every configured pipeline stage
and destination before fetching. Real-XSLT synthetic tests cover outbound transformation, correlated resume, preserving
the original reply message and holding ambiguous sends without replay. This does not translate original `.btp`
components: the inspected XML/flat-file email pipelines still require SetProperty; HL7 pipelines require HL7TypeCaster,
HL7 assembly and MIME behavior. Those helper implementations remain deliberately untouched, not replaced with pass-throughs.

All 37 actual transform statement shapes now bind completely: 29 in the main service and four in each batch
service, including transport-property tails and the two XML trailer-construction prefixes. No original renderer
was executed by this read-only coverage probe. A bound source statement still needs the actual selected map,
correct schema facts and verified renderer semantics; these counts do not prove map/business-output equivalence.
Synthetic tests verify counted XML trailers through real leased XSLT and rejected input without source callbacks.

This is **not yet runnable Billing import**: native seconds do not translate BatchTime/BatchInterval expressions;
MessageBox/outbound routing, general schema/context/DOM behavior, selected Billing maps/pipelines and full importer
wiring remain open. Original helpers remain deliberately untranslated. Atomic/long-running transactions are
explicitly rejected, not represented by local checkpoint writes. No source-oracle Billing end-to-end parity is
claimed and no imported execution gate has changed.

Native capabilities are also exposed for flows built from scratch: **Ordered renderer pipeline** Properties
supports visual stage selection, settings and reordering; **Durable ODX program > Outbound sends** supports
named send selection, delivery settings, optional response capture and the same outbound stage editor.
These reuse installed/hot-loadable native modules; they are not implementations of Billing's missing maps,
pipeline components or helpers. Source program authoring is still typed JSON, not a visual control-flow builder.

## Native-only migration direction (2026-11-05)

The selected execution target is native-only flows, not completing Billing against the ODX interpreter first.
The first native control-flow foundation now provides source-independent Sequence, If/Else, ForEach and bounded
While nodes, with visual authoring, installed augment/render invocation and full nested dependency checks.
Synthetic fixtures exercise both branches, ordered payload processing, real native XML rules/map dispatch,
loop limits and fail-before-fetch missing dependencies. This is not imported Billing execution evidence.

Native durable blocks now persist waits/receives, named batches, execution cursors and activity-effect intent.
Timer continuation skips source/upstream replay; ambiguous effects remain held rather than automatically resent.
The UI exposes durable IDs, channels, correlations, named-message operations and checkpoint holds. This is not
DTC or automatic transaction compensation. Existing ODX checkpoints are neither rewritten nor discarded.

Opt-in native draft generation (default in the import UI) translates safe ordered structure, simple receives,
exact named-message copies and uniquely owned physical sends/replies. Logical port references are preserved
from the supplied bindings rather than selected by endpoint/name similarity. Native JSON is downloadable.
Service-level String/Int32/Boolean declarations, supported complete scalar statements, first-match decisions
and bounded loops now translate to native typed trees. They are visually editable and survive durable restart.
Complete XPath count/string/boolean reads of declared messages now translate to native expanded-name selectors
for absolute child/attribute paths. Billing's seven complete counting blocks retain all 21 XML reads, additions
and final source overrides; comments are recognized lexically, not executed or stored in the native expression.
No XPath/source interpreter runs at execution time. Native scratch flows can author XML and explicit metadata
facts with graphical controls and clickable unsaved sample paths.
Exact distinguished schema members now resolve from unique declared XSD ownership and compiled explicit relative
imports of declared schema files, with built-in String/Int32/Boolean types and exact nonrepeating element paths.
Native XmlString/XmlInt32/XmlBoolean reads fail on missing/duplicate/nil/invalid fields rather than guessing values.
Complete source LoadXml/copy/typed-member-write blocks now compile to CreateXmlMessage/SetMessageValue nodes.
Construct-owned message references are retained as structural declarations. Source scalar defaults are null/zero/false.
The same operations are graphically authorable from scratch: choose a named message, target path/key and value
expression; sample-path clicks never persist the sample. Created XML inherits no implicit metadata; writes change
only the named snapshot. Synthetic imported construction/copy/typed writes/reply survive disk restart without
changing the source or other copies. Unsupported field types/ownership/context properties remain explicit blockers.

Declared context-property access now compiles to typed native metadata reads/writes from the supplied property
schemas. This includes Billing's String transport/department/group fields and Boolean context fields, without
pretending context access is an XML distinguished-field read. Keys are exact `{namespace}property` identities;
MetadataInt32/MetadataBoolean parse XML lexical values and writes require exact types. Missing context fails,
even when a similarly named XML body field exists. Duplicate or unsupported property identities, undeclared
platform properties and shadowed/scoped messages remain blockers. Synthetic imported copy/context-write/XML-write/
decision/reply fixtures execute across disk restart while preserving source snapshots. Graphical fact selection
and writable targets expose the same capabilities for scratch flows. Inbound promotion and MessageBox/scoped
correlation semantics still require explicit native adaptation; these reads/writes do not implement subscriptions.

Native XML accumulation now translates the trailer batch's dynamic empty-prefix root construction, header/body
initialization, subsequent body append and trailer append: one CreateXmlRoot and four AppendXmlMessage nodes.
The main service similarly translates one root and three fragment appends. Temporary XmlElement aliases are
eliminated only in a complete root-construction block with proven lexical ownership; their enclosing scopes and
transactions stay blocked. These reusable operations are also fully graphical for scratch flows. They preserve
ordered fragments, namespaces, CDATA and internal comments/PIs; named copies remain independent over disk restart.
The append requires one payload per named message and its first document node to be the root element. Leading
XML declarations/comments/PIs/whitespace fail explicitly instead of being skipped. Combined output is checked
against the existing character/depth/node limits before replacement. Synthetic import fixtures verify actual
execution/restart/reply, bounds and graphical authoring; analysis of Billing itself verifies translation only.
This does not implement the partner maps producing those fragments or prove Billing batch parity. All generic
composition belongs to the native runtime; sample-specific transforms/modules belong in `Mulse.Billing`.

The explicitly selected **Billing Processing Policy 1.52 native migration test target** now compiles all three
exact single-`msgBre` CallRules sites to native ApplyXmlRules nodes. The generated namespace/type/parameter alias
and every referenced direct-child String/Boolean schema field must agree; conflicting or unproven calls stay
blocked. Selection is available in the import policy/version UI and retained by analyze/import/download requests;
no automatic latest-version selection is made. Each node pins the selected policy JSON and exposes the same
graphical rule editor for scratch flows. No-match leaves that named document byte-for-byte unchanged; invalid
facts/conflicts fail before mutation. The working payload, other named copies and exact metadata remain intact.

All 40 active rules in the actual supplied 1.52 export executed through the native operation with synthetic
facts and verified literal outputs, without running a helper DLL or transport. One rule contains successive
PreprocessingMapName setters; its proposal now retains the final source setter, with an explicit diagnostic.
Only same-typed literal writes within that rule are normalized; conflicts between different matching rules are
not resolved by assumed priority. Raw source actions remain bounded and mixed target types remain unsupported.
This verifies the selected native subset, not BRE agenda behavior, deployed-version proof or end-to-end parity.

Unsupported helpers, general XPath/schema members, scope/transaction/pipeline behavior and broader policy fact
wiring remain explicit structured Blocked nodes. Unselected BRL proposals remain review evidence, not assumed
execution wiring. All imported flows remain disabled; synthetic native fixture success is not actual Billing
end-to-end business verification.

### Native Billing modules now available separately

Only `billing-field-augment` is Billing-specific and lives in the `Mulse.Billing` plugin. The other helpers were reviewed, found generic, and renamed with no alias (saved flows using the old `billing-*` IDs fail closed until re-selected): the first four plus the set-property step are built into the Compatibility Pack, and the HL7 message-type step is in the `Mulse.Hl7` package (enable it like `hl7-xml-render`).

| Module | Native capability | UI location |
|---|---|---|
| `filename-template-transform` (Compatibility Pack) | One-text-input filename macros. The importer translates `x = FilenameMaker.MakeFilename(arg);` (String target and argument) to a native `InvokeTransform` node with assumed `timeZone=UTC`, `culture=Invariant` settings: review both against the deployed host. `[GUID]` is random per call and `InvokeTransform` is not journaled, so checkpoint the result before a Deliver that must be replay-stable. Dynamic port `Address`/`TransportType` assignments (including `MakeFilename` ones) now translate to an explicit transport dispatch, see the `address-part-transform` row | Native control flow `InvokeTransform` node / Map parsed data transform selector |
| `address-part-transform` (Compatibility Pack) | Parses a dynamic send address for the native dispatch the importer generates for ports whose physical binding is dynamic: FILE (path), FTP (`ftp://host[:port]/dir/file`, default port 21), HTTP (whole address as the URL) and SMTP (`mailto:` stripped). The `TransportType` match is exact and case-sensitive; any other value, an unassigned value, or SFTP/other transports (no verified native dynamic address format) ends in an explicit `Terminate` or stays Blocked. Review the generated defaults (FTP `encryptionMode=Auto`, passive, no overwrite; FILE no overwrite; SMTP host/from placeholders) and supply credentials. Adapter properties set on the outbound message (`FTP.*`, `SMTP.*`) are not translated | Native control flow: `InvokeTransform` nodes and Deliver **Dynamic settings** |
| `filename-template-augment` (Compatibility Pack) | Literal/exact-metadata template to a configured metadata key | Augment toolbox / Native control flow Augment |
| `billing-field-augment` | Exact nonsensitive CommonBilling field to custom text or declared context-property metadata | Augment toolbox / Native control flow Augment |
| `xml-remove-namespaces-render` (Compatibility Pack) | Namespace-removal XML renderer with collision rejection | Renderer selector / Ordered renderer pipeline |
| `hl7-message-type-augment` (`Mulse.Hl7`) | Replaces the `HL7TypeCaster` pipeline component: parses the XML root and writes `<namespace>#<root>` (default `http://microsoft.com/HealthCare/HL7/2X`) to a configurable key (default the BizTalk system `MessageType` identity). Body unchanged; non-XML input fails instead of being swallowed | Augment toolbox / Native control flow Augment |
| `set-context-property-augment` (Compatibility Pack) | Replaces the `SetProperty` pipeline component: writes a value to metadata key `{namespace}name`; no-op when any of the three is empty, as in the source | Augment toolbox / Native control flow Augment |

The importer now reads each send port's `SendPipelineData` from the binding and automatically adds
`hl7-message-type-augment` (for `HL7TypeCaster`) and `set-context-property-augment` (for `SetProperty`, with
name/namespace/value pre-filled) to the draft flow; MIME/S-MIME and BTAHL7 components produce explicit warnings with
their property values. These steps run for the whole flow, so when send ports differ, scope them per route in the UI.

Downstream behavior is wired too: a BTAHL7 assembler on a send port selects the new `hl7-xml-render` module
(HL7 v2 XML to ER7; trailing-delimiter setting imported from `TrailingDelimiterAllowed`), and a `SetProperty` that sets
`FileName` fills SMTP deliver's new `attachmentNameMetadataKey` so the attachment is named as BizTalk's MIME encoder
named it. Not replicated: HL7 escape sequences, schema validation/acks, and S-MIME signing/encryption.

The migration report now emits
`SetProperty`, BTAHL7, MIME/S-MIME and flat-file components, naming the native module to use. Still open: MIME/S-MIME
signing/encryption (no native module), BTAHL7 assembler fidelity, partner dynamic maps, correlation/atomic semantics.

Build `Mulse.slnx`, then enable **Mulse.Billing** in **Modules**. A scratch native flow can select **Read
CommonBilling field**, choose **TargetLocation** and `billing.TargetLocation`, then **Expand Billing target
filename** using that exact metadata key and a chosen output key. Configure the original server's time zone
and culture explicitly; defaults are UTC/Invariant, not inferred deployment settings. Later delivery must opt in
to the output metadata: these modules do not change destinations or send messages. Renderer selection and
all helper settings use existing graphical controls; no handwritten native-flow JSON is required.

Field reads require `{http://Shs.SpaceTrax2.Interfaces.Core.Schemas.Services.Billing.CommonBilling}Billing`
and the source schema's unqualified ConfigurationInfo/Details children. Thirteen nonsensitive promoted fields
are selectable; credential/certificate fields are excluded. The default **Custom metadata key** mode remains
text-only and backward compatible. Choose **Declared Billing context property** to explicitly seed that field's
CoreProperties/BillingProperties expanded-name key; IsBatch requires a valid XML Boolean and is normalized.
Only the chosen key changes; do not reproject after intentionally changing its context. This opt-in body-to-context
projection lives in `Mulse.Billing`, not in a generic body/context fallback. Missing/duplicate/complex/nil or
oversized fields fail; no defaults, namespace guesses, automatic inlet insertion or BRE fact binding are supplied.

The filename helper supports the source's DATE/DATETIME/TIME/UTC/DAY/GUID/TICKS/SECONDS macros, bounded to
4096 characters. SECONDS retains the 2001-01-01 local-wall-clock epoch. Execution-start snapshot and explicit
zone/culture deliberately replace ambient/repeated clock calls. Invalid known formats throw rather than returning
`INV_FORMAT_` or unresolved known tokens. A durable native augment journals the returned GUID filename; tested
wait/restart continuation retains that result rather than regenerating it. Standalone mapper retry is not replay-safe.

The renderer reproduces namespace-removal semantics without loading the supplied stylesheet/helper assembly.
Text, CDATA, comments and processing instructions survive; colliding attribute local names fail explicitly.
The inspected AddBlockCharacters source returns its input unchanged: no HL7 framing has been invented from its
name. Existing native renderer dispatch/pipelines remain the explicit replacement seam for dynamic transforms;
actual selected partner maps and pipeline assembly behavior are **not** implemented by these modules.

Synthetic verification covers every allowlisted schema field, macro semantics and bounds, real hot-loaded
catalog/mapper invocation, UI controls, dependency protection/unload, and disk-backed native
field-read → GUID filename → wait/restart → namespace-removal execution. No production transport, legacy
helper DLL or real Billing payload is executed. Other import call-site adapters, broader context/schema facts,
broader policy binding, outbound/scoped correlations, remaining maps/pipelines, pattern matching/routing helpers,
throttling and transactions still require implementation. Imported flows stay disabled; full Billing support
and source-oracle parity are not claimed.

Read-only native analysis using the supplied binding/policy exports retains every one of the 192, 196 and
574 source nodes for DoubleBody, Trailer and Service respectively. Their compact review drafts contain 204, 214
and 685 native nodes. Without explicit policy selection they include 105, 104 and 271 blocked nodes, each
with policy proposal evidence. Selecting test target 1.52 reduces those to 103, 102 and 269 blockers and compiles
one ApplyXmlRules call in each flow while retaining every source ID and disabled status.

Both supplied beta host configurations have no appSettings entries or configuration overrides. Selecting either
file plus the supplied GlobalInterfaceAppSettings source as explicit configuration test evidence now translates
the complete initialization statement in every Billing orchestration. SMTP Pattern and FTP Pattern become empty
String literals from the source's missing-key return; commented example regexes are never used. The declared
Excel Pattern getter has the same empty result but is emitted only if referenced. The accompanying source instance
ID assignment becomes a stable native InstanceId expression, not a legacy ID. This is a reusable source-proven
constant-folding feature, not Billing logic added to the core or a redundant Billing module. Both inputs are
selectable in the import UI; pinned literals and InstanceId are graphically authorable from scratch.

With these beta snapshots and policy test target 1.52, the review drafts have 207, 217 and 688 native nodes and
102, 101 and 268 blockers respectively. Every original source node remains present, with two proven configuration
getter assignments and one native instance-ID assignment per flow. No helper assembly or transport was executed.
Synthetic imported initialization/wait/restart/message-write/reply proves configuration is pinned even after the
source/config files are removed, the native identity is unchanged, and source metadata remains isolated.
Deployed helper identity and inherited machine settings are not proven by these snapshots; full flow activation
remains disabled. Pattern parsing/dynamic routing, selected maps/pipelines, correlation and transaction behavior
are still unfinished, not fixed by empty configuration values.

Their translated expressions/targets contain 11, 11 and 21 declared context-property fact uses respectively.
Those counts include unresolved declaration/contract nodes as well as activities and are not executable-path
coverage or a completion percentage. Bound outbound Billing deliveries, correlations, policy facts, actual
maps/pipelines, helper call-site adapters and transactions remain unresolved. No legacy helpers or production
destinations were executed. Generic synthetic fixtures verify supported receive/copy/send/reply flows,
construction/typed writes across disk restart, graphical scratch authoring, and verified-committed reconciliation
through the UI/API without replaying the held effect. This is deliberately not actual Billing parity proof.

### Binding transports (FTP/SFTP/SMTP/FILE) and operational settings

The beta binding export has about 66 send ports (12 nsoftware FTP, 7 nsoftware SFTP, 2 SMTP, 1 FILE, 2 WCF-WSHttp) and 7 receive locations
(2 FILE, 5 WCF-WSHttp). FTP/SFTP send ports now import to the native `ftp-deliver`/`sftp-deliver` modules instead of a repository placeholder.
In the flow UI, supply: host/port/path values for any `%Variable%` binding values (Config), user names and passwords (Secret, never exported by
BizTalk), and the SFTP `hostKeyFingerprint` (the export accepted any host key; Mulse requires a pinned fingerprint or an explicit
`acceptAnyHostKey`). Set `privateKey` if SFTP key authentication was used and review `encryptionMode`/`acceptAnyCertificate` for FTP.
Backup (secondary) transports, ordered delivery, stop-on-failure, service windows, port maps and pipeline component properties are not
imported; each is reported as a draft warning with its UI equivalent (see "Transport and operational binding increment" in
`BizTalk-Import-Compatibility-Plan.md`).

## Native conditionals, Try and Scope import (latest)

Expression shapes containing `if (...) { ... } else { ... }` now import as native `If` nodes with an Otherwise branch, editable in the flow editor. A BizTalk `Scope` becomes a native `Sequence`; a Scope with exactly one `System.Exception` catch becomes a native `Try` node (durable flows) whose body, catch handler and exception message/details variables are all editable in the UI. In the handler, `ex.Message` and `ex.StackTrace` read those variables.

Review these deliberate limits before enabling any imported flow:

- Transactions (`LongRunningTransaction`, `AtomicTransaction`, `TransactionAttribute` batch/retry/timeout/isolation) are kept as named "(not enforced)" evidence nodes. No atomicity, compensation, retry or timeout is applied.
- `Try` catches faults raised by its body only. It does not catch `Terminate`, failed Deliver/Augment/Render effects, persistence faults, cancellation, or faults that cross a ForEach, Receive or Wait.
- `StackTrace` is the exception type name plus message, not a CLR stack trace.
- Typed catches, several catches, fault-message catches and assignments to `ex.*` stay Blocked.
- Correlation sets (`CorrelationDeclaration`/`StatementRef`) and receive filters remain Blocked nodes; correlation must be configured by hand.

Re-measured against the Billing sample (stand-in config helper, so indicative): BillingServiceProcess unresolved nodes 136 -> 126, BatchDoubleBody 54 -> 46, BatchWithTrailer 60 -> 48. Imported flows stay disabled and no BizTalk equivalence is claimed.

## Native string functions

The native expression editor now offers `Length`, `IndexOf`, `LastIndexOf`, `Substring` (with or without length), `RegexIsMatch`, `ToInt32` and `ToText`, each with labeled operands and hints. The importer translates `string.Length/IndexOf/LastIndexOf/Substring`, `Regex.IsMatch` and `Convert.ToInt32/ToString` for Billing-style address parsing only when operand types are proven; anything else stays a visible blocker. Review two deliberate differences: `IndexOf`/`LastIndexOf` search ordinally (BizTalk's default is culture-sensitive; only printable-ASCII constant search text is imported), and `Substring` fails the flow instead of clamping. With bindings, schemas, rules and a host configuration supplied, BillingServiceProcess unresolved nodes fell from 164 to 136. `GlobalInterfaceAppSettings.GetSMTPPattern/GetFTPPattern` need `NativeHostConfigurationPath` and the helper source path; `if` blocks inside expression shapes, `FilenameMaker.MakeFilename` and dynamic port `Address` assignments are the next gaps.

## Pipelines and ODX audit (2026-11-05)

- Send-pipeline components (BTAHL7 assembler, SetPropertyFileName) are imported as editable steps. When send ports use different pipelines, each step is scoped to its route with a `targetRoute` condition; populate `targetRoute` per payload as the draft warns. If a binding has no pipeline data, defaults come from the matching `.btp` file (secret-named properties are dropped). Receive pipelines are evidence only.
- ODX audit of the three Billing orchestrations: every shape is retained as evidence, but behavioral equivalence is not achieved. The draft path turns simple decisions into metadata tags only and does not run branch bodies. The opt-in native path reports fail-closed `BT-NATIVE-UNRESOLVED` blockers (128 / 128 / 338). See "ODX audit" in `BizTalk-Import-Compatibility-Plan.md` for the breakdown and remaining work.

## Decision

- **Importable as a disabled analysis/draft:** Yes. The current importer accepts an `.odx` path when exactly one sibling `.btproj` explicitly includes that file. Latest verification: 876 related tests pass, the full solution build succeeds, and actual Billing assertions pass against both supplied beta host snapshots. The earlier core-only analysis reported 19 BizTalk projects, 17 custom projects, 20 candidates, all three Billing orchestration candidates, and 574 designer elements for `BillingServiceProcess.odx`.
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

`BillingServiceProcess.odx` contains a `CallRules` shape named **Retrieve Map**, with policy name `Billing Processing Policy` and policy version `-1`. No `.rules` policy export was found inside the local `Shs.SpaceTrax2.Interfaces` source tree, but the separately supplied policy/vocabulary export is available (47 versions, 1.6-1.52). Explicitly selecting 1.52 as the native migration test target now compiles the call with exact schema-proven named-message fact binding. All 40 active rules have synthetic native execution coverage. The source metadata does not establish what `-1` resolves to in the deployed BRE environment; deployed-version and end-to-end business parity remain unverified, not the availability of the supplied export or this native test-target binding.

Message assignments dynamically obtain map types using `System.Type.GetType` and invoke BizTalk `transform` operations. The selection fields include `msgBre.HeaderMapName`, `msgBre.PreprocessingMapName`, `msgBre.MapName`, and `msgBre.TrailerMapName`; the main orchestration contains 29 dynamic transform call sites. Billing’s `.btproj` does not include `.btm` files. The supplied policy export is available for reviewing selected map names, but export availability does not implement those maps or establish deployed-version parity. The migration report lists `.btm` files found across the analyzed source scope, including maps owned by separate projects, with schema references and analyzer counts. This helps inspect candidates, but nearby maps are not safe substitutes for confirmed deployed selections. The existing map analyzer can produce limited starter XSLT for some direct links/constants, but does not translate script/functoid behavior or wire a complete map into this flow.

The earlier core-only analysis indexed nine workspace maps, including the seven PartnerMaps files. Include the sibling `Shs.SpaceTrax2.Interfaces.Partners` workspace in Additional sources to discover the actual partner map projects as well. Each source/target reference shows schema class/file candidates based on explicit Namespace/TypeName metadata or labeled literal naming conventions, with case-sensitive matching and explicit missing/ambiguous/incomplete states. This is source evidence, not evaluated build/deployed identity or policy selection.

Two concrete findings help avoid false ownership assumptions:

- The header target `BTAHL7Schemas.MSH_25_GLO_DEF` is explicitly declared by `Shs.SpaceTrax2.InterFaces.Common.HL7Schemas\Shs.SpaceTrax2.Interfaces.Common.HL7Schemas.btproj`, at `2.X\MSH_25_GLO_DEF.xsd`. Its project Namespace/TypeName metadata explains why filename-derived class lookup was insufficient.
- `Shs.SpaceTrax2.Interfaces.Core.Custom\Schemas\PartnerSchemas\TestSchema.xsd` and `TstTrailerSchema.xsd` exist, but that Custom `Schemas.btproj` includes only two PropertySchemas entries and no PartnerSchemas entries. The trailer's source/target type ownership therefore remains unresolved. Confirm the intended build/source history before claiming these disk files are the deployed schema classes; Mulse does not silently add them to the BizTalk project.

No Billing-specific binding export is in the Billing project folder. The wider local source root contains two binding-info filenames for PatientRegistration, not Billing; their contents were not opened. The separately supplied Billing binding export is now available to the importer for endpoint, subscription, transport and retry evidence; native adaptation and verification remain necessary. Do not paste unredacted secrets into tickets or this guide.

### Helpers and pipeline behavior

The ODX calls `FilenameMaker.MakeFilename` from `Shs.SpaceTrax2.Interfaces.Common.FilenameMaker\FilenameMaker.cs` and `GlobalInterfaceAppSettings.GetFTPPattern` / `GetSMTPPattern` from `GlobalInterfaceAppSettings\Shs.SpaceTrax2.Interfaces.Common.GlobalInterfaceAppSettings.cs`. The send-pipeline source also references the local `Shs.SpaceTrax2.InterFaces.Core.CPC.HL7TypeCaster\HL7TypeCaster.cs` and `Shs.SpaceTrax2.Interfaces.Core.CPC.PS.SetProp\SetProperty.cs`. These source files are concrete dependencies, not portable .NET Framework assemblies. The pure configuration getters now have the explicit source/snapshot translation described above; this does not translate arbitrary helper or pipeline behavior. Other helpers still need native call-site adapters and tested module behavior. Both supplied beta snapshots were inspected read-only and contain no appSettings entries; no deployed assembly or inherited machine setting was read.

The orchestration references Billing pipeline names from the local Core.Pipelines project. For example, `SendBTAHL7.btp` contains the local `HL7TypeCaster` component followed by the Microsoft BTAHL7 2.X Assembler; the email variant adds MIME/SMIME encoding. XML-email pipelines include PropertySetter stages and MIME/SMIME encoding; flat-file routes use the Flat File Assembler, and email variants also add MIME/SMIME encoding. These stages can affect serialization, context, HL7 framing, and delivery. The import report explicitly classifies pipeline behavior as untranslated; it does not reproduce those component effects.

A read-only check of all 32 local `.btp` files found 16 Receive / 16 Send pipelines and 63 components, without inventory diagnostics. This is workspace-wide inventory, not Billing's deployed selection. Fresh migration reports retain stage/component order, category IDs, class/version, source lines and property names only. `SendBTAHL7` shows PreAssemble (HL7TypeCaster), Assemble (BTAHL7 assembler), then an empty Encode stage. UTF-16 BOM/source lines were handled. No property values, descriptions or cached display metadata are copied into reports; inspect original configuration securely. Unknown/mixed categories, invalid identities, parsing/resource limits and omitted evidence remain explicit blockers.

### Ports, message context, batching, and failures

The main orchestration has one request Receive (`Submit`) on `prtBillingService` and a response Send (`msgServiceResult`) on that same service port. It also has 25 Sends across multiple delivery choices, including the direct `prtBillingBatchDirect` handoff and HL7 direct/message-box routes. Its metadata declares 18 ports: 6 physical binding attributes, 9 logical binding attributes, and 3 direct binding attributes. The physical attributes are marked dynamic and expose transport/pipeline/URI fields; their URI values are intentionally not reproduced here. The ODX also writes transport and HL7 context properties (including credential-related properties from message configuration). Preserve these as protected configuration and message metadata, not literals in code or tests.

The two batch orchestrations are separate BizTalk orchestrations, not nested calls found in the main ODX. `BillingServiceProcess` sends a message to the direct `prtBillingBatchDirect` port; the batch ODXs receive first/next requisitions on their own direct ports. `BillingBatchDoubleBodyProcess` and `BillingBatchWithTrailerProcess` each have a `Batch Loop`, two Receive shapes, a BRE call, and route-specific Sends; the trailer variant has an additional trailer assignment scope and a second atomic transaction. The main service has six named debatching/processing loops and corresponding scopes. The importer produces separate candidates, but does not recreate the subscription handoff, batch membership, order, trailer/body rules, or per-item failure/retry behavior. Design and test the inter-flow handoff explicitly; do not silently drop either batch variant or assume a direct MessageBox subscription is an ordinary endpoint.

The main process declares correlation sets/types, including batch/service-result/HL7 correlations; declaration counts alone do not prove which correlation is initialized, promoted, or active at each send/receive. It also contains a Delay, Catch, nested Scopes, and an AtomicTransaction. The importer does not preserve wait timing, restart/replay semantics, atomic boundaries, compensation, exceptions, or the exact request/reply failure contract. The simple decision analyzer may translate a narrow set of branch conditions into decision tags, but it does not perform each branch’s side effects.

## What can be done now in Mulse

1. Open **Designer → Import from BizTalk**.
2. Set **Source path** to the local `BillingServiceProcess.odx`. The file must be explicitly included by exactly one sibling `.btproj`; this file is. If ownership is ambiguous or missing, select the owning `.btproj` or solution instead.
3. Set **Additional solutions/projects** to the local `Shs.SpaceTrax2.Interfaces` source root so the importer can inventory sibling schema, pipeline, and helper projects. This makes source available for analysis, but does not resolve the four `Build`-path references described above.
4. Supply the already-provided Billing binding export under **Additional binding export paths** and the supplied policy export under **Rule policy XML exports**. Do not substitute the unrelated PatientRegistration exports.
5. Click **Analyze source**. Review the dependency report, all three Billing candidates, each blocking diagnostic, the shape inventory/source-line references, the workspace map inventory, and the warnings. Confirm whether sibling project references are resolved or unresolved; inspect each map's owning project and schema references, then ask the BizTalk team to confirm which maps the deployed policy actually selects. Do not treat candidates as runnable workflows.
6. Use **Load onto canvas** only to inspect/edit the disabled draft. Expand each pipeline artifact's **Pipeline source inventory** in its Migration report to inspect declared stage/component order, class/version, source lines, property names and any omitted counts. Saved drafts retain the import-time snapshot; reanalyze for current evidence.
7. Use each candidate's **Scaffolded modules > Download / Download all (.zip)** for starter artifacts. Downloaded orchestration and pipeline C# modules are labeled **unimplemented migration stubs**, honor cancellation and throw `NotSupportedException`; they do not pass through, implement Billing logic or lift the execution gate. Pipeline starters contain names-only evidence and are not installed/wired. Choose the correct native parse/augment/render stages before implementing components, configuration, ordering and context effects. Starter XSLT may still be incomplete. A successful build confirms scaffold shape only, not translated behavior.
8. Save only as a disabled review artifact. Do not edit a `FullyTranslated` status, remove the report, manually enable the flow, or route production messages around the fail-closed guard.

### Native map work available without the BizTalk team

The Mapping Designer includes XSD schema trees, conservative `.btm` proposals, explicit missing-value policies, ordered native string operations and persisted runtime XSD validation. These tools are available for both scratch-built workflows and disabled imported drafts. They do not prove deployed-version/fact parity for the supplied policy or make Billing runnable.

1. In **Flow Designer > Mapping**, add/select an **XML mapping render** node using the render-node selector. Mapping is a full-workspace tab beside **Flow**, not a floating window. **Edit XML mapping** in Flow Properties opens that node directly. For a new render route, return to Flow and add a deliver node before saving; do not connect it to a production destination. Switching tabs preserves transient inputs/previews but does not save them. Descriptions are available via **?** help (hover, keyboard focus or click; Escape dismisses); errors and execution blocks remain visible. Advanced map/field settings and **Test mapping** are collapsible.
2. Open **XSD schema trees and BizTalk map import**. Paste/upload the exact source and target `.xsd` files, including any explicitly supplied import dependencies. **Inspect source schema tree / Inspect target schema tree** shows expanded-name paths, namespaces, cardinality and unsupported shapes. Select scalar fields to add editable links; no arbitrary XPath is required.
3. For the locally verified trailer candidate, select `Maps\PartnerMaps\TestSchema_ADtA31Trailer.btm`, source `Schemas\PartnerSchemas\TestSchema.xsd`, and target `Schemas\PartnerSchemas\TstTrailerSchema.xsd`, relative to `Shs.SpaceTrax2.Interfaces.Core.Custom`. Choose **Create native map proposal**. A local read-only check generated this proposal; that does not prove Billing selects it or that its output is equivalent.
4. Inspect the proposed JSON/XSLT and warnings. Only **Replace this node's mapping with proposal** installs it. Unsupported maps fail as a whole and preserve the existing node, rather than silently dropping functoids or scripts. The source root and both exact XSD texts are retained in a version-2 definition.
5. For manual maps, select **Use supplied XSDs for runtime validation** explicitly to attach schemas. Simply uploading/inspecting does not attach them. Input and output validation run in preview and actual rendering; wrong namespaces, missing required data and invalid datatypes fail before output is returned. External schema files/URLs are never fetched; supply import dependencies as text/files, and do not expect XSD includes/redefine support.
6. Use **Error / Omit / Default** for missing scalar source values. Omit suppresses only that assigned field, not arbitrary parent records; Default inserts a literal only for absence. A present empty value stays empty. Repetition, complex values and arbitrary conditions/functions still require explicit translation.
7. For explicitly understood scalar behavior, use **Add string operation** and its ordered edit/move/remove controls. NormalizeWhitespace trims/collapses only XML spaces/tabs/line breaks; UppercaseAscii/LowercaseAscii leave non-ASCII letters unchanged; Prefix/Suffix take literal text, not XPath. Operations apply after source/constant/default selection; Omit stays omitted. Maximum 16 operations per field and 4,096 characters per prefix/suffix argument. Adding an operation upgrades to version 2. These native tools do not infer or automatically translate any BizTalk functoid ID.
8. Preview only schema-valid synthetic input and compare against trusted expected output. Save/reopen retains the node, policies, operations, source-root assertion and validation schemas. All report-bearing imported flows remain disabled; preview/proposal success is not execution approval.

The seven local PartnerMaps files were checked without executing source or scripts: the trailer generated a native proposal from explicitly supplied schemas; five other maps were rejected for functoid constructs; the header contains two scripts and has a declared target-schema file candidate as described above. The preprocessing maps contain one script each, for four script bodies total. These findings do not establish deployed policy selection. Starter XSLT remains separate and is not automatically substituted into Billing.

**Visual authoring:** the widened Mapping Designer now shows source paths on the left, target paths on the right, and saved connections/ordered operation nodes between them. After inspecting both XSD trees, select one supported scalar field on each side and choose **Connect fields**. Select a connection or operation node to edit that field's existing settings below the canvas, add operations, reorder/remove them, or remove the connection. **Show all field settings** restores the full form editor. Constants and manually entered mappings also appear. Duplicate/overlapping targets and incompatible roots are rejected without changing the map; namespace aliases are matched by URI. Optional-to-optional links start with Omit. Repeated/unsupported fields remain visible but disabled; this does not add repeated-record translation. Links use click/keyboard selection, not drag gestures; layout is automatic. Attached schemas reload on reopen, while inspected-but-unattached schemas are labeled separately. Save normally to persist; no separate graph file or migration approval is created.

In a fresh **Analyze source** result, expand **Maps found in analyzed source** and inspect source/target schema evidence and catalog warnings. Do not replace unresolved classes with nearby filenames. Previously saved reports are unchanged import-time snapshots; saving an existing draft does not recompute them.

**Latest portable verification:** 612 Service.Tests cases passed with zero failures/skips, including visual graph rendering/callbacks, namespace aliases, disabled repeated fields, duplicate/overlap/root/limit guards, schema reload/retry/stale-result handling and visual-created connections surviving both scratch/imported save/reopen with actual output checks. Workspace switching retains component instances, schema/sample inputs, previews and toolbox position; contextual help association and dismissal are covered. Earlier pipeline, API, render and migration-gate regression coverage remains. UI verification is component rendering/callback testing, not browser gesture or pixel-layout acceptance. This is synthetic/component-level verification, not Billing business-output or production integration acceptance.

## Work required before a complete migration

### Prioritized developer work

1. **Resolve dependency ownership safely.** Add a user-selectable, persisted exact project-path assertion for unresolved references (or align build outputs and HintPaths with verifiable literal paths). Keep ambiguous/missing paths blocking; do not fall back to assembly-name guessing. This is a recommended improvement, not an available feature.
2. **Wire and verify the supplied BRE policy and maps.** Pin the relevant approved policy/version, input/output vocabulary, all map class names selected by the policy, and their source `.btm`/XSLT. Implement decision/map selection as native code and translate every required map/function/functoid, including headers, preprocessing, body, trailer, and multi-input transforms.
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
