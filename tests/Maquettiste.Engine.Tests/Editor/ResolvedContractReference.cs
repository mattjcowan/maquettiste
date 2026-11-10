using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// Renders the reference (<c>resolved-model.md</c>) from the resolved-model schema: the prose here, a section per scope and a table per
/// record from the schema's definitions and descriptions. The same bytes are <c>docs/resolved-model.md</c> and a project's copy in
/// <c>.schema/v1/</c>, so links point only inside the page.
/// </summary>
internal static class ResolvedContractReference
{
    /// <summary>The reference's text, LF line ends.</summary>
    /// <param name="schema">The whole schema.</param>
    /// <returns>The Markdown.</returns>
    public static string Render(JsonObject schema)
    {
        var version = schema["x-contract"]!.GetValue<string>();
        var definitions = schema["$defs"]!.AsObject();
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"""
            # The resolved model

            <!-- Generated from the engine's record types by the engine tests; do not edit. -->

            **Contract {version}.** This page describes what Maquettiste {version} answers when you read the resolved model: the model as
            code generation sees it, with names, types, keys, tables and processes worked out. Until version 1 the contract is the
            Maquettiste release and is not frozen: any release may change it, and this page changes with it.

            ## Where to read it

            | Where | How |
            | --- | --- |
            | Command line | `maquettiste model export --resolved` (`--scope`, `--database`, `--format json` or `ndjson`, `--out`) |
            | HTTP API | `GET /api/model/resolved` (`scope`, `database`, `cursor`, `limit`) |
            | MCP | `get_resolved_model` (`scope`, `database`, `cursor`, `limit`) |

            Every answer states the version in `contract` and names its JSON Schema in `schema` (`resolved-model.json`). To get the schema:

            - In your project: `.maquettiste/.schema/v1/resolved-model.json`, with this page beside it as `resolved-model.md`. The editor and
              `maquettiste mcp` refresh both when they start, and so does `maquettiste init`. `validate` and `generate` warn (MQ1008) while
              they belong to another release. Both files hold the version, so they are rewritten at every release, even one that
              changes nothing else: keep one Maquettiste version per repository (the local tool manifest pins it), or the copies
              change back and forth between the people and machines that run different releases.
            - Over HTTP: `GET /api/model/resolved/schema`, or `?scope=entities` for one scope's records.
            - Over MCP: `get_schema` with kind `resolved-model`, or `resolved-model:entities` for one scope's records.

            A reader you write by hand should ignore fields it does not know, since a later release may add some. A check against the
            schema is exact for one release: an extra field, or an answer from another release, fails it. So validate an answer with the
            schema of the release its `contract` names.

            ## Answers

            A page (API and MCP):


            """);
        Table(text, schema);
        text.Append("""

            Pass `next` back as `cursor` until it is null. The export command reads every page for you and writes one file:

            - `--format json` (the default): one object, `{ "contract", "schema", "items" }`, with every record of the scope in `items`.
            - `--format ndjson`: a first line `{"contract": ..., "schema": ...}`, then one record per line.

            ## Scopes

            `scope` picks the records. `all` (the default) is every scope but `tables`, `routines`, `database-types`, `sql-objects` and
            `queries`, which `databases` already holds, plus the records only `all` lists. `database` narrows entities and relations to the ones
            mapped in that database, and databases, tables and migration facts to it.

            | Scope | Record |
            | --- | --- |

            """);
        foreach (var (scope, name) in schema["x-scopes"]!.AsObject())
            text.Append(CultureInfo.InvariantCulture, $"| `{scope}` | [{name}](#{Anchor(name!.GetValue<string>())}) |\n");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var scopeKinds = schema["x-scopes"]!.AsObject().Select(s => s.Value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var allOnly = schema["x-kinds"]!.AsObject().Select(k => k.Value!.GetValue<string>()).Where(n => !scopeKinds.Contains(n)).ToList();
        foreach (var name in allOnly)
            text.Append(CultureInfo.InvariantCulture, $"| `all` only | [{name}](#{Anchor(name)}) |\n");
        text.Append("\nThe answer's own records come first, then the records they hold, each listed once where it first appears.\n");
        listed.Add("record");
        listed.Add("exportFile");
        listed.Add("exportHeader");
        foreach (var (scope, name) in schema["x-scopes"]!.AsObject())
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## Scope {scope}\n");
            Records(text, definitions, name!.GetValue<string>(), listed);
        }

        text.Append("\n## Records only all lists\n");
        foreach (var name in allOnly)
            Records(text, definitions, name, listed);
        var rest = definitions.Select(d => d.Key).Where(n => !listed.Contains(n)).ToList();
        if (rest.Count > 0)
        {
            text.Append("\n## Records of the page\n");
            foreach (var name in rest)
                Records(text, definitions, name, listed);
        }

