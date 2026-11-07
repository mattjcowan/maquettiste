using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>Verbs (verbs-design.md section 6): <c>model.verbs</c>, and the verb of each event, transition and operation.</summary>
internal sealed partial class ResolveRun
{
    private const string VerbVocabularyKind = "k:verb-vocabulary";

    /// <summary>
    /// Resolves <c>model.verbs</c> and sets <see cref="REvent.Verb"/>, <see cref="RTransition.Verb"/> (its own, else its event's) and
    /// <see cref="ROperation.Verb"/>, after the processes and operations. The list is empty unless the project uses verbs (decision 19). Its
    /// keys, and each verb's, are the vocabulary's and <see cref="ModelSnapshot.VerbNamingKey"/> (which verb keys are named anywhere): what
    /// it holds, and whether a verb is used, depend on nothing else, so editing a process or an operation in other ways leaves them. A
    /// verb's events, transitions and operations key on every process and operation file; only a template that reads them pays. A use that
    /// names a verb also depends on the vocabulary, which decides what its key names.
    /// </summary>
    private RList<RVerb> ResolveVerbs()
    {
        var vocabulary = Model.Verbs;
        var listKeys = new DependencySet(Keys).Add(VerbVocabularyKind).Add(ModelSnapshot.VerbNamingKey);
        if (vocabulary is not null)
            listKeys.Element(vocabulary.Id);
        var keys = listKeys.ToList();

        var events = new List<(REvent Node, string Key)>();
        var transitions = new List<(RTransition Node, string Key)>();
        var operations = new List<(ROperation Node, string Key)>();
        foreach (var process in _processOrder.Concat(_libraryOrder))
        {
            if (Model.Get<Process>(process.Id) is not { } source)
                continue;
            var eventVerbs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ev in source.Events)
            {
                if (ev.Verb is { Length: > 0 } key)
                    eventVerbs.TryAdd(ev.Id, key);
            }

            var transitionVerbs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in source.Transitions)
            {
                if (t.Verb is { Length: > 0 } key)
                    transitionVerbs.TryAdd(t.Id, key);
            }

            foreach (var ev in process.Events)
            {
                if (eventVerbs.TryGetValue(ev.Id, out var key))
                    events.Add((ev, key));
            }

            foreach (var t in process.Transitions)
            {
                if (transitionVerbs.TryGetValue(t.Id, out var key) || (t.Event is { } ev && eventVerbs.TryGetValue(ev.Id, out key)))
                    transitions.Add((t, key));
            }
        }

        foreach (var operation in _operationOrder)
        {
            if (Model.Get<Operation>(operation.Id)?.Verb is { Length: > 0 } key)
                operations.Add((operation, key));
        }

        IReadOnlyList<string> vocabularyKeys = vocabulary is null ? [VerbVocabularyKind] : [VerbVocabularyKind, Keys.Element(vocabulary.Id)];
        if (vocabulary is null && events.Count == 0 && transitions.Count == 0 && operations.Count == 0 && _verbKeysNamed.Count == 0)
        {
            FillVerbLists(_ => null, vocabularyKeys);
            return new RList<RVerb>([], keys);
        }

        var hidden = new HashSet<string>(vocabulary?.Hidden ?? [], StringComparer.Ordinal);
        // Rights and verb steps name verbs too (rights-design.md section 6), so a hidden built-in verb they name stays listed.
        var named = new HashSet<string>(events.Select(e => e.Key).Concat(transitions.Select(t => t.Key)).Concat(operations.Select(o => o.Key)).Concat(_verbKeysNamed),
            StringComparer.Ordinal);
        var byKey = new Dictionary<string, RVerb>(StringComparer.Ordinal);
        var order = new List<RVerb>();
        var hiddenNamed = new List<RVerb>();
        foreach (var builtIn in BuiltInVerbs.All)
        {
            var isHidden = hidden.Contains(builtIn.Key);
            if (isHidden && !named.Contains(builtIn.Key))
                continue;
            var verb = new RVerb
            {
                Key = builtIn.Key, DisplayName = builtIn.DisplayName, Description = builtIn.Meaning, BuiltIn = true, Group = builtIn.Group, Hidden = isHidden,
            };
            byKey[verb.Key] = verb;
            (isHidden ? hiddenNamed : order).Add(verb);
        }

        foreach (var own in vocabulary?.Verbs ?? [])
        {
            // Only the own verb a use of its key names (ModelSnapshot.OwnVerb): never a built-in verb's key, the first of a repeated one.
            if (byKey.ContainsKey(own.Key) || BuiltInVerbs.IsBuiltIn(own.Key) || !ReferenceEquals(Model.OwnVerb(own.Key), own))
                continue;
            var verb = new RVerb
            {
                Key = own.Key, DisplayName = own.DisplayName ?? BuiltInVerbs.DisplayNameOf(own.Key), Description = own.Description ?? "",
                Group = BuiltInVerbs.ProjectGroup, Id = own.Id,
            };
            byKey[verb.Key] = verb;
            order.Add(verb);
        }

        order.AddRange(hiddenNamed);

        // Which uses a verb has: every process and operation file, besides the vocabulary.
        var useKeys = new DependencySet(Keys).Add(VerbVocabularyKind).Add(ProcessKind).Add(OperationKind);
        if (vocabulary is not null)
            useKeys.Element(vocabulary.Id);
        foreach (var process in _processOrder.Concat(_libraryOrder))
            useKeys.Element(process.Id);
        foreach (var operation in _operationOrder)
            useKeys.Element(operation.Id);
        var usesKeys = useKeys.ToList();

        FillVerbLists(byKey.GetValueOrDefault, vocabularyKeys);
        foreach (var (node, key) in events)
        {
            DepsOf(node).AddRange(vocabularyKeys);
            node.Verb = byKey.GetValueOrDefault(key);
        }

        foreach (var (node, key) in transitions)
        {
            DepsOf(node).AddRange(vocabularyKeys);
            node.Verb = byKey.GetValueOrDefault(key);
        }

        foreach (var (node, key) in operations)
        {
            DepsOf(node).AddRange(vocabularyKeys);
            node.Verb = byKey.GetValueOrDefault(key);
        }

        foreach (var verb in order)
        {
            verb.Dependencies = keys;
            verb.Events = new RList<REvent>(events.Where(e => ReferenceEquals(e.Node.Verb, verb)).Select(e => e.Node), usesKeys);
            verb.Transitions = new RList<RTransition>(transitions.Where(t => ReferenceEquals(t.Node.Verb, verb)).Select(t => t.Node), usesKeys);
            verb.Operations = new RList<ROperation>(operations.Where(o => ReferenceEquals(o.Node.Verb, verb)).Select(o => o.Node), usesKeys);
            verb.Used = verb.Events.Count > 0 || verb.Transitions.Count > 0 || verb.Operations.Count > 0;
            verb.Rights = RightsOf(verb);
            if (verb.Id is not null)
                Register(verb);
        }

        return new RList<RVerb>(order, keys);
    }

    /// <summary>Fills the verb lists of rights, <c>may_refuse</c> entries and verb steps, which wait for <c>model.verbs</c>.</summary>
    private void FillVerbLists(Func<string, RVerb?> verbOf, IReadOnlyList<string> vocabularyKeys)
    {
        foreach (var fill in _verbFills)
            fill(verbOf, vocabularyKeys);
    }
}
