using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste snapshot create|list|show|delete|restore|compare|export|import</c> (docs/engineering/snapshots.md): named, immutable copies
/// of the whole model as zip archives under <c>.maquettiste/model-snapshots/</c>, over the same <see cref="SnapshotLibrary"/> the editor API
/// (<c>/api/snapshots</c>) and the MCP tools use. <c>restore</c> previews what it would change and writes only with <c>--apply</c>.
/// </summary>
internal static class SnapshotCommand
{
    /// <summary>Runs <c>snapshot &lt;verb&gt;</c>.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        var verb = line.Positionals.Count > 1 ? line.Positionals[1]
            : throw new UsageException("'snapshot' needs a verb: create, list, show, delete, restore, compare, export or import.");
        switch (verb)
        {
            case "create":
                line.Expect("snapshot create", 3, "--description", "--packs", "--format");
                break;
            case "list":
                line.Expect("snapshot list", 2, "--format");
                break;
            case "show" or "delete":
                line.Expect("snapshot " + verb, 3, "--format");
                break;
            case "restore":
                line.Expect("snapshot restore", 3, "--packs", "--apply", "--format");
                break;
            case "compare":
                if (line.Positionals.Count is < 3 or > 4)
                    throw new UsageException("'snapshot compare' takes <from> and, optionally, <to> (a snapshot id or working; default working).");
                line.Expect("snapshot compare", line.Positionals.Count, "--element", "--format");
                break;
            case "export":
                line.Expect("snapshot export", 3, "--out");
                if (line.Value("--out") is null)
                    throw new UsageException("'snapshot export' needs --out <file>.");
                break;
            case "import":
                line.Expect("snapshot import", 3, "--format");
                break;
            default:
                throw new UsageException($"Unknown snapshot verb '{verb}': use create, list, show, delete, restore, compare, export or import.");
        }

        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;
        var json = verb != "export" && line.Choice("--format", "text", "text", "json") == "json";
        var store = new ModelStore(context.EngineOptions(repo));
        await using (store.ConfigureAwait(false))
        {
            var library = new SnapshotLibrary(store);
            var argument = line.Positionals.Count > 2 ? line.Positionals[2] : "";
            return verb switch
            {
                "create" => await CreateAsync(context, library, argument, json, ct).ConfigureAwait(false),
                "list" => await ListAsync(context, library, json, ct).ConfigureAwait(false),
                "show" => await ShowAsync(context, library, argument, json, ct).ConfigureAwait(false),
                "delete" => await DeleteAsync(context, library, argument, json, ct).ConfigureAwait(false),
                "restore" => await RestoreAsync(context, library, argument, json, ct).ConfigureAwait(false),
                "compare" => await CompareAsync(context, library, argument, line.Positionals.Count > 3 ? line.Positionals[3] : SnapshotLibrary.Working, json, ct).ConfigureAwait(false),
                "export" => await ExportAsync(context, library, argument, ct).ConfigureAwait(false),
                _ => await ImportAsync(context, library, argument, json, ct).ConfigureAwait(false),
            };
        }
    }

    private static async Task<int> CreateAsync(GlobalContext context, SnapshotLibrary library, string name, bool json, CancellationToken ct)
    {
        SnapshotInfo created;
        try
        {
            created = await library.CreateAsync(new SnapshotCreateRequest(name, context.Line.Value("--description"), context.Line.Has("--packs"), Author(context)), ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new UsageException(ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
        }

        await WriteAsync(context, json, created, () => Describe(created)).ConfigureAwait(false);
        context.Info($"Took snapshot {created.Id} ({created.Files.ToString(CultureInfo.InvariantCulture)} documents).");
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ListAsync(GlobalContext context, SnapshotLibrary library, bool json, CancellationToken ct)
    {
        var all = await library.ListAsync(ct).ConfigureAwait(false);
        await WriteAsync(context, json, all, () =>
        {
            var text = new StringBuilder();
            foreach (var s in all)
            {
                text.Append(CultureInfo.InvariantCulture, $"{s.Id}  {s.CreatedUtc}  {s.Elements,6} elements  {s.Name}");
                if (s.Published)
                    text.Append("  [published]");
                if (s.IncludesPacks)
                    text.Append("  [packs]");
                if (s.Origin != "user")
                    text.Append("  [" + s.Origin + "]");
                text.Append('\n');
            }

            return text.Length == 0 ? "No snapshots.\n" : text.ToString();
        }).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ShowAsync(GlobalContext context, SnapshotLibrary library, string id, bool json, CancellationToken ct)
    {
        if (await library.GetAsync(id, ct).ConfigureAwait(false) is not { } found)
            return await MissingAsync(context, id).ConfigureAwait(false);
        await WriteAsync(context, json, found, () => Describe(found)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> DeleteAsync(GlobalContext context, SnapshotLibrary library, string id, bool json, CancellationToken ct)
    {
        if (!await library.DeleteAsync(id, ct).ConfigureAwait(false))
            return await MissingAsync(context, id).ConfigureAwait(false);
        await WriteAsync(context, json, new { id, deleted = true }, () => $"Deleted snapshot {id}.\n").ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> RestoreAsync(GlobalContext context, SnapshotLibrary library, string id, bool json, CancellationToken ct)
    {
        if (await library.GetAsync(id, ct).ConfigureAwait(false) is not { } snapshot)
            return await MissingAsync(context, id).ConfigureAwait(false);
        var packs = context.Line.Has("--packs");
        if (!context.Line.Has("--apply"))
        {
            var preview = await AllAsync(library, SnapshotLibrary.Working, id, ct).ConfigureAwait(false);
            await WriteAsync(context, json, preview, () => CompareText(preview!)
                + $"Restoring {id} would make the working model as above{(packs && snapshot.IncludesPacks ? ", packs included" : "")}, after a safety snapshot. Run again with --apply.\n").ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        var result = await library.RestoreAsync(id, new SnapshotRestoreRequest(packs, Author(context), ChangeSource.Cli), ct).ConfigureAwait(false);
        if (result.Outcome == SnapshotRestoreOutcome.Locked)
        {
            await context.Error.WriteLineAsync("maquettiste: a generation run holds the run lock; the snapshot was not restored. Try again when it ends.").ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }

        await WriteAsync(context, json, result, () => result.Outcome == SnapshotRestoreOutcome.Restored
            ? $"Restored {id}: {result.Written.ToString(CultureInfo.InvariantCulture)} documents written, {result.Deleted.ToString(CultureInfo.InvariantCulture)} deleted{(result.PacksRestored ? ", packs included" : "")}.\n{result.Undo}\n"
            : string.Concat(result.Diagnostics.Select(d => DiagnosticOutput.Line(d) + "\n"))).ConfigureAwait(false);
        return result.Outcome == SnapshotRestoreOutcome.Restored ? Program.ExitCodes.Success : Program.ExitCodes.Internal;
    }

    private static async Task<int> CompareAsync(GlobalContext context, SnapshotLibrary library, string from, string to, bool json, CancellationToken ct)
    {
        if (context.Line.Value("--element") is { } element)
        {
            if (await library.CompareElementAsync(from, to, element, ct).ConfigureAwait(false) is not { } diff)
                return await MissingAsync(context, from + ", " + to + " or element " + element).ConfigureAwait(false);
            await WriteAsync(context, json, diff, () =>
            {
                var text = new StringBuilder($"{diff.Kind} {diff.Name} ({diff.Id}): {diff.Change}\n");
                foreach (var field in diff.Fields)
                    text.Append(CultureInfo.InvariantCulture, $"  {Mark(field.Change)} {(field.Pointer.Length == 0 ? "/" : field.Pointer)}: {Value(field.Before)} -> {Value(field.After)}\n");
                if (diff.FieldsTruncated)
                    text.Append("  ... more fields differ\n");
                return text.ToString();
            }).ConfigureAwait(false);
            return Program.ExitCodes.Success;
        }

        if (await AllAsync(library, from, to, ct).ConfigureAwait(false) is not { } comparison)
            return await MissingAsync(context, from + " or " + to).ConfigureAwait(false);
        await WriteAsync(context, json, comparison, () => CompareText(comparison)).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ExportAsync(GlobalContext context, SnapshotLibrary library, string id, CancellationToken ct)
    {
        var output = context.Line.Value("--out")!;
        var full = Path.GetFullPath(output, context.Environment.CurrentDirectory);
        var model = Path.Combine(context.RepoRoot(), GlobalContext.ModelFolder) + Path.DirectorySeparatorChar;
        if (full.StartsWith(model, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UsageException($"--out {output} lies inside the model folder; write the export elsewhere.");
        if (library.OpenRead(id) is not { } source)
            return await MissingAsync(context, id).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            if (Path.GetDirectoryName(full) is { } folder)
                Directory.CreateDirectory(folder);
            var target = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true);
            await using (target.ConfigureAwait(false))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        context.Info("Wrote " + full + ".");
        return Program.ExitCodes.Success;
    }

    private static async Task<int> ImportAsync(GlobalContext context, SnapshotLibrary library, string file, bool json, CancellationToken ct)
    {
        var full = Path.GetFullPath(file, context.Environment.CurrentDirectory);
        if (!File.Exists(full))
        {
            await context.Error.WriteLineAsync($"maquettiste: {file} does not exist.").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        SnapshotImportResult result;
        var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, useAsync: true);
        await using (source.ConfigureAwait(false))
            result = await library.ImportAsync(source, ct).ConfigureAwait(false);
        await WriteAsync(context, json, result, () => result.Snapshot is { } s
            ? Describe(s)
            : string.Concat(result.Diagnostics.Select(d => DiagnosticOutput.Line(d) + "\n"))).ConfigureAwait(false);
        if (result.Snapshot is { } imported)
            context.Info($"Imported snapshot {imported.Id}.");
        return result.Snapshot is null ? Program.ExitCodes.Invalid : Program.ExitCodes.Success;
    }

    /// <summary>Every page of a comparison in one: the CLI prints the whole list.</summary>
    private static async Task<SnapshotComparison?> AllAsync(SnapshotLibrary library, string from, string to, CancellationToken ct)
    {
        var first = await library.CompareAsync(from, to, 0, SnapshotLibrary.MaxCompareLimit, ct).ConfigureAwait(false);
        if (first is null)
            return null;
        var elements = new List<SnapshotElementChange>(first.Elements);
        for (var next = first.Next; next is { } offset;)
        {
            var page = await library.CompareAsync(from, to, offset, SnapshotLibrary.MaxCompareLimit, ct).ConfigureAwait(false);
            elements.AddRange(page!.Elements);
            next = page.Next;
        }

        return first with { Elements = elements, Next = null };
    }

    private static string CompareText(SnapshotComparison c)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"{c.From} -> {c.To}: {c.Added} added, {c.Removed} removed, {c.Changed} changed, {c.Files.Count}{(c.FilesTruncated ? "+" : "")} other documents\n"));
        foreach (var kind in c.Kinds)
            text.Append(CultureInfo.InvariantCulture, $"  {kind.Kind,-16} +{kind.Added} -{kind.Removed} ~{kind.Changed}\n");
        foreach (var e in c.Elements)
        {
            text.Append(CultureInfo.InvariantCulture, $"{Mark(e.Change)} {e.Kind} {e.Name} ({e.Id})");
            if (e.PreviousName is { } was)
                text.Append(CultureInfo.InvariantCulture, $" (was {was})");
            if (e.PreviousPath is { } moved)
                text.Append(CultureInfo.InvariantCulture, $" (moved from {moved})");
            text.Append('\n');
        }

        foreach (var f in c.Files)
            text.Append(CultureInfo.InvariantCulture, $"{Mark(f.Change)} {f.Path}\n");
        return text.ToString();
    }

    private static string Describe(SnapshotInfo s) =>
        string.Create(CultureInfo.InvariantCulture, $"""
            id:          {s.Id}
            name:        {s.Name}
            description: {s.Description}
            author:      {s.Author}
            created:     {s.CreatedUtc}
            origin:      {s.Origin}
            published:   {(s.Published ? "yes" : "no")}
            packs:       {(s.IncludesPacks ? "included" : "not included")}
            documents:   {s.Files}
            elements:    {s.Elements} ({string.Join(", ", s.Kinds.Select(k => k.Key + " " + k.Value.ToString(CultureInfo.InvariantCulture)))})
            model hash:  {s.ModelHash}
            size:        {s.Size} bytes

            """).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Mark(string change) => change switch
    {
        "added" => "+",
        "removed" => "-",
        _ => "~",
    };

    private static string Value(JsonElement? value) => value is { } v ? (v.GetRawText() is { Length: > 80 } long_ ? long_[..77] + "..." : v.GetRawText()) : "(none)";

    private static string Author(GlobalContext context) =>
        context.Environment.GetEnvironmentVariable("USER") ?? context.Environment.GetEnvironmentVariable("USERNAME") ?? "";

    private static async Task<int> MissingAsync(GlobalContext context, string what)
    {
        await context.Error.WriteLineAsync($"maquettiste: no snapshot {what} (maquettiste snapshot list shows them).").ConfigureAwait(false);
        return Program.ExitCodes.Invalid;
    }

    private static async Task WriteAsync<T>(GlobalContext context, bool json, T value, Func<string> text)
    {
        await context.Out.WriteAsync(json ? JsonSerializer.Serialize(value, CliJson.Options) + "\n" : text()).ConfigureAwait(false);
        await context.Out.FlushAsync().ConfigureAwait(false);
    }
}
