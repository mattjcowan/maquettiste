using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Scripting;

/// <summary>
/// A script rule's <c>model</c> index functions and the keys its functions record (next-phase-1-design.md section 5.7):
/// <c>withStereotype</c>, <c>withTag</c>, <c>withProperty</c> and <c>byQualifiedName</c> beside <c>get</c>, <c>all</c> and
/// <c>referencesTo</c>, with the <c>e:</c>, <c>k:</c>, <c>ix:</c> and <c>rt:</c> keys <see cref="RuleKeyHasher"/> hashes.
/// </summary>
public sealed class RuleIndexTests
{
    private sealed record Sample(ModelSnapshot Model, EntityBuilder Invoice, EntityBuilder Customer, EnumBuilder Status, string DiagramId);

    /// <summary>
    /// Billing: two entities named Alpha (to show the id order), Zeta, Invoice and Customer, an enum, and a diagram showing Customer.
    /// <paramref name="change"/> runs after every element of the sample is declared, so the ids stay the same across variants.
    /// </summary>
    private static Sample Build(Action<ModelBuilder, EntityBuilder>? change = null, bool customerOnDiagram = true)
    {
        var b = new ModelBuilder(seed: 41);
        var billing = b.Package("Billing");
        var other = b.Package("Other");
        var status = b.Enum("InvoiceStatus", billing).Member("Draft", 0).Tag("core");
        b.Entity("Zeta", billing).Key("id", "uuid").Stereotype("audited").Tag("core").Property("retention", 30);
        b.Entity("Alpha", other).Key("id", "uuid").Tag("core");
        b.Entity("Alpha", billing).Key("id", "uuid").Stereotype("audited").Stereotype("soft-delete");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid").Attr("status", status);
        var customer = b.Entity("Customer", billing).Key("id", "uuid");
        var diagramId = b.NewId();
        b.Add(new Diagram { Id = diagramId, Name = "Overview", Members = customerOnDiagram ? [new DiagramMember { Element = customer.Id }] : [] });
        change?.Invoke(b, customer);
        return new Sample(b.Build(), invoice, customer, status, diagramId);
    }

    private static (IReadOnlyList<Diagnostic> Diagnostics, List<string> Keys) Run(Sample s, string check)
    {
        using var pool = Scripts.Pool("maquettiste.rule({ id: 'probe', check(element, model, report) {\n" + check + "\n} });", path: Scripts.RuleScript);
        using var lease = pool.Rent();
        var reads = new ListRecorder();
        var diagnostics = lease.Sandbox.RunRule("probe", s.Model.GetDocument(s.Invoice.Id)!, s.Model, Scripts.Ctx(reads, seed: s.Invoice.Id));
        return (diagnostics, reads.Keys);
    }

    [Fact]
    public void Indexes_return_documents_in_name_then_id_order_and_record_their_keys()
    {
        var s = Build();
        var (diagnostics, keys) = Run(s, """
            const names = list => list.map(d => d.name + '/' + d.kind).join(',');
            report(names(model.withStereotype('audited')));
            report(names(model.withTag('core')));
            report(names(model.withProperty('retention')));
            report(String(model.withTag('unknown').length) + ' ' + Object.isFrozen(model.withTag('core')) + ' ' + Object.isFrozen(model.withTag('core')[0]));
            report(names(model.all('entity')));
            """);

        // Lists are ordinal by (name, id): the two Alphas by id; an element is found by what its own file lists.
        var alphas = s.Model.All<Entity>().Where(e => e.Name == "Alpha").Select(e => e.Id).Order(StringComparer.Ordinal).ToList();
        var alphaTagged = s.Model.All<Entity>().Single(e => e.Name == "Alpha" && e.Tags.Contains("core")).Id;
        var alphaAudited = s.Model.All<Entity>().Single(e => e.Name == "Alpha" && e.Stereotypes.Contains("audited")).Id;
        Assert.Equal(
        [
            "Alpha/entity,Zeta/entity",
            "Alpha/entity,InvoiceStatus/enum,Zeta/entity",
            "Zeta/entity",
            "0 true true",
            "Alpha/entity,Alpha/entity,Customer/entity,Invoice/entity,Zeta/entity",
        ], diagnostics.Select(d => d.Message));
        Assert.All(diagnostics, d => Assert.Equal("x/probe", d.Rule));

        // Each index function records its ix: key and e: per document it returns; all records k: and e: per document.
        Assert.Contains("ix:stereotype:audited", keys);
        Assert.Contains("ix:tag:core", keys);
        Assert.Contains("ix:property:retention", keys);
        Assert.Contains("ix:tag:unknown", keys);
        Assert.Contains("k:entity", keys);
        Assert.Contains("e:" + alphaTagged, keys);
        Assert.Contains("e:" + alphaAudited, keys);
        Assert.Contains("e:" + s.Status.Id, keys);
        Assert.All(alphas, id => Assert.Contains("e:" + id, keys));

        // An ix: key hashes the sorted ids found: a change elsewhere keeps it, a new member changes it.
        var hasher = RuleKeyHasher.Of(s.Model);
        var renamed = RuleKeyHasher.Of(Build((_, customer) => customer.Rename("Client")).Model);
        var tagged = RuleKeyHasher.Of(Build((_, customer) => customer.Tag("core")).Model);
        Assert.Equal(hasher.CurrentHash("ix:tag:core"), renamed.CurrentHash("ix:tag:core"));
        Assert.NotEqual(hasher.CurrentHash("ix:tag:core"), tagged.CurrentHash("ix:tag:core"));
        Assert.Equal(hasher.CurrentHash("ix:stereotype:audited"), tagged.CurrentHash("ix:stereotype:audited"));
        Assert.NotEqual(hasher.CurrentHash("e:" + s.Customer.Id), renamed.CurrentHash("e:" + s.Customer.Id));
        Assert.Equal(hasher.CurrentHash("k:entity"), renamed.CurrentHash("k:entity"));
        Assert.NotEqual(hasher.CurrentHash("k:entity"), RuleKeyHasher.Of(Build((b, _) => b.Entity("Ledger").Key("id", "uuid")).Model).CurrentHash("k:entity"));
        Assert.Equal(RuleKeyHasher.Absent, hasher.CurrentHash("e:01JNOTANID0000000000000000"));
    }

