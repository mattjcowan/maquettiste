using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>A migration fact a write created or updated (next-phase-1-design.md section 3.2).</summary>
/// <param name="Id">The fact's id.</param>
/// <param name="Database">The id of the fact's database.</param>
/// <param name="Fact">The fact type as the schema names it: <c>rename</c>, <c>rekey</c>, <c>change-type</c>, <c>backfill</c> or <c>drop</c>.</param>
/// <param name="State"><c>complete</c>, or <c>pending</c> while it lacks the intent its change needs (MQ4101 names what).</param>
/// <param name="Missing">The intent fields MQ4101 names; empty unless pending.</param>
/// <param name="Summary">A one-line summary by the names it records, such as <c>rename column billing.notes.text to body</c>.</param>
/// <param name="Path">The repo-relative path of its file.</param>
public sealed record RecordedFact(string Id, string Database, string Fact, string State, IReadOnlyList<string> Missing, string Summary, string Path);

/// <summary>
/// Recording migration facts on store writes (next-phase-1-design.md section 3.2, "Recording, captures and cost"; task A10). A write
/// that can reshape a database is classified after its validation: the store chooses, by ids alone, a superset of the databases the
/// change can reshape, captures each of them before the change (from the capture cache) and after it (one per-database resolve of
/// the candidate with the dependency keys kept), and hands the two captures, the open facts and the write's key maps to the
/// classifier (<see cref="FactClassifier"/>). The facts it returns join the write's files in the same atomic set, so a dry run lists
/// them and a refused write records none.
/// </summary>
/// <remarks>
/// <para>Nothing runs when <c>migrations.record</c> is <c>off</c> after the change, when only <c>migration</c> elements change, or
/// when the changes touch only annotations (<c>description</c>, <c>displayName</c>, <c>tags</c>, <c>category</c>, and at the top
/// level <c>generation</c> and <c>source</c>): those reach a capture only as comments, which never need a fact.</para>
/// <para>The capture cache holds one entry per database of the current snapshot: its capture and its footprint (the element ids of
/// its resolved objects' <c>e:</c> keys). A write hands the new snapshot its after entries and the untouched ones; any other change of
/// snapshot (a disk reload, a restore, a settings save that records nothing, any default-locale change) leaves the cache behind. A
/// database without an entry is chosen by its reach (<see cref="DatabaseReach"/>), and its before capture comes from the snapshot's
/// cached whole-model resolve when there is one, else from one more per-database resolve.</para>
/// <para>Objects owned by an element with an error in the write's validation, before or after, are left out of both captures, and
/// the write carries MQ4110 for each such element whose objects changed; errors of the block MQ2009 to MQ2023 (policy, decision 18)
/// exclude nothing.</para>
/// </remarks>
public sealed partial class ModelStore
{
    private static readonly string[] TopAnnotations = ["description", "displayName", "tags", "category", "generation", "source"];
    private static readonly string[] NestedAnnotations = ["description", "displayName", "tags", "category"];

    private readonly Lock _capturesGate = new();
    private CaptureCache? _captures;
    private const int RecordingResolveWindow = 64;
    private readonly Queue<string> _recordingResolves = new();
    private long _recordingResolveCount;

    /// <summary>
    /// The database ids resolved to record facts, in order: the last 64 of them, so a long-running host keeps a bounded log (tests
    /// read it).
    /// </summary>
    internal IReadOnlyList<string> RecordingResolves
    {
        get
        {
            lock (_capturesGate)
                return [.. _recordingResolves];
        }
    }

    /// <summary>How many databases were resolved to record facts since the store was created (tests count them).</summary>
    internal long RecordingResolveCount
    {
        get
        {
            lock (_capturesGate)
                return _recordingResolveCount;
        }
    }

    /// <summary>The database ids resolved to record facts after the first <paramref name="count"/> (tests read them).</summary>
    /// <exception cref="InvalidOperationException">More than 64 resolves happened since; the log no longer reaches back that far.</exception>
    internal IReadOnlyList<string> RecordingResolvesSince(long count)
    {
        lock (_capturesGate)
        {
            var since = _recordingResolveCount - count;
            if (since < 0 || since > _recordingResolves.Count)
                throw new InvalidOperationException($"The resolve log keeps the last {RecordingResolveWindow.ToString(CultureInfo.InvariantCulture)} entries.");
            return [.. _recordingResolves.Skip(_recordingResolves.Count - (int)since)];
        }
    }

    /// <summary>A database's capture and footprint.</summary>
    /// <param name="Capture">The capture, revision 0.</param>
    /// <param name="Footprint">The element ids of the <c>e:</c> keys of its resolved objects.</param>
    private sealed record CaptureEntry(PhysicalSnapshot Capture, FrozenSet<string> Footprint);

    /// <summary>The capture cache of one snapshot.</summary>
    private sealed record CaptureCache(ModelSnapshot Snapshot, Dictionary<string, CaptureEntry> Entries);

    /// <summary>What a write does with the capture cache once it is on disk.</summary>
    private enum HandoffKind
    {
        /// <summary>Every entry still holds: the write reshaped no database.</summary>
        Carry,

        /// <summary>The chosen databases take their after entries; the others keep theirs.</summary>
        Replace,

        /// <summary>Nothing holds any more (recording is off, so nothing was compared).</summary>
        Clear,
    }

    /// <summary>The cache handoff of a write.</summary>
    private sealed record CacheHandoff(HandoffKind Kind, IReadOnlySet<string> Chosen, IReadOnlyDictionary<string, CaptureEntry> After)
    {
        public static CacheHandoff Carry { get; } = new(HandoffKind.Carry, FrozenSet<string>.Empty, FrozenDictionary<string, CaptureEntry>.Empty);

        public static CacheHandoff Clear { get; } = new(HandoffKind.Clear, FrozenSet<string>.Empty, FrozenDictionary<string, CaptureEntry>.Empty);
    }

