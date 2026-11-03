# Validation

**Owner:** W2 Validator. See docs/engineering/engine-design.md sections 6 and 18.

Built-in rules (`RuleCatalog` ids), extension schema evaluation, JavaScript rules and SARIF (engine-design.md section 6).

- Implements: `IModelValidator` (`ModelValidator`), `SarifWriter` (public), `SaveRules` (MQ3020, for `ModelStore`).
- Consumes: `ModelSnapshot` indexes and `ElementRefAttribute`, `IScriptSandboxFactory`, `RuleCatalog`, `ValidationReport`.

## What runs

| File | Rules |
| --- | --- |
| `ModelValidator.cs` | Orchestration: scope, load diagnostics, script pool, parallel per-file rules, `validation.rules` settings, dedupe, positions, sorted report |
| `BuiltinRules.cs` | MQ2001 to MQ2007 (references through `ReferenceWalker`, stereotypes, categories, tags, rule ids), MQ3001 to MQ3012, MQ3015, MQ3016, MQ3018, MQ3022 (a package diagram without a package) |
| `StatusRules.cs` | MQ2022 (next-phase-1-design.md section 4.5): a reference to a retired element or sub-element (`ModelSnapshot.IsRetired`, from the index walks), per file over `ReferencesFrom`; a retired referrer, `package` and `parent`, owning references and diagrams are excepted. Tested in `Validation/ElementStatusTests.cs` |
| `PackageRules.cs` | MQ2020 and MQ2021 (next-phase-1-design.md section 4.3): package layering over `ReferencesFrom`, judged against `ValidationContext.Layers` (`PackageLayers`: per package its chain, whether it or a parent declares `dependsOn`, and the union it may reference), and `dependsOn` cycles (one strongly connected pass). A package's conflict peers are its subtree's members and their referrers, when some package is checked; the other packages of a cycle and the seeds a rename reorders are left to whole-model validation. Tested in `Validation/PackageLayeringTests.cs` |
| `AttributeRules.cs` | MQ3013 (facets), MQ3017 (credential-looking defaults, `Credentials`; a column's own `secret` feeds it too), MQ3019 (literal defaults, with the `defaultExpression` hint), MQ2007 |
| `PhysicalRules.cs` | MQ4001 to MQ4008, MQ4010, MQ2001 for a foreign key's missing table, MQ2002 for a schema or sequence of another database. Column lists also resolve a table's inherited columns by keyed id; a bare member id gets the keyed form; MQ4003 also compares own and inherited names of a designed or imported table |
| `MemberRules.cs` | Stereotype members on table files (next-phase-2-design.md section 3.1): MQ2027, MQ2028 and MQ4078 on a designed or imported table's `redefines` and `excludes` through `TableMembers.CheckLinks` (an overlay's are the resolver's), and `WithMembers`, the table file with its inherited columns, keys and indexes that the seed, binding and foreign-key lookups read. The stereotype's own member checks (MQ2024 to MQ2026) are in `BuiltinRules.cs`. Tested in `Validation/StereotypeMemberRuleTests.cs` |
| `ColumnTypeRules.cs` | Typed columns and column sensitivity (next-phase-2-design.md section 3.2): on a table file MQ4082 (a `typeRef` naming a value object or a built-in, or on an overlay entry), MQ4083 (the type or a stated facet cannot hold the type's values; `ReferenceBindingRules.CodeFit` judges codes as MQ7118 does), MQ4084 (a declared foreign key from a column typed by a reference type that does not point at the bound table's code column), MQ7014 (a default that is not a stored value or code; MQ7019 instead for a former code, as for table seed cells) and MQ4085 (an overlay entry and its attribute); own and inherited columns of designed and imported tables. On an entity, MQ4085 for binding fields and MQ7014 for binding constants. `ValueProblem` also serves table seed cells (in each environment the row loads in) and a reference type binding's constants; MQ4082's binding case is in `ReferenceBindingRules.cs`. An enum, a reference type or its seed brings the typed tables, their seeds and the entities and types bound to them as conflict peers. Tested in `Validation/ColumnTypeRuleTests.cs` |
| `DatabaseObjectRules.cs` | Routines, database types and SQL objects (2026-10-01): MQ4017 (nothing to create for the dialect, warning), MQ4018 (a slot type neither built-in nor a database type of the same database), MQ4019 (a column's `nativeType` or a `dependsOn` entry naming an object of another database), MQ4020 (a dependency cycle, through `DatabaseObjectIndex`), MQ2002 for a schema of another database, MQ4001; a column naming a database type skips MQ4006 and MQ4016 |
| `MappingRules.cs` | MQ4004, MQ4009, MQ4011 |
| `ExtensionRules.cs` | MQ5001, MQ5004 (`ExtensionSet`) |
| `ReferenceDataRules.cs` | Reference types, their use as attribute types and seeds (reference-types-seeds-localization.md sections 1.7, 2.4): MQ7001 to MQ7011, MQ7013, MQ7101 to MQ7106, the MQ3019 extension (a default code outside the rows), former codes (next-phase-2-design.md section 3.5: MQ7017 and MQ7018 on the type, MQ7019 in place of MQ3019, MQ7011 and MQ7009 for a value that is a former code; a seed of a type with former codes brings the type as a conflict peer, and every seed of a type its value maps), MQ7012 (enum lookup-table storage, retired: on mappings and, in whole-model runs, on `maquettiste.json` with MQ7007 and MQ7008 for project and database storage choices) |
| `LocalizationRules.cs` | Localization (reference-types-seeds-localization.md section 3.6), whole-model only: MQ7201 (settings, through `LocaleChains.Check`), MQ7202 (undeclared or default locale folders), MQ7203 (orphans), MQ7204 and MQ7206 (one per locale and shard), MQ7205 (per node and field, only when `validation.rules` gives it a severity), MQ7207 to MQ7210 (shard placement, sidecars, duplicates), MQ7211 (plural names on to-one ends, in element files and shards). Tested in `Localization/LocalizationTests.cs` |
| `ProcessRules.cs` | Processes (phase-3-design.md section 3), per file: MQ9001 to MQ9018 (structure, over `Processes/ProcessAnalysis.cs`: reachability ignoring guards, completion, transition groups, eventless cycles), MQ9101 to MQ9106 (gates and actors; MQ9106 on the actor file), MQ9201 to MQ9205 (lifecycle binding; the entity side of MQ9201 on the entity, MQ9016 on the process diagram). Tested in `Processes/ProcessRuleTests.cs` |
| `MigrationRules.cs` | Migration facts (next-phase-1-design.md section 3.2): MQ4101 (missing intent, one finding per missing field; a held fact is complete until released; the widening table decides when a type change narrows and so needs a data check), MQ4106 (fields that do not fit the fact's type; a value map changes a name or maps codes in a column or collection, with no chain), MQ4113 (a value map that maps away a live code of its reference type, next-phase-2-design.md section 3.5), MQ4108 (a dialect map without the database's dialect or `*`), MQ4056 (a fact outside PostgreSQL), and from `FactGraph`, built once per run over every fact: MQ4104 (after cycles, on each fact of the cycle), MQ4105, MQ4111 and MQ4107 (on the later fact of an unordered pair, by recorded keys). A fact's conflict peers are its database's facts and the facts whose `after` names it; for a deleted fact, the store adds the facts whose `after` named it (their MQ4111). Stale facts (MQ4103) and unexplained changes (MQ4102) need a resolved model or a baseline and are not here. Tested in `Validation/MigrationRulesTests.cs` and the `migrations` fixture |
| `BrandingRules.cs` | `maquettiste.json` `branding`, whole-model only: MQ8001 (a color that is not `#rrggbb` or `#rgb`), MQ8002 (an icon path outside `branding/<name>.svg|png`, or a missing file), MQ8003 (an icon that is not a safe SVG or a PNG of at most 512 KB; `Branding/BrandingIcons.Inspect` decides, and the editor's upload and read use it too). Reads the icon from the model root. Tested in `Validation/BrandingRulesTests.cs` |
| `SaveRules.cs` | MQ3020 |
| `SarifWriter.cs` | SARIF 2.1.0 |
| `Positions.cs` | Line and column per diagnostic (`PointerLocator`) |

Every diagnostic is reported in the file where the fix belongs, so validating a set of files gives exactly their diagnostics and
validating all files gives the model's. Cross-file conflicts (MQ3001 names, MQ4002 physical names, MQ4004 mappings and overlays,
MQ3009 navigations, MQ3016 compositions) are reported on every file after the ordinally first one, naming it. Because the
diagnostic can land on a file other than the one being saved, a scoped run also validates each listed file's conflict peers
(below), so a save that introduces a conflict is never accepted. Identical findings are reported once.

## Behavior worth knowing

- **Report contents.** The report includes the snapshot's `LoadDiagnostics` (MQ1xxx), filtered to the scope. `ModelStore` should
  not add them again.
- **Scope.** `ElementIds` null validates every file and the extension files (MQ5004). Otherwise the owners of the listed ids
  (sub-element ids map to their file), their conflict peers, and, with `IncludeReferrers`, every file that references the owner
  or any of its sub-elements. Conflict peers (`ModelValidator.ConflictPeers`) are added whether or not `IncludeReferrers` is set:
  files with the same scoped name (MQ3001) or physical name (MQ4002); the other mappings of the same target and database and the
  other overlays of the same synthesized table (MQ4004); the other compositions of the same child (MQ3016); the relations whose
  navigations land on the same inheritance hierarchy (MQ3009); an entity's descendants at any depth (MQ3007); and for a mapping,
  its relation file and, for an entity mapping, the relations touching the entity and their mappings in that database (MQ4009,
  MQ4011). Peers are validated in full, like referrers, so their other findings appear too. Load diagnostics are kept when their
  file or element is in scope.
- **Settings.** `validation.rules` sets a rule's severity (`error`, `warning`, `info`) or turns it `off`; MQ1xxx ignore `off`.
  The MQ1xxx errors whose file is left out of the snapshot or ignored (MQ1001, MQ1002, MQ1004, MQ1006, MQ1007, MQ1009) ignore
  every setting and stay errors, so a dropped file can never vanish silently; MQ1003, MQ1005 and MQ1008 can change severity.
  Settings apply to `x/` ids too.
- **Navigations (MQ3009).** A navigation is checked against the holder's own name, its flattened attributes, the attributes of
  every descendant (which inherits the navigation), and the navigations on the holder, its ancestors and its descendants; the
  first in (relation path, end) order keeps the name. Siblings in a hierarchy do not collide.
- **Patterns.** A literal default is checked against `validation.pattern` with `RegexOptions.NonBacktracking` (linear time, so
  the result never depends on machine speed). A pattern that engine cannot run (a backreference or lookaround) is valid (no
  MQ3013) but the default is not checked against it. No regex exception escapes a rule.
- **Ignored relation mappings.** A relation mapping with `ignore: true` gets its option checks (MQ4009) only; foreign key,
  junction and end bindings, and MQ4011, do not apply to a relation that is not stored.
- **Positions.** Line and column come from the file on disk when its hash still equals the snapshot's, else from the snapshot's
  parsed JSON (identical for canonical files). A pointer that does not resolve (an absent `key`, a property that comes from a
  stereotype default) falls back to its nearest ancestor that does. The column counts characters.
- **Tags.** MQ2006 fires only when the model has a tag vocabulary: info when it is not strict, error when it is.
- **Scripts.** One pool per validation (`CreatePool(RuleScripts, Limits, jobs)`), created when there are rule scripts and either
  `IncludeScriptRules` is set or a file in scope names a rule. Every registration of kind `Rule` runs on every file in scope;
  the sandbox applies the rule's `kinds` filter and severity (the registration record does not carry them). Results get the
  `x/<id>` rule id, element id and file path when the script leaves them out. A thrown `ScriptLimitException` is MQ5003, any
  other exception MQ5002; a pool that fails to load is one MQ5002 (or MQ5003) on the script file, and MQ2007 is skipped then.
  The real sandbox does not throw for a rule that throws: `RunRule` returns that failure as its own MQ5002 diagnostic (after the
  rule's reports), located at the failing line of the rule script. Such catalog diagnostics keep their rule id and script
  position (integration stage fix: they used to be relabelled `x/<id>`, which hid the script error behind the rule's own id).
  Because they no longer carry `x/<id>`, the rule's own `validation.rules` setting is applied in `RunScripts`: a rule set to
  `off` is not run at all (so a throwing rule that is off no longer stops generation), and a severity setting also applies to
  that rule's MQ5002 and MQ5003 failures. An explicit `MQ5002` or `MQ5003` setting still applies on top (WI review).
  Whole-model reports also carry the pool's load warnings (`IScriptSandboxPool.Warnings`: MQ5005 per rule file with a
  top-level `let` or `var`, at the first; next-phase-1-design.md section 5.7), as they carry the settings rules; a validation
  of some elements does not.
- **Whole-model rules** (section 5.7). A rule registered with `scope: "model"` (`ScriptRegistration.WholeModel`) is left
  out of `RunScripts` and runs once, on one lease, after the per-document loop, in id order, when the validation is
  whole-model (`scope.ElementIds` null) and `IncludeScriptRules` is set, unless `validation.rules` sets it `off`. That
  covers `validate` on every host, generation's validate stage, the store's whole-model report and both validations of a
  settings save (so a settings change that raises a model rule's finding to an error is refused); element saves skip it.
  Its findings carry the file of the element they name; a throw is MQ5002 and a limit MQ5003 on the rule file, with the
  rule's severity setting applied as for element rules. Its results are not cached.
- **Library packs** (next-phase-2-design.md section 5.2). With `ModelSnapshot.ScriptLibraries`, a validation creates one
  pool per library first, in dependency order, each reaching its own closure, then the rule pool with the libraries
  overload of `CreatePool`, so `maquettiste.use("naming")` works at the top of a rule file. A rule's call goes through
  `RuleRun` (`IScriptLibraryCalls`): it rents an engine of the library's pool, runs the helper with the library's
  parameters and the rule's recorder, and returns it. A library that fails to load, or fails in a call, is MQ5002 (a
  limit MQ5003) at the library's script, naming the library. The libraries' hashes fold into every rule file's cache key
  (`CachedRuleFiles`), so a cached result is never served after a library changed; without `uses` the keys are unchanged.
- **Rule result cache** (section 5.7). `RuleResultCache`, one per validator (so per store: the MCP server, the editor
  host and watch mode keep it), holds each element rule's raw diagnostics per (rule id, element id), before
  `ApplySettings`, so `validation.rules` applies after the cache and an `off` rule never runs. An entry is reused only
  while the element file's hash and path, the rule file's hash (`ScriptSource.Hash` of the registration's `DeclaredIn`),
  the limits and the current hash of every key the call recorded (`RuleKeyHasher`: `e:` the holding file's hash, `k:`
  the kind's id set, `ix:` the sorted ids found, `rt:` every reference entry) are unchanged. Not cached: a run that
  failed (MQ5002 among its results, a limit, cancellation), a rule of a file with MQ5005, a registration whose file is
  not one of the snapshot's rule scripts (the test fakes). A rule whose `kinds` leave a document out is skipped before
  the cache: no lease, no call, no entry. Every whole-model validation with script rules drops the entries it did not
  use, all of them when no element rule ran (every rule file deleted or failing to load, or only whole-model rules
  left); a ticket per validation, so one running at the same time keeps what it uses. `includeScriptRules: false` keeps
  them. Nothing is written to disk.
- **Extensions.** Compiled here with JsonSchema.Net (base URI `https://maquettiste.invalid/extensions/<file>`, invariant
  culture), not through `ISchemaRegistry`, whose `Evaluate` only takes the embedded schema files. Leaf failures only, as D34.
  A failing value that comes from a stereotype's `defaultProperties` points at that stereotype in the element's `stereotypes`.
- **MQ3020** needs the stored model, so it is `SaveRules.Check(current, proposed, path)`, which `ModelStore` calls on save.
- **Synthesized tables.** Only what files state is checked. Names made by conventions are not checked against the identifier
  limit here: that needs the resolver's casing, inflection and pattern expansion (W3), and no design section yet assigns
  MQ4001/MQ4002/MQ4003 on conventional names to the resolver. **Open contract gap**: until the design gives these checks to the
  resolver (emitted in `ResolvedModel.Diagnostics` and surfaced by `validate` and saves), conventional names over the limit are
  not reported by anyone. Column keys in overlays are checked structurally: every id segment must exist, the first must fit the overlay's
  target (an attribute of the entity or its TPH descendants, an end touching it, a relation's end or attribute, a value
  object member for child tables); `id`, `position`, `discriminator`, `code` and `name` are accepted.
- **MQ4006** uses the validator's own native type lists per dialect, extended by the values of the dialect's effective type map (the embedded `Resolution/Dialects/*.json` with `typeMaps` applied), by the native types the active custom types declare for the dialect (`nativeTypes`, 2026-10-01) and by the snake and kebab names (with or without `_t`) of the model's reference types and enums; quotes and a schema prefix are stripped first, and SQLite accepts any name. Any other quoted or qualified name is a type the database defines: **MQ4016** (info) reports it once per type and database, on its first column in path order, with the number of columns that use it (2026-10-01).

Tests: `tests/Maquettiste.Engine.Tests/Validation/`; fixtures under `tests/fixtures/validation/`.

## Scripted rows (next-phase-2-design.md section 3.4, D43)

- `ValidationContext.RowsOf(seed)` gives a seed's stored rows, or its scripted rows evaluated once per validation
  through the engine's rows cache. A save (a scoped validation) evaluates only the scripted seeds in its scope
  (`ValidationContext.Judges`); a finding that needs the rows of a scripted seed outside it (a code no stored row holds,
  MQ3019, MQ7009, MQ7011, MQ7014; a natural key or foreign key no judged row holds, MQ7111) waits for whole-model
  validation, which judges stored and scripted rows.
- The whole-model validation previews wait on (`ValidationScope.Previews`: the store's shared validation per snapshot,
  the SQL previews and the table lists) sets `ValidationContext.DeferScriptedRows` and judges no scripted seed, as a
  save judges none outside its scope; validate, plan and generate judge them all (decision 31).
- `ReferenceDataRules.CheckSeed` reports a scripted seed's MQ7116, MQ7127, MQ7128 and MQ5005 first, then runs the row
  rules on the scripted rows; their findings point at `/rowsFrom/script` and name the row by its key in seed terms, a
  string in single quotes as written (`Scripted row 'R&D': ...`).
