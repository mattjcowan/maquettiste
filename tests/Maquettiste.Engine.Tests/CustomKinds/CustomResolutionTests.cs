using System.Collections.Immutable;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Engine.Tests.Scripting;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.CustomKinds.CustomFixture;

namespace Maquettiste.Engine.Tests.CustomKinds;

/// <summary>
/// Custom elements in the resolved model and in templates (next-phase-2-design.md section 4.2, "Resolution and the resolved model" and
/// "Templates"): one <see cref="RCustom"/> per element with its content as template values, member arrays as lists of
/// <see cref="RMember"/>, the <c>c:&lt;type&gt;</c> key and <c>custom_named</c>.
/// </summary>
public sealed class CustomResolutionTests
{
    private const string Invoice = "01JRCE00000000000000000001";
    private const string Subject = "01JRCM00000000000000000001";
    private const string Body = "01JRCM00000000000000000002";
    private const string Grace = "01JRCE00000000000000000002";
    private const string Layout = "01JRCE00000000000000000003";
    private const string Section = "01JRCM00000000000000000003";
    private const string Line = "01JRCM00000000000000000004";

    private static (ModelSnapshot Snapshot, ResolvedModel Model, PackageBuilder Billing) Build(string settingKind = SettingKind)
    {
        var b = new ModelBuilder();
        var billing = b.Package("Billing");
        b.Add(Element(DueDays, "due_days", "setting", """{ "unit": "days", "value": 30 }""", billing.Id));
        b.Add(Element(Grace, "grace_days", "setting", """{ "value": 5 }"""));
        b.Add(Element(Invoice, "invoice_sent", "message", $$"""
            { "parts": [ { "id": "{{Subject}}", "name": "subject", "text": "Your invoice" }, { "id": "{{Body}}", "name": "body", "text": "Thanks." } ] }
            """, billing.Id));
        b.Add(Element(Layout, "statement", "layout", $$"""
            { "pick": "{{Line}}", "sections": [ { "id": "{{Section}}", "name": "head", "lines": [ { "id": "{{Line}}", "name": "total" } ] } ] }
            """));
        var snapshot = Snapshot(b, [KindDocument(settingKind), KindDocument(MessageKind), KindDocument(LayoutKind)]);
        return (snapshot, ResolutionKit.Resolve(snapshot), billing);
    }

    [Fact]
    public void Custom_elements_resolve_with_content_and_members()
    {
        var (_, model, billing) = Build();

        // Every custom element, by (type, name, id); the scalar content is plain values in ordinal key order.
        Assert.Equal(["layout statement", "message invoice_sent", "setting due_days", "setting grace_days"], model.CustomElements.Select(c => c.Type + " " + c.Name));
        var due = model.CustomElements.Single(c => c.Id == DueDays);
        Assert.Equal(("custom", "setting", "Setting", "Settings", billing.Id), (due.Kind, due.Type, due.TypeDisplayName, due.TypePluralName, due.Package?.Id));
        Assert.Equal(["unit", "value"], due.Content.Keys);
        Assert.Equal(("days", 30L), (due.Content["unit"], due.Content["value"]));
        Assert.Empty(due.Members);
        Assert.Contains("c:setting", due.Dependencies);
        Assert.Contains("e:" + DueDays, due.Dependencies);

        // Without names in the custom kind, the type stands in and the plural is inflected.
        var message = model.CustomElements.Single(c => c.Id == Invoice);
        Assert.Equal(("message", "messages"), (message.TypeDisplayName, message.TypePluralName));
        Assert.Equal([Subject, Body], message.Members.Select(m => m.Id));
    }

    [Fact]
    public void Member_arrays_become_member_lists()
    {
        var (_, model, _) = Build();
        var message = model.CustomElements.Single(c => c.Id == Invoice);

        // A member array is a list of members; each member's keys but id are its content.
        var parts = Assert.IsType<RList<RMember>>(message.Content["parts"]);
        Assert.Equal([("subject", "parts", "Your invoice"), ("body", "parts", "Thanks.")], parts.Select(p => (p.Name!, p.Array, (string)p.Content["text"]!)));
        Assert.All(parts, p => Assert.Same(message, p.Owner));
        Assert.Equal(["name", "text"], parts[0].Content.Keys);
        Assert.All(parts, p => Assert.Contains("c:message", p.Dependencies));

        // Nested members are members too: walk order, a member before its nested ones, each with its parent.
        var layout = model.CustomElements.Single(c => c.Id == Layout);
        Assert.Equal([(Section, "sections", (string?)null), (Line, "lines", Section)], layout.Members.Select(m => (m.Id, m.Array, m.Parent?.Id)));
        var section = Assert.Single(Assert.IsType<RList<RMember>>(layout.Content["sections"]));
        Assert.Same(layout.Members[1], Assert.Single(Assert.IsType<RList<RMember>>(section.Content["lines"])));
        Assert.Equal(Line, layout.Content["pick"]); // a value outside a member array stays plain
    }

