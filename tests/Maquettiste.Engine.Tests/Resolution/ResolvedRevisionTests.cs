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
/// The resolved model of revisions (revisions-design.md section 8; section 17's "Resolver and keys"): <c>entity.revisions</c> for the drawing
/// and the invoice of section 6, <c>transition.next_revision</c>, the records of <c>get_resolved_model</c>, and the keys <c>entity.revisions</c>
/// and a scenario whose subject's type has revisions add (decision 42), with nothing new in a project without a block. The model is built in
/// memory and invented.
/// </summary>
public sealed class ResolvedRevisionTests
{
    private const string Record = "01JRVE00000000000000000001"; // holds revision and approvedBy
    private const string Drawing = "01JRVE00000000000000000002"; // derived from Record, writes the drawing's block
    private const string Sketch = "01JRVE00000000000000000003"; // derived from Drawing, no block of its own
    private const string Invoice = "01JRVE00000000000000000004"; // the invoice's block
    private const string Document = "01JRVE00000000000000000005";
    private const string Note = "01JRVE00000000000000000006";
    private const string Plain = "01JRVE00000000000000000007"; // no block anywhere

    private const string Revision = "01JRVA00000000000000000001";
    private const string ApprovedBy = "01JRVA00000000000000000002";
    private const string Approver = "01JRVA00000000000000000003"; // Invoice.approver

    private const string InvoiceDocuments = "01JRVL00000000000000000001";
    private const string InvoicesEnd = "01JRVN00000000000000000001";
    private const string DocumentsEnd = "01JRVN00000000000000000002";
    private const string RecordNotes = "01JRVL00000000000000000002";
    private const string RecordsEnd = "01JRVN00000000000000000003";
    private const string NotesEnd = "01JRVN00000000000000000004";

    private const string DrawingLifecycle = "01JRVP00000000000000000001";
    private const string Approval = "01JRVP00000000000000000002"; // governs Invoice
    private const string Delivery = "01JRVP00000000000000000003"; // governs Invoice
    private const string PlainLifecycle = "01JRVP00000000000000000004";

    private const string InWork = "01JRVS00000000000000000001";
    private const string Approved = "01JRVS00000000000000000002";
    private const string Released = "01JRVS00000000000000000003";
    private const string Release = "01JRVT00000000000000000001";
    private const string ReviseMove = "01JRVT00000000000000000002";
    private const string ReleaseEvent = "01JRVX00000000000000000001";
    private const string ReviseEvent = "01JRVX00000000000000000002";

    private const string DrawingScenario = "01JRVC00000000000000000001";
    private const string DrawingObject = "01JRVC00000000000000000002";
    private const string DrawingStep = "01JRVC00000000000000000003";
    private const string PlainScenario = "01JRVC00000000000000000004";
    private const string PlainObject = "01JRVC00000000000000000005";
    private const string PlainStep = "01JRVC00000000000000000006";

    private static readonly EntityRevisions DrawingBlock = new()
    {
        Levels =
        [
            new RevisionLevel { Name = "major", Format = RevisionFormat.Letters, Skip = ["I", "O", "Q", "S", "X", "Z"] },
            new RevisionLevel { Name = "minor", Format = RevisionFormat.Numbers, StartEmpty = true },
        ],
        BoundAttribute = Revision,
        Attributes = [new RevisionAttributeEntry { Attribute = ApprovedBy, OnRevise = AttributeOnRevise.Reset }],
    };

    private static readonly EntityRevisions InvoiceBlock = new()
    {
        Levels = [new RevisionLevel { Format = RevisionFormat.Numbers }],
        Lifecycles = [new RevisionLifecycleEntry { Lifecycle = Delivery, OnRevise = RevisionLifecycleEntry.Keep }],
        Attributes = [new RevisionAttributeEntry { Attribute = Approver, OnRevise = AttributeOnRevise.Reset }],
        Links = [new RevisionLinkEntry { End = DocumentsEnd, OnRevise = LinkOnRevise.Duplicate }],
    };

    private static ModelAttribute Text(string id, string name) => new() { Id = id, Name = name, Type = new TypeRef { Builtin = "string" } };

