using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Maquettiste.Testing;
using YamlDotNet.RepresentationModel;

namespace Maquettiste.Functions.Tests.Support;

/// <summary>
/// <c>docs/api/openapi.yaml</c> as JSON (read with YamlDotNet), registered with <c>schemas/v1/*.json</c> in a private JsonSchema.Net
/// registry under <c>https://maquettiste.invalid/</c>, so the contract's relative references (<c>../../schemas/v1/entity.json</c>)
/// resolve offline; and the checks that validate a response or a realtime payload against the schema the contract gives it.
/// </summary>
internal static partial class Contract
{
    private const string Base = "https://maquettiste.invalid/";
    private static readonly Uri OpenApiUri = new(Base + "docs/api/openapi.json");
    private static readonly Lazy<State> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The contract as JSON.</summary>
    public static JsonObject OpenApi => Loaded.Value.Document;

    /// <summary>Every operation the functions answer: verb and path, leaving out <c>x-maquettiste-handler: host</c> ones.</summary>
    public static IReadOnlyList<(string Verb, string Path, string OperationId)> Operations => Loaded.Value.Operations;

    /// <summary>
    /// Asserts that a response is what the contract says the operation answers with this status: a documented status, the documented
    /// content type, and a body that validates against the documented schema.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="template">The route template of the operation, as the contract writes it (<c>/api/model/elements/{id}</c>).</param>
    public static void AssertResponse(TestResponse response, string template)
    {
        var verb = response.Request.Method.ToLowerInvariant();
        var operation = OpenApi["paths"]?[template]?[verb]?.AsObject()
            ?? throw new Xunit.Sdk.XunitException($"The contract has no {verb.ToUpperInvariant()} {template}.");
        var statusKey = response.Status.ToString(CultureInfo.InvariantCulture);
        var declared = operation["responses"]?[statusKey]
            ?? throw new Xunit.Sdk.XunitException($"{verb.ToUpperInvariant()} {template} does not document status {statusKey}.\n{response}");
        declared = Resolve(declared);
        var content = declared["content"]?.AsObject();
        if (content is null || content.Count == 0)
        {
            Assert.True(response.Body.Length == 0, $"{verb.ToUpperInvariant()} {template} {statusKey} documents no body, but one came back.\n{response}");
            return;
        }

        var mediaType = (response.ContentType ?? "").Split(';')[0].Trim();
        var entry = content[mediaType]
            ?? throw new Xunit.Sdk.XunitException($"{verb.ToUpperInvariant()} {template} {statusKey} does not document {mediaType}; it documents " +
                string.Join(", ", content.Select(c => c.Key)) + $".\n{response}");
        if (!mediaType.EndsWith("json", StringComparison.Ordinal))
            return; // text/x-diff: the type is the contract.
        AssertValid(entry["schema"]!, JsonDocument.Parse(response.Body).RootElement, $"{verb.ToUpperInvariant()} {template} {statusKey}");
    }

    /// <summary>Asserts that a realtime payload validates against the contract's schema for the event (<c>webhooks.&lt;event&gt;</c>).</summary>
    /// <param name="eventName">The event name, such as <c>model.changed</c>.</param>
    /// <param name="payload">The payload.</param>
    public static void AssertEvent(string eventName, JsonNode payload)
    {
        var schema = OpenApi["webhooks"]?[eventName]?["post"]?["requestBody"]?["content"]?["application/json"]?["schema"]
            ?? throw new Xunit.Sdk.XunitException($"The contract documents no realtime event {eventName}.");
        AssertValid(schema, JsonDocument.Parse(payload.ToJsonString()).RootElement, "realtime " + eventName);
    }

    /// <summary>Asserts that a JSON value validates against a named component schema.</summary>
    /// <param name="component">The component name under <c>components.schemas</c>.</param>
    /// <param name="value">The value.</param>
    public static void AssertSchema(string component, JsonNode value) =>
        AssertValid(new JsonObject { ["$ref"] = "#/components/schemas/" + component }, JsonDocument.Parse(value.ToJsonString()).RootElement, component);

