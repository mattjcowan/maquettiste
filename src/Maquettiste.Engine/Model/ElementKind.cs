using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>The kinds of model document that carry an id and a <c>kind</c> field (engine-design.md section 2.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ElementKind>))]
public enum ElementKind
{
    /// <summary>A namespace that groups elements (<c>package</c>).</summary>
    [JsonStringEnumMemberName("package")] Package,

    /// <summary>A business object with identity (<c>entity</c>).</summary>
    [JsonStringEnumMemberName("entity")] Entity,

    /// <summary>A composite type without identity (<c>value-object</c>).</summary>
    [JsonStringEnumMemberName("value-object")] ValueObject,

    /// <summary>A named restriction of a built-in type (<c>scalar-type</c>).</summary>
    [JsonStringEnumMemberName("scalar-type")] ScalarType,

    /// <summary>A closed set of named members (<c>enum</c>).</summary>
    [JsonStringEnumMemberName("enum")] Enum,

    /// <summary>A named association between entities (<c>relation</c>).</summary>
    [JsonStringEnumMemberName("relation")] Relation,

    /// <summary>A physical store with a dialect (<c>database</c>).</summary>
    [JsonStringEnumMemberName("database")] Database,

    /// <summary>A physical table (<c>table</c>).</summary>
    [JsonStringEnumMemberName("table")] Table,

    /// <summary>A physical view (<c>view</c>).</summary>
    [JsonStringEnumMemberName("view")] View,

    /// <summary>A physical sequence (<c>sequence</c>).</summary>
    [JsonStringEnumMemberName("sequence")] Sequence,

    /// <summary>A binding of an entity or relation to a database (<c>mapping</c>).</summary>
    [JsonStringEnumMemberName("mapping")] Mapping,

    /// <summary>A saved canvas: membership and positions only (<c>diagram</c>).</summary>
    [JsonStringEnumMemberName("diagram")] Diagram,

    /// <summary>The tag vocabulary (<c>tag-vocabulary</c>).</summary>
    [JsonStringEnumMemberName("tag-vocabulary")] TagVocabulary,

    /// <summary>The category tree (<c>category-tree</c>).</summary>
    [JsonStringEnumMemberName("category-tree")] CategoryTree,

    /// <summary>A stereotype definition (<c>stereotype</c>).</summary>
    [JsonStringEnumMemberName("stereotype")] Stereotype,
}
