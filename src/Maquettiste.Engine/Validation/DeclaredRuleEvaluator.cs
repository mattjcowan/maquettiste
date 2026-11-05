using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Runs the declared rules (next-phase-2-design.md section 5.6) on a resolved model, inside the checks after resolution: for each rule,
/// ordinal by id, the objects of its kinds (<see cref="ResolvedKinds.ObjectsOf"/>, walked once for every rule's kinds) that its selector
/// matches are judged in parallel. An object where <c>require</c> does not hold gives <c>x/&lt;id&gt;</c> with the rule's severity, its
/// rendered message and the location <see cref="ResolvedLocations"/> gives; a rule that selects nothing is MQ5017 on its <c>select</c>. A
/// rule <c>validation.rules</c> sets <c>off</c> does not run; other settings apply after (<see cref="ResolvedChecks"/>). An object whose
/// judgment reads a calculated value that failed gives nothing: the failure is reported once, on the value. An <c>expression</c>
/// condition (next-phase-3-design.md section 3.1, "Declared rule conditions") is typed as the static pass types it, lowered to
/// JavaScript once per run and judged as a guard, only true holding, in one sandbox pool per rule-file hash; one that does not type
/// holds (the static pass reports it). A condition that cannot answer for an object, a limit (MQ5003) or a throw (MQ5002), is reported
/// once for its rule, at the condition, and the rule reports no finding in that run. A project whose rules hold no expression loads no
/// runtime.
/// </summary>
internal static class DeclaredRuleEvaluator
{
    private static readonly ConcurrentDictionary<string, string[]> Paths = new(StringComparer.Ordinal);
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static MemberCatalog Members => DeclaredRuleChecks.Members;

