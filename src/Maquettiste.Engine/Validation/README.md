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
| `AttributeRules.cs` | MQ3013 (facets), MQ3017 (credential-looking defaults, `Credentials`), MQ3019 (literal defaults, with the `defaultExpression` hint), MQ2007 |
| `PhysicalRules.cs` | MQ4001 to MQ4008, MQ4010, MQ2001 for a foreign key's missing table, MQ2002 for a schema or sequence of another database |
| `MappingRules.cs` | MQ4004, MQ4009, MQ4011 |
| `ExtensionRules.cs` | MQ5001, MQ5004 (`ExtensionSet`) |
| `ReferenceDataRules.cs` | Reference types, their use as attribute types and seeds (reference-types-seeds-localization.md sections 1.7, 2.4): MQ7001 to MQ7011, MQ7013, MQ7101 to MQ7106, the MQ3019 extension (a default code outside the rows), MQ7012 (enum lookup-table storage, retired: on mappings and, in whole-model runs, on `maquettiste.json` with MQ7007 and MQ7008 for project and database storage choices) |
| `LocalizationRules.cs` | Localization (reference-types-seeds-localization.md section 3.6), whole-model only: MQ7201 (settings, through `LocaleChains.Check`), MQ7202 (undeclared or default locale folders), MQ7203 (orphans), MQ7204 and MQ7206 (one per locale and shard), MQ7205 (per node and field, only when `validation.rules` gives it a severity), MQ7207 to MQ7210 (shard placement, sidecars, duplicates), MQ7211 (plural names on to-one ends, in element files and shards). Tested in `Localization/LocalizationTests.cs` |
| `ProcessRules.cs` | Processes (phase-3-design.md section 3), per file: MQ9001 to MQ9018 (structure, over `Processes/ProcessAnalysis.cs`: reachability ignoring guards, completion, transition groups, eventless cycles), MQ9101 to MQ9106 (gates and actors; MQ9106 on the actor file), MQ9201 to MQ9205 (lifecycle binding; the entity side of MQ9201 on the entity, MQ9016 on the process diagram). Tested in `Processes/ProcessRuleTests.cs` |
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
