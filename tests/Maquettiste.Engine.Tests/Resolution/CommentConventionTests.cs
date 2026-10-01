using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The <c>comments</c> convention: an explicit comment always wins; with <c>descriptions</c> (the default) a column takes its own
/// description, else its attribute's, and a table its own description, else the description of what it stores; <c>none</c> keeps only
/// explicit comments. A database's entry under <c>databases</c> overrides the project's.
/// </summary>
public sealed class CommentConventionTests
{
    private sealed record Fixture(ModelBuilder Builder);

    private static Fixture Build()
    {
        var b = new ModelBuilder(seed: 90);
        var customer = b.Entity("Customer").Description("Someone we bill.").Key("id", "uuid")
            .Attr("name", "string", a => a.Description("The legal name."))
            .Attr("email", "string")
            .Attr("code", "string", a => a.Description("The attribute's text."))
            .Attr("phone", "string", a => a.Description("The attribute's phone text."))
            .Attr("nicknames", "string", a => a.Collection().Description("Names the customer goes by."));
        var invoice = b.Entity("Invoice").Description("A bill.").Key("id", "uuid").Attr("number", "string");
        var payment = b.Entity("Payment").Description("Money received.").Key("id", "uuid");
        var product = b.Entity("Product").Key("id", "uuid");
        b.Relation("Customer buys product", customer, product).Description("What a customer has bought.");
        var db = b.Database("main", Dialect.PostgreSql);
        b.Database("archive", Dialect.PostgreSql);
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id,
            Columns =
            [
                new Column { Id = b.NewId(), Attribute = customer.AttrId("code"), Description = new Description { Text = "The column's own text." } },
                new Column { Id = b.NewId(), Attribute = customer.AttrId("phone"), Comment = "Explicit.", Description = new Description { Text = "Ignored." } },
            ],
        });
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = invoice.Id,
            Description = new Description { Text = "  The table's own text.\n" },
        });
        b.Add(new Table { Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = payment.Id, Comment = "Explicit table comment." });
        b.Add(new Table
        {
            Id = b.NewId(), Name = "ledger", Database = db.Id, Origin = TableOrigin.Designed, Description = new Description { Text = "Every posting." },
            Columns =
            [
                new Column { Id = b.NewId(), Name = "id", Type = "int64", Nullable = false, Description = new Description { Text = "The posting number." } },
                new Column { Id = b.NewId(), Name = "amount", Type = "decimal" },
            ],
        });
        return new Fixture(b);
    }

    [Fact]
    public void Descriptions_is_the_default_and_fills_comments_an_explicit_comment_does_not_set()
    {
        var main = ResolutionKit.Resolve(Build().Builder).Db("main");

        var customers = main.Table("customers");
        Assert.Equal("Someone we bill.", customers.Comment);                       // the entity's description
        Assert.Null(customers.Column("id").Comment);                                 // no description anywhere
        Assert.Equal("The legal name.", customers.Column("name").Comment);          // the attribute's description
        Assert.Null(customers.Column("email").Comment);
        Assert.Equal("The column's own text.", customers.Column("code").Comment);   // the overlay entry's own description first
        Assert.Equal("Explicit.", customers.Column("phone").Comment);               // an explicit comment wins

        Assert.Equal("The table's own text.", main.Table("invoices").Comment);      // the overlay's own description, trimmed
        Assert.Equal("Explicit table comment.", main.Table("payments").Comment);    // explicit wins over the entity's description
        Assert.Null(main.Table("products").Comment);

        Assert.Equal("Names the customer goes by.", main.Tables.Single(t => t.Attribute is not null).Comment); // a child table: its attribute's
        Assert.Equal("What a customer has bought.", main.Tables.Single(t => t.IsJunction).Comment);           // a junction: its relation's

        var ledger = main.Table("ledger");                                           // a designed table: its own descriptions
        Assert.Equal("Every posting.", ledger.Comment);
        Assert.Equal("The posting number.", ledger.Column("id").Comment);
        Assert.Null(ledger.Column("amount").Comment);

        // Descriptions stay what the files say: the convention fills comments only.
        Assert.Null(customers.Description);
        Assert.Null(customers.Column("name").Description);
    }

    [Fact]
    public void None_keeps_only_explicit_comments_and_a_database_entry_overrides_the_project()
    {
        var f = Build();
        f.Builder.Settings(s => s with
        {
            Conventions = new Conventions { Comments = CommentSource.None },
            Databases = ImmutableDictionary<string, Conventions>.Empty.Add("archive", new Conventions { Comments = CommentSource.Descriptions }),
        });
        var model = ResolutionKit.Resolve(f.Builder);

        var main = model.Db("main");
        var customers = main.Table("customers");
        Assert.Null(customers.Comment);
        Assert.Null(customers.Column("name").Comment);
        Assert.Null(customers.Column("code").Comment);
        Assert.Equal("Explicit.", customers.Column("phone").Comment);
        Assert.Null(main.Table("invoices").Comment);
        Assert.Equal("Explicit table comment.", main.Table("payments").Comment);
        Assert.Null(main.Tables.Single(t => t.IsJunction).Comment);
        Assert.Null(main.Tables.Single(t => t.Attribute is not null).Comment);
        Assert.Null(main.Table("ledger").Comment);

        // The archive database turns descriptions back on for itself; it has no overlays, so the conceptual descriptions apply.
        var archive = model.Db("archive").Table("customers");
        Assert.Equal("Someone we bill.", archive.Comment);
        Assert.Equal("The legal name.", archive.Column("name").Comment);
        Assert.Equal("The attribute's text.", archive.Column("code").Comment);
        Assert.Equal("The attribute's phone text.", archive.Column("phone").Comment);
    }

    [Fact]
    public void The_settings_round_trip_the_comments_convention()
    {
        var json = TestServices.Json.Serialize(new ProjectSettings
        {
            FormatVersion = 1,
            Conventions = new Conventions { Comments = CommentSource.None },
        }, "maquettiste.json", ".maquettiste/maquettiste.json");
        var text = System.Text.Encoding.UTF8.GetString(json);
        Assert.Contains("\"comments\": \"none\"", text, StringComparison.Ordinal);
    }
}
