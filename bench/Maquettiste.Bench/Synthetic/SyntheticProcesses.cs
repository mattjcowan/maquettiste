using System.Globalization;
using System.Text.Json.Nodes;

namespace Maquettiste.Bench.Synthetic;

/// <summary>The size of the synthetic process extension (phase-3-design.md section 4.5).</summary>
public sealed record SyntheticProcessOptions
{
    /// <summary>Processes in all.</summary>
    public int Processes { get; init; } = 1_000;

    /// <summary>States of an ordinary process.</summary>
    public int States { get; init; } = 40;

    /// <summary>How many of the processes have <see cref="LargeStates"/> states.</summary>
    public int Large { get; init; } = 5;

    /// <summary>States of a large process.</summary>
    public int LargeStates { get; init; } = 400;

    /// <summary>Scenarios in all, spread over the ordinary processes.</summary>
    public int Scenarios { get; init; } = 5_000;

    /// <summary>Steps per scenario.</summary>
    public int Steps { get; init; } = 20;
}

/// <summary>A generated process: its id, name, the id of its <c>next</c> event and its atomic states in the order <c>next</c> walks them.</summary>
/// <param name="Id">The process id.</param>
/// <param name="Name">The process name.</param>
/// <param name="StateCount">States, compound states included (the final state not).</param>
/// <param name="Next">The <c>next</c> event id.</param>
/// <param name="Reset">The <c>reset</c> event id.</param>
/// <param name="Guard">The guard id (ordinary processes only).</param>
/// <param name="Leaves">The atomic states in walk order.</param>
public sealed record SyntheticProcess(string Id, string Name, int StateCount, string Next, string Reset, string? Guard, IReadOnlyList<string> Leaves);

