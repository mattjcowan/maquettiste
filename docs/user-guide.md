# Maquettiste editor: a first guide

Maquettiste turns one model of your application, stored as JSON files inside your repository, into most of its code.
The editor is where you look at and change that model. This guide walks through what you see the first time.

## What you are editing

A project lives under a `.maquettiste/` folder in a repository. It holds:

- **Domains**: business areas (Billing, Sales) that group elements and become folders and namespaces in generated
  code. A domain inside another is a **sub-domain**. The model files call a domain a `package`, and so does the CLI.
- **Entities**: the business objects (Customer, Invoice) with their attributes and keys.
- **Relationships**: named links between entities ("Customer places Invoice"), which can carry attributes of their
  own. The model files call them relations.
- **Enums**: closed sets of named values that belong to the code (InvoiceStatus).
- **Value objects**: reusable groups of fields without identity, such as Address or Money.
- **Custom types**: named restrictions of a built-in type, such as Email.
- **Reference data**: reference types, sets of rows managed as data (units of measure, countries), each with a code
  and a label, usable as the type of an attribute.
- **Seed data**: rows an element starts with (an entity's initial rows, a reference type's rows).
- **Databases**: the physical side. A database holds only what is mapped to it: the domains it takes **by
  convention** (all of them, the ones you pick, or none) and the entities mapped to it one by one. Tables of mapped
  entities follow the naming conventions; a table that differs has a **customised mapping** (an override file).
- **Diagrams**: saved views of the canvas. A diagram only remembers which elements it shows and where.
- **Templates**: the packs that turn all of the above into files (SQL, C#, TypeScript, docs).

The sample project you will see first is **billing**: a small invoicing model with five entities, four relationships, one
enum, two custom types and one database. It exists so the editor has something realistic to show; it is not part of
your repository's model.

## The layout

The editor is laid out like an IDE:

| Area | What it holds |
| --- | --- |
| Top bar | The project name (the whole model and its settings), git branch and changed-file count, the command palette (Ctrl+K or Cmd+K) and the theme |
| Rail | The explorers: Domain model, Reference data, Databases, Diagrams and Generate; Settings and the account menu at the bottom |
| Sidebar | The explorer the rail selected: a tree with a "Search the model" box, filterable by tag, category and stereotype |
| Center | The current screen: the canvas, a grid or an editor |
| Right | The inspector: the properties of what you are working on, editable (see below); none on Settings and Reference data |
| Bottom | Problems (live validation), generation output, and a diff viewer |

### Hiding panels, and what the editor remembers

Every panel hides and comes back, so a crowded screen can get the room it needs. Each has a button in its header, a
command in the palette (Toggle explorer, Toggle inspector, Toggle bottom panel, Toggle editor tabs, Toggle top bar
controls) and a shortcut:

| Panel | Shortcut |
| --- | --- |
| Explorer (the sidebar) | Alt+Shift+E |
| Inspector | Alt+Shift+P |
| Bottom panel | Alt+Shift+J |
| Editor tabs | Alt+Shift+O |
| Top bar controls (git status, content locale, undo and redo, theme, live status, user) | Alt+Shift+H |

The shortcuts use the physical key (Option+Shift on a Mac) and do nothing while you type in a field; Escape never
closes a panel. A hidden panel leaves a slim edge where it was (a thin strip beside the rail, at the right edge, or
along the top of the centre area; the bottom panel keeps its tab row, the top bar its button): click it to bring the
panel back. Clicking an explorer on the rail also brings the sidebar back.

The layout (which panels are open, the sidebar, inspector and bottom panel sizes, and a second explorer pinned beside
the first) is kept in this browser and comes back when you open the editor again. **Reset layout** in the command
palette opens every panel at its default size and unpins the second explorer; it leaves the page state below as it
is. **Reset layout and page state** does the same and also forgets this project's page state: the explorer, the
expanded rows, the selections, the editor tabs, the Generate packs and the Settings tab go back to their defaults. A layout saved by 0.2.0 (panel sizes only) is read and kept.

The page state is kept per project in this browser (by the checkout the editor serves, so two projects with one name
stay apart and renaming a project keeps it) and restored on reload: the explorer showing, each explorer's
expanded rows and selection, the open editor tabs and the active one, the packs ticked on the Generate screen, and
the Settings tab Settings opens on. The address still says which screen, diagram or database and which selection
shows, and wins where the two differ, so a shared link opens what it names.
The page state belongs to the project's name, so renaming the project starts from a fresh one. Both are kept in this
browser only: another browser, or a private window, opens with the default layout and nothing open.

### The rail: one explorer at a time

The rail's icons, top to bottom, are **Domain model**, **Reference data**, **Databases**, **Diagrams** and **Generate**;
**Settings** (the gear) and the **Account** menu sit at its foot. A click on an icon shows that explorer in the sidebar,
and only that one: each explorer has its own tree, its own expanded rows and its own filter, kept when you switch away
and back. A second explorer can stay open beside the first: the explorer header's menu has **Pin beside…** (and
**Unpin**), a per-browser preference. The header also offers **Collapse all** and **Highlight related elements**: with it
on, selecting an entity tints the relationships, tables and columns that belong to it, and a collapsed folder shows
"n related".

