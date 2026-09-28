# integration

**Owner:** WI End-to-end integration. See docs/engineering/engine-design.md sections 17 and 18.

Scriban packs for the end-to-end tests in `tests/Maquettiste.Engine.Tests/Integration/`, which copy
`tests/fixtures/models/billing` into a temporary repo, install these packs (and `tests/fixtures/templates/billing-demo`) under
`.maquettiste/templates/`, and drive the engine only through `new ModelStore(options)` and `new GenerationService(store, options)`.
The packs under `tests/fixtures/packs/` are written for the fake renderer of the W6 tests and are not valid Scriban.

| Pack | Output (set by the tests) | Units |
| --- | --- | --- |
| `packs/e2e` | `db/e2e` (committed) | `entity` (each entity, a JavaScript helper `shout`), `table` (each table of `main`), `index` (model), `types` (each entity, `type_of` through the pack type map `types/csharp.json`), `audited` (`select audited`, a JavaScript selector), `scaffold` (`once`), `pair` (with a companion), `regions` (one `body` region), `blocks` (file blocks only, one per package) |
| `packs/migrations` | `db` (committed) | `migration` (`for: model`, `mode: once`, `usesSchemaDiff`): a file block `migrations/<to_revision>.sql` when the `main` diff is not empty |

With `billing-demo` the full run renders 60 units (e2e 36, billing-demo 23, migrations 1). Tests that count units or files
depend on these packs; change them together.
