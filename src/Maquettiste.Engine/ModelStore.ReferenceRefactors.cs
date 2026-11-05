using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>
/// The reference-type refactors (next-phase-2-design.md section 3.5, "The four operations"): <c>rename-code</c>, <c>retire-code</c>,
/// <c>merge-reference-types</c> and <c>rename-reference-type</c>. Each expands into updates and deletes as <c>retag</c> does, reading the
/// model as it was before the batch and finding every use through the index, never a resolve. A refactor keeps the old code as a former
/// code of the type, so imports, validation and generated code still understand it; its value map (the database step) is recorded by
/// the write. The usage each one rewrites goes on the item of the type it names.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>The most uses a refactor's usage lists (<see cref="RefactorUsage.Uses"/>).</summary>
    private const int RefactorUsageLimit = 200;

    /// <summary>A conceptual name (<c>common.json#/$defs/identifier</c>).</summary>
    private static readonly Regex ElementName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>Whether an operation is a reference-type refactor.</summary>
    private static bool IsReferenceRefactor(BatchOp op) =>
        op is BatchOp.RenameCode or BatchOp.RetireCode or BatchOp.MergeReferenceTypes or BatchOp.RenameReferenceType;

    /// <summary>
    /// One refactor of a batch as its expansion leaves it: the item its usage goes on, and the parts of its code map, which the write's
    /// recording turns into one value map per database that holds the type (section 3.5, "Recording the value map").
    /// </summary>
    /// <param name="Operation">The operation's index in the batch.</param>
    /// <param name="ItemId">The id of the item that carries the usage: the type the operation names (for a merge, the merged-away one).</param>
    /// <param name="TypeId">The type that holds the codes after the refactor (for a merge, the target).</param>
    /// <param name="FromTypeId">A merge's merged-away type; <see langword="null"/> otherwise.</param>
    /// <param name="FromNames">The names before: the type's (a merge's merged-away type's) name and plural name.</param>
    /// <param name="ToNames">The names after.</param>
    /// <param name="Values">The codes mapped away, sorted by <c>from</c>, never an identity entry.</param>
    /// <param name="Names">Whether the refactor renames the type, which reaches every database.</param>
    /// <param name="Usage">What the refactor rewrites.</param>
    internal sealed record ReferenceRefactor(int Operation, string ItemId, string TypeId, string? FromTypeId, MigrationState FromNames,
        MigrationState ToNames, IReadOnlyList<MigrationValue> Values, bool Names, RefactorUsage Usage)
    {
        /// <summary>The code map the write's recording turns into value-map facts (the handoff, as a materialize hands its key maps).</summary>
        public CodeMap CodeMap => new(TypeId, FromTypeId, FromNames, ToNames, Values, Names);
    }

    /// <summary>
    /// Expands the batch's refactors in operation order. A refused one sets its slot of <paramref name="invalid"/> (MQ7020 at the
    /// operation, with the reason); when none is refused, their updates (reference types first, so a seed planned after its renamed type
    /// follows it into its folder) and deletes join <paramref name="changes"/>.
    /// </summary>
    /// <returns>The refactors that apply; empty when the batch holds none or one is refused.</returns>
    private List<ReferenceRefactor> ExpandReferenceRefactors(ModelSnapshot snapshot, ModelBatch batch, SaveResult?[] invalid, List<PlannedChange> changes)
    {
        var work = new RefactorWork(snapshot, Paths, new Lazy<PackParameterRefs>(() => PackParameterRefs.Read(Paths, snapshot.Settings)));
        var refused = false;
        for (var i = 0; i < batch.Operations.Count; i++)
        {
            var o = batch.Operations[i];
            if (!IsReferenceRefactor(o.Op) || invalid[i] is not null)
                continue;
            if (work.Apply(i, o) is not { } refusal)
                continue;
            var pointer = "/operations/" + i.ToString(CultureInfo.InvariantCulture);
            invalid[i] = new SaveResult(SaveOutcome.Invalid, o.Id, null, null, [RuleCatalog.Create("MQ7020", refusal, o.Id, null, pointer)], [], null);
            refused = true;
        }

        if (refused || work.Results.Count == 0)
            return [];

        // The lifecycle checks see every refactor of the batch at once, so renaming a code two bound types share in both passes.
        foreach (var (operation, refusal) in work.LifecycleRefusals())
        {
            if (invalid[operation] is not null)
                continue;
            var o = batch.Operations[operation];
            invalid[operation] = new SaveResult(SaveOutcome.Invalid, o.Id, null, null,
                [RuleCatalog.Create("MQ7020", refusal, o.Id, null, "/operations/" + operation.ToString(CultureInfo.InvariantCulture))], [], null);
            refused = true;
        }

        if (refused)
            return [];
        work.Finish();
        foreach (var (id, expected, node, operation) in work.Updates())
        {
            if (!TryParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()), id, out var parsed, out var failure))
            {
                var o = batch.Operations[operation];
                var reason = failure.Diagnostics.FirstOrDefault()?.Message ?? "it is not valid";
                invalid[operation] = new SaveResult(SaveOutcome.Invalid, o.Id, null, null,
                    [RuleCatalog.Create("MQ7020", $"The rewritten element {id} does not read back: {reason}", o.Id, null, "/operations/" + operation.ToString(CultureInfo.InvariantCulture))],
                    [], null);
                return [];
            }

            changes.Add(new PlannedChange(BatchOp.Update, id, expected, parsed, DeleteResolution.Refuse));
        }

        foreach (var (id, expected) in work.Deletes())
            changes.Add(new PlannedChange(BatchOp.Delete, id, expected, null, DeleteResolution.Refuse));
        return work.Results;
    }

    /// <summary>A batch result whose items carry the refactors' usage, each on the item of the type its operation names.</summary>
    private static BatchResult WithUsage(BatchResult result, IReadOnlyList<ReferenceRefactor> refactors)
    {
        if (refactors.Count == 0 || result.Outcome != SaveOutcome.Saved)
            return result;
        var usage = new Dictionary<string, RefactorUsage>(StringComparer.Ordinal);
        foreach (var refactor in refactors)
            usage.TryAdd(refactor.ItemId, refactor.Usage);
        return result with
        {
            Items = [.. result.Items.Select(item => item.Id is { } id && usage.Remove(id, out var found) ? item with { Usage = found } : item)],
        };
    }

    /// <summary>
    /// The documents a batch's refactors change, as working copies. Every check reads the snapshot (the model before the batch); value
    /// rewrites land on the copies at the pointers the index gives, and the structural edits (rows removed or moved, former codes added,
    /// columns remapped) wait in <see cref="_structural"/> until every refactor has written its values, so no pointer moves under another.
    /// </summary>
    /// <param name="snapshot">The model before the batch.</param>
    /// <param name="paths">The project's model paths, to tell which seeds sit in their type's seed folder.</param>
    /// <param name="packs">The x-ref values and scopes of the project's pack parameters, read only when a refactor removes something.</param>
    private sealed class RefactorWork(ModelSnapshot snapshot, ModelPaths paths, Lazy<PackParameterRefs> packs)
    {
        private readonly ReferenceDataIndex _data = snapshot.Index.ReferenceData ?? ReferenceDataIndex.Build(snapshot.Documents.Select(d => d.Element));
        private readonly Dictionary<string, Working> _nodes = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private readonly List<(string Id, string Expected)> _deletes = [];
        private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _refactored = new(StringComparer.Ordinal);
        private readonly List<Action> _structural = [];

        // What the batch does to each type's live codes, and the lifecycle state codes it rewrites (lifecycle-design.md decisions 16 and 18),
        // which LifecycleRefusals judges once every refactor has planned.
        private readonly Dictionary<string, HashSet<string>> _gone = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _came = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _mergedInto = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Process, string Pointer), int> _codeWrites = [];
        private readonly List<(string Process, string Pointer)> _codeWriteOrder = [];

        /// <summary>A document's working copy: the expected hash its write checks, its node, and the first operation that touched it.</summary>
        private sealed record Working(string Expected, JsonObject Node, int Operation, bool IsType);

        /// <summary>The refactors that applied, in operation order.</summary>
        public List<ReferenceRefactor> Results { get; } = [];

        /// <summary>Applies one refactor; returns why it is refused, or <see langword="null"/>.</summary>
        public string? Apply(int index, BatchOperation o)
        {
            if (o.Id is null || snapshot.Get<ReferenceType>(o.Id) is not { } type)
                return $"'{o.Id}' is not a reference type.";
            if (Taken(type) is { } second)
                return second;
            return o.Op switch
            {
                BatchOp.RenameCode => RenameCode(index, o, type),
                BatchOp.RetireCode => RetireCode(index, o, type),
                BatchOp.MergeReferenceTypes => Merge(index, o, type),
                _ => RenameType(index, o, type),
            };
        }

        /// <summary>
        /// The refusals the batch's refactors earn together, once each has planned (lifecycle-design.md decisions 16 and 18), each with the
        /// first operation that rewrote the code: a rewritten state code that another reference type its lifecycle binds held before and
        /// does not hold after the batch (a code two bound types share is renamed in both in one batch), and two states of one lifecycle
        /// left with one code. Empty when the batch rewrites no state code.
        /// </summary>
        public IEnumerable<(int Operation, string Refusal)> LifecycleRefusals()
        {
            foreach (var group in _codeWriteOrder.GroupBy(w => w.Process, StringComparer.Ordinal))
            {
                var process = snapshot.Get<Process>(group.Key)!;
                var node = _nodes[group.Key].Node;
                var document = snapshot.GetDocument(group.Key)!.Json;
                var types = new List<(string Type, string Attribute)>();
                foreach (var entry in process.Subjects)
                {
                    if (entry.BoundAttribute is { } bound && _data.AttributeTypes.TryGetValue(bound, out var typeId) && !types.Any(t => t.Type == typeId))
                        types.Add((typeId, bound));
                }

                foreach (var (_, pointer) in group)
                {
                    var index = _codeWrites[(group.Key, pointer)];
                    if (!JsonPointer.TryGet(document, pointer, out var before) || ReferenceDataIndex.CodeKey(before) is not { } oldKey
                        || CodeAt(node, pointer) is not { } newKey)
                    {
                        continue;
                    }

                    foreach (var (typeId, _) in types)
                    {
                        if (!IsLive(typeId, oldKey) || LiveAfter(typeId, newKey))
                            continue;
                        var name = snapshot.Get<ReferenceType>(typeId)?.Name ?? typeId;
                        yield return (index, $"Process '{process.Name}' binds reference type '{name}' too, which holds code '{oldKey}' and not '{newKey}', so the state whose code this rewrites would hold no code of '{name}'; rename the code in '{name}' in this batch as well.");
                        break;
                    }
                }

                // Two states of one code: compare the clashes after the batch with those before, so a model that had one is not blamed for it.
                var clashesBefore = Clashes(JsonNode.Parse(document.GetRawText()) as JsonObject);
                foreach (var (key, states) in Clashes(node))
                {
                    if (clashesBefore.ContainsKey(key))
                        continue;
                    var index = group.Min(w => _codeWrites[(group.Key, w.Pointer)]);
                    yield return (index, $"States {string.Join(" and ", states.Select(s => "'" + s + "'"))} of process '{process.Name}' would share code '{key}' after this batch; give one of them another code first, or refactor the codes another way.");
                    break;
                }
            }
        }

        /// <summary>The code a working copy holds at a pointer, as its key.</summary>
        private static string? CodeAt(JsonObject node, string pointer)
        {
            if (!JsonPointer.TryGetParent(node, pointer, out var parent, out var last))
                return null;
            var value = parent is JsonObject obj ? obj[last] : parent is JsonArray array && JsonPointer.TryIndex(last, out var at) && at < array.Count ? array[at] : null;
            return value is null ? null : ReferenceDataIndex.CodeKey(JsonSerializer.SerializeToElement(value));
        }

        /// <summary>The codes two or more states of a process document share, with the states' names, in document order; history and choice states left out.</summary>
        private static Dictionary<string, List<string>> Clashes(JsonObject? process)
        {
            var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();
            void Walk(JsonNode? states)
            {
                foreach (var state in (states as JsonArray ?? []).OfType<JsonObject>())
                {
                    if ((string?)state["type"] is not ("history" or "choice") && state["code"] is { } code
                        && ReferenceDataIndex.CodeKey(JsonSerializer.SerializeToElement(code)) is { } key)
                    {
                        if (!byKey.TryGetValue(key, out var names))
                        {
                            byKey[key] = names = [];
                            order.Add(key);
                        }

                        names.Add((string?)state["name"] ?? "");
                    }

                    Walk(state["states"]);
                }
            }

            Walk(process?["states"]);
            var clashes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var key in order)
            {
                if (byKey[key].Count > 1)
                    clashes[key] = byKey[key];
            }

            return clashes;
        }

        /// <summary>Whether a code is a live code of the type once the batch's refactors apply; a merged-away type's codes are its target's.</summary>
        private bool LiveAfter(string typeId, string code)
        {
            if (_mergedInto.TryGetValue(typeId, out var into))
                return LiveAfter(into, code);
            if (_came.TryGetValue(typeId, out var came) && came.Contains(code, StringComparer.Ordinal))
                return true;
            return !(_gone.TryGetValue(typeId, out var gone) && gone.Contains(code)) && IsLive(typeId, code);
        }

        private HashSet<string> Gone(string typeId) => _gone.TryGetValue(typeId, out var set) ? set : _gone[typeId] = new HashSet<string>(StringComparer.Ordinal);

        private List<string> Came(string typeId) => _came.TryGetValue(typeId, out var list) ? list : _came[typeId] = [];

        /// <summary>Runs the structural edits, once every refactor has written its values.</summary>
        public void Finish()
        {
            foreach (var edit in _structural)
                edit();
            _structural.Clear();
        }

        /// <summary>The updates: reference types first, then the other documents, each in the order the refactors first touched them.</summary>
        public IEnumerable<(string Id, string Expected, JsonObject Node, int Operation)> Updates() =>
            _order.Where(id => !_deleted.Contains(id)).OrderBy(id => _nodes[id].IsType ? 0 : 1)
                .Select(id => (id, _nodes[id].Expected, _nodes[id].Node, _nodes[id].Operation));

        /// <summary>The deletes, in the order the refactors asked for them.</summary>
        public IEnumerable<(string Id, string Expected)> Deletes() => _deletes;

        // ---- the four operations -------------------------------------------------------------------------------------------------

        private string? RenameCode(int index, BatchOperation o, ReferenceType type)
        {
            if (string.IsNullOrEmpty(o.Code) || string.IsNullOrEmpty(o.To))
                return "rename-code needs the code to rename (code) and its new text (to).";
            var (code, to) = (o.Code, o.To);
            if (!IsLive(type.Id, code))
                return Unknown(type, code);
            if (string.Equals(code, to, StringComparison.Ordinal))
                return $"The code is already '{code}'.";
            if (IsLive(type.Id, to))
                return $"'{to}' is already a code of reference type '{type.Name}'.";
            if (IsFormer(type.Id, to))
                return $"'{to}' is a former code of reference type '{type.Name}', and a code never comes back: once no database stores it, remove its entry and the value maps that map it away, then rename.";
            if (ReferenceDataRules.FormerCodeValue(type, to, out var problem) is not { } toValue)
                return $"'{to}' does not fit the code of reference type '{type.Name}': {problem}.";

            Mark(type, index);
            Node(type.Id, index, o);
            var rows = RowsWith(type.Id, code);
            var rowId = _data.Codes[type.Id][code];
            var uses = new List<ReferenceInfo>();
            foreach (var (seed, pointer, _) in rows)
                Set(seed, pointer, toValue, index, o);
            foreach (var use in CodeUses(type.Id, code, [.. rows.Select(r => r.RowId)]))
            {
                Set(use.FromElementId, use.JsonPointer, toValue, index, o);
                uses.Add(use);
            }

            _structural.Add(() => AddFormerCodes(type.Id, [(code, rowId)]));
            Gone(type.Id).Add(code);
            Came(type.Id).Add(ReferenceDataIndex.CodeKey(toValue) ?? to);
            Results.Add(new ReferenceRefactor(index, type.Id, type.Id, null, Names(type), Names(type), [new MigrationValue { From = code, To = to }], false,
                Usage(uses, 0, 0, 0)));
            return null;
        }

        private string? RetireCode(int index, BatchOperation o, ReferenceType type)
        {
            if (string.IsNullOrEmpty(o.Code) || string.IsNullOrEmpty(o.To))
                return "retire-code needs the code to retire (code) and the live code that replaces it (to).";
            var (code, to) = (o.Code, o.To);
            if (!IsLive(type.Id, code))
                return Unknown(type, code);
            if (!IsLive(type.Id, to))
                return Unknown(type, to);
            if (string.Equals(code, to, StringComparison.Ordinal))
                return $"A code cannot be retired into itself ('{code}').";

            var rows = RowsWith(type.Id, code);
            var removed = rows.Select(r => r.RowId).ToHashSet(StringComparer.Ordinal);
            var target = _data.Codes[type.Id][to];
            if (Outside(removed, $"the row of '{code}'") is { } outside)
                return outside;
            if ((CellOf(type.Id, target) ?? ReferenceDataRules.FormerCodeValue(type, to, out _)) is not { } toValue)
                return $"The row of '{to}' holds no code that can be written.";

            Mark(type, index);
            Node(type.Id, index, o);
            var uses = new List<ReferenceInfo>();
            foreach (var use in CodeUses(type.Id, code, removed))
            {
                Set(use.FromElementId, use.JsonPointer, toValue, index, o);
                uses.Add(use);
            }

            foreach (var use in IdUses(removed))
            {
                Set(use.FromElementId, use.JsonPointer, JsonSerializer.SerializeToElement(target), index, o);
                uses.Add(use);
            }

            foreach (var seed in rows.Select(r => r.SeedId).Distinct(StringComparer.Ordinal))
            {
                Node(seed, index, o);
                _structural.Add(() => RemoveRows(seed, removed));
            }

            _structural.Add(() => AddFormerCodes(type.Id, [(code, target)]));
            Gone(type.Id).Add(code);
            Results.Add(new ReferenceRefactor(index, type.Id, type.Id, null, Names(type), Names(type), [new MigrationValue { From = code, To = to }], false,
                Usage(uses, 0, removed.Count, Translations(removed))));
            return null;
        }

        private string? Merge(int index, BatchOperation o, ReferenceType from)
        {
            if (string.IsNullOrEmpty(o.Target))
                return "merge-reference-types needs the reference type to merge into (target).";
            if (string.Equals(o.Target, from.Id, StringComparison.Ordinal))
                return $"Reference type '{from.Name}' cannot merge into itself.";
            if (snapshot.Get<ReferenceType>(o.Target) is not { } into)
                return $"'{o.Target}' is not a reference type.";
            if (Taken(into) is { } second)
                return second;
            if (!string.Equals(from.Code.Type, into.Code.Type, StringComparison.Ordinal))
                return $"The codes of '{from.Name}' are {from.Code.Type} and those of '{into.Name}' are {into.Code.Type}; merge types whose codes have one type.";
            if (from.Bindings.Count > 0)
                return $"Reference type '{from.Name}' keeps its rows in a bound table; a merge moves rows between seeds only. Remove the binding first.";
            var fromSeeds = SeedsOf(from.Id);
            if (fromSeeds.FirstOrDefault(s => s.RowsFrom?.Script is not null) is { } scripted)
                return $"Seed '{scripted.Name}' of '{from.Name}' computes its rows with a script, which a merge does not move.";

            // The code map: codes keys are live codes of A that B does not hold, values live codes of B.
            var mapped = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in o.Codes ?? new Dictionary<string, string>())
            {
                if (!IsLive(from.Id, key))
                    return $"'{key}' (a key of codes) is not a code of a written row of '{from.Name}'.";
                if (IsLive(into.Id, key))
                    return $"'{key}' is a code of '{into.Name}' too, and maps to it by itself; leave it out of codes.";
                if (IsFormer(into.Id, key))
                    return $"'{key}' is a former code of '{into.Name}', and a code never comes back.";
                if (!IsLive(into.Id, value))
                    return $"'{value}' (the code '{key}' maps to) is not a code of a written row of '{into.Name}'.";
                mapped[key] = value;
            }

            // Every written row of A: removed (its code maps, by itself or through codes) or moving into B.
            var removed = new Dictionary<string, string>(StringComparer.Ordinal);   // A row id -> the B row that takes its place
            var moving = new HashSet<string>(StringComparer.Ordinal);
            var movingCodes = new List<string>();
            foreach (var seed in fromSeeds)
            {
                var at = IndexOf(seed.Columns, "code");
                foreach (var row in seed.Rows)
                {
                    var key = at >= 0 && at < row.Values.Count ? ReferenceDataIndex.CodeKey(row.Values[at]) : null;
                    if (key is not null && (mapped.TryGetValue(key, out var target) || IsLive(into.Id, target = key)))
                    {
                        removed[row.Id] = _data.Codes[into.Id][target];
                        continue;
                    }

                    if (key is not null && IsFormer(into.Id, key))
                        return $"Code '{key}' of '{from.Name}' would move into '{into.Name}', where it is a former code, and a code never comes back; map it with codes.";
                    if (key is not null && ReferenceDataRules.FormerCodeValue(into, key, out var problem) is null)
                        return $"Code '{key}' of '{from.Name}' would move into '{into.Name}' and does not fit its code: {problem}; map it with codes.";
                    moving.Add(row.Id);
                    if (key is not null)
                        movingCodes.Add(key);
                }
            }

            foreach (var former in from.FormerCodes)
            {
                if (IsLive(into.Id, former.Code) || IsFormer(into.Id, former.Code))
                    return $"Former code '{former.Code}' of '{from.Name}' is a code or a former code of '{into.Name}' already.";
            }

            // A's fields map to B's fields of the same name and type; a moving row may hold no value for a field B lacks.
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var field in from.Attributes)
            {
                fields[field.Id] = into.Attributes.FirstOrDefault(g => string.Equals(g.Name, field.Name, StringComparison.Ordinal)
                    && g.Collection == field.Collection && SameType(field.Type, g.Type, from.Id, into.Id))?.Id;
            }

            foreach (var seed in fromSeeds)
            {
                for (var k = 0; k < seed.Columns.Count; k++)
                {
                    if (!fields.TryGetValue(seed.Columns[k], out var match) || match is not null)
                        continue;
                    var holder = seed.Rows.FirstOrDefault(r => moving.Contains(r.Id) && k < r.Values.Count && r.Values[k].ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
                    if (holder is not null)
                    {
                        var name = from.Attributes.First(a => a.Id == seed.Columns[k]).Name;
                        return $"Row {holder.Id} of seed '{seed.Name}' holds a value for field '{name}' of '{from.Name}', and '{into.Name}' has no field of that name and type.";
                    }
                }
            }

            // A seed whose name a seed of B has joins it.
            var intoSeeds = SeedsOf(into.Id);
            var joins = new Dictionary<string, Seed>(StringComparer.Ordinal);
            foreach (var seed in fromSeeds)
            {
                if (intoSeeds.FirstOrDefault(s => string.Equals(s.Name, seed.Name, StringComparison.OrdinalIgnoreCase)) is not { } other)
                    continue;
                if (other.RowsFrom?.Script is not null)
                    return $"Seed '{other.Name}' of '{into.Name}', which seed '{seed.Name}' of '{from.Name}' would join, computes its rows with a script.";
                if (other.Apply != seed.Apply || other.Delete != seed.Delete
                    || !other.Environments.Order(StringComparer.Ordinal).SequenceEqual(seed.Environments.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    return $"Seed '{seed.Name}' of '{from.Name}' and seed '{other.Name}' of '{into.Name}' share a name but differ in environments or apply; rename one first.";
                }

                joins[seed.Id] = other;
            }

            if (Outside(new HashSet<string>(removed.Keys.Prepend(from.Id), StringComparer.Ordinal), $"'{from.Name}' or one of its rows that go") is { } outside)
                return outside;

            Mark(from, index);
            Mark(into, index);
            _mergedInto[from.Id] = into.Id;
            Came(into.Id).AddRange(movingCodes);
            Node(into.Id, index, o);
            Node(from.Id, index, o); // deleted below; its working copy carries the hash the delete checks and its former codes
            var uses = new List<ReferenceInfo>();

            // Values: codes that map through codes, then the ids of removed rows, then every reference to A.
            foreach (var (key, value) in mapped)
            {
                var toValue = CellOf(into.Id, _data.Codes[into.Id][value]) ?? ReferenceDataRules.FormerCodeValue(into, value, out _);
                if (toValue is null)
                    continue;
                foreach (var use in CodeUses(from.Id, key, [.. RowsWith(from.Id, key).Select(r => r.RowId)]))
                {
                    if (use.FromElementId == from.Id)
                        continue; // the merged-away type goes
                    Set(use.FromElementId, use.JsonPointer, toValue.Value, index, o);
                    uses.Add(use);
                }
            }

            // The merged-away type's own former codes follow too: they move into the target below.
            foreach (var use in IdUses(removed.Keys))
            {
                Set(use.FromElementId, use.JsonPointer, JsonSerializer.SerializeToElement(removed[use.ToId]), index, o);
                if (use.FromElementId != from.Id)
                    uses.Add(use);
            }

            var dropped = new List<(string Diagram, int Member)>();
            foreach (var use in snapshot.ReferencesTo(from.Id))
            {
                if (use.FromElementId == from.Id || Value(use) is not { ValueKind: JsonValueKind.String } value || !value.ValueEquals(from.Id))
                    continue;
                if (snapshot.GetDocument(use.FromElementId)?.Element is Diagram diagram && MemberOf(use.JsonPointer) is { } member
                    && diagram.Members.Any(m => m.Element == into.Id))
                {
                    Node(use.FromElementId, index, o);
                    dropped.Add((use.FromElementId, member)); // the diagram shows the target already
                }
                else
                {
                    Set(use.FromElementId, use.JsonPointer, JsonSerializer.SerializeToElement(into.Id), index, o);
                }

                if (!use.Owning)
                    uses.Add(use);
            }

            foreach (var seed in fromSeeds)
                Node(seed.Id, index, o);
            foreach (var other in joins.Values)
                Node(other.Id, index, o);

            _structural.Add(() =>
            {
                foreach (var (diagram, member) in dropped.OrderByDescending(d => d.Member))
                    RemoveAt(_nodes[diagram].Node["members"] as JsonArray, member);
                var formers = (_nodes[from.Id].Node["formerCodes"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(f => ((string?)f["code"], (string?)f["replacedBy"])).Where(f => f.Item1 is not null && f.Item2 is not null)
                    .Select(f => (f.Item1!, f.Item2!));
                AddFormerCodes(into.Id, [.. formers, .. mapped.Select(m => (m.Key, _data.Codes[into.Id][m.Value]))]);
                foreach (var seed in fromSeeds)
                {
                    RemoveRows(seed.Id, removed.Keys.ToHashSet(StringComparer.Ordinal));
                    RemapColumns(seed.Id, fields);
                    if (joins.TryGetValue(seed.Id, out var other))
                    {
                        Join(seed.Id, other.Id);
                        Delete(seed.Id);
                    }
                    else if (seed.RowsFrom is null && seed.Rows.Count > 0 && seed.Rows.All(r => removed.ContainsKey(r.Id)))
                    {
                        // Every row of the seed matched or mapped to a row of the target: the seed holds nothing more and goes with its type.
                        Delete(seed.Id);
                    }
                    else
                    {
                        _nodes[seed.Id].Node["target"] = into.Id;
                    }
                }

                Delete(from.Id);
            });

            var gone = removed.Keys.Append(from.Id).Concat(DocumentReader.Scan(snapshot.GetDocument(from.Id)!.Json).Ids.Select(x => x.Id));
            Results.Add(new ReferenceRefactor(index, from.Id, into.Id, from.Id, Names(from), Names(into),
                [.. mapped.Select(m => new MigrationValue { From = m.Key, To = m.Value })], false,
                Usage(uses, moving.Count, removed.Count, Translations(gone))));
            return null;
        }

        private string? RenameType(int index, BatchOperation o, ReferenceType type)
        {
            if (string.IsNullOrEmpty(o.Name))
                return "rename-reference-type needs the new name (name).";
            var name = o.Name;
            if (string.Equals(name, type.Name, StringComparison.Ordinal))
                return $"The reference type is already named '{name}'.";
            if (!ElementName.IsMatch(name))
                return $"'{name}' is not an element name: letters, digits and underscores, not starting with a digit.";
            if (snapshot.All<ReferenceType>().FirstOrDefault(t => t.Id != type.Id && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } other)
                return $"Reference type {other.Id} is named '{other.Name}' already.";

            Mark(type, index);
            Node(type.Id, index, o)["name"] = name;
            // The seeds in its seed folder are planned after it, so they follow the new name there. A seed elsewhere, or every seed when
            // the folder keeps its name, does not move: it is not planned, so it needs no expected hash and another operation may change it.
            if (!string.Equals(ModelPaths.Stem(type), ModelPaths.Stem(type with { Name = name }), StringComparison.Ordinal))
            {
                foreach (var seed in SeedsOf(type.Id))
                {
                    var seedFolder = ModelPaths.ConventionalFolder(seed, _ => null, _ => ModelPaths.Stem(type));
                    if (string.Equals(ModelPaths.FolderOf(paths.FromRepoPath(snapshot.GetDocument(seed.Id)!.Path)), seedFolder, StringComparison.Ordinal))
                        Node(seed.Id, index, o);
                }
            }
            Results.Add(new ReferenceRefactor(index, type.Id, type.Id, null, Names(type), Names(type) with { Name = name }, [], true, Usage([], 0, 0, 0)));
            return null;
        }

        // ---- reading the model as it was before the batch --------------------------------------------------------------------------

        /// <summary>Why a refactor of the type is refused because another operation of the batch refactors it, or <see langword="null"/>.</summary>
        private string? Taken(ReferenceType type) =>
            _refactored.TryGetValue(type.Id, out var other)
                ? $"Reference type '{type.Name}' is refactored by operation {other.ToString(CultureInfo.InvariantCulture)} of this batch already; refactor a type once per batch."
                : null;

        private void Mark(ReferenceType type, int index) => _refactored.TryAdd(type.Id, index);

        /// <summary>Whether a code is a code of a written row of the type (the index's codes; scripted rows are not there).</summary>
        private bool IsLive(string typeId, string code) => _data.Codes.TryGetValue(typeId, out var codes) && codes.ContainsKey(code);

        /// <summary>Whether a code is a former code of the type.</summary>
        private bool IsFormer(string typeId, string code) => _data.FormerCodes.TryGetValue(typeId, out var codes) && codes.ContainsKey(code);

        /// <summary>Why a code is no live code of the type, naming the scripted seeds whose codes a refactor cannot reach.</summary>
        private string Unknown(ReferenceType type, string code)
        {
            var scripted = SeedsOf(type.Id).Where(s => s.RowsFrom?.Script is not null).Select(s => $"'{s.Name}'").ToList();
            return scripted.Count == 0
                ? $"'{code}' is not a code of reference type '{type.Name}'."
                : $"'{code}' is not a code of a written row of reference type '{type.Name}'; seed {string.Join(", ", scripted)} computes rows with a script, whose codes a refactor does not change.";
        }

        /// <summary>The seeds of a type, by (name, id).</summary>
        private List<Seed> SeedsOf(string typeId) =>
        [
            .. snapshot.ReferencesTo(typeId).Where(r => r.Owning).Select(r => r.FromElementId).Distinct(StringComparer.Ordinal)
                .Select(snapshot.Get<Seed>).OfType<Seed>().Where(s => s.Target == typeId)
                .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal),
        ];

        /// <summary>The written rows of a type that hold a code: the seed, the pointer of the row's code cell and the row id.</summary>
        private List<(string SeedId, string Pointer, string RowId)> RowsWith(string typeId, string code)
        {
            var rows = new List<(string, string, string)>();
            foreach (var seed in SeedsOf(typeId))
            {
                var at = IndexOf(seed.Columns, "code");
                if (at < 0 || seed.RowsFrom?.Script is not null)
                    continue;
                for (var r = 0; r < seed.Rows.Count; r++)
                {
                    var row = seed.Rows[r];
                    if (at < row.Values.Count && ReferenceDataIndex.CodeKey(row.Values[at]) == code)
                        rows.Add((seed.Id, "/rows/" + Str(r) + "/values/" + Str(at), row.Id));
                }
            }

            return rows;
        }

        /// <summary>The code cell of a written row of the type, as the seed holds it.</summary>
        private JsonElement? CellOf(string typeId, string rowId)
        {
            if (!snapshot.TryGetEntry(rowId, out var entry) || snapshot.Get<Seed>(entry.OwnerId) is not { } seed || seed.Target != typeId)
                return null;
            var at = IndexOf(seed.Columns, "code");
            var row = seed.Rows.FirstOrDefault(r => r.Id == rowId);
            return at >= 0 && row is not null && at < row.Values.Count ? row.Values[at] : null;
        }

        /// <summary>
        /// Every value that names a code of the type through one of the rows: the index's code uses (defaults, <c>allowedValues</c> entries
        /// and seed cells of attributes and fields the type types), and the values written into columns the type types (3.2): their
        /// literal defaults, the cells of the table seeds that list them and the constants of the bindings that name them by id. Each
        /// holds the code itself; its <see cref="ReferenceInfo.ToId"/> is the row.
        /// </summary>
        private List<ReferenceInfo> CodeUses(string typeId, string code, IReadOnlyCollection<string> rows)
        {
            var uses = new List<ReferenceInfo>();
            var seen = new HashSet<(string, string)>();
            void Add(ReferenceInfo use)
            {
                if (Value(use) is { } value && ReferenceDataIndex.CodeKey(value) == code && seen.Add((use.FromElementId, use.JsonPointer)))
                    uses.Add(use);
            }

            foreach (var row in rows)
            {
                foreach (var use in snapshot.ReferencesTo(row))
                {
                    // A state code under a lifecycle's reference binding is a use of its row too (lifecycle-design.md decision 18).
                    if (use.Field is "default" or "allowedValues" or "values" or "code")
                        Add(use);
                }
            }

            var rowId = rows.FirstOrDefault() ?? typeId;
            foreach (var typed in snapshot.ReferencesTo(typeId))
            {
                if (typed.Field != "typeRef" || !typed.JsonPointer.EndsWith("/typeRef/ref", StringComparison.Ordinal)
                    || snapshot.GetDocument(typed.FromElementId)?.Element is not (Table or Stereotype))
                {
                    continue;
                }

                var column = typed.JsonPointer[..^"/typeRef/ref".Length];
                foreach (var (pointer, fromId) in Items(typed.FromElementId, column + "/default", typed.FromId))
                    Add(new ReferenceInfo(typed.FromElementId, fromId, pointer, "default", rowId));
                foreach (var use in snapshot.ReferencesTo(typed.FromId))
                {
                    if (snapshot.GetDocument(use.FromElementId) is not { } user)
                        continue;
                    if (user.Element is Seed seed && use.Field == "columns" && use.JsonPointer.StartsWith("/columns/", StringComparison.Ordinal)
                        && JsonPointer.TryIndex(use.JsonPointer["/columns/".Length..], out var k))
                    {
                        for (var r = 0; r < seed.Rows.Count; r++)
                        {
                            foreach (var (pointer, fromId) in Items(seed.Id, "/rows/" + Str(r) + "/values/" + Str(k), seed.Rows[r].Id))
                                Add(new ReferenceInfo(seed.Id, fromId, pointer, "values", rowId));
                        }
                    }
                    else if (use.JsonPointer.EndsWith("/column", StringComparison.Ordinal) && use.JsonPointer.Contains("/constants/", StringComparison.Ordinal))
                    {
                        foreach (var (pointer, fromId) in Items(use.FromElementId, use.JsonPointer[..^"/column".Length] + "/value", use.FromId))
                            Add(new ReferenceInfo(use.FromElementId, fromId, pointer, "value", rowId));
                    }
                }
            }

            return uses;
        }

        /// <summary>The value at a pointer, or each item of it when it is an array, with the pointer of each.</summary>
        private IEnumerable<(string Pointer, string FromId)> Items(string elementId, string pointer, string fromId)
        {
            if (snapshot.GetDocument(elementId) is not { } document || !JsonPointer.TryGet(document.Json, pointer, out var value))
                yield break;
            if (value.ValueKind != JsonValueKind.Array)
            {
                yield return (pointer, fromId);
                yield break;
            }

            for (var i = 0; i < value.GetArrayLength(); i++)
                yield return (pointer + "/" + Str(i), fromId);
        }

        /// <summary>The references that name one of the rows by id (<c>x-ref</c> values, former codes' <c>replacedBy</c>).</summary>
        private List<ReferenceInfo> IdUses(IEnumerable<string> rows)
        {
            var uses = new List<ReferenceInfo>();
            foreach (var row in rows)
            {
                foreach (var use in snapshot.ReferencesTo(row))
                {
                    if (Value(use) is { ValueKind: JsonValueKind.String } value && value.ValueEquals(row))
                        uses.Add(use);
                }
            }

            return uses;
        }

        /// <summary>The value a reference's pointer reads in its document as it was before the batch.</summary>
        private JsonElement? Value(ReferenceInfo use) =>
            snapshot.GetDocument(use.FromElementId) is { } document && JsonPointer.TryGet(document.Json, use.JsonPointer, out var value) ? value : null;

        /// <summary>
        /// Why a refactor that removes the ids is refused because a file outside the model's documents names one: an <c>x-ref</c> whose
        /// <c>of</c> names it in an extension file or a local pack's <c>parameterSchema</c>, or a pack parameter value in
        /// <c>maquettiste.json</c>. A batch writes model documents only, so those would name nothing; <see langword="null"/> when none does.
        /// </summary>
        private string? Outside(IReadOnlySet<string> ids, string what)
        {
            foreach (var source in snapshot.PropertySchemas.Sources.Where(s => s.StereotypeKey is null))
            {
                foreach (var (pointer, xref) in source.Keywords)
                {
                    if (xref.Of is { } of && ids.Contains(of))
                        return $"The x-ref at {source.Path} {pointer}/of takes the rows of {what}, and a refactor writes model documents only; point it elsewhere first.";
                }
            }

            if (packs.Value.Ofs.FirstOrDefault(o => ids.Contains(o.Of)) is { } scope)
                return $"The x-ref at {scope.Path} {scope.Pointer} takes the rows of {what}, and a refactor writes model documents only; point it elsewhere first.";
            if (packs.Value.Values.FirstOrDefault(v => ids.Contains(v.Value)) is { } value)
                return $"Parameter '{value.Key}' of pack '{value.Pack}' in maquettiste.json names {what}, and a refactor writes model documents only; change it first.";
            return null;
        }

        /// <summary>The number of translation entries (one per translated locale and node) of the ids.</summary>
        private int Translations(IEnumerable<string> ids)
        {
            var l10n = snapshot.Localization;
            var locales = l10n.Locales.Where(l10n.IsTranslated).ToList();
            var count = 0;
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                foreach (var locale in locales)
                {
                    if (l10n.TryGetEntry(locale, id, out _, out _))
                        count++;
                }
            }

            return count;
        }

        // ---- writing the working copies ------------------------------------------------------------------------------------------

        /// <summary>A document's working copy, made on first use; the operation's <c>expectedHashes</c> decide the hash its write checks.</summary>
        private JsonObject Node(string id, int index, BatchOperation o)
        {
            if (_nodes.TryGetValue(id, out var working))
            {
                if (o.ExpectedHashes is { } given && given.TryGetValue(id, out var read))
                    _nodes[id] = working with { Expected = read };
                return working.Node;
            }

            var document = snapshot.GetDocument(id)!;
            var expected = o.ExpectedHashes is { } hashes ? hashes.GetValueOrDefault(id) ?? "unread" : document.Hash;
            var node = (JsonObject)JsonNode.Parse(document.Json.GetRawText())!;
            _nodes[id] = new Working(expected, node, index, document.Element is ReferenceType);
            _order.Add(id);
            return node;
        }

        /// <summary>Writes a value at a pointer of a document's working copy (the pointer as the document was before the batch).</summary>
        private void Set(string id, string pointer, JsonElement value, int index, BatchOperation o)
        {
            if (pointer.EndsWith("/code", StringComparison.Ordinal) && snapshot.Get<Process>(id) is not null && _codeWrites.TryAdd((id, pointer), index))
                _codeWriteOrder.Add((id, pointer));
            var node = Node(id, index, o);
            if (!JsonPointer.TryGetParent(node, pointer, out var parent, out var last))
                return;
            var copy = JsonNode.Parse(value.GetRawText());
            if (parent is JsonObject obj)
                obj[last] = copy;
            else if (parent is JsonArray array && JsonPointer.TryIndex(last, out var at) && at < array.Count)
                array[at] = copy;
        }

        private void Delete(string id)
        {
            if (!_deleted.Add(id))
                return;
            var expected = _nodes.TryGetValue(id, out var working) ? working.Expected : snapshot.GetDocument(id)!.Hash;
            _deletes.Add((id, expected));
        }

        /// <summary>Adds former codes to a type, a code listed already keeping one entry (the new one), sorted by code.</summary>
        private void AddFormerCodes(string typeId, IReadOnlyList<(string Code, string ReplacedBy)> added)
        {
            var node = _nodes[typeId].Node;
            var codes = added.Select(a => a.Code).ToHashSet(StringComparer.Ordinal);
            var entries = (node["formerCodes"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(f => (string?)f["code"] is not { } code || !codes.Contains(code))
                .Select(f => f.DeepClone().AsObject())
                .Concat(added.Select(a => new JsonObject { ["code"] = a.Code, ["replacedBy"] = a.ReplacedBy }))
                .OrderBy(f => (string?)f["code"] ?? "", StringComparer.Ordinal)
                .ToList();
            node["formerCodes"] = new JsonArray([.. entries]);
        }

        /// <summary>Removes rows from a seed's working copy by id.</summary>
        private void RemoveRows(string seedId, IReadOnlySet<string> rows)
        {
            if (_nodes[seedId].Node["rows"] is not JsonArray array)
                return;
            for (var i = array.Count - 1; i >= 0; i--)
            {
                if (array[i] is JsonObject row && (string?)row["id"] is { } id && rows.Contains(id))
                    array.RemoveAt(i);
            }
        }

        /// <summary>A merged-away type's seed names the target's fields: each field column maps to its match, or goes (no moving row holds it).</summary>
        private void RemapColumns(string seedId, IReadOnlyDictionary<string, string?> fields)
        {
            var node = _nodes[seedId].Node;
            if (node["columns"] is not JsonArray columns)
                return;
            for (var k = columns.Count - 1; k >= 0; k--)
            {
                if ((string?)columns[k] is not { } column || !fields.TryGetValue(column, out var match))
                    continue;
                if (match is not null)
                {
                    columns[k] = match;
                    continue;
                }

                columns.RemoveAt(k);
                foreach (var row in (node["rows"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (row["values"] is JsonArray values && k < values.Count)
                        values.RemoveAt(k);
                }
            }

            foreach (var row in (node["rows"] as JsonArray ?? []).OfType<JsonObject>())
                Trim(row["values"] as JsonArray);
        }

        /// <summary>Moves a seed's rows into another seed of the target with its name, lining their cells up with its columns.</summary>
        private void Join(string seedId, string intoId)
        {
            var source = _nodes[seedId].Node;
            var target = _nodes[intoId].Node;
            var from = (source["columns"] as JsonArray ?? []).Select(c => (string?)c ?? "").ToList();
            var to = target["columns"] as JsonArray ?? [];
            target["columns"] = to;
            var rows = (source["rows"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var position = new int[from.Count];
            for (var k = 0; k < from.Count; k++)
            {
                var at = to.Select(c => (string?)c).ToList().IndexOf(from[k]);
                if (at < 0 && rows.Any(r => r["values"] is JsonArray v && k < v.Count && v[k] is not null))
                {
                    to.Add(from[k]);
                    at = to.Count - 1;
                }

                position[k] = at;
            }

            var into = target["rows"] as JsonArray ?? [];
            target["rows"] = into;
            foreach (var row in rows)
            {
                var values = new JsonArray();
                for (var i = 0; i < to.Count; i++)
                    values.Add((JsonNode?)null);
                if (row["values"] is JsonArray cells)
                {
                    for (var k = 0; k < from.Count && k < cells.Count; k++)
                    {
                        if (position[k] >= 0)
                            values[position[k]] = cells[k]?.DeepClone();
                    }
                }

                Trim(values);
                var moved = (JsonObject)row.DeepClone();
                moved["values"] = values;
                into.Add(moved);
            }
        }

        private static void Trim(JsonArray? values)
        {
            while (values is { Count: > 0 } && values[^1] is null)
                values.RemoveAt(values.Count - 1);
        }

        private static void RemoveAt(JsonArray? array, int index)
        {
            if (array is not null && index < array.Count)
                array.RemoveAt(index);
        }

        /// <summary>The member index of a diagram member's <c>element</c> pointer (<c>/members/&lt;i&gt;/element</c>), or <see langword="null"/>.</summary>
        private static int? MemberOf(string pointer)
        {
            const string Prefix = "/members/";
            if (!pointer.StartsWith(Prefix, StringComparison.Ordinal) || !pointer.EndsWith("/element", StringComparison.Ordinal))
                return null;
            return JsonPointer.TryIndex(pointer[Prefix.Length..^"/element".Length], out var at) ? at : null;
        }

        /// <summary>Whether two field types are one type, a type that names the merged-away type naming the target instead.</summary>
        private static bool SameType(TypeRef a, TypeRef b, string fromId, string intoId) =>
            string.Equals(a.Builtin, b.Builtin, StringComparison.Ordinal)
            && string.Equals(a.Ref == fromId ? intoId : a.Ref, b.Ref == fromId ? intoId : b.Ref, StringComparison.Ordinal);

        private static MigrationState Names(ReferenceType type) => new() { Name = type.Name, PluralName = type.PluralName };

        private static RefactorUsage Usage(List<ReferenceInfo> uses, int moved, int removed, int translations)
        {
            var distinct = uses.DistinctBy(u => (u.FromElementId, u.JsonPointer))
                .OrderBy(u => u.FromElementId, StringComparer.Ordinal).ThenBy(u => u.JsonPointer, StringComparer.Ordinal).ToList();
            return new RefactorUsage(distinct.Count, [.. distinct.Take(RefactorUsageLimit)], moved, removed, translations);
        }

        private static int IndexOf(IReadOnlyList<string> list, string value)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
