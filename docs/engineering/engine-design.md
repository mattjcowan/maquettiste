# Engine design (phase 1)

**Status (2026-09-29): phase 1 is complete and gate 1 passes** (README.md, "What is built"; `bench/README.md` and `bench/baseline.json`: 100,050 files, cold total 6.7 s against 60 s, incremental 1.4 s against 2 s).

The contract that the phase 1 workers implement against: `Maquettiste.Engine`, `Maquettiste.Cli`, the JSON schemas, two example packs and the benchmark. `SPEC.md` is the authority; section numbers written as S12 refer to it. Where the spec leaves a choice, this file decides, and each such choice is listed in §19 with an id (D1, D2…). Type and member names here are binding: implement them verbatim. A worker who needs a contract change edits this file first, in the same commit as the code.

**Phase 1 scope (fixed).** Engine and CLI only. In: packages, entities, attributes, value objects, enums, custom scalar types, relations, databases, schemas, tables, views, sequences, mappings, diagrams (membership only), vocabularies (tags, category tree, stereotypes), template packs, `maquettiste.json`, extension schemas, JavaScript validation rules, SARIF. Out: processes (phase 3); operations, events, permissions, seeds, queries, projections, actors (phase 4); import commands; shared git packs and layering (phase 4); editor, functions, `Maquettiste.Build`. `migrate` is a stub. Example packs: `sql-ddl` and `csharp-dapper` only.

## 1. Repository and solution layout

```
maquettiste/
├── global.json                    # sdk 10.0.109, rollForward "disable"
├── Directory.Build.props          # shared build settings (below); scaffold-owned
├── Directory.Packages.props       # central versions: Scriban 7.5.0, Jint 4.16.4, JsonSchema.Net 9.4.0,
│                                  # Ulid 1.4.1, StaticSiteHost.Abstractions 0.2.0 (unused until phase 2), xunit.v3 3.2.2
├── maquettiste.slnx
├── src/Maquettiste.Engine/        # NuGet package Maquettiste.Engine
├── src/Maquettiste.Cli/           # dotnet tool Maquettiste.Cli, command `maquettiste`
├── schemas/v1/                    # JSON Schema per document kind, with x-order lists; embedded into the engine
├── packs/sql-ddl/  packs/csharp-dapper/
├── bench/Maquettiste.Bench/       # console app whose public generator + harness `maquettiste bench` also calls
├── bench/packs/fanout/            # bench-only pack that brings the synthetic run to 100,000+ files
├── bench/baseline.json            # committed reference timings for the 10% regression gate
├── tests/Maquettiste.Engine.Tests/   tests/Maquettiste.Cli.Tests/   tests/Maquettiste.Packs.Tests/
├── tests/Maquettiste.Testing/     # shared helpers: ModelBuilder, TempRepo, SequentialIdGenerator, Golden
├── tests/fixtures/                # fixture repos (models, packs, golden output), one subfolder per owning area
├── .github/workflows/             # ci.yml (build, test, determinism on ubuntu + macos), bench.yml
└── docs/engineering/
```

**Directory.Build.props**: `TargetFramework net10.0`, `LangVersion 14`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, `GenerateDocumentationFile true` (CS1591 is an error in `src/`, suppressed in `tests/` and `bench/`, decided on the project folder relative to the props file so a checkout under a folder named `tests` stays strict), `Deterministic true`, `ContinuousIntegrationBuild true` when `GITHUB_ACTIONS` is `true`, `ManagePackageVersionsCentrally true`, `RestorePackagesWithLockFile true`. `InvariantGlobalization true` is set by the CLI and bench apps only: on the engine library it has no runtime effect (the editor's host decides the culture) and it would switch off the culture analyzers. `.editorconfig` makes CA1304, CA1305, CA1307, CA1309, CA1310 and CA1311 errors in `src/**/*.cs`, and the test projects run with real culture data (`InvariantGlobalization false`) so culture tests mean something (D44). The engine csproj references only the four pinned packages (no ASP.NET, no StaticSiteHost, no logging package), embeds `../../schemas/v1/*.json` as `Maquettiste.Engine.Schemas.v1.<file>`, `Resolution/Dialects/*.json` and `Rendering/Resources/*` (W5's data files, such as the `sql_quote` reserved-word lists, as `Maquettiste.Engine.Rendering.Resources.<file>`), and grants `InternalsVisibleTo` to the three test projects, `Maquettiste.Testing`, `Maquettiste.Cli` and `Maquettiste.Bench` (the CLI and the bench construct `SchemaRegistry` and `CanonicalJson`, which are internal). The CLI csproj sets `PackAsTool`, `ToolCommandName maquettiste`, references the engine and `Maquettiste.Bench`, and embeds `packs/sql-ddl/**` and `packs/csharp-dapper/**` as starter packs for `init`. The bench csproj embeds the same two packs and `bench/packs/fanout/**` as `Maquettiste.Bench.Packs/<pack>/<path>`, so `maquettiste bench` from an installed tool can write them into its synthetic repo.

**Engine folders and namespaces** (all under `src/Maquettiste.Engine/`, namespace `Maquettiste.Engine.<Folder>`; the root folder holds the public facade in namespace `Maquettiste.Engine`):

| Folder | Holds | Owner (§18) |
| --- | --- | --- |
| *(root)* | `EngineOptions`, `EngineVersion`, `IIdGenerator` (Scaffold); `ModelStore` (W1); `GenerationService`, `JobQueue` (W6); `EngineVersion.Product` and `Build` (the release and its build, beside the contract `Value`) and `WorkspaceInfo` (the workspace name from `MAQUETTISTE_WORKSPACE` or `.git`), which the CLI, the MCP server and the editor API report | see left |
| `Model/` | element records, `ModelSnapshot`, index, `KindInfo`, `BuiltinTypes`, `EngineJson` | Scaffold |
| `Diagnostics/` | `Diagnostic`, `RuleCatalog`, `DiagnosticSeverity` | Scaffold |
| `Pipeline/` | stage interfaces and the DTOs they exchange | Scaffold |
| `Hashing/` | `ContentHash`, `HashBuilder` | Scaffold |
| `Json/` | canonical writer, `SchemaRegistry`, `ElementReader`, JSON position locator | W1 (first version by the scaffold) |
| `Loading/` | loader, index cache, file-name policy, batch parser | W1 |
| `Validation/` | validator, built-in rules, extension schemas, SARIF writer | W2 |
| `Resolution/` | resolver, resolved model, conventions, dialects | W3 |
| `Text/` | casing, inflector, identifier rules | W3 |
| `Scripting/` | Jint sandbox, pool, JS model proxies | W4 |
| `Rendering/` | Scriban renderer, template cache, tracking accessor, helpers, delimiter translator | W5 |
| `Planning/` | pack loader, unit planner, dependency hasher, unit state store, change detector | W6 |
| `Generation/` | pipeline orchestration, plan store, `EngineServices` composition | W6 |
| `Jobs/` | job records and queue internals | W6 |
| `Writing/` | path policy, writer, manifest, journal, run lock, unified diff | W7 |
| `PostProcessing/` | line endings, regions, formatter runner | W8 |
| `SchemaDiff/` | snapshot store, differ | W8 |

Public surface: the root facade, `Model`, `Diagnostics`, `Pipeline` (interfaces and DTOs), the resolved model types in `Resolution`, `SchemaDiff` result types and `Validation.SarifWriter`. Every implementation class is `internal sealed`.

## 2. The model

### 2.1 Rules for all model types

- Records with `init` properties, deserialized with `EngineJson.Options` (`JsonSerializerDefaults.Web`, `AllowTrailingCommas false`; unknown keys are already rejected by schema validation, which runs first). Enums serialize as kebab-case strings through `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` on the enum type and `[JsonStringEnumMemberName]` on members. Three union shapes carry attribute-applied converters: `TypeRef`, `Description`, `MaxCardinality`. No converter is registered on options, so any `JsonSerializerDefaults.Web` options serialize engine results (host-contracts req. 5).
- `required` in C# means required in the schema. A property initializer is the schema `default`, and the canonical writer omits a value equal to its default. `IReadOnlyList<T>` defaults to `[]`; maps default to empty.
- **Ids** are strings matching `^[0-7][0-9A-HJKMNP-TV-Z]{25}$` (uppercase Crockford ULID), created by `IIdGenerator.NewId()`. The ids in SPEC examples are illustrative and not valid ULIDs (D1). Every id is unique across the whole model, including sub-element ids (attributes, members, ends, columns, keys, categories, schemas).
- **References** hold ids. The scaffold marks every reference property with `[ElementRef(params ElementKind[] targets)]` (shown as `// → Kind` below); a reference to a sub-element names its index kinds instead, `[ElementRef(IndexKinds = ["attribute"])]`, and `[ElementRef]` with neither means any element. `ModelSnapshot` builds its reverse index from these attributes. Properties that hold physical keys (§7.3) rather than plain ids (`Column.Attribute`, `PrimaryKey.Columns`, unique, foreign-key and index column lists, `ForeignKey.ReferencesTable`, `ForeignKey.ReferencesColumns`, `TableIndex.Include`) carry `[ElementRef(Keyed = true)]` (shown as `// ⇢ key`): the indexer splits the value on `@` and `.` and records one reference per segment that is a valid id, so a designed table targeted by another table's foreign key, or an attribute an overlay column overrides, has referrers (D43); segments such as `id`, `position` and `discriminator` are not references. W2 still checks that keyed values resolve to real column keys (MQ4007, MQ4008). Stereotype lists hold stereotype keys (D2) and tags hold free-form labels (D41); the indexer records each `stereotypes[i]` entry as a reference to the stereotype's id, so a stereotype's referrers are known.
- **Names**: conceptual names (package, entity, value object, scalar type, enum, enum member, attribute, role, navigation) match `^[A-Za-z_][A-Za-z0-9_]*$`. Relation names, physical names, mapping, diagram and stereotype names are any non-empty string up to 256 characters without control characters. Stereotype keys match `^[a-z][a-z0-9]*(-[a-z0-9]+)*$`; tags (`tagLabel` in `common.json`) are free-form labels of 1 to 64 characters without whitespace or control characters (`PII`, `team:billing`, `v2.1`). Because `ElementBase.Name` defaults to `""`, every `name` schema declares `"default": ""`, so an overlay column or synthesized table written without a name stays without one (D35).
- **Built-in scalar keywords** (`BuiltinTypes.All`): `string text bool int16 int32 int64 decimal float double date time datetime datetimeoffset duration uuid ulid binary json`. `decimal(p,s)` is written as `"decimal"` with `precision` and `scale` facets (D3).

### 2.2 Document kinds and files

| `kind` | CLR type | Folder under `.maquettiste/` | Schema file |
| --- | --- | --- | --- |
| `package` | `Package` | `model/packages/` | `package.json` |
| `entity` | `Entity` | `model/entities/` | `entity.json` |
| `value-object` | `ValueObject` | `model/types/` | `value-object.json` |
| `scalar-type` | `ScalarType` | `model/types/` | `scalar-type.json` |
| `enum` | `EnumType` | `model/enums/` | `enum.json` |
| `relation` | `Relation` | `model/relations/` | `relation.json` |
| `database` | `Database` | `model/databases/<db>/database.json` | `database.json` |
| `table` / `view` / `sequence` | `Table` / `View` / `Sequence` | `model/databases/<db>/tables/` `views/` `sequences/` | `table.json` `view.json` `sequence.json` |
| `routine` / `database-type` / `sql-object` | `Routine` / `DatabaseType` / `SqlObject` | `model/databases/<db>/routines/` `types/` `objects/` | `routine.json` `database-type.json` `sql-object.json` |
| `query` | `Query` | `model/databases/<db>/queries/` | `query.json` |
| `mapping` | `Mapping` | `model/mappings/` | `mapping.json` |
| `diagram` | `Diagram` | `model/diagrams/` | `diagram.json` |
| `tag-vocabulary` | `TagVocabulary` | `model/vocabularies/tags.json` | `tag-vocabulary.json` |
| `category-tree` | `CategoryTree` | `model/vocabularies/categories.json` | `category-tree.json` |
| `stereotype` | `Stereotype` | `model/vocabularies/stereotypes/` | `stereotype.json` |
| `reference-type` | `ReferenceType` | `model/reference-types/` | `reference-type.json` |
| `seed` | `Seed` | `model/seeds/<target>/` | `seed.json` |
| `process` | `Process` | `model/processes/` | `process.json` |
| `actor` | `Actor` | `model/actors/` | `actor.json` |
| `scenario` | `Scenario` | `model/scenarios/<process>/` | `scenario.json` |
| (locale shard) | `LocaleShard` | `model/locales/<locale>/<domain path>.json`, `_root.json`, `_reference-data.json` | `locale.json` |
| (settings) | `ProjectSettings` | `maquettiste.json` | `maquettiste.json` |
| (pack) | `PackManifest` | `templates/<pack>/pack.json` | `pack.json` |
| (extension) | `ExtensionSchema` | `extensions/*.json` | `extension.json` |

Processes, actors and scenarios arrive in phase 3 (phase-3-design.md section 2). A scenario belongs to its process through an
owning reference, as a seed belongs to its target: its folder is named after the process's file stem and deleting the process
deletes its scenarios in the same save. Their sub-elements (`state`, `transition`, `event`, `guard`, `action`, `invoke`, `gate`,
`meaning`, `step`) are index kinds with model-wide ULIDs. Index rows gain `use`, `subject` and `stateCount` (process), `actorType`
(actor), `process` and `stepCount` (scenario), all from the row's own file; the index format constant (`ModelReads.IndexFormat`)
moves with every change of the row shape and is `maquettiste-index/e9` since then (e9: process diagram rows carry `process`, phase 3 round P4).
| (manifest, snapshot) | `ManifestFile`, `PhysicalSnapshot` | `manifest/<pack>.json`, `snapshots/<db>.json` | `manifest.json`, `snapshot.json` |

