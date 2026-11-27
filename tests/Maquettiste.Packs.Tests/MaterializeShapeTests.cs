using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// "Create tables…" (materialize-tables) proposes tables with its own designer, outside resolution (retire-projection-design.md section
/// 7). These tests prove the designer gives exactly the projection's shapes: every projected entity of a model is stored as designed
/// tables with bindings, in the default shapes (the shape an older mapping or the conventions give), and the sql-ddl pack then writes the
/// same scripts byte for byte and no migration. The write's rekey fact is left out (<see cref="MaterializeProof"/>): it only adds ledger
/// rows, and without it the snapshot's aliases alone must make the next migration empty.
/// </summary>
public sealed class MaterializeShapeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// No error, no fact but the rekey, nothing still projected; the run after the conversion writes one migration per database that holds
    /// only its rekey fact's ledger rows (no schema statement); with that fact rendered, no further migration; and every script equal to
    /// the one before but for the differences <see cref="MaterializeProofCheck"/> names, each matched to what the pack wrote.
    /// </summary>
    internal static void AssertSameDdl(MaterializeProofResult proof)
    {
        Assert.True(proof.Errors.Count == 0, proof.Describe());
        Assert.True(proof.OtherFacts.Count == 0, string.Join("\n", proof.OtherFacts));
        Assert.True(proof.Projected.Count == 0, "Still projected: " + string.Join(", ", proof.Projected));
        Assert.All(proof.RekeyMigrations, m => Assert.True(MaterializeAdoptionTests.SchemaLines(m.Value).Count == 0, m.Key + ":\n" + string.Join("\n", MaterializeAdoptionTests.SchemaLines(m.Value))));
        Assert.Equal(proof.MigrationsBefore, proof.MigrationsAfter);
        var problems = MaterializeProofCheck.Problems(proof);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The check is exact: a dropped row, a dropped table or a dropped script fails the proof.</summary>
    [Fact]
    public async Task The_proof_fails_on_a_dropped_row_table_or_script()
    {
        using var temp = new TempRepo();
        var sample = Path.Combine(Fixtures.RepoRoot, "samples", "reference-app");
        PackRepo.CopyTree(Path.Combine(sample, ".maquettiste"), temp.ModelRoot);
        PackRepo.CopyTree(Path.Combine(sample, "db"), Path.Combine(temp.RepoRoot, "db"));
        var proof = await MaterializeProof.RunAsync(temp.Options, "db", Ct);
        Assert.Empty(MaterializeProofCheck.Problems(proof));

        MaterializeProofResult With(string path, string? text)
        {
            var after = proof.After.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (text is null)
                after.Remove(path);
            else
                after[path] = text;
            return proof with { After = after };
        }

        var seed = proof.After["db/main/seed.sql"];
        var row = seed.Split('\n').First(l => l.StartsWith("    ('ACTIVE'", StringComparison.Ordinal));
        Assert.NotEmpty(MaterializeProofCheck.Problems(With("db/main/seed.sql", seed.Replace(row + "\n", "", StringComparison.Ordinal))));
        var schema = proof.After["db/main/schema.sql"];
        var start = schema.IndexOf("CREATE TABLE northwind.shipments (", StringComparison.Ordinal);
        var end = schema.IndexOf(");", start, StringComparison.Ordinal) + 2;
        Assert.NotEmpty(MaterializeProofCheck.Problems(With("db/main/schema.sql", schema.Remove(start, end - start))));
        Assert.NotEmpty(MaterializeProofCheck.Problems(With("db/main/northwind/tables/shipments.sql", null)));
        var lookup = proof.After["db/main/northwind/tables/product_statuses.sql"];
        Assert.NotEmpty(MaterializeProofCheck.Problems(With("db/main/northwind/tables/product_statuses.sql", lookup.Replace("    label varchar(64) NOT NULL,\n", "", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task The_billing_model_keeps_its_ddl_when_every_entity_gets_a_designed_table()
    {
        using var repo = PackRepo.Billing();

        var proof = await MaterializeProof.RunAsync(repo.Repo.Options, "db", Ct);

        Assert.NotEmpty(proof.Materialized);
        AssertSameDdl(proof);
    }

    /// <summary>
    /// The dialects variant adds a SQL Server and a SQLite database, a reference type and CreditNote derived from Invoice. The
    /// conventions' inheritance gives the hierarchy's default shape, so each of the three shapes is proved against the projection's.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("tpt")]
    [InlineData("tpc")]
    public async Task The_billing_model_in_every_dialect_keeps_its_ddl_in_each_hierarchy_shape(string? inheritance)
    {
        using var repo = PackRepo.BillingDialects();
        if (inheritance is not null)
            repo.EditJson(".maquettiste/maquettiste.json", s => s["conventions"]!["inheritance"] = inheritance);

        var proof = await MaterializeProof.RunAsync(repo.Repo.Options, "db", Ct);

        Assert.Equal(3, proof.Materialized.Count);
        AssertSameDdl(proof);
    }

    /// <summary>
    /// A list of a reference type had no column: the sql-ddl pack made its table in seed.sql (<c>CREATE TABLE IF NOT EXISTS</c>,
    /// outside the snapshot). Stored, it is a designed table of the same shape (its owner columns, its code column, the same key and
    /// owner foreign key), bound by a child table entry, so its DDL moves from seed.sql into the table scripts.
    /// </summary>
    [Theory]
    [InlineData("lookup-table")]
    [InlineData("check")]
    public async Task A_list_of_a_reference_type_becomes_a_designed_table_with_the_shape_the_pack_gave_it(string strategy)
    {
        using var repo = PackRepo.ReferenceData(strategy);

        var proof = await MaterializeProof.RunAsync(repo.Repo.Options, "db", Ct);

        Assert.True(proof.Errors.Count == 0, proof.Describe());
        Assert.True(proof.OtherFacts.Count == 0, string.Join("\n", proof.OtherFacts));
        Assert.True(proof.Projected.Count == 0, "Still projected: " + string.Join(", ", proof.Projected));
        // The database already holds the tables (seed.sql made them), so the snapshot takes them in and no migration creates them.
        Assert.Equal(proof.MigrationsBefore, proof.MigrationsAfter);
        var main = proof.After["db/main/schema.sql"];
        foreach (var name in new[] { "ingredients_allergens", "ingredients_pack_units" })
        {
            // The same CREATE TABLE body as the pack's, now in the schema script instead of seed.sql; a list whose strategy keeps
            // no list in a table (check) had none and gets none.
            if (!proof.Before["db/main/seed.sql"].Contains("CREATE TABLE IF NOT EXISTS public." + name + " (", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("public." + name + " (", main, StringComparison.Ordinal);
                continue;
            }

            var before = Body(proof.Before["db/main/seed.sql"], "CREATE TABLE IF NOT EXISTS public." + name + " (");
            // The code column's constraint, which seed.sql added with ALTER TABLE, is now declared in the table.
            Assert.Equal(before.TrimEnd(), string.Join("\n", Body(main, "CREATE TABLE public." + name + " (").Split('\n').Where(l => !(l.TrimStart().StartsWith("CONSTRAINT ", StringComparison.Ordinal) && l.Contains("_ref ", StringComparison.Ordinal)))).TrimEnd().TrimEnd(','));
            Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS public." + name, proof.After["db/main/seed.sql"], StringComparison.Ordinal);
        }

        AssertSameDdl(proof);
    }

    private static string Body(string script, string start)
    {
        var at = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, start);
        var end = script.IndexOf(");", at, StringComparison.Ordinal);
        return script[(at + start.Length)..end];
    }

    [Fact]
    public async Task The_reference_sample_keeps_its_ddl_when_every_entity_gets_a_designed_table()
    {
        // About 200 projected entities: one table for a hierarchy, a table per type, a child table, a list in a JSON column, overlays.
        using var temp = new TempRepo();
        var sample = Path.Combine(Fixtures.RepoRoot, "samples", "reference-app");
        PackRepo.CopyTree(Path.Combine(sample, ".maquettiste"), temp.ModelRoot);
        PackRepo.CopyTree(Path.Combine(sample, "db"), Path.Combine(temp.RepoRoot, "db"));

        var proof = await MaterializeProof.RunAsync(temp.Options, "db", Ct);

        Assert.True(proof.Materialized["main"] > 150);
        AssertSameDdl(proof);
    }
}
