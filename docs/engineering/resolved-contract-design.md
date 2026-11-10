# The resolved-model contract: design

Status: built, released in 0.24.0 (pull request #78). Requested by the owner: "existing systems using maquettiste always have a
fresh reference that goes with their point release". The owner answered yes to both questions of section 9.

The contract describes what `model export --resolved`, `GET /api/model/resolved` and MCP `get_resolved_model` answer: one
JSON Schema and one Markdown reference, both generated from the engine's own record types, shipped in every project's
`.maquettiste/.schema/v1/`, and named by version in every answer. Until version 1 the contract version is the release's
version number and may change in any release. No freeze and no version-step check now.

## 1. How the records are built

- `ResolvedRecords` (src/Maquettiste.Engine/Editor/ResolvedRecords.cs) projects the resolved model (R-types, an object
  graph) into flat, typed C# records, with the views it reuses from `DatabaseViews.cs`, `QueryViews.cs` and
  `BindingViews.cs`: 127 record definitions reach the answer. Other objects appear by id. `ResolvedRecords.Scopes` maps the
  21 scopes to record kinds; `all` adds `verb`, `right` and `trigger-point` records.
- `GenerationService.GetResolvedAsync` pages them into a `ResolvedPage`: `contract`, `schema`, `items`, `next`,
  `diagnostics`, `calculated`.
- Every surface serializes with `JsonSerializerDefaults.Web` (camelCase, nothing ignored except the
  `[JsonIgnore(WhenWritingNull)]` members, such as `EntityRecord.checks`). The API (`ModelEndpoints.Resolved`) and MCP
  (`AgentTools.GetResolvedModelAsync`, shared by the MCP server and the editor's assistant) return the page as is.
- `docs/api/openapi.yaml` still describes the same records by hand (`x-engine-record`), checked against real responses
  by the Functions contract tests (section 8).

## 2. Generating the schema and the reference

Both generators live in the engine tests, next to the test that runs them:

- `ResolvedContractWriter` (tests/Maquettiste.Engine.Tests/Editor/ResolvedContractWriter.cs) builds the schema.
  - **Shapes**: a small walker over the serializer's own contract (`JsonTypeInfo` over the Web options), one `$defs` entry per
    record type. Nullability comes from the nullable annotations. Each record gets `additionalProperties: false`,
    `required` (every member but the `WhenWritingNull` ones, which may be left out), `x-order` (the serializer's order, as
    `SchemaConsistencyTests` require) and `kind` as a `const`. An open member (`object`, `JsonElement`) is `{}` when it may
    be null and `{"not": {"type": "null"}}` when it may not. The diagnostic record is generated from the `Diagnostic` type
    like the others, so the schema has no `$ref` to another file.
  - **Descriptions**: the engine's XML documentation file. A record's `<summary>` describes the record, a member's
    `<summary>` the field; `<see langword="null"/>` becomes `null`, `<c>x</c>` becomes `` `x` ``, "As `<cref>`" takes that
    member's text. A parenthesis that only cites an engineering document, and a mention of a resolver type, are dropped.
    A field that can be null or be left out, whose text does not say when, gets "Null otherwise." (after a condition such
    as "for a junction table") or "Null when not set."; a few comments were rewritten by hand.
- `ResolvedContractReference` (tests/Maquettiste.Engine.Tests/Editor/ResolvedContractReference.cs) renders the Markdown from
  the schema, with its prose (where to read it, scopes, how readers should treat unknown fields, what templates see).

Generated, checked in, never edited by hand:

| File | Holds |
| --- | --- |
| `schemas/v1/resolved-model.json` | JSON Schema 2020-12. The root describes one page; `$defs` holds every record, `record` (one of them by `kind`), `exportFile` (CLI JSON) and `exportHeader` (CLI NDJSON first line); `x-contract` is the version; `x-scopes` maps each scope, and `x-kinds` each record kind, to its definition. |
| `docs/resolved-model.md` | The reference: a section per scope with one table per record (Field, Type, Null, Meaning), then the records only `all` lists, the page's own records and "What templates see". Links point only inside the page. |
| `samples/reference-app/.maquettiste/.schema/v1/` | The reference app's copies of both, which gate 2's `check-model.mjs` compares (its `build-model.mjs` copies them). |

The engine embeds the schema with the other schemas of `schemas/v1/`, and the reference from `docs/` (the image's engine
stage copies that file). It renders nothing at run time. mkdocs.yml lists the page under "Reference".

## 3. The version and the schema in every answer

`ResolvedContract` (src/Maquettiste.Engine/Editor/ResolvedContract.cs) holds `Version`, `SchemaFile`, the embedded bytes
and the per-scope slices (each built once and kept).

- `Version` is the `x-contract` of the schema this build embeds, not the assembly's version, so an answer always agrees
  with the schema shipped beside it, a tool built with another `-p:Version` included. The drift test keeps `x-contract`
  equal to the repository's release (`EngineVersion.Product`).
- **API and MCP page**: `contract` and `schema` (`"resolved-model.json"`) come first. A model with errors answers them too.
- **CLI `--format json`**: `{ "contract", "schema", "items" }` instead of a bare array; **NDJSON**: a first line
  `{"contract": ..., "schema": "resolved-model.json"}`, then one record per line.
- The schema states the version twice, `x-contract` and `contract: { const }`, so validating an answer against another
  release's copy fails at once.

## 4. Reading the schema

- MCP `get_schema` `kind: "resolved-model"` (or `resolved-model.json`) returns `{ name, file, contract, schema, references }`
  with `references` empty. `resolved-model:<scope>` returns that scope's slice: `items` narrowed to the scope's record,
  `x-scope`, and only the `$defs` it reaches. The whole schema is about 300 KB; the tool text steers agents to a scope.
- `GET /api/model/resolved/schema?scope=` (`getResolvedModelSchema`, viewer) answers the same text.
- An unknown scope is `bad-request` everywhere: `get_schema`, `get_resolved_model`, and 400 `bad-request` from both routes.
- CLI: no command; the project copy is its answer.

## 5. The project copy follows the release

`SchemaFolder` copies every embedded schema and the reference to `.maquettiste/.schema/v1/` and compares bytes. `init`, the
editor's start and `maquettiste mcp`'s start refresh the folder; `validate` and `generate` warn MQ1008 while it differs.
When only the two resolved-model files differ, MQ1008 ends "they describe the resolved model this version answers" instead
of "IDEs check model files against these copies".

Both files carry the version, so every release rewrites them. The release notes and the reference say so and recommend one
Maquettiste version per repository.

Release step, in every "Version X" commit after the version bump (the dev skill names it): one command builds and writes
all four files for the new version, `MAQUETTISTE_UPDATE_GOLDEN=1 dotnet test tests/Maquettiste.Engine.Tests -c Release
--filter FullyQualifiedName~ResolvedContractTests.The_checked_in_schema_and_reference_match_the_record_types`. The next
build embeds them.

## 6. Template context versus the records

Templates (Scriban) and scripts do not read these records. They read the resolved objects the records are made from
(`TemplateMemberCatalog`: public R-type properties, snake_case). The reference's last section says how they differ:
snake_case names; another object is the object itself, not its id; some shapes differ (`entity.mappings` is a map by
database name) and templates see members the records leave out; calculated values are members; templates also get
helpers and unit variables. `get_template_context` lists what one unit can read. No second contract is designed here.

## 7. Tests

- **Drift** (`ResolvedContractTests`): regenerates all four files and compares them (CRLF read as LF); checks that the
  engine embeds the same bytes and that `ResolvedContract.Version` equals the release. The failure names the command.
- **Real output** (`ContractCheck.Validate`): builds each scope's page from `ResolvedRecords` (no `calculated` values) and
  validates it against the whole schema and the scope's slice; fails when a field is null in real output but the reference
  says it never is. Run on the seven fixture models and on models other tests build (uncovered kinds, approvals, rights,
  revisions, trigger points, migrations, the code-built billing model). `Every_record_is_produced_by_some_model` lists any
  definition none of them produces.
- **Credited records**: 19 definitions only feature tests produce (relation links, constraints, former codes, named trigger
  points, ...). `ContractCheck.Credited` names each test as `Class.Method`; that test calls `Validate` naming its records and
  fails when its model stops producing one, and `Every_credited_test_exists` fails when a named test is renamed or deleted.
- **Plain words**: every record and field has a description; none cites an engineering document, a section, an erratum or a
  resolver type; every field that can be null says when.
- **The page path**: the agent tools test pages every scope through `get_resolved_model` (small pages, whole and for one
  database) and validates each page; a Functions test validates the API pages, the schema route, and the openapi examples
  of `getResolvedModel`.
- **Surfaces**: `get_schema` (whole, file name, slice, unknown scope); CLI export JSON and NDJSON against `exportFile` and
  `exportHeader`; `init` and the editor start write the reference; MQ1008 names the files and its wording.

## 8. Risks left

- **Two descriptions.** openapi.yaml keeps its hand-written record schemas (the editor's generated types depend on them).
  Both are tested against real output, but wording may differ. Pointing openapi at `resolved-model.json` is a later change.
- **Coverage** is what the models produce; the credited list keeps the gap visible.
- **MQ1008 after every update** until a start or `init` refreshes the copy, and copies that change back and forth in a
  repository whose people run different releases.

## 9. Questions the owner answered

1. The CLI export file changes shape (`{contract, schema, items}`, and a header line in NDJSON): yes, before version 1 the
   contract may change; the release notes say what script authors update.
2. An HTTP route for the schema: yes.