The inspector follows what is active, never an element selected somewhere you have left. Each explorer keeps its own
selection: after a rail switch the inspector shows that explorer's selection, or "Select an element in Diagrams" (the
explorer's name) when it has none, and switching back brings the earlier one back. While an element editor tab shows in
the centre, the inspector shows that tab's element. On **Generate** it shows the open pack, or the unit a pack tree row
opened ("Select a pack in Generate" on Plan). **Settings** and **Reference data** have no inspector: those screens are
their own panel. Going to an element from one of them (a translation queue's "Open element", a where-used row) opens
the Domain model screen with the element selected.

### Search and filters

The **Search the model** box filters the explorer as you type: rows that do not match are hidden, their domains and
folders stay so you know where a match lives, and the header counts "n of m". Matches of other kinds, such as reference
types, are counted in an **Also matches** footer. Quick open (Ctrl+P or Cmd+P) and the command palette take the same
syntax:

| You type | It matches |
| --- | --- |
| `inv` or `*inv` | names that contain "inv" (the default) |
| `^inv` | names that start with "inv" |
| `~inv%line` | a like pattern: `%` matches any run of characters, anchored at both ends |
| `=Invoice` | exactly "Invoice" |
| `kind:enum`, `in:Billing`, `tag:pii`, `st:aggregate`, `cat:finance` | narrow by kind, domain, tag, stereotype or category; quote a value with spaces (`in:"Order taking"`); several values of one qualifier mean "any of" |

Matching ignores case. The **Filters** button beside the box adds chips: **Kinds**, **Domain**, tags, categories,
stereotypes, **Has errors** and **On this diagram**; the **Active filters** row shows them, each removable. **Save the
filters as a scope** names a set of chips: **Your scopes** stay in this browser, **Team scopes** come from
`maquettiste.json` (`explorer.scopes`). **Pin the filter** keeps the explorer's filter across reloads. **Favorites** and
**Recent** at the top of the explorer list the elements you starred (the row menu's **Add to favorites**) and the ones you
opened last.

The keyboard walks the tree: the arrows move and expand, Enter opens, F2 renames, Delete deletes, and Shift or Ctrl with a
click or an arrow selects several rows of one kind. Every row has a right-click menu (also Shift+F10) with the actions
that fit it, such as **Where used**, **Show on canvas**, **Add to diagram**, **Move to domain…**, **Go to table** or
**Open mappings**. **Move to domain…** warns before a tag or category declared by a domain would fall out of scope in
the new place. F12 on a reference goes to its definition and Shift+F12 lists where the element is used.

### Element editors and General mode

Enter or a double click on an entity, relationship, enum, value object, custom type or domain opens its **editor** as a
tab in the centre, beside the screen. A single click opens it in the **preview** tab (in italics), which the next single
click replaces; editing it, a double click or Enter keeps it open. Each editor has top controls (name, domain, and for an
entity its key, **Base entity**, **Is abstract**, stereotypes, tags and category as chips) over tabs:

- **Entity**: Attributes, Relationships, Indexes, Mappings, Inheritance, Seed data, References, Code generation. Under
  the attribute grid, **Inherited** lists the base entities' fields and **Virtual** the fields the entity's stereotypes
  add, both read-only. Relationships has **New relationship…**, which starts the New relationship dialog from this
  entity. **Inheritance** (available once the entity has a base entity or another entity derives from it) shows the base
  entity, the derived entities and, per database, the strategy (tph, tpt or tpc) with where it comes from: the root
  entity's mapping, else the database's or the project's conventions.
- **Relationship**: its ends at the top, then Attributes, Mappings (per database: the customised mapping's shape, or
  "By convention", and the junction table), Code generation, References.
- **Enum**: Members; **value object**: Attributes; **custom type**: Definition; each with Code generation and References.
- **Domain**: General, Tags and Categories. The display name, plural name and description are edited in the editor's
  header only, not again on General.

**Follow selection** on the tab bar turns the shown editor into **General mode**: it follows the selection in the
explorer and on the canvas and keeps its tab, so you can walk twenty entities on the Mappings tab without reopening
anything. Unsaved edits are kept per element, so moving on never loses one. A reference type opens in the Reference data
screen instead.

F6 moves keyboard focus between these regions. Edits in the inspector are drafts with undo and redo; they are saved
as you go, and a save that collides with a change made elsewhere shows a conflict dialog with the two versions.

## The explorers and screens

An **explorer** is the sidebar tree a rail icon selects; a **screen** is what the centre shows. The command palette's
"Go to screen" group opens any screen.

**Creating elements.** Right-click a domain for New entity, New relationship, New enum, New value object, New custom
type, New sub-domain and New diagram; right-click a kind folder for New of that kind. The **+** button in each
explorer's header offers that explorer's kinds (New domain and the element kinds in Domain model, New reference type,
New database, New diagram), and so does an empty explorer. Every New dialog starts its domain picker on the current
domain: the row you right-clicked, else the selected element's domain, else the open diagram's home. The new element
opens in its editor (a diagram on the canvas, a database on the Database screen, a reference type on the Reference
data screen), and Undo removes it. An empty model shows a first-run panel with the same actions. Right-click a diagram
for **Duplicate**. In the New relationship dialog, **On delete of the <source>** follows the **Kind** until you pick one yourself: a
composition starts on cascade (the whole owns its parts), every other kind on restrict. A database made with New
database shows on the Database screen at once. The explorers remember which rows you expanded across a reload.

- **Domain model**: the domains, each with one folder per kind (Entities, Relationships, Enums, Value objects, Custom
  types, Seed data), and elements not in a domain listed first under "Not in a domain". Its screen is the canvas.
  Cards are entities with their attributes; edges are relationships with their cardinality. Click a card or an edge
  to select it and edit it in the inspector. The diagram picker lists the diagrams and, for each domain, "All of
  <domain>". "Add related" pulls in neighbours to a chosen depth, and Auto-layout untangles the diagram. Export
  writes SVG or PNG.
- **Reference data**: the reference types and their rows. The Reference data explorer is the screen's list: the types
  nested by category with a count on every group, and the explorer's search operators (`*` contains, `^` starts with,
  `~` like with `%`, `=` equals); its **…** menu has **Types A to Z (no categories)** for one flat list. Clicking a
  type there opens it here. Right-click a type (also Shift+F10) for **Duplicate**, **Rename** (the seed named after the
  type follows), **Move to category…**, **Set storage…** (opens the Storage tab), **Export CSV**, **Convert to enum…** (when the type has no
  fields of its own and its codes are identifiers: rows become members and the fields that used the type use the enum)
  and **Delete…**, which is refused while a field uses the type and otherwise deletes the type with its seeds; each is one
  change that undo reverses. The Reference data explorer's row menu offers the same actions except Export CSV (the
  Rows tab has it), plus **Add to favorites**. When the explorer is collapsed or shows another
  view, the screen shows the same list on its left (with a flat **A to Z** option). Ctrl+1 to Ctrl+4 pick a tab and `/`
  focuses the type search. For the selected type, four tabs; the tab you pick stays when you select another type:
  - **Fields**: the built-in `code`, `label` and `description` (names fixed; code's type, length and pattern editable,
    label's length editable; the description is text of any length), then your own fields in the attribute grid. A code
    is a string by default, or an integer (`int16`, `int32`, `int64`) or a `uuid`; a uuid code is written lowercase with
    hyphens (the grid lowercases what you type; error MQ7013 reports any other form). Label and description are marked
    as translated: each locale can translate them.
  - **Rows**: the columns are code, label, description, then your fields. A description may span lines: while you edit
    it, Shift+Enter adds a line. With one declared locale the status bar says how to translate labels and descriptions
    (declare a second locale under Settings › Locales); with two or more, each locale's label and description columns
    sit side by side.
    The grid is a spreadsheet over the type's rows. Arrows move; Enter or F2 edits, Enter commits and moves down, Tab
    commits and moves right, Esc cancels; Ctrl+Enter inserts a row below, Ctrl+D duplicates one (with an empty code),
    Delete clears cells, Ctrl+Delete deletes rows, Alt+Up and Alt+Down move rows; Shift+arrows select a range, Ctrl+C
    copies it as tab-separated text and Ctrl+V pastes such text, adding rows past the end; Ctrl+F finds; Ctrl+1 to
    Ctrl+4 switch tabs; `/` goes to the type search. **Import CSV** shows what a file adds, changes and removes before
    you apply it as one change you can undo; **Export CSV** downloads the rows (`@id`, `@code`, `@label`,
    `@description`, then the fields by name).
  - **Used by**: every attribute whose type is this reference type, with its Many and Required badges; click one to go to it.
  - **Storage**: per database, the storage strategy in effect and where it comes from, and an override chosen from the
    strategies the project declares in Settings (`referenceData.strategies`) or **Template-defined** (the packs decide).

  **New reference type** asks for the name, display name, category and **Stored as**: Template-defined or one of the
  strategies the project declares (the example packs' `lookup-table`, `check` and `native` read Lookup table, Check
  constraint and Native type, where the dialect has one). Check constraint is preselected when the project declares it;
  the choice applies to every database, and the Storage tab overrides it per database.

  To use a reference type as an attribute's type, open the attribute's **Type** cell: the list has sections (Recent,
  Built-in, Custom types, Enums, Reference data, Value objects) and one search across them. Each reference type shows
  how it is stored beside its name, for example `Country · check` (`template` when the packs decide; `+1` when one
  database is set otherwise). **Many** (Alt+M) makes the
  attribute a collection of codes and **Required** (Alt+R) makes it required; the cell then shows `→ Unit of measure`.
- **Domains, tags and categories**: opening a domain row (Enter, a double click, or the row menu's Open) opens the
  **domain editor**: General, then the domain's own **Tags** and **Categories**. Tags and categories exist globally
  (Settings) and per domain; a domain's apply to it and the domains nested under it, and its tabs list the ones it
  inherits ("from Billing", "global"). The domain's vocabulary is created with the first tag or category you add.
  The tag and category pickers and the explorer's filter chips offer the global entries plus those of the element's
  domain and its enclosing domains, nearest first; an entry from a domain names it ("ledger · Billing"). With a
  domain chip on, the filter offers that domain's chain; without one, every vocabulary. A tag or category used
  outside its domain (MQ2008), or a domain key that the global vocabulary or an enclosing domain already declares
  (MQ3021), shows in Problems with a **Go to** button that opens the element, or the domain's Tags or Categories tab.
- **Databases**: each database, its schemas and tables, and the tables not linked to an entity. Expanding a table
  loads its detail: Columns (type, PK and FK markers), Primary key, Foreign keys, Unique constraints and Indexes.
  Clicking a table or a column selects it in place and tints, in the Domain model, the entity mapped onto the table
  and the attribute mapped onto the column (once the entity is expanded; a collapsed folder shows "n related").
  Enter or a double click on a table opens its screen with that table focused. The screen shows table diagrams per
  database, a Tables list with a filter (the first 300 matches), a dialect selector, and a live DDL preview for the
  selected table. The database row's menu opens **Mappings**: an entity and its table side by side, where names mapped automatically are
  muted and customised ones are highlighted.

  **What a database holds.** Entities are not turned into tables on their own: a database holds only what is mapped to
  it, and how an entity becomes a table is yours to say. There is no default database either; every database, the first
  one included, holds what its own mapping says, and an entity may land in several databases or in none.

  - **A new database starts empty.** New database asks **Map domains by convention**: **None** (the default), **Pick
    domains** or **All domains**. With None, the Databases explorer shows "Nothing is mapped here yet" under it and
    generation makes no tables for it.
  - **Mapping domains.** **Map to database…** on a domain (several selected domains at once if you like) adds it to the
    database's convention list; its sub-domains come with it, and their entities get tables named by the naming
    conventions. A database whose convention is already All domains has nothing to add.
  - **Mapping entities.** **Map to database…** on one or more entities maps each one by itself, whatever the convention
    says, with a mapping element (the entity's Mappings tab and the database's Mappings screen show it). A mapping is also
    where a table that differs from the conventions is described (a **customised mapping**: another table name, other
    column names, an inheritance strategy, an ignored attribute), and a mapping marked ignore keeps the entity out of that database.
  - **The Mapping section.** Select the database in the Databases explorer: its inspector form has a **Mapping**
    section with the convention (None, Picked domains, All domains), the domains checklist, and the entities mapped one by
    one.
  - **Databases made before 0.3.0** have no convention written in their file and keep the old rule: every entity, or the
    entities of the domains their file lists. The Mapping section says "By convention: all domains (unspecified)" (or
    lists the domains), and **Make explicit** writes that choice into the file; generated output does not change.
  - An entity that lands in no database is reported in Problems as MQ4012 (info, only once the model has a database); a
    domain list that the convention does not use (All or None) is MQ4013 (warning).
- **Diagrams**: the saved diagrams outside the domains.
- **Seed data**: an entity's or a relationship's initial rows, in its editor's **Seed data** tab and in the domain's
  Seed data folder; a reference type's rows are its Rows tab. A seed lists its columns once and holds one row per line,
  each with its own id; a cell that names a row of another seed holds that row's id, and a reference-typed cell holds the
  row's **code**. A seed belongs to its element: deleting the element deletes its seeds and its translations in the same
  change, and removing an attribute drops its column. A delete is refused only while other elements point at the element
  or at its rows.

  **Editing seed data.** An entity's **Seed data** tab is the same Rows grid as a reference type's Rows tab. With no seed
  yet it offers **New seed**: nothing is created until you ask, and the seed lists every column the grid shows, so its
  CSV export doubles as a template. The columns are the entity's attributes (a base entity's first), then one per
  to-one relationship end (a relationship with seeds of its own keeps its links there). An end cell names a row of the
  other entity's seed data: Enter opens a picker over those rows, labelled by their first two filled cells. A
  relationship that has attributes has the same tab, with both ends first. The keys are the Rows tab's: Enter or F2
  edits, Tab moves right, Ctrl+Enter inserts a row, Ctrl+D duplicates, Ctrl+Delete deletes the selected rows,
  Alt+Up/Down moves them, Ctrl+C copies, pasting tab-separated cells adds rows past the end, Ctrl+Z undoes. Every seed
  grid's header has **Import CSV** (paste or pick a file, preview what it adds, changes and removes, then apply it as
  one change you can undo) and **Export CSV** (the seed as `<seed name>.csv`). In the Domain model explorer, an entity's
  Seed data child opens this tab, and the entity's menu has **Edit seed data** and **Import seed CSV…** (which creates
  the seed first when there is none).

  **All seed data at once.** The Reference data explorer's header menu (…) has **Export all seed data**, which
  downloads `seed-data.zip` with one CSV per seed of the model, named after the seed (`<name>.<id>.csv` when two seeds
  share a name); a domain's menu in the Domain model explorer has **Export this domain's seed data**, the same for the
  seeds whose entity, reference type or relation sits in that domain or its sub-domains (`<domain> seed data.zip`).
  Both menus have **Import seed data…**, which takes such a ZIP or several CSV files, matches each file to the seed its
  name names, previews every seed's changes, then imports them together as one change and one undo step. Files that
  match no seed are listed and skipped; no seed is written when one of the files has an error or a seed changed since
  the preview.
