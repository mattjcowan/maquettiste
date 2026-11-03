using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Package layering (next-phase-1-design.md section 4.3): a package that it or a parent declares <c>dependsOn</c> for is checked, and
/// its elements may reference only its own subtree and the packages (with their subtrees) that <c>dependsOn</c> lists over it and
/// its parents (MQ2020); <c>dependsOn</c> may not name the package itself or form a cycle (MQ2021).
/// </summary>
public sealed class PackageLayeringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Outside_dependsOn_is_MQ2020_and_parents_lend_dependencies()
    {
        var b = new ModelBuilder(seed: 1);
        var shared = b.Package("Shared");
        var billing = b.Package("Billing");
        var sales = b.Package("Sales").DependsOn(shared);
        var orders = b.Package("Orders", sales);
        var money = b.ScalarType("Money", "decimal", shared);
        var tax = b.ScalarType("TaxRate", "decimal", billing);
        var order = b.Entity("Order", orders).Key("id", "uuid").Attr("total", money).Attr("taxRate", tax);
        b.Entity("Invoice", billing).Key("id", "uuid").Attr("total", money).Attr("taxRate", tax); // Billing is unchecked

        var found = await Layering(b);

        // Orders declares nothing, yet its parent's dependsOn reaches Shared; Billing is in no dependsOn of the chain.
        var d = Assert.Single(found);
        Assert.Equal(("MQ2020", DiagnosticSeverity.Error, order.AttrId("taxRate"), "/attributes/2/type/ref"), (d.Rule, d.Severity, d.ElementId, d.JsonPointer));
        Assert.Equal(".maquettiste/model/entities/order.json", d.FilePath);
        Assert.Contains("scalar-type TaxRate in package 'Billing', which package 'Sales.Orders' does not depend on", d.Message, StringComparison.Ordinal);
        Assert.Contains("add 'Billing'", d.Message, StringComparison.Ordinal);

        // Listing Billing in the child's own dependsOn clears it: the union runs over the package and its parents.
        orders.DependsOn(billing);
        Assert.Empty(await Layering(b));
    }

    [Fact]
    public async Task Descendants_are_reachable()
    {
        var b = new ModelBuilder(seed: 2);
        var platform = b.Package("Platform");
        var platformTypes = b.Package("Types", platform);
        var sales = b.Package("Sales").DependsOn(platform);
        var orders = b.Package("Orders", sales);
        var status = b.Enum("OrderStatus", orders).Member("Open", 0);
        var money = b.ScalarType("Money", "decimal", platformTypes);
        var region = b.Enum("Region", sales).Member("North", 0);
        // A package reaches its own descendants, and a dependsOn target's descendants.
        b.Entity("Customer", sales).Key("id", "uuid").Attr("status", status).Attr("balance", money);
        // A child does not reach its parent by default: Sales is not in the dependsOn of Orders or of Sales itself.
        var order = b.Entity("Order", orders).Key("id", "uuid").Attr("region", region);

        var d = Assert.Single(await Layering(b));

        Assert.Equal((order.AttrId("region"), "/attributes/1/type/ref"), (d.ElementId, d.JsonPointer));
        Assert.Contains("in package 'Sales', which package 'Sales.Orders' does not depend on", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cycle_is_MQ2021()
    {
        var b = new ModelBuilder(seed: 3);
        var a = b.Package("A");
        var c = b.Package("C");
        var bb = b.Package("B").DependsOn(c);
        a.DependsOn(bb);
        c.DependsOn(a);
        var self = b.Package("Self");
        self.DependsOn(self);
        b.Package("Outside").DependsOn(a); // reaches the cycle but is not in it

        var found = (await Layering(b)).Where(d => d.Rule == "MQ2021").ToList();

        Assert.Equal(
            new[] { a.Id, bb.Id, c.Id, self.Id }.Select(id => (id, "/dependsOn/0")).Order(),
            found.Select(d => (d.ElementId!, d.JsonPointer!)).Order());
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("forms a cycle with 'B' (packages 'A', 'B', 'C')", found.Single(d => d.ElementId == a.Id).Message, StringComparison.Ordinal);
        Assert.Contains("Package 'Self' names itself in dependsOn", found.Single(d => d.ElementId == self.Id).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_dependsOn_nothing_changes()
    {
        var b = new ModelBuilder(seed: 4);
        var shared = b.Package("Shared");
        var billing = b.Package("Billing");
        var orders = b.Package("Orders", b.Package("Sales"));
        var money = b.ScalarType("Money", "decimal", shared);
        var tax = b.ScalarType("TaxRate", "decimal", billing);
        b.Entity("Order", orders).Key("id", "uuid").Attr("total", money).Attr("taxRate", tax);
        b.Entity("Invoice", billing).Key("id", "uuid").Attr("total", money);

        var report = await ValidationFixture.Validator().ValidateAsync(b.Build(), ValidationScope.All, null, Ct);

        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ2020" or "MQ2021");
    }

    [Fact]
    public async Task Diagrams_and_membership_do_not_count()
    {
        var b = new ModelBuilder(seed: 5);
        var billing = b.Package("Billing");
        var sales = b.Package("Sales").DependsOn();
        var orders = b.Package("Orders", sales); // its parent is not a dependency
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid");
        var order = b.Entity("Order", orders).Key("id", "uuid");
        var loose = b.Entity("Loose").Key("id", "uuid"); // in no package: unchecked, and in no layer
        b.Entity("Customer", sales).Key("id", "uuid").Attr("favourite", "string").Base(loose);
        b.Entity("Audit").Key("id", "uuid").Base(invoice);
        b.Add(new Diagram
        {
            Id = b.NewId(), Name = "Sales overview", Package = sales.Id,
            Members = [new DiagramMember { Element = invoice.Id }, new DiagramMember { Element = order.Id }, new DiagramMember { Element = billing.Id }],
        });
        var main = b.Database("main", Dialect.PostgreSql);
        b.Table("orders", main).Column("id", "uuid", nullable: false).PrimaryKey("id");

        Assert.Empty(await Layering(b));

        // The same reference from a member of Sales is one.
        var vip = b.Entity("VipInvoice", sales).Base(invoice);
        var d = Assert.Single(await Layering(b));
        Assert.Equal((vip.Id, "/base"), (d.ElementId, d.JsonPointer));
    }

    [Fact]
    public async Task Category_references_are_left_to_MQ2008()
    {
        var b = new ModelBuilder(seed: 6);
        var billing = b.Package("Billing");
        var sales = b.Package("Sales").DependsOn();
        var categoryId = b.NewId();
        b.Add(new CategoryTree { Id = b.NewId(), Name = "Billing categories", Package = billing.Id, Categories = [new Category { Id = categoryId, Name = "Finance" }] });
        var order = b.Entity("Order", sales).Key("id", "uuid").Category(categoryId);

        var report = await ValidationFixture.Validator().ValidateAsync(b.Build(), ValidationScope.All, null, Ct);

        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ2020");
        var d = Assert.Single(report.Diagnostics, d => d.Rule == "MQ2008");
        Assert.Equal((order.Id, "/category"), (d.ElementId, d.JsonPointer));
    }

    [Fact]
    public async Task A_packages_own_stereotypes_are_judged_as_its_own()
    {
        var b = new ModelBuilder(seed: 7);
        var shared = b.Package("Shared");
        var sales = b.Package("Sales").DependsOn();
        b.Stereotype("shared-root").InPackage(shared);
        b.Stereotype("sales-root").InPackage(sales);
        sales.Stereotype("shared-root").Stereotype("sales-root");

        // Sales is top-level: judged as its parent's (none), it would be unchecked; it is judged as Sales, which reaches only itself.
        var d = Assert.Single(await Layering(b));
        Assert.Equal((sales.Id, "/stereotypes/0"), (d.ElementId, d.JsonPointer));
        Assert.Contains("stereotype shared-root in package 'Shared'", d.Message, StringComparison.Ordinal);

        sales.DependsOn(shared);
        Assert.Empty(await Layering(b));
    }

    /// <summary>Validates the whole model and returns its MQ2020 and MQ2021 findings.</summary>
    private static async Task<IReadOnlyList<Diagnostic>> Layering(ModelBuilder b)
    {
        var report = await ValidationFixture.Validator().ValidateAsync(b.Build(), ValidationScope.All, null, Ct);
        return [.. report.Diagnostics.Where(d => d.Rule is "MQ2020" or "MQ2021")];
    }
}
