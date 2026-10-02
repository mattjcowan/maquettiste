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
- **Custom types**: named restrictions of a built-in type, such as Email, which can also name the native type their
  columns take in each database dialect (a log sequence number stored as `pg_lsn` on PostgreSQL).
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
| Top bar | The mark (a link home), the project name (the whole model and its settings) with the release and the workspace under it (`v0.5.3 · feature/billing`, details in its tooltip), git branch and changed-file count, the command palette (Ctrl+K or Cmd+K) and the theme |
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
| Pack editor: pack files, template preview, unit help (Generate screen) | Alt+Shift+F, Alt+Shift+V, Alt+Shift+U |

The shortcuts use the physical key (Option+Shift on a Mac) and do nothing while you type in a field; Escape never
closes a panel. A hidden panel leaves a slim edge where it was (a thin strip beside the rail, at the right edge, or
along the top of the centre area; the bottom panel keeps its tab row, the top bar its button): click it to bring the
panel back. Clicking an explorer on the rail also brings the sidebar back.

The layout (which panels are open, the sidebar, inspector and bottom panel sizes, the pack editor's pane sizes, and a
second explorer pinned beside the first) is kept in this browser and comes back when you open the editor again. **Reset layout** in the command
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
their own panel. Going to an element from one of them (a translation queue's "Open element", a row of the References tab) opens
the Domain model screen with the element selected.

The inspector is about the side you are on. On the **Databases** side (the Database screen, or the Databases explorer) a
table you pick shows as the **table**, never its entity: a click on it on the canvas, in the Tables list or in the
explorer, a search result for a table file, and a problem in one all show the same table inspector (see "The table
inspector" below). On the **Domain model** side an entity's inspector is about the entity. The two meet through one
button each way: the table inspector's **Go to entity** opens the entity's editor in the Domain model, and an entity's
**Go to table** (its row menu) shows its table on the Databases side.

An element's inspector has four tabs, switched with the mouse or the arrow keys: **Properties** (its own fields, such
as name, domain, stereotypes, tags and custom properties, plus the kind's settings), **Attributes** (only for kinds that
have attributes: a value object's, stereotype's or relationship's attribute grid, edited there, while a relationship's
Properties keep its kind, inverse name and ends; an entity's attributes as a read-only
list of name, type and a `*` for required, with **Open editor** opening the entity editor on its Attributes tab, where
the entity's grid is edited; an attribute's type and length here are the entity's rules, used for validation, whatever
the column that stores it says), **JSON** (the element's document, editable) and **Used** (the elements that
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
that fit it, such as **Used**, **Show on canvas**, **Add to diagram**, **Move to domain…**, **Go to table** or
**Open mappings**. **Move to domain…** warns before a tag or category declared by a domain would fall out of scope in
the new place. **Apply stereotype…**, **Tag…** and **Set category…** mark a row, or every selected row at once, in one
change. **Promote to entity** on a value object or a custom type makes it an entity (an id key plus the value object's
attributes, or one `value` attribute of the custom type's base) and turns each entity attribute typed as it into a
relationship to the new entity. What pointed at a removed attribute follows: the owner's mapping drops its row for it
(the relationship's foreign key maps by convention) and the owner's seed drops its column; a diagram that showed the
promoted element shows the new entity. A use inside a value object, a relationship or a key, a seed left with no column,
or any other element that refers to it blocks the promotion, and the dialog names each one and lists every rewrite.
One undo reverses the whole promotion. F12 on a reference goes to its definition and Shift+F12 lists where the element is used (the **Used** menu item, shown on the bottom panel's References tab).

### Element editors and General mode

Enter or a double click on an entity, relationship, enum, value object, custom type or domain opens its **editor** as a
tab in the centre, beside the screen. A single click opens it in the **preview** tab (in italics), which the next single
click replaces; editing it, a double click or Enter keeps it open. Each editor has top controls (name, domain, and for an
entity its key, **Base entity**, **Is abstract**, stereotypes, tags and category as chips) over tabs:

- **Entity**: Attributes, Relationships, Indexes, Mappings, Inheritance, Seed data, References, Code generation. The
  attribute grid's last two columns are each attribute's **Display name** and **Description** (what the attribute
  means; the whole text shows when you hover the cell, and Shift+Enter adds a line while you edit it). Under
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
- **Custom type › Definition**: the base type, length, precision and scale, then **Native types**: one row per dialect
  the project's databases use, and **Add a dialect** for another. A value such as `pg_lsn` or `binary({length})`
  replaces the base type's type map entry for every column that stores the type in a database of that dialect: entity
  and child tables, junction tables and the foreign keys that copy a key of the type. `{length}`, `{precision}` and
  `{scale}` take the type's facets; an empty row keeps the type map, and an overlay column's own native type still wins.
  Each value is one undo step. The inspector shows the same fields. Generated code keeps the base type (a `binary`
  custom type is still a byte array in C#).
- **Domain**: General, Tags and Categories. The display name, plural name and description are edited in the editor's
  header only, not again on General.

The chevron at the right of the editor's title row folds the details away (the display names, the description and the
top controls; the inspector shows the same fields) so a chart or a grid gets the room; the choice is remembered in this
browser per kind of element. A process starts folded, so its chart has the room.

**Follow selection** on the tab bar turns the shown editor into **General mode**: it follows the selection in the
explorer and on the canvas and keeps its tab, so you can walk twenty entities on the Mappings tab without reopening
anything. Unsaved edits are kept per element, so moving on never loses one. A reference type opens in the Reference data
screen instead.

When the tabs do not fit, the tab bar keeps its height and shows no scrollbar: arrows at its ends scroll the tabs, as
do the mouse wheel over the bar and moving to a tab with the keyboard, which keeps the shown tab in view. A right click
on a tab (or the menu key, or Shift+F10) opens its menu: **Close**, **Close others**, **Close to the right**, **Close
all**, **Close saved** (every tab without unsaved changes) and **Pin** or **Unpin**; Close others, Close to the right
and Close all leave tabs with unsaved changes open and say how many, and a middle click closes a tab.

F6 moves keyboard focus between these regions. Edits in the inspector are drafts with undo and redo; they are saved
as you go, and a save that collides with a change made elsewhere shows a conflict dialog with the two versions.

## The explorers and screens

An **explorer** is the sidebar tree a rail icon selects; a **screen** is what the centre shows. The command palette's
"Go to screen" group opens any screen.

**Creating elements.** Right-click a domain for New entity, New relationship, New enum, New value object, New custom
type, New sub-domain and New diagram; right-click a kind folder for New of that kind. The **+** button in each
explorer's header offers that explorer's kinds (New domain and the element kinds in Domain model, New reference type,
New database, New diagram), and so does an empty explorer. In the Databases explorer, while a database or anything inside
it is selected, the **+** button also offers **New schema…**, **New table…**, **New view…**, **New sequence…**, **New
routine…**, **New database type…** and **New SQL object…** for that database (see "Creating a table, a view, a sequence
or another database object" below). Every New dialog starts its domain picker on the current
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

  **Display.** The canvas toolbar's **Display** menu sets how much each card shows (**All attributes**, **Keys only**,
  **Names only**) and the cardinality notation (**UML multiplicities** or **Crow's feet**); this browser keeps the
  choice for each diagram. A relationship that has attributes shows a paperclip with their count on its label (point
  at it for their names). Tick **Relation attributes** to draw them instead in a small box hung off the label by a
  dashed line, as a UML association class: the relationship's name, then one row per attribute with its type and the
  key and required marks, as on the cards. The box follows the attribute choice: **Names only** leaves out the types,
  and **Keys only** shows no box (a relationship has no key of its own; its ends identify it). It sits below the label,
  beside the line where the label is on an upright stretch, and follows the edge when you move cards; clicking it
  selects the relationship. Turning it on moves nothing: run Auto-layout to make room for the boxes. Export to SVG or
  PNG includes them. It is off until you tick it. **Minimap** shows or hides the small map at the canvas's lower right
  corner; it is on until you untick it, and the choice is kept the same way.
- **Reference data**: the reference types and their rows. The Reference data explorer is the screen's list: the types
  nested by category with a count on every group, and the explorer's search operators (`*` contains, `^` starts with,
  `~` like with `%`, `=` equals); its **…** menu has **Types A to Z (no categories)** for one flat list. Clicking a
  type there opens it here. A type's rows live in a seed named after it, and a type's only seed (whatever its name) is
  not listed under the type: the type row is its rows, and that rows file is renamed with the type. A type with several seeds (demo rows, test rows) lists them under it, each with its row
  count; a seed there has **Open**, **Used**, **Add to favorites** and **Delete**, but no **Move to domain…** (a
  reference type has no domain) and no **Rename** (it is renamed with its type). Right-click a type (also Shift+F10) for
  **Duplicate**, **Rename** (a dialog with **Name** and **Display name**; the type's only seed, whatever its name, or of
  several the one named after the type, takes the new name), **Move to category…** (when the project has no categories yet, the dialog says
  to add them under Settings › Categories and has **Open Settings › Categories**), **Set storage…** (opens the Storage
  tab), **Export CSV**, **Convert to enum…** (when the type has no fields of its own and its codes are identifiers: rows
  become members and the fields that used the type use the enum) and **Delete…**, which is refused while a field uses
  the type and otherwise deletes the type with its seeds; each is one change that undo reverses. The Reference data
  explorer's row menu offers the same actions except Export CSV (the Rows tab has it), plus **Apply stereotype…**,
  **Tag…** (both for every selected type at once) and **Add to favorites**. When the explorer is collapsed or shows
  another view, the screen shows the same list on its left (with a flat **A to Z** option). Ctrl+1 to Ctrl+5 pick a tab
  and `/` focuses the type search. The header shows the type's display name, then its name, its row count and how many
  fields use it. For the selected type, five tabs; the tab you pick stays when you select another type:
  - **General**: the type's **Name** (the identifier templates and files use: letters, digits and underscores, not
    starting with a digit, such as `UnitOfMeasure` or `car_models`; it commits on Enter or when you leave the field,
    renaming the type's seed with it, one undo step), **Display name** (what lists and headers show), **Plural name**,
    **Description**, **Category** (a list), **Stereotypes** and **Tags** (chips: the **add…** list adds one, the × on a
    chip removes it). These are the same controls the inspector shows for an entity, and each change is one undo step.
    When no stereotype applies to reference types, Stereotypes says so and points to Settings › Stereotypes. A
    reference type has no domain: the category groups it. With two or more declared locales, a collapsed
    **Translations** section below translates the display name, plural name and description, as in the inspector.
  - **Fields**: the built-in `code`, `label` and `description` (names fixed; code's type, length and pattern editable,
    label's length editable; the description is text of any length), then your own fields in the attribute grid. A code
    is a string by default, or an integer (`int16`, `int32`, `int64`) or a `uuid`; a uuid code is written lowercase with
    hyphens (the grid lowercases what you type; error MQ7013 reports any other form). Label and description are marked
    as translated: each locale can translate them. Every field has a **Display name** and a **Description**, saying what
    the field means: `code` and `label` in their own columns of the built-in table, your fields in the attribute grid's
    last two columns (a description edits in a text area where Shift+Enter adds a line; one kept in a separate file is
    named, not edited). The built-in `description` column's text is fixed. The Rows grid's header shows each field's
    display name (else its name), with the name and the description as its tooltip, and the row editor shows the
    description under each field's label. With two or more declared locales, a collapsed **Translations of the
    fields** section below the grid has one table per locale: a line per field (`code`, `label`, then yours) with its
    display name and description in that locale, the default text as the placeholder, a **stale** marker and
    **Confirm** as in the Translations section.
  - **Rows**: the columns are code, label, description, then your fields. A description may span lines: while you edit
    it, Shift+Enter adds a line. Hovering a cell shows its whole text. With one declared locale the status bar says how
    to translate labels and descriptions (declare a second locale under Settings › Locales), with **Open Settings ›
    Locales** beside it; with two or more, each locale's label and description columns sit side by side.
    The grid is a spreadsheet over the type's rows. Arrows move; Enter or F2 edits, Enter commits and moves down, Tab
    commits and moves right, Esc cancels; Ctrl+Enter inserts a row below, Ctrl+D duplicates one (with an empty code),
    Delete clears cells, Ctrl+Delete deletes rows, Alt+Up and Alt+Down move rows; Shift+arrows select a range, Ctrl+C
    copies it as tab-separated text and Ctrl+V pastes such text, adding rows past the end; Ctrl+F finds; Ctrl+1 to
    Ctrl+5 switch tabs; `/` goes to the type search. **Import CSV** shows what a file adds, changes and removes before
    you apply it as one change you can undo; **Export CSV** downloads the rows (`@id`, `@code`, `@label`,
    `@description`, then the fields by name).
    **The row editor** shows one row in full, in a panel beside the grid: Shift+Enter opens it on the active row, as do
    a double click on a row's number and the button that shows beside the number of the active row or the row you point
    at (**Open row N in the row editor**). It has one labelled control per column, with the field's description under
    its label: a text area for text (it grows with the text), a picker for a relationship end. **Save** (Ctrl+S) writes
    every change of the row as one change you can undo; **Cancel** (Esc) closes it without writing. The arrow buttons
    at its top (Alt+Up, Alt+Down) save the row and show the previous or next one. Drag its left edge to make it wider
    or narrower. Translations typed in a locale column are saved to that locale apart from the row, one save per
    locale with every changed field of it, and undo does not reverse them.
  - **Used by**: every attribute whose type is this reference type, with its Many and Required badges; click one to go
    to it. Entities and other elements are grouped by kind and domain ("Entity · Billing"); a reference type's field is
    grouped by the type's category ("Reference type · Measurement"), or under **Reference types** when the type has no
    category.
  - **Storage**: a line above the table says what a storage strategy is: how the packs store the type's codes in a
    database (for example as a lookup table or a check constraint). The project declares its strategies in Settings › Conventions
    (`referenceData.strategies` in the project settings) and may choose a default for every type there; a database's
    settings can choose another. The table has one row for **All databases** and one per database. **Strategy in use** names the
    strategy and where it comes from (**from this type**, **from the database** or **from the project**), or says the
    packs decide. **Set for this type** offers **Use the default (…)**, naming what the default is, **Let the packs
    decide** (no strategy: the templates choose), then each declared strategy by its name; a chosen strategy's
    description shows under it, with its options.

  **New reference type** asks for the name (letters, digits and underscores, not starting with a digit, such as
  `UnitOfMeasure` or `car_models`; no case is required), display name, category and **Stored as**: **Let the packs
  decide** or one of the strategies the project declares, by its name (the example packs' `lookup-table`, `check` and
  `native`), with the chosen one's description under the list, in the Storage tab's words. `check` is preselected when
  the project declares it; the choice applies to every database, and the Storage tab sets it per database.
  `maquettiste init` declares the three strategies the sql-ddl starter builds; a project that declares none sees
  **Declare the standard storage strategies** under Settings › Conventions, one click away. The Storage tab's
  **Preview output** lists the units of the enabled packs that render once per database (a unit whose name mentions
  seed is picked first), names the unit and the database it renders, and shows that unit's output for the chosen
  database without writing it, narrowed to the statements for the type. When the unit does not render for that
  database (its selector does not return it, its pack is disabled…), the preview says why in the engine's words, with
  the preview's own message under **Message from the preview**. Hovering a type in the list shows its first codes. When
  a type has several seeds, a picker beside **Import CSV** and **Export CSV** chooses the seed. Removing a field also
  removes its column from the type's seeds, in the same save (one undo step).

  **Where reference data is translated** (two or more declared locales; nothing of it shows with one): the type's
  display name, plural name and description in the General tab's **Translations** section; each field's display name
  and description in the Fields tab's **Translations of the fields**; each row's label and description in the Rows
  grid's locale columns (the content locale's, or every locale's with **All locales**), or in the row editor's locale
  fields. The translation queue under Settings › Locales lists all of them too. Every translation is optional: a
  missing one shows the fallback text, never a warning. Validation only counts missing texts per shard as a note
  (MQ7204, severity info); MQ7205, one entry per missing text, is off unless Settings › Validation gives it a severity.

  To use a reference type as an attribute's type, open the attribute's **Type** cell: the list has sections (Recent,
  Built-in, Custom types, Enums, Reference data, Value objects) and one search across them. Each reference type shows
  how it is stored beside its name, for example `Country · check` (`packs decide` when no strategy is chosen; `+1`
  when one database is set otherwise). **Many** (Alt+M) makes the
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
  Clicking a table or a column shows the table (with that column) in the inspector, whether or not the table has a file
  of its own, and tints, in the Domain model, the entity mapped onto the table and the attribute mapped onto the column
  (once the entity is expanded; a collapsed folder shows "n related"). Enter, a double click or the row menu's **Open**
  on a table made from an entity opens the Database screen with that table focused and the same table in the inspector;
  such a table has no editor of its own, so **Open** never opens the entity's. A table with columns of its own (designed
  or imported) opens in its table editor, in front of the Database screen focused on it; its row menu also has **Show in
  Database screen**. A view, sequence, routine, database type or SQL object row opens its editor the same way. The screen
  shows table diagrams per database, a list with a filter (the first 300 matches) whose chips **Tables**, **Views**,
  **Sequences**, **Routines**, **Types** and **Objects** pick what it lists (each with its count), a dialect selector, a
  **New** menu, and a live DDL preview for the selected table or the picked object. The preview renders the enabled pack
  that has a unit rendered per database (a unit named schema or table first; with a table selected, that pack's unit for
  each table; with another object picked, its unit for each view, each sequence, each routine, each database type or
  each sql object), names the pack and unit in its header, and says so when no enabled pack has such a unit. A pack's unit
  for an object may write nothing for it (the sql-ddl pack writes an object's own script only when its **objectScripts**
  parameter is on): the preview then says so in a line above and shows the whole database's script, which creates it. The Tables list and the DDL preview each hide from the button in their header ("Hide tables list",
  "Hide DDL preview") or with Alt+Shift+L and Alt+Shift+D on this screen, and come back from the slim strip they leave
  at the edge or the same shortcut; the palette has "Toggle tables list" and "Toggle DDL preview". Like the other
  panels, what you hid stays hidden after a reload, and "Reset layout" shows both again. A database of more than 300 tables is not drawn whole: with no table selected the screen says
  "<n> tables are too many to draw at once" and keeps the list and the DDL preview of the whole database; pick a table
  and the diagram draws it with the tables its foreign keys connect it to, in both directions (at most 300, the header
  saying how many more were left out). A database diagram's table positions and its pan and zoom are kept in your
  browser (there is no diagram file for a database yet); a table added later is placed beside a table its foreign
  keys connect it to, or under the drawing, without moving the others. The database row's menu opens **Mappings**: an entity and its table side by side, where names mapped automatically are
  muted and customised ones are highlighted.

  **Columns.** Under the diagram, the **Columns** panel lists the selected table's columns in a grid: Name,
  **Attribute**, Type, Length, Prec. (precision), Scale, Native, Null, Default, Comment and Description, with key and
  foreign key markers. Arrow keys move, Enter or F2 edits (Enter again saves), Escape cancels, Tab moves right, Space
  toggles Null, and a description edits in a text area where Shift+Enter adds a line. Each saved cell is one change you
  can undo. The edit goes to the table's file: a designed or imported table's own file, or, for a table made from an
  entity, the file that customises it, which holds only what differs from the conventions. A table made from an entity
  that has no such file yet gets one on its first edit, holding just that column's change; Undo removes it again.
  Clearing a cell of such a column returns it to what the conventions give (cleared, Native goes back to the dialect's
  type map). The Comment column shows the comment the database gets: an explicit comment, else (with the **comments**
  convention, on by default) the column's description, else its attribute's. The panel hides from the button in its
  header ("Hide columns").

  The **Attribute** column (read only) names what a column is made from, as `Entity.attribute` (an attribute of a base
  entity says "from" the base, one a stereotype adds shows the stereotype's «key»); hovering it shows the attribute's own
  type, such as `string(255)`, and a click, or Enter on the cell, opens that entity in the Domain model. A foreign key
  column, or a column you added, has none.

  **The column's type is storage, the attribute's is validation.** Type, Length, Prec., Scale and Native are the
  column's physical type, and they are yours to set for every table, whatever the attribute says: an attribute declared
  `string(255)` can be stored in a `text` column, and an attribute of length 128 in a column of length 2056. The
  attribute keeps its own type and length, which is what generated code validates against; the column's is what the
  database stores, and nothing compares the two. Hovering one of these cells says so ("Physical type of the column; the
  attribute keeps its own (string(255)) for validation."). For a table made from an entity, the file that customises it
  may hold, per column, the name, type, length, precision, scale, native type, nullability, default, comment and
  description. A foreign key column follows the column it references on its own (its cells say "Follows the
  referenced column"); you can still set its type, and when it then differs from the referenced column it is MQ4005,
  listed in the Problems panel at once (its row opens the Database screen on that table with the column picked). While
  the model has such an error the Database screen keeps the tables as they last resolved, with the errors above the
  diagram, so you can fix the cell or undo.

  **The table inspector.** On the Databases side the inspector shows the picked table. Its header names the table, its
  file (or "no file"), the save status, **Open in the Database screen** (the arrow icon; it focuses the table on the
  canvas) and Delete when the table has a file. **Properties** starts with what the table comes from: "Projected from
  entity Invoice", "Bound to entity Invoice" for a designed or imported table bound to an entity, or "Junction of
  relationship ...", each with **Go to entity** (or **Go to relationship**), which opens it in the Domain model. With a
  column picked in the grid (a click or the arrow keys) the **Column** section follows: "Derived from attribute
  Invoice.number (string(32))" with **Go to entity**, or "Follows the referenced column customers.id (uuid)" for a
  foreign key, then the column's Name, Type, Length, Precision, Scale, Native type, Nullable, Default, Comment and
  Description, each saved through the same file as the grid, one undo step per field (on Enter or when you leave it).
  The **Table** section edits the table's own fields in its file: origin, name, display and plural names, description,
  category, stereotypes, tags, schema and comment; a table made from an entity with no file of its own shows them read
  only, with a note that a column edit creates its file. **JSON** shows the table's file when it has one, and **Used**
  lists what references it.

  **Creating a table, a view, a sequence or another database object.** A database's New actions are **New schema…**,
  **New table…**, **New view…**, **New sequence…**, **New routine…**, **New query…**, **New database type…** and **New SQL
  object…**. They are on the database row's menu, on the **+** button of the Databases explorer while the database or
  anything inside it is selected, and on the Database screen's **New** menu (for the database it shows). A schema row's
  menu offers every one but New schema… and New query… (a query has no schema) in that schema, and the Tables, Views,
  Sequences, Routines, Types, Objects and Queries folders offer the one they hold. The palette has the same actions while
  the Database screen is showing. Each dialog asks for:

  - **Name**: letters, digits and underscores, not starting with a digit, and not already used in the same schema:
    tables, views, sequences and database types share one set of names, while routines and SQL objects each have their
    own; a query's name is not used by another query of the database.
  - **Schema**: one of the database's schemas; the default schema is picked first. A query has none.
  - For a table, **Kind**: **Designed table (its own columns)**. A projected table is not made here: it comes from mapping
    an entity to the database, and the dialog's **Open the Mappings tab** link goes there. **Start with an id column
    (int64, primary key)**, ticked by default, gives the table its first column and primary key.
  - For a view, **Dialect** (the database's own first, **Any dialect (*)** for SQL every dialect runs, or another) and
    **Body**, the SELECT the view runs, prefilled with `select 1 as id` for the dialect.
  - For a sequence, **Type** (int16, int32 or int64; int64 by default), **Start** and **Increment** (both 1 by default;
    the increment cannot be 0).
  - For a routine, **Routine kind** (**Function**, the default, or **Procedure**), for a function what it **Returns** (a
    built-in type, int32 to start, or nothing), then **Dialect** and **Body** as for a view, the body prefilled with a
    small block for the dialect (a function returns 0, a procedure does nothing).
  - For a database type, **Type kind**: **Domain** with its **Base type** (string to start), **Enum** with its
    **Members** (the labels, separated by commas, each once), **Composite** (it starts with one string field, `value`)
    or **Range** with its **Subtype**.
  - For a SQL object, **Object kind** (what it is, in your words: trigger to start, with suggestions such as grant or
    extension), **Runs** (**After the routines and views**, the default, or **Before the types and tables**), then
    **Dialect** and **Body**, the statements, run as written.
  - For a query, **Result entity** (an entity whose shape each row has, or none for an ad hoc row), **From** (a table or
    view of the database; with an entity, its table there is picked first) and **Alias** (the first letter of each word
    of the source's name: `i` for invoices, `il` for invoice_lines). The query starts valid: with an entity, its select
    list fills the entity's key from the column that stores it; without one, it holds the source's first column.

  **Create** saves the new file in one step (Undo removes it), shows the Databases explorer and opens the element's editor.
  The editors have text tabs:

  - **Table**: **General** (name, category, stereotypes, tags, schema and comment), **Columns** (the column grid described
    below, with **Add column**, which adds a nullable string column named `column_1`, `column_2`…, and **Delete column**
    for the column picked in the grid; deleting a column also takes it out of the table's keys), **Keys** (one grid each
    for the **Primary key**, **Unique constraints**, **Indexes** and **Foreign keys**, with **Add primary key**, **Add
    unique**, **Add index** and **Add foreign key** and a delete button per row; a key's columns are picked from a list of
    ticks and always keep one; a foreign key names the table it references and, optionally, the referenced columns,
    which are that table's primary key when none is picked; On delete and On update default to no action), **Code
    generation** and **References**.
  - **View**: **General** (name, marks, schema and comment), **Body** (one SQL editor per dialect the view has a body
    for; typing marks it **Unsaved**, and it is saved when you leave the editor, press Ctrl+S or click **Save**; **Add
    dialect** adds a body for another dialect, starting from the first one's text, and the bin removes one, a view
    keeping at least one), **Columns** (the columns the view returns, optional: name, type and Null, with **Add column**),
    **Code generation** and **References**.
  - **Sequence**: **General** (name, marks and schema), **Definition** (Type, Start, Increment, Minimum, Maximum, Cache and
    **Cycle**; each number is saved on Enter or when you leave it, and an empty one is left out: Start and Increment then
    count from 1, Minimum and Maximum are the type's, Cache is the database's), **Code generation** and **References**.
  - **Routine**: **General** (name, marks, schema and comment), **Definition** (Routine kind, **Language**, empty for the
    dialect's own, **Security**, the caller's or its owner's rights, **Deterministic**, what it returns under **Returns**:
    **Nothing**, **A single value** with its type, length, precision, scale and native type, or **A table** with a grid of
    its columns, and **Depends on**), **Parameters** (a grid of name, type, length, precision, scale, native type, mode and
    default, with **Add parameter** and, per row, move up, move down and delete; the type is a built-in type or one of the
    database's types, and a native type replaces what the type gives), **Body** (one SQL editor per dialect, as a view's),
    **Code generation** and **References**.
  - **Database type**: **General** (name, marks, schema and comment), **Definition** (the **Type kind**, then only what that
    kind uses: a domain's **Base type**, length, precision, scale and **Check**; a composite's grid of fields, typed like
    parameters; an enum's labels, renamed in place, moved, added with **Add label** and deleted; a range's **Subtype**; and
    the **Native name** columns write, empty for the type's own name), **Dialects** (optional: a definition per dialect,
    the text written after the type's name, which replaces the structured form for that dialect; **Add dialect** adds one
    and the bin removes it), **Code generation** and **References**. Switching the kind drops what the old kind used and
    starts the new one with something to create (a string base, one label, one field or an int32 subtype); Undo brings
    the old one back.
  - **SQL object**: **General** (name, marks and schema), **Definition** (**Object kind**, **Runs** and **Depends on**),
    **Body** (the statements, one SQL editor per dialect, as a view's body), **Code generation** and **References**.
  - **Query**: **General**, **Sources**, **Select**, **Filter**, **Group and order**, **Parameters**, **Collections**,
    **SQL**, **JSON**, **Code generation** and **References**; the Queries part below walks through them.

  **Depends on** lists what must exist before a routine or a SQL object, each with a button that removes it, and its
  picker adds any other table, view, sequence, routine, database type or SQL object of the same database. Every change in
  these editors is one save of the file and one step Undo takes back. The inspector shows a table of its own as the table
  inspector below, a view's schema, comment and the dialects its body has, a sequence's schema and definition, a
  routine's schema, kind and comment with a line on its parameters, result and body, a database type's schema, kind and
  comment with what it is ("Enum: draft, issued, paid"), and a SQL object's schema, kind and phase with the dialects of its
  statements and how many objects it depends on. Deleting one of them goes through the delete plan like any element: a
  routine another object depends on is taken out of that object's Depends on, and a database type that types a
  parameter, a result or a field takes the routine or type that needs it along when deleted with its dependents.

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
  - A column's native type that the database's dialect does not know is MQ4006 (warning). A native type written with
    quotes or a schema, such as `"public"."unit_of_measure"`, names a type the database defines, which Maquettiste cannot
    check: it is MQ4016 (info), reported once per type with the number of columns that use it. A native type named after
    a reference type or an enum of the model (`unit_of_measure`, `unit_of_measure_t`) is known.
  - The dialect lists hold each dialect's documented built-in types (on PostgreSQL also `pg_lsn`, `xid8`, `tid`, the
    `reg` types such as `regclass`, the multiranges and `jsonpath`) and the common extension types `citext`, `hstore`,
    `ltree`, `cube`, `earth`, `geometry` and `geography`. To use a type the list lacks, give a custom type a native type
    for the dialect (on its Definition tab): every column of that type takes it, and the name counts as known. For a
    single column, set its native type in the table's overlay. A type the database itself defines (a domain, an enum)
    is best modeled as a database type (see Routines, database types and other objects below), which a column names by
    id or name; one made by hand outside the model can be written quoted or with its schema,
    `"public"."ledger_position"`, which MQ4016 covers.
  - A foreign key column whose type, length, precision, scale or native type differs from the column it references is
    MQ4005 (error), reported on the file that sets the differing value (a table's column, or a column entry of a mapped
    table's overrides) and naming both columns and both types. A foreign key column follows the referenced column on its
    own; only a value you set on it, or on the referenced column, can make them differ. It is found while the tables are
    resolved, and every validation reports it, not only generation: the Problems panel (with its pointer; the row goes
    to the table), `maquettiste validate` in text, JSON and SARIF, and the agent server's `validate`. The same holds for
    the other findings of resolution (an over-long conventional name, MQ4001; a constraint or overlay that resolution
    leaves out, MQ4008 and MQ4009; MQ4011), which validation reports once the model has no other error.

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

  **Routines, database types and other objects.** Besides tables, views and sequences, a database holds three more kinds
  of file, each in its own folder of the database: routines in `model/databases/<db>/routines/`, database types in
  `types/` and SQL objects in `objects/`. The editor's New menu on a database creates them, and each has its editor
  (see "Creating a table, a view, a sequence or another database object" above). Each has a name, a schema (the
  default schema when it names none), a description, stereotypes, tags, a category, custom properties and generation
  hints, like a view.

  - **A routine** is a stored function or procedure (`routineKind`: `function`, the default, or `procedure`). Its
    `parameters` each have a name, a type (a built-in type such as `uuid` or `decimal`, or the id of a database type of
    the same database), optional length, precision and scale, a `nativeType` that replaces what the type gives, a
    `mode` (`in`, the default, `out` or `inout`) and an optional `default` written as SQL. Its `returns` is a single value
    (a `type` or a `nativeType`, such as `trigger`), a table (`table`, its columns), or absent when it returns nothing.
    `language` defaults to the dialect's own (`plpgsql` on PostgreSQL, `tsql` on SQL Server, `sql` elsewhere); `body`
    holds the text per dialect (or `*` for every dialect): on PostgreSQL what goes between the dollar quotes, on SQL
    Server what follows `AS`. `deterministic` (IMMUTABLE on PostgreSQL), `security` (`invoker` or `definer`) and
    `dependsOn` (the tables, views, sequences, routines, database types and SQL objects of the same database that must
    exist first) complete it. Routines are not overloaded: one name per schema.
  - **A database type** is a type the database owns (`typeKind`): a `domain` (a built-in `base` with its length,
    precision and scale, and a `check` over `VALUE`), an `enum` (its `members`, in order), a `composite` (its `fields`,
    typed like routine parameters) or a `range` (its `subtype`). A `definition` per dialect, the SQL text after the
    type's name (`AS ENUM ('a', 'b')`, `FROM nvarchar(320)`), replaces that structured form for its dialect.
    `nativeName` sets what columns write for the type when it is not the type's own name.
  - **A SQL object** is anything the model does not type yet: a trigger, a grant, an extension, a policy. `objectKind`
    says what it is in your words, `body` holds its statements per dialect, run as written, `phase` says whether they
    run `before` the database types and tables (an extension) or `after` the routines and views (the default: a
    trigger, a grant), and `dependsOn` orders it among the others.
  - **A column typed by a database type.** A column's native type (the Columns panel, or the `nativeType` of the table
    file) may name a database type of the same database, by its id or by its name. The column then writes the type's
    native name: the type's schema-qualified name where the dialect creates the type (PostgreSQL, and SQL Server for a
    domain, which becomes an alias type), else what it stands for (a domain's base, an enum as a string as long as its
    longest member, on SQLite). A foreign key column that copies such a key column follows it: it takes the database type
    and its native name too, unless the foreign key column's own entry pins a type, length, precision, scale or native
    type (MQ4005 then compares the two sides).
  - **The order of the DDL.** The schema script and a first migration create, in order: SQL objects of phase `before`,
    database types, sequences, tables, routines and views, then SQL objects of phase `after`. Routines come before
    views, so a view may call a function; a routine whose `dependsOn` names a view comes after it. Inside each group an
    object follows what its `dependsOn` names (a composite follows the types of its fields).
  - **Migrations.** A changed or removed routine is dropped before the table changes and created again after them; a
    new database type is created before the tables and a renamed one renamed; a changed one gets a TODO line with its new
    definition, since a type in use cannot simply be replaced; a removed one is dropped last. A SQL object's statements
    are opaque: a new or changed one runs again, and a changed or removed one gets a TODO asking you to drop the old one
    by hand.
  - **Problems.** A routine or SQL object without a body for the database's dialect (and no `*` one), or a database type
    with neither its structured form nor a definition for the dialect, is MQ4017 (warning): nothing is created there.
    A parameter, result column or composite field whose type is neither a built-in type nor a database type of the same
    database is MQ4018 (error). A column's native type or a `dependsOn` entry that names an object of another database is
    MQ4019 (error). Routines, database types and SQL objects that depend on each other in a circle are MQ4020 (error):
    there is no order to create them in. Two routines, two database types or two SQL objects with one name in a schema
    are MQ3001.

  **Queries.** Tables, views and routines are the physical model: they are what the database holds, and they generate the
  repository layer. Entities are the shapes the services work with. An entity is filled either by its simple mapping to
  one table, or by a **query**: a query over the tables and views of a database, with joins, filters, grouping,
  ordering, paging and collections, whose result rows have the shape of an entity (or, without one, of its own select
  list), and which a pack turns into the repository method that runs it. A query is data, a tree of JSON objects an
  agent reads and writes and a pack walks per dialect, never SQL text; an opaque text per dialect is allowed for one
  expression at a time. Entities and tables stay separate: a table needs no entity, an entity needs no table, and one
  query may read several tables into one entity.

  A query is one file per query in `model/databases/<db>/queries/<name>.json` (kind `query`). It has a name, a
  description, stereotypes, tags, a category, custom properties and generation hints, like a view, but no schema: a
  query creates nothing in the database. The billing sample's `InvoicesByCustomer`, a little shortened:

  ```json
  {
    "$schema": "../../../../.schema/v1/query.json",
    "kind": "query",
    "id": "01K6QRY0000000000000000001",
    "name": "InvoicesByCustomer",
    "database": "01J92P0V1QRN2181XM2ZWE02W4",
    "description": "A customer's invoices in some statuses, newest first, a page at a time, each with its lines.",
    "entity": "01J92P0V0FJ23CGSNKM7P1W5V7",
    "parameters": [
      { "name": "customerId", "type": "uuid" },
      { "name": "statuses", "type": "string", "length": 1, "collection": true },
      { "name": "offset", "type": "int32", "default": 0 },
      { "name": "limit", "type": "int32", "default": 50 }
    ],
    "from": { "source": "01J92P0V1T0J6RH4MY9H81NYB4", "alias": "i" },
    "select": [
      { "attribute": "01J92P0V0Q9EK961M5HAQ3C5MY", "expression": { "column": "i.01J92P0V0Q9EK961M5HAQ3C5MY" } },
      { "attribute": "01J92P0V0R3VSP7D5238DTNZX1", "expression": { "column": "i.01J92P0V0R3VSP7D5238DTNZX1" } },
      { "attribute": "01J92P0V0SNXS6PZ42VDK42HP9", "expression": { "column": "i.01J92P0V0SNXS6PZ42VDK42HP9" } },
      { "attribute": "01J92P0V0VB1Z49SWERMAGR4TV", "expression": { "column": "i.01J92P0V0VB1Z49SWERMAGR4TV" } },
      { "attribute": "01J92P0V26XYZRDQ8FJ6KZXT6S", "expression": { "column": "i.01J92P0V26XYZRDQ8FJ6KZXT6S" } }
    ],
    "where": {
      "and": [
        { "op": "eq", "left": { "column": "i.01J92P0V1EHF7PB28CZJG9C5SN.01J92P0V0KGPC29TQQG8R57EBM" }, "right": { "param": "customerId" } },
        { "op": "in", "left": { "column": "i.01J92P0V0VB1Z49SWERMAGR4TV" }, "right": { "param": "statuses" } },
        { "op": "isNull", "left": { "column": "i.deleted_at" } }
      ]
    },
    "orderBy": [{ "expression": { "column": "i.01J92P0V0SNXS6PZ42VDK42HP9" }, "direction": "desc" }],
    "paging": { "offset": "offset", "limit": "limit" },
    "collections": [
      {
        "attribute": "01J92P0V1H4D2M1HCK82ASJEWT",
        "entity": "01J92P0V0GWFR78HZH0P8Z3GY7",
        "query": {
          "from": { "source": "01J92P0V0GWFR78HZH0P8Z3GY7@01J92P0V1QRN2181XM2ZWE02W4", "alias": "l" },
          "select": [
            { "attribute": "01J92P0V0X1EDKMF6X5RA8NZWG", "expression": { "column": "l.01J92P0V0X1EDKMF6X5RA8NZWG" } },
            { "attribute": "01J92P0V0Y049452AH0K8CDC9N", "expression": { "column": "l.01J92P0V0Y049452AH0K8CDC9N" } }
          ],
          "where": {
            "op": "eq",
            "left": { "column": "l.01J92P0V1GMF7GJPA7981CH7YG.01J92P0V0Q9EK961M5HAQ3C5MY" },
            "right": { "column": "i.01J92P0V0Q9EK961M5HAQ3C5MY" }
          }
        }
      }
    ]
  }
  ```

  For `postgresql` it renders as:

  ```sql
  SELECT i.id AS id, i.number AS number, i.issued_on AS issuedOn, i.status AS status, i.created_at AS createdAt
  FROM billing.invoices i
  WHERE i.customer_id = @customerId AND i.status IN @statuses AND i.deleted_at IS NULL
  ORDER BY i.issued_on DESC
  LIMIT @limit OFFSET @offset
  ```

  - **The result shape.** With `entity`, each row is that entity: every select field names the `attribute` it fills (its
    name defaults to the attribute's), or a `name` of its own for a value the entity does not hold. A field may also
    fill a member of a value object attribute, written `<attribute id>.<member id>` (the members of one attribute build
    the value together; its name defaults to `totalAmount` for `total.amount`), or the foreign key of a to-one
    navigation, written as the relation end the navigation leads to (the invoice's customer end fills `CustomerId`; its
    name defaults to the foreign key column's, `customerId`). A field that fills an attribute takes the attribute's type
    in generated code: a value of unknown type (an unknown function, an `sql` text) takes it, a number of another
    number type is converted, and a value that cannot convert (a text into a number) is MQ4033. Without `entity`, the
    select list is the row: each field has a `name`, and a `type` (a built-in type) and `nullable` when the expression
    does not say them, and a pack turns the fields into a record. A required attribute of the entity that no field
    fills is MQ4026 (a warning: the rows leave it at its default); an attribute of a value object type spans several
    columns and is not asked for. A query's rows are read-only projections: an entity row holds what the query selects
    and leaves the rest at its defaults, so it is not a row to save back through a repository. The result entity, and
    a collection's element entity, is a concrete one (an abstract one is MQ4041).
  - **Sources.** `from` and each of `joins` name a `source` and an `alias`. The source is a table or view of the
    query's database: a table or view file's id (a designed table, or a synthesized table's override), a table key as
    the database view lists it (`<entity id>@<database id>`), or an entity id for that entity's table there. The alias
    defaults to the table's or view's name. A join has a `kind`: `inner` (the default), `left`, `right`, `full` or
    `cross`, and an `on` condition unless it is a cross join (a join without one, or a cross join with one, is MQ4035);
    the outer side of an outer join reads as nullable. MySQL has no full join (MQ4036), and SQLite runs right and full
    joins from version 3.39 only (MQ4037, a warning).
  - **Column references.** `{ "column": "alias.<column>" }` names a column of a source. The canonical form writes the
    column part as the column's **key**, the `key` the database view lists for it: the attribute id for an attribute's
    column, the attribute path for a value object member (`<attribute id>.<member id>`), the end and key attribute for
    a foreign key (`<end id>.<key attribute id>`), a designed column's own id. A key survives renames and changes of
    the naming conventions, and the model's reference index sees the ids in it, so a delete or a rename follows it. A
    physical column name is accepted too, and is the only form for a view's columns; it breaks when the name changes.
    A name matches ignoring case, then ignoring case and underscores, when only one column matches. Without an alias, a
    name resolves when exactly one source of the query has the column.
  - **Expressions.** Each expression is one object of one of these forms:

    | Form | Meaning |
    | --- | --- |
    | `{ "column": "i.<key>" }` | a column of a source |
    | `{ "param": "customerId" }` | a parameter |
    | `{ "value": "I" }` | a literal string, number or boolean |
    | `{ "value": "2.0", "type": "decimal" }` | a decimal number written as text, so its digits are kept (`2.0`, not `2`); the editor writes a number with a fractional point this way |
    | `{ "null": true }` | the null literal |
    | `{ "op": "+", "args": [...] }` | `+`, `-`, `*`, `/`, `%` or `concat` over two or more arguments (`-` over one negates it; fewer is MQ4042) |
    | `{ "call": "lower", "args": [...] }` | a function: `lower`, `upper`, `coalesce`, `count`, `sum`, `min`, `max`, `avg`, `length` and `now` are spelled per dialect, any other name (letters, digits and underscores, optionally `schema.name`; anything else is MQ4034) is written as given, and a routine id of the database calls that routine; `count` without arguments counts rows |
    | `{ "case": [{ "when": <predicate>, "then": <expression> }], "else": <expression> }` | the first branch whose condition holds |
    | `{ "cast": <expression>, "type": "date" }` | a conversion to a built-in type, through the dialect's type map; on MySQL to the types its `CAST` takes (`CHAR`, `SIGNED`, `DECIMAL(p,s)`, `DATE`, `DATETIME`, `TIME`, `BINARY`, `JSON`, `DOUBLE`) |
    | `{ "sql": { "postgresql": "...", "sqlite": "..." } }` | an opaque expression per dialect (or `*`), for what the tree cannot say; a blank text counts as none |

  - **Predicates.** A condition (`where`, `having`, a join's `on`, a case branch's `when`) is one of `{ "and": [...] }`,
    `{ "or": [...] }`, `{ "not": <predicate> }`, a comparison `{ "op": ..., "left": <expression>, "right": ... }` or
    `{ "exists": <nested query> }`. The comparisons are `eq`, `ne`, `lt`, `le`, `gt`, `ge`, `like`, `ilike` (case
    insensitive; written as `LOWER(a) LIKE LOWER(b)` where the dialect has no `ILIKE`), `in` and `notIn` (a list of
    expressions, or one parameter with `collection: true`), `between` (a list of two: the low and the high bound),
    `isNull` and `isNotNull` (no right side). A nested query of `exists` has its own `from`, `joins` and `where` and
    may name the aliases of the query around it; it is written `EXISTS (SELECT 1 ...)`, its clauses on lines of their own
    indented one level. Negate it with `{ "not": { "exists": ... } }`. A list parameter is only the whole right side
    of `in` or `notIn`; anywhere else (a comparison, the select list, a function's arguments, a list of values) it is
    MQ4039.
  - **Grouping, ordering, paging.** `groupBy` is a list of expressions and `having` a condition over the groups;
    `distinct` removes duplicate rows. Each of `orderBy` has an `expression`, a `direction` (`asc`, the default, or
    `desc`) and optionally `nulls` (`first` or `last`, emulated where the dialect has no such clause). A distinct query
    orders only by expressions of its select list, and not by `nulls` on `sqlserver` and `mysql`, whose emulation adds
    a term the select list does not have (MQ4038). `paging` has an
    `offset` and a `limit`, each a number or the name of an integer parameter; it is written `LIMIT ... OFFSET ...`, or
    `OFFSET ... ROWS FETCH NEXT ... ROWS ONLY` on `sqlserver` and `oracle`.
  - **Parameters.** Each parameter has a `name`, a `type` (a built-in type or a database type of the same database),
    optional length, precision and scale, `collection: true` for a list (an `in` or `notIn` right side), a `default`
    the generated method uses when the caller passes none (a value of the parameter's type; another is MQ4043, as is a
    number too large to hold), and a description. Parameter names differ ignoring case (MQ3001). In SQL a parameter is
    a placeholder, `@name` by default.
  - **Collections.** A collection fills a list per result row: its `attribute` is a collection attribute of the entity,
    or the relation end a to-many navigation of the entity leads to (the invoice's lines above; only an end with a
    navigation name is a navigation, so an unnamed end is MQ4027), or a name for an ad hoc row; its `entity` is the element shape (the navigation's target by default), and its `query` a nested query
    whose `where` ties its rows to the parent with equalities at its top (`l.<invoice key> = i.<id>`). A collection
    runs as a second statement, once for all the parent rows: the parent's statement carries the parent column of each
    equality (as a hidden column `mq_key0_0` when no select field holds it), and the collection's takes the parent values
    as the list parameter `mq_keys0` (`mq_keys1`, ...) and returns each row's value as `mq_key0`, so the rows group under
    their parent. A list parameter is written `IN @mq_keys0`, for a data access library that expands lists into their
    items. A grouped collection (a `groupBy`, or an aggregate such as `count` or `sum`) groups by its key columns too,
    and a grouped parent by its hidden key columns; a distinct parent must select its keys or group by them (MQ4032).
    A collection that names its parent anywhere else, or not at all, is MQ4028. Names starting with `mq_` are reserved
    for these columns and parameters and for the generated code's own names (MQ4040).
  - **The SQL preview.** `GET /api/model/queries/{id}/sql` and the MCP tool `preview_query_sql` return the query's
    statement and one per collection, each with the parameters it names, for the database's dialect or another
    (`dialect`); `placeholder` picks `@name` (the default), `:name` or `$1`, and `lists` picks `expand` (`IN @ids`, the
    default) or `any` (`= ANY(@ids)` with an array parameter on `postgresql`). The database view lists every query with
    its SQL for the database's dialect. An `sql` expression without a text for the dialect asked for is written `NULL`
    in the preview, and MQ4029 comes with it; for the database's own dialect it is an error of the model, and the query
    has no SQL until it is fixed.
  - **Generation.** A pack unit `for` `each query` runs once per query of every database, with the template variable
    `query`; `query_sql query` renders its statement and `query_collection_sql query.collections[0]` a collection's,
    both taking a dialect and options (`{ placeholder: ":", lists: "any" }`) as further arguments;
    `query_sql_parameters query` (or a collection, with the same arguments) lists the parameter names in placeholder
    order, the order `$1`, `$2`... number them. The csharp-dapper pack writes one query class per query
    (`Queries/<Name>Query.g.cs`, the name Pascal-cased, with `Q` in front when it would start with a digit; two queries
    of a database whose class names are equal are MQ4040, as are two collections, or two fields of an ad hoc row, whose
    names are equal once Pascal-cased). A query with collections returns `<Name>Result` records, the row and one list
    per collection; a collection's statement runs for at most 1000 parent keys at a time. The sql-ddl pack has nothing
    to write for a query, and the schema diff does not see queries.
  - **Deletes.** A query refers to its database, its entity, its sources, the attributes its fields fill and the ids in
    its column keys. Deleting one of these is refused while the query names it; with its dependents
    (`delete-dependents`) the query is deleted whole, since its aliases tie its parts together.
  - **Problems.** The resolver checks a query, where the columns are known, and points at the node:

    | Rule | Severity | Finding |
    | --- | --- | --- |
    | MQ4021 | error | a source that is not a table or view of the query's database, or a call of an id that is not one of its routines |
    | MQ4022 | error | an alias declared twice, or a column naming an alias no source declares |
    | MQ4023 | error | a column its source does not have, or a name without an alias that no source or several sources have |
    | MQ4024 | error | a parameter the query does not declare (in an expression or the paging) |
    | MQ4025 | error | a field naming an attribute the entity does not have, or an attribute where no entity is named |
    | MQ4026 | warning | a required attribute of the entity that no field fills |
    | MQ4027 | error | a collection that names neither a collection attribute nor a to-many navigation, or selects nothing |
    | MQ4028 | error | a nested query naming an alias no enclosing query declares, or a collection naming its parent outside an equality at the top of its where |
    | MQ4029 | error | an `sql` expression without a text for the database's dialect (nor `*`) |
    | MQ4030 | error | paging by a parameter that is not an integer |
    | MQ4031 | error | a comparison with the wrong right side (two values for `between`, none for `isNull`, one otherwise) |
    | MQ4032 | error | a distinct parent that neither selects nor groups by a collection's key column |
    | MQ4033 | warning | a field whose value cannot convert to the type of the attribute it fills |
    | MQ4034 | error | a function name that is neither an identifier (or `schema.name`) nor a routine id |
    | MQ4035 | error | a join other than cross without `on`, or a cross join with one |
    | MQ4036 | error | a full join on a MySQL database |
    | MQ4037 | warning | a right or full join on a SQLite database (it needs SQLite 3.39) |
    | MQ4038 | error | a distinct query ordering by an expression its select list does not have (or by nulls on `sqlserver` and `mysql`) |
    | MQ4039 | error | a list parameter used other than as the whole right side of `in` or `notIn` |
    | MQ4040 | error | names that collide in generated code (collections, an ad hoc row's fields, queries of a database, once Pascal-cased) or start with `mq_` |
    | MQ4041 | error | an abstract result or element entity |
    | MQ4042 | error | an operation with fewer than two operands (`-` may take one) |
    | MQ4043 | error | a number too large to hold, a typed literal that is not a decimal number, or a default that does not match its parameter's type |

    A parameter type that is neither a built-in type nor a database type of the database is MQ4018; two parameters or
    two fields with one name, or two queries of a database with one name, are MQ3001.
  - **Building a query in the editor.** **New query…** (see "Creating a table, a view, a sequence or another database
    object" above) creates the query and opens its editor; the Databases explorer lists it in the database's Queries
    folder and the Database screen under its **Queries** chip, where picking one shows its SQL in the preview pane. The
    inspector sums it up (the result entity, or an ad hoc shape of so many fields, and how many sources, parameters and
    collections it has) with **Show the SQL**, which opens the editor on its SQL tab. Nothing in the editor is SQL text
    but the SQL per dialect form: every cell writes the query's data, and every change is one save and one step Undo takes
    back. The tabs:
    - **General**: the name, marks and description, and the **Result entity**. Choosing none turns the select list into
      an ad hoc row: each field keeps its attribute's name as its own. Choosing another entity keeps the fields it has
      (the same attribute, member or foreign key) and turns each of the others into a field of its own name, its
      expression kept: its name, else the old attribute's (numbered when another field has it). Nothing is lost, and
      nothing names what the new entity lacks.
    - **Sources**: the **From** source and its alias, then the joins, each with its kind (inner, left, right, full or
      cross), its source, its alias and its **on** condition. **Add join** adds a source not read yet, one that shares a
      foreign key with a source already read first. **Join by foreign key** fills the condition from the foreign key
      between the joined source and one before it (when several link them, it lists them). Renaming an alias renames every
      column reference through it, the collections' correlations included.
    - **Select**: with a result entity, one row per attribute (an attribute of a value object type spans several columns:
      one row per member instead, `total.amount`), then one per to-one relationship's foreign key (`customer (foreign
      key to Customer)`), its expression, or **Select <attribute>** to give it one, and its type as the engine infers
      it. A field that names what the entity does not have (after a hand edit) is listed under the fields, marked not on
      the entity, with **Remove**. **Fill from columns by name** gives every attribute without a field the column that stores it, else the column
      whose name matches the attribute's ignoring case and underscores (`issuedOn` and `issued_on`), from the source you
      pick. **Add field** adds a field of its own name. Without an entity, the fields are the row: name, expression,
      **Declared type** and **Nulls** (inferred unless you say), moved up and down.
    - **Expressions**: each cell picks a form, then what the form needs. A **Column** is picked from the columns of the
      aliases in scope, shown as `alias.column` and written as the column's key (so a rename does not break it); a
      **Parameter** from the query's own; a **Value** is typed (a number, true or false, or a text, quoted when it would read
      as a number: `'1'`; a number with a fractional point keeps its digits, `2.0`, written as a typed decimal literal); a **Function** names one of lower, upper, coalesce, count, sum, min, max, avg, length and now,
      any other function, or a routine of the database, with its arguments; an **Operation** is +, -, *, /, % or concat
      over its arguments; a **Conversion** converts its operand to a built-in type; **SQL per dialect** holds one text per
      dialect (or * for any), for what the other forms cannot say: it starts as `NULL`, a new dialect's text too, and
      clearing a text removes that dialect (the form keeps one). A **Case** is edited as JSON for now.
    - **Filter**: the where condition as a tree. **Add condition** adds a comparison (left, operator, right; the right
      side follows the operator: nothing for is null, a list or a list parameter for in, the two bounds for between),
      **Add group** a group whose conditions all hold (and) or any of which holds (or), **Add exists** a condition on a
      nested query, edited as JSON for now (its from, joins and where; it may name the aliases around it). Every node
      has **Negate** (not) and **Remove**.
    - **Group and order**: **Distinct**, the **Group by** expressions, the **Having** condition, the **Order by** terms
      (direction, and where the nulls go, the database's order by default) and the query's **Paging**: an offset and a
      limit, each none, a number, or an integer parameter.
    - **Parameters**: a grid of name, type (a built-in type or a database type), length, precision, scale, **List** (a
      list for in and not in), **Default** (a number for a number type, true or false for bool) and description, with
      **Add parameter**, move and delete. Renaming a parameter renames its uses and the paging's.
    - **Collections**: one card per collection: what it **Fills** (a collection attribute of the result entity, or the
      other end of a to-many relationship of it, when that end has a navigation name; a name for an ad hoc one), its **Elements** entity (the relationship's
      other entity unless you pick one), then the nested query's Sources, Select, Filter and Group and order, the same as
      the query's, with the query's aliases in scope: an equality at the top of its filter between one of its columns and
      one of the query's ties its rows to the parent row (`l.invoice_id = i.id`), and the card says by what. **Add
      collection** starts one on the next to-many relationship, its source the other entity's table and its filter that
      tie, from the foreign key.
    - **SQL**: the statement as the engine renders it for the database's dialect, or the one picked in **Dialect**, with
      the parameters it names, then each collection's statement; it renders again after each change, and another
      dialect shows nothing until its own SQL comes. What stops it (a column the source does not have, an undeclared
      parameter) is listed above it with its rule and the node it points at, and shows in Problems too; while it stands
      the SQL is not rendered. A dialect other than the database's without an SQL text renders `NULL` in its place, with
      MQ4029 listed above.
    - **JSON**: the query's file, edited as text.
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
  Alt+Up/Down moves them, Ctrl+C copies, pasting tab-separated cells adds rows past the end, Ctrl+Z undoes, and
  Shift+Enter (or a double click on a row's number) opens the row editor described under Reference data. Every seed
  grid's header has **Import CSV** (paste or pick a file, preview what it adds, changes and removes, then apply it as
  one change you can undo) and **Export CSV** (the seed as `<seed name>.csv`). In the Domain model explorer, an entity's
  Seed data child opens this tab, and the entity's menu has **Edit seed data** and **Import seed CSV…** (which creates
  the seed first when there is none). A seed sits in its element's domain and moves with it, so its menu has no
  **Move to domain…**.

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
    **comments** says where the database comments of tables and columns come from when none is written: with
    **descriptions** (the default) a column takes its own description, else its attribute's, and a table its own
    description, else its entity's (a junction table its relationship's); with **none** only explicit comments are
    written. The sql-ddl pack writes them (`COMMENT ON` for PostgreSQL, a description property for SQL Server, a `--`
    line for SQLite) while its `comments` parameter is on.
  - **Locales**: the content locales (see "Translating the model in the editor").
  - **Validation**: the severity of each built-in rule (see "Settings › Validation" below).
  - **Type maps, outputs, formatters**: the output allowlist (`outputs.allow`), the type maps, the formatters and the
    packs, shown read-only; edit `maquettiste.json` to change them. A type map entry changes the native type of every
    value of a built-in type in a dialect (`typeMaps.postgresql.binary`); to change it for one kind of value only, give a
    custom type its own native types instead, which win over the type map.
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
**Verify scenarios**, **Export XState**, **Used**, **Move to domain…**, **Rename**, **Add to favorites** and
**Delete** (its scenarios and its chart's diagram go with it). Right-click a domain group for **New process…** and
**Import XState…**. The header's **+** offers New process…, New actor… and New scenario…. An actor or a scenario row
offers **Open**, **Open in new tab**, **Used**, **Rename**, **Add to favorites** and **Delete**. New scenario… from a
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

**How transitions are drawn.** A transition runs from the right side of its source to the left side of its target
(the other way round when the target lies behind), as a stepped line with its label on a pill. When that run would
pass through other states, the transition is lifted over them (a backward one runs below), and transitions that share
the stretch take separate lanes, the longer one outside: in a chain Draft, Issued, Paid, Void, a transition from Draft
to Paid arcs over Issued and one from Draft to Void arcs over both, so the alternative paths through a lifecycle stay
visible. Layout puts the states of a chain in one row on purpose; the lifted transitions are what shows the branches.
Routes are not saved: they follow the states wherever you move them.

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

The **Problems** panel is live: every save re-validates the model and the panel shows the result, so a finding leaves
as soon as its cause does, and nothing in it is stale. Its header has one chip per severity (**Errors**, **Warnings**,
**Information**) with the count of each: untick a chip to hide that severity, and the tab's badge counts only what is
shown; the choice is kept in this browser. Information findings are notes, not faults (a lifecycle that never completes,
a locale's missing counts); a rule can also be set to `off` under Settings › Validation. **Validate again** runs every
rule over the whole model now, for the times you want to see it happen.

The panel shows a fix button beside a diagnostic whose rule has one. Each fix is one change with one undo
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

A table offers what its own file says about it, the way an entity does: `table.display_name`, `table.plural_name`,
`table.description`, `table.tags`, `table.category` (with `name` and `path`), `table.stereotypes` (each with `key`
and `name`), `table.properties` (a custom property reads as `{{ table.properties.tablespace }}`, with the stereotypes'
default properties under the file's own values) and `table.generation`. The file is the designed or imported table,
or the overlay you add to a synthesized table; a synthesized table without one has none of these, and its entity's
are at `table.entity`. The rest of the database side works the same way, each from its own file or entry:
`database` (and `table.database`) from the database file, which also gives `by_convention`, `packages`, `quoting` and
`max_identifier_length`; each of `database.schemas` from its entry in that file, with `is_default` (a schema the file
does not declare has no annotations); each column of `table.columns` from its own entry in the table file, a designed
or extra column or the overlay entry of a synthesized one, never from its attribute, which stays at `column.attribute`;
and views and sequences (`database.views`, `database.sequences`) from their files, as are routines, database types and
SQL objects (`database.routines`, `database.types`, `database.objects`, each also on its schema) and queries
(`database.queries`, with their trees as plain objects: `query.from`, `query.select`, `query.where`, `query.collections`);
a column typed by a database type names it at `column.db_type`. `has_stereotype`, `has_tag` and
`in_category` take any of these as well as an element. A child table (a value object or collection stored as a table)
names its attribute at `table.attribute`, and the constraints of a table file keep their ids (`table.indexes[0].id`).

**A pack** is a folder, `.maquettiste/templates/<pack>/`, holding the templates and one `pack.json` that says what to
run. The example packs are `sql-ddl` (database scripts), `csharp-dapper` (classes and repositories, and the process
code) and `process-docs` (Markdown pages for processes, actors and scenarios); your own packs sit beside them and work
the same way. A pack also has an **output base** (`packs.<pack>.output` in `maquettiste.json`),
the folder its paths start from, and can be switched off there (`enabled: false`).

**A unit** is one line of a pack's work list. It names a template, which elements the template runs for (the scope),
and where the result goes (the output pattern). `sql-ddl` has ten units: `table`, `schema`, `migration`, `seed`, `process-tables` (off unless its `processTables` parameter is set), and `view`, `sequence`, `routine`, `database-type` and `sql-object` (one script per object, off unless its `objectScripts` parameter is set).

**The scope** (`for` in `pack.json`) decides how many times a unit runs:

| Scope | Runs | Result |
| --- | --- | --- |
| `each table`, `each entity`, `each enum`, … | once per element of that kind | one file per element: 40 tables give 40 scripts |
| `each view`, `each sequence` | once per view or sequence of every database (the key sequences the engine creates included) | one file each; a filter takes tags, stereotypes, categories and the database, read from the view's or sequence's own file |
| `each routine`, `each database type`, `each sql object` | once per routine, database type or SQL object of every database | one file each, the template variable being `routine`, `database_type` or `sql_object`; a filter works as for views |
| `each query` | once per query of every database | one file each, the template variable being `query`; `query_sql` and `query_collection_sql` render its SQL; a filter works as for views |
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
no unit produces any more). Files that already hold exactly what the plan renders are counted as unchanged
(`20 files unchanged`); when no file needs writing the line says so, `atlas-schema: 737 units, nothing to write: all
737 files already match the disk`, a note says Apply has nothing to do, and **Apply plan** stays disabled. The table
shows only files that change; choose **Show: unchanged** to list the others. The table groups the files by unit (`sql-ddl/table`, with its template and its counts);
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

In the editor, **+** in the Generate explorer header (or **New pack…** in the palette): a name, then **Start from**
**Empty pack** (one each-entity unit and its template, ready to edit) or **Copy of** a pack of this project. From a terminal, `maquettiste pack new <name> --from sql-ddl`
(or `csharp-dapper`, or `empty`). Then give it an output base under an allowed root (below) in the pack editor's header
or in `maquettiste.json`, edit its units and templates, and plan. The copy is yours: change it freely; the example
packs are not updated under you. To remove a pack, use **Remove pack…** in the pack editor's header (or
`maquettiste pack remove <name> --apply`): it deletes the pack's folder and its `packs.<name>` settings entry, and the
files it generated stay on disk, no longer tracked. To rename a pack, use **Rename pack…** beside it (or
`maquettiste pack rename <name> <new-name> --apply`): the folder, the `packs.<name>` settings entry and the record of
the files it generated move to the new name together, so those files stay tracked. Generation hints keyed by the old
name (`generation.<name>` on elements, tables, columns and other parts) move to the new name too: the dialog's **Also
update the generation hints that name this pack** is ticked by default and saves them as one change you can undo, and
the command does the same unless you add `--keep-hints`.

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
CI); `commit: false` marks build output, which `generate` writes again wherever a build runs. Whether git ignores a built
root or the team commits it is the team's choice: Maquettiste does not touch the repository's `.gitignore` unless asked
(`maquettiste init --gitignore` adds the built roots to it). Its own working folder, `.maquettiste/.cache/` (the run
journal and the built roots' manifests), holds a `.gitignore` with a single `*` line, so it is never committed by
accident whatever the repository's `.gitignore` says.

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

The **+** in the explorer header (or **New pack…** in the palette) creates a pack: a name, and **Start from**
**Empty pack** or **Copy of sql-ddl** (or another pack of this project). The line under the choice says what it gives
("One each-entity unit and its template, ready to edit." or "All 9 files of sql-ddl, renamed to my-pack."), and the
dialog says what it writes.

**The pack editor.** Click a pack (or Enter on any row under it) to open it as a tab beside **Plan** in the centre. The
header shows the version, engine range and description, the **Enabled** switch and the **Output base** (both saved to
`packs.<pack>` in `maquettiste.json`), and the project's hand-edit policy (changed in Settings). Four tabs, Alt+1 to
Alt+4:

- **Units**: a grid of the units, edited in place: id, scope, filter, template, output path, write mode, formatter.
  Beside each output pattern the grid shows how it reads, the path it gives for an **Example element** (chosen in the
  toolbar, one of the unit's own elements, the first until you pick another, kept per unit), and how many files the unit plans.
  The picker names each element with its kind, "Customer (entity)", or "Customer @ main" for a table a database makes
  from an entity (hover an entity for its id); past 20 elements it opens a list with a search box (type to narrow,
  arrows and Enter to pick). When two elements would get the same path the cell says MQ6020; a
  path outside every allowed root says MQ6019. The **Unit help** panel on the right explains the focused field and the row's
  scope in plain words; drag its edge to resize it, and its header button (or Alt+Shift+U, or **Toggle unit help** in
  the palette) hides it, leaving a slim edge that brings it back. Ctrl+Enter adds a unit, Ctrl+D duplicates it, Ctrl+Delete removes it, Alt+Up and Alt+Down reorder, Ctrl+S
  saves `pack.json` (every member the grid does not show is kept). When the file changed on disk since you opened
  it, the grid offers **Keep mine** or **Take theirs**.
- **Parameters**: one row per parameter with the right control (a switch, a list, a number, text, or JSON), its
  default, and **Reset to default**. Save writes the project's values; a value that breaks the pack's parameter schema
  is refused in the form, and a value for a parameter the pack does not declare (MQ6024) can be removed.
- **Templates**: three panes. The left and right ones hide with the button in their header (**Hide pack files**,
  **Hide template preview**), Alt+Shift+F and Alt+Shift+V, or **Toggle pack files** and **Toggle template preview** in
  the palette; a hidden pane leaves a slim edge that brings it back, and both resize by dragging their edge. The editor
  remembers your choice in this browser, and **Reset layout** shows them again. On the left, the pack folder's files
  (templates, partials, scripts such as `helpers.js`, and any other text file; `pack.json` is edited on Units); a dot marks a file with unsaved changes, and
  the tab's own dot says some file is unsaved. In the middle, the file in a code editor with Scriban colouring (the
  `{{ }}`, `{{- -}}` and `{{~ ~}}` blocks, keywords, strings, comments, pipes and the functions after them; text outside
  the blocks stays plain); the line above it says which units use the file, directly or through includes. On the
  right, the **preview**: pick a **Unit** (the ones that use the file come first) and one of its elements. The picker
  lists only elements of the unit's scope kind (each entity: entities; each reference type: reference types; each view
  and each sequence: the view and sequence files; each locale: locales; each table, and a `select` scope: the elements
  the unit plans; `model`: none, it renders once),
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
  translation, not the seed. The screen's **General** tab has the Translations section for the type itself, and its
  **Fields** tab a **Translations of the fields** section for each field's display name and description (see
  Reference data).

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
on the Mac), the editor hands its own volume to you and runs as you, so the model and the generated files stay yours. Before
it starts it repairs what an earlier run left owned by another user (root, or UID 1654 from an older image) in `.maquettiste/`
and in the output roots: those files become yours again, and the log says `repaired N files owned by another user under
<path>`. When Docker shows `.maquettiste/` itself as root's (Docker created it before `init` ran, or an earlier run as root
did), the editor runs as the owner of the repository instead and makes the folder yours. Under rootless Podman, whose root
inside the container is you outside it, the editor stays root, which writes your folders as you. Two variables override the
choice when you need to:

- `MAQUETTISTE_UID`: the user id to run as (`id -u`); `0` keeps root.
- `MAQUETTISTE_GID`: the group id to run as (`id -g`); defaults to the folder's group.

The line under the project name names the release and the workspace, `v0.5.3 · feature/billing`, so two editors running side
by side on different ports (one per worktree, say) are easy to tell apart. The container sees only `/repo`, so it takes the
workspace from `MAQUETTISTE_WORKSPACE` when you set it, else the branch in `.git/HEAD`, else, for a linked worktree (whose
`.git` file points at a git folder the container does not mount), the worktree's name:

```
MAQUETTISTE_WORKSPACE="$(git rev-parse --abbrev-ref HEAD)" MAQUETTISTE_PORT=8081 docker compose -f <maquettiste>/docker/compose.yaml --project-directory . -p billing up -d
```

## Which version is running

The release is `0.5.3` in all of these:

- The editor's top bar: the line under the project name starts with `v0.5.3`; its tooltip adds the build, the engine contract
  and model format, the branch, the worktree and the repository folder when they are known.
- `maquettiste --version`: `maquettiste 0.5.3 (engine contract 1.0.0, model format 1)`.
- `GET /api/health` (no sign-in needed) and `GET /api/project`: `productVersion` is the release and `build` its build (in the
  image, the per-build package version such as `0.5.3-b14a8131cfe39`); `/api/project` adds `workspace`, `branch`, `worktree`
  and `repository`. `/healthz` is the host's own liveness check and says only `{"status":"ok"}`.
- The agent server: `get_project` returns the same `productVersion`, `build` and workspace fields, and the server reports the
  release as its version.
- The image: the labels `org.opencontainers.image.version` (the release), `.revision` (the commit) and `.title`
  (`docker inspect -f '{{ index .Config.Labels "org.opencontainers.image.version" }}' <image>`), and the file
  `/opt/maquettiste/engine.version` (the build).

`engineVersion`, in the API and in `get_project`, is the engine contract (`1.0.0`) that packs' `engine` ranges are checked
against. It is not the release and changes far less often.

## The command line

`maquettiste` is the same engine without the editor: it creates the project, checks it and generates the code, which is
what a CI job and a terminal need. `maquettiste --help` lists every option; exit codes are 0 success, 1 validation or read errors,
2 drift (or a `--check` preview that would change something), 3 hand-edit conflicts (or, for the `l10n`, `seed` and `process` verbs, a file
that changed while the command ran), 4 an internal or usage error (a refused write, or an argument that names no locale, seed or process). A locale argument is read as
the editor reads one: `zh_cn` is `zh-CN` and `fr_ca` is `fr-CA`; a tag that cannot be read exits 4 and says how to write it.

| Command | What it does |
| --- | --- |
| `maquettiste init` | Creates `.maquettiste/` (`maquettiste.json`, the JSON schemas for editor completion, the `sql-ddl` starter pack) and prints the built output roots, which `generate` regenerates and the team may ignore or commit as it prefers. It does not read or write the repository's `.gitignore`; `--gitignore` asks it to add (or, run again, refresh in place) a `# maquettiste:begin` … `# maquettiste:end` block listing the built roots and `.maquettiste/.cache/`. A block written by an earlier version, which added it on every run, stays as it is: it is yours to keep, edit or remove. `--hooks` installs git hooks that regenerate the built roots after a checkout or merge. `--pack csharp-dapper` or `--pack none` picks another starter; `--mcp`, `--skill` and `--agent-setup` register the agent server, and `--mcp --docker <image>` registers it as a `docker run` of the image (`--runtime podman` for `podman run`; docs/mcp.md). The project is named by `--name <name>`, else the `name` of `package.json`, else the git remote's repository name, else the folder name (so a repository mounted at `/repo` in the image keeps its real name). Running `init` again, starting the editor or starting `maquettiste mcp` refreshes the JSON schemas when a new version ships different ones; until then `validate` and `generate` warn MQ1008 and name the files that differ. Running it again keeps what is there. |
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
| `maquettiste model export` | Writes the model as data for another system: the canonical document of every element, or of the ones `--kind`, `--package` (id or name), `--tag`, `--category`, `--stereotype`, `--query` (name contains) and `--ids a,b,...` select, as one JSON array (`--format json`, the default) or one document per line (`--format ndjson`, for a pipeline); `--fields name,attributes` keeps only those members of each document (`id` and `kind` always), `--out <file>` writes a file. With `--resolved` it writes the resolved model instead, what templates read, as flat records: `--scope entities` (attributes resolved, inherited ones marked, keys, relations and mappings by id), `databases` (each database's tables, views, sequences, routines, database types, SQL objects and queries), `tables`, `routines`, `database-types`, `sql-objects`, `queries`, `processes` and the other kinds, `all` by default; `--database <id or name>` keeps what is mapped to that database. A model with errors cannot be resolved: the errors go to stderr and the command exits 1. |
| `maquettiste model stats` | The kinds of element the model holds and how many of each; `--by package` adds the counts per package, `--format json` for scripts. |
| `maquettiste model delete <id or name>` | Deletes an element with `--resolution refuse|remove-references|delete-dependents` (default refuse); `--dry-run` prints the delete plan: what would be deleted, cleared or removed, and what blocks it; `--format json` for scripts. Exit 1 when refused or invalid, 3 on a conflict. |
| `maquettiste pack new <name>` | Scaffolds a pack under `.maquettiste/templates/<name>/` (`--from empty`, `sql-ddl` or `csharp-dapper`). Give it an `output` under an allowed root in `maquettiste.json` before the next `generate` (packs/README.md). |

Progress (`--progress plain`, the default when stderr is not a terminal) prints each stage once, in order, with a start
and a done line; `generate --check` prints no write stage, since it writes nothing. When the operating system refuses a
write (the run lock under `.maquettiste/.cache`, the cache folder or an output file), the CLI prints one line naming the
path and exits 1; in a container on a Linux host this usually means the container runs as another user than the one that
owns the mounted folder, or that a file there belongs to another user, so start it with `--user 0:0` (below).

With the .NET SDK installed, the CLI is a .NET tool (`dotnet tool install -g Maquettiste.Cli --prerelease`).

### Reading the model as data

`model export` and `model stats` read the same pages the editor's API (`GET /api/model/elements`, `/api/model/resolved`,
`/api/model/kinds`) and the agent server's `get_elements`, `get_resolved_model` and `get_model_kinds` serve (docs/mcp.md,
"Reading a large model"), so a script can use whichever is at hand. For example, every entity's name and attributes, one per
line, then the tables of the database `main` as the generator sees them, one per line:

```sh
maquettiste model stats
# entity                         5
# ...
maquettiste model export --kind entity --fields name,attributes --format ndjson > entities.ndjson
maquettiste model export --resolved --scope tables --database main --format ndjson > tables.ndjson
# one line per table, each with the table's columns, keys, foreign keys and indexes:
# {"id":"01J92P0V0ETQKXXP951CMMNHH3@01J92P0V1QRN2181XM2ZWE02W4","kind":"table","name":"customers","database":"01J92P0V1QRN2181XM2ZWE02W4","table":{...}}
```

### From the Docker image

The editor image carries the CLI, so a machine with only Docker needs nothing else. Mount the repository at `/repo` and
make it the working directory:

```sh
docker run --rm --user 0:0 -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste init
docker run --rm --user 0:0 -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate
docker run --rm --user 0:0 -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate --check
docker run --rm -it --user 0:0 -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate --watch
```

A shell function saves typing (`-it` only when you are at a terminal, so it also works in scripts and CI):

```sh
maquettiste() { docker run --rm $([ -t 0 ] && echo -it) --user 0:0 -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste "$@"; }
```

Two variants of that function are worth knowing. A team should run the version the repository is on, so read the tag
from the one place that pins it, the compose file's `image:` line, and the editor, the command line and the agent server
then agree:

```sh
maquettiste() {
  local image
  image=$(sed -n 's/^ *image: *//p' docker-compose.yaml | head -n 1)
  docker run --rm $([ -t 0 ] && echo -it) --user 0:0 -v "$(pwd -P):/repo" -w /repo "$image" maquettiste "$@"
}
```

For a machine that only ever runs what it has pulled, the newest version tag on hand works too; it follows the local
images rather than the repository, so two people can end up on different versions:

```sh
maquettiste() {
  local tag
  tag=$(docker image ls mattjcowan/maquettiste --format '{{.Tag}}' | grep -E '^[0-9]+(\.[0-9]+)*$' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)
  [ -n "$tag" ] || { echo "No maquettiste image pulled; run: docker pull mattjcowan/maquettiste:<tag>" >&2; return 1; }
  docker run --rm $([ -t 0 ] && echo -it) --user 0:0 -v "$(pwd -P):/repo" -w /repo "mattjcowan/maquettiste:$tag" maquettiste "$@"
}
```

Both mount `$(pwd -P)`, the real path of the folder: a repository reached through a link mounts by its real path, which
the container runtime on the Mac needs for file sharing.

**File ownership.** `--user 0:0` (as the function passes) starts the container as root only long enough for the image to
pick the user: the command then runs as the owner of the mounted folder, which is you, and writes the model and the generated
files as you. First it repairs what an earlier run left owned by another user (root, or UID 1654) in `.maquettiste/`, in the
output roots and in the files `init` writes at the project root: they become yours again, with one line on stderr,
`repaired N files owned by another user under <path>`. Without `--user` the image runs as UID 1654 and fails on a repository
that user cannot write, with "Access to the path ... is denied" (Linux) or "Permission denied" (the Mac); with
`--user "$(id -u):$(id -g)"` it runs as you but cannot repair a file root owns. The same function works under rootless Podman,
where root in the container is you outside it and the image stays root, so `--userns=keep-id` is not needed.
`MAQUETTISTE_UID` and `MAQUETTISTE_GID` (`-e MAQUETTISTE_UID=...`) pick another user, as for the editor. The CLI works under
any UID: when the image's own folders are not writable it keeps its cache in the container's `/tmp` for that run.

**Order with the editor.** Run `init` before `docker compose ... up`. The compose file bind-mounts `./.maquettiste`; when
the folder does not exist yet, Docker creates it empty (owned by root on Linux) and the editor starts on a project with no
`maquettiste.json`.

**Agents from the image.** `maquettiste init --mcp --docker mattjcowan/maquettiste:<tag>` (through the function above)
registers `maquettiste mcp` in `.mcp.json` as a `"type": "stdio"` server that the MCP client starts with `/bin/sh` and one
command line:

```json
"maquettiste": { "type": "stdio", "command": "/bin/sh", "args": ["-c", "export PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin\"; mkdir -p .maquettiste/.cache; exec docker run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli -e MAQUETTISTE_WORKSPACE=\"$(git symbolic-ref --short -q HEAD 2>/dev/null || basename \"$(pwd -P)\")\" mattjcowan/maquettiste:<tag> maquettiste mcp 2>>.maquettiste/.cache/mcp.log"] }
```

so the client runs the server in the image as you over the repository. The added `PATH` finds `docker` when the client was
started from the desktop on the Mac (it does not inherit the shell's `PATH` then); `$(pwd -P)` mounts the real path of the
project folder, so a repository behind a symbolic link works; the server's messages go to `.maquettiste/.cache/mcp.log`, which
git ignores; `MAQUETTISTE_WORKSPACE` carries the branch git reads on your machine (else the folder's name), which `get_project`
reports as `workspace`. Nothing else is written to the repository. On Windows, which has no `/bin/sh`, `init` writes `docker` as the
command with the arguments `run -i --rm --user 0:0 -v ${PWD}:/repo -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli
-e MAQUETTISTE_WORKSPACE <image> maquettiste mcp`; the client expands `${PWD}` to the project folder, passes `MAQUETTISTE_WORKSPACE`
on when its environment sets it (otherwise the server reads the branch from `.git`), and keeps the server's messages in its own log.
Under Podman add `--runtime podman`, which writes `podman` in place of `docker`. A re-run with another tag replaces the entry
(either form) and removes the `mcp.sh` wrapper that earlier versions wrote. docs/mcp.md has the details.

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

## Extending the model and the generation

Three kinds of files let a project add to what Maquettiste knows, without changing Maquettiste itself. All three are plain
files under `.maquettiste/`, so the editor, the command line, an agent and git see the same thing:

| What | Where | What it adds |
| --- | --- | --- |
| Custom properties | `.maquettiste/extensions/<name>.json` | Fields of your own on elements, shown in the inspector, checked by validation, read by templates |
| Script rules | `.maquettiste/extensions/rules/<name>.js` | Checks of your own, reported like the built-in rules |
| Pack scripts | `.maquettiste/templates/<pack>/*.js` | Helpers, selectors, filters and transforms for one template pack |

In the editor, the **Extensions** node of the Generate explorer lists the custom property schemas and the script rules.
Its row has **New property schema…** and **New script rule…**; a click on the node or on a file opens the **Extensions**
tab beside the pack tabs. The tab lists the files (Custom properties, then Script rules) with **New property schema…**,
**New script rule…**, **Rename…** and **Delete…** above them, shows the chosen file in the code editor, and lists below it
the **Problems** of that file. Save (or Ctrl+S) writes the file on disk with the hash it was read with: when the file changed
on disk meanwhile, a bar offers Keep mine, Take theirs and Compare, as on a pack's Templates tab. A file save is not an undo
step: Undo and Redo cover model edits only, so rename or edit the file back to go back. An agent does the same with the MCP
tools `list_extension_files`, `read_extension_file`, `write_extension_file`, `move_extension_file` and
`delete_extension_file` (docs/mcp.md), and the editor API with `/api/extensions/files` and `/api/extensions/file`.

### Custom properties (`extensions/*.json`)

An extension schema declares properties that elements may carry in their `properties` object. The file holds:

| Member | Meaning |
| --- | --- |
| `name` | Required. A name for the set, shown nowhere else but in messages. |
| `description` | Optional. Shown as the hint of the inspector's fields. |
| `appliesTo.kinds` | The element kinds that get the properties: `entity`, `relation`, `enum`, `attribute`, `enum-member`, `column`, `table`, `process`, `actor` and the other kinds of the model. Empty or absent: every kind. |
| `appliesTo.stereotypes` | Optional. Stereotype keys: when set, only elements that carry one of them get the properties. |
| `properties` | Required. A JSON Schema `properties` object: one schema per property name (`type`, `enum`, `minimum`, `maximum`, `pattern`, `items` and the rest of JSON Schema). |
| `required` | Optional. The property names an applicable element must set. |

The file's own schema is `.maquettiste/.schema/v1/extension.json`. On the Extensions tab the file is checked against it as
you type: a member it does not know, a kind that does not exist or a missing `properties` shows at once under Problems
(marked `schema`) and in the editor. A save that does not pass is refused with MQ5004 and writes nothing; one that passes is
written in the canonical form (the editor then shows the file as written). A file written by hand that is not valid is left
out of the model with MQ5004, and its properties go unchecked until it is fixed.

For example, `.maquettiste/extensions/retention.json` gives every entity with the `audited` stereotype a retention period:

```json
{
  "name": "retention",
  "description": "How long audited records are kept.",
  "appliesTo": {
    "kinds": ["entity"],
    "stereotypes": ["audited"]
  },
  "properties": {
    "retentionDays": {
      "type": "integer",
      "minimum": 1
    }
  }
}
```

The inspector of an audited entity then shows a **Custom properties** section with a **retentionDays** field. The section
has one field per property: a text box, a number box, a checkbox for a boolean, a picker for an `enum`, and comma-separated
values for an array. A value saves into the element's `properties` (`"properties": { "retentionDays": 365 }`); a stereotype's
`defaultProperties` show as the field's placeholder and apply when the element sets no value of its own. For attributes,
enum members and columns the properties are validated and reach the templates the same way; edit them in the element's
JSON. Validation reports a value that does not match as MQ5001 on `/properties/<name>`, and templates read the merged value
as `entity.properties.retentionDays`.

### Script rules (`extensions/rules/*.js`)

A script rule is a check of your own, written in JavaScript. Each file directly in `extensions/rules/` registers one or more
rules with `maquettiste.rule`:

```js
maquettiste.rule({
  id: "money-columns",
  severity: "warning",
  kinds: ["entity"],
  check(element, model, report) {
    (element.attributes || []).forEach((attribute, i) => {
      if (/(amount|price|total)$/i.test(attribute.name) && attribute.type !== "decimal") {
        report(`${attribute.name} holds money and should be a decimal.`, { pointer: `/attributes/${i}/type` });
      }
    });
    const referrers = model.referencesTo(element.id);
    if (element.tags && element.tags.includes("deprecated") && referrers.length > 0) {
      report(`${element.name} is deprecated but still used ${referrers.length} times.`, { severity: "info" });
    }
  },
});
```

| Part | Meaning |
| --- | --- |
| `id` | The rule's id, without spaces. Its findings are `x/<id>` (here `x/money-columns`). Two rules may not share an id. |
| `severity` | `error` (the default), `warning` or `info`. An error stops generation, and a save in the editor or by an agent that would introduce one is refused. |
| `kinds` | The element kinds the rule checks. Empty or absent: every kind. |
| `check(element, model, report)` | Called once for each element of those kinds. |
| `element` | The element's JSON as saved (the canonical document), read-only. |
| `model.get(id)` | An element, or a part of one such as an attribute, by id; `null` when there is none. Read-only. |
| `model.all(kind)` | Every element of a kind, ordered by name, then id. Read-only. |
| `model.referencesTo(id)` | Where an element or part is referenced: `{ fromElementId, fromId, jsonPointer, field, toId }` for each reference. |
| `report(message, { pointer, severity })` | One finding on the element. `pointer` is a JSON pointer into the element (`/name`, `/attributes/0/type`), so the finding points at the right line; `severity` overrides the rule's for this finding. |

**New script rule…** writes a rule that reports nothing until you edit it, with this contract in its comments. In the code
editor, completion offers `maquettiste.rule` (a whole rule), `report`, `element.` and `model.` members, the severities and
the kinds; typing `report(` or `model.all(` shows the arguments.

**Where the findings show.** A rule's findings are `x/<id>` everywhere: in the Problems panel (grouped by element, a click
opens the element at the pointer), under the rule file's Problems on the Extensions tab ("this file's rules report n
findings", each opening its element), in `maquettiste validate` (text, `--format json`, and `--format sarif`, whose rules
list carries every `x/` id seen), in the MCP tool `validate` and in generation, where an error stops the run. Script rules run
by default everywhere: the editor's validation, `maquettiste validate`, `generate`, and the MCP `validate` tool, whose
`includeScriptRules` is true unless you pass false.

**Severities in Settings › Validation.** The family **Extensions and script rules** holds the built-in rules about these
files: MQ5001 (a custom property value that fails its schema), MQ5002 (a rule script error), MQ5003 (a rule that broke a
sandbox limit) and MQ5004 (an invalid extension file). Below the built-in families, **Script rules** lists every `x/<id>`
the project's rule files register, with the severity the rule declares as its Default. Each row's picker sets `error`,
`warning`, `info` or `off` in `validation.rules` of `maquettiste.json`, as for the built-in rules; `off` stops the rule from
running, and the severity you set also applies to the rule's own failures (MQ5002 and MQ5003 on that rule).

**The sandbox.** Rules run in the same sandbox as pack scripts: no files, no network, no access to the .NET runtime, a
fixed clock in UTC, a `Math.random` seeded per file, and the globals frozen once the scripts have loaded. Each call runs
under the limits of `limits` in `maquettiste.json`: `scriptTimeoutMs` (2000 ms by default), `scriptStatements` (5,000,000),
`scriptRecursion` (256) and `scriptMemoryBytes` (64 MB). A rule that breaks one is reported as MQ5003 on that element, and the
other elements and rules still run. Rules run in parallel over the elements, so a rule must not rely on the order of the
calls or keep state between them.

**When a rule file is broken.**

- A syntax error, or a `maquettiste.rule` call without an `id`, with a bad `severity` or an unknown kind, is MQ5002 on that
  file, with its line and column. The Extensions tab shows it as soon as the file is saved (the save still writes the file,
  so work in progress is kept), and `validate` reports it too. The other rule files keep running without it.
- A `check` that throws is MQ5002 on the line of the rule file that threw, with the element it was checking; the findings the
  rule reported before it threw are kept, and the other elements are still checked.
- A rule id registered twice is MQ5002 on the second file.

Clicking such a finding in the Problems panel opens the rule file on the Extensions tab.

### Pack scripts (helpers, selectors and transforms)

A template pack may carry JavaScript of its own: every `*.js` in the pack folder (or the files its `pack.json` lists in
`scripts`) runs in the same sandbox and registers functions for that pack's templates. packs/README.md, section 6, has the
details; in short:

- `maquettiste.helper(name, fn)`: a function templates call like a built-in (`{{ join_path a b }}`), also in `output` paths.
  A name taken by a built-in helper is MQ6013.
- `maquettiste.selector(name, fn)`: `fn(model)` returns the elements (or their ids) a unit with `for: "select <name>"` runs
  for. An id the model does not know is MQ6017.
- `maquettiste.filter(name, fn)`: `fn(element, model)` decides whether a unit's `where.script` keeps an element.
- `maquettiste.transform(name, fn)`: `fn(element, model)` returns an object merged into the template's `data` for the units
  that list it in `transforms`.
- `maquettiste.params`: the pack's parameters, read-only.

Model objects reach pack scripts as read-only views of the resolved model that record what was read, so a unit re-renders
when what its scripts read changes. A script error is MQ6016 and a broken limit MQ6007. Pack scripts are edited on the pack's
Templates tab (helpers.js and the other files of the pack), and agents use the pack file tools.

### Formatters and linters

The model folder is written by Maquettiste in its own canonical form: JSON with the keys in a fixed order, two-space
indentation, LF line endings and a final newline, so that every save, whoever makes it, gives the same bytes and a diff shows
only what changed. Its JavaScript files run in the sandbox above, not as modules of a JavaScript project. A repository's formatter or linter
therefore has nothing to fix there, and its rules (module syntax, globals it does not know such as `maquettiste`, quote and
comma styles) do not apply. Leave `.maquettiste/` out of them. Maquettiste writes none of these files for you; they are your
repository's, so add the lines yourself:

- `.prettierignore`: a line `.maquettiste/`.
- The linter's ignore list: in `eslint.config.js`, an entry `{ ignores: [".maquettiste/"] }` (or a `.maquettiste/` line in an
  older `.eslintignore`).
- `.editorconfig`: a section that keeps an editor from reformatting the files on save:

  ```ini
  [.maquettiste/**]
  indent_style = space
  indent_size = 2
  end_of_line = lf
  insert_final_newline = true
  trim_trailing_whitespace = false
  ```

- `.gitattributes`: a line `.maquettiste/** text eol=lf`, so a checkout on Windows keeps LF line endings.

Other formatters and linters have the same kind of ignore file or setting; give it `.maquettiste/`. `maquettiste init`
reminds you of this in the line it prints after its report.

If a formatter rewrites a model file anyway, nothing breaks: the file still loads, as long as it is still valid JSON with the
same content. An element file, `maquettiste.json` or a locale file that is no longer in canonical form gets the warning
MQ1003 ("not in canonical form"); `maquettiste format` rewrites every such file in canonical form (`--check` lists them
without writing), and the next save of the element from the editor or an agent rewrites it too. An extension schema
reformatted by hand gets no warning; its next save on the Extensions tab writes it in canonical form again. A rule script
that a formatter only re-indents still runs as before; one that a linter's automatic fixes turn into a module (`import`,
`export`, `require`) no longer loads and reports MQ5002 on its file.

## Where the details are

- SPEC.md: the specification, Sections 5 to 12 for the model and generation, 14 for the editor.
- docs/engineering/phase2-design.md: how the editor and its API are built.
- docs/api/openapi.yaml: the editor's API.
- packs/README.md: how to write a template pack.
- schemas/v1/extension.json: the schema of a custom property file.
- docs/engineering/explorer-redesign.md: the rail, the explorers, search and the element editors.
- docs/engineering/reference-types-seeds-localization.md: reference types, seeds and localization.
- docs/engineering/phase-3-design.md: processes, actors and scenarios, the interpreter, import and export, and the
  process units of the packs.
- packs/csharp-dapper/README.md, packs/sql-ddl/README.md, packs/process-docs/README.md and
  samples/typescript-pack/README.md: what each pack writes.
- docs/mcp.md: the same operations for agents.
