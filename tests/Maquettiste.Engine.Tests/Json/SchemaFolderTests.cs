using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Json;

/// <summary><see cref="SchemaFolder"/>: the <c>.schema/v1/</c> copies compared with and refreshed from the embedded schemas.</summary>
public sealed class SchemaFolderTests
{
    private const string Folder = ".maquettiste/.schema/v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<string> Names => TestServices.Schemas.FileNames;

    private static void WriteCurrent(TempRepo repo)
    {
        foreach (var name in Names)
            repo.WriteBytes(Folder + "/" + name, TestServices.Schemas.GetFileBytes(name).ToArray());
    }

    private static void AssertCurrentOnDisk(TempRepo repo)
    {
        foreach (var name in Names)
            Assert.Equal(TestServices.Schemas.GetFileBytes(name).ToArray(), File.ReadAllBytes(repo.PathOf(Folder + "/" + name)));
        Assert.Equal(Names.Count, Directory.GetFiles(repo.PathOf(Folder)).Length);
    }

    [Fact]
    public void A_missing_folder_is_every_schema_missing()
    {
        using var repo = new TempRepo();
        var status = SchemaFolder.Status(repo.Options);
        Assert.False(status.IsCurrent);
        Assert.Equal(Names, status.Missing);
        Assert.Empty(status.Stale);
        Assert.Empty(status.Extra);
    }

    [Fact]
    public void A_current_folder_is_current()
    {
        using var repo = new TempRepo();
        WriteCurrent(repo);
        var status = SchemaFolder.Status(repo.Options);
        Assert.True(status.IsCurrent);
        Assert.Empty(status.Differing);
        Assert.Equal("", status.Describe());
        Assert.Empty(SchemaFolder.Findings(repo.Options, null));
    }

    [Fact]
    public void Status_names_missing_stale_and_extra_files()
    {
        using var repo = new TempRepo();
        WriteCurrent(repo);
        File.Delete(repo.PathOf(Folder + "/table.json"));
        repo.WriteFile(Folder + "/entity.json", "{}\n");
        repo.WriteFile(Folder + "/retired.json", "{}\n");
        repo.WriteFile(Folder + "/notes.txt", "not a schema\n");

        var status = SchemaFolder.Status(repo.Options);
        Assert.Equal(["table.json"], status.Missing);
        Assert.Equal(["entity.json"], status.Stale);
        Assert.Equal(["retired.json"], status.Extra);
        Assert.Equal(["entity.json", "retired.json", "table.json"], status.Differing);
        Assert.Equal("wrote entity.json, table.json; removed retired.json", status.Describe());
    }

    [Fact]
    public async Task Refresh_writes_a_missing_folder()
    {
        using var repo = new TempRepo();
        var before = await SchemaFolder.RefreshAsync(repo.Options, Ct);
        Assert.Equal(Names, before.Missing);
        AssertCurrentOnDisk(repo);
        Assert.True(SchemaFolder.Status(repo.Options).IsCurrent);
    }

    [Fact]
    public async Task Refresh_rewrites_stale_and_missing_files_and_deletes_extra_ones()
    {
        using var repo = new TempRepo();
        WriteCurrent(repo);
        File.Delete(repo.PathOf(Folder + "/table.json"));
        repo.WriteFile(Folder + "/entity.json", "{}\n");
        repo.WriteFile(Folder + "/retired.json", "{}\n");
        repo.WriteFile(Folder + "/notes.txt", "kept\n");

        var before = await SchemaFolder.RefreshAsync(repo.Options, Ct);
        Assert.Equal(["entity.json", "retired.json", "table.json"], before.Differing);
        Assert.False(repo.Exists(Folder + "/retired.json"));
        Assert.Equal("kept\n", repo.ReadFile(Folder + "/notes.txt"));
        foreach (var name in Names)
            Assert.Equal(TestServices.Schemas.GetFileBytes(name).ToArray(), File.ReadAllBytes(repo.PathOf(Folder + "/" + name)));
        Assert.True(SchemaFolder.Status(repo.Options).IsCurrent);
        Assert.Empty(Directory.GetFiles(repo.PathOf(Folder), "*.tmp"));
    }

    [Fact]
    public async Task Refresh_leaves_a_current_folder_untouched()
    {
        using var repo = new TempRepo();
        WriteCurrent(repo);
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        foreach (var name in Names)
            File.SetLastWriteTimeUtc(repo.PathOf(Folder + "/" + name), stamp);

        var before = await SchemaFolder.RefreshAsync(repo.Options, Ct);
        Assert.True(before.IsCurrent);
        foreach (var name in Names)
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(repo.PathOf(Folder + "/" + name)));
    }

    [Fact]
    public void The_MQ1008_finding_names_five_files_then_counts_the_rest_and_says_how_to_refresh()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".maquettiste/maquettiste.json", "{\n  \"formatVersion\": 1,\n  \"name\": \"t\"\n}\n");
        WriteCurrent(repo);
        repo.WriteFile(Folder + "/entity.json", "{}\n");
        repo.WriteFile(Folder + "/zz-retired.json", "{}\n");
        foreach (var name in new[] { "table.json", "enum.json", "view.json", "seed.json", "relation.json" })
            File.Delete(repo.PathOf(Folder + "/" + name));

        var finding = Assert.Single(SchemaFolder.Findings(repo.Options, null));
        Assert.Equal("MQ1008", finding.Rule);
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Equal(".maquettiste/maquettiste.json", finding.FilePath);
        Assert.Null(finding.JsonPointer);
        Assert.Null(finding.ElementId);
        Assert.Equal(
            "The schema copies in .maquettiste/.schema/v1/ differ from the ones this version ships: entity.json (out of date), enum.json (missing), " +
            "relation.json (missing), seed.json (missing), table.json (missing), and 2 more. Run 'maquettiste init', or start the editor, to refresh " +
            "them; IDEs check model files against these copies.", finding.Message);
    }

    [Fact]
    public void The_MQ1008_severity_follows_validation_rules_read_from_the_settings_file()
    {
        using var repo = new TempRepo();
        repo.WriteFile(".maquettiste/maquettiste.json",
            "{\n  \"formatVersion\": 1,\n  \"name\": \"t\",\n  \"validation\": {\n    \"rules\": {\n      \"MQ1008\": \"error\"\n    }\n  }\n}\n");
        var finding = Assert.Single(SchemaFolder.Findings(repo.Options, null));
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
    }
}
