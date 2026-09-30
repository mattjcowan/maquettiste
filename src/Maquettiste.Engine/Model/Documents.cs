using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>One loaded model file.</summary>
/// <param name="Element">The deserialized element.</param>
/// <param name="Path">The repo-relative path with <c>/</c> separators, for example <c>.maquettiste/model/entities/invoice.json</c>.</param>
/// <param name="Hash">The SHA-256 of the file bytes, lowercase hex: the element's ETag (host-contracts requirement 11).</param>
/// <param name="DependencyHash"><c>H(Hash, sidecar hash)</c>: what <c>e:&lt;id&gt;</c> dependency keys hash to.</param>
/// <param name="Json">The parsed file, as read.</param>
/// <param name="SidecarText">The sidecar description text, when the description is a file reference that was found.</param>
public sealed record ElementDocument(Element Element, string Path, string Hash, string DependencyHash, JsonElement Json, string? SidecarText);

/// <summary>An entry in the id index: an element or a sub-element.</summary>
/// <param name="Id">The id.</param>
/// <param name="OwnerId">The id of the top-level element whose file holds it (equal to <paramref name="Id"/> for an element).</param>
/// <param name="Kind">An element kind name, or <c>attribute</c>, <c>enum-member</c>, <c>end</c>, <c>column</c>, <c>category</c>, <c>schema</c> or <c>key</c>.</param>
/// <param name="JsonPointer">The pointer to the object in its owner's file (empty for an element).</param>
public sealed record IndexEntry(string Id, string OwnerId, string Kind, string JsonPointer);

/// <summary>One reference from a model file to an id.</summary>
/// <param name="FromElementId">The id of the top-level element whose file holds the reference.</param>
/// <param name="FromId">The id of the innermost element or sub-element holding the reference.</param>
/// <param name="JsonPointer">The pointer to the reference value (including the list index for list references).</param>
/// <param name="Field">The JSON property name holding the reference.</param>
/// <param name="ToId">The referenced id.</param>
/// <param name="Owning">Whether the referrer belongs to the referenced element (a seed's <c>target</c>): an owning reference never
/// refuses the target's delete, which deletes the referrer with it (reference-types-seeds-localization.md section 2.7). Left out of
/// the JSON when false.</param>
public sealed record ReferenceInfo(string FromElementId, string FromId, string JsonPointer, string Field, string ToId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Owning = false);

/// <summary>A summary of one element, for explorers and indexes.</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Name">The element name.</param>
/// <param name="Package">The id of the owning (or, for a package, parent) package, when it has one.</param>
/// <param name="Tags">The element's tag keys.</param>
/// <param name="Hash">The file hash (ETag).</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Category">The id of the element's category-tree node, when it has one (E4: the explorer's category filter).</param>
/// <param name="Stereotypes">The element's stereotype keys, in application order (E4: the explorer's stereotype filter).</param>
/// <param name="DisplayName">The element's display name, when it has one (E5: search and the explorer's label).</param>
/// <param name="Database">The owning database's id, on table, view, sequence and mapping rows (E5).</param>
/// <param name="Entity">The entity a mapping maps or a table overlay applies to, when it has one (E5).</param>
/// <param name="MemberCount">The number of members, on diagram rows (E5).</param>
/// <param name="Ends">The relation's ends in document order, on relation rows (E5).</param>
/// <param name="Target">The id of the entity, relation or reference type a seed holds rows for, on seed rows.</param>
/// <param name="RowCount">The number of rows, on seed rows.</param>
/// <param name="FieldCount">The number of user fields, on reference type rows.</param>
/// <param name="Base">The base entity's id, on entity rows that have one (E5h: base and derived entities in related-element highlighting).</param>
/// <param name="Use">The process's use, <c>lifecycle</c> or <c>orchestration</c>, on process rows (phase-3-design.md section 2.1).</param>
/// <param name="Subject">The entity whose lifecycle the process is, on process rows that have one.</param>
/// <param name="StateCount">The number of states at every depth, on process rows.</param>
/// <param name="ActorType">The actor's type, <c>person</c>, <c>role</c> or <c>external-system</c>, on actor rows.</param>
/// <param name="Process">The process a scenario runs, on scenario rows; the process a diagram is the statechart of, on process diagram rows.</param>
/// <param name="StepCount">The number of steps, on scenario rows.</param>
/// <remarks>
/// The E5 members are left out of the JSON when they are <see langword="null"/>, so rows of other kinds cost nothing. Equality compares
/// the lists by their items, so two indexes built from the same documents are equal whatever lists they hold.
/// </remarks>
public sealed record ElementSummary(string Id, string Kind, string Name, string? Package, IReadOnlyList<string> Tags, string Hash, string Path,
    string? Category, IReadOnlyList<string> Stereotypes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Entity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MemberCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RelationEndSummary>? Ends = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RowCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FieldCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Use = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subject = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StateCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActorType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Process = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StepCount = null)
{
    /// <inheritdoc/>
    public bool Equals(ElementSummary? other) =>
        other is not null
        && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Package, other.Package, StringComparison.Ordinal)
        && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal)
        && string.Equals(Hash, other.Hash, StringComparison.Ordinal)
        && string.Equals(Path, other.Path, StringComparison.Ordinal)
        && string.Equals(Category, other.Category, StringComparison.Ordinal)
        && Stereotypes.SequenceEqual(other.Stereotypes, StringComparer.Ordinal)
        && string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal)
        && string.Equals(Database, other.Database, StringComparison.Ordinal)
        && string.Equals(Entity, other.Entity, StringComparison.Ordinal)
        && MemberCount == other.MemberCount
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && RowCount == other.RowCount
        && FieldCount == other.FieldCount
        && string.Equals(Base, other.Base, StringComparison.Ordinal)
        && string.Equals(Use, other.Use, StringComparison.Ordinal)
        && string.Equals(Subject, other.Subject, StringComparison.Ordinal)
        && StateCount == other.StateCount
        && string.Equals(ActorType, other.ActorType, StringComparison.Ordinal)
        && string.Equals(Process, other.Process, StringComparison.Ordinal)
        && StepCount == other.StepCount
        && (ReferenceEquals(Ends, other.Ends) || (Ends is not null && other.Ends is not null && Ends.SequenceEqual(other.Ends)));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Id), StringComparer.Ordinal.GetHashCode(Hash), StringComparer.Ordinal.GetHashCode(Path));
}

/// <summary>One end of a relation, on the relation's index row (E5).</summary>
/// <param name="Entity">The id of the entity at this end.</param>
/// <param name="Role">The end's role name.</param>
public sealed record RelationEndSummary(string Entity, string Role);

/// <summary>A loaded extension schema file.</summary>
/// <param name="Schema">The extension.</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Hash">The file hash.</param>
public sealed record ExtensionDocument(ExtensionSchema Schema, string Path, string Hash);
