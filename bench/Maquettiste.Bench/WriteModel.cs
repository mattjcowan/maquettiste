using System.Globalization;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Bench;

/// <summary>
/// The bench app's <c>write-model</c> verb (explorer-redesign.md section 5): writes the synthetic model, with the explorer's scale
/// shape, into a repo folder and stops. No benchmark runs. The defaults are the explorer's large mock: 5,000 entities, 20,000
/// relations, 40 packages nested three deep, 150 diagrams of 20 to 300 entities, three schemas per server database, 60 designed
/// tables, 40 views, 30 sequences and 60 lookup entities of 60 attributes. Exit codes: 0 written, 2 usage or refused folder.
/// </summary>
public static class WriteModel
{
    /// <summary>The first argument that selects the verb.</summary>
    public const string Verb = "write-model";

    /// <summary>The marker the verb leaves in the model's cache folder; a later run only replaces a model folder that carries it.</summary>
    public const string MarkerFile = "write-model.marker";

    /// <summary>The usage text.</summary>
    public const string Usage =
        "usage: Maquettiste.Bench write-model --out <dir> [--seed 42] [--entities 5000] [--relations 20000] [--enums 500]\n" +
        "         [--domains 40] [--domain-depth 3] [--domain-width 4] [--diagrams 150 | --diagrams-per-domain <n>] [--diagram-size 20..300]\n" +
        "         [--schemas 3] [--designed-tables 60] [--views 40] [--sequences 30] [--lookups 60] [--lookup-attributes 60]\n" +
        "         [--fanout <n>] [--no-example-packs]\n";

    /// <summary>The explorer's large mock (the verb's defaults).</summary>
    public static SyntheticModelOptions ExplorerDefaults { get; } = new()
    {
        Packages = 40,
        DomainDepth = 3,
        DomainWidth = 4,
        Diagrams = 150,
        DiagramMinSize = 20,
        DiagramMaxSize = 300,
        Schemas = 3,
        DesignedTables = 60,
        Views = 40,
        Sequences = 30,
        Lookups = 60,
        LookupAttributes = 60,
    };

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after <see cref="Verb"/>.</param>
    /// <param name="output">Where the summary line goes.</param>
    /// <param name="error">Where usage errors go.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!TryParse(args, out var outDir, out var options, out var message))
        {
            await error.WriteAsync(message + "\n" + Usage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var files = await WriteAsync(outDir, options, ct).ConfigureAwait(false);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"wrote {files} model files to {Path.GetFullPath(outDir)}")).ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>
    /// Writes the model into <paramref name="outDir"/>. A <c>.maquettiste</c> folder left there by an earlier run (it carries
    /// <see cref="MarkerFile"/>) loses its <c>model</c> folder first, so no element of a larger earlier model survives; any other
    /// non-empty <c>.maquettiste</c> folder is refused.
    /// </summary>
    /// <param name="outDir">The repo folder.</param>
    /// <param name="options">The model options.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of files under <c>.maquettiste/model</c>.</returns>
    /// <exception cref="InvalidOperationException">The folder holds a model this verb did not write, or the path guard refused a path.</exception>
    public static async Task<int> WriteAsync(string outDir, SyntheticModelOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        ArgumentNullException.ThrowIfNull(options);
        Synthetic.SyntheticModel.Validate(options);
        var root = Path.GetFullPath(outDir);
        var modelRoot = Path.Combine(root, ".maquettiste");
        var cache = Path.Combine(modelRoot, ".cache");
        var policy = new OutputPathPolicy(new EngineOptions { RepoRoot = root, CacheDirectory = cache }, null);
        var marker = Guarded(policy, WriteTarget.Cache, Path.Combine(cache, MarkerFile));
        if (Directory.Exists(modelRoot) && Directory.EnumerateFileSystemEntries(modelRoot).Any())
        {
            if (!File.Exists(marker))
            {
                throw new InvalidOperationException(modelRoot + " holds a model write-model did not write (no .cache/" + MarkerFile +
                    "); it will not be replaced. Choose another --out.");
            }

            var model = Guarded(policy, WriteTarget.Model, Path.Combine(modelRoot, "model"));
            if (Directory.Exists(model))
                Directory.Delete(model, recursive: true);
        }

        await SyntheticModelGenerator.WriteAsync(root, options, ct).ConfigureAwait(false);
        Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(marker, "Written by Maquettiste.Bench write-model; its next run replaces model/.\n"u8.ToArray(), ct)
            .ConfigureAwait(false);
        return Directory.EnumerateFiles(Path.Combine(modelRoot, "model"), "*", SearchOption.AllDirectories).Count();
    }

