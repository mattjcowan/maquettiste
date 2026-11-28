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
    public void Holder_schema_matches_the_resolvers_schema_for_every_billing_table()
    {
        // next-phase-2-design.md section 3.1 (D3): validation's holders (a table file's) and the resolver's (by ids for a table without
        // a file) name the schema entry the resolved table is in, so both judge the same effective set.
        var model = Validation.ValidationFixture.LoadRepo(Fixtures.Path("models", "billing"));
        var resolved = Resolution.ResolutionKit.Resolve(model);
        var files = new Dictionary<string, Table>(StringComparer.Ordinal);
        foreach (var file in model.All<Table>())
        {
            files.TryAdd(file.Id, file);
        }

        var checkedTables = 0;
        foreach (var db in resolved.Databases)
        {
            var database = model.Get<Database>(db.Id)!;
            foreach (var table in db.Tables)
            {
                var schema = files.TryGetValue(table.Key, out var file) ? model.HolderOf(file).Schema : model.StereotypeDefaults.SchemaOf(db.Id, null);
                if (schema is null)
                    Assert.DoesNotContain(database.Schemas, s => s.Name == table.Schema);
                else
                    Assert.Equal(database.Schemas.Single(s => s.Id == schema).Name, table.Schema);
                checkedTables++;
            }
        }

        Assert.True(checkedTables >= 7, "billing has at least seven tables");
        Assert.All(resolved.Databases.SelectMany(d => d.Tables), t => Assert.True(files.ContainsKey(t.Key)));
    }
}