    /// <summary>Runs every declared rule of a snapshot on its resolved model.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="resolved">Its resolved model.</param>
    /// <param name="parallelism">The most objects judged at once.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="judged">How many objects were judged.</param>
    /// <returns>The findings, unpositioned and before <c>validation.rules</c>, in rule then object order.</returns>
    public static List<Diagnostic> Evaluate(ModelSnapshot snapshot, ResolvedModel resolved, int parallelism, CancellationToken ct, out int judged)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolved);
        judged = 0;
        var settings = snapshot.Settings.Validation.Rules;
        var rules = new List<(DeclaredRule Rule, string Path, int Index)>();
        foreach (var file in snapshot.DeclaredRules)
        {
            for (var i = 0; i < file.Rules.Rules.Count; i++)
            {
                var rule = file.Rules.Rules[i];
                if (!(settings.TryGetValue("x/" + rule.Id, out var setting) && string.Equals(setting, "off", StringComparison.Ordinal)))
                    rules.Add((rule, file.Path, i));
            }
        }

        var found = new List<Diagnostic>();
        if (rules.Count == 0)
            return found;
        rules.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.Rule.Id, b.Rule.Id);
            if (c == 0)
                c = string.CompareOrdinal(a.Path, b.Path);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });

        var objects = ResolvedKinds.ObjectsOf(resolved, rules.SelectMany(r => r.Rule.Select.Kind)
            .Select(k => k.StartsWith(ModelPages.CustomKindPrefix, StringComparison.Ordinal) ? "custom" : k));
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct };
        using var expressions = ExpressionConditions.Of(snapshot, rules, ct);
        foreach (var (rule, path, index) in rules)
        {
            ct.ThrowIfCancellationRequested();
            if (!DeclaredRuleChecks.TryParseMessage(rule.Message, out var parts, out _))
                continue; // MQ5016, and validation then has an error: never reached on a whole-model run
            var selected = objects.Where(o => Matches(rule.Select, o)).ToList();
            if (selected.Count == 0)
            {
                found.Add(RuleCatalog.Create("MQ5017", $"The rule '{rule.Id}' selects no object.", null, path, $"/rules/{index}/select"));
                continue;
            }

            judged += selected.Count;
            var results = new Diagnostic?[selected.Count];
            var failures = new ConditionFailedException?[selected.Count];
            Parallel.For(0, selected.Count, options, i =>
            {
                try
                {
                    results[i] = new Judge(snapshot, expressions, ct).Finding(rule, parts, selected[i]);
                }
                catch (ConditionFailedException failure)
                {
                    failures[i] = failure;
                }
            });

            // A condition that could not judge every object would leave its rule's findings partial: the first failure, in object order,
            // stands for them.
            var failed = Array.FindIndex(failures, f => f is not null);
            if (failed >= 0)
            {
                found.Add(failures[failed]!.Report(rule.Id, selected[failed], path));
                continue;
            }

            foreach (var result in results)
            {
                if (result is not null)
                    found.Add(result);
            }
        }

        return found;
    }

    /// <summary>
    /// An expression condition that could not answer for an object: it reached a limit or threw, while it ran or while its sandbox
    /// loaded. It stops the object's judgment, and its rule reports it instead of findings (<see cref="Report"/>).
    /// </summary>
    /// <param name="pointer">The condition's pointer in its rule file.</param>
    /// <param name="inner">The sandbox's <see cref="ScriptLimitException"/> or <see cref="ScriptErrorException"/>.</param>
    private sealed class ConditionFailedException(string pointer, Exception inner) : Exception(inner.Message, inner)
    {
        /// <summary>MQ5003 for a limit, MQ5002 for a throw, at the condition, naming the rule and the object it stopped on.</summary>
        public Diagnostic Report(string ruleId, RAnnotated value, string path)
        {
            var on = $"{value.Kind} '{(value is RElement { Name.Length: > 0 } element ? element.Name : value.Id)}'";
            return InnerException is ScriptLimitException limit
                ? RuleCatalog.Create("MQ5003", $"The expression condition of rule '{ruleId}' stopped on {on}: {Reason(limit.Diagnostic.Message)} Simplify the "
                    + "condition; the rule reports no finding until it runs.", null, path, pointer)
                : RuleCatalog.Create("MQ5002", $"The expression condition of rule '{ruleId}' threw on {on}: {Reason(((ScriptErrorException)InnerException!).Diagnostic.Message)} "
                    + "Fix the condition; the rule reports no finding until it runs.", null, path, pointer);
        }

        /// <summary>The sandbox's message without the helper it names (the condition's pointer, which the diagnostic carries).</summary>
        private static string Reason(string message)
        {
            const string Helper = "Helper '";
            var end = message.StartsWith(Helper, StringComparison.Ordinal) ? message.IndexOf("' ", Helper.Length, StringComparison.Ordinal) : -1;
            var text = (end < 0 ? message : message[(end + 2)..]).Trim();
            return text.EndsWith('.') ? text : text + ".";
        }
    }

    /// <summary>
    /// The expression conditions of a run's rules, lowered to JavaScript: one evaluator, a sandbox pool over the runtime and the file's
    /// functions, per rule-file hash, each function registered under its condition's pointer in the file. Empty, and free, when no rule
    /// holds an expression.
    /// </summary>
    private sealed class ExpressionConditions : IDisposable
    {
        private readonly Dictionary<Expr, (ExprEvaluator Evaluator, string Id, IReadOnlyList<string> Members)> _compiled = new(ReferenceEqualityComparer.Instance);
        private readonly List<ExprEvaluator> _evaluators = [];

        public static ExpressionConditions Of(ModelSnapshot snapshot, List<(DeclaredRule Rule, string Path, int Index)> rules, CancellationToken ct)
        {
            var expressions = new ExpressionConditions();
            var files = snapshot.DeclaredRules.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var pools = new Dictionary<string, (ExprEvaluator? Evaluator, Dictionary<string, string> Functions)>(StringComparer.Ordinal);
            var pending = new List<(Expr Tree, string Hash, string Id, List<string> Members)>();
            foreach (var group in rules.Where(r => HasExpression(r.Rule.Require)).GroupBy(r => r.Path, StringComparer.Ordinal))
            {
                var hash = files.TryGetValue(group.Key, out var file) ? file.Hash : group.Key;
                if (!pools.TryGetValue(hash, out var pool))
                    pools[hash] = pool = (null, new Dictionary<string, string>(StringComparer.Ordinal));
                foreach (var (rule, _, index) in group)
                {
                    foreach (var (tree, typed) in DeclaredRuleChecks.TypedExpressions(snapshot, rule, group.Key, index))
                    {
                        pool.Functions.TryAdd(typed.Pointer, ExprLowering.ToJavaScript(typed, ExprSiteKind.RuleCondition));
                        // The members from the typed form: text holds its member paths only once it is parsed.
                        pending.Add((tree, hash, typed.Pointer, [.. MembersOf(typed).Distinct(StringComparer.Ordinal)]));
                    }
                }
            }

            foreach (var (hash, (_, functions)) in pools.Where(p => p.Value.Functions.Count > 0).OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
            {
                var evaluator = ExprEvaluator.Create(functions.OrderBy(f => f.Key, StringComparer.Ordinal), null, ct);
                expressions._evaluators.Add(evaluator);
                pools[hash] = (evaluator, functions);
            }

            foreach (var (tree, hash, id, members) in pending)
                expressions._compiled[tree] = (pools[hash].Evaluator!, id, members);

            return expressions;

            static IEnumerable<string> MembersOf(TypedExpr t) =>
                (t.Node.Member is { } member ? [member] : Enumerable.Empty<string>()).Concat(t.Args.SelectMany(MembersOf));
        }

        /// <summary>
        /// Whether an expression condition holds of an item: <see langword="true"/> only when it answers true, as a guard; one that did
        /// not type holds. One that stops at a limit or throws is a <see cref="ConditionFailedException"/>, which its rule reports.
        /// </summary>
        public bool Holds(Expr tree, Func<string, object?> read, CancellationToken ct)
        {
            if (!_compiled.TryGetValue(tree, out var compiled))
                return true;
            try
            {
                return compiled.Evaluator.Evaluate(compiled.Id, Row(compiled.Members, read), ct) is true;
            }
            catch (Exception ex) when (ex is ScriptLimitException or ScriptErrorException)
            {
                throw new ConditionFailedException(compiled.Id, ex);
            }
        }

        public void Dispose()
        {
            foreach (var evaluator in _evaluators)
                evaluator.Dispose();
        }

        private static bool HasExpression(RuleCondition c) =>
            c.Expression is not null || (c.All?.Any(HasExpression) ?? false) || (c.Any?.Any(HasExpression) ?? false)
            || (c.Not is { } not && HasExpression(not)) || (c.Count?.Where is { } where && HasExpression(where));

        /// <summary>
        /// The row an expression reads: each member path's value at its place (<c>properties.retentionDays</c> under
        /// <c>properties</c>), a value as <see cref="Write"/> writes it, a date or timestamp as ISO text.
        /// </summary>
        private static JsonElement Row(IReadOnlyList<string> members, Func<string, object?> read)
        {
            var root = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                var segments = member.Split('.');
                var at = root;
                for (var i = 0; i < segments.Length - 1 && at is not null; i++)
                {
                    if (!at.TryGetValue(segments[i], out var next))
                        at[segments[i]] = next = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                    at = next as SortedDictionary<string, object?>;
                }

                at?.TryAdd(segments[^1], new Leaf(read(member)));
            }

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                WriteRow(writer, root);
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }

        private static void WriteRow(Utf8JsonWriter writer, SortedDictionary<string, object?> node)
        {
            writer.WriteStartObject();
            foreach (var (key, value) in node)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case SortedDictionary<string, object?> nested:
                        WriteRow(writer, nested);
                        break;
                    case Leaf { Value: DateOnly d }:
                        writer.WriteStringValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        break;
                    case Leaf { Value: TimeOnly t }:
                        writer.WriteStringValue(t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                        break;
                    case Leaf { Value: DateTime t }:
                        writer.WriteStringValue(t.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                        break;
                    case Leaf { Value: DateTimeOffset t }:
                        writer.WriteStringValue(t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
                        break;
                    case Leaf { Value: Guid g }:
                        writer.WriteStringValue(g.ToString("D", CultureInfo.InvariantCulture));
                        break;
                    case Leaf leaf:
                        Write(writer, leaf.Value);
                        break;
                }
            }

            writer.WriteEndObject();
        }

        /// <summary>A member's value in a row, apart from the maps its path goes through.</summary>
        private sealed record Leaf(object? Value);
    }

    /// <summary>Whether an object is one a selector matches: every key that has values holds one of them.</summary>
    private static bool Matches(RuleSelector select, RAnnotated value)
    {
        if (!select.Kind.Contains(value.Kind, StringComparer.Ordinal)
            && !(value is RCustom custom && select.Kind.Contains(ModelPages.CustomKindPrefix + custom.Type, StringComparer.Ordinal)))
            return false;
        if (select.Stereotype.Count > 0 && !select.Stereotype.Any(value.HasStereotype))
            return false;
        if (select.Tag.Count > 0 && !select.Tag.Any(value.HasTag))
            return false;
        if (select.Property.Count > 0 && !select.Property.Any(p => value.Properties.TryGetValue(p, out var v) && Normalize(v) is not null))
            return false;
        return select.Package.Count == 0 || InPackage(value, select.Package);
    }

    /// <summary>Whether an object's package (a table's, else its entity's or relation's) is one of the packages or nested in one.</summary>
    private static bool InPackage(RAnnotated value, IReadOnlyList<string> packages)
    {
        var package = value.Package ?? (value as RTable)?.Entity?.Package ?? (value as RTable)?.Relation?.Package;
        for (var depth = 0; package is not null && depth < 256; package = package.Parent, depth++)
        {
            if (packages.Contains(package.Id, StringComparer.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>A JSON null as <see langword="null"/>; any other value as it is.</summary>
    private static object? Normalize(object? value) => value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : value;

    /// <summary>The judgment of one object: conditions, paths and links read from the resolved model and the index.</summary>
    private sealed class Judge(ModelSnapshot snapshot, ExpressionConditions expressions, CancellationToken ct)
    {
        public Diagnostic? Finding(DeclaredRule rule, List<MessagePart> parts, RAnnotated value)
        {
            try
            {
                var require = rule.Require;
                int? count = require switch
                {
                    { Count: { } c } => Count(c, value),
                    { References: { } r } => References(r, value),
                    { ReferencedBy: { } b } => ReferencedBy(b, value),
                    _ => null,
                };
                var holds = require switch
                {
                    { Count: { } c } => Within(count!.Value, c.Min, c.Max, c.Min is null && c.Max is null ? 1 : 0),
                    { References: { } r } => Within(count!.Value, r.Min, r.Max, 1),
                    { ReferencedBy: { } b } => Within(count!.Value, b.Min, b.Max, 1),
                    _ => Holds(require, value),
                };
                if (holds)
                    return null;
                var message = Render(parts, value, count);
                return ResolvedLocations.Place(new Diagnostic("x/" + rule.Id, rule.Severity, message, null, null, null, null, null), value, snapshot);
            }
            catch (CalculatedValueException)
            {
                return null; // the value that failed is reported once, on itself
            }
        }

        private bool Holds(RuleCondition condition, object item)
        {
            if (condition.All is { } all)
                return all.All(c => Holds(c, item));
            if (condition.Any is { } any)
                return any.Any(c => Holds(c, item));
            if (condition.Not is { } not)
                return !Holds(not, item);
            if (condition.Has is { } has)
                return IsPresent(Read(item, has));
            if (condition.Equal is { } equal)
                return Equal(Read(item, equal.Path), equal.Value);
            if (condition.Count is { } count)
                return Within(Count(count, item), count.Min, count.Max, count.Min is null && count.Max is null ? 1 : 0);
            if (condition.References is { } references)
                return Within(References(references, item), references.Min, references.Max, 1);
            if (condition.ReferencedBy is { } referencedBy)
                return Within(ReferencedBy(referencedBy, item), referencedBy.Min, referencedBy.Max, 1);
            if (condition.Expression is { } expression)
                return expressions.Holds(expression, path => Read(item, path), ct);
            return true;
        }

        private static bool Within(int count, int? min, int? max, int defaultMin) => count >= (min ?? defaultMin) && (max is null || count <= max);

        private int Count(RuleCount count, object item)
        {
            if (Items(Read(item, count.Of)) is not { } items)
                return 0;
            var n = 0;
            foreach (var each in items)
            {
                if (each is not null && (count.Where is null || Holds(count.Where, each)))
                    n++;
            }

            return n;
        }

        /// <summary>The distinct objects of the kinds the object links to from its own part of its file, other than its own parts.</summary>
        private int References(RuleReferences links, object item)
        {
            if (item is not IResolvedObject value || !snapshot.TryGetEntry(value.Id, out var entry))
                return 0;
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in snapshot.ReferencesFrom(entry.OwnerId))
            {
                if (!ModelSnapshot.IsInside(reference.JsonPointer, entry.JsonPointer) || (links.Field is { } field && !string.Equals(reference.Field, field, StringComparison.Ordinal)))
                    continue;
                if (!snapshot.TryGetEntry(reference.ToId, out var target) || !links.Kind.Contains(target.Kind, StringComparer.Ordinal))
                    continue;
                if (string.Equals(target.OwnerId, entry.OwnerId, StringComparison.Ordinal) && (string.Equals(target.Id, entry.Id, StringComparison.Ordinal)
                    || ModelSnapshot.IsInside(target.JsonPointer, entry.JsonPointer)))
                    continue; // a link to the object itself or one of its own parts
                if (links.ExcludeRetired && (snapshot.IsRetired(target.Id) || snapshot.IsRetired(target.OwnerId)))
                    continue;
                targets.Add(reference.ToId);
            }

            return targets.Count;
        }

        /// <summary>The distinct objects of the kinds that link to the object, except from diagrams and from the object's own parts.</summary>
        private int ReferencedBy(RuleReferences links, object item)
        {
            if (item is not IResolvedObject value)
                return 0;
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in snapshot.LinksTo(value.Id))
            {
                if (links.Field is { } field && !string.Equals(reference.Field, field, StringComparison.Ordinal))
                    continue;
                if (!snapshot.TryGetEntry(reference.FromId, out var from) || !links.Kind.Contains(from.Kind, StringComparer.Ordinal))
                    continue;
                if (links.ExcludeRetired && (snapshot.IsRetired(reference.FromId) || snapshot.IsRetired(reference.FromElementId)))
                    continue;
                sources.Add(reference.FromId);
            }

            return sources.Count;
        }

        /// <summary>The value at a path, or <see langword="null"/> when a step is absent or null.</summary>
        private object? Read(object item, string path)
        {
            object? current = item;
            foreach (var segment in Paths.GetOrAdd(path, static p => p.Split('.')))
            {
                current = Normalize(current);
                if (current is null)
                    return null;
                current = Member(current, segment);
            }

            return Normalize(current);
        }

        private object? Member(object current, string name)
        {
            switch (current)
            {
                case RCalculated calculated:
                    return calculated.Read(name, null, ct);
                case JsonElement json:
                    return json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var property) ? property : null;
                case IReadOnlyDictionary<string, object?> map:
                    return map.TryGetValue(name, out var mapped) ? mapped : null;
                case IDictionary dictionary:
                    return dictionary.Contains(name) ? dictionary[name] : null;
            }

            var type = current.GetType();
            if (MemberCatalog.IsProxyType(type))
            {
                if (current is RAnnotated annotated && string.Equals(name, "calculated", StringComparison.Ordinal))
                    return RCalculated.Of(annotated);
                var table = Members.GetTable(type);
                return table.TryGetIndex(name, out var index) && table.Members[index].Property is { } member ? member.GetValue(current) : null;
            }

            if (Members.TryReadMap(current, out var entries))
            {
                foreach (var (key, entry) in entries)
                {
                    if (string.Equals(key, name, StringComparison.Ordinal))
                        return entry;
                }
            }

            return null;
        }

        private string Render(List<MessagePart> parts, RAnnotated value, int? count)
        {
            var text = new StringBuilder();
            foreach (var part in parts)
            {
                if (part.Text is not null)
                    text.Append(part.Text);
                else if (part.Count)
                    text.Append(count?.ToString(CultureInfo.InvariantCulture));
                else
                    text.Append(Format(Read(value, part.Path!)));
            }

            return text.ToString();
        }
    }

    /// <summary>Present: not null, not an empty string, list or map.</summary>
    private static bool IsPresent(object? value) => Normalize(value) switch
    {
        null => false,
        string s => s.Length > 0,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString()!.Length > 0,
        JsonElement { ValueKind: JsonValueKind.Array } json => json.GetArrayLength() > 0,
        JsonElement { ValueKind: JsonValueKind.Object } json => json.EnumerateObject().Any(),
        JsonElement => true,
        IEnumerable items => items.GetEnumerator().MoveNext(),
        _ => true,
    };

    /// <summary>The items of a list (not of a string or a map), or <see langword="null"/>.</summary>
    private static IEnumerable? Items(object? value) => Normalize(value) switch
    {
        null or string => null,
        JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray().Select(e => (object)e),
        JsonElement => null,
        IEnumerable items when !IsMap(items, out _) => items,
        _ => null,
    };

    /// <summary>The entries of a map, or <see langword="false"/>.</summary>
    private static bool IsMap(object value, out List<KeyValuePair<string, object?>> entries)
    {
        entries = [];
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                entries.AddRange(json.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, (object?)p.Value)));
                return true;
            case JsonElement:
            case string:
                return false;
        }

        return !MemberCatalog.IsProxyType(value.GetType()) && Members.TryReadMap(value, out entries);
    }

    /// <summary>
    /// Whether a value equals a declared JSON value: strings ordinally, numbers by value, booleans; an enum member by its JSON name, a
    /// resolved object by its id; lists item by item and maps by keys and values. <c>null</c> matches a null or missing value.
    /// </summary>
    private static bool Equal(object? actual, JsonElement expected)
    {
        actual = Normalize(actual);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                return actual is null;
            case JsonValueKind.String:
                var text = expected.GetString();
                return actual switch
                {
                    string s => string.Equals(s, text, StringComparison.Ordinal),
                    Enum e => string.Equals(Members.EnumName(e), text, StringComparison.Ordinal),
                    IResolvedObject o => string.Equals(o.Id, text, StringComparison.Ordinal),
                    JsonElement { ValueKind: JsonValueKind.String } json => string.Equals(json.GetString(), text, StringComparison.Ordinal),
                    _ => false,
                };
            case JsonValueKind.True:
            case JsonValueKind.False:
                var flag = expected.ValueKind == JsonValueKind.True;
                return actual switch
                {
                    bool b => b == flag,
                    JsonElement { ValueKind: JsonValueKind.True } => flag,
                    JsonElement { ValueKind: JsonValueKind.False } => !flag,
                    _ => false,
                };
            case JsonValueKind.Number:
                if (expected.TryGetDecimal(out var number) && TryDecimal(actual, out var value))
                    return number == value;
                return TryDouble(actual, out var real) && real.Equals(expected.GetDouble());
            case JsonValueKind.Array:
                if (Items(actual) is not { } items)
                    return false;
                var list = items.Cast<object?>().ToList();
                if (list.Count != expected.GetArrayLength())
                    return false;
                var i = 0;
                foreach (var item in expected.EnumerateArray())
                {
                    if (!Equal(list[i++], item))
                        return false;
                }

                return true;
            case JsonValueKind.Object:
                if (actual is null || !IsMap(actual, out var entries))
                    return false;
                var keys = expected.EnumerateObject().ToList();
                if (keys.Count != entries.Count)
                    return false;
                foreach (var key in keys)
                {
                    var match = entries.FindIndex(e => string.Equals(e.Key, key.Name, StringComparison.Ordinal));
                    if (match < 0 || !Equal(entries[match].Value, key.Value))
                        return false;
                }

                return true;
            default:
                return false;
        }
    }

    private static bool TryDecimal(object? value, out decimal number)
    {
        number = 0;
        try
        {
            switch (value)
            {
                case JsonElement { ValueKind: JsonValueKind.Number } json:
                    return json.TryGetDecimal(out number);
                case int or long or short or byte or sbyte or ushort or uint or ulong or decimal:
                    number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return true;
                case double d when double.IsFinite(d):
                    number = (decimal)d;
                    return true;
                case float f when float.IsFinite(f):
                    number = (decimal)f;
                    return true;
                default:
                    return false;
            }
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryDouble(object? value, out double number)
    {
        number = 0;
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Number } json:
                return json.TryGetDouble(out number);
            case int or long or short or byte or sbyte or ushort or uint or ulong or decimal or double or float:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A value in a message: a string as it is, a number in invariant form, <c>true</c> or <c>false</c>, an enum member's JSON name, a
    /// resolved object's name (its id when it has none), nothing for null, and a list or map as compact JSON.
    /// </summary>
    private static string Format(object? value)
    {
        value = Normalize(value);
        switch (value)
        {
            case null:
                return "";
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case Enum e:
                return Members.EnumName(e);
            case JsonElement { ValueKind: JsonValueKind.String } json:
                return json.GetString()!;
            case JsonElement { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } json:
                return json.GetRawText();
            case JsonElement or IEnumerable:
                using (var stream = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(stream, Compact))
                        Write(writer, value);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }

            case IConvertible convertible when TryDouble(value, out _):
                return convertible.ToString(CultureInfo.InvariantCulture);
            default:
                return NameOf(value);
        }
    }

    /// <summary>An object's <c>name</c>, else its id.</summary>
    private static string NameOf(object value)
    {
        var type = value.GetType();
        if (MemberCatalog.IsProxyType(type))
        {
            var table = Members.GetTable(type);
            if (table.TryGetIndex("name", out var index) && table.Members[index].Property?.GetValue(value) is string { Length: > 0 } name)
                return name;
            if (table.TryGetIndex("id", out index) && table.Members[index].Property?.GetValue(value) is string id)
                return id;
        }

        return value is IResolvedObject resolved ? resolved.Id : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        value = Normalize(value);
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case Enum e:
                writer.WriteStringValue(Members.EnumName(e));
                return;
            case JsonElement json:
                json.WriteTo(writer);
                return;
            case int or long or short or byte or sbyte or ushort or uint:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong u:
                writer.WriteNumberValue(u);
                return;
            case decimal m:
                writer.WriteNumberValue(m);
                return;
            case double or float:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;
        }

        if (IsMap(value, out var entries))
        {
            writer.WriteStartObject();
            foreach (var (key, entry) in entries)
            {
                writer.WritePropertyName(key);
                Write(writer, entry);
            }

            writer.WriteEndObject();
        }
        else if (value is IEnumerable items)
        {
            writer.WriteStartArray();
            foreach (var item in items)
                Write(writer, item);
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteStringValue(NameOf(value));
        }
    }
}
