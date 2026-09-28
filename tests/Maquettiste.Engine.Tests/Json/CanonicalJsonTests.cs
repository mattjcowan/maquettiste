using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Json;

public sealed class CanonicalJsonTests
{
    private const string EntityPath = ".maquettiste/model/entities/invoice.json";
    private static readonly ICanonicalJson Canonical = TestServices.Json;

    private static string Write(string json, string schemaFile = "entity.json", string path = EntityPath) =>
        Encoding.UTF8.GetString(Canonical.Write(JsonNode.Parse(json)!, schemaFile, path));

    [Fact]
    public void Spec_entity_example_is_written_in_canonical_form()
    {
        var bytes = Fixtures.ReadBytes("spec-examples", "invoice.entity.json");

        var text = Encoding.UTF8.GetString(Canonical.Write(JsonNode.Parse(bytes)!, "entity.json", EntityPath));

        const string expected = """
            {
              "$schema": "../../.schema/v1/entity.json",
              "kind": "entity",
              "id": "01JAX3K9V2Q7M4T8W1Z5C6B0DE",
              "name": "Invoice",
              "package": "01JAX3JZ0H6N2P5R8S1T4V7W9Y",
              "description": "A bill issued to a customer.",
              "stereotypes": [
                "aggregate-root",
                "audited",
                "soft-delete"
              ],
              "tags": [
                "billing"
              ],
              "category": "01JAX3N0RECE1VAB1ESCAT0001",
              "key": {
                "attributes": [
                  "01JAX3KA1B2C3D4E5F6G7H8J9K"
                ],
                "strategy": "uuid-v7"
              },
              "attributes": [
                {
                  "id": "01JAX3KA1B2C3D4E5F6G7H8J9K",
                  "name": "id",
                  "type": "uuid",
                  "required": true
                },
                {
                  "id": "01JAX3KB2C3D4E5F6G7H8J9KAM",
                  "name": "number",
                  "type": "string",
                  "length": 32,
                  "required": true,
                  "unique": true
                },
                {
                  "id": "01JAX3KC3D4E5F6G7H8J9KAMBN",
                  "name": "total",
                  "type": {
                    "ref": "01JAX3M0M0NEYVA1VE0BJECT01"
                  }
                },
                {
                  "id": "01JAX3KD4E5F6G7H8J9KAMBNCP",
                  "name": "status",
                  "type": {
                    "ref": "01JAX3M11NV01CESTATVSENVM1"
                  },
                  "required": true
                }
              ]
            }

            """;
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Round_trip_through_records_is_byte_identical()
    {
        var b = new ModelBuilder(seed: 7);
        var billing = b.Package("Billing");
        var money = b.ValueObject("Money", billing).Attr("amount", "decimal", a => a.Precision(18).Scale(2).Required()).Attr("currency", "string", a => a.Length(3));
        var status = b.Enum("InvoiceStatus", billing).Member("Draft", 0, "D").Member("Issued", 1, "I");
        var customer = b.Entity("Customer", billing).Key("id", "uuid", IdentityStrategy.UuidV7).Attr("name", "string", a => a.Length(120).Required().Sensitive());
        var invoice = b.Entity("Invoice", billing).Stereotype("audited").Tag("billing").Property("owner", new { team = "ar", level = 2 })
            .Key("id", "ulid", IdentityStrategy.Ulid)
            .Attr("number", "string", a => a.Length(32).Unique().Default("INV-0").Description("The invoice number."))
            .Attr("total", money)
            .Attr("status", status, a => a.Required().Order(-1))
            .AlternateKey("byNumber", "number");
        b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromMin: 1, fromRole: "customer", toNavigation: "invoices")
            .WithEnd(0, e => e with { OnDelete = ReferentialIntent.Restrict, Ordered = true });
        b.Database("main", Dialect.PostgreSql).Schema("billing");

        foreach (var document in b.BuildDocuments())
        {
            var info = KindInfo.Get(document.Element.Kind);
            var first = Canonical.Serialize(document.Element, info.SchemaFile, document.Path);
            var reread = ElementReader.ReadElement(first);
            var second = Canonical.Serialize(reread, info.SchemaFile, document.Path);

            Assert.Equal(Encoding.UTF8.GetString(first), Encoding.UTF8.GetString(second));
            Assert.True(Canonical.IsCanonical(first, info.SchemaFile, document.Path), document.Path);
            Assert.Equal(document.Element.GetType(), reread.GetType());
        }
    }

