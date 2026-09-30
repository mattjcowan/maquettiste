using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// Binds one entity or relation to one database (<c>model/mappings/</c>; SPEC section 10). One per (database, entity or relation);
/// duplicates are MQ4004. Exactly one of <see cref="Entity"/> and <see cref="Relation"/> is set.
/// </summary>
public sealed record Mapping : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Mapping;

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The id of the mapped entity.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>The id of the mapped relation.</summary>
    [ElementRef(ElementKind.Relation)]
    public string? Relation { get; init; }

    /// <summary>For an entity: the id of a designed or imported table to bind to instead of synthesizing one.</summary>
    [ElementRef(ElementKind.Table)]
    public string? Table { get; init; }

    /// <summary>For an entity: the id of the database schema its conventional table goes to (erratum E26).</summary>
    [ElementRef(IndexKinds = ["schema"])]
    public string? Schema { get; init; }

    /// <summary>Whether the element is not stored in this database.</summary>
    public bool Ignore { get; init; }

    /// <summary>The inheritance strategy; set on the hierarchy root only.</summary>
    public InheritanceStrategy? Inheritance { get; init; }

    /// <summary>The discriminator value; defaults to the entity name.</summary>
    public JsonElement? DiscriminatorValue { get; init; }

    /// <summary>Per-attribute mapping options.</summary>
    public IReadOnlyList<AttributeMapping> Attributes { get; init; } = [];

    /// <summary>For a relation: overrides the SPEC section 7 default shape.</summary>
    public RelationShape? Shape { get; init; }

    /// <summary>For a one-to-one relation tie: the id of the end whose table holds the foreign key.</summary>
    [ElementRef(IndexKinds = ["end"])]
    public string? ForeignKeyEnd { get; init; }

    /// <summary>
    /// For a foreign-key shape whose dependent end is bound to a designed or imported table (<see cref="Table"/> on that entity's
    /// mapping): the id of the existing foreign key in that table that realizes the relation. The resolver binds the relation to it
    /// instead of synthesizing foreign-key columns (MQ4011 when both ends are bound to such tables and this is missing).
    /// </summary>
    [ElementRef(IndexKinds = ["key"])]
    public string? ForeignKey { get; init; }

    /// <summary>For a junction shape: the id of a designed junction table.</summary>
    [ElementRef(ElementKind.Table)]
    public string? JunctionTable { get; init; }

    /// <summary>
    /// For a designed <see cref="JunctionTable"/>: which of its foreign keys realizes each relation end, so every end resolves to
    /// existing columns. Every end needs one entry when a junction table is named (MQ4011).
    /// </summary>
    public IReadOnlyList<RelationEndMapping> Ends { get; init; } = [];

    /// <summary>For a promoted shape: the generated entity's name; defaults to pascal(entity1 + entity2).</summary>
    public string? PromotedName { get; init; }
}

/// <summary>How one attribute maps to storage.</summary>
public sealed record AttributeMapping
{
    /// <summary>The id of the attribute.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public required string Attribute { get; init; }

    /// <summary>The id of a column in the bound designed or imported table.</summary>
    [ElementRef(IndexKinds = ["column"])]
    public string? Column { get; init; }

    /// <summary>How an enum or value object attribute is stored.</summary>
    public StorageKind? Storage { get; init; }

    /// <summary>The column prefix for an embedded value object.</summary>
    public string? Prefix { get; init; }

    /// <summary>Whether the attribute is not stored.</summary>
    public bool Ignore { get; init; }
}

/// <summary>Binds one relation end to an existing foreign key of a designed junction table.</summary>
public sealed record RelationEndMapping
{
    /// <summary>The id of the relation end.</summary>
    [ElementRef(IndexKinds = ["end"])]
    public required string End { get; init; }

    /// <summary>The id of the junction table's foreign key that references this end's table.</summary>
    [ElementRef(IndexKinds = ["key"])]
    public required string ForeignKey { get; init; }
}

/// <summary>An inheritance mapping strategy.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InheritanceStrategy>))]
public enum InheritanceStrategy
{
    /// <summary>Table per hierarchy, with a discriminator column: <c>tph</c>.</summary>
    [JsonStringEnumMemberName("tph")] Tph,

    /// <summary>Table per type: <c>tpt</c>.</summary>
    [JsonStringEnumMemberName("tpt")] Tpt,

    /// <summary>Table per concrete type: <c>tpc</c>.</summary>
    [JsonStringEnumMemberName("tpc")] Tpc,
}

/// <summary>How an enum or value object is stored.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StorageKind>))]
public enum StorageKind
{
    /// <summary>Enum as its integer value: <c>int</c>.</summary>
    [JsonStringEnumMemberName("int")] Int,

    /// <summary>Enum as its code or name: <c>string</c>.</summary>
    [JsonStringEnumMemberName("string")] String,

    /// <summary>Value object as prefixed columns: <c>embedded</c>.</summary>
    [JsonStringEnumMemberName("embedded")] Embedded,

    /// <summary>Value object in its own (child) table: <c>table</c>.</summary>
    [JsonStringEnumMemberName("table")] Table,

    /// <summary>Value object as one JSON column: <c>json</c>.</summary>
    [JsonStringEnumMemberName("json")] Json,
}

/// <summary>The physical shape of a relation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RelationShape>))]
public enum RelationShape
{
    /// <summary>A foreign key: <c>foreign-key</c>.</summary>
    [JsonStringEnumMemberName("foreign-key")] ForeignKey,

    /// <summary>A junction table: <c>junction</c>.</summary>
    [JsonStringEnumMemberName("junction")] Junction,

    /// <summary>A generated association entity: <c>promoted</c>.</summary>
    [JsonStringEnumMemberName("promoted")] Promoted,
}
