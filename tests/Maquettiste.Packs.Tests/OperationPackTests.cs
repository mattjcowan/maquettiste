using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The csharp-dapper unit <c>operation-contracts</c> (next-phase-2-design.md section 4.1, the first cut): golden output for
/// <c>send</c> on Invoice under <c>tests/fixtures/golden/csharp-dapper/operations/</c> (<c>MAQUETTISTE_UPDATE_GOLDEN=1</c> rewrites
/// it), and the shapes the golden does not show: a collection-level read returning a list of records, and an operation without a
/// subject, input or errors.
/// </summary>
public sealed class OperationPackTests
{
    private const string Contracts = "src/Generated/Operations/Billing";

    [Fact]
    public async Task Csharp_dapper_generates_the_send_request_and_result_types_from_the_operation_alone()
    {
        using var repo = PackRepo.Billing();
        repo.AddSendOperation();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "operations"), repo.PathOf("src/Generated/Operations"));
    }

    [Fact]
    public async Task A_collection_read_takes_no_key_and_an_operation_without_subject_or_input_takes_only_the_token()
    {
        using var repo = PackRepo.Billing();
        repo.Write(".maquettiste/model/operations/customer/search.json", """
            {
              "kind": "operation",
              "id": "01JR5NE0000000000000000021",
              "name": "search",
              "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "subject": "01J92P0V0ETQKXXP951CMMNHH3",
              "level": "collection",
              "effect": "read",
              "input": [ { "id": "01JR5NE0000000000000000022", "name": "text", "type": "string", "length": 100 } ],
              "outcomes": [ { "id": "01JR5NE0000000000000000023", "name": "found", "returns": { "entity": "01J92P0V0ETQKXXP951CMMNHH3", "list": true } } ],
              "errors": [ { "id": "01JR5NE0000000000000000024", "name": "too_broad", "code": "search.too_broad" } ]
            }

            """);
        repo.Write(".maquettiste/model/operations/close-month.json", """
            {
              "kind": "operation",
              "id": "01JR5NE0000000000000000031",
              "name": "closeMonth",
              "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "effect": "change",
              "outcomes": [ { "id": "01JR5NE0000000000000000032", "name": "closed" } ]
            }

            """);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        var search = repo.Read(Contracts + "/SearchCustomer.g.cs");
        Assert.Contains("public sealed record SearchCustomerRequest(string? Text);", search, StringComparison.Ordinal);
        Assert.Contains("public sealed record Found(IReadOnlyList<Customer> Value) : SearchCustomerResult;", search, StringComparison.Ordinal);
        Assert.Contains("public const string TooBroad = \"search.too_broad\";", search, StringComparison.Ordinal);
        Assert.Contains("Task<SearchCustomerResult> HandleAsync(SearchCustomerRequest request, CancellationToken cancellationToken);", search, StringComparison.Ordinal);

        var close = repo.Read(Contracts + "/CloseMonth.g.cs");
        Assert.Contains("public sealed record Closed : CloseMonthResult;", close, StringComparison.Ordinal);
        Assert.Contains("Task<CloseMonthResult> HandleAsync(CancellationToken cancellationToken);", close, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseMonthRequest", close, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseMonthErrors", close, StringComparison.Ordinal);
        Assert.Contains("/// <summary>Operation closeMonth.</summary>", close, StringComparison.Ordinal);
    }
}
