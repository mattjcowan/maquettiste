using System.Collections.Immutable;
using System.Text.Json;
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
