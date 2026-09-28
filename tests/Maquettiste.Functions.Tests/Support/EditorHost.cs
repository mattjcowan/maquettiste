using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;
using StaticSiteHost.Functions.Testing;

namespace Maquettiste.Functions.Tests.Support;

/// <summary>
/// The editor site as the host would run it, in memory: a temporary copy of the billing fixture (with the sql-ddl and csharp-dapper
/// packs under <c>templates/</c>) as the site's data folder, <see cref="FakeSite"/> with <see cref="FakeSiteVariables"/> and
/// <see cref="FakeRealtime"/>, the services <see cref="EditorSetup.Configure"/> registers over the ones the host provides, and
/// <see cref="SendAsync"/>, which runs a request through <see cref="SignInGate"/> and <see cref="TestRouter"/> like the host does.
/// Background services never start unless a test calls <see cref="StartBackground"/>.
/// </summary>
internal sealed class EditorHost : IAsyncDisposable
{
    public const string Domain = "maquettiste.localhost";
    public const string Origin = "http://maquettiste.localhost:8080";
    public const string Token = "test-editor-token-0123456789";
    public const string InvoiceId = "01J92P0V0FJ23CGSNKM7P1W5V7";
    public const string CustomerId = "01J92P0V0ETQKXXP951CMMNHH3";
    public const string PaymentId = "01J92P0V0HEGSC6MW92CST5KA6";
    public const string ProductId = "01J92P0V0JR8BE8253SKT29ZG7";
    public const string InvoiceNotesAttributeId = "01J92P0V0WKRGKH7YBKA2V30NC";
    public const string ContainsRelationId = "01J92P0V1BWHG0REWKSTR292RS";
    public const string MainDatabaseId = "01J92P0V1QRN2181XM2ZWE02W4";
    public const string OverviewDiagramId = "01J92P0V2164SDBW687ZV6E1MV";
    public const string BillingPackageId = "01J92P0V01KDRN8GX5PGYCNKSX";

    private readonly ServiceProvider _provider;
    private CancellationTokenSource? _background;
    private readonly List<Task> _running = [];

    private EditorHost(string root, Action<FakeSiteVariables>? variables, bool packs)
    {
        Root = root;
        RepoRoot = Path.Combine(root, "repo");
        ModelRoot = Path.Combine(RepoRoot, ".maquettiste");
        CacheRoot = Path.Combine(root, "cache");
        CopyTree(Fixtures.Path("models", "billing"), RepoRoot);
        if (packs)
        {
            foreach (var name in new[] { "sql-ddl", "csharp-dapper" })
                CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", name), Path.Combine(ModelRoot, "templates", name));
        }

        Variables = new FakeSiteVariables()
            .Set(EditorSettings.RepoRootVariable, RepoRoot)
            .Set(EditorSettings.CacheVariable, CacheRoot)
            .Set(EditorSettings.TokenVariable, Token, isPublic: false)
            .Set(EditorSettings.LocalUserVariable, "local");
        variables?.Invoke(Variables);
        Site = new FakeSite { Domain = Domain, Data = new DirectoryInfo(ModelRoot), Variables = Variables };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Logs));
        services.AddSingleton<ISite>(Site);
        services.AddSingleton<ISiteVariables>(Variables);
        services.AddSingleton<IRealtime>(Site.Realtime);
        services.AddSingleton<IAiChat>(Site.Ai);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<ILogger>(Logs.CreateLogger("functions:" + Domain));
        EditorSetup.Configure(services, Variables, Site.Data, Logs.CreateLogger("functions:" + Domain));
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Site.Services = _provider;
    }

    public string Root { get; }

    public string RepoRoot { get; }

    public string ModelRoot { get; }

    public string CacheRoot { get; }

    public FakeSite Site { get; }

    public FakeSiteVariables Variables { get; }

    public FakeRealtime Realtime => Site.Realtime;

    public TestClock Clock { get; } = new();

    public CapturingLoggers Logs { get; } = new();

    public IServiceProvider Services => _provider;

    public ModelStore Store => _provider.GetRequiredService<ModelStore>();

    public JobQueue Queue => _provider.GetRequiredService<JobQueue>();

    public EditorEvents Events => _provider.GetRequiredService<EditorEvents>();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A new site over a fresh copy of the billing fixture.</summary>
    public static EditorHost Create(Action<FakeSiteVariables>? variables = null, bool packs = true) =>
        new(Path.Combine(Path.GetTempPath(), "maquettiste-functions-tests", Guid.NewGuid().ToString("N")), variables, packs);

    /// <summary>The absolute path of a repo-relative path.</summary>
    public string PathOf(string repoPath) => Path.Combine(RepoRoot, repoPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Starts the two background services as the host would after a load.</summary>
    public void StartBackground()
    {
        _background ??= CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var token = _background.Token;
        var logger = Logs.CreateLogger("functions:" + Domain);
        _running.Add(Task.Run(() => ModelWatcher.Run(token, Store, Events, _provider.GetRequiredService<EditorSettings>(), logger), token));
        _running.Add(Task.Run(() => JobWorker.Run(token, Queue, Events), token));
    }

    /// <summary>Exceptions that escaped a handler and were answered by the host's own 500 (none should).</summary>
    public List<Exception> HostFailures { get; } = [];

    /// <summary>Runs a request through the sign-in gate and the router, as the host does, and captures the response.</summary>
    public async Task<TestResponse> SendAsync(TestRequest request)
    {
        var context = request.ToContext(Site, _provider);
        var body = new MemoryStream();
        context.Response.Body = body;
        var auth = _provider.GetRequiredService<EditorAuth>();
        var routed = false;
        await SignInGate.Run(context, async () =>
        {
            routed = true;
            try
            {
                if (!await TestRouter.TryDispatchAsync(context, Site))
                    context.Response.StatusCode = StatusCodes.Status404NotFound; // the host would serve the SPA's index.html
            }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                // What static-site-hosting's FunctionHost.DispatchAsync/FailAsync does: a handler's exception never reaches the
                // middleware; the host answers 500 text/plain itself.
                HostFailures.Add(ex);
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("The function failed. The details are in the server log.");
            }
        }, auth, _provider.GetRequiredService<ILogger>());
        return new TestResponse(request, context.Response.StatusCode, context.Response.Headers, body.ToArray(), routed);
    }

    public Task<TestResponse> GetAsync(string path, Action<TestRequest>? change = null) => SendAsync(TestRequest.Local("GET", path).With(change));

    public Task<TestResponse> SendJsonAsync(string method, string path, object? body, Action<TestRequest>? change = null) =>
        SendAsync(TestRequest.Local(method, path).WithJson(body).With(change));

    /// <summary>Waits until <paramref name="condition"/> holds (polling every 20 ms), failing after <paramref name="seconds"/>.</summary>
    public static async Task WaitForAsync(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for " + what + ".");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>The events published so far with a name, their payloads parsed.</summary>
    public IReadOnlyList<(string Target, JsonNode Payload)> Published(string eventName) =>
        [.. Realtime.Published.Where(e => e.EventName == eventName).Select(e => (e.Target, JsonNode.Parse(e.Payload!.Value.GetRawText())!))];

    public async ValueTask DisposeAsync()
    {
        if (_background is not null)
        {
            await _background.CancelAsync();
            foreach (var task in _running)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (OperationCanceledException)
                {
                }
            }

            _background.Dispose();
        }

        await _provider.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }
}

