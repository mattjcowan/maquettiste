using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acornima;
using Jint;
using JsEngine = Jint.Engine;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Acornima.Ast;
using AstScript = Acornima.Ast.Script;

namespace Maquettiste.Engine.Scripting;

/// <summary>A script prepared once per pool (<c>JsEngine.PrepareScript</c>) and shared by its engines.</summary>
/// <param name="Source">The script file.</param>
/// <param name="Program">The prepared program.</param>
internal sealed record PreparedScript(ScriptSource Source, Prepared<AstScript> Program)
{
    /// <summary>
    /// The text before a rule file's code: each rule file runs in its own function scope (next-phase-1-design.md section 5.7), so
    /// it cannot publish anything for another. It is on the code's first line, so line numbers stay.
    /// </summary>
    internal const string ScopePrefix = "(function () {";

    /// <summary>The text after a rule file's code, on a line of its own.</summary>
    internal const string ScopeSuffix = "\n})();";

    /// <summary>Whether the code runs inside its own function scope (<see cref="ScopePrefix"/>): every rule script does.</summary>
    public bool Scoped { get; init; }

    /// <summary>The number of lines of the script's own code.</summary>
    public int LineCount { get; init; } = 1;

    /// <summary>The length of the code's last line.</summary>
    public int LastLineLength { get; init; }

    /// <summary>MQ5005 at the rule script's first top-level <c>let</c> or <c>var</c>, or <see langword="null"/>.</summary>
    public Diagnostic? Warning { get; init; }

    /// <summary>
    /// Prepares scripts in load order; a syntax error is a <see cref="ScriptErrorException"/> with file, line and column. A rule script
    /// (<see cref="ScriptSandbox.IsRuleScript"/>) is wrapped in its own function scope and checked for top-level <c>let</c> and
    /// <c>var</c> (<see cref="Warning"/>).
    /// </summary>
    /// <param name="scripts">The scripts.</param>
    /// <param name="limits">The limits; they bound the match time of regular expression literals.</param>
    /// <returns>The prepared scripts.</returns>
    public static IReadOnlyList<PreparedScript> PrepareAll(IReadOnlyList<ScriptSource> scripts, SandboxLimits limits)
    {
        // A prepared script ignores ConstraintOptions.RegexTimeout: its literals get the parsing option's timeout, or 10 s.
        var options = new ScriptPreparationOptions
        {
            ParsingOptions = new ScriptParsingOptions { RegexTimeout = ScriptSandbox.RegexTimeout(limits) },
        };
        var prepared = new List<PreparedScript>(scripts.Count);
        foreach (var script in scripts)
        {
            var rule = ScriptSandbox.IsRuleScript(script.Path);
            var (lineCount, lastLineLength) = Measure(script.Code);
            var shape = new PreparedScript(script, default) { Scoped = rule, LineCount = lineCount, LastLineLength = lastLineLength };
            var code = rule ? ScopePrefix + script.Code + ScopeSuffix : script.Code;
            Prepared<AstScript> program;
            try
            {
                program = JsEngine.PrepareScript(code, script.Path, strict: true, options);
            }
            catch (ScriptPreparationException ex)
            {
                var parse = ex.InnerException as ParseErrorException;
                var (line, column) = parse is null ? (null, null) : shape.Map(parse.LineNumber, parse.Column + 1);
                throw new ScriptErrorException(
                    new Diagnostic(rule ? "MQ5002" : "MQ6016", DiagnosticSeverity.Error,
                        $"Script {script.Path} has a syntax error: {parse?.Description ?? ex.Message}",
                        null, script.Path, null, line, column),
                    ex);
            }

            prepared.Add(rule ? shape.Scope(program) : shape with { Program = program });
        }

        return prepared;
    }

    /// <summary>
    /// Maps a position in the prepared text (1-based line and column) to the script's own code: a rule script's first line loses the
    /// <see cref="ScopePrefix"/>, and a position past its last line (in the <see cref="ScopeSuffix"/>) goes to the end of the last line.
    /// </summary>
    /// <param name="line">The line, or <see langword="null"/>.</param>
    /// <param name="column">The 1-based column, or <see langword="null"/>.</param>
    /// <returns>The position in the script's code.</returns>
    public (int? Line, int? Column) Map(int? line, int? column)
    {
        if (!Scoped || line is not { } at)
            return (line, column);
        if (at > LineCount)
            return (LineCount, LastLineLength + 1);
        if (at == 1 && column is { } c)
            return (at, Math.Max(1, c - ScopePrefix.Length));
        return (at, column);
    }

    /// <summary>The number of lines of a text and the length of its last one, with the line terminators of JavaScript.</summary>
    private static (int Lines, int LastLength) Measure(string code)
    {
        var lines = 1;
        var lastStart = 0;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch is not ('\n' or '\r' or '\u2028' or '\u2029'))
                continue;
            if (ch == '\r' && i + 1 < code.Length && code[i + 1] == '\n')
                i++;
            lines++;
            lastStart = i + 1;
        }

        return (lines, code.Length - lastStart);
    }

    /// <summary>
    /// Checks a rule script's prepared program: the code must stay inside its scope (one statement, the call of the function the
    /// prefix opens, whose body closes in the suffix), else a closing brace in the code would let it declare globals for other files.
    /// Then finds its first top-level <c>let</c> or <c>var</c> (MQ5005).
    /// </summary>
    private PreparedScript Scope(Prepared<AstScript> program)
    {
        var body = program.Program!.Body;
        var close = ScopePrefix.Length + Source.Code.Length + 1; // the suffix's closing brace
        if (body.Count != 1 || body[0] is not ExpressionStatement { Expression: CallExpression { Arguments.Count: 0, Callee: FunctionExpression wrapper } }
            || wrapper.Range.Start != 1 || wrapper.Body.Range.End != close + 1)
        {
            var (line, column) = body.Count > 0 && body[0] is ExpressionStatement { Expression: CallExpression { Callee: FunctionExpression f } }
                ? Map(f.Body.Location.End.Line, f.Body.Location.End.Column)
                : (null, null);
            throw new ScriptErrorException(new Diagnostic("MQ5002", DiagnosticSeverity.Error,
                $"Script {Source.Path} closes the function scope its code runs in: a closing brace has no opening one.",
                null, Source.Path, null, line, column));
        }

        var found = FirstMutableBinding(wrapper.Body);
        if (found is null)
            return this with { Program = program };
        var (at, col) = Map(found.Location.Start.Line, found.Location.Start.Column + 1);
        var name = found.Declarations.Count > 0 && found.Declarations[0].Id is Identifier id ? $" '{id.Name}'" : "";
        var keyword = found.Kind == VariableDeclarationKind.Var ? "var" : "let";
        var (file, what) = ScriptSandbox.IsRowsScript(Source.Path) ? ("Rows script", "a rows script") : ("Rule file", "a rule");
        var warning = RuleCatalog.Create("MQ5005",
            $"{file} {Source.Path} declares{name} with {keyword} at its top level: {what} keeps no state between calls, so this file's " +
            "results are never cached. Use const, or declare the variable inside the function that uses it.",
            null, Source.Path) with { Line = at, Column = col };
        return this with { Program = program, Warning = warning };
    }

    /// <summary>
    /// The first <c>let</c> or <c>var</c> declaration in source order outside every function and class body: state that outlives a
    /// call, which freezing cannot reach (a top-level block or loop head included, since a closure can capture it).
    /// </summary>
    private static VariableDeclaration? FirstMutableBinding(Node node)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is VariableDeclaration { Kind: VariableDeclarationKind.Let or VariableDeclarationKind.Var } declaration)
                return declaration;
            if (child is IFunction or ClassBody)
                continue;
            if (FirstMutableBinding(child) is { } found)
                return found;
        }

        return null;
    }
}

