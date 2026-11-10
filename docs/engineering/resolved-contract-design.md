# The resolved-model contract: design

Status: built, unreleased. The owner answered yes to both questions of section 9. Requested by the owner: "existing systems
using maquettiste always have a fresh reference that goes with their point release".

As built, where it differs from the sections below:

- The shapes come from a small walker over the serializer's own contract (`JsonTypeInfo` over the Web options) rather than
  `JsonSchemaExporter`: the walker gives every record one `$defs` entry directly. An open member (`object`, `JsonElement`) is
  `{}` when it may be null and `{"not": {"type": "null"}}` when it may not. The Markdown is rendered from the schema
  (`ResolvedContractReference`). The writer and the renderer live in the engine tests; the engine embeds both checked-in files
  (the reference from `docs/`, which the image's engine stage copies) and renders nothing at run time.
- `contract` in every answer is the `x-contract` of the schema the engine embeds, not the assembly's version, so an answer always
  agrees with the schema shipped beside it (a tool built with another `-p:Version` included). The drift test keeps `x-contract`
  equal to the repository's release.
- The schema's root also has `x-kinds` (record kind to definition), beside `x-scopes`.
- The reference app holds both copies (its `build-model.mjs` copies the reference, `check-model.mjs` compares it). The drift test
  writes and checks all four files, so the release step is one command (the dev skill names it).
- A nullable or optional field whose description does not say when it is null gets "Null otherwise." (after a condition such
  as "for a junction table") or "Null when not set." mechanically; a few were rewritten by hand.
- Coverage: the fixture models plus models other tests build (approvals, rights, revisions, trigger points, migrations, the
  code-built billing model) produce every record but 19; the feature tests that build those (relation links, constraints,
  former codes, named trigger points, operation guards, ...) call `ContractCheck.Validate` on their whole model naming the
  records `ContractCheck.Credited` credits them with, and fail when their model stops producing one. `ContractCheck` also fails
  when a field is null in real output but the schema says it never is.
- `ContractCheck` builds each scope's page from `ResolvedRecords` itself, with no `calculated` values. The page path through
  `GetResolvedAsync` (paging, the database filter, calculated values) meets the schema in the agent tools test (every scope, small
  pages, one database) and in one Functions test on the billing fixture.
- The schema has no external reference: the diagnostic record is generated from its type too.

The contract describes what `model export --resolved`, `GET /api/model/resolved` and MCP `get_resolved_model` answer: one
JSON Schema and one Markdown reference, both generated from the engine's own record types, shipped in every project's
`.maquettiste/.schema/v1/`, and named by version in every answer. Until version 1 the contract version is the product
version (`0.24.0`) and may change in any release. No freeze and no version-step check now.

## 1. How the records are built today

- `ResolvedRecords` (src/Maquettiste.Engine/Editor/ResolvedRecords.cs) projects the resolved model (R-types, an object
  graph) into flat, typed C# records: 93 `public sealed record` types there, plus the views it reuses from
  `DatabaseViews.cs` (26), `QueryViews.cs` (9) and `BindingViews.cs` (8): about 120 types reach the answer. Other objects
  appear by id. `ResolvedRecords.Scopes` maps the 21 scopes to record kinds; `all` adds `verb`, `right` and
  `trigger-point` records.
- `GenerationService.GetResolvedAsync` pages them into `ResolvedPage(items, next, diagnostics)` plus `calculated`.
- Every surface serializes with `JsonSerializerDefaults.Web` (camelCase, nothing ignored except a few
  `[JsonIgnore(WhenWritingNull)]` members such as `EntityRecord.checks`). API (`ModelEndpoints.Resolved`) and MCP
  (`AgentTools.GetResolvedModelAsync`, shared by the MCP server and the editor's assistant) return the page as is.
- The CLI (`ModelCommand.ExportResolvedAsync`) writes only the items: a bare JSON array, or one record per line (NDJSON).
  It drops `calculated` and `diagnostics`.
- Every record and parameter already has an XML doc comment (`<summary>`, `<param>`). About 115 of the 1,600 doc lines
  name a design section or erratum ("next-phase-2-design.md section 3.5", "erratum E43").
- `docs/api/openapi.yaml` describes the same records by hand (`x-engine-record`), checked against real responses by the
  Functions contract tests. That stays as it is (see section 8).

Records are typed, so the schema can be generated from the types the serializer actually writes.

## 2. Generating the schema and the reference

`ResolvedContractWriter` (internal, src/Maquettiste.Engine/Editor/ResolvedContract.Writer.cs, next to the records) builds
both files from three inputs:

1. **Shapes** from `JsonSchemaExporter` (System.Text.Json, net10) over the Web options, one record type at a time. A
   `TransformSchemaNode` callback turns every nested record type into `{"$ref": "#/$defs/<camelName>"}` (with `null`
   allowed where the member is nullable), so each record is defined once in `$defs`. Nullability comes from the
   nullable annotations (`RespectNullableAnnotations`). The callback also adds `additionalProperties: false`,
   `required` (every member except the `WhenWritingNull` ones, which may be missing), `x-order` (the serializer's order;
   the existing `SchemaConsistencyTests` require it) and `kind` as a `const` from `ResolvedRecords.Scopes`. `object`
   and `JsonElement` members (a default value, a custom element's content, a stored expression tree) stay open (any
   JSON value), and their descriptions say what they hold.
2. **Descriptions** from the engine's XML documentation file (`Maquettiste.Engine.xml`, built because
   `GenerateDocumentationFile` is on, copied into the test output). A record's `<summary>` describes the record; a
   member's `<param>` (or a property's `<summary>`) describes the field. `<see langword="null"/>` becomes `null`,
   `<c>x</c>` becomes `` `x` ``, a `cref` to another member ("As `PackageRecord.DisplayName`") takes that member's text.
   A parenthesis that only cites an engineering document ("(next-phase-2-design.md section 3.5)", "(erratum E43)") is
   dropped; the comments that read badly without it are rewritten by hand. So the descriptions stay next to the code,
   in the XML docs engineers already keep.
