using System.Text.Json.Nodes;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// What a query names by key when tables or columns move: its sources' table keys and its column texts (<c>alias.&lt;key&gt;</c>, or a
/// bare key), its named queries' included, and a SQL text query's <c>reads</c> and <c>writes</c> entries. <c>materialize-tables</c> points a projected table's sources and columns at the designed table, and
/// <c>fold-into-stereotype</c> points an own column at the inherited column's keyed id (next-phase-2-design.md section 3.1). The
/// replacements come from the resolved query, which knows which table and column each text names, and apply to the query's document.
/// </summary>
internal static class QueryColumnRewrite
{
    private static readonly string[] QueryProperties = ["source", "column"];

    /// <summary>The lists of a query written as SQL text whose entries name tables.</summary>
    private static readonly string[] QueryLists = ["reads", "writes"];

    /// <summary>
    /// The replacements of one query's texts: each source whose table <paramref name="source"/> gives a new id, and each column text whose
    /// column <paramref name="column"/> gives a new id (an aliased text becomes <c>alias.&lt;new id&gt;</c>).
    /// </summary>
    /// <param name="query">The resolved query.</param>
    /// <param name="source">A source table's new id, or <see langword="null"/> to keep it.</param>
    /// <param name="column">A column's new id (by its table and column), or <see langword="null"/> to keep it.</param>
    /// <param name="byKeyOnly">Rewrite only the texts that name the column by its key, aliased or bare, and leave the texts that name it
    /// by physical name, which still find it; otherwise every aliased text that names the column.</param>
    /// <returns>The old text and the new one, by old text.</returns>
    public static Dictionary<string, string> Replacements(RQuery query, Func<RTable, string?> source, Func<RTable, RColumn, string?> column,
        bool byKeyOnly = false)
    {
        var replace = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in Sources(query))
        {
            if (s.Table is { } t && source(t) is { } id && s.Source != id)
                replace[s.Source] = id;
        }

        foreach (var (reference, target) in query.Declared)
        {
            if (target is RTable t && source(t) is { } id && reference != id)
                replace[reference] = id;
        }

        foreach (var e in Expressions(query))
        {
            if (e is not { Node: "column", Column: { } text, TableColumn: { } tableColumn, Source.Table: { } t } || column(t, tableColumn) is not { } newId)
                continue;
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            var alias = dot > 0 && e.Alias is { } a && text[..dot] == a ? a : null;
            var rewritten = !byKeyOnly ? (alias is null ? text : alias + "." + newId)
                : alias is not null && text[(dot + 1)..] == tableColumn.Key ? alias + "." + newId
                : alias is null && text == tableColumn.Key ? newId
                : text;
            if (rewritten != text)
                replace[text] = rewritten;
        }