/// <summary>
/// Processes and scenarios for the bench (phase-3-design.md section 4.5), deterministic for a given size. A process is a row of
/// compound states, each holding a chain of atomic states, then a final state: <c>next</c> walks the chain (from a compound's last
/// child into the next compound, whose initial child it enters), <c>reset</c> on each compound returns to its first child and runs an
/// action (<c>{ count: context.count + 1 }</c>). An ordinary process guards its first <c>next</c> with <c>context.count &gt;= 0</c>; the
/// large ones have no expression on the <c>next</c> path. A scenario walks <c>next</c> from the start and expects each atomic state.
/// </summary>
public static class SyntheticProcesses
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Writes the processes (and, when <paramref name="scenarios"/>, the scenarios) under the model folder.</summary>
    /// <param name="repoRoot">The repo root.</param>
    /// <param name="options">The size.</param>
    /// <param name="scenarios">Whether to write the scenarios.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The processes, large ones first.</returns>
    public static async Task<IReadOnlyList<SyntheticProcess>> WriteAsync(string repoRoot, SyntheticProcessOptions options, bool scenarios, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        var writer = new RepoWriter(repoRoot);
        ArgumentNullException.ThrowIfNull(options);
        var processes = new List<SyntheticProcess>();
        for (var p = 0; p < options.Processes; p++)
        {
            var large = p < options.Large;
            var (node, process) = Build(p, large ? options.LargeStates : options.States, !large);
            processes.Add(process);
            await writer.WriteNodeAsync(node, "process.json", $"model/processes/{Stem(process)}.json", ct).ConfigureAwait(false);
        }

        if (scenarios)
        {
            var ordinary = processes.Where(p => p.Guard is not null).ToList();
            for (var s = 0; s < options.Scenarios && ordinary.Count > 0; s++)
            {
                var process = ordinary[s % ordinary.Count];
                var node = Scenario(process, s, options.Steps);
                await writer.WriteNodeAsync(node, "scenario.json", $"model/scenarios/{Stem(process)}/run{s.ToString("D5", CultureInfo.InvariantCulture)}.json", ct).ConfigureAwait(false);
            }
        }

        return processes;
    }

    /// <summary>A process document and its description.</summary>
    /// <param name="index">The process index (its ids derive from it).</param>
    /// <param name="states">The state count, compound states included.</param>
    /// <param name="guarded">Whether the first <c>next</c> is guarded.</param>
    /// <returns>The document and the process.</returns>
    public static (JsonObject Node, SyntheticProcess Process) Build(int index, int states, bool guarded)
    {
        var counter = 0L;
        string Id() => NewId(index, counter++);
        var id = Id();
        var name = "Flow" + index.ToString("D4", CultureInfo.InvariantCulture);
        // k compound states with m atomic children each: k + k * m >= states, k about sqrt(states) (400 gives 20 x 19).
        var k = Math.Max(1, (int)Math.Round(Math.Sqrt(states)));
        var m = Math.Max(1, (states - k + k - 1) / k);
        var next = Id();
        var reset = Id();
        var counterAttribute = Id();
        var guard = guarded ? Id() : null;
        var action = Id();
        var compounds = new List<(string Id, List<string> Children)>();
        var stateNodes = new JsonArray();
        for (var c = 0; c < k; c++)
        {
            var compoundId = Id();
            var children = new List<string>();
            var childNodes = new JsonArray();
            for (var j = 0; j < m; j++)
            {
                var childId = Id();
                children.Add(childId);
                childNodes.Add(new JsonObject { ["id"] = childId, ["name"] = "Step" + j.ToString("D3", CultureInfo.InvariantCulture) });
            }

            compounds.Add((compoundId, children));
            stateNodes.Add(new JsonObject
            {
                ["id"] = compoundId, ["name"] = "Stage" + c.ToString("D3", CultureInfo.InvariantCulture), ["type"] = "compound", ["initial"] = children[0], ["states"] = childNodes,
            });
        }

        var done = Id();
        stateNodes.Add(new JsonObject { ["id"] = done, ["name"] = "Done", ["type"] = "final" });
        var transitions = new JsonArray();
        for (var c = 0; c < k; c++)
        {
            var (compoundId, children) = compounds[c];
            for (var j = 0; j < children.Count; j++)
            {
                var target = j + 1 < children.Count ? children[j + 1] : c + 1 < k ? compounds[c + 1].Id : done;
                var t = new JsonObject { ["id"] = Id(), ["source"] = children[j], ["event"] = next, ["targets"] = new JsonArray(target) };
                if (guard is not null && c == 0 && j == 0)
                    t["guard"] = guard;
                transitions.Add(t);
            }

            transitions.Add(new JsonObject { ["id"] = Id(), ["source"] = compoundId, ["event"] = reset, ["targets"] = new JsonArray(children[0]), ["actions"] = new JsonArray(action) });
        }

        var node = new JsonObject
        {
            ["kind"] = "process",
            ["id"] = id,
            ["name"] = name,
            ["context"] = new JsonArray(new JsonObject { ["id"] = counterAttribute, ["name"] = "count", ["type"] = "int32", ["default"] = 0 }),
            ["events"] = new JsonArray(new JsonObject { ["id"] = next, ["name"] = "next" }, new JsonObject { ["id"] = reset, ["name"] = "reset" }),
        };
        if (guard is not null)
            node["guards"] = new JsonArray(new JsonObject { ["id"] = guard, ["name"] = "counted", ["expression"] = "context.count >= 0" });
        node["actions"] = new JsonArray(new JsonObject { ["id"] = action, ["name"] = "countReset", ["expression"] = "({ count: context.count + 1 })" });
        node["states"] = stateNodes;
        node["initial"] = compounds[0].Id;
        node["transitions"] = transitions;
        var leaves = compounds.SelectMany(c => c.Children).ToList();
        return (node, new SyntheticProcess(id, name, k + (k * m), next, reset, guard, leaves));
    }

    /// <summary>A scenario that walks <c>next</c> from the start of a process.</summary>
    /// <param name="process">The process.</param>
    /// <param name="index">The scenario index.</param>
    /// <param name="steps">The step count (at most the atomic states less one).</param>
    /// <returns>The document.</returns>
    public static JsonObject Scenario(SyntheticProcess process, int index, int steps)
    {
        ArgumentNullException.ThrowIfNull(process);
        var counter = 0L;
        string Id() => NewId(1_000_000 + index, counter++);
        var stepNodes = new JsonArray();
        for (var i = 0; i < Math.Min(steps, process.Leaves.Count - 1); i++)
        {
            stepNodes.Add(new JsonObject
            {
                ["id"] = Id(), ["event"] = process.Next, ["expect"] = new JsonObject { ["states"] = new JsonArray(process.Leaves[i + 1]) },
            });
        }

        return new JsonObject
        {
            ["kind"] = "scenario", ["id"] = Id(), ["name"] = "Run" + index.ToString("D5", CultureInfo.InvariantCulture), ["process"] = process.Id, ["steps"] = stepNodes,
        };
    }

    /// <summary>The inputs of a <c>simulate</c> call: <c>next</c> and <c>reset</c> alternating in runs, so the process never ends.</summary>
    /// <param name="process">The process.</param>
    /// <param name="count">The input count.</param>
    /// <returns>The inputs.</returns>
    public static JsonArray Inputs(SyntheticProcess process, int count)
    {
        ArgumentNullException.ThrowIfNull(process);
        var inputs = new JsonArray();
        for (var i = 0; i < count; i++)
            inputs.Add(new JsonObject { ["event"] = i % 5 == 4 ? process.Reset : process.Next });
        return inputs;
    }

    private static string Stem(SyntheticProcess process) => process.Name.ToLowerInvariant();

    // A ULID-shaped id: "01K" then the owner and a counter in Crockford base 32 (23 characters).
    private static string NewId(long owner, long counter)
    {
        var chars = new char[23];
        var value = (owner * 1_000_000L) + counter;
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            chars[i] = Crockford[(int)(value % 32)];
            value /= 32;
        }

        return "01K" + new string(chars);
    }
}
