using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// Persists plans (engine-design.md section 15): <c>CacheDirectory/plans/&lt;id&gt;/plan.json</c> holds the <see cref="GenerationPlan"/>
/// (serialized with <see cref="JsonSerializerDefaults.Web"/>), <c>write.json</c> the write settings it was made with
/// (<see cref="PlanWriteSettings"/>), and <c>blobs/&lt;ContentHash&gt;</c> the post-processed bytes of each file the plan would write. Blobs are written while the plan runs and <c>plan.json</c> last, so a plan folder without
/// <c>plan.json</c> is an unfinished plan that loads as missing. The 20 newest plans (ULIDs sort by time) are kept. Every write and
/// deletion goes through the engine-write guard (<see cref="WriteTarget.Cache"/>).
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="paths">The engine-write guard.</param>
internal sealed class PlanStore(EngineOptions options, IOutputPathPolicy paths)
{
    /// <summary>How many plans are kept.</summary>
    public const int Keep = 20;

    private readonly EngineFiles _files = new(paths, WriteTarget.Cache);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The plans folder.</summary>
    internal string Folder => Path.Combine(Path.GetFullPath(options.CacheDirectory), "plans");

    /// <summary>Writes a blob unless it is already stored.</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="contentHash">The content hash of the bytes.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WriteBlobAsync(string planId, string contentHash, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var file = BlobPath(planId, contentHash) ?? throw new ArgumentException("Invalid plan id or content hash.", nameof(planId));
        if (File.Exists(file))
            return;
        try
        {
            await _files.WriteAsync(file, bytes, ct).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(file))
        {
            // Two units with the same bytes stored the blob at once; the other write won, with the same content.
        }
    }

    /// <summary>Saves a plan's <c>plan.json</c>, then prunes old plans.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SaveAsync(GenerationPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var folder = PlanFolder(plan.Id) ?? throw new ArgumentException("Invalid plan id.", nameof(plan));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, _json);
        await _files.WriteAsync(Path.Combine(folder, "plan.json"), bytes, ct).ConfigureAwait(false);
        Prune(plan.Id);
    }

    /// <summary>
    /// Saves the write settings a plan was made with (<c>write.json</c>: the effective hand-edit policy per pack), which the apply
    /// compares with its own. Written before <c>plan.json</c>, so a finished plan always has it.
    /// </summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="policies">The policies by pack.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SaveWriteSettingsAsync(string planId, IReadOnlyDictionary<string, HandEditPolicy> policies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var folder = PlanFolder(planId) ?? throw new ArgumentException("Invalid plan id.", nameof(planId));
        var sorted = new SortedDictionary<string, HandEditPolicy>(policies.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PlanWriteSettings(sorted), _json);
        await _files.WriteAsync(Path.Combine(folder, "write.json"), bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Loads the write settings a plan was made with.</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The settings, or <see langword="null"/> when missing or unreadable.</returns>
    public async Task<PlanWriteSettings?> LoadWriteSettingsAsync(string planId, CancellationToken ct)
    {
        var folder = PlanFolder(planId);
        if (folder is null)
            return null;
        var file = Path.Combine(folder, "write.json");
        try
        {
            if (!File.Exists(file))
                return null;
            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<PlanWriteSettings>(bytes, _json);
            return settings?.HandEdits is null ? null : settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Loads a plan.</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan, or <see langword="null"/> when it is unknown, unfinished or unreadable.</returns>
    public async Task<GenerationPlan?> LoadAsync(string planId, CancellationToken ct)
    {
        var folder = PlanFolder(planId);
        if (folder is null)
            return null;
        var file = Path.Combine(folder, "plan.json");
        try
        {
            if (!File.Exists(file))
                return null;
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var plan = await JsonSerializer.DeserializeAsync<GenerationPlan>(stream, _json, ct).ConfigureAwait(false);
            return plan is not null && string.Equals(plan.Id, planId, StringComparison.Ordinal) ? plan : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads a blob.</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="contentHash">The content hash.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes, or <see langword="null"/> when missing or when they no longer hash to <paramref name="contentHash"/>.</returns>
    public async Task<byte[]?> ReadBlobAsync(string planId, string contentHash, CancellationToken ct)
    {
        var file = BlobPath(planId, contentHash);
        if (file is null || !File.Exists(file))
            return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            return string.Equals(ContentHash.Of(bytes), contentHash, StringComparison.Ordinal) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a blob is stored (its content is verified when read).</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="contentHash">The content hash.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    public bool HasBlob(string planId, string contentHash) => BlobPath(planId, contentHash) is { } file && File.Exists(file);

    /// <summary>Deletes a plan's folder (an unfinished plan).</summary>
    /// <param name="planId">The plan id.</param>
    public void Delete(string planId)
    {
        if (PlanFolder(planId) is { } folder)
            _files.DeleteFolder(folder);
    }

    /// <summary>Deletes all but the newest <see cref="Keep"/> plan folders; <paramref name="keepId"/> is always kept.</summary>
    /// <param name="keepId">A plan to keep.</param>
    internal void Prune(string keepId)
    {
        if (!Directory.Exists(Folder))
            return;
        var ids = Directory.EnumerateDirectories(Folder).Select(Path.GetFileName).OfType<string>().Where(IdFormat.IsValid)
            .OrderDescending(StringComparer.Ordinal).ToList();
        foreach (var id in ids.Skip(Keep))
        {
            if (string.Equals(id, keepId, StringComparison.Ordinal))
                continue;
            try
            {
                Delete(id);
            }
            catch (IOException)
            {
                // A plan being read elsewhere; the next prune removes it.
            }
        }
    }

    private string? PlanFolder(string planId) => IdFormat.IsValid(planId) ? Path.Combine(Folder, planId) : null;

    private string? BlobPath(string planId, string contentHash) =>
        PlanFolder(planId) is { } folder && ContentHash.IsValid(contentHash) ? Path.Combine(folder, "blobs", contentHash) : null;
}

/// <summary>The write settings a plan was made with (<c>plans/&lt;id&gt;/write.json</c>).</summary>
/// <param name="HandEdits">The effective hand-edit policy per pack (written in ordinal order; read back with ordinal keys).</param>
internal sealed record PlanWriteSettings(IReadOnlyDictionary<string, HandEditPolicy> HandEdits);
