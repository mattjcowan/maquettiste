using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Json;

/// <summary>
/// The keys that 0.29.0 removed with projection (docs/engineering/retire-projection-design.md section 9): on a database,
/// <c>byConvention</c> set to <c>all</c> or <c>packages</c>, and <c>packages</c>; the entity half of a mapping (<c>entity</c>,
/// <c>table</c>, <c>schema</c>, <c>ignore</c>, <c>inheritance</c>, <c>discriminatorValue</c>, an attribute's <c>storage</c>,
/// <c>prefix</c> and <c>ignore</c>); the promoted shape (a mapping's <c>shape: promoted</c> and <c>promotedName</c>, the settings'
/// <c>relationsWithAttributes: promoted</c>); overlays (a table's <c>origin</c>, <c>entity</c>, <c>attribute</c> and <c>relation</c>, and
/// a column's <c>attribute</c> in a table or stereotype). A file that still has one does not load: <see cref="SchemaRegistry.Evaluate"/>
/// reports one MQ4145 (an error no setting changes) naming each removed key and what replaces it, in place of the bare schema violations
/// the keys cause. A baseline or a snapshot read for a comparison drops them instead (<see cref="Strip"/>).
/// </summary>
internal static class RemovedProjectionKeys
{
    /// <summary>The rule of a removed key.</summary>
    public const string Rule = "MQ4145";

    private const string Release = "0.29.0";

    private static readonly string[] MappingEntityKeys = ["entity", "table", "schema", "ignore", "inheritance", "discriminatorValue"];

    private static readonly string[] AttributeKeys = ["storage", "prefix", "ignore"];

    private static readonly string[] OverlayKeys = ["origin", "entity", "attribute", "relation"];

    /// <summary>Whether a schema file can hold a removed key.</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <returns><see langword="true"/> for databases, mappings, tables, stereotypes and the settings.</returns>
    public static bool Applies(string fileName) =>
        fileName is "database.json" or "mapping.json" or "table.json" or "stereotype.json" or "maquettiste.json";

    /// <summary>The pointers of the removed keys a document has, in document order.</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="document">The document.</param>
    /// <returns>The pointers; empty when the document has none.</returns>
    public static IReadOnlyList<string> Find(string fileName, JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
            return [];
        var found = new List<string>();
        switch (fileName)
        {
            case "database.json":
                if (document.TryGetProperty("byConvention", out var by) && !(by.ValueKind == JsonValueKind.String && by.ValueEquals("none")))
                    found.Add("/byConvention");
                if (document.TryGetProperty("packages", out _))
                    found.Add("/packages");
                break;
            case "mapping.json":
                foreach (var key in MappingEntityKeys)
                {
                    if (document.TryGetProperty(key, out _))
                        found.Add("/" + key);
                }

                if (document.TryGetProperty("shape", out var shape) && shape.ValueKind == JsonValueKind.String && shape.ValueEquals("promoted"))
                    found.Add("/shape");
                if (document.TryGetProperty("promotedName", out _))
                    found.Add("/promotedName");
                ItemKeys(document, "attributes", AttributeKeys, found);
                break;
            case "table.json":
                foreach (var key in OverlayKeys)
                {
                    if (document.TryGetProperty(key, out _))
                        found.Add("/" + key);
                }

                ItemKeys(document, "columns", ["attribute"], found);
                break;
            case "stereotype.json":
                ItemKeys(document, "columns", ["attribute"], found);
                break;
            case "maquettiste.json":
                if (document.TryGetProperty("conventions", out var conventions) && Promotes(conventions))
                    found.Add("/conventions/relationsWithAttributes");
                if (document.TryGetProperty("databases", out var databases) && databases.ValueKind == JsonValueKind.Object)
                {
                    foreach (var database in databases.EnumerateObject())
                    {
                        if (Promotes(database.Value))
                            found.Add("/databases/" + Escape(database.Name) + "/relationsWithAttributes");
                    }
                }

                break;
        }

        return found;
    }

