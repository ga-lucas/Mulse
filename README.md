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
| `Mulse.Excel` | Hot-loadable XLS/XLSX worksheet-to-JSON parser and JSON-to-workbook renderer; no installed Excel required. |
| `Mulse.SamplePlugin` | Reference/example hot-loadable plugin project - the template to follow when authoring a new module package. |
| `Mulse.AppHost` | .NET Aspire orchestration host for local multi-project run/debug. |
| `Mulse.ServiceDefaults` | Shared Aspire service-defaults (telemetry, health checks, service discovery). |
| `Service.Tests` | xUnit tests for the service and flow-list filtering (runtime, persistence, module catalog, BizTalk import/analyzers, etc.). |

## Module extensibility

Modules implement one of the core interfaces in `Mulse.Modules` and are registered via an `IModuleInstaller`.
Two ways to ship a module:

- **Built-in**: referenced directly by `Service` (compiled into the host process). Simple, but can't be
  reloaded/unloaded at runtime.
- **Hot-loadable plugin**: built into `Service/plugins/<PackageName>/` (see `Mulse.SamplePlugin`,
  `Mulse.Hl7`, `Mulse.Dbms.SqlServer` for the pattern) and picked up automatically by `ModuleCatalog`'s
  plugin discovery at startup and every five seconds. New compatible packages appear **unloaded**;
  activate them from **Configuration > Module Packages**. Loading uses a collectible `AssemblyLoadContext`
  so packages can later be reloaded or unloaded without restarting the service.

## File system delivery

The **File system deliver** module (`file-system-deliver`) exposes these options in the designer:

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

Use existing `file-fetch` / `file-deliver` modules for filesystem access:

- Read: `file-fetch` -> `excel-parse` -> downstream processing.
- Write: JSON rows -> `excel-render` -> `file-deliver`.

Both modules accept `sheetName` or a **1-based** `sheetNumber` (1-256, default `1`). A supplied name
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

## UI workspace

The admin UI menu links to the dashboard and groups Config values, Modules, and Module Packages
under Configuration. Open the flow designer using create/configure links from the dashboard rather
than a menu entry. `/modules` hosts the searchable module catalog with kind and source-package filters.
Each module links to its owning package and shows its source kind and assembly path. `/module-packages`
lists the available modules in each loaded package, collapsed by default under **Show/hide modules**,
with links back to a package-filtered catalog and load/unload/reload controls. Unloaded packages do not
execute code to enumerate module details; load one explicitly to inspect its modules.

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
These checks apply to PATCH, PUT/POST with `enabled: false`, and package DELETE, without changing
registrations on rejection. Its disabled registration survives restarts and prevents auto-discovery
from reloading the same package ID. **Load** re-enables it; **Reload** refreshes a loaded package after
its DLL changes. Built-in packages cannot be unloaded or replaced.

API clients can `PATCH /api/module-packages/{packageId}/enabled` with `{ "enabled": true }`, or
`{ "enabled": false, "confirmation": "UNLOAD PACKAGE" }` to unload.

Appearance follows your system's light/dark preference until you use **Dark mode** in the navigation.
Your selection is saved in this browser and applies to all pages, including the designer canvas.
No external fonts, styles, or theme services are required.

On smaller screens, designer tools stack around the canvas instead of floating over it. Tables keep
their column headers and scroll horizontally inside their panels. Desktop designer panels remain
draggable and collapsible, retaining their dragged positions during canvas interactions for as long
as each panel remains open on the page. Properties starts aligned to the right edge of the designer
viewport, then stays where you drag it.

UI regression tests can be run with Node.js (no npm dependencies):

```powershell
node --test Mulse.Ui.Tests\theme.test.cjs Mulse.Ui.Tests\floating-panel.test.cjs
```

## Status

This is an in-development project (not yet deployed to production). Expect breaking changes as the
architecture evolves.
