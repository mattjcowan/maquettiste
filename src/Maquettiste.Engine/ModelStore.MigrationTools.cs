using System.Buffers.Text;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>One open migration fact as <c>migration list</c> and <c>list_migrations</c> show it (next-phase-1-design.md section 3.2).</summary>
/// <param name="Id">The fact's id.</param>
/// <param name="Database">The id of its database.</param>
/// <param name="DatabaseName">The name of its database.</param>
/// <param name="Order">Its 1-based applied order within the database.</param>
/// <param name="Fact">The fact type as the schema names it: <c>rename</c>, <c>rekey</c>, <c>change-type</c>, ...</param>
/// <param name="State"><c>pending</c>, <c>complete</c>, <c>held</c> or <c>released</c>.</param>
/// <param name="Stale">Whether it is stale (MQ4103): a pending fact whose object shows its <c>from</c> again, or a fact the baseline (the
/// committed snapshot's rendered facts) lacks whose combined effect the model no longer shows; <c>migration fold</c> removes it.</param>
/// <param name="Missing">The intent fields MQ4101 names; empty unless pending.</param>
/// <param name="Summary">A one-line summary by the names it records.</param>
/// <param name="Object">The object it changes, as recorded (historical keys and names), or <see langword="null"/>.</param>
/// <param name="After">The ids of the facts it follows, as recorded.</param>
/// <param name="Path">The repo-relative path of its file.</param>
/// <param name="Hash">The fact content hash (what the fact means; its ledger rows record it).</param>
public sealed record MigrationEntry(string Id, string Database, string DatabaseName, int Order, string Fact, string State, bool Stale,
    IReadOnlyList<string> Missing, string Summary, MigrationObject? Object, IReadOnlyList<string> After, string Path, string Hash);

/// <summary>A page of open migration facts in applied order.</summary>
/// <param name="Facts">The facts of the page: by database name (then id), then applied order.</param>
/// <param name="Total">How many facts the listing holds, every page together.</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> on the last one.</param>
public sealed record MigrationList(IReadOnlyList<MigrationEntry> Facts, int Total, string? Next);

/// <summary>What <c>migration fold</c> folds.</summary>
/// <param name="Database">One database, by name or id; <see langword="null"/> for every database.</param>
/// <param name="Against">The baseline whose facts are never stale and never folded; <see langword="null"/>: the facts each database's
/// committed physical snapshot lists as rendered, else (no committed snapshot) only pending facts are folded.</param>
/// <param name="DryRun">Whether to write nothing and return the files instead.</param>
/// <param name="Source">What caused the change.</param>
public sealed record FoldRequest(string? Database, ModelBaseline? Against, bool DryRun, ChangeSource Source);

/// <summary>Facts joined into one.</summary>
/// <param name="Kept">The id of the fact that holds the join (the last one: its id and file stay).</param>
/// <param name="Removed">The ids of the facts joined into it and deleted, in applied order.</param>
/// <param name="Fact">The joined fact's type.</param>
public sealed record FoldJoin(string Kept, IReadOnlyList<string> Removed, string Fact);

/// <summary>Facts fold left as they are, and why.</summary>
/// <param name="Facts">The fact ids, in applied order.</param>
/// <param name="Reason">Why they were not joined.</param>
public sealed record FoldRefusal(IReadOnlyList<string> Facts, string Reason);

