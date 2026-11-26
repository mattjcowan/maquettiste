using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Subtypes in one table: a base entity's binding names the type column once (<c>discriminatorColumn</c>), and each derived entity's
/// binding to the same database uses its type code on that column: its reads, updates and deletes keep its own rows and those of the
/// entities derived from it, and its insert writes its code. The base reads every row and its insert writes its own code. MQ4122 to
/// MQ4125 report what goes wrong at the JSON pointer of the binding in the entity's file.
/// </summary>
public sealed class BindingSubtypeTests
{
    /// <summary>
    /// A table <c>notes (id, kind, entity_id, body, created_at)</c> shared by <c>Note</c> (the base, code <c>note</c>), <c>InvoiceNote</c>
    /// (<c>invoice</c>), <c>CustomerNote</c> (<c>customer</c>) and <c>UrgentNote</c>, derived from InvoiceNote (no code of its own: its
    /// name), and a table <c>archived_notes</c> without the type column.
    /// </summary>
    private sealed class Notes
    {
        public Notes(Func<Notes, string, EntityBinding, EntityBinding>? edit = null, bool baseShowsKind = false)
        {
            B = new ModelBuilder(seed: 47);
            var db = B.Database("main", Dialect.PostgreSql);
            Main = db.Id;
            Table = B.NewId();
            (Id, Kind, EntityId, Body, Created) = (B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId());
            B.Add(new Table
            {
                Id = Table, Name = "notes", Database = Main,
                Columns =
                [
                    new Column { Id = Id, Name = "id", Type = "uuid", Nullable = false },
                    new Column { Id = Kind, Name = "kind", Type = "string", Length = 32, Nullable = false },
                    new Column { Id = EntityId, Name = "entity_id", Type = "uuid", Nullable = true },
                    new Column { Id = Body, Name = "body", Type = "text", Nullable = false },
                    new Column { Id = Created, Name = "created_at", Type = "datetimeoffset", Nullable = false, DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_TIMESTAMP" } },
                ],
                PrimaryKey = new PrimaryKey { Columns = [Id] },
            });
            Archive = B.NewId();
            var archiveId = B.NewId();
            B.Add(new Table
            {
                Id = Archive, Name = "archived_notes", Database = Main,
                Columns =
                [
                    new Column { Id = archiveId, Name = "id", Type = "uuid", Nullable = false },
                    new Column { Id = B.NewId(), Name = "entity_id", Type = "uuid", Nullable = true },
                    new Column { Id = B.NewId(), Name = "body", Type = "text", Nullable = false },
                    new Column { Id = B.NewId(), Name = "created_at", Type = "datetimeoffset", Nullable = false, DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_TIMESTAMP" } },
                ],
                PrimaryKey = new PrimaryKey { Columns = [archiveId] },
            });
            Filed = B.NewId();
            var filedId = B.NewId();
            B.Add(new Table
            {
                Id = Filed, Name = "filed_notes", Database = Main,
                Columns =
                [
                    new Column { Id = filedId, Name = "id", Type = "uuid", Nullable = false },
                    new Column { Id = B.NewId(), Name = "kind", Type = "string", Length = 32, Nullable = false },
                    new Column { Id = B.NewId(), Name = "entity_id", Type = "uuid", Nullable = true },
                    new Column { Id = B.NewId(), Name = "body", Type = "text", Nullable = false },
                    new Column { Id = B.NewId(), Name = "created_at", Type = "datetimeoffset", Nullable = false, DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_TIMESTAMP" } },
                ],
                PrimaryKey = new PrimaryKey { Columns = [filedId] },
            });

            Note = B.Entity("Note").Key("id", "uuid", IdentityStrategy.UuidV7).Attr("body", "text", a => a.Required()).Attr("createdAt", "datetimeoffset", a => a.ReadOnly());
            if (baseShowsKind)
                Note.Attr("kind", "string", a => a.Length(32));
            Note.Bind(Edit(edit, "Note", new EntityBinding
            {
                Id = "", Database = Main, Source = Table, DiscriminatorColumn = Kind, DiscriminatorValue = Json("\"note\""),
                Fields = [Field(Note, "id", Id), Field(Note, "body", Body), Field(Note, "createdAt", Created), .. baseShowsKind ? new[] { Field(Note, "kind", Kind) } : []],
                Columns = [new BindingColumn { Column = Created, Status = BindingColumnStatus.Database }, new BindingColumn { Column = EntityId, Status = BindingColumnStatus.Ignored }],
            }));
            InvoiceNote = Derived("InvoiceNote", Note, "invoiceId", null, "\"invoice\"", edit);
            CustomerNote = Derived("CustomerNote", Note, "customerId", null, "\"customer\"", edit);
            UrgentNote = Derived("UrgentNote", InvoiceNote, "invoiceId", InvoiceNote, null, edit);
        }

        /// <summary>A derived note bound to <c>notes</c>; <paramref name="declaredBy"/> declares its reference attribute when it inherits it.</summary>
        private EntityBuilder Derived(string name, EntityBuilder baseEntity, string reference, EntityBuilder? declaredBy, string? code,
            Func<Notes, string, EntityBinding, EntityBinding>? edit)
        {
            var entity = B.Entity(name).Base(baseEntity);
            if (declaredBy is null)
                entity.Attr(reference, "uuid", a => a.Required());
            entity.Bind(Edit(edit, name, new EntityBinding
            {
                Id = "", Database = Main, Source = Table, DiscriminatorValue = code is null ? null : Json(code),
                Fields = [Field(Note, "id", Id), Field(declaredBy ?? entity, reference, EntityId), Field(Note, "body", Body), Field(Note, "createdAt", Created)],
                Columns = [new BindingColumn { Column = Created, Status = BindingColumnStatus.Database }],
            }));
            return entity;
        }

        private EntityBinding Edit(Func<Notes, string, EntityBinding, EntityBinding>? edit, string entity, EntityBinding binding) =>
            edit is null ? binding : edit(this, entity, binding);

        private static BindingField Field(EntityBuilder declaring, string attribute, string column) => new() { Attribute = declaring.AttrId(attribute), Column = column };

        public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        /// <summary>A derived binding moved to <c>filed_notes</c>, its columns named by their names there.</summary>
        public EntityBinding ToFiled(EntityBinding b)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal) { [Id] = "id", [Kind] = "kind", [EntityId] = "entity_id", [Body] = "body", [Created] = "created_at" };
            return b with
            {
                Source = Filed,
                Fields = [.. b.Fields.Select(f => f with { Column = names[f.Column] })],
                Columns = [.. b.Columns.Select(c => c with { Column = names[c.Column] })],
            };
        }

        public ModelBuilder B { get; }

        public string Main { get; }

        public string Table { get; }

        public string Archive { get; }

        public string Filed { get; }

        public string Id { get; }

        public string Kind { get; }

        public string EntityId { get; }

        public string Body { get; }

        public string Created { get; }

        public EntityBuilder Note { get; }

        public EntityBuilder InvoiceNote { get; }

        public EntityBuilder CustomerNote { get; }

        public EntityBuilder UrgentNote { get; }

        public ResolvedModel Resolve() => ResolutionKit.Resolve(B);
    }

