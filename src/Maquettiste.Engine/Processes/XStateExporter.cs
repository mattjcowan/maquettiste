using System.Globalization;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// Builds the XState config of one process from its canonical document (phase-3-design.md section 5.1). Every node is built in
/// <see cref="XStateProjection.KeyOrder"/>; what a node has with no XState home is copied, in canonical order, into its
/// <c>meta.maquettiste</c>.
/// </summary>
internal sealed class XStateExporter(JsonObject document, XStateExportOptions options)
{
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly Dictionary<string, string> _stateNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _eventNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _guardNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _actionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _invokeConfigs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _invokeStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonObject>> _bySource = new(StringComparer.Ordinal);

    public (JsonObject Config, IReadOnlyList<Diagnostic> Diagnostics) Run()
    {
        foreach (var state in Walk(Array(document, "states")))
            _stateNames[Text(state, "id")] = Text(state, "name");
        Names(_eventNames, "events");
        Names(_guardNames, "guards");
        Names(_actionNames, "actions");
        foreach (var t in Array(document, "transitions").OfType<JsonObject>())
        {
            var source = Text(t, "source");
            if (!_bySource.TryGetValue(source, out var list))
                _bySource[source] = list = [];
            list.Add(t);
        }

        var config = new JsonObject { ["id"] = Text(document, "name") };
        var implied = WriteInitial(config, document);
        config["states"] = States(Array(document, "states"));
        foreach (var t in _bySource.Values.SelectMany(l => l))
        {
            _diagnostics.Add(XStateProjection.Finding("MQ9404",
                "The transition's source is not a state of the process, so the export has no place for it.", null, Text(t, "id")));
        }

        var context = Array(document, "context").OfType<JsonObject>().ToList();
        if (context.Count > 0)
        {
            var values = new JsonObject();
            foreach (var attribute in context)
                values[Text(attribute, "name")] = attribute["default"]?.DeepClone();
            config["context"] = values;
        }

        var meta = new JsonObject();
        foreach (var (key, value) in document)
        {
            switch (key)
            {
                case "$schema" or "kind" or "name" or "initial" or "states":
                    break;
                case "context":
                    meta[key] = new JsonArray([.. context.Select(a => (JsonNode)Without(a, "default"))]);
                    break;
                case "actions":
                    meta[key] = new JsonArray([.. Array(document, "actions").OfType<JsonObject>().Select(Action)]);
                    break;
                case "transitions":
                    meta[key] = new JsonArray([.. Array(document, "transitions").OfType<JsonObject>().Select(t => (JsonNode)Text(t, "id"))]);
                    break;
                case "source":
                    var source = Without((JsonObject)value!, "extensions");
                    if (source.Count > 0)
                        meta[key] = source;
                    break;
                default:
                    meta[key] = value?.DeepClone();
                    break;
            }
        }

        if (implied)
            meta["initialImplied"] = true;
        foreach (var (pointer, value) in document["source"]?["extensions"] as JsonObject ?? [])
            Place(config, pointer, value);
        config["meta"] = Meta(config["meta"] as JsonObject, meta);
        _diagnostics.Add(XStateProjection.Finding("MQ9406",
            "Export wrote model data with no XState equivalent into meta.maquettiste: " + string.Join(", ", Carried()) + ".", "/meta/maquettiste",
            Text(document, "id")));
        return (config, _diagnostics);
    }

    private JsonObject States(JsonArray states)
    {
        var result = new JsonObject();
        foreach (var state in states.OfType<JsonObject>())
            result[Text(state, "name")] = State(state);
        return result;
    }

