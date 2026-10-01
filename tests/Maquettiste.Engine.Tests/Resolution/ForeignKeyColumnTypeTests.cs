using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The physical side is free (engine-design.md section 7.3): a column's type, length and native type may differ from its attribute's, which
/// keep their own for validation; only a foreign key column must match the column it references, checked over the resolved columns (MQ4005).
/// </summary>
public sealed class ForeignKeyColumnTypeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ValidationReport> Validate(ModelSnapshot model) =>
        ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);

    private static IReadOnlyList<Diagnostic> Mismatches(ResolvedModel model) => [.. model.Diagnostics.Where(d => d.Rule == "MQ4005")];

    /// <summary>Customer (string(26) key) places Invoices: invoices.customer_id is the foreign key column.</summary>
    private static (ModelBuilder B, EntityBuilder Customer, EntityBuilder Invoice, RelationBuilder Places, DatabaseBuilder Db) Model(int seed)
    {
        var b = new ModelBuilder(seed);
        var customer = b.Entity("Customer").Key("id", "string", configure: a => a.Length(26)).Attr("name", "string", a => a.Length(255));
        var invoice = b.Entity("Invoice").Key("id", "int64").Attr("note", "string", a => a.Length(128));
        var places = b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "invoice");
        var db = b.Database("main", Dialect.PostgreSql);
        return (b, customer, invoice, places, db);
    }

    private static string ForeignKeyColumnKey(EntityBuilder principal, RelationBuilder relation) => relation.EndIds[0] + "." + principal.AttrId("id");

    [Fact]
    public async Task An_overlay_may_change_a_columns_type_or_length_and_the_attribute_keeps_its_own()
    {
        var (b, customer, invoice, _, db) = Model(90);
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = customer.AttrId("name"), Type = "text" }],
        });
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = invoice.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = invoice.AttrId("note"), Length = 2056 }],
        });
        var snapshot = b.Build();
        var model = ResolutionKit.Resolve(snapshot);
        Assert.Empty(model.Diagnostics);
        Assert.Empty((await Validate(snapshot)).Diagnostics);

        var name = model.Db("main").Table("customers").Column("name");
        Assert.Equal("text", name.Type);
        Assert.Equal("text", name.NativeType);
        Assert.Equal(255, model.Entity("Customer").Attributes.Single(a => a.Name == "name").Length);
        Assert.Equal(255, name.Attribute!.Length);

        var note = model.Db("main").Table("invoices").Column("note");
        Assert.Equal(2056, note.Length);
        Assert.Equal("varchar(2056)", note.NativeType);
        Assert.Equal(128, model.Entity("Invoice").Attributes.Single(a => a.Name == "note").Length);
        Assert.Equal(128, note.Attribute!.Length);
    }

    [Fact]
    public async Task A_synthesized_foreign_key_column_follows_the_referenced_columns_pinned_native_type()
    {
        var (b, customer, _, _, db) = Model(91);
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = customer.AttrId("id"), NativeType = "char(26)" }],
        });
        var snapshot = b.Build();
        var model = ResolutionKit.Resolve(snapshot);
        Assert.Empty(Mismatches(model));
        var column = model.Db("main").Table("invoices").Column("customer_id");
        Assert.Equal(("string", 26, "char(26)"), (column.Type, column.Length, column.NativeType));
        Assert.Empty((await Validate(snapshot)).Diagnostics);
    }

    [Fact]
    public void An_overlay_that_pins_a_foreign_key_column_to_another_type_is_MQ4005_on_that_overlay_entry()
    {
        var (b, customer, invoice, places, db) = Model(92);
        var overlayId = b.NewId();
        b.Add(new Table
        {
            Id = overlayId, Database = db.Id, Origin = TableOrigin.Synthesized, Entity = invoice.Id,
            Columns =
            [
                new Column { Id = b.NewId(), Attribute = invoice.AttrId("note"), Length = 4000 },
                new Column { Id = b.NewId(), Attribute = ForeignKeyColumnKey(customer, places), Length = 40 },
            ],
        });
        var model = ResolutionKit.Resolve(b);
        var diagnostic = Assert.Single(Mismatches(model));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(overlayId, diagnostic.ElementId);
        Assert.Equal("/columns/1", diagnostic.JsonPointer);
        Assert.Equal(
            "Foreign key column 'invoices.customer_id' is string(40) (varchar(40)) but references 'customers.id', which is string(26) (varchar(26)) (database 'main').",
            diagnostic.Message);
    }

    [Fact]
    public void An_overlay_that_pins_a_foreign_key_column_to_the_referenced_type_is_fine()
    {
        var (b, customer, invoice, places, db) = Model(93);
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = invoice.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = ForeignKeyColumnKey(customer, places), Name = "client_ref", NativeType = "varchar(26)" }],
        });
        var model = ResolutionKit.Resolve(b);
        Assert.Empty(Mismatches(model));
        Assert.Equal("varchar(26)", model.Db("main").Table("invoices").Column("client_ref").NativeType);
    }

    [Fact]
    public void A_designed_table_whose_key_column_differs_from_the_synthesized_table_it_references_is_MQ4005_on_its_column()
    {
        var (b, customer, _, _, db) = Model(94);
        var tableId = b.NewId();
        var id = b.NewId();
        var customerRef = b.NewId();
        b.Add(new Table
        {
            Id = tableId, Name = "visits", Database = db.Id, Origin = TableOrigin.Designed,
            Columns =
            [
                new Column { Id = id, Name = "id", Type = "int64", Nullable = false },
                new Column { Id = customerRef, Name = "customer", Type = "string", Length = 30 },
            ],
            PrimaryKey = new PrimaryKey { Columns = [id] },
            ForeignKeys = [new ForeignKey { Id = b.NewId(), Columns = [customerRef], ReferencesTable = customer.Id + "@" + db.Id }],
        });
        var model = ResolutionKit.Resolve(b);
        var diagnostic = Assert.Single(Mismatches(model));
        Assert.Equal(tableId, diagnostic.ElementId);
        Assert.Equal("/columns/1", diagnostic.JsonPointer);
        Assert.Equal(
            "Foreign key column 'visits.customer' is string(30) (varchar(30)) but references 'customers.id', which is string(26) (varchar(26)) (database 'main').",
            diagnostic.Message);
    }

    [Fact]
    public async Task Two_designed_tables_are_reported_once_whichever_check_sees_the_mismatch()
    {
        var b = new ModelBuilder(seed: 95);
        var db = b.Database("main", Dialect.PostgreSql);
        var parentId = b.NewId();
        var parentKey = b.NewId();
        b.Add(new Table
        {
            Id = parentId, Name = "parent", Database = db.Id, Origin = TableOrigin.Designed,
            Columns = [new Column { Id = parentKey, Name = "code", Type = "string", Length = 10, Nullable = false }],
            PrimaryKey = new PrimaryKey { Columns = [parentKey] },
        });
        var typeChild = b.NewId();
        var typeRef = b.NewId();
        b.Add(new Table
        {
            Id = typeChild, Name = "by_type", Database = db.Id, Origin = TableOrigin.Designed,
            Columns = [new Column { Id = typeRef, Name = "parent_code", Type = "int32" }],
            ForeignKeys = [new ForeignKey { Id = b.NewId(), Columns = [typeRef], ReferencesTable = parentId }],
        });
        var lengthChild = b.NewId();
        var lengthRef = b.NewId();
        b.Add(new Table
        {
            Id = lengthChild, Name = "by_length", Database = db.Id, Origin = TableOrigin.Designed,
            Columns = [new Column { Id = lengthRef, Name = "parent_code", Type = "string", Length = 12 }],
            ForeignKeys = [new ForeignKey { Id = b.NewId(), Columns = [lengthRef], ReferencesTable = parentId }],
        });
        var snapshot = b.Build();
        var validation = (await Validate(snapshot)).Diagnostics.Where(d => d.Rule == "MQ4005").ToList();
        var resolved = Mismatches(ResolutionKit.Resolve(snapshot));

        // The type mismatch is the file-level check's (both files declare both types); the length mismatch only the resolved check sees.
        var byType = Assert.Single(validation);
        Assert.Equal(snapshot.GetDocument(typeChild)!.Path, byType.FilePath);
        Assert.Equal("Foreign key column 'parent_code' is int32 but references 'code', which is string.", byType.Message);
        var byLength = Assert.Single(resolved);
        Assert.Equal(lengthChild, byLength.ElementId);
        Assert.Equal("/columns/0", byLength.JsonPointer);
        Assert.Contains("'by_length.parent_code' is string(12)", byLength.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tpt_derived_key_follows_the_base_tables_pinned_native_type()
    {
        var b = new ModelBuilder(seed: 96);
        var party = b.Entity("Party").Key("id", "string", configure: a => a.Length(26)).Attr("name", "string");
        b.Entity("Person").Base(party).Attr("born", "date");
        var db = b.Database("main", Dialect.PostgreSql);
        b.Mapping(db, party).Inheritance(InheritanceStrategy.Tpt);
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = party.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = party.AttrId("id"), NativeType = "char(26)" }],
        });
        var model = ResolutionKit.Resolve(b);
        Assert.Empty(Mismatches(model));
        Assert.Equal("char(26)", model.Db("main").Table("people").Column("id").NativeType);
    }
}
