using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Named queries of a built query (sql-queries-design.md decisions 10 to 13): each entry of <c>with</c> resolved, in order, as a select
/// over the database's tables and views and the named queries before it; a source reads one by its name, from the top query, its
/// nested queries and the named queries after it. A recursive one's repeating part reads its own name once and has its anchor's fields.
/// Reported here: MQ3001 (two names equal ignoring case), MQ4099 (a misplaced named query) and MQ4099 (a repeating part that does not
/// fit), with the JSON pointer of the offending node.
/// </summary>
internal sealed partial class DatabaseRun
{
    private sealed partial class QueryBuilder
    {
        /// <summary>The named queries resolved so far, by position.</summary>
        private readonly List<RNamedQuery> _with = [];

        /// <summary>How many named queries the sources being resolved may read (those before the one being resolved; all, after).</summary>
        private int _withVisible;

        /// <summary>The position of the named query being resolved, or -1.</summary>
        private int _withCurrent = -1;

        /// <summary>Whether a named query's parts are being resolved (their select lists are no result rows: MQ4040 does not apply).</summary>
        private bool _inWith;

        /// <summary>The named query whose repeating part is being resolved, which that part may read; else <see langword="null"/>.</summary>
        private RNamedQuery? _self;

        /// <summary>The repeating part being resolved.</summary>
        private RQuery? _selfPart;

        /// <summary>Where the repeating part being resolved reads its own name: the source, whether in a nested query, and its pointer.</summary>
        private readonly List<(RQuerySource Source, bool Nested, string Pointer)> _selfReads = [];

        /// <summary>Resolves the file's <c>with</c> into <see cref="RQuery.With"/>, before the query's own sources, which may read them.</summary>
        private void ResolveWith()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < file.With.Count; i++)
            {
                var entry = file.With[i];
                var at = "/with/" + i.ToString(CultureInfo.InvariantCulture);
                if (!names.TryAdd(entry.Name, entry.Name))
                    Report("MQ3001", $"Named query name '{entry.Name}' is already used in query '{file.Name}'{(names[entry.Name] == entry.Name ? "" : " (ignoring case)")}.", at + "/name");

                (_withCurrent, _withVisible, _inWith) = (i, i, true);
                var anchor = Part(entry.Name, entry);
                ResolveBody(anchor, new Scope(anchor, null), entry.From, entry.Joins, entry.Select, entry.Where, entry.GroupBy, entry.Having, [], at, null);
                anchor.Distinct = entry.Distinct;
                var named = new RNamedQuery
                {
                    Name = entry.Name,
                    Query = anchor,
                    Index = i,
                    Columns = [.. anchor.Select.Select(f => new RQueryField
                    {
                        Name = f.Name, Expression = f.Expression, Type = f.Type, NativeType = f.NativeType, CodeType = f.CodeType, Nullable = f.Nullable,
                    })],
                };
                _with.Add(named);
                if (entry.Recursive is { } recursive)
                    ResolveRecursive(named, recursive, at + "/recursive");
            }

