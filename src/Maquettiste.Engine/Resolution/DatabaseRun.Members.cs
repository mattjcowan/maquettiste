using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The members a table inherits from the stereotypes of its effective set (next-phase-2-design.md section 3.1): columns, a primary key,
/// uniques, foreign keys, indexes and checks, keyed <c>&lt;tableKey&gt;.&lt;memberId&gt;</c>, added after the table's own members and
/// resolved as a table file's are. A synthesized table skips the members of a stereotype whose virtual attributes it stores as columns.
/// </summary>
internal sealed partial class DatabaseRun
{
    /// <summary>
    /// Adds a table's inherited members, before positions and keys are computed: columns after the own ones (and the overlay's extras),
    /// then the primary key, uniques, foreign keys, indexes and checks as specs after the own ones, each with its origin. Reports MQ4076
    /// (a second primary key), MQ4079 (a NOT NULL member a synthesized table's rows cannot fill), MQ4003 (a synthesized table's duplicate
    /// column name), MQ2013 to MQ2015 for a table without a file, and an overlay's links (MQ2027, MQ2028, MQ4078, and MQ4008 for a key,
    /// index or check naming a member the table skips).
    /// </summary>
    private void AddInheritedMembers(TableBuild t)
    {
        JudgeTableWithoutFile(t);
        var file = t.Source ?? t.Overlay;
        if (t.Effective.Count == 0 && (file is null || (file.Redefines.Count == 0 && file.Excludes.Count == 0)))
            return;

        var skip = SkippedStereotypes(t);
        var set = TableMembers.Of(_run.Model, t.Table.Key, t.Effective, file?.Redefines ?? [], file?.Excludes ?? [], skip);
        if (t.Overlay is { } overlay)
        {
            // An overlay's links are judged here, where skip is known; designed and imported tables are judged by validation.
            foreach (var d in TableMembers.CheckLinks(set, overlay))
                _run.AddDiagnostic(d.Rule, d.Message, d.ElementId, d.JsonPointer);
            ReportSkippedMemberUses(t, overlay, skip);
        }

        if (set.IsEmpty)
            return;
        AddInheritedColumns(t, set);
        AddInheritedPrimaryKey(t, set);
        foreach (var m in set.Uniques)
        {
            var u = m.Member;
            t.Uniques.Add(new UniqueSpec(u.Columns, u.Name, null, m.Key, u.NullsNotDistinct, u.WithoutOverlaps, OriginOf(t, m)));
        }

        foreach (var m in set.ForeignKeys)
        {
            var f = m.Member;
            // A carrier without the parameter's value gets no key: MQ2018 already says so, so no second finding.
            if ((f.ReferencesTable ?? ParameterTarget(t, f.ReferencesParameter)) is not { } target)
                continue;
            var spec = new ForeignKeySpec(t, f.Columns, null, target, f.ReferencesColumns, ResolutionValues.Kebab(f.OnDelete),
                ResolutionValues.Kebab(f.OnUpdate), f.Name) { Id = m.Key, OnDeleteColumns = f.OnDeleteColumns, Inherited = (m.MemberId, m.From) };
            spec.Result.Deferrable = ResolutionValues.Kebab(f.Deferrable);
            spec.Result.Period = f.Period;
            spec.Result.MemberOrigin = OriginOf(t, m);
            t.ForeignKeys.Add(spec);
        }

        foreach (var m in set.Indexes)
        {
            var index = m.Member;
            // An expression without a text for the dialect leaves the index out, as for a table file's index.
            var columns = index.Columns.Select(c => c.Column is null
                ? new IndexColumnSpec(null, c.Descending, c.Expression is null ? null : ForDialect(c.Expression), c.Length, c.OperatorClass, NullsOrder(c))
                : new IndexColumnSpec(c.Column, c.Descending, null, c.Length, c.OperatorClass, NullsOrder(c))).ToList();
            if (columns.Any(c => c.Column is null && c.Expression is null))
                continue;
            // Inherited indexes count as declared indexes: an indexed attribute's index never replaces one.
            t.Indexes.Add(new IndexSpec(columns, index.Include, index.Where, index.Unique, ResolutionValues.Kebab(index.Method), index.Name,
                FromFile: true, Id: m.Key, Storage: Storage([index.Storage]), Origin: OriginOf(t, m)));
        }

        // Inherited checks continue the own checks' ordinal.
        var ordinal = t.Checks.Count;
        foreach (var m in set.Checks)
        {
            if (ForDialect(m.Member.Expression) is { } expression)
                t.Checks.Add(new CheckSpec(expression, m.Member.Name, ++ordinal, m.Key, m.Member.Column, OriginOf(t, m)));
        }
    }