    /// <summary>A write to classify.</summary>
    /// <param name="Before">The snapshot before the change.</param>
    /// <param name="Candidate">The model after the change.</param>
    /// <param name="Changed">Each element the change created, updated or deleted (cascades included), with the index of its change.</param>
    /// <param name="EveryDatabase">A settings change that reshapes every database (<c>conventions</c>, <c>databases</c>, <c>typeMaps</c>, <c>inflection</c>).</param>
    /// <param name="Materialized">The materialize results of the write (their key maps).</param>
    /// <param name="Preview">A dry run: facts are listed, nothing is handed to the cache but the before entries.</param>
    /// <param name="Errors">The errors of the write's validation, before and after.</param>
    private sealed record RecordingRequest(ModelSnapshot Before, ModelSnapshot Candidate, IReadOnlyDictionary<string, int> Changed, bool EveryDatabase,
        IReadOnlyList<MaterializeResult> Materialized, bool Preview, IReadOnlyList<Diagnostic> Errors);

    /// <summary>The facts a write records.</summary>
    /// <param name="Facts">The facts created and updated, or <see langword="null"/> for a dry run whose change reaches every database.</param>
    /// <param name="Writes">The fact files, model-relative path and bytes.</param>
    /// <param name="Diagnostics">MQ4101 for the pending facts and MQ4110 for the objects left out, with the index of the change they go on.</param>
    /// <param name="Handoff">What the cache keeps once the write is on disk.</param>
    private sealed record FactRecording(IReadOnlyList<RecordedFact>? Facts, IReadOnlyList<(string Path, byte[] Bytes)> Writes,
        IReadOnlyList<(int Change, Diagnostic Diagnostic)> Diagnostics, CacheHandoff Handoff)
    {
        public static FactRecording Nothing(CacheHandoff handoff) => new([], [], [], handoff);

        /// <summary>
        /// The existing fact files the recording rewrites (a pending fact that followed its column) with the hash the model indexed:
        /// like every file a write overwrites, each must still be on disk as it was read (<see cref="ChangedFactFilesAsync"/>).
        /// </summary>
        public IReadOnlyDictionary<string, string> Touched { get; init; } = FrozenDictionary<string, string>.Empty;
    }

    /// <summary>
    /// The fact files a recording rewrites that changed on disk behind the index (model-relative paths, ordinal). Every write that
    /// records checks them before it writes: it refreshes them (<see cref="RefreshFactFilesAsync"/>) and classifies again, and a file
    /// that is still changing makes the write a conflict.
    /// </summary>
    private async Task<List<string>> ChangedFactFilesAsync(FactRecording recording, CancellationToken ct)
    {
        var changed = new List<string>();
        foreach (var (modelPath, indexHash) in recording.Touched.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string? diskHash = null;
            try
            {
                if (await Documents.ReadAsync(modelPath, ct).ConfigureAwait(false) is { } bytes)
                    diskHash = ContentHash.Of(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diskHash = null;
            }

            if (!string.Equals(diskHash, indexHash, StringComparison.Ordinal))
                changed.Add(modelPath);
        }

        return changed;
    }

    /// <summary>
    /// Indexes fact files that changed on disk behind the index (published as a <c>Disk</c> change set), so the write classifies again
    /// from what is really there. Like any disk reload, it leaves the capture cache behind.
    /// </summary>
    private async Task RefreshFactFilesAsync(IReadOnlyList<string> modelPaths, List<ChangeSet> notifications, CancellationToken ct)
    {
        var paths = _paths.Value;
        var refreshed = await ReloadAsync([.. modelPaths.Select(paths.ToRepoPath)], false, ChangeSource.Disk, ct).ConfigureAwait(false);
        if (!refreshed.IsEmpty)
            notifications.Add(refreshed);
    }

    /// <summary>Classifies a write and returns the facts it records (call under the write gate).</summary>
    private async Task<FactRecording> RecordFactsAsync(RecordingRequest request, CancellationToken ct)
    {
        var before = request.Before;
        var candidate = request.Candidate;
        if (candidate.Settings.Migrations.Record == MigrationRecording.Off)
            return FactRecording.Nothing(CacheHandoff.Clear);

        var relevant = request.Changed
            .Where(kv => Shapes(before.GetDocument(kv.Key), candidate.GetDocument(kv.Key)))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        if (relevant.Count == 0 && !request.EveryDatabase)
            return FactRecording.Nothing(CacheHandoff.Carry);

        // The databases the change can reshape: those of both models (a deleted database's facts go with it; a new one has nothing
        // to compare), chosen by ids alone.
        var databases = candidate.All<Database>().Where(d => before.Get<Database>(d.Id) is not null)
            .OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        var known = databases.Select(d => d.Id).ToFrozenSet(StringComparer.Ordinal);
        var cached = CachedEntries(before);
        var reaches = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        IReadOnlySet<string> FootprintBefore(string databaseId)
        {
            if (cached.TryGetValue(databaseId, out var entry))
                return entry.Footprint;
            if (!reaches.TryGetValue(databaseId, out var reach))
                reaches[databaseId] = reach = before.Get<Database>(databaseId) is { } database ? DatabaseReach.Of(before, database) : FrozenSet<string>.Empty;
            return reach;
        }

        var firstChange = new Dictionary<string, int>(StringComparer.Ordinal);
        void Choose(string? databaseId, int change)
        {
            if (databaseId is not null && known.Contains(databaseId) && (!firstChange.TryGetValue(databaseId, out var at) || change < at))
                firstChange[databaseId] = change;
        }

        if (request.EveryDatabase)
        {
            foreach (var database in databases)
                Choose(database.Id, 0);
        }

        foreach (var (id, change) in relevant)
        {
            foreach (var databaseId in ChooseDatabases(id, before, candidate, databases, FootprintBefore))
                Choose(databaseId, change);
        }

        if (firstChange.Count == 0)
            return FactRecording.Nothing(CacheHandoff.Carry);
        var chosen = databases.Where(d => firstChange.ContainsKey(d.Id)).ToList();
        // A dry run never resolves every database: when the change reaches them all, the save computes its facts.
        if (request.Preview && chosen.Count == databases.Count)
            return new FactRecording(null, [], [], CacheHandoff.Carry);

        var chosenIds = chosen.Select(d => d.Id).ToList();
        var after = await EntriesAsync(candidate, chosenIds, ct).ConfigureAwait(false);
        var beforeEntries = await BeforeEntriesAsync(before, chosenIds, cached, ct).ConfigureAwait(false);
        var failed = FailedOwners(before, candidate, request.Errors);
        var paths = _paths.Value;
        var facts = new List<RecordedFact>();
        var writes = new List<(string Path, byte[] Bytes)>();
        var diagnostics = new List<(int Change, Diagnostic Diagnostic)>();
        var written = request.Changed.Keys.Where(id => candidate.GetDocument(id)?.Element is Migration).ToFrozenSet(StringComparer.Ordinal);
        var touched = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = _options.EffectiveIdGenerator;

        string? DatabaseFolder(string databaseId) =>
            candidate.GetDocument(databaseId) is { Element: Database } document ? ModelPaths.FolderOf(paths.FromRepoPath(document.Path)) : null;

        foreach (var database in chosen)
        {
            var change = firstChange[database.Id];
            if (failed.Contains(database.Id) || !after.TryGetValue(database.Id, out var afterEntry) || !beforeEntries.TryGetValue(database.Id, out var beforeEntry))
            {
                Add(change, LeftOut(database, database.Id, "database " + database.Name));
                continue;
            }

            // Left out of both captures; an owner whose left-out objects differ between them had a change no fact records (MQ4110).
            var excludedBefore = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
            var excludedAfter = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
            var beforeCapture = Without(beforeEntry.Capture, failed, excludedBefore);
            var afterCapture = Without(afterEntry.Capture, failed, excludedAfter);
            foreach (var owner in excludedBefore.Keys.Union(excludedAfter.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!excludedBefore.TryGetValue(owner, out var was) || !excludedAfter.TryGetValue(owner, out var now) || !was.SequenceEqual(now))
                    Add(request.Changed.TryGetValue(owner, out var at) ? at : change, LeftOut(database, owner, Describe(candidate, before, owner)));
            }

            var materialized = request.Materialized.Where(m => string.Equals(m.Database, database.Name, StringComparison.Ordinal)).ToList();
            var input = new ClassifierInput
            {
                DatabaseId = database.Id,
                Dialect = database.Dialect,
                Before = beforeCapture,
                After = afterCapture,
                OpenFacts = [.. candidate.All<Migration>().Where(m => string.Equals(m.Database, database.Id, StringComparison.Ordinal))],
                WrittenFacts = written,
                Rekeys = [.. materialized.SelectMany(m => m.Rekeys ?? [])],
                SequenceRekeys = materialized.SelectMany(m => m.Sequences ?? FrozenDictionary<string, string>.Empty)
                    .GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal),
            };
            var result = FactClassifier.Classify(input, ids);
            foreach (var fact in result.Created.Concat(result.Updated))
            {
                var existing = candidate.GetDocument(fact.Id);
                var modelPath = existing is not null ? paths.FromRepoPath(existing.Path) : FactDocuments.PathOf(fact, DatabaseFolder);
                if (existing is not null && !written.Contains(fact.Id))
                    touched[modelPath] = existing.Hash;
                var repoPath = paths.ToRepoPath(modelPath);
                writes.Add((modelPath, FactDocuments.Bytes(_services.Json, fact, modelPath)));
                var state = MigrationRules.Missing(fact, database.Dialect).Count > 0 ? "pending" : "complete";
                facts.Add(new RecordedFact(fact.Id, database.Id, ResolutionValues.Kebab(fact.Fact), state,
                    state == "pending" ? MigrationRules.Missing(fact, database.Dialect) : [], FactDocuments.Summary(fact), repoPath));
                foreach (var (message, pointer) in MigrationRules.IntentFindings(fact, database.Dialect))
                    Add(change, RuleCatalog.Create("MQ4101", message, fact.Id, repoPath, pointer));
            }
        }

        var handoff = request.Preview ? CacheHandoff.Carry : new CacheHandoff(HandoffKind.Replace, chosenIds.ToFrozenSet(StringComparer.Ordinal), after);
        return new FactRecording(facts, writes, diagnostics, handoff) { Touched = touched };

        // A finding with the severity validation.rules gives its rule (none when the rule is off).
        void Add(int change, Diagnostic diagnostic)
        {
            foreach (var settled in ModelValidator.ApplySettings(candidate.Settings, [diagnostic]))
                diagnostics.Add((change, settled));
        }

        static Diagnostic LeftOut(Database database, string owner, string what) => RuleCatalog.Create("MQ4110",
            $"The write changed objects of database '{database.Name}' that {what} owns, and it has errors, so no migration facts were recorded for them; validate --against checks them.",
            owner);
    }