        text.Append("""

            ## What templates see

            Templates in a pack, and the scripts beside them, read the same resolved model, but not these records. They read the resolved
            objects the records are made from:

            - Names are snake_case: a template writes `entity.is_abstract` where a record has `isAbstract`.
            - Another object is the object itself, not its id: `entity.base.name` works, where a record holds `base` as an id.
            - Some shapes differ. For example, `entity.mappings` is a map by database name where the record has a list, and templates see
              members the records leave out, such as `entity.own_attributes`, `entity.trigger_points` and `entity.operations`.
            - A calculated value is a member, `element.calculated.<name>`, not an entry of the page's `calculated` map.
            - Templates also get helpers and the unit's variables: `model`, `element`, the scope's alias (such as `entity` or `table`),
              `pack`, `mapping`, `mappings`, `hints`, `data` and `unit`.

            To list exactly what one unit's templates can read, use the MCP tool `get_template_context` (pack and unit), or the
            completions of the editor's template editor.

            """);
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>A record's heading, description and table, then the records it holds that no earlier section listed.</summary>
    private static void Records(StringBuilder text, JsonObject definitions, string name, HashSet<string> listed)
    {
        if (!listed.Add(name))
            return;
        var definition = definitions[name]!.AsObject();
        text.Append(CultureInfo.InvariantCulture, $"\n### {name}\n\n");
        if (definition["description"]?.GetValue<string>() is { Length: > 0 } description)
            text.Append(description).Append("\n\n");
        Table(text, definition);
        foreach (var reference in ResolvedContract.References(definition["properties"]).Distinct(StringComparer.Ordinal))
            Records(text, definitions, reference, listed);
    }

    private static void Table(StringBuilder text, JsonObject definition)
    {
        var required = definition["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
        text.Append("| Field | Type | Null | Meaning |\n| --- | --- | --- | --- |\n");
        foreach (var (field, node) in definition["properties"]!.AsObject())
        {
            var property = node!.AsObject();
            var nullable = Nullable(property);
            var presence = !required.Contains(field) ? "left out when not set" : nullable ? "can be null" : "never";
            var meaning = property["description"]?.GetValue<string>() ?? "";
            if (property["const"] is { } constant)
                meaning = (meaning + " Always `" + constant.GetValue<string>() + "`.").Trim();
            text.Append(CultureInfo.InvariantCulture, $"| `{field}` | {TypeText(property)} | {presence} | {Cell(meaning)} |\n");
        }
    }

    private static bool Nullable(JsonObject property) => CanBeNull(property);

    /// <summary>
    /// Whether a value's schema accepts null: a type list with <c>null</c>, an <c>anyOf</c> with a null branch, or an open value
    /// (<c>{}</c>, any JSON value). An open value that may not be null is <c>{"not": {"type": "null"}}</c>.
    /// </summary>
    /// <param name="schema">A value's schema (a property's, descriptions included).</param>
    /// <returns>Whether it accepts null.</returns>
    public static bool CanBeNull(JsonObject schema) =>
        schema["type"] is JsonArray types ? types.Any(t => t?.GetValue<string>() == "null")
        : schema["anyOf"] is JsonArray any ? any.Any(a => a?["type"]?.GetValue<string>() == "null")
        : schema["type"] is null && schema["$ref"] is null && schema["not"] is null && schema["enum"] is null && schema["const"] is null;

    /// <summary>A value's type in words: <c>string</c>, <c>list of string</c>, <c>map of any</c>, a link to a record.</summary>
    private static string TypeText(JsonObject? schema)
    {
        if (schema is null)
            return "any";
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            var name = reference["#/$defs/".Length..];
            return name == "record" ? "record (by `kind`)" : $"[{name}](#{Anchor(name)})";
        }

        if (schema["anyOf"] is JsonArray any)
            return TypeText(any.OfType<JsonObject>().FirstOrDefault(a => a["$ref"] is not null));
        var type = schema["type"] switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonArray a => a.Select(t => t!.GetValue<string>()).First(t => t != "null"),
            _ => null,
        };
        return type switch
        {
            "array" => "list of " + TypeText(schema["items"] as JsonObject),
            "object" => "map of " + (schema["additionalProperties"] is JsonObject element ? TypeText(element) : "any"),
            "string" when schema["enum"] is JsonArray values => string.Join(", ", values.OfType<JsonValue>().Select(e => "`" + e.GetValue<string>() + "`")),
            null => "any",
            _ => type,
        };
    }

    /// <summary>The anchor MkDocs and most Markdown viewers give a heading that is one word.</summary>
    private static string Anchor(string name) => name.ToLowerInvariant();

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
