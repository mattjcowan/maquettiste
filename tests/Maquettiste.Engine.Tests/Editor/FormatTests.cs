using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary><see cref="ModelStore.FormatAsync"/>: the editor's "Rewrite in canonical form" (what <c>maquettiste format</c> does).</summary>
public sealed class FormatTests
{
    private const string InvoicePath = ".maquettiste/model/entities/invoice.json";
    private const string CustomerPath = ".maquettiste/model/entities/customer.json";

    /// <summary>maquettiste.json as a 0.5.4 project has it: compact (not canonical) and with <c>commit</c> on its first output root.</summary>
    private static void WriteLegacySettings(EditorRepo repo)
    {
        var node = JsonNode.Parse(File.ReadAllBytes(repo.Repo.PathOf(EditorRepo.SettingsPath)))!.AsObject();
        node["outputs"]!["allow"]![0]!["commit"] = true;
        File.WriteAllBytes(repo.Repo.PathOf(EditorRepo.SettingsPath), Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    private static async Task<string[]> FindingsAsync(EditorRepo repo) =>
    [
        .. (await repo.Store.ValidateAsync(ValidationScope.All, EditorRepo.Ct)).Diagnostics
            .Where(d => d.Rule is "MQ1003" or RetiredSettings.Rule)
            .Select(d => d.Rule + " " + d.FilePath)
            .Order(StringComparer.Ordinal),
    ];

    [Fact]
    public async Task A_settings_file_with_the_retired_commit_flag_is_rewritten_without_it_and_its_findings_go()
    {
        await using var repo = EditorRepo.Create(packs: false);
        WriteLegacySettings(repo);
        await repo.Store.LoadAsync(EditorRepo.Ct);
        Assert.Equal(["MQ1003 " + EditorRepo.SettingsPath, "MQ1010 " + EditorRepo.SettingsPath], await FindingsAsync(repo));

        var result = await repo.Store.FormatAsync([EditorRepo.SettingsPath], ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal([EditorRepo.SettingsPath], result.Formatted);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Refused);
        var text = repo.Repo.ReadFile(EditorRepo.SettingsPath);
        Assert.DoesNotContain("commit", text, StringComparison.Ordinal);
        Assert.Contains("\n  \"outputs\": {", text, StringComparison.Ordinal);
        Assert.Empty(await FindingsAsync(repo));
        var settings = await repo.Store.GetSettingsAsync(EditorRepo.Ct);
        Assert.Equal(repo.Store.Current!.SettingsHash, settings.Hash);
        Assert.Equal(["db", "src/Generated"], settings.Settings.Outputs.Allow.Select(a => a.Path));
    }

    [Fact]
    public async Task Every_model_file_is_considered_an_element_rewrite_is_published_and_canonical_files_are_untouched()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var customer = JsonNode.Parse(File.ReadAllBytes(repo.Repo.PathOf(CustomerPath)))!;
        File.WriteAllBytes(repo.Repo.PathOf(CustomerPath), Encoding.UTF8.GetBytes(customer.ToJsonString()));
        var invoice = File.ReadAllBytes(repo.Repo.PathOf(InvoicePath));
        var invoiceTime = File.GetLastWriteTimeUtc(repo.Repo.PathOf(InvoicePath));
        await repo.Store.LoadAsync(EditorRepo.Ct);
        Assert.Equal(["MQ1003 " + CustomerPath], await FindingsAsync(repo));
        var published = new List<ChangeSet>();
        using var subscription = repo.Store.OnChanged((c, _) =>
        {
            published.Add(c);
            return ValueTask.CompletedTask;
        });

        var result = await repo.Store.FormatAsync(null, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal([CustomerPath], result.Formatted);
        Assert.True(result.Total > 10, result.Total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(await FindingsAsync(repo));
        Assert.Equal(EditorRepo.CustomerId, Assert.Single(Assert.Single(published).Changed).Id);
        Assert.Equal(invoice, File.ReadAllBytes(repo.Repo.PathOf(InvoicePath)));
        Assert.Equal(invoiceTime, File.GetLastWriteTimeUtc(repo.Repo.PathOf(InvoicePath)));

        // Already canonical: nothing to do, nothing published.
        var again = await repo.Store.FormatAsync([InvoicePath, CustomerPath], ChangeSource.Editor, EditorRepo.Ct);
        Assert.Empty(again.Formatted);
        Assert.Equal(2, again.Total);
        Assert.Single(published);
    }

    [Fact]
    public async Task A_path_that_is_not_a_model_file_refuses_the_call_and_nothing_is_written()
    {
        await using var repo = EditorRepo.Create();
        WriteLegacySettings(repo);
        var before = repo.Files();

        foreach (var outside in new[] { "src/Generated/x.json", "../outside.json", ".maquettiste/templates/sql-ddl/pack.json", ".maquettiste/model/entities/missing.json" })
        {
            var result = await repo.Store.FormatAsync([EditorRepo.SettingsPath, outside], ChangeSource.Editor, EditorRepo.Ct);
            Assert.Equal([outside], result.Refused);
            Assert.Empty(result.Formatted);
        }

        Assert.Equal(before, repo.Files());
    }

    [Fact]
    public async Task A_file_that_fails_its_schema_is_left_as_it_is_and_listed()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var customer = JsonNode.Parse(File.ReadAllBytes(repo.Repo.PathOf(CustomerPath)))!.AsObject();
        customer["unknownMember"] = 1;
        var bytes = Encoding.UTF8.GetBytes(customer.ToJsonString());
        File.WriteAllBytes(repo.Repo.PathOf(CustomerPath), bytes);

        var result = await repo.Store.FormatAsync([CustomerPath], ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal([CustomerPath], result.Skipped);
        Assert.Empty(result.Formatted);
        Assert.Equal(bytes, File.ReadAllBytes(repo.Repo.PathOf(CustomerPath)));
    }
}
