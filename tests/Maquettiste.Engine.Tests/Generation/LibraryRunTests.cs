using System.Collections.Concurrent;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Rendering;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// Counts the pools, leases and engines of one pack's scripts: how many pools were created and disposed, how many leases were
/// rented and returned, and how many distinct engines the leases handed out.
/// </summary>
internal sealed class CountingScripts(IScriptSandboxFactory inner, string pack) : IScriptSandboxFactory
{
    private readonly ConcurrentDictionary<object, bool> _engines = new(ReferenceEqualityComparer.Instance);
    private int _pools;
    private int _poolsDisposed;
    private int _rented;
    private int _returned;

    public int Pools => Volatile.Read(ref _pools);

    public int PoolsDisposed => Volatile.Read(ref _poolsDisposed);

    public int Rented => Volatile.Read(ref _rented);

    public int Returned => Volatile.Read(ref _returned);

    public int Engines => _engines.Count;

    public IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, SandboxLimits limits, int size, CancellationToken ct) =>
        Wrap(scripts, inner.CreatePool(scripts, limits, size, ct));

    public IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, IReadOnlyList<ScriptLibrary> libraries, SandboxLimits limits, int size,
        CancellationToken ct) =>
        Wrap(scripts, inner.CreatePool(scripts, libraries, limits, size, ct));

    private IScriptSandboxPool Wrap(IReadOnlyList<ScriptSource> scripts, IScriptSandboxPool pool)
    {
        if (!scripts.Any(s => s.Path.Contains("/" + pack + "/", StringComparison.Ordinal)))
            return pool;
        Interlocked.Increment(ref _pools);
        return new Pool(this, pool);
    }

    private sealed class Pool(CountingScripts owner, IScriptSandboxPool inner) : IScriptSandboxPool
    {
        private int _disposed;

        public IReadOnlyList<ScriptRegistration> Registrations => inner.Registrations;

        public IReadOnlyList<Diagnostic> Warnings => inner.Warnings;

        public IScriptSandboxLease Rent()
        {
            var lease = inner.Rent();
            Interlocked.Increment(ref owner._rented);
            owner._engines.TryAdd(lease.Sandbox, true);
            return new Lease(owner, lease);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Increment(ref owner._poolsDisposed);
            inner.Dispose();
        }
    }

    private sealed class Lease(CountingScripts owner, IScriptSandboxLease inner) : IScriptSandboxLease
    {
        private int _returned;

        public IScriptSandbox Sandbox => inner.Sandbox;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                Interlocked.Increment(ref owner._returned);
            inner.Dispose();
        }
    }
}

