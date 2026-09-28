using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// A renderer that honors the <see cref="IRenderer"/> and <see cref="RenderContext"/> contracts without Scriban: it reads the unit's
/// template from the pack folder, substitutes a few placeholders, records every read through an <see cref="IReadRecorder"/> the way
/// the tracking context does (an <see cref="IResolvedObject"/>'s <c>Dependencies</c> on member reads, an <see cref="RList{T}"/>'s
/// <c>MembershipKeys</c> when enumerated, <c>t:&lt;pack&gt;/&lt;path&gt;</c> for templates), computes the input hash with
/// <see cref="RenderContext.Hasher"/>, and streams units lazily in input order.
/// </summary>
internal sealed class FakeRenderer : IRenderer
{
    private int _produced;

    /// <summary>Units yielded so far.</summary>
    public int Produced => Volatile.Read(ref _produced);

    /// <summary>Keys of the units rendered, in order.</summary>
    public ConcurrentQueue<string> Rendered { get; } = new();

    /// <summary>Called before each unit renders (tests use it to pause or cancel).</summary>
    public Func<PlannedUnit, CancellationToken, Task>? BeforeUnit { get; set; }

    /// <summary>The parallelism the last run was given.</summary>
    public int LastParallelism { get; private set; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RenderedUnit> RenderAsync(IReadOnlyList<PlannedUnit> units, RenderContext context, IProgress<ProgressUpdate>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        LastParallelism = context.MaxDegreeOfParallelism;
        for (var i = 0; i < units.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (BeforeUnit is not null)
                await BeforeUnit(units[i], ct);
            var rendered = Render(units[i], context);
            Interlocked.Increment(ref _produced);
            progress?.Report(new ProgressUpdate(PipelineStage.Render, i + 1, units.Count, rendered.Files.FirstOrDefault()?.Path, units[i].Pack.Name));
            yield return rendered;
        }
    }

    /// <inheritdoc/>
    public Task<RenderedUnit> RenderOneAsync(PlannedUnit unit, RenderContext context, CancellationToken ct) => Task.FromResult(Render(unit, context));

    private RenderedUnit Render(PlannedUnit unit, RenderContext context)
    {
        Rendered.Enqueue(unit.Key);
        var reads = new Recorder();
        var files = new List<RenderedFile>();
        var diagnostics = new List<Diagnostic>();
        var failed = false;
        var template = Read(unit.Pack, unit.Unit.Template, reads);
        if (template.Contains("{{fail}}", StringComparison.Ordinal))
        {
            failed = true;
            diagnostics.Add(RuleCatalog.Create("MQ6006", $"Render error in {unit.Key}.", unit.Element?.Id, unit.Pack.RelativePath + "/" + unit.Unit.Template));
        }
        else
        {
            var body = Substitute(template, unit, context, reads);
            var main = new StringBuilder();
            string? blockPath = null;
            var block = new StringBuilder();
            foreach (var line in body.Split('\n'))
            {
                if (line.StartsWith("@@file ", StringComparison.Ordinal))
                {
                    if (blockPath is not null)
                        files.Add(new RenderedFile(Prefix(unit.Pack, blockPath), block.ToString(), FileRole.Block));
                    blockPath = line["@@file ".Length..].Trim();
                    block.Clear();
                    continue;
                }

                (blockPath is null ? main : block).Append(line).Append('\n');
            }

            if (blockPath is not null)
                files.Add(new RenderedFile(Prefix(unit.Pack, blockPath), block.ToString().TrimEnd('\n') + "\n", FileRole.Block));
            if (unit.Unit.Output is { } output)
                files.Insert(0, new RenderedFile(Prefix(unit.Pack, Substitute(output, unit, context, reads)), main.ToString().TrimEnd('\n') + "\n", FileRole.Main));
            if (unit.Unit.Mode == OutputMode.Pair && unit.Unit.Companion is { } companion)
            {
                var text = Substitute(Read(unit.Pack, companion.Template, reads), unit, context, reads);
                files.Add(new RenderedFile(Prefix(unit.Pack, Substitute(companion.Output, unit, context, reads)), text, FileRole.Companion));
            }
        }

        var keys = reads.Keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new RenderedUnit(unit, files, keys, context.Hasher.InputHash(unit.StaticHash, keys), diagnostics, failed);
    }

    private static string Prefix(LoadedPack pack, string path) => pack.Settings.Output.Length == 0 ? path : pack.Settings.Output.TrimEnd('/') + "/" + path;

    private static string Read(LoadedPack pack, string path, Recorder reads)
    {
        reads.Record("t:" + pack.Name + "/" + path);
        var full = PackFiles.Resolve(pack.RootPath, path) ?? throw new InvalidOperationException("Template outside the pack: " + path);
        return File.ReadAllText(full, Encoding.UTF8);
    }

    private static string Substitute(string text, PlannedUnit unit, RenderContext context, Recorder reads)
    {
        var sb = new StringBuilder();
        var index = 0;
        while (true)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
                break;
            var close = text.IndexOf("}}", open, StringComparison.Ordinal);
            sb.Append(text, index, open - index);
            sb.Append(Value(text[(open + 2)..close].Trim(), unit, context, reads));
            index = close + 2;
        }

        sb.Append(text, index, text.Length - index);
        return sb.ToString();
    }

    private static string Value(string name, PlannedUnit unit, RenderContext context, Recorder reads)
    {
        var element = unit.Element;
        if (name.StartsWith("param:", StringComparison.Ordinal))
            return unit.Pack.Parameters.TryGetValue(name["param:".Length..], out var value) ? value.ToString() : "";
        switch (name)
        {
            case "name":
                reads.Read(element);
                return element switch
                {
                    RElement e => e.Name,
                    RTable t => t.Name,
                    _ => "model",
                };
            case "id":
                reads.Read(element);
                return element?.Id ?? "";
            case "attributes":
                reads.Read(element);
                if (element is not REntity entity)
                    return "";
                reads.Members(entity.Attributes);
                return string.Join(",", entity.Attributes.Select(a => { reads.Read(a); return a.Name; }));
            case "table":
                reads.Read(element);
                if (element is not REntity mapped)
                    return "-";
                var mapping = mapped.Mappings.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Value).FirstOrDefault();
                if (mapping is null)
                    return "-";
                reads.Read(mapping.Table);
                return mapping.Table.Name;
            case "entities":
                reads.Members(context.Model.Entities);
                return string.Join(" ", context.Model.Entities.Select(e => { reads.Read(e); return e.Name; }));
            default:
                return "{{" + name + "}}";
        }
    }

    private sealed class Recorder : IReadRecorder
    {
        public List<string> Keys { get; } = [];

        public void Record(string dependencyKey) => Keys.Add(dependencyKey);

        public void Read(IResolvedObject? element)
        {
            if (element is not null)
                Keys.AddRange(element.Dependencies);
        }

        public void Members<T>(IReadOnlyList<T> list) where T : IResolvedObject
        {
            if (list is RList<T> rlist)
                Keys.AddRange(rlist.MembershipKeys);
        }
    }
}
