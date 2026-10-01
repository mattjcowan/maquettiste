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
| *(root)* | `EngineOptions`, `EngineVersion`, `IIdGenerator` (Scaffold); `ModelStore` (W1); `GenerationService`, `JobQueue` (W6) | see left |
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
    IReadOnlyList<AlternateKey> AlternateKeys = []; IReadOnlyList<ModelAttribute> Attributes = []; }
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
public sealed record ScalarType : Element { string? Package; req string Base; int? Length; int? Precision; int? Scale; AttributeValidation? Validation; }
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
public sealed record OutputRoot { req string Path; bool Commit; }                        // repo-relative folder
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
    Delimiters? Delimiters; PairCompanion? Companion;       // Companion required when Mode = Pair
    IReadOnlyList<string> Transforms = []; }                // pre-render transforms; results merged into `data`
public sealed record UnitWhere {                            // lists match any value; every set filter must match
    IReadOnlyList<string> Tags = []; IReadOnlyList<string> NotTags = []; IReadOnlyList<string> Stereotypes = []; IReadOnlyList<string> NotStereotypes = [];
    IReadOnlyList<string> Categories = [];                  // id or name; descendants match
    IReadOnlyList<string> Packages = []; IReadOnlyList<string> NotPackages = [];   // id or qualified name; sub-packages match
    string? Database;                                       // name: tables in it; entities/relations mapped (not ignored) in it
    bool? Abstract; string? Script; }                       // Script: JavaScript filter name
public enum OutputMode { Overwrite, Once, Regions, Pair }
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
public enum RootSelection { All, Committed, Built }             // Check forces Committed
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
public sealed record OutputRootInfo(string Path, bool Commit);
public sealed record OutputFile(string Path, ReadOnlyMemory<byte> Content, string ContentHash, string ManifestHash,
    OutputMode Mode, FileRole Role, OutputRootInfo Root, bool ContentOmitted = false);   // omitted: plan apply of an Unchanged file (§15)
