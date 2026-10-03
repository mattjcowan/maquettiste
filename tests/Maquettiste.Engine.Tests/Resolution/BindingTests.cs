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
        public Shop(Dialect dialect = Dialect.PostgreSql, Func<Shop, string, EntityBinding, EntityBinding>? edit = null, bool mapInvoiceNote = false)
        {
            Builder = new ModelBuilder(seed: 43);
            var db = Builder.Database("main", dialect);
            Main = db.Id;
            Customer = Builder.Entity("Customer").Key("id", "uuid").Attr("name", "string", a => a.Length(100).Required());
            Notes = Builder.NewId();
            (NoteId, NoteType, NoteEntity, NoteBody, NoteCreated) = (Builder.NewId(), Builder.NewId(), Builder.NewId(), Builder.NewId(), Builder.NewId());
            Builder.Add(new Table
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
            });
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
}
