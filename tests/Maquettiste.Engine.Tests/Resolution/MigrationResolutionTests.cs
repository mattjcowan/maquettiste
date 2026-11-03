using System.Collections.Immutable;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Migration facts in the resolved model (next-phase-1-design.md section 3.2, "Applied order and projection"): each database's
/// facts in applied order, their keys moved through later rekey facts, and what they project onto tables and columns.
/// </summary>
public sealed class MigrationResolutionTests
{
    private const string F1 = "01JFCT00000000000000000001";
    private const string F2 = "01JFCT00000000000000000002";
    private const string F3 = "01JFCT00000000000000000003";
    private const string F4 = "01JFCT00000000000000000004";
    private const string F5 = "01JFCT00000000000000000005";
    private const string Dangling = "01JFCT0000000000000000000Z";

    /// <summary>A PostgreSQL database with a designed <c>notes</c> table (columns <c>body</c> and <c>author</c>).</summary>
    private static (ModelBuilder Model, DatabaseBuilder Db, TableBuilder Notes) Notes()
    {
        var model = new ModelBuilder();
        var db = model.Database("main", Dialect.PostgreSql);
        var notes = model.Table("notes", db).Column("id", "int64").Column("body", "text").Column("author", "string", length: 80).PrimaryKey("id");
        return (model, db, notes);
    }

    private static Migration Fact(string id, DatabaseBuilder db, MigrationFact fact, params string[] after) =>
        new() { Id = id, Database = db.Id, Fact = fact, After = after };

    private static MigrationObject Column(TableBuilder table, string key, string name) =>
        new() { Kind = "column", Table = table.Id, Key = key, Schema = "public", TableName = table.Name, Name = name };

    [Fact]
    public void Open_facts_resolve_in_after_order_with_ties_by_id()
    {
        var (model, db, notes) = Notes();
        var other = model.Database("audit", Dialect.PostgreSql);
        var table = new MigrationObject { Kind = "table", Key = notes.Id, Schema = "public", Name = "notes" };
        model.Add(Fact(F1, db, MigrationFact.Rename) with { Object = table with { Name = "memos" }, From = new() { Name = "memos" }, To = new() { Name = "notes" } })
            .Add(Fact(F2, db, MigrationFact.Rename, F3) with { Object = Column(notes, notes.ColumnId("author"), "writer"), From = new() { Name = "writer" }, To = new() { Name = "author" } })
            .Add(Fact(F3, db, MigrationFact.Rename) with { Object = Column(notes, notes.ColumnId("body"), "text"), From = new() { Name = "text" }, To = new() { Name = "body" } })
            .Add(Fact(F4, db, MigrationFact.Raw, F1, Dangling) with { Sql = ImmutableSortedDictionary<string, string>.Empty.Add("*", "select 1"), SkipWhen = ImmutableSortedDictionary<string, string>.Empty.Add("*", "select 1") })
            .Add(Fact(F5, other, MigrationFact.Raw) with { Sql = ImmutableSortedDictionary<string, string>.Empty.Add("*", "select 2"), SkipWhen = ImmutableSortedDictionary<string, string>.Empty.Add("*", "select 2") });

        var resolved = ResolutionKit.Resolve(model);
        var main = resolved.Db("main");

        // F1 and F3 are ready; F1 has the smaller id. Then F3, which frees F2; F4 follows F1 (its dangling link is ignored).
        Assert.Equal([F1, F3, F2, F4], main.Migrations.Select(m => m.Id));
        Assert.Equal([1, 2, 3, 4], main.Migrations.Select(m => m.Order));
        Assert.Equal([F1], main.Migrations.Single(m => m.Id == F4).After.Select(a => a.Id));
        Assert.Equal([F3], main.Migrations.Single(m => m.Id == F2).After.Select(a => a.Id));
        Assert.All(main.Migrations, m => Assert.Same(main, m.Database));
        Assert.Contains("k:migration", main.Migrations.MembershipKeys);
        Assert.Contains("e:" + F2, main.Migrations.MembershipKeys);

        // The model's list: by database name (audit before main), then applied order; FactKeys.Order is the same order.
        Assert.Equal([F5, F1, F3, F2, F4], resolved.Migrations.Select(m => m.Id));
        Assert.Equal(main.Migrations.Select(m => m.Id), FactKeys.Order(resolved.Source.All<Migration>().Where(m => m.Database == db.Id)).Select(m => m.Id));
        Assert.Same(main.Migrations[0], resolved.Find(F1));
        Assert.Equal("migration", main.Migrations[0].Kind);
        Assert.Equal(["e:" + db.Id, "e:" + F1], main.Migrations[0].Dependencies);
    }

