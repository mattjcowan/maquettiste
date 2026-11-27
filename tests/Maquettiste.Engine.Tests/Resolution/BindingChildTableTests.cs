using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// An attribute in a table of its own (retire-projection-design.md section 5, <c>childTables</c>) and field paths of any depth (section 6): a
/// list of value objects with a position, a list of single values with an id of its own, a single value object at most one row, nested value
/// objects embedded in columns and JSON columns that hold a nested value object or a list; the statements each entry renders; MQ4137 to MQ4141,
/// MQ4047 and MQ4058 at the JSON pointer of the entry in the entity's file.
/// </summary>
public sealed class BindingChildTableTests
{
    /// <summary>
    /// <c>quotes (id identity, number, billing_street, billing_city, billing_notes json, billing_latitude, billing_longitude)</c> holds each quote
    /// with its billing address in columns, the map point nested two levels down; <c>quote_notes (quote_id, position, text, author)</c> its notes
    /// in order; <c>quote_labels (id identity, quote_id, label)</c> its labels, a key that does not cascade; <c>quote_shipping (quote_id, street,
    /// city, geo json)</c> its shipping address, at most one row.
    /// </summary>
    internal sealed class Quotes
    {
        public Quotes(Func<Quotes, EntityBinding, EntityBinding>? edit = null, Dialect dialect = Dialect.PostgreSql, Action<Quotes>? before = null,
            bool numberCascades = true, bool shippingKey = true)
        {
            B = new ModelBuilder(seed: 73);
            var db = B.Database("main", dialect);
            Main = db.Id;
            Geo = B.ValueObject("GeoPoint").Attr("latitude", "decimal", a => a.Precision(9).Scale(6).Required()).Attr("longitude", "decimal", a => a.Precision(9).Scale(6).Required());
            Address = B.ValueObject("PostalAddress").Attr("street", "string", a => a.Length(120).Required()).Attr("city", "string", a => a.Length(80).Required())
                .Attr("deliveryNotes", "string", a => a.Length(200).Collection()).Attr("geo", Geo);
            Note = B.ValueObject("QuoteNote").Attr("text", "string", a => a.Length(500).Required()).Attr("author", "string", a => a.Length(80));

            (QuotesTable, Id, Number, Street, City, Notes, Latitude, Longitude) = (B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId());
            NumberKey = B.NewId();
            B.Add(new Table
            {
                Id = QuotesTable, Name = "quotes", Database = Main,
                Columns =
                [
                    new Column { Id = Id, Name = "id", Type = "int64", Nullable = false, Generated = ColumnGeneration.Identity },
                    new Column { Id = Number, Name = "number", Type = "string", Length = 20, Nullable = false },
                    new Column { Id = Street, Name = "billing_street", Type = "string", Length = 120, Nullable = false },
                    new Column { Id = City, Name = "billing_city", Type = "string", Length = 80, Nullable = false },
                    new Column { Id = Notes, Name = "billing_notes", Type = "json", Nullable = true },
                    new Column { Id = Latitude, Name = "billing_latitude", Type = "decimal", Precision = 9, Scale = 6, Nullable = true },
                    new Column { Id = Longitude, Name = "billing_longitude", Type = "decimal", Precision = 9, Scale = 6, Nullable = true },
                ],
                PrimaryKey = new PrimaryKey { Columns = [Id] },
                Uniques = [new UniqueConstraint { Id = NumberKey, Columns = [Number] }],
            });
            (NotesTable, NoteQuote, NotePosition, NoteText, NoteAuthor, NotesKey, NotesByNumber) = (B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId());
            NoteNumber = B.NewId();
            B.Add(new Table
            {
                Id = NotesTable, Name = "quote_notes", Database = Main,
                Columns =
                [
                    new Column { Id = NoteQuote, Name = "quote_id", Type = "int64", Nullable = false },
                    new Column { Id = NotePosition, Name = "position", Type = "int32", Nullable = false },
                    new Column { Id = NoteText, Name = "text", Type = "string", Length = 500, Nullable = false },
                    new Column { Id = NoteAuthor, Name = "author", Type = "string", Length = 80, Nullable = true },
                    new Column { Id = NoteNumber, Name = "quote_number", Type = "string", Length = 20, Nullable = true },
                ],
                PrimaryKey = new PrimaryKey { Columns = [NoteQuote, NotePosition] },
                ForeignKeys =
                [
                    new ForeignKey { Id = NotesKey, Name = "fk_quote_notes_quote", Columns = [NoteQuote], ReferencesTable = QuotesTable, ReferencesColumns = [Id], OnDelete = ReferentialAction.Cascade },
                    new ForeignKey { Id = NotesByNumber, Name = "fk_quote_notes_number", Columns = [NoteNumber], ReferencesTable = QuotesTable, ReferencesColumns = [Number], OnUpdate = numberCascades ? ReferentialAction.Cascade : ReferentialAction.NoAction },
                ],
            });
            (LabelsTable, LabelId, LabelQuote, Label, LabelsKey) = (B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId());
            B.Add(new Table
            {
                Id = LabelsTable, Name = "quote_labels", Database = Main,
                Columns =
                [
                    new Column { Id = LabelId, Name = "id", Type = "int64", Nullable = false, Generated = ColumnGeneration.Identity },
                    new Column { Id = LabelQuote, Name = "quote_id", Type = "int64", Nullable = false },
                    new Column { Id = Label, Name = "label", Type = "string", Length = 40, Nullable = false },
                ],
                PrimaryKey = new PrimaryKey { Columns = [LabelId] },
                ForeignKeys = [new ForeignKey { Id = LabelsKey, Name = "fk_quote_labels_quote", Columns = [LabelQuote], ReferencesTable = QuotesTable, ReferencesColumns = [Id] }],
            });
            (ShippingTable, ShippingQuote, ShippingStreet, ShippingCity, ShippingGeo, ShippingKey) = (B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId());
            B.Add(new Table
            {
                Id = ShippingTable, Name = "quote_shipping", Database = Main,
                Columns =
                [
                    new Column { Id = ShippingQuote, Name = "quote_id", Type = "int64", Nullable = false },
                    new Column { Id = ShippingStreet, Name = "street", Type = "string", Length = 120, Nullable = false },
                    new Column { Id = ShippingCity, Name = "city", Type = "string", Length = 80, Nullable = false },
                    new Column { Id = ShippingGeo, Name = "geo", Type = "json", Nullable = true },
                ],
                PrimaryKey = shippingKey ? new PrimaryKey { Columns = [ShippingQuote] } : null,
                ForeignKeys = [new ForeignKey { Id = ShippingKey, Name = "fk_quote_shipping_quote", Columns = [ShippingQuote], ReferencesTable = QuotesTable, ReferencesColumns = [Id], OnDelete = ReferentialAction.Cascade }],
            });

            Quote = B.Entity("Quote").Key("id", "int64", IdentityStrategy.DatabaseIdentity).Attr("number", "string", a => a.Length(20).Required())
                .Attr("billingAddress", Address, a => a.Required()).Attr("shippingAddress", Address).Attr("notes", Note, a => a.Collection())
                .Attr("labels", "string", a => a.Length(40).Collection());
            before?.Invoke(this);
            var binding = new EntityBinding
            {
                Id = "", Database = Main, Source = QuotesTable,
                Fields =
                [
                    Field("id", Id), Field("number", Number),
                    Field(Path("billingAddress", Address.AttrId("street")), Street), Field(Path("billingAddress", Address.AttrId("city")), City),
                    Field(Path("billingAddress", Address.AttrId("deliveryNotes")), Notes),
                    Field(Path("billingAddress", Address.AttrId("geo"), Geo.AttrId("latitude")), Latitude),
                    Field(Path("billingAddress", Address.AttrId("geo"), Geo.AttrId("longitude")), Longitude),
                ],
                ChildTables =
                [
                    new BindingChildTable
                    {
                        Id = B.NewId(), Attribute = Quote.AttrId("notes"), Table = NotesTable, OwnerForeignKey = NotesKey, OrderColumn = NotePosition,
                        Fields = [Field(Path("notes", Note.AttrId("text")), NoteText), Field(Path("notes", Note.AttrId("author")), NoteAuthor)],
                        Columns = [new BindingColumn { Column = NoteNumber, Status = BindingColumnStatus.Ignored }],
                    },
                    new BindingChildTable
                    {
                        Id = B.NewId(), Attribute = Quote.AttrId("labels"), Table = LabelsTable, OwnerForeignKey = LabelsKey,
                        Fields = [Field("labels", Label)], Columns = [new BindingColumn { Column = LabelId, Status = BindingColumnStatus.Database }],
                    },
                    new BindingChildTable
                    {
                        Id = B.NewId(), Attribute = Quote.AttrId("shippingAddress"), Table = ShippingTable, OwnerForeignKey = ShippingKey,
                        Fields =
                        [
                            Field(Path("shippingAddress", Address.AttrId("street")), ShippingStreet), Field(Path("shippingAddress", Address.AttrId("city")), ShippingCity),
                            Field(Path("shippingAddress", Address.AttrId("geo")), ShippingGeo),
                        ],
                    },
                ],
            };
            Quote.Bind(edit is null ? binding : edit(this, binding));
        }

