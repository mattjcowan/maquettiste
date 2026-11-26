using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Parts;

/// <summary>
/// The parts of a whole record and what a copy carries over (parts-and-copy-design.md), over the billing fixture with invented parts: an
/// invoice owns its lines (<c>contains</c>), payment schedules and tax groups; a line owns its discounts and points at one tax group of its
/// invoice. The tests cover <c>entity.parts</c> (depth, parent, the order safe for foreign keys with a to-one link between parts, a circle, a
/// part of its own kind, the foreign key per database), <c>entity.copy</c> (merged entries, the fallback, a kind left out with its own parts, a
/// choice by condition and by state) and every case of MQ3045 to MQ3048, each at its pointer in the invoice's file.
/// </summary>
public sealed class PartsAndCopyTests
{
    private const string Package = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string InvoiceFile = "model/entities/invoice.json";

    private const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string InvoiceId = "01J92P0V0Q9EK961M5HAQ3C5MY";
    private const string InvoiceNumber = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string InvoiceStatus = "01J92P0V0VB1Z49SWERMAGR4TV";
    private const string InvoiceNotes = "01J92P0V0WKRGKH7YBKA2V30NC";
    private const string LineDescription = "01J92P0V1015JY6PVQZG6MY8AS";
    private const string LinesEnd = "01J92P0V1H4D2M1HCK82ASJEWT";
    private const string CustomerEnd = "01J92P0V1EHF7PB28CZJG9C5SN";
    private const string ProductEnd = "01J92P0V1K5BZCDV8JB9BQ2N6T";

    private const string CopiedFrom = "01JPCA00000000000000000001";
    private const string Discount = "01JPCE00000000000000000001";
    private const string Schedule = "01JPCE00000000000000000002";
    private const string TaxGroup = "01JPCE00000000000000000003";
    private const string ScheduleVersion = "01JPCA00000000000000000011";
    private const string ScheduleCurrent = "01JPCA00000000000000000012";
    private const string TaxRate = "01JPCA00000000000000000021";
    private const string DiscountsEnd = "01JPCN00000000000000000002";
    private const string SchedulesEnd = "01JPCN00000000000000000004";
    private const string TaxGroupsEnd = "01JPCN00000000000000000006";
    private const string LineTaxGroupEnd = "01JPCN00000000000000000008";
    private const string CopiesEnd = "01JPCN00000000000000000009";
    private const string SourceEnd = "01JPCN00000000000000000010";
    private const string ScheduleLifecycle = "01JPCP00000000000000000001";
    private const string TaxLifecycle = "01JPCP00000000000000000002";
    private const string Active = "01JPCS00000000000000000001";
    private const string Replaced = "01JPCS00000000000000000002";
    private const string Open = "01JPCS00000000000000000003";

