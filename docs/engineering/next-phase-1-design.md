# Next capabilities, phase 1: design

**Status (2026-11-02): built, in 0.11.0.** Phase 1 is the first phase of the roadmap of 2026-10-30, which `next.md`
lists with its item numbers ("scope 6.2" here is item 6.2 there): typed links, the migration ledger, batches and answers
that fit an agent, and scoped generation. The roadmap's later decisions of that day hold here, among them no dependency
on version control, and PostgreSQL, the CLI and MCP first. `engine-design.md` stays binding for everything this file
does not change, with `phase2-design.md`, `phase-3-design.md` and `snapshots.md`; `docs/api/openapi.yaml` and
`schemas/v1/` remain the source of truth for their contracts. Public engine signatures change only as written here.
File, type, member, rule, verb and tool names here are binding. Where code and this file disagree during the build, fix
one of them in the same change.

## 1. Scope and decisions

| Scope item | What phase 1 builds | Section |
| --- | --- | --- |
| 5.10 (baselines) | `--against <model snapshot id | folder>`: a baseline is another model, never a repository | 3.1 |
| 6.2 | The `migration` kind, one file per fact, the widening table, one classifier on every write surface, `validate --against`, fold, stale and held facts, projection, `migration list`, sql-ddl reading facts | 3.2 |
| 6.2 (ledger) | The ledger table contract (`schemas/v1/ledger.json`) and its PostgreSQL DDL | 3.3 |
| 6.8 | Index `nulls`, `range` and `multirange`, SQL objects with environments and run-once | 3.4 |
| 6.5 (name rules) | Index and constraint name rules per dialect; literal defaults checked against their type | 3.5 |
| 3.1 | `x-ref`, a schema-driven reference walker, names-to-ids | 4.1 |
| 3.2 | Stereotype groups, requires, conflicts, implies and typed parameters | 4.2 |
| 3.4 | Package membership for every kind (migration facts aside) and `dependsOn` layering | 4.3 |
| 3.9 | `x-translatable` and the tags-to-property refactor | 4.4 |
| 3.11 | `rationale`, `decision` and `status` on every element | 4.5 |
| 7.1 | Batches by name, with ids minted by the engine | 5.1 |
| 7.5 | MCP answers that fit an agent's context | 5.2 |
| 5.4 | Seeds by name in templates | 5.3 |
| 5.7 | Copy units and closing block markers | 5.4 |
| 5.9 | Template helpers and the null contract of the resolved model | 5.5 |
| 5.10 (scoped) | `generate --unit` and `--element` | 5.6 |
| 8.4 | Script rules that scale | 5.7 |

Decisions that hold across sections (each section lists its own under "Decisions taken here"):

1. **No version control.** The engine never reads git or any other version control. A baseline is a model snapshot
   id or a folder holding another copy of the project. The product also runs hosted at one URL, where there is no
   repository; there the baseline is a model snapshot.
2. **PostgreSQL first.** New typed DDL features and the ledger DDL target PostgreSQL. Other dialects report MQ4056,
   whose catalog text grows instead of new ids being taken.
3. **Engine, CLI and MCP first.** Phase 1 editor work is limited to regenerated types and mocks (`npm run gen`) and a
   green editor suite. The generic JSON form covers new kinds and fields. Each deferred screen is listed as a follow-up
   in its section.
4. **No compatibility settings** for installed bases that do not exist. The committed physical snapshot stays as an
   option because it is the baseline that needs nothing passed in.
5. **Sealing and squash are deferred.** A revision label with one hash per object is optional in the scope and not in
   the phase 1 brief. Squash needs the ledger tables of declared environments, which only the live reader (6.1,
   phase 3) can read.
6. **Acceptance tests copy the billing fixture** into a temporary folder and add what they need. The shared fixture
   does not change, so goldens and counts stay put.
7. **Rule ids** come from fixed blocks: MQ2009–MQ2023 and MQ7121–MQ7123 and MQ7212–MQ7214 (section 4), MQ41xx, a new
   "Migrations" family, and MQ4067–MQ4075 (section 3), MQ1012–MQ1016, MQ6030–MQ6039 and MQ5005–MQ5009 (section 5).
   Section 2 lists the ids each section uses.
8. **Release notes** go under `## 0.11.0 (unreleased)` in `docs/releases.md`; the owner sets the version and date at
   release. Each milestone also lands as a pre-release with a short anonymous note under `tmp/`.

## 2. Conventions, rules and surfaces

### 2.1 Tasks and verification

Each section ends with its tasks. Ids are unique across the design: A1 to A30 (section 3), B1 to B14 (section 4) and
C1 to C33 (section 5). A task names the files it owns, the tests it adds and a Verify line. Section 6 gives the order
and the dependencies. A file path without a project prefix is under `src/Maquettiste.Engine/`.

One legend serves every Verify line:

- `build` is `dotnet build maquettiste.slnx -c Release`. Every task runs it first, so Verify lines do not repeat it.
- `E "<f>"`, `C "<f>"`, `P "<f>"` and `F "<f>"` are
  `dotnet test tests/Maquettiste.<Engine|Cli|Packs|Functions>.Tests -c Release --filter "<f>"`, where `~X` stands for
  `FullyQualifiedName~X` and `|` separates alternatives. A letter without a filter runs that whole project.
- `PG` before a command sets `MAQUETTISTE_TEST_POSTGRES_CONTAINER=<container>`, so the tests that need a PostgreSQL
  server run.
- `record` is `MAQUETTISTE_RECORD=1 F "~ProjectAndModelTests"`, then `F "~ProjectAndModelTests"` without the
  variable. The rewritten `src/editor/src/mocks/recorded/validation-rules.json` is intended.
- `editor` is `(cd src/editor && npm run gen && npm run check:generated && npm run typecheck && npm test)`.
- `copies` is `diff -r schemas/v1 samples/reference-app/.maquettiste/.schema/v1`, which must print nothing.
- `MAQUETTISTE_UPDATE_GOLDEN=1` is set only where a Verify line says so, once, and the task names the intended change.

Pinned counts move by relative steps. A task that adds a rule raises the `CatalogTests` pin by the number it adds. A
task that adds a tool raises the tool pins its section names by the step it states, from whatever the pin reads when
the task lands. The MCP tests live in the classes of `tests/Maquettiste.Cli.Tests/McpServerTests.cs`:
`McpSurfaceTests` (the Tools array, the counts and the docs list), `McpReadTests`, `McpWriteTests` and
`McpGenerationTests`. No class is named `McpServerTests`.

Each milestone ends with a gate (section 6): the full build, every .NET suite, the pack tests against PostgreSQL, the
editor suite, a short anonymous pre-release note under `tmp/` and a commit script the owner runs. Notes and examples
use billing and invented names only.

### 2.2 Rules

New and changed rules, by id. None of the new ids is in `src/Maquettiste.Engine/Diagnostics/RuleCatalog.cs` today
(checked on 2026-10-30). The catalog grows by 42 rules: 15 in milestone A, 16 in B and 11 in C.
`RuleCatalog.FamilyLabels` gains `MQ41xx` = "Migrations". Ids left free in the reserved blocks (MQ2012, MQ2023,
MQ4071 to MQ4075, MQ5006 to MQ5009, MQ6035 to MQ6039, MQ7122, MQ7123, MQ7214) stay reserved for phase 1 follow-ups.

| Id | Severity | Rule | Section |
| --- | --- | --- | --- |
| MQ1012 | E | A name or placeholder in a batch finds nothing. | 5.1 |
| MQ1013 | E | A name in a batch finds more than one element or sub-element. | 5.1 |
| MQ1014 | E | A write misuses an identity: a placeholder declared twice, a misplaced `as`, two `expectedHashes` keys for one element, or a sub-element dropped and added again without its id. | 5.1 |
| MQ1015 | E | A name in a batch is malformed or misplaced. | 5.1 |
| MQ1016 | E | A JSON Patch cannot be applied; nothing is written. | 5.2 |
| MQ2009 | E | An `x-ref` keyword is malformed or misplaced, or sits beside a `patternProperties` pattern that needs backtracking. | 4.1 |
| MQ2010 | E | A value an `x-ref` types is a name or other text, not an id. | 4.1 |
| MQ2011 | E | An `x-ref` value is out of scope. | 4.1 |
| MQ2013 | E | An element carries two stereotypes of one group, own or implied. | 4.2 |
| MQ2014 | E | An element lacks a stereotype that one of its stereotypes requires. | 4.2 |
| MQ2015 | E | An element carries two stereotypes that conflict. | 4.2 |
| MQ2016 | E | A stereotype's `requires`, `conflicts` or `implies` is invalid. | 4.2 |
| MQ2017 | E | A stereotype implies one that implies others; implication is one level. | 4.2 |
| MQ2018 | E | A property value or a default fails a stereotype's `parameters`. | 4.2 |
| MQ2019 | E | A stereotype's `parameters` is not a valid schema. | 4.2 |
| MQ2020 | E | A reference from one package to another that its package does not depend on. | 4.3 |
| MQ2021 | E | `dependsOn` names the package itself or forms a cycle. | 4.3 |
| MQ2022 | W | A reference to a retired element or sub-element from one that is not retired (membership, owning references and diagrams excepted). | 4.5 |
| MQ3019 | E | Changed text: a column's literal default is checked against its resolved length and type; a `json` column takes a string of JSON text. | 3.5 |
| MQ4001 | E | Changed behaviour: the resolver measures identifiers in the dialect's unit. | 3.5 |
| MQ4056 | W | Changed text: also migration fact steps and ledger rows, index `nulls`, range columns and run-once SQL objects outside PostgreSQL. | 3.2, 3.4 |
| MQ4057 | E | Changed text: also a misplaced or missing range `subtype`. | 3.4 |
| MQ4067 | W | The ledger schema is also a schema of the database's modeled objects. | 3.3 |
| MQ4068 | E | Two index or constraint names collide in a namespace the dialect keeps. | 3.5 |
| MQ4069 | E | A view, routine or SQL object depends on a SQL object that does not run in every environment it runs in. | 3.4 |
| MQ4070 | W | A SQL object with environments has phase `before`. | 3.4 |
| MQ4101 | E | A migration fact lacks the intent its change needs. | 3.2 |
| MQ4102 | E | A change since the baseline needs a migration fact and has none. | 3.2 |
| MQ4103 | W | An open fact the baseline lacks is stale. | 3.2 |
| MQ4104 | E | The `after` links of a database's open facts form a cycle. | 3.2 |
| MQ4105 | E | A fact's `after` link names a fact of another database. | 3.2 |
| MQ4106 | E | A fact's fields do not fit its type. | 3.2 |
| MQ4107 | E | Two open facts change the same object and neither comes after the other. | 3.2 |
| MQ4108 | W | A fact's SQL text has no entry for the database's dialect and no `*` entry. | 3.2 |
| MQ4109 | W | The baseline has errors that leave objects unchecked. | 3.2 |
| MQ4110 | I | A write recorded no facts for objects whose elements have errors. | 3.2 |
| MQ4111 | W | A fact's `after` link names a fact that does not exist. | 3.2 |
| MQ5005 | W | A rule file declares top-level `let` or `var`, so its results are never cached. | 5.7 |
| MQ6030 | E | A copy unit's source pattern matches no pack file. | 5.4 |
| MQ6031 | E | A copy unit's token names an undeclared parameter or one whose value is not a string. | 5.4 |
| MQ6032 | E | A generation filter names an unknown pack or unit, or an ambiguous bare unit id. | 5.6 |
| MQ6033 | E | A run limited by unit or element renders a unit that reads the schema diff. | 5.6 |
| MQ6034 | W | A generation filter selects no unit and no output. | 5.6 |
| MQ7121 | E | Two seeds of one table key their rows differently. | 4.3 |
| MQ7212 | E | `x-translatable` is not on a top-level string property of an extension file or stereotype parameters. | 4.4 |
| MQ7213 | W | `x-translatable` is on a property whose schema applies only to kinds without translations. | 4.4 |

MQ6013 keeps its text and reserves more names: 5.5's helpers and variables and 3.3's `ledger_ddl`.

### 2.3 Schema files

Every file created or changed under `schemas/v1/` gets a byte-identical copy under
`samples/reference-app/.maquettiste/.schema/v1/` in the same task, and `copies` checks it. A change to
`docs/api/openapi.yaml` goes with `editor`, which regenerates the editor's types and mocks.

| File | Change | Section |
| --- | --- | --- |
| `migration.json` | New kind: one migration fact per file | 3.2 |
| `ledger.json` | New, not a kind: the ledger rows as a document | 3.3 |
| `snapshot.json` | `facts`; column `type` takes `columnType`, column `subtype`, index column `nulls` | 3.1, 3.4 |
| `maquettiste.json` | `migrations.record`, `migrations.ledgerSchema`; `limits.scriptTimeoutMs` 0 and the `scriptStatements` cap | 3.2, 3.3, 5.7 |
| `common.json` | `columnType`, `rangeSubtype`; `xRef`; `status` and the attribute's three annotation keys | 3.4, 4.1, 4.5 |
| `table.json` | Column `type` and `subtype`, index column `nulls`; `package`; the column's annotation keys | 3.4, 4.3, 4.5 |
| `sql-object.json` | `environments`, `apply`; `package` | 3.4, 4.3 |
| `stereotype.json` | `group`, `requires`, `conflicts`, `implies`, `parameters`; `package` | 4.2, 4.3 |
| `package.json` | `dependsOn` | 4.3 |
| The 14 kind schemas 4.3 lists | `package` | 4.3 |
| Every kind schema, `enum.json`, `database.json`, `category-tree.json` | `rationale`, `decision`, `status` | 4.5 |
| `locale.json` | `properties` and `src` keys for property texts | 4.4 |
| `batch.json` | `names-to-ids`, `elementIds`; `tags-to-property`, `prefix`; `as`, `placeholder`, `qualifiedName`, `idOrName` | 4.1, 4.4, 5.1 |
| `pack.json` | Unit `copy` mode, `sources`, `tokens`, `blockCommentEnd`; `each database` and `each attribute`; `x-ref` in descriptions | 5.4, 5.5, 4.1 |
| `extension.json` | Descriptions (`x-ref`, `x-translatable`) | 4.1, 4.4 |

### 2.4 CLI verbs and flags

| Command | Change | Section |
| --- | --- | --- |
| `maquettiste validate [--against <snapshot id or folder>]` | Compares with a baseline; exit 1 on errors, 4 when the value resolves to nothing | 3.2 |
| `maquettiste generate [--against <snapshot id or folder>]` | Diffs from a baseline for one run | 3.1 |
| `maquettiste generate [--unit <pack/unit or unit>]... [--element <id>]...` | Scoped generation | 5.6 |
| `maquettiste migration list [--database <name or id>] [--format text\|json]` | New group `migration` | 3.2 |
| `maquettiste migration fold [--database] [--against] [--dry-run] [--format]` | Joins and removes open facts | 3.2 |
| `maquettiste migration ledger [--database] [--grant-read <role>] [--format] [--output <file>]` | Prints the ledger DDL | 3.3 |
| `maquettiste model apply <batch.json> [--dry-run] [--format text\|json]` | Applies a batch by name | 5.1 |

`--against`, `--grant-read` and `--unit` join `CommandLine.ValueOptions`; `--element` is there already. The `migration`
group is distinct from the `migrate` format stub.

### 2.5 MCP tools

Net change to the pinned counts over phase 1: MCP tools +5 (+3 in milestone A, +2 in milestone C) and
`AgentTools.All` +2 (both in milestone A). When this was written the pins read 66 and 25.

| Tool | Change | Section |
| --- | --- | --- |
| `list_migrations` | New catalog tool (MCP and the assistant) | 3.2 |
| `fold_migrations` | New | 3.2 |
| `get_ledger_ddl` | New catalog tool | 3.3 |
| `patch_element` | New | 5.2 |
| `generate` | New: plan and apply in one call, with `units`, `elements` and `against` | 5.2 |
| `validate` | `against` | 3.2 |
| `plan` | `against`; `units`, `elements` | 3.1, 5.6 |
| `save_element`, `create_element`, `delete_element` | Answers carry `facts` | 3.2 |
| `apply_batch` | `facts`; names, placeholders, minted ids, `ids`, `dryRun`; the `names-to-ids` and `tags-to-property` ops | 3.2, 5.1, 4.1, 4.4 |
| `get_references` | Rows carry `optional` and `schema` | 4.1 |
| `delete_element` | Describes the `x-ref` and pack parameter rules | 4.1 |
| `get_schema` | `stereotypeParameters` | 4.2 |
| `get_translations`, `set_translations`, `localization_status` | Property fields `properties/<name>` | 4.4 |
| `get_resolved_model` | Scope `migrations` | 3.2 |
| `get_project` | Workspace from the host; a summary with sections on request | 3.1, 5.2 |
| `list_validation_rules`, `tag_usage` | Summaries with pages | 5.2 |
| `preview_unit` | Binary files of copy units | 5.4 |
| `list_extension_files` | `wholeModel` and MQ5005 | 5.7 |

### 2.6 Public engine API

An index; each section states the signatures.

| Section | Added or changed |
| --- | --- |
| 3.1 | `ModelBaseline`, `BaselineKind`; `GenerationRequest.Against` (text); `GenerationService.Baselines`, `AllowFolderBaselines`; `PhysicalSnapshot.Facts`; `WorkspaceInfo.Detect` removed; `AgentToolsOptions.Workspace` (a `WorkspaceInfo`), `AllowFolderBaselines` |
| 3.2 | `ElementKind.Migration`, `Migration` and its parts, `MigrationSettings`, `ProjectSettings.Migrations`; `Widening` and its types; `RMigration` and its parts, `RDatabase.Migrations`, `ResolvedModel.Migrations`, projections on tables, views, sequences and columns; `SchemaDiffResult.Facts`, `FromState`, `ToState`, `FromBaseline`; `MigrationRecord`; `ModelStore.ValidateAgainstAsync`, `ListMigrationsAsync`, `FoldMigrationsAsync` and their records; `Facts` on `SaveResult`, `SettingsSaveResult`, `DefaultLocaleResult`, `BatchResult` and `BatchPreview` |
| 3.3 | `LedgerContract`, `LedgerDdlAnswer`, `ModelStore.GetLedgerDdlAsync`, `MigrationSettings.LedgerSchema`, `RDatabase.LedgerSchema` |
| 3.4 | `BuiltinTypes` additions; `Column.Subtype`, `IndexColumn.Nulls`, `IndexNulls`, `SqlObject.Environments`, `SqlObject.Apply`, `SqlObjectApply`; their snapshot and resolved counterparts; `RSqlObject.ContentHash` |
| 4.1 | `ReferenceInfo` (`Optional`, `Schema`); `BatchOp.NamesToIds`, `BatchOp.TagsToProperty`; `BatchOperation`, whole record |
| 4.2 | `StereotypeKeysAttribute`, `Stereotype` members, `StereotypeParameters`, `AppliedStereotype`, `ModelSnapshot.EffectiveStereotypes`, `RStereotype.ImpliedBy` |
| 4.3 | `IPackaged`, `Package` on 14 kinds, `Package.DependsOn`, `RAnnotated.Package` (moved), `RPackage.DependsOn` |
| 4.4 | `TranslationEntry.Properties`, `LocalizableNode` members, `LocalizationIndex` members |
| 4.5 | `ElementStatus`; `Rationale`, `Decision`, `Status` on `ElementBase` and `RAnnotated` |
| 5.1 | `BatchIds`, `MintedId`, `BatchResult` and `BatchPreview` in their final form, `BatchPreviewFile.Diff`, `ObjectLayout.RequiresId` |
| 5.2 | `AgentProjectInfo` (changed), `AgentPackSummary`, `AgentDiagnosticCounts`, `TagUsageSummary`, `TagUseSummary`, `TagUseElements`, `ModelStore.PatchAsync`, `RuleCatalogSummary`, `RuleFamilySummary`, `RuleCatalogPage`, `RuleCatalog.Summarize`, `RuleCatalog.Page` |
| 5.3 | `RSeed.QualifiedName` |
| 5.4 | `PackUnit.Template` (nullable), `Sources`, `Tokens`, `BlockCommentEnd`; `OutputMode.Copy`; `RenderedFile.Bytes`, `Binary` |
| 5.5 | `ResolvedModel.Stereotypes`, `TagVocabularies`, `CategoryTrees`; `RStereotypeDefinition`, `RTagVocabulary`, `RTagDefinition`, `RCategoryTree`, `RCategory` members; eight members become nullable |
| 5.6 | `GenerationRequest.Units`, `Elements` |
| 5.7 | `ScriptRegistration.WholeModel`, `IScriptSandboxPool.Warnings`, `IScriptSandbox.RunModelRule`, `ExtensionRuleInfo.WholeModel` |

## 3. Milestone A: the migration ledger

Milestone A builds scope 6.2 and the baseline half of 5.10: a `migration` kind that holds one change fact per file, one
classifier that records facts on every write, and `validate --against` a model snapshot or a folder. It comes first in
phase 1 because every later feature that reshapes a schema (3.3, 6.7, 4.5 of the scope) needs the fact formats (scope
11). Nothing here reads version control, a ledger table or a live database. The ledger table contract is section 3.3.

### 3.1 Baselines

**Decisions taken here**

1. A baseline is another model, never a repository (scope 5.10, 15): a model snapshot id (`model-snapshots/<id>.zip`,
   read by `Snapshots/ZipDocumentStore.cs`) or a folder that holds another copy of the project. It loads read-only
   through the document store seam (`Loading/ModelDocumentStore.cs`), with its own `maquettiste.json`, without packs
   or templates.
2. `--against <value>` resolves in one order. A value that is rooted, starts with `.` or holds a path separator is a
   folder. Otherwise it is a snapshot id when the archive exists, else a folder when the directory exists, else a
   usage error (exit 4). A folder is a project root (holding `.maquettiste/maquettiste.json`) or the `.maquettiste`
   folder itself.
3. Loading is public on the engine (`ModelBaseline`): the editor host has no internals access. Only `maquettiste` itself
   accepts folders. The Functions host accepts snapshot ids only, since a path in a request would name a folder of the
   server. Engine catalog tools serve both hosts, so they learn which one they run in from
   `AgentToolsOptions.AllowFolderBaselines`, which only `maquettiste mcp` sets.
4. A folder gets a new read-only document store. Code note: `FileDocumentStore` reports `IsReadOnly => false`, so it
   cannot serve as is. The new store refuses writes with MQ6029, as `ZipDocumentStore` does, and skips the index
   cache, so neither the baseline folder nor the user cache is written.
5. The committed physical snapshot (`.maquettiste/snapshots/<db>.json`) stays as an option (scope 15.2): it remains
   generation's default schema-diff baseline, the one that needs nothing passed in. Code note: the scope says it is
   rewritten after every apply; the code rewrites it only for databases with a non-empty diff
   (`Generation/GenerationRun.cs:439-453`) and when materialize records aliases (`ModelStore.Materialize.cs:45-49`).
6. `generate --against` replaces that baseline with a model for one run and never writes the committed snapshot: a
   team that drops it still gets migrations (5.10). `validate --against` (3.2) compares two models and reads no
   physical snapshot, ledger table or database (6.2); a read-only store's `services.Snapshots` reads the working
   project's files (`SnapshotLibrary.cs:277`), so it is never consulted. Databases match by element id, never name.
   Both read the baseline under today's keys: the working model's open `rekey` facts that the baseline lacks are
   applied to the baseline's capture first (3.2), so a table that materialize stored as a file is one table, not a
   drop and a create.
7. The engine reads no version control (scope 6.2, done-when line). Code note: today `WorkspaceInfo.Detect` reads
   `.git/HEAD` and a linked worktree's `.git` file so that `get_project` can name the branch
   (`WorkspaceInfo.cs:37-68`, `Agent/AgentTools.Handlers.cs:128`). Detection moves to the two hosts, which pass what
   they found to the engine; the engine keeps the `WorkspaceInfo` record as plain data. The editor host's top-bar git
   status (`_functions/GitStatus.cs`) stays a host feature. Both read nothing outside a checkout, so neither is a
   dependency.
8. `GenerationRequest` carries the baseline as text (`Against`: the snapshot id, or the folder's full path), and the
   run opens it. A plan stores its request as JSON and `apply_plan` plans again from it, so the text survives the round
   trip and apply reopens the same baseline; a baseline that changed in between makes the plan stale. A run with a
   baseline never answers from or writes the last-run record, whose key holds no baseline.
9. Decision 2 in detail (A1). The `.maquettiste` folder itself counts only when it carries that name: another folder
   that holds a `maquettiste.json` (the settings schema under `.maquettiste/.schema/v1`, for one) is not a project. `/`
   and `\` both count as path separators on every platform; no snapshot id holds either, so a Windows-style path is a
   folder on any system. A model snapshot whose metadata names a newer model format is refused with MQ1007's text, as
   a folder is and as an import refuses it.
10. A long-lived host detects the workspace once, when it starts (A2): `maquettiste mcp` and the editor's assistant
    (`AssistService`, a singleton) build their `AgentToolsOptions` then, so after a branch switch `get_project` reports
    the old branch until the host restarts, while `GET /api/project` detects per request. `docs/mcp.md` says to restart
    the server. `MAQUETTISTE_WORKSPACE`, the container form's source, was already read once at start.
11. The image's CLI stage (`docker/Dockerfile`) copies only what the CLI's project graph reads, so it copies the
    linked `src/Maquettiste.Functions/_functions/WorkspaceDetection.cs` too (A2).
12. A3. "The plan's input hash includes the baseline's `ModelHash`": a plan has no single input hash, so the baseline's
    `ModelHash` folds into the hash of every `d:` key (`DependencyHasher`, `EngineServices.CreateHasher` gain an
    optional baseline hash). The units that read a schema diff go stale when the baseline changes; the others, which the
    baseline cannot change, do not. The hosts turn a value into the request's text with the internal
    `ModelBaseline.RequestText` (the snapshot id when its archive exists, else the full folder path). `maquettiste mcp`
    builds its `GenerationService` with `AllowFolderBaselines` and one `SnapshotLibrary` (`Commands/McpCommand.cs`),
    since the two are init properties; that library is the server's only one: the snapshot tools and the catalog's
    tools (`AgentTools`) use the generation service's `Baselines` when it is set, so a snapshot loads once per host.
    `apply_plan` answers `bad-request` when the plan's baseline no longer opens, and `POST /api/generate/plan` checks
    the snapshot id before it queues the job. `ModelBaseline.OpenAsync` throws the internal `BadBaselineException` (an
    `ArgumentException`), so `generate` (a usage error) and MCP `plan` and `apply_plan` (`bad-request`) relabel only a
    baseline that does not open, never another argument error of the run. `--watch` refuses `--against` (a baseline
    serves one run).
13. A3. Baseline errors in `generate --against` follow `validate --against` (decision 33 of 3.2): the objects of
    baseline owners with errors are left out of both captures, so they read neither as dropped nor as added; a database
    the baseline holds that does not resolve there, or whose folder holds baseline files that did not load while the
    baseline lacks it, is not diffed (its diff is empty), since a diff from nothing would create objects that may exist.
    Each case is one MQ4109 on the database file in the run's diagnostics. Plain validate's stale chains now read
    `PhysicalSnapshot.Facts` as the rendered facts (empty until A15 fills them).

**Model and schema**

`schemas/v1/snapshot.json` (and its copy under `samples/reference-app/.maquettiste/.schema/v1/`), root, after `aliases`:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `facts` | array of migration ids, `x-sort` `.` (3.2, decision 19), unique | `[]` | Facts already rendered into a migration from this snapshot's history: the `Facts` of each applied diff (3.2, sql-ddl). Held and pending facts never enter. Ids gone from the model are dropped on write. |

**Public API**

```csharp
namespace Maquettiste.Engine;
/// A read-only model to compare with (scope 5.10). A folder baseline owns its store; a snapshot's store stays in the
/// library's open cache (MaxOpen = 2), so Dispose leaves it alone.
public sealed class ModelBaseline : IDisposable {
    public static Task<ModelBaseline> OpenAsync(ModelStore working, string against, string currentDirectory,
        bool allowFolders, SnapshotLibrary? library, CancellationToken ct);
        // ArgumentException naming the value when it resolves to nothing. library: the host's, so a long-lived host
        // loads one archive once; null builds one for this call.
    public BaselineKind Kind { get; }   public string Against { get; } /* as given */   public void Dispose();
    public string Label { get; }        // "snapshot <id>" or "folder <full path>"
    public ModelStore Store { get; }    // IsReadOnly is true
    public string ModelHash { get; } }  // every document's path and hash, as SnapshotArchive.WithCounts computes it
public enum BaselineKind { Snapshot, Folder }
// GenerationRequest gains: public string? Against { get; init; }  // snapshot id or full folder path (decision 8)
// GenerationService gains: public SnapshotLibrary? Baselines { get; init; }  // the host's library; null: one per run
//                          public bool AllowFolderBaselines { get; init; }   // false; true only in maquettiste
// PhysicalSnapshot (Maquettiste.Engine.Model) gains: public IReadOnlyList<string> Facts { get; init; } = [];

// WorkspaceInfo keeps its record and Variable; the static Detect method is removed (decision 7).
public sealed record WorkspaceInfo(string? Workspace, string? Branch, string? Worktree, string? Repository) {
    public const string Variable = "MAQUETTISTE_WORKSPACE";
    public static WorkspaceInfo None { get; } }        // nothing known: four nulls
// AgentToolsOptions: `public string? Workspace { get; init; }` becomes
//     public WorkspaceInfo Workspace { get; init; } = WorkspaceInfo.None;   // what the host detected
// and gains
//     public bool AllowFolderBaselines { get; init; }                      // false; true only in maquettiste mcp

namespace Maquettiste.Functions;   // _functions/WorkspaceDetection.cs, BCL only; the CLI compiles the same file
public static class WorkspaceDetection {
    public static WorkspaceInfo Detect(string repoRoot, string? configured); }  // today's WorkspaceInfo.Detect body
```

**Engine behaviour**

- *Snapshot.* `library.OpenAsync(id)` (existing; a new `SnapshotLibrary(working)` when no library is given): a
  read-only `ModelStore` over `ZipDocumentStore(file, includeTemplates: false)`. The Functions host passes its DI
  `SnapshotLibrary`; the MCP server's `ModelTools` and each `AgentTools` keep one library for their life.
- *Folder.* `ReadOnlyFolderDocumentStore` (internal, `Loading/ModelDocumentStore.cs`) reads as `FileDocumentStore`
  does; `IsReadOnly => true`, `UsesIndexCache => false`, `ApplyAsync` returns the `ReadOnlyPathPolicy` refusal.
  `ModelBaseline` builds `EngineOptions { RepoRoot, ModelRoot, CacheDirectory = working's }` and
  `EngineServices.CreateReadOnly(options, store)`. A newer `formatVersion` is an `ArgumentException` with MQ1007's
  text. Load and validation errors do not stop the open; 3.2 says how they are used (MQ4109).
- *Cost.* One cold load (no index cache), validation and resolution of the baseline: about 1.7 s on the bench model
  (docs/engineering/status.md, gate 1), so `validate --against` costs about two plain validates, on request only. A
  snapshot baseline stays loaded in the host's library, so a long-lived host pays the load once per archive; a
  one-shot CLI command pays it per run.
- *`generate --against`.* `GenerationService` opens `request.Against` inside the run (`ModelBaseline.OpenAsync` with its
  `Baselines` library and `AllowFolderBaselines`) and disposes it when the run ends; a value that resolves to nothing is
  the run's `ArgumentException`. `GenerationRun.SchemaDiffsAsync` takes the opened baseline. Each working database's
  baseline (same id) is captured as `validate --against` captures it (3.2): under today's keys, the open `rekey` facts
  the baseline lacks applied first. It is diffed with `SchemaDiffer.Compare(baseline, current, database)` instead of
  `DiffAndCapture`. No `PreparedSnapshot` is made, `SaveSnapshotsAsync` writes nothing, check mode adds no MQ6018. A
  database the baseline lacks diffs from nothing. The plan's input hash includes the baseline's `ModelHash`;
  `apply_plan` plans again from the stored request, which opens the baseline again, so a changed baseline makes the plan
  stale. Like 5.6's scoped runs, a run with `Against` never answers from or writes the last-run record and forgets it
  before applying (`LastRun.Key` holds no baseline). Template previews keep the committed snapshot (`LazySchemaDiffs`,
  which applies the same rekey step, 3.2).
- *No version control.* No engine code starts a version-control tool or reads under a `.git` folder. `get_project`
  reports `AgentToolsOptions.Workspace`. `maquettiste mcp` fills it with `WorkspaceDetection.Detect(repo,
  MAQUETTISTE_WORKSPACE)`; the editor host does the same in `ProjectEndpoints` and `AssistService`. The CLI compiles
  `src/Maquettiste.Functions/_functions/WorkspaceDetection.cs` through a linked `<Compile Include>` in
  `Maquettiste.Cli.csproj`, so one copy exists; no project references both hosts. `Writing/OutputPathPolicy.cs` keeps
  naming `.git` as a write guard: it refuses writes under `.git` and lets `init --hooks` write two hook files, which
  reads nothing. A team checks its base out into a folder.

**CLI and MCP**

- `--against` joins `CommandLine.ValueOptions` (`src/Maquettiste.Cli/CommandLine.cs:17-23`).
- `maquettiste generate [--against <snapshot id | folder>]`, with `--check` and `--dry-run` as today.
- MCP `plan` gains `against` (snapshot id or folder), as do section 5's `generate` tool and `validate` (3.2). The CLI
  MCP server allows folders. Catalog tools check `AgentToolsOptions.AllowFolderBaselines`, so the editor's assistant
  answers `bad-request` for a folder.
- The CLI and its MCP server resolve a relative folder against their current directory and pass the full path, so a
  stored plan reopens the same folder. `POST /api/generate/plan` takes `against` in its body (`GenerationRequest` in
  `openapi.yaml`); the Functions host's `GenerationService` keeps `AllowFolderBaselines` false and uses its DI
  `SnapshotLibrary`, so a folder answers 400 `bad-request`.
- HTTP `POST /api/validate?against=<snapshot id>` (`ValidationEndpoints.cs`); a folder answers 400 `bad-request`.

**Rules.** None new. An unresolvable `--against` is a usage error (exit 4), MCP `bad-request`, HTTP 400.

**Docs.** New `docs/guides/schema-changes.md`, part "Baselines" (a snapshot per deploy, a folder in CI, a snapshot
before an agent starts) and its `mkdocs.yml` line; `docs/user-guide.md` CLI reference; `docs/mcp.md`; `openapi.yaml`;
`docs/releases.md` under "## 0.11.0 (unreleased)"; the modeling skill. `src/Maquettiste.Functions/README.md` names
`WorkspaceDetection.cs` and the CLI's link to it.

**Tests**

- Engine `Snapshots/ModelBaselineTests.cs`: `A_snapshot_id_opens_a_read_only_baseline`,
  `A_folder_opens_a_read_only_baseline_that_refuses_writes`, `A_path_like_value_is_always_a_folder`,
  `A_folder_baseline_reads_its_own_settings`, `A_baseline_writes_nothing_into_its_folder_or_the_cache` (hashes every
  file before and after), `An_unknown_value_names_itself_in_the_error`,
  `A_host_held_library_opens_a_snapshot_baseline_once`. Engine `SchemaSnapshotTests`:
  `Generate_against_a_baseline_diffs_from_it_and_leaves_the_committed_snapshot_alone`,
  `Generate_against_a_baseline_after_materialize_plans_no_drop` (billing copy: baseline taken, `invoice` stored as a
  file with `materialize-tables`; the diff holds no dropped or added table) and
  `Plan_against_then_apply_plan_uses_the_same_baseline` (the stored request holds the text, apply reopens it, and a
  baseline changed in between is refused as stale). Engine
  `LastRunTests.Generate_against_never_replays_or_writes_the_last_run_record`. CLI
  `GenerateTests.Generate_against_an_unknown_value_exits_4`. Functions
  `SnapshotEndpointTests.Validate_against_accepts_snapshot_ids_and_refuses_folders` (existing file) and
  `AssistTests.Validate_against_a_folder_through_the_assistant_answers_bad_request`.
- Done when "nothing in the engine reads version control": Engine `Integration/NoVersionControlTests.cs`,
  `Baselines_load_from_snapshots_and_folders_with_no_version_control` (temporary folders without `.git`; a `.git`
  folder with a corrupt `HEAD` in the working copy changes nothing) and `Engine_source_reads_no_version_control`. The
  scan covers `src/Maquettiste.Engine/**/*.cs` and the engine csproj. It fails on the exact string literals `".git"`,
  `"HEAD"`, `"gitdir:"` and `"git"`, on a package reference whose name holds `git`, and on `Process.Start`,
  `new Process(` or `ProcessStartInfo` outside `PostProcessing/FormatterRunner.cs` (the project's own formatters; the
  model kind `Process` is not matched). Its one named exception is `Writing/OutputPathPolicy.cs`, the write guard
  above. Functions `WorkspaceDetectionTests` takes over
  the detection cases of Engine `VersionAndWorkspaceTests` unchanged.

**Tasks** (legend in section 2, order in section 6)

- **A1. Load a baseline.** `ModelBaseline` (with the `library` parameter) and the read-only folder store. Owns
  `Snapshots/ModelBaseline.cs`, `Loading/ModelDocumentStore.cs`. Tests: `ModelBaselineTests`,
  `NoVersionControlTests.Baselines_load_from_snapshots_and_folders_with_no_version_control`. Verify: `E
  "~ModelBaselineTests|~NoVersionControlTests"`.
- **A2. Workspace detection leaves the engine.** `WorkspaceInfo.Detect` moves to `WorkspaceDetection`;
  `AgentToolsOptions.Workspace` takes a `WorkspaceInfo`. Owns `src/Maquettiste.Engine/WorkspaceInfo.cs`,
  `Agent/AgentTools.cs` (options), `Agent/AgentTools.Handlers.cs` (`get_project`),
  `src/Maquettiste.Functions/_functions/{WorkspaceDetection,ProjectEndpoints,AssistService}.cs`,
  `src/Maquettiste.Functions/README.md`, `src/Maquettiste.Cli/{Maquettiste.Cli.csproj,Commands/McpCommand.cs,
  Mcp/ModelTools.cs}` (constructor), `docker/Dockerfile` (the CLI stage, decision 11),
  `tests/Maquettiste.Engine.Tests/VersionAndWorkspaceTests.cs`,
  `tests/Maquettiste.Functions.Tests/WorkspaceDetectionTests.cs`, `NoVersionControlTests` (the source scan). Tests:
  `Engine_source_reads_no_version_control`, `WorkspaceDetectionTests`; the workspace cases of `McpReadTests` unchanged.
  Verify: `dotnet build src/Maquettiste.Functions -c Release -p:HostUsings=true`; `E
  "~NoVersionControlTests|~VersionAndWorkspaceTests"`; `F
  "~WorkspaceDetectionTests|~ProjectAndModelTests|~AssistTests"`; `C "~McpReadTests"`.
- **A3. Generate against a baseline** (after A12, whose capture helper it reuses). `GenerationRequest.Against` as
  text, `GenerationService.Baselines` and `AllowFolderBaselines`, the baseline opened inside the run and again at
  apply, the last-run bypass, `generate --against`, MCP `plan` `against`, the HTTP body's `against`,
  `PhysicalSnapshot.Facts` (kept empty until A15). Owns `GenerationService.cs`, `Generation/GenerationRun.cs`,
  `Model/PhysicalSnapshot.cs`, `snapshot.json` and copy, `Commands/GenerateCommand.cs`, `Mcp/ModelTools.cs`,
  `CliApp.cs` usage, `docs/mcp.md`, `docs/api/openapi.yaml` (`GenerationRequest.against`), the Functions host's
  `GenerationService` setup, the generated editor types. Tests: the `SchemaSnapshotTests` and `LastRunTests` cases
  above, `GenerateTests.Generate_against_an_unknown_value_exits_4`. Verify:
  `E "~SchemaSnapshotTests|~SchemaConsistencyTests|~LastRunTests"`; `C "~GenerateTests|~McpSurfaceTests"`;
  `F "~ContractTests|~GenerationTests"`; `editor`.

### 3.2 Migration facts

**Decisions taken here**

1. One `migration` element per fact in `model/databases/<db>/migrations/open/` (6.2, its question 1). Recorded facts
   have no `name`, so the file is `<lowercase id>.json` (`ModelPaths.Stem`) and a listing reads in recording order
   (ULIDs sort by time). `ModelPaths.ConventionalFolder` places it; MQ1005 covers misplaced files. An empty name is
   valid for this kind (`BuiltinRules.CheckElementName`); a given name is checked as a label.
2. The discriminator is `fact`, with flat keys. Every fact type of 6.2 gets its format now (scope 11); phase 1 records
   `rename`, `rekey`, `change-type`, `drop` and `backfill`, and authors may write the others. In phase 1 sql-ddl
   writes a TODO comment for `adopt`, `baseline`, `value-map` and `state-map`: their steps come with 6.1, 6.7 and 4.5.
3. A fact names objects by physical key (the table and member keys `SchemaDiffer` matches on, built from ids) and by the
   physical names at the time. These keys are historical: plain strings with no `[ElementRef]` and no `x-ref`, so
   MQ2001, `get_references`, delete refusal and cascades never see them. A later `rekey` fact moves them forward for
   every reader (`Migrations/FactKeys.cs`). `database` (owning) is walked. `after` is a plain list of fact ids and is
   not walked: a fact that another copy folded away may leave it dangling, which is a warning (MQ4111), never MQ2001.
   Keys and `after` take ids only: their schema refuses a value that starts with `@` or `$`, so a 5.1 name there is
   MQ1002.
4. Intent fields are optional in the schema, so a pending fact loads; MQ4101 names what is missing at its pointer.
5. The classifier compares resolved physical captures, never documents: a column's type may come from an attribute,
   a scalar type, a mapping, conventions or type maps. It captures only the databases a change can reshape, reuses
   the capture from before the change, and skips annotation-only edits. It never matches objects by name: a moved key
   is known from the write's own key map (materialize) or from an open `rekey` fact, never guessed.
6. Renames of tables, views, sequences, columns and database schemas are facts; index and constraint renames are not
   (no data; matched by key). A schema rename is one fact, not one per table. New unique, check or foreign keys record
   nothing: the scope's intent list is type change, narrowing, drop and tightened nullability, and `db check` (6.5,
   phase 3) catches the rest.
7. A held fact is complete for validate until released (14.1); nothing releases it automatically.
8. Fold keeps the id and file of a joined chain's last fact: two copies that fold the same facts write the same file,
   and a database that applied that fact already stands at the joined end state.
9. Pending facts are errors and stop generation like any error; a project may lower MQ4101 in `validation.rules`.
   They do not block the shared resolution: template previews, path listings, `get_resolved_model` and resolver
   findings keep working while a fact waits for its intent.
10. sql-ddl names migration files by content. The writer keeps a `once` file owned while it exists, even when no run
    produces it (amending D29), so earlier files, the counter-named ones included, stay owned without placeholders.
    No setting keeps the old names.
11. Sealing (revision labels, object hash manifests) and squash are deferred: not in the phase-1 brief. Sealing is
    optional (13.1); squash needs the ledger tables of declared environments (6.1, 6.9). `baseline` is squash's
    output format, and `open/` leaves room for sealed folders beside it.
12. PostgreSQL first: only PostgreSQL has a widening table, so elsewhere every type change is a conversion; sql-ddl
    renders fact steps for PostgreSQL and keeps today's TODO comments elsewhere, which MQ4056 reports.
13. Stale facts and fold are judged against a baseline (scope 6.2, 15.4): the `--against` model, else the facts the
    database's committed snapshot lists as rendered. Facts in the baseline are never stale and never folded. A
    database with neither has only its pending facts judged, since a pending fact never reaches a migration.
14. A fact's own state is `state` (`pending`, `complete`, `held`, `released`) in every record, answer, CLI line and
    template. `status` is 4.5's element status, which a migration carries like every kind.
15. The fact content hash covers what the fact means, not its annotations or its place in the order (Engine
    behaviour): editing a fact's description, tags or element status, holding or releasing it, or rewiring its `after`
    never changes the hash a ledger row records.
16. The empty-name case of `BuiltinRules.CheckElementName` lands with the kind (A5), so the `clean` fixture's nameless
    fact validates from the first task.
17. `list_migrations` pages as `ModelPages` does: default 100, at most 1000.
18. Errors in the block MQ2009 to MQ2023 (section 4's typed links, stereotype rules, layering and status) never keep a
    fact from being recorded: they judge policy, and the owner still resolves.
19. `x-sort` takes string keys (A5). Code note: the canonical writer sorted an `x-sort` array only by an integer item
    key, so `after` and `values` would have kept their written order. It now sorts a string key ordinally after the
    integer ones, and `"x-sort": "."` sorts an array of strings by the items themselves: `after` uses `.` and `values`
    uses `from`, so two copies of one fact write the same bytes and hash alike (decision 15). `engine-design.md`
    (canonical form) says so; `CanonicalCheck` leaves such arrays to the node-based writer.
20. MQ4101 is one finding per missing field, at that field's pointer (A7): completing one field of a narrowing leaves
    the other's finding as it was, so a save that completes part of a fact adds no error and is not refused. A
    `backfill` that has neither names `value` (its message offers a data check instead). Until A12, the validator
    resolves nothing, so every pending fact is MQ4101; the stale judgment of pending facts (MQ4103) comes with A12.
21. Applied order breaks a cycle of `after` links (MQ4104) by placing the unplaced fact with the smallest id, and
    ignores links to facts of another database (MQ4105) as it ignores missing ones (A8): the order is total, so the
    resolved lists, `migration list` and the classifier never stall on an invalid model.
22. Projection keys (A8): a fact depends on its own file, its database and the `rekey` facts after it (its keys today
    come from them). Both migration lists carry `k:migration` and every fact's `e:` key, since one fact's `after`
    moves the others in the order. A projected table adds the `e:` keys of the facts on it and on its columns to the
    list it shares with its columns; a projected view or sequence adds its own, so a template that prints
    `former_names` re-renders when the fact changes. `Since` counts the facts that name the object as `object` or in
    `objects`, not the `rekey` entries that moved its key.
23. `get_resolved_model`'s `all` scope holds the `migrations` records too: `all` is every scope that `databases` does
    not already hold. The database filter narrows `migrations` to that database's facts (A8).
24. The classifier compares the two captures by key, as `SchemaDiffer` matches them (same key and a new name; a key
    that is gone), instead of running `SchemaDiffer.Compare`: it needs both sides' full column states, and the
    differ's constraint, index and dependency-order work would be discarded (A9). The matching is the differ's.
25. The write's one `rekey` fact is the first fact it records, and its `after` is the heads of every table and
    sequence whose keys it moved: the other facts of the write name the moved keys, so they apply after it (A9).
    Schema renames come next, by schema key; then the order (object key, member key; rename, change-type, backfill,
    drop), where a view or sequence is its own object key.
26. A reference column's storage is part of a `change-type`'s sides (A9 review). `migrationState` gains `strategy` and
    `referenceTable` (the bound table's key: a historical `factKey`, which later `rekey` facts move like an object's
    key), so a fact for a strategy or binding change whose type stays the same has a `from` and a `to` that differ.
    MQ4106's same-sides check, the stale judgment and the internal `Widening.Classify` over two sides compare them with
    the type facets; such a change is a conversion, or a narrowing when its type also narrows. Code note: with type
    facets alone, the fact had equal sides, which MQ4106 refused and the stale judgment would call stale on recording.
    Readers compare `referenceTable` under today's keys: the classifier moves the before capture's through the write's
    key maps, and a pending fact's through the `rekey` facts after it.
27. Choosing databases (A10): an element's footprint after the change is not known before the resolve it would choose,
    so the after side of an entity, value object, scalar type, enum, relation or reference type is read from the change
    itself, against the before footprints (or reaches): an entity's new package (the conventions that take it) and new
    base, a relation's ends, and the databases of the physical elements the element references (its bindings' tables).
    `Footprint_selection_covers_every_database_a_change_reshapes` pins the superset. The chosen databases share one
    conceptual run (`ModelResolver.ResolveDatabasesAsync`; `ResolveDatabaseAsync` gains `freeze`).
28. The skip list (A10) applies at an element's top level and, for `description`, `displayName`, `tags` and `category`,
    inside each sub-element (an object with an `id`); `generation` and `source` count only at the top level, since a
    binding's `source` names its table.
29. MQ4110 (A10) is one finding per element whose left-out objects differ between the two captures (a change no fact
    records), on the change that wrote it, else on the first change that chose the database; a chosen database that has
    errors itself, or does not resolve, records nothing and carries one MQ4110 on the database. A recorded fact's MQ4101
    goes on the first change that chose its database. Both follow `validation.rules`.
30. The capture cache (A10, A11) belongs to one snapshot. A write that reshapes nothing carries every entry; a write
    that classifies hands the new snapshot the chosen databases' after entries and the others' entries as they were; a
    settings save that records nothing, every default-locale change and `record: off` leave the cache behind (a
    settings section outside the four may still shape names; a default-locale change reloads the whole model, since
    the new default changes which locale shards load, and that reload may index other disk edits). A dry run keeps
    only the before entries it computed. A model with one database follows decision 6 of the recording list as
    written: a dry run that reaches it reaches every database, so it lists no facts and its `Facts` is null.
31. `facts` (A11) is an optional property of the HTTP contract's `SaveResult`, `BatchResult` and `SettingsSaveResult`:
    the host always writes it (a list, or null), and the editor's hand-built mock answers stay valid without it.
32. A pending fact that follows its column rewrites an existing fact file (A10, A11, review). Every recording write
    (element writes, settings saves, default-locale changes) checks those files against the disk before it writes, as
    the plan's own files are checked: a file changed behind the index is indexed (a `Disk` change set) and the write
    classifies again from it, so a hand edit (a fact held or completed in a text editor) is kept and the column records
    a new fact after it. A file still changing after that makes the write a conflict that carries no fact findings.
33. `validate --against` and stale facts (A12). The capture helper A3 reuses is `BaselineCheck.CapturesAsync(baseline,
    working, databaseIds, ct)` (internal): the baseline's captures under today's keys, without the objects of owners
    with errors in the baseline, over `ModelStore.CaptureDatabasesAsync` (the shared whole-model resolve, else
    per-database resolves naming the failed owners). Owners with errors on either side are left out of both captures,
    so their objects read neither as dropped nor as added. MQ4109 is one finding per database, on the working database
    file, naming the baseline owners left out (or saying the database did not resolve). MQ4103 is one finding per
    stale fact, on its file at `/fact`. A chain is judged per object and aspect (name, type, nullability, existence)
    from its first candidate's `from`; candidate `rekey` facts are stale together when their composed moves bring
    every key back. `ModelValidator` takes an optional resolver (`EngineServices` passes its own) to judge pending
    facts. Until A3 adds `PhysicalSnapshot.Facts`, plain validate reads no rendered facts, so every open fact of a
    database with a committed snapshot is a candidate. `against` with `elementIds` (MCP, HTTP) is `bad-request`.
34. A12 review. Candidate `rekey` facts compose from the first candidate: each key is followed from its value before
    the chain through every candidate after it, and all are stale only when every key ends where it began (a
    materialize made and undone). MQ4109 also names the baseline files that did not load (a load error, and no element
    read from them): those in the database's folder and those outside every database folder, in the same one finding
    per database; a working database whose baseline `database.json` did not load gets MQ4109 too. A derived table's
    rename lands on its overlay's `/name` only when the overlay names the table, else on its entity or relation. Plain
    validate's stale chains read the committed snapshots on each call (generation rewrites them with no model change);
    the captures they and `CaptureDatabasesAsync` make from the shared resolve join the snapshot's capture cache.
    Tests: `A_materialize_made_and_undone_since_the_baseline_leaves_both_rekeys_stale`,
    `A_baseline_file_that_does_not_parse_is_skipped_with_MQ4109`,
    `A_materialize_since_the_baseline_is_explained_by_its_rekey_and_reads_as_a_drop_without_it` and
    `An_entity_rename_reports_its_table_rename_on_the_entity_when_the_overlay_has_no_name`.
35. Part 1 review. Outside PostgreSQL a `rekey` fact's MQ4056 names only the missing ledger row: a rekey writes no DDL
    on any dialect, so there is no TODO comment to keep. MQ4106 also refuses a `value-map` or `state-map` code listed
    twice in `values` (at the entry) and a code mapped to itself (at its `to`). `Widening.Classify` over two sides
    renders, on PostgreSQL, the side without a native type through the embedded type map when the other side has one (a
    missing length, scale or decimal precision is a conversion, a missing time precision reads as 6), so `text` to a
    string of length 20 narrows and MQ4101 asks for a data check. A save that deletes a fact also validates the facts
    whose `after` named it (their MQ4111); a moved fact's peers come from its document as before. The classifier finds a
    table's heads once per write by one walk of the `after` links from all the table's facts, and a fact chained into
    one table becomes its only head. Plain validate keeps its stale findings per snapshot (held weakly, so no old
    snapshot stays alive for them) while every committed snapshot gives the judgment the same input (present or not, and
    the facts it lists as rendered, the only part decision 13 reads), and judges again when one changes.
    `RecordingResolves` stays the bounded log of the last 64 resolves with a counter (A10). Table rows in
    `docs/user-guide.md`, `docs/mcp.md` and the engine READMEs stay one line each, as Markdown tables need; the prose
    around them is wrapped at 120 columns. Tests: `Outside_PostgreSQL_a_rekey_reports_MQ4056_for_its_ledger_row_only`,
    `A_value_map_code_listed_twice_or_mapped_to_itself_is_MQ4106`,
    `A_change_type_from_a_native_type_to_a_keyword_is_classified_through_the_type_map`,
    `Deleting_a_fact_that_another_follows_reports_the_dangling_link_on_the_delete` and
    `A_hand_edit_made_before_a_default_locale_change_is_never_recorded`.
36. A13. `ListMigrationsAsync` and `FoldRequest.Database` take a database name or id (id first). The `stale` mark is the
    whole-model validation's MQ4103 on the fact (pending facts judged alone, complete ones against the committed
    snapshot's rendered facts), so a project that turns MQ4103 off sees no mark. A cursor whose fact is gone (folded or
    removed) is an `ArgumentException` like an unknown one, as is a page size outside 1 to 1000. `migration list` reads
    every page and prints one line per fact: `<database> <order> <id> <fact> <state>[ (needs <missing>)][ stale]:
    <summary>`. The store side of list and fold, with their records, is `ModelStore.MigrationTools.cs`.
37. A14. Fold answers `Invalid` with the errors, writing nothing, when the model has errors outside MQ41xx: the stale
    chains judge facts against captures those errors leave unsure; the migration rules' own errors (pending facts,
    MQ4107) are what fold is for. Two renames join only when the first's `to` name is the second's `from` name; a
    `backfill`, like `raw`, `adopt`, `baseline` and the maps, never joins; a `raw` fact with no objects and a `baseline`
    change any table, so no join crosses them; `rekey` facts chain only with no other open fact between them. Two
    `change-type` facts where one carries a conversion, the other is pending and neither widens stay in order with no
    refusal. A link to a removed stale fact is replaced by the links of the fact it named, so the order it gave stays.
    `FoldResult.Files` is filled for a dry run only. `migrationObject.formerNames` also holds the names a joined drop
    folded (its schema description says so; formerly "baseline only").
38. A4. "A pack that still exists" is a pack of the run (`WriteContext.UnitCountByPack`): with a pack filter only the
    run's packs are orphaned, as before, and a removed pack's manifest is orphaned in full, its owned files
    `OrphanedOwned`. A kept orphan's change carries its entry's hash as old and new hash, as a plan lists a skipped
    unit's owned output. The text summary of `generate` counts a kept file as produced only when the run rendered it
    (the writer reported its path to the progress), so a kept orphan, which was never compared, leaves the unchanged
    count alone.
39. A15. `FromState` hashes the baseline as stored, before the alias and `rekey` steps (the committed snapshot as
    loaded, or the baseline model's capture under its own keys, `BaselineCaptures.Stored`), so it equals the `ToState`
    of the run that produced that baseline even when a materialize moved keys in between; it is `""` with no baseline (a
    diff from nothing follows no file). Both states are init properties computed on first read
    (`SchemaDiffResult.WithStates`): the canonical form of a 10,000-table capture costs about 0.7 s, and an empty diff
    renders nothing. A database a run against a baseline does not diff (MQ4109) lists no facts, so its diff stays empty.
    `SnapshotRekey.Apply` also moves a reference column's `referenceTable` (decision 26), so aliases and facts read
    bound tables under today's keys too. Code note: a `rekey` fact alone now makes the next diff non-empty, so the
    `MaterializeTests` that expected no migration after a materialize expect one migration that changes no table, and
    the stale-fact test of A12 makes its stale change after the first apply, which renders the earlier facts into the
    committed snapshot's `facts`.
40. A16. Code note: a template could not tell a run against a baseline model from a committed snapshot at revision 0
    (both diff from revision 0), so the file name rule had no input; `SchemaDiffResult` gains the init property `bool
    FromBaseline` (true for every diff of a `generate --against` run), which `migration.scriban` reads. The key hashes
    (Scriban's `string.sha256`) the lines `mq-migration-1`, `FromState`, the diff hash, then `<id> <hash>` per listed
    fact, each ended by a line feed. The header is three lines after the title: `-- From state: <state, or none>`, `--
    To state: <state>` and `-- Facts: <ids, comma-separated, or none>`. On PostgreSQL the listed facts the diff holds no
    change for write their own steps first, before the schema statements, with `rekey` rows, the TODO lines and the
    `raw` steps of phase `before`, in applied order; the `raw` steps of phase `after` (and, in a migration from nothing,
    the rows of the facts a build reaches) close the file. A raw step runs its statements through `EXECUTE` of a
    dollar-quoted string, so any statement may stand there. A guarded type change (the `idempotent` parameter, or an own
    step) compares the column's type in the catalog with a temporary probe column of the new type. `adopt`, `baseline`,
    `value-map` and `state-map` write a TODO line and no ledger row, since the migration does not reach their end state;
    a fact whose texts lack the dialect's entry (MQ4108, its data check's query included) gets the diff's statements and
    TODO lines, and no row. A held fact's note replaces its change where the diff holds it; a held table, view or column
    rename leaves the later statements of that migration (indexes, constraints, views), which name the object by its new
    name, to the reviewer, while a held sequence rename keeps the sequence's old schema and name in its own later
    statements. A held drop or rename that is not released writes its note on every dialect, never the diff's `DROP
    COLUMN`, `DROP TABLE` or rename (`held_waits`; a schema rename outside PostgreSQL, which moves each object, and a
    sequence where the dialect has none are left out); a SQLite table whose rebuild would drop or rename a held column
    gets the notes and a TODO to rebuild it by hand. Outside PostgreSQL no fact writes a step, so such a fact released
    after an earlier migration wrote its note gets a TODO line (`held_release_todos`). A plan lists no change for a once
    file nothing renders (the writer reports it `Kept` on apply and check), so
    `LifecycleTests.A_plan_lists_a_migration_written_once_as_kept_with_no_diff` became
    `A_plan_after_a_migration_renders_nothing_for_it_and_the_apply_keeps_it_owned`. Code note: PostgreSQL's `ALTER
    COLUMN ... TYPE` keeps the nullability, and a column whose type and nullability both changed got no `SET NOT NULL`;
    the migration now writes it after the type change (no golden changes), which a `backfill` on a retyped column needs.
    The `ledger-facts` golden gains the migration from nothing.

**Model and schema**

`schemas/v1/migration.json` (new; copy under `samples/reference-app/.maquettiste/.schema/v1/`). Kind `migration`,
folder `model/databases/{db}/migrations/open`, CLR type `Migration`. Top level, in `x-order`:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `$schema`, `kind`, `id`, `name`, `displayName`, `pluralName` | as every kind | | `name` defaults to `""`: a recorded fact has none |
| `database` | id | required | `[ElementRef(Database, Owning = true)]`: deleting the database deletes its facts |
| `fact` | enum `rename`, `rekey`, `change-type`, `drop`, `adopt`, `baseline`, `backfill`, `raw`, `value-map`, `state-map` | required | |
| `after` | array of ids, `x-sort` `.` | `[]` | Fact ids; set by the write path; an ordering hint, not walked (decision 3) |
| `description`, `stereotypes`, `tags`, `category` | as every kind | | |
| `object` | migrationObject | none | `rename`, `change-type`, `drop`, `backfill` |
| `objects` | array of migrationObject | `[]` | `adopt`, `baseline`, `value-map`, `state-map`; context for `raw` |
| `from`, `to` | migrationState | none | `rename`: name and schema; `change-type`: type facets, reference storage |
| `via` | array of names | `[]` | Intermediate names of a folded rename, oldest first |
| `keys` | array of `{ kind, table?, from, to, name? }` | `[]` | `rekey`: every key the change moved; a column entry's `table` is its table's key before the fact |
| `conversion` | `{ cast: true }` or `{ expression: dialectMap }` | none | `{table}`, `{column}` expand to quoted names |
| `dataCheck` | `{ query: dialectMap }` or `{ accept: string }` | none | Rows the step must not find, or why no check is needed |
| `acknowledge` | string | none | `drop`: why the data may go |
| `value`, `where` | dialectMap | `{}` | `backfill`: the value, and the rows (default `{column} IS NULL`) |
| `sql`, `skipWhen` | dialectMap | `{}` | `raw`: statements, and the guard whose row means the end state holds |
| `phase` | enum `before`, `after` | `after` | `raw`: before or after the schema statements |
| `environment` | tagLabel | none | `adopt`: a free name until 6.9 |
| `referenceType`, `process` | string | none | Historical ids of `value-map`, `state-map`; not walked |
| `values` | array of `{ from, to }`, `x-sort` by `from` | `[]` | Stored codes (or state codes) and their replacements |
| `held`, `released` | boolean | `false` | A held fact waits; migrators skip it until `released` |
| `properties`, `generation`, `source` | as every kind | | |

`migrationObject` (`x-order`): `kind` (`table`, `column`, `view`, `sequence`, `index`, `unique`, `foreign-key`,
`check`, `routine`, `database-type`, `sql-object`, `schema`), `table` (table key, for members), `key`, `schema`,
`tableName`, `name`, `formerNames` (`baseline`, and a drop that fold joined with renames, decision 37). It has no
`id`, so the indexer registers no sub-element.
The object's `key` and `table`, the `table`, `from` and `to` of `keys` items, and a state's `referenceTable` use
`$defs/factKey`: a string that does not start with `@` or `$`. `after` items use the common id pattern.
`migrationState` (`x-order`): `name`, `schema`, `type`, `length`, `precision`, `scale`, `nativeType`, `strategy`,
`referenceTable`, `nullable` (`strategy` and `referenceTable`: a reference column's storage, decision 26).
`allOf` with `if`/`then` per `fact`:

| `fact` | Schema requires | Intent (MQ4101 when missing, not held) | End state (applying it again does nothing) |
| --- | --- | --- | --- |
| `rename` | `object`, `from.name`, `to.name` | none | the object (by key) is named `to` |
| `rekey` | `keys` | none | the ledger's object rows carry the new keys; no DDL |
| `change-type` | `object` (column), `from`, `to` | `conversion`; `dataCheck` when the step narrows | the column has type `to` |
| `drop` | `object` | `acknowledge` | the object is absent |
| `backfill` | `object` (column) | `value` or `dataCheck` | no row matches `where` |
| `raw` | `sql` | `skipWhen` | `skipWhen` returns a row |
| `adopt` | `objects`, `environment` | none | the ledger holds the objects' rows |
| `baseline` | nothing | none | the database is at the model's shape as of this fact |
| `value-map` | `objects` (columns), `values` | none | no stored value equals a `from` |
| `state-map` | `objects`, `values`, `process` | none | no stored state equals a `from` |

A pending fact recorded on billing when the invoice number column's `nativeType` goes from `text` to `varchar(20)`
(file `model/databases/main/migrations/open/01jbf3r5q7w0x2y4z6a8b0c2d4.json`):

```json
{ "$schema": "../../../../../.schema/v1/migration.json", "kind": "migration", "id": "01JBF3R5Q7W0X2Y4Z6A8B0C2D4",
  "database": "01J92P0V1QRN2181XM2ZWE02W4", "fact": "change-type",
  "object": { "kind": "column", "table": "01J92P0V0FJ23CGSNKM7P1W5V7@01J92P0V1QRN2181XM2ZWE02W4",
    "key": "01J92P0V0R3VSP7D5238DTNZX1", "schema": "billing", "tableName": "invoices", "name": "number" },
  "from": { "type": "string", "length": 32, "nativeType": "text" },
  "to": { "type": "string", "length": 32, "nativeType": "varchar(20)" } }
```

An agent completes it with `"conversion": { "cast": true }` and a `dataCheck` query such as
`select 1 from {table} where length({column}) > 20` (the file itself is written in canonical form, a key per line).

`schemas/v1/maquettiste.json`: a `migrations` block after `assistant` in the root `x-order` (3.3 adds ledger keys):

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `migrations.record` | enum `automatic`, `off` | `automatic` | `off`: no write records facts; `validate --against` still checks |

**Public API** (compressed C#: in a record body each `Type Name[ = default];` is a public `{ get; init; }` property;
resolved members are `{ get; }` with an internal setter, as every R type has)

```csharp
namespace Maquettiste.Engine.Model;
public enum ElementKind { /* ... */ Scenario, Migration }          // appended: KindInfo.Get indexes by value
public enum MigrationFact { Rename, Rekey, ChangeType, Drop, Adopt, Baseline, Backfill, Raw, ValueMap, StateMap }
public enum MigrationPhase { Before, After }
public enum MigrationRecording { Automatic, Off }
public sealed record Migration : Element {
    [ElementRef(ElementKind.Database, Owning = true)] required string Database; required MigrationFact Fact;
    IReadOnlyList<string> After = [];                               // fact ids; no [ElementRef] (decision 3)
    MigrationObject? Object; IReadOnlyList<MigrationObject> Objects = []; MigrationState? From; MigrationState? To;
    IReadOnlyList<string> Via = []; IReadOnlyList<MigrationRekey> Keys = [];
    MigrationConversion? Conversion; MigrationDataCheck? DataCheck; string? Acknowledge;
    IReadOnlyDictionary<string, string> Value, Where, Sql, SkipWhen = empty;   // dialect maps
    MigrationPhase Phase = After; string? Environment; string? ReferenceType; string? Process;  // last two: not walked
    IReadOnlyList<MigrationValue> Values = []; bool Held; bool Released; }
public sealed record MigrationObject { required string Kind; string? Table; required string Key; string? Schema;
    string? TableName; string? Name; IReadOnlyList<string> FormerNames = []; }
public sealed record MigrationState { string? Name; string? Schema; string? Type; int? Length; int? Precision;
    int? Scale; string? NativeType; string? Strategy; string? ReferenceTable; bool? Nullable; }   // decision 26
public sealed record MigrationRekey { required string Kind; string? Table; required string From; required string To;
    string? Name; }
public sealed record MigrationConversion { bool Cast; IReadOnlyDictionary<string, string> Expression = empty; }
public sealed record MigrationDataCheck { IReadOnlyDictionary<string, string> Query = empty; string? Accept; }
public sealed record MigrationValue { required string From; required string To; }
public sealed record MigrationSettings { MigrationRecording Record = Automatic; }
// ProjectSettings gains: MigrationSettings Migrations = new();

namespace Maquettiste.Engine.Migrations;
public enum TypeChange { Same, Widening, Narrowing, Conversion }
public enum WideningCondition { Always, LongerLength, DigitsKept, MorePrecision }
public sealed record WideningRule(string From, string To, WideningCondition When);
public static class Widening {
    public static IReadOnlyList<WideningRule> Rules(Dialect dialect);      // empty outside PostgreSQL
    public static TypeChange Classify(Dialect dialect, string fromNativeType, string toNativeType); }

namespace Maquettiste.Engine.Resolution;
public sealed class RMigration : RAnnotated {      // Kind "migration"; Id the fact id; Dependencies: e:<id>
    string Name;                                   // "" for a recorded fact
    RDatabase Database;
    string Fact;                                   // the schema value: rename, rekey, change-type, ...
    int Order;                                     // 1-based applied order within the database
    IReadOnlyList<RMigration> After;               // the facts it follows that exist; dangling ids left out (MQ4111)
    string State;                                  // pending | complete | held | released (decision 14)
    IReadOnlyList<string> Missing;                 // the intent fields MQ4101 names; empty unless pending
    RMigrationObject? Object; IReadOnlyList<RMigrationObject> Objects;
    RMigrationState? From; RMigrationState? To; IReadOnlyList<string> Via; IReadOnlyList<RMigrationRekey> Keys;
    RConversion? Conversion; RMigrationDataCheck? DataCheck; string? Acknowledge;
    string? Value; string? Where; string? Sql; string? SkipWhen;   // the dialect's entry, else "*", else null
    string Phase;                                  // before | after
    string? Environment; string? ReferenceType; string? Process;
    IReadOnlyList<RMigrationValue> Values; bool Held; bool Released;
    string Hash; }                                 // the fact content hash (Engine behaviour; ledger rows, 3.3)
public sealed class RMigrationObject { string Kind; string? Table; string Key; string? Schema; string? TableName;
    string? Name; IReadOnlyList<string> FormerNames;
    string CurrentKey; string? CurrentTable; }     // Key and Table moved through later rekey facts (FactKeys)
public sealed class RMigrationState { string? Name; string? Schema; string? Type; int? Length; int? Precision;
    int? Scale; string? NativeType; string? Strategy; string? ReferenceTable; bool? Nullable; }   // as recorded
public sealed class RMigrationRekey { string Kind; string? Table; string From; string To; string? Name; }
public sealed class RConversion { string FactId; bool Cast; string? Expression; }  // dialect's entry, else "*"
public sealed class RMigrationDataCheck { string? Query; string? Accept; }          // dialect's entry, else "*"
public sealed class RMigrationValue { string From; string To; }
// Added: RDatabase.Migrations : RList<RMigration> (applied order; membership key k:migration) and
// ResolvedModel.Migrations : RList<RMigration> (database name, then Order); on RTable, RView and RSequence:
// IReadOnlyList<string> FormerNames, string? Since; on RColumn: FormerNames, RConversion? Conversion, string? Since.
// SchemaDiffResult gains init properties: IReadOnlyList<RMigration> Facts = [] (the facts that render, applied
// order), string FromState = "", string ToState = "" (12 hex digits of SHA-256 over the canonical capture of each
// side without revision, aliases and facts), bool FromBaseline (a generate --against run's diff, decision 40).

namespace Maquettiste.Engine;   // Editor/ResolvedRecords.cs
// get_resolved_model scope "migrations" (ResolvedRecords.Scopes), sorted by database name, then Order:
public sealed record MigrationRecord(string Id, string Kind /* migration */, string Name, string Database, int Order,
    string Fact, string State, IReadOnlyList<string> Missing, IReadOnlyList<string> After, MigrationObject? Object,
    string? CurrentKey, MigrationState? From, MigrationState? To, string Hash);

namespace Maquettiste.Engine;
public sealed partial class ModelStore {
    public Task<ValidationReport> ValidateAgainstAsync(ModelBaseline baseline, CancellationToken ct);
    public Task<MigrationList> ListMigrationsAsync(string? database, string? cursor, int limit, CancellationToken ct);
        // ArgumentException for an unknown database or cursor
    public Task<FoldResult> FoldMigrationsAsync(FoldRequest request, CancellationToken ct); }
public sealed record RecordedFact(string Id, string Database, string Fact, string State /* complete | pending */,
    IReadOnlyList<string> Missing, string Summary, string Path);     // a fact the write created or updated
public sealed record MigrationEntry(string Id, string Database, string DatabaseName, int Order, string Fact,
    string State /* pending | complete | held | released */, bool Stale, IReadOnlyList<string> Missing,
    string Summary, MigrationObject? Object, IReadOnlyList<string> After, string Path, string Hash);
public sealed record MigrationList(IReadOnlyList<MigrationEntry> Facts, int Total, string? Next);
public sealed record FoldRequest(string? Database, ModelBaseline? Against, bool DryRun, ChangeSource Source);
public sealed record FoldJoin(string Kept, IReadOnlyList<string> Removed, string Fact);
public sealed record FoldRefusal(IReadOnlyList<string> Facts, string Reason);
public sealed record FoldResult(SaveOutcome Outcome, IReadOnlyList<FoldJoin> Joined, IReadOnlyList<string> RemovedStale,
    IReadOnlyList<FoldRefusal> Refused, IReadOnlyList<BatchPreviewFile> Files, IReadOnlyList<Diagnostic> Diagnostics);
// SaveResult, SettingsSaveResult and DefaultLocaleResult gain an init property (positional parameters do not change):
//     public IReadOnlyList<RecordedFact>? Facts { get; init; }
// BatchResult and BatchPreview gain the same property; their whole records after milestones A and C are stated once,
// in 5.1. A single-element call (create, save, delete) carries every fact its write recorded, cascades included. A
// batch carries them on BatchResult or BatchPreview; its Items carry none. On a BatchPreview, null means the change
// reaches every database and facts are computed on save.
```

**Engine behaviour**

*Widening table* (`Migrations/Widening.cs`: code data read by the classifier, validation, fold and later 6.1). A
widening is a change after which every stored value converts exactly and reads back as an equal value of the same
kind. Native types are parsed first (case and spaces folded, PostgreSQL aliases such as `int4`, `character varying`
and `timestamp with time zone` mapped, `numeric(p)` read as `numeric(p, 0)`, a missing time precision read as 6, array
suffixes counted). The rows, exactly as scope 6.2 lists them, are also the table in `docs/guides/schema-changes.md`:

| From | To | Widening when |
| --- | --- | --- |
| `varchar(n)` | `varchar(m)`; `varchar` with no length; `text` | `m > n`; always; always |
| `char(n)` | `char(m)` | `m > n` |
| `smallint` | `integer`, `bigint` | always |
| `integer` | `bigint` | always |
| `numeric(p, s)` | `numeric(p', s')`; `numeric` with no precision | `s' >= s` and `p' - s' >= p - s`; always |
| `timestamp(p)`, `timestamptz(p)`, `time(p)`, `timetz(p)`, `interval(p)` | the same type with `p'` | `p' > p` |
| an array of any of these | the array of its widening, same dimensions | as its element type |
| a not-null column | the column made nullable | always |

Not widenings: `real` to `double precision`, `timestamp` to or from `timestamptz`, `char` to `varchar`, an integer type
to `numeric`, and every pair not listed. `Classify` returns `Narrowing` when the reverse change is a widening, else
`Conversion`. Unknown names (database types, ranges, domains) are `Same` only when the parsed names are equal.

*Fact content hash* (`Migrations/FactHash.cs`, internal; `RMigration.Hash`, `MigrationEntry.Hash`, the ledger's
`content_hash`). The 64 lowercase hex digits of SHA-256 over the UTF-8 bytes of the fact document as `ICanonicalJson`
writes it, after these top-level keys are removed: `$schema`, `name`, `displayName`, `pluralName`, `description`,
`stereotypes`, `tags`, `category`, `rationale`, `decision`, `status`, `properties`, `generation`, `source`, `after`,
`held` and `released`. The hash therefore covers `kind`, `id`, `database`, `fact` and the fact's own fields (objects,
states, names, keys, conversion, checks, SQL, values), and two copies of one fact hash alike whatever their annotations.
Fold writes a joined fact under the kept id, whose hash changes with its meaning.

*Classifier* (`Migrations/FactClassifier.cs`, internal): two captures of one database, the open facts of the model
after the change, the dialect and the write's key maps in; facts out. Key maps are the `TableRekey` list and
sequence map of a materialize in the same write (`MaterializeResult.Rekeys`, `Sequences`), the keys of `rekey` facts
the write itself creates, and the reverse of open `rekey` facts whose new keys the write removes (an undo of a
materialize). The before capture is moved through them with `SnapshotRekey.Apply`; then the two captures are compared
by key, as `SchemaDiffer` matches them (decision 24), and map:

| Change between the two captures | Fact | State |
| --- | --- | --- |
| Any added object | none | |
| Keys moved by the write's key maps | `rekey`, one per write and database, every key moved | complete |
| Database schema: same key, new name | `rename` of kind `schema` | complete |
| Table, view or sequence: same key, new name or a schema other than its schema's rename | `rename` | complete |
| Column: same key, new name | `rename` | complete |
| Column type (the effective `nativeType`, else `type` and facets) the same or widened | none | |
| Column type narrowed | `change-type` | pending: `conversion`, `dataCheck` |
| Other type change, or a reference column's `strategy` or `referenceTable` | `change-type` | pending: `conversion` |
| Column made nullable | none | |
| Column made not null | `backfill` | pending: `value` or `dataCheck` |
| Table, column or sequence dropped (a dropped table's columns get no fact) | `drop` | pending: `acknowledge` |
| Indexes, constraints, views, routines, types, SQL objects, defaults, comments, identity, collation, storage | none | |

A key that disappears with no key map behind it is a drop, even when an object with the same name and type appears:
deleting a column and adding another with its name records a `drop` and nothing for the add. Tables, views and
sequences whose only change is following their schema's rename record nothing. A change that a fact created or
updated in the same write already explains is not recorded again. `after` is set to the heads of the database's open
facts after the change on the same table: the facts of that table and of its members that no other such fact follows.
Facts of one write chain in the order: the write's `rekey` first, then schema renames, then (table key, member key;
rename, change-type, backfill, drop) (decision 25). Ids come from `EngineOptions.EffectiveIdGenerator` in that order
(byte-exact with `SequentialIdGenerator`); bytes are canonical (`ICanonicalJson`), placed by
`ModelPaths.ConventionalFolder` and `FileName` as the planner places a create.

*Pending facts follow their column.* When a write changes the type of a column whose last open fact is a pending
`change-type` (not held, no conversion, no data check), the classifier rewrites that fact's `to` instead of recording
another fact, and its missing intent is computed again (a second narrowing, or a widening after the narrowing, keeps
one pending fact from the original type). When the new type is the fact's `from`, or a widening of it, the fact is
left as it is: it is stale (MQ4103), and fold removes it. A pending `backfill` or `drop` whose change is undone is
left as it is the same way.

*Recording, captures and cost.* Recording runs in `ModelStore.ExecuteCoreAsync` (`ModelStore.cs:631`), after
`ValidateAsync` and before the preview branch, so a dry run (`PreviewBatchAsync`, the assistant's proposals) lists the
facts it would record. Fact files join `plan.Writes` in the same `Documents.ApplyAsync` call: the store's atomic write
path (`AtomicFileSet`, which checks `IOutputPathPolicy`). `ApplyWithSchemasAsync` passes the materialize results to
`ExecuteAsync`, so the classifier gets their key maps.

1. *Skip.* Nothing runs when `migrations.record` is `off` after the change, when only `migration` elements change, or
   when changes touch only `description`, `displayName`, `tags`, `category`, `generation` or `source` (these reach a
   capture only as comments, which never need a fact). B2 adds 4.5's `rationale`, `decision` and `status`, and B3 adds
   4.3's `package` on physical kinds, to this list.
2. *Choose databases*, a superset of those the change can reshape, by ids alone (nothing is resolved to choose):
   - a table, view, sequence, routine, database type, SQL object or mapping: its `database` before and after;
   - a database: itself, unless the write deletes it; then nothing is recorded for it, since its facts go with it;
   - an entity, value object, scalar type, enum, relation or reference type: the databases whose footprint holds it
     before or after. A database's footprint is the set of element ids in the `e:` dependency keys of its resolved
     objects, kept next to its capture in the cache. A database with no cache entry uses its reach instead
     (`Migrations/DatabaseReach.cs`, computed from the snapshot with no resolve): the entities its convention takes
     (`DatabaseScope.ByConvention`) and those its own elements reference, closed over what they reference (base
     entities, value objects, scalar types, enums, reference types, relations and the entities at their other ends)
     and over the entities that derive from a reached one. The reach is a superset of the footprint (tested);
   - a package: the databases whose convention entries name it or one of its ancestors, before or after;
   - a stereotype: the databases whose footprint or reach holds an element that carries it, or that hold a physical
     element that carries it; from B8 on, carrying reads the effective set (4.2), so an implied stereotype counts;
   - settings (`conventions`, `databases`, `typeMaps`, `inflection`): every database.
3. *After side:* `ModelResolver.ResolveDatabaseAsync(candidate, id, freeze: true)` (conceptual layer plus that
   database; `freeze` keeps dependency keys; chosen databases share one conceptual run), then `SnapshotCapture.Capture`.
   The after footprint comes from the same run.
4. *Before side:* the store's capture cache, one entry (capture and footprint) per database of the current snapshot.
   A write hands the new snapshot its after entries and the untouched ones; a disk reload clears the cache; a missing
   entry comes from a cached whole-model `ResolvedAsync` of that snapshot, else from one more per-database resolve.
5. *Errors:* objects owned by an element with an error in the save's validation, before or after, are left out of
   both captures (as `DatabaseTables.FailedOwners` does) and the save carries MQ4110. Errors in the block MQ2009 to
   MQ2023 do not exclude (decision 18).
6. *Dry runs.* A dry run classifies the databases its change chooses. When the change chooses every database (a
   settings change, a stereotype or type that every database uses), the preview computes no facts and its `Facts` is
   null: the save computes them. So no preview resolves the whole physical model.

Cost: nothing for `off`, annotation edits and kinds that shape no table (diagrams, processes, seeds, queries,
translations); otherwise one per-database resolve and capture per chosen database, under the write gate, plus one
more per chosen database whose before entry is missing. That happens after a load, a disk reload or a restore; the
reach keeps even that first save to the databases the change can reshape. Measured analogues
(docs/engineering/explorer-redesign.md 4.5): 30 to 60 ms per 700 tables, about 415 ms for a 10,179-table database
warm, the conceptual layer about 86 ms on the 26,616-file bench model. A settings change chooses every database. A
save never resolves seeds, processes or unchosen databases, and a dry run never resolves every database. Memory: one
capture and one id set per database of the current snapshot.

*Write surfaces.* Every path through `ExecuteAsync` records: create, save, delete and `ApplyBatchAsync` (so MCP
`save_element` and `apply_batch`, `model apply`, the editor HTTP API, every CLI write), schema, materialize, process and
tag ops, and the seed and process APIs. The write-surfaces map's bypasses, decided:

| Bypass | Decision |
| --- | --- |
| `ChangeDefaultLocaleAsync` writes element files | Classifies the moved ids: a moved `pluralName` can rename a table |
| `ReplaceDocumentsAsync` (snapshot restore: MCP, CLI, HTTP) | Records nothing: restore rewinds the model, facts included |
| `SaveSettingsAsync` | Classifies every database when `conventions`, `databases`, `typeMaps` or `inflection` change |
| A delete cascade that removes a deleted database's `databases` settings entry (`ChangePlanner.Cascade.cs:368-382`) | Chooses nothing more: the deleted database records nothing |
| `FormatAsync`, CLI `format`; shard writers; CLI `init` | Nothing: bytes, translations or a first file; no shape changes |
| Hand edits, merges, other processes (disk reloads) | Nothing: no write intent; `validate --against` catches them |
| Physical snapshot, branding, extension writes | Not elements; materialize keeps its aliases, and the classifier records the rekey from its key map |

*Validation* (`Validation/MigrationRules.cs`, from `ModelValidator` for `migration` elements): MQ4101, MQ4104 to
MQ4108, MQ4111. A pending fact whose object shows its `from` again, or a state its `from` widens to, is stale, not
pending: MQ4103, never MQ4101 (a pending drop whose object is back, a pending backfill whose column is nullable
again). Pending facts never reach a migration, so this needs no baseline. The validator (given the resolver) resolves
a database to decide only when it holds pending facts; without a resolver (the fixture harness), every pending fact is
MQ4101. Facts in a non-PostgreSQL database add MQ4056. `BuiltinRules.CheckElementName` skips an empty
`migration` name. `RuleCatalog.FamilyLabels` gains `["MQ41xx"] = "Migrations"`.

*Pending facts and resolution.* MQ4101 does not block the shared resolution: `ResolveSnapshotAsync`
(`ModelStore.ResolverFindings.cs:41-47`), `FindAsync`, the preview session (`GenerationService.Packs.cs:323-325`) and
`ReadableAsync` (`GenerationService.Resolved.cs:44-46`) gate on errors other than MQ4101
(`Outcomes.BlocksResolution`). A generation run (plan, apply, check) still stops on it: `Outcomes.IsInvalid` is
unchanged.

*Stale facts* (MQ4103, warning; `Migrations/StaleFacts.cs`). Candidates are the open facts the baseline lacks
(decision 13). Per object (by current key), candidates compose in applied order, with the widenings between and after
them, into one net change from the first candidate's `from` to the object's state in the working model. The chain is
stale when that net change needs no fact: the same state (a rename made and undone, a type changed and changed back)
or a widening. A pending fact whose object moved on to a state that still needs a fact is not stale: the write path
keeps its `to` current, and composition covers merged copies. The validator judges pending facts (above); the store
reports stale chains for their complete facts: plain `validate` through its resolver findings, with the committed
snapshot as baseline (no committed snapshot: no complete fact is judged); `validate --against` through
`BaselineCheck`. `migration list` marks both.

*Applied order and projection* (`Resolution/ResolveRun.Migrations.cs`). Per database, a topological order over
`after` that takes the ready fact with the ordinally smallest id; links to missing facts are ignored for ordering and
reported as MQ4111. The resolved lists, `migration list` and `list_migrations` share it. A fact's recorded keys move
through the `rekey` facts after it in applied order (`Migrations/FactKeys.cs`), so a rename recorded before a
materialize projects onto the stored column. Projection by current object key: `FormerNames` are the `from` and `via`
names of its renames, oldest first, distinct, without the current name; `Conversion` is that of the column's last
`change-type`; `Since` is the id of its first fact. A projected table adds `e:<factId>` to its dependencies (and so to
its columns'), and a projected view or sequence to its own (decision 22). Facts never change a capture.
`get_resolved_model` gains the scope `migrations` (`MigrationRecord`).

*`validate --against`* (`ModelStore.ValidateAgainstAsync`, `Migrations/BaselineCheck.cs`). The whole-model validation
runs, then each database in both models (by id) is captured on both sides (the cached whole-model resolve when that
side has no error, else per-database resolves without owners that have errors). The baseline capture is first read
under today's keys: the working model's open `rekey` facts that the baseline lacks apply to it in applied order
(`SnapshotRekey.Apply`). Then it is classified from baseline to working. Every change that needs a fact must be
explained by facts absent from the baseline: renames composed from the baseline name to the working name;
`change-type` facts composed from the baseline type to the working type, widenings allowed around them, each step with
its intent; a `drop` for a drop, a `backfill` for a column made not null. A moved key with no `rekey` fact reads as a
drop and an add, so it needs a `drop`; the message suggests a rekey when the added object has the dropped one's name
(name matching words the message only). Pending and held facts explain their change (MQ4101 reports the rest).
Otherwise MQ4102 at the working element: the column entry of a designed table or overlay, the attribute of a derived
column, or the database file for a dropped object. Baseline objects whose owners have errors, and what baseline files
that did not load hold, are skipped with MQ4109. A database only in the baseline is not checked. Nothing reads a ledger
table, a physical snapshot or a database.

*Fold* (`ModelStore.FoldMigrationsAsync`, `Migrations/FactFolder.cs`). Candidates are the chosen databases' open facts
the baseline lacks (decision 13: `Against`, else the committed snapshot's `facts`, else pending facts only). Stale
chains go first. Then two candidates on one object (by current key) join when they are consecutive (no other open fact
of the same table lies between them in applied order), in one database, and both held or both not held:

- renames: `a` to `b` and `b` to `c` become `a` to `c` with `via: ["b"]`, keeping every intermediate name;
- a `change-type` whose own step is a widening joins its neighbour, which keeps its conversion and data check;
- two `change-type` facts that are both still pending join (neither carries a cast yet; after a merge of copies);
- two `change-type` facts that each carry a conversion are refused and stay in order (`FoldRefusal`);
- a rename then a drop of the object join into the drop, the names in `object.formerNames`;
- rekeys chain, and a chain back to its first key is removed; `raw`, `adopt`, `baseline` and the maps never join.

The joined fact keeps the last fact's id and file. Its `after` is the union of the members' `after`, minus the
members. The others are deleted, and `after` links to them name the kept fact, unless that would order facts in a
cycle: then the join is refused (`FoldRefusal`, "the join would order facts in a cycle"). Writes go through one batch
on the store's atomic path, which records nothing (only facts change). `DryRun` returns `Files` and writes nothing.

*Facts in the schema diff* (`SchemaDiff/SchemaDiffer.cs`, `Generation/GenerationRun.cs`,
`Generation/PreviewInputs.cs`). Before every diff, the baseline capture is read under today's keys: after the
committed snapshot's aliases (`SnapshotAliases.Normalize`), the open `rekey` facts it lacks apply in applied order
(`SnapshotRekey.Apply` returns the capture unchanged when no key is left to move, so aliases and facts never move a key
twice). That covers generation with the committed snapshot, `generate --against` and `LazySchemaDiffs`.
`SchemaDiffResult.Facts` lists the facts that render: complete or released facts the baseline lacks, in applied order.
A fact the baseline holds as held and not released, and the working model holds as released, counts as lacking. With
the committed snapshot the baseline's facts are its `facts`; with `--against`, the baseline model's. Pending facts and
held facts not released are never listed. `IsEmpty` also needs an empty `Facts`, `ToRevision` advances when the diff
is not empty, and `Hash` covers the facts' ids and hashes. `FromState` and `ToState` hash the two captures. A
successful apply writes the committed snapshot with `facts` extended by the diff's `Facts` (whatever the dialect wrote
for them), pruned to ids still in the model; `generate --against` writes nothing there.

*Facts a build reaches.* A script that builds a database at the model's shape (`schema.sql`, a migration from nothing)
reaches the end state of every open fact except pending ones, `raw` (its `skipWhen` guard decides) and `adopt` (its end
state is object rows). Both scripts write ledger fact rows for exactly that set, in applied order; held facts are in it,
since the model already shows their change. A pending fact gets no row even where MQ4101 is lowered: its `to` may still
move, and once it is complete a later migration renders it, guarded by its end state.

*sql-ddl* (`packs/sql-ddl/migration.scriban`, `_migration.scriban`). `schema_diff[db.name].facts` is
`SchemaDiffResult.Facts`; held facts come from `database.migrations`.

- Each listed fact renders a step on PostgreSQL. Where a diff change has the same object (by current key), the step
  takes that change's place: a rename is the diff's own `RENAME` statement (a schema rename, the diff's schema
  statement); a `change-type` writes its data check as a guard (raise when the query returns a row), then
  `ALTER COLUMN ... TYPE ... USING` with the conversion, replacing today's TODO; a `backfill` writes its `UPDATE`
  before `SET NOT NULL`; a released `drop` writes the drop.
- A listed fact with no matching change writes its own step, guarded by its end state: a released drop whose object
  left the baseline while it was held (`DROP ... IF EXISTS`), a fact completed after an earlier migration wrote its
  TODO (a rename or type change inside a `DO` block that checks the catalog first), a stand-alone `backfill` (its
  `where` is the guard). `raw` runs in its phase behind `skipWhen`. A `rekey` writes no DDL. `adopt`, `baseline`,
  `value-map` and `state-map` write a TODO comment in phase 1 (decision 2).
- A diff from nothing (the database had no baseline) builds the database at the model's shape: no fact steps except
  `raw` behind its guard, and one fact row per fact a build reaches (above), as `schema.sql` writes them (3.3).
- Each step is followed by its ledger fact row (3.3; `adopt` gets none). A held fact that is not released writes a
  comment where its change would go (a held drop keeps the object) and no row. Outside PostgreSQL, today's TODOs stay
  and no rows are written (MQ4056).
- The file is `<db>/migrations/<NNNN>-<key>.sql` when the committed snapshot is the baseline (`NNNN` is `ToRevision`,
  today's counter, so name order is apply order on one line of history; a missing snapshot counts as revision 0), and
  `<db>/migrations/<key>.sql` with `--against`, where nothing counts revisions. `key` is the first 12 hex digits of
  SHA-256 over `mq-migration-1`, `FromState`, the diff hash, and the facts' ids and hashes in applied order (a diff
  with no facts: the state and the diff hash alone). Two copies that diverge from one revision write distinct names
  that sort side by side. Each file opens with comment lines naming its `FromState`, its `ToState` and its fact ids:
  a file follows the one whose `ToState` is its `FromState`. `docs/guides/schema-changes.md` gives that rule to
  migrators, and says a team that applies a folder in name order keeps the committed snapshot.
- Nothing renders when the diff is empty: the per-revision placeholders go. Earlier files stay owned through the
  writer rule below, the counter-named ones included, so a check right after an apply is clean.

*Once files stay owned* (`Writing/WriteRun.cs` `OrphanAsync`). An owned orphan of a pack that still exists, outside a
companion (`StripCompanion` tells them apart), whose file is on disk, keeps its manifest entry and is reported `Kept`;
other owned orphans stay `OrphanedOwned` as today. Code note: today a `once` file no run produces becomes
`OrphanedOwned` (`WriteRun.cs:734-738`), which check mode counts as drift (`Outcomes.cs:38-41`), the chain
`docs/engineering/workstream-notes.md:246` raised and `FullRunTests` records as a known gap. This amends D29 and
engine-design 12.3 ("owned orphans"). Scaffold files of removed elements are then reported `Kept` rather than
`orphaned-owned`.

**CLI and MCP**

- `maquettiste validate [--against <snapshot id | folder>] [--format text|json|sarif] [--output <file>]`: exit 1 on
  errors (pending facts, unexplained changes), 4 when the value resolves to nothing.
- `maquettiste migration list [--database <name | id>] [--format text|json]`: a line per fact in applied order
  (order, id, fact, state and missing intent, stale mark, object and names); json is `MigrationList`; exit 0, 1 for
  an unknown database (as `migration ledger` answers one, 3.3).
- `maquettiste migration fold [--database <name | id>] [--against <snapshot id | folder>] [--dry-run]
  [--format text|json]`: joins, removals, refusals (json `FoldResult`); exit 0, 3 on a conflict, 1 when invalid or
  for an unknown database, 4 when `--against` resolves to nothing. `migration` is a new group in `CliApp.RunAsync`,
  distinct from the `migrate` format stub.
- MCP `list_migrations` (engine catalog `Agent/tools.json`, shared with the assistant): `database?`, `cursor?`, `limit?`
  (default `ModelPages.DefaultLimit`, 100; at most `ModelPages.MaxLimit`, 1000); answers `{ facts, total, next }` in
  applied order. The cursor is opaque, in `ModelPages`' style (a version prefix, then base64url of the last row's
  database id and fact id); the next page starts after that fact's position in the current order. An unknown database or
  cursor answers `bad-request`. MCP `fold_migrations` (`Mcp/ModelTools.Migrations.cs`): `database?`, `against?`,
  `dryRun?`; answers `FoldResult`; codes `bad-request`, `conflict`, `invalid`. MCP `validate` gains `against?` (folders
  only where `AgentToolsOptions.AllowFolderBaselines` is set).
- `save_element`, `create_element` and `delete_element` answers carry `facts` (every fact the write recorded,
  cascades included); `apply_batch` carries `facts` on the batch answer and none on its items. The pending ones'
  MQ4101 is in `diagnostics`; the save succeeds with no prompt. Pinned counts: MCP tools +2 (`list_migrations` and
  `fold_migrations`: the `McpSurfaceTests` Tools array, the `docs/mcp.md` table and counts, README) and
  `AgentTools.All` +1 (`list_migrations` is a catalog tool: `AgentToolsTests`).

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4101 | E | A migration fact lacks the intent its change needs: a conversion, a data check for a narrowing, a drop's acknowledgement, a backfill's value or data check, a raw step's skipWhen guard; complete it or hold it. | none |
| MQ4102 | E | A change since the baseline needs a migration fact and has none: a rename or moved key, a type change that is not a widening, a dropped table, column or sequence, or a column made not null. | none |
| MQ4103 | W | An open fact the baseline lacks is stale: the model no longer shows its combined effect (a change made and undone, a pending change reverted or widened back); migration fold removes it. | none |
| MQ4104 | E | The after links of a database's open facts form a cycle. | none |
| MQ4105 | E | A fact's after link names a fact of another database. | none |
| MQ4106 | E | A fact's fields do not fit its type: from equal to to, a conversion or data check with both forms or neither, released without held, a key or object listed twice, a values code listed twice or mapped to itself, an object kind the type does not take. | none |
| MQ4107 | E | Two open facts change the same object and neither comes after the other (merged copies changed one object); order them with after or fold them. The object is the fact's object key: a column for a column fact, a table for a table fact; a table drop also changes each of its members. | none |
| MQ4108 | W | A fact's SQL text has no entry for the database's dialect and no "*" entry. | none |
| MQ4109 | W | The baseline has errors that leave objects unresolved, so changes to them were not checked. | none |
| MQ4110 | I | A write changed objects whose elements have errors, so no facts were recorded for them; validate --against checks them. | none |
| MQ4111 | W | A fact's after link names a fact that does not exist (folded or removed in another copy); the link is ignored for ordering. | none |

MQ4056's text gains "migration facts outside PostgreSQL (a fact's migration keeps TODO comments where its steps would
go, a rekey writes no DDL on any dialect, and no ledger rows are written)". Catalog count +11.

**Docs.** `migration.json`, `maquettiste.json` and sample copies; `docs/guides/schema-changes.md` (facts, pending
facts and how they follow their column, widening table, held steps, stale facts and baselines, fold, sql-ddl output,
file names and the `FromState` chain); `docs/user-guide.md` (kinds table, MQ41xx rules, CLI, the `orphaned-owned` row
of the file table); `docs/engineering/engine-design.md` (12.3 and D29: once files stay owned); `docs/mcp.md`;
`docs/api/openapi.yaml` (kind enum, `ProjectSettings.migrations`, `facts` on save and batch results, the
resolved-model scope); `packs/sql-ddl/README.md` and the sample's template copy; `docs/demo.md` (migration file
names); `samples/reference-app/README.md`; `docs/releases.md`; the modeling skill (folder row; complete pending facts
with `save_element`; fold facts, never delete them by hand). Editor follow-ups, not phase 1: a migrations panel (list,
pending form, fold, release), asking for intent right after a save, validate against a snapshot in the Problems panel,
a Migrations explorer node. Phase 1 editor work is `npm run gen`, the exhaustive kind maps and the recorded plan mock.

**Tests** (Engine `Integration/FactRecordingTests.cs` unless named; each copies billing to a temporary folder.)

- Done when "narrowing `invoice.number` from `text` to `varchar(20)` fails `validate --against` a snapshot, and a
  folder, until its conversion and a data check are recorded":
  `Narrowing_invoice_number_fails_validate_against_a_snapshot_until_conversion_and_data_check_are_recorded` and its
  `_against_a_folder_` twin (the copy's overlay column goes `text`, baseline, `varchar(20)`; checked with `record: off`
  for MQ4102 and `automatic` for MQ4101); CLI
  `ValidateTests.Validate_against_a_snapshot_and_a_folder_exits_1_until_the_fact_is_complete`.
- jsonb to json (phase 1 brief): `Changing_a_jsonb_column_to_json_needs_a_change_type_fact` (a `jsonb` column
  added to the copy's `notes`).
- Done when "a rename made through MCP writes one fact file with no prompt": CLI
  `McpWriteTests.A_rename_through_save_element_writes_one_fact_file_with_no_prompt`.
- Done when "open facts reach templates with no seal and no physical snapshot kept":
  `Open_facts_reach_templates_with_no_seal_and_no_physical_snapshot` (no `snapshots/`; a test unit's preview prints
  `database.migrations` and `column.former_names`).
- Done when "two copies that each rename a different column merge with no conflict":
  `Two_copies_that_each_rename_a_different_column_merge_with_no_conflict` (A renames a `notes` column, B the invoice
  number; changed files are disjoint; the union validates against the base and lists both facts) and
  `Two_copies_that_each_rename_a_different_column_of_one_table_merge_with_no_conflict` (A renames `notes.body`, B
  `notes.entity_type`; no MQ4107).
- Done when "nothing in the engine reads version control": 3.1's `NoVersionControlTests`.
- Paths: `Changing_an_attribute_length_records_a_change_type_on_its_column` (Customer's `name`, 120 to 80, no pinned
  native type), `Changing_an_attribute_length_under_a_pinned_native_type_records_nothing` (invoice `number`, whose
  overlay pins `varchar(32)`), `Renaming_an_entity_records_renames_of_its_table_and_foreign_key_columns`,
  `Renaming_a_database_schema_records_one_schema_rename`,
  `A_conventions_change_records_renames_through_save_settings`,
  `Default_locale_change_of_a_plural_name_records_a_table_rename`,
  `Materialize_records_one_rekey_from_its_key_map_and_no_drop`,
  `Undoing_a_materialize_records_the_rekey_back_and_no_drop`,
  `Deleting_a_column_and_adding_one_with_its_name_records_a_drop` (the new column carries an explicit new id;
  without one, 5.1's MQ1014 refuses the save), `Snapshot_restore_records_no_facts`,
  `Record_off_records_nothing`, `Apply_batch_dry_run_lists_the_facts_it_would_record`,
  `A_dry_run_that_reaches_every_database_computes_no_facts`,
  `Batch_answers_carry_facts_on_the_batch_and_none_on_items`, `After_is_set_to_the_heads_on_the_same_table`,
  `A_second_narrowing_of_a_pending_column_keeps_one_pending_fact`,
  `A_widening_after_a_pending_narrowing_keeps_the_fact_pending`,
  `A_rename_recorded_before_a_materialize_projects_onto_the_stored_column`.
- Cost and walker: `Description_edits_resolve_no_database`, `A_change_in_one_database_resolves_only_that_database`,
  `First_entity_save_after_load_resolves_only_its_databases` (the copy gains a second database that takes no billing
  package; a resolve counter on the store), `A_package_edit_chooses_only_databases_whose_conventions_reach_it`,
  `Footprint_selection_covers_every_database_a_change_reshapes`, `Database_reach_covers_every_resolved_footprint`
  (billing, `clean` and the reference-data fixtures: every `e:` key of a database's resolved objects is in its reach),
  `Fact_object_keys_are_historical_and_never_dangle`, `Deleting_a_database_deletes_its_facts_and_records_none`,
  `A_dangling_after_link_is_a_warning_and_ignored_for_ordering`, `A_recorded_fact_without_a_name_validates_clean`.
- `Migrations/WideningTests.cs`: `Postgres_widening_table_matches_the_scope` (a row per widening and not-widening),
  `Widening_table_in_the_guide_matches_the_code`.
- `Migrations/FactHashTests.cs`: `Fact_content_hash_covers_the_facts_meaning_only` (annotations, `after`, `held` and
  `released` change nothing; a key, a state or a conversion changes the hash).
- Order, stale, held, fold: `Open_facts_list_in_after_order_with_ties_by_id` (resolved model, CLI and MCP agree),
  `List_migrations_pages_with_a_cursor_in_applied_order`,
  CLI `MigrationTests.Migration_list_with_an_unknown_database_exits_1`,
  `A_change_made_and_undone_leaves_stale_facts_as_a_warning`, `A_reverted_pending_change_is_stale_not_pending`,
  `A_held_fact_is_complete_for_validate_until_released`, `Fold_joins_renames_keeping_intermediate_names`,
  `Fold_joins_a_conversion_and_a_widening`, `Fold_refuses_two_conversions_that_carry_casts`,
  `Fold_keeps_the_last_id_and_rewires_after`, `Fold_never_creates_an_after_cycle`,
  `Fold_against_a_baseline_leaves_its_facts_alone`, `Fold_without_a_baseline_leaves_rendered_facts_alone` (a rename
  `a` to `b` rendered into the committed snapshot's `facts`, then `b` to `a`: fold removes nothing and the second rename
  stays).
- Resolution: `A_pending_fact_keeps_previews_and_resolved_reads_and_stops_generation`; `ResolvedCoverageTests` gains
  the targets `migration` (`migration.json`, root, `RMigration`), `migration object` (`$defs/migrationObject`,
  `RMigrationObject`), `migration state` (`$defs/migrationState`, `RMigrationState`), `migration key` (`keys/[]`,
  `RMigrationRekey`), `migration value` (`values/[]`, `RMigrationValue`), `migration conversion` (`conversion`,
  `RConversion`) and `migration data check` (`dataCheck`, `RMigrationDataCheck`).
- Rules: `ValidationFixtureTests.Invalid_model_matches_its_golden_diagnostics` gains the family `migrations` (MQ4101,
  MQ4104 to MQ4108, MQ4111, MQ4056) over `tests/fixtures/validation/migrations/` and its `expected.json`, with a row in
  `tests/fixtures/validation/README.md`. The `clean` fixture gains one complete `rename` fact (A5), so
  `Fixture_model_kinds_cover_every_element_kind_in_the_clean_model` and `Clean_model_yields_zero_diagnostics` hold.
  MQ4102, MQ4103, MQ4109 and MQ4110 need resolution or a baseline and are tested in `FactRecordingTests`.
- Writer: Engine `OutputWriterTests.Once_mode_writes_only_when_missing_and_never_deletes` (the third run reports `Kept`
  and keeps the entry), `OutputWriterTests.A_deleted_once_file_is_an_owned_orphan`,
  `FullRunTests.Migrations_are_written_once_and_a_check_after_the_apply_is_clean` (replaces the known-gap test),
  `OutputModeTests.Owned_orphans_stay_on_disk` and the two `ModelChangeTests` scaffold cases (now `Kept`).
- Schema diff: Engine `SchemaSnapshotTests.Rendered_facts_join_the_committed_snapshot_and_held_ones_never_do`,
  `SchemaSnapshotTests.A_new_fact_alone_makes_the_diff_not_empty`,
  `SchemaSnapshotTests.Generate_after_materialize_with_only_a_rekey_fact_plans_no_drop` (aliases removed from the
  committed snapshot; the fact alone keeps one table).
- Packs (`MigrationFactTests`): `Migration_files_are_named_by_revision_and_content_and_name_their_states`,
  `Migration_files_under_against_are_named_by_content`, `A_complete_change_type_renders_its_data_check_and_conversion`,
  `A_released_held_drop_renders_in_the_next_migration`, `Apply_then_check_after_a_migration_is_clean`,
  `Counter_named_migration_files_stay_owned_after_the_move_to_content_names`,
  `Value_maps_adopt_and_baseline_render_a_todo`,
  `Fact_steps_run_on_postgres_and_a_second_run_does_nothing` and
  `A_migration_records_its_facts_once_and_a_failed_one_none` (both with `MAQUETTISTE_TEST_POSTGRES_CONTAINER`).

**Tasks** (paths under `src/Maquettiste.Engine/` unless prefixed)

- **A4. Once files stay owned:** the writer rule. Owns `Writing/WriteRun.cs`, `Pipeline/PipelineTypes.cs` (the
  `OrphanedOwned` comment), `Generation/README.md`, `docs/engineering/engine-design.md` (12.3, D29),
  `docs/user-guide.md` (the `orphaned-owned` row), `tests/Maquettiste.Engine.Tests/{Writing/OutputWriterTests,
  Generation/OutputModeTests,Integration/ModelChangeTests,Integration/FullRunTests}.cs`. Tests: the writer tests
  above. Verify: `E "~OutputWriterTests|~OutputModeTests|~ModelChangeTests|~FullRunTests|~PackRenameTests"`.
- **A5. Kind and setting:** `ElementKind.Migration`, records, `migration.json`, `KindInfo`, `ConventionalFolder`,
  `migrations.record`, the `clean` fixture's fact, the empty-name case (decision 16). Owns
  `Model/{ElementKind,KindInfo,Migrations,ElementBase,ProjectSettings}.cs`, `Loading/ModelPaths.cs`,
  `Validation/BuiltinRules.cs` (the empty-name case), both schemas and copies, `openapi.yaml`,
  `tests/fixtures/validation/clean/.maquettiste/model/databases/main/migrations/open/`, the editor's generated types
  and kind maps. Tests: `FactRecordingTests.A_recorded_fact_without_a_name_validates_clean` (A5 creates the file).
  Verify: `E "~Json|~Loading|~Validation|~FactRecordingTests"`; `F "~ContractTests"`; `editor`.
- **A6. Widening table:** `Migrations/Widening.cs` and the guide's table. Verify: `E "~WideningTests"`.
- **A7. Fact rules:** MQ4101, MQ4104 to MQ4108, MQ4111, MQ4056 text, family label, user-guide table and the
  resolution gate for MQ4101. Owns `Validation/{MigrationRules,ModelValidator}.cs`,
  `Diagnostics/RuleCatalog.cs`, `ModelStore.ResolverFindings.cs`, `GenerationService.{Packs,Resolved}.cs`,
  `Generation/Outcomes.cs`, `tests/fixtures/validation/{migrations/,README.md}`, `ValidationFixtureTests`, the
  `CatalogTests` count, `src/editor/src/mocks/recorded/validation-rules.json`. Tests: the rules and resolution tests
  above. Verify: `E "~MigrationRules|~ValidationFixtureTests|~CatalogTests|~FactRecordingTests"`; `record`.
- **A8. Order and projection:** applied order, `RMigration` and its parts, current keys, projection, the resolved-model
  scope. Owns `Resolution/{ResolveRun.Migrations,ResolvedPhysical,ResolvedCore}.cs`,
  `Migrations/{FactKeys,FactHash}.cs`, `Editor/ResolvedRecords.cs`, `Agent/tools.json` (scope enum),
  `ResolvedCoverageTests`. Verify: `E "~Resolution|~ResolvedRecords|~AgentToolsTests|~FactHashTests"`;
  `MAQUETTISTE_UPDATE_GOLDEN=1` once for the resolved billing golden (intended: empty `former_names` and `since`).
- **A9. Classifier core:** `FactClassifier` over two captures and the write's key maps, `after` heads, schema renames,
  pending facts that follow their column, fact documents. Owns `Migrations/{FactClassifier,FactDocuments}.cs` and
  `FactClassifierTests`. Verify: `E "~FactClassifier"`.
- **A10. Recording on store writes:** the `ExecuteCoreAsync` hook, capture cache with footprints, `DatabaseReach`,
  selection, the skip list, dry-run cap, `freeze`, materialize key maps, `RecordedFact`, MQ4110 and its exclusions. Owns
  `ModelStore.cs`, `ModelStore.Migrations.cs` (new), `ModelStore.{Schemas,Materialize}.cs`,
  `Migrations/DatabaseReach.cs`, `Resolution/{ModelResolver,ResolveRun}.cs`. Tests: paths, cost, walker. Verify: `E
  "~FactRecordingTests|~Store|~MaterializeTests"`.
- **A11. Other write paths:** settings, default-locale and cascade paths; MCP and HTTP answers carry `facts` (batch
  level for batches). Owns `ModelStore.DefaultLocale.cs`, settings part of `ModelStore.cs`,
  `Loading/ChangePlanner.Cascade.cs` (the settings note only), `Editor/SettingsDocuments.cs`,
  `src/Maquettiste.Cli/Mcp/ModelTools.cs`, `openapi.yaml`. Verify: as A10; `C
  "~McpSurfaceTests|~McpReadTests|~McpWriteTests"`; `F "~ModelWriteTests"`.
- **A12. `validate --against`:** `ValidateAgainstAsync`, `BaselineCheck` (its capture helper, rekey step included,
  serves A3), `StaleFacts`, MQ4102, MQ4103, MQ4109, CLI flag (`--against` joins `CommandLine.ValueOptions` here), MCP
  and HTTP arguments, `AllowFolderBaselines`, one library per host. Owns `Migrations/{BaselineCheck,StaleFacts}.cs`,
  `ModelStore.Migrations.cs`, `src/Maquettiste.Cli/{CommandLine.cs,Commands/ValidateCommand.cs,
  Commands/McpCommand.cs}`, `Agent/{AgentTools.cs,AgentTools.Handlers.cs,tools.json}`, `ValidationEndpoints.cs`; also
  touches `Validation/{MigrationRules,ModelValidator}.cs` and `Generation/EngineServices.cs` (the validator's resolver
  for pending stale facts, decision 20), `ModelStore.cs` (plain validate's stale chains), `Mcp/ModelTools.cs` (it sets
  `AllowFolderBaselines`, so `McpCommand.cs` needs no change) and `openapi.yaml` (the `against` query). Tests:
  the done-when tests, `AssistTests.Validate_against_a_folder_through_the_assistant_answers_bad_request`. Verify: `E
  "~FactRecordingTests"`; `C "~ValidateTests|~McpSurfaceTests|~McpReadTests|~McpWriteTests"`; `F
  "~SnapshotEndpointTests|~AssistTests"`.
- **A13. `migration list`:** `ListMigrationsAsync` with its cursor, CLI verb, `list_migrations`, counts. Owns
  `Commands/MigrationCommand.cs`, `CliApp.cs`, `Agent/AgentTools.Migrations.cs`, `tools.json`, `docs/mcp.md`,
  `README.md`, `McpSurfaceTests` (Tools array, +1), `AgentToolsTests` (+1). Verify: `E
  "~AgentToolsTests|~FactRecordingTests"`; `C "~Migration|~McpSurfaceTests|~McpReadTests|~McpWriteTests"`.
- **A14. `migration fold`:** `FactFolder` (baselines, consecutive facts, `after` union, cycle refusal),
  `FoldMigrationsAsync`, CLI verb, `fold_migrations`, counts. Owns `Migrations/FactFolder.cs`, `MigrationCommand.cs`,
  `Mcp/ModelTools.Migrations.cs`, `docs/mcp.md`, `README.md`, `McpSurfaceTests` (Tools array, +1). Verify:
  `E "~Fold"`; `C` as A13.
- **A15. Facts reach the schema diff (engine):** `SchemaDiffResult.Facts`, `FromState`, `ToState`, `IsEmpty`,
  `ToRevision` and `Hash`; the rekey step before every diff; `PhysicalSnapshot.Facts` upkeep. Owns
  `SchemaDiff/{SchemaDiffTypes,SchemaDiffer,SnapshotRekey}.cs`, `Generation/{GenerationRun,SnapshotAliases,
  PreviewInputs}.cs`, the save path in `GenerationService.cs`. Tests: the schema-diff tests above. Verify: `E
  "~SchemaDiff|~SchemaSnapshotTests|~MaterializeTests|~Preview"`.
- **A16. sql-ddl renders facts (templates):** fact steps, held comments, TODOs for maps, adopt and baseline, ledger
  rows through A20's `_ledger.scriban` (in a migration from nothing, for the facts a build reaches), file names and
  headers, no placeholders. Owns `packs/sql-ddl/{migration,_migration}.scriban`,
  `packs/sql-ddl/README.md`, the sample's template copies and `samples/reference-app/{README.md,
  .maquettiste/templates/sql-ddl/README.md}`, `docs/demo.md`, `src/editor/src/mocks/recorded/plan.json`, the goldens
  under `tests/fixtures/golden/sql-ddl/`, a new helper `tests/Maquettiste.Packs.Tests/MigrationFiles.cs` (the migration
  file a run added, by database), `MigrationFactTests` (new), and the counter names in `SqliteTests`, `CycleTests`,
  `ServerTests`, `ReferenceBindingSqlTests`, `ReferenceDataTests`, `LifecycleTests`, `MigrationSafetyTests` and
  `DdlCoverageTests` (42 references, moved to the helper). Code notes: Engine `ManifestStoreTests` uses
  `db/main/migrations/0001.sql` as literal manifest data and needs no change; the sample's
  `.maquettiste/manifest/sql-ddl.json` and its `db/main/migrations/000N.sql` stay as they are (A4 keeps them owned),
  which the sample's own check confirms. Verify: `PG P`;
  `MAQUETTISTE_UPDATE_GOLDEN=1` once (intended: `0001.sql` becomes `0001-<key>.sql` with its state header; no other
  byte changes for models without facts).
- **A17. Docs sweep:** guide, user guide, skill, releases, pack README match A1 to A16. Verify: `C
  "~SkillRefreshTests|~AgentSetupTests|~McpSurfaceTests|~McpReadTests|~McpWriteTests"`.

### 3.3 The ledger table contract

**Decisions taken here**

- Three tables in one schema: `contract` (one version row), `applied_fact` (one row per applied fact) and
  `applied_object` (one row per object whose identity the writer owns), as scope 6.2 and 14.2 ask. Table names are
  fixed; only the schema is a setting, so every migrator finds the tables the same way.
- The schema is `migrations.ledgerSchema` in `maquettiste.json`, in the `migrations` object 3.2 adds for
  `migrations.record`. Default `migration_ledger`: it names the job, not a product. It is a lower-case identifier; a
  name on PostgreSQL's reserved-word list (`user`, `order`) is quoted wherever the DDL, the grant and the scripts write
  it (the list `sql_quote` reads, `SqlDialects.IsReserved`). `docs/ledger.md` tells other writers to do the same.
- One DDL text, a PostgreSQL engine resource, read by sql-ddl, the CLI and MCP. Other dialects get no ledger in
  phase 1 (16.1). 3.2's MQ4056 for facts outside PostgreSQL is the only report of it (see Engine behaviour).
- In phase 1 sql-ddl writes fact rows (in migrations through 3.2's A16, in `schema.sql` here) and the object rows of
  run-once SQL objects (3.4). Every SQL object's row, with its revert text, follows with 6.3 (phase 3); `revert_text`
  is in version 1 already, so 6.3 needs no new version.
- A script writes the ledger DDL only when it also writes a ledger row, so a model without facts or run-once objects
  generates the same bytes as today.
- Version 1 is a draft until the owner freezes it before phase 1 ends (6.2, 16.5). Each item lands as a pre-release
  (16.5), so databases may hold draft tables before the freeze. A change before the freeze is therefore additive only:
  a new nullable or defaulted column, added by `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` after its `CREATE TABLE` in
  the same DDL. A column is never renamed, retyped or removed, so every draft database converges when the DDL runs
  again. The freeze (A23) pins the DDL; after it, any change is version 2 with a conversion in the DDL.
- As built (A18 to A20, 2026-10-31): `ledger.json` joins `KindInfo.DocumentSchemaFiles` (the schema folder test lists
  every file). `Ddl` and `ReadGrant` quote a name that is reserved or not plain (`^[a-z_][a-z0-9_]*$`), doubling `"`;
  both throw `NotSupportedException` outside PostgreSQL and `ArgumentException` for an empty name. Only PostgreSQL
  databases add `s:migrations` to their membership keys. MQ4067 compares names without case unless the database quotes
  `always` (PostgreSQL folds unquoted names). `ledger_prelude` returns the DDL without its last line feed, so it stands
  as one statement block; `ledger_fact_row` quotes the schema with `sql_quote ... "reserved"`, as the DDL does, whatever
  the quoting settings; `_ledger.scriban` also holds `ledger_build_facts(db)`, the facts a build reaches, for
  `schema.scriban` and A16's migration from nothing. `schema.sql` adds one header comment line when it writes rows.
  `LedgerSqlTests` (A20) also holds the DDL and grant tests.
- As built (A21 and A22, 2026-10-31): `GetLedgerDdlAsync` without a database picks the only PostgreSQL database (other
  dialects do not count); a name two databases share is an `ArgumentException` asking for the id; an empty `grantRead`
  is an `ArgumentException` (`bad-request`), an empty `--grant-read` a usage error (4). `LedgerDdlAnswer.Database` is
  the database's name, `Dialect` its JSON name, `Schema` the setting unquoted, and `Grant` carries
  `JsonIgnore(Condition = Never)`, so the CLI's JSON (which drops nulls) still writes `"grant": null`. The text output
  puts one blank line between the DDL and the grant. `EngineBoundaryTests` also asserts that the engine references no
  database driver assembly. `docs/ledger.md` says object rows arrive with run-once SQL objects (A28); until then the
  scripts write fact rows only.
- (A23) The review of A22's note asked for no change, so version 1 is frozen as drafted: the DDL text is pinned by
  `Version_1_ddl_text_and_version_are_pinned` (the SHA-256 of the resource text with line feeds only), and
  `docs/ledger.md` says "Frozen, version 1".

**Model and schema**

`schemas/v1/maquettiste.json`, `migrations` object (x-order: `record` from 3.2, then `ledgerSchema`):

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `ledgerSchema` | string, pattern `^[a-z_][a-z0-9_]*$`, maxLength 63 | `"migration_ledger"` | Schema of the ledger tables in each PostgreSQL database; quoted where it is a reserved word. |

`schemas/v1/ledger.json` (new, not a model kind) describes one ledger's rows as a document. Property names are the
column names, so a writer in any language maps them one to one; each `$defs` row's `x-order` is the column order:

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `contract` | `$defs/contractRow` | | `id` (integer, const 1), `version` (integer, minimum 1). |
| `facts` | array of `$defs/factRow` | `[]` | `fact_id` (the fact id defined in 3.2), `content_hash` (the fact content hash defined in 3.2), `applied_at` (date-time), `applied_by` (string or null: free text naming the writer). |
| `objects` | array of `$defs/objectRow` | `[]` | `element_id` (a model id), `kind` (the model kind, such as `sql-object`; open vocabulary), `schema_name` (string or null), `name` (current name), `content_hash`, `revert_text` (string or null), `applied_at` (date-time). |

A byte-identical copy goes to `samples/reference-app/.maquettiste/.schema/v1/`. The DDL is
`src/Maquettiste.Engine/Ledger/ledger.postgresql.sql`, a new `EmbeddedResource` line in the csproj, LF endings, one
placeholder `{schema}`:

```sql
CREATE SCHEMA IF NOT EXISTS {schema};
CREATE TABLE IF NOT EXISTS {schema}.contract (id smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    version integer NOT NULL);
INSERT INTO {schema}.contract (id, version) VALUES (1, 1) ON CONFLICT (id) DO NOTHING;
DO $ledger$ BEGIN
    IF (SELECT version FROM {schema}.contract WHERE id = 1) > 1 THEN
        RAISE EXCEPTION 'ledger contract version is newer than 1; use a newer writer';
    END IF;
END $ledger$;
CREATE TABLE IF NOT EXISTS {schema}.applied_fact (fact_id text PRIMARY KEY, content_hash text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now(), applied_by text);
CREATE TABLE IF NOT EXISTS {schema}.applied_object (element_id text PRIMARY KEY, kind text NOT NULL,
    schema_name text, name text NOT NULL, content_hash text NOT NULL, revert_text text,
    applied_at timestamptz NOT NULL DEFAULT now());
```

The contract rules, as `docs/ledger.md` states them (each has a phase 1 test, except rule 8, tested with 6.3 in
phase 3):

1. Schema from `migrations.ledgerSchema`, quoted when it is a reserved word; fixed table names.
2. The DDL is idempotent and holds no transaction control: a writer runs it inside its own transaction before its rows,
   on every deploy if it likes.
3. A writer that finds a newer version stops (the DDL raises). A later version's DDL converts the tables and raises
   the version row in the same transaction.
4. Read grant: `GRANT USAGE ON SCHEMA {schema} TO <role>; GRANT SELECT ON {schema}.contract, {schema}.applied_fact,
   {schema}.applied_object TO <role>;`. Roles belong to environments (2026-10-06), so it is printed, never generated.
5. A writer inserts one fact row per fact when the database reaches the fact's end state (3.2), in the transaction of
   the statements that reach it, with `ON CONFLICT (fact_id) DO NOTHING`. Rows are never updated or deleted. A
   migration reaches a held fact's end state when the fact is released and applied; a script that builds the database
   at the model's shape reaches it at once. A writer records only the facts whose end state it reached. `applied_at`
   is the database's transaction time, so no writer's output holds a time.
6. Object rows are keyed by `element_id` and written only by the tool that owns the object's identity. It inserts the
   row when it applies the object and updates it when it applies a new version; a run-once object applies once, so
   its row keeps the version that ran. A `rekey` fact moves the rows of the keys it lists, which is its end state
   (3.2). Readers ignore unknown kinds and read a missing row as unknown. A migrator with its own identity map writes
   fact rows only.
7. `validate --against` and the rest of the engine never read the ledger. Run-once guards read it in the database;
   6.1 (phase 3) and a team's own migrator may read it.
8. A revert kept in `revert_text` must succeed when its object is already gone (6.3). Phase 1 writes no revert text.

**Public API**

```csharp
namespace Maquettiste.Engine.Ledger;
public static class LedgerContract   // schemas/v1/ledger.json, docs/ledger.md
{
    public const int Version = 1;
    public const string DefaultSchema = "migration_ledger";
    public const string ContractTable = "contract", FactTable = "applied_fact", ObjectTable = "applied_object";
    public static bool Supports(Dialect dialect);                  // PostgreSQL only in version 1
    public static string Ddl(Dialect dialect, string schema);      // NotSupportedException when !Supports;
                                                                   // the schema is quoted when reserved
    public static string ReadGrant(Dialect dialect, string schema, string role); // role quoted unless plain;
                                                                   // NotSupportedException when !Supports
}
namespace Maquettiste.Engine;
/// <summary>What `migration ledger` and `get_ledger_ddl` answer.</summary>
public sealed record LedgerDdlAnswer(int ContractVersion, string Database, string Dialect, string Schema,
    string Ddl, string? Grant);
public sealed partial class ModelStore
{
    /// The ledger DDL of one PostgreSQL database: the named one (name or id), else the only one. ArgumentException,
    /// naming the cause, when none exists, the named one is missing or not PostgreSQL, or several exist and none is
    /// named.
    public Task<LedgerDdlAnswer> GetLedgerDdlAsync(string? database, string? grantRead, CancellationToken ct);
}
namespace Maquettiste.Engine.Model;     // on 3.2's MigrationSettings record
public string LedgerSchema { get; init; } = LedgerContract.DefaultSchema;
namespace Maquettiste.Engine.Resolution; // on RDatabase; null when the dialect has no ledger
public string? LedgerSchema { get; internal set; }
```

**Engine behaviour**

- `LedgerContract.Ddl` reads the resource once, normalizes LF and fills `{schema}`, quoted when reserved; it adds no
  time, id or transaction statement.
- `DatabaseRun.Publish` (`Resolution/DatabaseRun.Finish.cs`) sets `RDatabase.LedgerSchema` on PostgreSQL and adds
  `s:migrations` to the database's membership keys, as it adds `TypeMaps`. `DependencyHasher.SettingsHash`
  (`Planning/DependencyHasher.cs:211-234`) gains a `"migrations"` case that hashes `LedgerSchema` only (`record`
  changes no rendered byte). Without it the key falls to `_ => Absent`, and a changed schema would leave cached
  `schema.sql`, migration and per-object files with the old name. `Planning/README.md` lists the key.
- MQ4067 runs in the resolver, in `Publish`, against the schema names it collects: declared schemas and those that
  tables, views, sequences, routines, database types and SQL objects name (`DatabaseRun.Finish.cs:663-670`). It
  reports on the database file at `/schemas/<i>/name` when the schema is declared, else without a pointer, naming the
  first object (in published order) that uses it.
- New built-in helper `ledger_ddl <database>`: the DDL, or `""` without a ledger. It is registered in
  `BuiltinHelpers.CreateBuiltins` and added to `BuiltinHelpers.Names` (`Rendering/BuiltinHelpers.cs:116-121`), which
  feeds completion (`GenerationService.Authoring.cs:427`) and reserves the name against pack helpers. It records
  `s:migrations`, as `TypeOf` records `s:typeMaps` (`Rendering/TypeOf.cs:187`). Every script that writes a ledger row
  writes the prelude through it, so each such script reads the key.
- New `packs/sql-ddl/_ledger.scriban`, owned here: `ledger_prelude(db)` (`ledger_ddl db`: the DDL, or `""`) and
  `ledger_fact_row(db, fact)`: one `INSERT INTO <schema>.applied_fact (fact_id, content_hash, applied_by) VALUES
  ('<id>', '<hash>', 'sql-ddl') ON CONFLICT (fact_id) DO NOTHING;`, or `""` for an `adopt` fact, whose end state is
  object rows that no writer of ours fills before 6.1. The schema goes through `sql_quote`. 3.4 adds
  `ledger_once_sql` and `ledger_rekey_rows` to the same file.
- Migrations: 3.2's A16 owns `migration.scriban` and `_migration.scriban` and places the rows. It writes
  `ledger_prelude` right after `BEGIN;` when the file records a fact, and follows each fact's step with its
  `ledger_fact_row`. The facts recorded are those of `schema_diff[db.name].facts` whose state is `complete` or
  `released`; a held fact not released keeps its comment and gets no row. Everything is in the file's one transaction,
  so a failed statement records nothing. Migration files are written once, so their rows are fixed.
- `schema.scriban`, owned here, builds the database at the model's shape, so it reaches the end state of the facts a
  build reaches (3.2): every open fact except pending ones, `raw` (its SQL is not run, and its `skipWhen` guard decides
  when a migrator runs it) and `adopt` (no object rows); held facts are included. On PostgreSQL, when the script writes
  a ledger row (a recorded fact here, or a run-once object from 3.4), it opens with `BEGIN;` after its header comments,
  writes `ledger_prelude` after the schemas and before the first SQL object, and ends with one `ledger_fact_row` per
  fact a build reaches, in applied order, then `COMMIT;`. The transaction makes the rows atomic with the DDL, as a
  migration's are. A SQL object body that cannot run in a transaction already cannot run in a migration. Without ledger
  rows the script is byte-identical to today.
- Outside PostgreSQL no script writes the ledger, and no 3.3 rule or comment says so a second time: 3.2's MQ4056 for
  facts in a non-PostgreSQL database (`Validation/MigrationRules.cs`) is the one report. Its catalog text (3.2, A7)
  ends with "and no ledger rows are written".

**CLI and MCP**

- `maquettiste migration ledger [--database <name|id>] [--grant-read <role>] [--format text|json] [--output <file>]`
  in the `migration` group (decision 2) calls `ModelStore.GetLedgerDdlAsync`. Text: the DDL, then the grant if asked.
  JSON: `LedgerDdlAnswer` (`{contractVersion, database, dialect, schema, ddl, grant}`), `grant` null unless asked.
  `--output` writes through `GuardedFiles(services.CreatePathPolicy(...))` as `WriteTarget.Output`. Exit 1 on the
  method's `ArgumentException` (no PostgreSQL database, the named one missing or not PostgreSQL, several and none
  named); 4 on a usage error. `--grant-read` joins `CommandLine.ValueOptions`.
- Engine catalog tool `get_ledger_ddl` (`Agent/tools.json`, `Agent/AgentTools.cs`, so MCP and the editor's assistant
  share it): `database` and `grantRead`, both optional. The handler calls the same method; its answer is the method's
  record, and its `ArgumentException` answers `bad-request`. Pinned counts: `AgentTools.All` +1
  (`AgentToolsTests`) and MCP tools +1 (the `McpSurfaceTests` Tools array, the `docs/mcp.md` table and counts, the
  `README.md` count), each from whatever the pin reads when the task lands.
- Follow-up: an HTTP operation (`GET /api/ledger`, `x-maquettiste-engine: ModelStore.GetLedgerDdlAsync`) and an editor
  screen. Until then the hosted product reaches the DDL through the assistant's catalog tool.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4067 | W | The ledger schema (`migrations.ledgerSchema`) is also a schema of a PostgreSQL database in the model, declared or named by one of its objects: the bookkeeping tables would share it with modeled objects. | |

**Docs**

New `docs/ledger.md` (the rules, the DDL, the grant, the quoting of a reserved schema name, which facts our scripts
record, the status line "Draft, version 1"), listed under Reference in `mkdocs.yml`. Also `docs/user-guide.md` (the
setting, a ledger paragraph naming `ledger_ddl`), `docs/engineering/engine-design.md` (the Helpers list),
`src/Maquettiste.Engine/Planning/README.md` (the `s:migrations` key), `packs/sql-ddl/README.md`,
`docs/guides/generate-code.md` (one paragraph), `docs/mcp.md` and `README.md` (tool, counts), the modeling skill and
`docs/releases.md` under "## 0.11.0 (unreleased)", which names `ledger_ddl` among the new reserved helper names
(MQ6013, as 5.5's note does for its own). Review note `tmp/response-next-capabilities-note-ledger-draft.md`,
anonymous, billing examples only. Outline: what it is (the version 1 draft, reviewed before the phase 1 freeze, and
additive-only until then); the three tables and their columns; the eight rules; what our scripts write (fact rows in
migrations and `schema.sql`, object rows of run-once objects), and that a team's migrator writes fact rows only; how
to get the DDL (`maquettiste migration ledger --grant-read <role>`, `get_ledger_ddl`) and the default schema; three
questions (the default name, whether `applied_by` helps, a column needed before the freeze) and a date.

**Tests**

- `LedgerContractTests`: `Ddl_is_idempotent_text_without_transaction_control_or_time`,
  `Ledger_schema_rows_match_the_postgresql_columns_and_nullability`,
  `A_reserved_schema_name_is_quoted_in_the_ddl_and_the_grant` (A18);
  `The_schema_setting_reaches_the_ddl_and_the_resolved_database`, `The_ledger_ddl_helper_records_the_migrations_key`
  (A19); at the freeze, `Version_1_ddl_text_and_version_are_pinned` (A23).
- `LedgerRuleTests.A_shared_ledger_schema_is_MQ4067` (theory: a declared schema, at `/schemas/<i>/name`; a schema only
  a table names, on the database file without a pointer).
- `LedgerSqlTests` (PostgreSQL container): `Ledger_ddl_runs_twice_in_one_transaction_and_a_newer_version_stops_it`,
  `The_read_grant_lets_a_role_select_and_not_insert`,
  `Schema_sql_records_its_facts_except_raw_ones_and_a_failed_run_records_none` (billing copied to a temporary folder,
  plus one rename fact, one held drop and one raw fact written as 3.2's files: the rename and the held drop are
  recorded, the raw fact is not). The migration counterpart,
  `A_migration_records_its_facts_once_and_a_failed_one_none`, belongs to 3.2's A16, which writes those rows.
- `LedgerGoldenTests`: `Schema_with_facts_writes_one_transaction_with_the_prelude_and_fact_rows` (golden
  `sql-ddl/ledger-facts`; A16 adds its migration file to the same golden),
  `Schema_sql_with_MQ4101_lowered_writes_no_row_for_a_pending_fact`, `A_model_without_facts_writes_no_ledger`
  (existing goldens unchanged), `Changing_the_ledger_schema_re_renders_the_scripts_that_write_the_ledger` (a plan
  after a `ledgerSchema` change lists `schema.sql` as changed).
- `MigrationLedgerCommandTests`: `Prints_the_ddl_and_the_read_grant_for_the_configured_schema`,
  `Exits_1_without_a_postgresql_database`, `Writes_output_through_the_path_policy`;
  `McpReadTests.Get_ledger_ddl_returns_the_contract_version_schema_and_ddl`, and the pinned list and counts in
  `McpSurfaceTests`;
  `AgentToolsTests` pinned count.
- Rule 6's rekey sentence: `LedgerSqlTests.A_rekey_fact_moves_the_row_of_a_run_once_object` (3.4, A28). Rule 7:
  `EngineBoundaryTests.The_engine_holds_no_database_connection_type` (reflection: no engine signature uses
  `System.Data.Common.DbConnection`).

**Tasks**

Legend in section 2; order in section 6.

- **A18. Engine contract** (after 3.2's A5, which creates `MigrationSettings` and the `migrations` block). Files:
  `schemas/v1/ledger.json`, `maquettiste.json` and copies, `Engine/Ledger/*`, csproj, the `MigrationSettings` file
  (`LedgerSchema`), `docs/api/openapi.yaml` (`ledgerSchema` under `migrations`), the generated editor types. Tests:
  the three `LedgerContractTests` marked A18. Verify: `E "~LedgerContract|~SchemaConsistency"`; `F "~ContractTests"`;
  `editor`.
- **A19. Resolver, planner and helper.** Files: `Resolution/ResolvedPhysical.cs` (`RDatabase.LedgerSchema`),
  `Resolution/DatabaseRun.Finish.cs` (the key, MQ4067), `Planning/DependencyHasher.cs` (`"migrations"` case),
  `Planning/README.md`, `Rendering/BuiltinHelpers.cs` (`ledger_ddl`, `Names`), `RuleCatalog.cs` (MQ4067),
  `CatalogTests` count +1, recorded rules. Tests: `LedgerRuleTests`, the two `LedgerContractTests` marked A19.
  Verify: `E "~Ledger|~CatalogTests|~DependencyHasher|~Resolution.GoldenTests"`; `record`.
- **A20. sql-ddl schema rows** (after A19 and 3.2's A8, which resolves `database.migrations`; before 3.2's A16, which
  calls `_ledger.scriban`). Files: `packs/sql-ddl/_ledger.scriban`, `schema.scriban`, pack README, sample template copy,
  golden `sql-ddl/ledger-facts`. Tests: `LedgerGoldenTests`, the schema `LedgerSqlTests`. Verify: `P
  "~Ledger|~GoldenTests|~DdlCoverage"`; `PG P "~LedgerSqlTests"`. `MAQUETTISTE_UPDATE_GOLDEN=1` only to create the new
  folder.
- **A21. CLI and MCP.** Files: `ModelStore` method and `LedgerDdlAnswer` (a new `ModelStore.Ledger.cs`),
  `Cli/Commands/MigrationCommand.cs` (the `ledger` verb, in the file A13 creates), `CommandLine.cs`, usage in
  `CliApp.cs`, `Agent/tools.json`, `AgentTools.cs`, `AgentToolsTests`, `docs/mcp.md`, `README.md`, `McpSurfaceTests`,
  `McpReadTests`. Tests: `MigrationLedgerCommandTests`, the MCP test, the pins. Verify: `E "~AgentToolsTests"`; `C
  "~MigrationLedger|~McpSurfaceTests|~McpReadTests"`.
- **A22. Contract docs and review note.** Files: `docs/ledger.md`, `mkdocs.yml`, `docs/user-guide.md`,
  `docs/engineering/engine-design.md`, `docs/guides/generate-code.md`, the modeling skill, `docs/releases.md`, the
  `tmp/` note. Test: `EngineBoundaryTests`. Verify: `E "~EngineBoundary"`; `C "~SkillRefreshTests|~AgentSetupTests"`.
- **A23. Freeze version 1** (the last task of phase 1, after the review answers). Files:
  `Engine/Ledger/ledger.postgresql.sql` (review changes, additive only; an answer that needs more is the owner's call
  before this task), `schemas/v1/ledger.json` and copy, `docs/ledger.md` (status "Frozen, version 1", the review
  outcome), `docs/releases.md`, `LedgerContractTests`. Test: `Version_1_ddl_text_and_version_are_pinned` (asserts
  `LedgerContract.Version == 1` and the SHA-256 of the resource text; its message says a change needs version 2 and
  a conversion). Verify: `E "~LedgerContract|~SchemaConsistency"`; `PG P "~LedgerSqlTests"`;
  `C "~MigrationLedger"`.

### 3.4 Small physical gaps

**Decisions taken here**

- Index `nulls` follows the query precedent: optional `first | last`, no default, as an `IndexNulls?` enum shaped like
  `QueryNulls` (a query-named type reads wrong on an index). PostgreSQL only, MQ4056 elsewhere. The resolver drops a
  value equal to PostgreSQL's default for the direction (last ascending, first descending), so DDL, snapshot and index
  key change only when the order does.
- `range` and `multirange` are column-only keywords in a new `common.json#/$defs/columnType` (`builtinType` plus the
  two): scope 6.8 asks for column types, and attribute types drive code types. `subtype` is one of the six keywords
  with a built-in PostgreSQL range: `int32`, `int64`, `decimal`, `date`, `datetime`, `datetimeoffset`. Custom ranges
  stay database types (`typeKind: range`) named by `nativeType`.
- Elsewhere a range column is stored as the dialect's `text` type (its text form) with MQ4056; `typeMaps.<dialect>`
  may map `range:<subtype>` otherwise. Range columns take no facets: length, precision or scale is MQ3013 through the
  existing `CheckFacets`, and a missing or misplaced `subtype` extends MQ4057, so no new id.
- Code generation: csharp-dapper maps both keywords to `string`, the text form, as it holds JSON as text. On
  PostgreSQL its repositories read `<column>::text` and write `CAST(@p AS <native type>)`; `repository.scriban` builds
  their select, insert and update lists. Bound repositories and query classes take their SQL from the engine helpers
  `binding_sql` and `query_sql` (`Rendering/BindingSql.cs`, `Rendering/QuerySql.cs`), which serve every pack and the
  `preview_binding_sql` and `preview_query_sql` tools, and a cast to text there is a pack's choice. So they get no
  casts in phase 1, and the csharp-dapper README says a binding or query over a range column needs them. Follow-ups:
  casts for bindings and queries, typed C# ranges.
- SQL objects take `environments` (free names, empty means every environment) and `apply: converge | once`, default
  `converge`: that is today's behaviour, and the seeds' default `once` would quietly change every existing object.
  Scripts follow the seed precedent:
  - Objects without environments run where they run today (`schema.sql`, migrations, per-object files), as `seed.sql`
    holds the rows of every environment.
  - Objects with environments run only in a new `<db>/objects.<env>.sql` (unit `object-environments`), one per name in
    the pack's `environments` parameter and none when nothing matches, as `seed.<env>.sql` does. It runs on every
    deploy, after `schema.sql` or the migrations and before the seed scripts, as the seed scripts do.
  - So a converge body there must be safe to run again (`CREATE OR REPLACE`, `IF NOT EXISTS`, or `DROP ... IF EXISTS`
    first). The script's header and the user guide say so, as the seed scripts' header does ("it can run again"); no
    SQL is parsed. A changed environment object takes effect at the next run of its script. A removed one stays in the
    database until it is dropped by hand (reverts are 6.3).
  - A run-once object runs inside a ledger guard and records itself (3.3), so it is safe in every script. A changed
    run-once object does not run again, as a once-seed row is never updated.
- `range_using` is wired by the task that creates it: A25 runs after 3.2's A16 and adds the call to A16's fact step,
  as A28 adds the rekey call, so milestone A's first priority (facts and `validate --against`) waits for neither.
- The object's snapshot definition text gains `\nenvironments: a,b` and `\napply: once` only when not default, so
  existing physical snapshots stay byte-identical.
- (A25) `range_using` takes the two sides, not the column change: `range_using(from, to, col)`, each side a keyword and
  a native type. The fact step's own steps (`own_step`) have no column change, only the fact's `from` and `to`, so one
  function serves the diff's retype (sides from the change and the column, `range_side_before`) and a cast fact. A cast
  fact between range types that has no cast (a multirange to a range) is not usable (`fact_usable`): no row is written,
  as for a fact without its dialect's text, and the migration writes a TODO line that names the fact
  (`fact_range_todo`) in place of the diff's, leaving the column as it is. `sqlite_rebuild_reason` reads a change that
  keeps the native type (`range_same_native`) as `column_change_statements` does, so it rebuilds no table for it.
- (A25) The old-idiom skip (`range_same_native`) holds on every dialect, and also covers a range column's subtype change
  with an unchanged native type: elsewhere a range column is text, so a subtype change alone changes nothing, and Oracle
  refuses `MODIFY` from a LOB to a LOB. `migration.scriban` does not drop and create the views again for such a change.
- (A25) A range column outside PostgreSQL is MQ4056 only when it is stored as text: not when the column names its
  `nativeType` or `typeMaps.<dialect>` maps `range:<subtype>`. MySQL's key prefix length rule counts a range column as
  text.
- (A27) `CaptureObject` writes the two lines after the phase line and before `body:`, so the free-text body stays last,
  as in a routine's definition text; default values write neither, so existing snapshots stay byte-identical. MQ4069 is
  reported at `/dependsOn/<i>`, MQ4070 at `/phase` and the run-once MQ4056 at `/apply`.
- (A27) The `environments:` line lists the environments sorted ordinal, so listing them in another order changes no
  definition and writes no migration; `RSqlObject.Environments` keeps the file's order.
- (A28) A `rekey` key of kind `sql-object` was MQ4106 (`MigrationRules.RekeyKinds` held table, column and sequence), so
  `ledger_rekey_rows` could never see one. MQ4106 now takes `sql-object`; `FactKeys.RekeyMap` moves such keys,
  `FactKeys.SqlObjectMoves` lists them, `BaselineCheck.ToTodayKeys` moves the baseline's object key (unless the new key
  is there already) so the diff sees the same object and the migration holds only the `UPDATE` and the fact row (no drop
  TODO, no guard), and fold's `Compose` carries them. Fold's `TablesOf` and the classifier's `CurrentObjects` and
  `RekeyObjects` count such a key as the SQL object itself, never as a table. Test: `SqlObjectRekeyTests` (engine).
- (A28) sql-ddl helpers: `object_in_schema`, `object_left_out` (run-once outside PostgreSQL: no script runs it, its
  object script says so) and `object_writes_ledger` in `_objects.scriban`; `object_run_sql(o, db)` writes the guard, the
  left-out comment or `object_sql`. `object_counts` counts the objects the schema script runs. `ledger_header(facts,
  once, what)` writes the transaction line, unchanged for facts alone; `schema.sql` and a migration that hold a run-once
  object run in one transaction even without facts (the migration counts them in `mq_ledger_state.once`).
  `objects.<env>.sql` has no `BEGIN`/`COMMIT`: each guard is one statement and the script can run again; it writes
  `ledger_prelude` only when it holds a run-once object, and orders phase `before` then `after`, each in dependency
  order. `reapplied_objects` gains a `notes` list for its run-once TODOs. A changed object with environments and
  run-once gets both lines; a changed left-out one gets a TODO saying it is not created. SQLite's rebuild recreates only
  triggers the migrations run. The per-object script of a run-once object writes `ledger_prelude` first.
  `docs/ledger.md` now describes the object rows.
- (A28) An object that converged before and runs once now has no object row, so its guard runs it one more time in
  every database. `object_drop_notes` tells it by the old definition text (`object_was_converge`: no `apply: once` line
  ahead of the body) and writes, instead of the run-once comment, `-- TODO: SQL object <name> changed to run once and
  the ledger has no row for it yet, so it runs one more time in every database, also where it exists already; drop it
  by hand before it runs, or make its statements safe to run again.`. Test: `SqlObjectEnvironmentTests`.

**Model and schema**

`common.json` adds `$defs/columnType` (`anyOf` `builtinType` and `enum ["range","multirange"]`) and
`$defs/rangeSubtype` (the six keywords). `table.json` column (x-order: `subtype` after `type`), index column (x-order
`column, expression, operatorClass, descending, nulls, length`) and `sql-object.json` (x-order `..., objectKind,
phase, environments, apply, dependsOn, body, ...`):

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| column `type` | `columnType` | | Was `builtinType`. |
| column `subtype` | `rangeSubtype` | | Range and multirange columns only (MQ4057); multirange needs PostgreSQL 14 or later. |
| index column `nulls` | enum `first`, `last` | | Where nulls sort: PostgreSQL NULLS FIRST or LAST; MQ4056 elsewhere. |
| `environments` | array of `tagLabel`, unique | `[]` | Empty means every environment. Otherwise only `objects.<env>.sql` runs it, on every deploy, so a converge body must be safe to run again. |
| `apply` | enum `converge`, `once` | `converge` | `once` runs one time per database and records itself in the ledger (PostgreSQL; MQ4056 elsewhere). |

`snapshot.json`: column `type` accepts `columnType`; column `subtype` and index column `nulls`, written only when set.
`Resolution/Dialects/postgresql.json` gains `range:<subtype>` and `multirange:<subtype>` (`int4range`, `int8range`,
`numrange`, `daterange`, `tsrange`, `tstzrange` and the `...multirange` forms). Changed schemas go to the sample copy.

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
public static class BuiltinTypes    // additions
{
    public static IReadOnlyList<string> ColumnOnly { get; }     // "range", "multirange"
    public static IReadOnlyList<string> RangeSubtypes { get; }  // int32, int64, decimal, date, datetime, datetimeoffset
    public static bool IsColumnType(string? keyword);           // IsBuiltin or ColumnOnly
    public static bool IsRange(string? keyword);
}
public string? Subtype { get; init; }                              // on Column
public IndexNulls? Nulls { get; init; }                            // on IndexColumn
public IReadOnlyList<string> Environments { get; init; } = [];     // on SqlObject
public SqlObjectApply Apply { get; init; } = SqlObjectApply.Converge; // on SqlObject
public string? Subtype { get; init; }                              // on SnapshotColumn
public string? Nulls { get; init; }                                // on SnapshotIndexColumn
[JsonConverter(typeof(JsonStringEnumConverter<IndexNulls>))]
public enum IndexNulls { [JsonStringEnumMemberName("first")] First, [JsonStringEnumMemberName("last")] Last }
[JsonConverter(typeof(JsonStringEnumConverter<SqlObjectApply>))]
public enum SqlObjectApply { [JsonStringEnumMemberName("converge")] Converge, [JsonStringEnumMemberName("once")] Once }
namespace Maquettiste.Engine.Resolution;
public string? Subtype { get; internal set; }                      // on RColumn
public string? Nulls { get; internal set; }                        // on RIndexColumn: PostgreSQL, non-default only
public IReadOnlyList<string> Environments { get; internal set; } = []; // on RSqlObject
public string Apply { get; internal set; } = "converge";           // on RSqlObject
public string ContentHash { get; internal set; } = "";             // on RSqlObject: ContentHash.Of(Body)
```

**Engine behaviour**

- Nulls: `IndexColumnSpec` gains `string? Nulls` (built in `DatabaseRun.cs:276-278`, projected in
  `DatabaseRun.Finish.cs:97-118`); `SnapshotCapture.cs:169-172` captures it, `IndexKey` appends ` nulls first|last`,
  the differ's `IndexColumns` compares it; `index_parts` (`_sql.scriban:512-527`) writes ` NULLS FIRST|LAST` on
  PostgreSQL; MQ4056 joins the index-column loop of `PhysicalRules.CheckDialectFeatures`.
- Range: `BuildDesignedTable` keeps `Type`, sets `Subtype` and renders map key `<type>:<subtype>` (new internal
  `DialectTypeMaps.RangeKey`), falling back to the map's `text` entry. `ApplyFacetDefaults` skips range columns (no
  length 255). `CheckColumnValues` accepts `IsColumnType`; `SnapshotCapture` captures `Subtype` and the differ reports
  it; `subtype` joins `$retype` in `_migration.scriban`. `TableSeedRules.Fit` and `AttributeRules.Fits` take range
  values as strings.
- Range retypes on PostgreSQL take their `USING` from a new `range_using(from, to, col)` in `_migration.scriban` (each
  side a keyword and a native type):

  | From | To | Statement |
  | --- | --- | --- |
  | range | range of another subtype | `USING <column>::text::<native>`: no direct cast exists, the text forms match |
  | multirange | multirange of another subtype | `USING <column>::text::<native>`, for the same reason |
  | range | multirange of the same subtype | `USING <column>::<native>`, PostgreSQL's own cast |
  | multirange | range | no ALTER: a TODO naming the conversion a change-type fact must give (no cast exists, and `range_merge` keeps only the outer bounds) |
  | range | multirange of another subtype | no ALTER: a TODO, as above |

  A change between a range keyword and another keyword keeps today's `USING <column>::<native>`. With 3.2 every one
  of these changes needs a `change-type` fact, since the widening table does not know range types; a fact's
  `cast: true` on a range column renders through the same `range_using`; A25 adds that call to the fact step A16
  writes.
- The old idiom: moving between `string` with `nativeType: tstzrange` and `range` with subtype `datetimeoffset` keeps
  the effective native type, but the differ still reports `type`, `length` (255 to none) and `subtype`, because the
  snapshot holds them (`SnapshotCapture.cs:198-206`). The template skips it, not the differ. On every dialect, a column
  change with no `nativeType` change writes no ALTER and no type TODO when its other retype changes are only `type`,
  `length`, `precision`, `scale` or `subtype` and `range` or `multirange` is on one side of `type` (or the column is a
  range column, for a subtype change alone). 3.2's classifier
  compares effective native types, so it records nothing either. The condition names a range keyword, so no existing
  golden changes.
- csharp-dapper: `types/csharp.json` gains `"range": "string"` and `"multirange": "string"`; `row_type` and
  `bind_row_type` (`_dapper.scriban`) hold both as text, as they hold JSON. In `repository.scriban`, on PostgreSQL, the
  select list writes `<column>::text AS <property>` and the insert values and update sets write
  `CAST(@<property> AS <native>)` for range and multirange columns. `_binding.scriban` and `query.scriban` do not
  change (see Decisions).
- SQL objects: `DatabaseRun.Objects.cs:148-166` fills `Environments`, `Apply` and `ContentHash`; `CaptureObject` adds
  the non-default fields after the phase line. In sql-ddl, `objects_in_phase` and `object_counts` skip objects with
  environments, and `object_change_statements` writes no body for them. `object_drop_notes` becomes
  `object_drop_notes(db, diff, out)` so it can find a changed object's current environments. For a changed object with
  environments it writes, instead of today's "drop it by hand before its new statements below run", `-- TODO: SQL object
  <name> changed and runs in environments a, b (objects.<env>.sql applies it there); drop it by hand where it should no
  longer run.`. For a changed run-once object it writes `-- Run-once SQL object <name> changed; it does not run again
  where the ledger records it.` (a TODO instead when it converged before, see Decisions). A dropped object leaves no
  `RSqlObject`, and `ObjectChange` carries no environments, so drops keep today's TODO ("left the model; drop it by
  hand"). `reapplied_objects` skips run-once objects with a TODO naming them. `objects.<env>.sql`'s header reads: "Run
  it after schema.sql or the migrations on every deploy; it can run again, so every statement in it must be safe to run
  again."
- `ledger_once_sql(o, db)` in `_ledger.scriban` writes `DO $once$ BEGIN IF NOT EXISTS (SELECT 1 FROM
  <schema>.applied_object WHERE element_id = '<id>') THEN EXECUTE '<body, quotes doubled>'; INSERT INTO
  <schema>.applied_object (element_id, kind, schema_name, name, content_hash) VALUES (...); END IF; END $once$;`, the
  tag being the first of `$once$`, `$once1$`, ... the body lacks. Every script holding a run-once object (`schema.sql`,
  a migration, `objects.<env>.sql`, the per-object file) writes `ledger_prelude` first. A body that cannot run inside
  a function (transaction control, `CONCURRENTLY`) cannot be run-once; the user guide says so, and no SQL is parsed.
- `ledger_rekey_rows(db, fact)` in `_ledger.scriban` writes, for each `sql-object` key of a `rekey` fact, `UPDATE
  <schema>.applied_object SET element_id = '<to>' WHERE element_id = '<from>' AND NOT EXISTS (SELECT 1 FROM
  <schema>.applied_object WHERE element_id = '<to>');`. A28 adds its call to the rekey step 3.2's A16 writes
  (today a rekey writes no DDL), so a run-once object whose id moved is not run again. That is the rekey end state
  3.2 defines.
- MQ4069 is an inclusion test: empty means every environment, and the dependency's environments must cover every
  environment the dependent runs in. The precedent is `MissingIn` (`TableSeedRules.cs:293-303`, MQ7111's coverage),
  not the seeds' `Overlap` (`:218-220`), which only asks whether two seeds load together somewhere.
  `DatabaseObjectRules.CheckDependsOn` gains the dependent's environments and reports MQ4069 at `/dependsOn/<i>`. Its
  three callers pass them: views (`PhysicalRules.cs:828`) and routines (`DatabaseObjectRules.cs:48`) pass `[]`, since
  they run everywhere, and SQL objects (`:132`) pass their own. `CheckSqlObject` also reports MQ4070 and the run-once
  MQ4056.

**CLI and MCP**

None new: `validate`, `save_element` and the generic JSON form cover the keys. Editor follow-ups: a nulls control in
the index column editor (`tableParts.ts`), a subtype picker beside the column type, environment and apply fields on
the SQL object form.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4069 | E | A view, routine or SQL object depends on a SQL object that does not run in every environment it runs in: the dependency's environments must cover the dependent's, and empty means every environment. | |
| MQ4070 | W | A SQL object with environments has phase before: environment scripts run after the schema script and the migrations, so its phase orders it only within its script. | |
| MQ4057 | E | Extended: subtype on a column that is not range or multirange, or a range or multirange column without one. | |
| MQ4056 | W | Extended: index column nulls, a range or multirange column (stored as text) and a run-once SQL object (left out), each outside PostgreSQL. | |

**Docs**

`docs/user-guide.md` (index columns, column types, SQL objects: environment scripts run on every deploy, so their
bodies must be safe to run again), `packs/sql-ddl/README.md` (`object-environments`, run-once guards, range DDL and
casts), `packs/csharp-dapper/README.md` (range columns as text; bindings and queries over them need casts, a
follow-up), schema descriptions, the modeling skill (Conventions worth knowing) and `docs/releases.md`.

**Tests**

Scope 6.8 has no done-when line; the three tests marked acceptance stand for its three bullets.

- `IndexNullsTests`: `Nulls_reach_the_resolved_model_snapshot_key_and_postgresql_ddl` (acceptance),
  `Nulls_equal_to_the_directions_default_change_nothing`, `Nulls_outside_postgresql_is_MQ4056_and_left_out`.
- `RangeColumnDdlTests.Range_columns_create_and_migrate_on_postgresql` (acceptance; golden `sql-ddl/range-columns`,
  server run when the container is set; covers range to range of another subtype and range to multirange of the same
  subtype), `Multirange_to_range_is_a_todo_without_an_alter`,
  `The_old_string_idiom_and_a_range_column_with_one_native_type_write_no_alter`. `RangeColumnTests`:
  `Range_and_multirange_resolve_to_postgresql_builtin_types_without_facet_defaults`,
  `A_missing_or_misplaced_subtype_is_MQ4057_and_facets_are_MQ3013`, `Range_outside_postgresql_is_text_and_MQ4056`.
  `SchemaDifferTests.A_subtype_change_is_a_column_change`.
  `MigrationFactTests.A_cast_fact_on_a_range_column_renders_through_range_using`.
  `CsharpRangeColumnTests.Repositories_read_and_write_range_columns_as_text_with_postgresql_casts` (golden
  `csharp-dapper/range-columns`).
- `LedgerSqlTests.A_run_once_object_runs_once_and_records_itself` (acceptance; a two-statement body run twice),
  `LedgerSqlTests.A_rekey_fact_moves_the_row_of_a_run_once_object`.
  `SqlObjectCaptureTests.Default_environments_and_apply_keep_the_definition_text_byte_identical`.
  `SqlObjectEnvironmentTests` (golden `sql-ddl/object-environments`):
  `Environment_objects_go_only_to_their_environment_script`, `Run_once_objects_are_guarded_by_the_ledger_everywhere`,
  `Default_environments_and_apply_keep_goldens_byte_identical`,
  `The_environment_script_runs_twice_with_a_rerunnable_body_and_a_run_once_object` (server run when the container is
  set: the second run succeeds and the run-once body ran once),
  `A_changed_environment_object_gets_its_todo_and_a_dropped_one_keeps_todays`.
  `SqlObjectRuleTests.MQ4069_MQ4070_and_run_once_MQ4056` (theory rows of mistake, rule, pointer; MQ4069 rows: a view
  on a `[dev]` object, a routine on one, a `[dev, test]` object on a `[dev]` one, and a passing `[dev]` object on a
  `[dev, test]` one).

**Tasks**

- **A24. Index nulls.** Files: `Model/Physical.cs`, `table.json`, `snapshot.json` (and copies), `TableBuild.cs`,
  `DatabaseRun*.cs`, `ResolvedPhysical.cs`, `PhysicalSnapshot.cs`, `SnapshotCapture.cs`, `SchemaDiffer.cs`,
  `PhysicalRules.cs`, the MQ4056 text, recorded rules, `_sql.scriban` (and copy), user guide. Tests:
  `IndexNullsTests`. Verify: `E "~IndexNulls|~SchemaConsistency|~ResolvedCoverage|~SchemaDiffer"`;
  `P "~GoldenTests|~DdlCoverage"` (no golden changes); `record`; `editor`.
- **A25. Range columns, engine and sql-ddl** (after A16 and A24). Files: `BuiltinTypes.cs`, `Model/Physical.cs`
  (`Column.Subtype`), `Model/PhysicalSnapshot.cs`, `Resolution/ResolvedPhysical.cs` (`RColumn.Subtype`),
  `SchemaDiff/SnapshotCapture.cs`, `common.json`, `table.json`, `snapshot.json` (and copies), `postgresql.json`,
  `DialectTypeMaps.cs`, `DatabaseRun.cs`, `PhysicalRules.cs`, `AttributeRules.cs`, `TableSeedRules.cs`,
  `SchemaDiffer.cs`, `_migration.scriban` (`range_using`, its call from the fact step, the old-idiom skip) and copy, the
  MQ4056 and MQ4057 texts, recorded rules. Tests: `RangeColumnTests`, the differ test, `RangeColumnDdlTests`, the
  `MigrationFactTests` case. Verify: as A24, then `PG P "~RangeColumnDdl|~MigrationFactTests"`.
  `MAQUETTISTE_UPDATE_GOLDEN=1` only for the new folder.
- **A26. Range columns in csharp-dapper.** Files: `types/csharp.json`, `_dapper.scriban`, `repository.scriban`, README,
  sample copies. Test: `CsharpRangeColumnTests`. Verify: `P "~CsharpRangeColumn|~GoldenTests"`.
  `MAQUETTISTE_UPDATE_GOLDEN=1` only for the new folder.
- **A27. SQL object environments and run-once, engine.** Files: `Model/Physical.cs`, `sql-object.json` (and copy),
  `DatabaseRun.Objects.cs`, `ResolvedPhysical.cs`, `SnapshotCapture.cs`, `DatabaseObjectRules.cs` (`CheckDependsOn`,
  `CheckSqlObject`), `PhysicalRules.cs` (the view call), `RuleCatalog.cs` (MQ4069, MQ4070, MQ4056 text, count +2),
  recorded rules. Tests: `SqlObjectRuleTests`, `SqlObjectCaptureTests`. Verify: `E
  "~SqlObject|~CatalogTests|~SchemaConsistency"`; `record`; `editor`.
- **A28. SQL object environments and run-once, sql-ddl** (after A20, A27 and 3.2's A16). Files: `_objects.scriban`,
  `_migration.scriban` (`object_drop_notes(db, ...)`, the rekey call), `migration.scriban`, `schema.scriban` (the
  prelude for run-once objects), `_ledger.scriban` (`ledger_once_sql`, `ledger_rekey_rows`),
  `object-environments.scriban`, `pack.json` (the unit uses `select databases`, as `seed-environments` does; C22
  moves both to `each database`), `sql-object.scriban`, README, sample copies, golden
  `sql-ddl/object-environments`. Tests: `SqlObjectEnvironmentTests`, the run-once and rekey `LedgerSqlTests`.
  Verify: `P "~SqlObjectEnvironment|~Ledger|~GoldenTests|~DdlCoverage"`;
  `PG P "~LedgerSqlTests|~SqlObjectEnvironment"`.
  `MAQUETTISTE_UPDATE_GOLDEN=1` only for the new folder.

### 3.5 Name rules and literal defaults

**Decisions taken here**

- Name rules run in the resolver beside MQ4001 on derived names (`CheckIdentifiers`, `DatabaseRun.Finish.cs`): most
  clashes come from derived names (two indexes on the same columns, an index named like a key), which only the
  resolver knows. The validator sees file names only.
- One length measure, `DialectInfo.IdentifierLength`: UTF-8 bytes on PostgreSQL and Oracle, characters elsewhere, as
  the validator's MQ4001 uses. The resolver's MQ4001 measures UTF-16 length today, against that intent; it moves to
  the same measure and names the unit. Names compare as the database keeps them: cut to the limit in that measure
  (PostgreSQL truncates a longer name to 63 bytes and only raises a NOTICE), then upper-cased invariantly,
  case-insensitive like MQ4002.
- One rule, MQ4068, for every namespace; the namespaces per dialect are data in `DialectInfo`:

| Dialect | Share the schema's object names | Unique per schema on their own | Unique per table |
| --- | --- | --- | --- |
| PostgreSQL | with tables, views and sequences (relations): indexes; primary, unique and exclusion keys (each creates an index) | | primary, unique, foreign, check and exclusion constraints |
| SQL Server | with tables, views, sequences and routines (`sys.objects`): primary, unique, foreign, check and default constraints | | indexes, with the primary and unique keys' indexes |
| MySQL | | foreign keys; checks (two namespaces) | indexes and unique keys (the primary key is always PRIMARY) |
| Oracle | | indexes, with the keys' indexes (one namespace); all constraints (a second namespace, so a key and its index may share a name) | |
| SQLite | with tables and views: indexes | | |

  SQL objects are opaque text, so their names join no namespace; MQ4002 still checks tables, views and sequences
  against each other.

- Defaults (the MQ3019 gaps): a string column's default is checked against its resolved length (`length ??
  DefaultStringLength`). A column with a `nativeType` or a database type is checked only for the kind of JSON value
  its keyword implies, since the native type, not the facets, sets the storage. A `json` column's literal default must
  be a string of valid JSON text. An object or array has no SQL literal (`sql_literal` fails the unit today), and a
  number or boolean renders as a numeric or boolean literal that the database refuses for a `json` column, so all of
  them become MQ3019 pointing to `defaultSql`. A range default must be a string. `defaultSql` stays unchecked until
  `db check` (6.5, phase 3); attribute defaults do not change.
- (A29, A30, as built) MQ3019 on a column default stays at the column's `/default` pointer; its message points to
  `defaultSql`. A primary or unique key holds one name in each namespace it joins (Oracle's index and constraint
  namespaces, SQL Server's schema and table ones), so a key never collides with its own index. A default constraint
  joins SQL Server's schema namespace only when its column sets `defaultName`; the pack's `df_<table>_<column>` is not
  a resolved name.

**Engine behaviour**

- `DatabaseRun.CheckNames()` runs after `CheckIdentifiers` over each database's published tables, views, sequences
  and, on SQL Server, routines. It keeps one key set per namespace instance (database and schema, or table) and visits
  tables in published order (schema, name); within a table: primary key, uniques, foreign keys, checks, exclusions,
  default constraints (SQL Server, their resolved `defaultName`), indexes, in file order. The first holder keeps a key;
  each later holder reports MQ4068 on its table file at its pointer (`/indexes/<i>/name` when named, else
  `/indexes/<i>`, and so on), or on `SourceElementId` without a pointer for an index an attribute's `indexed` flag
  made. The message names both holders, the namespace, and the shared first 63 bytes when truncation caused the
  clash. Views, sequences and SQL Server routines enter the schema sets only as names an index or constraint may not
  take; MQ4002 still checks them against each other.
- `PhysicalRules.CheckColumnValues` gains the `ValidationContext` and the `Database`, to read
  `EffectiveConventions.For` (internal) and the dialect; the kind check is a new internal
  `AttributeRules.FitsKind(keyword, value)`.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ4068 | E | Two index or constraint names collide in a namespace the dialect keeps (index and key names in a PostgreSQL schema with its tables, views and sequences; constraint names in a table; the other dialects' namespaces), compared case-insensitively after cutting each name to the identifier limit. | |
| MQ3019 | E | New text: "Literal default does not match its type: an attribute's (use defaultExpression for now, today, new-uuid or new-ulid) or a column's, checked against its resolved length; a json column takes a string of JSON text, and a column with a nativeType is checked for the kind of value only (use defaultSql otherwise)." | |
| MQ4001 | E | Text unchanged; the resolver now measures in the dialect's unit. | |

**Docs**

`docs/user-guide.md` (name rules with the namespace table; column default rules), the modeling skill (name indexes
that share columns) and `docs/releases.md`.

**Tests**

Scope 6.5 has no phase 1 done-when line; the tests marked acceptance stand for its two bullets.

- `NameRuleTests`: `Two_file_indexes_on_the_same_columns_derive_one_name_and_report_MQ4068` (acceptance; btree and
  gin), `A_unique_key_and_an_index_share_the_postgresql_schema_namespace`,
  `An_index_named_like_a_table_or_view_collides_on_postgresql`,
  `Foreign_key_names_may_repeat_across_tables_on_postgresql_but_not_on_mysql`,
  `Names_compare_after_cutting_to_the_byte_limit` (MQ4001 off, two 70-byte names),
  `Each_dialect_checks_its_own_namespaces` (a row per dialect and namespace, including a SQL Server constraint named
  like a routine and an Oracle key that shares its index's name without a clash),
  `Derived_name_length_counts_utf8_bytes_on_postgresql`.
- `ColumnDefaultTests`: `A_string_default_is_checked_against_the_resolved_length` (acceptance),
  `A_native_type_column_checks_the_kind_of_value_only`, `A_json_default_is_a_string_of_json_text` (rows for an
  object, an array, a number and a boolean), `A_range_default_is_a_string`. The `values` validation fixture gains the
  same cases; its `expected.json` is updated with `MAQUETTISTE_UPDATE_GOLDEN=1`, an intended change that only adds
  MQ3019 rows.

**Tasks**

- **A29. Name rules and one length measure.** Files: `DialectInfo.cs` (namespace table), `DatabaseRun.Finish.cs`
  (`CheckNames`, MQ4001 measure), `RuleCatalog.cs` (MQ4068, count +1), recorded rules, user guide, skill, releases.
  Tests: `NameRuleTests`. Verify: `E "~NameRule|~CatalogTests|~Resolution.GoldenTests"`;
  `P "~GoldenTests|~DdlCoverage|~PostgresFeature"` (no golden changes: shipped fixtures have no clashes); `record`.
- **A30. Literal defaults.** Files: `PhysicalRules.cs`, `AttributeRules.cs` (`FitsKind`), the MQ3019 text,
  `tests/fixtures/validation/values`, user guide, recorded rules. Tests: `ColumnDefaultTests`, the fixture. Verify: `E
  "~ColumnDefault|~ValidationFixtureTests|~ModelValidatorTests"`; `record`.

## 4. Milestone B: links become ids

This milestone makes every link in the model an id. Property values become ids the engine walks (4.1). Stereotypes
gain rules and typed parameters (4.2). Every kind can join a package, and packages declare layers (4.3). Extension
text becomes translatable, and prefixed tags become typed properties (4.4). Every element can say why it exists (4.5).

B1, B2 and B3 run first, in that order: B1 and B3 touch every kind schema. After them the real dependencies are: B4 to
B6 in order; B8 after B3; B9 after B4 and B8; B10 and B11 after B3; B12 after B4; B7 after B6 and C6; B13 after B7.
So B8 to B12 may run before B7. B7 and B13 wait for C6 (section 5.1), which adds
`maquettiste model apply <batch.json> [--dry-run]` and the `apply_batch` `dryRun`: `names-to-ids` and
`tags-to-property` are one-time actions that an agent previews before it applies them, scope 16.2 wants each item in
the CLI and MCP, and this milestone adds no batch surface of its own. Section 6 therefore runs 5.1 inside milestone B.
B14 runs last. The section adds 16 rules, no MCP tool and no CLI verb.

The rule count is pinned (`tests/Maquettiste.Engine.Tests/Model/CatalogTests.cs:46`). Every task that adds a rule owns
`CatalogTests.cs`, raises the pin by the number of rules it adds and runs `E "~CatalogTests"`, so the suite stays green
between tasks: B2 +1, B4 +1, B5 +2, B9 +7, B10 +2, B11 +1, B12 +2.

Until the editor screens follow, the JSON view covers the new keys. Deferred editor work: an `x-ref` picker in
`SchemaForm`, the stereotype form, `hasPackage` for every kind (`inspector/fields.tsx:277`) and `dependsOn` on
packages, "Move to property" on the Tags screen, rationale and status in the common fields, and the MQ2010 quick fix.

No golden is meant to change, so `P "~GoldenTests"` must pass untouched.

Test classes marked "(new)" are created by the task that owns them. The MCP tests go in the existing classes of
`tests/Maquettiste.Cli.Tests/McpServerTests.cs`: `McpSurfaceTests` (`:9`), `McpReadTests` (`:235`) and
`McpWriteTests` (`:340`).

### 4.1 Typed references in property schemas

**Decisions taken here**

- **Homes.** `x-ref` lives in extension `properties`, in stereotype `parameters` (4.2) and in pack `parameterSchema`.
  Project kinds (3.7) adopt it unchanged.
- **Target names.** Targets use index kind names: kind names for elements, `IndexEntry.Kind` for sub-elements
  (`ModelIndexer.cs:63-86`). An enum member is therefore `enum-member`, and `member` means a member of a custom
  element.
- **Reserved kinds.** `operation`, `outcome`, `error`, `member`, `custom` and `custom:<type>` are valid now.
  `custom:<type>` is a custom element of one declared type (scope 3.7), where `<type>` is a key
  (`common.json#/$defs/key`). Their values report MQ2001 ("arrives later") until phase 2 adds those kinds. The format
  does not change then.
- **Values.** A value is a ULID, or ULIDs joined by `.` (the keyed id of inherited members, D33). Anything else is a
  name, reported as MQ2010.
- **`of`.** `"$element"` means a member of the host's top-level element, the element whose file holds the host. Its
  base chain and the stereotypes it carries count too, implied ones included (4.2), because their attributes are its
  inherited and virtual attributes. So a column property may name a sibling column of the same table. An element id
  means "one code of that list": a row whose seed targets that element.
- **One walk.** The indexer and the validator share one schema-driven walk over the hosts `ExtensionSet` checks today
  (`ExtensionRules.cs:106-152`). So MQ2001, `get_references`, delete refusal, `remove-references` and
  `delete-dependents` all see the values in model documents.
- **`optional`.** An optional value is cleared by both resolutions. A required value refuses `remove-references`;
  `delete-dependents` removes the value and keeps its holder. Either resolution refuses (MQ2001, naming the property
  and the schema) when the edited holder would fail a schema that declares the property: its `required` names the
  property, or an array drops below `minItems`. `TryOps` reads only the kind schema
  (`ChangePlanner.Cascade.cs:190-199`), so the `x-ref` path checks those schemas itself.
- **Saves that remove a sub-element.** They clear optional values naming it in every file, the saved one included (the
  scope's own-attribute case). A required value makes the save invalid.
- **Pack parameter values.** They are settings in `maquettiste.json`, not model documents. Validation with packs
  loaded checks them (MQ2001, MQ2002, MQ2010). Deletes, and saves that remove a sub-element, refuse or remove the entry
  in the same change. Two parts of the scope's 3.1 text do not reach them, by decision here. `get_references` stays
  over model documents, because `ReferenceInfo.FromElementId` is an element id. `names-to-ids` does not convert them,
  because a batch writes model documents. The delete plan lists them, the pack load reports them (CLI `validate`, MCP
  `plan` and `get_pack`), and `save_settings` fixes them.
- **One definition of a name.** `names-to-ids` matches top-level kinds through 5.1's `NameIndex` forms, stereotypes by
  key only, as 5.1 registers them; `of` scoping narrows sub-element candidates only. So batches, rules, templates and
  this conversion resolve the same text the same way.
- **Names to ids.** Conversion is the batch operation `names-to-ids`, expanded like `retag`
  (`ModelStore.Schemas.cs:34-38`), so it gets validation, conflicts, all-or-nothing and undo. It cannot go in the
  formatter, which reads one file without a snapshot and changes only bytes (`ModelStore.Format.cs:27`).
- **One value, several schemas (B4).** `PropertySchemaSet.Sites` yields one site per schema, so the validator judges
  each. The index keeps one reference per (pointer, id): `Schema` names the first schema in set order, and `Optional`
  holds only when every schema that types the value says so, so a required declaration still refuses
  `remove-references`.
- **Where MQ2009 is reported (B4).** The set parses keywords once per set of files; `ExtensionSet` reports its findings
  with MQ5004 in a whole-model validation, plus an `of` that names no element, which needs the snapshot. The set also
  compiles each schema once, and `ExtensionSet` reuses it instead of compiling per run.
- **Keywords that compile (B5).** The default JSON Schema dialect refuses unknown keywords, so every extension with an
  `x-ref` failed to compile (MQ5004). `PropertySchemaSource` compiles with that dialect plus `x-ref` and
  `x-translatable` as keywords the evaluation ignores; any other unknown keyword is still MQ5004.
- **Keyed values (B5).** Every id of a value joined by `.` must exist (MQ2001 per missing id); the kind (MQ2002) and the
  scope (MQ2011) are judged on the last id, the member the value names. A finding two schemas share at one pointer is
  reported once.
- **One database (B5).** For `sameDatabase`, the holder's database is the database itself, the database of a physical
  object, mapping or migration fact, or the one database all of an entity's bindings name; any other holder has no
  single database.
- **Pack parameter checks (B5).** `UnitRules.Parameters` gains the snapshot and the `pack.json` path, both optional. It
  also reports MQ2011 for a value outside the list a fixed `of` names, and MQ2009 for `of: "$element"` and
  `sameDatabase` in a `parameterSchema`, since no element holds a parameter.
- **Pack value removals (B6).** A removal that leaves the parameter's schema failing (a required key, an array below
  `minItems`) refuses in every resolution and in saves, as for model documents, not only under `delete-dependents`. A
  plain delete's pack refusals fail the change before the reference check, so its plan lists them instead of the
  referrers. The planner makes a report for them in that resolution, so `GetDeletePlanAsync` lists owned seeds by
  resolution. Database conventions and pack values share one settings edit, so both land in one write of
  `maquettiste.json`.
- **`of` warnings (B6).** The delete plan warns, through `DeletePlanWarning`, for each `x-ref` of an extension file or a
  pack's `parameterSchema` whose `of` names a deleted element.
- **The `names-to-ids` resolver (B7).** It lives in `ModelStore.NamesToIds.cs` as `XRefNames`, which `tags-to-property`
  reuses. Every allowed kind with names is matched through its `NameIndex` forms, sub-element kinds included (an
  attribute as `Invoice.total`), and a value that starts with `@` is read as a qualified name of one of the kinds. The
  `of: "$element"` scope adds a sub-element's own name (the last segment of its forms), `of: <id>` adds
  `ReferenceDataIndex.RowOf`, and every candidate is narrowed by the scope, `sameDatabase` included (through
  `BuiltinRules.XRefDatabaseOf`, now internal). A value several schemas type must find one id in the candidates of each.
  An `elementIds` item that is not an element refuses the operation with MQ1002 at the item, as `retag` refuses a
  `package` that is not a domain.
- **Patterns beside a typed `additionalProperties`.** The link walk compiles each `patternProperties` pattern there for
  the non-backtracking engine with no timeout, so which members an `x-ref` types never depends on the machine's load. A
  pattern that engine cannot run (a backreference, a lookaround, an atomic group or a conditional) is MQ2009 at the
  pattern, and the keyword then types no member until the pattern changes.
- **MQ2010's message.** On a model value it names the batch operation `names-to-ids`; on a pack parameter it does not,
  since the operation writes model documents only. The catalog `QuickFix` stays null.

**Model and schema**

The keyword is documented as `common.json#/$defs/xRef`. The engine parses it and reports MQ2009.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `kind` | string or non-empty string array | required | Kind names, sub-element index kinds, or a reserved kind (`custom:<type>` included) |
| `of` | string | absent | `"$element"` or an element id; sub-element kinds only |
| `sameDatabase` | boolean | `false` | Target in the host's database; physical targets only |
| `optional` | boolean | `false` | `remove-references` may clear it |

- **Placement.** `x-ref` sits on a `"type": "string"` schema or on an array's `items`. The walker follows
  `properties`, `items` and `additionalProperties`. Anywhere else it is MQ2009.
- **Other files.** `extension.json` and `pack.json` change only their descriptions. `batch.json` adds the op
  `names-to-ids` and the key `elementIds` (a unique array of 5.1's `#/$defs/idOrName`, after `attributes` in
  `x-order`, so a name passes the batch schema and `BatchNames` resolves it). The key is not `ids`, because
  5.1 gives the batch result an `ids` member (`BatchIds`).

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
public sealed record ReferenceInfo(string FromElementId, string FromId, string JsonPointer, string Field, string ToId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Owning = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Optional = false,  // x-ref only
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Schema = null);
    // Schema: "extension:<name>" or "stereotype:<key>"
namespace Maquettiste.Engine;
public enum BatchOp { /* existing */ [JsonStringEnumMemberName("names-to-ids")] NamesToIds,          // B7
                      [JsonStringEnumMemberName("tags-to-property")] TagsToProperty }                // B13 (4.4)
// The whole record after this milestone and 5.1, stated only here. Parameters before Tags are unchanged.
public sealed record BatchOperation(
    BatchOp Op, string? Id, string? ExpectedHash, JsonElement? Element, string? Locale = null, string? Field = null,
    JsonElement? Value = null, string? Schema = null, string? Name = null, string? Target = null,
    string? Default = null, DeleteResolution? Resolution = null, string? Database = null,
    IReadOnlyList<string>? Entities = null, IReadOnlyList<string>? Tables = null, string? Package = null,
    IReadOnlyDictionary<string, string>? ExpectedHashes = null, IReadOnlyList<string>? Columns = null,
    IReadOnlyList<string>? Attributes = null, IReadOnlyList<string>? Tags = null,
    string? As = null,                         // C1 (5.1)
    IReadOnlyList<string>? ElementIds = null,  // B7: names-to-ids and tags-to-property act only on these (absent: all)
    string? Prefix = null);                    // B13: tags-to-property
```

These internal names are fixed. `PropertySchemaSet`, `XRef` and `PropertyHost` live in `Model/PropertySchemas.cs`.
`PackParameterRefs` lives in `Loading/PackParameterRefs.cs`.

**Engine behaviour**

1. **Schema set.** `ModelSnapshot.CreateAfter` (`:105`) builds `PropertySchemaSet` from the extensions (by path) and
   then each effective stereotype's `parameters`. For each stereotype key it takes the ordinally first path. Each
   source holds an `x-ref` tree, its translatable names and a compiled schema. `Hash` covers the ordinal
   `(path, hash)` pairs of both file sets. When the hash is unchanged, the previous set is reused.
2. **Hosts.** `PropertyHost.Of(Element)` yields `(pointer, id, kindName, stereotypes, properties)`. Hosts are the
   element, attributes, enum members, table columns, and process states, transitions and events: the same set
   `ExtensionSet.Check` visits. `ExtensionSet.Check` switches to it, and both walks, localization and the batches
   share it.
3. **Index.** `ModelIndexer.Build(documents, schemas, previous, parallelism, ct)` adds a fourth block to
   `WalkReferenceData` (`:593`). Each id-shaped value at an `x-ref` site of a host's own `properties` becomes a
   `ReferenceInfo`: the host is `FromId`, the top-level property is `Field`, and `Optional` and `Schema` are set.
   Values at other sites stay data. A stereotype's `defaultProperties` are walked against its own `parameters` and
   against the extensions that name its key. `of` ids in `parameters` are indexed under the field `of`.
4. **Staleness.** `Result` gains `PropertySchemasHash`. `TryPatch` does a full build when that hash differs, including
   at the early return (`:253-254`); today that return keeps stale walks after an extension-only change.
5. **Validation.** `ReferenceWalker.Walk(Element, PropertySchemaSet)` adds `x-ref` sites. Each carries a synthesized
   `ElementRefAttribute` (like `StorageTarget`, `ReferenceWalker.cs:31`) and its `XRef`. `CheckReferences`
   (`BuiltinRules.cs:105-136`) reports MQ2010, then MQ2001 and MQ2002, then MQ2011.
6. **Scope.** For `of: "$element"`, the target entry's `OwnerId` must be in the host's set: its top-level element's
   id, that element's base chain, and the ids of the stereotypes it carries. `IndexEntry.OwnerId` is always a
   top-level element (`Documents.cs:17`), so a sub-element host compares through its top-level element. B5 reads the
   stereotypes through the helper `ValidationContext.VirtualAttributes` uses, so B8's switch to the effective set
   covers implied ones. For `of: <id>`, the target must be a row whose seed targets that id. MQ2011 also covers
   `sameDatabase` on a host with no single database, and a stereotype default under `of: "$element"` or
   `sameDatabase`, because a default serves many hosts. A fixed `of: <id>` is fine in a default.
7. **Deletes and saves.** `ChangePlanner.Resolve` (`ChangePlanner.Cascade.cs:67`) handles references that carry a
   `Schema` before its generic path. It applies the edit, then checks the holder's properties against each schema
   source in `PropertySchemaSet` that declares the property, and refuses when one fails (the `optional` decision).
   Optional removals are listed under `clears`. `ClearOptionalReferences` runs beside `DropSeedColumns`
   (`ChangePlanner.cs:290-305`).
8. **Pack parameters.** Validation: `UnitRules.Parameters` gains the snapshot. It reports MQ2001, MQ2002 or MQ2010 at
   `/packs/<name>/parameters/<key>` in `maquettiste.json`, and MQ2009 in `pack.json`. These findings come with the
   pack load, as MQ6023 does today: CLI `validate` (`ValidateCommand.cs:37`), MCP `plan` and `get_pack` report them.
   MCP `validate` does not load packs (`AgentTools.Handlers.cs:359-366`) and does not start to.
   Deletes and removals run in `ChangePlanner`, where real deletes and settings edits happen, not in the read-only
   plan. `PackParameterRefs` holds, for every local pack, the `x-ref` sites of its `parameterSchema` (parsed with the
   `XRef` reader) and the project's values for them. It reads only `templates/<pack>/pack.json`, as
   `PackFiltersNaming` does (`ModelStore.DeletePlan.cs:105-150`), never templates. `ModelStore` passes it to the
   planner as a `Lazy<PackParameterRefs>` (`ModelStore.cs:729`), so a batch that deletes and removes nothing reads no
   manifest. A pack value naming a deleted element or sub-element, or a sub-element a save removes:
   - refuse, or a required value under `remove-references`: the change is invalid, with an MQ2001 diagnostic in
     `maquettiste.json` at the value's pointer and no element id;
   - an optional value under either resolution, or a required one under `delete-dependents`: the entry (or its array
     item) is removed in the same change. The edit is staged in `_writes` and `_candidateSettings`, as
     `RemoveDatabaseConventions` does (`ChangePlanner.Cascade.cs:342`), and listed as a `DeletePlanSetting`;
   - in every resolution and in saves, a removal that leaves the schema failing refuses instead, naming the key: a key
     that `parameterSchema.required` names, or an array that drops below `minItems`.

   The planner adds each refusal to its report with `id` null, in every resolution, because the plan drops the MQ2001
   diagnostics it reads from outcomes (`ModelStore.DeletePlan.cs:53`). `GetDeletePlanAsync` only reports. An `of` id
   inside an extension or pack file only warns in the plan, and becomes MQ2009 once the element is gone.
9. **`names-to-ids`.** The operation runs over `elementIds`, or over every document with `x-ref` sources (an explicit
   whole-model write, like `retag`). Each non-id is matched against the in-scope candidates of its allowed kinds:
   top-level kinds through 5.1's `NameIndex` forms (stereotypes by key only); within the `of` scope, sub-elements on
   `name`, columns on their physical name and rows on their code (`ReferenceDataIndex.RowOf`). One match replaces the
   value. No match, or several, refuses the batch with one MQ2010 per value, at the value's file and pointer. B7 changes
   the batch dispatcher (`ModelStore.Schemas.cs:34-43`) so that an expanded operation returns a list of diagnostics
   instead of one rule and one message; the existing branches wrap their message as today. When there is nothing to
   convert, nothing is saved. Stereotype `defaultProperties` are converted too. Writes take the store's atomic model
   write path, and the dry run is `PreviewBatchAsync`. The items of `elementIds` are id fields in 5.1's sense, so they
   take names: B7 adds them to `BatchNames`.

**CLI and MCP**

No tool or verb is added. `apply_batch` describes `{"op":"names-to-ids","elementIds":[...]}`. Its dry run is the
`dryRun` of C6, and the CLI form is `maquettiste model apply <batch.json> --dry-run` from the same task, which
B7 waits for. `get_references` rows carry `optional` and `schema`, and its `tools.json` description says so.
`delete_element` states the `x-ref` and pack parameter rules. `POST /api/model/batch` accepts the op; `openapi.yaml`
updates `BatchOperation` and `ReferenceInfo`.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ2009 | E | An `x-ref` is malformed: unknown `kind` or key, a `custom:<type>` whose type is not a key, `of` or `sameDatabase` where they do not fit, an `of` naming no element, not on a string, or under an applicator the walker does not follow. | Fix the schema |
| MQ2010 | E | A value an `x-ref` types is a name or other text, not an id. | Batch `names-to-ids` (catalog `QuickFix` null for now) |
| MQ2011 | E | An `x-ref` value is out of scope: a member of another element, a row of another list, another database, a holder with no single database, or a stereotype default under `of: "$element"` or `sameDatabase`. | Pick a target in scope |

**Docs**

`docs/user-guide.md` › "Custom properties" gains "Typed links (`x-ref`)" (B6, with a `names-to-ids` paragraph from
B7). `docs/mcp.md` rows: `get_references` (B4), `delete_element` (B6), `apply_batch` (B7). `schemas/v1` with its
copies; `openapi.yaml` (`ReferenceInfo` in B4, `BatchOperation` in B7).

**Tests**

- `PropertySchemaSetTests.Xref_keywords_parse_and_malformed_ones_are_MQ2009` (new; a theory, one row per branch,
  `custom:<type>` included).
- `ModelSnapshotIndexTests.Xref_values_are_indexed_with_schema_and_optional`.
- `ModelIndexerPatchTests.Changing_only_an_extension_rebuilds_the_index`. The random-edit test (`:19`) now edits
  extensions too.
- `McpReadTests.Get_references_shows_an_xref_with_schema_and_optional`.
- `XrefRuleTests` (new): `A_name_is_MQ2010`, `Dangling_is_MQ2001_and_wrong_kind_MQ2002`, `Scopes_are_MQ2011`,
  `A_column_host_reaches_a_sibling_column`, `A_fixed_of_in_a_stereotype_default_is_clean`,
  `Reserved_kinds_are_valid_and_dangle`.
- `PackLoaderTests.Pack_parameter_xrefs_are_checked`.
- `DeleteCascadeTests`: `An_optional_xref_is_cleared_and_a_required_one_refuses_remove_references`,
  `Delete_dependents_removes_a_required_xref_value_and_keeps_its_holder`,
  `A_resolution_refuses_when_the_extension_requires_the_property`.
- `ModelStoreDeleteTests`: `A_pack_parameter_naming_the_element_refuses_a_plain_delete`,
  `Remove_references_removes_an_optional_pack_parameter_in_the_same_change`.
- `NamesToIdsTests` (new): `Names_become_ids_all_or_nothing_and_an_ambiguous_name_refuses`, `A_dry_run_writes_nothing`,
  `A_second_run_changes_nothing`, `Each_unmatched_value_is_its_own_MQ2010`, `Element_ids_take_names`,
  `A_stereotype_is_matched_by_key_only`.
- `McpWriteTests.Names_to_ids_previews_then_applies_through_apply_batch`.
- `ModelApplyTests.Names_to_ids_dry_run_writes_nothing` (the class comes from C6).
- **Acceptance (3.1):**
  `Integration/TypedPropertyTests.A_default_sort_attribute_survives_rename_and_removal_is_refused_or_cleared` (new).
  Setup: an `E2ERepo` billing copy with `extensions/listing.json`, where `defaultSort` applies to entities and is typed
  `x-ref {kind: attribute, of: "$element"}`. Rename half: Invoice's value is `issuedOn`'s id. A rename keeps the value,
  and the model stays clean. Delete half: in the fixture `issuedOn` is also named by the Invoice register table's index
  and by two queries, so any save without it is invalid whatever the `x-ref` says. The copy therefore adds an invented
  attribute `dueOn`, which nothing else references, and points `defaultSort` at it. Saving Invoice without `dueOn` is
  `invalid`, with MQ2001 on Invoice at `/properties/defaultSort`. With `optional: true`, the same save is `saved`, and
  `defaultSort` is gone from the file.

**Tasks**

- **B4. Schema set and index pass.** Owns: `Model/PropertySchemas.cs` (new: `PropertySchemaSet`, `XRef`,
  `PropertyHost`), `ModelIndexer.cs`, `ModelSnapshot.cs`, `Documents.cs` (`ReferenceInfo`), `ExtensionRules.cs`
  (`Check` switches to `PropertyHost`), `RuleCatalog.cs` (MQ2009), `CatalogTests.cs` (+1), `common.json` with its
  copy, the `ReferenceInfo` schema in `openapi.yaml`, the `get_references` description in `tools.json` and its
  `docs/mcp.md` row. Tests: `PropertySchemaSetTests`, `ModelSnapshotIndexTests`, `ModelIndexerPatchTests`,
  `McpReadTests`. Verify: `E "~CatalogTests|~Model.|~Json.|~ExtensionFiles|~ValidationFixtureTests"`;
  `C "~McpSurfaceTests|~McpReadTests"`; `F "~ContractTests"`; `editor`; `copies`; `record`.
- **B5. Validator sites and pack parameter checks.** Owns: `ReferenceWalker.cs`, `BuiltinRules.cs` (`CheckReferences`),
  `UnitRules.cs`, `PackLoader.cs`, `RuleCatalog.cs` (MQ2010, MQ2011), `CatalogTests.cs` (+2). Tests: `XrefRuleTests`,
  `PackLoaderTests`. Verify: `E "~CatalogTests|~XrefRuleTests|~PackLoaderTests|~Validation."`; `C "~ValidateTests"`;
  `record`.
- **B6. Deletes, saves, pack parameter deletes, and the 3.1 acceptance test.** Owns: `ChangePlanner.Cascade.cs`,
  `ChangePlanner.cs`, `Loading/PackParameterRefs.cs` (new), `ModelStore.cs` (planner construction only),
  `ModelStore.DeletePlan.cs` (reporting only), the `delete_element` text and its `docs/mcp.md` row, and the user-guide
  subsection "Typed links (`x-ref`)". Tests: `DeleteCascadeTests`, `ModelStoreDeleteTests`,
  `Integration/TypedPropertyTests`. Verify: `E
  "~DeleteCascadeTests|~ModelStoreDeleteTests|~TypedPropertyTests|~ReferenceBindingTests"`; `C
  "~McpSurfaceTests|~McpWriteTests"`.
- **B7. `names-to-ids`.** Runs after B6 and after C6. Owns: `ModelStore.cs` (`BatchOp.NamesToIds`,
  `BatchOperation.ElementIds`), `ModelStore.Schemas.cs` (the dispatcher's list of diagnostics, and the `names-to-ids`
  branch), `ModelStore.NamesToIds.cs` (new; its resolver is shared with B13), `BatchParser.cs`, `Loading/BatchNames.cs`
  (`elementIds` items as id fields), `batch.json` with its copy, `BatchOperation` in `openapi.yaml`, the `apply_batch`
  text and its `docs/mcp.md` row, and the `names-to-ids` paragraph of "Typed links (`x-ref`)". Tests: `NamesToIdsTests`,
  `McpWriteTests`, `ModelApplyTests`. Verify: `E "~NamesToIdsTests|~ModelStoreBatchTests|~TagTests|~Json."`; `C
  "~McpSurfaceTests|~McpWriteTests|~ModelApplyTests"`; `F "~ContractTests"`; `editor`; `copies`.

### 4.2 Stereotype rules and typed parameters

**Decisions taken here**

- **Effective set.** Rules judge an element's own stereotypes plus the ones they imply. Phase 2 adds schema and
  package defaults to the same set (3.3), so a clash stays an error and never becomes a hidden precedence.
- **Order.** Implied come first, then own. When a stereotype is both applied and implied, the own entry is kept, at
  its own position with `ImpliedBy` null, and the implied copy is dropped. Because the later entry wins
  (`ResolveRun.Conceptual.cs:847-861`), an explicit choice beats an implied default.
- **One level of implication.** Implying a stereotype that itself implies others is MQ2017, which also refuses cycles.
- **Keys and labels.** `requires`, `conflicts` and `implies` hold keys, as `stereotypes` does. Keys are immutable
  (MQ3020) and the index resolves them to ids (`ModelIndexer.cs:709-718`). `group` is a label, not a link.
- **`parameters`.** It has the shape `{properties, required}`, like extension files and pack `parameterSchema`, and
  accepts `x-ref` and `x-translatable`. Values stay in the flat `properties`, where `defaultProperties` already merge
  and where templates read them.
- **Defaults.** Defaults live only in `defaultProperties`; these are the scope's "typed defaults". Each default is
  checked against its own property schema in `parameters`, without `required`, because a stereotype may leave a
  required parameter to its hosts. Required parameters are judged on each host's merged values. A schema `default`
  keyword is ignored.
- **Coverage.** `group`, `requires`, `conflicts`, `implies` and `parameters` are judged by validation and read by
  templates through the effective set, so `ResolvedCoverageTests` excuses them in `Pending`, with that reason (B8).
- **Raw versus effective.** Raw lists stay exactly as the file says. Judging and resolving use the effective set:

| Reader | Phase 1 |
| --- | --- |
| `ResolveRun.StereotypesOf` (`ResolveRun.Conceptual.cs:741`), `ResolveRun.Processes.FillNode` (`:428-456`) | effective, one helper |
| `ValidationContext.VirtualAttributes` (`:269-278`) and the `x-ref` `$element` scope that shares its helper (4.1), `ExtensionSet.CheckItem/Locate/Merge` (`:158-245`) | effective; implied values point at the implying entry |
| `UnitPlanner.Stereotypes` (`UnitPlanner.cs:658-668`) | effective |
| 3.2's classifier for a stereotype change: `Migrations/DatabaseReach.cs` and the selection in `ModelStore.Migrations.cs` | effective: carriers through `EffectiveStereotypes`, footprint and reach included |
| `ModelIndexer.Summarize`, `ModelPages --stereotype`, index references | the file's list (rows mirror files) |
| `PropertySchemaSet.For` and `Sites` (the `x-ref` walk of the index and the validator, delete checks, localization) | effective keys, from the set's own `implies` map |

- **Schema applicability (B9).** The set is built before the index, so it keeps its own map of each key's `implies` (the
  ordinally first file per key) and `For` applies schemas by the holder's keys plus the keys they imply. A schema that
  applies to an implied stereotype then types the values of its carriers in the index, the validator and deletes alike,
  as `ExtensionSet` judges them. `PropertySchemaSet.Create` takes the stereotype documents as an optional third
  argument; `Hash` adds their ordinal `(path, hash)` pairs after the extension files' (unchanged for a model without
  stereotypes), and a source whose file did not change is reused with its compiled schema.
- **`properties` is required (B9).** `StereotypeParameters.Properties` is a `required` member and `stereotype.json`
  requires `properties` in `parameters`, as `ExtensionSchema.Properties` and `extension.json` do: a `parameters` without
  it has nothing to type, and an unset `JsonElement` cannot be written back.
- **Where parameter findings go (B9).** A malformed `x-ref` in `parameters`, or an `of` that names no element, is MQ2009
  on the stereotype's own document, so a save of the stereotype reports it (extension files keep their whole-model
  report). A required parameter that neither the host nor a default sets is MQ2018 at the entry of the stereotype that
  requires it (the implying entry for an implied one), as `Locate` does for inherited values. Parameters that fail to
  evaluate are MQ2019 on the host. An `of` id in `parameters` is indexed under the field `of` (4.1, step 3) by the
  index's walk of the stereotype, so the delete plan's `of` warnings (B6) stay on extension files and pack schemas.
- **Carriers in the classifier (B8).** The carriers of a stereotype are the files whose stereotype lists name it and the
  carriers of each stereotype whose `implies` names it; a stereotype that only requires or conflicts with it is no
  carrier, since neither changes a database. The reach needs no change: a reached stereotype's `implies` are references.
- **A parameter's `of` on delete.** `remove-references` refuses to clear a stereotype parameter's `x-ref` `of`, since
  without it the parameter would take the members of any element; `delete-dependents` removes the `of` key, and the
  delete plan lists it as a clear.

**Model and schema**

`stereotype.json`: in `x-order`, `group`, `requires`, `conflicts` and `implies` follow `appliesTo`, and `parameters`
comes before `defaultProperties`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `group` | `$defs/key` | absent | At most one stereotype of a group per element |
| `requires` | `keyList` | `[]` | Keys the element must also carry |
| `conflicts` | `keyList` | `[]` | Keys it must not carry |
| `implies` | `keyList` | `[]` | Keys applied with it, one level |
| `parameters` | object, `x-order` `properties`, `required` | absent | Name to JSON Schema; required names |

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
// Marks key lists the index resolves to stereotype ids.
[AttributeUsage(AttributeTargets.Property)] public sealed class StereotypeKeysAttribute : Attribute;
public sealed record Stereotype : Element
{   // existing members, plus:
    public string? Group { get; init; }
    [StereotypeKeys] public IReadOnlyList<string> Requires { get; init; } = [];
    [StereotypeKeys] public IReadOnlyList<string> Conflicts { get; init; } = [];
    [StereotypeKeys] public IReadOnlyList<string> Implies { get; init; } = [];
    public StereotypeParameters? Parameters { get; init; }
}
public sealed record StereotypeParameters { public required JsonElement Properties { get; init; }
                                            public IReadOnlyList<string> Required { get; init; } = []; }
/// <param name="Entry">The element's own entry, or the implying stereotype's.</param>
public sealed record AppliedStereotype(Stereotype Stereotype, int Entry, string? ImpliedBy);
public sealed class ModelSnapshot
{   /// <summary>Implied first, then own; an own entry replaces an implied copy of the same key; unknown keys
    /// skipped.</summary>
    public IReadOnlyList<AppliedStereotype> EffectiveStereotypes(IReadOnlyList<string> keys);
}
namespace Maquettiste.Engine.Resolution;
public sealed class RStereotype { public string? ImpliedBy { get; internal set; } }
```

`ElementBase.Stereotypes` and the `Stereotypes` of process nodes also carry `[StereotypeKeys]`.

**Engine behaviour**

- **Index.** `DocumentWalk.RoleOf` returns `StereotypeKeys` for attributed properties, which replaces the name test at
  `ModelIndexer.cs:778-780`. The three new lists become references, so a stereotype they name cannot be deleted
  plainly.
- **Element rules.** `CheckStereotypes` (`BuiltinRules.cs:217-228`, also called from `ProcessRules.cs:132-145`) judges
  the effective set. It reports MQ2013 to MQ2015 at the later stereotype's `Entry`, and MQ2004 at the implying entry
  for an implied stereotype that does not apply.
- **Stereotype rules.** `CheckStereotype` (`:756-762`) adds MQ2016, MQ2017, MQ2019, and MQ2018 for defaults, checked
  per property as the defaults decision says.
- **Values.** `ExtensionSet` evaluates the parameter sources from B4. MQ2018 points at `<host>/properties<location>`.
  A missing or inherited value points at the stereotype's entry (`Locate`, `ExtensionRules.cs:209-222`).
- **Save scope.** A save validates the changed ids and their referrers (`ModelStore.cs:771-774`). The carriers of a
  stereotype `A` that implies `B` are not referrers of `B`, so a change to `B`'s group, conflicts or parameters would
  not be judged on them. `ModelValidator.ConflictPeers` (`:338`) gains a `Stereotype` case: the elements that carry a
  stereotype whose `implies` names the changed one.
- **Resolution.** `FillAnnotations` (`:817-844`) sets `ImpliedBy`. Implied stereotypes contribute virtual attributes,
  storage and defaults exactly as directly applied ones do. No existing model renders differently, so the engine
  contract does not change.
- **`get_schema`.** `AgentTools.Handlers.cs:207-214` adds `stereotypeParameters: [{stereotype, parameters}]` for the
  stereotypes that may apply to the kind. The assistant shares the tool.

**CLI and MCP**

`get_schema` returns `stereotypeParameters`, and its row in `docs/mcp.md` changes. No tool or verb is added.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ2013 | E | An element carries two stereotypes of one group, own or implied. | Remove one |
| MQ2014 | E | An element carries a stereotype that requires another it does not carry. | Add it |
| MQ2015 | E | An element carries two stereotypes that conflict. | Remove one |
| MQ2016 | E | `requires`, `conflicts` or `implies` names an unknown key or itself, conflicts with one it requires or implies, or implies one of its own group. | Fix the stereotype |
| MQ2017 | E | A stereotype implies one that implies others; implication is one level. | Imply each directly |
| MQ2018 | E | A property value fails the `parameters` of a stereotype the element carries (`required` included, on the merged values), or a default fails its own property schema (`required` not applied). | Fix the value |
| MQ2019 | E | `parameters` is not a valid schema, or `required` names an undeclared parameter. | Fix the stereotype |

**Docs**

New user-guide section "Stereotype rules and parameters"; the `get_schema` row in `docs/mcp.md`; `stereotype.json` and
its copy.

**Tests**

- `StereotypeRuleTests` (new): one failing and one passing case per rule from MQ2013 to MQ2019,
  `Implied_but_not_applicable_is_MQ2004_at_the_implying_entry`, and
  `A_default_may_leave_a_required_parameter_to_its_hosts`.
- `ModelSnapshotTests.Requires_conflicts_and_implies_are_indexed_as_references`.
- `FactRecordingTests.Changing_an_implied_stereotype_records_facts_on_the_implying_carriers_tables`.
- `ResolverBehaviorTests`: `Implied_stereotypes_merge_under_own_and_carry_implied_by`,
  `An_own_entry_replaces_an_implied_copy_of_the_same_stereotype`.
- `ModelStoreTests.A_save_that_puts_an_implied_stereotype_in_a_taken_group_is_refused_on_its_carriers`.
- `AgentToolsTests.Get_schema_lists_stereotype_parameters`.
- **Acceptance (3.2, first line):**
  `Integration/StereotypeRulesTests.Soft_delete_and_hard_delete_in_one_group_on_a_table_is_an_error` (new). Setup: a
  billing copy where `soft-delete` gains `table` and the group `deletion`, and a new `hard-delete` joins that group.
  Both go on `tables/01j92p0v1t0j6rh4my9h81nyb4.json`. Expected: MQ2013, an error, at `/stereotypes/2`.
- **Acceptance (3.2, second line):**
  `Integration/StereotypeRulesTests.A_wrong_typed_retention_days_on_an_audited_table_is_an_error_with_a_pointer`.
  Setup: `audited` gains `parameters` with `retentionDays: {type: integer, minimum: 1}`, and the table says
  `"seven years"`. Expected: MQ2018 at `/properties/retentionDays`. The default `2555` stays clean.

**Tasks**

- **B8. Keys, effective set and readers.** Owns: `Vocabularies.cs`, `StereotypeKeysAttribute.cs` (new), the attribute
  in `ElementBase.cs` and `Processes.cs`, `ModelIndexer.cs` (`RoleOf`), `ModelSnapshot.cs`,
  `ResolveRun.Conceptual.cs`, `ResolveRun.Processes.cs`, `ResolvedCore.cs`, `ValidationContext.cs`,
  `ExtensionRules.cs` (effective set only), `UnitPlanner.cs`, `Migrations/DatabaseReach.cs` and the stereotype
  selection in `ModelStore.Migrations.cs`, `ResolvedCoverageTests` (the `Pending` entries above), and
  `stereotype.json` with its copy. Tests: `ModelSnapshotTests`, `ResolverBehaviorTests`, the `FactRecordingTests` case.
  Verify: `E "~Model.|~Resolution.|~Json.|~RuleHelperTests|~FactRecordingTests"`;
  `P "~GoldenTests"`; `editor`; `copies`.
- **B9. Rules and parameters.** Runs after B4 and B8. Owns: the stereotype parts of `BuiltinRules.cs`,
  `ExtensionRules.cs`, the parameter sources in `PropertySchemas.cs`, `ModelValidator.cs` (`ConflictPeers` `Stereotype`
  case), `AgentTools.Handlers.cs`, `tools.json`, `RuleCatalog.cs` (MQ2013 to MQ2019), `CatalogTests.cs` (+7), the
  user-guide section and the `get_schema` row in `docs/mcp.md`. Tests: `StereotypeRuleTests`, `ModelStoreTests`,
  `AgentToolsTests`, `Integration/StereotypeRulesTests`. Verify: `E
  "~CatalogTests|~StereotypeRule|~AgentToolsTests|~ValidationFixtureTests|~ProcessRuleTests|~ModelStoreTests"`; `C
  "~McpSurfaceTests"`; `record`.

### 4.3 Package membership and layering

**Decisions taken here**

- **Every kind gets `package`, migration facts aside.** The kinds that gain it are table, view, sequence, routine,
  database type, SQL object, query, database, mapping, stereotype, reference type, seed, actor and scenario. A package
  keeps `parent`, and vocabularies keep `package` as their scope.
- **Migration facts have no package.** A fact (3.2) is history of its database: its file states no package, so it sits
  in no layer, `PackageOf` returns null for it, and 5.1's name index lists it by id only. `IPackaged` is on every kind
  with a `package` key (the 14 above and the nine that had one), not on `Package` or `Migration`.
- **Coverage and fact recording.** Where a targeted resolved type lacks `Package`, B3 adds a `Pending` entry with its
  reason to `ResolvedCoverageTests`. A table's or other physical object's `package` never reshapes a database, so B3
  adds it to 3.2's skip list.
- **Membership never changes the database.** A table's own package does not route it through
  `database.packages[].schema`, which still routes only entity-derived tables.
- **Resolved fallbacks.** A table falls back to its entity's or relation's package (as `UnitPlanner.SourceOf` does), a
  seed to its target's (`ResolveRun.ReferenceData.cs:156`), and a scenario to its process's. Layering and the index
  read only the file.
- **What widens.** `ModelIndexer.PackageOf` covers every kind, so `ElementSummary.Package`,
  `model stats --by package`, `model export --package`, `get_model_kinds`, `get_elements` and the vocabulary scope
  widen too. A table in a domain now sees that domain's tags.
- **What stays.** MQ3001 name scopes stay as they are, because names reach SQL and file paths. `RPackage` child lists
  and diagram membership stay.
- **Pack units.** `PackLoader` (`:196-201`) stops refusing `where.packages` on view, sequence, routine, database type,
  SQL object and query units.
- **The name `dependsOn` stays, as the scope uses it.** On views, routines and SQL objects the same key lists DDL
  order. Each kind's `[ElementRef]` targets type it, so MQ2002 refuses a mix-up.
- **Checked packages.** A package is checked when it or a parent declares `dependsOn`, even as `[]`. The key therefore
  has no default, so that `[]` survives canonical form.
- **Allowed references.** A checked package `P` may reference its own and its descendants' elements. It may also
  reference a package `T` when `T`, or an ancestor of `T`, is in the union of `dependsOn` over `P` and its parents.
- **The scope's example.** A child does not reach its parent by default. In the scope's acceptance line, "without
  `dependsOn`" means that catalog's list lacks billing.
- **What counts as a reference.** Every index reference counts, `x-ref` values and stereotype keys included. These do
  not:
  - `parent` and `dependsOn` themselves;
  - `package` fields, which state membership;
  - `category` references into category trees. `PackageOf` gives a tree its scope package (`ModelIndexer.cs:519-520`),
    and MQ2008 already governs those references along the domain chain;
  - references from diagrams, which are presentation;
  - references from an element whose file states no package (it is unchecked), or to one (it sits in no layer).
- **A package's own references.** Its stereotypes are judged as the package's own, not its parent's, as
  `BuiltinRules.VocabularyScope` (`:234`) already does for vocabularies.
- **Seed merging.** Seeds of one target merge ordered by (package qualified name, with null first; then name; then
  id). That is today's order when no seed has a package.
- **Seed key errors.** Duplicate keys keep MQ7112, MQ7001 and MQ7102, with both packages named. Where today the first
  table seed silently decides the key (`ResolveRun.TableSeeds.cs:57`), seeds that disagree now raise MQ7121.
- **`IPackaged` on every kind with the key (B3).** `PackageOf` becomes the two-arm switch below, so the kinds that
  already had `package` (entity, value object, scalar type, enum, relation, diagram, process, tag vocabulary, category
  tree) implement `IPackaged` too; naming only the 14 new kinds would have dropped them from the index.
- **Where the skip and the fallback sit (B3).** The fact-recording skip strips a top-level `package` only on the 14
  kinds that gained it; an entity's package can still route its tables. The table fallback runs at the start of
  `DatabaseRun.FinishTables`, after relations bound their junctions, so `RTable.Package` is final before templates and
  `UnitPlanner.PackageMatches` read it.
- **Every `package` field is skipped (B10).** The skip goes by field name, so a database's convention entries
  (`packages[].package`) do not count either: routing is not a layering reference. A reference whose target is a package
  (an `x-ref`, a convention entry) sits in that package's own layer, as a package's own references do.
- **MQ2021 on each edge (B10).** A `dependsOn` entry is reported when it names the package itself, or when both ends sit
  in one strongly connected component of the graph. The pass is `DatabaseObjectIndex.StronglyConnected`, made internal
  and shared.
- **The first seed decides (B11).** MQ7121 compares each table seed with the table's first seed in merge order, whose
  key the resolver uses. A later seed that disagrees is reported, whatever its environments, because a table has one
  row key.
- **Both packages, when they differ (B11).** MQ7112, MQ7001 and MQ7102 name both seeds' packages, with "in no package"
  for a seed without one, only when the two packages differ, so the messages of models without packages stay as they
  are. The code index in `ReferenceDataIndex` keeps its (name, id) order: only duplicate codes, which are errors, can
  tell the difference.
- **Seeds record the packages of their chain (B11).** No seed file holds the qualified name the merge order reads, so
  each `RSeed` adds the `e:` key of its file's package and of every parent (`SeedOrder.ChainOf`), and the seed lists of
  a target add those of all its seeds to their membership keys. Renaming or moving a package then re-renders what read
  the reordered seeds, and an incremental run writes what a full run writes.
- **A package save revisits no cycle peers or reordered seeds (B10, B11).** Save scope is as stated under "Save scope"
  below and no wider. A save that closes a `dependsOn` cycle is refused on the saved package's own MQ2021, but the other
  packages of the cycle, and those of a cycle a save opens, keep their last findings until the whole model is
  validated. A rename or move that reorders seeds only moves MQ7112, MQ7121, MQ7001 and MQ7102 between seeds that
  already disagree, so the save does not revisit those seeds either; whole-model validation shows where the findings
  now sit.

**Model and schema**

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `package` (14 kind schemas, after `pluralName`) | `$defs/id` | absent | Owning package; indexed reference |
| `dependsOn` (`package.json`, after `parent`) | `idList`, `uniqueItems` | none | Packages it may reference; absent: unchecked unless a parent declares it |

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
public interface IPackaged { string? Package { get; } }   // every kind with a package key; not Package or Migration
// Table, View, Sequence, Routine, DatabaseType, SqlObject, Query, Database, Mapping, Stereotype, ReferenceType, Seed,
// Actor, Scenario gain:  [ElementRef(ElementKind.Package)] public string? Package { get; init; }
public sealed record Package : Element
{   [ElementRef(ElementKind.Package)] public string? Parent { get; init; }
    [ElementRef(ElementKind.Package)] public IReadOnlyList<string>? DependsOn { get; init; }  // null: not declared
}
namespace Maquettiste.Engine.Resolution;
public abstract class RAnnotated : RObject { public RPackage? Package { get; internal set; } }  // moved from RElement
public sealed class RPackage : RElement { public IReadOnlyList<RPackage> DependsOn { get; internal set; } = []; }
```

**Engine behaviour**

- **Index.** `PackageOf` (`ModelIndexer.cs:509-522`) becomes `Package p => p.Parent, IPackaged x => x.Package`.
- **Resolution.** `FillPhysicalAnnotations` (`ResolveRun.Conceptual.cs:792-814`) fills `Package`, and
  `UnitPlanner.PackageMatches` (`:694-705`) reads it.
- **MQ2020.** The new `Validation/PackageRules.cs` reads `ReferencesFrom(id)` per document and finds owners through
  `TryGetEntry`. It judges each reference against a lazy `ValidationContext.Layers` map, which holds a checked flag
  and an allowed set per package. MQ2020 is reported at the reference.
- **Save scope.** A save validates the changed ids and their referrers (`ModelStore.cs:771-774`). A change to a
  package's `dependsOn` or `parent` changes the allowed set of its whole subtree, and the members of child packages
  are not referrers of the changed package. `ModelValidator.ConflictPeers` (`:338`) therefore gains, beside its
  existing `Package` case and only when some package is checked: every element whose file package is in the changed
  package's subtree, and every referrer of those elements. The first covers a changed `dependsOn` above a member; the
  second covers references into a moved subtree.
- **MQ2021.** One ordinal depth-first pass over the `dependsOn` graph.
- **Seed order.** One `SeedOrder` comparer replaces the sorts at `ResolveRun.ReferenceData.cs:141` and in
  `ValidationContext.SeedsOf` (`:115`). It also decides which seed is "later" in `TableSeedRules.CheckOtherSeeds`
  (`:189-216`).
- **MQ7121** is reported at the later seed's `/key`, or at `/target` when that seed has no key.

**CLI and MCP**

No tool, verb or code change. `model stats --by package`, `model export --package`
(`ModelCommand.cs:90-113, 367-395`), `get_model_kinds` and `get_elements` list the new members through `PackageOf`.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ2020 | E | A reference from one package to another its package does not depend on (`dependsOn` of the package or a parent). | Add it to `dependsOn` |
| MQ2021 | E | `dependsOn` names the package itself or forms a cycle. | Remove a dependency |
| MQ7121 | E | Two seeds of one table key their rows differently (different `key`, or a `key` beside the primary key). | Use one `key` |

**Docs**

New user-guide section "Packages and layering"; the merge order and MQ7121 in
`docs/guides/reference-data-and-seeds.md`; the 15 changed schemas and their copies.

**Tests**

- `SchemaConsistencyTests` (existing).
- `PackageLayeringTests` (new): `Outside_dependsOn_is_MQ2020_and_parents_lend_dependencies`,
  `Descendants_are_reachable`, `A_cycle_is_MQ2021`, `Without_dependsOn_nothing_changes`,
  `Diagrams_and_membership_do_not_count`, `Category_references_are_left_to_MQ2008`,
  `A_packages_own_stereotypes_are_judged_as_its_own`.
- `FactRecordingTests`: `A_table_package_edit_resolves_no_database` (B3), `A_layering_error_still_records_facts`
  (B10).
- `ModelStoreTests.A_save_that_drops_a_dependsOn_entry_is_refused_with_MQ2020_on_a_grandchilds_element`.
- `PackLoaderTests.Physical_object_units_filter_on_packages`.
- `UnitPlannerTests.A_table_file_package_wins_over_its_entitys`.
- `TableSeedTests`: `Seeds_in_two_packages_merge_by_package_then_name`, `Seeds_keying_rows_differently_are_MQ7121`,
  `A_row_key_claimed_in_two_packages_is_MQ7112_naming_both`,
  `Renaming_a_package_on_a_seeds_chain_reorders_its_rows_and_re_renders_the_seed_script`.
- `ReferenceDataGenerationTests.Renaming_a_package_that_orders_a_types_seeds_re_renders_the_type` (B11).
- **Acceptance (3.4, first and second lines):** Cli
  `ModelCommandTests.Catalog_holds_tables_seeds_and_reference_types_and_stats_list_them`. Setup: a `CliRepo.Billing()`
  copy where the `notes` table joins Catalog, and Catalog gains a reference type and a table seed. Expected: both
  `model stats --by package` and `model export --package Catalog` list them.
- **Acceptance (3.4, third line):**
  `Integration/PackageLayeringTests.A_reference_from_catalog_to_billing_without_dependsOn_is_an_error` (new). In the
  fixture Catalog is a child of Billing, `Money` is in Billing, and Product (in Catalog) has `listPrice` typed `Money`.
  Setup: Catalog gets `dependsOn: []`. Expected: one MQ2020, an error, on Product at `/attributes/3/type/ref`. Listing
  Billing in Catalog's `dependsOn` clears it.

**Tasks**

- **B3. Membership on every kind.** Owns: `Physical.cs`, `ReferenceData.cs`, `Processes.cs`, `Vocabularies.cs`,
  `Conceptual.cs`, `ModelIndexer.cs` (`PackageOf`), `ResolvedCore.cs`, `ResolvedConceptual.cs`,
  `ResolveRun.Conceptual.cs`, `ResolveRun.ReferenceData.cs`, `ResolveRun.Processes.cs`, `UnitPlanner.cs`,
  `PackLoader.cs`, 15 schemas with their copies, the skip list in `ModelStore.Migrations.cs` and `ResolvedCoverageTests`
  (`Pending` entries where needed). Tests: `PackLoaderTests`, `UnitPlannerTests`, `ModelCommandTests`, the B3
  `FactRecordingTests` case. Verify: `E "~Json.|~Planning.|~Resolution.|~Loading.|~FactRecordingTests"`; `C
  "~ModelCommandTests"`; `P "~GoldenTests"`; `editor`; `copies`.
- **B10. Layering.** Runs after B3. Owns: `PackageRules.cs` (new), `ValidationContext.cs` (`Layers`),
  `ModelValidator.cs` (wiring and the `ConflictPeers` package subtree), `RuleCatalog.cs` (MQ2020, MQ2021),
  `CatalogTests.cs` (+2), and the user-guide section. Tests: `PackageLayeringTests`, `ModelStoreTests`,
  `Integration/PackageLayeringTests`, the B10 `FactRecordingTests` case. Verify: `E
  "~CatalogTests|~PackageLayering|~ValidationFixtureTests|~ModelValidatorTests|~ModelStoreTests|~FactRecordingTests"`;
  `record`.
- **B11. Seeds across packages.** Runs after B3. Owns: `Resolution/SeedOrder.cs` (new), `ResolveRun.ReferenceData.cs`,
  `ResolveRun.TableSeeds.cs`, `TableSeedRules.cs`, `ReferenceDataRules.cs` (messages), `ValidationContext.cs`
  (`SeedsOf`), `RuleCatalog.cs` (MQ7121), `CatalogTests.cs` (+1), and the seeds guide. Tests: `TableSeedTests`.
  Verify: `E "~CatalogTests|~TableSeedTests|~ReferenceBindingTests|~ValidationFixtureTests"`; `record`;
  `PG P "~TableSeedSqlTests|~GoldenTests|~ReferenceDataTests"`. No fixture
  seed has a package, so the generated SQL does not change.

### 4.4 Translatable properties and the tags-to-property refactor

**Decisions taken here**

- **Where `x-translatable` goes.** On top-level string properties of extension files and stereotype `parameters`.
  Project kinds follow in phase 2. Anywhere else, the keyword is MQ7212.
- **Which nodes are translated.** Only nodes that localization already knows: domain kinds, plus the sub-nodes that
  `PropertyHost` reaches (attributes, enum members, states, transitions and events). Physical, mapping, diagram and
  vocabulary kinds stay out by design (`LocaleChains.cs:20-24`). A schema that applies only to those is MQ7213.
- **Inherited `defaultProperties` texts are not translated in phase 1.** Their translation entry would belong to every
  element that carries the default.
- **Field name.** It is `properties/<name>` in rows, edits, `src` keys and the `translate` helper. In an entry, the
  texts sit in a nested `properties` object. The `translate` batch op has no handler (`ModelStore.cs:233-239`), so its
  `field` enum in `batch.json` keeps the four standard fields.
- **The refactor is a batch operation, `tags-to-property`, beside `retag`.** None exists today: a8bd16b added only
  `tag_usage` and `retag`. The target property must be declared by a schema that applies to each holder, because a
  typed value is a property (the rule of 2026-10-06).
- **`src` takes property fields through `propertyNames`, not `patternProperties` (B12).** The schema precheck
  (`Json/SchemaPrecheck.cs`) treats `patternProperties` as unsupported, so every shard with a `src` would fall to the
  full evaluator and `SchemaPrecheckTests` would fail its "valid fixtures are decided" check. `src` keeps its four
  declared keys and adds `additionalProperties` `#/$defs/hash` with the `propertyNames` pattern
  `^(displayName|pluralName|label|description|properties/[A-Za-z_][A-Za-z0-9_]*)$`: the same documents are valid, and
  the canonical writer still puts the undeclared keys after the declared ones, ordinal.
- **The shard readers and the XLIFF reader learn the new keys (B12).** `LocaleShardReader.cs` (the streamed read) and
  `Loading/ShardCache.cs` (the parsed-shard cache, format 2) read and keep `properties`; without them a reload drops
  the texts. `TranslationFiles.ReadXliff` splits a unit id at its first slash, since ids hold none and
  `properties/<name>` does.
- **Where MQ7212 and MQ7213 are reported (B12).** `PropertySchemaSet.TranslatableFindings` finds them. An extension
  file's join the set's file diagnostics (whole-model validation, with MQ2009). A stereotype's are reported by
  `BuiltinRules.CheckStereotypeParameters` with the stereotype, beside its MQ2009, so a save of it reports them. A
  `x-translatable` that is not a boolean is MQ7212 too, and a misplaced mark marks nothing.
- **The `l:` hash and the helper (B12).** `OwnerHash` hashes the standard fields as before, then `FieldsIn(entry)`'s
  property fields with their texts and `src`, so an entry without property texts keeps its hash. `translate` on a
  property field falls back to the holder's own text, else to its resolved value (a stereotype default, untranslated),
  recording the object's dependencies.
- **Editor labels (B12).** The field of `TranslationPage` and `TranslationWrite` in `openapi.yaml` becomes a string
  with that pattern, so the editor's translation queue and Translations section label a property field by its name
  (`l10n/model.ts` `fieldLabel`).
- **What leaves the vocabulary (B13).** Without `elementIds`, the declarations of the tags that moved leave the
  vocabulary, except one still used on an object that holds no properties (a relation end, say), which the move does not
  reach. A prefixed declaration nothing used is not a moved tag and stays. `batch.json` requires `prefix` and `name` for
  the op. An array property takes each suffix as its `items` schema makes it, so a string array is the common case and
  an array of integers or of an `enum` works the same way. The first schema in set order that applies to the holder and
  declares the property converts the suffixes.

**Model and schema**

`"x-translatable": true` goes on a string property schema. A `locale.json` entry gets the `x-order` `displayName`,
`pluralName`, `label`, `description`, `properties`, `src`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `properties` | object, identifier to non-empty string | `{}` | Translated property texts |
| `src` › `properties/<name>` | `#/$defs/hash` via `additionalProperties`, the names by `propertyNames` `^(displayName\|pluralName\|label\|description\|properties/[A-Za-z_][A-Za-z0-9_]*)$` | absent | After the standard keys, ordinal |

`batch.json` adds the op `tags-to-property` and `prefix`, a non-empty string placed after `tags`. `name` names the
property. `package` and `expectedHashes` behave as they do for `retag`, and `elementIds` as it does for `names-to-ids`
(4.1).

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
public sealed record TranslationEntry
{   // existing members (Src may hold "properties/<name>"), plus:
    public IReadOnlyDictionary<string, string> Properties { get; init; } = ImmutableDictionary<string, string>.Empty;
}
namespace Maquettiste.Engine.Localization;
public sealed record LocalizableNode(/* positional parameters unchanged */)
{   public IReadOnlyDictionary<string, string> PropertyTexts { get; init; }  // own texts, ordinal by name
    public IReadOnlySet<string> TranslatableProperties { get; init; }       // declared names; others are orphans
}
public sealed class LocalizationIndex
{   public const string PropertyFieldPrefix = "properties/";
    public static IEnumerable<string> FieldsOf(LocalizableNode node);   // standard fields, then property fields
    public static IEnumerable<string> FieldsIn(TranslationEntry entry); // fields an entry holds
    public static bool IsField(string field);                           // standard, or properties/<identifier>
}
```

`BatchOp.TagsToProperty` and `BatchOperation.Prefix` are stated with the whole record in 4.1.

**Engine behaviour**

- **Nodes.** `BuildNodes` and `SubNode` (`LocalizationIndex.cs:583-677`) fill both members from `PropertySchemaSet`.
  `Source`, `Allows`, `ExpectedFields`, `Text`, `StateOf` and the `l:` hashes handle the prefix.
- **Positional field storage.** Three spots in `LocalizationIndex.cs` index fields by position today: `Completeness`
  (`:423-426`, `f switch { 0..3 }` over `_nodeHashes[n][f]`), the per-node hash rows (`:629`, arrays sized to
  `Fields.Count`) and `OwnerHash` (`:464`, a loop over `Fields`). Per-node hashes become field-keyed rows built from
  `node.ExpectedFields()`: the standard fields the node has, then its own property texts in ordinal order, each with
  the hash of its source text. `Completeness` walks those rows, not `FieldsOf(node)`, because a property that only
  inherits its default text is not text the node holds, so nobody is expected to translate it. `OwnerHash` iterates
  `FieldsIn(entry)` and also hashes the entry's `properties`.
- **Field loops.** `ModelStore.Translations.cs:72` uses `FieldsOf(node)`. `ModelStore.Translations.cs:178` checks
  `IsField && Allows`. `TranslationPrune.cs:66`, `DefaultLocale.cs:102` and `LocalizationRules.cs:83` use
  `FieldsIn(entry)`. `LocalizationHelpers.cs:36` accepts `IsField`, and its MQ6006 message names the prefix. As a
  result, `get_translations`, `localization_status`, MQ7203 to MQ7206 and pruning all cover property texts. Pruning
  drops properties that are no longer translatable.
- **`tags-to-property`.** It sits beside `Retag` (`ModelStore.Tags.cs:128-185`) and joins `IsTagOperation`. It acts on
  the holders that the `package` vocabulary governs (`GovernedUses`), limited to `elementIds`, whose tags start with
  `prefix` (ordinal) and leave a non-empty suffix. The declaring schema converts the suffixes: a string takes exactly
  one; a string array takes all of them, in list order; an integer, number or boolean takes one, parsed with the
  invariant culture; an `enum` takes one member; an `x-ref` resolves its name with B7. The tags leave the holder.
  Without `elementIds`, their declarations leave the vocabulary too.
- **Refusals.** MQ1002 when no tag matches, as `retag` does. MQ5001 when no schema declares `name`, a suffix does not
  fit, or the holder already holds another value: one diagnostic per holder, at its file and `/properties/<name>`,
  through the list of diagnostics B7 gives the dispatcher. The operation is all or nothing, uses the atomic store
  path, and `PreviewBatchAsync` is its dry run (with C6's `dryRun` and `model apply --dry-run`).

**CLI and MCP**

No tool or verb is added. The `get_translations` and `set_translations` descriptions (`ModelTools.cs:183-230`) name
`properties/<name>`, `apply_batch` describes `tags-to-property`, and `localization_status` (`tools.json`) counts
property fields.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ7212 | E | `x-translatable` on something other than a top-level string property of an extension file or stereotype parameters. | Move it |
| MQ7213 | W | `x-translatable` on a property whose schema applies only to kinds without translations (physical objects, mappings, diagrams, vocabulary containers). | Remove it |

**Docs**

A paragraph each in the user guide's "Translating the model in the editor" and "Custom properties" (B12). The
`get_translations`, `set_translations` and `localization_status` rows in `docs/mcp.md` (B12). The `apply_batch` row
and a paragraph after "Rename or remove a tag everywhere" in the user guide (B13). `locale.json` (B12) and `batch.json`
(B13), their copies, and `openapi.yaml`.

**Tests**

- `LocalizationTests`: `Translatable_properties_join_rows_status_and_pruning`,
  `An_inherited_default_text_is_not_translated`, `Completeness_and_owner_hashes_cover_property_fields`.
- `LocalizationRuleTests.Nested_or_number_translatable_is_MQ7212_and_physical_only_MQ7213` (new class).
- `LocalizationHelperTests.Translate_reads_a_property_field_through_the_chain` (new class).
- Cli `McpLocalizationToolTests.Translations_handle_property_fields`.
- `TagsToPropertyTests` (new): `Prefixed_tags_move_into_a_typed_property_and_leave_the_vocabulary`,
  `A_misfit_suffix_or_undeclared_property_refuses_with_one_MQ5001_per_holder`,
  `Element_ids_limit_the_move_and_keep_declarations`.

**Tasks**

- **B12. Translatable properties.** Runs after B4. Owns: `LocalizationIndex.cs`, `Model/Localization.cs`,
  `ModelStore.Translations.cs`, `TranslationPrune.cs`, `DefaultLocale.cs`, `LocalizationApi.cs`, `LocalizationRules.cs`,
  `LocalizationHelpers.cs`, `PropertySchemas.cs` (MQ7212, MQ7213), `RuleCatalog.cs`, `CatalogTests.cs` (+2), the
  `ModelTools.cs` texts, the `localization_status` description in `tools.json`, `locale.json` with its copy, the three
  `docs/mcp.md` rows and the user-guide paragraphs; also, as decided above, `LocaleShardReader.cs`, `ShardCache.cs`,
  `TranslationFiles.cs`, the stereotype call in `BuiltinRules.cs`, the translation field in `openapi.yaml`, the
  editor's `l10n` labels, and the `extension.json` description (2.3) with its copy. Tests: `LocalizationTests`,
  `LocalizationRuleTests`, `LocalizationHelperTests`, `McpLocalizationToolTests`. Verify:
  `E "~CatalogTests|~Localization|~Json."`; `C "~McpSurfaceTests|~McpLocalizationToolTests|~L10nAndSeedCommandTests"`;
  `F "~LocalizationEndpointTests"`; `record`; `editor`; `copies`.
- **B13. `tags-to-property`.** Runs after B7, and so after C6. Owns: `ModelStore.Tags.cs`
  (`TagsToProperty`, `IsTagOperation`), `ModelStore.Schemas.cs` (the tag branch only), `ModelStore.cs`
  (`BatchOp.TagsToProperty`, `BatchOperation.Prefix`), `BatchParser.cs`, `batch.json` with its copy, `openapi.yaml`,
  the `apply_batch` text and its `docs/mcp.md` row, and the user-guide paragraph. Tests: `TagsToPropertyTests`.
  Verify: `E "~TagsToPropertyTests|~TagTests|~ModelStoreBatchTests|~Json."`; `C "~McpSurfaceTests"`;
  `F "~ContractTests"`; `editor`; `copies`.

### 4.5 Rationale, decision and status on every element

**Decisions taken here**

- **On `ElementBase`.** Elements get the three fields, and so do the `ElementBase` sub-elements: attributes, enum
  members, columns, database schemas and categories. A deprecated attribute or column is the commonest case.
- **Process nodes are left out.** They are not `ElementBase` (`Processes.cs:11-15`), and their process holds the
  rationale. Adding them is a follow-up if asked.
- **`rationale`** is Markdown with no sidecar.
- **`decision`** is a URL or a reference such as `ADR-0042`. It is not an element id, and it is not checked, because
  it links to the team's tracker (3.11).
- **`status`** is one of the four values requirements use (3.10). It has no default.
- **MQ2022 warns on a reference to a retired element.** It is cheap, because the index walk already visits every
  `ElementBase`. It is useful, because retiring means finding the last users.
- **What MQ2022 does not count.** Membership (`package` and `parent` fields) and references from diagrams, as for
  layering (4.3): retiring a package does not warn on each member, and a diagram only shows. Owning references
  (`ReferenceInfo.Owning`) do not count either: retiring a database does not warn once per migration fact or other
  owned child.
- **No warning for `deprecated` targets, or for retired referrers.**
- **`get_element`** returns the fields unchanged, because it returns the whole document.
- **`status` is the element's status.** A migration fact carries it like every kind; the fact's own progress is its
  `state` (3.2), a different key and member.
- **Coverage and fact recording.** B1 adds `rationale`, `decision` and `status` to `ResolvedCoverageTests.Pending` for
  every target; B2 removes the entries its `RAnnotated` members cover and keeps, with a reason, those of targets whose
  resolved type is not an `RAnnotated` (categories among them). The three fields never reshape a database, so B2 adds
  them to 3.2's skip list, at the top level and inside each sub-element, since sub-elements carry them too.
- **Recordings.** The editor API's element bodies serialize the records, so B2's `record` also rewrites
  `element-invoice.json`, `element-contains.json` and `diagram-billing-overview.json` under
  `src/editor/src/mocks/recorded/`: each element and sub-element gains `rationale`, `decision` and `status` as null.
  Those rewrites are intended, with `validation-rules.json`.

**Model and schema**

`common.json` gains `$defs.status`. The keys go right after `description` in `x-order`, in every kind schema and in
the attribute (`common.json`), member (`enum.json`), column (`table.json`), schema (`database.json`) and category
(`category-tree.json`). Absent keys write nothing, so existing files stay byte-identical.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `rationale` | string, `minLength` 1 | absent | Markdown: why it is shaped this way |
| `decision` | string, `minLength` 1, `maxLength` 2000 | absent | URL or reference of the decision record |
| `status` | `$defs/status` | absent | `proposed`, `approved`, `deprecated`, `retired` |

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
[JsonConverter(typeof(JsonStringEnumConverter<ElementStatus>))]
public enum ElementStatus
{
    [JsonStringEnumMemberName("proposed")] Proposed, [JsonStringEnumMemberName("approved")] Approved,
    [JsonStringEnumMemberName("deprecated")] Deprecated, [JsonStringEnumMemberName("retired")] Retired,
}
public abstract record ElementBase
{   // existing members, plus:
    public string? Rationale { get; init; }
    public string? Decision { get; init; }
    public ElementStatus? Status { get; init; }
}
namespace Maquettiste.Engine.Resolution;
public abstract class RAnnotated : RObject
{   public string? Rationale { get; internal set; }
    public string? Decision { get; internal set; }
    public string? Status { get; internal set; }  // the JSON value
}
```

**Engine behaviour**

- **Resolution.** `FillAnnotations` and `FillPhysicalAnnotations` (`ResolveRun.Conceptual.cs:792-844`) copy the
  fields, so templates can read them.
- **Index.** `DocumentWalk.WalkElement` (`ModelIndexer.cs:580-584`) records the element's own status, and
  `WalkObject` (`:669-679`) each sub-element's. `WalkElement` does not pass the element through `WalkObject`, so both
  are needed. Retired ids are kept with each document's walk, so patches keep them. The internal
  `ModelSnapshot.IsRetired(string id)` reads them.
- **MQ2022.** The new `Validation/StatusRules.cs` checks `ReferencesFrom(id)` per document. It reports MQ2022 at each
  reference to a retired id, unless `FromId` or `FromElementId` is itself retired, the field is `package` or `parent`,
  the reference is owning, or the referrer is a diagram.

**CLI and MCP**

No tool or verb is added. `get_element`, `get_elements` and `model export` carry the fields, because they return
documents, and `validate` reports MQ2022. `get_resolved_model` does not carry them in phase 1: its flat records
(`Editor/ResolvedRecords.cs`) list their fields positionally, and adding three to each record is a public change this
milestone does not need. Follow-up: rationale, decision and status on the resolved records, if asked.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ2022 | W | A reference to a retired element or sub-element from one that is not retired (membership, owning references and diagrams excepted). | Repoint it, or retire the referrer |

**Docs**

New user-guide section "Rationale, decision and status". The modeling skill tells agents to read `rationale` and
`status` before they edit. Schemas and their copies.

**Tests**

- `SchemaConsistencyTests` (existing).
- `CanonicalJsonTests.Absent_rationale_decision_and_status_keep_files_byte_identical`.
- `McpReadTests.Get_element_returns_rationale_decision_and_status`.
- `ElementStatusTests` (new): `A_reference_to_a_retired_element_is_MQ2022_unless_the_referrer_is_retired`,
  `A_retired_column_used_by_an_index_warns`, `A_retired_top_level_element_warns_its_users`,
  `Members_of_a_retired_package_and_diagrams_do_not_warn`, `An_owning_reference_to_a_retired_element_does_not_warn`.
- `FactRecordingTests.Rationale_decision_and_status_edits_resolve_no_database` (B2).
- `ResolverBehaviorTests.Rationale_decision_and_status_reach_templates`.

**Tasks**

- **B1. Common fields in the schemas.** Runs first. Owns: `ElementBase.cs`, `ElementStatus.cs` (new), `common.json`
  (`$defs.status` and the attribute) and every kind schema, `enum.json`, `table.json`, `database.json` and
  `category-tree.json`, with their copies, and `ResolvedCoverageTests` (`Pending`). Tests: `SchemaConsistencyTests`,
  `CanonicalJsonTests`, `McpReadTests`. Verify: `E "~Json.|~ResolvedCoverage"`; `C "~McpReadTests"`; `P "~GoldenTests"`;
  `editor`; `copies`.
- **B2. Resolution, retired ids and MQ2022.** Runs after B1. Owns: `ResolvedCore.cs`, `ResolveRun.Conceptual.cs`,
  `ModelIndexer.cs` (retired ids), `ModelSnapshot.cs` (`IsRetired`), `StatusRules.cs` (new), `ModelValidator.cs`,
  `RuleCatalog.cs` (MQ2022), `CatalogTests.cs` (+1), `ResolvedCoverageTests`, the skip list in
  `ModelStore.Migrations.cs`, and the user-guide section. Tests: `ElementStatusTests`, `ResolverBehaviorTests`, the B2
  `FactRecordingTests` case. Verify: `E
  "~CatalogTests|~ElementStatusTests|~Resolution.|~ModelIndexerPatchTests|~FactRecordingTests"`; `P "~GoldenTests"`;
  `record`.
- **B14. Milestone close.** Runs last. Owns: `docs/guides/typed-properties-and-layers.md` (new; the showcase story,
  numbered steps, "See also"), its `mkdocs.yml` line, `docs/releases.md` under `## 0.11.0 (unreleased)`, and
  `skills/maquettiste-modeling/SKILL.md`. The skill covers `x-ref` ids, stereotype rules, `dependsOn`, status,
  `names-to-ids` and `tags-to-property`. It also checks that the `docs/mcp.md` rows the tasks changed match
  `tools.json` and `ModelTools`. Verify: `C "~SkillRefreshTests|~AgentSetupTests|~McpSurfaceTests"`, where the skill
  hash changes on purpose; `E "~CatalogTests"`, where the pin already holds the 16 rules the tasks added; `copies`.

## 5. Milestone C: agents and generation

Agents are a main way teams change the model, so this milestone fits the MCP and CLI surfaces to them: a batch names
what it touches instead of carrying ids, the engine mints every id a change needs, and large answers become summaries
with sections or pages on request (5.1, 5.2; scope 7.1, 7.5). Sections 5.3 to 5.7 cover generation. Everything lands in
the engine, the CLI and MCP; the editor keeps ids and its own endpoints.

### 5.1 Batches by name, with ids minted by the engine

**Decisions taken here**

1. Names and placeholders exist only in a request; files stay id-based (scope 7.1; every link is an id). Update and
   delete keep `expectedHash` (`batch.json:129`): a name changes how the target is written, not the conflict rule.
2. A name may stand exactly where the reference walker finds a reference (`Validation/ReferenceWalker.cs:41`:
   `[ElementRef]` properties, keyed references as a whole value, type refs, id-keyed maps such as `storage`) and in the
   id fields of `batch.json`: `id`, `database`, `package`, `schema`, `target`, `default`, the items of `entities`,
   `tables` and `attributes`, and the keys of `expectedHashes`. MQ2001, `get_references` and the delete refusal use the
   same sites, so "an id field" has one definition. Two values stay as they are. `columns` takes keys or physical names
   already, and physical names are data. An `attributes` item in the composite form `attributeId.memberId` stays ids,
   because a name path uses `.` itself; a single attribute or end in `attributes` may be a name. Free text, seed cells
   and fields that take physical names are data and are never rewritten. Sites that 4.1 types with `x-ref` take names
   as soon as its walker yields them. A migration fact's keys and `after` take ids only (3.2): they are history, not
   links.
3. Names match stored names, never resolved ones (conventions, prefixes), so a lookup never resolves the model. A
   synthesized overlay has no stored name and is reached by id. In billing the Invoice table is such an overlay, so the
   scope's `@table:billing.invoice` stands for a designed table; the acceptance test creates one.
4. An update's or delete's `id` names an element that exists before the batch: an element appears in one operation only
   (`ChangePlanner.cs:928`). So that `id` resolves on the model as loaded. Every other name sees the model as the
   earlier operations leave it, with the operation's own element as it will be written. The element's old entries go
   before its sites resolve, so a primary key names its own new column, and a name swapped inside one update finds its
   new holder. Only the sites that the element's own forms read (its package, database, schema and the like) resolve
   before that, as they name other elements; a category parent that the same operation adds or renames is reached by its
   placeholder or id. An operation replaces the entries of the element it writes and of its sub-elements, and nothing
   else. A form that goes through an element renamed earlier in the batch (a package path, a database or schema, a
   table, an owner) is not rebuilt for untouched elements, which keep their old qualifier: use the id or a second batch.
   Placeholders are batch-wide, so two new tables can reference each other, as no later update can close the cycle.
   Later names see creates, updates and deletes, not what schema, materialize, process and tag operations produce (add a
   schema by updating the database element, or use two batches).
5. Every object whose schema requires an `id` gets one when it has none, except an update's root. The planner takes
   an update's root from the resolved operation `id`, as today (`ChangePlanner.cs:256-263`). A create's root is the
   element's `id`, else the operation's `id` (`:199-211`), else a new id. The resolver mints before it resolves, so the
   result reports the ids; the planner mints for every other write (`CreateAsync`, `SaveAsync`). Order: operations in
   order; in an element, the root first, then keys in `x-order`, array items by index, an object before its children.
   Ids come from `EngineOptions.EffectiveIdGenerator`, as creates do today, so a deterministic generator gives
   byte-identical files. A sub-element without an id in an update is new; the engine never matches by name. It does
   refuse a silent replacement, though. An update that drops a sub-element and adds one without an id, under the same
   parent and property and with the same name, is MQ1014 ("keep its id"). Without the rule, the drop would also remove
   the seed columns and cells that name the old id (`ChangePlanner.cs:286-291`).
6. The code holds the scope's "Today" text and adds one fact: the assistant retries with ids taken from MQ1002 messages
   (`Agent/AssistantProposals.cs:170-190`). That loop goes. The proposal resolves update and delete targets before it
   fills their hashes, and pins resolved ids. No editor screen is needed: the editor writes ids, and names reach the
   assistant through `propose_changes`.
7. The kinds that 4.3 gives a package and that otherwise have only a `name` form (database, mapping, reference type,
   seed, actor) also take `<package path>.name` when their file states a package, so 5.3's `seed_named` and batches
   read one definition. `migration` (3.2) is listed by id only: a recorded fact has no name.
8. `NameIndex` is immutable once built and sits in a lazy member with `LazyThreadSafetyMode.ExecutionAndPublication`,
   because templates (5.3) and rule engines (5.7) read it in parallel. A batch's overlay is private to that batch and
   never changes the shared index.
9. Result records gain init properties only, and each is stated once in its final form after milestones A and C:
   `BatchResult` and `BatchPreview` here, with 3.2's `Facts`; `BatchOperation` in 4.1. A dry run carries the facts it
   would record (3.2) through the same sink as its files.
10. A package path in a form is the path from a root package or any trailing part of it, so `@entity:Catalog.Product`
    finds Product in `Billing.Catalog`, as the C4 rename test needs; the longest form is the full path. A package's own
    forms follow the same rule. (Taken in C3.)
11. A row of a reference-type seed is named by its cell in the seed's `code` column, as `ReferenceDataIndex.CodeKey`
    reads it: rows have no `code` key. The "keep its id" check (MQ1014) reads the same cell. (Taken in C2.)
12. `IdMinter.Fill` reports operation 0 and no name; the resolver (C4) sets the operation index and the longest form.
    The `as` key, when the object still carries it, fills `Placeholder`. (Taken in C2.)
13. `MintedId.Name` and the candidates MQ1013 lists are written as qualified names, `@kind:` then the longest form
    (`@column:main.billing.payment_method.code`), so an agent can paste them into a later batch. An `as` key is read
    only on an object whose schema declares keys and not `as`; in a free-form value (custom properties, scenario
    values) it is data. Two keys of an id-keyed map that name one element are MQ1014, like two `expectedHashes` keys.
    (Taken in C4.)
14. A dry run validates `before` over the keys of `plan.ChangeByElement`, the placements and the migration facts that
    follow a deleted fact, each that `before` has, with their referrers. It reuses the refusal baseline when the change
    deletes nothing. The validator widens each scope with conflict and row peers worked out on its own model, so the two
    runs can reach different documents: a peer of a created seed or name only after the change, a peer of a deleted one
    only before. Each side therefore also validates the documents that only the other run reached and that it has
    (`ModelValidator.TargetIds` gives a run's documents) and keeps their own findings, so both reports cover the same
    documents and an element that one run alone reached does not show as added or removed. (Taken in C5, corrected in
    its review.)
15. The proposal resolves its targets before it parses the batch, because an update without its `expectedHash` does not
    parse: `BatchNames.ResolveTargets` reads a stand-in batch built from each raw operation's `op` and `id`. A
    placeholder target goes through it too, so it fails with MQ1015 rather than with a missing hash. After the preview,
    an update's or delete's `id` is the target pinned before it, and a create's is its element's id, else the
    operation's, else the root id `Ids.Minted` gives for that operation: the items do not line up with the operations
    when the batch holds expanded operations, so the pin never reads them. (Taken in C7.)
16. openapi-typescript renders the root `$defs` of `batch.json` (the name grammar, C1) as a required member of the batch
    body's type, so the editor's `BatchRequest` omits `$defs` and `applyBatch` casts back to the body type; the
    assistant's unit-test fixtures and its mock proposal gain `diff`. (Taken in C8.)
17. The "keep its id" check (MQ1014) keys a reference-type seed row's code by `ReferenceDataIndex.CodeKey`, as
    `NameIndex` keys rows, so the string `"1"` and the number `1` are one code.
18. `docs/api/openapi.yaml` lists `BatchResult.ids` under `properties` but not under `required`, like `facts`, so editor
    code that builds `BatchResult` objects keeps compiling. The engine always writes it.

**Model and schema**

`schemas/v1/batch.json` and its byte-identical copy `samples/reference-app/.maquettiste/.schema/v1/batch.json`; the
operation's `x-order` gains `as` after `id`. Kind schemas do not change.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `id` | `#/$defs/idOrName` | | an id, a qualified name or a placeholder; a create's `id` must be an id, and an update's or delete's `id` must not be a placeholder (MQ1015) |
| `as` | `#/$defs/placeholder` | | create and update: names the operation's element for the whole batch |
| `database`, `package`, `schema`, `target`, `default` | `#/$defs/idOrName` | | as `id` |
| `entities`, `tables` | array of `#/$defs/idOrName` | | as `id` |
| `attributes` | array of strings (unchanged) | | an item that starts with `@` or `$` is one attribute or end; the composite `attributeId.memberId` stays ids |
| `expectedHashes` | object (unchanged) | | a key may be a qualified name, resolved in pass 2 like `entities`; a placeholder key is MQ1015; two keys for one element are MQ1014 |

New `$defs`: `placeholder` (`^\$[A-Za-z_][A-Za-z0-9_-]*$`), `qualifiedName` (`^@[a-z][a-z-]*:.+$`), `idOrName` (`anyOf`
the common id and both). Inside `element`, an object the kind schema gives a required `id` may carry `as`; the resolver
removes it before the kind schema runs. `columns` keeps its meaning (keys or physical names).

Grammar. A string at a name site that starts with `@` or `$` is read as below; no id starts with either (ULIDs start
with `0` to `7`, synthesized keys with an id). Names compare ordinally, as written in the files.

```text
reference   = "@" kind ":" path
placeholder = "$" ( ALPHA / "_" ) *( ALPHA / DIGIT / "_" / "-" )
path        = segment *( "." segment )
segment     = 1*( any char but "." DQUOTE white-space ) / DQUOTE *( any char but DQUOTE / 2DQUOTE ) DQUOTE
```

Each kind registers the forms below, and a name finds what is registered under exactly that form. A bare name is
unique or ambiguous (MQ1013); the longer forms qualify it. A `<package path>` is the full path or any trailing part of
it (decision 10). A form with an empty segment (no stored name, no schema and no default schema) is not registered.
When every form of a target is shared with another, the id is the way. One example: an attribute of a reference type
named like a root-package entity. MQ1013 then lists each candidate's longest form and id.

| Kind | Forms (billing and invented examples) |
| --- | --- |
| `package` | `Name`, or the path from a root package or a trailing part of it (decision 10): `@package:Billing.Catalog` |
| `entity`, `value-object`, `scalar-type`, `enum`, `process`, `relation`, `diagram` | `Name`, `<package path>.Name`: `@entity:Billing.Invoice` |
| `database`, `mapping`, `reference-type`, `seed`, `actor` | `name`, plus `<package path>.name` when the file states a package (decision 7): `@database:main`, `@seed:Billing.currencies` |
| `stereotype` | the key: `@stereotype:audited` |
| `table`, `view`, `sequence`, `routine`, `database-type`, `sql-object` | `name`, `schema.name`, `database.schema.name` (schema, else the database's `defaultSchema`): `@table:billing.notes` |
| `query` | `name`, `database.name` |
| `scenario` | `name`, `<process form>.name` |
| a kind another section adds | `name`, plus `<package path>.name` when it has a package |
| `attribute` | `<owner form>.name` for an entity, value object, relation, reference type or stereotype: `@attribute:Invoice.total`, `@attribute:settles.allocated`, `@attribute:audited.createdAt`; a process's context `<process form>.name`; an event's payload `<process form>.<event>.name`; a gate's audit attributes `<process form>.<gate>.name` |
| `column` | `<table form>.name`: `@column:billing.notes.body` |
| `schema` | `name`, `database.name`: `@schema:billing` |
| `enum-member`, `event`, `guard`, `action`, `invoke`, `gate` | `<enum or process form>.name` |
| `meaning` | `<process form>.<gate>.name` |
| `state` | `<process form>.<state path>`, parent states first |
| `end` | `<relation form>.<role, else navigation>` |
| `reference-field` | `<reference type>.code`, `<reference type>.label` |
| `row` | rows of reference-type seeds: `<reference type>.<code>` and `<seed name>.<code>` (two seeds of one type, for example one per environment); integer codes in invariant digits |
| `binding` | `<entity or reference type form>.<database name>` |
| `key` | `<table form>.<name>` (unique, foreign key, check, index), `<entity form>.<name>` (alternate key) |
| `category` | names from the tree's root, in any tree: `@category:Finance.Receivables`; in a package's tree, also `<package path>.<category path>` |
| `tag-vocabulary`, `category-tree`, `migration`, `transition`, `step`, rows of table seeds, unnamed keys | none: an id or a placeholder |

**Public API**

```csharp
namespace Maquettiste.Engine;

// BatchOperation gains `string? As = null` (new), its last positional parameter when 5.1 lands; its whole record
// after 4.1 and 5.1 is stated once, in 4.1.

// New. Names: each placeholder declared and qualified name used, to its id, ordinal by key.
public sealed record BatchIds(IReadOnlyDictionary<string, string> Names, IReadOnlyList<MintedId> Minted)
{
    public static BatchIds Empty { get; }
}

// New. Pointer into the operation's element ("" for its root); Kind: the element kind, else the index kind
// (ModelIndexer.SubElementKind), else "sub-element"; Name: the longest form when the kind has names (computed once
// the element's references are ids).
public sealed record MintedId(int Operation, string Pointer, string Kind, string Id, string? Name, string? Placeholder);

// BatchResult and BatchPreview in their final form after milestones A and C (decision 9).
public sealed record BatchResult(SaveOutcome Outcome, IReadOnlyList<SaveResult> Items, ChangeSet? Changes)
{
    public BatchIds Ids { get; init; } = BatchIds.Empty;                      // new; empty unless saved
    public IReadOnlyList<RecordedFact>? Facts { get; init; }                  // 3.2: the facts the batch recorded
}

public sealed record BatchPreview(SaveOutcome Outcome, IReadOnlyList<SaveResult> Items,
    IReadOnlyList<BatchPreviewFile> Files)
{
    // New; like Files, all three are empty unless the preview would save.
    public BatchIds Ids { get; init; } = BatchIds.Empty;
    public IReadOnlyList<Diagnostic> DiagnosticsAdded { get; init; } = [];    // Diagnostic.Order
    public IReadOnlyList<Diagnostic> DiagnosticsRemoved { get; init; } = [];  // Diagnostic.Order
    public IReadOnlyList<RecordedFact>? Facts { get; init; }   // 3.2: null when the change reaches every database
}

public sealed record BatchPreviewFile(string Path, string Action, string? Id, string? Kind, string? Name,
    string? BeforeHash, string? Before, string? After)
{
    public string? Diff { get; init; }                                       // new: unified, 3 lines of context
}

// Maquettiste.Engine.Json, Json/JsonContracts.cs
public sealed class ObjectLayout
{
    public bool RequiresId { get; internal set; }  // new: the schema requires id (common.json#/$defs/id)
}
```

**Engine behaviour**

1. `SchemaRegistry.LayoutBuilder.Fill` (`Json/SchemaRegistry.cs:243`) sets `RequiresId`. Combinators merge there; a
   `then` does not, so a batch operation is never a mint site.
2. `IdMinter.Fill(JsonNode element, ObjectLayout layout, IIdGenerator ids, List<MintedId>? minted)` (new, internal,
   `Loading/IdMinter.cs`) gives each `RequiresId` object below the root that has no `id` a new one, in the order of
   decision 5. It never touches the root. `ChangePlanner.Create` and `Update` call it after the root id is set
   (`Loading/ChangePlanner.cs:199-211`, `:256-263`) and before `Read`. A stale replan (`ModelStore.cs:613-626`) works
   on a fresh clone and changes nothing already reported. Each `MintedId` takes its kind from the record type the
   pointer reaches (`ModelIndexer.SubElementKind`), operation 0 and no name (decision 12).
3. `PlannedChange` (internal, `ChangePlanner.cs:17`) gains `IReadOnlyList<string>? Minted`: the pointers that pass 1
   minted in the operation's element. `Update` adds the pointers it minted itself. Before `DropSeedColumns` (`:291`),
   it refuses with MQ1014 at the new sub-element's pointer when a removed sub-element and a minted one share three
   things: the parent (the element, or the sub-element that holds them, compared by id), the property, and the name
   (`name`, or a reference-type seed row's `code` cell, decision 11). The batch fails, so nothing is written and no seed
   column is dropped. `save_element` and `patch_element` reach the same check.
4. `QualifiedName` (new, internal, `Loading/QualifiedName.cs`) parses and canonicalizes names. `NameIndex` (new,
   internal, `Loading/NameIndex.cs`) holds one kind's forms, built from typed elements on first use and cached in an
   internal lazy member of `ModelSnapshot` (`LazyThreadSafetyMode.ExecutionAndPublication`, decision 8), so a batch that
   names no table never indexes tables. The built index is immutable; a batch layers a private overlay on it. An
   operation replaces or removes the entries of the element it writes and of that element's sub-elements, and nothing
   else (decision 4). The internal surface: `ModelSnapshot.Names(kind)`; `NameIndex.Find(path)` (the candidates, ordinal
   by longest form, then id), `Get(id)`, `EntriesOf(elementId)` and `Of(element, lookup, kind)`, which gives an element
   file's entries with qualifiers from other files read through `lookup`, for the overlay; `NameEntry(Kind, Id,
   ElementId, Forms, Longest)`; `NameIndex.NamedKinds` and `IdOnlyKinds`, which `QualifiedName.TryParse` checks.
5. `BatchNames` (new, internal, `Loading/BatchNames.cs`) runs first in `ApplyBatchCoreAsync` (`ModelStore.cs:223`, made
   async) on `LoadedAsync`'s snapshot, before operations are planned or expanded (`ModelStore.Schemas.cs:23`).
   - Pass 1, every operation in order. An update's or delete's `id` that is a qualified name resolves on the model as
     loaded (`BatchNames.ResolveTargets`, internal); a placeholder there is MQ1015. A create's root gets its id: the
     element's `id`, else the operation's `id`, else a minted id written into the element. Then `IdMinter.Fill` mints
     the ids below the root; an update's root is left to the planner. Each `as` is recorded as a placeholder (the
     operation's on its root id, an object's on that sub-element's id) and its key removed. A duplicate is MQ1014, as
     is `as` on a delete or an expanded operation, or on an object that is not a mint site.
   - Pass 2, in order. The operation's other `batch.json` id fields resolve on the overlay as the earlier operations
     left it. The element is read into its typed record (`ElementReader.ReadElement`; names are strings, so it reads).
     First come the sites the element's own forms read: package; database and schema; a query's database; a scenario's
     process; a binding's database; a seed's target; a category's parent (a parent new in the same operation is
     reached by its placeholder). Then the overlay drops the element's old entries and adds those of its new
     document. Then every other `ReferenceWalker` site whose value starts with `@` or `$` resolves (a map key is
     renamed). A delete removes its entries at its turn. A value that does not resolve is MQ1012; malformed is MQ1015;
     ambiguous is MQ1013, listing each candidate's longest form and id. A record that does not read skips pass 2, and
     the planner reports its schema errors (MQ1002) as today.
   - Any MQ1012 to MQ1015 makes those operations `invalid`, with pointers under `/operations/<i>`, and nothing is
     written. Otherwise the batch goes on with ids only, each operation carrying its `Minted` pointers, and its result
     carries `Ids`. A batch with no `as` and no value starting with `@` or `$` skips pass 2; minting still runs, and its
     elements still reach the overlay in order so that each minted id gets its longest form.
6. The resolver runs outside the store's gate on the snapshot it read, as schema-operation expansion does; a change in
   between is caught as today (hashes checked on disk, MQ2001 for a target that went). The resolved batch reaches
   `ExecuteCoreAsync` (`ModelStore.cs:599`), so section 3.2's migration classifier sees batch writes like any other.
7. Dry run. An internal `BatchPreviewSink` (files, diagnostics added, diagnostics removed, facts) replaces the
   `List<BatchPreviewFile>` that `PreviewBatchAsync` passes down today (`ModelStore.cs:216-220`), through
   `ApplyBatchCoreAsync` and `ExecuteCoreAsync`. `PreviewFilesAsync` (`:681`) fills `Diff` with
   `EngineServices.Diffs.Unified`. With a sink, `ValidateAsync(plan, before)` (`:760`) keeps its candidate report and
   its refusal baseline (`known`, `:779`) exactly as today, so refusals do not change. It also validates `before` over
   the change's scope: the keys of `plan.ChangeByElement` (deletes included, unlike `plan.ChangedIds`,
   `ChangePlanner.cs:177`), their former referrers and the placements, each that `before` has. The early return when
   nothing changed (`:768`) counts the deletes too when a sink is present. Each side also validates the documents that
   only the other side's run reached, so both cover the same documents (decision 14). The two reports are
   compared as multisets keyed like `ErrorKey` (`:900`: rule, element, pointer), every severity, so a deleted element's
   diagnostics show as removed. The scope is the apply's, never the whole model. An invalid dry run answers its errors
   in `items`, with no files, no ids and an empty delta. Facts (3.2) are recorded after `ValidateAsync`; the sink takes
   them, and the MQ4101 of each pending fact the change would record joins `DiagnosticsAdded`.
8. `AssistantProposals` (`Agent/AssistantProposals.cs:85-160`) drops `FillMissingIds`, its retries and the create id
   fill (`:92`); the engine mints. Before it fills a missing `expectedHash` (`:94-96`), it resolves each update and
   delete `id` that starts with `@` through `BatchNames.ResolveTargets`, pins the resolved id, and reads the hash from
   that document. A name that does not resolve is returned like a parse failure, with its MQ1012 to MQ1015
   diagnostics. After the preview it pins each operation's `id` to the id it wrote (decision 15), its other id fields
   through `Ids.Names`, and its element to the previewed document, and drops `as`. Proposals carry `Diff` like any
   preview.

**CLI and MCP**

- `maquettiste model apply <batch.json> [--dry-run] [--format text|json]`. The file holds `{ "operations": [...] }` or
  the bare array, read with `CliFiles.ReadAsync`; then `ParseBatch` and `ApplyBatchAsync` or `PreviewBatchAsync`
  (`ChangeSource.Cli`). Text on stdout: a line per written or deleted path, then `name  id` per `ids.names` entry; with
  `--dry-run`, the diffs, the names, then `+ ` and `- ` diagnostic lines (`DiagnosticOutput.Line`). JSON: the
  `apply_batch` answer. Exit codes (`Program.ExitCodes`): saved 0; conflict 3; parse error, invalid, not-found or
  referenced 1. Model files are written only through the store's atomic path (`AtomicFileSet`, `IOutputPathPolicy`).
- MCP `apply_batch` (`Mcp/ModelTools.cs:156`) gains `dryRun` (default false); its description covers names, placeholders
  and minting. The answer adds `ids` (`{ names, minted }`) and `facts` (3.2). A dry run answers `{ outcome, items,
  files: [{ path, action, id, kind, name, beforeHash, diff }], diagnosticsAdded, diagnosticsRemoved, ids, facts }`
  (`facts` null when the change reaches every database) without the full texts, through one projection
  (`Commands/BatchOutput.cs`) that `model apply --format json` shares. Failures keep `FromOutcome`.
- `CliApp.Usage` lists `model apply`; the server instructions (`Mcp/McpServerSetup.cs:24-37`) mention names and
  `dryRun`. No tool is added here.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ1012 | error | A name or placeholder in a batch finds nothing. No element or sub-element of that kind has the name (an update's or delete's target on the model as loaded; any other name on the model as the earlier operations leave it, with the operation's own element as it will be written), or no operation declares the placeholder. A form through an element renamed earlier in the batch keeps the old qualifier: use the id or a second batch. | |
| MQ1013 | error | A name in a batch finds more than one element or sub-element. Use a longer form (package, database, schema, owner or seed), or the id when every form is shared; the message lists each candidate's longest form and id. | |
| MQ1014 | error | A write misuses an identity: a placeholder declared twice; `as` on a delete, a schema, materialize, process or tag operation, or on an object that is not a sub-element; two `expectedHashes` keys for one element; or an update that drops a sub-element and adds one without an id under the same parent and property with the same name (send the existing id to keep it). | |
| MQ1015 | error | A name in a batch is malformed or misplaced: not `@kind:path` or `$name`, an unknown kind or one without names, an empty or unclosed quoted segment, a name as a create's id (declare a placeholder with `as`), or a placeholder where the element must exist before the batch (an update's or delete's id, an `expectedHashes` key). | |

**Docs**

- `schemas/v1/batch.json` and its sample copy.
- `docs/mcp.md`: the `apply_batch` row, and a "Batches by name" section after "Semantics" with the grammar, the forms,
  the minting order, the "keep its id" refusal and MQ1012 to MQ1015. That section is the rules' documentation home.
- `docs/user-guide.md`: `model apply` in the command table.
- `docs/api/openapi.yaml`, then `npm run gen`. `BatchOperation` (x-engine-record) gains `as`, and `id` and the other
  id fields say "an id, a qualified name or a placeholder". `BatchResult` gains `ids`; new schemas `BatchIds` and
  `MintedId`. `AssistProposalFile` (x-engine-record `BatchPreviewFile`) gains `diff` (`[string, 'null']`, required, as
  every property is written).
- `docs/guides/work-with-an-agent.md`, step 4: one batch by name on the guide's showcase model. Example: table
  `workshop.tool_kinds` with columns `code` and `label`, reference type `ToolKind` bound to it, and its seed, all in
  the Workshop domain and database `main`. The guide keeps its own story; billing stays in tests and this design.
- `skills/maquettiste-modeling/SKILL.md`, step 3: names, placeholders, minting, `dryRun`, and "keep the ids of
  sub-elements you re-send". The fallback's step 1 (line 123) stops asking for ULIDs and points to
  `maquettiste model apply`, which mints them.
- `docs/releases.md` under "## 0.11.0 (unreleased)".

**Tests**

Engine test files for this section live in `tests/Maquettiste.Engine.Tests/Store/` (beside `BillingStore`, in
`Store/StoreTestSupport.cs`), except `SchemaConsistencyTests` (`Json/`) and `AgentToolsTests` (`Editor/`). The CLI
tests are `tests/Maquettiste.Cli.Tests/ModelApplyTests.cs` and `McpSurfaceTests` in `McpServerTests.cs`.

- Acceptance (the done-when line), on `BillingStore.OpenAsync` (a temporary copy of billing):
  `BatchNamesTests.An_agent_creates_a_table_its_columns_a_reference_type_and_a_seed_in_one_batch_without_writing_an_id`.
  One batch creates table `payment_method` (`as $pm`, `@database:main`, `@schema:billing`, columns `code` and
  `label`, primary key `@column:billing.payment_method.code`), reference type `PaymentMethod` (`as $type`, binding
  table `$pm`, fields by physical name) and seed `PaymentMethods` (target `$type`, two rows). Asserted: saved; no `@`
  or `$` value in a written file; 10 minted ids; `ids.names` holds both placeholders and the three names; no new
  error; byte-identical files over two runs with the sequential generator.
- `BatchNamesTests`: `Names_see_the_earlier_operations_and_the_operation_itself`,
  `Update_and_delete_targets_resolve_on_the_model_as_loaded`,
  `A_name_inside_the_operations_own_element_sees_its_new_document` (one update of `notes` swaps the names of
  `entity_type` and `entity_id`, ids kept, and adds an index on `@column:billing.notes.entity_type`; the index holds
  `01K6BND0000000000000000004`), `A_rename_earlier_in_the_batch_does_not_requalify_untouched_elements` (operation 0
  renames package Catalog to Catalog2; in operation 1, `@package:Billing.Catalog2` resolves, `@entity:Catalog2.Product`
  is MQ1012, and `@entity:Catalog.Product` still finds Product),
  `Placeholders_reach_forward_so_two_new_tables_reference_each_other`,
  `Attributes_items_and_expected_hashes_keys_take_names`, `An_unknown_name_is_MQ1012_and_nothing_is_written`,
  `An_ambiguous_name_is_MQ1013_listing_the_candidates`, `A_placeholder_declared_twice_or_misplaced_as_is_MQ1014`,
  `A_malformed_name_a_named_create_id_or_a_placeholder_target_is_MQ1015`,
  `Text_and_seed_cells_that_look_like_names_are_left_alone`.
- `IdMintingTests`: `Create_and_save_mint_missing_sub_element_ids`, `Minted_ids_follow_operation_and_document_order`,
  `A_create_takes_its_operation_id_and_an_update_its_target_as_the_root` (both pass today and must keep passing),
  `A_new_sub_element_without_an_id_in_an_update_gets_one`,
  `Resending_an_entity_without_attribute_ids_is_MQ1014_and_drops_no_seed_column` (a temporary billing copy adds an
  entity seed of `Customer`; the update re-sends `Customer` with no attribute ids; asserted: invalid, MQ1014 at each
  attribute, the seed file unchanged).
- `QualifiedNameTests`: `Parses_and_canonicalizes_quoted_segments`; `Billing_names_resolve_to_their_ids`, a row per
  form and one per attribute owner (`Invoice.total`, `Money.amount`, `settles.allocated`, `audited.createdAt`);
  `Process_attribute_names_resolve` (a temporary billing copy adds a process with a context attribute, an event
  payload and a gate audit attribute); `Rows_and_categories_take_their_longer_forms` (two seeds of one reference type;
  one category path in the global tree and in a package's tree);
  `Every_kind_and_index_kind_has_forms_or_is_listed_as_id_only` (walks `KindInfo.All` and the index kinds;
  `migration` is id only); `Packaged_kinds_take_their_package_form` (a seed, a database and a reference type with a
  `package`, from 4.3).
- `BatchPreviewTests`: `A_dry_run_returns_diffs_and_the_diagnostics_the_change_adds_or_removes`,
  `A_dry_run_of_a_delete_lists_its_vanished_diagnostics_as_removed`,
  `An_invalid_dry_run_returns_errors_in_items_and_an_empty_delta`,
  `A_dry_run_lists_the_facts_it_would_record_and_their_MQ4101_as_added` (a narrowing of `invoice.number`).
- `SchemaConsistencyTests.Layout_marks_objects_that_require_an_id`;
  `AgentToolsTests.A_proposal_by_name_is_pinned_to_ids` (a create by placeholder, plus an update by name with no
  `expectedHash`; asserted: pinned id and hash).
- CLI `ModelApplyTests`: `Model_apply_writes_a_batch_by_name_and_prints_the_ids`,
  `Model_apply_dry_run_prints_diffs_and_writes_nothing`, `Model_apply_exits_3_on_a_conflict_and_1_when_invalid`;
  MCP `McpSurfaceTests.Apply_batch_by_name_previews_then_applies`.
- The catalog count in `CatalogTests.Rule_catalog_ids_are_unique_sorted_and_well_formed` grows by 4, and
  `src/editor/src/mocks/recorded/validation-rules.json` is recorded again.

**Tasks**

Legend in section 2; order in section 6. Sections 5.1 and 5.2 change no DDL or template, so no golden update and no
database container run.

- **C1. Schema, records, rule ids**, no behaviour. Files: `schemas/v1/batch.json` and its copy, `ModelStore.cs`
  (records), `Loading/BatchParser.cs` (`as`), `Json/JsonContracts.cs`, `Json/SchemaRegistry.cs`,
  `Diagnostics/RuleCatalog.cs`. Tests: `Layout_marks_objects_that_require_an_id`,
  `ModelStoreBatchTests.ParseBatch_reads_as_and_names`, the catalog count. Verify: `E
  "~SchemaConsistencyTests|~CatalogTests|~ModelStoreBatchTests"`; `record`.
- **C2. Minting and the "keep its id" refusal.** Files: `Loading/IdMinter.cs`, `Loading/ChangePlanner.cs`
  (`PlannedChange.Minted`, the MQ1014 check). Tests: `IdMintingTests`. Verify: `E
  "~IdMintingTests|~ModelStoreBatchTests|~ModelStoreTests"`.
- **C3. Qualified names** (after B3, which gives seeds and the other kinds their `package`). Files:
  `Loading/QualifiedName.cs`, `Loading/NameIndex.cs`, `Model/ModelSnapshot.cs` (internal member). Tests:
  `QualifiedNameTests`. Verify: `E "~QualifiedNameTests"`.
- **C4. Resolver.** Files: `Loading/BatchNames.cs`, `ModelStore.cs`. Tests: `BatchNamesTests` with the acceptance
  test. Verify: `E "~BatchNamesTests|~IdMintingTests|~ModelStoreBatchTests|~Integration"`.
- **C5. Dry-run diff and diagnostics delta.** Files: `ModelStore.cs` (`BatchPreviewSink` with facts, preview branch,
  `PreviewFilesAsync`, `ValidateAsync`). Tests: `BatchPreviewTests`, existing `AgentToolsTests`. Verify: `E
  "~BatchPreviewTests|~AgentToolsTests|~ModelStoreBatchTests"`.
- **C6. MCP and CLI.** Files: `Mcp/ModelTools.cs`, `Mcp/McpServerSetup.cs`, `Commands/ModelCommand.cs`,
  `Commands/BatchOutput.cs` (`ids`, `facts`), `CliApp.cs`, `docs/mcp.md`, `docs/user-guide.md`. Tests:
  `ModelApplyTests`, `Apply_batch_by_name_previews_then_applies`. Verify: `C
  "~ModelApplyTests|~McpSurfaceTests|~ParserAndGlobalsTests"`.
- **C7. Assistant pinning.** Files: `Agent/AssistantProposals.cs`, `Loading/BatchNames.cs` (`ResolveTargets` made
  callable on its own). Tests: `A_proposal_by_name_is_pinned_to_ids`. Verify: `E "~AgentToolsTests"`;
  `F "~AssistTests"`.
- **C8. Contract, skill, guide, release note.** Files: `docs/api/openapi.yaml`, generated
  `src/editor/src/api/schema.d.ts` and `src/editor/src/mocks/openapi.json`, `src/editor/src/api/types.ts` and
  `endpoints.ts` (`BatchRequest`, decision 16), `src/editor/src/mocks/assist.ts` and `tests/unit/assistant.test.ts`
  (`diff`), `skills/maquettiste-modeling/SKILL.md`, `docs/guides/work-with-an-agent.md`, `docs/releases.md`. Tests:
  `ContractTests`, `SkillRefreshTests`, `AgentSetupTests`. Verify: `editor`; `F "~ContractTests"`; `C
  "~SkillRefreshTests|~AgentSetupTests"`.
- **C9. Pre-release note** (scope 16.5). File: `tmp/response-next-capabilities-note-batches-by-name.md`, a short
  anonymous note the owner sends: what changed, one batch by name on billing (the acceptance batch), how to dry-run
  it, and the "keep its id" rule. Billing and invented names only. Tests: none. Verify: the owner reads it before
  sending.

### 5.2 MCP answers that fit an agent's context

**Decisions taken here**

1. One implementation: the summaries live in the engine catalog handlers (`Agent/AgentTools.Handlers.cs`), shared by
   MCP and the editor's assistant. The editor's `GET /api/project`, `/api/validation/rules` and `/api/model/tags/usage`
   have their own handlers and do not change.
2. A summary by default; full content on request: sections for `get_project`, filters and pages for
   `list_validation_rules`, pages and one tag's element pages for `tag_usage`. Pages reuse the opaque keyset cursor of
   `ModelPages.Page` (`Editor/ModelPages.cs:226`; default 100, at most 1000), keyed by rule id or tag key.
3. `patch_element` uses a small in-house RFC 6902. The engine keeps four package dependencies and has its own RFC 6901
   helpers (`Json/JsonPointer.cs`); the six operations over `JsonNode` are about 200 lines; a failure must be a
   diagnostic with a pointer (MQ1016); a patch library pinned beside the JSON Schema library would tie their upgrades
   together.
4. A patch is written as a one-operation batch update with the caller's `expectedHash`. That is `SaveAsync`'s path: the
   planner checks the hash against the index (`ChangePlanner.cs:950`) and `ExecuteCoreAsync` re-hashes on disk
   (`ModelStore.cs:613`). It also mints and resolves names as in 5.1, so a patch can add a column without an id. Its
   target `id` is an id, never a name: the caller has it from `get_element` with the hash.
5. `generate` runs `GenerationService.RunAsync` in apply mode (`GenerationService.cs:66`), not `PlanAsync` then
   `ApplyAsync`. `PlanAsync` stores a plan and its blobs, which the scope excludes. `RunAsync` holds the run lock once
   for render and write, so nothing can make a plan stale in between, and it renders once. CLI `generate` takes the same
   path (`Commands/GenerateCommand.cs:82`), so both write through the same writer, manifests and `IOutputPathPolicy`.
   `ReuseLastRun` stays off in the long-lived server, as `GenerationService` documents.
6. No CLI verb is added (scope 16.2). The CLI already has both behaviours: `maquettiste generate` plans and applies in
   one call, and `maquettiste model apply` with an update covers a patch. The summaries exist for an agent's context,
   which a terminal does not have.
7. Editor follow-ups, not phase 1: an HTTP `PATCH /api/model/elements/{id}` route and a one-call generate summary on
   the Generate screen, when a screen needs them.
8. `generate` takes 3.1's `against`, as `plan` does. `patch_element`'s `facts` come from `BatchResult.Facts`, since a
   patch saves as a one-operation batch and its items carry no facts.
9. The test whose rules part changes, `Project_index_element_references_database_packs_settings_and_schema`, lives in
   `McpReadTests`, not `McpSurfaceTests`; it changes there and runs beside the Verify line's filter. (Taken in C10.)
10. `PatchAsync` looks the target up as an element id: a sub-element id is `NotFound` like a name. A patch that is not
    JSON or not an array has no operation index, so its MQ1016 points at `""`. The server instructions name
    `patch_element` beside `save_element`, where the hash is explained; `generate` goes beside plan in C12. (Taken in
    C11.)
11. `generate`'s `packs` list is in ordinal pack order and leaves out a pack that changed nothing; each pack's `counts`
    holds only the kinds that occur, keyed by their JSON names. An invalid run may already have written the files of
    the units without errors (only unit-level errors such as MQ6033 let the others render), so `FromRun` takes an
    optional title for `invalid` and `generate` passes one that says so; plan and apply keep theirs. `handEdits`,
    `units` and `elements` are checked as in `plan` (a shared helper): an empty unit or element value is
    `bad-request`. The summary records live in the CLI (`Mcp/GenerateSummary.cs`, internal), not in the engine API.
    (Taken in C12.)
12. `patch_element` answers in two pointer spaces: MQ1016 in the patch, every other diagnostic in the document.
    `PatchAsync` strips `/operations/0/element` from the batch's own diagnostics (those with no file path) and turns any
    other pointer into the operation into `""`. The MCP tool passes any JSON value but null to the engine, so an object
    or a number is `invalid` with MQ1016 at `""`, not `bad-request`. (Taken in the milestone C review.)

**Public API**

```csharp
namespace Maquettiste.Engine;

// Changed: settings, pack manifests, pack diagnostics and extension schemas move behind sections; Git (always null:
// the tools never read version control) goes.
public sealed record AgentProjectInfo(string Name, int FormatVersion, string EngineVersion, string ProductVersion,
    string Build, string? Workspace, string? Branch, string? Worktree, string? Repository, string Mode,
    string SettingsHash, IReadOnlyList<ElementSummary> Databases, IReadOnlyList<AgentPackSummary> Packs,
    AgentDiagnosticCounts PackDiagnosticCounts, IReadOnlyList<string> Extensions, IReadOnlyList<string> Sections)
{
    // Each member below carries [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)].
    public ProjectSettings? Settings { get; init; }
    public IReadOnlyList<PackManifest>? PackManifests { get; init; }
    public IReadOnlyList<Diagnostic>? PackDiagnostics { get; init; }
    public IReadOnlyList<ExtensionSchema>? ExtensionSchemas { get; init; }
}
public sealed record AgentPackSummary(string Name, string Version, string? Description, int Units);
public sealed record AgentDiagnosticCounts(int Errors, int Warnings, int Info);
public sealed record TagUsageSummary(string? Package, string? Vocabulary, bool Strict,
    IReadOnlyList<TagUseSummary> Tags, string? Next);
public sealed record TagUseSummary(string Tag, bool Declared, int Uses, IReadOnlyList<string> Examples);
public sealed record TagUseElements(string? Package, string Tag, bool Declared, int Uses,
    IReadOnlyList<string> Elements, string? Next);

public sealed partial class ModelStore
{
    // New: applies an RFC 6902 patch to the element's file document and saves it as a one-operation batch update.
    public Task<BatchResult> PatchAsync(string id, ReadOnlyMemory<byte> patch, string expectedHash,
        ChangeSource source, CancellationToken ct);
}

// Maquettiste.Engine.Diagnostics, Diagnostics/RuleCatalog.cs
public sealed record RuleCatalogSummary(int Total, IReadOnlyList<RuleFamilySummary> Families);
public sealed record RuleFamilySummary(string Family, string FamilyLabel, int Rules, string First, string Last);
public sealed record RuleCatalogPage(IReadOnlyList<RuleCatalogEntry> Items, string? Next);

public static class RuleCatalog
{
    public static RuleCatalogSummary Summarize();                    // new: families in id order
    public static RuleCatalogPage Page(string? family, IReadOnlyCollection<string>? ids, string? query,
        string? cursor, int limit);                                  // new: FormatException on a bad cursor
}
```

**Engine behaviour**

1. `GetProjectAsync` (`AgentTools.Handlers.cs:123`) builds the summary: each pack's name, version, description and unit
   count; pack diagnostics counted by severity; extension names; `Sections` always `["extensions", "packs",
   "settings"]`. `settings` fills `Settings`; `packs` fills `PackManifests` and `PackDiagnostics`; `extensions` fills
   `ExtensionSchemas`. An unknown section is a `bad-request`.
2. `ListValidationRulesAsync` (`:229`) answers `RuleCatalog.Summarize()` with no argument, else `RuleCatalog.Page`:
   `family` matches `FamilyOf`, `ids` match exactly, `query` matches the id or description ignoring case (ordinal);
   filters combine with AND, in id order.
3. `TagUsageAsync` (`:352`) projects `GetTagUsageAsync` (`ModelStore.Tags.cs:44`, unchanged). Without `tag`: tags in key
   order without `elements`, the whole list unless `limit` or `cursor` pages it. With `tag`: its element ids, paged in
   id order; an unknown tag is `not-found`.
4. `PatchAsync` (new partial `ModelStore.Patch.cs`): `id` is looked up as an id before anything else, so a missing
   element, or a name, is `NotFound`. A hash other than the indexed one is `Conflict` with `Current` and `Hash`, as
   `SaveAsync` reports it. Otherwise `JsonPatch.TryApply` (new, internal, `Json/JsonPatch.cs`) patches a clone of the
   file JSON (`$schema` included), all or nothing, and `ApplyBatchCoreAsync` runs one `update`. Names inside the
   patched values resolve, and 5.1's "keep its id" refusal (MQ1014) applies. Numbers in `test` compare by
   `CanonicalJson.CanonicalNumber`. A malformed patch, an unresolved path, an index out of range, a failed `test` or a
   change to `/id` or `/kind` is MQ1016 at `/<operation index>`; nothing is written.
5. `generate` sends `GenerationRequest { Mode = Apply, Packs, Units, Elements, Against, Force, Jobs, HandEdits,
   IncludeDiffs = false, Lock = noWait ? Fail : Wait }`; `Units` and `Elements` are 5.6's filters and `Against` is 3.1's
   baseline (folders allowed, as the CLI's MCP server allows them), each passed through under its own rules. The summary
   groups the changes that are not `unchanged` or `not-rendered` by `FileChange.Pack`, with counts per kind and the
   first 50 paths (ordinal) plus `more`; diagnostics come errors first, then `Diagnostic.Order`, at most 100, plus
   `moreDiagnostics`. Run id and timings are left out.

**CLI and MCP**

| Tool | Arguments | Answer |
| --- | --- | --- |
| `get_project` | `sections` (optional: `settings`, `packs`, `extensions`) | the summary; each section adds its full members |
| `list_validation_rules` | `family`, `ids`, `query`, `cursor`, `limit` (optional) | none given: `{ total, families: [{ family, familyLabel, rules, first, last }] }`; else `{ items, next }` of full entries |
| `tag_usage` | `package`, `tag`, `cursor`, `limit` (optional) | `{ package, vocabulary, strict, tags: [{ tag, declared, uses, examples }], next }`; with `tag`: `{ package, tag, declared, uses, elements, next }` |
| `patch_element` (new) | `id` (an id, not a name), `patch` (an RFC 6902 array or a string holding one), `expectedHash`; all required | `save_element`'s answer plus `ids`, with `facts` from `BatchResult.Facts`; no hash is `precondition-required` (428); a conflict carries `current`, `hash`, `submitted` (the patch) |
| `generate` (new) | `packs`, `units`, `elements`, `against`, `force`, `handEdits`, `jobs`, `noWait` (optional) | `{ outcome, unitsRendered, unitsSkipped, filesWritten, filesDeleted, packs: [{ pack, counts, files: [{ path, kind }], more }], diagnostics, moreDiagnostics }`; no plan is stored |

The new tools are `[McpServerTool]` methods on `ModelTools`, so the assistant gains no write tool. Their parameters are
nullable with `"; required."` descriptions (`McpServerSetup.cs:103-110`). Both call `_store.GetSnapshotAsync` first, as
`plan` and `save_element` do (`ModelTools.cs:406`, `:79`), so they see files changed on disk. A `generate` outcome
other than succeeded goes through `FromRun` (`ModelTools.cs:495`): invalid, conflicts, busy (with `noWait`),
cancelled, failed; `stale` cannot occur, as nothing is stored. `FromRun` receives the summary projection, never the
full `GenerationResult`, so a failed run's body has the answer's shape, without run id, timings or every change. The
server instructions name both tools beside plan, `get_plan_diff` and `apply_plan`. Pinned counts: MCP tools +2
(`patch_element` and `generate`: the `McpSurfaceTests.Tools` array, the docs/mcp.md table and its two counts, the
README count); `AgentTools.All` does not change.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ1016 | error | A JSON Patch cannot be applied: an unknown op, a path or from that does not resolve, an index out of range, a failed test, or a change to id or kind; nothing is written. | |

**Docs**

- `docs/mcp.md`: the rows of the five tools, the three summaries in "Reading a large model", the counts, and a
  Semantics bullet for patches. That bullet is MQ1016's documentation home.
- `README.md`: the count. `Agent/tools.json`: descriptions and schemas.
- `docs/api/openapi.yaml:3531`: "The MCP tool `tag_usage` answers the same" becomes "The MCP tool `tag_usage` answers a
  summary of the same, with pages and one tag's elements on request".
- `docs/user-guide.md:1857`: `GET /api/validation/rules` returns the full descriptions; the MCP tool
  `list_validation_rules` returns a summary by family, and full entries by family, id or text, in pages.
- `skills/maquettiste-modeling/SKILL.md`: step 1 (summaries, sections), step 3 (`patch_element`), step 6 (`generate`,
  or plan then apply when a review comes first).
- `docs/guides/work-with-an-agent.md`, step 5. `docs/releases.md` under "## 0.11.0 (unreleased)".

**Tests**

Scope 7.5 has no "done when" line; each part has an acceptance test. `JsonPatchTests` lives in
`tests/Maquettiste.Engine.Tests/Json/`, `ModelStorePatchTests` in `Store/`.

- Summaries: `AgentToolsTests.Get_project_answers_a_summary_and_sections_on_request`,
  `AgentToolsTests.List_validation_rules_summarizes_families_and_pages_rules`,
  `AgentToolsTests.Tag_usage_summarizes_tags_and_pages_one_tags_elements`; the rules part of
  `McpSurfaceTests.Project_index_element_references_database_packs_settings_and_schema` changes.
- Patch: `JsonPatchTests.Applies_the_rfc6902_appendix_a_examples`,
  `JsonPatchTests.A_failed_test_or_missing_path_is_MQ1016_and_changes_nothing`,
  `ModelStorePatchTests.A_patch_saves_through_the_conflict_check_and_mints_a_new_columns_id`,
  `ModelStorePatchTests.A_patch_that_renames_a_column_carries_its_facts`,
  `ModelStorePatchTests.A_patch_by_name_is_not_found`,
  `McpSurfaceTests.Patch_element_needs_the_hash_and_a_conflict_returns_the_disk_version`,
  `McpSurfaceTests.Patch_element_answers_the_facts_its_save_recorded`.
- Generate: `McpSurfaceTests.Generate_plans_and_applies_in_one_call_and_stores_no_plan`,
  `McpSurfaceTests.Generate_answers_invalid_with_diagnostics_and_writes_nothing` (asserts the summary shape),
  `McpSurfaceTests.Generate_scopes_to_units_and_elements`,
  `McpSurfaceTests.Generate_against_a_snapshot_diffs_from_it`.
- Pinned surface: `Lists_every_tool_and_serves_the_conventions_as_a_resource_and_a_prompt`,
  `Docs_list_every_tool_and_the_current_count`; the catalog count grows by 1.

**Tasks** (legend and build step as in 5.1)

- **C10. Catalog summaries.** Files: `Agent/tools.json`, `Agent/AgentTools.Handlers.cs`, `Diagnostics/RuleCatalog.cs`,
  `docs/mcp.md`, `docs/api/openapi.yaml` (the `tag_usage` sentence), `docs/user-guide.md`,
  `tests/Maquettiste.Cli.Tests/McpServerTests.cs`. Tests: the summary tests. Verify: `E "~AgentToolsTests"`; `C
  "~McpSurfaceTests"`; `F "~AssistTests|~ContractTests"`; `editor`.
- **C11. Patch** (after C4, which it calls). Files: `Json/JsonPatch.cs`, `ModelStore.Patch.cs`,
  `Diagnostics/RuleCatalog.cs`, `Mcp/ModelTools.cs`, `Mcp/McpServerSetup.cs`, `McpServerTests.cs` (`Tools`),
  `docs/mcp.md`, `README.md`. Tests: the patch tests, the pinned surface, the catalog count. Verify: `E
  "~JsonPatchTests|~ModelStorePatchTests|~CatalogTests"`; `C "~McpSurfaceTests"`; `record`.
- **C12. Generate** (after A3, `GenerationRequest.Against`, and after C26 and C27: `GenerationRequest.Units` and
  `Elements`, and the keep rule that leaves unselected units' files alone). Files: `Mcp/ModelTools.Generate.cs` (new
  partial), `Mcp/GenerateSummary.cs`, `Mcp/McpServerSetup.cs`, `McpServerTests.cs` (`Tools`), `docs/mcp.md`,
  `README.md`. Tests: the generate tests. Verify: `C "~McpSurfaceTests|~GenerateTests"`.
- **C13. Skill, guide, release note.** Files: `skills/maquettiste-modeling/SKILL.md`,
  `docs/guides/work-with-an-agent.md`, `docs/releases.md`. Tests: `SkillRefreshTests`, `AgentSetupTests`. Verify: `C
  "~SkillRefreshTests|~AgentSetupTests"`.
- **C14. Pre-release note** (scope 16.5). File: `tmp/response-next-capabilities-note-agent-answers.md`, a short
  anonymous note the owner sends: the three summaries and how to ask for more, `patch_element` with the hash, and
  `generate` in one call. Billing and invented names only. Tests: none. Verify: the owner reads it before sending.

### 5.3 Seeds by name in templates

**Decisions taken here** (scope 5.4)

- A seed's names are the forms section 5.1 registers for kind `seed`, looked up in 5.1's `NameIndex`, so a batch and a
  template name a seed the same way. Section 4.3 gives every seed a stored `package`. 5.1's `seed` row reads `name`,
  plus `<package path>.name` when the file states a package (5.1 decision 7): `currencies`, `Billing.currencies`. Names
  match stored values (5.1 decision 3), so a seed whose file has no `package` has its name only, even when its target
  sits in a package. The name is a template lookup only; links stay ids.
- A form two seeds share fails the unit (MQ6006, naming each seed's longest form and id). No validation rule: seed
  names are not unique today.
- A lookup records a new key, `n:seed:<canonical form>`, hashed over the sorted ids the index returns for that form, so
  a rename into or out of the name renders the unit again. Recording every seed would re-render all users of
  `seed_named` on any seed edit, and large models hold hundreds of seeds.
- Storage stays keyed by column id (2026-10-07). The code differs from the scope's "Today": the import CSV already
  matches headers by name (`ModelStore.LocalizationApi.cs:440`), and the `rowsFrom` CSV is storage, so it keeps ids.
  What is left is the seed document on input.
- Seed input has one model, in a fixed order. In a batch, 5.1's resolver runs first. It turns 5.1 forms
  (`@column:billing.currency.code`, a placeholder such as `$cur`) into ids at the `target`, `columns` and `key` sites,
  as at any reference site. Then `SeedInput`, this section's convenience, resolves what is left: bare names in
  `columns` and `key`, relative to the seed's target. A bare name never starts with `@` or `$`, so the two never claim
  the same string. Outside a batch (`create_element`, `save_element`) 5.1 does not run, and only bare names apply.
- `code`, `label` and `description` are keywords only when the target is a reference type (`seed.json:68`). For a
  table, entity or relation target they are names like any other, so a table column named `code` is stored as its id.
- A row may give its cells as a `values` object, keyed by the `columns` entries as the body writes them. A key that is
  no entry is refused. It is not appended, because appending would shift the positional cells of every other row.
- A name `SeedInput` cannot turn into an id stays a string the schema refuses, so it is MQ1002 with a message that says
  why. No rule id is added; 5.1's MQ1012 and MQ1013 stay batch rules.
- (C15, while building) `seed_row` also records the target of an entity or table seed, whose key it reads, besides
  what `row` records. A table seed whose table has no primary key and names no unique constraint, and an entity seed
  whose entity has no key, have no row key either: MQ6006, as for a relation seed. A seed whose target does not resolve
  gives null. `DependencyHasher` hashes `n:<kind>:<form>` for any kind with names in the same way; only `n:seed:` is
  recorded today.
- (C16, while building) In a batch, `Names` runs in a pass of its own right after 5.1's resolver and before the
  operations are planned, in operation order, and reads the earlier operations' elements only up to the last seed that
  holds a bare entry; a batch without one reads nothing more. A `columns` entry `code`, `label` or `description` counts
  as such an entry, since only the target tells whether it is a keyword.

**Public API**

```csharp
namespace Maquettiste.Engine.Resolution;
public sealed class RSeed : RElement
{
    /// <summary>The longest form 5.1 registers for the seed: its stored package path and name, else the name.</summary>
    public string QualifiedName { get; internal set; } = "";
}
```

**Engine behaviour**

- `ResolveSeedsAndUsages` (`ResolveRun.ReferenceData.cs:121`) sets `QualifiedName` from the seed file's `package`, as
  `NameIndex` forms it.
- `seed_named "<form>"`: parses the text as a 5.1 path (`QualifiedName`, without the `@seed:` prefix) and asks the
  snapshot's `NameIndex` for kind `seed` (through `model.Source`, as `in_category` reads it, `BuiltinHelpers.cs:1010`).
  It returns the `RSeed` (`Model.Find`) or null, records `n:seed:<canonical form>` and, on a hit, the seed. An empty or
  malformed text is MQ6006. Units render in parallel; 5.1's `NameIndex` member is thread-safe
  for that reason (5.1 decision 8).
- `seed_row <seed> <row key>`: the `RSeedRow` or null; `<seed>` is an `RSeed` or a form `seed_named` takes. The row key
  is the code (reference-type seed), the key columns' value (table seed: the unique constraint `RSeed.Key` names, else
  the primary key) or the key attributes' value (entity seed); a composite key is a list in key order; a relation seed
  has none (MQ6006). Values compare as `RRow.KeyOf` (`ResolvedReferenceData.cs:171`). It records what `row` records
  (`BuiltinHelpers.cs:1068`): the seed, its rows' membership, the row. The per-seed index is built on first use.
- `DependencyHasher.Compute` (`Planning/DependencyHasher.cs:192`) gains the `n` family: `n:seed:<form>` is
  `HashBuilder.Of("n:seed", <sorted ids>)` over the same index, or `absent`. `PlanExplainer.Describe` (`:242`) words
  `n:` keys ("The seeds named <form>"). `KindOf` (`:207`) keeps them under `inputs`, so the cause-kind enum of the
  contract (`docs/api/openapi.yaml:12174`) does not change.
- New internal `Loading/SeedInput.cs`, two steps on the seed body of a create or update:
  - `SeedInput.Rows(JsonObject seed, List<Diagnostic> errors)` needs no model. Each `values` object becomes the
    positional array, its keys matched ordinally to the `columns` entries, trailing nulls dropped. A key that is no
    entry is MQ1002 at `/rows/<i>/values/<key>`. It runs before anything reads the body as a typed record: in
    `CreateAsync` after `DefaultSeedColumnsAsync` (`ModelStore.cs:154`), in `SaveAsync` after `TryParseRequest`
    (`:169`), and at the start of `ApplyBatchCoreAsync` (`:223`), before 5.1's `BatchNames`, which reads each element
    into its typed record and skips one that does not read.
  - `SeedInput.Names(JsonObject seed, SeedTargets view, List<Diagnostic> errors)` resolves a `columns` or `key` entry
    that is not an id (the id pattern), does not start with `@` or `$`, and is not a keyword of a reference-type
    target. It looks the name up among the target's children in `view`: a table's columns (stored `name`), and for
    `key` its unique constraints; an entity's attributes, own and inherited, and the roles of the to-one ends that
    `ReferenceDataRules` accepts for it (`:353-375`); a relation's end roles and attributes; a reference type's
    attributes. These are the names the CSV headers use (`SeedColumns`, `ModelStore.LocalizationApi.cs:607`). No
    match, or several, is MQ1002 at `/columns/<i>` or `/key`, naming the target and each candidate's id; nothing is
    written.
  - `SeedTargets` (internal, in the same file) is the loaded snapshot with the batch's earlier operations laid over
    it: a create or update replaces the element's document, a delete removes it. `CreateAsync` and `SaveAsync` call
    `Names` after `Rows`, on the snapshot alone. `ApplyBatchCoreAsync` calls it in its operation loop, after the
    element is parsed (`:249`), on the batch as 5.1 left it, and lays each operation over the view after it. So a seed
    names the columns of a table that an earlier operation creates, with the ids 5.1 minted. Like 5.1's names, it does
    not see what schema, materialize, process and tag operations produce.

**CLI and MCP**

`create_element`, `save_element`, `apply_batch`, `model apply` (5.1), `patch_element` (5.2, through
`ApplyBatchCoreAsync`) and the editor's save and batch endpoints take seed columns, keys and cells by name through the
store. No tool is added.

**Docs**

`docs/user-guide.md`, `docs/guides/reference-data-and-seeds.md` (a constant per row from a seed found by name; columns
and cells by name), `docs/mcp.md` (a "Seeds by name" paragraph after 5.1's section: the order, the keywords, the
`values` rule, MQ1002), `skills/maquettiste-modeling/SKILL.md`, `docs/releases.md` under `## 0.11.0 (unreleased)`.

**Tests**

- Acceptance: `SeedByNameGenerationTests.A_template_emits_one_constant_per_row_of_a_table_seed_found_by_name`
  (Integration). A billing copy gains a designed table `billing.currency` with columns `code` and `label`, and a table
  seed `currencies` of three rows, created through the store with its columns by name. A `model` unit loops over
  `(seed_named "currencies").rows`, one `const` per row. After a rename it renders none.
- `SeedHelperTests`: `Seed_row_finds_rows_by_code_by_table_key_and_by_entity_key`,
  `Seed_named_fails_the_unit_when_two_seeds_share_the_name`, `A_seed_renamed_into_the_name_renders_the_unit_again`,
  `Seed_named_takes_the_package_form_of_a_seed_with_a_package`.
- `SeedInputTests` (Store): `Seed_columns_given_by_name_are_stored_as_ids`,
  `A_table_column_named_code_is_stored_as_its_id`, `Row_values_keyed_by_column_name_are_stored_in_column_order`,
  `A_values_key_outside_columns_is_MQ1002_and_writes_nothing`,
  `A_seed_key_given_by_constraint_name_is_stored_as_its_id`,
  `An_unknown_or_ambiguous_column_name_is_MQ1002_and_writes_nothing`,
  `A_batch_creates_a_table_and_its_seed_by_column_names` (the table by placeholder, the seed's columns bare),
  `Qualified_names_resolve_before_bare_names` (one `@column:` entry and one bare entry in one batch);
  `McpSurfaceTests.Create_element_takes_a_table_seed_with_column_names` (Cli).

**Tasks**

Legend in section 2; order in section 6. Each task in 5.3 to 5.7 updates the docs rows of what it builds.

- **C15. Seed lookups in templates.** Qualified names, `seed_named`, `seed_row`, `n:seed` (after C3, `NameIndex` with
  the seed forms above, and so after B3). Files: `Resolution/ResolvedReferenceData.cs`,
  `Resolution/ResolveRun.ReferenceData.cs`, `Rendering/BuiltinHelpers.cs`, `Planning/DependencyHasher.cs`,
  `Generation/PlanExplainer.cs`. Tests: `SeedHelperTests`, the acceptance test. Verify: `E
  "~Seed|~ReadTrackingTests|~PlanCauseTests"`; `P "~GoldenTests"` (unchanged).
- **C16. Seed input by name.** Seed columns, keys and cells by name (after C4, `BatchNames` in `ApplyBatchCoreAsync`).
  Files: `Loading/SeedInput.cs` (new), `ModelStore.cs` (`CreateAsync`, `SaveAsync`, `ApplyBatchCoreAsync`),
  `docs/mcp.md`, the guide, the skill. Tests: `SeedInputTests`, the MCP test. Verify: `E
  "~SeedInputTests|~BatchNamesTests"`; `E "~ModelStoreBatchTests"`; `C "~McpSurfaceTests|~SkillRefreshTests"`.

### 5.4 Copy units and closing block markers

**Decisions taken here** (scope 5.7)

- `copy` is a new output mode, so a copy unit keeps `for`, `where` and `output`, and its files are managed like
  `overwrite` files (rewritten when inputs change, hand edits caught by MQ6009). A write-once skeleton is a follow-up.
- Sources are globs over the pack's file list (`PackFileRoles.EnumerateFiles`), which skips dot files and folders: one
  file list for the editor, hashing and copies. Patterns match case-sensitively, unlike output allow and deny globs, so
  what a unit copies does not depend on the file system.
- One internal matcher, `Planning/CopySources.cs`, serves the planner, the preview and the renderer. It matches a
  unit's patterns once per loaded pack and reads each matched file once, so the hash, the preview and the copied bytes
  come from one read.
- `tokens` maps literal source text to a pack parameter, so sources stay ordinary, compilable code; tokens apply to text
  contents and output paths. Text means strict UTF-8 without a NUL byte; anything else is copied byte for byte.
  Replacement works on bytes, and no normalization, formatter or regions run, so every other byte stays.
- The static hash covers every matched path and its content hash, so editing, adding or removing a source renders the
  unit again. Sources record no `t:` keys, which would only repeat that.
- A malformed pattern is a manifest error, MQ6001, as a template path outside the pack is (`PackLoader.cs:266`); it
  fails the pack. A pattern that matches nothing (MQ6030) and a bad token (MQ6031) are unit-level, like MQ6019: the unit
  is skipped, the pack's other units render, and the run is invalid.
- A closed delimiter line is recognized whatever closing text the unit declares, so the orphan scan, which has no unit
  information (`WriteRun.cs:808`), finds closed blocks, as `Find` already ignores the opening comment
  (`ManagedBlock.cs:111`).
- Copy files reach disk only through `PostProcessor` (its `IOutputPathPolicy` check) and `WriteRun`, like every output.
- (C18) `sources` declares `"default": []` in `schemas/v1/pack.json`, not none: the record's initializer is `[]` and
  `SchemaConsistencyTests` requires an initializer to match a schema default. `minItems: 1` and the copy `if`/`then`
  still require a non-empty list on a copy unit.
- (C18) The schema's copy shape uses `not` (a copy unit's `template`; another unit's `sources` or `tokens`), whose own
  failure text names no key, so `PackLoader` rewrites those failures as a plain MQ6001 at the offending key.
- (C18) A unit is "declared" for MQ6031 as for MQ6024: a key of `parameters` or of `parameterSchema.properties`.
- (C18) Copy sources get no `usedBy` entries, since the pack file delete and move checks treat any entry as a unit
  naming the file, and their text is left out of the `include` scan. The role order is manifest, script, type map,
  template, partial, `copy-source`, other.
- (C18) A file a copy unit copies is never a default script or a type map: when `scripts` is empty, the `*.js` files a
  copy unit matches are not loaded as pack helpers, and a matched `types/*.json` is not read as a type map
  (`PackFileRoles.CopiedFiles`). So a skeleton holding JavaScript or a `types` folder is copied, never run or parsed.
  A file `scripts` names is still a script, and also copied.
- (C18) The unit's canonical form in the static hash leaves out empty `sources` and `tokens`, so units outside mode
  copy keep the hash they had before copy units and an upgrade renders nothing again.
- (C18) `PackUnit` in `docs/api/openapi.yaml` follows the record: `template` nullable, `sources`, `tokens`,
  `blockCommentEnd`; `OutputMode` gains `copy`.
- (C19) A copy unit's files come in the order of their sources (ordinal by pack path), each with role `main`; the
  writer lists changes by path, as for any unit.
- (C19) One text test, the internal `DiffGenerator.IsText` (strict UTF-8 without a NUL byte), serves the renderer and
  the diff. The diff's one line `Binary file <path> differs` applies to any file whose old or new bytes are not text.
- (C19) Tokens are replaced by an internal `CopyTokens` beside `UnitRun` in `Rendering/RenderRun.cs`, on UTF-8 bytes
  for paths as for text files, so one pass serves both.
- (C19) Two sources on one path fail the unit with MQ6005 at `pack.json` `/units/<i>/sources`. A preview's copy unit
  with MQ6030 or MQ6031 fails with the matcher's diagnostics as they are.
- (C19) Post-processing selects no formatter for a copy unit, so a copy unit is never warned about one (MQ6014).

**Model and schema**

`schemas/v1/pack.json`, unit (+ the byte-identical copy under `samples/reference-app/.maquettiste/.schema/v1/`). New
x-order: `id, template, for, where, output, mode, sources, tokens, blockComment, blockCommentEnd, createFile, formatter,
delimiters, companion, transforms`.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `template` | string | none | Required unless `mode` is `copy`; forbidden with `copy`. |
| `mode` | enum | `overwrite` | Gains `copy`. |
| `sources` | array of string, minItems 1 | none | Mode `copy` only, required there. `*`, `?` in a segment; `**` across. Case-sensitive. |
| `tokens` | object, string to string | `{}` | Mode `copy` only. Key: literal text, minLength 1. Value: a parameter name. |
| `blockCommentEnd` | string, `^[^\s]+$` | none | Mode `block`: the text closing each delimiter line. |

`required` becomes `["id", "for"]` plus an `allOf` of two `if`/`then` blocks: `pair` needs `companion`; `copy` needs
`sources` and forbids `template`, else `template` is required and `sources`, `tokens` are forbidden (MQ6001). A copy
unit's `output` is the destination folder; absent means the pack's output base.

**Public API**

```csharp
namespace Maquettiste.Engine.Model;
public sealed record PackUnit
{
    public string? Template { get; init; }   // was `required string`; null only in mode copy
    /// <summary>Mode copy: pack-relative glob patterns of the files to copy, matched case-sensitively.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];
    /// <summary>Mode copy: literal text replaced in text files and output paths, mapped to a parameter name.</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
    /// <summary>Mode block: the text closing each delimiter line (for example "-->"), or null.</summary>
    public string? BlockCommentEnd { get; init; }
}
public enum OutputMode { /* ... */ [JsonStringEnumMemberName("copy")] Copy }

namespace Maquettiste.Engine.Pipeline;
public sealed record RenderedFile(string Path, string Text, FileRole Role)
{
    /// <summary>A copy unit's output bytes, written as they are; null for rendered text. Never serialized.</summary>
    [JsonIgnore] public byte[]? Bytes { get; init; }
    /// <summary>A copy unit's file that is not text (not strict UTF-8, or holding a NUL); its Text is empty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Binary { get; init; }
}
```

Contract: `RenderedFile` in `docs/api/openapi.yaml` (:11765) gains an optional `binary` (boolean, written only when
true). The file-role enum of `PackFileInfo` (:11856) gains `copy-source`. `text` keeps its type: a copy unit's text file
carries its output text, a binary one `""`.

**Engine behaviour**

- `PackLoader` (`Planning/PackLoader.cs:189`, beside `CheckTemplate`): a copy unit's pattern that is absolute, has an
  empty, `.` or `..` segment, or holds a backslash is MQ6001 at `/units/<i>/sources/<j>`. `CheckTemplate` runs only
  when `template` is set.
- `CopySources.Of(LoadedPack pack, PackUnit unit)` (internal) matches each pattern against
  `PackFileRoles.EnumerateFiles(pack.RootPath)`. The expression is the one `OutputPathPolicy.GlobToRegex` (:228)
  builds, whose text a new internal `OutputPathPolicy.GlobPattern(string)` returns, compiled case-sensitive with
  `RegexOptions.CultureInvariant | RegexOptions.NonBacktracking` and no timeout: matching is linear, so no wall clock
  enters. The result lists the files ordinal by path, each once even when two patterns match it, with its bytes,
  content hash and the literal base of the first pattern that matched it. A `ConditionalWeakTable` keyed by the
  `LoadedPack` keeps it per unit id. So the planner, the preview's `UnitPlanner.StaticHash` call
  (`GenerationService.Packs.cs:97`) and the renderer share one match and one read per run or preview session.
- `UnitHashPrefix` (`UnitPlanner.cs:417`): for a copy unit `_templates` (:436) is
  `HashBuilder.Of("copy", <path>, <hash>, ...)` over the matched files, which `Prefix` (:502) already folds into the
  static hash. `BuildParts` (:466), for the explanation only, lists `source:<path>=<hash prefix>` instead of
  `template:`. The planner reports MQ6030 when a pattern matches no file, and MQ6031 when a token's parameter is
  undeclared or its effective value is not a string; either skips the unit. `Outcomes.IsUnitLevel`
  (`Generation/Outcomes.cs:57`) gains both ids.
- `UnitRun.RenderFiles` (`Rendering/RenderRun.cs:297`), copy branch: it takes the files from `CopySources.Of`, never
  from disk. `output`, when set, renders to a folder; each source lands at `<folder>/<path after its literal base>`
  (the prefix up to the last `/` before the first wildcard, or the file's folder). Tokens are replaced in the relative
  path and in text bytes in one left-to-right pass, longest token first, ordinal. Each file is a `RenderedFile` with
  `Bytes`; a text file also has its output text in `Text`, a binary one `Text = ""` and `Binary = true`. Two sources on
  one path are MQ6005.
- Preview: `PreviewAsync` (`GenerationService.Packs.cs:66`) renders through `RenderOneAsync`, so a copy unit needs no
  branch there. `preview_unit` and `POST /api/generate/preview` list every file with its path; text files show their
  text, binary files `binary: true` and empty text. The copy branch fails the unit with the matcher's MQ6030 or MQ6031
  when it has one; in a run the planner has already skipped such a unit, so only a preview gets there. The preview's
  MQ6007 messages (`:113`) name `pack.json` as a copy unit's file, where they join the template path today.
- `PostProcessor.ProcessFileAsync` (:131), copy branch, chosen by the unit's mode: after the path check,
  `new OutputFile(path, file.Bytes, hash, hash, OutputMode.Copy, role, root)`, with no line-ending normalization,
  formatter or regions. A binary diff (`--diff`, `get_plan_diff`) reads "Binary file <path> differs".
- `PackFileRoles` (`:65-67`) skips a unit without a template (a null key would throw there) and marks matched files
  `copy-source`, so MQ6025 never parses them.
- `ManagedBlock`: `BeginLine`/`EndLine` take `(comment, end, marker)` and append `" " + end` when set. `Matches` (:261)
  accepts, after the marker, the line end or one space and one non-whitespace run. `WriteRun.BlockInfo` (:994) gains
  `CommentEnd`; other closing text is rewritten, as a changed comment is (:459-479). `HoldsDelimiter` refuses a body
  holding a closed delimiter (MQ6006).

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ6030 | E | A copy unit's source pattern matches no pack file; the unit is skipped and the pack's other units still render. | — |
| MQ6031 | E | A copy unit's token names a parameter the pack does not declare, or one whose value is not a string; the unit is skipped. | — |

**Docs**

`schemas/v1/pack.json` and its copy; `docs/user-guide.md` (`copy`; `blockCommentEnd` with an XML example; the rules);
`docs/guides/generate-code.md` (a skeleton with a namespace token); `docs/engineering/engine-design.md` §6;
`docs/api/openapi.yaml` (`copy-source`, `binary`); `Agent/tools.json` (`preview_unit`: binary files);
`docs/releases.md`. Editor follow-up: unit form fields for `sources`, `tokens`, `blockCommentEnd`, and a binary
marker in the preview.

**Tests**

- Acceptance: `CopyUnitTests.A_folder_of_source_files_copies_with_the_namespace_token_replaced` (Generation). A
  temporary pack holds `skeleton/Program.cs`, `skeleton/Api/Invoices.cs` and `skeleton/logo.png` (binary, holding the
  token's bytes); `tokens: { "Skeleton.Api": "rootNamespace" }`, project value `Billing.Api`. Outputs are byte-exact,
  the png unchanged, and a second run skips the unit.
- Acceptance: `BlockModeTests.An_xml_file_holds_a_managed_block` (Writing): `<!--` and `-->` on an existing
  `app.config.xml`; inserted, kept on a rerun, removed as an orphan when the unit goes.
- `CopyUnitPlanTests` (Planning): `A_pattern_matching_nothing_is_MQ6030_and_other_units_are_planned`,
  `A_token_naming_an_undeclared_parameter_is_MQ6031`, `Patterns_match_case_sensitively` (`skeleton/*.CS` beside
  `skeleton/Program.cs` is MQ6030), `Editing_adding_or_removing_a_source_changes_the_static_hash`.
- `CopyUnitTests` (Generation): `Editing_adding_or_removing_a_source_renders_the_unit_again`,
  `Binary_sources_copy_byte_for_byte`, `A_hand_edit_of_a_copied_file_is_MQ6009`,
  `A_preview_lists_text_files_and_marks_binary_files`, `A_preview_of_a_pattern_matching_nothing_is_MQ6030`.
  `BlockModeTests`: `A_changed_closing_text_is_rewritten`, `A_body_holding_a_closed_delimiter_is_refused`.
  `PackLoaderTests`: `A_copy_unit_with_a_template_is_MQ6001`, `A_malformed_source_pattern_is_MQ6001`.

**Tasks**

- **C17. Closing block markers.** `blockCommentEnd`. Files: `schemas/v1/pack.json` (+ copy), `Model/PackManifest.cs`,
  `Writing/ManagedBlock.cs`, `Writing/WriteRun.cs`, `PostProcessing/PostProcessor.cs`. Tests: the block tests. Verify:
  `E "~BlockModeTests"`; `E "~SchemaConsistencyTests"`; `P "~BlockGoldenTests"`.
- **C18. Copy units: manifest, loader, matcher and planner.** Copy units in manifest, loader, matcher and planner
  (MQ6030, MQ6031). Files: schema (+ copy), `Model/PackManifest.cs`, `Planning/PackLoader.cs`, `Planning/CopySources.cs`
  (new), `Planning/UnitPlanner.cs`, `Planning/PackFileRoles.cs`, `Writing/OutputPathPolicy.cs` (`GlobPattern`),
  `Generation/Outcomes.cs`, `Diagnostics/RuleCatalog.cs`, the count in `Model/CatalogTests.cs`, `docs/api/openapi.yaml`
  (`copy-source`) and the generated editor files. The nullable `Template` must build under `TreatWarningsAsErrors`
  (`Directory.Build.props:6-8`), so this task also owns every other site it reaches: `Rendering/RenderRun.cs:305` (a
  guard: a copy unit fails with MQ6006 "copy units are rendered by a later step" until C19 replaces it) and
  `GenerationService.Packs.cs:113-114` (`pack.json` as a copy unit's file). Tests: `CopyUnitPlanTests`,
  `PackLoaderTests`. Verify: `E "~Planning|~CatalogTests"`; `F "~PackAuthoringEndpointTests"`; `record`; `editor`.
- **C19. Copy units: render, preview and write.** Render, preview, post-process and write copy files. Files:
  `Pipeline/PipelineTypes.cs`, `Rendering/RenderRun.cs` (the copy branch replaces the guard),
  `PostProcessing/PostProcessor.cs`, `Writing/DiffGenerator.cs`, `Agent/tools.json` (`preview_unit`),
  `docs/api/openapi.yaml` (`RenderedFile.binary`), generated editor files. Tests: `CopyUnitTests` with the acceptance
  test. Verify: `E "~CopyUnitTests|~Generation"`; `F "~ContractTests|~GenerationTests"`; `editor`.

### 5.5 Template helpers and the resolved model

**Decisions taken here** (scope 5.9)

- The template language's built-in `array` functions (version 7.5) are `add, add_range, any, compact, concat, contains,
  cycle, each, filter, first, insert_at, join, last, limit, map, offset, remove_at, reverse, size, sort, uniq`; nothing
  finds an index or an item by member, so `array_index_of` and `array_find` duplicate nothing. All new helpers are root
  names, as the scope lists them.
- New root names `array_index_of array_find table_named column_named toposort seed_named seed_row`, 3.3's `ledger_ddl`
  (added by A19), and new reserved variables `database`, `attribute`: a pack helper with one of these names is MQ6013
  (`RenderRun.cs:156`). No shipped helper collides (`md_*`, `diagram_*`, `ddl_*`, `reference_realization`, `join_path`,
  `package_folder`, `cs_*`, `ulid_uuid`); the release notes tell other packs to rename.
- Definitions do not resolve 4.2's stereotype rules and parameters or 4.5's annotations on categories: validation
  judges them and the files carry them, so `ResolvedCoverageTests` keeps them excused by name, with B8's and B1's
  reasons. `RStereotypeDefinition` inherits `package`, `rationale`, `decision` and `status` from `RAnnotated`.
- `table_named` matches resolved table names, not 5.1's stored forms: a synthesized table has no stored name, and
  templates need it.
- `each attribute` yields every attribute declared in a model file, once, at its declaring owner: each entity's
  `OwnAttributes` except a promoted entity's, each relation's attributes, and each value object's and reference type's
  attributes, all without stereotype virtual attributes. A promoted entity's attributes are copies of its relation's
  under the same ids (`ResolveRun.Conceptual.cs:988-1000`); inherited copies share their declaring id (:765, :863);
  a virtual attribute keeps one id across owners. The planner keeps the first of duplicate keys (`UnitPlanner.cs:153`),
  so any of these would drop units silently.
- `where` on the new scopes. For `each attribute`, tags, stereotypes and category are the attribute's own and packages
  its owner's (`RAttribute.Package`), all through `UnitFilter.SourceOf`, which already returns an `RElement`.
  `database` matches through the owner: an entity or relation mapped there, or, for a value object's or reference
  type's attribute, any existing database (the default arm). For `each database`, tags, stereotypes and category are
  the database file's, packages follow section 4.3's `RAnnotated.Package`, and `database` matches by name. Both refuse
  `abstract`.
- The shipped packs switch to `each database` now and drop the `databases` selector: keys and outputs stay, so the
  goldens prove it, and the editor already takes both forms (`storageChoices.ts:154`, `ddlPreview.ts:78`).
- `toposort` is a stable Kahn sort. `sql-ddl` keeps its depth-first `ddl_order`, whose order the goldens hold.
- Null contract: a member backed by a schema property with a default carries it; an absent value is null, never `""`
  or `false`; derived members (fallbacks, inferences) document their rule. A survey of `Resolved*.cs` found these to
  change from `string` to `string?`: `RAnnotated.DisplayName` and `PluralName` (physical objects,
  `ResolvedCore.cs:86-96`), `RView.Body` (`ResolvedPhysical.cs:587`, set to `""` at `DatabaseRun.cs:147` when the view
  has no body for the dialect), `RRoutine.Body` (:708), `RSqlObject.Body` (:950), `RRoutineReturns.NativeType` (:791),
  `RDatabaseType.NativeName` (:890) and `RQuery.Sql` (`ResolvedQueries.cs:64`). `RAuditField.Description` is already
  `string?` (`ResolvedProcesses.cs:357`), but the resolver fills it with `""` (`ResolveRun.Processes.cs:423`); it gets
  null. Booleans are not all `bool?` today: `RQueryField.Nullable` is a `bool` (`ResolvedQueries.cs:210`) for the
  optional `nullable` of `query.json`, and stays so as a derived member (the file's, else inferred). Any other member
  the contract test finds changes the same way or joins the test's `Derived` list with its rule. `get_resolved_model`
  records follow. `sql-ddl/_objects.scriban:345` (`t.native_name != ""`) becomes a null test; goldens stay
  byte-identical.
- JavaScript fast path: `maquettiste.helper(name, fn, { plain: true })` opts in; proxies stay the default. The flag
  lives in the sandbox's registry, so no public type changes. A plain copy is bounded by member hops (below), because a
  copy of everything reachable would copy the whole model through `database.tables`.
- (C21) `Rendering/RenderRun.cs` joins C21's files: `CreateGlobals` binds a scope's element under its kind only when the
  kind is not a reserved variable or is on its list of scope kinds, so reserving `database` and `attribute` in
  `BuiltinHelpers.Variables` adds both to that list; without it `each database`, `each attribute` and today's
  `select databases` units would lose their `database` or `attribute` variable.
- (C20) `n:table:<text>` hashes the sorted `<database id>/<table key>` of every matching table, so moving a designed
  table (whose key is its file id) to another database changes the hash too. `table_named` takes a database or its
  name; a name records `k:database` and every database, as a `schema_diff` miss does. The first dot splits the schema.
- (C20) `json` fields are one path or a list of them. A map's missing key writes null, since a map's keys are data;
  only an unknown member of a resolved object or view is MQ6006. `toposort` matches an end to every equal item and
  ignores an edge from an item to itself.
- (C21) `UnitRules.Scopes` lists `each attribute` after `each entity` and `each database` before `each table`; the
  MQ6021 text in `tests/fixtures/validation/generation/expected.json` follows.
- (C22) `seed-data/helpers.js` held only the `databases` selector, so it is deleted with the manifest's `scripts` entry:
  the pack loads no script. `sql-ddl/helpers.js` keeps its helpers, and the CLI's starter copy, embedded from
  `packs/sql-ddl`, follows.
- (C22) The editor mocks follow the switch. `mocks/model/packs.ts` refused any scope outside its list (MQ6021), so it
  takes `each database` and `each attribute`. `packAuthoring.ts`'s explain answers as `UnitPlanner.WhyNot` does for an
  element an `each <kind>` unit covers (`skip-hint`, else planned) and gives an unrendered `select` unit the
  `selector` reason in the engine's words; `generation.ts` previews a database unit the mock does not model (migration,
  the environment scripts) as rendering nothing, as it already did for object units. With every database covered, the
  e2e spec's migration step now shows no statement, and its failure step first saves a pack whose `seed-environments`
  is still `select databases` (a copy from before the switch, whose scripts no longer register the selector). The
  spec's unit list also gains A28's `object-environments`.
  `workspaces/generate/previewScope.ts` lists `each database` by kind, so the preview picker says "Database" for the
  shipped units, as it did for `select databases`.
- (C22) The recorded mocks keep only this task's change (each unit's `for` and the dropped selector registration);
  the rest of a fresh recording's drift is not part of the task.
- (C23) Definition order and shapes: one stereotype per key (`GetStereotype`), by key; vocabularies and trees the
  global one first, then by (package qualified name, id). A stereotype definition's attributes have the definition as
  `Owner`, `IsVirtual` and `FromStereotype` set and the ids of their carriers' copies, so they are not registered for
  `model.find` (the definitions, vocabularies and trees are). Siblings go by `order` (none counts as 0), then file
  order; a parent the tree lacks, or the category itself, makes a root; a category in a longer parent cycle follows
  the depth-first walk, in file order. `ResolveRun.cs` completes the `RCategory` instances `ResolveCategories`
  creates, so `ResolveRun.Conceptual.cs` is unchanged.
- (C23) `ResolvedCoverageTests`: a category's own names and annotations outside 4.5 (`displayName`, `pluralName`,
  `stereotypes`, `tags`, `category`, `properties`, `generation`) and a stereotype's `storage` move from `Pending` to
  `NotResolved` with their reasons, since the API above leaves them out on purpose; 4.2's stereotype keys and 4.5's
  category keys stay in `Pending`.
- (C24) The contract covers single values: a list or map is never absent, only empty, so
  `Every_optional_value_without_a_default_is_nullable` skips list and map members. Its `Derived` list holds what the
  test found beyond the survey, each with its rule: a database's `ByConvention`; a column's `Key`, `Type`, `NativeType`
  and `Identity`; constraint names; a routine's `Language` and `Volatility`; the native type of a routine parameter, a
  table result's column, a composite's field and a query parameter (the slot's own, else its database type's native
  name, else the type map's; empty only when the database type has no native name for the dialect, kept as text because
  the editor's previews concatenate it); a query source's `Alias`, a query field's `Name` and `Nullable`, an
  expression's `Null`; an attribute's `Order`; the display name of an enum member and of a process node; a scenario's
  `Start`.
- (C24) `No_string_is_empty_unless_its_default_is` also walks a built model holding what no fixture has (a routine, a
  database type and a SQL object without SQLite text on SQLite, a routine returning a table, a partitioned table with an
  exclusion and storage parameters, a derived attribute, a view's generation hints) and asserts their nulls. The view
  types it still cannot reach are excused by name with the tests that resolve them: migration facts
  (`MigrationResolutionTests`), case branches (`QueryTests`) and reference type bindings (`ReferenceBindingTests`).
- (C24) The records follow the members, which takes three files beyond the task's list: `Editor/DatabaseViews.cs` and
  `Editor/QueryViews.cs` (`ViewView.Body`, `RoutineView.Body`, `SqlObjectView.Body`, `RoutineReturnsView.NativeType`,
  `DatabaseTypeView.NativeName` and `QueryView.Sql` become nullable, with `docs/api/openapi.yaml` and the editor mocks),
  and `SchemaDiff/SnapshotCapture.cs`, which keeps `""` for a view without a body so captured snapshots stay
  byte-identical. A conceptual record keeps a non-null display and plural name (`ResolvedAnnotations`), since a
  conceptual object always falls back. `pluralize` of an element falls back to the inflector
  (`Rendering/BuiltinHelpers.cs`). `RQuery.Sql` is also null on a nested query, which never had its own SQL. A slot with
  no type has a null native type, so a scalar result without one stays null in a query expression.
- (C24) `display_name`, `plural_name` and `description_of` on a database object, which has no translations, now give its
  own file's text and record the object's read; null when unset, now that unset is null rather than `""`. Before, they
  gave null even when the file set a display name.
- (C24) Goldens: the null audit description reaches csharp-dapper's
  `f.description ?? ("Audit attribute " + f.name + ".")`, which `""` had made dead, so one line of
  `tests/fixtures/golden/csharp-dapper/processes` changes, from an empty `<summary>` to `Audit attribute costCentre.`.
  Every other golden stays byte-identical.
- (C25) A stub needs an id and a kind, so the hops count resolved objects (`IResolvedObject`). The model's helper
  objects (`RType`, `RStereotype`, `RCategory`, a mapping, an expression) are copied whole at their holder's hop and add
  none, as lists and maps do; the memo entry is made before the members are copied, so a cycle of helper objects (a
  category's parent and children) closes on its copy. Plain lists are frozen arrays (`Array.isArray` is true).
- (C25) Only `helper` reads a third argument: absent, or an object whose only key is `plain`, `true` or `false`. Any
  other key, a `plain` that is not a boolean, or a value that is not an object (null and arrays included) is a
  `TypeError` at load, MQ6016 on the registering line. `selector`, `filter` and `transform` are unchanged.

**Model and schema**

`schemas/v1/pack.json` unit `for` pattern gains `database` and `attribute` in the `each (...)` group (+ copy).

**Public API**

```csharp
namespace Maquettiste.Engine.Resolution;
public sealed class ResolvedModel   // gains; vocabularies and trees: the global one first, then by package
{
    public RList<RStereotypeDefinition> Stereotypes { get; internal init; } = RList<RStereotypeDefinition>.Empty;
    public RList<RTagVocabulary> TagVocabularies { get; internal init; } = RList<RTagVocabulary>.Empty;
    public RList<RCategoryTree> CategoryTrees { get; internal init; } = RList<RCategoryTree>.Empty;
}
public sealed class RStereotypeDefinition : RElement     // Kind "stereotype"; Name is the label; listed by key
{
    public string Key { get; internal set; } = "";
    public IReadOnlyList<string> AppliesTo { get; internal set; } = [];
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;   // Owner = the definition
    public IReadOnlyDictionary<string, object?> DefaultProperties { get; internal set; } = EmptyMap;
    public string? Icon { get; internal set; }
    public string? Color { get; internal set; }
}
public sealed class RTagVocabulary : RElement            // Kind "tag-vocabulary"; Package = scope, null for global
{
    public bool Strict { get; internal set; }
    public IReadOnlyList<RTagDefinition> Definitions { get; internal set; } = [];
}
public sealed class RTagDefinition { public string Key { get; internal set; } = "";
    public string? Description { get; internal set; } public string? Color { get; internal set; } }
public sealed class RCategoryTree : RElement             // Kind "category-tree"; Package = scope
{
    public IReadOnlyList<RCategory> Roots { get; internal set; } = [];
    public IReadOnlyList<RCategory> Categories { get; internal set; } = [];   // depth-first, sibling order
}
public sealed class RCategory { /* gains */ public RCategory? Parent { get; internal set; }
    public IReadOnlyList<RCategory> Children { get; internal set; } = [];
    public int? Order { get; internal set; } public string? Description { get; internal set; } }
```

`EmptyMap` is `FrozenDictionary<string, object?>.Empty`. `ResolvedModel` members use `internal init`, as its existing
ones do (`ResolvedCore.cs:173-240`). The eight null-contract members change type as listed above;
`RAuditField.Description` keeps its type.

**Engine behaviour**

- Helpers, each added to `Names` (`Rendering/BuiltinHelpers.cs:116`, :139):
  - `array_index_of <list> [<member path>] <value>`: first index whose item or item member equals the value as
    `OrdinalValueComparer` (:1131) compares, else -1. `array_find <list> <member path> <value>`: that item, else null.
    Members are read through `MemberValue` (:405), so reads are recorded.
  - `table_named "<schema>.<name>" [<database>]`: an `RTable` or null. Without a schema part it matches no schema or
    the default schema; without a database it searches all, and two matches are MQ6006. It records `n:table:<text>`,
    hashed over the sorted keys of every matching table. `column_named <table> "<name>"`: an `RColumn` or null;
    records the table. `PlanExplainer.Describe` words `n:table:` keys ("The tables named <text>").
  - `toposort <items> <edges>`: `<edges>` lists `[before, after]` pairs whose ends match items by identity (resolved
    objects) or ordinal value; other ends are ignored. Ties keep input order; a cycle releases the earliest item left.
  - `json <value> [<fields>]`: snake_case member paths (`"columns.name"`); shared prefixes merge, a path through a list
    applies per item, keys are ordinal. A path ending on a resolved object or naming no member is MQ6006. Without
    fields, resolved objects stay refused (:896).
- Scopes: `UnitRules.Scopes`; `UnitPlanner.Candidates` (:339) gives `model.Databases` and, for attributes, the list the
  decision above defines; the skip-hint guard (:122) adds `RDatabase`; `UnitFilter.InDatabase` (:625) matches a
  database by name and an attribute through its owner; the physical-object arms of `Tags`, `Stereotypes` and
  `CategoryMatches` (:653, :665, :677) add `RDatabase`; `PackLoader` (:192-213) refuses `abstract` for both;
  `ScopeType` (`Authoring.cs:432`) maps both; `BuiltinHelpers.Variables` adds both names. The switch covers `sql-ddl`'s
  schema, migration, seed, seed-environments and object-environments (3.4) units, the `seed-data` unit and the sample
  copy.
- Definitions: `ResolveRun.RunCore` (`ResolveRun.cs:124`) fills the lists from the snapshot (`GetStereotype`,
  `TagVocabularies`, `CategoryTrees`); each object depends on its own file; membership keys are `k:stereotype`,
  `k:tag-vocabulary`, `k:category-tree`. `RCategory` stays the instance annotated elements share.
- Null contract: the resolver sets null where it set `""`; `display_name` (`LocalizationHelpers.cs`) treats null as
  unset; `Editor/ResolvedRecords.cs` writes null.
- Plain helpers: `ScriptSandbox.Register` (:459) reads an optional `{ plain: true }` (other values are a TypeError).
  `CallHelper` (:534) converts arguments with a new `ScriptValues.ToPlainJs`, bounded by member hops:
  - A resolved object passed as an argument, or as an item of an argument list or map, is at hop 0. Its copy is a
    frozen object of every camelCase member `MemberCatalog` lists. A scalar, string, enum (its kebab name) or null
    copies as it is. A resolved object that a member holds, directly or inside a list or map, is copied at the next
    hop; lists and maps add no hop of their own. At hop 2 a resolved object becomes the stub `{ id, kind }`. So for a
    table argument, `table.columns[0].name` and `table.database.name` are there, and `table.columns[0].attribute` is a
    stub.
  - A per-call memo keyed by (object, hop) keeps one copy per pair, so a shared record is one object at a hop.
  - Plain copies carry no functions: `hasStereotype`, `hasTag` and `find` exist on proxies only
    (`JsModelProxy.cs:154-183`); a plain helper reads `stereotypes` and `tags`. The model itself (`ResolvedModel`) as a
    plain argument is a TypeError (MQ6016), since its copy would be the whole model; pass the list the helper needs.
  - Every object copied at hop 0 or 1 records its `Dependencies`, and every copied `RList` its membership keys. A stub
    records nothing: its id and kind change only with the member or list that holds it, which its holder records.
  - Limits, seeded random and cancellation are unchanged (`Calls.cs:33`).

**Docs**

`docs/user-guide.md` (templates: helpers, scopes and their `where`, `model.stereotypes`, `model.tag_vocabularies`,
`model.category_trees`, the null contract, `{ plain: true }` with its hop bound), `Rendering/README.md`,
`Scripting/README.md`, the `sql-ddl` and `seed-data` READMEs, `packs/README.md`, `docs/mcp.md`, `docs/releases.md`
(MQ6013 note, null contract). Editor follow-ups: `attribute` in the scope list (`unitsModel.ts:31`); helper and
definition completions.

**Tests**

- `ModelHelperTests` (Rendering): `Array_index_of_and_array_find_compare_ordinally_by_member`,
  `Table_named_finds_by_schema_and_name_and_fails_across_two_databases`,
  `A_table_renamed_into_the_name_renders_the_unit_again`, `Toposort_is_stable_and_releases_cycles_in_input_order`,
  `Json_projects_member_paths_through_lists`, `Builtin_names_list_every_registered_helper`,
  `A_pack_helper_named_toposort_is_MQ6013`, `A_pack_helper_named_database_or_attribute_is_MQ6013`.
- `ObjectScopeTests`: `Each_database_plans_one_unit_per_database_with_hints_and_where`,
  `Each_attribute_plans_declared_attributes_once_without_inherited_virtual_or_promoted_copies`,
  `Each_attribute_filters_on_its_own_tags_and_its_owners_package_and_database`. `GoldenTests` (Packs) unchanged after
  the pack switch and the null-contract fix.
- `ResolvedDefinitionsTests.Model_lists_stereotype_definitions_tag_vocabularies_and_category_trees`;
  `ResolvedCoverageTests` with the vocabulary entries moved out of `Unresolved` (:163) and `Pending` (:136), except
  the stereotype and category keys of 4.2 and 4.5, which stay excused by name.
- `ResolvedNullContractTests` (Resolution) holds one exemption list, `Derived`: each entry a member and its documented
  rule (`RQueryField.Nullable`, the file's value else inferred, is one). Both tests assert against it.
  - `Every_optional_value_without_a_default_is_nullable`: each optional schema property without a default mapped in
    `ResolvedCoverageTests.Targets` has a nullable member (`NullabilityInfoContext`), unless `Derived` lists it.
  - `No_string_is_empty_unless_its_default_is`: resolves every model under `tests/fixtures/models`, walks every object
    reachable from `ResolvedModel`, and finds `""` only in a member whose schema property defaults to `""` (read
    through `Targets`, such as `name` in `package.json`, `seed.json`, `table.json` and `actor.json`) or that `Derived`
    lists. It also asserts every view type (`MemberCatalog.IsProxyType`) is reached or listed with a reason.
- `PlainHelperTests` (Scripting): `A_plain_helper_sees_the_proxy_values_for_two_hops_and_stubs_beyond` (every member a
  proxy read gives at hops 0 and 1 is equal in the plain copy; hop-2 objects are `{ id, kind }`),
  `A_plain_helper_records_every_copied_object_as_read`, `A_plain_copy_has_no_proxy_functions_and_refuses_the_model`,
  `An_unknown_helper_option_is_a_load_error`.

**Tasks**

- **C20. Six helpers.** The six helpers (`array_index_of`, `array_find`, `table_named`, `column_named`, `toposort`,
  `json`) and `n:table`. Files: `Rendering/BuiltinHelpers.cs`, `Planning/DependencyHasher.cs`,
  `Generation/PlanExplainer.cs`. Tests: `ModelHelperTests` except the `database` and `attribute` MQ6013 test. Verify: `E
  "~Rendering"`; `E "~PlanCauseTests"`.
- **C21. Database and attribute scopes.** `each database`, `each attribute`. Files: schema (+ copy),
  `Planning/UnitRules.cs`, `Planning/UnitPlanner.cs`, `Planning/PackLoader.cs`, `GenerationService.Authoring.cs`,
  `Rendering/BuiltinHelpers.cs`, `Rendering/RenderRun.cs` (the scope alias list). Tests: `ObjectScopeTests`,
  `A_pack_helper_named_database_or_attribute_is_MQ6013`.
  Verify: `E "~Planning|~ModelHelperTests"`.
- **C22. Shipped packs on `each database`** (after C21 and A28). Files: `packs/sql-ddl/{pack.json,helpers.js,README.md}`
  (object-environments included),
  `packs/seed-data/{pack.json,helpers.js,README.md}`, `packs/README.md`, the `samples/reference-app` copy,
  `docs/user-guide.md` (the `select databases` examples at :1977 and :2222), `docs/api/openapi.yaml` (the example at
  :376), recorded editor mocks, the mock scope check (`src/editor/src/mocks/model/packAuthoring.ts:376`) and
  `src/editor/tests/e2e/reference-data-feedback.mock-only.spec.ts`, whose line 386 pins the text
  "Unit 'migration' is 'select databases'". `src/editor/scripts/copy-fixture.mjs` copies `packs/sql-ddl/pack.json`
  into the mocks and needs no change. Verify:
  `PG P` (goldens unchanged, no `MAQUETTISTE_UPDATE_GOLDEN`);
  `MAQUETTISTE_RECORD=1 F`, then `F`; `editor`;
  `cd src/editor && npx playwright test --project=mock tests/e2e/reference-data-feedback.mock-only.spec.ts`.
- **C23. Definitions on `model`** (after B2 and B8, whose keys it keeps excused). Files: `Resolution/ResolvedCore.cs`,
  `Resolution/ResolveRun.cs`, `Resolution/ResolvedVocabularies.cs` (new). Tests: `ResolvedDefinitionsTests`,
  `ResolvedCoverageTests`. Verify: `E "~Resolution"`.
- **C24. Null contract** (after C22 and A28, which touch the same sql-ddl files). Files: `Resolution/Resolved*.cs` and
  the resolver files that set them (`DatabaseRun.cs`, `ResolveRun.Processes.cs` among them),
  `Rendering/LocalizationHelpers.cs`, `Editor/ResolvedRecords.cs`, `packs/sql-ddl/_objects.scriban` (+ copy),
  `docs/api/openapi.yaml`. Tests: `ResolvedNullContractTests`. Verify: `E "~Resolution|~Editor"`; `PG P` (goldens
  unchanged but the csharp-dapper audit line a (C24) decision names); `editor`.
- **C25. Plain helpers.** Files: `Scripting/ScriptSandbox.cs`, `Scripting/ScriptSandbox.Calls.cs`,
  `Scripting/ScriptValues.cs`. Tests: `PlainHelperTests`. Verify: `E "~Scripting"`.

### 5.6 Scoped generation

**Decisions taken here** (scope 5.10, phase 1 part; baselines for comparisons are section 3.1's)

- `--unit` takes `<pack>/<unit>`, or a bare unit id that exactly one run pack has; `--element` takes ids, never names.
  Both repeat; values of one flag OR, the two flags AND. `--element` never selects model-scope units; a synthesized
  table's id is its key (`<entityId>@<dbId>`). An empty `Units` or `Elements` list is no filter, as null is (C26).
- The selection filters the plan once, at the end of `GenerationRun.PrepareAsync`, so skip, render, write, capture and
  plan apply see selected units only.
- The writer keeps every manifest entry and unit state of unselected units, and still deletes orphans of selected
  ones, so `generate --element <deleted id>` removes that element's outputs and nothing else. It is a keep rule over
  manifest entries, not fake skipped units, since an unselected unit may have entries but no state.
- A scoped run never answers from and never writes the last-run record. 3.1's runs with a baseline already follow
  this rule (A3); C27 adds the filters to the same condition. In a one-shot host it still forgets the record
  before applying, as any run not answered from it does, so a later full run cannot replay a record that the scoped
  run made stale. It takes the run lock like any apply.
- A scoped run never advances schema snapshots, and a selected unit that reads `schema_diff` fails with MQ6033 and
  keeps its outputs; otherwise a later full run would diff from a moved baseline and lose migration changes. Only
  `sql-ddl/migration` reads it today; per-table DDL stays scopable. Since a database element selects every unit of
  `each database`, `--element <database id>` alone reaches `sql-ddl/migration` and exits 1; one database's schema is
  regenerated with `--unit sql-ddl/schema --element <database id>`.
- An empty or blank `--unit` or `--element` value is a usage error (exit 4), and an empty `units` or `elements` value
  is an MCP `bad-request`; repeated values are passed once. MCP `plan` answers a scoped plan that MQ6032 or MQ6033 makes
  invalid as it answers any invalid plan that holds a plan: `{ outcome: "invalid", plan }` with the diagnostics in the
  plan, not a tool error. `POST /api/generate/plan` passes both filters through unchecked; an empty value there is
  MQ6032 or MQ6034. (Taken in C28.)
- When every unit value names its pack, only those packs load; a pack that only a unit value names and whose folder
  has no `pack.json` is left out of the load, so the value reports MQ6032 (a pack the run lacks) rather than the
  loader's MQ6001 for a missing pack. (Recorded in the milestone C review; `Generation/README.md` already said so.)

**Public API**

```csharp
namespace Maquettiste.Engine;
public sealed record GenerationRequest
{
    /// <summary>Units to run, "pack/unit" or a bare unit id; null means every unit of the selected packs.</summary>
    public IReadOnlyList<string>? Units { get; init; }
    /// <summary>Element ids whose units run (never model-scope units); null means every element.</summary>
    public IReadOnlyList<string>? Elements { get; init; }
}
```

`WriteContext` gains `internal UnitSelection? Selection { get; init; }`, like `ListUnchanged`; the internal
`UnitSelection` resolves the filters against the run's packs and matches unit keys and manifest unit names.

**Engine behaviour**

- `ExecuteAsync` and `PlanAsync` pass the selection to `PrepareAsync`. When every `--unit` names its pack, only those
  packs load (intersected with `Packs`). After `Planner.PlanAsync` (`GenerationRun.cs:194`) the plan keeps selected
  units. Unknown or ambiguous unit names are MQ6032; a selection matching no planned unit and no manifest entry is
  MQ6034.
- `AllPacks` becomes `request.Packs is null && !scoped` (`GenerationRun.cs:338`). `WriteRun` adds unselected units to
  `keepUnits` (:570); `SaveUnitStatesAsync` (:872) carries over their previous states.
- `UnitRun.Render` (`RenderRun.cs:285`) fails a unit with MQ6033 when the run is scoped and its keys hold `d:`.
  `SaveSnapshotsAsync` is skipped for scoped runs (`GenerationService.cs:469`, :622).
- `GenerationPlan.Request` (Web JSON, `PlanStore.cs:24`) stores the filters; `ApplyLockedAsync` (:529) re-plans with
  them, so unselected units never count as appeared or vanished.
- Last run (`GenerationService.cs:418-427`): when a filter or 3.1's `Against` is set (A3 adds `Against`, C27 the
  filters), `RunAsync` builds `LastRun` (under the same
  `ReuseLastRun` condition) only to call `Forget()`. It never calls `TryReplayAsync`, never hashes templates and never
  saves a record. `LastRun.Key` is unchanged.

**CLI and MCP**

- `maquettiste generate --unit <pack/unit|unit> --element <id>`, both repeatable, combine with `--pack`, `--dry-run`,
  `--check`, `--diff` and `--watch`. `--unit` joins the value options (`CommandLine.cs:17`; `--element` is there
  already); both join the `Expect` list (`GenerateCommand.cs:29`) and `CliApp.Usage`. Exit codes per
  `Program.ExitCodes`: MQ6032 or MQ6033 gives 1; MQ6034 alone gives 0 with a warning.
- MCP `plan` gains `units` and `elements` (string arrays); section 5.2's `generate` tool takes the same two arguments.
  `POST /api/generate/plan` takes them in its JSON body (`GenerationRequest` in `docs/api/openapi.yaml`, then
  `npm run gen`). No tool is added.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ6032 | E | A generation filter names a pack the run lacks, a unit no run pack has, or a bare unit id several run packs have. | — |
| MQ6033 | E | A run limited by unit or element renders a unit that reads the schema diff; schema snapshots advance only in whole-pack runs. | — |
| MQ6034 | W | A generation filter selects no unit and no output; nothing is generated. | — |

**Docs**

`docs/user-guide.md` (both flags, the keep rule, the rules, `--unit sql-ddl/schema --element <database id>`),
`docs/guides/generate-code.md` (regenerate one element; one database's schema), `docs/mcp.md` (`plan`),
`docs/api/openapi.yaml`, `SKILL.md`, `docs/releases.md`. Editor follow-up: "Generate this element" and "Generate this
unit" actions.

**Tests**

- `ScopedGenerationTests` (Generation, temporary billing copy):
  `Generating_one_element_rewrites_its_files_and_keeps_every_other_file_and_state`,
  `Generating_one_unit_deletes_only_its_own_orphans`, `Generating_a_deleted_element_removes_its_outputs`,
  `A_scoped_plan_applies_without_marking_unselected_units_stale`,
  `A_scoped_run_neither_answers_from_nor_writes_the_last_run_record`,
  `A_scoped_run_forgets_the_last_run_record_so_a_full_run_does_not_replay_it`,
  `A_scoped_run_rendering_the_migration_unit_is_MQ6033_and_keeps_the_schema_snapshot`,
  `The_schema_unit_of_one_database_is_scopable`, `An_ambiguous_bare_unit_id_is_MQ6032`,
  `A_filter_selecting_nothing_is_MQ6034`, `A_full_run_after_a_scoped_run_skips_every_unit`.
- `GenerateTests.Unit_and_element_flags_limit_the_run_and_exit_codes_follow_the_outcome` (Cli);
  `McpSurfaceTests.Plan_takes_units_and_elements_and_applies_only_them` (Cli).

**Tasks**

- **C26. Selection and plan filter.** Request, selection, plan filter (MQ6032, MQ6034), and the catalog lines of all
  three rules. Files: `GenerationService.cs` (request, plan paths), `Generation/GenerationRun.cs`,
  `Generation/UnitSelection.cs` (new), `Diagnostics/RuleCatalog.cs` (MQ6032 to MQ6034, + count). Tests: the selection
  and plan tests. Verify: `E "~ScopedGeneration|~PlanApplyTests|~CatalogTests"`; `record`.
- **C27. Keep rule, last run and snapshots** (after C26 and A3). Keep rule, unit states, last run, snapshots, MQ6033.
  Files: `Pipeline/Stages.cs`, `Writing/WriteRun.cs`, `Rendering/RenderRun.cs`, `Generation/LastRun.cs`,
  `GenerationService.cs` (`RunAsync` last run, snapshots). Tests: the rest of `ScopedGenerationTests`. Verify: `E
  "~Generation|~Writing|~LastRunTests"`.
- **C28. CLI, MCP and contract.** CLI flags, MCP `plan` arguments, API contract, docs. Files: `Cli/CommandLine.cs`,
  `Cli/CliApp.cs`, `Cli/Commands/GenerateCommand.cs`, `Cli/Mcp/ModelTools.cs`, `docs/api/openapi.yaml`, generated editor
  files, the docs above. Verify: `C "~GenerateTests|~McpSurfaceTests|~SkillRefreshTests"`; `F "~ContractTests"`;
  `editor`.

### 5.7 Script rules that scale

**Decisions taken here** (scope 8.4)

- `scriptTimeoutMs: 0` turns the wall clock off for every sandbox call, rules and pack scripts, since one setting
  serves both (`ProjectSettings.cs:286`) and generation must not depend on load either. Previews keep an 8 s deadline.
  The default stays 2000; CI should use 0.
- Regex matches keep a 250 ms wall-clock bound with the clock off, because a match cannot be interrupted and the bound
  keeps cancellation inside its one-second budget (`Scripting/README.md:44-61`). `RegexRetry` tries a timed-out match
  once more when the call cannot have run script code: a plain `RegExp` receiver and primitive arguments. So a rule's
  regex still gives MQ5003 when a retryable match is descheduled twice (about 500 ms), or a match that is not retryable
  (a replacer function, a receiver with own properties) is descheduled once. The scope's done-when line holds for every
  rule without regexes, and for rules whose regex calls are retryable unless a thread waits 250 ms twice in a row. The
  guide says so and tells CI rules to use literals with primitive arguments.
- `scriptStatements` is capped at 2,000,000,000 (schema and engine), because `int.MaxValue` means unlimited to the
  JavaScript engine (`ScriptSandbox.cs:119`); with the clock off, statements always bound a rule.
- A whole-model rule is `maquettiste.rule({ id, severity, scope: "model", check(model, report) })`: same `x/<id>`
  namespace, `validation.rules` overrides and `Rule` registration kind. It runs once per whole-model validation, that is
  every `ValidateAsync` with `scope.ElementIds` null: `validate` on CLI, MCP and HTTP; generation's validate stage; the
  store's whole-model report (`ModelStore.ResolverFindings.cs:11`); and settings saves, which validate the whole model
  twice (`ModelStore.cs:461-462`). In a settings save, an error a model rule newly reports refuses the save, as any
  introduced error does today (a severity raised to error included). Scoped validations (element saves,
  `ModelStore.cs:774`) skip it, so it never refuses an element save over elements outside the change.
- Each rule file gets its own function scope, and the global object is deep-frozen before the rule files run, not
  after. A file therefore cannot publish anything for another: a cross-file global (`globalThis.helper = ...`, a new
  prototype member) is a TypeError at load (MQ5002), and a call to a function of another rule file fails (MQ5002).
  Pack scripts keep one scope per pack and the freeze after loading. The release notes say so. Modules stay refused.
- Rule `model` gains `withStereotype(key)`, `withTag(tag)`, `withProperty(key)` (own properties) and
  `byQualifiedName(name)` beside `get`, `all` and `referencesTo`; lists stay in (name, id) ordinal order.
  `byQualifiedName` takes a 5.1 reference (`@entity:Billing.Invoice`) and reads 5.1's `NameIndex`, so rules, batches
  and templates share one definition of a qualified name.
- Per-element results are cached in memory per validator (the MCP server, editor host and watch mode gain); nothing is
  written to disk. Soundness: one scope per file and the early freeze make a rule file's hash, with the shared sandbox
  setup, its behaviour; rule calls now record reads (`ModelValidator.cs:693` passes null today) under sound keys; raw
  diagnostics are cached before `ApplySettings`, so `validation.rules` overrides apply after the cache and `off` rules
  never run; a file with top-level `let` or `var` (state that cannot be frozen) gets MQ5005 and is never cached. A
  mutated `const` object is a breach the engine cannot see; the guide states the rule.
- (C29) The sandbox clamps statements with the internal `ScriptSandbox.StatementLimit` (to `StatementCap`,
  2,000,000,000), and MQ5003/MQ6007's statement message names the clamped limit, not the setting. `PreviewDeadlineMs`
  computes 4 × the timeout in 64 bits, so a large `scriptTimeoutMs` no longer overflows the deadline.
- (C29) `LimitMemory` counts every byte a call allocates on its thread, not what it keeps, and a loop allocates its
  numbers, so a rule spending 5 million statements reaches the default 64 MB before the statement limit. The
  determinism acceptance test therefore raises `scriptMemoryBytes` with `scriptStatements`. A call's first run on an
  engine also allocates more (the rule model view is built once per snapshot per engine), so a rule within a few
  kilobytes of the memory limit can still differ between runs whatever the clock; this is left as it is.
- (C30) "Top-level" means outside every function and class body: a `let` or `var` in a top-level block or loop head is
  MQ5005 too, since a closure can capture it. The warning sits at the first one, by line, and names its variable.
- (C30) A rule file's prepared program must be exactly the call of its wrapper (one statement, the function the prefix
  opens, closed by the suffix's brace); otherwise a closing brace in the code ended the scope early, which would let it
  declare script-level bindings another file sees, and the file is MQ5002 ("closes the function scope its code runs
  in"), at the brace that closed it.
- (C30) `CheckRuleScript` applies `validation.rules` to the pool's warnings, so MQ5005 set `off` is hidden on the
  Extensions screen as it is in `validate`.
- (C31) The index functions read what the files state, as `get` returns it: `withStereotype` finds the elements whose
  file lists the key (not the ones that only imply it), `withTag` the file's tags, `withProperty` its own `properties`;
  only top-level elements, the first document of each id. `all(kind)` reads `ModelSnapshot.DocumentsOf`, so a duplicate
  id (MQ1004) is no longer listed twice. Index names in keys: `ix:stereotype:`, `ix:tag:`, `ix:property:`, `ix:qn:`.
- (C31) `byQualifiedName` lists its candidates ordinal by id: they share the name's last segment, so this is the (name,
  id) order of the other lists, and it is the order its `ix:qn:` hash covers.
- (C31) `RuleKeyHasher` hashes `e:` with the holding file's `Hash`, not its `DependencyHash`: a rule never sees
  sidecars.
- (C32) A model rule's finding takes the given id as its element id (a sub-element's when `element` names one), as the
  built-in rules do; its `Math.random` seed is `x/<id>`. `RunRule` returns nothing for a model rule, and `RunModelRule`
  on an id that is not a model rule is MQ5002.
- (C32) `wholeModel` is required in the contract, since the engine always writes it, so the editor's mock backend
  (`src/editor/src/mocks/model/extensions.ts`, which also runs model rules once) and its unit test carry it; the MCP
  description of `list_extension_files` (`ModelTools.Extensions.cs`) names `wholeModel` and MQ5005, as 2.5's row says.
- (C33) An entry also needs the element file's path unchanged, since the cached findings carry it. A registration whose
  `DeclaredIn` is not one of the snapshot's rule scripts (the test fakes) is never cached. Only a whole-model validation
  that ran element rules sweeps the cache (so `includeScriptRules: false` keeps it); each validation takes a ticket, and
  the sweep keeps entries a later validation running at the same time used. `RuleDeterminismTests` builds a validator
  per run, so its loaded run executes the rules instead of reusing the idle run's results.
- (C review) Every whole-model validation with script rules sweeps the cache, not only one that ran element rules, so
  entries go when every element rule is gone (rule files deleted or failing to load, only whole-model rules left);
  `includeScriptRules: false` still keeps them. A rule whose `kinds` leave a document out is skipped before the cache
  lookup (no lease, no `RunRule`, no empty entry); the kinds ride on `ScriptRegistration` as an internal member, so the
  contract does not change.

**Model and schema**

`schemas/v1/maquettiste.json` `limits` (+ sample copy; `src/editor/src/mocks/openapi.json` through `npm run gen`):

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `scriptTimeoutMs` | integer, minimum 0 | 2000 | 0 turns the wall clock off; statements still bound each call. |
| `scriptStatements` | integer, 1 to 2000000000 | 5000000 | Per top-level call; a whole-model rule spends one budget. |

**Public API**

```csharp
namespace Maquettiste.Engine.Scripting;
public sealed record ScriptRegistration(ScriptRegistrationKind Kind, string Name, string DeclaredIn)
{
    /// <summary>A rule registered with scope "model": run once per whole-model validation, not per element.</summary>
    public bool WholeModel { get; init; }
}
public interface IScriptSandboxPool   // gains
{
    /// <summary>Load warnings, ordinal by path: MQ5005 per rule file with top-level let or var, at the first.</summary>
    IReadOnlyList<Diagnostic> Warnings { get; }
}
public interface IScriptSandbox   // gains
{
    /// <summary>Runs a whole-model rule once; report(message, { element, pointer, severity }).</summary>
    IReadOnlyList<Diagnostic> RunModelRule(string ruleId, ModelSnapshot model, ScriptCallContext ctx);
}
namespace Maquettiste.Engine;
public sealed record ExtensionRuleInfo(string Id, DiagnosticSeverity Severity) { public bool WholeModel { get; init; } }
// SandboxLimits.ScriptTimeoutMs: 0 now means no wall-clock limit (documentation only).
```

Implementers to update: `IScriptSandbox` by `ScriptSandbox` (`ScriptSandbox.cs:66`) and the fake `Lease` in
`tests/Maquettiste.Engine.Tests/Validation/ValidationSupport.cs` (:154); `IScriptSandboxPool` by `ScriptSandboxPool`
(`ScriptSandboxPool.cs:11`) and the fake `Pool` in the same test file (:141). `CountingFactory`
(`Rendering/DeterminismTests.cs:162`) implements only the factory and does not change. The contract's
`ExtensionRuleInfo` (`docs/api/openapi.yaml:11963`) gains `wholeModel`.

**Engine behaviour**

- Timeout: `ScriptSandbox` (:118) calls `TimeoutInterval` only above 0; `RegexTimeout` (:169) is 250 ms at 0;
  `PreviewDeadlineMs` (`GenerationService.Packs.cs:125`) uses 2000 × 4 at 0; `MaxStatements` clamps to the cap. The
  MQ6007 deadline messages (`GenerationService.Packs.cs:88`, :113; `GenerationService.Authoring.cs:144`, :160, :198)
  read "did not finish within <n> ms (the preview deadline: 4 × limits.scriptTimeoutMs, or 8000 ms when it is 0)".
- Scope per file: `PreparedScript.PrepareAll` (:32) wraps each rule script (`IsRuleScript`) as `(function () {` + code
  + `\n})();`, the prefix on line 1, so lines stay; line-1 columns drop the prefix length and an error past the last
  line goes on the last line, in syntax errors and in `Translate` (`Calls.cs:75`). When every script of a pool is a rule
  script, `Initialize` (:206) runs `DeepFreeze(global)` after the setup and before the scripts, instead of after them.
- MQ5005: `PrepareAll` finds top-level `let` and `var` statements in each rule script's prepared AST, and the pool
  exposes them as `Warnings` (`ScriptSandboxFactory` passes them to `ScriptSandboxPool`). `ModelValidator` adds the
  pool's warnings to whole-model reports only, as it adds `ReferenceDataRules.CheckSettings` (:66-71).
  `CheckRuleScript` (`ModelStore.Extensions.cs:258`) returns them with the file's diagnostics, so
  `write_extension_file` and `list_extension_files` show MQ5005 at once.
- `RegisterRule` (:474) reads `scope` (`"element"` by default, or `"model"`); `kinds` with `"model"` is a TypeError.
  `RunModelRule` calls `check(model, report)` under `Run` (`Calls.cs:33`); `report` needs an `element` option naming a
  document or sub-element, whose file the diagnostic takes (the entry pointer prefixes the given one). An unknown id is
  a TypeError, so MQ5002 on the rule file. `ModelValidator.ValidateAsync` (:42) runs model rules on one lease after the
  per-document loop when `scope.ElementIds is null`, except those set `off`; `RunScripts` (:678) skips them.
  `CheckRuleScript` fills `ExtensionRuleInfo.WholeModel` from the internal `ScriptSandbox.Rules`.
- Indexes: internal `RuleModelIndex`, built lazily per snapshot in a `ConditionalWeakTable`, thread-safe for parallel
  engines, holds the by-stereotype, by-tag and by-property-key indexes. `all(kind)` uses the snapshot's sorted kind
  index (:24, a new internal accessor) instead of a scan. `byQualifiedName` parses its argument with 5.1's
  `QualifiedName` and asks the snapshot's `NameIndex`, whose lazy member is thread-safe (5.1 decision 8). A malformed
  name, or a kind without names, is a TypeError. It returns what `get` returns for each id.
- Keys: `get` records `e:`; `all` records `k:` and `e:` per document; index functions record `ix:<index>:<value>`
  (hashed over the sorted ids returned; `ix:qn:<canonical name>` for `byQualifiedName`) and `e:` per document;
  `referencesTo` records a new `rt:<id>`, hashed over every reference entry, since `r:` skips the id's own file and
  diagrams (`ModelSnapshot.cs:275`). Internal `RuleKeyHasher` per snapshot hashes them.
- Cache: internal `RuleResultCache` on `ModelValidator`, keyed by (rule id, element id). An entry is reused only when
  the element file hash, the rule file hash (`ScriptSource.Hash` of the registration's `DeclaredIn`), the limits and
  every recorded key's current hash are unchanged. A rule declared in a file with an MQ5005 warning is never cached.
  Failures (MQ5002, MQ5003) are not cached. A whole-model validation drops the entries it did not use, bounding memory.

**CLI and MCP**

No new verbs or tools. `validate` runs model rules on whole-model runs; `list_extension_files` and
`GET /api/extensions` show `wholeModel` and MQ5005; `Agent/tools.json:703` text describes 0.

**Rules**

| Id | Severity | Rule | Quick fix |
| --- | --- | --- | --- |
| MQ5005 | W | A rule file declares top-level `let` or `var` variables; a rule keeps no state between calls, so its results are never cached. | — |

**Docs**

`docs/user-guide.md` (rules :2801-2871: model rules, index functions, a scope per file and the early freeze, 0 for CI
and the regex bound, MQ5005; limits at :2857), `Scripting/README.md`, `Validation/README.md` (:64-74),
`docs/engineering/engine-design.md` (§10, §15, D12), `docs/engineering/generation-ui.md:215` (the 8 s deadline also at
0), `docs/mcp.md` (:283, :335), `skills/maquettiste-modeling/SKILL.md` (:67: whole-model rules, 0 for CI),
`docs/api/openapi.yaml` (`ExtensionRuleInfo.wholeModel`), `docs/releases.md`. Editor follow-ups: completions for the
new `model` functions (`ruleCompletion.ts:181`); a model-rule badge on the Extensions screen.

**Tests**

- Acceptance: `ModelRuleTests.A_whole_model_rule_that_every_reference_type_is_used_runs_once_per_validate`
  (Integration, real sandbox). A billing copy gains `currency` (used by an Invoice attribute) and `country` (unused).
  The test builds a `ModelValidator` over a new test `CountingSandboxFactory` (`Validation/ValidationSupport.cs`): it
  wraps `ScriptSandboxFactory`, and its pool, lease and sandbox wrap the real ones, passing every member through and
  counting `RunModelRule` calls. One `ValidateAsync` with `ValidationScope.All` makes one `RunModelRule` call and one
  warning, on `country`.
- Acceptance: `RuleDeterminismTests.The_same_model_gives_the_same_diagnostics_under_cpu_load_with_the_clock_off`, in a
  new collection with `DisableParallelization = true` (as `CultureCollection` in the Packs tests), so the spinning
  threads never starve the wall-clock tests the test runner runs in parallel. With `scriptTimeoutMs: 0`, rules without
  regexes that spend about 5 million statements per element validate once idle and once beside twice the processor count
  of spinning threads; the report bytes match. With `scriptTimeoutMs: 1` the same model gives MQ5003, showing the clock
  would matter. `RegexRetry`'s own tests cover the regex retry.
- `SandboxRestrictionTests`: `A_zero_timeout_lets_a_long_rule_finish_under_the_statement_limit`,
  `The_statement_cap_still_stops_an_infinite_loop`, `Regex_matches_keep_their_bound_with_the_clock_off`.
  `RuleScopeTests`: `Each_rule_file_has_its_own_scope`, `A_rule_cannot_call_a_function_of_another_rule_file`,
  `A_rule_file_cannot_publish_a_global_for_another_file`, `Syntax_errors_keep_their_line_and_column`,
  `Top_level_let_or_var_is_MQ5005_on_validate_and_on_the_extension_file`.
- `RuleIndexTests`: `Indexes_return_documents_in_name_then_id_order_and_record_their_keys`,
  `By_qualified_name_reads_the_name_index`, `References_to_cover_the_own_file_and_diagrams`. `ModelRuleTests`:
  `Element_saves_do_not_run_model_rules`, `A_settings_save_runs_model_rules_and_refuses_an_error_they_introduce`,
  `A_model_rule_reporting_an_unknown_element_is_MQ5002`, `Kinds_with_scope_model_is_a_load_error`.
- `RuleCacheTests`: `An_unchanged_element_reuses_its_results`, `A_change_to_any_recorded_key_runs_the_rule_again`,
  `A_severity_override_applies_to_cached_results`, `Files_with_MQ5005_are_never_cached`,
  `Cached_and_fresh_reports_are_byte_identical`.

**Tasks**

- **C29. Timeout 0 and the statement cap.** Files: `schemas/v1/maquettiste.json` (+ copy), `Scripting/ScriptSandbox.cs`,
  `GenerationService.Packs.cs`, `GenerationService.Authoring.cs` (the MQ6007 texts), `Agent/tools.json`,
  `docs/engineering/generation-ui.md`, `docs/user-guide.md` (limits). Tests: the `SandboxRestrictionTests` additions,
  the determinism acceptance test and its collection. Verify: `E "~Scripting|~RuleDeterminismTests|~Json"`;
  `E "~AgentToolsTests"`; `editor`.
- **C30. A scope per rule file.** A scope per rule file, the early freeze, MQ5005. Files: `Scripting/ScriptSandbox.cs`,
  `Scripting/ScriptSandbox.Calls.cs`, `Scripting/ScriptSandboxFactory.cs`, `Scripting/ScriptSandboxPool.cs`,
  `Scripting/ScriptingContracts.cs` (`Warnings`), `Validation/ModelValidator.cs` (warnings on whole-model reports),
  `ModelStore.Extensions.cs` (`CheckRuleScript`), the fake `Pool` in `ValidationSupport.cs`,
  `Diagnostics/RuleCatalog.cs` (+ count). Tests: `RuleScopeTests`. Verify: `E "~Scripting|~ExtensionFilesTests"`;
  `E "~ModelValidatorTests|~CatalogTests"`; `record`.
- **C31. Rule indexes and read keys.** Indexes and sound read keys (after C3, `QualifiedName` and a thread-safe
  `NameIndex` member). Files: `Scripting/ScriptSandbox.Calls.cs`, `Validation/RuleModelIndex.cs` (new),
  `Validation/RuleKeyHasher.cs` (new), `Model/ModelSnapshot.cs` (kind index accessor). Tests: `RuleIndexTests`,
  `RegistrationTests`. Verify: `E "~Scripting|~QualifiedNameTests"`.
- **C32. Whole-model rules.** Files: `Scripting/ScriptingContracts.cs` (`RunModelRule`, `WholeModel`),
  `Scripting/ScriptSandbox.cs`, `Validation/ModelValidator.cs`, `ModelStore.Extensions.cs` (`ExtensionRuleInfo`),
  `ValidationSupport.cs` (the fake `Lease`; `CountingSandboxFactory`, new), `docs/api/openapi.yaml`
  (`ExtensionRuleInfo.wholeModel`), generated editor files, `skills/maquettiste-modeling/SKILL.md`, `docs/mcp.md`.
  Tests: `ModelRuleTests` with the acceptance test. Verify: `E "~Validation|~Integration|~ModelStoreTests"`;
  `F "~ExtensionEndpointTests|~ContractTests"`; `C "~SkillRefreshTests"`; `editor`.
- **C33. Rule result cache.** Per-element result cache. Files: `Validation/RuleResultCache.cs` (new),
  `Validation/ModelValidator.cs`. Tests: `RuleCacheTests`. Verify: `E "~Validation"`; `C "~Maquettiste.Cli.Tests.Mcp"`.

## 6. Implementation order

Tasks run in the order below. A task starts only after the tasks in its "After" column; the reason says what it needs
from them. A task with no entry depends only on the tasks of earlier milestones. Each milestone ends with a gate.

**Gate** (the same at the end of each milestone):

1. `build`.
2. `E`, `C` and `F` without filters: every .NET suite.
3. `PG P` without a filter: the pack tests, including those that need the PostgreSQL container.
4. `editor`, since every milestone touches `src/editor` or `docs/api/openapi.yaml`. Milestone C also runs
   `cd src/editor && npx playwright test --project=mock tests/e2e/reference-data-feedback.mock-only.spec.ts` (C22).
5. A short pre-release note `tmp/response-next-capabilities-note-<slug>.md`: what changed and how to try it, with
   billing and invented names only, nothing about any customer. The milestone slugs are `migration-ledger`,
   `typed-links` and `generation`.
6. A commit script `tmp/NN-commit-<slug>.sh` (the next free number) that commits the milestone on `phase-1`. The owner
   runs it; no task commits.

### Milestone A: sections 3.1 to 3.5

The customer's priority is migration facts with `validate --against`, so steps 1 to 9 build exactly that. When the
owner wants it sooner, the gate may run after step 9 with the note slug `validate-against`; the rest of milestone A
then lands in the milestone's own pre-release.

| Step | Task | After | Reason |
| --- | --- | --- | --- |
| 1 | A1 Load a baseline | | |
| 2 | A5 Kind and setting | | Carries the empty-name case, so the `clean` fixture's fact validates |
| 3 | A6 Widening table | | |
| 4 | A7 Fact rules | A5, A6 | The kind; stale pending facts read the widening table |
| 5 | A8 Order and projection | A5 | The kind |
| 6 | A9 Classifier core | A6, A8 | Widenings; current keys and applied order |
| 7 | A10 Recording on store writes | A7, A9 | MQ4101 in save answers; the classifier |
| 8 | A11 Other write paths | A10 | The recording hook |
| 9 | A12 `validate --against` | A1, A10 | Baselines; captures and the classifier |
| 10 | A2 Workspace detection leaves the engine | | |
| 11 | A3 Generate against a baseline | A1, A12 | The capture helper with the rekey step |
| 12 | A13 `migration list` | A8, A12 | Applied order; stale marks |
| 13 | A14 `migration fold` | A12, A13 | Stale chains; `MigrationCommand.cs` |
| 14 | A4 Once files stay owned | | Needed before content-named migrations (A16) |
| 15 | A15 Facts reach the schema diff | A3, A8 | `PhysicalSnapshot.Facts`; applied order |
| 16 | A18 Ledger engine contract | A5 | `MigrationSettings` |
| 17 | A19 Ledger resolver, planner and helper | A18 | The contract |
| 18 | A20 sql-ddl schema rows | A8, A19 | `database.migrations`; `ledger_ddl` |
| 19 | A16 sql-ddl renders facts | A4, A15, A20 | Owned once files; `SchemaDiffResult.Facts`; `_ledger.scriban` |
| 20 | A21 Ledger CLI and MCP | A13, A18 | `MigrationCommand.cs` and the tool pins A13 moved |
| 21 | A22 Ledger docs and review note | A21 | |
| 22 | A24 Index nulls | | |
| 23 | A25 Range columns, engine and sql-ddl | A16, A24 | The fact step that calls `range_using`; shared files |
| 24 | A26 Range columns in csharp-dapper | A25 | Range keywords |
| 25 | A27 SQL object environments, engine | | |
| 26 | A28 SQL object environments, sql-ddl | A16, A20, A27 | The rekey step; `_ledger.scriban`; the resolved fields |
| 27 | A29 Name rules | | |
| 28 | A30 Literal defaults | A25 | Range defaults |
| 29 | A17 Docs sweep | A1 to A16 | |

Gate A. A23, the ledger freeze, belongs to 3.3 but runs at the phase close.

### Milestone B: section 4

B7 and B13 reuse 5.1's batch surface (C6), so milestone B also runs all of 5.1 (C1 to C9). Section 5.1 then lands
whole in milestone B's pre-release, with its own note (C9).

| Step | Task | After | Reason |
| --- | --- | --- | --- |
| 1 | B1 Common fields in the schemas | | Every kind schema, `migration.json` included |
| 2 | B2 Resolution, retired ids and MQ2022 | B1 | The fields |
| 3 | B3 Membership on every kind | B1 | Edits the same kind schemas after B1 |
| 4 | B4 Schema set and index pass | B3 | |
| 5 | B5 Validator sites and pack parameter checks | B4 | The schema set |
| 6 | B6 Deletes, saves and pack parameter deletes | B5 | The validator sites |
| 7 | B8 Keys, effective set and readers | B3 | `stereotype.json` after B3 |
| 8 | B9 Stereotype rules and parameters | B4, B8 | Parameter sources; the effective set |
| 9 | B10 Layering | B3 | `package` on every kind |
| 10 | B11 Seeds across packages | B3 | `Seed.Package` |
| 11 | B12 Translatable properties | B4 | `PropertySchemaSet` |
| 12 | C1 Batch schema, records, rule ids | | |
| 13 | C2 Minting and the "keep its id" refusal | C1 | |
| 14 | C3 Qualified names | B3, C1 | The package forms read `package` |
| 15 | C4 Batch name resolver | B4, C2, C3 | `x-ref` walker sites; minting; the name index |
| 16 | C5 Dry-run diff and diagnostics delta | C4, A10 | The resolved batch; recorded facts |
| 17 | C6 MCP and CLI batches | C5 | |
| 18 | C7 Assistant pinning | C4 | `BatchNames.ResolveTargets` |
| 19 | C8 Batch contract, skill and guide | C6, C7 | |
| 20 | C9 Batches pre-release note | C8 | |
| 21 | B7 `names-to-ids` | B6, C3, C6 | Delete rules; `NameIndex`; `model apply --dry-run` and `dryRun` |
| 22 | B13 `tags-to-property` | B7 | The shared name resolver |
| 23 | B14 Milestone close | B1 to B13 | |

Gate B.

### Milestone C: sections 5.2 to 5.7

| Step | Task | After | Reason |
| --- | --- | --- | --- |
| 1 | C10 Catalog summaries | | |
| 2 | C11 Patch | C4 | The batch resolver and minting |
| 3 | C15 Seed lookups in templates | C3 | Seed forms in `NameIndex` |
| 4 | C16 Seed input by name | C4 | `BatchNames` in `ApplyBatchCoreAsync` |
| 5 | C17 Closing block markers | | |
| 6 | C18 Copy units: manifest, loader, matcher and planner | C17 | `pack.json` and `PackManifest.cs` after C17 |
| 7 | C19 Copy units: render, preview and write | C18 | |
| 8 | C20 Six helpers | C15 | The names list holds `seed_named` and `seed_row` |
| 9 | C21 Database and attribute scopes | C20 | `BuiltinHelpers` after C20 |
| 10 | C22 Shipped packs on `each database` | C21, A28 | The scope; the `object-environments` unit |
| 11 | C23 Definitions on `model` | B2, B8 | The keys it keeps excused |
| 12 | C24 Null contract | C22, A28 | The same sql-ddl files |
| 13 | C25 Plain helpers | | |
| 14 | C26 Selection and plan filter | | |
| 15 | C27 Keep rule, last run and snapshots | C26, A3 | The selection; the last-run bypass for baselines |
| 16 | C28 Scoped generation CLI, MCP and contract | C27 | |
| 17 | C12 Generate | A3, C26, C27 | `Against`; `Units` and `Elements`; the keep rule |
| 18 | C13 Agent answers skill, guide and release notes | C10, C11, C12 | |
| 19 | C14 Agent answers pre-release note | C13 | |
| 20 | C29 Timeout 0 and the statement cap | | |
| 21 | C30 A scope per rule file | C29 | The sandbox changes |
| 22 | C31 Rule indexes and read keys | C3, C30 | `QualifiedName` and the thread-safe `NameIndex`; frozen scopes |
| 23 | C32 Whole-model rules | C30 | |
| 24 | C33 Rule result cache | C31, C32 | Sound read keys; model rules |

Gate C.

### Phase close

A23 freezes ledger version 1 after the review answers to A22's note and after gate C. The gate runs once more, with a
commit script; the pre-release note says the ledger contract is frozen.
