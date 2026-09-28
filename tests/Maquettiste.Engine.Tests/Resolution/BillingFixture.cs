using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The billing model used by the resolver's golden test: nested packages, a scalar type, two value objects, an enum, two
/// stereotypes with virtual attributes, five entities, one-to-many, composition, many-to-one, many-to-many and self relations,
/// and two databases (PostgreSQL with everything, SQL Server scoped to the Billing package with storage overrides).
/// </summary>
internal static class BillingFixture
{
    public static ModelBuilder Create()
    {
        var b = new ModelBuilder(seed: 7);
        var core = b.Package("Core");
        var billing = b.Package("Billing", core);
        var catalog = b.Package("Catalog", core);
        var email = b.ScalarType("Email", "string", core).Length(254).Pattern("^[^@]+@[^@]+$");
        var money = b.ValueObject("Money", core)
            .Attr("amount", "decimal", a => a.Precision(18).Scale(4).Required())
            .Attr("currency", "string", a => a.Length(3).Required());
        var address = b.ValueObject("Address", core)
            .Attr("street", "string", a => a.Length(200))
            .Attr("city", "string", a => a.Length(100))
            .Attr("postalCode", "string", a => a.Length(20));
        var status = b.Enum("InvoiceStatus", billing).Member("Draft", 1, "D").Member("Sent", 2, "S").Member("Paid", 3, "P");
        b.Stereotype("audited").AppliesTo("entity")
            .Attr("createdAt", "datetimeoffset", a => a.Required().Order(900))
            .Attr("updatedAt", "datetimeoffset", a => a.Order(901));
        b.Stereotype("soft-delete").AppliesTo("entity").Attr("deletedAt", "datetimeoffset", a => a.Order(902));

        var customer = b.Entity("Customer", billing).Key("id", "uuid", IdentityStrategy.UuidV7)
            .Attr("name", "string", a => a.Length(120).Required())
            .Attr("email", email, a => a.Unique())
            .Attr("billingAddress", address)
            .Stereotype("audited");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid", IdentityStrategy.UuidV7)
            .Attr("number", "string", a => a.Length(32).Required().Unique())
            .Attr("total", money)
            .Attr("status", status, a => a.Required().Default("Draft"))
            .Attr("issuedOn", "date", a => a.Required().Indexed())
            .Stereotype("audited").Stereotype("soft-delete");
        var line = b.Entity("InvoiceLine", billing).Key("id", "int64", IdentityStrategy.DatabaseIdentity)
            .Attr("description", "string")
            .Attr("quantity", "int32", a => a.Required())
            .Attr("unitPrice", money);
        var product = b.Entity("Product", catalog).Key("id", "uuid")
            .Attr("sku", "string", a => a.Length(40).Required())
            .Attr("name", "string", a => a.Length(200).Required())
            .Attr("tags", "string", a => a.Length(30).Collection())
            .AlternateKey("bySku", "sku");
        var category = b.Entity("Category", catalog).Key("id", "int32", IdentityStrategy.Sequence)
            .Attr("name", "string", a => a.Length(80).Required());

        b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, fromMin: 1, fromRole: "customer", toRole: "invoice",
            fromNavigation: "customer", toNavigation: "invoices", package: billing);
        b.Relation("contains", invoice, line, fromMax: MaxCardinality.One, fromMin: 1, fromRole: "invoice", toRole: "line",
            fromNavigation: "invoice", toNavigation: "lines", package: billing).Kind(RelationKind.Composition);
        b.Relation("refers to", line, product, toMax: MaxCardinality.One, toMin: 1, fromRole: "line", toRole: "product",
            toNavigation: "product", package: billing);
        b.Relation("is classified in", product, category, fromRole: "product", toRole: "category",
            fromNavigation: "products", toNavigation: "categories", package: catalog);
        b.Relation("has parent", category, category, toMax: MaxCardinality.One, fromRole: "child", toRole: "parent",
            fromNavigation: "children", toNavigation: "parent", package: catalog);

        b.Database("main", Dialect.PostgreSql);
        var reporting = b.Database("reporting", Dialect.SqlServer).Packages(billing);
        b.Mapping(reporting, invoice).Storage("status", StorageKind.String).Storage("total", StorageKind.Json);
        return b;
    }
}