/// <summary>
/// One sandboxed Jint engine (engine-design.md section 10). Not thread-safe: one call at a time, on one thread, which is what
/// <c>LimitMemory</c>'s per-thread allocation accounting needs.
/// </summary>
internal sealed partial class ScriptSandbox : IScriptSandbox, IDisposable
{
    /// <summary>Globals removed before any script runs: binary buffers (single allocations the per-statement memory check cannot see),
    /// garbage-collector observers (non-deterministic) and nested realms.</summary>
    private static readonly ImmutableArray<string> RemovedGlobals =
    [
        "ArrayBuffer", "SharedArrayBuffer", "DataView", "Atomics", "TypedArray",
        "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array",
        "BigInt64Array", "BigUint64Array", "Float16Array", "Float32Array", "Float64Array",
        "WeakRef", "FinalizationRegistry", "ShadowRealm",
    ];

    /// <summary>The source name prefix of the sandbox's own setup scripts.</summary>
    private const string InternalSourcePrefix = "maquettiste:";

    private readonly JsEngine _engine;
    private readonly SandboxLimits _limits;
    private readonly CancellationToken _runToken;
    private readonly CallState _state = new();
    private readonly SeededRandom _random = new();
    private readonly ScriptValues _values;
    private readonly Dictionary<(ScriptRegistrationKind Kind, string Name), Registered> _functions = [];
    private readonly Dictionary<string, RuleSpec> _rules = new(StringComparer.Ordinal);
    private readonly List<ScriptRegistration> _registrations = [];
    private readonly Dictionary<string, PreparedScript> _scripts = new(StringComparer.Ordinal);
    private string? _loadingPath;
    private bool _inCall;
    private bool _disposed;
    private IReadOnlyDictionary<string, object?>? _paramsSource;
    private JsValue? _paramsValue;
    private long _paramsCall = -1;
    private ModelSnapshot? _ruleSnapshot;
    private JsValue? _ruleModel;
    private readonly Dictionary<string, ScriptLibrary> _libraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsValue> _libraryObjects = new(StringComparer.Ordinal);

    private ScriptSandbox(SandboxLimits limits, MemberCatalog catalog, CancellationToken runToken, IReadOnlyList<ScriptLibrary> libraries)
    {
        foreach (var library in libraries)
            _libraries.TryAdd(library.Pack, library);
        _limits = limits;
        _runToken = runToken;
        var state = _state;
        _engine = new JsEngine(options =>
        {
            options.Strict();
            options.Interop.Enabled = false;
            options.Interop.AllowGetType = false;
            options.Interop.AllowSystemReflection = false;
            options.Interop.AllowWrite = false;
            options.DisableStringCompilation();
            options.Culture = CultureInfo.InvariantCulture;
            options.TimeZone = TimeZoneInfo.Utc;
            options.TimeSystem = new FixedTimeSystem();
            options.Temporal.TimeZoneProvider = UtcDefaultTimeZoneProvider.Instance;
            options.LimitRecursion(Math.Max(1, limits.ScriptRecursion));
            options.LimitMemory(Math.Max(1, limits.ScriptMemoryBytes));
            // 0 turns the wall clock off (next-phase-1-design.md section 5.7): the statement limit then bounds every call alone.
            if (limits.ScriptTimeoutMs > 0)
                options.TimeoutInterval(TimeSpan.FromMilliseconds(limits.ScriptTimeoutMs));
            options.MaxStatements(StatementLimit(limits));
            options.CancellationToken(runToken);
            options.Constraint(new CallCancellationConstraint(state));
            options.Constraints.StackOverflowGuard = true;
            options.Constraints.MaxArraySize = MaxArraySize(limits);
            options.Constraints.RegexTimeout = RegexTimeout(limits);
        });
        _values = new ScriptValues(_engine, _state, catalog, limits.ScriptRecursion, MaxArraySize(limits));
    }

    /// <summary>What this engine's scripts registered, in registration order.</summary>
    public IReadOnlyList<ScriptRegistration> Registrations => _registrations;

