using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>The body of <c>PUT /api/presence</c>.</summary>
/// <param name="ConnectionId"><c>site.realtime.connectionId</c> of the reporting window.</param>
/// <param name="ElementId">The selected element, if any.</param>
/// <param name="Workspace">The workspace, if any.</param>
public sealed record PresenceReport(string? ConnectionId, string? ElementId, string? Workspace);

/// <summary><c>PUT /api/presence</c> (PD14): the browser cannot publish, so it reports here and the server publishes.</summary>
public static class PresenceEndpoints
{
    private static readonly string[] Workspaces = ["entities", "reference-data", "database", "mappings", "generate", "settings"];

    /// <summary>Stores the selection of a realtime connection that belongs to the caller and publishes <c>presence.changed</c> to <c>editors</c>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="realtime">The site's realtime side.</param>
    /// <param name="presence">The presence registry.</param>
    /// <param name="events">The publisher.</param>
    /// <param name="time">The clock.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>204, 404 (no such connection of the caller's) or 400 (bad connection id, element id or workspace).</returns>
    [HttpPut("/api/presence")]
    public static Task<IResult> Report(HttpContext context, IRealtime realtime, PresenceRegistry presence, EditorEvents events, TimeProvider time,
        CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(realtime);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(time);
        var (report, error) = await Api.ReadJsonAsync<PresenceReport>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (string.IsNullOrEmpty(report!.ConnectionId))
            return Api.BadRequest("connectionId is required.");
        if (report.Workspace is not null && !Workspaces.Contains(report.Workspace))
            return Api.BadRequest($"workspace must be one of {string.Join(", ", Workspaces)}.");
        if (report.ElementId is not null && !Api.IsUlid(report.ElementId))
            return Api.BadRequest("elementId must be an element id (a ULID) or null.");
        var user = EditorUser.From(context.User)?.Name;
        var connection = realtime.Connections.FirstOrDefault(c => c.Id == report.ConnectionId);
        if (user is null || connection is null || !string.Equals(connection.User, user, StringComparison.Ordinal))
            return Api.NotFound("connection of yours", report.ConnectionId);
        presence.Report(new PresenceEntry(connection.Id, user, report.ElementId, report.Workspace, time.GetUtcNow()));
        await events.PublishPresenceAsync(presence, ct).ConfigureAwait(false);
        return Results.NoContent();
    });
}