    /// <summary>The one MQ4145 of a document whose removed keys are <paramref name="pointers"/>, at the first one.</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="document">The document.</param>
    /// <param name="pointers">The pointers <see cref="Find"/> returned (not empty).</param>
    /// <param name="elementId">The element id, when known.</param>
    /// <param name="path">The repo-relative path.</param>
    /// <returns>The diagnostic.</returns>
    public static Diagnostic Finding(string fileName, JsonElement document, IReadOnlyList<string> pointers, string? elementId, string path)
    {
        var name = document.ValueKind == JsonValueKind.Object && document.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;
        var keys = string.Join(", ", pointers.Select(p => p.TrimStart('/')));
        var what = fileName switch
        {
            "database.json" => $"Database {Named(name)}still has {keys}, which {Were(pointers)} removed in {Release} with projection: a database holds only the "
                + "tables written for it, and the engine no longer lays out a table for each entity. Write the tables and bind each entity to its table "
                + "(\"Create tables…\" on 0.28.0 does it for every entity), then remove packages and write \"byConvention\": \"none\".",
            "mapping.json" when pointers.Any(p => p is "/shape" or "/promotedName") && !pointers.Any(IsEntityHalf) =>
                $"Mapping {Named(name)}still has {keys}, which {Were(pointers)} removed in {Release}: a relation is no longer stored as a generated entity. "
                + "Model the link as an entity of its own with a relation to each end, bound to the junction table.",
            "mapping.json" => $"Mapping {Named(name)}still has {keys}, which {Were(pointers)} removed in {Release} with projection: a mapping says only how a "
                + "relation is stored (its foreign key or junction table). An entity's binding names the table it reads and writes; a hierarchy uses the "
                + "binding's discriminatorColumn (one table) or baseForeignKey (a table per type); a list or a value object in a table of its own uses "
                + "childTables, else fields (embedded columns or a JSON column). \"Create tables…\" on 0.28.0 converts an entity mapping and deletes it."
                + (pointers.Any(p => p is "/shape" or "/promotedName") ? " A relation is no longer stored as a generated entity: model the link as an entity with a relation to each end." : ""),
            "table.json" when pointers is ["/origin"] && OriginOf(document) is "designed" or "imported" =>
                $"Table {Named(name)}still has origin \"{OriginOf(document)}\", which was removed in {Release}: every table is a designed table now, so the "
                + "key says nothing. Delete the key.",
            "table.json" => $"Table {Named(name)}still has {keys}, which {Were(pointers)} removed in {Release} with projection: every table is a designed "
                + "table with its full definition, and an overlay no longer adjusts a table the engine lays out. Write the table's columns, keys and "
                + "constraints in the file and remove the keys (\"Create tables…\" on 0.28.0 folds an overlay into its designed table).",
            "stereotype.json" => $"Stereotype {Named(name)}still has {keys}, which {Were(pointers)} removed in {Release} with projection: a column no longer "
                + "overrides a column the engine lays out. Remove attribute.",
            _ => $"The settings still have {keys}: {Release} removed the promoted shape, so a relation that carries attributes is stored in a junction "
                + "table. Remove the setting, or model the link as an entity with a relation to each end.",
        };
        var pointer = pointers[0];
        return RuleCatalog.Create(Rule, pointer + " " + what, elementId, path, pointer);
    }

