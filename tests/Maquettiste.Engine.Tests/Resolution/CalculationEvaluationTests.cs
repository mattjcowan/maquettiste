using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Scripting;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>Evaluation and sites of calculated values (next-phase-2-design.md section 5.5, F51).</summary>
public sealed class CalculationEvaluationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ScriptSource Names = CalculationKit.File("names.js", """
        function kebab(text) {
          return text.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
        }

        maquettiste.calculated({
          name: "publicName",
          kinds: ["entity"],
          schema: { type: "string", pattern: "^[a-z][a-z0-9-]*$" },
          description: "The name clients see in URLs and payloads.",
          value(entity) {
            return kebab(entity.pluralName);
          },
        });
        """);

    private static CalculatedValueException Fails(Func<object?> read) => Assert.Throws<CalculatedValueException>(read);

    [Fact]
    public void A_calculated_value_is_evaluated_once_per_object_and_reused()
    {
        var kit = new CalculationKit();
        var model = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(), Names));

        Assert.Equal(0, kit.Factory.CalculationCalls); // resolution evaluates nothing
        Assert.Equal("invoices", model.Entity("Invoice").Calculated["publicName"]);
        Assert.Equal("invoices", model.Entity("Invoice").Calculated["publicName"]);
        Assert.Equal("invoice-lines", model.Entity("InvoiceLine").Calculated["publicName"]);
        Assert.Equal("customers", model.Entity("Customer").Calculated["publicName"]);
        Assert.Equal(3, kit.Factory.CalculationCalls);
        Assert.Equal(1, kit.Service.PoolsCreated);
        Assert.Equal(["publicName"], model.Entity("Invoice").Calculated.Names);
        Assert.True(model.Entity("Invoice").Calculated.Contains("publicName"));
        Assert.Empty(model.Entities[0].Attributes[0].Calculated.Names);
    }

    [Fact]
    public void A_calculated_value_may_read_another_calculated_value_and_records_its_keys()
    {
        var kit = new CalculationKit();
        var route = CalculationKit.File("routes.js", """
            maquettiste.calculated({ name: "route", kinds: ["entity"], schema: { type: "string" },
              value(entity) { return "/api/" + entity.calculated.publicName; } });
            """);
        var model = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(), Names, route));
        var invoice = model.Entity("Invoice");
        var reads = new ListRecorder();

        var value = invoice.Calculated.Read("route", reads, Ct);

        Assert.Equal("/api/invoices", value);
        Assert.Contains("x:calculated/names.js", reads.Keys);
        Assert.Contains("x:calculated/routes.js", reads.Keys);
        Assert.Contains("e:" + invoice.Id, reads.Keys);
        Assert.Contains("cv:entity", reads.Keys);
        Assert.Equal(2, kit.Factory.CalculationCalls);
        Assert.Equal("invoices", invoice.Calculated["publicName"]); // the inner value was memoized on the way
        Assert.Equal(2, kit.Factory.CalculationCalls);
    }

    [Fact]
    public void Calculated_values_that_read_each_other_are_one_MQ5013_with_the_same_chain_whatever_the_order()
    {
        var cycle = CalculationKit.File("cycle.js", """
            maquettiste.calculated({ name: "a", kinds: ["entity"], schema: {}, value(e) { return e.calculated.b; } });
            maquettiste.calculated({ name: "b", kinds: ["entity"], schema: {}, value(e) {
              try { return e.calculated.a; } catch (err) { return "caught"; } } });
            """);
        var snapshot = CalculationKit.Snapshot(CalculationKit.Billing(), cycle);

        var first = new CalculationKit().Resolve(snapshot).Entity("Invoice");
        var fromA = Fails(() => first.Calculated["a"]).Diagnostic;
        var fromB = Fails(() => first.Calculated["b"]).Diagnostic;
        var second = new CalculationKit().Resolve(snapshot).Entity("Invoice");
        var startingAtB = Fails(() => second.Calculated["b"]).Diagnostic;

        Assert.Equal("MQ5013", fromA.Rule);
        Assert.Equal(fromA, fromB);
        Assert.Equal(fromA, startingAtB);
        Assert.Equal(first.Id, fromA.ElementId);
        Assert.Equal((CalculationKit.Folder + "cycle.js", 1), (fromA.FilePath, fromA.Line));
        Assert.Contains($"a of entity Invoice ({first.Id}) -> b of entity Invoice ({first.Id}) -> a of", fromA.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_fails_its_schema_is_MQ5011()
    {
        var kit = new CalculationKit();
        var file = CalculationKit.File("codes.js", "\nmaquettiste.calculated({ name: 'code', kinds: ['entity'], schema: { type: 'integer', maximum: 5 },\n  value() { return 9; } });");
        var invoice = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(), file)).Entity("Invoice");

        var failure = Fails(() => invoice.Calculated["code"]).Diagnostic;

        Assert.Equal("MQ5011", failure.Rule);
        Assert.Equal((CalculationKit.Folder + "codes.js", 2, invoice.Id), (failure.FilePath, failure.Line, failure.ElementId));
        Assert.Contains("maximum", failure.Message, StringComparison.Ordinal);
        Assert.Contains("The value was 9", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_throw_or_a_limit_is_MQ5010_with_the_line_and_object()
    {
        var kit = new CalculationKit();
        var file = CalculationKit.File("bad.js", """
            maquettiste.calculated({ name: "thrown", kinds: ["entity"], schema: {}, value(e) {
              throw new Error("no " + e.name); } });
            maquettiste.calculated({ name: "endless", kinds: ["entity"], schema: {}, value() {
              for (;;) {} } });
            """);
        var invoice = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(settings: s => s with { Limits = s.Limits with { ScriptStatements = 10_000 } }), file))
            .Entity("Invoice");

        var thrown = Fails(() => invoice.Calculated["thrown"]).Diagnostic;
        var limit = Fails(() => invoice.Calculated["endless"]).Diagnostic;

        Assert.Equal(("MQ5010", 2, invoice.Id), (thrown.Rule, thrown.Line, thrown.ElementId));
        Assert.Contains("no Invoice", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(("MQ5010", invoice.Id), (limit.Rule, limit.ElementId));
        Assert.Contains("statement limit", limit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_is_not_json_is_MQ5010()
    {
        var kit = new CalculationKit();
        var file = CalculationKit.File("shapes.js", """
            maquettiste.calculated({ name: "self", kinds: ["entity"], schema: {}, value(e) { return e; } });
            maquettiste.calculated({ name: "fn", kinds: ["entity"], schema: {}, value() { return { f() {} }; } });
            maquettiste.calculated({ name: "nan", kinds: ["entity"], schema: {}, value() { return [NaN]; } });
            maquettiste.calculated({ name: "fine", kinds: ["entity"], schema: {}, value(e) { return { id: e.id, n: [1, 2.5, null, true] }; } });
            """);
        var invoice = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(), file)).Entity("Invoice");

        foreach (var name in new[] { "self", "fn", "nan" })
            Assert.Equal("MQ5010", Fails(() => invoice.Calculated[name]).Diagnostic.Rule);
        var fine = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(invoice.Calculated["fine"]);
        Assert.Equal(invoice.Id, fine["id"]);
        Assert.Equal([1L, 2.5, null, true], Assert.IsAssignableFrom<IReadOnlyList<object?>>(fine["n"]));
    }

    [Fact]
    public void An_undeclared_name_is_MQ5012()
    {
        var kit = new CalculationKit();
        var file = CalculationKit.File("typo.js", """
            maquettiste.calculated({ name: "route", kinds: ["entity"], schema: {}, value(e) {
              return e.calculated.publicNam; } });
            maquettiste.calculated({ name: "tested", kinds: ["entity"], schema: {}, value(e) {
              return ["publicNam" in e.calculated, "route" in e.calculated, Object.keys(e.calculated)]; } });
            """);
        var model = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(), Names, file));
        var invoice = model.Entity("Invoice");

        var direct = Fails(() => invoice.Calculated["publicNam"]).Diagnostic;
        var scripted = Fails(() => invoice.Calculated["route"]).Diagnostic;
        var attribute = Fails(() => invoice.Attributes[0].Calculated["publicName"]).Diagnostic;

        Assert.Equal("MQ5012", direct.Rule);
        Assert.Equal(("MQ5012", CalculationKit.Folder + "typo.js", 2), (scripted.Rule, scripted.FilePath, scripted.Line));
        Assert.Equal("MQ5012", attribute.Rule);
        Assert.Equal([false, true, (object?)new object?[] { "publicName", "route", "tested" }],
            Assert.IsAssignableFrom<IReadOnlyList<object?>>(invoice.Calculated["tested"]));
    }

    [Fact]
    public void A_value_that_read_a_failed_calculated_value_fails_even_when_the_script_caught_it()
    {
        var kit = new CalculationKit();
        var file = CalculationKit.File("chain.js", """
            maquettiste.calculated({ name: "broken", kinds: ["entity"], schema: {}, value() { throw new Error("broken"); } });
            maquettiste.calculated({ name: "careful", kinds: ["entity"], schema: {}, value(e) {
              try { return e.calculated.broken; } catch (err) { return "fallback"; } } });
            """);
        var invoice = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(), file)).Entity("Invoice");

        var careful = Fails(() => invoice.Calculated["careful"]).Diagnostic;

        Assert.Equal(Fails(() => invoice.Calculated["broken"]).Diagnostic, careful);
        Assert.Equal("MQ5010", careful.Rule);
    }

    [Fact]
    public void A_cancelled_read_publishes_nothing()
    {
        var kit = new CalculationKit();
        var invoice = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(), Names)).Entity("Invoice");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => invoice.Calculated.Read("publicName", null, cancelled.Token));

        Assert.Equal("invoices", invoice.Calculated["publicName"]);
        Assert.Equal(1, kit.Factory.CalculationCalls);
    }

    [Fact]
    public void A_nested_read_does_not_count_against_the_readers_memory_limit()
    {
        var kit = new CalculationKit();
        var file = CalculationKit.File("memory.js", """
            function fill() { const items = []; for (let i = 0; i < 20000; i++) items.push({ i, text: "item " + i }); return items.length; }
            maquettiste.calculated({ name: "inner", kinds: ["entity"], schema: {}, value() { return fill(); } });
            maquettiste.calculated({ name: "outer", kinds: ["entity"], schema: {}, value(e) { return e.calculated.inner + fill(); } });
            """);
        var limits = (Func<ProjectSettings, ProjectSettings>)(s => s with { Limits = s.Limits with { ScriptMemoryBytes = 10_000_000 } });
        var invoice = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(settings: limits), file)).Entity("Invoice");

        Assert.Equal(40000L, invoice.Calculated["outer"]);
    }

    [Fact]
    public void Virtual_attributes_of_two_entities_have_their_own_values()
    {
        var builder = new ModelBuilder();
        builder.Stereotype("audited").Attr("createdAt", "timestamp");
        builder.Entity("Invoice").Key("id", "uuid").Stereotype("audited");
        builder.Entity("Customer").Key("id", "uuid").Stereotype("audited");
        var file = CalculationKit.File("columns.js", """
            maquettiste.calculated({ name: "label", kinds: ["attribute"], schema: { type: "string" },
              value(a) { return a.owner.name + "." + a.name; } });
            """);
        var kit = new CalculationKit();
        var snapshot = CalculationKit.Snapshot(builder, file);
        var model = kit.Resolve(snapshot);
        var invoice = model.Entity("Invoice").Attributes.Single(a => a.Name == "createdAt");
        var customer = model.Entity("Customer").Attributes.Single(a => a.Name == "createdAt");

        Assert.Equal(invoice.Id, customer.Id);
        Assert.Equal("Invoice.createdAt", invoice.Calculated["label"]);
        Assert.Equal("Customer.createdAt", customer.Calculated["label"]);
        Assert.Equal(model.Entity("Invoice").Id + "/" + invoice.Id, ResolvedKinds.SiteOf(invoice, snapshot));
        var key = model.Entity("Invoice").Attributes.Single(a => a.Name == "id");
        Assert.Equal(key.Id, ResolvedKinds.SiteOf(key, snapshot));
    }

    [Fact]
    public void Values_are_byte_identical_across_runs_and_parallelism_with_the_clock_off()
    {
        var file = CalculationKit.File("noise.js", """
            maquettiste.calculated({ name: "noise", kinds: ["entity", "attribute"], schema: {},
              value(o) { return { r: Math.random(), d: new Date().toISOString(), id: o.id }; } });
            """);
        var snapshot = CalculationKit.Snapshot(CalculationKit.Billing(settings: s => s with { Limits = s.Limits with { ScriptTimeoutMs = 0 } }),
            Names, file);

        string Run(int parallelism)
        {
            var kit = new CalculationKit(parallelism);
            var model = kit.Resolve(snapshot);
            Assert.Empty(model.Calculated!.EvaluateAll(Ct));
            return string.Join("\n", ResolvedKinds.ObjectsOf(model, ["attribute", "entity"]).SelectMany(o => o.Calculated.Names.Select(n =>
                o.Kind + " " + ResolvedKinds.SiteOf(o, snapshot) + " " + n + " " +
                Maquettiste.Engine.Scripting.ScriptSandbox.CalculationJson(o.Calculated[n]).GetRawText())));
        }

        var one = Run(1);
        Assert.Equal(one, Run(4));
        Assert.Equal(one, Run(1));
        Assert.Contains("entity", one, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_database_runs_carry_no_calculated_values()
    {
        var builder = CalculationKit.Billing();
        var db = builder.Database("Main", Dialect.PostgreSql);
        var file = CalculationKit.File("db.js", "maquettiste.calculated({ name: 'tag', kinds: ['database', 'entity'], schema: {}, value(d) { return d.name; } });");
        var kit = new CalculationKit();
        var snapshot = CalculationKit.Snapshot(builder, file);

        var (databases, _) = await kit.Resolver().ResolveDatabasesAsync(snapshot, [db.Id], false, Ct);

        Assert.Same(RCalculated.Empty, databases[0]!.Calculated);
        Assert.Equal(0, kit.Service.PoolsCreated);
        Assert.Equal("Main", kit.Resolve(snapshot).Databases[0].Calculated["tag"]);
    }
}
