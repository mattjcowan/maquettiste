using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;
using Xunit.Sdk;

namespace Maquettiste.Functions.Tests;

/// <summary>
/// The functions and <c>docs/api/openapi.yaml</c> agree (phase2-design.md section 3.9): the same routes both ways, and the validator
/// the handler tests use really rejects what the contract does not allow (so a passing check means something).
/// </summary>
public sealed class ContractTests
{
    [Fact]
    public void Every_handler_route_is_an_operation_of_the_contract_and_every_operation_has_a_handler()
    {
        var handlers = TestRouter.Routes.Select(r => (r.Verb.ToUpperInvariant(), r.Template)).Order().ToList();
        var operations = Contract.Operations.Select(o => (o.Verb, o.Path)).Order().ToList();

        Assert.Equal(operations, handlers);
        Assert.Equal(68, operations.Count); // the phase 2 subset of S16 (phase2-design.md section 3.7), E5b, E5c and E5f, reference-types-seeds-localization.md section 3.9, the branding icon's upload and read, the several-seed CSV import, the validation rule catalog, the six process operations (phase-3-design.md section 4.4), the three bulk reads (elements in pages, kinds, the resolved model), the two delete plans (one element, several) and the pack removal
    }

    [Fact]
    public void The_host_answered_healthz_is_left_out_of_the_handler_check()
    {
        Assert.DoesNotContain(Contract.Operations, o => o.Path == "/healthz");
        Assert.Equal("host", Contract.OpenApi["paths"]!["/healthz"]!["get"]!["x-maquettiste-handler"]!.GetValue<string>());
    }

    [Fact]
    public void The_validator_rejects_a_summary_with_a_bad_id_and_a_missing_member()
    {
        var good = JsonNode.Parse("""
            { "id": "01J92P0V0FJ23CGSNKM7P1W5V7", "kind": "entity", "name": "Invoice", "package": null, "tags": [], "category": null,
              "stereotypes": [], "hash": "cdac722e6a2f7418b8f02fb4ab7097f297bd46bc67f46d9f61c45dfda534749d", "path": ".maquettiste/model/entities/invoice.json" }
            """)!;
        Contract.AssertSchema("ElementSummary", good);

        var badId = good.DeepClone();
        badId["id"] = "invoice";
        Assert.ThrowsAny<XunitException>(() => Contract.AssertSchema("ElementSummary", badId));
        var missing = good.DeepClone().AsObject();
        missing.Remove("stereotypes");
        Assert.ThrowsAny<XunitException>(() => Contract.AssertSchema("ElementSummary", missing));
        var wrongKind = good.DeepClone();
        wrongKind["kind"] = "widget";
        Assert.ThrowsAny<XunitException>(() => Contract.AssertSchema("ElementSummary", wrongKind));
    }

    [Fact]
    public void The_validator_follows_references_into_the_model_schemas()
    {
        // ElementDocument.json is a ModelDocument: oneOf the schemas/v1 kind files, which the contract references relatively.
        var document = JsonNode.Parse("""{ "kind": "entity", "id": "01J92P0V0FJ23CGSNKM7P1W5V7", "name": "Invoice", "attributes": [ { "name": "x", "type": "string" } ] }""")!;
        Assert.ThrowsAny<XunitException>(() => Contract.AssertSchema("ModelDocument", document));
        document["attributes"]![0]!["id"] = "01J92P0V0WKRGKH7YBKA2V30NC";
        Contract.AssertSchema("ModelDocument", document);
    }

    [Fact]
    public void The_validator_rejects_a_problem_with_an_unknown_code()
    {
        Contract.AssertSchema("Problem", JsonNode.Parse("""{ "title": "t", "status": 404, "code": "not-found" }""")!);
        Assert.ThrowsAny<XunitException>(() => Contract.AssertSchema("Problem", JsonNode.Parse("""{ "title": "t", "status": 404, "code": "gone" }""")!));
    }

    [Fact]
    public void Every_operation_documents_its_responses_and_every_realtime_event_a_payload()
    {
        foreach (var (verb, path, operationId) in Contract.Operations)
        {
            var responses = Contract.OpenApi["paths"]![path]![verb.ToLowerInvariant()]!["responses"]!.AsObject();
            Assert.True(responses.Count > 0, operationId);
        }

        foreach (var name in new[] { "model.changed", "validation.completed", "project.changed", "templates.changed", "packs.changed", "job.progress", "job.completed", "presence.changed" })
            Assert.NotNull(Contract.OpenApi["webhooks"]![name]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]);
    }
}