- **Generate**: Plan renders every template unit and shows what would change, grouped by unit with the reason each
  renders (see "How the plan explains itself" below); pick a file to see its diff; Apply writes the plan. Generation runs as a job and reports progress; the run history stays in the panel. The Generate
  explorer and the pack editor (below) show and change what each pack does.
- **Settings**: the project's settings, one tab each; the tab you left is the one Settings opens on next time.
  - **General**: the project's name and branding (below).
  - **Tags**, **Categories**, **Stereotypes**: the global vocabularies (a domain's own are on its editor's tabs).
  - **Conventions**: the naming conventions, for the project (every database) or for one database picked at the top;
    they name the tables and columns of the entities mapped to a database, and never decide which entities that is.
  - **Locales**: the content locales (see "Translating the model in the editor").
  - **Type maps, outputs, formatters**: the output allowlist (`outputs.allow`), the type maps, the formatters and the
    packs, shown read-only; edit `maquettiste.json` to change them.
  - **Explorer**: your preferences in this browser (Highlight related elements), your saved scopes and the team scopes
    from `maquettiste.json`.
- **Settings › General** names and brands the project; none of it changes generated output.
  - **Project name**: the name in the top bar, `name` in `maquettiste.json` (the field `maquettiste init --name` writes).
    The top bar follows as you type; Save writes it.
  - **Icon**: an SVG or a PNG of at most 512 KB, shown in the top bar, the browser tab and the sign-in page; without one
    the M mark is used. Upload stores it under a new name, `.maquettiste/branding/icon-<hash>.svg` (or `.png`), so the
    saved icon stays until you save; Save points `branding.icon` at it and removes the uploads it no longer names, and
    Discard keeps the saved icon. An SVG is stored without its scripts, event handlers and external references (the page lists what was
    removed). **Use the default mark** goes back to the M.
  - **Primary color**: one per theme, `branding.colors.light` and `.dark`, as `#rrggbb` (or `#rgb`). The accent, focus
    rings and selection follow it, and it is also a text color and the fill behind button text. A color with less than
    4.5:1 contrast against the theme's surfaces gets a warning (below 4.5:1 accent text is hard to read, below 3:1 the
    accent, focus rings and selection are hard to see), and so does one whose button text reaches less than 4.5:1; the
    warning states the ratios. **Reset** goes back to the built-in accent.
  - The rules: MQ8001 for a color that is not hex, MQ8002 for an icon that names no `.svg` or `.png` file under
    `branding/`, MQ8003 for an icon that is not a safe SVG or a PNG of at most 512 KB. A save that breaks one is refused
    with the message; a hand edit shows it in Problems.

