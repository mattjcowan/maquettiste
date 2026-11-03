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

    [Fact]
    public async Task A_rows_script_is_listed_written_and_checked_as_seed_rows()
    {
        // next-phase-2-design.md section 3.4: seeds/<name>.js is a seed's rows script (kind seed-rows); a write loads it alone, never
        // runs its rows, and reports a script that does not load or does not register its rows exactly once as MQ7127.
        await using var repo = E2ERepo.Create(demo: false);
        const string Rows = "maquettiste.rows((model, seed) => model.all(\"entity\").map(e => ({ code: e.name })));\n";

        var created = await repo.Store.WriteExtensionFileAsync("seeds/entities.js", Rows, null, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.Empty(created.Diagnostics);
        Assert.Equal(Rows, repo.Repo.ReadFile(".maquettiste/extensions/seeds/entities.js"));
        Assert.Equal([".maquettiste/extensions/seeds/entities.js"], (await repo.Store.GetSnapshotAsync(Ct)).SeedScripts.Select(s => s.Path));
        await repo.Store.WriteExtensionFileAsync("rules/naming.js", LongNames, null, Ct);

        // Listed after the schemas and the rules, with no rules of its own.
        var files = (await repo.Store.ListExtensionFilesAsync(Ct)).Files;
        Assert.Equal([("retention.json", "schema"), ("rules/naming.js", "rule"), ("seeds/entities.js", "seed-rows")], files.Select(f => (f.Path, f.Kind)));
        Assert.Empty(files[2].Rules);
        Assert.Empty(files[2].Diagnostics);
        Assert.Equal("seed-rows", (await repo.Store.ReadExtensionFileAsync("seeds/entities.js", Ct))!.Kind);

        // Rows registered twice, or not at all, or a registration of another kind: saved, and reported with the line.
        var twice = await repo.Store.WriteExtensionFileAsync("seeds/entities.js", Rows + "maquettiste.rows(() => []);\n", created.Hash, Ct);
        Assert.Equal(SaveOutcome.Saved, twice.Outcome);
        Assert.Equal(("MQ7127", ".maquettiste/extensions/seeds/entities.js", 2), (twice.Diagnostics[0].Rule, twice.Diagnostics[0].FilePath, twice.Diagnostics[0].Line));
        var none = await repo.Store.WriteExtensionFileAsync("seeds/none.js", "const rows = [];\n", null, Ct);
        Assert.Contains("registers no rows", Assert.Single(none.Diagnostics, d => d.Rule == "MQ7127").Message, StringComparison.Ordinal);
        var helper = await repo.Store.WriteExtensionFileAsync("seeds/helper.js", "maquettiste.helper('x', () => 1);\n", null, Ct);
        Assert.Equal("MQ7127", Assert.Single(helper.Diagnostics).Rule);
        Assert.Contains(Assert.Single((await repo.Store.ListExtensionFilesAsync(Ct)).Files, f => f.Path == "seeds/none.js").Diagnostics,
            d => d.Rule == "MQ7127");

        // A top-level let is a warning, as in a rule file.
        var state = await repo.Store.WriteExtensionFileAsync("seeds/state.js", "let n = 0;\nmaquettiste.rows(() => []);\n", null, Ct);
        Assert.Equal(("MQ5005", DiagnosticSeverity.Warning), (Assert.Single(state.Diagnostics).Rule, state.Diagnostics[0].Severity));

        // A rows script moves within its kind only.
        Assert.Equal(SaveOutcome.Invalid, (await repo.Store.MoveExtensionFileAsync(new ExtensionFileMove("seeds/state.js", "rules/state.js"), state.Hash!, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.MoveExtensionFileAsync(new ExtensionFileMove("seeds/state.js", "seeds/counter.js"), state.Hash!, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.DeleteExtensionFileAsync("seeds/counter.js", state.Hash!, Ct)).Outcome);
        Assert.DoesNotContain((await repo.Store.GetSnapshotAsync(Ct)).SeedScripts, s => s.Path.EndsWith("counter.js", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ExtensionPathException>(() => repo.Store.WriteExtensionFileAsync("seeds/nested/a.js", Rows, null, Ct));
    }

    [Theory]
    [InlineData("../maquettiste.json")]
    [InlineData("rules/nested/a.js")]
    [InlineData("rules/a.json")]
    [InlineData("seeds/a.json")]
    [InlineData("seeds/nested/a.js")]
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

    /// <summary>Custom kind files (next-phase-2-design.md section 4.2) through the extension file API: kind custom-kind, checked against
    /// custom-kind.json, written in canonical form, with their own findings listed.</summary>
    [Fact]
    public async Task Custom_kind_files_are_listed_read_and_written_in_canonical_form()
    {
        await using var s = await CustomKinds.CustomFixture.OpenAsync(withDueDays: false);

        var listed = Assert.Single((await s.Store.ListExtensionFilesAsync(Ct)).Files, f => f.Path == "kinds/setting.json");
        Assert.Equal("custom-kind", listed.Kind);
        Assert.Empty(listed.Diagnostics);
        var read = await s.Store.ReadExtensionFileAsync("kinds/setting.json", Ct);
        Assert.Equal(("custom-kind", listed.Hash), (read!.Kind, read.Hash));

        // A new kind is written in canonical form ($schema, then x-order); a text that fails custom-kind.json writes nothing.
        var written = await s.Store.WriteExtensionFileAsync("kinds/flag.json",
            """{"properties":{"on":{"type":"boolean"}},"displayName":"Flag","type":"flag"}""", null, Ct);
        Assert.True(written.Outcome == SaveOutcome.Saved, string.Join("; ", written.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Empty(written.Diagnostics);
        var text = written.Text!;
        Assert.True(text.IndexOf("\"type\"", StringComparison.Ordinal) < text.IndexOf("\"displayName\"", StringComparison.Ordinal)
            && text.IndexOf("\"displayName\"", StringComparison.Ordinal) < text.IndexOf("\"properties\"", StringComparison.Ordinal), text);
        Assert.Contains("flag", s.Model.PropertySchemas.ContentSources.Select(c => c.CustomType));
        var invalid = await s.Store.WriteExtensionFileAsync("kinds/broken.json", """{ "type": "broken" }""", null, Ct);
        Assert.Equal((SaveOutcome.Invalid, "MQ1002"), (invalid.Outcome, invalid.Diagnostics[0].Rule));
        var notJson = await s.Store.WriteExtensionFileAsync("kinds/broken.json", "{", null, Ct);
        Assert.Equal((SaveOutcome.Invalid, "MQ1001"), (notJson.Outcome, Assert.Single(notJson.Diagnostics).Rule));
        Assert.False(File.Exists(s.Harness.Model("extensions/kinds/broken.json")));

        // A type that differs from the file name is written, and its MQ1017 comes back with the write and the list.
        var mismatch = await s.Store.WriteExtensionFileAsync("kinds/toggle.json", """{ "type": "switch", "properties": {} }""", null, Ct);
        Assert.Equal(SaveOutcome.Saved, mismatch.Outcome);
        Assert.Contains(mismatch.Diagnostics, d => d.Rule == "MQ1017");
        Assert.Contains(Assert.Single((await s.Store.ListExtensionFilesAsync(Ct)).Files, f => f.Path == "kinds/toggle.json").Diagnostics, d => d.Rule == "MQ1017");

        // A custom kind moves only to another custom kind path.
        var crossKind = await s.Store.MoveExtensionFileAsync(new ExtensionFileMove("kinds/flag.json", "flag.json"), written.Hash!, Ct);
        Assert.Equal((SaveOutcome.Invalid, "MQ5004"), (crossKind.Outcome, Assert.Single(crossKind.Diagnostics).Rule));
    }

    [Fact]
    public async Task Deleting_or_moving_a_used_custom_kind_is_refused()
    {
        await using var s = await CustomKinds.CustomFixture.OpenAsync();
        var hash = (await s.Store.ReadExtensionFileAsync("kinds/setting.json", Ct))!.Hash;

        var deleted = await s.Store.DeleteExtensionFileAsync("kinds/setting.json", hash, Ct);
        var moved = await s.Store.MoveExtensionFileAsync(new ExtensionFileMove("kinds/setting.json", "kinds/option.json"), hash, Ct);

        foreach (var refused in new[] { deleted, moved })
        {
            Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
            var finding = Assert.Single(refused.Diagnostics);
            Assert.Equal(("MQ1019", CustomKinds.CustomFixture.DueDays, "/type"), (finding.Rule, finding.ElementId, finding.JsonPointer));
            Assert.Contains("'due_days'", finding.Message, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(s.Harness.Model("extensions/kinds/setting.json")));

        // Once no element uses it, the kind goes.
        var element = s.Model.GetDocument(CustomKinds.CustomFixture.DueDays)!;
        Assert.Equal(SaveOutcome.Saved, (await s.Store.DeleteAsync(CustomKinds.CustomFixture.DueDays, element.Hash, DeleteResolution.Refuse, ChangeSource.Cli, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await s.Store.DeleteExtensionFileAsync("kinds/setting.json", hash, Ct)).Outcome);
    }

    [Fact]
    public async Task A_custom_kind_write_reports_MQ3033_for_its_elements()
    {
        await using var s = await CustomKinds.CustomFixture.OpenAsync();
        var hash = (await s.Store.ReadExtensionFileAsync("kinds/setting.json", Ct))!.Hash;

        // due_days holds a unit of "days": a kind that makes value a string and drops unit leaves it two findings, and still writes.
        var written = await s.Store.WriteExtensionFileAsync("kinds/setting.json",
            """{ "type": "setting", "properties": { "value": { "type": "string" } } }""", hash, Ct);

        Assert.Equal(SaveOutcome.Saved, written.Outcome);
        var findings = written.Diagnostics.Where(d => d.Rule == "MQ3033").ToList();
        Assert.NotEmpty(findings);
        Assert.All(findings, d => Assert.Equal(CustomKinds.CustomFixture.DueDays, d.ElementId));
        Assert.Contains(findings, d => d.JsonPointer == "/content/value");
        Assert.Equal(findings.Count, (await CustomKinds.CustomFixture.ValidateAsync(s)).Count(d => d.Rule == "MQ3033"));
    }
}