    /// <summary>The rules the scripts registered, with the severity each one declared (the Extensions screen and Settings › Validation).</summary>
    internal IReadOnlyList<(string Id, DiagnosticSeverity Severity, bool WholeModel)> Rules =>
        [.. _rules.Values.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => (r.Id, r.Severity, r.WholeModel))];

    /// <summary>Whether a limit breach, cancellation or host failure left the engine in a state the pool should not reuse.</summary>
    public bool Faulted { get; private set; }

    /// <summary>
    /// Creates an engine and runs the pool's scripts in it, then freezes its globals; when every script is a rule script, the globals
    /// are frozen before the scripts run, so a rule file cannot publish anything for another (next-phase-1-design.md section 5.7).
    /// </summary>
    /// <param name="scripts">The prepared scripts, in load order.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="catalog">The pool's reflection catalog.</param>
    /// <param name="runToken">The run's cancellation.</param>
    /// <param name="libraries">The packs <c>maquettiste.use</c> reaches (library packs); none by default.</param>
    /// <returns>The sandbox.</returns>
    public static ScriptSandbox Create(IReadOnlyList<PreparedScript> scripts, SandboxLimits limits, MemberCatalog catalog, CancellationToken runToken,
        IReadOnlyList<ScriptLibrary>? libraries = null)
    {
        runToken.ThrowIfCancellationRequested();
        var sandbox = new ScriptSandbox(limits, catalog, runToken, libraries ?? []);
        try
        {
            sandbox.Initialize(scripts);
            return sandbox;
        }
        catch
        {
            sandbox.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The largest statement limit (<c>limits.scriptStatements</c>): 2,000,000,000, below <see cref="int.MaxValue"/>, which the
    /// JavaScript engine reads as no limit. With the wall clock off (<c>scriptTimeoutMs: 0</c>) statements always bound a call.
    /// </summary>
    public const int StatementCap = 2_000_000_000;

    /// <summary>The longest a regular expression match may run when the wall clock is off, and the cap on the bound otherwise.</summary>
    private const int MaxRegexMs = 250;

    /// <summary>The statement limit of one call: <c>limits.scriptStatements</c> clamped to 1 to <see cref="StatementCap"/>.</summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The limit passed to the engine.</returns>
    public static int StatementLimit(SandboxLimits limits) => (int)Math.Clamp(limits.ScriptStatements, 1, StatementCap);

    /// <summary>
    /// The longest a single regular expression match may run: the script time limit, capped at 250 ms so a catastrophic
    /// pattern cannot hold the thread past the one-second cancellation budget (host-contracts 26), and 250 ms when the wall
    /// clock is off (<c>scriptTimeoutMs: 0</c>), since a match cannot be interrupted. The bound is wall-clock time, so a
    /// timed-out match is tried once more before it fails (<see cref="RegexRetry"/>); cancellation is checked between the
    /// attempts, so the budget holds.
    /// </summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The match timeout.</returns>
    public static TimeSpan RegexTimeout(SandboxLimits limits) =>
        TimeSpan.FromMilliseconds(limits.ScriptTimeoutMs <= 0 ? MaxRegexMs : Math.Min(limits.ScriptTimeoutMs, MaxRegexMs));

    /// <summary>The largest array a script may create, or return to the host: one element per 16 bytes of the memory limit.</summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The maximum length.</returns>
    public static uint MaxArraySize(SandboxLimits limits) => (uint)Math.Clamp(limits.ScriptMemoryBytes / 16, 1024, uint.MaxValue);

    /// <summary>
    /// Whether a script path is a model script, which decides MQ5002/MQ5003 versus MQ6016/MQ6007 and runs in a function scope of its
    /// own: the validation rule scripts directly in the model's <c>extensions/rules/</c> folder (engine-design.md section 5), and the
    /// rows scripts directly in <c>extensions/seeds/</c> (next-phase-2-design.md section 3.4), whose failures the seed rules report as
    /// MQ7127. The path's folder is <c>extensions/rules</c> (or <c>seeds</c>) or ends with <c>.maquettiste/extensions/rules</c> (or
    /// <c>seeds</c>). A pack script in a folder of that name deeper in its pack is not one.
    /// </summary>
    /// <param name="path">The script path.</param>
    /// <returns><see langword="true"/> for rule and rows scripts.</returns>
    public static bool IsRuleScript(string path) => InModelFolder(path, "rules") || InModelFolder(path, "seeds");

    /// <summary>Whether a script path is a rows script: a file directly in the model's <c>extensions/seeds/</c> folder.</summary>
    /// <param name="path">The script path.</param>
    /// <returns><see langword="true"/> for rows scripts.</returns>
    public static bool IsRowsScript(string path) => InModelFolder(path, "seeds");

    private static bool InModelFolder(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        if (slash < 0)
            return false;
        var folder = normalized[..slash];
        return string.Equals(folder, "extensions/" + name, StringComparison.Ordinal)
            || string.Equals(folder, ".maquettiste/extensions/" + name, StringComparison.Ordinal)
            || folder.EndsWith("/.maquettiste/extensions/" + name, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _engine.Dispose();
    }

    private void Initialize(IReadOnlyList<PreparedScript> scripts)
    {
        var global = _engine.Global;
        foreach (var name in RemovedGlobals)
            global.Delete(name);
        ReplaceRandom();
        GuardStringGrowth();
        GuardRegExp();
        DefaultIntlTimeZoneToUtc();
        global.FastSetProperty("maquettiste", new PropertyDescriptor(CreateApi(), PropertyFlag.Enumerable));

        // Rule files each run in their own function scope; with the globals frozen first, a global or prototype member one of them
        // sets is a TypeError while it loads. Pack scripts share their pack's globals until the freeze after loading.
        var freezeFirst = scripts.Count > 0 && scripts.All(s => s.Scoped);
        if (freezeFirst)
            DeepFreeze(global);
        foreach (var script in scripts)
        {
            _scripts.TryAdd(script.Source.Path, script);
            _loadingPath = script.Source.Path;
            _random.Seed(script.Source.Path);
            var program = script.Program;
            try
            {
                _engine.Constraints.Reset();
                _engine.Execute(in program);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var rule = IsRuleScript(script.Source.Path);
                throw Translate(ex, new CallSite($"Script {script.Source.Path} failed while loading", script.Source.Path, null, rule), _runToken);
            }
            finally
            {
                _engine.Advanced.ResetCallStack();
            }
        }

        _loadingPath = null;
        if (!freezeFirst)
            DeepFreeze(global);
    }

    private void ReplaceRandom()
    {
        var math = (ObjectInstance)_engine.Global.Get("Math");
        math.DefineOwnProperty("random", new PropertyDescriptor(
            new ClrFunction(_engine, "random", (_, _) => JsNumber.Create(_random.NextDouble()), 0, PropertyFlag.Configurable),
            PropertyFlag.Writable | PropertyFlag.Configurable));
    }

    /// <summary>
    /// <c>repeat</c>, <c>padStart</c> and <c>padEnd</c> can allocate a huge string in one step, before the per-statement memory
    /// check runs; they refuse results larger than the memory limit.
    /// </summary>
    private void GuardStringGrowth()
    {
        var prototype = (ObjectInstance)_engine.Global.Get("String").Get("prototype");
        foreach (var name in new[] { "repeat", "padStart", "padEnd" })
        {
            var original = prototype.Get(name);
            var isRepeat = string.Equals(name, "repeat", StringComparison.Ordinal);
            var guarded = new ClrFunction(_engine, name, (thisObj, args) =>
            {
                var text = TypeConverter.ToString(thisObj);
                var n = args.Length > 0 ? TypeConverter.ToIntegerOrInfinity(args[0]) : 0;
                var chars = isRepeat ? text.Length * n : n;
                if (chars * 2 > _limits.ScriptMemoryBytes)
                    throw new MemoryLimitExceededException($"String.prototype.{name} would allocate more than {_limits.ScriptMemoryBytes} bytes.");
                return _engine.Call(original, thisObj, args);
            }, 1, PropertyFlag.Configurable);
            prototype.DefineOwnProperty(name, new PropertyDescriptor(guarded, PropertyFlag.Writable | PropertyFlag.Configurable));
        }
    }

    /// <summary>
    /// Bounds every regular expression match. Jint gives a <c>RegExp</c> built at run time (<c>new RegExp(...)</c>, or the one
    /// <c>'...'.match(string)</c> builds) a fixed 5 s match timeout whatever the options say, so the methods that run a match
    /// re-create an over-long .NET regex with <see cref="RegexTimeout(SandboxLimits)"/> first. Each of them also checks the
    /// engine's constraints, so a built-in that matches many times in a row (a global <c>replace</c>, <c>split</c>,
    /// <c>matchAll</c>) still observes the time limit and both cancellation tokens between matches. A match timeout is retried
    /// once (<see cref="RegexRetry"/>) when the call ran no script code (<see cref="IsPlainMatch"/>) and the constraints, checked
    /// again, still allow it, so a thread stalled by a busy machine does not fail a trivial pattern.
    /// </summary>
    private void GuardRegExp()
    {
        var bound = RegexTimeout(_limits);
        var retimed = new ConditionalWeakTable<Regex, Regex>();
        var prototype = (ObjectInstance)_engine.Global.Get("RegExp").Get("prototype");
        var symbols = (ObjectInstance)_engine.Global.Get("Symbol");
        var methods = new (JsValue Key, string Name)[]
        {
            (new JsString("exec"), "exec"), (new JsString("test"), "test"),
            (symbols.Get("match"), "[Symbol.match]"), (symbols.Get("matchAll"), "[Symbol.matchAll]"),
            (symbols.Get("replace"), "[Symbol.replace]"), (symbols.Get("search"), "[Symbol.search]"), (symbols.Get("split"), "[Symbol.split]"),
        };
        foreach (var (key, name) in methods)
        {
            var descriptor = prototype.GetOwnProperty(key);
            if (descriptor == PropertyDescriptor.Undefined || descriptor.Value is not Function original)
                continue;
            var length = (int)TypeConverter.ToNumber(original.Get("length"));
            var guarded = new ClrFunction(_engine, name, (thisObj, args) =>
            {
                _engine.Constraints.Check();
                if (thisObj is JsRegExp regExp && regExp.Value is { } regex && regex.MatchTimeout > bound)
                {
                    regExp.Value = retimed.GetValue(regex, r => new Regex(r.ToString(), r.Options, bound));
                }

                var plain = IsPlainMatch(prototype, thisObj, args);
                var lastIndex = plain ? ((JsRegExp)thisObj).Get(LastIndex) : JsValue.Undefined;
                return RegexRetry.Run(() => _engine.Call(original, thisObj, args), () =>
                {
                    if (!plain)
                        return false;
                    _engine.Constraints.Check();
                    ((JsRegExp)thisObj).Set(LastIndex, lastIndex, throwOnError: false);
                    return true;
                });
            }, length, PropertyFlag.Configurable);
            prototype.DefineOwnProperty(key, new PropertyDescriptor(guarded, PropertyFlag.Writable | PropertyFlag.Configurable));
        }
    }

    private static readonly JsString LastIndex = new("lastIndex");

    /// <summary>
    /// Whether a match call can run no script code, so a second attempt after a match timeout is invisible: the receiver is a
    /// plain <c>RegExp</c> (the intrinsic prototype, no own property but a numeric <c>lastIndex</c>, so no own <c>exec</c>,
    /// <c>constructor</c> or <c>flags</c>) and every argument is a primitive (no <c>toString</c> to call, no replacer function).
    /// The only state a first attempt can change is <c>lastIndex</c>, which the retry restores.
    /// </summary>
    private static bool IsPlainMatch(ObjectInstance prototype, JsValue thisObj, JsValue[] args)
    {
        if (thisObj is not JsRegExp regExp || !ReferenceEquals(regExp.Prototype, prototype))
            return false;
        foreach (var key in regExp.GetOwnPropertyKeys(Types.String | Types.Symbol))
        {
            if (key is not JsString name || !string.Equals(name.ToString(), "lastIndex", StringComparison.Ordinal))
                return false;
        }

        var descriptor = regExp.GetOwnProperty(LastIndex);
        if (descriptor == PropertyDescriptor.Undefined || descriptor.IsAccessorDescriptor() || descriptor.Value is not JsNumber)
            return false;
        foreach (var arg in args)
        {
            if (arg is ObjectInstance)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Jint's <c>Intl.DateTimeFormat</c> takes its default time zone from the host, whatever the engine's time zone is.
    /// <c>Intl.DateTimeFormat</c> is replaced by a constructor that fills in <c>timeZone: 'UTC'</c> when the options give
    /// none, so formatting does not depend on the machine (engine-design.md section 10). The prototype, statics,
    /// <c>instanceof</c> and subclassing are unchanged. Temporal's default zone comes from <see cref="UtcDefaultTimeZoneProvider"/>.
    /// </summary>
    private void DefaultIntlTimeZoneToUtc()
    {
        const string Code = """
            (() => {
              const Original = Intl.DateTimeFormat;
              const withUtc = options => {
                if (options === undefined) return { timeZone: 'UTC' };
                const o = Object(options);
                return o.timeZone === undefined ? Object.create(o, { timeZone: { value: 'UTC', enumerable: true } }) : o;
              };
              const DateTimeFormat = function DateTimeFormat(...args) {
                return Reflect.construct(Original, [args[0], withUtc(args[1])], new.target ?? Original);
              };
              Object.defineProperty(DateTimeFormat, 'length', { value: 0 });
              Object.defineProperty(DateTimeFormat, 'prototype', { value: Original.prototype, writable: false });
              Object.defineProperty(DateTimeFormat, 'supportedLocalesOf',
                { value: Original.supportedLocalesOf, writable: true, configurable: true });
              Object.defineProperty(Original.prototype, 'constructor', { value: DateTimeFormat, writable: true, configurable: true });
              Object.defineProperty(Intl, 'DateTimeFormat', { value: DateTimeFormat, writable: true, configurable: true });
            })();
            """;
        _engine.Execute(Code, InternalSourcePrefix + "intl");
        _engine.Advanced.ResetCallStack();
    }

    private void DeepFreeze(ObjectInstance root)
    {
        var seen = new HashSet<ObjectInstance>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ObjectInstance>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
                continue;
            foreach (var key in current.GetOwnPropertyKeys(Types.String | Types.Symbol))
            {
                var descriptor = current.GetOwnProperty(key);
                if (descriptor == PropertyDescriptor.Undefined)
                    continue;
                if (descriptor.IsAccessorDescriptor())
                {
                    Push(descriptor.Get);
                    Push(descriptor.Set);
                }
                else
                {
                    Push(descriptor.Value);
                }
            }

            Push(current.Prototype);
            _values.Freeze(current);
        }

        void Push(JsValue? value)
        {
            if (value is ObjectInstance obj && !seen.Contains(obj))
                pending.Push(obj);
        }
    }

    private JsObject CreateApi()
    {
        var api = new JsObject(_engine);
        foreach (var (name, kind) in new[]
        {
            ("helper", ScriptRegistrationKind.Helper), ("selector", ScriptRegistrationKind.Selector),
            ("filter", ScriptRegistrationKind.Filter), ("transform", ScriptRegistrationKind.Transform),
        })
        {
            api.FastSetProperty(name, new PropertyDescriptor(
                new ClrFunction(_engine, name, (_, args) => Register(kind, name, args), 2, PropertyFlag.Configurable), PropertyFlag.Enumerable));
        }

        api.FastSetProperty("rule", new PropertyDescriptor(
            new ClrFunction(_engine, "rule", (_, args) => RegisterRule(args), 1, PropertyFlag.Configurable), PropertyFlag.Enumerable));
        api.FastSetProperty("rows", new PropertyDescriptor(
            new ClrFunction(_engine, "rows", (_, args) => RegisterRows(args), 1, PropertyFlag.Configurable), PropertyFlag.Enumerable));
        var paramsGetter = new ClrFunction(_engine, "get params", (_, _) => CurrentParameters(), 0, PropertyFlag.Configurable);
        api.DefineOwnProperty("params", new GetSetPropertyDescriptor(paramsGetter, JsValue.Undefined, enumerable: true, configurable: false));
        api.FastSetProperty("use", new PropertyDescriptor(
            new ClrFunction(_engine, "use", (_, args) => Use(args), 1, PropertyFlag.Configurable), PropertyFlag.Enumerable));
        return api;
    }

    /// <summary>
    /// <c>maquettiste.use(name)</c> (library packs, next-phase-2-design.md section 5.2): the frozen object of a used pack's helpers,
    /// the same object on every call in this engine. It may run while scripts load; a name outside the libraries is a <c>TypeError</c>.
    /// </summary>
    private JsValue Use(JsValue[] args)
    {
        var name = args.Length > 0 && args[0].IsString() ? args[0].AsString() : null;
        if (string.IsNullOrEmpty(name))
            throw TypeError("maquettiste.use(name): name must be the name of a pack in this script's uses.");
        if (_libraryObjects.TryGetValue(name, out var known))
            return known;
        if (!_libraries.TryGetValue(name, out var library))
        {
            if (_loadingPath is null && _callContext is { Libraries: null })
                throw TypeError($"maquettiste.use('{name}') cannot reach a used pack here: selectors and script filters run while planning, where no used pack's helpers can run; call a helper of this pack instead.");
            throw TypeError(_libraries.Count == 0
                ? $"maquettiste.use('{name}'): this script's uses reach no pack; add {{ \"pack\": \"{name}\", \"version\": ... }} to uses."
                : $"maquettiste.use('{name}'): pack '{name}' is not in this script's uses; it reaches {string.Join(", ", _libraries.Keys.Order(StringComparer.Ordinal).Select(k => "'" + k + "'"))}.");
        }

        var members = new List<KeyValuePair<string, JsValue>>(library.Helpers.Count);
        foreach (var helper in library.Helpers.Order(StringComparer.Ordinal))
        {
            var pack = library.Pack;
            var fn = new ClrFunction(_engine, helper, (_, callArgs) => CallLibrary(pack, helper, callArgs), 0, PropertyFlag.Configurable);
            members.Add(new(helper, _values.Freeze(fn)));
        }

        var value = _values.FrozenObject(members);
        _libraryObjects[name] = value;
        return value;
    }

    /// <summary>
    /// A call of a used pack's helper through <c>maquettiste.use</c>: the arguments leave this engine as a script's results do, the
    /// call goes to <see cref="ScriptCallContext.Libraries"/>, and the result enters as a helper's arguments do. Without a call context
    /// (while scripts load) or without libraries (selectors and filters, which run while planning) it is a <c>TypeError</c>.
    /// </summary>
    private JsValue CallLibrary(string pack, string helper, JsValue[] args)
    {
        var label = $"maquettiste.use('{pack}').{helper}";
        if (_callContext is not { } ctx)
            throw TypeError($"{label} cannot be called while scripts load: no call context exists yet. Call it inside a helper or rule.");
        if (ctx.Libraries is not { } libraries)
            throw TypeError($"{label} cannot be called here: selectors and script filters run while planning, where no used pack's helpers can run; call a helper of this pack instead.");
        var values = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
            values[i] = _values.FromJs(args[i], $"argument {i + 1} of {label}");
        return _values.ToJs(libraries.Call(pack, helper, values, ctx));
    }

    /// <summary>
    /// <c>maquettiste.params</c>: the current call's parameters, converted once per call (the caller may pass the same
    /// dictionary to the next call with different contents, so nothing is kept between calls).
    /// </summary>
    private JsValue CurrentParameters()
    {
        var source = _state.Active ? _state.Parameters : null;
        if (_paramsValue is not null && _paramsCall == _state.CallNumber && ReferenceEquals(source, _paramsSource))
            return _paramsValue;
        _paramsCall = _state.CallNumber;
        _paramsSource = source;
        _paramsValue = source is null ? _values.FrozenObject([]) : _values.ToJs(source);
        return _paramsValue;
    }

    private JavaScriptException TypeError(string message) => new(_engine.Intrinsics.TypeError, message);

    private JsValue Register(ScriptRegistrationKind kind, string api, JsValue[] args)
    {
        var path = _loadingPath ?? throw TypeError($"maquettiste.{api} can only be called while scripts load.");
        RefuseInRowsScript(path, api);
        var name = args.Length > 0 && args[0] is JsString s ? s.ToString() : "";
        if (name.Length == 0)
            throw TypeError($"maquettiste.{api}(name, fn): name must be a non-empty string.");
        if (args.Length < 2 || args[1] is not Function fn)
            throw TypeError($"maquettiste.{api}('{name}', fn): fn must be a function.");
        var plain = kind == ScriptRegistrationKind.Helper && HelperOptions(name, args.Length > 2 ? args[2] : JsValue.Undefined);
        if (_functions.TryGetValue((kind, name), out var existing))
            throw TypeError($"A {api} named '{name}' is already registered by {existing.DeclaredIn}.");
        _functions.Add((kind, name), new Registered(kind, name, path, fn, plain));
        _registrations.Add(new ScriptRegistration(kind, name, path));
        return JsValue.Undefined;
    }

    /// <summary>
    /// Reads the options of <c>maquettiste.helper(name, fn, options)</c>: absent, or an object whose only key is <c>plain</c>, a
    /// boolean (next-phase-1-design.md section 5.5). Anything else is a <c>TypeError</c>, so the script fails to load.
    /// </summary>
    /// <returns>Whether the helper takes plain copies of its arguments.</returns>
    private bool HelperOptions(string name, JsValue options)
    {
        if (options.IsUndefined())
            return false;
        if (options is not ObjectInstance obj || options is Function || obj.IsArray())
            throw TypeError($"maquettiste.helper('{name}', fn, options): options must be an object such as {{ plain: true }}.");
        var plain = false;
        foreach (var key in obj.GetOwnPropertyKeys(Types.String))
        {
            var value = obj.Get(key);
            if (!string.Equals(key.ToString(), "plain", StringComparison.Ordinal))
                throw TypeError($"maquettiste.helper('{name}', fn, options): unknown option '{key}'; the only option is plain.");
            if (value is not JsBoolean flag)
                throw TypeError($"maquettiste.helper('{name}', fn, options): plain must be true or false.");
            plain = TypeConverter.ToBoolean(flag);
        }

        return plain;
    }

    /// <summary>A rows script registers its rows and nothing else (next-phase-2-design.md section 3.4): any other call fails its load.</summary>
    private void RefuseInRowsScript(string path, string api)
    {
        if (IsRowsScript(path))
            throw TypeError($"maquettiste.{api} cannot be called by a rows script ({path}): a rows script registers its rows once with maquettiste.rows(fn).");
    }

    /// <summary>
    /// <c>maquettiste.rows(fn)</c>: a rows script's function, <c>fn(model, seed)</c>, registered under the script's path. Only a script in
    /// <c>extensions/seeds/</c> may call it, once; a rule or pack script that calls it fails to load.
    /// </summary>
    private JsValue RegisterRows(JsValue[] args)
    {
        var path = _loadingPath ?? throw TypeError("maquettiste.rows can only be called while scripts load.");
        if (!IsRowsScript(path))
            throw TypeError($"maquettiste.rows can only be called by a rows script in extensions/seeds/, not by {path}.");
        if (args.Length == 0 || args[0] is not Function fn)
            throw TypeError("maquettiste.rows(fn): fn must be a function that takes (model, seed) and returns the rows.");
        if (_functions.ContainsKey((ScriptRegistrationKind.Rows, path)))
            throw TypeError($"maquettiste.rows is called more than once by {path}: a rows script registers its rows exactly once.");
        _functions.Add((ScriptRegistrationKind.Rows, path), new Registered(ScriptRegistrationKind.Rows, path, path, fn));
        _registrations.Add(new ScriptRegistration(ScriptRegistrationKind.Rows, path, path));
        return JsValue.Undefined;
    }

    private JsValue RegisterRule(JsValue[] args)
    {
        var path = _loadingPath ?? throw TypeError("maquettiste.rule can only be called while scripts load.");
        RefuseInRowsScript(path, "rule");
        if (args.Length == 0 || args[0] is not ObjectInstance spec)
            throw TypeError("maquettiste.rule({ id, severity, kinds, scope, check }): the argument must be an object.");
        var id = spec.Get("id") is JsString idValue ? idValue.ToString() : "";
        if (id.Length == 0 || id.Any(char.IsWhiteSpace))
            throw TypeError("maquettiste.rule: id must be a non-empty string without whitespace.");
        var severity = DiagnosticSeverity.Error;
        var severityValue = spec.Get("severity");
        if (!severityValue.IsUndefined() && !TryParseSeverity(severityValue, out severity))
            throw TypeError($"maquettiste.rule '{id}': severity must be 'error', 'warning' or 'info'.");
        FrozenSet<string>? kinds = null;
        var kindsValue = spec.Get("kinds");
        if (!kindsValue.IsUndefined() && !kindsValue.IsNull())
        {
            if (kindsValue is not ObjectInstance kindList)
                throw TypeError($"maquettiste.rule '{id}': kinds must be an array of kind names.");
            var names = new List<string>();
            var length = TypeConverter.ToLength(kindList.Get("length"));
            for (var i = 0UL; i < length; i++)
            {
                var item = kindList.Get(JsNumber.Create((double)i));
                if (item is not JsString kindName || !KindInfo.TryGet(kindName.ToString(), out _))
                    throw TypeError($"maquettiste.rule '{id}': unknown kind '{TypeConverter.ToString(item)}'.");
                names.Add(kindName.ToString());
            }

            kinds = names.Count == 0 ? null : names.ToFrozenSet(StringComparer.Ordinal);
        }

        // scope "element" (the default) checks each element of its kinds; "model" checks the whole model once (section 5.7).
        var wholeModel = false;
        var scopeValue = spec.Get("scope");
        if (!scopeValue.IsUndefined())
        {
            wholeModel = (scopeValue is JsString scopeName ? scopeName.ToString() : null) switch
            {
                "element" => false,
                "model" => true,
                _ => throw TypeError($"maquettiste.rule '{id}': scope must be 'element' or 'model'."),
            };
        }

        if (wholeModel && !kindsValue.IsUndefined() && !kindsValue.IsNull())
            throw TypeError($"maquettiste.rule '{id}': a rule with scope 'model' checks the whole model once and takes no kinds.");
        if (spec.Get("check") is not Function check)
            throw TypeError($"maquettiste.rule '{id}': check must be a function.");
        if (_rules.TryGetValue(id, out var existing))
            throw TypeError($"A rule with id '{id}' is already registered by {existing.DeclaredIn}.");
        _rules.Add(id, new RuleSpec(id, severity, kinds, check, path, wholeModel));
        _registrations.Add(new ScriptRegistration(ScriptRegistrationKind.Rule, id, path) { WholeModel = wholeModel, Kinds = kinds });
        return JsValue.Undefined;
    }

    private static bool TryParseSeverity(JsValue value, out DiagnosticSeverity severity)
    {
        switch (value is JsString s ? s.ToString() : null)
        {
            case "error":
                severity = DiagnosticSeverity.Error;
                return true;
            case "warning":
                severity = DiagnosticSeverity.Warning;
                return true;
            case "info":
                severity = DiagnosticSeverity.Info;
                return true;
            default:
                severity = DiagnosticSeverity.Error;
                return false;
        }
    }

    /// <inheritdoc/>
    public object? CallHelper(string name, IReadOnlyList<object?> args, ScriptCallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(args);
        var fn = Lookup(ScriptRegistrationKind.Helper, name, "helper");
        return Run(new CallSite($"Helper '{name}'", fn.DeclaredIn, null, false), ctx, () =>
        {
            JsValue[] jsArgs;
            if (fn.Plain)
            {
                jsArgs = _values.ToPlainJs(args);
            }
            else
            {
                jsArgs = new JsValue[args.Count];
                for (var i = 0; i < jsArgs.Length; i++)
                    jsArgs[i] = _values.ToJs(args[i]);
            }

            var result = _engine.Call(fn.Function, JsValue.Undefined, jsArgs);
            return _values.FromJs(result, $"result of helper '{name}'");
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Select(string name, ResolvedModel model, ScriptCallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(model);
        var fn = Lookup(ScriptRegistrationKind.Selector, name, "selector");
        return Run(new CallSite($"Selector '{name}'", fn.DeclaredIn, null, false), ctx, () =>
        {
            var result = _engine.Call(fn.Function, JsValue.Undefined, [_values.ToJs(model)]);
            return ToIds(result, name);
        });
    }

    /// <inheritdoc/>
    public bool Filter(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(model);
        var fn = Lookup(ScriptRegistrationKind.Filter, name, "filter");
        return Run(new CallSite($"Filter '{name}'", fn.DeclaredIn, element?.Id, false), ctx, () =>
        {
            var result = _engine.Call(fn.Function, JsValue.Undefined, [_values.ToJs(element), _values.ToJs(model)]);
            return TypeConverter.ToBoolean(result);
        });
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Transform(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(model);
        var fn = Lookup(ScriptRegistrationKind.Transform, name, "transform");
        return Run(new CallSite($"Transform '{name}'", fn.DeclaredIn, element?.Id, false), ctx, () =>
        {
            var result = _engine.Call(fn.Function, JsValue.Undefined, [_values.ToJs(element), _values.ToJs(model)]);
            if (result.IsUndefined() || result.IsNull())
                return (IReadOnlyDictionary<string, object?>)new ScriptObjectMap();
            return _values.FromJs(result, $"result of transform '{name}'") as ScriptObjectMap
                ?? throw new ScriptValueException($"Transform '{name}' must return a plain object (or null).");
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<Diagnostic> RunRule(string ruleId, ElementDocument element, ModelSnapshot model, ScriptCallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(ruleId);
        if (!_rules.TryGetValue(ruleId, out var rule))
        {
            throw new ScriptErrorException(new Diagnostic("MQ5002", DiagnosticSeverity.Error,
                $"No validation rule with id '{ruleId}' is registered.", element.Element.Id, element.Path, null, null, null));
        }

        if (rule.WholeModel || (rule.Kinds is not null && !rule.Kinds.Contains(KindInfo.Get(element.Element.Kind).Name)))
            return []; // a whole-model rule runs through RunModelRule only

        var diagnostics = new List<Diagnostic>();
        var open = true;
        var report = new ClrFunction(_engine, "report", (_, args) =>
        {
            if (!open)
                throw TypeError($"report() of rule '{ruleId}' was called after the rule returned.");
            diagnostics.Add(CreateReport(rule, element, args));
            return JsValue.Undefined;
        }, 2, PropertyFlag.Configurable);
        try
        {
            Run(new CallSite($"Rule 'x/{ruleId}'", rule.DeclaredIn, element.Element.Id, true), ctx, () =>
            {
                _engine.Call(rule.Check, JsValue.Undefined, [_values.FromJson(element.Json), RuleModel(model), report]);
                return 0;
            });
        }
        catch (ScriptErrorException ex)
        {
            diagnostics.Add(ex.Diagnostic);
        }
        finally
        {
            open = false;
        }

        return diagnostics;
    }

    /// <inheritdoc/>
    public IReadOnlyList<Diagnostic> RunModelRule(string ruleId, ModelSnapshot model, ScriptCallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        ArgumentNullException.ThrowIfNull(model);
        if (!_rules.TryGetValue(ruleId, out var rule) || !rule.WholeModel)
        {
            throw new ScriptErrorException(new Diagnostic("MQ5002", DiagnosticSeverity.Error,
                $"No whole-model validation rule with id '{ruleId}' is registered.", null, rule?.DeclaredIn, null, null, null));
        }

        var diagnostics = new List<Diagnostic>();
        var open = true;
        var report = new ClrFunction(_engine, "report", (_, args) =>
        {
            if (!open)
                throw TypeError($"report() of rule '{ruleId}' was called after the rule returned.");
            diagnostics.Add(CreateModelReport(rule, model, args));
            return JsValue.Undefined;
        }, 2, PropertyFlag.Configurable);
        try
        {
            Run(new CallSite($"Rule 'x/{ruleId}'", rule.DeclaredIn, null, true), ctx, () =>
            {
                _engine.Call(rule.Check, JsValue.Undefined, [RuleModel(model), report]);
                return 0;
            });
        }
        catch (ScriptErrorException ex)
        {
            diagnostics.Add(ex.Diagnostic);
        }
        finally
        {
            open = false;
        }

        return diagnostics;
    }

    /// <inheritdoc/>
    public JsonElement RunRows(string script, ElementDocument seed, ModelSnapshot model, ScriptCallContext ctx)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(model);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_functions.TryGetValue((ScriptRegistrationKind.Rows, script), out var fn))
        {
            throw new ScriptErrorException(new Diagnostic("MQ5002", DiagnosticSeverity.Error,
                $"Rows script {script} registers no rows: it calls maquettiste.rows(fn) exactly once.", seed.Element.Id, script, null, null, null));
        }

        return Run(new CallSite($"Rows script {script}", script, seed.Element.Id, true), ctx, () =>
        {
            var result = _engine.Call(fn.Function, JsValue.Undefined, [RuleModel(model), _values.FromJson(seed.Json)]);
            return RowsJson(_values.FromJs(result, $"result of rows script {script}"), script);
        });
    }

    private static readonly JsonWriterOptions RowsWriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The value a rows function returned, as JSON: an array of plain objects whose values are JSON values (null, booleans, strings,
    /// finite numbers, arrays and objects of them). Anything else is a <see cref="ScriptValueException"/>, which the call reports.
    /// </summary>
    private static JsonElement RowsJson(object? value, string script)
    {
        if (value is not IReadOnlyList<object?> rows || value is ScriptObjectMap)
            throw new ScriptValueException($"Rows script {script} must return an array of row objects, not {Shape(value)}.");
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        // The model's canonical form (EngineJson): the raw text of a scripted cell is the text a stored cell of the same value has, so
        // keys compared as raw JSON ('café', 'R&D') match.
        using (var writer = new Utf8JsonWriter(buffer, RowsWriterOptions))
        {
            writer.WriteStartArray();
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] is not ScriptObjectMap row)
                    throw new ScriptValueException($"Rows script {script} returned {Shape(rows[i])} at index {i.ToString(CultureInfo.InvariantCulture)}; each row is a plain object.");
                Write(writer, row, script, i);
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();

        static void Write(Utf8JsonWriter writer, object? value, string script, int row)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case bool b:
                    writer.WriteBooleanValue(b);
                    break;
                case string s:
                    writer.WriteStringValue(s);
                    break;
                case long l:
                    writer.WriteNumberValue(l);
                    break;
                case double d when double.IsFinite(d):
                    writer.WriteNumberValue(d);
                    break;
                case ScriptObjectMap map:
                    writer.WriteStartObject();
                    foreach (var (key, item) in map)
                    {
                        writer.WritePropertyName(key);
                        Write(writer, item, script, row);
                    }

                    writer.WriteEndObject();
                    break;
                case IReadOnlyList<object?> list:
                    writer.WriteStartArray();
                    foreach (var item in list)
                        Write(writer, item, script, row);
                    writer.WriteEndArray();
                    break;
                default:
                    throw new ScriptValueException(
                        $"Rows script {script} returned {Shape(value)} in row {row.ToString(CultureInfo.InvariantCulture)}; a row holds JSON values only.");
            }
        }

        static string Shape(object? value) => value switch
        {
            null => "nothing",
            double => "a number that is not finite",
            ScriptObjectMap => "an object",
            IReadOnlyList<object?> => "an array",
            string => "a string",
            bool => "a boolean",
            long => "a number",
            IResolvedObject r => "a model object (" + r.Kind + ")",
            _ => "a " + value.GetType().Name,
        };
    }

    private Diagnostic CreateReport(RuleSpec rule, ElementDocument element, JsValue[] args)
    {
        var message = args.Length > 0 && !args[0].IsUndefined() ? TypeConverter.ToString(args[0]) : "";
        var (pointer, severity) = ReportOptions(rule, args.Length > 1 ? args[1] as ObjectInstance : null);
        return new Diagnostic("x/" + rule.Id, severity, message, element.Element.Id, element.Path, pointer, null, null);
    }

    /// <summary>
    /// A finding of a whole-model rule: <c>report(message, { element, pointer, severity })</c>, where <c>element</c> names a document
    /// or sub-element, whose file the finding takes; the entry's pointer prefixes the given one. A missing or unknown <c>element</c> is a
    /// <c>TypeError</c>, so the rule fails with MQ5002 on its file.
    /// </summary>
    private Diagnostic CreateModelReport(RuleSpec rule, ModelSnapshot model, JsValue[] args)
    {
        var message = args.Length > 0 && !args[0].IsUndefined() ? TypeConverter.ToString(args[0]) : "";
        var options = args.Length > 1 ? args[1] as ObjectInstance : null;
        var elementValue = options?.Get("element") ?? JsValue.Undefined;
        if (elementValue is not JsString elementName || elementName.Length == 0)
            throw TypeError("report(message, { element }): a whole-model rule names the element or sub-element of each finding by its id.");
        var id = elementName.ToString();
        if (!model.TryGetEntry(id, out var entry) || model.GetDocument(id) is not { } document)
            throw TypeError($"report(message, {{ element }}): no element or sub-element has the id '{id}'.");
        var (pointer, severity) = ReportOptions(rule, options);
        return new Diagnostic("x/" + rule.Id, severity, message, id, document.Path, entry.JsonPointer + (pointer ?? ""), null, null);
    }

    /// <summary>The <c>pointer</c> and <c>severity</c> options of <c>report</c>; the rule's severity when none is given.</summary>
    private (string? Pointer, DiagnosticSeverity Severity) ReportOptions(RuleSpec rule, ObjectInstance? options)
    {
        var severity = rule.Severity;
        string? pointer = null;
        if (options is null)
            return (pointer, severity);
        var pointerValue = options.Get("pointer");
        if (!pointerValue.IsUndefined() && !pointerValue.IsNull())
        {
            pointer = pointerValue is JsString p && (p.Length == 0 || p.ToString()[0] == '/')
                ? p.ToString()
                : throw TypeError("report(message, { pointer }): pointer must be a JSON pointer string such as '/attributes/0/name'.");
        }

        var severityValue = options.Get("severity");
        if (!severityValue.IsUndefined() && !TryParseSeverity(severityValue, out severity))
            throw TypeError("report(message, { severity }): severity must be 'error', 'warning' or 'info'.");
        return (pointer, severity);
    }

    private Registered Lookup(ScriptRegistrationKind kind, string name, string label)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_functions.TryGetValue((kind, name), out var registered))
            return registered;
        throw new ScriptErrorException(new Diagnostic("MQ6016", DiagnosticSeverity.Error,
            $"No script registers a {label} named '{name}'.", null, null, null, null, null));
    }

    private IReadOnlyList<string> ToIds(JsValue result, string name)
    {
        if (result is not ObjectInstance list || result is Function)
            throw new ScriptValueException($"Selector '{name}' must return an array of elements or ids.");
        var length = _values.ArrayLength(list, $"result of selector '{name}'");
        var ids = new List<string>((int)Math.Min(length, 1024));
        for (var i = 0U; i < length; i++)
        {
            if (i % 1024 == 0)
                _engine.Constraints.Check();
            var item = list.Get(JsNumber.Create(i));
            ids.Add(item switch
            {
                JsString s => s.ToString(),
                JsModelProxy { Target: IResolvedObject resolved } => resolved.Id,
                _ => throw new ScriptValueException($"Selector '{name}' returned an item that is neither an element nor an id (index {i})."),
            });
        }

        return ids;
    }
}