    private static void AssertValid(JsonNode schemaNode, JsonElement instance, string what)
    {
        var state = Loaded.Value;
        var schemaJson = Absolutize(schemaNode.DeepClone());
        schemaJson.AsObject()["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        var schema = JsonSchema.Build(JsonDocument.Parse(schemaJson.ToJsonString()).RootElement, state.BuildOptions,
            new Uri(Base + "checks/" + Guid.NewGuid().ToString("N") + ".json"));
        var results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
            return;
        var errors = (results.Details ?? [])
            .Where(d => d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}) {e.Key}: {e.Value}"))
            .Take(40);
        Assert.Fail($"{what}: the body does not match the contract's schema.\n{string.Join('\n', errors)}\n{instance.GetRawText()[..Math.Min(4000, instance.GetRawText().Length)]}");
    }

    /// <summary>Resolves a <c>$ref</c> to <c>#/components/responses/…</c>.</summary>
    private static JsonNode Resolve(JsonNode node)
    {
        if (node["$ref"]?.GetValue<string>() is { } reference && reference.StartsWith("#/", StringComparison.Ordinal))
        {
            JsonNode? target = OpenApi;
            foreach (var segment in reference[2..].Split('/'))
                target = target?[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
            return target ?? throw new Xunit.Sdk.XunitException($"Unresolved reference {reference}.");
        }

        return node;
    }

    /// <summary>Makes every <c>$ref</c> absolute against the contract's URI, so a schema taken out of it resolves the same way.</summary>
    private static JsonNode Absolutize(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "$ref" && value?.GetValue<string>() is { } reference)
                        obj[key] = new Uri(OpenApiUri, reference).ToString();
                    else if (value is not null)
                        Absolutize(value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                        Absolutize(item);
                }

                break;
        }

        return node;
    }

    private sealed record State(JsonObject Document, BuildOptions BuildOptions, IReadOnlyList<(string Verb, string Path, string OperationId)> Operations);

    private static State Load()
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(Path.Combine(Fixtures.RepoRoot, "docs", "api", "openapi.yaml")))
            yaml.Load(reader);
        var document = ToJson(yaml.Documents[0].RootNode)!.AsObject();

        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry(), Dialect = Dialect.Draft202012 };
        var schemaFolder = Path.Combine(Fixtures.RepoRoot, "schemas", "v1");
        // common.json first: the other files reference its $defs.
        foreach (var file in Directory.EnumerateFiles(schemaFolder, "*.json").OrderBy(f => Path.GetFileName(f) == "common.json" ? 0 : 1).ThenBy(f => f, StringComparer.Ordinal))
            JsonSchema.Build(JsonDocument.Parse(File.ReadAllBytes(file)).RootElement, options, new Uri(Base + "schemas/v1/" + Path.GetFileName(file)));
        options.SchemaRegistry.Register(OpenApiUri, new JsonElementBaseDocument(JsonDocument.Parse(document.ToJsonString()).RootElement, OpenApiUri));

        var operations = new List<(string, string, string)>();
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (verb, operation) in item!.AsObject())
            {
                if (verb is not ("get" or "post" or "put" or "patch" or "delete"))
                    continue;
                if (operation?["x-maquettiste-handler"]?.GetValue<string>() == "host")
                    continue;
                operations.Add((verb.ToUpperInvariant(), path, operation!["operationId"]!.GetValue<string>()));
            }
        }

        return new State(document, options, operations);
    }

    /// <summary>YAML to JSON with the YAML 1.2 core schema for plain scalars (null, booleans, numbers); quoted scalars stay strings.</summary>
    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv =>
            new KeyValuePair<string, JsonNode?>(((YamlScalarNode)kv.Key).Value ?? "", ToJson(kv.Value)))),
        YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(ToJson)]),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (scalar.Style is not (YamlDotNet.Core.ScalarStyle.Plain or YamlDotNet.Core.ScalarStyle.Any))
            return JsonValue.Create(text);
        if (text is "" or "~" or "null" or "Null" or "NULL")
            return null;
        if (text is "true" or "True" or "TRUE")
            return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE")
            return JsonValue.Create(false);
        if (IntegerPattern().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer);
        if (FloatPattern().IsMatch(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        return JsonValue.Create(text);
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();
}
