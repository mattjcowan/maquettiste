using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Loading;

/// <summary>A batch after the resolver: every id field an id, with what the resolver minted and found.</summary>
/// <param name="Batch">The operations with their <c>batch.json</c> id fields as ids and no <c>as</c>; elements are the nodes the
/// resolver was given, changed in place.</param>
/// <param name="Minted">Per operation, the pointers of the ids minted below the element's root, or <see langword="null"/>.</param>
/// <param name="Ids">The placeholders declared, the qualified names used and the ids minted.</param>
/// <param name="Diagnostics">Per operation, its MQ1012 to MQ1015 diagnostics; any of them fails the batch.</param>
internal sealed record ResolvedBatch(ModelBatch Batch, IReadOnlyList<IReadOnlyList<string>?> Minted, BatchIds Ids,
    IReadOnlyList<IReadOnlyList<Diagnostic>> Diagnostics)
{
    /// <summary>Whether a name, a placeholder or an <c>as</c> failed.</summary>
    public bool Failed => Diagnostics.Any(d => d.Count > 0);
}

/// <summary>The update and delete targets of a batch, resolved on the model as loaded.</summary>
/// <param name="Targets">Per operation, the resolved id of an update's or delete's target (its <c>id</c> as given when it is no name),
/// or <see langword="null"/> for other operations and for a name that failed.</param>
/// <param name="Diagnostics">Per operation, the MQ1012, MQ1013 and MQ1015 of its target.</param>
/// <param name="Names">Each qualified name that resolved, to its id.</param>
internal sealed record BatchTargets(IReadOnlyList<string?> Targets, IReadOnlyList<IReadOnlyList<Diagnostic>> Diagnostics,
    IReadOnlyDictionary<string, string> Names);

/// <summary>
/// The batch resolver (next-phase-1-design.md section 5.1): turns the qualified names (<c>@kind:path</c>) and placeholders
/// (<c>$name</c>) of a batch into ids and mints every id a create or update needs, before operations are planned or expanded, so
/// files stay id-based. Pass 1 runs every operation in order: an update's or delete's target resolves on the model as loaded, a
/// create's root gets its id, <see cref="IdMinter"/> mints the ids below the root, and each <c>as</c> declares a placeholder for the
/// whole batch. Pass 2 runs in order again on a private overlay of the snapshot's name indexes: the operation's other id fields
/// resolve as the earlier operations leave the model; then, for a create or an update, the sites its own forms read (package,
/// database and schema, a scenario's process, a binding's database, a seed's target, a category's parent), then its element's
/// entries replace its old ones, then every other reference site. A delete removes its entries at its turn. Names compare
/// ordinally against stored names, never resolved ones, so the resolver never resolves the model.
/// </summary>
internal sealed class BatchNames
{
    private static readonly ReferenceWalker Walker = new();

    private readonly ModelSnapshot _snapshot;
    private readonly ModelBatch _batch;
    private readonly IReadOnlyList<JsonNode?> _elements;
    private readonly ISchemaRegistry _schemas;
    private readonly IIdGenerator _ids;
    private readonly List<Diagnostic>[] _diagnostics;
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly List<MintedId> _minted = [];
    private readonly Overlay _overlay;
    private bool _named;

    private BatchNames(ModelSnapshot snapshot, ModelBatch batch, IReadOnlyList<JsonNode?> elements, ISchemaRegistry schemas, IIdGenerator ids)
    {
        _snapshot = snapshot;
        _batch = batch;
        _elements = elements;
        _schemas = schemas;
        _ids = ids;
        _diagnostics = [.. batch.Operations.Select(_ => new List<Diagnostic>())];
        _overlay = new Overlay(snapshot);
    }

