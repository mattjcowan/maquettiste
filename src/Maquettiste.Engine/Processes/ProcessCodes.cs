using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>The kind of a lifecycle's state codes (lifecycle-design.md decision 1).</summary>
internal enum CodeKind
{
    /// <summary>No state has a code.</summary>
    None,

    /// <summary>Every code is a string.</summary>
    String,

    /// <summary>Every code is an integer.</summary>
    Integer,

    /// <summary>Strings and integers mixed (MQ9206).</summary>
    Mixed,
}

/// <summary>A state that carries a code, with where it sits.</summary>
/// <param name="State">The state.</param>
/// <param name="Pointer">The JSON pointer of the state in the process file.</param>
/// <param name="Key">The code's key (<see cref="ReferenceDataIndex.CodeKey"/>): <c>1</c> and <c>"1"</c> are one code.</param>
/// <param name="IsInteger">Whether the code is written as an integer.</param>
/// <param name="CodedAncestor">The nearest proper ancestor that carries a code (history and choice states never count), or <see langword="null"/>.</param>
/// <param name="UnderParallel">Whether a proper ancestor is a parallel state, so the state sits in one of its regions.</param>
internal sealed record CodedState(ProcessState State, string Pointer, string Key, bool IsInteger, ProcessState? CodedAncestor, bool UnderParallel);

/// <summary>
/// The state codes of one process (lifecycle-design.md decisions 1 to 4): which states carry one, whether they clash, their kind, and
/// whether they form a map, so that any configuration has at most one code. Rules, resolution and packs read codes through it. A pure
/// function of the process record.
/// </summary>
internal sealed class ProcessCodes
{
    private readonly Dictionary<string, CodedState> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessState?> _effective = new(StringComparer.Ordinal);

    private ProcessCodes(Process process)
    {
        var coded = new List<CodedState>();
        Walk(process.States, "", null, false, coded);
        Coded = coded;

        var first = coded.FirstOrDefault(c => !IsRestless(c.State));
        var settled = coded.Where(c => !IsRestless(c.State)).ToList();
        Kind = settled.Count == 0 ? CodeKind.None
            : settled.All(c => c.IsInteger) ? CodeKind.Integer
            : settled.All(c => !c.IsInteger) ? CodeKind.String
            : CodeKind.Mixed;
        OtherKind = first is null ? [] : [.. settled.Where(c => c.IsInteger != first.IsInteger)];
        OnHistoryOrChoice = [.. coded.Where(c => IsRestless(c.State))];

        var duplicates = new List<(CodedState First, CodedState Second)>();
        var byKey = new Dictionary<string, CodedState>(StringComparer.Ordinal);
        foreach (var c in settled)
        {
            if (!byKey.TryAdd(c.Key, c))
                duplicates.Add((byKey[c.Key], c));
        }

        Duplicates = duplicates;
        IsMap = settled.Count > 0 && settled.All(c => c.CodedAncestor is null && !c.UnderParallel);
        IsSound = OtherKind.Count == 0 && OnHistoryOrChoice.Count == 0 && Duplicates.Count == 0;
    }

    /// <summary>Every state that carries a code, in document order (pre-order).</summary>
    public IReadOnlyList<CodedState> Coded { get; }

    /// <summary>The codes' kind; history and choice states, which may not carry one, are left out.</summary>
    public CodeKind Kind { get; }

    /// <summary>The coded states whose code is not of the first code's kind (MQ9206), in document order.</summary>
    public IReadOnlyList<CodedState> OtherKind { get; }

    /// <summary>The history and choice states that carry a code (MQ9206): nothing rests in them.</summary>
    public IReadOnlyList<CodedState> OnHistoryOrChoice { get; }

    /// <summary>Each state whose code an earlier state already carries (MQ9206), with that earlier state; history and choice states are left out.</summary>
    public IReadOnlyList<(CodedState First, CodedState Second)> Duplicates { get; }

    /// <summary>
    /// Whether the codes form a map (decision 4): some state carries a code, no two coded states sit on one path from the root, and no
    /// coded state sits inside a parallel region. History and choice states are left out.
    /// </summary>
    public bool IsMap { get; }

    /// <summary>Whether the codes break none of the rules that hold with or without a binding (MQ9206): unique, of one kind, none on a history or choice state.</summary>
    public bool IsSound { get; }

    /// <summary>The codes of a process, from its record.</summary>
    /// <param name="process">The process.</param>
    /// <returns>The codes.</returns>
    public static ProcessCodes Of(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new ProcessCodes(process);
    }

    /// <summary>The state that gives a state its code in a configuration: itself or its nearest coded ancestor, or <see langword="null"/>.</summary>
    /// <param name="stateId">The state id.</param>
    /// <returns>The coded state, or <see langword="null"/> when no state on its path carries a code.</returns>
    public ProcessState? CodedStateOf(string stateId) => _effective.GetValueOrDefault(stateId);

    /// <summary>A state's own code record, or <see langword="null"/>.</summary>
    /// <param name="stateId">The state id.</param>
    /// <returns>The record.</returns>
    public CodedState? Find(string stateId) => _byId.GetValueOrDefault(stateId);

    /// <summary>A code as templates read it: a string, a <see cref="long"/>, or <see langword="null"/> for anything else.</summary>
    /// <param name="code">The code as the file writes it.</param>
    /// <returns>The value.</returns>
    public static object? Value(JsonElement? code) => code switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var value) => value,
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null,
    };

    /// <summary>A code as messages write it: a string quoted, an integer as is.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The text.</returns>
    public static string Text(JsonElement code) => code.ValueKind == JsonValueKind.String ? "'" + code.GetString() + "'" : code.GetRawText();

    private static bool IsRestless(ProcessState state) => state.Type is StateType.History or StateType.Choice;

    private void Walk(IReadOnlyList<ProcessState> states, string pointer, ProcessState? codedAncestor, bool underParallel, List<CodedState> coded)
    {
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var at = pointer + "/states/" + i.ToString(CultureInfo.InvariantCulture);
            var nearest = codedAncestor;
            if (state.Code is { } code && ReferenceDataIndex.CodeKey(code) is { } key)
            {
                var record = new CodedState(state, at, key, code.ValueKind == JsonValueKind.Number, codedAncestor, underParallel);
                coded.Add(record);
                _byId.TryAdd(state.Id, record);
                if (!IsRestless(state))
                    nearest = state;
            }

            _effective.TryAdd(state.Id, nearest);
            Walk(state.States, at, nearest, underParallel || state.Type == StateType.Parallel, coded);
        }
    }
}
