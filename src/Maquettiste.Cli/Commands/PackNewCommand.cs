using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste pack new &lt;name&gt; [--from empty|sql-ddl|csharp-dapper]</c>: scaffolds <c>.maquettiste/templates/&lt;name&gt;/</c>
/// (engine-design.md section 16). <c>empty</c> writes a <c>pack.json</c> with one <c>each entity</c> unit, its template and a
/// <c>helpers.js</c>; the other choices copy that starter pack under the new name. An existing folder is refused (exit 4).
/// </summary>
internal static partial class PackNewCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        if (context.Line.Positionals.Count >= 2 && context.Line.Positionals[1] == "remove")
            return await PackRemoveCommand.RunAsync(context, ct).ConfigureAwait(false);
        if (context.Line.Positionals.Count < 2 || context.Line.Positionals[1] != "new")
            throw new UsageException("Usage: maquettiste pack new <name> [--from empty|sql-ddl|csharp-dapper], or maquettiste pack remove <name> [--apply] [--format text|json].");
        context.Line.Expect("pack new", 3, "--from");
        var name = context.Line.Positionals[2];
        if (!KeyPattern().IsMatch(name))
            throw new UsageException($"The pack name '{name}' must be kebab-case: lowercase letters and digits separated by single hyphens, starting with a letter.");
        var from = context.Line.Choice("--from", "empty", ["empty", .. StarterPacks.Names]);
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var options = context.EngineOptions(repo);
        var packRoot = Path.Combine(repo, GlobalContext.ModelFolder, "templates", name);
        if (Directory.Exists(packRoot) || File.Exists(packRoot))
        {
            await context.Error.WriteLineAsync($"maquettiste: .maquettiste/templates/{name}/ already exists; choose another name or remove it.").ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }

        var files = new GuardedFiles(new OutputPathPolicy(options, null));
        var json = new CanonicalJson(new SchemaRegistry());
        var packJsonPath = "templates/" + name + "/pack.json";
        var output = new List<KeyValuePair<string, byte[]>>();
        if (from == "empty")
        {
            var manifest = new JsonObject
            {
                ["name"] = name,
                ["version"] = "0.1.0",
                ["engine"] = ">=1.0 <2.0",
                ["description"] = "A new template pack.",
                ["units"] = new JsonArray(new JsonObject
                {
                    ["id"] = "entity",
                    ["template"] = "entity.scriban",
                    ["for"] = "each entity",
                    ["output"] = "{{ kebab entity.name }}.txt",
                }),
            };
            output.Add(new("pack.json", json.Write(manifest, "pack.json", packJsonPath)));
            output.AddRange(StarterPacks.Empty().Select(f => new KeyValuePair<string, byte[]>(f.Key, Encoding.UTF8.GetBytes(f.Value))));
        }
        else
        {
            foreach (var (relative, bytes) in StarterPacks.Files(from, out _))
            {
                if (relative != "pack.json")
                {
                    output.Add(new(relative, bytes));
                    continue;
                }

                var manifest = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                    ?? throw new InvalidOperationException($"The {from} starter pack's pack.json is not an object.");
                manifest["name"] = name;
                output.Add(new("pack.json", json.Write(manifest, "pack.json", packJsonPath)));
            }
        }

        foreach (var (relative, bytes) in output.OrderBy(f => f.Key, StringComparer.Ordinal))
            await files.WriteAsync(WriteTarget.Model, Path.Combine(packRoot, relative.Replace('/', Path.DirectorySeparatorChar)), bytes, overwrite: false, ct).ConfigureAwait(false);

        foreach (var (relative, _) in output.OrderBy(f => f.Key, StringComparer.Ordinal))
            await context.Out.WriteLineAsync($".maquettiste/templates/{name}/{relative}").ConfigureAwait(false);
        context.Info($"Created pack '{name}' from {from}. To generate it, set packs.{name}.output in .maquettiste/maquettiste.json to a folder under an outputs.allow root.");
        return Program.ExitCodes.Success;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