/// <summary>The result of <c>migration fold</c>.</summary>
/// <param name="Outcome">Saved (or, for a dry run, would be saved) when the batch goes through; otherwise the batch's outcome and nothing
/// was written.</param>
/// <param name="Joined">The joins.</param>
/// <param name="RemovedStale">The stale facts removed, by id.</param>
/// <param name="Refused">The joins refused.</param>
/// <param name="Files">For a dry run, every file the fold would change or delete; empty otherwise.</param>
/// <param name="Diagnostics">Why the fold was refused: the model's errors, or the batch's.</param>
public sealed record FoldResult(SaveOutcome Outcome, IReadOnlyList<FoldJoin> Joined, IReadOnlyList<string> RemovedStale,
    IReadOnlyList<FoldRefusal> Refused, IReadOnlyList<BatchPreviewFile> Files, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Listing and folding migration facts (next-phase-1-design.md section 3.2; tasks A13 and A14).</summary>
public sealed partial class ModelStore
{
    private const string MigrationCursorVersion = "m1";

    /// <summary>
    /// The open migration facts, in applied order (by database name, then the order each database's <c>after</c> links give, ties by
    /// id), a page at a time: the order the resolved model, <c>migration list</c> and <c>list_migrations</c> share. Each fact carries its
    /// state, the intent it lacks, whether it is stale (as <see cref="ValidateAsync(ValidationScope, CancellationToken)"/> judges it: MQ4103), its summary, file and content
    /// hash.
    /// </summary>
    /// <param name="database">Only this database's facts, by name or id; <see langword="null"/> for every database.</param>
    /// <param name="cursor">The <see cref="MigrationList.Next"/> of the previous page; the page starts after that fact's position in the
    /// current order.</param>
    /// <param name="limit">The page size, 1 to <see cref="ModelPages.MaxLimit"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ArgumentException">An unknown database, a cursor no listing returned (or whose fact is gone), or a page size out
    /// of range.</exception>
    public async Task<MigrationList> ListMigrationsAsync(string? database, string? cursor, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > ModelPages.MaxLimit)
            throw new ArgumentException($"limit must be from 1 to {ModelPages.MaxLimit}.", nameof(limit));
        var after = string.IsNullOrEmpty(cursor) ? ((string, string)?)null : ReadMigrationCursor(cursor);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var databases = ChosenDatabases(snapshot, database);
        var facts = snapshot.All<Migration>().ToLookup(f => f.Database, StringComparer.Ordinal);
        var rows = new List<(Database Database, Migration Fact, int Order)>();
        foreach (var owner in databases)
        {
            var ordered = FactKeys.Order(facts[owner.Id]);
            for (var i = 0; i < ordered.Count; i++)
                rows.Add((owner, ordered[i], i + 1));
        }

        var start = 0;
        if (after is var (afterDatabase, afterFact))
        {
            var at = rows.FindIndex(r => string.Equals(r.Database.Id, afterDatabase, StringComparison.Ordinal) && string.Equals(r.Fact.Id, afterFact, StringComparison.Ordinal));
            if (at < 0)
                throw new ArgumentException("cursor names a fact that is no longer listed (folded or removed); list again from the start.", nameof(cursor));
            start = at + 1;
        }

        var page = rows.Skip(start).Take(limit).ToList();
        var stale = page.Count == 0 ? FrozenSet<string>.Empty : await StaleFactIdsAsync(ct).ConfigureAwait(false);
        var entries = page.Select(r => Entry(snapshot, r.Database, r.Fact, r.Order, stale)).ToList();
        var next = start + page.Count < rows.Count ? WriteMigrationCursor(page[^1].Database.Id, page[^1].Fact.Id) : null;
        return new MigrationList(entries, rows.Count, next);
    }

    /// <summary>
    /// Folds open migration facts (next-phase-1-design.md section 3.2, "Fold"; <see cref="FactFolder"/>): per chosen database, the
    /// open facts the baseline lacks are the candidates (the <see cref="FoldRequest.Against"/> model's facts, else the facts the
    /// committed physical snapshot lists as rendered, else only pending facts); stale chains are removed first (against a baseline model,
    /// also the value maps whose codes the baseline's types never held, next-phase-2-design.md section 3.5), then consecutive facts
    /// on one object are joined into the last one; value maps never join. The writes go through one batch on the store's atomic path, which records no facts
    /// (only facts change); a dry run writes nothing and returns the files.
    /// </summary>
    /// <param name="request">What to fold.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The joins, removals and refusals, and the batch's outcome: <see cref="SaveOutcome.Invalid"/> with the errors when the
    /// model has errors outside the migration rules (the facts' objects cannot be judged), or when the batch is refused;
    /// <see cref="SaveOutcome.Conflict"/> when a fact file changed meanwhile.</returns>
    /// <exception cref="ArgumentException">An unknown database.</exception>
    public async Task<FoldResult> FoldMigrationsAsync(FoldRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var databases = ChosenDatabases(snapshot, request.Database);

        // The stale chains judge the facts against the model's captures: errors outside the migration rules keep them from being
        // trusted. The migration rules' own errors (a pending fact, two facts on one object) are what fold is for.
        var report = await ValidateAsync(ValidationScope.All, ct).ConfigureAwait(false);
        var blocking = report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && !d.Rule.StartsWith("MQ41", StringComparison.Ordinal)).ToList();
        if (blocking.Count > 0)
            return new FoldResult(SaveOutcome.Invalid, [], [], [], [], blocking);

        FrozenSet<string>? baselineFacts = null;
        BaselineCodes? baselineCodes = null; // a value map is judged against a baseline model only (next-phase-2-design.md section 3.5)
        if (request.Against is { } against)
        {
            var baseModel = await against.Store.GetSnapshotAsync(ct).ConfigureAwait(false);
            baselineFacts = baseModel.All<Migration>().Select(f => f.Id).ToFrozenSet(StringComparer.Ordinal);
            baselineCodes = BaselineCodes.Of(baseModel);
        }

        var facts = snapshot.All<Migration>().ToLookup(f => f.Database, StringComparer.Ordinal);
        var chosen = databases.Where(d => facts[d.Id].Any()).ToList();
        var captures = await CaptureDatabasesAsync(snapshot, [.. chosen.Select(d => d.Id)], ct).ConfigureAwait(false);
        var joined = new List<FoldJoin>();
        var removedStale = new List<string>();
        var refused = new List<FoldRefusal>();
        var changed = new List<Migration>();
        var deleted = new List<string>();
        foreach (var owner in chosen)
        {
            if (!captures.Captures.TryGetValue(owner.Id, out var capture))
                continue; // the database does not resolve: nothing of it can be judged
            capture = Without(capture, captures.FailedOwners, []);
            Func<Migration, bool> candidate;
            if (baselineFacts is not null)
            {
                candidate = f => !baselineFacts.Contains(f.Id);
            }
            else if (await CommittedAsync(owner, ct).ConfigureAwait(false) is { } committed)
            {
                var rendered = RenderedFacts(committed);
                candidate = f => !rendered.Contains(f.Id);
            }
            else
            {
                candidate = f => MigrationRules.State(f, owner.Dialect) == "pending"; // a pending fact never reaches a migration
            }

            var plan = FactFolder.Fold(FactKeys.Order(facts[owner.Id]), candidate, capture, owner.Dialect, baselineCodes);
            joined.AddRange(plan.Joined);
            removedStale.AddRange(plan.RemovedStale);
            refused.AddRange(plan.Refused);
            changed.AddRange(plan.Changed);
            deleted.AddRange(plan.Deleted);
        }

        if (changed.Count == 0 && deleted.Count == 0)
            return new FoldResult(SaveOutcome.Saved, joined, removedStale, refused, [], []);

        var operations = new List<BatchOperation>(changed.Count + deleted.Count);
        foreach (var fact in changed)
        {
            var document = snapshot.GetDocument(fact.Id)!;
            var bytes = FactDocuments.Bytes(_services.Json, fact, _paths.Value.ToModelPath(document.Path) ?? document.Path);
            using var parsed = JsonDocument.Parse(bytes);
            operations.Add(new BatchOperation(BatchOp.Update, fact.Id, document.Hash, parsed.RootElement.Clone()));
        }

        foreach (var id in deleted)
            operations.Add(new BatchOperation(BatchOp.Delete, id, snapshot.GetDocument(id)!.Hash, null));

        var batch = new ModelBatch(operations);
        if (request.DryRun)
        {
            var preview = await PreviewBatchAsync(batch, ct).ConfigureAwait(false);
            return new FoldResult(preview.Outcome, joined, removedStale, refused, preview.Files, [.. preview.Items.SelectMany(i => i.Diagnostics)]);
        }

        var result = await ApplyBatchAsync(batch, request.Source, ct).ConfigureAwait(false);
        return new FoldResult(result.Outcome, joined, removedStale, refused, [], [.. result.Items.SelectMany(i => i.Diagnostics)]);
    }

    /// <summary>The databases a listing or a fold covers: every one by name (then id), or the one named or identified.</summary>
    /// <exception cref="ArgumentException">No database has that id or name.</exception>
    private static List<Database> ChosenDatabases(ModelSnapshot snapshot, string? database)
    {
        var all = snapshot.All<Database>().OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        if (string.IsNullOrEmpty(database))
            return all;
        var chosen = all.Where(d => string.Equals(d.Id, database, StringComparison.Ordinal)).ToList();
        if (chosen.Count == 0)
            chosen = all.Where(d => string.Equals(d.Name, database, StringComparison.Ordinal)).ToList();
        return chosen.Count > 0 ? chosen : throw new ArgumentException($"No database is named '{database}' or has that id.", nameof(database));
    }

    /// <summary>A database's committed physical snapshot, or <see langword="null"/> when it has none or it cannot be read.</summary>
    private async Task<PhysicalSnapshot?> CommittedAsync(Database database, CancellationToken ct)
    {
        if (IsReadOnly)
            return null; // a read-only store's physical snapshots would be the working project's
        try
        {
            return await _services.Snapshots.LoadAsync(database.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null; // generation reports the unreadable file (MQ1001)
        }
    }

    /// <summary>The ids of the stale facts whole-model validation reports (MQ4103, on the fact).</summary>
    private async Task<FrozenSet<string>> StaleFactIdsAsync(CancellationToken ct)
    {
        var report = await ValidateAsync(ValidationScope.All, ct).ConfigureAwait(false);
        return report.Diagnostics.Where(d => d.Rule == "MQ4103" && d.ElementId is not null).Select(d => d.ElementId!).ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>One listed fact.</summary>
    private static MigrationEntry Entry(ModelSnapshot snapshot, Database database, Migration fact, int order, FrozenSet<string> stale)
    {
        var document = snapshot.GetDocument(fact.Id);
        var state = MigrationRules.State(fact, database.Dialect);
        return new MigrationEntry(fact.Id, database.Id, database.Name, order, ResolutionValues.Kebab(fact.Fact), state, stale.Contains(fact.Id),
            state == "pending" ? MigrationRules.Missing(fact, database.Dialect) : [], FactDocuments.Summary(fact), fact.Object, fact.After,
            document?.Path ?? "", document is null ? FactHash.Of(fact) : FactHash.Of(document.Json));
    }

    /// <summary>The cursor after a listed fact: opaque base64url text of a version, the database id and the fact id.</summary>
    private static string WriteMigrationCursor(string databaseId, string factId) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(MigrationCursorVersion + "\0" + databaseId + "\0" + factId));

    /// <summary>The database and fact ids a cursor names.</summary>
    /// <exception cref="ArgumentException">The text is not a cursor a listing returned.</exception>
    private static (string Database, string Fact) ReadMigrationCursor(string cursor)
    {
        const string Refusal = "cursor is not a cursor list_migrations returned; pass next from the previous page as it is.";
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (FormatException)
        {
            throw new ArgumentException(Refusal, nameof(cursor));
        }

        var parts = text.Split('\0');
        return parts.Length == 3 && parts[0] == MigrationCursorVersion ? (parts[1], parts[2]) : throw new ArgumentException(Refusal, nameof(cursor));
    }
}
