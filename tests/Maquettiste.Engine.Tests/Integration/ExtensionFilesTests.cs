using System.Text.Json;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The extensions folder through the store's file API (the editor's Extensions screen and the MCP extension file tools), and how a
/// broken rule script is reported: on its own file, with line and column, while the other rule files keep running.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ExtensionFilesTests
{
    private const string LongNames = """
        maquettiste.rule({
          id: "short-names",
          severity: "warning",
          kinds: ["entity"],
          check(element, model, report) {
            if (element.name.length > 7) report("Entity names should be at most 7 characters.", { pointer: "/name" });
          },
        });
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rule_file_with_a_syntax_error_is_reported_on_its_line_and_the_other_rules_still_run()
    {
        await using var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/naming.js", LongNames);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/broken.js", "// a rule in progress\nmaquettiste.rule({ id: \"broken\", check(element) {\n");

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        var failure = Assert.Single(report.Diagnostics, d => d.Rule == "MQ5002");
        Assert.Equal(".maquettiste/extensions/rules/broken.js", failure.FilePath);
        Assert.Contains("syntax error", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.Line);
        Assert.NotNull(failure.Column);
        Assert.Null(failure.ElementId);
        Assert.Contains(report.Diagnostics, d => d.Rule == "x/short-names" && d.JsonPointer == "/name");
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ2007");
    }

    [Fact]
    public async Task A_rule_without_an_id_is_reported_on_the_line_that_registers_it()
    {
        await using var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/naming.js", LongNames);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/anonymous.js", "\n\nmaquettiste.rule({ severity: \"error\", check() {} });\n");

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        var failure = Assert.Single(report.Diagnostics, d => d.Rule == "MQ5002");
        Assert.Equal(".maquettiste/extensions/rules/anonymous.js", failure.FilePath);
        Assert.Contains("id must be a non-empty string", failure.Message, StringComparison.Ordinal);
        Assert.Equal(3, failure.Line);
        Assert.NotNull(failure.Column);
        Assert.Contains(report.Diagnostics, d => d.Rule == "x/short-names");
    }

    [Fact]
    public async Task A_check_that_throws_is_reported_on_the_rule_file_with_its_line()
    {
        await using var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/throws.js", """
            maquettiste.rule({
              id: "throws",
              kinds: ["entity"],
              check(element) {
                if (element.name === "Product") throw new Error("not ready");
              },
            });
            """);

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        var failure = Assert.Single(report.Diagnostics, d => d.Rule == "MQ5002");
        Assert.Equal(".maquettiste/extensions/rules/throws.js", failure.FilePath);
        Assert.Equal(E2ERepo.ProductId, failure.ElementId);
        Assert.Equal(5, failure.Line);
        Assert.Contains("not ready", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_store_lists_reads_writes_moves_and_deletes_extension_files()
    {
        await using var repo = E2ERepo.Create(demo: false);

        // The billing fixture has one custom property schema and no rules.
        var list = await repo.Store.ListExtensionFilesAsync(Ct);
        Assert.Equal(".maquettiste/extensions", list.Folder);
        var schema = Assert.Single(list.Files);
        Assert.Equal(("retention.json", "schema"), (schema.Path, schema.Kind));
        Assert.Empty(schema.Diagnostics);
        var read = (await repo.Store.ReadExtensionFileAsync("retention.json", Ct))!;
        Assert.Equal(schema.Hash, read.Hash);
        Assert.Contains("retentionDays", read.Text, StringComparison.Ordinal);

        // A new rule: written as sent, checked, and run by the next validation without a refresh.
        var created = await repo.Store.WriteExtensionFileAsync("rules/naming.js", LongNames, null, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.Empty(created.Diagnostics);
        Assert.Equal(LongNames, repo.Repo.ReadFile(".maquettiste/extensions/rules/naming.js"));
        Assert.Contains((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "x/short-names");
        var listed = Assert.Single((await repo.Store.ListExtensionFilesAsync(Ct)).Files, f => f.Kind == "rule");
        Assert.Equal(("rules/naming.js", created.Hash), (listed.Path, listed.Hash));
        Assert.Equal([new ExtensionRuleInfo("x/short-names", DiagnosticSeverity.Warning)], listed.Rules);

        // Creating it again is a conflict; a stale hash too, with the disk version.
        Assert.Equal(SaveOutcome.Conflict, (await repo.Store.WriteExtensionFileAsync("rules/naming.js", LongNames, null, Ct)).Outcome);
        var stale = await repo.Store.WriteExtensionFileAsync("rules/naming.js", "//", new string('0', 64), Ct);
        Assert.Equal((SaveOutcome.Conflict, created.Hash, LongNames), (stale.Outcome, stale.Hash, stale.Current));

        // A syntax error is saved and comes back at once, with line and column.
        var broken = await repo.Store.WriteExtensionFileAsync("rules/naming.js", "maquettiste.rule({\n  id: \"x\",,\n});\n", created.Hash, Ct);
        Assert.Equal(SaveOutcome.Saved, broken.Outcome);
        var syntax = Assert.Single(broken.Diagnostics);
        Assert.Equal(("MQ5002", ".maquettiste/extensions/rules/naming.js", 2), (syntax.Rule, syntax.FilePath, syntax.Line));
        Assert.NotNull(syntax.Column);
        Assert.Contains(Assert.Single((await repo.Store.ListExtensionFilesAsync(Ct)).Files, f => f.Kind == "rule").Diagnostics, d => d.Rule == "MQ5002");

        // A schema that is not JSON, or fails extension.json, is refused and nothing is written.
        var notJson = await repo.Store.WriteExtensionFileAsync("audit.json", "{ \"name\": ", null, Ct);
        Assert.Equal(SaveOutcome.Invalid, notJson.Outcome);
        Assert.Equal("MQ5004", Assert.Single(notJson.Diagnostics).Rule);
        Assert.NotNull(notJson.Diagnostics[0].Line);
        var noTarget = await repo.Store.WriteExtensionFileAsync("audit.json", "{ \"name\": \"audit\", \"properties\": {} }", null, Ct);
        Assert.Equal(SaveOutcome.Invalid, noTarget.Outcome);
        Assert.All(noTarget.Diagnostics, d => Assert.Equal("MQ5004", d.Rule));
        Assert.False(File.Exists(repo.Repo.PathOf(".maquettiste/extensions/audit.json")));

        // A valid schema is written in canonical form, and the text written comes back.
        var audit = await repo.Store.WriteExtensionFileAsync("audit.json",
            "{\"properties\":{\"owner\":{\"type\":\"string\"}},\"appliesTo\":{\"kinds\":[\"entity\"]},\"name\":\"audit\"}", null, Ct);
        Assert.Equal(SaveOutcome.Saved, audit.Outcome);
        Assert.Equal(audit.Text, repo.Repo.ReadFile(".maquettiste/extensions/audit.json"));
        using (var document = JsonDocument.Parse(audit.Text!))
            Assert.Equal(["$schema", "name", "appliesTo", "properties"], document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Contains((await repo.Store.GetSnapshotAsync(Ct)).Extensions, e => e.Schema.Name == "audit");

        // Move: within its kind only, never over another file.
        Assert.Equal(SaveOutcome.Invalid, (await repo.Store.MoveExtensionFileAsync(new ExtensionFileMove("audit.json", "rules/audit.js"), audit.Hash!, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Invalid, (await repo.Store.MoveExtensionFileAsync(new ExtensionFileMove("audit.json", "retention.json"), audit.Hash!, Ct)).Outcome);
        var moved = await repo.Store.MoveExtensionFileAsync(new ExtensionFileMove("audit.json", "ownership.json"), audit.Hash!, Ct);
        Assert.Equal((SaveOutcome.Saved, audit.Hash), (moved.Outcome, moved.Hash));
        Assert.False(File.Exists(repo.Repo.PathOf(".maquettiste/extensions/audit.json")));
        Assert.True(File.Exists(repo.Repo.PathOf(".maquettiste/extensions/ownership.json")));

        // Delete with the hash read; then the snapshot no longer runs the rule.
        Assert.Equal(SaveOutcome.Conflict, (await repo.Store.DeleteExtensionFileAsync("rules/naming.js", created.Hash!, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.DeleteExtensionFileAsync("rules/naming.js", broken.Hash!, Ct)).Outcome);
        Assert.Empty((await repo.Store.GetSnapshotAsync(Ct)).RuleScripts);
        Assert.Equal(SaveOutcome.NotFound, (await repo.Store.DeleteExtensionFileAsync("rules/naming.js", broken.Hash!, Ct)).Outcome);
        Assert.Null(await repo.Store.ReadExtensionFileAsync("rules/naming.js", Ct));
    }

    [Theory]
    [InlineData("../maquettiste.json")]
    [InlineData("rules/nested/a.js")]
    [InlineData("rules/a.json")]
    [InlineData("notes.txt")]
    [InlineData(".hidden.json")]
    [InlineData("a.js")]
    [InlineData("")]
    public async Task Paths_outside_the_two_extension_shapes_are_refused(string path)
    {
        await using var repo = E2ERepo.Create(demo: false);

        await Assert.ThrowsAsync<ExtensionPathException>(() => repo.Store.WriteExtensionFileAsync(path, "{}", null, Ct));
        await Assert.ThrowsAsync<ExtensionPathException>(() => repo.Store.ReadExtensionFileAsync(path, Ct));
    }
}