## Generation: how the model becomes files

This chapter is for someone who has never written a template. Generation reads the model (the entities, tables,
enums and the rest) and writes text files from it: SQL scripts, classes, documentation, anything a template describes.
Nothing about generation is hidden: every rule is a file under `.maquettiste/templates/`, and the editor, the command
line and git read the same files.

**A template** is a text file with holes in it. Text outside `{{ }}` is copied as it is; inside, `{{ entity.name }}`
prints a value from the model, `{{ for a in entity.attributes }} … {{ end }}` repeats a part, `{{ if … }} … {{ end }}`
keeps a part only when a condition holds. A template that prints `CREATE TABLE {{ table.name }} (` gives
`CREATE TABLE customers (` for the customers table.

**A pack** is a folder, `.maquettiste/templates/<pack>/`, holding the templates and one `pack.json` that says what to
run. The example packs are `sql-ddl` (database scripts) and `csharp-dapper` (classes and repositories); your own packs
sit beside them and work the same way. A pack also has an **output base** (`packs.<pack>.output` in `maquettiste.json`),
the folder its paths start from, and can be switched off there (`enabled: false`).

**A unit** is one line of a pack's work list. It names a template, which elements the template runs for (the scope),
and where the result goes (the output pattern). `sql-ddl` has four units: `table`, `schema`, `migration`, `seed`.

**The scope** (`for` in `pack.json`) decides how many times a unit runs:

| Scope | Runs | Result |
| --- | --- | --- |
| `each table`, `each entity`, `each enum`, … | once per element of that kind | one file per element: 40 tables give 40 scripts |
| `model` | once, with the whole model | one file for many: a template that loops over every entity writes them all into one file |
| `each locale` | once per declared language | one file per language (a resource file, a dictionary) |
| `select <name>` | once per element a pack script returns | `select databases` gives one file per database |

