# Rendering

**Owner:** W5 Renderer. See docs/engineering/engine-design.md section 18.

The Scriban renderer (stage 6), the template cache, the tracking context, built-in helpers and the delimiter translator
(engine-design.md sections 8 and 9). Implemented on Scriban 7.5.0; no `NotImplementedException` is left.

Tests: `tests/Maquettiste.Engine.Tests/Rendering/`; fixtures under `tests/fixtures/templates/` (the `billing-demo` pack and its
golden tree).

## Files

| File | Holds |
| --- | --- |
| `Renderer.cs` | `Renderer` (`IRenderer`): parallel workers, ordered streaming through a bounded window, progress, cancellation. |
| `RenderRun.cs` | `RenderRun` (one per call: inflector, per-pack runtimes), `PackRuntime` (sandbox pool, JavaScript helpers, MQ6013), `UnitRun` (one unit: variables, transforms, main/companion/output rendering, error mapping). |
| `TemplateCache.cs` | `ITemplateCache`, `TemplateCache`, `TemplateInfo`, `TemplateException` (MQ6003), `PackPaths` (confinement to the pack folder). |
| `TrackingTemplateContext.cs` | The per-unit `TemplateContext`, `PackTemplateLoader` (includes), `UnitGlobals` (variables). |
| `TemplateValues.cs` | `TemplateMemberCatalog` (visible types and members), `ViewAccessor`, `TrackedList`, `ValueList`, `MapView`, `UnitRecorder`. |
| `BuiltinHelpers.cs` | The helpers, the shared read-only builtin object, `HelperFunction`, `RenderHelperException`. |
| `TypeOf.cs`, `SqlDialects.cs` | `type_of`; SQL quoting and literals. `Resources/reserved-<dialect>.txt` are the embedded reserved-word lists. |
| `DelimiterTranslator.cs` | Custom delimiters to `{{ }}`, `PositionMap`. |

## What is implemented

- **Renderer.** `RenderAsync` renders on `RenderContext.MaxDegreeOfParallelism` workers (0: `EngineOptions` parallelism) and
  yields units in input order through a reorder window of 4 × workers (results are released once consumed, so memory stays
  flat). Every unit renders synchronously on one worker thread with a fresh `TrackingTemplateContext` and, when its pack has
  scripts, one sandbox leased from the pack's pool for the whole unit. One pool per pack and call, sized to the workers, created
  on first use and disposed when the enumeration ends. `RenderOneAsync` does the same for one unit (pool of size 1). A failed
  unit has no files, `Failed = true` and its diagnostics (sorted by `Diagnostic.Order`); cancellation throws
  `OperationCanceledException`, also when it lands in the middle of a unit: any failure raised while the token is set (Scriban
  reports its own cancellation as `ScriptAbortException`) is rethrown as cancellation, never MQ6006, and `RenderAsync` checks the
  token again after each result arrives, so a unit rendered while the cancel was arriving is never yielded. `InputHash = Hasher.InputHash(StaticHash, sorted read keys)`. Progress: one `Render` update per
  unit, in order, with the first output path (else the unit key).
