using System.Buffers;
using System.Collections;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        "banner", "file", "row", "row_uuid", "display_name", "plural_name", "description_of", "label_of", "translate", "has_translation",
        "state_path", "iso_duration_ms");

    /// <summary>The unit variables (engine-design.md section 8); pack helpers may not use these names either.</summary>
    public static readonly FrozenSet<string> Variables = FrozenSet.Create(StringComparer.Ordinal,
        "model", "element", "package", "entity", "relation", "enum", "value_object", "table", "reference_type", "seed", "locale", "process", "actor",
        "scenario", "pack", "mapping", "mappings", "schema_diff", "hints", "data", "unit");

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
        Replace(builtins, "regex", "match", new HelperFunction("regex.match", 2, 3, (c, a) =>
        {
            var match = Regex.Match(AsText(a[0]), AsText(a[1]), RegexOptionsOf(a, 2), c.RegexTimeOut);
            return match.Success ? new ScriptArray(match.Groups.Cast<Group>().Select(g => (object?)g.Value)) : [];
        }));
        Replace(builtins, "regex", "matches", new HelperFunction("regex.matches", 2, 3, (c, a) =>
            new ScriptArray(Regex.Matches(AsText(a[0]), AsText(a[1]), RegexOptionsOf(a, 2), c.RegexTimeOut)
                .Where(m => m.Success)
                .Select(m => (object?)new ScriptArray(m.Groups.Cast<Group>().Select(g => (object?)g.Value))))));
        Replace(builtins, "regex", "replace", new HelperFunction("regex.replace", 3, 4, (c, a) =>
            Regex.Replace(AsText(a[0]), AsText(a[1]), AsText(a[2]), RegexOptionsOf(a, 3), c.RegexTimeOut)));
        Replace(builtins, "regex", "split", new HelperFunction("regex.split", 2, 3, (c, a) =>
            new ScriptArray(Regex.Split(AsText(a[0]), AsText(a[1]), RegexOptionsOf(a, 2), c.RegexTimeOut).Select(p => (object?)p))));

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
        Add(builtins, "indent", 2, 2, (_, a) => Indent(AsText(a[0]), IndentPrefix(a[1])));
        Add(builtins, "dedent", 1, 1, (_, a) => Dedent(AsText(a[0])));
        Add(builtins, "escape_md", 1, 1, (_, a) => EscapeMarkdown(AsText(a[0])));
        Add(builtins, "escape_xml", 1, 1, (_, a) => EscapeXml(AsText(a[0])));
        Add(builtins, "escape_json", 1, 1, (_, a) => EscapeJson(AsText(a[0])));
        Add(builtins, "json", 1, 1, (c, a) => Json(c, a[0]));
        Add(builtins, "has_stereotype", 2, 2, (c, a) => HasStereotype(c, a[0], AsText(a[1])));
        Add(builtins, "has_tag", 2, 2, (c, a) => HasTag(c, a[0], AsText(a[1])));
        Add(builtins, "in_category", 2, 2, (c, a) => InCategory(c, a[0], AsText(a[1])));
        Add(builtins, "lookup", 1, 1, (c, a) => Lookup(c, AsText(a[0])));
        Add(builtins, "row", 2, 2, (c, a) => Row(c, a[0], a[1]));
        Add(builtins, "row_uuid", 1, 1, (c, a) => RowUuid(c, a[0]));
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
            return element.PluralName;
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

    private static string Json(TrackingTemplateContext context, object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonEncoder, Indented = false, SkipValidation = false }))
            WriteJson(context, writer, value, 0);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

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

        private static bool IsNumber(object value) =>
            value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal
            || (value is double d && double.IsFinite(d) && Math.Abs(d) < 7.9e28)
            || (value is float f && float.IsFinite(f) && Math.Abs(f) < 7.9e28f);
    }
}