    /// <summary>Resolves a batch's names and placeholders and mints the ids its creates and updates need.</summary>
    /// <param name="snapshot">The model as loaded.</param>
    /// <param name="batch">The batch.</param>
    /// <param name="elements">Per operation, the parsed element of a create or update (changed in place), else <see langword="null"/>.</param>
    /// <param name="schemas">The schema registry (layouts).</param>
    /// <param name="ids">The id generator.</param>
    /// <returns>The batch with ids only, or the diagnostics that fail it.</returns>
    public static ResolvedBatch Resolve(ModelSnapshot snapshot, ModelBatch batch, IReadOnlyList<JsonNode?> elements, ISchemaRegistry schemas, IIdGenerator ids)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count != batch.Operations.Count)
            throw new ArgumentException("One element slot per operation.", nameof(elements));
        return new BatchNames(snapshot, batch, elements, schemas, ids).Run();
    }

    /// <summary>
    /// Resolves each update's and delete's <c>id</c> that is a qualified name on the model as loaded: an element appears in one
    /// operation only, so its target exists before the batch. A placeholder there is MQ1015.
    /// </summary>
    /// <param name="snapshot">The model as loaded.</param>
    /// <param name="batch">The batch.</param>
    /// <returns>The targets and their diagnostics.</returns>
    internal static BatchTargets ResolveTargets(ModelSnapshot snapshot, ModelBatch batch)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(batch);
        var targets = new string?[batch.Operations.Count];
        var diagnostics = new IReadOnlyList<Diagnostic>[batch.Operations.Count];
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < batch.Operations.Count; i++)
        {
            var o = batch.Operations[i];
            diagnostics[i] = [];
            if (o.Op is not (BatchOp.Update or BatchOp.Delete))
                continue;
            if (!QualifiedName.IsName(o.Id))
            {
                targets[i] = o.Id;
                continue;
            }

            var pointer = OperationPointer(i) + "/id";
            if (o.Id[0] == '$')
            {
                diagnostics[i] = [RuleCatalog.Create("MQ1015",
                    $"A placeholder ('{o.Id}') cannot name an update's or delete's target: the element must exist before the batch. Use its id or a qualified name.",
                    null, null, pointer)];
                continue;
            }

            if (!QualifiedName.TryParse(o.Id, out var name, out var error))
            {
                diagnostics[i] = [Malformed(o.Id, error, null, pointer)];
                continue;
            }

            var candidates = snapshot.Names(name.Kind).Find(name.Path);
            switch (candidates.Count)
            {
                case 0:
                    diagnostics[i] = [RuleCatalog.Create("MQ1012",
                        $"No {name.Kind} is named '{o.Id}' in the model as loaded: an update's or delete's target must exist before the batch.", null, null, pointer)];
                    break;
                case 1:
                    targets[i] = candidates[0].Id;
                    names.TryAdd(o.Id, candidates[0].Id);
                    break;
                default:
                    diagnostics[i] = [Ambiguous(o.Id, name.Kind, candidates, null, pointer)];
                    break;
            }
        }

        return new BatchTargets(targets, diagnostics, names);
    }

    private ResolvedBatch Run()
    {
        var count = _batch.Operations.Count;
        var targets = ResolveTargets(_snapshot, _batch);
        foreach (var (name, id) in targets.Names)
            _names[name] = id;
        var minted = new IReadOnlyList<string>?[count];
        var roots = new string?[count];
        for (var i = 0; i < count; i++)
        {
            _diagnostics[i].AddRange(targets.Diagnostics[i]);
            (roots[i], minted[i]) = Mint(i, targets.Targets[i]);
        }

        var operations = new BatchOperation[count];
        for (var i = 0; i < count; i++)
        {
            var o = _batch.Operations[i];
            operations[i] = !_named
                ? o with { Id = o.Op is BatchOp.Update or BatchOp.Delete ? targets.Targets[i] ?? o.Id : o.Id, As = null }
                : ResolveOperation(i, targets.Targets[i], roots[i]);
        }

        if (!_named)
            NameMinted(targets.Targets, roots);
        var ids = new BatchIds(_names.ToImmutableSortedDictionary(StringComparer.Ordinal), [.. _minted]);
        return new ResolvedBatch(new ModelBatch(operations), minted, ids, _diagnostics);
    }

    // ---- Pass 1 -----------------------------------------------------------------------------------------------------------------

    /// <summary>Pass 1 for one operation: the root id of a create, the ids below the root, and the placeholders <c>as</c> declares.</summary>
    /// <returns>The element's root id when known, and the pointers minted below it.</returns>
    private (string? Root, IReadOnlyList<string>? Minted) Mint(int i, string? target)
    {
        var o = _batch.Operations[i];
        var pointer = OperationPointer(i);
        _named |= HasNames(o);
        if (o.As is not null)
        {
            _named = true;
            if (o.Op is not (BatchOp.Create or BatchOp.Update))
            {
                Report(i, "MQ1014", $"'as' names the element of a create or an update; a {OpName(o.Op)} operation has no element to name.", null, pointer + "/as");
                return (null, null);
            }
        }

        if (o.Op is not (BatchOp.Create or BatchOp.Update) || _elements[i] is not JsonObject node)
            return (null, null);
        _named |= HasNames(node);
        if (node["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kindName) || !KindInfo.TryGet(kindName, out var info))
            return (null, null); // the planner reports the missing kind (MQ1002)

        var elementPointer = pointer + "/element";
        string root;
        if (o.Op == BatchOp.Create)
        {
            if (QualifiedName.IsName(o.Id))
            {
                Report(i, "MQ1015", $"A create's id must be an id, not '{o.Id}': declare a placeholder with as to name the new element.", null, pointer + "/id");
                return (null, null);
            }

            if (node["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var given))
            {
                if (QualifiedName.IsName(given))
                {
                    Report(i, "MQ1015", $"A create's id must be an id, not '{given}': declare a placeholder with as to name the new element.", null, elementPointer + "/id");
                    return (null, null);
                }

                root = given;
            }
            else if (node["id"] is null)
            {
                if (o.Id is { } opId)
                {
                    root = opId;
                }
                else
                {
                    root = _ids.NewId();
                    _minted.Add(new MintedId(i, "", info.Name, root, null, o.As));
                }

                node["id"] = root;
            }
            else
            {
                return (null, null); // a non-string id: the planner reports it
            }
        }
        else
        {
            if (target is null)
                return (null, null);
            root = target;
            if (node["id"] is null)
                node["id"] = root;
            else if (node["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var given) && QualifiedName.IsName(given) && given == o.Id)
                node["id"] = root;
        }

        var layout = _schemas.GetLayout(info.SchemaFile);
        var found = new List<MintedId>();
        IdMinter.Fill(node, layout, _ids, found, _snapshot.PropertySchemas);
        _minted.AddRange(found.Select(m => m with { Operation = i }));
        if (o.As is { } placeholder)
            Declare(i, placeholder, root, root, pointer + "/as");
        DeclareSubElements(i, root, node, layout, "", elementPointer, true);
        DeclareMembers(i, root, node, elementPointer);
        return (root, found.Count == 0 ? null : [.. found.Select(m => m.Pointer)]);
    }

    /// <summary>
    /// Records the placeholders the <c>as</c> keys of a custom element's members declare (next-phase-2-design.md section 4.2) and removes
    /// the keys. The kind's layout leaves <c>content</c> free-form, so it is walked through the custom kind's member tree, as
    /// <see cref="IdMinter.Fill"/> mints the members' ids.
    /// </summary>
    private void DeclareMembers(int i, string elementId, JsonNode node, string elementPointer)
    {
        if (node is not JsonObject root || root["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var kindName) || kindName != "custom"
            || root["type"] is not JsonValue type || !type.TryGetValue<string>(out var typeName)
            || _snapshot.PropertySchemas.ContentOf(typeName) is not { Members.Count: > 0 } source || root["content"] is not JsonObject content)
            return;
        foreach (var key in content.Select(p => p.Key).Order(StringComparer.Ordinal).ToList())
        {
            if (source.Members.TryGetValue(key, out var member))
                DeclareMembers(i, elementId, content[key], member, "/content/" + JsonPointer.Escape(key), elementPointer);
        }
    }

    private void DeclareMembers(int i, string elementId, JsonNode? value, XMemberNode node, string pointer, string elementPointer)
    {
        switch (value)
        {
            case JsonArray array when node.Items is { } items:
                for (var k = 0; k < array.Count; k++)
                {
                    var itemPointer = pointer + "/" + k.ToString(CultureInfo.InvariantCulture);
                    if (items.IsMember && array[k] is JsonObject item && item.ContainsKey("as"))
                    {
                        _named = true;
                        var at = elementPointer + itemPointer + "/as";
                        if (item["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var memberId))
                            Report(i, "MQ1014", "'as' names a member that has an id; this one has none.", elementId, at);
                        else if (item["as"] is not JsonValue asValue || !asValue.TryGetValue<string>(out var text))
                            Report(i, "MQ1015", "'as' takes a placeholder: $ then a letter or _, then letters, digits, _ or -.", elementId, at);
                        else
                            Declare(i, text, memberId, elementId, at);
                        item.Remove("as");
                    }

                    DeclareMembers(i, elementId, array[k], items, itemPointer, elementPointer);
                }

                break;
            case JsonObject obj when node.Properties is not null:
                foreach (var key in obj.Select(p => p.Key).Order(StringComparer.Ordinal).ToList())
                {
                    if (node.Properties.TryGetValue(key, out var child))
                        DeclareMembers(i, elementId, obj[key], child, pointer + "/" + JsonPointer.Escape(key), elementPointer);
                }

                break;
        }
    }

    /// <summary>Records the placeholders the <c>as</c> keys of an element's sub-elements declare and removes the keys.</summary>
    private void DeclareSubElements(int i, string elementId, JsonNode? node, ObjectLayout layout, string pointer, string elementPointer, bool root)
    {
        switch (node)
        {
            case JsonObject obj when layout.HasKeys:
                if (obj.ContainsKey("as") && !layout.Properties.ContainsKey("as"))
                {
                    _named = true;
                    var at = elementPointer + pointer + "/as";
                    if (root)
                    {
                        Report(i, "MQ1014", "'as' inside the element's root is misplaced: name the element with the operation's as.", elementId, at);
                    }
                    else if (!layout.RequiresId || obj["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var subId))
                    {
                        Report(i, "MQ1014", "'as' names a sub-element that has an id; this object is not one.", elementId, at);
                    }
                    else if (obj["as"] is not JsonValue asValue || !asValue.TryGetValue<string>(out var text))
                    {
                        Report(i, "MQ1015", "'as' takes a placeholder: $ then a letter or _, then letters, digits, _ or -.", elementId, at);
                    }
                    else
                    {
                        Declare(i, text, subId, elementId, at);
                    }

                    obj.Remove("as");
                }

                foreach (var key in layout.Keys)
                {
                    if (obj[key] is JsonObject or JsonArray)
                        DeclareSubElements(i, elementId, obj[key], layout.Properties[key].Layout, pointer + "/" + JsonPointer.Escape(key), elementPointer, false);
                }

                break;
            case JsonObject obj when layout.MapValues is { } values:
                foreach (var key in obj.Select(p => p.Key).Order(StringComparer.Ordinal).ToList())
                {
                    if (obj[key] is JsonObject or JsonArray)
                        DeclareSubElements(i, elementId, obj[key], values, pointer + "/" + JsonPointer.Escape(key), elementPointer, false);
                }

                break;
            case JsonArray array when layout.Items is { } items:
                for (var k = 0; k < array.Count; k++)
                {
                    if (array[k] is JsonObject or JsonArray)
                        DeclareSubElements(i, elementId, array[k], items, pointer + "/" + k.ToString(CultureInfo.InvariantCulture), elementPointer, false);
                }

                break;
        }
    }

    private void Declare(int i, string text, string id, string? elementId, string pointer)
    {
        if (!QualifiedName.IsPlaceholder(text))
        {
            Report(i, "MQ1015", $"'{text}' is not a placeholder: $ then a letter or _, then letters, digits, _ or -.", elementId, pointer);
        }
        else if (!_placeholders.TryAdd(text, id))
        {
            Report(i, "MQ1014", $"The placeholder {text} is declared twice: a placeholder names one element or sub-element for the whole batch.", elementId, pointer);
        }
        else
        {
            _names[text] = id;
        }
    }

    // ---- Pass 2 -----------------------------------------------------------------------------------------------------------------

    /// <summary>Pass 2 for one operation: its id fields, then its element's sites, on the overlay as the earlier operations left it.</summary>
    private BatchOperation ResolveOperation(int i, string? target, string? root)
    {
        var o = _batch.Operations[i];
        var pointer = OperationPointer(i);
        var failed = _diagnostics[i].Count > 0;
        string? Field(string? value, string key) =>
            QualifiedName.IsName(value) ? Resolve(i, value, root, pointer + "/" + key) ?? value : value;
        IReadOnlyList<string>? Items(IReadOnlyList<string>? values, string key) => values?.Select((value, k) =>
            QualifiedName.IsName(value) ? Resolve(i, value, root, pointer + "/" + key + "/" + k.ToString(CultureInfo.InvariantCulture)) ?? value : value).ToList();

        var resolved = o with
        {
            Id = o.Op is BatchOp.Update or BatchOp.Delete ? target ?? o.Id : o.Op == BatchOp.Create ? o.Id : Field(o.Id, "id"),
            Database = Field(o.Database, "database"),
            Package = Field(o.Package, "package"),
            Schema = Field(o.Schema, "schema"),
            Target = Field(o.Target, "target"),
            Default = Field(o.Default, "default"),
            Entities = Items(o.Entities, "entities"),
            Tables = Items(o.Tables, "tables"),
            Attributes = Items(o.Attributes, "attributes"),
            ElementIds = Items(o.ElementIds, "elementIds"),
            ExpectedHashes = Hashes(i, o.ExpectedHashes, root, pointer + "/expectedHashes"),
            As = null,
        };

        if (o.Op == BatchOp.Delete)
        {
            if (target is not null)
                _overlay.Replace(target, null);
        }
        else if (o.Op is BatchOp.Create or BatchOp.Update && !failed && root is not null && _elements[i] is JsonObject node && TryRead(node) is { } element)
        {
            ResolveElement(i, root, node, element, pointer + "/element");
        }

        return resolved;
    }

    /// <summary>The <c>expectedHashes</c> keys as ids: a placeholder key is MQ1015, two keys for one element MQ1014.</summary>
    private Dictionary<string, string>? Hashes(int i, IReadOnlyDictionary<string, string>? hashes, string? root, string pointer)
    {
        if (hashes is null)
            return null;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, hash) in hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var at = pointer + "/" + JsonPointer.Escape(key);
            var id = key;
            if (QualifiedName.IsName(key))
            {
                if (key[0] == '$')
                {
                    Report(i, "MQ1015", $"A placeholder ('{key}') cannot be an expectedHashes key: the element must exist before the batch. Use its id or a qualified name.", root, at);
                    continue;
                }

                if (Resolve(i, key, root, at) is not { } found)
                    continue;
                id = found;
            }

            if (keyOf.TryGetValue(id, out var first))
            {
                Report(i, "MQ1014", $"The expectedHashes keys '{first}' and '{key}' name one element ({id}): give its hash once.", root, at);
                continue;
            }

            keyOf[id] = key;
            resolved[id] = hash;
        }

        return resolved;
    }

    /// <summary>
    /// The sites of a create's or update's element: first those its own forms read, then its entries replace its old ones on the
    /// overlay, then every other reference site whose value is a name or a placeholder. Minted ids get their longest form.
    /// </summary>
    private void ResolveElement(int i, string root, JsonObject node, Element element, string elementPointer)
    {
        var sites = Walker.Walk(element, _snapshot.PropertySchemas, _snapshot.PropertyKeys(element)).Where(s => QualifiedName.IsName(s.Value)).ToList();
        var forms = sites.Where(s => IsFormSite(s.Pointer)).ToList();
        if (forms.Count > 0)
        {
            Write(i, root, node, forms, elementPointer);
            element = TryRead(node) ?? element;
        }

        _overlay.Replace(root, element);
        var rest = sites.Where(s => !IsFormSite(s.Pointer)).ToList();
        if (rest.Count > 0)
            Write(i, root, node, rest, elementPointer);
        if (TryRead(node) is { } final)
            Name(i, root, final);
    }

    /// <summary>
    /// The longest forms of the ids a batch without names minted. Pass 2 is skipped, so the elements reach the overlay here, in
    /// operation order up to the last operation that minted, and each minted id gets its longest form as in a batch with names.
    /// </summary>
    private void NameMinted(IReadOnlyList<string?> targets, IReadOnlyList<string?> roots)
    {
        if (_minted.Count == 0)
            return;
        for (var i = 0; i <= _minted[^1].Operation; i++)
        {
            var o = _batch.Operations[i];
            if (o.Op == BatchOp.Delete && targets[i] is { } target)
                _overlay.Replace(target, null);
            else if (o.Op is BatchOp.Create or BatchOp.Update && roots[i] is { } root && _elements[i] is JsonObject node && TryRead(node) is { } element)
                Name(i, root, element);
        }
    }

    /// <summary>Puts an operation's final element on the overlay and gives the ids the operation minted their longest form.</summary>
    private void Name(int i, string root, Element final)
    {
        _overlay.Replace(root, final);
        var entries = new Dictionary<string, NameEntry>(StringComparer.Ordinal);
        foreach (var entry in NameIndex.Of(final, _overlay.Lookup, null, _snapshot.PropertySchemas))
            entries.TryAdd(entry.Id, entry);
        for (var k = 0; k < _minted.Count; k++)
        {
            if (_minted[k].Operation == i && entries.TryGetValue(_minted[k].Id, out var named))
                _minted[k] = _minted[k] with { Name = "@" + named.Kind + ":" + named.Longest };
        }
    }

    /// <summary>Resolves sites and writes their ids into the element: values first, then map keys, deepest first.</summary>
    private void Write(int i, string root, JsonObject node, List<ReferenceSite> sites, string elementPointer)
    {
        var keys = new List<(string Pointer, string Key, string Id)>();
        foreach (var site in sites)
        {
            if (Resolve(i, site.Value, root, elementPointer + site.Pointer) is not { } id
                || !JsonPointer.TryGetParent(node, site.Pointer, out var parent, out var last))
            {
                continue;
            }

            switch (parent)
            {
                case JsonObject obj when obj[last] is JsonValue value && value.TryGetValue<string>(out var text) && text == site.Value:
                    obj[last] = id;
                    break;
                case JsonObject obj when last == site.Value && obj.ContainsKey(last):
                    keys.Add((site.Pointer, last, id));
                    break;
                case JsonArray array when JsonPointer.TryIndex(last, out var index) && index < array.Count:
                    array[index] = id;
                    break;
            }
        }

        foreach (var (pointer, key, id) in keys.OrderByDescending(k => k.Pointer.Count(c => c == '/')).ThenBy(k => k.Pointer, StringComparer.Ordinal))
        {
            if (!JsonPointer.TryGetParent(node, pointer, out var parent, out _) || parent is not JsonObject obj || !obj.ContainsKey(key))
                continue;
            if (obj.ContainsKey(id))
            {
                Report(i, "MQ1014", $"The map key '{key}' names {id}, which the map already has as a key: give each element one key.", root, elementPointer + pointer);
                continue;
            }

            var value = obj[key];
            obj.Remove(key);
            obj[id] = value;
        }
    }

    /// <summary>What a name or a placeholder finds on the overlay, or <see langword="null"/> after reporting why it finds no one id.</summary>
    private string? Resolve(int i, string text, string? elementId, string pointer)
    {
        if (text[0] == '$')
        {
            if (!QualifiedName.IsPlaceholder(text))
            {
                Report(i, "MQ1015", $"'{text}' is not a placeholder: $ then a letter or _, then letters, digits, _ or -.", elementId, pointer);
                return null;
            }

            if (_placeholders.TryGetValue(text, out var declared))
                return declared;
            Report(i, "MQ1012", $"No operation declares the placeholder {text}: declare it with as on the create, update or sub-element it names.", elementId, pointer);
            return null;
        }

        if (!QualifiedName.TryParse(text, out var name, out var error))
        {
            _diagnostics[i].Add(Malformed(text, error, elementId, pointer));
            return null;
        }

        var candidates = _overlay.Find(name.Kind, name.Path);
        switch (candidates.Count)
        {
            case 0:
                Report(i, "MQ1012",
                    $"No {name.Kind} is named '{text}' in the model as the earlier operations leave it. A form through an element renamed earlier in the batch keeps the old qualifier: use the id or a second batch.",
                    elementId, pointer);
                return null;
            case 1:
                _names.TryAdd(text, candidates[0].Id);
                return candidates[0].Id;
            default:
                _diagnostics[i].Add(Ambiguous(text, name.Kind, candidates, elementId, pointer));
                return null;
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a site is one an element's own forms read, so it resolves before the element's entries replace its old ones: its
    /// package (a package's parent), database and schema, a scenario's process, a seed's target, an operation's subject (a process's
    /// subject too, harmlessly), a binding's database and a category's parent. All of them name other elements.
    /// </summary>
    private static bool IsFormSite(string pointer)
    {
        if (pointer is "/package" or "/parent" or "/database" or "/schema" or "/process" or "/target" or "/subject")
            return true;
        if (!JsonPointer.TryParse(pointer, out var segments) || segments.Length != 3 || !JsonPointer.TryIndex(segments[1], out _))
            return false;
        return (segments[0], segments[2]) is ("bindings", "database") or ("categories", "parent");
    }

    /// <summary>The element as a typed record, or <see langword="null"/> when it does not read (the planner reports its schema errors).</summary>
    private static Element? TryRead(JsonObject node)
    {
        try
        {
            return ElementReader.ReadElement(JsonElement.Parse(node.ToJsonString(), DocumentReader.ParseOptions));
        }
        catch (Exception ex) when (DocumentReader.IsDeserializationFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Whether an operation's <c>batch.json</c> id fields hold a value that starts with <c>@</c> or <c>$</c>.</summary>
    private static bool HasNames(BatchOperation o) =>
        QualifiedName.IsName(o.Id) || QualifiedName.IsName(o.Database) || QualifiedName.IsName(o.Package) || QualifiedName.IsName(o.Schema)
        || QualifiedName.IsName(o.Target) || QualifiedName.IsName(o.Default) || (o.Entities?.Any(QualifiedName.IsName) ?? false)
        || (o.Tables?.Any(QualifiedName.IsName) ?? false) || (o.Attributes?.Any(QualifiedName.IsName) ?? false)
        || (o.ElementIds?.Any(QualifiedName.IsName) ?? false)
        || (o.ExpectedHashes?.Keys.Any(QualifiedName.IsName) ?? false);

    /// <summary>Whether an element holds a string value, or a map key other than <c>$schema</c>, that starts with <c>@</c> or <c>$</c>.</summary>
    private static bool HasNames(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p => (p.Key != "$schema" && QualifiedName.IsName(p.Key)) || HasNames(p.Value)),
        JsonArray array => array.Any(HasNames),
        JsonValue value => value.TryGetValue<string>(out var text) && QualifiedName.IsName(text),
        _ => false,
    };

    private void Report(int i, string rule, string message, string? elementId, string pointer) =>
        _diagnostics[i].Add(RuleCatalog.Create(rule, message, elementId, null, pointer));

    private static Diagnostic Malformed(string text, string error, string? elementId, string pointer) =>
        RuleCatalog.Create("MQ1015", $"'{text}' is not a well-formed name: {error}.", elementId, null, pointer);

    private static Diagnostic Ambiguous(string text, string kind, IReadOnlyList<NameEntry> candidates, string? elementId, string pointer) =>
        RuleCatalog.Create("MQ1013",
            $"'{text}' finds {candidates.Count.ToString(CultureInfo.InvariantCulture)} of kind {kind}: "
            + string.Join(", ", candidates.Select(c => "@" + c.Kind + ":" + c.Longest + " (" + c.Id + ")"))
            + ". Use a longer form (package, database, schema, owner or seed), or the id when every form is shared.",
            elementId, null, pointer);

    private static string OperationPointer(int i) => "/operations/" + i.ToString(CultureInfo.InvariantCulture);

    private static string OpName(BatchOp op) => JsonSerializer.Serialize(op, EngineJson.Options).Trim('"');

    /// <summary>
    /// A batch's private layer over the snapshot's immutable name indexes: the elements the batch has written so far (an element
    /// appears in one operation only) replace every entry of their file, a deleted one leaves none, and untouched elements keep
    /// theirs, qualifiers included.
    /// </summary>
    private sealed class Overlay(ModelSnapshot snapshot)
    {
        private readonly Dictionary<string, Element?> _elements = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Kind, string Form), List<NameEntry>> _byForm = [];
        private readonly Dictionary<string, List<NameEntry>> _byElement = new(StringComparer.Ordinal);

        /// <summary>An element as the batch leaves it so far: written, deleted (<see langword="null"/>) or as loaded.</summary>
        public Element? Lookup(string id) => _elements.TryGetValue(id, out var element) ? element : snapshot.Get<Element>(id);

        /// <summary>Replaces an element file's entries with those of its new document, or removes them for a delete.</summary>
        public void Replace(string elementId, Element? element)
        {
            if (_byElement.Remove(elementId, out var old))
            {
                foreach (var entry in old)
                {
                    foreach (var form in entry.Forms)
                        _byForm[(entry.Kind, form)].Remove(entry);
                }
            }

            _elements[elementId] = element;
            if (element is null)
                return;
            var entries = NameIndex.Of(element, Lookup, null, snapshot.PropertySchemas).ToList();
            _byElement[elementId] = entries;
            foreach (var entry in entries)
            {
                foreach (var form in entry.Forms)
                {
                    if (!_byForm.TryGetValue((entry.Kind, form), out var list))
                        _byForm[(entry.Kind, form)] = list = [];
                    list.Add(entry);
                }
            }
        }

        /// <summary>What a path finds: the loaded entries of files the batch has not written, and the batch's own, ordinal by longest form, then id.</summary>
        public IReadOnlyList<NameEntry> Find(string kind, string path)
        {
            var found = snapshot.Names(kind).Find(path).Where(e => !_elements.ContainsKey(e.ElementId));
            if (_byForm.TryGetValue((kind, path), out var added))
                found = found.Concat(added);
            return [.. found.DistinctBy(e => e.Id, StringComparer.Ordinal).OrderBy(e => e.Longest, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)];
        }
    }
}
