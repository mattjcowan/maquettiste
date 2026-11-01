using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// Copy units rendered, previewed, post-processed and written with the real renderer (next-phase-1-design.md section 5.4): files
/// land byte-exact under the unit's output folder, tokens replace literal text in text files and paths, binary files copy byte for
/// byte, the static hash over the sources decides when the unit renders again, and a hand edit is caught like any overwrite file.
/// </summary>
public sealed class CopyUnitTests
{
    private const string Pack = ".maquettiste/templates/skel/";

    private static readonly byte[] Token = Encoding.UTF8.GetBytes("Skeleton.Api");

    /// <summary>A small "png": its signature (a CR LF and a NUL), the token's bytes and an invalid UTF-8 byte.</summary>
    private static readonly byte[] Logo = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x00, .. Token, 0xFF, 0x0D, 0x0A];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Manifest(string sources = "\"skeleton/**\"", string output = ", \"output\": \"out/app\"") => $$"""
        { "name": "skel", "version": "1.0.0", "engine": ">=1.0",
          "parameters": { "rootNamespace": "Skeleton.Api" },
          "units": [ { "id": "skeleton", "for": "model", "mode": "copy", "sources": [{{sources}}],
            "tokens": { "Skeleton.Api": "rootNamespace" }{{output}} } ] }
        """;

    /// <summary>A repo with the real renderer, the pack <c>skel</c> and the project value <c>Billing.Api</c> for <c>rootNamespace</c>.</summary>
    private static async Task<GenerationFixture> CreateAsync(string? manifest = null, string packOutput = "")
    {
        var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), s => s with { RendererFactory = null });
        await f.WriteModelAsync(b => Models.Shop(b), s => s with
        {
            Packs = new Dictionary<string, PackSettings>
            {
                ["skel"] = new()
                {
                    Output = packOutput,
                    Parameters = new Dictionary<string, JsonElement> { ["rootNamespace"] = JsonSerializer.SerializeToElement("Billing.Api") },
                },
            },
        });
        f.Repo.WriteFile(Pack + "pack.json", manifest ?? Manifest());
        f.Repo.WriteBytes(Pack + "skeleton/Program.cs", Encoding.UTF8.GetBytes("\uFEFFnamespace Skeleton.Api;\r\n\r\npublic static class Program { }\r\n"));
        f.Repo.WriteFile(Pack + "skeleton/Api/Invoices.cs", "namespace Skeleton.Api.Api;\n\npublic sealed class Invoices { }");
        f.Repo.WriteFile(Pack + "skeleton/Skeleton.Api.csproj", "<Project><RootNamespace>Skeleton.Api</RootNamespace></Project>\n");
        f.Repo.WriteBytes(Pack + "skeleton/logo.png", Logo);
        return f;
    }

    private static byte[] Bytes(GenerationFixture f, string path) => File.ReadAllBytes(f.Repo.PathOf(path));

    private static void AssertSucceeded(GenerationResult result) =>
        Assert.True(result.Outcome == RunOutcome.Succeeded,
            result.Outcome + "\n" + string.Join('\n', result.Diagnostics.Select(d => $"{d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}")));

    [Fact]
    public async Task A_folder_of_source_files_copies_with_the_namespace_token_replaced()
    {
        await using var f = await CreateAsync();

        var first = await f.RunAsync();

        AssertSucceeded(first);
        Assert.Equal(
            [("out/app/Api/Invoices.cs", FileChangeKind.Added), ("out/app/Billing.Api.csproj", FileChangeKind.Added),
                ("out/app/Program.cs", FileChangeKind.Added), ("out/app/logo.png", FileChangeKind.Added)],
            first.Changes.Select(c => (c.Path, c.Kind)));
        // Byte-exact: the byte order mark and CR LF line ends stay, a file without a final line end gets none, and only the token
        // changes.
        Assert.Equal(Encoding.UTF8.GetBytes("\uFEFFnamespace Billing.Api;\r\n\r\npublic static class Program { }\r\n"), Bytes(f, "out/app/Program.cs"));
        Assert.Equal("namespace Billing.Api.Api;\n\npublic sealed class Invoices { }", f.Repo.ReadFile("out/app/Api/Invoices.cs"));
        Assert.Equal("<Project><RootNamespace>Billing.Api</RootNamespace></Project>\n", f.Repo.ReadFile("out/app/Billing.Api.csproj"));
        // The binary file holds the token's bytes and is copied unchanged.
        Assert.Equal(Logo, Bytes(f, "out/app/logo.png"));

        var second = await f.RunAsync();

        AssertSucceeded(second);
        Assert.Empty(second.Changes);
        Assert.Equal((0, 1), (second.UnitsRendered, second.UnitsSkipped));
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync(GenerationMode.Check)).Outcome);
    }

    [Fact]
    public async Task Editing_adding_or_removing_a_source_renders_the_unit_again()
    {
        await using var f = await CreateAsync();
        AssertSucceeded(await f.RunAsync());

        f.Repo.WriteFile(Pack + "skeleton/Api/Invoices.cs", "namespace Skeleton.Api.Api;\n// edited\n");
        var edited = await f.RunAsync();
        AssertSucceeded(edited);
        Assert.Equal(1, edited.UnitsRendered);
        Assert.Equal([("out/app/Api/Invoices.cs", FileChangeKind.Modified)], edited.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("namespace Billing.Api.Api;\n// edited\n", f.Repo.ReadFile("out/app/Api/Invoices.cs"));

        f.Repo.WriteFile(Pack + "skeleton/Api/Customers.cs", "namespace Skeleton.Api.Api;\n");
        var added = await f.RunAsync();
        AssertSucceeded(added);
        Assert.Equal(1, added.UnitsRendered);
        Assert.Equal([("out/app/Api/Customers.cs", FileChangeKind.Added)], added.Changes.Select(c => (c.Path, c.Kind)));

        File.Delete(f.Repo.PathOf(Pack + "skeleton/Api/Customers.cs"));
        var removed = await f.RunAsync();
        AssertSucceeded(removed);
        Assert.Equal(1, removed.UnitsRendered);
        Assert.Equal([("out/app/Api/Customers.cs", FileChangeKind.Deleted)], removed.Changes.Select(c => (c.Path, c.Kind)));
        Assert.False(f.Repo.Exists("out/app/Api/Customers.cs"));

        // A file no pattern matches does not count.
        f.Repo.WriteFile(Pack + "readme.md", "not copied\n");
        var unrelated = await f.RunAsync();
        Assert.Equal((0, 1), (unrelated.UnitsRendered, unrelated.UnitsSkipped));
    }

    [Fact]
    public async Task Binary_sources_copy_byte_for_byte()
    {
        // No unit output: the files land at the pack's output base, each after the literal base of the pattern that matched it.
        await using var f = await CreateAsync(Manifest("\"skeleton/assets/*.bin\", \"skeleton/logo.png\"", output: ""), packOutput: "out/base");
        var every = Enumerable.Range(0, 256).Select(i => (byte)i).Concat(Token).Concat(Enumerable.Range(0, 256).Select(i => (byte)(255 - i))).ToArray();
        f.Repo.WriteBytes(Pack + "skeleton/assets/every.bin", every);

        var result = await f.RunAsync();

        AssertSucceeded(result);
        Assert.Equal(["out/base/every.bin", "out/base/logo.png"], result.Changes.Select(c => c.Path));
        Assert.Equal(every, Bytes(f, "out/base/every.bin"));
        Assert.Equal(Logo, Bytes(f, "out/base/logo.png"));

        // A dry run's diff of a binary file is one line.
        f.Repo.WriteBytes(Pack + "skeleton/logo.png", [.. Logo, 0x00]);
        var dry = await f.RunAsync(GenerationMode.DryRun);
        var change = Assert.Single(dry.Changes);
        Assert.Equal((FileChangeKind.Modified, "Binary file out/base/logo.png differs\n"), (change.Kind, change.Diff));
        Assert.Equal(Logo, Bytes(f, "out/base/logo.png"));
    }

    [Fact]
    public void Tokens_replace_longest_first_in_one_pass()
    {
        var tokens = new Maquettiste.Engine.Rendering.CopyTokens(
            new Dictionary<string, string> { ["ab"] = "short", ["abc"] = "long" },
            new Dictionary<string, JsonElement> { ["short"] = JsonSerializer.SerializeToElement("X"), ["long"] = JsonSerializer.SerializeToElement("abc") });

        // "abc" wins over "ab" where both start, and its value is not scanned again.
        Assert.Equal("abcX-a", tokens.Replace("abcab-a"));
        var untouched = Encoding.UTF8.GetBytes("no token here");
        Assert.Same(untouched, tokens.Replace(untouched));
    }

    [Fact]
    public async Task A_hand_edit_of_a_copied_file_is_MQ6009()
    {
        await using var f = await CreateAsync();
        AssertSucceeded(await f.RunAsync());
        f.Repo.WriteFile("out/app/Api/Invoices.cs", "my own version\n");

        var check = await f.RunAsync(GenerationMode.Check);
        Assert.NotEqual(RunOutcome.Succeeded, check.Outcome);

        var refused = await f.RunAsync(force: true);
        Assert.Equal(RunOutcome.Conflicts, refused.Outcome);
        Assert.Contains(refused.Changes, c => c.Path == "out/app/Api/Invoices.cs" && c.Kind == FileChangeKind.Conflict);
        Assert.Contains(refused.Diagnostics, d => d.Rule == "MQ6009" && d.FilePath == "out/app/Api/Invoices.cs");
        Assert.Equal("my own version\n", f.Repo.ReadFile("out/app/Api/Invoices.cs"));

        var overwritten = await f.RunAsync(force: true, handEdits: HandEditPolicy.Overwrite);
        Assert.Equal(RunOutcome.Succeeded, overwritten.Outcome);
        Assert.Contains(overwritten.Changes, c => c.Path == "out/app/Api/Invoices.cs" && c.Kind == FileChangeKind.HandEdited);
        Assert.StartsWith("namespace Billing.Api.Api;", f.Repo.ReadFile("out/app/Api/Invoices.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preview_lists_text_files_and_marks_binary_files()
    {
        await using var f = await CreateAsync();

        var preview = await f.Service.PreviewAsync("skel", "skeleton", null, Ct);

        Assert.Empty(preview.Diagnostics);
        // In the order of their sources, ordinal by pack path.
        Assert.Equal(
            [("out/app/Api/Invoices.cs", false), ("out/app/Program.cs", false), ("out/app/Billing.Api.csproj", false), ("out/app/logo.png", true)],
            preview.Files.Select(p => (p.Path, p.Binary)));
        Assert.Equal("\uFEFFnamespace Billing.Api;\r\n\r\npublic static class Program { }\r\n", preview.Files[1].Text);
        Assert.Equal("<Project><RootNamespace>Billing.Api</RootNamespace></Project>\n", preview.Files[2].Text);
        Assert.Equal("", preview.Files[3].Text);
        Assert.Equal(Logo, preview.Files[3].Bytes);
        Assert.All(preview.Files, p => Assert.Equal(FileRole.Main, p.Role));
        Assert.Empty(preview.ReadKeys);

        // On the wire: binary only when true, and never the bytes.
        var json = JsonSerializer.Serialize(preview.Files, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(1, json.Split("\"binary\"").Length - 1);
        Assert.Contains("\"text\":\"\",\"role\":\"main\",\"binary\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(f.Outputs());
    }

    [Fact]
    public async Task A_preview_of_a_pattern_matching_nothing_is_MQ6030()
    {
        await using var f = await CreateAsync(Manifest("\"skeleton/**\", \"missing/*.cs\""));

        var preview = await f.Service.PreviewAsync("skel", "skeleton", null, Ct);

        Assert.Empty(preview.Files);
        var error = Assert.Single(preview.Diagnostics);
        Assert.Equal(("MQ6030", ".maquettiste/templates/skel/pack.json", "/units/0/sources/1"), (error.Rule, error.FilePath, error.JsonPointer));

        // In a run the planner skips the unit: nothing is written and the run is invalid.
        var run = await f.RunAsync();
        Assert.Equal(RunOutcome.Invalid, run.Outcome);
        Assert.Contains(run.Diagnostics, d => d.Rule == "MQ6030");
        Assert.Empty(f.Outputs());
    }
}