    private static readonly string[] Rules = ["MQ3045", "MQ3046", "MQ3047", "MQ3048"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string EntityFile(string id, string name, string attributes) => $$"""
        {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Package}}",
         "key":{"attributes":["01JPCK{{id[6..]}}"]},"attributes":[{"id":"01JPCK{{id[6..]}}","name":"id","type":"uuid","required":true}{{attributes}}]}
        """;

    private static string Composition(string id, string name, string ownerEnd, string owner, string partEnd, string part, string role) => $$"""
        {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{id}}","name":"{{name}}","package":"{{Package}}","relationKind":"composition",
         "ends":[{"id":"{{ownerEnd}}","entity":"{{owner}}","role":"owner","min":1,"max":1},{"id":"{{partEnd}}","entity":"{{part}}","role":"{{role}}","navigation":"{{role}}"}]}
        """;

    private static string Lifecycle(string id, string name, string subject, string states) => $$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{id}}","name":"{{name}}","package":"{{Package}}","use":"lifecycle",
         "subjects":[{"id":"01JPCB{{id[6..]}}","entity":"{{subject}}"}],"states":[{{states}}]}
        """;

    /// <summary>The billing fixture with the invented parts, and <paramref name="copy"/> (JSON) as the invoice's copy block, when given.</summary>
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? copy, Action<LoaderHarness>? prepare = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        h.Write("model/entities/line-discount.json", EntityFile(Discount, "LineDiscount", """,{"id":"01JPCA00000000000000000031","name":"percent","type":"decimal","precision":5,"scale":2}"""));
        h.Write("model/entities/payment-schedule.json", EntityFile(Schedule, "PaymentSchedule",
            $$""",{"id":"{{ScheduleVersion}}","name":"version","type":"int32","required":true},{"id":"{{ScheduleCurrent}}","name":"current","type":"bool","required":true}"""));
        h.Write("model/entities/tax-group.json", EntityFile(TaxGroup, "TaxGroup", $$""",{"id":"{{TaxRate}}","name":"rate","type":"decimal","precision":5,"scale":2}"""));
        h.Write("model/relations/has-discounts.json", Composition("01JPCR00000000000000000001", "hasDiscounts", "01JPCN00000000000000000001", "01J92P0V0GWFR78HZH0P8Z3GY7", DiscountsEnd, Discount, "discounts"));
        h.Write("model/relations/has-schedules.json", Composition("01JPCR00000000000000000002", "hasSchedules", "01JPCN00000000000000000003", Invoice, SchedulesEnd, Schedule, "schedules"));
        h.Write("model/relations/has-tax-groups.json", Composition("01JPCR00000000000000000003", "hasTaxGroups", "01JPCN00000000000000000005", Invoice, TaxGroupsEnd, TaxGroup, "taxGroups"));
        h.Write("model/relations/taxed-in.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"01JPCR00000000000000000004","name":"taxedIn","package":"{{Package}}",
             "ends":[{"id":"01JPCN00000000000000000007","entity":"01J92P0V0GWFR78HZH0P8Z3GY7","role":"taxedLines"},
                     {"id":"{{LineTaxGroupEnd}}","entity":"{{TaxGroup}}","role":"taxGroup","navigation":"taxGroup","max":1}]}
            """);
        h.Write("model/relations/copied-from.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"01JPCR00000000000000000005","name":"copiedFrom","package":"{{Package}}",
             "ends":[{"id":"{{CopiesEnd}}","entity":"{{Invoice}}","role":"copies"},
                     {"id":"{{SourceEnd}}","entity":"{{Invoice}}","role":"source","max":1}]}
            """);
        h.Write("model/processes/schedule-lifecycle.json", Lifecycle(ScheduleLifecycle, "ScheduleLifecycle", Schedule,
            $$"""{"id":"{{Active}}","name":"Active"},{"id":"{{Replaced}}","name":"Replaced"}"""));
        h.Write("model/processes/tax-lifecycle.json", Lifecycle(TaxLifecycle, "TaxLifecycle", TaxGroup, $$"""{"id":"{{Open}}","name":"Open"}"""));
        var invoice = JsonNode.Parse(h.Read(InvoiceFile))!.AsObject();
        invoice["attributes"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"{{CopiedFrom}}","name":"copiedFrom","type":"uuid"}"""));
        if (copy is not null)
            invoice["copy"] = JsonNode.Parse(copy);
        h.Write(InvoiceFile, invoice.ToJsonString());
        prepare?.Invoke(h);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (h, store);
    }

    private static async Task<(ResolvedModel Model, IReadOnlyList<Diagnostic> Diagnostics)> ResolveAsync(string? copy, Action<LoaderHarness>? prepare = null)
    {
        var (h, store) = await OpenAsync(copy, prepare);
        using var _ = h;
        await using var __ = store;
        var diagnostics = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
        return (ResolutionKit.Resolve(store.Current!), diagnostics);
    }

    /// <summary>The findings of the copy rules and of the expression rules as (rule, pointer), ordered.</summary>
    private static async Task<(string Rule, string Pointer, string Message)[]> FindingsAsync(string copy)
    {
        var (_, diagnostics) = await ResolveAsync(copy);
        return [.. diagnostics.Where(d => Rules.Contains(d.Rule) || (d.Rule.StartsWith("MQ31", StringComparison.Ordinal) && d.FilePath?.EndsWith("invoice.json", StringComparison.Ordinal) == true))
            .Select(d => (d.Rule, d.JsonPointer ?? "", d.Message)).Order()];
    }

    private const string GoodCopy = $$"""
        {"attributes":[{"attribute":"{{InvoiceStatus}}","onCopy":"reset"},{"attribute":"{{InvoiceNotes}}","onCopy":"clear"},{"attribute":"{{CopiedFrom}}","onCopy":"original"}],
         "links":[{"end":"{{SourceEnd}}","onCopy":"original"}],
         "parts":[{"end":"{{SchedulesEnd}}","onCopy":"one","choose":{"condition":{"op":"eq","args":[{"path":["{{ScheduleCurrent}}"]},{"value":true}]},"latestBy":"{{ScheduleVersion}}"} },
                  {"end":"{{DiscountsEnd}}","onCopy":"none"},
                  {"end":"{{LinesEnd}}","onCopy":"all","attributes":[{"attribute":"{{LineDescription}}","onCopy":"clear"}]}]}
        """;

    [Fact]
    public async Task An_entity_lists_its_parts_at_every_level_in_an_order_safe_for_foreign_keys()
    {
        var (model, _) = await ResolveAsync(null);
        var invoice = model.Entity("Invoice");

        // Walk order would be lines, discounts, schedules, tax groups (relation names); a line points at its tax group, so tax groups come first.
        Assert.Equal(["PaymentSchedule", "TaxGroup", "InvoiceLine", "LineDiscount"], invoice.Parts.Select(p => p.Entity.Name));
        var lines = invoice.Parts[2];
        var discounts = invoice.Parts[3];
        Assert.Equal(("contains", "invoice", "lines", "Invoice", 1, null), (lines.Relation.Name, lines.OwnerEnd.Role, lines.End.Role, lines.Owner.Name, lines.Depth, lines.Parent));
        Assert.Equal(("hasDiscounts", "InvoiceLine", 2), (discounts.Relation.Name, discounts.Owner.Name, discounts.Depth));
        Assert.Same(lines, discounts.Parent);
        Assert.Equal(LinesEnd + "/" + DiscountsEnd, discounts.Id);
        Assert.All(invoice.Parts, p => Assert.False(p.IsRecursive || p.InCycle));

        // The line's own parts, and nothing for an entity without parts.
        Assert.Equal(["LineDiscount"], model.Entity("InvoiceLine").Parts.Select(p => p.Entity.Name));
        Assert.Empty(model.Entity("Customer").Parts);
    }

    [Fact]
    public async Task A_part_lists_the_foreign_key_that_links_it_to_its_owner_per_database()
    {
        var (model, _) = await ResolveAsync(null);
        var lines = model.Entity("Invoice").Parts.Single(p => p.End.Id == LinesEnd);

        var (database, keys) = Assert.Single(lines.ForeignKeys);
        var fk = Assert.Single(keys);
        Assert.Equal("main", database);
        Assert.Equal(("invoice_lines", "invoices"), (fk.Columns[0].Table.Name, fk.ReferencedTable.Name));
        Assert.Equal(["invoice_id"], fk.Columns.Select(c => c.Name));
        Assert.Equal(["id"], fk.ReferencedColumns.Select(c => c.Name));
        Assert.Same(lines.End, fk.End);
    }

    [Fact]
    public async Task Links_that_go_round_in_a_circle_are_marked_and_never_put_a_part_before_its_owner()
    {
        // The tax group points back at one line (its main line): line before group and group before line.
        var (model, _) = await ResolveAsync(null, h => h.Write("model/relations/main-line.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"01JPCR00000000000000000006","name":"mainLine","package":"{{Package}}",
             "ends":[{"id":"01JPCN00000000000000000011","entity":"{{TaxGroup}}","role":"groups"},
                     {"id":"01JPCN00000000000000000012","entity":"01J92P0V0GWFR78HZH0P8Z3GY7","role":"mainLine","navigation":"mainLine","max":1}]}
            """));
        var parts = model.Entity("Invoice").Parts;

        // The circle holds the earliest part (the lines), so it goes first, whole and in walk order; the discounts follow their lines.
        Assert.Equal(["InvoiceLine", "TaxGroup", "LineDiscount", "PaymentSchedule"], parts.Select(p => p.Entity.Name));
        Assert.Equal([true, true, false, false], parts.Select(p => p.InCycle));
    }

    [Fact]
    public async Task A_part_that_only_points_at_a_circle_comes_after_it_and_is_not_marked()
    {
        // The circle of the test above, and an adjustment of the invoice that points at a tax group: it waits on the circle, never breaks it.
        var (model, _) = await ResolveAsync(null, h =>
        {
            h.Write("model/relations/main-line.json", $$"""
                {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"01JPCR00000000000000000006","name":"mainLine","package":"{{Package}}",
                 "ends":[{"id":"01JPCN00000000000000000011","entity":"{{TaxGroup}}","role":"groups"},
                         {"id":"01JPCN00000000000000000012","entity":"01J92P0V0GWFR78HZH0P8Z3GY7","role":"mainLine","navigation":"mainLine","max":1}]}
                """);
            h.Write("model/entities/adjustment.json", EntityFile("01JPCE00000000000000000005", "Adjustment", ""));
            h.Write("model/relations/has-adjustments.json", Composition("01JPCR00000000000000000008", "hasAdjustments", "01JPCN00000000000000000015", Invoice,
                "01JPCN00000000000000000016", "01JPCE00000000000000000005", "adjustments"));
            h.Write("model/relations/adjusts.json", $$"""
                {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"01JPCR00000000000000000009","name":"adjusts","package":"{{Package}}",
                 "ends":[{"id":"01JPCN00000000000000000017","entity":"01JPCE00000000000000000005","role":"adjustments"},
                         {"id":"01JPCN00000000000000000018","entity":"{{TaxGroup}}","role":"taxGroup","min":1,"max":1}]}
                """);
        });
        var parts = model.Entity("Invoice").Parts;

        Assert.Equal(["InvoiceLine", "TaxGroup", "LineDiscount", "Adjustment", "PaymentSchedule"], parts.Select(p => p.Entity.Name));
        Assert.Equal([true, true, false, false, false], parts.Select(p => p.InCycle));
    }

    [Fact]
    public void Parts_that_multiply_are_ordered_in_time_linear_in_their_number()
    {
        // Each level owns two parts of the next through two compositions (MQ3016, but such a model loads), and each part points at its owner's
        // kind: depth 10 lists 2,046 parts with links between every part of one level and every part of the level above.
        static (ModelSnapshot Model, Engine.Model.Entity Root) Levels(int depth)
        {
            var b = new ModelBuilder(seed: 61);
            var levels = Enumerable.Range(0, depth + 1).Select(k => b.Entity("Level" + k).Key("id", "uuid")).ToList();
            for (var k = 0; k < depth; k++)
            {
                b.Relation("a" + k, levels[k], levels[k + 1], fromMax: MaxCardinality.One, fromMin: 1, fromRole: "owner", toRole: "a" + k).Kind(RelationKind.Composition);
                b.Relation("b" + k, levels[k], levels[k + 1], fromMax: MaxCardinality.One, fromMin: 1, fromRole: "owner", toRole: "b" + k).Kind(RelationKind.Composition);
                b.Relation("up" + k, levels[k + 1], levels[k], toMax: MaxCardinality.One, fromRole: "below", toRole: "above");
            }

            var model = b.Build();
            return (model, model.Get<Engine.Model.Entity>(levels[0].Id)!);
        }

        static TimeSpan Time(int depth)
        {
            var best = TimeSpan.MaxValue;
            for (var run = 0; run < 3; run++)
            {
                var (model, root) = Levels(depth); // a new snapshot each time: nothing is reused between runs
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var tree = PartTree.Of(model, root);
                best = clock.Elapsed < best ? clock.Elapsed : best;
                Assert.Equal((1 << (depth + 1)) - 2, tree.Order.Count);
                Assert.False(tree.Truncated);
            }

            return best;
        }

        Time(3); // warm up
        var nine = Time(9);
        var ten = Time(10);
        Assert.True(ten < nine * 3 + TimeSpan.FromMilliseconds(100) && ten < TimeSpan.FromSeconds(2),
            $"depth 9 took {nine.TotalMilliseconds:F0} ms, depth 10 took {ten.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task A_relation_that_becomes_a_composition_keeps_a_copy_blocks_earlier_findings_known()
    {
        // The copy block already clears a required attribute (MQ3045) while contains is an association: making it a composition reaches the block
        // only after the change, and its old finding must not refuse the save.
        var (h, store) = await OpenAsync($$"""{"attributes":[{"attribute":"{{InvoiceNumber}}","onCopy":"clear"}]}""",
            h => h.Write("model/relations/contains.json", h.Read("model/relations/contains.json").Replace("\"composition\"", "\"association\"", StringComparison.Ordinal)));
        using var _ = h;
        await using var __ = store;
        Assert.Contains((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ3045");

        var contains = store.Current!.GetDocument("01J92P0V1BWHG0REWKSTR292RS")!;
        var text = h.Read("model/relations/contains.json").Replace("\"association\"", "\"composition\"", StringComparison.Ordinal);
        var saved = await store.SaveAsync(contains.Element.Id, System.Text.Encoding.UTF8.GetBytes(text), contains.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
    }

    [Fact]
    public async Task A_part_of_its_own_kind_is_listed_once_and_marked_recursive()
    {
        var (model, _) = await ResolveAsync(null, h => h.Write("model/relations/sub-discounts.json",
            Composition("01JPCR00000000000000000007", "subDiscounts", "01JPCN00000000000000000013", Discount, "01JPCN00000000000000000014", Discount, "subDiscounts")));
        var parts = model.Entity("Invoice").Parts;

        var sub = parts.Single(p => p.Relation.Name == "subDiscounts");
        Assert.Equal((3, true, "LineDiscount"), (sub.Depth, sub.IsRecursive, sub.Parent!.Entity.Name));
        Assert.Equal(5, parts.Count);
        // A tree of parts of one kind is no circle of links: the composition orders it.
        Assert.All(parts, p => Assert.False(p.InCycle));
    }

    [Fact]
    public async Task The_copy_resolves_its_entries_fills_in_the_rest_and_leaves_out_a_kind_with_its_parts()
    {
        var (model, diagnostics) = await ResolveAsync(GoodCopy);
        Assert.DoesNotContain(diagnostics, d => Rules.Contains(d.Rule) || d.Rule.StartsWith("MQ31", StringComparison.Ordinal));
        var copy = model.Entity("Invoice").Copy!;

        Assert.Equal([("status", "reset"), ("notes", "clear"), ("copiedFrom", "original")], copy.Attributes.Select(a => (a.Attribute.Name, a.OnCopy)));
        Assert.All(copy.Attributes, a => Assert.Equal("Invoice", a.From.Name));
        Assert.Equal(("source", "original"), (Assert.Single(copy.Links).End.Role, copy.Links[0].OnCopy));
        // Discounts are left out; tax groups have no entry, so every one is copied.
        Assert.Equal([("PaymentSchedule", "one", "Invoice"), ("TaxGroup", "all", null), ("InvoiceLine", "all", "Invoice")],
            copy.Parts.Select(p => (p.Part.Entity.Name, p.OnCopy, p.From?.Name)));
        var choice = copy.Parts[0].Choose!;
        Assert.Equal(("current = true", "version", null), (choice.Condition!.Text, choice.LatestBy!.Name, choice.State));
        Assert.Same(model.Entity("PaymentSchedule").Attributes.Single(a => a.Name == "current"), choice.Condition.Args[0].Target);
        Assert.Equal([("description", "clear")], copy.Parts[2].Attributes.Select(a => (a.Attribute.Name, a.OnCopy)));
        Assert.Null(model.Entity("Customer").Copy);
        ContractCheck.Validate(model, "copyRecord", "copyAttributeRecord", "copyLinkRecord", "copyPartRecord", "copyChoiceRecord");
    }

    [Fact]
    public async Task A_part_can_be_chosen_by_a_state_of_its_lifecycle()
    {
        var (model, diagnostics) = await ResolveAsync($$"""{"parts":[{"end":"{{SchedulesEnd}}","onCopy":"one","choose":{"state":"{{Active}}","latestBy":"{{ScheduleVersion}}"} }]}""");

        Assert.DoesNotContain(diagnostics, d => Rules.Contains(d.Rule));
        var choice = model.Entity("Invoice").Copy!.Parts.Single(p => p.OnCopy == "one").Choose!;
        Assert.Equal(("Active", Active), (choice.State!.Name, choice.State.Id));
    }

    [Fact]
    public async Task A_derived_entity_inherits_the_copy_and_replaces_an_entry()
    {
        var (model, _) = await ResolveAsync(GoodCopy, h => h.Write("model/entities/credit-note.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"01JPCE00000000000000000004","name":"CreditNote","package":"{{Package}}","base":"{{Invoice}}",
             "copy":{"attributes":[{"attribute":"{{InvoiceNotes}}","onCopy":"copy"}],"parts":[{"end":"{{DiscountsEnd}}","onCopy":"all"}]} }
            """));
        var copy = model.Entity("CreditNote").Copy!;

        Assert.Equal(["status", "copiedFrom"], copy.Attributes.Select(a => a.Attribute.Name));
        Assert.Equal(("LineDiscount", "CreditNote"), (copy.Parts.Last().Part.Entity.Name, copy.Parts.Last().From!.Name));
        Assert.Equal(4, copy.Parts.Count);
    }

    [Fact]
    public async Task Attribute_entries_that_cant_stand_are_MQ3045_and_MQ3046()
    {
        var found = await FindingsAsync($$"""
            {"attributes":[{"attribute":"{{InvoiceId}}","onCopy":"reset"},{"attribute":"{{InvoiceNumber}}","onCopy":"clear"},{"attribute":"{{TaxRate}}","onCopy":"reset"},
                           {"attribute":"{{InvoiceNotes}}","onCopy":"original"},{"attribute":"{{InvoiceNotes}}","onCopy":"clear"},
                           {"attribute":"01J92P0V0SNXS6PZ42VDK42HP9","onCopy":"reset"}],
             "links":[{"end":"{{LinesEnd}}","onCopy":"original"},{"end":"{{ProductEnd}}","onCopy":"original"},{"end":"{{CustomerEnd}}","onCopy":"original"}],
             "parts":[{"end":"{{LinesEnd}}","onCopy":"all","attributes":[{"attribute":"01J92P0V0Y049452AH0K8CDC9N","onCopy":"clear"}]}]}
            """);

        Assert.Equal(
        [
            ("MQ3045", "/copy/attributes/0/attribute", "Attribute 'id' is part of the key of 'Invoice': a copy is a new record, so its key is always new and made the team's way; remove the entry."),
            ("MQ3045", "/copy/attributes/1/onCopy", "Attribute 'number' of 'Invoice' is required, so a copy can't leave it empty; use reset, or remove the entry."),
            ("MQ3045", "/copy/attributes/2/attribute", $"Attribute '{TaxRate}' is not one of the attributes of 'Invoice' (its own, an inherited one or one from a stereotype it carries); remove the entry."),
            ("MQ3045", "/copy/attributes/4/attribute", "'Invoice' has two copy entries for attribute 'notes'; remove the second."),
            ("MQ3045", "/copy/attributes/5/onCopy", "Attribute 'issuedOn' of 'Invoice' is required and has no default, so a reset copy would leave it empty; give it a default, or remove the entry."),
            ("MQ3045", "/copy/links/0/end", "End 'lines' belongs to composition 'contains': a composition makes parts of a whole, which the copy's parts say, not a link; remove the entry."),
            ("MQ3045", "/copy/links/1/end", "'Invoice' doesn't reach end 'product' of relation 'refers to'; name the end at the other side of a relation of 'Invoice'."),
            ("MQ3045", "/copy/parts/0/attributes/0/onCopy", "Attribute 'quantity' of 'InvoiceLine' is required, so a copy can't leave it empty; use reset, or remove the entry."),
            ("MQ3046", "/copy/attributes/3/onCopy", "Attribute 'notes' of 'Invoice' is typed text, and its key 'id' is typed uuid, so it can't hold the id of the record a copy was made from; type it as the key is typed."),
            ("MQ3046", "/copy/links/2/onCopy", "End 'customer' of relation 'places' can't lead to a 'Invoice', so it can't link a copy to the record it was made from; name an end whose type covers 'Invoice'."),
        ], found);
    }

    [Fact]
    public async Task Part_entries_that_cant_stand_are_MQ3047_and_MQ3048()
    {
        var found = await FindingsAsync($$"""
            {"parts":[{"end":"{{LineTaxGroupEnd}}","onCopy":"all"},
                      {"end":"{{SchedulesEnd}}","onCopy":"one"},
                      {"end":"{{SchedulesEnd}}","onCopy":"all"},
                      {"end":"{{TaxGroupsEnd}}","onCopy":"all","choose":{"latestBy":"{{TaxRate}}"} },
                      {"end":"{{DiscountsEnd}}","onCopy":"none","attributes":[{"attribute":"01JPCA00000000000000000031","onCopy":"reset"}]},
                      {"end":"{{LinesEnd}}","onCopy":"one","choose":{"state":"{{Active}}","condition":{"value":true} } }]}
            """);

        Assert.Equal(
        [
            ("MQ3047", "/copy/parts/0/end", "End 'taxGroup' of relation 'taxedIn' is not the part end of a composition among the parts of 'Invoice'; name the end that leads to a kind of part."),
            ("MQ3047", "/copy/parts/1/onCopy", "The copy of 'Invoice' keeps one 'schedules' part but doesn't say which; add choose with a condition, a state or latestBy."),
            ("MQ3047", "/copy/parts/2/end", "'Invoice' has two copy entries for end 'schedules'; remove the second."),
            ("MQ3047", "/copy/parts/3/choose", "The copy of 'Invoice' keeps every 'taxGroups' part, so it chooses none; remove choose, or use one."),
            ("MQ3047", "/copy/parts/4/attributes", "The copy of 'Invoice' keeps no 'discounts' part, so it sets no values on them; remove attributes, or copy the parts."),
            ("MQ3047", "/copy/parts/5/choose", "The choice of one 'lines' part names both a condition and a state; keep one."),
            ("MQ3048", "/copy/parts/5/choose/state", "State 'Active' belongs to lifecycle 'ScheduleLifecycle', which doesn't govern 'InvoiceLine'; name a state of a lifecycle of 'InvoiceLine'."),
        ], found);
    }

    [Fact]
    public async Task A_choice_by_an_attribute_with_no_order_or_a_condition_that_doesnt_type_is_refused()
    {
        var found = await FindingsAsync($$"""
            {"parts":[{"end":"{{SchedulesEnd}}","onCopy":"one","choose":{"latestBy":"{{ScheduleCurrent}}"} },
                      {"end":"{{TaxGroupsEnd}}","onCopy":"one","choose":{"condition":{"path":["{{TaxRate}}"]},"latestBy":"{{ScheduleVersion}}"} },
                      {"end":"{{LinesEnd}}","onCopy":"one","choose":{"condition":{"path":["{{ScheduleVersion}}"]} } }]}
            """);

        Assert.Equal(
        [
            ("MQ3048", "/copy/parts/0/choose/latestBy", "Attribute 'current' of 'PaymentSchedule' has no order, so no part is the latest by it; name a number, a text, a date or a time."),
            ("MQ3048", "/copy/parts/1/choose/latestBy", $"Attribute '{ScheduleVersion}' is not one of the attributes of 'TaxGroup', the 'taxGroups' part; name one of its attributes."),
            ("MQ3102", "/copy/parts/2/choose/condition", $"The site has no member with the id '{ScheduleVersion}'."),
            ("MQ3103", "/copy/parts/1/choose/condition", "The expression is decimal; a part choice must be bool."),
        ], found);
    }

    [Fact]
    public async Task The_ids_in_a_copy_block_are_references_and_a_delete_takes_the_entries_that_name_them()
    {
        var (h, store) = await OpenAsync(GoodCopy);
        using var _ = h;
        await using var __ = store;

        Assert.Contains(store.Current!.ReferencesTo(SchedulesEnd), r => r.FromElementId == Invoice && r.JsonPointer == "/copy/parts/0/end");
        Assert.Contains(store.Current!.ReferencesTo(ScheduleVersion), r => r.FromElementId == Invoice && r.JsonPointer == "/copy/parts/0/choose/latestBy");
        Assert.Contains(store.Current!.ReferencesTo(LineDescription), r => r.FromElementId == Invoice && r.JsonPointer == "/copy/parts/2/attributes/0/attribute");

        // The schedules go: their part entry goes with them; the other entries stay.
        var schedules = "01JPCR00000000000000000002";
        var result = await store.DeleteAsync(schedules, store.Current!.GetDocument(schedules)!.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var parts = JsonNode.Parse(h.Read(InvoiceFile))!["copy"]!["parts"]!.AsArray();
        Assert.Equal([DiscountsEnd, LinesEnd], parts.Select(p => (string)p!["end"]!));
    }

    [Fact]
    public async Task Deleting_a_composition_on_the_way_down_checks_the_copy_again()
    {
        var subs = "01JPCN00000000000000000014";
        var (h, store) = await OpenAsync($$"""{"parts":[{"end":"{{subs}}","onCopy":"none"}]}""", h => h.Write("model/relations/sub-discounts.json",
            Composition("01JPCR00000000000000000007", "subDiscounts", "01JPCN00000000000000000013", Discount, subs, Discount, "subDiscounts")));
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ3047");

        // Taking the discounts away makes their sub-discounts no part of an invoice, though the entry's end is still there.
        var hasDiscounts = "01JPCR00000000000000000001";
        var deleted = await store.DeleteAsync(hasDiscounts, store.Current!.GetDocument(hasDiscounts)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        // The delete would leave the entry naming no part, so it is refused with that finding.
        Assert.Equal(SaveOutcome.Invalid, deleted.Outcome);
        Assert.Contains(deleted.Diagnostics, d => d.Rule == "MQ3047" && d.FilePath!.EndsWith("invoice.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_change_to_a_composition_on_the_way_down_checks_the_copy_again()
    {
        var (h, store) = await OpenAsync($$"""{"parts":[{"end":"{{DiscountsEnd}}","onCopy":"none"}]}""");
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ3047");

        // contains becomes an association: lines, and so their discounts, are no longer parts of an invoice.
        var contains = store.Current!.GetDocument("01J92P0V1BWHG0REWKSTR292RS")!;
        var text = h.Read("model/relations/contains.json").Replace("\"composition\"", "\"association\"", StringComparison.Ordinal);
        var saved = await store.SaveAsync(contains.Element.Id, System.Text.Encoding.UTF8.GetBytes(text), contains.Hash, ChangeSource.Editor, Ct);

        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ3047" && d.FilePath!.EndsWith("invoice.json", StringComparison.Ordinal));
    }
}
