namespace Maquettiste.Engine;

/// <summary>
/// Engine options. Nothing is read from the environment or the current directory (host-contracts requirement 4): every
/// folder is explicit or derived from <see cref="RepoRoot"/>.
/// </summary>
public sealed record EngineOptions
{
    /// <summary>The absolute repo root; output paths are relative to it.</summary>
    public required string RepoRoot { get; init; }

    /// <summary>The model root; <see langword="null"/> means <c>&lt;RepoRoot&gt;/.maquettiste</c>. May be another mount of the same folder.</summary>
    public string? ModelRoot { get; init; }

    /// <summary>The folder for the index cache, unit state, plans and jobs (host-contracts requirement 10); never under a bind mount.</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>The folder for the run journal and run lock; <see langword="null"/> means <c>&lt;ModelRoot&gt;/.cache</c>.</summary>
    public string? JournalDirectory { get; init; }

    /// <summary>The engine's parallelism; 0 means <see cref="Environment.ProcessorCount"/>.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>The id generator; <see langword="null"/> means <see cref="UlidIdGenerator"/>.</summary>
    public IIdGenerator? IdGenerator { get; init; }

    /// <summary>The clock for job timestamps and new ids only; never reaches generated output. <see langword="null"/> means <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>The effective model root.</summary>
    internal string EffectiveModelRoot => ModelRoot ?? Path.Combine(RepoRoot, ".maquettiste");

    /// <summary>The effective journal folder.</summary>
    internal string EffectiveJournalDirectory => JournalDirectory ?? Path.Combine(EffectiveModelRoot, ".cache");

    /// <summary>The effective parallelism.</summary>
    internal int EffectiveParallelism => MaxDegreeOfParallelism > 0 ? MaxDegreeOfParallelism : Environment.ProcessorCount;

    /// <summary>The effective clock.</summary>
    internal TimeProvider EffectiveTimeProvider => TimeProvider ?? System.TimeProvider.System;

    /// <summary>The effective id generator.</summary>
    internal IIdGenerator EffectiveIdGenerator => IdGenerator ?? new UlidIdGenerator(EffectiveTimeProvider);
}
