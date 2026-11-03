using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Maquettiste.Engine.Model;

/// <summary>Static facts about one element kind: its JSON name, CLR type, schema file and conventional folder (engine-design.md section 2.2).</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The <c>kind</c> value in JSON, for example <c>value-object</c>.</param>
/// <param name="ClrType">The record type the kind deserializes to.</param>
/// <param name="SchemaFile">The schema file name under <c>schemas/v1/</c>.</param>
/// <param name="Folder">
/// The conventional folder under <c>.maquettiste/</c>; <c>{db}</c> stands for the database's kebab-case name.
/// </param>
/// <param name="FixedFileName">The file name when the kind has a fixed one (<c>database.json</c>, <c>tags.json</c>, <c>categories.json</c>), else <see langword="null"/>.</param>
public sealed record KindInfo(ElementKind Kind, string Name, Type ClrType, string SchemaFile, string Folder, string? FixedFileName)
{
    private static readonly ImmutableArray<KindInfo> AllKinds =
    [
        new(ElementKind.Package, "package", typeof(Package), "package.json", "model/packages", null),
        new(ElementKind.Entity, "entity", typeof(Entity), "entity.json", "model/entities", null),
        new(ElementKind.ValueObject, "value-object", typeof(ValueObject), "value-object.json", "model/types", null),
        new(ElementKind.ScalarType, "scalar-type", typeof(ScalarType), "scalar-type.json", "model/types", null),
        new(ElementKind.Enum, "enum", typeof(EnumType), "enum.json", "model/enums", null),
        new(ElementKind.Relation, "relation", typeof(Relation), "relation.json", "model/relations", null),
        new(ElementKind.Database, "database", typeof(Database), "database.json", "model/databases/{db}", "database.json"),
        new(ElementKind.Table, "table", typeof(Table), "table.json", "model/databases/{db}/tables", null),
        new(ElementKind.View, "view", typeof(View), "view.json", "model/databases/{db}/views", null),
        new(ElementKind.Sequence, "sequence", typeof(Sequence), "sequence.json", "model/databases/{db}/sequences", null),
        new(ElementKind.Routine, "routine", typeof(Routine), "routine.json", "model/databases/{db}/routines", null),
        new(ElementKind.DatabaseType, "database-type", typeof(DatabaseType), "database-type.json", "model/databases/{db}/types", null),
        new(ElementKind.SqlObject, "sql-object", typeof(SqlObject), "sql-object.json", "model/databases/{db}/objects", null),
        new(ElementKind.Query, "query", typeof(Query), "query.json", "model/databases/{db}/queries", null),
        new(ElementKind.Mapping, "mapping", typeof(Mapping), "mapping.json", "model/mappings", null),
        new(ElementKind.Diagram, "diagram", typeof(Diagram), "diagram.json", "model/diagrams", null),
        new(ElementKind.TagVocabulary, "tag-vocabulary", typeof(TagVocabulary), "tag-vocabulary.json", "model/vocabularies", "tags.json"),
        new(ElementKind.CategoryTree, "category-tree", typeof(CategoryTree), "category-tree.json", "model/vocabularies", "categories.json"),
        new(ElementKind.Stereotype, "stereotype", typeof(Stereotype), "stereotype.json", "model/vocabularies/stereotypes", null),
        new(ElementKind.ReferenceType, "reference-type", typeof(ReferenceType), "reference-type.json", "model/reference-types", null),
        new(ElementKind.Seed, "seed", typeof(Seed), "seed.json", SeedsFolder, null),
        new(ElementKind.Process, "process", typeof(Process), "process.json", "model/processes", null),
        new(ElementKind.Actor, "actor", typeof(Actor), "actor.json", "model/actors", null),
        new(ElementKind.Scenario, "scenario", typeof(Scenario), "scenario.json", ScenariosFolder, null),
        new(ElementKind.Migration, "migration", typeof(Migration), "migration.json", "model/databases/{db}/migrations/open", null),
        new(ElementKind.Operation, "operation", typeof(Operation), "operation.json", OperationsFolder, null),
        new(ElementKind.Constraint, "constraint", typeof(Constraint), "constraint.json", ConstraintsFolder, null),
        new(ElementKind.Custom, "custom", typeof(Custom), "custom.json", CustomFolder, null),
    ];

    /// <summary>The folder of seeds; each target's seeds share a sub-folder named after the target.</summary>
    public const string SeedsFolder = "model/seeds";

    /// <summary>The folder of scenarios; each process's scenarios share a sub-folder named after the process's file stem.</summary>
    public const string ScenariosFolder = "model/scenarios";

    /// <summary>The folder of operations; an operation with a subject lives in a sub-folder named after the subject's file stem.</summary>
    public const string OperationsFolder = "model/operations";

    /// <summary>The folder of named constraints (next-phase-3-design.md section 3.1).</summary>
    public const string ConstraintsFolder = "model/constraints";

    /// <summary>The folder of custom elements; each custom kind's elements share a sub-folder named after its type (<c>model/custom/&lt;type&gt;/</c>).</summary>
    public const string CustomFolder = "model/custom";

    /// <summary>The folder of locale shards, one sub-folder per locale (<c>model/locales/&lt;locale&gt;/</c>).</summary>
    public const string LocalesFolder = "model/locales";

    private static readonly FrozenDictionary<string, KindInfo> ByName = AllKinds.ToFrozenDictionary(k => k.Name, StringComparer.Ordinal);

    /// <summary>Every element kind, in <see cref="ElementKind"/> order.</summary>
    public static IReadOnlyList<KindInfo> All => AllKinds;

    /// <summary>Returns the facts for a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The kind's facts.</returns>
    public static KindInfo Get(ElementKind kind) => AllKinds[(int)kind];

    /// <summary>Looks a kind up by its JSON name.</summary>
    /// <param name="name">The <c>kind</c> value, for example <c>entity</c>.</param>
    /// <param name="info">The kind's facts when found.</param>
    /// <returns><see langword="true"/> when the name is an element kind.</returns>
    public static bool TryGet(string name, [MaybeNullWhen(false)] out KindInfo info) => ByName.TryGetValue(name, out info);

    /// <summary>
    /// The non-element document schemas: project settings, pack manifests, extension schemas, manifests, snapshots, batches
    /// the <c>validate --format json</c> output, locale shards, a migration ledger's rows (<c>ledger.json</c>) and custom kind files
    /// (<c>custom-kind.json</c>), a pack test case (<c>pack-case.json</c>), plus the shared definitions.
    /// </summary>
    public static IReadOnlyList<string> DocumentSchemaFiles { get; } =
        ["common.json", "maquettiste.json", "pack.json", "extension.json", "manifest.json", "snapshot.json", "batch.json", "diagnostics.json", "locale.json", "ledger.json", "custom-kind.json", "pack-case.json", "declared-rules.json"];
}
