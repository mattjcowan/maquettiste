using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>Designed and imported tables, views and sequences (SPEC Section 9).</summary>
public sealed class PhysicalFileTests
{
    [Fact]
    public void Views_take_the_body_for_the_dialect_and_sequences_pass_through()
    {
        var b = new ModelBuilder(seed: 63);
        var db = b.Database("main", Dialect.PostgreSql);
        b.Add(new View
        {
            Id = b.NewId(), Name = "active_customers", Database = db.Id,
            Body = ImmutableDictionary<string, string>.Empty.Add("*", "select 1").Add("postgresql", "select 2"),
            Columns = [new ViewColumn { Name = "n", Type = "int32", Nullable = false }],
        });
        b.Add(new Sequence { Id = b.NewId(), Name = "invoice_numbers", Database = db.Id, Type = "int32", Start = 1000, Increment = 5 });
        var rdb = ResolutionKit.Resolve(b).Db("main");
        var view = Assert.Single(rdb.Views);
        Assert.Equal("select 2", view.Body);
        Assert.Equal("public", view.Schema);
        Assert.Equal("integer", view.Columns.Single().NativeType);
        var sequence = Assert.Single(rdb.Sequences);
        Assert.Equal((1000L, 5L, "integer"), (sequence.Start, sequence.Increment, sequence.NativeType));
    }

