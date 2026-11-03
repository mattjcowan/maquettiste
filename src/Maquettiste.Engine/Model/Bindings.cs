using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// How an entity reads from and writes to one database (erratum E43, engine-design.md section 7, "Bindings and materialize"): the
/// source it reads (a table, a view or a query of the database), the constant columns that filter every read and are set on every
/// insert, the field map from the entity's attributes to the source's columns, the columns it accounts for without a field, where it
/// writes back and how it deletes. The database knows nothing about entities: the binding lives in the entity's file, one per
/// database (MQ4050). An entity with a binding to a database is never projected into that database.
/// </summary>
public sealed record EntityBinding
{
    /// <summary>The binding's permanent ULID, unique across the whole model like every sub-element id.</summary>
    public required string Id { get; init; }

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>
    /// What the entity reads from: the id of a table (a designed or imported table, a synthesized table's overlay file, or a
    /// synthesized table key), a view or a query of <see cref="Database"/>.
    /// </summary>
    [ElementRef(Keyed = true)]
    public required string Source { get; init; }

    /// <summary>Columns with a fixed value: a filter on every read and a value on every insert (several allowed).</summary>
    public IReadOnlyList<BindingConstant> Constants { get; init; } = [];

    /// <summary>The field map: each entity attribute (or value object member, or to-one navigation key) and the source column it reads.</summary>
    public IReadOnlyList<BindingField> Fields { get; init; } = [];

    /// <summary>Source columns no field maps, each accounted for with its status.</summary>
    public IReadOnlyList<BindingColumn> Columns { get; init; } = [];

    /// <summary>
    /// Where the entity writes: a table, or none. <see langword="null"/> (absent) writes to the source when it is a table and
    /// nowhere when it is a view or a query. In JSON: <c>{ "table": id }</c> or <c>"none"</c>.
    /// </summary>
    [JsonConverter(typeof(BindingWriteJsonConverter))]
    public BindingWrite? Write { get; init; }

    /// <summary>
    /// How the entity deletes: by key, by a soft-delete update, or not at all. <see langword="null"/> (absent) deletes by key when the
    /// binding writes, else not at all. In JSON: <c>"key"</c>, <c>{ "soft": { "column", "value" } }</c> or <c>"none"</c>.
    /// </summary>
    [JsonConverter(typeof(BindingDeleteJsonConverter))]
    public BindingDelete? Delete { get; init; }

    /// <summary>
    /// The ids of the entity's checks the write table enforces, lowered through the field map (next-phase-3-design.md section 3.1).
    /// A check no binding lists reaches no table.
    /// </summary>
    [ElementRef(IndexKinds = ["key"])]
    public IReadOnlyList<string> Checks { get; init; } = [];

    /// <summary>
    /// The columns of a table-file source as the last by-name operation recorded them (next-phase-2-design.md section 3.3); written by
    /// the engine only. <see langword="null"/> (absent): no operation recorded them, and only MQ4047 reports what the binding misses.
    /// </summary>
    public BindingProvenance? Provenance { get; init; }

    /// <summary>Markdown text, or a reference to a sidecar Markdown file beside the entity's file.</summary>
    public Description? Description { get; init; }

    /// <summary>Tag keys, in the order written.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Custom property values (the property bag).</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = System.Collections.Immutable.ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>
/// What a binding's source looked like when a by-name operation last followed it (next-phase-2-design.md section 3.3): one entry per
/// column of a designed or imported table file, two short hashes that tell a rename and a retype apart (<see cref="ColumnShapeHash"/>).
/// It is a record, not a link: nothing in it is an <see cref="ElementRefAttribute"/>, so the index does not walk it and a column delete
/// never sees it; a recorded id the source no longer has reads as removed.
/// </summary>
public sealed record BindingProvenance
{
    /// <summary>
    /// Column id (an inherited column by its keyed id, <c>&lt;tableId&gt;.&lt;memberId&gt;</c>) to <c>"&lt;name part&gt;:&lt;type part&gt;"</c>,
    /// 8 hex digits each. Ids here are records, not references. The canonical writer sorts the keys.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Columns { get; init; }
}

/// <summary>
/// The table file of one database that holds a reference type's rows (2026-10-11, optional): the column of each field the rows fill
/// (<c>code</c>, <c>label</c>, <c>description</c> or a user field's id) and the constant columns that tell the type's rows apart in
/// a table several types share. With it, the engine knows the rows are there: foreign keys into the table may name them, a table
/// seed beside them may not repeat their keys, and the database's seed tables carry them.
/// </summary>
public sealed record ReferenceTypeBinding
{
    /// <summary>The binding's permanent ULID, unique across the whole model like every sub-element id.</summary>
    public required string Id { get; init; }

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The id of a table file of <see cref="Database"/>.</summary>
    [ElementRef(ElementKind.Table)]
    public required string Table { get; init; }

    /// <summary>Columns with a fixed value on every row of the type (several allowed).</summary>
    public IReadOnlyList<BindingConstant> Constants { get; init; } = [];

    /// <summary>
    /// The field map: <c>code</c>, <c>label</c>, <c>description</c> or a user field's id as <see cref="BindingField.Attribute"/>, and
    /// the table's column (its id or physical name).
    /// </summary>
    public IReadOnlyList<BindingField> Fields { get; init; } = [];

    /// <summary>Markdown text, or a reference to a sidecar Markdown file beside the reference type's file.</summary>
    public Description? Description { get; init; }
}

/// <summary>A column with a fixed value in a binding.</summary>
public sealed record BindingConstant
{
    /// <summary>The column: a column key or physical name of the source (and of the write table).</summary>
    [ElementRef(Keyed = true)]
    public required string Column { get; init; }

