using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>
/// The process operations of a batch (phase-3-design.md sections 3 and 4.4): <c>sync-enum</c>, <c>set-lifecycle</c> and
/// <c>set-initial</c> and <c>refresh-scenario</c>. Each expands into updates of the documents it touches (the enum, the entity and the processes of a lifecycle
/// binding, or the process holding a compound state); the updates run with the batch's other operations, all or nothing, and a
/// refused operation reports MQ9019 on its pointer.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>Whether an operation is a process operation.</summary>
    /// <param name="op">The operation kind.</param>
    public static bool IsProcessOperation(BatchOp op) => op is BatchOp.SyncEnum or BatchOp.SetLifecycle or BatchOp.SetInitial or BatchOp.RefreshScenario;

    /// <summary>
    /// What <c>sync-enum</c> on a lifecycle would change, without writing: the dry run the Problems quick fix and the process
    /// endpoints show before applying.
    /// </summary>
    /// <param name="processId">The lifecycle process.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan; <see cref="SyncEnumPlan.Problem"/> says why the process cannot be synced.</returns>
    public async Task<SyncEnumPlan> PlanSyncEnumAsync(string processId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        return new ProcessWork(snapshot, _options.EffectiveIdGenerator, []).PlanSync(processId, out _);
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
            BatchOp.SetLifecycle => SetLifecycle(o),
            BatchOp.RefreshScenario => RefreshScenario(o),
            _ => SetInitial(o),
        };

        // ---- sync-enum ----

        private string? Sync(BatchOperation o)
        {
            var plan = PlanSync(o.Id ?? "", out var members);
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

        /// <summary>Plans a sync; <paramref name="members"/> is the new members array.</summary>
        public SyncEnumPlan PlanSync(string processId, out JsonArray members)
        {
            members = [];
            SyncEnumPlan Problem(string message, string? enumId = null) => new(processId, enumId, [], [], false, [], message);
            if (snapshot.Get<Process>(processId) is not { } process)
                return Problem($"'{processId}' is not a process; pass the id of a lifecycle process.");
            if (process.Use != ProcessUse.Lifecycle || process.BoundAttribute is null)
                return Problem($"Process '{process.Name}' binds no attribute; set use to lifecycle and bind an enum attribute of the subject, then sync.");
            if (AttributeNode(process.BoundAttribute) is not { } attribute
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

        /// <summary>
        /// The literals that attributes typed by an enum hold, with who holds them: defaults and allowed values (the attribute),
        /// seed cells in their columns (the seed), and scenario values keyed by them in <c>start.context</c>, a step's
        /// <c>payload</c> or <c>expect.context</c> (the scenario). Documents the batch writes are read as the batch leaves them.
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
                    if (string.Equals(process.Id, current, StringComparison.Ordinal)
                        || (node["use"]?.GetValue<string>() == "lifecycle" && node["subject"]?.GetValue<string>() == entity.Id))
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

            // The entity's previous lifecycle turns back into an orchestration.
            if (current is not null && !string.Equals(current, target.Id, StringComparison.Ordinal) && snapshot.Get<Process>(current) is not null
                && Node(current, null, track: false)["subject"]?.GetValue<string>() == entity.Id)
            {
                Unbind(current);
            }

            var processNode = Node(target.Id, null, track: false);
            var previousSubject = processNode["subject"]?.GetValue<string>();
            if (processNode["use"]?.GetValue<string>() != "lifecycle" || previousSubject != entity.Id)
            {
                var node = Node(target.Id, null);
                node["use"] = "lifecycle";
                if (previousSubject != entity.Id)
                {
                    node["subject"] = entity.Id;
                    node.Remove("boundAttribute"); // an attribute of the previous subject
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

        private void Unbind(string processId)
        {
            var node = Node(processId, null);
            node.Remove("use");
            node.Remove("boundAttribute");
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

            var (expects, outcome) = replay.Observed();
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

        private static JsonObject ExpectNode(StepExpectation expect)
        {
            var node = new JsonObject();
            if (!expect.Accepted)
                node["accepted"] = false;
            node["states"] = new JsonArray([.. expect.States.Select(s => (JsonNode?)JsonValue.Create(s))]);
            if (expect.Context.Count > 0)
            {
                var context = new JsonObject();
                foreach (var (id, value) in expect.Context.OrderBy(p => p.Key, StringComparer.Ordinal))
                    context[id] = JsonNode.Parse(value.GetRawText());
                node["context"] = context;
            }

            return node;
        }

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
