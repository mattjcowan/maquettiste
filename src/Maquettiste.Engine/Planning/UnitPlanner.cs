using System.Globalization;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// Stage 4: expands packs into render units (W6; engine-design.md section 8): template × element for each unit's <c>for</c> scope
/// (<c>model</c>, <c>each package|entity|relation|enum|value object|table</c>, or <c>select &lt;name&gt;</c> through the sandbox),
/// minus elements whose <c>generation["*"|pack].skip</c> is set, filtered by <c>where</c> (<see cref="UnitFilter"/>). Units are
/// ordered by pack order, then key ordinal; a key produced twice (a selector returning an id twice) is planned once.
/// </summary>
/// <param name="options">The engine options.</param>
internal sealed class UnitPlanner(EngineOptions options) : IUnitPlanner
{
    private const string ScriptError = "MQ6016";
    private const string UnknownId = "MQ6017";

    /// <inheritdoc/>
    public Task<UnitPlan> PlanAsync(ResolvedModel model, PackSet packs, IScriptSandboxFactory scripts, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Plan(model, packs, scripts, progress, ct), ct);
    }

    /// <summary>Computes a unit's static hash (engine-design.md section 11).</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="unit">The pack unit.</param>
    /// <param name="formatters">The configured formatters.</param>
    /// <param name="key">The unit key.</param>
    /// <returns>The static hash.</returns>
    /// <remarks>
    /// <c>H("mq-unit-1", engine version, pack name, pack version, canonical unit JSON, scripts hash, canonical effective parameters,
    /// output base, formatter settings or "none", template hashes, unit key)</c>. The pack version and the hashes of the unit's
    /// template and companion template are additions to the section 11 list: templates see the version as <c>pack.version</c> with no
    /// dependency key, and the renderer records <c>t:</c> keys for what it loads, so folding the unit's own templates in here keeps a
    /// template edit visible whether or not the renderer records the top-level template.
    /// </remarks>
    public static string StaticHash(LoadedPack pack, PackUnit unit, IReadOnlyList<FormatterSettings> formatters, string key) =>
        new UnitHashPrefix(pack, unit, formatters).For(key);

    /// <summary>The unit key: <c>&lt;pack&gt;/&lt;unitId&gt;</c> for model scope, else <c>&lt;pack&gt;/&lt;unitId&gt;:&lt;elementId&gt;</c>.</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="unitId">The unit id.</param>
    /// <param name="elementId">The element id, or <see langword="null"/>.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(string pack, string unitId, string? elementId) =>
        elementId is null ? pack + "/" + unitId : pack + "/" + unitId + ":" + elementId;

    private UnitPlan Plan(ResolvedModel model, PackSet packs, IScriptSandboxFactory scripts, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var diagnostics = new List<Diagnostic>();
        var planned = new List<(string Key, LoadedPack Pack, PackUnit Unit, IResolvedObject? Element, UnitHashPrefix Hash)>();
        var filter = new UnitFilter(model);
        var formatters = model.Settings.Formatters;
        var totalUnits = packs.Packs.Sum(p => p.Manifest.Units.Count);
        var doneUnits = 0;
        foreach (var pack in packs.Packs.OrderBy(p => p.Order))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = pack.Parameters.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
            IScriptSandboxPool? pool = null;
            var poolFailed = false;
            IScriptSandboxPool? Pool()
            {
                if (pool is not null || poolFailed)
                    return pool;
                try
                {
                    pool = scripts.CreatePool(pack.Scripts, model.Settings.Limits, 1, ct);
                }
                catch (Exception ex) when (ScriptDiagnostic(ex) is { } diagnostic)
                {
                    diagnostics.Add(diagnostic);
                    poolFailed = true;
                }

                return pool;
            }

            try
            {
                var packFile = pack.RelativePath + "/pack.json";
                for (var index = 0; index < pack.Manifest.Units.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var unit = pack.Manifest.Units[index];
                    var pointer = "/units/" + index.ToString(CultureInfo.InvariantCulture);
                    var hash = new UnitHashPrefix(pack, unit, formatters);
                    foreach (var element in Candidates(model, pack, unit, parameters, Pool, packFile, pointer, diagnostics, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (element is RElement conceptual && SkippedByHints(conceptual.Generation, pack.Name))
                            continue;
                        if (element is RTable table && filter.FileOf(table) is { } tableFile && SkippedByHints(tableFile.Generation, pack.Name))
                            continue;
                        var key = KeyOf(pack.Name, unit.Id, element?.Id);
                        if (!filter.Matches(unit.Where, element, key, pack, parameters, Pool, diagnostics, ct))
                            continue;
                        planned.Add((key, pack, unit, element, hash));
                    }

                    progress?.Report(new ProgressUpdate(PipelineStage.Plan, ++doneUnits, totalUnits, packFile, pack.Name));
                }
            }
            finally
            {
                pool?.Dispose();
            }
        }

        // Pack order, then key ordinal, stable (the index breaks ties); a key planned twice keeps its first unit. Static hashes (one
        // SHA-256 per unit) are computed for the kept units only, in parallel, into their final places.
        var order = new int[planned.Count];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var c = planned[a].Pack.Order.CompareTo(planned[b].Pack.Order);
            if (c == 0)
                c = string.CompareOrdinal(planned[a].Key, planned[b].Key);
            return c != 0 ? c : a.CompareTo(b);
        });
        var kept = new List<int>(order.Length);
        for (var i = 0; i < order.Length; i++)
        {
            if (i == 0 || planned[order[i]].Pack.Order != planned[order[i - 1]].Pack.Order
                || !string.Equals(planned[order[i]].Key, planned[order[i - 1]].Key, StringComparison.Ordinal))
                kept.Add(order[i]);
        }

        var units = new PlannedUnit[kept.Count];
        Parallel.For(0, kept.Count, new ParallelOptions { MaxDegreeOfParallelism = options.EffectiveParallelism, CancellationToken = ct }, i =>
        {
            var (key, pack, unit, element, hash) = planned[kept[i]];
            units[i] = new PlannedUnit(key, pack, unit, element, hash.For(key));
        });
        return new UnitPlan(units, PackLoader.Sort(diagnostics));
    }

    /// <summary>Whether an element's generation hints drop its units for a pack: <c>generation["*"].skip</c> or <c>generation[pack].skip</c>.</summary>
    /// <param name="generation">The element's (or table file's) generation hints.</param>
    /// <param name="pack">The pack name.</param>
    /// <returns><see langword="true"/> to drop.</returns>
    internal static bool SkippedByHints(IReadOnlyDictionary<string, GenerationHints> generation, string pack) =>
        (generation.TryGetValue("*", out var all) && all.Skip) || (generation.TryGetValue(pack, out var own) && own.Skip);

    /// <summary>Maps a sandbox exception to its diagnostic.</summary>
    /// <param name="ex">The exception.</param>
    /// <returns>The diagnostic, or <see langword="null"/> for other exceptions.</returns>
    internal static Diagnostic? ScriptDiagnostic(Exception ex) => ex switch
    {
        ScriptErrorException error => error.Diagnostic,
        ScriptLimitException limit => limit.Diagnostic,
        _ => null,
    };

    private static IEnumerable<IResolvedObject?> Candidates(ResolvedModel model, LoadedPack pack, PackUnit unit,
        IReadOnlyDictionary<string, object?> parameters, Func<IScriptSandboxPool?> pool, string packFile, string pointer,
        List<Diagnostic> diagnostics, CancellationToken ct)
    {
        switch (unit.For)
        {
            case "model": return [null];
            case "each package": return model.Packages;
            case "each entity": return model.Entities;
            case "each relation": return model.Relations;
            case "each enum": return model.Enums;
            case "each value object": return model.ValueObjects;
            case "each table": return model.Databases.SelectMany(d => d.Tables);
            case "each reference type": return model.ReferenceTypes;
            case "each seed": return model.Seeds;
            case "each locale": return model.Locales;
        }

        if (!unit.For.StartsWith("select ", StringComparison.Ordinal))
            return [];
        var selector = unit.For["select ".Length..].Trim();
        var sandboxes = pool();
        if (sandboxes is null)
            return [];
        IReadOnlyList<string> ids;
        try
        {
            using var lease = sandboxes.Rent();
            ids = lease.Sandbox.Select(selector, model, new ScriptCallContext(null, KeyOf(pack.Name, unit.Id, null), parameters, ct));
        }
        catch (Exception ex) when (ScriptDiagnostic(ex) is { } diagnostic)
        {
            diagnostics.Add(diagnostic);
            return [];
        }

        var elements = new List<IResolvedObject?>(ids.Count);
        foreach (var id in ids)
        {
            if (model.Find(id) is { } element)
            {
                elements.Add(element);
                continue;
            }

            diagnostics.Add(new Diagnostic(UnknownId, DiagnosticSeverity.Error,
                $"Selector '{selector}' of unit '{unit.Id}' in pack '{pack.Name}' returned '{id}', which is not an element of the resolved model.",
                null, packFile, pointer + "/for", null, null));
        }

        return elements;
    }

    /// <summary>The static-hash fields shared by every key of one pack unit.</summary>
    private sealed class UnitHashPrefix
    {
        private readonly string _pack;
        private readonly string _version;
        private readonly string _unitJson;
        private readonly string _scriptsHash;
        private readonly string _parameters;
        private readonly string _output;
        private readonly string _formatter;
        private readonly string _templates;
        private byte[]? _prefix;

        public UnitHashPrefix(LoadedPack pack, PackUnit unit, IReadOnlyList<FormatterSettings> formatters)
        {
            _pack = pack.Name;
            _version = pack.Manifest.Version;
            _unitJson = CanonicalForm.Json(unit);
            _scriptsHash = pack.ScriptsHash;
            _parameters = CanonicalForm.Json(pack.Parameters);
            _output = pack.Settings.Output;
            _formatter = unit.Formatter switch
            {
                "none" => "none",
                // By extension: which formatter applies depends on paths only known after rendering, so every configured one counts.
                null => formatters.Count == 0 ? "none" : CanonicalForm.Json(formatters),
                var name => formatters.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal)) is { } f
                    ? CanonicalForm.Json(f)
                    : "unknown:" + name,
            };
            _templates = HashBuilder.Of(
                unit.Template, PackFiles.Hash(pack.RootPath, unit.Template) ?? DependencyHasher.Absent,
                unit.Companion?.Template, unit.Companion is { } c ? PackFiles.Hash(pack.RootPath, c.Template) ?? DependencyHasher.Absent : null);
        }

        /// <summary>
        /// <c>H</c> over the shared fields and the key. The shared fields' length-prefixed bytes are built once per pack unit and
        /// only the key is appended per element: the bytes hashed, and so the hash, are exactly those of <see cref="HashBuilder.Of"/>.
        /// </summary>
        /// <remarks>Thread-safe: the prefix is built once (a race builds equal bytes).</remarks>
        public string For(string key)
        {
            var prefix = Volatile.Read(ref _prefix);
            if (prefix is null)
                Volatile.Write(ref _prefix, prefix = Prefix());
            return ForPrefix(prefix, key);
        }

        private static string ForPrefix(byte[] prefix, string key)
        {
            var keyBytes = Encoding.UTF8.GetByteCount(key);
            var length = prefix.Length + sizeof(ulong) + keyBytes;
            var rented = length <= 1024 ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            var buffer = rented is null ? stackalloc byte[length] : rented.AsSpan(0, length);
            prefix.CopyTo(buffer);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[prefix.Length..], (ulong)keyBytes);
            Encoding.UTF8.GetBytes(key, buffer[(prefix.Length + sizeof(ulong))..]);
            Span<byte> digest = stackalloc byte[System.Security.Cryptography.SHA256.HashSizeInBytes];
            System.Security.Cryptography.SHA256.HashData(buffer, digest);
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            return Convert.ToHexStringLower(digest);
        }

        private byte[] Prefix()
        {
            string?[] fields = ["mq-unit-1", EngineVersion.Value, _pack, _version, _unitJson, _scriptsHash, _parameters, _output, _formatter, _templates];
            var bytes = new List<byte>();
            Span<byte> prefix = stackalloc byte[sizeof(ulong)];
            foreach (var field in fields)
            {
                // As HashBuilder.Add: the UTF-8 byte count then the bytes; a null field is the count ulong.MaxValue alone.
                var encoded = field is null ? [] : Encoding.UTF8.GetBytes(field);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(prefix, field is null ? ulong.MaxValue : (ulong)encoded.Length);
                bytes.AddRange(prefix);
                bytes.AddRange(encoded);
            }

            return [.. bytes];
        }
    }
}

