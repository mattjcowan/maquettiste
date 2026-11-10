using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Maquettiste.Engine.Json;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// Generates <c>schemas/v1/resolved-model.json</c> from the record types the resolved-model answers serialize and from their XML docs
/// (docs/engineering/resolved-contract-design.md section 2). The shapes come from the serializer's own contract
/// (<see cref="JsonTypeInfo"/> over <see cref="JsonSerializerDefaults.Web"/>), the descriptions from the engine's XML documentation
/// file, so neither can drift from the code. The engine tests run it (<c>ResolvedContractTests</c>); the engine embeds its checked-in
/// output. Open members (<c>object</c>, <c>JsonElement</c>) are <c>{}</c> when they may be null and <c>{"not": {"type": "null"}}</c> when
/// they may not.
/// </summary>
internal sealed partial class ResolvedContractWriter
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private readonly Dictionary<string, XElement> _docs;
    private readonly SortedDictionary<string, JsonObject> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _names = [];
    private readonly Dictionary<Type, string> _kinds;
    private readonly NullabilityInfoContext _nullability = new();
    private readonly string _version;

    private ResolvedContractWriter(XDocument docs, string version)
    {
        _docs = docs.Descendants("member").Where(m => m.Attribute("name") is not null)
            .GroupBy(m => m.Attribute("name")!.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _kinds = ResolvedContract.RecordTypes.ToDictionary(r => r.Value, r => r.Key);
        _version = version;
    }

    /// <summary>The schema's text, LF line ends, two-space indents, a final newline.</summary>
    /// <param name="xmlDocPath">The engine's XML documentation file (<c>Maquettiste.Engine.xml</c>).</param>
    /// <param name="version">The contract version to state.</param>
    /// <returns>The text.</returns>
    public static string Write(string xmlDocPath, string version)
    {
        var writer = new ResolvedContractWriter(XDocument.Load(xmlDocPath), version);
        var text = writer.Build().ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private JsonObject Build()
    {
        foreach (var (_, type) in ResolvedContract.RecordTypes)
            Reference(type);
        var page = Definition(typeof(ResolvedPage), out _);
        _definitions.Remove(NameOf(typeof(ResolvedPage)));
        var record = new JsonObject
        {
            ["description"] = "One record of a page: the record of its kind (`kind` tells them apart).",
            ["oneOf"] = new JsonArray([.. ResolvedContract.RecordTypes.Select(r => (JsonNode)new JsonObject { ["$ref"] = "#/$defs/" + NameOf(r.Value) })]),
        };
        _definitions["record"] = record;
        _definitions["exportFile"] = Envelope(page, "What `maquettiste model export --resolved --format json` writes: the version, the schema's file name and every record of the scope.", true);
        _definitions["exportHeader"] = Envelope(page, "The first line `maquettiste model export --resolved --format ndjson` writes, before one record per line.", false);

        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Resolved model",
            ["description"] = "One page of the resolved model, the model as code generation sees it, as `GET /api/model/resolved` and the MCP tool " +
                "`get_resolved_model` answer it. Records are flat: other objects appear by id. Contract " + _version + ": until version 1 the " +
                "contract is the Maquettiste release and may change in any release. The reference is resolved-model.md, next to this file.",
            ["x-contract"] = _version,
            ["x-scopes"] = new JsonObject([.. ResolvedRecords.Scopes.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)NameOf(TypeOf(s.Value))))]),
            ["x-kinds"] = new JsonObject([.. ResolvedContract.RecordTypes.Select(r => KeyValuePair.Create(r.Key, (JsonNode?)NameOf(r.Value)))]),
        };
        foreach (var (key, value) in page)
        {
            if (key != "description")
                root[key] = value?.DeepClone();
        }

        root["$defs"] = new JsonObject([.. _definitions.Select(d => KeyValuePair.Create(d.Key, (JsonNode?)d.Value))]);
        return root;
    }

    private static Type TypeOf(string kind) => ResolvedContract.RecordTypes.First(r => r.Key == kind).Value;

    /// <summary>The export file or header: the page's <c>contract</c> and <c>schema</c>, and for the file its items.</summary>
    private static JsonObject Envelope(JsonObject page, string description, bool items)
    {
        var keys = items ? new[] { "contract", "schema", "items" } : ["contract", "schema"];
        var properties = new JsonObject();
        foreach (var key in keys)
            properties[key] = page["properties"]![key]!.DeepClone();
        if (items)
            properties["items"]!["description"] = "Every record of the scope, by kind, name and id (ordinal).";
        return new JsonObject
        {
            ["description"] = description,
            ["type"] = "object",
            ["x-order"] = new JsonArray([.. keys.Select(k => (JsonNode)k)]),
            ["properties"] = properties,
            ["required"] = new JsonArray([.. keys.Select(k => (JsonNode)k)]),
            ["additionalProperties"] = false,
        };
    }

    /// <summary>A <c>$ref</c> to the type's definition, building it on first use.</summary>
    private JsonObject Reference(Type type)
    {
        var name = NameOf(type);
        if (!_definitions.ContainsKey(name))
        {
            _definitions[name] = new JsonObject(); // a placeholder, so a type that reaches itself ends
            _definitions[name] = Definition(type, out _);
        }

        return new JsonObject { ["$ref"] = "#/$defs/" + name };
    }

    private string NameOf(Type type)
    {
        if (!_names.TryGetValue(type, out var name))
        {
            name = JsonNamingPolicy.CamelCase.ConvertName(type.Name);
            if (_names.ContainsValue(name))
                throw new InvalidOperationException($"Two contract types are named {type.Name}.");
            _names[type] = name;
        }

        return name;
    }

    private JsonObject Definition(Type type, out string name)
    {
        name = NameOf(type);
        var info = Web.GetTypeInfo(type);
        if (info.Kind != JsonTypeInfoKind.Object)
            throw new InvalidOperationException($"{type.FullName} is not serialized as an object.");
        var properties = new JsonObject();
        var order = new JsonArray();
        var required = new JsonArray();
        foreach (var property in info.Properties.OrderBy(p => p.Order))
        {
            if (property.Get is null)
                continue;
            var member = property.AttributeProvider as PropertyInfo
                ?? throw new InvalidOperationException($"{type.FullName}.{property.Name} is not a property.");
            var ignore = member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition;
            var optional = ignore is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault;
            var nullability = _nullability.Create(member);
            // A member left out when null is never written as null.
            var schema = Value(property.PropertyType, !optional && nullability.ReadState == NullabilityState.Nullable, nullability, optional);
            if (property.Name == "kind" && _kinds.TryGetValue(type, out var kind))
                schema["const"] = kind;
            else if (type == typeof(ResolvedPage) && property.Name == "contract")
                schema["const"] = _version;
            else if (type == typeof(ResolvedPage) && property.Name == "schema")
                schema["const"] = ResolvedContract.SchemaFile;
            else if (type == typeof(ResolvedPage) && property.Name == "items")
                schema["items"] = new JsonObject { ["$ref"] = "#/$defs/record" };
            var text = Describe("P:" + Doc(member.DeclaringType!) + "." + member.Name);
            if ((optional || ResolvedContractReference.CanBeNull(schema)) && !NullWords().IsMatch(text))
                text += Conditional().IsMatch(text) ? (optional ? " Left out otherwise." : " Null otherwise.") : optional ? " Left out when not set." : " Null when not set.";
            var described = new JsonObject { ["description"] = text };
            foreach (var (key, value) in schema)
                described[key] = value?.DeepClone();
            properties[property.Name] = described;
            order.Add(property.Name);
            if (!optional)
                required.Add(property.Name);
        }

        return new JsonObject
        {
            ["description"] = Describe("T:" + Doc(type)),
            ["type"] = "object",
            ["x-order"] = order,
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static string Doc(Type type) => type.FullName!.Replace('+', '.');

    /// <summary>The schema of a value of a type.</summary>
    /// <param name="type">The member's type.</param>
    /// <param name="nullable">Whether the value may be null.</param>
    /// <param name="nullability">The member's nullability, for its type arguments.</param>
    /// <param name="optional">Whether the member is left out when null (then it is never written as null).</param>
    private JsonObject Value(Type type, bool nullable, NullabilityInfo? nullability, bool optional = false)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            type = underlying;
            nullable = !optional;
        }

        if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonNode))
            return nullable || optional ? [] : new JsonObject { ["not"] = new JsonObject { ["type"] = "null" } };
        var simple = type == typeof(string) ? "string"
            : type == typeof(bool) ? "boolean"
            : type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ? "integer"
            : type == typeof(double) || type == typeof(float) || type == typeof(decimal) ? "number"
            : null;
        if (simple is not null)
            return new JsonObject { ["type"] = nullable ? new JsonArray(simple, "null") : simple };
        if (type.IsEnum)
        {
            if (type.GetCustomAttribute<JsonConverterAttribute>() is null)
                throw new InvalidOperationException($"{type.FullName} is written as a number; name its values.");
            var values = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (JsonNode?)(f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(f.Name)));
            var schema = new JsonObject { ["type"] = nullable ? new JsonArray("string", "null") : "string", ["enum"] = new JsonArray([.. values]) };
            if (nullable)
                schema["enum"]!.AsArray().Add(null);
            return schema;
        }

        var info = Web.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Dictionary:
            {
                var element = nullability?.GenericTypeArguments is { Length: 2 } args ? args[1] : null;
                return new JsonObject
                {
                    ["type"] = nullable ? new JsonArray("object", "null") : "object",
                    ["additionalProperties"] = Value(info.ElementType!, element?.ReadState == NullabilityState.Nullable, element),
                };
            }

            case JsonTypeInfoKind.Enumerable:
            {
                var element = nullability?.ElementType ?? (nullability?.GenericTypeArguments is { Length: 1 } args ? args[0] : null);
                return new JsonObject
                {
                    ["type"] = nullable ? new JsonArray("array", "null") : "array",
                    ["items"] = Value(info.ElementType!, element?.ReadState == NullabilityState.Nullable, element),
                };
            }

            case JsonTypeInfoKind.Object:
                var reference = Reference(type);
                return nullable ? new JsonObject { ["anyOf"] = new JsonArray(reference, new JsonObject { ["type"] = "null" }) } : reference;
            default:
                throw new InvalidOperationException($"{type.FullName} has a custom converter; the contract cannot describe it.");
        }
    }

    /// <summary>A member's or type's documentation as plain text, without citations of engineering documents.</summary>
    private string Describe(string member, int depth = 0)
    {
        if (!_docs.TryGetValue(member, out var doc) || doc.Element("summary") is not { } summary)
            return "";
        var nodes = summary.Nodes().Where(n => n is not XText t || !string.IsNullOrWhiteSpace(t.Value)).ToList();
        // "As <see cref="PackageRecord.DisplayName"/>." takes that member's text.
        if (depth < 4 && nodes.Count is 2 or 3 && nodes[0] is XText { Value: var lead } && lead.Trim() == "As"
            && nodes[1] is XElement { Name.LocalName: "see" } see && see.Attribute("cref")?.Value is { } cref && cref.StartsWith("P:", StringComparison.Ordinal)
            && (nodes.Count == 2 || nodes[2] is XText { Value: var tail } && tail.Trim() == "."))
        {
            return Describe(cref, depth + 1);
        }

        return Clean(Render(summary));
    }

    private static string Render(XElement element)
    {
        var text = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText t:
                    text.Append(t.Value);
                    break;
                case XElement { Name.LocalName: "c" } c:
                    text.Append('`').Append(c.Value).Append('`');
                    break;
                case XElement { Name.LocalName: "see" } s when s.Attribute("langword") is { } word:
                    text.Append(word.Value);
                    break;
                case XElement { Name.LocalName: "see" } s when s.Attribute("cref") is { } cref:
                    text.Append('`').Append(CrefText(cref.Value)).Append('`');
                    break;
                case XElement { Name.LocalName: "paramref" or "typeparamref" } p:
                    text.Append('`').Append(JsonNamingPolicy.CamelCase.ConvertName(p.Attribute("name")?.Value ?? "")).Append('`');
                    break;
                case XElement e:
                    text.Append(' ').Append(Render(e)).Append(' ');
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>A cref as a reader knows it: a record as its definition name, a member as <c>record.field</c>.</summary>
    private static string CrefText(string cref)
    {
        var name = cref[2..];
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
            name = name[..paren];
        var parts = name.Split('.');
        return cref.StartsWith("T:", StringComparison.Ordinal)
            ? JsonNamingPolicy.CamelCase.ConvertName(parts[^1])
            : JsonNamingPolicy.CamelCase.ConvertName(parts[^2]) + "." + JsonNamingPolicy.CamelCase.ConvertName(parts[^1]);
    }

    /// <summary>Whitespace collapsed, and parentheses that only cite an engineering document dropped.</summary>
    internal static string Clean(string text)
    {
        text = Whitespace().Replace(text, " ").Trim();
        text = Citation().Replace(text, "");
        text = ResolverType().Replace(text, "");
        text = SpaceBeforePunctuation().Replace(text, "$1");
        return text.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A parenthesis naming a design file, a section, an erratum, a spec section, a decision or a date: "(next-phase-2-design.md section 3.5)",
    // "(erratum E43)", "(2026-10-11)", "(decision 33)".
    [GeneratedRegex(@"\s*\((?:[^()]*?(?:\.md\b|\bsection \d|\berrat(?:um|a)\b|\bSPEC\b|\bdecisions? \d|\b\d{4}-\d{2}-\d{2}\b|\bE\d{1,2}[a-z]?\b|\bD\d{1,2}\b|\bPD\d+\b)[^()]*)\)")]
    private static partial Regex Citation();

    // What names a resolver type: ", projected from `rTable`", "(see `rDatabaseType.nativeName`)", "(as `rQuery.sql`)".
    [GeneratedRegex(@",? projected from (?:the resolver's )?`r[A-Z]\w*`|\s*\((?:see|as) `r[A-Z][\w.]*`\)")]
    private static partial Regex ResolverType();

    [GeneratedRegex(@"\bnull\b|\bleft out\b|\babsent\b|\bomitted\b", RegexOptions.IgnoreCase)]
    private static partial Regex NullWords();

    // A description that already names when the value is there: "for an entity table", "when known", "if any", "For a domain:".
    [GeneratedRegex(@"\b(?:for (?:a|an|the|each)|for `|when|if|unless|with status|on an?|set with|in a|only)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Conditional();

    [GeneratedRegex(@"\s+([.,;:])")]
    private static partial Regex SpaceBeforePunctuation();
}