    [Fact]
    public void A_cycle_still_orders_every_fact_by_the_smallest_id()
    {
        var (model, db, notes) = Notes();
        model.Add(Fact(F1, db, MigrationFact.Rename, F2) with { Object = Column(notes, notes.ColumnId("body"), "text"), From = new() { Name = "text" }, To = new() { Name = "body" } })
            .Add(Fact(F2, db, MigrationFact.Rename, F1) with { Object = Column(notes, notes.ColumnId("author"), "writer"), From = new() { Name = "writer" }, To = new() { Name = "author" } })
            .Add(Fact(F3, db, MigrationFact.Rename, F2) with { Object = new MigrationObject { Kind = "table", Key = notes.Id, Name = "memos" }, From = new() { Name = "memos" }, To = new() { Name = "notes" } });

        var main = ResolutionKit.Resolve(model).Db("main");
        Assert.Equal([F1, F2, F3], main.Migrations.Select(m => m.Id));
    }

    [Fact]
    public void Renames_and_conversions_project_onto_their_table_and_columns()
    {
        var (model, db, notes) = Notes();
        var body = notes.ColumnId("body");
        model.Add(Fact(F1, db, MigrationFact.Rename) with
            {
                Object = new MigrationObject { Kind = "table", Key = notes.Id, Schema = "public", Name = "memo" },
                From = new() { Name = "memo" }, To = new() { Name = "notes" }, Via = ["memos"],
            })
            .Add(Fact(F2, db, MigrationFact.Rename, F1) with { Object = Column(notes, body, "text"), From = new() { Name = "text" }, To = new() { Name = "content" } })
            .Add(Fact(F3, db, MigrationFact.Rename, F2) with { Object = Column(notes, body, "content"), From = new() { Name = "content" }, To = new() { Name = "body" } })
            .Add(Fact(F4, db, MigrationFact.ChangeType, F3) with
            {
                Object = Column(notes, body, "body"), From = new() { Type = "string", NativeType = "varchar(200)" }, To = new() { Type = "text", NativeType = "text" },
                Conversion = new MigrationConversion { Expression = ImmutableSortedDictionary<string, string>.Empty.Add("postgresql", "{column}::text").Add("*", "cast({column} as text)") },
            });

        var resolved = ResolutionKit.Resolve(model);
        var table = resolved.Db("main").Table("notes");
        Assert.Equal(["memo", "memos"], table.FormerNames);
        Assert.Equal(F1, table.Since);
        var column = table.Column("body");
        Assert.Equal(["text", "content"], column.FormerNames);
        Assert.Equal(F2, column.Since);
        Assert.NotNull(column.Conversion);
        Assert.Equal(F4, column.Conversion.FactId);
        Assert.False(column.Conversion.Cast);
        Assert.Equal("{column}::text", column.Conversion.Expression); // the dialect's entry wins over "*"

        // A projected table (and so its columns) depends on every fact projected onto it or its columns.
        foreach (var id in new[] { F1, F2, F3, F4 })
        {
            Assert.Contains("e:" + id, table.Dependencies);
            Assert.Contains("e:" + id, column.Dependencies);
            Assert.Contains("e:" + id, table.Columns.MembershipKeys);
        }

        // An object no fact names keeps empty projections.
        var author = table.Column("author");
        Assert.Empty(author.FormerNames);
        Assert.Null(author.Since);
        Assert.Null(author.Conversion);
    }

    [Fact]
    public void Renames_project_onto_views_and_sequences_and_facts_with_objects_set_since()
    {
        var (model, db, notes) = Notes();
        var author = notes.ColumnId("author");
        var view = new View { Id = model.NewId(), Name = "open_notes", Database = db.Id, Body = new Dictionary<string, string> { ["*"] = "SELECT id FROM notes" } };
        var sequence = new Sequence { Id = model.NewId(), Name = "note_numbers", Database = db.Id };
        model.Add(view).Add(sequence)
            .Add(Fact(F1, db, MigrationFact.Rename) with
            {
                Object = new MigrationObject { Kind = "view", Key = view.Id, Schema = "public", Name = "notes_open" },
                From = new() { Name = "notes_open" }, To = new() { Name = "open_notes" }, Via = ["live_notes"],
            })
            .Add(Fact(F2, db, MigrationFact.Rename) with
            {
                Object = new MigrationObject { Kind = "sequence", Key = sequence.Id, Schema = "public", Name = "note_ids" },
                From = new() { Name = "note_ids" }, To = new() { Name = "note_numbers" },
            })
            .Add(Fact(F3, db, MigrationFact.ValueMap) with
            {
                Objects = [Column(notes, author, "author")], ReferenceType = "01JFRT00000000000000000001",
                Values = [new MigrationValue { From = "anon", To = "unknown" }],
            })
            .Add(Fact(F4, db, MigrationFact.Rename, F3) with { Object = Column(notes, author, "writer"), From = new() { Name = "writer" }, To = new() { Name = "author" } });

        var main = ResolutionKit.Resolve(model).Db("main");

        // A view or sequence takes its renames' names and first fact, and depends on its own facts only.
        var rview = main.Views.Single(v => v.Id == view.Id);
        Assert.Equal(["notes_open", "live_notes"], rview.FormerNames);
        Assert.Equal(F1, rview.Since);
        Assert.Contains("e:" + F1, rview.Dependencies);
        Assert.DoesNotContain("e:" + F2, rview.Dependencies);
        var rsequence = main.Sequences.Single(s => s.Id == sequence.Id);
        Assert.Equal(["note_ids"], rsequence.FormerNames);
        Assert.Equal(F2, rsequence.Since);
        Assert.Contains("e:" + F2, rsequence.Dependencies);
        Assert.DoesNotContain("e:" + F1, rsequence.Dependencies);

        // A fact that names the column in objects sets its since and joins the table's dependencies, but adds no former name.
        var table = main.Table("notes");
        var column = table.Column("author");
        Assert.Equal(F3, column.Since);
        Assert.Equal(["writer"], column.FormerNames);
        Assert.Contains("e:" + F3, table.Dependencies);
        Assert.Contains("e:" + F4, column.Dependencies);
        Assert.Null(table.Since); // no fact names the table itself
    }