    private JsonObject State(JsonObject state)
    {
        var id = Text(state, "id");
        var type = state["type"]?.GetValue<string>() ?? "atomic";
        var children = Array(state, "states");
        var x = new JsonObject { ["id"] = id };
        var skip = new HashSet<string>(["id", "name", "states", "entry", "exit", "invoke"], StringComparer.Ordinal);
        string? metaType = null;
        var implied = false;
        if (type == "compound" && children.Count > 0)
        {
            implied = WriteInitial(x, state);
            skip.Add("initial");
        }

        if (children.Count > 0)
            x["states"] = States(children);
        switch (type)
        {
            case "parallel" or "final":
                x["type"] = type;
                skip.Add("type");
                break;
            case "history":
                x["type"] = type;
                x["history"] = state["history"]?.GetValue<string>() ?? "shallow";
                if (state["defaultTarget"] is { } target)
                    x["target"] = "#" + target.GetValue<string>();
                skip.UnionWith(["type", "history", "defaultTarget"]);
                break;
            case "compound" when children.Count > 0:
            case "atomic" when children.Count == 0:
                skip.Add("type");
                break;
            default:
                metaType = type; // choice, or a type the children contradict
                break;
        }

        if (Array(state, "entry").Count > 0)
            x["entry"] = Refs(Array(state, "entry"), _actionNames);
        if (Array(state, "exit").Count > 0)
            x["exit"] = Refs(Array(state, "exit"), _actionNames);
        var invokes = Array(state, "invoke").OfType<JsonObject>().Select(i => (Invoke: i, Config: Invoke(i, id))).ToList();
        Transitions(x, id);
        if (invokes.Count > 0)
            x["invoke"] = new JsonArray([.. invokes.Select(i => (JsonNode)i.Config)]);

        var meta = Leftovers(state, skip);
        if (metaType is not null && !meta.ContainsKey("type"))
            meta["type"] = metaType;
        if (implied)
            meta["initialImplied"] = true;
        if (meta.Count > 0)
            x["meta"] = new JsonObject { [XStateProjection.MetaKey] = meta };
        return x;
    }

    /// <summary>Writes <c>initial</c> of a compound node (the root or a state): the child named, or the first child when none is.</summary>
    /// <returns>Whether the initial was implied (the model names none).</returns>
    private bool WriteInitial(JsonObject x, JsonObject node)
    {
        var first = Array(node, "states").OfType<JsonObject>().FirstOrDefault();
        if (node["initial"] is { } initial && _stateNames.TryGetValue(initial.GetValue<string>(), out var name))
        {
            x["initial"] = name;
            return false;
        }

        if (first is null)
            return false;
        x["initial"] = Text(first, "name");
        return node["initial"] is null;
    }

    private void Transitions(JsonObject x, string stateId)
    {
        if (!_bySource.Remove(stateId, out var transitions))
            return;
        JsonObject? on = null, after = null;
        JsonArray? always = null, onDone = null;
        foreach (var t in transitions)
        {
            var entry = Transition(t);
            switch (t["trigger"]?.GetValue<string>() ?? "event")
            {
                case "event":
                    var ev = t["event"]?.GetValue<string>() ?? "";
                    Append(on ??= [], _eventNames.GetValueOrDefault(ev, ev), entry);
                    break;
                case "after":
                    var text = t["after"]?.GetValue<string>();
                    var key = XStateProjection.Milliseconds(text) is { } ms ? ms.ToString(CultureInfo.InvariantCulture) : text ?? "0";
                    Append(after ??= [], key, entry);
                    break;
                case "always":
                    (always ??= []).Add(entry);
                    break;
                case "done":
                    (onDone ??= []).Add(entry);
                    break;
                default: // invoke-done, invoke-error
                    var invoke = t["invoke"]?.GetValue<string>() ?? "";
                    if (_invokeStates.GetValueOrDefault(invoke) != stateId)
                    {
                        _diagnostics.Add(XStateProjection.Finding("MQ9404",
                            "The transition names no invoke of its source state, so the export has no place for it.", null, Text(t, "id")));
                        break;
                    }

                    var property = t["trigger"]!.GetValue<string>() == "invoke-done" ? "onDone" : "onError";
                    var target = _invokeConfigs[invoke];
                    if (target[property] is not JsonArray list)
                    {
                        list = [];
                        // onDone before onError, both before meta.
                        var meta = target["meta"];
                        target.Remove("meta");
                        var error = property == "onDone" ? target["onError"] : null;
                        if (error is not null)
                            target.Remove("onError");
                        target[property] = list;
                        if (error is not null)
                            target["onError"] = error;
                        if (meta is not null)
                            target["meta"] = meta;
                    }

                    list.Add(entry);
                    break;
            }
        }

        if (on is not null)
            x["on"] = on;
        if (after is not null)
            x["after"] = after;
        if (always is not null)
            x["always"] = always;
        if (onDone is not null)
            x["onDone"] = onDone;
    }