    /// <summary>A name for an element in an MQ4110 message.</summary>
    private static string Describe(ModelSnapshot candidate, ModelSnapshot before, string id)
    {
        var element = candidate.GetDocument(id)?.Element ?? before.GetDocument(id)?.Element;
        return element is null ? id : element.KindName + " " + (element.Name.Length > 0 ? element.Name : element.Id);
    }

    /// <summary>
    /// Whether a change of one element can reshape a database: a create or a delete, or an update that changes more than annotations.
    /// Migration facts never do (facts never change a capture).
    /// </summary>
    private static bool Shapes(ElementDocument? before, ElementDocument? after)
    {
        if (before is null && after is null)
            return false;
        if ((after ?? before)!.Element is Migration && (before?.Element ?? after!.Element) is Migration)
            return false;
        if (before is null || after is null)
            return true;
        var a = JsonNode.Parse(before.Json.GetRawText());
        var b = JsonNode.Parse(after.Json.GetRawText());
        StripAnnotations(a, top: true);
        StripAnnotations(b, top: true);
        return !JsonNode.DeepEquals(a, b);
    }

    /// <summary>Removes the annotation keys of an element and of each sub-element (an object with an <c>id</c>), recursively.</summary>
    private static void StripAnnotations(JsonNode? node, bool top)
    {
        switch (node)
        {
            case JsonObject o:
                if (top)
                {
                    foreach (var key in TopAnnotations)
                        o.Remove(key);
                }
                else if (o.ContainsKey("id"))
                {
                    foreach (var key in NestedAnnotations)
                        o.Remove(key);
                }

                foreach (var (_, child) in o)
                    StripAnnotations(child, false);
                break;
            case JsonArray a:
                foreach (var child in a)
                    StripAnnotations(child, false);
                break;
        }
    }

