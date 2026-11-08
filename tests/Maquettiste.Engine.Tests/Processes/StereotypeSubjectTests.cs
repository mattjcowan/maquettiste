using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// A stereotype as a set of types in a process's subjects (stereotype-sets-design.md), engine rules and tools, over the
/// <c>process-basics</c> fixture with an invented model. Package Purchasing gives its entities <c>approvable</c> by default: PurchaseOrder
/// and TravelRequest carry it, Memo excludes it, and RushOrder derives from PurchaseOrder and excludes it. Contract lists it itself; Asset
/// carries it through <c>workflowed</c>, which implies it; Laptop derives from Asset and lists it itself. Approval lists the stereotype
/// (bound to its approvalStatus, owner requestedBy), Contract (bound to its own status) and Asset.
/// </summary>
public sealed class StereotypeSubjectTests
{
    private const string Sales = "01JPKG00000000000000000001";
    private const string Purchasing = "01JPKG00000000000000000301";
    private const string Approvable = "01JSTR00000000000000000301";
    private const string Workflowed = "01JSTR00000000000000000302";
    private const string Tabled = "01JSTR00000000000000000303";
    private const string Reviewed = "01JSTR00000000000000000304";
    private const string ApprovalStatusEnum = "01JENM00000000000000000301";
    private const string ApprovalStatus = "01JATT00000000000000000301";
    private const string RequestedBy = "01JATT00000000000000000302";
    private const string Reviewers = "01JATT00000000000000000303";
    private const string PurchaseOrder = "01JENT00000000000000000301";
    private const string TravelRequest = "01JENT00000000000000000302";
    private const string Memo = "01JENT00000000000000000303";
    private const string RushOrder = "01JENT00000000000000000304";
    private const string Contract = "01JENT00000000000000000305";
    private const string Asset = "01JENT00000000000000000306";
    private const string Laptop = "01JENT00000000000000000307";
    private const string ContractStatus = "01JATT00000000000000000315";
    private const string Approval = "01JPRC00000000000000000301";
    private const string Audit = "01JPRC00000000000000002302";
    private const string StereotypeEntry = "01JPRX00000000000000000301";
    private const string ContractEntry = "01JPRX00000000000000000302";
    private const string AssetEntry = "01JPRX00000000000000000303";
    private const string Owner = "01JACT00000000000000000301";
    private const string Requester = "01JACT00000000000000000302";
    private const string ScenarioId = "01JSCN00000000000000000301";
    private const string ApprovalFile = "model/processes/approval.json";

    private static CancellationToken Ct => ActorKit.Ct;

    private static string Attr(string id, string name, string type = "string", bool collection = false, string? enumId = null) =>
        enumId is not null
            ? $$"""{"id":"{{id}}","name":"{{name}}","type":{"ref":"{{enumId}}"},"default":"Draft"}"""
            : $$"""{"id":"{{id}}","name":"{{name}}","type":"{{type}}"{{(type == "string" ? ",\"length\":40" : "")}}{{(collection ? ",\"collection\":true" : "")}}}""";

