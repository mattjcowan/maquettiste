using System.Collections.Frozen;
using System.Collections.Immutable;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>An element or sub-element and the forms a qualified name finds it by.</summary>
/// <param name="Kind">The kind: the element kind, or the index kind of a sub-element.</param>
/// <param name="Id">Its id.</param>
/// <param name="ElementId">The id of the element whose file holds it (its own for an element).</param>
/// <param name="Forms">Its forms as canonical paths (<see cref="QualifiedName.Path"/>), distinct, shortest first.</param>
/// <param name="Longest">The longest form, which MQ1013 lists and a minted id reports.</param>
internal sealed record NameEntry(string Kind, string Id, string ElementId, ImmutableArray<string> Forms, string Longest);

/// <summary>
/// The forms of one kind (next-phase-1-design.md section 5.1): every element or sub-element of the kind under each form its stored
/// names give it, never resolved ones (conventions, prefixes), so a lookup never resolves the model. A bare name is unique or
/// ambiguous; the longer forms qualify it. Built from typed elements on first use (<see cref="ModelSnapshot"/> holds one lazy index
/// per kind), immutable once built and safe to read in parallel; a batch layers a private overlay on it from <see cref="Of"/>.
/// </summary>
internal sealed class NameIndex
{
    private readonly FrozenDictionary<string, ImmutableArray<NameEntry>> _byForm;
    private readonly FrozenDictionary<string, NameEntry> _byId;
    private readonly FrozenDictionary<string, ImmutableArray<NameEntry>> _byElement;

