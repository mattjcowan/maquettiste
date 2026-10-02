using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// Deletes that resolve references (engine-design.md section 15.1): <c>remove-references</c> clears each reference whose referrer
/// stays valid without it and refuses the others; <c>delete-dependents</c> also resolves those. A required reference inside a
/// sub-element removes the smallest enclosing part of the referrer that leaves it valid (the reference's parent object, then its
/// parent's, outwards: an attribute whose type is gone, an end, a key, a foreign key); when only the whole referrer would do (a table
/// whose database is gone, a mapping whose entity is gone, a relation that loses an end), the referrer is deleted with its own
/// dependents, recursively. Everything is planned against the files as the index read them, for every delete of the batch, and
/// staged at once when the batch's operations have all run; the result is one change.
/// </summary>
internal sealed partial class ChangePlanner
{
    private readonly Dictionary<string, (int Index, string? Because)> _cascadeDeleted = new(StringComparer.Ordinal);
    private readonly List<string> _cascadeOrder = [];
    private readonly Dictionary<string, List<CascadeOp>> _cascadeOps = new(StringComparer.Ordinal);
    private readonly Queue<(int Index, string Target, DeleteResolution Mode)> _cascadeQueue = new();
    private readonly List<(int Index, string ElementId, ReferenceInfo Reference, string Why)> _cascadeRefused = [];
    private readonly Dictionary<string, bool> _cascadeReadable = new(StringComparer.Ordinal);
    private readonly List<DeletePlanSetting> _settingsRemovals = [];
    private ProjectSettings? _candidateSettings;
    private string? _candidateSettingsHash;

    /// <summary>One planned edit of a surviving referrer: a reference cleared, or a part of the referrer removed.</summary>
    /// <param name="Pointer">The reference (clear) or the part removed (remove), in the file as the index read it.</param>
    /// <param name="Remove">Whether the part at <paramref name="Pointer"/> is removed rather than the reference cleared.</param>
    /// <param name="Reference">The reference that caused it.</param>
    /// <param name="Index">The change it belongs to.</param>
    private sealed record CascadeOp(string Pointer, bool Remove, ReferenceInfo Reference, int Index);

    /// <summary>What the cascade of the planned deletes does, for the delete plan read (null when no delete resolved references).</summary>
    public DeletePlanReport? CascadeReport { get; private set; }

    private void CascadeDelete(int index, string id, DeleteResolution mode)
    {
        MarkCascadeDeleted(index, id, null, mode);
        while (_cascadeQueue.Count > 0)
        {
            var (at, target, how) = _cascadeQueue.Dequeue();
            foreach (var reference in _snapshot.ReferencesTo(target)
                         .OrderBy(r => r.FromElementId, StringComparer.Ordinal)
                         .ThenBy(r => r.JsonPointer, PointerOrder.Instance))
                Resolve(at, target, reference, how);
        }
    }

    private void MarkCascadeDeleted(int index, string id, string? because, DeleteResolution mode)
    {
        if (_cascadeDeleted.ContainsKey(id) || !TryGetCurrent(id, null, out var current))
            return;
        _cascadeDeleted[id] = (index, because);
        _cascadeOrder.Add(id);
        _cascadeOps.Remove(id);
        _cascadeQueue.Enqueue((index, id, mode));
        foreach (var sub in SubElementIds(id, current.Json))
            _cascadeQueue.Enqueue((index, sub, mode));
    }

