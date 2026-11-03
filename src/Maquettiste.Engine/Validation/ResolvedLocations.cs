using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Validation;

/// <summary>Where a finding on a resolved object goes: a file and the pointer of the object's entry in it.</summary>
/// <param name="FilePath">The file, or <see langword="null"/> when no file holds the object.</param>
/// <param name="JsonPointer">The entry's pointer in the file, or <see langword="null"/>.</param>
/// <param name="Holder">Whether the location is another object's (a holder, a table, an entity) rather than the object's own entry, so
/// a pointer relative to the object no longer applies.</param>
internal sealed record ResolvedLocation(string? FilePath, string? JsonPointer, bool Holder);

/// <summary>
/// Locates the findings of the checks after resolution (next-phase-2-design.md section 5.5): the first rule that applies gives a
/// resolved object its file and pointer. 1. an attribute whose site is <c>&lt;holder id&gt;/&lt;attribute id&gt;</c> takes its
/// holder's location; 2. an id the index holds, its owner's file and the entry's pointer; 3. a column its table's location, and a table
/// without an index entry its overlay file (<see cref="RTable.FileId"/>), else the location of its entity, relation or owning attribute;
/// 4. any other synthesized key (<c>&lt;id&gt;.sequence@&lt;databaseId&gt;</c>, <c>&lt;a&gt;.&lt;b&gt;</c>, <c>&lt;a&gt;/&lt;b&gt;</c>)
/// the location of the id it starts with; 5. otherwise no file.
/// </summary>
internal static class ResolvedLocations
{
    /// <summary>The location of a resolved object.</summary>
    /// <param name="value">The object.</param>
    /// <param name="snapshot">The snapshot it was resolved from.</param>
    /// <returns>The location.</returns>
    public static ResolvedLocation Of(IResolvedObject value, ModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(snapshot);
        return Locate(value, snapshot, 0);
    }

    /// <summary>A raw finding on an object, located: its pointer (relative to the object's entry) joins the entry's, or is dropped
    /// when the location is a holder's; the finding keeps its element id.</summary>
    /// <param name="finding">The finding.</param>
    /// <param name="value">The object it is about.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The located finding.</returns>
    public static Diagnostics.Diagnostic Place(Diagnostics.Diagnostic finding, IResolvedObject value, ModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.FilePath is not null)
            return finding;
        var at = Of(value, snapshot);
        var pointer = at.FilePath is null ? null : at.Holder ? at.JsonPointer : (at.JsonPointer ?? "") + (finding.JsonPointer ?? "");
        return finding with { FilePath = at.FilePath, JsonPointer = pointer, ElementId = finding.ElementId ?? value.Id };
    }

    private static ResolvedLocation Locate(IResolvedObject value, ModelSnapshot snapshot, int depth)
    {
        if (depth > 8)
            return new ResolvedLocation(null, null, true);
        if (value is RAttribute { Owner: { } holder } attribute
            && !string.Equals(ResolvedKinds.SiteOf(attribute, snapshot), attribute.Id, StringComparison.Ordinal))
            return Locate(holder, snapshot, depth + 1) with { Holder = true };
        if (OfId(value.Id, snapshot) is { } own)
            return own;
        switch (value)
        {
            case RColumn column:
                return Locate(column.Table, snapshot, depth + 1) with { Holder = true };
            case RTable table:
                if (table.FileId is { } file && OfId(file, snapshot) is { } overlay)
                    return overlay with { Holder = true };
                IResolvedObject? from = (IResolvedObject?)table.Entity ?? (IResolvedObject?)table.Relation ?? table.Attribute;
                if (from is not null)
                    return Locate(from, snapshot, depth + 1) with { Holder = true };
                break;
        }

        var cut = value.Id.IndexOfAny(['.', '/', '@']);
        if (cut > 0 && OfId(value.Id[..cut], snapshot) is { } prefix)
            return prefix with { Holder = true };
        return new ResolvedLocation(null, null, true);
    }

    private static ResolvedLocation? OfId(string id, ModelSnapshot snapshot)
    {
        if (!snapshot.TryGetEntry(id, out var entry) || snapshot.GetDocument(entry.OwnerId) is not { } document)
            return null;
        return new ResolvedLocation(document.Path, entry.JsonPointer, false);
    }
}
