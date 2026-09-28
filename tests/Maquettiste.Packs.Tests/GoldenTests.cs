using System.Globalization;
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