    /// <summary>
    /// The databases a change of one element can reshape, a superset chosen by ids alone (nothing is resolved to choose):
    /// a physical element or mapping, its database before and after; a database, itself; an entity, value object, scalar type, enum,
    /// relation or reference type, the databases whose footprint (or reach) holds it before, those its new package's convention or its
    /// new base or ends reach after, and the databases of the physical elements it references; a package, the databases whose
    /// convention entries name it or an ancestor, before or after; a stereotype, the databases of what carries it.
    /// </summary>
    private static IEnumerable<string> ChooseDatabases(string id, ModelSnapshot before, ModelSnapshot candidate, IReadOnlyList<Database> databases,
        Func<string, IReadOnlySet<string>> footprint)
    {
        var b = before.GetDocument(id)?.Element;
        var a = candidate.GetDocument(id)?.Element;
        IEnumerable<string> Holding(string? element) =>
            element is null ? [] : databases.Where(d => footprint(d.Id).Contains(element)).Select(d => d.Id);

        switch (a ?? b)
        {
            case Database:
                if (a is not null && b is not null)
                    yield return id;
                break;
            case Table or View or Sequence or Routine or DatabaseType or SqlObject or Mapping:
                if (PhysicalDatabase(b) is { } was)
                    yield return was;
                if (PhysicalDatabase(a) is { } now)
                    yield return now;
                break;
            case Entity or ValueObject or ScalarType or EnumType or Relation or ReferenceType:
                foreach (var databaseId in Holding(id))
                    yield return databaseId;
                switch (a)
                {
                    case Entity entity:
                        if (b is not Entity old || !string.Equals(old.Package, entity.Package, StringComparison.Ordinal))
                        {
                            foreach (var database in databases.Where(d => DatabaseScope.ByConvention(candidate, d, entity.Package)))
                                yield return database.Id;
                        }

                        if (entity.Base is { } baseId && (b is not Entity previous || !string.Equals(previous.Base, baseId, StringComparison.Ordinal)))
                        {
                            foreach (var databaseId in Holding(baseId))
                                yield return databaseId;
                        }

                        break;
                    case Relation:
                        // Its ends today: a relation that now reaches an entity of a database reshapes that database.
                        foreach (var reference in candidate.ReferencesFrom(id))
                        {
                            if (candidate.GetDocument(reference.ToId)?.Element is Entity end)
                            {
                                foreach (var databaseId in Holding(end.Id))
                                    yield return databaseId;
                            }
                        }

                        break;
                }

                foreach (var databaseId in ReferencedDatabases(id, before).Concat(ReferencedDatabases(id, candidate)))
                    yield return databaseId;
                break;
            case Package:
                foreach (var database in databases)
                {
                    if (NamesPackage(before, before.Get<Database>(database.Id), id) || NamesPackage(candidate, database, id))
                        yield return database.Id;
                }

                break;
            case Stereotype:
                foreach (var model in new[] { before, candidate })
                {
                    foreach (var reference in model.ReferencesTo(id))
                    {
                        var carrier = model.GetDocument(reference.FromElementId)?.Element;
                        if (PhysicalDatabase(carrier) is { } physical)
                            yield return physical;
                        else
                        {
                            foreach (var databaseId in Holding(reference.FromElementId))
                                yield return databaseId;
                        }
                    }
                }

                break;
        }
    }

    /// <summary>The database a physical element or mapping belongs to.</summary>
    private static string? PhysicalDatabase(Element? element) => element switch
    {
        Table t => t.Database,
        View v => v.Database,
        Sequence s => s.Database,
        Routine r => r.Database,
        DatabaseType t => t.Database,
        SqlObject o => o.Database,
        Mapping m => m.Database,
        _ => null,
    };

    /// <summary>The databases of the physical elements an element references (a binding's table, a bound reference type's table).</summary>
    private static IEnumerable<string> ReferencedDatabases(string id, ModelSnapshot model)
    {
        foreach (var reference in model.ReferencesFrom(id))
        {
            var owner = model.TryGetEntry(reference.ToId, out var entry) ? entry.OwnerId : reference.ToId;
            if (PhysicalDatabase(model.GetDocument(owner)?.Element) is { } database)
                yield return database;
        }
    }

    /// <summary>Whether a database's convention entries name a package or one of its ancestors in a model.</summary>
    private static bool NamesPackage(ModelSnapshot model, Database? database, string packageId) =>
        database is { Packages.Count: > 0 } && DatabaseScope.NearestEntry(model, database, packageId) is not null;

    /// <summary>The cache entries that belong to a snapshot.</summary>
    private Dictionary<string, CaptureEntry> CachedEntries(ModelSnapshot snapshot)
    {
        lock (_capturesGate)
        {
            return _captures is { } cache && ReferenceEquals(cache.Snapshot, snapshot)
                ? new Dictionary<string, CaptureEntry>(cache.Entries, StringComparer.Ordinal)
                : new Dictionary<string, CaptureEntry>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The before entries of the chosen databases: the cache's, else from the snapshot's cached whole-model resolve, else from one
    /// per-database resolve of the snapshot (the chosen databases missing an entry share one conceptual run). New entries join the cache.
    /// </summary>
    private async Task<Dictionary<string, CaptureEntry>> BeforeEntriesAsync(ModelSnapshot snapshot, IReadOnlyList<string> databaseIds,
        Dictionary<string, CaptureEntry> cached, CancellationToken ct)
    {
        var result = new Dictionary<string, CaptureEntry>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in databaseIds)
        {
            if (cached.TryGetValue(id, out var entry))
                result[id] = entry;
            else
                missing.Add(id);
        }

        if (missing.Count == 0)
            return result;
        Resolution.ResolvedModel? whole = null;
        lock (_resolvedGate)
        {
            if (_resolved is { } shared && ReferenceEquals(shared.Snapshot, snapshot) && shared.Task.IsCompletedSuccessfully)
                whole = shared.Task.Result.Model;
        }

        var added = new Dictionary<string, CaptureEntry>(StringComparer.Ordinal);
        if (whole is not null)
        {
            foreach (var id in missing.ToList())
            {
                if (whole.Databases.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal)) is { } database && EntryOf(database, ct) is { } entry)
                {
                    added[id] = entry;
                    missing.Remove(id);
                }
            }
        }

        if (missing.Count > 0)
        {
            foreach (var (id, entry) in await EntriesAsync(snapshot, missing, ct).ConfigureAwait(false))
                added[id] = entry;
        }

