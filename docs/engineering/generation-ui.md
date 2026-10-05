# Generation in the editor: transparent and configurable (design)

Status: binding design, mostly built; the Status column of section 8 says what is built and what is left. It answers the owner's complaint that generation is "a complete black box": what a pack does, which templates it runs, where it writes, one file per element or many items in one file, and why a plan re-renders what it does. It builds on SPEC §12, §14 to §16 and §18, on `engine-design.md` §2.4, §2.5, §8, §9, §11 and §12 (whose names it uses; a worker who implements a step edits that file in the same commit), on `explorer-redesign.md` (rail, tree, keyboard) and on `phase2-design.md` §4.8 (the Generate workspace). Spec additions are errata E18 to E23.

Three principles bind every choice. **(P1) The engine models intent; templates decide persistence**: nothing here makes the engine synthesize an output the pack did not ask for. **(P2) The product reserves no vocabulary**: unit ids, parameter names, selector names and file names are the pack's; the editor derives file roles with the engine's own rules, never from a naming convention: unit and companion templates are the files `pack.json` names, partials are the files a template `include`s, scripts are the `scripts` list or, when that list is empty, every `*.js` file the pack loader loads, and type maps are the `types/*.json` files the loader reads. One function, shared with `PackLoader`, derives them (GU2). **(P3) The files are the truth**: a pack is `.maquettiste/templates/<pack>/`, which the CLI, the editor and git read the same way. The editor edits those files in place, never a copy or a database row, and an edit made outside the editor (git checkout, a text editor, the CLI) shows up in the editor within one watcher tick.

The owner's scenario sizes the design: a monorepo with `.maquettiste` at the root, several custom packs and dozens of `outputs.allow` roots, shown to a partner team. Everything here must read at that size without a manual.

## 0. What is wrong today

- The Generate sidebar lists pack names and "enabled"; nothing says what a pack contains. Units, scopes, templates and output patterns live only in `pack.json`, and the output pattern is a Scriban expression nobody reads aloud.
- There is no way to add or edit a template, a unit or a parameter in the editor; `pack new` exists only on the command line.
- `POST /api/templates/preview` renders a saved unit for one element, used only by the Database screen; it cannot render unsaved text, and it resolves the whole model on every call.
- The Generate table shows files and kinds (added, modified, deleted) only. The plan already carries each change's `pack` and `unitKey` and each unit's `readKeys` (the planner records them, §11, and decides the skip from them), but the table does not show the unit, the template or why a unit re-rendered.
- The manifest knows which unit wrote which file, but no screen shows it.

## 1. The model of generation, in the words the UI uses

| Term | Meaning | Where it lives |
| --- | --- | --- |
| Pack | A folder of templates and one `pack.json`; generation runs every enabled pack | `.maquettiste/templates/<pack>/` |
| Unit | One line of the pack's work list: which elements, which template, which output path | `pack.json` `units[]` |
| Scope (`for`) | Which elements the unit runs for: once, or once per element of a kind, or once per element a selector returns | `units[].for` |
| Filter (`where`) | Narrows the scope by tags, stereotypes, categories, packages and their negations (not tags, not stereotypes, not packages), database, abstract, or a script filter | `units[].where` |
| Template | The Scriban file the unit renders; it may `include` partials and call helpers | a pack file |
| Output path | A pattern rendered with the same variables as the template; the file lands at `<output base>/<path>` | `units[].output` |
| Output base | The pack's folder prefix in this project | `maquettiste.json` `packs.<name>.output` |
| Output root | An `outputs.allow` folder; every file must land under one; committed or built *(superseded 2026-10-02 by spec-errata E42: no root kinds; an entry may also name a single file)* | `maquettiste.json` `outputs.allow` |
| Write mode | Overwrite; Create only if missing (`once`); Protected regions (`regions`); Pair (`pair`) | `units[].mode` |
| Parameters | Pack settings the templates read as `pack.params`; defaults in `pack.json`, values per project | `pack.json` `parameters`; `packs.<name>.parameters` |

**Files per unit (the owner's "one file per, or a bunch of items into a single file").** A unit whose scope is `each <kind>` writes one file per element; a unit whose scope is `model` writes one file for the whole model, the template looping over what it needs; `each package` and `each database` (E19) write one file per package or database that gathers its items; `select <name>` writes one file per element the selector returns. Any template may also write extra files with `{{ file "path" content }}`; they carry the unit's mode and appear under the unit in Outputs. The grid calls the choice **Files**: "Each <kind>" or "Once" (with a group: model, package, database). The write mode `once` is labelled **Create only if missing** everywhere, so "once" never means two things on one screen (GU3).

**What `sql-ddl` does, as the explorer shows it** (the path summary of §2.2):

```
table      each table      → table.scriban      → <database>/[<schema>/]tables/<table>.sql
schema     select databases → schema.scriban    → <database>/schema.sql
migration  select databases → migration.scriban → (files the template writes)       Create only if missing
seed       select databases → seed.scriban      → <database>/seed.sql               Protected regions
```

## 2. The Generate explorer (rail)

### 2.1 Tree

The Generate explorer replaces today's pack list with a tree, built with the explorer's tree model and virtualization (`explorer/tree.ts`, explorer-redesign §4.3), with the header "Generate · 4 packs · 61 units · 12,480 files".

```
▾ sql-ddl                         4 units · 3 roots        ⋯
  ▾ Units
      table · each table → table.scriban → <database>/[<schema>/]tables/<table>.sql      412
      schema · select databases → schema.scriban → <database>/schema.sql                    3
  ▸ Templates                     10 files
  ▸ Parameters                    5 · 2 set
  ▾ Outputs                       last run 2026-09-29 14:02
      ▾ table                     412 files · 1 hand-edited
          db/main/billing/tables/customers.sql                       hand-edited
      ▸ seed                      3 files
▸ csharp-dapper                   off
```

- **Pack node**: name, unit count, the number of distinct output roots its last run wrote, "off" when disabled, a warning icon with a count when the pack has diagnostics (MQ6001 to MQ6003, MQ6019 to MQ6025). Enter opens the pack editor (§3).
- **Units**: one row per unit in `pack.json` order, reading `id · scope → template → path summary`, then the planned file count (the number of elements in scope after the filter, from the last paths call, §5.2) and the mode when it is not Overwrite. A row with a filter shows a funnel icon whose tooltip lists the filter in words ("tags: api; not abstract"). Enter opens the Units tab on that row; the context menu has Open template, Preview…, Explain…, Duplicate, Delete.
- **Templates**: the pack folder as a file tree; each file carries its derived role (unit template, companion, partial, script, type map, other; derived as P2 says) and the units that use it. Enter opens it in the Templates tab.
- **Parameters**: one row per parameter, `name = value` with "default" or "set" beside it. Enter opens the Parameters tab on it.
- **Outputs**: the pack's manifest entries (committed and built manifests, §12.2; *since spec-errata E42 one manifest per pack, and a mode `block` entry*) grouped by unit, then by file; a unit group shows its file count and the counts of states other than clean. File states: **clean**, **hand-edited** (disk bytes differ from the manifest hash, skeleton for regions), **missing**, **owned** (`o:` entries, never checked), **orphan** (its unit id is no longer in `pack.json`, or its element id is no longer in the index; a plan's Deleted changes remain the complete answer). Enter on a file opens its diff (§3.5); the context menu has Reveal in explorer (the element), Open template, Explain.
- **New pack…** in the explorer header menu and the palette: a dialog with name (`^[a-z][a-z0-9-]*$`, unique) and **Start from**: **Empty pack** (one `each entity` unit, its template and a script file, as `pack new --from empty`) or **Copy of** a starter (`sql-ddl`, `csharp-dapper`, copied under the new name), with a line under the choice saying what it gives ("One each-entity unit and its template, ready to edit." or "All 9 files of sql-ddl, renamed to <name>."). It calls `POST /api/packs` (§5.1), then opens the new pack's editor on Units. The dialog says what gets written: "Creates .maquettiste/templates/<name>/ with 3 files".

