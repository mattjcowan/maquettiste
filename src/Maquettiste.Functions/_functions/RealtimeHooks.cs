using Microsoft.AspNetCore.Http;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>
/// The realtime and AI hooks (phase2-design.md section 3.4). Hooks run without middleware, so each identifies the caller itself
/// through <see cref="EditorAuth.Identify"/>, the same rules as the sign-in gate; they register nothing on the request.
/// </summary>
public static class RealtimeHooks
{
    /// <summary>The group every editor window joins.</summary>
    public const string EditorsGroup = "editors";

    /// <summary>Admits the same callers as the sign-in gate to the hub and names the connection's user.</summary>
    /// <param name="context">The connecting request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <returns>The user name, or <see langword="null"/> to refuse.</returns>
    [RealtimeConnect]
    public static string? Connect(HttpContext context, EditorAuth auth)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auth);
        if (context.Request.Headers.Origin is { Count: > 0 } origin && !SignInGate.SameOrigin(origin.ToString(), context.Request))
            return null;
        return auth.Identify(context)?.Name;
    }

    /// <summary>Admits a signed-in connection to <c>editors</c> and to <c>job:{id}</c> for a job id (a ULID); refuses every other group.</summary>
    /// <param name="context">The joining connection's request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="group">The group asked for.</param>
    /// <returns><see langword="true"/> to let it join.</returns>
    [RealtimeJoin]
    public static bool Join(HttpContext context, EditorAuth auth, string group)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auth);
        if (!IsEditorGroup(group))
            return false;
        return auth.Identify(context) is not null;
    }

    /// <summary>Admits signed-in callers to <c>/_host/ai/chat</c> (the phase 2 editor does not chat; the hook keeps it closed to others).</summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <returns><see langword="true"/> to let the caller chat.</returns>
    [AiAccess]
    public static bool Ai(HttpContext context, EditorAuth auth)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auth);
        return auth.Identify(context) is not null;
    }

    /// <summary>Whether a group is one the editor uses: <c>editors</c>, or <c>job:</c> and a ULID.</summary>
    /// <param name="group">The group.</param>
    /// <returns><see langword="true"/> for an editor group.</returns>
    public static bool IsEditorGroup(string? group) =>
        group == EditorsGroup || (group is { Length: 30 } && group.StartsWith("job:", StringComparison.Ordinal) && Api.IsUlid(group[4..]));
}
