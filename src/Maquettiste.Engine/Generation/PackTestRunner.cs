using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Generation;

/// <summary>The optional settings of a pack test case: <c>templates/&lt;pack&gt;/tests/&lt;case&gt;/case.json</c> (<c>pack-case.json</c>).</summary>
internal sealed record PackCaseSettings
{
    /// <summary>The <c>$schema</c> value.</summary>
    [JsonPropertyName("$schema")]
    public string? SchemaPath { get; init; }

    /// <summary>What the case shows; the report prints it.</summary>
    public string? Description { get; init; }

    /// <summary>Another case of the same pack whose project files serve as this case's project.</summary>
    public string? Model { get; init; }

    /// <summary>Parameters merged over <c>packs.&lt;pack&gt;.parameters</c> of the case's <c>maquettiste.json</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>
/// Runs one pack's tests against the project's templates folder (next-phase-2-design.md section 5.4). Each case under
/// <c>tests/&lt;case&gt;/</c> is a small project: it runs as a fresh project in a temporary folder outside the project (the case files,
/// the pack and its uses closure without their <c>tests/</c> folders), one apply of the pack under test, and its output folder is
/// compared byte for byte with <c>expected/</c>. Then each <c>tests/*.test.js</c> file's helper tests run in a test pool. Cases run one
/// after another in ordinal order; nothing in the project changes but <c>expected/</c> under update. A link anywhere under
/// <c>tests/</c> stops the pack's tests before anything there is read or changed.
/// </summary>
/// <param name="services">The project's engine services (schemas, canonical JSON, diffs, the write guard).</param>
/// <param name="options">The project's engine options.</param>
internal sealed partial class PackTestRunner(EngineServices services, EngineOptions options)
{
    /// <summary>The most lines of a file's diff the report carries.</summary>
    public const int MaxDiffLines = 200;

    private const string Differs = "MQ6051";
    private const string HelperFails = "MQ6052";
    private const string Malformed = "MQ6053";
    private const string ExpectedFolder = "expected";
    private const string CaseFile = "case.json";

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>A case name: its folder's name, as <c>model</c> of <c>pack-case.json</c> names one.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    public static partial Regex CaseNamePattern();