public sealed record ProcessedUnit(RenderedUnit Rendered, IReadOnlyList<OutputFile> Files, IReadOnlyList<Diagnostic> Diagnostics, bool Failed);
public enum FileChangeKind { Added, Modified, Deleted, Unchanged, HandEdited, Kept, OrphanedOwned, Conflict }
public sealed record FileChange(string Path, FileChangeKind Kind, string Pack, string UnitKey, string? OldHash, string? NewHash, string? Diff);
public sealed record WriteSummary(IReadOnlyList<FileChange> Changes, IReadOnlyList<Diagnostic> Diagnostics, int Written, int Deleted);
```

`Changes` lists every file except `Unchanged` ones, sorted by path; `Diff` is set only when requested.

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
    bool AllPacks, RootSelection Roots, bool IncludeDiffs, IRunJournal? Journal, IUnitStateStore State,   // orphans only within Roots
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
    public IReadOnlyList<ManifestEntry> Entries(string pack, bool committed);                                   // ordinal by Path
    public ManifestSet WithJournalOverlay(IReadOnlyList<JournalRecord> records); }                              // resume (§12.4)
public sealed record JournalRecord(string Type, string? Pack, string? Path, string? Hash, string? Unit);         // t: begin|write|delete|pack|end
public interface IManifestStore { Task<ManifestSet> LoadAsync(IReadOnlyCollection<string> packs, CancellationToken ct);   // W7
    Task SavePackAsync(string pack, bool committed, IReadOnlyList<ManifestEntry> entries, CancellationToken ct); }
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

`GenerationService.RunAsync` (W6) runs: lock (§12.4) and journal resume → 1 load (`ModelStore.GetSnapshotAsync`, which rescans by stat) → 2 validate; any error stops the run → pack load and 3 resolve → schema diffs (§14) → 4 plan → 5 skip → 6, 7 and 8 as a streaming pipeline: renderer workers feed a bounded channel (capacity `2 × jobs`) into post-processing workers, which feed the writer's bounded queue (capacity 256 files). Units are ordered pack by pack, so the writer can close a pack's manifest as soon as that pack's last unit arrives. With `StageBarriers = true` (bench only) each of stages 6 to 8 finishes before the next starts, so each has a clean wall time. **Dry run** runs stage 8's comparison with no side effects: no journal, manifest, unit-state, snapshot or output write, and a `FileChange` with `Diff` per file. **Check** is a dry run that renders every unit (the cache is ignored), keeps committed roots only, and hashes every manifest file on disk.

## 5. Loader, index and `ModelStore` (W1)

- **Load**: enumerate `maquettiste.json`, `model/**/*.json`, description sidecars, `extensions/*.json` (into `Extensions`) and `extensions/rules/*.js` (into `RuleScripts`); read in parallel (`jobs` readers); for each file parse → `ISchemaRegistry.Evaluate` → deserialize to the kind's CLR type. A file that fails is left out of the snapshot and its diagnostics go into `LoadDiagnostics` (MQ1001 JSON, MQ1002 schema, MQ1004 duplicate id, MQ1006 bad ULID, MQ1007 unsupported `formatVersion`). Non-canonical files load, with warning MQ1003. Then `ModelSnapshot.Create`.
- **Index cache** at `EngineOptions.CacheDirectory/index.v1.bin` (never under the bind mount; S13): header (`MQIX`, format 1, engine version) then one record per file: path, length, last-write ticks, SHA-256, file bytes, sidecar hash. On open, a file whose length and mtime match its record is taken from the cache without reading it through the mount; any other file is read and hashed. `VerifyHashes` (and `RescanAsync(verify: true)`) re-hashes everything. The cache is rewritten atomically after a load that changed anything. Records are content-addressed, so a cache from another checkout is harmless.
- **ETag** = `ElementDocument.Hash`, the SHA-256 of the file bytes as lowercase hex, 64 characters.
- **Change hook**: `RefreshAsync(paths)` re-reads only those paths and returns a `ChangeSet`; a path whose hash equals the indexed hash yields nothing, so the store's own writes (already indexed) never echo as changes (host-contracts 19). `OnChanged` subscribers get every non-empty `ChangeSet`. The CLI's `--watch` and the future functions watcher call `RefreshAsync`.
- **Saves** go through the canonical writer, a `SemaphoreSlim` (one writer per store), staged temp files in the target folder, then `File.Move(overwrite: true)`. A save validates the element and every element that references it before touching disk. A save that changes an id, or a stereotype's `key`, is `Invalid` (MQ3020 for the key). Every file the store and the loader write (model files, the index cache) is checked first with `EngineServices.EnginePaths.CheckEngineWrite` (§12.1).

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
| MQ1xxx file | 1001 invalid JSON · 1002 schema violation · 1003 not canonical (warning) · 1004 duplicate id · 1005 file in wrong folder or name mismatch (warning) · 1006 invalid ULID · 1007 unsupported format version · 1008 `.schema` out of date (warning) · 1009 a second tag vocabulary or category tree (the ordinally first is used) |
| MQ2xxx references | 2001 dangling reference · 2002 reference to wrong kind · 2003 unknown stereotype · 2004 stereotype not applicable to kind · 2005 unknown category · 2006 undeclared tag (error when `strict`, else info) · 2007 attribute validation names an unknown rule |
| MQ3xxx conceptual | 3001 duplicate name in scope · 3002 inheritance cycle · 3003 package cycle · 3004 category cycle · 3005 entity without key · 3006 key names missing attribute · 3007 duplicate attribute name, virtual included · 3008 relation end count or kind mismatch · 3009 navigation collides with a member · 3010 invalid cardinality · 3011 set-null on a required end · 3012 enum member name or code duplicated; flags values not powers of two · 3013 facet invalid for type · 3014 scalar base not built-in · 3015 value object containment cycle · 3016 composition child with two owners · 3017 default looks like a credential (warning; S19) · 3018 invalid name for kind · 3019 literal `default` does not match the attribute's effective type (a string on a numeric, bool or temporal type, a non-ISO 8601 temporal string, a value outside an enum's member names or codes, a number out of range); when the literal is a known expression name (`now`, `today`, `new-uuid`, `new-ulid`) the message says "use defaultExpression" · 3020 a save changes a stereotype's key |
| MQ4xxx physical | 4001 identifier over dialect limit · 4002 duplicate table name in schema · 4003 duplicate column name · 4004 two mappings for one target · 4005 FK column type mismatch · 4006 native type unknown to dialect map, a plain name without quotes or schema (warning) · 4007 overlay references a missing column key · 4008 constraint or index names a missing column · 4009 mapping option invalid for the element · 4010 view body missing for the database dialect · 4011 a relation whose ends are both bound to designed or imported tables (`Mapping.Table`) names no `Mapping.ForeignKey`, or a designed `JunctionTable` without an `Ends` entry per end · 4012 an entity lands in no database (info; D46) · 4013 a database's `packages` unused because `byConvention` is `all` or `none` (warning; D46) · 4014 a convention package entry or an entity mapping names a schema its database does not declare (E26) · 4015 a schema operation refused: unknown schema or taken name, occupants and no target, or the default and no new default (E26) · 4016 a quoted or schema-qualified native type naming a type the database defines, once per type and database with its column count (info) |
| MQ5xxx extensions | 5001 property fails extension schema · 5002 rule script error · 5003 rule exceeded a sandbox limit · 5004 invalid extension file |
| MQ6xxx packs | 6001 invalid pack.json · 6002 engine range not satisfied · 6003 template parse error · 6004 output path refused · 6005 duplicate or case-colliding output path · 6006 render error · 6007 sandbox limit exceeded · 6008 formatter failed or version mismatch · 6009 hand edit · 6010 protected region lost · 6011 text outside file blocks in a unit without output (warning) · 6012 non-deterministic builtin used · 6013 helper name collides with a builtin · 6014 unit names an unknown formatter (warning) · 6015 regions mode on a built root · 6016 script error · 6017 selector returned an unknown id · 6018 stale schema snapshot |
| MQ8xxx project | 8001 a `branding.colors` value that is not `#rrggbb` or `#rgb` · 8002 `branding.icon` is not `branding/<name>.svg` or `.png` in the model folder, or the file does not exist · 8003 the icon is not a safe SVG (scripts, event handlers, external references) or a PNG of at most 512 KB. Whole-model validation; MQ7xxx (reference data and localization) is in reference-types-seeds-localization.md |
| `x/<id>` | JavaScript rules; the severity comes from the rule |

**Native types (MQ4006, MQ4016; status: built 2026-10-01).** As built on 2026-10-01, the validator reads a column's native type before the lookup: arguments and array brackets go, identifier quotes (`"..."`, `[...]`, backticks) are stripped and a schema or other prefix is dropped, so `"public"."citext"` is the known `citext`. A name is known when it is a native type of the dialect, the base of a value of the dialect's effective type map (`Resolution/Dialects/*.json` with the project's `typeMaps` applied), or the snake or kebab name of one of the model's reference types or enums, with or without the `_t` suffix sql-ddl gives a reference type stored as a native type. Any other quoted or qualified name is a type the database defines, which the validator cannot check: MQ4016 (info) is reported once per type and database, on the first column that uses it in path order, with the number of columns and tables that use it (a model read from a database had hundreds of MQ4006 warnings for one enum type). A plain unknown name stays MQ4006 (warning) on each column. Scoped validation revisits the first holder of such a type when another table using it changes, and the tables whose native types carry a reference type's or enum's name when that element changes.

**Extension schemas**: for each element (and attribute, enum member, column), merge the stereotype `DefaultProperties` (stereotype order, later wins) under the element's own `properties`, then evaluate the result against each applicable `ExtensionSchema` as `{ "type": "object", "properties": …, "required": … }` through `ISchemaRegistry` (MQ5001, pointer `/properties/<name>`).

**JavaScript rules** live in `extensions/rules/*.js` and register with `maquettiste.rule({ id, severity, kinds, check(element, model, report) })`. `element` is a frozen copy of the element's canonical JSON; `model` offers `get(id)`, `all(kind)` and `referencesTo(id)`, also frozen; `report(message, { pointer, severity })`. Rules run through a validation pool (§10) in parallel over elements.

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
               Lifecycle (RProcess?, phase 3)
RAttribute   : Owner, DeclaringEntity, Type (RType), Required, Default, DefaultExpression, Length, Precision, Scale (effective, scalar facets applied),
               Collection, Unique, Indexed, ReadOnly, Immutable, Derived, Sensitive ("pii"|"secret"|null), Validation, Order, IsInherited, IsVirtual, FromStereotype
RType        : Kind ("builtin"|"enum"|"value-object"|"scalar"), Name, Builtin (effective keyword; null for enum/value object), Enum, ValueObject, Scalar
REnum        : Flags, Members (REnumMember: Id, Name, DisplayName, Description, Value, Code, Properties); RValueObject: Attributes; RScalarType: Base, facets, Validation
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

**7.4 Relation shapes** (S7, binary relations; the mapping's `Shape` overrides). **Binding to existing tables** (S5 "relation to foreign key or junction table"): when the dependent end's entity is bound to a designed or imported table (`Mapping.Table`), the relation mapping's `ForeignKey` names the existing foreign key in that table that realizes the relation, and the resolver uses its columns instead of synthesizing FK columns into a full-definition table; when both ends are bound to such tables and no `ForeignKey` is named, it is MQ4011. A designed `JunctionTable` binds each end through `Ends` (`{ end, foreignKey }`, a foreign key of the junction table); a missing end is MQ4011. When only the principal is bound, FK columns are synthesized into the dependent's synthesized table as usual. one to one (both `Max` 1) → FK with a unique constraint in the dependent end's table, where the principal is the end with `Min` 1 (on a tie `Ends[0]` is the principal unless `Mapping.ForeignKeyEnd` names the dependent end); one to many → FK in the table of the many end's entity, referencing the end with `Max` 1, nullable when that end has `Min` 0; many to many without attributes → junction with a composite PK of the FK columns; any relation with attributes → `Conventions.RelationsWithAttributes` (default junction: extra columns hold the attributes); three or more ends → junction with one FK per end; promoted → a synthesized `REntity` with the relation's id and two many-to-one relations whose ids are `<relationId>.<endId>`. `AllowDuplicates` or an `Ordered` end gives the junction a surrogate `id` PK; `Ordered` adds the `OrderColumn`. FK `OnDelete` is the principal end's intent (`None` → no action, `Composition` → cascade by default); junction FKs cascade unless the end says `restrict`.

**7.5 Keys, inheritance, enums, value objects.** The `sequence` identity strategy is resolved per database (D37): the key column's overlay in that database's table file (`generated: sequence`, `sequence: <id>`) names the sequence; without one, the resolver synthesizes a sequence named by `Conventions.SequenceName` (default `{table}_seq`) with the key of §7.3, and `RKey.Sequences` and the key column's `RColumn.Sequence` point at it. TPH: the root table holds every attribute in the hierarchy plus the discriminator column (string, length 64), with values defaulting to entity names. TPT: each entity's table holds its own attributes, and its PK is also an FK to the base table. TPC: each concrete entity's table holds all inherited attributes, and abstract entities get no table. Enum storage: `int` (member `Value`, else ordinal) or `string` (`Code`, else name; length = longest). The `lookup` option (a synthesized lookup table with an FK) is retired: a settings or mapping file that still sets it does not load and reports error MQ7012 naming the conversion to a reference type, whose storage strategy the templates realize (principle: the engine models intent, templates decide persistence). Value objects: `embedded` (prefixed columns, recursive), `table` (child table keyed by the owner PK), `json` (one `json` column); collections are `table` (owner FK plus `position`) or `json`.

**7.6 Dialect types.** `Resolution/Dialects/<dialect>.json` embeds the default map from keyword to native pattern (`"decimal": "numeric({precision},{scale})"`, `"string": "varchar({length})"`), overridden entry by entry by `typeMaps.<dialect>` in `maquettiste.json`, then by `Column.NativeType`. Missing facets take convention defaults.

## 8. Template packs (W6 loads and plans; W5 renders)

- **Discovery.** Every `templates/<name>/pack.json` whose `name` equals its folder is a pack; `packs.<name>.enabled: false` turns it off. Packs run in ordinal name order. `types/<target>.json` files are type maps for `type_of` (keyword → language type, plus `"nullable": "{type}?"` and `"collection": "IReadOnlyList<{type}>"` patterns). Built-in dialect targets need no file.
- **`for`**: `model` (one unit, no element), `each package|entity|relation|enum|value object|table|view|sequence|reference type|seed|locale|process|actor|scenario` (one unit per resolved element; `table`, `view` and `sequence` cover every database (§7.0a); the unit key of `each reference type`, `each seed`, `each process`, `each actor` and `each scenario` is the element id, and the scope alias is `reference_type`, `seed`, `process`, `actor` or `scenario`; `where` on the three phase 3 scopes takes tags, stereotypes, categories and packages (a scenario's package is its process's, an actor has none, so a package filter matches no actor) and refuses `database` and `abstract` at pack load (MQ6001); `each locale` plans one unit per declared locale, the default first then ordinal, with the `RLocale` as `element` and `locale`, unit key `locale:<tag>`, and rejects `where` at pack load), or `select <name>` (a JavaScript selector that returns elements or ids; unknown ids fail with MQ6017). `generation["*"|pack].skip` on an element drops its units. `where` filters as in §2.5; `where.database` also picks `mapping` for entity and relation units.
- **Template context.** Variables: `model`, `element`, a scope alias (`package`, `entity`, `relation`, `enum`, `value_object`, `table`, `view`, `sequence`, `reference_type`, `seed`, `locale`, `process`, `actor`, `scenario`), `pack` (`name`, `version`, `params`), `mapping` (`REntityMapping`/`RRelationMapping` for `where.database`, else the only one, else null), `mappings` (by database name), `schema_diff` (database name → `SchemaDiffResult`), `hints` (merged `generation["*"]` and `generation[pack]`), `data` (transform results), `unit` (`id`, `key`).
- **Output.** `Output` is rendered with the same context (tracked like the body) and prefixed with `PackSettings.Output`. A template emits more files with `{{ file "path" content }}`, usually after `{{ capture content }}…{{ end }}` (D10). With `Output` null, only file blocks are written. Block paths take the same prefix and the unit's mode, except `pair`, whose blocks are `overwrite`.
- **Modes.** `overwrite`, `once` (written only when missing; recorded as owned), `regions` (committed roots only; MQ6015), `pair` (`Output` rendered every time with `Template`; `Companion.Template` rendered to `Companion.Output` only when that file is missing, as owned).
- **Built versus committed.** A file's root is the longest `outputs.allow` path that contains it. `Commit` decides the manifest location (§12.2), `--check` coverage and the `.gitignore` entries `init` writes.

## 9. Rendering (W5)

- **Template cache**: `internal interface ITemplateCache { Template Get(LoadedPack pack, string path, Delimiters? delimiters); }` returns a parsed Scriban `Template`, keyed by (pack, path, file hash, delimiters), in a `ConcurrentDictionary` owned by the renderer instance for one run. Renderers are per run: `EngineServices.CreateRenderer()` builds one with a fresh `TemplateCache` for each run and each preview, so nothing accumulates in a long-lived host. Parse errors → MQ6003 with line and column.
- **Delimiters**: Scriban 7.5 has no custom delimiter option, so `DelimiterTranslator` rewrites a template that uses `Open`/`Close` into `{{ }}` form before parsing. Code spans become `{{…}}`, and a text span containing `{{` or `}}` is wrapped in an escape block `{%{…}%}` with enough `%` that the text cannot close it. Line breaks are kept one for one, so error positions map back unchanged.
- **Context** per unit: a fresh `TemplateContext` with `StrictVariables = true`, `EnableRelaxedMemberAccess = true`, `EnableRelaxedTargetAccess = false`, `LoopLimit` and `RecursiveLimit` from `SandboxLimits`, `NewLine = "\n"`, invariant culture, `MemberRenamer` = snake_case, and a `MemberFilter` that admits only public get-only properties of resolved-model types and helper result types. No `ScriptObject.Import` of arbitrary CLR types. `include` resolves through a `TemplateLoader` confined to the pack folder, which records `t:<pack>/<path>`. Builtins `date.now`, `math.random`, `object.eval` and `object.eval_template` are replaced by functions that fail with MQ6012.
- **Tracking proxy**: `TrackingTemplateContext` overrides `GetMemberAccessorImpl`. For any `IResolvedObject`, the accessor records the object's `Dependencies` into the unit's `IReadRecorder` on every member read. Every list of resolved objects is an `RList<T>`; enumerating it, indexing it or reading `size` records its `MembershipKeys` (the kind-set and element keys that decide which items it holds, set by the resolver: `model.entities` → `k:entity`; `database.tables` → `k:entity`, `k:relation`, `k:enum`, `k:table`, `k:mapping`; and so on). `lookup` of a missing id records `e:<id>`, so creating that element later re-renders the unit. Helpers receive the same recorder. Reference data (reference-types-seeds-localization.md §1.5, §2.5): a row (`RRow`, `RSeedRow`) records its seed's keys (the seed's `e:` key and its target's); `reference_type.rows` records `k:seed`, `r:<type id>` and the `e:` key of each of the type's seeds, so editing one seed re-renders only the units that read that seed's rows; a storage choice (`RStorageChoice`) records `s:referenceData`, the type's `e:` key and the database's; `row <type> "<code>"` records the type, its rows' membership and the row; `row_uuid <row>` returns the row's ULID bits as a UUID. Localization helpers (reference-types-seeds-localization.md section 3.7): `display_name`, `plural_name`, `description_of`, `label_of` `<x> [locale]` and `translate <x> "<field>" [locale]` walk the locale's chain (`RLocale.chain`: the locale, its `fallbacks` or supported truncations, the default) down to the default-locale value; without a locale argument they use the unit's `locale` in an `each locale` unit, else the default; `has_translation <x> "<field>" <locale>` reads one locale. `model.locales` lists the `RLocale`s.
- **Helpers** (`BuiltinHelpers`, all pure): `pascal camel snake kebab upper_snake` (split on non-alphanumerics, lower→upper and acronym→word boundaries; digits join the preceding word; words lowercased, then styled: `HTTPServer2Id` → `http_server2_id`); `pluralize singularize` (fixed English rules, `inflection` overrides, element `pluralName` wins); `type_of <attr|column|type> "<target>"` (pack type map or dialect); `sql_quote <name> "<dialect>"` (pg/sqlite/oracle `"x"`, sqlserver `[x]`, mysql `` `x` ``, by the database's `Quoting` and embedded reserved-word lists); `sql_literal <value> "<dialect>"` (strings single-quoted with `''`, sqlserver `N'…'`; booleans `true/false` on pg, `1/0` elsewhere; dates ISO 8601 quoted; null `NULL`); `indent <text> <n|string>`, `dedent`; `escape_md escape_xml escape_json`; `json <value>` (canonical, compact); `has_stereotype has_tag in_category`; `lookup <id>`; `banner "<comment prefix>"` → `<prefix> Generated by Maquettiste (<pack>/<unit id>). Do not edit; changes are overwritten.` (no timestamp, no version); `file <path> <content>`; `state_path <state>` (a process state's dotted path, `Fulfilment.Shipping.Packed`; a state id works too; records the state); `iso_duration_ms <text>` (the milliseconds of an ISO 8601 duration with the interpreter's fixed spans, a month 30 days and a year 365; a text that does not parse fails the unit with MQ6006 naming it). Pack helpers from JavaScript register under their own names and fail with MQ6013 on a collision.
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
| `l:<locale>:<ownerId>` | `LocalizationIndex.OwnerHash`: `H` over the owner's effective entries in that locale (every field of the owner and its sub-elements, ordinal by id, with their `src` fingerprints) and their description sidecars' hashes. A helper records one per consulted chain locale up to the one that answered, plus the owner's `e:` key when the default text answered (reference-types-seeds-localization.md section 3.8) |
| `t:<pack>/<path>` | file hash of a template or partial |
| `d:<databaseId>` | `SchemaDiffResult.Hash` |
| any key that no longer resolves | `"absent"` |

`StaticHash = H("mq-unit-1", EngineVersion.Value, pack name, canonical PackUnit JSON, ScriptsHash, canonical effective parameters, output base, formatter settings or "none", unit key)`. `InputHash = H(StaticHash, key₁, hash₁, key₂, hash₂, …)` over the unit's recorded read keys in ordinal order. A unit is **skipped** when not forced, not in check mode, its `UnitState` exists, the `InputHash` recomputed from the recorded keys equals the stored one, and every recorded output still has the same manifest hash and the same length and mtime on disk (a stat mismatch re-hashes the file). Owned outputs only need to exist. Skipped units keep their manifest entries and state. Because resolved objects carry `r:` keys (§7), a new file that starts contributing to an existing resolved object (a mapping, a table overlay, a relation) changes the recomputed `InputHash` of every unit that read that object, so incremental runs and `--force` runs agree. Rendered units store their new `UnitState` (read keys, hash, outputs with stat) in `CacheDirectory/units/<pack>.v1.bin` when their pack completes, and only in apply mode. A unit whose render fails keeps its previous outputs and state.

**Which units an entity edit re-renders.** An ordinary edit of one element (no seed, reference type, vocabulary or locale shard, and no id-carrying member of the element changed) patches the model index instead of rebuilding it, and changes only the edited file's `e:` key, the `r:` keys of the ids the file references, and the `d:` key of a database whose schema diff it changes; every other key keeps its hash. A unit therefore re-renders only when one of its own recorded reads is among those keys, and `s:referenceData`, `s:localization` and `l:` keys are recorded only by the units that read those facts (a reference type's storage choice, a localization helper, an `each locale` unit), so a pack that reads neither never carries them. `IncrementalUnitCountTests` (bench tests) enforces this on a small synthetic model with every benchmark pack: after the benchmark's one-entity edit, every unit with a changed `e:`, `r:` or `l:` read renders, and no unit renders outside those plus the readers of a `d:` key. On the benchmark model the edit re-renders 91 to 93 of 122,826 units, the same set as before reference data and localization were added; the example packs' incremental cost (about 4.5 s against 1.6 s for fanout alone, whose figure the phase 1 gate recorded) is one unit, the `sql-ddl` `schema` script of the 10,005-table main database, whose output changes with the edit and so cannot be skipped (`bench/README.md`, "Incremental run with the example packs").

**Last-run record (one-shot hosts, D45).** A host that runs one generation per process (the CLI's `generate`) sets the internal
`GenerationService.ReuseLastRun`. After an apply run that succeeded and left every planned unit with a current state (no pending
schema diff, no MQ6004, MQ6005, MQ6009, MQ6010 or MQ6015, no journal left), it writes `CacheDirectory/last-run.v1.bin` through
`EnginePaths`: the run key (the engine build: contract version, module version ids of the engine and entry assemblies, runtime
version and dependency manifests; the folders; the request's packs, roots and hand-edit override), the stat of every model file and
referenced sidecar as the run read them, the content hash of the templates folder taken before the packs load, the stat of the
unit-state and built-root manifest files and the content hash of the committed manifest and snapshot files after the run, every
planned unit's recorded outputs with their stat, the planned unit count and the diagnostics of stages 1 to 5. The next such
process, before loading anything, answers an apply run with the same key from the intact record when every recorded stat and hash
still holds and no journal exists: `Succeeded`, every unit skipped, nothing written, the recorded diagnostics, which is what the
full run would return. Anything else (a changed, added or removed file, another engine build, a damaged or foreign record, a
recorded path that cannot be checked) runs the full pipeline. Long-lived hosts neither read nor write it. Details:
`Generation/README.md`.

## 12. Writer, manifest and journal (W7)

**12.1 Path policy** (`IOutputPathPolicy`, run for every output path, and the same guard used for every engine write, S23): the path must be relative with `/` separators, non-empty, with no `.` or `..` segment, no drive, UNC or leading `/`, no characters invalid on Windows (`<>:"|?*` and control characters), no segment that is a reserved device name or ends with `.` or space; it must lie under an `outputs.allow` path; it must match no `outputs.deny` glob (`*`, `**`, `?`); and no segment may be `.git` or `.maquettiste`. Before any write, each existing ancestor directory and the target are checked: a symlink whose resolved target leaves the root's real path is refused (MQ6004). Two outputs that differ only by case are refused (MQ6005), so output is identical on case-insensitive disks. Engine writes elsewhere use `WriteTarget.Model` (under `ModelRoot`: model files, manifests, snapshots, `.schema`), `WriteTarget.Cache` (`CacheDirectory`, `JournalDirectory`: index cache, unit state, built-root manifests, journal, run lock, plans, jobs) or `WriteTarget.Setup` (only `init`: `.gitignore`, `.git/hooks/post-checkout`, `.git/hooks/post-merge`). The guard is wired by construction (D40): `EngineServices.EnginePaths` (an `OutputPathPolicy(options, null)`, engine-write checks only, no I/O) is passed to every component that writes a file (§18), and `ModelStore`, the plan store and the job store reach it through `EngineServices`; none of them writes a path `CheckEngineWrite` refused.

**12.2 Manifest.** One file per pack and root kind: committed roots at `.maquettiste/manifest/<pack>.json`, built roots at `.maquettiste/.cache/manifest/<pack>.json`. The format is canonical JSON with one entry per line, sorted by path in ordinal UTF-8 order. An empty manifest deletes the file.

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

The hash is the content hash of the bytes written. An `r:` prefix marks a regions file hashed with every region body emptied (the skeleton), so edits inside regions are not hand edits; an `o:` prefix marks an owned file (`once`, companion), never checked for edits and never deleted. The unit is `<unitId>` or `<unitId>:<elementId>`, plus `#companion` for companions.

**12.3 Decision per output file** (`D` = disk bytes, `M` = manifest entry, `N` = new bytes), first match wins: no `D` → Added. `D` = `N` → Unchanged: no write, mtime untouched, entry written or refreshed (this also adopts an identical untracked file). `D` without `M` → HandEdited (an untracked file in the way). `D` ≠ `M` (skeletons for regions) → HandEdited. Otherwise → Modified. Owned: `D` exists → Kept, else Added. HandEdited follows the pack's policy: `fail` → Conflict (nothing written, exit 3), `overwrite` → write, `skip` → leave file and old entry. **Orphans**: entries of the run's packs produced by no rendered or skipped unit → Deleted (only if `D` = `M`, else HandEdited under the policy); owned orphans → OrphanedOwned (file kept, entry dropped). Without a `--pack` filter, manifests of packs that no longer exist are orphaned in full. Emptied directories under a root (never the root itself) are removed. Failed units are never orphaned. A `ContentOmitted` file (plan apply, §15) is produced like any other, so it is never an orphan; it is never written, and a disk hash other than its `ContentHash` is a Conflict. With `WriteContext.PlannedPaths` set, a write or delete outside it is refused (MQ6004).

**12.3a Removing a pack (status: built 2026-10-01).** `GenerationService.DeletePackAsync` (`DELETE /api/packs/{pack}`, the MCP tool `delete_pack`, `pack remove <name> --apply`) takes the run lock (waiting for a run in progress), checks `pack.json` against the caller's hash, removes `packs.<pack>` from `maquettiste.json` through the settings save (a refused save removes nothing), deletes `templates/<pack>/` (every path checked with `CheckEngineWrite(Model)` before the first delete; a link is removed, never followed), then deletes the pack's committed and built manifests and its unit states. As decided on 2026-10-01, the files the pack generated stay on disk, untracked, and the result lists them: deleting the manifests is what keeps the orphan rule above (manifests of packs that no longer exist are orphaned in full) from deleting every intact file on the next run without a `--pack` filter, which is not what someone removing a pack expects. A file left behind is then an untracked file like any other: a pack that later renders the same path adopts it when the bytes are identical and otherwise sees a hand edit under its policy. **Renaming a pack (added 2026-10-01).** `GenerationService.RenamePackAsync(pack, newName, expectedHash, ct, source, dryRun)` (`POST /api/packs/{pack}/rename` with `{ name }` and `If-Match`, the MCP tool `rename_pack`, `pack rename <name> <new-name> --apply`) takes the run lock, checks `pack.json` against the caller's hash, and refuses (`invalid`, nothing changed) a new name that is not a pack key, is the current one, has a folder under `templates/`, has a `packs.<name>` settings entry, has manifests of a former pack, or while an unfinished journal names the pack. It then moves `packs.<pack>` to `packs.<newName>` with its values through the settings save (a refused save renames nothing), renames `templates/<pack>/` with one directory move, never a copy (every entry checked with `CheckEngineWrite(Model)` at its old and new path first; a pack folder that is a link is refused; a link inside moves as an entry; a failed move puts the settings entry back), writes the new name into `pack.json` (the loader requires the name to equal the folder), and moves the committed and built manifests and the unit states, rewriting the unit keys' `<pack>/` prefix. Manifest entries carry no pack name, so the paths stay recorded under the new name and the orphan rule above does not delete them. The pack name is part of every unit's static hash, so the next plan renders each unit again and finds identical bytes (`unchanged`) unless a template prints the pack's name. Element generation hints keyed by the old name (`generation.<pack>` at any depth: an element, a table, a column, an enum member, a category node) are listed in the result (`hints`); the rename itself does not write the model, and the hints move to the new name in one model batch through the normal element save path (`PackHints.Rename`, `GenerationService.RenamePackHintsAsync`, each element checked against its current hash, all or nothing). The MCP tool (`updateHints`, default true) and `pack rename --apply` (unless `--keep-hints`) run that batch after the rename (`RenamePackAsync(..., updateHints: true)`, reported in `hintsUpdated`; a refused batch leaves the rename in place with a warning); the editor counts the hints with a dry run (`dryRun` in the request) and, ticked by default, saves the same change as one batch of its own, so it is one undo step. A map that already has a key with the new name is left as it is (added 2026-10-01). The embedded starter names do not matter: a project pack may take or leave `sql-ddl` or `csharp-dapper`, as `pack new` already allows; `packs.lock.json` (SPEC §19) is not implemented, so nothing pins a pack by name. The sample's pack copies and the reference application are not touched.

**12.4 Writes, journal, lock.** The writer drains a bounded queue with `min(jobs, 8)` tasks. Each write goes to `<dir>/.<name>.mq-<runId>-<n>.tmp`, then `File.Move(overwrite: true)`; the directory is created only after the path policy passes. The run journal `JournalDirectory/journal.jsonl` (default `.maquettiste/.cache/`) holds `{"t":"begin","run","plan","packs"}`, then a `{"t":"write","pack","path","hash","unit"}` or `{"t":"delete","pack","path"}` line appended and flushed to the OS after each file, `{"t":"pack","pack"}` after that pack's manifest and unit state are saved (fsync there), and `{"t":"end"}`, after which the file is deleted. A run that finds a journal without `end` replays it: for packs without a `pack` line, its entries overlay the manifests, so those files are never hand edits, and the new run then completes normally. Every generation mode first takes `JournalDirectory/run.lock` (`FileShare.None`); `LockMode.Wait` polls every 100 ms, `LockMode.Fail` returns `Busy`. Cancellation is checked between files; after it, the writer finishes the current file, leaves the journal consistent and returns within one second.

## 13. Post-processing: regions and formatters (W8)

Per rendered file, in order: (1) normalize CRLF and CR to LF, strip a BOM, encode UTF-8; (2) format, only for rendered units, when the unit's formatter (by name, or by extension when unnamed; `"none"` disables it) is configured: `ProcessStartInfo` with `ArgumentList` (`{path}` replaced), working directory = repo root, input on stdin, stdout = result, a non-zero exit or timeout → MQ6008 and the unit fails; (3) for `regions`, read the disk file and move each region body (`maquettiste:keep id=<id>` line … `maquettiste:end-keep` line, any comment syntax) into the same id in the new output; a disk region with no counterpart → MQ6010 Conflict; (4) hash (skeleton for regions) and classify the root through `IOutputPathPolicy`. Before stage 7 the runner checks each used formatter once with `Command VersionArgs`: the output must contain `Version`, else MQ6008 fails the run. Formatter settings are part of `StaticHash`. `--check` runs the same code in memory.

## 14. Schema diff (W8)

When an enabled pack has `UsesSchemaDiff`, the orchestrator loads `.maquettiste/snapshots/<database kebab name>.json` for each database, captures the current `RDatabase` and diffs them before planning. `PhysicalSnapshot` is canonical JSON: `database` (id), `name`, `dialect`, `revision`, and `tables`, `views` and `sequences` sorted by key, with columns (`key`, `name`, `type`, facets, `nativeType`, `nullable`, `default`, `defaultSql`, `identity`, `sequence`, `computed`, `computedStored`, `collation`, `comment`), `primaryKey` and `uniques` (with `clustered`), `foreignKeys`, `checks`, `indexes` and the table `comment`, all keyed by §7.3 keys. The snapshot carries every physical property of S9's table contents, and the differ compares every snapshot property, so identity-to-sequence switches, virtual-to-stored computed columns and comment changes surface as `Altered` with `PropertyChange`s.

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
    public Task<PackListResult> GetPacksAsync(CancellationToken ct); }   // E2: every pack under templates/, enabled or not
public enum LockMode { Wait, Fail }
public sealed record GenerationRequest { public GenerationMode Mode { get; init; } = GenerationMode.Apply; public IReadOnlyList<string>? Packs { get; init; }
    public bool Force { get; init; } public int? Jobs { get; init; } public HandEditPolicy? HandEdits { get; init; } public bool IncludeDiffs { get; init; }
    public RootSelection Roots { get; init; } = RootSelection.All; public LockMode Lock { get; init; } = LockMode.Wait; public bool StageBarriers { get; init; } }
public enum RunOutcome { Succeeded, Invalid, Drift, Conflicts, Busy, Stale, Cancelled, Failed }
public sealed record GenerationResult(string RunId, GenerationMode Mode, RunOutcome Outcome, IReadOnlyList<FileChange> Changes, int UnitsRendered,
    int UnitsSkipped, int FilesWritten, int FilesDeleted, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<StageTiming> Timings);
public sealed record PlanUnit(string Key, string InputHash, IReadOnlyList<string> ReadKeys, bool Skipped, IReadOnlyList<PlanFile> Outputs);
public sealed record PlanFile(string Path, string ContentHash, string ManifestHash, OutputMode Mode, FileRole Role, OutputRootInfo Root,
    string? DiskHashAtPlan);                                // null: the file did not exist at plan time
public sealed record GenerationPlan(string Id, GenerationRequest Request, long ModelVersion, IReadOnlyList<string> Packs,
    IReadOnlyList<PlanUnit> Units, IReadOnlyList<FileChange> Changes, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record PlanResult(RunOutcome Outcome, GenerationPlan? Plan);
public sealed record ApplyResult(RunOutcome Outcome, IReadOnlyList<string> StaleUnits, IReadOnlyList<string> StalePaths, GenerationResult? Result);
public sealed record PreviewResult(IReadOnlyList<RenderedFile> Files, IReadOnlyList<Diagnostic> Diagnostics);

public sealed class JobQueue : IAsyncDisposable             // (23–28) W6
{   public JobQueue(GenerationService generation, EngineOptions options, int capacity = 16);
    public bool TryEnqueue(JobRequest request, [NotNullWhen(true)] out JobInfo? job);
    public Task<JobInfo?> GetAsync(string id, CancellationToken ct);   // in-memory jobs without I/O; finished jobs from CacheDirectory/jobs/<id>.json (27)
    public Task<IReadOnlyList<JobInfo>> ListAsync(CancellationToken ct);
    public bool Cancel(string id);
    public Task RunAsync(CancellationToken stoppingToken);  // one job at a time; the [BackgroundService] awaits this
    public IDisposable OnProgress(Func<JobInfo, ProgressUpdate, ValueTask> handler);
    public IDisposable OnCompleted(Func<JobInfo, ValueTask> handler); }
public enum JobKind { Plan, Apply }
public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }
public sealed record JobRequest(JobKind Kind, GenerationRequest? Plan, string? PlanId);
public sealed record JobInfo(string Id, JobKind Kind, JobState State, int? QueuePosition, ProgressUpdate? Progress,
    PlanResult? PlanResult, ApplyResult? ApplyResult, string? Error,
    DateTimeOffset QueuedUtc, DateTimeOffset? StartedUtc, DateTimeOffset? FinishedUtc);   // from EngineOptions.TimeProvider

// Phase 2 editor additions E1–E4 (phase2-design.md §3.8); records in Editor/, public, Web-default JSON without converters
public sealed record DatabaseViewResult(DatabaseView? View, IReadOnlyList<Diagnostic> Diagnostics);   // View null on model errors or an unknown id (MQ6017)
public sealed record DatabaseView(string Id, string Name, string Dialect, string? Version, string? DefaultSchema, IReadOnlyList<TableView> Tables,
    IReadOnlyList<ViewView> Views, IReadOnlyList<SequenceView> Sequences, IReadOnlyList<SchemaView> Schemas, string Quoting, int? MaxIdentifierLength,
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

A plan persists to `CacheDirectory/plans/<id>/plan.json` (through `EnginePaths`), with post-processed bytes of each added or modified file in `blobs/<ContentHash>`; the 20 newest plans are kept. The plan stores the request it was made with (`GenerationPlan.Request`: packs, roots, hand-edit policy, force, lock mode) and, per unit, every output file (`PlanUnit.Outputs`, unchanged files included, with mode, role, root and the disk hash seen at plan time), so apply needs no second render. `ApplyAsync` locks (with `Request.Lock`), reloads, validates, resolves, re-plans, recomputes each `PlanUnit.InputHash` from its read keys, and re-hashes every planned path on disk. Any input difference, any unit added or removed, or any planned path whose disk hash differs from `DiskHashAtPlan` (a hand edit, or an edit inside a protected region, since the plan's region bodies were merged at plan time) returns `Stale` with `StaleUnits` and `StalePaths` and writes nothing (30). Otherwise it feeds the writer one `ProcessedUnit` per planned unit: added and modified files from the blobs, unchanged ones as `ContentOmitted` files, skipped units as `SkippedUnit`s, with `WriteContext.PlannedPaths` = every path in `Outputs` and `Changes` so nothing outside the plan is touched (S19); then it saves unit state from the plan's read keys. Batches stage every file as `.<name>.mq-<batchId>.tmp` in the target folder and rename only after all staging succeeded; a rename failure rolls back the renamed files from copies staged beside them.

## 16. CLI (W9)

`maquettiste [global options] <command> [options]`. The parser is hand-written (no extra package). Globals: `--repo <dir>` (default: nearest ancestor holding `.maquettiste/maquettiste.json`), `--cache-dir <dir>` (default: `$MAQUETTISTE_CACHE_DIR`, else the OS user cache folder `…/maquettiste/<first 16 hex of SHA-256 of the repo path>`; D13), `--jobs <n>`, `--progress auto|plain|json|none`, `--verbosity quiet|normal|detailed`, `--no-color`.

| Command | Options and behavior |
| --- | --- |
| `init` | `--pack sql-ddl\|csharp-dapper\|none` (default sql-ddl), `--hooks`. Creates `maquettiste.json` (format 1, `outputs.allow` roots `db` committed and `src/Generated` built), the model folders, `.schema/v1/*`, and the starter pack copied from embedded resources. Writes a `# maquettiste:begin` … `# maquettiste:end` block in `.gitignore` (built roots, `.maquettiste/.cache/`). Idempotent: existing files are kept, except `.schema/v1`, which is refreshed |
| `validate` | `--format text\|json\|sarif`, `--output <file>` |
| `generate` | `--pack <name>` (repeatable), `--force`, `--roots all\|committed\|built`, `--hand-edits fail\|overwrite\|skip`, `--watch` (FileSystemWatcher on the model root, 250 ms debounce, `RefreshAsync`, then an incremental run; engine-owned paths ignored), `--dry-run` (lists `A`/`M`/`D`/`H`/`K`/`O` per path), `--diff` (with `--dry-run`: unified diffs on stdout), `--check` (committed roots, in memory), `--format text\|json` |
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
| D14 | Manifest is JSON with one entry per line; `r:` and `o:` hash prefixes | Valid JSON and line-level diffs; region edits and owned files need distinct checks |
| D15 | Schema snapshots at `.maquettiste/snapshots/<db>.json`, kept only when a pack sets `usesSchemaDiff`; migrations use `once` | No surprise files; each migration is written once and kept |
| D16 | `maquettiste.json` and `pack.json` refer to databases and packs by name | Hand-edited settings and portable packs cannot know ids |
| D17 | Output paths are repo-relative after the pack's `output` prefix; the longest matching root decides commit and built | One rule for allow, manifest location and `--check` |
| D18 | Dependencies are tracked per resolved object (the union of its source files) plus kind-set keys | Over-approximation is safe; element granularity keeps read sets small |
| D19 | `output` is evaluated at render time; duplicate paths are caught in stage 8 | Keeps the incremental plan stage cheap at 100k units |
| D20 | `--check` renders every unit and covers committed roots; hand edits exit 3, other drift exits 2 | Matches S12 and S17 |
| D21 | Journal lines are flushed to the OS per file and fsynced per pack | Per-file fsync would break the write budget |
| D22 | Every generation mode, dry run included, takes the run lock | Manifests read mid-apply would be inconsistent |
| D23 | Usage errors, busy and cancellation exit 4; template and script errors exit 1; precedence 4, 1, 3, 2 | S17 defines only five codes |
| D24 | Schemas carry no `$id`; the engine assigns `https://maquettiste.invalid/schemas/v1/<file>` bases | Editors resolve relative refs locally; nothing can reach a network |
| D25 | Packages live in `model/packages/`; tags and categories are one file each; one file per stereotype | S4 lists no packages folder; vocabularies are small |
| D26 | Casing lowercases acronyms (`HTTPServer` → `HttpServer`, `http_server`) | One deterministic rule |
| D27 | No file-system abstraction, logging package or command-line library | Fewer dependencies (host-contracts 2); real temp dirs test symlinks honestly |
| D28 | `generate --roots` added to S17's options | S17's CI row runs built and committed roots separately |
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
