using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// A snapshot built after a previous one patches the previous indexes when few documents changed
/// (<see cref="ModelSnapshot.CreateAfter"/>); it must equal a snapshot built from scratch, map by map and in order. Checked over
/// chains of seeded random edits: changed, renamed, added and removed documents, moved sub-elements, stereotype and id collisions
/// and extension changes (which fall back to a full build).
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
        IReadOnlyList<ExtensionDocument> extensions = [Typed];
        var previous = Full(documents, extensions);
        var patchedCount = 0;
        var patchedWithRetired = 0;
        for (var step = 0; step < 60; step++)
        {
            // A duplicate id from the previous step is removed again (it forces full builds while it stays).
            documents = [.. documents.Where(d => !d.Path.EndsWith(".dup", StringComparison.Ordinal))];
            var edits = 1 + random.Next(random.Next(4) == 0 ? 12 : 3);
            for (var e = 0; e < edits; e++)
                documents = Edit(documents, random, ids);
            if (random.Next(8) == 0) // an extension changes: the x-ref sites of every document may change (full build)
                extensions = extensions.Count == 0 ? [Typed] : random.Next(2) == 0 ? [] : extensions[0] == Typed ? [Optional] : [Typed];
            var full = Full(documents, extensions);
            var after = ModelSnapshot.CreateAfter(previous, documents, previous.Settings, "", extensions, [], step + 2, null, 1 + (step % 3),
                TestContext.Current.CancellationToken);
            AssertSameIndex(full, after, $"seed {seed} step {step}");
            if (after.Index.Patched)
            {
                patchedCount++;
                if (after.Index.Retired.Count > 0)
                    patchedWithRetired++;
            }

            previous = after; // chains: each patch builds on the last one
        }

        Assert.True(patchedCount > 25, "patched " + patchedCount + " of 60");
        Assert.True(patchedWithRetired > 0, "no patch kept retired ids");
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

    [Fact]
    public void Changing_only_an_extension_rebuilds_the_index()
    {
        // next-phase-1-design.md section 4.1: the walks of x-ref sites depend on the extensions, so a snapshot whose extensions
        // changed rebuilds in full even when no document changed; the same files keep the set and patch.
        var (documents, _) = Model(10);
        var thing = documents.Select(d => d.Element).OfType<Entity>().Single(e => e.Name == "Thing0");
        var plain = PropertySchemaSetTests.Extension("""{ "partner": { "type": "string" } }""");
        var previous = Full(documents, [plain]);
        Assert.DoesNotContain(previous.ReferencesTo(thing.Id), r => r.Schema is not null);

        var typed = ModelSnapshot.CreateAfter(previous, documents, previous.Settings, "", [Typed], [], 2, null, 2, TestContext.Current.CancellationToken);
        Assert.False(typed.Index.Patched);
        AssertSameIndex(Full(documents, [Typed]), typed, "typed");
        Assert.Contains(typed.ReferencesTo(thing.Id), r => r is { Field: "partner", Schema: "extension:links", Optional: false });

        var optional = ModelSnapshot.CreateAfter(typed, documents, typed.Settings, "", [Optional], [], 3, null, 2, TestContext.Current.CancellationToken);
        Assert.False(optional.Index.Patched);
        Assert.All(optional.ReferencesTo(thing.Id).Where(r => r.Field == "partner"), r => Assert.True(r.Optional));

        var same = ModelSnapshot.CreateAfter(optional, documents, optional.Settings, "", [PropertySchemaSetTests.Extension(OptionalLinks)], [], 4, null, 2,
            TestContext.Current.CancellationToken);
        Assert.True(same.Index.Patched);
        Assert.Same(optional.PropertySchemas, same.PropertySchemas);
        AssertSameIndex(Full(documents, [Optional]), same, "same");
    }

    [Fact]
    public void A_package_default_change_builds_the_index_in_full_and_matches_a_full_build()
    {
        // next-phase-2-design.md section 3.1: the walks type properties by each element's effective keys, defaults included, so a
        // package that gains a default list, or an entity whose package moves the holder of a document the patch would keep (its
        // overlay), builds in full; a change that moves no holder still patches. Each result equals a full build.
        var b = new ModelBuilder(31);
        b.Stereotype("owned").Parameters("""{ "partner": { "type": "string", "x-ref": { "kind": "entity" } } }""");
        var billing = b.Package("Billing");
        var stock = b.Package("Stock");
        var customer = b.Entity("Customer", stock).Key("id", "uuid");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid").Property("partner", customer.Id);
        var main = b.Database("main", Dialect.PostgreSql);
        var overlay = b.Table("invoices", main).OverlayFor(invoice).Property("partner", customer.Id);
        var documents = b.BuildDocuments().OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        var previous = Full(documents);
        Assert.DoesNotContain(previous.ReferencesTo(customer.Id), r => r.Field == "partner");

        documents = Change(documents, billing.Id, e => ((Package)e) with { DefaultStereotypes = ["owned"] });
        var withDefault = After(previous, documents, 2);
        Assert.False(withDefault.Index.Patched);
        AssertSameIndex(Full(documents), withDefault, "default added");
        Assert.Equal(new[] { invoice.Id, overlay.Id }.Order(StringComparer.Ordinal),
            withDefault.ReferencesTo(customer.Id).Where(r => r.Field == "partner").Select(r => r.FromElementId).Order(StringComparer.Ordinal));

        documents = Change(documents, invoice.Id, e => e with { Name = "Bill" });
        var renamed = After(withDefault, documents, 3);
        Assert.True(renamed.Index.Patched);
        AssertSameIndex(Full(documents), renamed, "renamed");

        documents = Change(documents, invoice.Id, e => ((Entity)e) with { Package = stock.Id });
        var moved = After(renamed, documents, 4);
        Assert.False(moved.Index.Patched);
        AssertSameIndex(Full(documents), moved, "moved");
        Assert.DoesNotContain(moved.ReferencesTo(customer.Id), r => r.Field == "partner");

        static List<ElementDocument> Change(List<ElementDocument> documents, string id, Func<Element, Element> edit) =>
            [.. documents.Select(d => d.Element.Id == id ? d with { Element = edit(d.Element), Hash = d.Hash + "1" } : d)];

        static ModelSnapshot After(ModelSnapshot previous, List<ElementDocument> documents, long version) =>
            ModelSnapshot.CreateAfter(previous, documents, previous.Settings, "", [], [], version, null, 2, TestContext.Current.CancellationToken);
    }

    private const string TypedLinks = """{ "partner": { "type": "string", "x-ref": { "kind": "entity" } } }""";

    private const string OptionalLinks = """{ "partner": { "type": "string", "x-ref": { "kind": "entity", "optional": true } } }""";

    private static readonly ExtensionDocument Typed = PropertySchemaSetTests.Extension(TypedLinks);

    private static readonly ExtensionDocument Optional = PropertySchemaSetTests.Extension(OptionalLinks);

    private static ModelSnapshot Full(IReadOnlyList<ElementDocument> documents, IReadOnlyList<ExtensionDocument>? extensions = null) =>
        ModelSnapshot.Create(documents, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", extensions ?? [], [], 1);

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
            if (i > 0) // a custom property the extension types as a link to another entity (x-ref)
                e.Property("partner", entities[i - 1].Id);
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

        for (var i = 0; i < 6; i++) // operations: on a subject or not, with members, outcomes, errors and links (section 4.1)
        {
            var subject = entities[i * 4];
            var key = b.NewId();
            b.Add(new Operation
            {
                Id = b.NewId(), Name = "op" + i, Package = i % 2 == 0 ? null : packages[i % 3].Id, Subject = i % 3 == 2 ? null : subject.Id,
                Effect = i % 2 == 0 ? OperationEffect.Read : OperationEffect.Change, Stereotypes = i % 3 == 0 ? ["audited"] : [],
                Input = [new OperationMember { Id = key, Name = "other", KeyOf = entities[i + 1].Id }],
                Outcomes = [new OperationOutcome { Id = b.NewId(), Name = "done", Returns = new OperationReturns { Entity = subject.Id, List = i % 2 == 0 } }],
                Errors = [new OperationError { Id = b.NewId(), Name = "failed", Input = key }],
                Requires = [entities[i + 2].Id],
            });
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
        switch (random.Next(10))
        {
            case 0: // content change, same path (a new document instance); the element's status toggles retired (MQ2022's index)
                list[index] = doc with
                {
                    Element = doc.Element with { Name = doc.Element.Name + "x", Status = doc.Element.Status is null ? ElementStatus.Retired : null },
                    Hash = doc.Hash + "1",
                };
                break;
            case 1 when doc.Element is Entity entity && list.Count > 4: // move an attribute to another entity (sub-element ids move)
            {
                var other = list.FindIndex(d => d.Element is Entity && !ReferenceEquals(d, doc));
                if (other < 0 || entity.Attributes.Count < 2)
                    break;
                var moved = entity.Attributes[^1] with { Status = ElementStatus.Retired }; // a retired sub-element moves with its id
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
            case 9 when doc.Element is Operation operation: // re-point an operation's subject and key member, drop its errors
            {
                var targets = list.Where(d => d.Element is Entity).ToList();
                var target = targets[random.Next(targets.Count)].Element.Id;
                list[index] = doc with
                {
                    Element = operation with
                    {
                        Subject = random.Next(2) == 0 ? null : target,
                        Input = [.. operation.Input.Select(m => m with { KeyOf = target })],
                        Errors = [],
                    },
                    Hash = doc.Hash + "9",
                };
                break;
            }
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
        Assert.True(e.Retired.SetEquals(a.Retired), label + ": retired ids");
        Assert.Equal(e.PropertySchemasHash, a.PropertySchemasHash);
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

    [Fact]
    public void Changing_a_custom_kind_file_rebuilds_the_index()
    {
        // next-phase-2-design.md section 4.2: the set's hash covers the custom kind files, so a changed one is a full build.
        var b = new ModelBuilder(62);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var setting = b.NewId();
        b.Add(CustomKinds.CustomFixture.Element(setting, "default_customer", "pointer", $$"""{ "target": "{{customer.Id}}" }"""));
        var documents = b.BuildDocuments();
        var settings = new ProjectSettings { FormatVersion = EngineVersion.FormatVersion };
        var plain = CustomKinds.CustomFixture.KindDocument("""{ "type": "pointer", "properties": { "target": { "type": "string" } } }""");
        var linked = CustomKinds.CustomFixture.KindDocument("""{ "type": "pointer", "properties": { "target": { "type": "string", "x-ref": { "kind": "entity" } } } }""");
        var before = ModelSnapshot.Create(documents, settings, "", [], [], 1, customKinds: [plain]);
        Assert.DoesNotContain(before.ReferencesTo(customer.Id), r => r.FromElementId == setting);

        var after = ModelSnapshot.CreateAfter(before, documents, settings, "", [], [], 2, null, 1, TestContext.Current.CancellationToken, customKinds: [linked]);
        var full = ModelSnapshot.Create(documents, settings, "", [], [], 2, customKinds: [linked]);

        Assert.NotEqual(before.PropertySchemas.Hash, after.PropertySchemas.Hash);
        var reference = Assert.Single(after.ReferencesTo(customer.Id), r => r.FromElementId == setting);
        Assert.Equal(("/content/target", "custom-kind:pointer"), (reference.JsonPointer, reference.Schema));
        Assert.Equal(full.ReferencesTo(customer.Id), after.ReferencesTo(customer.Id));
        Assert.Same(linked, after.CustomKindOf("pointer"));

        // Without custom kinds the previous snapshot's are kept.
        Assert.Same(linked, ModelSnapshot.CreateAfter(after, documents, settings, "", [], [], 3, null, 1, TestContext.Current.CancellationToken).CustomKindOf("pointer"));
    }
}