### 2.2 The path summary

The summary turns an output pattern into placeholders without rendering: the pattern is parsed with the pack's delimiters; literal text stays; a code span that is a member path becomes `<name>` where `name` is the last member, except that a final `name` takes its owner's name (`table.database.name` → `<database>`, `table.name` → `<table>`, `table.schema` → `<schema>`); a helper call keeps its argument's placeholder (`kebab database.name` → `<database>`); an `if … end` body becomes `[…]`. Anything else (a loop, arithmetic, a string function the summary does not know) shows the raw pattern in monospace. A unit without `output` shows "(files the template writes)". The summary is pure and computed in the editor (`generate/pathSummary.ts`); the CLI's `pack show` prints the same text through a C# port that shares its test table.

## 3. The pack editor screen

Opening a pack brings the **pack editor** to the centre: a header (`sql-ddl 1.0.0`, description, engine range, Enabled switch, output base, the project's hand-edit policy shown read-only with a link to Settings because it is project-wide, the pack's diagnostics count, then **Rename pack…** and **Remove pack…**) and four tabs: **Units**, **Parameters**, **Templates**, **Outputs**. `PUT /api/project/settings` is admin-only (`saveSettings`), while plan and apply are maintainer, so the Enabled switch, the output base and the parameter values save through a narrow `PUT /api/project/settings/packs/{pack}` (§5.1) at maintainer role, which writes only `packs.<pack>` in `maquettiste.json` with the settings ETag; `outputs.allow` and every other section stay admin. The tabs write the pack's own files (§5.1). Unsaved changes are per tab and per file; leaving the screen with unsaved changes asks once.

### 3.1 Units

A grid, one row per unit, edited in place with the Rows grid key map of `reference-types-seeds-localization.md` §4.4. Columns:

| Column | Edits | Help (shown in the side panel for the focused row) |
| --- | --- | --- |
| Id | text, `^[a-z][a-z0-9-]*$`, unique | "Names the unit in plans, manifests and file headers. Renaming it re-renders the unit and orphans its old files' manifest entries, which the next apply adopts or deletes." |
| Files | Each <kind> / Once (model, package, database) / Selector | see the scope table below |
| Scope | the `for` value: kind list, or `select` with a picker over the selectors the pack's scripts register | as below |
| Filter | popover form over `where`: pickers for tags, not tags, stereotypes, not stereotypes, categories, packages and not packages from the index, database picker, abstract tri-state, script filter picker from the registrations | "Only elements that match every set filter; a list matches any of its values; a not-list excludes any of its values." |
| Template | picker over the pack's files, or New template… (creates `<id>.scriban` with a starter body for the scope) | "The file this unit renders. Partials it includes are listed in Templates." |
| Output path | text with Scriban tokens highlighted and variable completion; the path summary under it; the example path under that | "Where the file lands, under the output base (`db/`) and an output root. Leave empty when the template writes its files with `file`." |
| Write | Overwrite / Create only if missing / Protected regions / Pair (companion template and output appear when chosen) | one line per mode |
| Formatter | by extension / none / a configured formatter | "Runs after rendering; configured in Settings › Formatters." |
| Delimiters (side panel) | the unit's `delimiters` (open and close text), empty for `{{ }}` | "Use other code-span markers when the output language uses `{{`." |
| Transforms (side panel) | the unit's `transforms`: an ordered picker over the transform registrations of the pack's scripts | "Run on the rendered text, in order, before the formatter." |

The grid edits the `pack.json` document it loaded, not a projection of it: a member it does not show (a future one, or `x-` members) is kept as loaded, and Save sends the whole document.

