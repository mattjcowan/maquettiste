using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The template helper <c>referenced_by</c>, on a copy of the billing fixture (<see cref="E2ERepo"/>): an invented kind <c>policy</c>, an
/// extension whose property <c>meets</c> links to it, and a pack with one page per policy. The page must follow the links: a file that
/// starts linking to a policy renders the policy's page again, though the policy's own file did not change.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ReferencedByTests
{
    private const string Policy = "01JRRB00000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<E2ERepo> CopyAsync(string template)
    {
        var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/kinds/policy.json", """{ "type": "policy", "properties": { "text": { "type": "string" } } }""");
        repo.Repo.WriteFile(".maquettiste/extensions/traces.json", """
            { "name": "traces", "appliesTo": { "kinds": ["entity", "attribute"] },
              "properties": {
                "meets": { "type": "array", "items": { "type": "string", "x-ref": { "kind": "custom:policy", "optional": true } } },
                "shaped_by": { "type": "string", "x-ref": { "kind": "custom:policy", "optional": true } } } }
            """);
        var pack = Path.Combine(repo.Repo.ModelRoot, "templates", "links");
        Directory.CreateDirectory(pack);
        await File.WriteAllTextAsync(Path.Combine(pack, "pack.json"), """
            {"name": "links", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "page", "template": "page.scriban", "for": "each custom:policy", "output": "src/Generated/links/{{ custom.name }}.txt"}]}
            """, Ct);
        await File.WriteAllTextAsync(Path.Combine(pack, "page.scriban"), template, Ct);
        await repo.RestartAsync();
        await repo.CreateElementAsync($$"""{ "kind": "custom", "id": "{{Policy}}", "name": "keeps_lines", "type": "policy", "content": { "text": "An issued invoice keeps its lines." } }""");
        return repo;
    }

    private static void Link(JsonObject holder, string property = "meets") =>
        holder["properties"] = new JsonObject { [property] = property == "meets" ? new JsonArray(JsonValue.Create(Policy)) : JsonValue.Create(Policy) };

    private static JsonObject AttributeOf(JsonObject entity, string id) =>
        entity["attributes"]!.AsArray().Select(a => a!.AsObject()).Single(a => (string?)a["id"] == id);

    private static async Task<List<string>> RenderedAsync(E2ERepo repo)
    {
        var result = await repo.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        return [.. result.Plan!.Units.Where(u => !u.Skipped).Select(u => u.Key).Where(k => k.StartsWith("links/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_row_says_what_links_and_through_which_field_for_elements_and_their_parts()
    {
        await using var repo = await CopyAsync(
            "{{ for l in (referenced_by custom) }}{{ l.kind }} {{ l.id }} {{ l.field }} {{ l.status }} {{ l.from.name }} {{ l.owner.name }}\n{{ end }}");
        await repo.EditAsync(E2ERepo.CustomerId, entity =>
        {
            Link(entity);
            entity["status"] = "retired";
            Link(AttributeOf(entity, E2ERepo.CustomerNameAttributeId), "shaped_by");
        });

        await repo.ApplyAsync();

        // Ordinal by holder id: the entity 01J92P0V0ET…, then its attribute 01J92P0V0MS….
        Assert.Equal(
            $"entity {E2ERepo.CustomerId} meets retired Customer Customer\nattribute {E2ERepo.CustomerNameAttributeId} shaped_by  name Customer\n",
            repo.Repo.ReadFile("src/Generated/links/keeps_lines.txt"));
    }

    [Fact]
    public async Task A_field_keeps_the_links_of_that_field_and_an_id_works_like_the_element()
    {
        await using var repo = await CopyAsync(
            "meets:{{ for l in (referenced_by custom 'meets') }}{{ l.from.name }} {{ end }}|shaped_by:{{ for l in (referenced_by custom.id 'shaped_by') }}{{ l.from.name }} {{ end }}|other:{{ (referenced_by custom 'other').size }}|unknown:{{ (referenced_by 'no-such-id').size }}");
        await repo.EditAsync(E2ERepo.CustomerId, entity => Link(entity));
        await repo.EditAsync(E2ERepo.ProductId, entity => Link(entity, "shaped_by"));

        await repo.ApplyAsync();

        Assert.Equal("meets:Customer |shaped_by:Product |other:0|unknown:0", repo.Repo.ReadFile("src/Generated/links/keeps_lines.txt"));
    }

    [Fact]
    public async Task A_page_renders_again_when_a_file_starts_or_stops_linking_and_when_a_holder_changes()
    {
        await using var repo = await CopyAsync("{{ for l in (referenced_by custom) }}{{ l.from.name }}={{ l.status }}\n{{ end }}");
        await repo.ApplyAsync();
        Assert.Equal("", repo.Repo.ReadFile("src/Generated/links/keeps_lines.txt"));
        Assert.Empty(await RenderedAsync(repo));

        // The policy's own file does not change in any of these.
        await repo.EditAsync(E2ERepo.CustomerId, entity => Link(entity));
        Assert.Equal(["links/page:" + Policy], await RenderedAsync(repo));
        await repo.ApplyAsync();
        Assert.Equal("Customer=\n", repo.Repo.ReadFile("src/Generated/links/keeps_lines.txt"));

        await repo.EditAsync(E2ERepo.CustomerId, entity => entity["status"] = "retired");
        Assert.Equal(["links/page:" + Policy], await RenderedAsync(repo));
        await repo.ApplyAsync();
        Assert.Equal("Customer=retired\n", repo.Repo.ReadFile("src/Generated/links/keeps_lines.txt"));

        await repo.EditAsync(E2ERepo.CustomerId, entity => entity.Remove("properties"));
        Assert.Equal(["links/page:" + Policy], await RenderedAsync(repo));
        await repo.ApplyAsync();
        Assert.Equal("", repo.Repo.ReadFile("src/Generated/links/keeps_lines.txt"));
        Assert.Empty(await RenderedAsync(repo));
    }

    [Fact]
    public async Task An_element_or_id_is_required()
    {
        var unit = await Adhoc.RenderAsync("{{ referenced_by null }}");

        var error = unit.Error();
        Assert.Equal("MQ6006", error.Rule);
        Assert.Contains("`referenced_by` takes an element or an element id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_helper_of_a_pack_cannot_take_the_name() =>
        Assert.Contains("referenced_by", (IReadOnlySet<string>)Engine.Rendering.BuiltinHelpers.Names);
}
