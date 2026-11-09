using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// sql-ddl over the several-types storage model (<see cref="TypeStorageModel"/>; several-types-design.md sections 19.9 and 19.16, cut 1 of
/// question 10): byte-exact goldens under <c>tests/fixtures/golden/sql-ddl/type-storage</c> of every way of section 19.4 in the
/// <c>postgresql</c> dialect, the create script and the migration that adds a type's column and drops another's; and in the other four dialects
/// only the tables whose output differs (the team's checks, uniques and the filtered unique index). No template changes: the DDL is the
/// designed tables' own. Each golden runs with the relation mappings and without them; the run with them waits for the storage run, after
/// which a link table a mapping names also gets the "Junction table." comment (section 19.8): those goldens, under <c>&lt;dialect&gt;-mapped</c>,
/// are recorded then and should differ from the unmapped ones by those comments only.
/// </summary>
public sealed class TypeStorageDdlTests
{
    private const string Waits = "several-types-2: needs the storage run";

    [Theory]
    [InlineData(false)]
    [InlineData(true, Skip = Waits)]
    public async Task Every_way_in_postgresql_creates_and_migrates_as_its_tables_say(bool relationMappings)
    {
        using var repo = TypeStorageModel.Create("postgresql", relationMappings);
        var golden = Fixtures.Path("golden", "sql-ddl", "type-storage", Folder("postgresql", relationMappings));
        var first = await repo.GenerateCleanlyAsync();
        Assert.DoesNotContain(first.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Rule is "MQ4120");
        Golden.AssertMatches(Path.Combine(golden, "step1"), repo.PathOf("db"));
        var create = Read(repo, "db");
        Assert.Contains("CONSTRAINT ck_comments_attached_type CHECK (attached_type IN ('Assembly', 'Part'))", create, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT uq_links UNIQUE (relation_code, from_type, from_id, to_type, to_id)", create, StringComparison.Ordinal);

        TypeStorageModel.Write(repo, "postgresql", relationMappings, step: 2);
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(golden, "step2"), repo.PathOf("db"));
        var migration = Directory.EnumerateFiles(repo.PathOf("db"), "*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(File.ReadAllText).Last(t => t.Contains("tool_id", StringComparison.Ordinal));
        Assert.Contains("tool_id", migration, StringComparison.Ordinal);
        Assert.Contains("assembly_id", migration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlserver", false)]
    [InlineData("mysql", false)]
    [InlineData("oracle", false)]
    [InlineData("sqlite", false)]
    [InlineData("sqlserver", true, Skip = Waits)]
    [InlineData("mysql", true, Skip = Waits)]
    [InlineData("oracle", true, Skip = Waits)]
    [InlineData("sqlite", true, Skip = Waits)]
    public async Task The_ways_whose_output_differs_create_in_each_other_dialect(string dialect, bool relationMappings)
    {
        using var repo = TypeStorageModel.Create(dialect, relationMappings);
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "type-storage", Folder(dialect, relationMappings)), repo.PathOf("db"));
    }

    /// <summary>The golden folder: with the relation mappings, a link table a mapping names gets the "Junction table." comment, so it has its own.</summary>
    private static string Folder(string dialect, bool relationMappings) => relationMappings ? dialect + "-mapped" : dialect;

    private static string Read(PackRepo repo, string folder) =>
        string.Concat(Directory.EnumerateFiles(repo.PathOf(folder), "*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(File.ReadAllText));
}
