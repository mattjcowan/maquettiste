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

    /// <summary>A stored function or procedure (<c>routine</c>).</summary>
    [JsonStringEnumMemberName("routine")] Routine,

    /// <summary>A type a database owns: domain, composite, enumeration or range (<c>database-type</c>).</summary>
    [JsonStringEnumMemberName("database-type")] DatabaseType,

    /// <summary>A database object the model does not type, as SQL statements per dialect (<c>sql-object</c>).</summary>
    [JsonStringEnumMemberName("sql-object")] SqlObject,

    /// <summary>A query over a database's tables and views, written as data (<c>query</c>).</summary>
    [JsonStringEnumMemberName("query")] Query,

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

    /// <summary>A named set of rows that attributes use as their type (<c>reference-type</c>).</summary>
    [JsonStringEnumMemberName("reference-type")] ReferenceType,

    /// <summary>Rows of data for an entity, a relation or a reference type (<c>seed</c>).</summary>
    [JsonStringEnumMemberName("seed")] Seed,

    /// <summary>A statechart: an entity lifecycle or an orchestration (<c>process</c>).</summary>
    [JsonStringEnumMemberName("process")] Process,

    /// <summary>A person, role or external system that takes part in processes (<c>actor</c>).</summary>
    [JsonStringEnumMemberName("actor")] Actor,

    /// <summary>A recorded event sequence of one process (<c>scenario</c>).</summary>
    [JsonStringEnumMemberName("scenario")] Scenario,
}
