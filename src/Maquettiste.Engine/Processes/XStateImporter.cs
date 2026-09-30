using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// Reads an XState machine config into a process document (phase-3-design.md section 5.2). States are collected first so every
/// target resolves whatever its order; then states, transitions and definitions are built as model JSON and written canonically.
/// </summary>
internal sealed partial class XStateImporter(ICanonicalJson json, XStateImportOptions options)
{
    private static readonly HashSet<string> RootKeys = new(["id", "initial", "states", "context", "meta", "description", "type"], StringComparer.Ordinal);
    private static readonly HashSet<string> StateKeys = new(
        ["id", "type", "initial", "states", "history", "target", "entry", "exit", "on", "after", "always", "onDone", "invoke", "meta", "description"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> TransitionKeys = new(["target", "guard", "actions", "reenter", "meta", "description"], StringComparer.Ordinal);
    private static readonly HashSet<string> InvokeKeys = new(["id", "src", "onDone", "onError", "meta"], StringComparer.Ordinal);
    private static readonly HashSet<string> StateTypes = new(["atomic", "compound", "parallel", "final", "history"], StringComparer.Ordinal);
    private static readonly string[] Descriptive = ["displayName", "description", "stereotypes", "properties"];

    private readonly List<Diagnostic> _diagnostics = [];
    private readonly List<string> _created = [];
    private readonly SortedDictionary<string, JsonNode?> _extensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _used = new(StringComparer.Ordinal); // id -> kind
    private readonly Dictionary<string, StateInfo> _byXStateId = new(StringComparer.Ordinal);
    private readonly Definitions _events = new("event");
    private readonly Definitions _guards = new("guard");
    private readonly Definitions _actions = new("action");
    private readonly HashSet<string> _invokeNames = new(StringComparer.Ordinal);
    private readonly List<(int Walk, JsonObject Node)> _transitions = [];
    private readonly Dictionary<string, int> _groupCounts = new(StringComparer.Ordinal);
    private readonly Into _into = new();
    private string _seed = "";
    private long _seedTime;
    private string _processId = "";
    private string _machine = "";
    private JsonObject? _delays;
    private int _walk;
    private List<StateInfo> _rootStates = [];

    public XStateImport Run(string text)
    {
        try
        {
            return RunCore(text);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException)
        {
            // A value of an unexpected JSON kind where the projection reads a name, a flag or an id.
            return Refused("The config has a shape the importer refuses: " + e.Message, null);
        }
    }

    private XStateImport RunCore(string text)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
                ?? throw new JsonException("the top level is not an object");
        }
        catch (JsonException e)
        {
            return Refused("The input is not a JSON statechart config: " + e.Message, "");
        }

        if (root["states"] is not JsonObject { Count: > 0 } rootStates)
            return Refused("The config has no states: a machine needs a states object with at least one state.", "/states");
        if (root["type"] is { } rootType && rootType.GetValueKind() == JsonValueKind.String && rootType.GetValue<string>() != "compound")
            return Refused("The machine root is '" + rootType.GetValue<string>() + "': the process root is a compound state; wrap the regions in one parallel state.", "/type");

        _seed = (options.Ids ?? new UlidIdGenerator()).NewId();
        _seedTime = DecodeTime(_seed);
        if (options.Into is { } into)
            _into.Load(json, into);
        _machine = root["id"] is JsonValue idValue && idValue.GetValueKind() == JsonValueKind.String ? idValue.GetValue<string>() : "";
        var rootMeta = Meta(root, "");
        _delays = root["delays"] as JsonObject;
        foreach (var (key, value) in root)
        {
            if (!RootKeys.Contains(key))
                Opaque("/" + Escape(key), value, "machine key '" + key + "'");
        }

        // The process id first: every other id is checked against it.
        _processId = options.Into?.Id ?? "";
        if (_processId.Length == 0)
        {
            var carried = rootMeta?["id"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            if (carried is not null && IdFormat.IsValid(carried) && options.Model?.GetDocument(carried) is null && !(options.Model?.TryGetEntry(carried, out _) ?? false))
                _processId = carried;
            else
            {
                if (carried is not null && IdFormat.IsValid(carried))
                    Refuse("MQ9405", "The process id " + carried + " already names an element of the model; import into that process to re-import it, or remove meta.maquettiste.id to import a copy.", "/meta/maquettiste/id");
                _processId = Derive("process");
            }
        }

        _used[_processId] = "process";
        LoadDefinitions(rootMeta);
        foreach (var (definitions, key) in new[] { (_events, "events"), (_guards, "guards"), (_actions, "actions") })
        {
            if (rootMeta?[key] is null && _into.Root is not null)
                definitions.Rank = IntoRank(key, "name");
        }

        // Pass 1: every state, so targets resolve in any order.
        var states = _rootStates;
        foreach (var (key, value) in rootStates)
            states.Add(Collect(key, value, null, "/states/" + Escape(key)));
        if (_diagnostics.Any(d => d.Rule == "MQ9403"))
            return Refused(null, null);

        // Pass 2: states, transitions and invokes as model JSON.
        var stateNodes = new JsonArray([.. states.Select(s => (JsonNode)Build(s))]);
        if (_diagnostics.Any(d => d.Rule == "MQ9403"))
            return Refused(null, null);

        var document = new JsonObject
        {
            ["kind"] = "process",
            ["id"] = _processId,
            ["name"] = ProcessName(rootMeta),
        };
        foreach (var (key, value) in RootLeftovers(rootMeta))
            document[key] = value;
        if (options.Package is not null)
            document["package"] = options.Package;
        if (options.Use is { } use)
            document["use"] = use == ProcessUse.Lifecycle ? "lifecycle" : "orchestration";
        if (options.Subject is not null)
            document["subject"] = options.Subject;
        if (root["description"] is JsonValue description && description.GetValueKind() == JsonValueKind.String && document["description"] is null)
            document["description"] = description.GetValue<string>();
        document["context"] = Context(root, rootMeta);
        var actions = Actions(); // first: raises may declare events
        document["events"] = _events.ToArray();
        document["guards"] = _guards.ToArray();
        document["actions"] = actions;
        document["states"] = stateNodes;
        if (root["initial"] is { } initial && rootMeta?["initialImplied"]?.GetValue<bool>() != true)
        {
            var child = Initial(initial, states, "/initial");
            if (child is not null)
                document["initial"] = child.Id;
        }

        document["transitions"] = Ordered(rootMeta);
        if (Source(rootMeta) is { } source)
            document["source"] = source;
        DropUnknownActors(document);
        if (_diagnostics.Any(d => d.Rule == "MQ9403"))
            return Refused(null, null);

        byte[] file;
        Process process;
        try
        {
            file = json.Write(document, "process.json", options.DocumentPath);
            process = JsonSerializer.Deserialize<Process>(file, EngineJson.Options)
                ?? throw new JsonException("the document is empty");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
        {
            return Refused("The imported document is not a valid process: " + e.Message, null);
        }

        var removed = _into.Ids.Where(id => !_used.ContainsKey(id)).Order(StringComparer.Ordinal).ToList();
        return new XStateImport(file, process, Sorted(), _created, removed);
    }

    // ---- states ----

    private sealed class StateInfo
    {
        public required string Key { get; init; }
        public required string Path { get; init; }
        public required string Pointer { get; init; }
        public required JsonObject Config { get; init; }
        public StateInfo? Parent { get; init; }
        public List<StateInfo> Children { get; } = [];
        public string Id { get; set; } = "";
        public JsonObject? Meta { get; set; }
        public JsonObject? IntoNode { get; set; }
    }

    private StateInfo Collect(string key, JsonNode? value, StateInfo? parent, string pointer)
    {
        var config = value as JsonObject ?? [];
        if (value is not JsonObject)
            Refuse("MQ9403", "State '" + key + "' is not an object.", pointer);
        var path = parent is null ? key : parent.Path + "." + key;
        var info = new StateInfo { Key = key, Path = path, Pointer = pointer, Config = config, Parent = parent };
        info.Meta = Meta(config, pointer);
        info.IntoNode = _into.States.GetValueOrDefault(path);
        string? explicitId = config["id"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        var carried = explicitId is not null && IdFormat.IsValid(explicitId) ? explicitId : Carried(info.Meta);
        if (explicitId is not null && !IdFormat.IsValid(explicitId))
        {
            _byXStateId[explicitId] = info;
            Warn("MQ9404", "State id '" + explicitId + "' is not a model id: the state gets a model id and targets naming '#" + explicitId + "' are resolved to it.", pointer + "/id");
        }

        info.Id = Assign(carried, "state", pointer + "/id", info.IntoNode, "state:" + path);
        _byXStateId[info.Id] = info;
        if (_machine.Length > 0)
            _byXStateId[_machine + "." + path] = info;
        if (config["states"] is JsonObject children)
        {
            foreach (var (childKey, childValue) in children)
                info.Children.Add(Collect(childKey, childValue, info, pointer + "/states/" + Escape(childKey)));
        }
        else if (config["states"] is not null)
            Refuse("MQ9403", "The states of '" + key + "' are not an object.", pointer + "/states");

        return info;
    }

    private JsonObject Build(StateInfo s)
    {
        var config = s.Config;
        var node = new JsonObject { ["id"] = s.Id, ["name"] = Identifier(s.Key, s.Pointer, "state name") };
        var leftovers = s.Meta is not null ? Without(s.Meta, "id", "initialImplied") : Pick(s.IntoNode, Descriptive);
        var configType = config["type"] is JsonValue tv && tv.GetValueKind() == JsonValueKind.String ? tv.GetValue<string>() : null;
        if (configType is not null && !StateTypes.Contains(configType))
            Refuse("MQ9403", "State '" + s.Key + "' has type '" + configType + "', which is not a statechart state type.", s.Pointer + "/type");
        var type = leftovers["type"]?.GetValue<string>() ?? configType ?? (s.Children.Count > 0 ? "compound" : "atomic");
        if (s.Meta is null && s.IntoNode?["type"]?.GetValue<string>() == "choice" && configType is null && s.Children.Count == 0
            && config["always"] is not null && config["on"] is null)
            type = "choice";
        leftovers.Remove("type");
        node["type"] = type;
        foreach (var (key, value) in config)
        {
            if (!StateKeys.Contains(key))
                Opaque(s.Pointer + "/" + Escape(key), value, "state key '" + key + "'");
        }

        if (config["initial"] is { } initial && s.Meta?["initialImplied"]?.GetValue<bool>() != true && type is "compound")
        {
            var child = Initial(initial, s.Children, s.Pointer + "/initial");
            if (child is not null)
                node["initial"] = child.Id;
        }

        if (type == "history")
        {
            if (config["history"]?.GetValue<string>() is { } history)
                node["history"] = history;
            if (config["target"] is { } target)
            {
                var resolved = Targets(target, s, s.Pointer + "/target");
                if (resolved.Count > 0)
                    node["defaultTarget"] = resolved[0];
            }
        }

        var stateName = Lower(s.Key);
        if (config["entry"] is { } entry)
            node["entry"] = ActionRefs(entry, s.Pointer + "/entry", stateName + "_entry");
        if (config["exit"] is { } exit)
            node["exit"] = ActionRefs(exit, s.Pointer + "/exit", stateName + "_exit");
        if (config["description"] is JsonValue d && d.GetValueKind() == JsonValueKind.String && leftovers["description"] is null)
            leftovers["description"] = d.GetValue<string>();

        Transitions(s, "on", "event");
        Transitions(s, "after", "after");
        Transitions(s, "always", "always");
        Transitions(s, "onDone", "done");
        var invokes = new JsonArray();
        if (config["invoke"] is { } invoke)
        {
            foreach (var (item, pointer) in Entries(invoke, s.Pointer + "/invoke"))
            {
                if (Invoke(item, pointer, s) is { } built)
                    invokes.Add(built);
            }
        }

        node["invoke"] = invokes;
        node["states"] = new JsonArray([.. s.Children.Select(c => (JsonNode)Build(c))]);
        foreach (var (key, value) in leftovers)
            node[key] = value?.DeepClone();
        return node;
    }

    private StateInfo? Initial(JsonNode initial, IReadOnlyList<StateInfo> children, string pointer)
    {
        var key = initial is JsonObject o ? o["target"]?.ToString() : initial.GetValueKind() == JsonValueKind.String ? initial.GetValue<string>() : null;
        var child = children.FirstOrDefault(c => c.Key == key);
        if (child is null)
            Refuse("MQ9403", "The initial state '" + key + "' is not a child of its state.", pointer);
        return child;
    }

    // ---- transitions ----

    private void Transitions(StateInfo s, string property, string trigger)
    {
        var value = s.Config[property];
        if (value is null)
            return;
        var pointer = s.Pointer + "/" + property;
        if (trigger is "event" or "after")
        {
            if (value is not JsonObject groups)
            {
                Opaque(pointer, value, "'" + property + "' that is not an object");
                return;
            }

            foreach (var (key, entries) in groups)
            {
                var groupPointer = pointer + "/" + Escape(key);
                if (trigger == "event" && (key.Length == 0 || key.Contains('*', StringComparison.Ordinal) || key.StartsWith("xstate.", StringComparison.Ordinal)))
                {
                    Opaque(groupPointer, entries, "event descriptor '" + key + "'");
                    continue;
                }

                foreach (var (entry, entryPointer) in Entries(entries, groupPointer))
                    Transition(s, trigger, key, null, entry, entryPointer);
            }

            return;
        }

        foreach (var (entry, entryPointer) in Entries(value, pointer))
            Transition(s, trigger, trigger, null, entry, entryPointer);
    }

    /// <summary>
    /// The items of a value that may be one item or an array of them. Pointers always take the array form (<c>/0</c> for a single
    /// item), the shape export writes, so data kept opaque under an item is written back at the same pointer.
    /// </summary>
    private static IEnumerable<(JsonNode? Entry, string Pointer)> Entries(JsonNode? value, string pointer) => value is JsonArray array
        ? array.Select((n, i) => (n, pointer + "/" + i.ToString(CultureInfo.InvariantCulture)))
        : [(value, pointer + "/0")];

    private void Transition(StateInfo s, string trigger, string discriminator, JsonObject? invoke, JsonNode? entry, string pointer)
    {
        var groupKey = s.Path + "|" + trigger + "|" + discriminator;
        var index = _groupCounts.GetValueOrDefault(groupKey);
        _groupCounts[groupKey] = index + 1;
        var matchKey = groupKey + "|" + index.ToString(CultureInfo.InvariantCulture);
        var config = entry switch
        {
            JsonObject o => o,
            JsonValue v when v.GetValueKind() == JsonValueKind.String => new JsonObject { ["target"] = v.GetValue<string>() },
            JsonArray a => new JsonObject { ["target"] = a.DeepClone() },
            _ => [],
        };
        var meta = entry is JsonObject ? Meta(config, pointer) : null;
        var intoNode = _into.Transitions.GetValueOrDefault(matchKey);
        var id = Assign(Carried(meta), "transition", pointer + "/meta/maquettiste/id", intoNode, "transition:" + pointer);
        var node = new JsonObject { ["id"] = id, ["source"] = s.Id, ["trigger"] = trigger };
        var leftovers = meta is not null ? Without(meta, "id") : Pick(intoNode, [.. Descriptive, "gate"]);
        if (entry is JsonObject)
        {
            foreach (var (key, value) in config)
            {
                if (!TransitionKeys.Contains(key))
                    Opaque(pointer + "/" + Escape(key), value, "transition key '" + key + "'");
            }
        }

        var targets = config["target"] is { } target ? Targets(target, s, pointer + "/target") : [];
        var targetName = targets.Count > 0 ? Lower(StateKey(targets[0])) : null;
        var naming = (trigger == "event" ? discriminator : invoke is not null ? discriminator : trigger) + (targetName is null ? "" : "_to_" + targetName);
        switch (trigger)
        {
            case "event":
                node["event"] = _events.Get(Identifier(discriminator, pointer, "event name"), this, "event:" + discriminator);
                break;
            case "after":
                node["after"] = Delay(discriminator, leftovers["after"]?.GetValue<string>(), pointer);
                leftovers.Remove("after");
                break;
            case "invoke-done" or "invoke-error":
                node["invoke"] = invoke!["id"]!.GetValue<string>();
                break;
        }

        if (config["guard"] is { } guard && Guard(guard, pointer + "/guard", naming) is { } guardId)
            node["guard"] = guardId;
        node["targets"] = new JsonArray([.. targets.Select(t => (JsonNode)t)]);
        if (config["actions"] is { } actions)
            node["actions"] = ActionRefs(actions, pointer + "/actions", naming);
        if (config["reenter"]?.GetValueKind() == JsonValueKind.True)
            node["external"] = true;
        if (config["description"] is JsonValue d && d.GetValueKind() == JsonValueKind.String && leftovers["description"] is null)
            leftovers["description"] = d.GetValue<string>();
        if (leftovers["gate"] is JsonObject gate)
            CheckGate(gate, pointer + "/meta/maquettiste/gate");
        foreach (var (key, value) in leftovers)
            node[key] = value?.DeepClone();
        _transitions.Add((_walk++, node));
    }

    private List<string> Targets(JsonNode target, StateInfo source, string pointer)
    {
        var result = new List<string>();
        foreach (var (item, itemPointer) in Entries(target, pointer))
        {
            var text = item is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            var state = text is null ? null : Resolve(text, source);
            if (state is null)
                Refuse("MQ9403", "Target '" + (text ?? item?.ToJsonString()) + "' does not resolve to a state of the machine.", itemPointer);
            else
                result.Add(state.Id);
        }

        return result;
    }

    private StateInfo? Resolve(string target, StateInfo source)
    {
        if (target.StartsWith('#'))
        {
            var id = target[1..];
            if (_byXStateId.TryGetValue(id, out var direct))
                return direct;
            // "#id.child.grandchild": the longest id prefix, then keys.
            for (var dot = id.LastIndexOf('.'); dot > 0; dot = id.LastIndexOf('.', dot - 1))
            {
                if (_byXStateId.TryGetValue(id[..dot], out var start))
                    return Descend(start, id[(dot + 1)..]);
            }

            return null;
        }

        if (target.StartsWith('.'))
            return Descend(source, target[1..]);
        var siblings = source.Parent?.Children ?? _rootStates;
        var first = target.Split('.', 2);
        var sibling = siblings.FirstOrDefault(c => c.Key == first[0]);
        return sibling is null ? null : first.Length == 1 ? sibling : Descend(sibling, first[1]);
    }

    private static StateInfo? Descend(StateInfo start, string path)
    {
        var current = start;
        foreach (var key in path.Split('.'))
        {
            current = current.Children.FirstOrDefault(c => c.Key == key);
            if (current is null)
                return null;
        }

        return current;
    }

    private string StateKey(string id) => _byXStateId.TryGetValue(id, out var s) ? s.Key : id;

    private string Delay(string key, string? carried, string pointer)
    {
        long? ms = long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        if (ms is null && _delays?[key] is JsonValue named && named.TryGetValue<long>(out var value))
            ms = value;
        if (carried is not null && (ms is null || XStateProjection.Milliseconds(carried) == ms))
            return carried;
        if (ms is not null)
            return XStateProjection.Duration(ms.Value);
        if (IsFunction(key))
            Warn("MQ9402", "The delay is an inline function; the transition waits the stub duration PT0S. Set the duration.", pointer);
        else
            Warn("MQ9404", "The named delay '" + key + "' has no value in the config; the transition waits the stub duration PT0S. Set the duration.", pointer);
        return "PT0S";
    }

    // ---- guards and actions ----

    private string? Guard(JsonNode guard, string pointer, string naming)
    {
        if (guard is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var text = v.GetValue<string>();
            return IsFunction(text) ? Stub(_guards, naming + "_guard", text, pointer) : _guards.Get(Identifier(text, pointer, "guard name"), this, "guard:" + text);
        }

        if (guard is JsonObject o && o["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String)
        {
            var type = t.GetValue<string>();
            if (Combinator(type) is { } op)
            {
                var expression = Combine(op, o);
                var name = Unique(_guards, naming + "_guard");
                var description = "Combines " + op + " of: " + string.Join(", ", Parts(o).Select(p => p?.ToJsonString() ?? "null")) + ".";
                Warn("MQ9404", "The guard combinator '" + type + "' became the guard '" + name + "'" +
                    (expression is null ? ", a stub because a part has no expression." : " combining the parts' expressions."), pointer);
                return _guards.Add(name, Derive("guard:" + name), expression, description, this);
            }

            var id = _guards.Get(Identifier(type, pointer + "/type", "guard name"), this, "guard:" + type);
            if (o.Count > 1)
                Opaque(pointer, o, "parameterized guard '" + type + "'");
            return id;
        }

        Opaque(pointer, guard, "guard that is neither a name nor a { type } object");
        return null;
    }

    private static string? Combinator(string type) => type switch
    {
        "and" or "xstate.and" => "and",
        "or" or "xstate.or" => "or",
        "not" or "xstate.not" => "not",
        _ => null,
    };

    private static IEnumerable<JsonNode?> Parts(JsonObject o) =>
        o["guards"] is JsonArray a ? a : o["guard"] is { } g ? [g] : [];

    /// <summary>The combined expression, or null when any part has none (the guard is then a stub).</summary>
    private string? Combine(string op, JsonObject o)
    {
        var parts = new List<string>();
        foreach (var part in Parts(o))
        {
            string? expression = part switch
            {
                JsonValue v when v.GetValueKind() == JsonValueKind.String && !IsFunction(v.GetValue<string>()) => _guards.Expression(v.GetValue<string>()),
                JsonObject p when p["type"]?.GetValue<string>() is { } type && Combinator(type) is { } inner => Combine(inner, p),
                JsonObject p when p["type"]?.GetValue<string>() is { } type && p.Count == 1 => _guards.Expression(type),
                _ => null,
            };
            if (expression is null)
                return null;
            parts.Add("(" + expression + ")");
        }

        if (parts.Count == 0)
            return null;
        return op switch
        {
            "and" => string.Join(" && ", parts),
            "or" => string.Join(" || ", parts),
            _ => "!(" + string.Join(" && ", parts) + ")",
        };
    }

    private JsonArray ActionRefs(JsonNode value, string pointer, string naming)
    {
        var ids = new JsonArray();
        foreach (var (item, itemPointer) in Entries(value, pointer))
        {
            switch (item)
            {
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    var text = v.GetValue<string>();
                    ids.Add(IsFunction(text) ? Stub(_actions, naming + "_action", text, itemPointer)
                        : _actions.Get(Identifier(text, itemPointer, "action name"), this, "action:" + text));
                    break;
                case JsonObject o when o["type"] is JsonValue t && t.GetValueKind() == JsonValueKind.String:
                    var type = t.GetValue<string>();
                    ids.Add(_actions.Get(Identifier(type, itemPointer + "/type", "action name"), this, "action:" + type));
                    if (o.Count > 1)
                        Opaque(itemPointer, o, "parameterized action '" + type + "'");
                    break;
                default:
                    var name = Unique(_actions, naming + "_action");
                    ids.Add(_actions.Add(name, Derive("action:" + name), null, null, this));
                    Opaque(itemPointer, item, "action that is neither a name nor a { type } object");
                    break;
            }
        }

        return ids;
    }

    private string Stub(Definitions definitions, string baseName, string text, string pointer)
    {
        var name = Unique(definitions, baseName);
        Warn("MQ9402", "An inline function became the named stub '" + name + "'; its text is in the description and never runs.", pointer);
        return definitions.Add(name, Derive(definitions.Kind + ":" + name), null, "```js\n" + text + "\n```", this);
    }

    private static string Unique(Definitions definitions, string baseName)
    {
        var name = Sanitize(baseName);
        for (var n = 2; definitions.Has(name); n++)
            name = Sanitize(baseName) + n.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    // ---- invokes ----

    private JsonObject? Invoke(JsonNode? item, string pointer, StateInfo s)
    {
        if (item is not JsonObject config)
        {
            Opaque(pointer, item, "invoke that is not an object");
            return null;
        }

        var meta = Meta(config, pointer);
        foreach (var (key, value) in config)
        {
            if (!InvokeKeys.Contains(key))
                Opaque(pointer + "/" + Escape(key), value, "invoke key '" + key + "'");
        }

        var src = config["src"] is JsonValue sv && sv.GetValueKind() == JsonValueKind.String ? sv.GetValue<string>() : null;
        var inline = src is not null && IsFunction(src);
        if (config["src"] is { } srcNode && src is null)
            Opaque(pointer + "/src", srcNode, "invoke source that is not a name");
        var xid = config["id"] is JsonValue iv && iv.GetValueKind() == JsonValueKind.String ? iv.GetValue<string>() : null;
        var baseName = xid ?? (src is not null && !inline ? src : Lower(s.Key) + "_invoke");
        var name = Sanitize(baseName);
        if (name != baseName)
            Warn("MQ9404", "The invoke '" + baseName + "' was renamed '" + name + "': names are identifiers.", pointer);
        for (var n = 2; _invokeNames.Contains(name); n++)
            name = Sanitize(baseName) + n.ToString(CultureInfo.InvariantCulture);
        _invokeNames.Add(name);
        var intoNode = _into.Invokes.GetValueOrDefault(s.Path + "/" + name);
        var id = Assign(Carried(meta), "invoke", pointer + "/meta/maquettiste/id", intoNode, "invoke:" + s.Path + "/" + name);
        var node = new JsonObject { ["id"] = id, ["name"] = name };
        var leftovers = meta is not null ? Without(meta, "id") : Pick(intoNode, ["displayName", "type", "process", "actors", "description"]);
        if (leftovers["type"] is null)
        {
            var process = src is null || inline ? null : options.Model?.All<Process>().FirstOrDefault(p => p.Name == src);
            leftovers["type"] = process is null ? "service" : "process";
            if (process is not null)
                leftovers["process"] = process.Id;
        }

        if (inline)
        {
            Warn("MQ9402", "The invoke source is an inline function; the invoke became the service stub '" + name + "'.", pointer + "/src");
            leftovers["description"] ??= "```js\n" + src + "\n```";
        }

        foreach (var (key, value) in leftovers)
            node[key] = value?.DeepClone();
        foreach (var (entry, entryPointer) in config["onDone"] is { } done ? Entries(done, pointer + "/onDone") : [])
            Transition(s, "invoke-done", name + "_done", node, entry, entryPointer);
        foreach (var (entry, entryPointer) in config["onError"] is { } error ? Entries(error, pointer + "/onError") : [])
            Transition(s, "invoke-error", name + "_error", node, entry, entryPointer);
        return node;
    }

    // ---- definitions, context, root ----

    private void LoadDefinitions(JsonObject? rootMeta)
    {
        foreach (var (definitions, key) in new[] { (_events, "events"), (_guards, "guards"), (_actions, "actions") })
        {
            if (rootMeta?[key] is not JsonArray list)
                continue;
            foreach (var (item, i) in list.Select((n, i) => (n, i)))
            {
                if (item is not JsonObject o || o["name"]?.GetValue<string>() is not { } name)
                    continue;
                var copy = (JsonObject)o.DeepClone();
                var pointer = "/meta/maquettiste/" + key + "/" + i.ToString(CultureInfo.InvariantCulture);
                copy["id"] = Assign(Carried(o), definitions.Kind, pointer + "/id", definitions.Into(_into, name), definitions.Kind + ":" + name);
                definitions.Put(name, copy);
            }
        }
    }

    private JsonArray Actions()
    {
        // raises: { type: "raise", event: name } entries (or plain ids) back to event ids.
        foreach (var action in _actions.All)
        {
            if (action["raises"] is not JsonArray raises)
                continue;
            action["raises"] = new JsonArray([.. raises.Select(r => (JsonNode)(r switch
            {
                JsonObject o when o["event"]?.GetValue<string>() is { } name => _events.Get(name, this, "event:" + name),
                JsonValue v when v.GetValueKind() == JsonValueKind.String && IdFormat.IsValid(v.GetValue<string>()) => v.GetValue<string>(),
                JsonValue v when v.GetValueKind() == JsonValueKind.String => _events.Get(v.GetValue<string>(), this, "event:" + v.GetValue<string>()),
                _ => "",
            })).Where(id => id.GetValue<string>().Length > 0)]);
        }

        return _actions.ToArray();
    }

    private JsonArray Context(JsonObject root, JsonObject? rootMeta)
    {
        var attributes = new List<JsonObject>();
        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (rootMeta?["context"] is JsonArray carried)
        {
            foreach (var (item, i) in carried.Select((n, i) => (n, i)))
            {
                if (item is not JsonObject o || o["name"]?.GetValue<string>() is not { } name)
                    continue;
                var copy = (JsonObject)o.DeepClone();
                copy["id"] = Assign(Carried(o), "attribute", "/meta/maquettiste/context/" + i.ToString(CultureInfo.InvariantCulture) + "/id",
                    _into.Context.GetValueOrDefault(name), "attribute:" + name);
                attributes.Add(copy);
                byName[name] = copy;
            }
        }

        var values = root["context"];
        if (values is not null and not JsonObject)
        {
            Opaque("/context", values, "context that is not an object");
            values = null;
        }

        foreach (var (name, value) in (JsonObject?)values ?? [])
        {
            if (!byName.TryGetValue(name, out var attribute))
            {
                var into = _into.Context.GetValueOrDefault(name);
                attribute = into is not null ? Without(into, "default") : new JsonObject { ["name"] = Identifier(name, "/context/" + Escape(name), "context name"), ["type"] = TypeOf(value) };
                attribute["id"] = Assign(null, "attribute", "/context/" + Escape(name), into, "attribute:" + name);
                attributes.Add(attribute);
                byName[name] = attribute;
            }

            if (value is not null && value.GetValueKind() != JsonValueKind.Null)
                attribute["default"] = value.DeepClone();
        }

        if (rootMeta?["context"] is null && _into.Root is not null)
        {
            var rank = IntoRank("context", "name");
            attributes = [.. attributes.OrderBy(a => rank.GetValueOrDefault(XStateExporter.Text(a, "name"), int.MaxValue))];
        }

        return new JsonArray([.. attributes]);
    }

    private static string TypeOf(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Number when value.AsValue().TryGetValue<long>(out var n) => n is >= int.MinValue and <= int.MaxValue ? "int32" : "int64",
        JsonValueKind.Number => "decimal",
        _ => "json",
    };

    private string ProcessName(JsonObject? rootMeta)
    {
        if (options.Name is not null)
            return options.Name;
        if (_machine.Length > 0)
            return Identifier(_machine, "/id", "machine id");
        return options.Into?.Name ?? rootMeta?["name"]?.GetValue<string>() ?? "ImportedProcess";
    }

    private IEnumerable<(string Key, JsonNode? Value)> RootLeftovers(JsonObject? rootMeta)
    {
        var skip = new HashSet<string>(["id", "name", "context", "events", "guards", "actions", "transitions", "initialImplied", "source", "states", "initial", "kind", "$schema"],
            StringComparer.Ordinal);
        var from = rootMeta ?? _into.Root;
        foreach (var (key, value) in from ?? [])
        {
            if (!skip.Contains(key))
                yield return (key, value?.DeepClone());
        }
    }

    /// <summary>Positions of the <c>into</c> process's nodes of one array, by a key field: nodes matched there keep their order.</summary>
    private Dictionary<string, int> IntoRank(string array, string field)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (node, i) in XStateExporter.Array(_into.Root!, array).OfType<JsonObject>().Select((n, i) => (n, i)))
            rank.TryAdd(XStateExporter.Text(node, field), i);
        return rank;
    }

    private JsonArray Ordered(JsonObject? rootMeta)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rootMeta?["transitions"] is JsonArray order)
        {
            foreach (var (id, i) in order.Select((n, i) => (n?.GetValue<string>() ?? "", i)))
                rank.TryAdd(id, i);
        }
        else if (_into.Root is not null)
            rank = IntoRank("transitions", "id");

        return new JsonArray([.. _transitions
            .OrderBy(t => rank.TryGetValue(t.Node["id"]!.GetValue<string>(), out var r) ? r : int.MaxValue)
            .ThenBy(t => t.Walk)
            .Select(t => (JsonNode)t.Node)]);
    }

    private JsonObject? Source(JsonObject? rootMeta)
    {
        var source = rootMeta is not null
            ? rootMeta["source"]?.DeepClone() as JsonObject
            : new JsonObject { ["format"] = XStateProjection.Format };
        if (rootMeta is null && _machine.Length > 0)
            source!["name"] = _machine;
        if (_extensions.Count > 0)
        {
            source ??= new JsonObject { ["format"] = XStateProjection.Format };
            var extensions = new JsonObject();
            foreach (var (pointer, value) in _extensions)
                extensions[pointer] = value?.DeepClone();
            source["extensions"] = extensions;
        }

        return source;
    }

    private void DropUnknownActors(JsonObject document)
    {
        if (options.Model is not { } model)
            return;
        void Filter(JsonObject? node, string key, string where)
        {
            if (node?[key] is not JsonArray list)
                return;
            var kept = new JsonArray();
            foreach (var item in list)
            {
                var id = item?.GetValue<string>() ?? "";
                if (model.Get<Actor>(id) is not null)
                    kept.Add(id);
                else
                    Warn("MQ9404", "Actor " + id + " of " + where + " is not in the model and was dropped: import never creates actors.", null);
            }

            node[key] = kept;
        }

        foreach (var ev in document["events"]!.AsArray().OfType<JsonObject>())
            Filter(ev, "actors", "event '" + ev["name"] + "'");
        foreach (var state in Walk(document["states"]!.AsArray()))
        {
            foreach (var invoke in (state["invoke"] as JsonArray ?? []).OfType<JsonObject>())
                Filter(invoke, "actors", "invoke '" + invoke["name"] + "'");
        }

        foreach (var t in document["transitions"]!.AsArray().OfType<JsonObject>())
        {
            Filter(t["gate"] as JsonObject, "signers", "a gate");
            Filter(t["gate"] as JsonObject, "requiredActors", "a gate");
        }
    }

    private static IEnumerable<JsonObject> Walk(JsonArray states)
    {
        foreach (var state in states.OfType<JsonObject>())
        {
            yield return state;
            foreach (var child in Walk(state["states"] as JsonArray ?? []))
                yield return child;
        }
    }

    private void CheckGate(JsonObject gate, string pointer)
    {
        if (gate["id"]?.GetValue<string>() is { } id && IdFormat.IsValid(id))
            gate["id"] = Assign(id, "gate", pointer + "/id", null, "gate:" + pointer);
    }

    // ---- ids ----

    /// <summary>The id of a node: the carried id when it is valid and free, else the matched <c>into</c> node's, else a derived one.</summary>
    private string Assign(string? carried, string kind, string pointer, JsonObject? intoNode, string path)
    {
        if (carried is not null && Free(carried, kind, pointer))
            return Use(carried, kind);
        if (intoNode?["id"]?.GetValue<string>() is { } matched && !_used.ContainsKey(matched))
            return Use(matched, kind);
        return Use(Derive(path), kind);
    }

    private bool Free(string id, string kind, string pointer)
    {
        if (_used.TryGetValue(id, out var usedAs))
        {
            Refuse("MQ9405", "Id " + id + " is carried by a " + usedAs + " and a " + kind + " of this config; the " + kind + " got a new id.", pointer);
            return false;
        }

        if (options.Model is not { } model)
            return true;
        if (model.TryGetEntry(id, out var entry))
        {
            if (entry.Kind != kind)
            {
                Refuse("MQ9405", "Id " + id + " names a " + entry.Kind + " of the model, not a " + kind + "; the " + kind + " got a new id.", pointer);
                return false;
            }

            if (entry.OwnerId != _processId)
            {
                Refuse("MQ9405", "Id " + id + " names a " + kind + " of another element (" + entry.OwnerId + "); import into that process, or remove the id to import a copy.", pointer);
                return false;
            }
        }
        else if (model.GetDocument(id) is not null)
        {
            Refuse("MQ9405", "Id " + id + " names an element of the model, not a " + kind + "; the " + kind + " got a new id.", pointer);
            return false;
        }

        return true;
    }

    private string Use(string id, string kind)
    {
        _used[id] = kind;
        return id;
    }

    /// <summary>An id derived from the import's seed and a path in the config: one path, one id, within one import.</summary>
    internal string Derive(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_seed + "\n" + path));
        var high = (ushort)((hash[0] << 8) | hash[1]);
        var low = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(2, 8));
        var id = IdFormat.Format(_seedTime, high, low);
        if (!_created.Contains(id))
            _created.Add(id);
        return id;
    }

    private static long DecodeTime(string ulid)
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        long time = 0;
        foreach (var c in ulid.AsSpan(0, 10))
            time = (time << 5) | (long)Alphabet.IndexOf(c, StringComparison.Ordinal);
        return time;
    }

    private static string? Carried(JsonObject? meta) =>
        meta?["id"] is JsonValue v && v.GetValueKind() == JsonValueKind.String && IdFormat.IsValid(v.GetValue<string>()) ? v.GetValue<string>() : null;

    // ---- helpers ----

    /// <summary>Reads <c>meta.maquettiste</c> of a node; other <c>meta</c> keys are kept opaque.</summary>
    private JsonObject? Meta(JsonObject node, string pointer)
    {
        if (node["meta"] is not { } meta)
            return null;
        if (meta is not JsonObject o)
        {
            Opaque(pointer + "/meta", meta, "meta that is not an object");
            return null;
        }

        foreach (var (key, value) in o)
        {
            if (key != XStateProjection.MetaKey)
                Opaque(pointer + "/meta/" + Escape(key), value, "meta key '" + key + "'");
        }

        return o[XStateProjection.MetaKey] as JsonObject;
    }

    private void Opaque(string pointer, JsonNode? value, string what)
    {
        _extensions[pointer] = value?.DeepClone();
        Warn("MQ9401", "The " + what + " has no model equivalent and was kept as opaque data in source.extensions (written back on export).", pointer);
    }

    private string Identifier(string name, string pointer, string what)
    {
        var sanitized = Sanitize(name);
        if (sanitized != name)
            Warn("MQ9404", "The " + what + " '" + name + "' was renamed '" + sanitized + "': names are identifiers.", pointer);
        return sanitized;
    }

    private static string Sanitize(string name)
    {
        var text = NonIdentifier().Replace(name, "_");
        return text.Length == 0 || char.IsAsciiDigit(text[0]) ? "_" + text : text;
    }

    private static string Lower(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static bool IsFunction(string text) => FunctionText().IsMatch(text);

    private static string Escape(string segment) => JsonPointer.Escape(segment);

    private static JsonObject Without(JsonObject node, params string[] keys)
    {
        var result = new JsonObject();
        foreach (var (key, value) in node)
        {
            if (!keys.Contains(key))
                result[key] = value?.DeepClone();
        }

        return result;
    }

    private static JsonObject Pick(JsonObject? node, string[] keys)
    {
        var result = new JsonObject();
        foreach (var (key, value) in node ?? [])
        {
            if (keys.Contains(key))
                result[key] = value?.DeepClone();
        }

        return result;
    }

    private void Warn(string rule, string message, string? pointer) => _diagnostics.Add(XStateProjection.Finding(rule, message, pointer));

    private void Refuse(string rule, string message, string? pointer) => _diagnostics.Add(XStateProjection.Finding(rule, message, pointer));

    private XStateImport Refused(string? message, string? pointer)
    {
        if (message is not null)
            Refuse("MQ9403", message, pointer);
        return new XStateImport(null, null, Sorted(), [], []);
    }

    private List<Diagnostic> Sorted() => _diagnostics;

    [GeneratedRegex("[^A-Za-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifier();

    [GeneratedRegex(@"^\s*(?:async\s+)?(?:function\b|(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>)", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionText();

    /// <summary>The events, guards or actions of the process, by name, in the order they are declared or first used.</summary>
    private sealed class Definitions(string kind)
    {
        private readonly Dictionary<string, JsonObject> _byName = new(StringComparer.Ordinal);
        private readonly List<JsonObject> _order = [];

        public string Kind => kind;

        public IEnumerable<JsonObject> All => _order;

        public bool Has(string name) => _byName.ContainsKey(name);

        public void Put(string name, JsonObject node)
        {
            if (_byName.TryAdd(name, node))
                _order.Add(node);
        }

        public JsonObject? Into(Into into, string name) => kind switch
        {
            "event" => into.Events.GetValueOrDefault(name),
            "guard" => into.Guards.GetValueOrDefault(name),
            _ => into.Actions.GetValueOrDefault(name),
        };

        /// <summary>The id of the definition of that name: declared, taken from <c>into</c>, or created as a stub.</summary>
        public string Get(string name, XStateImporter importer, string path)
        {
            if (_byName.TryGetValue(name, out var node))
                return node["id"]!.GetValue<string>();
            var into = Into(importer._into, name);
            var copy = into is not null ? (JsonObject)into.DeepClone() : new JsonObject { ["name"] = name };
            copy["id"] = importer.Assign(null, kind, "", into, path);
            Put(name, copy);
            return copy["id"]!.GetValue<string>();
        }

        public string Add(string name, string id, string? expression, string? description, XStateImporter importer)
        {
            importer._used[id] = kind;
            var node = new JsonObject { ["id"] = id, ["name"] = name };
            if (expression is not null)
                node["expression"] = expression;
            if (description is not null)
                node["description"] = description;
            Put(name, node);
            return id;
        }

        public string? Expression(string name) =>
            _byName.TryGetValue(name, out var node) ? node["expression"]?.GetValue<string>() : null;

        /// <summary>With <c>into</c> and no declared list: the positions of the definitions there, which keep their order.</summary>
        public Dictionary<string, int>? Rank { get; set; }

        public JsonArray ToArray() =>
            [.. (Rank is null ? _order : _order.OrderBy(n => Rank.GetValueOrDefault(XStateExporter.Text(n, "name"), int.MaxValue)).ToList()).Select(n => (JsonNode)n)];
    }

    /// <summary>The canonical nodes of the process re-imported over, by path or name.</summary>
    private sealed class Into
    {
        public JsonObject? Root { get; private set; }
        public Dictionary<string, JsonObject> States { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> Transitions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> Events { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> Guards { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> Actions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> Invokes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> Context { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);

        public void Load(ICanonicalJson json, Process process)
        {
            var root = JsonNode.Parse(json.Serialize(process, "process.json", XStateProjection.DefaultPath))!.AsObject();
            Root = root;
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            var invokeNames = new Dictionary<string, string>(StringComparer.Ordinal);
            void Visit(JsonArray states, string? parent)
            {
                foreach (var state in states.OfType<JsonObject>())
                {
                    var path = parent is null ? XStateExporter.Text(state, "name") : parent + "." + XStateExporter.Text(state, "name");
                    States[path] = state;
                    paths[XStateExporter.Text(state, "id")] = path;
                    Ids.Add(XStateExporter.Text(state, "id"));
                    foreach (var invoke in XStateExporter.Array(state, "invoke").OfType<JsonObject>())
                    {
                        Invokes[path + "/" + XStateExporter.Text(invoke, "name")] = invoke;
                        invokeNames[XStateExporter.Text(invoke, "id")] = XStateExporter.Text(invoke, "name");
                        Ids.Add(XStateExporter.Text(invoke, "id"));
                    }

                    Visit(XStateExporter.Array(state, "states"), path);
                }
            }

            Visit(XStateExporter.Array(root, "states"), null);
            foreach (var (map, key) in new[] { (Events, "events"), (Guards, "guards"), (Actions, "actions"), (Context, "context") })
            {
                foreach (var node in XStateExporter.Array(root, key).OfType<JsonObject>())
                {
                    map[XStateExporter.Text(node, "name")] = node;
                    Ids.Add(XStateExporter.Text(node, "id"));
                }
            }

            var eventNames = Events.Values.ToDictionary(e => XStateExporter.Text(e, "id"), e => XStateExporter.Text(e, "name"), StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in XStateExporter.Array(root, "transitions").OfType<JsonObject>())
            {
                var trigger = t["trigger"]?.GetValue<string>() ?? "event";
                var discriminator = trigger switch
                {
                    "event" => eventNames.GetValueOrDefault(XStateExporter.Text(t, "event"), ""),
                    "after" => XStateProjection.Milliseconds(t["after"]?.GetValue<string>())?.ToString(CultureInfo.InvariantCulture) ?? "",
                    "invoke-done" => invokeNames.GetValueOrDefault(XStateExporter.Text(t, "invoke"), "") + "_done",
                    "invoke-error" => invokeNames.GetValueOrDefault(XStateExporter.Text(t, "invoke"), "") + "_error",
                    _ => trigger,
                };
                var group = paths.GetValueOrDefault(XStateExporter.Text(t, "source"), "") + "|" + trigger + "|" + discriminator;
                var index = counts.GetValueOrDefault(group);
                counts[group] = index + 1;
                Transitions[group + "|" + index.ToString(CultureInfo.InvariantCulture)] = t;
                Ids.Add(XStateExporter.Text(t, "id"));
                if (t["gate"]?["id"]?.GetValue<string>() is { } gate)
                    Ids.Add(gate);
            }
        }
    }
}