    private static Process Lifecycle(string id, string name, string entity, IReadOnlyList<ProcessState> states) => new()
    {
        Id = id, Name = name, Use = ProcessUse.Lifecycle, SubjectList = [new ProcessSubject { Id = "01JRVB" + id[6..], Entity = entity }], States = states,
    };

    private static ModelSnapshot Model(bool blocks = true, Func<Element, Element>? edit = null, IEnumerable<Element>? extra = null)
    {
        var drawings = Lifecycle(DrawingLifecycle, "DrawingLifecycle", Drawing,
            [new ProcessState { Id = InWork, Name = "InWork" }, new ProcessState { Id = Approved, Name = "Approved" }, new ProcessState { Id = Released, Name = "Released" }]) with
        {
            Events = [new ProcessEvent { Id = ReleaseEvent, Name = "release", Verb = "promote" }, new ProcessEvent { Id = ReviseEvent, Name = "revise", Verb = "revise" }],
            Transitions =
            [
                new ProcessTransition { Id = Release, Source = Approved, Event = ReleaseEvent, Targets = [Released], NextRevision = 1 },
                new ProcessTransition { Id = ReviseMove, Source = Released, Event = ReviseEvent, Targets = [InWork], NextRevision = 2 },
            ],
        };
        List<Element> elements =
        [
            new Entity { Id = Record, Name = "Record", Abstract = true, Attributes = [Text(Revision, "revision"), Text(ApprovedBy, "approvedBy")] },
            new Entity { Id = Drawing, Name = "Drawing", Base = Record, Revisions = blocks ? DrawingBlock : null },
            new Entity { Id = Sketch, Name = "Sketch", Base = Drawing },
            new Entity { Id = Invoice, Name = "Invoice", Attributes = [Text(Approver, "approver")], Revisions = blocks ? InvoiceBlock : null },
            new Entity { Id = Document, Name = "Document" },
            new Entity { Id = Note, Name = "Note" },
            new Entity { Id = Plain, Name = "Plain" },
            new Relation
            {
                Id = InvoiceDocuments, Name = "InvoiceDocuments",
                Ends = [new RelationEnd { Id = InvoicesEnd, Entity = Invoice, Role = "invoices" }, new RelationEnd { Id = DocumentsEnd, Entity = Document, Role = "documents" }],
            },
            new Relation
            {
                Id = RecordNotes, Name = "RecordNotes",
                Ends = [new RelationEnd { Id = RecordsEnd, Entity = Record, Role = "records" }, new RelationEnd { Id = NotesEnd, Entity = Note, Role = "notes" }],
            },
            drawings,
            Lifecycle(Approval, "Approval", Invoice, [new ProcessState { Id = "01JRVS00000000000000000011", Name = "Open" }]),
            Lifecycle(Delivery, "Delivery", Invoice, [new ProcessState { Id = "01JRVS00000000000000000021", Name = "Waiting" }]),
            Lifecycle(PlainLifecycle, "PlainLifecycle", Plain, [new ProcessState { Id = "01JRVS00000000000000000031", Name = "Open" }]) with
            {
                Events = [new ProcessEvent { Id = "01JRVX00000000000000000031", Name = "revise", Verb = "revise" }],
                Transitions = [new ProcessTransition { Id = "01JRVT00000000000000000031", Source = "01JRVS00000000000000000031", Event = "01JRVX00000000000000000031", Targets = ["01JRVS00000000000000000031"] }],
            },
            new Scenario
            {
                Id = DrawingScenario, Name = "reviseDrawing", Process = DrawingLifecycle, Subject = DrawingObject,
                Objects = [new ScenarioObject { Id = DrawingObject, Name = "drawing", Entity = Drawing, States = [Released] }],
                Steps = [new ScenarioStep { Id = DrawingStep, Event = ReviseEvent }],
            },
            new Scenario
            {
                Id = PlainScenario, Name = "revisePlain", Process = PlainLifecycle, Subject = PlainObject,
                Objects = [new ScenarioObject { Id = PlainObject, Name = "plain", Entity = Plain }],
                Steps = [new ScenarioStep { Id = PlainStep, Event = "01JRVX00000000000000000031" }],
            },
        ];
        if (edit is not null)
            elements = [.. elements.Select(edit)];
        elements.AddRange(extra ?? []);
        var docs = elements.Select((e, i) => Doc(e, $"model/x/{i:D2}-{e.Name}.json")).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        return ModelSnapshot.Create(docs, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
    }

    private static ElementDocument Doc(Element element, string path)
    {
        var bytes = TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
        var hash = ContentHash.Of(bytes);
        using var json = JsonDocument.Parse(bytes);
        return new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null);
    }

