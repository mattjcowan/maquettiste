# Explorer redesign: organizing the model at scale (proposal)

Status: proposal for the owner's review. Stages S1 (the contract additions E5 to E5e, §4.1) and S2 (the large mock dataset and the Playwright `scale` project, §5) are built; the explorer, search, editors and screens described here are not. Scope: the editor's left-hand explorer, the words the editor uses, search and navigation, the element editors the explorer opens, the Reference data screen, and the data all of these need. The model side of reference types, seeds and localization is designed in `reference-types-seeds-localization.md` (the binding design for those features); this document covers only how the explorer and the editor present them. It builds on SPEC.md §1 (5,000 entities, 20,000 relations, 300 nodes at 60 fps), §5 (layers), §10, §13 ("Editor at scale"), §14 and §18, and on phase2-design.md §4.8 and §4.9. File-format names (`kind: "package"`, the `package` field, `model/packages/`, `schemas/v1/package.json`, `ElementSummary.package`) do not change.

**Revision of 2026-09-28.** The owner answered the first round and asked for a nested, self-describing tree, a databases explorer, an entity editor that keeps its tab across elements, and search across every kind. This revision: makes the tree nested and self-describing (a Project node, a **Domain model** root with domains as nested packages and one folder per kind, a **Databases** root nested database › schema › object, **Diagrams** outside the domains); drops the Auto option that flattened small domains and the separate Mappings section; adds search operators (contains, starts with, like, equals), related-element highlighting, context menus with multi-selection, and entity editors with a General mode; takes reference types out of the tree into a **Reference data** screen while keeping them in search; keeps enums a kind; places seed data and localized labels in the explorer (their model design is in `reference-types-seeds-localization.md`); brings the performance plan, the mock dataset and the migration up to date with what is built; and lists the prior art and the patterns refused (§8.1, §8.2). Later the same day the owner settled the shell and the vocabularies: a rail of icons, each selecting one explorer for the sidebar, with Settings at its foot (§1.0, replacing the Project node and the Settings root), and tags and categories both global and per domain (§1.11). Sections that changed carry a "Revised 2026-09-28" note.

**Two principles this document follows** (the owner's):

1. **The engine models intent; templates decide persistence.** Nothing here asks the engine to synthesize tables, columns or constraints for a feature. Where a feature could have a physical form (reference types, seed data, translations, multi-value fields), that form is chosen by the templates; at most a project may pick an optional per-project mapping strategy whose default is "template-defined". The Databases root shows the physical layer the model already has (designed and imported tables, and the tables SPEC §10's existing conventions resolve); it never shows tables invented for these features.
2. **The product reserves no vocabulary.** No stereotype, tag or category name has built-in meaning. Projects mark elements with their own stereotypes, tags, categories and custom properties; where the explorer should behave differently for marked elements (a folder of its own, an icon), that is configured in project settings (§1.6, §8.6). Descriptions of the owner's own projects below are context, not requirements.

## 0. What is wrong today

*Revised 2026-09-28: states what S1 and S2 changed.*

- `explorer/filter.ts` groups every index row flat by its `package`, one level deep. `Catalog` is a child of `Billing` in the fixture (`"parent"` in `packages/catalog.json`), but renders as a sibling. Elements with no package fall into a group labelled "Project", which is not a thing the user created.
- Inside a group, all kinds (diagrams, entities, relations, enums, databases, tables, mappings, vocabularies) are interleaved in one list. Table files, views, sequences, databases, mappings and vocabularies have no package, so they all land in "Project" (tables resolved from conventions are not index rows, so they never reach it). On the scale mock the largest group is a package of 694 rows, and it is already slow to expand (§4.5). The reader cannot tell from a row's place what kind of thing it is.
- Nothing is collapsed by default, and the only counts are on the flat groups.
- The text filter is a substring match on the name, run on the main thread over every row, with no ranking and no operators.
- Two scale problems sit behind the tree. The virtual "Package: X" canvas view (`canvas/model.ts`, `viewElements`) loads **every** relation in the model to find the edges between that package's entities: 20,000 element GETs at spec scale. And `useElements` (`api/queries.ts`) issues one GET per id, so a 300-node diagram with its relations is about 1,000 requests. The server side of both fixes is built (relation `ends` in the index, the batched read `POST /api/model/elements/read`), and `api/endpoints.ts` has `readElements` and `getDatabaseTables`, but no query uses them yet.
- Any `model.changed` event the editor did not cause refetches the whole index (phase2-design §4.3), about 10.8 MB of JSON on the scale model. The server now tags the index (E5e) and sends a summary with each change (E5d), but the editor sends no `If-None-Match` and does not patch from summaries yet.

## 1. Information architecture

*Revised 2026-09-28: rebuilt as nested, self-describing trees for the domain model and the databases; then a rail of explorers (§1.0) in place of the Project node and the Settings root, and domain-scoped vocabularies (§1.11).*

### 1.0 The shell: a rail of explorers

*New 2026-09-28 (the owner's decision).* The shell is a rail of explorers, which SPEC §14 calls the workspace switcher:

- **Rail.** A narrow strip of icons. From the top: **Domain model**, **Reference data**, **Databases**, **Diagrams**, **Generate**. At the bottom: **Settings** (a gear, the project's settings, §1.6) and the account menu with sign out.
- **Sidebar: one explorer.** The sidebar shows the one explorer the selected icon names, under a header that says which, with its totals ("Domain model · 40 domains · 5,000 entities"), so the tree always says what it is browsing. Each explorer keeps its own expansion, scroll and filter. Generate's sidebar lists packs and targets; its screen is outside this document. Below, "the Domain model root", "the Databases root" and "the Diagrams root" mean the tops of the Domain model, Databases and Diagrams explorers.
- **A second explorer** pinned beside the first (for example Domain model and Databases side by side) is a per-user preference, off by default.
- **Centre and bottom.** The centre holds editor tabs opened from any explorer (element editors §3.6, diagrams, the Database screen, a reference type, Settings); they stay open when the rail changes. The bottom panel (Problems, Output, diffs, References) is shared by every explorer.
- **Selection follows the active tab** and outlives rail switches, so highlighting (§1.9) and "mapped by" (§1.3) show in whichever explorer is visible: select the `invoices` table in Databases, switch to Domain model, and Invoice is tinted.

### 1.1 The tree, and the rule that you always know what you are browsing

Each explorer is one virtualized tree (§1.0). There is no Project node: the project's name is in the top bar, and Settings and Generate are rail icons. The explorers, each named after what it holds:

| Explorer | Spec layer | Holds |
| --- | --- | --- |
| **Domain model** | Conceptual | Domains nested as packages, at any depth; in each, one folder per kind (§1.2) |
| **Reference data** | Conceptual | The reference types by category; a type opens as a centre tab (§1.7) |
| **Databases** | Physical | Database › schema › Tables, Views, Sequences › columns and constraints (§1.3) |
| **Diagrams** | View | Every saved diagram, kept outside the domains because a diagram spans them (§1.5) |

Settings is not a tree: the gear opens the Settings screen (§1.6). The tables and the diagrams could have been nodes under the domain model; Maquettiste gives them explorers of their own on purpose: SPEC §5 separates the conceptual, physical and view layers, and an explorer per layer keeps that separation visible.

Two more top-level rows of the Domain model explorer appear only when they have members, last: **People and access** (actors and permissions: conceptual, but SPEC §5 gives them no package, so they are never in a domain and never orphans), then **Other elements** (kinds with no package that this editor version does not know).

**The rule.** Every row tells the reader what it is, from its own label or its parent's:

1. A folder is named by what it holds: a kind in the plural ("Entities"), or a domain, database or schema name with that thing's icon and a count phrase that names kinds ("2 sub-domains · 41 entities"), or a project-defined folder (§1.6), whose tooltip states its condition and kind.
2. Every element row sits in a folder named after its kind, or in a project-defined folder that holds a single kind. There are no mixed folders, and no option that flattens a small domain (the first proposal's Auto option is dropped: it traded one click for rows whose kind the reader had to guess).
3. Domains, databases and schemas are containers, not elements of a folder: each carries its icon, its count phrase and the tooltip "Domain: Billing", "Database: main (PostgreSQL 16)", "Schema: public". A domain-named group met outside the Domain model root (under Tables when grouped by domain, under Diagrams) shows the same icon and tooltip.
4. A row shown away from its place (a search result, a highlighted related row, the References tab) always shows its path: `Domain model › Sales › Orders › Entities › Order`.
5. Each domain and folder starts collapsed and shows its count; the explorer's header shows the totals. At first open the rail selects Domain model and the tree shows its top-level domains. The Reference data icon appears from step 7 of `reference-types-seeds-localization.md` §5. The sketch shows three explorers; the sidebar shows one at a time.

```
DOMAIN MODEL                                 40 domains · 5,000 entities   (the explorer's header)
  ▸ Not in a domain                          3             (only when non-empty; always first)
  ▾ ◇ Billing                                1 sub-domain · 4 entities   (Enter opens General, Tags, Categories, §1.11)
    ▸ ◇ Catalog                              1 entity
    ▸ Entities                               4
    ▾ Relationships                          4
      ▾ contains                             Order → Product
        ▾ Attributes                         2
            quantity                         decimal(12,3)
            unitOfMeasure                    Unit of measure · reference data · 0..1   (F12 opens the type)
    ▸ Enums                                  1
    ▸ Value objects                          2
    ▸ Custom types                           1
    ▸ Seed data                              2
    ▸ Agents                                 3             (a project-defined folder, §1.6)
  ▸ ◇ Sales                                  3 sub-domains · 212 entities
  ▸ People and access                        Actors 4 · Permissions 30   (only when non-empty)
DATABASES                                    3 databases · 1,184 tables
  ▾ ▣ main   PostgreSQL 16                   712 tables · 12 views · 3 sequences · 20 customised mappings
    ▾ ▤ public                               700 tables
      ▸ Tables                               700
      ▸ Views                                12
      ▸ Sequences                            3
    ▸ ▤ audit                                12 tables
    ▸ Customised mappings                    20
DIAGRAMS                                     146
```

The counts on collapsed Databases rows come from the index (databases, views, sequences, table files, mapping files) until the table summaries (E5c) load in the background after the tree's first paint (§4.2); until then those rows show the index counts and a small spinner.

### 1.2 Domain model: nested domains, one folder per kind

- **Nesting.** The Domain model root's children are the top-level domains (packages with `package: null` in the index, as the fixture's Billing). A domain's children are its sub-domains (packages whose `parent` is this package), sorted by name, then its kind folders. The tree is built from `ElementSummary.package`, which for a package already holds its parent (`ModelIndexer.PackageOf`). No contract change is needed for nesting. Sub-domains are listed directly rather than in a "Sub-domains" folder: they are the same kind of node as their parent, their icon and count phrase say so, and a folder would add a level at every depth (question 2 in §8.8).
- **Not in a domain** is the first child of Domain model, before any domain, and appears only when it has members: conceptual elements whose kind belongs in a domain but that have no package. A broken `parent` chain (a cycle, or a parent that does not exist) is shown there too with a warning icon; validation already reports it, and the tree must not loop. Elements of other layers are never here, because each has its own explorer.
- **Counts.** A domain's count phrase rolls up everything beneath it ("3 sub-domains · 212 entities"); a folder's count covers only its direct members. Rolled-up counts are computed once per index version, bottom-up.
- **Kind folders are data, not code.** One table in `model/labels.ts` maps each kind to its folder label, tooltip, icon, order and placement rule. Order: Entities, Relationships, Enums, Value objects, Custom types, Seed data (seeds are elements, `kind: "seed"`, several per target; a seed of an entity or a relation is placed in its target's domain; seeds of reference types appear only in the Reference data screen), then the later SPEC §5 and §10 kinds as they land (Processes, Operations, Business events, Queries, Projections), then the project-defined folders (§1.6). A folder with no members is hidden; the domain's context menu still offers "New entity", "New enum" and the rest, so an empty folder is never needed as a drop target. A kind the table does not know goes in an **Other** folder in its domain when it has a `package`, and otherwise in the Other elements root.
- **Enums stay a kind** with their own folder (the owner's decision). An enum is a closed set whose members are part of the code; a reference type (§8.3) is a set of rows managed as data, with fields of its own. Reference types are not in this root.
- **Element children.** An entity row expands into folders of its own, one per kind of child:
  - **Attributes (n)**: each with its type in the secondary text. Needs the entity's document; expanding loads it through the batched read, and hovering the row for 300 ms prefetches it (§4.2).
  - **Relationships (n)**: every relation with this entity at one of its ends, including relations from other domains (each shows its domain path in the secondary text). From the index `ends`; no load.
  - **Seed data (n seeds · m rows)**: the seeds whose `target` is this entity (§8.4), from the index `target` and `rowCount`. Opens the seed grid. Like Relationships, this is a navigation view: each seed's place is the Seed data folder.
  - **Mappings**: one row per database the entity is mapped into ("main → invoices", "reporting → invoice_facts", marked customised when a mapping file exists). From the table summaries' `entityId` and the index `entity` on mapping rows.
- A relationship row expands into **Ends** (the entities, with role and cardinality, from the index) and **Attributes (n)** when it has any. A relation attribute can be typed with a reference type, single or multi-value, required or not (the owner's example: `contains` with `quantity` and `unitOfMeasure`, where Unit of measure is a reference type); its row names the type, "reference data" and the cardinality (`1`, `0..1`, `many`, `0..many`, as the Used by tab's badges show it), and Go to definition opens the Reference data screen on that type. Relation ends stay entities.
- An enum row expands into its **Members**; a value object into its **Attributes**.

### 1.3 Databases: database, schema, object

- **Shape.** Databases › database › schema › **Tables**, **Views**, **Sequences** › each object › **Columns**, **Primary key**, **Foreign keys**, **Unique constraints**, **Indexes** (and a view's **Columns**). There is no level between a database and its schemas: a Maquettiste database is one namespace of schemas. A schema node always shows the schema's own name when it has one (`public`, even when it is the database's only schema, from `DatabaseView.defaultSchema` or the table's `schema`). A **Default schema** node appears only when the dialect has no schemas (SQLite) or a table's schema is null, so the depth is the same everywhere.
- **Where the rows come from.** A database node shows its dialect and version. The list of tables comes from the table summaries (E5c, built): `key`, `name`, `schema`, `origin`, `entityId`, `relationId`, `isJunction`, `columnCount` (`isLookup` is deprecated and always false: the enum lookup-table option is retired, MQ7012, §4.1 "As built (E5i)"). Tables resolved from SPEC §10's conventions have no file, or a file holding only overrides (`origin`); designed and imported tables are files. The Databases root never shows tables for reference types, seed data or translations: their physical form belongs to the templates (principle 1), and the generated DDL is seen in the Generate screen and the DDL preview. The children of a table (columns, keys, constraints, indexes) need one table's detail, which today only `GET /api/databases/{id}/view` returns, with every table of the database; §4.1 proposes E5f, one table's `TableView`.
- **Order inside Tables.** Sorted by name, flat, as database tools commonly list them. Two scales matter: the owner's databases of about 700 tables, and the 10,005-table `main` of the bench model and `?mock=large` that CI exercises. At 700 tables a virtualized list with type-ahead and the search box is quicker than folders; at 10,005 the flat list still works (virtualized rows, type-ahead over a sorted array) but is long enough that Group by domain may be the better default (§8.8 question 4). A view option, **Group by domain**, puts tables in folders named after the domain of the entity that owns them (`entityId`, then that entity's `package`), with junction tables under the relation's domain, and a **Not linked to an entity** folder reserved for designed and imported tables with no owning entity or relation. With the option off, those tables carry a small "unlinked" marker instead. There is no enum lookup-table grouping or "lookup" marker: the enum lookup-table option is retired (MQ7012), and a reference type's lookup table is a template's object, not a resolved table.
- **Mapped by.** Selecting a table highlights, in the Domain model root, the entity or relation mapped onto it (from `entityId` and `relationId`); selecting a column highlights the attribute (`ColumnView.attributeId`). The reverse also holds: selecting an entity highlights its tables. With one explorer visible, the tint shows after a rail switch, since the selection persists (§1.0); with a pinned second explorer, at once. It follows the highlighting preference (§1.9). Collapsed folders that hold a highlighted row show a "2 related" badge instead of expanding.
- **Context actions**: on a table, "Go to entity", "Create entity from table" (the reverse-engineering path; for a designed or imported table with no entity), "Copy SELECT" and "Copy DDL"; on a schema or Tables folder with several tables selected, "Create entities from tables…", one batch.
- **Opening a table** opens the Database screen scoped, never on the whole database: the table and its foreign-key neighbours to depth 1, widened to the rest of that table's domain while the total stays at 300 tables or fewer (SPEC §1's node budget; SPEC §13: diagrams are subject areas, never the whole model). The table is focused and its DDL shown. The screen's own "whole database" choice is offered only for databases of 300 tables or fewer; above that it shows a virtualized list with DDL instead of a canvas.
- **As built (step 13, editor side, 2026-09-29).** Expanding a table row fetches its detail (`tableDetailQuery`, key `["tables", dbId, "detail", key]`, so the debounced table invalidation refetches it; E5f, falling back to `/view` on a 404 or 405) and adds Columns, Primary key, Foreign keys, Unique constraints and Indexes (`tableChildren`); a column row carries `attribute` (`ColumnView.attributeId`). Clicking a table or a column sets the store's `explorerItem` (selected in place; selecting elements clears it), and `relatedKeys` tints the mapped entity or relation and, for a column, the attribute row `<entity row>/attributes/<attributeId>` once it exists. Enter or a double click opens the Database screen focused on the table (the store's `databaseTable`), which gains a Tables section (the summaries filtered by every word, sorted, the first 300; `workspaces/database/tableList.ts`). **Not built:** the scoped canvas (neighbours to depth 1, cap 300) and the list-and-DDL form above 300 tables (the canvas still reads `/view`); "Create entity from table", "Copy SELECT", "Copy DDL"; revealing an enum from a column (`enumId` is withdrawn with the enum lookup-table option, MQ7012; an attribute's enum type would need the entity's document). Tests: `tests/unit/table-children.test.ts`, Playwright `explorer-tables.mock-only.spec.ts`.
- **Status, round 7 (2026-09-29): mapping is explicit (engine-design.md D46, errata E24).** A database holds only what is mapped to it. The New database dialog asks "Map domains by convention: none (default), pick domains, all" and always writes `byConvention`. A database that holds nothing shows one hint row, "Nothing is mapped here yet: map domains or entities from their menus or the Mapping section of this database's inspector". The domain and entity menus offer **Map to database…** (multi-selection too): a domain joins the database's convention list, an entity gets a mapping element (an ignoring one stops ignoring). The database's inspector has a **Mapping** section: what the convention takes, the entities mapped one by one, the convention editor, and for a file without the member "By convention: all domains (unspecified)" with **Make explicit**. The Playwright spec `database-mapping.mock-only.spec.ts` walks it.
- **Status, phase 3 round P3 (2026-09-30): processes.** The rail has **Processes** after Domain model (processes by domain, then Actors; states, events and scenarios under each process), with New process…, New actor…, New scenario… and Import XState… in the + menu and the domain group's menu, and the §1.8 row menu for processes, actors and scenarios. Process, actor and scenario editors follow the §3.6 frame and the keyboard model; the Problems panel offers the quick fixes of phase-3-design.md §3. See phase-3-design.md §6.1, §6.2 and the P3 status lines of §9. *Round P4 (2026-09-30):* the process editor opens on its **Chart** tab (the statechart canvas with the simulation panel below it); **Simulate** on the process row opens it; a process's diagram (its statechart, phase-3-design.md §2.7) is listed in the Diagrams explorer under the process's domain as "statechart of <process>" and opens the process editor's Chart tab; the Domain model canvas never shows it.
- **Customised mappings** is a folder under each database listing the mapping files for that database (from the index `database` on mapping rows), grouped by domain when the option is on, each labelled "Invoice → invoices". It replaces the first proposal's Mappings section (§1.4).

### 1.4 Mappings

*Revised 2026-09-28: no longer a root.* An entity's mapping belongs on the entity (its editor's Mappings tab), not in a tree root, and a Mappings root duplicated what the Databases root shows. So: the coverage line moves onto the database row ("712 tables · 20 customised mappings"; with the table summaries loaded, also "5,000 entities mapped · 3 left out", where "left out" is an entity in one of this database's packages that has no table because it is ignored); customised mappings are the **Customised mappings** folder under each database (§1.3); an entity's mappings are its **Mappings** child (§1.2) and its editor's Mappings tab (§3.6). The Mappings screen (SPEC §14) stays as the side-by-side view.

### 1.5 Diagrams outside the domains

*Revised 2026-09-28.*

- **Where they live.** The Diagrams root sits beside Domain model, not inside it, because a diagram spans domains; it is a root of its own because SPEC §5 keeps the view layer apart (§1.1). A diagram whose file names a `package` (its home domain) is listed in a folder named after that domain (domain icon, tooltip "Diagrams whose home is Billing"); diagrams without one are listed directly under Diagrams. Each diagram row shows its member count from `memberCount` on the index row (E5, built); the tree never GETs a diagram to label it.
- **Domain view.** The virtual "Package: X" view becomes **All of Billing** in the diagram picker, offered only when the domain has 300 entities or fewer. Above that, the picker offers "New diagram from Billing…", which opens the add-elements dialog scoped to that domain. A domain's context menu has "Diagrams of this domain", which switches the rail to Diagrams, filtered.
- **Membership in the tree.** Each tree row shows a small dot when its element is on the active diagram. The members come from the diagram document, which is already loaded.
- **From the tree to the canvas** (row context menu, plus drag and drop onto the canvas): **Add to diagram** (multi-select, one batch); **Add with related…** (neighbours to depth N, §14 "add related"; the dialog previews the node count and warns past 300); **Show on canvas** (centres the element on the active diagram, or lists the diagrams that contain it, "In 3 diagrams", from the references endpoint; `ReferenceWalker` must be confirmed to count diagram membership as the mock's `refs.ts` does); **New diagram from selection** (in the selection's domain); **Used** (§3.3; labelled "Where used" until the owner asked for one word that does not wrap in the inspector).
- **From the canvas to the tree.** With "Follow selection" on (the default), selecting a node reveals it in the tree: only its ancestors expand, and it scrolls into view.

### 1.6 Settings, and project-defined explorer folders

*Revised 2026-09-28: Settings is the rail's gear, not a tree root.*

- The gear at the foot of the rail opens the **Settings** screen as a centre tab, with tabs **General**, **Packs**, **Dialects**, **Locales**, **Tags**, **Categories**, **Stereotypes** and **Explorer folders**. *(As built, 2026-09-29: seven tabs, **Tags**, **Categories**, **Stereotypes**, **Conventions**, **Locales**, **Type maps, outputs, formatters** and **Explorer**; General, Packs and Dialects are folded into the Conventions and project tabs.)* No tree has a Settings row. The vocabulary elements (`tag-vocabulary`, `category-tree`, `stereotype`) are not listed as model elements beside entities: the global ones live in these tabs, a domain's own on the domain (§1.11). The gear ships with the tree itself, because the vocabularies have no other place once the "Project" group is gone.
- **Explorer folders** (a Settings tab and a project setting). The product gives no stereotype, tag or category a meaning (principle 2), so a project that wants its agent definitions, or any other marked set, in a folder of its own says so in project settings: a list of `{ label, icon, kind, match }`, one kind per folder (so the folder holds a single kind, as rule 2 requires), where `match` is a stereotype, tag, category (with descendants) or custom-property condition. A project that wants agents drawn from two kinds declares two folders. In each domain, an element matching a folder's condition is listed in the first matching folder instead of its kind folder, and the kind folder's count tooltip says "plus 3 in Agents". A folder's tooltip states its condition ("Entities with stereotype `acme-agent`, from project settings"; the stereotype is the project's own), so the rule of §1.1 holds. This is a SysML v2 view defined by criteria (§8.1) applied to the tree. It needs an `explorer.folders` member in `maquettiste.json` (a contract and schema addition, §6 step 4).

### 1.7 Reference data screen

*New 2026-09-28.* The owner's decision: "a single place to manage all reference data with a list of the reference data types, and then the ability to manage them and add fields if they need them." The screen is designed in `reference-types-seeds-localization.md` §4; what matters to the explorer:

- *Revised 2026-09-28.* **Reference types appear only there.** The Domain model tree lists none. The rail's **Reference data** icon (second, §1.0) shows the screen's list as its explorer ("812 types · 16,240 rows" in the header); a selected type opens as a centre tab.
- **The list is nested the same way**: a virtualized tree of category paths with counts (reference types have no package; categories group them), a flat A–Z toggle, and a search box that accepts the operators of §3.1. Tabs Fields, Rows, Used by and Storage keep their choice when another type is selected, as an element editor in General mode keeps its tab (§3.6).
- **Search across all kinds** finds reference types: the tree filter's footer ("Also 4 reference types match") and quick open list them with the path `Reference data › Measurement › Unit of measure` (§3.1), and opening one goes to the screen.
- **Highlighting**: selecting a reference type in the screen highlights, in the explorer, the owners of the attributes it types (entities, relations, value objects), as §1.9 does for selections in the tree. The index does not carry attribute types, so this needs the type's references (the Used by tab's request) and appears once they are loaded.
- **Go to definition** on a reference-typed attribute (an entity's, or a relation's such as `contains.unitOfMeasure`) opens the screen on that type; "New reference type…" is offered in every attribute type picker.

### 1.8 Context menus and multi-selection

*New 2026-09-28.* Every row has a context menu (right click, the context-menu key or Shift+F10). Multi-selection (Ctrl or Cmd+click, Shift+click, Shift+arrows, Space) is limited to rows of one kind: a Ctrl+click on a row of another kind starts a new selection. Menus then stay predictable, and SPEC §14's bulk edits ("apply a stereotype to 50 entities") are per kind anyway. The menu for a selection offers only the actions valid for every selected row.

| Row | Menu (multi-selection support marked ✱) |
| --- | --- |
| Explorer header (*revised 2026-09-28*: the Project node is gone) | Collapse all, Pin beside…, Highlight related elements |
| Domain | Open (General, Tags, Categories, §1.11), New entity, New relationship, New enum, New value object, New custom type, New sub-domain, Search in this domain, Diagrams of this domain, New diagram from this domain, Rename, Move to domain…, Delete |
| Kind folder | New (that kind), Select all ✱, Expand all |
| Entity ✱ | Open, Open in new tab, Add to diagram ✱, Add with related… ✱, Show on canvas, Used, Go to table, Apply stereotype… ✱, Tag… ✱, Set category… ✱, Move to domain… ✱, Edit seed data, Rename, Delete ✱ |
| Relationship ✱ | Open, Add to diagram ✱, Go to ends, Used, Promote to entity, Move to domain… ✱, Delete ✱ |
| Enum, value object, custom type ✱ | Open, Used, Move to domain… ✱, Delete ✱ |
| Database | New schema, New table, New view, New sequence, New routine, New database type, New SQL object, Open Database screen, Coverage report, Generate DDL |
| Table ✱ | Open, Go to entity, Create entity from table ✱, Copy SELECT, Copy DDL, Used |
| Diagram ✱ | Open, Duplicate, Move to domain… ✱, Delete ✱ |

**As built (creation, 2026-09-29).** `explorer/create.ts` (pure): the New actions (`CREATE_LABELS`; `DOMAIN_CREATE` on a domain row, `folderCreate` on a kind folder, `EXPLORER_CREATE` per explorer for the header's **+** button and the empty state, `FIRST_RUN_CREATE` for the first-run panel of an empty model), `domainOfKey` and `currentDomain` (the row's domain, else the selection's, else the open diagram's home) and `buildElement` (only the fields each schema requires: a custom type's `base`, a database's `dialect`, a relationship's two ends; a sub-domain's `parent`). `explorer/NewElementDialog.tsx` is one dialog for every kind, opened through the store's `newElement` (`requestNew`) and mounted once in the shell; it saves through `createElement` (a reference type with its seed through the Reference data screen's batch), pushes an undo entry, shows the element's explorer and opens the element. The canvas's own New entity dialog now starts on the selected element's domain, else the view's. Also built: Duplicate on a diagram row; the explorers' expanded rows persist per browser (`mq.explorer.expanded.<id>`, the first 2,000 keys); "All of X" shows at most 300 entities and only relationships whose ends are all shown (`canvas/model.ts` `ALL_OF_CAP`), and the picker offers it only for a domain of at most 300 entities. Tests: `tests/unit/create.test.ts`, Playwright `create.mock-only.spec.ts`. **Not built:** Promote to entity, Apply stereotype… / Tag… / Set category… from the menu (the bulk inspector does these for a selection), Edit seed data, Diagrams of this domain, Create entity from table, Copy SELECT, Copy DDL. (New schema, New table, New view and New
sequence were built later: `explorer/databaseCreate.ts` and `explorer/NewDatabaseObjectDialog.tsx`.)

