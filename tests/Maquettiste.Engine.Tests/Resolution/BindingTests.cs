using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Entity bindings (erratum E43): a bound entity is never projected, its source, constants, field map, accounted columns, write table
/// and delete resolve, the tables, views and queries list the bindings that use them, the statements render per dialect, and the
/// rules MQ4044 to MQ4054 report what a binding gets wrong at the JSON pointer of its entry in the entity's file.
/// </summary>
public sealed class BindingTests
{
    /// <summary>
    /// A database <c>main</c> with a designed table <c>notes</c> shared by InvoiceNote and CustomerNote (told apart by
    /// <c>entity_type</c>), a table <c>events</c> with an identity key and a soft-delete flag bound one to one by Event, a query over
    /// the notes read by NoteCount, and Customer projected by convention.
    /// </summary>
    private sealed class Shop
    {
        public Shop(Dialect dialect = Dialect.PostgreSql, Func<Shop, string, EntityBinding, EntityBinding>? edit = null, bool mapInvoiceNote = false,
            Func<Table, Table>? changeNotes = null)
        {
            Builder = new ModelBuilder(seed: 43);
            var db = Builder.Database("main", dialect);
            Main = db.Id;
            Customer = Builder.Entity("Customer").Key("id", "uuid").Attr("name", "string", a => a.Length(100).Required());
            Notes = Builder.NewId();
            (NoteId, NoteType, NoteEntity, NoteBody, NoteCreated) = (Builder.NewId(), Builder.NewId(), Builder.NewId(), Builder.NewId(), Builder.NewId());
            NotesFile = new Table
            {
                Id = Notes, Name = "notes", Database = Main,
                Columns =
                [
                    new Column { Id = NoteId, Name = "id", Type = "uuid", Nullable = false },
                    new Column { Id = NoteType, Name = "entity_type", Type = "string", Length = 32, Nullable = false },
                    new Column { Id = NoteEntity, Name = "entity_id", Type = "uuid", Nullable = false },
                    new Column { Id = NoteBody, Name = "body", Type = "text", Nullable = false },
                    new Column { Id = NoteCreated, Name = "created_at", Type = "datetimeoffset", Nullable = false, DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_TIMESTAMP" } },
                ],
                PrimaryKey = new PrimaryKey { Columns = [NoteId] },
            };
            Builder.Add(changeNotes is null ? NotesFile : changeNotes(NotesFile));
            Events = Builder.NewId();
            (EventId, EventName, EventDeleted) = (Builder.NewId(), Builder.NewId(), Builder.NewId());
            Builder.Add(new Table
            {
                Id = Events, Name = "events", Database = Main,
                Columns =
                [
                    new Column { Id = EventId, Name = "id", Type = "int64", Nullable = false, Generated = ColumnGeneration.Identity },
                    new Column { Id = EventName, Name = "name", Type = "string", Length = 80, Nullable = false },
                    new Column { Id = EventDeleted, Name = "is_deleted", Type = "bool", Nullable = false, Default = Json("false") },
                ],
                PrimaryKey = new PrimaryKey { Columns = [EventId] },
            });
            Query = Builder.NewId();
            Builder.Add(new Query
            {
                Id = Query, Name = "NotesPerType", Database = Main,
                Parameters = [new QueryParameter { Name = "since", Type = "datetimeoffset" }],
                From = new QuerySource { Source = Notes, Alias = "n" },
                Select =
                [
                    new QueryField { Name = "entityType", Expression = new QueryExpression { Column = "n.entity_type" } },
                    new QueryField { Name = "total", Expression = new QueryExpression { Call = "count" } },
                ],
                Where = new QueryPredicate { Op = "ge", Left = new QueryExpression { Column = "n.created_at" }, Right = [new QueryExpression { Param = "since" }] },
                GroupBy = [new QueryExpression { Column = "n.entity_type" }],
                OrderBy = [new QueryOrder { Expression = new QueryExpression { Column = "n.entity_type" } }],
            });

            InvoiceNote = Note("InvoiceNote", "invoiceId", "invoice", edit);
            CustomerNote = Note("CustomerNote", "customerId", "customer", edit);
            Event = Builder.Entity("Event").Key("id", "int64", IdentityStrategy.DatabaseIdentity).Attr("name", "string", a => a.Length(80).Required());
            Event.Bind(Edit(edit, "Event", new EntityBinding
            {
                Id = "", Database = Main, Source = Events,
                Fields = [Field(Event, "id", EventId), Field(Event, "name", EventName)],
                Delete = new BindingDelete { Mode = BindingDeleteMode.Soft, Soft = new SoftDelete { Column = "is_deleted", Value = Json("true") } },
            }));
            NoteCount = Builder.Entity("NoteCount").Key("entityType", "string", configure: a => a.Length(32)).Attr("total", "int64", a => a.Required());
            NoteCount.Bind(Edit(edit, "NoteCount", new EntityBinding
            {
                Id = "", Database = Main, Source = Query,
                Fields = [Field(NoteCount, "entityType", "entityType"), Field(NoteCount, "total", "total")],
            }));
            if (mapInvoiceNote)
                Builder.Mapping(db, InvoiceNote);
        }

        private EntityBuilder Note(string name, string reference, string type, Func<Shop, string, EntityBinding, EntityBinding>? edit)
        {
            var entity = Builder.Entity(name).Key("id", "uuid", IdentityStrategy.UuidV7).Attr(reference, "uuid", a => a.Required()).Attr("body", "text", a => a.Required())
                .Attr("createdAt", "datetimeoffset", a => a.ReadOnly());
            entity.Bind(Edit(edit, name, new EntityBinding
            {
                Id = "", Database = Main, Source = Notes,
                Constants = [new BindingConstant { Column = NoteType, Value = Json("\"" + type + "\"") }],
                Fields = [Field(entity, "id", NoteId), Field(entity, reference, NoteEntity), Field(entity, "body", NoteBody), Field(entity, "createdAt", NoteCreated)],
                Columns = [new BindingColumn { Column = NoteCreated, Status = BindingColumnStatus.Database }],
            }));
            return entity;
        }

        private EntityBinding Edit(Func<Shop, string, EntityBinding, EntityBinding>? edit, string entity, EntityBinding binding) => edit is null ? binding : edit(this, entity, binding);

        public static BindingField Field(EntityBuilder entity, string attribute, string column) => new() { Attribute = entity.AttrId(attribute), Column = column };

        public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        public ModelBuilder Builder { get; }

        public string Main { get; }

        public string Notes { get; }

        /// <summary>The table <c>notes</c> as the shop defines it, before <c>changeNotes</c>.</summary>
        public Table NotesFile { get; }

        public string NoteId { get; }

        public string NoteType { get; }

        public string NoteEntity { get; }

        public string NoteBody { get; }

        public string NoteCreated { get; }

        public string Events { get; }

        public string EventId { get; }

        public string EventName { get; }

        public string EventDeleted { get; }

        public string Query { get; }

        public EntityBuilder Customer { get; }

        public EntityBuilder InvoiceNote { get; }

        public EntityBuilder CustomerNote { get; }

        public EntityBuilder Event { get; }

        public EntityBuilder NoteCount { get; }

        public ResolvedModel Resolve() => ResolutionKit.Resolve(Builder);
    }

