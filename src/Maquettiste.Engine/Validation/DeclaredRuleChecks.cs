using System.Collections;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Validation;

/// <summary>One piece of a declared rule's message: literal text, a path placeholder or <c>{$count}</c>.</summary>
/// <param name="Text">The literal text, or <see langword="null"/>.</param>
/// <param name="Path">The placeholder's path, or <see langword="null"/>.</param>
/// <param name="Count">Whether the piece is <c>{$count}</c>.</param>
internal sealed record MessagePart(string? Text, string? Path, bool Count);

/// <summary>
/// The static checks of rule declaration files (next-phase-2-design.md section 5.6), on whole-model validations only, before resolution:
/// MQ5015, an id another declaration or a rule script already uses, on the later path; MQ5016, a kind, stereotype key, package id, path,
/// calculated value, link kind, bounds or message placeholder that does not exist or does not apply. Each finding is on the declaration
/// file with a pointer into it. A project without declaration files is never walked.
/// </summary>
internal static class DeclaredRuleChecks
{
    /// <summary>The reflection tables of the members scripts see, shared with the evaluator.</summary>
    internal static MemberCatalog Members { get; } = new();

    /// <summary>The kinds the index names: every element kind and every part kind.</summary>
    internal static IReadOnlySet<string> LinkKinds { get; } =
        KindInfo.All.Select(k => k.Name).Concat(ModelIndexer.SubElementKinds).ToHashSet(StringComparer.Ordinal);

    /// <summary>Checks every declaration of a snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="scriptRules">The rule ids the rule scripts register, with the file that registers each; <see langword="null"/> when
    /// a rule script did not load (or was not loaded), so ids are compared among declarations only.</param>
    /// <param name="declares">Whether a kind declares a calculated value of a name; <see langword="null"/> when the calculation files
    /// did not load (or were not loaded), so calculated values in paths are not checked.</param>
    /// <param name="written">The path of the declaration file a write checks alone, or <see langword="null"/> for a whole-model
    /// validation: a write also reports MQ5015 on that file when the other holder of the id is the later path, so a clean write never
    /// turns into an error on the next validation.</param>
    /// <returns>The findings, in no particular order.</returns>
    public static List<Diagnostic> Check(ModelSnapshot model, IReadOnlyDictionary<string, string>? scriptRules, Func<string, string, bool>? declares,
        string? written = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var found = new List<Diagnostic>();
        if (model.DeclaredRules.Count == 0)
            return found;
        CheckIds(model, scriptRules, written, found);
        foreach (var file in model.DeclaredRules)
        {
            for (var i = 0; i < file.Rules.Rules.Count; i++)
                new RuleCheck(model, file.Path, "/rules/" + i, declares, found).Check(file.Rules.Rules[i]);
        }

        return found;
    }

