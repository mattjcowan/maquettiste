using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Support;

/// <summary>Tests of <see cref="ModelBuilder"/> (in <c>tests/Maquettiste.Testing</c>, shared by every test project).</summary>
public sealed class ModelBuilderTests
{
    [Fact]
    public void Design_sketch_builds_a_valid_model()
    {
        var b = new ModelBuilder(seed: 1);
        var billing = b.Package("Billing");
        var customer = b.Entity("Customer", billing).Key("id", "uuid", IdentityStrategy.UuidV7).Attr("name", "string", a => a.Length(120).Required());
        var invoice = b.Entity("Invoice", billing).Stereotype("audited").Attr("number", "string", a => a.Length(32).Unique());
        b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "customer", toNavigation: "invoices");
        b.Database("main", Dialect.PostgreSql);

        var model = b.Build();

        Assert.Equal(5, model.Documents.Count);
        var relation = model.All<Relation>().Single();
        Assert.Equal(customer.Id, relation.Ends[0].Entity);
        Assert.Equal("customer", relation.Ends[0].Role);
        Assert.Equal(MaxCardinality.One, relation.Ends[0].Max);
        Assert.Equal("invoice", relation.Ends[1].Role);
        Assert.Equal("invoices", relation.Ends[1].Navigation);
        Assert.Equal(["audited"], model.Get<Entity>(invoice.Id)!.Stereotypes);
        Assert.Equal(IdentityStrategy.UuidV7, model.Get<Entity>(customer.Id)!.Key!.Strategy);
        Assert.True(model.Get<Entity>(customer.Id)!.Attributes[0].Required);