    /// <summary>The stereotypes whose virtual attributes the table stores as columns (decision 1): it skips their members.</summary>
    private static HashSet<string> SkippedStereotypes(TableBuild t)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in t.Columns)
        {
            if (column.Attribute?.FromStereotype is { } from)
                skip.Add(from.Key);
        }

        return skip;
    }

    /// <summary>
    /// The inherited columns, built as a designed column is from the stereotype's column with the redefinition applied: the keyed id is
    /// the column's id and key, its annotations come from the member entry, and it stores no attribute.
    /// </summary>
    private void AddInheritedColumns(TableBuild t, TableMemberSet set)
    {
        // Designed and imported tables get MQ4003 from validation; a synthesized table's names are known only here.
        Dictionary<string, RColumn>? names = null;
        if (t.Source is null)
        {
            names = new Dictionary<string, RColumn>(StringComparer.OrdinalIgnoreCase);
            foreach (var own in t.Columns)
                names.TryAdd(own.Name, own);
        }

        foreach (var m in set.Columns)
        {
            var column = m.Member;
            var c = new RColumn
            {
                Id = m.Key,
                Key = m.Key,
                Name = column.Name,
                Table = t.Table,
                Type = column.Type ?? "string",
                Subtype = BuiltinTypes.IsRange(column.Type) ? column.Subtype : null,
                Length = column.Length,
                Precision = column.Precision,
                Scale = column.Scale,
                Nullable = column.Nullable ?? true,
                MemberOrigin = OriginOf(t, m),
            };
            ApplyColumnFile(t, c, column);
            var scalar = TypeColumn(t, c, column);
            ApplyFacetDefaults(c);
            ApplyNativeType(t, c, column.NativeType, () => ColumnNativeType(c, scalar));

            if (names is not null)
            {
                if (names.TryGetValue(c.Name, out var other))
                {
                    var (pinId, pointer) = MemberPin(t, m.MemberId, m.From);
                    _run.AddDiagnostic("MQ4003",
                        $"Column name '{c.Name}' that stereotype '{m.From.Stereotype.Key}' gives table '{t.Table.Name}' of database '{_db.Name}' is already used by {ColumnOrigin(other)}; exclude the member, or rename one of them.",
                        pinId, pointer);
                }
                else
                {
                    names.Add(c.Name, c);
                }

                if (!c.Nullable && c.Default is null && c.DefaultSql is null && !c.Identity && c.Sequence is null && c.Computed is null)
                {
                    var (pinId, pointer) = MemberPin(t, m.MemberId, m.From);
                    var writer = t.Table.Entity is { } entity ? $"entity '{entity.Name}'" : t.Table.Relation is { } relation ? $"relation '{relation.Name}'" : "its owner";
                    _run.AddDiagnostic("MQ4079",
                        $"Table '{t.Table.Name}' of database '{_db.Name}' inherits column '{c.Name}' from stereotype '{m.From.Stereotype.Key}' as NOT NULL with no default, identity or generation: rows written through {writer} cannot fill it. Give the column a default, or exclude it.",
                        pinId, pointer);
                }
            }

            t.Columns.Add(c);
            t.ByKey.TryAdd(m.Key, c);
            _run.Register(c);
        }
    }

    /// <summary>How a message names the column an inherited column's name collides with.</summary>
    private static string ColumnOrigin(RColumn column) =>
        column.MemberOrigin.Stereotype is { } stereotype ? $"column '{column.Name}' of stereotype '{stereotype.Key}'"
        : column.Attribute is { } attribute ? $"the column of attribute '{attribute.Name}'"
        : $"column '{column.Name}'";

    /// <summary>
    /// The inherited primary key: it becomes the table's key when the table has none of its own (its file's, or its entity's key); every
    /// other primary key, inherited beside an own one or a first inherited one, is MQ4076 and left out.
    /// </summary>
    private void AddInheritedPrimaryKey(TableBuild t, TableMemberSet set)
    {
        InheritedMember<PrimaryKey>? first = null;
        var own = t.PrimaryKey.Count > 0;
        foreach (var m in set.PrimaryKeys)
        {
            if (own || first is not null)
            {
                var beside = first is { } kept ? $"stereotype '{kept.From.Stereotype.Key}''s primary key '{kept.MemberId}'"
                    : t.Source is not null || t.Overlay?.PrimaryKey is not null ? "its own primary key"
                    : t.Table.Entity is not null ? "its entity's key"
                    : "its own key";
                var (pinId, pointer) = MemberPin(t, m.MemberId, m.From);
                _run.AddDiagnostic("MQ4076",
                    $"Table '{t.Table.Name}' of database '{_db.Name}' gets a second primary key: stereotype '{m.From.Stereotype.Key}' gives primary key '{m.MemberId}' beside {beside}, which stays. Exclude one, or redefine its columns.",
                    pinId, pointer);
                continue;
            }

            first = m;
            var pk = m.Member;
            t.PrimaryKey.Clear();
            t.PrimaryKey.AddRange(pk.Columns);
            t.PrimaryKeyName = pk.Name;
            t.PrimaryKeyClustered = pk.Clustered;
            t.PrimaryKeyWithoutOverlaps = pk.WithoutOverlaps;
            t.PrimaryKeyId = m.Key;
            t.PrimaryKeyOrigin = OriginOf(t, m);
        }
    }

    /// <summary>
    /// A contributed foreign key's target named by a stereotype parameter: the table id in the carrier's merged properties, or
    /// <see langword="null"/> when the carrier gives none.
    /// </summary>
    private static string? ParameterTarget(TableBuild t, string? parameter) =>
        parameter is not null && t.Table.Properties.TryGetValue(parameter, out var value) && value is string { Length: > 0 } id ? id : null;

    /// <summary>An inherited member's origin: the stereotype entry of the table that contributes it, its member id and the facets redefined.</summary>
    private static RMemberOrigin OriginOf<TMember>(TableBuild t, InheritedMember<TMember> member) where TMember : class => new()
    {
        Source = member.From.Source switch
        {
            StereotypeSource.SchemaDefault => "schema-default",
            StereotypeSource.PackageDefault => "package-default",
            _ => "stereotype",
        },
        Stereotype = StereotypeEntry(t, member.From),
        MemberId = member.MemberId,
        Redefined = member.Redefined,
    };

    /// <summary>The table's resolved entry for an applied stereotype (its annotations list the effective set in order).</summary>
    private static RStereotype StereotypeEntry(TableBuild t, AppliedStereotype from)
    {
        var stereotypes = t.Table.Stereotypes;
        for (var i = 0; i < t.Effective.Count && i < stereotypes.Count; i++)
        {
            if (t.Effective[i].Equals(from) && string.Equals(stereotypes[i].Key, from.Stereotype.Key, StringComparison.Ordinal))
                return stereotypes[i];
        }

        return ResolveRun.Summary(from);
    }

    /// <summary>
    /// Where a finding about an inherited member of one carrier goes: its <c>redefines</c> entry when it has one, else the contributing
    /// stereotype's own entry (<c>/stereotypes/&lt;n&gt;</c>), else <c>/stereotypes</c> (a default) or the file root; a table without a
    /// file reports on its entity or relation.
    /// </summary>
    private static (string ElementId, string? Pointer) MemberPin(TableBuild t, string memberId, AppliedStereotype from)
    {
        if ((t.Source ?? t.Overlay) is not { } file)
            return (t.SourceElementId, null);
        for (var i = 0; i < file.Redefines.Count; i++)
        {
            if (string.Equals(file.Redefines[i].Member, memberId, StringComparison.Ordinal))
                return (file.Id, "/redefines/" + i.ToString(CultureInfo.InvariantCulture));
        }

        if (from.Entry >= 0)
            return (file.Id, "/stereotypes/" + from.Entry.ToString(CultureInfo.InvariantCulture));
        return (file.Id, file.Stereotypes.Count > 0 ? "/stereotypes" : "");
    }

    /// <summary>
    /// MQ4008 for an overlay key, index or check that names a column of a stereotype the synthesized table skips (decision 1): validation
    /// judges an overlay on its candidate set, which still holds those columns, so only the resolver knows they are not there.
    /// </summary>
    private void ReportSkippedMemberUses(TableBuild t, Table overlay, HashSet<string> skip)
    {
        if (skip.Count == 0)
            return;
        var skipped = new Dictionary<string, (string MemberId, string Stereotype)>(StringComparer.Ordinal);
        foreach (var applied in t.Effective)
        {
            if (!skip.Contains(applied.Stereotype.Key))
                continue;
            foreach (var column in applied.Stereotype.Columns)
            {
                if (!string.IsNullOrEmpty(column.Id))
                    skipped.TryAdd(ContributedMembers.KeyOf(t.Table.Key, column.Id), (column.Id, applied.Stereotype.Key));
            }
        }

        if (skipped.Count == 0)
            return;

        void Check(string? column, string pointer)
        {
            if (column is null || !skipped.TryGetValue(column, out var hit))
                return;
            _run.AddDiagnostic("MQ4008",
                $"The overlay of table '{t.Table.Name}' of database '{_db.Name}' names column '{hit.MemberId}' of stereotype '{hit.Stereotype}', which the table does not inherit: it stores the stereotype's attributes as columns. Name the attribute's column instead.",
                overlay.Id, pointer);
        }

        void CheckAll(IReadOnlyList<string> columns, string list)
        {
            for (var i = 0; i < columns.Count; i++)
                Check(columns[i], list + "/" + i.ToString(CultureInfo.InvariantCulture));
        }

        static string At(string list, int i) => list + "/" + i.ToString(CultureInfo.InvariantCulture);

        if (overlay.PrimaryKey is { } pk)
            CheckAll(pk.Columns, "/primaryKey/columns");
        for (var u = 0; u < overlay.Uniques.Count; u++)
            CheckAll(overlay.Uniques[u].Columns, At("/uniques", u) + "/columns");
        for (var f = 0; f < overlay.ForeignKeys.Count; f++)
        {
            CheckAll(overlay.ForeignKeys[f].Columns, At("/foreignKeys", f) + "/columns");
            CheckAll(overlay.ForeignKeys[f].OnDeleteColumns, At("/foreignKeys", f) + "/onDeleteColumns");
        }

        for (var x = 0; x < overlay.Indexes.Count; x++)
        {
            var index = overlay.Indexes[x];
            for (var i = 0; i < index.Columns.Count; i++)
                Check(index.Columns[i].Column, At(At("/indexes", x) + "/columns", i) + "/column");
            CheckAll(index.Include, At("/indexes", x) + "/include");
        }

        for (var c = 0; c < overlay.Checks.Count; c++)
            Check(overlay.Checks[c].Column, At("/checks", c) + "/column");
    }

    /// <summary>
    /// MQ2013 to MQ2015 for a table without a file (an entity's, a relation's or a promoted table without an overlay): its effective set
    /// holds only what its schema and package defaults give, which no file's validation judges, so the findings go on its entity or
    /// relation at <c>/stereotypes</c>, naming the defaults that clash. A child table takes its owner's defaults, judged with its owner.
    /// </summary>
    private void JudgeTableWithoutFile(TableBuild t)
    {
        if (t.Source is not null || t.Overlay is not null || t.Table.Attribute is not null || t.Effective.Count == 0
            || _run.Model.GetDocument(t.SourceElementId) is not { } document)
            return;
        var report = new Report(document);
        BuiltinRules.JudgeEffectiveSet(_run.Model, t.Effective, _ => "/stereotypes", t.SourceElementId, "table", report);
        foreach (var d in report.Items)
        {
            if (d.Rule is not ("MQ2013" or "MQ2014" or "MQ2015"))
                continue;
            _run.AddDiagnostic(d.Rule,
                $"Table '{t.Table.Name}' of database '{_db.Name}', which has no table file: {d.Message} An overlay file for the table holds an excludeStereotypes entry.",
                d.ElementId, "/stereotypes");
        }
    }
}