    private void Resolve(int index, string target, ReferenceInfo reference, DeleteResolution mode)
    {
        var referrerId = reference.FromElementId;
        if (_cascadeDeleted.ContainsKey(referrerId) || _deleted.Contains(referrerId))
            return;
        // An element the batch names itself is left to its own operation; the candidate check judges what it still references.
        if (_batchIds.Contains(referrerId))
            return;
        // Seed columns naming a removed attribute or end are dropped with their cells once the plan is staged (section 2.7).
        if (reference.Field == "columns" && _snapshot.Get<Seed>(referrerId) is not null)
            return;
        var ops = _cascadeOps.GetValueOrDefault(referrerId) ?? [];
        if (ops.Any(o => o.Remove && (reference.JsonPointer == o.Pointer || IsUnder(reference.JsonPointer, o.Pointer))))
            return; // inside a part already removed
        if (ops.Any(o => !o.Remove && o.Pointer == reference.JsonPointer))
            return;
        if (reference.Owning)
        {
            MarkCascadeDeleted(index, referrerId, "belongs to " + Label(target), mode);
            return;
        }

        if (!TryGetCurrent(referrerId, null, out var referrer))
            return;
        if (!Readable(referrerId, referrer))
        {
            _cascadeRefused.Add((index, referrerId, reference, "its file is not valid, so it cannot be changed; fix it first."));
            return;
        }

        var narrow = mode == DeleteResolution.DeleteDependents;
        var clear = new CascadeOp(reference.JsonPointer, false, reference, index);
        var reason = TryOps(referrer, [.. ops, clear], narrow, out var widening);
        if (reason is null)
        {
            Commit(referrerId, [.. ops, clear]);
            return;
        }

        if (mode != DeleteResolution.DeleteDependents)
        {
            _cascadeRefused.Add((index, referrerId, reference, reason));
            return;
        }

        // A query whose required reference goes (a source, a column, a selected attribute) is deleted whole: its aliases tie its parts
        // together, so dropping a join or a field would leave references to it (engine-design.md section 7, "Queries").
        if (referrer.Element is Query)
        {
            MarkCascadeDeleted(index, referrerId, "needs " + Label(target), mode);
            return;
        }

        // Remove the smallest enclosing part that leaves the referrer valid; the list itself is skipped when emptying it would widen it.
        if (JsonPointer.TryParse(reference.JsonPointer, out var segments))
        {
            for (var k = segments.Length - (widening ? 2 : 1); k >= 1; k--)
            {
                var part = JsonPointer.Build(segments[..k]);
                var remove = new CascadeOp(part, true, reference, index);
                List<CascadeOp> next = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), remove];
                if (TryOps(referrer, next, narrow, out _) is not null)
                    continue;
                Commit(referrerId, next);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == part || IsUnder(pointer, part)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        MarkCascadeDeleted(index, referrerId, "needs " + Label(target), mode);
    }

    private void Commit(string referrerId, List<CascadeOp> ops) => _cascadeOps[referrerId] = ops;

    private bool Readable(string id, Working current)
    {
        if (_cascadeReadable.TryGetValue(id, out var readable))
            return readable;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        readable = Read(node, KindInfo.Get(current.Element.Kind), null, new ChangeOutcome()) is not null;
        _cascadeReadable[id] = readable;
        return readable;
    }

    /// <summary>Applies the edits to a copy of the referrer; null when it is valid after them, else why not.</summary>
    private string? TryOps(Working referrer, IReadOnlyList<CascadeOp> ops, bool narrow, out bool widening)
    {
        widening = false;
        var node = ApplyOps(referrer, ops, narrow, out var failure, out widening);
        if (node is null)
            return failure;
        return Read(node, KindInfo.Get(referrer.Element.Kind), null, new ChangeOutcome()) is not null
            ? null
            : "the reference is required, so it cannot be cleared.";
    }

    private static JsonObject? ApplyOps(Working referrer, IReadOnlyList<CascadeOp> ops, bool narrow, out string? failure, out bool widening)
    {
        failure = null;
        widening = false;
        var node = JsonNode.Parse(referrer.Json.GetRawText())!.AsObject();
        foreach (var op in ops.OrderByDescending(o => o.Pointer, PointerOrder.Instance))
        {
            if (op.Remove)
            {
                if (!JsonPointer.TryGetParent(node, op.Pointer, out var parent, out var last))
                {
                    failure = "it is not where the index says.";
                    return null;
                }

                var removed = parent switch
                {
                    JsonObject obj => obj.Remove(last),
                    JsonArray array when JsonPointer.TryIndex(last, out var i) && i < array.Count => Remove(array, i),
                    _ => false,
                };
                if (!removed)
                {
                    failure = "it is not where the index says.";
                    return null;
                }

                continue;
            }

            if (RemoveAt(node, op.Pointer, referrer.Element.Kind, narrow, out var wide) is { } reason)
            {
                failure = reason;
                widening = wide;
                return null;
            }
        }

        return node;

        static bool Remove(JsonArray array, int i)
        {
            array.RemoveAt(i);
            return true;
        }
    }

    private static bool IsUnder(string pointer, string ancestor) =>
        pointer.Length > ancestor.Length && pointer[ancestor.Length] == '/' && pointer.StartsWith(ancestor, StringComparison.Ordinal);

