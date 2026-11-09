using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The engine's answer for relations with several types, part 2 (storage in the team's tables), written where the editor's tests read it, so
/// the mock is checked against the engine (several-types-design.md section 19.10). Two invented projects, since a whole-model validation
/// drops the run's findings while any error exists: <c>several-types-storage-files</c> holds one misfit per MQ4009 file case, MQ4011,
/// MQ4116's query case and MQ4115 with its new skip; <c>several-types-storage-run</c>, with no validation error, holds every way of
/// section 19.4, one misfit per MQ4117 case it covers, every MQ4118 gap, MQ4051 and MQ4120. Each writes its findings, sorted, to its
/// <c>diagnostics.json</c> with <c>MAQUETTISTE_RECORD=1</c>; otherwise the test fails when the engine's answer differs.
/// </summary>
public sealed class SeveralTypesStorageParityTests
{
    private static readonly string[] FileRules = ["MQ4009", "MQ4011", "MQ4115", "MQ4116"];
    private static readonly string[] RunRules = ["MQ4051", "MQ4117", "MQ4118", "MQ4120"];

    private static bool Recording => Environment.GetEnvironmentVariable("MAQUETTISTE_RECORD") == "1";

    private static async Task<List<(string Rule, string Element, string Pointer, string Message, string Severity)>> FindingsAsync(string fixture, string[] rules,
        bool noErrors)
    {
        using var h = new LoaderHarness();
        var source = Path.Combine(Fixtures.RepoRoot, "src", "editor", "tests", "fixtures", fixture, ".maquettiste");
        foreach (var file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
            h.Write(Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(file, Encoding.UTF8));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var report = await store.ValidateAsync(ValidationScope.All, TestContext.Current.CancellationToken);
        if (noErrors)
            Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && !rules.Contains(d.Rule));
        return [.. report.Diagnostics.Where(d => rules.Contains(d.Rule))
            .Select(d => (Rule: d.Rule, Element: d.ElementId ?? "", Pointer: d.JsonPointer ?? "", d.Message, Severity: d.Severity.ToString().ToLowerInvariant()))
            .Distinct()
            .OrderBy(d => d.Rule, StringComparer.Ordinal).ThenBy(d => d.Element, StringComparer.Ordinal).ThenBy(d => d.Pointer, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)];
    }

    private static void Record(string fixture, string[] rules, List<(string Rule, string Element, string Pointer, string Message, string Severity)> rows)
    {
        var json = new JsonObject
        {
            ["about"] = "The engine's findings for the rules below on the project in .maquettiste, sorted by rule, element id, pointer and message. Written by SeveralTypesStorageParityTests with MAQUETTISTE_RECORD=1; the editor's mock must give the same.",
            ["rules"] = new JsonArray([.. rules.Select(r => JsonValue.Create(r))]),
            ["diagnostics"] = new JsonArray([.. rows.Select(r => new JsonObject
            {
                ["rule"] = r.Rule, ["elementId"] = r.Element, ["pointer"] = r.Pointer, ["message"] = r.Message, ["severity"] = r.Severity,
            })]),
        };
        var text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        var path = Path.Combine(Fixtures.RepoRoot, "src", "editor", "tests", "fixtures", fixture, "diagnostics.json");
        if (Recording)
            File.WriteAllText(path, text, new UTF8Encoding(false));
        Assert.True(File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text,
            $"src/editor/tests/fixtures/{fixture}/diagnostics.json is out of date: run this test with MAQUETTISTE_RECORD=1.\n" + text);
    }

    [Fact]
    public async Task The_file_rules_on_the_storage_files_fixture_match_the_recorded_file()
    {
        var rows = await FindingsAsync("several-types-storage-files", FileRules, noErrors: false);
        void Hit(string rule, string text) => Assert.True(rows.Exists(r => r.Rule == rule && r.Message.Contains(text, StringComparison.Ordinal)), rule + ": " + text);
        Hit("MQ4009", "doesn't take 'shape'");
        Hit("MQ4009", "doesn't take 'foreignKeyEnd'");
        Hit("MQ4009", "doesn't take 'foreignKey'");
        Hit("MQ4009", "doesn't take 'promotedName'");
        Hit("MQ4009", "so 'storage' of attribute 0 can't shape engine-made columns");
        Hit("MQ4009", "so 'prefix' of attribute 0 can't shape engine-made columns");
        Hit("MQ4009", "keeps its links in the tables of its ends, so shape 'junction' disagrees");
        Hit("MQ4009", "names more than one way to point at end 'target' (foreignKey, idColumn)");
        Hit("MQ4009", "names no way to point at end 'comment'");
        Hit("MQ4009", "has two entries; keep one");
        Hit("MQ4009", "uses one column for 'typeColumn' and 'idColumn'");
        Hit("MQ4009", "sets 'referencesColumn' with no 'idColumn'");
        Hit("MQ4009", "sets 'referencesColumn' on the entry and in types items");
        Hit("MQ4009", "has types items with foreignKey beside 'idColumn'");
        Hit("MQ4009", "mixes types items with foreignKey and with referencesColumn");
        Hit("MQ4009", "has types items with referencesColumn but no 'idColumn'");
        Hit("MQ4009", "sets 'foreignKeys' beside 'junctionTable'");
        Hit("MQ4009", "names a column that an end entry already uses as a pointer");
        Hit("MQ4009", "is not a column of link table 'links'");
        Hit("MQ4009", "Constant 0 needs a designed 'junctionTable'");
        Hit("MQ4009", "sets 'idColumn', which needs a relation with an end of several types");
        Hit("MQ4009", "End entry 1 names no 'foreignKey'; junction table 'links' is designed");
        Hit("MQ4011", "Link table 'links' holds relation 'NoEntry', so end 'comment' needs an 'ends' entry");
        Hit("MQ4115", "Relation 'Unmapped' has an end with several types, so database 'main' stores nothing for it.");
        Hit("MQ4116", "Field 1 of query 'Comments' names end 'target'");
        // MQ4115's skips: a mapping that sets ignore, or that stores the relation (decision 49); discriminatorValue on Comment is allowed.
        Assert.DoesNotContain(rows, r => r.Rule == "MQ4115" && (r.Message.Contains("'Ignored'", StringComparison.Ordinal) || r.Message.Contains("'Stored'", StringComparison.Ordinal)));
        Assert.DoesNotContain(rows, r => r.Rule == "MQ4009" && r.Message.Contains("discriminator", StringComparison.Ordinal));
        Record("several-types-storage-files", FileRules, rows);
    }