            (_withCurrent, _withVisible, _inWith) = (-1, _with.Count, false);
            query.With = [.. _with];
        }

        /// <summary>A part of a named query: a nested query of the top query, with its parameters.</summary>
        private RQuery Part(string name, object definition) => new()
        {
            Id = query.Id, Name = name, Database = query.Database, Parent = query, Settings = query.Settings, Parameters = query.Parameters, Definition = definition,
        };

        /// <summary>The repeating part of a recursive named query, its rules (MQ4099), and the columns' types and nullability merged.</summary>
        private void ResolveRecursive(RNamedQuery named, QueryRecursivePart recursive, string at)
        {
            var part = Part(named.Name, recursive);
            (_self, _selfPart) = (named, part);
            _selfReads.Clear();
            ResolveBody(part, new Scope(part, null), recursive.From, recursive.Joins, recursive.Select, recursive.Where, [], null, [], at, null);
            (_self, _selfPart) = (null, null);
            named.Recursive = part;
            named.Union = recursive.Union == QueryUnion.Distinct ? "distinct" : "all";

            var what = $"The repeating part of named query '{named.Name}' of query '{file.Name}'";
            var dialect = run._dialect switch
            {
                "sqlserver" => "SQL Server",
                "oracle" => "Oracle",
                _ => null,
            };
            if (named.Union == "distinct" && dialect is not null)
                Report("MQ4099", $"{what} adds its rows with union distinct, which {dialect} (database '{run._db.Name}') does not take in a recursive query: use union all.", at + "/union");

            var anchor = named.Query.Select;
            if (part.Select.Count != anchor.Count || part.Select.Zip(anchor).Any(p => !string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal)))
            {
                Report("MQ4099", $"{what} selects ({string.Join(", ", part.Select.Select(f => f.Name))}) where its anchor selects ({string.Join(", ", anchor.Select(f => f.Name))}): give it the same fields, with the same names in the same order.", at + "/select");
            }
            else
            {
                for (var i = 0; i < anchor.Count; i++)
                {
                    if (anchor[i].Type is { } expected && part.Select[i].Type is { } actual && !string.Equals(expected, actual, StringComparison.Ordinal))
                        Report("MQ4099", $"Field '{anchor[i].Name}' of the {what[4..]} is of type '{actual}', and the anchor's of type '{expected}': give both the same type (a cast).", at + "/select/" + i.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (_selfReads.Count == 0)
                Report("MQ4099", $"{what} does not read '{named.Name}': a repeating part reads its own name exactly once, in its from or a join.", at + "/from");
            else if (_selfReads.Count > 1)
                Report("MQ4099", $"{what} reads '{named.Name}' {_selfReads.Count.ToString(CultureInfo.InvariantCulture)} times: a repeating part reads its own name exactly once.", _selfReads[1].Pointer);
            else if (_selfReads[0].Nested)
                Report("MQ4099", $"{what} reads '{named.Name}' in a nested query: a repeating part reads its own name in its own from or a join.", _selfReads[0].Pointer);
            else if (_selfReads[0].Source.Optional)
                Report("MQ4099", $"{what} reads '{named.Name}' on the null-padded side of an outer join (the right side of a left join, the left side of a right join, either side of a full join), which the databases refuse: read it on the side that keeps its rows, or use an inner join.", _selfReads[0].Pointer);

            if (QuerySql.IsGrouped(part))
                Report("MQ4099", $"{what} calls an aggregate function, which a repeating part may not.", at + "/select");

            if (run._dialect == "sqlserver")
            {
                for (var i = 0; i < part.Joins.Count; i++)
                {
                    if (part.Joins[i].JoinKind is "left" or "right" or "full")
                        Report("MQ4099", $"{what} has an outer join, which SQL Server (database '{run._db.Name}') does not take in a repeating part: use an inner join.", at + "/joins/" + i.ToString(CultureInfo.InvariantCulture) + "/kind");
                }

                if (HasNested(part))
                    Report("MQ4099", $"{what} has a nested query (exists), which SQL Server (database '{run._db.Name}') does not take in a repeating part.", at);
            }

            // A column's type is the anchor's (the repeating part's when the anchor's is unknown); it may be null when either part's may.
            for (var i = 0; i < Math.Min(anchor.Count, part.Select.Count); i++)
            {
                var (column, field) = (named.Columns[i], part.Select[i]);
                column.Nullable = column.Nullable || field.Nullable;
                if (column.Type is null && field.Type is not null)
                    (column.Type, column.NativeType, column.CodeType) = (field.Type, field.NativeType, field.CodeType);
            }
        }

        /// <summary>
        /// Resolves a source that names a named query of the file's <c>with</c>, or reports why it may not read it (MQ4099); false when
        /// the reference names none (MQ4021 then).
        /// </summary>
        private bool NamedSource(Scope scope, RQuerySource source, string reference, string pointer)
        {
            var index = -1;
            for (var i = 0; i < file.With.Count && index < 0; i++)
            {
                if (string.Equals(file.With[i].Name, reference, StringComparison.Ordinal))
                    index = i;
            }

            if (index < 0)
                return false;
            source.Name = reference;
            if (index < _withVisible)
            {
                source.Named = _with[index];
                return true;
            }

            if (index == _withCurrent && _self is not null)
            {
                source.Named = _self;
                _selfReads.Add((source, !ReferenceEquals(scope.Query, _selfPart), pointer + "/source"));
                return true;
            }

            var reader = file.With[_withCurrent].Name;
            Report("MQ4099", index == _withCurrent
                ? $"Named query '{reader}' of query '{file.Name}' reads itself outside recursive: only its repeating part may read its own name."
                : $"Named query '{reader}' of query '{file.Name}' reads named query '{reference}', which is listed after it: move '{reference}' before it.",
                pointer + "/source");
            return true;
        }

        /// <summary>MQ4099: a named query with the name of a table or view the query reads, which the SQL would then not read.</summary>
        private void CheckWithNames()
        {
            foreach (var named in _with)
            {
                var clash = _uses.Select(u => u switch
                {
                    RTable t => ("table", t.Name),
                    RView v => ("view", v.Name),
                    _ => default,
                }).FirstOrDefault(u => u.Name is not null && string.Equals(u.Name, named.Name, StringComparison.OrdinalIgnoreCase));
                if (clash.Name is not null)
                    Report("MQ4099", $"Named query '{named.Name}' of query '{file.Name}' has the name of {clash.Item1} '{clash.Name}', which the query reads, so the SQL would read one for the other: rename the named query.",
                        "/with/" + named.Index.ToString(CultureInfo.InvariantCulture) + "/name");
            }
        }

        /// <summary>Whether a query has a nested query (an exists) in its joins' conditions, select list or where.</summary>
        private static bool HasNested(RQuery part) =>
            part.Joins.Any(j => j.On is { } on && HasNested(on)) || part.Select.Any(f => f.Expression is { } e && HasNested(e)) || (part.Where is { } where && HasNested(where));

        private static bool HasNested(RQueryPredicate p) =>
            p.Exists is not null || p.And.Any(HasNested) || p.Or.Any(HasNested) || (p.Not is { } not && HasNested(not))
            || (p.Left is { } left && HasNested(left)) || (p.Right is { } right && HasNested(right)) || p.Values.Any(HasNested);

        private static bool HasNested(RQueryExpression e) =>
            e.Args.Any(HasNested) || (e.Cast is { } cast && HasNested(cast)) || e.Case.Any(w => HasNested(w.When) || HasNested(w.Then))
            || (e.Else is { } otherwise && HasNested(otherwise));
    }
}
