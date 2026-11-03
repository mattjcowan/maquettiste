using System.Text.RegularExpressions;
using Maquettiste.Engine.Tests.Snapshots;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Nothing in the engine reads version control (next-phase-1-design.md section 3.1, decision 7): baselines are model snapshots or
/// folders, and the workspace name is data the hosts pass in.
/// </summary>
public sealed partial class NoVersionControlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Baselines_load_from_snapshots_and_folders_with_no_version_control()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        for (var dir = new DirectoryInfo(f.Repo.Root); dir is not null; dir = dir.Parent)
            Assert.False(Path.Exists(Path.Combine(dir.FullName, ".git")), $"{dir.FullName} holds a .git: the test needs temporary folders outside any checkout.");
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        var folder = ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.Root, "base"));
        var working = (await f.Store.GetSnapshotAsync(Ct)).Documents.Select(d => d.Element.Id + " " + d.Hash).Order(StringComparer.Ordinal).ToList();

        var clean = await ReadAsync(new SnapshotLibrary(f.Store));
        Assert.Equal(working, clean.Snapshot);
        Assert.Equal(working, clean.Folder);
        Assert.Equal(clean.SnapshotHash, clean.FolderHash);

        // A .git folder with a corrupt HEAD in the working copy, and a .git file pointing nowhere in the folder, change nothing.
        f.Repo.WriteBytes(".git/HEAD", [0xFF, 0xFE, 0x00, (byte)'r', (byte)'e', (byte)'f', (byte)':', 0x01]);
        File.WriteAllText(Path.Combine(folder, ".git"), "gitdir: \0/nowhere/.git/worktrees/\n");
        var dirty = await ReadAsync(new SnapshotLibrary(f.Store));
        Assert.Equal((clean.SnapshotHash, clean.FolderHash), (dirty.SnapshotHash, dirty.FolderHash));
        Assert.Equal(clean.Snapshot, dirty.Snapshot);
        Assert.Equal(clean.Folder, dirty.Folder);

        async Task<Read> ReadAsync(SnapshotLibrary library)
        {
            using var fromSnapshot = await ModelBaseline.OpenAsync(f.Store, snapshot.Id, f.Repo.RepoRoot, true, library, Ct);
            using var fromFolder = await ModelBaseline.OpenAsync(f.Store, folder, f.Repo.RepoRoot, true, library, Ct);
            return new Read(fromSnapshot.ModelHash, await HashesAsync(fromSnapshot), fromFolder.ModelHash, await HashesAsync(fromFolder));
        }

        static async Task<List<string>> HashesAsync(ModelBaseline baseline) =>
            (await baseline.Store.GetSnapshotAsync(Ct)).Documents.Select(d => d.Element.Id + " " + d.Hash).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Engine_source_reads_no_version_control()
    {
        var engine = Path.Combine(Fixtures.RepoRoot, "src", "Maquettiste.Engine");
        string[] literals = ["\".git\"", "\"HEAD\"", "\"gitdir:\"", "\"git\""];
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(engine, file).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            scanned++;
            if (relative == "Writing/OutputPathPolicy.cs")
                continue; // the write guard: it refuses writes under .git and lets init --hooks write two hook files, reading nothing
            var text = File.ReadAllText(file);
            offenders.AddRange(literals.Where(l => text.Contains(l, StringComparison.Ordinal)).Select(l => $"{relative}: {l}"));
            if (relative != "PostProcessing/FormatterRunner.cs") // the project's own formatters
                offenders.AddRange(ProcessStart().Matches(text).Select(m => $"{relative}: {m.Value}"));
        }

        var project = File.ReadAllText(Path.Combine(engine, "Maquettiste.Engine.csproj"));
        offenders.AddRange(PackageReference().Matches(project).Select(m => m.Groups[1].Value)
            .Where(name => name.Contains("git", StringComparison.OrdinalIgnoreCase)).Select(name => "Maquettiste.Engine.csproj: " + name));

        Assert.True(scanned > 100, $"only {scanned} files scanned under {engine}");
        Assert.Empty(offenders);
    }

    /// <summary>Starting a process: <c>Process.Start</c>, <c>new Process(</c>, <c>ProcessStartInfo</c> (the model kind is not matched).</summary>
    [GeneratedRegex(@"\bProcess\.Start\b|\bnew\s+Process\s*\(|\bProcessStartInfo\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessStart();

    [GeneratedRegex("<PackageReference\\s+Include=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex PackageReference();

    private sealed record Read(string SnapshotHash, List<string> Snapshot, string FolderHash, List<string> Folder);
}