        return replace;
    }

    /// <summary>Applies replacements to the source and column texts and the <c>reads</c> and <c>writes</c> entries of a query's document;
    /// true when one changed.</summary>
    /// <param name="query">The query's document.</param>
    /// <param name="replace">The replacements (<see cref="Replacements"/>).</param>
    /// <returns>Whether a text changed.</returns>
    public static bool Apply(JsonNode query, IReadOnlyDictionary<string, string> replace)
    {
        if (replace.Count == 0)
            return false;
        var changed = ReplaceStrings(query, replace, QueryProperties);
        foreach (var list in QueryLists)
        {
            if (query[list] is not JsonArray entries)
                continue;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] is JsonValue v && v.TryGetValue<string>(out var text) && replace.TryGetValue(text, out var replacement))
                {
                    entries[i] = replacement;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>Replaces string values of the named properties anywhere under a node; true when one changed.</summary>
    /// <param name="node">The node.</param>
    /// <param name="map">The replacements, by old value.</param>
    /// <param name="properties">The property names whose string values are replaced.</param>
    /// <returns>Whether a value changed.</returns>
    public static bool ReplaceStrings(JsonNode node, IReadOnlyDictionary<string, string> map, string[] properties)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[name];
                    if (value is JsonValue v && properties.Contains(name) && v.TryGetValue<string>(out var text) && map.TryGetValue(text, out var replacement))
                    {
                        obj[name] = replacement;
                        changed = true;
                    }
                    else if (value is not null)
                    {
                        changed |= ReplaceStrings(value, map, properties);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array.ToList())
                {
                    if (item is not null)
                        changed |= ReplaceStrings(item, map, properties);
                }

                break;
        }

        return changed;
    }

    private static IEnumerable<RQuerySource> Sources(RQuery query)
    {
        foreach (var part in NamedParts(query))
        {
            foreach (var source in Sources(part))
                yield return source;
        }

        if (query.From is { } from)
            yield return from;
        foreach (var join in query.Joins)
            yield return join;
        foreach (var collection in query.Collections)
        {
            foreach (var source in Sources(collection.Query))
                yield return source;
        }

        foreach (var nested in Predicates(query).Where(p => p.Exists is not null).Select(p => p.Exists!))
        {
            foreach (var source in Sources(nested))
                yield return source;
        }
    }

    private static IEnumerable<RQueryPredicate> Predicates(RQuery query)
    {
        var roots = new List<RQueryPredicate?> { query.Where, query.Having };
        roots.AddRange(query.Joins.Select(j => j.On));
        foreach (var root in roots.OfType<RQueryPredicate>())
        {
            foreach (var p in Walk(root))
                yield return p;
        }

        static IEnumerable<RQueryPredicate> Walk(RQueryPredicate p)
        {
            yield return p;
            foreach (var child in p.And.Concat(p.Or).Concat(p.Not is { } not ? [not] : []))
            {
                foreach (var x in Walk(child))
                    yield return x;
            }
        }
    }

    /// <summary>The anchor and the repeating part of each named query of a query's <c>with</c>.</summary>
    private static IEnumerable<RQuery> NamedParts(RQuery query) =>
        query.With.SelectMany(n => n.Recursive is { } recursive ? new[] { n.Query, recursive } : [n.Query]);

    private static IEnumerable<RQueryExpression> Expressions(RQuery query)
    {
        foreach (var part in NamedParts(query))
        {
            foreach (var e in Expressions(part))
                yield return e;
        }

        var roots = new List<RQueryExpression>();
        roots.AddRange(query.Select.Select(f => f.Expression).OfType<RQueryExpression>());
        roots.AddRange(query.GroupBy);
        roots.AddRange(query.OrderBy.Select(o => o.Expression));
        foreach (var p in Predicates(query))
        {
            if (p.Left is { } left)
                roots.Add(left);
            if (p.Right is { } right)
                roots.Add(right);
            roots.AddRange(p.Values);
        }

        foreach (var root in roots)
        {
            foreach (var e in Walk(root))
                yield return e;
        }

        foreach (var nested in query.Collections.Select(c => c.Query).Concat(Predicates(query).Where(p => p.Exists is not null).Select(p => p.Exists!)))
        {
            foreach (var e in Expressions(nested))
                yield return e;
        }

        static IEnumerable<RQueryExpression> Walk(RQueryExpression e)
        {
            yield return e;
            var children = e.Args.Concat(e.Case.SelectMany(w => new[] { w.Then })).Concat(e.Else is { } x ? [x] : []).Concat(e.Cast is { } c ? [c] : []);
            foreach (var child in children)
            {
                foreach (var y in Walk(child))
                    yield return y;
            }

            foreach (var when in e.Case)
            {
                foreach (var p in WalkPredicate(when.When))
                {
                    foreach (var operand in (p.Left is { } l ? new[] { l } : []).Concat(p.Right is { } r ? [r] : []).Concat(p.Values))
                    {
                        foreach (var y in Walk(operand))
                            yield return y;
                    }
                }
            }
        }

        static IEnumerable<RQueryPredicate> WalkPredicate(RQueryPredicate p)
        {
            yield return p;
            foreach (var child in p.And.Concat(p.Or).Concat(p.Not is { } not ? [not] : []))
            {
                foreach (var x in WalkPredicate(child))
                    yield return x;
            }
        }
    }
}