    private static string Entity(string id, string name, string package, string? @base, string extra, params string[] attributes) =>
        $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{package}}"{{(@base is null ? ",\"key\":{\"attributes\":[\"" + id[..^3] + "9" + id[^2..] + "\"]}" : ",\"base\":\"" + @base + "\"")}}{{extra}},"attributes":[{{string.Join(",", (@base is null ? new[] { Attr(id[..^3] + "9" + id[^2..], "id", "uuid") } : []).Concat(attributes))}}]}""";

    private static string Stereotype(string id, string key, string extra) =>
        $$"""{"kind":"stereotype","id":"{{id}}","key":"{{key}}","name":"{{key}}"{{extra}}}""";

    private static string Entries(params string[] entries) => ",\"subjects\":[" + string.Join(",", entries) + "]";

    private const string DefaultEntries = $$"""
        ,"subjects":[{"id":"{{StereotypeEntry}}","stereotype":"approvable","boundAttribute":"{{ApprovalStatus}}","owner":"{{RequestedBy}}"},
          {"id":"{{ContractEntry}}","entity":"{{Contract}}","boundAttribute":"{{ContractStatus}}"},{"id":"{{AssetEntry}}","entity":"{{Asset}}"}]
        """;

    private static string ApprovalProcess(string subjects, string guards = "", string id = Approval, string name = "Approval") =>
        RelativeActorTests.Process(id, name, subjects, RelativeActorTests.Ids(Owner), RelativeActorTests.Ids(Requester))
            .Replace("\"events\":", guards.Length == 0 ? "\"events\":" : "\"guards\":[" + guards + "],\"events\":", StringComparison.Ordinal);

    // The guard on the approve transition that reads the stereotype's requestedBy.
    private const string RequesterGuard = "{\"id\":\"01JPRC00000000000000000951\",\"name\":\"hasRequester\",\"condition\":{\"op\":\"ne\",\"args\":[{\"path\":[\"" + RequestedBy + "\"]},{\"value\":\"nobody\"}]}}";

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null) =>
        await ActorKit.OpenAsync(h =>
        {
            h.Write("model/packages/purchasing.json", $$"""{"kind":"package","id":"{{Purchasing}}","name":"Purchasing","defaultStereotypes":["approvable"]}""");
            h.Write("model/enums/approval-status.json", $$"""
                {"kind":"enum","id":"{{ApprovalStatusEnum}}","name":"ApprovalStatus","package":"{{Sales}}",
                 "members":[{"id":"01JMEM00000000000000000301","name":"Draft"},{"id":"01JMEM00000000000000000302","name":"Submitted"},{"id":"01JMEM00000000000000000303","name":"Done"}]}
                """);
            h.Write("model/vocabularies/stereotypes/approvable.json", Stereotype(Approvable, "approvable",
                $$""","appliesTo":["entity"],"attributes":[{{Attr(ApprovalStatus, "approvalStatus", enumId: ApprovalStatusEnum)}},{{Attr(RequestedBy, "requestedBy")}},{{Attr(Reviewers, "reviewers", collection: true)}}]"""));
            h.Write("model/vocabularies/stereotypes/workflowed.json", Stereotype(Workflowed, "workflowed", ""","appliesTo":["entity"],"implies":["approvable"]"""));
            h.Write("model/vocabularies/stereotypes/tabled.json", Stereotype(Tabled, "tabled", ""","appliesTo":["table"]"""));
            h.Write("model/vocabularies/stereotypes/reviewed.json", Stereotype(Reviewed, "reviewed", ""","appliesTo":["entity"]"""));
            h.Write("model/entities/purchase-order.json", Entity(PurchaseOrder, "PurchaseOrder", Purchasing, null, ""));
            h.Write("model/entities/travel-request.json", Entity(TravelRequest, "TravelRequest", Purchasing, null, ""));
            h.Write("model/entities/memo.json", Entity(Memo, "Memo", Purchasing, null, ""","excludeStereotypes":["approvable"]"""));
            h.Write("model/entities/rush-order.json", Entity(RushOrder, "RushOrder", Purchasing, PurchaseOrder, ""","excludeStereotypes":["approvable"]"""));
            h.Write("model/entities/contract.json", Entity(Contract, "Contract", Sales, null, ""","stereotypes":["approvable"]""", Attr(ContractStatus, "status", enumId: ApprovalStatusEnum)));
            h.Write("model/entities/asset.json", Entity(Asset, "Asset", Sales, null, ""","stereotypes":["workflowed"]"""));
            h.Write("model/entities/laptop.json", Entity(Laptop, "Laptop", Sales, Asset, ""","stereotypes":["approvable"]"""));
            h.Write("model/actors/owner.json", RelativeActorTests.Relative(Owner, "Owner", "person", """{"owner":true}"""));
            h.Write("model/actors/requester.json", RelativeActorTests.Relative(Requester, "Requester", "person", $$"""{"path":["{{RequestedBy}}"]}"""));
            h.Write(ApprovalFile, ApprovalProcess(DefaultEntries));
            prepare?.Invoke(h);
        });

    private static IEnumerable<string> Hits(IEnumerable<Diagnostic> diagnostics, string rule) =>
        diagnostics.Where(d => d.Rule == rule).Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer).Order(StringComparer.Ordinal);

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + Path.GetFileName(d.FilePath) + d.JsonPointer + " " + d.Message));

    private static JsonObject File(LoaderHarness h, string relative) =>
        JsonNode.Parse(h.Read(relative))!.AsObject();

    // ---- no change ----

    [Fact]
    public async Task A_project_without_stereotype_entries_costs_nothing()
    {
        var (h, store) = await ActorKit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        Assert.False(SubjectCoverage.Of(model).Any);
        Assert.Empty(SubjectCoverage.Of(model).Keys);
        Assert.DoesNotContain(ModelValidator.ProcessPeers(model, model.Get<Package>(Sales)!), d => d.Element is Process or Scenario);
        var diagnostics = await ActorKit.ValidateAsync(h, model);
        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ9211");
        var process = model.All<Process>().First();
        Assert.Empty(ExprSites.StereotypeAttributesRead(model, process));
    }

    // ---- coverage (decisions 3 and 4) ----

    [Fact]
    public async Task Coverage_follows_defaults_implication_own_lists_exclusions_and_derived_entities()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var coverage = SubjectCoverage.Of(model);

        Assert.True(coverage.Any);
        Assert.Equal(["approvable"], coverage.Keys);
        // Carriers by package default, by implication and by their own list, by qualified name; Memo excludes the default; RushOrder
        // excludes it too, so it is no carrier, but it derives from one and stays covered.
        Assert.Equal([PurchaseOrder, TravelRequest, Asset, Contract, Laptop], coverage.CarriersOf("approvable").Select(e => e.Id));
        Assert.True(coverage.Covers("approvable", RushOrder));
        Assert.False(coverage.Covers("approvable", Memo));
        Assert.Empty(coverage.CarriersOf("reviewed"));

        var approval = model.Get<Process>(Approval)!;
        string? Entry(string entity) => SubjectCoverage.EntryOf(model, approval, model.Get<Entity>(entity)!)?.Id;
        Assert.Equal(StereotypeEntry, Entry(PurchaseOrder));
        Assert.Equal(StereotypeEntry, Entry(RushOrder));
        Assert.Equal(ContractEntry, Entry(Contract)); // its own entry wins
        Assert.Equal(AssetEntry, Entry(Laptop)); // a base named by its own entry wins over a stereotype the derived entity carries directly
        Assert.Null(Entry(Memo));
        // covers: only the carriers the entry governs.
        Assert.Equal([PurchaseOrder, TravelRequest], SubjectCoverage.Governed(model, approval, approval.Subjects[0]).Select(e => e.Id));
        Assert.Empty(SubjectCoverage.Governed(model, approval, approval.Subjects[1]));
    }

    [Fact]
    public async Task Two_stereotype_entries_covering_one_entity_follow_entry_order_and_nothing_carrying_one_is_fine()
    {
        var entries = Entries($$"""{"id":"01JPRX00000000000000000311","stereotype":"workflowed"}""", $$"""{"id":"01JPRX00000000000000000312","stereotype":"approvable"}""",
            $$"""{"id":"01JPRX00000000000000000313","stereotype":"reviewed"}""");
        var (h, store) = await OpenAsync(x => x.Write(ApprovalFile, ApprovalProcess(entries)));
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var approval = model.Get<Process>(Approval)!;

        Assert.Equal("01JPRX00000000000000000311", SubjectCoverage.EntryOf(model, approval, model.Get<Entity>(Laptop)!)!.Id);
        Assert.Equal([Asset], SubjectCoverage.Governed(model, approval, approval.Subjects[0]).Select(e => e.Id));
        Assert.Equal([PurchaseOrder, TravelRequest, Contract], SubjectCoverage.Governed(model, approval, approval.Subjects[1]).Select(e => e.Id));
        Assert.Empty(SubjectCoverage.Governed(model, approval, approval.Subjects[2]));
        var diagnostics = await ActorKit.ValidateAsync(h, model);
        Assert.DoesNotContain(diagnostics, d => d.Rule is "MQ2003" or "MQ9201" or "MQ9211" && (d.FilePath ?? "").EndsWith("approval.json", StringComparison.Ordinal));
    }

    // ---- rules (section 5) ----

    [Fact]
    public async Task The_model_validates_with_a_stereotype_entry_owner_path_and_guard()
    {
        var (h, store) = await OpenAsync(x => x.Write(ApprovalFile, ApprovalProcess(DefaultEntries, RequesterGuard)
            .Replace("\"targets\":[\"01JPRC00000000000000000923\"]", "\"targets\":[\"01JPRC00000000000000000923\"],\"guard\":\"01JPRC00000000000000000951\"", StringComparison.Ordinal)));
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);

        Assert.True(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("approval", StringComparison.Ordinal)), Describe(diagnostics));
        Assert.DoesNotContain(diagnostics, d => d.Rule is "MQ2003" or "MQ9201" or "MQ9202" or "MQ9203" or "MQ9211" or "MQ9604" or "MQ9606");
        Assert.Equal([RequestedBy], ExprSites.StereotypeAttributesRead(store.Current!, store.Current!.Get<Process>(Approval)!));
    }

    [Fact]
    public async Task A_trigger_point_that_reads_or_sets_a_stereotype_attribute_puts_it_in_the_object_row()
    {
        // trigger-points-design.md decision 15: a check's condition and when, and a before action's set, are read on the object like a
        // guard's condition, so the stereotype attributes they read or write join the scenario's row, even with no guard and no action sets.
        const string Points = $$$"""
            "triggerPoints":[{"id":"01JPRC00000000000000000961","name":"HasRequester","type":"check","verbs":["promote"],
               "condition":{"op":"ne","args":[{"path":["{{{RequestedBy}}}"]},{"value":"nobody"}]}},
             {"id":"01JPRC00000000000000000962","name":"Restart","type":"action","timing":"before","verbs":["promote"],
               "when":{"op":"ne","args":[{"path":["{{{RequestedBy}}}"]},{"value":""}]},
               "sets":[{"id":"01JPRC00000000000000000963","attribute":"{{{Reviewers}}}","value":{"null":true}}]}],"events":
            """;
        var (h, store) = await OpenAsync(x => x.Write(ApprovalFile, ApprovalProcess(DefaultEntries).Replace("\"events\":", Points, StringComparison.Ordinal)));
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        Assert.Equal([RequestedBy, Reviewers], ExprSites.StereotypeAttributesRead(model, model.Get<Process>(Approval)!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Unknown_keys_stereotypes_for_other_kinds_and_repeats_are_refused()
    {
        var entries = Entries($$"""{"id":"01JPRX00000000000000000311","stereotype":"nope"}""", $$"""{"id":"01JPRX00000000000000000312","stereotype":"tabled"}""",
            $$"""{"id":"01JPRX00000000000000000313","stereotype":"reviewed"}""", $$"""{"id":"01JPRX00000000000000000314","stereotype":"reviewed"}""");
        var (h, store) = await OpenAsync(x => x.Write(ApprovalFile, ApprovalProcess(entries)));
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);

        Assert.Equal(["approval.json:/subjects/0/stereotype"], Hits(diagnostics, "MQ2003"));
        Assert.Equal(["approval.json:/subjects/1/stereotype"], Hits(diagnostics, "MQ9211"));
        Assert.Contains("approval.json:/subjects/3/stereotype", Hits(diagnostics, "MQ9201"));
    }

    [Fact]
    public async Task A_stereotype_entry_binds_only_its_stereotypes_own_attribute_and_two_lifecycles_listing_it_clash()
    {
        var wrong = Entries($$"""{"id":"{{StereotypeEntry}}","stereotype":"approvable","boundAttribute":"{{ContractStatus}}"}""");
        var (h, store) = await OpenAsync(x => x.Write(ApprovalFile, ApprovalProcess(wrong)));
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.Equal(["approval.json:/subjects/0/boundAttribute"], Hits(diagnostics, "MQ9202"));
        Assert.Contains("not an attribute of stereotype 'approvable'", diagnostics.First(d => d.Rule == "MQ9202").Message, StringComparison.Ordinal);

        var both = Entries($$"""{"id":"01JPRX00000000000000000321","stereotype":"approvable","boundAttribute":"{{ApprovalStatus}}"}""");
        var (h2, store2) = await OpenAsync(x => x.Write("model/processes/second.json", ApprovalProcess(both, id: "01JPRC00000000000000001311", name: "Second")));
        using var _2 = h2;
        await using var __2 = store2;
        var clash = await ActorKit.ValidateAsync(h2, store2.Current!);
        Assert.Equal(["approval.json:/subjects/0/boundAttribute", "second.json:/subjects/0/boundAttribute"], Hits(clash, "MQ9202"));
        Assert.Contains("for stereotype 'approvable', which both list", clash.First(d => d.Rule == "MQ9202").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entity_names_a_lifecycle_that_covers_it_through_a_stereotype()
    {
        var (h, store) = await OpenAsync(x =>
        {
            x.Write("model/entities/travel-request.json", Entity(TravelRequest, "TravelRequest", Purchasing, null, $$""","lifecycle":"{{Approval}}" """));
            x.Write("model/entities/memo.json", Entity(Memo, "Memo", Purchasing, null, $$""","excludeStereotypes":["approvable"],"lifecycle":"{{Approval}}" """));
        });
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);

        Assert.Equal(["memo.json:/lifecycle"], Hits(diagnostics, "MQ9201"));
    }

    [Fact]
    public async Task Owners_read_a_stereotypes_attribute_on_either_entry_form()
    {
        var entries = Entries($$"""{"id":"{{StereotypeEntry}}","stereotype":"approvable","owner":"{{Reviewers}}"}""",
            $$"""{"id":"{{ContractEntry}}","entity":"{{Contract}}","owner":"{{RequestedBy}}"}""", $$"""{"id":"{{AssetEntry}}","entity":"{{Asset}}","owner":"{{ContractStatus}}"}""");
        var (h, store) = await OpenAsync(x => x.Write(ApprovalFile, ApprovalProcess(entries)));
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);

        // The stereotype entry's owner holds many values; Contract's entry reads the stereotype's attribute it carries; Asset's names an
        // attribute of another entity.
        Assert.Equal(["approval.json:/subjects/0/owner", "approval.json:/subjects/2/owner"], Hits(diagnostics, "MQ9606"));
    }

    [Fact]
    public async Task An_actor_path_is_one_step_to_a_stereotype_attribute_and_within_is_refused()
    {
        var (h, store) = await OpenAsync(x => x.Write("model/actors/requester.json",
            RelativeActorTests.Relative(Requester, "Requester", "role", $$"""{"membersOf":"{{ActorKit.Manager}}","within":["{{RequestedBy}}"]}""")));
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);

        var refused = Assert.Single(diagnostics, d => d.Rule == "MQ9604");
        Assert.Contains("in within", refused.Message, StringComparison.Ordinal);
        var (steps, why) = RelativeActors.Resolve(store.Current!, store.Current!.Get<Process>(Approval)!, new ActorRelative { Path = [RequestedBy] });
        Assert.Null(why);
        Assert.Equal(RequestedBy, Assert.Single(steps).Attribute!.Id);
    }

    [Fact]
    public async Task The_guard_scope_holds_what_every_entry_holds_and_keeps_the_bound_attribute_out()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        // A stereotype entry beside entity entries: the stereotype's attributes, no navigations, the bound attribute kept out.
        var (attributes, navigations) = ExprSites.SubjectMembers(model, model.Get<Process>(Approval)!);
        Assert.Equal([RequestedBy, Reviewers], attributes.Select(a => a.Id));
        Assert.Empty(navigations);

        // An entity entry alone reads its own attributes and those of the stereotypes it or a base carries.
        var alone = model.Get<Process>(Approval)! with { SubjectList = [new ProcessSubject { Id = ContractEntry, Entity = Laptop }] };
        Assert.Equal([Asset[..^3] + "906", ApprovalStatus, RequestedBy, Reviewers], ExprSites.SubjectMembers(model, alone).Attributes.Select(a => a.Id));

        // A context attribute named subject keeps its meaning.
        var named = alone with { Context = [new ModelAttribute { Id = "01JATT00000000000000000399", Name = "subject", Type = new TypeRef { Builtin = "string" } }] };
        Assert.Empty(ExprSites.SubjectMembers(model, named).Attributes);
    }

    [Fact]
    public async Task A_scenario_object_of_a_covered_entity_gives_a_stereotype_value_and_its_row_holds_what_a_guard_reads()
    {
        var scenario = $$"""
            {"kind":"scenario","id":"{{ScenarioId}}","name":"Rush","process":"{{Approval}}",
             "objects":[{"id":"01JSCN00000000000000000391","name":"rush","entity":"{{RushOrder}}","values":{"{{RequestedBy}}":"ann"} }],
             "subject":"01JSCN00000000000000000391","steps":[{"id":"01JSTP00000000000000000391","event":"01JPRC00000000000000000911"}]}
            """;
        var (h, store) = await OpenAsync(x =>
        {
            x.Write("model/scenarios/approval/rush.json", scenario);
            x.Write(ApprovalFile, ApprovalProcess(DefaultEntries, RequesterGuard));
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var diagnostics = await ActorKit.ValidateAsync(h, model);
        Assert.True(!diagnostics.Any(d => d.Rule == "MQ9305"), Describe(diagnostics));

        var objects = model.Get<Scenario>(ScenarioId)!.Objects;
        var row = ScenarioObjects.Row(model, model.Get<Process>(Approval)!, objects, "01JSCN00000000000000000391");
        Assert.Equal("ann", row.GetProperty("requestedBy").GetString());
        Assert.False(row.TryGetProperty("reviewers", out var _r));

        // Without a guard that reads one, the row is what it was.
        var plain = model.Get<Process>(Approval)! with { Guards = [] };
        Assert.False(ScenarioObjects.Row(model, plain, objects, "01JSCN00000000000000000391").TryGetProperty("requestedBy", out var _p));
    }

    // ---- incremental validation (section 5) ----

    [Fact]
    public async Task Carrier_changes_recheck_the_processes_that_list_a_stereotype_or_read_one_of_its_attributes()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        foreach (var changed in new Element[] { model.Get<Package>(Purchasing)!, model.Get<Entity>(Memo)!, model.Get<Stereotype>(Workflowed)! })
            Assert.Contains(ModelValidator.ProcessPeers(model, changed), d => d.Element.Id == Approval);

        // With entity entries only, a process whose guard reads a stereotype's attribute is re-checked too.
        var (h2, store2) = await OpenAsync(x => x.Write(ApprovalFile, ApprovalProcess(Entries($$"""{"id":"{{ContractEntry}}","entity":"{{Contract}}"}"""), RequesterGuard)));
        using var _2 = h2;
        await using var __2 = store2;
        var entityOnly = store2.Current!;
        Assert.False(SubjectCoverage.Of(entityOnly).Any);
        Assert.Contains(ModelValidator.ProcessPeers(entityOnly, entityOnly.Get<Package>(Sales)!), d => d.Element.Id == Approval);
        var diagnostics = await ActorKit.ValidateAsync(h2, entityOnly);
        Assert.True(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("approval", StringComparison.Ordinal)), Describe(diagnostics));
    }

    // ---- tools (decisions 9 to 11) ----

    [Fact]
    public async Task Set_lifecycle_counts_a_covering_stereotype_as_listing_the_entity()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.SetLifecycle, PurchaseOrder, null, null, Target: Approval)]), ChangeSource.Editor, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(Approval, File(h, "model/entities/purchase-order.json")["lifecycle"]!.GetValue<string>());
        Assert.Equal(3, File(h, ApprovalFile)["subjects"]!.AsArray().Count);
    }

    [Fact]
    public async Task Sync_enum_and_sync_codes_pick_a_stereotype_entry()
    {
        var unbound = Entries($$"""{"id":"{{StereotypeEntry}}","stereotype":"approvable"}""", $$"""{"id":"{{ContractEntry}}","entity":"{{Contract}}","boundAttribute":"{{ContractStatus}}"}""");
        var (h, store) = await OpenAsync(x => x.Write(ApprovalFile, ApprovalProcess(unbound)));
        using var _ = h;
        await using var __ = store;

        // An entity covered only through the stereotype entry is refused: codes sync through the stereotype's own attribute.
        var through = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.SyncCodes, Approval, null, null, Entity: PurchaseOrder, Attribute: ApprovalStatus)]), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, through.Outcome);
        Assert.Contains("through stereotype 'approvable'", Assert.Single(through.Items[0].Diagnostics, d => d.Rule == "MQ9019").Message, StringComparison.Ordinal);
        var notOwn = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.SyncCodes, Approval, null, null, Attribute: ContractStatus, Stereotype: "approvable")]), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, notOwn.Outcome);

        var bound = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.SyncCodes, Approval, null, null, Attribute: ApprovalStatus, Stereotype: "approvable")]), ChangeSource.Editor, Ct);
        Assert.True(bound.Outcome == SaveOutcome.Saved, string.Join(" | ", bound.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(ApprovalStatus, File(h, ApprovalFile)["subjects"]![0]!["boundAttribute"]!.GetValue<string>());

        // sync-enum on the stereotype entry's enum: a member the chart has and the enum lacks is added.
        var (h2, store2) = await OpenAsync(x => x.Write("model/enums/approval-status.json", $$"""
            {"kind":"enum","id":"{{ApprovalStatusEnum}}","name":"ApprovalStatus","package":"{{Sales}}",
             "members":[{"id":"01JMEM00000000000000000301","name":"Draft"},{"id":"01JMEM00000000000000000302","name":"Submitted"}]}
            """));
        using var _2 = h2;
        await using var __2 = store2;
        var synced = await store2.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.SyncEnum, Approval, null, null, Stereotype: "approvable")]), ChangeSource.Editor, Ct);
        Assert.True(synced.Outcome == SaveOutcome.Saved, string.Join(" | ", synced.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(["Draft", "Submitted", "Done"], File(h2, "model/enums/approval-status.json")["members"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()));
        var missing = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.SyncEnum, Approval, null, null, Stereotype: "reviewed")]), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, missing.Outcome);
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_a_listed_stereotype_drops_its_entries(DeleteResolution resolution)
    {
        var entries = Entries($$"""{"id":"01JPRX00000000000000000331","stereotype":"reviewed"}""", $$"""{"id":"01JPRX00000000000000000332","entity":"{{Memo}}"}""");
        var (h, store) = await OpenAsync(x => x.Write("model/processes/audit.json", ApprovalProcess(entries, id: Audit, name: "Audit")));
        using var _ = h;
        await using var __ = store;
        Assert.Contains(store.Current!.ReferencesTo(Reviewed), r => r.FromElementId == Audit && r.JsonPointer == "/subjects/0/stereotype");

        var reviewed = (await store.GetElementAsync(Reviewed, Ct))!;
        var deleted = await store.DeleteAsync(Reviewed, reviewed.Hash, resolution, ChangeSource.Editor, Ct);

        Assert.True(deleted.Outcome == SaveOutcome.Saved, string.Join(" | ", deleted.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(["01JPRX00000000000000000332"], File(h, "model/processes/audit.json")["subjects"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
    }
}