/// <summary>
/// The <c>where</c> filters of a unit (engine-design.md section 2.5): every set filter must match; lists match any value. Tags,
/// stereotypes, categories and packages are those of the element; a table uses its own file (designed, imported, or the overlay of a
/// synthesized table) together with the entity or relation it comes from. Categories and packages match by id or name (qualified
/// name for packages) and include descendants. <c>database</c>: a table in that database; an entity or relation mapped (not ignored)
/// there; any other element when the database exists. <c>abstract</c> matches entities by <see cref="REntity.IsAbstract"/> and other
/// elements as not abstract. <c>script</c> calls a JavaScript filter.
/// </summary>
internal sealed class UnitFilter
{
    private readonly ResolvedModel _model;
    private readonly Dictionary<string, Category> _categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Table> _tableFiles = new(StringComparer.Ordinal);

    /// <summary>Creates the filter context over a resolved model.</summary>
    /// <param name="model">The resolved model.</param>
    public UnitFilter(ResolvedModel model)
    {
        _model = model;
        foreach (var category in model.Source.CategoryTrees.SelectMany(t => t.Categories))
            _categories.TryAdd(category.Id, category);
        foreach (var table in model.Source.All<Table>())
        {
            var key = table.Origin == TableOrigin.Synthesized ? SynthesizedKey(table) : table.Id;
            if (key is not null)
                _tableFiles.TryAdd(key, table);
        }
    }

