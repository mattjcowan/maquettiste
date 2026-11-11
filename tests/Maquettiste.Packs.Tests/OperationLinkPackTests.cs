using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// csharp-dapper with processes linked to operations (processes-operations-design.md decision 14 and section 15): it reads none of the links,
/// so the gate 3 fixture's process output stays the golden's with operations that fire nothing, and a model whose actions call operations and
/// whose service steps name them writes the same bytes as the model without those links; a call-only action stays a stub.
/// </summary>
public sealed class OperationLinkPackTests
{
    private const string Purchasing = "01JQPKG0000000000000000002";
    private const string PurchaseRequest = "01JQENT0000000000000000003";
    private const string ApprovalFile = ".maquettiste/model/processes/purchase-approval.json";
    private const string SendReminder = "01JQPRX0000000000000000018";
    private const string Notify = "01JR6PK0000000000000000001"; // a stub action the links give a call
    private const string Remind = "01JR6PK0000000000000000010"; // remind, input count (int32)
    private const string Check = "01JR6PK0000000000000000020"; // check, named by the service steps

    private static void AddOperations(PackRepo repo, bool stub = true)
    {
        repo.Write(".maquettiste/model/operations/purchase-request/remind.json", $$"""
            {"$schema":"../../../.schema/v1/operation.json","kind":"operation","id":"{{Remind}}","name":"remind","package":"{{Purchasing}}","subject":"{{PurchaseRequest}}",
             "effect":"change","input":[{"id":"01JR6PK0000000000000000011","name":"count","type":"int32","required":true}],"outcomes":[{"id":"01JR6PK0000000000000000012","name":"sent"}]}

            """);
        repo.Write(".maquettiste/model/operations/purchase-request/check.json", $$"""
            {"$schema":"../../../.schema/v1/operation.json","kind":"operation","id":"{{Check}}","name":"check","package":"{{Purchasing}}","subject":"{{PurchaseRequest}}",
             "effect":"read","outcomes":[{"id":"01JR6PK0000000000000000021","name":"done"}]}

            """);
        if (stub)
            repo.EditJson(ApprovalFile, process => process["actions"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"{{Notify}}","name":"notifyRequester"}""")));
    }

    private static void AddLinks(PackRepo repo) => repo.EditJson(ApprovalFile, process =>
    {
        foreach (var action in process["actions"]!.AsArray())
        {
            var id = (string?)action!["id"];
            if (id is not (SendReminder or Notify))
                continue;
            var input = id == Notify ? "01JR6PK0000000000000000031" : "01JR6PK0000000000000000032";
            action["call"] = JsonNode.Parse($$"""{"operation":"{{Remind}}","inputs":[{"id":"{{input}}","member":"01JR6PK0000000000000000011","value":{"value":1} }]}""");
        }

        Walk(process["states"]!.AsArray());

        static void Walk(JsonArray states)
        {
            foreach (var state in states)
            {
                foreach (var invoke in state!["invoke"]?.AsArray() ?? [])
                {
                    if ((string?)invoke!["type"] == "service")
                        invoke["operation"] = Check;
                }

                if (state["states"] is JsonArray children)
                    Walk(children);
            }
        }
    });

    private const string Reject = "01JQPRX0000000000000000017"; // reject, payload reason (text)

    /// <summary>
    /// remind also takes amount, costCentre and reason, and the stub action runs on reject too, whose payload carries reason: in both models,
    /// so only the links differ.
    /// </summary>
    private static void AddReadingInputs(PackRepo repo)
    {
        repo.EditJson(".maquettiste/model/operations/purchase-request/remind.json", remind =>
        {
            var input = remind["input"]!.AsArray();
            input.Add(JsonNode.Parse("""{"id":"01JR6PK0000000000000000013","name":"amount","type":"decimal","precision":12,"scale":2}"""));
            input.Add(JsonNode.Parse("""{"id":"01JR6PK0000000000000000014","name":"costCentre","type":"string","length":20}"""));
            input.Add(JsonNode.Parse("""{"id":"01JR6PK0000000000000000015","name":"reason","type":"text"}"""));
        });
        repo.EditJson(ApprovalFile, process =>
        {
            foreach (var transition in process["transitions"]!.AsArray())
            {
                if ((string?)transition!["event"] == Reject)
                    transition["actions"] = new JsonArray(Notify);
            }
        });
    }

    // The values read the context (count, amount), the object (costCentre) and the payload (reason); none is a literal.
    private static void AddReadingLinks(PackRepo repo) => repo.EditJson(ApprovalFile, process =>
    {
        foreach (var action in process["actions"]!.AsArray())
        {
            var id = (string?)action!["id"];
            if (id == SendReminder)
            {
                action["call"] = JsonNode.Parse($$"""
                    {"operation":"{{Remind}}","inputs":[{"id":"01JR6PK0000000000000000041","member":"01JR6PK0000000000000000011","value":{"path":["01JQATT0000000000000000202"]} },
                      {"id":"01JR6PK0000000000000000042","member":"01JR6PK0000000000000000013","value":{"path":["01JQATT0000000000000000201"]} }]}
                    """);
            }
            else if (id == Notify)
            {
                action["call"] = JsonNode.Parse($$"""
                    {"operation":"{{Remind}}","inputs":[{"id":"01JR6PK0000000000000000043","member":"01JR6PK0000000000000000011","value":{"path":["01JQATT0000000000000000202"]} },
                      {"id":"01JR6PK0000000000000000044","member":"01JR6PK0000000000000000014","value":{"path":["01JQATT0000000000000000013"]} },
                      {"id":"01JR6PK0000000000000000045","member":"01JR6PK0000000000000000015","value":{"path":["01JQATT0000000000000000203"]} }]}
                    """);
            }
        }
    });

    [Fact]
    public async Task Calls_whose_values_read_the_context_the_object_and_the_payload_write_the_same_bytes_as_the_model_without_them()
    {
        using var without = PackRepo.Processes();
        AddOperations(without);
        AddReadingInputs(without);
        using var with = PackRepo.Processes();
        AddOperations(with);
        AddReadingInputs(with);
        AddReadingLinks(with);
        Assert.Contains("01JQATT0000000000000000203", with.Read(ApprovalFile), StringComparison.Ordinal);
        await without.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        await with.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.Empty(Golden.Compare(without.PathOf("src"), with.PathOf("src")));
    }

    [Fact]
    public async Task Operations_that_fire_nothing_leave_the_process_output_as_the_golden()
    {
        using var repo = PackRepo.Processes();
        AddOperations(repo, stub: false);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        var golden = Fixtures.Path("golden", "csharp-dapper", "processes");
        foreach (var file in Directory.EnumerateFiles(golden, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(golden, file);
            Assert.True(File.ReadAllText(file) == repo.Read(Path.Combine("src", relative)), relative + " differs from the golden");
        }
    }

    [Fact]
    public async Task Calls_and_step_operations_write_the_same_bytes_as_the_model_without_them()
    {
        using var without = PackRepo.Processes();
        AddOperations(without);
        using var with = PackRepo.Processes();
        AddOperations(with);
        AddLinks(with);
        Assert.Contains("\"call\"", with.Read(ApprovalFile), StringComparison.Ordinal);
        Assert.Contains($"\"operation\": \"{Check}\"", with.Read(ApprovalFile), StringComparison.Ordinal);
        await without.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        await with.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.Empty(Golden.Compare(without.PathOf("src"), with.PathOf("src")));
    }
}
