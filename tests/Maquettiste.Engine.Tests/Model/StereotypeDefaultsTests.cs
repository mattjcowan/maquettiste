using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// The holders of default stereotypes (next-phase-2-design.md section 3.1, D2): which package chain and which database schema entry
/// reach an element, so its effective set takes their defaults.
/// </summary>
public sealed class StereotypeDefaultsTests
{
    /// <summary>Builds a snapshot from a builder's elements after an edit (the builders have no default-list members).</summary>
    internal static ModelSnapshot Rebuild(ModelBuilder b, Func<Element, Element> edit)
    {
        var copy = new ModelBuilder();
        foreach (var element in b.BuildElements())
            copy.Add(edit(element));
        return copy.Build();
    }

    /// <summary>Gives a database schema entry a default list.</summary>
    internal static Element SchemaDefaults(Element element, string schemaId, params string[] keys) =>
        element is Database database && database.Schemas.Any(s => s.Id == schemaId)
            ? database with { Schemas = [.. database.Schemas.Select(s => s.Id == schemaId ? s with { DefaultStereotypes = keys } : s)] }
            : element;

    internal static IEnumerable<(string Key, StereotypeSource Source, string? Holder)> Sources(IReadOnlyList<AppliedStereotype> set) =>
        set.Select(s => (s.Stereotype.Key, s.Source, s.DefaultHolder));

    [Fact]
    public void Package_defaults_run_outermost_first()
    {
        var b = new ModelBuilder(seed: 11);
        b.Stereotype("audited");
        b.Stereotype("owned");
        b.Stereotype("tracked");
        var outer = b.Package("Outer");
        var inner = b.Package("Inner", outer);
        var invoice = b.Entity("Invoice", inner).Key("id", "uuid").Stereotype("tracked");
        var model = Rebuild(b, e => e switch
        {
            Package p when p.Id == outer.Id => p with { DefaultStereotypes = ["audited"] },
            Package p when p.Id == inner.Id => p with { DefaultStereotypes = ["owned"] },
            _ => e,
        });

        var entity = model.Get<Entity>(invoice.Id)!;
        var holder = model.HolderOf(entity);
        Assert.Equal((ElementKind.Entity, inner.Id, (string?)null), (holder.Kind, holder.Package, holder.Schema));
        Assert.Equal(["tracked"], holder.Own);
        Assert.Empty(holder.Excludes);
        Assert.Equal(
            [("audited", StereotypeSource.PackageDefault, outer.Id), ("owned", StereotypeSource.PackageDefault, inner.Id), ("tracked", StereotypeSource.Own, null)],
            Sources(model.EffectiveStereotypes(entity)));
        Assert.All(model.EffectiveStereotypes(entity).Where(s => s.Source != StereotypeSource.Own), s => Assert.Equal(-1, s.Entry));
        Assert.Same(holder, model.HolderOf(entity));

        // A package's chain starts at its parent: it gives its own defaults to its members, not to itself.
        var innerPackage = model.Get<Package>(inner.Id)!;
        Assert.Equal(outer.Id, model.HolderOf(innerPackage).Package);
        Assert.Equal([("audited", StereotypeSource.PackageDefault, outer.Id)], Sources(model.EffectiveStereotypes(innerPackage)));
        Assert.Empty(model.EffectiveStereotypes(model.Get<Package>(outer.Id)!));
    }

    [Fact]
    public void A_seed_takes_its_targets_package()
    {
        var b = new ModelBuilder(seed: 12);
        b.Stereotype("reviewed");
        var billing = b.Package("Billing");
        var other = b.Package("Other");
        var status = b.Entity("Status", billing).Key("id", "uuid").Attr("code", "string");
        var plain = new Seed { Id = b.NewId(), Name = "statuses", Target = status.Id, Columns = [status.AttrId("code")] };
        var placed = new Seed { Id = b.NewId(), Name = "more-statuses", Package = other.Id, Target = status.Id, Columns = [status.AttrId("code")] };
        b.Add(plain).Add(placed);
        var model = Rebuild(b, e => e is Package p && p.Id == billing.Id ? p with { DefaultStereotypes = ["reviewed"] } : e);

        var seed = model.Get<Seed>(plain.Id)!;
        Assert.Equal(billing.Id, model.HolderOf(seed).Package);
        Assert.Equal([("reviewed", StereotypeSource.PackageDefault, billing.Id)], Sources(model.EffectiveStereotypes(seed)));
        // A seed's own package wins over its target's.
        Assert.Equal(other.Id, model.HolderOf(model.Get<Seed>(placed.Id)!).Package);
        Assert.Empty(model.EffectiveStereotypes(model.Get<Seed>(placed.Id)!));
    }

