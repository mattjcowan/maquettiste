using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Planning.PlanningKit;

namespace Maquettiste.Engine.Tests.Planning;

/// <summary>
/// The <c>each view</c> and <c>each sequence</c> scopes (engine-design.md section 7.0a, erratum E38): one unit per view or sequence of
/// every database, the key sequences the resolver creates included, keyed by the object's id; <c>generation.skip</c> and <c>where</c>
/// (tags, stereotypes, categories, database) read the object's own file.
/// </summary>
public sealed class ObjectScopeTests
{
    private static readonly ResolvedModel Model = Build();

    private static ResolvedModel Build()
    {
        var b = new ModelBuilder(seed: 71);
        b.Stereotype("ledger").AppliesTo("view", "sequence");
        var receivables = b.Category("Receivables");
        b.Entity("Counter").Key("id", "int64", IdentityStrategy.Sequence);
        var main = b.Database("main", Dialect.PostgreSql);
        var reporting = b.Database("reporting", Dialect.SqlServer);
        View(b, "open_ledger", main.Id, v => v with { Tags = ["finance"], Stereotypes = ["ledger"], Category = receivables });
        View(b, "monthly_totals", reporting.Id, v => v);
        View(b, "scratch", main.Id, v => v with { Generation = new Dictionary<string, GenerationHints> { ["p"] = new() { Skip = true } } });
        b.Add(new Sequence { Id = b.NewId(), Name = "posting_seq", Database = main.Id, Tags = ["finance"] });
        return Resolve(b.Build());
    }

    private static void View(ModelBuilder b, string name, string database, Func<View, View> change) =>
        b.Add(change(new View { Id = b.NewId(), Name = name, Database = database, Body = ImmutableDictionary<string, string>.Empty.Add("*", "select 1") }));

    [Fact]
    public async Task Each_view_and_each_sequence_plan_one_unit_per_object_of_every_database()
    {
        var plan = await Plan(Pack("p", [Unit("view", "each view"), Unit("sequence", "each sequence")]));

        Assert.Empty(plan.Diagnostics);
        var views = Model.Databases.SelectMany(d => d.Views).ToList();
        Assert.Equal(3, views.Count);
        // The scratch view's hint names pack "p": this pack plans the other two, keyed by the view's id.
        Assert.Equal(["monthly_totals", "open_ledger"], Names(plan, "view"));
        Assert.Contains(plan.Units, u => u.Key == "p/view:" + views.Single(v => v.Name == "open_ledger").Id);
        // The sequence file and the key sequence the resolver creates for Counter in each database.
        Assert.Equal(["counters_seq", "counters_seq", "posting_seq"], Names(plan, "sequence"));
        Assert.All(plan.Units.Where(u => u.Unit.Id == "sequence"), u => Assert.IsType<RSequence>(u.Element));

        var other = await Plan(Pack("q", [Unit("view", "each view")]));
        Assert.Equal(["monthly_totals", "open_ledger", "scratch"], Names(other, "view"));
    }

    [Theory]
    [InlineData("each view", "tags", "finance", "open_ledger")]
    [InlineData("each view", "notTags", "finance", "monthly_totals")]
    [InlineData("each view", "stereotypes", "ledger", "open_ledger")]
    [InlineData("each view", "categories", "Receivables", "open_ledger")]
    [InlineData("each view", "database", "reporting", "monthly_totals")]
    [InlineData("each sequence", "database", "main", "counters_seq,posting_seq")]
    [InlineData("each sequence", "tags", "finance", "posting_seq")]
    public async Task Where_filters_views_and_sequences_by_their_own_files(string scope, string filter, string value, string expected)
    {
        var where = filter switch
        {
            "tags" => new UnitWhere { Tags = [value] },
            "notTags" => new UnitWhere { NotTags = [value] },
            "stereotypes" => new UnitWhere { Stereotypes = [value] },
            "categories" => new UnitWhere { Categories = [value] },
            _ => new UnitWhere { Database = value },
        };
        var plan = await Plan(Pack("p", [Unit("u", scope, where)]));

        Assert.Empty(plan.Diagnostics);
        Assert.Equal(expected, string.Join(",", Names(plan, "u")));
    }

    [Fact]
    public void Explain_names_the_hint_and_the_filter()
    {
        var pack = Pack("p", [Unit("view", "each view"), Unit("tagged", "each view", new UnitWhere { Tags = ["finance"] })]);
        var views = Model.Databases.SelectMany(d => d.Views).ToDictionary(v => v.Name, StringComparer.Ordinal);

        Assert.Equal("skip-hint", UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[0], views["scratch"], new ScriptSandboxFactory(), Ct)?.Kind);
        Assert.Null(UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[0], views["open_ledger"], new ScriptSandboxFactory(), Ct));
        var filtered = UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[1], views["monthly_totals"], new ScriptSandboxFactory(), Ct);
        Assert.Equal("filter", filtered?.Kind);
        Assert.Contains("where.tags: finance", filtered?.Detail, StringComparison.Ordinal);
        var sequence = Model.Databases.SelectMany(d => d.Sequences).First();
        Assert.Equal("scope", UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[0], sequence, new ScriptSandboxFactory(), Ct)?.Kind);
    }

    private static IReadOnlyList<string> Names(UnitPlan plan, string unit) =>
        [.. plan.Units.Where(u => u.Unit.Id == unit).Select(u => u.Element switch { RView v => v.Name, RSequence s => s.Name, _ => "?" }).Order(StringComparer.Ordinal)];

    private static Task<UnitPlan> Plan(LoadedPack pack) =>
        new UnitPlanner(Options).PlanAsync(Model, new PackSet([pack], []), new ScriptSandboxFactory(), null, Ct);
}
