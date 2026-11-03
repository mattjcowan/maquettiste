using System.Text.Json;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;

namespace Maquettiste.Engine.Snapshots;

/// <summary>
/// Names the sub-element a field change belongs to (next-phase-2-design.md section 6.2, decision 4): the innermost object with an
/// <c>id</c> on its pointer, never the element. Its kind is the index kind of the record type the pointer reaches (the walk
/// <see cref="IdMinter"/> shares; a custom element's <c>content</c> holds members), <c>sub-element</c> when unknown; its name is the
/// first text of <c>name</c>, <c>role</c>, <c>code</c>, <c>key</c> and <c>label</c>, on the <c>to</c> side, else the <c>from</c> side.
/// </summary>
/// <param name="kind">The element's kind, as its index row states it.</param>
internal sealed class FieldOwners(string? kind)
{
    private static readonly string[] NameKeys = ["name", "role", "code", "key", "label"];

    /// <summary>The owner a comparison descends into at an object of both sides, or the current one when the object has no id.</summary>
    /// <param name="current">The owner so far, or <see langword="null"/> at the element's own fields.</param>
    /// <param name="pointer">The object's pointer.</param>
    /// <param name="before">The object on the <c>from</c> side.</param>
    /// <param name="after">The object on the <c>to</c> side.</param>
    /// <returns>The owner of the fields below the object.</returns>
    public Owner? Enter(Owner? current, string pointer, JsonElement before, JsonElement after)
    {
        if (pointer.Length == 0 || (IdOf(after) ?? IdOf(before)) is not { } id)
            return current;
        return new Owner(id, KindOf(pointer), NameOf(after) ?? NameOf(before), pointer);
    }

    /// <summary>A field change with its owner: the value itself when a whole object with an id was added or removed, else
    /// <paramref name="current"/>.</summary>
    /// <param name="change">The field change.</param>
    /// <param name="current">The owner of the fields where it was found.</param>
    /// <returns>The change, with its <c>sub*</c> members when it has an owner.</returns>
    public SnapshotFieldChange Attribute(SnapshotFieldChange change, Owner? current)
    {
        var whole = change.Change switch
        {
            "added" => change.After,
            "removed" => change.Before,
            _ => null,
        };
        if (change.Pointer.Length > 0 && whole is { } value && IdOf(value) is { } id)
            return change with { SubId = id, SubKind = KindOf(change.Pointer), SubName = NameOf(value), SubPointer = "" };
        if (current is null || !change.Pointer.StartsWith(current.Pointer, StringComparison.Ordinal))
            return change;
        return change with { SubId = current.Id, SubKind = current.Kind, SubName = current.Name, SubPointer = change.Pointer[current.Pointer.Length..] };
    }

    /// <summary>A sub-element's name: the first text of <c>name</c>, <c>role</c>, <c>code</c>, <c>key</c> and <c>label</c>.</summary>
    /// <param name="value">The sub-element's object.</param>
    /// <returns>The name, or <see langword="null"/>.</returns>
    public static string? NameOf(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in NameKeys)
        {
            if (value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } s)
                return s;
        }

        return null;
    }

    private static string? IdOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && id.GetString() is { Length: > 0 } text ? text : null;

    private string KindOf(string pointer)
    {
        if (kind is null || !JsonPointer.TryParse(pointer, out var segments) || segments.Length == 0)
            return "sub-element";
        if (IdMinter.KindAt(kind, segments) is { } found)
            return found;
        return kind == "custom" && segments[0] == "content" ? "member" : "sub-element";
    }

    /// <summary>The sub-element that owns the fields below its pointer.</summary>
    /// <param name="Id">Its id.</param>
    /// <param name="Kind">Its kind.</param>
    /// <param name="Name">Its name, or <see langword="null"/>.</param>
    /// <param name="Pointer">Its pointer in the document.</param>
    internal sealed record Owner(string Id, string Kind, string? Name, string Pointer);
}
