using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// The batch operation <c>tags-to-property</c> (next-phase-1-design.md section 4.4): the tags of one prefix move into a typed custom
/// property of each holder, converted by the schema that declares it, all or nothing, with one MQ5001 per holder that cannot take its
/// value.
/// </summary>
public sealed class TagsToPropertyTests
{
    private const string TagsVocabularyId = "01J92P0V22Q6775D5GE10SN3RB";
    private const string TotalId = "01J92P0V0T6EA0XM025XQX8GBW";
    private const string InvoicePath = ".maquettiste/model/entities/invoice.json";
    private const string CustomerPath = ".maquettiste/model/entities/customer.json";
    private const string PaymentPath = ".maquettiste/model/entities/payment.json";
    private const string VocabularyPath = ".maquettiste/model/vocabularies/tags.json";

    private const string Workflow = """
        { "name": "workflow", "appliesTo": { "kinds": ["entity", "attribute"] },
          "properties": {
            "status": { "type": "string", "enum": ["draft", "paid"] },
            "priority": { "type": "integer" },
            "owner": { "type": "string", "x-ref": { "kind": "entity" } },
            "labels": { "type": "array", "items": { "type": "string" } } } }
        """;

    private static readonly string[] Declared =
        ["status:draft", "status:paid", "status:void", "priority:3", "priority:big", "owner:Customer", "label:urgent", "label:review"];

    private static CancellationToken Ct => EditorRepo.Ct;

    /// <summary>
    /// The billing fixture with the workflow extension, the prefixed tags declared in the (strict) global vocabulary, and the documents
    /// tagged on disk before the store loads.
    /// </summary>
    private static EditorRepo Repo(string[] invoice, string[] customer, string[] payment, string[]? total = null, Action<JsonObject>? paymentEdit = null)
    {
        var r = EditorRepo.Create(packs: false);
        Write(r, ".maquettiste/extensions/workflow.json", "extension.json", JsonNode.Parse(Workflow)!);
        Edit(r, VocabularyPath, "tag-vocabulary.json", n =>
        {
            foreach (var key in Declared)
                n["definitions"]!.AsArray().Add(new JsonObject { ["key"] = key });
        });
        Edit(r, InvoicePath, "entity.json", n =>
        {
            Tag(n, invoice);
            if (total is not null)
                Tag(n["attributes"]!.AsArray().OfType<JsonObject>().Single(a => (string)a["id"]! == TotalId), total);
        });
        Edit(r, CustomerPath, "entity.json", n => Tag(n, customer));
        Edit(r, PaymentPath, "entity.json", n =>
        {
            Tag(n, payment);
            paymentEdit?.Invoke(n);
        });
        return r;
    }

    private static void Tag(JsonObject holder, string[] tags)
    {
        var list = holder["tags"] as JsonArray ?? [];
        holder["tags"] = list;
        foreach (var tag in tags)
            list.Add(tag);
    }

    private static void Write(EditorRepo r, string path, string schema, JsonNode node) => r.Repo.WriteBytes(path, TestServices.Json.Write(node, schema, path));

    private static void Edit(EditorRepo r, string path, string schema, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(r.Repo.ReadFile(path))!.AsObject();
        change(node);
        Write(r, path, schema, node);
    }

