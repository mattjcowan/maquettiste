using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>Snapshots with calculation files and their resolutions (next-phase-2-design.md section 5.5).</summary>
internal sealed class CalculationKit(int parallelism = 1)
{
    public const string Folder = ".maquettiste/extensions/calculated/";

    public CountingSandboxFactory Factory { get; } = new();

    public CalculationService Service => _service ??= new CalculationService(Factory, Parallelism);

    private CalculationService? _service;

    public int Parallelism { get; } = parallelism;

    /// <summary>Invoice, InvoiceLine and Customer, each with a key; ids are the same for the same seed.</summary>
    public static ModelBuilder Billing(string invoice = "Invoice", bool line = true, Func<ProjectSettings, ProjectSettings>? settings = null)
    {
        var builder = new ModelBuilder();
        builder.Entity(invoice).Key("id", "uuid");
        builder.Entity("Customer").Key("id", "uuid");
        if (line)
            builder.Entity("InvoiceLine").Key("id", "uuid"); // last, so leaving it out keeps the other ids
        if (settings is not null)
            builder.Settings(settings);
        return builder;
    }

    public static ScriptSource File(string name, string code) => new(Folder + name, code, ContentHash.Of(code));

    public static ModelSnapshot Snapshot(ModelBuilder builder, params ScriptSource[] files)
    {
        var basic = builder.Build();
        return ModelSnapshot.Create(basic.Documents, basic.Settings, basic.SettingsHash, [], [], 1, calculationScripts: files);
    }

    public ModelResolver Resolver() =>
        new(ResolutionKit.Options with { MaxDegreeOfParallelism = Parallelism }, Factory, null, Service);

    public ResolvedModel Resolve(ModelSnapshot snapshot) =>
        Resolver().ResolveAsync(snapshot, null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
}
