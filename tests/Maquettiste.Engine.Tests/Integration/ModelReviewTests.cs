using Maquettiste.Engine.Tests.Snapshots;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance of next-phase-2-design.md section 6.2: a reviewer reads a change by package and by member, from the snapshot taken
/// before it or from a folder that holds the base (a CI checkout stands in), with the migration facts in their own group.
/// </summary>
public sealed class ModelReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_review_of_a_rename_and_a_new_table_reads_by_package()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("before-review"), Ct);
        Assert.StartsWith("before-review-", snapshot.Id, StringComparison.Ordinal);
        var folder = ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "base"));
        await ModelDiffTests.ReviewChangeAsync(f.Store);
        var review = new SnapshotCompareOptions { ByPackage = true, FieldChanges = true };

        var comparison = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, SnapshotLibrary.MaxCompareLimit, review, Ct))!;
        var markdown = ModelDiffFormat.Markdown(comparison);

        var table = Assert.Single(comparison.Elements, e => e.Kind == "table").Id;
        var fact = Assert.Single(comparison.Elements, e => e.Kind == "migration").Id;
        Assert.Equal($$"""
            # Model diff: `{{snapshot.Id}}` to `working`

            2 added, 0 removed, 1 changed.

            ## Billing

            - changed entity `Invoice` (`{{ModelDiffTests.Invoice}}`)
              - attribute `code`, `/name` from `"number"` to `"code"`
            - added table `payment_method` (`{{table}}`)

            ## migrations of main

            - added migration `rename column billing.invoices.number` (`{{fact}}`)

            """.ReplaceLineEndings("\n"), markdown);

        // From the folder that holds the base: the same lines after the heading.
        var fromFolder = (await f.Library.CompareAsync("./base", SnapshotLibrary.Working, 0, SnapshotLibrary.MaxCompareLimit,
            review with { AllowFolders = true, CurrentDirectory = f.Repo.Root }, Ct))!;
        var lines = ModelDiffFormat.Markdown(fromFolder).Split('\n');
        Assert.Equal($"# Model diff: `./base` to `working`", lines[0]);
        Assert.Equal(markdown.Split('\n')[1..], lines[1..]);
        Assert.Equal(folder, Path.Combine(f.Repo.Root, "base"));
    }
}