    /// <summary>Stages what <see cref="CascadeDelete"/> planned, after every operation of the batch ran.</summary>
    private void FinishCascade(IReadOnlyList<ChangeOutcome> outcomes)
    {
        if (_cascadeOrder.Count == 0)
            return;
        CascadeReport = BuildReport();
        foreach (var group in _cascadeRefused.GroupBy(r => (r.Index, r.ElementId)).OrderBy(g => g.Key.Index).ThenBy(g => g.Key.ElementId, StringComparer.Ordinal))
        {
            var referrer = _snapshot.GetDocument(group.Key.ElementId)!;
            var pointers = group.Select(r => r.Reference.JsonPointer).Distinct(StringComparer.Ordinal).ToList();
            var targets = group.Select(r => Label(r.Reference.ToId)).Distinct(StringComparer.Ordinal);
            outcomes[group.Key.Index].Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                "MQ2001",
                $"{referrer.Element.KindName} '{NameOf(referrer.Element)}' cannot lose its reference to {string.Join(", ", targets)} "
                + $"({string.Join(", ", pointers)}): {group.First().Why} Change or delete it first, or delete with its dependents (resolution delete-dependents).",
                group.Key.ElementId,
                referrer.Path,
                pointers[0]));
        }

        if (outcomes.Any(o => o.Outcome != SaveOutcome.Saved))
            return;

        var removedSubIds = new Dictionary<int, List<string>>();
        foreach (var id in _cascadeOrder)
        {
            if (!TryGetCurrent(id, null, out var current))
                continue;
            var index = _cascadeDeleted[id].Index;
            _changeByElement.TryAdd(id, index);
            Touch(id, current);
            var subIds = StageDelete(index, id, current);
            Add(removedSubIds, index, subIds);
        }

        foreach (var (referrerId, ops) in _cascadeOps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ops.Count == 0 || !TryGetCurrent(referrerId, null, out var referrer))
                continue;
            var index = ops.Min(o => o.Index);
            var outcome = outcomes[index];
            var node = ApplyOps(referrer, ops, narrow: true, out var failure, out _);
            var repoPath = _paths.ToRepoPath(referrer.ModelPath);
            var info = KindInfo.Get(referrer.Element.Kind);
            if (node is null || Read(node, info, repoPath, outcome) is not { } element)
            {
                outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                    "MQ2001",
                    $"{referrer.Element.KindName} '{NameOf(referrer.Element)}' cannot be changed to drop its references: {failure ?? "it would not be valid."}",
                    referrerId,
                    repoPath,
                    ops[0].Pointer));
                continue;
            }

            outcome.Referrers.AddRange(ops.Select(o => o.Reference).Distinct());
            _changeByElement.TryAdd(referrerId, index);
            Touch(referrerId, referrer);
            Stage(referrerId, element, node, info, referrer.ModelPath, outcome, referrer);
            var removed = DocumentReader.Scan(referrer.Json).Ids
                .Where(x => x.Pointer.Length > 0 && ops.Any(o => o.Remove && (x.Pointer == o.Pointer || IsUnder(x.Pointer, o.Pointer))))
                .Select(x => x.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var subId in removed)
                _deleteChecks.Add((index, subId, []));
            _removedIds.UnionWith(removed);
            Add(removedSubIds, index, removed);
        }

        foreach (var (index, subIds) in removedSubIds.OrderBy(kv => kv.Key))
        {
            if (outcomes[index].Outcome == SaveOutcome.Saved && subIds.Count > 0)
                DropSeedColumns(index, subIds, outcomes[index]);
        }

        RemoveDatabaseConventions(outcomes);

        static void Add(Dictionary<int, List<string>> map, int index, IEnumerable<string> ids)
        {
            if (!map.TryGetValue(index, out var list))
                map[index] = list = [];
            list.AddRange(ids);
        }
    }

    /// <summary>
    /// Removes the <c>databases.&lt;name&gt;</c> convention entry of every deleted database from <c>maquettiste.json</c> in the same
    /// change, unless another database keeps the name: the entry is keyed by name, so it would otherwise apply to a later database
    /// that takes the name.
    /// </summary>
    private void RemoveDatabaseConventions(IReadOnlyList<ChangeOutcome> outcomes)
    {
        var names = _cascadeOrder
            .Select(id => _snapshot.Get<Database>(id))
            .OfType<Database>()
            .Select(d => d.Name)
            .Where(n => _snapshot.Settings.Databases.ContainsKey(n))
            .Distinct(StringComparer.Ordinal)
            .Where(n => !_snapshot.All<Database>().Any(d => d.Name == n && !_deleted.Contains(d.Id)))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            return;
        var full = _paths.FullPath(ModelPaths.SettingsFile);
        if (!File.Exists(full) || outcomes.Any(o => o.Outcome != SaveOutcome.Saved))
            return;
        JsonObject node;
        try
        {
            node = JsonNode.Parse(DocumentReader.StripBom(File.ReadAllBytes(full)).Span, documentOptions: DocumentReader.ParseOptions)!.AsObject();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return; // an unreadable settings file is reported by the loader; the delete does not depend on it
        }

        if (node["databases"] is not JsonObject databases)
            return;
        foreach (var name in names)
            databases.Remove(name);
        if (databases.Count == 0)
            node.Remove("databases");
        var bytes = _json.Write(node, ModelPaths.SettingsFile, ModelPaths.SettingsFile);
        _writes[ModelPaths.SettingsFile] = bytes;
        _touched[ModelPaths.SettingsFile] = _snapshot.SettingsHash;
        _candidateSettings = _snapshot.Settings with
        {
            Databases = _snapshot.Settings.Databases.Where(kv => !names.Contains(kv.Key)).ToImmutableDictionary(StringComparer.Ordinal),
        };
        _candidateSettingsHash = ContentHash.Of(bytes);
    }

    private DeletePlanReport BuildReport()
    {
        var deletes = _cascadeOrder
            .Where(id => _cascadeDeleted[id].Because is not null)
            .Select(id =>
            {
                var document = _snapshot.GetDocument(id)!;
                return new DeletePlanDelete(id, document.Element.KindName, NameOf(document.Element), document.Path, _cascadeDeleted[id].Because!);
            })
            .ToList();
        var clears = new List<DeletePlanClear>();
        var removes = new List<DeletePlanRemove>();
        foreach (var (referrerId, ops) in _cascadeOps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var document = _snapshot.GetDocument(referrerId)!;
            foreach (var op in ops.OrderBy(o => o.Pointer, PointerOrder.Instance))
            {
                if (op.Remove)
                {
                    var sub = DocumentReader.Scan(document.Json).Ids.Where(x => x.Pointer == op.Pointer).Select(x => x.Id).FirstOrDefault();
                    var subKind = sub is not null && _snapshot.TryGetEntry(sub, out var entry) ? entry.Kind : null;
                    removes.Add(new DeletePlanRemove(referrerId, document.Element.KindName, NameOf(document.Element), op.Pointer,
                        Part(document.Json, op.Pointer, subKind, ShortName(op.Reference.ToId)), sub, subKind, "needs " + Label(op.Reference.ToId)));
                }
                else if (document.Element.Kind == ElementKind.Diagram && op.Reference.Field == "element")
                {
                    var member = op.Pointer[..op.Pointer.LastIndexOf('/')];
                    removes.Add(new DeletePlanRemove(referrerId, document.Element.KindName, NameOf(document.Element), member,
                        "member " + NameOrId(op.Reference.ToId), null, "member", "shows " + Label(op.Reference.ToId)));
                }
                else
                {
                    clears.Add(new DeletePlanClear(referrerId, document.Element.KindName, NameOf(document.Element), op.Pointer, op.Reference.Field,
                        op.Reference.ToId, "pointed to " + Label(op.Reference.ToId)));
                }
            }
        }

        var refused = _cascadeRefused
            .Select(r =>
            {
                var document = _snapshot.GetDocument(r.ElementId)!;
                return new DeletePlanRefusal(r.ElementId, document.Element.KindName, NameOf(document.Element), r.Reference.JsonPointer,
                    $"its reference to {Label(r.Reference.ToId)} at {r.Reference.JsonPointer}: {r.Why}", "MQ2001");
            })
            .ToList();
        var settings = _cascadeOrder
            .Select(id => _snapshot.Get<Database>(id))
            .OfType<Database>()
            .Where(d => _snapshot.Settings.Databases.ContainsKey(d.Name))
            .Select(d => new DeletePlanSetting("/databases/" + JsonPointer.Escape(d.Name), $"the conventions of database {d.Name}"))
            .DistinctBy(s => s.Pointer, StringComparer.Ordinal)
            .ToList();
        var roots = _cascadeOrder.Where(id => _cascadeDeleted[id].Because is null).ToList();
        return new DeletePlanReport(roots, deletes, clears, removes, refused, settings);
    }

    private string NameOrId(string id) => _snapshot.GetDocument(id) is { } document && document.Element.Id == id ? NameOf(document.Element) : id;

    private string NameOf(Element element) => ReadableName(_snapshot, element);

    /// <summary>A readable name for an element: its name, else its display name, else (a synthesized table) its entity's name.</summary>
    /// <param name="snapshot">The model the element is in.</param>
    /// <param name="element">The element.</param>
    /// <returns>The name.</returns>
    internal static string ReadableName(ModelSnapshot snapshot, Element element)
    {
        if (element.Name.Length > 0)
            return element.Name;
        if (!string.IsNullOrEmpty(element.DisplayName))
            return element.DisplayName;
        if (element is Table { Entity: { } entity } && snapshot.Get<Element>(entity) is { } owner)
            return owner.Name + " table";
        return element.Id;
    }

    private string Label(string id) => Describe(_snapshot, id);

    /// <summary>"entity Invoice", or for a sub-element "attribute total of entity Invoice".</summary>
    /// <param name="snapshot">The model the id is in.</param>
    /// <param name="id">An element or sub-element id.</param>
    /// <returns>The description, or the id when the model does not have it.</returns>
    internal static string Describe(ModelSnapshot snapshot, string id)
    {
        if (snapshot.GetDocument(id) is not { } document)
            return id;
        var owner = $"{document.Element.KindName} {ReadableName(snapshot, document.Element)}";
        if (document.Element.Id == id)
            return owner;
        if (!snapshot.TryGetEntry(id, out var entry))
            return id;
        return $"{entry.Kind} {SubName(document.Json, entry.JsonPointer) ?? id} of {owner}";
    }

    /// <summary>The name of an element or sub-element alone, without its owner.</summary>
    private string ShortName(string id)
    {
        if (_snapshot.GetDocument(id) is not { } document)
            return id;
        if (document.Element.Id == id)
            return NameOf(document.Element);
        return _snapshot.TryGetEntry(id, out var entry) ? SubName(document.Json, entry.JsonPointer) ?? id : id;
    }

    private static string? SubName(JsonElement json, string pointer)
    {
        if (!JsonPointer.TryGet(json, pointer, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in (string[])["name", "role", "code", "key", "label"])
        {
            if (value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } s)
                return s;
        }

        return null;
    }

    /// <summary>"attribute total", "end lines", "foreign key fk_invoice", "key"; an unnamed part takes the name of what it pointed to.</summary>
    private static string Part(JsonElement json, string pointer, string? subKind, string? fallback)
    {
        if (!JsonPointer.TryParse(pointer, out var segments) || segments.Length == 0)
            return pointer;
        var last = segments[^1];
        var item = JsonPointer.TryIndex(last, out _) && segments.Length > 1;
        var noun = subKind ?? Noun(item ? segments[^2] : last);
        var name = SubName(json, pointer) ?? (item ? fallback : null);
        return name is null ? noun : noun + " " + name;
    }

    private static string Noun(string property) => property switch
    {
        "attributes" => "attribute",
        "ends" => "end",
        "members" => "member",
        "columns" => "column",
        "foreignKeys" => "foreign key",
        "indexes" => "index",
        "uniques" => "unique constraint",
        "checks" => "check",
        "rows" => "row",
        "fields" => "field",
        "steps" => "step",
        "states" => "state",
        "transitions" => "transition",
        "values" => "value",
        _ => property,
    };
}

/// <summary>What the planned deletes of a change do (the planner's half of <see cref="DeletePlan"/>).</summary>
/// <param name="Roots">The ids the change deletes itself.</param>
/// <param name="Deletes">Every other element deleted.</param>
/// <param name="Clears">References cleared.</param>
/// <param name="Removes">Parts of surviving elements removed.</param>
/// <param name="Refused">References that cannot be resolved.</param>
/// <param name="Settings">Settings entries removed.</param>
internal sealed record DeletePlanReport(
    IReadOnlyList<string> Roots,
    IReadOnlyList<DeletePlanDelete> Deletes,
    IReadOnlyList<DeletePlanClear> Clears,
    IReadOnlyList<DeletePlanRemove> Removes,
    IReadOnlyList<DeletePlanRefusal> Refused,
    IReadOnlyList<DeletePlanSetting> Settings);