    [Fact]
    public void An_overlay_takes_the_schema_its_entity_is_routed_to()
    {
        var b = new ModelBuilder(seed: 13);
        b.Stereotype("audited").AppliesTo("table");
        var billing = b.Package("Billing");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid").Attr("notes", "string", a => a.Collection());
        var customer = b.Entity("Customer", billing).Key("id", "uuid");
        var main = b.Database("main", Dialect.PostgreSql);
        var sales = main.Schema("sales");
        var common = main.Schema("public");
        main.Package(billing, sales);
        b.Mapping(main, customer).Schema(common);
        var invoices = b.Table("invoices", main).OverlayFor(invoice);
        var customers = b.Table("customers", main).OverlayFor(customer);
        var notes = b.Table("notes", main).Column("id", "uuid");
        var stray = b.Table("stray", main).Column("id", "uuid").Edit(t => t with { Schema = b.NewId() });
        var child = new Table { Id = b.NewId(), Database = main.Id, Origin = TableOrigin.Synthesized, Entity = invoice.Id, Attribute = invoice.AttrId("notes") };
        b.Add(child);
        var model = Rebuild(b, e => SchemaDefaults(e, sales, "audited"));

        // The entity's convention package routes its overlay; a mapping's schema wins; a child table follows its owner's table.
        Assert.Equal(sales, model.HolderOf(model.Get<Table>(invoices.Id)!).Schema);
        Assert.Equal(common, model.HolderOf(model.Get<Table>(customers.Id)!).Schema);
        Assert.Equal(sales, model.HolderOf(model.Get<Table>(child.Id)!).Schema);
        // A table that states none, or names no entry of its database, is in the entry the default schema names.
        Assert.Equal(common, model.HolderOf(model.Get<Table>(notes.Id)!).Schema);
        Assert.Equal(common, model.HolderOf(model.Get<Table>(stray.Id)!).Schema);
        // A table without a file takes the same rule by ids.
        Assert.Equal(sales, model.StereotypeDefaults.SchemaOf(main.Id, null, invoice.Id));
        Assert.Equal(sales, model.StereotypeDefaults.SchemaOf(main.Id, null, invoice.Id, invoice.AttrId("notes")));
        Assert.Equal(common, model.StereotypeDefaults.SchemaOf(main.Id, null, customer.Id));

        // The overlay's package falls back to its entity's, and the schema's default reaches it.
        var overlay = model.Get<Table>(invoices.Id)!;
        Assert.Equal(billing.Id, model.HolderOf(overlay).Package);
        Assert.Equal([("audited", StereotypeSource.SchemaDefault, sales)], Sources(model.EffectiveStereotypes(overlay)));
        Assert.Empty(model.EffectiveStereotypes(model.Get<Table>(notes.Id)!));
    }

    [Fact]
    public void Holder_schema_matches_the_resolvers_schema_for_every_billing_table()
    {
        // next-phase-2-design.md section 3.1 (D3): validation's holders (a table file's) and the resolver's (by ids for a table without
        // a file) name the schema entry the resolved table is in, so both judge the same effective set.
        var model = Validation.ValidationFixture.LoadRepo(Fixtures.Path("models", "billing"));
        var resolved = Resolution.ResolutionKit.Resolve(model);
        var files = new Dictionary<string, Table>(StringComparer.Ordinal);
        foreach (var file in model.All<Table>())
        {
            var key = file.Origin != TableOrigin.Synthesized ? file.Id
                : file.Entity is { } entity ? entity + (file.Attribute is { } attribute ? "." + attribute : "") + "@" + file.Database
                : file.Relation + "@" + file.Database;
            files.TryAdd(key, file);
        }

        var checkedTables = 0;
        foreach (var db in resolved.Databases)
        {
            var database = model.Get<Database>(db.Id)!;
            foreach (var table in db.Tables)
            {
                var schema = files.TryGetValue(table.Key, out var file) ? model.HolderOf(file).Schema
                    : table.Relation is not null && table.Attribute is null ? model.StereotypeDefaults.SchemaOf(db.Id, null)
                    : model.StereotypeDefaults.SchemaOf(db.Id, null, table.Entity!.Id, table.Attribute?.Id);
                if (schema is null)
                    Assert.DoesNotContain(database.Schemas, s => s.Name == table.Schema);
                else
                    Assert.Equal(database.Schemas.Single(s => s.Id == schema).Name, table.Schema);
                checkedTables++;
            }
        }

        Assert.True(checkedTables >= 7, "billing has at least seven tables");
        Assert.Contains(resolved.Databases.SelectMany(d => d.Tables), t => !files.ContainsKey(t.Key));
        Assert.Contains(resolved.Databases.SelectMany(d => d.Tables), t => files.ContainsKey(t.Key) && t.Origin == "synthesized");
    }
}
