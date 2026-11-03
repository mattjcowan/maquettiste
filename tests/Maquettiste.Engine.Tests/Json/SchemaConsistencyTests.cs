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
        data.Add("locale.json", typeof(LocaleShard));
        data.Add("pack-case.json", typeof(Maquettiste.Engine.Generation.PackCaseSettings));
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

    [Fact]
    public void Layout_marks_objects_that_require_an_id()
    {
        static ObjectLayout Items(ObjectLayout layout, string key) => layout.Properties[key].Layout.Items!;

        var entity = Schemas.GetLayout("entity.json");
        var table = Schemas.GetLayout("table.json");
        var referenceType = Schemas.GetLayout("reference-type.json");
        var batch = Schemas.GetLayout("batch.json");

        // Element roots and sub-elements: the schema requires an id typed common.json#/$defs/id, own or through a $ref.
        Assert.All(KindInfo.All, kind => Assert.True(Schemas.GetLayout(kind.SchemaFile).RequiresId, kind.SchemaFile));
        Assert.True(Items(entity, "attributes").RequiresId);
        Assert.True(Items(entity, "bindings").RequiresId);
        Assert.True(Items(table, "columns").RequiresId);
        Assert.True(Items(table, "indexes").RequiresId);
        Assert.True(Items(Schemas.GetLayout("enum.json"), "members").RequiresId);
        Assert.True(Items(Schemas.GetLayout("seed.json"), "rows").RequiresId);
        Assert.True(referenceType.Properties["code"].Layout.RequiresId);

        // Not mint sites: arrays and plain objects, an id that is not a model id, and a batch operation, whose id is required only
        // by a conditional then, which is never merged into the layout.
        Assert.False(entity.Properties["attributes"].Layout.RequiresId);
        Assert.False(Items(Items(table, "indexes"), "columns").RequiresId);
        Assert.False(Items(Schemas.GetLayout("pack.json"), "units").RequiresId);
        Assert.False(Schemas.GetLayout("maquettiste.json").RequiresId);
        Assert.False(Schemas.GetLayout("snapshot.json").RequiresId);
        Assert.False(batch.RequiresId);
        Assert.False(Items(batch, "operations").RequiresId);
        Assert.False(ObjectLayout.FreeForm.RequiresId);
    }

    [Fact]
    public void Binding_provenance_maps_column_ids_to_two_hash_parts_and_writes_sorted()
    {
        // next-phase-2-design.md section 3.3 (D30): provenance follows delete in a binding; its columns map a column id or a keyed id to
        // "<8 hex>:<8 hex>", and the canonical writer sorts the keys.
        var binding = Schemas.GetLayout("entity.json").Properties["bindings"].Layout.Items!;
        Assert.Equal(["id", "database", "source", "constants", "fields", "columns", "write", "delete", "provenance", "checks", "description", "tags", "properties"], binding.Keys);

        const string Keyed = "01JAX3K9V2Q7M4T8W1Z5C6B0DE.01JAX3KB2C3D4E5F6G7H8J9KAM", Plain = "01JAX3KC3D4E5F6G7H8J9KAMBN";
        string Entity(string provenance) =>
            "{\"kind\":\"entity\",\"id\":\"01JAX3K9V2Q7M4T8W1Z5C6B0AA\",\"name\":\"Note\",\"bindings\":[{\"id\":\"01JAX3K9V2Q7M4T8W1Z5C6B0AB\","
            + "\"database\":\"01JAX3JZ0H6N2P5R8S1T4V7W9Y\",\"source\":\"01JAX3K9V2Q7M4T8W1Z5C6B0DE\",\"provenance\":" + provenance + "}]}";
        static IReadOnlyList<Maquettiste.Engine.Diagnostics.Diagnostic> Evaluate(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return Schemas.Evaluate("entity.json", doc.RootElement, "model/entities/note.json");
        }

        var valid = Entity($$$"""{"columns":{"{{{Plain}}}":"8a1f3c20:5be0d7a9","{{{Keyed}}}":"41d09e7b:77c2a015"}}""");
        Assert.Empty(Evaluate(valid));
        foreach (var bad in new[]
        {
            "{}",
            """{"columns":{"not-an-id":"8a1f3c20:5be0d7a9"}}""",
            $$$"""{"columns":{"{{{Plain}}}":"8A1F3C20:5be0d7a9"}}""",
            $$$"""{"columns":{"{{{Plain}}}":"8a1f3c20"}}""",
            """{"columns":{},"hash":"8a1f3c20"}""",
        })
        {
            Assert.Contains(Evaluate(Entity(bad)), d => d.Rule == "MQ1002" && (d.JsonPointer ?? "").StartsWith("/bindings/0/provenance", StringComparison.Ordinal));
        }

        var written = System.Text.Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(valid)!.AsObject(), "entity.json", "model/entities/note.json"));
        Assert.True(written.IndexOf(Keyed, StringComparison.Ordinal) < written.IndexOf(Plain, StringComparison.Ordinal), written);
    }

    [Fact]
    public void Table_members_validate_alike_after_the_move_to_common()
    {
        // next-phase-2-design.md section 3.1 (D1): table.json's members are $refs to common.json $defs, so a stereotype can declare
        // the same shapes. The layout reaches each def's keys, a valid table stays valid, and each member's failures keep their pointers.
        var common = JsonNode.Parse(Schemas.GetFileBytes("common.json").Span)!["$defs"]!;
        var table = Schemas.GetLayout("table.json");
        foreach (var (property, def) in new[]
        {
            ("columns", "tableColumn"), ("uniques", "uniqueConstraint"), ("foreignKeys", "foreignKey"), ("checks", "checkConstraint"),
            ("indexes", "tableIndex"), ("primaryKey", "primaryKey"),
        })
        {
            var layout = table.Properties[property].Layout;
            var member = layout.Items ?? layout;
            Assert.Equal(common[def]!["x-order"]!.AsArray().Select(n => n!.GetValue<string>()), member.Keys);
            Assert.Equal(common[def]!["required"]!.AsArray().Any(n => n!.GetValue<string>() == "id"), member.RequiresId);
        }

        const string head = "{\"kind\":\"table\",\"id\":\"01JAX3K9V2Q7M4T8W1Z5C6B0DE\",\"name\":\"t\",\"database\":\"01JAX3JZ0H6N2P5R8S1T4V7W9Y\"";
        const string valid = head + """
            ,"columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"id","type":"uuid","nullable":false},
                       {"id":"01JAX3KC3D4E5F6G7H8J9KAMBN","name":"parent_id","type":"uuid"}],
            "primaryKey":{"id":"01JAX3KD4E5F6G7H8J9KAMBNCP","columns":["01JAX3KB2C3D4E5F6G7H8J9KAM"]},
            "uniques":[{"id":"01JAX3KE5F6G7H8J9KAMBNCPDQ","columns":["01JAX3KC3D4E5F6G7H8J9KAMBN"]}],
            "foreignKeys":[{"id":"01JAX3KF6G7H8J9KAMBNCPDQER","columns":["01JAX3KC3D4E5F6G7H8J9KAMBN"],"referencesTable":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","onDelete":"cascade"}],
            "checks":[{"id":"01JAX3KG7H8J9KAMBNCPDQERFS","expression":{"*":"parent_id <> id"}}],
            "indexes":[{"id":"01JAX3KH8J9KAMBNCPDQERFSGT","columns":[{"column":"01JAX3KC3D4E5F6G7H8J9KAMBN"}],"where":"parent_id IS NOT NULL"}]}
            """;
        Assert.Empty(Evaluate(valid));
        Assert.Empty(Evaluate(head + ""","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"id","type":"uuid"}],"primaryKey":{"columns":["01JAX3KB2C3D4E5F6G7H8J9KAM"]}}"""));
        // Section 3.2 (D20): a column's typeRef is the object form only, and sensitive takes pii or secret.
        Assert.Empty(Evaluate(head + ""","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"status","type":"string","typeRef":{"ref":"01JAX3KC3D4E5F6G7H8J9KAMBN"},"length":1,"sensitive":"pii"}]}"""));

        foreach (var (json, pointer) in new[]
        {
            (""","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"id","type":"uuid","colour":"red"}]}""", "/columns/0/colour"),
            (""","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"id","type":"int99"}]}""", "/columns/0/type"),
            (""","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"s","type":"string","typeRef":"string"}]}""", "/columns/0/typeRef"),
            (""","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"s","type":"string","typeRef":{"ref":"01JAX3KC3D4E5F6G7H8J9KAMBN","of":"x"}}]}""", "/columns/0/typeRef"),
            (""","columns":[{"id":"01JAX3KB2C3D4E5F6G7H8J9KAM","name":"s","type":"string","sensitive":"public"}]}""", "/columns/0/sensitive"),
            (""","primaryKey":{"id":"not-an-id","columns":["c"]}}""", "/primaryKey/id"),
            (""","primaryKey":{"columns":[]}}""", "/primaryKey/columns"),
            (""","uniques":[{"id":"01JAX3KE5F6G7H8J9KAMBNCPDQ","columns":["c"],"clustered":true}]}""", "/uniques/0/clustered"),
            (""","foreignKeys":[{"id":"01JAX3KF6G7H8J9KAMBNCPDQER","columns":["c"],"referencesTable":"t","onDelete":"explode"}]}""", "/foreignKeys/0/onDelete"),
            (""","checks":[{"id":"01JAX3KG7H8J9KAMBNCPDQERFS","expression":{"cobol":"x"}}]}""", "/checks/0/expression"),
            (""","indexes":[{"id":"01JAX3KH8J9KAMBNCPDQERFSGT","columns":[{"column":"c"}],"method":"bitmap"}]}""", "/indexes/0/method"),
        })
        {
            var diagnostics = Evaluate(head + json);
            Assert.True(diagnostics.Any(d => d.Rule == "MQ1002" && (d.JsonPointer ?? "").StartsWith(pointer, StringComparison.Ordinal)),
                $"{pointer}: {string.Join("; ", diagnostics.Select(d => d.JsonPointer + " " + d.Message))}");
        }

        static IReadOnlyList<Maquettiste.Engine.Diagnostics.Diagnostic> Evaluate(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return Schemas.Evaluate("table.json", doc.RootElement, "model/databases/main/tables/t.json");
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
        && type != typeof(Description) && type != typeof(TypeRef) && type != typeof(PackExport) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
}