**As built (a database's New actions, 2026-10-01).** `explorer/databaseCreate.ts` (pure): `DATABASE_CREATE` (schema, table,
view, sequence, in menu order) with `DATABASE_CREATE_LABELS`, the dialogs' checks (`databaseObjectProblems`: the identifier
rule, a name not already a table, view or sequence of the same schema, a view body, whole-number sequence values and a
non-zero increment) and the documents (`buildDatabaseObject`: what the schema requires, defaults left out; a table may
start with an `id` int64 primary key column). The store's `newDatabaseObject` (`requestNewDatabaseObject`) opens the
dialog, mounted once (`NewDatabaseObjectHost`; New schema… keeps `DatabaseSchemas.tsx`'s dialog). The menus offer the
actions on a database row (`new-db:<kind>`), a schema row (table, view, sequence), the Tables, Views and Sequences
folders, the explorer's **+** while a database or a row inside it is selected, the Database screen's **New** menu and
the palette. `table` (with a file of its own), `view` and `sequence` are editor kinds (`editors/database/`: General,
Columns and Keys for a table, Body and Columns for a view, Definition for a sequence, each with Code generation and
References); a table overlay's row (an index row with `entity`) has no editor. Tests: `tests/unit/database-create.test.ts`,
`tests/unit/database-docs.test.ts`, Playwright `database-new-table`, `database-new-view` and `database-new-sequence`
(mock-only).

**As built (routines, database types and SQL objects, 2026-10-01).** `DATABASE_CREATE` gains `routine`, `database-type` and
`sql-object` (New routine…, New database type…, New SQL object…): the dialogs add a routine's kind and a function's result
type, a database type's kind with a domain's base, a range's subtype or an enum's labels (a composite starts with one
field), and a SQL object's kind (free text) and phase, with a body per dialect for routines and SQL objects as for a view.
Names are checked within their group: tables, views, sequences and database types share one; routines and SQL objects
have their own. `model/labels.ts` adds the Routines, Types and Objects folders after Sequences (a schema's buckets in
`explorer/tree.ts`, which counts them on the schema and database rows). The editors (`editors/database/RoutineEditor.tsx`,
`DatabaseTypeEditor.tsx`, `SqlObjectEditor.tsx`) share `DialectBodies.tsx` (the per-dialect SQL editors, also the view's
Body tab), `TypedRowsGrid.tsx` (parameters, a table result's columns, a composite's fields) and `DependsOn.tsx`. The
Database screen's chips list them from the resolved view (`routines`, `types`, `objects`) and the DDL preview renders the
pack's each-routine, each-database-type and each-sql-object units. Tests: `tests/unit/database-objects.test.ts`, Playwright
`database-new-routine`, `database-new-database-type` and `database-new-sql-object` (mock-only).

**As built (queries, 2026-10-02).** `DATABASE_CREATE` gains `query` (New query…, after New routine…; not on a schema row, since
a query has no schema: `SCHEMA_ELEMENT_KINDS`): the dialog asks for the result entity (optional), the from source (a table key
or a view id of the database's resolved view, the entity's table first) and its alias (`defaultAlias`: the first letter of each
word of the source's name), and creates a valid query whose select list fills the entity's key from its column, or holds the
source's first column for an ad hoc row (`querySelect`), as one undo step. Query names are checked across the database.
`model/queryTree.ts` holds the trees' types (query.json), constructors, `describe`/`describePredicate` labels, the editor's
checks, the edits across a query (`renameParameter`, `renameAlias`, `setResultEntity`) and what needs the resolved view
(`sourceOptions`, `findSource`, `scopeOf`, `resolveColumn`, `foreignKeyJoins`, `fillFromColumns`). The editor
(`editors/database/QueryEditor.tsx`, sections in `QueryParts.tsx`, cells in `queryExpression.tsx` and `queryPredicate.tsx`)
has General, Sources, Select, Filter, Group and order, Parameters, Collections (each card reuses the Sources, Select, Filter
and Group and order sections with the parent's aliases in scope), SQL (`GET /api/model/queries/{id}/sql` per dialect, under
the previews' query key so a model change renders it again 400 ms later), JSON, Code generation and References; a case
expression and an exists condition's nested query are edited as JSON for now. The inspector's `QueryFields` sums a query up
with Show the SQL; the Database screen's Queries chip lists the resolved view's `queries`, and picking one shows its SQL in
the preview pane (`ddlPreviewTarget` scope `query`). The mock binds and renders queries as the engine does
(`mocks/model/queries.ts`, `querySql.ts`, a port of `DatabaseRun.Queries.cs` and `QuerySql.cs`; the billing queries render
byte for byte as the engine's goldens), reports MQ3001, MQ4018 and MQ4021 to MQ4043 through validate, and answers the SQL
endpoint. Tests: `tests/unit/query-tree.test.ts`, `tests/unit/mock-queries.test.ts`, Playwright `database-new-query` and
`query-editor` (mock-only).

### 1.9 Related-element highlighting

*New 2026-09-28.* Selecting a row tints the rows related to it:

- an entity: the relationships it is an end of, the entities at their other ends, its base and derived entities (from `base` on entity rows, E5h, proposed in §4.1; against a server without it, only once the entity's document is loaded), the tables mapped from it, and its seed data;
- a relationship: its end entities and the table or foreign key that realizes it;
- a table or column: the entity, relation, enum or attribute mapped onto it ("mapped by", §1.3);
- an enum, value object or custom type: the elements whose attributes use it (only once the references for it are loaded; the index does not carry attribute types).

Highlighting never expands folders; a collapsed folder holding highlighted rows shows a "n related" badge. The related set comes from maps built with the tree (ends, `entity`, `entityId`, `relationId`, `base`; §4.3), so it costs no request, except where a line above says it needs a load. A preference, **Highlight related elements** (on by default, in the explorer's menu and in Settings), turns it off.

### 1.10 Presence

SPEC §14: the model explorer shows who else has an element selected. An element row shows the avatars of the other people who have it selected (up to three, then "+n"), at the row's right edge before the error marker. A collapsed domain or folder holding such rows shows a roll-up badge, "2 people here", counted from the parent map as the "n related" badge is; it never expands the folder. The row memoization of §4.3 already keys on presence state.

### 1.11 Tags and categories, global and per domain

*New 2026-09-28.* The owner's decision: tags and categories exist globally and per domain, because domains are large. Stereotypes stay global.

- **Model.** No new element kind and no new file shape: a tag vocabulary (`tag-vocabulary`) or category tree (`category-tree`) with no `package` is **global**; one whose `package` is a domain is scoped to that domain and the domains nested under it, at most one of each kind per scope. An element's **domain chain** is its domain, each enclosing domain, then global; an element with no package sees only the global ones. Only the vocabulary's `package` member gains a meaning.
- **The engine today refuses it** (checked 2026-09-28), so this is a contract change (E5i, §4.1; step 16, §6). Both schemas (`schemas/v1/tag-vocabulary.json`, `category-tree.json`) have `additionalProperties: false` and no `package`; the records (`Model/Vocabularies.cs`) have none, and `ModelIndexer.PackageOf` returns null for them; and each kind is a singleton (`KindInfo` fixes `model/vocabularies/tags.json` and `categories.json`, `ModelSnapshot` holds one of each, and MQ1009 ignores a second file). The change: an optional `package` member, one vocabulary of each kind per scope (MQ1009 per scope), the domain's files placed under `model/vocabularies/`, and MQ2005 and MQ2006 resolved along the chain.
- **Management.** Global vocabularies in the Settings screen's Tags and Categories tabs (§1.6). A domain's own on the domain: opening a domain row opens a domain editor with tabs **General**, **Tags**, **Categories**, while the row still expands into its sub-domains and kind folders. Each tab lists the inherited vocabularies read-only, nearest first ("from Sales", "global").
- **Use.** The tag picker, the category picker (editors, inspector, bulk "Tag…" and "Set category…") and the filter chips (§3.2) offer the global vocabularies plus those on the element's domain chain, nearest first; an entry or chip from a domain vocabulary names its domain ("core · Billing"). "Move to domain…" warns when the move takes an element's tags or category out of scope.
- **Validation** (next free ids in the spec's ranges): **MQ2008**, error: an element's tag or category is declared only in a vocabulary outside its domain chain (a tag declared nowhere stays MQ2006). **MQ3021**, error: a domain vocabulary declares a tag key or category name that the global vocabulary or an enclosing domain's vocabulary declares (as `RuleCatalog` states it).

**As built (step 16, editor side, 2026-09-29).** `model/vocabularies.ts` (pure) holds the chain: `domainChain` (cycle-safe), `vocabulariesOnChain` (nearest first, then global), `filterVocabularies` (a domain chip's chain, else global then every domain's by name), `tagOptions` (a key keeps its nearest entry; strict when any vocabulary on the chain is), `categoryOptions`, `scopedLabel` ("core · Billing"), `commonDomain` (bulk tagging offers the nearest domain every selected element is in), `markDomainOf` (a domain's own marks resolve along its own chain) and `vocabularyProblemTarget`. `useVocabularies(kind, domain)` loads the chain's documents; the inspector, the editors' mark chips and the bulk inspector pass the element's domain. The domain editor (`editors/DomainEditor.tsx`; `package` is an editor kind) replaces the explorer's domain dialog: General (the common fields), Tags and Categories (`vocabularies/VocabularyEditors.tsx`, shared with the Settings tabs, which edit the global ones); a domain's vocabulary is created with its first entry, named `<domain>-tags` or `<domain>-categories` (the mock places it at `model/vocabularies/<domain>-tags.json`); inherited vocabularies are listed read-only. Revealing a domain vocabulary (search, Go to) opens its domain's tab. The Problems panel shows a **Go to** button on MQ2008 (the element's editor) and MQ3021 (the domain's Tags or Categories tab, or the Settings tab for a global vocabulary). Tests: `tests/unit/vocabulary-chain.test.ts`, Playwright `domain-vocabularies.mock-only.spec.ts`. **Not built:** the "Move to domain…" warning when marks leave scope; the explorer's category filter widens to sub-categories only in the global tree (`categoryParents`).

## 2. Naming

*Revised 2026-09-28: one folder per kind, "Enums" kept, reference data and seed data added, the Mappings section's words moved to the database row.*

The rule: the user-facing words follow how the owner and non-experts talk. Code identifiers and file formats keep the spec's words. Each row is a UI string change only unless noted. Every user-facing string below lives in one module, `model/labels.ts` (which absorbs `KIND_LABELS`), so a later rename is a one-file change. The words are proposals until the owner answers question 7 in §8.8; the naming step (step 6 in §6) waits for that answer.

| Current term | Proposed term | Where it appears |
| --- | --- | --- |
| Package | **Domain** | `model/model.ts` `KIND_LABELS.package`; explorer; inspector "Package" field; new-entity dialog default (`workspaces/entities/dialogs.tsx`); "Move to package" refactor; filters; Problems grouping |
| Package whose parent is a package | **Sub-domain** | Count phrases and breadcrumbs; the same element kind |
| "Project" catch-all group | **Not in a domain** (only when non-empty, listed first) | `explorer/filter.ts` |
| "Package: Billing" virtual view | **All of Billing** | `workspaces/entities/EntitiesWorkspace.tsx` (diagram picker, view title) |
| Workspace / Workspaces | **Screen** / "Go to screen" for a centre tab; the rail's icons name **explorers** (*revised 2026-09-28*, §1.0) | `app/Rail.tsx` aria-label, command palette heading (`palette/CommandPalette.tsx`), SPEC §14 and phase2-design §4.8 wording. The `Workspace` type and the URL segments stay in code |
| Entities (screen) | **Domain model** (rail icon, explorer and screen) | Rail label and tooltip, command palette. The URL segment `/entities` stays |
| Database (screen) | **Databases** for the rail icon, the explorer and the screen (the owner's choice, 2026-09-28, as the word most people understand; tooltip "Databases, schemas and tables") | Rail, command palette |
| Model explorer | **Explorer** (visible label); the aria-label "Model explorer" stays | `explorer/Explorer.tsx` |
| "Filter elements" | **Search the model** (placeholder) | `explorer/Explorer.tsx` |
| Tag vocabulary, Category tree | **Tags**, **Categories** | `KIND_LABELS`, Settings tabs, domain editor tabs (§1.11) |
| Relations | **Relationships** (technical word in the tooltip) | Kind folder |
| Enums | **Enums** (unchanged; the owner's word; tooltip "a closed set of named values") | Kind folder |
| Value object | **Value objects** (tooltip "a reusable group of fields without identity, such as Address or Money") | Kind folder |
| Scalar type | **Custom types** (tooltip "a named restriction of a built-in type, such as Email") | Kind folder |
| (new) reference type | **Reference type**; the screen is **Reference data** | Rail, Reference data screen, attribute type picker, search results |
| (new) seed | **Seed data** ("2 seeds · 40 rows") | Kind folder, entity children, editors; a reference type's rows are its **Rows** tab |
| Mapping (list of files) | **Customised mappings** ("20 customised mappings" on the database row); "by convention" becomes **mapped automatically** | Databases root |
| "Not mapped to an entity" | **Not linked to an entity** | Databases root (grouped by domain) |
| Project | Unchanged: the whole repo's model and its settings (`maquettiste.json`); the tree's top node and the top bar | Tree, top bar |
| Model | Unchanged: the content (domains, databases, diagrams, reference data) | "Search the model", Problems |

**Why "Screen" rather than "View".** "View" already means two things in the spec: a SQL view (a physical element kind) and a diagram (the View layer in §5). A third meaning would bring back the confusion this redesign removes.

**"Domain" beside the spec, and beside its other meaning.** Spec §5 says a package "drives folders and namespaces in output"; the domain tooltip says so: "Domain (package in model files): groups elements; also sets the output folder and namespace." Some data-modeling tools use "domain" for a reusable attribute type; users who know that meaning will find that idea under Custom types and Reference data, and the glossary says so.

**Glossary** (added to SPEC §14 in step 6, and shown as tooltips):

- **Domain**: a business area such as Billing or Sales; stored as a `package` in the model files. A domain inside another is a **sub-domain**. (Not "domain" in the sense of a reusable attribute type, which is a Custom type here.)
- **Domain model**: the tree root and the screen for drawing and editing a domain's entities and relationships (URL `/entities`).
- **Reference type**: a set of rows managed as data (units of measure, countries), each with a code and a label and any fields the project adds, usable as the type of an attribute or a relationship attribute. Managed in the Reference data screen.
- **Enum**: a closed set of named values that belongs to the code.
- **Seed data**: rows an element starts with (an entity's initial rows, a reference type's rows).
- **Business event**: SPEC §5's Event kind, a thing that happened, raised by an operation or a process. The folder is called "Business events" so it is not read as a kind of domain.
- **Explorer**: the sidebar tree a rail icon selects (Domain model, Reference data, Databases, Diagrams, Generate). **Screen**: a document in the centre (an element editor, the Database screen, Settings…).
- **Mapped automatically / customised**: a table that follows the naming conventions needs no mapping file; a customised one has a mapping override.

## 3. Navigation at scale

### 3.1 Search: the tree filter and quick open

*Revised 2026-09-28: search operators, hidden non-matches, and search across all kinds beside the tree.*

The owner wants both: a nested tree that tells you where you are, and search across every kind. There are two entry points over one index.

**The explorer's search box filters the tree**, with four operators:

| Operator | Meaning | Example |
| --- | --- | --- |
| `*` (default when no operator is typed) | contains | `line` or `*line` finds InvoiceLine, OrderLine |
| `^` | starts with | `^inv` |
| `~` | like, with `%` matching any run of characters | `~inv%line` |
| `=` | equals | `=Invoice` |

- Matching is case-insensitive, against the name, the display name (default locale and the content locale in use, §8.5) and, for tables and columns, the physical name. Qualifiers narrow it: `kind:enum`, `in:Billing` (a domain and its sub-domains), `tag:`, `st:`, `cat:`; each qualifier also shows as a filter chip (§3.2).
- **Non-matching rows are hidden.** A folder or container stays when anything beneath it matches, with its count showing "3 of 41". A matching domain, database or schema keeps its whole subtree, collapsed, so `=Billing` shows the Billing domain. Ancestors of matches are expanded while the filter is active; clearing it restores the user's expansion state.
- Matched text is highlighted in each row. Enter on the box moves to the first match; Down moves into the tree.
- **Kinds that are not in the tree still answer.** Below the filtered tree, a footer says "Also 4 reference types match" (and "12 tables in Databases", "2 settings"; *revised 2026-09-28*: other explorers' matches too); clicking switches the rail or opens them where they live. That is how reference types stay out of the tree yet stay findable.
- Table and column names come from the table summaries and from the tables whose detail is loaded; until the summaries arrive, a footer line says "table names still loading" and the filter re-runs when they land.

**Quick open (Ctrl/Cmd+P) and the command palette (Ctrl/Cmd+K)** search across every kind as a ranked flat list: elements, reference types, tables, views, diagrams, settings tabs and screens, and in the palette also commands. Prefixes pick a scope: `>` commands, `@` the members of the open element (attributes, members, columns), `#` attributes across the model once attribute names are indexed (§4.1, E5g). Each result shows its kind icon and path.

- **Ranking**, best first: exact match; prefix match; word-start match across camel, snake and space boundaries (`invli` finds `InvoiceLine`, `inv_li` finds `invoice_lines`); substring match; fuzzy match on a subsequence. Typing one of the four operators switches from ranking to the operator's plain match, so the two boxes share one syntax.
- **Tie-breakers.** Kind weight (entity, then reference type, table, relationship, enum, value object, custom type, diagram, other). Then the element is in or near the domain of the current selection. Then it was opened recently in this session. Then the shorter name, then alphabetical order. Candidate library: uFuzzy (MIT, built for about 100k short strings, with ranking hooks); the fallback is a small in-house prefix and trigram index. `search/rank.ts` holds the ranking as a pure function, whichever library is used.
- The first 200 results are shown with "N more: narrow the search".

**One search index, in a web worker** (§13: client-side search indexing runs in workers), shared by the tree filter, quick open, the palette and the Reference data screen's list. The tree filter asks the worker for the set of matching ids; the main thread computes the visible rows from that set and the tree's parent map (§4.3).

**Keys.** `/` focuses the explorer's search box. Esc clears it and returns to the tree with the selection revealed.

### 3.2 Filters and scopes

*Revised 2026-09-28: scopes and the pinned filter.*

Chips below the search box, which combine with the search text:

- **Kind**: multi-select.
- **Domain scope**: "in Billing", including sub-domains; also set from a domain's context menu with "Search in this domain".
- **Tag**, **Stereotype**: multi-select, any of.
- **Category**: the category tree; choosing a node includes its descendants. *Revised 2026-09-28:* Tag and Category chips offer the global vocabularies plus those on the domain chain of the domain scope (or, without one, of the selection), nearest first; a chip from a domain vocabulary names its domain ("core · Billing", §1.11). The `tag:` and `cat:` qualifiers match in every vocabulary.
- **Has errors**: from validation. **Missing translations** for a chosen locale (§8.5).
- **On this diagram.**

Active filters show a count on the filter button, and counts in the tree reflect them ("12 of 212"). **Scopes**: a set of chips can be saved under a name, per user or, through project settings, for the team ("Billing core", "Everything tagged pii"), and chosen from a drop-down beside the box. **Pinned filter**: a pin keeps the filter across screen changes and reloads (per user, in `localStorage`), shown as a pinned chip until it is unpinned.

### 3.3 Go to definition, where used, breadcrumbs

*Revised 2026-09-28: reference types as targets, generated files, favorites and recents.*

- **Go to definition** (F12, or Ctrl/Cmd+click on a reference in the inspector, an editor or a grid). It follows an attribute's type to its enum, value object, custom type or reference type (the last opens the Reference data screen), a relation end to its entity, a mapping to its entity or table, and a table or column to its entity or attribute. It then selects the target, reveals it in the tree and centres it on the canvas when the target is on the active diagram.
- **Used** (Shift+F12; the menu item and the inspector tab, formerly "Where used"). A **References** tab in the bottom panel lists the results of `GET /api/model/references/{id}` in a virtualized list, grouped by kind of the referencing element, then by domain, with the JSON pointer field shown ("InvoiceLine · attribute amount → type"). A diagram membership shows as "on diagram Billing overview". Clicking a row goes to that element and pointer. Later, as an impact analysis, the tab adds a **Generated files** group from the last plan (the template units that read the element).
- **Breadcrumbs.** Above the canvas and the editors: `Domain model › Sales › Orders › Entities › Order`. Each segment opens a menu of its siblings. The same path appears under each search result.
- **History.** Alt+Left and Alt+Right move back and forward through selections.
- **Favorites and recents**: a star on any row adds it to a **Favorites** list, and a **Recent** list holds the last 20 opened elements; both sit in a collapsible strip above the tree, per user.

### 3.4 Keyboard navigation of a virtualized tree

*Revised 2026-09-28: multi-selection.*

- **ARIA.** The tree follows the WAI-ARIA tree pattern: `role="tree"`, `treeitem`, `aria-level`, `aria-expanded`, `aria-selected`, and, because rows outside the viewport are not in the DOM, `aria-setsize` and `aria-posinset` on every rendered row; `aria-multiselectable="true"` on the tree. DOM focus stays on the `role="tree"` container, which carries `aria-activedescendant` pointing at the active row (as today's listbox does). There is no roving `tabindex`: the two are alternative focus models, and keeping focus on the container suits virtualization, because focus never leaves it when rows unmount. The virtualizer keeps the active row rendered.
- **Keys:**
  - Up and Down move between rows.
  - Right expands a row, or moves to its first child. Left collapses a row, or moves to its parent.
  - Home and End go to the first and last row. PageUp and PageDown move by one viewport.
  - `*` expands all siblings.
  - Typing letters jumps to the next visible row that starts with them.
  - Enter opens the row. Space toggles it in the selection. Shift+arrows extend the selection (same kind only, §1.8).
  - The context-menu key, or Shift+F10, opens the row menu.
  - F6 still cycles the shell regions (phase2-design §4.8).

### 3.5 Keeping the explorer and the canvas in step without loading the model

*Revised 2026-09-28: E5 and E5b are built; element children load on expand.*

- **Selection.** The store's selection is the only source. The tree reveals the selection, and the canvas centres it when it is a member of the active diagram.
- **What the canvas loads.** The canvas loads only the members of the active diagram. Relations between members are found from the relation ends in the index (E5, built), not by loading every relation. The member documents arrive through batched reads (E5b, built) in chunks of up to 200 ids. The canvas's working set is therefore bounded by the diagram (at most 300 entities and their relations), whatever the model's size.
- **What the tree loads.** The tree loads full elements only when an entity, relationship, enum or value object row is expanded (its attributes or members). Hovering or focusing a row for 300 ms prefetches that element's document, so the inspector and the editor open instantly.

**As built (canvas arrangement, 2026-09-29).** The canvas no longer re-fits or re-lays out what the user arranged. Pan
and zoom are saved on move end (debounced 300 ms, the canvas's own fit, restore and centring ignored) into the
diagram's `viewport` through its draft, rounded to three decimals; a viewport-only save joins the diagram's last undo
step (`state/drafts.ts` `viewportOnly`). Opening restores a saved viewport (x, y and zoom) and fits only when there is
none or after an explicit Auto-layout, which also saves the fitted view. Cards without a position while others have one
are placed by `canvas/placement.ts` `placeNodes` (beside the nearest related card, else rows under the bounding box,
with the layout's 96/48 gaps, measured sizes, placed cards never moved); the full layout runs only when no card has a
position. "All of <domain>" becomes the domain's own diagram on the first drag, Auto-layout, pan or zoom
(`canvas/domainDiagram.ts`: a diagram in the domain with `"membership": "package"`, first named after it, created in
one undoable step; its members then follow the domain), and positions kept per browser under `mq.pos.pkg.<id>` migrate
into it once. The database canvas keeps positions and viewport per browser (`mq.pos.db.<id>`, `mq.viewport.db.<id>`)
with the same placement. Tests: `tests/unit/canvas-placement.test.ts`, Playwright `canvas-arrangement.mock-only.spec.ts`.
*2026-09-30 (with phase 3 round P4):* Auto-layout makes room for the relationship labels: every relation edge reaches
the layout with its label pill's size plus a 12 px gap (`relationLabelSize`, `layoutInput` in `canvas/model.ts`; the
flat `elkGraph` carries edge labels and `elk.spacing.edgeLabel`, `elk.spacing.edgeNode` and
`elk.layered.spacing.edgeNodeBetweenLayers`), and a label whose edge is too short to hold it with the gap floats beside
the line instead of over a card (`labelOffset` in `RelationEdge.tsx`). The owner: labels "tend to just stick right to
the adjacent entities making it hard to read them on big diagrams".
*Status 2026-09-29:* the domain's diagram is explicit, no longer recognised by its name: `schemas/v1/diagram.json`
`membership` (`explicit`, the default and left out of the canonical form, or `package`: the diagram shows every entity
of its package and their relationships, members carry positions only), the engine's `Diagram.Membership`, and MQ3022
(a `package` diagram without a package). "All of <domain>" opens the first `package` diagram of the domain by ordinal
id; member sync, the hidden "Add related" and the refused drops key off the membership; the diagram inspector shows it
read-only. Renaming the domain or the diagram changes nothing. The Database screen's Tables list and DDL preview are
panels of the layout (`tables`, `ddl` in `mq.layout`): header buttons, Alt+Shift+L and Alt+Shift+D on that screen
only (Alt+Shift+T is the browser's), edge strips, Reset layout; Playwright `database-panels.mock-only.spec.ts`.

### 3.6 Element editors

*New 2026-09-28.* Enter or a double click on an entity row opens its **entity editor** as a document tab in the centre area, beside the canvas; a single click opens it in a preview tab that the next single click replaces; editing or a double click pins it. The inspector stays the compact property view for canvas work. The layout is a band of top controls over tabs:

- **Top controls:** name; domain (a picker); key ("id", with **Edit…** for a composite or alternate keys); base entity (SPEC §6's inheritance); **Is abstract**; stereotypes, tags and category as chips (the pickers offer the vocabularies of the entity's domain chain, §1.11). Display names and the description are in the header; other locales appear only when the project declares two or more, in a collapsed **Translations** section (§8.5), so localization does not crowd the editor.
- **Tabs:**
  - **Attributes**: the attribute grid (`inspector/AttributeGrid.tsx`), then alternate keys and unique attributes, then **Inherited** (read-only rows from the base entity) and **Virtual** (attributes added by the entity's stereotypes, SPEC §6, read-only here).
  - **Relationships**: the relations with this entity at an end, with role, cardinality and their attributes; "New relationship…".
  - **Mappings**: one section per database: table, column mapping grid with conventions muted and overrides highlighted (SPEC §14's Mappings screen, scoped to this entity).
  - **Inheritance**: strategy and discriminator; disabled when the entity is in no hierarchy.
  - **Seed data**: the entity's rows in a grid (§8.4).
  - **Code generation**: per-pack `generation{}` hints and the custom properties from extension schemas (`inspector/SchemaForm.tsx`).
- **General mode.** A **Follow selection** toggle on the tab bar turns an editor tab into the **Entity editor (general)** ("pin" keeps its sense of making a preview tab permanent): it follows the selection in the tree or on the canvas and keeps the current sub-tab, so a user can walk twenty entities on the Mappings tab without reopening anything. Unsaved drafts are kept per element (`state/drafts.ts`), so moving on never loses an edit.
- **Other kinds** use the same frame: the **relationship editor** (top controls: name, domain, ends with entity, role, cardinality and delete behaviour; tabs Attributes, Mappings, Code generation), and the **enum**, **value object** and **custom type** editors (members or attributes, Code generation). A reference type opens in the Reference data screen (§1.7). *Revised 2026-09-28:* the **domain editor** has tabs General (name, parent domain, description), Tags and Categories (§1.11).

## 4. Performance plan

### 4.1 What the index carries

*Revised 2026-09-28: E5 to E5e are built; the editor does not use them yet; E5f to E5h and an E5c addendum are proposed for this revision.*

`ElementSummary` before S1 had `id`, `kind`, `name`, `package`, `tags`, `category`, `stereotypes`, `hash` and `path`. The first round proposed these engine and contract additions (E5), all optional and nullable so older servers still work. (E5 to E5h name contract additions in this document only; they are not the ids in `spec-errata.md`.)

| Addition | For | Cost at spec scale |
| --- | --- | --- |
| `displayName` | Search and the label shown | Small |
| `database` on table, view, sequence and mapping rows | Databases root and its Customised mappings folders | 26 B per physical row |
| `entity` on mapping and table-overlay rows | An entity's Mappings child, go to definition | 26 B per row |
| `memberCount` on diagram rows | Member count on diagram rows without loading diagrams | About 15 B per diagram |
| `ends: [entityId…]` on relation rows | Entity → Relations, canvas edges without loading relations, "add related" without a references call | About 60 B × 20,000 ≈ 1.2 MB before gzip |
| (E5b) `POST /api/model/elements/read` `{ ids: [...] }` → `ElementDocument[]`, up to 200 ids | Diagram and inspector loads in one request per chunk | New endpoint |
| (E5c) `GET /api/databases/{id}/tables` → `{ tables: TableSummary[], diagnostics, partial }` (`key`, `name`, `schema`, `origin`, `entityId`, `relationId`, `isJunction`, `columnCount`) | Tables in the tree, table-name search and the coverage line, without every column | New endpoint, a projection of `DatabaseView`. Unlike `/view`, it works on a model with errors: it returns the tables that resolve, the diagnostics, and `partial: true` (see below). Target: 700 tables ≤ 300 ms server time and ≤ 150 KB gzipped |
| (E5d) `summary: ElementSummary` on `ElementChange`, required when the server supports E5d; deletions keep using `ChangeSet.deleted` (`Ulid[]`) | Patching the index in place from `model.changed` | Only changed rows |
| (E5e) `ETag` on `GET /api/model/index` (a hash of the index version), 304 on `If-None-Match`, `Cache-Control: no-cache` in place of today's `no-store` (phase2-design §4.3 and its host notes are amended to match) | Warm reopen and debounced refetches with no transfer | One header |

**As built (stage S1, 2026-09-28).** The contract (`docs/api/openapi.yaml`), the engine, the functions, the recorded mocks and the stateful mock implement E5 to E5e with these differences from the table above:

- **E5 `ends`** is `[{ entity, role }]` per relation (`RelationEndSummary`), not a bare id list: roles label the edges and "add related" needs them. The E5 members are left out of the JSON when null (`JsonIgnore(WhenWritingNull)`), so rows of other kinds carry nothing; `displayName` appears only when the element has one. `ElementSummary` equality now compares its lists by item.
- **E5b** answers `ElementReadResult { elements: ElementDocument[], missing: string[] }` (`ModelStore.ReadElementsAsync`, an extension in `Engine/Editor/ModelReads.cs`): one snapshot, no rescan, each document once in the order its first id was asked for (a sub-element id reads its holder), unknown ids in `missing`. More than 200 ids, or an id that is not a ULID, is 400 `bad-request`.
- **E5c** is `DatabaseTables.GetAsync` (`Engine/Editor/DatabaseTables.cs`, one singleton per host). It reads the store's current snapshot (no stat rescan, like the index), validates it once per snapshot and, side by side, resolves each database asked for on its own (step 13; see "As built (E5f and the per-database resolve)" below). `TableSummary` also carries `isLookup`, deprecated and always false since the enum lookup-table option was retired. Left out when the model has errors: tables whose entity, relation, table file or key owner (`<owner>@<db>`) has an error, the targets of a mapping or table overlay with errors, and every table of a database with errors. A file that fails to load (schema error, MQ1002) is absent from the snapshot, so its effect is absent too; `partial` is still true.
- **E5d** is added by the functions host, not the engine: `EditorEvents` publishes `ModelChangedEvent`, the `ChangeSet` shape with `summary` on each change looked up in the index when the event goes out (so it can be newer than the change's hash), cut to 200 KB. `SaveResult.changes` and the engine's `ElementChange` are unchanged.
- **E5e** tags the index with `ModelReads.IndexTag`: SHA-256 of an index format constant (`maquettiste-index/e6` since reference-types-seeds-localization.md step 5, bumped with the row shape) and every row's id, file hash and path. It is a function of the files, so it survives a server restart, unlike a version counter.

**Editor side, not wired yet.** `api/endpoints.ts` has `readElements` (E5b) and `getDatabaseTables` (E5c), and `api/schema.d.ts` is regenerated, but `useElements` still issues one GET per id, no query calls `getDatabaseTables`, the index query neither sends `If-None-Match` nor turns off structural sharing, and nothing patches from `ModelChangedEvent` summaries. That wiring is migration step 3 (§6), and it needs no further contract work.

**Proposed in this revision** (optional and nullable like E5, so older servers still work):

| Addition | For | Cost |
| --- | --- | --- |
| (E5f) `GET /api/databases/{id}/tables/{key}` → `{ table: TableView \| null, diagnostics, partial }` | A table's children in the tree (columns, primary key, foreign keys, unique constraints, indexes) and column-level "mapped by" (`ColumnView.attributeId`), without `/view`'s every table | A projection of the same per-snapshot resolve E5c uses: after the first call of a snapshot, a lookup. About 2 to 5 KB per table |
| (E5g) `GET /api/model/terms` → `{ rows: [ownerId, subId, name, displayName][] }`, ETag like E5e | `#` attribute search in quick open, and matching attribute, member and column names in the tree filter | About 75,000 terms at spec scale, an estimated 3 MB of JSON and 0.5 MB gzipped; fetched by the search worker after the index, off the critical path. Later; not needed for the tree |
| `?locale=` on `GET /api/model/index` (designed in `reference-types-seeds-localization.md` §3.8) | Tree labels and search in the content locale the user picks (§8.5); the E5e ETag covers the locale | Only when the user picks a locale other than the default |
| (E5h) `base` on entity rows: the base entity's id, left out when there is none | Base and derived entities in related-element highlighting (§1.9) without loading documents | About 30 B per derived entity |
| (E5i, *2026-09-28*) optional `package` on `tag-vocabulary` and `category-tree` files and their index rows; one of each kind per scope | Domain vocabularies, their pickers, chips and MQ2008 and MQ3021 (§1.11) | One member per vocabulary |
| ~~`enumId` on `TableSummary`~~ (withdrawn: the enum lookup-table option is retired, MQ7012; see "As built (E5i)" below) | Not built | None |

**As built (E5i, 2026-09-29).** `schemas/v1/tag-vocabulary.json` and `category-tree.json` and `Model/Vocabularies.cs` carry an optional `package`; the index row's `package` is set for both kinds (`ModelIndexer.PackageOf`), so the editor derives the chain from the package rows' `parent` with no new endpoint. `ModelSnapshot` keeps one vocabulary of each kind per scope (`TagVocabularies`, `CategoryTrees`, `TagVocabularyOf`, `CategoryTreeOf`, `VocabularyChain`; `Tags` and `Categories` are the global ones); MQ1009 is per scope; MQ2005 and MQ2006 resolve along the chain; MQ2008 and MQ3021 (the latter also against every enclosing domain, the owner's decision) are in `RuleCatalog` and `BuiltinRules` (`DomainVocabularyTests`). A domain's files are `model/vocabularies/<name>-tags.json` and `<name>-categories.json`. The bench's `write-model --domain-vocabularies` writes a global and one per domain. **`enumId` is not added:** the enum lookup-table option is retired (MQ7012, reference-types-seeds-localization.md), so no resolved table realizes an enum and `isLookup` is always false (deprecated in the contract).
| Index members for the new kinds (designed in `reference-types-seeds-localization.md` §3.8): `rowCount` and `fieldCount` on reference-type rows, `target` and `rowCount` on seed rows | Reference data count and list, search, the entity's **Seed data** child | Ships with those kinds (§8.3, §8.4); about 800 reference-type rows in a large project |

**Index size, measured.** On the S1 bench model (26,267 rows) the index is 10.8 MB of JSON and 2.4 MB gzipped (§4.5), more than the first estimate of 7 MB because of the relation ends. Parsing it is the largest single task at open. With E5e (built) the index answers 304 on a matching ETag, so a warm reopen costs no transfer once the editor sends `If-None-Match` (step 3).

**As built (E5f and the per-database resolve, step 13 engine part, 2026-09-29).** `GET /api/databases/{id}/tables/{key}` answers `DatabaseTableResult` `{ table: TableView | null, diagnostics, partial }` (`DatabaseTables.GetTableAsync`; `table` is null when no table has the key or the table is left out on a model with errors). E5c and E5f share one cache per model version: the model is validated once, and each database asked for is resolved on its own (`ModelResolver.ResolveDatabaseAsync`: the conceptual layer, then that database's run only; the conceptual finish, seeds, usages and the dependency freeze, which shape no table, are skipped). Its tables are those of the whole-model resolve (tested on the fixture model and the bench's synthetic model, every table's `TableView` compared). `diagnostics` then holds the validation diagnostics and that database's resolution diagnostics, so another database's resolution error no longer marks this one partial. The tables are projected (summaries and `TableView`s) in parallel and the resolved objects dropped. A resolver without the per-database entry point falls back to one whole-model resolve per version. Measured in §4.5.

**Table summaries on a model with errors.** `GET /api/databases/{id}/view` returns `view: null` whenever the model has any error, and so does the mock (`mocks/model/generation.ts`). A tree that emptied its Databases section on every half-typed edit would be useless, so E5c resolves what it can: tables whose owning elements are valid are returned, the rest are left out, and `partial: true` plus the diagnostics say so. The editor also keeps the last complete summary per database; when a response is partial it shows the last good tables with a "stale: model has errors" badge on the database row. **Invalidation and cost:** `["tables", dbId]` is invalidated on `model.changed` only when the change set touches a kind that shapes tables (package, entity, relation, enum, value object, scalar type, database, table, view, sequence, mapping, project settings). As built after step 13, the server validates the model once per snapshot and resolves each database asked for on its own: on the bench model about 0.3 to 0.4 s for a 4,831-table database and 0.5 to 0.95 s for a 10,179-table one, and under 130 ms for a 1,460-table database of a 700-entity model (§4.5); a repeated call is a lookup. Every entity, relation or package edit therefore costs one such run per database the editor shows. The invalidation is debounced on its own timer, 1.5 s after the last qualifying change (with E5d there is no index refetch to share a timer with), and the tree shows the last-good tables meanwhile. The per-database resolve of step 13 meets the 300 ms per 700 tables target (§4.5).

### 4.2 Lazy loading

*Revised 2026-09-28: entity, table and reference-data loads.*

- **At start:** the index, validation, the settings, and the active diagram's members (in batches).
- **Right after the tree's first paint, in the background:** the table summaries (E5c) of every database, at low priority, so the Databases counts, the coverage line and table-name search fill in within about 1 s per database on the scale model (one per-database resolve each, §4.5) without blocking the tree.
- **When needed:** a selected element (the inspector, the editor), an expanded entity, relationship, enum or value object (one batched read covers every row expanded in the same frame), an expanded table (E5f), hovered rows (prefetch), references when Used, Show on canvas or a reference type's Used by tab asks for them, and a reference type's rows when the Reference data screen shows them.
- TanStack Query's `gcTime` for element documents drops to 5 minutes, so browsing does not keep thousands of documents in memory. The General-mode editor (§3.6) prefetches the next and previous rows' documents.

### 4.3 Tree model and virtualization

*Revised 2026-09-28: deeper nesting, filter rows and related maps.*

- **Index query.** `["index"]` sets `structuralSharing: false`: TanStack's default `replaceEqualDeep` would walk 27,000 rows on every refetch for nothing, since the tree patches or rebuilds by version anyway.
- **Search worker handoff.** The worker fetches `GET /api/model/index` itself (the HTTP cache or the E5e 304 makes the second fetch cheap), or, where it cannot, receives the raw response `ArrayBuffer` as a transferable. It never receives the parsed object graph through a structured-clone `postMessage`, which would cost tens of milliseconds on the main thread.
- **`explorer/tree.ts` (pure).** It builds a node map from the index: one tree per explorer (§1.0), domain nesting, kind folders, project-defined folders (§1.6), element children that the index can answer (relationships from `ends`, seed data, mappings), the Databases shape once the table summaries arrive, and rolled-up counts. With it, it builds a parent map (for reveal, breadcrumbs and the filter) and the related maps of §1.9 (entity → relations, relation → entities, entity ↔ tables, relation → junction table, base ↔ derived entities), and each domain's vocabulary chain for the pickers (§1.11). A build costs O(n), an estimated 20 to 30 ms for 27,000 rows with the related maps; it is rebuilt only when the index version changes, and patched in place for single-row changes.
- **Visible rows.** A depth-first walk over expanded nodes, cached. Expanding splices the child rows into the cached array, and collapsing removes the range, so the cost is proportional to the rows that change, not the model. In filter mode, the visible rows are the walk restricted to the worker's match set and its ancestors (from the parent map), and the counts become "n of m".
- **Highlight.** The related set of the selection is computed once per selection change from the related maps (O(degree)); each rendered row checks membership in a `Set`, and each folder's "n related" badge is counted from the parent map, so a selection change re-renders only the visible rows.
- **Rows.** TanStack Virtual with fixed row heights (32 or 40 px, as today), so nothing is measured. Overscan is 12. Row components are memoized by id plus draft, error and presence state.
- **Expansion state** lives in the store (`explorer.expanded`, `explorer.followSelection`). It is mirrored to `localStorage` per project name, with every access wrapped in try/catch, and keyed by element id so it survives renames and moves.

### 4.4 Incremental updates from `model.changed`

*Revised 2026-09-28: the server side of E5d is built.*

- With E5d, each `ElementChange` carries its new summary, and each id in `ChangeSet.deleted` removes a row. The index cache is patched, the tree patches the affected node and its ancestor counts, and the search worker updates its entry. There is no refetch.
- Without E5d (older server, or `truncated: true`), the current behaviour stays: a debounced refetch after 250 ms, sent with `If-None-Match`. The tree is rebuilt, and expansion and selection are kept because they are keyed by id.
- The editor's own saves keep patching from `SaveResult.current` (phase2-design §4.3).
- *Added 2026-10-01 (the owner: "I click Layout, and States and Events disappear from the tree").* Children that come
  from an element's document (an entity's or relationship's Attributes, an enum's Members, a process's States and
  Events) are not in the index, so a rebuilt forest, or a patch that replaces a changed row's node, has none. Each row
  node records the index hash its document children were read at (`needsDocument` in `tree.ts`); after every new forest
  the explorer reads them again for each visible expanded row that lacks them or shows an older version, from the cached
  document when its hash matches the index and from the server otherwise. A node a patch leaves alone keeps its children
  and its mark. Tables keep their per-forest record of loaded details.

### 4.5 Targets

*Revised 2026-09-28: targets for the filter, highlighting, element children and the General-mode editor.*

The measurements assume the spec's laptop (§13), 5,000 entities and 20,000 relations, and the live mode. In CI the mock's own cost is measured, not guessed: the mock backend records its handler time per request (index serialization, validation, resolves) in `window.__mqPerf.mock`, and each CI budget is the target plus the recorded mock time for that measure, plus 25 % for CI machine noise. The mock work that ran on the page's main thread is moved off it where it can be (§5 item 4).

| Measure | Target | Spec anchor |
| --- | --- | --- |
| Editor open, warm index (304, needs E5e) → explorer usable | ≤ 1.5 s | §13 load budget 3 s cold |
| Editor open, cold index (about 10.8 MB, 2.4 MB gzipped, measured in S1; no E5e) → explorer usable | ≤ 3 s | §13 load budget 3 s cold |
| Index arrives → explorer first paint (tree build + first rows) | ≤ 150 ms, no main-thread task over 100 ms. Per stage: parse ≤ 90 ms, measured on the 10.8 MB index in the `scale` project (if a single parse task exceeds the 100 ms long-task cap, a follow-up moves the parse off the main thread), query cache write ≤ 5 ms (no structural sharing), tree build ≤ 30 ms including the related maps (§4.3's estimate is 20 to 30 ms), worker handoff ≤ 5 ms (worker fetches or takes a transferable), first rows ≤ 20 ms. Parse and the rest run as separate tasks (the build is scheduled after the parse yields) | §13 virtualized explorer |
| Table summaries (E5c) | As built: first call after a change ≤ 2.5 s server on the bench model (one whole-model resolve), every other database of that snapshot ≤ 5 ms; off the critical path. After the step 13 follow-up (per-database or incremental resolve): 700 tables ≤ 300 ms and 10,005 tables ≤ 1 s server; ≤ 150 KB gzipped for 700 tables, ≤ 300 KB for 10,005. **Measured (S1, bench model, 26,267 rows, three databases with 10,005 + 1,683 + 327 tables):** the first call after a change costs one validate + resolve of the whole model, 2.4 s with the JIT warm (validation alone about 0.3 s; the resolve dominates); every other database of that snapshot then answers in under 1 ms. Main's 10,005 tables are 2.3 MB, 230 KB gzipped, so 700 tables are about 16 KB. **Measured after step 13 (per-database resolve, `bench time-tables`, 8 threads, WSL2, 2026-09-29):** the bench model (`write-model` defaults, 26,616 files, databases of 4,831, 10,179 and 10,179 tables): after an edit, 271 to 400 ms for the 4,831-table database and 459 to 954 ms for a 10,179-table one (validation about 110 ms, run side by side; the conceptual layer alone about 86 ms; one 10,179-table database's resolve about 415 ms warm; the whole-model resolve about 1.05 s); a 700-entity model (`--entities 700 --relations 2800`, three databases of about 1,460 tables): 59 to 127 ms per database after an edit, so about 30 to 60 ms per 700 tables. One table (E5f) after the first call of a version: under 1 ms | Met |
| Quick open or palette keystroke → ranked results painted | ≤ 50 ms p95; worker ready ≤ 500 ms after the index. **Measured (S2):** p95 11 ms for today's main-thread substring filter on the scale mock | §13 search in workers |
| Tree filter keystroke → filtered tree painted (hidden non-matches, ancestors expanded) | ≤ 50 ms p95 | §13 search in workers |
| Expand or collapse any node (including one with 5,000 children) | ≤ 16 ms (one frame). **Measured (S2)** on today's explorer: 34 ms and 28 ms on the largest group (694 rows), a known miss, because `explorerRows` rebuilds and re-sorts every row per toggle; the splice of §4.3 removes that | §14 keyboard-friendly |
| Expand an entity row: prefetched / not prefetched | ≤ 16 ms / ≤ 150 ms (one batched read) | New |
| Expand a table row (E5f) | ≤ 200 ms | Engine built: a lookup (under 1 ms) once the database is resolved for the version; the first call costs the database's resolve (E5c row) |
| Expand the Tables folder of `main` (10,005 tables, `?mock=large`) | ≤ 16 ms once the summaries are loaded (a splice of one sorted array) | New |
| Type-ahead in a 10,005-row Tables folder | ≤ 16 ms per key (binary search on the sorted names) | New |
| Selection change → related rows highlighted | ≤ 16 ms | New |
| General-mode editor: next entity, same tab | ≤ 100 ms with the document prefetched | New |
| Scroll the tree, fully expanded | 60 fps, no long tasks over 50 ms | §13 |
| Select on canvas → row revealed in tree | ≤ 50 ms | New |
| `model.changed` for one element → tree and search updated | ≤ 16 ms with E5d; ≤ 400 ms including the debounce without it | §14 sync |
| 300-node diagram: open → first paint | ≤ 2 s (two to eight batched reads) | §1 300 nodes |
| 300-node diagram: pan and zoom | 60 fps (≥ 50 fps measured in CI) | §1 |
| 300-node diagram: ELK auto-layout | ≤ 3 s in the worker, UI responsive throughout | §4.9 (200 entities under 2 s) |
| Index size (measured, S1) | Not a target: at 26,267 rows the index is 10.8 MB of JSON (8.2 MB without the E5 members; the relation ends are most of the difference), 2.4 MB gzipped; serializing takes about 100 ms and the E5e tag about 20 ms | §13 |
| Memory, index + tree + search index, page and workers | ≤ 150 MB, measured with `performance.measureUserAgentSpecificMemory()` in the Playwright `scale` project (Chromium, cross-origin isolated preview), which includes worker heaps; `performance.memory` is not used because it is Chrome-only and excludes workers | New |

**Measured optimizations (2026-09-29, later the same day; before → after, two `scale` runs after, WSL2).** Search
worker handoff: the index loader keeps the JSON text beside the parsed rows (`api/indexText.ts`) and the search client
posts that text in 1 MB slices, one per task (the worker joins, parses and encodes it). The longest main-thread task of
the handoff went 9.9 → 0.9 ms (target 5 ms, **met**), but that is a new measure: the slices together still copy the whole
text through structured clone on the main thread, so the scale spec also records their total ("all slices") against the
handoff's original 5 ms target, a known miss until the design above (the worker fetches the index, or takes a
transferable `ArrayBuffer`) is built. The worker is ready 322 → 429–488 ms after the index (target 500 ms, budget 625 ms;
closer to its target than before, watch it). General-mode walk: profiled; every step rebuilt the index id map and scanned every
row for vocabularies (`model/vocabularies.ts`, now cached per index array), the filter bar re-collected every tag in use
on each selection (now once per index) and the covered canvas re-rendered every card and edge (it now keeps the
selection it showed until the editor closes): 177.7 → 162.2 / 147.7 ms median (budget 125 ms, **still a known miss**;
what remains is the editor remounting per entity, whose first presence check (the UI library reads the computed animation name) forces one style recalculation of the
new DOM, about 45 ms a step, and the explorer's selection commit). `model.changed` → tree and search 41.5 → 29.2 / 24.9 ms
(budget 20 ms, known miss); selection → related rows highlighted 43.3 → 33.9 / 32.4 ms (known miss); explorer first rows
37.4 → 45.2 / 33.8 ms and index → first paint 215.6 → 139.2 / 188.5 ms (noisy on this machine; first rows is still a
known miss, the total is at its 188 ms budget). Not changed: the forest build (35–44 ms against 38 ms).

**Measured misses (2026-09-29, `CI=1 npx playwright test --project=scale` on an idle machine, `?mock=large`).** Six
measures pass only as a recorded "known miss" (`within()` in `tests/e2e/scale.spec.ts` annotates them; nothing fails):
search worker handoff on the main thread 10.1 ms (budget 6 ms; since the optimizations above, its longest task is in budget and
the total of its slices is the known miss); explorer first rows 37.2 ms (budget 25 ms);
`model.changed` → tree and search updated 34.1 ms median (budget 20 ms, target 16 ms); General-mode editor, next
entity on the Mappings tab 167.1 ms median (budget 125 ms, target 100 ms; about 155 ms when first measured);
selection → related rows highlighted 44 ms median (budget 20 ms, target 16 ms; from the click to the layout effect of
the commit that paints the highlight, `explorer:highlight`, which shares its commit with the selection's other panels);
select on canvas → row revealed 137 ms (budget 63 ms, target 50 ms; the reveal runs in an effect after the selection
commit, then rebuilds the rows and scrolls in a second commit). Expand a table row is measured and in budget (22 ms
median, target 200 ms) and asserted. Expand and collapse of a folder, the index parse and index → first paint are in
budget and asserted with no known-miss allowance. In budget
but above earlier logs: explorer build 32.8 ms (target 30 ms, budget 38 ms; 7.5 to 13 ms on 2026-09-28) and search
keystroke p95 35.1 ms (budget 63 ms; 12 to 14 ms on 2026-09-28); the regression is confirmed on an idle machine and its
cause is not yet found. Not yet measured (skipped in the spec): the Tables folder expand and type-ahead, an entity row expand, full-tree
scroll and the 300-node diagram measures. `MQ_SCALE_STRICT=1` turns every recorded miss into a failure.

## 5. A large mock dataset

*Revised 2026-09-28: built in stage S2; the first plan's items are replaced by what exists, and this revision's additions are listed at the end.*

The goal: the owner and Playwright can feel 5,000 entities and 700-plus tables in `npm run dev`, before the real container. Items 1 to 4 and 6 are built; item 5 is a recipe; item 7 is measured from the page, not yet from the editor's own marks.

1. **The generator.** `dotnet run -c Release --project bench/Maquettiste.Bench -- write-model --out <dir>` (`bench/Maquettiste.Bench/WriteModel.cs`, shape in `Synthetic/SyntheticModel.Scale.cs`). Its defaults are the large mock: 5,000 entities, 20,000 relations, `--domains 40 --domain-depth 3 --domain-width 4` (breadth-first nesting: 2 roots, 8 children, 30 grandchildren), `--diagrams 150 --diagram-size 20..300` (or `--diagrams-per-domain <n>`; a diagram takes its package's entities, then their relation neighbours breadth first, then the next packages', and every relation with both ends on it), `--schemas 3` (main and reporting; SQLite gets none), `--designed-tables 60 --views 40 --sequences 30` (on the two server databases, every other table with a foreign key to an earlier one), and two options the list above did not have: `--lookups 60 --lookup-attributes 60` (entities standing in for reference data until reference types exist: id, code, name, sortOrder, isActive and more; they carry the tag `lookup`, which is the synthetic project's own and has no meaning to the product). Every option added to `SyntheticModelOptions` defaults to off and draws from its own random stream, so the benchmark model is byte-identical to before. The verb replaces only a model it wrote (marker `.maquettiste/.cache/write-model.marker`). Output: 26,616 model files, 30 MB, valid with 0 diagnostics, written in about 4 s.
2. **The packed seed.** It is `src/editor/src/mocks/data/large.json.gz` (33 MB of JSON, 3.6 MB gzipped; the folder's own `.gitignore` keeps it out of git). It is not served by a Vite plugin: `model/largeSeed.ts` names it with `import.meta.glob(..., { query: "?url" })`, so the dev server serves it, `build:mock` emits it as an asset of `dist-mock`, the production build (whose mock code is dead) never sees it, and a missing file resolves to nothing. `npm run gen:scale` runs the script. Without `dotnet` on the path (or with `--docker`), it runs write-model in the `mcr.microsoft.com/dotnet/sdk:10.0` image, which carries a newer SDK than `global.json` pins (10.0.109, `rollForward: disable`), so the container first installs the pinned SDK into its `/tmp/dotnet` with `dotnet-install.sh --jsonfile /repo/global.json` (as `docker/Dockerfile` does). It runs as the calling user (`--user uid:gid`, `HOME`, `DOTNET_CLI_HOME` and `NUGET_PACKAGES` under `/tmp`) and builds with `--artifacts-path /tmp/mq-artifacts`, so the host's `bin/` and `obj/` are untouched and `tmp/scale` belongs to the caller. The Docker path writes a seed byte-identical to the host path's (about 30 s plus the SDK download). The script finds its entry point by real path (`fileURLToPath`, `realpathSync`), so a checkout path with spaces or a symlink works. In CI (`CI` set) the `scale` spec fails when the seed is missing instead of skipping. The seed keeps the bench's conventions but points its settings at the two packs the mock carries (sql-ddl, csharp-dapper), not the bench's fanout pack. No COOP/COEP headers yet (the memory target is pending).
3. **`?mock=large` loads it.** `mocks/browser.ts` fetches the seed before starting MSW, and `MockBackend` seeds from it; the in-browser 200-entity seed is `?mock=medium`. Without the file the page shows a banner naming `npm run gen:scale` and falls back to `?mock=medium`.
4. **The mock backend is ready for 27,000 elements.** The reverse reference index, the sub-element id map and incremental validation are one structure, `model/modelIndex.ts`, synced once per model version: a write re-checks the changed entries, the entries that reference any id they held or hold, and the entries of their MQ3001 name scopes (a stereotype, category tree or tag vocabulary change re-checks everything); a save's check moves the index to the candidate and back instead of validating the model twice. A unit test compares it with a from-scratch validation after edits. On the large model (Node, S2): backend start 1.0 s (parse, canonical text and SHA-256 of 26,617 files), first validation 0.7 s, then a save 45 ms, a validation 2 to 14 ms, `owner()` and `references()` about 1 ms (they scanned before: 16 ms and 47 ms). The worker is not done: after the start, what runs per request is small. `window.__mqPerf.mock` holds `{scenario, elements, seed: {fetchMs, parseMs, bytes}, backendMs, requests: [{method, path, status, ms}]}`. The mock follows the host's wire rules from one module, `mocks/wire.ts`: `model.changed` is cut to 200 KB (`MAX_EVENT_BYTES`, the largest prefix of changed-then-deleted items, `truncated: true`) as `ModelChangedEvent.Create` does, so the editor's truncated-refetch path runs against the mock; entity tags are read as `Api.TryReadTag` reads them (`"<hash>"`, `W/"<hash>"` or bare) for `If-None-Match` and `If-Match`; and ids are checked with the contract's Ulid pattern (first character 0 to 7).
5. **The same model in the real container.** `tmp/scale` can be opened with the compose recipe in phase2-design §6.4 (copy `packs/`, then `docker compose … --project-directory tmp/scale up -d`). The owner can then compare the mock with the engine.
6. **CI.** The editor workflow caches `large.json.gz` keyed on `bench/Maquettiste.Bench/**`, `src/Maquettiste.Engine/**`, `schemas/v1/**`, the script, `Directory.Packages.props` and `global.json`; on a miss it sets up .NET (with the NuGet cache) and runs `npm run gen:scale`; then `npm run e2e:scale` runs the Playwright `scale` project and its report is uploaded on every run.
7. **Measurements.** The editor's own marks are not started. The `scale` project measures from the page instead: a MutationObserver marks the first explorer row, and each interaction is timed from the event to the next task after React's flush plus a forced layout, with the long tasks that overlapped it. Budgets are (target + mock time) × 1.25. Measured on the first run (WSL2 laptop, Chromium): open, cold index 2.1 s (of which the mock is about 1.6 s), reload 2.1 s (the editor does not send `If-None-Match` yet, so the index answers 200 and the 3 s target applies), search keystroke p95 11 ms, expand and collapse of the largest group (694 rows; today's explorer groups by package, so no group reaches 5,000) 34 ms and 28 ms, over the 16 ms target: `explorerRows` rebuilds and re-sorts every row on each toggle. That miss is reported as a "known miss" annotation, not a failure, unless `MQ_SCALE_STRICT=1`. The targets that need unbuilt parts are skipped tests that name what they wait for.

**Left from S2:** the editor's own `performance.mark` pairs (`lib/perf.ts`, `window.__mqPerf` for index-received, tree-built, first-row-painted, search-answered and diagram-painted); the COOP and COEP headers on the preview for `measureUserAgentSpecificMemory()`; and running `MockBackend` in a worker, which is deferred because what runs per request after the start is small (item 4).

**Added by this revision** (each option defaults to off and draws from its own random stream, as in S2, so the benchmark model stays byte-identical):

- `--reference-types 800 --reference-fields 0..12 --reference-rows 3..400` (rows written as seeds, types spread over nested categories), once the reference-type kind exists (§8.3), replacing `--lookups`; and `--reference-typed 0.1`, the share of entity and relation attributes typed with a reference type, a fifth of them multi-value, so that `contains`-style relations with a reference-typed attribute are common.
- `--seed-rows 0..50` on a tenth of the entities and relations with attributes, once seeds exist (§8.4).
- `--locales 3` with 80 % of display names translated, written as per-locale, per-domain shards, once localization exists (§8.5).
- An `explorer.folders` entry in the seed's `maquettiste.json` for a project-defined stereotype the generator puts on 2 % of entities, so the project-folder path (§1.6) is exercised at scale.
- `--domain-vocabularies 0.2`: a tag vocabulary and a category tree on a fifth of the domains, once E5i exists (§1.11).

## 6. Migration

*Revised 2026-09-29 (phase 2 close-out): the status column checked against the source; round 7's left items are built; what remains is listed in the next paragraph and in HANDOFF.md, none of it a phase 2 gate item. Revised 2026-09-29 (after the demo): steps 14 and 16 are built in full (the Inheritance tab and Virtual group, the relationship Mappings tab, the scope warning of Move to domain…); the reference type row menu of §1.8 is built; the measured numbers below are the two `scale` runs after the optimization pass of §4.5. Revised 2026-09-29: the Status column says what is built and what remains; steps 1 to 12 and 14 to 16 are built, step 13 partly. Revised 2026-09-28: steps 1 and 2 are built; the editor wiring of the built contract is its own step; new steps for the tree's shape, highlighting, table detail and the editors; reference data, seed data and localization are separate workstreams; then the rail in step 5 and domain vocabularies as step 16.*

The order keeps the explorer working after every step: every index row is reachable in the tree after each step (an acceptance test below checks it), and nothing is removed before its replacement ships. Every step is measured against the large dataset, which exists. Sizes are rough, for one engineer or agent. Steps marked **contract** need engine, schema or functions work outside `src/editor`, and go through `docs/api/openapi.yaml` and the contract tests first; each editor step works without them, degrading to today's behaviour.

| # | Step | Status (2026-09-29, after the demo) | Files | Size |
| --- | --- | --- | --- | --- |
| 1 | Large dataset and perf harness | **Built**, including `lib/perf.ts` (`window.__mqPerf`) and COOP and COEP on the preview | §5 | 0.5 day (done) |
| 2 | Contract E5 to E5e | **Built**; the phase2-design §4.3 amendment is made (2026-09-29) | §4.1 | 0.25 day (done) |
| 3 | Wire the built contract into the editor | **Built** | `api/queries.ts`: `useElements` over `readElements` in chunks of 200 (falling back to chunked GETs with 16 in flight against an older server), the index query with `If-None-Match` and `structuralSharing: false`, `["tables", dbId]` over `getDatabaseTables` (background load after first paint, last-good fallback and stale badge, invalidation by kind); `canvas/model.ts` (`viewElements` from `ends`, "All of X" capped at 300) | 2 days |
| 4 | Tree model | **Built** | New `explorer/tree.ts` (one tree per explorer, domain nesting, Not in a domain first, kind folders, element children the index answers, the Databases shape with Default schema and Customised mappings, Diagrams, People and access, Other elements, project-defined folders, rolled-up counts, parent map, related maps, reveal path, patch) and `model/labels.ts` (the kind → folder table); `explorer.folders` in `schemas/v1/maquettiste.json` and `ProjectSettings` (**contract**, small). `explorer/filter.ts` is **not** touched: `Explorer.tsx` still calls `explorerRows` | 3 days (0.5 contract) |
| 5 | Tree rendering, keyboard, context menus, multi-selection | **Built**, rail in §1.0's order and **Pin beside…**; built 2026-09-29 (close-out): **Promote to entity** on a value object or a custom type (`explorer/promote.ts`: an entity with an id key and the value object's attributes, or one `value` attribute of the custom type's base; each entity attribute typed as it becomes a relationship; a use in a value object, a relationship or a key blocks it; one batch, one undo entry), **Apply stereotype…, Tag… and Set category…** on a row and on a multi-selection (`explorer/marks.ts`, `explorer/markDialogs.tsx`; the bulk inspector shares the batch). Left: Diagrams of this domain; Open in new tab | `explorer/Explorer.tsx` split into `Explorer.tsx`, `TreeRow.tsx`, `useTreeKeyboard.ts`, `RowMenu.tsx` (§1.8), switched to `tree.ts`; `explorerRows` deleted in the same change and `explorer/filter.ts` reduced to filter predicates; `state/store.ts` (`explorer` slice: expanded, followSelection, highlightRelated, selection); `app/navigation.ts` (`reveal` expands ancestors); `app/Rail.tsx` and `app/App.tsx` (*revised 2026-09-28*: the icons select the sidebar's explorer in §1.0's order, Settings gear and account menu at the foot, the pinned second explorer as a preference, selection following the active tab). Ships the Settings gear and the interim Databases explorer from the index, so no row loses its place when "Project" goes | 3.5 days |
| 6 | Naming pass, **after the owner answers question 7 (§8.8)** | **Built** in `model/labels.ts`; phase2-design §4.8 status corrected. Left: SPEC §14 wording and glossary (the owner's call, not an errata row) | `model/labels.ts`, `model/model.ts`, `app/Rail.tsx`, `palette/CommandPalette.tsx`, `workspaces/entities/EntitiesWorkspace.tsx`, `workspaces/entities/dialogs.tsx`, `inspector/*` (package field label), `workspaces/settings/SettingsWorkspace.tsx`, e2e selectors; in the same change SPEC.md §14 wording and glossary (the owner's call) and phase2-design §4.8. Until then steps 4 and 5 use the current words from `labels.ts` | 1 day |
| 7 | Search worker: tree filter and quick open | **Built**; 2026-09-29 the index bytes go to the worker as a transferable the moment the index is parsed and the worker parses them (ready 626 → 238 ms after the index, handoff 9.2 → 0.1 ms; the first-paint numbers did not move beyond the noise: over four runs on 2026-09-29, index arrives → first paint 140.7 to 250.8 ms against 188 (193 before), explorer build 36.3 to 51.6 ms against 38, first rows 27.3 to 38.6 ms against 25; all three known misses on some runs) | New `search/worker.ts`, `search/query.ts` (operators and qualifiers, §3.1), `search/rank.ts`, `search/client.ts`; `explorer/Explorer.tsx` (filter mode, hidden non-matches, "also matches" footer); `palette/CommandPalette.tsx` and a quick-open dialog on the same client | 3 days |
| 8 | Filters, scopes, pinned filter, favorites and recents | **Built**, including (2026-09-29) the category filter's sub-categories and project-defined folders over every category tree, the global one first, then each domain's (§1.11; `mergeCategoryTrees`) | `explorer/filter.ts` (kind, domain scope, category subtree, has errors, on diagram), filter bar, scope picker; `explorer.scopes` in `maquettiste.json` (**contract**, small) | 1.5 days |
| 9 | Related-element highlighting and presence | **Built, target missed** (`base` on entity rows included; selection → related rows highlighted 33.9 / 32.4 ms median against 16 ms, was 44 ms, §4.5; 36.7 ms on 2026-09-29) | `TreeRow.tsx`, the related maps from step 4, the "n related" and "n people here" badges, presence avatars, the preference; `base` on entity rows (E5h, **contract**, small) | 1.5 days (0.5 contract) |
| 10 | Canvas in step | **Built, target missed** (tree drag and drop, Show on canvas, On this diagram chip; select on canvas → row revealed 137 ms against 50 ms, §4.5; 2026-09-29: 138.7 ms, and the same with the reveal and scroll as layout effects in one commit, so the cost is the selection commit of the other panels; the scale test waits on the explorer-settled flag, which fixed its full-run timeout); membership dots on member rows and, built 2026-09-29, a hollow dot with the count on a collapsed domain or folder holding members of the active diagram (`canvasCounts`) | `workspaces/entities/EntitiesWorkspace.tsx` (tree drag and drop, membership dots, "Show on canvas") | 1.5 days |
| 11 | References tab, breadcrumbs, go to definition, history | **Built** | New `references/ReferencesPanel.tsx`, `app/Breadcrumbs.tsx`; `app/BottomPanel.tsx`, `app/shortcuts.ts`, `inspector/*` (F12 on reference fields) | 2 days |
| 12 | Incremental index patching | **Built, target missed** (`model.changed` → tree and search 29.2 / 24.9 ms median against 16 ms, budget 20 ms, was 34 ms, §4.5; 26.7 ms on 2026-09-29) | `api/queries.ts`, `realtime/events.ts` (from `ModelChangedEvent` summaries, built server-side), `explorer/tree.ts` (patch), search worker update message | 1–2 days |
| 13 | Table detail and the Databases root in full | **Partly built**: E5f, the per-database resolve, table children and column-level mapped by; built 2026-09-29: above 300 tables the Database screen draws the selected table and its foreign-key neighbours (at most 300, `scopeTables`), and with no table selected the list-and-DDL form. Left: Create entity from table, Copy SELECT, Copy DDL (the scale measure "expand a table row ≤ 200 ms" is written and in budget, §4.5) | E5f (`enumId` on `TableSummary` withdrawn with the enum lookup-table retirement, MQ7012), and a per-database or incremental resolve behind E5c so a change costs ≤ 300 ms per 700-table database (**contract** and engine; built 2026-09-29: E5f and the per-database resolve, measured in §4.5; `enumId` dropped with the enum lookup-table option, see "As built (E5i)"); the table's children in the tree, column-level "mapped by"; Database screen scoped to a table and its neighbours (cap 300), the list-and-DDL form above 300 tables; "Create entity from table", "Copy SELECT", "Copy DDL" (editor side built 2026-09-29 except the scoped canvas and the three actions, §1.3) | 5 days (3 contract and engine) |
| 14 | Element editors with General mode | **Built, target missed** (next entity 100.3 / 106.1 ms median against 100 ms, budget 125 ms, was 147–178 ms, §4.5; 2026-09-29: the entity editor stays mounted across entities, the base picker's options are built once per index): preview and pinned tabs, General mode (Follow selection), the entity, relationship, enum, value object, custom type and domain editors; the entity editor's Inheritance tab (base, derived entities, strategy per database with its source; `editors/inheritance.ts`), the Inherited and Virtual groups under the attribute grid and **New relationship…** on its Relationships tab; the relationship editor's Mappings tab (shape and junction table per database); the domain editor's header no longer repeated on General; built 2026-09-29: the Inheritance tab and the Mappings tab are editable (base entity picker refusing a loop, `wouldCycle`; strategy per database on the root's mapping; discriminator value on the entity's mapping; relation shape, junction table and promoted name, creating the relation's mapping when there is none; `editors/mappingEdit.ts`). Left: the walk's remaining few ms (the Mappings tab's per-entity queries) | New `editors/EditorTabs.tsx` (preview and pinned tabs), `editors/EntityEditor.tsx`, `editors/RelationshipEditor.tsx`, and the enum, value object and custom type editors on one frame; reuses `inspector/AttributeGrid.tsx`, `inspector/SchemaForm.tsx` and `state/drafts.ts` | 4 days |
| 15 | Docs | **Built** 2026-09-29 (round 7 included: layout, page state, Databases mapping, seed data, Settings tabs): user guide, demo, skills, the status columns here and in reference-types-seeds-localization.md §5, SPEC errata E6–E16; phase 2 close-out (2026-09-29): the user guide reviewed end to end against the final UI (row menu items, Inheritance and Mappings editing, the Database screen above 300 tables, page state), README.md "What is built", `phase-3-brief.md` | This file marked as done; the remaining phase2-design notes | 0.5 day |
| 16 | Domain-scoped tags and categories (*new 2026-09-28*, §1.11) | **Built**, including the scope warning of Move to domain… (lists the marks that fall out of scope; **Move anyway**) | E5i (**contract** and engine): optional `package` in `schemas/v1/tag-vocabulary.json` and `category-tree.json` and in `Model/Vocabularies.cs`; one vocabulary of each kind per scope in `KindInfo`, `ModelSnapshot` and MQ1009; `ModelIndexer.PackageOf`; MQ2005 and MQ2006 along the domain chain; MQ2008 and MQ3021 in `RuleCatalog` and `BuiltinRules`; the mock's `model/modelIndex.ts`. Editor: the Settings Tags and Categories tabs, the domain editor, the pickers and chips (built 2026-09-29, "As built (step 16, editor side)" in §1.11) | 3 days (2 contract and engine) |

**Inspector (round 6, 2026-09-29, built).** `inspector/context.ts` (pure, `inspectorContext`) decides what the inspector shows: the active element editor tab; nothing on Settings and Reference data (the pane collapses); on Generate the open pack, or the unit a pack tree row focused, else "Select a pack in Generate"; otherwise the active explorer's own selection (`selectionBy[explorer]`), else "Select an element in <explorer>". A rail switch never shows an element selected in an explorer the user has left. Built 2026-09-29: each explorer shows its own selection and the pinned second explorer records its selection under its own id (`select(ids, focus, explorer)`, `selectionFrom`), which the inspector follows while that explorer is pinned; a row focused in the Units grid sets `packFocus` as a pack tree row does.

**As built (2026-10-01): the inspector is about the side you are on.** The owner: "when I'm in the database, should the inspector be about the database not the entity, and when I'm in the model, the inspector should be about the entity?" Yes. A new context mode `table` (`{ database, key, column }`, from the store's `inspectedTable`) wins over the explorer selection while the Database screen shows or the Databases explorer is the selection's explorer; selecting elements clears it. Every way to a table sets it by the table's resolved key, a file or not: a canvas pick and the Tables list (`pickTable`), a click on a table row or a column row in the Databases explorer (the click no longer selects the table's file, so a table with a file and one without show the same thing), Enter, a double click and the row menu's Open (`openTable`: the Database screen focused on the table), Go to table on an entity row, a palette result for a table, and `reveal` of a table file (a search result, a problem, a Used row; `tableKeyOfDoc`, else the summaries). `inspector/TableInspector.tsx` shows it: Properties (what the table derives from or is bound to, with **Go to entity** to the entity editor in the Domain model; the column picked in the grid, its physical fields written through `workspaces/database/useTableFile.ts`, the grid's own path and queue; the table's own fields through the table file's draft, read-only for a projected table without an overlay), JSON (the file) and Used. Tables still have no element editor: "Open" on a table is the Database screen, never the entity editor, which is reached from the Domain model or through Go to entity. While the model has errors (such as MQ4005 over the resolved columns) `useDatabaseView` keeps the last resolved view, marked stale, so the screen, the grid and the table inspector stay up with the errors shown above the diagram. Tests: `tests/unit/inspector-context.test.ts`, `tests/unit/database-columns.test.ts`, Playwright `database-columns.mock-only.spec.ts`.

**Round 7 (2026-09-29, built).** Panels and page state: `state/layout.ts` (explorer, inspector, bottom panel, editor tabs and top bar controls collapse; sizes and collapsed flags in `mq.layout`, the 0.2.0 form still read; Alt+Shift+E/P/J/O/H; palette Toggle <panel> and Reset layout) and `state/pageState.ts` (`mq.page.<project>`: explorer, expanded rows, selections, editor tabs, Generate packs, Settings tab; the address wins). Explicit mapping (D46, E24): the New database dialog's "Map domains by convention", **Map to database…** on domain and entity rows, the empty-database hint row, the inspector's Mapping section. Seed data: the entity's Seed data child and menu items (Edit seed data, Import seed CSV…) open the editor's Seed data tab; domain and Reference data header menus have Export all seed data and Import seed data…. Built in the close-out (2026-09-29), so nothing of round 7 is left: **Reset layout and page state** beside **Reset layout** (which keeps page state); page state keyed by the host's `projectKey` (a hash of the checkout), read once from the name key of an older host and moved; the old `mq.explorer.active` and `mq.explorer.expanded.*` keys removed after migration (`LEGACY_EXPLORER_KEYS`); a domain's **Export this domain's seed data** exports the seeds of that domain and its sub-domains.

Remaining for the explorer (2026-09-29, after the close-out): the items the Status column lists as left, about 1.5–2 working days (Create entity from table, Copy SELECT, Copy DDL; Diagrams of this domain, Open in new tab); the known misses of §4.5 (first rows, `model.changed`, highlight, reveal, the General-mode walk) are performance work with no step of their own. Built since the first estimate of what remained: the Inheritance tab and Virtual group, the relationship Mappings tab, the Move to domain… scope warning, the reference type row menu (the Reference data screen's type actions, less Export CSV), On delete following the kind in the New relationship dialog, and the Database screen listing a database created a moment ago. The first estimate (about 32.5–36.5 working days, of which about 6.5 contract and engine work) is spent.

**Reference types, seeds and localization** are one separate workstream, sized and ordered in `reference-types-seeds-localization.md` §5 (nine steps, about 26–29 working days, contract first). Its editor steps touch the explorer in three places, all of which this plan leaves room for: the Reference data leaf row in `explorer/tree.ts` (its step 7), the Seed data kind folder and entity child (the index `target` and `rowCount`), and labels and search in the content locale (`?locale=` on the index, its step 5). The explorer's steps do not wait for it.

Steps 3 to 5 and 7 come first (the tree and search); steps 8 to 14 and 16 can run beside that workstream.

**Acceptance tests**

- **Unit tests (Vitest), `tree.ts`:**
  - Catalog nests under Billing; Billing is a top-level row of the Domain model explorer.
  - Each explorer builds its own tree; no tree has a Project, Settings or Reference data row; People and access, when present, then Other elements, are the last top-level rows of Domain model.
  - No "Project" group; "Not in a domain" appears only when non-empty, and first.
  - A top-level package (`package: null`) is a top-level domain, not an orphan; an entity with no package is an orphan.
  - Every element row sits in a folder named after its kind (no mixed folders), except domains, databases, schemas and project-defined folders; each project-defined folder holds a single kind; property test over the medium seed.
  - Every index row is reachable in an explorer, on the billing fixture and the medium seed (run from step 4 on), except reference types (the Reference data explorer and search) and vocabularies and stereotypes (the Settings tabs and the domain editor). This covers databases, views, sequences, table overlays, mappings and vocabularies with and without the E5 `database` field.
  - A database with one named schema shows that schema's name (`public`); a SQLite database, and tables with a null schema, show a Default schema node.
  - An element matching a project-defined folder is listed there once, not also in its kind folder, and the kind folder's tooltip counts it.
  - An unknown kind lands in Other; an actor lands in People and access.
  - Rolled-up counts are correct; a parent cycle does not loop.
  - Expand and collapse splice the right ranges; the reveal path is right.
  - The related set of an entity, a relation and a table is right (§1.9), and a collapsed folder's "n related" count matches; base and derived entities are related when rows carry `base`.
  - Property test: patching one change gives the same tree as a full rebuild, on 1,000 random changes over the medium seed.
- **Unit tests (Vitest), other modules:**
  - `search/query.ts`: `*`, `^`, `~` with `%`, `=`, case-insensitivity, qualifiers.
  - Filter mode: non-matches hidden, ancestors kept, a matching domain keeps its subtree, "n of m" counts, the "also matches" footer counts reference types.
  - `rank.ts` ordering fixtures (exact > prefix > word-start > substring > fuzzy, and the tie-breakers).
  - Filter predicates and scopes.
  - Vocabulary chain (§1.11): a Billing › Catalog element is offered Catalog's, then Billing's, then the global vocabularies, and never Sales's; a domain chip names its domain.
  - Engine and contract tests (step 16): a global and a Billing vocabulary load side by side; a second one in the same scope is MQ1009; a Sales tag on a Billing entity is MQ2008; a Billing tag key that a global vocabulary declares is MQ3021.
  - Batched reads chunking and falling back.
  - Index patching from `ModelChangedEvent` summaries and from `ChangeSet.deleted`.
  - Table summaries on an invalid model: the tree shows the last good tables with the stale badge.
  - Mock contract suite extended to E5f.
- **Component tests:** the tree's ARIA attributes (`aria-level`, `aria-setsize`, `aria-posinset`, `aria-multiselectable`) on rendered rows after scrolling; every key in §3.4; multi-selection refuses a second kind; each row menu of §1.8 offers only valid actions for a multi-selection; the entity editor's Inheritance tab is disabled outside a hierarchy; General mode keeps the current tab when the selection moves; presence avatars on element rows and the "n people here" roll-up on a collapsed domain and folder.
- **Playwright on the mock billing model** (every push):
  - The rail lists Domain model, Reference data, Databases, Diagrams, Generate, then Settings and the account menu at its foot; the gear opens Settings with its seven tabs as built (§1.6).
  - The Domain model explorer shows Billing › Catalog, and Billing › Entities › Invoice.
  - Databases › main › billing (the fixture's `defaultSchema`, shown by name) › Tables lists the invoice table; selecting it, then switching the rail to Domain model, shows Invoice highlighted.
  - Keyboard-only walk: expand Billing, open Invoice, F12 on a relation end, Shift+F12.
  - Tree filter `^inv` hides Product; quick open "invli" puts InvoiceLine first.
  - Drag Product onto the diagram.
  - Axe scan of the explorer and the entity editor in both themes.
  - Copy check: no visible tree label (tooltips excluded) contains a word from the jargon list (package, workspace, convention, scalar).
  - A domain-named group under Diagrams shows the domain icon and the "Diagrams whose home is Billing" tooltip.
- **Playwright on `?mock=large`**, the `scale` project (built; every push, with the measured mock allowance of §4.5; its skipped tests name what they wait for and are turned on as the steps land):
  - Explorer usable and first paint under target.
  - Tree filter and quick open keystrokes under 50 ms plus the allowance.
  - Expand the largest domain and the largest Tables folder, then scroll to the end with no long task over 100 ms.
  - Select an entity with 40 relations: highlights painted within a frame.
  - Reveal from a canvas selection.
  - Open a table from the Databases root: the Database screen draws 300 table nodes or fewer.
  - Open a 300-node diagram: painted under 2 s plus the allowance, and a pan sampled at ≥ 50 fps with a `requestAnimationFrame` counter.
  - General-mode editor: walk 20 entities on the Mappings tab under 100 ms each plus the allowance.
  - One simulated external change patches the tree without an index request (asserted on the network log).
- **Live check** (manual, before the phase closes): the same Playwright `scale` spec with the `live` project against `tmp/scale` in the container. The numbers are recorded in the bench report.

## 7. Open questions for the owner

*Revised 2026-09-28.* The first round's questions and where they stand; the questions that remain are listed at the end of the document (§8.8), after the decisions they follow from.

1. **Domain hierarchy** (depth and shape): still open, §8.8 question 1.
2. **Tables** (under the database, under domains, or both): answered by the owner's direction. Tables live under their database, then their schema; "Group by domain" is a view option; "mapped by" highlighting links the two roots (§1.3).
3. **Names in search** (business name, table name, code): still open, §8.8 question 6.
4. **Diagrams** (one domain each, or cross-domain folders): answered. Diagrams sit outside the domains in their own root; a diagram's home domain, when it has one, groups it (§1.5).
5. **Words**: partly answered ("Enums" stays, from the owner's own words); the rest is §8.8 question 7.
6. **Your real shape** (an anonymized export): still open, §8.8 question 8.

## 8. The owner's decisions and the patterns behind them

*Revised 2026-09-28: rewritten. The first version of this section (borrowed patterns, reference data inside a domain as stereotyped entities, agents as entities, localization, the editor API) is replaced: reference data leaves the tree for its own screen and its own kind, reserved stereotype names are gone, and packs, not the engine, decide every physical form. The first version's §8.2 (reference data inside a domain) and §8.4 (localization) are superseded by `reference-types-seeds-localization.md`, which is the binding design for reference types, seeds and localization; §8.3 to §8.5 below only summarize it and say what it means for the explorer.*

### 8.1 Prior art (kinds of tools, described by pattern; none of this is product vocabulary)

| Kind of tool | Pattern | What Maquettiste does |
| --- | --- | --- |
| ORM designers, model explorer | A Project node; an Entity Model root; groups, one level deep (an unnamed one first, then named ones), holding one folder per definition kind | Domain model root, kind folders in each domain, Not in a domain first (§1.1, §1.2); the Project node gave way to the rail (§1.0) |
| UML and data modeling tools | A package browser with nested packages | Domains nested as packages (§1.2); the ORM designers' groups do not nest |
| ORM designers, model explorer | Model View definitions outside the groups, because they span groups (still inside the Entity Model root) | The Diagrams root (§1.5), lifted to a root because SPEC §5 keeps the view layer apart |
| ORM designers, catalog explorer | Catalog › Schema (or Default) › Table, View, Sequence › Field, Foreign Key Constraint, Unique Constraint; reverse engineer, copy select query, bulk reverse engineer | The Databases root, Default schema, table children, "Create entity from table", "Copy SELECT" (§1.3) |
| ORM designers, both explorers | Search box with `*` contains (default), `^` starts with, `~` like with `%`, `=` equals; case-insensitive; non-matching nodes hidden | The tree filter (§3.1), also accepted by quick open and the Reference data list |
| ORM designers, both explorers | A context menu on every node; multi-selection of nodes of one type | §1.8 |
| ORM designers, model explorer | Related elements highlighted on selection, switchable | Related-element highlighting (§1.9) |
| ORM designers, catalog explorer | Project elements mapped onto the selected catalog element highlighted in the Project Explorer | "Mapped by" (§1.3) |
| ORM designers, entity editor | Top controls (name, group, identifying fields with Edit…, subtype, is abstract) over tabs (Fields, Field Mappings, Inheritance Info disabled outside a hierarchy, Code Generation Info); a General Entity Editor that keeps the sub-tab while the selection moves | The element editors and General mode (§3.6) |
| Data modeling tools | Conceptual and physical models kept apart, with a mapping between them | The Domain model and Databases roots, following SPEC §5's layers |
| Data modeling tools | Subject areas | Diagrams as subject areas, "All of Billing" capped at 300, "add related" |
| Data modeling tools | Domains as reusable attribute types, with a usage list | Custom types and reference types as attribute types; the reference type's Used by tab (§1.7) |
| Data modeling tools | Impact and lineage analysis across models | Where used across layers, with a Generated files group from the last plan (§3.3) |
| UML modeling tools | "Find in all diagrams"; "Locate in project browser" | "Show on canvas: In 3 diagrams" (§1.5); Follow selection and reveal |
| Database IDEs | Lazy schema trees with counts; a pinned filter; scroll from source; favorites | The Databases root over E5c and E5f; the pinned filter (§3.2); Follow selection; Favorites and Recent (§3.3) |
| SysML v2 | Views defined by criteria, not by hand-picked membership | Project-defined explorer folders (§1.6) and scopes (§3.2); later, a diagram with a criteria filter the engine keeps current |
| SysML v2 | Definitions versus usages | A reference type is a definition, a reference-typed attribute a usage; the Used by tab lists usages; relation ends shown with role names |
| SysML v2 | Metadata definitions | Stereotypes with extension schemas, all project-defined (§8.6) |
| Code editors, *2026-09-28* | Activity bar: a strip of icons, one sidebar view at a time, Manage and Accounts at the foot | The rail (§1.0); side-by-side explorers only as a preference |
| Language IDEs | Structure view; scopes; go to definition; find usages; Search Everywhere | Element children and editor tabs; scopes (§3.2); F12 and Shift+F12 (§3.3); quick open across all kinds (§3.1) |
| Code editors | Outline; breadcrumbs with sibling menus; quick open with prefixes; preview tabs | `@` in quick open; breadcrumbs (§3.3); `>`, `@`, `#` (§3.1); preview and pinned editor tabs (§3.6) |
| ORM designers | Target Framework node; Relational Model Data Storage node; Derived Models root with Typed List and Typed View folders | Refused (§8.2) |
| Data modeling tools | A logical model between conceptual and physical; a physical model generated as a one-shot copy; one diagram switching logical and physical display | Refused (§8.2) |
| UML modeling tools | Diagrams inside packages; a free-form package tree | Refused (§8.2) |
| Database IDEs and code editors | Live introspection of a connected database in the tree; Project, Packages and Scopes views; multi-root workspaces | Refused (§8.2) |

### 8.2 Patterns considered and refused

- **A target-framework node in the tree**: generation targets are template packs chosen in the Generate screen, not a property of the model, so the tree has no place for them.
- **A storage node under the relational model**: a Maquettiste database is a model with no connection or storage container, so there is nothing to show.
- **A derived-models root with typed-list and typed-view folders**: SPEC §10's projections and queries cover these and belong in their domain's kind folders next to the entities they shape.
- **A logical model between the conceptual and physical ones**: SPEC §10's conventions and the mapping layer do that job without a third copy of the model to keep in step.
- **Generating a physical model as a one-shot copy**: tables here are designed or resolved live from conventions, and for the features in this document the physical form is the templates' choice (principle 1); a copy would drift.
- **One diagram switching between logical and physical display**: entity diagrams and table diagrams stay on separate screens, so a user always knows which layer an edit changes.
- **Diagrams inside packages**: diagrams span domains, so they sit in their own root (§1.5).
- **A free-form package tree where any element can go anywhere**: it breaks the kind-folder rule of §1.1.
- **Live introspection of a connected database in the tree**: the Databases root is the model; reading a live database is SPEC §18's later importer, not a way of browsing.
- **Several alternative views of the same project (by project, by package, by scope)**: one explorer per layer with scopes and search covers them, and a second view of the same layer would weaken "you always know what you are browsing".
- **Part, port and connection usages as tree nodes (SysML v2)**: not a data-model concern; only the definition and usage split is kept.
- **Multi-root workspaces**: an editor session serves one project (SPEC §14).

### 8.3 Reference data

The owner's decisions: one place to manage all reference data, a list of reference types that can gain fields, usable as field types (nullable or not, single or multi-value) on entities and on relation attributes; enums stay a separate kind; reference types appear only in the Reference data screen; generators decide the database form ("CREATE TYPE, or CHECK constraint, or whatever"). The design is `reference-types-seeds-localization.md` §1 and §4. In short:

- A new element kind, `reference-type`, with built-in `code` and `label` fields, user fields in the attribute model, no package (categories group the types), and rows held in seeds (RS1 to RS4 there).
- An attribute on an entity, value object or relation, or another reference type's field, uses one through `type: { "ref": … }`, with `required` and `collection`; the owner's `contains` relation carries `quantity` and `unitOfMeasure`. Relation ends stay entities.
- The engine synthesizes no table, column, constraint or type for it. Physical form is template-defined unless the project declares its own storage strategies and chooses one (RS5); the Databases root therefore never shows reference-data tables (§1.3), and the screen's Storage tab previews what each database's templates emit.
- Enum or reference type: an enum is a closed set whose members belong to the code; a reference type is data that grows and carries fields. The attribute type picker lists them in separate groups.
- **Withdrawn from the first version**: a reserved stereotype family on entities, a Reference data folder in each domain, and tenant-extension structures produced by the packs. The names broke principle 2, the folder put reference types in the tree, and extension by tenants is the project's own concern, expressible with its own marks and templates.

For the explorer: the Reference data explorer (§1.0, §1.7), search and highlighting (§1.7), and Go to definition (§3.3).

### 8.4 Seed data

The owner's decision: seeds as a generic feature "for any generic element/entity". The design is `reference-types-seeds-localization.md` §2: a `seed` kind holding rows for one target (an entity, a relation or a reference type), any number of seeds per target told apart by their own tags, columns keyed by id so renames never touch seeds, one row per line, CSV import and export, and templates deciding what rows become (the engine synthesizes nothing). Seeds move from phase 4 to the phase that ships reference data (RS6).

For the explorer: a **Seed data** kind folder in each domain for the seeds of its entities and relations, a **Seed data** child on an entity row (§1.2), a Seed data tab in the entity and relationship editors (§3.6), and a reference type's rows in the screen's Rows tab, not in the tree.

### 8.5 Localization of the standard fields

The owner's decision: display names (singular and plural) and descriptions for entities, fields, enums and the rest, "generic and simple", without crowding the interface. The design is `reference-types-seeds-localization.md` §3: default-language texts stay in the element files, other locales live in per-locale, per-domain sidecar shards, the project settings declare the locales and fallbacks, templates read translations through helpers with a fallback chain, and packs decide what to emit.

For the explorer and the editors: nothing appears while the project has one locale. With two or more, the top bar's content-locale switcher sets the language of tree labels and search (`?locale=` on the index, §4.1), the editors and inspector show a collapsed Translations section (§3.6), the tree filter gains a **Missing translations** chip (§3.2), and Settings › Locales holds the completeness view.

As built (reference-types-seeds-localization.md §5 step 8, `src/editor/src/l10n/`): the switcher, the collapsed Translations section in the inspector and the editors' headers, Settings › Locales with the completeness matrix and the translation queue, the Rows grid's locale columns, a content-locale chip in the explorer header (removing it returns to the default locale), and label patching from `model.changed` `translations`. The **Missing translations** filter chip is not built yet.

### 8.6 Project-defined marks, and agents as an example

The product reserves no name (principle 2). The owner's agent definitions show how marks work: they are entities carrying a stereotype the project defines, with an extension schema whose typed properties the inspector renders as a form (SPEC §18), generated by packs like any other entity. The explorer lists them in a folder of their own only because the project says so in **Explorer folders** (§1.6); the stereotype's own icon and colour (SPEC §18) show on their rows. Nothing in the editor, the engine or the packs shipped with the product tests for a particular stereotype, tag or category name. Promotion to an element kind waits until a template needs more than a stereotyped entity can express; processes (phase 3) give agents a role as actors.

### 8.7 Agents and the editor API

Edits made on disk by an external tool reach the editor through the file watcher and `model.changed` (verified on the running container). The MCP server is built: `maquettiste mcp` (`docs/mcp.md`) serves a repository's model over stdio, in-process over the engine, with the same `ModelStore`, write path, validation, plans and error codes as the editor API, so any MCP client works on the user's own plan. The in-app Assist panel (SPEC §14) follows, on the host's AI hook.

### 8.8 Questions that remain for the owner

Questions about reference types, seeds and localization themselves (enum lookup tables, other translatable fields) are in `reference-types-seeds-localization.md` §7. These remain for the explorer:

1. **Domain hierarchy.** How deep does it go, and what shape: about 40 flat domains, or about 8 areas of 5 domains with some sub-domains? Does a domain with sub-domains also hold entities directly? This sets the generator's defaults and question 2.
2. **Sub-domains.** Listed directly under their parent domain, before its kind folders (proposed, §1.2), or inside a "Sub-domains" folder, which is stricter about "you always know what you are browsing" but adds a level at every depth?
3. **Project-defined folders.** Is an Explorer folders setting (§1.6), where the project names a stereotype, tag, category or property condition and the matching elements get a folder of their own in each domain, the right way to give your agent definitions and similar sets a place, or should such marks only show as icons and filters?
4. **Tables grouping default.** Flat and sorted by name under each schema (proposed), or grouped by owning domain by default? Flat reads well at your roughly 700 tables per database; at the 10,005 tables of the scale model's `main`, grouping by domain gives folders of a few hundred, which may be the better default for databases above a threshold (for example 2,000 tables).
5. **Where editors open.** As tabs in the centre beside the canvas, with a General mode tab that follows the selection (proposed, §3.6), or in place of the inspector?
6. **Names in search.** Do you look things up by business name ("Invoice line"), table name (`ar_inv_line`) or a code? This sets the ranking, and whether elements need an "aliases" property.
7. **Words.** Are "Domain", "Sub-domain", "Domain model", "Explorer", "Screen", "Relationships", "Value objects", "Custom types", "Reference data", "Seed data", "Customised mappings" and "Mapped automatically" the words you would use? ("Databases" is settled: the owner chose it on 2026-09-28.) Should "Domain" also replace "package" in the CLI's help and generated docs, or only in the editor? The naming step waits for this.
8. **Your real shape.** Could you share an anonymized export of one large project (element counts per domain, a DBML or DDL dump with renamed identifiers, the number of reference types and rows)? It would let the generator match your distribution rather than a guess.
