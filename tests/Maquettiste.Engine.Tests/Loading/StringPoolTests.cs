using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Text;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Loading;

/// <summary>
/// A load keeps one string per distinct id and pointer (Loading/README.md, memory notes): an element's id, the references to it and the
/// index share one instance, before and after an edit, and the pool stays bounded.
/// </summary>
public sealed class StringPoolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_pool_hands_out_one_instance_per_value()
    {
        var pool = new StringPool();
        var first = pool.Intern(new string('a', 3));
        Assert.Same(first, pool.Intern(new string('a', 3)));
        Assert.Same(first, pool.Intern("aaa".AsSpan()));
        Assert.Same(first, pool.InternAscii("aaa"u8));
        Assert.Null(pool.InternAscii("é"u8));
        Assert.Equal(1, pool.Count);
        pool.Reset();
        Assert.Equal(0, pool.Count);
        Assert.NotSame(first, pool.Intern(new string('a', 3)));
    }

    [Fact]
    public async Task Ids_and_pointers_are_one_instance_per_value_across_files_and_after_an_edit()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        var loader = h.NewLoader();
        var model = (await loader.LoadAsync(new LoadRequest(null, null, false), null, Ct)).Snapshot;
        AssertShared(model);

        // An edit: the changed file's ids and pointers are the instances the unchanged files and the previous index hold.
        var invoice = model.GetDocument(Billing.IdOf(model, "entity", "Invoice"))!;
        var path = invoice.Path;
        var node = JsonNode.Parse(File.ReadAllText(h.Repo.PathOf(path)))!.AsObject();
        node["description"] = "Changed.";
        File.WriteAllText(h.Repo.PathOf(path), node.ToJsonString());
        var edited = (await loader.LoadAsync(new LoadRequest(model, [h.Repo.PathOf(path)], false), null, Ct)).Snapshot;
        Assert.NotSame(model, edited);
        AssertShared(edited);
        var before = model.GetDocument(invoice.Element.Id)!.Element;
        var after = edited.GetDocument(invoice.Element.Id)!.Element;
        Assert.NotSame(before, after);
        Assert.Same(before.Id, after.Id);
        Assert.True(loader.Strings.Count > 0);
    }

    private static void AssertShared(ModelSnapshot model)
    {
        var index = model.Index;
        var references = index.ReferencesFrom.Values.SelectMany(r => r).ToList();
        Assert.NotEmpty(references);

        // Every reference to an indexed id holds the id's own instance (the element's or sub-element's).
        var resolved = 0;
        foreach (var reference in references)
        {
            if (index.Entries.TryGetValue(reference.ToId, out var entry))
            {
                Assert.Same(entry.Id, reference.ToId);
                resolved++;
            }
        }

        Assert.True(resolved > 0);

        // Every element's id is its index entry's, and every pointer value is one instance.
        foreach (var document in model.Documents)
            Assert.Same(index.Entries[document.Element.Id].Id, document.Element.Id);
        var pointers = index.Entries.Values.Select(e => e.JsonPointer).Concat(references.Select(r => r.JsonPointer)).Where(p => p.Length > 0);
        foreach (var group in pointers.GroupBy(p => p, StringComparer.Ordinal))
            Assert.Single(group.Distinct(ReferenceEqualityComparer.Instance));
    }
}