    /// <summary>
    /// The file of a table: the designed or imported table, or the overlay of a synthesized one (found by its section 7.3 key);
    /// <see langword="null"/> for a synthesized table without an overlay.
    /// </summary>
    /// <param name="table">The resolved table.</param>
    /// <returns>The file, or <see langword="null"/>.</returns>
    public Table? FileOf(RTable table) => _tableFiles.GetValueOrDefault(table.Key);

    /// <summary>Whether an element passes a unit's filters.</summary>
    /// <param name="where">The filters.</param>
    /// <param name="element">The element, or <see langword="null"/> for model scope.</param>
    /// <param name="key">The unit key (script seed).</param>
    /// <param name="pack">The pack.</param>
    /// <param name="parameters">The pack parameters for scripts.</param>
    /// <param name="pool">The pack's sandbox pool, created on first use.</param>
    /// <param name="diagnostics">Where script errors go.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="true"/> when the element passes.</returns>
    public bool Matches(UnitWhere? where, IResolvedObject? element, string key, LoadedPack pack, IReadOnlyDictionary<string, object?> parameters,
        Func<IScriptSandboxPool?> pool, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        if (where is null)
            return true;
        if (where.Database is { } database && !InDatabase(element, database))
            return false;
        if (element is null)
            return true;

        var tags = Tags(element);
        if (where.Tags.Count > 0 && !where.Tags.Any(tags.Contains))
            return false;
        if (where.NotTags.Any(tags.Contains))
            return false;
        var stereotypes = Stereotypes(element);
        if (where.Stereotypes.Count > 0 && !where.Stereotypes.Any(stereotypes.Contains))
            return false;
        if (where.NotStereotypes.Any(stereotypes.Contains))
            return false;
        if (where.Categories.Count > 0 && !CategoryMatches(element, where.Categories))
            return false;
        if (where.Packages.Count > 0 && !PackageMatches(element, where.Packages))
            return false;
        if (where.NotPackages.Count > 0 && PackageMatches(element, where.NotPackages))
            return false;
        if (where.Abstract is { } isAbstract && (element is REntity { IsAbstract: true }) != isAbstract)
            return false;
        if (where.Script is { } script)
        {
            var sandboxes = pool();
            if (sandboxes is null)
                return false;
            try
            {
                using var lease = sandboxes.Rent();
                return lease.Sandbox.Filter(script, element, _model, new ScriptCallContext(null, key, parameters, ct));
            }
            catch (Exception ex) when (UnitPlanner.ScriptDiagnostic(ex) is { } diagnostic)
            {
                diagnostics.Add(diagnostic);
                return false;
            }
        }

        return true;
    }

