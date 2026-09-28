using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Json;

/// <summary>The schemas are the one source of key order and defaults; these tests keep them and the records in step.</summary>
public sealed class SchemaConsistencyTests
{
    private static readonly ISchemaRegistry Schemas = TestServices.Schemas;

    public static TheoryData<string> SchemaFiles() => [.. TestServices.Schemas.FileNames];

    public static TheoryData<string, Type> Documents()
    {
        var data = new TheoryData<string, Type>();
        foreach (var kind in KindInfo.All)
            data.Add(kind.SchemaFile, kind.ClrType);
        data.Add("maquettiste.json", typeof(ProjectSettings));
        data.Add("pack.json", typeof(PackManifest));
        data.Add("extension.json", typeof(ExtensionSchema));
        data.Add("manifest.json", typeof(ManifestFile));
        data.Add("snapshot.json", typeof(PhysicalSnapshot));
        data.Add("batch.json", typeof(ModelBatch));
        return data;
    }

    [Theory]
    [MemberData(nameof(SchemaFiles))]
    public void Every_object_with_properties_has_an_x_order_listing_exactly_those_keys(string file)
    {
        var root = JsonNode.Parse(Schemas.GetFileBytes(file).Span)!;
        var problems = new List<string>();
        WalkSchema(root, "#", problems);
        Assert.Empty(problems);
    }

    private static void WalkSchema(JsonNode? node, string path, List<string> problems)
    {
        if (node is not JsonObject obj)
            return;

        if (obj["properties"] is JsonObject properties)
        {
            var keys = properties.Select(p => p.Key).ToList();
            var order = (obj["x-order"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList();
            if (order is null)
                problems.Add($"{path}: no x-order");
            else if (!order.SequenceEqual(keys))
                problems.Add($"{path}: x-order [{string.Join(", ", order)}] differs from properties [{string.Join(", ", keys)}]");
        }

        foreach (var (keyword, value) in obj)
        {
            switch (keyword)
            {
                case "properties" or "$defs" or "patternProperties" or "dependentSchemas":
                    foreach (var (name, schema) in value!.AsObject())
                        WalkSchema(schema, $"{path}/{keyword}/{name}", problems);
                    break;
                case "allOf" or "anyOf" or "oneOf" or "prefixItems":
                    for (var i = 0; i < value!.AsArray().Count; i++)
                        WalkSchema(value.AsArray()[i], $"{path}/{keyword}/{i}", problems);
                    break;
                // "if", "not", "contains" and "propertyNames" hold conditions, not document layouts: they need no x-order.
                case "items" or "additionalProperties" or "then" or "else":
                    WalkSchema(value, $"{path}/{keyword}", problems);
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Record_properties_match_schema_properties_and_initializers_match_defaults(string file, Type type)
    {
        var problems = new List<string>();
        Compare(type, Schemas.GetLayout(file), type.Name, problems, []);
        Assert.Empty(problems);
    }

    private static void Compare(Type type, ObjectLayout layout, string path, List<string> problems, HashSet<Type> seen)
    {
        if (!seen.Add(type))
            return;

        var info = EngineJson.Options.GetTypeInfo(type);
        var jsonProperties = info.Properties.Where(p => p.Get is not null).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var recordKeys = jsonProperties.Keys.Order(StringComparer.Ordinal).ToList();
        var schemaKeys = layout.Keys.Order(StringComparer.Ordinal).ToList();
        if (!recordKeys.SequenceEqual(schemaKeys))
        {
            problems.Add($"{path}: record [{string.Join(", ", recordKeys)}] vs schema [{string.Join(", ", schemaKeys)}]");
            return;
        }

        // Defaults: an instance with initializers run (required members stay null) serialized property by property.
        var instance = type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(type);
        foreach (var (name, property) in jsonProperties)
        {
            var schemaProperty = layout.Properties[name];
            if (instance is not null && name is not ("kind" or "$schema") && !property.IsRequired)
                CheckDefault(path + "." + name, property, instance, schemaProperty, problems);

            var child = ElementType(property.PropertyType);
            if (child is not null && schemaProperty.Layout is { } childLayout)
            {
                var target = property.PropertyType == child ? childLayout : childLayout.Items ?? childLayout.MapValues;
                if (target is not null)
                    Compare(child, target, path + "." + name, problems, seen);
            }
        }
    }

    private static void CheckDefault(string path, JsonPropertyInfo property, object instance, LayoutProperty schemaProperty, List<string> problems)
    {
        var value = property.Get!(instance);
        var actual = value is null ? null : JsonSerializer.SerializeToNode(value, property.PropertyType, EngineJson.Options);
        if (actual is null && schemaProperty.DefaultUnless is not null)
            return; // a conditional default (x-default-unless) belongs to a tri-state property whose null means "not set"
        if (schemaProperty.Default is { } expected)
        {
            // Nested default objects compare after canonical omission of their own defaults: compare the canonical text.
            var expectedNode = JsonNode.Parse(expected.GetRawText());
            if (!JsonNode.DeepEquals(Normalize(actual, schemaProperty.Layout), Normalize(expectedNode, schemaProperty.Layout)))
                problems.Add($"{path}: initializer {actual?.ToJsonString() ?? "null"} vs schema default {expected.GetRawText()}");
        }
        else if (actual is not null)
        {
            problems.Add($"{path}: initializer {actual.ToJsonString()} but the schema declares no default");
        }
    }

    private static JsonNode? Normalize(JsonNode? node, ObjectLayout layout)
    {
        if (node is not JsonObject obj || !layout.HasKeys)
            return node;
        var result = new JsonObject();
        foreach (var key in layout.Keys)
        {
            if (!obj.TryGetPropertyValue(key, out var value) || value is null)
                continue;
            var property = layout.Properties[key];
            var normalized = Normalize(value.DeepClone(), property.Layout);
            if (property.Default is { } d && JsonNode.DeepEquals(normalized, Normalize(JsonNode.Parse(d.GetRawText()), property.Layout)))
                continue;
            result[key] = normalized;
        }

        return result;
    }

    private static Type? ElementType(Type type)
    {
        if (IsModelRecord(type))
            return type;
        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            var candidate = args[^1];
            if (IsModelRecord(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsModelRecord(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace is "Maquettiste.Engine.Model" or "Maquettiste.Engine"
        && type != typeof(Description) && type != typeof(TypeRef) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
}
