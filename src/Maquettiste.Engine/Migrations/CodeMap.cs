using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Migrations;

/// <summary>
/// What one reference-type refactor of a write changed in a type's codes and names (next-phase-2-design.md section 3.5, "Recording the
/// value map"). The batch's expansion builds one per operation (<c>ModelStore.ReferenceRefactors.cs</c>) and hands it to the write's
/// recording, as a materialize hands its key maps; the classifier turns it into one <c>value-map</c> fact per database that holds the
/// codes, or, for a type rename, per database without a binding of the type. Nothing infers a code map from documents.
/// </summary>
/// <param name="ReferenceType">The id of the type that holds the codes after the refactor (a merge's target).</param>
/// <param name="FromReferenceType">A merge's merged-away type; <see langword="null"/> otherwise.</param>
/// <param name="FromNames">The names before: the type's (a merge's merged-away type's) <c>name</c> and <c>pluralName</c>.</param>
/// <param name="ToNames">The names after.</param>
/// <param name="Values">The codes mapped away, sorted by <c>from</c>, never an identity entry and never a chain.</param>
/// <param name="Names">Whether the refactor renames the type, which reaches every database.</param>
internal sealed record CodeMap(string ReferenceType, string? FromReferenceType, MigrationState FromNames, MigrationState ToNames,
    IReadOnlyList<MigrationValue> Values, bool Names)
{
    /// <summary>The type whose stored codes the map changes: a merge's merged-away type, else the type itself.</summary>
    public string HeldType => FromReferenceType ?? ReferenceType;
}