3. **Prose** held as constants in the writer: the page envelope, the scope list, how to fetch the schema, and the
   template section (section 6).

The generator runs inside a test, not at build or run time: the engine embeds the checked-in output.

Files (generated, checked in, never edited by hand):

| File | Holds |
| --- | --- |
| `schemas/v1/resolved-model.json` | JSON Schema 2020-12. The root describes one page; `$defs` holds every record, `exportFile` (CLI JSON) and `exportHeader` (CLI NDJSON first line); `x-contract` is the version; `x-scopes` maps each scope to its record's `$def`. Diagnostics `$ref` the existing `diagnostics.json`. |
| `docs/resolved-model.md` | The reference: version, how to get it, the envelope, a scope table, one section per scope with one table per record (Field, Type, Null, Meaning), shared records after, then "What templates see". Plain text links only, so the same bytes read well on the site and in a project. |

The schema sits beside the editing schemas in `schemas/v1/` (as `ledger.json` already does), so it is embedded with them
and copied with them. The Markdown is embedded from `docs/` as `Maquettiste.Engine.Contract.resolved-model.md`.
mkdocs.yml: "Reference > The resolved model: resolved-model.md", after "The migration ledger".

## 3. The version and the schema in every answer

`ResolvedContract` (src/Maquettiste.Engine/Editor/ResolvedContract.cs) holds `Version` (= `EngineVersion.Product`),
`SchemaFile` (`resolved-model.json`), the embedded bytes and the per-scope slice.

- **API and MCP page**: `ResolvedPage` gains `contract` (`"0.24.0"`) and `schema` (`"resolved-model.json"`), first in the
  JSON. A model with errors answers them too.
- **CLI `--format json`**: `{ "contract": "0.24.0", "schema": "resolved-model.json", "items": [ ... ] }` instead of a
  bare array. **NDJSON**: a first line `{"contract":"0.24.0","schema":"resolved-model.json"}`, then one record per line.
  This changes what scripts read today (section 9, question 1).
- The schema states the version twice: `x-contract` at the root and `contract: { const: "0.24.0" }` in the envelope, so
  validating an answer against another release's copy fails at once and says why.

## 4. get_schema and its equivalents

- MCP `get_schema` `kind: "resolved-model"` returns the whole file in the usual shape (`name`, `file`, `schema`,
  `references` with `diagnostics.json`). This already works once the file is in `schemas/v1/`; the tool text names it.
- `kind: "resolved-model:<scope>"` (as `custom:<type>`) returns a standalone schema for that scope: the envelope with
  `items` narrowed to the scope's record, and only the `$defs` it reaches. `resolved-model:all` is the whole file. An
  unknown scope is `not-found` listing the scopes. Answers add `contract`.
- The whole file is large (an estimated 150 to 250 KB); the tool text tells agents to ask for a scope.
- API: nothing serves schemas today. Proposed: `GET /api/model/resolved/schema?scope=` (operationId
  `getResolvedModelSchema`, viewer), the same slice, for hosted users with no project folder (section 9, question 2).
- CLI: no new command. The project copy is the CLI's answer, and `maquettiste mcp` exposes `get_schema`.

## 5. The project copy follows the release

`SchemaFolder` already copies every embedded schema to `.maquettiste/.schema/v1/` and compares bytes. `init`, the editor's
start and `maquettiste mcp`'s start refresh it; `validate` and `generate` warn MQ1008 while it differs.

- `resolved-model.json` rides along with no change: it is one more embedded schema.
- `SchemaFolder` gains one more managed file, `resolved-model.md`, compared and refreshed the same way (`Extras` still
  lists only `*.json`, so the Markdown is named explicitly).
- Both files carry the version, so every release changes their bytes: after an update, the next editor or MCP start
  refreshes them, and `validate` and `generate` warn MQ1008 until then. That is the "fresh reference with each point
  release". The MQ1008 text stays; it lists `resolved-model.json (out of date)` like any other file.

