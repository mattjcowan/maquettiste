# Maquettiste editor: a first guide

Maquettiste turns one model of your application, stored as JSON files inside your repository, into most of its code.
The editor is where you look at and change that model. This guide walks through what you see the first time.

## What you are editing

A project lives under a `.maquettiste/` folder in a repository. It holds:

- **Entities**: the business objects (Customer, Invoice) with their attributes and keys.
- **Relations**: named links between entities ("Customer places Invoice"), which can carry attributes of their own.
- **Enums and types**: closed value sets (InvoiceStatus) and reusable custom types (Money, EmailAddress).
- **Packages**: folders that group elements and become namespaces in generated code.
- **Databases**: the physical side, derived from the entities by convention and adjusted by overlay files.
- **Mappings**: explicit bindings between an entity and a table where the convention is not enough.
- **Diagrams**: saved views of the canvas. A diagram only remembers which elements it shows and where.
- **Templates**: the packs that turn all of the above into files (SQL, C#, TypeScript, docs).

The sample project you will see first is **billing**: a small invoicing model with five entities, four relations, one
enum, two custom types and one database. It exists so the editor has something realistic to show; it is not part of
your repository's model.

## The layout

The editor is laid out like an IDE:

| Area | What it holds |
| --- | --- |
| Top bar | Project name, git branch and changed-file count, the command palette (Ctrl+K or Cmd+K), theme and density |
| Left rail | The workspace switcher and the model explorer, grouped by package, filterable by tag, category and stereotype |
| Center | The canvas or grid of the current workspace |
| Right | The inspector: every property of the selected element, editable |
| Bottom | Problems (live validation), generation output, and a diff viewer |

F6 moves keyboard focus between these regions. Edits in the inspector are drafts with undo and redo; they are saved
as you go, and a save that collides with a change made elsewhere shows a conflict dialog with the two versions.

## The workspaces

- **Entities**: the conceptual model on a canvas. Cards are entities with their attributes; edges are relations with
  their cardinality. Click a card or an edge to select it and edit it in the inspector. "Add related" pulls in
  neighbours to a chosen depth, and Auto-layout untangles the diagram. Export writes SVG or PNG.
- **Database**: table diagrams per database, a dialect selector, and a live DDL preview for the selected table.
  Tables come from the entities by convention; the overlay files add what differs (a native type, an extra index).
- **Mappings**: an entity and its table side by side. Names that come from conventions are muted; overrides are
  highlighted.
- **Generate**: Plan renders every template unit and shows what would change; pick a file to see its diff; Apply
  writes the plan. Generation runs as a job and reports progress; the run history stays in the panel.
- **Settings**: the vocabularies (tags, categories, stereotypes) and the naming conventions.

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

## Where the details are

- SPEC.md: the specification, Sections 5 to 12 for the model and generation, 14 for the editor.
- docs/engineering/phase2-design.md: how the editor and its API are built.
- docs/api/openapi.yaml: the editor's API.
- packs/README.md: how to write a template pack.
