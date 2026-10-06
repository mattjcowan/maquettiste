# Next

The work queued after 0.7.1 (2026-10-04), in the order proposed. Each item says where it stands and what it waits on.
When an item is built, its record moves to `status.md` (and the design documents) and it leaves this file.

## 1. Validation speed (proposed next; needs no decision)

The gate 3 bench line "MQ90xx-MQ92xx, whole model, added to validate" has a 400 ms budget. It measured 359 ms at gate 3;
since the database designer, bindings and DDL rounds (0.6.0) it runs between 400 and 850 ms, so the line misses on most
local runs and on some CI runs. The time is spent in whole-model validation, not in the process rules themselves.

- Profile `validate` on the bench model (5,000 entities, 1,000 processes) and on the reference app; find which rules or
  indexes grew superlinear (the binding and table rules added in 0.6.0 are the first suspects).
- Fix at the cause; never raise the budget.
- Also check the editor's validation on the 5,000-entity scale dataset (`explorer-redesign.md` §4.5).

## 2. Bindings, the rest (`binding` phases B and C)

Built in 0.6.0: an entity binds to a table, view or query of a database, with constant columns, a field map, write
columns and delete by key or soft delete; bulk auto-map, create tables, remove bindings, entities from tables.

Left:

- **Raw SQL as a read source.** A binding reads from a hand-written SQL statement, not only a table, view or query.
- **Split.** One entity read from and written to several tables.
- **Routine bindings.** Read, write and delete through stored routines.
- **Converters** as named, reusable elements on a field map.
- **The unmapped-attribute rule in the engine.** MQ4058 (an attribute of a bound entity that no column maps) exists in
  the editor's mock only; the engine should report it too.

Decided: the database knows no entities; entities own their bindings; writes of attribute-per-row (EAV) tables are out.

## 3. Migrations as recorded facts (proposal; needs answers)

Migrations become a model kind: a `migration` per database holds ordered steps (data, never SQL) with guards, checked
against the schema diff instead of inferred from it. A change made outside Maquettiste can be recorded; the editor and
the agent server can record steps as changes are made (asking first or automatically, by setting). Any pack can render
the facts: `sql-ddl` first, later an attribute-based C# migration pack and a docs pack.

Phases: A (the kind, steps, guards, validation against the diff, sealing on generate, manual authoring, `sql-ddl` on
facts); B (live recording, ask or automatic); C (the C# migration pack and the docs pack).

Open questions:

1. One open migration per database that accumulates steps until sealed, or one per change? Proposed: one open per
   database; sealing is the revision.
2. Does `generate` seal automatically, or only an explicit Close revision? Proposed: generate seals, with a setting to
   require explicit closing.
3. Do data-only migrations (backfills, no schema change) get revisions of their own? Proposed: yes, same kind.

## 4. Model service: persistence providers, snapshots, a model library (phase A engine and server built; editor next)

The model stops being only "the JSON files in this checkout" and becomes something an instance serves; generation stays
something a checkout does. One central instance can hold many models (title, entity and table counts, last updated),
each with snapshots (name, date) that can be opened read-only, restored, compared, exported and imported.

**Built (2026-10-05, phase A, engine and server; docs/engineering/snapshots.md, erratum E45).** The document store
interface under `ModelStore` with the model folder as its provider (a pure refactor) and a read-only snapshot archive as the
second; snapshots as deterministic zip archives under `.maquettiste/model-snapshots/` (packs off by default, held on
request): take, list, rename and publish, delete, open read-only ("as of", through `?snapshot=<id>` on the editor's model
reads), compare (with each other or the working model; per-element fields on demand), restore (after a `before-restore-`
safety snapshot; refused while generation runs), export and import; over the API (`/api/snapshots`, with the mock serving
them), the CLI (`maquettiste snapshot …`) and the MCP tools (`list_snapshots`, `create_snapshot`, `compare_snapshots`,
`restore_snapshot`).

**Editor built (2026-10-05; snapshots.md section 10).** The snapshot picker on the project name (search, take with packs off
by default, import, and per snapshot open, compare, restore, export, rename, publish and delete, by role), the as-of mode
(`?snapshot=<id>` in the URL, every read scoped and every write off, a one-line banner in the top bar), the compare view
(changes by kind in pages, the conflict view's diff per element) and restore with "Undo: restore before-restore-…"; the mock
serves as-of reads, export and import. An import over the host's 512 MiB request bound is now a 413 with MQ1011, not a 503.

Left in phase A: instance capabilities (`model: read`, `generation: off`, `packs: read|off`); generating from a snapshot with its packs.

Phases B (an http provider, multi-model service mode, the library picker, `generate --model <url>`) and C (a database
provider with versions as history, accounts and single sign-on, per-model permissions, cross-model imports) are unchanged.

Decided: one central service, many models, one URL; working models are visible, and a snapshot can be marked published;
packs travel with a snapshot only when asked; compare is in phase A; snapshots are zip files on the file provider.

Open questions:

1. Do developers need a local copy of the model, or is `generate --model <url>` enough (and what about offline work)?
2. Auth for the first cut: is one shared token per instance enough until accounts arrive?
3. Should cross-model imports (shared vocabularies) be designed now, since they shape the archive format (it can carry an
   `imports` list later)?
4. Should anything in a working model be hidden from a reviewer?
5. Should a project keep its snapshots out of git (the product writes no ignore file), or are they model history to commit?

## 5. The assistant, next steps

Built in 0.7.0: the sidebar reads the whole model and proposes model changes as reviewed diffs.

- Let it propose pack and extension file edits, reviewed the same way.
- Let it run a plan and show the diff (apply stays a user action).
- Drafting a template from an example output (the spec's last assist task) waits on pack writes.

## 6. Smaller items

- **Explain still prepares the whole pack.** Explaining one unit (an explicit click) runs a full dry-run preparation; it
  should plan only the unit asked about, as the preview now does.
- **The store rescans every file per request.** Reading the model snapshot stats every model file each time (about
  150 ms on 40,000 files); a file watcher or a cheaper change check would remove most of a warm preview's time.
- **Oracle DDL against a server.** Every other dialect ran against a real server in 0.6.0; Oracle did not.
- **Lock files.** The project-reference ranges in `packages.lock.json` still read `1.0.0-alpha.1` (harmless metadata from
  the first restore). Refreshing them would mean a release script that re-evaluates the locks at every version bump.
- **Regex limit tests.** Since the retry in 0.7.1, the catastrophic-pattern tests take about 0.5 s against a 1 s bound;
  watch them on slow runners.

## 7. Editor approachability (waits on a walkthrough with the owner)

- A reorganization for non-experts: onboarding, naming, empty states, a clearer first screen. Collect confusion points
  in a live walkthrough first.