/// <summary>Incremental runs and pool lifetimes of packs that use a library pack (next-phase-2-design.md sections 5.2 and 5.3).</summary>
public sealed class LibraryRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] BillingEntities =
        ["Account", "CreditNote", "Customer", "Discount", "Invoice", "InvoiceLine", "Payment", "Plan", "Price", "Refund", "Subscription", "TaxRate"];

    private const string NamingUse = """{ "pack": "naming", "version": "^1.2.0" }""";

    /// <summary>The naming library; records exports the class name its helper gives; services reads it.</summary>
    private static async Task<(GenerationFixture Fixture, CountingScripts Scripts)> ExportingAsync(int entities)
    {
        CountingScripts? scripts = null;
        var f = await GenerationFixture.CreateAsync(b =>
        {
            foreach (var name in BillingEntities.Take(entities))
                b.Entity(name).Key("id", "uuid").Attr("code", "string");
        }, s => s with
        {
            Scripts = scripts = new CountingScripts(s.Scripts, "naming"),
            RendererFactory = EngineServices.Create(s.Options).RendererFactory,
        });
        LibraryPacks.Pack(f, "naming", extra: "\"parameters\": { \"suffix\": \"Record\" }");
        f.WritePackFile("naming", "helpers.js", LibraryPacks.NamingHelpers);
        LibraryPacks.Pack(f, "records", LibraryPacks.EntityUnit("record.scriban", "out/records/{{ exports.class_name }}.cs",
            ", \"exports\": { \"class_name\": \"{{ naming.class_name entity }}\" }"), NamingUse);
        f.WritePackFile("records", "record.scriban", "class {{ exports.class_name }}");
        LibraryPacks.Pack(f, "services", """{ "id": "service", "template": "service.scriban", "for": "each entity", "output": "out/services/{{ entity.name }}Service.cs" }""",
            """{ "pack": "records", "version": "^1.0.0" }""");
        f.WritePackFile("services", "service.scriban", """class {{ entity.name }}Service uses {{ (entity | exported "records").class_name }}""");
        return (f, scripts!);
    }

    /// <summary>The naming library, records using it (a unit including its partial, one not), and ledger using nothing.</summary>
    private static async Task<GenerationFixture> IncrementalAsync()
    {
        var f = await LibraryPacks.CreateAsync();
        LibraryPacks.Pack(f, "records", LibraryPacks.EntityUnit("record.scriban", "out/records/{{ naming.class_name entity }}.cs")
            + """, { "id": "note", "template": "note.scriban", "for": "each entity", "output": "out/records/{{ entity.name }}Note.txt" }""",
            NamingUse, "\"parameters\": { \"suffix\": \"Other\" }");
        f.WritePackFile("records", "note.scriban", "// {{ entity.name }}");
        LibraryPacks.Pack(f, "ledger", LibraryPacks.EntityUnit("ledger.scriban", "out/ledger/{{ entity.name }}.txt"));
        f.WritePackFile("ledger", "ledger.scriban", "ledger {{ entity.name }}");
        return f;
    }

    /// <summary>The units a plan renders, as pack/unit per element, ordinal.</summary>
    private static async Task<List<string>> RenderedAsync(GenerationFixture f)
    {
        var plan = (await f.Service.PlanAsync(new GenerationRequest { Mode = GenerationMode.DryRun }, null, Ct)).Plan!;
        return [.. plan.Units.Where(u => !u.Skipped).Select(u => u.Key[..u.Key.IndexOf(':', StringComparison.Ordinal)]).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Editing_a_library_partial_renders_again_only_the_units_that_include_it()
    {
        await using var f = await IncrementalAsync();
        var first = await f.RunAsync();
        Assert.True(first.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(first));
        Assert.Equal(6, first.UnitsRendered);

        f.WritePackFile("naming", "partials/header.scriban", "// v2 {{ element.name }}\n");

        Assert.Equal(["records/record", "records/record"], await RenderedAsync(f));
        var second = await f.RunAsync();
        Assert.True(second.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(second));
        Assert.Equal((2, 4), (second.UnitsRendered, second.UnitsSkipped));
        Assert.Equal("// v2 Customer\nclass CustomerRecord", f.Outputs()["out/records/CustomerRecord.cs"]);
    }

    [Fact]
    public async Task Editing_a_library_helper_renders_again_the_units_of_the_packs_that_use_it_and_nothing_else()
    {
        await using var f = await IncrementalAsync();
        Assert.Equal(6, (await f.RunAsync()).UnitsRendered);

        // A used pack's scripts fold into the static hash of every unit whose pack uses it (next-phase-2-design.md 5.2).
        f.WritePackFile("naming", "helpers.js", LibraryPacks.NamingHelpers.Replace("+ maquettiste.params.suffix", "+ 'Row'", StringComparison.Ordinal));

        Assert.Equal(["records/note", "records/note", "records/record", "records/record"], await RenderedAsync(f));
        var second = await f.RunAsync();
        Assert.True(second.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(second));
        Assert.Equal((4, 2), (second.UnitsRendered, second.UnitsSkipped));
        Assert.Equal("// Customer (CustomerRow)\nclass CustomerRow", f.Outputs()["out/records/CustomerRow.cs"]);
    }

    [Fact]
    public async Task Exporting_units_return_their_library_leases_so_the_pool_reuses_its_engines()
    {
        var (f, scripts) = await ExportingAsync(BillingEntities.Length);
        await using var _ = f;

        // Forced, and records is not run: each record is evaluated once, while a services unit renders, on one of its two workers.
        var result = await f.RunAsync(force: true, packs: ["services"]);

        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.Equal("class InvoiceService uses InvoiceRecord", f.Outputs()["out/services/InvoiceService.cs"]);
        Assert.True(scripts.Rented >= BillingEntities.Length, $"{scripts.Rented} leases");
        Assert.Equal(scripts.Rented, scripts.Returned);
        Assert.InRange(scripts.Engines, 1, 2);
    }

    [Fact]
    public async Task Runs_plans_applies_and_previews_dispose_the_pools_their_export_tables_create()
    {
        var (f, scripts) = await ExportingAsync(2);
        await using var _ = f;

        var run = await f.RunAsync(packs: ["services"]);
        Assert.True(run.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(run));
        Assert.True(scripts.Pools > 0);
        Assert.Equal(scripts.Pools, scripts.PoolsDisposed);

        var plan = (await f.Service.PlanAsync(new GenerationRequest { Mode = GenerationMode.DryRun, Force = true }, null, Ct)).Plan!;
        Assert.Equal(scripts.Pools, scripts.PoolsDisposed);
        Assert.Equal(RunOutcome.Succeeded, (await f.Service.ApplyAsync(plan.Id, null, Ct)).Outcome);
        Assert.Equal(scripts.Pools, scripts.PoolsDisposed);

        var before = scripts.Pools;
        var element = plan.Units.First(u => u.Key.StartsWith("services/service:", StringComparison.Ordinal)).ElementId;
        var preview = await f.Service.PreviewAsync("services", "service", element, null, Ct);
        Assert.Empty(preview.Diagnostics);
        Assert.EndsWith("Record", preview.Files[0].Text, StringComparison.Ordinal);
        Assert.True(scripts.Pools > before);
        Assert.Equal(scripts.Pools, scripts.PoolsDisposed);
        Assert.Equal(scripts.Rented, scripts.Returned);
    }
}