    private static Func<Element, Element> OnEntity(string id, Func<Entity, Entity> edit) => e => e is Entity x && x.Id == id ? edit(x) : e;

    // What a unit that read these keys hashes to over a snapshot: a changed hash renders or replays it again.
    private static string HashOf(ModelSnapshot snapshot, IEnumerable<string> keys) =>
        new DependencyHasher(snapshot, ResolutionKit.Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>())
            .InputHash("unit", [.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    private static IReadOnlyList<string> KeysOf(RRevisions revisions) => ((ITrackedValue)revisions).Dependencies;

    [Fact]
    public void The_drawing_reads_its_levels_examples_start_bound_attribute_and_the_fallback()
    {
        var model = ResolutionKit.Resolve(Model());
        var drawing = model.Entity("Drawing").Revisions!;

        Assert.Equal(".", drawing.Separator);
        Assert.Equal("A", drawing.FirstRevision);
        Assert.Equal("revision", drawing.BoundAttribute!.Name);
        var major = drawing.Levels[0];
        Assert.Equal((1, "major", "letters", "A", false, (int?)null), (major.Number, major.Name, major.Format, major.First, major.StartEmpty, major.Width));
        Assert.Equal(["I", "O", "Q", "S", "X", "Z"], major.Skip);
        Assert.Empty(major.Values);
        Assert.Equal(["A", "B", "C", "D", "E"], major.Examples);
        var minor = drawing.Levels[1];
        Assert.Equal((2, "minor", "numbers", "1", true), (minor.Number, minor.Name, minor.Format, minor.First, minor.StartEmpty));
        Assert.Equal(["1", "2", "3", "4", "5"], minor.Examples);
        Assert.Empty(minor.Skip);

        // The drawing's lifecycle starts over, the fallback, said by no one; approvedBy resets, said by the drawing.
        var lifecycle = Assert.Single(drawing.Lifecycles);
        Assert.Equal(("DrawingLifecycle", "restart", (RState?)null, (REntity?)null), (lifecycle.Lifecycle.Name, lifecycle.OnRevise, lifecycle.State, lifecycle.From));
        var reset = Assert.Single(drawing.Attributes);
        Assert.Equal(("approvedBy", "reset", "Drawing"), (reset.Attribute.Name, reset.OnRevise, reset.From.Name));
        Assert.Empty(drawing.Links);

        // A derived type with no block of its own reads its base's, said by the base.
        var sketch = model.Entity("Sketch").Revisions!;
        Assert.Equal("A", sketch.FirstRevision);
        Assert.Equal("Drawing", Assert.Single(sketch.Attributes).From.Name);
        Assert.Equal("DrawingLifecycle", Assert.Single(sketch.Lifecycles).Lifecycle.Name);

        // The moves: a promote that gives the next letter, a revise at the minor level; other transitions name none.
        var transitions = model.Processes.Single(p => p.Id == DrawingLifecycle).Transitions;
        Assert.Equal([(1, "promote"), (2, "revise")], transitions.Select(t => (t.NextRevision!.Value, t.Verb!.Key)));
        Assert.Null(Assert.Single(model.Processes.Single(p => p.Id == PlainLifecycle).Transitions).NextRevision);
    }

    [Fact]
    public void The_invoice_keeps_delivery_restarts_approval_resets_its_approver_and_duplicates_its_documents_links()
    {
        var model = ResolutionKit.Resolve(Model());
        var invoice = model.Entity("Invoice").Revisions!;

        Assert.Equal("1", invoice.FirstRevision);
        Assert.Null(invoice.BoundAttribute);
        var level = Assert.Single(invoice.Levels);
        Assert.Equal((1, (string?)null, "numbers", "1"), (level.Number, level.Name, level.Format, level.First));
        Assert.Equal(
            [("Approval", "restart", (string?)null), ("Delivery", "keep", "Invoice")],
            invoice.Lifecycles.Select(l => (l.Lifecycle.Name, l.OnRevise, l.From?.Name)));
        Assert.Equal(("approver", "reset", "Invoice"), (invoice.Attributes[0].Attribute.Name, invoice.Attributes[0].OnRevise, invoice.Attributes[0].From.Name));
        var link = Assert.Single(invoice.Links);
        Assert.Equal(("documents", "duplicate", "Invoice"), (link.End.Role, link.OnRevise, link.From.Name));

        Assert.Null(model.Entity("Plain").Revisions);
        Assert.Null(model.Entity("Document").Revisions);
    }

    [Fact]
    public void A_lifecycle_sent_to_a_state_and_a_list_level_read_as_written()
    {
        var model = ResolutionKit.Resolve(Model(edit: OnEntity(Invoice, e => e with
        {
            Revisions = InvoiceBlock with
            {
                Levels = [new RevisionLevel { Name = "stage", Values = ["Prototype", "Pilot run"] }, new RevisionLevel { Format = RevisionFormat.Numbers, First = JsonSerializer.SerializeToElement(0), Width = 2 }],
                Separator = "-",
                Lifecycles = [new RevisionLifecycleEntry { Lifecycle = Approval, OnRevise = "01JRVS00000000000000000011" }],
            },
        })));
        var invoice = model.Entity("Invoice").Revisions!;

        Assert.Equal(("-", "Prototype-00"), (invoice.Separator, invoice.FirstRevision));
        Assert.Equal(("list", "Prototype"), (invoice.Levels[0].Format, invoice.Levels[0].First));
        Assert.Equal(["Prototype", "Pilot run"], invoice.Levels[0].Values);
        Assert.Equal(["Prototype", "Pilot run"], invoice.Levels[0].Examples);
        Assert.Equal(("00", 2), (invoice.Levels[1].First, invoice.Levels[1].Width));
        Assert.Equal(["00", "01", "02", "03", "04"], invoice.Levels[1].Examples);
        var approval = invoice.Lifecycles.Single(l => l.Lifecycle.Id == Approval);
        Assert.Equal(("state", "Open", "Invoice"), (approval.OnRevise, approval.State!.Name, approval.From!.Name));
    }

    [Fact]
    public void The_records_carry_ids_and_are_left_out_when_unset()
    {
        var model = ResolutionKit.Resolve(Model());
        EntityRecord EntityRecordOf(string name) =>
            ResolvedRecords.Entries(model, "entities", null).Select(e => e.Project()).OfType<EntityRecord>().Single(r => r.Name == name);

        var invoice = EntityRecordOf("Invoice").Revisions!;
        Assert.Equal(("1", ".", (string?)null), (invoice.FirstRevision, invoice.Separator, invoice.BoundAttribute));
        Assert.Equal([new RevisionLevelRecord(1, null, "numbers", [], "1", [], null, false, ["1", "2", "3", "4", "5"])], invoice.Levels,
            (a, b) => a.Number == b.Number && a.Format == b.Format && a.First == b.First && a.Examples.SequenceEqual(b.Examples) && a.Name == b.Name);
        Assert.Equal([new RevisionLifecycleRecord(Approval, "restart", null, null), new RevisionLifecycleRecord(Delivery, "keep", null, Invoice)], invoice.Lifecycles);
        Assert.Equal([new RevisionAttributeRecord(Approver, "reset", Invoice)], invoice.Attributes);
        Assert.Equal([new RevisionLinkRecord(DocumentsEnd, "duplicate", Invoice)], invoice.Links);
        Assert.Equal(Revision, EntityRecordOf("Sketch").Revisions!.BoundAttribute);

        // Left out of the JSON when unset: an entity with no block in its family, a transition that names no level.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain("revisions", JsonSerializer.SerializeToNode(EntityRecordOf("Plain"), options)!.AsObject().Select(p => p.Key));
        var json = JsonSerializer.SerializeToNode(EntityRecordOf("Invoice"), options)!["revisions"]!;
        Assert.Null(json["boundAttribute"]);
        Assert.True(json.AsObject().ContainsKey("boundAttribute"));
        var process = ResolvedRecords.Entries(model, "processes", null).Select(e => e.Project()).OfType<ProcessRecord>().Single(p => p.Id == DrawingLifecycle);
        Assert.Equal([1, 2], process.Transitions.Select(t => t.NextRevision!.Value));
        var plain = ResolvedRecords.Entries(model, "processes", null).Select(e => e.Project()).OfType<ProcessRecord>().Single(p => p.Id == PlainLifecycle);
        Assert.DoesNotContain("nextRevision", JsonSerializer.SerializeToNode(Assert.Single(plain.Transitions), options)!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task Templates_read_the_members_and_record_the_keys_of_what_they_read()
    {
        var model = ResolutionKit.Resolve(Model());
        var unit = await Adhoc.RenderAsync(
            "{{ for e in model.entities }}{{ if e.revisions }}{{ e.name }} {{ e.revisions.first_revision }} {{ e.revisions.bound_attribute?.name }}"
            + "{{ for l in e.revisions.levels }} {{ l.number }}:{{ l.format }}:{{ l.examples | array.join ',' }}{{ end }}"
            + "{{ for l in e.revisions.lifecycles }} {{ l.lifecycle.name }}={{ l.on_revise }}/{{ l.from?.name }}{{ end }}"
            + "{{ for a in e.revisions.attributes }} {{ a.attribute.name }}={{ a.on_revise }}{{ end }}"
            + "{{ for k in e.revisions.links }} {{ k.end.role }}={{ k.on_revise }}{{ end }}\n{{ end }}{{ end }}"
            + "{{ for t in (model.processes | array.filter @(do; ret $0.name == 'DrawingLifecycle'; end) | array.first).transitions }}{{ t.verb.key }}:{{ t.next_revision }} {{ end }}",
            model: model);
        var text = unit.Text();

        Assert.Contains("Drawing A revision 1:letters:A,B,C,D,E 2:numbers:1,2,3,4,5 DrawingLifecycle=restart/ approvedBy=reset\n", text, StringComparison.Ordinal);
        Assert.Contains("Invoice 1  1:numbers:1,2,3,4,5 Approval=restart/ Delivery=keep/Invoice approver=reset documents=duplicate\n", text, StringComparison.Ordinal);
        Assert.Contains("Sketch A revision", text, StringComparison.Ordinal);
        Assert.Contains("promote:1 revise:2 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain", text, StringComparison.Ordinal);
        // Reading Sketch's revisions records its base's file and the relations at its bases.
        Assert.Contains("e:" + Drawing, unit.ReadKeys);
        Assert.Contains("e:" + RecordNotes, unit.ReadKeys);
    }

    [Fact]
    public void A_base_block_that_changes_renders_again_what_reads_a_derived_types_revisions()
    {
        var before = Model();
        var keys = KeysOf(ResolutionKit.Resolve(before).Entity("Sketch").Revisions!);
        var changed = Model(edit: OnEntity(Drawing, e => e with { Revisions = DrawingBlock with { Separator = "-" } }));

        Assert.Equal("-", ResolutionKit.Resolve(changed).Entity("Sketch").Revisions!.Separator);
        Assert.Contains("e:" + Drawing, keys);
        Assert.NotEqual(HashOf(before, keys), HashOf(changed, keys));
    }

    [Fact]
    public void A_new_lifecycle_that_starts_governing_the_entity_renders_again_what_reads_its_revisions()
    {
        var before = Model();
        var keys = KeysOf(ResolutionKit.Resolve(before).Entity("Sketch").Revisions!);
        const string Checking = "01JRVP00000000000000000009";
        var governing = Model(extra: [Lifecycle(Checking, "Checking", Drawing, [new ProcessState { Id = "01JRVS00000000000000000091", Name = "Checked" }])]);

        Assert.Equal(["Checking", "DrawingLifecycle"], ResolutionKit.Resolve(governing).Entity("Sketch").Revisions!.Lifecycles.Select(l => l.Lifecycle.Name));
        Assert.NotEqual(HashOf(before, keys), HashOf(governing, keys));
        Assert.Contains("e:" + Checking, KeysOf(ResolutionKit.Resolve(governing).Entity("Sketch").Revisions!));
    }

    [Fact]
    public void A_new_relation_that_adds_an_end_at_a_base_renders_again_what_reads_its_revisions()
    {
        // Sketch's block names an end of a relation no type of its family reaches yet, so the rule is left out; a new relation puts the end's
        // other side at Record, a base, and the rule applies.
        const string Sheets = "01JRVL00000000000000000009";
        const string SheetsEnd = "01JRVN00000000000000000091";
        var block = new EntityRevisions { Links = [new RevisionLinkEntry { End = SheetsEnd, OnRevise = LinkOnRevise.Move }] };
        var before = Model(edit: OnEntity(Sketch, e => e with { Revisions = block }));
        var keys = KeysOf(ResolutionKit.Resolve(before).Entity("Sketch").Revisions!);
        Assert.Empty(ResolutionKit.Resolve(before).Entity("Sketch").Revisions!.Links);
        var related = Model(edit: OnEntity(Sketch, e => e with { Revisions = block }), extra:
        [
            new Relation
            {
                Id = Sheets, Name = "RecordSheets",
                Ends = [new RelationEnd { Id = "01JRVN00000000000000000092", Entity = Record, Role = "records" }, new RelationEnd { Id = SheetsEnd, Entity = Note, Role = "sheets" }],
            },
        ]);

        Assert.Equal("sheets", Assert.Single(ResolutionKit.Resolve(related).Entity("Sketch").Revisions!.Links).End.Role);
        Assert.NotEqual(HashOf(before, keys), HashOf(related, keys));
    }

    [Fact]
    public void A_scenario_with_a_revise_event_replays_when_its_subjects_type_gains_a_block_changes_it_or_gains_a_governing_lifecycle()
    {
        var none = Model(blocks: false);
        var plainKeys = ResolutionKit.Resolve(none).Find(DrawingStep)!.Dependencies;
        var gains = Model();
        Assert.NotEqual(HashOf(none, plainKeys), HashOf(gains, plainKeys)); // through the subject's entity and its bases, which the steps key on

        var keys = ResolutionKit.Resolve(gains).Find(DrawingStep)!.Dependencies;
        var changed = Model(edit: OnEntity(Drawing, e => e with { Revisions = DrawingBlock with { Separator = "-" } }));
        Assert.NotEqual(HashOf(gains, keys), HashOf(changed, keys));

        // A new lifecycle that governs the subject's type changes what a revise does to it: that is the revisions keys'.
        const string Checking = "01JRVP00000000000000000009";
        var governing = Model(extra: [Lifecycle(Checking, "Checking", Drawing, [new ProcessState { Id = "01JRVS00000000000000000091", Name = "Checked" }])]);
        Assert.NotEqual(HashOf(gains, keys), HashOf(governing, keys));
        Assert.Contains("e:" + RecordNotes, keys);
    }

    [Fact]
    public void Nothing_is_keyed_without_a_block()
    {
        // A project without a block: no entity has revisions, and the scenarios' steps key as they did.
        var none = ResolutionKit.Resolve(Model(blocks: false));
        Assert.All(none.Entities, e => Assert.Null(e.Revisions));
        var plainStep = none.Find(PlainStep)!.Dependencies;
        var drawingStep = none.Find(DrawingStep)!.Dependencies;
        Assert.DoesNotContain("e:" + RecordNotes, drawingStep);
        Assert.DoesNotContain("r:" + Record, drawingStep);

        // In a project with blocks, a scenario whose subject's type has none keys as it did without them.
        var some = ResolutionKit.Resolve(Model());
        Assert.Equal(plainStep, some.Find(PlainStep)!.Dependencies);
        Assert.Contains("e:" + RecordNotes, some.Find(DrawingStep)!.Dependencies);
    }
}
