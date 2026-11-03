using System.Text.Json.Serialization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>
/// How far a batch reaches beyond its own files (next-phase-2-design.md section 6.1): the elements that point at what it changes,
/// the databases it can reach and, when the generation service ran the dry run, per pack the units whose stored inputs it touches.
/// Every list is ordinal, so two dry runs of one batch on one model answer the same bytes.
/// </summary>
/// <param name="Referrers">At most <see cref="MaxReferrers"/>, by target, referring element and pointer.</param>
/// <param name="ReferrersTotal">How many there are.</param>
/// <param name="Databases">The databases the change can reach, by name then id.</param>
public sealed record BatchImpact(IReadOnlyList<ImpactReferrer> Referrers, int ReferrersTotal, IReadOnlyList<ImpactDatabase> Databases)
{
    /// <summary>The most referrers an impact lists.</summary>
    public const int MaxReferrers = 200;

    /// <summary>Per enabled pack, in pack order; <see langword="null"/> (and left out of the JSON) when the dry run ran on the store
    /// alone.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ImpactPack>? Packs { get; init; }
}

/// <summary>A reference, on the model before the change, to something the change alters or deletes.</summary>
/// <param name="Target">The id the change touches.</param>
/// <param name="Change"><c>changed</c> or <c>deleted</c>.</param>
/// <param name="FromKind">The kind of the referring element.</param>
/// <param name="FromName">Its name, when it has one.</param>
/// <param name="Reference">The reference.</param>
public sealed record ImpactReferrer(string Target, string Change, string FromKind, string? FromName, ReferenceInfo Reference);

/// <summary>A database the change can reach.</summary>
/// <param name="Id">The database id.</param>
/// <param name="Name">Its name after the change.</param>
public sealed record ImpactDatabase(string Id, string Name);

/// <summary>A pack's units whose stored inputs the change touches.</summary>
/// <param name="Pack">The pack name.</param>
/// <param name="State"><c>known</c> (unit states were read), <c>none</c> (no unit states) or <c>invalid</c> (the pack does not load).</param>
/// <param name="Units">At most <see cref="MaxUnits"/>, by key.</param>
/// <param name="UnitsTotal">How many there are.</param>
public sealed record ImpactPack(string Pack, string State, IReadOnlyList<ImpactUnit> Units, int UnitsTotal)
{
    /// <summary>The most units a pack lists.</summary>
    public const int MaxUnits = 200;
}

/// <summary>A unit the change touches.</summary>
/// <param name="Key">The unit key.</param>
/// <param name="Change"><c>inputs</c>, <c>deleted</c> or <c>added</c>.</param>
/// <param name="Keys">At most five touched read keys, ordinal; empty for added.</param>
/// <param name="Outputs">The output paths the unit's state records, ordinal; empty for added.</param>
public sealed record ImpactUnit(string Key, string Change, IReadOnlyList<string> Keys, IReadOnlyList<string> Outputs)
{
    /// <summary>An added unit whose <c>where</c> filter was not evaluated; left out of the JSON when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Filtered { get; init; }
}

/// <summary>
/// A dry run as the store ran it (<see cref="ModelStore.PreviewBatchCoreAsync"/>): the preview, and when it would save, the model
/// before, the candidate and the ids of the elements the change created, updated, deleted or rewrote, ordinal.
/// </summary>
/// <param name="Preview">The preview.</param>
/// <param name="Before">The model before the change, or <see langword="null"/> when it would not save.</param>
/// <param name="Candidate">The model after it, or <see langword="null"/> when it would not save.</param>
/// <param name="Changed">The changed element ids, ordinal.</param>
internal sealed record BatchPreviewRun(BatchPreview Preview, ModelSnapshot? Before, ModelSnapshot? Candidate, IReadOnlyList<string> Changed);
