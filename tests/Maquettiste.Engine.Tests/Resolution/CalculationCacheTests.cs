using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>Calculated values cached by the keys they read, across resolutions of a long-lived host (section 5.5, F51).</summary>
public sealed class CalculationCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ScriptSource Names = CalculationKit.File("names.js", """
        maquettiste.calculated({ name: "publicName", kinds: ["entity"], schema: { type: "string" },
          value(entity) { return entity.pluralName.toLowerCase(); } });
        """);

    private static int Evaluate(CalculationKit kit, ModelSnapshot snapshot)
    {
        var before = kit.Factory.CalculationCalls;
        var model = kit.Resolve(snapshot);
        model.Calculated!.EvaluateAll(Ct);
        return kit.Factory.CalculationCalls - before;
    }

    [Fact]
    public void An_unchanged_object_reuses_its_value()
    {
        var kit = new CalculationKit();

        Assert.Equal(3, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), Names)));
        Assert.Equal(0, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), Names)));
        Assert.Equal("invoices", kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(), Names)).Entity("Invoice").Calculated["publicName"]);
        Assert.Equal(3, kit.Service.Cache.Count);
    }

    [Fact]
    public void A_change_to_any_read_key_evaluates_again()
    {
        var kit = new CalculationKit();
        Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), Names));

        // Only the renamed entity's value read a key that changed; the same seed keeps every id.
        Assert.Equal(1, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(invoice: "Bill"), Names)));
        Assert.Equal("bills", kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(invoice: "Bill"), Names)).Entity("Bill").Calculated["publicName"]);
    }

    [Fact]
    public void A_changed_calculation_file_evaluates_again()
    {
        var kit = new CalculationKit();
        Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), Names));
        var changed = CalculationKit.File("names.js", Names.Code.Replace("toLowerCase()", "toUpperCase()", StringComparison.Ordinal));

        Assert.Equal(3, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), changed)));
    }

    [Fact]
    public void Values_of_a_file_with_MQ5005_are_never_cached()
    {
        var kit = new CalculationKit();
        var stateful = CalculationKit.File("names.js", "let calls = 0;\n" + Names.Code);

        Assert.Equal(3, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), stateful)));
        Assert.Equal(3, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), stateful)));
        Assert.Equal(0, kit.Service.Cache.Count);
    }

    [Fact]
    public void Failures_are_not_cached()
    {
        var kit = new CalculationKit();
        var failing = CalculationKit.File("bad.js", """
            maquettiste.calculated({ name: "bad", kinds: ["entity"], schema: {}, value(e) { if (e.name === "Customer") throw new Error("no"); return 1; } });
            """);

        Assert.Equal(3, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), failing)));
        Assert.Equal(1, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), failing)));
        Assert.Equal(2, kit.Service.Cache.Count);
    }

    [Fact]
    public void A_whole_model_run_sweeps_unused_entries()
    {
        var kit = new CalculationKit();
        Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(), Names));
        Assert.Equal(3, kit.Service.Cache.Count);

        // Without InvoiceLine (the same seed keeps the other ids), the run reuses two entries and sweeps the third.
        Assert.Equal(0, Evaluate(kit, CalculationKit.Snapshot(CalculationKit.Billing(line: false), Names)));

        Assert.Equal(2, kit.Service.Cache.Count);
    }
}