    private static REntityBinding BindingOf(ResolvedModel model, string entity) => model.Entity(entity).Bindings["main"];

    [Fact]
    public void A_bound_entity_is_never_projected_and_the_table_lists_every_entity_bound_to_it()
    {
        var model = new Shop().Resolve();
        var db = model.Db("main");

        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(["customers", "events", "notes"], db.Tables.Select(t => t.Name));
        Assert.All(db.Tables.Where(t => t.Name != "customers"), t => Assert.Null(t.Entity));
        var notes = db.Table("notes");
        Assert.Equal(["CustomerNote", "InvoiceNote"], notes.BoundBy.Select(b => b.Entity.Name));
        Assert.Equal(["customer", "invoice"], notes.BoundBy.Select(b => Assert.Single(b.Constants).Value));
        Assert.Equal(["NoteCount"], db.Queries.Single().BoundBy.Select(b => b.Entity.Name));
        Assert.Empty(model.Entity("InvoiceNote").Mappings);
        Assert.True(model.Entity("Customer").Mappings.ContainsKey("main"));

        var binding = BindingOf(model, "InvoiceNote");
        Assert.Equal(("table", "notes", true, "key"), (binding.SourceKind, binding.SourceName, binding.Writes, binding.Delete));
        Assert.Same(notes, binding.WriteTable);
        Assert.Equal(["id", "invoiceId", "body", "createdAt"], binding.Fields.Select(f => f.Name));
        Assert.Equal(["id"], binding.Key.Select(f => f.Name));
        Assert.Equal([true, true, true, false], binding.Fields.Select(f => f.InInsert));
        Assert.Equal([false, true, true, false], binding.Fields.Select(f => f.InUpdate));
        Assert.Equal(["field", "constant", "field", "field", "field"], binding.Columns.Select(c => c.Status));
        Assert.Contains("e:" + model.Entity("InvoiceNote").Id, notes.Dependencies);
        Assert.Contains("e:" + notes.Id, binding.Dependencies);
    }

    [Fact]
    public void A_model_without_bindings_has_none_and_lists_no_bound_objects()
    {
        var model = ResolutionKit.Resolve(BillingFixture.Create());

        Assert.All(model.Entities, e => Assert.Empty(e.Bindings));
        Assert.All(model.Databases.SelectMany(d => d.Tables), t => Assert.Empty(t.BoundBy));
        Assert.All(model.Databases.SelectMany(d => d.Views), v => Assert.Empty(v.BoundBy));
    }

    [Fact]
    public void A_mapping_element_for_a_bound_entity_is_ignored_with_an_info()
    {
        var shop = new Shop(mapInvoiceNote: true);
        var model = shop.Resolve();

        var info = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4054");
        Assert.Equal((DiagnosticSeverity.Info, "/entity"), (info.Severity, info.JsonPointer));
        Assert.DoesNotContain(model.Db("main").Tables, t => t.Entity?.Name == "InvoiceNote");
    }