    [Fact]
    public void Keys_follow_x_order_at_every_level_and_free_form_maps_sort_ordinal()
    {
        const string shuffled = """
            {
              "attributes": [ { "required": true, "type": "uuid", "name": "id", "id": "01JAX3KA1B2C3D4E5F6G7H8J9K" } ],
              "properties": { "zeta": { "b": 1, "a": 2 }, "alpha": 1 },
              "key": { "strategy": "ulid", "attributes": ["01JAX3KA1B2C3D4E5F6G7H8J9K"] },
              "name": "Invoice",
              "id": "01JAX3K9V2Q7M4T8W1Z5C6B0DE",
              "kind": "entity"
            }
            """;

        var node = JsonNode.Parse(Write(shuffled))!.AsObject();

        Assert.Equal(["$schema", "kind", "id", "name", "key", "attributes", "properties"], node.Select(p => p.Key));
        Assert.Equal(["attributes", "strategy"], node["key"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "name", "type", "required"], node["attributes"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["alpha", "zeta"], node["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["a", "b"], node["properties"]!["zeta"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Values_equal_to_their_schema_default_and_nulls_are_omitted()
    {
        const string verbose = """
            {
              "kind": "relation", "id": "01JAX4R0MEMBERSH1PRE1AT10N", "name": "is member of",
              "relationKind": "association", "displayName": null, "tags": [], "properties": {}, "generation": {},
              "ends": [
                { "id": "01JAX4R3VSEREND0000000000A", "entity": "01JAX4E0VSERENT1TY00000001", "role": "member", "min": 0, "max": "*", "onDelete": "none", "ordered": false },
                { "id": "01JAX4R4TEAMEND0000000000A", "entity": "01JAX4E1TEAMENT1TY00000001", "role": "team", "min": 1, "max": 1, "onDelete": "cascade" }
              ],
              "attributes": [ { "id": "01JAX4R1R01EATTR1BVTE00001", "name": "role", "type": "string", "required": false, "unique": false, "validation": { "rules": [] } } ],
              "allowDuplicates": false
            }
            """;

        var node = JsonNode.Parse(Write(verbose, "relation.json", ".maquettiste/model/relations/is-member-of.json"))!.AsObject();

        Assert.Equal(["$schema", "kind", "id", "name", "ends", "attributes"], node.Select(p => p.Key));
        Assert.Equal(["id", "entity", "role"], node["ends"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "entity", "role", "min", "max", "onDelete"], node["ends"]![1]!.AsObject().Select(p => p.Key));
        // An object with no schema default is kept even when every value inside it was a default.
        Assert.Equal(["id", "name", "type", "validation"], node["attributes"]![0]!.AsObject().Select(p => p.Key));
        Assert.Empty(node["attributes"]![0]!["validation"]!.AsObject());
    }

    [Fact]
    public void Default_objects_are_omitted_once_their_contents_are_defaults()
    {
        var settings = new ProjectSettings
        {
            FormatVersion = 1,
            Limits = new SandboxLimits(),
            Outputs = new OutputSettings { Allow = [new OutputRoot { Path = "db", Commit = true }] },
            Packs = new Dictionary<string, PackSettings> { ["sql-ddl"] = new() },
        };

        var text = Encoding.UTF8.GetString(Canonical.Serialize(settings, "maquettiste.json", ".maquettiste/maquettiste.json"));

        const string expected = """
            {
              "$schema": ".schema/v1/maquettiste.json",
              "formatVersion": 1,
              "outputs": {
                "allow": [
                  {
                    "path": "db",
                    "commit": true
                  }
                ]
              },
              "packs": {
                "sql-ddl": {}
              }
            }

            """;
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Output_is_utf8_without_bom_with_lf_two_space_indent_and_a_trailing_newline()
    {
        const string json = "{\r\n\t\"kind\":\"package\",\"id\":\"01JAX3JZ0H6N2P5R8S1T4V7W9Y\",\"name\":\"Billing\",\"description\":\"Caf\\u00e9 <b> & co\"}";

        var bytes = Canonical.Write(JsonNode.Parse(json)!, "package.json", ".maquettiste/model/packages/billing.json");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\t', text);
        Assert.EndsWith("}\n", text);
        Assert.False(text.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.All(text.Split('\n'), line => Assert.Equal(line.TrimEnd(), line));
        Assert.Contains("\n  \"kind\": \"package\",\n", text);
        Assert.Contains("\"description\": \"Café <b> & co\"", text);
    }

    [Fact]
    public void Is_canonical_rejects_bom_crlf_missing_newline_and_key_disorder()
    {
        var canonical = Canonical.Write(JsonNode.Parse("""{"kind":"package","id":"01JAX3JZ0H6N2P5R8S1T4V7W9Y","name":"Billing"}""")!, "package.json", "model/packages/billing.json");
        var text = Encoding.UTF8.GetString(canonical);
        const string file = "package.json";
        const string path = "model/packages/billing.json";

        Assert.True(Canonical.IsCanonical(canonical, file, path));
        Assert.False(Canonical.IsCanonical([0xEF, 0xBB, 0xBF, .. canonical], file, path));
        Assert.False(Canonical.IsCanonical(Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal)), file, path));
        Assert.False(Canonical.IsCanonical(canonical.AsSpan(0, canonical.Length - 1), file, path));
        Assert.False(Canonical.IsCanonical(Encoding.UTF8.GetBytes(text.Replace("\"id\"", "\"zz\"", StringComparison.Ordinal)), file, path));
        Assert.False(Canonical.IsCanonical("not json"u8, file, path));
    }

    [Fact]
    public void Writing_is_idempotent()
    {
        var once = Canonical.Write(JsonNode.Parse(Fixtures.ReadBytes("spec-examples", "membership.relation.json"))!, "relation.json", "model/relations/m.json");
        var twice = Canonical.Write(JsonNode.Parse(once)!, "relation.json", "model/relations/m.json");

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData(".maquettiste/model/entities/invoice.json", "entity.json", "../../.schema/v1/entity.json")]
    [InlineData("model/entities/invoice.json", "entity.json", "../../.schema/v1/entity.json")]
    [InlineData(".maquettiste/model/databases/main/tables/invoice.json", "table.json", "../../../../.schema/v1/table.json")]
    [InlineData(".maquettiste/model/databases/main/database.json", "database.json", "../../../.schema/v1/database.json")]
    [InlineData(".maquettiste/maquettiste.json", "maquettiste.json", ".schema/v1/maquettiste.json")]
    [InlineData(".maquettiste/templates/sql-ddl/pack.json", "pack.json", "../../.schema/v1/pack.json")]
    [InlineData(".maquettiste/manifest/sql-ddl.json", "manifest.json", "../.schema/v1/manifest.json")]
    [InlineData("repo/sub/.maquettiste/model/enums/status.json", "enum.json", "../../.schema/v1/enum.json")]
    public void Schema_reference_is_relative_to_the_document_folder(string path, string schemaFile, string expected) =>
        Assert.Equal(expected, CanonicalJson.SchemaReference(schemaFile, path));

    [Fact]
    public void Schema_reference_is_rewritten_and_placed_first()
    {
        var node = JsonNode.Parse(Write("""{"name":"Invoice","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","kind":"entity","$schema":"https://example.com/whatever"}"""))!.AsObject();

        Assert.Equal("$schema", node.First().Key);
        Assert.Equal("../../.schema/v1/entity.json", node["$schema"]!.GetValue<string>());
        Assert.Equal("kind", node.Skip(1).First().Key);
    }

    [Fact]
    public void Keys_the_schema_does_not_declare_are_kept_after_declared_ones()
    {
        var node = JsonNode.Parse(Write("""{"zebra":1,"kind":"entity","id":"01JAX3K9V2Q7M4T8W1Z5C6B0DE","name":"Invoice","apple":2}"""))!.AsObject();

        Assert.Equal(["$schema", "kind", "id", "name", "apple", "zebra"], node.Select(p => p.Key));
    }

    [Fact]
    public void Serialize_uses_the_runtime_type_of_an_element()
    {
        Element element = new Package { Id = "01JAX3JZ0H6N2P5R8S1T4V7W9Y", Name = "Billing", Parent = "01JAX3K9V2Q7M4T8W1Z5C6B0DE" };

        var node = JsonNode.Parse(Canonical.Serialize(element, "package.json", "model/packages/billing.json"))!;

        Assert.Equal("package", node["kind"]!.GetValue<string>());
        Assert.Equal("01JAX3K9V2Q7M4T8W1Z5C6B0DE", node["parent"]!.GetValue<string>());
    }

    [Fact]
    public void Reader_dispatches_on_kind_and_rejects_unknown_kinds()
    {
        var element = ElementReader.ReadElement(Fixtures.ReadBytes("spec-examples", "membership.relation.json"));

        var relation = Assert.IsType<Relation>(element);
        Assert.Equal(MaxCardinality.Many, relation.Ends[0].Max);
        Assert.Equal("date", relation.Attributes[1].Type.Builtin);
        Assert.Throws<JsonException>(() => ElementReader.ReadElement("""{"kind":"process","id":"x"}"""u8));
    }
    [Fact]
    public void Attributes_and_categories_are_stable_sorted_by_order_with_missing_order_as_zero()
    {
        // SPEC section 11: arrays in their meaningful order ("order" for attributes). A missing order counts as 0; equal orders
        // keep their array order, so files without any order keep the order they were written in.
        const string entity = """
            {
              "kind": "entity", "id": "01JAX3K9V2Q7M4T8W1Z5C6B0DE", "name": "Invoice",
              "attributes": [
                { "id": "01JAX3KA1B2C3D4E5F6G7H8J9K", "name": "late", "type": "string", "order": 900 },
                { "id": "01JAX3KB2C3D4E5F6G7H8J9KAM", "name": "first", "type": "string" },
                { "id": "01JAX3KC3D4E5F6G7H8J9KAMBN", "name": "early", "type": "string", "order": -1 },
                { "id": "01JAX3KD4E5F6G7H8J9KAMBNCP", "name": "second", "type": "string", "order": 0 }
              ]
            }
            """;
        const string tree = """
            {
              "kind": "category-tree", "id": "01JAX3K9V2Q7M4T8W1Z5C6B0DE", "name": "categories",
              "categories": [
                { "id": "01JAX3KA1B2C3D4E5F6G7H8J9K", "name": "b", "order": 2 },
                { "id": "01JAX3KB2C3D4E5F6G7H8J9KAM", "name": "a", "order": 1 }
              ]
            }
            """;

        var attributes = JsonNode.Parse(Write(entity))!["attributes"]!.AsArray().Select(a => a!["name"]!.GetValue<string>());
        var categories = JsonNode.Parse(Write(tree, "category-tree.json", ".maquettiste/model/vocabularies/categories.json"))!["categories"]!
            .AsArray().Select(c => c!["name"]!.GetValue<string>());

        Assert.Equal(["early", "first", "second", "late"], attributes);
        Assert.Equal(["a", "b"], categories);
    }

    [Fact]
    public void Designed_columns_drop_nullable_true_but_overlay_columns_keep_it()
    {
        const string table = """
            {
              "kind": "table", "id": "01JAX3K9V2Q7M4T8W1Z5C6B0DE", "name": "t", "database": "01JAX3JZ0H6N2P5R8S1T4V7W9Y",
              "columns": [
                { "id": "01JAX3KA1B2C3D4E5F6G7H8J9K", "name": "a", "type": "string", "nullable": true },
                { "id": "01JAX3KB2C3D4E5F6G7H8J9KAM", "name": "b", "type": "string", "nullable": false },
                { "id": "01JAX3KC3D4E5F6G7H8J9KAMBN", "attribute": "01JAX3KD4E5F6G7H8J9KAMBNCP", "nullable": true }
              ]
            }
            """;

        var columns = JsonNode.Parse(Write(table, "table.json", ".maquettiste/model/databases/main/tables/t.json"))!["columns"]!.AsArray();

        Assert.Null(columns[0]!["nullable"]);
        Assert.False(columns[1]!["nullable"]!.GetValue<bool>());
        Assert.True(columns[2]!["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void Empty_navigation_is_the_default_and_is_omitted()
    {
        const string relation = """
            {
              "kind": "relation", "id": "01JAX4R0MEMBERSH1PRE1AT10N", "name": "r",
              "ends": [
                { "id": "01JAX4R3VSEREND0000000000A", "entity": "01JAX4E0VSERENT1TY00000001", "role": "a", "navigation": "" },
                { "id": "01JAX4R4TEAMEND0000000000A", "entity": "01JAX4E1TEAMENT1TY00000001", "role": "b" }
              ]
            }
            """;

        var text = Write(relation, "relation.json", ".maquettiste/model/relations/r.json");
        var reread = Assert.IsType<Relation>(ElementReader.ReadElement(Encoding.UTF8.GetBytes(text)));

        Assert.DoesNotContain("navigation", text, StringComparison.Ordinal);
        Assert.Equal("", reread.Ends[0].Navigation);
        Assert.Equal(reread.Ends[0].Navigation, reread.Ends[1].Navigation);
    }
}
