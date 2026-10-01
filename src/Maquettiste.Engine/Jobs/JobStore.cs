using System.Text.Json;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Engine.Jobs;

/// <summary>
/// Persists job records (<see cref="JobRecord"/>: the job and its request) in <c>CacheDirectory/jobs/&lt;id&gt;.json</c>
/// (host-contracts requirement 27), serialized with <see cref="JsonSerializerDefaults.Web"/>. Records are written when a job is queued, when it starts and when it finishes, so a
/// restart finds the jobs it must resume. The newest <see cref="Keep"/> records are kept. Every write and deletion goes through the
/// engine-write guard (<see cref="WriteTarget.Cache"/>).
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="paths">The engine-write guard.</param>
internal sealed class JobStore(EngineOptions options, IOutputPathPolicy paths)
{
    /// <summary>How many job records are kept.</summary>
    public const int Keep = 200;

    private readonly EngineFiles _files = new(paths, WriteTarget.Cache, options);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The jobs folder.</summary>
    internal string Folder => Path.Combine(Path.GetFullPath(options.CacheDirectory), "jobs");

    /// <summary>Saves a job record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SaveAsync(JobRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var file = FileOf(record.Job.Id) ?? throw new ArgumentException("Invalid job id.", nameof(record));
        await _files.WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(record, _json), ct).ConfigureAwait(false);
    }

    /// <summary>Loads a job record.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The record, or <see langword="null"/>.</returns>
    public async Task<JobRecord?> LoadAsync(string id, CancellationToken ct)
    {
        var file = FileOf(id);
        if (file is null || !File.Exists(file))
            return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize<JobRecord>(bytes, _json);
            return record?.Job is not null && record.Request is not null && string.Equals(record.Job.Id, id, StringComparison.Ordinal) ? record : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Loads every job record, newest id first.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The records.</returns>
    public async Task<IReadOnlyList<JobRecord>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Folder))
            return [];
        var jobs = new List<JobRecord>();
        foreach (var id in Ids())
        {
            if (await LoadAsync(id, ct).ConfigureAwait(false) is { } job)
                jobs.Add(job);
        }

        return jobs;
    }

    /// <summary>Deletes all but the newest <see cref="Keep"/> finished records; records of queued or running jobs are kept.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task PruneAsync(CancellationToken ct)
    {
        var finished = 0;
        foreach (var id in Ids())
        {
            var record = await LoadAsync(id, ct).ConfigureAwait(false);
            if (record is not null && record.Job.State is JobState.Queued or JobState.Running)
                continue;
            if (++finished <= Keep)
                continue;
            try
            {
                _files.Delete(FileOf(id)!);
            }
            catch (IOException)
            {
            }
        }
    }

    private List<string> Ids() =>
        Directory.Exists(Folder)
            ? [.. Directory.EnumerateFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(IdFormat.IsValid)
                .OrderDescending(StringComparer.Ordinal)]
            : [];

    private string? FileOf(string id) => IdFormat.IsValid(id) ? Path.Combine(Folder, id + ".json") : null;
}
