using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acornima.Ast;
using Jint;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Validation;
using JsEngine = Jint.Engine;

namespace Maquettiste.Engine.Processes;

/// <summary>An expression that does not parse (MQ9501).</summary>
/// <param name="Id">The guard or action id.</param>
/// <param name="Pointer">The JSON pointer of the expression in the process file.</param>
/// <param name="Name">The guard or action name.</param>
/// <param name="Kind">"guard" or "action".</param>
/// <param name="Message">The parser's message.</param>
public sealed record ExpressionProblem(string Id, string Pointer, string Name, string Kind, string Message);

/// <summary>
/// A process's guard and action expressions compiled once (phase-3-design.md section 4.2): each expression that parses becomes a
/// prepared sandbox script <c>(context, event) =&gt; (expression)</c> registered under the guard or action id. Parsing never runs code
/// (MQ9501). A guard's condition, written in the expression language (next-phase-3-design.md section 3.1, "Process guards"), is typed in
/// its process's scope and lowered to JavaScript into the same script, which then loads after the expression runtime, prepared once for
/// every process (<see cref="ExprRuntime.Prepared"/>); a process without one loads no runtime. Compiled forms are cached by the content
/// of the process's expressions and lowered conditions.
/// </summary>
public sealed class ProcessExpressions
{
    /// <summary>The per-expression deadline (ms).</summary>
    public const int DeadlineMs = 50;

    /// <summary>The per-expression statement limit.</summary>
    public const long StatementLimit = 100_000;

    // The compiled expressions travel with the process object: replaying one model reuses them however many processes it has,
    // and they go when the model does. The content cache only shares them across models (a process an edit left unchanged),
    // up to CacheLimit; it is consulted once per process object, so a large model no longer clears it on every replay.
    // A process with conditions lowers them in its model's scope (an enum member's code, a cited constraint), which can change
    // while the process object does not, so its compiled forms travel with the model and the process together.
    private static readonly ConditionalWeakTable<Process, ProcessExpressions> ByProcess = new();
    private static readonly ConditionalWeakTable<ModelSnapshot, ConditionalWeakTable<Process, ProcessExpressions>> ByModel = new();
    private const int CacheLimit = 512;
    private static readonly ConcurrentDictionary<string, ProcessExpressions> Cache = new(StringComparer.Ordinal);

    private ProcessExpressions(Process process, IReadOnlyList<LoweredCondition> conditions, JsonElement? subjectDefaults)
    {
        SubjectDefaults = subjectDefaults;
        var code = new StringBuilder();
        var compiled = new HashSet<string>(StringComparer.Ordinal);
        var replaced = conditions.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Problems = Collect(process, compiled, code, replaced);
        if (conditions.Count > 0)
        {
            // The runtime and the clock load first, as a script of their own, so a condition's function reads them from the global
            // scope; the JavaScript expressions keep their form.
            foreach (var condition in conditions)
            {
                if (!compiled.Add(condition.Id))
                    continue;
                // A condition or set that reads current_actor() or in_state calls helpers only mqAt(now, actor) has (ExprRuntime.Clock).
                code.Append("maquettiste.helper(").Append(JsonSerializer.Serialize(condition.Id)).Append(", ")
                    .Append(condition.EngineOnly ? "(row, now, actor) => ((mq) => " + condition.JavaScript + ")(mqAt(now, actor))(row)"
                        : condition.ReadsNow ? "(row, now) => ((mq) => " + condition.JavaScript + ")(mqAt(now))(row)"
                        : condition.JavaScript).Append(");\n");
            }
        }

        Compiled = compiled;
        Conditions = conditions.Where(c => c.Set is null).Select(c => c.Id).Where(compiled.Contains).ToHashSet(StringComparer.Ordinal);
        Lowered = conditions.Where(c => compiled.Contains(c.Id)).ToDictionary(c => c.Id, StringComparer.Ordinal);
        Script = compiled.Count == 0 ? "" : code.ToString();
        var own = compiled.Count == 0 ? [] : PreparedScript.PrepareAll([new ScriptSource("process:" + process.Name + ".js", Script, "")], Limits);
        Scripts = conditions.Count > 0 ? [ExprRuntime.Prepared, .. own] : own;
    }

    /// <summary>The sandbox limits of an expression: 50 ms and 100,000 statements; the other limits are the sandbox defaults.</summary>
    public static SandboxLimits Limits { get; } = new() { ScriptTimeoutMs = DeadlineMs, ScriptStatements = StatementLimit };

    /// <summary>
    /// The limits of an expression in a project: <see cref="Limits"/>, without the 50 ms deadline when the project turns the scripts'
    /// wall clock off (<c>limits.scriptTimeoutMs: 0</c>, next-phase-1-design.md section 5.7), so that validation, verification and
    /// generation do not depend on the machine's load there either. The statement limit then bounds every expression alone.
    /// </summary>
    /// <param name="project">The project's limits.</param>
    /// <returns>The expression limits.</returns>
    internal static SandboxLimits LimitsIn(SandboxLimits project) => project.ScriptTimeoutMs <= 0 ? Limits with { ScriptTimeoutMs = 0 } : Limits;

    /// <summary>The expressions that do not parse, in guard then action order.</summary>
    public IReadOnlyList<ExpressionProblem> Problems { get; }

    /// <summary>The ids of the guards and actions whose expressions, or conditions, compiled.</summary>
    public IReadOnlySet<string> Compiled { get; }

    /// <summary>The ids of the guards whose conditions compiled: each answers only true or false, a null operand failing it.</summary>
    public IReadOnlySet<string> Conditions { get; }

    /// <summary>The guards' conditions and the actions' sets that compiled, by guard or set id, with what each reads.</summary>
    internal IReadOnlyDictionary<string, LoweredCondition> Lowered { get; }

    /// <summary>
    /// The text of the process's own script (empty when nothing compiled), for tests: the registrations of its expressions and
    /// conditions, without the runtime.
    /// </summary>
    internal string Script { get; }

