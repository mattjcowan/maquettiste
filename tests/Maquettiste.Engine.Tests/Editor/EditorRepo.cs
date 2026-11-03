using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// A temporary copy of the billing fixture with the two example packs (<c>packs/sql-ddl</c>, <c>packs/csharp-dapper</c>) under
/// <c>templates/</c>, driven only through the public API the editor's functions use: <c>new ModelStore(options)</c> and
/// <c>new GenerationService(store, options)</c>.
/// </summary>
internal sealed class EditorRepo : IAsyncDisposable
{
    public const string InvoiceId = "01J92P0V0FJ23CGSNKM7P1W5V7";
    public const string CustomerId = "01J92P0V0ETQKXXP951CMMNHH3";
    public const string PaymentId = "01J92P0V0HEGSC6MW92CST5KA6";
    public const string BillingPackageId = "01J92P0V01KDRN8GX5PGYCNKSX";
    public const string BillingCategoryId = "01J92P0V06TYE8P8990AV35A8K";
    public const string MainDatabaseId = "01J92P0V1QRN2181XM2ZWE02W4";
    public const string PlacesRelationId = "01J92P0V1ACKN3G6TK3NJDTM82";
    public const string OverlayTableId = "01J92P0V1T0J6RH4MY9H81NYB4";
    public const string SettingsPath = ".maquettiste/maquettiste.json";

    private EditorRepo(TempRepo repo)
    {
        Repo = repo;
        Store = new ModelStore(repo.Options);
        Service = new GenerationService(Store, repo.Options);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TempRepo Repo { get; }

    public ModelStore Store { get; }

    public GenerationService Service { get; }

    /// <summary>Copies the billing fixture and, unless <paramref name="packs"/> is false, the example packs.</summary>
    public static EditorRepo Create(bool packs = true)
    {
        var repo = new TempRepo();
        CopyTree(Fixtures.Path("models", "billing"), repo.RepoRoot);
        if (packs)
        {
            foreach (var name in new[] { "sql-ddl", "csharp-dapper" })
                CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", name), Path.Combine(repo.ModelRoot, "templates", name));
            TestLimits.ForPreviews(repo.ModelRoot);
        }

        return new EditorRepo(repo);
    }

    /// <summary>
    /// Writes two packs beside the copied ones: <c>naming</c>, a library (no units, a <c>class_name</c> helper with the parameter
    /// <c>suffix</c>, and <c>partials/header.scriban</c>), and <c>records</c>, one <c>each entity</c> unit <c>record</c> that uses
    /// <c>naming</c> <c>^1.2.0</c>, includes its header and names its file with <c>naming.class_name</c>.
    /// </summary>
    public void WriteLibraryPacks()
    {
        void Write(string pack, string path, string text)
        {
            var full = Repo.PathOf(".maquettiste/templates/" + pack + "/" + path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        Write("naming", "pack.json", """{ "name": "naming", "version": "1.4.0", "engine": ">=1.0", "parameters": { "suffix": "Record" } }""");
        Write("naming", "helpers.js", """
            function pascal(s) { return String(s).replace(/(^|[-_ ])([a-z])/g, (_, __, c) => c.toUpperCase()); }
            maquettiste.helper('class_name', e => pascal(e.name) + maquettiste.params.suffix);
            """);
        Write("naming", "partials/header.scriban", "// {{ element.name }} ({{ naming.class_name element }})\n");
        Write("records", "pack.json", """
            { "name": "records", "version": "1.0.0", "engine": ">=1.0", "uses": [ { "pack": "naming", "version": "^1.2.0" } ],
              "units": [ { "id": "record", "template": "record.scriban", "for": "each entity", "output": "src/Generated/records/{{ naming.class_name entity }}.cs" } ] }
            """);
        Write("records", "record.scriban", "{{ include \"naming:partials/header.scriban\" }}class {{ entity | naming.class_name }}\n");
    }

    /// <summary>Rewrites <c>maquettiste.json</c> on disk (canonically), behind the store's back.</summary>
    public void EditSettingsOnDisk(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllBytes(Repo.PathOf(SettingsPath)))!.AsObject();
        change(node);
        File.WriteAllBytes(Repo.PathOf(SettingsPath), TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));
    }

    /// <summary>The settings file with a change applied, compact (not canonical), as the editor would send it.</summary>
    public byte[] SettingsWith(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllBytes(Repo.PathOf(SettingsPath)))!.AsObject();
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    /// <summary>Every file under the repo root with its bytes as hex (the engine's caches live outside it).</summary>
    public SortedDictionary<string, string> Files()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Repo.ListFiles())
            files[path] = Convert.ToHexString(File.ReadAllBytes(Repo.PathOf(path)));
        return files;
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Repo.Dispose();
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
