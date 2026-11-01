using System.Collections.Frozen;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Migrations;

/// <summary>
/// The classifier core (next-phase-1-design.md section 3.2, task A9): two captures of one database and the write's key maps in,
/// facts out; <c>after</c> heads, schema renames, pending facts that follow their column, and the fact documents.
/// </summary>
public sealed class FactClassifierTests
{
    private const string Db = "01JFDB00000000000000000001";
    private const string Notes = "01JFTB00000000000000000001";
    private const string Body = "01JFCL00000000000000000001";
    private const string Kind = "01JFCL00000000000000000002";
    private const string Schema = "01JFSC00000000000000000001";
    private const string Existing = "01JFCT00000000000000000001";

    // ---- captures ----

    private static SnapshotColumn Column(string key, string name, string native, bool nullable = true, string type = "string", int? length = null) =>
        new() { Key = key, Name = name, Type = type, Length = length, NativeType = native, Nullable = nullable };

    private static SnapshotTable Table(string key, string name, params SnapshotColumn[] columns) =>
        new() { Key = key, Name = name, Schema = "billing", Columns = columns };

    private static PhysicalSnapshot Capture(params SnapshotTable[] tables) => new()
    {
        Database = Db, Name = "main", Dialect = Dialect.PostgreSql, Schemas = [new SnapshotSchema { Key = Schema, Name = "billing" }], Tables = tables,
    };

    private static SnapshotTable NotesTable(string bodyName = "body", string bodyType = "text", bool bodyNullable = true) =>
        Table(Notes, "notes", Column(Body, bodyName, bodyType, bodyNullable), Column(Kind, "kind", "varchar(20)"));

    private static ClassifierResult Classify(PhysicalSnapshot before, PhysicalSnapshot after, IReadOnlyList<Migration>? facts = null,
        IReadOnlySet<string>? written = null, IReadOnlyList<TableRekey>? rekeys = null, IReadOnlyDictionary<string, string>? sequences = null,
        Dialect dialect = Dialect.PostgreSql) =>
        FactClassifier.Classify(new ClassifierInput
        {
            DatabaseId = Db, Dialect = dialect, Before = before, After = after, OpenFacts = facts ?? [], WrittenFacts = written ?? FrozenSet<string>.Empty,
            Rekeys = rekeys ?? [], SequenceRekeys = sequences ?? FrozenDictionary<string, string>.Empty,
        }, new SequentialIdGenerator(7));

    private static MigrationObject BodyObject(string name = "body") =>
        new() { Kind = "column", Table = Notes, Key = Body, Schema = "billing", TableName = "notes", Name = name };

    // ---- the mapping ----

    [Fact]
    public void A_column_rename_records_one_complete_rename_with_the_names_at_the_time()
    {
        var result = Classify(Capture(NotesTable("text")), Capture(NotesTable("body")));

        var fact = Assert.Single(result.Created);
        Assert.Empty(result.Updated);
        Assert.Equal(MigrationFact.Rename, fact.Fact);
        Assert.Equal(Db, fact.Database);
        Assert.Equal(BodyObject("text"), fact.Object);
        Assert.Equal("text", fact.From!.Name);
        Assert.Equal("body", fact.To!.Name);
        Assert.Empty(fact.After);
        Assert.Equal("", fact.Name);
        Assert.Equal("complete", MigrationRules.State(fact, Dialect.PostgreSql));
        Assert.Equal(new SequentialIdGenerator(7).NewId(), fact.Id);
    }

