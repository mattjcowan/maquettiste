using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary><c>get_calculated</c> (next-phase-2-design.md section 5.5): the calculated values of the objects asked for, and nothing else.</summary>
public sealed partial class AgentToolsTests
{
    /// <summary>A calculation file on entities and one on attributes: an <c>api_name</c> of each entity, and a label of each attribute.</summary>
    internal const string ApiNames = """
        maquettiste.calculated({
          name: "apiName",
          kinds: ["entity"],
          schema: { type: "string", pattern: "^[a-z_]+$" },
          description: "The entity's name in the API.",
          value(entity) { return entity.name.replace(/([a-z])([A-Z])/g, "$1_$2").toLowerCase(); },
        });
        maquettiste.calculated({
          name: "label",
          kinds: ["attribute"],
          schema: { type: "string" },
          value(attribute) { return attribute.owner.name + "." + attribute.name; },
        });

        """;

    [Fact]
    public async Task Get_calculated_evaluates_only_the_ids_asked()
    {
        await using var r = EditorRepo.Create(packs: false);
        r.Repo.WriteFile(".maquettiste/extensions/calculated/names.js", ApiNames);
        // A store of its own over fresh services, so its calculation service counts this test's evaluations only.
        var services = EngineServices.Create(r.Repo.Options);
        await using var store = new ModelStore(r.Repo.Options, services);
        var catalog = new AgentTools(store, new GenerationService(store, r.Repo.Options), new AgentToolsOptions { RepoRoot = r.Repo.RepoRoot });
        async Task<JsonNode> OkAsync(object args)
        {
            var answer = await catalog.CallAsync("get_calculated", JsonSerializer.SerializeToElement(args), Ct);
            Assert.False(answer.IsError, answer.Text);
            return JsonNode.Parse(answer.Text)!;
        }

        var invoice = await OkAsync(new { ids = new[] { EditorRepo.InvoiceId, "01J00000000000000000000000" } });
        var item = Assert.Single(invoice["items"]!.AsArray())!;
        Assert.Equal((EditorRepo.InvoiceId, "entity", "Invoice", "invoice"),
            ((string?)item["id"], (string?)item["kind"], (string?)item["name"], (string?)item["calculated"]!["apiName"]));
        Assert.Null(item["holder"]);
        Assert.Equal(["01J00000000000000000000000"], invoice["missing"]!.AsArray().Select(m => (string?)m));
        Assert.Equal("apiName", (string?)Assert.Single(invoice["declarations"]!.AsArray())!["name"]);
        Assert.Empty(invoice["diagnostics"]!.AsArray());
        Assert.Equal(1, services.Calculations!.Evaluations);

        // An attribute id: its own value only (the entity's apiName is not evaluated), and names keep only those asked.
        var attributeId = (await store.GetElementAsync(EditorRepo.CustomerId, Ct))!.Element is Engine.Model.Entity { Attributes: [var first, ..] } ? first.Id : null;
        Assert.NotNull(attributeId);
        var attribute = await OkAsync(new { ids = new[] { attributeId }, names = new[] { "label" } });
        var labelled = Assert.Single(attribute["items"]!.AsArray())!;
        Assert.Equal("attribute", (string?)labelled["kind"]);
        Assert.StartsWith("Customer.", (string?)labelled["calculated"]!["label"], StringComparison.Ordinal);
        Assert.Equal(2, services.Calculations.Evaluations);
        var none = await OkAsync(new { ids = new[] { EditorRepo.CustomerId }, names = new[] { "label" } });
        Assert.Empty(Assert.Single(none["items"]!.AsArray())!["calculated"]!.AsObject());
        Assert.Equal(2, services.Calculations.Evaluations);

        // Asking again reuses the shared resolution's values.
        await OkAsync(new { ids = new[] { EditorRepo.InvoiceId } });
        Assert.Equal(2, services.Calculations.Evaluations);

        var empty = await catalog.CallAsync("get_calculated", JsonSerializer.SerializeToElement(new { ids = Array.Empty<string>() }), Ct);
        Assert.Equal("bad-request", (string?)JsonNode.Parse(empty.Text)!["code"]);
        var tooMany = await catalog.CallAsync("get_calculated", JsonSerializer.SerializeToElement(new { ids = Enumerable.Repeat(EditorRepo.InvoiceId, 101) }), Ct);
        Assert.Equal("bad-request", (string?)JsonNode.Parse(tooMany.Text)!["code"]);
    }
}
