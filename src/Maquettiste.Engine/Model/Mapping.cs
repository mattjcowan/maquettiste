using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// Binds one entity or relation to one database (<c>model/mappings/</c>; SPEC section 10). One per (database, entity or relation);
/// duplicates are MQ4004. Exactly one of <see cref="Entity"/> and <see cref="Relation"/> is set.
/// </summary>
public sealed record Mapping : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Mapping;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root. Membership only: it never changes
    /// where the element goes in a database.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

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
    /// existing columns. Every end needs one entry when a junction table is named (MQ4011). For a relation with an end of several types:
    /// how the tables point at each end (several-types-design.md decision 38).
    /// </summary>
    public IReadOnlyList<RelationEndMapping> Ends { get; init; } = [];

    /// <summary>
    /// For a relation mapping with a designed <see cref="JunctionTable"/>: its columns with a fixed value, a filter on the links the mapping
    /// reads and a value on every link it inserts (several-types-design.md decision 44).
    /// </summary>
    public IReadOnlyList<MappingConstant> Constants { get; init; } = [];

    /// <summary>For a promoted shape: the generated entity's name; defaults to pascal(entity1 + entity2).</summary>
    public string? PromotedName { get; init; }
}

/// <summary>How one attribute maps to storage.</summary>
public sealed record AttributeMapping
{
    /// <summary>The id of the attribute.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public required string Attribute { get; init; }

    /// <summary>
    /// The id of a column in the bound designed or imported table; on a relation mapping with a designed junction table, a column of the
    /// table that holds every link (several-types-design.md decision 45).
    /// </summary>
    [ElementRef(IndexKinds = ["column"])]
    public string? Column { get; init; }

    /// <summary>How an enum or value object attribute is stored.</summary>
    public StorageKind? Storage { get; init; }

    /// <summary>The column prefix for an embedded value object.</summary>
    public string? Prefix { get; init; }

    /// <summary>Whether the attribute is not stored.</summary>
    public bool Ignore { get; init; }
}

/// <summary>
/// How the database's tables point at one relation end (several-types-design.md decision 38). For a designed junction table of a relation
/// whose ends each have one type: the junction's foreign key that realizes the end. For a relation with an end of several types: exactly
/// one way, <see cref="ForeignKey"/>, <see cref="ForeignKeys"/>, <see cref="Types"/> with foreign key items, or <see cref="IdColumn"/>
/// (with <see cref="TypeColumn"/> and <see cref="ReferencesColumn"/>). The schema requires only <see cref="End"/>: an entry with no way,
/// or two, is MQ4009, so every reader handles an entry with no <see cref="ForeignKey"/>.
/// </summary>
public sealed record RelationEndMapping
{
    /// <summary>The id of the relation end.</summary>
    [ElementRef(IndexKinds = ["end"])]
    public required string End { get; init; }

    /// <summary>
    /// The id of one existing foreign key that points at the end: the junction table's key that references the end's table, or on an end
    /// with several types a key to a table that holds the rows of every placed type; <see langword="null"/> when the entry uses another way.
    /// </summary>
    [ElementRef(IndexKinds = ["key"])]
    public string? ForeignKey { get; init; }

    /// <summary>The ids of several existing foreign keys that point at the end, one per table that holds links (ways 1 and 7).</summary>
    [ElementRef(IndexKinds = ["key"])]
    public IReadOnlyList<string> ForeignKeys { get; init; } = [];

    /// <summary>Per type of an end with several types: its foreign key (a key per type), or beside <see cref="IdColumn"/> the column its id refers to.</summary>
    public IReadOnlyList<RelationEndTypeStorage> Types { get; init; } = [];

    /// <summary>The id of the column that holds the linked object's id, in the table that holds the link.</summary>
    [ElementRef(IndexKinds = ["column"])]
    public string? IdColumn { get; init; }

    /// <summary>Beside <see cref="IdColumn"/>: the id of the column that holds the linked object's type code.</summary>
    [ElementRef(IndexKinds = ["column"])]
    public string? TypeColumn { get; init; }

    /// <summary>
    /// Beside <see cref="IdColumn"/>, when one table holds the rows of every placed type: the unique column of that table the id refers to,
    /// instead of its primary key (a column id, or a column key of a table the engine makes).
    /// </summary>
    [ElementRef(Keyed = true)]
    public string? ReferencesColumn { get; init; }

    /// <summary>
    /// Whether the entry names a way (decision 38): <see cref="ForeignKey"/>, <see cref="ForeignKeys"/>, <see cref="Types"/> with foreign key
    /// items, or <see cref="IdColumn"/>.
    /// </summary>
    [JsonIgnore]
    public bool HasWay => ForeignKey is not null || ForeignKeys.Count > 0 || IdColumn is not null || Types.Any(t => t.ForeignKey is not null);
}

/// <summary>One type of a relation end with several types and how its objects are pointed at (schema title "End type storage"; decision 38).</summary>
public sealed record RelationEndTypeStorage
{
    /// <summary>The id of an entity the end covers.</summary>
    [ElementRef(ElementKind.Entity)]
    public required string Entity { get; init; }

    /// <summary>The id of the existing foreign key that points at objects of this type; not written with <see cref="ReferencesColumn"/>.</summary>
    [ElementRef(IndexKinds = ["key"])]
    public string? ForeignKey { get; init; }

    /// <summary>
    /// Beside the entry's <see cref="RelationEndMapping.IdColumn"/>: the column of this type's table the id refers to, unique in that table
    /// (a column id, or a column key of a table the engine makes); not written with <see cref="ForeignKey"/>.
    /// </summary>
    [ElementRef(Keyed = true)]
    public string? ReferencesColumn { get; init; }
}

/// <summary>
/// A column of a relation mapping's designed junction table with a fixed value (several-types-design.md decision 44): a filter on the links
/// the mapping reads and the value it sets on every link it inserts, such as a relation code in a table several relations share. The JSON
/// shape is a binding's constant (<see cref="BindingConstant"/>), but the column is a column id only, so a renamed column is followed.
/// </summary>
public sealed record MappingConstant
{
    /// <summary>The id of a column of the junction table.</summary>
    [ElementRef(IndexKinds = ["column"])]
    public required string Column { get; init; }

    /// <summary>The value, a JSON string, number or boolean; absent (or null) is SQL NULL.</summary>
    public JsonElement? Value { get; init; }
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
