using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The members a table file inherits from its stereotypes, as validation sees them (next-phase-2-design.md section 3.1, D8): MQ2027,
/// MQ2028 and MQ4078 on the <c>redefines</c> and <c>excludes</c> of designed and imported table files through
/// <see cref="TableMembers.CheckLinks"/> (an overlay's links are judged by the resolver, which knows the stereotypes its table skips),
/// and the helpers the physical, seed and binding rules use to look a table's columns and keys up beside its inherited ones.
/// </summary>
internal static class MemberRules
{
    /// <summary>Judges the member links of one designed or imported table file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the document to validate.</param>
    public static void Validate(ValidationContext context, Report report)
    {
        if (report.Document.Element is not Table { Origin: not TableOrigin.Synthesized } table || (table.Redefines.Count == 0 && table.Excludes.Count == 0))
            return;
        foreach (var d in TableMembers.CheckLinks(TableMembers.Of(context.Model, table), table))
            report.Add(d.Rule, d.Message, d.JsonPointer ?? "", d.ElementId);
    }

    /// <summary>
    /// A designed or imported table file as the lookups of validation read it: its own columns, primary key, uniques and indexes, then
    /// the inherited ones, each with its keyed id and its column lists keyed (the table's own primary key wins; a second one is the
    /// resolver's MQ4076). An overlay, a table without members, and an instance that is not the snapshot's own (a copy this method
    /// returned, so it never adds the members twice) come back unchanged.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="file">The table file.</param>
    /// <returns>The table with its inherited members, or <paramref name="file"/>.</returns>
    public static Table WithMembers(ModelSnapshot model, Table file)
    {
        if (file.Origin == TableOrigin.Synthesized || !ReferenceEquals(model.Get<Table>(file.Id), file))
            return file;
        var set = TableMembers.Of(model, file);
        if (set.IsEmpty)
            return file;
        return file with
        {
            Columns = [.. file.Columns, .. set.Columns.Select(m => m.Member)],
            PrimaryKey = file.PrimaryKey ?? set.PrimaryKeys.FirstOrDefault()?.Member,
            Uniques = [.. file.Uniques, .. set.Uniques.Select(m => m.Member)],
            Indexes = [.. file.Indexes, .. set.Indexes.Select(m => m.Member)],
        };
    }

    /// <summary>
    /// Where a finding about a member a table file inherits goes: its <c>redefines</c> entry when it has one, else the contributing
    /// stereotype's own entry (<c>/stereotypes/&lt;n&gt;</c>), else <c>/stereotypes</c> (a default) or the file root.
    /// </summary>
    /// <param name="file">The table file.</param>
    /// <param name="memberId">The stereotype member's id.</param>
    /// <param name="from">The effective-set entry that contributes it.</param>
    /// <returns>The pointer.</returns>
    public static string PointerOf(Table file, string memberId, AppliedStereotype from)
    {
        for (var i = 0; i < file.Redefines.Count; i++)
        {
            if (string.Equals(file.Redefines[i].Member, memberId, StringComparison.Ordinal))
                return "/redefines/" + i.ToString(CultureInfo.InvariantCulture);
        }

        if (from.Entry >= 0)
            return "/stereotypes/" + from.Entry.ToString(CultureInfo.InvariantCulture);
        return file.Stereotypes.Count > 0 ? "/stereotypes" : "";
    }

    /// <summary>
    /// The sentence a finding adds when a value that should name a column of the table is the bare id of a column a stereotype gives it:
    /// the keyed id to write, or that the table excludes the member; <c>""</c> for any other value.
    /// </summary>
    /// <param name="set">The table's member set.</param>
    /// <param name="value">The value that names no column.</param>
    /// <returns>The hint, with a leading space, or <c>""</c>.</returns>
    public static string KeyedHint(TableMemberSet set, string value) => Hint(set, value, "column");

    /// <summary>As <see cref="KeyedHint"/>, for a value that should name a unique constraint of the table (a seed's row key).</summary>
    /// <param name="set">The table's member set.</param>
    /// <param name="value">The value that names no unique constraint.</param>
    /// <returns>The hint, with a leading space, or <c>""</c>.</returns>
    public static string KeyedUniqueHint(TableMemberSet set, string value) => Hint(set, value, "unique");

    private static string Hint(TableMemberSet set, string value, string kind)
    {
        if (!set.Inheritable.TryGetValue(value, out var member) || TableMembers.KindName(member) != kind)
            return "";
        var keyed = ContributedMembers.KeyOf(set.TableKey, value);
        var inherited = kind == "column"
            ? set.Columns.FirstOrDefault(c => string.Equals(c.Key, keyed, StringComparison.Ordinal))?.From
            : set.Uniques.FirstOrDefault(u => string.Equals(u.Key, keyed, StringComparison.Ordinal))?.From;
        var what = member is Column column ? $"column '{column.Name}'" : $"unique '{value}'";
        return inherited is { } from
            ? $" It is the {what} the table inherits from stereotype '{from.Stereotype.Key}': write '{keyed}'."
            : $" It is the {what} of a stereotype, which the table excludes.";
    }
}