A **filter** (`where`) narrows a scope: only entities tagged `api`, only one package, not the abstract ones. An element
can also opt out of a pack with `generation.skip`.

**One file per element, or one file for many?** Pick the scope. For one file per entity, use `each entity` and an
output pattern that contains the entity's name. For one file that lists every entity (a registry, an index, one big
migration), use `model` and loop inside the template: `{{ for e in model.entities }} … {{ end }}`. Two elements that
would get the same path are refused (MQ6020), so a per-element pattern must contain something that differs per element.

**The output pattern** (`output`) is itself a small template that gives the file's path, relative to the output base:
`{{ kebab table.database.name }}/tables/{{ table.name }}.sql` writes `main/tables/customers.sql`. In the editor the
Generate explorer reads each pattern aloud (`<database>/tables/<table>.sql`), and the Units tab shows the path it gives
for an example element and how many files the unit plans.

**Parameters** are the pack's knobs: `pack.json` declares them with defaults (`comments: true`, a namespace, a folder
name), templates read them as `pack.params.<name>`, and a project sets its own values under `packs.<pack>.parameters`
in `maquettiste.json`. The pack editor's Parameters tab shows each one with the right control and a Reset to default.

**The write mode** says what happens to a file that already exists: Overwrite (the default), Create only if missing
(`once`), Protected regions (your code between markers survives), or Pair (a generated file plus a companion file for
hand-written code that is created once and then left alone).

### Change a template and see the result

1. Choose **Generate** in the rail and open a pack in its explorer; its tab opens beside **Plan**.
2. **Templates** (Alt+3): pick a file on the left; the middle pane is the template. The line above it says which units
   use it.
