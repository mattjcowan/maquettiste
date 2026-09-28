using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.PostProcessing;

/// <summary>
/// A path policy for W8 tests (W7's <c>OutputPathPolicy</c> is another workstream): outputs are allowed under the given roots,
/// refused elsewhere or with a <c>..</c> segment; engine writes are allowed under one folder.
/// </summary>
internal sealed class FakePathPolicy(string? engineRoot, params OutputRootInfo[] roots) : IOutputPathPolicy
{
    public List<string> EngineChecks { get; } = [];

    public PathCheck Check(string repoRelativePath)
    {
        var normalized = repoRelativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(s => s is ".." or "."))
            return new PathCheck(false, normalized, null, "MQ6004", "path has a '..' segment");
        var root = roots.Where(r => normalized.StartsWith(r.Path + "/", StringComparison.Ordinal))
            .OrderByDescending(r => r.Path.Length).FirstOrDefault();
        return root is null
            ? new PathCheck(false, normalized, null, "MQ6004", "not under an outputs.allow root")
            : new PathCheck(true, normalized, root, null, null);
    }

    public PathCheck CheckEngineWrite(WriteTarget target, string fullPath)
    {
        EngineChecks.Add(fullPath);
        var allowed = engineRoot is not null && target == WriteTarget.Model
            && Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(engineRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return new PathCheck(allowed, fullPath, null, allowed ? null : "MQ6004", allowed ? null : "outside the model root");
    }
}

/// <summary>A formatter runner that records calls and applies a transform in memory.</summary>
internal sealed class FakeFormatterRunner : IFormatterRunner
{
    public List<(string Formatter, string Path, string Input)> Calls { get; } = [];

    public int VersionChecks { get; private set; }

    public Func<FormatterSettings, string, string> Transform { get; set; } = (f, text) => text.ToUpperInvariant();

    public Diagnostic? VersionError { get; set; }

    public Diagnostic? FormatError { get; set; }

    public Task<IReadOnlyList<Diagnostic>> VerifyVersionsAsync(IReadOnlyList<FormatterSettings> formatters, CancellationToken ct)
    {
        VersionChecks++;
        IReadOnlyList<Diagnostic> result = VersionError is null ? [] : [VersionError];
        return Task.FromResult(result);
    }

    public Task<FormatResult> FormatAsync(FormatterSettings formatter, string path, ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        var text = System.Text.Encoding.UTF8.GetString(input.Span);
        Calls.Add((formatter.Name, path, text));
        if (FormatError is not null)
            return Task.FromResult(new FormatResult(false, ReadOnlyMemory<byte>.Empty, FormatError));
        return Task.FromResult(new FormatResult(true, System.Text.Encoding.UTF8.GetBytes(Transform(formatter, text)), null));
    }
}

/// <summary>Builds rendered units for post-processing tests.</summary>
internal static class Units
{
    public static RenderedUnit Rendered(OutputMode mode, string? formatter, params RenderedFile[] files) =>
        Rendered("unit", mode, formatter, false, files);

    public static RenderedUnit Rendered(string unitId, OutputMode mode, string? formatter, bool failed, params RenderedFile[] files)
    {
        var packUnit = new PackUnit { Id = unitId, Template = "t.scriban", For = "model", Mode = mode, Formatter = formatter };
        var manifest = new PackManifest { Name = "pack", Version = "1.0.0", Engine = ">=1.0 <2.0", Units = [packUnit] };
        var pack = new LoadedPack("pack", 0, "/packs/pack", "templates/pack", manifest, new PackSettings(),
            new Dictionary<string, System.Text.Json.JsonElement>(), [], "scripts", new Dictionary<string, IReadOnlyDictionary<string, string>>());
        var planned = new PlannedUnit("pack/" + unitId, pack, packUnit, (IResolvedObject?)null, "static");
        return new RenderedUnit(planned, files, [], "input", [], failed);
    }

    public static RenderedFile File(string path, string text) => new(path, text, FileRole.Main);

    public static FormatterSettings Formatter(string name, params string[] extensions) =>
        new() { Name = name, Extensions = extensions, Command = "fmt-" + name, Version = "1.0.0" };
}
