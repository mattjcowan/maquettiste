namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The pass after every database (next-phase-2-design.md section 3.2, D23): what a statement made on one side shows on the other once
/// every database is resolved. Sensitivity is said once, on a column or on an attribute (decision 9). A column shows its own value, else
/// a projected column's attribute's own, else the strongest own value of the attributes bound to it (set in each database run, after its
/// bindings). Here an entity attribute that states none shows the strongest own value of the columns its entity's bindings map it to
/// (read from and written to), and depends on those columns' tables whether or not they state one, so a later <c>sensitive</c> on a
/// column replans what reads the attribute. Each side reads only the other's own value, so nothing cycles. A value object member a
/// field maps is shared by every embedding of its value object and is left alone. One-database runs skip this pass: nothing they show
/// reads attributes.
/// <para>
/// The same pass gives an attribute with no description of its own the comment of the column its entity's bindings map it to (section
/// 3.3, decision 12, D34): the first column with a comment, bindings in database name order (the order of
/// <see cref="REntity.Bindings"/>), the read column before the write column. Such an attribute depends on those columns' tables whether
/// or not they carry a comment, so a comment added later replans what reads it. A projected column's comment was set in its database
/// run from its attribute's own description, before this pass, so nothing cycles. Value object members and relation keys are left out.
/// </para>
/// </summary>
internal sealed partial class ResolveRun
{
    /// <summary>The stronger of two sensitivities (<c>secret</c> over <c>pii</c>); <see langword="null"/> only when both are.</summary>
    /// <param name="a">A sensitivity, or <see langword="null"/>.</param>
    /// <param name="b">Another, or <see langword="null"/>.</param>
    /// <returns>The stronger one.</returns>
    internal static string? Stronger(string? a, string? b) => a == "secret" || b == "secret" ? "secret" : a ?? b;

    /// <summary>Runs after <see cref="FinishConceptual"/>, once the entities' bindings of every database are published.</summary>
    private void ResolveShared()
    {
        // Every attribute's own values are read before any is set, in entity, database-name and field order.
        Dictionary<RAttribute, string?>? shown = null;
        Dictionary<RAttribute, RColumn?>? described = null;
        foreach (var entity in _entityOrder)
        {
            foreach (var binding in entity.Bindings.Values)
            {
                foreach (var field in binding.Fields)
                {
                    if (field.Member is not null || field.Attribute is not { } attribute)
                        continue;
                    var borrowsSensitivity = attribute.Sensitive is null;
                    var borrowsDescription = attribute.Description is null;
                    if (!borrowsSensitivity && !borrowsDescription)
                        continue;
                    string? value = null;
                    if (borrowsSensitivity)
                        (shown ??= new Dictionary<RAttribute, string?>(ReferenceEqualityComparer.Instance)).TryGetValue(attribute, out value);
                    RColumn? commented = null;
                    if (borrowsDescription)
                        (described ??= new Dictionary<RAttribute, RColumn?>(ReferenceEqualityComparer.Instance)).TryGetValue(attribute, out commented);
                    var deps = DepsOf(attribute);
                    foreach (var column in new[] { field.SourceColumn, field.WriteColumn })
                    {
                        if (column is null)
                            continue;
                        deps.AddRange(column.Table.Dependencies);
                        if (borrowsSensitivity)
                            value = Stronger(value, column.OwnSensitive);
                        if (borrowsDescription && commented is null && !string.IsNullOrEmpty(column.Comment))
                            commented = column;
                    }

                    if (borrowsSensitivity)
                        shown![attribute] = value;
                    if (borrowsDescription)
                        described![attribute] = commented;
                }
            }
        }

        if (shown is not null)
        {
            foreach (var (attribute, value) in shown)
                attribute.Sensitive = value;
        }

        if (described is not null)
        {
            foreach (var (attribute, column) in described)
            {
                if (column is null)
                    continue;
                attribute.Description = column.Comment;
                attribute.DescriptionColumn = column;
            }
        }
    }
}