    private static void Append(JsonObject group, string key, JsonObject entry)
    {
        if (group[key] is not JsonArray list)
            group[key] = list = [];
        list.Add(entry);
    }

    private JsonObject Transition(JsonObject t)
    {
        var trigger = t["trigger"]?.GetValue<string>() ?? "event";
        var entry = new JsonObject();
        var targets = Array(t, "targets");
        if (targets.Count == 1)
            entry["target"] = "#" + targets[0]!.GetValue<string>();
        else if (targets.Count > 1)
            entry["target"] = new JsonArray([.. targets.Select(s => (JsonNode)("#" + s!.GetValue<string>()))]);
        if (t["guard"] is { } guard)
            entry["guard"] = _guardNames.GetValueOrDefault(guard.GetValue<string>(), guard.GetValue<string>());
        if (Array(t, "actions").Count > 0)
            entry["actions"] = Refs(Array(t, "actions"), _actionNames);
        if (t["external"]?.GetValue<bool>() == true)
            entry["reenter"] = true;
        var skip = new HashSet<string>(["source", "trigger", "guard", "targets", "actions", "external"], StringComparer.Ordinal);
        if (trigger == "event")
            skip.Add("event");
        if (trigger is "invoke-done" or "invoke-error")
            skip.Add("invoke");
        entry["meta"] = new JsonObject { [XStateProjection.MetaKey] = Leftovers(t, skip) };
        return entry;
    }

    private JsonObject Invoke(JsonObject invoke, string stateId)
    {
        var id = Text(invoke, "id");
        var name = Text(invoke, "name");
        var src = name;
        if (invoke["type"]?.GetValue<string>() == "process" && invoke["process"]?.GetValue<string>() is { } process)
            src = options.ProcessName?.Invoke(process) ?? process;
        var config = new JsonObject
        {
            ["id"] = name,
            ["src"] = src,
            ["meta"] = new JsonObject { [XStateProjection.MetaKey] = Leftovers(invoke, new HashSet<string>(["name"], StringComparer.Ordinal)) },
        };
        _invokeConfigs[id] = config;
        _invokeStates[id] = stateId;
        return config;
    }

    /// <summary>An action definition for <c>meta</c>: its <c>raises</c> become <c>{ type: "raise", event: name }</c> entries.</summary>
    private JsonNode Action(JsonObject action)
    {
        var copy = (JsonObject)action.DeepClone();
        if (copy["raises"] is JsonArray raises)
        {
            copy["raises"] = new JsonArray([.. raises.Select(r => (JsonNode)new JsonObject
            {
                ["type"] = "raise",
                ["event"] = _eventNames.GetValueOrDefault(r!.GetValue<string>(), r.GetValue<string>()),
            })]);
        }

        return copy;
    }

    /// <summary>Writes one kept subtree back at its pointer: an existing value is replaced, a missing key is added before <c>meta</c>.</summary>
    private void Place(JsonObject config, string pointer, JsonNode? value)
    {
        if (!JsonPointer.TryParse(pointer, out var segments) || segments.Length == 0)
        {
            Unplaced(pointer);
            return;
        }

        JsonNode current = config;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var next = current switch
            {
                JsonObject o when o[segments[i]] is { } child => child,
                JsonObject o when segments[i] == "meta" => o["meta"] = new JsonObject(),
                JsonArray a when JsonPointer.TryIndex(segments[i], out var index) && index < a.Count => a[index],
                _ => null,
            };
            if (next is null)
            {
                Unplaced(pointer);
                return;
            }

            current = next;
        }

