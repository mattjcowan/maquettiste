using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Project rule scripts use library packs through <c>uses</c> in <c>maquettiste.json</c> (next-phase-2-design.md section 5.2): the
/// loader reads the closure into the snapshot, rules reach its helpers through <c>maquettiste.use</c>, a library change runs cached
/// rules again, and the project's own entries are checked (MQ6042, MQ6043).
/// </summary>
public sealed class RuleLibraryTests
{
    private const string RulePath = ".maquettiste/extensions/rules/class-names.js";
    private const string HelpersPath = ".maquettiste/templates/naming/helpers.js";

    private const string Rule = """
        const naming = maquettiste.use('naming');
        maquettiste.rule({
          id: 'class-name-length', severity: 'error', kinds: ['entity'],
          check(element, model, report) {
            const name = naming.class_name(element);
            if (name.length > 15) report(`${name} is longer than 15 characters.`, { pointer: '/name' });
          },
        });
        """;

    private static CancellationToken Ct => EditorRepo.Ct;

    /// <summary>Billing with the naming library and the rule; <paramref name="range"/> is the project's uses range for it.</summary>
    private static EditorRepo Create(string pack = "naming", string range = "^1.0.0")
    {
        var repo = EditorRepo.Create(packs: false);
        repo.WriteLibraryPacks();
        repo.EditSettingsOnDisk(node => node["uses"] = new JsonArray(new JsonObject { ["pack"] = pack, ["version"] = range }));
        Write(repo, RulePath, Rule);
        return repo;
    }

    private static void Write(EditorRepo repo, string path, string text)
    {
        var full = repo.Repo.PathOf(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static List<string> Findings(ValidationReport report) =>
        [.. report.Diagnostics.Where(d => d.Rule == "x/class-name-length").Select(d => d.Message).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_rule_calls_a_project_library_helper()
    {
        await using var repo = Create();

        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        var library = Assert.Single(snapshot.ScriptLibraries);
        Assert.Equal(("naming", "1.4.0"), (library.Pack, library.Version));
        Assert.Equal([HelpersPath], library.Scripts.Select(s => s.Path));
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5002" or "MQ6042" or "MQ6043");
        Assert.Equal(["CustomerNoteRecord is longer than 15 characters.", "InvoiceLineRecord is longer than 15 characters.",
            "InvoiceNoteRecord is longer than 15 characters.", "RevenueMonthRecord is longer than 15 characters."], Findings(report));
    }

    [Fact]
    public async Task A_library_change_runs_cached_rules_again()
    {
        await using var repo = Create();
        Assert.Equal(4, Findings(await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Count);

        // The rule file and the elements are unchanged; only the library's script is, so a cached result must not be served.
        Write(repo, HelpersPath, LibraryText("Entry"));
        await repo.Store.GetSnapshotAsync(Ct); // the store sees the edit by stat
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        Assert.Equal(["CustomerNoteEntry is longer than 15 characters.", "InvoiceLineEntry is longer than 15 characters.",
            "InvoiceNoteEntry is longer than 15 characters.", "RevenueMonthEntry is longer than 15 characters."], Findings(report));
    }

    [Fact]
    public async Task A_project_uses_entry_naming_a_missing_pack_is_MQ6042()
    {
        await using var repo = Create(pack: "ghost");

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        var error = Assert.Single(report.Diagnostics, d => d.Rule == "MQ6042");
        Assert.Equal((EditorRepo.SettingsPath, "/uses/0/pack"), (error.FilePath, error.JsonPointer));
        Assert.NotNull(error.Line);
        // The rule's maquettiste.use names a pack outside the project's uses: its file does not load.
        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ5002" && d.FilePath == RulePath);
    }

    [Fact]
    public async Task A_project_uses_range_the_library_misses_is_MQ6043()
    {
        await using var repo = Create(range: "^2.0.0");

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        var error = Assert.Single(report.Diagnostics, d => d.Rule == "MQ6043");
        Assert.Equal((EditorRepo.SettingsPath, "/uses/0/version"), (error.FilePath, error.JsonPointer));
        Assert.Contains("1.4.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_failure_in_a_rule_is_MQ5002_naming_the_library()
    {
        await using var repo = Create();
        Write(repo, HelpersPath, """
            maquettiste.helper('class_name', e => {
              if (e.name === 'Invoice') throw new Error('no invoices here');
              return e.name + maquettiste.params.suffix;
            });
            """);

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        var failure = Assert.Single(report.Diagnostics, d => d.Rule == "MQ5002");
        Assert.Equal(HelpersPath, failure.FilePath);
        Assert.Contains("library pack 'naming'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no invoices here", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_a_library_script_reloads_the_snapshot()
    {
        await using var repo = Create();
        var before = await repo.Store.GetSnapshotAsync(Ct);

        Write(repo, HelpersPath, LibraryText("Entry"));
        var after = await repo.Store.GetSnapshotAsync(Ct);

        Assert.True(after.Version > before.Version);
        Assert.NotEqual(before.ScriptLibraries[0].Hash, after.ScriptLibraries[0].Hash);
        Assert.Contains("'Entry'", after.ScriptLibraries[0].Scripts[0].Code, StringComparison.Ordinal);
    }

    private static string LibraryText(string suffix) => $$"""
        function pascal(s) { return String(s).replace(/(^|[-_ ])([a-z])/g, (_, __, c) => c.toUpperCase()); }
        maquettiste.helper('class_name', e => pascal(e.name) + '{{suffix}}');
        """;
}
