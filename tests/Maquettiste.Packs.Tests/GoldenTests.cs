using System.Globalization;
using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Byte-exact golden output of both example packs over the billing fixture, under <c>tests/fixtures/golden/&lt;pack&gt;/</c>.
/// Set <c>MAQUETTISTE_UPDATE_GOLDEN=1</c> to rewrite the goldens from the current output, then review the diff.
/// </summary>
public sealed class GoldenTests
{
    [Fact]
    public async Task Sql_ddl_over_the_billing_fixture_matches_the_golden_tree()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "billing"), repo.PathOf("db"));
    }

    [Fact]
    public async Task Sql_ddl_object_scripts_are_off_by_default_and_match_the_golden_tree_when_on()
    {
        using (var off = PackRepo.Billing())
        {
            await off.GenerateCleanlyAsync(packs: ["sql-ddl"]);
            Assert.False(Directory.Exists(off.PathOf("db/main/billing/views")), "the view unit wrote files without objectScripts");
            Assert.False(Directory.Exists(off.PathOf("db/main/billing/sequences")), "the sequence unit wrote files without objectScripts");
        }

        // One script per view and per sequence of the billing fixture (its view and its sequence file); everything else is unchanged.
        using var repo = PackRepo.Billing();
        repo.EditJson(".maquettiste/maquettiste.json", settings => settings["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["objectScripts"] = true });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "objects", "main", "billing", "views"), repo.PathOf("db/main/billing/views"));
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "objects", "main", "billing", "sequences"), repo.PathOf("db/main/billing/sequences"));
        Assert.Empty(Golden.Compare(Fixtures.Path("golden", "sql-ddl", "billing", "main", "billing", "tables"), repo.PathOf("db/main/billing/tables")));
    }

    [Fact]
    public async Task Sql_ddl_writes_one_script_per_routine_database_type_and_sql_object_only_when_object_scripts_is_on()
    {
        // Each database of the dialects fixture has a routine, a domain type and a trigger (PackRepo.AddDatabaseObjects).
        string[] folders =
        [
            "main/billing/routines", "main/billing/types", "main/billing/objects", "reporting/dbo/routines", "reporting/dbo/types", "reporting/dbo/objects",
            "local/routines", "local/types", "local/objects",
        ];
        using (var off = PackRepo.BillingDialects())
        {
            await off.GenerateCleanlyAsync(packs: ["sql-ddl"]);
            foreach (var folder in folders)
                Assert.False(Directory.Exists(off.PathOf("db/" + folder)), $"db/{folder} was written without objectScripts");
        }

        using var repo = PackRepo.BillingDialects();
        repo.EditJson(".maquettiste/maquettiste.json", settings => settings["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["objectScripts"] = true });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        foreach (var folder in folders)
            Golden.AssertMatches(Fixtures.Path(["golden", "sql-ddl", "objects-dialects", .. folder.Split('/')]), repo.PathOf("db/" + folder));
        Assert.Equal(File.ReadAllText(Fixtures.Path("golden", "sql-ddl", "billing-dialects", "main", "schema.sql")), repo.Read("db/main/schema.sql"));
    }

    [Fact]
    public async Task Csharp_dapper_over_the_billing_fixture_matches_the_golden_tree()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "billing"), repo.PathOf("src/Generated"));
    }

    [Fact]
    public async Task Sql_ddl_for_postgresql_sql_server_and_sqlite_matches_the_golden_tree()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"], jobs: 1);
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "billing-dialects"), repo.PathOf("db"));
    }

    [Fact]
    public async Task Csharp_dapper_over_a_sqlite_database_matches_the_golden_tree()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"], jobs: 8);
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "billing-dialects"), repo.PathOf("src/Generated"));
    }

    [Fact]
    public void Golden_trees_hold_lf_line_endings_only()
    {
        foreach (var file in Directory.EnumerateFiles(Fixtures.Path("golden"), "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == ".gitattributes")
                continue;
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(file));
        }
    }
}

/// <summary>Tests that change the process culture; they must not run next to other tests.</summary>
[CollectionDefinition(nameof(CultureCollection), DisableParallelization = true)]
public sealed class CultureCollection;

/// <summary>Both packs give the same bytes whatever the culture of the generating process.</summary>
[Collection(nameof(CultureCollection))]
public sealed class CultureTests
{
    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    public async Task Output_does_not_depend_on_the_process_culture(string culture)
    {
        var (current, ui, threadDefault, threadUiDefault) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            var info = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = info;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = info;
            using var repo = PackRepo.BillingDialects();
            await repo.GenerateCleanlyAsync(jobs: 4);
            Assert.Empty(Golden.Compare(Fixtures.Path("golden", "sql-ddl", "billing-dialects"), repo.PathOf("db")));
            Assert.Empty(Golden.Compare(Fixtures.Path("golden", "csharp-dapper", "billing-dialects"), repo.PathOf("src/Generated")));
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.DefaultThreadCurrentCulture = threadDefault;
            CultureInfo.DefaultThreadCurrentUICulture = threadUiDefault;
        }
    }
}
