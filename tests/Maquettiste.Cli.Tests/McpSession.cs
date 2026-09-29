using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Maquettiste.Cli.Tests;

/// <summary>A tool call's outcome: whether it is a tool error, its text and, when the text is JSON, the parsed node.</summary>
public sealed record ToolReply(bool IsError, string Text)
{
    public JsonNode Json => JsonNode.Parse(Text) ?? throw new InvalidOperationException("null body");

    public string Code => (string)Json["code"]!;

    public override string ToString() => (IsError ? "error: " : "ok: ") + (Text.Length > 2000 ? Text[..2000] + "..." : Text);
}

/// <summary>
/// A <c>maquettiste mcp</c> process over a repo, driven through the SDK's client over stdio: the CLI is started as
/// <c>dotnet Maquettiste.Cli.dll --cache-dir &lt;temp&gt; mcp --repo &lt;repo&gt;</c>, exactly as a Claude Code <c>.mcp.json</c> would.
/// </summary>
public sealed class McpSession : IAsyncDisposable
{
    public const string BillingPackage = "01J92P0V01KDRN8GX5PGYCNKSX";
    public const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";
    public const string CustomerPath = ".maquettiste/model/entities/customer.json";

    private readonly bool _ownsRepo;

    private McpSession(CliRepo repo, McpClient client, bool ownsRepo)
    {
        Repo = repo;
        Client = client;
        _ownsRepo = ownsRepo;
    }

    public CliRepo Repo { get; }

    public McpClient Client { get; }

    /// <summary>Starts the server over a repo (the billing repo of <see cref="CliRepo.Billing"/> when none is given, owned by the session).</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="protocolVersion">The protocol revision to ask for; <see langword="null"/> lets the SDK pick its latest.</param>
    public static async Task<McpSession> StartAsync(CliRepo? repo = null, CancellationToken ct = default, string? protocolVersion = null)
    {
        var owns = repo is null;
        repo ??= CliRepo.Billing();
        var host = Environment.ProcessPath is { } p && Path.GetFileNameWithoutExtension(p) == "dotnet" ? p : "dotnet";
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "maquettiste",
            Command = host,
            Arguments = [typeof(Program).Assembly.Location, "--cache-dir", repo.CacheDirectory, "mcp", "--repo", repo.RepoRoot],
            WorkingDirectory = repo.RepoRoot,
        });
        var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: ct);
        return new McpSession(repo, client, owns);
    }

    /// <summary>
    /// Starts the server the way a client does from a registration that <c>init --mcp</c> writes: the registered arguments only (no
    /// <c>--repo</c>), in the repo folder, with the cache folder from <c>MAQUETTISTE_CACHE_DIR</c>. The session owns the repo.
    /// </summary>
    public static async Task<McpSession> StartRegisteredAsync(CliRepo repo, IReadOnlyList<string> registeredArgs, CancellationToken ct)
    {
        var host = Environment.ProcessPath is { } p && Path.GetFileNameWithoutExtension(p) == "dotnet" ? p : "dotnet";
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "maquettiste",
            Command = host,
            Arguments = [typeof(Program).Assembly.Location, .. registeredArgs],
            WorkingDirectory = repo.RepoRoot,
            EnvironmentVariables = new Dictionary<string, string?>(StringComparer.Ordinal) { ["MAQUETTISTE_CACHE_DIR"] = repo.CacheDirectory },
        });
        var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        return new McpSession(repo, client, true);
    }

    public Task<ToolReply> CallAsync(string tool, object? arguments = null) => CallCancellableAsync(tool, arguments, TestContext.Current.CancellationToken);

    public async Task<ToolReply> CallCancellableAsync(string tool, object? arguments, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(arguments).EnumerateObject())
                args[property.Name] = property.Value.Clone();
        }

        var result = await Client.CallToolAsync(tool, args, cancellationToken: ct);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return new ToolReply(result.IsError == true, text);
    }

    public async Task<JsonNode> OkAsync(string tool, object? arguments = null)
    {
        var reply = await CallAsync(tool, arguments);
        Assert.False(reply.IsError, reply.ToString());
        return reply.Json;
    }

    public async Task<ToolReply> ErrorAsync(string tool, object? arguments = null)
    {
        var reply = await CallAsync(tool, arguments);
        Assert.True(reply.IsError, reply.ToString());
        return reply;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        if (_ownsRepo)
            Repo.Dispose();
    }
}
