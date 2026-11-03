using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Store;
using static Maquettiste.Engine.Tests.CustomKinds.CustomFixture;

namespace Maquettiste.Engine.Tests.CustomKinds;

/// <summary>The <c>custom</c> kind and the custom kind files (next-phase-2-design.md section 4.2, task E20).</summary>
public sealed class CustomKindLoadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Custom_kind_files_load_from_extensions_kinds_only()
    {
        Assert.Equal(ModelFileKind.CustomKind, ModelPaths.Classify("extensions/kinds/setting.json"));
        Assert.Null(ModelPaths.Classify("extensions/kinds/deeper/setting.json"));
        Assert.Null(ModelPaths.Classify("extensions/kinds/setting.js"));
        Assert.Equal(ModelFileKind.Extension, ModelPaths.Classify("extensions/setting.json"));

        await using var s = await BillingStore.OpenAsync(load: false);
        s.Harness.Write("extensions/kinds/setting.json", SettingKind);
        s.Harness.Write("extensions/kinds/deeper/flag.json", FlagKind);
        s.Harness.Write("extensions/kinds/broken.json", """{ "type": "broken" }""");
        var snapshot = await ReloadAsync(s);

        var setting = Assert.Single(snapshot.CustomKinds);
        Assert.Equal(("setting", "Setting", "Settings", ".maquettiste/extensions/kinds/setting.json"),
            (setting.Kind.Type, setting.Kind.DisplayName, setting.Kind.PluralName, setting.Path));
        Assert.Equal(["code", "unit", "value"], setting.Kind.Properties.EnumerateObject().Select(p => p.Name));
        Assert.Same(setting, snapshot.CustomKindOf("setting"));
        Assert.Null(snapshot.CustomKindOf("flag"));
        Assert.Null(snapshot.CustomKindOf("broken"));
        var broken = Assert.Single(snapshot.LoadDiagnostics, d => d.FilePath == ".maquettiste/extensions/kinds/broken.json");
        Assert.Equal("MQ1002", broken.Rule);
        Assert.DoesNotContain(snapshot.LoadDiagnostics, d => d.FilePath != null && d.FilePath.Contains("kinds/setting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_custom_element_of_an_undeclared_kind_is_MQ1019()
    {
        await using var s = await CustomFixture.OpenAsync();
        s.Harness.Write("model/custom/flag/billing-live.json", """
            { "kind": "custom", "id": "01JR6CK00000000000000000Z1", "name": "billing_live", "type": "flag", "content": { "on": true } }
            """);
        await s.Store.RescanAsync(verify: false, Ct);

        var diagnostics = await ValidateAsync(s);

        var finding = Assert.Single(diagnostics, d => d.Rule == "MQ1019");
        Assert.Equal(("01JR6CK00000000000000000Z1", "/type"), (finding.ElementId, finding.JsonPointer));
        Assert.Contains("extensions/kinds/flag.json", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.ElementId == DueDays && d.Severity == Diagnostics.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task A_custom_element_outside_its_type_folder_is_MQ1005()
    {
        await using var s = await CustomFixture.OpenAsync();
        Assert.Equal(".maquettiste/model/custom/setting/due-days.json", s.Model.GetDocument(DueDays)!.Path);
        Assert.StartsWith("{\n  \"$schema\": \"../../../.schema/v1/custom.json\",", s.Harness.Read("model/custom/setting/due-days.json"), StringComparison.Ordinal);

        s.Harness.Write("model/custom/other/due-days.json", s.Harness.Read("model/custom/setting/due-days.json"));
        File.Delete(s.Harness.Model("model/custom/setting/due-days.json"));
        var snapshot = await ReloadAsync(s);

        var misplaced = Assert.Single(snapshot.LoadDiagnostics, d => d.Rule == "MQ1005");
        Assert.Contains("belongs at .maquettiste/model/custom/setting/due-days.json", misplaced.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_type_moves_the_file()
    {
        await using var s = await CustomFixture.OpenAsync(true, ("setting", SettingKind), ("flag", FlagKind));
        var created = await CreateAsync(s, """{ "kind": "custom", "name": "billing_live", "type": "setting", "content": { "value": true } }""");
        var document = s.Model.GetDocument(created.Id!)!;
        Assert.Equal(".maquettiste/model/custom/setting/billing-live.json", document.Path);

        var saved = await s.Store.SaveAsync(created.Id!, BillingStore.Edit(document, o =>
        {
            o["type"] = "flag";
            o["content"] = new System.Text.Json.Nodes.JsonObject { ["on"] = true };
        }), document.Hash, ChangeSource.Cli, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Message)));
        Assert.Equal(".maquettiste/model/custom/flag/billing-live.json", s.Model.GetDocument(created.Id!)!.Path);
        Assert.False(s.Harness.Exists("model/custom/setting/billing-live.json"));
    }
}
