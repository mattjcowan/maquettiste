using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
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
using AstScript = Acornima.Ast.Script;

namespace Maquettiste.Engine.Scripting;

/// <summary>A script prepared once per pool (<c>JsEngine.PrepareScript</c>) and shared by its engines.</summary>
/// <param name="Source">The script file.</param>
/// <param name="Program">The prepared program.</param>
internal sealed record PreparedScript(ScriptSource Source, Prepared<AstScript> Program)
{
    /// <summary>Prepares scripts in load order; a syntax error is a <see cref="ScriptErrorException"/> with file, line and column.</summary>
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
            try
            {
                prepared.Add(new PreparedScript(script, JsEngine.PrepareScript(script.Code, script.Path, strict: true, options)));
            }
            catch (ScriptPreparationException ex)
            {
                var parse = ex.InnerException as ParseErrorException;
                var rule = ScriptSandbox.IsRuleScript(script.Path) ? "MQ5002" : "MQ6016";
                throw new ScriptErrorException(
                    new Diagnostic(rule, DiagnosticSeverity.Error,
                        $"Script {script.Path} has a syntax error: {parse?.Description ?? ex.Message}",
                        null, script.Path, null, parse?.LineNumber, parse is null ? null : parse.Column + 1),
                    ex);
            }
        }

        return prepared;
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
    private string? _loadingPath;
    private bool _inCall;
    private bool _disposed;
    private IReadOnlyDictionary<string, object?>? _paramsSource;
    private JsValue? _paramsValue;
    private long _paramsCall = -1;
    private ModelSnapshot? _ruleSnapshot;
    private JsValue? _ruleModel;

    private ScriptSandbox(SandboxLimits limits, MemberCatalog catalog, CancellationToken runToken)
    {
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
            options.TimeoutInterval(TimeSpan.FromMilliseconds(Math.Max(1, limits.ScriptTimeoutMs)));
            options.MaxStatements((int)Math.Clamp(limits.ScriptStatements, 1, int.MaxValue));
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

    /// <summary>Whether a limit breach, cancellation or host failure left the engine in a state the pool should not reuse.</summary>
    public bool Faulted { get; private set; }

    /// <summary>Creates an engine and runs the pool's scripts in it, then freezes its globals.</summary>
    /// <param name="scripts">The prepared scripts, in load order.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="catalog">The pool's reflection catalog.</param>
    /// <param name="runToken">The run's cancellation.</param>
    /// <returns>The sandbox.</returns>
    public static ScriptSandbox Create(IReadOnlyList<PreparedScript> scripts, SandboxLimits limits, MemberCatalog catalog, CancellationToken runToken)
    {
        runToken.ThrowIfCancellationRequested();
        var sandbox = new ScriptSandbox(limits, catalog, runToken);
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
    /// The longest a single regular expression match may run: the script time limit, capped at 250 ms so a catastrophic
    /// pattern cannot hold the thread past the one-second cancellation budget (host-contracts 26).
    /// </summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The match timeout.</returns>
    public static TimeSpan RegexTimeout(SandboxLimits limits) => TimeSpan.FromMilliseconds(Math.Clamp(limits.ScriptTimeoutMs, 1, 250));

    /// <summary>The largest array a script may create, or return to the host: one element per 16 bytes of the memory limit.</summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The maximum length.</returns>
    public static uint MaxArraySize(SandboxLimits limits) => (uint)Math.Clamp(limits.ScriptMemoryBytes / 16, 1024, uint.MaxValue);

    /// <summary>
    /// Whether a script path is a validation rule script, which decides MQ5002/MQ5003 versus MQ6016/MQ6007. Rule scripts are
    /// the files directly in the model's <c>extensions/rules/</c> folder (engine-design.md section 5): the path's folder is
    /// <c>extensions/rules</c> or ends with <c>.maquettiste/extensions/rules</c>. A pack script in a folder of that name deeper
    /// in its pack is not a rule script.
    /// </summary>
    /// <param name="path">The script path.</param>
    /// <returns><see langword="true"/> for rule scripts.</returns>
    public static bool IsRuleScript(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        if (slash < 0)
            return false;
        var folder = normalized[..slash];
        return string.Equals(folder, "extensions/rules", StringComparison.Ordinal)
            || string.Equals(folder, ".maquettiste/extensions/rules", StringComparison.Ordinal)
            || folder.EndsWith("/.maquettiste/extensions/rules", StringComparison.Ordinal);
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

        foreach (var script in scripts)
        {
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
    /// <c>matchAll</c>) still observes the time limit and both cancellation tokens between matches.
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

                return _engine.Call(original, thisObj, args);
            }, length, PropertyFlag.Configurable);
            prototype.DefineOwnProperty(key, new PropertyDescriptor(guarded, PropertyFlag.Writable | PropertyFlag.Configurable));
        }
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
        var paramsGetter = new ClrFunction(_engine, "get params", (_, _) => CurrentParameters(), 0, PropertyFlag.Configurable);
        api.DefineOwnProperty("params", new GetSetPropertyDescriptor(paramsGetter, JsValue.Undefined, enumerable: true, configurable: false));
        return api;
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
        var name = args.Length > 0 && args[0] is JsString s ? s.ToString() : "";
        if (name.Length == 0)
            throw TypeError($"maquettiste.{api}(name, fn): name must be a non-empty string.");
        if (args.Length < 2 || args[1] is not Function fn)
            throw TypeError($"maquettiste.{api}('{name}', fn): fn must be a function.");
        if (_functions.TryGetValue((kind, name), out var existing))
            throw TypeError($"A {api} named '{name}' is already registered by {existing.DeclaredIn}.");
        _functions.Add((kind, name), new Registered(kind, name, path, fn));
        _registrations.Add(new ScriptRegistration(kind, name, path));
        return JsValue.Undefined;
    }

    private JsValue RegisterRule(JsValue[] args)
    {
        var path = _loadingPath ?? throw TypeError("maquettiste.rule can only be called while scripts load.");
        if (args.Length == 0 || args[0] is not ObjectInstance spec)
            throw TypeError("maquettiste.rule({ id, severity, kinds, check }): the argument must be an object.");
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

        if (spec.Get("check") is not Function check)
            throw TypeError($"maquettiste.rule '{id}': check must be a function.");
        if (_rules.TryGetValue(id, out var existing))
            throw TypeError($"A rule with id '{id}' is already registered by {existing.DeclaredIn}.");
        _rules.Add(id, new RuleSpec(id, severity, kinds, check, path));
        _registrations.Add(new ScriptRegistration(ScriptRegistrationKind.Rule, id, path));
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
            var jsArgs = new JsValue[args.Count];
            for (var i = 0; i < jsArgs.Length; i++)
                jsArgs[i] = _values.ToJs(args[i]);
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

        if (rule.Kinds is not null && !rule.Kinds.Contains(KindInfo.Get(element.Element.Kind).Name))
            return [];

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

    private Diagnostic CreateReport(RuleSpec rule, ElementDocument element, JsValue[] args)
    {
        var message = args.Length > 0 && !args[0].IsUndefined() ? TypeConverter.ToString(args[0]) : "";
        var severity = rule.Severity;
        string? pointer = null;
        if (args.Length > 1 && args[1] is ObjectInstance options)
        {
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
        }

        return new Diagnostic("x/" + rule.Id, severity, message, element.Element.Id, element.Path, pointer, null, null);
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