    private static REntityBinding BindingOf(ResolvedModel model, string entity) => model.Entity(entity).Bindings["main"];

    /// <summary>The codes the base keeps: its own, then its hierarchy's in the shared table, in entity order.</summary>
    private static readonly object[] BaseCodes = ["note", "customer", "invoice", "UrgentNote"];

    private const string BaseIn = "('note', 'customer', 'invoice', 'UrgentNote')";

    private static (string Select, string ByKey, string Insert, string Update, string Delete) Statements(REntityBinding binding) =>
        (BindingSql.Render(binding, "select").Sql, BindingSql.Render(binding, "select-by-key").Sql, BindingSql.Render(binding, "insert").Sql,
            BindingSql.Render(binding, "update").Sql, BindingSql.Render(binding, "delete").Sql);

    [Fact]
    public void The_base_reads_every_row_of_its_hierarchy_and_writes_its_own_code()
    {
        var model = new Notes().Resolve();

        Assert.DoesNotContain(model.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
        var binding = BindingOf(model, "Note");
        var typeColumn = Assert.IsType<RBindingDiscriminator>(binding.DiscriminatorColumn);
        Assert.Equal(("kind", "Note", "note"), (typeColumn.ColumnName, typeColumn.Base.Name, typeColumn.Value));
        Assert.Equal(BaseCodes, typeColumn.Codes);
        Assert.Same(typeColumn.SourceColumn, typeColumn.WriteColumn);
        Assert.Equal("discriminator", Assert.Single(binding.Columns, c => c.Name == "kind").Status);

        var (select, byKey, insert, update, delete) = Statements(binding);
        // Every row of its hierarchy, subtypes included, and none of another entity's.
        Assert.Equal("SELECT t.id AS id, t.body AS body, t.created_at AS createdAt\nFROM public.notes t\nWHERE t.kind IN " + BaseIn, select);
        Assert.Equal("SELECT t.id AS id, t.body AS body, t.created_at AS createdAt\nFROM public.notes t\nWHERE t.kind IN " + BaseIn + " AND t.id = @id", byKey);
        Assert.Equal("INSERT INTO public.notes (id, body, kind)\nVALUES (@id, @body, 'note')", insert);
        Assert.Equal("UPDATE public.notes SET body = @body\nWHERE id = @id AND kind IN " + BaseIn, update);
        Assert.Equal("DELETE FROM public.notes\nWHERE id = @id AND kind IN " + BaseIn, delete);
    }

    [Fact]
    public async Task Validation_accepts_the_type_codes_of_a_shared_table()
    {
        var result = await EngineServices.Create(ResolutionKit.Options).Validator.ValidateAsync(new Notes().B.Build(), ValidationScope.All, null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_derived_entity_reads_its_rows_and_those_of_its_subtypes_and_writes_its_code()
    {
        var model = new Notes().Resolve();

        var invoice = BindingOf(model, "InvoiceNote");
        var typeColumn = Assert.IsType<RBindingDiscriminator>(invoice.DiscriminatorColumn);
        Assert.Equal(("Note", "invoice"), (typeColumn.Base.Name, typeColumn.Value));
        Assert.Equal(new object[] { "invoice", "UrgentNote" }, typeColumn.Codes);
        Assert.Contains("e:" + model.Entity("UrgentNote").Id, invoice.Dependencies);
        Assert.Equal("discriminator", Assert.Single(invoice.Columns, c => c.Name == "kind").Status);

        var (select, byKey, insert, update, delete) = Statements(invoice);
        Assert.Equal("SELECT t.id AS id, t.entity_id AS invoiceId, t.body AS body, t.created_at AS createdAt\nFROM public.notes t\nWHERE t.kind IN ('invoice', 'UrgentNote')", select);
        Assert.EndsWith("WHERE t.kind IN ('invoice', 'UrgentNote') AND t.id = @id", byKey, StringComparison.Ordinal);
        Assert.Equal("INSERT INTO public.notes (id, entity_id, body, kind)\nVALUES (@id, @invoiceId, @body, 'invoice')", insert);
        Assert.Equal("UPDATE public.notes SET entity_id = @invoiceId, body = @body\nWHERE id = @id AND kind IN ('invoice', 'UrgentNote')", update);
        Assert.Equal("DELETE FROM public.notes\nWHERE id = @id AND kind IN ('invoice', 'UrgentNote')", delete);

        // One code is an equality; a code of its own defaults to the entity's name.
        var customer = Statements(BindingOf(model, "CustomerNote"));
        Assert.EndsWith("WHERE t.kind = 'customer'", customer.Select, StringComparison.Ordinal);
        Assert.Equal("DELETE FROM public.notes\nWHERE id = @id AND kind = 'customer'", customer.Delete);
        var urgent = BindingOf(model, "UrgentNote");
        Assert.Equal(("Note", "UrgentNote"), (urgent.DiscriminatorColumn!.Base.Name, urgent.DiscriminatorColumn.Value));
        Assert.Equal("INSERT INTO public.notes (id, entity_id, body, kind)\nVALUES (@id, @invoiceId, @body, 'UrgentNote')", Statements(urgent).Insert);
        Assert.EndsWith("WHERE t.kind = 'UrgentNote'", Statements(urgent).Select, StringComparison.Ordinal);
    }

    [Fact]
    public void Integer_codes_are_written_as_numbers()
    {
        var notes = new Notes((s, entity, b) => b with { DiscriminatorValue = Notes.Json(entity switch { "Note" => "1", "InvoiceNote" => "2", "CustomerNote" => "3", _ => "4" }) });
        var model = notes.Resolve();

        Assert.Contains(model.Diagnostics, d => d.Rule == "MQ4125" && d.JsonPointer == "/bindings/0/discriminatorValue");
        Assert.Equal("DELETE FROM public.notes\nWHERE id = @id AND kind IN (2, 4)", Statements(BindingOf(model, "InvoiceNote")).Delete);
    }

    [Fact]
    public void A_field_on_the_type_column_is_read_but_never_written()
    {
        var model = new Notes(baseShowsKind: true).Resolve();
        var binding = BindingOf(model, "Note");

        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var field = Assert.Single(binding.Fields, f => f.Name == "kind");
        Assert.Equal((false, false), (field.InInsert, field.InUpdate));
        Assert.Equal("field", Assert.Single(binding.Columns, c => c.Name == "kind").Status);
        Assert.Equal("INSERT INTO public.notes (id, body, kind)\nVALUES (@id, @body, 'note')", BindingSql.Render(binding, "insert").Sql);
        Assert.Equal("SELECT t.id AS id, t.body AS body, t.created_at AS createdAt, t.kind AS kind\nFROM public.notes t\nWHERE t.kind IN " + BaseIn, BindingSql.Render(binding, "select").Sql);
    }

    [Fact]
    public void The_resolved_record_carries_the_type_column()
    {
        var model = new Notes().Resolve();

        Editor.ContractCheck.Validate(model, "bindingDiscriminatorRecord", "bindingDiscriminatorView");
        var invoice = BindingViews.Record(BindingOf(model, "InvoiceNote"));
        Assert.Equal("invoice", invoice.DiscriminatorValue);
        var typeColumn = Assert.IsType<BindingDiscriminatorRecord>(invoice.DiscriminatorColumn);
        Assert.Equal(("kind", model.Entity("Note").Id, "invoice"), (typeColumn.Column, typeColumn.Base, typeColumn.Value));
        Assert.Equal(new object[] { "invoice", "UrgentNote" }, typeColumn.Codes);
        Assert.NotNull(typeColumn.WriteColumn);
        Assert.Equal(BaseCodes, BindingViews.Record(BindingOf(model, "Note")).DiscriminatorColumn!.Codes);

        // The table's list of bindings shows how each entity's rows are told apart: the type column and the codes.
        var notes = model.Db("main").Table("notes");
        var boundBy = BindingViews.BoundBy(notes.BoundBy, notes).ToDictionary(b => b.EntityName, StringComparer.Ordinal);
        Assert.Equal(["CustomerNote", "InvoiceNote", "Note", "UrgentNote"], boundBy.Keys.Order(StringComparer.Ordinal));
        Assert.All(boundBy.Values, b => Assert.Equal("kind", b.Discriminator!.Column));
        Assert.Equal("invoice", boundBy["InvoiceNote"].Discriminator!.Value);
        Assert.Equal(new object[] { "invoice", "UrgentNote" }, boundBy["InvoiceNote"].Discriminator!.Codes);
        Assert.Equal(BaseCodes, boundBy["Note"].Discriminator!.Codes);
    }

    [Fact]
    public void Bindings_without_a_type_column_have_none()
    {
        var model = new Notes((s, entity, b) => b with { DiscriminatorColumn = null }).Resolve();

        Assert.All(model.Entities.Select(e => e.Bindings["main"]), b => Assert.Null(b.DiscriminatorColumn));
        Assert.Null(BindingViews.Record(BindingOf(model, "InvoiceNote")).DiscriminatorColumn);
    }

    [Fact]
    public void A_seed_row_of_a_derived_entity_holds_its_code()
    {
        var notes = new Notes();
        notes.B.Add(new Seed
        {
            Id = notes.B.NewId(), Name = "welcomeNotes", Target = notes.CustomerNote.Id, Columns = ["id", "customerId", "body"],
            Rows = [new SeedRow { Id = notes.B.NewId(), Values = [Notes.Json("\"018f0000-0000-7000-8000-000000000001\""), Notes.Json("\"018f0000-0000-7000-8000-000000000002\""), Notes.Json("\"Welcome\"")] }],
        });
        var model = notes.Resolve();

        var row = Assert.Single(Assert.Single(model.Db("main").SeedTables, t => t.Table.Name == "notes").Rows);
        Assert.Equal("customer", row.Values["kind"]);
    }

    [Fact]
    public void The_drift_report_counts_an_inherited_type_column_as_named()
    {
        var notes = new Notes();
        var snapshot = notes.B.Build();
        var entity = snapshot.Get<Entity>(notes.CustomerNote.Id)!;

        Assert.Equal(notes.Kind, BindingDrift.TypeColumnOf(snapshot, entity, entity.Bindings[0]));
        Assert.DoesNotContain(BindingDrift.Of(snapshot, entity, entity.Bindings[0]).Added, a => a.Name == "kind");
        // Only a base's binding to the same database gives it.
        Assert.Null(BindingDrift.TypeColumnOf(snapshot, entity, entity.Bindings[0] with { Database = notes.Archive }));
    }

    [Fact]
    public void A_derived_entity_bound_to_another_table_may_name_its_own_type_column()
    {
        // InvoiceNote and UrgentNote keep their rows in filed_notes; InvoiceNote names the type column there.
        var notes = new Notes((s, entity, b) => entity switch
        {
            "InvoiceNote" => s.ToFiled(b) with { DiscriminatorColumn = "kind" },
            "UrgentNote" => s.ToFiled(b),
            _ => b,
        });
        var model = notes.Resolve();

        Assert.DoesNotContain(model.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
        var invoice = BindingOf(model, "InvoiceNote").DiscriminatorColumn!;
        Assert.Equal("InvoiceNote", invoice.Base.Name);
        Assert.Equal(new object[] { "invoice", "UrgentNote" }, invoice.Codes);
        Assert.Equal("InvoiceNote", BindingOf(model, "UrgentNote").DiscriminatorColumn!.Base.Name);
        Assert.Equal(new object[] { "note", "customer" }, BindingOf(model, "Note").DiscriminatorColumn!.Codes);
    }

    [Fact]
    public void A_derived_entity_in_the_base_table_takes_the_base_column_when_it_names_one()
    {
        // MQ4126 refuses the key, and the binding keeps the base's column and filters as before.
        var model = new Notes((s, entity, b) => entity == "InvoiceNote" ? b with { DiscriminatorColumn = "entity_id" } : b).Resolve();

        var refused = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4126");
        Assert.Equal((DiagnosticSeverity.Error, "/bindings/0/discriminatorColumn"), (refused.Severity, refused.JsonPointer));
        var invoice = BindingOf(model, "InvoiceNote").DiscriminatorColumn!;
        Assert.Equal(("Note", "kind"), (invoice.Base.Name, invoice.ColumnName));
        Assert.Equal(BaseCodes, BindingOf(model, "Note").DiscriminatorColumn!.Codes);
    }

    public static TheoryData<string, string, string, string> Rules() => new()
    {
        { "missing", "Note", "MQ4122", "/bindings/0/discriminatorColumn" },
        { "derived-source", "CustomerNote", "MQ4122", "/bindings/0/source" },
        { "derived-write", "CustomerNote", "MQ4122", "/bindings/0/write/table" },
        { "elsewhere", "Note", "MQ4123", "/bindings/0/discriminatorColumn" },
        { "constant", "InvoiceNote", "MQ4124", "/bindings/0/constants/0/column" },
        { "soft-type", "InvoiceNote", "MQ4124", "/bindings/0/delete/soft/column" },
        { "nested", "UrgentNote", "MQ4126", "/bindings/0/discriminatorColumn" },
        { "same-code", "CustomerNote", "MQ4125", "/bindings/0/discriminatorValue" },
        { "fit", "UrgentNote", "MQ4125", "/bindings/0/discriminatorValue" },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Each_type_column_rule_points_into_the_entity_file(string mistake, string entity, string rule, string pointer)
    {
        EntityBinding Break(Notes s, string name, EntityBinding b)
        {
            // Every derived note moves to filed_notes: the base keeps the table alone.
            if (mistake == "elsewhere")
                return name == "Note" ? b : s.ToFiled(b);
            if (name != entity && !(mistake == "same-code" && name == "InvoiceNote"))
                return b;
            return mistake switch
            {
                "missing" => b with { DiscriminatorColumn = "nope" },
                "derived-source" => b with { Source = s.Archive },
                "derived-write" => b with { Write = new BindingWrite { Table = s.Archive } },
                "soft-type" => b with { Delete = new BindingDelete { Mode = BindingDeleteMode.Soft, Soft = new SoftDelete { Column = s.Kind, Value = Notes.Json("\"gone\"") } } },
                "nested" => b with { DiscriminatorColumn = s.Kind },
                "constant" => b with { Constants = [new BindingConstant { Column = s.Kind, Value = Notes.Json("\"invoice\"") }] },
                "same-code" => b with { DiscriminatorValue = Notes.Json("\"shared\"") },
                "fit" => b with { DiscriminatorValue = Notes.Json("\"" + new string('x', 40) + "\"") },
                _ => b,
            };
        }

        var notes = new Notes(Break);
        var model = notes.Resolve();

        var found = Assert.Single(model.Diagnostics, d => d.Rule == rule);
        Assert.Equal((RuleCatalog.Get(rule).DefaultSeverity, pointer), (found.Severity, found.JsonPointer));
        var expected = mistake == "same-code"
            ? new[] { notes.InvoiceNote.Id, notes.CustomerNote.Id }
            : [entity switch { "Note" => notes.Note.Id, "InvoiceNote" => notes.InvoiceNote.Id, "CustomerNote" => notes.CustomerNote.Id, _ => notes.UrgentNote.Id }];
        Assert.Contains(found.ElementId, expected);
    }
}
