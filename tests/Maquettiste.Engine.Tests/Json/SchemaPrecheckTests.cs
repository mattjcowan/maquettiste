using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Json;

/// <summary>
/// The schema precheck (<see cref="SchemaPrecheck"/>) must never disagree with the full evaluator: a <c>Valid</c> verdict means
/// JsonSchema.Net finds the document valid, an <c>Invalid</c> verdict means it finds it invalid. Checked over every fixture document,
/// seeded random mutations of them, and edge cases where evaluators could differ.
/// </summary>
public sealed class SchemaPrecheckTests
{
    private static readonly SchemaRegistry Registry = new();

    private static readonly string[] Names =
    [
        "id", "name", "kind", "type", "ref", "entity", "relation", "enum", "attribute", "origin", "mode", "op", "file", "required",
        "length", "precision", "scale", "min", "max", "ends", "attributes", "columns", "description", "stereotypes", "tags", "x",
        "increment", "navigation", "package", "base", "key", "strategy", "storage", "shape", "units", "expectedHash", "element",
    ];

    private static readonly string[] Values =
    [
        "\"\"", "\"a\"", "\"Ab\"", "\"a b\"", "\"\\u00e9\"", "\"e\\u0301\"", "\"\\ud83d\\ude00\"", "\"a\\n\"", "\"ab\\u00a0\"",
        "\"01JAX3K9V2Q7M4T8W1Z5C6B0DE\"", "\"01jax3k9v2q7m4t8w1z5c6b0de\"", "\"synthesized\"", "\"pair\"", "\"update\"", "\"create\"",
        "\"string\"", "\"int32\"", "\"decimal\"", "\"audited\"", "\"Invoice\"", "\"1\"", "\"*\"", "\"postgresql\"", "\"x/rule\"",
        "0", "1", "-1", "1.0", "1.5", "1e2", "99999999999999999999", "2147483648", "true", "false", "null", "[]", "{}",
        "[\"a\",\"a\"]", "[\"a\",\"\\u0061\"]", "[\"a\"]", "[1,1.0]", "[1,2]", "{\"file\":\"x.md\"}", "{\"file\":\"\"}",
        "{\"ref\":\"01JAX3K9V2Q7M4T8W1Z5C6B0DE\"}", "{\"ref\":\"nope\"}", "{\"a\":1}", "\"" + new string('a', 300) + "\"",
    ];

    [Fact]
    public void Every_fixture_document_gets_a_sound_verdict_and_valid_ones_are_decided()
    {
        var corpus = Corpus();
        Assert.True(corpus.Count > 50, "fixture corpus unexpectedly small: " + corpus.Count);
        int fullyValid = 0, decided = 0;
        foreach (var (schema, json, label) in corpus)
        {
            using var document = JsonDocument.Parse(json);
            var full = AssertSound(schema, document.RootElement, label);
            if (full)
            {
                fullyValid++;
                if (Registry.Precheck(schema, document.RootElement) == PrecheckVerdict.Valid)
                    decided++;
            }
        }

        // The precheck is only worth having when it decides the documents a model is made of.
        Assert.True(fullyValid > 40, "few valid fixtures: " + fullyValid);
        Assert.True(decided * 10 >= fullyValid * 9, $"the precheck decided only {decided} of {fullyValid} valid fixture documents");
    }

    [Fact]
    public void Random_mutations_of_fixture_documents_get_sound_verdicts()
    {
        var random = new Random(20260928);
        var checkedCount = 0;
        foreach (var (schema, json, label) in Corpus())
        {
            for (var i = 0; i < 60; i++)
            {
                var node = JsonNode.Parse(json)!;
                var mutations = 1 + random.Next(3);
                for (var m = 0; m < mutations; m++)
                    node = Mutate(node, random);
                using var document = JsonDocument.Parse(node.ToJsonString());
                AssertSound(schema, document.RootElement, $"{label} mutation {i}: {node.ToJsonString()}");
                checkedCount++;
            }
        }

        Assert.True(checkedCount > 3000);
    }

