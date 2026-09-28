using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>One requested change: a save, create or delete, alone or in a batch.</summary>
/// <param name="Op">The operation.</param>
/// <param name="Id">The element id (update and delete; optional for create).</param>
/// <param name="ExpectedHash">The ETag the caller loaded (update and delete).</param>
/// <param name="Element">The element JSON (create and update).</param>
/// <param name="Resolution">What a delete does with references to the element.</param>
internal sealed record PlannedChange(BatchOp Op, string? Id, string? ExpectedHash, JsonNode? Element, DeleteResolution Resolution);

/// <summary>The planner's verdict on one change.</summary>
internal sealed class ChangeOutcome
{
    /// <summary>The outcome.</summary>
    public SaveOutcome Outcome { get; set; } = SaveOutcome.Saved;

    /// <summary>The element id, when known.</summary>
    public string? Id { get; set; }

    /// <summary>For a conflict, the element as it is on disk.</summary>
    public ElementDocument? Current { get; set; }

    /// <summary>For a conflict, the hash on disk.</summary>
    public string? CurrentHash { get; set; }

    /// <summary>Diagnostics.</summary>
    public List<Diagnostic> Diagnostics { get; } = [];

    /// <summary>References that block a delete, or that a delete removed.</summary>
    public List<ReferenceInfo> Referrers { get; } = [];

    /// <summary>Marks the change failed.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <param name="diagnostic">A diagnostic, if any.</param>
    public void Fail(SaveOutcome outcome, Diagnostic? diagnostic = null)
    {
        if (Outcome == SaveOutcome.Saved)
            Outcome = outcome;
        if (diagnostic is not null)
            Diagnostics.Add(diagnostic);
    }
}

/// <summary>The files a plan writes and deletes, and the candidate model after it.</summary>
internal sealed class ChangePlan
{
    /// <summary>One outcome per change, in request order.</summary>
    public required IReadOnlyList<ChangeOutcome> Outcomes { get; init; }

    /// <summary>Model-relative paths and their new bytes.</summary>
    public required IReadOnlyDictionary<string, byte[]> Writes { get; init; }

    /// <summary>Model-relative paths to delete.</summary>
    public required IReadOnlySet<string> Deletes { get; init; }

    /// <summary>Existing element files the plan overwrites or deletes, with the index hash it assumed; the store checks them against disk.</summary>
    public required IReadOnlyDictionary<string, string> Touched { get; init; }

    /// <summary>The model after the plan, for validation (null when a change already failed).</summary>
    public ModelSnapshot? Candidate { get; init; }

    /// <summary>Surviving elements the plan creates or changes, to validate with their referrers.</summary>
    public required IReadOnlyList<string> ChangedIds { get; init; }

    /// <summary>The change index of each element a change created, updated, deleted or rewrote (reference removal).</summary>
    public required IReadOnlyDictionary<string, int> ChangeByElement { get; init; }

    /// <summary>Whether any change failed.</summary>
    public bool Failed => Outcomes.Any(o => o.Outcome != SaveOutcome.Saved);

    /// <summary>Whether the plan changes nothing on disk.</summary>
    public bool IsNoOp => Writes.Count == 0 && Deletes.Count == 0;
}

/// <summary>
/// Plans changes against a snapshot without writing anything: validates each element against its schema, assigns ids, rewrites it
/// canonically, computes its file path (kebab-case name, id suffix only on a collision; a rename moves the file, a database rename
/// moves its folder), checks expected hashes, removes references for a delete that asks for it and builds the candidate snapshot.
/// Reads files only to carry bytes that move (database folders, sidecars) and to test whether a path is free.
/// </summary>
internal sealed class ChangePlanner
{
    private readonly ModelSnapshot _snapshot;
    private readonly ISchemaRegistry _schemas;
    private readonly ICanonicalJson _json;
    private readonly ModelPaths _paths;
    private readonly IIdGenerator _ids;
    private readonly Dictionary<string, Working> _working = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _writes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _touched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _changeByElement = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _snapshotPaths = new(StringComparer.Ordinal);
    private readonly List<(int Index, string Id, ImmutableArray<string> SubIds)> _deleteChecks = [];
    private readonly Dictionary<string, string> _sidecarMoves = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>>? _sidecarUsers;
    private readonly int _parallelism;
    private CancellationToken _ct;