    public static TheoryData<Dialect, string, string, string, string, string> SharedTableStatements() => new()
    {
        {
            Dialect.PostgreSql,
            "SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM public.notes t\nWHERE t.entity_type = 'invoice' AND t.id = @id",
            "INSERT INTO public.notes (id, entity_id, body, entity_type)\nVALUES (@id, @invoiceId, @body, 'invoice')",
            "UPDATE public.notes SET entity_id = @invoiceId, body = @body\nWHERE id = @id AND entity_type = 'invoice'",
            "DELETE FROM public.notes\nWHERE id = @id AND entity_type = 'invoice'",
            "SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM public.notes t\nWHERE t.entity_type = 'invoice'"
        },
        {
            Dialect.SqlServer,
            "SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM dbo.notes t\nWHERE t.entity_type = N'invoice' AND t.id = @id",
            "INSERT INTO dbo.notes (id, entity_id, body, entity_type)\nVALUES (@id, @invoiceId, @body, N'invoice')",
            "UPDATE dbo.notes SET entity_id = @invoiceId, body = @body\nWHERE id = @id AND entity_type = N'invoice'",
            "DELETE FROM dbo.notes\nWHERE id = @id AND entity_type = N'invoice'",
            "SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM dbo.notes t\nWHERE t.entity_type = N'invoice'"
        },
        {
            Dialect.Sqlite,
            "SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM notes t\nWHERE t.entity_type = 'invoice' AND t.id = @id",
            "INSERT INTO notes (id, entity_id, body, entity_type)\nVALUES (@id, @invoiceId, @body, 'invoice')",
            "UPDATE notes SET entity_id = @invoiceId, body = @body\nWHERE id = @id AND entity_type = 'invoice'",
            "DELETE FROM notes\nWHERE id = @id AND entity_type = 'invoice'",
            "SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM notes t\nWHERE t.entity_type = 'invoice'"
        },
        {
            Dialect.MySql,
            "SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM notes t\nWHERE t.entity_type = 'invoice' AND t.id = @id",
            "INSERT INTO notes (id, entity_id, body, entity_type)\nVALUES (@id, @invoiceId, @body, 'invoice')",
            "UPDATE notes SET entity_id = @invoiceId, body = @body\nWHERE id = @id AND entity_type = 'invoice'",
            "DELETE FROM notes\nWHERE id = @id AND entity_type = 'invoice'",
            "SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM notes t\nWHERE t.entity_type = 'invoice'"
        },
    };

    [Theory]
    [MemberData(nameof(SharedTableStatements))]
    public void A_shared_table_filters_reads_and_sets_inserts_by_its_constant(Dialect dialect, string selectByKey, string insert, string update, string delete, string select)
    {
        var binding = BindingOf(new Shop(dialect).Resolve(), "InvoiceNote");

        Assert.Equal(selectByKey, BindingSql.Render(binding, "select-by-key").Sql);
        Assert.Equal(insert, BindingSql.Render(binding, "insert").Sql);
        Assert.Equal(update, BindingSql.Render(binding, "update").Sql);
        Assert.Equal(delete, BindingSql.Render(binding, "delete").Sql);
        Assert.Equal(select, BindingSql.Render(binding, "select").Sql);
        Assert.Equal(["id", "invoiceId", "body"], BindingSql.Parameters(binding, "insert"));
    }

    public static TheoryData<Dialect, string, string, string> IdentityStatements() => new()
    {
        { Dialect.PostgreSql, "INSERT INTO public.events (name)\nVALUES (@name)\nRETURNING id AS id", "UPDATE public.events SET is_deleted = true\nWHERE id = @id", "(t.is_deleted IS NULL OR t.is_deleted <> true)" },
        { Dialect.SqlServer, "INSERT INTO dbo.events (name)\nOUTPUT INSERTED.id AS id\nVALUES (@name)", "UPDATE dbo.events SET is_deleted = 1\nWHERE id = @id", "(t.is_deleted IS NULL OR t.is_deleted <> 1)" },
        { Dialect.Sqlite, "INSERT INTO events (name)\nVALUES (@name)\nRETURNING id AS id", "UPDATE events SET is_deleted = 1\nWHERE id = @id", "(t.is_deleted IS NULL OR t.is_deleted <> 1)" },
        { Dialect.MySql, "INSERT INTO events (name)\nVALUES (@name);\nSELECT LAST_INSERT_ID() AS id", "UPDATE events SET is_deleted = 1\nWHERE id = @id", "(t.is_deleted IS NULL OR t.is_deleted <> 1)" },
    };

    [Theory]
    [MemberData(nameof(IdentityStatements))]
    public void An_identity_key_comes_back_from_the_insert_and_a_soft_delete_is_an_update_reads_leave_out(Dialect dialect, string insert, string delete, string notDeleted)
    {
        var binding = BindingOf(new Shop(dialect).Resolve(), "Event");

        Assert.Equal(["id"], binding.Generated.Select(f => f.Name));
        Assert.Equal(("soft", "is_deleted"), (binding.Delete, binding.SoftDeleteColumn?.Name));
        Assert.Equal(insert, BindingSql.Render(binding, "insert").Sql);
        Assert.Equal(delete, BindingSql.Render(binding, "delete").Sql);
        Assert.EndsWith("WHERE " + notDeleted + " AND t.id = @id", BindingSql.Render(binding, "select-by-key").Sql, StringComparison.Ordinal);
        Assert.Equal("is_deleted", Assert.Single(binding.Columns, c => c.Status == "soft-delete").Name);
    }

    [Fact]
    public void A_query_source_is_a_derived_table_without_its_order_and_the_binding_is_read_only()
    {
        var binding = BindingOf(new Shop().Resolve(), "NoteCount");

        Assert.Equal(("query", false, "none"), (binding.SourceKind, binding.Writes, binding.Delete));
        Assert.Equal(
            "SELECT q.entityType AS entityType, q.total AS total\nFROM (\nSELECT n.entity_type AS entityType, COUNT(*) AS total\nFROM public.notes n\nWHERE n.created_at >= @since\nGROUP BY n.entity_type\n) q\nWHERE q.entityType = @entityType",
            BindingSql.Render(binding, "select-by-key").Sql);
        Assert.Equal("", BindingSql.Render(binding, "insert").Sql);
        Assert.Equal("", BindingSql.Render(binding, "update").Sql);
        Assert.Equal("", BindingSql.Render(binding, "delete").Sql);
        // $ placeholders number the binding's parameters after the query's.
        var numbered = BindingSql.Render(binding, "select-by-key", null, new QuerySqlOptions { Placeholder = "$" });
        Assert.EndsWith("n.created_at >= $1\nGROUP BY n.entity_type\n) q\nWHERE q.entityType = $2", numbered.Sql, StringComparison.Ordinal);
        Assert.Equal(["since", "entityType"], numbered.Parameters);
        Assert.Throws<ArgumentException>(() => BindingSql.Render(binding, "upsert"));
    }