Shared `$defs` live in `common.json`; `batch.json` validates untrusted batches and `diagnostics.json` describes `validate --format json`. The loader walks `model/**/*.json` recursively and dispatches on `kind`; a file outside its conventional folder loads, with warning MQ1005. The file name is the kebab-case name (`<db>` is the database's kebab name); on a collision in one folder, `-<last 6 characters of the id, lowercase>` is appended. Stores write to the conventional folder, and a rename moves the file in the same save (S11). A seed's folder is named after its target's file stem (`model/seeds/unit-of-measure/`), suffixed `-<last 6 of the target id>` on a collision; seed names are unique per target (reference-types-seeds-localization.md section 2.1). Files under `model/locales/` are **locale shards**, not elements: the loader reads them with `locale.json` (MQ1002, MQ1003 as for element files) and dispatches on `kind: "locale-shard"`; they carry no id and are not in `ModelSnapshot.Documents` (the shard model, completeness and the `l:` keys land with the localization engine, RT section 5 step 4).

### 2.3 Element records

Listing shorthand: every property is `public … { get; init; }`, shown as `Type Name = default;`. `req` marks a C# `required` property. `{}` means an empty `ImmutableDictionary`. `// → Kind` marks an `[ElementRef]`.

```csharp
namespace Maquettiste.Engine.Model;
public enum ElementKind { Package, Entity, ValueObject, ScalarType, Enum, Relation, Database, Table, View, Sequence, Mapping, Diagram, TagVocabulary, CategoryTree, Stereotype }

public abstract record ElementBase {                        // S5 fields on every element
    req string Id; string Name = "";                        // Name: schema-required except synthesized Table and overlay Column
    string? DisplayName; string? PluralName;
    Description? Description;                               // "markdown" or { "file": "invoice.md" } (sidecar beside the file)
    IReadOnlyList<string> Tags = [];                        // tag keys
    string? Category;                                       // → Category
    IReadOnlyList<string> Stereotypes = [];                 // stereotype keys, in application order
    IReadOnlyDictionary<string, JsonElement> Properties = {};
    IReadOnlyDictionary<string, GenerationHints> Generation = {};   // key: pack name or "*"
    SourceInfo? Source; }
public abstract record Element : ElementBase {
    [JsonPropertyName("$schema")] string? SchemaPath;       // rewritten by the canonical writer; not "Schema": Table, View and Sequence have a Schema reference
    [JsonPropertyName("kind")] string KindName => KindInfo.Get(Kind).Name;   // written, ignored on read (the loader dispatches on it)
    [JsonIgnore] abstract ElementKind Kind { get; } }       // every override repeats [JsonIgnore]
public sealed record Description { string? Text; string? File; }           // exactly one; JSON string or { "file" }
public sealed record GenerationHints { bool Skip; string? Rename; IReadOnlyDictionary<string, JsonElement> Variables = {}; }
public sealed record SourceInfo { req string Format; string? Name; string? Location; string? Fingerprint; }   // format: dbml|sql|database|openapi
public sealed record TypeRef { string? Builtin; string? Ref; }             // JSON "uuid" or { "ref": id } → Enum|ValueObject|ScalarType
public enum MaxCardinality { One, Many }                                   // JSON 1 or "*"

public sealed record Package : Element { string? Parent; }                 // → Package
public sealed record Entity : Element {
    string? Package; bool Abstract; string? Base;           // → Package (null = root), → Entity
    EntityKey? Key;                                         // required unless Abstract or Base (MQ3005)
    IReadOnlyList<AlternateKey> AlternateKeys = []; IReadOnlyList<ModelAttribute> Attributes = [];
    IReadOnlyList<EntityBinding> Bindings = []; }          // one per database (erratum E43, §7 "Bindings and materialize")
public sealed record EntityKey { req IReadOnlyList<string> Attributes; IdentityStrategy Strategy = Application; }   // → ModelAttribute; sequence per database (D37)
public enum IdentityStrategy { DatabaseIdentity, Sequence, UuidV7, Ulid, Application }   // database-identity|sequence|uuid-v7|ulid|application
public sealed record AlternateKey { req string Id; req string Name; req IReadOnlyList<string> Attributes; }
public sealed record ModelAttribute : ElementBase {         // S6 attribute table
    req TypeRef Type; bool Required;
    JsonElement? Default; string? DefaultExpression;        // literal; or named expression now|today|new-uuid|new-ulid|pack-defined (D4)
    int? Length; int? Precision; int? Scale; bool Collection; bool Unique; bool Indexed; bool ReadOnly; bool Immutable;
    DerivedSpec? Derived; Sensitivity? Sensitive; AttributeValidation? Validation;
    int? Order; }                                           // stable position: files and the resolved order stable-sort by it, missing = 0 (§3, §7.2; D36)
public sealed record DerivedSpec { req string Expression; bool Stored; }
public enum Sensitivity { Pii, Secret }
public sealed record AttributeValidation { JsonElement? Min; JsonElement? Max; string? Pattern;
    IReadOnlyList<JsonElement> AllowedValues = []; IReadOnlyList<string> Rules = []; }   // Rules: JavaScript rule ids
public sealed record ValueObject : Element { string? Package; IReadOnlyList<ModelAttribute> Attributes = []; }
public sealed record ScalarType : Element { string? Package; req string Base; int? Length; int? Precision; int? Scale; AttributeValidation? Validation;
    IReadOnlyDictionary<string, string> NativeTypes; }                    // dialect name → native pattern ({length}, {precision}, {scale}); 2026-10-01
public sealed record EnumType : Element { string? Package; bool Flags; IReadOnlyList<EnumMember> Members = []; }
public sealed record EnumMember : ElementBase { long? Value; string? Code; }            // integer and/or string code

public sealed record Relation : Element {
    string? Package; string? InverseName; RelationKind RelationKind = Association;
    req IReadOnlyList<RelationEnd> Ends;                    // 2, or 3+ with NAry
    IReadOnlyList<ModelAttribute> Attributes = []; bool AllowDuplicates; }
public enum RelationKind { Association, Aggregation, Composition, NAry }            // association|aggregation|composition|n-ary
public sealed record RelationEnd {
    req string Id; req string Entity; req string Role;      // → Entity
    string Navigation = "";                                 // property generated on the opposite entity; "" (default, omitted) = not navigable
    int Min = 0; MaxCardinality Max = Many;                 // min 0|1
    ReferentialIntent OnDelete = None;                      // what happens to the other ends' rows when this end's row is deleted
    bool Ordered; string? Description; }                    // Ordered: this end's collection keeps a position (D5)
public enum ReferentialIntent { None, Cascade, Restrict, SetNull }

public sealed record Database : Element {
    req Dialect Dialect; string? Version;
    string? DefaultSchema;                                  // null: pg public, sqlserver dbo, others none
    IReadOnlyList<DbSchema> Schemas = []; Quoting Quoting = Reserved;
    int? MaxIdentifierLength;                               // null: pg 63, sqlserver 128, mysql 64, oracle 128, sqlite none
    ConventionMapping? ByConvention;                        // all | packages | none; null (older files): Packages empty = all (D46)
    IReadOnlyList<string> Packages = []; }                  // → Package: entities there (and below) map here by convention (D6, D46)
public sealed record DbSchema : ElementBase;
public enum Dialect { PostgreSql, SqlServer, MySql, Sqlite, Oracle }                 // postgresql|sqlserver|mysql|sqlite|oracle
public enum Quoting { Always, Reserved, Never }
public sealed record Table : Element {
    req string Database; string? Schema;                    // → Database, → DbSchema (null = default schema)
    TableOrigin Origin = Designed;                          // synthesized: exactly one of Entity, Relation, Enum; none otherwise (D9)
    string? Entity; string? Attribute; string? Relation; string? Enum;  // → Entity, → ModelAttribute (child table of Entity), → Relation, → EnumType
    IReadOnlyList<Column> Columns = []; PrimaryKey? PrimaryKey; IReadOnlyList<UniqueConstraint> Uniques = [];
    IReadOnlyList<ForeignKey> ForeignKeys = []; IReadOnlyList<CheckConstraint> Checks = []; IReadOnlyList<TableIndex> Indexes = [];
    string? Comment; }
public enum TableOrigin { Synthesized, Designed, Imported }
public sealed record Column : ElementBase {
    string? Attribute;                                      // ⇢ key; overlay: the synthesized column key it overrides (§7.3); null = extra column
    string? Type; int? Length; int? Precision; int? Scale; string? NativeType;   // Type: built-in keyword, required on designed columns
    bool? Nullable;                                         // designed: null or true (canonical: omitted); overlay: null keeps (x-default-unless, §3)
    JsonElement? Default; IReadOnlyDictionary<string, string> DefaultSql = {};  // dialect or "*" → SQL
    ColumnGeneration? Generated; string? Sequence;          // → Sequence
    string? Computed; bool ComputedStored; string? Collation; string? Comment; }
public enum ColumnGeneration { Identity, Sequence }
public sealed record PrimaryKey { string? Name; req IReadOnlyList<string> Columns; bool? Clustered; }   // ⇢ key: column ids or keys
public sealed record UniqueConstraint { req string Id; string? Name; req IReadOnlyList<string> Columns; }   // ⇢ key
public sealed record ForeignKey { req string Id; string? Name; req IReadOnlyList<string> Columns;
    req string ReferencesTable;                             // ⇢ key: table id or synthesized table key (§7.3); Columns ⇢ key too
    IReadOnlyList<string> ReferencesColumns = [];           // ⇢ key; empty = referenced primary key
    ReferentialAction OnDelete = NoAction; ReferentialAction OnUpdate = NoAction; }
public enum ReferentialAction { NoAction, Restrict, Cascade, SetNull, SetDefault }
public sealed record CheckConstraint { req string Id; string? Name; req IReadOnlyDictionary<string, string> Expression; }   // dialect or "*"
public sealed record TableIndex { req string Id; string? Name; req IReadOnlyList<IndexColumn> Columns; IReadOnlyList<string> Include = [];   // Include ⇢ key
    string? Where; bool Unique; IndexMethod Method = Default; }
public sealed record IndexColumn { req string Column; bool Descending; }   // Column ⇢ key
public enum IndexMethod { Default, Btree, Hash, Gin, Gist, Clustered }
public sealed record View : Element { req string Database; string? Schema; req IReadOnlyDictionary<string, string> Body;   // dialect or "*" → SELECT
    IReadOnlyList<ViewColumn> Columns = []; string? Comment; }
public sealed record ViewColumn { req string Name; string? Type; bool Nullable = true; }
public sealed record Sequence : Element { req string Database; string? Schema; string Type = "int64"; long Start = 1; long Increment = 1;
    long? Min; long? Max; bool Cycle; int? Cache; }

public sealed record Mapping : Element {                    // one per (database, entity|relation); MQ4004 on duplicates
    req string Database; string? Entity; string? Relation;  // exactly one of Entity, Relation
    string? Table;                                          // entity: bind to this designed/imported table instead of synthesizing
    bool Ignore;                                            // not stored in this database
    InheritanceStrategy? Inheritance; JsonElement? DiscriminatorValue;   // strategy on the hierarchy root only; value default = entity name
    IReadOnlyList<AttributeMapping> Attributes = [];
    RelationShape? Shape; string? ForeignKeyEnd;            // relation: override the S7 default; → RelationEnd holding the FK (1:1 ties)
    string? ForeignKey;                                     // → key: existing FK in the dependent end's bound (designed/imported) table (§7.4)
    string? JunctionTable;                                  // → Table (designed junction)
    IReadOnlyList<RelationEndMapping> Ends = [];            // designed junction: which of its FKs realizes each end
    string? PromotedName; }                                 // promoted entity name, default pascal(entity1+entity2)
public sealed record RelationEndMapping { req string End; req string ForeignKey; }   // → RelationEnd, → key (a ForeignKey of JunctionTable)
public sealed record AttributeMapping { req string Attribute; string? Column; StorageKind? Storage; string? Prefix; bool Ignore; }  // Column: id in the bound table
public enum InheritanceStrategy { Tph, Tpt, Tpc }
public enum StorageKind { Int, String, Embedded, Table, Json }   // the enum "lookup" option is retired: refused at load as MQ7012
public enum RelationShape { ForeignKey, Junction, Promoted }                       // foreign-key|junction|promoted

public sealed record Diagram : Element { string? Package; IReadOnlyList<DiagramMember> Members = []; Viewport? Viewport; }   // never visible to templates
public sealed record DiagramMember { req string Element; double X; double Y; double? Width; double? Height; bool Collapsed; }
public sealed record Viewport { double X; double Y; double Zoom = 1; }
public sealed record TagVocabulary : Element { bool Strict; IReadOnlyList<TagDefinition> Definitions = []; }   // Strict: undeclared tags are errors; not "Tags", which ElementBase already has
public sealed record TagDefinition { req string Key; string? Description; string? Color; }   // Key: the tag label (D41)
public sealed record CategoryTree : Element { IReadOnlyList<Category> Categories = []; }             // flat list, tree through Parent
public sealed record Category : ElementBase { string? Parent; int? Order; }
public sealed record Stereotype : Element {                 // Name is a renamable label (D2)
    req string Key;                                         // immutable kebab key used in every Stereotypes list; a save changing it is MQ3020
    IReadOnlyList<string> AppliesTo = [];                   // kind names (§2.2) plus attribute, enum-member, column; empty = all
    IReadOnlyList<ModelAttribute> Attributes = [];          // virtual attributes for entities, value objects and relations
    IReadOnlyDictionary<string, JsonElement> DefaultProperties = {}; string? Icon; string? Color; }

// Reference types, seeds and localization (reference-types-seeds-localization.md, Appendix A). TypeRef.Ref may also name a ReferenceType.
public sealed record ReferenceType : Element {             // no Package (RS3)
    req ReferenceCode Code; req ReferenceLabel Label;
    IReadOnlyList<ModelAttribute> Attributes = [];          // x-sort "order"; names exclude code, label, description (MQ7010)
    IReadOnlyDictionary<string, StorageChoice> Storage = {}; }   // key: database id or "*"
public sealed record ReferenceCode { req string Id; string Type = "string"; int? Length; string? Pattern; string? DisplayName; Description? Description; }   // Type: string|int16|int32|int64
public sealed record ReferenceLabel { req string Id; int? Length; string? DisplayName; Description? Description; }
public sealed record StorageChoice { string? Strategy; IReadOnlyDictionary<string, JsonElement> Options = {}; }   // Strategy null = template-defined
public sealed record Seed : Element {
    req string Target;                                      // → Entity|Relation|ReferenceType; the seed belongs to it
    req IReadOnlyList<string> Columns;                      // "code"|"label"|"description", or → ModelAttribute|RelationEnd (keyed reference)
    IReadOnlyList<SeedRow> Rows = []; }                     // x-layout "row-per-line"
public sealed record SeedRow { req string Id; IReadOnlyList<JsonElement> Values = []; }   // x-trim "trailing-nulls"
public sealed record LocaleShard { [JsonPropertyName("$schema")] string? SchemaPath; req string Kind; req string Locale;   // Kind "locale-shard"; not an element
    req string Scope; IReadOnlyDictionary<string, TranslationEntry> Entries = {}; }   // Scope: package id, "root" or "reference-data"
public sealed record TranslationEntry { string? DisplayName; string? PluralName; string? Label; Description? Description;
    IReadOnlyDictionary<string, string> Src = {}; }         // field → 8-hex SHA-256 prefix of the default text
// RelationEnd gains string? DisplayName; string? PluralName. Index sub-element kinds gain "reference-field" (code, label) and "row" (SeedRow).
```

The S6 example's `lifecycle` field names a process: phase 1 schemas rejected it (D7), and since phase 3 `entity.json` accepts it (erratum E3 retired). The verbatim S6 and S7 examples fail only for the three adaptations listed in `docs/engineering/spec-errata.md` (E1 to E3); a test pins that.

**Element serialization.** `Element` carries `[JsonDerivedType]` for all 17 concrete records without discriminators, so an `Element`-typed property (`ElementDocument.Element` in `SaveResult.Current` and `GetElementAsync`) serializes as its runtime record under any `JsonSerializerDefaults.Web` options; reading always goes through the kind's concrete type.

### 2.4 Project settings (`maquettiste.json`)

```csharp
public sealed record ProjectSettings {
    [JsonPropertyName("$schema")] string? SchemaPath; req int FormatVersion; string? Name;   // FormatVersion: 1
    IReadOnlyDictionary<string, string> Properties = {};   // the project's own key → text (2026-10-07), templates read project.properties
    OutputSettings Outputs = new(); HandEditPolicy HandEdits = Fail; IReadOnlyList<FormatterSettings> Formatters = [];
    Conventions Conventions = new(); IReadOnlyDictionary<string, Conventions> Databases = {};  // key: database name; sparse overrides
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TypeMaps = {};           // dialect → keyword → native pattern
    InflectionSettings Inflection = new(); IReadOnlyDictionary<string, PackSettings> Packs = {};   // key: pack name
    ValidationSettings Validation = new(); SandboxLimits Limits = new();
    LocalizationSettings? Localization; ReferenceDataSettings ReferenceData = new(); }   // written after Inflection (x-order)
public sealed record LocalizationSettings { req string DefaultLocale; IReadOnlyList<string> Locales = [];
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fallbacks = {}; IReadOnlyList<string> Require = []; }
public sealed record ReferenceDataSettings { IReadOnlyDictionary<string, StrategyDeclaration> Strategies = {}; string GroupBy = "category"; }   // category|tag:<prefix>|property:<name>
public sealed record StrategyDeclaration { string? Description; CollectionSupport Collections = CollectionSupport.None; JsonElement? Options; IReadOnlyList<string> Required = []; }
// CollectionSupport: a bool or a map dialect|"*" → bool (converter like MaxCardinality); For(dialect) evaluates it. The engine knows no strategy name (P2).
public sealed record OutputSettings { IReadOnlyList<OutputRoot> Allow = []; IReadOnlyList<string> Deny = []; }   // S4 allow roots and deny rules (D42)
public sealed record OutputRoot { req string Path; }                                     // repo-relative folder, or the file it names (E42; `commit` removed, MQ1010)
public enum HandEditPolicy { Fail, Overwrite, Skip }
public sealed record FormatterSettings { req string Name; req IReadOnlyList<string> Extensions; req string Command;
    IReadOnlyList<string> Args = [];                        // "{path}" is replaced by the repo-relative path
    req string Version; IReadOnlyList<string> VersionArgs = ["--version"]; int TimeoutSeconds = 30; }
public sealed record InflectionSettings { IReadOnlyDictionary<string, string> Plurals = {}; IReadOnlyList<string> Uncountable = []; }
public sealed record PackSettings { bool Enabled = true; string Output = "";            // Output: folder prefixed to the pack's paths
    IReadOnlyDictionary<string, JsonElement> Parameters = {}; HandEditPolicy? HandEdits; }
public sealed record ValidationSettings { IReadOnlyDictionary<string, string> Rules = {}; }   // rule id → error|warning|info|off
public sealed record SandboxLimits { int ScriptTimeoutMs = 2000; long ScriptStatements = 5_000_000; int ScriptRecursion = 256;
    long ScriptMemoryBytes = 67_108_864; int TemplateLoopLimit = 1_000_000; int TemplateRecursionLimit = 64; }
public sealed record Conventions {                          // all nullable; effective = built-in default ← project ← database
    CaseStyle? TableCase /*snake*/; bool? PluralTables /*true*/; CaseStyle? ColumnCase /*snake*/;
    string? TableName /*"{entity}"*/; string? KeyColumn /*"{attribute}"*/; string? ForeignKeyColumn /*"{role}_{key}"*/;
    string? JunctionTable /*"{entity1}_{entity2}"*/; string? ChildTable /*"{entity}_{attribute}"*/; string? ValueObjectColumn /*"{attribute}_{member}"*/;
    string? OrderColumn /*"position"*/; string? DiscriminatorColumn /*"discriminator"*/;
    string? PrimaryKeyName /*"pk_{table}"*/; string? ForeignKeyName /*"fk_{table}_{columns}"*/; string? UniqueName /*"uq_{table}_{columns}"*/;
    string? IndexName /*"ix_{table}_{columns}"*/; string? CheckName /*"ck_{table}_{name}"*/; string? SequenceName /*"{table}_seq"*/;
    int? DefaultStringLength /*255*/; int? DecimalPrecision /*18*/; int? DecimalScale /*2*/; int? DatetimePrecision /*6*/;
    StorageKind? EnumStorage /*int*/; StorageKind? ValueObjectStorage /*embedded*/; StorageKind? ValueObjectCollectionStorage /*table*/;
    RelationShape? RelationsWithAttributes /*junction (D8)*/; InheritanceStrategy? Inheritance /*tph*/;
    StorageChoice? ReferenceStorage /*none: template-defined*/; CommentSource? Comments /*descriptions*/; }
public enum CaseStyle { Snake, Pascal, Camel, Kebab, UpperSnake, Preserve }
public enum CommentSource { None, Descriptions }   // "none" | "descriptions"
```

Name patterns take the tokens `{entity} {attribute} {member} {role} {key} {table} {columns} {name} {relation} {entity1} {entity2}`. `{columns}` joins column names with `_`; `{entity1}` and `{entity2}` follow `Ends` order. The result is split into words (§9) and re-cased with `TableCase` for tables and `ColumnCase` for everything else; `PluralTables` pluralizes the last word of `{entity}` in table names. A name over the identifier limit is an error (MQ4001), never truncated.

### 2.5 Pack manifest and extension schema

```csharp
public sealed record PackManifest {
    [JsonPropertyName("$schema")] string? SchemaPath; req string Name; req string Version /*semver*/; req string Engine /*">=1.0 <2.0"*/;
    string? Description; IReadOnlyDictionary<string, JsonElement> Parameters = {};           // name → default value
    IReadOnlyList<string> Scripts = [];                     // empty = every *.js in the pack, ordinal path order
    bool UsesSchemaDiff; req IReadOnlyList<PackUnit> Units; }
public sealed record PackUnit {
    req string Id;                                          // ^[a-z][a-z0-9-]*$, unique in the pack
    req string Template;                                    // pack-relative path
    req string For;                                         // "model" | "each package|entity|relation|enum|value object|table|view|sequence|reference type|seed|locale|process|actor|scenario" | "select <name>"
    UnitWhere? Where;
    string? Output;                                         // Scriban expression → path under PackSettings.Output; null = file blocks only
    OutputMode Mode = Overwrite; string? Formatter;         // formatter name, "none", or null = by extension
    string BlockComment = "#"; bool CreateFile;            // Mode = Block only (E42, §12.3b): delimiter comment, create a missing target
    Delimiters? Delimiters; PairCompanion? Companion;       // Companion required when Mode = Pair
    IReadOnlyList<string> Transforms = []; }                // pre-render transforms; results merged into `data`
public sealed record UnitWhere {                            // lists match any value; every set filter must match
    IReadOnlyList<string> Tags = []; IReadOnlyList<string> NotTags = []; IReadOnlyList<string> Stereotypes = []; IReadOnlyList<string> NotStereotypes = [];
    IReadOnlyList<string> Categories = [];                  // id or name; descendants match
    IReadOnlyList<string> Packages = []; IReadOnlyList<string> NotPackages = [];   // id or qualified name; sub-packages match
    string? Database;                                       // name: tables in it; entities/relations mapped (not ignored) in it
    bool? Abstract; string? Script; }                       // Script: JavaScript filter name
public enum OutputMode { Overwrite, Once, Regions, Pair, Block }   // Block: as built 2026-10-02 (spec-errata E42, §12.3b)
public sealed record Delimiters { req string Open; req string Close; }
public sealed record PairCompanion { req string Template; req string Output; }
public sealed record ExtensionSchema {                      // extensions/*.json; not an element, no id
    [JsonPropertyName("$schema")] string? SchemaPath; req string Name; string? Description; req ExtensionTarget AppliesTo;
    req JsonElement Properties; IReadOnlyList<string> Required = []; }   // Properties: a JSON Schema "properties" object
public sealed record ExtensionTarget { IReadOnlyList<string> Kinds = []; IReadOnlyList<string> Stereotypes = []; }
```

An extension applies to an element when the element's kind is listed (or `Kinds` is empty) and, if `Stereotypes` is non-empty, the element carries one of them.

**Manifest and snapshot files** (scaffold-owned records; W7 and W8 write them):

```csharp
public sealed record ManifestFile { [JsonPropertyName("$schema")] string? SchemaPath; req string Pack;
    IReadOnlyList<IReadOnlyList<string>> Files = []; }        // [path, hash, unit] triples, ordinal by path (§12.2)
public sealed record PhysicalSnapshot { [JsonPropertyName("$schema")] string? SchemaPath; req string Database /*id*/; req string Name;
    req Dialect Dialect; int Revision; IReadOnlyList<SnapshotTable> Tables = []; IReadOnlyList<SnapshotView> Views = [];
    IReadOnlyList<SnapshotSequence> Sequences = []; }
public sealed record SnapshotTable { req string Key; req string Name; string? Schema; IReadOnlyList<SnapshotColumn> Columns = [];
    SnapshotConstraint? PrimaryKey; IReadOnlyList<SnapshotConstraint> Uniques = []; IReadOnlyList<SnapshotForeignKey> ForeignKeys = [];
    IReadOnlyList<SnapshotCheck> Checks = []; IReadOnlyList<SnapshotIndex> Indexes = []; string? Comment; }
public sealed record SnapshotColumn { req string Key; req string Name; req string Type; int? Length; int? Precision; int? Scale;
    string? NativeType; bool Nullable; JsonElement? Default; string? DefaultSql; bool Identity; string? Sequence /*sequence key*/;
    string? Computed; bool ComputedStored; string? Collation; string? Comment; }
public sealed record SnapshotConstraint { req string Key; req string Name; req IReadOnlyList<string> Columns; bool? Clustered; }   // PK and uniques
public sealed record SnapshotForeignKey { req string Key; req string Name; req IReadOnlyList<string> Columns; req string ReferencedTable;
    req IReadOnlyList<string> ReferencedColumns; ReferentialAction OnDelete = NoAction; ReferentialAction OnUpdate = NoAction; }
public sealed record SnapshotCheck { req string Key; req string Name; req string Expression; }
public sealed record SnapshotIndex { req string Key; req string Name; req IReadOnlyList<IndexColumn> Columns; IReadOnlyList<string> Include = [];
    string? Where; bool Unique; IndexMethod Method = Default; }
public sealed record SnapshotView { req string Key; req string Name; string? Schema; req string Body; }
public sealed record SnapshotSequence { req string Key; req string Name; string? Schema; string Type = "int64"; long Start = 1; long Increment = 1;
    long? Min; long? Max; bool Cycle; int? Cache; }
```

The canonical writer lays a manifest's triples out one value per line; W7 writes the one-entry-per-line form of §12.2 itself.

### 2.6 Snapshot and index

```csharp
public sealed record ElementDocument(Element Element, string Path, string Hash, string DependencyHash, JsonElement Json, string? SidecarText);
// Path ".maquettiste/model/entities/invoice.json"; Hash = SHA-256 of the file bytes (the ETag); DependencyHash = H(Hash, sidecar hash)
public sealed record IndexEntry(string Id, string OwnerId, string Kind, string JsonPointer);   // Kind: element kind or attribute|enum-member|end|column|category|schema|key
public sealed record ReferenceInfo(string FromElementId, string FromId, string JsonPointer, string Field, string ToId);
public sealed record ElementSummary(string Id, string Kind, string Name, string? Package, IReadOnlyList<string> Tags, string Hash, string Path,
    string? Category, IReadOnlyList<string> Stereotypes);  // E4 (§15): category-tree node id and stereotype keys, from the in-memory element
public sealed record ExtensionDocument(ExtensionSchema Schema, string Path, string Hash);
public sealed class ModelSnapshot {                        // immutable, thread-safe
    public static ModelSnapshot Create(IEnumerable<ElementDocument> documents, ProjectSettings settings, string settingsHash,
        IReadOnlyList<ExtensionDocument> extensions, IReadOnlyList<ScriptSource> ruleScripts,
        long version, IReadOnlyList<Diagnostic>? loadDiagnostics = null);   // pure: id, name and reverse indexes
    public long Version { get; } public ProjectSettings Settings { get; } public string SettingsHash { get; }
    public IReadOnlyList<ElementDocument> Documents { get; }                 // ordinal by Path
    public IReadOnlyList<Diagnostic> LoadDiagnostics { get; }
    public IReadOnlyList<ExtensionDocument> Extensions { get; } public IReadOnlyList<ScriptSource> RuleScripts { get; }   // ordinal by Path
    public bool TryGetEntry(string id, [MaybeNullWhen(false)] out IndexEntry entry);
    public ElementDocument? GetDocument(string id);                          // a sub-element id returns its owner
    public string ReferrersHash(string id);                                  // current hash of r:<id> (§11)
    public T? Get<T>(string id) where T : Element; public IReadOnlyList<T> All<T>() where T : Element;   // All: ordinal by (Name, Id)
    public IReadOnlyList<ReferenceInfo> ReferencesTo(string id); public IReadOnlyList<ReferenceInfo> ReferencesFrom(string elementId);
    public Stereotype? GetStereotype(string key); public TagVocabulary? Tags { get; } public CategoryTree? Categories { get; }   // key: Stereotype.Key
    public string KindSetHash(ElementKind kind);                             // H(sorted ids of that kind)
    public IReadOnlyList<ElementSummary> Summaries(); }
```

The tag vocabulary and the category tree are one per scope (explorer-redesign.md section 1.11): a vocabulary without `package` is global, one with `package` belongs to that domain and the domains nested in it. `Create` keeps the ordinally first file of each kind per scope and adds error MQ1009, naming both paths, to `LoadDiagnostics` for any other; `Tags` and `Categories` are the global ones, `TagVocabularies`, `CategoryTrees`, `TagVocabularyOf(scope)`, `CategoryTreeOf(scope)` and `VocabularyChain(package)` (the package, each enclosing package nearest first, then global) serve the domain chain. An element's tags and categories resolve along its chain: declared nowhere on it but in another domain is MQ2008, declared nowhere is MQ2006; a domain vocabulary redeclaring a key or category name of the global vocabulary or an enclosing domain's is MQ3021. A domain's files live in `model/vocabularies/` as `<name>-tags.json` and `<name>-categories.json`.
*Table seeds* (2026-10-07; `tmp/scope-table-seeds.md` the proposal). A seed's `target` may be a table file (designed or imported, never
a synthesized overlay): its `columns` are column ids and its cells column values; `key` names a unique constraint as its row key
(default the primary key). Every seed takes `environments` (empty: every environment), `apply` (`once`, the default, inserts the rows
whose key is missing; `converge` also updates changed rows) and `delete` (with converge: remove the rows the seed does not hold).
`rowsFrom: { file }` keeps the rows in a CSV sidecar (`SeedCsv`: `@id` and the column ids as the header, RFC 4180, a quoted cell
is text, an unquoted one a JSON number, boolean, array or object when it parses as one): the loader reads them into the seed and
its document JSON, the change planner writes them back on every save (and deletes the file when `rowsFrom` goes), so the
editor, the API and batches see rows as for any seed; the sidecar is watched, hashed and moved as description sidecars are. A
CSV-backed seed is exempt from MQ7104's size limit. Resolution: `RTable.Seeds`; `RDatabase.SeedTables` (built on first read):
per table in foreign key order (a cycle keeps name order, `InCycle`), the rows the database receives in database terms
(`RDataRow`: `values` by column name, `key`, `source` `table|entity|relation`, the seed's settings): table seeds, entity seeds
through the binding to the database (field map, constants, write table; a read-only binding none) or through the mapping of a
database that projects the entity, relation seeds into junction tables; `RDatabase.SeedHash` hashes them canonically. Rules:
MQ7107 (a table file and its columns), MQ7108 (no identity, generated or computed column), MQ7109 (a cell fits its column), MQ7111
(a foreign key names a row of the referenced table's seeds, when it has seeds), MQ7112 (one row per key, across a table's seeds of
overlapping environments), MQ7113 (warning: a table seeded by its own seeds and a bound entity's), MQ7114 (a row key), MQ7116 (the
rows file). CHECK constraints are left to the database.

*Tags across the model* (2026-10-07, `ModelStore.Tags.cs`; the owner: a vocabulary created on a model that already used tags turned
every use into a note). A use of a tag belongs to the nearest vocabulary on the element's chain that declares it, or to none; a
scope (global, or a domain) governs its own declared tags' uses inside it and the undeclared uses inside it, so a domain that
declares a key keeps its uses when the global one goes. `GetTagUsageAsync(package)` (`GET /api/model/tags/usage`, MCP `tag_usage`)
lists every tag the scope's vocabulary declares and every undeclared tag used inside it, with its governed uses (the element's own
`tags` and every sub-element's, free-form maps such as `properties` not entered), the elements holding them and five of their
names. The batch operation `retag { tags, name?, package?, expectedHashes? }` removes the tags, or renames the one to `name`
(merging with the new key on a list that has both; a definition of the new key already there keeps its entry and the old one's
goes), in the scope's vocabulary and every governed use, as updates of the batch; refused (MQ1002) for no tags, a rename of
several or to a key that is not a `tagLabel`, an unknown domain, and nothing to change. Whole-model validation reports MQ2006 once
per tag and severity, on its first use with the count of uses and elements (`ModelValidator.CollapseUndeclaredTags`); a scoped
validation (a save) and a strict vocabulary's errors stay one per use.

## 3. Canonical JSON and schemas

**Canonical form** (S11): UTF-8 without BOM, LF, two-space indent, one space after `:`, trailing newline, no trailing whitespace; strings escaped minimally (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, then `<`, `>`, `&` left literal); numbers as written by `Utf8JsonWriter`. Object keys follow the schema's `x-order` list for that object; keys of free-form maps (`properties`, `generation`, `variables`, `defaultSql`, `body`, `expression`, `parameters`, `typeMaps`, `plurals`, `rules`, `databases`, `packs`, `storage`, `options`, `strategies`, `fallbacks`, `entries`) sort ordinal. Arrays keep their order, except an array whose schema declares `"x-sort": "<key>"`, which is stable-sorted by that integer item key with a missing value counting as 0: the `attributes` arrays of entities, value objects, relations and stereotypes and the category tree's `categories` carry `"x-sort": "order"` (S6 "`order`: stable position used for … serialization", S11 "`order` for attributes"; D36). A property equal to its schema `default` (deep JSON equality; `[]` and `{}` count) is omitted, unless the property schema names a sibling in `"x-default-unless"` and that sibling is present: a column's `nullable` has `"default": true, "x-default-unless": "attribute"`, so a designed column's `nullable: true` is dropped while an overlay column's explicit `true` is kept. An array whose schema declares `"x-trim": "trailing-nulls"` (a seed row's `values`) drops its trailing `null` items. An array whose schema declares `"x-layout": "row-per-line"` (a seed's `rows`) writes each item on its own line, indented as an array item, in exactly one compact form: `{ "id": "<ulid>", "values": [<cell>, <cell>] }`, one space inside braces, none inside brackets, `", "` between items and `": "` after keys, nested objects written the same way, and numbers as the shortest plain decimal text of their value (no exponent, no `+`, no leading zeros, no trailing fractional zeros; `1e3` and `1000.0` give `1000`, `0.360` gives `0.36`), computed on the decimal text, never through `double` (reference-types-seeds-localization.md section 2.1, RS7). `CanonicalCheck` leaves arrays with either keyword to the node-based writer, so `IsCanonical` (MQ1003) and the writer read the same schema keywords. `$schema` is always first and is the relative path from the file's folder to `.maquettiste/.schema/v1/<schema file>` (e.g. `../../.schema/v1/entity.json`, `../../../../.schema/v1/table.json`, `.schema/v1/maquettiste.json`); `kind` is second.

**One source for key order.** `schemas/v1/*.json` (draft 2020-12) are hand-written and are the only place key order and defaults live. Every object schema that has `properties` has an `"x-order": [...]` listing exactly those keys, except condition schemas under `if`, `not`, `contains` and `propertyNames`, which describe no document layout. No schema has `$id`; `$ref`s are relative (`common.json#/$defs/attribute`). The engine embeds the files; `SchemaRegistry` loads each with `JsonSchema.FromText(text, options, new Uri("https://maquettiste.invalid/schemas/v1/<file>"))` so references resolve offline, and builds an `ObjectLayout` tree per document kind (for each object location: ordered keys, default per key, child layouts, map-or-object). `CanonicalJsonWriter` walks a `JsonNode` against that layout. `SchemaConsistencyTests` (W1) fail the build if any `x-order` list differs from its `properties` keys, or if a model record property has no schema property (or the reverse). `ISchemaRegistry.Evaluate` reports leaf failures only (D34) and drops failures inside subschemas whose failure does not fail the document: an `if` condition, the operand of `not`, and branches of `oneOf`, `anyOf` or `contains` whose keyword passed. `maquettiste init`, the editor's start and the start of `maquettiste mcp` bring `.maquettiste/.schema/v1/` in line with the embedded files (`SchemaFolder.RefreshAsync`: files the engine no longer ships deleted, missing and stale files written, every path through the write guard as `WriteTarget.Model`; the editor's file watcher ignores the folder, so the refresh raises no change, and a folder that cannot be written is logged and the start goes on); `validate` and `generate` (every mode) warn MQ1008 when the folder differs from the embedded copies (byte comparison: missing, stale or extra `*.json` files), never refreshing it. The finding is on `maquettiste.json` without a pointer, names up to five differing files (then "and N more") and says that `maquettiste init` or the editor's start refreshes them; `validation.rules` can change its severity. `init` does not report it.

```csharp
namespace Maquettiste.Engine.Json;
public interface ISchemaRegistry
{
    IReadOnlyList<string> FileNames { get; }                                 // "entity.json", …
    ReadOnlyMemory<byte> GetFileBytes(string fileName);                      // embedded bytes, for init
    IReadOnlyList<Diagnostic> Evaluate(string fileName, JsonElement document, string path);   // MQ1002 per failed keyword
    ObjectLayout GetLayout(string fileName);
}
public interface ICanonicalJson
{
    byte[] Write(JsonNode document, string fileName, string documentPath);  // canonical bytes, $schema computed from documentPath
    byte[] Serialize<T>(T value, string fileName, string documentPath);     // SerializeToNode + Write
    bool IsCanonical(ReadOnlySpan<byte> bytes, string fileName, string documentPath);
}
```

## 4. Pipeline

### 4.1 Shared types (`Maquettiste.Engine.Pipeline`, scaffold-owned)

```csharp
public enum PipelineStage { Load = 1, Validate, Resolve, Plan, Skip, Render, PostProcess, Write }
public sealed record ProgressUpdate(PipelineStage Stage, int Done, int Total, string? CurrentPath, string? Pack);
public sealed record StageTiming(PipelineStage Stage, TimeSpan Wall, TimeSpan Busy, int Items);
public enum GenerationMode { Apply, DryRun, Check }
// RootSelection { All, Committed, Built } removed 2026-10-02 (spec-errata E42): every run covers every root
public enum FileRole { Main, Block, Companion }
public interface IReadRecorder { void Record(string dependencyKey); }         // one per unit, single-threaded
public sealed record LoadedPack(string Name, int Order, string RootPath, string RelativePath, PackManifest Manifest,
    PackSettings Settings, IReadOnlyDictionary<string, JsonElement> Parameters, IReadOnlyList<ScriptSource> Scripts,
    string ScriptsHash, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TypeMaps);   // types/<target>.json
public sealed record ScriptSource(string Path, string Code, string Hash);
public sealed record PackSet(IReadOnlyList<LoadedPack> Packs, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record PlannedUnit(string Key, LoadedPack Pack, PackUnit Unit, IResolvedObject? Element, string StaticHash);
// Key: "<pack>/<unitId>" for model scope, "<pack>/<unitId>:<elementId>" otherwise
public sealed record UnitPlan(IReadOnlyList<PlannedUnit> Units, IReadOnlyList<Diagnostic> Diagnostics);   // pack order, then Key ordinal
public sealed record UnitOutput(string Path, string ManifestHash, long Length, long LastWriteUtcTicks);
public sealed record UnitState(string Key, string InputHash, IReadOnlyList<string> ReadKeys, IReadOnlyList<UnitOutput> Outputs);
public sealed record SkippedUnit(PlannedUnit Unit, UnitState Previous);
public sealed record SkipResult(IReadOnlyList<PlannedUnit> ToRender, IReadOnlyList<SkippedUnit> Skipped);
public sealed record RenderedFile(string Path, string Text, FileRole Role);   // repo-relative, "/" separators
public sealed record RenderedUnit(PlannedUnit Unit, IReadOnlyList<RenderedFile> Files, IReadOnlyList<string> ReadKeys,
    string InputHash, IReadOnlyList<Diagnostic> Diagnostics, bool Failed);
public sealed record OutputRootInfo(string Path);   // the root folder, or the file an allow entry names (E42: Commit removed)
public sealed record OutputFile(string Path, ReadOnlyMemory<byte> Content, string ContentHash, string ManifestHash,
    OutputMode Mode, FileRole Role, OutputRootInfo Root, bool ContentOmitted = false);   // omitted: plan apply of an Unchanged file (§15)
public sealed record ProcessedUnit(RenderedUnit Rendered, IReadOnlyList<OutputFile> Files, IReadOnlyList<Diagnostic> Diagnostics, bool Failed);
public enum FileChangeKind { Added, Modified, Deleted, Unchanged, HandEdited, Kept, OrphanedOwned, Conflict, NotRendered }   // NotRendered: plans only (§15), added 2026-10-02
public sealed record FileChange(string Path, FileChangeKind Kind, string Pack, string UnitKey, string? OldHash, string? NewHash, string? Diff);
public sealed record WriteSummary(IReadOnlyList<FileChange> Changes, IReadOnlyList<Diagnostic> Diagnostics, int Written, int Deleted);
```

`Changes` lists every file except `Unchanged` ones, sorted by path; `Diff` is set only when requested. The dry run behind a plan
sets the internal `WriteContext.ListUnchanged`, so it also lists `Unchanged` files (never with a diff); see section 15.

### 4.2 Stage interfaces

Every stage is async, takes a `CancellationToken`, observes it at least between files, and reports `ProgressUpdate`s at file (or element) granularity; the resolver and the pack loader take an `IProgress<ProgressUpdate>` like the others. Implementations hold no static mutable state.

```csharp
public interface IModelLoader                                                   // 1 load (W1)
{   Task<LoadResult> LoadAsync(LoadRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct); }
public sealed record LoadRequest(ModelSnapshot? Previous, IReadOnlyCollection<string>? ChangedPaths, bool VerifyHashes);  // null paths = full scan
public sealed record LoadResult(ModelSnapshot Snapshot, ChangeSet Changes);
public interface IModelValidator                                                // 2 validate (W2)
{   Task<ValidationReport> ValidateAsync(ModelSnapshot model, ValidationScope scope, IProgress<ProgressUpdate>? progress, CancellationToken ct); }
public interface IModelResolver                                                 // 3 resolve (W3)
{   Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct); }   // per element
public interface IPackLoader                                                    // part of 4 (W6)
{   Task<PackSet> LoadAsync(ModelSnapshot model, IReadOnlyCollection<string>? packNames, IProgress<ProgressUpdate>? progress, CancellationToken ct); }
public interface IUnitPlanner                                                   // 4 plan units (W6)
{   Task<UnitPlan> PlanAsync(ResolvedModel model, PackSet packs, IScriptSandboxFactory scripts, IProgress<ProgressUpdate>? progress, CancellationToken ct); }
public interface IChangeDetector                                                // 5 skip unchanged (W6)
{   Task<SkipResult> SelectAsync(UnitPlan plan, IDependencyHasher hasher, IUnitStateStore state, ManifestSet manifests,
        GenerationMode mode, bool force, IProgress<ProgressUpdate>? progress, CancellationToken ct); }
public interface IRenderer                                                      // 6 render (W5)
{   IAsyncEnumerable<RenderedUnit> RenderAsync(IReadOnlyList<PlannedUnit> units, RenderContext context, IProgress<ProgressUpdate>? progress, CancellationToken ct);
    Task<RenderedUnit> RenderOneAsync(PlannedUnit unit, RenderContext context, CancellationToken ct); }
public sealed record RenderContext(ResolvedModel Model, PackSet Packs, IReadOnlyDictionary<string, SchemaDiffResult> SchemaDiffs,
    IScriptSandboxFactory Scripts, IDependencyHasher Hasher, int MaxDegreeOfParallelism);
public interface IPostProcessor                                                 // 7 post-process (W8)
{   Task<ProcessedUnit> ProcessAsync(RenderedUnit unit, PostProcessContext context, CancellationToken ct); }
public sealed record PostProcessContext(string RepoRoot, IOutputPathPolicy Paths, IReadOnlyList<FormatterSettings> Formatters, bool FormatterVersionsVerified);
public interface IOutputWriter                                                  // 8 write and manifest (W7)
{   Task<WriteSummary> WriteAsync(IAsyncEnumerable<ProcessedUnit> units, WriteContext context, IProgress<ProgressUpdate>? progress, CancellationToken ct); }
public sealed record WriteContext(GenerationMode Mode, string RunId, IReadOnlyDictionary<string, HandEditPolicy> PolicyByPack,
    ManifestSet Manifests, IReadOnlyList<SkippedUnit> Skipped, IReadOnlyDictionary<string, int> UnitCountByPack,
    bool AllPacks, bool IncludeDiffs, IRunJournal? Journal, IUnitStateStore State,   // E42: Roots removed, orphans over every root
    IReadOnlySet<string>? PlannedPaths = null);             // plan apply: MQ6004 for any write or delete outside it (S19)
```

Supporting interfaces, each owned by the workstream that implements it:

