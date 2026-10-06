using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine;

/// <summary>A model snapshot as the list, the API and the CLI show it (docs/engineering/snapshots.md).</summary>
/// <param name="Id">The stable id: the file name without <c>.zip</c> (<c>&lt;slug&gt;-&lt;yyyymmdd-hhmmss&gt;</c>).</param>
/// <param name="Name">The name.</param>
/// <param name="Description">What it is for; empty when none.</param>
/// <param name="Author">Who took it; empty when unknown.</param>
/// <param name="CreatedUtc">When it was taken, <c>yyyy-MM-ddTHH:mm:ssZ</c>.</param>
/// <param name="Origin"><c>user</c>, or <c>before-restore</c> for the safety snapshot a restore takes first.</param>
/// <param name="Published">Whether it is marked as ready for review.</param>
/// <param name="IncludesPacks">Whether it holds the template packs.</param>
/// <param name="ModelHash">The hash of the model it was taken from (every document's path and hash, packs aside).</param>
/// <param name="ModelFormat">The model format it was taken at.</param>
/// <param name="Engine">The release that took it.</param>
/// <param name="Files">The number of documents it holds.</param>
/// <param name="Elements">The number of elements it holds.</param>
/// <param name="Kinds">The number of elements of each kind.</param>
/// <param name="Size">The archive's size in bytes.</param>
public sealed record SnapshotInfo(
    string Id, string Name, string Description, string Author, string CreatedUtc, string Origin, bool Published, bool IncludesPacks,
    string ModelHash, int ModelFormat, string Engine, int Files, int Elements, IReadOnlyDictionary<string, int> Kinds, long Size);

/// <summary>What to snapshot.</summary>
/// <param name="Name">The name (1 to 200 characters); the id is made from it and the time.</param>
/// <param name="Description">What it is for.</param>
/// <param name="IncludePacks">Whether to hold the template packs too (off by default), so generating from the snapshot reproduces exactly.</param>
/// <param name="Author">Who takes it.</param>
public sealed record SnapshotCreateRequest(string Name, string? Description = null, bool IncludePacks = false, string? Author = null);

/// <summary>A change to a snapshot's metadata; <see langword="null"/> keeps a value.</summary>
/// <param name="Name">The new name (the id does not change).</param>
/// <param name="Description">The new description.</param>
/// <param name="Published">Whether it is ready for review.</param>
public sealed record SnapshotUpdate(string? Name = null, string? Description = null, bool? Published = null);

/// <summary>A snapshot's elements that a comparison found added, removed or changed, for one kind.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Added">Elements in <c>to</c> only.</param>
/// <param name="Removed">Elements in <c>from</c> only.</param>
/// <param name="Changed">Elements whose document differs.</param>
public sealed record SnapshotKindChanges(string Kind, int Added, int Removed, int Changed);

/// <summary>One element a comparison found added, removed or changed.</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The name on the <c>to</c> side (the <c>from</c> side for a removed element).</param>
/// <param name="Change"><c>added</c>, <c>removed</c> or <c>changed</c>.</param>
/// <param name="Path">The model-relative path on the <c>to</c> side (<c>from</c> for a removed element).</param>
/// <param name="PreviousName">The name on the <c>from</c> side when it was renamed.</param>
/// <param name="PreviousPath">The path on the <c>from</c> side when the file moved.</param>
public sealed record SnapshotElementChange(
    string Id, string Kind, string Name, string Change, string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousPath = null);

/// <summary>A document that is not an element (settings, locale shards, extensions, sidecars, branding, packs) and differs.</summary>
/// <param name="Path">The model-relative path.</param>
/// <param name="Change"><c>added</c>, <c>removed</c> or <c>changed</c>.</param>
public sealed record SnapshotFileChange(string Path, string Change);

/// <summary>
/// What differs between two models (a snapshot or the working model on each side): documents are compared by hash first, elements by
/// id, so only changed documents are read. The element list is paged (<see cref="Next"/>); the field-level detail of one element comes
/// from <see cref="SnapshotLibrary.CompareElementAsync"/>.
/// </summary>
/// <param name="From">The <c>from</c> side: a snapshot id or <c>working</c>.</param>
/// <param name="To">The <c>to</c> side.</param>
/// <param name="Added">Elements added, over every kind.</param>
/// <param name="Removed">Elements removed.</param>
/// <param name="Changed">Elements changed.</param>
/// <param name="Kinds">The counts per kind, ordinal by kind.</param>
/// <param name="Elements">One page of the changed elements, by kind, name and id.</param>
/// <param name="Next">The offset of the next page, or <see langword="null"/> after the last.</param>
/// <param name="Files">The other documents that differ, by path (at most 1,000).</param>
/// <param name="FilesTruncated">Whether more documents differ than <paramref name="Files"/> lists.</param>
/// <param name="PacksCompared">Whether the packs were compared (both sides hold them).</param>
public sealed record SnapshotComparison(
    string From, string To, int Added, int Removed, int Changed, IReadOnlyList<SnapshotKindChanges> Kinds, IReadOnlyList<SnapshotElementChange> Elements,
    int? Next, IReadOnlyList<SnapshotFileChange> Files, bool FilesTruncated, bool PacksCompared);

