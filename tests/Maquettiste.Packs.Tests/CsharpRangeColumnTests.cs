using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Range and multirange columns in csharp-dapper (next-phase-1-design.md section 3.4): held as their text form, a string, like JSON;
/// on PostgreSQL the repository reads <c>&lt;column&gt;::text</c> and writes <c>CAST(@&lt;property&gt; AS &lt;native type&gt;)</c>.
/// The model: entity Slot (package Booking) mapped to the PostgreSQL database <c>main</c>, whose table overlay adds two range
/// columns, <c>seats</c> (a range of int32) and <c>days</c> (a multirange of date, not null). Golden
/// <c>tests/fixtures/golden/csharp-dapper/range-columns</c>.
/// </summary>
public sealed class CsharpRangeColumnTests
{
    private static string Id(int n) => "01CR" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static PackRepo Create(string dialect)
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper"), Path.Combine(repo.Repo.ModelRoot, "templates", "csharp-dapper"));
        const string Model = ".maquettiste/model/";
        repo.Write(".maquettiste/maquettiste.json", Json(new JsonObject
        {
            ["$schema"] = ".schema/v1/maquettiste.json",
            ["formatVersion"] = 1,
            ["name"] = "range-columns",
            ["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "src/Generated" }) },
            ["packs"] = new JsonObject { ["csharp-dapper"] = new JsonObject { ["output"] = "src/Generated" } },
        }));
        repo.Write(Model + "packages/booking.json", Json(new JsonObject
        {
            ["$schema"] = "../../.schema/v1/package.json", ["kind"] = "package", ["id"] = Id(1), ["name"] = "Booking",
        }));
        repo.Write(Model + "entities/slot.json", Json(new JsonObject
        {
            ["$schema"] = "../../.schema/v1/entity.json",
            ["kind"] = "entity",
            ["id"] = Id(2),
            ["name"] = "Slot",
            ["package"] = Id(1),
            ["key"] = new JsonObject { ["attributes"] = new JsonArray(Id(3)), ["strategy"] = "uuid-v7" },
            ["attributes"] = new JsonArray(
                new JsonObject { ["id"] = Id(3), ["name"] = "id", ["type"] = "uuid", ["required"] = true },
                new JsonObject { ["id"] = Id(4), ["name"] = "name", ["type"] = "string", ["length"] = 100, ["required"] = true }),
        }));
        var database = new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/database.json", ["kind"] = "database", ["id"] = Id(5), ["name"] = "main", ["dialect"] = dialect,
        };
        if (dialect == "postgresql")
        {
            database["version"] = "16";
            database["defaultSchema"] = "app";
            database["schemas"] = new JsonArray(new JsonObject { ["id"] = Id(6), ["name"] = "app" });
        }

        repo.Write(Model + "databases/main/database.json", Json(database));
        repo.Write(Model + "mappings/slot-in-main.json", Json(new JsonObject
        {
            ["$schema"] = "../../.schema/v1/mapping.json", ["kind"] = "mapping", ["id"] = Id(7), ["name"] = "Slot in main", ["database"] = Id(5),
            ["entity"] = Id(2),
        }));
        repo.Write(Model + "databases/main/tables/slots.json", Json(new JsonObject
        {
            ["$schema"] = "../../../../.schema/v1/table.json",
            ["kind"] = "table",
            ["id"] = Id(8),
            ["database"] = Id(5),
            ["origin"] = "synthesized",
            ["entity"] = Id(2),
            ["columns"] = new JsonArray(
                new JsonObject { ["id"] = Id(9), ["name"] = "seats", ["type"] = "range", ["subtype"] = "int32" },
                new JsonObject { ["id"] = Id(10), ["name"] = "days", ["type"] = "multirange", ["subtype"] = "date", ["nullable"] = false }),
        }));
        return repo;
    }

    [Fact]
    public async Task Repositories_read_and_write_range_columns_as_text_with_postgresql_casts()
    {
        using var repo = Create("postgresql");
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "range-columns"), repo.PathOf("src/Generated"));

        var repository = repo.Read("src/Generated/Booking/SlotRepository.g.cs");
        Assert.Contains("seats::text AS Seats,\n        days::text AS Days\n", repository, StringComparison.Ordinal);
        Assert.Contains("VALUES (@Id, @Name, CAST(@Seats AS int4range), CAST(@Days AS datemultirange))", repository, StringComparison.Ordinal);
        Assert.Contains("SET name = @Name, seats = CAST(@Seats AS int4range), days = CAST(@Days AS datemultirange)", repository, StringComparison.Ordinal);
        Assert.Contains("public string? Seats { get; set; }", repository, StringComparison.Ordinal);
        Assert.Contains("public string Days { get; set; } = default!;", repository, StringComparison.Ordinal);
        var entity = repo.Read("src/Generated/Booking/Slot.g.cs");
        Assert.Contains("public string? Seats { get; set; }", entity, StringComparison.Ordinal);
        Assert.Contains("public string Days { get; set; } = default!;", entity, StringComparison.Ordinal);

        // Elsewhere a range column is text already: no casts.
        using var sqlite = Create("sqlite");
        await sqlite.GenerateCleanlyAsync();
        var plain = sqlite.Read("src/Generated/Booking/SlotRepository.g.cs");
        Assert.DoesNotContain("::text", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("CAST(", plain, StringComparison.Ordinal);
        Assert.Contains("public string? Seats { get; set; }", plain, StringComparison.Ordinal);
    }
}
