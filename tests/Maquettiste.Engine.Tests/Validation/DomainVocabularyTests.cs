using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Tags and categories global and per domain (explorer-redesign.md section 1.11, step 16): MQ1009 per scope, MQ2008, MQ3021.</summary>
public sealed class DomainVocabularyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ValidationReport> Validate(ModelSnapshot model) =>
        ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);

    private static TagVocabulary Tags(ModelBuilder b, string name, string? package, params string[] keys) =>
        new() { Id = b.NewId(), Name = name, Package = package, Definitions = [.. keys.Select(k => new TagDefinition { Key = k })] };

    private static CategoryTree Tree(ModelBuilder b, string name, string? package, out string categoryId, string categoryName)
    {
        categoryId = b.NewId();
        return new CategoryTree { Id = b.NewId(), Name = name, Package = package, Categories = [new Category { Id = categoryId, Name = categoryName }] };
    }

    private static (ModelBuilder Builder, PackageBuilder Sales, PackageBuilder Billing, PackageBuilder Invoicing) Domains()
    {
        var b = new ModelBuilder(seed: 16);
        var sales = b.Package("Sales");
        var billing = b.Package("Billing");
        var invoicing = b.Package("Invoicing", billing);
        return (b, sales, billing, invoicing);
    }

    [Fact]
    public async Task A_global_and_a_domain_vocabulary_load_side_by_side()
    {
        var (b, _, billing, invoicing) = Domains();
        b.Add(Tags(b, "Tags", null, "core"));
        b.Add(Tags(b, "Billing tags", billing.Id, "ledger"));
        b.Add(Tree(b, "Categories", null, out var global, "Shared"));
        b.Add(Tree(b, "Billing categories", billing.Id, out var receivables, "Receivables"));
        b.Entity("Invoice", invoicing).Key("id", "uuid").Tag("core").Tag("ledger").Category(receivables);
        b.Entity("Account", billing).Key("id", "uuid").Category(global);
        var model = b.Build();

        Assert.Empty(model.LoadDiagnostics);
        Assert.Equal(2, model.TagVocabularies.Count());
        Assert.Equal(2, model.CategoryTrees.Count());
        Assert.Equal("Tags", model.Tags!.Name);
        Assert.Equal([invoicing.Id, billing.Id, null], model.VocabularyChain(invoicing.Id));
        Assert.Equal(billing.Id, model.Summaries().Single(s => s.Name == "Billing tags").Package);
        Assert.Empty((await Validate(model)).Diagnostics);
    }

    [Fact]
    public async Task A_second_vocabulary_in_the_same_scope_is_MQ1009()
    {
        var (b, _, billing, _) = Domains();
        b.Add(Tags(b, "Tags", null, "core"));
        b.Add(Tags(b, "Billing tags", billing.Id, "ledger"));
        var second = Tags(b, "More billing tags", billing.Id, "cash");
        b.Add(second);
        var model = b.Build();

        var d = Assert.Single(model.LoadDiagnostics);
        Assert.Equal(("MQ1009", second.Id), (d.Rule, d.ElementId));
        Assert.Equal("MQ1009", Assert.Single((await Validate(model)).Diagnostics).Rule);
    }

    [Fact]
    public async Task A_tag_or_category_of_another_domain_is_MQ2008()
    {
        var (b, sales, billing, _) = Domains();
        b.Add(Tags(b, "Sales tags", sales.Id, "pipeline"));
        b.Add(Tree(b, "Sales categories", sales.Id, out var leads, "Leads"));
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid").Tag("pipeline").Category(leads);
        b.Entity("Opportunity", sales).Key("id", "uuid").Tag("pipeline").Category(leads);

        var diagnostics = (await Validate(b.Build())).Diagnostics;

        Assert.Equal(
            [("MQ2008", "/category"), ("MQ2008", "/tags/0")],
            diagnostics.Select(d => (d.Rule, d.JsonPointer!)).Order());
        Assert.All(diagnostics, d => Assert.Equal((DiagnosticSeverity.Error, invoice.Id), (d.Severity, d.ElementId)));
        Assert.Contains("domain 'Sales'", diagnostics[0].Message, StringComparison.Ordinal);
    }

    private static Task<ValidationReport> ValidateSaved(ModelSnapshot model, string changedId) =>
        ValidationFixture.Validator().ValidateAsync(model, new ValidationScope([changedId], IncludeReferrers: true), null, Ct);

    [Fact]
    public async Task Saving_the_global_vocabulary_validates_the_domain_vocabularies_it_now_clashes_with()
    {
        // The scope a save validates (the changed file and its referrers) must include the vocabularies that MQ3021 lands on.
        var (b, _, billing, _) = Domains();
        var global = Tags(b, "Tags", null, "core", "ledger");
        b.Add(global);
        var domain = Tags(b, "Billing tags", billing.Id, "ledger");
        b.Add(domain);

        var diagnostics = (await ValidateSaved(b.Build(), global.Id)).Diagnostics;

        Assert.Contains(diagnostics, d => d.Rule == "MQ3021" && d.ElementId == domain.Id);
    }

    [Fact]
    public async Task Moving_a_domain_validates_the_elements_whose_tags_it_strands()
    {
        // Invoicing moved from Billing to Sales: the Billing tag and category of an entity in its Drafts sub-package are now outside
        // the entity's chain (MQ2008), although the entity is not a referrer of the package that changed.
        var b = new ModelBuilder(seed: 16);
        var sales = b.Package("Sales");
        var billing = b.Package("Billing");
        var invoicing = b.Package("Invoicing", sales);
        b.Add(Tags(b, "Billing tags", billing.Id, "ledger"));
        b.Add(Tree(b, "Billing categories", billing.Id, out var receivables, "Receivables"));
        var drafts = b.Package("Drafts", invoicing);
        var invoice = b.Entity("Invoice", drafts).Key("id", "uuid").Tag("ledger").Category(receivables);

        var diagnostics = (await ValidateSaved(b.Build(), invoicing.Id)).Diagnostics;

        Assert.Equal(
            [("MQ2008", "/category"), ("MQ2008", "/tags/0")],
            diagnostics.Where(d => d.ElementId == invoice.Id).Select(d => (d.Rule, d.JsonPointer!)).Order());
    }

    [Fact]
    public async Task An_undeclared_tag_on_the_chain_stays_MQ2006_and_strict_anywhere_on_the_chain_makes_it_an_error()
    {
        var (b, _, billing, _) = Domains();
        b.Add(Tags(b, "Tags", null, "core"));
        b.Add(Tags(b, "Billing tags", billing.Id, "ledger") with { Strict = true });
        b.Entity("Invoice", billing).Key("id", "uuid").Tag("adhoc");
        b.Entity("Loose").Key("id", "uuid").Tag("adhoc");

        var diagnostics = (await Validate(b.Build())).Diagnostics.OrderBy(d => d.Severity).ToList();

        Assert.Equal([("MQ2006", DiagnosticSeverity.Error), ("MQ2006", DiagnosticSeverity.Info)], diagnostics.Select(d => (d.Rule, d.Severity)));
    }

    [Fact]
    public async Task A_domain_key_or_name_that_the_global_or_an_enclosing_vocabulary_declares_is_MQ3021()
    {
        var (b, _, billing, invoicing) = Domains();
        b.Add(Tags(b, "Tags", null, "core"));
        var billingTags = Tags(b, "Billing tags", billing.Id, "core", "ledger");
        var invoicingTags = Tags(b, "Invoicing tags", invoicing.Id, "ledger", "draft");
        b.Add(billingTags).Add(invoicingTags);
        b.Add(Tree(b, "Categories", null, out _, "Shared"));
        var tree = Tree(b, "Invoicing categories", invoicing.Id, out var shared, "Shared");
        b.Add(tree);

        var diagnostics = (await Validate(b.Build())).Diagnostics.Where(d => d.Rule == "MQ3021").ToList();

        Assert.Equal(
            [(shared, "/categories/0/name"), (billingTags.Id, "/definitions/0/key"), (invoicingTags.Id, "/definitions/0/key")],
            diagnostics.Select(d => (d.ElementId!, d.JsonPointer!)).OrderBy(x => x.Item2, StringComparer.Ordinal).ThenBy(x => x.Item1 == billingTags.Id ? 0 : 1));
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void A_domain_vocabulary_belongs_under_model_vocabularies_with_its_name()
    {
        var b = new ModelBuilder(seed: 3);
        var global = Tags(b, "Tags", null);
        var domain = Tags(b, "Billing", "01J00000000000000000000000");
        var tree = new CategoryTree { Id = b.NewId(), Name = "Billing", Package = "01J00000000000000000000000" };

        Assert.Equal("tags.json", ModelPaths.FileName(global, false));
        Assert.Equal("billing-tags.json", ModelPaths.FileName(domain, false));
        Assert.Equal("billing-categories.json", ModelPaths.FileName(tree, false));
        Assert.Equal("model/vocabularies", KindInfo.Get(ElementKind.TagVocabulary).Folder);
    }
}
