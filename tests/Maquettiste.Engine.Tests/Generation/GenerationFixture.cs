using System.Text;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// A temporary repo with a model written by a <see cref="ModelBuilder"/>, fixture packs copied from <c>tests/fixtures/packs</c>, and
/// the real engine (loader, validator, resolver, planner, post-processor, writer) around a <see cref="FakeRenderer"/>. Output roots:
/// <c>out</c> and <c>gen</c>.
/// </summary>
internal sealed class GenerationFixture : IAsyncDisposable
{
    private GenerationFixture(TempRepo repo, Func<EngineServices, EngineServices>? services = null)
    {
        Repo = repo;
        Services = EngineServices.Create(repo.Options) with { RendererFactory = () => Renderer };
        if (services is not null)
            Services = services(Services);
        Store = new ModelStore(repo.Options, Services);
        Service = new GenerationService(Store, repo.Options, Services);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TempRepo Repo { get; }

    public FakeRenderer Renderer { get; set; } = new();

    public EngineServices Services { get; }

    public ModelStore Store { get; }

    public GenerationService Service { get; }

    /// <summary>Creates a fixture: writes the model and copies the packs.</summary>
    /// <param name="model">Builds the model (the output roots are added).</param>
    /// <param name="packs">Fixture pack names.</param>
    /// <returns>The fixture.</returns>
    public static Task<GenerationFixture> CreateAsync(Action<ModelBuilder> model, params string[] packs) => CreateAsync(model, null, packs);

    /// <summary>Creates a fixture whose engine services are changed first (a stage replaced by a test double).</summary>
    /// <param name="model">Builds the model (the output roots are added).</param>
    /// <param name="services">Changes the engine services.</param>
    /// <param name="packs">Fixture pack names.</param>
    /// <returns>The fixture.</returns>
    public static async Task<GenerationFixture> CreateAsync(Action<ModelBuilder> model, Func<EngineServices, EngineServices>? services, params string[] packs)
    {
        var fixture = new GenerationFixture(new TempRepo(), services);
        await fixture.WriteModelAsync(model);
        foreach (var pack in packs)
            fixture.CopyPack(pack);
        return fixture;
    }

    /// <summary>Writes a model (a builder with the same seed and construction order gives the same ids).</summary>
    /// <param name="model">Builds the model.</param>
    /// <param name="settings">Changes the settings after the output roots are set.</param>
    /// <returns>A task.</returns>
    public async Task WriteModelAsync(Action<ModelBuilder> model, Func<ProjectSettings, ProjectSettings>? settings = null)
    {
        var builder = new ModelBuilder(seed: 7);
        builder.Settings(s => s with
        {
            Outputs = new OutputSettings { Allow = [new OutputRoot { Path = "out" }, new OutputRoot { Path = "gen" }] },
        });
        if (settings is not null)
            builder.Settings(settings);
        model(builder);
        await builder.WriteToAsync(Repo.ModelRoot, Ct);
    }

    /// <summary>Copies a fixture pack into <c>.maquettiste/templates/&lt;name&gt;/</c>.</summary>
    /// <param name="name">The pack.</param>
    /// <param name="as">The name to install it under (its pack.json name is rewritten).</param>
    public void CopyPack(string name, string? @as = null)
    {
        var source = Fixtures.Path("packs", name);
        var target = Path.Combine(Repo.ModelRoot, "templates", @as ?? name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        if (@as is not null)
        {
            var packFile = Path.Combine(target, "pack.json");
            File.WriteAllText(packFile, File.ReadAllText(packFile).Replace($"\"name\": \"{name}\"", $"\"name\": \"{@as}\"", StringComparison.Ordinal),
                new UTF8Encoding(false));
        }
    }

    /// <summary>Writes a pack file (relative to the pack folder).</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="path">The pack-relative path.</param>
    /// <param name="text">The content.</param>
    public void WritePackFile(string pack, string path, string text) =>
        Repo.WriteFile(".maquettiste/templates/" + pack + "/" + path, text);

    /// <summary>Runs generation.</summary>
    /// <param name="mode">The mode.</param>
    /// <param name="force">Whether to ignore the unit state.</param>
    /// <param name="handEdits">A hand-edit policy.</param>
    /// <param name="packs">A pack filter.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="ct">Cancellation (the test's by default).</param>
    /// <returns>The result.</returns>
    public Task<GenerationResult> RunAsync(GenerationMode mode = GenerationMode.Apply, bool force = false, HandEditPolicy? handEdits = null,
        IReadOnlyList<string>? packs = null, IProgress<ProgressUpdate>? progress = null, CancellationToken? ct = null) =>
        Service.RunAsync(new GenerationRequest { Mode = mode, Force = force, HandEdits = handEdits, Packs = packs, Jobs = 2, IncludeDiffs = mode == GenerationMode.DryRun },
            progress, ct ?? Ct);

    /// <summary>Every file under the output roots with its content.</summary>
    /// <returns>Path → text, ordinal.</returns>
    public SortedDictionary<string, string> Outputs()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Repo.ListFiles().Where(p => p.StartsWith("out/", StringComparison.Ordinal) || p.StartsWith("gen/", StringComparison.Ordinal)))
            files[path] = Repo.ReadFile(path);
        return files;
    }

    /// <summary>The manifest of a pack (<c>.maquettiste/manifest/</c>), or empty.</summary>
    /// <param name="pack">The pack.</param>
    /// <returns>The text.</returns>
    public string CommittedManifest(string pack) =>
        Repo.Exists(".maquettiste/manifest/" + pack + ".json") ? Repo.ReadFile(".maquettiste/manifest/" + pack + ".json") : "";

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        await Services.Journal.DisposeAsync();
        Repo.Dispose();
    }
}

/// <summary>Model shapes shared by the tests.</summary>
internal static class Models
{
    /// <summary>Two unrelated entities, <c>Customer</c> and <c>Product</c>, and a PostgreSQL database <c>main</c>.</summary>
    /// <param name="b">The builder.</param>
    /// <param name="customerName">The type of Customer's <c>name</c> attribute can change through this.</param>
    /// <param name="extra">More elements, added after these.</param>
    public static void Shop(ModelBuilder b, string customerName = "string", Action<ModelBuilder, EntityBuilder, DatabaseBuilder>? extra = null)
    {
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("name", customerName);
        b.Entity("Product").Key("id", "uuid").Attr("title", "string");
        var main = b.Database("main", Dialect.PostgreSql);
        extra?.Invoke(b, customer, main);
    }
}