    [Fact]
    public void Lookup_finds_members()
    {
        var (_, model, _) = Build();

        Assert.IsType<RCustom>(model.Find(DueDays));
        var body = Assert.IsType<RMember>(model.Find(Body));
        Assert.Equal(("member", "body", Invoice), (body.Kind, body.Name, body.Owner.Id));
        Assert.IsType<RMember>(model.Find(Line));
    }

    [Fact]
    public async Task A_custom_kind_file_change_re_renders_through_its_c_key()
    {
        var (snapshot, model, _) = Build();
        var due = model.CustomElements.Single(c => c.Id == DueDays);

        var unit = await Adhoc.RenderAsync("{{ custom.type_display_name }}:{{ custom.name }}={{ custom.content.value }}{{ custom.content.unit }}", due,
            u => u with { For = "each custom:setting" }, model: model);

        Assert.Equal("Setting:due_days=30days", unit.Text());
        Assert.Contains("c:setting", unit.ReadKeys);

        // The key hashes the custom kind file: a changed file changes it, an undeclared kind is absent.
        var changed = Build(SettingKind.Replace("\"Settings\"", "\"All settings\"", StringComparison.Ordinal));
        string Hash(ModelSnapshot s, ResolvedModel m, string key) =>
            new DependencyHasher(s, m, new PackSet([], []), new Dictionary<string, SchemaDiffResult>()).CurrentHash(key);
        Assert.NotEqual(Hash(snapshot, model, "c:setting"), Hash(changed.Snapshot, changed.Model, "c:setting"));
        Assert.Equal(Hash(snapshot, model, "c:message"), Hash(changed.Snapshot, changed.Model, "c:message"));
        Assert.Equal("absent", Hash(snapshot, model, "c:flag"));
        Assert.Equal("custom-kind", Maquettiste.Engine.Generation.PlanExplainer.KindOf("c:setting"));
    }

    [Fact]
    public void Renaming_a_misnamed_custom_kind_file_to_its_type_changes_the_c_key()
    {
        // A file whose stem is not its type declares nothing (MQ1017), so c:<type> is absent until the file is renamed to its type,
        // though the content, and so the file hash, stays the same.
        static string Hash(string stem)
        {
            var snapshot = Snapshot(new ModelBuilder(), [KindDocument(FlagKind, stem)]);
            return new DependencyHasher(snapshot, ResolutionKit.Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>())
                .CurrentHash("c:flag");
        }

        Assert.Equal("absent", Hash("flags"));
        Assert.Equal(ContentHash.Of(FlagKind), Hash("flag"));
    }

    [Fact]
    public async Task Custom_named_records_the_batch_form_as_its_name_key()
    {
        var (_, model, _) = Build();

        var unit = await Adhoc.RenderAsync(
            "{{ (custom_named 'setting' 'due_days').content.value }}|{{ (custom_named 'setting' 'Billing.due_days').id }}|"
            + "{{ (custom_named 'setting' 'missing') == null }}|{{ (lookup '" + DueDays + "').content.unit }}",
            model: model);

        Assert.Equal("30|" + DueDays + "|true|days", unit.Text());
        Assert.Contains("n:custom:setting.due_days", unit.ReadKeys);
        Assert.Contains("n:custom:setting.Billing.due_days", unit.ReadKeys);
        Assert.Contains("n:custom:setting.missing", unit.ReadKeys);
        Assert.Contains("e:" + DueDays, unit.ReadKeys);

        var wrong = await Adhoc.RenderAsync("{{ custom_named 'setting' '' }}", model: model);
        Assert.Equal("MQ6006", wrong.Error().Rule);
    }

    [Fact]
    public void A_rows_script_gives_one_row_per_custom_element()
    {
        const string script = ".maquettiste/extensions/seeds/settings.js";
        var (snapshot, _, _) = Build();
        using var pool = Scripts.Pool("""
            maquettiste.rows((model, seed) => model.all("custom").filter(c => c.type === "setting").map(c => ({ code: c.name, value: c.content.value })));
            """, path: script);
        using var lease = pool.Rent();
        var reads = new ListRecorder();

        var rows = lease.Sandbox.RunRows(script, snapshot.GetDocument(DueDays)!, snapshot, Scripts.Ctx(reads, seed: DueDays));

        Assert.Equal("""[{"code":"due_days","value":30},{"code":"grace_days","value":5}]""", rows.GetRawText());
        Assert.Contains("k:custom", reads.Keys);
        Assert.Contains("e:" + Grace, reads.Keys);
    }
}
