using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Testing;

/// <summary>
/// Proves the store's incremental whole-model validation (change-driven-edits-design.md section 4, step 2): after each edit, the report the
/// store works out from the previous snapshot's equals, element by element and in order, a fresh whole-model validation of the same
/// snapshot by a validator of its own, for both scopes (seeds with a rows script judged, and left out as previews want), and the store says
/// whether it validated incrementally or the whole model.
/// </summary>
public sealed class IncrementalValidationCheck : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly EngineOptions _options;
    private readonly IModelValidator _fresh;
    private readonly List<string> _log = [];

    /// <summary>Opens a store over a repo.</summary>
    /// <param name="options">The repo's engine options.</param>
    public IncrementalValidationCheck(EngineOptions options)
    {
        _options = options;
        Store = new ModelStore(options);
        _fresh = Engine.Generation.EngineServices.Create(options).Validator;
    }

    /// <summary>The store under test.</summary>
    public ModelStore Store { get; }

    /// <summary>The current snapshot.</summary>
    public ModelSnapshot Snapshot => Store.Current ?? throw new InvalidOperationException("Load first.");

    /// <summary>What each check found: its label, and whether it was incremental.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>How many checks validated incrementally.</summary>
    public int Incremental { get; private set; }

    /// <summary>How many checks validated the whole model.</summary>
    public int Whole { get; private set; }

    /// <summary>Copies every file under a folder into the repo's model root.</summary>
    /// <param name="modelRoot">The repo's model root.</param>
    /// <param name="from">A folder holding a <c>.maquettiste</c> folder's content.</param>
    public static void CopyModel(string modelRoot, string from)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            if (relative.StartsWith("manifest", StringComparison.Ordinal) || relative.StartsWith("snapshots", StringComparison.Ordinal))
                continue;
            var target = Path.Combine(modelRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Compares the store's validation of the current snapshot with a fresh whole-model validation, for both scopes, and checks how the
    /// store validated it.
    /// </summary>
    /// <param name="label">What the last edit did, for messages.</param>
    /// <param name="expectIncremental">Whether the store must have validated incrementally, or the whole model; <see langword="null"/> for either.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the store validated incrementally.</returns>
    public async Task<bool> CheckAsync(string label, bool? expectIncremental, CancellationToken ct)
    {
        await Store.LoadAsync(ct).ConfigureAwait(false);
        var snapshot = Snapshot;
        var incremental = false;
        foreach (var defer in new[] { false, true })
        {
            var shared = await Store.WholeValidationAsync(snapshot, defer, ct).ConfigureAwait(false);
            var expected = await _fresh.ValidateAsync(snapshot, defer ? ValidationScope.Previews : ValidationScope.All, null, ct).ConfigureAwait(false);
            Compare(label + (defer ? " (previews)" : ""), expected.Diagnostics, shared.Report.Diagnostics);
            Compare(label + (defer ? " (previews, suppressed)" : " (suppressed)"), [.. expected.Suppressed.Select(s => s.ToString())],
                [.. shared.Report.Suppressed.Select(s => s.ToString())]);
            if (!defer)
                incremental = shared.Incremental;
        }

        if (expectIncremental is { } wanted && wanted != incremental)
            throw new InvalidOperationException($"{label}: the store validated {(incremental ? "incrementally" : "the whole model")}, expected {(wanted ? "incrementally" : "the whole model")} ({Store.LastIncrementalFallback}).");
        if (incremental)
            Incremental++;
        else
            Whole++;
        _log.Add(label + ": " + (incremental ? "incremental" : "whole (" + Store.LastIncrementalFallback + ")"));
        return incremental;
    }

    /// <summary>
    /// The store's shared whole-model validation of its current snapshot (what a generation run's validation stage used), compared with a
    /// fresh whole-model validation; throws when they differ.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="options">Its engine options.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was incremental, and how many of the snapshot's documents its built-in rules validated.</returns>
    public static async Task<(bool Incremental, int Validated, int Documents)> SharedValidationAsync(ModelStore store, EngineOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var snapshot = store.Current ?? throw new InvalidOperationException("Load first.");
        var shared = await store.WholeValidationAsync(snapshot, deferScriptedRows: false, ct).ConfigureAwait(false);
        var fresh = await Engine.Generation.EngineServices.Create(options).Validator.ValidateAsync(snapshot, ValidationScope.All, null, ct).ConfigureAwait(false);
        Compare("shared validation", fresh.Diagnostics, shared.Report.Diagnostics);
        return (shared.Incremental, shared.Validated, snapshot.Documents.Count);
    }

    private static void Compare<T>(string label, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        var count = Math.Min(expected.Count, actual.Count);
        for (var i = 0; i < count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
                throw new InvalidOperationException($"{label}: finding {i} differs.\n  whole:       {expected[i]}\n  incremental: {actual[i]}");
        }

        if (expected.Count != actual.Count)
        {
            var more = expected.Count > actual.Count ? expected.Skip(count).Take(5) : actual.Skip(count).Take(5);
            throw new InvalidOperationException($"{label}: whole has {expected.Count} findings, incremental {actual.Count}; first extra: "
                + string.Join("\n  ", more));
        }
    }

    /// <summary>The full path of a repo-relative path.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <returns>The full path.</returns>
    public string FullPath(string path) => Path.Combine(_options.RepoRoot, path);

    /// <summary>Edits an element's file on disk and refreshes the store with its path (the watcher's route).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="edit">The edit.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The file's bytes before the edit.</returns>
    public async Task<byte[]> EditAsync(string id, Action<JsonObject> edit, CancellationToken ct)
    {
        var path = Snapshot.GetDocument(id)?.Path ?? throw new InvalidOperationException("No element " + id);
        var before = await File.ReadAllBytesAsync(FullPath(path), ct).ConfigureAwait(false);
        var node = JsonNode.Parse(before)!.AsObject();
        edit(node);
        await WriteAsync(path, Encoding.UTF8.GetBytes(node.ToJsonString(Indented) + "\n"), ct).ConfigureAwait(false);
        return before;
    }

    /// <summary>Saves an element through the store's save (the editor's route), the document changed by <paramref name="edit"/>.</summary>
    /// <param name="id">The element id.</param>
    /// <param name="edit">The edit.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the store saved it (a save that introduces an error is refused).</returns>
    public async Task<bool> SaveAsync(string id, Action<JsonObject> edit, CancellationToken ct)
    {
        var document = Snapshot.GetDocument(id) ?? throw new InvalidOperationException("No element " + id);
        var node = JsonNode.Parse(await File.ReadAllBytesAsync(FullPath(document.Path), ct).ConfigureAwait(false))!.AsObject();
        node.Remove("$schema");
        edit(node);
        var result = await Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, ct).ConfigureAwait(false);
        return result.Outcome == SaveOutcome.Saved;
    }

    /// <summary>Writes a file and refreshes the store with its path.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="bytes">The content.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FullPath(path))!);
        await File.WriteAllBytesAsync(FullPath(path), bytes, ct).ConfigureAwait(false);
        await Store.RefreshAsync([path], ct).ConfigureAwait(false);
    }

    /// <summary>Writes an element's earlier bytes back at its earlier path, removing the file a save moved it to, in one refresh.</summary>
    /// <param name="id">The element id.</param>
    /// <param name="path">The earlier repo-relative path.</param>
    /// <param name="bytes">The earlier content.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RestoreAsync(string id, string path, byte[] bytes, CancellationToken ct)
    {
        var now = Snapshot.GetDocument(id)?.Path;
        if (now is not null && !string.Equals(now, path, StringComparison.Ordinal))
            File.Delete(FullPath(now));
        await File.WriteAllBytesAsync(FullPath(path), bytes, ct).ConfigureAwait(false);
        await Store.RefreshAsync(now is null || now == path ? [path] : [path, now], ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a file and refreshes the store with its path.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The file's bytes.</returns>
    public async Task<byte[]> DeleteAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(FullPath(path), ct).ConfigureAwait(false);
        File.Delete(FullPath(path));
        await Store.RefreshAsync([path], ct).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>
    /// The standard edit sequence over whatever the model holds, each followed by a check: an entity's attributes changed (renamed, made
    /// required, given a name another attribute has), an entity renamed (through the store's save, then to another entity's name), a
    /// relation mapping removed and added back, an entity's bindings removed, retargeted and restored, a table's column changed, renamed
    /// and removed, an enum member renamed, a seed row changed, an entity created and deleted; then edits that must validate the whole
    /// model: the settings, a package rename and an entity's base.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunStandardEditsAsync(CancellationToken ct)
    {
        await CheckAsync("load", false, ct).ConfigureAwait(false);
        var model = Snapshot;
        var entities = model.All<Entity>().OrderBy(e => model.GetDocument(e.Id)!.Path, StringComparer.Ordinal).ToList();

        // An entity's attributes: the most referenced entity with two attributes or more.
        var subject = entities.Where(e => e.Attributes.Count >= 2).OrderByDescending(e => model.ReferencesTo(e.Id).Count).FirstOrDefault();
        if (subject is not null)
        {
            var original = await EditAsync(subject.Id, n => Attribute(n, ^1)["name"] = "renamedAttribute", ct).ConfigureAwait(false);
            await CheckAsync("attribute renamed", true, ct).ConfigureAwait(false);
            await EditAsync(subject.Id, n => Attribute(n, 0)["required"] = !(Attribute(n, 0)["required"]?.GetValue<bool>() ?? false), ct).ConfigureAwait(false);
            await CheckAsync("attribute made required", true, ct).ConfigureAwait(false);
            await EditAsync(subject.Id, n => Attribute(n, ^1)["name"] = Attribute(n, 0)["name"]!.GetValue<string>(), ct).ConfigureAwait(false);
            await CheckAsync("attribute given another attribute's name", true, ct).ConfigureAwait(false);
            await EditAsync(subject.Id, n => Attribute(n, ^1)["type"] = "string", ct).ConfigureAwait(false);
            await CheckAsync("attribute type changed", true, ct).ConfigureAwait(false);
            await WriteAsync(model.GetDocument(subject.Id)!.Path, original, ct).ConfigureAwait(false);
            await CheckAsync("entity restored", true, ct).ConfigureAwait(false);
        }

        // An entity renamed through the store's save, then given another entity's name.
        var renamed = entities.OrderByDescending(e => model.ReferencesTo(e.Id).Count).FirstOrDefault();
        if (renamed is not null)
        {
            var path = model.GetDocument(renamed.Id)!.Path;
            var original = await File.ReadAllBytesAsync(FullPath(path), ct).ConfigureAwait(false);
            if (await SaveAsync(renamed.Id, n => n["name"] = renamed.Name + "Renamed", ct).ConfigureAwait(false))
                await CheckAsync("entity renamed (save)", true, ct).ConfigureAwait(false);
            if (entities.FirstOrDefault(e => e.Id != renamed.Id && e.Package == renamed.Package) is { } other)
            {
                await EditAsync(renamed.Id, n => n["name"] = other.Name, ct).ConfigureAwait(false);
                await CheckAsync("entity given another entity's name", true, ct).ConfigureAwait(false);
            }

            await RestoreAsync(renamed.Id, path, original, ct).ConfigureAwait(false);
            await CheckAsync("entity name restored", true, ct).ConfigureAwait(false);
        }

        // A relation mapping removed and added back.
        if (model.All<Mapping>().FirstOrDefault() is { } mapping)
        {
            var path = model.GetDocument(mapping.Id)!.Path;
            var bytes = await DeleteAsync(path, ct).ConfigureAwait(false);
            await CheckAsync("relation mapping removed", true, ct).ConfigureAwait(false);
            await WriteAsync(path, bytes, ct).ConfigureAwait(false);
            await CheckAsync("relation mapping added", true, ct).ConfigureAwait(false);
        }

        // An entity's bindings removed, retargeted and restored.
        if (entities.FirstOrDefault(e => e.Bindings.Count > 0) is { } bound)
        {
            var original = await EditAsync(bound.Id, n => n.Remove("bindings"), ct).ConfigureAwait(false);
            await CheckAsync("binding removed", true, ct).ConfigureAwait(false);
            await WriteAsync(model.GetDocument(bound.Id)!.Path, original, ct).ConfigureAwait(false);
            await CheckAsync("binding restored", true, ct).ConfigureAwait(false);
            var source = bound.Bindings[0].Source;
            if (model.All<Table>().FirstOrDefault(t => t.Id != source) is { } elsewhere)
            {
                await EditAsync(bound.Id, n => n["bindings"]![0]!["source"] = elsewhere.Id, ct).ConfigureAwait(false);
                await CheckAsync("binding retargeted", true, ct).ConfigureAwait(false);
                await WriteAsync(model.GetDocument(bound.Id)!.Path, original, ct).ConfigureAwait(false);
                await CheckAsync("binding restored again", true, ct).ConfigureAwait(false);
            }
        }

        // A table's column changed, renamed and removed.
        if (model.All<Table>().Where(t => t.Columns.Count >= 2).OrderByDescending(t => model.ReferencesTo(t.Id).Count).FirstOrDefault() is { } table)
        {
            var original = await EditAsync(table.Id, n => Column(n, 0)["nullable"] = !(Column(n, 0)["nullable"]?.GetValue<bool>() ?? false), ct).ConfigureAwait(false);
            await CheckAsync("column nullability changed", true, ct).ConfigureAwait(false);
            await EditAsync(table.Id, n => Column(n, 0)["name"] = "renamed_column", ct).ConfigureAwait(false);
            await CheckAsync("column renamed", true, ct).ConfigureAwait(false);
            await EditAsync(table.Id, n => n["columns"]!.AsArray().RemoveAt(n["columns"]!.AsArray().Count - 1), ct).ConfigureAwait(false);
            await CheckAsync("column removed", true, ct).ConfigureAwait(false);
            await WriteAsync(model.GetDocument(table.Id)!.Path, original, ct).ConfigureAwait(false);
            await CheckAsync("table restored", true, ct).ConfigureAwait(false);
        }

        // An enum member renamed.
        if (model.All<EnumType>().FirstOrDefault(e => e.Members.Count > 0) is { } enumType)
        {
            var original = await EditAsync(enumType.Id, n => n["members"]![0]!["name"] = "Renamed", ct).ConfigureAwait(false);
            await CheckAsync("enum member renamed", true, ct).ConfigureAwait(false);
            await WriteAsync(model.GetDocument(enumType.Id)!.Path, original, ct).ConfigureAwait(false);
            await CheckAsync("enum restored", true, ct).ConfigureAwait(false);
        }

        // A seed row removed.
        if (model.All<Seed>().FirstOrDefault(s => s.RowsFrom is null && s.Rows.Count > 0) is { } seed)
        {
            var original = await EditAsync(seed.Id, n => n["rows"]!.AsArray().RemoveAt(0), ct).ConfigureAwait(false);
            await CheckAsync("seed row removed", true, ct).ConfigureAwait(false);
            await WriteAsync(model.GetDocument(seed.Id)!.Path, original, ct).ConfigureAwait(false);
            await CheckAsync("seed restored", true, ct).ConfigureAwait(false);
        }

        // An entity created, then deleted.
        if (entities.FirstOrDefault() is { } sibling)
        {
            var folder = Path.GetDirectoryName(model.GetDocument(sibling.Id)!.Path)!.Replace('\\', '/');
            var path = folder + "/fresh-entity.json";
            var json = new JsonObject
            {
                ["kind"] = "entity",
                ["id"] = "01KZZZZZZZZZZZZZZZZZZZZZ01",
                ["name"] = "FreshEntity",
            };
            if (sibling.Package is { } package)
                json["package"] = package;
            json["attributes"] = new JsonArray(new JsonObject { ["id"] = "01KZZZZZZZZZZZZZZZZZZZZZ02", ["name"] = "id", ["type"] = "uuid", ["required"] = true });
            await WriteAsync(path, Encoding.UTF8.GetBytes(json.ToJsonString(Indented) + "\n"), ct).ConfigureAwait(false);
            await CheckAsync("entity created", true, ct).ConfigureAwait(false);
            await DeleteAsync(path, ct).ConfigureAwait(false);
            await CheckAsync("entity deleted", true, ct).ConfigureAwait(false);
        }

        // Edits that validate the whole model.
        var settings = ".maquettiste/maquettiste.json";
        if (File.Exists(FullPath(settings)))
        {
            var original = await File.ReadAllBytesAsync(FullPath(settings), ct).ConfigureAwait(false);
            var node = JsonNode.Parse(original)!.AsObject();
            node["name"] = (node["name"]?.GetValue<string>() ?? "model") + "-renamed";
            await WriteAsync(settings, Encoding.UTF8.GetBytes(node.ToJsonString(Indented) + "\n"), ct).ConfigureAwait(false);
            await CheckAsync("settings changed", false, ct).ConfigureAwait(false);
            await WriteAsync(settings, original, ct).ConfigureAwait(false);
            await CheckAsync("settings restored", false, ct).ConfigureAwait(false);
        }

        if (model.All<Package>().FirstOrDefault() is { } packageElement)
        {
            var original = await EditAsync(packageElement.Id, n => n["name"] = packageElement.Name + "Renamed", ct).ConfigureAwait(false);
            await CheckAsync("package renamed", false, ct).ConfigureAwait(false);
            await WriteAsync(model.GetDocument(packageElement.Id)!.Path, original, ct).ConfigureAwait(false);
            await CheckAsync("package restored", false, ct).ConfigureAwait(false);
        }

        if (entities.Count >= 2 && entities.FirstOrDefault(e => e.Base is null && !entities.Any(d => d.Base == e.Id)) is { } leaf
            && entities.FirstOrDefault(e => e.Id != leaf.Id && e.Base is null) is { } baseEntity)
        {
            var original = await EditAsync(leaf.Id, n => n["base"] = baseEntity.Id, ct).ConfigureAwait(false);
            await CheckAsync("entity given a base", false, ct).ConfigureAwait(false);
            await WriteAsync(model.GetDocument(leaf.Id)!.Path, original, ct).ConfigureAwait(false);
            await CheckAsync("entity base removed", false, ct).ConfigureAwait(false);
        }

        // Back where it started: the lineage resumes after a whole validation.
        if (subject is not null)
        {
            var original = await EditAsync(subject.Id, n => Attribute(n, 0)["name"] = "renamedAgain", ct).ConfigureAwait(false);
            await CheckAsync("attribute renamed after whole validations", true, ct).ConfigureAwait(false);
            await WriteAsync(model.GetDocument(subject.Id)!.Path, original, ct).ConfigureAwait(false);
            await CheckAsync("entity restored again", true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A sweep over the elements of the kinds an incremental validation takes, each edited and restored with a check after every step:
    /// renamed, given the name of another element of its kind, its first list of parts (attributes, columns, rows, members, ends,
    /// bindings, ...) without its first item, and deleted. Only the reports' equality is asserted; which edits validate incrementally is
    /// logged.
    /// </summary>
    /// <param name="perKind">The most elements of each kind edited.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunSweepAsync(int perKind, CancellationToken ct)
    {
        await CheckAsync("load", null, ct).ConfigureAwait(false);
        var model = Snapshot;
        var kinds = new[] { typeof(Entity), typeof(Relation), typeof(Mapping), typeof(Table), typeof(Seed), typeof(ReferenceType), typeof(EnumType), typeof(ValueObject) };
        foreach (var kind in kinds)
        {
            var documents = model.Documents.Where(d => d.Element.GetType() == kind).Take(perKind).ToList();
            foreach (var document in documents)
            {
                var id = document.Element.Id;
                var path = document.Path;
                var label = document.Element.KindName + " " + document.Element.Name;
                var original = await EditAsync(id, n => n["name"] = document.Element.Name + "X", ct).ConfigureAwait(false);
                await CheckAsync(label + " renamed", null, ct).ConfigureAwait(false);
                if (documents.FirstOrDefault(d => d.Element.Id != id) is { } other)
                {
                    await EditAsync(id, n => n["name"] = other.Element.Name, ct).ConfigureAwait(false);
                    await CheckAsync(label + " given another's name", null, ct).ConfigureAwait(false);
                }

                if (await PartsAsync(id, ct).ConfigureAwait(false) is { } parts)
                {
                    await EditAsync(id, n => n[parts]!.AsArray().RemoveAt(0), ct).ConfigureAwait(false);
                    await CheckAsync(label + " without its first " + parts + " item", null, ct).ConfigureAwait(false);
                }

                await RestoreAsync(id, path, original, ct).ConfigureAwait(false);
                await CheckAsync(label + " restored", null, ct).ConfigureAwait(false);
                await DeleteAsync(path, ct).ConfigureAwait(false);
                await CheckAsync(label + " deleted", null, ct).ConfigureAwait(false);
                await WriteAsync(path, original, ct).ConfigureAwait(false);
                await CheckAsync(label + " created again", null, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The first property of an element's file that is a list of objects with an id, or <see langword="null"/>.</summary>
    private async Task<string?> PartsAsync(string id, CancellationToken ct)
    {
        var node = JsonNode.Parse(await File.ReadAllBytesAsync(FullPath(Snapshot.GetDocument(id)!.Path), ct).ConfigureAwait(false))!.AsObject();
        foreach (var (name, value) in node)
        {
            if (value is JsonArray { Count: > 0 } list && list[0] is JsonObject first && first.ContainsKey("id"))
                return name;
        }

        return null;
    }

    private static JsonObject Attribute(JsonObject entity, Index index) => entity["attributes"]![index.GetOffset(entity["attributes"]!.AsArray().Count)]!.AsObject();

    private static JsonObject Column(JsonObject table, int index) => table["columns"]![index]!.AsObject();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await Store.DisposeAsync().ConfigureAwait(false);
}
