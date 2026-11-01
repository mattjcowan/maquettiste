using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>The definitions <c>model</c> lists (next-phase-1-design.md section 5.5): stereotypes, tag vocabularies and category trees.</summary>
public sealed class ResolvedDefinitionsTests
{
    [Fact]
    public void Model_lists_stereotype_definitions_tag_vocabularies_and_category_trees()
    {
        var b = new ModelBuilder(seed: 550);
        var billing = b.Package("Billing");
        b.Stereotype("versioned").InPackage(billing).AppliesTo("entity", "relation").DisplayName("Versioned").Description("Rows keep a version.")
            .Attr("version", "int32").Attr("stamp", "datetime", a => a.Order(-1)).DefaultProperty("retain", 3);
        b.Add(new Stereotype { Id = b.NewId(), Key = "audited", Name = "Audited", Icon = "clock", Color = "#336699" });
        b.Tags(true, "api", "internal");
        b.Add(new TagVocabulary
        {
            Id = b.NewId(),
            Name = "billing-tags",
            Package = billing.Id,
            Definitions = [new TagDefinition { Key = "ledger", Description = "Posts to the ledger.", Color = "#aa0000" }],
        });
        var finance = b.Category("Finance");
        b.Category("Tax", finance);
        b.Category("Payroll", finance);
        b.Category("Operations");
        string receivables = b.NewId(), payables = b.NewId(), disputes = b.NewId(), orphan = b.NewId();
        b.Add(new CategoryTree
        {
            Id = b.NewId(),
            Name = "billing-categories",
            Package = billing.Id,
            Categories =
            [
                new Category { Id = receivables, Name = "Receivables", Order = 2 },
                new Category { Id = payables, Name = "Payables", Order = 1 },
                new Category { Id = disputes, Name = "Disputes", Parent = receivables, Description = new Description { Text = "Contested invoices." } },
                new Category { Id = orphan, Name = "Orphan", Parent = "01J0000000000000000000NONE", Order = 3 },
            ],
        });
        b.Entity("Invoice", billing).Key("id", "uuid").Stereotype("versioned").Category(disputes);
        var model = ResolutionKit.Resolve(b);

        // Stereotypes: one per key, by key; the label is the name, the file's package and annotations come with it.
        Assert.Equal(["audited", "versioned"], model.Stereotypes.Select(s => s.Key));
        Assert.Equal(["k:stereotype"], model.Stereotypes.MembershipKeys);
        var audited = model.Stereotypes[0];
        Assert.Equal(("stereotype", "Audited", "clock", "#336699"), (audited.Kind, audited.Name, audited.Icon, audited.Color));
        Assert.Null(audited.Package);
        Assert.Empty(audited.Attributes);
        Assert.Empty(audited.DefaultProperties);
        var versioned = model.Stereotypes[1];
        Assert.Equal(("versioned", "Versioned", "Rows keep a version."), (versioned.Name, versioned.DisplayName, versioned.Description));
        Assert.Same(model.Packages.Single(p => p.Name == "Billing"), versioned.Package);
        Assert.Equal(["entity", "relation"], versioned.AppliesTo);
        Assert.Equal(3L, versioned.DefaultProperties["retain"]);
        Assert.Equal(["stamp", "version"], versioned.Attributes.Select(a => a.Name));
        Assert.All(versioned.Attributes, a =>
        {
            Assert.Same(versioned, a.Owner);
            Assert.True(a.IsVirtual);
            Assert.Equal("versioned", a.FromStereotype?.Key);
        });
        // A definition's attribute keeps the id of its virtual copies on the carriers.
        var invoice = model.Entity("Invoice");
        Assert.Equal(versioned.Attributes.Single(a => a.Name == "version").Id, invoice.Attributes.Single(a => a.Name == "version").Id);
        Assert.Contains("e:" + versioned.Id, versioned.Dependencies);
        Assert.Same(versioned, model.Find(versioned.Id));

        // Tag vocabularies: the global one first, then by package; each with its scope and declared tags.
        Assert.Equal(["k:tag-vocabulary"], model.TagVocabularies.MembershipKeys);
        Assert.Equal(["tags", "billing-tags"], model.TagVocabularies.Select(v => v.Name));
        var global = model.TagVocabularies[0];
        Assert.Equal("tag-vocabulary", global.Kind);
        Assert.Null(global.Package);
        Assert.True(global.Strict);
        Assert.Equal(["api", "internal"], global.Definitions.Select(d => d.Key));
        var scoped = model.TagVocabularies[1];
        Assert.Same(versioned.Package, scoped.Package);
        Assert.False(scoped.Strict);
        Assert.Equal(("ledger", "Posts to the ledger.", "#aa0000"), (scoped.Definitions[0].Key, scoped.Definitions[0].Description, scoped.Definitions[0].Color));
        Assert.Contains("e:" + scoped.Id, scoped.Dependencies);

        // Category trees: the global one first; roots and depth-first categories in sibling order (order, then file order).
        Assert.Equal(["k:category-tree"], model.CategoryTrees.MembershipKeys);
        Assert.Equal(["categories", "billing-categories"], model.CategoryTrees.Select(t => t.Name));
        var globalTree = model.CategoryTrees[0];
        Assert.Equal("category-tree", globalTree.Kind);
        Assert.Null(globalTree.Package);
        Assert.Equal(["Finance", "Operations"], globalTree.Roots.Select(c => c.Name));
        Assert.Equal(["Finance", "Tax", "Payroll", "Operations"], globalTree.Categories.Select(c => c.Name));
        Assert.Equal(["Tax", "Payroll"], globalTree.Roots[0].Children.Select(c => c.Name));
        Assert.Same(globalTree.Roots[0], globalTree.Categories[1].Parent);
        Assert.Equal("Finance/Tax", globalTree.Categories[1].Path);

        var billingTree = model.CategoryTrees[1];
        Assert.Same(versioned.Package, billingTree.Package);
        Assert.Equal(["Payables", "Receivables", "Orphan"], billingTree.Roots.Select(c => c.Name));
        Assert.Equal(["Payables", "Receivables", "Disputes", "Orphan"], billingTree.Categories.Select(c => c.Name));
        Assert.Equal([1, 2, (int?)null, 3], billingTree.Categories.Select(c => c.Order));
        Assert.Null(billingTree.Roots[2].Parent); // its parent is not in the tree
        var disputesCategory = billingTree.Categories[2];
        Assert.Equal(("Receivables/Disputes", "Contested invoices."), (disputesCategory.Path, disputesCategory.Description));
        Assert.Same(billingTree.Roots[1], disputesCategory.Parent);
        Assert.Empty(disputesCategory.Children);
        // The tree's categories are the instances annotated objects carry.
        Assert.Same(disputesCategory, invoice.Category);
        Assert.Contains("e:" + billingTree.Id, billingTree.Dependencies);
        Assert.Same(billingTree, model.Find(billingTree.Id));
    }
}