    [Theory]
    [InlineData("entity.json", """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice"}""", "Valid")]
    [InlineData("entity.json", """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","colour":"red"}""", "Invalid")]
    [InlineData("entity.json", """{"kind":"entity","id":"01jax3k9v2q7m4t8w1z5c6b0de","name":"Invoice"}""", "Invalid")]
    [InlineData("entity.json", """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE"}""", "Invalid")]
    [InlineData("entity.json", """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","stereotypes":["a","a"]}""", "Invalid")]
    [InlineData("entity.json", """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","stereotypes":["a","\u0061"]}""", "Invalid")]
    [InlineData("entity.json", """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","description":{"file":"a.md"}}""", "Valid")]
    [InlineData("entity.json", """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","description":{"file":""}}""", "Invalid")]
    public void Decides_plain_cases(string schema, string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, Registry.Precheck(schema, document.RootElement).ToString());
        AssertSound(schema, document.RootElement, json);
    }

    [Theory]
    [InlineData("\"Invoic\\u00e9\"")] // non-ASCII: patterns are left to the full evaluator
    [InlineData("\"Invoice\\n\"")] // a trailing newline: .NET and ECMA-262 anchors differ
    [InlineData("\"e\\u0301\"")] // one text element, two code points
    public void Leaves_dialect_sensitive_strings_to_the_full_evaluator(string name)
    {
        var json = """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":""" + name + "}";
        using var document = JsonDocument.Parse(json);
        Assert.Equal(PrecheckVerdict.Unknown, Registry.Precheck("entity.json", document.RootElement));
        AssertSound("entity.json", document.RootElement, json);
    }

    [Fact]
    public void Repeated_property_names_are_left_to_the_full_evaluator()
    {
        const string json = """{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","name":"Other"}""";
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = true });
        Assert.Equal(PrecheckVerdict.Unknown, Registry.Precheck("entity.json", document.RootElement));
    }

    [Fact]
    public void Evaluate_reports_the_same_failures_as_before()
    {
        // A document the precheck rejects still gets the full evaluator's diagnostics (pointer and message).
        using var document = JsonDocument.Parse("""{"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"2Invoice"}""");
        var diagnostics = Registry.Evaluate("entity.json", document.RootElement, "model/x.json");
        Assert.Contains(diagnostics, d => d.Rule == "MQ1002" && d.JsonPointer == "/name");
    }

    private static bool AssertSound(string schema, JsonElement document, string label)
    {
        var verdict = Registry.Precheck(schema, document);
        var full = Registry.EvaluateFully(schema, document);
        if (verdict == PrecheckVerdict.Valid)
            Assert.True(full, $"precheck said valid, JsonSchema.Net invalid ({schema}): {label}");
        else if (verdict == PrecheckVerdict.Invalid)
            Assert.False(full, $"precheck said invalid, JsonSchema.Net valid ({schema}): {label}");
        return full;
    }

    private static List<(string Schema, string Json, string Label)> Corpus()
    {
        var corpus = new List<(string, string, string)>();
        foreach (var path in Directory.EnumerateFiles(Fixtures.Root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string text;
            JsonElement root;
            try
            {
                text = File.ReadAllText(path);
                using var document = JsonDocument.Parse(text);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            var name = Path.GetFileName(path);
            string? schema = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                && KindInfo.TryGet(kind.GetString()!, out var info))
                schema = info.SchemaFile;
            else if (name == "maquettiste.json")
                schema = "maquettiste.json";
            else if (name == "pack.json")
                schema = "pack.json";
            else if (path.Replace('\\', '/').Contains("/extensions/", StringComparison.Ordinal))
                schema = "extension.json";
            if (schema is not null)
                corpus.Add((schema, text, Path.GetRelativePath(Fixtures.Root, path)));
        }

        return corpus;
    }

    private static JsonNode Mutate(JsonNode root, Random random)
    {
        var slots = new List<(JsonNode Parent, string? Property, int Index)>();
        Collect(root, slots);
        if (slots.Count == 0)
            return root;
        var (parent, property, index) = slots[random.Next(slots.Count)];
        var value = JsonNode.Parse(Values[random.Next(Values.Length)]);
        switch (random.Next(4))
        {
            case 0: // remove
                if (parent is JsonObject o)
                    o.Remove(property!);
                else
                    ((JsonArray)parent).RemoveAt(index);
                break;
            case 1: // replace
                if (parent is JsonObject o2)
                    o2[property!] = value;
                else
                    ((JsonArray)parent)[index] = value;
                break;
            case 2: // add a property to the slot's object, or to the value when it is an object
                var target = parent is JsonObject po ? po : (((JsonArray)parent)[index] as JsonObject);
                if (target is not null)
                    target[Names[random.Next(Names.Length)]] = value;
                break;
            default: // duplicate an array item
                if (parent is JsonArray a && a[index] is { } item)
                    a.Add(item.DeepClone());
                else if (parent is JsonObject o3 && o3[property!] is JsonArray inner && inner.Count > 0)
                    inner.Add(inner[0]?.DeepClone()); // seed cells may be null
                break;
        }

        return root;
    }

    private static void Collect(JsonNode node, List<(JsonNode, string?, int)> slots)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o.ToList())
                {
                    slots.Add((o, name, -1));
                    if (value is not null)
                        Collect(value, slots);
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    slots.Add((a, null, i));
                    if (a[i] is { } item)
                        Collect(item, slots);
                }

                break;
        }
    }
}