    private static async Task<BatchResult> ApplyAsync(EditorRepo r, string operation)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return await r.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
    }

    private static void Saved(BatchResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));

    private static Entity EntityOf(EditorRepo r, string id) => r.Store.Current!.Get<Entity>(id)!;

    private static IReadOnlyList<string> Keys(EditorRepo r) => [.. r.Store.Current!.Get<TagVocabulary>(TagsVocabularyId)!.Definitions.Select(d => d.Key)];

    [Fact]
    public async Task A_tag_on_an_entity_check_stays_where_it_is_unless_a_schema_reaches_the_check()
    {
        await using var r = Repo(["status:draft"], [], ["status:paid"], paymentEdit: n => n["checks"] = JsonNode.Parse("""
            [ { "id": "01K9TPC0000000000000000001", "name": "always", "tags": ["status:paid"], "condition": { "op": "eq", "args": [{ "value": 1 }, { "value": 1 }] } } ]
            """));

        Saved(await ApplyAsync(r, """{"op":"tags-to-property","prefix":"status:","name":"status"}"""));

        var payment = EntityOf(r, EditorRepo.PaymentId);
        Assert.Equal("paid", payment.Properties["status"].GetString());
        Assert.Equal(["status:paid"], payment.Checks.Single().Tags);
        Assert.Empty(payment.Checks.Single().Properties);
    }

    [Fact]
    public async Task Prefixed_tags_move_into_a_typed_property_and_leave_the_vocabulary()
    {
        await using var r = Repo(["status:draft", "owner:Customer", "label:urgent", "label:review"], ["status:paid"], ["status:paid"], total: ["priority:3"]);

        Saved(await ApplyAsync(r, """{"op":"tags-to-property","prefix":"status:","name":"status"}"""));

        Assert.Equal("draft", EntityOf(r, EditorRepo.InvoiceId).Properties["status"].GetString());
        Assert.Equal("paid", EntityOf(r, EditorRepo.CustomerId).Properties["status"].GetString());
        Assert.Equal("paid", EntityOf(r, EditorRepo.PaymentId).Properties["status"].GetString());
        Assert.Equal(["billing", "owner:Customer", "label:urgent", "label:review"], EntityOf(r, EditorRepo.InvoiceId).Tags);
        Assert.Equal(["billing", "pii"], EntityOf(r, EditorRepo.CustomerId).Tags);
        // The moved tags' declarations go; a prefixed one nothing used (status:void) is not a moved tag and stays.
        Assert.DoesNotContain("status:draft", Keys(r));
        Assert.DoesNotContain("status:paid", Keys(r));
        Assert.Contains("status:void", Keys(r));
        Assert.Contains("priority:3", Keys(r));

        // A string array takes every suffix in list order, an x-ref the id its suffix names, an integer its parsed suffix.
        Saved(await ApplyAsync(r, """{"op":"tags-to-property","prefix":"label:","name":"labels"}"""));
        Saved(await ApplyAsync(r, """{"op":"tags-to-property","prefix":"owner:","name":"owner"}"""));
        Saved(await ApplyAsync(r, """{"op":"tags-to-property","prefix":"priority:","name":"priority"}"""));

        var invoice = EntityOf(r, EditorRepo.InvoiceId);
        Assert.Equal(["urgent", "review"], invoice.Properties["labels"].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(EditorRepo.CustomerId, invoice.Properties["owner"].GetString());
        Assert.Equal(["billing"], invoice.Tags);
        var total = invoice.Attributes.Single(a => a.Id == TotalId);
        Assert.Equal(3, total.Properties["priority"].GetInt32());
        Assert.Empty(total.Tags);
        Assert.Equal(["billing", "pii", "status:void", "priority:big"], Keys(r));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Rule is "MQ5001" or "MQ2007" or "MQ2010");
    }

    [Fact]
    public async Task A_misfit_suffix_or_undeclared_property_refuses_with_one_MQ5001_per_holder()
    {
        await using var r = Repo(["status:draft", "status:paid"], ["status:void"], ["status:paid"], total: ["priority:big"],
            paymentEdit: n => n["properties"] = new JsonObject { ["status"] = "draft" });
        var before = r.Files();

        // Two suffixes for one value, a suffix the enum lacks, and a holder that already holds another value: one MQ5001 each.
        var status = await ApplyAsync(r, """{"op":"tags-to-property","prefix":"status:","name":"status"}""");

        Assert.Equal(SaveOutcome.Invalid, status.Outcome);
        var found = status.Items.SelectMany(i => i.Diagnostics).ToList();
        Assert.Equal(
            [
                ("MQ5001", EditorRepo.CustomerId, CustomerPath, "/properties/status"),
                ("MQ5001", EditorRepo.InvoiceId, InvoicePath, "/properties/status"),
                ("MQ5001", EditorRepo.PaymentId, PaymentPath, "/properties/status"),
            ],
            found.Select(d => (d.Rule, d.ElementId!, d.FilePath!, d.JsonPointer!)).Order());
        Assert.Contains("one value, and 2 suffixes are left ('draft', 'paid')", found.Single(d => d.ElementId == EditorRepo.InvoiceId).Message, StringComparison.Ordinal);
        Assert.Contains("'void' is not one of its values", found.Single(d => d.ElementId == EditorRepo.CustomerId).Message, StringComparison.Ordinal);
        Assert.Contains("already holds \"draft\"", found.Single(d => d.ElementId == EditorRepo.PaymentId).Message, StringComparison.Ordinal);

        // A suffix an integer cannot parse, on an attribute.
        var priority = await ApplyAsync(r, """{"op":"tags-to-property","prefix":"priority:","name":"priority"}""");
        var misfit = Assert.Single(priority.Items.SelectMany(i => i.Diagnostics));
        Assert.Equal(("MQ5001", TotalId, "/attributes/3/properties/priority"), (misfit.Rule, misfit.ElementId, misfit.JsonPointer));
        Assert.Contains("'big' is not an integer", misfit.Message, StringComparison.Ordinal);

        // A property no schema declares, one finding per holder; no matching tag is MQ1002.
        var undeclared = await ApplyAsync(r, """{"op":"tags-to-property","prefix":"status:","name":"phase"}""");
        Assert.Equal(3, undeclared.Items.SelectMany(i => i.Diagnostics).Count(d => d.Rule == "MQ5001" && d.Message.Contains("declares 'phase'", StringComparison.Ordinal)));
        var nothing = await ApplyAsync(r, """{"op":"tags-to-property","prefix":"phase:","name":"status"}""");
        Assert.Equal(("MQ1002", "/operations/0"), (Assert.Single(nothing.Items.SelectMany(i => i.Diagnostics)).Rule, nothing.Items.SelectMany(i => i.Diagnostics).Single().JsonPointer));
        Assert.Equal(before, r.Files());
    }

    [Fact]
    public async Task Element_ids_limit_the_move_and_keep_declarations()
    {
        await using var r = Repo(["status:draft"], ["status:paid"], ["status:paid"]);

        Saved(await ApplyAsync(r, """{"op":"tags-to-property","prefix":"status:","name":"status","elementIds":["@entity:Payment"]}"""));

        Assert.Equal("paid", EntityOf(r, EditorRepo.PaymentId).Properties["status"].GetString());
        Assert.DoesNotContain("status:paid", EntityOf(r, EditorRepo.PaymentId).Tags);
        Assert.False(EntityOf(r, EditorRepo.CustomerId).Properties.ContainsKey("status"));
        Assert.Contains("status:paid", EntityOf(r, EditorRepo.CustomerId).Tags);
        Assert.Contains("status:draft", EntityOf(r, EditorRepo.InvoiceId).Tags);
        Assert.Contains("status:paid", Keys(r));
        Assert.Contains("status:draft", Keys(r));
    }
}
