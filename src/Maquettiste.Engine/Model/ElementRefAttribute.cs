namespace Maquettiste.Engine.Model;

/// <summary>
/// Marks a model property that holds an id (or a list of ids) of another element. <see cref="ModelSnapshot"/> builds its
/// reverse index from these attributes, and validation checks each one for dangling references (MQ2001) and wrong kinds (MQ2002).
/// </summary>
/// <param name="targets">The element kinds the reference may point at; empty (with no <see cref="IndexKinds"/>) means any element.</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ElementRefAttribute(params ElementKind[] targets) : Attribute
{
    /// <summary>The element kinds the reference may point at.</summary>
    public IReadOnlyList<ElementKind> Targets { get; } = targets;

    /// <summary>
    /// Sub-element index kinds the reference may point at (<see cref="IndexEntry.Kind"/> values such as <c>attribute</c>,
    /// <c>end</c>, <c>column</c>, <c>category</c>, <c>schema</c>).
    /// </summary>
    public string[] IndexKinds { get; set; } = [];

    /// <summary>
    /// Whether the value is a physical key (engine-design.md section 7.3) rather than a plain id: a table id or synthesized table key
    /// (<c>&lt;entityId&gt;@&lt;databaseId&gt;</c>, <c>&lt;entityId&gt;.&lt;attributeId&gt;@&lt;databaseId&gt;</c>…), a column id or
    /// column key (<c>&lt;attrId&gt;.&lt;memberAttrId&gt;</c>, <c>&lt;endId&gt;.&lt;keyAttrId&gt;</c>, <c>position</c>…). The value is
    /// split on <c>@</c> and <c>.</c>, and every segment that is a valid id is a reference; other segments (<c>id</c>,
    /// <c>position</c>, <c>discriminator</c>) are not. Keyed references point at any element or sub-element.
    /// </summary>
    public bool Keyed { get; set; }

    /// <summary>Whether the reference is owning: the holder belongs to the target and is deleted with it (a seed's target).</summary>
    public bool Owning { get; set; }

    /// <summary>Whether the reference may point at any element (no targets and no index kinds).</summary>
    public bool IsAny => Targets.Count == 0 && IndexKinds.Length == 0;
}
