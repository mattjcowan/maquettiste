using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Jobs;

/// <summary>
/// What a job record on disk holds besides <see cref="JobInfo"/>: the request, so a job that was queued or running when the process
/// stopped can be queued again by the next process (resume after a restart).
/// </summary>
/// <param name="Job">The job's state.</param>
/// <param name="Request">The job's request.</param>
internal sealed record JobRecord([property: JsonPropertyName("job")] JobInfo Job, [property: JsonPropertyName("request")] JobRequest Request);