    /// <summary>Parses the arguments after <see cref="Verb"/>, starting from <see cref="ExplorerDefaults"/>.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="outDir">The <c>--out</c> folder.</param>
    /// <param name="options">The model options.</param>
    /// <param name="error">The usage error, when parsing fails.</param>
    /// <returns><see langword="true"/> when the arguments are valid.</returns>
    public static bool TryParse(IReadOnlyList<string> args, out string outDir, out SyntheticModelOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        var folder = "";
        var current = ExplorerDefaults;
        int? perDomain = null;
        options = current;
        outDir = "";
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var value = i + 1 < args.Count ? args[i + 1] : null;
            var parsed = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number);
            int Min(string option) => option switch
            {
                "--entities" => 2,
                "--enums" or "--domains" or "--domain-depth" or "--domain-width" or "--fanout" => 1,
                "--lookup-attributes" => 5,
                _ => 0,
            };

            SyntheticModelOptions? next = arg switch
            {
                "--seed" => current with { Seed = number },
                "--entities" => current with { Entities = number },
                "--relations" => current with { Relations = number },
                "--enums" => current with { Enums = number },
                "--domains" => current with { Packages = number },
                "--domain-depth" => current with { DomainDepth = number },
                "--domain-width" => current with { DomainWidth = number },
                "--diagrams" => current with { Diagrams = number },
                "--diagrams-per-domain" => current,
                "--schemas" => current with { Schemas = number },
                "--designed-tables" => current with { DesignedTables = number },
                "--views" => current with { Views = number },
                "--sequences" => current with { Sequences = number },
                "--lookups" => current with { Lookups = number },
                "--lookup-attributes" => current with { LookupAttributes = number },
                "--fanout" => current with { Fanout = number },
                _ => null,
            };
            if (next is not null)
            {
                if (!parsed || number < Min(arg))
                    return Fail(string.Create(CultureInfo.InvariantCulture, $"{arg} needs a whole number of at least {Min(arg)}."), out error);
                if (arg == "--diagrams-per-domain")
                    perDomain = number;
                current = next;
                i++;
                continue;
            }

            switch (arg)
            {
                case "--out":
                    if (string.IsNullOrEmpty(value))
                        return Fail("--out needs a folder.", out error);
                    folder = value;
                    i++;
                    break;
                case "--diagram-size":
                    if (!SizeRange(value, ref current))
                        return Fail("--diagram-size needs <min>..<max> with 1 <= min <= max.", out error);
                    i++;
                    break;
                case "--no-example-packs":
                    current = current with { IncludeExamplePacks = false };
                    break;
                default:
                    return Fail("unknown option " + arg + ".", out error);
            }
        }

        if (perDomain is { } n)
            current = current with { Diagrams = n * current.Packages };
        if (folder.Length == 0)
            return Fail("--out is required.", out error);
        try
        {
            Synthetic.SyntheticModel.Validate(current);
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message, out error);
        }

        outDir = folder;
        options = current;
        error = "";
        return true;
    }

    private static bool SizeRange(string? value, ref SyntheticModelOptions options)
    {
        var parts = (value ?? "").Split("..");
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            || min < 1 || max < min)
        {
            return false;
        }

        options = options with { DiagramMinSize = min, DiagramMaxSize = max };
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static string Guarded(IOutputPathPolicy policy, WriteTarget target, string path)
    {
        var check = policy.CheckEngineWrite(target, path);
        return check.Allowed ? check.NormalizedPath : throw new InvalidOperationException("The engine-write guard refused " + path + ": " + check.Reason);
    }
}
