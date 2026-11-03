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
- **Databases**: the physical side, managed on its own: schemas, tables with their columns, keys, indexes and
  checks, views, sequences, routines, database types, SQL objects and queries, and a diagram of the tables. A database
  knows no entities and holds only what is made in it; an entity says on its own **Storage** tab which table, view or
  query it reads and writes (its **binding**), or that it is **domain only**.
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
table you pick shows as the **table**: a click on it on the canvas, in the Tables list or in the explorer, a search
result for a table file, and a problem in one all show the same table inspector (see "The inspector: one subject at a
time" below). The database side names no entity. On the **Domain model** side an entity's inspector is about the
entity, and an entity's **Go to table** (its row menu) shows its table on the Databases side.

An element's inspector has four tabs, switched with the mouse or the arrow keys: **Properties** (its own fields, such
as name, domain, stereotypes, tags and custom properties, plus the kind's settings), **Attributes** (only for kinds that
have attributes: a value object's, stereotype's or relationship's attribute grid, edited there, while a relationship's
Properties keep its kind, inverse name and ends; an entity's attributes as a read-only
list of name, type and a `*` for required, with **Open editor** opening the entity editor on its Attributes tab, where
the entity's grid is edited; an attribute's type and length here are the entity's rules, used for validation, whatever
the column that stores it says), **JSON** (the element's document, editable) and **Used** (the elements that
reference it; a row goes to the referring element). The inspector remembers the tab you chose for each kind of
element; a kind without the chosen tab shows Properties.

Every element's Properties end with its **property bag**, titled Properties: free key and value pairs of your own, kept in
the element's `properties` beside the custom properties a schema declares (see "Custom properties" under "Extending the
model and the generation"). **Add property** adds a row with its cursor in the key; a row has a key, a value, a type
(**Text** by default, **Number** or **True/false**, which store a JSON number or boolean; a value saved otherwise shows its
type) and a remove button. A key is required and unique: a missing or repeated one is said under its row and nothing saves
until it is fixed; renaming a key keeps its value. Each change saves when you leave the field or press Enter (a type, a
true/false pick and a removal at once), one undo step each. A key that a custom property schema declares keeps its typed
field in Custom properties and is never a row of the bag. An element editor's **Code generation** tab, a state selected in
a process and a table's column show the same bag.

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
opened last. Each row has a remove button (the cross, on hover) that takes it off the list, and each list's header has
**Clear**: clearing the recents is immediate, clearing the favorites asks first; both clear only the rows that explorer
lists. An element you delete leaves the recent list on its own.