Scope help, in plain language (the grid's side panel and the Scope picker show it):

| Scope | Help |
| --- | --- |
| `model` | Runs once. The template sees the whole model: use it for one file that lists many things, or to write several files with `file`. |
| `each entity`, `each relation`, `each enum`, `each value object`, `each reference type`, `each seed` | Runs once per element of that kind; one file each. |
| `each package` | Runs once per package (domain folder); one file per package gathering its elements. |
| `each database` (E19) | Runs once per database; one file per database gathering its tables. |
| `each table` | Runs once per table of every database: the tables the databases design and the tables their mappings resolve. |
| `each view` | Runs once per view of every database; one file each. |
| `each sequence` | Runs once per sequence of every database, the ones the resolver creates for keys included; one file each. |
| `each routine`, `each database type`, `each sql object` (added 2026-10-01) | Runs once per routine, database type or SQL object of every database; one file each. |
| `each locale` | Runs once per declared language; no element, no filter. |
| `each process`, `each actor`, `each scenario` (phase-3-design.md 7.1) | Runs once per process, actor or scenario; one file each. |
| `select <name>` | Runs once per element that the selector `<name>`, registered by the pack's scripts, returns. |

**Example path.** The grid toolbar holds an **Example element** picker per scope kind (defaulting to the last chosen, else the first element in scope order). Each row shows the planned file count (the unit's scope listing, rendered for nothing) and, for the **selected row only**, its output path rendered for that one element (`db/main/billing/tables/customers.sql`); a unit whose one render covers the whole model, a database or a locale (`wide`, §5.2) shows no example (it is previewed on Templates when asked). A pattern with no code span is the same path for every element, so the listing reports MQ6020 without rendering; two of the elements asked for that render the same path report it with both element names. Rows update from `POST /api/templates/paths` (§5.2), debounced 250 ms after an edit, against the unsaved row (which needs maintainer, §5.1; a viewer sees the saved rows' paths). A path outside every output root shows MQ6019 in the cell. When a pattern reads a variable only the template assigns, the count and collisions are not available and the cell says "Path depends on the template; shown for the example element only", with the example path taken from a full preview render. Save sends the whole `pack.json` document to `PUT /api/packs/{pack}` with `If-Match`, which writes it in canonical form; a 409 reloads the rows and replays the unsaved edits on top when they touch other units, and otherwise shows both versions of the row to choose from.

### 3.2 Parameters

A form built from the pack's **parameter schema** (E18): `pack.json` gains an optional `parameterSchema` with `properties` (a JSON Schema properties object, the subset extension schemas accept) and `required`. Defaults stay in `parameters`. Without a schema, the form infers the control from the default: boolean → switch, string → text, number → number, array or object → a JSON editor (Monaco, validated). A property with `enum` becomes a select; `title` and `description` label and explain it. Each row shows the default, the project's value from `packs.<name>.parameters`, and **Reset to default** (removes the key). Values save through `PUT /api/project/settings/packs/{pack}` with the settings ETag (maintainer; §3). A value that fails the schema is refused in the form and reported as MQ6023 when found in the file; a project value for a parameter the pack does not declare shows MQ6024 with Remove. A pack author edits the schema and defaults on the same tab (**Edit definitions**, a JSON editor over the two members, saved with `pack.json`'s ETag).

### 3.3 Templates

Three panes, resizable, each collapsible: **file tree** (left), **template editor** (centre), **preview** (right, or below at narrow widths).

- **File tree** over `.maquettiste/templates/<pack>/` from `GET /api/packs/{pack}/files`, with roles and "used by" (units that name the file, templates that `include` it, found by parsing). Actions: New file, New folder, Rename (refused while a unit names the file unless "Update units" is ticked, which rewrites `pack.json` in the same request; refused while another template `include`s it, listing the includers, since this version does not rewrite template text), Delete (refused while named or included, listing where). `pack.json` itself is edited only through the Units and Parameters tabs, never as a raw file. Any file name and extension is allowed, since a unit's `template` and an `include` may name any file (the engine does not restrict them); a file must be UTF-8 text (no NUL byte) and at most 1 MB.
- **Template editor**: Monaco through the existing `code/CodeEditor.tsx`, with a Monarch tokenizer for Scriban (`code/scriban.ts`): code spans `{{ }}` and whitespace-trimming `{{~ ~}}`, the pack's custom delimiters when a unit declares them, keywords (`if else end for in while func ret capture with include`), strings, numbers, comments; text outside code spans is embedded as the language of the unit's output extension when Monaco ships one (`.sql`, `.cs`, `.ts`, `.json`, `.md`, `.yaml`), else plain text. Completion lists the scope's variables (`model`, `element`, the scope alias, `pack.params.*`, `mapping`, `hints`, `data`, `unit`), members of the resolved-model records in snake_case, built-in helpers and the helpers the pack's scripts register, from `GET /api/templates/context` (§5.1). A script file opens with Monaco's JavaScript language and the registration API declared (`maquettiste.helper(name, fn)` and the other four). JSON files validate against their schema when one applies. This needs more of Monaco than the editor loads today (`code/monaco-setup.ts` registers only json, sql and csharp and the JSON worker; `code/CodeEditor.tsx` takes only those languages and has no markers or completion hooks): step 6 registers javascript and typescript with their worker, markdown, yaml and the Scriban language, routes `getWorker` by label, and widens `CodeEditor` with the language type, an `onMount` and markers API and a completion provider; the bundle-size cost is recorded in the step's commit.
- **Save** (Ctrl/Cmd+S) sends `PUT /api/packs/{pack}/file?path=` with `If-Match: "<hash>"` (a new file: `If-None-Match: *`). The response carries the new hash and the file's parse diagnostics (MQ6003 with line and column), which appear as markers; a template that fails to parse is still saved, because the file is the truth (P3) and the next plan reports it. A **409** (the file changed since it was read: another tab, the CLI, git) keeps the buffer and shows "Changed on disk" with **Compare** (a diff editor: disk on the left, yours on the right), **Keep mine** (saves with the new hash) and **Take theirs**. A `templates.changed` event for an open, unmodified file reloads it silently; for a modified one it shows the same bar before any save.
- **Live preview**: the preview renders the **selected unit** (a picker over the units that use the open file directly or through includes; the first by default) for **one chosen element** (the Example element picker of §3.1, shared; its elements come from the index for an `each` kind and from the unit's scope listing otherwise, which the server computes without rendering) with the **unsaved text of every open, modified pack file** as an overlay. Opening a template renders that one element and nothing more (§5.2, "Bounds"). **Preview a list…** renders up to 20 elements the user ticks, when asked, one request each. A `wide` unit (`for: model`, `each locale`, a selector that returns databases: one render covers the whole model, a database or a locale) is not rendered as the template is typed: the pane offers **Preview (renders the whole model)** ("a whole database", "every string of a locale"); once asked it renders again as the text changes only while its last render took at most 1 s, and otherwise shows "Out of date" with **Preview again** (`widePreview.ts`). The Database screen's whole-database DDL preview follows the same rule. It calls `POST /api/templates/preview` (§5.2) 300 ms after typing stops, cancels a request still in flight when a newer one starts (and the server cancels the older one too, §5.2), works for a disabled pack as for an enabled one, and shows each rendered file with its output path (`files[].path`), its role and a **Diff against disk** toggle (what applying would change for that file). Diagnostics from the preview appear inline: markers in the open template at the reported line and column (a render error in a partial marks the partial and shows a link in the preview's diagnostics list), with the rule id and message. The preview never writes; its read keys are shown on request ("Reads: Customer, 4 of its referrers, conventions, table.scriban, _sql.scriban"), which is how a pack author learns what a change will re-render.

### 3.4 Outputs

The manifest view: `GET /api/packs/{pack}/outputs` returns every manifest entry with its unit, element, root, mode and state (§2.1), and the time of the last run that wrote the manifest. A virtualized table groups by unit (or by root: the owner's dozens of roots) with filters by state and root and a path filter. **Open diff** on a file: for a file in the current plan, the plan's diff (`GET /api/generate/plan/{id}/diff`); otherwise the disk text against a fresh preview of its unit and element, which shows a hand edit against what the template now produces. Actions: Open template, Reveal element, Explain (§4.3).

## 4. Plan explanation on the Generate screen

### 4.1 What every planned change shows

The changes table (phase2-design §4.8) gains the columns **Pack**, **Unit**, **Template**, **Element** and **Why**, beside kind and path. **Why** is one sentence from the unit's first cause, with "+N" when there are more; Space or the chevron expands the row into the full cause list (the virtualizer measures expanded rows with `measureElement` rather than a fixed estimate), each cause a link (open the element, the template at the file, the settings section, the parameter row). Above the table a **cause summary** groups the plan: "Template table.scriban changed: 412 files · Customer (entity) changed: 3 files · Setting conventions changed: every unit of sql-ddl". Group by cause, by pack and unit, or by root; filter by any of them.

### 4.2 Causes, from the recorded keys

The planner already recomputes the current hash of every key a unit recorded, to decide the skip (§11). It now compares each one to the hash **recorded at the unit's last render**, so the explanation costs a comparison per key. The unit state store's next format (`Format = 3` in `UnitStateStore.cs`; today `Format = 2` is written to a file named `.v1.bin`, so the file is renamed after the constant, `units/<pack>.v3.bin`, and the old file is deleted when the new one is written) stores, per unit, each read key with its hash at render time (the key table the file already shares, plus a 16-byte truncated hash per key) and the hashes of the static parts `UnitHashPrefix` already combines: pack version, unit definition, each effective parameter by name, scripts, output base, formatter settings, and the unit and companion template hashes. `StaticHash` is unchanged; the parts are only for explanation. The store refuses a file written by another engine version or format (it loads as empty), so after an engine upgrade, or with a state file from before this feature, every unit has reason `new` and the single cause `state-reset`. A unit's `reason` is the first that applies: `new` (no usable previous state), `forced`, `check`, `inputs` (a key or static part differs), `outputs` (an output is missing, or its disk bytes differ from the manifest), `unchanged` (skipped). Causes, ordered by kind then key (ordinal), then capped at 20 with `causeCount` giving the total:

| Cause kind | From | Sentence |
| --- | --- | --- |
| `element` | `e:<id>` | "Customer (entity) changed" |
| `kind-set` | `k:<kind>` | "An entity was added or removed" |
| `referrers` | `r:<id>` | "Something that references Customer changed" |
| `setting` | `s:<section>` | "Setting conventions changed" |
| `template` | `t:<pack>/<path>`, and the static unit and companion template hashes | "Template _table.scriban changed" |
| `schema-diff` | `d:<databaseId>` | "The schema diff of main changed" |
| `translation`, `localization` | `l:<locale>:<owner>`, `s:localization` | "French texts of Customer changed" |
| `pack-version`, `unit`, `parameter`, `scripts`, `output-base`, `formatter` | static parts | "sql-ddl went from 1.0.0 to 1.1.0", "Parameter quoting changed", "Unit table's definition changed" |
| `absent` | a key that no longer resolves | "Order (entity) was deleted" |
| `output-missing`, `output-edited` | outputs | "db/main/seed.sql is missing", "… was edited on disk" |
| `state-reset` | no usable state (reason `new` after an engine change or a state file of another format) | "No state from this engine version or format; every unit renders" (once, until it renders again) |

Parameter causes name the parameter (per-name hashes), so "quoting changed" does not blame every parameter. Causes are deterministic: the same model, templates and state give the same causes in the same order at `--jobs 1` and `--jobs N`.

### 4.3 Why not

**Show unchanged units** lists the plan's skipped units; **Why not?** on one calls `GET /api/generate/plan/{id}/unit?key=`: "Skipped: its 23 recorded inputs (Customer, 4 referrers, conventions, 2 templates…) are unchanged since the run of 14:02, and its 1 output is intact." **Explain…** (explorer context menu, palette, and a field on the Generate screen) answers for any pack, unit and element through `POST /api/generate/explain`, including elements the plan does not contain, with the first reason that applies: pack disabled; pack not in this run's selection; unit scope does not cover the element's kind; the filter excludes it (naming the filter that failed: "tags: api is not on Order"); `generation.skip` on the element; the selector did not return it; its output root is not in the selected roots; or planned, with its reason and causes. The same answers come from `maquettiste explain` (§5.4).

## 5. API, MCP and validation

### 5.1 Operations

`openapi.yaml` and `schemas/v1` change first (steps 1 and 3 of §8); file paths and unit keys (which contain `/` and `:`) travel as query parameters, never as path segments. Every write takes `If-Match` (or `If-None-Match: *` to create) and answers 409 with the current hash and text; the ETag of a pack file is the SHA-256 of its bytes, as for model files.

| Operation | Does | Role |
| --- | --- | --- |
| `GET /api/packs` | Every pack: name, version, enabled, unit rows with path summary inputs, diagnostics, file count | viewer |
| `POST /api/packs` | New pack `{name, from: "empty" \| <starter>}`; 409 when the folder exists | maintainer |
| `GET /api/packs/{pack}` | `pack.json` as a document with its ETag, the script registrations (helpers, selectors, filters, transforms), diagnostics | viewer |
| `PUT /api/packs/{pack}` | Save the whole `pack.json` document (every member: `version`, `description`, `engine`, `scripts`, `usesSchemaDiff`, `units`, `parameters`, `parameterSchema`, and any other), schema-checked and written canonical with `CanonicalJson` as `pack new` does; returns diagnostics | maintainer |
| `GET /api/packs/{pack}/files` | The folder tree: path, size, hash, derived role, used by | viewer |
| `GET`, `PUT`, `DELETE /api/packs/{pack}/file?path=` | Read, write, delete one file; `PUT` returns hash and parse diagnostics. `PUT` and `DELETE` of `path=pack.json` answer 400 pointing to `PUT /api/packs/{pack}`, so `pack.json` is never written raw | viewer; maintainer |
| `POST /api/packs/{pack}/file/move` | `{from, to, updateUnits, expectedPackHash}` with `If-Match` for the source file; `expectedPackHash` is required when `updateUnits` is set, which rewrites `pack.json` (canonical) in the same write; 409 when either hash is stale; refused while a template `include`s `from` | maintainer |
| `GET /api/packs/{pack}/outputs` | Manifest entries with unit, element, root, mode, state; last run time | viewer |
| `PUT /api/project/settings/packs/{pack}` | Save `packs.<pack>` of `maquettiste.json` (enabled, output base, parameter values) with the settings ETag; nothing else in the file | maintainer |
| `POST /api/templates/preview` | Extended (§5.2) | viewer for a saved unit; maintainer when the request carries `unitOverride`, `overlay` or `parameters` |
| `POST /api/templates/paths` | Rendered output paths for a unit over its scope or given elements, count, collisions, root check | viewer for a saved unit; maintainer with an unsaved `unitOverride` |
| `GET /api/templates/context?pack=&unit=` | Completion data: variables, member tree of the resolved records, helpers | viewer |
| `GET /api/generate/plan/{id}/unit?key=` | One unit's reason, all causes and, when skipped, its recorded keys grouped | viewer (as `getPlan`, which already returns the read keys) |
| `POST /api/generate/explain` | Why (not) for `{pack, unit, elementId}` | viewer |

**Unsaved text is code.** An output pattern, a template, a script and a parameter value are all rendered or run by the server (an output pattern is Scriban text rendered by `RenderRun.RenderPath`; `select` and `where.script` run pack scripts), so any request that carries text not saved in the pack (`unitOverride`, `overlay`, `parameters`) requires maintainer, the role that may save that text anyway. Viewers preview and compute paths for saved units only. The `x-maquettiste-role` description of both operations in `openapi.yaml` states this.

**The pack file guard.** The engine's model-write guard alone is not enough: `WriteTarget.Model` checks the real path against the whole model root, so a symlink inside one pack that points at another pack, at `model/` or at `maquettiste.json` passes it, and `PackFiles.Resolve` is lexical only. `Editor/PackFiles.cs` therefore resolves the request's path lexically (relative, no `..`, no absolute segment), then checks the real path of every existing ancestor and of the target against the real pack folder (`templates/<pack>`), and only then calls the model-write guard (§12.1). It also enforces §3.3's text and size rules and refuses `pack.json`.

**Events.** The model watcher already watches `templates/` and publishes `project.changed`, which drops cached previews. Two refinements go with it: `templates.changed {pack, files: [{path, hash|null}]}` and `packs.changed {packs}`, sent alongside `project.changed` (never instead of it) so the editor can reload one file or one pack; the functions' own writes emit them too.

### 5.2 Preview, paths and the plan's new members

`PreviewRequest` keeps `unit: string` (the unit id, which the Database screen sends today) and gains `unitOverride` (a whole `PackUnit` used instead of the saved unit: the unsaved grid row; its `id` must equal `unit`, else 400), `overlay` (pack-relative path → text: unsaved template, partial and script text) and `parameters` (effective values to use). `PreviewResult` gains only `readKeys` and `elapsedMs`; `files[].path` is already the repo-relative output path. The host keeps a **preview session** per pack: the resolved model, the pack and its schema diffs, rebuilt only when one of its inputs changes; the session loads the requested pack even when `packs.<name>.enabled` is false. A call parses the overlaid files (cached by text hash), renders one unit and discards the renderer. Nothing in a preview touches disk.

**Bounds** (2026-10-05; the owner: "if I'm not clicking generate somewhere, you should not be generating the full model ever ... I have 100k seed rows or more, this is not a toy"). Only Generate, Plan and Apply (and the command line's `generate`) plan every unit or render more than what the user is looking at. On a model of 3,000 entities stored as 2,741 designed tables with bindings, 20,000 relations and a 100,000-row seed, a template preview used to time out (MQ6007): it prepared a whole dry run of the pack (validation, a full resolution, every unit planned, every schema diff) and kept it for 2 s only, and opening a template also rendered 200 elements to list paths. Now:

- **The session plans nothing.** It holds the snapshot's resolved model from the store's shared resolution (`ModelStore.ResolvedAsync`: the whole-model validation and the resolution run once per snapshot and are shared with validate's resolver findings and `GET /api/model/resolved`, so an edit costs one of each whoever asks first), the pack loaded by name, the output path policy, schema diffs computed per database on first read (`LazySchemaDiffs`, only for a pack with `usesSchemaDiff`; a table's DDL never diffs anything) and the saved units' scope listings. A preview renders one `PlannedUnit` built for the asked element, with a hasher that hashes nothing (`PreviewHasher`: a preview returns no input hash). The pack's other units are not planned, so a broken selector elsewhere in the pack no longer blocks a preview.
- **The session lives until its key changes**: model version, settings hash, the service's write epoch, the pack folder's paths, sizes and write times, and the committed schema snapshots' sizes and write times (what a command-line `generate` in the same repository writes that a preview reads). There is no time limit. Concurrent requests with the same key wait for one preparation (single flight, run without any caller's token).
- **Seed rows are converted when read.** The resolver builds an entity's or a relation's seed rows without converting their cells (`RSeedRow.Values` on first read), and orders a seed's rows (`RSeed.OrderedRows`) and the seeds (`ResolvedModel.SeedsInOrder`) on first read; a seed whose columns name no other row keeps file order without reading its cells. A reference type's rows stay eager (codes and usages need them). Output is byte-identical.
- **The resolver's binding lookups are indexed.** A binding's foreign-key field found its relation end by walking every relation (`DatabaseRun.BindingBuilder.Target`); `ResolveRun.RelationsWithEnd` indexes the ends once. Resolving the database with 2,741 bound tables went from 15.6 s to 0.8 s, the whole model from 16.2 s to 1.4 s.
- **`paths` renders only what is asked** (below), and the template context reads the pack alone.

| Request | What it does now | Bound |
| --- | --- | --- |
| `POST /api/templates/preview` | one unit for one element, on the pack's session | 1 render; no unit planned |
| `POST /api/templates/paths` without `elementIds` | this unit planned alone (scope, skip hints, filter): `count`, `elements` (up to `limit`, default 200, at most 2000, with names and kinds), `wide`, MQ6019 and a constant pattern's MQ6020 | 0 renders; 1 unit planned, kept with the session for a saved unit |
| `POST /api/templates/paths` with `elementIds` | those elements rendered for their paths | at most 20 renders (more is 400) |
| `GET /api/templates/context` | the pack's `pack.json`, parameters and script registrations, and the record types' members | no model work |
| Opening a template (the editor) | context, the scope listing, one preview; a `wide` unit waits for **Preview** | 1 render |
| Units tab | each row's scope listing; the selected row's example path | 1 render, none for a `wide` unit |
| Database screen | the picked table or object; the whole database waits for **Preview the whole database** | 1 render |

`time-preview` in the bench app (`bench/README.md`) builds that model and measures it.

**Deadline.** Template text being typed can loop without end (the Scriban limits are per loop and per recursion, so two nested loops of a million run until cancelled). Preview and paths therefore pass `HttpContext.RequestAborted` linked to a deadline of `limits.scriptTimeoutMs` × 4 (8 s by default) into the render; a deadline hit fails the unit with MQ6007. One preview is in flight per client: a request that sends `X-Maquettiste-Client` (the editor's live preview sends one key per tab) cancels an older request of the same kind and key on the server, not only in the browser, and the older one answers 409 `superseded`. The key is opt-in because one tab sends several path listings at once and HTTP/2 multiplexes them on one connection. The preview session is reused while the model version, the settings hash, the pack folder (paths, sizes, write times) and the service's write epoch (bumped when a run takes and releases the run lock) and the committed schema snapshots (sizes, write times) are unchanged, with no time limit (2026-10-05; it was at most 2 s, which made every preview on a large model prepare again): the stamps see what a command-line run in the same repository writes that a preview reads. Overlay scripts get a pool from `scripts.CreatePool(scripts, model.Settings.Limits, …)` exactly as `UnitPlanner` does, so the sandbox limits apply. Step 3 adds a fixture with a nested-loop template that must end with MQ6007 within the deadline.

**Paths.** The engine renders a unit's output pattern in the same context as the template, after it, so a pattern can read variables the template assigned. `paths` plans the unit alone over its scope after the filter and after `generation.skip` hints (`UnitPlanner.SkippedByHints`), as the planner counts them, and renders nothing for that: it reports the count, the planned elements with names and kinds (`elements`, what an element picker lists), `wide`, MQ6019, and MQ6020 for an output pattern with no code span over a scope of several elements. Paths are rendered only for `elementIds` (at most 20), each fully, so a pattern that reads a variable the template assigned gets its real value; the count and `elements` are then those of the listed elements the unit plans, and two of them on one path are MQ6020 with both names. A `wide` unit's paths come with a preview the user asks for.

`PlanUnit` gains `pack`, `unit`, `template`, `elementId` (null for `model` and `each locale`), `reason`, `causes` (`PlanCause {kind, key, detail, elementId?, path?}`) and `causeCount`; `FileChange.pack` and `FileChange.unitKey` already exist and join a change to its unit; `PlanUnit.readKeys` already exists. The explanation members add at most 10% to a plan's payload at bench scale (causes are capped, skipped units carry none).

### 5.3 Validation rules

Every rule has a catalog entry in `RuleCatalog.cs`, a fixture under `tests/fixtures/validation/` and a test; the editor shows them in place (grid cell, form row, file marker) and the Problems panel lists them with the pack.

| Rule | Severity | When |
| --- | --- | --- |
| MQ6003 (exists) | error | A template or partial fails to parse. Now also found at pack load and on save: each file is parsed with the delimiters of each unit that reaches it, and the error is reported at that unit, which renders nothing while the pack's other units run. The pack is not unloaded (a pack with any load error is left out and stops the run, `PackLoader.cs`, so load-time checks must not be pack errors) |
| MQ6025 | warning | A pack file that no unit reaches fails to parse with the default delimiters |
| MQ6026 | error | A preview names an element outside its unit's scope (round 7): an `each <kind>` unit without an element or with an element of another kind, a selector unit with an element its selector does not return, a `model` unit with an element. The preview renders nothing and says "This template expects <kind>; pick one" instead of the raw script error (MQ6006) the template would raise; `paths` is not affected (it plans the unit) |
| MQ6019 | error | A unit's output path cannot stay under an allowed root: the output base plus the pattern's literal prefix (up to the last `/` before the first code span) is under no `outputs.allow` root and contains none, or the pattern has a literal `.`, `..` or absolute segment. Checked at pack load as a unit-level error that skips that unit only, in `paths` and in the grid; a rendered path outside every root stays MQ6004 |
| MQ6020 | error | Two elements of one unit render the same output path (the pattern is not unique per element); names the unit, both elements and the path. At plan it replaces MQ6005 for collisions inside one unit; MQ6005 keeps cross-unit and case collisions |
| MQ6021 | error | A unit's `for` names an unknown scope; lists the valid scopes and the nearest one ("each tables" → "each table"). The schema pattern on `for` catches it, so `PackLoader` maps a schema failure at `/units/<i>/for` to MQ6021 before its early return on schema errors. Was part of MQ6001; the pack still does not load |
| MQ6022 | error | A unit names a template or companion template that is not in the pack. Was part of MQ6001 |
| MQ6023 | error | A project value in `packs.<name>.parameters` fails the pack's `parameterSchema` |
| MQ6024 | warning | A project value names a parameter the pack neither defaults nor declares |

### 5.4 MCP tools and CLI

MCP (docs/mcp.md): `list_packs` (extended with units and diagnostics), `get_pack`, `save_pack`, `new_pack`, `list_pack_files`, `read_pack_file`, `write_pack_file` (`expectedHash` required, as the API's `If-Match`), `delete_pack_file`, `get_pack_outputs`, `preview_unit` (with overlay), `unit_paths`, `explain_unit`; `plan` and `get_plan` carry the new members. An MCP client can then write a pack end to end: create it, add a unit, write the template, preview on an element, fix diagnostics, plan and read why. CLI: `maquettiste pack show <name>` prints the unit rows of §1 and the parameter values; `maquettiste explain <pack>/<unit> [<element>]` prints §4.3's answer; `generate --dry-run --explain` adds the Why column to the text output and the members to `--format json`.

## 6. Density, keyboard and accessibility

**Density** (the owner: "we don't need comfortable rows, make everything very dense"; E23). The editor has one density, and it applies to every screen, not only the ones this design adds. Rows are 24 px, tree and grid text 12 px with 11 px secondary, cell padding 4 px, toolbars and tab strips 28 px, pane headers 24 px, the top bar 32 px (from 44), the rail 36 px wide (from 48); buttons and inputs sit on a 24 px (`h-6`) or 28 px (`h-7`) scale, so today's `h-8`, `h-9` and `h-10` controls come down. The row height is one constant shared by CSS and JavaScript: `design/density.ts` exports `ROW_H = 24`, `tokens.css` sets `--mq-row-h: 24px`, and a unit test asserts they agree; every virtualizer's `estimateSize` and every derived height (the explorer's rename input) reads `ROW_H` instead of its own number. The density toggle, its palette command, the store's `density`, `applyDensity`, `data-density` and the `[data-density=comfortable]` block go. Every screen in this design is laid out for that: the Units grid shows about 30 rows on a laptop screen, the Outputs table about 35. 24 px rows meet WCAG 2.2 2.5.8 (target size minimum) when every row action's hit area is the full 24 × 24 px, even where its glyph is 14 px.

**Keyboard.** The tree follows explorer-redesign §3.4 (arrows, Home, End, type-ahead, `*` expands siblings, Enter opens, Shift+F10 or the menu key opens the context menu). The pack editor: Alt+1 to Alt+4 switch tabs; F6 and Shift+F6 keep cycling the shell regions (phase2-design §4.8), and on the Templates tab its panes (tree, editor, preview, diagnostics) are regions of that cycle; Ctrl/Cmd+S saves the active file or `pack.json`; in the template editor, Ctrl/Cmd+Enter refreshes the preview now; F8 and Shift+F8 walk diagnostics; Ctrl+M toggles Monaco's Tab capture so Tab leaves the editor; quick open (explorer-redesign §3.1) finds pack files. The Units grid uses the Rows grid map (arrows; Enter or F2 edits; Esc cancels; Tab commits and moves right; Ctrl+Enter inserts a unit below; Ctrl+D duplicates it with the id suffixed `-copy`; Ctrl+Delete removes the selected units after a confirm that names the files they own; Alt+Up and Alt+Down reorder). The Generate table: arrows move, Enter opens the diff, Space expands causes, `e` explains the focused unit.

**Accessibility.** The tree is an ARIA `tree` with `aria-level`, `aria-expanded` and `aria-setsize`/`aria-posinset` under virtualization; the grids are `grid` with `aria-rowindex`; tabs are a `tablist`. States (hand-edited, orphan, missing, owned) carry text and an icon, never color alone. The preview announces only a change in its diagnostics count through a polite live region ("2 errors"), not each render. The 409 bar takes focus and returns it to the editor. Monaco's accessibility mode follows the screen reader setting. Axe runs on the pack editor tabs and the explained Generate table in the Playwright mock project.

## 7. Performance targets

| Measure | Target | How |
| --- | --- | --- |
| Preview of a typical unit (one table's DDL at bench scale), warm session (a runaway template ends at the §5.2 deadline) | p95 ≤ 300 ms from request to response; ≤ 400 ms from last keystroke to painted result with the debounce excluded | Preview session (§5.2); one unit rendered; overlay parse cached by text hash |
| First preview after a model change | ≤ the host's incremental resolve budget plus 300 ms | Session rebuilt once per model version |
| Explorer: expand a pack node or its Units, Templates or Parameters | ≤ 50 ms to paint | From the cached `GET /api/packs`; no call on expand |
| Explorer: expand Outputs of a unit with 5,000 files | ≤ 50 ms to first paint | Manifest data fetched once per pack and run, virtualized |
| `paths` over 5,000 elements | ≤ 500 ms | The unit planned alone on the session's model; nothing rendered (2026-10-05: 250 to 450 ms over 20,070 tables of the `time-preview` model) |
| Preview of one element on a large model (`time-preview`: 3,000 entities, 2,741 bound tables, a 100,000-row seed) | well under 1 s warm, a few seconds cold | Session per pack with no time limit; 2026-10-05: 200 to 360 ms warm, 0.5 to 3.6 s cold (the shared validation and resolution), 1.9 to 3.1 s after a model edit |
| Plan with explanation | ≤ 5% slower than without; unit state file ≤ 2× its v1 size | Per-key comparison of hashes already computed |
| Template save round trip | ≤ 100 ms | One write through the guard, one parse |
| Scriban tokenizer | 60 fps typing in a 5,000-line template | Monarch states only, no regex backtracking |

The bench (`bench/`) gains `preview` and `explain` measures that record, as the scale timings do on the shared runner.

## 8. Migration and sizing

Each step leaves the model valid and the editor usable; contract steps go through `openapi.yaml`, `schemas/v1`, `npm run gen:api` and `npm run gen:mocks` first. Sizes are for one engineer or agent.

| # | Step | Main files | Tests | Size | Status |
| --- | --- | --- | --- | --- | --- |
| 0 | One density (E23) | `src/editor/src/design/tokens.css` (`--mq-row-h: 24px`, `--mq-topbar-h: 32px`, `--mq-rail-w: 36px`, remove the comfortable block), a new `design/density.ts` (`ROW_H`), `design/theme.ts` and `app/App.tsx` (remove `applyDensity` and `data-density`), `state/store.ts`, `app/TopBar.tsx`, `palette/CommandPalette.tsx`; `ROW_H` in `explorer/Explorer.tsx` (today `density === "compact" ? 32 : 40`, also paging), `explorer/TreeRow.tsx` (rename input), `workspaces/generate/GenerateWorkspace.tsx` (estimate 30), `workspaces/reference-data/RowsGrid.tsx` and `TypeList.tsx` (`ROW_H = 28`), `references/ReferencesPanel.tsx` (`ROW = 24`); every `h-7` to `h-10` control and header onto the §6 scale | `tests/unit/store.test.ts` (density removed), a token/constant agreement test, `tests/e2e/shell.spec.ts` (the density toggle part removed), Playwright specs that assert row heights, axe target-size | 1.5 days | Built: one density, no toggle (`design/density.ts`, `tests/unit/density.test.ts`) |
| 1 | Contract | `schemas/v1/pack.json` (`parameterSchema`, `each database`); `openapi.yaml` (§5.1 operations, `PreviewRequest`/`PreviewResult`, `PlanUnit`, `PlanCause`, `PackDocument`, `PackFile`, `PackOutputs`, `ExplainResult`, the two events); `RuleCatalog.cs` MQ6019–MQ6025; `x-maquettiste-role` descriptions for unsaved text (§5.1); `engine-design.md` §2.5, §8, §11 | Contract suite; schema consistency; catalog completeness | 1.5 days | Built |
| 2 | Engine rules and explanation | `Planning/PackLoader.cs` (MQ6003 per reaching unit, MQ6025, MQ6019 and MQ6003 as unit-level errors, MQ6021 mapped from the schema failure, MQ6022, MQ6023, MQ6024), `Planning/UnitPlanner.cs` (`each database` if Q3 keeps it, MQ6020), `Planning/UnitStateStore.cs` (`Format = 3`, file named after it, per-key and static-part hashes), skip selection (reasons and causes), `Explain` service; `engine-design.md` §11's `StaticHash` formula corrected to name the pack version and the unit and companion template hashes | One fixture per rule; explanation goldens: edit a template, an entity, a parameter, a setting, a translation, bump the pack version, delete an element, hand-edit an output, a state file of the previous format (`new` with `state-reset`); causes identical at `--jobs 1` and `--jobs 8` | 4 days | Built (reasons, causes, `Format = 3` state, Explain service) |
| 3 | Preview session, paths, pack files | `GenerationService` (session loading disabled packs, overlay loader with the sandbox limits, unit override, deadline and per-connection cancellation, `PathsAsync` in fresh contexts, context), `Editor/PackFiles.cs` (read, write, move with both hashes, delete, ETag, real-path guard, `pack.json` refused), `Editor/PackList.cs` (units, registrations, roles through the loader's shared function) | Overlay never writes (file-system spy); guard fixtures (`..`, a symlink leaving the folder, a symlink to another pack, to `model/` and to `maquettiste.json`, binary content, size, `pack.json`); 409 on a stale file or pack hash in move; nested-loop template ends with MQ6007; a pattern reading a template variable; preview p95 in the bench | 3.5 days | Built; round 7: a preview outside the unit's scope returns MQ6026 and no files (`UnitPlanner.OutOfScope`). Built with it: the preview's element picker offers only elements of the unit's scope (`workspaces/generate/previewScope.ts`), and the mock server answers MQ6026 as the engine does. Left: a real source map in `PreviewResult` (step 6 matches lines by text) |
| 4 | Functions, MCP, CLI | `_functions/TemplateEndpoints.cs` (maintainer for unsaved text), a new `PackEndpoints.cs` (including the narrow pack settings save), `GenerateEndpoints.cs`, `ModelWatcher.cs` (the two refinement events beside `project.changed`); `Cli/Mcp/ModelTools.cs`; `Commands/PackShowCommand.cs`, `ExplainCommand.cs`, `--explain`; `docs/mcp.md` | Contract tests per operation; MCP tool tests; CLI parser and golden text tests | 2.5 days | MCP and Functions built; left: CLI `pack show`, `explain`, `generate --dry-run --explain` |
| 5 | Generate explorer and Units, Parameters | `explorer/generate/*` (tree, `pathSummary.ts`), `app/Rail.tsx` (`GenerateSidebar` moves out), `workspaces/pack/{PackScreen,UnitsTab,ParametersTab}.tsx`, `workspaces/pack/unitsModel.ts`; mocks for §5.1 with the two example packs and a monorepo fixture (40 packs, 60 roots) | Vitest: path summary table, scope help, grid model, parameter form inference; Playwright mock `generate-explorer.spec.ts`, `pack-units.spec.ts`; axe | 3 days | Built, under `workspaces/generate/` (`GenerateExplorer`, `PackEditor`, `UnitsTab`, parameters in `PackEditor`); left: selector picker, 409 replay of unsaved rows, Edit definitions |
| 6 | Templates tab | `code/monaco-setup.ts` (javascript/typescript and worker, markdown, yaml, scriban; `getWorker` by label), `code/CodeEditor.tsx` (wider language type, `onMount`, markers, completion), `code/scriban.ts` (tokenizer, completion provider), `workspaces/pack/{TemplatesTab,FileTree,PreviewPane,ConflictBar}.tsx`, `workspaces/pack/preview.ts` (debounce, abort, markers) | Tokenizer unit tests; debounce and abort; 409 flow and external change e2e; markers from MQ6003 and MQ6006; bundle size recorded | 3.5 days | Built (`code/scriban.ts`, `CodeEditor.tsx`, `TemplatesTab.tsx`, `templatesModel.ts`). Close-out (2026-09-29): completion and hover from `GET /api/templates/context` plus the template language's pipe functions (`code/scribanCompletion.ts`, providers in `code/scribanProviders.ts`, Monaco's suggest, snippet and hover contributions now loaded); template and output lines highlighted both ways (`lineMap.ts`). Differs: the renderer reports no output positions, so the map is a text match on each line's literal text, marked "approximate" in the preview, and `PreviewResult` has no source map; New, Rename (units rewritten through `updateUnits`) and Delete (refused with the users while referenced) in the file tree (`packPathProblem`, `namedByUnits`); tests `template-authoring.test.ts`, `templates.mock-only.spec.ts` |
| 7 | Outputs and plan explanation | `workspaces/pack/OutputsTab.tsx`; `workspaces/generate/GenerateWorkspace.tsx` (columns, cause summary, grouping, expandable rows measured with `measureElement`, why not, Explain…) | Vitest: cause sentences, grouping; Playwright mock `generate-explain.spec.ts` | 2 days | Built: Outputs tab; plan summary line per pack, changes grouped by unit with counts, Unit and Element and Why columns, filters (kind, pack, unit, words), Why this file, Unchanged units with Why not?, Explain (`PlanExplain.tsx`, `planModel.ts`, `generate-plan.test.ts`, `generate-explain.mock-only.spec.ts`). Differs: the full cause list shows in the Why this file panel instead of expanding the row; close-out (2026-09-29): **By cause** ("Template table.scriban changed: 412 files", most files first) with links to the element, template, parameter, unit or settings tab, and **By output root** (longest pack output base, else the first folder) under the summary lines (`causeGroups`, `causeLink`, `rootGroups` in `planModel.ts`, `PlanSummary`); left: explorer context menus |
| 8 | Docs and SPEC | `docs/user-guide.md` ("How generation works", §1's table and the sql-ddl reading), pack READMEs (`parameterSchema`), SPEC amendments E18–E23 | Link and copy checks | 1 day | User guide "Generation: how the model becomes files" built, demo and MCP docs updated; left: pack READMEs (`parameterSchema`); round 7 (2026-09-29): MQ6026 in the rule table, docs/mcp.md's `preview_unit` row and errata E25; phase 2 close-out (2026-09-29): the user guide's Templates section checked against the final tab (completion, line highlighting, New file, Rename, Delete, the scope-aware picker); still left: pack READMEs (`parameterSchema`) |

Total: about 22.5 working days. Steps 0, 1 and 8 are independent; 2 and 3 need 1; 5 needs 4; 6 and 7 need 5.

## 9. Decisions

| Id | Decision | Why |
| --- | --- | --- |
| GU1 | The editor edits the pack's files in place through the API; no copy, no stored draft on the server | P3: the CLI, the editor and git must agree; unsaved text lives only in the browser and in preview overlays |
| GU2 | File roles are derived with `PackLoader`'s own rules (the `scripts` list or every loaded `*.js`, `types/*.json`), the unit templates `pack.json` names and the `include`s, through one function shared with the loader | P2: `_` prefixes and `helpers.js` are conventions of the example packs, not the product's; one function keeps the editor and the engine from disagreeing |
| GU3 | "Once" in the Files column means one file for a group; the `once` write mode is labelled "Create only if missing" | The owner asked for "1 file per, or a bunch of items into a single file"; one word must not mean two things |
| GU4 | `each database` becomes a built-in scope, **if the owner agrees (Q3)**; otherwise steps 1 and 2 leave it out and databases stay on selectors | A database is a model kind, so it reserves no vocabulary; "one file per database" should not need a script; but it is new product scope, which the owner decides |
| GU5 | Explanation from per-key hashes recorded at render, not from a second model diff | The planner already hashes every key; the comparison is nearly free and exact |
| GU6 | A template that fails to parse is saved and reported, not refused | The file is the truth; refusing would make the editor stricter than git |
| GU7 | Unit rules get their own ids (MQ6019–MQ6022) instead of MQ6001, and load-time checks fail a unit, not the pack | The editor must place the error on the cell and say how to fix it |
| GU8 | One density everywhere: 24 px rows from one shared constant, 32 px top bar, 36 px rail | The owner's words ("make everything very dense"); 24 px still meets WCAG 2.2 2.5.8 |
| GU9 | Requests carrying unsaved text need maintainer; preview and paths run under a deadline with server-side cancellation | Unsaved patterns, templates and scripts are code the server runs |

## 10. Questions for the owner

1. Shared packs pinned in `packs.lock.json` (SPEC §19): should the editor edit their files in place, or show them read-only with **Copy into project** (a new local pack from the pinned one)? The design assumes read-only with Copy, since a pinned pack's files are someone else's.
2. Roles today: `saveSettings` (all of `maquettiste.json`) is admin, plan and apply are maintainer. The design lets maintainers edit `pack.json`, pack files and their own pack's `packs.<name>` (enabled, output base, parameter values) through the narrow `PUT /api/project/settings/packs/{pack}`, and keeps `outputs.allow` admin, so the writer's bounds stay with admins. Is that the split you want, or should pack settings stay admin-only (the header and Parameters tab then read-only for maintainers), or should pack authoring be its own role?
3. Should `each database` (GU4) be added, or should databases stay on selectors as `sql-ddl` does today? GU4, E19 and steps 1 and 2 depend on the answer; if added, `sql-ddl` keeps its selector until its README is revised.

## Status note (round 6, generation leftovers)

- Unit state is now **format 4** (`units/<pack>.v4.bin`): format 3 shipped in 0.2.0 with the per-key hashes, so the element names
  could not be added to it in place. Each state records the label (`Name (kind)`) of every `e:` key it read at render time; an
  `absent` cause uses it when the model no longer has the element ("Customer (entity) was deleted"). A `.v3.bin` or `.v1.bin` file
  and no `.v4.bin` gives `state-reset` once; saving deletes both older files.
- A plan requested with `mode: check` runs the check (every unit rendered in memory over committed roots; *every root since spec-errata E42*) and gives its units the
  reason `check`; other plans are dry runs as before (the endpoint and the MCP tool still plan in apply mode).
- `GET /api/packs/{pack}` and `GET /api/templates/context` carry `registrations` (kind, name, declaredIn) from one sandbox run of
  the pack's scripts, cached per pack by settings hash and pack folder stamp; the context's `helpers` add the pack's helpers.
- The Problem `code` enum lists `superseded` (the 409 of a superseded preview or paths request did not match the contract).
- Templates tab preview (after the live test): the element picker lists only the unit's scope kind (`previewScope.ts`); with no
  element of that kind it says "The model has no <kind> to preview this template with.", and a render outside the unit's scope
  reads "This template renders one <kind>; pick a <kind> to preview it." instead of the template engine's error.

## Status note (2026-10-01, the owner's day of use)

- **Plan summary.** Files that already match the disk are counted: a pack that writes something ends with "N files
  unchanged"; one that writes nothing says "nothing to write: all 737 files already match the disk" (and "N files kept"
  when the hand-edit policy keeps some). Under the summary a note says "Every file this plan renders is identical to the
  file on disk; Apply has nothing to do.", Apply says why it is disabled, and the change table's **Show** filter always
  offers `unchanged` (`packSummaryLine`, `nothingToWriteNote` in `planModel.ts`).
- **Pack editor panes.** The Templates tab's pack files and preview and the Units tab's help hide from a header button,
  the palette (Toggle pack files, Toggle template preview, Toggle unit help) or Alt+Shift+F, V and U on the Generate
  screen, leave a slim edge, resize by their edge, and are kept in the layout (`state/layout.ts`; Reset layout restores
  them). The template text keeps at least 240 px; the preview gives way first.
- **Example element names.** The Units tab's picker and the Templates preview's element picker name each planned element:
  the path's `elementName` and `elementKind` (read through `NamedUnitPath` until the schema carries them), else the index,
  else "entity @ database" for a synthesized table key, else the id (the option's tooltip). The Units picker reads up to
  the server's 2000 paths and turns into a searchable list past 20 elements (`exampleOptions`, `filterExamples`).
- **New pack.** The choices read **Empty pack** and **Copy of <pack>**, with the line under them described in §2.1.
- **Rename pack** (the owner: "how do i rename the pack?"; added 2026-10-01). **Rename pack…** sits beside **Remove
  pack…** in the pack editor's header and opens a small dialog: a Name field with the engine's pack-name rule as its hint
  (lowercase letters and digits separated by single hyphens, starting with a letter; the current name, a taken name and
  any other name are refused before anything is sent), the folder it moves to, the count of generated files that stay
  tracked, and "This cannot be undone from the editor": the rename moves files outside the model, so it is not an undo
  step. It calls `POST /api/packs/{pack}/rename` (engine-design.md 12.3a) with the `pack.json` hash the editor showed.
  The dialog is mounted beside the Generate explorer, not in the pack editor, so it outlives the pack's tab, which closes
  while the request runs (nothing reads the old name meanwhile) and comes back in the same place under the new name, with
  its pane, focus and plan choice; the explorer's expanded rows follow too. A refused rename reopens the tab under the old
  name and keeps the dialog open with the reason. Unsaved edits in the pack disable the rename until they are saved or
  discarded (`RenamePackDialog.tsx`, `renamePackTab` and `renameExpandedKeys` in `packTabs.ts`). Once the name is valid,
  a dry run of the rename (`dryRun: true`) runs the server's checks and counts the elements whose generation hints name
  the pack; **Also update the generation hints that name this pack (N elements)**, ticked by default, then moves them to
  the new name after the rename as one model batch through the normal element save path (`useBatchEdit`,
  `renamePackHints` in `packHints.ts`), one undo step.
- **DDL preview** (added 2026-10-01). The Database screen's DDL preview no longer assumes a pack named `sql-ddl`: it
  renders the enabled pack with a unit rendered per database (the Storage tab's `databaseUnits` rule), preferring a unit
  whose name mentions schema or table, and for a selected table that pack's `each table` unit; its header names the
  pack and unit, and with no such pack the pane says so instead of hiding (`database/ddlPreview.ts`).

## Status note (2026-10-02, the Generate toolbar)

- **No Roots picker.** The Plan tab's Roots control (all, committed, built) is removed on the owner's call ("you either
  generate or you don't"), with the committed and built root kinds themselves: the editor's plan request no longer
  sends `roots`, and the mock planner plans every root. The API field goes in a parallel engine round.
- **Pack picker.** The row of one checkbox per pack became one toolbar button, **Packs · n of total**, that opens a dense
  checkbox list in manifest order: a filter box past 8 packs, **All** and **None** (acting on the packs the filter
  shows), disabled packs greyed and unticked, and the explorer's warning count on a pack with diagnostics
  (`PackPicker.tsx`). The choice is the same stored `chosenPacks` as before.
