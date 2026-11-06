using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The lifecycle output of csharp-dapper (lifecycle-design.md section 9): the pack's <c>lifecycle-codes</c> case matches its expected
/// tree; codes that don't form a map get constants only; and a lifecycle with one subject keeps today's states file in either form.
/// The case's output compiling and its mapping round-tripping is in <see cref="CompileTests"/>; the TypeScript sample's in
/// <see cref="TypeScriptPackTests"/>.
/// </summary>
public sealed class LifecycleCodeTests
{
    private const string TicketFlow = "out/Processes/TicketFlow/TicketFlowStates.cs";

    [Fact]
    public async Task The_lifecycle_codes_case_matches_its_expected_tree()
    {
        using var repo = PackRepo.LifecycleCodes();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "lifecycle-codes", "expected"), repo.PathOf("out"));
    }

    [Fact]
    public async Task Codes_that_do_not_form_a_map_get_constants_only()
    {
        // Open gets a code above New's and Assigned's, so two coded states sit on one path: allowed with no binding (decision 4).
        using var repo = PackRepo.LifecycleCodes();
        repo.EditJson(".maquettiste/model/processes/ticket-flow.json", p => p["states"]![0]!["code"] = 5);
        await repo.GenerateCleanlyAsync();

        var text = repo.Read(TicketFlow);
        Assert.Contains("    public const long Open = 5;\n", text, StringComparison.Ordinal);
        Assert.Contains("    public const long New = 10;\n", text, StringComparison.Ordinal);
        Assert.Contains("    public const long Closed = 90;\n}\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ToCode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FromCode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Moves", text, StringComparison.Ordinal);
        Assert.Contains("FromCode", repo.Read("out/Processes/OrderFlow/OrderFlowStates.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_coded_states_sharing_a_name_are_named_by_their_paths()
    {
        using var repo = PackRepo.LifecycleCodes();
        repo.EditJson(".maquettiste/model/processes/ticket-flow.json", p =>
        {
            var open = p["states"]![0]!["states"]!.AsArray();
            open.Add(new JsonObject { ["id"] = "01JMC000000000000000STA107", ["name"] = "Resolved", ["code"] = 25 });
        });
        await repo.GenerateCleanlyAsync();

        var text = repo.Read(TicketFlow);
        Assert.Contains("    public const long OpenResolved = 25;\n", text, StringComparison.Ordinal);
        Assert.Contains("    public const long Resolved = 30;\n", text, StringComparison.Ordinal);
        Assert.Contains("        OpenResolved => TicketFlowStates.OpenResolved,\n", text, StringComparison.Ordinal);
        Assert.Contains("        Resolved => TicketFlowStates.Resolved,\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lifecycle_with_one_enum_binding_keeps_its_states_file_in_either_form()
    {
        // The fixture's SalesOrderLifecycle lists its one subject; the one-subject form gives the same bytes, ToStatus included.
        const string states = ProcessTests.Generated + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleStates.cs";
        using var repo = PackRepo.Processes();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        var listed = repo.Read(states);
        Assert.Contains(" ToStatus(IReadOnlyList<string> states)", listed, StringComparison.Ordinal);

        repo.EditJson(".maquettiste/model/processes/sales-order-lifecycle.json", p =>
        {
            var process = p.AsObject();
            var entry = process["subjects"]![0]!;
            var (entity, attribute) = ((string)entry["entity"]!, (string)entry["boundAttribute"]!);
            process.Remove("subjects");
            process["subject"] = entity;
            process["boundAttribute"] = attribute;
        });
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.Equal(listed, repo.Read(states));
    }
}
