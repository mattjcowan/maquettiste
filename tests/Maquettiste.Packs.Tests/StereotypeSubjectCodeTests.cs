using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The csharp-dapper lifecycle output when a subject entry names a stereotype (stereotype-sets-design.md decision 12): templates see one
/// subject per carrier, so a stereotype nothing carries yet still writes the states, and one carrier writes what the entity's own entry
/// writes.
/// </summary>
public sealed class StereotypeSubjectCodeTests
{
    private const string TicketFlow = "out/Processes/TicketFlow/TicketFlowStates.cs";

    private static PackRepo Repo(params string[] carriers)
    {
        var repo = PackRepo.LifecycleCodes();
        repo.Write(".maquettiste/model/vocabularies/stereotypes/tracked.json", """
            { "$schema": "../../../.schema/v1/stereotype.json", "kind": "stereotype", "id": "01JMC000000000000000STR001", "key": "tracked", "name": "Tracked", "appliesTo": [ "entity" ] }

            """);
        repo.EditJson(".maquettiste/model/processes/ticket-flow.json", p => p["subjects"] = JsonNode.Parse("""[ { "id": "01JMC000000000000000SBJ004", "stereotype": "tracked" } ]"""));
        foreach (var carrier in carriers)
            repo.EditJson(".maquettiste/model/entities/" + carrier + ".json", e => e["stereotypes"] = new JsonArray("tracked"));
        return repo;
    }

    [Fact]
    public async Task Zero_one_and_two_carriers_write_the_states()
    {
        string expected;
        using (var plain = PackRepo.LifecycleCodes())
        {
            await plain.GenerateCleanlyAsync();
            expected = plain.Read(TicketFlow);
        }

        using (var none = Repo())
        {
            await none.GenerateCleanlyAsync();
            Assert.Contains("class TicketFlowStates", none.Read(TicketFlow), StringComparison.Ordinal);
        }

        using (var one = Repo("ticket"))
        {
            await one.GenerateCleanlyAsync();
            Assert.Equal(expected, one.Read(TicketFlow));
        }

        using var two = Repo("ticket", "item");
        await two.GenerateCleanlyAsync();
        Assert.Contains("class TicketFlowStates", two.Read(TicketFlow), StringComparison.Ordinal);
    }
}