        foreach (var document in model.Documents)
            Assert.Empty(TestServices.Schemas.Evaluate(KindInfo.Get(document.Element.Kind).SchemaFile, document.Json, document.Path));
    }

    [Fact]
    public void Builds_every_kind_the_phase_1_model_has()
    {
        var b = new ModelBuilder(seed: 2);
        var core = b.Package("Core");
        var billing = b.Package("Billing", core);
        var email = b.ScalarType("Email", "string", core).Length(254).Pattern("^[^@]+@[^@]+$");
        var money = b.ValueObject("Money", core).Attr("amount", "decimal", a => a.Precision(18).Scale(2)).Attr("currency", "string", a => a.Length(3));
        var status = b.Enum("Status", billing).Member("Open", 1, "O").Member("Closed", 2, "C");
        var receivables = b.Category("Receivables");
        b.Category("Overdue", receivables);
        b.Tags(strict: true, "billing", "external");
        b.Stereotype("audited").AppliesTo("entity").Attr("createdAt", "datetimeoffset", a => a.Required()).DefaultProperty("retention", 7);
        var party = b.Entity("Party", core).Abstract().Key("id", "uuid");
        var customer = b.Entity("Customer", billing).Base(party).Attr("email", email).Tag("billing").Category(receivables);
        var invoice = b.Entity("Invoice", billing).Key("id", "ulid", IdentityStrategy.Ulid).Attr("total", money).Attr("status", status)
            .Attr("number", "string", a => a.Length(32)).AlternateKey("byNumber", "number").Property("owner", "ar");
        var line = b.Entity("InvoiceLine", billing).Key("id", "uuid").Attr("position", "int32");
        b.Relation("contains", invoice, line, fromMax: MaxCardinality.One, fromMin: 1, toNavigation: "lines").Kind(RelationKind.Composition);
        var tagged = b.Relation("is tagged with", invoice, customer).Attr("since", "date", a => a.Required()).AllowDuplicates();
        var main = b.Database("main", Dialect.PostgreSql).DefaultSchema("app");
        main.Schema("app");
        var legacy = b.Table("legacy_customer", main).Column("id", "int64", nullable: false).Column("email", "string", length: 254).PrimaryKey("id").Index(true, "email");
        b.Table("invoices", main).OverlayFor(invoice).Overlay(invoice.AttrId("number"), nativeType: "citext");
        b.Mapping(main, party).Inheritance(InheritanceStrategy.Tph).Discriminator("party");
        b.Mapping(main, customer).Table(legacy);
        b.Mapping(main, invoice).Storage("status", StorageKind.String);
        b.Mapping(main, tagged).Shape(RelationShape.Promoted);
        b.Settings(s => s with { Name = "Billing", Outputs = new OutputSettings { Allow = [new OutputRoot { Path = "db" }] } });

        var model = b.Build();

        foreach (var document in model.Documents)
            Assert.Empty(TestServices.Schemas.Evaluate(KindInfo.Get(document.Element.Kind).SchemaFile, document.Json, document.Path));
        Assert.Equal("Billing", model.Settings.Name);
        Assert.NotNull(model.Tags);
        Assert.Equal(2, model.Categories!.Categories.Count);
        Assert.Equal(4, model.All<Mapping>().Count);
        Assert.Contains(model.Documents, d => d.Path == ".maquettiste/model/databases/main/tables/legacy-customer.json");
        Assert.Contains(model.Documents, d => d.Path == ".maquettiste/model/vocabularies/tags.json");
        Assert.Contains(model.Documents, d => d.Path == ".maquettiste/model/vocabularies/categories.json");
        Assert.Contains(model.Documents, d => d.Path == ".maquettiste/model/types/email.json");
    }

    [Fact]
    public void Build_is_deterministic_for_a_seed()
    {
        static ModelSnapshot Make()
        {
            var b = new ModelBuilder(seed: 9);
            var p = b.Package("Sales");
            b.Entity("Order", p).Key("id", "uuid").Attr("placedAt", "datetimeoffset");
            return b.Build();
        }

        var a = Make();
        var c = Make();

        Assert.Equal(a.Documents.Select(d => (d.Path, d.Hash)), c.Documents.Select(d => (d.Path, d.Hash)));
        Assert.Equal(a.SettingsHash, c.SettingsHash);
    }

    [Fact]
    public void Name_collisions_in_a_folder_get_an_id_suffix()
    {
        var b = new ModelBuilder(seed: 3);
        var first = b.Entity("Invoice");
        var second = b.Entity("Invoice", b.Package("Other"));

        var paths = b.Build().Documents.Where(d => d.Element is Entity).Select(d => d.Path).ToList();

        Assert.Contains(".maquettiste/model/entities/invoice.json", paths);
        Assert.Contains(".maquettiste/model/entities/invoice-" + second.Id[^6..].ToLowerInvariant() + ".json", paths);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData("InvoiceLine", "invoice-line")]
    [InlineData("is member of", "is-member-of")]
    [InlineData("HTTPServer2Id", "http-server2-id")]
    [InlineData("main", "main")]
    [InlineData("legacy_customer", "legacy-customer")]
    public void Kebab_cases_file_names(string name, string expected) => Assert.Equal(expected, ModelBuilder.Kebab(name));

    [Fact]
    public async Task Writes_canonical_files_that_read_back()
    {
        using var repo = new TempRepo();
        var b = new ModelBuilder(seed: 4);
        var p = b.Package("Sales");
        b.Entity("Order", p).Key("id", "uuid").Attr("total", "decimal", a => a.Precision(10).Scale(2));

        await b.WriteToAsync(repo.ModelRoot, TestContext.Current.CancellationToken);

        Assert.Equal(
            [".maquettiste/maquettiste.json", ".maquettiste/model/entities/order.json", ".maquettiste/model/packages/sales.json"],
            repo.ListFiles());
        foreach (var path in repo.ListFiles().Where(f => f.Contains("/model/", StringComparison.Ordinal)))
        {
            var bytes = File.ReadAllBytes(repo.PathOf(path));
            var element = ElementReader.ReadElement(bytes);
            Assert.True(TestServices.Json.IsCanonical(bytes, KindInfo.Get(element.Kind).SchemaFile, path), path);
        }

        var settings = JsonNode.Parse(File.ReadAllBytes(repo.PathOf(".maquettiste/maquettiste.json")))!;
        Assert.Equal(".schema/v1/maquettiste.json", settings["$schema"]!.GetValue<string>());
        Assert.Equal(1, settings["formatVersion"]!.GetValue<int>());
    }
}
