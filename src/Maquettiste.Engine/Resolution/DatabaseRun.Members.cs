using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The members a table inherits from the stereotypes of its effective set (next-phase-2-design.md section 3.1): columns, a primary key,
/// uniques, foreign keys, indexes and checks, keyed <c>&lt;tableKey&gt;.&lt;memberId&gt;</c>, added after the table's own members and
/// resolved as a table file's are.
/// </summary>
internal sealed partial class DatabaseRun
{
    /// <summary>
    /// Adds a table's inherited members, before positions and keys are computed: columns after the own ones, then the primary key,
    /// uniques, foreign keys, indexes and checks as specs after the own ones, each with its origin. Reports MQ4076 (a second primary key).
    /// The table file's links (MQ2027, MQ2028, MQ4078) are judged by validation.
    /// </summary>
    private void AddInheritedMembers(TableBuild t)
    {
        var file = t.Source;
        if (t.Effective.Count == 0 && file.Redefines.Count == 0 && file.Excludes.Count == 0)
            return;

        var set = TableMembers.Of(_run.Model, t.Table.Key, t.Effective, file.Redefines, file.Excludes, new HashSet<string>(StringComparer.Ordinal));
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

        // Inherited checks continue the own checks' ordinal; one in the expression language reads the stereotype's columns, keyed in the table.
        var ordinal = t.Checks.Count;
        foreach (var m in set.Checks)
        {
            if (m.Member.Expression.Count == 0 && m.Member.Condition is not null)
            {
                var (pinId, pointer) = MemberPin(t, m.MemberId, m.From);
                if (TypeCheck(t, m.From.Stereotype, m.MemberId, m.Member.Name, set.TableKey, pinId, pointer) is { } tree)
                    t.Checks.Add(new CheckSpec("", m.Member.Name, ++ordinal, m.Key, m.Member.Column, OriginOf(t, m)) { Tree = tree, Properties = m.Member.Properties });
                continue;
            }

            if (ForDialect(m.Member.Expression) is { } expression)
                t.Checks.Add(new CheckSpec(expression, m.Member.Name, ++ordinal, m.Key, m.Member.Column, OriginOf(t, m)) { Properties = m.Member.Properties });
        }
    }

    /// <summary>
    /// The inherited columns, built as a designed column is from the stereotype's column with the redefinition applied: the keyed id is
    /// the column's id and key, its annotations come from the member entry, and it stores no attribute.
    /// </summary>
    private void AddInheritedColumns(TableBuild t, TableMemberSet set)
    {
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
            TypeColumn(t, c);
            ApplyFacetDefaults(c);
            ApplyNativeType(t, c, column.NativeType);

            t.Columns.Add(c);
            t.ByKey.TryAdd(m.Key, c);
            _run.Register(c);
        }
    }

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
                    : "its own primary key";
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
        var file = t.Source;
        for (var i = 0; i < file.Redefines.Count; i++)
        {
            if (string.Equals(file.Redefines[i].Member, memberId, StringComparison.Ordinal))
                return (file.Id, "/redefines/" + i.ToString(CultureInfo.InvariantCulture));
        }

        if (from.Entry >= 0)
            return (file.Id, "/stereotypes/" + from.Entry.ToString(CultureInfo.InvariantCulture));
        return (file.Id, file.Stereotypes.Count > 0 ? "/stereotypes" : "");
    }

}