    private NameIndex(string kind, IEnumerable<NameEntry> entries)
    {
        Kind = kind;
        var byId = new Dictionary<string, NameEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
            byId.TryAdd(entry.Id, entry); // a duplicate id is MQ1004; the first one read wins, as in the model index
        var ordered = byId.Values.OrderBy(e => e.Longest, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        _byId = byId.ToFrozenDictionary(StringComparer.Ordinal);
        _byForm = ordered.SelectMany(e => e.Forms.Select(f => (Form: f, Entry: e)))
            .GroupBy(p => p.Form, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.Select(p => p.Entry).ToImmutableArray(), StringComparer.Ordinal);
        _byElement = ordered.GroupBy(e => e.ElementId, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.Ordinal);
    }

    /// <summary>The kinds whose elements and sub-elements have no names: an id or a placeholder reaches them.</summary>
    public static FrozenSet<string> IdOnlyKinds { get; } =
        new[] { "tag-vocabulary", "category-tree", "migration", "transition", "step" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The kinds with forms: every element kind and index kind not listed by id only. A kind another section adds takes its
    /// <c>name</c>, and <c>&lt;package path&gt;.name</c> when its file states a package.
    /// </summary>
    public static FrozenSet<string> NamedKinds { get; } = KindInfo.All.Select(k => k.Name).Concat(ModelIndexer.SubElementKinds)
        .Where(k => !IdOnlyKinds.Contains(k)).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The kind.</summary>
    public string Kind { get; }

    /// <summary>Every entry, ordinal by longest form, then id.</summary>
    public IEnumerable<NameEntry> Entries => _byId.Values.OrderBy(e => e.Longest, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal);

    /// <summary>What a path finds: nothing (MQ1012), one entry, or several (MQ1013), ordinal by longest form, then id.</summary>
    /// <param name="path">The canonical path (<see cref="QualifiedName.Path"/>).</param>
    /// <returns>The candidates.</returns>
    public IReadOnlyList<NameEntry> Find(string path) => _byForm.TryGetValue(path, out var entries) ? entries : [];

    /// <summary>The entry of an id, or <see langword="null"/> when the id has no forms of this kind.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The entry.</returns>
    public NameEntry? Get(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The entries an element's file holds (the element and its sub-elements), which a write replaces.</summary>
    /// <param name="elementId">The element id.</param>
    /// <returns>The entries.</returns>
    public IReadOnlyList<NameEntry> EntriesOf(string elementId) => _byElement.TryGetValue(elementId, out var entries) ? entries : [];

    /// <summary>Builds the index of one kind from a snapshot's typed elements, reading only the kinds that hold it.</summary>
    /// <param name="kind">A kind of <see cref="NamedKinds"/>.</param>
    /// <param name="snapshot">The model.</param>
    /// <returns>The index.</returns>
    internal static NameIndex Build(string kind, ModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!NamedKinds.Contains(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind has no names.");
        Element? Lookup(string id) => snapshot.Get<Element>(id);
        return new NameIndex(kind, Holders(kind).SelectMany(snapshot.ElementsOf).SelectMany(e => Of(e, Lookup, kind, snapshot.PropertySchemas)));
    }

    /// <summary>
    /// The entries of an element's file: the element and its sub-elements, of one kind or of all. Qualifiers that live in other files
    /// (package paths, database and schema names, a scenario's process, a seed's reference type) come from <paramref name="lookup"/>,
    /// so a batch's overlay reads them as the earlier operations leave them.
    /// </summary>
    /// <param name="element">The element as it is (or will be) written.</param>
    /// <param name="lookup">Any element by id, or <see langword="null"/>.</param>
    /// <param name="kind">Only entries of this kind, or <see langword="null"/> for all.</param>
    /// <param name="schemas">The property schema set whose custom kinds give a custom element's members, or <see langword="null"/> for no
    /// member entries.</param>
    /// <returns>The entries, in document order.</returns>
    public static IEnumerable<NameEntry> Of(Element element, Func<string, Element?> lookup, string? kind = null, PropertySchemaSet? schemas = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(lookup);
        var entries = new List<NameEntry>();
        var forms = FormsOf(element, lookup);
        void Add(string entryKind, string id, Forms? set)
        {
            if ((kind is null || string.Equals(kind, entryKind, StringComparison.Ordinal)) && set?.Entry(entryKind, id, element.Id) is { } entry)
                entries.Add(entry);
        }

        bool Wants(params ReadOnlySpan<string> kinds)
        {
            if (kind is null)
                return true;
            foreach (var k in kinds)
            {
                if (string.Equals(k, kind, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        Add(element.KindName, element.Id, forms);
        switch (element)
        {
            case Entity entity when forms is not null:
                AddAttributes(entity.Attributes, forms, Add);
                foreach (var key in entity.AlternateKeys)
                    Add("key", key.Id, forms.Then(key.Name));
                foreach (var check in entity.Checks)
                    Add("key", check.Id, forms.Then(check.Name));
                if (Wants("binding"))
                {
                    foreach (var binding in entity.Bindings)
                        Add("binding", binding.Id, forms.Then(NameOf(lookup(binding.Database))));
                }

                break;
            case ValueObject valueObject when forms is not null:
                AddAttributes(valueObject.Attributes, forms, Add);
                break;
            case Relation relation when forms is not null:
                AddAttributes(relation.Attributes, forms, Add);
                foreach (var end in relation.Ends)
                    Add("end", end.Id, forms.Then(end.Role is { Length: > 0 } role ? role : end.Navigation));
                break;
            case ReferenceType type when forms is not null:
                AddAttributes(type.Attributes, forms, Add);
                Add("reference-field", type.Code.Id, forms.Then("code"));
                Add("reference-field", type.Label.Id, forms.Then("label"));
                if (Wants("binding"))
                {
                    foreach (var binding in type.Bindings)
                        Add("binding", binding.Id, forms.Then(NameOf(lookup(binding.Database))));
                }

                break;
            case Stereotype stereotype when forms is not null:
                AddAttributes(stereotype.Attributes, forms, Add);
                foreach (var error in stereotype.Errors)
                    Add("error", error.Id, forms.Then(error.Name));
                break;
            case EnumType enumType when forms is not null:
                foreach (var member in enumType.Members)
                    Add("enum-member", member.Id, forms.Then(member.Name));
                break;
            case Process process when forms is not null:
                AddProcess(process, forms, Add);
                break;
            case Scenario scenario when forms is not null:
                // A scenario's objects (lifecycle-design.md decision 22): <scenario form>.<object name>.
                foreach (var obj in scenario.Objects)
                    Add("object", obj.Id, forms.Then(obj.Name));
                break;
            case Table table when forms is not null:
                foreach (var column in table.Columns)
                    Add("column", column.Id, forms.Then(column.Name));
                foreach (var unique in table.Uniques)
                    Add("key", unique.Id, forms.Then(unique.Name));
                foreach (var foreignKey in table.ForeignKeys)
                    Add("key", foreignKey.Id, forms.Then(foreignKey.Name));
                foreach (var check in table.Checks)
                    Add("key", check.Id, forms.Then(check.Name));
                foreach (var index in table.Indexes)
                    Add("key", index.Id, forms.Then(index.Name));
                break;
            case Database database when Wants("schema"):
                foreach (var schema in database.Schemas)
                    Add("schema", schema.Id, Forms.Of([schema.Name], [database.Name, schema.Name]));
                break;
            case Seed seed when Wants("row") && lookup(seed.Target) is ReferenceType target && FormsOf(target, lookup) is { } typeForms:
                AddRows(seed, forms, typeForms, Add);
                break;
            case CategoryTree tree when Wants("category"):
                AddCategories(tree, lookup, Add);
                break;
            case Operation operation when forms is not null:
                AddOperation(operation, forms, Add);
                break;
            case Constraint constraint when forms is not null:
                foreach (var parameter in constraint.Parameters)
                    Add("parameter", parameter.Id, forms.Then(parameter.Name));
                break;
            case Custom custom when forms is not null && schemas is not null && Wants("member"):
                AddMembers(custom, schemas, forms, Add);
                break;
        }

        return entries;
    }

    /// <summary>The element kinds whose files hold entries of a kind.</summary>
    private static IEnumerable<ElementKind> Holders(string kind) => kind switch
    {
        "attribute" => [ElementKind.Entity, ElementKind.ValueObject, ElementKind.Relation, ElementKind.ReferenceType, ElementKind.Stereotype, ElementKind.Process],
        "column" => [ElementKind.Table],
        "key" => [ElementKind.Table, ElementKind.Entity],
        "schema" => [ElementKind.Database],
        "enum-member" => [ElementKind.Enum],
        "guard" => [ElementKind.Process, ElementKind.Operation],
        "event" or "action" or "invoke" or "gate" or "meaning" or "state" => [ElementKind.Process],
        "end" => [ElementKind.Relation],
        "reference-field" => [ElementKind.ReferenceType],
        "row" => [ElementKind.Seed],
        "binding" => [ElementKind.Entity, ElementKind.ReferenceType],
        "category" => [ElementKind.CategoryTree],
        "operation-member" or "outcome" => [ElementKind.Operation],
        "parameter" => [ElementKind.Constraint],
        "error" => [ElementKind.Operation, ElementKind.Stereotype],
        "member" => [ElementKind.Custom],
        "object" => [ElementKind.Scenario],
        _ => KindInfo.TryGet(kind, out var info) ? [info.Kind] : [],
    };

    /// <summary>An element's own forms, or <see langword="null"/> when it has none (no stored name, or a kind listed by id only).</summary>
    private static Forms? FormsOf(Element element, Func<string, Element?> lookup)
    {
        switch (element)
        {
            case TagVocabulary or CategoryTree or Migration:
                return null;
            case Package package:
            {
                // The name, or the path from a root package; a trailing part of that path qualifies it too.
                var path = PackagePath(package.Id, lookup);
                return path.Count == 0 ? null : Forms.Of([.. Enumerable.Range(0, path.Count).Select(k => path.Skip(path.Count - 1 - k).ToArray())]);
            }

            case Stereotype stereotype:
                return Forms.Of([stereotype.Key]);
            case Table table:
                return Physical(table.Name, table.Database, table.Schema, lookup);
            case View view:
                return Physical(view.Name, view.Database, view.Schema, lookup);
            case Sequence sequence:
                return Physical(sequence.Name, sequence.Database, sequence.Schema, lookup);
            case Routine routine:
                return Physical(routine.Name, routine.Database, routine.Schema, lookup);
            case DatabaseType databaseType:
                return Physical(databaseType.Name, databaseType.Database, databaseType.Schema, lookup);
            case SqlObject sqlObject:
                return Physical(sqlObject.Name, sqlObject.Database, sqlObject.Schema, lookup);
            case Query query:
                return Forms.Of([query.Name], [NameOf(lookup(query.Database)), query.Name]);
            case Scenario scenario:
            {
                var own = Forms.Of([scenario.Name]);
                return lookup(scenario.Process) is Process process && FormsOf(process, lookup) is { } processForms
                    ? Forms.Union(own, processForms.Then(scenario.Name))
                    : own;
            }

            case Operation operation:
            {
                // As any packaged element, plus <subject form>.name (Invoice.send, Billing.Invoice.send), as a scenario adds its process's.
                var own = Packaged(operation, lookup);
                return operation.Subject is { } subject && lookup(subject) is Entity entity && FormsOf(entity, lookup) is { } subjectForms
                    ? Forms.Union(own, subjectForms.Then(operation.Name))
                    : own;
            }

            case Custom custom:
            {
                // Names are unique per type, so every form carries it (next-phase-2-design.md section 4.2): <type>.<name> and
                // <type>.<package path>.<name>, a trailing part of the path qualifying it too. The plain name is no form.
                var own = Forms.Of([custom.Type, custom.Name]);
                if (custom.Package is not { } packageId || own is null)
                    return own;
                var path = PackagePath(packageId, lookup);
                return Forms.Union(own, Forms.Of([.. Enumerable.Range(1, path.Count).Select(k => path.Skip(path.Count - k).Prepend(custom.Type).Append(custom.Name).ToArray())]));
            }

            default:
                return Packaged(element, lookup);
        }
    }

    /// <summary>
    /// A custom element's members (next-phase-2-design.md section 4.2): <c>&lt;custom form&gt;.&lt;member name&gt;</c>, parent members first.
    /// A member without a name, or under one without a name, has no form.
    /// </summary>
    private static void AddMembers(Custom custom, PropertySchemaSet schemas, Forms forms, Action<string, string, Forms?> add)
    {
        var members = ModelIndexer.MembersOf(custom, schemas);
        for (var i = 0; i < members.Count; i++)
        {
            if (members[i].Id is not { } id)
                continue;
            var names = new List<string?>();
            for (var at = i; at >= 0; at = members[at].Parent)
                names.Insert(0, members[at].Name);
            add("member", id, forms.Then([.. names]));
        }
    }

    /// <summary>The name, plus <c>&lt;package path&gt;.name</c> when the file states a package (a trailing part of the path qualifies it too).</summary>
    private static Forms? Packaged(Element element, Func<string, Element?> lookup)
    {
        var own = Forms.Of([element.Name]);
        if (element is not IPackaged { Package: { } packageId } || own is null)
            return own;
        var path = PackagePath(packageId, lookup);
        return Forms.Union(own, Forms.Of([.. Enumerable.Range(1, path.Count).Select(k => path.Skip(path.Count - k).Append(element.Name).ToArray())]));
    }

    /// <summary><c>name</c>, <c>schema.name</c>, <c>database.schema.name</c>: the schema, else the database's default schema.</summary>
    private static Forms? Physical(string name, string databaseId, string? schemaId, Func<string, Element?> lookup)
    {
        var database = lookup(databaseId) as Database;
        var schema = schemaId is null ? database?.DefaultSchema : database?.Schemas.FirstOrDefault(s => string.Equals(s.Id, schemaId, StringComparison.Ordinal))?.Name;
        return Forms.Of([name], [schema ?? "", name], [database?.Name ?? "", schema ?? "", name]);
    }

    /// <summary>The names from a root package down to a package; empty when the package is missing. A cycle stops at its first repeat.</summary>
    private static List<string> PackagePath(string packageId, Func<string, Element?> lookup)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = packageId; id is not null && seen.Add(id) && lookup(id) is Package package; id = package.Parent)
            names.Add(package.Name);
        names.Reverse();
        return names;
    }

    private static string NameOf(Element? element) => element?.Name ?? "";

    private static void AddAttributes(IEnumerable<ModelAttribute> attributes, Forms owner, Action<string, string, Forms?> add)
    {
        foreach (var attribute in attributes)
            add("attribute", attribute.Id, owner.Then(attribute.Name));
    }

    /// <summary>An operation's input members, outcomes (each followed by its returns' members, under the outcome) and errors.</summary>
    private static void AddOperation(Operation operation, Forms forms, Action<string, string, Forms?> add)
    {
        foreach (var member in operation.Input)
            add("operation-member", member.Id, forms.Then(member.Name));
        foreach (var outcome in operation.Outcomes)
        {
            add("outcome", outcome.Id, forms.Then(outcome.Name));
            foreach (var member in outcome.Returns?.Members ?? [])
                add("operation-member", member.Id, forms.Then(outcome.Name, member.Name));
        }

        foreach (var error in operation.Errors)
            add("error", error.Id, forms.Then(error.Name));
        foreach (var guard in operation.Guards)
            add("guard", guard.Id, forms.Then(guard.Name));
    }

    private static void AddProcess(Process process, Forms forms, Action<string, string, Forms?> add)
    {
        AddAttributes(process.Context, forms, add);
        foreach (var e in process.Events)
        {
            add("event", e.Id, forms.Then(e.Name));
            foreach (var attribute in e.Payload)
                add("attribute", attribute.Id, forms.Then(e.Name, attribute.Name));
        }

        foreach (var guard in process.Guards)
            add("guard", guard.Id, forms.Then(guard.Name));
        foreach (var action in process.Actions)
            add("action", action.Id, forms.Then(action.Name));

        void States(IEnumerable<ProcessState> states, string[] parents)
        {
            foreach (var state in states)
            {
                string[] path = [.. parents, state.Name];
                add("state", state.Id, forms.Then(path));
                foreach (var invoke in state.Invoke)
                    add("invoke", invoke.Id, forms.Then(invoke.Name));
                States(state.States, path);
            }
        }

        States(process.States, []);
        foreach (var gate in process.Transitions.Select(t => t.Gate).OfType<ProcessGate>().Concat(process.Gates))
        {
            add("gate", gate.Id, forms.Then(gate.Name));
            foreach (var meaning in gate.Meanings)
                add("meaning", meaning.Id, forms.Then(gate.Name, meaning.Name));
            foreach (var attribute in gate.AuditAttributes)
                add("attribute", attribute.Id, forms.Then(gate.Name, attribute.Name));
        }
    }

    /// <summary>Rows of a reference-type seed: <c>&lt;reference type&gt;.&lt;code&gt;</c> and <c>&lt;seed&gt;.&lt;code&gt;</c>.</summary>
    private static void AddRows(Seed seed, Forms? seedForms, Forms typeForms, Action<string, string, Forms?> add)
    {
        var codeColumn = -1;
        for (var k = 0; k < seed.Columns.Count && codeColumn < 0; k++)
        {
            if (string.Equals(seed.Columns[k], "code", StringComparison.Ordinal))
                codeColumn = k;
        }

        if (codeColumn < 0)
            return;
        foreach (var row in seed.Rows)
        {
            if (codeColumn >= row.Values.Count || ReferenceDataIndex.CodeKey(row.Values[codeColumn]) is not { } code)
                continue;
            var byType = typeForms.Then(code);
            // The seed's forms are the longer ones: they tell apart two seeds of one type (one per environment, say).
            add("row", row.Id, seedForms is null ? byType : Forms.Union(byType, seedForms.Then(code)));
        }
    }

    /// <summary>Categories: the names from the tree's root in any tree, and in a package's tree also the package path before them.</summary>
    private static void AddCategories(CategoryTree tree, Func<string, Element?> lookup, Action<string, string, Forms?> add)
    {
        var byId = new Dictionary<string, Category>(StringComparer.Ordinal);
        foreach (var category in tree.Categories)
            byId.TryAdd(category.Id, category);
        var package = tree.Package is { } packageId ? PackagePath(packageId, lookup) : [];
        foreach (var category in tree.Categories)
        {
            var path = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = category; current is not null && seen.Add(current.Id); current = current.Parent is { } parent ? byId.GetValueOrDefault(parent) : null)
                path.Add(current.Name);
            path.Reverse();
            var own = Forms.Of([.. path]);
            add("category", category.Id, own is null || package.Count == 0
                ? own
                : Forms.Union(own, Forms.Of([.. Enumerable.Range(1, package.Count).Select(k => package.Skip(package.Count - k).Concat(path).ToArray())])));
        }
    }

    /// <summary>A set of forms as segment arrays, shortest first, and the longest; a form with an empty segment is dropped.</summary>
    private sealed class Forms
    {
        private Forms(ImmutableArray<string[]> all, string[] longest)
        {
            All = all;
            Longest = longest;
        }

        public ImmutableArray<string[]> All { get; }

        public string[] Longest { get; }

        /// <summary>The forms that have no empty segment, or <see langword="null"/> when none is left; the longest is the last longest one.</summary>
        public static Forms? Of(params string[][] forms)
        {
            var kept = forms.Where(f => f.Length > 0 && f.All(s => s.Length > 0)).ToImmutableArray();
            if (kept.IsEmpty)
                return null;
            var longest = kept[0];
            foreach (var form in kept)
            {
                if (form.Length >= longest.Length)
                    longest = form;
            }

            return new Forms(kept, longest);
        }

        /// <summary>Both sets; the longest is the second set's when it is at least as long.</summary>
        public static Forms? Union(Forms? first, Forms? second) => (first, second) switch
        {
            (null, _) => second,
            (_, null) => first,
            _ => Of([.. first.All, .. second.All]),
        };

        /// <summary>Each form followed by the segments.</summary>
        public Forms? Then(params string?[] tail)
        {
            var segments = new string[tail.Length];
            for (var k = 0; k < tail.Length; k++)
            {
                if (tail[k] is not { Length: > 0 } segment)
                    return null;
                segments[k] = segment;
            }

            return Of([.. All.Select(f => f.Concat(segments).ToArray())]);
        }

        public NameEntry Entry(string kind, string id, string elementId)
        {
            var paths = new List<string>();
            foreach (var form in All.OrderBy(f => f.Length))
            {
                var path = QualifiedName.FormatPath(form);
                if (!paths.Contains(path, StringComparer.Ordinal))
                    paths.Add(path);
            }

            return new NameEntry(kind, id, elementId, [.. paths], QualifiedName.FormatPath(Longest));
        }
    }
}

/// <summary>The lazy name indexes of one snapshot: one per kind, each built on first use, so a batch that names no table never indexes tables.</summary>
internal sealed class NameIndexes
{
    private readonly FrozenDictionary<string, Lazy<NameIndex>> _byKind;

    /// <summary>Creates the lazy indexes of a snapshot.</summary>
    /// <param name="snapshot">The model.</param>
    public NameIndexes(ModelSnapshot snapshot) =>
        _byKind = NameIndex.NamedKinds.ToFrozenDictionary(
            k => k, k => new Lazy<NameIndex>(() => NameIndex.Build(k, snapshot), LazyThreadSafetyMode.ExecutionAndPublication), StringComparer.Ordinal);

    /// <summary>The index of a kind with names.</summary>
    /// <param name="kind">A kind of <see cref="NameIndex.NamedKinds"/>.</param>
    /// <returns>The index.</returns>
    public NameIndex Get(string kind) =>
        _byKind.TryGetValue(kind, out var index) ? index.Value : throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind has no names.");
}