    [Fact]
    public void A_narrowing_records_a_pending_change_type_and_a_widening_records_nothing()
    {
        var narrowed = Classify(Capture(NotesTable(bodyType: "text")), Capture(NotesTable(bodyType: "varchar(20)")));
        var fact = Assert.Single(narrowed.Created);
        Assert.Equal(MigrationFact.ChangeType, fact.Fact);
        Assert.Equal("text", fact.From!.NativeType);
        Assert.Equal("varchar(20)", fact.To!.NativeType);
        Assert.Equal("string", fact.To.Type);
        Assert.Equal(["conversion", "dataCheck"], MigrationRules.Missing(fact, Dialect.PostgreSql));

        Assert.True(Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "text"))).IsEmpty);
        Assert.True(Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "VARCHAR(40)"))).IsEmpty);
        Assert.True(Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "character varying(20)"))).IsEmpty);
    }

    [Fact]
    public void Jsonb_to_json_and_other_conversions_record_a_change_type_pending_its_conversion()
    {
        var result = Classify(Capture(NotesTable(bodyType: "jsonb")), Capture(NotesTable(bodyType: "json")));
        var fact = Assert.Single(result.Created);
        Assert.Equal(MigrationFact.ChangeType, fact.Fact);
        Assert.Equal(["conversion"], MigrationRules.Missing(fact, Dialect.PostgreSql));

        // Outside PostgreSQL there is no widening table: every change of type is a conversion.
        var sqlite = Classify(Capture(NotesTable(bodyType: "varchar(20)")) with { Dialect = Dialect.Sqlite },
            Capture(NotesTable(bodyType: "varchar(40)")) with { Dialect = Dialect.Sqlite }, dialect: Dialect.Sqlite);
        Assert.Equal(MigrationFact.ChangeType, Assert.Single(sqlite.Created).Fact);
    }

    private static SnapshotTable StatusTable(string? strategy, string? referenceTable, string native = "varchar(10)") =>
        Table(Notes, "notes", Column(Body, "status", native, type: "reference") with { ReferenceType = "01JFRT00000000000000000001", Strategy = strategy, ReferenceTable = referenceTable });

    [Fact]
    public async Task A_reference_columns_strategy_or_bound_table_records_a_change_type_that_validates()
    {
        const string Lookup = "01JFTB00000000000000000009";
        var strategy = Assert.Single(Classify(Capture(StatusTable("check", null)), Capture(StatusTable("lookup", null))).Created);
        var bound = Assert.Single(Classify(Capture(StatusTable("check", null)), Capture(StatusTable("check", Lookup))).Created);

        // The sides record the reference storage, so they differ though the type stays varchar(10).
        Assert.Equal(MigrationFact.ChangeType, strategy.Fact);
        Assert.Equal(("check", "lookup"), (strategy.From!.Strategy, strategy.To!.Strategy));
        Assert.Equal((null, Lookup), (bound.From!.ReferenceTable, bound.To!.ReferenceTable));
        Assert.Equal("change-type column billing.notes.status varchar(10) (check) to varchar(10) (bound table)", FactDocuments.Summary(bound));

        // Each validates with only its missing conversion (no MQ4106 for equal sides) and is a valid document.
        var model = new ModelBuilder();
        var main = model.Database("main", Dialect.PostgreSql);
        model.Add(strategy with { Database = main.Id }).Add(bound with { Id = "01JFCT00000000000000000009", Database = main.Id, After = [strategy.Id] });
        var report = await ValidationFixture.Validator().ValidateAsync(model.Build(), ValidationScope.All, null, TestContext.Current.CancellationToken);
        Assert.Equal([("MQ4101", "/conversion"), ("MQ4101", "/conversion")], report.Diagnostics.Select(d => (d.Rule, d.JsonPointer)));
        foreach (var fact in new[] { strategy, bound })
        {
            var path = FactDocuments.PathOf(fact, _ => "model/databases/main");
            using var document = JsonDocument.Parse(FactDocuments.Bytes(TestServices.Json, fact, path));
            Assert.Empty(TestServices.Schemas.Evaluate("migration.json", document.RootElement, path));
        }

        // A narrower type with the new storage is a narrowing: it also needs a data check.
        var narrowed = Assert.Single(Classify(Capture(StatusTable("check", null, "varchar(20)")), Capture(StatusTable("lookup", null))).Created);
        Assert.Equal(["conversion", "dataCheck"], MigrationRules.Missing(narrowed, Dialect.PostgreSql));
    }

    [Fact]
    public void A_pending_reference_change_follows_its_column_and_is_left_when_undone()
    {
        var pending = Assert.Single(Classify(Capture(StatusTable("check", null)), Capture(StatusTable("lookup", null))).Created) with { Id = Existing };

        // A further storage change rewrites its to; going back to its from leaves it as it is (stale).
        var updated = Assert.Single(Classify(Capture(StatusTable("lookup", null)), Capture(StatusTable("enum", null)), facts: [pending]).Updated);
        Assert.Equal(("check", "enum"), (updated.From!.Strategy, updated.To!.Strategy));
        Assert.True(Classify(Capture(StatusTable("lookup", null)), Capture(StatusTable("check", null)), facts: [pending]).IsEmpty);
    }

    [Fact]
    public void A_materialize_that_moves_a_bound_table_records_no_change_type()
    {
        const string Entity = "01JFEN00000000000000000001";
        var projected = Entity + "@" + Db;
        var lookup = Table(projected, "statuses", Column("code", "code", "varchar(10)"));
        var stored = Table("01JFTB00000000000000000009", "statuses", Column("01JFCL00000000000000000009", "code", "varchar(10)"));
        var rekeys = new[] { new TableRekey(projected, stored.Key, new Dictionary<string, string> { ["code"] = "01JFCL00000000000000000009" }) };

        var facts = Classify(Capture(StatusTable("check", projected), lookup), Capture(StatusTable("check", stored.Key), stored), rekeys: rekeys).Created;
        Assert.Equal(MigrationFact.Rekey, Assert.Single(facts).Fact);
    }

    [Fact]
    public void Made_not_null_records_a_backfill_and_made_nullable_records_nothing()
    {
        var tightened = Classify(Capture(NotesTable(bodyNullable: true)), Capture(NotesTable(bodyNullable: false)));
        var fact = Assert.Single(tightened.Created);
        Assert.Equal(MigrationFact.Backfill, fact.Fact);
        Assert.Equal(BodyObject(), fact.Object);
        Assert.Equal(["value"], MigrationRules.Missing(fact, Dialect.PostgreSql));

        Assert.True(Classify(Capture(NotesTable(bodyNullable: false)), Capture(NotesTable(bodyNullable: true))).IsEmpty);
    }

    [Fact]
    public void Dropping_a_column_and_adding_one_with_its_name_records_a_drop_and_nothing_for_the_add()
    {
        const string NewBody = "01JFCL00000000000000000009";
        var after = Table(Notes, "notes", Column(NewBody, "body", "text"), Column(Kind, "kind", "varchar(20)"));
        var fact = Assert.Single(Classify(Capture(NotesTable()), Capture(after)).Created);
        Assert.Equal(MigrationFact.Drop, fact.Fact);
        Assert.Equal(BodyObject(), fact.Object);
        Assert.Equal(["acknowledge"], MigrationRules.Missing(fact, Dialect.PostgreSql));
    }

    [Fact]
    public void A_dropped_table_records_one_drop_and_none_for_its_columns()
    {
        var other = Table("01JFTB00000000000000000002", "invoices", Column("01JFCL00000000000000000003", "number", "text"));
        var fact = Assert.Single(Classify(Capture(NotesTable(), other), Capture(other)).Created);
        Assert.Equal(MigrationFact.Drop, fact.Fact);
        Assert.Equal(new MigrationObject { Kind = "table", Key = Notes, Schema = "billing", Name = "notes" }, fact.Object);
    }

    [Fact]
    public void A_schema_rename_records_one_fact_and_its_tables_record_nothing()
    {
        var before = Capture(NotesTable(), Table("01JFTB00000000000000000002", "invoices"));
        var after = before with
        {
            Schemas = [new SnapshotSchema { Key = Schema, Name = "accounts" }],
            Tables = [.. before.Tables.Select(t => t with { Schema = "accounts" })],
        };
        var fact = Assert.Single(Classify(before, after).Created);
        Assert.Equal(MigrationFact.Rename, fact.Fact);
        Assert.Equal(new MigrationObject { Kind = "schema", Key = Schema, Name = "billing" }, fact.Object);
        Assert.Equal("billing", fact.From!.Name);
        Assert.Equal("accounts", fact.To!.Name);

        // A table renamed in the same write records its rename, named in the renamed schema (the schema rename comes first).
        var renamed = after with { Tables = [after.Tables[0] with { Name = "memos" }, after.Tables[1]] };
        var facts = Classify(before, renamed).Created;
        Assert.Equal(["schema", "table"], facts.Select(f => f.Object!.Kind));
        Assert.Equal(new MigrationState { Name = "notes", Schema = "accounts" }, facts[1].From);
        Assert.Equal(new MigrationState { Name = "memos", Schema = "accounts" }, facts[1].To);
        Assert.Empty(facts[1].After); // another table: no head to follow

        // A table moved to another schema is a rename.
        var moved = before with { Tables = [before.Tables[0] with { Schema = "archive" }, before.Tables[1]] };
        var move = Assert.Single(Classify(before, moved).Created);
        Assert.Equal("billing", move.From!.Schema);
        Assert.Equal("archive", move.To!.Schema);
    }

    [Fact]
    public void Views_and_sequences_record_renames_and_sequences_drops()
    {
        var view = new SnapshotView { Key = "01JFVW00000000000000000001", Name = "open_notes", Schema = "billing", Body = "select 1" };
        var sequence = new SnapshotSequence { Key = "01JFSQ00000000000000000001", Name = "note_numbers", Schema = "billing" };
        var before = Capture(NotesTable()) with { Views = [view], Sequences = [sequence] };

        var renamed = Classify(before, before with { Views = [view with { Name = "notes_open" }], Sequences = [sequence with { Name = "note_ids" }] }).Created;
        Assert.Equal([("sequence", "note_numbers", "note_ids"), ("view", "open_notes", "notes_open")],
            renamed.Select(f => (f.Object!.Kind, f.From!.Name!, f.To!.Name!)));

        var dropped = Classify(before, before with { Views = [], Sequences = [] }).Created;
        var drop = Assert.Single(dropped); // a dropped view records nothing
        Assert.Equal(("sequence", MigrationFact.Drop), (drop.Object!.Kind, drop.Fact));
    }

    [Fact]
    public void Indexes_constraints_defaults_comments_and_added_objects_record_nothing()
    {
        var before = Capture(NotesTable());
        var after = Capture(
            NotesTable() with
            {
                Comment = "Notes on anything.",
                Indexes = [new SnapshotIndex { Key = "ix:" + Body, Name = "ix_notes_body", Columns = [new SnapshotIndexColumn { Column = Body }] }],
                Columns = [NotesTable().Columns[0] with { DefaultSql = "''", Comment = "The text." }, NotesTable().Columns[1], Column("01JFCL00000000000000000004", "added", "text")],
            },
            Table("01JFTB00000000000000000003", "attachments", Column("01JFCL00000000000000000005", "path", "text")));
        Assert.True(Classify(before, after).IsEmpty);
    }

    // ---- key maps ----

    [Fact]
    public void Materialize_key_maps_record_one_rekey_and_no_drop()
    {
        const string Entity = "01JFEN00000000000000000001";
        var projected = Entity + "@" + Db;
        var before = Capture(Table(projected, "notes", Column("body", "body", "text"), Column("kind", "kind", "varchar(20)")));
        var after = Capture(NotesTable());
        var rekeys = new[] { new TableRekey(projected, Notes, new Dictionary<string, string> { ["body"] = Body, ["kind"] = Kind }) };

        var fact = Assert.Single(Classify(before, after, rekeys: rekeys).Created);
        Assert.Equal(MigrationFact.Rekey, fact.Fact);
        Assert.Equal("complete", MigrationRules.State(fact, Dialect.PostgreSql));
        Assert.Equal(
            [("table", null, projected, Notes, "notes"), ("column", projected, "body", Body, "body"), ("column", projected, "kind", Kind, "kind")],
            fact.Keys.Select(k => (k.Kind, k.Table, k.From, k.To, k.Name)));

        // Without the key map the same change reads as a drop and an add.
        Assert.Equal(MigrationFact.Drop, Assert.Single(Classify(before, after).Created).Fact);

        // The rekey comes first: a rename in the same write names the moved keys and follows it.
        var renamed = Capture(NotesTable("content"));
        var facts = Classify(before, renamed, rekeys: rekeys).Created;
        Assert.Equal([MigrationFact.Rekey, MigrationFact.Rename], facts.Select(f => f.Fact));
        Assert.Equal(Body, facts[1].Object!.Key);
        Assert.Equal(Notes, facts[1].Object!.Table);
        Assert.Equal([facts[0].Id], facts[1].After);
    }

    [Fact]
    public void Undoing_a_materialize_records_the_rekey_back_and_no_drop()
    {
        const string Entity = "01JFEN00000000000000000001";
        var projected = Entity + "@" + Db;
        var materialize = new Migration
        {
            Id = Existing, Database = Db, Fact = MigrationFact.Rekey,
            Keys =
            [
                new MigrationRekey { Kind = "table", From = projected, To = Notes, Name = "notes" },
                new MigrationRekey { Kind = "column", Table = projected, From = "body", To = Body, Name = "body" },
                new MigrationRekey { Kind = "column", Table = projected, From = "kind", To = Kind, Name = "kind" },
            ],
        };
        var before = Capture(NotesTable());
        var after = Capture(Table(projected, "notes", Column("body", "body", "text"), Column("kind", "kind", "varchar(20)")));

        var fact = Assert.Single(Classify(before, after, facts: [materialize]).Created);
        Assert.Equal(MigrationFact.Rekey, fact.Fact);
        Assert.Equal(
            [("table", null, Notes, projected), ("column", Notes, Body, "body"), ("column", Notes, Kind, "kind")],
            fact.Keys.Select(k => (k.Kind, k.Table, k.From, k.To)));
        Assert.Equal([Existing], fact.After);
    }

    [Fact]
    public void A_rekey_the_write_creates_is_a_key_map_and_is_not_recorded_again()
    {
        const string Entity = "01JFEN00000000000000000001";
        var projected = Entity + "@" + Db;
        var authored = new Migration
        {
            Id = Existing, Database = Db, Fact = MigrationFact.Rekey,
            Keys = [new MigrationRekey { Kind = "table", From = projected, To = Notes }, new MigrationRekey { Kind = "column", Table = projected, From = "body", To = Body }, new MigrationRekey { Kind = "column", Table = projected, From = "kind", To = Kind }],
        };
        var before = Capture(Table(projected, "notes", Column("body", "body", "text"), Column("kind", "kind", "varchar(20)")));
        Assert.True(Classify(before, Capture(NotesTable()), facts: [authored], written: new HashSet<string> { Existing }).IsEmpty);
    }

    // ---- after heads and order ----

    [Fact]
    public void After_is_set_to_the_heads_on_the_same_table_and_one_write_chains()
    {
        var invoices = Table("01JFTB00000000000000000002", "invoices", Column("01JFCL00000000000000000003", "number", "text"));
        // Open facts: A and B on notes (B follows A), C on invoices.
        var a = new Migration { Id = "01JFCT00000000000000000001", Database = Db, Fact = MigrationFact.Rename, Object = BodyObject("text"), From = new() { Name = "text" }, To = new() { Name = "body" } };
        var b = new Migration
        {
            Id = "01JFCT00000000000000000002", Database = Db, Fact = MigrationFact.Rename, After = [a.Id],
            Object = new MigrationObject { Kind = "column", Table = Notes, Key = Kind, Schema = "billing", TableName = "notes", Name = "type" }, From = new() { Name = "type" }, To = new() { Name = "kind" },
        };
        var c = new Migration
        {
            Id = "01JFCT00000000000000000003", Database = Db, Fact = MigrationFact.Rename,
            Object = new MigrationObject { Kind = "table", Key = invoices.Key, Schema = "billing", Name = "bills" }, From = new() { Name = "bills" }, To = new() { Name = "invoices" },
        };
        // One write renames the notes table, narrows its body and makes it not null.
        var after = Capture(NotesTable(bodyType: "varchar(20)", bodyNullable: false) with { Name = "memos" }, invoices);
        var facts = Classify(Capture(NotesTable(), invoices), after, facts: [c, b, a]).Created;

        Assert.Equal([MigrationFact.Rename, MigrationFact.ChangeType, MigrationFact.Backfill], facts.Select(f => f.Fact));
        Assert.Equal([b.Id], facts[0].After); // b follows a, so b is the only head of notes
        Assert.Equal([facts[0].Id], facts[1].After);
        Assert.Equal([facts[1].Id], facts[2].After);
        var ids = new SequentialIdGenerator(7);
        Assert.Equal([ids.NewId(), ids.NewId(), ids.NewId()], facts.Select(f => f.Id));
        // Members carry the table's name at the time: after its rename.
        Assert.Equal("memos", facts[1].Object!.TableName);
        Assert.Equal("notes", facts[0].Object!.Name);
    }

    [Fact]
    public void Facts_of_one_write_chain_by_table_key_then_member_key_and_type()
    {
        const string Other = "01JFTB00000000000000000000"; // sorts before notes
        var other = Table(Other, "events", Column("01JFCL00000000000000000008", "at", "timestamp(3)"));
        var before = Capture(NotesTable(), other);
        var after = Capture(Table(Notes, "notes", Column(Body, "content", "varchar(20)")), Table(Other, "events", Column("01JFCL00000000000000000008", "at", "date")));
        var facts = Classify(before, after).Created;
        // events first (its key sorts first); then notes: the body column's rename and change-type, then the kind column's drop.
        Assert.Equal(
            [(Other, MigrationFact.ChangeType), (Notes, MigrationFact.Rename), (Notes, MigrationFact.ChangeType), (Notes, MigrationFact.Drop)],
            facts.Select(f => (f.Object!.Table!, f.Fact)));
        Assert.Equal("content", facts[2].Object!.Name); // after its rename
        Assert.Empty(facts[0].After);
        Assert.Empty(facts[1].After);
        Assert.Equal([facts[1].Id], facts[2].After);
        Assert.Equal([facts[2].Id], facts[3].After);
    }

    // ---- pending facts follow their column ----

    private static Migration PendingNarrowing() => new()
    {
        Id = Existing, Database = Db, Fact = MigrationFact.ChangeType, Object = BodyObject(),
        From = new MigrationState { Type = "string", NativeType = "text" }, To = new MigrationState { Type = "string", NativeType = "varchar(20)" },
    };

    [Fact]
    public void A_second_narrowing_of_a_pending_column_keeps_one_pending_fact()
    {
        var result = Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "varchar(10)")), facts: [PendingNarrowing()]);
        Assert.Empty(result.Created);
        var updated = Assert.Single(result.Updated);
        Assert.Equal(Existing, updated.Id);
        Assert.Equal("text", updated.From!.NativeType);
        Assert.Equal("varchar(10)", updated.To!.NativeType);
        Assert.Equal(["conversion", "dataCheck"], MigrationRules.Missing(updated, Dialect.PostgreSql));
    }

    [Fact]
    public void A_widening_after_a_pending_narrowing_keeps_the_fact_pending()
    {
        var result = Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "varchar(30)")), facts: [PendingNarrowing()]);
        Assert.Empty(result.Created);
        var updated = Assert.Single(result.Updated);
        Assert.Equal("varchar(30)", updated.To!.NativeType);
        Assert.Equal("pending", MigrationRules.State(updated, Dialect.PostgreSql));
    }

    [Fact]
    public void A_pending_fact_whose_column_goes_back_or_widens_past_its_from_is_left_as_it_is()
    {
        Assert.True(Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "text")), facts: [PendingNarrowing()]).IsEmpty);
        var fromVarchar = PendingNarrowing() with { From = new MigrationState { Type = "string", NativeType = "varchar(40)" } };
        Assert.True(Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "varchar(50)")), facts: [fromVarchar]).IsEmpty);
    }

    [Fact]
    public void A_completed_or_held_change_type_does_not_follow_its_column()
    {
        var complete = PendingNarrowing() with { Conversion = new MigrationConversion { Cast = true }, DataCheck = new MigrationDataCheck { Accept = "short numbers" } };
        var result = Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "varchar(10)")), facts: [complete]);
        var fact = Assert.Single(result.Created);
        Assert.Equal("varchar(20)", fact.From!.NativeType);
        Assert.Equal([Existing], fact.After);
        Assert.Empty(result.Updated);

        var held = PendingNarrowing() with { Held = true };
        Assert.Single(Classify(Capture(NotesTable(bodyType: "varchar(20)")), Capture(NotesTable(bodyType: "varchar(10)")), facts: [held]).Created);
    }

    [Fact]
    public void A_change_a_written_fact_explains_is_not_recorded_again()
    {
        var authored = new Migration
        {
            Id = Existing, Database = Db, Fact = MigrationFact.Rename, Object = BodyObject("text"), From = new() { Name = "text" }, To = new() { Name = "body" },
        };
        Assert.True(Classify(Capture(NotesTable("text")), Capture(NotesTable("body")), facts: [authored], written: new HashSet<string> { Existing }).IsEmpty);
        // The same fact already open (not written by this write) explains nothing new: a second rename is recorded.
        Assert.Single(Classify(Capture(NotesTable("body")), Capture(NotesTable("content")), facts: [authored]).Created);
    }

    // ---- documents and real captures ----

    [Fact]
    public void Fact_documents_are_canonical_and_placed_in_the_databases_open_folder()
    {
        var fact = Assert.Single(Classify(Capture(NotesTable(bodyType: "text")), Capture(NotesTable(bodyType: "varchar(20)"))).Created);
        var path = FactDocuments.PathOf(fact, id => id == Db ? "model/databases/main" : null);
        Assert.Equal("model/databases/main/migrations/open/" + fact.Id.ToLowerInvariant() + ".json", path);
        var bytes = FactDocuments.Bytes(TestServices.Json, fact, path);
        Assert.True(TestServices.Json.IsCanonical(bytes, "migration.json", path));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("{\n  \"$schema\": \"../../../../../.schema/v1/migration.json\",\n  \"kind\": \"migration\",\n  \"id\": \"" + fact.Id + "\",\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  \"name\": ", text, StringComparison.Ordinal); // no top-level name: a recorded fact has none
        using (var document = JsonDocument.Parse(bytes))
            Assert.Empty(TestServices.Schemas.Evaluate("migration.json", document.RootElement, path));
        Assert.Equal("change-type column billing.notes.body text to varchar(20)", FactDocuments.Summary(fact));

        // The same write classified twice writes the same bytes.
        var again = Assert.Single(Classify(Capture(NotesTable(bodyType: "text")), Capture(NotesTable(bodyType: "varchar(20)"))).Created);
        Assert.Equal(bytes, FactDocuments.Bytes(TestServices.Json, again, path));
    }

    [Fact]
    public void Summaries_name_what_each_fact_records()
    {
        var rename = Assert.Single(Classify(Capture(NotesTable("text")), Capture(NotesTable("body"))).Created);
        Assert.Equal("rename column billing.notes.text to body", FactDocuments.Summary(rename));
        var drop = Assert.Single(Classify(Capture(NotesTable()), Capture()).Created);
        Assert.Equal("drop table billing.notes", FactDocuments.Summary(drop));
        var projected = "01JFEN00000000000000000001@" + Db;
        var rekey = Assert.Single(Classify(Capture(Table(projected, "notes", Column("body", "body", "text"), Column("kind", "kind", "varchar(20)"))), Capture(NotesTable()),
            rekeys: [new TableRekey(projected, Notes, new Dictionary<string, string> { ["body"] = Body, ["kind"] = Kind })]).Created);
        Assert.Equal("rekey 1 table, 2 columns", FactDocuments.Summary(rekey));
    }

    [Fact]
    public void Captures_of_resolved_models_classify_a_native_type_change()
    {
        static PhysicalSnapshot Resolve(string nativeType)
        {
            var model = new ModelBuilder(3);
            var db = model.Database("main", Dialect.PostgreSql);
            model.Table("notes", db).Column("id", "int64").Column("body", "string", length: 200, nativeType: nativeType).PrimaryKey("id");
            var resolved = ResolutionKit.Resolve(model);
            return SnapshotCapture.Capture(resolved.Db("main"), 0);
        }

        var before = Resolve("text");
        var after = Resolve("varchar(20)");
        var fact = Assert.Single(FactClassifier.Classify(new ClassifierInput { DatabaseId = before.Database, Dialect = Dialect.PostgreSql, Before = before, After = after },
            new SequentialIdGenerator(9)).Created);
        Assert.Equal(MigrationFact.ChangeType, fact.Fact);
        Assert.Equal("body", fact.Object!.Name);
        Assert.Equal(("text", "varchar(20)"), (fact.From!.NativeType, fact.To!.NativeType));
        Assert.Equal(200, fact.To.Length);
        Assert.True(FactClassifier.Classify(new ClassifierInput { DatabaseId = before.Database, Dialect = Dialect.PostgreSql, Before = before, After = before },
            new SequentialIdGenerator(9)).IsEmpty);
    }
}
