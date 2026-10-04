using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>A model turn kept on the server: what the site's model is sent again on every turn. Never accepted from the browser.</summary>
/// <param name="Role"><c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="Content">The text.</param>
/// <param name="ToolCalls">On an assistant turn that called tools, the calls.</param>
/// <param name="ToolCallId">On a tool result, the call it answers.</param>
/// <param name="IsError">On a tool result, whether the tool failed.</param>
public sealed record AssistTurn(string Role, string Content, IReadOnlyList<AssistToolCall>? ToolCalls = null, string? ToolCallId = null, bool IsError = false);

/// <summary>A tool call as stored.</summary>
/// <param name="Id">The provider's call id.</param>
/// <param name="Name">The tool name.</param>
/// <param name="Arguments">The arguments as the model sent them.</param>
public sealed record AssistToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>
/// A stored conversation (one file per conversation under the cache folder, per user): the model turns, the entries the panel shows,
/// and notes for the model about proposals the user applied or discarded since its last answer.
/// </summary>
public sealed class AssistConversationFile
{
    /// <summary>The conversation id (a ULID).</summary>
    public required string Id { get; init; }

    /// <summary>The owner's user name.</summary>
    public required string User { get; init; }

    /// <summary>The first message, cut to 80 characters.</summary>
    public required string Title { get; set; }

    /// <summary>When it was started.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>The model that answered last.</summary>
    public string? Model { get; set; }

    /// <summary>Whether older exchanges were dropped to keep it under <see cref="AssistService.MaxConversationBytes"/>.</summary>
    public bool Truncated { get; set; }

    /// <summary>The model turns.</summary>
    public List<AssistTurn> Turns { get; init; } = [];

    /// <summary>The panel's entries (AssistEntry in the contract).</summary>
    public JsonArray Entries { get; init; } = [];

    /// <summary>What to tell the model with the next message.</summary>
    public List<string> Notes { get; init; } = [];
}

/// <summary>One chat request, after the handler checked who asks and what.</summary>
/// <param name="User">The caller's user name.</param>
/// <param name="Conversation">The conversation (new or loaded).</param>
/// <param name="Message">The user's text.</param>
/// <param name="Context">The context chips the user kept.</param>
public sealed record AssistRun(string User, AssistConversationFile Conversation, string Message, AssistantContext? Context);

/// <summary>
/// The assistant's server side (erratum E44): the conversation store (per user and project, newest 50 kept, each capped in size), the
/// per-day token usage, and the agent loop over the host's <see cref="IAiChat"/> with the shared read tools (<see cref="AgentTools"/>)
/// and <c>propose_changes</c>. The loop follows the host's tool rules: an assistant turn with calls is followed at once by one result
/// per call; the tools are sent on every turn; the last turn (turn cap or token budget) is forced to text with
/// <see cref="AiToolChoice.None"/>; arguments that are not JSON get an error result; a turn cut by the token limit is reported.
/// </summary>
public sealed class AssistService
{
    /// <summary>The most conversations kept per user.</summary>
    public const int MaxConversations = 50;

    /// <summary>The largest stored conversation; older exchanges are dropped past it.</summary>
    public const int MaxConversationBytes = 1024 * 1024;

    /// <summary>The longest tool result sent to the model, in characters.</summary>
    public const int MaxToolResultChars = 24 * 1024;

    /// <summary>The output token limit of one model turn.</summary>
    public const int TurnMaxTokens = 4096;

    private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly ModelStore _store;
    private readonly EditorSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IIdGenerator _ids;
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userLocks = new(StringComparer.Ordinal);
    private readonly Lazy<string> _conventions = new(AgentConventions.Read);

