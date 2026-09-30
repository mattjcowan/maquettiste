using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Loads a validation fixture repo (<c>tests/fixtures/validation/&lt;family&gt;/.maquettiste</c>) into a snapshot without the loader:
/// every file is schema-checked (fixtures must be schema-valid, so only semantic rules fire), deserialized to its kind's record
/// and indexed with its real bytes and hash.
/// </summary>
internal static class ValidationFixture
{
    public static string RepoRoot(string family) => Fixtures.Path("validation", family);

    public static ModelSnapshot Load(string family) => LoadRepo(RepoRoot(family));

    /// <summary>Loads any fixture repo folder (the one holding <c>.maquettiste</c>) the same way.</summary>
    /// <param name="repo">The folder.</param>
    /// <param name="change">Changes an element record after it is read (tags, hints), or <see langword="null"/>.</param>
    public static ModelSnapshot LoadRepo(string repo, Func<Element, Element>? change = null)
    {
        var modelRoot = Path.Combine(repo, ".maquettiste");
        var settingsBytes = File.ReadAllBytes(Path.Combine(modelRoot, "maquettiste.json"));
        using (var settingsJson = JsonDocument.Parse(settingsBytes))
            AssertSchemaValid("maquettiste.json", settingsJson.RootElement, ".maquettiste/maquettiste.json");
        var settings = JsonSerializer.Deserialize<ProjectSettings>(settingsBytes, EngineJson.Options)!;

        var documents = new List<ElementDocument>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(modelRoot, "model"), "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var path = RepoPath(repo, file);
            var bytes = File.ReadAllBytes(file);
            using var json = JsonDocument.Parse(bytes);
            var kind = json.RootElement.GetProperty("kind").GetString()!;
            Assert.True(KindInfo.TryGet(kind, out var info), $"{path}: unknown kind {kind}");
            AssertSchemaValid(info.SchemaFile, json.RootElement, path);
            var element = (Element)JsonSerializer.Deserialize(bytes, info.ClrType, EngineJson.Options)!;
            if (change is not null)
                element = change(element);
            var hash = ContentHash.Of(bytes);
            documents.Add(new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null));
        }

        var extensions = new List<ExtensionDocument>();
        var extensionFolder = Path.Combine(modelRoot, "extensions");
        var scripts = new List<ScriptSource>();
        if (Directory.Exists(extensionFolder))
        {
            foreach (var file in Directory.EnumerateFiles(extensionFolder, "*.json").Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(file);
                using var json = JsonDocument.Parse(bytes);
                AssertSchemaValid("extension.json", json.RootElement, RepoPath(repo, file));
                extensions.Add(new ExtensionDocument(JsonSerializer.Deserialize<ExtensionSchema>(bytes, EngineJson.Options)!, RepoPath(repo, file), ContentHash.Of(bytes)));
            }

            var rules = Path.Combine(extensionFolder, "rules");
            if (Directory.Exists(rules))
            {
                foreach (var file in Directory.EnumerateFiles(rules, "*.js").Order(StringComparer.Ordinal))
                {
                    var code = File.ReadAllText(file);
                    scripts.Add(new ScriptSource(RepoPath(repo, file), code, ContentHash.Of(code)));
                }
            }
        }

        return ModelSnapshot.Create(documents, settings, ContentHash.Of(settingsBytes), extensions, scripts, 1);
    }

    private static string RepoPath(string repo, string file) => Path.GetRelativePath(repo, file).Replace(Path.DirectorySeparatorChar, '/');

    private static void AssertSchemaValid(string schemaFile, JsonElement json, string path)
    {
        var diagnostics = TestServices.Schemas.Evaluate(schemaFile, json, path);
        Assert.True(diagnostics.Count == 0, $"Fixture {path} is not schema-valid:\n" + string.Join('\n', diagnostics.Select(d => d.Message)));
    }

    /// <summary>Serializes diagnostics as the golden JSON: web defaults, indented, LF, trailing newline.</summary>
    public static string ToGolden(IReadOnlyList<Diagnostic> diagnostics)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = "\n",
        };
        return JsonSerializer.Serialize(diagnostics, options) + "\n";
    }

    /// <summary>Compares diagnostics with <c>expected.json</c>, or rewrites it when <c>MAQUETTISTE_UPDATE_GOLDEN=1</c>.</summary>
    public static void AssertGolden(string family, IReadOnlyList<Diagnostic> diagnostics)
    {
        var path = Path.Combine(RepoRoot(family), "expected.json");
        var actual = ToGolden(diagnostics);
        if (Environment.GetEnvironmentVariable(Golden.UpdateVariable) == "1")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing (set {Golden.UpdateVariable}=1 to create it).");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    public static ModelValidator Validator(string? repoRoot = null, IScriptSandboxFactory? scripts = null, int parallelism = 4) =>
        new(new EngineOptions { RepoRoot = repoRoot ?? Path.GetTempPath(), CacheDirectory = Path.GetTempPath(), MaxDegreeOfParallelism = parallelism },
            TestServices.Schemas, scripts ?? new FakeSandboxFactory());
}

/// <summary>
/// A fake sandbox: rules are C# delegates keyed by rule name. The real Jint sandbox (W4) implements the same contract; these tests
/// pin what the validator does with registrations, results and failures.
/// </summary>
internal sealed class FakeSandboxFactory(params (string Name, Func<ElementDocument, IReadOnlyList<Diagnostic>> Check)[] rules) : IScriptSandboxFactory
{
    public Exception? CreateFailure { get; init; }

    public int PoolsCreated { get; private set; }

    public int PoolsDisposed { get; private set; }

    public IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, SandboxLimits limits, int size, CancellationToken ct)
    {
        if (CreateFailure is not null)
            throw CreateFailure;
        PoolsCreated++;
        return new Pool(this, rules);
    }

    private sealed class Pool(FakeSandboxFactory owner, (string Name, Func<ElementDocument, IReadOnlyList<Diagnostic>> Check)[] rules) : IScriptSandboxPool
    {
        private readonly Lease _lease = new(rules);

        public IReadOnlyList<ScriptRegistration> Registrations { get; } =
            [.. rules.Select(r => new ScriptRegistration(ScriptRegistrationKind.Rule, r.Name, "rules.js")),
             new ScriptRegistration(ScriptRegistrationKind.Helper, "not-a-rule", "rules.js")];

        public IScriptSandboxLease Rent() => _lease;

        public void Dispose() => owner.PoolsDisposed++;
    }

    private sealed class Lease((string Name, Func<ElementDocument, IReadOnlyList<Diagnostic>> Check)[] rules) : IScriptSandboxLease, IScriptSandbox
    {
        public IScriptSandbox Sandbox => this;

        public void Dispose()
        {
        }

        public IReadOnlyList<Diagnostic> RunRule(string ruleId, ElementDocument element, ModelSnapshot model, ScriptCallContext ctx)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            return rules.Single(r => r.Name == ruleId).Check(element);
        }

        public object? CallHelper(string name, IReadOnlyList<object?> args, ScriptCallContext ctx) => throw new NotSupportedException();

        public IReadOnlyList<string> Select(string name, ResolvedModel model, ScriptCallContext ctx) => throw new NotSupportedException();

        public bool Filter(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx) => throw new NotSupportedException();

        public IReadOnlyDictionary<string, object?> Transform(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx) =>
            throw new NotSupportedException();
    }
}
