using System.Collections.Frozen;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolved custom element (next-phase-2-design.md section 4.2): an element of a kind the project declares in
/// <c>extensions/kinds/&lt;type&gt;.json</c>, with the common annotations, its custom kind's names and its content as template values.
/// A content value at an <c>x-member</c> array is an <see cref="RList{T}"/> of <see cref="RMember"/>.
/// </summary>
public sealed class RCustom : RElement
{
    /// <inheritdoc/>
    public override string Kind => "custom";

    /// <summary>The custom kind's type key.</summary>
    public string Type { get; internal set; } = "";

    /// <summary>The custom kind's display name, falling back to <see cref="Type"/>.</summary>
    public string TypeDisplayName { get; internal set; } = "";

    /// <summary>The custom kind's plural name, falling back to the inflected <see cref="TypeDisplayName"/>.</summary>
    public string TypePluralName { get; internal set; } = "";

    /// <summary>The content as written, by key in ordinal order: plain values, with each member array as a list of members.</summary>
    public IReadOnlyDictionary<string, object?> Content { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Every member at any depth, in walk order (content keys ordinal, items by index, a member before its nested ones).</summary>
    public RList<RMember> Members { get; internal set; } = RList<RMember>.Empty;
}

/// <summary>
/// A member of a custom element (next-phase-2-design.md section 4.2): one item of an <c>x-member</c> array, a sub-element with an id
/// the engine owns. Its own keys, <c>id</c> aside, are its <see cref="Content"/>, converted as the element's content is.
/// </summary>
public sealed class RMember : RObject
{
    /// <inheritdoc/>
    public override string Kind => "member";

    /// <summary>The member's <c>name</c> when its item schema declares a string <c>name</c> and the item holds one.</summary>
    public string? Name { get; internal set; }

    /// <summary>The key of the array that holds it.</summary>
    public string Array { get; internal set; } = "";

    /// <summary>The custom element that owns it.</summary>
    public RCustom Owner { get; internal set; } = null!;

    /// <summary>The innermost member that holds it, or <see langword="null"/> for a member directly under the element's content.</summary>
    public RMember? Parent { get; internal set; }

    /// <summary>The item's keys but <c>id</c>, ordinal: plain values, with each nested member array as a list of members.</summary>
    public IReadOnlyDictionary<string, object?> Content { get; internal set; } = FrozenDictionary<string, object?>.Empty;
}
