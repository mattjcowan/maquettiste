# Explorer redesign: organizing the model at scale (proposal)

Status: proposal for the owner's review; nothing here is implemented. Scope: the editor's left-hand explorer, the words the editor uses, search and navigation, and the data the explorer needs. It builds on SPEC.md §1 (5,000 entities, 20,000 relations, 300 nodes at 60 fps), §5 (layers), §13 ("Editor at scale") and §14, and on phase2-design.md §4.8 and §4.9. File-format names (`kind: "package"`, the `package` field, `model/packages/`, `schemas/v1/package.json`, `ElementSummary.package`) do not change.

## 0. What is wrong today

- `explorer/filter.ts` groups every index row flat by its `package`, one level deep. `Catalog` is a child of `Billing` in the fixture (`"parent"` in `packages/catalog.json`), but renders as a sibling. Elements with no package fall into a group labelled "Project", which is not a thing the user created.
- Inside a group, all kinds (diagrams, entities, relations, enums, databases, tables, mappings, vocabularies) are interleaved in one list. Tables, views, sequences, databases, mappings and vocabularies have no package, so they all land in "Project". At 700 tables that group alone is unusable.
- Nothing is collapsed by default, and the only counts are on the flat groups.
- The text filter is a substring match on the name, run on the main thread over every row, with no ranking.
- Two scale problems sit behind the tree and must be fixed with it. The virtual "Package: X" canvas view (`canvas/model.ts`, `viewElements`) loads **every** relation in the model to find the edges between that package's entities: 20,000 element GETs at spec scale. And `useElements` issues one GET per id, so a 300-node diagram with its relations is about 1,000 requests.
- Any `model.changed` event the editor did not cause refetches the whole index (phase2-design §4.3). At spec scale that is roughly 7 MB of JSON per external edit.

## 1. Information architecture

### 1.1 Top-level sections

The explorer is one virtualized tree with five fixed top-level sections. They follow the spec's layers (§5), with diagrams and settings kept apart from the model itself:

| Section | Spec layer | Holds |
| --- | --- | --- |
| **Domains** | Conceptual | Packages as nested domains; inside each, its sub-domains, then its elements in kind groups (entities, relationships, lists of values, shared types, and later processes, operations, business events and seed data) |
| **Databases** | Physical | One node per database; inside, tables, views and sequences |
| **Mappings** | Bridge | How each database is mapped: automatic coverage and the customised mappings, grouped by domain |
| **Diagrams** | View | Every saved diagram, in folders that mirror the domain tree |
| **Settings** | Classification and extension | Tags, categories and stereotypes (these open the Settings screen) |

Each section, domain and group starts collapsed and shows a count. At first open the user sees five lines, and each line tells them what is inside. Two more sections appear only when they have members, so they never show empty: **People and access** (actors and permissions, §1.2) and **Other elements** (kinds this editor version does not know yet).

The counts on collapsed Databases and Mappings rows come from the index (databases, views, sequences, table overrides, mapping overrides). The table count and the coverage line need the table summaries (E5c), which load in the background right after the tree's first paint (§4.2); until they arrive those rows show the index counts and a small spinner.

```
▾ Domains                                   40 domains · 5,000 entities
  ▾ Billing                                 1 sub-domain · 4 entities
    ▸ Catalog                               1 entity
    ▸ Entities                              4
    ▸ Relationships                         4
    ▸ Lists of values                       1     (tooltip: "enums")
    ▸ Shared types                          2     (tooltip: "value objects and scalar types")
  ▸ Sales                                   3 sub-domains · 212 entities
  ▸ Not in a domain                         3            (shown only when non-empty)
▾ Databases                                 3 databases · 7,140 tables
  ▾ main   PostgreSQL 16                    712 tables · 12 views · 3 sequences
    ▸ Tables                                712
        ▸ ◇ Billing                         41    (domain icon; tooltip "Domain: Billing")
        ▸ Not linked to an entity           18    (designed or imported tables)
    ▸ Views                                 12
    ▸ Sequences                             3
▸ Mappings                                  main: 5,000 mapped automatically · 20 customised
▸ Diagrams                                  146
▸ Settings                                  Tags 24 · Categories 9 · Stereotypes 6
```

### 1.2 Domains: real nesting, sub-domains first