        public string Path(string attribute, params string[] members) => string.Join('.', [Quote.AttrId(attribute), .. members]);

        public BindingField Field(string attribute, string column) => new() { Attribute = attribute.Contains('.', StringComparison.Ordinal) ? attribute : Quote.AttrId(attribute), Column = column };

        public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        public ModelBuilder B { get; }

        public string Main { get; }

        public ValueObjectBuilder Geo { get; }

        public ValueObjectBuilder Address { get; }

        public ValueObjectBuilder Note { get; }

        public string QuotesTable { get; }

        public string Id { get; }

        public string Number { get; }

        public string NumberKey { get; }

        public string Street { get; }

        public string City { get; }

        public string Notes { get; }

        public string Latitude { get; }

        public string Longitude { get; }

        public string NotesTable { get; }

        public string NoteQuote { get; }

        public string NotePosition { get; }

        public string NoteText { get; }

        public string NoteAuthor { get; }

        public string NoteNumber { get; }

        public string NotesKey { get; }

        public string NotesByNumber { get; }

        public string LabelsTable { get; }

        public string LabelId { get; }

        public string LabelQuote { get; }

        public string Label { get; }

        public string LabelsKey { get; }

        public string ShippingTable { get; }

        public string ShippingQuote { get; }

        public string ShippingStreet { get; }

        public string ShippingCity { get; }

        public string ShippingGeo { get; }

        public string ShippingKey { get; }

        public EntityBuilder Quote { get; }

        public ResolvedModel Resolve() => ResolutionKit.Resolve(B);
    }

    private static REntityBinding BindingOf(ResolvedModel model, string entity = "Quote") => model.Entity(entity).Bindings["main"];

    private static string Sql(RBindingChildTable child, string statement, string? dialect = null) => BindingSql.Render(child, statement, dialect).Sql;

    private static void NoProblems(ResolvedModel model) =>
        Assert.DoesNotContain(model.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);

    private static EntityBinding WithChild(EntityBinding binding, int index, Func<BindingChildTable, BindingChildTable> change) =>
        binding with { ChildTables = [.. binding.ChildTables.Select((c, i) => i == index ? change(c) : c)] };

    [Fact]
    public void Each_entry_resolves_its_attribute_table_owner_fields_and_columns()
    {
        var quotes = new Quotes();
        var model = quotes.Resolve();

        NoProblems(model);
        var binding = BindingOf(model);
        Assert.Equal(["notes", "labels", "shippingAddress"], binding.ChildTables.Select(c => c.Attribute!.Name));
        var notes = binding.ChildTables[0];
        Assert.Equal((true, "quote_notes", "fk_quote_notes_quote", "position", "position", true, "copy"),
            (notes.IsList, notes.Table!.Name, notes.OwnerForeignKey!.Name, notes.OrderColumn!.Name, notes.OrderParameter, notes.Writes, notes.OnCopy));
        var owner = Assert.Single(notes.Owner);
        Assert.Equal(("quote_id", "id", "id", "ownerId"), (owner.Column.Name, owner.ReferencedColumn.Name, owner.Field!.Name, owner.Parameter));
        Assert.Equal(["text", "author"], notes.Fields.Select(f => f.Name));
        Assert.All(notes.Fields, f => Assert.Equal((true, false), (f.InInsert, f.InUpdate)));
        Assert.Equal(["owner", "order", "field", "field", "ignored"], notes.Columns.Select(c => c.Status));

        // A list of single values maps the attribute itself, named value; its own generated id is the database's.
        var labels = binding.ChildTables[1];
        var value = Assert.Single(labels.Fields);
        Assert.Equal(("value", "label", null, 0), (value.Name, value.ColumnName, value.Member, value.Members.Count));
        Assert.Equal(["database", "owner", "field"], labels.Columns.Select(c => c.Status));
        Assert.Null(labels.OrderColumn);

        // A single value object: at most one row; a nested value object in one JSON column.
        var shipping = binding.ChildTables[2];
        Assert.False(shipping.IsList);
        Assert.Equal(["street", "city", "geo"], shipping.Fields.Select(f => f.Name));
        Assert.Equal("json", shipping.Fields[2].Type);

        // The child tables list the binding: it reads them and writes them.
        var table = model.Db("main").Table("quote_notes");
        Assert.Same(binding, Assert.Single(table.BoundBy));
    }

