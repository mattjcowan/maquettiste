using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The extension schemas of one validation run (MQ5001, MQ5004). Each <c>extensions/*.json</c> becomes
/// <c>{ "type": "object", "properties": …, "required": … }</c>, built once with JsonSchema.Net under the base URI
/// <c>https://maquettiste.invalid/extensions/&lt;file&gt;</c> (offline, D24). Element properties are evaluated with the stereotype
/// <c>defaultProperties</c> merged under them (stereotype order, later wins; the element's own values win over all). Failures are
/// leaf keyword failures only, as in <see cref="Json.ISchemaRegistry.Evaluate"/> (D34). Thread-safe after construction.
/// </summary>
internal sealed class ExtensionSet
{
    private const string BaseUri = "https://maquettiste.invalid/extensions/";

    private static readonly FrozenSet<string> ApplicatorKeywords =
        new[] { "properties", "items", "prefixItems", "allOf", "$ref", "additionalProperties", "patternProperties", "dependentSchemas", "then", "else" }
            .ToFrozenSet(StringComparer.Ordinal);

    private readonly List<(ExtensionDocument Document, JsonSchema Schema)> _extensions = [];
    private readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List, Culture = CultureInfo.InvariantCulture };

    /// <summary>Builds the extension schemas, reporting invalid extension files.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="fileDiagnostics">Receives MQ5004 diagnostics for extension files, or <see langword="null"/> to drop them.</param>
    public ExtensionSet(ModelSnapshot model, List<Diagnostic>? fileDiagnostics)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in model.Extensions)
        {
            var extension = document.Schema;
            void Invalid(string message, string pointer) =>
                fileDiagnostics?.Add(RuleCatalog.Create("MQ5004", message, null, document.Path, pointer));

            if (!names.TryAdd(extension.Name, document.Path))
                Invalid($"Extension name '{extension.Name}' is already used by {names[extension.Name]}.", "/name");
            for (var i = 0; i < extension.AppliesTo.Stereotypes.Count; i++)
            {
                var key = extension.AppliesTo.Stereotypes[i];
                if (model.GetStereotype(key) is null)
                    Invalid($"appliesTo names stereotype '{key}', which is not defined.", Ptr.At("/appliesTo/stereotypes", i));
            }

            if (extension.Properties.ValueKind != JsonValueKind.Object)
            {
                Invalid("'properties' must be an object of property schemas.", "/properties");
                continue;
            }

            for (var i = 0; i < extension.Required.Count; i++)
            {
                if (!extension.Properties.TryGetProperty(extension.Required[i], out _))
                    Invalid($"'required' names '{extension.Required[i]}', which 'properties' does not define.", Ptr.At("/required", i));
            }

            var schema = Compile(document, out var error);
            if (schema is null)
            {
                Invalid($"The extension is not a valid JSON Schema: {error}", "/properties");
                continue;
            }

            _extensions.Add((document, schema));
        }
    }

    /// <summary>The number of usable extensions.</summary>
    public int Count => _extensions.Count;

    private JsonSchema? Compile(ExtensionDocument document, out string? error)
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = JsonNode.Parse(document.Schema.Properties.GetRawText()),
        };
        if (document.Schema.Required.Count > 0)
            root["required"] = new JsonArray([.. document.Schema.Required.Select(r => (JsonNode?)JsonValue.Create(r))]);
        try
        {
            var element = JsonSerializer.SerializeToElement(root);
            var fileName = document.Path[(document.Path.LastIndexOf('/') + 1)..];
            var options = new BuildOptions { SchemaRegistry = new global::Json.Schema.SchemaRegistry() };
            var schema = JsonSchema.Build(element, options, new Uri(BaseUri + Uri.EscapeDataString(fileName)));
            using var empty = JsonDocument.Parse("{}");
            _ = schema.Evaluate(empty.RootElement, _evaluation); // surfaces unresolvable references now, once
            error = null;
            return schema;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Evaluates an element and its attributes, enum members and columns against the applicable extensions.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the element's document.</param>
    public void Check(ValidationContext context, Report report)
    {
        if (_extensions.Count == 0)
            return;
        var element = report.Document.Element;
        CheckItem(context, element, "", element.KindName, report);
        IReadOnlyList<ModelAttribute> attributes = element switch
        {
            Entity e => e.Attributes,
            ValueObject v => v.Attributes,
            Relation r => r.Attributes,
            Stereotype s => s.Attributes,
            _ => [],
        };
        for (var i = 0; i < attributes.Count; i++)
            CheckItem(context, attributes[i], Ptr.At("/attributes", i), "attribute", report);
        if (element is EnumType enumType)
        {
            for (var i = 0; i < enumType.Members.Count; i++)
                CheckItem(context, enumType.Members[i], Ptr.At("/members", i), "enum-member", report);
        }

        if (element is Table table)
        {
            for (var i = 0; i < table.Columns.Count; i++)
                CheckItem(context, table.Columns[i], Ptr.At("/columns", i), "column", report);
        }

        if (element is Process process)
        {
            CheckStates(process.States, "");
            for (var i = 0; i < process.Transitions.Count; i++)
                CheckItem(context, process.Transitions[i].Id, process.Transitions[i].Stereotypes, process.Transitions[i].Properties, Ptr.At("/transitions", i), "transition", report);
            for (var i = 0; i < process.Events.Count; i++)
                CheckItem(context, process.Events[i].Id, process.Events[i].Stereotypes, process.Events[i].Properties, Ptr.At("/events", i), "event", report);
        }

        void CheckStates(IReadOnlyList<ProcessState> states, string pointer)
        {
            for (var i = 0; i < states.Count; i++)
            {
                var at = Ptr.At(pointer + "/states", i);
                CheckItem(context, states[i].Id, states[i].Stereotypes, states[i].Properties, at, "state", report);
                CheckStates(states[i].States, at);
            }
        }
    }

    private void CheckItem(ValidationContext context, ElementBase item, string pointer, string kind, Report report) =>
        CheckItem(context, item.Id, item.Stereotypes, item.Properties, pointer, kind, report);

    // A process node (state, transition, event) is not an ElementBase but carries stereotypes and properties the same way.
    private void CheckItem(ValidationContext context, string? id, IReadOnlyList<string> stereotypes, IReadOnlyDictionary<string, JsonElement> properties,
        string pointer, string kind, Report report)
    {
        JsonElement? merged = null;
        foreach (var (document, schema) in _extensions)
        {
            var target = document.Schema.AppliesTo;
            if (target.Kinds.Count > 0 && !target.Kinds.Contains(kind, StringComparer.Ordinal))
                continue;
            if (target.Stereotypes.Count > 0 && !target.Stereotypes.Any(s => stereotypes.Contains(s, StringComparer.Ordinal)))
                continue;
            merged ??= Merge(context.Model, stereotypes, properties);
            EvaluationResults results;
            try
            {
                results = schema.Evaluate(merged.Value, _evaluation);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                report.Add("MQ5004", $"Extension '{document.Schema.Name}' ({document.Path}) could not be evaluated: {e.Message}", pointer + "/properties", id);
                continue;
            }

            if (results.IsValid)
                continue;
            var any = false;
            foreach (var detail in results.Details ?? [])
            {
                if (detail.Errors is null || InsideDiscardedBranch(detail))
                    continue;
                foreach (var (keyword, message) in detail.Errors.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    if (ApplicatorKeywords.Contains(keyword))
                        continue;
                    var location = detail.InstanceLocation.ToString();
                    var text = keyword.Length == 0 ? message : keyword + ": " + message;
                    var (at, origin) = Locate(context.Model, stereotypes, properties, pointer, location);
                    report.Add("MQ5001", $"Extension '{document.Schema.Name}': properties{location} {text}{origin}", at, id);
                    any = true;
                }
            }

            if (!any)
                report.Add("MQ5001", $"Extension '{document.Schema.Name}': the properties do not match the extension schema.", pointer + "/properties", id);
        }
    }

    /// <summary>
    /// The pointer of a failing property value: in the item's own <c>properties</c>, or, for a value that comes from a stereotype's
    /// <c>defaultProperties</c>, the stereotype's entry in the item's <c>stereotypes</c> list (the file has no value to point at).
    /// </summary>
    private static (string Pointer, string Origin) Locate(ModelSnapshot model, IReadOnlyList<string> stereotypes, IReadOnlyDictionary<string, JsonElement> properties,
        string pointer, string location)
    {
        var segments = Ptr.Split(location);
        if (segments.Length == 0 || properties.ContainsKey(segments[0]))
            return (pointer + "/properties" + location, "");
        for (var i = stereotypes.Count - 1; i >= 0; i--)
        {
            if (model.GetStereotype(stereotypes[i]) is { } stereotype && stereotype.DefaultProperties.ContainsKey(segments[0]))
                return (Ptr.At(pointer + "/stereotypes", i), $" (the value comes from the defaultProperties of stereotype '{stereotype.Key}')");
        }

        return (pointer + "/properties" + location, "");
    }

    /// <summary>Merges the stereotype default properties (stereotype order, later wins) under the item's own properties.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="item">The element or sub-element.</param>
    /// <returns>The merged properties as a JSON object with ordinal key order.</returns>
    public static JsonElement Merge(ModelSnapshot model, ElementBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Merge(model, item.Stereotypes, item.Properties);
    }

    private static JsonElement Merge(ModelSnapshot model, IReadOnlyList<string> stereotypes, IReadOnlyDictionary<string, JsonElement> properties)
    {
        var merged = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in stereotypes)
        {
            if (model.GetStereotype(key) is not { } stereotype)
                continue;
            foreach (var (name, value) in stereotype.DefaultProperties)
                merged[name] = value;
        }

        foreach (var (name, value) in properties)
            merged[name] = value;
        return JsonSerializer.SerializeToElement(merged);
    }

    private static bool InsideDiscardedBranch(EvaluationResults node)
    {
        for (var child = node; child.Parent is { } parent; child = parent)
        {
            var parentPath = parent.EvaluationPath.ToString();
            var childPath = child.EvaluationPath.ToString();
            if (childPath.Length <= parentPath.Length + 1 || !childPath.StartsWith(parentPath + "/", StringComparison.Ordinal))
                continue;
            var rest = childPath.AsSpan(parentPath.Length + 1);
            var slash = rest.IndexOf('/');
            var keyword = (slash < 0 ? rest : rest[..slash]).ToString();
            switch (keyword)
            {
                case "if" or "not":
                    return true;
                case "oneOf" or "anyOf" or "contains" when parent.Errors is null || !parent.Errors.ContainsKey(keyword):
                    return true;
            }
        }

        return false;
    }
}