- **Nesting.** A domain node's children are its sub-domains (packages whose `parent` is this package), sorted by name. Then come its kind groups. The tree is built from `ElementSummary.package`, which for a package already holds its parent (`ModelIndexer.PackageOf`). No contract change is needed for nesting.
- **Counts.** The count on a domain rolls up everything beneath it: "3 sub-domains · 212 entities". The count on a kind group covers only direct members. Rolled-up counts are computed once per index version, bottom-up.
- **Kind groups.** Entities, Relationships, Lists of values (enums) and Shared types (value objects and scalar types together, each with its kind icon). The visible label is the plain word; the technical word is in the tooltip. A group with no members is hidden. **Justification:** in a domain with 150 entities and 600 relations, one mixed list buries the entities. Kinds are also what people scan for ("which enums does Billing have?"). The cost is one extra click in a small domain. The **Auto** option below removes that click without making the structure inconsistent.
- **Kind groups are data, not code.** One table in `model/labels.ts` maps each kind to its group label, tooltip, icon, order and placement rule. Today's rows: entity → Entities, relation → Relationships, enum → Lists of values, value-object and scalar-type → Shared types. The rows for the other SPEC §5 conceptual kinds are named now so they land without a tree change: process → Processes, operation → Operations, event → Business events, seed → Seed data (placed in the domain of the entity or enum it fills, which needs an `entity` field in the index like mappings). Actors and permissions have no package in §5, so they are never placed in a domain and never counted as orphans: they go in the **People and access** section (Actors, Permissions), which appears when the first one exists. A kind the table does not know goes in an **Other** group in its domain when it has a `package`, and otherwise in the **Other elements** section; never in Not in a domain.
- **Auto option (default on, a per-user view preference).** When a domain has 12 or fewer direct elements, its kind groups are replaced by the elements themselves, sorted by kind. The choice is made per domain the first time that domain is expanded, and stored with the expansion state (§4.3). It never changes live: adding a 13th entity does not reshape a domain the user has open; the grouped form applies from the next time the domain is first expanded, or at once when the user turns Auto off. Keyboard users still see a stable order: sub-domains, then entities, relationships, lists of values, shared types.
- **Relations.** A relation is listed in its own domain (its `package`). An entity row can be expanded to show "Relations (n)": every relation that has this entity at one of its ends, including relations from other domains. This needs relation ends in the index (§4.1, E5). Until then, the entity row is not expandable.
- **Orphans.** Non-package conceptual elements whose kind belongs in a domain (entities, relations, enums, types, and later processes, operations, events) and that have no package go under **Not in a domain**, which appears only when it has members. Packages with no parent (`package: null` in the index, as the fixture's Billing) are the top-level domains, not orphans. Elements of other layers are never orphans, because each has its own section, and every such section exists from migration step 4 (§6).
- **Cycles.** A broken `parent` chain (a cycle, or a parent that does not exist) is shown under Not in a domain with a warning icon. Validation already reports it. The tree must not loop.

### 1.3 Databases: tables, views and sequences

- A database node shows its dialect and version. Its children are Tables, Views and Sequences.
- **Tables are mostly synthesized.** A synthesized table's file holds only overrides (`table.json`: `origin`). Most of the 700 tables do not exist in the index at all. The list of tables comes from the database's table list (`TableView`: `key`, `name`, `schema`, `origin`, `entityId`, `relationId`, `isJunction`). Today that means `GET /api/databases/{id}/view`, which also returns every column. At 700 tables that is too heavy for a tree, so a summary form is proposed (§4.1, E5c).
- **Grouping inside Tables.** When the database has more than one schema, tables are first grouped by schema. Within that, they are grouped by the domain of the entity that owns them (`entityId`, then that entity's `package`). Junction tables go with the domain of the relation. Designed and imported tables with no entity go under "Not linked to an entity". A group with more than 12 members shows a count and starts collapsed. Every group named after a domain, here and in the Mappings and Diagrams sections, shows the domain icon and the tooltip "Domain: Billing", so a user who meets "Billing" outside Domains knows what it is.
- Selecting a table opens the Database screen **scoped**, never on the whole database: it draws the table and its foreign-key neighbours to depth 1, widened to the rest of that table's domain group while the total stays at 300 tables or fewer (SPEC §1's node budget; SPEC §13: diagrams are subject areas, never the whole model). The table is focused and its DDL shown. The screen's own "whole database" choice is offered only for databases of 300 tables or fewer; above that it shows a virtualized list with DDL instead of a canvas. The table row's context menu offers "Go to entity".
- **Open question for the owner:** tables could also be shown inside their domains, next to the entities (question 2 in §7). The default proposed here keeps the physical layer in one place, grouped by domain.

### 1.4 Mappings

A mapping file exists only for an override (a column rename, a storage change, an ignored attribute). Conventions map everything else without a file. A section that listed only the files would say "Mappings (20)" in a model where 5,000 entities are mapped, which misleads. So the section shows, per database:

- a coverage line: "main: 5,000 mapped automatically · 20 customised · 3 left out". "Left out" means an entity in one of this database's packages that has no table because it is ignored. The line is computed from the table summaries (E5c); until they have loaded it reads "main: 20 customised" from the index alone.
- **Customised**: one row per mapping element, grouped by domain (with the domain icon), labelled "Invoice → invoices". Opening a row goes to the Mappings screen for that entity and database.

### 1.5 Diagrams as subject areas

- **Where they live.** All diagrams sit in the Diagrams section, in folders that mirror the domain tree (from the diagram's `package`). Diagrams without a package go under "General". Each diagram row shows its member count, from `memberCount` on diagram rows in the index (E5); the tree never GETs a diagram to label it. Against an older server without E5 the count is left out of the row.
- **Domain view.** The virtual "Package: X" view becomes **All of Billing** in the diagram picker. It is offered only when the domain has 300 entities or fewer, the spec's render budget. Above that, the picker offers "New diagram from Billing…", which opens the add-elements dialog scoped to that domain.
- **Membership in the tree.** Each tree row shows a small dot when its element is on the active diagram. The set of members comes from the diagram document, which is already loaded, so this costs nothing extra.
- **From the tree to the canvas** (row context menu, plus drag and drop onto the canvas):
  - **Add to diagram** adds the selected rows. Multi-select works, and the add is one batch.
  - **Add with related…** adds the rows and their neighbours to depth N (§14 "add related", `canvas/model.ts`). The dialog previews the node count and warns when it passes 300.
  - **Show on canvas** centres the element when it is on the active diagram. Otherwise it lists the diagrams that contain it ("In 3 diagrams"), taken from the references endpoint. The mock's `refs.ts` counts diagram members as references; the engine's `ReferenceWalker` must be confirmed to do the same.
  - **New diagram from selection** creates a diagram in the selection's domain.
  - **Where used** opens the References tab (§3.3).
- **From the canvas to the tree.** When the "Follow selection" toggle is on (the default), selecting a node reveals it in the tree. The tree expands only the node's ancestors and scrolls it into view. It never expands whole sections.

### 1.6 Settings

Tags, Categories and Stereotypes are leaf rows that open the matching Settings tab. The vocabulary elements (`tag-vocabulary`, `category-tree`, `stereotype`) are no longer listed as model elements beside entities. This section ships in migration step 4, with the tree itself, because those rows have no other place once the "Project" group is gone.

## 2. Naming

The rule: the user-facing words follow how the owner and non-experts talk. Code identifiers and file formats keep the spec's words. Each row is a UI string change only unless noted. Every user-facing string below lives in one module, `model/labels.ts` (which absorbs `KIND_LABELS`), so a later rename is a one-file change. The words are proposals until the owner answers question 5 in §7; the naming step (step 5 in §6) waits for that answer.

| Current term | Proposed term | Where it appears |
| --- | --- | --- |
| Package | **Domain** | `model/model.ts` `KIND_LABELS.package`; explorer groups; inspector "Package" field; new-entity dialog default (`workspaces/entities/dialogs.tsx`); "Move to package" refactor; filters; Problems grouping |
| Package whose parent is a package | **Sub-domain** | Explorer counts and breadcrumbs; the same element kind |
| "Project" catch-all group | **Not in a domain** (only when non-empty) | `explorer/filter.ts` |
| "Package: Billing" virtual view | **All of Billing** | `workspaces/entities/EntitiesWorkspace.tsx` (diagram picker, view title) |
| Workspace / Workspaces | **Screen** / "Go to screen" | `app/Rail.tsx` aria-label, command palette heading (`palette/CommandPalette.tsx`), SPEC §14 and phase2-design §4.8 wording. The `Workspace` type and the URL segments stay in code |
| Entities (screen) | **Domain model** | Rail label and tooltip, command palette. The URL segment `/entities` stays, so existing links keep working |
| Database (screen) | **Databases** | Rail, command palette |
| Model explorer | **Explorer** (visible label); the aria-label "Model explorer" stays | `explorer/Explorer.tsx` |
| "Filter elements" | **Search the model** (placeholder) | `explorer/Explorer.tsx` |
| Tag vocabulary, Category tree | **Tags**, **Categories** | `KIND_LABELS`, Settings section |
| Mapping (list of files) | **Customised** ("20 customised"); "by convention" becomes **mapped automatically** | Mappings section |
| Relations, Enums | **Relationships**, **Lists of values** (technical word in the tooltip) | Kind group labels |
| "Not mapped to an entity" | **Not linked to an entity** | Databases section |
| Scalar type, Value object | Unchanged as kind names; grouped as **Shared types**, each with a one-line tooltip ("a reusable group of fields without identity, such as Address or Money") | Kind group label, tooltips |
| Project | Unchanged: the whole repo's model and its settings (`maquettiste.json`), shown by name in the top bar | Top bar only; never a tree group |
| Model | Unchanged: the content (domains, databases, mappings, diagrams) | "Search the model", Problems |

**Why "Screen" rather than "View".** "View" already means two things in the spec: a SQL view (a physical element kind) and a diagram (the View layer in §5). A third meaning would bring back the confusion this redesign removes.

**Where "Domain" sits beside the spec.** Spec §5 says a package "drives folders and namespaces in output". The domain tooltip should say so: "Domain (package in model files): groups elements; also sets the output folder and namespace." That keeps the link to the file format visible for people who read the JSON.

**Glossary** (added to SPEC §14 in step 5, and shown as tooltips):

- **Domain**: a business area such as Billing or Sales; stored as a `package` in the model files. A domain inside another is a **sub-domain**.
- **Domain model**: the screen for drawing and editing a domain's entities and relationships (URL `/entities`).
- **Business event**: SPEC §5's Event kind (a "domain event"), a thing that happened, raised by an operation or a process. The group is called "Business events" so it is not read as a kind of domain.
- **Screen**: one of the editor's main areas in the left rail (Domain model, Databases, Processes, Settings…).
- **Mapped automatically / customised**: a table that follows the naming conventions needs no mapping file; a customised one has a mapping override.

## 3. Navigation at scale

### 3.1 Search as you type

- **One search index, in a web worker** (§13: client-side search indexing runs in workers). The explorer box and the command palette (Ctrl/Cmd+K) share it.
- **What is indexed.** Name, `displayName`, kind label, the domain path, and the table name for tables (from the table summaries, which load in the background after first paint; until they arrive, a search shows "table names still loading" under the results and re-runs when they land). Tags and stereotypes are indexed as fields that can be matched with a prefix (`tag:`, `st:`).
- **Ranking**, best first:
  1. exact match
  2. prefix match
  3. word-start match across camel, snake and space boundaries (`invli` finds `InvoiceLine`, `inv_li` finds `invoice_lines`)
  4. substring match
  5. fuzzy match on a subsequence
- **Tie-breakers.** Kind weight (entity, then table, relation, enum, type, diagram, other). Then the element is in or near the domain of the current selection. Then it was opened recently in this session. Then the shorter name, then alphabetical order. Candidate library: uFuzzy (MIT, built for about 100k short strings, with ranking hooks). The fallback is a small in-house prefix and trigram index. `search/rank.ts` holds the ranking as a pure function, whichever library is used.
- **Results mode.** While there is text in the box, the tree is replaced by a flat ranked list. Each row shows its breadcrumb (`Sales › Orders`) and kind icon. The first 200 are shown with "N more: narrow the search". A "Show in tree" toggle switches to the filtered tree instead, with ancestors auto-expanded. That toggle is capped at 500 matches.
- **Keys.** `/` focuses search. Down moves into the results, and Enter opens the element and reveals it in the tree. Esc clears the search and returns to the tree with the selection revealed.

### 3.2 Filters

Chips below the search box, which combine with the search text:

- **Kind**: multi-select.
- **Domain scope**: "in Billing". It includes sub-domains and can be set from a domain's context menu with "Search in this domain".
- **Tag**, **Stereotype**: multi-select, any of.
- **Category**: the category tree. Choosing a node includes its descendants.
- **Has errors**: from validation.
- **On this diagram.**

Filters that are active show a count on the filter button. Counts in the tree reflect the filter ("12 of 212").

### 3.3 Go to definition, where used, breadcrumbs

- **Go to definition** (F12, or Ctrl/Cmd+click on a reference in the inspector or grid). It follows an attribute's type to its enum, value object or scalar type, a relation end to its entity, a mapping to its entity or table, and a table to its entity. It then selects the target, reveals it in the tree and centres it on the canvas when the target is on the active diagram.
- **Where used** (Shift+F12). A **References** tab in the bottom panel lists the results of `GET /api/model/references/{id}` in a virtualized list. The results are grouped by kind of the referencing element, then by domain, with the JSON pointer field shown ("InvoiceLine · attribute amount → type"). A diagram membership shows as "on diagram Billing overview". Clicking a row goes to that element and pointer.
- **Breadcrumbs.** A breadcrumb above the canvas and the inspector: `Domains › Sales › Orders › Entities › Order`. Each segment opens a menu of its siblings, as in VS Code. The same path appears under each search result.
- **History.** Alt+Left and Alt+Right move back and forward through selections.

### 3.4 Keyboard navigation of a virtualized tree

- **ARIA.** The tree follows the WAI-ARIA tree pattern: `role="tree"`, `treeitem`, `aria-level`, `aria-expanded`, `aria-selected`, and, because rows outside the viewport are not in the DOM, `aria-setsize` and `aria-posinset` on every rendered row. DOM focus stays on the `role="tree"` container, which carries `aria-activedescendant` pointing at the active row (as today's listbox does). There is no roving `tabindex`: the two are alternative focus models, and keeping focus on the container suits virtualization, because focus never leaves it when rows unmount. The virtualizer keeps the active row rendered.
- **Keys:**
  - Up and Down move between rows.
  - Right expands a row, or moves to its first child. Left collapses a row, or moves to its parent.
  - Home and End go to the first and last row. PageUp and PageDown move by one viewport.
  - `*` expands all siblings.
  - Typing letters jumps to the next visible row that starts with them.
  - Enter opens the row. Space toggles it in the selection. Shift+arrows extend the selection.
  - The context-menu key, or Shift+F10, opens the row menu.
  - F6 still cycles the shell regions (phase2-design §4.8).

### 3.5 Keeping the explorer and the canvas in step without loading the model

- **Selection.** The store's selection is the only source. The tree reveals the selection, and the canvas centres it when it is a member of the active diagram.
- **What the canvas loads.** The canvas loads only the members of the active diagram. Relations between members are found from the relation ends in the index (E5), not by loading every relation. The member documents arrive through batched reads (E5b) in chunks of up to 200 ids. The canvas's working set is therefore bounded by the diagram (at most 300 entities and their relations), whatever the model's size.
- **What the tree loads.** The tree never loads full elements. Hovering or focusing a row for 300 ms prefetches that element's document, so the inspector opens instantly.

## 4. Performance plan

### 4.1 What the index carries

`ElementSummary` today has `id`, `kind`, `name`, `package`, `tags`, `category`, `stereotypes`, `hash` and `path`. Proposed engine and contract additions (E5), all optional and nullable so older servers still work:

| Addition | For | Cost at spec scale |
| --- | --- | --- |
| `displayName` | Search and the label shown | Small |
| `database` on table, view, sequence and mapping rows | Databases and Mappings sections | 26 B per physical row |
| `entity` on mapping and table-overlay rows | Mappings section, go to definition | 26 B per row |
| `memberCount` on diagram rows | Member count on diagram rows without loading diagrams | About 15 B per diagram |
| `ends: [entityId…]` on relation rows | Entity → Relations, canvas edges without loading relations, "add related" without a references call | About 60 B × 20,000 ≈ 1.2 MB before gzip |
| (E5b) `POST /api/model/elements/read` `{ ids: [...] }` → `ElementDocument[]`, up to 200 ids | Diagram and inspector loads in one request per chunk | New endpoint |
| (E5c) `GET /api/databases/{id}/tables` → `{ tables: TableSummary[], diagnostics, partial }` (`key`, `name`, `schema`, `origin`, `entityId`, `relationId`, `isJunction`, `columnCount`) | Tables in the tree, table-name search and the coverage line, without every column | New endpoint, a projection of `DatabaseView`. Unlike `/view`, it works on a model with errors: it returns the tables that resolve, the diagnostics, and `partial: true` (see below). Target: 700 tables ≤ 300 ms server time and ≤ 150 KB gzipped |
| (E5d) `summary: ElementSummary` on `ElementChange`, required when the server supports E5d; deletions keep using `ChangeSet.deleted` (`Ulid[]`) | Patching the index in place from `model.changed` | Only changed rows |
| (E5e) `ETag` on `GET /api/model/index` (a hash of the index version), 304 on `If-None-Match`, `Cache-Control: no-cache` in place of today's `no-store` (phase2-design §4.3 and its host notes are amended to match) | Warm reopen and debounced refetches with no transfer | One header |

**Estimated index size.** At 5,000 entities, 20,000 relations, 500 enums, 300 types and about 1,000 other rows, the index has about 27,000 rows of about 250 bytes each. That is about 7 MB of JSON, about 1 MB gzipped. Parsing takes an estimated 60–90 ms on a laptop. With E5e the index answers 304 on a matching ETag, so reopening the editor costs no transfer; today it is sent `no-store` with no ETag, so without E5e every open is a cold open.

**Table summaries on a model with errors.** `GET /api/databases/{id}/view` returns `view: null` whenever the model has any error, and so does the mock (`mocks/model/generation.ts`). A tree that emptied its Databases section on every half-typed edit would be useless, so E5c resolves what it can: tables whose owning elements are valid are returned, the rest are left out, and `partial: true` plus the diagnostics say so. The editor also keeps the last complete summary per database; when a response is partial it shows the last good tables with a "stale: model has errors" badge on the database row. **Invalidation and cost:** `["tables", dbId]` is invalidated on `model.changed` only when the change set touches a kind that shapes tables (package, entity, relation, enum, value object, scalar type, database, table, view, sequence, mapping, project settings), debounced with the index refetch. Each invalidation is one resolve per database; at spec scale that is three resolves of about 700 tables each, measured in the bench and bounded by the 300 ms target above.

### 4.2 Lazy loading

- **At start:** the index, validation, the settings, and the active diagram's members (in batches).
- **Right after the tree's first paint, in the background:** the table summaries (E5c) of every database, at low priority, so the Databases and Mappings counts, the coverage line and table-name search fill in within a second or two without blocking the tree.
- **When needed:** a selected element (the inspector), hovered rows (prefetch), and references when Where used or Show on canvas asks for them.
- TanStack Query's `gcTime` for element documents drops to 5 minutes, so browsing does not keep thousands of documents in memory.

### 4.3 Tree model and virtualization

- **Index query.** `["index"]` sets `structuralSharing: false`: TanStack's default `replaceEqualDeep` would walk 27,000 rows on every refetch for nothing, since the tree patches or rebuilds by version anyway.
- **Search worker handoff.** The worker fetches `GET /api/model/index` itself (the HTTP cache or the E5e 304 makes the second fetch cheap), or, where it cannot, receives the raw response `ArrayBuffer` as a transferable. It never receives the parsed object graph through a structured-clone `postMessage`, which would cost tens of milliseconds on the main thread.
- **`explorer/tree.ts` (pure).** It builds a node map from the index: sections, domain nesting, kind groups and rolled-up counts. A build costs O(n), about 15 ms for 27,000 rows. It is rebuilt only when the index version changes, and patched in place for single-row changes.
- **Visible rows.** A depth-first walk over expanded nodes, cached. Expanding splices the child rows into the cached array, and collapsing removes the range, so the cost is proportional to the rows that change, not the model.
- **Rows.** TanStack Virtual with fixed row heights (32 or 40 px, as today), so nothing is measured. Overscan is 12. Row components are memoized by id plus draft, error and presence state.
- **Expansion state** lives in the store (`explorer.expanded`, `explorer.followSelection`). It is mirrored to `localStorage` per project name, with every access wrapped in try/catch, and keyed by element id so it survives renames and moves.

### 4.4 Incremental updates from `model.changed`

- With E5d, each `ElementChange` carries its new summary, and each id in `ChangeSet.deleted` removes a row. The index cache is patched, the tree patches the affected node and its ancestor counts, and the search worker updates its entry. There is no refetch.
- Without E5d (older server, or `truncated: true`), the current behaviour stays: a debounced refetch after 250 ms, sent with `If-None-Match`. The tree is rebuilt, and expansion and selection are kept because they are keyed by id.
- The editor's own saves keep patching from `SaveResult.current` (phase2-design §4.3).

### 4.5 Targets

The measurements assume the spec's laptop (§13), 5,000 entities and 20,000 relations, and the live mode. In CI the mock's own cost is measured, not guessed: the mock backend records its handler time per request (index serialization, validation, resolves) in `window.__mqPerf.mock`, and each CI budget is the target plus the recorded mock time for that measure, plus 25 % for CI machine noise. The mock work that ran on the page's main thread is moved off it where it can be (§5 item 4).

| Measure | Target | Spec anchor |
| --- | --- | --- |
| Editor open, warm index (304, needs E5e) → explorer usable | ≤ 1.5 s | §13 load budget 3 s cold |
| Editor open, cold index (about 7 MB, 1 MB gzipped, no E5e) → explorer usable | ≤ 3 s | §13 load budget 3 s cold |
| Index arrives → explorer first paint (tree build + first rows) | ≤ 150 ms, no main-thread task over 100 ms. Per stage: parse ≤ 90 ms, query cache write ≤ 5 ms (no structural sharing), tree build ≤ 20 ms, worker handoff ≤ 5 ms (worker fetches or takes a transferable), first rows ≤ 20 ms. Parse and the rest run as separate tasks (the build is scheduled after the parse yields) | §13 virtualized explorer |
| Table summaries (E5c), 700 tables, one database | ≤ 300 ms server, ≤ 150 KB gzipped, off the critical path | New |
| Search keystroke → ranked results painted | ≤ 50 ms p95; worker ready ≤ 500 ms after the index | §13 search in workers |
| Expand or collapse any node (including one with 5,000 children) | ≤ 16 ms (one frame) | §14 keyboard-friendly |
| Scroll the tree, fully expanded | 60 fps, no long tasks over 50 ms | §13 |
| Select on canvas → row revealed in tree | ≤ 50 ms | New |
| `model.changed` for one element → tree and search updated | ≤ 16 ms with E5d; ≤ 400 ms including the debounce without it | §14 sync |
| 300-node diagram: open → first paint | ≤ 2 s (two to eight batched reads) | §1 300 nodes |
| 300-node diagram: pan and zoom | 60 fps (≥ 50 fps measured in CI) | §1 |
| 300-node diagram: ELK auto-layout | ≤ 3 s in the worker, UI responsive throughout | §4.9 (200 entities under 2 s) |
| Memory, index + tree + search index, page and workers | ≤ 150 MB, measured with `performance.measureUserAgentSpecificMemory()` in the Playwright `scale` project (Chromium, cross-origin isolated preview), which includes worker heaps; `performance.memory` is not used because it is Chrome-only and excludes workers | New |

## 5. A large mock dataset

The goal: the owner and Playwright can feel 5,000 entities and 700-plus tables in `npm run dev`, before the real container.

1. **The generator gains a model-only verb and shape options** (`bench/Maquettiste.Bench`). The command is `write-model --out <dir>`, alongside the existing options, plus:
   - `--domains 40 --domain-depth 3`: nested packages, so parents are set. The generator's packages are flat today.
   - `--diagrams 150 --diagram-size 20..300`: diagrams as subject areas, one or more per domain. The generator writes none today.
   - `--designed-tables 60 --views 40 --sequences 30`: tables that no entity owns.
   - The existing three databases (main, reporting, edge) stay. The output is deterministic, written by the same canonical writer, so it is engine-valid. It uses the same `SyntheticModelOptions`, so the benchmark and the editor exercise one generator (§13).
2. **`src/editor/scripts/gen-scale-model.mjs`** runs `dotnet run -c Release --project ../../bench/Maquettiste.Bench -- write-model --out ../../tmp/scale …`. When `dotnet` is not on the path, it falls back to the `mcr.microsoft.com/dotnet/sdk:10.0` image. It packs `.maquettiste/**` into one JSON file, `src/editor/.mock-data/large.json` (gitignored; an estimated 20–30 MB, about 2 MB gzipped). That file is served only in mock mode by a small Vite plugin at `/__mock/large.json` (its `configureServer` hook for `npm run dev`, and a `configurePreviewServer` hook for `preview:mock`, which CI's Playwright mock project runs against), copied into `dist-mock` only by `build:mock` (`vite build --mode mock --outDir dist-mock`), and never included by `pack-site.mjs`. The same preview hook sends `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp` for the `scale` project, which `measureUserAgentSpecificMemory()` needs.
3. **`?mock=large` loads that file.** `mocks/browser.ts` fetches it before starting MSW, and `MockBackend` seeds from it. The in-browser 200-entity `largeSeed()` is renamed `?mock=medium` (no Playwright spec uses `large` today). When the file is missing, the editor shows a banner that names the script to run.
4. **The mock backend gets ready for 27,000 elements.** Otherwise the scale tests would measure the mock, not the editor:
   - a reverse reference index built once and patched on each write (`refs.ts` scans the model today);
   - the index response cached per model version, with an ETag (E5e);
   - incremental validation: a write re-validates only the changed entries and the entries that reference them, and keeps the other diagnostics (`backend.ts` schedules `model.validate()` over every entry, via `store.ts` `diagnosticsOf(this.entries.values())`, on each write today);
   - sub-element ids (attributes, enum members) indexed once and patched on write, so `owner(id)` is a map lookup instead of a scan of every entry (`store.ts`);
   - the resolved `DatabaseView` cached per model version, and E5c answered from it (`generation.ts` re-runs full validation and `resolveDatabase` on every call today);
   - `MockBackend` run in a dedicated worker, with the MSW handlers forwarding each request to it, so what cost remains stays off the thread being measured;
   - handler times recorded in `window.__mqPerf.mock` (index, validate, resolve, per request), which set the CI allowance (§4.5);
   - E5 to E5e implemented, so the editor code under test is the code that will run live.
5. **The same model in the real container.** `tmp/scale` can be opened with the compose recipe in phase2-design §6.4 (copy `packs/`, then `docker compose … --project-directory tmp/scale up -d`). The owner can then compare the mock with the engine.
6. **CI.** The editor workflow's e2e job adds `actions/setup-dotnet` and caches `.mock-data/large.json`, keyed on a hash of `bench/**`, `src/Maquettiste.Engine/**` (the canonical writer spans `Json`, `Writing`, `Loading` and `Model`), `schemas/v1/**` and `src/editor/scripts/gen-scale-model.mjs`. A cache hit skips .NET.
7. **Measurements.** The editor records `performance.mark`/`measure` pairs for index-received, tree-built, first-row-painted, search-answered and diagram-painted. It exposes them in mock mode as `window.__mqPerf`, which the Playwright spec reads.

## 6. Migration

The order is chosen so that each step is measured against the large dataset from the start, and so that the explorer works after every step: every index row is reachable in the tree after each step (an acceptance test below checks it), and nothing is removed before its replacement ships. The sizes are rough, for one engineer or agent. Steps marked **contract** need engine and functions work outside `src/editor`, and must go through `docs/api/openapi.yaml` and the contract tests first. Each editor step works without them, degrading to today's behaviour.

| # | Step | Files | Size |
| --- | --- | --- | --- |
| 1 | Large dataset and perf harness | `bench/…/Program.cs`, `Synthetic/SyntheticModel.cs` (options above); `src/editor/scripts/gen-scale-model.mjs`, `vite.config.ts` (plugin), `mocks/browser.ts`, `mocks/backend.ts`, `mocks/model/seed.ts`, `mocks/model/refs.ts`, `mocks/model/store.ts` (incremental validation, sub-element id index), `mocks/model/generation.ts` (cached `DatabaseView`), new `mocks/worker.ts` (MockBackend off the main thread), `lib/perf.ts` (`__mqPerf`, including `__mqPerf.mock`), `.gitignore`, `.github/workflows/editor.yml` | 2–3 days |
| 2 | Contract E5 to E5e | `docs/api/openapi.yaml`, engine `Model/Documents.cs` and `ModelIndexer.cs` (`displayName`, `database`, `entity`, `ends`, `memberCount`), `ElementChange.summary`, E5c on invalid models, the index ETag and `Cache-Control: no-cache` (E5e) in the functions handlers and host, `src/editor/src/api/schema.d.ts` (generated), mock handlers (index ETag and 304, E5c), phase2-design §4.3 amendment (index caching, `["tables", dbId]` invalidation) | 3–4 days (**contract**) |
| 3 | Tree model | New `explorer/tree.ts` (build, counts, flatten, reveal path, patch), including an interim Databases section built from index rows only (database → Views, Sequences, customised tables and customised mappings; physical rows without `database`, before E5, go under an "Other" node in that section), the Settings section (§1.6), People and access, and Other elements; new `model/labels.ts` (the kind → group table, §1.2). `explorer/filter.ts` is **not** touched: `Explorer.tsx` still calls `explorerRows` | 2 days |
| 4 | Tree rendering and keyboard | `explorer/Explorer.tsx` split into `Explorer.tsx`, `TreeRow.tsx`, `useTreeKeyboard.ts`, `RowMenu.tsx`, and switched to `tree.ts`; `explorerRows` deleted in the same change and `explorer/filter.ts` reduced to filter predicates; `state/store.ts` (`explorer` slice); `app/navigation.ts` (`reveal` expands ancestors). Ships the Settings section and the interim Databases section, so no row loses its place when "Project" goes | 2–3 days |
| 5 | Naming pass, **after the owner answers question 5 (§7)** | `model/labels.ts` (all user-facing strings), `model/model.ts`, `app/Rail.tsx`, `palette/CommandPalette.tsx`, `workspaces/entities/EntitiesWorkspace.tsx`, `workspaces/entities/dialogs.tsx`, `inspector/*` (package field label), `workspaces/settings/SettingsWorkspace.tsx`, e2e selectors; in the same change SPEC.md §14 wording and glossary (the owner's call) and phase2-design §4.8, so the spec never contradicts the UI. Until then steps 3 and 4 use the current words from `labels.ts` | 1 day |
| 6 | Search worker and results mode | New `search/worker.ts`, `search/rank.ts`, `search/client.ts`; `explorer/Explorer.tsx`; `palette/CommandPalette.tsx` uses the same client | 2–3 days |
| 7 | Filters | `explorer/filter.ts` (kind, domain scope, category subtree, has errors, on diagram); filter bar component | 1 day |
| 8 | Canvas in step | `canvas/model.ts` (`viewElements` from `ends`; "All of X" capped at 300), `api/queries.ts` (batched reads, falling back to chunked GETs with 16 in flight), `workspaces/entities/EntitiesWorkspace.tsx` (tree drag and drop, membership dots, "Show on canvas") | 2–3 days |
| 9 | References tab, breadcrumbs, go to definition, history | New `references/ReferencesPanel.tsx`, `app/Breadcrumbs.tsx`; `app/BottomPanel.tsx`, `app/shortcuts.ts`, `inspector/*` (F12 on reference fields) | 2 days |
| 10 | Incremental index patching | `api/queries.ts`, `realtime/events.ts`, `explorer/tree.ts` (patch), search worker update message | 1–2 days |
| 11 | Databases and Mappings sections, full | `explorer/tree.ts` (the interim Databases section enriched with the table summaries: Tables grouped by schema and domain, coverage line), `api/queries.ts` (`["tables", dbId]`, background load after first paint, last-good fallback and stale badge, invalidation by kind), Database screen scoped to a table and its neighbours (cap 300) and the list-and-DDL form above 300 tables | 2–3 days |
| 12 | Docs | This file marked as done; any remaining phase2-design notes | 0.5 day |

Total: about 22–27 working days, of which 3–4 are contract work.

**Acceptance tests**

- **Unit tests (Vitest), `tree.ts`:**
  - Catalog nests under Billing.
  - No "Project" group, and "Not in a domain" appears only when non-empty.
  - A top-level package (`package: null`) is a top-level domain, not an orphan; an entity with no package is an orphan.
  - Every index row is reachable in the tree, on the billing fixture and the medium seed (run as part of every migration step from step 3 on). This covers databases, views, sequences, table overlays, mappings and vocabularies with and without the E5 `database` field.
  - An unknown kind lands in Other; an actor lands in People and access.
  - Rolled-up counts are correct.
  - A parent cycle does not loop.
  - Kind groups are hidden when empty, and the Auto option applies at 12 or fewer when a domain is first expanded, and does not change when a 13th element is added to an expanded domain.
  - Expand and collapse splice the right ranges.
  - The reveal path is right.
  - Property test: patching one change gives the same tree as a full rebuild, on 1,000 random changes over the medium seed.
- **Unit tests (Vitest), other modules:**
  - `rank.ts` ordering fixtures (exact > prefix > word-start > substring > fuzzy, and the tie-breakers).
  - Filter predicates.
  - Batched reads chunking and falling back.
  - Index patching from `ElementChange.summary` and from `ChangeSet.deleted`.
  - Table summaries on an invalid model: E5c returns the tables that resolve with `partial: true`, and the tree shows the last good tables with the stale badge (mock contract suite and a Vitest query test).
  - Mock contract suite extended to E5 to E5e.
- **Component tests:** the tree's ARIA attributes (`aria-level`, `aria-setsize`, `aria-posinset`) on rendered rows after scrolling, and every key in §3.4.
- **Playwright on the mock billing model** (every push):
  - The tree shows Domains › Billing › Catalog.
  - Databases › main › Tables lists the invoice table under Billing.
  - Keyboard-only walk: expand Billing, open Invoice, F12 on a relation end, Shift+F12.
  - Search "invli" puts InvoiceLine first.
  - Drag Product onto the diagram.
  - Axe scan of the explorer in both themes.
  - Copy check: no visible tree label (tooltips excluded) contains a word from the jargon list (enum, scalar, convention, package, workspace).
  - A domain-named group under Databases shows the domain icon and the "Domain: Billing" tooltip.
- **Playwright on `?mock=large`**, a new project `scale` (every push, with the measured mock allowance of §4.5):
  - Explorer usable and first paint under target.
  - Search for a known entity: top result, under 50 ms plus the allowance.
  - Expand the largest domain, then scroll to the end with no long task over 100 ms.
  - Reveal from a canvas selection.
  - Open a table from the Databases section: the Database screen draws 300 table nodes or fewer.
  - Open a 300-node diagram: painted under 2 s plus the allowance, and a pan sampled at ≥ 50 fps with a `requestAnimationFrame` counter.
  - One simulated external change patches the tree without an index request (asserted on the network log).
- **Live check** (manual, before the phase closes): the same Playwright `scale` spec with the `live` project against `tmp/scale` in the container. The numbers are recorded in the bench report.

## 7. Open questions for the owner

1. **Domain hierarchy.** How deep does your hierarchy go, and what shape is it? For example 40 flat domains, or about 8 areas with 5 domains each and some sub-domains below? Does a domain that has sub-domains also hold entities directly?
2. **Tables.** Should tables show under their database, grouped by domain as proposed, under their domains next to the entities, or both? And across how many databases are your 700 tables spread? Is one entity often mapped into several databases?
3. **Names in search.** How do you look things up: by business name ("Invoice line"), by table name (`ar_inv_line`), or by a code or prefix? This decides which fields search ranks first, and whether elements need an "aliases" property.
4. **Diagrams.** Does every diagram belong to one domain, as the file format allows today, or do you keep many cross-domain diagrams that need their own folders?
5. **Words.** Are "Domain", "Sub-domain", "Screen" and "Domain model" (for the Entities screen), and "Relationships", "Lists of values", "Shared types", "Mapped automatically" and "Customised", the words you would use? The naming step waits for this answer. Should "Domain" also replace "package" in generated-code docs and the CLI's help text, or only in the editor?
6. **Your real shape.** Could you share an anonymized export of one large project, such as element counts per domain and a DBML or DDL dump with renamed identifiers? It would let the synthetic generator match your real distribution rather than a guess.

## 8. Additions from the owner's review (2026-09-28)

These came up while the proposal was being written and reviewed; they are part of it.

### 8.1 Patterns borrowed on purpose

| Source | Pattern | How it lands here |
| --- | --- | --- |
| LLBLGen Pro | Entity Model / Relational Model Data / Model Views as the top-level split; groups with per-kind folders and counts | The Domains, Databases and Diagrams sections; tables under their database, not under a domain; an entity's mapping is a tab on the entity |
| LLBLGen Pro | Element Search with expressions; a validation panel where every error links to its element and a fix | Ranked search (§3) with filter expressions; Problems rows open the element and offer the fix |
| LLBLGen Pro | Model Views as saved subsets with "include related" | Diagrams as subject areas with "add related" (§1) |
| LLBLGen Pro | Quick Model, a text DSL beside the canvas; generation presets | A text pane for the selected element (canonical JSON first, a DSL later); named generation presets in the Generate screen |
| SysML v2 | Views defined by criteria, not by hand-picked membership | An optional filter on a diagram file ("every entity tagged billing") kept current by the engine |
| SysML v2 | Definitions versus usages; metadata definitions; library packages | Relation ends shown as usages with role names; stereotypes and tags as metadata; shared vocabularies and packs as libraries |
| PowerDesigner, ERwin | Domains as reusable attribute types; impact analysis across layers | Custom types; "where used" that follows type to attribute to column to template unit |
| Database IDEs | Lazy nodes with counts, a pinned filter box, scroll-from-source, favorites and recents | §1 and §3 as written, plus favorites and recents in the tree header |

### 8.2 Reference data inside a domain

The owner's projects hold about 800 lookup and reference types across their domains: plain key-value sets, sets
that tenants extend on top of a root set, and lookups with dozens of attributes. Closed sets stay enums. Everything
extensible or attribute-heavy is an entity carrying a `lookup` stereotype family (`lookup`, `tenant-extensible`),
with the root set as Seed rows and the tenant extension structures generated by the DDL and repository packs. In the
tree, each domain gets a **Reference data** group beside Entities, holding its enums and its `lookup` entities, with
counts, so 800 of them never crowd the business entities. Seeds move from phase 4 to the phase that ships this
section, because the root sets need them.

### 8.3 Agents as entities

Agent definitions are entities with an `agent` stereotype and an extension schema (SPEC Section 18): typed
properties the inspector renders as a form, generated by packs like any other entity. They appear under their domain
in an **Agents** group. Promotion to an element kind waits until a template needs more than a stereotyped entity can
express; processes (phase 3) give agents a role as actors.

### 8.4 Localization

Display names (singular and plural) and descriptions, at every level down to enum and lookup members, are localized:
the element file holds the project's default language, one sidecar per locale under `model/locales/<locale>.json`
holds the rest keyed by element and member id, and long descriptions get per-locale Markdown sidecars. Validation
reports per-locale completeness; template helpers take a locale with a fallback chain; packs emit resource bundles
and, for lookups, translation tables. In the editor: a Translations tab in the inspector and a per-locale
completeness view. Open: a separate short `hint` field beside `description`. This is a spec addition (errata E6) and
its own workstream; the tree shows translated display names in the user's locale once it lands.

### 8.5 Agents and the editor API

Edits made on disk by an external tool already reach the editor through the file watcher and `model.changed`
(verified on the running container). The MCP server over the editor API (SPEC Section 18) is pulled forward ahead
of the in-app assistant: it gives tools such as Claude Code the validated save path, batches, plan and apply and
where-used, on the user's own plan. The in-app Assist panel follows, on the host's AI hook.
