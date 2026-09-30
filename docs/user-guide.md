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
- **Processes**: statecharts. A **lifecycle** describes the states of one entity; an **orchestration** coordinates work
  across people, systems and other processes. **Actors** are the people, roles and systems that raise their events and
  sign their gates; **scenarios** are recorded runs of a process that the engine replays as tests (see "Processes, actors
  and scenarios").
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
| Rail | The explorers: Domain model, Processes, Reference data, Databases, Diagrams and Generate; Settings and the account menu at the bottom |
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
Both are kept in this browser only: another browser, or a private window, opens with the default layout and nothing open.

### The rail: one explorer at a time

The rail's icons, top to bottom, are **Domain model**, **Processes**, **Reference data**, **Databases**, **Diagrams** and
**Generate**;
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

An element's inspector has four tabs, switched with the mouse or the arrow keys: **Properties** (its own fields, such
as name, domain, stereotypes, tags and custom properties, plus the kind's settings), **Attributes** (only for kinds that
have attributes: a value object's or stereotype's attribute grid, edited there; an entity's attributes as a read-only
list of name, type and a `*` for required, with **Open editor** opening the entity editor on its Attributes tab, where
the entity's grid is edited), **JSON** (the element's document, editable) and **Where used** (the elements that
reference it; a row goes to the referring element). The inspector remembers the tab you chose for each kind of
element; a kind without the chosen tab shows Properties.

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
the new place. **Apply stereotype…**, **Tag…** and **Set category…** mark a row, or every selected row at once, in one
change. **Promote to entity** on a value object or a custom type makes it an entity (an id key plus the value object's
attributes, or one `value` attribute of the custom type's base) and turns each entity attribute typed as it into a
relationship to the new entity. What pointed at a removed attribute follows: the owner's mapping drops its row for it
(the relationship's foreign key maps by convention) and the owner's seed drops its column; a diagram that showed the
promoted element shows the new entity. A use inside a value object, a relationship or a key, a seed left with no column,
or any other element that refers to it blocks the promotion, and the dialog names each one and lists every rewrite.
One undo reverses the whole promotion. F12 on a reference goes to its definition and Shift+F12 lists where the element is used.

### Element editors and General mode

Enter or a double click on an entity, relationship, enum, value object, custom type or domain opens its **editor** as a
tab in the centre, beside the screen. A single click opens it in the **preview** tab (in italics), which the next single
click replaces; editing it, a double click or Enter keeps it open. Each editor has top controls (name, domain, and for an
entity its key, **Base entity**, **Is abstract**, stereotypes, tags and category as chips) over tabs:

- **Entity**: Attributes, Relationships, Indexes, Mappings, Inheritance, Seed data, References, Code generation. Under
  the attribute grid, **Inherited** lists the base entities' fields and **Virtual** the fields the entity's stereotypes
  add, both read-only. Relationships has **New relationship…**, which starts the New relationship dialog from this
  entity. **Inheritance** (available once the entity has a base entity or another entity derives from it) edits the hierarchy:
  the **Base entity** picker (it refuses a base that would close a loop and says why), the derived entities, and under
  **Mapping strategy**, per database, the strategy (**One table for the hierarchy (tph)**, **One table per entity,
  joined (tpt)**, **One table per concrete entity (tpc)**, or **By convention**, which names the strategy the
  conventions give) and the **Discriminator value**. The strategy is written on the root entity's mapping in that
  database (disabled, with a tooltip, while the root has none); the discriminator value on this entity's mapping.
- **Relationship**: its ends at the top, then Attributes, Mappings, Code generation, References. **Mappings** edits,
  per database, the **Shape** (**Foreign key**, **Junction table**, **Promoted to an entity**, or **By convention**),
  the **Junction table** name and the **Promoted entity name**; the first edit creates the relationship's mapping in
  that database when it has none. Every edit here is one undo step.
- **Enum**: Members; **value object**: Attributes; **custom type**: Definition; each with Code generation and References.
- **Domain**: General, Tags and Categories. The display name, plural name and description are edited in the editor's
  header only, not again on General.

The chevron at the right of the editor's title row folds the details away (the display names, the description and the
top controls; the inspector shows the same fields) so a chart or a grid gets the room; the choice is remembered in this
browser per kind of element. A process starts folded, so its chart has the room.

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

  **Where the arrangement lives.** A diagram's file keeps each card's position and the canvas's pan and zoom: moving a
  card, panning or zooming saves them there (the zoom a moment after you stop), and opening the diagram again shows it
  exactly as you left it. The view is fitted to the cards only when the diagram has no saved pan and zoom. A pan or
  zoom is not an undo step of its own. "All of <domain>" becomes a diagram the first time you arrange it (drag a card,
  run Auto-layout, pan or zoom): a diagram in the domain, first named after it, with its entities where you see them
  and its membership set to follow the domain. From then on the picker lists it under Diagrams and "All of <domain>"
  opens it; it keeps following the domain, so an entity that joins the domain is added to it and one that leaves is
  removed, and entities cannot be dropped onto it or added with "Add related". The inspector shows a diagram's
  Membership as "Follows the domain" or "Explicit members" (an ordinary diagram, whose members are exactly what you put
  on it). The membership decides it, not the name: renaming the diagram or the domain changes nothing, and when a
  domain has several diagrams that follow it, "All of <domain>" opens the first by id. In the file this is
  `"membership": "package"`; a diagram that follows a package but names none is reported (MQ3022). Only looking at
  "All of <domain>" creates nothing, and Undo removes the diagram it created. Positions a browser kept for
  "All of <domain>" before this move into that diagram the next time it opens.

  **Auto-layout and new cards.** Auto-layout re-arranges every card of the diagram and fits the view; it is the only
  thing that moves cards you placed. It makes room for the relationship labels: layers are spaced for the label pills
  plus a gap, and a label whose edge is too short to hold it floats beside the line rather than over a card. A diagram whose cards have no position yet is laid out the same way when it first
  opens. A card that arrives later (a new entity, an entity added to the diagram without a position, one that joined
  the domain) is placed on its own, in free space beside a card it has a relationship with, or else in a row under the
  drawing; nothing else moves and the zoom stays, and a new card you just created is scrolled into view.
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
  the choice applies to every database, and the Storage tab overrides it per database. `maquettiste init` declares the
  three strategies the sql-ddl starter builds; a project that declares none sees **Declare the standard storage
  strategies** under Settings › Conventions, one click away. The Storage tab's **Preview output** renders the sql-ddl
  seed script of the chosen database without writing it and shows the statements for the type. Hovering a type in the
  list shows its first codes. When a type has several seeds, a picker beside **Import CSV** and **Export CSV** chooses
  the seed. Removing a field also removes its column from the type's seeds, in the same save (one undo step).

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
  selected table. The Tables list and the DDL preview each hide from the button in their header ("Hide tables list",
  "Hide DDL preview") or with Alt+Shift+L and Alt+Shift+D on this screen, and come back from the slim strip they leave
  at the edge or the same shortcut; the palette has "Toggle tables list" and "Toggle DDL preview". Like the other
  panels, what you hid stays hidden after a reload, and "Reset layout" shows both again. A database of more than 300 tables is not drawn whole: with no table selected the screen says
  "<n> tables are too many to draw at once" and keeps the list and the DDL preview of the whole database; pick a table
  and the diagram draws it with the tables its foreign keys connect it to, in both directions (at most 300, the header
  saying how many more were left out). A database diagram's table positions and its pan and zoom are kept in your
  browser (there is no diagram file for a database yet); a table added later is placed beside a table its foreign
  keys connect it to, or under the drawing, without moving the others. The database row's menu opens **Mappings**: an entity and its table side by side, where names mapped automatically are
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

  **Schemas.** A PostgreSQL or SQL Server database can hold several schemas (namespaces such as `sales` or `ops`).

  - **New schema…** on a database (its context menu, or the New menu while a database is selected) adds one. The
    database inspector's **Schemas** section lists them with the **Default schema** marked, and renames, removes or
    makes one the default. A rename changes nothing else: tables, views, sequences and mappings point at the schema,
    not at its name.
  - **Where a table goes.** A table file (a designed table, or a customised mapping's override) says its own schema. A
    mapped entity's table otherwise goes to the schema its **Mappings** tab picks, else to the schema picked for its
    domain (or the nearest parent domain) in the database's convention list, else to the default schema. When a
    database has more than one schema, New database, the convention list and the Mappings tab offer the schema, and
    the Databases explorer groups the tables under one row per schema.
  - **Removing a schema** shows what still lives in it and asks for the schema to move it to; the default schema can
    be removed only by making another one the default at the same time. A schema named by a convention entry or a
    mapping but not declared in the database is MQ4014; a refused schema change is MQ4015.
  - Moving a table to another schema is, for sql-ddl's migrations, a drop and a create of the table (not a move that
    keeps its rows): move data yourself when that matters.
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
  name names, previews every seed's changes, then imports the rows of every seed together as one change and one undo
  step. Files that match no seed are listed and skipped; no seed is written when one of the files has an error or a seed
  changed since the preview. Translation columns (`@label:<locale>`, `@description:<locale>`) are saved after the rows,
  one save per seed and locale: if one of those saves fails the rows stay imported, and Undo takes back the rows, not
  the translations (fix or remove them in the Translations section or with `maquettiste l10n`).
- **Generate**: Plan renders every template unit and shows what would change, grouped by unit with the reason each
  renders (see "How the plan explains itself" below); pick a file to see its diff; Apply writes the plan. Generation runs as a job and reports progress; the run history stays in the panel. The Generate
  explorer and the pack editor (below) show and change what each pack does.
- **Settings**: the project's settings, one tab each; the tab you left is the one Settings opens on next time.
  - **General**: the project's name and branding (below).
  - **Tags**, **Categories**, **Stereotypes**: the global vocabularies (a domain's own are on its editor's tabs).
  - **Conventions**: the naming conventions, for the project (every database) or for one database picked at the top;
    they name the tables and columns of the entities mapped to a database, and never decide which entities that is.
  - **Locales**: the content locales (see "Translating the model in the editor").
  - **Validation**: the severity of each built-in rule (see "Settings › Validation" below).
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
- **Settings › Validation** <a id="settings-validation"></a> lists every built-in rule, grouped by family (Model files,
  References and vocabularies, Model structure, Databases and mappings, Extensions and script rules, Packs and
  generation, Reference types, Seeds, Localization, Branding, Processes, Actors and gates, Lifecycles, Scenarios,
  Process import and export, Process expressions and simulation), with its id, description and default severity. Each row's
  picker is **Default** (the engine's severity), `error`, `warning`, `info` or `off`; the model file rules (MQ1xxx) have
  no `off`. The filter box matches an id or a word of the description; a changed row shows a dot until you save, the
  count says how many rules are overridden, and **Reset all** sets every picker back to Default. Save writes only the
  overrides to `validation.rules` in `maquettiste.json` (Default removes the rule's entry) and the Problems panel
  revalidates; Discard drops the unsaved changes. The rows take the keyboard: the arrow keys move between rows, Enter
  moves into the picker, whose arrows change the value. The same catalog is `GET /api/validation/rules` and the MCP tool
  `list_validation_rules`.

## Processes, actors and scenarios

A **process** is a statechart: a **lifecycle** describes the states of one entity (its subject), usually bound to an
enum attribute of that entity whose members are the lifecycle's root-level states; an **orchestration** coordinates work
and needs no subject. **Actors** are the people, roles and external systems that raise events and sign gates.
**Scenarios** are recorded runs of a process, step by step, with what each step is expected to lead to; the engine
replays them to check the process still behaves as written. A process is drawn on its **chart** (a statechart canvas)
and tried out in the **simulation panel** under it; its grids edit the same document.

### The Processes explorer

The rail's **Processes** icon (after Domain model) opens it. Processes are grouped by domain (domains without processes
are hidden); under a process its **States** (the state tree, nested), **Events** and **Scenarios**, each scenario with
its last replay status (not run, passed, or failed at step N). The **Actors** folder follows the domains. Opening a
state, event or scenario row opens the process editor on that tab with the row selected. Domain model's **Processes**
kind folder lists the same process rows.

Right-click a process for **Open**, **Open in new tab**, **Simulate** (opens its chart with the simulation panel),
**Verify scenarios**, **Export XState**, **Where used**, **Move to domain…**, **Rename**, **Add to favorites** and
**Delete** (its scenarios and its chart's diagram go with it). Right-click a domain group for **New process…** and
**Import XState…**. The header's **+** offers New process…, New actor… and New scenario…. An actor or a scenario row
offers **Open**, **Open in new tab**, **Where used**, **Rename**, **Add to favorites** and **Delete**. New scenario… from a
process's Scenarios folder starts on that process.

A process's chart is a diagram like any other: once you have arranged it, the **Diagrams** explorer lists it under the
process's domain as "statechart of <process>", and opening it there opens the process editor on its Chart tab. It has
no **Duplicate** (a process has one chart), and renaming the process renames its chart too while the chart still
carries the process's name.

### New process…

**Name**, **Domain** and **Use**:

A sentence under the choice says what each one means. A **lifecycle** describes the states one entity goes through (a
sales order from Draft to Completed): it has a subject entity, and its root states can be bound to an enum attribute
of that entity so the two never drift. An **orchestration** coordinates work across people, roles, systems and other
processes (a purchase approval with parallel checks, tasks and signatures): a subject entity is optional. The same
sentence is the tooltip of **Use** in the process editor.

- **Lifecycle**: pick the **Subject entity**, then its **Bound attribute**: one of the subject's enum-typed attributes,
  or **New status attribute and enum**, created in the same change (its default is `Initial`, the first state). With
  an existing enum, the lifecycle starts with one root state per member, so the enum and the states agree. When the
  subject already has a lifecycle, the dialog says so, and that process becomes an orchestration in the same change.
- **Orchestration**: the subject entity is optional (None).

The line under the fields says what the process starts with: one state per member, or one state, `Initial`. The new
process opens in its editor, and Undo removes it.

### New actor…

**Name**, **Type** (person, role, external system) and the project's actor **Stereotypes**; a persona stereotype shows
its **Goals** field (one per line).

### New scenario…

**Process**, **Name** and **Start**: **Record from simulation** creates nothing yet: it opens the process's chart with
the simulation panel recording under that name, and **Record to scenario…** in the panel saves what you raise.
**Empty** creates the scenario with one step to fill in.

### The process editor

A process opens in a document tab with its top controls: name and domain, **Use**, **Subject**, **Bound attribute**
(with a drift badge when the enum and the states differ, and **Sync enum** beside it), stereotype, tag and category
chips. **Use** and **Subject** change both sides of a lifecycle at once: making a process a lifecycle (it needs a
subject first), moving it to another entity, or turning it back into an orchestration also updates the entity's
lifecycle and releases the entity's previous lifecycle, as one change. **Lifecycle** can be chosen only once a subject
is set; until then the option reads "Lifecycle (choose a subject first)". The display names, the description and the
top controls start folded away so the chart has the room (the inspector shows the same fields); the chevron at the
right of the title row shows them. It opens on **Chart**. The tabs:

- **Chart**: the statechart canvas with the simulation panel docked below it (both described next).
- **States**: the state tree as a grid: name, type, initial, history, entry and exit actions, invokes, and the bound
  enum member (read-only). Below it, the selected state's **Invokes** grid: name, type (process, service, human task),
  the invoked process and the human task's actors. An invoke a transition still waits on is not removed.
- **Transitions**: source, trigger, event or duration, guard, targets, actions, external and the gate badge; below it
  the **Guards** and **Actions** grids. An expression that does not parse is marked MQ9501 at once. Ctrl+G on an event
  transition adds a gate, or removes the one it has (the row's shield button does the same).
- **Events**: name, actors allowed to raise it (none listed: any actor), where it is used, and the selected event's
  payload attributes.
- **Gates**: one form per gate: its name, the required count out of the signers (saved when you leave the field or press
  Enter), signers, required actors, **Allow repeat signer**, **Reason required**, the meanings and the audit attributes.
- **Context**: the process's context attributes, in the attribute grid.
- **Scenarios**: the process's scenarios with their status; each row's buttons **Replay**, **Open** and **Refresh the
  expectations** (from a replay); Enter opens a scenario, R replays it, Shift+R refreshes its expectations and
  Ctrl+Delete deletes it.

Every grid is keyboard-driven like the attribute grid: arrows move, Enter or F2 edits, Escape cancels, Tab moves to
the next cell, Ctrl+Enter adds a row (on States a sibling, and Ctrl+Shift+Enter a child), Ctrl+Delete removes one. In
a list cell, the arrow keys move through the choices
and Enter, Tab or leaving the cell saves the one chosen. F12 on a cell that names something (a source or target state,
an event, a guard, an action, an actor, an invoked process, a scenario) goes to it, and Shift+F12 lists where it is
used. Each change is one undo step (Ctrl/Cmd+Z).

An **actor** opens in its own editor (name, type, chips; General lists the processes that use it). A **scenario** opens
in its editor: process, outcome and the **Steps** grid, each step with its replay status; **Replay** runs it and shows
the first failure.

### The chart

The **Chart** tab draws the process as a statechart. States are boxes with a 24 px header; a compound state is a box
holding its children, a parallel state holds its regions side by side, separated by dashed lines; the initial state is
marked by a filled dot with an arrow, a final state is a ringed dot, a history state a circled H (H* for deep), a choice
state a diamond. A transition is an edge labelled `event [guard] / actions`, `after 5d`, `done` or `always`; a transition
that waits for approvals carries a badge such as "2 of 3" (hover it for the signers). A lifecycle's root states show
their enum member in muted text. A state or an edge with a problem (unreachable, a dead end, overlapping guards) shows a
badge; hover it for the message. Clicking a state or an edge selects it, and the inspector shows its State or Transition
section; clicking the background shows the process's own fields.

**Arranging the chart.** A chart you have not arranged yet is drawn with an automatic layout; nothing is saved by
looking at it. The first drag, **Layout**, pan or zoom, or collapse creates the process's diagram (named after the
process, in its domain) and saves the arrangement there: from then on every state keeps the place you gave it, and only
**Layout** (the toolbar button, or Ctrl+L, Cmd+L on a Mac) moves states again. Layout arranges the whole chart, or only
the states inside the selected compound or parallel state. A new state (from the chart or the States grid) is placed
inside its container without moving anything else; the container grows to hold it. A compound or parallel state can be
collapsed to one box that says how many states it hides; selecting a state it hides (on the States grid, say) opens it
again. Undo takes an arrangement back like any change.

**Keys on the chart.** The arrow keys move the selection to the nearest state in that direction inside the same
container; Enter enters a compound state (its initial child), Escape goes back to its parent; Tab and Shift+Tab walk the
selected state's outgoing transitions. N adds a sibling state and Shift+N a child; T starts a transition from the
selected state (pick the target with the arrows or the mouse, Enter or a click confirms, Escape cancels); F2 renames;
Delete removes the selection, and when transitions enter the state from elsewhere a dialog lists them: each loses the
state as a target, and one left with no target is deleted with it. While the process has changes that could not be saved (they do not
validate, or someone else changed the process), a delete is refused until you fix or discard them. F12 on an edge
opens its gate, guard or event on the matching tab; Shift+F12 lists where the selected state or transition is used.
Shift-click states, or hold Shift and drag a box, to select several; they move and delete together. Each gesture is one
undo step. Right-click a state for the same actions as a menu (Add state, Add child state, Draw a transition, Rename,
and on a container Lay out its states and Collapse or Expand, then Delete); right-click a transition for Go to
definition and Delete. A screen reader announces the state or transition selected while
the focus stays on the chart.

**Drawing a transition with the mouse.** Every state has a dot on its right edge; it grows when the pointer is on it.
Drag the dot onto the target state and let go: the transition is created on a new event and selected, so the
Transitions tab or F12 opens it to name the event, add a guard or a gate. Letting go on empty canvas draws nothing. The
same drag works between a state and one inside a compound state, in either direction. The keyboard route (T, then the
arrows or a click on the target) draws the same transition. From the Domain model explorer, a process row under
**Processes** opens its editor with a double click; a single click only shows it in the inspector.

### The simulation panel

Under the chart, the **simulation panel** runs the process through the engine: the browser only sends the inputs and
shows the answer, so what you see is what the interpreter does. It starts folded to its title row so the chart keeps
its room; expand it with the chevron on that row (the choice is remembered in this browser), or open it with
**Simulate** on the process's row or **Record from simulation** in New scenario…. Its sections (each collapses):

- **Start**: the context attributes as a form, with their defaults, and the **Clock start**. Editing a value restarts
  the simulation. **From scenario…** loads a scenario's start and steps as the inputs.
- **Enabled**: one row per input the process can take now: an event with its actor (only the actors the event allows),
  its payload fields, and for a gated event the signer, the meaning and the reason; a guard without an expression shows
  a true/false toggle whose value is recorded as an assumption. **Raise** (Enter on the row) sends it; the keys 1 to 9
  raise the first nine rows. A refused input stays in the trace with its reason.
- **Time**: the next timer due, and **Advance** by a duration (the next timer by default); the clock starts at
  2000-01-01 unless the Start says otherwise.
- **Pending**: the service and human tasks waiting for a result, with **Done** and **Error**.
- **Configuration**: the active states by path; the chart highlights them, and the edges just taken flash once.
- **Last step**: the guards evaluated (with their result and where it came from: an expression, an assumption, or
  missing), the actions run and what they changed in the context, the gate's signatures and audit records, or the
  refusal and its reason.
- **Trace**: the inputs so far. Selecting one shows the state after it (the chart follows); Delete removes it and every
  input after it. The title row's restart button (**Restart from the start**) clears them.

While the process has unsaved edits, the simulation runs on the draft, so a chart edit is tried at once; an edit that
does not validate is reported in the panel instead. Record and Replay work on the saved process, so they save your edits
first and refuse while changes that could not be saved remain. **Record to scenario…** (on the title row, available once
an input exists) saves the inputs as a scenario of the process (name, and the outcome prefilled: final when the
process ended, active otherwise), with each step's expected states and context filled from the run; one undo removes
it. While New scenario… is recording, the title row shows **Recording <name>** with a button that stops it. **Replay
scenario…** steps through a scenario: the engine verifies it, each step shows passed or failed, and the replay stops at the first failure with the expected and actual
states side by side; the chart follows the selected step.

### The inspector for processes

The inspector follows the selection inside the process editor: with a state selected it shows the **State** section
(with **Entry and exit**, **Invokes**, **Bound member**, **Description** and **Properties**); with a transition
selected, the **Transition** section (with its **Gate** and **Actors**); otherwise the **Process** section (the binding,
**Code generation hints** and the **Source** JSON). An actor shows its type and the processes that use it; a scenario
its process, outcome, steps and status.

### Verify scenarios, Export XState and Import XState

- **Verify scenarios** (a process's menu) replays every scenario of the process and writes one line per scenario to the
  Output panel; the explorer's statuses follow.
- **Export XState** downloads the process as an XState machine config (`<name>.xstate.json`). What has no XState
  equivalent travels under `meta`, so importing the file back gives the same process.
- **Import XState…** (a domain's menu) takes a pasted config or a file, previews what the import creates and the
  diagnostics (**Preview**), then **Apply** writes the new process as one change.

### Sync enum and the quick fixes

When a lifecycle's bound enum and its root-level states differ (MQ9203), **Sync enum** (beside Bound attribute, or the
fix in Problems) first shows the plan (members added, removed, reordered, and the removals it keeps because something
still uses them), then **Apply** makes the enum follow the states.

The **Problems** panel shows a fix button beside a diagnostic whose rule has one. Each fix is one change with one undo
step, and the panel re-validates after it:

| Rule | Fix button |
| --- | --- |
| MQ9001 | **Set initial**: asks which direct child (the first one preselected); **Remove initial** when the state is not compound |
| MQ9203 | **Sync enum**: the plan first, then Apply |
| MQ9302, MQ9303, MQ9304 | **Update expectations from replay**: rewrites the scenario's expectations and outcome from a replay |
| MQ9013 | **Remove** the unused event, guard, action or invoke |
| MQ9016 | **Remove the member** from the process diagram |
| MQ9102 | **Add to signers**: the gate's missing required actor |
| MQ9105 | **Add the signer to the event's actors** |
| MQ9205 | **Set default to** the initial state's name, on the subject's bound attribute |
| MQ9201 | **Set lifecycle on the subject** (the subject names another lifecycle), or **Make it this entity's lifecycle** (an entity names an orchestration) |

Undo reverts a fix like any change, with one limit that applies to every save: a change that would bring an error back
is refused, so undoing the fix of an error (Sync enum, Set initial) reports "Cannot undo" and the model stays fixed.

### The rules for processes (MQ9xxx)

Every rule below has a row in **Settings › Validation**, where its severity can be changed. The structural rules (MQ9001
to MQ9018, MQ9101 to MQ9106, MQ9201 to MQ9205 and MQ9501) run on every save and every `validate`; MQ9019 is the answer
to a refused batch operation. The scenario rules (MQ93xx) and MQ9502 to MQ9507 come from replaying
scenarios: `validate` replays every scenario of the processes it checks, **Verify scenarios** and `maquettiste process
verify` replay on request, and the simulation panel shows them for its own run. A replay finding is reported on a save but
does not refuse it, so a chart edit is saved and its scenarios can be refreshed afterwards. The import and export rules
(MQ94xx) are returned by Import XState, Export XState and the `process import` and `process export` commands (the
command's export prints MQ9404 but not the MQ9406 note).

| Rule | Severity | What it reports | Fix button |
| --- | --- | --- | --- |
| MQ9001 | error | An initial state that is not a direct child of its compound state (or of the process), or an initial on a state that is not compound | **Set initial**, or **Remove initial** |
| MQ9002 | error | A state whose type contradicts its children: an atomic, final, history or choice state with children, a compound or parallel state without | |
| MQ9003 | warning | An unreachable state: nothing enters it from the initial state (guards ignored); reported once per unreachable subtree | |
| MQ9004 | warning | A dead end: a reachable atomic state with no transition on itself or an ancestor and no invoke | |
| MQ9005 | info | No final state is reachable, so the process never completes (normal for some lifecycles) | |
| MQ9006 | warning | Overlapping transitions for one source and trigger: one after an unguarded one, or a guard tested twice, never fires | |
| MQ9007 | error | Invalid targets: a state of another process, two targets in one region, or targets not in orthogonal regions of one parallel state | |
| MQ9008 | error | Trigger fields that do not fit the trigger: an event trigger without an event, an `after` without a positive duration, `done` on a state that cannot complete, an invoke trigger naming no invoke of the source | |
| MQ9009 | error | A cycle of unguarded eventless (`always`) transitions that would never end | |
| MQ9010 | error | A choice state whose transitions are not all `always`, or whose last one is guarded (no default) | |
| MQ9011 | error | A history state outside a compound parent, or a default target outside its parent | |
| MQ9012 | error | A final state with outgoing transitions or invokes | |
| MQ9013 | warning | An event, guard, action or invoke that nothing uses | **Remove** it |
| MQ9014 | error | A reference to a state, event, guard, action or invoke of another process | |
| MQ9015 | error | A sub-process invoke cycle, a process invoke without a process, or a human task without actors | |
| MQ9016 | warning | A member of a process diagram that is not a state of that process | **Remove the member** |
| MQ9017 | warning | A parallel state with one region | |
| MQ9018 | warning | A `done` transition whose source can never reach a final state, so it never fires | |
| MQ9019 | error | A process operation (Sync enum, set lifecycle, set initial, refresh a scenario) was refused: it names the wrong element, would remove an enum member still in use, cannot replay the scenario to its end, or shares a batch with a change to the same process, enum or scenario | |
| MQ9101 | error | A gate needs more signatures than its signers can give | |
| MQ9102 | error | A gate's required actors are not all among its signers | **Add to signers** |
| MQ9103 | error | A gate on a transition whose trigger is not an event, or two gates on one source and event | |
| MQ9104 | error | A gate without meanings | |
| MQ9105 | warning | A gate signer the event does not allow to raise it | **Add the signer to the event's actors** |
| MQ9106 | info | An actor no process uses | |
| MQ9201 | error | A lifecycle and its subject that do not name each other: no subject, a subject whose lifecycle is another process, or an entity whose lifecycle is an orchestration or another entity's lifecycle | **Set lifecycle on the subject**, or **Make it this entity's lifecycle** |
| MQ9202 | error | A bound attribute that is not a single-valued enum attribute of the subject, or one set on an orchestration | |
| MQ9203 | error | Enum drift: the bound enum's members differ from the lifecycle's root-level states (missing, extra or out of order) | **Sync enum** |
| MQ9204 | warning | The bound enum is used elsewhere too, so syncing it changes those uses | |
| MQ9205 | warning | The bound attribute's default is not the lifecycle's initial root-level state | **Set default to** that state |
| MQ9301 | error | A scenario step accepted while it expects a refusal, or refused while it expects to be accepted | |
| MQ9302 | error | The active states after a step differ from what the step expects | **Update expectations from replay** |
| MQ9303 | error | The context attributes a step changed differ from what the step expects | **Update expectations from replay** |
| MQ9304 | error | Whether the process is final after the last step differs from the scenario's outcome | **Update expectations from replay** |
| MQ9305 | error | A step whose fields do not fit: an unknown event, a time step without a duration, an invoke that is not pending, a payload value of the wrong type | |
| MQ9306 | warning | A guard without an expression is evaluated and the step has no assumption for it, so the replay stops there | |
| MQ9401 | warning | Imported configuration with no place in the model, kept as opaque data and written back on export | |
| MQ9402 | warning | An inline function in an imported configuration became a named stub (its text kept in the description, never run) | |
| MQ9403 | error | The import input is not a statechart configuration the importer accepts | |
| MQ9404 | warning | A feature mapped approximately or dropped on import or export | |
| MQ9405 | error | An id carried in the configuration belongs to another kind or another process, or is used twice; the node got a new id | |
| MQ9406 | info | Export wrote model data with no equivalent in the format into `meta.maquettiste`, so an import restores it | |
| MQ9501 | error | A guard or action expression does not parse | |
| MQ9502 | error | An expression threw during a replay or a simulation | |
| MQ9503 | error | An expression exceeded the sandbox's deadline (50 ms) or statement limit (100,000) | |
| MQ9504 | warning | A guard returned something other than true or false (counted as false) | |
| MQ9505 | warning | An action returned an attribute the context does not declare, or a value of the wrong type (ignored) | |
| MQ9506 | warning | Two guarded transitions of one source and trigger were enabled at once; the first in priority order was taken | |
| MQ9507 | error | One input ran more than 1,000 microsteps (an eventless loop, or events that keep raising each other) | |

`GET /api/validation/rules` and the MCP tool `list_validation_rules` return the full descriptions.

### Generating code from processes

The engine runs no process in your application and creates no table for one: what a process becomes is what the packs
write. Three scopes serve processes: `each process`, `each actor` and `each scenario`, one file each (a filter takes tags,
stereotypes, categories and packages). The example packs use them as follows; the pack READMEs have every detail.

**The C# example pack (`csharp-dapper`)** writes, for each process, under `Processes/<Process>/`:

| Files | What they hold | Who changes them |
| --- | --- | --- |
| `<P>States.cs` | A constant per state path; for a lifecycle, the mapping from the active states to the bound enum | Generated, overwritten on every run |
| `<P>Definition.cs` | The chart as static data: states, transitions in priority order, timers, gates and their meanings | Generated |
| `<P>Contracts.cs` | The context record, one command per event (the typed payload and an envelope with the instance, actor, signer, meaning and reason), the start, invoke-result and timers-due commands, the transition record and one audit record per gate | Generated |
| `<P>Handlers.g.cs` and `<P>Handlers.cs` | The guards and actions: the generated half implements the expressions it can translate and declares the others; the companion holds the code for those | A pair: the companion is written once, then it is the team's |
| `<P>Services.g.cs` and `<P>Services.cs` | An interface per service task and a hook per human task; the companion implements them | A pair |
| `<P>Machine.g.cs` and `<P>Machine.cs` | A typed facade: start, restore, one method per event; the companion adds the team's own queries | A pair |
| `<P>Store.g.cs` and `<P>Store.cs` | The store interface (load and save the instance's snapshot over the version it loaded, append history and audit records); the companion starts as an in-memory store and is adapted to the entities the project mapped | A pair |
| `Endpoints/<P>Endpoints.cs` | One `POST` endpoint per event, each with a user-code region where the request and the response can be reshaped | Regions: the code inside a region survives regeneration; written only when `endpointsFolder` is set, in a committed root |

and, once for the model when it has a process:

| Files | What they hold |
| --- | --- |
| `Dispatch/Dispatch.g.cs` and `Dispatch/Pipeline.cs` | The dispatcher that sends every command through the behaviours to its handler; the companion orders the behaviours and holds the policy hooks (which actors a caller may act as, the transaction) |
| `Dispatch/HandlerRegistry.g.cs` | The typed list of handlers, with a registration method for a dependency-injection container; no reflection |
| `Dispatch/Behaviours.g.cs` | Validation of the payload, authorization by actor, logging (attributes marked sensitive left out), the transaction and the hand-off of transition and audit records to an outbox |
| `Runtime/Statechart.g.cs` and `Runtime/ProcessHost.cs` | The generated interpreter, with the engine's semantics; the companion holds the clock, the timer scheduler and the invoke host, in memory to start with |
| `Processes/Actors.cs` | A constant per actor with its type, and the actors each event allows |

A companion is created once and never overwritten: a guard, action or service that the model adds fails the build until
its companion code is written, which is how the pack tells you what to implement. Keep companions in a committed output
root (the pack's `partialFolder`).

**Scenario tests.** With `testsFolder` set, each scenario becomes one test (`<P>/<Scenario>Tests.cs`). It starts the
instance with the scenario's start context on a manual clock at the scenario's start instant, sends every step through the
dispatcher to the generated interpreter, and asserts after each step that it was accepted, or refused with the reason the
engine gives, the outcomes of the gate audit records the step wrote, the active states and the context attributes it
changed, and after the last step whether the process is final. The tests call your real guards and services, not the
scenario's assumptions, so a handler that disagrees with a scenario fails its test. Fix the model or the handler, never
the test. The test project supplies a small `ScenarioHost` that wires the stores it chose.

**Expressions.** Guards and actions are JavaScript expressions for the engine's sandbox. The C# pack translates a subset:
literals, `context.x`, `event.payload.x`, `event.name` and `event.actor`, comparisons, `&&`, `||`, `!`, arithmetic, `? :`,
and an action's object of context updates. Anything else (a function call, `Math`, a division of integers, arithmetic on a
value that may be null, a comparison across kinds) becomes a stub in the handlers companion with the expression as a
comment, because C# would compute it otherwise. Preview the handlers unit to see which guards became stubs;
packs/csharp-dapper/README.md lists the subset exactly.

**Tables.** The `sql-ddl` pack writes no table for processes unless its parameter `processTables` is `true`: then
`<db>/processes/<process>.sql` holds an instances, a history and an audit table per process (no audit table without
gates). These scripts are outside `schema.sql` and the migrations. A project that wants the tables in its model models
entities for them, maps them to a database and adapts the store companion, as the gate 3 fixture does.

**Documentation.** The `process-docs` pack writes Markdown to a committed root: a page per process (the description, a
state diagram in diagram text, and tables of states, transitions, gates with their audit record, events, context,
guards, actions and invokes), a page per actor (a persona's goals among them), a walk-through page per scenario and an
index. Copy `packs/process-docs` into `.maquettiste/templates/`, set `packs.process-docs.output` (for example `docs`) and
allow that root; packs/process-docs/README.md shows the settings.

**TypeScript.** The sample pack `samples/typescript-pack` mirrors the C# units with the same ids: `*.gen.ts` modules with
companions, the typed registry map, behaviours as composed functions, the interpreter module, endpoints with regions, and
one `node:test` file per scenario.

**Running the tests.** Build and test the generated code with its companions as any other project:
`dotnet build <solution> -warnaserror` then `dotnet test <solution>` (tests/fixtures/models/processes/README.md walks
through the gate 3 fixture's solution); for TypeScript, `npx tsc --noEmit` then `node --test` on the generated tests
(samples/typescript-pack/README.md). `maquettiste process verify` replays the same scenarios in the engine, without
generated code, and is the faster check while you model.

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
run. The example packs are `sql-ddl` (database scripts), `csharp-dapper` (classes and repositories, and the process
code) and `process-docs` (Markdown pages for processes, actors and scenarios); your own packs sit beside them and work
the same way. A pack also has an **output base** (`packs.<pack>.output` in `maquettiste.json`),
the folder its paths start from, and can be switched off there (`enabled: false`).

**A unit** is one line of a pack's work list. It names a template, which elements the template runs for (the scope),
and where the result goes (the output pattern). `sql-ddl` has five units: `table`, `schema`, `migration`, `seed` and `process-tables` (off unless its `processTables` parameter is set).

**The scope** (`for` in `pack.json`) decides how many times a unit runs:

| Scope | Runs | Result |
| --- | --- | --- |
| `each table`, `each entity`, `each enum`, … | once per element of that kind | one file per element: 40 tables give 40 scripts |
| `model` | once, with the whole model | one file for many: a template that loops over every entity writes them all into one file |
| `each locale` | once per declared language | one file per language (a resource file, a dictionary) |
| `each process`, `each actor`, `each scenario` | once per process, actor or scenario | one file each: a page per process, a test per scenario (see "Generating code from processes") |
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
   Inside `{{ }}` the editor completes what the unit's templates can use: its variables (`model`, `element` and the
   scope's own name such as `entity`, `pack.params.<name>`), the members of the model and of the element after a dot,
   the built-in helpers and the pack's own after `|`, and the template language's functions (`string.upcase`,
   `array.size`, ...); hovering a name shows what it holds. With the cursor on a template line, the preview highlights
   the output lines it produced; click an output line to highlight the template lines behind it. The match is made on
   the line's literal text (the template engine reports no positions), so it is approximate, a line of code alone
   matches nothing, and the preview says so.
4. **Save** (Ctrl+S) writes the file under `.maquettiste/templates/<pack>/`, where git sees it.
5. Back on **Plan**, **Plan** shows every file the change touches; **Apply plan** writes them.

To add a unit, use the **Units** tab (Ctrl+Enter), give it a template, a scope and an output pattern, and save.

The buttons above the file tree create, rename and delete pack files. **New file** creates an empty file at the path
you type (inside the pack folder, never `pack.json`). **Rename** moves the file; when a unit names it as its template or
companion, the unit in `pack.json` is rewritten in the same change. Rename and **Delete** are refused while a template
includes the file, and Delete also while a unit names it; the refusal says who uses it.

### How the plan explains itself

A plan is a dry run: it renders what needs rendering and compares it with the disk, and nothing is written until you
apply it. Above the table, one line per pack says what it will do, for example
`sql-ddl: 4 units, 12 files to add, 3 to modify, 1 orphan to delete` (an orphan is a file generation wrote earlier that
no unit produces any more). The table groups the files by unit (`sql-ddl/table`, with its template and its counts);
click a group to fold it. Each file shows its change, its path, its unit, its element and **Why** its unit renders:
"New: no recorded state from an earlier run" the first time, "Customer (entity) changed" or "Template table.scriban
changed" after an edit, "… was edited on disk" when a generated file was changed by hand. Filter by change, pack, unit
or any words.

Below the summary lines, **By cause** counts the files each cause writes, most first ("Template table.scriban changed:
412 files", "Customer (entity) changed: 3 files"); a cause that names something you can edit is a link to it: the
element, the template in its pack, the pack's parameter or unit, or the settings tab. **By output root** counts the
files under each pack's output base (`db: 12 files (8 to add, 4 to modify)`).

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
  A declared locale is optional: a text it lacks falls back along its chain to the default locale, and nothing fails.
  Validation and the plan report the missing counts per shard as **notes** (MQ7204, severity info); to enforce a locale,
  give MQ7205 (one entry per missing text) a severity under `validation.rules` in maquettiste.json, and to silence the
  counts set MQ7204 to `off` there (`"validation": { "rules": { "MQ7204": "off" } }`); both are pickers in
  [Settings › Validation](#settings-validation).
  The new-locale field corrects a tag as you type it: an underscore becomes a hyphen, the language turns lowercase, a
  script Titlecase and the region uppercase (`zh_cn` becomes `zh-CN`, `zh_hant_tw` becomes `zh-Hant-TW`). **Add locale**
  stays in place; while the tag cannot be added, the reason shows beside it ("use language-REGION with a hyphen, such as
  zh-CN, or a language alone, such as fr", or "already a supported locale"), and Save stays off while the block has a
  problem MQ7201 would report. The CLI's `l10n` verbs and `seed export --locale` correct a tag the same way; a hand edit
  of `maquettiste.json` is not corrected, and MQ7201 names the hyphen form to write (`zh-CN`).
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
- While a content locale other than the default is chosen, the **Display name**, **Plural name** and **Description**
  fields of the editor header and the inspector edit that locale's translation (their labels name the locale, and the
  default text is the placeholder); switch back to the default locale to edit the model's own text.
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
2 drift (or a `--check` preview that would change something), 3 hand-edit conflicts (or, for the `l10n`, `seed` and `process` verbs, a file
that changed while the command ran), 4 an internal or usage error (a refused write, or an argument that names no locale, seed or process). A locale argument is read as
the editor reads one: `zh_cn` is `zh-CN` and `fr_ca` is `fr-CA`; a tag that cannot be read exits 4 and says how to write it.

| Command | What it does |
| --- | --- |
| `maquettiste init` | Creates `.maquettiste/` (`maquettiste.json`, the JSON schemas for editor completion, the `sql-ddl` starter pack) and a `.gitignore` block. `--pack csharp-dapper` or `--pack none` picks another starter; `--mcp`, `--skill` and `--agent-setup` register the agent server, and `--mcp --docker <image>` registers a `./mcp.sh` wrapper that runs it from the image (docs/mcp.md). The project is named by `--name <name>`, else the `name` of `package.json`, else the git remote's repository name, else the folder name (so a repository mounted at `/repo` in the image keeps its real name). Running it again keeps what is there. |
| `maquettiste validate` | Validates the model and the packs, and replays every scenario of every process (MQ9301 to MQ9306 and MQ9502 to MQ9507); `--format sarif` for code-scanning tools. |
| `maquettiste generate` | Renders the packs into the output roots of `maquettiste.json`, incrementally: only units whose inputs changed re-render. Prints one line per file (`A` added, `M` modified, `D` deleted, `K` kept). A generated file edited by hand stops the run (exit 3); `--hand-edits overwrite` replaces it. |
| `maquettiste generate --check` | Renders without writing and exits 2 when the committed output differs from the model: the CI gate. |
| `maquettiste generate --watch` | Regenerates on every change under `.maquettiste/` (a save in the editor, a template edit) until Ctrl+C. |
| `maquettiste format` | Rewrites every model file (`maquettiste.json`, `model/**`) in canonical form, the form the editor writes, and prints the count; hand-written files then stop reporting MQ1003. A file that does not pass its schema is left as it is and named. `--check` writes nothing and exits 2 (drift, as `generate --check` does) when a file would change. |
| `maquettiste l10n status` | The default locale, the declared locales and, per translated locale and shard, how many texts are translated, missing and stale (`--format json` for scripts). |
| `maquettiste l10n export fr` | The French texts as XLIFF 2.1 for translators (`--format csv` for a spreadsheet), on stdout or into `--out <file>`. |
| `maquettiste l10n import fr <file>` | Previews what an XLIFF or CSV file adds, changes and confirms (a stale text the translator marked `translated`, `reviewed` or `final` without changing it), and names the units that match nothing; `--apply` plans again and writes it as one save, refused (exit 3) when a shard changes while the command runs; it prints what it wrote, which can differ from an earlier preview if the files changed in between. `--check` exits 2 when the file would change something. |
| `maquettiste l10n prune` | Lists the orphan translations (MQ7203: an entry whose element is gone, or a field its element does not have); `--apply` removes them in one save, as the other `l10n` and `seed` verbs write only with `--apply`. `--check` exits 2 when there are orphans. |
| `maquettiste l10n set-default fr` | Previews making French the default language: each French text moves into the element files and the text it replaces becomes an English translation; `--apply` writes it all in one change to review in git. Other locales' translations turn stale where their source text changed. Sidecar descriptions and orphans are not moved: they stay in the new default's locale folder, which is no longer loaded (MQ7202), and a sidecar-described element keeps its old-language description; the command lists them as skipped. Move or delete them by hand, or run `l10n prune` before switching. |
| `maquettiste seed new <type>` | Creates a reference type's seed (by id or name), named after the type, with the columns code, label and description and no rows; a type that has a seed keeps it. |
| `maquettiste seed export <seed>` | A seed's rows as CSV (the seed's id or name, or the id or name of the element it seeds); `--locale fr` adds the French label and description columns, `--out <file>` writes a file. |
| `maquettiste seed import <seed> <file>` | Previews a CSV import (rows match by `@id`, else by `@code`): added, changed, removed and blocked rows; `--mode replace` also removes the rows the file leaves out, except rows still referenced; `--apply` plans again and writes it, refused (exit 3) when the seed file changes while the command runs; it prints what it wrote, which can differ from an earlier preview if the seed changed in between. |
| `maquettiste process simulate <process>` | Runs a process (by id, name or model path) through the engine from its initial state: `--inputs <file>` (or `-` for stdin) holds an array of steps, or `{ "start": { "context": {...}, "at": "..." }, "steps": [...] }`, each step a scenario step without `expect`; `--scenario <name>` runs a scenario's steps first. Prints one line per input (accepted or refused, and the active states after it), then the configuration, what can happen next and the clock; `--format json` prints the whole trace, `--from <n>` only from input n. The same inputs always give the same trace. |
| `maquettiste process record <process> <name>` | Replays `--inputs <file>` and prints the scenario it would write, each step's expectations and the outcome filled from the replay; `--apply` writes it under `model/scenarios/<process>/`. |
| `maquettiste process verify [<process>...]` | Replays the scenarios of the processes named (all processes when none is) and prints `pass` or `FAIL` per scenario with the first failing step and rule; exits 1 when one fails. `--format json` for scripts. |
| `maquettiste process export <process>` | The process as an XState machine config (`--format xstate`, the only format), on stdout or into `--out <file>`. What has no XState home travels under `meta.maquettiste`, so importing the file back over the process gives the same file. |
| `maquettiste process import <file>` | Previews importing an XState config: `--domain <package>` (with `--name`, `--use lifecycle\|orchestration`, `--subject <entity>`) for a new process, or `--into <process>` to re-import over one, keeping the ids of what matches. Prints the diagnostics and how many ids are created and removed; `--apply` writes it as one change, refused (exit 3) when the process changed while the command ran and exit 1 when the import has errors. `--format json` prints the document. |
| `maquettiste process sync-enum <process>` | Previews making a lifecycle's bound enum follow its root-level states (members added, removed, reordered, and removals refused because a default, allowed values, a seed cell or a scenario still uses the member); `--apply` writes it; `--check` exits 2 when the enum is out of sync. A refused removal exits 1: change the uses first. |
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
- docs/engineering/phase-3-design.md: processes, actors and scenarios, the interpreter, import and export, and the
  process units of the packs.
- packs/csharp-dapper/README.md, packs/sql-ddl/README.md, packs/process-docs/README.md and
  samples/typescript-pack/README.md: what each pack writes.
- docs/mcp.md: the same operations for agents.
