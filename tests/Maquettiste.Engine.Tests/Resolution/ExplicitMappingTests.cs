using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Explicit mapping (D46, errata E24): a database holds the entities its <c>byConvention</c> takes plus the ones a
/// mapping names; a file without the member keeps the original rule (every entity, or its <c>packages</c>). MQ4012 and MQ4013.
/// </summary>
public sealed class ExplicitMappingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ModelBuilder B, PackageBuilder Sales, PackageBuilder Ops) Start()
    {
        var b = new ModelBuilder(63);
        var sales = b.Package("Sales");
        var orders = b.Package("Orders", sales);
        var ops = b.Package("Ops");
        b.Entity("Order", orders).Key("id", "uuid");
        b.Entity("Log", ops).Key("id", "uuid");
        return (b, sales, ops);
    }

    private static string[] Tables(ModelBuilder b, string database) =>
        [.. ResolutionKit.Resolve(b).Db(database).Tables.Select(t => t.Name).Order(StringComparer.Ordinal)];

    private static async Task<IReadOnlyList<Diagnostic>> Diagnostics(ModelSnapshot model, ValidationScope? scope = null) =>
        (await ValidationFixture.Validator().ValidateAsync(model, scope ?? ValidationScope.All, null, Ct)).Diagnostics;

    [Fact]
    public void A_database_without_the_member_takes_every_entity_as_before()
    {
        var (b, _, _) = Start();
        b.Database("main", Dialect.PostgreSql);
        Assert.Equal(["logs", "orders"], Tables(b, "main"));
    }

    [Fact]
    public void A_database_without_the_member_but_with_packages_takes_those_packages_as_before()
    {
        var (b, sales, _) = Start();
        b.Database("main", Dialect.PostgreSql).Packages(sales);
        Assert.Equal(["orders"], Tables(b, "main"));
    }

    [Fact]
    public void None_holds_nothing_and_no_database_is_special()
    {
        var (b, _, _) = Start();
        b.Database("main", Dialect.PostgreSql);
        b.Database("second", Dialect.PostgreSql).ByConvention(ConventionMapping.None);
        Assert.Empty(Tables(b, "second"));
        Assert.Equal(["logs", "orders"], Tables(b, "main"));
    }

    [Fact]
    public void Packages_with_an_empty_list_holds_nothing()
    {
        var (b, _, _) = Start();
        b.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.Packages);
        Assert.Empty(Tables(b, "main"));
    }

    [Fact]
    public void Packages_takes_the_chosen_domains_and_their_sub_packages()
    {
        var (b, sales, _) = Start();
        b.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.Packages).Packages(sales);
        Assert.Equal(["orders"], Tables(b, "main"));
    }

    [Fact]
    public void All_takes_every_entity()
    {
        var (b, _, _) = Start();
        b.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.All);
        Assert.Equal(["logs", "orders"], Tables(b, "main"));
    }

    [Fact]
    public void A_mapping_element_places_an_entity_the_convention_does_not_take_and_ignore_still_removes_one()
    {
        var (b, _, _) = Start();
        var main = b.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.None);
        var log = b.Entity("Audit").Key("id", "uuid");
        b.Mapping(main, log);
        Assert.Equal(["audits"], Tables(b, "main"));

        var (c, sales2, _) = Start();
        var other = c.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.Packages).Packages(sales2);
        var order = c.Entity("Invoice", sales2).Key("id", "uuid");
        c.Mapping(other, order).Ignore();
        Assert.Equal(["orders"], Tables(c, "main"));
    }

    [Fact]
    public async Task An_entity_in_no_database_is_MQ4012_info_and_a_model_without_databases_is_quiet()
    {
        var (b, _, _) = Start();
        Assert.DoesNotContain(await Diagnostics(b.Build()), d => d.Rule == "MQ4012");

        var (c, sales, _) = Start();
        c.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.Packages).Packages(sales);
        var found = Assert.Single(await Diagnostics(c.Build()), d => d.Rule == "MQ4012");
        Assert.Equal(DiagnosticSeverity.Info, found.Severity);
        Assert.Contains("'Log'", found.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ignoring_the_last_database_of_an_entity_reports_MQ4012_from_the_mapping_file_too()
    {
        var b = new ModelBuilder(64);
        var main = b.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.All);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var mapping = b.Mapping(main, customer).Ignore();
        var model = b.Build();
        var whole = Assert.Single(await Diagnostics(model), d => d.Rule == "MQ4012");
        Assert.Contains(whole, await Diagnostics(model, new ValidationScope([mapping.Id], false)));
        Assert.Contains(whole, await Diagnostics(model, new ValidationScope([main.Id], false)));
    }

    [Fact]
    public async Task Packages_that_byConvention_does_not_use_are_MQ4013()
    {
        var (b, sales, _) = Start();
        b.Database("main", Dialect.PostgreSql).ByConvention(ConventionMapping.All).Packages(sales);
        var found = Assert.Single(await Diagnostics(b.Build()), d => d.Rule == "MQ4013");
        Assert.Equal(DiagnosticSeverity.Warning, found.Severity);
        Assert.Equal("/packages", found.JsonPointer);
    }

    [Fact]
    public void The_member_reads_and_writes_as_its_schema_names()
    {
        var database = new Database { Id = "d", Name = "main", Dialect = Dialect.PostgreSql, ByConvention = ConventionMapping.None };
        var json = JsonSerializer.Serialize(database, EngineJson.Options);
        Assert.Contains("\"byConvention\":\"none\"", json, StringComparison.Ordinal);
        Assert.Equal(ConventionMapping.Packages, JsonSerializer.Deserialize<Database>("""{"id":"d","name":"m","dialect":"sqlite","byConvention":"packages"}""", EngineJson.Options)!.ByConvention);
        Assert.Null(JsonSerializer.Deserialize<Database>("""{"id":"d","name":"m","dialect":"sqlite"}""", EngineJson.Options)!.ByConvention);
    }
}