```csharp
public interface IDependencyHasher { string CurrentHash(string dependencyKey);                                  // W6
    string InputHash(string staticHash, IReadOnlyList<string> sortedReadKeys); }
public interface IUnitStateStore { Task<IReadOnlyDictionary<string, UnitState>> LoadAsync(string pack, CancellationToken ct);  // W6
    Task SaveAsync(string pack, IReadOnlyCollection<UnitState> states, CancellationToken ct); }
public enum WriteTarget { Output, Model, Cache, Setup }
public interface IOutputPathPolicy { PathCheck Check(string repoRelativePath);                                  // W7; Output target
    PathCheck CheckEngineWrite(WriteTarget target, string fullPath); }                                          // Model, Cache, Setup
public sealed record PathCheck(bool Allowed, string NormalizedPath, OutputRootInfo? Root, string? RuleId, string? Reason);
public sealed class ManifestSet {                                                                               // W7; immutable
    public IReadOnlyCollection<string> Packs { get; }                                                           // packs with any manifest file
    public bool TryGet(string path, [MaybeNullWhen(false)] out ManifestEntry entry, [MaybeNullWhen(false)] out string pack);
    public IReadOnlyList<ManifestEntry> Entries(string pack);                                                   // ordinal by Path (E42: one manifest per pack)
    public ManifestSet WithJournalOverlay(IReadOnlyList<JournalRecord> records); }                              // resume (§12.4)
public sealed record JournalRecord(string Type, string? Pack, string? Path, string? Hash, string? Unit);         // t: begin|write|delete|pack|end
public interface IManifestStore { Task<ManifestSet> LoadAsync(IReadOnlyCollection<string> packs, CancellationToken ct);   // W7
    Task SavePackAsync(string pack, IReadOnlyList<ManifestEntry> entries, CancellationToken ct); }               // E42: deletes a cache copy of an earlier release
public sealed record ManifestEntry(string Path, string Hash, string Unit);
public interface IRunJournal : IAsyncDisposable { Task<IReadOnlyList<JournalRecord>?> ReadUnfinishedAsync(CancellationToken ct);  // W7
    Task BeginAsync(string runId, string? planId, IReadOnlyList<string> packs, CancellationToken ct);
    ValueTask RecordWriteAsync(string pack, ManifestEntry entry, CancellationToken ct); ValueTask RecordDeleteAsync(string pack, string path, CancellationToken ct);
    ValueTask RecordPackCompleteAsync(string pack, CancellationToken ct); Task EndAsync(CancellationToken ct); }
public interface IRunLock { Task<IAsyncDisposable?> AcquireAsync(bool wait, CancellationToken ct); }          // W7; null = busy
public interface IDiffGenerator { string Unified(string path, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int context = 3); }  // W7
public interface IFormatterRunner { Task<IReadOnlyList<Diagnostic>> VerifyVersionsAsync(IReadOnlyList<FormatterSettings> formatters, CancellationToken ct);  // W8
    Task<FormatResult> FormatAsync(FormatterSettings formatter, string path, ReadOnlyMemory<byte> input, CancellationToken ct); }
public sealed record FormatResult(bool Succeeded, ReadOnlyMemory<byte> Output, Diagnostic? Error);
public interface ISnapshotStore { Task<PhysicalSnapshot?> LoadAsync(string databaseName, CancellationToken ct);       // W8
    Task SaveAsync(PhysicalSnapshot snapshot, CancellationToken ct); }
public interface ISchemaDiffer { PhysicalSnapshot Capture(RDatabase database, int revision);                     // W8
    SchemaDiffResult Diff(PhysicalSnapshot? previous, RDatabase current); }
```

### 4.3 Orchestration

`GenerationService.RunAsync` (W6) runs: lock (§12.4) and journal resume → 1 load (`ModelStore.GetSnapshotAsync`, which rescans by stat) → 2 validate; any error stops the run → pack load and 3 resolve → schema diffs (§14) → 4 plan → 5 skip → 6, 7 and 8 as a streaming pipeline: renderer workers feed a bounded channel (capacity `2 × jobs`) into post-processing workers, which feed the writer's bounded queue (capacity 256 files). Units are ordered pack by pack, so the writer can close a pack's manifest as soon as that pack's last unit arrives. With `StageBarriers = true` (bench only) each of stages 6 to 8 finishes before the next starts, so each has a clean wall time. **Dry run** runs stage 8's comparison with no side effects: no journal, manifest, unit-state, snapshot or output write, and a `FileChange` with `Diff` per file. **Check** is a dry run that renders every unit (the cache is ignored), keeps committed roots only, and hashes every manifest file on disk. As built on 2026-10-02 (spec-errata E42), there are no root kinds: check covers every root, and a missing or different manifest entry is drift too (a block compares the hash of its lines; a block whose target is missing without `createFile` is not drift).

## 5. Loader, index and `ModelStore` (W1)

- **Load**: enumerate `maquettiste.json`, `model/**/*.json`, description sidecars, `extensions/*.json` (into `Extensions`) and `extensions/rules/*.js` (into `RuleScripts`); read in parallel (`jobs` readers); for each file parse → `ISchemaRegistry.Evaluate` → deserialize to the kind's CLR type. A file that fails is left out of the snapshot and its diagnostics go into `LoadDiagnostics` (MQ1001 JSON, MQ1002 schema, MQ1004 duplicate id, MQ1006 bad ULID, MQ1007 unsupported `formatVersion`). Non-canonical files load, with warning MQ1003. Then `ModelSnapshot.Create`.
- **Index cache** at `EngineOptions.CacheDirectory/index.v1.bin` (never under the bind mount; S13): header (`MQIX`, format 1, engine version) then one record per file: path, length, last-write ticks, SHA-256, file bytes, sidecar hash. On open, a file whose length and mtime match its record is taken from the cache without reading it through the mount; any other file is read and hashed. `VerifyHashes` (and `RescanAsync(verify: true)`) re-hashes everything. The cache is rewritten atomically after a load that changed anything. Records are content-addressed, so a cache from another checkout is harmless.
- **ETag** = `ElementDocument.Hash`, the SHA-256 of the file bytes as lowercase hex, 64 characters.
- **Change hook**: `RefreshAsync(paths)` re-reads only those paths and returns a `ChangeSet`; a path whose hash equals the indexed hash yields nothing, so the store's own writes (already indexed) never echo as changes (host-contracts 19). `OnChanged` subscribers get every non-empty `ChangeSet`. The CLI's `--watch` and the future functions watcher call `RefreshAsync`.
- **Saves** go through the canonical writer, a `SemaphoreSlim` (one writer per store), staged temp files in the target folder, then `File.Move(overwrite: true)`. A save validates the element and every element that references it before touching disk. A save that changes an id, or a stereotype's `key`, is `Invalid` (MQ3020 for the key). Every file the store and the loader write (model files, the index cache) is checked first with `EngineServices.EnginePaths.CheckEngineWrite` (§12.1).

**Document store (added 2026-10-05).** The loader and the store read and write the model's documents through
`IModelDocumentStore` (the model folder, `FileDocumentStore`; or a snapshot archive, read-only); the seam, the snapshot archive
and the read-only store over it are designed in `docs/engineering/snapshots.md`.

## 6. Validation (W2)

```csharp
namespace Maquettiste.Engine.Diagnostics;
public enum DiagnosticSeverity { Error, Warning, Info }      // JSON error|warning|info; SARIF error|warning|note
public sealed record Diagnostic(string Rule, DiagnosticSeverity Severity, string Message, string? ElementId, string? FilePath,
    string? JsonPointer, int? Line, int? Column);            // FilePath repo-relative with "/"; Line and Column 1-based
public sealed record ValidationScope(IReadOnlyList<string>? ElementIds = null, bool IncludeReferrers = true, bool IncludeScriptRules = true);   // null ids = whole model
public sealed record ValidationReport(IReadOnlyList<Diagnostic> Diagnostics, int Errors, int Warnings, int Infos, bool Truncated = false)
{   public bool HasErrors => Errors > 0;
    public ValidationReport TruncateTo(int maxJsonBytes);   // (host-contracts 20) keeps counts, drops trailing diagnostics, sets Truncated
    public IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> ByElement(); }   // key: element id or file path
```

Diagnostics sort by `(FilePath, Line, Column, Rule, Message)` ordinal. `Line` and `Column` come from `JsonPositionLocator` (W1), which re-reads a file's bytes with `Utf8JsonReader` only when a diagnostic needs them. `maquettiste.json` `validation.rules` sets a rule's severity or turns it `off`; MQ1xxx cannot be turned off.

**Rule catalog** (`RuleCatalog`, id → default severity and short description; SARIF `rules[]` comes from it):

| Range | Rules |
| --- | --- |
| MQ1xxx file | 1001 invalid JSON · 1002 schema violation · 1003 not canonical (warning) · 1004 duplicate id · 1005 file in wrong folder or name mismatch (warning) · 1006 invalid ULID · 1007 unsupported format version · 1008 `.schema` out of date (warning) · 1009 a second tag vocabulary or category tree (the ordinally first is used) · 1010 an `outputs.allow` entry sets the retired `commit` (info; as built 2026-10-02, E42) |
| MQ2xxx references | 2001 dangling reference · 2002 reference to wrong kind · 2003 unknown stereotype · 2004 stereotype not applicable to kind · 2005 unknown category · 2006 undeclared tag (error when `strict`, else info) · 2007 attribute validation names an unknown rule |
| MQ3xxx conceptual | 3001 duplicate name in scope · 3002 inheritance cycle · 3003 package cycle · 3004 category cycle · 3005 entity without key · 3006 key names missing attribute · 3007 duplicate attribute name, virtual included · 3008 relation end count or kind mismatch · 3009 navigation collides with a member · 3010 invalid cardinality · 3011 set-null on a required end · 3012 enum member name or code duplicated; flags values not powers of two · 3013 facet invalid for type · 3014 scalar base not built-in · 3015 value object containment cycle · 3016 composition child with two owners · 3017 default looks like a credential (warning; S19) · 3018 invalid name for kind · 3019 literal `default` does not match the attribute's effective type (a string on a numeric, bool or temporal type, a non-ISO 8601 temporal string, a value outside an enum's member names or codes, a number out of range); when the literal is a known expression name (`now`, `today`, `new-uuid`, `new-ulid`) the message says "use defaultExpression" · 3020 a save changes a stereotype's key |
| MQ4xxx physical | 4001 identifier over dialect limit · 4002 duplicate table name in schema · 4003 duplicate column name · 4004 two mappings for one target · 4005 FK column type mismatch · 4006 native type unknown to dialect map, a plain name without quotes or schema (warning) · 4007 overlay references a missing column key · 4008 constraint or index names a missing column · 4009 mapping option invalid for the element · 4010 view body missing for the database dialect · 4011 a relation whose ends are both bound to designed or imported tables (`Mapping.Table`) names no `Mapping.ForeignKey`, or a designed `JunctionTable` without an `Ends` entry per end · 4012 an entity lands in no database (info; D46) · 4013 a database's `packages` unused because `byConvention` is `all` or `none` (warning; D46) · 4014 a convention package entry or an entity mapping names a schema its database does not declare (E26) · 4015 a schema operation refused: unknown schema or taken name, occupants and no target, or the default and no new default (E26) · 4016 a quoted or schema-qualified native type naming a type the database defines, once per type and database with its column count (info) |
| MQ5xxx extensions | 5001 property fails extension schema · 5002 rule script error · 5003 rule exceeded a sandbox limit · 5004 invalid extension file |
| MQ6xxx packs | 6001 invalid pack.json · 6002 engine range not satisfied · 6003 template parse error · 6004 output path refused · 6005 duplicate or case-colliding output path · 6006 render error · 6007 sandbox limit exceeded · 6008 formatter failed or version mismatch · 6009 hand edit · 6010 protected region lost · 6011 text outside file blocks in a unit without output (warning) · 6012 non-deterministic builtin used · 6013 helper name collides with a builtin · 6014 unit names an unknown formatter (warning) · ~~6015 regions mode on a built root~~ (retired 2026-10-02, E42) · 6016 script error · 6017 selector returned an unknown id · 6018 stale schema snapshot · as built 2026-10-02 (E42): 6019 now reads "cannot stay under an allowed output root or be an allowed file", 6027 a block unit's file holds its block twice or unclosed, left alone · 6028 a block unit's target file is missing without `createFile` (info, target-missing) |
| MQ8xxx project | 8001 a `branding.colors` value that is not `#rrggbb` or `#rgb` · 8002 `branding.icon` is not `branding/<name>.svg` or `.png` in the model folder, or the file does not exist · 8003 the icon is not a safe SVG (scripts, event handlers, external references) or a PNG of at most 512 KB. Whole-model validation; MQ7xxx (reference data and localization) is in reference-types-seeds-localization.md |
| `x/<id>` | JavaScript rules; the severity comes from the rule |