The keyboard walks the tree: the arrows move and expand, Enter opens, F2 renames, Delete deletes, and Shift or Ctrl with a
click or an arrow selects several rows of one kind. Every row has a right-click menu (also Shift+F10) with the actions
that fit it, such as **Used**, **Show on canvas**, **Add to diagram**, **Move to domain…**, **Go to table**, a
table's **New index**, or the storage actions **Auto-map to existing tables…**, **Create tables…** and **Remove
bindings…** (see [Storage: binding an entity](#storage-binding-an-entity)). **Move to domain…** warns before a tag or
category declared by a domain would fall out of scope in
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

- **Entity**: Attributes, Relationships, Indexes, Storage, Inheritance, Seed data, References, Code generation. The
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
  **Storage** binds the entity to databases (see [Storage: binding an entity](#storage-binding-an-entity)).
- **Relationship**: its ends at the top, then Attributes, Storage, Code generation, References. **Storage** says, per
  database, how the relationship is stored (see [Relationship storage](#relationship-storage)); every edit there is one
  undo step.
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
explorer and on the canvas and keeps its tab, so you can walk twenty entities on the Storage tab without reopening
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
    **Stored in a table**, under it, is optional: per database, the table file that holds the type's rows. Choosing a
    table maps each field (Code, Label, Description, the type's own fields) to the column named like it; change any of
    them, and add **constant columns** (a fixed value on every row, such as a column naming the type, when several types
    share one table). A database with a table shows **Table <name>** as its strategy in use: the table wins over the
    strategy there. Validation then counts the type's rows as the table's rows: a table seed's foreign key into the table
    may name a code, a table seed of the table may not repeat one where both are loaded, and the binding itself is checked
    (MQ7117 to MQ7120: a table file of its database, a code column, a key made of the code column and the constants, every
    NOT NULL column filled). The database's seed data lists the type's rows first in the table (source `list`).

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
  database (its scope or filter leaves it out, its pack is disabled…), the preview says why in the engine's words, with
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
- **Databases**: each database with its schemas and, in each schema, its tables, views, sequences, routines, database
  types and SQL objects, and the database's queries (a query has no schema). The database side is a database manager of its own: it shows tables, never the
  entities of the Domain model (how an entity reads and writes a table is set on the entity's side). Expanding a table
  loads its folders: **Columns** (type, PK and FK markers), **Primary key**, **Unique constraints**, **Indexes**,
  **Foreign keys**, **Checks**, and **Referenced by** when another table's foreign key points at it. Clicking a table, or
  anything under it, shows it in the inspector (see "The inspector: one subject at a time" below). Enter, a double click
  or the row menu's **Open** opens the table in its **table editor**, whatever the table: a folder opens the editor on
  its tab, and a column, key, index or check opens it on its tab with that row picked (a **Referenced by** row opens the
  other table's editor on its foreign key). While an editor shows, a click on a table or one of its rows moves the
  editor to it. A table's row menu offers **Open**, **Show in Database screen**, **New column**, **New unique
  constraint**, **New index**, **New foreign key** (it opens the foreign key dialog, to pick the referenced table and
  columns) and **New check**, and **Rename** (F2 too; a table not stored as a file yet is stored first); a table file also
  has **Used**, **Add to favorites** and **Delete** (its delete plan first, as anywhere else). A folder offers its own New
  (the Primary key folder **New primary key** when the table has none); a column, key, index or check offers **Rename**
  (F2 too) and **Delete** (see "Deleting a column or a key" below). Each of these is one change, one step Undo takes back,
  and a new part opens picked on its tab. A view,
  sequence, routine, database type or SQL object row opens its editor the same way.

  The **Database screen** shows the table diagram of one database, a list with a filter (the first 300 matches) whose
  chips **Tables**, **Views**, **Sequences**, **Routines**, **Queries**, **Types** and **Objects** pick what it lists (each with its
  count), a dialect selector, a **New** menu, and a live DDL preview for the selected table or the picked object. The
  preview renders the enabled pack that has a unit rendered per database (a unit named schema or table first; with a table
  selected, that pack's unit for each table; with another object picked, its unit for each view, each sequence, each
  routine, each database type or each sql object), names the pack and unit in its header, and says so when no enabled pack
  has such a unit. A pack's unit for an object may write nothing for it (the sql-ddl pack writes an object's own script
  only when its **objectScripts** parameter is on): the preview then says so in a line above and offers the whole
  database's script, which creates it. The whole database (nothing picked, or that fallback) renders every table, so it
  renders only when you click **Preview the whole database**; after that it follows the model while a render takes under
  a second, and a slower one is marked **Out of date** with **Preview again**. A picked table or object renders at once. The diagram has most of the screen: the list starts narrow and the DDL preview
  starts hidden behind the slim strip at the right edge (click it, or press Alt+Shift+D, to open it). The Tables list and
  the DDL preview each hide from the button in their header ("Hide tables list", "Hide DDL preview") or with Alt+Shift+L
  and Alt+Shift+D on this screen, come back from the slim strip they leave at the edge or the same shortcut, and resize
  from the line between them and the diagram (a drag, or the arrow keys once it has focus); the palette has "Toggle
  tables list" and "Toggle DDL preview". Like the other panels, what you hid and the widths you chose stay after a
  reload, and "Reset layout" goes back to the defaults (the list shown, the DDL preview hidden). A database of
  more than 300 tables is not drawn whole: with no table selected the screen says "<n> tables are too many to draw at
  once" and keeps the list and the DDL preview of the whole database; pick a table and the diagram draws it with the
  tables its foreign keys connect it to, in both directions (at most 300, the header saying how many more were left out).
  A database diagram's table positions and its pan and zoom are kept in your browser (there is no diagram file for a
  database yet); a table added later is placed beside a table its foreign keys connect it to, or under the drawing,
  without moving the others.

  **Tables that are not table files yet.** A model made before table files were the rule may still have tables that the
  conventions lay out, with no file of their own (or with a small file holding only a few adjustments). They show and
  work like every other table, and the Database screen says so once, at the foot of the diagram: "5 tables are not stored as
  table files yet. Store them as files to edit every part of them." **Store as table files** checks the change first,
  then writes one table file per table, holding every column, key, index and foreign key the table has (a file that held
  adjustments becomes the table's file and keeps its id), in one step that Undo takes back whole. You do not have to use
  it: the first edit of any part of such a table (a column's cell, a key, an index, a check, the comment, a property)
  stores that table as a file first and then makes the edit, says "customers is now stored as a table file", and Undo
  takes both back at once. Undoing a store is safe for your migrations: the database's schema snapshot remembers the key
  each table had before it was stored, so neither the store nor its undo (nor a redo) reads as dropping and creating the
  table in the next migration. Storing the table again after an undo (a new store, not a redo) takes back the ids it had
  when it was first stored (the table's, its columns' and its key sequence's), so the next migration holds only what really
  changed in between, such as a column you added and then undid. And the store checks that nothing it changes was saved by someone else meanwhile (it is
  refused then, and nothing is written), so its undo never puts back an older version. The table's JSON tab shows the whole file from then on. A table editor or inspector showing a
  table that is not a file yet also has **Store as a table file** for that one table. A few tables cannot be stored yet
  (a table linking two others for a many-to-many relationship, a table holding a list of values, and the tables laid out
  for an abstract entity or for the entities of an inheritance hierarchy); **Store as table files** leaves them out of its
  count (its tooltip says so), their columns still edit, but their keys and constraints are set by the model and say so.

  **The database diagram.** Each table is a card: its schema-qualified name, its column count, and one row per column
  with its markers (a key for the primary key, a link for a foreign key, a fingerprint for a column that is unique on its
  own, an asterisk for not null; hover a marker for its name) and the dialect's native type. The diagram shows tables
  only: a table not stored as a table file yet looks like any other. The toolbar's **Display** menu picks **All columns**, **Keys only** (primary key, foreign key and unique columns) or **Names only** (the table names),
  and turns the minimap on or off; your browser keeps the choice. **Auto-layout** arranges the tables again; dragging a
  card moves it, and positions are kept as described above.
  - *Create a foreign key by dragging.* Hover a column row: a dot shows at its right edge. Drag it onto a column of
    another table (or of the same table, for a key that references its own table), or onto a table's header. The
    **New foreign key** dialog opens with the dragged column paired with the column you dropped on (or with the dropped
    table's primary key; one pair per key column for a composite key). Change a pair, **Add column pair** for a
    composite key, or **Add column** to add a column to the table, typed like the column it references and named after
    the referenced table (`invoice_id` for `invoices.id`). The name follows the project's foreign key naming
    convention (Settings > Conventions, `fk_{table}_{columns}` by default) until you type another; **On delete** and
    **On update** take No action, Restrict, Cascade, Set null or Set default, and **Deferrable** when the key is checked
    (not deferrable, the default; per statement; or at commit; on SQL Server and MySQL, which check every key on each
    statement, a note says the DDL leaves it out, MQ4056). The referenced columns must be the referenced table's primary key or
    one of its unique keys (a unique constraint, or a unique index without a filter except on Oracle), each column once; on
    MySQL some key or index of the referenced table must also start with them in the order the pairs name them: anything
    else is refused, as the database would (MQ4059). Dropping on one column of a composite key references the whole key, the
    dragged column in the dropped one's place. A column whose native type differs from the one it references is pointed
    out; it is not refused. **Create** writes the key into the table's file, one undo step. The same dialog opens from
    **New foreign key** in the explorer and from **Add foreign key** on the table editor's Foreign keys tab. Dragging from a table's header starts the same dialog with the columns to choose. From the keyboard, Tab to a
    row's dot (or the header's) and press Enter: the dialog opens with that column and the referenced table to pick.
  - *A table that is not stored as a table file yet* (see above) is stored as one first: the dialog says so, and Create
    stores the table as a file, then writes the key into the new file (its columns found under the ids the store gave
    them), in one undo step ("invoice_lines is now stored as a table file", then "Foreign key ... created."). Undo puts
    the table back as it was; Redo stores it again with the key.
  - *Foreign keys on the diagram.* Each key is a line between the two tables, from the row of its first column to the row
    of the column it references when both rows show (else between the headers), on the sides that face each other. Its
    ends are crow's feet: at the referencing table, many (or zero or one when the key columns are unique); at the
    referenced table, exactly one when every key column is not null, else zero or one. Hovering a line shows the key's
    name and a tooltip with its columns, rules and ends. A click selects it and shows the key alone in the inspector,
    under its table's breadcrumb, where every field of it edits (columns, referenced table and columns, rules,
    deferrable) and **Delete** removes it; a double click (or **Edit in the dialog…** in the inspector) edits it in the
    same dialog as a new key (**Save**), which keeps what the dialog does not show (its comment, properties and anything
    else in the file). Delete on a selected key asks first, then removes it from the table's file (a relationship's
    mapping that named the key no longer does). Each is one undo step; the key's columns stay.
  - *Tables.* **New table** in the toolbar, or a double click on an empty part of the diagram, opens the New table dialog;
    a table created from a double click lands where you clicked. Delete on a selected table opens its delete plan, as
    the explorer's Delete does (a table not stored as a table file yet cannot be deleted from here).

  **Deleting a column or a key.** Delete on a column (the Columns tab, the explorer, the inspector), the primary key, a
  unique constraint, an index, a foreign key or a check works as a database manager does: a column goes with every key,
  constraint and index that holds it, whole (a composite primary key, unique constraint, foreign key or index; an index
  that includes it or whose filter reads it; a check on it or whose expression reads it) and with the table's own
  foreign keys to it. Before anything is written, a plan lists what else goes when there is more than the part itself:
  other tables' foreign keys to a key that goes (a key that loses a column goes whole; a foreign key without referenced
  columns references the primary key), and how many storage bindings follow ("2 storage bindings follow": a relationship's
  mapping that named a foreign key that goes, a binding's fields, constants, listed columns and soft-delete column that read
  the column; **Files** lists their files, and the plan never names what they belong to). A query whose references resolve to
  the column keeps the delete from happening (change the query first): each reference's alias is followed to its source (a
  table, or an entity bound to it; without an alias, the table's own name), and the column matches by its name in any case,
  its id or an attribute bound to it, nested queries included. Listed to check by hand: a query that may read it (a reference
  without an alias while the query has other sources, an SQL expression that names it), a view whose body names it or returns
  `*` from the table, a computed column or default that reads it, and every routine and SQL object whose text names it.
  **Delete** then changes everything in one batch and one undo step; a delete that takes nothing else goes at once. The plan
  is written as it was read: if any document it changes was saved since (another tab, another person), nothing is written and
  the notice says to ask again for the new plan. The primary key, a unique key or a unique index goes the same way, with the
  foreign keys that reference it. A table not stored as a file yet (the one the part is on, or one whose foreign key goes with
  it) is stored first, before the plan is made, so the plan shows exactly what Delete writes; the store and the delete are one
  undo step, and **Cancel** (or a plan that cannot be carried out) takes the store back, leaving nothing in the history.

  **Renaming a column.** A column's new name (the grid, the inspector, the explorer's Rename) is written where the table
  names it in SQL: its checks' expressions, its indexes' filters and expressions, its computed columns and its per-dialect
  defaults; in the bodies of the database's views that read the table; in its queries, where a reference's alias leads to the
  table (`o.order_number`, nested queries included) and in their SQL expressions when the table is the query's only source;
  and the storage bindings that name the column by its name follow (the notice says how many, never whose). SQL text is read
  per dialect (MySQL's `"…"` strings and backslash escapes, PostgreSQL's `E'…'` and `$$…$$` strings, SQL Server's `[a]]b]`,
  names in any script) and only columns are rewritten: never text in strings or comments, a function call, a type, or a
  table, an alias or a qualifier of the same name (`JOIN customer ON customer.id = o.customer` becomes `… = o.customer_id`).
  A view whose body returns the column under its own name (`SELECT email`) while its column list is off gets
  `SELECT mail AS email`, so the view's column and what reads it keep their name. All of it is one save and one undo step,
  and the notice says where the name was rewritten. Left as it is and listed to check by hand: a name that is also an SQL
  keyword; a text that also uses the name for a table, an alias or a qualifier while some use of it stands bare; a view that
  reads another table with a column of the same name, or returns `*` without a column list (its column would change name);
  a query's reference without an alias that another source may hold; and every routine and SQL object whose text names the
  column. Renaming a view's declared column (the view editor's Columns tab, or its JSON) renames it in the view's body too
  when the Column list option is off (the alias that names it, or `AS` added; a body that does not name it plainly is listed
  to check), and in the bindings that read the view, in the same undo step; a query's column renamed is followed by the
  bindings that read it the same way. Renaming a table (its General tab, the explorer) lists the views, queries (those that
  read it without an alias, so their references say its old name), routines and SQL objects that still name the old name.

  **Columns.** Under the diagram, the **Columns** panel lists the selected table's columns in a grid: Name, Type,
  Length, Prec. (precision), Scale, Native, Null, Default, Comment and Description, with key and foreign key markers.
  Arrow keys move, Enter or F2 edits (Enter again saves), Escape cancels, Tab moves right, Space toggles Null, and a
  description edits in a text area where Shift+Enter adds a line. Each saved cell is one change of the table's file you
  can undo. Clearing Native goes back to the dialect's type map. The Comment column shows the comment the database gets:
  an explicit comment, else (with the **comments** convention, on by default) the column's description. The panel hides
  from the button in its header ("Hide columns"). Type, Length, Prec., Scale and Native are the column's physical type,
  yours to set for every table. A foreign key column follows the column it references on its own (its cells say
  "Follows the referenced column"); you can still set its type, and when it then differs from the referenced column it
  is MQ4005, listed in the Problems panel at once (its row opens the Database screen on that table with the column
  picked). While the model has such an error the Database screen keeps the tables as they last resolved, with the errors
  above the diagram, so you can fix the cell or undo.

  **The inspector: one subject at a time.** On the Databases side the inspector shows the picked table, or one part of
  it. For the table, its header names the table and its file (or "not stored as a table file yet"), the save status,
  **Open the table editor**, **Open in the Database screen** and Delete when the table is a file; **Properties** edits the
  table's own fields in its file (name, display and plural names, description, category, stereotypes, tags, schema,
  comment, its custom properties and its property bag), **JSON** shows the file (or, before the table is a file, the
  table as resolved, read-only, with **Store as a table file**), and **Used** lists what of the database references the file
  (other tables' foreign keys, views, routines, queries, SQL objects; an entity bound to it shows on that entity's Storage
  tab, not here, as on every database-side element's Used list). Pick a
  column (in the grid, the explorer or the table editor), a key, an index, a foreign key or a check, and the inspector
  shows that part alone, under a breadcrumb such as **billing.customers › email**: its table part (tooltip "Back to the
  table") returns to the table. A column shows its Name, Type, Length, Precision, Scale, Native type, Nullable, Default,
  Comment and Description (and "Follows the referenced column customers.id (uuid)" for a foreign key column), its
  DDL facets (**Text encoding** and **Fixed width** where its type has them, **Collation**; see "What a table can say"
  below), how it gets its values (**Generated by**: nothing, **Identity** or **A sequence**, the database's sequences to
  pick from; under **Identity**, **Identity seed** (the first value; empty, the database's 1), **Identity increment** (the
  step, not 0; empty, 1) and **Generated always (an insert cannot give a value)**, with a note when the database's dialect
  leaves some of them out (MQ4056, a warning: a DDL feature the database's dialect lacks, left out of the DDL; what each
  dialect writes is under "Identity options" in "What a table can say");
  **Computed from**, an SQL expression, with **Stored** or virtual; **Default SQL** per dialect, a row for the
  database's dialect and one for any dialect; **Default constraint name**), what does not fit together said
  under them (a sequence not picked, an identity on a type that is not an integer, a computed column with a default or a
  generation), then its Stereotypes, Tags and property bag, each saved on Enter or when you leave it, one undo step each,
  and **Delete column** (its delete plan, as above). A key, an index, a foreign key or a check shows the same fields as
  its row in the table editor, its problems, and **Delete**: an index also its **Index columns**, a unique constraint
  **Nulls not distinct**, a foreign key **Deferrable** (with a note when the database's dialect checks every key on each
  statement), and a check its **Column**.

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
  - For a table, **Start with an id column (int64, primary key)**, ticked by default, which gives the table its first
    column and primary key.
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
  - For a query, **From** (a table or view of the database) and **Alias** (the first letter of each word of the source's
    name: `i` for invoices, `il` for invoice_lines). The query starts valid: its select list holds the source's first
    column. A query is the database's: its rows are an ad hoc shape, and an entity reads through it from the entity's
    Storage tab (a binding whose source is the query).

  **Create** saves the new file in one step (Undo removes it), shows the Databases explorer and opens the element's editor.
  The editors have text tabs:

  - **Table** (every table, stored as a file or not yet): **General** (name, display names, description, category,
    stereotypes, tags, schema, comment, custom properties and property bag); **Columns** (the column grid, with **Add
    column**, which adds a nullable string column named `column_1`, `column_2`…, and **Delete column** for the column
    picked in the grid; deleting a column also takes the keys, indexes and checks that hold it, whole, with its plan
    first when more goes; the picked column's other settings are edited in the inspector: **Text encoding**, **Fixed
    width**, **Collation**, **Generated by** (with **Identity seed**, **Identity increment** and **Generated always** for
    an identity), **Sequence**, **Computed from** and **Stored**, **Default SQL** per dialect, **Default constraint
    name**); **Primary key** (its name, columns and Clustered); **Unique constraints** (name,
    columns and **Nulls not distinct**, which makes two rows with nulls in its columns conflict: PostgreSQL 15 and later,
    the other dialects leave it out, MQ4056); a picked primary or unique key has **Without overlaps** in the inspector,
    which makes it a temporal key (PostgreSQL 18 and later); **Indexes** (name; the columns in key order, each with its order, ascending
    or descending, and a move earlier and later, any column either way; **Include** columns; **Unique**; **Method**; and
    **Where** for a partial index; a picked index row shows its **Index columns** beneath it, described below, and the
    inspector its **Storage parameters** for the database's dialect, the method's names offered as you type);
    **Foreign keys** (name, columns, the table it references, picked from the
    database's tables, the referenced columns, which are that table's primary key when none is picked, and **On delete**
    and **On update**, no action by default, and **Deferrable**: not deferrable by default, or checked per statement or
    at commit; on SQL Server and MySQL, which check every key on each statement, it says so and the DDL leaves it out,
    MQ4056; a picked key whose on delete is Set null or Set default, on two columns or more, shows **Columns set on delete** in
    the inspector: none picked sets every column of the key, pick some to keep the others, such as a tenant column; outside
    PostgreSQL a note says the DDL leaves the list out and sets them all, MQ4056; and **Period**, for a key to a temporal
    key); **Checks** (name, the **Column** it constrains, or the table, and the expression for the database's dialect
    and for any dialect); **Exclusions** (each exclusion constraint with its name, **Method**, **Checked** (when it is
    checked), its elements, each a column or an expression with an operator class and an **Operator**, **Add column** and
    **Add expression**, the **Where** predicate, and the constraint as the DDL writes it); **Storage** (the storage
    parameters for the database's dialect, a value typed as a number or true/false saved as one, what the table's
    stereotypes give it listed above with their stereotype, struck through where the table overrides one; then
    **Partitioning**: **Partitioned by** a strategy, its **Partition columns**, and the partitions, each a name and the
    bounds after FOR VALUES, with **Add partition** and **Add default partition**; a primary or unique key that lacks a
    partition column is said in place, MQ4066); **DDL** (the table's create script, from the DDL preview's unit for one table); **References** (the foreign
    keys of other tables that point at it, each opening that table on its foreign key, and what else of the database uses the
    table's file);
    and **JSON** (the whole file). Each part tab has its **Add** button and a delete button per row; a key's columns are
    picked from a list of ticks and always keep one; a new constraint or index is named after the table and its first
    column (`uq_orders_id`, `ix_orders_id`), and a name typed over replaces it. A row you click is picked: the inspector
    shows it alone, and a row's problems show under it. Every change is one save of the table, one step Undo takes back.

    **Index columns** (beneath a picked index row, and in the inspector's index section) lists the index's columns in key
    order, each a column or an expression; the ƒx button beside an index's columns adds an expression column (a functional index, such as
    `lower(email)`), written per dialect like a check: one text for the database's dialect and one for any dialect (`*`);
    a dialect with neither leaves the index out. SQL Server indexes no expression (MQ4056, the index is left out: index a
    computed column instead); MySQL takes one from 8.0.13, MariaDB not at all. **Operator class** (PostgreSQL) indexes
    a column or expression with a class of its own, such as `gin_trgm_ops` for a trigram index or `vector_cosine_ops`
    for a vector index; the other dialects leave it out (MQ4056). **Prefix length** indexes only the first
    characters of a column; a typed value that is not a whole number from 1 is said in place and not saved (MySQL, which needs one for a text or blob column: without it the index is left out, MQ4056);
    the other dialects leave it out (MQ4056).
  - **View**: **General** (name, marks, schema and comment, then the **DDL** section: **Column list (CREATE VIEW names the
    Columns tab's columns)**; **With check option**, so an insert or update through the view must satisfy its WHERE
    (SQLite, and a materialized view, leave it out, MQ4056); **Materialized**, a view that stores its rows (PostgreSQL and
    Oracle; the other dialects create a plain view, MQ4056; refreshing it is the application's); **Security invoker**, so
    the view reads its tables with the caller's rights and row-level security applies to the caller, and **Security
    barrier** (PostgreSQL; the other dialects and a materialized view leave them out, MQ4056); **Depends on** (below);
    and **Found in the body**, read-only: the other views whose names the body uses, which the scripts create first and
    drop after without your naming them), **Body** (one SQL editor per dialect the view has a body
    for; typing marks it **Unsaved**, and it is saved when you leave the editor, press Ctrl+S or click **Save**; **Add
    dialect** adds a body for another dialect, starting from the first one's text, and the bin removes one, a view
    keeping at least one), **Columns** (the columns the view returns, optional: name, type and Null, with **Add column**),
    **Code generation** and **References**.
  - **Sequence**: **General** (name, marks and schema), **Definition** (Type, Start, Increment, Minimum, Maximum, Cache and
    **Cycle**; each number is saved on Enter or when you leave it, and an empty one is left out: Start and Increment then
    count from 1, Minimum and Maximum are the type's, Cache is the database's), **Code generation** and **References**.
  - **Routine**: **General** (name, marks, schema and comment), **Definition** (Routine kind, **Language**, empty for the
    dialect's own, **Security**, the caller's or its owner's rights, **Deterministic**, **Volatility** for a function
    (empty follows Deterministic), **Settings**, the parameters set while it runs (`search_path`, which a
    security-definer routine should set, MQ4063), what it returns under **Returns**:
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

  **Depends on** lists what must exist before a view, a routine or a SQL object, each with a button that removes it, and
  its picker adds any other table, view, sequence, routine, database type or SQL object of the same database. Every change
  in these editors is one save of the file and one step Undo takes back. The inspector shows a table as described in
  "The inspector: one subject at a time" above, a view's schema, comment and the dialects its body has, a sequence's schema and definition, a
  routine's schema, kind and comment with a line on its parameters, result and body, a database type's schema, kind and
  comment with what it is ("Enum: draft, issued, paid"), and a SQL object's schema, kind and phase with the dialects of its
  statements and how many objects it depends on. Deleting one of them goes through the delete plan like any element: a
  routine another object depends on is taken out of that object's Depends on, and a database type that types a
  parameter, a result or a field takes the routine or type that needs it along when deleted with its dependents.

  **Type checks.** What validation says about a table's types:

  - A column's native type that the database's dialect does not know is MQ4006 (warning). A native type written with
    quotes or a schema, such as `"public"."unit_of_measure"`, names a type the database defines, which Maquettiste cannot
    check: it is MQ4016 (info), reported once per type with the number of columns that use it. A native type named after
    a reference type or an enum of the model (`unit_of_measure`, `unit_of_measure_t`) is known.
  - The dialect lists hold each dialect's documented built-in types (on PostgreSQL also `pg_lsn`, `xid8`, `tid`, the
    `reg` types such as `regclass`, the multiranges and `jsonpath`) and the common extension types `citext`, `hstore`,
    `ltree`, `cube`, `earth`, `geometry` and `geography`. To use a type the list lacks, give a custom type a native type
    for the dialect (on its Definition tab): every column of that type takes it, and the name counts as known. For a
    single column, set its native type in the column grid (the Native cell). A type the database itself defines (a domain, an enum)
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

  **What a table can say.** Besides its columns' types, nullability, defaults, identity or sequence, computed expression,
  collation and comment, a table file holds every database concept the DDL writes:

  - **Unicode and fixed-length text.** A string or text column's `unicode` asks for Unicode text (`true`: `nvarchar` on SQL
    Server, `nvarchar2` on Oracle, the `utf8mb4` character set on MySQL) or single-byte text (`false`: `varchar`); left out,
    the dialect's default applies (Unicode on SQL Server). PostgreSQL and SQLite keep every text in the database's encoding.
    `fixedLength` on a string or binary column asks for a fixed-length type (`char(n)`, `nchar(n)`, `binary(n)`). Both pick
    a variant entry of the type map (`string:unicode`, `string:ansi`, `string:fixed`, `string:fixed:ansi`, `text:ansi`,
    `binary:fixed`, ...), so `typeMaps` can change them like any other entry. Either one on a type that has no such facet
    (an integer, a decimal) is MQ4057 (error).
  - **Range columns.** A column's `type` may be `range` or `multirange`, with a `subtype` for its bounds: `int32`,
    `int64`, `decimal`, `date`, `datetime` or `datetimeoffset`, PostgreSQL's built-in `int4range`, `int8range`,
    `numrange`, `daterange`, `tsrange` and `tstzrange` and their multiranges (PostgreSQL 14 and later). Only a column
    takes them; an attribute does not. A range column without a subtype, or a subtype on another column, is MQ4057
    (error), and a length, precision or scale on one is MQ3013 (error). A default or a seed cell is the range's text
    form, a string such as `"[1,10)"`. Elsewhere a range column is stored as the dialect's text type (MQ4056, warning),
    unless `typeMaps.<dialect>` maps `range:<subtype>` or the column names its `nativeType`. Custom range types stay
    database types (`typeKind: range`). A migration converts between range types with the cast PostgreSQL has (a range
    of another subtype through its text form, a range to the multirange of its subtype directly); where none exists (a
    multirange to a range) it writes a TODO and no ALTER. Moving a column between `string` with `nativeType: tstzrange`
    and a range keyword of the same native type changes nothing in the database, and the migration writes nothing for
    it.
  - **Literal defaults.** A column's `default` is a literal, checked against the column (MQ3019, error): a string
    column's against its length, its own or else the conventions' `defaultStringLength` (255 unless set, per database
    too); a number, boolean, date, UUID or binary value against its type, as an attribute's default is. A `json` column
    takes a string of JSON text (`"{\"days\": 30}"`): an object, array, number or boolean has no literal the DDL can
    write for it, so write it as a string or use `defaultSql`. A column with a `nativeType` (a native type or a
    database type) is checked only for the kind of value its type keyword implies (a string, a number, true or false),
    since the native type, not the facets, sets the storage. A range column's default is a string. `defaultSql`, SQL
    text per dialect, is not checked.
  - **Typed columns.** A column's `typeRef` names the enum, reference type or scalar type whose values it holds, by id:
    `"type": "string", "typeRef": { "ref": "<InvoiceState's id>" }, "length": 1`. `type` stays the column's own keyword.
    A facet the column leaves out comes from its type (the code's length, a scalar type's length, precision and scale,
    the longest stored code of an enum in a text column), and a scalar type's native type applies when the column's type
    is the scalar's base. A column typed by a reference type holds its codes as an attribute of that type does: sql-ddl
    gives it a foreign key into the bound table or the lookup table, or a CHECK, and `reference_type_usage` lists it.
    When the table declares a foreign key from the column, that key is the check: it must point at the code column of
    the type's bound table in that database. Values written into the column (table seed cells, its literal default,
    binding constants) are codes of the type, or an enum's stored values. Designed, imported, extra and contributed
    columns take `typeRef`; a projected column has its attribute's type, and a column a reference type's binding fills
    says nothing more. Templates read it as `column.type_ref`; `type_of` still maps the physical type. The [reference
    data guide](guides/reference-data-and-seeds.md#11-type-a-column-of-your-own-table) walks through it.
  - **Sensitive columns.** A column's `sensitive` (`pii` or `secret`) says it holds personal data or a secret, as an
    attribute's does. Say it once, on either side, and the other side shows it: a column without its own value shows the
    strongest one (`secret` over `pii`) of the attributes bindings map to it, or a projected column its attribute's; an
    attribute without its own shows the strongest of the columns its entity's bindings map it to. An overlay entry
    states it for its projected column. A column's own `secret` makes a literal default that looks like a credential
    MQ3017, as on an attribute.
  - **Named defaults.** `defaultName` names a column's default constraint where the dialect names defaults (SQL Server);
    without it the DDL names it `df_<table>_<column>`. Migrations rename it with the column, the table or the name, and drop
    and add it on its own when only the default changes.
  - **Identity options.** A column with `generated: identity` may say `identity`: its first value (`seed`), its step
    (`increment`) and whether the database always generates the value and refuses one an insert gives (`always`, GENERATED
    ALWAYS, rather than BY DEFAULT). PostgreSQL and Oracle write all three, SQL Server writes `IDENTITY(seed, increment)`
    (its identity always generates), MySQL writes the seed as the table's `AUTO_INCREMENT` and leaves out the increment and
    `always`, SQLite leaves out all three (MQ4056 for what is left out); a column that becomes an identity in a migration
    starts after the largest value it holds.
  - **Keys and constraints.** A primary key has a name and, on SQL Server, `clustered`; unique constraints, checks and
    foreign keys have names; a foreign key has `onDelete` and `onUpdate` (`no-action`, `restrict`, `cascade`, `set-null`,
    `set-default`) and `deferrable` (`not-deferrable`, `initially-immediate`, `initially-deferred`: checked at the end of
    each statement, or at commit). With `set-null` or `set-default` on delete, `onDeleteColumns` names the columns of the
    key the action sets, and the others keep their values (PostgreSQL 15 and later: `ON DELETE SET NULL (folder_id)` on a
    key `(tenant_id, folder_id)` clears the folder and keeps the tenant); empty sets every column. A column that is not
    the key's, or the list on another action, is MQ4060 (error). A set-null that would null a column that is not nullable
    (or a set-default on one that is not nullable and has no default) is MQ4061 (warning): the DDL is accepted, but deleting a
    referenced row fails; list only the nullable columns, make the column nullable, or pick another action. A check's `column` makes it a column check: the column it constrains. A unique
    constraint's `nullsNotDistinct` makes two rows with nulls in its columns conflict (PostgreSQL 15 and later).
  - **Temporal keys** (PostgreSQL 18 and later). A primary or unique key's `withoutOverlaps` makes its last column a period
    (a range column, such as `tstzrange`): rows may repeat the other columns' values as long as their periods do not overlap
    (`PRIMARY KEY (room_id, valid WITHOUT OVERLAPS)`, one rate per room at a time). A foreign key's `period` makes its last
    column pair a period that the referenced rows' periods must cover (`FOREIGN KEY (room_id, PERIOD stay) REFERENCES
    room_rates (room_id, PERIOD valid)`). MQ4065 (error) says what PostgreSQL refuses: a temporal key of one column, a
    period key with an action other than no action, a period key to a key that is not temporal, and a key without a period
    to one that is. The other columns of a temporal key need the btree_gist extension (a SQL object, below).
  - **Exclusion constraints** (`exclusions`, PostgreSQL): no two rows may match on every element, each a column or an
    expression with its operator (`EXCLUDE USING gist (room_id WITH =, during WITH &&)`: no overlapping reservations of a
    room), with an optional `method` (gist, the default; spgist, btree, hash), operator class, `where` predicate and
    `deferrable`. Use one where a temporal key cannot say the rule: a predicate (`WHERE (NOT cancelled)`), an operator other
    than `=` and `&&`, or no key at all.
  - **Indexes** list their columns with a sort order, and may be `unique`, have `include` columns, a partial `where`
    predicate and a `method` (`btree`, `hash`, `gin`, `gist`, `spgist`, `brin`, `hnsw`, `ivfflat`, `clustered`; hnsw and
    ivfflat are the vector extension's). An index column may be an `expression` instead of a column, per dialect like a
    check (`{ "*": "lower(email)" }`: a functional index), may name an `operatorClass` (PostgreSQL: `gin_trgm_ops` for a
    trigram index, `vector_cosine_ops` for a vector index, written as it is, so it may carry its parameters), may say
    where its nulls sort (`nulls`: `first` or `last`, PostgreSQL's `NULLS FIRST` or `NULLS LAST`; unset keeps the
    direction's default, last ascending and first descending, and a value equal to it changes nothing), and may index
    only the first characters of its column (`length`, MySQL, which needs one for a text or blob column). An index's
    `storage` holds its parameters per dialect (PostgreSQL and SQL Server `WITH (...)`: `fillfactor`, `pages_per_range`
    for brin, `m` and `ef_construction` for hnsw, `lists` for ivfflat).
  - **Storage parameters.** A table's `storage` holds its parameters per dialect, written only in the DDL of a database of
    that dialect: `{ "postgresql": { "fillfactor": 90, "autovacuum_vacuum_scale_factor": 0.03 } }` is PostgreSQL's
    `WITH (...)`; SQL Server's `WITH (DATA_COMPRESSION = PAGE)`, MySQL's table options and Oracle's physical attributes are
    written the same way. A number or boolean is written as the dialect writes it, a string as SQL as it is. A stereotype
    that applies to tables can hold a `storage` profile (a high-churn stereotype with aggressive autovacuum; Settings ›
    Stereotypes, **Table storage it gives**, one dialect at a time): every table
    that carries it takes those parameters, its own `storage` overriding one, a later stereotype an earlier one. A PostgreSQL
    parameter the table or index method does not take is MQ4064 (warning, most likely a typo; PostgreSQL would refuse the
    table), and so is a stereotype with storage that does not apply to tables. A migration sets and resets what changed
    (`ALTER TABLE ... SET (...)`, `RESET (...)`, and `ALTER INDEX` without rebuilding the index).
  - **Partitioning** (PostgreSQL). `partitionBy` (a `strategy`, range, list or hash, and its `columns`) makes the table
    partitioned, and `partitions` lists the partitions created with it: each a `name` and the `bounds` that follow
    `FOR VALUES` (`FROM ('2026-01-01') TO ('2026-02-01')`, `IN ('eu')`, `WITH (MODULUS 4, REMAINDER 0)`), or `default`. A
    partitioned table takes no storage parameters, so its `storage` is written on each partition. Every primary or unique
    key includes the partition columns (MQ4066, error, with a second default partition, a default partition of a hash
    partitioned table and partitions without partitionBy). A migration creates, drops and renames partitions, and detaches
    and attaches one again when its bounds change; it cannot partition an existing table, and says so in a TODO. Partitions
    made as time goes by (a month at a time) are an operational job: set up pg_partman, or your own job, with a SQL object.
  - **Views** (their own files) have a body per dialect and their `columns`; `columnList` writes the column names in
    `CREATE VIEW`, `withCheckOption` makes writes through the view satisfy its `WHERE`, and `materialized` stores its rows
    (PostgreSQL and Oracle; refreshing them is up to the application). `securityInvoker` makes the view read its tables
    with the caller's rights, so row-level security and grants apply to whoever queries it (PostgreSQL 15 and later), and
    `securityBarrier` runs its filter before any function of the query that is not leakproof. `dependsOn` names what must
    exist before the view; the views its body names are found without it, so views are created after the views they read
    and dropped before them.
  - **Routines** (their own files) have a `volatility` (PostgreSQL: `volatile`, the default; `stable`, which reads but does
    not write; `immutable`, which `deterministic` implies) and `settings`, configuration parameters set while the routine
    runs (`SET search_path = app, pg_temp`). A security-definer routine on PostgreSQL that does not set `search_path` is
    MQ4063 (warning): a caller's schema could shadow what it names. A deterministic function that is not immutable, or a
    volatility on a procedure, is MQ4062 (error).
  - **Grants and roles** are not part of the model: roles differ from one environment to the next, and grants are written
    as SQL objects (`objectKind` grant, `dependsOn` the objects they grant on). A migration that drops and creates a view or
    routine again runs the SQL objects that depend on it again, so the grants come back (a run-once one gets a TODO
    instead); one statement per schema, `ALTER DEFAULT PRIVILEGES`, in a SQL object, grants what is created later too.
  - **What a foreign key references** is the referenced table's primary key or one of its unique keys (a unique
    constraint, or a unique index without a `where` filter; Oracle takes a constraint only), its columns in any order, each
    named once. On MySQL (and MariaDB) the referenced table also needs a primary key, unique constraint or index whose first
    columns are the referenced ones in the order the foreign key names them. Anything else is MQ4059 (error), as the
    database would refuse it.
  - **Name rules.** Two indexes or constraints that would take one name where the database keeps one namespace for
    them is MQ4068 (error), found while the tables are resolved, on given and conventional names alike: two indexes on
    the same columns (a btree and a gin) both get `ix_<table>_<columns>` unless one is named, and an index named like a
    unique key or a table collides on PostgreSQL. Names compare as the database keeps them: ignoring case, and cut to
    the identifier limit first (PostgreSQL keeps the first 63 bytes of a longer name and only raises a notice, so two
    long names that start alike collide even with MQ4001 turned off). The limit counts UTF-8 bytes on PostgreSQL and
    Oracle and characters elsewhere, and MQ4001 counts the same way. The namespaces per dialect:

    | Dialect | Shared with the schema's objects | Unique per schema on their own | Unique per table |
    | --- | --- | --- | --- |
    | PostgreSQL | indexes, and primary, unique and exclusion keys (each creates an index), with tables, views and sequences | | primary, unique, foreign, check and exclusion constraints |
    | SQL Server | primary, unique, foreign, check and default constraints, with tables, views, sequences and routines | | indexes, with the primary and unique keys' indexes |
    | MySQL | | foreign keys; checks (two namespaces) | indexes and unique keys (the primary key is always PRIMARY) |
    | Oracle | | indexes, with the keys' indexes; all constraints (two namespaces, so a key and its index share a name) | |
    | SQLite | indexes, with tables and views | | |

    The finding sits on the later of the two, in published order (schema, then table name; within a table the primary
    key, unique keys, foreign keys, checks, exclusions, default constraints and indexes), and names both. SQL objects
    are text the model does not read, so their names join no namespace; MQ4002 checks tables, views and sequences
    against each other.
  - A feature the database's dialect does not have is MQ4056 (warning) and the DDL leaves it out: a deferrable foreign key on
    SQL Server or MySQL, `ON UPDATE` or a `restrict` or `set-default` action on Oracle, `set-default` on MySQL, include
    columns outside PostgreSQL and SQL Server, a partial index on MySQL or Oracle, an index method the dialect lacks, an
    index expression on SQL Server (the index is left out), a key prefix length outside MySQL, a MySQL index on a text column
    without one (the index is left out), `nullsNotDistinct` outside PostgreSQL, a stored computed column on Oracle, identity
    options (any on SQLite, the increment or `always` on MySQL), a clustered primary key outside SQL Server, a foreign key's
    `onDeleteColumns` outside PostgreSQL (the action then sets every column of the key), a temporal key, an exclusion
    constraint, partitioning, an index operator class, index column `nulls` or a view's security options outside
    PostgreSQL, index storage on MySQL, SQLite and Oracle, a routine's volatility or settings on SQL Server, a
    materialized view outside PostgreSQL and Oracle, `withCheckOption` on SQLite or a materialized view, a sequence on
    SQLite or MySQL. A `range` or `multirange` column outside PostgreSQL is MQ4056 too and is stored as the dialect's
    text type. Migration facts outside PostgreSQL are MQ4056 too: a fact's migration keeps TODO comments where its steps
    would go (a rekey writes no DDL on any dialect) and no ledger rows are written (see "Migration facts"). So is a
    run-once SQL object outside PostgreSQL, which every script leaves out.

  The sql-ddl pack writes all of it for PostgreSQL, SQL Server, MySQL and MariaDB, SQLite and Oracle, and migrates every
  change: its README has the coverage table per dialect, and how each dialect's limits are handled (SQLite tables are rebuilt
  when they cannot be altered, for instance). A migration says what it cannot decide: a "Data loss" line before every
  dropped column or table, and a TODO with a query that counts the rows at risk before a column gets shorter, holds fewer
  digits (a larger scale at the same precision included), or switches from Unicode to single-byte text. It also does what a
  database would otherwise refuse: on PostgreSQL, a view dropped or given a new body takes the views that read it with it
  (dropped first, created again after, unchanged), and a unique key or unique index that foreign keys rely on is recreated
  with those foreign keys dropped before and added back after. Run each migration so it stops at its first error (a client
  that carries on reaches the `COMMIT` with part of it applied): `psql -v ON_ERROR_STOP=1`, `sqlcmd -b`, `sqlite3 -bail`
  (or the pack's `sqliteBail` parameter, which writes `.bail on` at the top of SQLite migrations for the sqlite3 shell); a
  SQLite rebuild's foreign key check and SQL Server's renames and descriptions roll the migration back on their own. Its
  `idempotent` parameter writes scripts that can run again
  (`CREATE TABLE IF NOT EXISTS`, `DROP ... IF EXISTS` and the dialect's guards, renames and column changes included on
  PostgreSQL, SQL Server and MySQL). Truncating a table is data, not schema, and
  reading a live database's tables is an importer's job: the pack does neither.

  **Schemas.** A PostgreSQL or SQL Server database can hold several schemas (namespaces such as `sales` or `ops`).

  - **New schema…** on a database (its context menu, or the New menu while a database is selected) adds one. The
    database inspector's **Schemas** section lists them with the **Default schema** marked, and renames, removes or
    makes one the default. A rename changes nothing else: tables, views, sequences and mappings point at the schema,
    not at its name. Each of these (a new schema, a rename, a removal with what moves, a new default) is one undo step.
  - **Where a table goes.** A table file says its own schema. A table not stored as a file yet goes to the schema its
    mapping element names (`schema`), else to the schema picked for its domain (or the nearest parent domain) in the
    database's convention list (an older project's; see "Tables laid out by convention" under Storage), else to the default
    schema. When a database has more than one schema, New table and **Create tables…** offer the schema, and the
    Databases explorer groups the tables under one row per schema.
  - **Removing a schema** shows what still lives in it and asks for the schema to move it to; the default schema can
    be removed only by making another one the default at the same time. A schema named by a convention entry or a
    mapping but not declared in the database is MQ4014; a refused schema change is MQ4015.
  - Moving a table to another schema keeps its rows: sql-ddl's migration moves it (`ALTER TABLE ... SET SCHEMA` on
    PostgreSQL, `ALTER SCHEMA ... TRANSFER` on SQL Server, `RENAME TABLE` on MySQL). Renaming a schema renames it on
    PostgreSQL; on SQL Server and MySQL the migration creates the new schema, moves what is in it and drops the old one
    (on MySQL, where dropping a schema drops what is left in it, that last step is a note). Adding and removing a schema
    creates and drops it.

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
  - **Where a SQL object runs.** `environments` lists the environments it runs in (free names such as `dev` or `test`;
    empty, the default, means every environment). An object without environments runs in the schema script, the
    migrations and its object script, as before. An object with environments runs only in the sql-ddl pack's
    `objects.<environment>.sql`, one script per name in the pack's `environments` parameter (the one that writes
    `seed.<environment>.sql`), run on every deploy after the schema script or the migrations and before the seed
    scripts. That script can run again, so every statement in it must be safe to run again: `CREATE OR REPLACE`, `IF NOT
    EXISTS`, or `DROP ... IF EXISTS` first; nothing parses the SQL to check. A changed environment object takes effect
    at the next run of its script; a removed one stays in the database until you drop it.
  - **Run once.** `apply` is `converge` (the default: the statements run wherever the scripts run the object, again when
    it changes) or `once`: the statements run one time per database, inside a guard that records the object in the
    migration ledger (see "The migration ledger"), so every script that holds it can run it and only the first does. A
    changed run-once object does not run again where it ran, as a seed row applied once is never updated. An object
    switched from `converge` to `once` has no ledger row yet, so it runs one more time in every database, also where it
    exists already: drop it by hand first, or make its statements safe to run again. Statements that cannot run inside
    a function (transaction control, `CREATE INDEX CONCURRENTLY`) cannot run once. Run-once needs
    the ledger, so it is PostgreSQL only: elsewhere the object is left out of every script (MQ4056, warning). A `rekey`
    migration fact may list a SQL object whose id changed (`kind` `sql-object`), so its ledger row follows the new id
    and the object does not run again.
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
    by hand. A changed object with environments runs only in its environment scripts, and its TODO names them; a changed
    run-once object runs inside its guard, so it does not run again where the ledger records it, and a comment says so;
    an object switched to run once gets a TODO, since it runs one more time.
  - **Problems.** A routine or SQL object without a body for the database's dialect (and no `*` one), or a database type
    with neither its structured form nor a definition for the dialect, is MQ4017 (warning): nothing is created there. A
    parameter, result column or composite field whose type is neither a built-in type nor a database type of the same
    database is MQ4018 (error). A column's native type or a `dependsOn` entry that names an object of another database
    is MQ4019 (error). Views, routines, database types and SQL objects that depend on each other in a circle are MQ4020
    (error): there is no order to create them in. Two routines, two database types or two SQL objects with one name in a
    schema are MQ3001. A view, routine or SQL object that depends on a SQL object that does not run in every environment
    it runs in is MQ4069 (error): the dependency's environments must cover the dependent's, and empty means every
    environment (a view or routine runs everywhere). A SQL object with environments and phase `before` is MQ4070
    (warning): its environment scripts run after the schema script and the migrations, so the phase orders it only
    within its script.

  **Queries.** Tables, views and routines are the physical model: they are what the database holds, and they generate the
  repository layer. A **query** reads the tables and views of a database, with joins, filters, grouping, ordering, paging
  and collections; its rows have the shape of its own select list, and a pack turns it into the repository method that
  runs it. A query is data, a tree of JSON objects an agent reads and writes and a pack walks per dialect, never SQL
  text; an opaque text per dialect is allowed for one expression at a time. Entities are the shapes the services work
  with, and they stay separate: a table needs no entity, an entity needs no table, and an entity that should read
  through a query (several tables into one entity) binds to it from its own Storage tab (see "A read-only entity over a
  query").

  A query is one file per query in `model/databases/<db>/queries/<name>.json` (kind `query`). It has a name, a
  description, stereotypes, tags, a category, custom properties and generation hints, like a view, but no schema: a
  query creates nothing in the database. The billing sample's `InvoicesByCustomer`, a little shortened (an older query
  that names a result entity; see "The result shape" below):

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

  - **The result shape.** Without `entity` (every query the editor makes), the select list is the row: each field has a
    `name`, and a `type` (a built-in type) and `nullable` when the expression does not say them, and a pack turns the
    fields into a record. An older query may name an `entity` (as the sample above does), which the engine still reads:
    each row is then that entity. Every select field names the `attribute` it fills (its name defaults to the
    attribute's), or a `name` of its own for a value the entity does not hold. A field may also fill a member of a value
    object attribute, written `<attribute id>.<member id>` (the members of one attribute build the value together; its
    name defaults to `totalAmount` for `total.amount`), or the foreign key of a to-one navigation, written as the relation
    end the navigation leads to (the invoice's customer end fills `CustomerId`; its name defaults to the foreign key
    column's, `customerId`). A field that fills an attribute takes the attribute's type in generated code: a value of
    unknown type (an unknown function, an `sql` text) takes it, a number of another number type is converted, and a
    value that cannot convert (a text into a number) is MQ4033. A required attribute of the entity that no field fills is
    MQ4026 (a warning: the rows leave it at its default); an attribute of a value object type spans several columns and
    is not asked for. Such rows are read-only projections: an entity row holds what the query selects and leaves the
    rest at its defaults, so it is not a row to save back through a repository. The result entity, and a collection's
    element entity, is a concrete one (an abstract one is MQ4041).
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
    inspector sums it up (how many fields each row has, and how many sources, parameters and collections it has) with **Show the SQL**, which opens the editor on its SQL tab. Nothing in the editor is SQL text
    but the SQL per dialect form: every cell writes the query's data, and every change is one save and one step Undo takes
    back. The tabs:
    - **General**: the name, marks and description. The editor says nothing about entities: an entity binds to the
      query from its own Storage tab. An older query that still names a result entity (its own, or a collection's
      elements') keeps working and shows one line, "An older result entity is set; bind the entity to this query on its
      Storage tab instead", with **Remove**: each field that filled an attribute becomes a field of its own name, its
      expression kept (the attribute's name, numbered when another field has it), and each such collection takes the name
      of what it filled. Nothing is lost; one undo step.
    - **Sources**: the **From** source and its alias, then the joins, each with its kind (inner, left, right, full or
      cross), its source, its alias and its **on** condition. **Add join** adds a source not read yet, one that shares a
      foreign key with a source already read first. **Join by foreign key** fills the condition from the foreign key
      between the joined source and one before it (when several link them, it lists them). Renaming an alias renames every
      column reference through it, the collections' correlations included.
    - **Select**: the fields are the row: name, expression, **Declared type** and **Nulls** (inferred unless you say),
      moved up and down; **Add field** adds one. An older query with a result entity shows one row per member of that
      shape instead (a value object's members one by one, `total.amount`, and each to-one relationship's foreign key), its
      expression or **Select <member>**, with **Fill from columns by name** (the same-named column of the source you pick,
      ignoring case and underscores) and fields it does not have marked not in the shape, with **Remove**.
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
    - **Collections**: one card per collection: its name (for an older query with a result entity, what it **Fills**),
      then the nested query's Sources, Select, Filter and Group and order, the same as the query's, with the query's
      aliases in scope: an equality at the top of its filter between one of its columns and
      one of the query's ties its rows to the parent row (`l.invoice_id = i.id`), and the card says by what. **Add
      collection** starts one on the first table or view, its filter the tie from a foreign key to the query's source
      when there is one.
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

  **A table's own rows (table seeds).** A table file's editor has a **Data** tab: the same grid over the table's columns
  (less those the database fills: identity, generated and computed columns), for the rows the database itself needs, such
  as catalogs or allowed values, with no entity in between. Cells are column values. New seed creates one, as for an
  entity. A table seed belongs to its table and goes with it. When reference types keep their rows in the table (Stored in
  a table, on the type's Storage tab), a line above the grid names them, each a link to the type: their rows come first,
  and the table's own rows may not repeat their codes.

  **How rows are applied.** Under every seed grid, each seed has its settings:
  - **Environments**: the environments its rows belong to (`dev, test`); empty means every environment.
  - **Apply**: **Insert once** (the default: a row whose key is missing is inserted, a row that is there is left alone, so
    edits made in a database survive) or **Keep as the model has them** (the rows are also updated to the model's values on
    the next run).
  - **Delete rows the seed does not hold** (with Keep as the model has them): the table holds exactly the seed's rows.
  - **Row key** (a table seed): the primary key, or a unique constraint, that finds a row again on the next run.
  - **Rows in a CSV file**: the rows move to `<seed>.csv` beside the seed file (a header of `@id` and the column ids, one
    row per line), which suits seeds of thousands of rows and spreadsheet editing; the grid, validation and generation read
    it the same way, and a save writes it back. Untick to bring the rows back into the seed file.

  An entity's seed reaches the table its binding writes: the field map gives the columns and the binding's constants are
  added to every row (a read-only binding writes none). Validation checks a table seed's cells against their columns (type,
  length, precision, NOT NULL), its row keys, and its foreign keys against the referenced table's seeds (MQ7107 to MQ7116),
  and warns when a table receives rows both from its own seeds and from a bound entity's (MQ7113).

  **Natural keys, stable ids and scripted rows.** A table seed may list a foreign key of its table in `columns`, in
  place of the key's own columns, and give in each cell the referenced row's natural key (an email, an invoice number;
  an array for a key of several columns); the engine fills the columns from that row (MQ7111 when no row has the key,
  MQ7126 for a misplaced entry). A `uuid` column with `stableId` gets a value derived from the table and the row key for
  every row the table receives (RFC 9562 version 5, so any tool computes the same; MQ7124, MQ7125). A seed may take its
  rows from a script, `rowsFrom: { "script": "seeds/<name>.js" }` (MQ7127, MQ7128). The grids have no controls for the
  three yet: set them in the seed's or the column's JSON form. The [reference data
  guide](guides/reference-data-and-seeds.md#12-name-a-referenced-row-by-its-natural-key) walks through them, and
  `maquettiste seed preview` shows one seed's rows as a database receives them.

  **Where the rows go.** `sql-ddl`'s `seed.sql` writes the rows of every environment (inserts that skip existing keys, or
  upserts per dialect for Keep as the model has them, and the deletes), and `seed.<environment>.sql` an environment's own
  rows for each environment its `environments` parameter names. The `seed-data` pack writes them as files for a loader:
  per database a `manifest.json` and one CSV per table, or one `seed-data.json` (`format`), in database terms and foreign
  key order; `csharp-dapper` with `seedLoader` generates a `SeedDataLoader` that applies them. See `packs/seed-data/README.md`.
- **Generate**: Plan renders every template unit and shows what would change, grouped by unit with the reason each
  renders (see "How the plan explains itself" below); pick a file to see its diff; Apply writes the plan. Generation runs as a job and reports progress; the run history stays in the panel. Clear, beside the Run history heading, deletes the finished runs and their stored plans (not while a run is queued or running), so a plan you have not applied must be made again. The Generate
  explorer and the pack editor (below) show and change what each pack does.
- **Settings**: the project's settings, one tab each; the tab you left is the one Settings opens on next time.
  - **General**: the project's name and branding (below).
  - **Tags**, **Categories**, **Stereotypes**: the global vocabularies (a domain's own are on its editor's tabs).
  - **Conventions**: the naming conventions, for the project (every database) or for one database picked at the top;
    they name what **Create a table for this entity…** and **Create tables…** write, the name the foreign key dialog
    proposes, and the tables an older project lays out by convention; they never decide which entities
    a database stores.
    **comments** says where the database comments of tables and columns come from when none is written: with
    **descriptions** (the default) a column takes its own description, else its attribute's, and a table its own
    description, else its entity's (a junction table its relationship's); with **none** only explicit comments are
    written. The sql-ddl pack writes them (`COMMENT ON` for PostgreSQL and Oracle, a description property for SQL
    Server, `COMMENT` in the definitions for MySQL, a `--` line for SQLite) while its `comments` parameter is on.
  - **Locales**: the content locales (see "Translating the model in the editor").
  - **Validation**: the severity of each built-in rule (see "Settings › Validation" below).
  - **Type maps, outputs, formatters**: the output allowlist (`outputs.allow`), the type maps, the formatters and the
    packs, shown read-only; edit `maquettiste.json` to change them. A type map entry changes the native type of every
    value of a built-in type in a dialect (`typeMaps.postgresql.binary`); to change it for one kind of value only, give a
    custom type its own native types instead, which win over the type map.
  - **Explorer**: your preferences in this browser (Highlight related elements), your saved scopes and the team scopes
    from `maquettiste.json`.
- **Settings › General** names and brands the project and holds its own properties.
  - **Project name**: the name in the top bar and the browser tab, `name` in `maquettiste.json` (the field
    `maquettiste init --name` writes). The top bar and the tab follow as you type; Save writes it.
  - **Properties**: key to text values every pack's templates read as `project.properties.<key>` (a base namespace, a
    company name); see "Project properties" under the pack reference. Add property, then Save; a key that a template
    could not name (a dot, a leading digit) or a key used twice is refused before the save.
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
  References and vocabularies, Model structure, Databases and mappings, Migrations, Extensions and script rules, Packs
  and generation, Reference types, Seeds, Localization, Branding, Processes, Actors and gates, Lifecycles, Scenarios,
  Process import and export, Process expressions and simulation), with its id, description and default severity. Each row's
  picker is **Default** (the engine's severity), `error`, `warning`, `info` or `off`; the model file rules (MQ1xxx) have
  no `off`. The filter box matches an id or a word of the description; a changed row shows a dot until you save, the
  count says how many rules are overridden, and **Reset all** sets every picker back to Default. Save writes only the
  overrides to `validation.rules` in `maquettiste.json` (Default removes the rule's entry) and the Problems panel
  revalidates; Discard drops the unsaved changes. The rows take the keyboard: the arrow keys move between rows, Enter
  moves into the picker, whose arrows change the value. The same catalog is `GET /api/validation/rules`; the MCP tool
  `list_validation_rules` summarizes it by family and pages its entries.

## Binding entities to tables, views and queries

A database knows nothing about entities: it holds tables, views, routines, types and queries, each with its own columns. An
entity says how it reads from and writes to a database in its own file, with a **binding** per database (`bindings` in the
entity's JSON). A binding is written down whole: where the entity reads, the columns that carry a fixed value, which column each
field reads, what happens to the columns no field maps, where it writes back and how it deletes. An entity with a binding to a
database is never projected into it: no table follows the entity there, whatever the database's convention or a mapping
element says (such a mapping element is reported as MQ4054 and ignored). Models without bindings keep working as before.

In the editor, all of this is done from the entity side (below, [Storage: binding an entity](#storage-binding-an-entity)); the
database side shows tables, views and queries and never the entities stored in them. The file shape, the rules and the command
line follow.

### The file shape

```json
{
  "kind": "entity",
  "id": "01K6BND0000000000000000010",
  "name": "InvoiceNote",
  "key": { "attributes": ["01K6BND0000000000000000011"], "strategy": "uuid-v7" },
  "attributes": [
    { "id": "01K6BND0000000000000000011", "name": "id", "type": "uuid", "required": true },
    { "id": "01K6BND0000000000000000012", "name": "invoiceId", "type": "uuid", "required": true },
    { "id": "01K6BND0000000000000000013", "name": "body", "type": "text", "required": true },
    { "id": "01K6BND0000000000000000014", "name": "createdAt", "type": "datetimeoffset", "readOnly": true }
  ],
  "bindings": [
    {
      "id": "01K6BND0000000000000000015",
      "database": "01J92P0V1QRN2181XM2ZWE02W4",
      "source": "01K6BND0000000000000000001",
      "constants": [{ "column": "01K6BND0000000000000000003", "value": "invoice" }],
      "fields": [
        { "attribute": "01K6BND0000000000000000011", "column": "01K6BND0000000000000000002" },
        { "attribute": "01K6BND0000000000000000012", "column": "01K6BND0000000000000000004" },
        { "attribute": "01K6BND0000000000000000013", "column": "01K6BND0000000000000000005" },
        { "attribute": "01K6BND0000000000000000014", "column": "01K6BND0000000000000000006" }
      ],
      "columns": [{ "column": "01K6BND0000000000000000006", "status": "database" }]
    }
  ]
}
```

- `database`: the database. One binding per database (two are MQ4050).
- `source`: what the entity reads, the id of a table, a view or a query of that database (a table a projection still
  synthesizes may be named by its key, `<entity id>@<database id>`).
- `constants`: columns with a fixed value, as many as you need (an entity type, a tenant): every read filters on them and
  every insert sets them. A value is a string, a number or true/false; leaving it out means NULL.
- `fields`: each field names an attribute of the entity (inherited and stereotype attributes included), a member of a value
  object attribute as `attributeId.memberId`, or the relation end a to-one navigation leads to (for its key), and the source
  column it reads, by the column's id or key or by its name.
- `columns`: the source columns no field maps, each with why: `ignored` (the entity does not use it), `database` (the
  database fills it, by a default or a trigger, and it is never written) or `computed`. A field's column may be listed too:
  `database` or `computed` then means the field is read but never written, as `created_at` above.
- `write`: `{ "table": <table id> }` or `"none"`. Without it the entity writes its source when that is a table and nowhere
  when it is a view or a query. A write table other than the source matches each field's column by key, then by name.
- `delete`: `"key"` (the default when the entity writes), `{ "soft": { "column": <column>, "value": <value> } }` (sets the
  column instead; reads then leave out the rows it marked) or `"none"`.
- `provenance`: `{ "columns": { "<column id>": "<name part>:<type part>" } }`, what each column of a table file looked
  like when an operation last followed it (see [Provenance and drift](#provenance-and-drift)). The engine writes it; you
  never need to, and removing it stops the drift report for that binding.
- `description`, `tags` and `properties` annotate the binding like any other part of the model.

Every column of the source must be accounted for: a field, a constant, a `columns` entry, or a column that fills itself (an
identity, a computed column, a column with a default). Any other column is warning MQ4047, so nothing a table holds goes
unnoticed.

### One table, several entities

The billing sample keeps notes on invoices and on customers in one table, `notes (id, entity_type, entity_id, body,
created_at)`. `InvoiceNote` above and `CustomerNote` both bind to it; their constants (`entity_type = 'invoice'` and
`entity_type = 'customer'`) tell their rows apart: reading invoice notes never returns a customer note, inserting one sets the
type, and updates and deletes find their row by key and type. The editor shows the two bindings on each entity's Storage tab;
for tools, the API's and the agent server's database view shows `boundBy` on each table, view and query, with each binding's
constants.

### A read-only entity over a query

An entity can read a query of the database: its fields name the query's columns (its select list's names). The billing
sample's `RevenueMonth` reads the `RevenueByMonth` query (month, invoice count, revenue); a query source does not write, so
the generated repository has `GetAsync` and `ListAsync` (taking the query's parameters first) and no insert, update or delete.

### What the statements look like

The engine renders each binding's five statements for any dialect: `select`, `select-by-key`, `insert`, `update` and
`delete`. Parameters are named after the fields; constants are literals; the columns the database fills are never written,
and a generated key comes back from the insert (`RETURNING`, `OUTPUT INSERTED`, or `SELECT LAST_INSERT_ID()` on MySQL):

```sh
curl -s -H "Authorization: Bearer $TOKEN" \
  "http://localhost:8080/api/model/entities/01K6BND0000000000000000010/bindings/01K6BND0000000000000000015/sql?dialect=postgresql"
# {"preview":{"select":{"sql":"SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM billing.notes t\nWHERE t.entity_type = 'invoice'", ...},
#  "insert":{"sql":"INSERT INTO billing.notes (id, entity_id, body, entity_type)\nVALUES (@id, @invoiceId, @body, 'invoice')", ...}, ...}}
```

Templates use the same text through `binding_sql binding "insert"` (and `binding_sql_parameters`), with `entity.bindings["main"]`;
the csharp-dapper pack builds a bound entity's repository from it.

### Materialize, both ways

**A table for each entity** (`materialize-tables`; in the editor, **Create a table for this entity…** and **Create
tables…**). For an entity the database projects today (the older format), `materialize-tables` writes a designed table with
exactly the shape the projection has (columns, keys, uniques, checks, indexes, foreign keys, comments) and binds the entity to
it, every column mapped. The table's overlay, when there was one, becomes the designed table and keeps its id, so queries and
keys naming it keep working; the entity's mapping element for the database is deleted; relations whose foreign key is now in the
designed table name it in their relation mapping; and the committed schema snapshot follows, so the next generation writes no
migration (the DDL is the same table). Undone and run again, it gives the table, its columns and its key sequences the ids they
had the first time (the snapshot remembers them), so the snapshot still reads it as the same table. From then on the table is
yours. An entity already bound to the database is refused (MQ4055), so running it twice is harmless.

**An entity for each table** (`materialize-entities`; in the editor, **New entities from tables…**). For designed or
imported tables (or views), `materialize-entities` writes an entity per table in the package you name: the singular of
the table name in Pascal case, one attribute per column, the key from the primary key, and a binding to the table.
Foreign keys between the picked tables, or to a table an entity is already bound to, become many-to-one relations naming
those foreign keys. A column typed by a reference type, an enum or a scalar type (its `typeRef`) gives an attribute of
that type, with no length of its own; a foreign key from such a column into the code column of the reference type's
bound table makes no relation, and a note says the attribute is typed by the type instead. A table an entity is already
bound to is refused.

**Attributes from the table's columns** (`materialize-attributes`; in the editor, on the binding card of the Storage tab,
**Add attributes for the unmapped columns**, or **New attribute** in a column's *Account for*). An entity you made yourself and
bound to a table takes an attribute per column its binding leaves unmapped, named and typed by the same rule, each mapped to its
column; a column you marked ignored keeps its listing until you name it. Its key, constants, write and delete stay as they are.
An entity with no binding to the database, a query source, or a column already mapped is refused.

**Columns for the entity's attributes** (`materialize-columns`; in the editor, in the field map, **Add columns for the
unmapped attributes**, or **New column** in an attribute's *Column*). The other way round: a bound entity whose table is a table
file gets a column per attribute its binding leaves unmapped, named and typed as your conventions would project it, each mapped to
its attribute. A column name the table already has is left for you to map; a to-one navigation's column gets no foreign key (add
it in the table editor).

```sh
maquettiste model materialize tables --database main Invoice Customer --dry-run   # what it would do
maquettiste model materialize tables --database main Invoice Customer             # do it
maquettiste model materialize entities --database main --package Billing suppliers supplier_contacts --dry-run
maquettiste model materialize attributes --database main Supplier                 # every unmapped column
maquettiste model materialize attributes --database main --column note Supplier   # just that one
maquettiste model materialize columns --database main Supplier                    # a column per unmapped attribute
```

Over the API: `GET /api/model/databases/{id}/materialize` lists the entities with no binding to the database (and whether
each is projected there) and the tables and views with the entities bound to each; `POST /api/model/databases/{id}/materialize/preview`
with `{ "op": "materialize-tables", "entities": [...] }` or `{ "op": "materialize-entities", "tables": [...], "package": "..." }`
shows the plan (creates, updates, deletes, notes, diagnostics) without writing; `POST /api/model/batch` applies it with
`{ "op": "materialize-tables", "database": "...", "entities": [...] }`. The agent tools are `get_materialize_status`,
`preview_materialize` and `apply_batch` (docs/mcp.md).

Deleting a table, view, query or database a binding names is refused while the binding names it; with **remove references**
or **delete dependents**, the binding goes and the entity stays.

### Provenance and drift

Tables you bind to keep changing: a migration adds a column, someone renames one, a type widens. The binding keeps
working, because it names columns by id, but the entity stops following the table. So a binding records its
**provenance**: for each column of its source, two short hashes, one of the name (ignoring case and separators) and one
of the type (type, subtype, `typeRef`, length, precision, scale, native type, nullable). It is never a copy of the
table, and it holds no date or author, so model files stay the same byte for byte. Only a designed or imported table has
provenance; a view, a query or a table the database makes from an entity has no column ids to record.

The engine's operations write it: `materialize-tables` and `materialize-entities` record every column of the table they
bind; `materialize-attributes`, `materialize-columns` and `map-by-name` record the columns whose fields they write
(every column when the binding has none yet); `resync-binding` records every column again. A save by hand leaves it as
it is, and the editor's Auto-map and **Map by name…** do not record it yet. A stereotype fold moves its keys with the
column ids it rewrites, so a fold reads as no drift.

**Drift** compares the table with what was recorded:

- **added**: a column not recorded that nothing in the binding names (no field, constant, listing or soft delete),
  whatever the database fills;
- **renamed** and **retyped**: a column a field maps whose name or type changed;
- **removed**: a recorded column the table no longer has.

A column that changed but that no field maps (a constant, a listed column) is not drift: nothing in the entity follows
it.

How the rules split the work: MQ4047 stays as it was, on every binding, for a column nothing accounts for, new or old.
MQ4087 is for a binding with provenance: a column a field maps that was renamed or retyped since it was recorded, one
warning per binding listing the columns. MQ4088 says the provenance does not describe the source (the binding was
pointed at another table by hand, or its source is not a table file); the provenance is then read as none. An added
column the database fills is accounted for, so no rule reports it; the drift report and a re-sync list it. A removed
column a field still maps is MQ4045. All three take the severity `validation.rules` sets; at `error` they fail
validation and stop generation, while a save that renames a column is never refused for drift.

**Re-sync** (`resync-binding`) brings each named binding into step with its table file in one batch, all or nothing:

1. an attribute whose name followed its column's old name takes the column's new name, as materialize names attributes;
   an attribute that never followed its column keeps its name, and `keepNames` keeps every name;
2. an attribute whose column's type changed keeps its reference type, enum or scalar type when the new type still fits,
   and otherwise takes the type materialize would write; its facets and `required` follow the column;
3. the attributes the binding leaves unmapped are mapped by name to free columns;
4. each added column still free gets an attribute, mapped to it;
5. the field of a removed column goes; its attribute stays;
6. the provenance is recorded again (only the named columns' entries with `columns`).

A renamed or retyped attribute keeps its id, so every link holds; renaming it changes generated names, and when the
entity is also projected into another database the dry run lists the migration facts that change would record.
Attributes from a base entity or a stereotype are never renamed or retyped; a note names them. A binding already in step
is skipped with a note, and a re-sync of bindings that are all in step is refused (MQ4089). Re-sync never deletes
attributes and never adds or changes columns: the entity follows the table, never the reverse.

**Map by name** (`map-by-name`) maps every attribute a binding leaves unmapped to a free column of the same loose name:
lowercase, with every character that is not an ASCII letter or digit dropped. An attribute matches by its name, a member
of a value object attribute as `attributeMember`, and a to-one navigation's key as the navigation's name plus `Id` (or
without `Id`). Each column matches once, in the table's column order. Fields name a table file's columns by id and a
view's by name; a query source is refused.

```sh
maquettiste binding drift                                       # bindings with something to report
maquettiste binding drift --database main Invoice --all         # also the ones in step
maquettiste binding drift --check                               # exit 2 when a listed binding drifts
maquettiste binding map --database main Supplier --dry-run      # map by name, and record provenance
maquettiste binding resync --database main Invoice --dry-run    # what a re-sync would change
maquettiste binding resync --database main Invoice --keep-names
maquettiste binding resync --database main Invoice --column due_on
```

`--column` takes a column's key or physical name and serves one entity. The drift report reads files only. Over MCP,
`get_binding_drift` answers the same report in pages; `preview_materialize` and `apply_batch` take `map-by-name` and
`resync-binding` (with `keepNames`), and so does `POST /api/model/databases/{id}/materialize/preview`. These operations
read the named entities and their tables only, never the whole model.

### Column comments as descriptions

An attribute with no description of its own shows the comment of the column its entity's binding maps it to: the text
the database gets, which is the column's own comment, or its description by the `comments` convention. With bindings to
several databases, the first by database name that has a comment gives it. The resolved model names that column in
`descriptionColumn`, and generated code documents the attribute with the text (csharp-dapper writes it as the property's
summary). Write a description on the attribute and it replaces both; the comment stays on the table. Value object
members and relation keys take none, since a member is shared by every entity that uses its value object. Materialize
writes no description, so the text stays in one place.

### The rules for bindings

| Rule | Severity | When |
| --- | --- | --- |
| MQ4044 | error | the source is not a table, view or query of the binding's database, or the write table is not a table of it |
| MQ4045 | error | a field names nothing of the entity, a column the source does not have, a listed column of no table the binding uses, or maps an attribute or a column twice |
| MQ4046 | error | a binding that writes maps no field for a key attribute |
| MQ4047 | warning | a column of the source or write table is accounted for by nothing |
| MQ4048 | error | a constant names a column the source does not have |
| MQ4049 | error | a binding that writes has a constant whose column is not in the write table |
| MQ4050 | error | two bindings to one database |
| MQ4051 | warning | a constant (or soft-delete value) does not fit its column |
| MQ4052 | error | a write table other than the source has no column for a key field |
| MQ4053 | error | a key delete from a table without a primary key, or a delete with no table to write |
| MQ4054 | info | a mapping element maps an entity the database binds: the binding wins |
| MQ4055 | error | a materialize operation is refused (something already bound, an entity in an inheritance hierarchy, a name taken) |
| MQ4087 | warning | a column a binding maps was renamed or retyped since its provenance was recorded |
| MQ4088 | warning | a binding's provenance does not describe its source: the source is not a table file, or none of the recorded columns belongs to it |
| MQ4089 | error | a `map-by-name` or `resync-binding` operation is refused: an entity with no binding to the database, a source it cannot read, `columns` for more than one entity or naming a column the source lacks, or nothing to map or re-sync |

### The rules for typed and sensitive columns

| Rule | Severity | When |
| --- | --- | --- |
| MQ4082 | error | a column's `typeRef` names no enum, reference type or scalar type, sits on an overlay entry of a projected column, or is on a column a reference type's binding fills (reported on the binding's field) |
| MQ4083 | error | a typed column's type or a facet it states cannot hold the type's values: the codes, an enum's stored values, or a scalar type's base and facets |
| MQ4084 | error | a declared foreign key from a column typed by a reference type does not point at the code column of the type's bound table, or the type has no binding in that database |
| MQ4085 | warning | a column and an attribute it stores, or that a binding maps to it, state different sensitivities (reported on the binding field or the overlay entry) |
| MQ7014 | error | a table seed cell, a column's literal default or a binding constant written into a typed column is not a code or stored value of its type |
| MQ7019 | error | a table seed cell or a column's literal default names a former code of the column's reference type (as an attribute's default, `allowedValues` entry or seed cell does); write the code of the row that replaced it |

## Storage: binding an entity

An entity is either **domain only** (it has no binding: it is not stored in any database) or bound to one or more databases,
each binding saying where it reads, which column each field reads, where it writes and how it deletes. Everything about that is
on the entity side: the entity's **Storage** tab, the Domain model's storage actions, and a relationship's **Storage** tab.

### The Storage tab

Open an entity and pick **Storage**. With no binding it says **Domain only: not stored in any database.** (no warning: that is a
choice). When a database still makes a table for it by its conventions (the older format), the note says so and points at
**Create a table for this entity…**, which makes that table explicit.

Each binding is a card, one per database:

- **Source**: the table, view or query the entity reads, grouped as Tables, Views and Queries. Changing it keeps the fields,
  constants and listed columns the new source has. A plain SQL statement as a source is not available yet: write it as a
  view or a query of the database.
- **Constants**: rows of a column and a value (several if you need them, such as an entity type and a tenant). Every read
  filters on them and every insert sets them. A value that reads as a number or true/false is stored as one; an empty value is
  NULL. **Add constant** takes the first free column.
- **Field map**: every attribute the binding can map, own, inherited, from a stereotype, each member of a value object
  attribute (`totalAmount`, `totalCurrency`) and the key of each to-one relationship (`customerId`), with a column picker.
  **Map by name…** lists the attributes without a field whose name matches a free column (case and underscores ignored, so
  `customerId` reads `customer_id`); untick any you do not want, then **Map**.
- **Columns of the source**: every column with what accounts for it: mapped by a field, a constant, ignored, filled by the
  database, computed, an identity, a column with a default, the soft-delete column, or **unaccounted** (in the warning color).
  **Ignore** accounts for an unaccounted column at once; **Account for** sets ignored, filled by the database or computed,
  or takes the listing away.
- **Write**: **The source table** (the default for a table source), **None (read-only)**, or another table. **Delete**: **By
  key**, **Soft delete** (a column and the value it is set to; reads then leave those rows out) or **None**; a read-only
  binding deletes nothing.
- **SQL**: the binding's five statements (select, select by key, insert, update, delete) for the database's dialect or another.
- Problems: the binding's findings (MQ4044 to MQ4055) show on the card as you edit. A stored attribute no field maps is
  warning MQ4058, with the names: own, inherited and stereotype attributes, each member of a value object attribute, on
  a read-only binding too. A collection, a derived attribute that is not stored and the key of a to-one relationship are
  not expected.

**Add binding…** picks a database without a binding and its source; the fields start empty, so **Map by name…** is the next
step. The trash button removes a binding (the table, view or query stays). Every gesture on the tab is one save of the entity
and one undo step.

When the entity also has an older mapping element for a database, the tab notes it ("This entity also has an older mapping
element for main; its binding wins", or that it places the entity there by convention) with **Open** and **Delete**. Mapping
elements stay readable in their JSON and in the Domain explorer's References.

### Create a table for this entity

**Create a table for this entity…** on the Storage tab, or **Create tables…** on one or several selected entities or on a
domain (its entities and its sub-domains') in the Domain model explorer, asks for the database (and the schema, when the
database has several), shows the plan (the tables it writes, the bindings, the older mapping elements it replaces, the
relationships whose foreign key it names) and applies it as one batch and one undo step. Each entity gets a table of its own
with the shape its conventions give it, every column mapped; from then on the table is yours. Entities already bound to that
database are left as they are.

### Auto-map to existing tables

**Auto-map to existing tables…** (on entities, a domain, or the Storage tab) picks a database and matches each entity to a
table by name through the naming conventions (the table case does not matter, and the plural the conventions prefer is tried
first), then its attributes to the table's columns by name. The review list shows each entity as a **match**, a **partial
match** (with the attributes no column matched) or **no table by that name**; pick a table or view for a miss, untick a row to
leave it out, then apply: each included entity gets a binding, in one batch and one undo step.

### Remove bindings

**Remove bindings…** (on entities or a domain) removes their bindings to one database, or to every database; the entities
become domain only there, and the tables, views and queries stay. One batch, one undo step.

### New entities from tables

**New entities from tables…** (the Domain model's New menu, or a domain's menu) picks a database, lists its tables and views
no entity is bound to, and the domain the new entities go to. Each picked table or view becomes an entity named after it (the
singular, in Pascal case), one attribute per column, its key from the primary key, bound to it; a foreign key between the
picked tables becomes a many-to-one relationship that names it. Preview, then apply: one batch, one undo step.

### Relationship storage

A relationship's **Storage** tab has a section per database. When both ends' entities are bound to tables there, **Foreign key**
lists the foreign keys that can realize it: the keys held by the dependent end's table (the many end of a one-to-many; of a
one-to-one, the optional end, or either end when both are alike, the pick then naming which) that reference the other end's
table. A key held the other way is not offered, and when it is the only one, the hint says which table must hold one. The
key you pick is written to the relationship's mapping in that database; the pickers wait until the relationship's mappings
are read, so a pick never makes a second mapping. A many-to-many names its **Junction table** and, for each end, the junction
table's foreign key for it. Two bound ends with nothing named is MQ4011, shown in the section. While the ends are not both
bound, the section says "Bind both ends to a table to name the foreign key"; a shape an older mapping names (Foreign key,
Junction table, Promoted to an entity) shows there read only, and no mapping of that older format is made from here.

### Tables laid out by convention (older projects)

Before bindings, a database stored entities by convention: it laid out a table for each entity of the domains it listed, and
mapping elements adjusted or added single entities. Projects made that way keep working, and the database side shows those
tables like any other (with **Store as table files** to turn them into table files; see the Databases part of [The explorers
and screens](#the-explorers-and-screens)). New work binds each entity on its Storage tab instead.

Entities are never turned into tables on their own: a database holds only what is made in it or bound to it, and **a new
database starts empty** (New database asks only its name, schemas and dialect; the Databases explorer shows "Empty: New
table…, New view… or New query… adds one here" under it). Settings › Conventions has, for older projects only, a read-only
panel **Older projects: tables laid out by convention**: one card per database whose convention still lays out tables (the
domains it lists, or every domain for a database made before 0.3.0, whose file says nothing), the entities an older mapping
element places one by one, and **Stop laying out by convention** per domain. Stopping shows how many tables that convention
lays out and offers **Store as table files, then stop** (each entity is then bound to its own table file, one undo step, then
the convention drops the domain) or **Stop without storing**. Tables it cannot store yet go with the convention (an abstract
entity's, an inheritance hierarchy's), except a junction table, which stays while both its ends are stored there. Nothing there adds a domain: new work binds entities on
their Storage tab, alone or in bulk with **Create tables…** and **Auto-map to existing tables…**. An entity that lands in no
database is reported in Problems as MQ4012 (info, only once the model has a database); a domain list that the convention
does not use (All or None) is MQ4013 (warning).

### Finding domain-only and bound entities

The Domain model explorer's **Filters** menu has **Storage**: **Domain only (no binding)** keeps the entities with no binding
to any database, **Bound to a database** the others. A bound entity's row has a **Storage** folder listing its bindings,
"main › invoices" (the database, then the table, view or query it reads), each going to its source; a domain-only entity has
none. Generation treats a domain-only entity as stored nowhere: the C# pack writes no repository for it.

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
| MQ1003, MQ1010 | **Rewrite in canonical form**: rewrites that file (an element file, a locale file or `maquettiste.json`) the way the editor writes it, as `maquettiste format` does; `maquettiste.json` also loses the retired `commit` flags |

**Rewrite in canonical form** changes no content, only the file's layout, so it adds no undo step: there is nothing to
put back. A file that does not pass its schema is left as it is (the notice says so); fix its other problems first.
Settings › Validation has the same for every file at once: **Rewrite all model files in canonical form**, with the
number of files the report flags.

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

`GET /api/validation/rules` returns the full descriptions. The MCP tool `list_validation_rules` returns a summary by
family, and full entries by family, id or text, in pages.

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
| `Endpoints/<P>Endpoints.cs` | One `POST` endpoint per event, each with a user-code region where the request and the response can be reshaped | Regions: the code inside a region survives regeneration; written only when `endpointsFolder` is set; keep that folder in version control (see the regions note in "Which files to commit") |

and, once for the model when it has a process:

| Files | What they hold |
| --- | --- |
| `Dispatch/Dispatch.g.cs` and `Dispatch/Pipeline.cs` | The dispatcher that sends every command through the behaviours to its handler; the companion orders the behaviours and holds the policy hooks (which actors a caller may act as, the transaction) |
| `Dispatch/HandlerRegistry.g.cs` | The typed list of handlers, with a registration method for a dependency-injection container; no reflection |
| `Dispatch/Behaviours.g.cs` | Validation of the payload, authorization by actor, logging (attributes marked sensitive left out), the transaction and the hand-off of transition and audit records to an outbox |
| `Runtime/Statechart.g.cs` and `Runtime/ProcessHost.cs` | The generated interpreter, with the engine's semantics; the companion holds the clock, the timer scheduler and the invoke host, in memory to start with |
| `Processes/Actors.cs` | A constant per actor with its type, and the actors each event allows |

A companion is created once and never overwritten: a guard, action or service that the model adds fails the build until
its companion code is written, which is how the pack tells you what to implement. Keep companions in an output root
you commit (the pack's `partialFolder`).

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

**Documentation.** The `process-docs` pack writes Markdown to an output root (usually one you commit): a page per process (the description, a
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

## Operations

An **operation** is one command over the model: what it acts on, whether it reads or changes data, its input, the ways
it ends, its errors and the process events it fires. The shape names no protocol, so a pack maps it to HTTP, messages, a
command line or a job, in its own language. Maquettiste implies no operation: an entity has only the operations you
model. Each operation is one file of kind `operation` under `.maquettiste/model/operations/`, in a folder named after
its subject's file when it has a subject (`operations/invoice/send.json` for `send` on Invoice); changing the subject
moves the file. The editor lists operations in an **Operations** folder under their domain (an operation without a
`package` sits in its subject's) and opens one with its General fields, where the subject, level and effect are shown
read-only, and the JSON tab, which edits the rest. Errors shared from stereotypes, file members and file returns,
paging and an HTTP endpoint template come later.

### The file shape

```json
{
  "kind": "operation",
  "id": "01K8QPN0000000000000000001",
  "name": "send",
  "subject": "01K8QPN0000000000000000010",
  "effect": "change",
  "input": [
    { "id": "01K8QPN0000000000000000002", "name": "recipient", "type": "string", "length": 254, "required": true }
  ],
  "outcomes": [
    { "id": "01K8QPN0000000000000000003", "name": "applied", "returns": { "entity": "01K8QPN0000000000000000010" } },
    {
      "id": "01K8QPN0000000000000000004",
      "name": "waiting",
      "completes": "later",
      "returns": { "members": [{ "id": "01K8QPN0000000000000000005", "name": "trackingId", "type": "string" }] }
    }
  ],
  "errors": [
    { "id": "01K8QPN0000000000000000006", "name": "already_sent" },
    { "id": "01K8QPN0000000000000000007", "name": "missing_recipient", "input": "01K8QPN0000000000000000002" }
  ],
  "fires": ["01K8QPN0000000000000000020"]
}
```

- `subject`: the entity the operation acts on (Invoice here), optional and not owning. Deleting the entity is refused
  while an operation names it, and both delete resolutions clear `subject`. An operation's name is unique among the
  operations of its subject, so `archive` on Invoice and `archive` on Customer never meet; without a subject, it is
  unique among the subject-less operations of its package.
- `level`: `record` (the default) acts on one record of the subject and takes that record's key implicitly, so `input`
  holds only the rest; `collection` acts on the whole set (a search, a create, a bulk change). It means something only
  with a subject (MQ3023).
- `effect`: required, `read` or `change`, whether or not the operation takes input.
- `input`: the input members, in file order. A member is exactly one of two forms (MQ3024): a typed value, `type`, which
  takes what an attribute's `type` takes (a built-in keyword, an enum, a value object, a custom scalar type or a
  reference type), with `length`, `precision`, `scale`, `default` and `validation` as an attribute has them; or a record
  key, `keyOf`, the id of an entity whose record's key the member holds (with `collection`, a list of keys, such as the
  invoices a bulk change applies to). Either form takes `required`, `collection` and `sensitive`.
- `outcomes` and `defaultOutcome`, `errors`, `fires` and `requires`: below.
- `description`, `rationale`, `decision`, `status`, `stereotypes`, `tags`, `category` and `properties` annotate the
  operation, each member, outcome and error, like any other part of the model. Their display names and descriptions
  translate; the entries live in the shard of the operation file's own package, else the root.

In a batch, an operation is found by its name, by its package path and name, or by its subject's name and its own
(`@operation:Invoice.send`, `@operation:Billing.Invoice.send`); a member, outcome or error adds its name to the
operation's form (`@operation-member:Invoice.send.recipient`, `@outcome:Invoice.send.waiting`). Saving an operation
never records a migration fact: no database holds one.

### Outcomes and returns

An operation ends in one of its **outcomes**, at least one. `completes` is `now` (the default: the work is done when
the outcome is reported) or `later` (accepted, and the work goes on). `defaultOutcome` names one of the operation's own
outcomes (MQ3028); without it, the first is the default. An outcome's `returns` says what comes back, and an outcome
without one returns nothing. A `returns` is exactly one of (MQ3025):

- `members`: an inline shape, at least one member, written as input members are, except that a member of a returns
  takes no `default` (MQ3024);
- `entity`: records of an entity, its attributes as the shape.

`list: true` makes either a list (the invoices a search finds, rather than one).

### Errors

Each error has a `name` (an identifier), an optional `code`, the stable text clients see (the name when it has none),
and an optional `input`, the input member of the same operation that the error points at (MQ3027), such as
`missing_recipient` pointing at `recipient`. Its display name and description are the texts a client shows, and they
translate. Two errors of one operation may not share a name or a code (MQ3026), the name counting as the code of an
error that has none.

### Fires and requires

`fires` lists the ids of the process events the operation fires, of any process (`sent` in the Invoice lifecycle, say).
A `read` operation that fires an event is warning MQ3030, since an event changes state. The resolved event lists the
operations that fire it (`fired_by`).

`requires` lists ids of any element or sub-element that stands for what the operation requires: an actor, a row of a
reference type that holds permission codes, or whatever the project uses. The engine gives the list no meaning; packs
and scripts read it. It may not name the operation itself, one of its own members, outcomes or errors, a diagram or a
migration fact (MQ3031).

### The rules for operations

Every rule below has a row in **Settings › Validation**, where its severity can be changed, and runs on every save and
every `validate`. Names are identifiers (MQ3018) and unique where the file shape says (MQ3001).

| Rule | Severity | What it reports |
| --- | --- | --- |
| MQ3023 | warning | An operation sets `level` `collection` without a subject; the level is ignored |
| MQ3024 | error | A member is not exactly one of a typed value (`type`) or a record key (`keyOf`); a record key sets `default`, `length`, `precision`, `scale` or `validation`; or a member of a returns sets `default` |
| MQ3025 | error | An outcome's `returns` is not exactly one of `members` or `entity` |
| MQ3026 | error | Two errors of an operation share a name or a code (the code, else the name) |
| MQ3027 | error | An error's `input` is not an input member of the operation that holds it |
| MQ3028 | error | `defaultOutcome` names an outcome of another operation |
| MQ3030 | warning | A `read` operation fires a process event; an event changes state |
| MQ3031 | error | `requires` names the operation itself, one of its own sub-elements, a diagram or a migration fact |

### Generating code from operations

The engine runs no operation in your application: what an operation becomes is what the packs write. The scope `each
operation` renders one unit per operation, the template variable being `operation`, with its `subject` (the entity, or
null), `level` (null without a subject), `effect`, `input`, `outcomes` (each with `is_default`, `completes` and
`returns`, whose `members`, `entity` and `is_list` say what comes back), `default_outcome`, `errors` (each with `code`
and the `input` member it points at), `fires` (the events) and `requires` (the elements). A member has `type` or
`key_of`, and `required`, `collection`, `default`, `length`, `precision`, `scale`, `sensitive` and `validation`.
`type_of` maps a member: a typed member as an attribute (the `collection` pattern for a collection, `nullable` when not
required), a record key through the type of its entity's key attribute (an entity with a composite key is MQ6006). An
entity lists its operations (`entity.operations`). A filter takes tags, stereotypes, categories and packages (an
operation's package is its file's, else its subject's) and refuses `database` and `abstract`. The same records are in
MCP `get_resolved_model` and `maquettiste model export --resolved` under the scope `operations`.

**The C# example pack (`csharp-dapper`)** has the unit `operation-contracts`, which writes, for each operation,
`Operations/<package folder>/<Op>.g.cs`, where `<Op>` is the operation's Pascal name followed by its subject's
(`SendInvoice` for `send` on Invoice), or the Pascal name alone without a subject. The file holds plain types with no
dependency on a web framework: `SendInvoiceRequest`, a record of the input members; a record per outcome that returns
members (`SendInvoiceWaiting`), while an outcome that returns an entity uses its class; `SendInvoiceErrors`, a constant
per error code; `SendInvoiceResult`, with a nested record per outcome (`Applied`, `Waiting`) and `Failed(string Code,
string? Input)`; and `ISendInvoiceHandler`, whose `HandleAsync` takes the subject's key at record level, the request and
a cancellation token, and whose summary names the events the operation fires. You implement the handler and map it to
your transport. packs/csharp-dapper/README.md has every detail.

## Packages and layering

Every element can belong to a package (a domain): entities and relations as before, and now tables, views, sequences,
routines, database types, SQL objects, queries, databases, mappings, stereotypes, reference types, seeds, actors and
scenarios too. The element's `package` holds the package's id, and a package names its parent in `parent`. A migration
fact is part of its database's history and has no package. Until the editor's forms show the key for every kind, set it
on the inspector's **JSON** tab.

Membership groups elements: the explorers, `model stats --by package`, `model export --package`, `get_model_kinds`,
`get_elements` and a pack unit's `packages` filter follow it, and a table in a domain sees that domain's tags. It never
changes a database: `database.packages[].schema` still routes only the tables that entities lay out by convention, and
moving a table to another package records no migration fact. Names stay unique as before.

A package can declare layers with `dependsOn`, the packages its elements may reference:

```json
{
  "kind": "package",
  "name": "Catalog",
  "parent": "01J92P0V01KDRN8GX5PGYCNKSX",
  "dependsOn": ["01J92P0V01KDRN8GX5PGYCNKSX"]
}
```

- A package is **checked** when it or a parent declares `dependsOn`, even as `[]`. A model without any `dependsOn`
  behaves exactly as before.
- An element of a checked package may reference the elements of its own package and of its sub-packages, and of every
  package that the `dependsOn` of its package or of a parent lists, with their sub-packages. Parents lend their
  `dependsOn` to the whole subtree.
- A sub-package does not reach its parent by default: in the example, Catalog lists Billing, its parent, so that its
  products may use Billing's `Money`.
- Any other reference is **MQ2020**, an error at the reference, in the file that holds it. Every reference counts:
  attribute types, relation ends, `base`, stereotype keys, `x-ref` values, a seed's target, a view's `dependsOn`. These
  do not:
    - a package's `parent` and `dependsOn`;
    - `package` fields: membership, a vocabulary's scope and a database's convention entries;
    - categories, which MQ2008 already checks along the domain chain;
    - references from diagrams, which only show elements;
    - references from or to an element in no package.
- A package's own references, such as its stereotypes, are judged as the package's, not its parent's.
- **MQ2021** reports a `dependsOn` entry that names the package itself, or packages that depend on each other in a cycle
  (on each entry of the cycle). Layers depend one way.

On a view, a routine or a SQL object, `dependsOn` keeps its own meaning: the database objects to create first. A save is
judged on every element it reaches, so dropping an entry from a package's `dependsOn` is refused while an element
anywhere under that package still uses it, and moving a package is refused while something that may no longer reach it
references its elements. Layering errors never keep a migration fact from being recorded. Like the other rules, MQ2020
and MQ2021 can be turned off or given another severity in `validation.rules`.

## Rationale, decision and status

Every element, and every attribute, enum member, column, database schema and category, can say why it exists. Three
optional fields sit right after `description` in its file:

| Field | What it holds |
| --- | --- |
| `rationale` | Markdown: why the element is shaped this way. It is written in the file itself; there is no sidecar file |
| `decision` | A URL or a reference to the decision record, such as `ADR-0042` (at most 2000 characters). It is not an element id and is not checked: it points into your team's tracker |
| `status` | `proposed`, `approved`, `deprecated` or `retired`. There is no default: an element without `status` says nothing |

```json
{
  "kind": "entity",
  "name": "Invoice",
  "description": "A bill issued to a customer.",
  "rationale": "One row per issued bill; a credit note is an entity of its own, so a total never goes negative.",
  "decision": "ADR-0042",
  "status": "approved"
}
```

A file without them is written exactly as before. Until the editor's screens show them, edit them on the inspector's
**JSON** tab. `get_element`, `get_elements` and `model export` return them with the rest of the document, and
templates read `rationale`, `decision` and `status` (the JSON text) on entities and their attributes, tables and their
columns, databases and their schemas, enum members, categories, the stereotypes `model.stereotypes` lists, and the other
resolved elements. Each is null when the file does not write it. `get_resolved_model` does not carry them yet. Process
states, transitions and the other parts of a process do not take them: the process does. A migration fact's `status`
is its status as an element; the fact's own progress is its `state`. Editing the three fields records no migration
fact, because they never change a database.

**Retiring.** `retired` means no longer to be used. Each reference to a retired element or sub-element is MQ2022, a
warning on the file that holds the reference, so retiring something lists its last users: an attribute typed by a
retired enum, an index over a retired column, a relation end or a `base` that names a retired entity, a retired
stereotype in `stereotypes`. Repoint the reference, or retire the element that holds it as well. These do not warn:

- a reference held by an element or sub-element that is retired itself;
- membership: an element's `package`, and a package's or a category's `parent`, so retiring a package does not warn on
  each of its members;
- owning references: a migration fact's database, a seed's target, a scenario's process, so retiring a database does
  not warn once per fact;
- references from diagrams, which only show elements;
- a reference to a `deprecated` element, which is still in use.

Like the other rules, MQ2022 can be turned off or given another severity in `validation.rules`.

## Migration facts

A change to a database's shape that its stored rows must follow (a renamed column, a narrower type, a dropped table) is
a **migration fact**: one file per fact under `.maquettiste/model/databases/<database>/migrations/open/`, of kind
`migration`. A recorded fact has no name, so its file is named by its id and the folder lists facts in the order they
were recorded. A fact names its object by its physical key and by the names it had at the time; those are history, not
references, so deleting or renaming an element never edits a fact. `after` lists the facts it follows: an ordering hint,
not a reference. Deleting a database deletes its facts. `migrations.record` in `maquettiste.json` is `automatic` (the
default) or `off`. What each fact type records and needs, and the widening table that decides when a change of type
needs a fact, are in the [schema changes guide](guides/schema-changes.md).

Facts are recorded by the writes themselves: a save, create, delete or batch (in the editor, the command line, MCP or
the HTTP API) that renames, retypes or drops a table, column or sequence, or makes a column not null, writes its facts
in the same atomic save and lists them in its answer (`facts`). A settings change of `conventions`, `databases`,
`typeMaps` or `inflection` and a default-locale change record theirs too, and the reference-type refactors record a
`value-map` per database that stores the type's codes. Edits of descriptions, display names, tags,
categories, rationales, decisions or statuses record nothing, and neither do hand edits or a snapshot restore. The guide
explains what each change records.

A fact that lacks the intent its change needs is **pending** (MQ4101, an error): generation stops until it is completed
or held, while template previews, path listings and the resolved model keep working. A **held** fact (`held: true`)
counts as complete for validation and waits until it is `released`. A fact in a database that is not PostgreSQL is
MQ4056 (a warning): its migration keeps TODO comments where its steps would go (a rekey writes no DDL on any dialect),
and no ledger rows are written.

`maquettiste validate --against <baseline>` compares the model with another model: a model snapshot id, or a folder that
holds another copy of the project (a checkout of the main line in CI, for one). Every change since the baseline that the
stored rows must follow (a rename, a type change that is not a widening, a dropped table, column or sequence, a column
made not null) must be explained by facts the baseline does not have; otherwise it is MQ4102, reported on the column
entry, attribute, table or database it concerns. The check reads no database and no version control, and runs even when
`migrations.record` is `off`. MCP `validate` takes the same `against`, and `POST /api/validate?against=<snapshot id>`
takes a snapshot id. A fact whose change the model no longer shows (a rename made and undone, a pending narrowing
reverted) is **stale** (MQ4103, a warning); `migration fold` removes it. The schema changes guide says more.

### The migration ledger

On PostgreSQL, each database records which facts it has reached in the **migration ledger**: three bookkeeping tables
(`contract`, `applied_fact`, `applied_object`) in the schema `migrations.ledgerSchema` names in `maquettiste.json`
(default `migration_ledger`; a lower-case name, quoted where it is a reserved word such as `order`). The sql-ddl scripts
write one row per fact they reach, in the same transaction as the statements that reach it: `schema.sql` for every fact
a build reaches, a migration for the facts it completes or releases. A run-once SQL object writes its own row
(`applied_object`) when it runs, which is how its guard knows not to run it again. A team's own migrator writes fact
rows the same way. The template helper `ledger_ddl <database>` writes the ledger's DDL (idempotent, without transaction
control), or nothing for a database without a ledger; a changed `ledgerSchema` renders the scripts that call it again. A
ledger schema that is also a schema of the database's modeled objects is MQ4067 (a warning). `maquettiste migration
ledger --grant-read <role>` and MCP `get_ledger_ddl` print the DDL and the read grant for a migrator you run yourself.
Maquettiste never reads the ledger: `validate --against` compares two models, never a database. The contract, its rules
and its status are in [The migration ledger](ledger.md).

### The rules for migration facts (MQ41xx)

| Rule | Severity | What it reports |
| --- | --- | --- |
| MQ4101 | error | A fact lacks the intent its change needs, one finding per missing field: a `conversion` for a `change-type`, and a `dataCheck` too when it narrows; an `acknowledge` for a `drop`; a `value` (or a `dataCheck`) for a `backfill`; a `skipWhen` guard for a `raw` step. Complete it, or hold it |
| MQ4102 | error | `validate --against`: a change since the baseline needs a migration fact and none the baseline lacks explains it (a rename or a moved key, a type change that is not a widening, a dropped table, column or sequence, a column made not null). Reported on the column entry of a table file or overlay, the attribute of a derived column, the table or its entity, or the database file for a dropped object |
| MQ4103 | warning | A fact the baseline lacks is stale: the model no longer shows what it, alone or with the facts on the same object, changed (a rename made and undone, a type changed back or widened back, a dropped object that is back, a column nullable again), or a `value-map` whose codes its reference type did not hold in the baseline model and whose names did not change (a code born after the baseline, then renamed). A pending fact that is stale is MQ4103, never MQ4101. Plain `validate` judges complete facts only for a database that has a committed physical snapshot, and never a value map, since a physical snapshot holds no rows; `--against` judges them against its baseline |
| MQ4104 | error | The `after` links of a database's facts form a cycle, so no order applies them; reported on each fact of the cycle |
| MQ4105 | error | An `after` link names a fact of another database |
| MQ4106 | error | A fact's fields do not fit its type: `from` equal to `to`, a `conversion` or `dataCheck` with both forms or neither, `released` without `held`, a key or object listed twice, a `values` code listed twice or mapped to itself, an object kind the type does not take (a `drop` takes a table, column or sequence; a `change-type` or `backfill` a column), a member without its table, a collection listed twice, or a `value-map` that maps codes in no column or collection and changes no name, or whose `to` is also a `from` (a chain) |
| MQ4107 | error | Two facts change one object and neither comes after the other (merged copies changed one column): order them with `after`, or fold them. Reported on the later fact; a table drop also changes each of its members |
| MQ4108 | warning | A fact's SQL text (`sql`, `skipWhen`, `value`, `where`, a conversion's expression, a data check's query) has no entry for the database's dialect and no `*` entry |
| MQ4109 | warning | `validate --against`: the baseline has errors, so the objects their elements own, or the objects in baseline files that did not load, were left out (or the database did not resolve) and their changes were not checked; one finding per database, on the database file. `generate --against` reports it the same way: those objects are left out of both sides of the schema diff, and a database that does not resolve in the baseline is not diffed |
| MQ4110 | info | A write changed objects whose elements have errors, so no facts were recorded for them; on the save's answer. Fix the errors; validate against a baseline checks the change |
| MQ4111 | warning | An `after` link names a fact that does not exist (folded or removed in another copy); the link is ignored for ordering |
| MQ4112 | error | `validate --against`: since the baseline, a kept reference row's code changed, a former code of a code the baseline held was added, or a reference type's `name` or `pluralName` changed, and no open `value-map` the baseline lacks records it in a database that holds the type (for a rename, also each database without a binding of the type). Reported on the row's code cell, the `formerCodes` entry, or `/name` or `/pluralName`, naming the databases that lack the fact. Run the refactor, or write the fact |
| MQ4113 | error | An open `value-map` maps away a code that is a live code of its reference type: applied late, it would move the data of the row that holds the code now. Give that row another code, or fold the fact if it is stale |
| MQ4114 | warning | A save, create or batch changed the code of a reference row it keeps, or a reference type's `name` or `pluralName`, outside the refactor operations (`rename-code`, `retire-code`, `merge-reference-types`, `rename-reference-type`), so no `value-map` was recorded; on the save's answer, at the cell or field. Use the refactor. Not reported while `migrations.record` is `off` |

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
`table.description`, `table.rationale`, `table.decision`, `table.status` (see [Rationale, decision and
status](#rationale-decision-and-status)), `table.tags`, `table.category` (with `name` and `path`), `table.stereotypes`
(each with `key` and `name`), `table.properties` (a custom property reads as `{{ table.properties.tablespace }}`, with
the stereotypes' default properties under the file's own values) and `table.generation`. The file is the designed or
imported table, or the overlay you add to a synthesized table; a synthesized table without one has none of these, and
its entity's are at `table.entity`. The rest of the database side works the same way, each from its own file or entry:
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

A value a file leaves out reads as null in a template, never as an empty text or `false`, unless its schema gives a
default, which it then carries. So `{{ table.display_name ?? table.name }}` works, and a test against `""` is wrong:
`t.native_name != ""` is true when the value is null, so write `t.native_name != null`. Some values are worked out and
always set, each by its own rule: an element's display and plural names fall back to its name and the plural the
inflector makes; a column's type and native type come from the type maps; constraint names from the naming conventions;
a routine's language and volatility from the dialect; a query field's name from its attribute and its nullability from
its expression. A table, column or other database object has no such fallback: its `display_name` and `plural_name` are
its own file's, or null. A view, routine or SQL object without text for the database's dialect has a null `body` (and
`has_body` false); a routine that returns a table has a null `returns.native_type`; a database type the dialect cannot
create, such as a composite outside PostgreSQL, has a null `native_name`; a query that cannot be written for the
dialect, or a nested one, has a null `sql`. The helpers `display_name`, `plural_name` and `description_of` give a
database object's own text, or null.

A few helpers find and order things so a template does not loop to search. `table_named "billing.invoices"` gives the
table of that name (without a schema part, a table with no schema or in its database's default schema), or null; it
searches every database unless you pass one, `table_named "invoices" database` or `table_named "invoices" "main"`, and a
name two tables share fails the unit (MQ6006). Renaming a table into or out of the name renders the unit again.
`column_named table "total"` gives one column. `array_index_of list "name" "Invoice"` gives the first index whose item's
member equals the value (-1 when none; without the member path it compares the items themselves), and `array_find list
"name" "Invoice"` gives that item or null; values compare exactly, so `"Invoice"` and `"invoice"` differ. `toposort
items edges` orders a list so that, for each `[before, after]` pair in `edges`, `before` comes first; items keep their
order where no pair decides, and a cycle lets the earliest item left go first. `json value ["name", "columns.name"]`
writes the named members as JSON, so a table or an entity can be written without a script: a path through a list applies
to each item, and a path must end on a value, not on a table or column (MQ6006). Pack helpers may not take these names,
nor `database` and `attribute` (MQ6013). A function a template defines with `func` may: it takes the name in that
template only, and every other template still sees the built-in.

The model also lists its definitions. `model.stereotypes` has one stereotype per key, by key: `key`, its label as
`name`, `applies_to`, the `attributes` it adds (each with the stereotype as `owner`), `default_properties`, `icon`,
`color`, and the package, description, rationale, decision and status of its file. `model.tag_vocabularies` has the
global vocabulary first, then each domain's: `package` (none for the global one), `strict` and `definitions`, each with
`key`, `description` and `color`. `model.category_trees` has the global tree first, then each domain's: `package`,
`roots`, and `categories`, every category depth-first in sibling order. A category, wherever a template meets it
(`table.category`, a tree's `categories`), also has `parent`, `children`, `order`, `description`, `rationale`,
`decision` and `status`.

**A pack** is a folder, `.maquettiste/templates/<pack>/`, holding the templates and one `pack.json` that says what to
run. The example packs are `sql-ddl` (database scripts), `csharp-dapper` (classes and repositories, and the process
code) and `process-docs` (Markdown pages for processes, actors and scenarios); your own packs sit beside them and work
the same way. A pack also has an **output base** (`packs.<pack>.output` in `maquettiste.json`),
the folder its paths start from, and can be switched off there (`enabled: false`).

**A unit** is one line of a pack's work list. It names a template, which elements the template runs for (the scope),
and where the result goes (the output pattern). `sql-ddl` has twelve units: `table`, `schema`, `migration`, `seed`, `seed-environments` and `object-environments` (one script per environment its `environments` parameter names), `process-tables` (off unless its `processTables` parameter is set), and `view`, `sequence`, `routine`, `database-type` and `sql-object` (one script per object, off unless its `objectScripts` parameter is set).

**The scope** (`for` in `pack.json`) decides how many times a unit runs:

| Scope | Runs | Result |
| --- | --- | --- |
| `each table`, `each entity`, `each enum`, … | once per element of that kind | one file per element: 40 tables give 40 scripts |
| `each attribute` | once per attribute declared in a model file: an entity's own, a relation's, a value object's and a reference type's (never an inherited copy, a stereotype's added attribute or a promoted relation's copy) | one file each, the template variable being `attribute`; a filter reads the attribute's own tags, stereotypes and category and its owner's package, and `database` keeps the attributes of entities and relations mapped there (a value object's or reference type's only when a database of that name exists) |
| `each database` | once per database | one file each, the template variable being `database`; a filter reads the database file's tags, stereotypes, category and package, and `database` matches its name |
| `each view`, `each sequence` | once per view or sequence of every database (the key sequences the engine creates included) | one file each; a filter takes tags, stereotypes, categories and the database, read from the view's or sequence's own file |
| `each routine`, `each database type`, `each sql object` | once per routine, database type or SQL object of every database | one file each, the template variable being `routine`, `database_type` or `sql_object`; a filter works as for views |
| `each query` | once per query of every database | one file each, the template variable being `query`; `query_sql` and `query_collection_sql` render its SQL; a filter works as for views |
| `model` | once, with the whole model | one file for many: a template that loops over every entity writes them all into one file |
| `each locale` | once per declared language | one file per language (a resource file, a dictionary) |
| `each process`, `each actor`, `each scenario` | once per process, actor or scenario | one file each: a page per process, a test per scenario (see "Generating code from processes") |
| `each operation` | once per operation | one file each, the template variable being `operation`: its `subject`, `level`, `effect`, `input`, `outcomes` (each with `completes` and `returns`, whose `members`, `entity` and `is_list` say what comes back), `errors` (each with `code`, the name when it has none, and the `input` it points at) and `fires`; `type_of` maps an input or returned member; a filter takes tags, stereotypes, categories and packages (an operation's package is its file's, else its subject's) and refuses `database` and `abstract`; `csharp-dapper` writes the request, result, error codes and handler interface of each one |
| `select <name>` | once per element a pack script returns | `select roots` gives one file per element the pack's `roots` selector returns |

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

**Project properties** are the project's own values that every pack can read, such as a base namespace or a company
name: Settings › General › Properties (`properties` in `maquettiste.json`, key to text; a key is letters, digits and `_`,
not starting with a digit). Templates read them as `project.properties.<key>` (and the project's name as `project.name`),
apart from the pack's own `pack.params`: `namespace {{ project.properties.baseNamespace }}.Model`. Changing one
re-renders only the units whose templates read `project`.

**The write mode** says what happens to a file that already exists: Overwrite (the default), Create only if missing
(`once`), Protected regions (your code between markers survives), Pair (a generated file plus a companion file for
hand-written code that is created once and then left alone), Managed block (`block`: a few lines the pack keeps inside a
file that is otherwise yours, such as an ignore file; see "Managed blocks"), or Copy (`copy`: pack files copied as they
are, with no template; see "Copy units").

### Change a template and see the result

1. Choose **Generate** in the rail and open a pack in its explorer; its tab opens beside **Plan**.
2. **Templates** (Alt+3): pick a file on the left; the middle pane is the template. The line above it says which units
   use it.
3. On the right, the **preview** renders a unit for one element with your unsaved text, a moment after you stop
   typing: pick the **Unit** and one of the unit's elements (the picker lists only its kind: entities for an `each
   entity` unit, reference types for `each reference type`, locales for `each locale`), read the output path and the
   text it would write, and any error is marked at its line. A preview writes nothing, and it renders that one element
   only: however large the model, writing a template never renders more than what you are looking at. To see a few
   elements at once, **Preview a list…** lets you tick up to 20 and renders them when you click **Preview**. A unit that
   renders once for the whole model (`for: model`), once per database (a schema or seed script) or once per locale is
   not rendered as you type: click **Preview (renders the whole model)** (or "a whole database") when you want to see
   it. After that it follows your typing while a render takes under a second; a slower one stays on screen marked
   **Out of date**, with **Preview again**.
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

The **Plan** tab's toolbar chooses which packs to plan: **Packs · 2 of 3** opens a list with one checkbox per pack, in
the order of the project's packs, with **All** and **None**; past eight packs a filter box narrows the list, and All
and None then act on the packs it shows. A disabled pack is listed greyed and cannot be ticked (turn on **Enabled** in
its pack editor's header), and a pack with diagnostics shows their count. The choice is kept with the page state;
**Plan** stays disabled while no pack is ticked. A plan covers every output root of `outputs.allow`.

**Options**, next to **Plan**, holds two choices for the next plan only; nothing is saved, and the button shows a dot
while one differs from its default:

- **Re-render every file**: renders every file again, even when nothing its unit reads changed (the plan's Why then says
  "Forced: the run renders every unit"). Use it after a change the plan cannot see, or to be sure. Nothing is written
  until Apply, and Apply still writes only files that differ, and never files that are yours. A forced plan has no
  "Not re-rendered" files: what comes out the same as the disk is listed as **Identical**.
- **If a generated file was edited by hand**: **Project default** (the project's `handEdits` setting, shown in
  brackets), **Stop and report it** (`fail`: the file is listed as a conflict and left as it is), **Overwrite it**
  (`overwrite`: your edit is replaced) or **Keep it and skip** (`skip`: your edit stays and nothing is generated for it).
  Apply uses the choice the plan was made with; the Apply result names it only when a file was in fact edited by hand
  ("2 files edited by hand: overwritten (this run's choice: Overwrite it)").

When the plan is made, one line says what it found: `Plan ready: 3 files to write (1 added, 2 modified); 58 identical,
6 yours` after a forced plan, `Plan ready: 3 files to write (1 added, 2 modified); 2 identical, 40 not re-rendered
(inputs unchanged), 6 yours` after an incremental one, and `Plan ready: nothing to write; 650 not re-rendered (inputs
unchanged), 6 yours` when Apply has nothing to do. Files edited by hand are counted first after the semicolon. A plan
that failed or was cancelled shows its outcome and the reason instead.

A plan is a dry run: it renders what needs rendering and compares it with the disk, and nothing is written until you
apply it. It lists **every file** its packs produce, each with what Apply will do to it:

| Badge | Meaning | Apply |
| --- | --- | --- |
| added, modified, deleted | a new file, new content, or an orphan (a file generation wrote earlier that no unit produces any more) | writes or removes it |
| identical | rendered again, and the same as the file on disk | nothing to write |
| not re-rendered | its unit was skipped: nothing the unit reads changed since the last run (Re-render every file renders it again) | nothing to write |
| yours | a file written once (a `once` file such as a migration, or a pair's companion) that exists: it is yours to edit; a `once` file no unit produces any more stays yours, and tracked, while it exists | never overwrites it |
| hand-edited, conflict | a generated file edited by hand since; the hand-edit choice decides | as that choice says |
| orphaned-owned | a pair's companion no unit produces any more, a file of yours from a pack that was removed, or a file of yours that was deleted | leaves whatever is on disk and stops tracking it |

A migration written earlier stays owned while it is on disk, even when no run produces it any more, so the plan shows it
neither as an orphan nor as drift and Apply never deletes it. Selecting a file Apply leaves alone
(identical, not re-rendered, yours) shows what it is instead of a diff: "Written once; yours to edit. Generation never
overwrites it."

Above the table, one line per pack says what it will do, for example
`sql-ddl: 4 units, 12 files to add, 3 to modify, 1 orphan to delete, 20 files identical, 2 files yours` (identical,
not re-rendered and yours files come last, then the units the plan skipped). When no file needs writing the line says
so, `atlas-schema: 737 units, nothing to write: all 737 files already match the disk`, a note says Apply has nothing to
do, and **Apply plan** stays disabled.

The chips above the table filter it by what Apply does, each with its count: **All** (the default: every file), **To
write**, **Identical**, **Not re-rendered**, **Yours** and **Edited by hand**; a chip with no file is greyed. The
table stays quick with thousands of files (only the rows on screen are drawn).

Under the summary the plan lists its own notes (a file edited by hand, a formatter that failed, a locale left
incomplete). The model's validation findings, which a plan also carries, are not repeated there: one line counts them,
`3 model findings (2 warnings, 1 note) are in Problems`, and **Open Problems** shows them. When the model has errors the
plan stops, and the line reads `Plan stopped: 2 model errors, see Problems`. The table groups the files by unit (`sql-ddl/table`, with its template and its counts);
click a group to fold it. Each file shows what Apply does to it, its path, its unit, its element and **Why** its unit
renders: "New: no recorded state from an earlier run" the first time, "Customer (entity) changed" or "Template
table.scriban changed" after an edit, "… was edited on disk" when a generated file was changed by hand, "Unchanged:
nothing it read changed" for a file not re-rendered. Filter by the chips, pack, unit or any words.

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

### Regenerate one element or one unit

A full run is the usual way to generate, and it is quick when little changed. When you want the files of one element
or one unit now and the rest later, limit the run from a terminal:

```sh
maquettiste generate --element <entity id>                           # one entity's files, in every pack
maquettiste generate --unit csharp-dapper/entity                     # one unit, for every element it runs for
maquettiste generate --unit sql-ddl/schema --element <database id>   # one database's schema script
```

`--unit` takes `<pack>/<unit>`, or a unit id that only one pack of the run has (`schema` alone fails when two packs have
a `schema` unit). `--element` takes an element id, never a name. A table that the engine builds from an entity has the
id `<entity id>@<database id>`, and a unit that runs once for the whole model is never selected by an element. Both
options repeat: values of one option add up (OR), and the two options narrow each other (AND). They combine with
`--pack`, `--dry-run`, `--check`, `--diff` and `--watch`. The MCP tools `plan` and `generate` take the same filters as
`units` and `elements`, and so does `POST /api/generate/plan` in its body; a limited plan's apply writes only its own
units' files.

**What a limited run leaves alone.** It writes only the files of the units it selects. Every other generated file, its
entry in `.maquettiste/manifest/` and its unit's record stay as the last run left them, so the next full run still sees
what is left to do. The selected units still delete the files they no longer produce: `generate --element <id>` for an
element you deleted removes that element's files and nothing else. A limited run neither answers from nor writes the
record of the last run that lets a later run with nothing changed finish at once.

**Migrations need a full run.** A migration is the difference between the model and the schema snapshot of the last
full run, and only a full run moves that snapshot forward. A limited run therefore refuses to render a unit that reads
the schema difference (`sql-ddl/migration`): that unit fails with MQ6033 and keeps its files, and the next full run
writes it from the snapshot it diffs from. A database id selects every unit that runs once per database, the migration
among them, so `--element <database id>` alone exits 1; add `--unit sql-ddl/schema` to regenerate only that
database's schema script.

| Rule | Severity | What it reports |
| --- | --- | --- |
| MQ6032 | error | A `--unit` value names a pack the run lacks (it does not exist, is disabled, or is left out by `--pack`), a unit that no pack of the run has, or a unit id that several packs of the run have. Nothing is generated; exit 1 |
| MQ6033 | error | A limited run selects a unit that reads the schema difference. That unit fails and keeps its files, and the schema snapshot stays where it was; exit 1 |
| MQ6034 | warning | The filters select no unit and no file an earlier run wrote. Nothing is generated; exit 0 |

### Make your own pack from a starter

In the editor, **+** in the Generate explorer header (or **New pack…** in the palette): a name, then **Start from**
**Empty pack** (one each-entity unit and its template, ready to edit) or **Copy of** a pack of this project. From a
terminal, `maquettiste pack new <name> --from sql-ddl` (or `csharp-dapper`, or `empty`). An empty pack starts with an
example test case under `tests/` (see "Test a pack" below); a copy starts without tests, since its name and banners
differ. Then give it an output base under an allowed root (below) in the pack editor's header or in `maquettiste.json`,
edit its units and templates, and plan. The copy is yours: change it freely; the example packs are not updated under
you. To remove a pack, use **Remove pack…** in the pack editor's header (or `maquettiste pack remove <name> --apply`):
it deletes the pack's folder and its `packs.<name>` settings entry, and the files it generated stay on disk, no longer
tracked. To rename a pack, use **Rename pack…** beside it (or `maquettiste pack rename <name> <new-name> --apply`): the
folder, the `packs.<name>` settings entry and the record of the files it generated move to the new name together, so
those files stay tracked. Generation hints keyed by the old name (`generation.<name>` on elements, tables, columns and
other parts) move to the new name too: the dialog's **Also update the generation hints that name this pack** is ticked
by default and saves them as one change you can undo, and the command does the same unless you add `--keep-hints`.

### Library packs: share partials and helpers

Two packs that render the same header or compute the same class names can share one **library pack** instead of
copies. A library has no units; other packs list it in `uses` with a version range:

```json
"uses": [ { "pack": "naming", "version": "^1.2.0" } ]
```

`maquettiste pack new naming --library` (or `new_pack` with `library`) writes a library with
`partials/header.scriban` and a `helpers.js`. A pack that uses it includes the partial by a prefixed name,
`{{ include "naming:partials/header.scriban" }}`, and calls its helpers through a namespace named after the pack:
`{{ naming.class_name entity }}`. The partial renders with the including unit's variables; the helpers run with the
library's own parameters (`packs.naming.parameters` in `maquettiste.json`). A pack's scripts reach a used pack with
`maquettiste.use("naming")`. The project's own rule scripts can too: list the library in `uses` at the top of
`maquettiste.json`, and a rule calls `maquettiste.use("naming").class_name(element)`.

A used pack loads with every pack that uses it, enabled or not, and is never rendered for it. Changing a library's
version, scripts or parameters renders again every unit that uses it, and nothing else; validation runs its cached
rules again. Renaming a library rewrites the `uses` entries that name it and lists the templates and scripts whose text
still does (edit those yourself); deleting it, or a partial another pack includes, is refused while something uses it.

| Rule | Severity | When |
| --- | --- | --- |
| MQ6042 | error | A `uses` entry names a pack that does not exist or did not load, or a list names a pack twice. |
| MQ6043 | error | The used pack's version does not satisfy the range, or the range does not parse. |
| MQ6044 | error | A pack uses itself, or packs use each other in a cycle. |
| MQ6045 | error | A used pack's namespace collides with a builtin, a template keyword or variable, or a root helper. |
| MQ6046 | warning | A pack has no units, and no pack and no project `uses` lists it. |
| MQ6047 | error | A template reads the exports of a pack its pack does not use, or an export template reads its own pack's exports. |
| MQ6048 | error | Two units of one pack export one name for one element with different values. |
| MQ6049 | error | An export's `each` names a member that is not a list of items with ids, or two items share an id. |
| MQ6050 | error | A unit reads exports that failed to evaluate; it is not rendered. |

[Share and test packs](guides/share-and-test-packs.md) walks an example.

### Packs that read each other's exports

A unit can state names per element that other packs read, so a name has one definition. `exports` in a unit maps a
snake_case name to a template, rendered with the unit's variables as `output` is, or to `{ "each": "<list member>",
"value": "<template>" }`, rendered once per item of that member of the element (`attributes`, `columns`, an enum's
`members`) with the item as `item` and keyed by the item's id. Values are text: line endings LF, leading and trailing
whitespace trimmed.

```json
{ "id": "record", "template": "record.scriban", "for": "each entity",
  "output": "{{ entity.package.name }}/{{ exports.class_name }}.cs",
  "exports": {
    "class_name": "{{ pascal entity.name }}Record",
    "namespace": "{{ project.properties.baseNamespace }}.{{ entity.package.name }}",
    "members": { "each": "attributes", "value": "{{ pascal item.name }}" } } }
```

- The unit's own template, `output` and companion read its values as the variable `exports`, so a file's name and the
  name other packs read are one definition.
- `exported <element | id | model> "<pack>"` returns a pack's record for an element as a read-only map, or `null` when
  no unit of the pack plans the element. The record merges the exports of every unit of the pack that plans the
  element, with its scope, `where` filter and skip hints applied. `exported model "<pack>"` reads the `for: model` units.
- A template reads the exports of its own pack and of the packs in its `uses` closure. An export template reads only
  the packs its pack uses, never its own, and sees neither `data`, `schema_diff` nor `exports`.
- Exports are computed from the model, never read back from rendered files, so render order cannot change a byte.
  A unit that read a record renders again when that record changes, and only then: editing the exporting template's
  body renders only the exporting units.
- `maquettiste pack exports <pack> [--element <id>]...` and the MCP tool `get_pack_exports` show the records.
- Rules: MQ6047 (a template reads a pack its pack does not use, or an export template reads its own pack), MQ6048 (two
  units of one pack give one name different values for one element), MQ6049 (`each` names no list of items with
  ids, or two items share an id) and MQ6050 (a unit reads exports that failed to evaluate; it is not rendered).

### Test a pack

A pack can carry its own tests in a `tests/` folder, which generation never reads. Each folder directly under `tests/`
is a **case**: a small project (`maquettiste.json` and `model/`) and `expected/`, the files the pack must write for it.
Each `tests/*.test.js` file holds **helper tests**, which call the pack's helpers with plain values:

```js
maquettiste.test("record_name adds the suffix", (assert, helpers) => {
  assert.equal(helpers.record_name({ name: "invoice" }), "InvoiceRecord");
});
```

`maquettiste pack test` runs the tests of every pack that has them; name packs to run only theirs, and `--case <name>`
(repeatable) to run only those cases. Each case runs as a fresh project in a temporary folder, with the pack and the
packs it uses, and its output must equal `expected/` byte for byte. The project's own outputs, manifests and unit states
never change.

```
$ maquettiste pack test records
records
  PASS case billing-small
  FAIL case billing-small-rows
    differs Billing/InvoiceRow.cs
      --- a/Billing/InvoiceRow.cs
      +++ b/Billing/InvoiceRow.cs
      ...
  PASS test tests/helpers.test.js: record_name adds the suffix
2 passed, 1 failed
```

Exit 0 when everything passed, 1 when a case or test failed, 4 on a usage error or an unknown pack; `--format json`
writes the report. `--update` writes each run case's `expected/` from its output (and nothing else), for a new case or
an intended change: review the diff before keeping it. A `case.json` in a case can name another case's `model` and
override `parameters`, so one model serves several variants. `pack new` writes an example case and a helper test; the
MCP tool `test_pack` runs the same tests and answers the same report. packs/README.md, section 9, has the folder layout
and the assert API, and the [Share and test packs](guides/share-and-test-packs.md) guide walks through an example.

| Rule | Severity | When |
| --- | --- | --- |
| MQ6051 | error | A case's output differs from `expected/`: a file added, missing or different, with a diff. |
| MQ6052 | error | A helper test fails: an assertion fails, the test throws or it hits a sandbox limit, at the failing statement. |
| MQ6053 | error | A malformed test: a case without a project or `expected/`, a case holding `templates/`, an invalid `case.json`, cases in a pack without units, a test file that registers anything but tests, or two tests with one name. |

### outputs.allow: what generation may touch

Generation writes only under the paths listed in `outputs.allow` of `maquettiste.json`, never elsewhere, whatever a
template or an output pattern says:

```json
"outputs": {
  "allow": [
    { "path": "db" },
    { "path": "services/billing/src/Generated" },
    { "path": "services/billing/src/.gitignore" }
  ],
  "deny": ["**/*.user.cs"]
}
```

An entry is a folder generation may write under, or a single file it may write: `services/billing/src/.gitignore` above
allows exactly that file, so a pack can keep lines in it (see "Managed blocks") without the whole `src` folder, or the
repository root, becoming writable. An entry that ends with `/` is a folder only; `""` or `.` is the whole repository. In
a large repository with many projects, list each generated folder as its own entry; everything else (hand-written code,
other teams' folders, `.git`, `.maquettiste`) is out of reach. A path outside every entry is refused before anything is
written (MQ6019, shown in the Units grid next to the pattern), and `deny` globs carve exceptions out of an entry. An entry
says nothing else: the `commit` flag of releases before 0.5.5 is ignored (MQ1010, an info line per entry; `maquettiste
format` removes it).

**Which files to commit.** That is the team's choice, and Maquettiste never needs git: a project kept in a shared folder
works the same. Whatever generation writes is recorded in one manifest per pack, `.maquettiste/manifest/<pack>.json`,
which lives with the model; commit it with the model. A team that commits some outputs and not others gets the same
behavior for both: `generate` rewrites what changed, and a file the engine wrote in this checkout is never taken for a
hand edit after a pull brings a newer manifest. `generate --check` renders every output in memory and exits 2 when any
file would be added, changed or deleted, so in CI either commit every output you want `--check` to guard, or run
`maquettiste generate` and then compare the working tree (`git diff --exit-code`). Protected regions work in any output,
but their bodies live only in the file: in output you do not commit, a fresh clone starts them empty, so keep region files
(the C# process endpoints, for example) in a folder you commit. Maquettiste's own working folder, `.maquettiste/.cache/`
(the run journal and lock), holds a `.gitignore` with a single `*` line, so it is never committed by accident whatever
the repository's own ignore file says; Maquettiste never edits that file unless a pack's managed block asks it to.

### Managed blocks: a few generated lines in a file you own

Some files are yours but should carry a few generated lines: an ignore file that must list the generated folder, a
configuration file with a generated section. A unit in mode `block` owns one block of lines in such a file and nothing
else. Its template renders the block's lines; the engine puts them between two delimiter lines,

```text
# maquettiste: begin csharp-dapper/gitignore
/Generated/
# maquettiste: end csharp-dapper/gitignore
```

where `#` is the unit's `blockComment` (default `#`; `//`, `;` or any other comment marker without spaces) and the name
after `begin` and `end` is the pack and the unit id. A file whose comments must be closed, such as XML or HTML, also
sets `blockCommentEnd`, the text that ends each delimiter line. A unit with `"blockComment": "<!--"` and
`"blockCommentEnd": "-->"` keeps its block in `app.config.xml` like this:

```xml
<!-- maquettiste: begin billing/settings -->
<add key="Billing.InvoicePrefix" value="INV" />
<!-- maquettiste: end billing/settings -->
```

A delimiter line is found whatever its comment and closing text, so a block written with other ones is still found, and
removed when its unit goes; the next run rewrites its delimiter lines with the unit's. Each run then:

- replaces the lines between the delimiters when the block is there (the rest of the file, every byte of it, is left as it was);
- appends the block when the file has none: after a line break if the file lacks a final one, and after a blank line unless
  the file already ends with one;
- creates the file, holding just the block, when it does not exist and the unit sets `"createFile": true`. Without it
  (the default) the unit writes nothing, the plan lists it with the reason `target-missing` ("the file does not exist; set
  createFile to create it"), `generate` prints MQ6028 as information, and the unit tries again on the next run;
- removes the block when the unit is removed, its pack disabled, or the unit no longer writes that file: the change is
  listed as `D`, a blank line the engine added before the block goes with it, and the file itself is deleted only when the
  engine created it and nothing but whitespace is left.

Only the block's lines are recorded and compared, so you may edit the file around it freely; an edit inside the block (or
deleting the block) is a hand edit like any other and follows `handEdits`. A file holding the same block twice, or a
begin line without its end, is left alone and reported (MQ6027) until you fix it. Formatters never run on a block. Two
units cannot share one file yet (MQ6005), so give each its own file. The path must be allowed: a file entry in
`outputs.allow` such as `src/App/.gitignore` is the narrow way.

Example: the `csharp-dapper` pack's `gitignore` unit keeps the generated folder out of version control. Set its
`gitignorePath` parameter to the ignore file's path under the pack output and allow that file; the reference application
does it this way:

```json
"outputs": { "allow": [ { "path": "db" }, { "path": "src/ReferenceApp.Data/Generated" }, { "path": "src/ReferenceApp.Data/.gitignore" } ] },
"packs": {
  "csharp-dapper": {
    "output": "src/ReferenceApp.Data",
    "parameters": { "generatedFolder": "Generated", "partialFolder": "Generated", "gitignorePath": ".gitignore", "namespace": "ReferenceApp.Data" }
  }
}
```

The block then holds `/Generated/`. With `gitignorePath` empty (the default) the unit writes nothing.

### Copy units: a folder of files copied as they are

Some files need no template: a project skeleton, a logo, a configuration file. A unit in mode `copy` names no template;
its `sources` are glob patterns over the pack's files, and every file they match is copied as it is:

```json
{
  "id": "api-skeleton",
  "for": "model",
  "mode": "copy",
  "sources": ["skeleton/**"],
  "tokens": { "Skeleton.Api": "rootNamespace" },
  "output": "src/Billing.Api"
}
```

- A pattern is relative to the pack folder: `*` and `?` match inside one folder or file name, `**` across folders.
  Patterns match case-sensitively and skip dot files and dot folders, whatever the file system. A pattern that matches
  no pack file is MQ6030: the unit is skipped, the pack's other units still render, and the run is invalid.
- `output` is the destination folder, under the pack's output base, rendered like any output path; without it the files
  land at the output base itself. Each file keeps its path after the pattern's fixed folders (those before the first `*`
  or `?`): with `skeleton/**`, `skeleton/Api/Invoices.cs` lands at `src/Billing.Api/Api/Invoices.cs`. A pattern with no
  wildcard, such as `skeleton/logo.png`, keeps the file's own folder as its base, so the file lands at
  `src/Billing.Api/logo.png`. Two files on one path are MQ6005. A copy unit keeps `for` and `where`: with `each entity`
  it copies the files once per entity, so its `output` must differ per element.
- `tokens` maps literal text to a pack parameter, so the sources stay ordinary code that builds on its own. With the
  project value `Billing.Api` for `rootNamespace`, every `Skeleton.Api` in a text file and in a copied file's path
  becomes `Billing.Api`. Tokens are replaced in one pass, the longest first where two start at the same place. A token
  that names a parameter the pack does not declare, or one whose value is not text, is MQ6031 and skips the unit.
- A text file (valid UTF-8 without a NUL byte) changes only where a token occurs: line endings, a byte order mark and
  every other byte stay, and no formatter runs. Any other file, an image for example, is copied byte for byte.
- The copies are managed like Overwrite files. Editing, adding or removing a source renders the unit again (a copy whose
  source went is removed), and a hand edit of a copy is MQ6009 and follows `handEdits`: edit the source in the pack
  instead. The preview lists every file the unit copies; a binary file shows as binary, with no text, and its diff
  reads `Binary file <path> differs`.

The pack's file list marks the files a copy unit copies as copy sources: they are never parsed as templates, loaded as
pack scripts (unless `scripts` names them) or read as type maps.

## Packs in the editor: what generation does

A **pack** is a folder of templates and one `pack.json` under `.maquettiste/templates/<pack>/`; generation runs every
enabled pack. The editor reads and writes those files in place, so the command line, the editor and git always see the
same pack. The words the screens use:

| Word | Meaning |
| --- | --- |
| Unit | One line of the pack's work list: which elements, which template, which output path |
| Scope | Which elements a unit runs for: once (`model`), once per element of a kind (`each table`, `each database`), or once per element a selector returns (`select roots`) |
| Files | The scope read as files: "Each table" writes one file per table, "Once" one file for the whole model, "Once per package" and "Once per database" one file per group |
| Filter | Narrows the scope by tags, stereotypes, categories, packages (and their "not" lists), database, abstract, or a script filter |
| Output path | A pattern rendered with the template's variables; the file lands under the pack's output base, inside an allowed output root |
| Write | Overwrite, Create only if missing, Protected regions, Pair (a generated file and a companion for hand code), or Managed block (a delimited block of lines in a file you own) |
| Parameters | Pack settings the templates read as `pack.params`; defaults in `pack.json`, values per project in `maquettiste.json` |
| Project properties | The project's own values every pack's templates read as `project.properties` (Settings › General) |

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
  The count comes from the unit's scope without rendering anything; the example path is rendered for the selected row
  only (one element), and a unit that renders once for the whole model, a database or a locale shows no example here
  (preview it on Templates).
  The picker names each element with its kind, "Customer (entity)", or "Customer @ main" for a table a database makes
  from an entity (hover an entity for its id); past 20 elements it opens a list with a search box (type to narrow,
  arrows and Enter to pick). When two elements would get the same path (an output pattern with no `{{ }}` gives every element one path) the cell says MQ6020; a
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
  diagnostics are listed above it and marked in the editor at their line. Nothing is written by a preview. The preview
  renders one element and nothing else: the element picker comes from the unit's scope, which the server counts and
  lists without rendering it, and no other unit of the pack is planned or rendered. **Preview a list…** (beside the
  picker) renders up to 20 elements you tick, when you click **Preview**, each under its name; **Back to one** returns
  to the single element. A unit whose one render covers the whole model (`for: model`), a whole database (`each
  database`, such as sql-ddl's schema, migration and seed units, or a selector that returns databases) or a whole
  locale is never rendered as you type: the pane offers **Preview (renders the whole model)** (or "a whole database").
  Once you asked, it renders again as you type while a render takes under a second; a slower render stays up marked
  **Out of date** with **Preview again**. The server keeps what a preview needs (the model resolved once per change,
  shared with validation, and the pack) until the model, the settings, the pack's files or the committed schema
  snapshots change.
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

Custom properties whose schema marks them `x-translatable` (see
[Translatable properties](#translatable-properties-x-translatable)) are translated like the standard fields. Their
field is `properties/<name>`: the Translations section and the translation queue list it under the property's name,
the completeness matrix and MQ7204 to MQ7206 count it, and the `l10n` verbs, XLIFF and CSV exports carry it. A locale
shard keeps the texts in a nested `properties` object, with their fingerprints under `src` as `properties/<name>`. Only
an element's or sub-element's own text is translated: a stereotype's `defaultProperties` text serves every element that
carries the stereotype and stays in the default locale. When a property is no longer marked, its translations become
orphans (MQ7203), and `l10n prune` removes them.

The content of a custom element (see [Custom kinds](#custom-kinds-extensionskindsjson)) is translated the same way. A
top-level string of the custom kind's `properties`, or of a member array's items, marked `x-translatable` is the field
`content/<name>`: on the element for its own content, and on the member for a member's text, so each part of a message
has its own translation. A member's translations sit under the member's id in the shard of its owner's package. The
completeness matrix, MQ7204 to MQ7206, MCP `localization_status`, `get_translations` and `set_translations`, the `l10n`
verbs and the exports count and carry these fields, and templates read them with `translate <element or member>
"content/<name>"`.

## The assistant

The **Assistant** panel (the top bar's assistant button, tooltip "Assistant (Ctrl+I)", or **Ctrl+I**, which also closes it) is a chat about the model
on the right side of the editor, backed by the AI that the host (static-site-hosting) gives the site. Ask it what the model
holds ("which entities reference Invoice?", "why does MQ3005 fire here?"), or ask it to change the model ("add an entity
Shipment with a key and a relation to Order").

**What it can and cannot do.** It reads everything with the same read tools the MCP server has (docs/mcp.md): the project,
the model index and documents, references, the resolved model, database views, validation, JSON schemas, query and binding
SQL, materialize previews, stored generation plans and unit previews. Each tool call shows in the conversation as a compact
row (its name, its arguments and a one-line result) that you can expand. It changes **the model only, and only through
proposals**: it never writes a file itself, never runs or applies generation, and never writes template packs, extension
files, settings or translations.

**Review before write.** A change the assistant suggests arrives as a **proposal card**: a one-line summary and every file it
would create, change or delete, each expandable to the diff view the conflict dialog uses (in one column, read-only). Nothing is written
until you press **Apply**. Apply sends the proposal as one model batch (`POST /api/model/batch`), so it is validated, it is
one undo step (Ctrl+Z takes it back) and every other window sees it at once. **Discard** drops it. A proposal is checked as
a dry run before you see it, and it is pinned to the documents it read: when one of them changed since (your own edit,
another window, git), Apply is refused as a conflict and **Ask again** asks the assistant to read them again and propose
anew. The assistant hears whether you applied or discarded a proposal with your next message. **Apply small changes without
asking** (in the panel, off by default, remembered per browser) applies a proposal of at most three operations and no
deletes as soon as it arrives; it still lands as one undo step.

**Context chips.** Above the message box the panel shows what it will send along: the workspace, the open element, the
selection and a summary of the current problems. Remove a chip to leave that part out of the next message. **Stop** ends
an answer that is still streaming; what was said so far is kept. Conversations are kept on the server, per user and per
project (the newest 50, each capped in size, oldest exchanges first out); pick one from the list at the top of the panel, start a **New conversation**,
or **Clear conversation** to delete it.

**Setting up the provider (in the host, never in Maquettiste).** Maquettiste never holds an AI key. An administrator of the
host sets it up once, signed in to the host's management UI at `http://localhost:<port>/` (the same container and port as the
editor; with the Docker image, the user is `admin` and the first password is in `/data/config/bootstrap-password.txt`, see
docker/README.md):

1. In the host's management UI, **AI › providers**: add a provider (kind `openai` for OpenAI and any compatible service such
   as a LiteLLM proxy, Ollama, OpenRouter or xAI; kind `anthropic` for Anthropic), its base URL, key and default model. The key
   is encrypted by the host and never shown again.
2. In **Sites › maquettiste.localhost › AI**: pick that provider and the model (and, optionally, a site system prompt).

**Settings › Assistant** shows whether the site has a provider ("configured" or "not configured"), the API it speaks
("OpenAI-compatible API" or "Anthropic API") and the model in use, with
a link to the host's management UI when `MAQUETTISTE_HOST_AI_URL` is set. The compose file sets it to `http://localhost:<port>/`,
where the host answers its management UI on the editor's own port (sign in as the host's administrator).
The model must support tools (all current OpenAI and Anthropic models do; on Ollama, Llama 3.1+, Qwen 2.5+ and similar); with
one that does not, the panel says "the site's model does not support tools; choose another in the host's AI settings".
Other provider failures are worded by cause: a conversation too long for the model (start a new one), too many requests
(try again in a moment), a refused key (check the provider in the host) or a provider that is down; each also shows the
provider's own message.
Without a provider, the panel says so and links to Settings › Assistant; the rest of the editor works as before.

**House rules and budgets.** Settings › Assistant also edits the `assistant` section of `maquettiste.json`: **Instructions**
(house rules such as naming or what to avoid, appended to the assistant's system prompt, which already carries the modeling
conventions and the repository's `CONVENTIONS.md`), **Max turns** per answer (10 by default; the last turn must answer in
text), **Token budget per request** (200 000 by default: every turn resends the whole conversation, so the tokens add up; once
spent, the assistant must answer in text) and **Token budget per day per user** (2 000 000 by default, counted on the server,
reset at midnight UTC; once spent, the panel refuses new messages until then). The panel shows today's usage.

**Privacy.** What you type, the context chips you keep, and everything the assistant reads with its tools (element documents,
database views, validation results, SQL, plans) are sent to the provider the host is configured with, under that provider's
terms; nothing is sent while no provider is configured or while you are not using the panel. Viewers may chat and read;
applying a proposal needs the editor role.

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
| `maquettiste init` | Creates `.maquettiste/` (`maquettiste.json`, the JSON schemas for editor completion, the `sql-ddl` starter pack) with the output entries `db` and `src/Generated`. It never reads or writes the repository's ignore file (the `--gitignore` option of earlier releases is gone; a pack's managed block can keep lines there, see "Managed blocks"). A block an earlier version wrote stays as it is: it is yours to keep, edit or remove. `--hooks` installs git hooks that run `generate` after a checkout or merge. `--pack csharp-dapper` or `--pack none` picks another starter; `--mcp`, `--skill` and `--agent-setup` register the agent server, and `--mcp --docker <image>` registers it as a `docker run` of the image (`--runtime podman` for `podman run`; docs/mcp.md). `--skill` writes the modeling skill, `.claude/skills/maquettiste-modeling/SKILL.md`, which belongs to Maquettiste: put your own modeling conventions in `CONVENTIONS.md` in the same folder, which `init` never touches and which agents read after the skill (the MCP conventions resource and prompt end with it). A skill nobody changed is refreshed to the installed version; one edited by hand is left alone, the new version goes to `SKILL.md.new` beside it with a one-line message, and `--force` overwrites it. `init --skill --check` writes nothing and exits 2 when the skill is missing, stale or edited, for CI. The project is named by `--name <name>`, else the `name` of `package.json`, else the git remote's repository name, else the folder name (so a repository mounted at `/repo` in the image keeps its real name). Running `init` again, starting the editor or starting `maquettiste mcp` refreshes the JSON schemas when a new version ships different ones; until then `validate` and `generate` warn MQ1008 and name the files that differ. Running it again keeps what is there. |
| `maquettiste validate` | Validates the model and the packs, and replays every scenario of every process (MQ9301 to MQ9306 and MQ9502 to MQ9507); `--format sarif` for code-scanning tools. `--against <snapshot id or folder>` also compares the model with a baseline (a model snapshot, or a folder holding another copy of the project): a change since it that a migration must follow and no migration fact explains is MQ4102 (see "Migration facts"); a value that names nothing exits 4. |
| `maquettiste generate` | Renders the packs into the output roots of `maquettiste.json`, incrementally: only units whose inputs changed re-render. Prints one line per file (`A` added, `M` modified, `D` deleted, `K` kept). A generated file edited by hand stops the run (exit 3); `--hand-edits overwrite` replaces it. `--against <snapshot id or folder>` diffs the schema (migrations) from a baseline model for this run instead of the committed physical snapshots, and leaves those alone; a value that names nothing exits 4. `--unit <pack/unit or unit>` and `--element <id>` (both repeatable) run only those units and leave every other file as it is (see "Regenerate one element or one unit"); MQ6032 or MQ6033 exits 1, a filter that selects nothing warns (MQ6034) and exits 0. |
| `maquettiste migration list` | The open migration facts in applied order, one line each (database, order, id, fact, state and the intent a pending fact lacks, `stale`, summary; a value map's summary names its type, codes and columns, or a type rename's two names); `--database <name or id>`, `--format json` for scripts. An unknown database exits 1. |
| `maquettiste migration fold` | Removes stale facts and joins consecutive facts on one object into the last one (see the schema changes guide); `--against <snapshot id or folder>` (its facts stay; default: the facts the committed snapshot lists as rendered, else pending facts only), `--database`, `--dry-run`, `--format json`. Exit 3 when a fact file changed meanwhile, 1 when the model has errors outside the migration rules or for an unknown database, 4 when `--against` names nothing. |
| `maquettiste migration ledger` | Prints the migration ledger's DDL (see "The migration ledger" under Migration facts) for a PostgreSQL database: `--database <name or id>` (needed when the model has several), `--grant-read <role>` also prints the read grant, `--format json` prints `{ contractVersion, database, dialect, schema, ddl, grant }`, `--output <file>` writes it under an allowed output root. Exit 1 when the model has no PostgreSQL database, when the database named is missing or not PostgreSQL, or when several exist and none is named. |
| `maquettiste generate --check` | Renders every output without writing and exits 2 when any file would be added, changed or deleted: the CI gate when the outputs it guards are committed (otherwise run `generate`, then compare the working tree). `--roots` of earlier releases is gone. |
| `maquettiste generate --watch` | Regenerates on every change under `.maquettiste/` (a save in the editor, a template edit) until Ctrl+C. |
| `maquettiste format` | Rewrites every model file (`maquettiste.json`, `model/**`) in canonical form, the form the editor writes, and prints the count; hand-written files then stop reporting MQ1003. A file that does not pass its schema is left as it is and named. `--check` writes nothing and exits 2 (drift, as `generate --check` does) when a file would change. |
| `maquettiste l10n status` | The default locale, the declared locales and, per translated locale and shard, how many texts are translated, missing and stale (`--format json` for scripts). |
| `maquettiste l10n export fr` | The French texts as XLIFF 2.1 for translators (`--format csv` for a spreadsheet), on stdout or into `--out <file>`. |
| `maquettiste l10n import fr <file>` | Previews what an XLIFF or CSV file adds, changes and confirms (a stale text the translator marked `translated`, `reviewed` or `final` without changing it), and names the units that match nothing; `--apply` plans again and writes it as one save, refused (exit 3) when a shard changes while the command runs; it prints what it wrote, which can differ from an earlier preview if the files changed in between. `--check` exits 2 when the file would change something. |
| `maquettiste l10n prune` | Lists the orphan translations (MQ7203: an entry whose element is gone, or a field its element does not have); `--apply` removes them in one save, as the other `l10n` and `seed` verbs write only with `--apply`. `--check` exits 2 when there are orphans. |
| `maquettiste l10n set-default fr` | Previews making French the default language: each French text moves into the element files and the text it replaces becomes an English translation; `--apply` writes it all in one change to review in git. Other locales' translations turn stale where their source text changed. Sidecar descriptions and orphans are not moved: they stay in the new default's locale folder, which is no longer loaded (MQ7202), and a sidecar-described element keeps its old-language description; the command lists them as skipped. Move or delete them by hand, or run `l10n prune` before switching. |
| `maquettiste seed new <type>` | Creates a reference type's seed (by id or name), named after the type, with the columns code, label and description and no rows; a type that has a seed keeps it. |
| `maquettiste seed export <seed>` | A seed's rows as CSV (the seed's id or name, or the id or name of the element it seeds); `--locale fr` adds the French label and description columns, `--out <file>` writes a file. A foreign-key column is headed by the foreign key's name and holds the natural key (a composite one as JSON array text); a seed with a rows script exports the rows its script returns, with their derived ids. |
| `maquettiste seed import <seed> <file>` | Previews a CSV import (rows match by `@id`, else by `@code`): added, changed, removed and blocked rows; `--mode replace` also removes the rows the file leaves out, except rows still referenced; `--apply` plans again and writes it, refused (exit 3) when the seed file changes while the command runs; it prints what it wrote, which can differ from an earlier preview if the seed changed in between. A seed with a rows script takes no import (exit 4). A line whose `@code` is a former code of the type is skipped and listed with MQ7019. |
| `maquettiste seed preview <seed>` | One line per row of the seed, `<key> <id>: <column>=<value> ...`: the rows as written, or as its rows script returns them. `--database <name or id>` gives them as that database receives them: stable ids, the values natural keys find, by column name. Only the seed's script and those of the seeds it names run. `--format json` prints the rows with their keys and refs. An unknown seed or database exits 4; a failed script exits 1. |
| `maquettiste process simulate <process>` | Runs a process (by id, name or model path) through the engine from its initial state: `--inputs <file>` (or `-` for stdin) holds an array of steps, or `{ "start": { "context": {...}, "at": "..." }, "steps": [...] }`, each step a scenario step without `expect`; `--scenario <name>` runs a scenario's steps first. Prints one line per input (accepted or refused, and the active states after it), then the configuration, what can happen next and the clock; `--format json` prints the whole trace, `--from <n>` only from input n. The same inputs always give the same trace. |
| `maquettiste process record <process> <name>` | Replays `--inputs <file>` and prints the scenario it would write, each step's expectations and the outcome filled from the replay; `--apply` writes it under `model/scenarios/<process>/`. |
| `maquettiste process verify [<process>...]` | Replays the scenarios of the processes named (all processes when none is) and prints `pass` or `FAIL` per scenario with the first failing step and rule; exits 1 when one fails. `--format json` for scripts. |
| `maquettiste process export <process>` | The process as an XState machine config (`--format xstate`, the only format), on stdout or into `--out <file>`. What has no XState home travels under `meta.maquettiste`, so importing the file back over the process gives the same file. |
| `maquettiste process import <file>` | Previews importing an XState config: `--domain <package>` (with `--name`, `--use lifecycle\|orchestration`, `--subject <entity>`) for a new process, or `--into <process>` to re-import over one, keeping the ids of what matches. Prints the diagnostics and how many ids are created and removed; `--apply` writes it as one change, refused (exit 3) when the process changed while the command ran and exit 1 when the import has errors. `--format json` prints the document. |
| `maquettiste process sync-enum <process>` | Previews making a lifecycle's bound enum follow its root-level states (members added, removed, reordered, and removals refused because a default, allowed values, a seed cell or a scenario still uses the member); `--apply` writes it; `--check` exits 2 when the enum is out of sync. A refused removal exits 1: change the uses first. |
| `maquettiste model export` | Writes the model as data for another system: the canonical document of every element, or of the ones `--kind`, `--package` (id or name), `--tag`, `--category`, `--stereotype`, `--query` (name contains) and `--ids a,b,...` select, as one JSON array (`--format json`, the default) or one document per line (`--format ndjson`, for a pipeline); `--fields name,attributes` keeps only those members of each document (`id` and `kind` always), `--out <file>` writes a file. With `--resolved` it writes the resolved model instead, what templates read, as flat records: `--scope entities` (attributes resolved, inherited ones marked, keys, relations and mappings by id), `databases` (each database's tables, views, sequences, routines, database types, SQL objects and queries), `tables`, `routines`, `database-types`, `sql-objects`, `queries`, `migrations` (open migration facts by database, in applied order), `processes`, `operations` (each operation's input members, outcomes with what they return, errors with their codes, and the events it fires and what it requires, by id) and the other kinds, `all` by default; `--database <id or name>` keeps what is mapped to that database. A model with errors cannot be resolved: the errors go to stderr and the command exits 1. |
| `maquettiste model stats` | The kinds of element the model holds and how many of each; `--by package` adds the counts per package, `--format json` for scripts. |
| `maquettiste model calculated` | The calculated values (see "Calculated values") of the elements `--element <id>` names (repeat it; an attribute id gives one line per entity that holds the attribute), evaluated for them only: one line per object and value, such as `entity Invoice 01J92P0V0FJ23CGSNKM7P1W5V7 publicName = "invoices"`; `--name <name>` (repeatable) keeps only those values, `--format json` prints the answer `get_calculated` gives. A value that fails is reported on stderr and the command exits 1. |
| `maquettiste model delete <id or name>` | Deletes an element with `--resolution refuse|remove-references|delete-dependents` (default refuse); `--dry-run` prints the delete plan: what would be deleted, cleared or removed, and what blocks it; `--format json` for scripts. Exit 1 when refused or invalid, 3 on a conflict. |
| `maquettiste model apply <batch.json>` | Applies a batch file all or nothing, as the agent server's `apply_batch` does: `{ "operations": [...] }` or the bare array of creates, updates, deletes and the schema, materialize, process, tag and reference-type refactor operations. Id fields take ids, qualified names such as `@table:billing.notes` or `@entity:Billing.Invoice`, and placeholders (`"as": "$pm"` names a new element for the whole batch), and the engine mints every missing id, so a batch needs no id written by hand (docs/mcp.md, "Batches by name"). A name or placeholder that finds nothing is MQ1012, a name that finds several is MQ1013 (use a longer form or the id), a misused identity (a placeholder declared twice, a misplaced `as`, a sub-element dropped and added again without its id) is MQ1014 and a malformed or misplaced name is MQ1015; any of them writes nothing. Prints a line per path written or deleted, then each name and placeholder with its id, and per reference-type refactor `<op> <type>: <n> uses, <m> rows moved, <k> rows removed, <t> translations removed`; `--dry-run` writes nothing and prints the diffs, the names, and a `+` or `-` line per diagnostic the change adds or removes; `--format json` prints the `apply_batch` answer. Exit 1 when the file is not a valid batch or the batch is invalid or refused, 3 on a conflict. The agent server's `patch_element` changes part of one element with an RFC 6902 JSON Patch instead; a patch that cannot be applied (an unknown operation, a path that does not resolve, a failed `test`, a change to `/id` or `/kind`) is MQ1016 and writes nothing. |
| `maquettiste binding drift [<entity>...]` | Per binding, the columns of its table file added, renamed, retyped and removed since its provenance was recorded, from files only (`--database`, `--all` for bindings in step too, `--format json`); `--check` exits 2 when a listed binding drifts. See "Provenance and drift" under Binding entities. |
| `maquettiste binding map --database <db> <entity...>` | Maps every attribute a binding leaves unmapped to the free column of the same name, ignoring case and separators, and records provenance; `--dry-run` shows the change. A refusal is MQ4089. |
| `maquettiste binding resync --database <db> <entity...>` | Brings bindings into step with their tables in one batch (renames, retypes, added and removed columns); `--keep-names` keeps every attribute name, `--column` limits one entity to some columns, `--dry-run` shows the change. A refusal is MQ4089. |
| `maquettiste snapshot create <name>` | Takes a snapshot of the whole model: a zip under `.maquettiste/model-snapshots/` named `<name in kebab case>-<yyyymmdd-hhmmss>.zip` (the snapshot's id) holding `maquettiste.json`, `model/`, `extensions/` and `branding/`; `--packs` holds the template packs too, `--description <text>`, `--format json`. |
| `maquettiste snapshot list` | The snapshots, newest first, with their elements and flags (`[published]`, `[packs]`, `[before-restore]`); `snapshot show <id>` prints one snapshot's metadata, `snapshot delete <id>` deletes it. |
| `maquettiste snapshot compare <from> [<to>]` | What differs between two snapshots, or a snapshot and the working model (`working`, the default `to`): counts per kind, then `+` added, `-` removed and `~` changed elements (a rename and a moved file say so) and the other documents; `--element <id>` prints that element's changed fields; `--format json`. |
| `maquettiste snapshot restore <id>` | Previews what restoring the snapshot changes; `--apply` takes a safety snapshot of the working model (`before-restore-<time>`, restore it to undo) and then makes the working model the snapshot's; `--packs` restores its packs too. Refused (exit 4) while a generation run holds the run lock. |
| `maquettiste snapshot export <id> --out <file>` | Copies a snapshot's archive out of the model folder, to mail or share; `snapshot import <file>` checks an archive (paths, sizes, metadata, every document parses and is canonical) and stores it as a new snapshot without touching the working model (exit 1 and the reasons when refused). |
| `maquettiste pack new <name>` | Scaffolds a pack under `.maquettiste/templates/<name>/` (`--from empty`, `sql-ddl` or `csharp-dapper`). Give it an `output` under an allowed root in `maquettiste.json` before the next `generate` (packs/README.md). |
| `maquettiste pack test [<pack>...]` | Runs the packs' own tests: each case under `tests/` against its `expected/` tree, then the helper tests (`--case <name>` for one case, `--update` to rewrite `expected/`). Exit 0 when all pass, 1 when any fails (see "Test a pack"). |

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
# entity                         8
# ...
maquettiste model export --kind entity --fields name,attributes --format ndjson > entities.ndjson
maquettiste model export --resolved --scope tables --database main --format ndjson > tables.ndjson
# one line per table, each with the table's columns, keys, foreign keys and indexes:
# {"id":"01J92P0V0ETQKXXP951CMMNHH3@01J92P0V1QRN2181XM2ZWE02W4","kind":"table","name":"customers","database":"01J92P0V1QRN2181XM2ZWE02W4","table":{...}}
```

### Snapshots

A snapshot is a named copy of the whole model at one moment, kept as one zip file under `.maquettiste/model-snapshots/`:
the model documents, `maquettiste.json`, the extensions and the project icon, and the template packs only when you ask
(`--packs`), so a snapshot taken with them generates exactly what it generated then. The same model always gives the same
bytes. Take one before a large change, compare it with the working model as you go, and restore it if the change goes
wrong:

```sh
maquettiste snapshot create "Before the billing split"
# Took snapshot before-the-billing-split-20261005-120000 (36 documents).
maquettiste snapshot compare before-the-billing-split-20261005-120000
# before-the-billing-split-20261005-120000 -> working: 1 added, 0 removed, 2 changed, 0 other documents
#   entity           +1 -0 ~2
# ~ entity Client (01J92P0V0ETQKXXP951CMMNHH3) (was Customer) (moved from model/entities/customer.json)
maquettiste snapshot restore before-the-billing-split-20261005-120000 --apply
# Restored before-the-billing-split-20261005-120000: 3 documents written, 1 deleted.
# Restore snapshot before-restore-20261005-121500 to undo this restore.
```

A restore never asks the editor's undo: it first takes a snapshot of the working model as it was, and restoring that one
puts everything back. Editor windows that are open see the restored model at once. Snapshots are files in the model folder,
so they travel with the repository unless the project ignores `.maquettiste/model-snapshots/`; to send one to someone
without the repository, `snapshot export` it and have them `snapshot import` the file. Agents have the same operations as
MCP tools (`list_snapshots`, `create_snapshot`, `compare_snapshots`, `restore_snapshot`; docs/mcp.md), and the editor's API
serves them under `/api/snapshots`.

**In the editor.** Click the project name in the top bar. The picker lists the working model and its snapshots, newest
first, with their date and time, who took them, their description and a **Published** badge (a safety snapshot says
**Safety**, one that holds the packs says **Packs**); the search box finds one by name, description or author.

- **Take snapshot…** asks for a name and a description. **Include the template packs** is off: tick it only when generating
  from the snapshot later must reproduce exactly.
- Click a snapshot to **open it as of**: the whole editor shows the model as it was, read-only. The top bar says *Viewing
  snapshot <name> (<date>) — read-only* with **Compare with working**, **Restore…** and **Back to working**; the address
  carries `?snapshot=<id>`, so a reload or a link someone else opens shows the same snapshot. The domain model, the
  diagrams, the databases, reference data, the inspector and the problems all read the snapshot; fields are read-only,
  undo is off, and nothing can be created or deleted. Generate and Settings work on the working model only and say so.
- The **…** menu of a snapshot has **Compare with working**, **Compare with…** (another snapshot), **Restore…**, **Export
  (.zip)**, **Rename or describe…**, **Publish** (or **Unpublish**) and **Delete…**. **Import snapshot…** (the arrow
  beside Take snapshot) adds an exported `.zip` as a new snapshot without touching the working model; through the editor an
  archive can be up to 512 MiB (the host's request limit, the same as the engine's), and `maquettiste snapshot import`
  has no such limit.
- **Compare** opens over the screen: the changed elements by kind with their counts (`+` added, `−` removed, `~` changed;
  a renamed element says *renamed from …*), a filter by change and a search by name, and the other changed documents
  (settings, translations, extensions, branding) on their own tab. Pick an element to see the fields that differ and both
  documents side by side. The list comes in pages of 500 (**Load more**); the filters and the search apply to what is
  loaded. Nothing is compared until you ask.
- **Restore…** says what happens first: a safety snapshot of the working model (`before-restore-<time>`), then the working
  model replaced by the snapshot, the packs only when the snapshot holds them and you tick the box, and every open window
  seeing the change. After it, **Undo: restore before-restore-<time>** puts the model back. While a generation run is
  writing, a restore is refused and nothing changes.

Who may do what follows the editor's roles: a viewer opens and compares; an editor also takes, renames, publishes and
exports; a maintainer also restores, deletes and imports. An action your role does not allow is greyed out and its tooltip
says which role it needs.

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
(either form) and removes the `mcp.sh` wrapper that earlier versions wrote. Add `--skill` to refresh the modeling skill too; your
own conventions belong in `CONVENTIONS.md` beside it, which the refresh never touches. docs/mcp.md has the details.

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
- In a seed CSV, a table seed's foreign-key column is headed by the foreign key's name (its id when it has none) and
  holds the referenced row's natural key, a composite one as JSON array text. A seed whose rows a script computes
  exports those rows with their derived `@id`, and refuses an import (400).
- `GET /api/reference-types/<id>/usage` lists the attributes that use a reference type and how each database stores it.
- `GET /api/model/index?locale=fr` fills the display names from French, falling back as the settings say.
- A seed saved through the API (an element save, `POST /api/model/batch`) or by an agent may give its `columns` and its
  `key` by name (a table's column and unique constraint names, an entity's attribute names and to-one end roles) and a
  row's cells as a `values` object keyed by those columns. The seed is stored with ids and one cell per column; a name
  that finds nothing, or several, is MQ1002 and nothing is written (docs/mcp.md, "Seeds by name"). Templates find a
  seed by its name with `seed_named "<name>"` and one of its rows by key with `seed_row`.

**Rename, retire or merge codes.** Databases store reference rows by code, so a code change moves data. Four batch
operations (`POST /api/model/batch`, MCP `apply_batch`, `maquettiste model apply`) make the change and record it once
for every environment: `rename-code` (`id`, `code`, `to`) gives a row a new code, `retire-code` (`id`, `code`, `to`)
removes a row and moves its uses to another live code, `merge-reference-types` (`id`, `target`, optional `codes`) moves
every use of one type to another, and `rename-reference-type` (`id`, `name`) renames a type. Each rewrites every
default, `allowedValues` entry and seed cell that names a moved code, keeps the old code in the type's `formerCodes` (`{
code, replacedBy }`, a row id), and records one `value-map` migration fact per database that stores the codes, which
sql-ddl turns into a data step on PostgreSQL. A dry run lists what it would rewrite (`usage`) and the facts. One
refactor per type per batch; a refusal is MQ7020 with the reason. A former code stays understood: a seed import skips a
row that names one, `names-to-ids` turns it into its row's id, and csharp-dapper keeps an obsolete alias for it; a
default, `allowedValues` entry or seed cell that names one is MQ7019. A former code that is also a live code, is listed
twice or does not fit the type's code is MQ7017, and a `replacedBy` that is not a row of the type is MQ7018. The
[reference data guide](guides/reference-data-and-seeds.md#15-rename-retire-or-merge-codes) walks through all four on a
billing model. The editor has no menu for them yet: the batch endpoint takes them, and the type's JSON form edits
`formerCodes`.

Enums are stored as `int` or `string` columns. Storing an enum through a lookup table (`enumStorage` or a mapping's `storage`
set to `lookup`) is no longer offered: a file that still uses it does not load and reports error MQ7012. Convert the enum to a
reference type (its members become the rows of a seed) and pick the reference type's storage strategy, for example a lookup
table, under `referenceData.strategies`.

Tags and categories can be declared globally (`model/vocabularies/tags.json`, `categories.json`) and per domain: a tag vocabulary
or category tree with a `package` belongs to that domain and the domains nested in it (`model/vocabularies/<name>-tags.json`,
`<name>-categories.json`), at most one of each per scope (MQ1009). An element may use the tags and categories of its own domain,
of every enclosing domain and the global ones; one declared only in another domain is error MQ2008, and a domain vocabulary may
not redeclare a key or category name of the global vocabulary or of an enclosing domain (MQ3021).

**Tags already in use.** A model can use tags before it has a vocabulary; nothing checks them then. The first tag you add on
Settings › Tags (or on a domain's Tags tab) creates the vocabulary and declares the tags already in use with it, so none of them
turns into a note. Later, a tag used but declared nowhere on the element's chain is listed under **Tags in use, not declared**,
with its uses: **Add** declares it (or **Add all**). In the Problems panel each such tag is one note (MQ2006) with its count, not
one per use.

**Rename or remove a tag everywhere.** Each tag shows how many uses it has (hover for some of the elements). The pencil renames it
in the vocabulary and in every use; renaming to a key the vocabulary has already merges the two. The bin removes a tag nothing
uses at once, and asks first for one in use. Check several tags (declared or not) and **Remove everywhere…** removes them all from
the vocabulary and from every element and sub-element that uses them. Each is one undo step. On a domain's Tags tab only the
domain's own uses change: a tag the domain declares itself is kept when the global one with the same key goes. From the command
line or an agent: `apply_batch` with `{ "op": "retag", "tags": [...], "name": "<new key>", "package": "<domain>" }`
(`name` to rename, `package` for a domain's vocabulary), and `tag_usage` for the counts.

**Turn tags into a property.** Tags that carry a value, such as `status:draft` and `status:paid`, read better as a
custom property once one exists. The batch operation `tags-to-property` moves them:
`{ "op": "tags-to-property", "prefix": "status:", "name": "status" }` takes every tag that starts with `status:` off
each element and sub-element that holds it (in the scope `retag` would change; `package` for a domain's vocabulary) and
writes what follows the prefix into its `status` property. A property schema that applies to the holder must declare
the property, and it decides the value: a string takes one suffix, an array of strings all of them in their order, an
integer, number or boolean one parsed as written (`3`, `2.5`, `true`), an `enum` one of its values, and an `x-ref` the
id the suffix names, matched as `names-to-ids` matches names. The moved tags leave the vocabulary too, except a tag
that something without properties still uses (a relation end, for example): its declaration stays. With
`"elementIds": [...]` only those elements change and the declarations stay. Nothing is written when one holder cannot
take its value: no schema declares the property, the suffixes do not fit (two for a string, `big` for an integer), or
the holder already has another value there; each such holder is MQ5001 at its property. No matching tag is MQ1002.

## Extending the model and the generation

Four kinds of files let a project add to what Maquettiste knows, without changing Maquettiste itself. All four are plain
files under `.maquettiste/`, so the editor, the command line, an agent and git see the same thing:

| What | Where | What it adds |
| --- | --- | --- |
| Custom properties | `.maquettiste/extensions/<name>.json` | Fields of your own on elements, shown in the inspector, checked by validation, read by templates |
| Script rules | `.maquettiste/extensions/rules/<name>.js` | Checks of your own, reported like the built-in rules |
| Rows scripts | `.maquettiste/extensions/seeds/<name>.js` | A seed's rows, computed from the model |
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
`defaultProperties` show as the field's placeholder and apply when the element sets no value of its own. Keys no schema
declares are the element's property bag, edited under the custom properties (free keys, text values unless you pick
Number or True/false) and read by templates the same way. For attributes and enum members the properties are validated
and reach the templates the same way; edit them in the element's JSON. A table column's own properties edit in the
table inspector's column section, as a property bag. Validation reports a value that does not match as MQ5001 on `/properties/<name>`, and templates read the merged value
as `entity.properties.retentionDays`.

#### Translatable properties (`x-translatable`)

A top-level string property of an extension file, or of a stereotype's `parameters`, can be marked
`"x-translatable": true`. Its text is then translated per locale like a display name or a description (see
[Translating the model in the editor](#translating-the-model-in-the-editor)), and templates read a locale's text with
`translate(entity, "properties/<name>")`, which walks the fallback chain to the element's own value. For example,
`.maquettiste/extensions/invoice-texts.json` gives every entity a footer text that each locale can translate:

```json
{
  "name": "invoice-texts",
  "appliesTo": { "kinds": ["entity"] },
  "properties": {
    "footer": { "type": "string", "x-translatable": true }
  }
}
```

The holders that are translated are the ones localization knows: the domain kinds and their attributes, enum members,
process states, transitions and events. The keyword anywhere else is an error, MQ7212: under a nested schema, on a
property that is not a string, or with a value that is not `true` or `false`. A mark on a schema whose `appliesTo` kinds
all lack translations (tables and other physical objects, mappings, diagrams, the vocabulary containers) is the warning
MQ7213, since it does nothing there. Both are reported on the extension file, or on the stereotype at
`/parameters/properties/<name>/x-translatable`.

#### Typed links (`x-ref`)

A property schema may type its value as a link with the `x-ref` keyword (`common.json#/$defs/xRef`): on a string, or on
an array's items, `"x-ref": { "kind": "entity" }` says the value is the id of an entity. The value stays an id, so a
rename changes nothing in the property, and where used (`get_references`) lists it with the schema that types it.

| Key | Meaning |
| --- | --- |
| `kind` | Required. An element kind (`entity`, `table`, ...), a sub-element kind (`attribute`, `column`, `enum-member`, `row`, ...) or a list of them. `operation`, `outcome`, `error`, `member`, `custom` and `custom:<type>` are accepted now; their values dangle (MQ2001) until those kinds arrive. |
| `of` | Sub-element kinds only. `"$element"`: a member of the element that holds the value, of its base entities or of its stereotypes (a column property may name a sibling column). An element id: a row of a seed of that element. |
| `sameDatabase` | Physical kinds only. The target is in the database of the element that holds the value. |
| `optional` | Whether a delete with `remove-references` may clear the value. Default `false`. |

For example, `.maquettiste/extensions/listing.json` lets an entity name the attribute its lists sort by:

```json
{
  "name": "listing",
  "appliesTo": { "kinds": ["entity"] },
  "properties": {
    "defaultSort": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element", "optional": true } }
  }
}
```

Validation checks each value: a name or other text where the id belongs is MQ2010, an id that does not exist MQ2001, an
id of another kind MQ2002, and a target out of scope MQ2011 (a member of another element, a row of another list, another
database, an element with no single database for `sameDatabase`, or a stereotype default under `of: "$element"` or
`sameDatabase`, since a default serves many elements). A malformed or misplaced `x-ref` (an unknown kind or key, `of` or
`sameDatabase` where they do not fit, an `of` naming no element, a keyword not on a string or under `allOf`, `anyOf` and
the other applicators the link walk does not follow) is MQ2009 on the extension file.

**Names to ids.** A value written as a name (by hand, or before the schema typed the property) stays MQ2010 until it
becomes an id. The batch operation `names-to-ids` converts them all at once: `apply_batch` or `maquettiste model apply`
with `{ "op": "names-to-ids" }` looks at every document with typed values, stereotype defaults included, and
`"elementIds": [...]` (ids or names such as `@entity:Invoice`) limits it to those elements. Each name is matched against
what the keyword allows and its scope admits: an element by its name or a longer form such as `Billing.Invoice` (a
stereotype by its key), and within `of` a member by its own name (`issuedOn`), a column by its physical name and a row
of the list by its code. A value that finds one target gets its id; one that finds nothing, or several, is MQ2010 at the
value, and the whole batch is refused, so nothing changes until every name finds one target. A value that is already an
id is left alone, so a second run writes nothing. Preview it with `--dry-run` (`dryRun` for `apply_batch`).

Deletes treat a typed value as a reference. A plain delete refuses while one names the element. `remove-references`
clears an optional value and refuses a required one; `delete-dependents` clears it either way and keeps the element that
holds it. Either refuses (MQ2001, naming the property and the schema) when a schema that declares the property would
fail without the value: its `required` lists the property, or an array drops below its `minItems`. A save that removes a
sub-element (an attribute, a column, a member) clears the optional values that name it in every file, the saved one
included; a required value makes the save invalid, with MQ2001 at the value.

A pack's `parameterSchema` takes `x-ref` too. Its values are settings in `maquettiste.json`, so the pack load checks
them (`maquettiste validate`, and the MCP `plan` and `get_pack`): MQ2010, MQ2001, MQ2002 and MQ2011 at
`/packs/<name>/parameters/<key>`, and MQ2009 in `pack.json`, where `of: "$element"` and `sameDatabase` do not fit
because no element holds a parameter. A plain delete of an element that a parameter value names refuses with MQ2001 in
`maquettiste.json`. `remove-references` (an optional value) and `delete-dependents` remove the entry, or its array item,
in the same change, unless the parameter schema requires the parameter or its `minItems` would fail; a save that removes
a sub-element does the same with optional values. The delete plan lists those entries under `settings`, and warns about
an `of` in an extension or pack file that names the deleted element: once the element is gone, that keyword is MQ2009.

### Stereotype rules and parameters

A stereotype (`model/vocabularies/stereotypes/`) can say how it combines with others, and can type the custom properties
of the elements that carry it. Until the stereotype form follows, edit these keys in the stereotype's JSON.

| Key | Meaning |
| --- | --- |
| `group` | A label, not a link. An element carries at most one stereotype of a group (MQ2013). |
| `requires` | Stereotype keys an element that carries this one must also carry (MQ2014). |
| `conflicts` | Stereotype keys an element that carries this one must not carry (MQ2015). |
| `implies` | Stereotype keys applied with this one: an element that carries it carries them too. |
| `parameters` | `{ "properties": {...}, "required": [...] }`, shaped like an extension file: one JSON Schema per property name (`x-ref` and `x-translatable` included) and the names each carrier must have. |

`requires`, `conflicts` and `implies` hold keys, as an element's `stereotypes` does, and the keys are references: a
stereotype they name cannot be deleted plainly. An element's stereotypes are its own plus the ones they imply. The
implied ones come first and its own after, so where two give the same default property, its own stereotype wins, and an
element that lists an implied stereotype itself keeps its own entry. Implied stereotypes give virtual attributes, table
storage and defaults exactly as listed ones do, and templates see them in `stereotypes`, each implied one with
`implied_by` (the key of the stereotype that implies it). Implication is one level: a stereotype that implies one that
implies others is MQ2017, which also refuses a cycle.

The rules judge every element on its own and implied stereotypes together. Two of one group are MQ2013, a missing
required one MQ2014 and two that conflict MQ2015, each at the later stereotype's entry in the element's `stereotypes`
(for an implied one, the entry of the stereotype that implies it). An implied stereotype that does not apply to the
element's kind is MQ2004 at the implying entry. On the stereotype itself, MQ2016 reports a `requires`, `conflicts` or
`implies` entry that names an unknown key or the stereotype itself, a conflict with a stereotype it requires or implies,
and an implied stereotype of its own group. A save is judged on every element the change reaches, so giving an implied
stereotype a group that one of its carriers already fills is refused on that carrier.

For example, a `deletion` group keeps soft and hard deletes apart, and `audited` types its retention period:

```json
{
  "kind": "stereotype",
  "key": "audited",
  "name": "Audited",
  "appliesTo": ["entity", "table"],
  "parameters": {
    "properties": {
      "retentionDays": { "type": "integer", "minimum": 1 }
    },
    "required": ["retentionDays"]
  },
  "defaultProperties": { "retentionDays": 2555 }
}
```

Values stay in each element's `properties`, where templates read them merged with the defaults
(`table.properties.retentionDays`). A value that fails the parameters is MQ2018 at `/properties/<name>`; a required
parameter that neither the element nor a default sets is MQ2018 at the stereotype's entry. Defaults live only in
`defaultProperties` (a `default` keyword in the schema is ignored), and each is checked against its own property schema
without `required`, so a stereotype may leave a required parameter to the elements that carry it (MQ2018 at
`/defaultProperties/<name>`). `parameters` that is not a valid schema, or a `required` that names an undeclared
parameter, is MQ2019; a malformed `x-ref` in it is MQ2009 on the stereotype. MCP `get_schema` lists the parameters of
the stereotypes that may apply to a kind under `stereotypeParameters`.

### Custom kinds (`extensions/kinds/*.json`)

A project can declare kinds of its own for facts no built-in kind holds, such as settings, messages or feature flags.
Each kind is one file, `.maquettiste/extensions/kinds/<type>.json`, and its elements are files of `kind: "custom"` under
`model/custom/<type>/`. An element of a custom kind has what every element has (an id, a name, a package, a description,
tags, stereotypes) and a `content` object that the kind's schema checks. The engine gives the content no meaning; it
indexes it, checks it, translates it and hands it to templates.

| Key | Meaning |
| --- | --- |
| `type` | The kind's key, kebab-case, equal to the file name (`feature-flag` in `kinds/feature-flag.json`). Elements name it in their `type`. |
| `displayName`, `pluralName` | How tools and templates call the kind; absent, the type and its plural. |
| `description` | Text for people. |
| `properties` | One JSON Schema per content key, as in an extension file, with `x-ref`, `x-translatable` and `x-member`. |
| `required` | The content keys every element of the kind must set. |

For example, a `feature-flag` kind and one flag in the Billing domain:

```json
{
  "type": "feature-flag",
  "displayName": "Feature flag",
  "pluralName": "Feature flags",
  "properties": {
    "enabled": { "type": "boolean" },
    "rollout": { "type": "integer", "minimum": 0, "maximum": 100 },
    "owner": { "type": "string", "x-ref": { "kind": "actor", "optional": true } }
  },
  "required": ["enabled"]
}
```

```json
{
  "kind": "custom",
  "name": "early_invoicing",
  "package": "<id of Billing>",
  "type": "feature-flag",
  "content": { "enabled": false, "rollout": 10 }
}
```

A custom element's name is unique per type and package. An `x-ref` in its content is a reference like any other: the
element it names cannot be deleted plainly, and `get_references` lists the link. An `x-ref` anywhere in the project,
in a custom property schema too, can name a custom kind as `{ "kind": "custom:feature-flag" }`.

**Members.** An array marked `"x-member": true`, whose items are objects, makes each item a member: a sub-element with
an id the engine mints on save, so the item schema declares no `id`. Members nest, and a member whose item schema
declares a string `name` is named by it; two members of one array may not share a name (MQ3001). A content value with
`"x-ref": { "kind": "member" }` names a member of any custom element; with `"of": "$element"`, a member of the same
element (MQ2011 otherwise). Removing a member that a required value names is refused, and a delete with
`remove-references` clears the optional ones.

**Rules.** A custom kind file that is invalid (its `type` differs from its file name, its `properties` do not compile,
its `required` names an undeclared key) is MQ1017, a misplaced `x-member` MQ1018 and a misplaced `x-translatable`
MQ7212. An element whose `type` names no custom kind file is MQ1019, and its content is not checked; content that fails
the schema (a required key missing, an undeclared key, a value of the wrong shape, a member without an id) is MQ3033.
Deleting or moving a custom kind file that elements use is refused, with one MQ1019 per element.

**Names.** Every name of a custom element carries its type, because names are unique per type:
`@custom:feature-flag.early_invoicing` or `@custom:feature-flag.Billing.early_invoicing` in a batch; the plain name is
not a form. A member adds its name, and a nested one its parents' names: `@member:message.invoice_sent.subject`. The
ids a batch mints for a custom element and its members come back under `ids.minted` with their longest form.

**Templates.** A unit `"for": "each custom:<type>"` renders once per element of that kind, with the variable `custom`:
the element's common fields, `type`, `type_display_name`, `type_plural_name`, `content` (the keys as written, a member
array as a list of members) and `members` (every member at any depth, a member before its nested ones). A member has
`name`, `array`, `owner`, `parent` and `content`. From any unit, `custom_named "<type>" "<name>"` finds an element by
name (`"Billing.early_invoicing"` qualifies it; `null` when none matches), and `lookup` takes the id of a custom
element or a member. A unit renders again when an element it read changes, and when the custom kind file of a
`custom` record it read changes. MCP `get_template_context` lists the kind's content keys under `custom.content`.

For example, a pack unit that writes one line per flag:

```json
{ "id": "flag", "template": "flag.scriban", "for": "each custom:feature-flag", "output": "src/Generated/flags/{{ custom.name }}.txt" }
```

```scriban
{{ custom.name }}={{ custom.content.enabled }} ({{ custom.content.rollout }} %)
```

**Translations.** A top-level string of the content, or of a member's items, marked `x-translatable` is translated
as the field `content/<name>` (see [Translating the model in the editor](#translating-the-model-in-the-editor)).

**Tools.** MCP `get_schema` takes `custom:<type>` and returns the element schema with the kind's content. The `kind`
filter of `get_model_index` and `get_elements`, and `--kind` of the CLI's `model export` and `model stats`, take
`custom` for every custom element and `custom:<type>` for one kind. `get_model_kinds` counts one `custom:<type>` row per
kind, and `get_resolved_model` has the scope `custom-elements`. The extension file tools list, read, write, move and
delete `kinds/<type>.json`, and `format_model` writes them in canonical form. An operation's `requires` can name a
custom element, a flag the operation needs for instance, and deleting the element is then refused while the operation
names it.

In the editor a custom element shows as "Custom element" and is edited as JSON. A form driven by the kind's schema, an
explorer section per custom kind, a member list editor and a custom kind editor on the Extensions screen come later.

### Stereotypes that contribute structure

Many tables repeat a few small structures: audit timestamps, a row version, an owner column with its foreign key. A
stereotype that applies to tables can carry them as **members**: `columns`, `primaryKey`, `uniques`, `foreignKeys`,
`checks` and `indexes`, written as a table file writes its own. Every table that carries the stereotype inherits its
members, whether the table is designed, imported or synthesized (projected by the database from an entity or a
relation), so the structure is written once and a change to it reaches every table. Until the stereotype form follows,
edit these keys in the stereotype's JSON. In billing terms, `audited` keeps its attributes and gains two columns and an
index:

```json
{
  "kind": "stereotype",
  "key": "audited",
  "name": "Audited",
  "appliesTo": ["entity", "table", "column"],
  "columns": [
    { "id": "01JC7AVD1TC0000000000000C1", "name": "created_at", "type": "datetimeoffset", "nullable": false,
      "defaultSql": { "*": "CURRENT_TIMESTAMP" } },
    { "id": "01JC7AVD1TC0000000000000C2", "name": "updated_at", "type": "datetimeoffset" }
  ],
  "indexes": [ { "id": "01JC7AVD1TC0000000000000X1", "columns": [ { "column": "01JC7AVD1TC0000000000000C1" } ] } ]
}
```

Members differ from a table's own in a few ways, which MQ2024 to MQ2026 check on the stereotype:

- A stereotype whose `appliesTo` leaves out `table` has no members, and a member column has no `attribute` or
  `sequence` (MQ2024).
- A member key, index or check names only the stereotype's own columns (MQ2025) and has no `name` (MQ2024): each table
  names it by the naming conventions, so two tables never share a constraint name. A member primary key needs an `id`.
- A member foreign key names its target with `referencesTable` (one fixed table) or `referencesParameter`: the name of
  a required parameter of the stereotype typed
  `{ "type": "string", "x-ref": { "kind": "table", "sameDatabase": true } }` (MQ2026). Each table that carries the
  stereotype then names a table of its own database in its `properties`; one without the value is MQ2018 and gets no
  such key.

**Keyed ids.** An inherited member's id in a table is `<tableKey>.<memberId>`: the table file's id for a designed or
imported table, the synthesized key for a synthesized one (`<entityId>@<databaseId>`, `<relationId>@<databaseId>`).
The `notes` table inherits `updated_at` as `01K6BND0000000000000000001.01JC7AVD1TC0000000000000C2`. Seeds, keys,
indexes, binding fields and queries name an inherited member by its keyed id, never by the member id alone (MQ4008 then
gives the keyed form), and `get_references` on a member lists every table and file that names a copy of it. MCP
`get_database_view` and `get_resolved_model` show each member's keyed id and `memberOrigin`: where it comes from
(`stereotype`, `schema-default` or `package-default`), the stereotype, the member id and the facets the table
redefines; own members have none. Templates read the same as `member_origin`, and sql-ddl writes inherited members as
it writes own ones.

**Order.** A table lists its own members first (a synthesized table's attribute columns and an overlay's extra
columns), then the inherited ones: stereotype by stereotype in the order of its effective set, each in the stereotype's
order. Inherited checks continue the numbering of the table's own checks.

**Synthesized tables.** A synthesized table that already stores a stereotype's attributes as columns, because its entity
carries the stereotype, skips that stereotype's members: the attributes realize it there. So one stereotype serves
entities through its attributes and tables through its members. A synthesized table that inherits a NOT NULL column
with no default is MQ4079 (a warning): rows written through its entity or relation have no value for it.

**Defaults.** A database schema entry and a package can list `defaultStereotypes`, which apply a stereotype to every
element they hold in one line. On the schema entry of database `main`:

```json
{ "id": "01J92P0V1RC04SKQ5353EAKHG2", "name": "billing", "defaultStereotypes": ["audited"] }
```

Now every table of schema `billing` carries `audited`. A schema's defaults reach its tables, views, sequences, routines,
database types and SQL objects; a package's reach every element of the package and its sub-packages (a table overlay
takes its entity's or relation's package, a seed its target's, a scenario its process's). An element's stereotypes are
built in layers: the schema's defaults, then the package defaults from the outermost package in, then its own
`stereotypes`, each layer with the stereotypes it implies first; a key listed twice keeps its last place. A default
skips an element whose kind it does not apply to, so the schema default above gives nothing to a view or a sequence,
with no MQ2004. Group, requires and conflict rules judge the whole set (MQ2013 to MQ2015), and a finding about a default
names the schema or package that gives it; a default list with two stereotypes of one group, or two that conflict, is
MQ2031. Defaults reach top-level elements only, never columns, attributes or process nodes, and databases and the
project have none: list the schemas.

**Exceptions per table.** A table says only what differs:

- `excludeStereotypes` drops a stereotype a default gives, with the stereotypes it implies. An own stereotype, or one an
  own stereotype implies, cannot be excluded (MQ2029); an entry no default gives excludes nothing (MQ2030, a warning).
- `excludes` drops inherited members by member id: `notes` keeps its own `created_at` with
  `"excludes": ["01JC7AVD1TC0000000000000C1", "01JC7AVD1TC0000000000000X1"]`. Excluding a column that a kept key,
  index or check uses is MQ2028: exclude that member too (here, the index).
- `redefines` changes facets of one member and keeps its keyed id: an overlay of Product's table states
  `"redefines": [{ "member": "01JC7AVD1TC0000000000000C1", "nullable": true }]`, and its `created_at` follows
  `audited` for every other facet. MQ2027 refuses an entry that names a member the table does not inherit, names one
  twice, states no facet or states a facet its member does not take; MQ4078 (a warning) reports a facet equal to the
  inherited value, which only stops the table from following the stereotype for it.

| Member | Facets a redefinition may state |
| --- | --- |
| Column | `nullable`, `default`, `defaultSql`, `defaultName`, `comment`, `collation` |
| Primary key | `columns`, `name`, `clustered` |
| Unique | `columns`, `name`, `nullsNotDistinct` |
| Foreign key | `name`, `onDelete`, `onDeleteColumns`, `onUpdate`, `deferrable` |
| Index | `name`, `where`, `method`, `storage` |
| Check | `name` |

Types and a column's name cannot be redefined: the stereotype's check expressions and index predicates name its columns
in SQL. A table that needs another type or name excludes the member and declares its own column. Setting `default` or
`defaultSql` replaces an inherited default of either form, but no redefinition removes one. A key's `columns` lists the
whole new list (own column ids and keyed ids), and an index's `storage` replaces the inherited map. Two primary keys are
MQ4076: an inherited one beside the table's own or its entity's, or beside another stereotype's. A partitioned table
adds its partition column with a `columns` redefinition of the inherited key.

**Materialize.** `materialize-tables` writes the designed table with the shape of the synthesized one but leaves
inherited members inherited: their keyed ids follow the new table id, and the overlay's `redefines`, `excludes` and
`excludeStereotypes` move into the table. The table names the package whose defaults reached the synthesized table, so
it keeps the same stereotypes. A stereotype the synthesized table skipped because it stored the attributes gets an
`excludes` entry per member, since the designed table holds those columns as its own; fold them in later.

**Migration facts.** Members are columns and keys of each table that carries them, so a change to a stereotype records
facts table by table, as a change to an own column does: adding a member records nothing, removing one records a
pending `drop` per table, renaming one a complete `rename` per table and changes no table file, a widening records
nothing and a narrowing a pending `change-type` per table. An `excludes` entry is a `drop` for that table, and a
redefinition that makes a column not null is a `backfill`. To make tables that already hold the same columns inherit
them instead, use the fold: [Fold columns into a stereotype](guides/schema-changes.md#fold-columns-into-a-stereotype).
A fold that cannot apply (a column that differs in its type, a facet that differs without `redefine`, a target that is
not a designed or imported table file) is MQ4077, one per refused table, and writes nothing.

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
| `model.referencesTo(id)` | Where an element or part is referenced: `{ fromElementId, fromId, jsonPointer, field, toId }` for each reference, diagrams and the element's own file included. |
| `model.withStereotype(key)`, `model.withTag(tag)`, `model.withProperty(key)` | The elements whose file lists that stereotype, that tag, or that key in its own `properties`, ordered by name, then id. Read-only. Faster than filtering `model.all`. |
| `model.byQualifiedName(name)` | What `model.get` returns for each element or part a qualified name finds, such as `@entity:Billing.Invoice` or `@attribute:Invoice.total` (the names batches take), ordered by id: one when the name is unique, several when it is ambiguous, none when it finds nothing. A malformed name is an error of the rule. |
| `report(message, { pointer, severity })` | One finding on the element. `pointer` is a JSON pointer into the element (`/name`, `/attributes/0/type`), so the finding points at the right line; `severity` overrides the rule's for this finding. |

**Whole-model rules.** A check about the model as a whole, such as "every reference type is used", is a rule with
`scope: "model"`. Its `check(model, report)` runs once per validation of the whole model instead of once per element,
and each `report` names the element or part the finding is about with `element` (an id); the finding lands on that file,
and `pointer` is relative to that part:

```js
maquettiste.rule({
  id: "unused-reference-types",
  severity: "warning",
  scope: "model",
  check(model, report) {
    for (const type of model.all("reference-type")) {
      if (model.referencesTo(type.id).length === 0) {
        report(`${type.name} is not used.`, { element: type.id, pointer: "/name" });
      }
    }
  },
});
```

A whole-model rule runs in `maquettiste validate`, the MCP `validate` tool without `elementIds`, generation and the
editor's full validation, and when settings are saved (raising its finding to an error there refuses the save). It does
not run when an element is saved, so it never refuses a save over elements the change did not touch. `kinds` does not
apply to it (giving both is MQ5002), and one call spends one `scriptStatements` budget for the whole model. An `element`
that names nothing, or a `report` without one, is MQ5002 on the rule file. The MCP tool `list_extension_files` marks
such a rule (`wholeModel`).

**Rules on the resolved model.** A rule with `scope: "resolved"` checks what generation sees instead of what the files
hold: its `check(element, model, report)` runs once per resolved object of its `kinds`, with `element` and `model` the
read-only resolved views pack scripts get (inherited and virtual attributes included, and the calculated values below).
`kinds` is required and takes resolved kinds (`entity`, `attribute`, `table`, `column`, ...); an attribute an entity
inherits is checked once per entity that holds it. `report(message, { pointer, severity })` lands on the file that
declares the object, and on its entity for an attribute the entity does not own. Resolved rules run in the checks after
resolution: `maquettiste validate`, the MCP `validate` tool, the editor's full validation and generation, where an error
stops the run before planning. They never run on an element save or in a preview, and `includeScriptRules: false` leaves
them out with the other script rules. Their results are cached by object and by what they read, as element rules' are.
`list_extension_files` marks such a rule (`resolved`).

```js
maquettiste.rule({
  id: "public-names-unique",
  scope: "resolved",
  kinds: ["entity"],
  check(entity, model, report) {
    const name = entity.calculated.publicName;
    const other = model.entities.find((e) => e.id !== entity.id && e.calculated.publicName === name);
    if (other) report(`${entity.name} and ${other.name} share the public name ${name}.`);
  },
});
```

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
files: MQ5001 (a custom property value that fails its schema), MQ5002 (a rule or calculation script error), MQ5003 (a
rule or calculation script that broke a sandbox limit), MQ5004 (an invalid extension file), MQ5005 (a rule, rows or
calculation script that keeps state at its top level), MQ5010 to MQ5013 (calculated values, below), MQ5014 to MQ5017
(rule declaration files, below) and MQ5018 to MQ5020 (suppressions, below). Below the built-in families, **Script
rules** lists every `x/<id>` the project's rule files register, with the severity the rule declares as its Default;
set a declared rule's severity with its `x/<id>` in `validation.rules`. Each row's picker sets `error`, `warning`, `info` or `off` in `validation.rules` of
`maquettiste.json`, as for the built-in rules; `off` stops the rule from running, and the severity you set also applies
to the rule's own failures (MQ5002 and MQ5003 on that rule).

**The sandbox.** Rules run in the same sandbox as pack scripts: no files, no network, no access to the .NET runtime, a
fixed clock in UTC and a `Math.random` seeded per file. Each rule file runs in its own function scope, and the globals
are frozen before the rule files run, so one file cannot share anything with another: a rule cannot call a function of
another rule file, and a file that sets a global (`globalThis.helper = ...`) or adds to a built-in prototype fails to
load with MQ5002. Copy a shared function into each file that needs it. Each call runs under the limits of `limits` in
`maquettiste.json`: `scriptTimeoutMs` (2000 ms by default), `scriptStatements` (5,000,000, at most 2,000,000,000),
`scriptRecursion` (256) and `scriptMemoryBytes` (64 MB). A rule that breaks one is reported as MQ5003 on that element,
and the other elements and rules still run. Rules run in parallel over the elements, so a rule must not rely on the
order of the calls or keep state between them.

**The same findings on every machine.** `scriptTimeoutMs` is a wall-clock limit, so a rule that runs close to it can
pass on a quiet machine and fail with MQ5003 on a busy one. `scriptTimeoutMs: 0` turns the clock off: the statement
limit and the memory limit then bound each call, and the findings no longer depend on the machine's load. Use 0 in CI.
`scriptMemoryBytes` counts every byte a call allocates, not what it keeps, so a long loop can reach 64 MB before the
statement limit: raise `scriptMemoryBytes` along with `scriptStatements`. Pack scripts share the setting; previews and
path listings keep their deadline of 8 seconds when it is 0. One bound stays on the clock: a regular expression match
stops after 250 ms, because a match cannot be interrupted. A match that times out is tried once more when it cannot have
run script code, so a busy machine fails it only when the thread waits that long twice in a row. For rules that CI runs,
match regular expression literals against strings and give them strings (`/^[A-Z]/.test(element.name)`), not objects or
a replacer function.

**State between calls.** A rule keeps nothing between calls. The editor, the MCP server and
`maquettiste generate --watch` keep each rule's findings per element in memory, and run the rule on an element again
only when the element, the rule file, the limits or something the rule read through `model` changed; nothing is written
to disk. A rule file that declares a variable with `let` or `var` at its top level, outside every function, gets the
warning MQ5005 on its first such line, in `validate` and on the Extensions tab as soon as it is saved, since that
variable is state the sandbox cannot freeze: the file's results are never cached. Declare values computed once with
`const`, keep `let` and `var` inside functions, and never change an object a `const` holds (the sandbox cannot see
that).

**When a rule file is broken.**

- A syntax error, or a `maquettiste.rule` call without an `id`, with a bad `severity` or an unknown kind, is MQ5002 on that
  file, with its line and column. The Extensions tab shows it as soon as the file is saved (the save still writes the file,
  so work in progress is kept), and `validate` reports it too. The other rule files keep running without it.
- A `check` that throws is MQ5002 on the line of the rule file that threw, with the element it was checking; the findings the
  rule reported before it threw are kept, and the other elements are still checked.
- A rule id registered twice is MQ5002 on the second file.

Clicking such a finding in the Problems panel opens the rule file on the Extensions tab.

### Declared rules (`extensions/rules/*.json`)

A simple convention needs no script. A declaration file in `extensions/rules/` states rules as data: which resolved
objects a rule judges (`select`), what must hold for each (`require`) and the message of a finding. Prefer a
declaration when the convention fits; keep scripts for what it cannot say.

```json
{
  "$schema": "../../.schema/v1/declared-rules.json",
  "rules": [
    {
      "id": "audited-tables-have-updated-at",
      "severity": "error",
      "description": "Audited tables record when a row last changed.",
      "select": { "kind": "table", "stereotype": "audited" },
      "require": {
        "count": { "of": "columns", "where": { "equals": { "path": "name", "value": "updated_at" } }, "min": 1 }
      },
      "message": "Table {name} carries audited but has no updated_at column."
    }
  ]
}
```

- **A rule.** `id` (letters, digits, `.`, `_` and `-`; findings are `x/<id>`, unique across declarations and rule
  scripts), `severity` (`error` by default; `validation.rules` overrides it by `x/<id>`, and `off` skips the rule),
  `description`, `select`, `require` and `message`.
- **Selectors.** `kind` is required; `stereotype` (the effective set, defaults and implied keys included), `tag`,
  `property` (a custom property with a value) and `package` (the package or one nested in it) narrow it. Each key takes
  one value or a list, a list means any of its values, and the keys combine with AND.
- **Conditions.** Exactly one key each: `all`, `any`, `not`, `has` (a path holds a value), `equals` (`{ path, value }`),
  `count` (`{ of, where, min, max }` over a list of objects such as `columns`, `attributes` or a table's `indexes`;
  `min` 1 when no bound is given), `references` and `referencedBy` (`{ kind, field, min, max }`: links through the
  index; a diagram that shows an element does not count). A path is the names of resolved members joined by `.`, and
  may read a calculated value (`calculated.<name>`).
- **Messages.** `{<path>}` inserts the value at a path of the object (a resolved object by its name), `{$count}` the
  number a top-level `count`, `references` or `referencedBy` found, and `{{` and `}}` write braces.

Declared rules run on the resolved model, after resolution, wherever script rules with `scope: "resolved"` run:
`maquettiste validate`, the editor's validation, the MCP `validate` tool and generation, where an error stops the run.
They never run on element saves. A finding lands on the object's file as resolved-rule findings do. Whole-model
validations check the declarations first: a file that does not fit `declared-rules.json` is MQ5014 and declares nothing,
an id another declaration or a rule script already has is MQ5015 on the later file, and MQ5016 marks what does not exist
or apply (a kind, a stereotype key, a package id, a path, a calculated value, a link kind, bounds or a placeholder). A
broken declaration stops resolution as a broken rule script does. A rule that selects no object warns MQ5017 at its
`select`. The Extensions tab and `list_extension_files` list a declaration file as kind `declared-rules` with its rules.

**How a rule sees inherited members.** A declared rule reads the resolved object, never its file. A table's `columns`
lists every column generation sees: its own, the ones its stereotypes contribute (keyed `<table key>.<member id>`, with
the table's redefinitions applied) and, for a table a binding or mapping shapes, the ones resolution adds; a column the
table `excludes` is absent. The same holds for `indexes`, `checks`, `foreignKeys` and `primaryKey`. An entity's
`attributes` lists inherited, own and stereotype attributes, and `ownAttributes` its own. So when `audited` contributes
an `updated_at` column, every table that carries `audited` satisfies the rule above, a table that excludes the column
fails it again, and a redefinition that makes it nullable keeps it counted. A finding about an inherited column, from a
rule that selects `column`, lands on the table's file with the column's keyed id as its element id, so a suppression on
the table with that `member` matches it.

### Suppressions

Every team meets the one element a rule is wrong for. Instead of turning the rule off for everyone, the element accepts
the finding, with a reason, in its own file:

```json
"suppressions": [
  {
    "rule": "x/audited-tables-have-updated-at",
    "reason": "Notes are append-only; a row never changes after it is written."
  }
]
```

- **An entry.** `rule` is a built-in rule (`MQ` and four digits), a script rule or a declared rule (`x/<id>`); `reason`
  is required and says why this element is the exception; `member` (optional) limits the entry to one part of the
  element: a part in its file (its id), an attribute it holds through a base or a stereotype, or a member a table
  inherits (its keyed id, `<table key>.<member id>`). The key sits on the element's file only, never inside a part.
- **What it matches.** A finding on the element's file whose rule is the entry's rule; with `member`, only a finding on
  that part (its element id, or a pointer at or under the part, so moving the part in its array changes nothing). The
  first matching entry wins.
- **Where they apply.** Everywhere findings are reported, after `validation.rules` sets their severity: element saves
  (a suppressed error does not refuse a save), `maquettiste validate`, the editor, the MCP `validate` tool, the
  resolver's and the declared and resolved rules' findings, validation against a baseline, and generation, which runs
  when the only errors are suppressed. A project without suppressions pays nothing.
- **What you see.** Suppressed findings leave the counts and the diagnostics list. `maquettiste validate` prints
  `<n> findings suppressed` after its summary; `--format json` lists them under `suppressed` (the finding, the reason,
  the element and the entry's pointer, `/suppressions/<i>`); `--format sarif` writes each as a result with an in-source
  suppression whose justification is the reason. The MCP `validate` answer and `POST /api/validate` carry `suppressed`.
- **Rules on the entries.** MQ5018 (an error) for a rule that cannot be suppressed: MQ1xxx (a file that does not load),
  MQ7012 and MQ5018 to MQ5020. MQ5019 (a warning) for a rule nothing defines, a `member` the element does not hold,
  or an entry that repeats another's rule and member; `x/` ids are judged when every rule script loads and the
  declarations are valid. MQ5020 (a warning) for an entry that matched no finding in a whole-model validate whose model
  resolved, so an exception that is no longer needed shows up.

There is no expiry date (validation never depends on the clock), no wildcard and no suppression by selector: a rule
that is wrong for a group of elements needs a narrower `select`, and `validation.rules` still sets a rule's severity for
the whole project.

### Calculated values (`extensions/calculated/*.js`)

A calculated value is a named value your project computes for each object of some kinds, defined once and read by every
template, pack script and resolved rule. Each file directly in `extensions/calculated/` declares one or more with
`maquettiste.calculated`, and nothing else:

```js
// extensions/calculated/names.js
function kebab(text) {
  return text.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
}

maquettiste.calculated({
  name: "publicName",
  kinds: ["entity"],
  schema: { type: "string", pattern: "^[a-z][a-z0-9-]*$" },
  description: "The name clients see in URLs and payloads.",
  value(entity) {
    return kebab(entity.pluralName);
  },
});
```

In the billing example `Invoice` gets `invoices`, `InvoiceLine` gets `invoice-lines` and `Customer` gets `customers`.

| Part | Meaning |
| --- | --- |
| `name` | `^[a-z][A-Za-z0-9]*$`, at most 64 characters. Unique per kind across the calculation files; one name may serve several kinds. |
| `kinds` | Required: the resolved kinds whose objects get the value (`entity`, `attribute`, `table`, `column`, `enum`, ...). |
| `schema` | Required: a JSON Schema every value must fit. `null` is a value only when the schema allows it. |
| `description` | Optional: what the value means; `list_extension_files` and `get_calculated` show it. |
| `value(element, model)` | Called once per object of those kinds, with the read-only resolved views pack scripts get. Returns the value (`undefined` is `null`): a string, number, boolean, `null`, or a list or map of them. |

**Reading them.** A template reads `{{ element.calculated.publicName }}` and tests for one with `{{ if has_calculated
element "publicName" }}`, which evaluates nothing. A pack script, a helper, a resolved rule and another calculated value
read `entity.calculated.publicName`, and `"publicName" in entity.calculated` tests for one. A value is evaluated the
first time something reads it and reused after that, so a preview evaluates only the values of the objects it reads.
Reading a name the object's kind does not declare is MQ5012 on the reader. A rule with the default scope reads the saved
JSON, which has no calculated values; give it `scope: "resolved"` to read them.

**Seeing them.** `maquettiste model calculated --element <id>` and the MCP tool `get_calculated` give the values of the
objects you name, evaluated for those only. Each `get_resolved_model` page carries `calculated` for its records and the
attributes they list. `get_template_context` lists `calculated.<name>` among the scope's members.

**When a value fails.** A value function that throws, breaks a sandbox limit or returns something that is not JSON is
MQ5010 on the calculation file, at the failing line, with the object's id. A value that does not fit its schema is
MQ5011 at the `maquettiste.calculated` call. Values that read each other in a cycle are one MQ5013 naming the chain. A
value that reads a failed value fails too. `maquettiste validate`, the MCP `validate` tool and the editor's full
validation evaluate every value and report each failure once; generation stops on one before planning. A file that does
not load, a malformed declaration or a (name, kind) pair another file already declares is MQ5002 on that file (the later
file by path), shown on the Extensions tab as soon as it is saved. `validation.rules` may lower or turn off MQ5010 to
MQ5013.

**What it costs.** A project that declares no calculated value pays nothing: no sandbox is created and nothing is
evaluated. Otherwise each value is computed once per object of its kinds, in parallel; on a benchmark model of 5,000
entities, two values on entities add about 36 ms to `validate`. The editor, the MCP server and
`maquettiste generate --watch` cache each value by the keys it read (the object, what `model` returned, the calculation
file), so after an edit only the values that read what changed run again. A generated file that reads a calculated value
is planned again when that value changes, and only then.

**The sandbox.** Calculation files run in the sandbox rule files use, under the same `limits`, one function scope per
file and no script library. A file that declares `let` or `var` at its top level gets MQ5005 and its values are never
cached. Calculated values write no model file and never read a database.

### Rows scripts (`extensions/seeds/*.js`)

A seed can compute its rows from the model instead of listing them: the seed names a file directly in
`extensions/seeds/` with `rowsFrom: { "script": "seeds/<name>.js" }`, and the file calls `maquettiste.rows` once:

```js
maquettiste.rows((model, seed) => model.withStereotype("audited")
  .filter(d => d.kind === "entity")
  .map(d => ({ code: d.name.toLowerCase(), label: d.displayName ?? d.name })));
```

| Part | Meaning |
| --- | --- |
| `model` | The model documents, read-only, with the same calls as a rule's `model` (`get`, `all`, `referencesTo`, `withStereotype`, `withTag`, `withProperty`, `byQualifiedName`). Never the resolved model or a pack. |
| `seed` | The seed's document, frozen; several seeds may share one script and tell themselves apart by it. |
| The result | An array of plain objects keyed by the seed's column names: `code`, `label` and `description` for a reference type, an attribute's name or an end's role, a table column's name, a foreign key's name for a foreign-key entry. |

A scripted row's id is derived from the seed's id and the row's key, so it is the same on every run; a target with no
row key (a relation, for one) takes no scripted rows. The script runs in the same sandbox and under the same `limits`
as a rule, in its own scope with frozen globals; calling `maquettiste.rule` there, or `maquettiste.rows` in a rule file
or a pack script, fails to load. It runs only when something reads the seed's rows (a unit, `validate`, a save of the
seed, `maquettiste seed preview`, the MCP tool `preview_seed_rows`), and its rows are kept until the script, the seed or
something it read changes. Its failures are MQ7127 (it does not load, throws, breaks a limit or returns no array of
rows) and MQ7128 (a key no column has, or no row key), both on the seed at `rowsFrom.script`, and MQ5005 on the script
for a top-level `let` or `var`. The Generate explorer lists rows scripts under Extensions and opens them on the
Extensions tab, which has no group or New command for them yet; the MCP extension file tools and the API write them too
(kind `seed-rows`), and a write loads the script without running it. The [reference data
guide](guides/reference-data-and-seeds.md#14-compute-rows-with-a-script) has an example from start to end.

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
when what its scripts read changes. A script error is MQ6016 and a broken limit MQ6007.

A helper that reads many members in a loop can ask for plain copies instead of views:
`maquettiste.helper("columns_json", fn, { plain: true })`. Its arguments arrive as frozen plain objects and arrays,
which are faster to read, bounded by member hops so a copy never grows into the whole model. A model object passed as an
argument, or as an item of a list or map argument, is at hop 0 and has every member a view has. A model object one of
its members holds, directly or in a list, is at hop 1 and is copied the same way. From hop 2 a model object is only
`{ id, kind }`. So for a table, `table.columns[0].name` and `table.database.name` are there, and
`table.columns[0].attribute` is `{ id, kind }`. A copy has no functions: read `stereotypes` and `tags` rather than
calling `hasStereotype` or `hasTag`. Everything copied at hops 0 and 1 counts as read, whether the helper reads it or
not, so the unit renders again when any of it changes. The model itself cannot be passed (MQ6016, its copy would be the
whole model): pass the list the helper needs, such as `model.entities`. Any other option, or a `plain` that is not
`true` or `false`, stops the script from loading (MQ6016). Pack scripts are edited on the pack's
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
MQ1003 ("not in canonical form"); **Rewrite in canonical form** on the finding in Problems rewrites that file, Settings ›
Validation › **Rewrite all model files in canonical form** rewrites every one, `maquettiste format` does the same from the
command line (`--check` lists them without writing), and the next save of the element from the editor or an agent
rewrites it too.

**Upgrading from 0.5.4 or earlier.** A project made before 0.5.5 usually still has `"commit": true` (or `false`) on its
`outputs.allow` entries. 0.5.5 ignores the flag and the canonical writer drops it, so such a project shows MQ1010 (an
information line per entry) and MQ1003 on `.maquettiste/maquettiste.json` until the file is rewritten: use the quick fix
in Problems, the Settings action or `maquettiste format` once, and both go. An extension schema
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
