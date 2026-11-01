using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Stereotype rules and typed parameters (next-phase-1-design.md section 4.2): an element is judged on its effective set (own
/// stereotypes plus the ones they imply) for groups (MQ2013), requirements (MQ2014), conflicts (MQ2015) and parameters (MQ2018); a
/// stereotype's links (MQ2016, MQ2017) and parameters (MQ2019, MQ2018 for defaults) are judged on the stereotype.
/// </summary>
public sealed class StereotypeRuleTests
{
    private static readonly string[] Rules = ["MQ2003", "MQ2004", "MQ2009", "MQ2013", "MQ2014", "MQ2015", "MQ2016", "MQ2017", "MQ2018", "MQ2019"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_stereotypes_of_one_group_are_MQ2013_at_the_later_entry()
    {
        var b = new ModelBuilder(41);
        b.Stereotype("soft-delete").Group("deletion");
        b.Stereotype("hard-delete").Group("deletion");
        b.Stereotype("aggregate-root").Implies("soft-delete");
        var own = b.Entity("Invoice").Key("id", "uuid").Stereotype("soft-delete").Stereotype("hard-delete");
        // Implied first: the own hard-delete is the later one, at its own entry.
        var implied = b.Entity("Payment").Key("id", "uuid").Stereotype("aggregate-root").Stereotype("hard-delete");

        var found = await Findings(b);

        Assert.Equal([("MQ2013", own.Id, "/stereotypes/1"), ("MQ2013", implied.Id, "/stereotypes/1")], Rows(found));
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("'soft-delete' (implied by 'aggregate-root') is in it too", found[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_stereotype_per_group_is_clean()
    {
        var b = new ModelBuilder(42);
        b.Stereotype("soft-delete").Group("deletion");
        b.Stereotype("hard-delete").Group("deletion");
        b.Stereotype("aggregate-root").Implies("soft-delete");
        b.Entity("Invoice").Key("id", "uuid").Stereotype("aggregate-root");
        // Listing the implied stereotype too keeps one entry of it: still one of the group.
        b.Entity("Payment").Key("id", "uuid").Stereotype("aggregate-root").Stereotype("soft-delete");
        b.Entity("Product").Key("id", "uuid").Stereotype("hard-delete");

        Assert.Empty(await Findings(b));
    }

    [Fact]
    public async Task A_missing_required_stereotype_is_MQ2014()
    {
        var b = new ModelBuilder(43);
        b.Stereotype("audited");
        b.Stereotype("tracked").Requires("audited");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Stereotype("tracked");

        var found = await Findings(b);

        Assert.Equal([("MQ2014", invoice.Id, "/stereotypes/0")], Rows(found));
        Assert.Contains("requires stereotype 'audited'", found[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_stereotype_own_or_implied_is_clean()
    {
        var b = new ModelBuilder(44);
        b.Stereotype("audited");
        b.Stereotype("tracked").Requires("audited");
        b.Stereotype("aggregate-root").Implies("audited");
        b.Entity("Invoice").Key("id", "uuid").Stereotype("tracked").Stereotype("audited");
        b.Entity("Payment").Key("id", "uuid").Stereotype("aggregate-root").Stereotype("tracked");

        Assert.Empty(await Findings(b));
    }

    [Fact]
    public async Task Two_conflicting_stereotypes_are_MQ2015()
    {
        var b = new ModelBuilder(45);
        b.Stereotype("immutable");
        b.Stereotype("versioned").Conflicts("immutable");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Stereotype("immutable").Stereotype("versioned");
        // Either side may declare the conflict; it is reported once per pair, at the later entry.
        var payment = b.Entity("Payment").Key("id", "uuid").Stereotype("versioned").Stereotype("immutable");

        var found = await Findings(b);

        Assert.Equal([("MQ2015", invoice.Id, "/stereotypes/1"), ("MQ2015", payment.Id, "/stereotypes/1")], Rows(found));
    }

    [Fact]
    public async Task Stereotypes_that_do_not_conflict_are_clean()
    {
        var b = new ModelBuilder(46);
        b.Stereotype("immutable");
        b.Stereotype("audited");
        b.Stereotype("versioned").Conflicts("immutable");
        b.Entity("Invoice").Key("id", "uuid").Stereotype("versioned").Stereotype("audited");

        Assert.Empty(await Findings(b));
    }

    [Fact]
    public async Task Invalid_links_are_MQ2016()
    {
        var b = new ModelBuilder(47);
        b.Stereotype("audited");
        b.Stereotype("hard-delete").Group("deletion");
        var broken = b.Stereotype("soft-delete").Group("deletion")
            .Requires("missing", "audited")
            .Conflicts("soft-delete", "audited")
            .Implies("hard-delete");

        var found = await Findings(b);

        Assert.Equal(
            [
                ("MQ2016", broken.Id, "/requires/0"),
                ("MQ2016", broken.Id, "/conflicts/0"),
                ("MQ2016", broken.Id, "/conflicts/1"),
                ("MQ2016", broken.Id, "/implies/0"),
            ],
            Rows(found));
        Assert.Contains("'missing', which is not defined", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("names the stereotype itself", found[1].Message, StringComparison.Ordinal);
        Assert.Contains("which the stereotype also requires", found[2].Message, StringComparison.Ordinal);
        Assert.Contains("own group 'deletion'", found[3].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_links_are_clean()
    {
        var b = new ModelBuilder(48);
        b.Stereotype("audited");
        b.Stereotype("hard-delete").Group("deletion");
        b.Stereotype("soft-delete").Group("deletion").Requires("audited").Conflicts("hard-delete");
        b.Stereotype("aggregate-root").Group("shape").Implies("soft-delete", "audited");

        Assert.Empty(await Findings(b));
    }

    [Fact]
    public async Task Implying_one_that_implies_others_is_MQ2017()
    {
        var b = new ModelBuilder(49);
        b.Stereotype("audited");
        var middle = b.Stereotype("soft-delete").Implies("audited");
        var top = b.Stereotype("aggregate-root").Implies("soft-delete");
        // A cycle: each implies one that implies others.
        var ping = b.Stereotype("ping").Implies("pong");
        var pong = b.Stereotype("pong").Implies("ping");

        var found = await Findings(b);

        Assert.Equal(
            [("MQ2017", top.Id, "/implies/0"), ("MQ2017", ping.Id, "/implies/0"), ("MQ2017", pong.Id, "/implies/0")],
            Rows(found).OrderBy(r => r.Item2 == top.Id ? 0 : r.Item2 == ping.Id ? 1 : 2));
        Assert.DoesNotContain(found, d => d.ElementId == middle.Id);
        Assert.Contains("implication is one level", found.Single(d => d.ElementId == top.Id).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_level_of_implication_is_clean()
    {
        var b = new ModelBuilder(50);
        b.Stereotype("audited");
        b.Stereotype("soft-delete");
        b.Stereotype("aggregate-root").Implies("soft-delete", "audited");
        b.Entity("Invoice").Key("id", "uuid").Stereotype("aggregate-root");

        Assert.Empty(await Findings(b));
    }

    [Fact]
    public async Task A_value_that_fails_the_parameters_is_MQ2018_and_a_missing_required_one_points_at_the_entry()
    {
        var b = new ModelBuilder(51);
        b.Stereotype("audited").Parameters("""{ "retentionDays": { "type": "integer", "minimum": 1 } }""", "retentionDays");
        b.Stereotype("aggregate-root").Implies("audited");
        var wrong = b.Entity("Invoice").Key("id", "uuid").Stereotype("audited").Property("retentionDays", "seven years");
        // Through an implied stereotype: the missing value points at the implying entry.
        var missing = b.Entity("Payment").Key("id", "uuid").Stereotype("aggregate-root");
        var product = b.Entity("Product").Key("id", "uuid").Attr("code", "string", a => a.Stereotype("audited").Property("retentionDays", 0));

        var found = await Findings(b);

        Assert.Equal(
            [
                ("MQ2018", wrong.Id, "/properties/retentionDays"),
                ("MQ2018", missing.Id, "/stereotypes/0"),
                ("MQ2018", product.AttrId("code"), "/attributes/1/properties/retentionDays"),
            ],
            Rows(found));
        Assert.Contains("Stereotype 'audited' parameters: properties/retentionDays", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("which 'aggregate-root' implies", found[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Values_that_match_the_parameters_are_clean()
    {
        var b = new ModelBuilder(52);
        b.Stereotype("audited").Parameters("""{ "retentionDays": { "type": "integer", "minimum": 1 } }""", "retentionDays")
            .DefaultProperty("retentionDays", 2555);
        b.Stereotype("aggregate-root").Implies("audited");
        b.Entity("Invoice").Key("id", "uuid").Stereotype("audited").Property("retentionDays", 30);
        b.Entity("Payment").Key("id", "uuid").Stereotype("aggregate-root");

        Assert.Empty(await Findings(b));
    }

    [Fact]
    public async Task A_default_that_fails_its_own_property_schema_is_MQ2018_on_the_stereotype()
    {
        var b = new ModelBuilder(53);
        var audited = b.Stereotype("audited").Parameters("""{ "retentionDays": { "type": "integer", "minimum": 1 } }""")
            .DefaultProperty("retentionDays", -1).DefaultProperty("owner", "platform");

        var found = await Findings(b);

        Assert.Equal([("MQ2018", audited.Id, "/defaultProperties/retentionDays")], Rows(found));
        Assert.Contains("The default retentionDays fails its parameter schema", found[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_default_may_leave_a_required_parameter_to_its_hosts()
    {
        var b = new ModelBuilder(54);
        b.Stereotype("audited").Parameters("""{ "retentionDays": { "type": "integer" }, "owner": { "type": "string" } }""", "retentionDays", "owner")
            .DefaultProperty("retentionDays", 2555);
        var host = b.Entity("Invoice").Key("id", "uuid").Stereotype("audited").Property("owner", "finance");
        var lacking = b.Entity("Payment").Key("id", "uuid").Stereotype("audited");

        var found = await Findings(b);

        // The stereotype is clean; the host that sets owner is clean; the one that does not fails on its merged values.
        Assert.Equal([("MQ2018", lacking.Id, "/stereotypes/0")], Rows(found));
        Assert.Contains("owner", found[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(found, d => d.ElementId == host.Id);
    }

    [Fact]
    public async Task Parameters_that_are_not_a_valid_schema_are_MQ2019()
    {
        var b = new ModelBuilder(55);
        var invalid = b.Stereotype("audited").Parameters("""{ "retentionDays": { "type": "whole-number" } }""");
        var undeclared = b.Stereotype("tracked").Parameters("""{ "owner": { "type": "string" } }""", "owner", "team");
        var xref = b.Stereotype("listed").Parameters("""{ "sortBy": { "type": "integer", "x-ref": { "kind": "attribute" } } }""");

        var found = await Findings(b);

        Assert.Equal(
            [
                ("MQ2019", invalid.Id, "/parameters/properties"),
                ("MQ2009", xref.Id, "/parameters/properties/sortBy/x-ref"),
                ("MQ2019", undeclared.Id, "/parameters/required/1"),
            ],
            Rows(found));
    }

    [Fact]
    public async Task Valid_parameters_are_clean()
    {
        var b = new ModelBuilder(56);
        b.Stereotype("audited").Parameters("""{ "retentionDays": { "type": "integer", "minimum": 1 } }""", "retentionDays")
            .DefaultProperty("retentionDays", 2555);
        b.Stereotype("listed").Parameters("""{ "sortBy": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element", "optional": true } } }""");

        Assert.Empty(await Findings(b));
    }

    [Fact]
    public async Task Implied_but_not_applicable_is_MQ2004_at_the_implying_entry()
    {
        var b = new ModelBuilder(57);
        b.Stereotype("audited");
        b.Stereotype("partitioned").AppliesTo("table");
        b.Stereotype("aggregate-root").Implies("partitioned");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Stereotype("audited").Stereotype("aggregate-root");

        var found = await Findings(b);

        Assert.Equal([("MQ2004", invoice.Id, "/stereotypes/1")], Rows(found));
        Assert.Contains("'partitioned' (implied by 'aggregate-root') applies to table, not to an entity", found[0].Message, StringComparison.Ordinal);
    }

    private static List<(string, string, string)> Rows(IEnumerable<Diagnostic> found) =>
        [.. found.Select(d => (d.Rule, d.ElementId!, d.JsonPointer!))];

    private static async Task<IReadOnlyList<Diagnostic>> Findings(ModelBuilder b)
    {
        var model = b.Build();
        var report = await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5001" or "MQ5004");
        return [.. report.Diagnostics.Where(d => Rules.Contains(d.Rule))];
    }
}
