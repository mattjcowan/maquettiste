using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>E4: <see cref="Maquettiste.Engine.Model.ElementSummary"/> carries the element's category and stereotypes (phase2-design.md section 3.8).</summary>
public sealed class ElementSummaryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Index_summaries_carry_category_and_stereotypes_from_the_element()
    {
        await using var repo = EditorRepo.Create(packs: false);

        var index = await repo.Store.GetIndexAsync(EditorRepo.Ct);

        var invoice = index.Single(s => s.Id == EditorRepo.InvoiceId);
        Assert.Equal(EditorRepo.BillingCategoryId, invoice.Category);
        Assert.Equal(["aggregate-root", "audited", "soft-delete"], invoice.Stereotypes);
        var package = index.Single(s => s.Id == EditorRepo.BillingPackageId);
        Assert.Null(package.Category);
        Assert.Empty(package.Stereotypes);
        // Every summary matches its element: the whole index, not just the two above.
        foreach (var summary in index)
        {
            var element = (await repo.Store.GetElementAsync(summary.Id, EditorRepo.Ct))!.Element;
            Assert.Equal(element.Category, summary.Category);
            Assert.Equal(element.Stereotypes, summary.Stereotypes);
        }
    }

    [Fact]
    public async Task A_save_updates_the_summary_through_the_patched_index()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var document = (await repo.Store.GetElementAsync(EditorRepo.InvoiceId, EditorRepo.Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["stereotypes"] = new JsonArray("audited");
        node.Remove("category");

        var result = await repo.Store.SaveAsync(EditorRepo.InvoiceId, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var summary = (await repo.Store.GetIndexAsync(EditorRepo.Ct)).Single(s => s.Id == EditorRepo.InvoiceId);
        Assert.Null(summary.Category);
        Assert.Equal(["audited"], summary.Stereotypes);
        Assert.Equal(result.Hash, summary.Hash);
    }

    [Fact]
    public async Task Summaries_serialize_category_and_stereotypes_with_web_defaults()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var index = await repo.Store.GetIndexAsync(EditorRepo.Ct);

        var json = JsonNode.Parse(JsonSerializer.Serialize(index, Web))!.AsArray();

        var invoice = json.Single(n => n!["id"]!.GetValue<string>() == EditorRepo.InvoiceId)!;
        Assert.Equal(EditorRepo.BillingCategoryId, invoice["category"]!.GetValue<string>());
        Assert.Equal(3, invoice["stereotypes"]!.AsArray().Count);
        var package = json.Single(n => n!["id"]!.GetValue<string>() == EditorRepo.BillingPackageId)!.AsObject();
        Assert.True(package.ContainsKey("category"));
        Assert.Null(package["category"]);
        Assert.Empty(package["stereotypes"]!.AsArray());
    }

    [Fact]
    public void A_process_row_carries_its_subjects_only_when_it_has_two_or_more()
    {
        // lifecycle-design.md decision 23: subject for exactly one subject, in either form; subjects for two or more.
        var one = new Maquettiste.Engine.Model.ElementSummary("01JPRC00000000000000000001", "process", "P", null, [], "h", "p.json", null, [], Use: "lifecycle",
            Subject: "01JENT00000000000000000001");
        var two = one with { Subject = null, Subjects = ["01JENT00000000000000000001", "01JENT00000000000000000002"] };

        Assert.False(JsonSerializer.SerializeToNode(one, Web)!.AsObject().ContainsKey("subjects"));
        Assert.Equal(["01JENT00000000000000000001", "01JENT00000000000000000002"],
            JsonSerializer.SerializeToNode(two, Web)!["subjects"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(JsonSerializer.SerializeToNode(two, Web)!.AsObject().ContainsKey("subject"));
        Assert.Equal(two, two with { Subjects = ["01JENT00000000000000000001", "01JENT00000000000000000002"] }); // compared by items
        Assert.NotEqual(two, two with { Subjects = ["01JENT00000000000000000002", "01JENT00000000000000000001"] });
        Assert.NotEqual(one, two);
    }

    [Fact]
    public async Task A_custom_row_carries_type_and_member_count()
    {
        // next-phase-2-design.md section 4.2: ElementSummary.Type, and MemberCount counts the members.
        await using var s = await CustomKinds.CustomFixture.OpenAsync(true, ("setting", CustomKinds.CustomFixture.SettingKind), ("message", CustomKinds.CustomFixture.MessageKind));
        var created = await CustomKinds.CustomFixture.CreateAsync(s, """
            { "kind": "custom", "name": "invoice_sent", "type": "message", "content": { "parts": [ { "name": "subject" }, { "name": "body" } ] } }
            """);

        var message = s.Model.Summaries().Single(r => r.Id == created.Id);
        var setting = s.Model.Summaries().Single(r => r.Id == CustomKinds.CustomFixture.DueDays);

        Assert.Equal(("custom", "message", 2), (message.Kind, message.Type, message.MemberCount));
        Assert.Equal(("setting", 0, CustomKinds.CustomFixture.Billing), (setting.Type, setting.MemberCount, setting.Package));
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(message, web)!.AsObject();
        Assert.Equal("message", json["type"]!.GetValue<string>());
        Assert.Equal(2, json["memberCount"]!.GetValue<int>());
        Assert.False(JsonSerializer.SerializeToNode(s.Model.Summaries().First(r => r.Kind == "entity"), web)!.AsObject().ContainsKey("type"));
        Assert.NotEqual(message, message with { Type = "notice" });
        Assert.Equal("maquettiste-index/e12", Maquettiste.Engine.ModelReads.IndexFormat);
    }

    [Fact]
    public void A_process_row_lists_the_actors_it_names_and_leaves_them_out_when_it_names_none()
    {
        // Every field that names an actor counts (event senders, gate signers and required actors), libraries too; an id that is no
        // actor (a missing one, the subject entity) does not. The list is distinct and sorted ordinally.
        var b = new ModelBuilder(71);
        var order = b.Entity("Order").Key("id", "uuid");
        var clerk = new Actor { Id = b.NewId(), Name = "Clerk", Type = ActorType.Role };
        var buyer = new Actor { Id = b.NewId(), Name = "Buyer", Type = ActorType.Person };
        b.Add(clerk);
        b.Add(buyer);
        var ghost = b.NewId();
        var named = new Process
        {
            Id = b.NewId(), Name = "OrderFlow", Subject = order.Id,
            Events = [new ProcessEvent { Id = b.NewId(), Name = "submit", Actors = [buyer.Id, ghost] }],
            Gates = [new ProcessGate { Id = b.NewId(), Name = "approve", Signers = [clerk.Id, buyer.Id], RequiredActors = [clerk.Id],
                Meanings = [new GateMeaning { Id = b.NewId(), Name = "approved" }] }],
        };
        var library = new Process
        {
            Id = b.NewId(), Name = "Shared", Use = ProcessUse.Library,
            Events = [new ProcessEvent { Id = b.NewId(), Name = "ping", Actors = [clerk.Id] }],
        };
        var silent = new Process
        {
            Id = b.NewId(), Name = "Quiet", Subject = order.Id,
            Events = [new ProcessEvent { Id = b.NewId(), Name = "tick" }],
        };
        b.Add(named);
        b.Add(library);
        b.Add(silent);
        var documents = b.BuildDocuments().OrderBy(d => d.Path, StringComparer.Ordinal).ToList();

        var rows = ModelSnapshot.Create(documents, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1).Summaries();

        Assert.Equal(new[] { clerk.Id, buyer.Id }.Order(StringComparer.Ordinal), rows.Single(r => r.Id == named.Id).Actors);
        Assert.Equal([clerk.Id], rows.Single(r => r.Id == library.Id).Actors);
        var quiet = rows.Single(r => r.Id == silent.Id);
        Assert.Null(quiet.Actors);
        Assert.All(rows.Where(r => r.Kind != "process"), r => Assert.Null(r.Actors));
        Assert.False(JsonSerializer.SerializeToNode(quiet, Web)!.AsObject().ContainsKey("actors"));
        var json = JsonSerializer.SerializeToNode(rows.Single(r => r.Id == named.Id), Web)!;
        Assert.Equal(new[] { clerk.Id, buyer.Id }.Order(StringComparer.Ordinal), json["actors"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(quiet, quiet with { Actors = null });
        var row = rows.Single(r => r.Id == named.Id);
        Assert.Equal(row, row with { Actors = [.. row.Actors!] }); // compared by items
        Assert.NotEqual(row, row with { Actors = [clerk.Id] });
        Assert.NotEqual(quiet, quiet with { Actors = [clerk.Id] });
    }
}
