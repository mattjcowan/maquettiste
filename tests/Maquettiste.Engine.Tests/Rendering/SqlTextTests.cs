using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>
/// The SQL text scanner (sql-queries-design.md decisions 4 and 6) against the shared vectors (<c>tests/fixtures/sql-text/scanner.json</c>),
/// which the editor's copy of the scanner reads too, so the two can't drift.
/// </summary>
public sealed class SqlTextTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors()["cases"]!.AsArray())
            data.Add((string)c!["name"]!);
        return data;
    }

    private static JsonObject Vectors() => JsonNode.Parse(File.ReadAllText(Fixtures.Path("sql-text", "scanner.json")))!.AsObject();

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_scanner_answers_the_shared_vector(string name)
    {
        var c = Vectors()["cases"]!.AsArray().Single(x => (string)x!["name"]! == name)!.AsObject();
        Assert.True(SqlDialects.TryParse((string)c["dialect"]!, out var dialect));
        var text = (string)c["text"]!;
        var scan = SqlText.Scan(text, dialect);

        var expected = c["placeholders"]!.AsArray().Select(p => ((string)p!["name"]!, (int)p["offset"]!, (int)p["line"]!, (int)p["column"]!)).ToList();
        var actual = scan.Placeholders.Select(p =>
        {
            var (line, column) = SqlText.Position(text, p.Offset);
            return (p.Name, p.Offset, line, column);
        }).ToList();
        Assert.Equal(expected, actual);
        Assert.All(scan.Placeholders, p => Assert.Equal(":" + p.Name, text.Substring(p.Offset, p.Length)));
        Assert.Equal((string?)c["firstWord"], scan.FirstWord);
        Assert.Equal((string?)c["verb"], scan.Verb);
        if (c["names"] is JsonArray names)
            Assert.Equal(names.Select(n => (string)n!), scan.Names);
        Assert.Equal((bool)c["orderBy"]!, scan.OrderBy);
        Assert.Equal((bool)c["offsetFetchOrTop"]!, scan.OffsetFetchOrTop);
        if (c.ContainsKey("finalSemicolon"))
            Assert.Equal((int?)c["finalSemicolon"], scan.FinalSemicolon);
    }

    [Fact]
    public void The_vectors_cover_every_dialect_and_the_with_verbs()
    {
        var cases = Vectors()["cases"]!.AsArray().Select(c => c!.AsObject()).ToList();
        Assert.Equal(cases.Count, cases.Select(c => (string)c["name"]!).Distinct(StringComparer.Ordinal).Count());
        foreach (var dialect in Enum.GetValues<Dialect>())
        {
            var key = dialect switch
            {
                Dialect.PostgreSql => "postgresql",
                Dialect.SqlServer => "sqlserver",
                Dialect.MySql => "mysql",
                Dialect.Sqlite => "sqlite",
                _ => "oracle",
            };
            Assert.Contains(cases, c => (string)c["dialect"]! == key);
        }

        var withVerbs = cases.Where(c => (string?)c["firstWord"] == "WITH").Select(c => (string?)c["verb"]).ToHashSet();
        Assert.Superset(new HashSet<string?> { "SELECT", "UPDATE", "DELETE", "INSERT", "MERGE", null }, withVerbs);
    }

    [Fact]
    public void Names_are_found_ignoring_case()
    {
        var scan = SqlText.Scan("SELECT * FROM billing.Invoices", Dialect.PostgreSql);
        Assert.True(scan.Mentions("invoices"));
        Assert.False(scan.Mentions("customers"));
    }

    [Fact]
    public void Position_counts_lines_and_columns_from_one()
    {
        Assert.Equal((1, 1), SqlText.Position("abc", 0));
        Assert.Equal((2, 3), SqlText.Position("ab\ncd", 5));
    }
}