/// <summary>A request to send through <see cref="EditorHost.SendAsync"/>.</summary>
internal sealed class TestRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public string Host { get; set; } = "maquettiste.localhost:8080";

    public string Scheme { get; set; } = "http";

    public IPAddress? Remote { get; set; } = IPAddress.Loopback;

    public byte[]? Body { get; set; }

    public string? ContentType { get; set; }

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A request from the local developer: loopback, <c>maquettiste.localhost:8080</c>, no credential.</summary>
    public static TestRequest Local(string method, string path) => new() { Method = method, Path = path };

    /// <summary>A request from another machine, with no credential.</summary>
    public static TestRequest FromElsewhere(string method, string path) => new() { Method = method, Path = path, Remote = IPAddress.Parse("203.0.113.7"), Host = "editor.example.com" };

    public TestRequest With(Action<TestRequest>? change)
    {
        change?.Invoke(this);
        return this;
    }

    public TestRequest WithJson(object? body)
    {
        if (body is null)
            return this;
        Body = body switch
        {
            byte[] bytes => bytes,
            string text => Encoding.UTF8.GetBytes(text),
            JsonNode node => Encoding.UTF8.GetBytes(node.ToJsonString()),
            JsonElement element => Encoding.UTF8.GetBytes(element.GetRawText()),
            _ => JsonSerializer.SerializeToUtf8Bytes(body, Api.JsonOptions),
        };
        ContentType ??= "application/json";
        return this;
    }

    public TestRequest Header(string name, string value)
    {
        Headers[name] = value;
        return this;
    }

    public TestRequest Bearer(string token) => Header("Authorization", "Bearer " + token);

    public TestRequest IfMatch(string hash) => Header("If-Match", "\"" + hash + "\"");

    public DefaultHttpContext ToContext(ISite site, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.UseSite(site);
        var request = context.Request;
        request.Method = Method;
        request.Scheme = Scheme;
        request.Host = new HostString(Host);
        var query = Path.IndexOf('?', StringComparison.Ordinal);
        request.Path = query < 0 ? Path : Path[..query];
        request.QueryString = query < 0 ? QueryString.Empty : new QueryString(Path[query..]);
        foreach (var (name, value) in Headers)
            request.Headers[name] = value;
        if (ContentType is not null)
            request.ContentType = ContentType;
        if (Body is not null)
        {
            request.Body = new MemoryStream(Body);
            request.ContentLength = Body.Length;
        }

        context.Connection.RemoteIpAddress = Remote;
        context.TraceIdentifier = "test-trace";
        return context;
    }
}

/// <summary>A captured response.</summary>
internal sealed record TestResponse(TestRequest Request, int Status, IHeaderDictionary Headers, byte[] Body, bool Routed)
{
    public string Text => Encoding.UTF8.GetString(Body);

    public string? ContentType => Headers.ContentType.ToString() is { Length: > 0 } type ? type : null;

    public JsonNode Json => JsonNode.Parse(Body) ?? throw new InvalidOperationException("The body is JSON null.");

    public string? ETag => Headers.ETag.ToString() is { Length: > 0 } tag ? tag : null;

    /// <summary>The problem's <c>code</c>, after checking the body is a problem document.</summary>
    public string ProblemCode
    {
        get
        {
            Assert.StartsWith("application/problem+json", ContentType ?? "", StringComparison.Ordinal);
            return Json["code"]!.GetValue<string>();
        }
    }

    public override string ToString() => $"{Request.Method} {Request.Path} -> {Status} {ContentType}\n{Text}";
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.UtcTicks;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Loggers that keep what they were told, so a test can check a warning was logged (once).</summary>
internal sealed class CapturingLoggers : ILoggerProvider
{
    private readonly List<(string Category, LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
                return [.. _entries];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggers owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._entries)
                owner._entries.Add((category, logLevel, formatter(state, exception)));
        }
    }
}
