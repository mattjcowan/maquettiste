using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>
/// The process operations of a batch (phase-3-design.md sections 3 and 4.4): <c>sync-enum</c>, <c>set-lifecycle</c>,
/// <c>set-initial</c>, <c>refresh-scenario</c> and <c>use-subjects</c> (lifecycle-design.md decision 6). Each expands into updates of the documents it touches (the enum, the entity and the processes of a lifecycle
/// binding, or the process holding a compound state); the updates run with the batch's other operations, all or nothing, and a
/// refused operation reports MQ9019 on its pointer.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>Whether an operation is a process operation.</summary>
    /// <param name="op">The operation kind.</param>
    public static bool IsProcessOperation(BatchOp op) =>
        op is BatchOp.SyncEnum or BatchOp.SyncCodes or BatchOp.SetLifecycle or BatchOp.SetInitial or BatchOp.RefreshScenario or BatchOp.UseSubjects;

    /// <summary>
    /// What <c>sync-enum</c> on a lifecycle would change, without writing: the dry run the Problems quick fix and the process
    /// endpoints show before applying.
    /// </summary>
    /// <param name="processId">The lifecycle process.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan; <see cref="SyncEnumPlan.Problem"/> says why the process cannot be synced.</returns>
    public Task<SyncEnumPlan> PlanSyncEnumAsync(string processId, CancellationToken ct) => PlanSyncEnumAsync(processId, null, ct);

    /// <summary>What <c>sync-enum</c> on one subject's enum binding would change, without writing (lifecycle-design.md decision 15).</summary>
    /// <param name="processId">The lifecycle process.</param>
    /// <param name="entity">The subject whose enum binding to sync; <see langword="null"/> means the lifecycle's one enum binding.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan; <see cref="SyncEnumPlan.Problem"/> says why the process cannot be synced.</returns>
    public async Task<SyncEnumPlan> PlanSyncEnumAsync(string processId, string? entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        return new ProcessWork(snapshot, _options.EffectiveIdGenerator, []).PlanSync(processId, entity, null, out _);
    }

    /// <summary>
    /// What <c>sync-codes</c> would write, without writing (lifecycle-design.md decision 19): the codes each state that needs one keeps or
    /// takes, the states left unmatched, and whether it writes the binding.
    /// </summary>
    /// <param name="processId">The lifecycle process.</param>
    /// <param name="entity">The subject.</param>
    /// <param name="attribute">The subject's attribute that holds the state.</param>
    /// <param name="stateCodes">Codes keyed by state id, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan; <see cref="SyncCodesPlan.Problem"/> says why the operation is refused.</returns>
    public async Task<SyncCodesPlan> PlanSyncCodesAsync(string processId, string entity, string attribute, IReadOnlyDictionary<string, JsonElement>? stateCodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        return new ProcessWork(snapshot, _options.EffectiveIdGenerator, []).PlanCodes(processId, entity, null, attribute, stateCodes, out _);
    }

    /// <summary>
    /// The documents a batch's process operations change, as working copies. <paramref name="staged"/> are the batch's ordinary
    /// operations: <c>sync-enum</c> plans against the documents as the batch leaves them, so a member another operation starts
    /// using is refused like any used member.
    /// </summary>
    private sealed class ProcessWork(ModelSnapshot snapshot, IIdGenerator ids, IReadOnlyList<PlannedChange> staged)
    {
        // Documents the batch's ordinary operations write: the new element, or null for a delete.
        private readonly Dictionary<string, JsonObject?> _staged = Stage(staged);

        private readonly Dictionary<string, (string Hash, JsonObject Node)> _nodes = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];

        public IEnumerable<(string Id, string Hash, JsonObject Node)> Changes() => _order.Select(id => (id, _nodes[id].Hash, _nodes[id].Node));

        /// <summary>Applies one operation; returns why it is refused, or <see langword="null"/>.</summary>
        public string? Apply(BatchOperation o) => o.Op switch
        {
            BatchOp.SyncEnum => Sync(o),
            BatchOp.SyncCodes => SyncCodes(o),
            BatchOp.SetLifecycle => SetLifecycle(o),
            BatchOp.RefreshScenario => RefreshScenario(o),
            BatchOp.UseSubjects => UseSubjects(o),
            _ => SetInitial(o),
        };

        // ---- sync-enum ----

        private string? Sync(BatchOperation o)
        {
            var plan = PlanSync(o.Id ?? "", o.Entity, o.Stereotype, out var members);
            if (plan.Problem is not null)
                return plan.Problem;
            foreach (var written in new[] { plan.Process, plan.Enum! })
            {
                if (_staged.ContainsKey(written))
                    return $"'{Name(written)}' is also written by another operation of this batch; sync the enum in a batch of its own.";
            }

            if (plan.Refused.Count > 0)
            {
                var uses = string.Join("; ", plan.Refused.Select(r => $"'{r.Member}' by {string.Join(", ", r.ReferencedBy.Select(Label))}"));
                return $"Syncing enum '{Name(plan.Enum!)}' from process '{Name(plan.Process)}' would remove members still in use ({uses}); change those uses to a remaining member first, then sync again.";
            }

            if (o.ExpectedHash is not null && !string.Equals(o.ExpectedHash, snapshot.GetDocument(plan.Process)!.Hash, StringComparison.Ordinal))
                return $"Process '{Name(plan.Process)}' changed since it was read; reload it and sync again.";
            if (plan.Added.Count == 0 && plan.Removed.Count == 0 && !plan.Reordered)
                return null;
            Node(plan.Enum!, null)["members"] = members;
            return null;
        }

        /// <summary>
        /// Plans a sync of one subject's enum binding (lifecycle-design.md decision 15): <paramref name="entityId"/>'s, or the entry of
        /// stereotype <paramref name="stereotypeKey"/> (stereotype-sets-design.md decision 10), else the lifecycle's one enum binding in either
        /// form; <paramref name="members"/> is the new members array.
        /// </summary>
        public SyncEnumPlan PlanSync(string processId, string? entityId, string? stereotypeKey, out JsonArray members)
        {
            members = [];
            SyncEnumPlan Problem(string message, string? enumId = null) => new(processId, enumId, [], [], false, [], message);
            if (snapshot.Get<Process>(processId) is not { } process)
                return Problem($"'{processId}' is not a process; pass the id of a lifecycle process.");
            // The bindings of every subject, in either form (lifecycle-design.md decision 6).
            var bindings = process.Subjects.Select(s => s.BoundAttribute).OfType<string>().ToList();
            if (process.Use != ProcessUse.Lifecycle || bindings.Count == 0)
                return Problem(process.Use == ProcessUse.Library
                    ? $"Process '{process.Name}' is a library, which has no states to sync; pass the id of a lifecycle process."
                    : $"Process '{process.Name}' binds no attribute; set use to lifecycle and bind an enum attribute of the subject, then sync.");
            string bound;
            if (entityId is not null && stereotypeKey is not null)
                return Problem($"sync-enum on process '{process.Name}' names both an entity and a stereotype; pass one of them.");
            if (stereotypeKey is not null)
            {
                var entry = process.Subjects.FirstOrDefault(s => string.Equals(s.Stereotype, stereotypeKey, StringComparison.Ordinal));
                if (entry is null)
                    return Problem($"Process '{process.Name}' does not list stereotype '{stereotypeKey}' as a subject; pass one of its subjects.");
                if (entry.BoundAttribute is null)
                    return Problem($"Subject stereotype '{stereotypeKey}' of process '{process.Name}' binds no attribute; bind an enum attribute of the stereotype, then sync.");
                bound = entry.BoundAttribute;
            }
            else if (entityId is not null)
            {
                var entry = process.Subjects.FirstOrDefault(s => string.Equals(s.Entity, entityId, StringComparison.Ordinal));
                var label = snapshot.Get<Entity>(entityId) is { } named ? "entity '" + named.Name + "'" : "'" + entityId + "'";
                if (entry is null)
                    return Problem($"Process '{process.Name}' does not list {label} as a subject; pass one of its subjects as entity.");
                if (entry.BoundAttribute is null)
                    return Problem($"Subject {label} of process '{process.Name}' binds no attribute; bind an enum attribute of it, then sync.");
                bound = entry.BoundAttribute;
            }
            else
            {
                // The one enum binding: subjects that bind attributes typed by one enum count once.
                var enums = bindings.Where(b => EnumOf(b) is not null).ToList();
                if (enums.Select(EnumOf).Distinct(StringComparer.Ordinal).Count() > 1)
                    return Problem($"Process '{process.Name}' binds enum attributes of several subjects; pass the subject whose enum to sync as entity.");
                if (enums.Count == 0 && bindings.Count > 1)
                    return Problem($"Process '{process.Name}' binds no enum attribute; sync-enum syncs an enum binding.");
                bound = enums.Count > 0 ? enums[0] : bindings[0];
            }

            if (AttributeNode(bound) is not { } attribute
                || attribute["type"] is not JsonObject { } type || type["ref"]?.GetValue<string>() is not { } enumId
                || snapshot.Get<EnumType>(enumId) is not { } enumType)
            {
                return Problem($"The bound attribute of process '{process.Name}' is not typed by an enum; bind an enum attribute of the subject.");
            }

            if (enumType.Flags)
                return Problem($"Enum '{enumType.Name}' is a flags enum; bind a non-flags enum attribute, since a lifecycle is in one state at a time.", enumId);

            var states = ProcessRules.BoundStates(process).Distinct(StringComparer.Ordinal).ToList();
            var current = (Node(enumId, null, track: false)["members"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var member in current)
                byName.TryAdd(member["name"]?.GetValue<string>() ?? "", member);

            var added = new List<string>();
            foreach (var name in states)
            {
                if (byName.TryGetValue(name, out var kept))
                {
                    members.Add(kept.DeepClone());
                    continue;
                }

                added.Add(name);
                var member = new JsonObject { ["id"] = ids.NewId(), ["name"] = name };
                if (FindState(process.States, name) is { DisplayName: { } display })
                    member["displayName"] = display;
                members.Add(member);
            }

            var removedMembers = current.Where(m => !states.Contains(m["name"]?.GetValue<string>() ?? "", StringComparer.Ordinal)).ToList();
            var keptOrder = current.Select(m => m["name"]?.GetValue<string>() ?? "").Where(n => states.Contains(n, StringComparer.Ordinal)).ToList();
            var reordered = !keptOrder.SequenceEqual(states.Where(n => byName.ContainsKey(n)), StringComparer.Ordinal);
            var uses = Uses(enumId);
            var refused = new List<SyncEnumRefusal>();
            foreach (var member in removedMembers)
            {
                var referencedBy = uses.Where(u => u.Values.Any(v => Matches(member, v))).Select(u => u.By).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (referencedBy.Count > 0)
                    refused.Add(new SyncEnumRefusal(member["name"]?.GetValue<string>() ?? "", referencedBy));
            }

            return new SyncEnumPlan(processId, enumId, added, [.. removedMembers.Select(m => m["name"]?.GetValue<string>() ?? "")], reordered, refused, null);
        }

        private static ProcessState? FindState(IEnumerable<ProcessState> states, string name) =>
            states.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

        // The enum an attribute's type names, as the batch leaves it, or null.
        private string? EnumOf(string attributeId) =>
            AttributeNode(attributeId)?["type"] is JsonObject type && type["ref"] is JsonValue r && r.TryGetValue<string>(out var id) && snapshot.Get<EnumType>(id) is not null
                ? id : null;

        // ---- sync-codes ----

        // lifecycle-design.md decision 19: writes the plan's codes and binding to the process, in the form the file has.
        private string? SyncCodes(BatchOperation o)
        {
            var plan = PlanCodes(o.Id ?? "", o.Entity, o.Stereotype, o.Attribute, o.StateCodes, out var writes);
            if (plan.Problem is not null)
                return plan.Problem;
            if (_staged.ContainsKey(plan.Process))
                return $"'{Name(plan.Process)}' is also written by another operation of this batch; sync its codes in a batch of its own.";
            if (o.ExpectedHash is not null && !string.Equals(o.ExpectedHash, snapshot.GetDocument(plan.Process)!.Hash, StringComparison.Ordinal))
                return $"Process '{Name(plan.Process)}' changed since it was read; reload it and sync again.";
            if (writes.Count == 0 && !plan.Binds)
                return null;
            var node = Node(plan.Process, null);
            foreach (var (pointer, code) in writes)
            {
                if (Navigate(node, pointer) is JsonObject state)
                    state["code"] = JsonNode.Parse(code.GetRawText());
            }

            if (plan.Binds && o.Stereotype is { } key)
            {
                // A stereotype entry (stereotype-sets-design.md decision 10): the lifecycle lists it, so its entry takes the binding.
                if (Listed(node)?.FirstOrDefault(e => e["stereotype"]?.GetValue<string>() == key) is { } entry)
                    entry["boundAttribute"] = plan.Attribute;
            }
            else if (plan.Binds)
            {
                Bind(node, plan.Entity, plan.Attribute);
            }

            return null;
        }

        // The binding in the form the file has: the one-subject form's boundAttribute, the entity's subjects entry (added when missing), or
        // subjects with one entry when the process has no subject (decision 7).
        private void Bind(JsonObject node, string entity, string attribute)
        {
            if (OneSubjectForm(node))
            {
                node["boundAttribute"] = attribute;
                return;
            }

            if (Listed(node) is { } entries)
            {
                if (entries.FirstOrDefault(e => e["entity"]?.GetValue<string>() == entity) is { } entry)
                    entry["boundAttribute"] = attribute;
                else
                    ((JsonArray)node["subjects"]!).Add(new JsonObject { ["id"] = ids.NewId(), ["entity"] = entity, ["boundAttribute"] = attribute });
                return;
            }

            node["subjects"] = new JsonArray(new JsonObject { ["id"] = ids.NewId(), ["entity"] = entity, ["boundAttribute"] = attribute });
        }

        /// <summary>
        /// Plans <c>sync-codes</c> (lifecycle-design.md decision 19): the states a binding needs a code on keep theirs, take the one
        /// <paramref name="given"/> names, else the one code of the attribute's type equal to their name (case, <c>_</c> and <c>-</c>
        /// ignored), else the one row or member whose label equals their display name or name. An ambiguous match, or a code another state
        /// keeps or takes, takes none. <paramref name="writes"/> are the codes to write, by the state's pointer.
        /// </summary>
        public SyncCodesPlan PlanCodes(string processId, string? entityId, string? stereotypeKey, string? attributeId, IReadOnlyDictionary<string, JsonElement>? given,
            out List<(string Pointer, JsonElement Code)> writes)
        {
            writes = [];
            SyncCodesPlan Problem(string message) => new(processId, entityId ?? "", attributeId ?? "", false, [], [], message);
            if (snapshot.Get<Process>(processId) is not { } process)
                return Problem($"'{processId}' is not a process; pass the id of a lifecycle process.");
            if (process.Use != ProcessUse.Lifecycle)
                return Problem(process.Use == ProcessUse.Library
                    ? $"Process '{process.Name}' is a library, which has no states to code; pass the id of a lifecycle process."
                    : $"Process '{process.Name}' is an orchestration, which binds no attribute; set use to lifecycle first, then sync its codes.");
            // The subject: an entity, or a stereotype the lifecycle lists (stereotype-sets-design.md decision 10), whose own attribute it binds.
            ModelAttribute? attribute;
            string label;
            ProcessSubject? listedEntry;
            if (stereotypeKey is not null)
            {
                if (entityId is not null)
                    return Problem($"sync-codes on process '{process.Name}' names both an entity and a stereotype; pass one of them.");
                listedEntry = process.Subjects.FirstOrDefault(s => string.Equals(s.Stereotype, stereotypeKey, StringComparison.Ordinal));
                if (listedEntry is null || snapshot.GetStereotype(stereotypeKey) is not { } stereotype)
                    return Problem($"Process '{process.Name}' does not list stereotype '{stereotypeKey}' as a subject; list it first, or pass an entity.");
                label = "stereotype '" + stereotype.Key + "'";
                attribute = attributeId is null ? null : stereotype.Attributes.FirstOrDefault(a => string.Equals(a.Id, attributeId, StringComparison.Ordinal));
                if (attribute is null)
                    return Problem($"'{attributeId}' is not an attribute of stereotype '{stereotype.Key}'; a stereotype subject binds one of the stereotype's own attributes.");
            }
            else
            {
                if (entityId is null || snapshot.Get<Entity>(entityId) is not { } entity)
                    return Problem($"'{entityId}' is not an entity; pass the subject whose attribute holds the state as entity.");
                if (process.UsesOneSubjectForm && !string.Equals(process.Subject, entityId, StringComparison.Ordinal))
                {
                    var subject = process.Subject is not null && snapshot.Get<Entity>(process.Subject) is { } named ? "entity '" + named.Name + "'" : "no entity";
                    return Problem($"Process '{process.Name}' names {subject} as its one subject, so sync-codes binds that subject's attribute only; pass it as entity, or list the subjects first (use-subjects).");
                }

                // An entity that follows the lifecycle only through a stereotype entry keeps following it there: an entry of its own would
                // take it from the stereotype's binding (stereotype-sets-design.md decision 10).
                if (SubjectCoverage.EntryOf(snapshot, process, entity) is { Stereotype: { } through })
                    return Problem($"Entity '{entity.Name}' follows process '{process.Name}' through stereotype '{through}'; sync codes through the stereotype: pass stereotype '{through}' and one of its own attributes.");
                label = "entity '" + entity.Name + "'";
                listedEntry = process.Subjects.FirstOrDefault(s => string.Equals(s.Entity, entity.Id, StringComparison.Ordinal));
                attribute = attributeId is null || !Holds(entity, attributeId) ? null : LifecycleBindings.Find(snapshot, attributeId);
                if (attribute is null)
                    return Problem($"'{attributeId}' is not an attribute of entity '{entity.Name}' (own, inherited or from a stereotype); pass the one that holds the state.");
            }

            var type = LifecycleBindings.Classify(snapshot, attribute);
            if (type.Kind == BindingKind.Unsupported)
                return Problem($"Attribute '{attribute.Name}' of {label} {type.Why}; pass an attribute typed by an enum, a reference type, or a string or integer type.");

            var analysis = new ProcessAnalysis(process);
            var codes = ProcessCodes.Of(process);
            var stateCodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (stateId, code) in (given ?? new Dictionary<string, JsonElement>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (analysis.Find(stateId) is not { } node)
                    return Problem($"'{stateId}' (a key of stateCodes) is not a state of process '{process.Name}'.");
                if (node.Type is StateType.History or StateType.Choice)
                    return Problem($"State '{node.Path}' of process '{process.Name}' is a {(node.Type == StateType.History ? "history" : "choice")} state, which nothing rests in, so it takes no code; leave it out of stateCodes.");
                if (code.ValueKind == JsonValueKind.String ? code.GetString()!.Length == 0 : !(code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out _)))
                    return Problem($"The code stateCodes gives state '{node.Path}' is {code.GetRawText()}; a code is a non-empty string or an integer.");
                stateCodes[stateId] = code;
            }

            var candidates = Candidates(type, attribute, codes.Kind);
            var needing = LifecycleBindings.Uncoded(analysis, codes).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var coded in codes.Coded)
            {
                if (coded.State.Type is not (StateType.History or StateType.Choice) && !stateCodes.ContainsKey(coded.State.Id))
                    taken.Add(coded.Key);
            }

            foreach (var code in stateCodes.Values)
                taken.Add(ReferenceDataIndex.CodeKey(code)!);

            // Match each state that needs a code and has none given; a code two states match goes to neither.
            var matched = new Dictionary<string, (Candidate Code, string Source)>(StringComparer.Ordinal);
            var unmatched = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
            foreach (var node in analysis.States)
            {
                if (!needing.Contains(node.Id) || stateCodes.ContainsKey(node.Id))
                    continue;
                var key = Loose(node.State.Name);
                var byName = candidates.Where(c => c.Names.Any(n => string.Equals(Loose(n), key, StringComparison.Ordinal))).ToList();
                var byLabel = byName.Count > 0 ? [] : candidates.Where(c => c.Label is { } label
                    && (string.Equals(label, node.State.DisplayName, StringComparison.OrdinalIgnoreCase) || string.Equals(label, node.State.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                var found = byName.Count > 0 ? byName : byLabel;
                if (found.Count == 1 && !taken.Contains(found[0].Key))
                    matched[node.Id] = (found[0], byName.Count > 0 ? "name" : "label");
                else
                    unmatched[node.Id] = [.. found.Select(c => c.Code)];
            }

            foreach (var group in matched.GroupBy(m => m.Value.Code.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList())
            {
                foreach (var (stateId, (code, _)) in group)
                {
                    matched.Remove(stateId);
                    unmatched[stateId] = [code.Code];
                }
            }

            var result = new List<SyncCodesState>();
            var left = new List<SyncCodesUnmatched>();
            foreach (var node in analysis.States)
            {
                if (stateCodes.TryGetValue(node.Id, out var code))
                {
                    result.Add(new SyncCodesState(node.Id, node.Path, code, "given"));
                    writes.Add((node.Pointer, code));
                }
                else if (codes.Find(node.Id) is { } kept && node.Type is not (StateType.History or StateType.Choice))
                {
                    result.Add(new SyncCodesState(node.Id, node.Path, kept.State.Code!.Value, "kept"));
                }
                else if (matched.TryGetValue(node.Id, out var match))
                {
                    result.Add(new SyncCodesState(node.Id, node.Path, match.Code.Code, match.Source));
                    writes.Add((node.Pointer, match.Code.Code));
                }
                else if (unmatched.TryGetValue(node.Id, out var options))
                {
                    left.Add(new SyncCodesUnmatched(node.Id, node.Path, options));
                }
            }

            var binds = !string.Equals(listedEntry?.BoundAttribute, attribute.Id, StringComparison.Ordinal);
            return new SyncCodesPlan(process.Id, entityId ?? "", attribute.Id, binds, result, left, null);
        }

        // Whether an attribute is one of an entity's: its own or a base's, or one of a stereotype the entity carries.
        private bool Holds(Entity entity, string attributeId)
        {
            if (!snapshot.TryGetEntry(attributeId, out var entry))
                return false;
            if (Chain(entity).Contains(entry.OwnerId))
                return true;
            var chain = Chain(entity).Select(snapshot.Get<Entity>).OfType<Entity>();
            return chain.Any(e => snapshot.EffectiveStereotypes(e).Any(s => string.Equals(s.Stereotype.Id, entry.OwnerId, StringComparison.Ordinal)));
        }

        /// <summary>A code the attribute's type offers: the value to write, its key, the texts a state's name matches, and its label.</summary>
        private sealed record Candidate(JsonElement Code, string Key, IReadOnlyList<string> Names, string? Label);

        // The codes the attribute's type offers, in the type's order: a reference type's stored rows (by seed name and id, then row order),
        // an enum's members (an integer value when the lifecycle's codes are integers, else the member's key, else its name), or a plain
        // status's allowedValues.
        private List<Candidate> Candidates(BindingType type, ModelAttribute attribute, CodeKind kind)
        {
            var list = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(JsonElement code, IReadOnlyList<string> names, string? label)
            {
                if (ReferenceDataIndex.CodeKey(code) is { } key && seen.Add(key))
                    list.Add(new Candidate(code, key, names, label));
            }

            switch (type.Kind)
            {
                case BindingKind.Reference:
                    foreach (var seed in snapshot.All<Seed>().Where(s => s.Target == type.Reference!.Id && s.RowsFrom?.Script is null)
                                 .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal))
                    {
                        var at = IndexOfColumn(seed.Columns, "code");
                        var labelAt = IndexOfColumn(seed.Columns, "label");
                        if (at < 0)
                            continue;
                        foreach (var row in seed.Rows)
                        {
                            if (at >= row.Values.Count || ReferenceDataIndex.CodeKey(row.Values[at]) is not { } text)
                                continue;
                            var label = labelAt >= 0 && labelAt < row.Values.Count && row.Values[labelAt].ValueKind == JsonValueKind.String ? row.Values[labelAt].GetString() : null;
                            Add(row.Values[at], [text], label);
                        }
                    }

                    break;
                case BindingKind.Enum:
                    foreach (var member in type.Enum!.Members)
                    {
                        var names = new List<string> { member.Name };
                        if (member.Code is { } memberKey)
                            names.Add(memberKey);
                        if (member.Value is { } value)
                            names.Add(value.ToString(CultureInfo.InvariantCulture));
                        if (kind == CodeKind.Integer)
                        {
                            if (member.Value is { } number)
                                Add(JsonSerializer.SerializeToElement(number), names, member.DisplayName);
                        }
                        else
                        {
                            Add(JsonSerializer.SerializeToElement(member.Code ?? member.Name), names, member.DisplayName);
                        }
                    }

                    break;
                default:
                    var allowed = attribute.Validation?.AllowedValues is { Count: > 0 } own ? own : type.Plain!.Scalar?.Validation?.AllowedValues ?? [];
                    foreach (var value in allowed)
                    {
                        if (ReferenceDataIndex.CodeKey(value) is { } text)
                            Add(value, [text], null);
                    }

                    break;
            }

            return list;
        }

        private static int IndexOfColumn(IReadOnlyList<string> columns, string name)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i], name, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        // A name or code for loose matching: case, '_' and '-' ignored.
        private static string Loose(string text) =>
            text.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();

        /// <summary>
        /// The literals that attributes typed by an enum hold, with who holds them: defaults and allowed values (the attribute),
        /// seed cells in their columns (the seed), and scenario values keyed by them in <c>start.context</c>, an object's
        /// <c>values</c> (lifecycle-design.md decision 22), a step's <c>payload</c> or <c>expect.context</c> (the scenario). Documents the batch writes are read as the batch leaves them.
        /// </summary>
        private List<(string By, List<JsonNode?> Values)> Uses(string enumId)
        {
            var uses = new List<(string By, List<JsonNode?> Values)>();
            var attributes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in snapshot.ReferencesTo(enumId).Where(r => r.JsonPointer.EndsWith("/type/ref", StringComparison.Ordinal)))
            {
                if (!_staged.ContainsKey(reference.FromElementId)
                    && Navigate(Node(reference.FromElementId, null, track: false), reference.JsonPointer[..^"/type/ref".Length]) is JsonObject attribute)
                {
                    AddAttribute(reference.FromId, attribute);
                }
            }

            foreach (var node in _staged.Values.OfType<JsonObject>())
                FindTyped(node);

            foreach (var (id, node) in Documents<Seed>("seed"))
            {
                var columns = (node["columns"] as JsonArray ?? []).Select(c => c is JsonValue v && v.TryGetValue<string>(out var t) ? t : null).ToList();
                var cells = new List<JsonNode?>();
                foreach (var row in (node["rows"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var values = row["values"] as JsonArray ?? [];
                    for (var c = 0; c < columns.Count && c < values.Count; c++)
                    {
                        if (columns[c] is { } column && attributes.Contains(column))
                            Flatten(values[c], cells);
                    }
                }

                if (cells.Count > 0)
                    uses.Add((id, cells));
            }

            foreach (var (id, node) in Documents<Scenario>("scenario"))
            {
                var values = new List<JsonNode?>();
                Keyed((node["start"] as JsonObject)?["context"], values);
                foreach (var obj in (node["objects"] as JsonArray ?? []).OfType<JsonObject>())
                    Keyed(obj["values"], values);
                foreach (var step in (node["steps"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    Keyed(step["payload"], values);
                    Keyed((step["expect"] as JsonObject)?["context"], values);
                }

                if (values.Count > 0)
                    uses.Add((id, values));
            }

            return uses;

            void AddAttribute(string attributeId, JsonObject attribute)
            {
                attributes.Add(attributeId);
                var values = new List<JsonNode?>();
                Flatten(attribute["default"], values);
                Flatten((attribute["validation"] as JsonObject)?["allowedValues"], values);
                if (values.Count > 0)
                    uses.Add((attributeId, values));
            }

            void FindTyped(JsonNode? node)
            {
                if (node is JsonArray array)
                {
                    foreach (var item in array)
                        FindTyped(item);
                }
                else if (node is JsonObject obj)
                {
                    if (obj["type"] is JsonObject type && type["ref"] is JsonValue r && r.TryGetValue<string>(out var target) && target == enumId
                        && obj["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var attributeId))
                    {
                        AddAttribute(attributeId, obj);
                    }

                    foreach (var (_, child) in obj)
                        FindTyped(child);
                }
            }

            void Keyed(JsonNode? map, List<JsonNode?> into)
            {
                if (map is not JsonObject obj)
                    return;
                foreach (var (key, value) in obj)
                {
                    if (attributes.Contains(key))
                        Flatten(value, into);
                }
            }
        }

        /// <summary>The documents of a kind as the batch leaves them: the loaded ones it does not write, then the ones it writes.</summary>
        private IEnumerable<(string Id, JsonObject Node)> Documents<T>(string kind)
            where T : Element
        {
            foreach (var element in snapshot.All<T>())
            {
                if (!_staged.ContainsKey(element.Id))
                    yield return (element.Id, Node(element.Id, null, track: false));
            }

            foreach (var (id, node) in _staged)
            {
                if (node is not null && node["kind"] is JsonValue k && k.TryGetValue<string>(out var text) && text == kind)
                    yield return (id, node);
            }
        }

        private static Dictionary<string, JsonObject?> Stage(IReadOnlyList<PlannedChange> changes)
        {
            var staged = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                var node = change.Element as JsonObject;
                var id = change.Id ?? (node?["id"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null);
                if (id is not null)
                    staged[id] = change.Op == BatchOp.Delete ? null : node;
            }

            return staged;
        }

        private static void Flatten(JsonNode? node, List<JsonNode?> into)
        {
            if (node is JsonArray array)
                into.AddRange(array);
            else if (node is not null)
                into.Add(node);
        }

        // An enum literal is a member name or code (string) or its value (number).
        private static bool Matches(JsonObject member, JsonNode? literal)
        {
            if (literal is not JsonValue value)
                return false;
            if (value.TryGetValue<string>(out var text))
                return text == member["name"]?.GetValue<string>() || text == member["code"]?.GetValue<string>();
            return value.GetValueKind() == JsonValueKind.Number && member["value"] is JsonValue v
                && v.GetValueKind() == JsonValueKind.Number && value.ToJsonString() == v.ToJsonString();
        }

        // ---- set-lifecycle ----

        // lifecycle-design.md decision 9: today's behaviour for a target in the one-subject form or with no subject; a target that lists
        // its subjects gains the entity and leaves the entity's other lifecycles that list theirs alone. A process with no subject gets
        // subjects, never the one-subject form (decision 7).
        private string? SetLifecycle(BatchOperation o)
        {
            if (o.Id is null || snapshot.Get<Entity>(o.Id) is not { } entity)
                return $"'{o.Id}' is not an entity; pass the id of the entity whose lifecycle to set.";
            var entityNode = Node(entity.Id, o.ExpectedHash, track: false);
            var current = entityNode["lifecycle"]?.GetValue<string>();

            if (o.Target is null)
            {
                if (current is not null)
                    Node(entity.Id, o.ExpectedHash).Remove("lifecycle");
                foreach (var process in snapshot.All<Process>())
                {
                    var node = Node(process.Id, null, track: false);
                    var isCurrent = string.Equals(process.Id, current, StringComparison.Ordinal);
                    if (Listed(node) is { } entries)
                    {
                        // The entity leaves the subjects of its lifecycles; one left with none becomes an orchestration.
                        if (!isCurrent && node["use"]?.GetValue<string>() != "lifecycle")
                            continue;
                        var at = entries.FindIndex(e => e["entity"]?.GetValue<string>() == entity.Id);
                        if (at < 0)
                            continue;
                        var listed = (JsonArray)Node(process.Id, null)["subjects"]!;
                        listed.RemoveAt(at);
                        if (listed.Count == 0)
                        {
                            Node(process.Id, null).Remove("subjects");
                            Unbind(process.Id);
                        }
                    }
                    else if (isCurrent || (node["use"]?.GetValue<string>() == "lifecycle" && node["subject"]?.GetValue<string>() == entity.Id))
                    {
                        Unbind(process.Id);
                    }
                }

                return null;
            }

            if (snapshot.Get<Process>(o.Target) is not { } target)
                return $"'{o.Target}' is not a process; pass a process id as target, or omit target to clear the lifecycle.";

            if (!string.Equals(current, target.Id, StringComparison.Ordinal))
                Node(entity.Id, o.ExpectedHash)["lifecycle"] = target.Id;

            // The entity's previous lifecycle in the one-subject form turns back into an orchestration, which it would otherwise fail MQ9201.
            if (current is not null && !string.Equals(current, target.Id, StringComparison.Ordinal) && snapshot.Get<Process>(current) is not null
                && Node(current, null, track: false)["subject"]?.GetValue<string>() == entity.Id)
            {
                Unbind(current);
            }

            var processNode = Node(target.Id, null, track: false);
            if (!OneSubjectForm(processNode) && Listed(processNode) is { } targetEntries)
            {
                // The target lists its subjects: it gains the entity unless the entity, or an entity it derives from, is listed, or a stereotype
                // entry covers it (stereotype-sets-design.md decision 9).
                var chain = Chain(entity);
                var covered = targetEntries.Any(e => e["entity"]?.GetValue<string>() is { } listed && chain.Contains(listed))
                    || SubjectCoverage.EntryOf(snapshot, target, entity) is { Stereotype: not null };
                if (processNode["use"]?.GetValue<string>() != "lifecycle" || !covered)
                {
                    var node = Node(target.Id, null);
                    node["use"] = "lifecycle";
                    if (!covered)
                        ((JsonArray)node["subjects"]!).Add(new JsonObject { ["id"] = ids.NewId(), ["entity"] = entity.Id });
                }

                return null;
            }

            var previousSubject = processNode["subject"]?.GetValue<string>();
            if (processNode["use"]?.GetValue<string>() != "lifecycle" || previousSubject != entity.Id)
            {
                var node = Node(target.Id, null);
                node["use"] = "lifecycle";
                if (previousSubject != entity.Id)
                {
                    node.Remove("boundAttribute"); // an attribute of the previous subject
                    if (OneSubjectForm(processNode))
                    {
                        node["subject"] = entity.Id; // the form the file has
                    }
                    else
                    {
                        node.Remove("subjects"); // an empty list
                        node["subjects"] = new JsonArray(new JsonObject { ["id"] = ids.NewId(), ["entity"] = entity.Id });
                    }
                }
            }

            // The previous subject no longer follows the process.
            if (previousSubject is not null && previousSubject != entity.Id && snapshot.Get<Entity>(previousSubject) is not null
                && Node(previousSubject, null, track: false)["lifecycle"]?.GetValue<string>() == target.Id)
            {
                Node(previousSubject, null).Remove("lifecycle");
            }

            return null;
        }

        // Demoting a lifecycle to an orchestration clears its bindings and keeps its subjects, in either form.
        private void Unbind(string processId)
        {
            var node = Node(processId, null);
            node.Remove("use");
            node.Remove("boundAttribute");
            foreach (var entry in (node["subjects"] as JsonArray ?? []).OfType<JsonObject>())
                entry.Remove("boundAttribute");
        }

        // A process document's subjects entries, when it lists any.
        private static List<JsonObject>? Listed(JsonObject process) =>
            process["subjects"] is JsonArray { Count: > 0 } entries ? [.. entries.OfType<JsonObject>()] : null;

        // Whether a process document is in the one-subject form.
        private static bool OneSubjectForm(JsonObject process) => process["subject"] is not null || process["boundAttribute"] is not null;

        // An entity and the entities it derives from, by id; an inheritance cycle is cut at the first repeat.
        private HashSet<string> Chain(Entity entity)
        {
            var chain = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
            for (var next = entity.Base; next is not null && chain.Add(next) && snapshot.Get<Entity>(next) is { } ancestor; next = ancestor.Base)
            {
            }

            return chain;
        }

        // ---- use-subjects ----

        // lifecycle-design.md decision 6: the one-subject form converts only on request, each process to a subjects list of one entry.
        private string? UseSubjects(BatchOperation o)
        {
            var processes = new List<Process>();
            foreach (var id in o.ElementIds ?? [.. snapshot.All<Process>().Select(p => p.Id)])
            {
                if (snapshot.Get<Process>(id) is not { } process)
                    return $"'{id}' is not a process; list the processes to convert in elementIds.";
                processes.Add(process);
            }

            foreach (var process in processes)
            {
                var node = Node(process.Id, null, track: false);
                if (node["subject"]?.GetValue<string>() is not { } subject)
                    continue; // already in the subjects form, or no subject to list
                if (_staged.ContainsKey(process.Id))
                    return $"'{process.Name}' is also written by another operation of this batch; convert it in a batch of its own.";
                var entry = new JsonObject { ["id"] = ids.NewId(), ["entity"] = subject };
                if (node["boundAttribute"]?.GetValue<string>() is { } bound)
                    entry["boundAttribute"] = bound;
                var changed = Node(process.Id, null);
                changed.Remove("subject");
                changed.Remove("boundAttribute");
                changed["subjects"] = new JsonArray(entry);
            }

            return null;
        }

        // ---- set-initial ----

        private string? SetInitial(BatchOperation o)
        {
            if (o.Id is null)
                return "set-initial needs the process or compound state in id.";
            JsonObject holder;
            string where;
            string owner;
            if (snapshot.Get<Process>(o.Id) is { } process)
            {
                owner = process.Id;
                holder = Node(process.Id, o.ExpectedHash, track: false);
                where = $"process '{process.Name}'";
            }
            else if (snapshot.TryGetEntry(o.Id, out var entry) && entry.Kind == "state" && snapshot.Get<Process>(entry.OwnerId) is not null
                && Navigate(Node(entry.OwnerId, o.ExpectedHash, track: false), entry.JsonPointer) is JsonObject state)
            {
                if (state["type"]?.GetValue<string>() != "compound")
                    return $"State '{state["name"]?.GetValue<string>()}' is not compound; only the process and a compound state have an initial child.";
                owner = entry.OwnerId;
                holder = state;
                where = $"state '{state["name"]?.GetValue<string>()}'";
            }
            else
            {
                return $"'{o.Id}' is neither a process nor a state; pass the id of the process or of a compound state.";
            }

            var children = (holder["states"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (!children.Any(c => c["id"]?.GetValue<string>() == o.Target))
                return $"'{o.Target}' is not a direct child of {where}; the initial state is one of its children.";
            if (holder["initial"]?.GetValue<string>() == o.Target)
                return null;

            Node(owner, o.ExpectedHash); // marks the working copy the holder belongs to as changed
            holder["initial"] = o.Target;
            return null;
        }

        // ---- refresh-scenario ----

        private string? RefreshScenario(BatchOperation o)
        {
            if (o.Id is null || snapshot.Get<Scenario>(o.Id) is not { } scenario)
                return $"'{o.Id}' is not a scenario; pass the id of the scenario whose expectations to rewrite.";
            foreach (var written in new[] { scenario.Id, scenario.Process })
            {
                if (_staged.ContainsKey(written))
                    return $"'{Name(written)}' is also written by another operation of this batch; save it first, then refresh the scenario in a batch of its own.";
            }

            ScenarioReplay? replay;
            using (var runtime = new ProcessRuntime(snapshot, 1, CancellationToken.None))
                replay = ScenarioReplayer.Replay(scenario, runtime);
            if (replay is null)
                return $"Scenario '{scenario.Name}' names no process of the model; set its process, then refresh it.";
            if (!replay.Complete)
            {
                var why = replay.Diagnostics.FirstOrDefault(d => d.Rule is "MQ9305" or "MQ9306" or "MQ9507")?.Message ?? "the replay stopped early";
                return $"Scenario '{scenario.Name}' cannot be replayed to its last step ({why}); fix that step, then refresh again.";
            }

            var (expects, outcome) = replay.Observed(o.TriggerPoints == true);
            var node = Node(scenario.Id, o.ExpectedHash, track: false);
            var steps = node["steps"] as JsonArray ?? [];
            var changed = false;
            for (var i = 0; i < steps.Count && i < expects.Count; i++)
            {
                if (steps[i] is not JsonObject step)
                    continue;
                var expect = ExpectNode(expects[i]);
                if (!JsonNode.DeepEquals(step["expect"], expect))
                {
                    step["expect"] = expect;
                    changed = true;
                }
            }

            var outcomeText = outcome == ScenarioOutcome.Final ? "final" : null;
            if (node["outcome"]?.GetValue<string>() is var current && !string.Equals(current ?? "active", outcomeText ?? "active", StringComparison.Ordinal))
            {
                if (outcomeText is null)
                    node.Remove("outcome");
                else
                    node["outcome"] = outcomeText;
                changed = true;
            }

            if (changed)
                Node(scenario.Id, o.ExpectedHash); // marks the working copy as changed
            return null;
        }

        // The expectations a replay gives, as recording writes them: the refusal and what refused it too, where the replay records them
        // (rights-design.md decision 26).
        private static JsonObject ExpectNode(StepExpectation expect) => ProcessApi.ExpectNode(expect);

        // ---- helpers ----

        private string Name(string id) => snapshot.GetDocument(id)?.Element.Name ?? id;

        private string Label(string id)
        {
            if (snapshot.TryGetEntry(id, out var entry) && entry.Kind == "attribute"
                && Navigate(Node(entry.OwnerId, null, track: false), entry.JsonPointer) is JsonObject attribute)
            {
                return $"'{Name(entry.OwnerId)}.{attribute["name"]?.GetValue<string>()}'";
            }

            return snapshot.Get<Seed>(id) is { } seed ? $"seed '{seed.Name}'"
                : snapshot.Get<Scenario>(id) is { } scenario ? $"scenario '{scenario.Name}'" : $"'{Name(id)}'";
        }

        private JsonObject? AttributeNode(string attributeId) =>
            snapshot.TryGetEntry(attributeId, out var entry) && entry.Kind == "attribute"
                ? Navigate(Node(entry.OwnerId, null, track: false), entry.JsonPointer) as JsonObject
                : null;

        private static JsonNode? Navigate(JsonNode? node, string pointer)
        {
            if (pointer.Length == 0)
                return node;
            foreach (var raw in pointer[1..].Split('/'))
            {
                var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                node = node switch
                {
                    JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count => array[i],
                    JsonObject obj => obj[segment],
                    _ => null,
                };
                if (node is null)
                    return null;
            }

            return node;
        }

        /// <summary>
        /// The working copy of a document. <paramref name="track"/> false reads it without marking it changed (a later tracked call
        /// returns the same node and marks it); <paramref name="hash"/> is the caller's expected hash, else the loaded one.
        /// </summary>
        private JsonObject Node(string id, string? hash, bool track = true)
        {
            if (!_nodes.TryGetValue(id, out var working))
            {
                var document = snapshot.GetDocument(id)!;
                working = (hash ?? document.Hash, (JsonNode.Parse(document.Json.GetRawText()) as JsonObject)!);
                _nodes[id] = working;
            }
            else if (hash is not null && track && !_order.Contains(id, StringComparer.Ordinal))
            {
                _nodes[id] = working = (hash, working.Node);
            }

            if (track && !_order.Contains(id, StringComparer.Ordinal))
                _order.Add(id);
            return working.Node;
        }
    }
}

/// <summary>What <c>sync-enum</c> changes (phase-3-design.md section 4.4), or why it cannot run.</summary>
/// <param name="Process">The lifecycle process.</param>
/// <param name="Enum">The bound enum, when found.</param>
/// <param name="Added">Member names the sync adds, in state order.</param>
/// <param name="Removed">Member names the sync removes, in their current order.</param>
/// <param name="Reordered">Whether the members it keeps change order.</param>
/// <param name="Refused">Removed members that a default, allowed values, a seed cell or a scenario value still uses; applying refuses
/// the sync while any is listed.</param>
/// <param name="Problem">Why the process cannot be synced (not a lifecycle, no enum-typed bound attribute), or <see langword="null"/>.</param>
public sealed record SyncEnumPlan(
    string Process, string? Enum, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool Reordered, IReadOnlyList<SyncEnumRefusal> Refused, string? Problem);

/// <summary>A member removal that is refused because something still uses the member.</summary>
/// <param name="Member">The member name.</param>
/// <param name="ReferencedBy">The ids of the attributes, seeds and scenarios that use it, ordinal.</param>
public sealed record SyncEnumRefusal(string Member, IReadOnlyList<string> ReferencedBy);

/// <summary>What <c>sync-codes</c> writes (lifecycle-design.md decision 19), or why it is refused.</summary>
/// <param name="Process">The lifecycle process.</param>
/// <param name="Entity">The subject.</param>
/// <param name="Attribute">The subject's attribute that holds the state.</param>
/// <param name="Binds">Whether the sync writes the binding: a new entry, a new binding, or another attribute than the one bound.</param>
/// <param name="Codes">Every state that needs a code and has one after the sync, with each state <c>stateCodes</c> names, in document order.</param>
/// <param name="Unmatched">Every state that needs a code and gets none, in document order.</param>
/// <param name="Problem">Why the operation is refused (MQ9019), or <see langword="null"/>.</param>
public sealed record SyncCodesPlan(string Process, string Entity, string Attribute, bool Binds, IReadOnlyList<SyncCodesState> Codes,
    IReadOnlyList<SyncCodesUnmatched> Unmatched, string? Problem);