    /// <summary>
    /// Whether a schema failure at <paramref name="pointer"/> is one the removed keys cause, which the MQ4145 replaces: a failure at or under
    /// a removed key, or at the document root (a mapping without <c>relation</c>, an overlay without <c>name</c>), or at an item that holds
    /// a removed key.
    /// </summary>
    /// <param name="pointer">The failure's pointer.</param>
    /// <param name="removed">The pointers <see cref="Find"/> returned.</param>
    /// <returns>Whether the MQ4145 covers it.</returns>
    public static bool Covers(string pointer, IReadOnlyList<string> removed)
    {
        if (pointer.Length == 0)
            return true;
        foreach (var r in removed)
        {
            if (string.Equals(pointer, r, StringComparison.Ordinal) || pointer.StartsWith(r + "/", StringComparison.Ordinal))
                return true;
            var slash = r.LastIndexOf('/');
            if (slash > 0 && string.Equals(pointer, r[..slash], StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Drops the removed keys from a document read for a comparison (a baseline or a snapshot taken before 0.29.0), so the file still loads:
    /// the database's convention keys, a mapping's entity half (a mapping that names an entity is dropped whole), the promoted shape and an
    /// attribute's options, and a table's overlay keys (an overlay file, with <c>origin</c> set to <c>synthesized</c>, is dropped whole). A
    /// database that had no <c>byConvention</c> is read as <c>none</c>.
    /// </summary>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="node">The document, changed in place.</param>
    /// <returns>Whether to keep the file, and what the dropped keys did to the layout of tables (<see cref="StripResult"/>).</returns>
    public static StripResult Strip(string fileName, JsonObject node)
    {
        var layout = StripLayout.None;
        switch (fileName)
        {
            case "database.json":
                if (node["packages"] is not null || (node["byConvention"] is JsonValue by && !(by.TryGetValue<string>(out var b) && b == "none")))
                    layout = StripLayout.LaidOut;
                else if (node["byConvention"] is null)
                    layout = StripLayout.NoConvention;
                node.Remove("packages");
                node["byConvention"] = "none";
                break;
            case "mapping.json":
                if (node["entity"] is not null && node["relation"] is null)
                    return new StripResult(false, StripLayout.LaidOut);
                if (MappingEntityKeys.Any(k => node[k] is not null) || node["promotedName"] is not null
                    || (node["shape"] is JsonValue promoted && promoted.TryGetValue<string>(out var ps) && ps == "promoted")
                    || HasItemKeys(node, "attributes", AttributeKeys))
                    layout = StripLayout.LaidOut;
                foreach (var key in MappingEntityKeys)
                    node.Remove(key);
                node.Remove("promotedName");
                if (node["shape"] is JsonValue shape && shape.TryGetValue<string>(out var s) && s == "promoted")
                    node.Remove("shape");
                StripItems(node, "attributes", AttributeKeys);
                break;
            case "table.json":
                if (node["origin"] is JsonValue origin && origin.TryGetValue<string>(out var o) && o == "synthesized")
                    return new StripResult(false, StripLayout.LaidOut);
                // An overlay adjusts a table the engine laid out; a designed or imported table's origin is only a key.
                if (node["entity"] is not null || node["attribute"] is not null || node["relation"] is not null || HasItemKeys(node, "columns", ["attribute"]))
                    layout = StripLayout.LaidOut;
                foreach (var key in OverlayKeys)
                    node.Remove(key);
                StripItems(node, "columns", ["attribute"]);
                break;
            case "stereotype.json":
                StripItems(node, "columns", ["attribute"]);
                break;
        }

        return new StripResult(true, layout);
    }

    private static string? OriginOf(JsonElement document) =>
        document.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.String ? origin.GetString() : null;

    private static bool IsEntityHalf(string pointer) => pointer is not ("/shape" or "/promotedName");

    private static string Named(string? name) => name is null ? "" : "'" + name + "' ";

    private static string Were(IReadOnlyList<string> pointers) => pointers.Count == 1 ? "was" : "were";

    private static bool Promotes(JsonElement conventions) =>
        conventions.ValueKind == JsonValueKind.Object && conventions.TryGetProperty("relationsWithAttributes", out var value)
        && value.ValueKind == JsonValueKind.String && value.ValueEquals("promoted");

    private static void ItemKeys(JsonElement document, string list, string[] keys, List<string> found)
    {
        if (!document.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array)
            return;
        var i = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in keys)
                {
                    if (item.TryGetProperty(key, out _))
                        found.Add("/" + list + "/" + i.ToString(CultureInfo.InvariantCulture) + "/" + key);
                }
            }

            i++;
        }
    }

    private static bool HasItemKeys(JsonObject node, string list, string[] keys) =>
        node[list] is JsonArray items && items.Any(item => item is JsonObject obj && keys.Any(k => obj[k] is not null));

    private static void StripItems(JsonObject node, string list, string[] keys)
    {
        if (node[list] is not JsonArray items)
            return;
        foreach (var item in items)
        {
            if (item is JsonObject obj)
            {
                foreach (var key in keys)
                    obj.Remove(key);
            }
        }
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>What <see cref="RemovedProjectionKeys.Strip"/> did to a document.</summary>
/// <param name="Keep">Whether to keep the file; <see langword="false"/> leaves it out (an entity mapping, an overlay file).</param>
/// <param name="Layout">What the dropped keys did to the layout of tables.</param>
internal readonly record struct StripResult(bool Keep, StripLayout Layout);

/// <summary>What a stripped document's removed keys did to the layout of tables before 0.29.0.</summary>
internal enum StripLayout
{
    /// <summary>Nothing: the keys changed no table (or there were none).</summary>
    None,

    /// <summary>The keys laid out or adjusted tables: an entity mapping, an overlay, the promoted shape, <c>byConvention</c> other than <c>none</c>.</summary>
    LaidOut,

    /// <summary>A database with no <c>byConvention</c>: it laid out a table for each entity not bound to it.</summary>
    NoConvention,
}