    [Fact]
    public async Task The_run_findings_on_the_storage_run_fixture_match_the_recorded_file()
    {
        var rows = await FindingsAsync("several-types-storage-run", RunRules, noErrors: true);
        void Hit(string rule, string text) => Assert.True(rows.Exists(r => r.Rule == rule && r.Message.Contains(text, StringComparison.Ordinal)), rule + ": " + text);
        Hit("MQ4117", "Foreign key 'fk_assembly' of table 'comments' is named for type 'Part' of end 'x', but it refers to table 'assemblies'");
        Hit("MQ4117", "Column 'target_id' named by end 'x' isn't in table 'comments', which holds the links of 'Part'.");
        Hit("MQ4117", "Foreign keys 'fk_parts_folder' and 'fk_parts_folder2' of end 'folder' both belong to table 'parts'");
        Hit("MQ4117", "Every end of relation 'NoHolder' has an entry and there is no 'junctionTable'");
        Hit("MQ4117", "Both ends of relation 'BothTyped' have a key per type and there is no 'junctionTable'");
        Hit("MQ4117", "but end 'folder' is pointed at by one column");
        Hit("MQ4117", "a row holds one link and end 'x' is to-many");
        Hit("MQ4117", "which is neither its primary key nor a unique key");
        Hit("MQ4117", "names one 'referencesColumn', but its targets live in tables 'parts', 'assemblies'");
        Hit("MQ4117", "Column 'attached_type' of table 'comments' is string(20), but it refers to column 'id' of table 'parts', which is uuid");
        Hit("MQ4117", "Types 'Widget' and 'Gadget' both have code 'Widget'");
        Hit("MQ4117", "code 'Part' isn't an integer");
        Hit("MQ4117", "End 'x' is any entity, so it can't have a key per type");
        Hit("MQ4117", "Relations 'Linked' and 'Linked2' both keep their links in table 'links'");
        Hit("MQ4117", "Column 'note' for attribute 'remark' isn't in table 'comments'");
        Hit("MQ4117", "so one column can't hold attribute 'remark'; a column per table waits");
        Hit("MQ4118", "can't refuse a link to a missing");
        Hit("MQ4118", "it can't refuse a key to a row of table 'gizmos' whose type end 'gizmo' doesn't cover");
        Hit("MQ4118", "it can't check the type an id points at");
        Hit("MQ4118", "it finds no check that names column 'attached_type'");
        Hit("MQ4118", "it finds no check on how many of columns 'part_id', 'assembly_id' of table 'comments' are set");
        Hit("MQ4118", "so two rows can link to one object");
        Hit("MQ4118", "so it can't refuse a duplicate link");
        Hit("MQ4118", "it can't refuse duplicate links in table 'bundles'");
        Hit("MQ4118", "nothing tells the rows of 'Widget' apart from the other rows of table 'gizmos'");
        Hit("MQ4120", "Relation 'PinnedTo' covers entity 'Note' in database 'main'");
        Hit("MQ4051", "The constant for column 'relation_code' of the mapping of relation 'Noted' to database 'main'");
        // Every way of section 19.4 stores without a refusal.
        foreach (var way in new[] { "'AttachedTo'", "'Folded'", "'PinnedTo'", "'Refers'", "'OnGizmo'", "'Linked'", "'PerType'", "'Noted'", "'Bridges'", "'Pairs'", "'Cover'", "'Bundled'" })
            Assert.DoesNotContain(rows, r => r.Rule == "MQ4117" && r.Message.Contains("relation " + way, StringComparison.Ordinal) && !way.Equals("'Linked'", StringComparison.Ordinal));
        Record("several-types-storage-run", RunRules, rows);
    }
}