    [Fact]
    public void A_field_path_walks_value_objects_to_any_depth()
    {
        var model = new Quotes().Resolve();

        var binding = BindingOf(model);
        var latitude = binding.Fields.Single(f => f.Name == "billingAddressGeoLatitude");
        Assert.Equal(("billingAddress", "latitude"), (latitude.Attribute!.Name, latitude.Member!.Name));
        Assert.Equal(["geo", "latitude"], latitude.Members.Select(m => m.Name));
        Assert.Equal((true, true), (latitude.InInsert, latitude.InUpdate));
        // A path that ends at a list maps a JSON column; a path of one member keeps the shape it had.
        var notes = binding.Fields.Single(f => f.Name == "billingAddressDeliveryNotes");
        Assert.Equal(("deliveryNotes", true), (notes.Member!.Name, notes.Member.Collection));
        var street = binding.Fields.Single(f => f.Name == "billingAddressStreet");
        Assert.Equal(["street"], street.Members.Select(m => m.Name));

        var record = BindingViews.Record(binding);
        Assert.Equal(2, record.Fields.Single(f => f.Name == "billingAddressGeoLatitude").MemberPath!.Count);
        Assert.Null(record.Fields.Single(f => f.Name == "billingAddressStreet").MemberPath);
        Assert.Equal("SELECT t.id AS id, t.number AS number, t.billing_street AS billingAddressStreet, t.billing_city AS billingAddressCity, t.billing_notes AS billingAddressDeliveryNotes, "
            + "t.billing_latitude AS billingAddressGeoLatitude, t.billing_longitude AS billingAddressGeoLongitude\nFROM public.quotes t", BindingSql.Render(binding, "select").Sql);
    }

    [Fact]
    public void Each_entry_renders_its_select_by_owner_delete_by_owner_and_insert_of_one_row()
    {
        var binding = BindingOf(new Quotes().Resolve());
        var (notes, labels, shipping) = (binding.ChildTables[0], binding.ChildTables[1], binding.ChildTables[2]);

        Assert.Equal("SELECT t.text AS text, t.author AS author\nFROM public.quote_notes t\nWHERE t.quote_id = @ownerId\nORDER BY t.position", Sql(notes, "select"));
        Assert.Equal("DELETE FROM public.quote_notes\nWHERE quote_id = @ownerId", Sql(notes, "delete"));
        Assert.Equal("INSERT INTO public.quote_notes (quote_id, position, text, author)\nVALUES (@ownerId, @position, @text, @author)", Sql(notes, "insert"));
        Assert.Equal(["ownerId", "position", "text", "author"], BindingSql.Render(notes, "insert").Parameters);
        Assert.Equal("INSERT INTO public.quote_labels (quote_id, label)\nVALUES (@ownerId, @value)", Sql(labels, "insert"));
        Assert.Equal("SELECT t.street AS street, t.city AS city, t.geo AS geo\nFROM public.quote_shipping t\nWHERE t.quote_id = @ownerId", Sql(shipping, "select"));
        Assert.Equal("", Sql(notes, "update"));

        Assert.Equal("SELECT t.text AS text, t.author AS author\nFROM dbo.quote_notes t\nWHERE t.quote_id = @ownerId\nORDER BY t.position",
            Sql(BindingOf(new Quotes(dialect: Dialect.SqlServer).Resolve()).ChildTables[0], "select"));
        foreach (var dialect in new[] { "postgresql", "sqlserver", "mysql", "sqlite", "oracle" })
        {
            foreach (var statement in BindingSql.ChildStatements)
                Assert.NotEqual("", Sql(notes, statement, dialect));
        }

        Assert.Equal("INSERT INTO public.quote_notes (quote_id, position, text, author)\nVALUES ($1, $2, $3, $4)",
            BindingSql.Render(notes, "insert", null, new QuerySqlOptions { Placeholder = "$" }).Sql);
        Assert.Throws<ArgumentException>(() => BindingSql.Render(notes, "merge"));
    }

    [Fact]
    public void A_read_only_binding_reads_its_child_tables_and_writes_none()
    {
        var binding = BindingOf(new Quotes((_, b) => b with { Write = new BindingWrite { None = true } }).Resolve());

        var notes = binding.ChildTables[0];
        Assert.False(notes.Writes);
        Assert.NotEqual("", Sql(notes, "select"));
        Assert.Equal(("", ""), (Sql(notes, "insert"), Sql(notes, "delete")));
        Assert.All(notes.Fields, f => Assert.False(f.InInsert));
    }

    [Fact]
    public void An_owner_foreign_key_on_a_unique_key_takes_the_owners_field_for_it()
    {
        var model = new Quotes((q, b) => WithChild(b, 0, c => c with
        {
            OwnerForeignKey = q.NotesByNumber, OrderColumn = q.NotePosition,
            Columns = [new BindingColumn { Column = q.NoteQuote, Status = BindingColumnStatus.Ignored }],
        })).Resolve();

        NoProblems(model);
        var notes = BindingOf(model).ChildTables[0];
        var owner = Assert.Single(notes.Owner);
        Assert.Equal(("quote_number", "number", "ownerNumber"), (owner.Column.Name, owner.Field!.Name, owner.Parameter));
        Assert.Equal("DELETE FROM public.quote_notes\nWHERE quote_number = @ownerNumber", Sql(notes, "delete"));
    }