        var last = segments[^1];
        switch (current)
        {
            case JsonObject o:
                if (o.ContainsKey(last))
                {
                    o[last] = value?.DeepClone();
                    break;
                }

                var meta = last != "meta" && o.ContainsKey("meta") ? o["meta"] : null;
                if (meta is not null)
                    o.Remove("meta");
                o[last] = value?.DeepClone();
                if (meta is not null)
                    o["meta"] = meta;
                break;
            case JsonArray a when JsonPointer.TryIndex(last, out var index) && index <= a.Count:
                if (index == a.Count)
                    a.Add(value?.DeepClone());
                else
                    a[index] = value?.DeepClone();
                break;
            default:
                Unplaced(pointer);
                break;
        }
    }

    private void Unplaced(string pointer) =>
        _diagnostics.Add(XStateProjection.Finding("MQ9404",
            "Kept XState data at " + pointer + " has no place in the exported config any more (its state or transition changed), so it was dropped.",
            pointer, Text(document, "id")));

    private static JsonObject Meta(JsonObject? existing, JsonObject maquettiste)
    {
        var meta = new JsonObject { [XStateProjection.MetaKey] = maquettiste };
        foreach (var (key, value) in existing ?? [])
        {
            if (key != XStateProjection.MetaKey)
                meta[key] = value?.DeepClone();
        }

        return meta;
    }

    private IEnumerable<string> Carried()
    {
        yield return "ids";
        var all = Walk(Array(document, "states")).ToList();
        if (document["displayName"] is not null || all.Any(s => s["displayName"] is not null) || Array(document, "transitions").OfType<JsonObject>().Any(t => t["displayName"] is not null))
            yield return "display names";
        if (all.Any(s => s["type"]?.GetValue<string>() == "choice"))
            yield return "choice states";
        if (document["use"] is not null || document["subject"] is not null || document["package"] is not null)
            yield return "package, use and subject";
        if (Array(document, "context").Count > 0)
            yield return "context types";
        if (Array(document, "events").Count > 0)
            yield return "events with payloads and actors";
        if (Array(document, "guards").Count > 0 || Array(document, "actions").Count > 0)
            yield return "guard and action definitions";
        var transitions = Array(document, "transitions").OfType<JsonObject>().ToList();
        if (transitions.Any(t => t["gate"] is not null))
            yield return "gates";
        if (transitions.Any(t => t["after"] is not null))
            yield return "ISO durations";
        if (all.Any(s => Array(s, "invoke").Count > 0))
            yield return "invoke kinds";
        if (Array(document, "actions").OfType<JsonObject>().Any(a => Array(a, "raises").Count > 0))
            yield return "raises";
    }

    private static IEnumerable<JsonObject> Walk(JsonArray states)
    {
        foreach (var state in states.OfType<JsonObject>())
        {
            yield return state;
            foreach (var child in Walk(Array(state, "states")))
                yield return child;
        }
    }

    private void Names(Dictionary<string, string> map, string key)
    {
        foreach (var node in Array(document, key).OfType<JsonObject>())
            map[Text(node, "id")] = Text(node, "name");
    }

    private static JsonArray Refs(JsonArray ids, Dictionary<string, string> names) =>
        [.. ids.Select(i => (JsonNode)names.GetValueOrDefault(i!.GetValue<string>(), i.GetValue<string>()))];

    private static JsonObject Leftovers(JsonObject node, HashSet<string> skip)
    {
        var result = new JsonObject();
        foreach (var (key, value) in node)
        {
            if (!skip.Contains(key))
                result[key] = value?.DeepClone();
        }

        return result;
    }

    private static JsonObject Without(JsonObject node, string key) => Leftovers(node, new HashSet<string>([key], StringComparer.Ordinal));

    internal static JsonArray Array(JsonObject node, string key) => node[key] as JsonArray ?? [];

    internal static string Text(JsonObject node, string key) => node[key]?.GetValue<string>() ?? "";
}