    [Fact]
    public void A_rename_recorded_before_a_rekey_projects_onto_the_stored_column()
    {
        // F1 renamed a column of a projected table (keys <entity>@<db> and the attribute id); F2, a materialize's rekey, moved both
        // keys to the stored table and its column; F3, recorded after the rekey, names the stored keys already.
        var (model, db, notes) = Notes();
        var body = notes.ColumnId("body");
        const string Entity = "01JFCT000000000000000000E1";
        const string Attribute = "01JFCT000000000000000000A1";
        var projected = Entity + "@" + db.Id;
        model.Add(Fact(F1, db, MigrationFact.Rename) with
            {
                Object = new MigrationObject { Kind = "column", Table = projected, Key = Attribute, Schema = "public", TableName = "notes", Name = "text" },
                From = new() { Name = "text" }, To = new() { Name = "body" },
            })
            .Add(Fact(F2, db, MigrationFact.Rekey, F1) with
            {
                Keys =
                [
                    new MigrationRekey { Kind = "table", From = projected, To = notes.Id, Name = "notes" },
                    new MigrationRekey { Kind = "column", Table = projected, From = Attribute, To = body, Name = "body" },
                ],
            })
            .Add(Fact(F3, db, MigrationFact.Backfill, F2) with { Object = Column(notes, body, "body"), Value = ImmutableSortedDictionary<string, string>.Empty.Add("*", "''") });

        var resolved = ResolutionKit.Resolve(model);
        var facts = resolved.Db("main").Migrations;
        var rename = facts.Single(f => f.Id == F1);
        Assert.Equal(projected, rename.Object!.Table);
        Assert.Equal(Attribute, rename.Object.Key);
        Assert.Equal(notes.Id, rename.Object.CurrentTable);
        Assert.Equal(body, rename.Object.CurrentKey);
        Assert.Contains("e:" + F2, rename.Dependencies); // its keys today depend on the rekey after it
        var backfill = facts.Single(f => f.Id == F3);
        Assert.Equal(body, backfill.Object!.CurrentKey);

        var column = resolved.Db("main").Table("notes").Column("body");
        Assert.Equal(["text"], column.FormerNames);
        Assert.Equal(F1, column.Since);
        Assert.Equal(["table", "column"], facts.Single(f => f.Id == F2).Keys.Select(k => k.Kind));
    }