    [Theory]
    [InlineData("not-an-attribute", "MQ4137", "/bindings/0/childTables/0/attribute")]
    [InlineData("scalar", "MQ4137", "/bindings/0/childTables/0/attribute")]
    [InlineData("twice", "MQ4137", "/bindings/0/childTables/1/attribute")]
    [InlineData("also-a-field", "MQ4137", "/bindings/0/childTables/2/attribute")]
    [InlineData("table-of-another-database", "MQ4138", "/bindings/0/childTables/0/table")]
    [InlineData("key-of-another-table", "MQ4138", "/bindings/0/childTables/0/ownerForeignKey")]
    [InlineData("key-not-to-the-owner", "MQ4138", "/bindings/0/childTables/0/ownerForeignKey")]
    [InlineData("owner-column-as-field", "MQ4139", "/bindings/0/childTables/0/fields/0/column")]
    [InlineData("path-of-another-attribute", "MQ4140", "/bindings/0/childTables/0/fields/0/attribute")]
    [InlineData("no-such-member", "MQ4140", "/bindings/0/childTables/0/fields/0/attribute")]
    [InlineData("value-object-itself", "MQ4140", "/bindings/0/childTables/2/fields/0/attribute")]
    [InlineData("path-twice", "MQ4140", "/bindings/0/childTables/0/fields/1/attribute")]
    [InlineData("whole-and-member", "MQ4140", "/bindings/0/childTables/2/fields/3/attribute")]
    [InlineData("no-such-column", "MQ4140", "/bindings/0/childTables/0/fields/0/column")]
    [InlineData("order-on-single", "MQ4141", "/bindings/0/childTables/2/orderColumn")]
    [InlineData("order-not-integer", "MQ4141", "/bindings/0/childTables/0/orderColumn")]
    [InlineData("unaccounted", "MQ4047", "/bindings/0/childTables/0")]
    [InlineData("member-unmapped", "MQ4058", "/bindings/0/childTables/0/fields")]
    public void A_child_table_that_does_not_fit_is_reported_at_its_entry(string mistake, string rule, string pointer)
    {
        string? other = null;
        BindingChildTable Break(Quotes q, BindingChildTable c, int i) => (mistake, i) switch
        {
            ("not-an-attribute", 0) => c with { Attribute = q.Address.AttrId("street") },
            ("scalar", 0) => c with { Attribute = q.Quote.AttrId("number") },
            ("twice", 1) => c with { Attribute = q.Quote.AttrId("notes") },
            ("table-of-another-database", 0) => c with { Table = other! },
            ("key-of-another-table", 0) => c with { OwnerForeignKey = q.LabelsKey },
            ("owner-column-as-field", 0) => c with { Fields = [new BindingField { Attribute = q.Path("notes", q.Note.AttrId("text")), Column = q.NoteQuote }, c.Fields[1]] },
            ("path-of-another-attribute", 0) => c with { Fields = [new BindingField { Attribute = q.Path("shippingAddress", q.Address.AttrId("street")), Column = q.NoteText }, c.Fields[1]] },
            ("no-such-member", 0) => c with { Fields = [new BindingField { Attribute = q.Path("notes", q.Address.AttrId("street")), Column = q.NoteText }, c.Fields[1]] },
            ("value-object-itself", 2) => c with { Fields = [new BindingField { Attribute = q.Quote.AttrId("shippingAddress"), Column = q.ShippingStreet }, c.Fields[1], c.Fields[2]] },
            ("path-twice", 0) => c with { Fields = [c.Fields[0], c.Fields[0] with { Column = q.NoteAuthor }] },
            ("whole-and-member", 2) => c with { Fields = [.. c.Fields, new BindingField { Attribute = q.Path("shippingAddress", q.Address.AttrId("geo"), q.Geo.AttrId("latitude")), Column = q.ShippingStreet }] },
            ("no-such-column", 0) => c with { Fields = [c.Fields[0] with { Column = "no_such_column" }, c.Fields[1]] },
            ("order-on-single", 2) => c with { OrderColumn = q.ShippingGeo },
            ("order-not-integer", 0) => c with { OrderColumn = q.NoteAuthor, Fields = [c.Fields[0]] },
            ("unaccounted", 0) => c with { Columns = [] },
            ("member-unmapped", 0) => c with { Fields = [c.Fields[0]] },
            _ => c,
        };

        var quotes = new Quotes((q, b) =>
        {
            b = b with { ChildTables = [.. b.ChildTables.Select((c, i) => Break(q, c, i))] };
            if (mistake == "also-a-field")
                b = b with { Fields = [.. b.Fields, q.Field(q.Path("shippingAddress", q.Address.AttrId("city")), q.City)] };
            if (mistake == "key-not-to-the-owner")
                b = b with { Source = q.LabelsTable, Fields = [], Columns = [] };
            return b;
        }, before: q =>
        {
            if (mistake != "table-of-another-database")
                return;
            var db = q.B.Database("other", Dialect.PostgreSql);
            other = q.B.NewId();
            q.B.Add(new Table { Id = other, Name = "other_notes", Database = db.Id, Columns = [new Column { Id = q.B.NewId(), Name = "id", Type = "int64" }] });
        });
        var model = quotes.Resolve();

        var found = Assert.Single(model.Diagnostics, d => d.Rule == rule && d.ElementId == quotes.Quote.Id && d.JsonPointer == pointer);
        Assert.Equal(RuleCatalog.Get(rule).DefaultSeverity, found.Severity);
    }

    [Fact]
    public void An_owner_key_whose_referenced_columns_no_field_maps_is_reported()
    {
        var quotes = new Quotes((q, b) => WithChild(b, 0, c => c with
        {
            OwnerForeignKey = q.NotesByNumber,
            Columns = [new BindingColumn { Column = q.NoteQuote, Status = BindingColumnStatus.Ignored }],
        }) with { Fields = [.. b.Fields.Where(f => f.Column != q.Number)] });
        var model = quotes.Resolve();

        var found = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4139");
        Assert.Equal("/bindings/0/childTables/0/ownerForeignKey", found.JsonPointer);
        Assert.Contains("'number'", found.Message, StringComparison.Ordinal);
        var notes = BindingOf(model).ChildTables[0];
        Assert.False(notes.Writes);
        Assert.Equal("", Sql(notes, "select"));
    }

