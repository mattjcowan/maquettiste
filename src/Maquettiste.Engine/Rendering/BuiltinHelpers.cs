using System.Buffers;
using System.Collections;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Text;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Maquettiste.Engine.Rendering;

/// <summary>An error raised by a helper: the rule and message of the diagnostic the unit fails with.</summary>
/// <param name="rule">The rule id (MQ6006 by default, MQ6012 for non-deterministic builtins).</param>
/// <param name="message">The message.</param>
internal sealed class RenderHelperException(string rule, string message) : Exception(message)
{
    /// <summary>The rule id.</summary>
    public string Rule { get; } = rule;
}

/// <summary>A Scriban function backed by a delegate that receives the unit's <see cref="TrackingTemplateContext"/>.</summary>
/// <param name="name">The template name of the function.</param>
/// <param name="minArguments">The fewest arguments.</param>
/// <param name="maxArguments">The most arguments.</param>
/// <param name="body">The implementation.</param>
internal sealed class HelperFunction(string name, int minArguments, int maxArguments, Func<TrackingTemplateContext, IReadOnlyList<object?>, object?> body)
    : IScriptCustomFunction
{
    /// <summary>The template name.</summary>
    public string Name { get; } = name;

    /// <inheritdoc/>
    public int RequiredParameterCount => minArguments;

    /// <inheritdoc/>
    public int ParameterCount => maxArguments;

    /// <inheritdoc/>
    public ScriptVarParamKind VarParamKind => ScriptVarParamKind.Direct;

    /// <inheritdoc/>
    public Type ReturnType => typeof(object);

    /// <inheritdoc/>
    public ScriptParameterInfo GetParameterInfo(int index) => new(typeof(object), "arg" + index.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc/>
    public object? Invoke(TemplateContext context, ScriptNode? callerContext, ScriptArray arguments, ScriptBlockStatement? blockStatement)
    {
        if (arguments.Count < minArguments || arguments.Count > maxArguments)
        {
            var expected = minArguments == maxArguments
                ? minArguments.ToString(CultureInfo.InvariantCulture)
                : $"{minArguments.ToString(CultureInfo.InvariantCulture)} to {maxArguments.ToString(CultureInfo.InvariantCulture)}";
            throw new RenderHelperException("MQ6006", $"`{Name}` takes {expected} argument(s), not {arguments.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        object?[] args = [.. arguments];
        return body(TrackingTemplateContext.Of(context), args);
    }

    /// <inheritdoc/>
    public ValueTask<object?> InvokeAsync(TemplateContext context, ScriptNode? callerContext, ScriptArray arguments, ScriptBlockStatement? blockStatement) =>
        new(Invoke(context, callerContext, arguments, blockStatement));
}

/// <summary>
/// A Scriban function that keeps another function's signature (so named and optional arguments bind the same way) but runs its
/// own body, which may call the original.
/// </summary>
/// <param name="inner">The function whose signature is kept.</param>
/// <param name="body">The implementation.</param>
internal sealed class DelegatingFunction(IScriptCustomFunction inner, Func<TemplateContext, ScriptNode?, ScriptArray, ScriptBlockStatement?, object?> body)
    : IScriptCustomFunction
{
    /// <inheritdoc/>
    public int RequiredParameterCount => inner.RequiredParameterCount;

    /// <inheritdoc/>
    public int ParameterCount => inner.ParameterCount;

    /// <inheritdoc/>
    public ScriptVarParamKind VarParamKind => inner.VarParamKind;

    /// <inheritdoc/>
    public Type ReturnType => inner.ReturnType;

    /// <inheritdoc/>
    public ScriptParameterInfo GetParameterInfo(int index) => inner.GetParameterInfo(index);

    /// <inheritdoc/>
    public object? Invoke(TemplateContext context, ScriptNode? callerContext, ScriptArray arguments, ScriptBlockStatement? blockStatement) =>
        body(context, callerContext, arguments, blockStatement);

    /// <inheritdoc/>
    public ValueTask<object?> InvokeAsync(TemplateContext context, ScriptNode? callerContext, ScriptArray arguments, ScriptBlockStatement? blockStatement) =>
        new(Invoke(context, callerContext, arguments, blockStatement));
}

/// <summary>
/// The built-in template helpers (engine-design.md section 9), all pure: casing through <see cref="Casing"/>, inflection through
/// the run's <see cref="Inflector"/>, <c>type_of</c>, SQL quoting and literals, indentation, escaping, canonical JSON, model
/// predicates, <c>lookup</c>, <c>banner</c> and <c>file</c>. <see cref="CreateBuiltins"/> also replaces Scriban's non-deterministic
/// builtins (MQ6012) and its culture-sensitive string and sort functions with invariant, ordinal ones.
/// </summary>
internal static class BuiltinHelpers
{
    /// <summary>The helper names this class registers.</summary>
    public static readonly FrozenSet<string> Names = FrozenSet.Create(StringComparer.Ordinal,
        "pascal", "camel", "snake", "kebab", "upper_snake", "pluralize", "singularize", "type_of", "sql_quote", "sql_literal",
        "indent", "dedent", "escape_md", "escape_xml", "escape_json", "json", "has_stereotype", "has_tag", "in_category", "lookup",
        "banner", "file", "row", "row_uuid", "stable_id", "seed_named", "seed_row", "display_name", "plural_name", "description_of", "label_of", "translate", "has_translation",
        "state_path", "iso_duration_ms", "query_sql", "query_collection_sql", "query_sql_parameters",
        "binding_sql", "binding_sql_parameters", "ledger_ddl", "array_index_of", "array_find", "table_named", "column_named", "toposort");

    /// <summary>The unit variables (engine-design.md section 8); pack helpers may not use these names either.</summary>
    public static readonly FrozenSet<string> Variables = FrozenSet.Create(StringComparer.Ordinal,
        "model", "element", "package", "entity", "relation", "enum", "value_object", "table", "view", "sequence", "routine", "database_type", "sql_object", "query", "reference_type",
        "seed", "locale", "process", "actor", "scenario", "operation", "database", "attribute", "pack", "project", "mapping", "mappings", "schema_diff", "hints", "data", "unit");

    private static readonly JavaScriptEncoder JsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Builds the shared builtin object of a renderer: Scriban's builtins with <c>date.now</c>, <c>date.utc_now</c>,
    /// <c>math.random</c>, <c>math.uuid</c>, <c>object.eval</c> and <c>object.eval_template</c> replaced by functions that fail with
    /// MQ6012; <c>string.capitalize</c>, <c>string.capitalizewords</c>, <c>string.starts_with</c>, <c>string.ends_with</c>,
    /// <c>string.index_of</c>, <c>array.sort</c> and <c>regex.match</c>, <c>regex.matches</c>, <c>regex.replace</c>,
    /// <c>regex.split</c> replaced by culture-invariant, ordinal versions; and the Maquettiste helpers at
    /// the root. Every object is made read-only, so contexts on many threads can share it and no template can change it.
    /// </summary>
    /// <returns>The builtin object.</returns>
    public static ScriptObject CreateBuiltins()
    {
        var builtins = TemplateContext.GetDefaultBuiltinObject();
        Replace(builtins, "date", "now", Nondeterministic("date.now"));
        Replace(builtins, "date", "utc_now", Nondeterministic("date.utc_now"));
        ReplaceDates(builtins);
        Replace(builtins, "math", "random", Nondeterministic("math.random"));
        Replace(builtins, "math", "uuid", Nondeterministic("math.uuid"));
        Replace(builtins, "object", "eval", Nondeterministic("object.eval"));
        Replace(builtins, "object", "eval_template", Nondeterministic("object.eval_template"));
        Replace(builtins, "string", "capitalize", new HelperFunction("string.capitalize", 1, 1, (_, a) => Capitalize(AsText(a[0]), words: false)));
        Replace(builtins, "string", "capitalizewords", new HelperFunction("string.capitalizewords", 1, 1, (_, a) => Capitalize(AsText(a[0]), words: true)));
        Replace(builtins, "string", "starts_with", new HelperFunction("string.starts_with", 2, 2, (_, a) =>
            AsText(a[1]).Length > 0 && AsText(a[0]).StartsWith(AsText(a[1]), StringComparison.Ordinal)));
        Replace(builtins, "string", "ends_with", new HelperFunction("string.ends_with", 2, 2, (_, a) =>
            AsText(a[1]).Length > 0 && AsText(a[0]).EndsWith(AsText(a[1]), StringComparison.Ordinal)));
        Replace(builtins, "string", "index_of", new HelperFunction("string.index_of", 2, 2, (_, a) =>
            AsText(a[0]).IndexOf(AsText(a[1]), StringComparison.Ordinal)));
        Replace(builtins, "array", "sort", new HelperFunction("array.sort", 1, 2, (c, a) => Sort(c, a[0], a.Count > 1 ? AsText(a[1]) : null)));
        // Pure functions of their text arguments, so a match timeout (wall-clock, possibly a stalled thread) is retried once
        // unless the render is being cancelled; a catastrophic pattern times out again and fails with MQ6007.
        Replace(builtins, "regex", "match", new HelperFunction("regex.match", 2, 3, (c, a) => RetryMatch(c, () =>
        {
            var match = Regex.Match(AsText(a[0]), AsText(a[1]), RegexOptionsOf(a, 2), c.RegexTimeOut);
            return match.Success ? new ScriptArray(match.Groups.Cast<Group>().Select(g => (object?)g.Value)) : [];
        })));
        Replace(builtins, "regex", "matches", new HelperFunction("regex.matches", 2, 3, (c, a) => RetryMatch(c, () =>
            new ScriptArray(Regex.Matches(AsText(a[0]), AsText(a[1]), RegexOptionsOf(a, 2), c.RegexTimeOut)
                .Where(m => m.Success)
                .Select(m => (object?)new ScriptArray(m.Groups.Cast<Group>().Select(g => (object?)g.Value)))))));
        Replace(builtins, "regex", "replace", new HelperFunction("regex.replace", 3, 4, (c, a) => RetryMatch(c, () =>
            Regex.Replace(AsText(a[0]), AsText(a[1]), AsText(a[2]), RegexOptionsOf(a, 3), c.RegexTimeOut))));
        Replace(builtins, "regex", "split", new HelperFunction("regex.split", 2, 3, (c, a) => RetryMatch(c, () =>
            new ScriptArray(Regex.Split(AsText(a[0]), AsText(a[1]), RegexOptionsOf(a, 2), c.RegexTimeOut).Select(p => (object?)p)))));

        Add(builtins, "pascal", 1, 1, (_, a) => Casing.Pascal(AsText(a[0])));
        Add(builtins, "camel", 1, 1, (_, a) => Casing.Camel(AsText(a[0])));
        Add(builtins, "snake", 1, 1, (_, a) => Casing.Snake(AsText(a[0])));
        Add(builtins, "kebab", 1, 1, (_, a) => Casing.Kebab(AsText(a[0])));
        Add(builtins, "upper_snake", 1, 1, (_, a) => Casing.UpperSnake(AsText(a[0])));
        Add(builtins, "pluralize", 1, 1, (c, a) => Pluralize(c, a[0]));
        Add(builtins, "singularize", 1, 1, (c, a) => Singularize(c, a[0]));
        Add(builtins, "type_of", 2, 2, (c, a) => TypeOf.Map(c, a[0], AsText(a[1])));
        Add(builtins, "sql_quote", 1, 3, (c, a) => SqlQuote(c, a[0], a.Count > 1 ? a[1] : null, a.Count > 2 ? AsText(a[2]) : null));
        Add(builtins, "sql_literal", 2, 2, (c, a) => SqlLiteral(c, a[0], a[1]));
        Add(builtins, "query_sql", 1, 3, (c, a) => QuerySqlText(c, "query_sql", a));
        Add(builtins, "query_collection_sql", 1, 3, (c, a) => QuerySqlText(c, "query_collection_sql", a));
        Add(builtins, "query_sql_parameters", 1, 3, (c, a) => QuerySqlText(c, "query_sql_parameters", a));
        Add(builtins, "binding_sql", 2, 4, (c, a) => BindingSqlText(c, "binding_sql", a));
        Add(builtins, "binding_sql_parameters", 2, 4, (c, a) => BindingSqlText(c, "binding_sql_parameters", a));
        Add(builtins, "ledger_ddl", 1, 1, (c, a) => LedgerDdl(c, a[0]));
        Add(builtins, "indent", 2, 2, (_, a) => Indent(AsText(a[0]), IndentPrefix(a[1])));
        Add(builtins, "dedent", 1, 1, (_, a) => Dedent(AsText(a[0])));
        Add(builtins, "escape_md", 1, 1, (_, a) => EscapeMarkdown(AsText(a[0])));
        Add(builtins, "escape_xml", 1, 1, (_, a) => EscapeXml(AsText(a[0])));
        Add(builtins, "escape_json", 1, 1, (_, a) => EscapeJson(AsText(a[0])));
        Add(builtins, "json", 1, 2, (c, a) => Json(c, a[0], a.Count > 1 ? a[1] : null));
        Add(builtins, "array_index_of", 2, 3, (c, a) => ArrayIndexOf(c, a));
        Add(builtins, "array_find", 3, 3, (c, a) => ArrayFind(c, a[0], a[1], a[2]));
        Add(builtins, "table_named", 1, 2, (c, a) => TableNamed(c, a[0], a.Count > 1 ? a[1] : null));
        Add(builtins, "column_named", 2, 2, (c, a) => ColumnNamed(c, a[0], a[1]));
        Add(builtins, "toposort", 2, 2, (_, a) => Toposort(a[0], a[1]));
        Add(builtins, "has_stereotype", 2, 2, (c, a) => HasStereotype(c, a[0], AsText(a[1])));
        Add(builtins, "has_tag", 2, 2, (c, a) => HasTag(c, a[0], AsText(a[1])));
        Add(builtins, "in_category", 2, 2, (c, a) => InCategory(c, a[0], AsText(a[1])));
        Add(builtins, "lookup", 1, 1, (c, a) => Lookup(c, AsText(a[0])));
        Add(builtins, "row", 2, 2, (c, a) => Row(c, a[0], a[1]));
        Add(builtins, "row_uuid", 1, 1, (c, a) => RowUuid(c, a[0]));
        Add(builtins, "stable_id", 2, 2, (c, a) => StableId(c, a[0], a[1]));
        Add(builtins, "seed_named", 1, 1, (c, a) => SeedNamed(c, a[0]));
        Add(builtins, "seed_row", 2, 2, (c, a) => SeedRow(c, a[0], a[1]));
        Add(builtins, "state_path", 1, 1, (c, a) => StatePath(c, a[0]));
        Add(builtins, "iso_duration_ms", 1, 1, (_, a) => IsoDurationMs(a[0]));
        LocalizationHelpers.Register((name, min, max, body) => Add(builtins, name, min, max, body));
        Add(builtins, "banner", 1, 1, (c, a) => Banner(AsText(a[0]), c.Unit.Planned.Pack.Name, c.Unit.Planned.Unit.Id));
        Add(builtins, "file", 2, 2, (c, a) => File(c, a[0], a[1]));

        foreach (var member in builtins.GetMembers().ToArray())
        {
            if (builtins[member] is ScriptObject child)
                child.IsReadOnly = true;
        }

        builtins.IsReadOnly = true;
        return builtins;
    }

    /// <summary>A function that fails with MQ6012 when called.</summary>
    /// <param name="name">The builtin's name.</param>
    /// <returns>The function.</returns>
    public static HelperFunction Nondeterministic(string name) => new(name, 0, 16, (_, _) =>
        throw new RenderHelperException("MQ6012", $"`{name}` is not deterministic and cannot be used in templates; generated output must depend only on the model and the pack."));

    /// <summary>
    /// Makes Scriban's date functions independent of the host's time zone: <c>date.parse</c> reads offsets and <c>Z</c> as UTC
    /// instants and returns UTC dates (Scriban converts them to machine-local time), <c>date.to_string</c> treats every date as UTC
    /// (so <c>%Z</c> prints <c>+00:00</c>), and <c>date.parse_to_string</c> composes the two.
    /// </summary>
    private static void ReplaceDates(ScriptObject builtins)
    {
        var date = (ScriptObject)builtins["date"]!;
        var parse = (IScriptCustomFunction)date["parse"]!;
        var toString = (IScriptCustomFunction)date["to_string"]!;
        var parseToString = (IScriptCustomFunction)date["parse_to_string"]!;

        object? Parse(TemplateContext context, ScriptNode? caller, object? text, object? pattern, object? culture)
        {
            var source = AsText(text);
            var format = pattern is null ? "" : AsText(pattern);
            if (format.Length == 0)
            {
                var info = culture is null || AsText(culture).Length == 0 ? CultureInfo.InvariantCulture : CultureOf(AsText(culture));
                if (DateTime.TryParse(source, info, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                    return parsed;
            }
            else if (ReadsOffset(format))
            {
                throw new RenderHelperException("MQ6006",
                    "`date.parse` cannot read a UTC offset (%Z) through a pattern without depending on the machine's time zone; parse without a pattern (offsets and Z are then read as UTC).");
            }

            return AsUtc(parse.Invoke(context, caller, new ScriptArray { source, format.Length == 0 ? null : format, culture }, null));
        }

        date.Remove("parse");
        date.SetValue("parse", new DelegatingFunction(parse, (c, n, a, _) => Parse(c, n, At(a, 0), At(a, 1), At(a, 2))), true);
        date.Remove("to_string");
        date.SetValue("to_string", new DelegatingFunction(toString, (c, n, a, b) =>
        {
            var args = new ScriptArray(a);
            if (args.Count > 0)
                args[0] = AsUtc(args[0]);
            return toString.Invoke(c, n, args, b);
        }), true);
        date.Remove("parse_to_string");
        date.SetValue("parse_to_string", new DelegatingFunction(parseToString, (c, n, a, b) =>
        {
            // text, output_pattern, output_culture, input_pattern, input_culture
            var parsed = Parse(c, n, At(a, 0), At(a, 3), At(a, 4));
            return toString.Invoke(c, n, new ScriptArray { parsed, At(a, 1), At(a, 2) }, b);
        }), true);
    }

    /// <summary>Whether a strftime pattern holds <c>%Z</c> or <c>%z</c> (after any flag characters; <c>%%</c> is a literal).</summary>
    private static bool ReadsOffset(string pattern)
    {
        for (var i = 0; i < pattern.Length - 1; i++)
        {
            if (pattern[i] != '%')
                continue;
            var j = i + 1;
            if (pattern[j] == '%')
            {
                i = j;
                continue;
            }

            while (j < pattern.Length && "-_0^#:".Contains(pattern[j], StringComparison.Ordinal))
                j++;
            if (j < pattern.Length && pattern[j] is 'Z' or 'z')
                return true;
            i = j - 1;
        }

        return false;
    }

    private static object? At(ScriptArray args, int index) => index < args.Count ? args[index] : null;

    private static CultureInfo CultureOf(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            throw new RenderHelperException("MQ6006", $"Unknown culture '{name}'.");
        }
    }

    /// <summary>
    /// A date marked UTC with its wall-clock value kept: Scriban's pattern parse returns the written date and time marked
    /// <see cref="DateTimeKind.Local"/> (it never converts them, since offsets are refused before), so converting would shift them by
    /// the machine's offset. Other values pass through.
    /// </summary>
    private static object? AsUtc(object? value) => value is DateTime { Kind: not DateTimeKind.Utc } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : value;

    private static void Replace(ScriptObject builtins, string owner, string member, IScriptCustomFunction function)
    {
        var target = (ScriptObject)builtins[owner]!;
        target.Remove(member);
        target.SetValue(member, function, true);
    }

    private static void Add(ScriptObject builtins, string name, int min, int max, Func<TrackingTemplateContext, IReadOnlyList<object?>, object?> body) =>
        builtins.SetValue(name, new HelperFunction(name, min, max, body), true);

    /// <summary>The text of a helper argument: strings as they are, <see langword="null"/> as empty, anything else invariant.</summary>
    /// <param name="value">The argument.</param>
    /// <returns>The text.</returns>
    public static string AsText(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IResolvedObject r => throw new RenderHelperException("MQ6006", $"Expected text but got a {r.Kind}; pass one of its members (for example `.name`)."),
        IEnumerable or IScriptObject or ITemplateView => throw new RenderHelperException("MQ6006", $"Expected text but got a {TemplateValues.TypeName(value)}."),
        _ when TemplateMemberCatalog.IsViewType(value.GetType()) =>
            throw new RenderHelperException("MQ6006", $"Expected text but got a {value.GetType().Name}; pass one of its members."),
        _ => value.ToString() ?? "",
    };

    private static string Capitalize(string text, bool words)
    {
        if (text.Length == 0)
            return text;
        var chars = text.ToCharArray();
        var boundary = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i]))
            {
                boundary = true;
                continue;
            }

            if (boundary && char.IsLetter(chars[i]))
                chars[i] = char.ToUpperInvariant(chars[i]);
            boundary = false;
            if (!words)
                break;
        }

        return new string(chars);
    }

    /// <summary>Runs a template regex function, once more after a match timeout unless the render is being cancelled.</summary>
    private static object? RetryMatch(TemplateContext context, Func<object?> match) =>
        Scripting.RegexRetry.Run(match, () => !context.CancellationToken.IsCancellationRequested);

    /// <summary>Scriban's regex option letters (<c>i m s x</c>), always culture-invariant.</summary>
    private static RegexOptions RegexOptionsOf(IReadOnlyList<object?> args, int index)
    {
        var options = RegexOptions.CultureInvariant;
        if (args.Count <= index)
            return options;
        foreach (var c in AsText(args[index]))
        {
            options |= c switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                _ => RegexOptions.None,
            };
        }

        return options;
    }

    private static ScriptArray Sort(TrackingTemplateContext context, object? list, string? member)
    {
        if (list is null)
            return [];
        if (list is not IEnumerable enumerable || list is string)
            throw new RenderHelperException("MQ6006", "`array.sort` expects a list.");
        var items = enumerable.Cast<object?>().ToList();
        Func<object?, object?> key = member is null ? static v => v : v => MemberValue(context, v, member);
        var sorted = items.Select(v => (Item: v, Key: key(v))).OrderBy(p => p.Key, OrdinalValueComparer.Instance).Select(p => p.Item);
        return [.. sorted];
    }

    private static object? MemberValue(TrackingTemplateContext context, object? target, string member)
    {
        object? value = target;
        foreach (var part in member.Split('.'))
        {
            if (value is null)
                return null;
            var accessor = context.GetMemberAccessor(value);
            if (!accessor.TryGetValue(context, context.CurrentSpan, value, part, out value))
                return null;
        }

        return value;
    }

    private static string Pluralize(TrackingTemplateContext context, object? value)
    {
        if (value is RElement element)
        {
            context.Recorder.RecordObject(element);
            return element.PluralName ?? context.Unit.Run.Inflector.Pluralize(element.Name);
        }

        context.Recorder.Record("s:inflection");
        return context.Unit.Run.Inflector.Pluralize(AsText(value));
    }

    private static string Singularize(TrackingTemplateContext context, object? value)
    {
        if (value is RElement element)
        {
            context.Recorder.RecordObject(element);
            return element.Name;
        }

        context.Recorder.Record("s:inflection");
        return context.Unit.Run.Inflector.Singularize(AsText(value));
    }

    private static string SqlQuote(TrackingTemplateContext context, object? name, object? dialectOrDatabase, string? quotingText)
    {
        RDatabase? database = null;
        string text;
        switch (name)
        {
            case RTable table:
                context.Recorder.RecordObject(table);
                text = table.Name;
                database = table.Database;
                break;
            case RColumn column:
                context.Recorder.RecordObject(column);
                text = column.Name;
                database = column.Table?.Database;
                break;
            case RView view:
                context.Recorder.RecordObject(view);
                text = view.Name;
                database = view.Database;
                break;
            case RSequence sequence:
                context.Recorder.RecordObject(sequence);
                text = sequence.Name;
                break;
            case RRoutine routine:
                context.Recorder.RecordObject(routine);
                text = routine.Name;
                database = routine.Database;
                break;
            case RDatabaseType type:
                context.Recorder.RecordObject(type);
                text = type.Name;
                database = type.Database;
                break;
            case RSchema schema:
                context.Recorder.RecordObject(schema);
                text = schema.Name;
                break;
            case RDatabase db:
                context.Recorder.RecordObject(db);
                text = db.Name;
                database = db;
                break;
            default:
                text = AsText(name);
                break;
        }

        Dialect dialect;
        if (dialectOrDatabase is RDatabase target)
        {
            context.Recorder.RecordObject(target);
            database ??= target;
            dialect = ParseDialect(target.Dialect);
        }
        else if (dialectOrDatabase is null)
        {
            database ??= context.Unit.ElementDatabase;
            if (database is null)
                throw new RenderHelperException("MQ6006", "`sql_quote` needs a dialect (or a database) when the name is not a table, column or view.");
            context.Recorder.RecordObject(database);
            dialect = ParseDialect(database.Dialect);
        }
        else
        {
            dialect = ParseDialect(AsText(dialectOrDatabase));
        }

        database ??= context.Unit.ElementDatabase;
        Quoting quoting;
        if (quotingText is not null)
        {
            if (!SqlDialects.TryParseQuoting(quotingText, out quoting))
                throw new RenderHelperException("MQ6006", $"`sql_quote`: unknown quoting '{quotingText}' (always, reserved or never).");
        }
        else if (database is not null)
        {
            context.Recorder.RecordObject(database);
            SqlDialects.TryParseQuoting(database.Quoting, out quoting);
        }
        else
        {
            quoting = Quoting.Reserved;
        }

        if (text.Length == 0)
            throw new RenderHelperException("MQ6006", "`sql_quote`: the identifier is empty.");
        return SqlDialects.Quote(text, dialect, quoting);
    }

    private static string SqlLiteral(TrackingTemplateContext context, object? value, object? dialectOrDatabase)
    {
        Dialect dialect;
        if (dialectOrDatabase is RDatabase database)
        {
            context.Recorder.RecordObject(database);
            dialect = ParseDialect(database.Dialect);
        }
        else
        {
            dialect = ParseDialect(AsText(dialectOrDatabase));
        }

        try
        {
            return SqlDialects.Literal(value switch
            {
                int i => (long)i,
                _ => value,
            }, dialect);
        }
        catch (ArgumentException ex)
        {
            throw new RenderHelperException("MQ6006", "`sql_literal`: " + ex.Message);
        }
    }

    /// <summary>
    /// <c>query_sql(query, dialect?, options?)</c> and <c>query_collection_sql(collection, dialect?, options?)</c>: the statement
    /// <see cref="QuerySql"/> renders for the query's database's dialect, or the one named (a name or a database); <c>options</c>
    /// takes <c>placeholder</c> (<c>@</c>, <c>:</c> or <c>$</c>) and <c>lists</c> (<c>expand</c> or <c>any</c>). A query that cannot
    /// be rendered for the dialect fails the unit with the renderer's diagnostic (MQ4029). <c>query_sql_parameters(query, dialect?,
    /// options?)</c> (a query or a collection; the options may stand in the dialect's place) returns the parameter names of the
    /// statement in placeholder order, the order <c>$n</c> placeholders number them.
    /// </summary>
    private static object QuerySqlText(TrackingTemplateContext context, string name, IReadOnlyList<object?> args)
    {
        if (name == "query_sql_parameters" && args.Count == 2 && args[1] is IDictionary<string, object?>)
            args = [args[0], null, args[1]];
        var dialect = args.Count > 1 ? args[1] switch
        {
            null => null,
            RDatabase database => database.Dialect,
            var other => AsText(other) is { Length: > 0 } text ? text : null,
        } : null;
        var options = QuerySqlOptions.Default;
        if (args.Count > 2 && args[2] is IDictionary<string, object?> map)
        {
            options = new QuerySqlOptions
            {
                Placeholder = map.TryGetValue("placeholder", out var placeholder) && placeholder is not null ? AsText(placeholder) : "@",
                Lists = map.TryGetValue("lists", out var lists) && lists is not null ? AsText(lists) : "expand",
            };
        }
        else if (args.Count > 2 && args[2] is not null)
        {
            throw new RenderHelperException("MQ6006", $"`{name}` takes its options as an object ({{ placeholder: \"@\", lists: \"expand\" }}).");
        }

        QuerySqlText result;
        try
        {
            switch (name, args[0])
            {
                case ("query_sql", RQuery query):
                    context.Recorder.RecordObject(Top(query));
                    result = QuerySql.Render(query, dialect, options);
                    break;
                case ("query_collection_sql" or "query_sql_parameters", RQueryCollection collection):
                    context.Recorder.RecordObject(Top(collection.Query));
                    result = QuerySql.RenderCollection(collection, dialect, options);
                    break;
                case ("query_sql_parameters", RQuery query):
                    context.Recorder.RecordObject(Top(query));
                    result = QuerySql.Render(query, dialect, options);
                    break;
                default:
                    throw new RenderHelperException("MQ6006", name switch
                    {
                        "query_sql" => $"`query_sql` takes a query, not a {TemplateValues.TypeName(args[0])}.",
                        "query_sql_parameters" => $"`query_sql_parameters` takes a query or a collection of one, not a {TemplateValues.TypeName(args[0])}.",
                        _ => $"`query_collection_sql` takes a collection of a query (query.collections[i]), not a {TemplateValues.TypeName(args[0])}.",
                    });
            }
        }
        catch (ArgumentException ex)
        {
            throw new RenderHelperException("MQ6006", $"`{name}`: " + ex.Message);
        }

        if (result.Diagnostics.Count > 0)
            throw new RenderHelperException(result.Diagnostics[0].Rule, result.Diagnostics[0].Message);
        return name == "query_sql_parameters" ? new ScriptArray(result.Parameters) : result.Sql;

        static RQuery Top(RQuery query)
        {
            while (query.Parent is { } parent)
                query = parent;
            return query;
        }
    }

    /// <summary>
    /// <c>binding_sql(binding, statement, dialect?, options?)</c>: one statement of an entity binding (<c>select</c>,
    /// <c>select-by-key</c>, <c>insert</c>, <c>update</c>, <c>delete</c>) as <see cref="BindingSql"/> renders it for the binding's
    /// database's dialect or the one named (a name or a database), empty when the binding does not have it; <c>options</c> takes
    /// <c>placeholder</c> (<c>@</c>, <c>:</c> or <c>$</c>). <c>binding_sql_parameters(binding, statement, dialect?, options?)</c> (the
    /// options may stand in the dialect's place) returns the statement's parameter names in placeholder order.
    /// </summary>
    private static object BindingSqlText(TrackingTemplateContext context, string name, IReadOnlyList<object?> args)
    {
        if (args.Count == 3 && args[2] is IDictionary<string, object?>)
            args = [args[0], args[1], null, args[2]];
        if (args[0] is not REntityBinding binding)
            throw new RenderHelperException("MQ6006", $"`{name}` takes a binding (entity.bindings[database name]), not a {TemplateValues.TypeName(args[0])}.");
        var statement = AsText(args[1]);
        var dialect = args.Count > 2 ? args[2] switch
        {
            null => null,
            RDatabase database => database.Dialect,
            var other => AsText(other) is { Length: > 0 } text ? text : null,
        } : null;
        var options = QuerySqlOptions.Default;
        if (args.Count > 3 && args[3] is IDictionary<string, object?> map)
            options = new QuerySqlOptions { Placeholder = map.TryGetValue("placeholder", out var placeholder) && placeholder is not null ? AsText(placeholder) : "@" };
        else if (args.Count > 3 && args[3] is not null)
            throw new RenderHelperException("MQ6006", $"`{name}` takes its options as an object ({{ placeholder: \"@\" }}).");
        context.Recorder.RecordObject(binding);
        QuerySqlText result;
        try
        {
            result = BindingSql.Render(binding, statement, dialect, options);
        }
        catch (ArgumentException ex)
        {
            throw new RenderHelperException("MQ6006", $"`{name}`: " + ex.Message);
        }

        if (result.Diagnostics.Count > 0)
            throw new RenderHelperException(result.Diagnostics[0].Rule, result.Diagnostics[0].Message);
        return name == "binding_sql_parameters" ? new ScriptArray(result.Parameters) : result.Sql;
    }

    /// <summary>
    /// <c>ledger_ddl db</c> (next-phase-1-design.md section 3.3): the migration ledger's DDL for the database's ledger schema
    /// (<see cref="LedgerContract.Ddl"/>), or <c>""</c> when its dialect has no ledger. It records <c>s:migrations</c>, the key of
    /// <c>migrations.ledgerSchema</c>, so every script that writes ledger rows re-renders when the schema changes.
    /// </summary>
    private static string LedgerDdl(TrackingTemplateContext context, object? value)
    {
        if (value is not RDatabase database)
            throw new RenderHelperException("MQ6006", $"`ledger_ddl` takes a database, not a {TemplateValues.TypeName(value)}.");
        context.Recorder.RecordObject(database);
        context.Recorder.Record("s:migrations");
        return database.LedgerSchema is { } schema ? LedgerContract.Ddl(ParseDialect(database.Dialect), schema) : "";
    }

    /// <summary>Parses a dialect name or fails the render with MQ6006.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The dialect.</returns>
    public static Dialect ParseDialect(string? name) =>
        SqlDialects.TryParse(name, out var dialect)
            ? dialect
            : throw new RenderHelperException("MQ6006", $"Unknown SQL dialect '{name}' (postgresql, sqlserver, mysql, sqlite or oracle).");

    private static string IndentPrefix(object? value) => value switch
    {
        int n when n is >= 0 and <= 1024 => new string(' ', n),
        long n when n is >= 0 and <= 1024 => new string(' ', (int)n),
        string s => s,
        _ => throw new RenderHelperException("MQ6006", "`indent` takes a number of spaces (0 to 1024) or a prefix string."),
    };

    /// <summary>Prefixes every non-empty line with <paramref name="prefix"/>; empty lines stay empty.</summary>
    /// <param name="text">The text.</param>
    /// <param name="prefix">The prefix.</param>
    /// <returns>The indented text.</returns>
    public static string Indent(string text, string prefix)
    {
        if (text.Length == 0 || prefix.Length == 0)
            return text;
        var lines = text.Split('\n');
        long size = text.Length;
        foreach (var line in lines)
        {
            if (line.Length > 0)
                size += prefix.Length;
        }

        if (size > TrackingTemplateContext.MaxTextLength)
        {
            throw new RenderHelperException("MQ6007",
                $"`indent` would produce {size.ToString(CultureInfo.InvariantCulture)} characters, more than the {TrackingTemplateContext.MaxTextLength.ToString(CultureInfo.InvariantCulture)} a template may produce.");
        }

        var sb = new StringBuilder((int)size);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');
            if (lines[i].Length > 0)
                sb.Append(prefix);
            sb.Append(lines[i]);
        }

        return sb.ToString();
    }

    /// <summary>Removes the leading whitespace common to every non-blank line; blank lines become empty.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The dedented text.</returns>
    public static string Dedent(string text)
    {
        var lines = text.Split('\n');
        string? common = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var lead = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            if (common is null)
            {
                common = lead;
                continue;
            }

            var n = 0;
            while (n < common.Length && n < lead.Length && common[n] == lead[n])
                n++;
            common = common[..n];
        }

        if (string.IsNullOrEmpty(common))
            return string.Join('\n', lines.Select(l => string.IsNullOrWhiteSpace(l) ? "" : l));
        return string.Join('\n', lines.Select(l => string.IsNullOrWhiteSpace(l) ? "" : l[common.Length..]));
    }

    /// <summary>
    /// Escapes Markdown: a backslash before <c>\ ` * _ { } [ ] &lt; &gt; ( ) ! | ~</c> anywhere, before <c>#</c>, <c>+</c> and
    /// <c>-</c> at the start of a line (after indentation), and before the <c>.</c> or <c>)</c> of a leading number (<c>1\.</c>).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text.</returns>
    public static string EscapeMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        var lineStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                sb.Append(c);
                lineStart = true;
                continue;
            }

            if (lineStart && c is ' ' or '\t')
            {
                sb.Append(c);
                continue;
            }

            if (lineStart && char.IsAsciiDigit(c))
            {
                var j = i;
                while (j < text.Length && char.IsAsciiDigit(text[j]))
                    j++;
                sb.Append(text, i, j - i);
                if (j < text.Length && text[j] is '.' or ')')
                {
                    sb.Append('\\').Append(text[j]);
                    j++;
                }

                i = j - 1;
                lineStart = false;
                continue;
            }

            if (c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>' or '(' or ')' or '!' or '|' or '~'
                || (lineStart && c is '#' or '+' or '-'))
                sb.Append('\\');
            sb.Append(c);
            lineStart = false;
        }

        return sb.ToString();
    }

    /// <summary>Escapes XML text and attribute values: <c>&amp; &lt; &gt; " '</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text.</returns>
    public static string EscapeXml(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    /// <summary>Escapes text for the inside of a JSON string (no surrounding quotes), minimally, as canonical JSON does.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text.</returns>
    public static string EscapeJson(string text) => JsonEncodedText.Encode(text, JsonEncoder).Value;

    /// <summary>The banner line: no timestamp, no version.</summary>
    /// <param name="prefix">The comment prefix.</param>
    /// <param name="pack">The pack name.</param>
    /// <param name="unitId">The unit id.</param>
    /// <returns>The banner.</returns>
    public static string Banner(string prefix, string pack, string unitId) =>
        (prefix.Length == 0 ? "" : prefix + " ") + $"Generated by Maquettiste ({pack}/{unitId}). Do not edit; changes are overwritten.";

    /// <summary>
    /// <c>json &lt;value&gt; [&lt;fields&gt;]</c>: canonical JSON of a plain value; with fields (next-phase-1-design.md section 5.5), a
    /// projection of the value's members, so a resolved object can be written too.
    /// </summary>
    private static string Json(TrackingTemplateContext context, object? value, object? fields)
    {
        var tree = fields is null ? null : FieldTree(fields);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonEncoder, Indented = false, SkipValidation = false }))
        {
            if (tree is null)
                WriteJson(context, writer, value, 0);
            else
                WriteProjected(context, writer, value, tree, "", 0);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>One segment of the <c>json</c> field paths: the member names under it, in ordinal order, and whether a path ends here.</summary>
    private sealed class FieldNode
    {
        public bool Leaf { get; set; }

        public SortedDictionary<string, FieldNode> Children { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The field paths of <c>json</c> as a tree: one path (<c>"columns.name"</c>) or a list of them; paths that share a prefix merge.
    /// </summary>
    private static FieldNode FieldTree(object fields)
    {
        var raw = TrackingTemplateContext.Unwrap(fields);
        IEnumerable<object?> paths = raw switch
        {
            string text => [text],
            IDictionary or IScriptObject and not ScriptArray or IResolvedObject => throw new RenderHelperException("MQ6006",
                $"`json` takes its fields as a list of member paths (for example [\"name\", \"columns.name\"]), not a {TemplateValues.TypeName(fields)}."),
            IEnumerable items => items.Cast<object?>(),
            _ => throw new RenderHelperException("MQ6006",
                $"`json` takes its fields as a list of member paths (for example [\"name\", \"columns.name\"]), not a {TemplateValues.TypeName(fields)}."),
        };
        var root = new FieldNode();
        foreach (var item in paths)
        {
            var path = AsText(TrackingTemplateContext.Unwrap(item));
            var segments = path.Split('.');
            if (segments.Any(s => s.Length == 0))
                throw new RenderHelperException("MQ6006", $"`json`: '{path}' is not a member path; write snake_case member names joined by dots, such as \"columns.name\".");
            var node = root;
            foreach (var segment in segments)
            {
                if (!node.Children.TryGetValue(segment, out var child))
                    node.Children[segment] = child = new FieldNode();
                node = child;
            }

            node.Leaf = true;
        }

        if (root.Children.Count == 0)
            throw new RenderHelperException("MQ6006", "`json` needs at least one field; leave the fields out to write a plain value whole.");
        return root;
    }

    /// <summary>
    /// Writes a value through the field tree: a list applies the tree per item; an object (a resolved object, a view or a map)
    /// becomes a JSON object of the named members, keys ordinal; where a path ends, the member's value is written as <c>json</c>
    /// writes a plain value. A path ending on a resolved object, or naming a member a resolved object does not have, is MQ6006; a
    /// map's missing key is null.
    /// </summary>
    private static void WriteProjected(TrackingTemplateContext context, Utf8JsonWriter writer, object? value, FieldNode node, string path, int depth)
    {
        if (depth > 64)
            throw new RenderHelperException("MQ6006", "`json`: the value is nested too deeply.");
        var raw = TrackingTemplateContext.Unwrap(value);
        if (raw is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (node.Leaf)
        {
            WriteField(context, writer, value, path, depth);
            return;
        }

        var map = IsMap(value);
        if (!map && raw is IEnumerable and not string)
        {
            writer.WriteStartArray();
            foreach (var item in (IEnumerable)value!)
                WriteProjected(context, writer, item, node, path, depth + 1);
            writer.WriteEndArray();
            return;
        }

        if (!map && !IsObject(raw))
        {
            var first = path + (path.Length == 0 ? "" : ".") + node.Children.Keys.First();
            throw new RenderHelperException("MQ6006", $"`json`: the field '{first}' reads a member of {Described(value)}, which has none.");
        }

        if (map && value is not MapView and not IScriptObject)
            value = context.Wrap(raw);
        var accessor = context.GetMemberAccessor(value!);
        writer.WriteStartObject();
        foreach (var (segment, child) in node.Children)
        {
            var at = path.Length == 0 ? segment : path + "." + segment;
            if (!accessor.TryGetValue(context, context.CurrentSpan, value!, segment, out var member))
            {
                if (!map)
                    throw new RenderHelperException("MQ6006", $"`json`: {Described(value)} has no member '{segment}' (field '{at}').");
                member = null;
            }

            writer.WritePropertyName(segment);
            WriteProjected(context, writer, member, child, at, depth + 1);
        }

        writer.WriteEndObject();
    }

    /// <summary>The value where a field path ends: a plain value, or a list of them; a resolved object there is MQ6006.</summary>
    private static void WriteField(TrackingTemplateContext context, Utf8JsonWriter writer, object? value, string path, int depth)
    {
        var raw = TrackingTemplateContext.Unwrap(value);
        if (raw is not null && IsObject(raw))
        {
            throw new RenderHelperException("MQ6006",
                $"`json`: the field '{path}' ends on {Described(value)}; name its members instead (for example '{path}.name').");
        }

        if (raw is IEnumerable and not string && !IsMap(value))
        {
            writer.WriteStartArray();
            foreach (var item in (IEnumerable)value!)
                WriteField(context, writer, item, path, depth + 1);
            writer.WriteEndArray();
            return;
        }

        WriteJson(context, writer, value, depth);
    }

    /// <summary>A value's type name with its article, for messages: "an attribute", "a table".</summary>
    private static string Described(object? value)
    {
        var name = TemplateValues.TypeName(value);
        return Validation.BuiltinRules.Article(name) + " " + name;
    }

    /// <summary>Whether a value is an object with members: a resolved object or another template-visible view.</summary>
    private static bool IsObject(object raw) => raw is IResolvedObject || TemplateMemberCatalog.IsViewType(raw.GetType());

    /// <summary>Whether a template value is a string-keyed map (a map view, a Scriban object or a dictionary).</summary>
    private static bool IsMap(object? value) =>
        value is MapView || (value is IScriptObject && value is not ScriptArray) || value is IDictionary;

    private static void WriteJson(TrackingTemplateContext context, Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > 64)
            throw new RenderHelperException("MQ6006", "`json`: the value is nested too deeply (or contains a cycle).");
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case char c:
                writer.WriteStringValue(c.ToString());
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong ul:
                writer.WriteNumberValue(ul);
                return;
            case decimal m:
                writer.WriteNumberValue(m);
                return;
            case double d when double.IsFinite(d):
                writer.WriteNumberValue(d);
                return;
            case float f when float.IsFinite(f):
                writer.WriteNumberValue(f);
                return;
            case Enum e:
                writer.WriteStringValue(context.Unit.Run.Catalog.EnumName(e));
                return;
            case JsonElement element:
                WriteJson(context, writer, TemplateValues.Plain(element), depth + 1);
                return;
            case DateTime or DateTimeOffset or DateOnly or TimeOnly:
                writer.WriteStringValue(SqlDialects.Literal(value, Dialect.PostgreSql).Trim('\''));
                return;
            case IResolvedObject r:
                context.Recorder.RecordObject(r);
                throw new RenderHelperException("MQ6006", $"`json` cannot serialize a {r.Kind}; pass plain values (for example `element.properties`).");
            case IScriptCustomFunction:
                throw new RenderHelperException("MQ6006", "`json` cannot serialize a function.");
        }

        var unwrapped = TrackingTemplateContext.Unwrap(value);
        if (value is MapView map)
        {
            WriteObject(context, writer, map.RawEntries(), depth);
            return;
        }

        if (unwrapped is IScriptObject scriptObject && unwrapped is not ScriptArray)
        {
            var entries = scriptObject.GetMembers().Order(StringComparer.Ordinal)
                .Select(k => KeyValuePair.Create(k, scriptObject.TryGetValue(context, context.CurrentSpan, k, out var v) ? v : null));
            WriteObject(context, writer, entries, depth);
            return;
        }

        if (context.Unit.Run.Catalog.TryReadMap(unwrapped!, out var sorted))
        {
            WriteObject(context, writer, sorted, depth);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            writer.WriteStartArray();
            foreach (var item in enumerable)
                WriteJson(context, writer, item is TrackedList or ValueList or MapView ? item : item, depth + 1);
            writer.WriteEndArray();
            return;
        }

        throw new RenderHelperException("MQ6006", $"`json` cannot serialize a value of type {TemplateValues.TypeName(value)}.");
    }

    private static void WriteObject(TrackingTemplateContext context, Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> entries, int depth)
    {
        writer.WriteStartObject();
        foreach (var (key, item) in entries)
        {
            writer.WritePropertyName(key);
            WriteJson(context, writer, item, depth + 1);
        }

        writer.WriteEndObject();
    }

    private static bool HasStereotype(TrackingTemplateContext context, object? value, string key)
    {
        switch (value)
        {
            case RAnnotated element:
                context.Recorder.RecordObject(element);
                return element.HasStereotype(key);
            case RProcessNode node:
                context.Recorder.RecordObject(node);
                return node.HasStereotype(key);
            case IResolvedObject other:
                context.Recorder.RecordObject(other);
                return false;
            case null:
                return false;
            default:
                throw new RenderHelperException("MQ6006", "`has_stereotype` takes a model element.");
        }
    }

    private static bool HasTag(TrackingTemplateContext context, object? value, string key)
    {
        switch (value)
        {
            case RAnnotated element:
                context.Recorder.RecordObject(element);
                return element.HasTag(key);
            case IResolvedObject other:
                context.Recorder.RecordObject(other);
                return false;
            case null:
                return false;
            default:
                throw new RenderHelperException("MQ6006", "`has_tag` takes a model element.");
        }
    }

    /// <summary>
    /// Whether an element's category is the named category (by id or name) or one of its descendants. Records the element and,
    /// when ancestry is needed, the category tree file.
    /// </summary>
    private static bool InCategory(TrackingTemplateContext context, object? value, string category)
    {
        if (value is null)
            return false;
        if (value is not RAnnotated element)
        {
            if (value is IResolvedObject other)
            {
                context.Recorder.RecordObject(other);
                return false;
            }

            throw new RenderHelperException("MQ6006", "`in_category` takes a model element.");
        }

        context.Recorder.RecordObject(element);
        if (element.Category is not { } own)
            return false;
        if (string.Equals(own.Id, category, StringComparison.Ordinal) || string.Equals(own.Name, category, StringComparison.Ordinal))
            return true;
        // The category's tree: the global one or a domain's (explorer-redesign.md section 1.11); a category id is unique model-wide.
        var tree = context.Unit.Run.Context.Model.Source?.CategoryTrees.FirstOrDefault(t => t.Categories.Any(c => string.Equals(c.Id, own.Id, StringComparison.Ordinal)));
        if (tree is null)
            return false;
        context.Recorder.Record("e:" + tree.Id);
        var byId = tree.Categories.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = own.Id; id is not null && seen.Add(id) && byId.TryGetValue(id, out var node); id = node.Parent)
        {
            if (string.Equals(node.Id, category, StringComparison.Ordinal) || string.Equals(node.Name, category, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>array_index_of &lt;list&gt; [&lt;member path&gt;] &lt;value&gt;</c> (next-phase-1-design.md section 5.5): the first index whose
    /// item, or the item's member at the dotted snake_case path, equals the value, else -1. Values compare as <c>array.sort</c>
    /// orders them (numbers by value, strings ordinally); a resolved object, list or map equals only itself.
    /// </summary>
    private static int ArrayIndexOf(TrackingTemplateContext context, IReadOnlyList<object?> args)
    {
        var (list, member, value) = args.Count == 3 ? (args[0], MemberPath(args[1], "array_index_of"), args[2]) : (args[0], null, args[1]);
        var index = 0;
        foreach (var item in ListItems(list, "array_index_of"))
        {
            if (ValuesEqual(member is null ? item : MemberValue(context, item, member), value))
                return index;
            index++;
        }

        return -1;
    }

    /// <summary><c>array_find &lt;list&gt; &lt;member path&gt; &lt;value&gt;</c>: the first item whose member equals the value, else null.</summary>
    private static object? ArrayFind(TrackingTemplateContext context, object? list, object? path, object? value)
    {
        var member = MemberPath(path, "array_find");
        foreach (var item in ListItems(list, "array_find"))
        {
            if (ValuesEqual(MemberValue(context, item, member), value))
                return item;
        }

        return null;
    }

    private static string MemberPath(object? value, string helper)
    {
        var path = AsText(TrackingTemplateContext.Unwrap(value));
        if (path.Length == 0 || path.Split('.').Any(s => s.Length == 0))
            throw new RenderHelperException("MQ6006", $"`{helper}` takes a member path such as \"name\" or \"table.name\", not '{path}'.");
        return path;
    }

    /// <summary>The items of a list argument as templates see them (null is empty); a map or any other value fails with MQ6006.</summary>
    private static IEnumerable<object?> ListItems(object? list, string helper)
    {
        if (list is null)
            return [];
        if (list is string || IsMap(list) || list is not IEnumerable items)
            throw new RenderHelperException("MQ6006", $"`{helper}` expects a list, not a {TemplateValues.TypeName(list)}.");
        return items.Cast<object?>();
    }

    /// <summary>
    /// Whether two template values are equal: the same object, or two scalars <see cref="OrdinalValueComparer"/> orders as equal.
    /// Resolved objects, lists and maps equal only themselves.
    /// </summary>
    private static bool ValuesEqual(object? x, object? y)
    {
        x = TrackingTemplateContext.Unwrap(x);
        y = TrackingTemplateContext.Unwrap(y);
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null || !IsScalar(x) || !IsScalar(y))
            return false;
        return OrdinalValueComparer.Instance.Compare(x, y) == 0;
    }

    private static bool IsScalar(object value) =>
        value is string or bool or char or Enum or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan
        || value.GetType().IsPrimitive || value is decimal;

    /// <summary>
    /// <c>toposort &lt;items&gt; &lt;edges&gt;</c> (next-phase-1-design.md section 5.5): the items in an order where, for each
    /// <c>[before, after]</c> pair of <paramref name="edgesValue"/>, the item <c>before</c> names comes first. Ends match items by
    /// identity (resolved objects) or by value as <c>array_index_of</c> compares; ends that match no item are ignored. A stable Kahn
    /// sort: of the items free to go, the earliest in input order goes next, and when a cycle leaves none free, the earliest item left
    /// is released.
    /// </summary>
    private static ScriptArray Toposort(object? itemsValue, object? edgesValue)
    {
        var items = ListItems(itemsValue, "toposort").ToList();
        var byKey = new Dictionary<object, List<int>>(IdentityOrText.Instance);
        for (var i = 0; i < items.Count; i++)
        {
            var key = MatchKey(items[i]);
            if (!byKey.TryGetValue(key, out var indexes))
                byKey[key] = indexes = [];
            indexes.Add(i);
        }

        var successors = new List<int>?[items.Count];
        var waiting = new int[items.Count];
        foreach (var edge in ListItems(edgesValue, "toposort"))
        {
            var pair = TrackingTemplateContext.Unwrap(edge) is IEnumerable and not string && !IsMap(edge)
                ? ((IEnumerable)edge!).Cast<object?>().ToList()
                : null;
            if (pair is not { Count: 2 })
                throw new RenderHelperException("MQ6006", $"`toposort` takes its edges as [before, after] pairs, not a {TemplateValues.TypeName(edge)}.");
            if (!byKey.TryGetValue(MatchKey(pair[0]), out var before) || !byKey.TryGetValue(MatchKey(pair[1]), out var after))
                continue;
            foreach (var b in before)
            {
                foreach (var a in after)
                {
                    if (a == b)
                        continue;
                    (successors[b] ??= []).Add(a);
                    waiting[a]++;
                }
            }
        }

        var ready = new SortedSet<int>();
        var left = new SortedSet<int>(Enumerable.Range(0, items.Count));
        for (var i = 0; i < items.Count; i++)
        {
            if (waiting[i] == 0)
                ready.Add(i);
        }

        var result = new ScriptArray(items.Count);
        while (left.Count > 0)
        {
            var next = ready.Count > 0 ? ready.Min : left.Min;
            ready.Remove(next);
            left.Remove(next);
            result.Add(items[next]);
            foreach (var successor in successors[next] ?? [])
            {
                if (left.Contains(successor) && --waiting[successor] == 0)
                    ready.Add(successor);
            }
        }

        return result;
    }

    /// <summary>
    /// The key <c>toposort</c> matches a value by: a scalar's canonical text (numbers by value, so <c>1</c> and <c>1.0</c> match), any
    /// other object itself.
    /// </summary>
    private static object MatchKey(object? value)
    {
        var raw = TrackingTemplateContext.Unwrap(value);
        return raw switch
        {
            null => "z:",
            string s => "s:" + s,
            bool b => b ? "b:true" : "b:false",
            _ when OrdinalValueComparer.IsNumber(raw) => "n:" + Convert.ToDecimal(raw, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture),
            _ when IsScalar(raw) => "o:" + raw.GetType().Name + ":" + AsText(raw),
            _ => raw,
        };
    }

    /// <summary>Equality of <see cref="MatchKey"/> keys: strings ordinally, anything else by reference.</summary>
    private sealed class IdentityOrText : IEqualityComparer<object>
    {
        public static readonly IdentityOrText Instance = new();

        public new bool Equals(object? x, object? y) =>
            x is string a && y is string b ? string.Equals(a, b, StringComparison.Ordinal) : ReferenceEquals(x, y);

        public int GetHashCode(object obj) =>
            obj is string s ? StringComparer.Ordinal.GetHashCode(s) : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// <c>table_named "&lt;schema&gt;.&lt;name&gt;" [&lt;database&gt;]</c> (next-phase-1-design.md section 5.5): the resolved table of
    /// that name, or null. Without a schema part it matches a table with no schema or in its database's default schema; without a
    /// database it searches every database, and a name two tables share fails the unit (MQ6006). The database is a database or its
    /// name. It records <c>n:table:&lt;text&gt;</c>, whose hash covers every table the name matches, so a table renamed into or out
    /// of the name renders the unit again, and on a hit the table.
    /// </summary>
    private static RTable? TableNamed(TrackingTemplateContext context, object? nameValue, object? databaseValue)
    {
        var text = AsText(TrackingTemplateContext.Unwrap(nameValue));
        if (!TryParseTableName(text, out var schema, out var name, out var problem))
            throw new RenderHelperException("MQ6006", "`table_named`: " + problem);
        context.Recorder.Record("n:table:" + text);
        var model = context.Unit.Run.Context.Model;
        IReadOnlyList<RDatabase> databases;
        switch (TrackingTemplateContext.Unwrap(databaseValue))
        {
            case null:
                databases = model.Databases;
                break;
            case RDatabase database:
                context.Recorder.RecordObject(database);
                databases = [database];
                break;
            case string databaseName:
                // A name depends on every database's name: renaming one into it must render the unit again.
                context.Recorder.Record("k:database");
                foreach (var each in model.Databases)
                    context.Recorder.RecordObject(each);
                databases = [.. model.Databases.Where(d => string.Equals(d.Name, databaseName, StringComparison.Ordinal))];
                break;
            default:
                throw new RenderHelperException("MQ6006", $"`table_named` takes a database (or its name) after the table's name, not a {TemplateValues.TypeName(databaseValue)}.");
        }

        var found = TablesNamed(databases, schema, name);
        if (found.Count > 1)
        {
            throw new RenderHelperException("MQ6006",
                $"`table_named`: '{text}' names {found.Count.ToString(CultureInfo.InvariantCulture)} tables: "
                + string.Join(", ", found.Select(t => t.Database.Name + "." + (t.Schema is { } s ? s + "." : "") + t.Name))
                + ". Pass the database as the second argument.");
        }

        if (found.Count == 0)
            return null;
        context.Recorder.RecordObject(found[0]);
        return found[0];
    }

    /// <summary>Splits a <c>table_named</c> text at its first dot into the schema and the name; without a dot the schema is null.</summary>
    /// <param name="text">The text.</param>
    /// <param name="schema">The schema part, or <see langword="null"/>.</param>
    /// <param name="name">The table name.</param>
    /// <param name="problem">Why the text is not a table's name.</param>
    /// <returns><see langword="true"/> when the text is a table's name.</returns>
    internal static bool TryParseTableName(string text, out string? schema, out string name, out string problem)
    {
        schema = null;
        name = text;
        problem = "";
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            schema = text[..dot];
            name = text[(dot + 1)..];
        }

        if (name.Length > 0 && schema is not { Length: 0 })
            return true;
        problem = $"'{text}' is not a table's name; write \"<name>\" or \"<schema>.<name>\", such as \"invoices\" or \"billing.invoices\".";
        return false;
    }

    /// <summary>
    /// The tables of <paramref name="databases"/> named <paramref name="name"/> (ordinal), in database then table order: with a schema,
    /// those in it (a table without one is in its database's default schema); without, those with no schema or in the default schema.
    /// </summary>
    /// <param name="databases">The databases.</param>
    /// <param name="schema">The schema, or <see langword="null"/>.</param>
    /// <param name="name">The table name.</param>
    /// <returns>The tables.</returns>
    internal static List<RTable> TablesNamed(IEnumerable<RDatabase> databases, string? schema, string name)
    {
        var found = new List<RTable>();
        foreach (var database in databases)
        {
            foreach (var table in database.Tables)
            {
                if (!string.Equals(table.Name, name, StringComparison.Ordinal))
                    continue;
                var matches = schema is null
                    ? table.Schema is null || string.Equals(table.Schema, database.DefaultSchema, StringComparison.Ordinal)
                    : string.Equals(table.Schema ?? database.DefaultSchema, schema, StringComparison.Ordinal);
                if (matches)
                    found.Add(table);
            }
        }

        return found;
    }

    /// <summary>
    /// <c>column_named &lt;table&gt; "&lt;name&gt;"</c> (next-phase-1-design.md section 5.5): the table's column of that name (ordinal),
    /// or null; a null table gives null. Records the table and its columns' membership, and on a hit the column.
    /// </summary>
    private static RColumn? ColumnNamed(TrackingTemplateContext context, object? tableValue, object? nameValue)
    {
        var raw = TrackingTemplateContext.Unwrap(tableValue);
        if (raw is null)
            return null;
        if (raw is not RTable table)
            throw new RenderHelperException("MQ6006", $"`column_named` takes a table, not a {TemplateValues.TypeName(tableValue)}.");
        var name = AsText(TrackingTemplateContext.Unwrap(nameValue));
        if (name.Length == 0)
            throw new RenderHelperException("MQ6006", "`column_named` needs a column's name.");
        context.Recorder.RecordObject(table);
        context.Recorder.RecordAll(table.Columns.MembershipKeys);
        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        if (column is not null)
            context.Recorder.RecordObject(column);
        return column;
    }

    private static object? Lookup(TrackingTemplateContext context, string id)
    {
        var found = id.Length == 0 ? null : context.Unit.Run.Context.Model.Find(id);
        if (found is null)
        {
            if (id.Length > 0)
                context.Recorder.Record("e:" + id);
            return null;
        }

        context.Recorder.RecordObject(found);
        return found;
    }

    /// <summary><c>state_path &lt;state&gt;</c>: a process state's dotted path (<c>Fulfilment.Shipping.Packed</c>); a state id works too.</summary>
    private static string? StatePath(TrackingTemplateContext context, object? value)
    {
        var unwrapped = TrackingTemplateContext.Unwrap(value);
        var state = unwrapped switch
        {
            null => null,
            RState s => s,
            string id => Lookup(context, id) as RState ?? throw new RenderHelperException("MQ6006", $"`state_path`: '{id}' is not the id of a process state."),
            _ => throw new RenderHelperException("MQ6006", $"`state_path` takes a process state (or its id), not a {TemplateValues.TypeName(unwrapped)}."),
        };
        if (state is null)
            return null;
        context.Recorder.RecordObject(state);
        return state.Path;
    }

    /// <summary>
    /// <c>iso_duration_ms &lt;text&gt;</c>: the milliseconds of an ISO 8601 duration with the interpreter's fixed spans (a month is 30
    /// days, a year 365); a text that is not a duration fails the unit (MQ6006).
    /// </summary>
    private static long IsoDurationMs(object? value)
    {
        var text = AsText(TrackingTemplateContext.Unwrap(value));
        return Resolution.ProcessText.Milliseconds(text)
            ?? throw new RenderHelperException("MQ6006", $"`iso_duration_ms`: '{text}' is not an ISO 8601 duration (for example P5D or PT1H30M).");
    }

    /// <summary><c>row &lt;reference type&gt; "&lt;code&gt;"</c>: the type's row with that code, or null.</summary>
    private static RRow? Row(TrackingTemplateContext context, object? type, object? code)
    {
        var unwrapped = TrackingTemplateContext.Unwrap(type);
        var referenceType = unwrapped switch
        {
            RReferenceType t => t,
            string id => Lookup(context, id) as RReferenceType,
            _ => throw new RenderHelperException("MQ6006", $"`row` takes a reference type (or its id), not a {TemplateValues.TypeName(unwrapped)}."),
        };
        if (referenceType is null)
            return null;
        context.Recorder.RecordObject(referenceType);
        context.Recorder.RecordAll(referenceType.Rows.MembershipKeys);
        var row = referenceType.RowOf(TrackingTemplateContext.Unwrap(code));
        if (row is not null)
            context.Recorder.RecordObject(row);
        return row;
    }

    /// <summary>
    /// <c>seed_named "&lt;form&gt;"</c> (next-phase-1-design.md section 5.3): the seed a batch's form names (<c>currencies</c>,
    /// <c>Billing.currencies</c>, the path of <c>@seed:</c>), found in the snapshot's name index, or null. Records
    /// <c>n:seed:&lt;canonical form&gt;</c>, so a seed renamed into or out of the name renders the unit again, and on a hit the seed. An
    /// empty or malformed form, and one that two seeds share, fail the unit (MQ6006).
    /// </summary>
    private static RSeed? SeedNamed(TrackingTemplateContext context, object? value)
    {
        var text = AsText(TrackingTemplateContext.Unwrap(value));
        if (text.Length == 0)
            throw new RenderHelperException("MQ6006", "`seed_named` needs a seed's name, such as \"currencies\" or \"Billing.currencies\".");
        if (text[0] == '@')
            throw new RenderHelperException("MQ6006", $"`seed_named` takes the seed's path without the @seed: prefix, not '{text}'.");
        if (!QualifiedName.TryParse("@seed:" + text, out var name, out var error))
            throw new RenderHelperException("MQ6006", $"`seed_named`: '{text}' is not a seed's name: {error}.");
        context.Recorder.Record("n:seed:" + name.Path);
        var model = context.Unit.Run.Context.Model;
        var found = model.Source?.Names("seed").Find(name.Path) ?? [];
        if (found.Count > 1)
        {
            throw new RenderHelperException("MQ6006",
                $"`seed_named`: '{text}' names {found.Count.ToString(CultureInfo.InvariantCulture)} seeds: "
                + string.Join(", ", found.Select(e => e.Longest + " (" + e.Id + ")")) + ". Use a longer form, or rename one.");
        }

        if (found.Count == 0 || model.Find(found[0].Id) is not RSeed seed)
            return null;
        context.Recorder.RecordObject(seed);
        return seed;
    }

    /// <summary>
    /// <c>seed_row &lt;seed&gt; &lt;row key&gt;</c> (next-phase-1-design.md section 5.3): the seed's row with that key, or null. The seed is
    /// an <see cref="RSeed"/> or a form <c>seed_named</c> takes; the key is the code (a reference type's seed), the key columns' value
    /// (a table seed: the unique constraint the seed names, else the primary key) or the key attributes' value (an entity's seed), a
    /// list in key order when the key has several parts. A relation's seed has no key (MQ6006). Records what <c>row</c> records: the
    /// seed, its rows' membership and the row, and the target whose key it reads.
    /// </summary>
    private static RSeedRow? SeedRow(TrackingTemplateContext context, object? seedValue, object? key)
    {
        var unwrapped = TrackingTemplateContext.Unwrap(seedValue);
        var seed = unwrapped switch
        {
            null => null,
            RSeed s => s,
            string form => SeedNamed(context, form),
            _ => throw new RenderHelperException("MQ6006", $"`seed_row` takes a seed (or a name seed_named takes), not a {TemplateValues.TypeName(unwrapped)}."),
        };
        if (seed is null)
            return null;
        context.Recorder.RecordObject(seed);
        context.Recorder.RecordAll(seed.Rows.MembershipKeys);
        if (seed.Target is null)
            return null; // a target that does not resolve (MQ2001): no row has a key
        if (seed.Target is REntity or RTable)
            context.Recorder.RecordObject(seed.Target);
        if (seed.KeyCells is not { } cells)
        {
            throw new RenderHelperException("MQ6006", seed.Target is RRelation
                ? $"`seed_row`: the rows of relation seed '{seed.QualifiedName}' have no key; loop over its rows instead."
                : $"`seed_row`: seed '{seed.QualifiedName}' has no row key: its target has no primary key or key attributes.");
        }

        var parts = KeyParts(TrackingTemplateContext.Unwrap(key));
        if (parts.Count != cells.Count)
        {
            throw new RenderHelperException("MQ6006",
                $"`seed_row`: seed '{seed.QualifiedName}' finds a row by {cells.Count.ToString(CultureInfo.InvariantCulture)} value(s) ({string.Join(", ", cells)}), "
                + $"not {parts.Count.ToString(CultureInfo.InvariantCulture)}; give a key of several parts as a list in key order.");
        }

        var row = seed.RowOf(parts);
        if (row is not null)
            context.Recorder.RecordObject(row);
        return row;
    }

    /// <summary>The parts of a row key: a list's items, else the value itself.</summary>
    private static List<object?> KeyParts(object? key)
    {
        switch (key)
        {
            case null or string:
                return [key];
            case IResolvedObject or ITemplateView or ScriptObject or IDictionary or IReadOnlyDictionary<string, object?>:
                throw new RenderHelperException("MQ6006", $"`seed_row` takes a row key (a value, or a list of values in key order), not a {TemplateValues.TypeName(key)}.");
            case IEnumerable items:
                var parts = new List<object?>();
                foreach (var item in items)
                    parts.Add(TrackingTemplateContext.Unwrap(item));
                return parts;
            default:
                return [key];
        }
    }

    /// <summary><c>row_uuid &lt;row&gt;</c>: a row's 128-bit ULID written as a UUID (big-endian, lowercase, dashed).</summary>
    private static string RowUuid(TrackingTemplateContext context, object? row)
    {
        var unwrapped = TrackingTemplateContext.Unwrap(row);
        string id;
        switch (unwrapped)
        {
            case RRow r:
                context.Recorder.RecordObject(r);
                id = r.Id;
                break;
            case RSeedRow r:
                context.Recorder.RecordObject(r);
                id = r.Id;
                break;
            case string text:
                id = text;
                break;
            default:
                throw new RenderHelperException("MQ6006", $"`row_uuid` takes a row, not a {TemplateValues.TypeName(unwrapped)}.");
        }

        if (!Ulid.TryParse(id, out var ulid))
            throw new RenderHelperException("MQ6006", $"`row_uuid`: '{id}' is not a ULID.");
        var hex = Convert.ToHexStringLower(ulid.ToByteArray());
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..];
    }

    /// <summary>
    /// <c>stable_id &lt;table&gt; &lt;key&gt;</c> (next-phase-2-design.md section 3.4): the stable id of a row key in a table, as the
    /// resolver gives a stable id column of the table's rows (<see cref="StableIds.Of"/> with the table id as namespace), for packs that
    /// emit constants for seeded rows. The key is a value, or a list of values in key order, in database terms (a uuid in lowercase
    /// with hyphens). Records <c>e:&lt;table id&gt;</c>. A table without a stable id column, or a key with a null part, is MQ6006.
    /// </summary>
    private static string StableId(TrackingTemplateContext context, object? table, object? key)
    {
        if (TrackingTemplateContext.Unwrap(table) is not RTable t)
            throw new RenderHelperException("MQ6006", $"`stable_id` takes a table, not a {TemplateValues.TypeName(TrackingTemplateContext.Unwrap(table))}.");
        context.Recorder.Record("e:" + t.Id);
        if (!t.Columns.Any(c => c.StableId) || !StableIds.IsNamespace(t.Id))
            throw new RenderHelperException("MQ6006", $"`stable_id`: table '{t.Name}' has no stable id column; set stableId on a uuid column of a table file.");
        var unwrapped = TrackingTemplateContext.Unwrap(key);
        if (unwrapped is IResolvedObject or ITemplateView or ScriptObject or IDictionary)
            throw new RenderHelperException("MQ6006", $"`stable_id` takes a row key (a value, or a list of values in key order), not a {TemplateValues.TypeName(unwrapped)}.");
        var parts = KeyParts(unwrapped);
        if (parts.Count == 0 || parts.Any(p => p is null))
            throw new RenderHelperException("MQ6006", $"`stable_id`: the key for table '{t.Name}' has no value in some part; a stable id needs every part of its key.");
        try
        {
            return StableIds.Of(t.Id, parts);
        }
        catch (ArgumentException ex)
        {
            throw new RenderHelperException("MQ6006", "`stable_id`: " + ex.Message);
        }
    }

    private static string File(TrackingTemplateContext context, object? path, object? content)
    {
        var relative = AsText(path).Trim();
        if (relative.Length == 0)
            throw new RenderHelperException("MQ6006", "`file` needs a non-empty path.");
        var text = content switch
        {
            null => "",
            string s => s,
            _ => context.ObjectToString(content) ?? "",
        };
        context.Blocks.Add(new RenderedFile(context.Unit.OutputPath(relative), Renderer.NormalizeLineEndings(text), FileRole.Block));
        return "";
    }

    /// <summary>Orders template values: null first, numbers numerically, strings ordinally, then by type name and ordinal text.</summary>
    private sealed class OrdinalValueComparer : IComparer<object?>
    {
        public static readonly OrdinalValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null || y is null)
                return x is null ? (y is null ? 0 : -1) : 1;
            if (IsNumber(x) && IsNumber(y))
                return Convert.ToDecimal(x, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(y, CultureInfo.InvariantCulture));
            if (x is string sx && y is string sy)
                return string.CompareOrdinal(sx, sy);
            if (x is bool bx && y is bool by)
                return bx.CompareTo(by);
            var byType = string.CompareOrdinal(x.GetType().Name, y.GetType().Name);
            return byType != 0 ? byType : string.CompareOrdinal(AsText(x), AsText(y));
        }

        public static bool IsNumber(object value) =>
            value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal
            || (value is double d && double.IsFinite(d) && Math.Abs(d) < 7.9e28)
            || (value is float f && float.IsFinite(f) && Math.Abs(f) < 7.9e28f);
    }
}