    private static string? SynthesizedKey(Table table)
    {
        if (table.Entity is { } entity)
            return table.Attribute is { } attribute ? entity + "." + attribute + "@" + table.Database : entity + "@" + table.Database;
        if (table.Relation is { } relation)
            return relation + "@" + table.Database;
        return null;
    }

    private bool InDatabase(IResolvedObject? element, string database) => element switch
    {
        RTable table => string.Equals(table.Database.Name, database, StringComparison.Ordinal),
        REntity entity => entity.Mappings.ContainsKey(database),
        RRelation relation => relation.Mappings.ContainsKey(database),
        _ => _model.Databases.Any(d => string.Equals(d.Name, database, StringComparison.Ordinal)),
    };

    private static RElement? SourceOf(IResolvedObject element) => element switch
    {
        RElement conceptual => conceptual,
        RTable table => (RElement?)table.Entity ?? table.Relation,
        _ => null,
    };

    private HashSet<string> Tags(IResolvedObject element)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (SourceOf(element) is { } source)
            set.UnionWith(source.Tags);
        if (element is RTable table && _tableFiles.TryGetValue(table.Key, out var file))
            set.UnionWith(file.Tags);
        return set;
    }

    private HashSet<string> Stereotypes(IResolvedObject element)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (SourceOf(element) is { } source)
            set.UnionWith(source.Stereotypes.Select(s => s.Key));
        if (element is RTable table && _tableFiles.TryGetValue(table.Key, out var file))
            set.UnionWith(file.Stereotypes);
        return set;
    }

    private bool CategoryMatches(IResolvedObject element, IReadOnlyList<string> filters)
    {
        var ids = new List<string>();
        if (SourceOf(element)?.Category?.Id is { } own)
            ids.Add(own);
        if (element is RTable table && _tableFiles.TryGetValue(table.Key, out var file) && file.Category is { } fileCategory)
            ids.Add(fileCategory);
        foreach (var start in ids)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var id = start; id is not null && seen.Add(id);)
            {
                _categories.TryGetValue(id, out var category);
                if (filters.Contains(id, StringComparer.Ordinal) || (category is not null && filters.Contains(category.Name, StringComparer.Ordinal)))
                    return true;
                id = category?.Parent;
            }
        }

        return false;
    }

    private static bool PackageMatches(IResolvedObject element, IReadOnlyList<string> filters)
    {
        var package = element as RPackage ?? SourceOf(element)?.Package;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (; package is not null && seen.Add(package.Id); package = package.Parent)
        {
            if (filters.Contains(package.Id, StringComparer.Ordinal) || filters.Contains(package.QualifiedName, StringComparer.Ordinal))
                return true;
        }

        return false;
    }
}