    [Fact]
    public void Value_map_collections_move_with_later_rekeys()
    {
        // F1, a value map (next-phase-2-design.md section 3.5), names a collection of a projected table by its synthesized key; F2, a
        // materialize's rekey, moved that table to the designed one. F3, recorded after the rekey, names the designed table already.
        var (model, db, notes) = Notes();
        const string Entity = "01JFCT000000000000000000E1";
        const string Tags = "01JFCT000000000000000000A2";
        const string Type = "01JFCT000000000000000000T1";
        const string Merged = "01JFCT000000000000000000T2";
        var projected = Entity + "@" + db.Id;
        var collection = new MigrationCollection { Table = projected, Schema = "public", TableName = "notes", Attribute = Tags, Name = "tags" };
        model.Add(Fact(F1, db, MigrationFact.ValueMap) with
            {
                Collections = [collection], ReferenceType = Type, FromReferenceType = Merged,
                From = new() { Name = "NoteLabel", PluralName = "NoteLabels" }, To = new() { Name = "NoteTag", PluralName = "NoteTags" },
                Values = [new MigrationValue { From = "todo", To = "open" }],
            })
            .Add(Fact(F2, db, MigrationFact.Rekey, F1) with { Keys = [new MigrationRekey { Kind = "table", From = projected, To = notes.Id, Name = "notes" }] })
            .Add(Fact(F3, db, MigrationFact.ValueMap, F2) with
            {
                Collections = [collection with { Table = notes.Id }], ReferenceType = Type,
                From = new() { Name = "NoteTag" }, To = new() { Name = "NoteTag" }, Values = [new MigrationValue { From = "wip", To = "open" }],
            });

        var resolved = ResolutionKit.Resolve(model);
        var facts = resolved.Db("main").Migrations;
        var map = facts.Single(f => f.Id == F1);
        var moved = Assert.Single(map.Collections);
        Assert.Equal((projected, notes.Id, Tags, "tags"), (moved.Table, moved.CurrentTable, moved.Attribute, moved.Name));
        Assert.Equal(("public", "notes"), (moved.Schema, moved.TableName));
        Assert.Equal((Type, Merged), (map.ReferenceType, map.FromReferenceType));
        Assert.Equal(("NoteLabel", "NoteLabels", "NoteTag", "NoteTags"), (map.From!.Name, map.From.PluralName, map.To!.Name, map.To.PluralName));
        Assert.Contains("e:" + F2, map.Dependencies); // its keys today depend on the rekey after it
        Assert.Equal(notes.Id, Assert.Single(facts.Single(f => f.Id == F3).Collections).CurrentTable);
    }

    [Fact]
    public void State_missing_intent_and_dialect_entries_resolve_for_the_database()
    {
        var (model, db, notes) = Notes();
        var author = notes.ColumnId("author");
        var narrowing = Fact(F1, db, MigrationFact.ChangeType) with
        {
            Object = Column(notes, author, "author"), From = new() { Type = "string", NativeType = "text" }, To = new() { Type = "string", NativeType = "varchar(20)" },
        };
        model.Add(narrowing)
            .Add(Fact(F2, db, MigrationFact.Drop, F1) with { Object = Column(notes, "01JFCT0000000000000000C0L0", "legacy"), Held = true })
            .Add(Fact(F3, db, MigrationFact.Backfill, F2) with
            {
                Object = Column(notes, author, "author"),
                Value = ImmutableSortedDictionary<string, string>.Empty.Add("postgresql", "'unknown'").Add("*", "'?'"),
                Where = ImmutableSortedDictionary<string, string>.Empty.Add("*", "{column} = ''"),
                DataCheck = new MigrationDataCheck { Query = ImmutableSortedDictionary<string, string>.Empty.Add("sqlite", "select 1") },
            })
            .Add(Fact(F4, db, MigrationFact.Drop, F3) with { Object = Column(notes, "01JFCT0000000000000000C0L1", "old"), Held = true, Released = true, Acknowledge = "unused" });

        var resolved = ResolutionKit.Resolve(model);
        var facts = resolved.Db("main").Migrations.ToDictionary(f => f.Id);

        Assert.Equal("change-type", facts[F1].Fact);
        Assert.Equal("pending", facts[F1].State);
        Assert.Equal(["conversion", "dataCheck"], facts[F1].Missing);
        Assert.Equal("varchar(20)", facts[F1].To!.NativeType);
        Assert.Equal("", facts[F1].Name);

        Assert.Equal("held", facts[F2].State);
        Assert.Empty(facts[F2].Missing); // held: complete for validate until released

        Assert.Equal("complete", facts[F3].State);
        Assert.Equal("'unknown'", facts[F3].Value);
        Assert.Equal("{column} = ''", facts[F3].Where);
        Assert.Null(facts[F3].DataCheck!.Query); // no entry for postgresql and no "*" entry
        Assert.Null(facts[F3].Sql);
        Assert.Equal("after", facts[F3].Phase);

        Assert.Equal("released", facts[F4].State);
        Assert.True(facts[F4].Held);
        Assert.True(facts[F4].Released);
        Assert.Equal("unused", facts[F4].Acknowledge);

        // The hash is the fact content hash of its file.
        var document = resolved.Source.GetDocument(F1)!;
        Assert.Equal(FactHash.Of(document.Json), facts[F1].Hash);
        Assert.Equal(FactHash.Of(narrowing), facts[F1].Hash);
    }

    [Fact]
    public void A_database_without_facts_has_an_empty_list_that_still_records_its_kind()
    {
        var (model, _, _) = Notes();
        var resolved = ResolutionKit.Resolve(model);
        Assert.Empty(resolved.Db("main").Migrations);
        Assert.Equal(["k:migration"], resolved.Db("main").Migrations.MembershipKeys);
        Assert.Empty(resolved.Migrations);
        var table = resolved.Db("main").Table("notes");
        Assert.Empty(table.FormerNames);
        Assert.Null(table.Since);
    }
}
