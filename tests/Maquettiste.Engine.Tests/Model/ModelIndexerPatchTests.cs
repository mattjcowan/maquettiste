using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// A snapshot built after a previous one patches the previous indexes when few documents changed
/// (<see cref="ModelSnapshot.CreateAfter"/>); it must equal a snapshot built from scratch, map by map and in order. Checked over
/// chains of seeded random edits: changed, renamed, added and removed documents, moved sub-elements, stereotype and id collisions
/// (which fall back to a full build).
/// </summary>
public sealed class ModelIndexerPatchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Patched_indexes_equal_a_full_build_over_random_edits(int seed)
    {
        var random = new Random(seed);
        var (documents, ids) = Model(seed);
        var previous = Full(documents);
        var patchedCount = 0;
        for (var step = 0; step < 60; step++)
        {
            // A duplicate id from the previous step is removed again (it forces full builds while it stays).
            documents = [.. documents.Where(d => !d.Path.EndsWith(".dup", StringComparison.Ordinal))];
            var edits = 1 + random.Next(random.Next(4) == 0 ? 12 : 3);
            for (var e = 0; e < edits; e++)
                documents = Edit(documents, random, ids);
            var full = Full(documents);
            var after = ModelSnapshot.CreateAfter(previous, documents, previous.Settings, "", [], [], step + 2, null, 1 + (step % 3), TestContext.Current.CancellationToken);
            AssertSameIndex(full, after, $"seed {seed} step {step}");
            if (after.Index.Patched)
                patchedCount++;
            previous = after; // chains: each patch builds on the last one
        }

        Assert.True(patchedCount > 25, "patched " + patchedCount + " of 60");
    }

    [Fact]
    public void Unchanged_documents_reuse_the_previous_indexes()
    {
        var (documents, _) = Model(9);
        var previous = Full(documents);
        var after = ModelSnapshot.CreateAfter(previous, documents, previous.Settings, "", [], [], 2, null, 2, TestContext.Current.CancellationToken);
        AssertSameIndex(Full(documents), after, "unchanged");
        Assert.Same(previous.Index.Entries, after.Index.Entries);
    }

    private static ModelSnapshot Full(IReadOnlyList<ElementDocument> documents) =>
        ModelSnapshot.Create(documents, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);

    private static (IReadOnlyList<ElementDocument> Documents, List<string> Ids) Model(int seed)
    {
        var b = new ModelBuilder(seed);
        var root = b.Package("Root");
        var packages = new[] { b.Package("Billing", root), b.Package("Sales", root), b.Package("Stock") };
        b.Stereotype("audited").Attr("createdAt", "datetimeoffset", a => a.Required());
        b.Stereotype("soft-delete").Attr("deletedAt", "datetimeoffset");
        var status = b.Enum("Status", packages[0]).Member("Draft", 0).Member("Issued", 1);
        var entities = new List<EntityBuilder>();
        for (var i = 0; i < 24; i++)
        {
            var e = b.Entity("Thing" + i, packages[i % 3]).Key("id", "uuid", IdentityStrategy.UuidV7).Attr("name", "string", a => a.Length(40))
                .Attr("status", status);
            if (i % 3 == 0)
                e.Stereotype("audited");
            if (i % 5 == 0)
                e.Stereotype("soft-delete");
            entities.Add(e);
        }

        for (var i = 0; i < 30; i++)
        {
            var from = entities[i % entities.Count];
            var to = entities[(i * 7 + 3) % entities.Count];
            if (ReferenceEquals(from, to))
                continue;
            b.Relation("rel" + i, from, to, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "r" + i, toNavigation: "n" + i);
        }

        var main = b.Database("main", Dialect.PostgreSql);
        b.Mapping(main, entities[1]).Storage("status", StorageKind.String);
        var documents = b.BuildDocuments().OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        var ids = documents.Select(d => d.Element.Id).ToList();
        return (documents, ids);
    }

    private static IReadOnlyList<ElementDocument> Edit(IReadOnlyList<ElementDocument> documents, Random random, List<string> ids)
    {
        var list = documents.ToList();
        var index = random.Next(list.Count);
        var doc = list[index];
        switch (random.Next(9))
        {
            case 0: // content change, same path (a new document instance)
                list[index] = doc with { Element = doc.Element with { Name = doc.Element.Name + "x" }, Hash = doc.Hash + "1" };
                break;
            case 1 when doc.Element is Entity entity && list.Count > 4: // move an attribute to another entity (sub-element ids move)
            {
                var other = list.FindIndex(d => d.Element is Entity && !ReferenceEquals(d, doc));
                if (other < 0 || entity.Attributes.Count < 2)
                    break;
                var moved = entity.Attributes[^1];
                var target = (Entity)list[other].Element;
                list[index] = doc with { Element = entity with { Attributes = [.. entity.Attributes.Take(entity.Attributes.Count - 1)] } };
                list[other] = list[other] with { Element = target with { Attributes = [.. target.Attributes, moved] } };
                break;
            }

            case 2: // remove a document
                list.RemoveAt(index);
                break;
            case 3: // add a copy under a new id and path (references the same package, stereotypes, types)
            {
                var id = NewId(random);
                ids.Add(id);
                Element? element = doc.Element is Entity entity
                    ? entity with { Id = id, Attributes = [.. entity.Attributes.Select(a => a with { Id = NewId(random) })] }
                    : doc.Element is Relation relation
                        ? relation with { Id = id, Ends = [.. relation.Ends.Select(end => end with { Id = NewId(random) })] }
                        : null;
                if (element is not null)
                    list.Add(doc with { Element = element, Path = doc.Path + ".copy" + id });
                break;
            }

            case 4 when doc.Element is Entity entity: // change references: stereotypes and package
                list[index] = doc with
                {
                    Element = entity with
                    {
                        Stereotypes = entity.Stereotypes.Count > 0 ? [] : ["audited", "no-such-key"],
                        Package = random.Next(2) == 0 ? null : entity.Package,
                    },
                };
                break;
            case 5 when doc.Element is Relation relation && relation.Ends.Count == 2: // re-point an end
            {
                var targets = list.Where(d => d.Element is Entity).ToList();
                var target = targets[random.Next(targets.Count)].Element.Id;
                list[index] = doc with { Element = relation with { Ends = [relation.Ends[0], relation.Ends[1] with { Entity = target }] } };
                break;
            }

            case 6: // rename the file (same element, new path)
                list[index] = doc with { Path = doc.Path.Replace(".json", ".moved.json", StringComparison.Ordinal) };
                break;
            case 7 when random.Next(3) == 0: // a stereotype changes (full build)
            {
                var s = list.FindIndex(d => d.Element is Stereotype);
                if (s >= 0)
                    list[s] = list[s] with { Element = list[s].Element with { Name = list[s].Element.Name + "s" } };
                break;
            }

            case 8 when random.Next(4) == 0: // an id collision (full build; the first path wins)
                list.Add(doc with { Path = doc.Path + ".dup" });
                break;
        }

        list.Sort((a, c) => string.CompareOrdinal(a.Path, c.Path));
        return list;
    }

    private static string NewId(Random random)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var chars = new char[26];
        chars[0] = '0';
        for (var i = 1; i < chars.Length; i++)
            chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }

    private static void AssertSameIndex(ModelSnapshot expected, ModelSnapshot actual, string label)
    {
        var e = expected.Index;
        var a = actual.Index;
        Assert.True(e.Entries.Count == a.Entries.Count, label + ": entry count");
        foreach (var (id, entry) in e.Entries)
            Assert.True(a.Entries.TryGetValue(id, out var other) && other == entry, $"{label}: entry {id}");
        Assert.Equal(e.DocumentsById.Count, a.DocumentsById.Count);
        foreach (var (id, doc) in e.DocumentsById)
            Assert.True(a.DocumentsById.TryGetValue(id, out var other) && ReferenceEquals(other, doc), $"{label}: document {id}");
        AssertSameGroups(e.ReferencesTo, a.ReferencesTo, label + ": references to");
        AssertSameGroups(e.ReferencesFrom, a.ReferencesFrom, label + ": references from");
        Assert.Equal(e.ByKind.Keys.Order(), a.ByKind.Keys.Order());
        foreach (var (kind, elements) in e.ByKind)
            Assert.True(Enumerable.SequenceEqual<object>(elements, a.ByKind[kind], ReferenceEqualityComparer.Instance), $"{label}: kind {kind}");
        Assert.Equal(e.KindSetHashes.OrderBy(p => p.Key), a.KindSetHashes.OrderBy(p => p.Key));
        Assert.Equal(e.Summaries, a.Summaries);
        Assert.Equal(e.Stereotypes.OrderBy(p => p.Key, StringComparer.Ordinal), a.Stereotypes.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Same(e.Tags, a.Tags);
        Assert.Same(e.Categories, a.Categories);
        Assert.Equal(e.Diagnostics, a.Diagnostics);
        Assert.Equal(expected.Documents, actual.Documents);

        // And through the public surface.
        foreach (var id in e.Entries.Keys)
        {
            Assert.Equal(expected.ReferencesTo(id), actual.ReferencesTo(id));
            Assert.Equal(expected.ReferrersHash(id), actual.ReferrersHash(id));
        }
    }

    private static void AssertSameGroups(IReadOnlyDictionary<string, ReferenceInfo[]> expected, IReadOnlyDictionary<string, ReferenceInfo[]> actual, string label)
    {
        Assert.True(expected.Count == actual.Count, $"{label}: group count {expected.Count} vs {actual.Count}");
        foreach (var (key, group) in expected)
            Assert.True(actual.TryGetValue(key, out var other) && group.SequenceEqual(other), $"{label}: group {key}");
    }
}
