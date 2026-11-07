using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Whether the project uses freezing is one resolved flag, <c>model.freezing.used</c>, keyed only on the freezing naming key
/// (rights-design.md decision 28). The interpreter unit, Freezing.cs and the scenario tests ask it, not the events, transitions and rights
/// of every process, so an unrelated edit renders no more units than it did before freezing.
/// </summary>
public sealed class FreezingNamingTests
{
    // The verbs case uses no rights and doesn't freeze: an operation's description renders the seven units 0.15.0 rendered. Part 2 as
    // first written rendered nine, since the interpreter and Freezing.cs read every verb's events.
    [Fact]
    public async Task An_operation_edit_in_the_verbs_case_renders_what_it_rendered_before_freezing()
    {
        using var repo = PackRepo.Verbs();
        await repo.GenerateCleanlyAsync();
        Edit(repo, ".maquettiste/model/operations/assign-reviewer.json", o => o["description"] = "Assigns a reviewer to the document.");
        var again = await repo.GenerateCleanlyAsync();
        Assert.Equal(7, again.UnitsRendered);
    }

    // The rights case uses rights and doesn't freeze: a named right's description renders the nine units 0.15.0 rendered. Part 2 as first
    // written rendered eleven, since the interpreter and Freezing.cs read every right's whileFrozen.
    [Fact]
    public async Task A_named_right_description_in_the_rights_case_renders_no_extra_unit()
    {
        using var repo = PackRepo.Rights();
        await repo.GenerateCleanlyAsync();
        Edit(repo, ".maquettiste/model/rights/secret-only-owner.json", o => o["description"] = "Only its owner may change a secret document, whoever asks.");
        var again = await repo.GenerateCleanlyAsync();
        Assert.Equal(9, again.UnitsRendered);
    }

    private static void Edit(PackRepo repo, string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(repo.Read(path))!.AsObject();
        change(node);
        repo.Write(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
