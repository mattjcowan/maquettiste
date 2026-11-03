using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// MQ2022 (next-phase-1-design.md section 4.5): a reference to a retired element or sub-element warns in the referring file, unless
/// the referrer is retired, the reference is membership (<c>package</c>, <c>parent</c>) or owning, or the referrer is a diagram.
/// </summary>
public sealed class ElementStatusTests
{
    private const ElementStatus Retired = ElementStatus.Retired;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reference_to_a_retired_element_is_MQ2022_unless_the_referrer_is_retired()
    {
        var b = new ModelBuilder(seed: 1);
        var status = b.Enum("InvoiceStatus").Member("Draft", 0).Member("Issued", 1);
        var draft = b.Enum("DraftStatus").Member("Open", 0);
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("status", status).Attr("draft", draft);
        var order = b.Entity("Order").Key("id", "uuid").Attr("status", status);
        var quote = b.Entity("Quote").Key("id", "uuid").Attr("status", status);

        // A retired element (Order) and a retired attribute (Quote.status) referring to the retired enum do not warn; a deprecated
        // target (DraftStatus) never does.
        var found = await Mq2022(b, (status.Id, Retired), (draft.Id, ElementStatus.Deprecated), (order.Id, Retired), (quote.AttrId("status"), Retired));

        var d = Assert.Single(found);
        Assert.Equal((DiagnosticSeverity.Warning, invoice.AttrId("status"), "/attributes/1/type/ref"), (d.Severity, d.ElementId, d.JsonPointer));
        Assert.Equal(".maquettiste/model/entities/invoice.json", d.FilePath);
        Assert.Contains("enum InvoiceStatus, which is retired", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retired_column_used_by_an_index_warns()
    {
        var b = new ModelBuilder(seed: 2);
        var main = b.Database("main", Dialect.PostgreSql);
        var ledger = b.Table("ledger", main).Column("id", "int64", nullable: false).Column("code", "string", length: 20).PrimaryKey("id").Index(false, "code");

        var d = Assert.Single(await Mq2022(b, (ledger.ColumnId("code"), Retired)));

        Assert.Equal("/indexes/0/columns/0/column", d.JsonPointer);
        Assert.Contains("column code of table ledger", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retired_top_level_element_warns_its_users()
    {
        var b = new ModelBuilder(seed: 3);
        var audited = b.Stereotype("audited");
        var customer = b.Entity("Customer").Key("id", "uuid");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Stereotype("audited");
        var vip = b.Entity("VipCustomer").Base(customer);
        var places = b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "customer", toNavigation: "invoices");

        var found = await Mq2022(b, (customer.Id, Retired), (audited.Id, Retired));

        Assert.Equal(
            [(vip.Id, "/base"), (places.EndIds[0], "/ends/0/entity"), (invoice.Id, "/stereotypes/0")],
            found.Select(d => (d.ElementId!, d.JsonPointer!)).OrderBy(p => p.Item2, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Members_of_a_retired_package_and_diagrams_do_not_warn()
    {
        var b = new ModelBuilder(seed: 4);
        var sales = b.Package("Sales");
        b.Package("Orders", sales);
        var order = b.Entity("Order", sales).Key("id", "uuid");
        var legacy = b.Entity("Legacy").Key("id", "uuid");
        var report = b.Entity("Report").Base(legacy);
        b.Add(new Diagram
        {
            Id = b.NewId(), Name = "Sales overview", Package = sales.Id,
            Members = [new DiagramMember { Element = order.Id }, new DiagramMember { Element = legacy.Id }, new DiagramMember { Element = sales.Id }],
        });

        var found = await Mq2022(b, (sales.Id, Retired), (legacy.Id, Retired));

        // The sub-package's parent, the entity's package and the diagram's members and package do not count; Report's base does.
        var d = Assert.Single(found);
        Assert.Equal((report.Id, "/base"), (d.ElementId, d.JsonPointer));
    }

    [Fact]
    public async Task An_owning_reference_to_a_retired_element_does_not_warn()
    {
        var b = new ModelBuilder(seed: 5);
        var main = b.Database("main", Dialect.PostgreSql);
        var product = b.Entity("Product").Key("id", "uuid").Attr("code", "string");
        var ledger = b.Table("ledger", main).Column("id", "int64", nullable: false).PrimaryKey("id");
        b.Add(new Migration
        {
            Id = b.NewId(), Database = main.Id, Fact = MigrationFact.Rename,
            Object = new MigrationObject { Kind = "column", Table = ledger.Id, Key = ledger.ColumnId("id"), TableName = "ledger", Name = "id" },
            From = new MigrationState { Name = "id" }, To = new MigrationState { Name = "ledger_id" },
        });
        b.Add(new Seed { Id = b.NewId(), Name = "products", Target = product.Id, Columns = [product.AttrId("code")] });

        var found = await Mq2022(b, (main.Id, Retired), (product.Id, Retired));

        // The fact's database and the seed's target are owning; the table's database is not, so the table is the last user.
        var d = Assert.Single(found);
        Assert.Equal((ledger.Id, "/database"), (d.ElementId, d.JsonPointer));
    }

    /// <summary>Validates the whole model with the statuses applied and returns its MQ2022 findings.</summary>
    private static async Task<IReadOnlyList<Diagnostic>> Mq2022(ModelBuilder b, params (string Id, ElementStatus Status)[] statuses)
    {
        var map = statuses.ToDictionary(s => s.Id, s => s.Status, StringComparer.Ordinal);
        var documents = b.BuildDocuments().Select(d => Document(WithStatus(d.Element, map), d.Path)).ToList();
        var model = ModelSnapshot.Create(documents, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
        foreach (var (id, status) in statuses)
            Assert.Equal(status == Retired, model.IsRetired(id));
        var report = await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);
        return [.. report.Diagnostics.Where(d => d.Rule == "MQ2022")];
    }

    private static Element WithStatus(Element element, Dictionary<string, ElementStatus> map)
    {
        var changed = map.TryGetValue(element.Id, out var own) ? element with { Status = own } : element;
        return changed switch
        {
            Entity e => e with { Attributes = [.. e.Attributes.Select(a => map.TryGetValue(a.Id, out var s) ? a with { Status = s } : a)] },
            Table t => t with { Columns = [.. t.Columns.Select(c => map.TryGetValue(c.Id, out var s) ? c with { Status = s } : c)] },
            _ => changed,
        };
    }

    private static ElementDocument Document(Element element, string path)
    {
        var bytes = TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
        var hash = ContentHash.Of(bytes);
        using var json = JsonDocument.Parse(bytes);
        return new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null);
    }
}
