using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>The body of <c>POST /api/assist/chat</c>: one new message. Any other member (tool turns included) is ignored.</summary>
/// <param name="ConversationId">The conversation to continue, or <see langword="null"/> for a new one.</param>
/// <param name="Message">The user's text.</param>
/// <param name="Context">The context chips the user kept.</param>
public sealed record AssistChatRequest(string? ConversationId, string? Message, AssistantContext? Context);

/// <summary>The body of <c>PUT /api/assist/conversations/{id}/proposals/{proposalId}</c>.</summary>
/// <param name="State"><c>applied</c> or <c>discarded</c>.</param>
public sealed record AssistProposalStateRequest(string? State);

/// <summary>
/// The assistant's routes (SPEC Section 14 "Assist", erratum E44). Each checks who asks itself (<see cref="EditorAuth"/>, the same
/// rules as the sign-in gate): calls from functions bypass the host's <c>[AiAccess]</c> hook, so the gate is not trusted alone.
/// Viewers may chat and read; applying a proposal goes through <c>POST /api/model/batch</c>, which needs an editor.
/// </summary>
public static class AssistEndpoints
{
    private static readonly string[] Roles = ["viewer", "editor", "maintainer", "admin"];

    /// <summary>Whether the site has an AI provider, its model, the budgets and the caller's usage today.</summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="ai">The host's AI.</param>
    /// <param name="store">The model store.</param>
    /// <param name="assist">The assistant service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the status, or 401.</returns>
    [HttpGet("/api/assist/status")]
    public static Task<IResult> Status(HttpContext context, EditorAuth auth, IAiChat ai, ModelStore store, AssistService assist, CancellationToken ct) =>
        Api.GuardAsync(context, async () =>
        {
            ArgumentNullException.ThrowIfNull(ai);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(assist);
            if (Caller(context, auth) is not { } user)
                return Unauthenticated();
            var settings = (await store.GetSnapshotAsync(ct).ConfigureAwait(false)).Settings.Assistant;
            var url = auth.VariablesFor(context).Get(EditorSettings.HostAiUrlVariable).Trim();
            return Api.Json(new JsonObject
            {
                ["configured"] = ai.IsConfigured,
                ["model"] = ai.IsConfigured ? ai.Model : null,
                ["hostAiUrl"] = url.Length > 0 ? url : null,
                ["canApply"] = CanApply(user),
                ["maxTurns"] = settings.MaxTurns,
                ["tokenBudgetPerRequest"] = settings.TokenBudgetPerRequest,
                ["tokenBudgetPerDayPerUser"] = settings.TokenBudgetPerDayPerUser,
                ["usedToday"] = await assist.UsedTodayAsync(user.Name, ct).ConfigureAwait(false),
                ["hasInstructions"] = !string.IsNullOrWhiteSpace(settings.Instructions),
            });
        });

