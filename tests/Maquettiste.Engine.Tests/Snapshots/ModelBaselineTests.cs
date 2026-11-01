using System.IO.Compression;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Snapshots;

/// <summary>Baselines (next-phase-1-design.md section 3.1): another model to compare with, from a model snapshot or a folder.</summary>
public sealed class ModelBaselineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_snapshot_id_opens_a_read_only_baseline()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        await f.RenameAsync("Customer", "Client");

        using var baseline = await ModelBaseline.OpenAsync(f.Store, snapshot.Id, f.Repo.Root, allowFolders: false, f.Library, Ct);

        Assert.Equal(BaselineKind.Snapshot, baseline.Kind);
        Assert.Equal((snapshot.Id, "snapshot " + snapshot.Id), (baseline.Against, baseline.Label));
        Assert.Equal(snapshot.ModelHash, baseline.ModelHash);
        Assert.True(baseline.Store.IsReadOnly);
        var model = await baseline.Store.GetSnapshotAsync(Ct);
        Assert.Contains(model.Summaries(), s => s.Name == "Customer");
        Assert.DoesNotContain(model.Summaries(), s => s.Name == "Client");

        // Without the host's library, one is built for the call.
        using (var again = await ModelBaseline.OpenAsync(f.Store, snapshot.Id, f.Repo.Root, allowFolders: false, null, Ct))
        {
            Assert.Equal((BaselineKind.Snapshot, snapshot.ModelHash), (again.Kind, again.ModelHash));
            Assert.True(again.Store.IsReadOnly);
        }

        // A snapshot taken at a newer model format is refused with MQ1007's text, as an import refuses it.
        using (var zip = ZipFile.Open(Path.Combine(f.Library.Folder, snapshot.Id + ".zip"), ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("snapshot.json")!;
            string text;
            using (var reader = new StreamReader(entry.Open()))
                text = reader.ReadToEnd();
            Assert.Contains("\"modelFormat\": 1", text, StringComparison.Ordinal);
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("snapshot.json").Open());
            writer.Write(text.Replace("\"modelFormat\": 1", "\"modelFormat\": 99", StringComparison.Ordinal));
        }

        var newer = await Assert.ThrowsAsync<BadBaselineException>(
            () => ModelBaseline.OpenAsync(f.Store, snapshot.Id, f.Repo.Root, false, new SnapshotLibrary(f.Store), Ct));
        Assert.StartsWith("MQ1007 " + RuleCatalog.Get("MQ1007").Description.TrimEnd('.'), newer.Message, StringComparison.Ordinal);
        Assert.Contains("snapshot " + snapshot.Id, newer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_opens_a_read_only_baseline_that_refuses_writes()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        var folder = CopyBilling(Path.Combine(f.Repo.Root, "base"));
        await f.RenameAsync("Customer", "Client");

        var baseline = await ModelBaseline.OpenAsync(f.Store, folder, f.Repo.RepoRoot, allowFolders: true, f.Library, Ct);

        Assert.Equal(BaselineKind.Folder, baseline.Kind);
        Assert.Equal((folder, "folder " + folder), (baseline.Against, baseline.Label));
        Assert.Equal(snapshot.ModelHash, baseline.ModelHash); // the same documents give the same hash
        Assert.True(baseline.Store.IsReadOnly);
        var model = await baseline.Store.GetSnapshotAsync(Ct);
        Assert.Contains(model.Summaries(), s => s.Name == "Customer");
        Assert.DoesNotContain(model.Summaries(), s => s.Name == "Client");
        var invoice = model.Documents.First(d => d.Element.Name == "Invoice");
        var save = await baseline.Store.SaveAsync(invoice.Element.Id, Encoding.UTF8.GetBytes(invoice.Json.GetRawText()), invoice.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, save.Outcome);
        Assert.Equal("MQ6029", Assert.Single(save.Diagnostics).Rule);

        // The .maquettiste folder itself is the same baseline.
        using (var modelFolder = await ModelBaseline.OpenAsync(f.Store, Path.Combine(folder, ".maquettiste"), f.Repo.RepoRoot, true, f.Library, Ct))
        {
            Assert.Equal(baseline.ModelHash, modelFolder.ModelHash);
            Assert.Equal(model.Documents.Count, (await modelFolder.Store.GetSnapshotAsync(Ct)).Documents.Count);
        }

        // A folder baseline owns its store.
        baseline.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => baseline.Store.GetSnapshotAsync(Ct));

        // A host that takes snapshot ids only refuses the folder.
        var refused = await Assert.ThrowsAsync<BadBaselineException>(() => ModelBaseline.OpenAsync(f.Store, folder, f.Repo.RepoRoot, false, f.Library, Ct));
        Assert.Contains(folder, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_like_value_is_always_a_folder()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        var cwd = f.Repo.Root;
        var folder = CopyBilling(Path.Combine(cwd, snapshot.Id), name: "billing-folder");

        using (var bare = await ModelBaseline.OpenAsync(f.Store, snapshot.Id, cwd, true, f.Library, Ct))
            Assert.Equal(BaselineKind.Snapshot, bare.Kind); // the archive exists: a snapshot first
        foreach (var value in new[] { "./" + snapshot.Id, folder, Path.Combine("..", Path.GetFileName(cwd), snapshot.Id) })
        {
            using var baseline = await ModelBaseline.OpenAsync(f.Store, value, cwd, true, f.Library, Ct);
            Assert.Equal((BaselineKind.Folder, value, "folder " + folder), (baseline.Kind, baseline.Against, baseline.Label));
            Assert.Equal("billing-folder", (await baseline.Store.GetSnapshotAsync(Ct)).Settings.Name);
        }

        // A bare value that is no snapshot id is a folder when the directory exists.
        var other = CopyBilling(Path.Combine(cwd, "Other Copy"));
        using (var plain = await ModelBaseline.OpenAsync(f.Store, "Other Copy", cwd, true, f.Library, Ct))
            Assert.Equal((BaselineKind.Folder, "folder " + other), (plain.Kind, plain.Label));

        // A path-like value never falls back to the snapshot of the same name.
        Directory.Delete(folder, recursive: true);
        var missing = await Assert.ThrowsAsync<BadBaselineException>(() => ModelBaseline.OpenAsync(f.Store, "./" + snapshot.Id, cwd, true, f.Library, Ct));
        Assert.Contains("'./" + snapshot.Id + "'", missing.Message, StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<BadBaselineException>(() => ModelBaseline.OpenAsync(f.Store, "./" + snapshot.Id, cwd, false, f.Library, Ct));
        Assert.Contains("folder", refused.Message, StringComparison.Ordinal);

        // Both separators count on every platform; no snapshot id holds either.
        Assert.True(ModelBaseline.IsPathLike(@"base\copy"));
        Assert.True(ModelBaseline.IsPathLike("base/copy"));
        Assert.False(ModelBaseline.IsPathLike(snapshot.Id));
    }

    [Fact]
    public async Task A_folder_baseline_reads_its_own_settings()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var folder = CopyBilling(Path.Combine(f.Repo.Root, "base"), name: "billing-base");

        using (var baseline = await ModelBaseline.OpenAsync(f.Store, folder, f.Repo.RepoRoot, true, null, Ct))
        {
            Assert.Equal("billing-base", (await baseline.Store.GetSnapshotAsync(Ct)).Settings.Name);
            Assert.Equal("billing", (await f.Store.GetSnapshotAsync(Ct)).Settings.Name);
        }

        // A newer model format is refused with MQ1007's text; nothing is loaded.
        var settings = Path.Combine(folder, ".maquettiste", "maquettiste.json");
        File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"formatVersion\": 1", "\"formatVersion\": 99", StringComparison.Ordinal));
        var newer = await Assert.ThrowsAsync<BadBaselineException>(() => ModelBaseline.OpenAsync(f.Store, folder, f.Repo.RepoRoot, true, null, Ct));
        Assert.StartsWith("MQ1007 " + RuleCatalog.Get("MQ1007").Description.TrimEnd('.'), newer.Message, StringComparison.Ordinal);
        Assert.Contains("99", newer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_baseline_writes_nothing_into_its_folder_or_the_cache()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);
        var folder = CopyBilling(Path.Combine(f.Repo.Root, "base"));
        var before = Tree(f.Repo.Root);

        foreach (var value in new[] { folder, snapshot.Id })
        {
            using var baseline = await ModelBaseline.OpenAsync(f.Store, value, f.Repo.RepoRoot, true, new SnapshotLibrary(f.Store), Ct);
            var report = await baseline.Store.ValidateAsync(ValidationScope.All, Ct);
            Assert.Equal(0, report.Errors);
            var model = await baseline.Store.GetSnapshotAsync(Ct);
            var invoice = model.Documents.First(d => d.Element.Name == "Invoice");
            var save = await baseline.Store.SaveAsync(invoice.Element.Id, Encoding.UTF8.GetBytes(invoice.Json.GetRawText()), invoice.Hash, ChangeSource.Editor, Ct);
            Assert.Equal(SaveOutcome.Invalid, save.Outcome);
        }

        // Every file of the working copy, the baseline folder and the user cache (all under the root) is as it was.
        Assert.Equal(before, Tree(f.Repo.Root));

        // Nor does a folder baseline read the working copy's index cache (it shares its cache folder): a baseline file with a
        // cached path, length and modification time but other bytes loads its own bytes.
        var cached = Path.Combine(f.Repo.ModelRoot, "model", "entities", "invoice.json");
        var copy = Path.Combine(folder, ".maquettiste", "model", "entities", "invoice.json");
        var bytes = File.ReadAllBytes(copy);
        var other = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"name\": \"Invoice\"", "\"name\": \"Involce\"", StringComparison.Ordinal));
        Assert.Equal(bytes.Length, other.Length);
        Assert.NotEqual(bytes, other);
        File.WriteAllBytes(copy, other);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(cached));
        using (var own = await ModelBaseline.OpenAsync(f.Store, folder, f.Repo.RepoRoot, true, null, Ct))
        {
            var names = (await own.Store.GetSnapshotAsync(Ct)).Summaries().Select(s => s.Name).ToList();
            Assert.Contains("Involce", names);
            Assert.DoesNotContain("Invoice", names);
        }
    }

    [Fact]
    public async Task An_unknown_value_names_itself_in_the_error()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(f.Repo.Root, "empty"));
        // A folder that holds a maquettiste.json but is not a .maquettiste folder (as the settings schema's folder) is no project.
        var loose = Directory.CreateDirectory(Path.Combine(f.Repo.Root, "loose")).FullName;
        File.Copy(Path.Combine(f.Repo.ModelRoot, "maquettiste.json"), Path.Combine(loose, "maquettiste.json"));

        foreach (var (value, folders) in new[] { ("no-such-baseline", true), ("no-such-baseline", false), ("./nowhere", true), ("empty", true), ("loose", true) })
        {
            var e = await Assert.ThrowsAsync<BadBaselineException>(() => ModelBaseline.OpenAsync(f.Store, value, f.Repo.Root, folders, f.Library, Ct));
            Assert.Contains("'" + value + "'", e.Message, StringComparison.Ordinal);
            Assert.Equal("against", e.ParamName);
        }

        await Assert.ThrowsAsync<BadBaselineException>(() => ModelBaseline.OpenAsync(f.Store, " ", f.Repo.Root, true, f.Library, Ct));
    }

    [Fact]
    public async Task A_host_held_library_opens_a_snapshot_baseline_once()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Base"), Ct);

        var first = await ModelBaseline.OpenAsync(f.Store, snapshot.Id, f.Repo.Root, false, f.Library, Ct);
        using var second = await ModelBaseline.OpenAsync(f.Store, snapshot.Id, f.Repo.Root, false, f.Library, Ct);

        Assert.Same(first.Store, second.Store);
        first.Dispose(); // the library keeps its store
        Assert.NotEmpty((await second.Store.GetSnapshotAsync(Ct)).Documents);
        Assert.Same(first.Store, await f.Library.OpenAsync(snapshot.Id, Ct));

        using var own = await ModelBaseline.OpenAsync(f.Store, snapshot.Id, f.Repo.Root, false, null, Ct);
        Assert.NotSame(first.Store, own.Store);
    }

    /// <summary>Copies the billing fixture's model into <paramref name="root"/><c>/.maquettiste</c>, optionally renamed.</summary>
    internal static string CopyBilling(string root, string? name = null)
    {
        var source = Fixtures.Path("models", "billing", ".maquettiste");
        var target = Path.Combine(root, ".maquettiste");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }

        if (name is not null)
        {
            var settings = Path.Combine(target, "maquettiste.json");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"name\": \"billing\"", $"\"name\": \"{name}\"", StringComparison.Ordinal));
        }

        return root;
    }

    /// <summary>Every file under a folder with its content hash, ordinal.</summary>
    internal static IReadOnlyList<string> Tree(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/') + " " + ContentHash.Of(File.ReadAllBytes(p)))
            .Order(StringComparer.Ordinal)];
}