    [Fact]
    public void An_attribute_a_child_table_stores_is_not_unmapped_and_a_value_object_nobody_stores_is()
    {
        // The binding stores the billing address's members and every child attribute: MQ4058 has nothing to say.
        Assert.DoesNotContain(new Quotes().Resolve().Diagnostics, d => d.Rule == "MQ4058");

        // Without its child table, a single value object counts as its members, nested ones included; lists are still skipped.
        var model = new Quotes((_, b) => b with { ChildTables = [.. b.ChildTables.Take(2)], Fields = [.. b.Fields.Take(4)] }).Resolve();
        var unmapped = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4058");
        Assert.Equal("/bindings/0/fields", unmapped.JsonPointer);
        Assert.Contains("billingAddressGeoLatitude, billingAddressGeoLongitude, shippingAddressStreet, shippingAddressCity, shippingAddressGeoLatitude, shippingAddressGeoLongitude", unmapped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DeliveryNotes", unmapped.Message, StringComparison.Ordinal);

        // A field on a nested value object stores it whole, in a JSON column.
        var whole = new Quotes((q, b) => b with { Fields = [.. b.Fields.Take(4), q.Field(q.Path("billingAddress", q.Address.AttrId("geo")), q.Notes)] }).Resolve();
        Assert.DoesNotContain(whole.Diagnostics, d => d.Rule is "MQ4058" or "MQ4045");

        // A value object with no member a field can map (only lists) is skipped, as a list attribute is.
        var lists = new Quotes(before: q => q.Quote.Attr("contacts", q.B.ValueObject("Contacts").Attr("phones", "string", a => a.Length(20).Collection()))).Resolve();
        Assert.DoesNotContain(lists.Diagnostics, d => d.Rule == "MQ4058");
    }

    [Fact]
    public void A_path_that_ends_at_a_value_object_or_a_list_maps_a_json_column_and_never_also_its_members()
    {
        // Held whole in a decimal column: refused, for the binding and for a child table.
        var decimalColumn = new Quotes((q, b) => b with { Fields = [.. b.Fields.Take(4), q.Field(q.Path("billingAddress", q.Address.AttrId("geo")), q.Latitude)] }).Resolve();
        Assert.Contains(decimalColumn.Diagnostics, d => d.Rule == "MQ4045" && d.JsonPointer == "/bindings/0/fields/4/column");
        var childColumn = new Quotes((q, b) => WithChild(b, 2, c => c with { Fields = [c.Fields[0], c.Fields[1], c.Fields[2] with { Column = q.ShippingCity }] })).Resolve();
        Assert.Contains(childColumn.Diagnostics, d => d.Rule == "MQ4140" && d.JsonPointer == "/bindings/0/childTables/2/fields/2/column");

        // Whole and member by member: billingAddress.geo beside billingAddress.geo.latitude.
        var both = new Quotes((q, b) => b with { Fields = [.. b.Fields, q.Field(q.Path("billingAddress", q.Address.AttrId("geo")), q.Notes)] }).Resolve();
        Assert.Contains(both.Diagnostics, d => d.Rule == "MQ4045" && d.JsonPointer == "/bindings/0/fields/7/attribute" && d.Message.Contains("whole and member by member", StringComparison.Ordinal));
    }

    [Fact]
    public void Constants_filter_reads_and_fill_inserts_and_tell_entries_in_one_table_apart()
    {
        EntityBinding Shared(Quotes q, EntityBinding b, bool constants) => b with
        {
            ChildTables =
            [
                b.ChildTables[0] with { Columns = [], Constants = constants ? [new BindingConstant { Column = q.NoteNumber, Value = Quotes.Json("\"note\"") }] : [] },
                b.ChildTables[1],
                b.ChildTables[2],
                new BindingChildTable
                {
                    Id = q.B.NewId(), Attribute = q.Quote.AttrId("labels"), Table = q.NotesTable, OwnerForeignKey = q.NotesKey,
                    Fields = [new BindingField { Attribute = q.Quote.AttrId("labels"), Column = q.NoteText }],
                    Columns = [new BindingColumn { Column = q.NoteAuthor, Status = BindingColumnStatus.Ignored }, new BindingColumn { Column = q.NotePosition, Status = BindingColumnStatus.Ignored }],
                    Constants = constants ? [new BindingConstant { Column = q.NoteNumber, Value = Quotes.Json("\"label\"") }] : [new BindingConstant { Column = q.NoteNumber, Value = Quotes.Json("\"label\"") }],
                },
            ],
        };

        var told = new Quotes((q, b) => Shared(q, b, constants: true)).Resolve();
        Assert.DoesNotContain(told.Diagnostics, d => d.Rule == "MQ4142");
        var notes = BindingOf(told).ChildTables[0];
        Assert.Equal("SELECT t.text AS text, t.author AS author\nFROM public.quote_notes t\nWHERE t.quote_id = @ownerId AND t.quote_number = 'note'\nORDER BY t.position", Sql(notes, "select"));
        Assert.Equal("INSERT INTO public.quote_notes (quote_id, position, text, author, quote_number)\nVALUES (@ownerId, @position, @text, @author, 'note')", Sql(notes, "insert"));
        Assert.Equal("DELETE FROM public.quote_notes\nWHERE quote_id = @ownerId AND quote_number = 'note'", Sql(notes, "delete"));
        Assert.Equal("SELECT t.text AS text, t.author AS author\nFROM public.quote_notes t\nWHERE t.quote_id = $1 AND t.quote_number = 'note'\nORDER BY t.position",
            BindingSql.Render(notes, "select", null, new QuerySqlOptions { Placeholder = "$" }).Sql);
        Assert.Equal("constant", notes.Columns.Single(c => c.Name == "quote_number").Status);

        // One entry with no constant: their rows are the same rows.
        var shared = new Quotes((q, b) => Shared(q, b, constants: false)).Resolve();
        var clash = Assert.Single(shared.Diagnostics, d => d.Rule == "MQ4142");
        Assert.Equal(("/bindings/0/childTables/3/constants", DiagnosticSeverity.Error), (clash.JsonPointer, clash.Severity));
    }

    [Fact]
    public void A_constant_on_a_column_the_entry_uses_is_reported_and_left_out()
    {
        var model = new Quotes((q, b) => WithChild(b, 0, c => c with { Constants = [new BindingConstant { Column = q.NoteText, Value = Quotes.Json("\"x\"") }] })).Resolve();

        Assert.Contains(model.Diagnostics, d => d.Rule == "MQ4140" && d.JsonPointer == "/bindings/0/childTables/0/constants/0/column");
        var notes = BindingOf(model).ChildTables[0];
        Assert.Equal("INSERT INTO public.quote_notes (quote_id, position, text, author)\nVALUES (@ownerId, @position, @text, @author)", Sql(notes, "insert"));
        Assert.DoesNotContain("'x'", Sql(notes, "select"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_owner_key_on_an_updatable_unique_key_without_on_update_cascade_is_a_warning()
    {
        EntityBinding ByNumber(Quotes q, EntityBinding b) => WithChild(b, 0, c => c with
        {
            OwnerForeignKey = q.NotesByNumber, Columns = [new BindingColumn { Column = q.NoteQuote, Status = BindingColumnStatus.Ignored }],
        });

        var warned = Assert.Single(new Quotes(ByNumber, numberCascades: false).Resolve().Diagnostics, d => d.Rule == "MQ4143");
        Assert.Equal(("/bindings/0/childTables/0/ownerForeignKey", DiagnosticSeverity.Warning), (warned.JsonPointer, warned.Severity));
        Assert.DoesNotContain(new Quotes(ByNumber).Resolve().Diagnostics, d => d.Rule == "MQ4143");
    }

    [Fact]
    public void A_single_value_object_whose_table_lets_an_owner_have_several_rows_is_a_warning()
    {
        var warned = Assert.Single(new Quotes(shippingKey: false).Resolve().Diagnostics, d => d.Rule == "MQ4144");
        Assert.Equal(("/bindings/0/childTables/2/table", DiagnosticSeverity.Warning), (warned.JsonPointer, warned.Severity));
    }

    [Fact]
    public void Each_dialect_renders_the_child_statements()
    {
        foreach (var (dialect, select, insert, delete) in new[]
        {
            (Dialect.MySql, "SELECT t.text AS text, t.author AS author\nFROM quote_notes t\nWHERE t.quote_id = @ownerId\nORDER BY t.position",
                "INSERT INTO quote_notes (quote_id, position, text, author)\nVALUES (@ownerId, @position, @text, @author)", "DELETE FROM quote_notes\nWHERE quote_id = @ownerId"),
            (Dialect.Sqlite, "SELECT t.text AS text, t.author AS author\nFROM quote_notes t\nWHERE t.quote_id = @ownerId\nORDER BY t.position",
                "INSERT INTO quote_notes (quote_id, position, text, author)\nVALUES (@ownerId, @position, @text, @author)", "DELETE FROM quote_notes\nWHERE quote_id = @ownerId"),
            (Dialect.Oracle, "SELECT t.text AS text, t.author AS author\nFROM quote_notes t\nWHERE t.quote_id = @ownerId\nORDER BY t.position",
                "INSERT INTO quote_notes (quote_id, position, text, author)\nVALUES (@ownerId, @position, @text, @author)", "DELETE FROM quote_notes\nWHERE quote_id = @ownerId"),
        })
        {
            var notes = BindingOf(new Quotes(dialect: dialect).Resolve()).ChildTables[0];
            Assert.Equal(select, Sql(notes, "select"));
            Assert.Equal(insert, Sql(notes, "insert"));
            Assert.Equal(delete, Sql(notes, "delete"));
        }

        Assert.Equal("DELETE FROM quote_notes\nWHERE quote_id = :ownerId",
            BindingSql.Render(BindingOf(new Quotes(dialect: Dialect.Oracle).Resolve()).ChildTables[0], "delete", null, new QuerySqlOptions { Placeholder = ":" }).Sql);
    }

    [Fact]
    public void A_composite_owner_key_pairs_each_column_with_the_owners_field()
    {
        var b = new ModelBuilder(seed: 75);
        var db = b.Database("main", Dialect.PostgreSql);
        string N() => b.NewId();
        var (orders, tenant, id, lines, lineTenant, lineOrder, text, key) = (N(), N(), N(), N(), N(), N(), N(), N());
        b.Add(new Table
        {
            Id = orders, Name = "orders", Database = db.Id,
            Columns = [new Column { Id = tenant, Name = "tenant", Type = "int32", Nullable = false }, new Column { Id = id, Name = "id", Type = "int64", Nullable = false }],
            PrimaryKey = new PrimaryKey { Columns = [tenant, id] },
        });
        b.Add(new Table
        {
            Id = lines, Name = "order_comments", Database = db.Id,
            Columns =
            [
                new Column { Id = lineTenant, Name = "tenant", Type = "int32", Nullable = false }, new Column { Id = lineOrder, Name = "order_id", Type = "int64", Nullable = false },
                new Column { Id = text, Name = "text", Type = "string", Length = 200, Nullable = false },
            ],
            ForeignKeys = [new ForeignKey { Id = key, Name = "fk_order_comments_order", Columns = [lineTenant, lineOrder], ReferencesTable = orders, ReferencesColumns = [tenant, id], OnDelete = ReferentialAction.Cascade }],
        });
        var order = b.Entity("Order").Key("tenant", "int32").Attr("id", "int64", a => a.Required()).Attr("comments", "string", a => a.Length(200).Collection());
        order.Bind(new EntityBinding
        {
            Id = "", Database = db.Id, Source = orders,
            Fields = [new BindingField { Attribute = order.AttrId("tenant"), Column = tenant }, new BindingField { Attribute = order.AttrId("id"), Column = id }],
            ChildTables = [new BindingChildTable { Id = N(), Attribute = order.AttrId("comments"), Table = lines, OwnerForeignKey = key, Fields = [new BindingField { Attribute = order.AttrId("comments"), Column = text }] }],
        });
        var model = ResolutionKit.Resolve(b);

        NoProblems(model);
        var comments = model.Entity("Order").Bindings["main"].ChildTables[0];
        Assert.Equal(["tenant>tenant:ownerTenant", "order_id>id:ownerId"], comments.Owner.Select(o => o.Column.Name + ">" + o.Field!.Name + ":" + o.Parameter));
        Assert.Equal("DELETE FROM public.order_comments\nWHERE tenant = @ownerTenant AND order_id = @ownerId", Sql(comments, "delete"));
    }

    [Fact]
    public void A_soft_deleted_owner_keeps_its_rows_and_a_view_source_reads_its_child_tables()
    {
        // A soft delete marks the owner: its child rows stay, and the entry's statements are the same.
        var soft = BindingOf(new Quotes((q, b) => b with
        {
            Delete = new BindingDelete { Mode = BindingDeleteMode.Soft, Soft = new SoftDelete { Column = q.Notes, Value = null } },
            Fields = [.. b.Fields.Where(f => f.Column != q.Notes)],
        }).Resolve());
        Assert.Equal("soft", soft.Delete);
        Assert.True(soft.ChildTables[0].Writes);
        Assert.Equal("DELETE FROM public.quote_notes\nWHERE quote_id = @ownerId", Sql(soft.ChildTables[0], "delete"));

        // Over a view that writes the quotes table, the write table owns the rows; over a view that writes none, nothing does.
        string? view = null;
        var names = new[] { "id", "number", "billing_street", "billing_city", "billing_notes", "billing_latitude", "billing_longitude" };
        void AddView(Quotes q)
        {
            view = q.B.NewId();
            q.B.Add(new View
            {
                Id = view, Name = "quote_list", Database = q.Main, Body = new Dictionary<string, string> { ["*"] = "SELECT * FROM quotes" },
                Columns = [.. names.Select(n => new ViewColumn { Name = n, Type = n.StartsWith("billing_l", StringComparison.Ordinal) ? "decimal" : n == "id" ? "int64" : n == "billing_notes" ? "json" : "string" })],
            });
        }

        EntityBinding OverView(Quotes q, EntityBinding b, BindingWrite write) => b with
        {
            Source = view!, Write = write, Fields = [.. b.Fields.Select((f, i) => f with { Column = names[i] })],
        };
        var written = BindingOf(new Quotes((q, b) => OverView(q, b, new BindingWrite { Table = q.QuotesTable }), before: AddView).Resolve());
        Assert.Equal("SELECT t.text AS text, t.author AS author\nFROM public.quote_notes t\nWHERE t.quote_id = @ownerId\nORDER BY t.position", Sql(written.ChildTables[0], "select"));
        var readOnly = new Quotes((q, b) => OverView(q, b, new BindingWrite { None = true }), before: AddView).Resolve();
        Assert.Contains(readOnly.Diagnostics, d => d.Rule == "MQ4138" && d.JsonPointer == "/bindings/0/childTables/0/ownerForeignKey");
    }

    [Fact]
    public void A_field_path_that_does_not_walk_value_objects_is_reported()
    {
        var model = new Quotes((q, b) => b with { Fields = [.. b.Fields, q.Field(q.Path("billingAddress", q.Address.AttrId("deliveryNotes"), q.Geo.AttrId("latitude")), q.Notes)] }).Resolve();

        Assert.Contains(model.Diagnostics, d => d.Rule == "MQ4045" && d.JsonPointer == "/bindings/0/fields/7/attribute");
    }

    [Fact]
    public void A_copy_carries_the_rows_as_the_attributes_onCopy_says()
    {
        var model = new Quotes(before: q => q.Quote.Copy(new EntityCopy
        {
            Attributes = [new CopyAttributeEntry { Attribute = q.Quote.AttrId("labels"), OnCopy = AttributeOnCopy.Clear }],
        })).Resolve();

        NoProblems(model);
        var binding = BindingOf(model);
        Assert.Equal(["copy", "clear", "copy"], binding.ChildTables.Select(c => c.OnCopy));
        Assert.Equal("clear", BindingViews.Record(binding).ChildTables![1].OnCopy);
    }

    [Fact]
    public void The_preview_and_the_record_carry_each_entry()
    {
        var binding = BindingOf(new Quotes().Resolve());

        var (preview, diagnostics) = BindingViews.Preview(binding, null, null);
        Assert.Empty(diagnostics);
        Assert.Equal(["notes", "labels", "shippingAddress"], preview.ChildTables!.Select(c => c.AttributeName));
        Assert.Equal("DELETE FROM public.quote_labels\nWHERE quote_id = @ownerId", preview.ChildTables![1].Delete!.Sql);

        var record = BindingViews.Record(binding);
        var notes = record.ChildTables![0];
        Assert.Equal((true, "position", true), (notes.IsList, notes.OrderColumn == null ? null : "position", notes.Writes));
        Assert.Equal("ownerId", Assert.Single(notes.Owner).Parameter);
        Assert.Equal(["text", "author"], notes.Fields.Select(f => f.Name));
        Assert.Null(BindingViews.Record(BindingOf(new Quotes((_, b) => b with { ChildTables = [] }).Resolve())).ChildTables);
    }

    /// <summary>
    /// A hierarchy: <c>Document</c> holds the notes list; <c>SignedDocument</c> joins its own table by a base foreign key (table per type), and
    /// <c>CounterSignedDocument</c> joins <c>SignedDocument</c>'s; each may store the inherited notes in a table of its own, owned by its own
    /// table; <c>DraftDocument</c> shares the base's table and stores them in the base's child table.
    /// </summary>
    private static (ResolvedModel Model, EntityBuilder Signed, EntityBuilder Counter, EntityBuilder Draft, EntityBuilder Document) Documents(
        bool baseStores, bool signedStores, bool counterStores, Func<EntityBuilder, EntityCopy>? counterCopy = null)
    {
        var b = new ModelBuilder(seed: 74);
        var db = b.Database("main", Dialect.PostgreSql);
        var note = b.ValueObject("DocumentNote").Attr("text", "string", a => a.Length(200).Required());
        string N() => b.NewId();
        var (docs, docId) = (N(), N());
        b.Add(new Table { Id = docs, Name = "documents", Database = db.Id, Columns = [new Column { Id = docId, Name = "id", Type = "int64", Nullable = false }], PrimaryKey = new PrimaryKey { Columns = [docId] } });
        (string Table, string Id, string Key) Level(string name, string above, string aboveId)
        {
            var (table, id, key) = (N(), N(), N());
            b.Add(new Table
            {
                Id = table, Name = name, Database = db.Id, Columns = [new Column { Id = id, Name = "document_id", Type = "int64", Nullable = false }],
                PrimaryKey = new PrimaryKey { Columns = [id] },
                ForeignKeys = [new ForeignKey { Id = key, Name = "fk_" + name, Columns = [id], ReferencesTable = above, ReferencesColumns = [aboveId], OnDelete = ReferentialAction.Cascade }],
            });
            return (table, id, key);
        }

        (string Table, string Key) Notes(string name, string owner, string ownerId)
        {
            var (table, column, key) = (N(), N(), N());
            b.Add(new Table
            {
                Id = table, Name = name, Database = db.Id,
                Columns = [new Column { Id = column, Name = "document_id", Type = "int64", Nullable = false }, new Column { Id = N(), Name = "text", Type = "string", Length = 200, Nullable = false }],
                ForeignKeys = [new ForeignKey { Id = key, Name = "fk_" + name, Columns = [column], ReferencesTable = owner, ReferencesColumns = [ownerId], OnDelete = ReferentialAction.Cascade }],
            });
            return (table, key);
        }

        var signed = Level("signed_documents", docs, docId);
        var counter = Level("counter_signed_documents", signed.Table, signed.Id);
        var docNotes = Notes("document_notes", docs, docId);
        var signedNotes = Notes("signed_document_notes", signed.Table, signed.Id);
        var counterNotes = Notes("counter_signed_document_notes", counter.Table, counter.Id);
        var document = b.Entity("Document").Key("id", "int64").Attr("notes", note, a => a.Collection());
        BindingChildTable Child((string Table, string Key) table) => new()
        {
            Id = N(), Attribute = document.AttrId("notes"), Table = table.Table, OwnerForeignKey = table.Key,
            Fields = [new BindingField { Attribute = document.AttrId("notes") + "." + note.AttrId("text"), Column = "text" }],
        };
        document.Bind(new EntityBinding { Id = "", Database = db.Id, Source = docs, Fields = [new BindingField { Attribute = document.AttrId("id"), Column = docId }], ChildTables = baseStores ? [Child(docNotes)] : [] });
        var signedDocument = b.Entity("SignedDocument").Base(document);
        signedDocument.Bind(new EntityBinding { Id = "", Database = db.Id, Source = signed.Table, BaseForeignKey = signed.Key, ChildTables = signedStores ? [Child(signedNotes)] : [] });
        var counterDocument = b.Entity("CounterSignedDocument").Base(signedDocument);
        if (counterCopy is not null)
            counterDocument.Copy(counterCopy(document));
        counterDocument.Bind(new EntityBinding { Id = "", Database = db.Id, Source = counter.Table, BaseForeignKey = counter.Key, ChildTables = counterStores ? [Child(counterNotes)] : [] });
        var draft = b.Entity("DraftDocument").Base(document);
        draft.Bind(new EntityBinding { Id = "", Database = db.Id, Source = docs, Fields = [new BindingField { Attribute = document.AttrId("id"), Column = docId }], ChildTables = [Child(docNotes)] });
        return (ResolutionKit.Resolve(b), signedDocument, counterDocument, draft, document);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, true)]
    public void An_inherited_list_is_stored_by_the_derived_binding_unless_a_base_up_the_chain_does(bool baseStores, bool signedStores, bool signedRefused, bool counterRefused)
    {
        var (model, signed, counter, draft, _) = Documents(baseStores, signedStores, counterStores: true);

        Assert.Equal(signedRefused, model.Diagnostics.Any(d => d.Rule == "MQ4137" && d.ElementId == signed.Id));
        Assert.Equal(counterRefused, model.Diagnostics.Any(d => d.Rule == "MQ4137" && d.ElementId == counter.Id));
        var derived = model.Entity("CounterSignedDocument").Bindings["main"].ChildTables[0];
        Assert.Equal(("counter_signed_document_notes", "document_id", "id"), (derived.Table!.Name, derived.Owner[0].Column.Name, derived.Owner[0].Field!.Name));
        Assert.Equal("INSERT INTO public.counter_signed_document_notes (document_id, text)\nVALUES (@ownerId, @text)", Sql(derived, "insert"));
        // One table for the hierarchy: the derived binding stores the inherited list in the base's child table, beside the base's entry.
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == draft.Id && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule == "MQ4142");
        Assert.Equal("document_notes", model.Entity("DraftDocument").Bindings["main"].ChildTables[0].Table!.Name);
    }

    [Fact]
    public void A_bases_child_table_reaches_the_derived_binding_with_the_derived_entitys_onCopy()
    {
        var (model, _, _, _, _) = Documents(baseStores: true, signedStores: false, counterStores: false,
            counterCopy: d => new EntityCopy { Attributes = [new CopyAttributeEntry { Attribute = d.AttrId("notes"), OnCopy = AttributeOnCopy.Clear }] });

        NoProblems(model);
        var counter = model.Entity("CounterSignedDocument").Bindings["main"];
        Assert.Equal([0, 1], counter.Bases.Select(l => l.ChildTables.Count));
        Assert.Equal("clear", counter.Bases[1].ChildTables[0].OnCopy);
        Assert.Equal("copy", model.Entity("SignedDocument").Bindings["main"].Bases[0].ChildTables[0].OnCopy);
        Assert.Equal("copy", model.Entity("Document").Bindings["main"].ChildTables[0].OnCopy);
        Assert.Equal("clear", BindingViews.Record(counter).Bases![1].ChildTables![0].OnCopy);
        Editor.ContractCheck.Validate(model, "bindingBaseChildRecord");
    }

    [Fact]
    public void Drift_and_map_by_name_name_the_columns_of_a_child_table_on_the_source()
    {
        var binding = new EntityBinding
        {
            Id = "b", Database = "d", Source = "t",
            ChildTables = [new BindingChildTable { Id = "c", Attribute = "a", Table = "t", OwnerForeignKey = "fk", OrderColumn = "position", Fields = [new BindingField { Attribute = "a", Column = "value" }], Columns = [new BindingColumn { Column = "id", Status = BindingColumnStatus.Database }] }],
        };
        var table = new Table { Id = "t", Name = "t", Database = "d", ForeignKeys = [new ForeignKey { Id = "fk", Columns = ["owner"], ReferencesTable = "o", ReferencesColumns = ["id"] }] };

        Assert.Equal(["value", "id", "position", "owner"], BindingDrift.ChildColumnsOf(binding, table));
        Assert.Empty(BindingDrift.ChildColumnsOf(binding, table with { Id = "other" }));
    }
}
