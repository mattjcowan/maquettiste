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
- **Databases**: the physical side. Tables that follow the naming conventions are **mapped automatically**; a table
  that differs has a **customised mapping** (an override file).
- **Diagrams**: saved views of the canvas. A diagram only remembers which elements it shows and where.
- **Templates**: the packs that turn all of the above into files (SQL, C#, TypeScript, docs).

The sample project you will see first is **billing**: a small invoicing model with five entities, four relationships, one
enum, two custom types and one database. It exists so the editor has something realistic to show; it is not part of
your repository's model.

## The layout

The editor is laid out like an IDE:

| Area | What it holds |
| --- | --- |
| Top bar | The project name (the whole model and its settings), git branch and changed-file count, the command palette (Ctrl+K or Cmd+K), theme and density |
| Rail | The explorers: Domain model, Reference data, Databases, Diagrams and Generate; Settings and the account menu at the bottom |
| Sidebar | The explorer the rail selected: a tree with a "Search the model" box, filterable by tag, category and stereotype |
| Center | The current screen: the canvas, a grid or an editor |
| Right | The inspector: every property of the selected element, editable |
| Bottom | Problems (live validation), generation output, and a diff viewer |

### The rail: one explorer at a time

The rail's icons, top to bottom, are **Domain model**, **Reference data**, **Databases**, **Diagrams** and **Generate**;
**Settings** (the gear) and the **Account** menu sit at its foot. A click on an icon shows that explorer in the sidebar,
and only that one: each explorer has its own tree, its own expanded rows and its own filter, kept when you switch away
and back. A second explorer can stay open beside the first: the explorer header's menu has **Pin beside…** (and
**Unpin**), a per-browser preference. The header also offers **Collapse all** and **Highlight related elements**: with it
on, selecting an entity tints the relationships, tables and columns that belong to it, and a collapsed folder shows
"n related".

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
  - **Fields**: the built-in `code` and `label` (names fixed; code's type, length and pattern editable), then your own
    fields in the attribute grid.
  - **Rows**: a spreadsheet over the type's rows. Arrows move; Enter or F2 edits, Enter commits and moves down, Tab
    commits and moves right, Esc cancels; Ctrl+Enter inserts a row below, Ctrl+D duplicates one (with an empty code),
    Delete clears cells, Ctrl+Delete deletes rows, Alt+Up and Alt+Down move rows; Shift+arrows select a range, Ctrl+C
    copies it as tab-separated text and Ctrl+V pastes such text, adding rows past the end; Ctrl+F finds; Ctrl+1 to
    Ctrl+4 switch tabs; `/` goes to the type search. **Import CSV** shows what a file adds, changes and removes before
    you apply it as one change you can undo; **Export CSV** downloads the rows (`@id`, `@code`, `@label`, then the
    fields by name).
  - **Used by**: every attribute whose type is this reference type, with its Many and Required badges; click one to go to it.
  - **Storage**: per database, the storage strategy in effect and where it comes from, and an override chosen from the
    strategies the project declares in Settings (`referenceData.strategies`) or **Template-defined** (the packs decide).

  To use a reference type as an attribute's type, open the attribute's **Type** cell: the list has sections (Recent,
  Built-in, Custom types, Enums, Reference data, Value objects) and one search across them. **Many** (Alt+M) makes the
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
  selected table. The database row's menu opens **Mappings**: an entity and its table side by side, where names mapped automatically are muted
  and customised ones are highlighted.
- **Diagrams**: the saved diagrams outside the domains.
- **Seed data**: an entity's or a relationship's initial rows, in its editor's **Seed data** tab and in the domain's
  Seed data folder; a reference type's rows are its Rows tab. A seed lists its columns once and holds one row per line,
  each with its own id; a cell that names a row of another seed holds that row's id, and a reference-typed cell holds the
  row's **code**. A seed belongs to its element: deleting the element deletes its seeds and its translations in the same
  change, and removing an attribute drops its column. A delete is refused only while other elements point at the element
  or at its rows.
- **Generate**: Plan renders every template unit and shows what would change; pick a file to see its diff; Apply
  writes the plan. Generation runs as a job and reports progress; the run history stays in the panel.
- **Settings**: the vocabularies (Tags, Categories, Stereotypes), the naming conventions, and **Locales** (below).

## Translating the model in the editor

The display names, plural names and descriptions of the domain model, and the labels of reference data rows, can be
translated (reference-types-seeds-localization.md section 3). While the project declares one locale, nothing of this
shows anywhere: no switcher, no Translations section, no locale columns.

- **Settings › Locales** declares the locales: the default locale (the language the element files are written in), the
  supported locales, each locale's fallbacks (for example `fr-CA` falls back to `fr`; left empty, a locale falls back to
  its shorter tag, then to the default) and which kinds count for completeness (none checked: every translatable field).
  Save writes the `localization` block of maquettiste.json. "Declare locales" starts the block when there is none.
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
SPEC.md Section 4 describes.

## The command line

`maquettiste` is the same engine without the editor: it creates the project, checks it and generates the code, which is
what a CI job and a terminal need. `maquettiste --help` lists every option; exit codes are 0 success, 1 validation or read errors,
2 drift (or a `--check` preview that would change something), 3 hand-edit conflicts (or, for the `l10n` and `seed` verbs, a file
that changed while the command ran), 4 an internal or usage error (a refused write, or an argument that names no locale or seed).

| Command | What it does |
| --- | --- |
| `maquettiste init` | Creates `.maquettiste/` (`maquettiste.json`, the JSON schemas for editor completion, the `sql-ddl` starter pack) and a `.gitignore` block. `--pack csharp-dapper` or `--pack none` picks another starter; `--mcp`, `--skill` and `--agent-setup` register the agent server (docs/mcp.md). The project is named by `--name <name>`, else the `name` of `package.json`, else the git remote's repository name, else the folder name (so a repository mounted at `/repo` in the image keeps its real name). Running it again keeps what is there. |
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
docker run --rm -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste init
docker run --rm -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate
docker run --rm -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate --check
docker run --rm -it -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate --watch
```

A shell function saves typing (`-it` only when you are at a terminal, so it also works in scripts and CI):

```sh
maquettiste() { docker run --rm $([ -t 0 ] && echo -it) --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste "$@"; }
```

**File ownership.** The image runs as UID 1654. On Linux, add `--user "$(id -u):$(id -g)"` (as the function does): the
CLI then writes the model and the generated files as you, and they stay yours. Without it the run fails on a repository
UID 1654 cannot write, with "Access to the path ... is denied". Docker Desktop on macOS maps file ownership to the Mac
user, so `--user` is harmless there and the files are yours either way (not verified here). The CLI works under any
UID: when the image's own folders are not writable it keeps its cache in the container's `/tmp` for that run.

**Order with the editor.** Run `init` before `docker compose ... up`. The compose file bind-mounts `./.maquettiste`; when
the folder does not exist yet, Docker creates it empty (owned by root on Linux) and the editor starts on a project with no
`maquettiste.json`.

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