    /// <summary>Creates a planner over a snapshot.</summary>
    /// <param name="snapshot">The current snapshot.</param>
    /// <param name="schemas">The schema registry.</param>
    /// <param name="json">The canonical writer.</param>
    /// <param name="paths">The model path conventions.</param>
    /// <param name="ids">The id generator for creates without an id.</param>
    /// <param name="parallelism">The most documents a candidate snapshot's index build walks at once
    /// (<see cref="EngineOptions.EffectiveParallelism"/>).</param>
    public ChangePlanner(ModelSnapshot snapshot, ISchemaRegistry schemas, ICanonicalJson json, ModelPaths paths, IIdGenerator ids, int parallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        _parallelism = parallelism;
        _snapshot = snapshot;
        _schemas = schemas;
        _json = json;
        _paths = paths;
        _ids = ids;
        foreach (var document in snapshot.Documents)
            _snapshotPaths[paths.FromRepoPath(document.Path)] = document.Element.Id;
    }

    /// <summary>An element's planned version.</summary>
    private sealed record Working(Element Element, string ModelPath, byte[] Bytes, string Hash, JsonElement Json, string? SidecarText);

    /// <summary>Plans the changes, in order.</summary>
    /// <param name="changes">The changes.</param>
    /// <param name="ct">Cancellation, observed while the candidate snapshot is indexed.</param>
    /// <returns>The plan.</returns>
    public ChangePlan Plan(IReadOnlyList<PlannedChange> changes, CancellationToken ct)
    {
        _ct = ct;
        var outcomes = new List<ChangeOutcome>();
        for (var i = 0; i < changes.Count; i++)
        {
            var outcome = new ChangeOutcome { Id = changes[i].Id };
            outcomes.Add(outcome);
            switch (changes[i].Op)
            {
                case BatchOp.Create:
                    Create(i, changes[i], outcome);
                    break;
                case BatchOp.Update:
                    Update(i, changes[i], outcome);
                    break;
                default:
                    Delete(i, changes[i], outcome);
                    break;
            }
        }

        ModelSnapshot? candidate = null;
        if (outcomes.All(o => o.Outcome == SaveOutcome.Saved))
        {
            candidate = BuildCandidate();
            CheckCandidate(candidate, outcomes);
        }

        var changedIds = _working.Keys.Where(id => !_deleted.Contains(id) && _changeByElement.ContainsKey(id)).Order(StringComparer.Ordinal).ToList();
        return new ChangePlan
        {
            Outcomes = outcomes,
            Writes = _writes,
            Deletes = _deletes,
            Touched = _touched,
            Candidate = candidate,
            ChangedIds = changedIds,
            ChangeByElement = _changeByElement,
        };
    }

    private void Create(int index, PlannedChange change, ChangeOutcome outcome)
    {
        if (!TryGetKind(change.Element, outcome, null, out var node, out var info))
            return;

        string id;
        if (node["id"] is null)
        {
            id = change.Id ?? _ids.NewId();
            node["id"] = id;
        }
        else if (node["id"] is JsonValue value && value.TryGetValue<string>(out var given))
        {
            id = given;
            if (change.Id is not null && change.Id != id)
            {
                outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1002", $"/id The element's id {id} differs from the operation's id {change.Id}.", id, null, "/id"));
                return;
            }
        }
        else
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1002", "/id The id must be a string.", null, null, "/id"));
            return;
        }

