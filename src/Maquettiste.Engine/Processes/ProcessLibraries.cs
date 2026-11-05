using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// What a process takes from library processes (next-phase-2-design.md section 4.4): a library holds guards, actions and gate policies once,
/// and any process names them by id. Whatever runs a chart reads <see cref="Effective"/>, so the chart, the expressions, the interpreter and
/// the exporter see one process with its library members in place. A process that names no library member is its own effective process.
/// </summary>
public static class ProcessLibraries
{
    private static readonly ConditionalWeakTable<ModelSnapshot, PerModel> ByModel = new();

    /// <summary>
    /// The process as it runs: its own guards and actions, then the library members it names in order of first use, with each
    /// <c>gatePolicy</c> in place of an inline gate. Library expressions so read the using process's context and event.
    /// </summary>
    /// <param name="model">The model that holds the process.</param>
    /// <param name="process">The process.</param>
    /// <returns>The effective process; <paramref name="process"/> itself when it names no library member.</returns>
    public static Process Effective(ModelSnapshot model, Process process)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        var known = ByModel.GetValue(model, static m => new PerModel(m));
        if (!known.HasLibraries || process.Use == ProcessUse.Library)
            return process;
        // A process outside the model (a draft) is not the model's to cache.
        return ReferenceEquals(model.Get<Process>(process.Id), process)
            ? known.Effective.GetOrAdd(process.Id, static (_, a) => Build(a.Model, a.Process), (Model: model, Process: process))
            : Build(model, process);
    }

    /// <summary>The chart of a process with its library members, keyed by the hashes of the process and of the libraries it names.</summary>
    /// <param name="model">The model that holds the process.</param>
    /// <param name="process">The process.</param>
    /// <returns>The chart.</returns>
    public static StatechartModel Chart(ModelSnapshot model, Process process)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        var hash = model.GetDocument(process.Id)?.Hash;
        var effective = Effective(model, process);
        if (hash is null || ReferenceEquals(effective, process))
            return StatechartModel.Get(effective, hash);
        var libraries = UsesOf(model, process).Select(u => u.Library.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(id => model.GetDocument(id)?.Hash);
        return StatechartModel.Get(effective, hash + "|" + string.Join('|', libraries));
    }

    /// <summary>The library members a process names, once each, in order of first use: transitions in priority order, then states' entry and exit.</summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>The uses; empty for a library or when it names none.</returns>
    internal static IReadOnlyList<LibraryUse> UsesOf(ModelSnapshot model, Process process)
    {
        if (process.Use == ProcessUse.Library || !ByModel.GetValue(model, static m => new PerModel(m)).HasLibraries)
            return [];
        var uses = new List<LibraryUse>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Named(string id, string kind, string pointer)
        {
            if (Owner(model, id, kind) is not { } library || !seen.Add(id))
                return;
            uses.Add(kind switch
            {
                "guard" => new LibraryUse(pointer, library, library.Guards.First(g => g.Id == id), null, null),
                "action" => new LibraryUse(pointer, library, null, library.Actions.First(a => a.Id == id), null),
                _ => new LibraryUse(pointer, library, null, null, library.Gates.First(g => g.Id == id)),
            });
        }

        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var t = process.Transitions[i];
            var at = "/transitions/" + N(i);
            if (t.Guard is { } guard)
                Named(guard, "guard", at + "/guard");
            for (var j = 0; j < t.Actions.Count; j++)
                Named(t.Actions[j], "action", at + "/actions/" + N(j));
            if (t.GatePolicy is { } policy)
                Named(policy, "gate", at + "/gatePolicy");
        }

        void Walk(IReadOnlyList<ProcessState> states, string pointer)
        {
            for (var i = 0; i < states.Count; i++)
            {
                var at = pointer + "/states/" + N(i);
                for (var j = 0; j < states[i].Entry.Count; j++)
                    Named(states[i].Entry[j], "action", at + "/entry/" + N(j));
                for (var j = 0; j < states[i].Exit.Count; j++)
                    Named(states[i].Exit[j], "action", at + "/exit/" + N(j));
                Walk(states[i].States, at);
            }
        }

        Walk(process.States, "");
        return uses;
    }

    /// <summary>The library that holds a guard, action or gate, or <see langword="null"/> when the id names no such member of a library.</summary>
    /// <param name="model">The model.</param>
    /// <param name="id">The member's id.</param>
    /// <param name="kind">Its index kind: <c>guard</c>, <c>action</c> or <c>gate</c>.</param>
    /// <returns>The library.</returns>
    internal static Process? Owner(ModelSnapshot model, string id, string kind)
    {
        if (!model.TryGetEntry(id, out var entry) || !string.Equals(entry.Kind, kind, StringComparison.Ordinal)
            || model.Get<Process>(entry.OwnerId) is not { Use: ProcessUse.Library } library)
            return null;
        // A library that holds a transition's gate or an event's parts is MQ9020, and those are not members to name.
        var held = kind switch
        {
            "guard" => library.Guards.Any(g => string.Equals(g.Id, id, StringComparison.Ordinal)),
            "action" => library.Actions.Any(a => string.Equals(a.Id, id, StringComparison.Ordinal)),
            _ => library.Gates.Any(g => string.Equals(g.Id, id, StringComparison.Ordinal)),
        };
        return held ? library : null;
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static Process Build(ModelSnapshot model, Process process)
    {
        var uses = UsesOf(model, process);
        if (uses.Count == 0)
            return process;
        var gates = uses.Where(u => u.Gate is not null).ToDictionary(u => u.Gate!.Id, u => u.Gate!, StringComparer.Ordinal);
        return process with
        {
            Guards = [.. process.Guards, .. uses.Select(u => u.Guard).OfType<ProcessGuard>()],
            Actions = [.. process.Actions, .. uses.Select(u => u.Action).OfType<ProcessAction>()],
            Transitions = gates.Count == 0
                ? process.Transitions
                : [.. process.Transitions.Select(t => t.Gate is null && t.GatePolicy is { } policy && gates.TryGetValue(policy, out var gate) ? t with { Gate = gate, GatePolicy = null } : t)],
        };
    }

    private sealed class PerModel(ModelSnapshot model)
    {
        public bool HasLibraries { get; } = model.All<Process>().Any(static p => p.Use == ProcessUse.Library);

        public ConcurrentDictionary<string, Process> Effective { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>One library member a process names.</summary>
/// <param name="Pointer">The pointer of the first place in the process that names it.</param>
/// <param name="Library">The library that holds it.</param>
/// <param name="Guard">The guard, when it is one.</param>
/// <param name="Action">The action, when it is one.</param>
/// <param name="Gate">The gate, when it is one.</param>
internal readonly record struct LibraryUse(string Pointer, Process Library, ProcessGuard? Guard, ProcessAction? Action, ProcessGate? Gate);
