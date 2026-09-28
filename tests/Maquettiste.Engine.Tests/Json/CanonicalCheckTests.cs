using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Json;

/// <summary>
/// The element-based canonical check (<see cref="CanonicalCheck"/>, used by the loader) must agree with the node-based check it
/// replaces on every input: fixture files, their canonical rewrites, and seeded random mutations written canonically or not.
/// </summary>
public sealed class CanonicalCheckTests
{
    private static readonly SchemaRegistry Registry = new();
    private static readonly CanonicalJson Canonical = new(Registry);

    private static readonly string[] Values =
    [
        "false", "true", "0", "1", "1.0", "-1", "\"\"", "\"a\"", "\"A\\u0042\"", "\"\\u00e9\"", "null", "[]", "{}", "[1,2]", "{\"a\":null}",
        "{\"b\":1,\"a\":2}", "\"no-action\"", "\"overwrite\"", "\"int64\"", "{\"order\":2}", "{\"order\":-1,\"name\":\"x\"}",
    ];

    private static readonly string[] Names =
    [
        "abstract", "required", "unique", "order", "tags", "stereotypes", "properties", "generation", "description", "nullable",
        "attribute", "onDelete", "length", "zzz", "aaa", "$schema", "name", "default",
    ];

    [Fact]
    public void Agrees_with_the_node_based_check_on_fixtures_and_their_rewrites()
    {
        int files = 0, decided = 0;
        foreach (var (schema, path, bytes) in Corpus())
        {
            files++;
            if (Check(schema, path, bytes, "fixture " + path))
                decided++;
            var rewritten = Canonical.Write(JsonNode.Parse(bytes)!, schema, path);
            Assert.True(Canonical.IsCanonical(rewritten, schema, path), "a canonical rewrite is canonical: " + path);
            Check(schema, path, rewritten, "rewrite " + path);
        }

        Assert.True(files > 50);
        Assert.True(decided * 10 >= files * 9, $"the element check decided only {decided} of {files} fixture files");
    }