    /// <summary>
    /// The row a condition that reads the object sees under <c>subject</c> for each attribute the object's row lacks: the defaults of the
    /// attributes in scope by name (lifecycle-design.md decision 22). <see langword="null"/> when no condition reads the object.
    /// </summary>
    internal JsonElement? SubjectDefaults { get; }

    /// <summary>
    /// The scripts a session's engines load, in order (for tests): with conditions, the shared runtime (<see cref="ExprRuntime.Prepared"/>)
    /// and then the process's own; else the process's own, or none when nothing compiled.
    /// </summary>
    internal IReadOnlyList<PreparedScript> Scripts { get; }

    /// <summary>
    /// The compiled expressions of a process (held by the process object, and shared by the expressions' content). A guard's condition
    /// needs its model to compile: use <see cref="Get(ModelSnapshot, Process)"/>; here such a guard is a stub.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <returns>The compiled expressions.</returns>
    public static ProcessExpressions Get(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return ByProcess.GetValue(process, static p => FromContent(p, [], null));
    }

    /// <summary>
    /// The compiled expressions of a process of a model, its guards' conditions included: each is typed in the process's scope and
    /// lowered as <see cref="RGuard.Condition"/> is, and one that does not type stays a stub (validation says why). A process
    /// without conditions is <see cref="Get(Process)"/>.
    /// </summary>
    /// <param name="model">The model that holds the process.</param>
    /// <param name="process">The process.</param>
    /// <returns>The compiled expressions.</returns>
    public static ProcessExpressions Get(ModelSnapshot model, Process process)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        if (process.Guards.All(g => g.Condition is null) && !ExprSites.SetsData(process) && !HasRightConditions(model, process) && !ExprSites.TriggerData(process)
            && !ExprSites.ApprovalData(process))
            return Get(process);
        return ByModel.GetOrCreateValue(model).GetValue(process, p =>
        {
            var lowered = Lower(model, p);
            return FromContent(p, lowered, SubjectDefaultsOf(model, p, lowered));
        });
    }

    private static readonly ConditionalWeakTable<ModelSnapshot, ConditionalWeakTable<TriggerPoint, ProcessExpressions>> ByNamedPoint = new();

    /// <summary>
    /// The compiled expressions of a named trigger point of a model (trigger-points-design.md decision 33, part 2's simulation): its
    /// <c>when</c> under <see cref="ExprSites.WhenKey"/>, a check's condition under its id and each value it sets under the set's id, typed in
    /// the point's own scope (what every entity it covers holds) and lowered as a lifecycle's trigger point's are. One that does not type stays
    /// out (validation says why). Kept per model and point, so a run keys its session by the point, not by a lifecycle.
    /// </summary>
    /// <param name="model">The model that holds the point.</param>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The compiled expressions; none when the point has no expression and no set.</returns>
    internal static ProcessExpressions Get(ModelSnapshot model, TriggerPoint point)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(point);
        return ByNamedPoint.GetOrCreateValue(model).GetValue(point, p =>
        {
            // A process of its own, with no guards and no actions, holds the point's lowered conditions; its name keeps it apart in the cache.
            var holder = new Process { Id = p.Id, Name = "trigger-point:" + p.Name };
            var path = model.GetDocument(p.Id)?.Path;
            var lowered = new List<LoweredCondition>();
            foreach (var (key, tree) in new[] { (ExprSites.WhenKey(p.Id), p.When), (p.Id, p.Condition) })
            {
                if (tree is null || ExprResolution.Type(model, p, key) is not { } typed)
                    continue;
                var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.Trigger, ExprResolution.ApiColumns(model, p.Id, path));
                lowered.Add(NamedLowered(key, js, typed.Typed, ExprSites.SubjectPathDepth(typed.Typed) is not null, null));
            }

            IReadOnlyList<ModelAttribute>? members = null;
            foreach (var set in p.Sets)
            {
                members ??= ExprSites.NamedTriggerMembers(model, p).Attributes;
                if (members.FirstOrDefault(a => string.Equals(a.Id, set.Attribute, StringComparison.Ordinal)) is not { } attribute
                    || ExprResolution.Type(model, p, set.Id) is not { } typed)
                    continue;
                var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.TriggerSet, ExprResolution.ApiColumns(model, set.Id, path));
                lowered.Add(NamedLowered(set.Id, js, typed.Typed, true, new LoweredSet(set.Attribute, attribute.Name, typed.Typed.Type.Kind)));
            }

            JsonElement? defaults = null;
            if (lowered.Any(c => c.ReadsSubject))
            {
                var map = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var attribute in members ?? ExprSites.NamedTriggerMembers(model, p).Attributes)
                {
                    if (!map.ContainsKey(attribute.Name))
                        map[attribute.Name] = attribute.Default is { ValueKind: not JsonValueKind.Undefined } value ? value : NullValue;
                }

                defaults = JsonSerializer.SerializeToElement(map);
            }

            return lowered.Count == 0 ? FromContent(holder, [], null) : FromContent(holder, lowered, defaults);
        });
    }

    private static LoweredCondition NamedLowered(string id, string js, TypedExpr typed, bool readsSubject, LoweredSet? set) =>
        new(id, js, ReadsNowOf(typed), readsSubject, set)
        {
            EngineOnly = ExprLowering.IsEngineOnly(typed),
            ActorKinds = [.. ActorKindsOf(typed).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ReadsRunning = ReadsRunningOf(typed),
            StateReads = ObjectStates.ReadsOf(typed),
        };

    private static bool ReadsNowOf(TypedExpr t) => t.Node.Now is not null || (t.Body is { } body && ReadsNowOf(body)) || t.Args.Any(ReadsNowOf);

    private static bool ReadsRunningOf(TypedExpr t) =>
        (t.Node.InState is not null && t.Running) || (t.Body is { } body && ReadsRunningOf(body)) || t.Args.Any(ReadsRunningOf);

    private static IEnumerable<string> ActorKindsOf(TypedExpr t) =>
        (t.Node.CurrentActor is not null ? [t.Type.Kind] : Enumerable.Empty<string>()).Concat(t.Body is { } body ? ActorKindsOf(body) : []).Concat(t.Args.SelectMany(ActorKindsOf));

    private static ProcessExpressions FromContent(Process process, IReadOnlyList<LoweredCondition> conditions, JsonElement? subjectDefaults)
    {
        var key = Key(process, conditions, subjectDefaults);
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        if (Cache.Count >= CacheLimit)
            Cache.Clear();
        return Cache.GetOrAdd(key, _ => new ProcessExpressions(process, conditions, subjectDefaults));
    }

    // The values a condition that reads the object sees for an attribute the object's row lacks (lifecycle-design.md decision 22): each
    // attribute in scope under subject (ExprSites.SubjectMembers) by name, the nearest declaration of a name first, as its default, else
    // null. Null when no condition reads the object, so the row of such a process is what it was before objects.
    private static JsonElement? SubjectDefaultsOf(ModelSnapshot model, Process process, IReadOnlyList<LoweredCondition> conditions)
    {
        if (!conditions.Any(c => c.ReadsSubject))
            return null;
        var defaults = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        // A stereotype's attribute joins only when a condition reads one (stereotype-sets-design.md section 6), so the defaults stay as
        // they were otherwise.
        var stereotypeReads = ExprSites.StereotypeAttributesRead(model, process);
        foreach (var attribute in ExprSites.SubjectMembers(model, process).Attributes)
        {
            if (!stereotypeReads.Contains(attribute.Id) && ExprSites.IsStereotypeAttribute(model, attribute.Id))
                continue;
            if (!defaults.ContainsKey(attribute.Name))
                defaults[attribute.Name] = attribute.Default is { ValueKind: not JsonValueKind.Undefined } value ? value : NullValue;
        }

        return JsonSerializer.SerializeToElement(defaults);
    }

    private static readonly JsonElement NullValue = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>The conditions of a process's guards that type, lowered at a guard site through the API's form of values.</summary>
    private static List<LoweredCondition> Lower(ModelSnapshot model, Process process)
    {
        var path = model.GetDocument(process.Id)?.Path;
        var lowered = new List<LoweredCondition>();
        foreach (var guard in process.Guards)
        {
            if (guard.Condition is null || ExprResolution.Type(model, process, guard.Id) is not { } typed)
                continue;
            var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.Guard, ExprResolution.ApiColumns(model, guard.Id, path));
            lowered.Add(Of(guard.Id, js, typed.Typed, ExprSites.ReadsSubject(process, typed.Typed), null));
        }

        // Each action's sets (object-effects-design.md section 7), lowered at a set site: each writes into the object's row, so each counts
        // as a read of the object.
        Dictionary<string, ModelAttribute>? held = null;
        foreach (var set in process.Actions.SelectMany(a => a.Sets))
        {
            held ??= ExprSites.HeldAttributes(model, process);
            if (!held.TryGetValue(set.Attribute, out var attribute) || ExprResolution.Type(model, process, set.Id) is not { } typed)
                continue;
            var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.Set, ExprResolution.ApiColumns(model, set.Id, path));
            lowered.Add(Of(set.Id, js, typed.Typed, true, new LoweredSet(set.Attribute, attribute.Name, typed.Typed.Type.Kind)));
        }

        // A lifecycle's rights' conditions (rights-design.md decision 6), lowered at a right site under the entry's id: an inline right's, and a
        // named right's typed at this use against the lifecycle's subjects. Only true holds, as at a guard.
        if (process.Use == ProcessUse.Lifecycle)
        {
            foreach (var entry in process.Rights)
            {
                if (RightCondition(model, entry) is null || ExprResolution.Type(model, process, entry.Id) is not { } typed)
                    continue;
                var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.Right, ExprResolution.ApiColumns(model, entry.Id, path));
                lowered.Add(Of(entry.Id, js, typed.Typed, ExprSites.ReadsSubject(process, typed.Typed), null));
            }

            // Its trigger points (trigger-points-design.md decision 15): a when under ExprSites.WhenKey, a check's condition under the
            // point's id, each at a trigger site, and each value a point sets at a trigger set site under the set's id. They read the values
            // before the verb under old, which EvaluateCondition and EvaluateSet take.
            foreach (var point in process.TriggerPoints)
            {
                foreach (var (key, tree) in new[] { (ExprSites.WhenKey(point.Id), point.When), (point.Id, point.Condition) })
                {
                    if (tree is null || ExprResolution.Type(model, process, key) is not { } typed)
                        continue;
                    var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.Trigger, ExprResolution.ApiColumns(model, point.Id, path));
                    lowered.Add(Of(key, js, typed.Typed, ExprSites.ReadsSubject(process, typed.Typed), null));
                }

                IReadOnlyList<ModelAttribute>? members = null;
                foreach (var set in point.Sets)
                {
                    members ??= ExprSites.TriggerMembers(model, process, point, withBound: true).Attributes;
                    if (members.FirstOrDefault(a => string.Equals(a.Id, set.Attribute, StringComparison.Ordinal)) is not { } attribute
                        || ExprResolution.Type(model, process, set.Id) is not { } typed)
                        continue;
                    var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.TriggerSet, ExprResolution.ApiColumns(model, set.Id, path));
                    lowered.Add(Of(set.Id, js, typed.Typed, true, new LoweredSet(set.Attribute, attribute.Name, typed.Typed.Type.Kind)));
                }
            }
        }

        // Each named signature's when of an approval on a transition (signoff-design.md decision 7), and each stage's (decision 6), lowered at
        // an approval site under the signature's or the stage's id: true or unknown means needed, or run, so only false answers false
        // (EvaluateApproval).
        foreach (var transition in process.Use == ProcessUse.Library ? [] : process.Transitions)
        {
            if (transition.Gate is not { UsesStages: true } gate)
                continue;
            foreach (var id in ExprSites.ApprovalConditionIds(gate))
            {
                if (ExprResolution.Type(model, process, id) is not { } typed)
                    continue;
                var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.Approval, ExprResolution.ApiColumns(model, id, path));
                lowered.Add(Of(id, js, typed.Typed, ExprSites.ReadsSubject(process, typed.Typed), null));
            }
        }

        return lowered;

        static LoweredCondition Of(string id, string js, TypedExpr typed, bool readsSubject, LoweredSet? set) =>
            new(id, js, ReadsNow(typed), readsSubject, set)
            {
                EngineOnly = ExprLowering.IsEngineOnly(typed),
                ActorKinds = [.. ActorKinds(typed).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                ReadsRunning = ReadsRunning(typed),
                StateReads = ObjectStates.ReadsOf(typed),
            };

        static bool ReadsNow(TypedExpr t) => t.Node.Now is not null || (t.Body is { } body && ReadsNow(body)) || t.Args.Any(ReadsNow);

        static bool ReadsRunning(TypedExpr t) => (t.Node.InState is not null && t.Running) || (t.Body is { } body && ReadsRunning(body)) || t.Args.Any(ReadsRunning);

        static IEnumerable<string> ActorKinds(TypedExpr t) =>
            (t.Node.CurrentActor is not null ? [t.Type.Kind] : Enumerable.Empty<string>()).Concat(t.Body is { } body ? ActorKinds(body) : []).Concat(t.Args.SelectMany(ActorKinds));
    }

    /// <summary>The condition of an entry of a lifecycle's rights: an inline right's own, or the named right's it uses; <see langword="null"/> for none.</summary>
    /// <param name="model">The model.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>The condition.</returns>
    internal static Expr? RightCondition(ModelSnapshot model, ProcessRight entry) =>
        entry.Right is { } named ? model.Get<Right>(named)?.Condition : entry.Condition;

    // Whether a lifecycle states a right with a condition, which compiles with its guards' conditions (rights-design.md section 7).
    private static bool HasRightConditions(ModelSnapshot model, Process process) =>
        process.Use == ProcessUse.Lifecycle && process.Rights.Count > 0 && process.Rights.Any(e => RightCondition(model, e) is not null);

    /// <summary>
    /// The expressions of a process that do not parse (MQ9501), in guard then action order: the <see cref="Problems"/> of
    /// <see cref="Get(Process)"/>, found by parsing alone. Validation reads these for every process, so it neither compiles the expressions
    /// nor goes through the shared cache: a model with more processes than the cache holds would clear it on every run, and with
    /// every validation thread contending for it that cost more than the rest of the process rules together.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <returns>The problems.</returns>
    internal static IReadOnlyList<ExpressionProblem> ProblemsOf(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Collect(process, new HashSet<string>(StringComparer.Ordinal), null, null);
    }

    /// <summary>Whether <see cref="Get(Process)"/> holds compiled expressions for the process's current expressions (for tests).</summary>
    /// <param name="process">The process.</param>
    /// <returns><see langword="true"/> when they are cached.</returns>
    internal static bool IsCached(Process process) => ByProcess.TryGetValue(process, out _) || Cache.ContainsKey(Key(process, [], null));

    /// <summary>
    /// Whether <see cref="Get(ModelSnapshot, Process)"/> holds compiled expressions for the process's current expressions and lowered
    /// conditions in the model (for tests): a rename that changes a condition's JavaScript gives a new key.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns><see langword="true"/> when they are cached.</returns>
    internal static bool IsCached(ModelSnapshot model, Process process) =>
        process.Guards.All(g => g.Condition is null) && !ExprSites.SetsData(process) && !HasRightConditions(model, process) && !ExprSites.TriggerData(process)
            && !ExprSites.ApprovalData(process)
            ? IsCached(process)
            : (ByModel.TryGetValue(model, out var processes) && processes.TryGetValue(process, out _)) || IsCachedByContent(model, process);

    private static bool IsCachedByContent(ModelSnapshot model, Process process)
    {
        var lowered = Lower(model, process);
        return Cache.ContainsKey(Key(process, lowered, SubjectDefaultsOf(model, process, lowered)));
    }

    // The problems of a process's expressions; the ids that parse go to compiled and, with code, their helper registrations to code. A
    // guard whose condition replaces its expression is still parsed, so the problems are the same with or without the model.
    private static List<ExpressionProblem> Collect(Process process, HashSet<string> compiled, StringBuilder? code, IReadOnlySet<string>? replaced)
    {
        var problems = new List<ExpressionProblem>();
        for (var i = 0; i < process.Guards.Count; i++)
            Add(process.Guards[i].Id, process.Guards[i].Name, "guard", process.Guards[i].Expression, "/guards/" + N(i) + "/expression");
        for (var i = 0; i < process.Actions.Count; i++)
            Add(process.Actions[i].Id, process.Actions[i].Name, "action", process.Actions[i].Expression, "/actions/" + N(i) + "/expression");
        return problems;

        void Add(string id, string name, string kind, string? expression, string pointer)
        {
            if (expression is null || compiled.Contains(id))
                return;
            if (Parse(expression) is { } problem)
            {
                problems.Add(new ExpressionProblem(id, pointer, name, kind, problem));
                return;
            }

            if (replaced?.Contains(id) ?? false)
                return;
            compiled.Add(id);
            code?.Append("maquettiste.helper(").Append(JsonSerializer.Serialize(id)).Append(", ").Append(Wrap(expression)).Append(");\n");
        }
    }

    /// <summary>Returns why an expression does not parse as one expression, or <see langword="null"/>; nothing runs.</summary>
    /// <param name="expression">The expression text.</param>
    /// <returns>The problem.</returns>
    public static string? Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        try
        {
            var prepared = JsEngine.PrepareScript(Wrap(expression), "expression", strict: true);
            var body = prepared.Program!.Body;
            return body.Count == 1 && body[0] is ExpressionStatement { Expression: ArrowFunctionExpression }
                ? null
                : "it is not a single expression (a closing parenthesis or a statement separator leaves the expression)";
        }
        catch (ScriptPreparationException ex)
        {
            return ex.InnerException is Acornima.ParseErrorException parse
                ? parse.Description + " at column " + (Math.Max(0, parse.Column) + 1).ToString(CultureInfo.InvariantCulture) + " of line " + Math.Max(1, parse.LineNumber - 1).ToString(CultureInfo.InvariantCulture)
                : ex.Message;
        }
    }

    /// <summary>
    /// Opens a session: a sandbox pool over the compiled expressions, bound to <paramref name="ct"/>, built on the session's first
    /// evaluation, so a failure to load is that evaluation's MQ9502 or MQ9503.
    /// </summary>
    /// <param name="size">The most sandboxes kept idle.</param>
    /// <param name="ct">The run's cancellation.</param>
    /// <returns>The session; dispose it when done.</returns>
    public ProcessExpressionSession Open(int size, CancellationToken ct) => Open(size, ct, Limits);

    /// <summary>
    /// Opens a session with other sandbox limits: a project's (<see cref="LimitsIn"/>), or a test's that reaches one limit before the
    /// other. A process with conditions runs without the wall clock, as <see cref="ExprEvaluator.Limits"/> do: loading the expression
    /// runtime and its exact arithmetic take a time that depends on the machine's load, and a guard's answer, so a replay, must not;
    /// the statement limit still bounds every call, its JavaScript expressions' too.
    /// </summary>
    /// <param name="size">The most sandboxes kept idle.</param>
    /// <param name="ct">The run's cancellation.</param>
    /// <param name="limits">The limits of every expression run in the session.</param>
    /// <returns>The session; dispose it when done.</returns>
    public ProcessExpressionSession Open(int size, CancellationToken ct, SandboxLimits limits)
    {
        if (Scripts.Count == 0)
            return new ProcessExpressionSession(null, Compiled, Conditions) { SubjectDefaults = SubjectDefaults, Lowered = Lowered };
        var scripts = Scripts;
        var effective = Lowered.Count > 0 ? limits with { ScriptTimeoutMs = 0 } : limits;
        var first = Compiled.First();
        return new ProcessExpressionSession(new LazySandboxPool(() =>
        {
            var pool = new ScriptSandboxPool(scripts, effective, Math.Max(1, size), ct);
            try
            {
                WarmUp(pool, first, ct);
                return pool;
            }
            catch
            {
                pool.Dispose();
                throw;
            }
        }), Compiled, Conditions) { SubjectDefaults = SubjectDefaults, Lowered = Lowered };
    }

    // The first call into a fresh engine pays the runtime's one-time costs (code paths compiled on first use), which
    // on a cold or slow host can exceed the per-expression deadline by itself. One throwaway evaluation of the first
    // expression, with empty inputs and its outcome ignored, takes that cost before any real evaluation is timed.
    private static void WarmUp(IScriptSandboxPool pool, string id, CancellationToken ct)
    {
        using var empty = JsonDocument.Parse("{}");
        using var lease = pool.Rent();
        try
        {
            lease.Sandbox.CallHelper(id, [empty.RootElement, empty.RootElement], new ScriptCallContext(null, "warm-up", ImmutableDictionary<string, object?>.Empty, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A throw or a limit on empty inputs is expected for many expressions; only the warm-up matters.
        }
    }

    private static string Wrap(string expression) => "(context, event) => (\n" + expression + "\n)";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    // The content key: a condition's lowered JavaScript names what it reads by current name and holds each enum member's or code's
    // literal, so a rename that changes what it reads changes the key.
    private static string Key(Process process, IReadOnlyList<LoweredCondition> conditions, JsonElement? subjectDefaults)
    {
        var text = new StringBuilder(process.Name).Append('\0');
        foreach (var g in process.Guards)
            text.Append('g').Append(g.Id).Append('\0').Append(g.Expression ?? "\u0001").Append('\0');
        foreach (var a in process.Actions)
            text.Append('a').Append(a.Id).Append('\0').Append(a.Expression ?? "\u0001").Append('\0');
        foreach (var c in conditions)
        {
            text.Append('c').Append(c.Id).Append('\0').Append(c.JavaScript).Append('\0');
            if (c.Set is { } set) // the attribute a set writes, by name and type, is part of what it does
                text.Append('s').Append(set.Attribute).Append('\0').Append(set.Name).Append('\0').Append(set.Kind).Append('\0');
            foreach (var read in c.StateReads) // the lifecycles it reads on an object, which its JavaScript names by id
                text.Append('r').Append(read.Key).Append('\0');
        }
        if (subjectDefaults is { } defaults) // the defaults a condition reading the object sees are part of what it answers
            text.Append('d').Append(defaults.GetRawText()).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>A guard's condition lowered to JavaScript.</summary>
    /// <param name="Id">The guard id.</param>
    /// <param name="JavaScript">The function over a row (<see cref="ExprLowering.ToJavaScript"/>).</param>
    /// <param name="ReadsNow">Whether it reads <c>now</c>, so it receives the simulation's clock.</param>
    /// <param name="ReadsSubject">Whether it reads the object under <c>subject</c> (<see cref="ExprSites.ReadsSubject"/>); every set does.</param>
    /// <param name="Set">For an action's set, the attribute it writes; <see langword="null"/> for a guard's condition.</param>
    internal sealed record LoweredCondition(string Id, string JavaScript, bool ReadsNow, bool ReadsSubject, LoweredSet? Set)
    {
        /// <summary>Whether it reads <c>current_actor()</c> or <c>in_state</c> (<see cref="ExprLowering.IsEngineOnly"/>), so it runs with <c>mqAt(now, actor)</c>.</summary>
        public bool EngineOnly { get; init; }

        /// <summary>The types the <c>current_actor()</c> nodes it holds met, each once, in order: the identity is read through each.</summary>
        public IReadOnlyList<string> ActorKinds { get; init; } = [];

        /// <summary>Whether an <c>in_state</c> of it reads the running chart, so its row holds the active states.</summary>
        public bool ReadsRunning { get; init; }

        /// <summary>The lifecycles an <c>in_state</c> of it reads on an object.</summary>
        public IReadOnlyList<StateRead> StateReads { get; init; } = [];
    }

    /// <summary>The attribute an action's set writes.</summary>
    /// <param name="Attribute">The attribute's id as the set names it.</param>
    /// <param name="Name">The attribute's name, its member in the object's row.</param>
    /// <param name="Kind">The value's type kind (<see cref="ExprType.Kind"/>), which says how its result is written.</param>
    internal sealed record LoweredSet(string Attribute, string Name, string Kind);
}

/// <summary>The result of one expression evaluation.</summary>
/// <param name="Value">The result as JSON-like data (a bool, number, string, null, map or list); <see langword="null"/> on failure.</param>
/// <param name="Diagnostic">MQ9502 (the expression threw) or MQ9503 (a limit), or <see langword="null"/>.</param>
public sealed record ExpressionResult(object? Value, Diagnostic? Diagnostic);

/// <summary>
/// A sandbox pool over one process's compiled expressions (phase-3-design.md section 4.2). <c>context</c> and <c>event</c> cross as
/// frozen JSON-like data; <c>Math.random</c> is seeded per call. Thread-safe: each evaluation rents a lease.
/// </summary>
public sealed class ProcessExpressionSession : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = ImmutableDictionary<string, object?>.Empty;
    private readonly LazySandboxPool? _pool;

    internal ProcessExpressionSession(LazySandboxPool? pool, IReadOnlySet<string> compiled, IReadOnlySet<string> conditions)
    {
        _pool = pool;
        Compiled = compiled;
        Conditions = conditions;
    }

    /// <summary>The ids of the guards and actions whose expressions, or conditions, compiled (<see cref="ProcessExpressions.Compiled"/>).</summary>
    public IReadOnlySet<string> Compiled { get; }

    /// <summary>The ids of the guards whose conditions compiled (<see cref="ProcessExpressions.Conditions"/>): evaluate them with
    /// <see cref="EvaluateCondition(string, string, JsonElement, JsonElement, JsonElement, string, string, CancellationToken)"/>.</summary>
    public IReadOnlySet<string> Conditions { get; }

    /// <summary>The defaults the object's row falls back on (<see cref="ProcessExpressions.SubjectDefaults"/>); <see langword="null"/> when no
    /// condition reads the object.</summary>
    internal JsonElement? SubjectDefaults { get; init; }

    /// <summary>The guards' conditions and the actions' sets that compiled, by id (<see cref="ProcessExpressions.Lowered"/>).</summary>
    internal IReadOnlyDictionary<string, ProcessExpressions.LoweredCondition> Lowered { get; init; } =
        ImmutableDictionary<string, ProcessExpressions.LoweredCondition>.Empty;

    /// <summary>
    /// Evaluates a compiled guard condition over its row (<see cref="EvaluateCondition(string, string, JsonElement, JsonElement, JsonElement, string, string, CancellationToken)"/>),
    /// with what <c>current_actor()</c> and <c>in_state</c> read (object-effects-design.md section 7): the running chart's active states by process
    /// id, held at the row's top under <c>$states</c>, and the acting identity as text.
    /// </summary>
    /// <param name="id">The guard id.</param>
    /// <param name="label">"Guard 'name'", for messages.</param>
    /// <param name="context">The context object.</param>
    /// <param name="event">The event object.</param>
    /// <param name="subject">The object.</param>
    /// <param name="running">The running chart's states (a JSON object of process id to state ids), or <see langword="null"/> when not read.</param>
    /// <param name="now">The simulation's clock.</param>
    /// <param name="actor">The acting identity, or <see langword="null"/>.</param>
    /// <param name="seed">The seed of <c>Math.random</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="old">
    /// At a trigger point's site, the object's values before the verb (trigger-points-design.md decision 14), which <c>old.</c> reads: a JSON
    /// object by attribute name, or JSON null on <c>create</c>. <see langword="null"/> elsewhere, and the row then holds no <c>old</c>.
    /// </param>
    /// <returns>The result: a <see cref="bool"/>, or MQ9502 or MQ9503.</returns>
    internal ExpressionResult EvaluateCondition(string id, string label, JsonElement context, JsonElement @event, JsonElement subject, JsonElement? running,
        string now, string? actor, string seed, CancellationToken ct, JsonElement? old = null)
    {
        if (_pool is null)
            throw new InvalidOperationException($"{label} has no compiled condition.");
        var row = Row(context, @event, subject, SubjectDefaults, running, old);
        try
        {
            using var lease = _pool.Rent();
            var value = lease.Sandbox.CallHelper(id, Args(id, row, now, actor), new ScriptCallContext(null, seed, NoParameters, ct));
            return new ExpressionResult(value is true, null);
        }
        catch (ScriptLimitException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9503", $"{label} stopped: {Detail(ex.Diagnostic.Message)} Simplify the condition.", id, null, null));
        }
        catch (ScriptErrorException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9502", $"{label} threw: {Detail(ex.Diagnostic.Message)}", id, null, null));
        }
    }

    /// <summary>
    /// Works out the value an action's set gives its attribute (object-effects-design.md decisions 3 and 4) over the row a guard reads: the
    /// value as JSON, a number for a numeric attribute, else the API's form of the value.
    /// </summary>
    /// <param name="id">The set id.</param>
    /// <param name="label">"Action 'name'", for messages.</param>
    /// <param name="context">The context object as the action started.</param>
    /// <param name="event">The event object.</param>
    /// <param name="subject">The object as the action started.</param>
    /// <param name="running">The running chart's states, or <see langword="null"/> when not read.</param>
    /// <param name="now">The simulation's clock.</param>
    /// <param name="actor">The acting identity, or <see langword="null"/>.</param>
    /// <param name="seed">The seed of <c>Math.random</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="old">At a trigger point's set, the object's values before the verb, as <see cref="EvaluateCondition(string, string, JsonElement, JsonElement, JsonElement, JsonElement?, string, string?, string, CancellationToken, JsonElement?)"/> takes them.</param>
    /// <returns>The result: a <see cref="JsonElement"/>, or MQ9502 or MQ9503.</returns>
    internal ExpressionResult EvaluateSet(string id, string label, JsonElement context, JsonElement @event, JsonElement subject, JsonElement? running,
        string now, string? actor, string seed, CancellationToken ct, JsonElement? old = null)
    {
        if (_pool is null || !Lowered.TryGetValue(id, out var lowered) || lowered.Set is not { } set)
            throw new InvalidOperationException($"{label} has no compiled set '{id}'.");
        var row = Row(context, @event, subject, SubjectDefaults, running, old);
        try
        {
            using var lease = _pool.Rent();
            var value = lease.Sandbox.CallHelper(id, Args(id, row, now, actor), new ScriptCallContext(null, seed, NoParameters, ct));
            return new ExpressionResult(SetValue(value, set.Kind), null);
        }
        catch (ScriptLimitException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9503", $"{label} stopped working out '{set.Name}': {Detail(ex.Diagnostic.Message)} Simplify the value.", id, null, null));
        }
        catch (ScriptErrorException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9502", $"{label} threw working out '{set.Name}': {Detail(ex.Diagnostic.Message)}", id, null, null));
        }
    }

    /// <summary>
    /// Judges whether a named signature is needed when its round starts (signoff-design.md decision 7): its <c>when</c>, compiled under the
    /// signature's id when it types (<see cref="Conditions"/> holds the id), over a row of the context and the object, with no payload and
    /// nobody acting. Only false means not needed: the answer is <see langword="true"/> when the condition holds or is unknown. A signature
    /// whose <c>when</c> did not compile is needed, the safe choice; the caller checks <see cref="Conditions"/> first. A stage's <c>when</c> is
    /// judged the same way under the stage's id (decision 6): only false skips the stage.
    /// </summary>
    /// <param name="signatureId">The named signature's id, or a stage's.</param>
    /// <param name="label">"Signature 'name'" or "Stage 'name'", for messages.</param>
    /// <param name="context">The context object.</param>
    /// <param name="subject">The object, after the entering move's actions and sets; an empty object when there is none.</param>
    /// <param name="running">The running chart's states (a JSON object of process id to state ids), or <see langword="null"/> when not read.</param>
    /// <param name="now">The simulation's clock.</param>
    /// <param name="seed">The seed of <c>Math.random</c>, which no condition reads.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result: a <see cref="bool"/>, whether the signature is needed, or MQ9502 or MQ9503.</returns>
    internal ExpressionResult EvaluateApproval(string signatureId, string label, JsonElement context, JsonElement subject, JsonElement? running, string now,
        string seed, CancellationToken ct) =>
        EvaluateCondition(signatureId, label, context, NoObject, subject, running, now, null, seed, ct);

    // The arguments a lowered function takes: the row and the clock, and the identity when it reads current_actor() or in_state.
    private object?[] Args(string id, JsonElement row, string now, string? actor) =>
        Lowered.TryGetValue(id, out var lowered) && lowered.EngineOnly ? [new ExactJson(row), now, actor] : [new ExactJson(row), now];

    // A set's result as the object holds it: an exact number for an integer or decimal (the runtime writes it as text), else as it came.
    private static JsonElement SetValue(object? value, string kind)
    {
        if (value is string text && kind is "int" or "decimal" && decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out _))
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Number)
                return document.RootElement.Clone();
        }

        return ToJson(value);
    }

    /// <summary>Evaluates a compiled guard or action expression.</summary>
    /// <param name="id">The guard or action id.</param>
    /// <param name="label">"Guard 'name'" or "Action 'name'", for messages.</param>
    /// <param name="context">The context object (attribute names to values).</param>
    /// <param name="event">The event object (<c>name</c>, <c>actor</c>, <c>payload</c>).</param>
    /// <param name="seed">The seed of <c>Math.random</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    public ExpressionResult Evaluate(string id, string label, JsonElement context, JsonElement @event, string seed, CancellationToken ct)
    {
        if (_pool is null)
            throw new InvalidOperationException($"{label} has no compiled expression.");
        try
        {
            // Renting may load an engine (the session's first, or one after a limit dropped the last): a failure there is this call's.
            using var lease = _pool.Rent();
            return new ExpressionResult(lease.Sandbox.CallHelper(id, [context, @event], new ScriptCallContext(null, seed, NoParameters, ct)), null);
        }
        catch (ScriptLimitException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9503", $"{label} stopped: {Detail(ex.Diagnostic.Message)} Simplify the expression, or move the work to a handler (leave the expression empty).", id, null, null));
        }
        catch (ScriptErrorException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9502", $"{label} threw: {Detail(ex.Diagnostic.Message)} Fix the expression, for example guard against missing values.", id, null, null));
        }
    }

    /// <summary>
    /// Evaluates a compiled guard condition (next-phase-3-design.md section 3.1, "Process guards") over one row: the context's
    /// attributes by name, and the event's payload under <c>event</c>, every number crossing as its exact text. The answer is
    /// <see langword="true"/> only when the condition holds; unknown, a null operand among the causes, is <see langword="false"/>.
    /// </summary>
    /// <param name="id">The guard id.</param>
    /// <param name="label">"Guard 'name'", for messages.</param>
    /// <param name="context">The context object (attribute names to values).</param>
    /// <param name="event">The event object (<c>name</c>, <c>actor</c>, <c>payload</c>).</param>
    /// <param name="now">The simulation's clock, an ISO 8601 instant in UTC, which <c>now()</c> reads.</param>
    /// <param name="seed">The seed of <c>Math.random</c>, which no condition reads.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result: a <see cref="bool"/>, or MQ9502 or MQ9503.</returns>
    public ExpressionResult EvaluateCondition(string id, string label, JsonElement context, JsonElement @event, string now, string seed, CancellationToken ct) =>
        EvaluateCondition(id, label, context, @event, NoObject, now, seed, ct);

    /// <summary>
    /// Evaluates a compiled guard condition over one row that also holds the object the run is for under <c>subject</c>
    /// (lifecycle-design.md decisions 20 and 22): its attributes by name and its links by navigation name, an attribute the object does not
    /// give reading as its default, then null; with no object (an empty one) every attribute reads so. A context attribute named
    /// <c>subject</c> keeps its meaning, and a process none of whose conditions reads the object sees the row it always did.
    /// </summary>
    /// <param name="id">The guard id.</param>
    /// <param name="label">"Guard 'name'", for messages.</param>
    /// <param name="context">The context object (attribute names to values).</param>
    /// <param name="event">The event object (<c>name</c>, <c>actor</c>, <c>payload</c>).</param>
    /// <param name="subject">The object (attribute and navigation names to values); an empty object when there is none.</param>
    /// <param name="now">The simulation's clock, an ISO 8601 instant in UTC, which <c>now()</c> reads.</param>
    /// <param name="seed">The seed of <c>Math.random</c>, which no condition reads.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result: a <see cref="bool"/>, or MQ9502 or MQ9503.</returns>
    public ExpressionResult EvaluateCondition(string id, string label, JsonElement context, JsonElement @event, JsonElement subject, string now, string seed,
        CancellationToken ct)
    {
        if (_pool is null)
            throw new InvalidOperationException($"{label} has no compiled condition.");
        var row = Row(context, @event, subject, SubjectDefaults, null);
        try
        {
            using var lease = _pool.Rent();
            var value = lease.Sandbox.CallHelper(id, [new ExactJson(row), now], new ScriptCallContext(null, seed, NoParameters, ct));
            return new ExpressionResult(value is true, null);
        }
        catch (ScriptLimitException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9503", $"{label} stopped: {Detail(ex.Diagnostic.Message)} Simplify the condition.", id, null, null));
        }
        catch (ScriptErrorException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9502", $"{label} threw: {Detail(ex.Diagnostic.Message)}", id, null, null));
        }
    }

    private static readonly JsonElement NoObject = JsonDocument.Parse("{}").RootElement.Clone();

    // The row a condition reads: the context's members, then the payload as event. A context attribute named event gives way to it; no
    // condition reads one (MQ3102 leaves such a guard unlowered). When a condition of the process reads the object (defaults given), the
    // object follows as subject, each attribute it lacks as its default; a context attribute named subject keeps its place instead.
    // With the running chart's states read by an in_state, they follow at the row's top under $states, a member no attribute can be named.
    // At a trigger point's site the values before the verb follow under old (trigger-points-design.md decision 14); a trigger point reads no
    // context, so a context attribute named old gives way to them.
    private static JsonElement Row(JsonElement context, JsonElement @event, JsonElement subject, JsonElement? defaults, JsonElement? running,
        JsonElement? old = null)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (context.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in context.EnumerateObject())
                {
                    if (!string.Equals(member.Name, "event", StringComparison.Ordinal)
                        && !(old is not null && string.Equals(member.Name, ExprSites.OldPrefix, StringComparison.Ordinal)))
                        member.WriteTo(writer);
                }
            }

            if (old is { } before)
            {
                writer.WritePropertyName(ExprSites.OldPrefix);
                if (before.ValueKind == JsonValueKind.Object)
                    before.WriteTo(writer);
                else
                    writer.WriteNullValue();
            }

            writer.WritePropertyName("event");
            if (@event.ValueKind == JsonValueKind.Object && @event.TryGetProperty("payload", out var payload))
                payload.WriteTo(writer);
            else
                writer.WriteNullValue();
            if (defaults is { ValueKind: JsonValueKind.Object } fallback
                && !(context.ValueKind == JsonValueKind.Object && context.TryGetProperty(ExprSites.SubjectPrefix, out _)))
            {
                writer.WritePropertyName(ExprSites.SubjectPrefix);
                writer.WriteStartObject();
                var given = new HashSet<string>(StringComparer.Ordinal);
                if (subject.ValueKind == JsonValueKind.Object)
                {
                    foreach (var member in subject.EnumerateObject())
                    {
                        if (given.Add(member.Name))
                            member.WriteTo(writer);
                    }
                }

                foreach (var member in fallback.EnumerateObject())
                {
                    if (!given.Contains(member.Name))
                        member.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            if (running is { ValueKind: JsonValueKind.Object } states)
            {
                writer.WritePropertyName(ObjectStates.Member);
                states.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Converts an evaluation result to JSON.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON value.</returns>
    public static JsonElement ToJson(object? value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, value);
        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    /// <inheritdoc/>
    public void Dispose() => _pool?.Dispose();

    private static string Detail(string message)
    {
        var text = message.Replace("Helper '", "'", StringComparison.Ordinal).Trim();
        return text.EndsWith('.') ? text : text + ".";
    }

    private static void Write(Utf8JsonWriter writer, object? value)
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
            case double d when double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 9e15:
                writer.WriteNumberValue((long)d);
                break;
            case double d when double.IsFinite(d):
                writer.WriteNumberValue(d);
                break;
            case double:
                writer.WriteNullValue();
                break;
            case int or long or decimal or float or short or byte or sbyte or ushort or uint:
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                break;
            case ulong u:
                writer.WriteNumberValue(u);
                break;
            case System.Numerics.BigInteger big:
                // Every digit, as JSON text: a tree expression reads it exactly (mq.dec).
                writer.WriteRawValue(big.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonElement json:
                json.WriteTo(writer);
                break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (k, v) in map)
                {
                    writer.WritePropertyName(k);
                    Write(writer, v);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable list:
                writer.WriteStartArray();
                foreach (var item in list)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