    /// <summary>Creates the service.</summary>
    /// <param name="store">The model store.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="settings">The editor settings (the cache folder).</param>
    /// <param name="time">The clock.</param>
    /// <param name="loggers">The loggers.</param>
    public AssistService(ModelStore store, GenerationService generation, EditorSettings settings, TimeProvider time, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = loggers.CreateLogger("maquettiste.assist");
        _ids = settings.Engine.IdGenerator ?? new UlidIdGenerator(time);
        Catalog = new AgentTools(store, generation ?? throw new ArgumentNullException(nameof(generation)), new AgentToolsOptions
        {
            RepoRoot = settings.Engine.RepoRoot,
            Mode = "local",
            LogFailure = (tool, failure, _) =>
            {
                _logger.LogError("maquettiste: assistant tool {Tool} failed: {Failure}", tool, failure);
                return Task.CompletedTask;
            },
        });
    }

    /// <summary>The shared read tools.</summary>
    public AgentTools Catalog { get; }

    /// <summary>The tools the model is offered: the shared read tools and <c>propose_changes</c>.</summary>
    public static IReadOnlyList<AiTool> Tools { get; } =
        [.. AgentTools.All.Append(AssistantProposals.Tool).Select(t => new AiTool(t.Name, t.Description, t.InputSchema))];

    /// <summary>A new id.</summary>
    /// <returns>A ULID.</returns>
    public string NewId() => _ids.NewId();

    /// <summary>Marks a conversation busy; <see langword="false"/> when it already is.</summary>
    /// <param name="id">The conversation id.</param>
    /// <returns>Whether the caller now holds it.</returns>
    public bool TryEnter(string id) => _busy.TryAdd(id, 0);

    /// <summary>Releases a conversation marked by <see cref="TryEnter"/>.</summary>
    /// <param name="id">The conversation id.</param>
    public void Exit(string id) => _busy.TryRemove(id, out _);

    /// <summary>Whether a conversation is answering.</summary>
    /// <param name="id">The conversation id.</param>
    /// <returns><see langword="true"/> when busy.</returns>
    public bool IsBusy(string id) => _busy.ContainsKey(id);

    // ------------------------------------------------------------------ store