        outcome.Id = id;
        if (!IdFormat.IsValid(id))
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1006", $"/id '{id}' is not a valid id: ids are 26-character uppercase Crockford ULIDs.", id, null, "/id"));
            return;
        }

        if (_snapshot.TryGetEntry(id, out _) || _changeByElement.ContainsKey(id))
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1004", $"/id The id {id} is already used in the model.", id, null, "/id"));
            return;
        }

        _changeByElement[id] = index;
        if (Read(node, info, null, outcome) is not { } element)
            return;

        var modelPath = element is Database database
            ? ModelPaths.Join(FreeDatabaseFolder(database, null), "database.json")
            : FreePath(ModelPaths.ConventionalFolder(element, DatabaseFolderOf), element, id);
        Stage(id, element, node, info, modelPath, outcome);
    }

    private void Update(int index, PlannedChange change, ChangeOutcome outcome)
    {
        var id = change.Id!;
        outcome.Id = id;
        if (!TryGetCurrent(id, outcome, out var current))
            return;
        if (!ClaimOnce(index, id, outcome))
            return;
        Touch(id, current);
        if (!CheckHash(id, change.ExpectedHash, outcome))
            return;
        if (!TryGetKind(change.Element, outcome, current.Element.Kind, out var node, out var info))
            return;

        if (node["id"] is null)
        {
            node["id"] = id;
        }
        else if (node["id"] is not JsonValue value || !value.TryGetValue<string>(out var given) || given != id)
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1002", $"/id The id cannot change: the element's id is {id}.", id, _paths.ToRepoPath(current.ModelPath), "/id"));
            return;
        }

        if (Read(node, info, _paths.ToRepoPath(current.ModelPath), outcome) is not { } element)
            return;
        if (current.Element is Stereotype before && element is Stereotype after && before.Key != after.Key)
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                "MQ3020",
                $"/key A stereotype's key cannot change ('{before.Key}' to '{after.Key}'): elements list it by key. Create a new stereotype instead.",
                id,
                _paths.ToRepoPath(current.ModelPath),
                "/key"));
            return;
        }

        var modelPath = element is Database database
            ? MoveDatabase(current, database)
            : Relocate(current, element);
        Stage(id, element, node, info, modelPath, outcome, current);
    }

    private void Delete(int index, PlannedChange change, ChangeOutcome outcome)
    {
        var id = change.Id!;
        outcome.Id = id;
        if (!TryGetCurrent(id, outcome, out var current))
            return;
        if (!ClaimOnce(index, id, outcome))
            return;
        Touch(id, current);
        if (!CheckHash(id, change.ExpectedHash, outcome))
            return;

        var subIds = SubElementIds(id, current.Json);
        if (change.Resolution == DeleteResolution.RemoveReferences)
            RemoveReferences(index, id, subIds, outcome);
        if (outcome.Outcome != SaveOutcome.Saved)
            return;

        _deleted.Add(id);
        _working.Remove(id);
        _deletes.Add(current.ModelPath);
        _writes.Remove(current.ModelPath);
        if (_snapshotPaths.ContainsKey(current.ModelPath))
            _touched[current.ModelPath] = _snapshot.GetDocument(id)!.Hash;
        foreach (var (_, file) in DocumentReader.Scan(current.Json).Sidecars)
        {
            if (ModelPaths.ResolveSidecar(current.ModelPath, file) is { } sidecar && !SidecarUsedByOthers(sidecar, id) && Exists(sidecar))
                _deletes.Add(sidecar);
        }

        _deleteChecks.Add((index, id, subIds));
    }

    private void RemoveReferences(int index, string id, ImmutableArray<string> subIds, ChangeOutcome outcome)
    {
        var references = subIds.Prepend(id)
            .SelectMany(target => _snapshot.ReferencesTo(target))
            .Where(r => r.FromElementId != id && !_deleted.Contains(r.FromElementId))
            .Distinct()
            .ToList();
        foreach (var group in references.GroupBy(r => r.FromElementId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var referrerId = group.Key;
            if (!TryGetCurrent(referrerId, null, out var referrer))
                continue;
            var repoPath = _paths.ToRepoPath(referrer.ModelPath);
            var node = JsonNode.Parse(referrer.Json.GetRawText())!.AsObject();
            var failed = false;
            foreach (var pointer in group.Select(r => r.JsonPointer).Distinct(StringComparer.Ordinal).OrderByDescending(p => p, PointerOrder.Instance))
            {
                if (RemoveAt(node, pointer, referrer.Element.Kind) is { } reason)
                {
                    failed = true;
                    outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ2001", $"{pointer} The reference to {id} cannot be removed: {reason}", referrerId, repoPath, pointer));
                }
            }

            if (failed)
                continue;
            var info = KindInfo.Get(referrer.Element.Kind);
            var probe = new ChangeOutcome();
            if (Read(node, info, repoPath, probe) is not { } element)
            {
                outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                    "MQ2001",
                    $"{referrer.Element.KindName} '{referrer.Element.Name}' holds a required reference to {id} ({string.Join(", ", group.Select(r => r.JsonPointer).Distinct(StringComparer.Ordinal))}); change or delete it first.",
                    referrerId,
                    repoPath,
                    group.First().JsonPointer));
                outcome.Diagnostics.AddRange(probe.Diagnostics);
                continue;
            }

            outcome.Referrers.AddRange(group);
            _changeByElement.TryAdd(referrerId, index);
            Stage(referrerId, element, node, info, referrer.ModelPath, outcome, referrer);
        }
    }

    /// <summary>Removes one reference; returns <see langword="null"/> when done, else why it cannot be removed.</summary>
    private static string? RemoveAt(JsonNode root, string pointer, ElementKind referrerKind)
    {
        const string NotFound = "it is not where the index says.";
        if (!JsonPointer.TryParse(pointer, out var segments) || segments.Length == 0)
            return NotFound;
        // A diagram member without its element means nothing: drop the member.
        if (referrerKind == ElementKind.Diagram && segments.Length == 3 && segments[0] == "members" && segments[2] == "element")
            pointer = "/members/" + segments[1];
        if (!JsonPointer.TryGetParent(root, pointer, out var parent, out var last))
            return NotFound;
        switch (parent)
        {
            case JsonObject obj:
                return obj.Remove(last) ? null : NotFound;
            case JsonArray array when JsonPointer.TryIndex(last, out var i) && i < array.Count:
                // Some lists mean something wider when empty; emptying one would widen the element instead of clearing a reference.
                if (array.Count == 1 && EmptyListMeaning(referrerKind, segments) is { } meaning)
                    return $"it is the last entry, and an empty list means {meaning}; change the list first.";
                array.RemoveAt(i);
                return null;
            default:
                return NotFound;
        }
    }

    /// <summary>What an empty reference list means, for the lists where empty is not simply "none" (D6; foreign keys).</summary>
    private static string? EmptyListMeaning(ElementKind kind, string[] segments) => kind switch
    {
        ElementKind.Database when segments.Length == 2 && segments[0] == "packages" => "every package",
        ElementKind.Table when segments.Length >= 4 && segments[^2] == "referencesColumns" && segments[^4] == "foreignKeys" => "the referenced table's primary key",
        _ => null,
    };

    /// <summary>Validates and deserializes an element node; null with diagnostics on failure.</summary>
    private Element? Read(JsonObject node, KindInfo info, string? repoPath, ChangeOutcome outcome)
    {
        var id = node["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        using var document = JsonDocument.Parse(node.ToJsonString());
        var root = document.RootElement;
        var diagnostics = DocumentReader.MapIdFailures(_schemas.Evaluate(info.SchemaFile, root, repoPath ?? ""), root);
        if (diagnostics.Count > 0)
        {
            foreach (var d in diagnostics)
                outcome.Fail(SaveOutcome.Invalid, d with { FilePath = repoPath });
            return null;
        }

        try
        {
            return ElementReader.ReadElement(root);
        }
        catch (Exception ex) when (DocumentReader.IsDeserializationFailure(ex))
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1002", "The element does not deserialize: " + ex.Message, id, repoPath, ""));
            return null;
        }
    }

    private void Stage(string id, Element element, JsonObject node, KindInfo info, string modelPath, ChangeOutcome outcome, Working? previous = null)
    {
        var repoPath = _paths.ToRepoPath(modelPath);
        var bytes = _json.Write(node, info.SchemaFile, repoPath);
        var json = Parse(bytes);
        List<(string Source, string Target, byte[] Bytes)>? sidecarMoves = null;
        if (previous is not null && ModelPaths.FolderOf(previous.ModelPath) != ModelPaths.FolderOf(modelPath))
        {
            if (PlanSidecarMoves(id, previous.ModelPath, modelPath, json, node, repoPath, outcome) is not { } moves)
                return;
            sidecarMoves = moves.Moves;
            if (moves.Renamed)
            {
                // A sidecar took a new name in the target folder: the element's description.file changed with it.
                bytes = _json.Write(node, info.SchemaFile, repoPath);
                json = Parse(bytes);
                element = ElementReader.ReadElement(json);
            }
        }

        var hash = ContentHash.Of(bytes);

        // The element's ids must be new to the model, or its own already.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (subId, pointer) in DocumentReader.Scan(json).Ids)
        {
            if (!seen.Add(subId))
                outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1004", $"{pointer}/id The id {subId} appears twice in the element.", id, repoPath, pointer + "/id"));
        }

        _working[id] = new Working(element, modelPath, bytes, hash, json, previous?.SidecarText);
        if (previous is not null && previous.ModelPath == modelPath && previous.Hash == hash && _snapshotPaths.ContainsKey(modelPath))
            return; // unchanged bytes at the same path: nothing to write
        _writes[modelPath] = bytes;
        if (previous is not null && _snapshotPaths.ContainsKey(previous.ModelPath))
            _touched[previous.ModelPath] = _snapshot.GetDocument(id)!.Hash;
        if (previous is not null && previous.ModelPath != modelPath)
        {
            _deletes.Add(previous.ModelPath);
            _writes.Remove(previous.ModelPath);
            foreach (var (source, target, sidecar) in sidecarMoves ?? [])
            {
                _writes[target] = sidecar;
                _deletes.Remove(target);
                _sidecarMoves[target] = source;
                if (!SidecarUsedByOthers(source, id))
                {
                    _deletes.Add(source);
                    _writes.Remove(source);
                }
            }
        }

        _deletes.Remove(modelPath);
    }

    private static JsonElement Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    /// <summary>The path of an updated element: kept unless its name (and so its file name) or its conventional folder changes.</summary>
    private string Relocate(Working current, Element element)
    {
        var folder = ModelPaths.FolderOf(current.ModelPath);
        var oldConventional = ModelPaths.ConventionalFolder(current.Element, DatabaseFolderOf);
        var newConventional = ModelPaths.ConventionalFolder(element, DatabaseFolderOf);
        if (folder == oldConventional && newConventional != oldConventional)
            folder = newConventional; // a table, view or sequence moved to another database follows it
        if (folder == ModelPaths.FolderOf(current.ModelPath) && ModelPaths.FileName(current.Element, false) == ModelPaths.FileName(element, false))
            return current.ModelPath;
        return FreePath(folder, element, element.Id);
    }

    /// <summary>A database whose name changes moves its folder, with every file in it, when the folder follows the convention.</summary>
    private string MoveDatabase(Working current, Database database)
    {
        var oldFolder = ModelPaths.FolderOf(current.ModelPath);
        if (ModelPaths.FileNameOf(current.ModelPath) != "database.json"
            || ModelPaths.FolderOf(oldFolder) != ModelPaths.DatabasesFolder
            || ModelPaths.Stem(current.Element) == ModelPaths.Stem(database))
            return current.ModelPath;

        var newFolder = FreeDatabaseFolder(database, oldFolder);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var full = _paths.FullPath(oldFolder);
        if (Directory.Exists(full))
        {
            // Every file moves, hidden ones included (the default options skip dot-files on Unix and hidden files on Windows).
            foreach (var file in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if (_paths.ToModelPath(file) is { } modelPath)
                    files.Add(modelPath);
            }
        }

        files.UnionWith(_writes.Keys.Where(p => p.StartsWith(oldFolder + "/", StringComparison.Ordinal)));
        files.Remove(current.ModelPath);
        foreach (var file in files)
        {
            if (_deletes.Contains(file) && !_writes.ContainsKey(file))
                continue;
            if (AtomicFileSet.IsStagedName(ModelPaths.FileNameOf(file)))
            {
                _deletes.Add(file); // a leftover of a write that died midway: not model content, so it does not move
                continue;
            }

            var target = newFolder + file[oldFolder.Length..];
            var bytes = _writes.Remove(file, out var planned) ? planned : File.ReadAllBytes(_paths.FullPath(file));
            _writes[target] = bytes;
            _deletes.Add(file);
            var ownerId = _working.FirstOrDefault(w => w.Value.ModelPath == file).Key ?? _snapshotPaths.GetValueOrDefault(file);
            if (ownerId is null || _deleted.Contains(ownerId) || !TryGetCurrent(ownerId, null, out var moved))
                continue;
            if (_snapshotPaths.ContainsKey(file))
                _touched[file] = _snapshot.GetDocument(ownerId)!.Hash;
            _working[ownerId] = moved with { ModelPath = target };
        }

        return ModelPaths.Join(newFolder, "database.json");
    }

    /// <summary>
    /// Plans the sidecars that follow an element into another folder. A sidecar never lands on a file that is already there, planned,
    /// or referenced by another element: it takes the id's collision suffix instead (<c>notes.md</c> becomes <c>notes-3f9k2a.md</c>),
    /// and the element's <c>description.file</c> in <paramref name="node"/> is rewritten to match. Null (the change failed) when even
    /// that name is taken.
    /// </summary>
    private (List<(string Source, string Target, byte[] Bytes)> Moves, bool Renamed)? PlanSidecarMoves(
        string id, string from, string to, JsonElement json, JsonObject node, string repoPath, ChangeOutcome outcome)
    {
        var moves = new List<(string Source, string Target, byte[] Bytes)>();
        var renamed = false;
        foreach (var (pointer, file) in DocumentReader.Scan(json).Sidecars)
        {
            if (ModelPaths.ResolveSidecar(from, file) is not { } source || ModelPaths.ResolveSidecar(to, file) is not { } target || source == target)
                continue;
            byte[] bytes;
            if (_writes.TryGetValue(source, out var planned))
                bytes = planned;
            else if (Exists(source))
                bytes = File.ReadAllBytes(_paths.FullPath(source));
            else
                continue;

            if (!IsSidecarTargetFree(target, source, id, moves))
            {
                var slash = file.LastIndexOf('/');
                var name = file[(slash + 1)..];
                var dot = name.LastIndexOf('.');
                var alternative = file[..(slash + 1)] + (dot > 0 ? name[..dot] + ModelPaths.Suffix(id) + name[dot..] : name + ModelPaths.Suffix(id));
                if (ModelPaths.ResolveSidecar(to, alternative) is not { } other || !IsSidecarTargetFree(other, source, id, moves)
                    || !JsonPointer.TryGetParent(node, pointer + "/file", out var parent, out var last) || parent is not JsonObject description)
                {
                    outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                        "MQ1002",
                        $"{pointer}/file The sidecar '{file}' cannot move with the element: {_paths.ToRepoPath(target)} is already taken.",
                        id,
                        repoPath,
                        pointer + "/file"));
                    return null;
                }

                description[last] = alternative;
                target = other;
                renamed = true;
            }

            moves.Add((source, target, bytes));
        }

        return (moves, renamed);
    }

    /// <summary>Whether a sidecar may be written at <paramref name="target"/> without replacing anything another element or the user owns.</summary>
    private bool IsSidecarTargetFree(string target, string source, string id, List<(string Source, string Target, byte[] Bytes)> pending)
    {
        foreach (var move in pending)
        {
            if (move.Target == target)
                return move.Source == source;
        }

        if (_sidecarMoves.TryGetValue(target, out var movedFrom))
            return movedFrom == source; // a sidecar shared with another element that already moved it here
        if (_writes.ContainsKey(target) || SidecarUsedByOthers(target, id))
            return false;
        return _deletes.Contains(target) || !Exists(target);
    }

    private ModelSnapshot BuildCandidate()
    {
        var documents = new List<ElementDocument>(_snapshot.Documents.Count + _working.Count);
        foreach (var document in _snapshot.Documents)
        {
            var id = document.Element.Id;
            if (!_deleted.Contains(id) && !_working.ContainsKey(id))
                documents.Add(document);
        }

        foreach (var working in _working.Values)
        {
            documents.Add(new ElementDocument(
                working.Element,
                _paths.ToRepoPath(working.ModelPath),
                working.Hash,
                HashBuilder.Of(working.Hash, null),
                working.Json,
                working.SidecarText));
        }

        // Patched from the current snapshot's indexes when the change is small (the result equals a full build).
        return ModelSnapshot.CreateAfter(_snapshot, documents, _snapshot.Settings, _snapshot.SettingsHash, _snapshot.Extensions, _snapshot.RuleScripts,
            _snapshot.Version, null, _parallelism, _ct);
    }

    private void CheckCandidate(ModelSnapshot candidate, List<ChangeOutcome> outcomes)
    {
        // Deletes: nothing that survives may still reference the element or its sub-elements. Stereotypes are referenced by key, and
        // the index resolves a key only while the stereotype exists, so deleted stereotypes stay in the model this check looks at.
        var deletedStereotypes = _deleteChecks
            .Select(d => _snapshot.GetDocument(d.Id))
            .Where(d => d?.Element is Stereotype)
            .Select(d => d!)
            .ToList();
        var referenceModel = deletedStereotypes.Count == 0
            ? candidate
            : ModelSnapshot.CreateAfter(null, candidate.Documents.Concat(deletedStereotypes), candidate.Settings, candidate.SettingsHash, candidate.Extensions,
                candidate.RuleScripts, candidate.Version, null, _parallelism, _ct);
        foreach (var (index, id, subIds) in _deleteChecks)
        {
            var remaining = subIds.Prepend(id)
                .SelectMany(target => referenceModel.ReferencesTo(target))
                .Where(r => r.FromElementId != id && !_deleted.Contains(r.FromElementId))
                .Distinct()
                .OrderBy(r => r.FromElementId, StringComparer.Ordinal)
                .ThenBy(r => r.JsonPointer, StringComparer.Ordinal)
                .ToList();
            if (remaining.Count == 0)
                continue;
            outcomes[index].Referrers.Clear();
            outcomes[index].Referrers.AddRange(remaining);
            outcomes[index].Fail(SaveOutcome.Referenced);
        }

        // Sub-element ids must stay unique across the model: a changed element may not take an id another element still holds.
        foreach (var (id, working) in _working)
        {
            if (!_changeByElement.TryGetValue(id, out var index))
                continue;
            foreach (var (subId, pointer) in DocumentReader.Scan(working.Json).Ids)
            {
                if (pointer.Length == 0 || !_snapshot.TryGetEntry(subId, out var entry) || entry.OwnerId == id || _deleted.Contains(entry.OwnerId))
                    continue;
                if (_working.TryGetValue(entry.OwnerId, out var owner) && !DocumentReader.Scan(owner.Json).Ids.Any(x => x.Id == subId))
                    continue; // the other element gives the id up in the same plan
                outcomes[index].Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                    "MQ1004",
                    $"{pointer}/id The id {subId} is already used by {_snapshot.GetDocument(subId)?.Path}{entry.JsonPointer}.",
                    id,
                    _paths.ToRepoPath(working.ModelPath),
                    pointer + "/id"));
            }
        }

        // Ids must also be unique among the elements the plan writes: two creates or updates may not claim the same id (top-level
        // or sub-element). The later change, in request order, fails.
        var claimed = new Dictionary<string, (string Owner, string Path, string Pointer)>(StringComparer.Ordinal);
        foreach (var (id, index) in _changeByElement.Where(kv => _working.ContainsKey(kv.Key) && !_deleted.Contains(kv.Key))
                     .OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var working = _working[id];
            var repoPath = _paths.ToRepoPath(working.ModelPath);
            foreach (var (subId, pointer) in DocumentReader.Scan(working.Json).Ids)
            {
                if (claimed.TryAdd(subId, (id, repoPath, pointer)))
                    continue;
                var first = claimed[subId];
                if (first.Owner == id)
                    continue; // repeated inside one element: Stage reported it
                outcomes[index].Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                    "MQ1004",
                    $"{pointer}/id The id {subId} is already used by {first.Path}{first.Pointer}.",
                    id,
                    repoPath,
                    pointer + "/id"));
            }
        }

        // Index diagnostics the change introduces (a second tag vocabulary or category tree: MQ1009).
        var before = _snapshot.LoadDiagnostics.Where(d => d.Rule == "MQ1009").Select(d => d.ElementId).ToHashSet(StringComparer.Ordinal);
        foreach (var d in candidate.LoadDiagnostics)
        {
            if (d.ElementId is not { } owner || before.Contains(owner))
                continue;
            // The new file may sort first and push the existing singleton out: blame the change that added one of that kind.
            var kind = candidate.GetDocument(owner)?.Element.Kind;
            var culprit = _changeByElement.ContainsKey(owner)
                ? owner
                : _working.Keys.Where(id => _changeByElement.ContainsKey(id) && _working[id].Element.Kind == kind).Order(StringComparer.Ordinal).FirstOrDefault();
            if (culprit is not null)
                outcomes[_changeByElement[culprit]].Fail(SaveOutcome.Invalid, d);
        }
    }

    private bool TryGetKind(JsonNode? element, ChangeOutcome outcome, ElementKind? required, out JsonObject node, out KindInfo info)
    {
        node = null!;
        info = null!;
        if (element is not JsonObject obj)
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1002", "The element must be a JSON object.", outcome.Id, null, ""));
            return false;
        }

        if (obj["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kindName) || !KindInfo.TryGet(kindName, out var found))
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1002", "/kind The element has no known 'kind'.", outcome.Id, null, "/kind"));
            return false;
        }

        if (required is { } kind && found.Kind != kind)
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                "MQ1002",
                $"/kind The kind cannot change: the element is a {KindInfo.Get(kind).Name}.",
                outcome.Id,
                null,
                "/kind"));
            return false;
        }

        node = obj;
        info = found;
        return true;
    }

    private bool TryGetCurrent(string id, ChangeOutcome? outcome, out Working current)
    {
        current = null!;
        if (_deleted.Contains(id))
        {
            outcome?.Fail(SaveOutcome.NotFound);
            return false;
        }

        if (_working.TryGetValue(id, out var working))
        {
            current = working;
            return true;
        }

        if (_snapshot.Get<Element>(id) is null || _snapshot.GetDocument(id) is not { } document)
        {
            outcome?.Fail(SaveOutcome.NotFound);
            return false;
        }

        current = new Working(document.Element, _paths.FromRepoPath(document.Path), [], document.Hash, document.Json, document.SidecarText);
        return true;
    }

    private bool ClaimOnce(int index, string id, ChangeOutcome outcome)
    {
        if (_changeByElement.TryGetValue(id, out var other) && other != index)
        {
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ1002", $"The element {id} appears in more than one operation of the batch.", id));
            return false;
        }

        _changeByElement[id] = index;
        return true;
    }

    /// <summary>
    /// Records an element's indexed file as touched before its expected hash is checked, so the store compares it with the disk even
    /// when the change fails: a conflict is then judged against the file as it really is (SPEC section 11), not a stale index.
    /// </summary>
    private void Touch(string id, Working current)
    {
        if (_snapshotPaths.TryGetValue(current.ModelPath, out var owner) && owner == id && _snapshot.GetDocument(id) is { } indexed)
            _touched.TryAdd(current.ModelPath, indexed.Hash);
    }

    private bool CheckHash(string id, string? expectedHash, ChangeOutcome outcome)
    {
        var document = _snapshot.GetDocument(id)!;
        if (string.Equals(expectedHash, document.Hash, StringComparison.Ordinal))
            return true;
        outcome.Current = document;
        outcome.CurrentHash = document.Hash;
        outcome.Fail(SaveOutcome.Conflict);
        return false;
    }

    private ImmutableArray<string> SubElementIds(string id, JsonElement json) =>
        [.. DocumentReader.Scan(json).Ids
            .Where(x => x.Pointer.Length > 0 && _snapshot.TryGetEntry(x.Id, out var entry) && entry.OwnerId == id)
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)];

    private string? DatabaseFolderOf(string databaseId) =>
        !_deleted.Contains(databaseId) && TryGetCurrent(databaseId, null, out var database) && database.Element is Database
            ? ModelPaths.FolderOf(database.ModelPath)
            : null;

    private string FreePath(string folder, Element element, string id)
    {
        var plain = ModelPaths.Join(folder, ModelPaths.FileName(element, false));
        return IsFree(plain, id) ? plain : ModelPaths.Join(folder, ModelPaths.FileName(element, true));
    }

    private string FreeDatabaseFolder(Database database, string? current)
    {
        var plain = ModelPaths.DatabaseFolder(ModelPaths.Stem(database), null);
        return plain == current || IsFreeFolder(plain) ? plain : ModelPaths.DatabaseFolder(ModelPaths.Stem(database), database.Id);
    }

    private bool IsFree(string modelPath, string id)
    {
        if (_writes.ContainsKey(modelPath))
            return _working.TryGetValue(id, out var own) && own.ModelPath == modelPath;
        if (_working.Values.Any(w => w.ModelPath == modelPath && w.Element.Id != id))
            return false;
        if (_snapshotPaths.TryGetValue(modelPath, out var owner))
            return owner == id || _deletes.Contains(modelPath);
        return _deletes.Contains(modelPath) || !Exists(modelPath);
    }

    private bool IsFreeFolder(string folder)
    {
        var prefix = folder + "/";
        return !Directory.Exists(_paths.FullPath(folder))
            && !_writes.Keys.Any(p => p.StartsWith(prefix, StringComparison.Ordinal))
            && !_snapshotPaths.Keys.Any(p => p.StartsWith(prefix, StringComparison.Ordinal));
    }

    private bool Exists(string modelPath) => File.Exists(_paths.FullPath(modelPath));

    private bool SidecarUsedByOthers(string sidecar, string exceptId)
    {
        if (_sidecarUsers is null)
        {
            _sidecarUsers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var document in _snapshot.Documents)
            {
                var modelPath = _paths.FromRepoPath(document.Path);
                foreach (var (_, file) in DocumentReader.Scan(document.Json).Sidecars)
                {
                    if (ModelPaths.ResolveSidecar(modelPath, file) is not { } path)
                        continue;
                    if (!_sidecarUsers.TryGetValue(path, out var users))
                        _sidecarUsers[path] = users = new HashSet<string>(StringComparer.Ordinal);
                    users.Add(document.Element.Id);
                }
            }
        }

        if (_sidecarUsers.TryGetValue(sidecar, out var ids)
            && ids.Any(user => user != exceptId && !_deleted.Contains(user) && !_working.ContainsKey(user)))
            return true;
        foreach (var working in _working.Values)
        {
            if (working.Element.Id == exceptId)
                continue;
            if (DocumentReader.Scan(working.Json).Sidecars.Any(s => ModelPaths.ResolveSidecar(working.ModelPath, s.File) == sidecar))
                return true;
        }

        return false;
    }

    /// <summary>Orders pointers segment by segment, array indexes numerically, so removals can run from the last item backwards.</summary>
    private sealed class PointerOrder : IComparer<string>
    {
        public static PointerOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var a = (x ?? "").Split('/');
            var b = (y ?? "").Split('/');
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int c;
                if (JsonPointer.TryIndex(a[i], out var ai) && JsonPointer.TryIndex(b[i], out var bi))
                    c = ai.CompareTo(bi);
                else
                    c = string.CompareOrdinal(a[i], b[i]);
                if (c != 0)
                    return c;
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
