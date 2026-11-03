using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Store;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Validation;
using static Maquettiste.Engine.Tests.CustomKinds.CustomFixture;

namespace Maquettiste.Engine.Tests.CustomKinds;

/// <summary>Members of custom elements as sub-elements (next-phase-2-design.md section 4.2, task E22).</summary>
public sealed class CustomMemberTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string InvoiceSent = """
        { "kind": "custom", "name": "invoice_sent", "type": "message",
          "content": { "parts": [ { "name": "subject", "text": "Invoice {number} sent" }, { "name": "body", "text": "Please pay by {due}." } ] } }
        """;

    private static Task<BillingStore> OpenAsync(params (string Type, string Json)[] kinds) =>
        CustomFixture.OpenAsync(false, [("message", MessageKind), .. kinds]);

    [Fact]
    public async Task Members_are_indexed_as_member_sub_elements()
    {
        await using var s = await OpenAsync();
        var created = await CreateAsync(s, InvoiceSent);

        var custom = s.Model.Get<global::Maquettiste.Engine.Model.Custom>(created.Id!)!;
        var parts = custom.Content["parts"].EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, id => Assert.True(IdFormat.IsValid(id)));
        for (var i = 0; i < parts.Count; i++)
        {
            Assert.True(s.Model.TryGetEntry(parts[i], out var entry));
            Assert.Equal((custom.Id, "member", "/content/parts/" + i), (entry.OwnerId, entry.Kind, entry.JsonPointer));
        }

        Assert.Equal(parts, ModelValidator.SubElementIds(custom, s.Model));
        Assert.Equal(parts[0], Assert.Single(s.Model.Names("member").Find("message.invoice_sent.subject")).Id);
        Assert.Equal(created.Id, Assert.Single(s.Model.Names("custom").Find("message.invoice_sent")).Id);
        Assert.Empty(s.Model.Names("custom").Find("invoice_sent"));
    }

    [Fact]
    public async Task Nested_members_are_indexed_under_their_owner()
    {
        await using var s = await OpenAsync(("layout", LayoutKind));
        var created = await CreateAsync(s, """
            { "kind": "custom", "name": "invoice_layout", "type": "layout", "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "content": { "sections": [ { "name": "header", "lines": [ { "name": "number" }, { "name": "date" } ] }, { "name": "footer" } ] } }
            """);

        var model = s.Model;
        var members = ModelIndexer.MembersOf(model.Get<global::Maquettiste.Engine.Model.Custom>(created.Id!)!, model.PropertySchemas);
        Assert.Equal(
            ["/content/sections/0", "/content/sections/0/lines/0", "/content/sections/0/lines/1", "/content/sections/1"],
            members.Select(m => m.Pointer));
        Assert.Equal([-1, 0, 0, -1], members.Select(m => m.Parent));
        Assert.Equal(["sections", "lines", "lines", "sections"], members.Select(m => m.Array));
        Assert.All(members, m => Assert.True(model.TryGetEntry(m.Id!, out var e) && e.OwnerId == created.Id && e.Kind == "member"));
        var date = Assert.Single(model.Names("member").Find("layout.Billing.invoice_layout.header.date"));
        Assert.Equal(members[2].Id, date.Id);
        Assert.Equal("layout.Billing.invoice_layout.header.date", date.Longest);
    }

    [Fact]
    public async Task A_duplicate_member_id_is_MQ1004()
    {
        await using var s = await OpenAsync();
        s.Harness.Write("model/custom/message/twice.json", """
            { "kind": "custom", "id": "01JR6CK00000000000000000T1", "name": "twice", "type": "message",
              "content": { "parts": [ { "id": "01JR6CK00000000000000000T2", "name": "a" }, { "id": "01JR6CK00000000000000000T2", "name": "b" } ] } }
            """);

        var snapshot = await ReloadAsync(s);

        var duplicate = Assert.Single(snapshot.LoadDiagnostics, d => d.Rule == "MQ1004");
        Assert.Equal("/content/parts/1/id", duplicate.JsonPointer);
    }

    [Fact]
    public async Task Removing_a_used_member_is_refused_and_an_optional_value_is_cleared()
    {
        await using var s = await OpenAsync(("reply", ReplyKind));
        var message = await CreateAsync(s, InvoiceSent);
        var parts = s.Model.Get<global::Maquettiste.Engine.Model.Custom>(message.Id!)!.Content["parts"].EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToList();
        var required = await CreateAsync(s, $$"""{ "kind": "custom", "name": "on_subject", "type": "reply", "content": { "part": "{{parts[0]}}" } }""");
        var optional = await CreateAsync(s, $$"""{ "kind": "custom", "name": "on_body", "type": "reply", "content": { "also": "{{parts[1]}}" } }""");
        Assert.Contains(s.Model.ReferencesTo(parts[0]), r => r.FromElementId == required.Id && r.Field == "part" && !r.Optional);

        // Dropping the subject part, which a required value names, is refused.
        var document = s.Model.GetDocument(message.Id!)!;
        var dropped = await s.Store.SaveAsync(message.Id!, BillingStore.Edit(document, o => o["content"]!["parts"]!.AsArray().RemoveAt(0)), document.Hash,
            ChangeSource.Cli, Ct);
        Assert.NotEqual(SaveOutcome.Saved, dropped.Outcome);
        Assert.Contains(dropped.Diagnostics, d => d.Message.Contains(parts[0], StringComparison.Ordinal));

        // Deleting the reply that needs it, then the message with remove-references, clears the optional value.
        Assert.Equal(SaveOutcome.Saved, (await s.Store.DeleteAsync(required.Id!, s.Model.GetDocument(required.Id!)!.Hash, DeleteResolution.Refuse, ChangeSource.Cli, Ct)).Outcome);
        var deleted = await s.Store.DeleteAsync(message.Id!, s.Model.GetDocument(message.Id!)!.Hash, DeleteResolution.RemoveReferences, ChangeSource.Cli, Ct);
        Assert.True(deleted.Outcome == SaveOutcome.Saved, string.Join("; ", deleted.Diagnostics.Select(d => $"{d.Rule} {d.Message}")));
        Assert.False(s.Model.Get<global::Maquettiste.Engine.Model.Custom>(optional.Id!)!.Content.ContainsKey("also"));
    }

    [Fact]
    public async Task A_delete_refuses_to_clear_a_link_the_custom_kind_requires()
    {
        // Decision 11: content links are checked against the custom kind's compiled schema, as custom properties are. The links are
        // optional, but the kind requires 'part', 'parts' needs one item and a line requires its 'ref': both resolutions refuse.
        const string noticeKind = """
            {
              "type": "notice",
              "properties": {
                "part": { "type": "string", "x-ref": { "kind": "member", "optional": true } },
                "parts": { "type": "array", "minItems": 1, "items": { "type": "string", "x-ref": { "kind": "member", "optional": true } } },
                "lines": {
                  "type": "array",
                  "x-member": true,
                  "items": {
                    "type": "object",
                    "properties": { "name": { "type": "string" }, "ref": { "type": "string", "x-ref": { "kind": "member", "optional": true } } },
                    "required": ["name", "ref"]
                  }
                }
              },
              "required": ["part"]
            }
            """;
        await using var s = await OpenAsync(("notice", noticeKind));
        var message = await CreateAsync(s, InvoiceSent);
        var part = s.Model.Get<global::Maquettiste.Engine.Model.Custom>(message.Id!)!.Content["parts"][0].GetProperty("id").GetString()!;
        var notice = await CreateAsync(s, $$"""
            { "kind": "custom", "name": "overdue_notice", "type": "notice",
              "content": { "part": "{{part}}", "parts": ["{{part}}"], "lines": [ { "name": "opening", "ref": "{{part}}" } ] } }
            """);

        foreach (var resolution in new[] { DeleteResolution.RemoveReferences, DeleteResolution.DeleteDependents })
        {
            var plan = await s.Store.GetDeletePlanAsync([message.Id!], resolution, Ct);
            Assert.Equal(SaveOutcome.Invalid, plan.Outcome);
            var refusals = plan.Refused.Where(x => x.Id == notice.Id).OrderBy(x => x.Pointer, StringComparer.Ordinal).ToList();
            Assert.Equal(["/content/lines/0/ref", "/content/part", "/content/parts/0"], refusals.Select(x => x.Pointer));
            Assert.Contains("its property 'lines' (custom-kind:notice) requires its member 'ref'", refusals[0].Why, StringComparison.Ordinal);
            Assert.Contains("its property 'part' (custom-kind:notice) is required, so the link cannot be cleared", refusals[1].Why, StringComparison.Ordinal);
            Assert.Contains("its property 'parts' (custom-kind:notice) needs at least 1 item", refusals[2].Why, StringComparison.Ordinal);
        }

        var deleted = await s.Store.DeleteAsync(message.Id!, s.Model.GetDocument(message.Id!)!.Hash, DeleteResolution.RemoveReferences, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Invalid, deleted.Outcome);
        Assert.Contains(deleted.Diagnostics, d => d.Rule == "MQ2001" && d.ElementId == notice.Id && d.JsonPointer == "/content/lines/0/ref"
            && d.Message.Contains("(custom-kind:notice) requires its member 'ref'", StringComparison.Ordinal));
        Assert.Equal(part, s.Model.Get<global::Maquettiste.Engine.Model.Custom>(notice.Id!)!.Content["part"].GetString());
    }

    [Fact]
    public async Task A_member_placeholder_resolves_later_in_the_batch_and_is_not_stored()
    {
        await using var s = await OpenAsync(("reply", ReplyKind));

        var result = await ApplyAsync(s, """
            { "operations": [
              { "op": "create", "element": { "kind": "custom", "name": "invoice_sent", "type": "message",
                "content": { "parts": [ { "name": "subject", "as": "$subj", "text": "Invoice sent" } ] } } },
              { "op": "create", "element": { "kind": "custom", "name": "on_subject", "type": "reply", "content": { "part": "$subj" } } }
            ] }
            """);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        var message = s.Model.Get<global::Maquettiste.Engine.Model.Custom>(Assert.Single(s.Model.Names("custom").Find("message.invoice_sent")).Id)!;
        var subject = message.Content["parts"][0];
        Assert.False(subject.TryGetProperty("as", out _));
        var reply = s.Model.Get<global::Maquettiste.Engine.Model.Custom>(Assert.Single(s.Model.Names("custom").Find("reply.on_subject")).Id)!;
        Assert.Equal(subject.GetProperty("id").GetString(), reply.Content["part"].GetString());
        var stored = s.Harness.Repo.ReadFile(s.Model.GetDocument(message.Id)!.Path);
        Assert.DoesNotContain("\"as\"", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("$subj", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_a_member_change_revalidates_its_referrers()
    {
        await using var s = await OpenAsync(("reply", ReplyKind));
        var message = await CreateAsync(s, InvoiceSent);
        var part = s.Model.Get<global::Maquettiste.Engine.Model.Custom>(message.Id!)!.Content["parts"][0].GetProperty("id").GetString()!;
        var reply = await CreateAsync(s, $$"""{ "kind": "custom", "name": "on_subject", "type": "reply", "content": { "also": "{{part}}" } }""");

        // A save of the message widens to the referrers of its members: the reply that names a part.
        var targets = ModelValidator.TargetIds(s.Model, new ValidationScope([message.Id!], IncludeReferrers: true));

        Assert.Contains(reply.Id!, targets);
        Assert.Contains(message.Id!, targets);
    }
}