    private string UserFolder(string user) =>
        Path.Combine(_settings.Engine.CacheDirectory, "assist", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16]);

    private string ConversationPath(string user, string id) => Path.Combine(UserFolder(user), id + ".json");

    /// <summary>The caller's conversations, newest first.</summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The summaries.</returns>
    public async Task<IReadOnlyList<AssistConversationFile>> ListAsync(string user, CancellationToken ct)
    {
        var folder = UserFolder(user);
        if (!Directory.Exists(folder))
            return [];
        var list = new List<AssistConversationFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (Path.GetFileNameWithoutExtension(path) is { } name && Api.IsUlid(name) && await ReadAsync(path, ct).ConfigureAwait(false) is { } file)
                list.Add(file);
        }

        return [.. list.OrderByDescending(c => c.UpdatedUtc).ThenByDescending(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>Loads one of the caller's conversations.</summary>
    /// <param name="user">The user.</param>
    /// <param name="id">The conversation id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The conversation, or <see langword="null"/>.</returns>
    public Task<AssistConversationFile?> LoadAsync(string user, string id, CancellationToken ct) =>
        Api.IsUlid(id) ? ReadAsync(ConversationPath(user, id), ct) : Task.FromResult<AssistConversationFile?>(null);

    /// <summary>Deletes one of the caller's conversations.</summary>
    /// <param name="user">The user.</param>
    /// <param name="id">The conversation id.</param>
    /// <returns>Whether it existed.</returns>
    public bool Delete(string user, string id)
    {
        if (!Api.IsUlid(id))
            return false;
        var path = ConversationPath(user, id);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Records that the user applied or discarded a pending proposal, and notes it for the model.</summary>
    /// <param name="user">The user.</param>
    /// <param name="id">The conversation id.</param>
    /// <param name="proposalId">The proposal id.</param>
    /// <param name="state"><c>applied</c> or <c>discarded</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The proposal, <c>null</c> when there is none, or the flag that it was not pending.</returns>
    public async Task<(JsonObject? Proposal, bool NotPending)> SetProposalStateAsync(string user, string id, string proposalId, string state, CancellationToken ct)
    {
        var gate = _userLocks.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await LoadAsync(user, id, ct).ConfigureAwait(false) is not { } conversation)
                return (null, false);
            var proposal = conversation.Entries.OfType<JsonObject>()
                .Select(e => e["proposal"] as JsonObject)
                .FirstOrDefault(p => p is not null && (string?)p["id"] == proposalId);
            if (proposal is null)
                return (null, false);
            if ((string?)proposal["state"] != "pending")
                return (proposal, true);
            proposal["state"] = state;
            conversation.Notes.Add($"Since your last answer, the user {state} the proposal \"{(string?)proposal["summary"]}\" ({proposalId}).");
            conversation.UpdatedUtc = _time.GetUtcNow();
            await SaveAsync(conversation, CancellationToken.None).ConfigureAwait(false);
            return (proposal, false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The caller's tokens today (UTC).</summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count.</returns>
    public async Task<int> UsedTodayAsync(string user, CancellationToken ct)
    {
        var path = Path.Combine(UserFolder(user), "usage.json");
        try
        {
            if (!File.Exists(path))
                return 0;
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            return (string?)node?["day"] == Today() ? (int?)node?["tokens"] ?? 0 : 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidOperationException)
        {
            return 0;
        }
    }

    private async Task<int> AddUsageAsync(string user, int tokens)
    {
        var gate = _userLocks.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var total = await UsedTodayAsync(user, CancellationToken.None).ConfigureAwait(false) + Math.Max(0, tokens);
            Directory.CreateDirectory(UserFolder(user));
            await WriteAtomicAsync(Path.Combine(UserFolder(user), "usage.json"), new JsonObject { ["day"] = Today(), ["tokens"] = total }.ToJsonString()).ConfigureAwait(false);
            return total;
        }
        finally
        {
            gate.Release();
        }
    }

    private string Today() => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<AssistConversationFile?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AssistConversationFile>(stream, StoreJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a conversation (cut to its size cap) and keeps the user's newest <see cref="MaxConversations"/>.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SaveAsync(AssistConversationFile conversation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var text = JsonSerializer.Serialize(conversation, StoreJson);
        while (Encoding.UTF8.GetByteCount(text) > MaxConversationBytes && DropOldestExchange(conversation))
            text = JsonSerializer.Serialize(conversation, StoreJson);
        var folder = UserFolder(conversation.User);
        Directory.CreateDirectory(folder);
        await WriteAtomicAsync(ConversationPath(conversation.User, conversation.Id), text).ConfigureAwait(false);
        var files = new DirectoryInfo(folder).EnumerateFiles("*.json").Where(f => Api.IsUlid(Path.GetFileNameWithoutExtension(f.Name)))
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        foreach (var old in files.Skip(MaxConversations))
            old.Delete();
    }

    /// <summary>Drops the oldest exchange (a user turn and everything up to the next one), keeping the last; false when one is left.</summary>
    private static bool DropOldestExchange(AssistConversationFile conversation)
    {
        var next = conversation.Turns.FindIndex(1, t => t.Role == AiMessage.UserRole);
        var nextEntry = -1;
        for (var i = 1; i < conversation.Entries.Count; i++)
        {
            if ((string?)conversation.Entries[i]?["type"] == "user")
            {
                nextEntry = i;
                break;
            }
        }

        if (next < 0 || nextEntry < 0)
            return false;
        conversation.Turns.RemoveRange(0, next);
        for (var i = 0; i < nextEntry; i++)
            conversation.Entries.RemoveAt(0);
        conversation.Truncated = true;
        return true;
    }

    private static async Task WriteAtomicAsync(string path, string text)
    {
        var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    // ------------------------------------------------------------------ the loop

    /// <summary>
    /// Answers one message: appends it to the conversation, runs model turns until a text answer, streams each event through
    /// <paramref name="emit"/>, and stores the conversation (also when the caller goes away).
    /// </summary>
    /// <param name="ai">The host's AI.</param>
    /// <param name="run">The request.</param>
    /// <param name="emit">Writes one event (an AssistEvent object) to the stream.</param>
    /// <param name="ct">The request's cancellation (the browser closed the stream).</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(IAiChat ai, AssistRun run, Func<JsonObject, Task> emit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(emit);
        var conversation = run.Conversation;
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var limits = snapshot.Settings.Assistant;
        var maxTurns = Math.Clamp(limits.MaxTurns, 1, 25);
        var budget = Math.Max(1000, limits.TokenBudgetPerRequest);
        var projectName = snapshot.Settings.Name is { Length: > 0 } n ? n : Path.GetFileName(_settings.Engine.RepoRoot);
        var system = AssistantPrompt.System(projectName, AgentConventions.WithProjectConventions(_conventions.Value, _settings.Engine.RepoRoot), limits.Instructions);

        await emit(new JsonObject { ["type"] = "conversation", ["conversationId"] = conversation.Id, ["title"] = conversation.Title }).ConfigureAwait(false);
        var text = AssistantPrompt.UserMessage(run.Message, run.Context);
        if (conversation.Notes.Count > 0)
        {
            text = string.Join("\n", conversation.Notes) + "\n\n" + text;
            conversation.Notes.Clear();
        }

        conversation.Turns.Add(new AssistTurn(AiMessage.UserRole, text));
        conversation.Entries.Add(new JsonObject
        {
            ["type"] = "user",
            ["text"] = run.Message,
            ["context"] = run.Context is null ? null : JsonSerializer.SerializeToNode(run.Context, Api.JsonOptions),
        });

        var requestTokens = 0;
        var inputTokens = 0;
        var outputTokens = 0;
        var maxTokens = TurnMaxTokens;
        var raised = false;
        var answered = false;
        try
        {
            for (var turn = 1; ; turn++)
            {
                string? limited = turn >= maxTurns ? "turns" : requestTokens >= budget ? "budget" : null;
                var request = new AiChatRequest
                {
                    Messages = [.. conversation.Turns.Select(ToMessage)],
                    System = system,
                    Tools = Tools,
                    ToolChoice = limited is null ? AiToolChoice.Auto : AiToolChoice.None,
                    MaxTokens = maxTokens,
                };
                var streamed = new StringBuilder();
                AiChatResponse? answer = null;
                try
                {
                    await foreach (var chunk in ai.StreamAsync(request, ct).ConfigureAwait(false))
                    {
                        if (chunk.Text is { Length: > 0 } piece)
                        {
                            streamed.Append(piece);
                            await emit(new JsonObject { ["type"] = "text", ["delta"] = piece }).ConfigureAwait(false);
                        }

                        if (chunk.Final is not null)
                            answer = chunk.Final;
                    }
                }
                catch (AiChatException ex) when (!ct.IsCancellationRequested)
                {
                    var (code, message) = DescribeFailure(ex);
                    await FailAsync(conversation, emit, code, message).ConfigureAwait(false);
                    answered = true;
                    return;
                }

                if (answer is null)
                {
                    await FailAsync(conversation, emit, "provider", "The site's AI provider ended the answer without a final response.").ConfigureAwait(false);
                    answered = true;
                    return;
                }

                conversation.Model = answer.Model;
                var spent = answer.InputTokens + answer.OutputTokens;
                requestTokens += spent;
                inputTokens += answer.InputTokens;
                outputTokens += answer.OutputTokens;
                await AddUsageAsync(run.User, spent).ConfigureAwait(false);
                var said = answer.Text.Length > 0 ? answer.Text : streamed.ToString();
                var cut = answer.StopReason is "length" or "max_tokens";

                if (answer.ToolCalls.Count == 0 && cut && said.Trim().Length == 0 && !raised)
                {
                    // Every call of the turn was dropped when the model ran out of tokens part way: try once more with a larger limit.
                    raised = true;
                    maxTokens = TurnMaxTokens * 4;
                    continue;
                }

                if (answer.ToolCalls.Count == 0)
                {
                    conversation.Turns.Add(new AssistTurn(AiMessage.AssistantRole, said.Trim().Length > 0 ? said : "(no answer)"));
                    if (said.Trim().Length > 0)
                        conversation.Entries.Add(new JsonObject { ["type"] = "assistant", ["text"] = said });
                    answered = true;
                    if (cut)
                    {
                        await FailAsync(conversation, emit, "max-tokens",
                            "The model ran out of output tokens before it finished (its tool calls were dropped). Ask for a smaller step.").ConfigureAwait(false);
                        return;
                    }

                    var used = await UsedTodayAsync(run.User, CancellationToken.None).ConfigureAwait(false);
                    await emit(new JsonObject
                    {
                        ["type"] = "final",
                        ["text"] = said,
                        ["stopReason"] = answer.StopReason,
                        ["turns"] = turn,
                        ["inputTokens"] = inputTokens,
                        ["outputTokens"] = outputTokens,
                        ["usedToday"] = used,
                        ["limited"] = limited,
                    }).ConfigureAwait(false);
                    return;
                }

                if (said.Trim().Length > 0)
                    conversation.Entries.Add(new JsonObject { ["type"] = "assistant", ["text"] = said });
                var calls = answer.ToolCalls.Select(c => new AssistToolCall(c.Id, c.Name, c.Arguments.ValueKind == JsonValueKind.Undefined ? EmptyObject : c.Arguments.Clone())).ToList();
                var results = new List<AssistTurn>(calls.Count);
                foreach (var call in calls)
                {
                    await emit(new JsonObject { ["type"] = "tool-started", ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = JsonNode.Parse(call.Arguments.GetRawText()) }).ConfigureAwait(false);
                    var (content, isError) = limited is not null
                        ? ("No more tool calls in this answer (the " + (limited == "turns" ? "turn limit" : "token budget") + " is reached): answer in text now.", true)
                        : await CallToolAsync(conversation, call, emit, ct).ConfigureAwait(false);
                    if (content.Length > MaxToolResultChars)
                        content = content[..MaxToolResultChars] + "\n(The result is cut at " + MaxToolResultChars.ToString(CultureInfo.InvariantCulture) + " characters: read less at once, with limit, cursor, fields or ids.)";
                    var summary = Summarize(content, isError);
                    await emit(new JsonObject { ["type"] = "tool-finished", ["id"] = call.Id, ["name"] = call.Name, ["isError"] = isError, ["summary"] = summary }).ConfigureAwait(false);
                    conversation.Entries.Add(new JsonObject
                    {
                        ["type"] = "tool",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["arguments"] = JsonNode.Parse(call.Arguments.GetRawText()),
                        ["isError"] = isError,
                        ["summary"] = summary,
                    });
                    results.Add(new AssistTurn(AiMessage.ToolRole, content, ToolCallId: call.Id, IsError: isError));
                }

                // The calls and one result per call, together and in order: nothing may come between them.
                conversation.Turns.Add(new AssistTurn(AiMessage.AssistantRole, said, calls));
                conversation.Turns.AddRange(results);
                if (limited is not null)
                {
                    // The model called tools although told not to: close the answer so the conversation stays valid.
                    const string Stopped = "I stopped here: this answer reached its limit. Ask me to continue.";
                    conversation.Turns.Add(new AssistTurn(AiMessage.AssistantRole, Stopped));
                    conversation.Entries.Add(new JsonObject { ["type"] = "assistant", ["text"] = Stopped });
                    answered = true;
                    await emit(new JsonObject
                    {
                        ["type"] = "final",
                        ["text"] = Stopped,
                        ["stopReason"] = answer.StopReason,
                        ["turns"] = turn,
                        ["inputTokens"] = inputTokens,
                        ["outputTokens"] = outputTokens,
                        ["usedToday"] = await UsedTodayAsync(run.User, CancellationToken.None).ConfigureAwait(false),
                        ["limited"] = limited,
                    }).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The browser stopped the stream: keep what was answered and close the turn.
        }
#pragma warning disable CA1031 // The stream is already open: a failure becomes an error event, never a broken response.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "maquettiste: the assistant failed");
            await FailAsync(conversation, emit, "internal", "The assistant failed: " + ex.Message).ConfigureAwait(false);
            answered = true;
        }
        finally
        {
            if (!answered && conversation.Turns.Count > 0 && conversation.Turns[^1].Role != AiMessage.AssistantRole)
                conversation.Turns.Add(new AssistTurn(AiMessage.AssistantRole, "(The user stopped this answer.)"));
            conversation.UpdatedUtc = _time.GetUtcNow();
            try
            {
                await SaveAsync(conversation, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "maquettiste: the assistant could not store conversation {Id}", conversation.Id);
            }
        }
    }

    /// <summary>
    /// The error event for a provider failure, by the host's <see cref="AiChatException.Reason"/>. Only
    /// <see cref="AiChatException.ToolsUnsupported"/> says to choose another model: the host sets it only when the provider
    /// said plainly that the model cannot take tools, never for a tool schema it refused. Other failures keep the provider's
    /// message, which the host has already scrubbed of keys and addresses.
    /// </summary>
    internal static (string Code, string Message) DescribeFailure(AiChatException ex)
    {
        if (ex.Reason == AiChatException.ToolsUnsupported)
            return ("tools-unsupported", "The site's model does not support tools; choose another in the host's AI settings.");
        var lead = ex.Reason switch
        {
            AiChatException.ContextTooLong => "The conversation is too long for the site's model; start a new conversation",
            AiChatException.RateLimited => "The site's AI provider is limiting requests; try again in a moment",
            AiChatException.Auth => "The site's AI provider refused its key; check the provider in the host's AI settings",
            AiChatException.Unavailable => "The site's AI provider is unavailable; try again later",
            _ => "The site's AI provider failed",
        };
        var status = ex.StatusCode is { } s ? " (" + s.ToString(CultureInfo.InvariantCulture) + ")" : "";
        return ("provider", lead + status + ": " + ex.Message);
    }

    private async Task FailAsync(AssistConversationFile conversation, Func<JsonObject, Task> emit, string code, string message)
    {
        // Keep the conversation valid for the next message: every model turn sequence ends with the assistant.
        if (conversation.Turns.Count > 0 && conversation.Turns[^1].Role != AiMessage.AssistantRole)
            conversation.Turns.Add(new AssistTurn(AiMessage.AssistantRole, "(The answer failed: " + code + ".)"));
        conversation.Entries.Add(new JsonObject { ["type"] = "error", ["code"] = code, ["message"] = message });
        try
        {
            await emit(new JsonObject { ["type"] = "error", ["code"] = code, ["message"] = message }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The browser is gone; the error is in the stored conversation.
        }
    }

    private async Task<(string Content, bool IsError)> CallToolAsync(AssistConversationFile conversation, AssistToolCall call, Func<JsonObject, Task> emit, CancellationToken ct)
    {
        if (call.Arguments.ValueKind == JsonValueKind.String)
        {
            var raw = call.Arguments.GetString() ?? "";
            return ("Your input was not valid JSON: " + (raw.Length > 200 ? raw[..200] + "…" : raw) + ". Send the arguments as a JSON object.", true);
        }

        if (call.Name == AssistantProposals.ToolName)
        {
            var (draft, error) = await AssistantProposals.PrepareAsync(_store, _ids, call.Arguments, ct).ConfigureAwait(false);
            if (draft is null)
                return (error!, true);
            var id = _ids.NewId();
            var proposal = new JsonObject
            {
                ["id"] = id,
                ["toolCallId"] = call.Id,
                ["summary"] = draft.Summary,
                ["state"] = "pending",
                ["createdUtc"] = JsonValue.Create(_time.GetUtcNow()),
                ["operations"] = draft.Operations.DeepClone(),
                ["files"] = JsonSerializer.SerializeToNode(draft.Files, Api.JsonOptions),
            };
            conversation.Entries.Add(new JsonObject { ["type"] = "proposal", ["proposal"] = proposal });
            await emit(new JsonObject { ["type"] = "proposal", ["proposal"] = proposal.DeepClone() }).ConfigureAwait(false);
            return (AssistantProposals.Accepted(id, draft), false);
        }

        if (!AgentTools.Contains(call.Name))
            return ($"No tool is named '{call.Name}'. Use one of the tools you were given.", true);
        var result = await Catalog.CallAsync(call.Name, call.Arguments, ct).ConfigureAwait(false);
        return (result.Text, result.IsError);
    }

    private static AiMessage ToMessage(AssistTurn turn) => turn.Role switch
    {
        AiMessage.ToolRole => AiMessage.ToolResult(turn.ToolCallId!, turn.Content, turn.IsError),
        AiMessage.AssistantRole when turn.ToolCalls is { Count: > 0 } calls =>
            AiMessage.AssistantToolCalls(turn.Content, [.. calls.Select(c => new AiToolCall(c.Id, c.Name, c.Arguments))]),
        AiMessage.AssistantRole => AiMessage.Assistant(turn.Content),
        _ => AiMessage.User(turn.Content),
    };

    /// <summary>One line about a tool's result, for the panel's tool row.</summary>
    /// <param name="content">The result text.</param>
    /// <param name="isError">Whether it failed.</param>
    /// <returns>The summary.</returns>
    public static string Summarize(string content, bool isError)
    {
        ArgumentNullException.ThrowIfNull(content);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            node = null;
        }

        static string Plural(int count, string what) => count.ToString(CultureInfo.InvariantCulture) + " " + what + (count == 1 ? "" : "s");
        if (isError)
        {
            if (node is JsonObject problem && problem["title"]?.GetValueKind() == JsonValueKind.String)
                return Clip((string)problem["title"]! + (problem["detail"]?.GetValueKind() == JsonValueKind.String ? " " + (string)problem["detail"]! : ""));
            return Clip(content);
        }

        return node switch
        {
            JsonArray array => Plural(array.Count, "result"),
            JsonObject { } o when o["proposal"] is not null && o["files"] is JsonArray files => "Proposal stored: " + Plural(files.Count, "file"),
            JsonObject { } o when o["items"] is JsonArray items => Plural(items.Count, "item") + (o["next"] is not null ? ", more to read" : ""),
            JsonObject { } o when o["errors"] is not null && o["warnings"] is not null && o["diagnostics"] is JsonArray =>
                $"{Plural((int?)o["errors"] ?? 0, "error")}, {Plural((int?)o["warnings"] ?? 0, "warning")}",
            JsonObject { } o when o["element"] is JsonObject element && element["name"] is not null => $"{(string?)element["kind"]} {(string?)element["name"]}",
            JsonObject { } o when o["kinds"] is JsonArray && o["total"] is not null => Plural((int?)o["total"] ?? 0, "element"),
            JsonObject { } o when o["name"]?.GetValueKind() == JsonValueKind.String => Clip((string)o["name"]!),
            null => Plural(content.Split('\n').Length, "line"),
            _ => Plural(content.Length, "character"),
        };
    }

    private static string Clip(string text) => text.Length <= 160 ? text : text[..160] + "…";
}
