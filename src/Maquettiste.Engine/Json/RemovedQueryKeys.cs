using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Json;

/// <summary>
/// The keys of a query file that 0.28.0 removed: <c>entity</c> (the entity each row had), <c>collections</c> (lists filled per row)
/// and a select field's <c>attribute</c> (what the field filled on that entity). The database knows no entities: a query declares its
/// own columns, an entity reads it through its binding's <c>source</c>, and a record's parts come through composition. A file that
/// still has one of them does not load: <see cref="SchemaRegistry.Evaluate"/> reports one MQ4127 (an error no setting changes) naming
/// the replacement, in place of the bare schema violations the keys cause.
/// </summary>
internal static class RemovedQueryKeys
{
    /// <summary>The rule of a removed key.</summary>
    public const string Rule = "MQ4127";

    /// <summary>The schema file of queries.</summary>
    public const string SchemaFile = "query.json";

    /// <summary>
    /// The pointers of the removed keys a query document has: <c>/entity</c>, <c>/collections</c>, then each select field's
    /// <c>attribute</c> (the top query's, a named query's, a repeating part's or a nested query's) in document order.
    /// </summary>
    /// <param name="document">The query document.</param>
    /// <returns>The pointers; empty when the document has none.</returns>
    public static IReadOnlyList<string> Find(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
            return [];
        var found = new List<string>();
        if (document.TryGetProperty("entity", out _))
            found.Add("/entity");
        if (document.TryGetProperty("collections", out _))
            found.Add("/collections");
        foreach (var property in document.EnumerateObject())
        {
            if (!property.NameEquals("entity") && !property.NameEquals("collections") && !property.NameEquals("properties") && !property.NameEquals("generation")
                && !property.NameEquals("source"))
                Walk(property.Value, "/" + Escape(property.Name), property.NameEquals("select"), found);
        }

        return found;
    }

    /// <summary>
    /// The one MQ4127 of a query document whose removed keys are <paramref name="pointers"/>, at the first one, naming what the file has
    /// and what replaces it.
    /// </summary>
    /// <param name="document">The query document.</param>
    /// <param name="pointers">The pointers <see cref="Find"/> returned (not empty).</param>
    /// <param name="elementId">The element id, when known.</param>
    /// <param name="path">The repo-relative path.</param>
    /// <returns>The diagnostic.</returns>
    public static Diagnostic Finding(JsonElement document, IReadOnlyList<string> pointers, string? elementId, string path)
    {
        var name = document.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var what = new List<string>();
        if (pointers.Contains("/entity"))
            what.Add("an entity as the shape of its rows (entity)");
        if (pointers.Contains("/collections"))
            what.Add("collections");
        var fields = pointers.Count(p => p.EndsWith("/attribute", StringComparison.Ordinal));
        if (fields > 0)
            what.Add(fields == 1 ? "a select field that fills an attribute (attribute)" : fields.ToString(CultureInfo.InvariantCulture) + " select fields that fill attributes (attribute)");
        var listed = what.Count == 1 ? what[0] : string.Join(", ", what.Take(what.Count - 1)) + " and " + what[^1];
        var pointer = pointers[0];
        return RuleCatalog.Create(Rule,
            $"{pointer} Query {(name is null ? "" : "'" + name + "' ")}still has {listed}, which were removed in 0.28.0: a query names no entity. "
            + "Give each select field a name of its own and bind the entity to the query (its binding's source names the query, each binding field a column); "
            + "read a record with its parts through composition (entity.parts), each part with its own binding.",
            elementId, path, pointer);
    }

    /// <summary>
    /// Whether a schema failure at <paramref name="pointer"/> is one the removed keys cause, which the MQ4127 replaces: a failure at or
    /// under a removed key, or at a select field that has <c>attribute</c> (it lacks the <c>name</c> that is now required).
    /// </summary>
    /// <param name="pointer">The failure's pointer.</param>
    /// <param name="removed">The pointers <see cref="Find"/> returned.</param>
    /// <returns>Whether the MQ4127 covers it.</returns>
    public static bool Covers(string pointer, IReadOnlyList<string> removed)
    {
        foreach (var r in removed)
        {
            if (string.Equals(pointer, r, StringComparison.Ordinal) || pointer.StartsWith(r + "/", StringComparison.Ordinal))
                return true;
            if (r.EndsWith("/attribute", StringComparison.Ordinal) && string.Equals(pointer, r[..^"/attribute".Length], StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void Walk(JsonElement value, string pointer, bool inSelect, List<string> found)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var at = pointer + "/" + i.ToString(CultureInfo.InvariantCulture);
                    if (inSelect && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("attribute", out _))
                        found.Add(at + "/attribute");
                    Walk(item, at, false, found);
                    i++;
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    // An sql text per dialect holds no query parts.
                    if (property.NameEquals("sql"))
                        continue;
                    Walk(property.Value, pointer + "/" + Escape(property.Name), property.NameEquals("select"), found);
                }

                break;
        }
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