    /// <summary>
    /// MQ5015: every use of an id after its first, in path order (a declaration file at the rule's id, a rule script on the file); for a
    /// write, also the first use when it is in <paramref name="written"/> and another file uses the id later.
    /// </summary>
    private static void CheckIds(ModelSnapshot model, IReadOnlyDictionary<string, string>? scriptRules, string? written, List<Diagnostic> found)
    {
        var uses = new List<(string Id, string Path, int Index, bool Script)>();
        foreach (var file in model.DeclaredRules)
        {
            for (var i = 0; i < file.Rules.Rules.Count; i++)
                uses.Add((file.Rules.Rules[i].Id, file.Path, i, false));
        }

        var declared = uses.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, path) in scriptRules ?? new Dictionary<string, string>())
        {
            if (declared.Contains(id))
                uses.Add((id, path, -1, true));
        }

        foreach (var group in uses.GroupBy(u => u.Id, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(u => u.Path, StringComparer.Ordinal).ThenBy(u => u.Index).ToList();
            var first = ordered[0];
            foreach (var later in ordered.Skip(1))
            {
                var by = first.Script ? "the rule script " + first.Path : first.Path;
                found.Add(later.Script
                    ? RuleCatalog.Create("MQ5015", $"The rule '{later.Id}' this script registers is also declared in {by}.", null, later.Path, null)
                    : RuleCatalog.Create("MQ5015", $"The rule id '{later.Id}' is already used by {by}.", null, later.Path, $"/rules/{later.Index}/id"));
            }

            if (written is not null && !first.Script && string.Equals(first.Path, written, StringComparison.Ordinal)
                && ordered.FirstOrDefault(u => !string.Equals(u.Path, written, StringComparison.Ordinal)) is { Path: not null } other)
            {
                var by = other.Script ? "the rule script " + other.Path : other.Path;
                found.Add(RuleCatalog.Create("MQ5015", $"The rule id '{first.Id}' is also used by {by}.", null, first.Path, $"/rules/{first.Index}/id"));
            }
        }
    }

    /// <summary>
    /// Splits a message into its pieces: <c>{{</c> and <c>}}</c> write braces, <c>{$count}</c> is the count, and any other <c>{...}</c> a
    /// path.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="parts">The pieces.</param>
    /// <param name="error">Why the message does not parse, or <see langword="null"/>.</param>
    /// <returns>Whether it parses.</returns>
    internal static bool TryParseMessage(string message, out List<MessagePart> parts, out string? error)
    {
        parts = [];
        error = null;
        var text = new StringBuilder();
        for (var i = 0; i < message.Length; i++)
        {
            var c = message[i];
            if (c == '}' && i + 1 < message.Length && message[i + 1] == '}')
            {
                text.Append('}');
                i++;
            }
            else if (c == '}')
            {
                error = "has a } that closes no placeholder; write }} for a brace";
                return false;
            }
            else if (c == '{' && i + 1 < message.Length && message[i + 1] == '{')
            {
                text.Append('{');
                i++;
            }
            else if (c == '{')
            {
                var end = message.IndexOf('}', i + 1);
                if (end < 0)
                {
                    error = "has a { that opens no placeholder; write {{ for a brace";
                    return false;
                }

                if (text.Length > 0)
                    parts.Add(new MessagePart(text.ToString(), null, false));
                text.Clear();
                var inner = message[(i + 1)..end];
                if (string.Equals(inner, "$count", StringComparison.Ordinal))
                {
                    parts.Add(new MessagePart(null, null, true));
                }
                else if (IsPath(inner))
                {
                    parts.Add(new MessagePart(null, inner, false));
                }
                else
                {
                    error = $"has the placeholder {{{inner}}}, which is not a path";
                    return false;
                }

                i = end;
            }
            else
            {
                text.Append(c);
            }
        }

        if (text.Length > 0)
            parts.Add(new MessagePart(text.ToString(), null, false));
        return true;
    }

    private static bool IsPath(string path) =>
        path.Length > 0 && path.Split('.').All(s => s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c is '{' or '}'));

    /// <summary>The static shape of a member's type: <see langword="null"/> when nothing is known (a JSON value, an object).</summary>
    private static Type? Known(Type type) =>
        type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonElement?) ? null : type;

    /// <summary>Whether a static type is a map of string keys, with its value type.</summary>
    internal static bool IsMap(Type type, out Type? value)
    {
        value = null;
        foreach (var candidate in type.GetInterfaces().Prepend(type))
        {
            if (!candidate.IsGenericType)
                continue;
            var definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>) && candidate.GetGenericArguments()[0] is { IsGenericType: true } item
                && item.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) && item.GetGenericArguments()[0] == typeof(string))
            {
                value = Known(item.GetGenericArguments()[1]);
                return true;
            }
        }

        return typeof(IDictionary).IsAssignableFrom(type);
    }

    /// <summary>Whether a static type is a list (not a string, not a map), with its item type.</summary>
    private static bool IsList(Type type, out Type? item)
    {
        item = null;
        if (type == typeof(string) || IsMap(type, out _))
            return false;
        if (type.GetInterfaces().Prepend(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } enumerable)
        {
            item = Known(enumerable.GetGenericArguments()[0]);
            return true;
        }

        return typeof(IEnumerable).IsAssignableFrom(type);
    }

    /// <summary>Whether a static type is an object whose members a path names (a resolved object or a record it holds).</summary>
    private static bool IsObject(Type type) => type != typeof(RCalculated) && MemberCatalog.IsProxyType(type);

    private static string? KindOf(Type type) => ResolvedKinds.All.FirstOrDefault(k => ResolvedKinds.TypeOf(k) == type);

    /// <summary>The checks of one rule, with the pointer of its entry.</summary>
    private sealed class RuleCheck(ModelSnapshot model, string path, string pointer, Func<string, string, bool>? declares, List<Diagnostic> found)
    {
        public void Check(DeclaredRule rule)
        {
            var select = rule.Select;
            var starts = new List<Type?>();
            for (var j = 0; j < select.Kind.Count; j++)
            {
                if (ResolvedKinds.TypeOf(select.Kind[j]) is { } type)
                    starts.Add(type);
                else
                    Add(At("/select/kind", select.Kind, j), $"'{select.Kind[j]}' is not a kind of the resolved model.");
            }

            for (var j = 0; j < select.Stereotype.Count; j++)
            {
                if (model.GetStereotype(select.Stereotype[j]) is null)
                    Add(At("/select/stereotype", select.Stereotype, j), $"No stereotype has the key '{select.Stereotype[j]}'.");
            }

            for (var j = 0; j < select.Package.Count; j++)
            {
                if (model.Get<Package>(select.Package[j]) is null)
                    Add(At("/select/package", select.Package, j), $"No package has the id '{select.Package[j]}'.");
            }

            Condition(rule.Require, "/require", starts);
            if (!TryParseMessage(rule.Message, out var parts, out var error))
            {
                Add("/message", "The message " + error + ".");
                return;
            }

            if (parts.Any(p => p.Count) && rule.Require is { Count: null, References: null, ReferencedBy: null })
                Add("/message", "The message inserts {$count}, but the rule's require is not a count, references or referencedBy.");
            foreach (var part in parts.Where(p => p.Path is not null))
                Walk(part.Path!, "/message", starts, "The placeholder {" + part.Path + "}");
        }

        private void Condition(RuleCondition condition, string at, IReadOnlyList<Type?> starts)
        {
            for (var i = 0; i < (condition.All?.Count ?? 0); i++)
                Condition(condition.All![i], $"{at}/all/{i}", starts);
            for (var i = 0; i < (condition.Any?.Count ?? 0); i++)
                Condition(condition.Any![i], $"{at}/any/{i}", starts);
            if (condition.Not is { } not)
                Condition(not, at + "/not", starts);
            if (condition.Has is { } has)
                Walk(has, at + "/has", starts, "The path '" + has + "'");
            if (condition.Equal is { } equal)
                Walk(equal.Path, at + "/equals/path", starts, "The path '" + equal.Path + "'");
            if (condition.Count is { } count)
            {
                Bounds(count.Min, count.Max, at + "/count");
                if (Walk(count.Of, at + "/count/of", starts, "The path '" + count.Of + "'") is { } ends)
                {
                    var items = new List<Type?>();
                    foreach (var end in ends)
                    {
                        if (end is null)
                            items.Add(null);
                        else if (IsList(end, out var item) && (item is null || IsObject(item)))
                            items.Add(item);
                    }

                    if (items.Count == 0 && ends.Count > 0)
                        Add(at + "/count/of", $"count.of '{count.Of}' is not a list of objects; a list of plain values takes has or equals.");
                    else if (count.Where is { } where)
                        Condition(where, at + "/count/where", items);
                }
            }

            if (condition.References is { } references)
                Links(references, at + "/references");
            if (condition.ReferencedBy is { } referencedBy)
                Links(referencedBy, at + "/referencedBy");
        }

        private void Links(RuleReferences links, string at)
        {
            for (var j = 0; j < links.Kind.Count; j++)
            {
                if (!LinkKinds.Contains(links.Kind[j]))
                    Add(At(at + "/kind", links.Kind, j), $"'{links.Kind[j]}' is not a kind the index names.");
            }

            Bounds(links.Min, links.Max, at);
        }

        private void Bounds(int? min, int? max, string at)
        {
            if (min is { } low && max is { } high && low > high)
                Add(at + "/max", $"max {high} is below min {low}.");
        }

        /// <summary>
        /// Walks a path from the start types; reports MQ5016 when no start type can read it, and returns the types it ends at (an
        /// unknown type as <see langword="null"/>), or <see langword="null"/> when it was reported.
        /// </summary>
        private List<Type?>? Walk(string path, string at, IReadOnlyList<Type?> starts, string subject)
        {
            if (starts.Count == 0)
                return null; // no kind to read it from was valid: that is reported already
            var ends = new List<Type?>();
            string? firstError = null;
            var missingEverywhere = true;
            var segments = path.Split('.');
            foreach (var start in starts)
            {
                var (end, error, missingFirst) = WalkFrom(start, segments);
                missingEverywhere &= missingFirst;
                if (error is null)
                    ends.Add(end);
                else
                    firstError ??= error;
            }

            if (ends.Count > 0)
                return ends;
            Add(at, missingEverywhere ? $"{subject} names '{segments[0]}', which no selected kind has." : $"{subject} {firstError}.");
            return null;
        }

        private (Type? End, string? Error, bool MissingFirst) WalkFrom(Type? start, string[] segments)
        {
            var type = start;
            Type? holder = null;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (type is null)
                    return (null, null, false);
                if (type == typeof(RCalculated))
                {
                    if (declares is not null && holder is not null && KindOf(holder) is { } kind && !declares(kind, segment))
                        return (null, $"reads the calculated value '{segment}', which {kind} does not declare", false);
                    type = null;
                    continue;
                }

                if (IsObject(type))
                {
                    if (!Members.GetTable(type).TryGetIndex(segment, out var index) || Members.GetTable(type).Members[index].Property is not { } property)
                    {
                        if (i == 0)
                            return (null, "", true);
                        if (!type.IsSealed)
                            return (null, null, false); // a subtype may have it
                        return (null, $"goes on to '{segment}', which {segments[i - 1]} does not have", false);
                    }

                    holder = type;
                    type = Known(property.PropertyType);
                    continue;
                }

                if (IsMap(type, out var value))
                {
                    type = value;
                    continue;
                }

                return (null, $"goes on past '{segments[i - 1]}', which is neither a map nor an object", false);
            }

            return (type, null, false);
        }

        /// <summary>The pointer of one value of a key that reads one value or a list.</summary>
        private static string At(string member, IReadOnlyList<string> values, int index) => member + (values.Count > 1 ? "/" + index : "");

        /// <summary>An MQ5016 at a pointer relative to the rule's entry.</summary>
        private void Add(string at, string message) => found.Add(RuleCatalog.Create("MQ5016", message, null, path, pointer + at));
    }
}