    /// <summary>
    /// Sends one message and streams the answer as server-sent events. Checks before the stream opens: the caller (401), the body (400),
    /// the provider (503 <c>assist-not-configured</c>), the day's budget (429 <c>assist-budget-exhausted</c>), the conversation (404) and
    /// that it is not already answering (409 <c>assist-busy</c>).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="ai">The host's AI.</param>
    /// <param name="store">The model store.</param>
    /// <param name="assist">The assistant service.</param>
    /// <param name="time">The clock.</param>
    /// <param name="ct">Cancellation (the browser closed the stream).</param>
    /// <returns>The event stream, or a problem.</returns>
    [HttpPost("/api/assist/chat")]
    public static Task<IResult> Chat(HttpContext context, EditorAuth auth, IAiChat ai, ModelStore store, AssistService assist, TimeProvider time, CancellationToken ct) =>
        Api.GuardAsync(context, async () =>
        {
            ArgumentNullException.ThrowIfNull(ai);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(assist);
            ArgumentNullException.ThrowIfNull(time);
            if (Caller(context, auth) is not { } user)
                return Unauthenticated();
            var (request, error) = await Api.ReadJsonAsync<AssistChatRequest>(context.Request, null, ct).ConfigureAwait(false);
            if (error is not null)
                return error;
            var message = request!.Message?.Trim() ?? "";
            if (message.Length == 0)
                return Api.BadRequest("message is required.");
            if (message.Length > 16000)
                return Api.BadRequest("message is longer than 16000 characters.");
            if (request.ConversationId is { } given && !Api.IsUlid(given))
                return Api.BadRequest("conversationId must be a conversation id (a ULID) or null.");
            if (!ai.IsConfigured)
            {
                return Api.Problem("assist-not-configured", "The site has no AI provider. An administrator sets one in the host's management UI (AI, then the site's AI settings).",
                    StatusCodes.Status503ServiceUnavailable);
            }

            var budget = (await store.GetSnapshotAsync(ct).ConfigureAwait(false)).Settings.Assistant.TokenBudgetPerDayPerUser;
            if (await assist.UsedTodayAsync(user.Name, ct).ConfigureAwait(false) >= budget)
                return Api.Problem("assist-budget-exhausted", "Your assistant budget for today is spent; it resets at midnight UTC.", StatusCodes.Status429TooManyRequests);

            AssistConversationFile conversation;
            if (request.ConversationId is { } id)
            {
                if (await assist.LoadAsync(user.Name, id, ct).ConfigureAwait(false) is not { } loaded)
                    return Api.NotFound("conversation", id);
                conversation = loaded;
            }
            else
            {
                var now = time.GetUtcNow();
                conversation = new AssistConversationFile
                {
                    Id = assist.NewId(),
                    User = user.Name,
                    Title = message.Length <= 80 ? message.ReplaceLineEndings(" ") : message[..80].ReplaceLineEndings(" ") + "…",
                    CreatedUtc = now,
                    UpdatedUtc = now,
                };
            }

            if (!assist.TryEnter(conversation.Id))
                return Api.Problem("assist-busy", "This conversation is answering another message.", StatusCodes.Status409Conflict);
            var context2 = request.Context is { } c ? c with { Selection = c.Selection?.Take(200).ToList() } : null;
            return new EventStream(assist, ai, new AssistRun(user.Name, conversation, message, context2));
        });

