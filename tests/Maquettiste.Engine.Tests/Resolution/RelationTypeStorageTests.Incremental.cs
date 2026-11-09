using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;
using Maquettiste.Engine.Validation;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The relaxed MQ4009 on <c>discriminatorValue</c> (several-types-design.md decision 42, section 19.7) follows every change that moves what an
/// end with several types covers: a stereotype or package save that moves carriers, and a relation save, before the change and after it.
/// Each save's findings on the files that set a code agree with a whole-model validation of the saved model.
/// </summary>
public sealed partial class RelationTypeStorageTests
{
    private const string Linkable = "01JSTR00000000000000000961", Tracked = "01JSTR00000000000000000962";

    private const string AssemblyCodeFinding = "Entity 'Assembly' is not part of an inheritance hierarchy, so it has no discriminator.";

    /// <summary>AttachedTo covers Part and whatever carries <c>linkable</c>; Assembly carries <c>tracked</c> and sets a code in its mapping.</summary>
    private static Task<(LoaderHarness Harness, ModelStore Store)> OpenCarriersAsync(string trackedImplies) => OpenAsync(h =>
    {
        // MQ4009 as a warning, so a save that brings it back is saved and its findings can be compared with a whole-model validation.
        var settings = JsonNode.Parse(h.Read("maquettiste.json"))!;
        settings["validation"] = JsonNode.Parse("""{"rules":{"MQ4009":"warning"}}""");
        h.Write("maquettiste.json", settings.ToJsonString());
        h.Write("model/stereotypes/linkable.json", $$"""{"kind":"stereotype","id":"{{Linkable}}","key":"linkable","name":"Linkable","appliesTo":["entity"]}""");
        h.Write("model/stereotypes/tracked.json", $$"""{"kind":"stereotype","id":"{{Tracked}}","key":"tracked","name":"Tracked","appliesTo":["entity"]{{trackedImplies}}}""");
        h.Write("model/entities/assembly.json", EntityFile(SAssembly, "Assembly", stereotype: "tracked",
            attributes: $$""","key":{"attributes":["{{N(P, 102)}}"]},"attributes":[{"id":"{{N(P, 102)}}","name":"id","type":"uuid"}]"""));
        h.Write("model/relations/attached-to.json", $$"""
            {"kind":"relation","id":"{{Attached}}","name":"AttachedTo","package":"{{Sales}}","ends":[
             {"id":"{{CommentEnd}}","entity":"{{SComment}}","role":"comment","navigation":"comments","max":"*"},
             {"id":"{{AttachedToEnd}}","types":[{"entity":"{{SPart}}"},{"stereotype":"linkable"}],"role":"attachedTo","navigation":"attachedTo","max":1}]}
            """);
        h.Write("model/relations/filed.json", $$"""
            {"kind":"relation","id":"{{Filed}}","name":"Filed","package":"{{Sales}}","ends":[
             {"id":"{{FolderEnd}}","entity":"{{SFolder}}","role":"folder","navigation":"folder","max":1},
             {"id":"{{ItemsEnd}}","types":[{"entity":"{{SPart}}"},{"entity":"{{SComment}}"}],"role":"items","navigation":"items","max":"*"}]}
            """);
    }, assemblyMapping: ",\"discriminatorValue\":\"A\"");

    private static async Task<SaveResult> SaveAsync(ModelStore store, string id, Action<JsonObject> edit)
    {
        var document = store.Current!.GetDocument(id)!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        var saved = await store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, saved.Outcome + ": " + string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return saved;
    }

    /// <summary>
    /// The save checked Assembly's mapping again, and its findings there agree with a whole-model validation: present in both, or in
    /// neither while the save's scope reached the file.
    /// </summary>
    private static async Task AssertAgreesAsync(LoaderHarness h, ModelStore store, SaveResult saved, string savedId, bool expected)
    {
        var mappingId = N(P, 903);
        static List<string> On(IEnumerable<Diagnostic> found, string id) => [.. found.Where(d => d.Rule == "MQ4009" && d.ElementId == id).Select(d => d.Message)];
        var full = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.Equal(expected ? [AssemblyCodeFinding] : [], On(full, mappingId));
        Assert.Equal(On(full, mappingId), On(saved.Diagnostics, mappingId));
        if (!expected)
            Assert.Contains(mappingId, ModelValidator.TargetIds(store.Current!, new ValidationScope([savedId], IncludeReferrers: true)));
    }

    [Fact]
    public async Task A_stereotype_save_that_moves_carriers_brings_back_the_files_that_set_a_code()
    {
        var (h, store) = await OpenCarriersAsync("");
        using var _ = h;
        await using var __ = store;
        Assert.Contains(AssemblyCodeFinding, Of(await ActorKit.ValidateAsync(h, store.Current!), "MQ4009", "/mappings/assembly").Select(x => x.Message));

        // tracked now implies linkable: Assembly carries it, the end covers Assembly, and its code is allowed.
        var implies = await SaveAsync(store, Tracked, s => s["implies"] = new JsonArray("linkable"));
        await AssertAgreesAsync(h, store, implies, Tracked, expected: false);

        // And back: the finding comes back on a file only the model before the save reaches.
        var plain = await SaveAsync(store, Tracked, s => s.Remove("implies"));
        await AssertAgreesAsync(h, store, plain, Tracked, expected: true);
    }

    [Fact]
    public async Task A_package_save_that_moves_carriers_brings_back_the_files_that_set_a_code()
    {
        var (h, store) = await OpenCarriersAsync("");
        using var _ = h;
        await using var __ = store;
        var defaults = await SaveAsync(store, Sales, p => p["defaultStereotypes"] = new JsonArray("linkable"));
        await AssertAgreesAsync(h, store, defaults, Sales, expected: false);
        var none = await SaveAsync(store, Sales, p => p.Remove("defaultStereotypes"));
        await AssertAgreesAsync(h, store, none, Sales, expected: true);
    }

    [Fact]
    public async Task A_relation_save_brings_back_the_files_of_entities_it_covered_before_and_covers_after()
    {
        var (h, store) = await OpenCarriersAsync(""","implies":["linkable"]""");
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain(AssemblyCodeFinding, Of(await ActorKit.ValidateAsync(h, store.Current!), "MQ4009").Select(x => x.Message));

        // The end names Folder instead of the stereotype: Assembly is no longer covered.
        var narrowed = await SaveAsync(store, Attached, r => r["ends"]![1]!["types"] = new JsonArray(
            new JsonObject { ["entity"] = SPart }, new JsonObject { ["entity"] = SFolder }));
        await AssertAgreesAsync(h, store, narrowed, Attached, expected: true);

        var widened = await SaveAsync(store, Attached, r => r["ends"]![1]!["types"] = new JsonArray(
            new JsonObject { ["entity"] = SPart }, new JsonObject { ["entity"] = SAssembly }));
        await AssertAgreesAsync(h, store, widened, Attached, expected: false);
    }
}