- **Template cache.** Per run (`EngineServices.CreateRenderer()`), so nothing outlives a run in a long-lived host. Each
  (pack root, path) is read and hashed once per run (UTF-8 strict, BOM stripped, CRLF/CR to LF); each
  (pack, path, file hash, delimiters) is parsed once, whatever the number of threads; failures are cached, so every unit of a
  broken template reports the same MQ6003 with line and column (mapped back through the delimiter translator). Paths are
  normalized (`\` to `/`, leading `./` dropped) and refused when rooted, drive-qualified, or holding `.`/`..`/empty segments, or
  when their real path (every symbolic link resolved) leaves the pack folder's real path.
- **Context** (section 9): `StrictVariables`, relaxed member access, strict target access, `LoopLimit`/`RecursiveLimit` from
  `SandboxLimits`, `NewLine = "\n"`, invariant culture, snake_case names, `MemberFilter` that admits nothing (member access goes
  through the renderer's own accessors), regex timeout of `ScriptSandbox.RegexTimeout` (the script limit capped at 250 ms, as for scripts, since a match cannot be
  interrupted by the token; a timeout fails the unit with MQ6007), and output/string limits of 64 Mi
  characters that throw (MQ6007) instead of Scriban's default silent truncation at 1 MiB. Variables: `model`, `element`, the
  scope alias (`package`, `entity`, `relation`, `enum`, `value_object`, `table`; other kinds use their kind name with `_`),
  `pack` (`name`, `version`, `params`), `mapping`, `mappings`, `schema_diff`, `hints`, `data`, `unit` (`id`, `key`); all are
  read-only.
- **Tracking.** `GetMemberAccessorImpl` gives members only to template-visible types: the public R-types (as W4's
  `MemberCatalog.IsProxyType`), `GenerationHints`, the schema-diff records and the renderer's views; only public properties,
  snake_case, with `Dependencies`, `MembershipKeys` and `ResolvedModel.Source`/`Settings`/`Diagnostics` hidden and no method
  callable. Any member read (or member listing) of an `IResolvedObject` records its `Dependencies`. Every `RList<T>` reaches
  templates as a `TrackedList` whose enumeration, indexing, `size` or `Contains` records its `MembershipKeys`. Reading `mapping`,
  `mappings` or `hints` records the element's dependencies (mapping objects carry none). `schema_diff` reads record
  `k:database` (the map's key set follows the databases), and a name that is a database also records that database's
  dependencies and `d:<databaseId>`; a name that is not a database records every database's dependencies (a rename could make it
  one). Enumerating it, `object.size` or `object.keys` record `k:database` even when the map is empty. `lookup` records `e:<id>` for a missing id and the object's dependencies otherwise. The main template, the
  companion and every include record `t:<pack>/<path>`. JavaScript calls get the same recorder. The recorder deduplicates
  per object and per key list.
- **Helpers** (all section 9 names): `pascal camel snake kebab upper_snake` (`Text/Casing`), `pluralize singularize` (`Text/Inflector`
  with `inflection` overrides, records `s:inflection`; an element argument returns its `PluralName`/`Name`), `type_of`,
  `sql_quote`, `sql_literal`, `indent`, `dedent`, `escape_md`, `escape_xml`, `escape_json`, `json`, `has_stereotype`, `has_tag`,
  `in_category`, `lookup`, `banner`, `file`. Pack JavaScript helpers register under their own names (callable and pipeable);
  a name that collides with a builtin or a variable is MQ6013 and fails every unit of the pack. Transforms run before the
  template, in `transforms` order, merged into `data` (later wins).
- **Builtins.** `date.now`, `date.utc_now`, `math.random`, `math.uuid`, `object.eval`, `object.eval_template` fail with MQ6012.
  Scriban's culture-sensitive `string.capitalize`, `string.capitalizewords`, `string.starts_with`, `string.ends_with`,
  `string.index_of`, `array.sort` and `regex.match`, `regex.matches`, `regex.replace`, `regex.split` (Scriban omits
  `RegexOptions.CultureInvariant`, so `i` would match differently under tr-TR) are replaced by invariant/ordinal versions.
  `date.parse`, `date.to_string` and `date.parse_to_string` are wrapped so no output depends on the machine's time zone: without
  a pattern, `date.parse` reads offsets and `Z` as UTC instants (`AdjustToUniversal | AssumeUniversal`, invariant culture unless
  `culture:` is given) and returns UTC dates; with a pattern, Scriban parses the written wall clock and the result is marked UTC
  unchanged; a pattern holding `%Z`/`%z` is refused (MQ6006), since Scriban converts such values to machine-local time;
  `date.to_string` treats every date as UTC, so `%Z` prints `+00:00`. The builtin object is built once per renderer
  and every part of it is read-only, so templates cannot change it (`date.format = …` fails).
- **Delimiters.** `DelimiterTranslator` as section 9 says; see the class comment. Scriban escape blocks in the source
  (`{%{ … }%}`) are kept verbatim, so raw blocks still pass literal braces and literal custom delimiters through.
- **File blocks.** `file <path> <content>` adds a `FileRole.Block` file (path prefixed with `PackSettings.Output`). Files are
  ordered main, companion, blocks (emission order). Text outside file blocks in a unit without `output` is MQ6011 (warning;
  `validation.rules` can change its severity or turn it off).
- **Errors.** Parse errors MQ6003; runtime errors MQ6006; loop, recursion and size limits MQ6007; script errors and limits keep
  W4's MQ6016/MQ6007 diagnostics; non-deterministic builtins MQ6012. Template diagnostics carry the repo-relative template path
  (includes: the partial's path), 1-based line and column, and the unit's element id. Errors of `output` expressions point at
  `<pack>/pack.json` with `/units/<i>/output` (or `/units/<i>/companion/output`).

## Choices and deviations

- `ITemplateCache` (internal) gained `GetInline` (output expressions), `ReadText` and `Describe` (position mapping); its
  `Get` signature is unchanged.
- `output` and `companion.output` expressions always use `{{ }}`, whatever the unit's `delimiters` (they live in `pack.json`,
  not in the template file).
- `sql_quote <name|table|column|view|sequence|schema|database> [<dialect|database>] [<always|reserved|never>]`: the dialect may be
  omitted when the name is a table, column or view, or the unit's element is (or belongs to) a database. Quoting comes from the
  third argument, else that database's `quoting`, else `reserved`. `reserved` quotes reserved words of the dialect **and** any
  name that is not `^[A-Za-z_][A-Za-z0-9_]*$`. Dialect aliases: `postgres`, `pg`, `mssql`, `mariadb`.
- `type_of`: pack maps (`types/<target>.json`) win over dialect names; a collection attribute takes the map's `collection`
  pattern, otherwise an optional attribute or nullable column takes `nullable`. With a dialect target, a column of a database
  in that dialect yields its resolved `native_type`; anything else goes through the dialect map (enums as `int32`, value objects
  as `json`). Reading a pack map records **`t:<pack>/types/<target>.json`** (the static hash does not cover type maps), so W6's
  `DependencyHasher` must hash that path like a template.
- `sql_literal`: MySQL string literals also double backslashes (MySQL treats `\` as an escape by default).
- `indent` takes 0 to 1024 spaces or a prefix string; a result over the 64 Mi-character limit is MQ6007 before anything is
  allocated.
- A failure with no Scriban position names the unit's main template (or no file when the main template did not load).
- Scriban's date members (`d.year`, `d.hour`, …) render empty: the member filter admits no CLR members outside the
  template-visible types, `DateTime` included. Use `date.to_string` with a pattern.
- `json` refuses model objects (MQ6006) rather than walking the object graph; pass plain values such as `element.properties`.
- Maps keep their keys as written (`pack.params.useRecords`, `data.attributeCount`); only CLR property names are snake_cased.
- A pair companion is rendered on every run; writing it only when missing is the writer's owned-file rule (section 12.3).
- An escaped text span keeps its leading and trailing whitespace, `-` and `~` outside the escape block (Scriban reads
  `{%{-` and `-}%}` as whitespace control), so `<%-`/`-%>` still trim around it. Whitespace control never reaches inside a raw
  block the template author wrote.
- Include names are relative to the pack folder (not to the including template).
- Scriban counts loop steps across nested loops of one outermost loop (and range sizes), so `templateLoopLimit` bounds the total
  iterations of an outermost loop, nested loops included.
- Pack-level failures (a script that does not load, MQ6013) fail every unit of the pack with the same diagnostic.
- `schema_diff` values cannot be passed to JavaScript helpers (W4's proxies do not cover the schema-diff records; MQ6016).
- JavaScript `filter` registrations are the planner's `where.script` (W6); templates use JavaScript helpers, which also work as
  Scriban pipe filters.