/// <summary>One field that differs between the two documents of an element.</summary>
/// <param name="Pointer">The JSON pointer in the document (the <c>to</c> side's, the <c>from</c> side's for a removed value).</param>
/// <param name="Change"><c>added</c>, <c>removed</c> or <c>changed</c>.</param>
/// <param name="Before">The value on the <c>from</c> side.</param>
/// <param name="After">The value on the <c>to</c> side.</param>
public sealed record SnapshotFieldChange(string Pointer, string Change, JsonElement? Before, JsonElement? After);

/// <summary>
/// One element on both sides, in the form the editor's conflict view renders (the two whole documents, <see cref="Before"/> and
/// <see cref="After"/>), plus the fields that differ (at most 500; arrays of objects with ids are matched by id).
/// </summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The name (the <c>to</c> side's, else the <c>from</c> side's).</param>
/// <param name="Change"><c>added</c>, <c>removed</c>, <c>changed</c> or <c>unchanged</c>.</param>
/// <param name="FromPath">The model-relative path on the <c>from</c> side.</param>
/// <param name="ToPath">The path on the <c>to</c> side.</param>
/// <param name="FromHash">The document's hash on the <c>from</c> side.</param>
/// <param name="ToHash">The hash on the <c>to</c> side.</param>
/// <param name="Before">The document on the <c>from</c> side.</param>
/// <param name="After">The document on the <c>to</c> side.</param>
/// <param name="Fields">The fields that differ.</param>
/// <param name="FieldsTruncated">Whether more fields differ than <paramref name="Fields"/> lists.</param>
public sealed record SnapshotElementDiff(
    string Id, string Kind, string Name, string Change, string? FromPath, string? ToPath, string? FromHash, string? ToHash,
    JsonElement? Before, JsonElement? After, IReadOnlyList<SnapshotFieldChange> Fields, bool FieldsTruncated);

/// <summary>How to restore a snapshot.</summary>
/// <param name="IncludePacks">Whether to restore its packs too (only when it holds them).</param>
/// <param name="Author">Who restores it (the safety snapshot's author).</param>
/// <param name="Source">What the change to the model is recorded as.</param>
public sealed record SnapshotRestoreRequest(bool IncludePacks = false, string? Author = null, ChangeSource Source = ChangeSource.Editor);

/// <summary>The outcome of a restore.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SnapshotRestoreOutcome>))]
public enum SnapshotRestoreOutcome
{
    /// <summary>The working model is now the snapshot's: <c>restored</c>.</summary>
    [JsonStringEnumMemberName("restored")] Restored,

    /// <summary>No snapshot has the id: <c>not-found</c>.</summary>
    [JsonStringEnumMemberName("not-found")] NotFound,

    /// <summary>A generation run holds the run lock: <c>locked</c>; nothing changed.</summary>
    [JsonStringEnumMemberName("locked")] Locked,

    /// <summary>The write was refused (MQ6004, MQ6029): <c>refused</c>; nothing changed.</summary>
    [JsonStringEnumMemberName("refused")] Refused,
}

/// <summary>What a restore did.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Snapshot">The snapshot restored.</param>
/// <param name="Safety">The snapshot of the working model taken just before (<c>before-restore-…</c>); restoring it undoes the restore.</param>
/// <param name="Written">Documents written (only those that differed).</param>
/// <param name="Deleted">Documents deleted (in the working model, not in the snapshot).</param>
/// <param name="PacksRestored">Whether the packs were restored.</param>
/// <param name="Undo">How to undo it, in one sentence.</param>
/// <param name="ElementsChanged">Elements the model's change set reports as changed or added.</param>
/// <param name="ElementsDeleted">Elements it reports as deleted.</param>
/// <param name="Diagnostics">Why it was refused.</param>
public sealed record SnapshotRestoreResult(
    SnapshotRestoreOutcome Outcome, SnapshotInfo? Snapshot, SnapshotInfo? Safety, int Written, int Deleted, bool PacksRestored, string? Undo,
    int ElementsChanged, int ElementsDeleted, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The model's change set (realtime clients got it through <see cref="ModelStore.OnChanged"/>); not serialized.</summary>
    [JsonIgnore]
    public ChangeSet? Changes { get; init; }
}

/// <summary>What an import did.</summary>
/// <param name="Snapshot">The stored snapshot, or <see langword="null"/> when refused.</param>
/// <param name="Diagnostics">Why it was refused (MQ1011, and MQ1001, MQ1003 or MQ1007 for a document).</param>
/// <param name="TooLarge">Whether it was refused for its size.</param>
public sealed record SnapshotImportResult(SnapshotInfo? Snapshot, IReadOnlyList<Diagnostic> Diagnostics, bool TooLarge = false);