    [Fact]
    public void Agrees_with_the_node_based_check_on_random_mutations()
    {
        var random = new Random(4242);
        var count = 0;
        foreach (var (schema, path, bytes) in Corpus())
        {
            for (var i = 0; i < 25; i++)
            {
                var node = JsonNode.Parse(bytes)!;
                var mutations = 1 + random.Next(3);
                for (var m = 0; m < mutations; m++)
                    Mutate(node, random);
                var canonical = Canonical.Write(node, schema, path);
                Check(schema, path, canonical, $"canonical mutation of {path}");
                var loose = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = random.Next(2) == 0 }) + (random.Next(2) == 0 ? "\n" : ""));
                Check(schema, path, loose, $"loose mutation of {path}");
                count++;
            }
        }

        Assert.True(count > 1000);
    }

    [Theory]
    [InlineData("{\n  \"$schema\": \"../../.schema/v1/entity.json\",\n  \"kind\": \"entity\",\n  \"id\": \"01JAX3K9V2Q7M4T8W1Z5C6B0DE\",\n  \"name\": \"Invoice\"\n}\n", true)]
    [InlineData("{\n  \"$schema\": \"../../.schema/v1/entity.json\",\n  \"kind\": \"entity\",\n  \"id\": \"01JAX3K9V2Q7M4T8W1Z5C6B0DE\",\n  \"name\": \"Invoice\"\n}", false)]
    [InlineData("{\n  \"$schema\": \"../../.schema/v1/entity.json\",\n  \"kind\": \"entity\",\n  \"id\": \"01JAX3K9V2Q7M4T8W1Z5C6B0DE\",\n  \"name\": \"Invoice\",\n  \"abstract\": false\n}\n", false)]
    [InlineData("{\n  \"$schema\": \"../../.schema/v1/entity.json\",\n  \"id\": \"01JAX3K9V2Q7M4T8W1Z5C6B0DE\",\n  \"kind\": \"entity\",\n  \"name\": \"Invoice\"\n}\n", false)]
    [InlineData("{\r\n  \"$schema\": \"../../.schema/v1/entity.json\",\r\n  \"kind\": \"entity\",\r\n  \"id\": \"01JAX3K9V2Q7M4T8W1Z5C6B0DE\",\r\n  \"name\": \"Invoice\"\r\n}\r\n", false)]
    [InlineData("{\n  \"$schema\": \"../../.schema/v1/entity.json\",\n  \"kind\": \"entity\",\n  \"id\": \"01JAX3K9V2Q7M4T8W1Z5C6B0DE\",\n  \"name\": \"Invoic\\u0065\"\n}\n", false)]
    public void Decides_like_the_canonical_writer(string text, bool expected)
    {
        const string path = ".maquettiste/model/entities/invoice.json";
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal(expected, Canonical.IsCanonicalByNodes(bytes, "entity.json", path));
        Assert.Equal(expected, Canonical.IsCanonical(bytes, "entity.json", path));
    }

    /// <summary>Asserts agreement; returns whether the element check decided without the node-based fallback.</summary>
    private static bool Check(string schema, string path, byte[] bytes, string label)
    {
        var expected = Canonical.IsCanonicalByNodes(bytes, schema, path);
        Assert.Equal(expected, Canonical.IsCanonical(bytes, schema, path));
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.AsMemory(DocumentReader.StripBom(bytes).Length == bytes.Length ? 0 : 3), DocumentReader.ParseOptions);
        }
        catch (JsonException)
        {
            return true; // not parseable: the loader never gets here
        }

        using (document)
        {
            var layout = Registry.GetLayout(schema);
            var reference = document.RootElement.ValueKind == JsonValueKind.Object && layout.Properties.ContainsKey("$schema")
                ? CanonicalJson.SchemaReference(schema, path)
                : "";
            var fast = CanonicalCheck.IsCanonical(bytes, document.RootElement, layout, reference, WriterOptions());
            if (fast is { } verdict)
                Assert.True(verdict == expected, $"element check {verdict}, node check {expected}: {label}\n{Encoding.UTF8.GetString(bytes)}");
            Assert.Equal(expected, Canonical.IsCanonical(bytes, document.RootElement, schema, path));
            return fast is not null;
        }
    }

    private static JsonWriterOptions WriterOptions() => new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static List<(string Schema, string Path, byte[] Bytes)> Corpus()
    {
        var corpus = new List<(string, string, byte[])>();
        foreach (var file in Directory.EnumerateFiles(Fixtures.Root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(file);
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(bytes);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            var path = Path.GetRelativePath(Fixtures.Root, file).Replace('\\', '/');
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                && KindInfo.TryGet(kind.GetString()!, out var info))
                corpus.Add((info.SchemaFile, path, bytes));
            else if (Path.GetFileName(file) == "maquettiste.json")
                corpus.Add(("maquettiste.json", path, bytes));
        }

        return corpus;
    }

    private static void Mutate(JsonNode root, Random random)
    {
        var objects = new List<JsonObject>();
        var arrays = new List<JsonArray>();
        Collect(root, objects, arrays);
        var value = JsonNode.Parse(Values[random.Next(Values.Length)]);
        switch (random.Next(4))
        {
            case 0 when objects.Count > 0: // set a property (often one with a schema default)
                objects[random.Next(objects.Count)][Names[random.Next(Names.Length)]] = value;
                break;
            case 1 when objects.Count > 0: // remove a property
            {
                var o = objects[random.Next(objects.Count)];
                if (o.Count > 0)
                    o.Remove(o.ElementAt(random.Next(o.Count)).Key);
                break;
            }

            case 2 when arrays.Count > 0: // reverse an array (x-sort arrays re-sort)
            {
                var a = arrays[random.Next(arrays.Count)];
                var items = a.Select(i => i?.DeepClone()).Reverse().ToList();
                a.Clear();
                foreach (var item in items)
                    a.Add(item);
                break;
            }

            case 3 when objects.Count > 0: // move a property to the end (key order)
            {
                var o = objects[random.Next(objects.Count)];
                if (o.Count > 1)
                {
                    var (key, item) = o.ElementAt(0);
                    o.Remove(key);
                    o[key] = item;
                }

                break;
            }
        }
    }

    private static void Collect(JsonNode? node, List<JsonObject> objects, List<JsonArray> arrays)
    {
        switch (node)
        {
            case JsonObject o:
                objects.Add(o);
                foreach (var (_, value) in o)
                    Collect(value, objects, arrays);
                break;
            case JsonArray a:
                arrays.Add(a);
                foreach (var item in a)
                    Collect(item, objects, arrays);
                break;
        }
    }
}