    public static TheoryData<string, string, string> Rules() => new()
    {
        { "source", "MQ4044", "/bindings/0/source" },
        { "write", "MQ4044", "/bindings/0/write/table" },
        { "attribute", "MQ4045", "/bindings/0/fields/1/attribute" },
        { "column", "MQ4045", "/bindings/0/fields/2/column" },
        { "listed", "MQ4045", "/bindings/0/columns/0/column" },
        { "key", "MQ4046", "/bindings/0/fields" },
        { "unaccounted", "MQ4047", "/bindings/0" },
        { "constant", "MQ4048", "/bindings/0/constants/0/column" },
        { "write-constant", "MQ4049", "/bindings/0/constants/0/column" },
        { "twice", "MQ4050", "/bindings/1/database" },
        { "fit", "MQ4051", "/bindings/0/constants/0/value" },
        { "write-key", "MQ4052", "/bindings/0/write/table" },
        { "soft", "MQ4053", "/bindings/0/delete" },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Each_binding_rule_points_into_the_entity_file(string mistake, string rule, string pointer)
    {
        static EntityBinding Break(Shop shop, string mistake, string entity, EntityBinding b)
        {
            if (entity != (mistake is "write-key" or "soft" ? "NoteCount" : "InvoiceNote"))
                return b;
            return mistake switch
            {
                "source" => b with { Source = shop.Builder.NewId() },
                "write" => b with { Write = new BindingWrite { Table = shop.Query } },
                "attribute" => b with { Fields = [b.Fields[0], b.Fields[1] with { Attribute = "nope" }, .. b.Fields.Skip(2)] },
                "column" => b with { Fields = [b.Fields[0], b.Fields[1], b.Fields[2] with { Column = "nope" }, b.Fields[3]] },
                "listed" => b with { Columns = [new BindingColumn { Column = "nope", Status = BindingColumnStatus.Ignored }] },
                "key" => b with { Fields = [.. b.Fields.Skip(1)] },
                "unaccounted" => b with { Fields = [b.Fields[0], b.Fields[1], b.Fields[3]] },
                "constant" => b with { Constants = [new BindingConstant { Column = "nope", Value = Shop.Json("1") }] },
                "write-constant" => b with { Write = new BindingWrite { Table = shop.Events } },
                "twice" => b,
                "fit" => b with { Constants = [b.Constants[0] with { Value = Shop.Json("42") }] },
                "write-key" => b with { Write = new BindingWrite { Table = shop.Events } },
                "soft" => b with { Delete = new BindingDelete { Mode = BindingDeleteMode.Soft, Soft = new SoftDelete { Column = "is_deleted" } } },
                _ => b,
            };
        }

        var shop = new Shop(edit: (s, entity, b) => Break(s, mistake, entity, b));
        if (mistake == "twice")
        {
            shop.InvoiceNote.Bind(new EntityBinding { Id = "", Database = shop.Main, Source = shop.Notes, Write = new BindingWrite { None = true } });
        }

        var model = shop.Resolve();
        var found = Assert.Single(model.Diagnostics, d => d.Rule == rule && d.JsonPointer == pointer);
        Assert.Equal(RuleCatalog.Get(rule).DefaultSeverity, found.Severity);
        Assert.Equal(mistake is "write-key" or "soft" ? shop.NoteCount.Id : shop.InvoiceNote.Id, found.ElementId);
    }

    [Fact]
    public void MQ4058_names_the_stored_attributes_no_field_maps_once_per_binding()
    {
        var shop = new Shop();
        Assert.DoesNotContain(shop.Resolve().Diagnostics, d => d.Rule == "MQ4058");

        var money = shop.Builder.ValueObject("Money").Attr("amount", "decimal").Attr("currency", "string", a => a.Length(3));
        shop.Builder.Stereotype("archived").AppliesTo("entity").Attr("archivedAt", "datetimeoffset");
        shop.InvoiceNote.Stereotype("archived")
            .Attr("pinned", "bool")
            .Attr("total", money)
            .Attr("rank", "int32", a => a.Derived("now - born", stored: true))
            .Attr("summary", "string", a => a.Derived("first + last"))
            .Attr("labels", "string", a => a.Collection());
        // A second binding whose source is nothing of its database.
        shop.InvoiceNote.Bind(new EntityBinding
        {
            Id = "", Database = shop.Builder.Database("archive", Dialect.PostgreSql).Id, Source = "missing",
        });
        var model = shop.Resolve();

        var found = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4058");
        Assert.Equal((DiagnosticSeverity.Warning, "/bindings/0/fields", shop.InvoiceNote.Id), (found.Severity, found.JsonPointer, found.ElementId));
        // Not the virtual derivation, nor the collection; the unresolved source of the second binding is MQ4044's alone.
        Assert.Equal("No field maps pinned, totalAmount, totalCurrency, rank, archivedAt: the entity reads them from nowhere.", found.Message);
        Assert.Contains(model.Diagnostics, d => d.Rule == "MQ4044" && d.JsonPointer == "/bindings/1/source");
    }

    [Fact]
    public void MQ4058_reports_a_read_only_binding_and_each_value_object_member_no_field_maps()
    {
        // The read-only query binding of NoteCount leaves total unmapped: it reads it as much as a binding that writes.
        var shop = new Shop(edit: (_, entity, b) => entity == "NoteCount" ? b with { Fields = [b.Fields[0]] } : b);
        var readOnly = Assert.Single(shop.Resolve().Diagnostics, d => d.Rule == "MQ4058");
        Assert.Equal((shop.NoteCount.Id, "/bindings/0/fields", "No field maps total: the entity reads it from nowhere."),
            (readOnly.ElementId, readOnly.JsonPointer, readOnly.Message));

        // A member its field maps is mapped; the other member is not.
        var b = new ModelBuilder(seed: 58);
        var db = b.Database("main", Dialect.PostgreSql);
        var (rentals, id, amount) = (b.NewId(), b.NewId(), b.NewId());
        b.Add(new Table
        {
            Id = rentals, Name = "rentals", Database = db.Id,
            Columns = [new Column { Id = id, Name = "id", Type = "uuid", Nullable = false }, new Column { Id = amount, Name = "amount", Type = "decimal", Nullable = false }],
            PrimaryKey = new PrimaryKey { Columns = [id] },
        });
        var money = b.ValueObject("Money").Attr("amount", "decimal").Attr("currency", "string", a => a.Length(3));
        var rental = b.Entity("Rental").Key("id", "uuid").Attr("cost", money);
        rental.Bind(new EntityBinding
        {
            Id = "", Database = db.Id, Source = rentals,
            Fields = [Shop.Field(rental, "id", id), new BindingField { Attribute = rental.AttrId("cost") + "." + money.AttrId("amount"), Column = amount }],
        });
        var partly = Assert.Single(ResolutionKit.Resolve(b).Diagnostics, d => d.Rule == "MQ4058");
        Assert.Equal("No field maps costCurrency: the entity reads it from nowhere.", partly.Message);
    }

    [Fact]
    public void A_relation_between_bound_entities_uses_the_foreign_key_its_mapping_names()
    {
        var b = new ModelBuilder(seed: 44);
        var db = b.Database("main", Dialect.PostgreSql);
        var orders = b.Table("orders", db).Column("id", "uuid", nullable: false).PrimaryKey("id");
        var lines = b.Table("order_lines", db).Column("id", "uuid", nullable: false).Column("order_id", "uuid", nullable: false).PrimaryKey("id").ForeignKey(orders, "order_id");
        var order = b.Entity("Order").Key("id", "uuid");
        var line = b.Entity("OrderLine").Key("id", "uuid").Attr("orderId", "uuid", a => a.Required());
        order.Bind(new EntityBinding { Id = "", Database = db.Id, Source = orders.Id, Fields = [Shop.Field(order, "id", orders.ColumnId("id"))] });
        line.Bind(new EntityBinding
        {
            Id = "", Database = db.Id, Source = lines.Id,
            Fields = [Shop.Field(line, "id", lines.ColumnId("id")), Shop.Field(line, "orderId", lines.ColumnId("order_id"))],
        });
        var relation = b.Relation("contains", order, line, fromMax: MaxCardinality.One, fromMin: 1, fromRole: "order", toRole: "lines", fromNavigation: "order");
        var withoutKey = ResolutionKit.Resolve(b);
        Assert.Contains(withoutKey.Diagnostics, d => d.Rule == "MQ4011" && d.ElementId == relation.Id);

        var fk = ((Table)b.BuildElements().First(e => e.Id == lines.Id)).ForeignKeys[0].Id;
        b.Mapping(db, relation).ForeignKey(fk);
        var model = ResolutionKit.Resolve(b);

        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var mapping = model.Relation("contains").Mappings["main"];
        Assert.Equal(("foreign-key", "order_lines"), (mapping.Shape, mapping.ForeignKey?.Columns[0].Table.Name));
        Assert.Equal(["order_id"], mapping.ForeignKey!.Columns.Select(c => c.Name));
        Assert.Empty(model.Entity("OrderLine").Mappings);
    }

    // ---- sensitivity said once, shown on both sides (next-phase-2-design.md section 3.2, D23) ----

    /// <summary>
    /// A table <c>notes</c> (id, entity_type, body) shared by InvoiceNote and CustomerNote, each with a body attribute bound to the
    /// body column; Customer projected with an email attribute, and its overlay entry for the email column. Each value is the
    /// sensitivity that side states, or <see langword="null"/>.
    /// </summary>
    private static ResolvedModel SensitiveNotes(Sensitivity? body, Sensitivity? invoiceBody, Sensitivity? customerBody, Sensitivity? email = null,
        Sensitivity? emailEntry = null)
    {
        var b = new ModelBuilder(seed: 45);
        var db = b.Database("main", Dialect.PostgreSql);
        var notes = b.Table("notes", db).Column("id", "uuid", nullable: false).Column("entity_type", "string", nullable: false, length: 32).Column("body", "text")
            .PrimaryKey("id").Edit(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "body" ? c with { Sensitive = body } : c)] });
        foreach (var (name, type, sensitivity) in new[] { ("InvoiceNote", "invoice", invoiceBody), ("CustomerNote", "customer", customerBody) })
        {
            var note = b.Entity(name).Key("id", "uuid").Attr("body", "text", a => { if (sensitivity is { } s) a.Sensitive(s); });
            note.Bind(new EntityBinding
            {
                Id = "", Database = db.Id, Source = notes.Id,
                Constants = [new BindingConstant { Column = notes.ColumnId("entity_type"), Value = Shop.Json("\"" + type + "\"") }],
                Fields = [Shop.Field(note, "id", notes.ColumnId("id")), Shop.Field(note, "body", notes.ColumnId("body"))],
            });
        }

