using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The process-docs pages name the ends a right and a trigger point speak of links through by the end's navigation, else its role: an end has
/// no name, so the pages used to say "on links through , ". The pack's <c>trigger-points</c> case gains a relation from Document to its
/// attachments, a right on <c>connect</c> and a check on <c>connect</c> and <c>disconnect</c> narrowed to links through two ends (one of a
/// relation from Document to Document); the pages are pinned under <c>tests/fixtures/golden/process-docs/link-ends</c>.
/// </summary>
public sealed partial class ProcessDocsTests
{
    [Fact]
    public async Task A_right_and_a_trigger_point_with_ends_name_the_ends_by_navigation_or_role()
    {
        using var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "process-docs"), Path.Combine(repo.Repo.ModelRoot, "templates", "process-docs"));
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "process-docs", "tests", "trigger-points", "model"), Path.Combine(repo.Repo.ModelRoot, "model"));
        repo.Write(".maquettiste/maquettiste.json", """
            {
              "formatVersion": 1,
              "name": "link-ends",
              "outputs": { "allow": [ { "path": "docs" } ] },
              "packs": { "process-docs": { "output": "docs" } }
            }

            """);
        repo.Write(".maquettiste/model/entities/attachment.json", """
            {"kind":"entity","id":"01JTP0QE000000000000000E02","name":"Attachment","package":"01JTP000000000000000000P01",
             "key":{"attributes":["01JTP0QE000000000000000A21"]},"attributes":[{"id":"01JTP0QE000000000000000A21","name":"id","type":"uuid","required":true}]}

            """);
        repo.Write(".maquettiste/model/relations/document-attachments.json", """
            {"kind":"relation","id":"01JTP0QE000000000000000N01","name":"DocumentAttachments","package":"01JTP000000000000000000P01","ends":[
              {"id":"01JTP0QE000000000000000N02","entity":"01JTP000000000000000000E01","role":"document","max":1},
              {"id":"01JTP0QE000000000000000N03","entity":"01JTP0QE000000000000000E02","role":"attachment","navigation":"attachments","max":"*"}]}

            """);
        repo.Write(".maquettiste/model/relations/document-references.json", """
            {"kind":"relation","id":"01JTP0QE000000000000000N11","name":"DocumentReferences","package":"01JTP000000000000000000P01","ends":[
              {"id":"01JTP0QE000000000000000N12","entity":"01JTP000000000000000000E01","role":"citing","navigation":"citedBy","max":"*"},
              {"id":"01JTP0QE000000000000000N13","entity":"01JTP000000000000000000E01","role":"cited","max":"*"}]}

            """);
        repo.EditJson(".maquettiste/model/processes/document-lifecycle.json", process =>
        {
            process["rights"] = JsonNode.Parse("""
                [{"id":"01JTP0QE000000000000000R01","name":"NoNewLinksOnceReleased","access":"deny","verbs":["connect"],
                  "ends":["01JTP0QE000000000000000N03","01JTP0QE000000000000000N12"],"states":["01JTP000000000000000000S03"]}]
                """);
            process["triggerPoints"]!.AsArray().Add(JsonNode.Parse("""
                {"id":"01JTP0QE000000000000000Q01","name":"LinksNeedATitle","type":"check","verbs":["connect","disconnect"],
                 "ends":["01JTP0QE000000000000000N03","01JTP0QE000000000000000N12"],"condition":{"text":"coalesce(subject.title, '') <> ''"},
                 "description":"A document needs a title before its links change."}
                """));
        });

        await repo.GenerateCleanlyAsync();
        var page = File.ReadAllText(repo.PathOf("docs/processes/document-lifecycle.md"));
        Assert.Contains("`connect` on links through `attachments`, `citedBy`", page, StringComparison.Ordinal);
        Assert.Contains("links through `attachments`, `citedBy`", page, StringComparison.Ordinal);
        Assert.DoesNotContain("links through , ", page, StringComparison.Ordinal);
        Golden.AssertMatches(Fixtures.Path("golden", "process-docs", "link-ends"), repo.PathOf("docs"));
    }
}