    /// <summary>The value, a JSON string, number or boolean; absent (or null) is SQL NULL.</summary>
    public JsonElement? Value { get; init; }
}

/// <summary>One entry of a binding's field map.</summary>
public sealed record BindingField
{
    /// <summary>
    /// An attribute id of the entity (own or inherited), <c>attributeId.memberId</c> for a member of an embedded value object
    /// attribute, or the id of the relation end a to-one navigation leads to, for its key.
    /// </summary>
    [ElementRef(Keyed = true)]
    public required string Attribute { get; init; }

    /// <summary>A column of the source: its key or physical name (the rules queries use for column references).</summary>
    [ElementRef(Keyed = true)]
    public required string Column { get; init; }
}

/// <summary>A source column a binding accounts for without a field.</summary>
public sealed record BindingColumn
{
    /// <summary>The column: a column key or physical name of the source (or of the write table).</summary>
    [ElementRef(Keyed = true)]
    public required string Column { get; init; }

    /// <summary>What fills the column.</summary>
    public required BindingColumnStatus Status { get; init; }
}

/// <summary>Why a binding leaves a column without a field.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BindingColumnStatus>))]
public enum BindingColumnStatus
{
    /// <summary>The entity does not use the column: <c>ignored</c>.</summary>
    [JsonStringEnumMemberName("ignored")] Ignored,

    /// <summary>The database fills it (a default, a trigger); never written: <c>database</c>.</summary>
    [JsonStringEnumMemberName("database")] Database,

    /// <summary>A computed column; never written: <c>computed</c>.</summary>
    [JsonStringEnumMemberName("computed")] Computed,
}

/// <summary>Where a binding writes: a table, or none.</summary>
public sealed record BindingWrite
{
    /// <summary>The id of the table written (a table file id or a synthesized table key); <see langword="null"/> with <see cref="None"/>.</summary>
    [ElementRef(Keyed = true)]
    public string? Table { get; init; }

    /// <summary>Whether the binding never writes (<c>"none"</c>); the converter reads and writes it as the string.</summary>
    [JsonIgnore]
    public bool None { get; init; }
}

/// <summary>How a binding deletes.</summary>
public sealed record BindingDelete
{
    /// <summary>The delete mode; the converter reads and writes <c>key</c> and <c>none</c> as strings.</summary>
    [JsonIgnore]
    public BindingDeleteMode Mode { get; init; }

    /// <summary>The soft-delete update, with <see cref="BindingDeleteMode.Soft"/>.</summary>
    public SoftDelete? Soft { get; init; }
}

/// <summary>A binding's delete mode.</summary>
public enum BindingDeleteMode
{
    /// <summary>Delete the row by key (plus the constants).</summary>
    Key,

    /// <summary>Set a column instead of deleting the row.</summary>
    Soft,

    /// <summary>Never delete.</summary>
    None,
}

/// <summary>The column a soft delete sets, and its value.</summary>
public sealed record SoftDelete
{
    /// <summary>The column: a column key or physical name of the write table.</summary>
    [ElementRef(Keyed = true)]
    public required string Column { get; init; }

    /// <summary>The value set, a JSON string, number or boolean; absent (or null) is SQL NULL.</summary>
    public JsonElement? Value { get; init; }
}

/// <summary>Reads <c>{ "table": id }</c> or <c>"none"</c>.</summary>
internal sealed class BindingWriteJsonConverter : JsonConverter<BindingWrite>
{
    /// <inheritdoc/>
    public override BindingWrite? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String when reader.GetString() == "none":
                return new BindingWrite { None = true };
            case JsonTokenType.StartObject:
                string? table = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString();
                    reader.Read();
                    if (name == "table" && reader.TokenType == JsonTokenType.String)
                        table = reader.GetString();
                    else
                        reader.Skip();
                }

                return new BindingWrite { Table = table ?? throw new JsonException("A binding's write names its table.") };
            default:
                throw new JsonException("A binding's write is { \"table\": id } or \"none\".");
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindingWrite value, JsonSerializerOptions options)
    {
        if (value.None || value.Table is null)
        {
            writer.WriteStringValue("none");
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("table", value.Table);
        writer.WriteEndObject();
    }
}

/// <summary>Reads <c>"key"</c>, <c>{ "soft": { "column", "value" } }</c> or <c>"none"</c>.</summary>
internal sealed class BindingDeleteJsonConverter : JsonConverter<BindingDelete>
{
    /// <inheritdoc/>
    public override BindingDelete? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString() switch
                {
                    "key" => new BindingDelete { Mode = BindingDeleteMode.Key },
                    "none" => new BindingDelete { Mode = BindingDeleteMode.None },
                    _ => throw new JsonException("A binding's delete is \"key\", \"none\" or { \"soft\": ... }."),
                };
            case JsonTokenType.StartObject:
                SoftDelete? soft = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString();
                    reader.Read();
                    if (name == "soft" && reader.TokenType == JsonTokenType.StartObject)
                        soft = JsonSerializer.Deserialize<SoftDelete>(ref reader, options);
                    else
                        reader.Skip();
                }

                return new BindingDelete { Mode = BindingDeleteMode.Soft, Soft = soft ?? throw new JsonException("A soft delete names its column.") };
            default:
                throw new JsonException("A binding's delete is \"key\", \"none\" or { \"soft\": ... }.");
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindingDelete value, JsonSerializerOptions options)
    {
        switch (value.Mode)
        {
            case BindingDeleteMode.Soft when value.Soft is { } soft:
                writer.WriteStartObject();
                writer.WritePropertyName("soft");
                JsonSerializer.Serialize(writer, soft, options);
                writer.WriteEndObject();
                break;
            case BindingDeleteMode.None:
                writer.WriteStringValue("none");
                break;
            default:
                writer.WriteStringValue("key");
                break;
        }
    }
}