    /// <summary>
    /// The names of a pack's cases: the folders directly under its <c>tests/</c> folder, hidden ones left out, ordinal. A name
    /// <see cref="CaseNamePattern"/> refuses is listed too; its case does not run and reports MQ6053.
    /// </summary>
    /// <param name="packRoot">The pack folder.</param>
    /// <returns>The case names.</returns>
    public static IReadOnlyList<string> CaseNames(string packRoot)
    {
        var tests = Path.Combine(packRoot, PackFileRoles.TestsFolder);
        return Directory.Exists(tests)
            ? [.. Directory.EnumerateDirectories(tests).Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.')).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The helper test files of a pack: <c>tests/*.test.js</c>, directly in its tests folder, pack-relative, ordinal.</summary>
    /// <param name="packRoot">The pack folder.</param>
    /// <returns>The pack-relative paths.</returns>
    public static IReadOnlyList<string> TestFiles(string packRoot)
    {
        var tests = Path.Combine(packRoot, PackFileRoles.TestsFolder);
        return Directory.Exists(tests)
            ? [.. Directory.EnumerateFiles(tests).Select(Path.GetFileName).OfType<string>().Select(n => PackFileRoles.TestsFolder + "/" + n)
                .Where(ScriptTests.IsTestFile).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>A diff cut to at most <paramref name="maxLines"/> lines, with a last line that counts the lines left out.</summary>
    /// <param name="diff">The unified diff.</param>
    /// <param name="maxLines">The most lines kept.</param>
    /// <returns>The diff.</returns>
    public static string Truncate(string diff, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var lines = diff.Split('\n');
        var count = diff.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (count <= maxLines)
            return diff;
        return string.Join('\n', lines.Take(maxLines)) + "\n... " + (count - maxLines).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more lines\n";
    }

    /// <summary>Runs the tests of one pack.</summary>
    /// <param name="snapshot">The project's snapshot (pack settings and sandbox limits).</param>
    /// <param name="pack">The pack name.</param>
    /// <param name="test">The options.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The pack's result.</returns>
    public async Task<PackTestResult> RunAsync(ModelSnapshot snapshot, string pack, PackTestOptions test, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(test);
        var set = await new PackLoader(options, services.Schemas).LoadClosureAsync(snapshot, pack, ct).ConfigureAwait(false);
        if (set.Packs.FirstOrDefault() is not { } loaded)
        {
            IReadOnlyList<Diagnostic> errors = [.. set.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
            return new PackTestResult(pack, [], [], errors.Count > 0 ? errors
                : [RuleCatalog.Create("MQ6001", $"Pack '{pack}' did not load, so its tests cannot run.", filePath: Repo("templates/" + pack + "/pack.json"))]);
        }

        // A link under tests/ could show a file outside the pack in a diff, or let an update write or delete through it.
        if (PackAuthoring.Links(loaded.RootPath, PackFileRoles.TestsFolder) is { Count: > 0 } links)
        {
            return new PackTestResult(pack, [], [], [.. links.Select(link => RuleCatalog.Create(Malformed,
                $"{link} is a link, which pack tests refuse, so no test of pack '{pack}' ran; replace it with the file or folder it points at.",
                filePath: Repo("templates/" + pack + "/" + link)))]);
        }

        var cases = new List<PackCaseResult>();
        var names = CaseNames(loaded.RootPath);
        foreach (var name in names.Where(n => test.Cases.Count == 0 || test.Cases.Contains(n, StringComparer.Ordinal)))
            cases.Add(await RunCaseAsync(loaded, set, names, name, test, ct).ConfigureAwait(false));

        var diagnostics = new List<Diagnostic>();
        IReadOnlyList<PackHelperTestResult> helperTests = test.Cases.Count == 0 ? RunHelperTests(snapshot, loaded, set, diagnostics, ct) : [];
        return new PackTestResult(pack, cases, helperTests, PackLoader.Sort(diagnostics));
    }

    private string Repo(string modelPath) => new ModelPaths(options).ToRepoPath(modelPath);

    private async Task<PackCaseResult> RunCaseAsync(LoadedPack pack, PackSet set, IReadOnlyList<string> names, string name, PackTestOptions test,
        CancellationToken ct)
    {
        var tests = Path.Combine(pack.RootPath, PackFileRoles.TestsFolder);
        var folder = Path.Combine(tests, name);
        var caseRepo = Repo("templates/" + pack.Name + "/" + PackFileRoles.TestsFolder + "/" + name);
        if (!CaseNamePattern().IsMatch(name))
        {
            return new PackCaseResult(name, null, PackTestOutcome.Error, [], [RuleCatalog.Create(Malformed,
                $"Case folder '{name}' is not a case name, so it did not run: rename it with lowercase letters, digits and hyphens, starting with a letter or digit.",
                filePath: caseRepo)]);
        }

        var problems = new List<Diagnostic>();
        var settings = ReadCase(folder, caseRepo, problems);
        if (pack.Manifest.Units.Count == 0)
            problems.Add(RuleCatalog.Create(Malformed, $"Pack '{pack.Name}' has no units, so its case '{name}' produces nothing; test a library's helpers with tests/*.test.js and its partials through a pack that uses it.", filePath: caseRepo));
        if (Directory.Exists(Path.Combine(folder, "templates")))
            problems.Add(RuleCatalog.Create(Malformed, $"Case '{name}' holds a templates/ folder; a case runs with the pack under test and its uses, so remove templates/.", filePath: caseRepo + "/templates"));
        string? modelFolder = null;
        if (settings?.Model is { } model)
        {
            var other = Path.Combine(tests, model);
            if (!names.Contains(model, StringComparer.Ordinal) || string.Equals(model, name, StringComparison.Ordinal))
                problems.Add(RuleCatalog.Create(Malformed, $"Case '{name}' takes the model of case '{model}', which pack '{pack.Name}' does not have.", filePath: caseRepo + "/" + CaseFile, jsonPointer: "/model"));
            else if (ReadCase(other, caseRepo, []) is { Model: not null })
                problems.Add(RuleCatalog.Create(Malformed, $"Case '{name}' takes the model of case '{model}', which itself takes the model of another case; name a case that holds its own project.", filePath: caseRepo + "/" + CaseFile, jsonPointer: "/model"));
            else if (!File.Exists(Path.Combine(other, ModelPaths.SettingsFile)))
                problems.Add(RuleCatalog.Create(Malformed, $"Case '{name}' takes the model of case '{model}', which holds no maquettiste.json.", filePath: caseRepo + "/" + CaseFile, jsonPointer: "/model"));
            else
                modelFolder = other;
        }
        else if (settings is not null && !File.Exists(Path.Combine(folder, ModelPaths.SettingsFile)))
        {
            problems.Add(RuleCatalog.Create(Malformed, $"Case '{name}' has no project: add a maquettiste.json and model/, or a case.json whose model names another case.", filePath: caseRepo));
        }

        var expected = Path.Combine(folder, ExpectedFolder);
        if (!test.Update && !Directory.Exists(expected))
            problems.Add(RuleCatalog.Create(Malformed, $"Case '{name}' has no expected/ folder; run the tests with update to write it from the output.", filePath: caseRepo));
        if (problems.Count > 0)
            return new PackCaseResult(name, settings?.Description, PackTestOutcome.Error, [], PackLoader.Sort(problems));

        var temp = TempFolder();
        try
        {
            var project = Path.Combine(temp, "project");
            var modelRoot = Path.Combine(project, ".maquettiste");
            if (modelFolder is not null)
                CopyCase(modelFolder, modelRoot);
            CopyCase(folder, modelRoot);
            foreach (var used in new[] { pack }.Concat(set.Used))
                CopyPack(used.RootPath, Path.Combine(modelRoot, "templates", used.Name));
            var output = await WriteSettingsAsync(Path.Combine(modelRoot, ModelPaths.SettingsFile), pack.Name, settings!, ct).ConfigureAwait(false);

            var caseOptions = new EngineOptions
            {
                RepoRoot = project,
                CacheDirectory = Path.Combine(temp, "cache"),
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                IdGenerator = options.IdGenerator,
                TimeProvider = options.TimeProvider,
            };
            var caseServices = EngineServices.Create(caseOptions);
            GenerationResult result;
            await using (var store = new ModelStore(caseOptions, caseServices))
            {
                var service = new GenerationService(store, caseOptions, caseServices);
                result = await service.RunAsync(new GenerationRequest { Packs = [pack.Name], Jobs = test.Jobs }, null, ct).ConfigureAwait(false);
            }

            if (result.Outcome != RunOutcome.Succeeded)
            {
                IReadOnlyList<Diagnostic> run = [.. result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d with { FilePath = MapPath(d.FilePath, folder, caseRepo, modelFolder, settings!.Model) })];
                return new PackCaseResult(name, settings!.Description, PackTestOutcome.Error, [], run.Count > 0 ? PackLoader.Sort(run)
                    : [RuleCatalog.Create(Malformed, $"Case '{name}' did not generate: the run ended '{JsonNamingPolicy.CamelCase.ConvertName(result.Outcome.ToString())}'.", filePath: caseRepo)]);
            }

            var produced = ReadTree(Path.Combine(project, output.Replace('/', Path.DirectorySeparatorChar)));
            var files = Compare(produced, ReadTree(expected));
            if (test.Update)
            {
                IReadOnlyList<string> changed;
                try
                {
                    changed = await PackAuthoring.ReplaceTreeAsync(services, pack.Name, PackFileRoles.TestsFolder + "/" + name + "/" + ExpectedFolder, produced, ct)
                        .ConfigureAwait(false);
                }
                catch (PackPathException e)
                {
                    return new PackCaseResult(name, settings!.Description, PackTestOutcome.Error, files,
                        [RuleCatalog.Create(Malformed, $"Case '{name}' was not updated, and its expected/ tree is as it was: {e.Message}", filePath: caseRepo + "/" + ExpectedFolder)]);
                }

                return new PackCaseResult(name, settings!.Description, changed.Count > 0 ? PackTestOutcome.Updated : PackTestOutcome.Passed, files, []);
            }

            if (files.Count == 0)
                return new PackCaseResult(name, settings!.Description, PackTestOutcome.Passed, [], []);
            var list = string.Join(", ", files.Select(f => f.Path + " (" + Change(f.Change) + ")"));
            return new PackCaseResult(name, settings!.Description, PackTestOutcome.Failed, files,
                [RuleCatalog.Create(Differs, $"Case '{name}' of pack '{pack.Name}' differs from its expected tree: {list}. Fix the pack, or run with update if the change is intended.", filePath: caseRepo)]);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string Change(PackCaseFileChange change) => change switch
    {
        PackCaseFileChange.Added => "added",
        PackCaseFileChange.Missing => "missing",
        _ => "differs",
    };

    /// <summary>
    /// Reads <c>case.json</c>: absent is the empty settings; invalid JSON or a document the schema refuses is MQ6053 and
    /// <see langword="null"/>.
    /// </summary>
    private PackCaseSettings? ReadCase(string folder, string caseRepo, List<Diagnostic> problems)
    {
        var path = Path.Combine(folder, CaseFile);
        if (!File.Exists(path))
            return new PackCaseSettings();
        var repoPath = caseRepo + "/" + CaseFile;
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var failures = services.Schemas.Evaluate("pack-case.json", document.RootElement, repoPath);
            if (failures.Count > 0)
            {
                problems.AddRange(failures.Select(f => RuleCatalog.Create(Malformed, "case.json is invalid: " + f.Message, filePath: repoPath, jsonPointer: f.JsonPointer)
                    with { Line = f.Line, Column = f.Column }));
                return null;
            }

            return document.RootElement.Deserialize<PackCaseSettings>(EngineJson.Options) ?? new PackCaseSettings();
        }
        catch (JsonException ex)
        {
            problems.Add(RuleCatalog.Create(Malformed, "case.json is not valid JSON: " + ex.Message, filePath: repoPath));
            return null;
        }
    }

    /// <summary>
    /// Writes the case's <c>maquettiste.json</c> canonically with the pack under test enabled, its output (<c>out</c> when empty) as the
    /// only allowed output and the case's parameters merged over the pack's.
    /// </summary>
    /// <returns>The output folder, repo-relative.</returns>
    private async Task<string> WriteSettingsAsync(string path, string pack, PackCaseSettings settings, CancellationToken ct)
    {
        var root = JsonNode.Parse(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)) as JsonObject ?? [];
        var packs = root["packs"] as JsonObject ?? [];
        root["packs"] = packs;
        var entry = packs[pack] as JsonObject ?? [];
        packs[pack] = entry;
        entry["enabled"] = true;
        var output = entry["output"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim('/').Length > 0 ? text.Trim('/') : "out";
        entry["output"] = output;
        if (settings.Parameters.Count > 0)
        {
            var parameters = entry["parameters"] as JsonObject ?? [];
            foreach (var (key, parameter) in settings.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                parameters[key] = JsonNode.Parse(parameter.GetRawText());
            entry["parameters"] = parameters;
        }

        var outputs = root["outputs"] as JsonObject ?? [];
        root["outputs"] = outputs;
        outputs["allow"] = new JsonArray(new JsonObject { ["path"] = output });
        await File.WriteAllBytesAsync(path, services.Json.Write(root, "maquettiste.json", ModelPaths.SettingsFile), ct).ConfigureAwait(false);
        return output;
    }

    /// <summary>Compares the output with the expected tree, path by path in ordinal order.</summary>
    private List<PackCaseFile> Compare(IReadOnlyDictionary<string, byte[]> produced, IReadOnlyDictionary<string, byte[]> expected)
    {
        var files = new List<PackCaseFile>();
        foreach (var path in produced.Keys.Union(expected.Keys).Order(StringComparer.Ordinal))
        {
            if (!expected.TryGetValue(path, out var want))
            {
                files.Add(new PackCaseFile(path, PackCaseFileChange.Added, null));
            }
            else if (!produced.TryGetValue(path, out var got))
            {
                files.Add(new PackCaseFile(path, PackCaseFileChange.Missing, null));
            }
            else if (!want.AsSpan().SequenceEqual(got))
            {
                var diff = DiffGenerator.IsText(want) && DiffGenerator.IsText(got) ? Truncate(services.Diffs.Unified(path, want, got), MaxDiffLines) : null;
                files.Add(new PackCaseFile(path, PackCaseFileChange.Differs, diff));
            }
        }

        return files;
    }

    /// <summary>Every file under a folder (dot files included), folder-relative with <c>/</c>, to its bytes; empty when it does not exist.</summary>
    private static SortedDictionary<string, byte[]> ReadTree(string root)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
            return files;
        foreach (var full in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            files[Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(full);
        return files;
    }

    /// <summary>Copies a case's project files (all but <c>expected/</c>, <c>case.json</c> and hidden files) into the model root.</summary>
    private static void CopyCase(string folder, string modelRoot)
    {
        foreach (var path in PackFileRoles.EnumerateFiles(folder))
        {
            if (path == CaseFile || path.StartsWith(ExpectedFolder + "/", StringComparison.Ordinal))
                continue;
            Copy(folder, modelRoot, path);
        }
    }

    /// <summary>Copies a pack's files without its <c>tests/</c> folder.</summary>
    private static void CopyPack(string root, string target)
    {
        foreach (var path in PackFileRoles.EnumerateFiles(root).Where(p => !PackFileRoles.IsTest(p)))
            Copy(root, target, path);
    }

    private static void Copy(string from, string to, string path)
    {
        var target = Path.Combine(to, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(from, path.Replace('/', Path.DirectorySeparatorChar)), target, overwrite: true);
    }

    /// <summary>
    /// A diagnostic's path in the case's run mapped back into the project: a pack file to the project's pack, a model file to the case
    /// that holds it (the case's own folder first, then the case whose model it takes).
    /// </summary>
    private string? MapPath(string? path, string folder, string caseRepo, string? modelFolder, string? model)
    {
        const string Prefix = ".maquettiste/";
        if (path is null || !path.StartsWith(Prefix, StringComparison.Ordinal))
            return path;
        var rest = path[Prefix.Length..];
        if (rest.StartsWith("templates/", StringComparison.Ordinal))
            return Repo(rest);
        if (modelFolder is not null && !File.Exists(Path.Combine(folder, rest.Replace('/', Path.DirectorySeparatorChar)))
            && File.Exists(Path.Combine(modelFolder, rest.Replace('/', Path.DirectorySeparatorChar))))
            return caseRepo[..(caseRepo.LastIndexOf('/') + 1)] + model + "/" + rest;
        return caseRepo + "/" + rest;
    }

    /// <summary>
    /// Runs the pack's helper tests: a test pool with the pack's scripts, its test files and its uses closure as libraries (each library
    /// in its own pool with its <c>pack.json</c> defaults), one sandbox call per test under the project's limits, with the pack's own
    /// defaults as <c>maquettiste.params</c>.
    /// </summary>
    private List<PackHelperTestResult> RunHelperTests(ModelSnapshot snapshot, LoadedPack pack, PackSet set, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var results = new List<PackHelperTestResult>();
        var files = TestFiles(pack.RootPath);
        if (files.Count == 0)
            return results;
        var limits = snapshot.Settings.Limits;
        var sources = new List<ScriptSource>(files.Count);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(pack.RootPath, file.Replace('/', Path.DirectorySeparatorChar)));
            try
            {
                sources.Add(new ScriptSource(pack.RelativePath + "/" + file, StrictUtf8.GetString(bytes), Hashing.ContentHash.Of(bytes)));
            }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(RuleCatalog.Create(Malformed, $"Test file {file} is not valid UTF-8.", filePath: pack.RelativePath + "/" + file));
            }
        }

        var pools = new Dictionary<string, TestLibrary>(StringComparer.Ordinal);
        try
        {
            if (!LibraryPools(pack, set, limits, pools, diagnostics, ct))
                return results;
            IReadOnlyList<ScriptLibrary> libraries = [.. pack.Sees.Where(pools.ContainsKey).Select(n => new ScriptLibrary(n, pools[n].Helpers))];
            ScriptSandboxPool pool;
            try
            {
                pool = ScriptSandboxFactory.CreateTestPool(pack.Scripts, sources, libraries, limits, ct);
            }
            catch (Exception e) when (e is ScriptErrorException or ScriptLimitException)
            {
                diagnostics.Add(e is ScriptLimitException limit ? limit.Diagnostic : ((ScriptErrorException)e).Diagnostic);
                return results;
            }

            using (pool)
            {
                foreach (var failure in pool.TestFailures)
                {
                    diagnostics.Add(failure.Diagnostic with
                    {
                        Rule = Malformed,
                        Severity = DiagnosticSeverity.Error,
                        Message = $"Test file {PackRelative(pack, failure.File)} did not load, so none of its tests ran: {failure.Diagnostic.Message}",
                        FilePath = failure.Diagnostic.FilePath ?? failure.File,
                    });
                }

                var calls = new TestLibraryCalls(pools);
                var parameters = Plain(pack.Manifest.Parameters);
                for (var i = 0; i < pool.Tests.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var test = pool.Tests[i];
                    var file = PackRelative(pack, test.File);
                    var ctx = new ScriptCallContext(null, test.File + "#" + test.Name, parameters, ct) { Libraries = calls };
                    var result = pool.RunTest(i, ctx);
                    if (result.Malformed)
                    {
                        diagnostics.Add(RuleCatalog.Create(Malformed, $"Test '{test.Name}' of {file} did not run: {result.Message}", filePath: test.File));
                        continue;
                    }

                    if (result.Passed)
                    {
                        results.Add(new PackHelperTestResult(file, test.Name, PackTestOutcome.Passed, null, null, null));
                        continue;
                    }

                    var inFile = string.Equals(result.File, test.File, StringComparison.Ordinal);
                    results.Add(new PackHelperTestResult(file, test.Name, PackTestOutcome.Failed, result.Message, inFile ? result.Line : null, inFile ? result.Column : null));
                    diagnostics.Add(new Diagnostic(HelperFails, DiagnosticSeverity.Error, $"Test '{test.Name}' of {file} fails: {result.Message}", null,
                        result.File ?? test.File, null, result.File is null ? null : result.Line, result.File is null ? null : result.Column));
                }
            }
        }
        finally
        {
            foreach (var library in pools.Values)
                library.Pool?.Dispose();
        }

        return results;
    }

    private static string PackRelative(LoadedPack pack, string scriptPath) =>
        scriptPath.StartsWith(pack.RelativePath + "/", StringComparison.Ordinal) ? scriptPath[(pack.RelativePath.Length + 1)..] : scriptPath;

    private static SortedDictionary<string, object?> Plain(IReadOnlyDictionary<string, JsonElement> values)
    {
        var plain = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            plain[key] = TemplateValues.Plain(value);
        return plain;
    }

    /// <summary>
    /// One pool per pack of the closure with scripts, in dependency order, each reaching its own closure and seeing its own
    /// <c>pack.json</c> defaults. A library whose scripts fail to load fails the helper tests with its own diagnostic.
    /// </summary>
    /// <returns>Whether every library loaded.</returns>
    private static bool LibraryPools(LoadedPack pack, PackSet set, SandboxLimits limits, Dictionary<string, TestLibrary> pools, List<Diagnostic> diagnostics,
        CancellationToken ct)
    {
        foreach (var name in pack.Sees)
        {
            if (set.Find(name) is not { } used)
                continue;
            if (used.Scripts.Count == 0)
            {
                pools[name] = new TestLibrary(null, [], Plain(used.Manifest.Parameters));
                continue;
            }

            IReadOnlyList<ScriptLibrary> reached = [.. used.Sees.Where(pools.ContainsKey).Select(n => new ScriptLibrary(n, pools[n].Helpers))];
            try
            {
                var pool = new ScriptSandboxFactory().CreatePool(used.Scripts, reached, limits, 1, ct);
                pools[name] = new TestLibrary(pool, [.. pool.Registrations.Where(r => r.Kind == ScriptRegistrationKind.Helper).Select(r => r.Name)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], Plain(used.Manifest.Parameters));
            }
            catch (Exception e) when (e is ScriptErrorException or ScriptLimitException)
            {
                var located = e is ScriptLimitException limit ? limit.Diagnostic : ((ScriptErrorException)e).Diagnostic;
                diagnostics.Add(located with { Message = $"The used pack '{name}' failed to load, so the helper tests cannot run: {located.Message}" });
                return false;
            }
        }

        return true;
    }

    private static string TempFolder()
    {
        var root = Path.Combine(RealPath(Path.GetTempPath()), "maquettiste-pack-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>A folder path with every link resolved (macOS's temp folder is reached through <c>/var</c>, a link).</summary>
    private static string RealPath(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var info = new DirectoryInfo(path);
        if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            return RealPath(target.FullName);
        var parent = Path.GetDirectoryName(path);
        return parent is null || string.Equals(parent, path, StringComparison.Ordinal) ? path : Path.Combine(RealPath(parent), Path.GetFileName(path));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary folder changes no result.
        }
    }

    /// <summary>A pack of the closure of one helper test run: its pool (none without scripts), helper names and default parameters.</summary>
    private sealed record TestLibrary(IScriptSandboxPool? Pool, IReadOnlyList<string> Helpers, IReadOnlyDictionary<string, object?> Parameters);

    /// <summary>The <c>maquettiste.use</c> calls of helper tests: each runs in the used pack's own pool with its own defaults.</summary>
    private sealed class TestLibraryCalls(IReadOnlyDictionary<string, TestLibrary> libraries) : IScriptLibraryCalls
    {
        public object? Call(string pack, string helper, IReadOnlyList<object?> args, ScriptCallContext caller)
        {
            ArgumentNullException.ThrowIfNull(caller);
            if (!libraries.TryGetValue(pack, out var library) || library.Pool is null)
            {
                throw new ScriptErrorException(new Diagnostic("MQ6016", DiagnosticSeverity.Error,
                    $"maquettiste.use('{pack}').{helper}: the used pack '{pack}' has no helper '{helper}'.", null, null, null, null, null));
            }

            using var lease = library.Pool.Rent();
            return lease.Sandbox.CallHelper(helper, args, caller with { Parameters = library.Parameters });
        }
    }
}