Release step: the "Version X" commit must regenerate the two files (the drift test fails until it does, and prints the
command). Run `MAQUETTISTE_UPDATE_GOLDEN=1 dotnet test tests/Maquettiste.Engine.Tests -c Release --filter
FullyQualifiedName~ResolvedContract`. The dev skill and the release memory get that line.

## 6. Template context versus the records

Templates (Scriban) and scripts do not read these records. They read the resolved objects the records are made from
(`TemplateMemberCatalog`: public R-type properties, snake_case). The reference's last section says so plainly:

- Names are snake_case (`is_abstract`), not camelCase.
- Another object is the object itself, not its id: `entity.base.name` works; a record holds `base: "<id>"`.
- Some shapes differ: `entity.mappings` is a map by database name (a list in the record); templates see members the
  records leave out (`own_attributes`, `trigger_points`, `operations`, ...).
- Calculated values are members (`element.calculated.<name>`), not the page's `calculated` map.
- Templates also get helpers and unit variables (`model`, `element`, the scope alias, `pack`, `mapping`, `hints`,
  `data`, `unit`).
- To list what one unit's templates can read: MCP `get_template_context` (pack, unit).

No second contract is designed here.

## 7. Tests

In tests/Maquettiste.Engine.Tests/Editor/ResolvedContractTests.cs:

1. **Drift**: regenerate both files and compare them byte for byte with `schemas/v1/resolved-model.json` and
   `docs/resolved-model.md`; on a difference, fail with the regenerate command (`MAQUETTISTE_UPDATE_GOLDEN=1` rewrites
   them). Also: `x-contract` equals `EngineVersion.Product`, and the embedded bytes equal the checked-in ones.
2. **Real output**: for each fixture model in tests/fixtures/models (billing, bindings, process-basics, processes,
   reference-data, schemas, showcase) and the built model `ResolvedNullContractTests.Uncovered` (operations, custom
   elements and what no fixture has; made shared), for every scope and `all`, page through `GetResolvedAsync`,
   serialize with the Web options and validate each page against the schema (JsonSchema.Net, already a dependency).
   Then list every record `$def` no page produced: the test fails unless the list equals a short, commented list of
   records no model reaches yet (to grow coverage, not to hide gaps).
3. **Plain words**: every record and field has a description; none cites an engineering document, a section or an
   erratum; every field that can be null says when (mentions `null` or "left out").
4. **Slices**: `resolved-model:<scope>` for every scope is a valid schema, and the pages of item 2 validate against their
   scope's slice too.
5. **Surfaces**: MCP `get_schema` (whole, slice, unknown scope); the page carries `contract` and `schema`; CLI export
   JSON and NDJSON shapes validate against `exportFile` / `exportHeader`; `init` writes and refreshes
   `resolved-model.md`; MQ1008 names it when stale. Existing tests that count schema files are adjusted. Functions
   contract tests cover the new page fields (and the endpoint, if approved).

## 8. Risks

- **Exporter details.** `JsonSchemaExporter` may shape something unexpectedly (a nullable value type, a dictionary of
  records, `JsonElement?`). Fallback: a small walker over `JsonTypeInfo.Properties`, same output. Moderate.
- **Size.** About 120 `$defs`; the JSON may reach 6,000 to 9,000 lines, the Markdown 2,000 to 3,000. The scope slice keeps
  agent reads small. The schema registry builds every embedded schema on first use, this one included; checked once.
- **Coverage.** Validation proves only the records the models produce; item 2's explicit list keeps the gap visible.
- **Two descriptions.** openapi.yaml keeps its hand-written record schemas (the editor's generated types depend on them).
  Both are tested against real output, so neither can silently drift, but wording may differ. Pointing openapi at
  `resolved-model.json` is a later, separate change.
- **Release coupling.** The Version commit must regenerate the files; the test makes forgetting impossible to merge.
- **MQ1008 after every update** until a start or `init` refreshes the copy. That is the intent, but people will see it.
- **Breaking CLI shape** (question 1).

## 9. Questions for the owner

1. **The CLI export file changes shape.** `model export --resolved --format json` becomes an object with `contract`,
   `schema` and `items` instead of a bare array, and NDJSON gains a first header line. Scripts that read the array today
   must read `.items`. Recommended: yes; it is the only way the file itself can say its version.
2. **An HTTP route for the schema** (`GET /api/model/resolved/schema`). A permanent addition; it helps teams that use
   the hosted editor and have no project folder. Recommended: yes.

## 10. Estimate

About 6 to 8 hours of agent time.

| Part | Hand-written lines |
| --- | --- |
| Writer (exporter transform, XML docs, Markdown) | 550 |
| Runtime (`ResolvedContract`, page fields, slices, `SchemaFolder`, embedding) | 200 |
| Surfaces (MCP `get_schema`, CLI envelope, API route, tool texts) | 150 |
| Doc comment rewrites | 150 edited |
| Tests | 450 |
| Docs (mcp.md, api.md, openapi.yaml, user-guide.md, releases.md, next.md, skill, mkdocs.yml) | 120 |

Plus the generated files (about 8,000 to 12,000 lines together).
