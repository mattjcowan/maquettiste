using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The csharp-dapper units <c>operation-contracts</c> and <c>operation-endpoints</c> (next-phase-2-design.md section 4.1): golden
/// output for <c>send</c> on Invoice under <c>tests/fixtures/golden/csharp-dapper/operations/</c> (the contracts) and, beside it,
/// <c>attach</c> (file members and a file result) and <c>browse</c> (a paged list) on Customer under <c>operation-endpoints/</c> (the
/// endpoints, with <c>endpointsFolder</c> set; <c>MAQUETTISTE_UPDATE_GOLDEN=1</c> rewrites both), and the shapes the goldens do not
/// show: a collection-level read returning a list of records, an operation without a subject, input or errors, and inherited errors.
/// </summary>
public sealed class OperationPackTests
{
    private const string Contracts = "src/Generated/Operations/Billing";

    private const string Endpoints = "src/Generated/Api/Operations/Billing";

    [Fact]
    public async Task Csharp_dapper_generates_the_send_endpoint_request_and_response_types_from_the_operation_alone()
    {
        using var repo = PackRepo.Billing();
        repo.AddSendOperation();
        UseEndpointsFolder(repo);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "operations"), repo.PathOf("src/Generated/Operations"));

        var endpoint = repo.Read(Endpoints + "/SendInvoiceEndpoint.g.cs");
        Assert.Contains("public const string Route = \"/invoices/{id}/send\";", endpoint, StringComparison.Ordinal);
        Assert.Contains("SendInvoiceErrors.AlreadySent => 409,", endpoint, StringComparison.Ordinal);
        Assert.Contains("SendInvoiceErrors.MissingRecipient => 422,", endpoint, StringComparison.Ordinal);
        Assert.Contains("SendInvoiceResult.Waiting __later => Results.Accepted(null, __later.Value),", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_endpoints_folder_only_the_contracts_are_written()
    {
        using var repo = PackRepo.Billing();
        repo.AddSendOperation();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        Assert.True(File.Exists(repo.PathOf(Contracts + "/SendInvoice.g.cs")));
        Assert.Empty(Directory.EnumerateFiles(repo.PathOf("src"), "*Endpoint.g.cs", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Inherited_errors_files_and_paged_lists_reach_the_contracts_and_the_endpoint()
    {
        // The endpoint golden holds send's endpoint beside attach's and browse's.
        using var repo = PackRepo.Billing();
        repo.AddSendOperation();
        UseEndpointsFolder(repo);
        AddAttachAndBrowse(repo);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "operation-endpoints"), repo.PathOf("src/Generated/Api/Operations"));

        var attach = repo.Read(Contracts + "/AttachCustomer.g.cs");
        Assert.Contains("public sealed record AttachCustomerRequest(System.IO.Stream Scan, string? Note);", attach, StringComparison.Ordinal);
        Assert.Contains("public sealed record Receipt(System.IO.Stream Value) : AttachCustomerResult;", attach, StringComparison.Ordinal);
        Assert.Contains("public const string NotFound = \"not_found\";", attach, StringComparison.Ordinal);
        var attachEndpoint = repo.Read(Endpoints + "/AttachCustomerEndpoint.g.cs");
        Assert.Contains("var scan = __form.Files.GetFile(\"scan\")?.OpenReadStream();", attachEndpoint, StringComparison.Ordinal);
        Assert.Contains("Results.Stream(__file.Value, \"application/pdf\")", attachEndpoint, StringComparison.Ordinal);
        Assert.Contains("AttachCustomerErrors.RateLimited => 429,", attachEndpoint, StringComparison.Ordinal);
        Assert.Contains("AttachCustomerErrors.NotFound => 409,", attachEndpoint, StringComparison.Ordinal);

        var browse = repo.Read(Contracts + "/BrowseCustomer.g.cs");
        Assert.Contains("public sealed record BrowseCustomerFoundPage(IReadOnlyList<Customer> Items, string? Next)", browse, StringComparison.Ordinal);
        Assert.Contains("public const int MaxPageSize = 100;", browse, StringComparison.Ordinal);
        Assert.Contains("Task<BrowseCustomerResult> HandleAsync(string? cursor, int pageSize, CancellationToken cancellationToken);", browse, StringComparison.Ordinal);
        var browseEndpoint = repo.Read(Endpoints + "/BrowseCustomerEndpoint.g.cs");
        Assert.Contains("public const string Route = \"/customers/browse\";", browseEndpoint, StringComparison.Ordinal);
        Assert.Contains("routes.MapGet(Route, HandleAsync);", browseEndpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds <c>common-errors</c> (not_found, and rate_limited with httpStatus 429), <c>attach</c> on Customer (a required file input
    /// and a typed one, a file result, the stereotype's errors) and <c>browse</c> on Customer (a cursor-paged list).
    /// </summary>
    internal static void AddAttachAndBrowse(PackRepo repo)
    {
        repo.Write(".maquettiste/model/vocabularies/stereotypes/common-errors.json", """
            {
              "kind": "stereotype",
              "id": "01JR5NH0000000000000000001",
              "key": "common-errors",
              "name": "CommonErrors",
              "appliesTo": [ "operation" ],
              "errors": [ { "id": "01JR5NH0000000000000000002", "name": "not_found" }, { "id": "01JR5NH0000000000000000003", "name": "rate_limited", "properties": { "httpStatus": 429 } } ]
            }

            """);
        repo.Write(".maquettiste/model/operations/customer/attach.json", """
            {
              "kind": "operation",
              "id": "01JR5NH0000000000000000011",
              "name": "attach",
              "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "subject": "01J92P0V0ETQKXXP951CMMNHH3",
              "effect": "change",
              "stereotypes": [ "common-errors" ],
              "input": [
                { "id": "01JR5NH0000000000000000012", "name": "scan", "file": true, "mediaTypes": [ "application/pdf" ], "length": 1048576, "required": true },
                { "id": "01JR5NH0000000000000000013", "name": "note", "type": "string", "length": 200 }
              ],
              "outcomes": [ { "id": "01JR5NH0000000000000000014", "name": "receipt", "returns": { "file": true, "mediaTypes": [ "application/pdf" ] } } ]
            }

            """);
        repo.Write(".maquettiste/model/operations/customer/browse.json", """
            {
              "kind": "operation",
              "id": "01JR5NH0000000000000000021",
              "name": "browse",
              "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "subject": "01J92P0V0ETQKXXP951CMMNHH3",
              "level": "collection",
              "effect": "read",
              "outcomes": [ { "id": "01JR5NH0000000000000000022", "name": "found", "returns": { "entity": "01J92P0V0ETQKXXP951CMMNHH3", "list": true, "paging": "cursor", "maxPageSize": 100 } } ]
            }

            """);
    }

    /// <summary>Sets <c>endpointsFolder</c> to <c>Api</c>, under the pack's output root.</summary>
    internal static void UseEndpointsFolder(PackRepo repo) =>
        repo.EditJson(".maquettiste/maquettiste.json", settings => settings["packs"]!["csharp-dapper"]!["parameters"] = new System.Text.Json.Nodes.JsonObject { ["endpointsFolder"] = "Api" });

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
