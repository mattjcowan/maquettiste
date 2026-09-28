using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>A temp copy of the billing fixture with a loaded store over a real loader, a fake policy and a fake validator.</summary>
internal sealed class BillingStore : IAsyncDisposable
{
    private BillingStore(LoaderHarness harness, ModelStore store)
    {
        Harness = harness;
        Store = store;
    }

    public LoaderHarness Harness { get; }

    public ModelStore Store { get; }

    public List<ChangeSet> Notifications { get; } = [];

    public ModelSnapshot Model => Store.Current!;

    public static async Task<BillingStore> OpenAsync(bool load = true)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "billing");
        var store = harness.NewStore();
        var result = new BillingStore(harness, store);
        store.OnChanged((changes, _) =>
        {
            lock (result.Notifications)
                result.Notifications.Add(changes);
            return ValueTask.CompletedTask;
        });
        if (load)
            await store.LoadAsync(TestContext.Current.CancellationToken);
        return result;
    }

    public string Id(string kind, string name) => Billing.IdOf(Model, kind, name);

    public ElementDocument Doc(string kind, string name) => Model.GetDocument(Id(kind, name))!;

    /// <summary>The element's JSON after a change, compact (not canonical), as an editor would send it.</summary>
    public static byte[] Edit(ElementDocument document, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        change(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    /// <summary>Every file under the model root with its bytes, for all-or-nothing checks.</summary>
    public SortedDictionary<string, string> Files()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Harness.Repo.ModelRoot, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(Harness.Repo.ModelRoot, file).Replace('\\', '/')] = Convert.ToHexString(File.ReadAllBytes(file));
        return files;
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Harness.Dispose();
    }
}