        JoinCache(snapshot, added);
        foreach (var (id, entry) in added)
            result[id] = entry;
        return result;
    }

    /// <summary>Adds entries of a snapshot to the capture cache, which starts over when it held another snapshot's.</summary>
    private void JoinCache(ModelSnapshot snapshot, IReadOnlyDictionary<string, CaptureEntry> added)
    {
        if (added.Count == 0)
            return;
        lock (_capturesGate)
        {
            if (_captures is not { } cache || !ReferenceEquals(cache.Snapshot, snapshot))
                _captures = cache = new CaptureCache(snapshot, new Dictionary<string, CaptureEntry>(StringComparer.Ordinal));
            foreach (var (id, entry) in added)
                cache.Entries[id] = entry;
        }
    }

    /// <summary>Resolves the given databases of a model (one conceptual run, dependency keys kept) and captures each.</summary>
    /// <returns>The entries of the databases that resolved; a database that did not is left out.</returns>
    private async Task<Dictionary<string, CaptureEntry>> EntriesAsync(ModelSnapshot model, IReadOnlyList<string> databaseIds, CancellationToken ct)
    {
        var result = new Dictionary<string, CaptureEntry>(StringComparer.Ordinal);
        if (databaseIds.Count == 0)
            return result;
        lock (_capturesGate)
        {
            foreach (var id in databaseIds)
            {
                _recordingResolves.Enqueue(id);
                if (_recordingResolves.Count > RecordingResolveWindow)
                    _recordingResolves.Dequeue();
            }

            _recordingResolveCount += databaseIds.Count;
        }

        // A shape the resolver cannot take: those databases record nothing (MQ4110).
        if (await ResolveDatabasesAsync(model, databaseIds, freeze: true, ct).ConfigureAwait(false) is not { } resolved)
            return result;
        for (var i = 0; i < databaseIds.Count; i++)
        {
            if (resolved[i] is { } database && EntryOf(database, ct) is { } entry)
                result[databaseIds[i]] = entry;
        }

        return result;
    }

    /// <summary>
    /// Resolves the given databases of a model (one conceptual run), or <see langword="null"/> when the resolver cannot take the model's
    /// shape; a substitute resolver answers every database from one whole-model resolve.
    /// </summary>
    private async Task<IReadOnlyList<RDatabase?>?> ResolveDatabasesAsync(ModelSnapshot model, IReadOnlyList<string> databaseIds, bool freeze, CancellationToken ct)
    {
        try
        {
            if (_services.Resolver is ModelResolver resolver)
                return (await resolver.ResolveDatabasesAsync(model, databaseIds, freeze, ct).ConfigureAwait(false)).Databases;
            var whole = await _services.Resolver.ResolveAsync(model, null, ct).ConfigureAwait(false);
            return [.. databaseIds.Select(id => whole.Databases.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal)))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>A resolved database's capture and footprint, or <see langword="null"/> when it cannot be captured (no dialect).</summary>
    private CaptureEntry? EntryOf(RDatabase database, CancellationToken ct)
    {
        PhysicalSnapshot capture;
        try
        {
            capture = SnapshotCapture.Capture(database, 0, _options.EffectiveParallelism, ct);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new CaptureEntry(capture, Footprint(database));
    }

    /// <summary>The element ids of the <c>e:</c> dependency keys of a database and of every object it resolved.</summary>
    internal static FrozenSet<string> Footprint(RDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var ids = new HashSet<string>(StringComparer.Ordinal) { database.Id };
        void Add(IResolvedObject value)
        {
            foreach (var key in value.Dependencies)
            {
                if (key.StartsWith("e:", StringComparison.Ordinal))
                    ids.Add(key[2..]);
            }
        }

        void AddAll<T>(IEnumerable<T> values) where T : IResolvedObject
        {
            foreach (var value in values)
                Add(value);
        }

        Add(database);
        foreach (var table in database.Tables)
        {
            Add(table);
            AddAll(table.Columns);
        }

        AddAll(database.Schemas);
        AddAll(database.Views);
        AddAll(database.Sequences);
        AddAll(database.Routines);
        AddAll(database.Types);
        AddAll(database.Objects);
        return ids.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The top-level ids of the elements with errors in a write's validation (policy rules MQ2009 to MQ2023 aside), plus, for a mapping
    /// or table file with errors, the entity, relation and table it applies to, as the explorer's table list leaves them out.
    /// </summary>
    internal static HashSet<string> FailedOwners(ModelSnapshot before, ModelSnapshot candidate, IReadOnlyList<Diagnostic> errors)
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in errors)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error || diagnostic.ElementId is not { } id || IsPolicyRule(diagnostic.Rule))
                continue;
            var owner = candidate.TryGetEntry(id, out var entry) ? entry.OwnerId : before.TryGetEntry(id, out entry) ? entry.OwnerId : id;
            if (!failed.Add(owner))
                continue;
            foreach (var element in new[] { candidate.GetDocument(owner)?.Element, before.GetDocument(owner)?.Element })
            {
                switch (element)
                {
                    case Mapping mapping:
                        AddIfSet(mapping.Entity);
                        AddIfSet(mapping.Relation);
                        AddIfSet(mapping.Table);
                        break;
                    case Table table:
                        AddIfSet(table.Entity);
                        AddIfSet(table.Relation);
                        break;
                }
            }
        }

        return failed;

        void AddIfSet(string? id)
        {
            if (id is not null)
                failed.Add(id);
        }
    }

    /// <summary>Whether a rule is one of the policy rules MQ2009 to MQ2023, which never keep a fact from being recorded (decision 18).</summary>
    private static bool IsPolicyRule(string rule) =>
        rule.Length == 6 && rule.StartsWith("MQ20", StringComparison.Ordinal)
        && int.TryParse(rule.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 9 and <= 23;

    /// <summary>
    /// A capture without the tables, views and sequences whose owner (the key, or the part before its <c>@</c>) failed; each object left
    /// out is added to <paramref name="excluded"/> under its owner, by kind and key, as JSON text.
    /// </summary>
    internal static PhysicalSnapshot Without(PhysicalSnapshot capture, IReadOnlySet<string> failed, Dictionary<string, SortedDictionary<string, string>> excluded)
    {
        if (failed.Count == 0)
            return capture;
        bool Keep<T>(string kind, string key, T value)
        {
            var owner = failed.Contains(key) ? key : key.IndexOf('@', StringComparison.Ordinal) is var at and > 0 && failed.Contains(key[..at]) ? key[..at] : null;
            if (owner is null)
                return true;
            if (!excluded.TryGetValue(owner, out var objects))
                excluded[owner] = objects = new SortedDictionary<string, string>(StringComparer.Ordinal);
            objects[kind + ":" + key] = System.Text.Json.JsonSerializer.Serialize(value, EngineJson.Options);
            return false;
        }

        var tables = capture.Tables.Where(t => Keep("table", t.Key, t)).ToList();
        var views = capture.Views.Where(v => Keep("view", v.Key, v)).ToList();
        var sequences = capture.Sequences.Where(s => Keep("sequence", s.Key, s)).ToList();
        if (tables.Count == capture.Tables.Count && views.Count == capture.Views.Count && sequences.Count == capture.Sequences.Count)
            return capture;
        return capture with { Tables = tables, Views = views, Sequences = sequences };
    }

    /// <summary>Hands the capture cache from the snapshot a write started from to the snapshot it produced (call under the write gate).</summary>
    private void HandOffCaptures(ModelSnapshot before, ModelSnapshot after, CacheHandoff handoff)
    {
        lock (_capturesGate)
        {
            if (handoff.Kind == HandoffKind.Clear || ReferenceEquals(before, after) && handoff.Kind == HandoffKind.Carry)
            {
                if (handoff.Kind == HandoffKind.Clear)
                    _captures = null;
                return;
            }

            var old = _captures is { } cache && ReferenceEquals(cache.Snapshot, before) ? cache.Entries : null;
            var entries = new Dictionary<string, CaptureEntry>(StringComparer.Ordinal);
            if (old is not null)
            {
                foreach (var (id, entry) in old)
                {
                    if (handoff.Kind == HandoffKind.Carry || !handoff.Chosen.Contains(id))
                        entries[id] = entry;
                }
            }

            if (handoff.Kind == HandoffKind.Replace)
            {
                foreach (var (id, entry) in handoff.After)
                    entries[id] = entry;
            }

            foreach (var id in entries.Keys.Where(id => after.Get<Database>(id) is null).ToList())
                entries.Remove(id);
            _captures = new CaptureCache(after, entries);
        }
    }

    /// <summary>Whether a settings change reshapes every database: its conventions, per-database conventions, type maps or inflection changed.</summary>
    private static bool ReshapesEveryDatabase(ProjectSettings before, ProjectSettings after)
    {
        static string Text<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, EngineJson.Options);
        return !string.Equals(Text(before.Conventions), Text(after.Conventions), StringComparison.Ordinal)
            || !string.Equals(Text(before.Databases), Text(after.Databases), StringComparison.Ordinal)
            || !string.Equals(Text(before.TypeMaps), Text(after.TypeMaps), StringComparison.Ordinal)
            || !string.Equals(Text(before.Inflection), Text(after.Inflection), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- validate --against and stale facts (task A12)

    /// <summary>The captures of databases in one model, for comparing two models.</summary>
    /// <param name="Captures">Per database id that resolved and captured, its capture (revision 0).</param>
    /// <param name="FailedOwners">The top-level ids of the elements with errors (policy rules aside) and what their errors leave
    /// out; empty when the model's validation has no error but pending facts. Callers leave their objects out of both sides.</param>
    internal sealed record DatabaseCaptures(IReadOnlyDictionary<string, PhysicalSnapshot> Captures, IReadOnlySet<string> FailedOwners);

    /// <summary>
    /// Validates the whole model, then compares it with a baseline model (next-phase-1-design.md section 3.2, <c>validate
    /// --against</c>): each database of both models (by id) is captured on both sides, and every change since the baseline that a
    /// migration must follow (a rename or a moved key, a type change that is not a widening, a dropped table, column or sequence, a
    /// column made not null) must be explained by migration facts the baseline lacks, else MQ4102 at the working element. Open facts
    /// the baseline lacks whose combined effect the model no longer shows are MQ4103. Baseline objects whose owners have errors, and
    /// what baseline files that did not load hold, are skipped with MQ4109. A database only in the baseline is not checked; nothing
    /// reads a ledger table, a physical snapshot or a database. The check runs whatever <c>migrations.record</c> says.
    /// </summary>
    /// <param name="baseline">The baseline (<see cref="ModelBaseline.OpenAsync"/>).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The whole model's report with the baseline's findings.</returns>
    public async Task<ValidationReport> ValidateAgainstAsync(ModelBaseline baseline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var report = await ValidateCoreAsync(snapshot, ValidationScope.All, committedStale: false, ct).ConfigureAwait(false);
        var findings = await BaselineFindingsAsync(snapshot, baseline, ct).ConfigureAwait(false);
        return findings.Count == 0 ? report : ValidationReport.From(report.Diagnostics.Concat(findings).Distinct());
    }

    /// <summary>
    /// Captures databases of a model of this store: from the model's shared whole-model resolve when its validation has no error
    /// (pending facts aside, MQ4101), else from per-database resolves (one conceptual run) with the owners that have errors named, so
    /// the caller leaves their objects out (as <see cref="FailedOwners"/> chooses them for a write). A database that does not resolve
    /// or capture is left out.
    /// </summary>
    /// <param name="snapshot">The model (this store's).</param>
    /// <param name="databaseIds">The database ids.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The captures.</returns>
    internal async Task<DatabaseCaptures> CaptureDatabasesAsync(ModelSnapshot snapshot, IReadOnlyList<string> databaseIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(databaseIds);
        var captures = new Dictionary<string, PhysicalSnapshot>(StringComparer.Ordinal);
        if (databaseIds.Count == 0)
            return new DatabaseCaptures(captures, FrozenSet<string>.Empty);
        var shared = await ResolvedAsync(snapshot, ct).ConfigureAwait(false);
        IReadOnlyList<RDatabase?> resolved;
        IReadOnlySet<string> failed;
        if (shared.Model is { } whole)
        {
            // A write, a compare or a validate of this snapshot left captures in the cache: the same capture, already made.
            var cached = CachedEntries(snapshot);
            foreach (var id in databaseIds)
            {
                if (cached.TryGetValue(id, out var entry))
                    captures[id] = entry.Capture;
            }

            resolved = [.. databaseIds.Select(id => captures.ContainsKey(id) ? null
                : whole.Databases.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal)))];
            failed = FrozenSet<string>.Empty;
        }
        else
        {
            failed = FailedOwners(snapshot, snapshot, [.. shared.Report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)]);
            resolved = await ResolveDatabasesAsync(snapshot, databaseIds, freeze: false, ct).ConfigureAwait(false) ?? new RDatabase?[databaseIds.Count];
        }

        var added = new Dictionary<string, CaptureEntry>(StringComparer.Ordinal);
        for (var i = 0; i < databaseIds.Count; i++)
        {
            if (resolved[i] is not { } database)
                continue;
            if (shared.Model is not null)
            {
                // From the shared resolve: the entry joins the capture cache, so the next compare or write reads it.
                if (EntryOf(database, ct) is { } entry)
                    captures[databaseIds[i]] = (added[databaseIds[i]] = entry).Capture;
                continue;
            }

            try
            {
                captures[databaseIds[i]] = SnapshotCapture.Capture(database, 0, _options.EffectiveParallelism, ct);
            }
            catch (ArgumentException)
            {
                // no dialect to capture with: left out
            }
        }

        JoinCache(snapshot, added);
        return new DatabaseCaptures(captures, failed);
    }

    /// <summary>
    /// The findings of comparing the working model with a baseline: MQ4102, MQ4103 and MQ4109, settled and positioned. MQ4109 is one
    /// finding per database: the baseline owners with errors whose objects were left out, the baseline files that did not load (in the
    /// database's folder, or outside every database folder), or a database that did not resolve. A working database the baseline lacks
    /// is not compared, but a database file in its folder that did not load in the baseline is MQ4109 too.
    /// </summary>
    private async Task<List<Diagnostic>> BaselineFindingsAsync(ModelSnapshot snapshot, ModelBaseline baseline, CancellationToken ct)
    {
        var baseModel = await baseline.Store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var all = snapshot.All<Database>().OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        var notLoaded = BaselineCheck.NotLoaded(baseModel);
        var found = new List<Diagnostic>();
        foreach (var database in all.Where(d => baseModel.Get<Database>(d.Id) is null))
        {
            var path = snapshot.GetDocument(database.Id)?.Path;
            if (BaselineCheck.NotLoadedFor(notLoaded, path, withShared: false) is { Count: > 0 } files)
            {
                found.Add(RuleCatalog.Create("MQ4109",
                    $"The baseline ({baseline.Label}) has files in the folder of database '{database.Name}' that did not load ({Shown(files)}), so the database was not compared with the baseline and its changes were not checked; fix them in the baseline, or compare with another one.",
                    database.Id, path));
            }
        }

        var databases = all.Where(d => baseModel.Get<Database>(d.Id) is not null).ToList();
        if (databases.Count == 0)
            return found.Count == 0 ? found : await SettleAsync(snapshot, found, ct).ConfigureAwait(false);

        var ids = databases.Select(d => d.Id).ToList();
        var working = await CaptureDatabasesAsync(snapshot, ids, ct).ConfigureAwait(false);
        var based = await BaselineCheck.CapturesAsync(baseline, snapshot, ids, ct).ConfigureAwait(false);
        var failed = working.FailedOwners.Union(based.FailedOwners, StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);
        foreach (var database in databases)
        {
            var path = snapshot.GetDocument(database.Id)?.Path;
            var files = BaselineCheck.NotLoadedFor(notLoaded, baseModel.GetDocument(database.Id)?.Path ?? path, withShared: true);
            var unloaded = files.Count > 0 ? $"files that did not load ({Shown(files)})" : null;
            if (!based.Captures.TryGetValue(database.Id, out var baseCapture))
            {
                found.Add(RuleCatalog.Create("MQ4109",
                    $"The baseline ({baseline.Label}) has errors that keep database '{database.Name}' from resolving{(unloaded is null ? "" : " and " + unloaded)}, so its changes since the baseline were not checked; fix them in the baseline, or compare with another one.",
                    database.Id, path));
                continue;
            }

            var reasons = new List<string>(2);
            if (based.LeftOut.TryGetValue(database.Id, out var owners) && owners.Count > 0)
                reasons.Add("errors in " + Shown([.. owners.Select(o => Describe(baseModel, baseModel, o))]));
            if (unloaded is not null)
                reasons.Add(unloaded);
            if (reasons.Count > 0)
            {
                found.Add(RuleCatalog.Create("MQ4109",
                    $"The baseline ({baseline.Label}) has {string.Join(" and ", reasons)}, so the objects of database '{database.Name}' they hold were left out and their changes since the baseline were not checked; fix them in the baseline, or compare with another one.",
                    database.Id, path));
            }

            if (!working.Captures.TryGetValue(database.Id, out var workCapture))
                continue; // the working model's errors keep it from resolving: validation reports them

            // An owner with errors on either side is left out of both, so its objects read neither as dropped nor as added.
            var workToday = Without(workCapture, failed, []);
            var baseToday = Without(baseCapture, failed, []);
            var ordered = FactKeys.Order(snapshot.All<Migration>().Where(f => string.Equals(f.Database, database.Id, StringComparison.Ordinal)));
            bool Lacking(Migration fact) => !based.Facts.Contains(fact.Id);
            foreach (var unexplained in BaselineCheck.Check(baseToday, workToday, ordered, Lacking, database.Dialect, database.Id))
            {
                var (elementId, pointer) = BaselineCheck.Locate(snapshot, database, unexplained.Need);
                found.Add(RuleCatalog.Create("MQ4102", BaselineCheck.Message(unexplained, baseline.Label), elementId,
                    snapshot.GetDocument(elementId)?.Path, pointer));
            }

            found.AddRange(StaleFindings(snapshot, database, ordered, Lacking, workToday));
        }

        return await SettleAsync(snapshot, found, ct).ConfigureAwait(false);

        static string Shown(IReadOnlyList<string> names) =>
            string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" and {(names.Count - 5).ToString(CultureInfo.InvariantCulture)} more" : "");
    }

    /// <summary>
    /// MQ4103 for the stale chains of a database's candidate facts that are not pending (the validator judges pending facts on their
    /// own): one finding per fact, on its <c>fact</c> key.
    /// </summary>
    private static IEnumerable<Diagnostic> StaleFindings(ModelSnapshot snapshot, Database database, IReadOnlyList<Migration> ordered,
        Func<Migration, bool> candidate, PhysicalSnapshot capture)
    {
        foreach (var stale in StaleFacts.Chains(ordered, candidate, capture, database.Dialect))
        {
            if (MigrationRules.State(stale.Fact, database.Dialect) == "pending")
                continue;
            yield return RuleCatalog.Create("MQ4103", stale.Message, stale.Fact.Id, snapshot.GetDocument(stale.Fact.Id)?.Path, "/fact");
        }
    }

    /// <summary>
    /// The stale chains plain validation computed for a snapshot: what each judged database's committed physical snapshot gave the
    /// judgment (none, or the facts it lists as rendered) and the findings.
    /// </summary>
    private sealed record CommittedStale(IReadOnlyList<string?> Committed, IReadOnlyList<Diagnostic> Found);

    /// <summary>The stale chains by snapshot, held weakly: an entry goes when its snapshot does, so a host keeps no old model alive.</summary>
    private readonly ConditionalWeakTable<ModelSnapshot, CommittedStale> _committedStale = new();
    private long _committedStaleRuns;

    /// <summary>How many times plain validation judged stale chains against the committed snapshots (tests count them).</summary>
    internal long CommittedStaleRuns
    {
        get
        {
            lock (_capturesGate)
                return _committedStaleRuns;
        }
    }

    /// <summary>
    /// The stale chains plain validation reports (MQ4103 on facts that are not pending), judged against each database's committed
    /// physical snapshot: the facts it lists as rendered are the baseline's, and a database without one judges none of its complete
    /// facts. Computed from the model's shared resolve, so only when the model validates without error but pending facts. The committed
    /// snapshots are read on each call (generation rewrites them with no model change; the snapshot store parses a file again only
    /// when its bytes changed), and the findings are kept for the snapshot while every committed snapshot gives the judgment the same
    /// input (present or not, and the same rendered facts), so a repeated validate judges nothing again. The captures they are judged
    /// against come from the snapshot's capture cache, and a capture made here joins it. A read-only store (a model snapshot, a
    /// baseline folder) reports none: its physical snapshot store reads the working project's files.
    /// </summary>
    private async Task<IReadOnlyList<Diagnostic>> CommittedStaleFindingsAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        if (IsReadOnly)
            return [];
        var facts = snapshot.All<Migration>().ToList();
        if (facts.Count == 0)
            return [];
        var byDatabase = new List<(Database Database, List<Migration> Facts)>();
        foreach (var group in facts.GroupBy(f => f.Database, StringComparer.Ordinal))
        {
            if (snapshot.Get<Database>(group.Key) is { } owner && group.Any(f => MigrationRules.State(f, owner.Dialect) != "pending"))
                byDatabase.Add((owner, group.ToList()));
        }

        byDatabase.Sort((a, b) => string.CompareOrdinal(a.Database.Name, b.Database.Name) is var c && c != 0 ? c : string.CompareOrdinal(a.Database.Id, b.Database.Id));
        if (byDatabase.Count == 0)
            return [];
        var shared = await ResolvedAsync(snapshot, ct).ConfigureAwait(false);
        if (shared.Model is not { } resolved)
            return [];

        var committedSnapshots = new List<PhysicalSnapshot?>(byDatabase.Count);
        var inputs = new List<string?>(byDatabase.Count);
        foreach (var (database, _) in byDatabase)
        {
            PhysicalSnapshot? committed;
            try
            {
                committed = await _services.Snapshots.LoadAsync(database.Name, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                committed = null; // generation reports the unreadable file (MQ1001)
            }

            committedSnapshots.Add(committed);
            inputs.Add(committed is null ? null : string.Join('\n', RenderedFacts(committed).Order(StringComparer.Ordinal)));
        }

        lock (_capturesGate)
        {
            if (_committedStale.TryGetValue(snapshot, out var known) && known.Committed.SequenceEqual(inputs, StringComparer.Ordinal))
                return known.Found;
            _committedStaleRuns++;
        }

        var found = new List<Diagnostic>();
        Dictionary<string, CaptureEntry>? cached = null;
        for (var i = 0; i < byDatabase.Count; i++)
        {
            var (database, list) = byDatabase[i];
            if (committedSnapshots[i] is not { } committed
                || resolved.Databases.FirstOrDefault(d => string.Equals(d.Id, database.Id, StringComparison.Ordinal)) is not { } rdb)
                continue;
            // A write, a compare or an earlier validate of this snapshot left the database's capture in the cache.
            cached ??= CachedEntries(snapshot);
            if (!cached.TryGetValue(database.Id, out var entry))
            {
                if (EntryOf(rdb, ct) is not { } made)
                    continue;
                cached[database.Id] = entry = made;
                JoinCache(snapshot, new Dictionary<string, CaptureEntry>(StringComparer.Ordinal) { [database.Id] = made });
            }

            var capture = entry.Capture;

            var rendered = RenderedFacts(committed);
            var ordered = FactKeys.Order(list);
            found.AddRange(StaleFindings(snapshot, database, ordered, f => !rendered.Contains(f.Id), capture));
        }

        IReadOnlyList<Diagnostic> settled = found.Count == 0 ? found : await SettleAsync(snapshot, found, ct).ConfigureAwait(false);
        lock (_capturesGate)
            _committedStale.AddOrUpdate(snapshot, new CommittedStale(inputs, settled));
        return settled;
    }

    /// <summary>
    /// The facts a committed physical snapshot lists as rendered into a migration from its history (<see cref="PhysicalSnapshot.Facts"/>,
    /// section 3.1); empty until the schema diff renders facts and a successful apply records them (section 3.2, task A15).
    /// </summary>
    internal static FrozenSet<string> RenderedFacts(PhysicalSnapshot committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        return committed.Facts.Count == 0 ? FrozenSet<string>.Empty : committed.Facts.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Findings the store adds to a report, with the severity <c>validation.rules</c> gives them and their line and column.</summary>
    private async Task<List<Diagnostic>> SettleAsync(ModelSnapshot snapshot, List<Diagnostic> found, CancellationToken ct)
    {
        var settled = ModelValidator.ApplySettings(snapshot.Settings, found);
        var positions = new Positions(_options, snapshot);
        var result = new List<Diagnostic>(settled.Count);
        foreach (var d in settled)
        {
            if (d.Line is null && d.FilePath is not null && d.JsonPointer is not null
                && await positions.LocateAsync(d.FilePath, d.JsonPointer, ct).ConfigureAwait(false) is { } at)
                result.Add(d with { Line = at.Line, Column = at.Column });
            else
                result.Add(d);
        }

        return result;
    }
}