3. On the right, the **preview** renders a unit for one element with your unsaved text, a moment after you stop
   typing: pick the **Unit** and one of the unit's elements (the picker lists only its kind: entities for an `each
   entity` unit, reference types for `each reference type`, locales for `each locale`), read the output path and the
   text it would write, and any error is marked at its line. A preview writes nothing.
4. **Save** (Ctrl+S) writes the file under `.maquettiste/templates/<pack>/`, where git sees it.
5. Back on **Plan**, **Plan** shows every file the change touches; **Apply plan** writes them.

To add a unit, use the **Units** tab (Ctrl+Enter), give it a template, a scope and an output pattern, and save.

### How the plan explains itself

A plan is a dry run: it renders what needs rendering and compares it with the disk, and nothing is written until you
apply it. Above the table, one line per pack says what it will do, for example
`sql-ddl: 4 units, 12 files to add, 3 to modify, 1 orphan to delete` (an orphan is a file generation wrote earlier that
no unit produces any more). The table groups the files by unit (`sql-ddl/table`, with its template and its counts);
click a group to fold it. Each file shows its change, its path, its unit, its element and **Why** its unit renders:
"New: no recorded state from an earlier run" the first time, "Customer (entity) changed" or "Template table.scriban
changed" after an edit, "… was edited on disk" when a generated file was changed by hand. Filter by change, pack, unit
or any words.

Selecting a file opens its diff below and, on the right, **Why this file**: pack, unit, template, element, output path,
the reason and every cause; **What it read** asks the engine for the inputs the unit recorded. Generation is
incremental, so most units are skipped when little changed: **Unchanged units** lists them, and **Why not?** on one
answers "Skipped: its 23 recorded inputs are unchanged since its last render, and its outputs are intact."
**Explain** answers for any pack, unit and element, planned or not: the pack is disabled, not in this run, the scope
does not cover that kind, a filter excludes it, `generation.skip` is set, or it renders and why.

### Make your own pack from a starter

In the editor, **+** in the Generate explorer header (or **New pack…** in the palette): a name, then Empty (one unit
and its template) or a copy of a pack of this project. From a terminal, `maquettiste pack new <name> --from sql-ddl`
(or `csharp-dapper`, or `empty`). Then give it an output base under an allowed root (below) in the pack editor's header
or in `maquettiste.json`, edit its units and templates, and plan. The copy is yours: change it freely; the example
packs are not updated under you.

### outputs.allow: what generation may touch

Generation writes only under the roots listed in `outputs.allow` of `maquettiste.json`, never elsewhere, whatever a
template or an output pattern says:

```json
"outputs": {
  "allow": [
    { "path": "db", "commit": true },
    { "path": "services/billing/src/Generated", "commit": false }
  ],
  "deny": ["**/*.user.cs"]
}
```

In a large repository with many projects, list each generated folder as its own root; everything else (hand-written
code, other teams' folders, `.git`, `.maquettiste`) is out of reach. A path outside every root is refused before
anything is written (MQ6019, shown in the Units grid next to the pattern), and `deny` globs carve exceptions out of a
root. `commit: true` marks output that belongs in git (its manifest is committed and `generate --check` guards it in
CI); `commit: false` marks build output that `init` adds to `.gitignore`.

## Packs in the editor: what generation does

A **pack** is a folder of templates and one `pack.json` under `.maquettiste/templates/<pack>/`; generation runs every
enabled pack. The editor reads and writes those files in place, so the command line, the editor and git always see the
same pack. The words the screens use:

| Word | Meaning |
| --- | --- |
| Unit | One line of the pack's work list: which elements, which template, which output path |
| Scope | Which elements a unit runs for: once (`model`), once per element of a kind (`each table`), or once per element a selector returns (`select databases`) |
| Files | The scope read as files: "Each table" writes one file per table, "Once" one file for the whole model, "Once per package" and "Once per database" one file per group |
| Filter | Narrows the scope by tags, stereotypes, categories, packages (and their "not" lists), database, abstract, or a script filter |
| Output path | A pattern rendered with the template's variables; the file lands under the pack's output base, inside an allowed output root |
| Write | Overwrite, Create only if missing, Protected regions, or Pair (a generated file and a companion for hand code) |
| Parameters | Pack settings the templates read as `pack.params`; defaults in `pack.json`, values per project in `maquettiste.json` |

**The Generate explorer.** Each pack is a tree node with its unit count and the number of output roots its last run
wrote ("off" when disabled, a warning count when it has diagnostics). Expand it for:

- **Units**, one line each: `table · each table → table.scriban → <database>/[<schema>/]tables/<table>.sql`. The path
  reads the output pattern aloud: `<table>` is a name that changes per element, `[...]` a part that appears only when
  its condition holds. A pattern too complex to read aloud shows as written. A funnel means the unit has a filter
  (hover it for the filter in words); the write mode shows when it is not Overwrite.
- **Templates**: the pack's files with their role (template, partial, script, type map) and the units that use them.
- **Parameters**: `name = value`, with "default" or "set".
- **Outputs**: the files the pack last wrote, from the manifest, grouped by unit, with the ones that are hand-edited,
  missing or orphaned (their unit or element is gone) counted.

The **+** in the explorer header (or **New pack…** in the palette) creates a pack: a name, and Start from Empty (one
unit and its template) or a copy of a pack of this project. The dialog says what it writes.

**The pack editor.** Click a pack (or Enter on any row under it) to open it as a tab beside **Plan** in the centre. The
header shows the version, engine range and description, the **Enabled** switch and the **Output base** (both saved to
`packs.<pack>` in `maquettiste.json`), and the project's hand-edit policy (changed in Settings). Four tabs, Alt+1 to
Alt+4:

- **Units**: a grid of the units, edited in place: id, scope, filter, template, output path, write mode, formatter.
  Beside each output pattern the grid shows how it reads, the path it gives for an **Example element** (chosen in the
  toolbar, one of the unit's own elements, the first until you pick another, kept per unit), and how many files the unit plans. When two elements would get the same path the cell says MQ6020; a
  path outside every allowed root says MQ6019. The side panel explains the focused field and the row's scope in plain
  words. Ctrl+Enter adds a unit, Ctrl+D duplicates it, Ctrl+Delete removes it, Alt+Up and Alt+Down reorder, Ctrl+S
  saves `pack.json` (every member the grid does not show is kept). When the file changed on disk since you opened
  it, the grid offers **Keep mine** or **Take theirs**.
- **Parameters**: one row per parameter with the right control (a switch, a list, a number, text, or JSON), its
  default, and **Reset to default**. Save writes the project's values; a value that breaks the pack's parameter schema
  is refused in the form, and a value for a parameter the pack does not declare (MQ6024) can be removed.
- **Templates**: three panes. On the left, the pack folder's files (templates, partials, scripts such as
  `helpers.js`, and any other text file; `pack.json` is edited on Units); a dot marks a file with unsaved changes, and
  the tab's own dot says some file is unsaved. In the middle, the file in a code editor with Scriban colouring (the
  `{{ }}`, `{{- -}}` and `{{~ ~}}` blocks, keywords, strings, comments, pipes and the functions after them; text outside
  the blocks stays plain); the line above it says which units use the file, directly or through includes. On the
  right, the **preview**: pick a **Unit** (the ones that use the file come first) and one of its elements. The picker
  lists only elements of the unit's scope kind (each entity: entities; each reference type: reference types; each
  locale: locales; each table, and a `select` scope: the elements the unit plans; `model`: none, it renders once),
  starts on the first and remembers your choice per unit. A partial previews through the first unit that includes it,
  and the line above the preview says so; a file no unit uses previews through the first unit, also said there. When
  the model has no element of the unit's kind the preview says so ("The model has no reference type to preview this
  template with."), and a render outside the unit's scope reads "This template renders one reference type; pick a
  reference type to preview it" instead of the template engine's error (the server checks the same: a preview for an
  element outside the unit's scope returns MQ6026, which names the kind the template expects, and no files). The preview renders that unit for that
  element with the text you have not saved yet, about 300 ms after you stop typing. The output path it would write shows above the rendered text, and its
  diagnostics are listed above it and marked in the editor at their line. Nothing is written by a preview.
  **Save** (or Ctrl+S) writes the file under `.maquettiste/templates/<pack>/` with the version you opened; a template
  that does not parse is still saved and its MQ6003 error marked. When the file changed on disk since you opened it
  (someone else, git, or your text editor), the save is refused and a bar offers **Keep mine** (write your text over
  it) or **Take theirs** (reload the disk text). Clicking a file under Templates in the explorer opens it here.
- **Outputs**: every file the pack wrote, by unit or by root, filtered by state, root and path; **Diff** opens the
  file's diff when the current plan includes it.

## Translating the model in the editor

The display names, plural names and descriptions of the domain model, and the labels of reference data rows, can be
translated (reference-types-seeds-localization.md section 3). While the project declares one locale, nothing of this
shows anywhere: no switcher, no Translations section, no locale columns.

- **Settings › Locales** declares the locales: the default locale (the language the element files are written in), the
  supported locales, each locale's fallbacks (for example `fr-CA` falls back to `fr`; left empty, a locale falls back to
  its shorter tag, then to the default) and which kinds count for completeness (none checked: every translatable field).
  Save writes the `localization` block of maquettiste.json. "Declare locales" starts the block when there is none.
  The new-locale field corrects a tag as you type it: an underscore becomes a hyphen, the language turns lowercase, a
  script Titlecase and the region uppercase (`zh_cn` becomes `zh-CN`, `zh_hant_tw` becomes `zh-Hant-TW`). **Add locale**
  stays in place; while the tag cannot be added, the reason shows beside it ("use language-REGION with a hyphen, such as
  zh-CN, or a language alone, such as fr", or "already a supported locale"), and Save stays off while the block has a
  problem MQ7201 would report. The CLI's `l10n` verbs and hand edits of `maquettiste.json` are not corrected this way:
  write `zh-CN` there.
- With two or more locales, the same tab shows the **completeness matrix**: one row per domain (plus Not in a domain and
  Reference data), one column per locale, the percent translated in each cell (stale translations count as not done).
  A cell opens the **translation queue** for that locale and domain: the default text on the left, the translation on
  the right. Enter saves and jumps to the next field that needs work, Ctrl+Enter confirms a stale translation as it is,
  the arrow keys move between rows, Esc drops an unsaved change, and "Open element" goes to the element.
- The top bar's **Content** switcher picks the language the explorer, the canvases, the Reference data screen and search
  show, with each locale's completeness. While a locale other than the default is shown, the explorer header shows its
  tag as a chip; removing the chip goes back to the default. The choice is kept per browser.
- The inspector and the element editors have a collapsed **Translations** section (it stays open or closed as you left
  it): one row per locale with the display name, plural and description. An empty field shows, in italics, the text the
  fallback chain gives. A **stale** marker means the default text changed since the translation was made; **Confirm**
  keeps the translation and clears the marker. A field saves when you leave it or press Enter.
- The Reference data screen's **Rows** grid gains a `label (<locale>)` column for the content locale (none while the
  content locale is the default), and **All locales** shows one per locale; the status bar shows how complete the
  content locale's labels are (`fr 75 %`). An empty cell shows the fallback label in italics; typing in it writes the
  translation, not the seed.

## Two ways to run it

**Mock mode**, for looking at the editor without any backend:

```
cd src/editor && npm ci && npm run dev
```

Open http://localhost:5173. Every API call is answered in the browser from an in-memory copy of the billing model,
so edits last until you reload. Append `?mock=conflict`, `?mock=slow`, `?mock=empty` or `?mock=invalid` to the URL
to see those situations.

**The real editor**, over your repository, runs in a container built on static-site-hosting:

```
docker build -f docker/Dockerfile -t mattjcowan/maquettiste:dev .
sh docker/dev-billing.sh          # starts the sample project from tmp/billing on 127.0.0.1:8080
# If port 8080 is taken on your machine: MAQUETTISTE_PORT=8090 sh docker/dev-billing.sh, then use :8090 below.
```

Open http://maquettiste.localhost:8080. There is no login in local mode: the container trusts requests from the
machine it runs on. Saves write the JSON files in the mounted `.maquettiste/` folder, and Apply writes generated
files into the mounted repository. Stop it with:

```
docker compose -f docker/compose.yaml --project-directory tmp/billing down -v
```

To run it over your own repository, use `docker/compose.yaml` with your repository as the project directory, as
SPEC.md Section 4 describes; it works unchanged under Docker and under Podman:

```
MAQUETTISTE_IMAGE=mattjcowan/maquettiste:<tag> docker compose -f <maquettiste>/docker/compose.yaml --project-directory . up -d
```

The container starts as root and looks at who owns the mounted `.maquettiste/` folder. When it is you (Docker on Linux or
on the Mac), the editor hands its own volume to you and runs as you, so the model and the generated files stay yours. When it
is root (rootless Podman, whose root inside the container is you outside it), the editor stays root, which writes your
folders as you. Two variables override the choice when you need to:

- `MAQUETTISTE_UID`: the user id to run as (`id -u`); `0` keeps root.
- `MAQUETTISTE_GID`: the group id to run as (`id -g`); defaults to the folder's group.

## The command line

`maquettiste` is the same engine without the editor: it creates the project, checks it and generates the code, which is
what a CI job and a terminal need. `maquettiste --help` lists every option; exit codes are 0 success, 1 validation or read errors,
2 drift (or a `--check` preview that would change something), 3 hand-edit conflicts (or, for the `l10n` and `seed` verbs, a file
that changed while the command ran), 4 an internal or usage error (a refused write, or an argument that names no locale or seed). A locale argument is read as
the editor reads one: `zh_cn` is `zh-CN` and `fr_ca` is `fr-CA`; a tag that cannot be read exits 4 and says how to write it.

| Command | What it does |
| --- | --- |
| `maquettiste init` | Creates `.maquettiste/` (`maquettiste.json`, the JSON schemas for editor completion, the `sql-ddl` starter pack) and a `.gitignore` block. `--pack csharp-dapper` or `--pack none` picks another starter; `--mcp`, `--skill` and `--agent-setup` register the agent server, and `--mcp --docker <image>` registers a `./mcp.sh` wrapper that runs it from the image (docs/mcp.md). The project is named by `--name <name>`, else the `name` of `package.json`, else the git remote's repository name, else the folder name (so a repository mounted at `/repo` in the image keeps its real name). Running it again keeps what is there. |
| `maquettiste validate` | Validates the model and the packs; `--format sarif` for code-scanning tools. |
| `maquettiste generate` | Renders the packs into the output roots of `maquettiste.json`, incrementally: only units whose inputs changed re-render. Prints one line per file (`A` added, `M` modified, `D` deleted, `K` kept). A generated file edited by hand stops the run (exit 3); `--hand-edits overwrite` replaces it. |
| `maquettiste generate --check` | Renders without writing and exits 2 when the committed output differs from the model: the CI gate. |
| `maquettiste generate --watch` | Regenerates on every change under `.maquettiste/` (a save in the editor, a template edit) until Ctrl+C. |
| `maquettiste format` | Rewrites every model file (`maquettiste.json`, `model/**`) in canonical form, the form the editor writes, and prints the count; hand-written files then stop reporting MQ1003. A file that does not pass its schema is left as it is and named. `--check` writes nothing and exits 2 (drift, as `generate --check` does) when a file would change. |
| `maquettiste l10n status` | The default locale, the declared locales and, per translated locale and shard, how many texts are translated, missing and stale (`--format json` for scripts). |
| `maquettiste l10n export fr` | The French texts as XLIFF 2.1 for translators (`--format csv` for a spreadsheet), on stdout or into `--out <file>`. |
| `maquettiste l10n import fr <file>` | Previews what an XLIFF or CSV file adds, changes and confirms (a stale text the translator marked `translated`, `reviewed` or `final` without changing it), and names the units that match nothing; `--apply` plans again and writes it as one save, refused (exit 3) when a shard changes while the command runs; it prints what it wrote, which can differ from an earlier preview if the files changed in between. `--check` exits 2 when the file would change something. |
| `maquettiste l10n prune` | Lists the orphan translations (MQ7203: an entry whose element is gone, or a field its element does not have); `--apply` removes them in one save, as the other `l10n` and `seed` verbs write only with `--apply`. `--check` exits 2 when there are orphans. |
| `maquettiste l10n set-default fr` | Previews making French the default language: each French text moves into the element files and the text it replaces becomes an English translation; `--apply` writes it all in one change to review in git. Other locales' translations turn stale where their source text changed. Sidecar descriptions and orphans are not moved: they stay in the new default's locale folder, which is no longer loaded (MQ7202), and a sidecar-described element keeps its old-language description; the command lists them as skipped. Move or delete them by hand, or run `l10n prune` before switching. |
| `maquettiste seed export <seed>` | A seed's rows as CSV (the seed's id or name, or the id or name of the element it seeds); `--locale fr` adds the French label and description columns, `--out <file>` writes a file. |
| `maquettiste seed import <seed> <file>` | Previews a CSV import (rows match by `@id`, else by `@code`): added, changed, removed and blocked rows; `--mode replace` also removes the rows the file leaves out, except rows still referenced; `--apply` plans again and writes it, refused (exit 3) when the seed file changes while the command runs; it prints what it wrote, which can differ from an earlier preview if the seed changed in between. |
| `maquettiste pack new <name>` | Scaffolds a pack under `.maquettiste/templates/<name>/` (`--from empty`, `sql-ddl` or `csharp-dapper`). Give it an `output` under an allowed root in `maquettiste.json` before the next `generate` (packs/README.md). |

Progress (`--progress plain`, the default when stderr is not a terminal) prints each stage once, in order, with a start
and a done line; `generate --check` prints no write stage, since it writes nothing. When the operating system refuses a
write (the run lock under `.maquettiste/.cache`, the cache folder or an output file), the CLI prints one line naming the
path and exits 1; in a container on a Linux host this usually means the container runs as another user than the one that
owns the mounted folder, so run it with `--user $(id -u):$(id -g)`.

With the .NET SDK installed, the CLI is a .NET tool (`dotnet tool install -g Maquettiste.Cli --prerelease`).

### From the Docker image

The editor image carries the CLI, so a machine with only Docker needs nothing else. Mount the repository at `/repo` and
make it the working directory:

```sh
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste init
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate --check
docker run --rm -it --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate --watch
```

A shell function saves typing (`-it` only when you are at a terminal, so it also works in scripts and CI):

```sh
maquettiste() { docker run --rm $([ -t 0 ] && echo -it) --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste "$@"; }
```

**File ownership.** The image runs as UID 1654. Add `--user "$(id -u):$(id -g)"` (as the function does): the CLI then
writes the model and the generated files as you, and they stay yours. Without it the run fails on a repository UID 1654
cannot write, with "Access to the path ... is denied" (Linux) or "Permission denied" (the Mac, whose
Docker file sharing does not map the container's user). The CLI works under any
UID: when the image's own folders are not writable it keeps its cache in the container's `/tmp` for that run. Under rootless
Podman, leave `--user` out (root in the container is you outside it) or replace it with `--userns=keep-id`.

**Order with the editor.** Run `init` before `docker compose ... up`. The compose file bind-mounts `./.maquettiste`; when
the folder does not exist yet, Docker creates it empty (owned by root on Linux) and the editor starts on a project with no
`maquettiste.json`.

**Agents from the image.** `maquettiste init --mcp --docker mattjcowan/maquettiste:<tag>` (through the function above)
writes `mcp.sh`, a wrapper that runs `maquettiste mcp` in the image as you over the repository, and registers it in
`.mcp.json` as `{"type": "stdio", "command": "./mcp.sh", "args": []}`. The server's messages go to
`.maquettiste/.cache/mcp.log`. docs/mcp.md has the details.

## Translations, seed CSV and reference data over the API

The `l10n` and `seed` commands above do the same from a terminal. The editor's API (and the matching MCP tools, see docs/mcp.md) serves the translations of the standard fields and the seed
rows as files people outside the model can work with (reference-types-seeds-localization.md sections 2.3 and 3.9):

- `GET /api/localization`: the default locale, the declared locales and, for each translated locale, its fallback chain and
  how complete each shard is (expected, translated, missing and stale fields).
- `GET /api/localization/fr/entries?owner=<id>` (or `?shard=<path>`): each field's default text, its French text, the text
  the fallback chain gives, and its state. `?missing=true` pages the fields that still need work, 200 at a time.
- `PUT /api/localization/fr/entries`: writes, removes or confirms translations in one save. Send the shard hashes you read as
  `expected`; if someone changed the shard since, the answer is 409 and nothing is written.
- `GET /api/localization/fr/export?format=xliff` (or `csv`) gives translators an XLIFF 2.1 or CSV file; `POST
  /api/localization/fr/import` with `{ format, content }` previews what the file adds and changes, and `?dryRun=false` applies it.
- `GET /api/seeds/<id>/csv` exports a seed's rows (`?bom=true` for spreadsheet programs, `?locale=fr` adds the French label
  and description columns); `POST /api/seeds/<id>/csv` with `{ content }` previews an import (rows match by `@id`, else by
  `@code`), and `?dryRun=false` applies it (`?mode=replace` also removes the rows the file leaves out).
- `GET /api/reference-types/<id>/usage` lists the attributes that use a reference type and how each database stores it.
- `GET /api/model/index?locale=fr` fills the display names from French, falling back as the settings say.

Enums are stored as `int` or `string` columns. Storing an enum through a lookup table (`enumStorage` or a mapping's `storage`
set to `lookup`) is no longer offered: a file that still uses it does not load and reports error MQ7012. Convert the enum to a
reference type (its members become the rows of a seed) and pick the reference type's storage strategy, for example a lookup
table, under `referenceData.strategies`.

Tags and categories can be declared globally (`model/vocabularies/tags.json`, `categories.json`) and per domain: a tag vocabulary
or category tree with a `package` belongs to that domain and the domains nested in it (`model/vocabularies/<name>-tags.json`,
`<name>-categories.json`), at most one of each per scope (MQ1009). An element may use the tags and categories of its own domain,
of every enclosing domain and the global ones; one declared only in another domain is error MQ2008, and a domain vocabulary may
not redeclare a key or category name of the global vocabulary or of an enclosing domain (MQ3021).

## Where the details are

- SPEC.md: the specification, Sections 5 to 12 for the model and generation, 14 for the editor.
- docs/engineering/phase2-design.md: how the editor and its API are built.
- docs/api/openapi.yaml: the editor's API.
- packs/README.md: how to write a template pack.
- docs/engineering/explorer-redesign.md: the rail, the explorers, search and the element editors.
- docs/engineering/reference-types-seeds-localization.md: reference types, seeds and localization.
- docs/mcp.md: the same operations for agents.