    [Fact]
    public void By_qualified_name_reads_the_name_index()
    {
        var s = Build();
        var (diagnostics, keys) = Run(s, """
            const found = model.byQualifiedName('@entity:Billing.Invoice');
            report(found.length + ' ' + found[0].id);
            report(model.byQualifiedName('@entity:Alpha').map(d => d.id).join(','));
            report(model.byQualifiedName('@attribute:Invoice.status')[0].name);
            report(String(model.byQualifiedName('@entity:Billing.Nothing').length));
            """);

        // A bare name that two elements share finds both, as a batch would (MQ1013 there); a sub-element comes back as get returns it.
        var alphas = s.Model.All<Entity>().Where(e => e.Name == "Alpha").Select(e => e.Id).Order(StringComparer.Ordinal);
        Assert.Equal(["1 " + s.Invoice.Id, string.Join(",", alphas), "status", "0"], diagnostics.Select(d => d.Message));
        Assert.Contains("ix:qn:@entity:Billing.Invoice", keys);
        Assert.Contains("ix:qn:@attribute:Invoice.status", keys);
        Assert.Contains("ix:qn:@entity:Billing.Nothing", keys);
        Assert.Contains("e:" + s.Invoice.Id, keys);
        Assert.Contains("e:" + s.Invoice.AttrId("status"), keys);

        // The key hashes what the name finds: a rename into the name changes it.
        var before = RuleKeyHasher.Of(s.Model).CurrentHash("ix:qn:@entity:Billing.Nothing");
        var after = RuleKeyHasher.Of(Build((_, customer) => customer.Rename("Nothing")).Model).CurrentHash("ix:qn:@entity:Billing.Nothing");
        Assert.NotEqual(before, after);

        // A malformed name, or a kind without names, is a TypeError: MQ5002 on the rule file.
        foreach (var bad in new[] { "Invoice", "@migration:x", "@entity:", "$invoice" })
        {
            var failure = Assert.Single(Run(s, $"model.byQualifiedName('{bad}');").Diagnostics);
            Assert.Equal(("MQ5002", Scripts.RuleScript), (failure.Rule, failure.FilePath));
            Assert.Contains("TypeError", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void References_to_cover_the_own_file_and_diagrams()
    {
        var s = Build();
        var customerKey = ((Entity)s.Model.GetDocument(s.Customer.Id)!.Element).Key!.Attributes[0];
        var (diagnostics, keys) = Run(s, $$"""
            const from = list => list.map(r => r.fromElementId).join(',');
            report(from(model.referencesTo('{{s.Customer.Id}}')));
            report(from(model.referencesTo('{{customerKey}}')));
            """);

        // The diagram showing Customer, and Customer's own file naming its key attribute, are both listed.
        Assert.Equal([s.DiagramId, s.Customer.Id], diagnostics.Select(d => d.Message));
        Assert.Contains("rt:" + s.Customer.Id, keys);
        Assert.Contains("rt:" + customerKey, keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("r:", StringComparison.Ordinal));

        // r: skips diagrams, so a diagram that stops showing Customer leaves it alone; rt: covers every entry, so it changes.
        var hidden = Build(customerOnDiagram: false).Model;
        Assert.Equal(s.Model.ReferrersHash(s.Customer.Id), hidden.ReferrersHash(s.Customer.Id));
        Assert.NotEqual(RuleKeyHasher.Of(s.Model).CurrentHash("rt:" + s.Customer.Id), RuleKeyHasher.Of(hidden).CurrentHash("rt:" + s.Customer.Id));
        Assert.Equal(RuleKeyHasher.Of(s.Model).CurrentHash("rt:" + customerKey), RuleKeyHasher.Of(hidden).CurrentHash("rt:" + customerKey));
    }
}
