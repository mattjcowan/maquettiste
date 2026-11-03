using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Migrations;

/// <summary>The fact content hash (next-phase-1-design.md section 3.2, decision 15): what a fact means, never its annotations or order.</summary>
public sealed class FactHashTests
{
    private const string DatabaseId = "01J92P0V1QRN2181XM2ZWE02W4";

    private static Migration Narrowing() => new()
    {
        Id = "01JBF3R5Q7W0X2Y4Z6A8B0C2D4",
        Database = DatabaseId,
        Fact = MigrationFact.ChangeType,
        Object = new MigrationObject
        {
            Kind = "column", Table = "01J92P0V0FJ23CGSNKM7P1W5V7@" + DatabaseId, Key = "01J92P0V0R3VSP7D5238DTNZX1", Schema = "billing",
            TableName = "invoices", Name = "number",
        },
        From = new MigrationState { Type = "string", Length = 32, NativeType = "text" },
        To = new MigrationState { Type = "string", Length = 32, NativeType = "varchar(20)" },
        Conversion = new MigrationConversion { Cast = true },
    };

    [Fact]
    public void Fact_content_hash_covers_the_facts_meaning_only()
    {
        var fact = Narrowing();
        var hash = FactHash.Of(fact);
        Assert.Matches("^[0-9a-f]{64}$", hash);

        // The same fact as a file holds it (canonical bytes, with $schema) and through the engine's own writer hashes alike.
        var bytes = TestServices.Json.Serialize(fact, "migration.json", ".maquettiste/model/databases/main/migrations/open/" + fact.Id.ToLowerInvariant() + ".json");
        using (var document = JsonDocument.Parse(bytes))
            Assert.Equal(hash, FactHash.Of(document.RootElement));
        Assert.Equal(hash, FactHash.Of(fact, TestServices.Json));

        // Annotations, the order and the hold change nothing.
        var annotated = fact with
        {
            Name = "invoice number narrowing",
            DisplayName = "Invoice number",
            PluralName = "Invoice numbers",
            Description = new Description { Text = "Invoice numbers fit in twenty characters." },
            Tags = ["billing"],
            Category = "01JBF3R5Q7W0X2Y4Z6A8B0C2D5",
            Stereotypes = ["01JBF3R5Q7W0X2Y4Z6A8B0C2D6"],
            Properties = ImmutableSortedDictionary<string, JsonElement>.Empty.Add("owner", JsonSerializer.SerializeToElement("billing")),
            Generation = ImmutableSortedDictionary<string, GenerationHints>.Empty.Add("*", new GenerationHints { Skip = true }),
            After = ["01JBF3R5Q7W0X2Y4Z6A8B0C2D1", "01JBF3R5Q7W0X2Y4Z6A8B0C2D0"],
            Held = true,
            Released = true,
        };
        Assert.Equal(hash, FactHash.Of(annotated));

        // What the fact means changes it: a key, a state, a conversion, the type, the id, the database.
        Assert.NotEqual(hash, FactHash.Of(fact with { Object = fact.Object! with { Key = "01J92P0V0R3VSP7D5238DTNZX2" } }));
        Assert.NotEqual(hash, FactHash.Of(fact with { Object = fact.Object! with { Table = "01K6BND0000000000000000001" } }));
        Assert.NotEqual(hash, FactHash.Of(fact with { To = fact.To! with { NativeType = "varchar(24)" } }));
        Assert.NotEqual(hash, FactHash.Of(fact with { Conversion = new MigrationConversion { Expression = ImmutableSortedDictionary<string, string>.Empty.Add("*", "left({column}, 20)") } }));
        Assert.NotEqual(hash, FactHash.Of(fact with { Conversion = null }));
        Assert.NotEqual(hash, FactHash.Of(fact with { DataCheck = new MigrationDataCheck { Accept = "numbers are issued with 12 characters" } }));
        Assert.NotEqual(hash, FactHash.Of(fact with { Id = "01JBF3R5Q7W0X2Y4Z6A8B0C2D9" }));
        Assert.NotEqual(hash, FactHash.Of(fact with { Database = "01J92P0V1QRN2181XM2ZWE02W5" }));

        // after is sorted by the writer, so two copies that list it in another order write the same bytes too.
        var reordered = TestServices.Json.Serialize(annotated with { After = [.. annotated.After.Reverse()] }, "migration.json", "model/databases/main/migrations/open/a.json");
        Assert.Equal(TestServices.Json.Serialize(annotated, "migration.json", "model/databases/main/migrations/open/a.json"), reordered);
    }

