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
        new(ElementKind.Mapping, "mapping", typeof(Mapping), "mapping.json", "model/mappings", null),
        new(ElementKind.Diagram, "diagram", typeof(Diagram), "diagram.json", "model/diagrams", null),
        new(ElementKind.TagVocabulary, "tag-vocabulary", typeof(TagVocabulary), "tag-vocabulary.json", "model/vocabularies", "tags.json"),
        new(ElementKind.CategoryTree, "category-tree", typeof(CategoryTree), "category-tree.json", "model/vocabularies", "categories.json"),
        new(ElementKind.Stereotype, "stereotype", typeof(Stereotype), "stereotype.json", "model/vocabularies/stereotypes", null),
        new(ElementKind.ReferenceType, "reference-type", typeof(ReferenceType), "reference-type.json", "model/reference-types", null),
        new(ElementKind.Seed, "seed", typeof(Seed), "seed.json", SeedsFolder, null),
    ];

    /// <summary>The folder of seeds; each target's seeds share a sub-folder named after the target.</summary>
    public const string SeedsFolder = "model/seeds";

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
    /// the <c>validate --format json</c> output and locale shards, plus the shared definitions.
    /// </summary>
    public static IReadOnlyList<string> DocumentSchemaFiles { get; } =
        ["common.json", "maquettiste.json", "pack.json", "extension.json", "manifest.json", "snapshot.json", "batch.json", "diagnostics.json", "locale.json"];
}