    [Fact]
    public void Tables_views_and_sequences_carry_the_annotations_of_their_own_files_only()
    {
        var b = new ModelBuilder(seed: 66);
        var stereotype = b.Stereotype("ledger").AppliesTo("entity", "table", "view", "sequence").DefaultProperty("retentionDays", 30).DefaultProperty("owner", "finance");
        var receivables = b.Category("Receivables");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("number", "string").Stereotype("ledger").Tag("billing").Category(receivables);
        var payment = b.Entity("Payment").Key("id", "uuid");
        var db = b.Database("main", Dialect.PostgreSql);
        var properties = ImmutableDictionary<string, JsonElement>.Empty.Add("owner", JsonDocument.Parse("\"treasury\"").RootElement);
        var hints = new Dictionary<string, GenerationHints> { ["sql-ddl"] = new() { Rename = "ledger_entries" } };
        var tableId = b.NewId();
        b.Add(new Table
        {
            Id = tableId, Name = "ledger", Database = db.Id, DisplayName = "Ledger", PluralName = "Ledgers",
            Description = new Description { Text = "Every posting." }, Stereotypes = ["ledger"], Tags = ["finance"], Category = receivables,
            Properties = properties, Generation = hints,
            Columns = [new Column { Id = b.NewId(), Name = "id", Type = "int64", Nullable = false }],
        });
        b.Add(new View
        {
            Id = b.NewId(), Name = "open_ledger", Database = db.Id, Body = ImmutableDictionary<string, string>.Empty.Add("*", "select 1"),
            DisplayName = "Open ledger", Stereotypes = ["ledger"], Tags = ["finance"], Comment = "Open postings.",
        });
        b.Add(new Sequence { Id = b.NewId(), Name = "ledger_seq", Database = db.Id, Description = new Description { Text = "Posting numbers." }, Properties = properties });

        var rdb = ResolutionKit.Resolve(b).Db("main");

        var ledger = rdb.Table("ledger");
        Assert.Equal(("Ledger", "Ledgers", "Every posting."), (ledger.DisplayName, ledger.PluralName, ledger.Description));
        Assert.Equal(["ledger"], ledger.Stereotypes.Select(s => s.Key));
        Assert.True(ledger.HasStereotype("ledger") && ledger.HasTag("finance"));
        Assert.Equal("Receivables", ledger.Category!.Path);
        Assert.Equal(["owner", "retentionDays"], ledger.Properties.Keys);
        Assert.Equal("treasury", ledger.Properties["owner"]); // the file's own value wins over the stereotype default
        Assert.Equal(30L, Convert.ToInt64(ledger.Properties["retentionDays"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("ledger_entries", ledger.Generation["sql-ddl"].Rename);
        Assert.Contains("e:" + stereotype.Id, ledger.Dependencies);

        var view = Assert.Single(rdb.Views);
        Assert.Equal(("Open ledger", "Open postings."), (view.DisplayName, view.Comment));
        Assert.Equal(["ledger"], view.Stereotypes.Select(s => s.Key));
        Assert.Equal(["finance"], view.Tags);
        var sequence = Assert.Single(rdb.Sequences);
        Assert.Equal("Posting numbers.", sequence.Description);
        Assert.Equal("treasury", sequence.Properties["owner"]);
    }

    [Fact]
    public void Columns_databases_and_schemas_carry_the_annotations_of_their_own_entries_only()
    {
        var b = new ModelBuilder(seed: 72);
        b.Stereotype("ledger").AppliesTo("entity", "column", "database").DefaultProperty("retentionDays", 30);
        var receivables = b.Category("Receivables");
        var invoice = b.Entity("Invoice").Key("id", "uuid")
            .Attr("number", "string", a => a.Length(20))
            .Attr("lines", "string", a => a.Collection())
            .Stereotype("ledger");
        var properties = ImmutableDictionary<string, JsonElement>.Empty.Add("masking", JsonDocument.Parse("\"partial\"").RootElement);
        var dbId = b.NewId();
        var salesId = b.NewId();
        b.Add(new Database
        {
            Id = dbId, Name = "main", Dialect = Dialect.PostgreSql, DefaultSchema = "sales", DisplayName = "Main store",
            Description = new Description { Text = "The system of record." }, Stereotypes = ["ledger"], Tags = ["primary"], Category = receivables,
            Schemas = [new DbSchema { Id = salesId, Name = "sales", DisplayName = "Sales", Tags = ["finance"], Description = new Description { Text = "Orders." } }],
        });
        var uniqueId = b.NewId();
        var checkId = b.NewId();
        var idColumn = b.NewId();
        var noteColumn = b.NewId();
        b.Add(new Table
        {
            Id = b.NewId(), Name = "ledger", Database = dbId, PrimaryKey = new PrimaryKey { Columns = [idColumn], Clustered = false },
            Columns =
            [
                new Column { Id = idColumn, Name = "id", Type = "int64", Nullable = false, DisplayName = "Posting", Stereotypes = ["ledger"], Properties = properties },
                new Column { Id = noteColumn, Name = "note", Type = "string", Collation = "C" },
            ],
            Uniques = [new UniqueConstraint { Id = uniqueId, Columns = [noteColumn] }],
            Checks = [new CheckConstraint { Id = checkId, Expression = ImmutableDictionary<string, string>.Empty.Add("*", "id > 0") }],
        });

        var rdb = ResolutionKit.Resolve(b).Db("main");

        // The database and its declared schema: their own annotations; the stereotype's default properties merged.
        Assert.Equal(("Main store", "The system of record.", "Receivables"), (rdb.DisplayName, rdb.Description, rdb.Category!.Path));
        Assert.True(rdb.HasStereotype("ledger") && rdb.HasTag("primary"));
        Assert.Equal(30L, Convert.ToInt64(rdb.Properties["retentionDays"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((63, "reserved"), (rdb.MaxIdentifierLength, rdb.Quoting));
        var sales = Assert.Single(rdb.Schemas);
        Assert.Equal((salesId, "Sales", "Orders.", true, true), (sales.Id, sales.DisplayName, sales.Description, sales.IsDefault, sales.IsDeclared));
        Assert.Equal(["finance"], sales.Tags);

        // A designed column's own entry; a sibling without annotations has none.
        var ledger = rdb.Table("ledger");
        var id = ledger.Column("id");
        Assert.Equal("Posting", id.DisplayName);
        Assert.Equal(["ledger"], id.Stereotypes.Select(s => s.Key));
        Assert.Equal(["masking", "retentionDays"], id.Properties.Keys);
        var note = ledger.Column("note");
        Assert.Equal(((string?)null, 0, 0, "C"), (note.DisplayName, note.Stereotypes.Count, note.Properties.Count, note.Collation));
        Assert.False(ledger.PrimaryKey!.Clustered);
        Assert.Equal(uniqueId, Assert.Single(ledger.Uniques).Id);
        Assert.Equal(checkId, Assert.Single(ledger.Checks).Id);

        // A sequence names its database.
        Assert.All(rdb.Sequences, s => Assert.Same(rdb, s.Database));
    }

    /// <summary>A reference type with a string code of <paramref name="length"/> (next-phase-2-design.md section 3.2's tests).</summary>
    private static ReferenceType CodeType(ModelBuilder b, string name, int? length, string codeType = "string",
        IReadOnlyDictionary<string, StorageChoice>? storage = null, IReadOnlyList<ReferenceTypeBinding>? bindings = null)
    {
        var type = new ReferenceType
        {
            Id = b.NewId(), Name = name,
            Code = new ReferenceCode { Id = b.NewId(), Type = codeType, Length = length },
            Label = new ReferenceLabel { Id = b.NewId(), Length = 40 },
            Storage = storage ?? ImmutableDictionary<string, StorageChoice>.Empty,
            Bindings = bindings ?? [],
        };
        b.Add(type);
        return type;
    }

    /// <summary>An attribute type by id, for a type the builders do not make (a reference type).</summary>
    private sealed record TypeId(string Id) : ITypeSource;

    [Fact]
    public void A_designed_column_bound_to_a_reference_type_attribute_holds_no_codes_for_the_database()
    {
        // 0.28.0: the column's bound type is the reference type, as information; binding never changes storage, so the column keeps its
        // physical keyword and is no reference column for packs: no reference type, strategy or binding.
        var b = new ModelBuilder(seed: 90);
        var db = b.Database("main", Dialect.PostgreSql);
        var states = b.Table("invoice_states", db).Column("code", "string", nullable: false, length: 1).Column("label", "string", nullable: false, length: 40)
            .PrimaryKey("code");
        var state = CodeType(b, "InvoiceState", 1, storage: new Dictionary<string, StorageChoice> { ["*"] = new() { Strategy = "lookup-table" } });
        var kind = CodeType(b, "EventKind", 8, bindings:
        [
            new ReferenceTypeBinding
            {
                Id = b.NewId(), Database = db.Id, Table = states.Id,
                Fields = [new BindingField { Attribute = "code", Column = states.ColumnId("code") }, new BindingField { Attribute = "label", Column = states.ColumnId("label") }],
            },
        ]);
        b.Table("invoice_events", db).Column("id", "uuid", nullable: false).Column("status", "string", nullable: false, length: 1)
            .Column("kind", "string", length: 1).PrimaryKey("id")
            .BindTyped("InvoiceEvent", ("status", state.Id), ("kind", kind.Id));

        var table = ResolutionKit.Resolve(b).Db("main").Table("invoice_events");

        var status = table.Column("status");
        Assert.Equal(("string", "varchar(1)", 1), (status.Type, status.NativeType, status.Length));
        Assert.Equal(("reference", "InvoiceState", state.Id), (status.BoundType!.Kind, status.BoundType.Name, status.BoundType.ReferenceType!.Id));
        var bound = table.Column("kind");
        Assert.Equal(kind.Id, bound.BoundType!.ReferenceType!.Id);
        Assert.Null(table.Column("id").BoundType);
    }

    [Fact]
    public void A_bound_column_keeps_its_own_facets_and_native_type()
    {
        // 0.28.0: a table states its own shape, so a column's bound type fills no facet and no native type: a facet the column leaves out
        // takes the conventions' default, and a scalar type's native type does not reach it.
        var b = new ModelBuilder(seed: 91);
        var db = b.Database("main", Dialect.PostgreSql);
        var state = CodeType(b, "InvoiceState", 2);
        var status = b.Enum("Status").Member("Draft", 0, "draft").Member("Issued", 1, "issued").Member("Void", 2);
        var money = b.ScalarType("Money", "decimal").Decimal(12, 2);
        var email = b.ScalarType("EmailAddress", "string").Length(320).NativeType("postgresql", "citext");
        b.Table("invoice_events", db).Column("id", "uuid", nullable: false)
            .Column("state", "string").Column("status", "string").Column("amount", "decimal").Column("email", "string").PrimaryKey("id")
            .BindTyped("InvoiceEvent", ("state", state.Id), ("status", status.Id), ("amount", money.Id), ("email", email.Id));
        var plain = b.Table("plain_events", db).Column("id", "uuid", nullable: false)
            .Column("state", "string").Column("status", "string").Column("amount", "decimal").Column("email", "string").PrimaryKey("id");

        var main = ResolutionKit.Resolve(b).Db("main");
        var table = main.Table("invoice_events");
        var untyped = main.Table("plain_events");

        foreach (var name in new[] { "state", "status", "amount", "email" })
        {
            var (typed, own) = (table.Column(name), untyped.Column(name));
            Assert.NotNull(typed.BoundType);
            Assert.Null(own.BoundType);
            Assert.Equal((own.Length, own.Precision, own.Scale, own.NativeType), (typed.Length, typed.Precision, typed.Scale, typed.NativeType));
        }

        Assert.Equal(("enum", "scalar", "scalar"), (table.Column("status").BoundType!.Kind, table.Column("amount").BoundType!.Kind, table.Column("email").BoundType!.Kind));
        Assert.NotEqual("citext", table.Column("email").NativeType);
    }

    [Fact]
    public void A_column_whose_bindings_disagree_has_no_bound_type()
    {
        // 0.28.0: the bound type is set only when every binding field that maps the column maps an attribute of that one type.
        var b = new ModelBuilder(seed: 93);
        var state = CodeType(b, "InvoiceState", 1);
        var status = b.Enum("Status").Member("Draft", 0, "D").Member("Issued", 1, "I");
        var db = b.Database("main", Dialect.PostgreSql);
        var events = b.Table("invoice_events", db).Column("id", "uuid", nullable: false).Column("state", "string", length: 1).Column("kind", "string", length: 1)
            .PrimaryKey("id");
        events.BindTyped("InvoiceEvent", ("state", state.Id), ("kind", status.Id));
        events.BindTyped("EventView", ("state", status.Id), ("kind", status.Id));

        var table = ResolutionKit.Resolve(b).Db("main").Table("invoice_events");

        Assert.Null(table.Column("state").BoundType);
        Assert.Equal(status.Id, table.Column("kind").BoundType!.Enum!.Id);
    }
}