        var customer = b.Entity("Customer").Key("id", "uuid").Attr("email", "string", a => { a.Length(320); if (email is { } s) a.Sensitive(s); });
        if (emailEntry is { } entry)
            b.Table("customers", db).OverlayFor(customer).Overlay(customer.AttrId("email")).Edit(t => t with { Columns = [t.Columns[0] with { Sensitive = entry }] });
        return ResolutionKit.Resolve(b);
    }

    [Fact]
    public void A_designed_column_shows_the_strongest_sensitivity_of_its_bound_attributes()
    {
        Assert.Equal("secret", SensitiveNotes(null, Sensitivity.Pii, Sensitivity.Secret).Db("main").Table("notes").Column("body").Sensitive);
        Assert.Equal("pii", SensitiveNotes(null, Sensitivity.Pii, null).Db("main").Table("notes").Column("body").Sensitive);
        var model = SensitiveNotes(null, null, null);
        Assert.Null(model.Db("main").Table("notes").Column("body").Sensitive);
        Assert.Null(model.Db("main").Table("notes").Column("id").Sensitive);
    }

    [Fact]
    public void A_bound_attribute_shows_its_column_sensitivity()
    {
        var model = SensitiveNotes(Sensitivity.Pii, null, null);

        Assert.Equal("pii", model.Db("main").Table("notes").Column("body").Sensitive);
        Assert.Equal("pii", model.Entity("InvoiceNote").Attributes.Single(a => a.Name == "body").Sensitive);
        Assert.Equal("pii", model.Entity("CustomerNote").Attributes.Single(a => a.Name == "body").Sensitive);
        Assert.Null(model.Entity("InvoiceNote").Attributes.Single(a => a.Name == "id").Sensitive);
    }

    [Fact]
    public void A_projected_column_shows_its_attribute_sensitivity()
    {
        var model = SensitiveNotes(null, null, null, email: Sensitivity.Pii);
        Assert.Equal("pii", model.Db("main").Table("customers").Column("email").Sensitive);
        Assert.Null(model.Db("main").Table("customers").Column("id").Sensitive);

        // The overlay entry's own value is the column's statement.
        var overlaid = SensitiveNotes(null, null, null, email: Sensitivity.Pii, emailEntry: Sensitivity.Secret);
        Assert.Equal("secret", overlaid.Db("main").Table("customers").Column("email").Sensitive);
        Assert.Equal("pii", overlaid.Entity("Customer").Attributes.Single(a => a.Name == "email").Sensitive);
    }

    [Fact]
    public void Own_sensitivity_wins_on_both_sides()
    {
        // Each side states its own: neither shows the other's (MQ4085 warns).
        var both = SensitiveNotes(Sensitivity.Pii, Sensitivity.Secret, null);
        Assert.Equal("pii", both.Db("main").Table("notes").Column("body").Sensitive);
        Assert.Equal("secret", both.Entity("InvoiceNote").Attributes.Single(a => a.Name == "body").Sensitive);
        Assert.Equal("pii", both.Entity("CustomerNote").Attributes.Single(a => a.Name == "body").Sensitive);

        // A side reads only the other's own value: the column borrows InvoiceNote's, and CustomerNote does not borrow it back.
        var borrowed = SensitiveNotes(null, Sensitivity.Secret, null);
        Assert.Equal("secret", borrowed.Db("main").Table("notes").Column("body").Sensitive);
        Assert.Null(borrowed.Entity("CustomerNote").Attributes.Single(a => a.Name == "body").Sensitive);
    }

    // ---- the description fallback (next-phase-2-design.md section 3.3, decision 12, D34) ----

    /// <summary>
    /// Note (id, body, title) bound to a table <c>notes</c> in <c>main</c> whose <c>body</c> column carries <paramref name="mainComment"/>
    /// and whose <c>title</c> column carries <paramref name="titleDescription"/> as its own description; with
    /// <paramref name="archived"/>, Note is also bound to <c>notes</c> in <c>archive</c>, a database created after <c>main</c> whose
    /// <c>body</c> column carries <paramref name="archiveComment"/>. The body attribute states <paramref name="own"/> as its description,
    /// when given.
    /// </summary>
    private static ResolvedModel CommentedNotes(string? mainComment, string? own = null, bool archived = false, string? archiveComment = null,
        string? titleDescription = null)
    {
        var b = new ModelBuilder(seed: 46);
        var note = b.Entity("Note").Key("id", "uuid").Attr("body", "text", a => { if (own is not null) a.Description(own); }).Attr("title", "string");
        Bind(b.Database("main", Dialect.PostgreSql), mainComment);
        if (archived)
            Bind(b.Database("archive", Dialect.PostgreSql), archiveComment);
        return ResolutionKit.Resolve(b);

        void Bind(DatabaseBuilder db, string? comment)
        {
            var notes = b.Table("notes", db).Column("id", "uuid", nullable: false).Column("body", "text").Column("title", "string").PrimaryKey("id")
                .Edit(t => t with
                {
                    Columns =
                    [
                        .. t.Columns.Select(c => c.Name switch
                        {
                            "body" => c with { Comment = comment },
                            "title" when titleDescription is not null => c with { Description = new Description { Text = titleDescription } },
                            _ => c,
                        }),
                    ],
                });
            note.Bind(new EntityBinding
            {
                Id = "", Database = db.Id, Source = notes.Id,
                Fields =
                [
                    Shop.Field(note, "id", notes.ColumnId("id")),
                    Shop.Field(note, "body", notes.ColumnId("body")),
                    Shop.Field(note, "title", notes.ColumnId("title")),
                ],
            });
        }
    }

    [Fact]
    public void An_attribute_without_a_description_shows_its_column_comment()
    {
        var model = CommentedNotes("Text of the note, as typed.");
        var note = model.Entity("Note");
        var column = model.Db("main").Table("notes").Column("body");

        var body = note.Attributes.Single(a => a.Name == "body");
        Assert.Equal("Text of the note, as typed.", body.Description);
        Assert.Same(column, body.DescriptionColumn);
        Assert.Equal("Text of the note, as typed.", column.Comment);
        Assert.Null(column.Description); // the column keeps its own text; nothing is copied back

        // An attribute bound to a column with no comment has no description.
        var id = note.Attributes.Single(a => a.Name == "id");
        Assert.Null(id.Description);
        Assert.Null(id.DescriptionColumn);

        // The comment is the text the database gets: a designed column's own description fills it by convention.
        var described = CommentedNotes(null, titleDescription: "The heading.").Entity("Note").Attributes.Single(a => a.Name == "title");
        Assert.Equal("The heading.", described.Description);
        Assert.Equal("title", described.DescriptionColumn!.Name);
    }

    [Fact]
    public void An_own_description_wins()
    {
        var model = CommentedNotes("Text of the note, as typed.", own: "What the author wrote.");
        var body = model.Entity("Note").Attributes.Single(a => a.Name == "body");
        Assert.Equal("What the author wrote.", body.Description);
        Assert.Null(body.DescriptionColumn);
        Assert.Equal("Text of the note, as typed.", model.Db("main").Table("notes").Column("body").Comment);
    }

    [Fact]
    public void The_first_database_by_name_gives_the_comment()
    {
        // archive sorts before main, although it was created after it.
        var model = CommentedNotes("From main.", archived: true, archiveComment: "From archive.");
        var body = model.Entity("Note").Attributes.Single(a => a.Name == "body");
        Assert.Equal("From archive.", body.Description);
        Assert.Same(model.Db("archive").Table("notes").Column("body"), body.DescriptionColumn);

        // A binding whose column has no comment is passed over for the next one that has.
        var later = CommentedNotes("From main.", archived: true).Entity("Note").Attributes.Single(a => a.Name == "body");
        Assert.Equal("From main.", later.Description);
        Assert.Equal("main", later.DescriptionColumn!.Table.Database.Name);
    }

    [Fact]
    public void A_value_object_member_takes_no_fallback()
    {
        var b = new ModelBuilder(seed: 47);
        var db = b.Database("main", Dialect.PostgreSql);
        var money = b.ValueObject("Money").Attr("amount", "decimal").Attr("currency", "string");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("total", money);
        var invoices = b.Table("invoices", db).Column("id", "uuid", nullable: false).Column("total_amount", "decimal").Column("total_currency", "string")
            .PrimaryKey("id")
            .Edit(t => t with { Columns = [.. t.Columns.Select(c => c with { Comment = "Column " + c.Name + "." })] });
        invoice.Bind(new EntityBinding
        {
            Id = "", Database = db.Id, Source = invoices.Id,
            Fields =
            [
                Shop.Field(invoice, "id", invoices.ColumnId("id")),
                new BindingField { Attribute = invoice.AttrId("total") + "." + money.AttrId("amount"), Column = invoices.ColumnId("total_amount") },
                new BindingField { Attribute = invoice.AttrId("total") + "." + money.AttrId("currency"), Column = invoices.ColumnId("total_currency") },
            ],
        });
        var model = ResolutionKit.Resolve(b);

        // The member is shared by every entity that embeds the value object, and the embedding attribute maps no column of its own.
        var total = model.Entity("Invoice").Attributes.Single(a => a.Name == "total");
        Assert.Null(total.Description);
        Assert.Null(total.DescriptionColumn);
        Assert.All(model.ValueObjects.Single().Attributes, m => Assert.Null(m.Description));
        Assert.Equal("Column id.", model.Entity("Invoice").Attributes.Single(a => a.Name == "id").Description);
    }

    // ---- drift against recorded provenance (next-phase-2-design.md section 3.3, decisions 5 and 6, D31) ----

    /// <summary>The provenance a by-name operation records for the shop's <c>notes</c> table as it defines it.</summary>
    private static BindingProvenance Recorded(Shop shop) =>
        new() { Columns = shop.NotesFile.Columns.ToDictionary(c => c.Id, ColumnShapeHash.Of, StringComparer.Ordinal) };

    /// <summary>
    /// The shop with provenance on InvoiceNote's binding, recorded from <c>notes</c> as defined, and <paramref name="changeNotes"/> applied
    /// to the table after; CustomerNote, bound to the same table, records none.
    /// </summary>
    private static Shop Drifted(Func<Table, Table>? changeNotes, Func<Shop, BindingProvenance>? provenance = null, string? severity = null)
    {
        var shop = new Shop(
            edit: (s, entity, b) => entity == "InvoiceNote" ? b with { Provenance = provenance?.Invoke(s) ?? Recorded(s) } : b,
            changeNotes: changeNotes);
        if (severity is not null)
            shop.Builder.Settings(s => s with { Validation = new ValidationSettings { Rules = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("MQ4087", severity) } });
        return shop;
    }

    /// <summary>Renames <c>body</c> to <c>note_body</c> and makes <c>created_at</c> nullable.</summary>
    private static Table RenameBodyAndRetypeCreated(Table t) =>
        t with
        {
            Columns =
            [
                .. t.Columns.Select(c => c.Name switch
                {
                    "body" => c with { Name = "note_body" },
                    "created_at" => c with { Nullable = true },
                    _ => c,
                }),
            ],
        };

    [Fact]
    public void A_renamed_column_is_MQ4087_at_the_binding()
    {
        var shop = Drifted(RenameBodyAndRetypeCreated);
        var model = shop.Resolve();

        var found = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4087");
        Assert.Equal((DiagnosticSeverity.Warning, shop.InvoiceNote.Id, "/bindings/0"), (found.Severity, found.ElementId, found.JsonPointer));
        Assert.Equal("The binding of entity 'InvoiceNote' to database 'main' maps columns that changed since its provenance was recorded: "
            + "'note_body' renamed (attribute 'body'), 'created_at' retyped (attribute 'createdAt'). Re-sync the binding (resync-binding).", found.Message);
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule is "MQ4088" or "MQ4047");

        // The binding still resolves by column id: nothing else breaks, and a table unchanged since it was recorded is not drift.
        Assert.Equal("note_body", BindingOf(model, "InvoiceNote").Fields.Single(f => f.Name == "body").ColumnName);
        Assert.DoesNotContain(Drifted(null).Resolve().Diagnostics, d => d.Rule is "MQ4087" or "MQ4088");

        // A change of case or separators is not a rename.
        var cased = Drifted(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "entity_id" ? c with { Name = "EntityID" } : c)] }).Resolve();
        Assert.DoesNotContain(cased.Diagnostics, d => d.Rule == "MQ4087");
    }

    [Fact]
    public void MQ4087_takes_the_project_severity()
    {
        var model = Drifted(RenameBodyAndRetypeCreated, severity: "error").Resolve();
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(model.Diagnostics, d => d.Rule == "MQ4087").Severity);
    }

    [Fact]
    public void MQ4087_off_reports_nothing()
    {
        var model = Drifted(RenameBodyAndRetypeCreated, severity: "off").Resolve();
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule is "MQ4087" or "MQ4088");
    }

    [Fact]
    public void Stale_provenance_is_MQ4088()
    {
        // Every recorded id is a column of another table: the provenance describes another source and is read as none.
        var shop = Drifted(RenameBodyAndRetypeCreated, provenance: s => new BindingProvenance
        {
            Columns = new Dictionary<string, string>(StringComparer.Ordinal) { [s.EventId] = "00000000:00000000", [s.EventName] = "00000000:00000000" },
        });
        var model = shop.Resolve();
        var stale = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4088");
        Assert.Equal((DiagnosticSeverity.Warning, shop.InvoiceNote.Id, "/bindings/0/provenance"), (stale.Severity, stale.ElementId, stale.JsonPointer));
        Assert.Contains("records provenance for 2 columns, none of which is a column of table 'notes'", stale.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule == "MQ4087");

        // A source that is not a table file (here a query) has no columns with ids.
        var query = new Shop(edit: (s, entity, b) => entity == "NoteCount" ? b with { Provenance = Recorded(s) } : b).Resolve();
        var notTable = Assert.Single(query.Diagnostics, d => d.Rule == "MQ4088");
        Assert.Equal("/bindings/0/provenance", notTable.JsonPointer);
        Assert.Contains("query 'NotesPerType' is not a designed or imported table file", notTable.Message, StringComparison.Ordinal);

        // An empty provenance describes a source that had no columns: not stale.
        var empty = Drifted(null, provenance: _ => new BindingProvenance { Columns = new Dictionary<string, string>(StringComparer.Ordinal) }).Resolve();
        Assert.DoesNotContain(empty.Diagnostics, d => d.Rule is "MQ4087" or "MQ4088");
    }

    [Fact]
    public void An_added_column_is_MQ4047_not_MQ4087()
    {
        var shop = Drifted(t => t with
        {
            Columns =
            [
                .. t.Columns,
                new Column { Id = "01K6BNDADDED00000000000001", Name = "subject", Type = "string", Length = 80 },
                new Column { Id = "01K6BNDADDED00000000000002", Name = "pinned", Type = "bool", Nullable = false, Default = Shop.Json("false") },
            ],
        });
        var model = shop.Resolve();

        // Both bindings miss the new column nothing fills (MQ4047, with or without provenance); the defaulted one is accounted for.
        var unaccounted = model.Diagnostics.Where(d => d.Rule == "MQ4047").ToList();
        Assert.Equal(new[] { shop.CustomerNote.Id, shop.InvoiceNote.Id }.Order(StringComparer.Ordinal), unaccounted.Select(d => d.ElementId!).Order(StringComparer.Ordinal));
        Assert.All(unaccounted, d => Assert.Contains("'subject'", d.Message, StringComparison.Ordinal));
        Assert.All(unaccounted, d => Assert.DoesNotContain("'pinned'", d.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule is "MQ4087" or "MQ4088");
    }
}