    /// <summary>The caller's conversations in this project, newest first.</summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="assist">The assistant service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 401.</returns>
    [HttpGet("/api/assist/conversations")]
    public static Task<IResult> List(HttpContext context, EditorAuth auth, AssistService assist, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(assist);
        if (Caller(context, auth) is not { } user)
            return Unauthenticated();
        var items = new JsonArray();
        foreach (var c in await assist.ListAsync(user.Name, ct).ConfigureAwait(false))
        {
            items.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["title"] = c.Title,
                ["createdUtc"] = JsonValue.Create(c.CreatedUtc),
                ["updatedUtc"] = JsonValue.Create(c.UpdatedUtc),
                ["entryCount"] = c.Entries.Count,
            });
        }

        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(new JsonObject { ["items"] = items });
    });

    /// <summary>One of the caller's conversations as the panel shows it.</summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="assist">The assistant service.</param>
    /// <param name="id">The conversation id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 404 or 401.</returns>
    [HttpGet("/api/assist/conversations/{id}")]
    public static Task<IResult> Get(HttpContext context, EditorAuth auth, AssistService assist, string id, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(assist);
        if (Caller(context, auth) is not { } user)
            return Unauthenticated();
        if (await assist.LoadAsync(user.Name, id, ct).ConfigureAwait(false) is not { } c)
            return Api.NotFound("conversation", id);
        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(new JsonObject
        {
            ["id"] = c.Id,
            ["title"] = c.Title,
            ["createdUtc"] = JsonValue.Create(c.CreatedUtc),
            ["updatedUtc"] = JsonValue.Create(c.UpdatedUtc),
            ["model"] = c.Model,
            ["entries"] = c.Entries.DeepClone(),
            ["truncated"] = c.Truncated,
        });
    });

    /// <summary>Clears (deletes) one of the caller's conversations.</summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="assist">The assistant service.</param>
    /// <param name="id">The conversation id.</param>
    /// <returns>204, 404, 409 or 401.</returns>
    [HttpDelete("/api/assist/conversations/{id}")]
    public static IResult Delete(HttpContext context, EditorAuth auth, AssistService assist, string id) => Api.Guard(context, () =>
    {
        ArgumentNullException.ThrowIfNull(assist);
        if (Caller(context, auth) is not { } user)
            return Unauthenticated();
        if (assist.IsBusy(id))
            return Api.Problem("assist-busy", "This conversation is answering a message; stop it first.", StatusCodes.Status409Conflict);
        return assist.Delete(user.Name, id) ? Results.NoContent() : Api.NotFound("conversation", id);
    });

    /// <summary>Records that the user applied or discarded a pending proposal.</summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="assist">The assistant service.</param>
    /// <param name="id">The conversation id.</param>
    /// <param name="proposalId">The proposal id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 400, 404, 409 or 401.</returns>
    [HttpPut("/api/assist/conversations/{id}/proposals/{proposalId}")]
    public static Task<IResult> SetProposalState(HttpContext context, EditorAuth auth, AssistService assist, string id, string proposalId, CancellationToken ct) =>
        Api.GuardAsync(context, async () =>
        {
            ArgumentNullException.ThrowIfNull(assist);
            if (Caller(context, auth) is not { } user)
                return Unauthenticated();
            var (request, error) = await Api.ReadJsonAsync<AssistProposalStateRequest>(context.Request, null, ct).ConfigureAwait(false);
            if (error is not null)
                return error;
            if (request!.State is not ("applied" or "discarded"))
                return Api.BadRequest("state must be applied or discarded.");
            var (proposal, notPending) = await assist.SetProposalStateAsync(user.Name, id, proposalId, request.State, ct).ConfigureAwait(false);
            if (proposal is null)
                return Api.NotFound("proposal", proposalId);
            if (notPending)
                return Api.Problem("conflict", $"The proposal is already {(string?)proposal["state"]}.", StatusCodes.Status409Conflict);
            return Api.Json(proposal);
        });

    /// <summary>The caller: the user the sign-in gate put on the request, else the same check done here.</summary>
    private static EditorUser? Caller(HttpContext context, EditorAuth auth)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auth);
        if (auth.HasWrongBearer(context))
            return null;
        return EditorUser.From(context.User) ?? auth.Identify(context);
    }

    private static bool CanApply(EditorUser user) => Array.IndexOf(Roles, user.Role) >= Array.IndexOf(Roles, "editor");

    private static IResult Unauthenticated() => Api.Problem("unauthenticated", "Sign in to use the editor.", StatusCodes.Status401Unauthorized);

    /// <summary>The server-sent event stream of one chat request: runs the loop and writes each event as it comes.</summary>
    private sealed class EventStream(AssistService assist, IAiChat ai, AssistRun run) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            var response = httpContext.Response;
            var ct = httpContext.RequestAborted;
            try
            {
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "text/event-stream; charset=utf-8";
                response.Headers.CacheControl = "no-cache, no-store";
                response.Headers["X-Accel-Buffering"] = "no";
                httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await response.Body.FlushAsync(ct).ConfigureAwait(false);
                await assist.RunAsync(ai, run, async data =>
                {
                    var bytes = Encoding.UTF8.GetBytes("event: " + (string?)data["type"] + "\ndata: " + data.ToJsonString(Api.JsonOptions) + "\n\n");
                    await response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The browser closed the stream; the loop stored what it had.
            }
            finally
            {
                assist.Exit(run.Conversation.Id);
            }
        }
    }
}