**Native types (MQ4006, MQ4016; status: built 2026-10-01).** As built on 2026-10-01, the validator reads a column's native type before the lookup: arguments and array brackets go, identifier quotes (`"..."`, `[...]`, backticks) are stripped and a schema or other prefix is dropped, so `"public"."citext"` is the known `citext`. A name is known when it is a native type of the dialect, the base of a value of the dialect's effective type map (`Resolution/Dialects/*.json` with the project's `typeMaps` applied), the base of a native type a custom type declares for the dialect (as built later on 2026-10-01, section 7.6), or the snake or kebab name of one of the model's reference types or enums, with or without the `_t` suffix sql-ddl gives a reference type stored as a native type. Any other quoted or qualified name is a type the database defines, which the validator cannot check: MQ4016 (info) is reported once per type and database, on the first column that uses it in path order, with the number of columns and tables that use it (a model read from a database had hundreds of MQ4006 warnings for one enum type). A plain unknown name stays MQ4006 (warning) on each column. Scoped validation revisits the first holder of such a type when another table using it changes, and the tables whose native types carry a reference type's or enum's name when that element changes.

**Extension schemas**: for each element (and attribute, enum member, column), merge the stereotype `DefaultProperties` (stereotype order, later wins) under the element's own `properties`, then evaluate the result against each applicable `ExtensionSchema` as `{ "type": "object", "properties": …, "required": … }` through `ISchemaRegistry` (MQ5001, pointer `/properties/<name>`).

**JavaScript rules** live in `extensions/rules/*.js` and register with `maquettiste.rule({ id, severity, kinds, check(element, model, report) })`. `element` is a frozen copy of the element's canonical JSON; `model` offers `get(id)`, `all(kind)` and `referencesTo(id)`, also frozen; `report(message, { pointer, severity })`. Rules run through a validation pool (§10) in parallel over elements.

**Extension files (status: built 2026-10-01).** As built on 2026-10-01, a rule file that does not load (a syntax error, a `maquettiste.rule` call the sandbox refuses, a limit while loading) is MQ5002 (MQ5003 for a limit) on that file with the sandbox's line and column, and the validator creates the pool again without it, so the other rule files still run; while one is broken the rule ids are unknown and MQ2007 is not reported. `ModelStore` edits the folder for the editor's Extensions tab and the MCP extension file tools: `ListExtensionFilesAsync` (each file with its hash, its own diagnostics and, for a rule file, the rules it registers with their severity), `ReadExtensionFileAsync`, `WriteExtensionFileAsync` (a schema must pass `extension.json` and is written canonical; a rule file is written as sent and loaded alone in the sandbox, its MQ5002 returned), `DeleteExtensionFileAsync` and `MoveExtensionFileAsync` (within its kind). Paths are `<name>.json` and `rules/<name>.js` under `extensions/`, never through a link; every write takes the hash read, goes through `CheckEngineWrite(WriteTarget.Model, …)` under the store's gate, and reloads the snapshot before it returns.

**SARIF**: `SarifWriter.WriteAsync(Stream, IReadOnlyList<Diagnostic>, string toolVersion, CancellationToken)` writes SARIF 2.1.0 asynchronously (`Utf8JsonWriter` and `FlushAsync`, so it can stream to a response body that refuses synchronous I/O) with tool `maquettiste`, rules from the catalog plus seen `x/` ids, results in diagnostic order, `artifactLocation { uri, uriBaseId: "%SRCROOT%" }`, a region when line is known, and `properties { elementId, jsonPointer }`. `validate --format json` writes `{ "errors", "warnings", "infos", "diagnostics": [...] }`, described by `diagnostics.json`.

## 7. Resolver (W3)

The resolver turns a valid `ModelSnapshot` into the S10 resolved model in one pass, deterministically (every list below is sorted as stated, never by hash order). Each resolved object lists its **dependency keys** (§11): the `e:` keys of every file that contributed to any of its members, plus `s:conventions` / `s:typeMaps` / `s:inflection` when those were used, plus **`r:<id>`** for every entity and relation it derives from. `e:` keys cannot see a file that did not contribute when the unit was last rendered, such as a mapping, a table overlay or a relation created later; `r:<id>` changes whenever a file starts or stops referencing that id (D38). So every `REntity`, `RRelation`, promoted entity, synthesized `RTable` (entity, child and junction tables), each of their `RColumn`s, `RForeignKey`s and indexes, `REntityMapping`, `RRelationMapping` and `RNavigation` lists `r:<entityId>` for each entity it comes from and `r:<relationId>` for each relation.

```csharp
namespace Maquettiste.Engine.Resolution;
public interface IResolvedObject { string Id { get; } string Kind { get; } IReadOnlyList<string> Dependencies { get; } }
public sealed class RList<T> : IReadOnlyList<T> where T : IResolvedObject { public IReadOnlyList<string> MembershipKeys { get; } }  // every R-type list property
public sealed class ResolvedModel { ModelSnapshot Source; ProjectSettings Settings; IReadOnlyList<RPackage> Packages; IReadOnlyList<REntity> Entities;
    IReadOnlyList<RValueObject> ValueObjects; IReadOnlyList<REnum> Enums; IReadOnlyList<RScalarType> ScalarTypes; IReadOnlyList<RRelation> Relations;
    IReadOnlyList<RDatabase> Databases; IResolvedObject? Find(string id); IReadOnlyList<Diagnostic> Diagnostics; }   // get-only properties
    // Phase 3 (P5a) adds RList<RProcess> Processes, RList<RActor> Actors and RList<RScenario> Scenarios (section 7.1a).
// Common to all conceptual R-types: Id, Name, DisplayName (fallback Name), PluralName (fallback inflector), Description (text, sidecar loaded),
// Tags, Category (RCategory: Id, Name, Path), Stereotypes (RStereotype: Key, Name, Icon, Color), Properties (merged, plain CLR values),
// Generation (IReadOnlyDictionary<string, GenerationHints>), Package (RPackage?), bool HasStereotype(string key), bool HasTag(string key).
// All of these but Name and Package sit on RAnnotated, which RDatabase, RSchema, RTable, RColumn, RView and RSequence share (filled from their own
// files or entries, no fallbacks; §7.0a).
RPackage     : QualifiedName ("Billing.Invoicing"), Parent, Children, Entities, ValueObjects, Enums, Relations
REntity      : IsAbstract, Base, Derived, Attributes (flattened, §7.2), OwnAttributes, Key (RKey: Attributes, Strategy, Sequences (db name → RSequence)), AlternateKeys,
               Navigations, Relations, Mappings (IReadOnlyDictionary<string /*db name*/, REntityMapping>), IsPromoted, PromotedFrom (RRelation?),
               Lifecycle (RProcess?, phase 3), Bindings (db name → REntityBinding; erratum E43)
RAttribute   : Owner, DeclaringEntity, Type (RType), Required, Default, DefaultExpression, Length, Precision, Scale (effective, scalar facets applied),
               Collection, Unique, Indexed, ReadOnly, Immutable, Derived, Sensitive ("pii"|"secret"|null), Validation, Order, IsInherited, IsVirtual, FromStereotype
RType        : Kind ("builtin"|"enum"|"value-object"|"scalar"), Name, Builtin (effective keyword; null for enum/value object), Enum, ValueObject, Scalar
REnum        : Flags, Members (REnumMember: Id, Name, DisplayName, Description, Value, Code, Properties); RValueObject: Attributes; RScalarType: Base, facets, Validation, NativeTypes (dialect name → pattern as written, sorted)
RRelation    : InverseName, RelationKind, Ends (REnd: Id, Entity, Role, Navigation, Min, Max ("1"|"*"), IsMany, OnDelete, Ordered, Opposite),
               Attributes, AllowDuplicates, Cardinality ("one-to-one"|"one-to-many"|"many-to-many"|"n-ary"), Mappings (db name → RRelationMapping)
RRelationMapping : Shape ("foreign-key"|"junction"|"promoted"), ForeignKey (RForeignKey?), JunctionTable (RTable?), PromotedEntity (REntity?)
RNavigation  : Name, Relation, From (REnd), To (REnd), Target (REntity), IsCollection, Joins (db name → RJoinPath)
REntityMapping : Database, Table, Inheritance ("tph"|"tpt"|"tpc"|null), DiscriminatorColumn, DiscriminatorValue, Columns (RColumnMapping: AttributePath, Column), Joins
RJoinPath    : Steps (RJoinStep: FromTable, FromColumns, ToTable, ToColumns, ViaJunction)
RDatabase    : Name, Dialect, Version, DefaultSchema, Schemas (RSchema: Name, IsDefault, IsDeclared, Tables, Views, Sequences, and the RAnnotated
               members), Tables, Views, Sequences, Quoting, MaxIdentifierLength, ByConvention ("all"|"packages"|"none", effective),
               Packages (RConventionPackage: Package (RPackage?), PackageId, Schema (name?)), and the RAnnotated members
RTable       : Key, Name, Schema (string?), Database, Origin, Entity, Relation, Attribute (a child table's), Columns, PrimaryKey (Name, Columns,
               Clustered), Uniques (RUnique: Id, Name, Columns), ForeignKeys (RForeignKey: Id, Name, Columns, ReferencedTable, ReferencedColumns,
               OnDelete, OnUpdate, Relation, End), Checks (RCheck: Id, Name, Expression), Indexes (RIndex: Id, Name, Columns (Column, Descending),
               Include, Where, Unique, Method), Comment, IsJunction, and the RAnnotated members; a constraint's Id is its file's, null when synthesized
RColumn      : Key, Name, Table, Type, Length, Precision, Scale, NativeType, Nullable, Default, DefaultSql (for the dialect), Identity, Sequence,
               Computed, ComputedStored, Collation, Comment, Attribute, AttributePath, IsPrimaryKey, IsForeignKey, IsDiscriminator, Position,
               and the RAnnotated members (from the column's own entry: designed, extra or overlay; never the attribute's)
RView        : Name, Schema, Database, Body (for the dialect), Columns, Comment;  RSequence: Name, Schema, Database, Type, NativeType, Start, Increment,
               Min, Max, Cycle, Cache (both with the RAnnotated members)
// Scaffold shapes: every R-type is a public sealed class with public getters and internal setters, so only the resolver fills it.
// RObject (abstract: Id, Kind, Dependencies) is the base of every IResolvedObject; RAnnotated : RObject carries the annotations above
// (DisplayName to Generation, HasStereotype, HasTag) and RElement : RAnnotated adds Name and Package. RDatabase, RSchema, RTable, RColumn,
// RView and RSequence derive from RAnnotated; REnumMember, REnd and RNavigation derive from RObject.
// Enumerations reach templates as kebab strings ("uuid-v7", "set-null", "no-action"). Helper shapes: RKey, RAlternateKey (Id, Name,
// Attributes), RDerived (Expression, Stored), RValidation (Min, Max, Pattern, AllowedValues, Rules as plain values), RPrimaryKey
// (Name, Columns, Clustered), RUnique (Id, Name, Columns), RCheck (Id, Name, Expression), RIndexColumn, RViewColumn (Name, Type,
// NativeType, Nullable). ResolvedModel.Find uses an internal ById index the resolver fills.
```

**7.0a Annotations of the physical model (status: built 2026-10-01).** As built on 2026-10-01, a table, view or sequence
carries the annotations its own file holds (display and plural names, description with a sidecar loaded, tags, category, stereotypes,
properties merged over the stereotypes' default properties, generation hints) through `RAnnotated`; before, resolution dropped them
and kept only `Comment`. The file is the designed or imported table, a synthesized table's overlay, or the view or sequence file;
`DatabaseRun.AddTable` and the view and sequence passes call `ResolveRun.FillPhysicalAnnotations`, which adds the category tree's
and each stereotype's `e:` key. Display and plural names have no fallback (empty when the file sets none), and an object without a
file (a synthesized table without an overlay, a key sequence) has none of them: it never takes its entity's or relation's, which
templates read through `table.entity` and `table.relation`. `has_stereotype`, `has_tag`, `in_category`, the scripts' `hasStereotype`
and `hasTag`, and the `hints` variable accept any `RAnnotated`. `RView` gains `Comment`. The E1 projection (`TableView`, and the
new `ViewView` and `SequenceView` lists on `DatabaseView`) carries them too, with display and plural names as null when unset.
As built later on 2026-10-01, the rest of the physical model follows the same rule: `RDatabase`, `RSchema` (a schema entry of the
database file; a schema known only because an object uses it has none) and `RColumn` (the column's own entry: a designed or extra
column, or a synthesized column's overlay entry, never its attribute's, which templates read through `column.attribute`) derive from
`RAnnotated` and are filled by `FillPhysicalAnnotations` (a column's stereotype and category keys join its table's set, a schema's
join the schema's own `e:`/`r:` pair of the database). `ResolvedCoverageTests` walks every document kind's schema (and the parts
that resolve to objects of their own) and asserts that each property is a member of the resolved type, so what it found came in
with this round: `RDatabase.ByConvention` and `Packages`, `RSchema.IsDefault` and `IsDeclared`, `RTable.Attribute` (a child
table's attribute), `RSequence.Database`, `RPrimaryKey.Clustered` and the file ids of uniques, foreign keys, checks and indexes;
the gaps it lists for other kinds (enum members, relation ends, stereotypes, categories, and the mapping, diagram, tag vocabulary
and category tree, which resolve to no object of their own) wait for a later round. The E1 projection gains `ColumnView`'s
annotations with its default, sequence, collation and comment, and `DatabaseView`'s annotations, quoting, identifier limit,
convention and `schemas` (`SchemaView`). The scopes `each view` and `each sequence` (erratum E38) plan one unit per view or
sequence of every database (key sequences included); their alias is `view` or `sequence`, `generation.skip` and `where`
(tags, stereotypes, categories, `database`, `script`) read the object's own annotations, and `packages`, `notPackages` and
`abstract` are refused at pack load (MQ6001). The `sql-ddl` pack's `view` and `sequence` units use them, off unless its
`objectScripts` parameter is set.
As built later on 2026-10-01, the `comments` convention (`none` or `descriptions`, default `descriptions`, project ←
database like the other conventions) fills `Comment` where no file sets one: `DatabaseRun.FillComments`, in the first pass of
`FinishTables`, gives a column its own entry's description, else its attribute's, and a table its own description, else its
child table's attribute's, its entity's or its junction's relation's (trimmed; blank counts as none). An explicit `comment`
always wins, `Description` itself is untouched (an object still takes no annotation from its entity or attribute), and the
inputs are already among the table's dependency keys (the producing entity, value object or relation, and `s:conventions`).
So sql-ddl's `comments` parameter now writes descriptions as `COMMENT ON` text, which changed the billing and processes
goldens; its SQLite column comments moved to a line of their own above the column, since a trailing one swallowed the comma
after it. The editor's Database screen edits columns (name, type, null, default, comment, description) in the table's file,
creating a synthesized table's overlay with just the edited column on its first edit.

**7.1 Ordering.** Packages by qualified name; entities, value objects, enums, scalars and relations by (package qualified name, name, id); databases by name; tables by (schema, name); columns by `Position`.

**7.1a Processes, actors and scenarios (phase-3-design.md sections 4.3 and 7.1, status: built 2026-09-30, P5a).**
`ResolveRun.Processes.cs` resolves them after the conceptual layer (a lifecycle reads its subject's flattened attributes) into
`ResolvedProcesses.cs`: `RProcess : RElement` (`Use`, `Subject`, `BoundAttribute`, `BoundEnum`, `Context`, `Events`, `Guards`,
`Actions`, `States` (the root's children, a tree), `AllStates` (document order), `AtomicStates`, `BoundStates`, `Transitions`
(priority), `Gates`, `Invokes`, `Actors`, `Scenarios`, `Initial`); the process nodes derive from `RProcessNode : RObject`
(`DisplayName`, `Description`, `Stereotypes`, `Properties`): `RState` (`Name`, `Path`, `Type`, `Parent`, `Children`, `Initial`,
`History`, `DefaultTarget`, `Entry`, `Exit`, `Invoke`, `IsFinal`, `IsAtomic`, `Depth`, `BoundMember`, `TransitionsOut`,
`RegionIndex`), `RTransition` (`Source`, `Targets`, `Trigger`, `Event`, `After`, `AfterMs`, `AfterTicks`, `Invoke`, `Guard`,
`GuardMissing`, `Actions`, `External`, `Gate`, `Label`, `IsTargetless`), `REvent` (`Name`, `Payload`, `Actors`, `Transitions`), `RGuard` and `RAction` (`Name`, `Expression`,
`IsStub`, `UsedBy`; an action also `Raises`), `RInvoke` (`Name`, `Type`, `Process`, `Actors`, `State`), `RGate` (`Name`, `Transition`,
`Required`, `Signers`, `RequiredActors`, `AllowRepeatSigner`, `ReasonRequired`, `Meanings`, `AuditAttributes`, `Audit`), `RMeaning`
(`Name`); `RAuditField : RObject` (`Name`, `Type`, `Required`, `Description`, `Values`, `Attribute`); `RActor : RElement` (`Type`,
`Processes`, `Events`, `Gates`; no package); `RScenario : RElement` (`Process`, `Start` (`RScenarioStart`: `Context`, `At`), `Steps`,
`Outcome`; its package is its process's) with `RStep : RObject` (`Index`, `Input`, `Event`, `Invoke`, `After`, `AfterMs`,
`AfterTicks`, `Actor`, `Signer`, `Meaning`, `Reason`, `Payload` (event payload and gate audit attributes by name), `Assume`, `Expect`
(`RExpectation`: `Accepted`, `States`, `StatePaths`, `Context`), `Description`, `Trace` (`RStepTrace`: `Accepted`, `Refusal`, `Audit`
outcomes, `StatePaths`, `Final`: the engine interpreter's replay of the scenario, run once per scenario on first read through a
`ProcessRuntime` over the resolved snapshot; null for a step the replay did not reach)). `AfterMs` drops the part below a
millisecond, `AfterTicks` keeps it (review fixes of P5, 2026-09-30); `GuardMissing` marks a transition naming an undeclared
guard, which the interpreter treats as never holding. Paths, document order, depth, initial children, history defaults and priority are read from the interpreter's
`StatechartModel`, never re-derived; `AfterMs` uses its fixed spans (a month 30 days, a year 365); `Label` follows the editor's edge
label rule (`ProcessText`, checked against `chartModel.ts` on the gate 3 fixture). Scenario maps key by attribute or guard name.
Orders: processes by (package qualified name, name, id), actors by (name, id), scenarios by (process order, name, id). Dependency
keys: every process node lists the process file's `e:` key and those of its subject and bound enum, plus the `e:` keys of the
actors and invoked processes it names itself; a scenario and its steps list the scenario's and the process's `e:` keys (a step also
its actor's). List memberships: `model.processes` `k:process`, `model.actors` `k:actor`, `model.scenarios` `k:scenario`; a list of
actors adds `k:actor` to the process keys; `process.scenarios` is `k:scenario` and `r:<process id>`; an actor's `processes`,
`events` and `gates` are `r:<actor id>`; an entity with a `lifecycle` adds `k:process`. The process object itself carries no `r:`
key, so editing a scenario (a referrer of its process) re-renders only the units that read that scenario or enumerate
`process.scenarios`.

**7.2 Attribute order.** Flattened list = base-entity attributes (root first), then own attributes in array order, then stereotype virtual attributes (stereotype order, then array order); then a stable sort by `Order`, where a missing `Order` counts as 0. So `Order: -1` moves an attribute before the others and `Order: 900` moves it after.

**7.2a Which entities a database holds (D46, status: built 2026-09-29, round 7).** `DatabaseScope.Places` decides it for the resolver's placements and for MQ4012: a mapping element for (database, entity) places the entity unless it sets `ignore`; otherwise the database's `byConvention` decides (`all`; `packages`: the entity's package or an ancestor is listed; `none`; absent: every entity when `packages` is empty, else as `packages`). Before round 7 a mapping for an entity outside `packages` was ignored; it now places the entity (E24). Validation revisits every entity when a database changes, and when a package changes while a database lists packages.

**7.2b Which schema a conventional table goes to (E26, status: built 2026-09-29, round 9).** `DatabaseScope.SchemaFor` answers for an entity's table: the entity's mapping `schema`, else the schema of the nearest `packages` entry (walking up from the entity's package) that names one, else none; `DatabaseRun.NewEntityTable` puts a table file's (overlay's) `schema` first and falls back to the database's default schema (`defaultSchema`, else the dialect's). A `packages` entry is a package id or `{ "package", "schema" }`; `ConventionPackageListConverter` reads both and writes the id when there is no schema, and the canonical writer does the same through the schema keyword `x-collapse`. Schema references are ids, so renaming a schema touches only its entry and, when it is the default, `defaultSchema`. The batch operations `add-schema`, `rename-schema`, `remove-schema` and `set-default-schema` (`ModelStore.Schemas.cs`) expand into element updates executed with the batch's other operations; `remove-schema` lists the tables, views, sequences, convention entries and mappings still in the schema (MQ4015) unless a `target` receives them. Validation revisits a database's mappings with a `schema` when the database changes (MQ4014).

**7.3 Physical keys.** File-backed tables keep their ULID as `Key`. A synthesized table's key is `<entityId>@<databaseId>` (junction: `<relationId>@<databaseId>`; child table: `<entityId>.<attributeId>@<databaseId>`; the enum lookup table and its `enum` overlay are retired, MQ7012). A table overlay file targets exactly one of them (D9): `entity` (the entity table), `entity` + `attribute` (that entity's child table for the attribute, own, inherited or virtual, so TPC copies stay distinct) or `relation` (junction). A synthesized key sequence (§7.5) has the key `<entityId>.sequence@<databaseId>`. Column keys are attribute paths: `<attrId>`, `<attrId>.<memberAttrId>` for embedded value objects, `<endId>.<keyAttrId>` for FK columns, `discriminator`, `position`, `id` (surrogate). Overlay columns (`Column.Attribute`), constraint and index column lists, and snapshots all use these keys, which is what lets the schema diff see renames.

As built on 2026-10-01, the physical side of a synthesized column is free: an overlay column entry may set `name`, `type`, `length`, `precision`, `scale`, `nativeType`, `nullable`, `default`, `comment` and `description` (and the other column members), and `DatabaseRun.AddColumn` applies each over what the attribute gives (`ApplyColumnFile` for the default, comment and annotations; a native type is computed from the resulting type and facets unless the entry sets one). The attribute keeps its own type and facets in the resolved model (`RColumn.Attribute.Length` stays 255 under a column the overlay makes `text` or `varchar(2056)`): the attribute's length is validation, the column's is storage, and no rule compares the two. A synthesized foreign key column (relation, junction, child table owner key, TPT derived key) copies the referenced column's type, facets and native type unless its own overlay entry pins any of type, length, precision, scale or native type. `DatabaseRun.CheckForeignKeyColumns` then runs MQ4005 over the resolved columns, after `FinishTables`: each foreign key column must have the referenced column's type and native type (ordinal, native type case-insensitive) and the facets its type uses (length for string, text and binary; precision for decimal and the time types; scale for decimal). A mismatch is reported on the file that pins it, with a pointer: the foreign key column's overlay entry or designed-table column, else the referenced column's, else the relation end (`/ends/<i>`) of the key, else the entity that declares the column's attribute. Validation's file-level MQ4005 still compares the declared types when both tables are files; the resolved check skips exactly that case (both columns declared in files with differing types), so a column is reported once. Every validate path reports these resolver findings, not only a generation run: `ModelStore.ValidateAsync` (the editor's validation loop and `POST /api/validate`, `maquettiste validate` with its JSON and SARIF, the MCP `validate` tool) adds the resolver's diagnostics once the whole-model validation, load diagnostics included, has no error, resolving once per snapshot (`ModelStore.ResolverFindingsAsync`, kept with the snapshot, positioned with `Positions`), filtered to a scope like the load diagnostics, and dropping an exact repeat or an unpointed finding whose rule validation already reports on the same element (`ModelStore.NewFindings`).

**7.4 Relation shapes** (S7, binary relations; the mapping's `Shape` overrides). **Binding to existing tables** (S5 "relation to foreign key or junction table"): when the dependent end's entity is bound to a designed or imported table (`Mapping.Table`), the relation mapping's `ForeignKey` names the existing foreign key in that table that realizes the relation, and the resolver uses its columns instead of synthesizing FK columns into a full-definition table; when both ends are bound to such tables and no `ForeignKey` is named, it is MQ4011. A designed `JunctionTable` binds each end through `Ends` (`{ end, foreignKey }`, a foreign key of the junction table); a missing end is MQ4011. When only the principal is bound, FK columns are synthesized into the dependent's synthesized table as usual. one to one (both `Max` 1) → FK with a unique constraint in the dependent end's table, where the principal is the end with `Min` 1 (on a tie `Ends[0]` is the principal unless `Mapping.ForeignKeyEnd` names the dependent end); one to many → FK in the table of the many end's entity, referencing the end with `Max` 1, nullable when that end has `Min` 0; many to many without attributes → junction with a composite PK of the FK columns; any relation with attributes → `Conventions.RelationsWithAttributes` (default junction: extra columns hold the attributes); three or more ends → junction with one FK per end; promoted → a synthesized `REntity` with the relation's id and two many-to-one relations whose ids are `<relationId>.<endId>`. `AllowDuplicates` or an `Ordered` end gives the junction a surrogate `id` PK; `Ordered` adds the `OrderColumn`. FK `OnDelete` is the principal end's intent (`None` → no action, `Composition` → cascade by default); junction FKs cascade unless the end says `restrict`.

**7.5 Keys, inheritance, enums, value objects.** The `sequence` identity strategy is resolved per database (D37): the key column's overlay in that database's table file (`generated: sequence`, `sequence: <id>`) names the sequence; without one, the resolver synthesizes a sequence named by `Conventions.SequenceName` (default `{table}_seq`) with the key of §7.3, and `RKey.Sequences` and the key column's `RColumn.Sequence` point at it. TPH: the root table holds every attribute in the hierarchy plus the discriminator column (string, length 64), with values defaulting to entity names. TPT: each entity's table holds its own attributes, and its PK is also an FK to the base table. TPC: each concrete entity's table holds all inherited attributes, and abstract entities get no table. Enum storage: `int` (member `Value`, else ordinal) or `string` (`Code`, else name; length = longest). The `lookup` option (a synthesized lookup table with an FK) is retired: a settings or mapping file that still sets it does not load and reports error MQ7012 naming the conversion to a reference type, whose storage strategy the templates realize (principle: the engine models intent, templates decide persistence). Value objects: `embedded` (prefixed columns, recursive), `table` (child table keyed by the owner PK), `json` (one `json` column); collections are `table` (owner FK plus `position`) or `json`.

**7.6 Dialect types.** `Resolution/Dialects/<dialect>.json` embeds the default map from keyword to native pattern (`"decimal": "numeric({precision},{scale})"`, `"string": "varchar({length})"`), overridden entry by entry by `typeMaps.<dialect>` in `maquettiste.json`, then by `Column.NativeType`. Missing facets take convention defaults.

**Custom type native types (status: built 2026-10-01).** As built on 2026-10-01, a custom type may declare `nativeTypes` (dialect name → pattern with the same placeholders). The resolver applies it in one place, `DatabaseRun.AddColumn`: a synthesized column that stores a custom type (an attribute's or value object member's column, a child table's value column, and a key column copied from one into a child table, a foreign key or a junction table) renders the custom type's pattern for the database's dialect instead of the type map entry of its base, unless an overlay sets the column's native type (which wins) or changes its type away from the base. The order is therefore: `Column.NativeType`, then the custom type's native type, then `typeMaps.<dialect>`, then the embedded map. The column's `Type` stays the base keyword, so code type maps (`type_of` with a pack target) keep the base; `type_of` with a dialect target renders the custom type's pattern for an attribute, type or custom type of it. The validator counts the base names of every active custom type's native types for a dialect as known (MQ4006, MQ4016), and a custom type revisits the tables whose native types carry one of them. The fixed dialect lists were completed the same day with each dialect's documented built-in types (PostgreSQL's `pg_lsn`, `pg_snapshot`, `txid_snapshot`, `xid`, `xid8`, `cid`, `tid`, the `reg` object identifier types, `jsonpath` and the multiranges, plus the extension types `ltree`, `lquery`, `ltxtquery`, `cube`, `earth`, `geometry`, `geography`; the ISO synonyms, `vector` and `sysname` on SQL Server; the national character and compatibility names, `geomcollection`, `vector`, `uuid`, `inet4` and `inet6` on MySQL; the ANSI names, `vector`, the spatial, any and URI types on Oracle).

**Routines, database types and SQL objects (status: built 2026-10-01, erratum E40).** As built on 2026-10-01, a database holds
three more element kinds beside tables, views and sequences, each a file under its database's folder and each carrying the
annotations, `database`, `schema` and `source` a view has:

- `routine` (`routines/`, `Routine`, `routine.json`): `routineKind` (`function`, the default, or `procedure`); `parameters`
  (`name`, `type`, `length`, `precision`, `scale`, `nativeType`, `mode` `in|out|inout`, `default` as SQL text; `type` or
  `nativeType` required); `returns` (a single value through `type` or `nativeType` with facets, or `table`, a list of columns
  with `nullable`; absent means no result); `language`; `body` (a dialect map, required); `deterministic`; `security`
  (`invoker|definer`); `dependsOn`; `comment`.
- `database-type` (`types/`, `DatabaseType`, `database-type.json`): `typeKind` (`domain|composite|enum|range`, required);
  `base` with `length`, `precision`, `scale` and `check` for a domain; `members` for an enum; `fields` (typed like
  parameters) for a composite; `subtype` for a range; `definition`, a dialect map of the SQL text after the type's name, which
  replaces the structured form for its dialect; `nativeName`; `comment`.
- `sql-object` (`objects/`, `SqlObject`, `sql-object.json`): `objectKind` (free text: trigger, grant, extension...);
  `phase` (`before|after`, default `after`); `dependsOn`; `body` (a dialect map, required), run as written.

A typed slot (parameter, result, result column, field) takes the shared definition `common.json#/$defs/dbTypeRef`: a built-in
keyword or the id of a database type of the same database. `dependsOn` is an `[ElementRef]` to tables, views, sequences,
routines, database types and SQL objects; the slot types and `Column.NativeType` are keyed references (`[ElementRef(Keyed =
true)]`, D43), so a database type id there is a reference the reverse index and deletes see, and any other text is not.

*Names.* `database-type` reads beside `scalar-type` and `reference-type` and keeps the kebab case of every multi-word kind; it
is a type the database owns, where a scalar type is a conceptual restriction that every database renders through its map.
`sql-object` says plainly that its content is SQL text the model does not type; "database object" would cover tables too. The
field that says which variety an element is cannot be `kind`, which every element file uses for its own kind, so it is
`routineKind`, `typeKind` and `objectKind`. The scopes and template variables follow the existing rule (spaces in the scope,
the kind with `-` as `_` as the variable): `each routine`, `each database type`, `each sql object`; `routine`,
`database_type`, `sql_object`.

*Resolution* (`DatabaseRun.Objects.cs`). Before the tables, each database resolves its database types, then its routines and
SQL objects, by id: `RDatabaseType`, `RRoutine` and `RSqlObject` (all `RAnnotated`, annotations from their own file), listed
on `RDatabase.Types`, `.Routines` and `.Objects` and on each `RSchema`, by (schema, name, id); a file without a schema takes
the database's default schema. A database type `IsCreated` when it has a definition for the dialect (or `"*"`), on
PostgreSQL for every kind, and on SQL Server for a domain with a base (an alias type); its `NativeName`, what a column or
parameter typed by it writes, is the file's `nativeName`, else its schema-qualified name when it is created, else the native
type of its base for a domain or of a string as long as the longest member for an enum, else empty (a composite or range on
SQLite). `BaseNativeType` and `SubtypeNativeType` render through the dialect map. A slot resolves to a built-in keyword
(facet defaults from the conventions, then the map), to a database type of the same database (`DbType` set, the native type
its `NativeName`) or to its own text, and an explicit `nativeType` wins. A routine's `Language` defaults to `plpgsql`,
`tsql`, `plsql` or `sql` by dialect; `Body` is the dialect's (or `"*"`) text with `HasBody` saying whether there was one. A
column whose file's `nativeType` names a database type of its database by id, by name or by schema-qualified name gets
`RColumn.DbType` and that type's `NativeName` (designed, overlay and extra columns alike; a type with no native name leaves the
column's computed type); a foreign key column that copies the key does not follow it. `DependsOn` holds the resolved objects
(a table named by its overlay file's id resolves to the synthesized table); a composite's `DependsOn` is the database types
its fields use. Dependency keys: each object's file, referrers, database, the type maps and conventions (types and
routines), the database types its slots use, and each `dependsOn` id; a column using a type adds the type to its table's keys;
the database lists add `k:routine`, `k:database-type` and `k:sql-object`.

*Validation* (`DatabaseObjectRules.cs`, `DatabaseObjectIndex`). MQ4017 (warning): a routine or SQL object without a body for
the database's dialect and no `"*"` one, or a database type with neither its structured form (a domain's base, an enum's
members, a composite's fields, a range's subtype) nor a definition for the dialect. MQ4018 (error): a slot type that is neither
built-in nor a database type of the same database. MQ4019 (error): a column's `nativeType` or a `dependsOn` entry naming an
object of another database (a missing id or a wrong kind stays MQ2001 or MQ2002). MQ4020 (error): every element of a cycle
over `dependsOn` and composite fields (Tarjan's components over the active documents), with the cycle's names. MQ3001 keys
names per kind, database and schema, case-insensitively, so routines are not overloaded and each object has one script. A
column whose native type names a database type skips MQ4006 and MQ4016. Changing one of the three kinds revisits the others of
its database and, for a database type, the tables whose columns write its id or name.

*Generation.* The planner scopes `each routine`, `each database type` and `each sql object` take the same `where` as `each
view` (tags, stereotypes, categories, the database, a script; packages and `abstract` refused at pack load) and the element's
own `generation.skip`; `get_template_context` lists their members. `sql_quote` takes a routine or database type. The snapshot
(`snapshot.json`) gains `types`, `routines` and `objects`, each a `SnapshotDefinition` (`key`, `name`, `schema`, `kind`,
`definition`: a canonical text of the definition for the dialect, without the name, so a rename is not a change); empty
lists are omitted, so existing snapshots are unchanged. `SchemaDiffResult.Types`, `.Routines` and `.Objects` list
`DefinitionChange`s (kind, key, old and new name, old and new kind, old schema, property changes `schema`, `kind`,
`definition`); the diff hash takes them only when one changed, so every existing hash is the same. sql-ddl writes them in this
order: SQL objects of phase `before`, database types, sequences, tables, routines and views (routines first, a routine after
a view its `dependsOn` names), SQL objects of phase `after`, each group in `dependsOn` order through `ddl_order`; migrations
drop and recreate changed routines, rename renamed types, flag changed types and SQL objects with a TODO, and drop removed
types last (packs/sql-ddl/README.md).

*API.* `DatabaseView` and `DatabaseRecord` gain `routines`, `types` and `objects` (`RoutineView` with `RoutineParameterView`,
`RoutineReturnsView` and `RoutineColumnView`, `DatabaseTypeView` with `DatabaseTypeFieldView`, `SqlObjectView`), `ColumnView`
gains `dbTypeId`, and the resolved model gains the scopes `routines`, `database-types` and `sql-objects` (one record each,
outside `all`, as `tables`). The OpenAPI contract declares the new members without making them required, so older clients and
the editor's recorded mocks stay valid; the server always sends them. The MCP tools that take a kind (`create_element`,
`get_schema`, `get_elements`) and the CLI's `model export` and `validate` take the kinds through the kind table.

**Queries (status: built 2026-10-02, erratum E41).** As built on 2026-10-02, a database holds a fourth kind beside its routines,
database types and SQL objects: the `query`, a query over the database's tables and views written as data. Tables, views and
routines are the physical model and generate the repository layer; entities are the service layer's shapes. An entity is
filled by its simple one-table mapping or by a query whose result shape it is, and which generates the repository method that
materialises it. A query is a JSON tree an agent edits and a pack walks per dialect, never SQL text; the only opaque SQL is the
`sql` expression, one expression per dialect. Entities and tables stay separable: tables without entities, entities without
tables, and many-to-many between them through queries.

- `query` (`queries/`, `Query`, `query.json`, scope `each query`, template variable `query`): the annotations, `name`,
  `database` and `source` a view has, but no `schema` (a query creates nothing in the database); `entity` (optional: the
  result shape; without it the select list is an ad hoc row); `parameters` (`name`, `type` as `common.json#/$defs/dbTypeRef`,
  `length`, `precision`, `scale`, `collection` for `in` lists, `default` as a JSON string, number or boolean, `description`);
  `from` (`source`, `alias`); `joins` (`source`, `alias`, `kind` `inner|left|right|full|cross`, default `inner`, `on`);
  `select` (required, at least one: `name`, `attribute`, `type` and `nullable` for an ad hoc row, `expression`; `name` or
  `attribute` required; `attribute` is an attribute id, `<attribute id>.<member id>` for a member of a value object
  attribute, or the id of the relation end a to-one navigation leads to, for its foreign key column); `where`; `groupBy`; `having`; `orderBy` (`expression`, `direction` `asc|desc`, `nulls`
  `first|last`); `distinct`; `paging` (`offset` and `limit`, each an integer parameter's name or a number); `collections`
  (`attribute`, `entity`, `query`: a nested query with `from`, `joins`, `select`, `where`, `groupBy`, `having`, `orderBy`,
  `distinct`, whose predicates may name the outer query's aliases).
- The trees are `$defs` of `query.json`, each one object whose `x-order` lists every member so the canonical writer orders
  them: an **expression** is exactly one of `column` (`alias.<column>`), `param`, `value` (string, number or boolean;
  with `type: "decimal"`, a decimal number written as text, `{ "value": "2.0", "type": "decimal" }`, so its digits survive
  an editor whose numbers drop them), `null` (`true`), `op` (`+ - * / % concat`) with `args`, `call` (a function name,
  `^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$`, or a routine id) with `args`, `case`
  (`[{ when, then }]`) with `else`, `cast` with `type` (a built-in keyword), `sql` (a dialect map); a **predicate** is
  exactly one of `and`, `or`, `not`, `op` (`eq ne lt le gt ge like ilike in notIn between isNull isNotNull`) with `left`
  and `right` (one expression, or a list for `in`, `notIn` and `between`), `exists` (a nested query). `oneOf` over
  `required` members picks the form and `dependentRequired` adds `args` to `op` and `type` to `cast`.
- Model records (`Model/Queries.cs`): `Query`, `QueryParameter`, `QuerySource`, `QueryJoin`, `QueryField`, `QueryOrder`,
  `QueryPaging`, `QueryCollection`, `QuerySubquery`, `QueryWhen`, `QueryExpression` and `QueryPredicate`, plain records so the
  reference walker and the indexer recurse through the trees. `QueryPredicate.Right` is a list read through a converter that
  takes one expression or an array (a list of one is written back as the expression). The references: `database`, `entity`
  and a collection's `entity` are typed; a source, a column, a call, a parameter type, a field's `attribute` and a
  collection's `attribute` are keyed references (`[ElementRef(Keyed = true)]`), so each id-shaped segment
  of `<entity id>@<database id>` or `alias.<attribute id>.<member id>` is a reference and any other text is not.

*Decisions where the brief left a choice.* The canonical column form is `alias.<column key>`, the key the database view lists
(an attribute id, a value object member's attribute path, `<end id>.<key attribute id>` for a foreign key, a designed
column's id): it survives renames and changes of the column conventions, and the reference index sees it, so a delete plan
follows it. A physical column name is accepted (the only form for a view's columns, since view columns have no ids) and
matches ordinally, then ignoring case, then ignoring case and underscores, when only one column matches; a name without an
alias resolves when exactly one source of the nearest query has it. A source is a table or view file's id (a designed table or
a synthesized table's overlay), a table key, or an entity id (its table in the query's database). The null literal is
`{ "null": true }`, because the canonical writer drops nulls; a negated `exists` is `{ "not": { "exists": ... } }` rather than
a `not` flag beside `exists`, which would give one key two shapes. `select` is required at the top level and optional in a
nested query (`exists` selects 1; a collection without fields is MQ4027). A collection's `attribute` is a collection
attribute of the query's entity, or the relation end a to-many navigation of the entity leads to (or the navigation's id),
or a name for an ad hoc collection; its element entity defaults to the navigation's target. A value object attribute spans
several columns, so MQ4026 does not ask for it.

*Resolution* (`DatabaseRun.Queries.cs`). After `FinishDatabaseObjects`, so tables, views, routines and database types are
complete, each query file of the database resolves by id into an `RQuery` (`RAnnotated`, annotations from its file) on
`RDatabase.Queries`, ordered by (name, id): `Entity`, `Parameters` (`RQueryParameter`: the slot resolution of routine
parameters, so a keyword or a database type with its native type, plus `CodeType`, the keyword a code type map maps: the
keyword, a domain's base, else `string`), `From` and `Joins` (`RQuerySource`: `Alias`, `JoinKind` `from|inner|left|right|full|
cross`, `Table` or `View`, `Name`, `Schema`, `On`, and `Optional` for the outer side of an outer join), `Select`
(`RQueryField`: `Name`, `Attribute`, `Member` (a value object member the field fills, `Attribute` then the value object
attribute), `Navigation` and `ForeignKeyColumn` (a to-one navigation whose foreign key column of the entity's table the field
fills), `Expression`, `Type`, `NativeType`, `CodeType`, `Nullable`), `Where`, `GroupBy`,
`Having`, `OrderBy` (`RQueryOrder`), `Distinct`, `Paging` (`RQueryPaging`: a parameter or a number per bound), `Collections`
(`RQueryCollection`: `Name`, `Attribute` or `Navigation`, `Entity`, the nested `Query`, `Keys`), `Sql` (the rendering for the
database's dialect with the default options, empty when the query has errors) and `Uses` (the tables, views, routines and
database types it reads, in first-use order). The trees resolve to `RQueryExpression` (`Node`: `column`, `param`, `value`,
`null`, `op`, `call`, `case`, `cast` or `sql`) and `RQueryPredicate` (`Node`: `and`, `or`, `not`, `compare`, `exists`; `Right`
for one operand, `Values` for the lists of `in` and `between`), plain objects templates walk as snake_case members. A nested
query (a collection's, an `exists`) is an `RQuery` with `Parent` set and its top query's parameters. Every expression
carries an inferred `Type`, `NativeType`, `CodeType` and `Nullable`: a column's (a reference column's code type; nullable on
the outer side of a join), a parameter's, a literal's (`string`, `bool`, `int32` or `int64`, `decimal`, `double`), a cast's
target, an operation's widest argument (`concat` is a string), a call's (`count` `int64`, `sum` widened, `avg` `decimal` or
`double`, `min`, `max`, `lower`, `upper` and `coalesce` their argument's, `length` `int32`, `now` `datetime`, a routine its
result, any other unknown), a case's first typed branch; a field's `type` and `nullable` override them. A field that fills an
attribute or a value object member without a `type` holds the attribute's type in generated code: a value of unknown type takes
the attribute's keyword, a number of another number type or a text of another text type converts (the pack casts it), and any
other is MQ4033. A cast on a MySQL database takes the type MySQL's `CAST` accepts as its native type. A collection's keys
are the equalities at the top of its nested `where` between a parent column and an expression of the nested query
(`RQueryKey`: `Outer`, `Inner`, `ParentField` (a select field over the same column, else the hidden `mq_key<c>_<k>`; names
starting with `mq_` are reserved for these and for generated code, MQ4040),
`Hidden`, `ChildField` `mq_key<k>`, `Parameter` `mq_keys<k>`). Dependency keys: the query file and its referrers, the database,
the type maps and conventions, the dependencies of every table and view it reads, each routine and database type it uses, the
result and collection entities (and their bases), a navigation's relation; the database lists add `k:query`. Queries do not
enter the schema snapshot, so the schema diff and its hash ignore them.

*Validation*. The rules on a query need the resolved columns, which only the resolver has, so the resolver reports them, with
the JSON pointer of the node, and `ModelStore` adds them to every validate path as it does MQ4005: MQ4021 (a source that is not
a table or view of the database, or a call of an id that is not one of its routines), MQ4022 (an alias declared twice, or a
column naming an alias no source declares), MQ4023 (an unknown column, or a name without an alias that no source or several
sources have), MQ4024 (an undeclared parameter, in an expression or the paging), MQ4025 (a field's attribute the entity does
not have, or an attribute where no entity is named), MQ4026 (warning: a required attribute of the entity no field fills),
MQ4027 (a collection that names neither a collection attribute nor a to-many navigation, or selects nothing), MQ4028 (a nested
query naming an alias no enclosing query declares, or a collection naming its parent outside an equality at the top of its
where, or not at all), MQ4029 (an `sql` expression without a text for the dialect nor `*`), MQ4030 (paging by a parameter that
is not `int16`, `int32` or `int64`), MQ4031 (a comparison's right side of the wrong shape), and as built on 2026-10-02 after
the review of the query rounds: MQ4032 (a distinct parent that neither selects nor groups by a collection's key column, which
a hidden column would add to what DISTINCT compares), MQ4033 (warning: a field whose value cannot convert to its attribute's
type), MQ4034 (a call naming neither an identifier, optionally schema-qualified, nor a routine id: the name reaches SQL as
written), MQ4035 (a join other than cross without `on`, or a cross join with one), MQ4036 (a full join on MySQL), MQ4037
(warning: a right or full join on SQLite, which runs them from 3.39), MQ4038 (a distinct query ordering by an expression its
select list, or a collection's key columns, does not have, or by `nulls` on SQL Server and MySQL, whose emulation adds a
term), MQ4039 (a list parameter used other than as the whole right side of `in` or `notIn`), MQ4040 (names that collide in
generated code: two collections, or fields of an ad hoc row, equal once Pascal-cased, a collection named `Item` after its
result record's row, a name starting with `mq_`, two queries of a database whose class names, `QueryClassName`: Pascal-cased
with `Q` before a leading digit, are equal), MQ4041 (an abstract result or element entity), MQ4042 (an operation with fewer
than two operands; `-` takes one) and MQ4043 (a number literal or default no double holds, a typed literal that is not a
decimal number, a default that is not a value of its parameter's type). Names, unselected attributes, conversions, abstract
entities and MQ4037 leave the query rendered; every other finding leaves it without SQL. A parameter type that is neither
built-in nor a database type of the database is MQ4018, and two parameters with one name ignoring case or two fields with one
name are MQ3001, from the resolver; the validator keys query names per database case-insensitively (MQ3001), since a query
becomes a class named after it.

*Rendering* (`Rendering/QuerySql.cs`, public). `QuerySql.Render(RQuery, string? dialect, QuerySqlOptions?)` and
`QuerySql.RenderCollection(RQueryCollection, ...)` return a `QuerySqlText(Sql, Parameters, Diagnostics)`: one clause per
line, identifiers quoted by the database's quoting through `SqlDialects`, tables schema-qualified (not on SQLite), table
aliases without `AS`, fields as `<expression> AS <name>` (a name starting with `_` is quoted, since Oracle's unquoted
identifiers start with a letter). `QuerySqlOptions.Placeholder` is `@` (`@name`, the default), `:`
or `$` (`$1`, numbered by first appearance; `Parameters` lists the names in that order); `Lists` is `expand` (`x IN @ids`,
for a data access library that expands a list into its items) or `any` (`x = ANY(@ids)` with an array parameter on
PostgreSQL; other dialects expand). Per dialect: `length` is `LEN` on SQL Server, `CHAR_LENGTH` on MySQL, `LENGTH`
elsewhere; `now` is `CURRENT_TIMESTAMP`; `count` without arguments is `COUNT(*)`; any other name is written as given and a
routine as its quoted, qualified name; `concat` is `||`, or `CONCAT(...)` on SQL Server and MySQL; `%` is `MOD` on Oracle;
`ilike` is `ILIKE` on PostgreSQL and `LOWER(a) LIKE LOWER(b)` elsewhere; `NULLS FIRST|LAST` is written on PostgreSQL, Oracle
and SQLite and emulated with a leading `CASE WHEN x IS NULL` term on SQL Server and MySQL; paging is `LIMIT … OFFSET …`
(SQLite `LIMIT -1` and MySQL `LIMIT 18446744073709551615` when only an offset is given), `OFFSET … ROWS FETCH NEXT … ROWS
ONLY` on SQL Server (with `ORDER BY (SELECT NULL)` when the query has no order) and Oracle; a cast takes the native type of
its keyword through the dialect's effective type map, on MySQL the type its `CAST` accepts (`QuerySql.MySqlCastType`:
`CHAR` for the texts, uuid and ulid, `SIGNED` for the integers, bool and duration, `BINARY`, `DECIMAL(p,s)`, `DATE`,
`DATETIME` for both datetimes, `TIME`, `JSON`, `DOUBLE` for float and double); literals go through `SqlDialects.Literal`,
a typed decimal literal as its text without leading zeros; an `exists` is `EXISTS (SELECT 1` followed by its clauses on
lines of their own, indented four spaces deeper per level, correlated by its aliases (the text of literals and `sql`
expressions is never touched). A grouped statement (a `groupBy`, or an aggregate call, `count`, `sum`, `min`, `max`,
`avg` or any other name the renderer knows as an aggregate, in the select list, having or order) adds its key columns to
its GROUP BY: a collection its key expressions, a parent its hidden key columns. An `sql` expression without a text for
the dialect (a blank text is none) renders `NULL` and returns MQ4029. The template helpers are `query_sql(query, dialect?,
options?)`, `query_collection_sql(collection, dialect?, options?)` (`options` an object with `placeholder` and `lists`) and
`query_sql_parameters(query or collection, dialect?, options?)` (the options may stand in the dialect's place), the
parameter names in placeholder order (`QuerySql.Parameters`, the order of `$n`); they record the top query's dependencies
and fail the unit with the renderer's diagnostic.

*Collections.* The portable form is the default and the only one built: the parent's statement carries each key's parent
column (a hidden column when no field holds it), and each collection is a statement of its own, run once for all parent rows
(a second round trip): its select list plus each key's inner expression as `mq_key<k>`, its where without the key equalities
plus `<inner> IN @mq_keys<k>`, its grouping (with its keys, when grouped) and order. The repository groups the rows by their
keys and attaches them to the parent whose key values match; with several keys the `IN` lists over-fetch and the full tuple
decides. Generated code returns the collections beside the row, never on the entity: csharp-dapper's `<Name>Result` holds the
row (`Item`) and one list per collection, and the entity's collection attribute or navigation is left as the entity class
leaves it. csharp-dapper runs a collection's statement for at most 1000 parent keys at a time and merges the chunks in order. The JSON aggregation
form (`json_agg`, `JSON_ARRAYAGG`, `FOR JSON`) is not built: the portable form is one path that every dialect and every data
access layer runs, with typed rows instead of JSON to parse; aggregation is left for a later round.

*Deletes.* A query refers to its database, its entity, its sources, its fields' attributes and the ids in its column keys
and calls. A plain delete of one of them is refused with the query among the referrers; `remove-references` clears an
optional reference and refuses a required one; `delete-dependents` deletes the query whole (`ChangePlanner.Cascade`: a query
is never trimmed, since its aliases tie its parts together and a removed join or field would leave references to it).

*API.* `DatabaseView` and `DatabaseRecord` gain `Queries` (`QueryView`: id, name, `entityId`, `parameters`
(`QueryParameterView`), `from` and `joins` (`QuerySourceView`), `select` (`QueryFieldView` with the expression as written and
the inferred type), `where`, `groupBy`, `having`, `orderBy` and `paging` as the file writes them, `distinct`, `collections`
(`QueryCollectionView`: the nested query as written, its resolved fields, `keys` as `QueryKeyView`, its `sql`), `sql`,
`sqlParameters`, `uses`, the annotations). The resolved model gains the scope `queries` (`QueryRecord`, outside `all`).
`GenerationService.GetQuerySqlAsync(queryId, dialect, options, ct)` validates and resolves like `GetDatabaseViewAsync` and
returns `QuerySqlResult(Preview, Diagnostics)`, the preview (`QuerySqlPreview`: the statement and one `QueryCollectionSql` per
collection) null on a model with errors or an unknown id (MQ6017); an unknown dialect is an `ArgumentException`. The functions
answer `GET /api/model/queries/{id}/sql?dialect=&placeholder=&lists=` (404 `not-a-query` for another kind), the MCP server
`preview_query_sql`.

*Generation.* `each query` takes the `where` of `each view` (tags, stereotypes, categories, the database, a script; packages
and `abstract` refused at pack load) and the query's `generation.skip`. csharp-dapper's `query` unit writes
`Queries/<Name>Query.g.cs` per query (an interface and a class with one `ExecuteAsync(<parameters>, CancellationToken)`
returning the entity, a `<Name>Row` record, or `<Name>Result` with the row and the collections; the class name is
`QueryClassName`'s, the template's own names start with `mq_`, entity and value types are written `global::`-qualified, and
the class's remarks say its entity rows are read-only projections) and its `registrations` unit
`Queries/QueryRegistrations.g.cs`; sql-ddl has nothing to write for a query.

**Bindings and materialize (status: built 2026-10-02, erratum E43).** As built on 2026-10-02, after the owner's correction
("The database doesn't know about entities. It only knows about itself and how to store data. Entities track their own mapping
... Bindings should be explicit. An entity is a query + a map, an insert or update with a map, a delete on a PK."), an entity owns
its **bindings**: how it reads from and writes to a database, written down whole in its own file. The database holds tables,
views, routines, types and queries and nothing about entities; a table lists the bindings that use it (`BoundBy`) the way a
column lists its referrers, as a fact of the model, not of the table. Projection (conventions placing an entity and synthesizing
its table) keeps working for models that do not bind; for new work it is superseded by **materialize**, which writes a designed
table and a binding once, after which both are the user's.

*Model* (`Model/Bindings.cs`, `entity.json` `bindings`, x-order after `attributes`). `EntityBinding { req Id; req Database
(→ Database); req Source (keyed: a table file id, a synthesized table key such as <entityId>@<databaseId>, a view id or a query
id of that database); Constants (BindingConstant { Column (keyed); JsonElement? Value }, a JSON string, number or boolean,
absent or null meaning SQL NULL, since the canonical writer drops nulls); Fields (BindingField { Attribute (keyed: an attribute
id, own or inherited or virtual, attributeId.memberId, or a relation end id for a to-one navigation's key); Column (keyed: a
column key or physical name, the rules of query column references) }); Columns (BindingColumn { Column; Status ignored|database|
computed }); Write (BindingWrite { Table (keyed) } | "none", through a property converter; null = the source when it is a table,
else none); Delete (BindingDelete: "key" | { "soft": { column, value } } | "none"; null = key when the binding writes, else none);
Description; Tags; Properties }`. A binding is a sub-element (index kind `binding`, unique id); it carries description, tags and
properties (not the whole `ElementBase`, whose `source` member would collide with the binding's). Every id it holds is a
reference the indexer records, so renames never break a binding.

*Placement.* `DatabaseRun.CollectBindings` runs first: each entity's first binding to the database is kept (a second one is
MQ4050). An entity with a binding is never placed by convention or by a mapping element (a mapping element for it and the database
is MQ4054, info, and ignored); when the binding writes, or reads, a designed or imported table, the entity gets a placement with
`Bound` and `ViaBinding` set and that table (the write table, else the source), so relations resolve as for `Mapping.Table`: a
bound dependent uses the foreign key its relation mapping names (`foreignKey`), a projected dependent of a bound principal
references the principal's table, and a relation between bound entities with no foreign key named is MQ4011. Such a placement
makes no entity mapping (`REntity.Mappings` has no entry for the database), sets no `RTable.Entity` and binds no column to an
attribute. Old models without bindings resolve byte for byte (the golden tests).

*Resolution* (`DatabaseRun.Bindings.cs`, `ResolvedBindings.cs`), after the queries: `REntityBinding : RAnnotated` (kind
`binding`; `Entity`, `Database`, `SourceKind` table|view|query, `SourceTable`|`SourceView`|`SourceQuery`, `SourceName`,
`Constants` (`RBindingConstant`: `Column`, `ColumnName`, the source column, `WriteColumn`, `Value`), `Fields` (`RBindingField`:
`Name` (the attribute's name, `attributeMember` for a member, the navigation's name plus `Id` for an end), `Attribute`, `Member`,
`End`, `Navigation`, the source column, `WriteColumn`, `IsKey`, `IsGenerated`, `InInsert`, `InUpdate`, `Type`, `NativeType`,
`Nullable`), `Columns` and `WriteColumns` (`RBindingColumn`: `Name`, `Column`, `Status` field|constant|ignored|database|computed|
identity|default|soft-delete|unaccounted, `Field`, `Constant`), `Writes`, `WriteTable`, `Delete` key|soft|none,
`SoftDeleteColumn`, `SoftDeleteValue`, `Key`, `Generated`) on `REntity.Bindings` by database name, and `RTable.BoundBy`,
`RView.BoundBy`, `RQuery.BoundBy` (by entity name, id). Columns are found by name, key, id or attribute id, then loosely by
name (as queries do); a write table that is not the source matches each field's column by key, then by name. A field is
generated (never inserted or updated) when its column is an identity, a key sequence or computed, or the binding lists it as
`database` or `computed`; it is updated unless it is a key or its attribute is read-only or immutable. Dependency keys: the
binding lists the database, the entity and its bases, every table, view and query it touches (their own keys) and the value
objects and relations its fields read; the tables, views and queries a binding names list the binding entity's file (collected
before they are created, so `BoundBy` is tracked).

*Rules* (resolver findings, with the JSON pointer of the binding entry, added to every validate path like the query rules):
MQ4044 (E) a source that is not a table, view or query of the database, or a write table that is not a table of it; MQ4045 (E) a
field attribute that is no attribute, member or to-one end of the entity, a column the source does not have, a listed or
soft-delete column of no table the binding uses, or an attribute or column mapped twice; MQ4046 (E) a writing binding missing a
key attribute; MQ4047 (W) a source or write-table column nothing accounts for (no field, constant or listed status, and not an
identity, computed or defaulted column), once per table with the names; MQ4048 (E) a constant column the source does not have;
MQ4049 (E) a writing binding whose constant column is not in the write table; MQ4050 (E) two bindings to one database; MQ4051 (W)
a constant or soft-delete value that does not fit its column (type, length, integer range, NULL into NOT NULL); MQ4052 (E) a write
table other than the source without a column for a key field; MQ4053 (E) a key delete from a table without a primary key, or a
delete without a write table; MQ4054 (I) an entity mapping element ignored because of a binding; MQ4055 (E) a materialize
refusal. MQ4012 counts a binding as placing the entity.

*SQL* (`Rendering/BindingSql.cs`, public, beside `QuerySql` and sharing its quoting, literals, placeholder styles and
dialects): `BindingSql.Render(binding, statement, dialect?, options?)` returns a `QuerySqlText` for `select` (the fields' columns
aliased to the field names, `FROM <table> t`, `WHERE` the constants and, with a soft delete on the source, `(col IS NULL OR col
<> value)`; a query source is `FROM (<the query's SQL without its order, unless paged>) q`), `select-by-key`, `insert` (written
fields and constants as literals; generated keys come back through `RETURNING col AS field` on PostgreSQL and SQLite, `OUTPUT
INSERTED.col AS field` on SQL Server, `RETURNING col INTO :field` on Oracle and a second statement `SELECT LAST_INSERT_ID()` on
MySQL for one identity key; no column at all is `DEFAULT VALUES`), `update` (by the key fields' write columns, constants in the
where clause; with nothing to set, `SELECT COUNT(*)` of the matching rows) and `delete` (by key plus constants, or the soft-delete
`UPDATE`). A statement the binding does not have is the empty string. Parameters are named after the fields; with `$` they are
numbered after a query source's own parameters. The template helpers `binding_sql(binding, statement, dialect?, options?)` and
`binding_sql_parameters(...)` are registered like `query_sql` (they record the binding's dependencies and fail the unit with the
renderer's diagnostic). `RQuery.ForDerivedTable()` (internal) drops the order of an unpaged query, which SQL Server refuses in a
derived table.

*Materialize* (`Loading/Materializer.cs`, `ModelStore.Materialize.cs`). Two batch operations, applied with the batch's other
operations all or nothing like E26's schema operations, with a preview (`ModelStore.PlanMaterializeAsync`, which plans and
validates the change and resolves the result for the binding rules, writing nothing) and a status read
(`ModelStore.GetMaterializeStatusAsync`): `materialize-tables { database, entities, schema? }` resolves the model with every picked
entity projected into the database (a mapping element added in memory, an ignoring one made placing) and writes, per entity, a
designed table with the projected table's exact shape: name, schema (the request's, else the overlay's, else the projected one),
columns (type keyword (a reference column's code type), facets, native type when the overlay set one or it differs from the type
map's, nullability, default, `defaultSql`, identity or sequence (a key sequence the projection synthesized becomes a sequence
file), computed, collation, comment as resolved), the primary key, uniques, checks, indexes and foreign keys with their resolved
names (a key to a table another entity still projects names that table's key until it is materialized too), and the overlay's
annotations. When the entity had an overlay, the overlay file becomes the designed table and keeps its id and its column,
constraint and index ids, so queries and keys naming it keep working; the brief's "deleted" is read as "replaced". The entity
gets a binding with every column mapped (a column no attribute can hold is listed `ignored`, with a note); its mapping element for
the database is deleted; the relations whose foreign key is now in a designed table get a relation mapping naming it (created, or
updated with `shape` removed). What named the projected table by its key follows: the database's queries (sources and
`alias.<column key>` references, found through the resolved trees), other tables' foreign keys and other entities' bindings. After
the batch is saved, the committed snapshot of each database records the keys of each stored table as an alias (`aliases`: the
projected key, the file id, and each column's key before and after; a database without a snapshot gets an alias-only one, read as
none), and nothing is rekeyed. The schema diff reads the snapshot through its aliases (`Generation.SnapshotAliases.Normalize`,
which renames with `SchemaDiff.SnapshotRekey` the table and column keys and the constraint and index keys derived from them, in the
table and in the foreign keys of other tables, in whichever direction the model's keys call for), so the next diff sees the same
tables and writes no migration, after the materialize and after an undo that puts the projection back or a redo that stores it
again; a saved snapshot keeps the aliases. Storing a key again after an undo (a new `materialize-tables`, not a redo) reuses the
ids its alias recorded whenever no element holds them now: the table id (unless the entity's overlay supplies one), each column id
from the alias's `columns` (unless an overlay column supplies it) and a key sequence's file id, so the store yields the same ids,
the alias does not change, and the next diff holds only what changed in between (a column added and undone is one `DROP COLUMN`).
Recording an alias for the same file again keeps the column ids it recorded for columns the table no longer has. The batch operation takes optional `expectedHashes` (id to the hash the caller read):
given, an update or delete of a document that changed since, or that the map does not name, is a conflict, so an undo built from
what the caller read never restores a stale version. Refused (MQ4055): an entity already bound to the database (the idempotence check), an
abstract entity, an entity in an inheritance hierarchy (left to hand binding this round), an entity without a table of its own.
`materialize-entities { database, tables, package }` writes, per designed or imported table or view of the database, an entity in
the package named the singular Pascal case of the table name (the project's inflection), one attribute per column (camel case
name; the column's type keyword, else the dialect map reversed from the native type, else `string`; length, precision and scale;
`required` when not nullable), the key from the primary key (`database-identity` for one identity column; a view takes its `id`
column, else its first, with a note), and a binding to the table; each foreign key between the picked tables, or towards a table an
entity is already bound to, becomes a many-to-one relation (the principal end navigable from the dependent, `min` 1 when the key
columns are not nullable, `onDelete` from the key) with a relation mapping naming the foreign key. Refused: a table an entity is
already bound to, a synthesized table, a name the package already has. New ids come from the store's id generator.
`materialize-attributes { database, entities, columns? }` (2026-10-07, the owner: an entity made by hand, bound to a table, takes
the table's columns as attributes) adds, per entity bound to the database, an attribute per column of its binding's source, made
by the rule `materialize-entities` uses (the name unique among the entity's attributes, own and inherited, with a number after it
when taken), and a field mapping it to the column; an `ignored` listing of the column goes, `database` and `computed` stay (they
sit beside a field). Without `columns`, the columns nothing in the binding names (status `unaccounted`, `identity` or `default`);
with them (keys or physical names, one entity only), those columns whatever their listing. The key, the constants and the write
and delete plans stay as they are. Refused (MQ4055): an entity with no binding to the database, a source that does not resolve or
is a query, a column a field, a constant or the soft delete uses, a column the source does not have, `columns` with several
entities, and nothing left to add. The entity's file is the only change (one update, one undo step).
`materialize-columns { database, entities, attributes? }` (2026-10-07, the other direction, the owner: "create a new table column
for that field") adds, per entity bound to the database whose source is a designed or imported table it writes, a column of that
table per attribute the binding leaves unmapped, and a field mapping it. The column's shape is the one the entity's projection
gives it: the materializer resolves the model with the entity unbound and projected into the database (as `materialize-tables`
does), finds the projected column whose attribute path is the field's target (`FieldTarget`: an attribute, a value object member,
a to-one end), and writes it with `ColumnNode`, the code `materialize-tables` writes its columns with (names and types by the
project's conventions; an enum as the project stores enums). Without `attributes`, every target a projected column holds that no
field maps; with them (field references, one entity only), those. A projected column whose name the table has already is left out
with a note (the user maps it), and no foreign key is written for a to-one end's column (a note says so). Refused (MQ4055): no
binding, a source that is not a table file of the database, a binding that writes another table, a target already mapped or no
projected column holds, a name the table has (when named), `attributes` with several entities, nothing left to add. Two documents
change, the table and the entity (one batch, one undo step).

*Deletes.* A binding's references are ordinary references: deleting a table, view, query or database a binding names is refused
with the entity among the referrers. `remove-references` and `delete-dependents` both drop the binding (a field, constant or
listed column entry only, when the reference sits in one) and never delete the entity; the plan lists it as a removed part
("the binding needs table notes; the entity stays").

*API, MCP, CLI.* `DatabaseView` tables, views and queries carry `boundBy` (`BoundByView`: entity, binding, reads, writes,
constants); the resolved records' `EntityRecord.Bindings` (`EntityBindingRecord` with `BindingFieldRecord` and
`BindingColumnRecord`); the scope `entities` with a database takes bound entities too. `GET /api/model/entities/{id}/bindings/
{bindingId}/sql?dialect=&placeholder=` (`GenerationService.GetBindingSqlAsync`, `BindingSqlResult`/`BindingSqlPreview`, the five
statements or null), `GET /api/model/databases/{id}/materialize` (`MaterializeStatus`) and `POST /api/model/databases/{id}/
materialize/preview` (the batch operation without its database; `MaterializePlan`); `POST /api/model/batch` takes the two
operations (`BatchOperation` gains `Database`, `Entities`, `Tables`, `Package`; `BatchOp` gains `MaterializeTables` and
`MaterializeEntities`). MCP: `preview_binding_sql`, `get_materialize_status`, `preview_materialize`, and `apply_batch`. CLI:
`maquettiste model materialize tables --database <name> [--schema <name>] <entity...>` and `... entities --database <name>
--package <name> <table...>`, each with `--dry-run` and `--format text|json`. The editor's types are regenerated; its mock treats
bindings as data and only stops projecting a bound entity (no binding resolution in the mock this round); the editor screens come
next round.

*Generation.* csharp-dapper: `dapper_binding(e)` picks the binding of the pack's `database` parameter, else of the first database
(by name) that binds or maps the entity; when it binds, `repository.scriban` includes `_binding.scriban`: `GetAsync` and
`ListAsync` (key order, `mq_skip`/`mq_take` paging; a query source's parameters first) from `binding_sql` select statements, and,
when the binding writes, `InsertAsync` (uuid-v7 and ulid keys assigned as before, one generated key read back), `UpdateAsync` and
`DeleteAsync` from its insert, update and delete; a binding that does not write gets a read-only repository. Its private `Row`
class has one property per field, named exactly as the field (the select's alias and the statements' parameter), so every provider
matches parameters by their exact name. Entities without bindings generate byte for byte as before. sql-ddl writes designed tables
as it always did (a bound entity has no projected table). sql-ddl's `schema` and `migration` units now handle a single table (a
database with one table, a migration adding one): the foreign-key order of one table is an empty spec, which `ddl_order` reads as
no tables, so they wrote no CREATE TABLE; a bug older than bindings that the reference application's new `remarks` table showed.

*Decisions where the brief left a choice.* The binding is a plain sub-element record rather than an `ElementBase` (the `source`
name). The overlay of a materialized entity becomes the designed table under the same id rather than being deleted, so every
reference to it survives; the snapshot records aliases so materialize (and its undo) is not a drop and a create. Foreign keys to still-projected
tables are kept, by key, so the relation keeps its foreign key (else MQ4011); materializing the referenced entity later points
them at its designed table. Constants are SQL literals in the statements, not parameters. Derived tables are aliased `q` without
`AS` (Oracle refuses it; `QuerySql` writes no `AS` either). A soft delete also filters reads. A column marked `database` or
`computed` that a field maps is read but never written. Inheritance hierarchies are refused by materialize-tables this round.

## 8. Template packs (W6 loads and plans; W5 renders)

- **Discovery.** Every `templates/<name>/pack.json` whose `name` equals its folder is a pack; `packs.<name>.enabled: false` turns it off. Packs run in ordinal name order. `types/<target>.json` files are type maps for `type_of` (keyword → language type, plus `"nullable": "{type}?"` and `"collection": "IReadOnlyList<{type}>"` patterns). Built-in dialect targets need no file.
- **`for`**: `model` (one unit, no element), `each package|entity|relation|enum|value object|table|view|sequence|routine|database type|sql object|query|reference type|seed|locale|process|actor|scenario` (one unit per resolved element; `table`, `view`, `sequence`, `routine`, `database type`, `sql object` and `query` cover every database (§7.0a, "Routines, database types and SQL objects" and "Queries" in §7); the unit key of `each reference type`, `each seed`, `each process`, `each actor` and `each scenario` is the element id, and the scope alias is `reference_type`, `seed`, `process`, `actor` or `scenario`; `where` on the three phase 3 scopes takes tags, stereotypes, categories and packages (a scenario's package is its process's, an actor has none, so a package filter matches no actor) and refuses `database` and `abstract` at pack load (MQ6001); `each locale` plans one unit per declared locale, the default first then ordinal, with the `RLocale` as `element` and `locale`, unit key `locale:<tag>`, and rejects `where` at pack load), or `select <name>` (a JavaScript selector that returns elements or ids; unknown ids fail with MQ6017). `generation["*"|pack].skip` on an element drops its units. `where` filters as in §2.5; `where.database` also picks `mapping` for entity and relation units.
- **Template context.** Variables: `model`, `element`, a scope alias (`package`, `entity`, `relation`, `enum`, `value_object`, `table`, `view`, `sequence`, `routine`, `database_type`, `sql_object`, `query`, `reference_type`, `seed`, `locale`, `process`, `actor`, `scenario`), `pack` (`name`, `version`, `params`), `project` (`name`, `properties`: the project's own key → text values from `maquettiste.json`; reading `project` records `s:project`, the hash of the name and the properties, so editing a property re-renders only the units that read it), `mapping` (`REntityMapping`/`RRelationMapping` for `where.database`, else the only one, else null), `mappings` (by database name), `schema_diff` (database name → `SchemaDiffResult`), `hints` (merged `generation["*"]` and `generation[pack]`), `data` (transform results), `unit` (`id`, `key`).
- **Output.** `Output` is rendered with the same context (tracked like the body) and prefixed with `PackSettings.Output`. A template emits more files with `{{ file "path" content }}`, usually after `{{ capture content }}…{{ end }}` (D10). With `Output` null, only file blocks are written. Block paths take the same prefix and the unit's mode, except `pair`, whose blocks are `overwrite`.
- **Modes.** `overwrite`, `once` (written only when missing; recorded as owned), `regions` (committed roots only; MQ6015), `pair` (`Output` rendered every time with `Template`; `Companion.Template` rendered to `Companion.Output` only when that file is missing, as owned). As built on 2026-10-02 (spec-errata E42): `regions` works on any root (MQ6015 is retired), and `block` manages one delimited block of lines inside a file the team owns (§12.3b); a block unit's file blocks are blocks too, and formatters never run on them.
- **Built versus committed.** A file's root is the longest `outputs.allow` path that contains it. `Commit` decides the manifest location (§12.2), `--check` coverage, the roots `init` names as built, and the `.gitignore` entries `init --gitignore` writes when asked (never by default; spec-errata E39). As built on 2026-10-02 (spec-errata E42), this distinction is gone: an allow entry is only a path, a folder generation may write under or the file of that exact name (`.gitignore`, `src/App/.gitignore`; an entry ending in `/` is a folder only), every pack has one manifest (§12.2), `--check` covers every root, and `init` has no `.gitignore` logic (`--gitignore` is refused). A `commit` member left in `maquettiste.json` is dropped before the schema check and reported once per entry (MQ1010, info); `maquettiste format` and every settings write drop it.

## 9. Rendering (W5)

- **Template cache**: `internal interface ITemplateCache { Template Get(LoadedPack pack, string path, Delimiters? delimiters); }` returns a parsed Scriban `Template`, keyed by (pack, path, file hash, delimiters), in a `ConcurrentDictionary` owned by the renderer instance for one run. Renderers are per run: `EngineServices.CreateRenderer()` builds one with a fresh `TemplateCache` for each run and each preview, so nothing accumulates in a long-lived host. Parse errors → MQ6003 with line and column.
- **Delimiters**: Scriban 7.5 has no custom delimiter option, so `DelimiterTranslator` rewrites a template that uses `Open`/`Close` into `{{ }}` form before parsing. Code spans become `{{…}}`, and a text span containing `{{` or `}}` is wrapped in an escape block `{%{…}%}` with enough `%` that the text cannot close it. Line breaks are kept one for one, so error positions map back unchanged.
- **Context** per unit: a fresh `TemplateContext` with `StrictVariables = true`, `EnableRelaxedMemberAccess = true`, `EnableRelaxedTargetAccess = false`, `LoopLimit` and `RecursiveLimit` from `SandboxLimits`, `NewLine = "\n"`, invariant culture, `MemberRenamer` = snake_case, and a `MemberFilter` that admits only public get-only properties of resolved-model types and helper result types. No `ScriptObject.Import` of arbitrary CLR types. `include` resolves through a `TemplateLoader` confined to the pack folder, which records `t:<pack>/<path>`. Builtins `date.now`, `math.random`, `object.eval` and `object.eval_template` are replaced by functions that fail with MQ6012.
- **Tracking proxy**: `TrackingTemplateContext` overrides `GetMemberAccessorImpl`. For any `IResolvedObject`, the accessor records the object's `Dependencies` into the unit's `IReadRecorder` on every member read. Every list of resolved objects is an `RList<T>`; enumerating it, indexing it or reading `size` records its `MembershipKeys` (the kind-set and element keys that decide which items it holds, set by the resolver: `model.entities` → `k:entity`; `database.tables` → `k:entity`, `k:relation`, `k:enum`, `k:table`, `k:mapping`; and so on). `lookup` of a missing id records `e:<id>`, so creating that element later re-renders the unit. Helpers receive the same recorder. Reference data (reference-types-seeds-localization.md §1.5, §2.5): a row (`RRow`, `RSeedRow`) records its seed's keys (the seed's `e:` key and its target's); `reference_type.rows` records `k:seed`, `r:<type id>` and the `e:` key of each of the type's seeds, so editing one seed re-renders only the units that read that seed's rows; a storage choice (`RStorageChoice`) records `s:referenceData`, the type's `e:` key and the database's; `row <type> "<code>"` records the type, its rows' membership and the row; `row_uuid <row>` returns the row's ULID bits as a UUID. Localization helpers (reference-types-seeds-localization.md section 3.7): `display_name`, `plural_name`, `description_of`, `label_of` `<x> [locale]` and `translate <x> "<field>" [locale]` walk the locale's chain (`RLocale.chain`: the locale, its `fallbacks` or supported truncations, the default) down to the default-locale value; without a locale argument they use the unit's `locale` in an `each locale` unit, else the default; `has_translation <x> "<field>" <locale>` reads one locale. `model.locales` lists the `RLocale`s.
- **Helpers** (`BuiltinHelpers`, all pure): `pascal camel snake kebab upper_snake` (split on non-alphanumerics, lower→upper and acronym→word boundaries; digits join the preceding word; words lowercased, then styled: `HTTPServer2Id` → `http_server2_id`); `pluralize singularize` (fixed English rules, `inflection` overrides, element `pluralName` wins); `type_of <attr|column|type> "<target>"` (pack type map or dialect); `sql_quote <name> "<dialect>"` (pg/sqlite/oracle `"x"`, sqlserver `[x]`, mysql `` `x` ``, by the database's `Quoting` and embedded reserved-word lists); `sql_literal <value> "<dialect>"` (strings single-quoted with `''`, sqlserver `N'…'`; booleans `true/false` on pg, `1/0` elsewhere; dates ISO 8601 quoted; null `NULL`); `indent <text> <n|string>`, `dedent`; `escape_md escape_xml escape_json`; `json <value>` (canonical, compact); `has_stereotype has_tag in_category`; `lookup <id>`; `banner "<comment prefix>"` → `<prefix> Generated by Maquettiste (<pack>/<unit id>). Do not edit; changes are overwritten.` (no timestamp, no version); `file <path> <content>`; `state_path <state>` (a process state's dotted path, `Fulfilment.Shipping.Packed`; a state id works too; records the state); `iso_duration_ms <text>` (the milliseconds of an ISO 8601 duration with the interpreter's fixed spans, a month 30 days and a year 365; a text that does not parse fails the unit with MQ6006 naming it); `query_sql`, `query_collection_sql` and `query_sql_parameters` (a query's or a collection's statement, and its parameter names in placeholder order; section 7, "Queries"). Pack helpers from JavaScript register under their own names and fail with MQ6013 on a collision.
- **Output normalization**: rendered text is kept as a string; post-processing (§13) encodes it.

## 10. Script sandbox (W4)

```csharp
namespace Maquettiste.Engine.Scripting;
public interface IScriptSandboxFactory { IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, SandboxLimits limits, int size, CancellationToken ct); }
public interface IScriptSandboxPool : IDisposable { IReadOnlyList<ScriptRegistration> Registrations { get; } IScriptSandboxLease Rent(); }
public interface IScriptSandboxLease : IDisposable { IScriptSandbox Sandbox { get; } }
public sealed record ScriptRegistration(ScriptRegistrationKind Kind, string Name, string DeclaredIn);
public enum ScriptRegistrationKind { Helper, Selector, Filter, Transform, Rule }
public sealed record ScriptCallContext(IReadRecorder? Reads, string Seed, IReadOnlyDictionary<string, object?> Parameters, CancellationToken CancellationToken);
public interface IScriptSandbox
{
    object? CallHelper(string name, IReadOnlyList<object?> args, ScriptCallContext ctx);
    IReadOnlyList<string> Select(string name, ResolvedModel model, ScriptCallContext ctx);           // element ids
    bool Filter(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx);
    IReadOnlyDictionary<string, object?> Transform(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx);
    IReadOnlyList<Diagnostic> RunRule(string ruleId, ElementDocument element, ModelSnapshot model, ScriptCallContext ctx);
}
public sealed class ScriptLimitException : Exception { public Diagnostic Diagnostic { get; } }   // MQ6007 / MQ5003
```

- **Engine options** (Jint 4.16.4): `Strict()`, `Interop.Enabled = false`, `AllowGetType = false`, `AllowSystemReflection = false`, `DisableStringCompilation()` (no `eval`, no `Function`), no modules, `Culture(CultureInfo.InvariantCulture)`, `LocalTimeZone(TimeZoneInfo.Utc)`, `TimeSystem` = a fixed clock at 2000-01-01T00:00:00Z (so `Date.now()` and `new Date()` are constant), `LimitRecursion`, `LimitMemory`, `TimeoutInterval`, `MaxStatements` from `SandboxLimits`, and `Options.CancellationToken(ct)` with the pool's run token, with `engine.Constraints.Reset()` before every top-level call. Pools live for one run (or one validation), so the run token fits; every call also checks `ScriptCallContext.CancellationToken` before it starts. A running script therefore stops within the one-second cancellation budget (host-contracts 26) although the script timeout (2 s by default) is longer. `Math.random` is replaced by xorshift128+ seeded from `ScriptCallContext.Seed` (the unit key or element id) at every top-level call.
- **Registration**: each engine runs the pool's prepared scripts (`Engine.PrepareScript`, prepared once per pool) against a global `maquettiste` with `helper(name, fn)`, `selector(name, fn)`, `filter(name, fn)`, `transform(name, fn)`, `rule(spec)`, then deep-freezes `globalThis` and the registration object. Helpers must be pure; closure state cannot be frozen, and the `--jobs 1` versus `--jobs N` determinism test (§17) is what catches it (D12).
- **Pool**: one Jint engine per worker (`size` = render parallelism), rented for a whole unit. A unit renders synchronously on its worker thread, which `LimitMemory`'s per-thread accounting requires.
- **Model view**: `JsModelProxy : ObjectInstance` exposes an `IResolvedObject`'s template-visible properties in camelCase, read-only (`Set`, `DefineOwnProperty` and `Delete` refuse), records dependencies like the Scriban accessor, and wraps lists as frozen array-likes. Values cross as JSON-like data: string, number (integral → `long`), bool, null, arrays, plain objects, and resolved objects as proxies both ways. A returned function or symbol is an error.

## 11. Hashing and incremental generation (W6)

`H(...)` is SHA-256 over length-prefixed UTF-8 fields, as lowercase hex (`HashBuilder`). Dependency keys:

| Key | Current hash |
| --- | --- |
| `e:<elementId>` | `ElementDocument.DependencyHash` of the owning file (sub-element ids map to their owner) |
| `k:<kind>` | `ModelSnapshot.KindSetHash(kind)` |
| `r:<id>` | `ModelSnapshot.ReferrersHash(id)`: `H` over the sorted `(element id, DependencyHash)` of every file that references the id, except the file holding it and diagrams (D38) |
| `s:conventions`, `s:typeMaps`, `s:inflection` | hash of that settings section in canonical form (`databases` counts with `conventions`) |
| `s:referenceData` | hash of `referenceData` (strategy declarations) with `conventions.referenceStorage` and every `databases.<name>.referenceStorage` |
| `s:localization` | hash of the `localization` block; recorded by every localization helper call and every `each locale` unit (its `RLocale` depends on it) |
| `s:project` | hash of the project's `name` and `properties` (keys in ordinal order); recorded when a template reads the `project` variable (2026-10-07) |
| `l:<locale>:<ownerId>` | `LocalizationIndex.OwnerHash`: `H` over the owner's effective entries in that locale (every field of the owner and its sub-elements, ordinal by id, with their `src` fingerprints) and their description sidecars' hashes. A helper records one per consulted chain locale up to the one that answered, plus the owner's `e:` key when the default text answered (reference-types-seeds-localization.md section 3.8) |
| `t:<pack>/<path>` | file hash of a template or partial |
| `d:<databaseId>` | `SchemaDiffResult.Hash` |
| any key that no longer resolves | `"absent"` |

`StaticHash = H("mq-unit-1", EngineVersion.Value, pack name, canonical PackUnit JSON, ScriptsHash, canonical effective parameters, output base, formatter settings or "none", unit key)`. `InputHash = H(StaticHash, key₁, hash₁, key₂, hash₂, …)` over the unit's recorded read keys in ordinal order. A unit is **skipped** when not forced, not in check mode, its `UnitState` exists, the `InputHash` recomputed from the recorded keys equals the stored one, and every recorded output still has the same manifest hash and the same length and mtime on disk (a stat mismatch re-hashes the file). Owned outputs only need to exist. Skipped units keep their manifest entries and state. Because resolved objects carry `r:` keys (§7), a new file that starts contributing to an existing resolved object (a mapping, a table overlay, a relation) changes the recomputed `InputHash` of every unit that read that object, so incremental runs and `--force` runs agree. Rendered units store their new `UnitState` (read keys, hash, outputs with stat) in `CacheDirectory/units/<pack>.v1.bin` when their pack completes, and only in apply mode. A unit whose render fails keeps its previous outputs and state.

**Which units an entity edit re-renders.** An ordinary edit of one element (no seed, reference type, vocabulary or locale shard, and no id-carrying member of the element changed) patches the model index instead of rebuilding it, and changes only the edited file's `e:` key, the `r:` keys of the ids the file references, and the `d:` key of a database whose schema diff it changes; every other key keeps its hash. A unit therefore re-renders only when one of its own recorded reads is among those keys, and `s:referenceData`, `s:localization` and `l:` keys are recorded only by the units that read those facts (a reference type's storage choice, a localization helper, an `each locale` unit), so a pack that reads neither never carries them. `IncrementalUnitCountTests` (bench tests) enforces this on a small synthetic model with every benchmark pack: after the benchmark's one-entity edit, every unit with a changed `e:`, `r:` or `l:` read renders, and no unit renders outside those plus the readers of a `d:` key. On the benchmark model the edit re-renders 91 to 93 of 122,826 units, the same set as before reference data and localization were added; the example packs' incremental cost (about 4.5 s against 1.6 s for fanout alone, whose figure the phase 1 gate recorded) is one unit, the `sql-ddl` `schema` script of the 10,005-table main database, whose output changes with the edit and so cannot be skipped (`bench/README.md`, "Incremental run with the example packs").

**Last-run record (one-shot hosts, D45).** A host that runs one generation per process (the CLI's `generate`) sets the internal
`GenerationService.ReuseLastRun`. After an apply run that succeeded and left every planned unit with a current state (no pending
schema diff, no MQ6004, MQ6005, MQ6009, MQ6010 or MQ6015 (as built on 2026-10-02: MQ6004, MQ6005, MQ6009, MQ6010, MQ6027 or MQ6028), no journal left), it writes `CacheDirectory/last-run.v1.bin` through
`EnginePaths`: the run key (the engine build: contract version, module version ids of the engine and entry assemblies, runtime
version and dependency manifests; the folders; the request's packs, roots and hand-edit override; since 2026-10-02 packs and hand-edit override only, spec-errata E42), the stat of every model file and
referenced sidecar as the run read them, the content hash of the templates folder taken before the packs load, the stat of the
unit-state and built-root manifest files (since E42: any cache copy of a manifest an earlier release left) and the content hash of the committed manifest and snapshot files after the run, every
planned unit's recorded outputs with their stat, the planned unit count and the diagnostics of stages 1 to 5. The next such
process, before loading anything, answers an apply run with the same key from the intact record when every recorded stat and hash
still holds and no journal exists: `Succeeded`, every unit skipped, nothing written, the recorded diagnostics, which is what the
full run would return. Anything else (a changed, added or removed file, another engine build, a damaged or foreign record, a
recorded path that cannot be checked) runs the full pipeline. Long-lived hosts neither read nor write it. Details:
`Generation/README.md`.

## 12. Writer, manifest and journal (W7)

**12.1 Path policy** (`IOutputPathPolicy`, run for every output path, and the same guard used for every engine write, S23): the path must be relative with `/` separators, non-empty, with no `.` or `..` segment, no drive, UNC or leading `/`, no characters invalid on Windows (`<>:"|?*` and control characters), no segment that is a reserved device name or ends with `.` or space; it must lie under an `outputs.allow` path; it must match no `outputs.deny` glob (`*`, `**`, `?`); and no segment may be `.git` or `.maquettiste`. Before any write, each existing ancestor directory and the target are checked: a symlink whose resolved target leaves the root's real path is refused (MQ6004). Two outputs that differ only by case are refused (MQ6005), so output is identical on case-insensitive disks. Engine writes elsewhere use `WriteTarget.Model` (under `ModelRoot`: model files, manifests, snapshots, `.schema`), `WriteTarget.Cache` (`CacheDirectory`, `JournalDirectory`: index cache, unit state, built-root manifests, journal, run lock, plans, jobs) or `WriteTarget.Setup` (only `init`: `.git/hooks/post-checkout`, `.git/hooks/post-merge`, `.mcp.json`, the modeling skill, and the repository's `.gitignore` only in a policy built with `allowGitignore`, which `init --gitignore` alone passes). As built on 2026-10-02 (spec-errata E42): `allowGitignore` and `init --gitignore` are gone, so `Setup` never reaches `.gitignore`; an output path may also equal an `outputs.allow` entry exactly (the entry then names that one file, and the link check requires the file's real path to stay in the repo), unless the entry ends with `/`; `""` and `.` still mean the whole repo. A `.gitignore` reaches the disk only as generated output (a `block` unit, §12.3b) under an allow entry. The engine's own folder `<ModelRoot>/.cache` (the default `JournalDirectory`) ignores itself: before the first write under it (run lock, journal, a built-root manifest, or a `CacheDirectory` placed under it), the engine writes `.cache/.gitignore` holding the single line `*` through the guard (`Writing/CacheFolder`); a host-chosen `JournalDirectory` gets no file. The guard is wired by construction (D40): `EngineServices.EnginePaths` (an `OutputPathPolicy(options, null)`, engine-write checks only, no I/O) is passed to every component that writes a file (§18), and `ModelStore`, the plan store and the job store reach it through `EngineServices`; none of them writes a path `CheckEngineWrite` refused.

**12.2 Manifest.** One file per pack and root kind: committed roots at `.maquettiste/manifest/<pack>.json`, built roots at `.maquettiste/.cache/manifest/<pack>.json`. The format is canonical JSON with one entry per line, sorted by path in ordinal UTF-8 order. An empty manifest deletes the file. As built on 2026-10-02 (spec-errata E42): one file per pack for every root, `<ModelRoot>/manifest/<pack>.json`, which lives (and is committed) with the model. A cache copy an earlier release left at `<JournalDirectory>/manifest/<pack>.json` is read beside it (the model folder's entry wins a path both list), and the pack's next save writes the merged entries to the model folder and deletes the copy (`ManifestStore.LegacyFileOf`, a `Cache` write).

```json
{
  "$schema": "../.schema/v1/manifest.json",
  "pack": "sql-ddl",
  "files": [
    ["db/main/tables/invoice.sql", "9c1e…64 hex…", "table:01JB2Q0M8X4T5V6W7Y8Z9A0B1C"],
    ["db/main/tables/team.sql", "r:5a7f…", "table:01JB2Q0N…"]
  ]
}
```

The hash is the content hash of the bytes written. An `r:` prefix marks a regions file hashed with every region body emptied (the skeleton), so edits inside regions are not hand edits; an `o:` prefix marks an owned file (`once`, companion), never checked for edits and never deleted. As built on 2026-10-02 (E42), `b:` marks a `block` entry, hashed over the block's lines only, and `bc:` one whose file the engine created (§12.3b); the schema's hash pattern is `^(r:|o:|b:|bc:)?[0-9a-f]{64}$`. The unit is `<unitId>` or `<unitId>:<elementId>`, plus `#companion` for companions.

**12.3 Decision per output file** (`D` = disk bytes, `M` = manifest entry, `N` = new bytes), first match wins: no `D` → Added. `D` = `N` → Unchanged: no write, mtime untouched, entry written or refreshed (this also adopts an identical untracked file). `D` without `M` → HandEdited (an untracked file in the way). `D` ≠ `M` (skeletons for regions) → HandEdited. Otherwise → Modified. Owned: `D` exists → Kept, else Added. HandEdited follows the pack's policy: `fail` → Conflict (nothing written, exit 3), `overwrite` → write, `skip` → leave file and old entry. **Orphans**: entries of the run's packs produced by no rendered or skipped unit → Deleted (only if `D` = `M`, else HandEdited under the policy); owned orphans → OrphanedOwned (file kept, entry dropped). Without a `--pack` filter, manifests of packs that no longer exist are orphaned in full. Emptied directories under a root (never the root itself) are removed. Failed units are never orphaned. A `ContentOmitted` file (plan apply, §15) is produced like any other, so it is never an orphan; it is never written, and a disk hash other than its `ContentHash` is a Conflict. With `WriteContext.PlannedPaths` set, a write or delete outside it is refused (MQ6004).

*As built 2026-10-02 (spec-errata E42), written here.* Because the manifest now moves with the model (a pull brings a teammate's entries while ignored outputs stay as this checkout generated them), a file whose bytes the engine itself last wrote at that path in this checkout is not a hand edit: when `D` ≠ `M` (or `M` is missing) but the unit's recorded state in the cache (never shared) lists the path with a manifest hash `D` still matches, the decision is Modified, and an orphan in that state is Deleted. The state is loaded once per pack, and only when a file would otherwise be a hand edit.

**12.3b Managed blocks (status: built 2026-10-02, spec-errata E42).** A unit with `mode: block` renders the lines of one block (normalized to LF, a final line end added; a body holding one of its own delimiter lines fails the unit, MQ6006; no formatter, no regions). The block sits in a file the team otherwise owns, between `<comment> maquettiste: begin <pack>/<unit id>` and `<comment> maquettiste: end <pack>/<unit id>`, where `<comment>` is `blockComment` (default `#`, a run of non-whitespace). A delimiter line matches whatever its comment (trimmed, one non-whitespace run, a space, then the keyword and marker), and a rewrite uses the unit's comment. The post-processor's `OutputFile.Content` is the block's lines and its manifest hash `b:` + their hash; the writer (`Writing/ManagedBlock`, `WriteRun.ProcessBlockAsync`) works on the file's bytes, so nothing outside the block changes. Decision per block file (`D` = disk file, `B` = its block's lines, `M` = manifest entry, `N` = new lines), first match wins:

| Case | Decision |
| --- | --- |
| no `D`, `createFile` false | nothing written, no entry, no change listed; MQ6028 (info, "target-missing"); the unit keeps its previous state, so it renders again next run; the plan's reason is `target-missing` with one `target-missing` cause per file; `--check` does not count it; the last-run record is not written |
| no `D`, `createFile` true | Added: the file holds just the block; entry `bc:` |
| `D` holds the block twice, an end without a begin, or a begin never closed | Conflict, MQ6027 (error); the file is left alone, the old entry kept |
| the file with `N` in place equals `D` | Unchanged; the entry is refreshed (keeping `bc:`), adopting an identical untracked block |
| no block, no `M` | Added: appended at the end, after a line end when `D` lacks a final one and a blank line unless `D` already ends with one (an empty `D` holds just the block); entry `b:` |
| `B` = `N` (only the delimiters' comment differs) | Modified |
| `B` = `M`, or the engine wrote `B` here (§12.3) | Modified: the block's lines replaced |
| otherwise (`B` ≠ `M`, an untracked block that differs, or the block removed by hand while `M` exists) | HandEdited under the pack's policy: `overwrite` writes, `skip` keeps the file and the old entry, `fail` is a Conflict |

An orphaned block entry (the unit removed, the pack disabled and orphaned by a full run, or the unit no longer producing that path): no `D`, or `D` without the block → Deleted (only the entry goes); the block twice → MQ6027, kept; `B` ≠ `M` (and not written here) → the hand-edit policy; otherwise the block's lines are removed (with the blank line just before a block that ended the file, so insert then remove gives the file back), listed as `D`, and the file is deleted only when nothing but whitespace is left and the entry is `bc:`. Hand edits outside the block are never reported. Plans store a block file's bytes as a blob and the whole file's disk hash, so any edit of the file before apply makes the plan stale. One block per output path: two units writing one path are still MQ6005. `pack remove` leaves blocks in place, untracked, as it leaves every output (§12.3a).

**12.3a Removing a pack (status: built 2026-10-01).** `GenerationService.DeletePackAsync` (`DELETE /api/packs/{pack}`, the MCP tool `delete_pack`, `pack remove <name> --apply`) takes the run lock (waiting for a run in progress), checks `pack.json` against the caller's hash, removes `packs.<pack>` from `maquettiste.json` through the settings save (a refused save removes nothing), deletes `templates/<pack>/` (every path checked with `CheckEngineWrite(Model)` before the first delete; a link is removed, never followed), then deletes the pack's committed and built manifests and its unit states (as built 2026-10-02, E42: its one manifest, and any cache copy). As decided on 2026-10-01, the files the pack generated stay on disk, untracked, and the result lists them: deleting the manifests is what keeps the orphan rule above (manifests of packs that no longer exist are orphaned in full) from deleting every intact file on the next run without a `--pack` filter, which is not what someone removing a pack expects. A file left behind is then an untracked file like any other: a pack that later renders the same path adopts it when the bytes are identical and otherwise sees a hand edit under its policy. **Renaming a pack (added 2026-10-01).** `GenerationService.RenamePackAsync(pack, newName, expectedHash, ct, source, dryRun)` (`POST /api/packs/{pack}/rename` with `{ name }` and `If-Match`, the MCP tool `rename_pack`, `pack rename <name> <new-name> --apply`) takes the run lock, checks `pack.json` against the caller's hash, and refuses (`invalid`, nothing changed) a new name that is not a pack key, is the current one, has a folder under `templates/`, has a `packs.<name>` settings entry, has manifests of a former pack, or while an unfinished journal names the pack. It then moves `packs.<pack>` to `packs.<newName>` with its values through the settings save (a refused save renames nothing), renames `templates/<pack>/` with one directory move, never a copy (every entry checked with `CheckEngineWrite(Model)` at its old and new path first; a pack folder that is a link is refused; a link inside moves as an entry; a failed move puts the settings entry back), writes the new name into `pack.json` (the loader requires the name to equal the folder), and moves the committed and built manifests (as built 2026-10-02, E42: the one manifest) and the unit states, rewriting the unit keys' `<pack>/` prefix. Manifest entries carry no pack name, so the paths stay recorded under the new name and the orphan rule above does not delete them. The pack name is part of every unit's static hash, so the next plan renders each unit again and finds identical bytes (`unchanged`) unless a template prints the pack's name. Element generation hints keyed by the old name (`generation.<pack>` at any depth: an element, a table, a column, an enum member, a category node) are listed in the result (`hints`); the rename itself does not write the model, and the hints move to the new name in one model batch through the normal element save path (`PackHints.Rename`, `GenerationService.RenamePackHintsAsync`, each element checked against its current hash, all or nothing). The MCP tool (`updateHints`, default true) and `pack rename --apply` (unless `--keep-hints`) run that batch after the rename (`RenamePackAsync(..., updateHints: true)`, reported in `hintsUpdated`; a refused batch leaves the rename in place with a warning); the editor counts the hints with a dry run (`dryRun` in the request) and, ticked by default, saves the same change as one batch of its own, so it is one undo step. A map that already has a key with the new name is left as it is (added 2026-10-01). The embedded starter names do not matter: a project pack may take or leave `sql-ddl` or `csharp-dapper`, as `pack new` already allows; `packs.lock.json` (SPEC §19) is not implemented, so nothing pins a pack by name. The sample's pack copies and the reference application are not touched.

**12.4 Writes, journal, lock.** The writer drains a bounded queue with `min(jobs, 8)` tasks. Each write goes to `<dir>/.<name>.mq-<runId>-<n>.tmp`, then `File.Move(overwrite: true)`; the directory is created only after the path policy passes. The run journal `JournalDirectory/journal.jsonl` (default `.maquettiste/.cache/`) holds `{"t":"begin","run","plan","packs"}`, then a `{"t":"write","pack","path","hash","unit"}` or `{"t":"delete","pack","path"}` line appended and flushed to the OS after each file, `{"t":"pack","pack"}` after that pack's manifest and unit state are saved (fsync there), and `{"t":"end"}`, after which the file is deleted. A run that finds a journal without `end` replays it: for packs without a `pack` line, its entries overlay the manifests, so those files are never hand edits, and the new run then completes normally. Every generation mode first takes `JournalDirectory/run.lock` (`FileShare.None`); `LockMode.Wait` polls every 100 ms, `LockMode.Fail` returns `Busy`. Cancellation is checked between files; after it, the writer finishes the current file, leaves the journal consistent and returns within one second.

## 13. Post-processing: regions and formatters (W8)

Per rendered file, in order: (1) normalize CRLF and CR to LF, strip a BOM, encode UTF-8; (2) format, only for rendered units, when the unit's formatter (by name, or by extension when unnamed; `"none"` disables it) is configured: `ProcessStartInfo` with `ArgumentList` (`{path}` replaced), working directory = repo root, input on stdin, stdout = result, a non-zero exit or timeout → MQ6008 and the unit fails; (3) for `regions`, read the disk file and move each region body (`maquettiste:keep id=<id>` line … `maquettiste:end-keep` line, any comment syntax) into the same id in the new output; a disk region with no counterpart → MQ6010 Conflict; (4) hash (skeleton for regions) and classify the root through `IOutputPathPolicy`. Before stage 7 the runner checks each used formatter once with `Command VersionArgs`: the output must contain `Version`, else MQ6008 fails the run. Formatter settings are part of `StaticHash`. `--check` runs the same code in memory. As built on 2026-10-02 (spec-errata E42): the root classified in step 4 no longer refuses regions (MQ6015 is retired; regions work on any root), and a `block` file stops after step 1 (its lines, with a final line end; no formatter, no regions), hashed `b:` over its lines (§12.3b).

## 14. Schema diff (W8)

When an enabled pack has `UsesSchemaDiff`, the orchestrator loads `.maquettiste/snapshots/<database kebab name>.json` for each database, captures the current `RDatabase` and diffs them before planning. `PhysicalSnapshot` is canonical JSON: `database` (id), `name`, `dialect`, `revision`, and `tables`, `views` and `sequences` sorted by key, with columns (`key`, `name`, `type`, facets, `nativeType`, `nullable`, `default`, `defaultSql`, `identity`, `sequence`, `computed`, `computedStored`, `collation`, `comment`), `primaryKey` and `uniques` (with `clustered`), `foreignKeys`, `checks`, `indexes` and the table `comment`, all keyed by §7.3 keys (after 0.5.5 also `schemas`, a column's `defaultName`, a foreign key's `deferrable` and a view's `comment`; see the end of this section). The snapshot carries every physical property of S9's table contents, and the differ compares every snapshot property, so identity-to-sequence switches, virtual-to-stored computed columns and comment changes surface as `Altered` with `PropertyChange`s.

```csharp
namespace Maquettiste.Engine.SchemaDiff;
public enum ChangeKind { Added, Dropped, Renamed, Altered }
public sealed record PropertyChange(string Property, object? Old, object? New);
public sealed record ColumnChange(ChangeKind Kind, string Key, string? OldName, string? NewName, IReadOnlyList<PropertyChange> Changes, RColumn? Column);
public sealed record ObjectChange(ChangeKind Kind, string Key, string? OldName, string? NewName, IReadOnlyList<PropertyChange> Changes);
public sealed record TableChange(ChangeKind Kind, string Key, string? OldName, string? NewName, RTable? Table, IReadOnlyList<ColumnChange> Columns,
    IReadOnlyList<ObjectChange> PrimaryKey, IReadOnlyList<ObjectChange> Uniques, IReadOnlyList<ObjectChange> ForeignKeys,
    IReadOnlyList<ObjectChange> Checks, IReadOnlyList<ObjectChange> Indexes);
public sealed record SchemaDiffResult(string Database, int FromRevision, int ToRevision, bool IsEmpty, string Hash,
    IReadOnlyList<TableChange> Tables, IReadOnlyList<ObjectChange> Views, IReadOnlyList<ObjectChange> Sequences);
```

A renamed object keeps its key and may also carry property changes. `Tables` is ordered: added tables in FK dependency order, then renamed, then altered, then dropped in reverse dependency order. `ToRevision` = `FromRevision + 1` when the diff is non-empty. After a successful apply, the snapshot is saved with `ToRevision` (dry run and check never save it); `--check` reports a non-empty diff as drift (MQ6018). A migration unit is typically `for: model`, `mode: once`, with file blocks named from `schema_diff.<db>.to_revision`, so each migration is written once and kept.

**DDL coverage additions (after 0.5.5).** So a migration can write every DDL change of every object kind, the snapshot and the diff carry
more, all additive (init properties and optional JSON members, so earlier snapshots load unchanged, a model without the new
facets gives the same diff hashes, and only its declared schemas are new in the next snapshot saved):

- The snapshot records the declared `schemas` (`key` = the schema's id, `name`), a column's `defaultName` (the name its file
  gives its default constraint), a foreign key's `deferrable`, the primary key's `clustered` and a view's `comment`. A snapshot
  written before `schemas` existed has none (`null`): the diff then compares no schemas until the next snapshot records them,
  and a diff without a previous snapshot reports none (a first migration creates every schema with its tables).
- `SchemaDiffResult.Schemas` lists added, renamed and dropped schemas (`ObjectChange`, keyed by id); they reach the hash only
  when there are some. `TableChange.OldSchema` and `OldComment`, `ObjectChange.OldSchema` (views and sequences) and
  `ColumnChange.OldDefaultName` give what the database has before the migration, so a drop, move or rename names the object
  where it is.
- The resolved model gains `RColumn.Unicode`, `FixedLength` and `DefaultName`, `RForeignKey.Deferrable` and `RCheck.Column`. A
  column's `unicode` and `fixedLength` pick the type map's variant entry (`DialectTypeMaps.VariantKey`: `<keyword>:fixed:unicode`,
  `:fixed:ansi`, `:fixed`, `:unicode`, `:ansi`, most specific first, else the keyword), so the native type, the snapshot and
  MQ4005 see the variant. Validation adds MQ4056 (a DDL feature the dialect lacks, left out of the DDL), MQ4057 (unicode or
  fixedLength on a type without the facet) and MQ4059 (a foreign key whose referenced columns of a table file are neither
  its primary key nor one of its unique keys: a unique constraint, or a unique index without a filter except on Oracle; in
  any order).

**DDL coverage review additions (2026-10-03).** Also additive, with the same guarantees (a model without them gives the same
snapshots and diff hashes):

- Model: an index column is a `column` or an `expression` (a dialect map; `IndexColumn.Column` becomes nullable) with an
  optional key prefix `length`; a unique constraint has `nullsNotDistinct`; a column has `identity` (`ColumnIdentity`: `seed`,
  `increment`, `always`), read with `generated: identity`; a view has `columnList`, `withCheckOption`, `materialized` and
  `dependsOn` (ids, like a routine's; MQ4019 and MQ4020 cover it).
- Resolved model: `RIndexColumn.Column` is nullable, with `Expression` (the dialect's text; an expression without one leaves the
  index out, as a check without one is left out) and `Length`; `RUnique.NullsNotDistinct`; `RColumn.IdentitySeed`,
  `IdentityIncrement` and `IdentityAlways`; `RView.ColumnList`, `WithCheckOption`, `Materialized` and `DependsOn`, which is the
  file's `dependsOn` followed by the other views of the database whose names the body uses as identifiers (case-insensitive, a
  best-effort reading; the view's dependency keys gain those views).
- Snapshot: `SnapshotIndex.Columns` is a list of `SnapshotIndexColumn` (`column` or `expression`, `descending`, `length`; the
  JSON of a plain column is unchanged), whose index key part for an expression is `expr:` + 16 hex of its SHA-256;
  `SnapshotConstraint.NullsNotDistinct`; `SnapshotColumn.IdentitySeed`, `IdentityIncrement`, `IdentityAlways`; `SnapshotView`
  `columns` (the names CREATE VIEW lists), `withCheckOption`, `materialized` and `dependsOn` (the keys of the views it reads).
- Diff: the new properties are compared like the others (`identitySeed`, `identityIncrement`, `identityAlways`,
  `nullsNotDistinct`, a view's `columns`, `withCheckOption`, `materialized`; a view's `dependsOn` alone is no change), and
  `ObjectChange.OldDependsOn` and `OldMaterialized` give a changed or dropped view's dependencies and materialization in the
  committed snapshot, so a migration drops views before the views they read and drops a materialized view as one (a snapshot
  written before views recorded `dependsOn` reads as none, and sql-ddl then takes the model's dependencies of a changed view).
  `ObjectChange.OldColumns` and `OldUnique` give a changed or dropped unique constraint's or index's column keys and whether it was
  a key a foreign key can rely on (an index: unique, without a filter), so a migration drops the foreign keys that rely on it first.
- Validation: MQ4056 also covers Oracle's `ON UPDATE` and `restrict`/`set-default` actions, MySQL's `set-default`, Oracle's stored
  computed columns, `nullsNotDistinct` outside PostgreSQL, identity options a dialect lacks, an index expression on SQL Server,
  a key prefix length outside MySQL and a MySQL index on a text or blob column without one, a materialized view outside
  PostgreSQL and Oracle, and `withCheckOption` on SQLite or a materialized view.

**Foreign key columns set on delete (2026-10-06).** Additive, with the same guarantees: a table file's foreign key has
`onDeleteColumns` (`ForeignKey.OnDeleteColumns`, column ids or keys, default empty), the columns of the key a `set-null` or
`set-default` on-delete sets, PostgreSQL 15's `ON DELETE SET NULL (column, ...)`; empty sets every column, as before. It keeps
a tenant column of a composite key while clearing the reference. The resolved model gains `RForeignKey.OnDeleteColumns` (the
key's own columns, empty unless the action sets columns), the snapshot `SnapshotForeignKey.OnDeleteColumns` (column keys), and
the diff an `onDeleteColumns` property change, so a migration drops and adds the key again. Validation adds MQ4060 (error: the
list on an action other than set-null or set-default, or naming a column that is not the key's, or one twice) and MQ4056 for the
list outside PostgreSQL, where the DDL leaves it out and the action sets every column of the key. The resolver adds MQ4061
(warning) on a key a table file declares: set-null on a column that is not nullable, or set-default on one that is not nullable
and has no default, by the resolved nullability (an overlay's column included), at the key's `onDelete`; the database accepts
the key and refuses the delete. A relation's set-null on a required end stays MQ3011.

**PostgreSQL DDL features (2026-10-06).** Additive, with the same guarantees (a model without them gives the same snapshots
and diff hashes: new snapshot members are omitted at their defaults, a routine's definition text gains a line only for a
volatility other than the one `deterministic` implies or for settings, and the diff's new lists and table properties reach the
hash only when they hold something):

- Routines: `Routine.Volatility` (`RoutineVolatility?`; resolved `RRoutine.Volatility`, the file's or `immutable` when
  deterministic, else `volatile`) and `Routine.Settings` (name to SQL value; resolved `RRoutine.Settings`, `RRoutineSetting`
  by name). MQ4062 (error: deterministic but not immutable, a volatility on a procedure), MQ4063 (warning: a PostgreSQL
  security-definer routine without `search_path`). The idempotent sql-ddl script now writes `CREATE OR REPLACE` (PostgreSQL)
  and `CREATE OR ALTER` (SQL Server) routines, which a second run needs.
- Views: `View.SecurityInvoker` and `SecurityBarrier` (resolved, snapshot, diff properties that recreate the view).
- Indexes: `IndexMethod` gains `spgist`, `brin`, `hnsw`, `ivfflat`; `IndexColumn.OperatorClass` (resolved
  `RIndexColumn.OperatorClass`; it joins the index's snapshot key only when set, so existing keys stay) and
  `TableIndex.Storage`.
- Storage: `Table.Storage`, `TableIndex.Storage` and `Stereotype.Storage`, each dialect name to parameter name to a JSON
  value (`common.json#/$defs/storage`). Resolved `RTable.Storage` and `RIndex.Storage` are the database dialect's
  parameters by name (`RStorageParameter`, the value as the dialect writes it), a table's over its stereotypes' (stereotype
  order, later wins). The snapshot records them (`SnapshotStorageParameter`); the diff reports `storage` as a table property
  (`TableChange.Changes`, a new list for a table's own properties) and an index property, with `TableChange.OldStorage` and
  `ObjectChange.OldStorage` so a migration resets what went. MQ4064 (warning) names a PostgreSQL parameter the table or index
  method does not take, and a stereotype with storage that does not apply to tables.
- Temporal keys: `PrimaryKey.WithoutOverlaps`, `UniqueConstraint.WithoutOverlaps`, `ForeignKey.Period` (resolved, snapshot,
  diff properties). MQ4065 (error) mirrors what PostgreSQL 18 refuses.
- Exclusion constraints: `Table.Exclusions` (`ExclusionConstraint`: method, elements of a column or expression with an
  operator class and operator, `where`, `deferrable`); resolved `RTable.Exclusions` (`RExclusion`, named `ex_<table>_<columns>`
  when the file gives no name); snapshot `SnapshotTable.Exclusions`, keyed `ex:` and 16 hex of the SHA-256 of the definition,
  so a changed definition is a drop and an add and a new name a rename; `TableChange.Exclusions`.
- Partitioning: `Table.PartitionBy` (`PartitionStrategy`, columns) and `Table.Partitions` (`TablePartition`: name, bounds or
  default); resolved `RTable.PartitionBy` and `RTable.Partitions`; snapshot `SnapshotTable.PartitionBy` and `Partitions`
  (keyed by id); the diff reports `partitionBy` as a table property and `TableChange.Partitions` (a changed partition's
  property is `bounds`). A partitioned table's storage parameters are written on its partitions, since PostgreSQL takes
  none on the partitioned table. MQ4066 (error) mirrors what PostgreSQL refuses.
- MQ4056 covers each of these outside PostgreSQL (and a routine's volatility or settings on SQL Server, index storage on
  MySQL, SQLite and Oracle).
- Migrations run the unchanged SQL objects with phase `after` that depend on a view or routine they drop and create again
  (grants, INSTEAD OF triggers), after it. Grants and roles stay outside the model: roles differ per environment, and grants
  are SQL objects.

What each dialect's DDL does with them (SQLite table rebuilds, SQL Server dependents dropped and added back around `ALTER
COLUMN`, PostgreSQL views recreated around a retyped column, MySQL `MODIFY COLUMN`, the `idempotent` parameter) is the sql-ddl
pack's business: `packs/sql-ddl/README.md` has the coverage table per dialect.

## 15. Public API for the functions layer

The root namespace implements `host-contracts.md` Part 2; the numbers below are its requirement numbers. One handler maps to one call.

```csharp
namespace Maquettiste.Engine;
public sealed record EngineOptions                          // (4) nothing is read from the environment or the current directory
{   public required string RepoRoot { get; init; }          // absolute; output paths are relative to it
    public string? ModelRoot { get; init; }                 // default <RepoRoot>/.maquettiste; may be another mount of the same folder
    public required string CacheDirectory { get; init; }    // (10) index cache, unit state, plans, jobs
    public string? JournalDirectory { get; init; }          // default <ModelRoot>/.cache
    public int MaxDegreeOfParallelism { get; init; }        // 0 = Environment.ProcessorCount
    public IIdGenerator? IdGenerator { get; init; }         // default ULID generator
    public TimeProvider? TimeProvider { get; init; } }      // job timestamps (JobInfo.QueuedUtc…) and new ids only; never reaches output
// The output allowlist is not an option: it comes from the repo's maquettiste.json (outputs.allow, S4), and the path policy
// confines every output to RepoRoot whatever the allowlist says (host-contracts 4).
public enum ChangeSource { Editor, Disk, Cli, Engine }
public sealed record ElementChange(string Id, string Kind, string Path, string Hash);
public sealed record ChangeSet(IReadOnlyList<ElementChange> Changed, IReadOnlyList<string> Deleted, ChangeSource Source, bool Truncated)
{   public bool IsEmpty { get; } public ChangeSet TruncateTo(int maxJsonBytes); }                 // (17, 20)

public sealed class ModelStore : IAsyncDisposable           // (6–19) W1
{   public ModelStore(EngineOptions options);               // no I/O, never throws for model content
    public ModelSnapshot? Current { get; }
    public Task LoadAsync(CancellationToken ct);             // idempotent under concurrent callers
    public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct);   // loads on first call
    public Task<IReadOnlyList<ElementSummary>> GetIndexAsync(CancellationToken ct);
    public Task<ElementDocument?> GetElementAsync(string id, CancellationToken ct);
    public Task<IReadOnlyList<ReferenceInfo>> GetReferencesAsync(string id, CancellationToken ct);
    public Task<SaveResult> CreateAsync(ReadOnlyMemory<byte> json, ChangeSource source, CancellationToken ct);   // id assigned when absent
    public Task<SaveResult> SaveAsync(string id, ReadOnlyMemory<byte> json, string expectedHash, ChangeSource source, CancellationToken ct);
    public Task<SaveResult> DeleteAsync(string id, string expectedHash, DeleteResolution resolution, ChangeSource source, CancellationToken ct);
    public BatchParseResult ParseBatch(ReadOnlySpan<byte> json);           // (16) schema-validated against batch.json, no disk access
    public Task<BatchResult> ApplyBatchAsync(ModelBatch batch, ChangeSource source, CancellationToken ct);   // (15) all or nothing
    public Task<ChangeSet> RefreshAsync(IReadOnlyCollection<string> paths, CancellationToken ct);
    public Task<ChangeSet> RescanAsync(bool verify, CancellationToken ct);
    public Task<ValidationReport> ValidateAsync(ValidationScope scope, CancellationToken ct);
    public IDisposable OnChanged(Func<ChangeSet, CancellationToken, ValueTask> handler);
    public Task<SettingsDocument> GetSettingsAsync(CancellationToken ct);   // E3: maquettiste.json with its hash
    public Task<SettingsSaveResult> SaveSettingsAsync(ReadOnlyMemory<byte> json, string expectedHash, ChangeSource source, CancellationToken ct);   // E3
    public ValueTask DisposeAsync(); }
public enum SaveOutcome { Saved, Conflict, Invalid, NotFound, Referenced }
public sealed record SaveResult(SaveOutcome Outcome, string? Id, string? Hash, ElementDocument? Current, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ReferenceInfo> Referrers, ChangeSet? Changes);        // (12, 13) → 200 / 409 / 422 / 404
public enum DeleteResolution { Refuse, RemoveReferences, DeleteDependents }  // §15.1: RemoveReferences clears optional refs, a required ref makes it Invalid; DeleteDependents also resolves required refs
// §15.1 (2026-10-01): what a delete would do, computed like the delete without writing; BatchOperation gains DeleteResolution? Resolution (delete ops)
//   public Task<DeletePlan> GetDeletePlanAsync(IReadOnlyList<string> ids, DeleteResolution resolution, CancellationToken ct);
public sealed record DeletePlan(IReadOnlyList<string> Ids, DeleteResolution Resolution, SaveOutcome Outcome, IReadOnlyList<DeletePlanDelete> Deletes,
    IReadOnlyList<DeletePlanClear> Clears, IReadOnlyList<DeletePlanRemove> Removes, IReadOnlyList<DeletePlanRefusal> Refused,
    IReadOnlyList<DeletePlanSetting> Settings, IReadOnlyList<DeletePlanWarning> Warnings);   // entry records in DeletePlan.cs
public enum BatchOp { Create, Update, Delete }
public sealed record BatchOperation(BatchOp Op, string? Id, string? ExpectedHash, JsonElement? Element);
public sealed record ModelBatch(IReadOnlyList<BatchOperation> Operations);
public sealed record BatchParseResult(ModelBatch? Batch, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record BatchResult(SaveOutcome Outcome, IReadOnlyList<SaveResult> Items, ChangeSet? Changes);

public sealed class GenerationService                       // (29–35) W6
{   public GenerationService(ModelStore store, EngineOptions options);
    public Task<GenerationResult> RunAsync(GenerationRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct);
    public Task<PlanResult> PlanAsync(GenerationRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct);  // persisted dry run
    public Task<ApplyResult> ApplyAsync(string planId, IProgress<ProgressUpdate>? progress, CancellationToken ct);   // uses GenerationPlan.Request
    public Task<GenerationPlan?> GetPlanAsync(string planId, CancellationToken ct);
    public Task<string?> GetPlanDiffAsync(string planId, string path, CancellationToken ct);   // (31) from stored blobs, no re-render
    public Task<PreviewResult> PreviewAsync(string pack, string unitId, string? elementId, CancellationToken ct);   // (35)
    public Task<DatabaseViewResult> GetDatabaseViewAsync(string databaseId, CancellationToken ct);   // E1: load, validate, resolve; no lock, no writes
    public Task<QuerySqlResult> GetQuerySqlAsync(string queryId, string? dialect, QuerySqlOptions? options, CancellationToken ct);   // 2026-10-02: as E1, then QuerySql
    public Task<PackListResult> GetPacksAsync(CancellationToken ct); }   // E2: every pack under templates/, enabled or not
public enum LockMode { Wait, Fail }
public sealed record GenerationRequest { public GenerationMode Mode { get; init; } = GenerationMode.Apply; public IReadOnlyList<string>? Packs { get; init; }
    public bool Force { get; init; } public int? Jobs { get; init; } public HandEditPolicy? HandEdits { get; init; } public bool IncludeDiffs { get; init; }
    /* Roots removed 2026-10-02, E42 */ public LockMode Lock { get; init; } = LockMode.Wait; public bool StageBarriers { get; init; } }
public enum RunOutcome { Succeeded, Invalid, Drift, Conflicts, Busy, Stale, Cancelled, Failed }
public sealed record GenerationResult(string RunId, GenerationMode Mode, RunOutcome Outcome, IReadOnlyList<FileChange> Changes, int UnitsRendered,
    int UnitsSkipped, int FilesWritten, int FilesDeleted, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<StageTiming> Timings);
public sealed record PlanUnit(string Key, string InputHash, IReadOnlyList<string> ReadKeys, bool Skipped, IReadOnlyList<PlanFile> Outputs);
public sealed record PlanFile(string Path, string ContentHash, string ManifestHash, OutputMode Mode, FileRole Role, OutputRootInfo Root,
    string? DiskHashAtPlan);                                // null: the file did not exist at plan time
public sealed record GenerationPlan(string Id, GenerationRequest Request, long ModelVersion, IReadOnlyList<string> Packs,
    IReadOnlyList<PlanUnit> Units, IReadOnlyList<FileChange> Changes, IReadOnlyList<Diagnostic> Diagnostics)
{   public IReadOnlyDictionary<string, int> Counts { get; init; }          // every FileChangeKind's JSON name → count (added 2026-10-02)
    public IReadOnlyDictionary<string, int> UnitsRendered { get; init; }   // reason (new, forced, check, inputs, outputs, ...) → units
    public IReadOnlyDictionary<string, int> UnitsSkipped { get; init; } }  // reason (unchanged) → units
public sealed record PlanResult(RunOutcome Outcome, GenerationPlan? Plan);
public sealed record ApplyResult(RunOutcome Outcome, IReadOnlyList<string> StaleUnits, IReadOnlyList<string> StalePaths, GenerationResult? Result);
public sealed record PreviewResult(IReadOnlyList<RenderedFile> Files, IReadOnlyList<Diagnostic> Diagnostics);

public sealed class JobQueue : IAsyncDisposable             // (23–28) W6
{   public JobQueue(GenerationService generation, EngineOptions options, int capacity = 16);
    public bool TryEnqueue(JobRequest request, [NotNullWhen(true)] out JobInfo? job);
    public Task<JobInfo?> GetAsync(string id, CancellationToken ct);   // in-memory jobs without I/O; finished jobs from CacheDirectory/jobs/<id>.json (27)
    public Task<IReadOnlyList<JobInfo>> ListAsync(CancellationToken ct);
    public bool Cancel(string id);
    public Task<JobHistoryCleared> ClearHistoryAsync(CancellationToken ct);  // finished records, and finished plans no queued or running job names
    public Task RunAsync(CancellationToken stoppingToken);  // one job at a time; the [BackgroundService] awaits this
    public IDisposable OnProgress(Func<JobInfo, ProgressUpdate, ValueTask> handler);
    public IDisposable OnCompleted(Func<JobInfo, ValueTask> handler); }
public enum JobKind { Plan, Apply }
public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }
public sealed record JobRequest(JobKind Kind, GenerationRequest? Plan, string? PlanId);
public sealed record JobHistoryCleared(int Jobs, int Plans);   // 2026-10-02, DELETE /api/jobs
public sealed record JobInfo(string Id, JobKind Kind, JobState State, int? QueuePosition, ProgressUpdate? Progress,
    PlanResult? PlanResult, ApplyResult? ApplyResult, string? Error,
    DateTimeOffset QueuedUtc, DateTimeOffset? StartedUtc, DateTimeOffset? FinishedUtc);   // from EngineOptions.TimeProvider

// Phase 2 editor additions E1–E4 (phase2-design.md §3.8); records in Editor/, public, Web-default JSON without converters
public sealed record DatabaseViewResult(DatabaseView? View, IReadOnlyList<Diagnostic> Diagnostics);   // View null on model errors or an unknown id (MQ6017)
public sealed record DatabaseView(string Id, string Name, string Dialect, string? Version, string? DefaultSchema, IReadOnlyList<TableView> Tables,
    IReadOnlyList<ViewView> Views, IReadOnlyList<SequenceView> Sequences,
    IReadOnlyList<RoutineView> Routines, IReadOnlyList<DatabaseTypeView> Types, IReadOnlyList<SqlObjectView> Objects, // 2026-10-01
    IReadOnlyList<QueryView> Queries, // 2026-10-02, §7 "Queries"
    IReadOnlyList<SchemaView> Schemas, string Quoting, int? MaxIdentifierLength,
    string ByConvention, IReadOnlyList<ConventionPackageView> Packages, /* annotations (§7.0a): */ string? DisplayName, string? PluralName,
    string? Description, IReadOnlyList<string> Stereotypes, IReadOnlyList<string> Tags, string? Category, IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);
public sealed record SchemaView(string Id, string Name, bool IsDefault, bool IsDeclared, /* annotations */ ...);
public sealed record ConventionPackageView(string PackageId, string? Schema);
// TableView, ViewView and SequenceView end with the same eight annotation members (§7.0a).
public sealed record TableView(string Key, string Name, string? Schema, string Origin, string? EntityId, string? RelationId, bool IsJunction,
    bool IsLookup /* always false: the enum lookup-table option is retired (MQ7012) */, string? Comment, IReadOnlyList<ColumnView> Columns, KeyView? PrimaryKey, IReadOnlyList<KeyView> Uniques,
    IReadOnlyList<ForeignKeyView> ForeignKeys, IReadOnlyList<IndexView> Indexes);
public sealed record ColumnView(string Key, string Name, string Type, string NativeType, int? Length, int? Precision, int? Scale, bool Nullable,
    string? DefaultSql, bool Identity, string? Computed, string? AttributeId, string? AttributePath, bool IsPrimaryKey, bool IsForeignKey,
    bool IsDiscriminator, int Position, object? Default, bool ComputedStored, string? SequenceId, string? Collation, string? Comment,
    /* annotations of the column's own entry (§7.0a) */ ...);
public sealed record KeyView(string Name, IReadOnlyList<string> Columns);              // column keys
public sealed record ForeignKeyView(string Name, IReadOnlyList<string> Columns, string ReferencedTable, IReadOnlyList<string> ReferencedColumns,
    string OnDelete, string OnUpdate, string? RelationId, string? EndId);
public sealed record IndexView(string Name, IReadOnlyList<IndexColumnView> Columns, bool Unique, string? Where);
public sealed record IndexColumnView(string Column, bool Descending);
public sealed record PackListResult(IReadOnlyList<PackManifest> Packs, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record SettingsDocument(ProjectSettings Settings, string Path, string Hash, JsonElement Json);
public sealed record SettingsSaveResult(SaveOutcome Outcome, string? Hash, SettingsDocument? Current, IReadOnlyList<Diagnostic> Diagnostics);

// Bulk reads for external systems (2026-10-01): every page by (kind, name, id) ordinal, an opaque cursor encoding the last position
public static class ModelPages
{   public const int DefaultLimit = 100, MaxLimit = 1000;
    public static IReadOnlyList<ElementSummary> Filter(IReadOnlyList<ElementSummary> index, ElementFilter filter);
    public static IndexPage PageIndex(IReadOnlyList<ElementSummary> rows, string? cursor, int limit);
    public static ElementPage ReadElements(ModelSnapshot snapshot, IReadOnlyList<string>? ids, ElementFilter filter, IReadOnlyList<string>? fields, string? cursor, int limit);
    public static ModelKindsResult Kinds(IReadOnlyList<ElementSummary> index, bool byPackage); }   // a bad cursor is FormatException
public sealed record ResolvedQuery(string Scope = "all", string? Database = null, string? Cursor = null, int Limit = 100);
// GenerationService: Task<ResolvedPage> GetResolvedAsync(ResolvedQuery query, CancellationToken ct);   records in Editor/ResolvedRecords.cs

// Queries (2026-10-02, §7 "Queries"), namespace Maquettiste.Engine; projections in Editor/QueryViews.cs
public static class QuerySql {
    public static QuerySqlText Render(RQuery query, string? dialect = null, QuerySqlOptions? options = null);   // ArgumentException: unknown dialect or option
    public static QuerySqlText RenderCollection(RQueryCollection collection, string? dialect = null, QuerySqlOptions? options = null); }
public sealed record QuerySqlOptions { public static QuerySqlOptions Default { get; }
    public string Placeholder { get; init; } = "@";   // "@", ":" or "$"
    public string Lists { get; init; } = "expand"; }   // "expand" or "any"
public sealed record QuerySqlText(string Sql, IReadOnlyList<string> Parameters, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record QuerySqlResult(QuerySqlPreview? Preview, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record QuerySqlPreview(string Id, string Name, string Database, string Dialect, string Sql, IReadOnlyList<string> Parameters,
    IReadOnlyList<QueryCollectionSql> Collections);
public sealed record QueryCollectionSql(string Name, string Sql, IReadOnlyList<string> Parameters, IReadOnlyList<QueryKeyView> Keys);
// QueryView, QueryParameterView, QuerySourceView, QueryFieldView, QueryCollectionView, QueryKeyView and QueryRecord: docs/api/openapi.yaml.

// The schema copies in <ModelRoot>/.schema/v1/ (§3), namespace Maquettiste.Engine.Json; init, the editor's start and the MCP server's start
public static class SchemaFolder
{   public const string RelativePath = ".schema/v1";
    public static SchemaFolderStatus Status(EngineOptions options);   // byte comparison with the embedded schemas; reads only
    public static Task<SchemaFolderStatus> RefreshAsync(EngineOptions options, CancellationToken ct); }   // returns the status before; throws IOException or UnauthorizedAccessException
public sealed record SchemaFolderStatus(IReadOnlyList<string> Missing, IReadOnlyList<string> Stale, IReadOnlyList<string> Extra)
{   public bool IsCurrent { get; } public IReadOnlyList<string> Differing { get; } public string Describe(); }   // Describe: "wrote a, b; removed c"
```

**Editor additions (E1–E4).** E1 projects the resolved database into flat records (resolved objects reference each other, so R-types are never serialized); a model with validation or resolution errors returns those errors and no view, and an unknown database id is MQ6017. E2 loads enabled packs exactly as a run does and only parses and schema-checks disabled ones. E3 follows the element save rules: schema (`maquettiste.json`), canonical bytes, expected hash against disk (a disk edit behind the index is refreshed first), validation of a candidate snapshot where only errors the change introduces make it `Invalid`, atomic write under the store's write gate, then reload, with any resulting `ChangeSet` sent to `OnChanged` subscribers; an unchanged body is `Saved` without a write. E4 fills `Category` and `Stereotypes` from `ElementBase` at both indexer call sites; summaries are never persisted.

**As built (bulk reads, 2026-10-01).** For external systems that read a model of thousands of elements, `ModelPages` filters the index (the filters `get_model_index` had), pages it, reads canonical documents in pages trimmed to chosen top-level members, and counts kinds per model or per package; `GenerationService.GetResolvedAsync` projects the resolved model into flat records per template scope the way E1 projects a database (entities with resolved attributes marked `isKey` and `isInherited`, relations, processes with every state and transition, databases as their `DatabaseView`, single tables, and the other kinds), validating and resolving once per snapshot and keeping only the last snapshot's resolved model; a model with errors returns its errors and no records. The functions answer them as `GET /api/model/elements`, `/api/model/resolved` and `/api/model/kinds` (and `/api/model/index` with filters, `limit` or `cursor`, the next page in a `Link` header), the MCP server as `get_elements`, `get_resolved_model`, `get_model_kinds` and the paged `get_model_index`, and the CLI as `model export` and `model stats`.

**15.1 Deletes that resolve references (status: built 2026-10-01).** A delete had two resolutions, and `remove-references` could
never succeed where it mattered most: a table, view, sequence or mapping needs its `database`, a relation end and an overlay need their
`entity`, a typed attribute needs its `ref`, so deleting a database or an entity that anything stored or related was refused with a raw
schema error. `DeleteResolution.DeleteDependents` (`delete-dependents`) resolves required references too. `ChangePlanner.Cascade.cs`
plans every resolving delete of a change against the files as the index read them, then stages the result once every operation has
run. Each reference to a deleted element or sub-element is cleared when the referrer stays schema-valid without it (an optional
reference, a list entry, a diagram member, a seed cell); otherwise the smallest enclosing part of the referrer that leaves it valid is
removed, from the reference's parent object outwards (an attribute whose type is deleted, a relation attribute, a key, a foreign key, an
index; the list itself is skipped when emptying it would widen it, and a database's last convention package goes by narrowing the
database to `byConvention: packages`); when only the whole referrer would do, it is deleted with its own dependents, recursively (the
tables, views, sequences and mappings of a database; the relations, overlays and mappings of an entity, and the mappings of those
relations). Removed parts are deleted targets in turn, owning referrers (seeds, scenarios) go with their owner as before, elements the
batch names itself are left to their own operation, and a visited set ends cycles. `remove-references` uses the same planner and
refuses what would need more, with one readable MQ2001 per referrer ("relation 'refers to' cannot lose its reference to entity Product
(/ends/1/entity): the reference is required ... or delete with its dependents"); a failed change now lists a readable MQ2001 before the
raw MQ1002 schema failures it produced. A deleted database's `databases.<name>` conventions entry in `maquettiste.json` is removed in
the same atomic change under either resolution (it is keyed by name and would otherwise apply to a later database of that name). Pack
units whose `where.database` names it are left as they are (pack manifests are not model files, and a filter may name a database before it
exists) and the plan warns that they stop matching. `GetDeletePlanAsync` runs the same planner and validation without writing and returns
the deletes (with why), the clears, the removed parts, what blocks it, the settings entries and the warnings, with names from the index;
the API serves it as `GET /api/model/elements/{id}/delete-plan` and `POST /api/model/delete-plan`, MCP as `delete_element` with `dryRun`,
the CLI as `model delete --dry-run`. A batch delete operation takes `resolution`, so the explorer's bulk delete is one change, and the
editor reads every document the plan touches first so the whole cascade is one undo step.

A plan persists to `CacheDirectory/plans/<id>/plan.json` (through `EnginePaths`), with post-processed bytes of each added or modified file in `blobs/<ContentHash>`; the 20 newest plans are kept. The plan stores the request it was made with (`GenerationPlan.Request`: packs, roots (removed 2026-10-02, E42), hand-edit policy, force, lock mode) and, per unit, every output file (`PlanUnit.Outputs`, unchanged files included, with mode, role, root and the disk hash seen at plan time), so apply needs no second render. `ApplyAsync` locks (with `Request.Lock`), reloads, validates, resolves, re-plans, recomputes each `PlanUnit.InputHash` from its read keys, and re-hashes every planned path on disk. Any input difference, any unit added or removed, or any planned path whose disk hash differs from `DiskHashAtPlan` (a hand edit, or an edit inside a protected region, since the plan's region bodies were merged at plan time) returns `Stale` with `StaleUnits` and `StalePaths` and writes nothing (30). Otherwise it feeds the writer one `ProcessedUnit` per planned unit: added and modified files from the blobs, unchanged ones as `ContentOmitted` files, skipped units as `SkippedUnit`s, with `WriteContext.PlannedPaths` = every path in `Outputs` and `Changes` so nothing outside the plan is touched (S19); then it saves unit state from the plan's read keys. A plan's `Changes` name **every file** its packs produce or remove, each with what Apply does to it (added 2026-10-02): the writer's decisions with identical files included (`Unchanged`, no diff), plus the outputs of the units the plan skipped, from their stored state (an owned `o:` output as `Kept`, any other as `NotRendered`, with its unit key and manifest hash). Entries carry no content and no diff, and `Counts`, `UnitsRendered` and `UnitsSkipped` summarize them so a client never has to count a large list (a job record keeps them when it drops the lists). Apply ignores `Unchanged`, `NotRendered` and `Kept` entries: it is fed from `Units`, and their paths are already among the units' outputs. A `Kept` file (an existing `once` file or companion) has no diff: `GetPlanDiffAsync` returns an empty one, so a migration unit's placeholder for an earlier revision is never shown against the real migration. On the reference application (867 files) the entries add about 70 KB to a 1.65 MB first plan and about 300 KB to a 1.47 MB incremental one; `Units` (read keys) stays the bulk of `plan.json`. Batches stage every file as `.<name>.mq-<batchId>.tmp` in the target folder and rename only after all staging succeeded; a rename failure rolls back the renamed files from copies staged beside them.

**15.2 The shared tool catalog and the assistant (status: built 2026-10-03, erratum E44).** The read tools of `maquettiste mcp` and the
editor's assistant come from one catalog in the engine, so the two cannot drift. `AgentTools` (`Agent/AgentTools.cs`) holds 24 read
tools (`get_project`, `get_model_index`, `get_model_kinds`, `get_elements`, `get_element`, `get_references`, `get_resolved_model`,
`get_schema`, `get_settings`, `get_database_view`, `validate`, `list_validation_rules`, `preview_query_sql`, `preview_binding_sql`,
`get_materialize_status`, `preview_materialize`, `list_packs`, `get_plan`, `get_plan_diff`, `preview_unit`, `reference_type_usage`,
`localization_status`, and since 2026-10-05 `list_snapshots` and `compare_snapshots`, docs/engineering/snapshots.md): each tool's name, title, description and input schema are one definition (`Agent/tools.json`, embedded,
taken byte for byte from what the MCP SDK reflected before the move) and each handler one method over `ModelStore` and
`GenerationService` with the editor API's bodies and problems; `CallAsync(name, arguments, ct)` checks the arguments' JSON types against
the schema first (`ArgumentProblem`, the MCP server's check, moved here). The MCP server lists the catalog's tools beside its own
write and pack tools (`CatalogTool`, a `McpServerTool` over the catalog, read-only, idempotent and closed-world), so its tool list and
answers are unchanged (tests compare both). `AgentConventions` serves the embedded `skills/maquettiste-modeling/SKILL.md` with the
repository's `CONVENTIONS.md` (the MCP resource and prompt, and the assistant's system prompt). `ModelStore.PreviewBatchAsync(batch, ct)`
runs a batch as a dry run (the same parsing, expansion of schema, process and materialize operations, planning, hash checks and
validation as `ApplyBatchAsync`, nothing written) and returns `BatchPreview`: the outcome per operation and, when it would succeed,
each file it would create, change or delete with its text before and after. `AssistantProposals` is the assistant's only write tool,
`propose_changes`: it gives new elements and sub-elements the ids their schemas require, pins each update and delete to the hash of the
document it read, runs the dry run, and replaces each created or updated element by the document the dry run would write, so applying
the proposal through `POST /api/model/batch` writes exactly what was shown or is refused as a conflict. `AssistantPrompt` builds the
system prompt (what Maquettiste is, what the assistant may and may not do, the conventions, `assistant.instructions`) and the tagged
context block of a user message. `ProjectSettings.Assistant` (`AssistantSettings`: `Instructions`, `MaxTurns` 10, `TokenBudgetPerRequest`
200 000, `TokenBudgetPerDayPerUser` 2 000 000) is the `assistant` section of `maquettiste.json`; it changes no generated output. The
agent loop itself lives in the functions (`AssistService`), since it talks to the host's `IAiChat` (host-contracts.md §1.3).

## 16. CLI (W9)

`maquettiste [global options] <command> [options]`. The parser is hand-written (no extra package). Globals: `--repo <dir>` (default: nearest ancestor holding `.maquettiste/maquettiste.json`), `--cache-dir <dir>` (default: `$MAQUETTISTE_CACHE_DIR`, else the OS user cache folder `…/maquettiste/<first 16 hex of SHA-256 of the repo path>`; D13), `--jobs <n>`, `--progress auto|plain|json|none`, `--verbosity quiet|normal|detailed`, `--no-color`.

| Command | Options and behavior |
| --- | --- |
| `init` | As built on 2026-10-02 (spec-errata E42): `--gitignore` is refused with a hint, the default roots are `db` and `src/Generated` without `commit`, no built-roots line is printed (only the formatters note), and the `--hooks` hooks run `maquettiste generate --quiet`. Before: `--pack sql-ddl\|csharp-dapper\|none` (default sql-ddl), `--hooks`, `--gitignore`. Creates `maquettiste.json` (format 1, `outputs.allow` roots `db` committed and `src/Generated` built), the model folders, `.schema/v1/*`, and the starter pack copied from embedded resources, then prints one line naming the built roots (regenerated by `generate`; the team ignores or commits them). It never reads or writes the repository's `.gitignore` (spec-errata E39), and a block from an earlier version stays as it is. `--gitignore` is the opt-in: it appends, or refreshes in place, a `# maquettiste:begin` … `# maquettiste:end` block in `.gitignore` (built roots, `.maquettiste/.cache/`), refusing malformed markers before anything is written. Idempotent: existing files are kept, except `.schema/v1`, which is refreshed |
| `validate` | `--format text\|json\|sarif`, `--output <file>` |
| `generate` | (As built 2026-10-02, E42: `--roots` is refused, `--check` renders every root and exits 2 when a file would be added, changed or deleted.) `--pack <name>` (repeatable), `--force`, `--roots all\|committed\|built`, `--hand-edits fail\|overwrite\|skip`, `--watch` (FileSystemWatcher on the model root, 250 ms debounce, `RefreshAsync`, then an incremental run; engine-owned paths ignored), `--dry-run` (lists `A`/`M`/`D`/`H`/`K`/`O` per path), `--diff` (with `--dry-run`: unified diffs on stdout), `--check` (committed roots, in memory), `--format text\|json` |
| `migrate` | Stub: format 1 → "Model format 1 is current." exit 0; newer → exit 4; missing or invalid → exit 1 |
| `pack new <name>` | `--from empty\|sql-ddl\|csharp-dapper`; creates `templates/<name>/` (pack.json, one template, `helpers.js`); refuses an existing folder |
| `pack remove <name>` | Previews removing a pack (the files of `templates/<name>/`, the `packs.<name>` entry, the generated files that stay on disk untracked); `--apply` removes it (12.3a); `--format text\|json`. Exit 1 for an unknown pack or a refused settings save, 3 when `pack.json` changed meanwhile (added 2026-10-01) |
| `pack rename <name> <new-name>` | Previews renaming a pack (the files that move, the `packs.<name>` entry, the generated files that stay tracked, the elements whose generation hints name the old pack); `--apply` renames it and moves those hints to the new name in one model batch, `--keep-hints` leaves them (12.3a); `--format text\|json`. Exit 1 for an unknown pack, a refused name or a refused settings save, 3 when `pack.json` changed meanwhile (added 2026-10-01) |
| `model delete <id\|name>` | `--resolution refuse\|remove-references\|delete-dependents` (default refuse), `--dry-run` (prints the delete plan, writes nothing), `--format text\|json` (15.1); exit 1 when refused or invalid, 3 on a conflict |
| `bench` | §17 options |

**Exit codes** (S17): 0 success; 1 validation errors in the model or packs, including template and script errors; 2 drift (`--check`: stale, missing or orphaned file, stale snapshot); 3 hand-edit or region conflicts; 4 internal error, usage error, busy lock with `LockMode.Fail`, or cancellation. With several outcomes the precedence is 4, 1, 3, 2. `bench` exits 2 when a budget or the regression threshold fails.

**Progress** goes to stderr; stdout carries only results (lists, diffs, JSON, SARIF), so output pipes cleanly. `auto` means `plain` when stderr is not a terminal. On a terminal: one rewritten line, at most 10 per second: `[6/8 render] 41,200/100,480 src/Generated/Billing/Invoice.g.cs`. `plain`: a line when each stage starts and when it ends (count, elapsed). `json`: one `ProgressUpdate` JSON line per update, at most 10 per second. Summary: added, modified, deleted, unchanged, units skipped and hand edits; `--verbosity detailed` adds stage timings.

## 17. Bench (W11), tests and CI

**Bench.** `SyntheticModelGenerator.WriteAsync(string repoRoot, SyntheticModelOptions options, CancellationToken ct)` writes a deterministic (seeded) model through `ICanonicalJson`: 50 packages, 5,000 entities (8 to 20 attributes each, 10% in inheritance hierarchies, stereotypes `audited` and `soft-delete` on 30%), 20,000 relations (70% one to many, 15% many to many, 10% with attributes, 5% one to one), 500 enums, 250 value objects, 50 scalar types, 3 databases (PostgreSQL with every package, SQL Server with 1,000 entities, SQLite with 200), `sql-ddl`, `csharp-dapper` and `bench/packs/fanout` (N files per entity, default sized so the run passes 100,000 files). No processes. `BenchmarkHarness.RunAsync(BenchmarkOptions, IProgress<ProgressUpdate>?, CancellationToken)` runs: a cold full run with `StageBarriers = true` (per-stage numbers), a cold pipelined run (total), and an incremental run after editing one attribute of one entity. It returns a `BenchmarkReport` (per stage: wall, busy, items; files; machine cores; jobs used; OS; per budget: limit, actual, pass). Budgets from S13: load + validate + resolve ≤ 3 s, plan + skip ≤ 2 s, render ≤ 40 s, post-process + write ≤ 15 s, cold total ≤ 60 s, incremental ≤ 2 s, files ≥ 100,000. CLI: `maquettiste bench [--out <dir>] [--jobs 8] [--seed 42] [--entities 5000] [--relations 20000] [--enums 500] [--fanout <n>] [--keep] [--baseline bench/baseline.json] [--max-regression 10] [--format text|json]`. `--jobs 8` caps the engine's parallelism so a 24-core machine approximates the 8-core budget; it does not cap GC or I/O threads, so that is an approximation, noted in the report.

**Tests.** xunit v3 (`xunit.v3` 3.2.2 with `xunit.runner.visualstudio`, run by `dotnet test`); no test needs Docker or network. `tests/Maquettiste.Testing` provides `ModelBuilder` (in-memory, no I/O), `SequentialIdGenerator` (valid ULIDs from a seed), `TempRepo` (temporary repo root with `.maquettiste/`, disposable) and `Golden` (tree comparison; rewrites when `MAQUETTISTE_UPDATE_GOLDEN=1`, which only tests read). `ModelBuilder` sketch:

```csharp
var b = new ModelBuilder(seed: 1);
var billing = b.Package("Billing");
var customer = b.Entity("Customer", billing).Key("id", "uuid", IdentityStrategy.UuidV7).Attr("name", "string", a => a.Length(120).Required());
var invoice = b.Entity("Invoice", billing).Stereotype("audited").Attr("number", "string", a => a.Length(32).Unique());
b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "customer", toNavigation: "invoices");
b.Database("main", Dialect.PostgreSql);
ModelSnapshot model = b.Build();                  // pure; also b.Add(element), b.Settings(s => s with { … }), b.WriteToAsync(modelRoot, ct)
```

Fixtures live in `tests/fixtures/<area>/` (for example `spec-examples/` (the SPEC Section 6 and 7 examples with valid ULIDs, scaffold), `models/billing`; `models/invalid-*`; `models/relation-shapes`; `packs/`; `golden/sql-ddl`, `golden/csharp-dapper`). Every module is unit-tested through `ModelBuilder` without the loader; the loader, writer and CLI use `TempRepo`. Required cross-cutting tests: schema/record consistency (§3); determinism (the billing fixture generated with `--jobs 1` and `--jobs 8` is byte-identical, generated under `tr-TR`, `de-DE` and the invariant culture is byte-identical (W5, W6), and `ci.yml` compares a SHA-256 listing of that output between ubuntu and macos); `--check` catches drift, orphans and hand edits; an interrupted run (cancelled mid-write) resumes without reporting hand edits; path policy refuses `..`, absolute paths, deny globs, `.git`, `.maquettiste` and symlink escapes, and `CheckEngineWrite` refuses a `Cache` target outside `CacheDirectory` and `JournalDirectory` (W7); incremental equals forced: creating a mapping file (and, separately, a table overlay and a relation) and running incrementally gives the same output as `--force` (W6, §11); a plan applied after a region edit or hand edit on a planned path returns `Stale` (W6); a running script is cancelled within one second (W4). `bench.yml` runs on `v*` tags and on manual dispatch against `bench/baseline.json`.

## 18. Workstreams

**Scaffold (S) runs first, alone.** It creates `global.json`, both props files, `maquettiste.slnx`, every csproj (engine, CLI, bench, the four test projects), every folder in §1, and `.github/workflows/ci.yml`. It also delivers the first complete version of `Json/` (`SchemaRegistry`, `CanonicalJson`, `ElementReader`) and `schemas/v1/`, with their tests (`tests/Maquettiste.Engine.Tests/Json/`); W1 owns them from then on. `ModelBuilder` lives in `tests/Maquettiste.Testing` so every test project can use it; its tests are in `tests/Maquettiste.Engine.Tests/Support/`. It writes every public type named in this document with its exact signature: interfaces, records, enums, the R-types of §7, the §14 diff records, the §10 sandbox interfaces and the §15 facade. It implements the contract layer in full: `Model/` (all records, `KindInfo`, `BuiltinTypes`, `ElementRef`, `ModelSnapshot.Create` with its indexes), `Diagnostics/` (records and the full `RuleCatalog`), `Pipeline/` (every interface and DTO in §4), `Hashing/` (`ContentHash`, `HashBuilder`), `EngineVersion`, `IIdGenerator` with the ULID generator, and `tests/Maquettiste.Testing` (`ModelBuilder`, `SequentialIdGenerator`, `TempRepo`, `Golden`). Every other class named here gets a stub with its signature and bodies that throw `NotImplementedException`. Workers fill in bodies and add internal members; a public signature changes only through this document. The solution must build with warnings as errors. After the scaffold, no worker edits a csproj, the solution, `Directory.Build.props`, `Directory.Packages.props`, or anything the scaffold owns; a needed change goes through this document first.

**Concrete classes and constructors** (all `internal sealed`, in the implementing workstream's folder; an `IOutputPathPolicy paths` parameter is the engine-write guard, D40): `SchemaRegistry()`, `CanonicalJson(ISchemaRegistry)`, `ModelLoader(EngineOptions, ISchemaRegistry, ICanonicalJson, IOutputPathPolicy)`, `ModelValidator(EngineOptions, ISchemaRegistry, IScriptSandboxFactory)`, `ModelResolver(EngineOptions)`, `ScriptSandboxFactory()`, `TemplateCache()`, `Renderer(EngineOptions, ITemplateCache)`, `PackLoader(EngineOptions, ISchemaRegistry)`, `UnitPlanner(EngineOptions)`, `ChangeDetector(EngineOptions)`, `DependencyHasher(ModelSnapshot, ResolvedModel, PackSet, IReadOnlyDictionary<string, SchemaDiffResult>)`, `UnitStateStore(EngineOptions, IOutputPathPolicy)`, `OutputPathPolicy(EngineOptions, ProjectSettings?)` (null settings: engine-write checks only), `ManifestStore(EngineOptions, ICanonicalJson, IOutputPathPolicy)`, `RunJournal(EngineOptions, IOutputPathPolicy)`, `RunLock(EngineOptions, IOutputPathPolicy)`, `DiffGenerator()`, `OutputWriter(EngineOptions, IOutputPathPolicy, IManifestStore, IDiffGenerator)`, `FormatterRunner(EngineOptions)`, `PostProcessor(EngineOptions, IFormatterRunner)`, `SnapshotStore(EngineOptions, ICanonicalJson, IOutputPathPolicy)`, `SchemaDiffer()`; W6's plan and job stores take `(EngineOptions, IOutputPathPolicy)` too. The composition root is `EngineServices` (W6, `Generation/`): `static EngineServices Create(EngineOptions options)` builds the long-lived components and `EnginePaths` (`new OutputPathPolicy(options, null)`), passed to every writer above; `CreateHasher(...)`, `CreatePathPolicy(ProjectSettings)`, `CreateWriter(IOutputPathPolicy)` and `CreateRenderer()` (a `Renderer` over a fresh `TemplateCache`) build the per-run ones. The public constructors of `ModelStore` and `GenerationService` call `EngineServices.Create`; an internal overload taking an `EngineServices` lets tests substitute fakes.

| Workstream | Owns | Implements | Consumes |
| --- | --- | --- | --- |
| W1 Loader | `Json/`, `Loading/`, `ModelStore.cs`, `schemas/v1/`, tests `Json/` `Loading/` `Store/`, `fixtures/models/` | `IModelLoader`, `ISchemaRegistry`, `ICanonicalJson`, `JsonPositionLocator`, `ModelStore` | `IModelValidator` (save checks), `IOutputPathPolicy.CheckEngineWrite` |
| W2 Validator | `Validation/`, tests `Validation/`, `fixtures/validation/` | `IModelValidator`, built-in rules, extension evaluation, `SarifWriter` | `ISchemaRegistry`, `IScriptSandboxFactory` |
| W3 Resolver | `Resolution/`, `Text/`, tests `Resolution/` `Text/` | `IModelResolver`, R-types, `Casing`, `Inflector`, dialect maps | none (`ModelSnapshot` only) |
| W4 Sandbox | `Scripting/`, tests `Scripting/` | `IScriptSandboxFactory`, `JsModelProxy` | R-types, `IReadRecorder` |
| W5 Renderer | `Rendering/`, tests `Rendering/`, `fixtures/templates/` | `IRenderer`, `ITemplateCache`, `DelimiterTranslator`, `BuiltinHelpers`, `TrackingTemplateContext` | `Text/` (`Casing`, `Inflector`); per run: `RenderContext` |
| W6 Planner and orchestration | `Planning/`, `Generation/`, `Jobs/`, `GenerationService.cs`, `JobQueue.cs`, tests of the same, the determinism job in `.github/workflows/ci.yml` (with W10) | `IPackLoader`, `IUnitPlanner`, `IChangeDetector`, `IDependencyHasher`, `IUnitStateStore`, `GenerationService`, `JobQueue`, `EngineServices` | every stage interface; `ModelStore` |
| W7 Writer | `Writing/`, tests `Writing/` | `IOutputWriter`, `IOutputPathPolicy`, `IManifestStore`, `IRunJournal`, `IRunLock`, `IDiffGenerator` | `ICanonicalJson`; per run: `IUnitStateStore`, `ManifestSet` |
| W8 Formatters and schema diff | `PostProcessing/`, `SchemaDiff/`, tests of the same | `IPostProcessor`, `IFormatterRunner`, `ISnapshotStore`, `ISchemaDiffer` | `IFormatterRunner`, `ICanonicalJson`; per run: `IOutputPathPolicy` |
| W9 CLI | `src/Maquettiste.Cli/`, `tests/Maquettiste.Cli.Tests/` | commands, parser, console progress | `ModelStore`, `GenerationService`, `SarifWriter`, `ISchemaRegistry`, `BenchmarkHarness` |
| W10 Packs | `packs/`, `tests/Maquettiste.Packs.Tests/`, `fixtures/golden/`, the determinism job in `ci.yml` (with W6) | `sql-ddl` (table DDL per dialect, one schema script per database, `once` migrations from `schema_diff`) and `csharp-dapper` (entity `pair` files, enums, value objects, a repository per mapped entity, one registration file per package through file blocks, `types/csharp.json`) | template contract §8–9 |
| W11 Bench | `bench/`, `.github/workflows/bench.yml` | `SyntheticModelGenerator`, `BenchmarkHarness`, `bench/packs/fanout`, `baseline.json` | `ICanonicalJson`, `GenerationService` |

`ci.yml` is scaffold-owned except its cross-OS determinism job (a SHA-256 listing of the billing fixture's generated output compared between ubuntu and macos), which W6 and W10 add once generation works; that job is the only edit they make to it. Until a producer lands, consumers test against `ModelBuilder` and hand-built DTOs. The planned merge order is W1, W3, W4 → W2, W5, W7, W8 → W6 → W9, W10, W11, but every workstream can start on day one.

## 19. Decisions

| Id | Decision | Why |
| --- | --- | --- |
| D1 | Ids are uppercase ULID strings, strictly validated; SPEC example ids are illustrative (SPEC erratum E1) | Strict ids catch typos; `string` keeps JSON and API plain |
| D2 | Stereotypes are referenced by their immutable kebab `key` (a property separate from the renamable `name`; a save changing it is MQ3020); the index records each `stereotypes[i]` as a reference to the stereotype's id. Tags are labels (D41); categories and all elements are referenced by id | Follows the S6 example (keys in `stereotypes`) without breaking S5's "names can change without breaking links"; keys read well in templates (SPEC erratum E4) |
| D3 | `decimal` is a keyword plus `precision`/`scale` facets, not `decimal(p,s)` syntax | One facet model for all types |
| D4 | Attribute default split into literal `default` and `defaultExpression`; MQ3019 rejects a literal that does not fit the type and points `"now"` and friends at `defaultExpression` (SPEC erratum E5) | A literal `"now"` stays distinguishable from the expression |
| D5 | `ordered` lives on the relation end, not the relation | "Whether one side keeps a position" needs to name the side |
| D6 | `Database.Packages` scopes which entities map to a database; `Mapping.Ignore` excludes single ones | S10 "entity in several databases" needs a default |
| D7 | No `lifecycle` field in phase 1 (SPEC erratum E3) | Process schema is phase 3 |
| D8 | Relations with attributes default to a junction table (`Conventions.RelationsWithAttributes`) | Phase 1 answer to the S22 open question; configurable per project and database, and per relation through `Mapping.Shape` |
| D9 | Synthesized table files hold physical overlays (names, native types, extra columns, indexes, checks) and target exactly one synthesized table: `entity`, `entity` + `attribute` (child table) or `relation` (junction) (the `enum` lookup overlay is retired with the enum lookup-table option, MQ7012); designed and imported tables name none of these (the entity binding lives in `Mapping.Table` only). Mappings hold shapes and strategies | S9 "table files store only overrides"; no field has two homes; the schema enforces it |
| D10 | File blocks are a `file path content` helper fed by `capture` | Scriban has no user-defined block statements |
| D11 | Custom delimiters through a translator to `{{ }}` | Scriban 7.5.0 has no delimiter option |
| D12 | Script helpers must be pure; globals are frozen; determinism tests enforce it | Closure state cannot be frozen by Jint |
| D13 | CLI index cache defaults to the OS user cache folder, not `.maquettiste/.cache` | S4: `.cache` holds the journal only; keeps the cache off bind mounts |
| D14 | Manifest is JSON with one entry per line; `r:` and `o:` hash prefixes (E42 adds `b:` and `bc:` for blocks) | Valid JSON and line-level diffs; region edits and owned files need distinct checks |
| D15 | Schema snapshots at `.maquettiste/snapshots/<db>.json`, kept only when a pack sets `usesSchemaDiff`; migrations use `once` | No surprise files; each migration is written once and kept |
| D16 | `maquettiste.json` and `pack.json` refer to databases and packs by name | Hand-edited settings and portable packs cannot know ids |
| D17 | Output paths are repo-relative after the pack's `output` prefix; the longest matching root decides commit and built (E42: no root kinds remain; an entry may name a single file) | One rule for allow, manifest location and `--check` |
| D18 | Dependencies are tracked per resolved object (the union of its source files) plus kind-set keys | Over-approximation is safe; element granularity keeps read sets small |
| D19 | `output` is evaluated at render time; duplicate paths are caught in stage 8 | Keeps the incremental plan stage cheap at 100k units |
| D20 | `--check` renders every unit and covers committed roots (E42: every root); hand edits exit 3, other drift exits 2 | Matches S12 and S17 |
| D21 | Journal lines are flushed to the OS per file and fsynced per pack | Per-file fsync would break the write budget |
| D22 | Every generation mode, dry run included, takes the run lock | Manifests read mid-apply would be inconsistent |
| D23 | Usage errors, busy and cancellation exit 4; template and script errors exit 1; precedence 4, 1, 3, 2 | S17 defines only five codes |
| D24 | Schemas carry no `$id`; the engine assigns `https://maquettiste.invalid/schemas/v1/<file>` bases | Editors resolve relative refs locally; nothing can reach a network |
| D25 | Packages live in `model/packages/`; tags and categories are one file each; one file per stereotype | S4 lists no packages folder; vocabularies are small |
| D26 | Casing lowercases acronyms (`HTTPServer` → `HttpServer`, `http_server`) | One deterministic rule |
| D27 | No file-system abstraction, logging package or command-line library | Fewer dependencies (host-contracts 2); real temp dirs test symlinks honestly |
| D28 | `generate --roots` added to S17's options (removed 2026-10-02, E42) | S17's CI row runs built and committed roots separately |
| D29 | `once` and companion files are never deleted; orphaned ones leave the manifest | The team owns them (S12) |
| D30 | Bench uses a fanout pack to reach 100,000 files and stage barriers for per-stage timings | The two example packs alone emit about 40,000 files; overlapped stages have no clean per-stage wall time |
| D31 | The `$schema` property is `SchemaPath` in C# on every document record | `Table`, `View` and `Sequence` already have a `Schema` reference; one name for both would collide in JSON |
| D32 | The tag vocabulary's declared tags are `definitions`, not `tags` | `tags` is already the element's own tag list (S5) |
| D33 | Relation ends carry a required `id`, which the SPEC Section 7 example omits (SPEC erratum E2) | Physical column keys (`<endId>.<keyAttrId>`), `Mapping.ForeignKeyEnd` and the reverse index need a stable handle per end; the SPEC's end table does not forbid it and its example ids are illustrative anyway (D1) |
| D34 | Schema validation reports leaf keyword failures only (`properties`, `items`, `allOf`, `$ref`, `then`, `else` and similar applicator summaries are dropped) | One MQ1002 per real failure, with the instance pointer of the offending value |
| D35 | Every `name` schema declares `"default": ""` | `ElementBase.Name` defaults to `""`; the canonical writer must not write an empty name for overlays |
| D36 | `order` is the canonical array position: the writer stable-sorts `x-sort: order` arrays (attributes, categories), missing = 0 | S6 and S11 make `order` the serialization position; stable sort keeps files without `order` as written |
| D37 | `EntityKey` holds only its attributes and strategy; the key sequence is chosen per database by the key column's overlay, else synthesized by `Conventions.SequenceName` | A sequence is physical and an entity may map to several databases (S10); no field has two homes (D9) |
| D38 | Resolved objects list `r:<id>` keys for the entities and relations they derive from; `r:<id>` hashes the set of referring files | `e:` keys of the last render cannot see a mapping, overlay or relation created afterwards; S13 incremental runs must equal forced runs |
| D39 | A plan stores its request and every unit's full output list with disk hashes; apply feeds the writer from the plan and is `Stale` when any planned path changed on disk | Apply without a second render must not orphan unchanged files or overwrite region edits (S19: apply writes nothing outside the plan) |
| D40 | One engine-write guard (`EngineServices.EnginePaths`) is a constructor argument of every component that writes | S23 "every file the engine writes goes through the path allowlist", by construction rather than by review |
| D41 | Tags are free-form labels (1 to 64 characters, no whitespace or control characters) | S5 "Tag: free-form label"; whitespace would make `where.tags` and CLI filters ambiguous |
| D42 | The output allowlist is `outputs.allow` (with `outputs.deny`) | S4 and S19 call them `allow` and `deny` rules |
| D43 | Physical-key properties are `[ElementRef(Keyed = true)]`: every id segment of the key is a reference | Deleting a table or attribute must report referrers that hold it inside a key (host-contracts 13); dependency keys need the edges |
| D44 | `InvariantGlobalization` only on the CLI and bench apps; culture analyzers are errors in `src/`; tests run with culture data | The engine runs under the editor host's culture; the analyzers are silent when invariant globalization is on |
| D45 | One-shot hosts keep a last-run record (§11) that answers an unchanged run from the same engine build; no persisted resolved model | A no-op CLI `generate` (every build, S17) paid about 2.8 s to load, validate and resolve 26,000 files; a serialized resolved model would be a derived graph whose missed member silently changes output, and would decode about as slowly as it resolves. The record trusts stats only where the index cache (§5) and the skip check (§11) already do (model files, sidecars, outputs) and for engine-only cache files (unit states, built-root manifests); templates, committed manifests and snapshots are compared by content, and the key names the engine build, not only the contract version, so a rebuilt tool never replays another build's diagnostics |
| D46 | Mapping is explicit: a database holds the entities its `byConvention` takes (`all`, the domains in `packages` with their sub-domains, or `none`) plus every entity a mapping element names; `ignore` still removes one. No database is special. A file without `byConvention` keeps D6 (every entity when `packages` is empty), so existing models resolve byte-identically; the editor writes the member on every new database (`none` by default). MQ4012 (info) reports an entity in no database when the model has one; MQ4013 (warning) a `packages` list that `all` or `none` leaves unused; a missing package id stays MQ2001 | The owner, round 7: "why are tables automatically generated for the entities ... the mapping isn't always going to be straightforward"; a second database copied every table of the first. The engine synthesizes no persistence the project did not ask for (errata E24) |
