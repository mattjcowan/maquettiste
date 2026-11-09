using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The engine's answer for relations with several types at an end, part 1, written where the editor's tests read it, so the editor's mock is
/// checked against the engine and not only against itself (several-types-design.md section 6). A small invented project
/// (<c>src/editor/tests/fixtures/several-types/.maquettiste</c>) hits every new rule and new case: MQ3041 (two cases; part 2 dropped its both-ends case, so relation 'Both' is valid), MQ3042, MQ3043 (three
/// texts), MQ2003 and MQ9211 on a stereotype entry, MQ4009, MQ4115, MQ4116 (each case), MQ7105 (each case), MQ3116 and MQ9305 (a link and a
/// connect step). The findings of those rules, sorted, are in <c>diagnostics.json</c> beside it. With <c>MAQUETTISTE_RECORD=1</c> the test
/// writes the file; otherwise it fails when the engine's answer differs from it.
/// </summary>
public sealed class SeveralTypesParityTests
{
    private static readonly string[] Rules = ["MQ2003", "MQ3041", "MQ3042", "MQ3043", "MQ3116", "MQ4009", "MQ4115", "MQ4116", "MQ7105", "MQ9211", "MQ9305"];

    private static string Folder => Path.Combine(Fixtures.RepoRoot, "src", "editor", "tests", "fixtures", "several-types");

    private static bool Recording => Environment.GetEnvironmentVariable("MAQUETTISTE_RECORD") == "1";

    [Fact]
    public async Task The_engines_findings_on_the_shared_fixture_match_the_recorded_file()
    {
        using var h = new LoaderHarness();
        var source = Path.Combine(Folder, ".maquettiste");
        foreach (var file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
            h.Write(Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(file, Encoding.UTF8));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var found = await ActorKit.ValidateAsync(h, store.Current!);

        var rows = found.Where(d => Rules.Contains(d.Rule))
            .Select(d => (Rule: d.Rule, Element: d.ElementId ?? "", Pointer: d.JsonPointer ?? "", d.Message, Severity: d.Severity.ToString().ToLowerInvariant()))
            .Distinct()
            .OrderBy(d => d.Rule, StringComparer.Ordinal).ThenBy(d => d.Element, StringComparer.Ordinal).ThenBy(d => d.Pointer, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();

        // Every rule and case the fixture is there for is hit.
        void Hit(string rule, string text) => Assert.True(rows.Any(r => r.Rule == rule && r.Message.Contains(text, StringComparison.Ordinal)), rule + ": " + text);
        Hit("MQ3041", "is n-ary");
        Hit("MQ3041", "is the part in composition");
        // Part 2 allows several types at both ends (several-types-design.md decision 53): relation 'Both' is valid.
        Assert.DoesNotContain(rows, r => r.Rule == "MQ3041" && r.Message.Contains("both ends", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Element == "01KST0RXN00000000000000006" && r.Rule == "MQ3041");
        Hit("MQ3042", "lists only entity 'Tool'");
        Hit("MQ3043", "which entity 'Part' already covers");
        Hit("MQ3043", "which carries stereotype 'approvable', also listed");
        Hit("MQ3043", "which stereotype 'approvable' already covers, since it derives from entity 'Document'");
        Hit("MQ2003", "Stereotype 'nope'");
        Hit("MQ9211", "names stereotype 'table-only'");
        Hit("MQ4009", "sets only ignore");
        Hit("MQ4115", "stores nothing for it");
        Hit("MQ4116", "of the binding of 'Comment' names end 'attachedTo', which leads to several types");
        Hit("MQ4116", "of the binding of 'Tool' names end 'comment' of relation 'Filed'");
        Hit("MQ4116", "Field 1 of query 'Comments'");
        Hit("MQ4116", "Collection 0 of query 'Comments'");
        Hit("MQ4116", "Field 0 of collection 0 of query 'Comments'");
        Hit("MQ4116", "Collection 0 of query 'PartNotes' names end 'comment' of relation 'AttachedTo'");
        Hit("MQ4116", "Field 1 of query 'ToolFiling' names end 'comment' of relation 'Filed'");
        Hit("MQ7105", "has a column for end 'attachedTo', which leads to several types");
        Hit("MQ7105", "has a column for end 'concernedBy' of relation 'Concerns'");
        Hit("MQ7105", "holds rows of relation 'AttachedTo'");
        Hit("MQ3116", "can't govern any type the object in_state reaches");
        Hit("MQ9305", "links to 't1' through end 'AttachedTo.attachedTo'");
        Hit("MQ9305", "is an object of entity 'Tool', but end 'AttachedTo.attachedTo' leads to");

        var json = new JsonObject
        {
            ["about"] = "The engine's findings for the rules below on the project in .maquettiste, sorted by rule, element id, pointer and message. Written by SeveralTypesParityTests with MAQUETTISTE_RECORD=1; the editor's mock must give the same.",
            ["rules"] = new JsonArray([.. Rules.Select(r => JsonValue.Create(r))]),
            ["diagnostics"] = new JsonArray([.. rows.Select(r => new JsonObject
            {
                ["rule"] = r.Rule, ["elementId"] = r.Element, ["pointer"] = r.Pointer, ["message"] = r.Message, ["severity"] = r.Severity,
            })]),
        };
        var text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        var path = Path.Combine(Folder, "diagnostics.json");
        if (Recording)
            File.WriteAllText(path, text, new UTF8Encoding(false));
        Assert.True(File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text,
            "src/editor/tests/fixtures/several-types/diagnostics.json is out of date: run this test with MAQUETTISTE_RECORD=1.\n" + text);
    }
}