    /// <summary>A merge's value map (next-phase-2-design.md section 3.5): CreditTerm's n90 into PaymentTerm's net60, in a column and a collection.</summary>
    private static Migration ValueMap() => new()
    {
        Id = "01JCB7Q1D2E3F4G5H6J7K8M9N0",
        Database = DatabaseId,
        Fact = MigrationFact.ValueMap,
        Objects =
        [
            new MigrationObject
            {
                Kind = "column", Table = "01J92P0V0FJ23CGSNKM7P1W5V7@" + DatabaseId, Key = "01JCB7P4K5M6N7P8Q9R0S1T2V3", Schema = "billing",
                TableName = "customers", Name = "credit_term",
            },
        ],
        Collections =
        [
            new MigrationCollection { Table = "01J92P0V0FJ23CGSNKM7P1W5V7@" + DatabaseId, Schema = "billing", TableName = "customers", Attribute = "01JCB7P4K5M6N7P8Q9R0S1T2V5", Name = "terms" },
            new MigrationCollection { Table = "01J92P0V0FJ23CGSNKM7P1W5V7@" + DatabaseId, Schema = "billing", TableName = "customers", Attribute = "01JCB7P4K5M6N7P8Q9R0S1T2V4", Name = "pastTerms" },
        ],
        From = new MigrationState { Name = "CreditTerm", PluralName = "CreditTerms" },
        To = new MigrationState { Name = "PaymentTerm", PluralName = "PaymentTerms" },
        ReferenceType = "01JCB7P2N4Q6S8T0V2W4X6Y8Z0",
        FromReferenceType = "01JCB7R5S6T7V8W9X0Y1Z2A3B4",
        Values = [new MigrationValue { From = "n90", To = "net60" }],
    };

    [Fact]
    public void Collections_and_names_count_in_the_value_map_hash()
    {
        var fact = ValueMap();
        var hash = FactHash.Of(fact);
        const string Path = ".maquettiste/model/databases/main/migrations/open/01jcb7q1d2e3f4g5h6j7k8m9n0.json";
        var bytes = TestServices.Json.Serialize(fact, "migration.json", Path);
        using (var document = JsonDocument.Parse(bytes))
        {
            Assert.Equal(hash, FactHash.Of(document.RootElement));
            // Collections are written by table, then attribute; the new fields keep their places.
            Assert.Equal(["01JCB7P4K5M6N7P8Q9R0S1T2V4", "01JCB7P4K5M6N7P8Q9R0S1T2V5"],
                document.RootElement.GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("attribute").GetString()));
            Assert.Equal(["objects", "collections", "from", "to", "referenceType", "fromReferenceType", "values"],
                document.RootElement.EnumerateObject().Select(p => p.Name).Where(n => n is "objects" or "collections" or "from" or "to" or "referenceType" or "fromReferenceType" or "values"));
            Assert.Equal(["name", "pluralName"], document.RootElement.GetProperty("to").EnumerateObject().Select(p => p.Name));
        }

        // Two copies that list the collections in another order write the same bytes, and only the sorted file is canonical.
        Assert.Equal(bytes, TestServices.Json.Serialize(fact with { Collections = [.. fact.Collections.Reverse()] }, "migration.json", Path));
        Assert.True(TestServices.Json.IsCanonical(bytes, "migration.json", Path));
        var unsorted = JsonNode.Parse(bytes)!;
        var items = unsorted["collections"]!.AsArray().Select(c => c!.DeepClone()).Reverse().ToList();
        unsorted["collections"] = new JsonArray([.. items]);
        Assert.False(TestServices.Json.IsCanonical(System.Text.Encoding.UTF8.GetBytes(unsorted.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"), "migration.json", Path));

        // Each new field is part of what the fact means.
        Assert.NotEqual(hash, FactHash.Of(fact with { Collections = [fact.Collections[0]] }));
        Assert.NotEqual(hash, FactHash.Of(fact with { Collections = [fact.Collections[0] with { Table = "01K6BND0000000000000000001" }, fact.Collections[1]] }));
        Assert.NotEqual(hash, FactHash.Of(fact with { Collections = [fact.Collections[0] with { Name = "paymentTerms" }, fact.Collections[1]] }));
        Assert.NotEqual(hash, FactHash.Of(fact with { FromReferenceType = null }));
        Assert.NotEqual(hash, FactHash.Of(fact with { To = fact.To! with { PluralName = "PaymentTermList" } }));
        Assert.NotEqual(hash, FactHash.Of(fact with { From = fact.From! with { Name = "Credit" } }));
    }

    [Fact]
    public void The_hash_is_the_same_wherever_the_file_sits()
    {
        var fact = Narrowing();
        var conventional = TestServices.Json.Serialize(fact, "migration.json", "model/databases/main/migrations/open/x.json");
        var misplaced = TestServices.Json.Serialize(fact, "migration.json", "model/x.json");
        Assert.NotEqual(conventional, misplaced); // the $schema reference differs
        using var a = JsonDocument.Parse(conventional);
        using var b = JsonDocument.Parse(misplaced);
        Assert.Equal(FactHash.Of(a.RootElement), FactHash.Of(b.RootElement));
    }
}
