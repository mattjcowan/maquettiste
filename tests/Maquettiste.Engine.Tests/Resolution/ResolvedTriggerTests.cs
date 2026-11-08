using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The resolved model of trigger points (trigger-points-design.md section 7; section 15's <c>ResolvedTriggerTests</c>):
/// <c>model.trigger_lifecycles</c>, a lifecycle's <c>trigger_points</c> and <c>trigger_verbs</c>, a point's members and <c>covers</c>,
/// <c>entity.trigger_lifecycles</c>, <c>verb.trigger_points</c>, a step's <c>assume</c> words and its expected warnings and points, and what a
/// project without trigger points resolves. The model is built in memory and invented.
/// </summary>
public sealed class ResolvedTriggerTests
{
    private const string Document = "01JRTE00000000000000000001";
    private const string Drawing = "01JRTE00000000000000000002"; // derived from Document
    private const string Sketch = "01JRTE00000000000000000003"; // derived from Drawing
    private const string Employee = "01JRTE00000000000000000004";
    private const string Price = "01JRTA00000000000000000001";
    private const string Title = "01JRTA00000000000000000002";
    private const string ArchivedOn = "01JRTA00000000000000000003";
    private const string Salary = "01JRTA00000000000000000004";
    private const string Orders = "01JRTN00000000000000000001";
    private const string OrderEnd = "01JRTN00000000000000000002";
    private const string Order = "01JRTE00000000000000000005";
    private const string OrdersRelation = "01JRTL00000000000000000001";

    private const string Documents = "01JRTP00000000000000000001";
    private const string Hr = "01JRTP00000000000000000002";
    private const string Drawings = "01JRTP00000000000000000003"; // a lifecycle of Drawing, whose only object-verb point is narrowed to Sketch
    private const string Onboarding = "01JRTP00000000000000000004"; // an orchestration of Employee

    private const string Draft = "01JRTS00000000000000000001";
    private const string Review = "01JRTS00000000000000000002";
    private const string Submit = "01JRTT00000000000000000001";

    private const string AssignNumber = "01JRTQ00000000000000000001";
    private const string TitleBeforeReview = "01JRTQ00000000000000000002";
    private const string LargePriceDrop = "01JRTQ00000000000000000003";
    private const string Archive = "01JRTQ00000000000000000004";
    private const string CreditLimit = "01JRTQ00000000000000000005";
    private const string PublishToPortal = "01JRTQ00000000000000000006";
    private const string DrawingsOnly = "01JRTQ00000000000000000007";
    private const string SalaryBand = "01JRTQ00000000000000000011";
    private const string SketchesOnly = "01JRTQ00000000000000000021";
    private const string DrawingPromote = "01JRTQ00000000000000000022";

    private const string Scenario = "01JRTC00000000000000000001";
    private const string Spec = "01JRTC00000000000000000002";
    private const string Step = "01JRTC00000000000000000003";

    private static Expr Text(string text) => new() { Text = text };

    private static ModelSnapshot Model(bool points = true, Func<Element, Element>? edit = null)
    {
        var documents = Lifecycle(Documents, "Documents", Document, [new ProcessState { Id = Draft, Name = "Draft" }, new ProcessState { Id = Review, Name = "Review" }]) with
        {
            Events = [new ProcessEvent { Id = "01JRTX00000000000000000001", Name = "submit", Verb = "promote" }],
            Transitions = [new ProcessTransition { Id = Submit, Source = Draft, Event = "01JRTX00000000000000000001", Targets = [Review] }],
            TriggerPoints = !points ? [] :
            [
                new ProcessTriggerPoint
                {
                    Id = AssignNumber, Name = "AssignNumber", Type = TriggerPointType.Action, Timing = TriggerTiming.Before, Verbs = ["create"],
                    Description = new Description { Text = "Gives the document its number." },
                },
                new ProcessTriggerPoint
                {
                    Id = TitleBeforeReview, Name = "TitleBeforeReview", DisplayName = "Title before review", Type = TriggerPointType.Check, Verbs = ["promote"],
                    Targets = [Review], Transitions = [Submit], Condition = Text("coalesce(subject.title, '') <> ''"),
                },
                new ProcessTriggerPoint
                {
                    Id = LargePriceDrop, Name = "LargePriceDrop", Type = TriggerPointType.Check, Severity = TriggerSeverity.Warning, Verbs = ["modify"],
                    Attributes = [Price], States = [Draft], When = Text("true"), Condition = Text("coalesce(old.price, 0) = 0 or subject.price >= old.price * 0.9"),
                },
                new ProcessTriggerPoint
                {
                    Id = Archive, Name = "Archive", Type = TriggerPointType.Replace, Verbs = ["delete"],
                    Sets = [new ProcessSet { Id = "01JRTV00000000000000000001", Attribute = ArchivedOn, Value = Text("now()") }],
                },
                new ProcessTriggerPoint
                {
                    Id = CreditLimit, Name = "CreditLimit", Type = TriggerPointType.Check, Verbs = ["connect"], Ends = [Orders], Outside = true,
                },
                new ProcessTriggerPoint
                {
                    Id = PublishToPortal, Name = "PublishToPortal", Type = TriggerPointType.Action, Timing = TriggerTiming.AfterCommit, Verbs = ["promote"],
                    Targets = [Review], Outside = true,
                },
                new ProcessTriggerPoint { Id = DrawingsOnly, Name = "DrawingsOnly", Type = TriggerPointType.Check, Verbs = ["modify"], Entities = [Drawing] },
            ],
        };
        var hr = Lifecycle(Hr, "HR", Employee, [new ProcessState { Id = "01JRTS00000000000000000011", Name = "Active" }]) with
        {
            TriggerPoints = !points ? [] :
            [
                new ProcessTriggerPoint
                {
                    Id = SalaryBand, Name = "SalaryBand", Type = TriggerPointType.Check, Severity = TriggerSeverity.Warning, Verbs = ["modify"], Attributes = [Salary],
                },
            ],
        };
        var drawings = Lifecycle(Drawings, "Drawings", Drawing, [new ProcessState { Id = "01JRTS00000000000000000021", Name = "Open" }]) with
        {
            TriggerPoints = !points ? [] :
            [
                new ProcessTriggerPoint { Id = SketchesOnly, Name = "SketchesOnly", Type = TriggerPointType.Check, Verbs = ["modify"], Entities = [Sketch] },
                new ProcessTriggerPoint { Id = DrawingPromote, Name = "DrawingPromote", Type = TriggerPointType.Check, Verbs = ["promote"] },
            ],
        };

        List<Element> elements =
        [
            new Entity
            {
                Id = Document, Name = "Document",
                Attributes =
                [
                    new ModelAttribute { Id = Price, Name = "price", Type = new TypeRef { Builtin = "decimal" } },
                    new ModelAttribute { Id = Title, Name = "title", Type = new TypeRef { Builtin = "string" } },
                    new ModelAttribute { Id = ArchivedOn, Name = "archivedOn", Type = new TypeRef { Builtin = "datetimeoffset" } },
                ],
            },
            new Entity { Id = Drawing, Name = "Drawing", Base = Document },
            new Entity { Id = Sketch, Name = "Sketch", Base = Drawing },
            new Entity { Id = Order, Name = "Order" },
            new Entity
            {
                Id = Employee, Name = "Employee",
                Attributes = [new ModelAttribute { Id = Salary, Name = "salary", Type = new TypeRef { Builtin = "decimal" } }],
            },
            new Relation
            {
                Id = OrdersRelation, Name = "DocumentOrders",
                Ends =
                [
                    new RelationEnd { Id = Orders, Entity = Document, Role = "orders", Navigation = "orders" },
                    new RelationEnd { Id = OrderEnd, Entity = Order, Role = "document", Navigation = "document", Max = MaxCardinality.One },
                ],
            },
            documents, hr, drawings,
            new Process
            {
                Id = Onboarding, Name = "Onboarding", Use = ProcessUse.Orchestration,
                SubjectList = [new ProcessSubject { Id = "01JRTB00000000000000000004", Entity = Employee }],
                States = [new ProcessState { Id = "01JRTS00000000000000000041", Name = "Start" }],
            },
            new Scenario
            {
                Id = Scenario, Name = "connectRefused", Process = Documents, Subject = Spec,
                Objects = [new ScenarioObject { Id = Spec, Name = "spec", Entity = Document }],
                Steps =
                [
                    new ScenarioStep
                    {
                        Id = Step, Input = StepInput.Verb, Verb = "modify",
                        Values = new Dictionary<string, JsonElement> { [Price] = JsonSerializer.SerializeToElement(80) },
                        Assume = points
                            ? new Dictionary<string, StepAssumption> { [CreditLimit] = AssumedCheck.Error, ["01JRTG00000000000000000001"] = true }
                            : new Dictionary<string, StepAssumption> { ["01JRTG00000000000000000001"] = true },
                        Expect = points
                            ? new StepExpectation { CheckWarnings = [LargePriceDrop], TriggerPoints = [LargePriceDrop, LargePriceDrop, "01JRTQ99999999999999999999"] }
                            : null,
                    },
                ],
            },
        ];
        if (edit is not null)
            elements = [.. elements.Select(edit)];
        var docs = elements.Select((e, i) => Doc(e, $"model/x/{i:D2}-{e.Name}.json")).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        return ModelSnapshot.Create(docs, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
    }

    private static Process Lifecycle(string id, string name, string entity, IReadOnlyList<ProcessState> states) => new()
    {
        Id = id, Name = name, Use = ProcessUse.Lifecycle, SubjectList = [new ProcessSubject { Id = "01JRTB" + id[6..], Entity = entity }], States = states,
    };

    private static ElementDocument Doc(Element element, string path)
    {
        var bytes = TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
        var hash = ContentHash.Of(bytes);
        using var json = JsonDocument.Parse(bytes);
        return new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null);
    }

    private static RProcess ProcessOf(ResolvedModel model, string id) => model.Processes.Single(p => p.Id == id);

    private static RTriggerPoint PointOf(ResolvedModel model, string id) => Assert.IsType<RTriggerPoint>(model.Find(id));

    // What a unit that read these keys hashes to over a snapshot: a changed hash renders or replays it again.
    private static string HashOf(ModelSnapshot snapshot, IEnumerable<string> keys) =>
        new DependencyHasher(snapshot, ResolutionKit.Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>())
            .InputHash("unit", [.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    private static Func<Element, Element> OnProcess(string id, Func<Process, Process> edit) => e => e is Process p && p.Id == id ? edit(p) : e;

    [Fact]
    public void Narrowing_a_point_by_entities_leaves_what_an_entity_lists_and_its_keys_as_they_were()
    {
        // Drawings' only object-verb point is narrowed to Sketch; widening it lists the same lifecycles for Drawing, under keys whose hash holds.
        var narrowed = Model();
        var widened = Model(edit: OnProcess(Drawings, p => p with { TriggerPoints = [.. p.TriggerPoints.Select(x => x.Id == SketchesOnly ? x with { Entities = [] } : x)] }));
        var before = ResolutionKit.Resolve(narrowed);
        Assert.Equal(before.Entity("Drawing").TriggerLifecycles.Select(l => l.Id), ResolutionKit.Resolve(widened).Entity("Drawing").TriggerLifecycles.Select(l => l.Id));
        Assert.Equal(TriggerNaming.Hash(narrowed), TriggerNaming.Hash(widened));
        var document = before.Entity("Document").TriggerLifecycles.MembershipKeys;
        Assert.Equal(HashOf(narrowed, document), HashOf(widened, document));

        // covers reads entities, so it renders again.
        var covers = PointOf(before, SketchesOnly).Covers.MembershipKeys;
        Assert.NotEqual(HashOf(narrowed, covers), HashOf(widened, covers));
    }

    [Fact]
    public void A_lifecycles_first_trigger_point_renders_again_what_reads_the_trigger_point_lists()
    {
        var none = Model(edit: OnProcess(Drawings, p => p with { TriggerPoints = [] }));
        var model = ResolutionKit.Resolve(none);
        var promote = model.Verbs.Single(v => v.Key == "promote").TriggerPoints;
        Assert.DoesNotContain(promote, x => x.Id == DrawingPromote);
        var first = Model(edit: OnProcess(Drawings, p => p with
        {
            TriggerPoints = [new ProcessTriggerPoint { Id = DrawingPromote, Name = "DrawingPromote", Type = TriggerPointType.Check, Verbs = ["promote"] }],
        }));
        Assert.NotEqual(HashOf(none, promote.MembershipKeys), HashOf(first, promote.MembershipKeys)); // the verbs page
        Assert.NotEqual(HashOf(none, model.TriggerLifecycles.MembershipKeys), HashOf(first, model.TriggerLifecycles.MembershipKeys)); // shared units
        var drawing = model.Entity("Drawing").TriggerLifecycles.MembershipKeys;
        var modify = Model(edit: OnProcess(Drawings, p => p with
        {
            TriggerPoints = [new ProcessTriggerPoint { Id = SketchesOnly, Name = "SketchesOnly", Type = TriggerPointType.Check, Verbs = ["modify"] }],
        }));
        Assert.NotEqual(HashOf(none, drawing), HashOf(modify, drawing)); // a per-type runner
    }

    [Fact]
    public void A_scenario_replays_when_another_lifecycle_with_a_modify_point_starts_governing_its_subject()
    {
        var before = Model();
        var keys = ResolutionKit.Resolve(before).Find(Step)!.Dependencies;
        Assert.Contains(TriggerNaming.Key, keys);
        Assert.DoesNotContain("e:" + Drawings, keys);
        var governing = Model(edit: OnProcess(Drawings, p => p with { SubjectList = [new ProcessSubject { Id = "01JRTB" + Drawings[6..], Entity = Document }] }));
        Assert.NotEqual(HashOf(before, keys), HashOf(governing, keys));
        Assert.Contains("e:" + Drawings, ResolutionKit.Resolve(governing).Find(Step)!.Dependencies);

        // A project without trigger points: the verb step keys on the naming key, whose hash never changes there.
        var plain = ResolutionKit.Resolve(Model(points: false)).Find(Step)!.Dependencies;
        Assert.Contains(TriggerNaming.Key, plain);
        Assert.DoesNotContain("e:" + Hr, plain);
    }

    [Fact]
    public void Records_carry_trigger_points_and_expectations_only_where_present()
    {
        var model = ResolutionKit.Resolve(Model());
        var documents = (ProcessRecord)Engine.ResolvedRecords.Entries(model, "processes", null).Single(e => e.Id == Documents).Project();
        var archive = Assert.Single(documents.TriggerPoints!, t => t.Id == Archive);
        Assert.Equal("replace", archive.Type);
        Assert.Equal(["delete"], archive.Verbs);
        var scenario = (ScenarioRecord)Engine.ResolvedRecords.Entries(model, "scenarios", null).Single(e => e.Id == Scenario).Project();
        var step = Assert.Single(scenario.Steps);
        Assert.Equal([LargePriceDrop], step.Expect!.CheckWarnings);
        Assert.Contains(step.Assume.Values, v => v is "error");

        var plain = ResolutionKit.Resolve(Model(points: false));
        var records = Engine.ResolvedRecords.Entries(plain, "processes", null).Select(e => (ProcessRecord)e.Project()).ToList();
        Assert.All(records, r => Assert.Null(r.TriggerPoints));
        Assert.DoesNotContain("triggerPoints", JsonSerializer.Serialize(records, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_without_trigger_points_resolves_empty_lists_keyed_on_the_naming_key_alone()
    {
        var snapshot = Model(points: false);
        var model = ResolutionKit.Resolve(snapshot);

        Assert.Empty(model.TriggerLifecycles);
        Assert.Equal([TriggerNaming.Key], model.TriggerLifecycles.MembershipKeys);
        Assert.All(model.Processes, p => Assert.Empty(p.TriggerPoints));
        Assert.All(model.Processes, p => Assert.Empty(p.TriggerVerbs));
        Assert.All(model.Entities, e => Assert.Equal([TriggerNaming.Key], e.TriggerLifecycles.MembershipKeys));
        Assert.All(model.Entities, e => Assert.Empty(e.TriggerLifecycles));
        Assert.All(model.Verbs, v => Assert.Equal([TriggerNaming.Key], v.TriggerPoints.MembershipKeys));
        var step = Assert.Single(Assert.Single(model.Scenarios).Steps);
        Assert.Equal(new Dictionary<string, object?> { ["01JRTG00000000000000000001"] = true }, step.Assume);
        Assert.Empty(step.Trace!.TriggerPoints);
        Assert.Empty(step.Trace.CheckWarnings);

        // The naming key's hash is the same for any project without trigger points.
        var renamed = Model(points: false, edit: e => e is Entity { Id: Document } d ? d with { Name = "Paper" } : e);
        Assert.Equal(TriggerNaming.Hash(snapshot), TriggerNaming.Hash(renamed));
    }

    [Fact]
    public void A_lifecycle_lists_its_trigger_points_in_file_order_with_their_members()
    {
        var model = ResolutionKit.Resolve(Model());
        var documents = ProcessOf(model, Documents);

        Assert.Equal([AssignNumber, TitleBeforeReview, LargePriceDrop, Archive, CreditLimit, PublishToPortal, DrawingsOnly],
            documents.TriggerPoints.Select(p => p.Id));
        Assert.All(documents.TriggerPoints, p => Assert.Same(documents, p.Process));
        Assert.All(documents.TriggerPoints, p => Assert.Equal("process-trigger-point", p.Kind));

        var number = PointOf(model, AssignNumber);
        Assert.Equal(("action", "before", null, true, false), (number.Type, number.Timing, number.Severity, number.TeamCode, number.Outside));
        Assert.Equal(("AssignNumber", "Gives the document its number."), (number.DisplayName, number.Message));
        Assert.Equal(["create"], number.Verbs.Select(v => v.Key));

        var title = PointOf(model, TitleBeforeReview);
        Assert.Equal(("check", null, "error", false), (title.Type, title.Timing, title.Severity, title.TeamCode));
        Assert.Equal("Title before review", title.Message); // no description: the display name
        Assert.Equal(["Review"], title.Targets.Select(s => s.Name));
        Assert.Equal([Submit], title.Transitions.Select(t => t.Id));
        Assert.NotNull(title.Condition);
        Assert.Null(title.When);

        // old. reads the object before the verb, so its paths end at the declaring entity's attribute too.
        var drop = PointOf(model, LargePriceDrop);
        Assert.Equal(("warning", "LargePriceDrop"), (drop.Severity, drop.Message));
        Assert.Equal(["Draft"], drop.States.Select(s => s.Name));
        Assert.Equal(["price"], drop.Attributes.Select(a => a.Name));
        Assert.NotNull(drop.When);
        Assert.Contains(Paths(drop.Condition!), p => p is RAttribute { Name: "price" });

        var archive = PointOf(model, Archive);
        Assert.Equal(("replace", false), (archive.Type, archive.TeamCode));
        var set = Assert.Single(archive.Sets);
        Assert.Equal("archivedOn", set.Attribute.Name);
        Assert.Null(set.Action);

        var credit = PointOf(model, CreditLimit);
        Assert.True(credit.Outside);
        Assert.Equal([Orders], credit.Ends.Select(e => e.Id));
        Assert.Equal(["Drawing"], PointOf(model, DrawingsOnly).Entities.Select(e => e.Name));
        Assert.Empty(ProcessOf(model, Onboarding).TriggerPoints);
    }

    [Fact]
    public void Trigger_verbs_group_a_lifecycles_points_per_verb_by_moment_in_the_verb_tables_order()
    {
        var model = ResolutionKit.Resolve(Model());
        var verbs = ProcessOf(model, Documents).TriggerVerbs;

        // The verb table's order: promote, a move verb, comes before the object and link verbs.
        Assert.Equal(["promote", "create", "modify", "delete", "connect"], verbs.Select(v => v.Verb!.Key));
        var promote = verbs.Single(v => v.Verb!.Key == "promote");
        Assert.Equal(Documents + "/trigger-verb/promote", promote.Id);
        Assert.Equal([TitleBeforeReview], promote.Checks.Select(p => p.Id));
        Assert.Equal([PublishToPortal], promote.AfterCommit.Select(p => p.Id));
        Assert.Empty(promote.Before);
        Assert.Equal([LargePriceDrop, DrawingsOnly], verbs.Single(v => v.Verb!.Key == "modify").Checks.Select(p => p.Id));
        Assert.Equal([Archive], verbs.Single(v => v.Verb!.Key == "delete").Replace.Select(p => p.Id));
        Assert.Equal([AssignNumber], verbs.Single(v => v.Verb!.Key == "create").Before.Select(p => p.Id));

        Assert.Contains("e:" + Documents, verbs.MembershipKeys);
    }

    [Fact]
    public void Covers_lists_every_entity_a_point_runs_for_and_entities_narrows_it()
    {
        var model = ResolutionKit.Resolve(Model());

        Assert.Equal(["Document", "Drawing", "Sketch"], PointOf(model, Archive).Covers.Select(e => e.Name));
        Assert.Equal(["Drawing", "Sketch"], PointOf(model, DrawingsOnly).Covers.Select(e => e.Name));
        Assert.Equal(["Sketch"], PointOf(model, SketchesOnly).Covers.Select(e => e.Name));
        var keys = PointOf(model, Archive).Covers.MembershipKeys;
        Assert.Contains("e:" + Documents, keys);
        Assert.Contains("r:" + Document, keys);
        Assert.Contains("r:" + Drawing, keys); // a new entity derived from Drawing
        Assert.Contains("r:" + Sketch, keys);
    }

    [Fact]
    public void Model_and_entity_trigger_lifecycles_and_verb_trigger_points()
    {
        var model = ResolutionKit.Resolve(Model());

        // By name then id.
        Assert.Equal(["Documents", "Drawings", "HR"], model.TriggerLifecycles.Select(p => p.Name));
        Assert.Equal([TriggerNaming.Key], model.TriggerLifecycles.MembershipKeys);

        // Every governing lifecycle with a point on an object, link or own verb, whatever its entities: Drawings' modify point is narrowed to
        // Sketch, and Drawing still lists it; Document is not governed by Drawings.
        Assert.Equal(["Documents"], model.Entity("Document").TriggerLifecycles.Select(p => p.Name));
        Assert.Equal(["Documents", "Drawings"], model.Entity("Drawing").TriggerLifecycles.Select(p => p.Name));
        Assert.Equal(["Documents", "Drawings"], model.Entity("Sketch").TriggerLifecycles.Select(p => p.Name));
        Assert.Equal(["HR"], model.Entity("Employee").TriggerLifecycles.Select(p => p.Name));
        Assert.Empty(model.Entity("Order").TriggerLifecycles);
        var keys = model.Entity("Drawing").TriggerLifecycles.MembershipKeys;
        Assert.Contains(TriggerNaming.Key, keys);
        Assert.Contains("r:" + Document, keys);
        Assert.Contains("e:" + Drawings, keys);

        var modify = model.Verbs.Single(v => v.Key == "modify");
        Assert.Equal([LargePriceDrop, DrawingsOnly, SketchesOnly, SalaryBand], modify.TriggerPoints.Select(p => p.Id));
        Assert.Contains(TriggerNaming.Key, modify.TriggerPoints.MembershipKeys);
        Assert.Contains("e:" + Hr, modify.TriggerPoints.MembershipKeys);
    }

    [Fact]
    public void A_step_assumes_checks_by_name_and_expects_warnings_and_points()
    {
        var model = ResolutionKit.Resolve(Model(edit: e => e is Scenario s ? s with
        {
            Steps = [s.Steps[0] with { Assume = new Dictionary<string, StepAssumption>(s.Steps[0].Assume) { [SalaryBand] = AssumedCheck.Warning } }],
        } : e));
        var step = Assert.Single(Assert.Single(model.Scenarios).Steps);

        // The running lifecycle's check by its name, another lifecycle's as Lifecycle.Name, a boolean as before.
        Assert.Equal(new Dictionary<string, object?>
        {
            ["01JRTG00000000000000000001"] = true, ["CreditLimit"] = "error", ["HR.SalaryBand"] = "warning",
        }, step.Assume);
        Assert.Equal([LargePriceDrop], step.Expect!.CheckWarnings.Select(p => p.Id));
        Assert.Equal([LargePriceDrop, LargePriceDrop], step.Expect.TriggerPoints.Select(p => p.Id)); // an id that names nothing is left out
    }

    [Fact]
    public void Two_reached_lifecycles_with_one_name_key_their_checks_by_lifecycle_id()
    {
        var model = ResolutionKit.Resolve(Model(edit: e => e switch
        {
            Process { Id: Drawings } p => p with { Name = "HR" },
            Scenario s => s with
            {
                Objects = [s.Objects[0] with { Entity = Sketch }],
                Steps = [s.Steps[0] with { Assume = new Dictionary<string, StepAssumption> { [SketchesOnly] = AssumedCheck.Pass, [SalaryBand] = AssumedCheck.Warning } }],
            },
            _ => e,
        }));
        var step = Assert.Single(Assert.Single(model.Scenarios).Steps);

        Assert.Equal(new Dictionary<string, object?> { [Drawings + ".SketchesOnly"] = "pass", [Hr + ".SalaryBand"] = "warning" }, step.Assume);
    }

    [Fact]
    public async Task Templates_read_the_trigger_point_members()
    {
        var model = ResolutionKit.Resolve(Model());
        var text = (await Adhoc.RenderAsync(
            "{{ for l in model.trigger_lifecycles }}{{ l.name }}:{{ for v in l.trigger_verbs }} {{ v.verb.key }}({{ v.checks.size }}){{ end }}\n{{ end }}"
            + "{{ for p in (model.processes | array.first).trigger_points }}{{ p.name }} {{ p.type }} {{ p.timing }} {{ p.severity }} {{ p.team_code }} {{ p.covers.size }}\n{{ end }}",
            model: model)).Text();

        Assert.Contains("Documents: promote(1) create(0) modify(2) delete(0) connect(1)", text, StringComparison.Ordinal);
        Assert.Contains("TitleBeforeReview check  error false 3", text, StringComparison.Ordinal);
        Assert.Contains("PublishToPortal action after-commit  true 3", text, StringComparison.Ordinal);
    }

    private static IEnumerable<object?> Paths(RExpression expression) =>
        (expression.Node == "path" ? [expression.Target] : Enumerable.Empty<object?>()).Concat(expression.Args.SelectMany(Paths));
}
