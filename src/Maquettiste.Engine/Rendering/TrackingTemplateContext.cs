using System.Collections;
using System.Collections.Frozen;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Maquettiste.Engine.Rendering;

/// <summary>
/// The Scriban context of one unit (engine-design.md section 9). It overrides <see cref="GetMemberAccessorImpl"/> so that only the
/// template-visible types expose members and every member read of a resolved object records its dependencies into the unit's
/// <see cref="IReadRecorder"/>; lists of resolved objects arrive as <see cref="TrackedList"/>s that record their membership keys,
/// and maps as <see cref="MapView"/>s in ordinal key order. Includes resolve through the pack folder and the run's template cache
/// and record <c>t:&lt;pack&gt;/&lt;path&gt;</c>. One instance per unit, used by one thread.
/// </summary>
internal sealed class TrackingTemplateContext : TemplateContext
{
    private readonly UnitRun _unit;

    /// <summary>Creates the context of one unit.</summary>
    /// <param name="builtins">The run's shared, read-only builtin object.</param>
    /// <param name="unit">The unit being rendered.</param>
    public TrackingTemplateContext(ScriptObject builtins, UnitRun unit)
        : base(builtins)
    {
        _unit = unit;
        var limits = unit.Run.Limits;
        StrictVariables = true;
        EnableRelaxedMemberAccess = true;
        EnableRelaxedTargetAccess = false;
        EnableRelaxedFunctionAccess = false;
        LoopLimit = limits.TemplateLoopLimit;
        RecursiveLimit = limits.TemplateRecursionLimit;
        NewLine = "\n";
        MemberRenamer = static member => Text.Casing.Snake(member.Name);
        MemberFilter = static _ => false;
        // A regex match cannot be interrupted by the token, so it gets the scripts' bound (the script limit capped at 250 ms) to keep
        // cancellation inside its one-second budget (host-contracts 26); the regex functions retry a timeout once (a stalled
        // thread), and a second one fails the unit with MQ6007.
        RegexTimeOut = Scripting.ScriptSandbox.RegexTimeout(limits);
        LimitToString = MaxTextLength;
        OnStringLimit = ScriptLimitBehavior.Throw;
        OutputLimit = MaxTextLength;
        OnOutputLimit = ScriptLimitBehavior.Throw;
        TemplateLoader = new PackTemplateLoader(unit);
        CancellationToken = unit.CancellationToken;
    }

    /// <summary>The largest text one render may produce (64 MiB of characters); larger output fails with MQ6007.</summary>
    public const int MaxTextLength = 64 * 1024 * 1024;

    /// <summary>The unit being rendered.</summary>
    public UnitRun Unit => _unit;

    /// <summary>The unit's recorder.</summary>
    public UnitRecorder Recorder => _unit.Recorder;

    /// <summary>File blocks emitted so far (<c>file</c> helper), in emission order.</summary>
    public List<RenderedFile> Blocks { get; } = [];

    /// <summary>Casts a Scriban context to the renderer's.</summary>
    /// <param name="context">The context.</param>
    /// <returns>The tracking context.</returns>
    public static TrackingTemplateContext Of(TemplateContext context) =>
        context as TrackingTemplateContext ?? throw new InvalidOperationException("Maquettiste helpers need a TrackingTemplateContext.");

    /// <summary>Records the dependencies of a resolved object that a template reads.</summary>
    /// <param name="target">The object read.</param>
    public void RecordRead(object? target)
    {
        if (target is IResolvedObject resolved)
            Recorder.RecordObject(resolved);
    }

    /// <summary>
    /// Wraps a CLR value for templates: enums become their kebab JSON names, JSON elements plain values, <see cref="RList{T}"/>s
    /// <see cref="TrackedList"/>s, other lists <see cref="ValueList"/>s and string-keyed maps <see cref="MapView"/>s. Scriban's own
    /// values, strings, numbers and template-visible objects pass through.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The template value.</returns>
    public object? Wrap(object? value)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
            case char:
            case IScriptObject:
            case IScriptCustomFunction:
            case TrackedList:
            case ValueList:
            case MapView:
            case IResolvedObject:
            case ITemplateView:
                return value;
            case Enum e:
                return _unit.Run.Catalog.EnumName(e);
            case JsonElement json:
                return Wrap(TemplateValues.Plain(json));
        }

        var catalog = _unit.Run.Catalog;
        switch (catalog.Shape(value.GetType()))
        {
            case ValueShape.ResolvedList when catalog.MembershipKeys(value) is { } keys && value is IReadOnlyList<IResolvedObject> items:
                return new TrackedList(value, items, keys, Recorder);
            case ValueShape.Map when catalog.TryReadMap(value, out var entries):
                return new MapView(value, entries, this);
            case ValueShape.List:
                return new ValueList(value, value as IList ?? ((IEnumerable)value).Cast<object?>().ToArray(), this);
            default:
                return value;
        }
    }

    /// <summary>Removes the renderer's wrappers again (what scripts and <c>json</c> receive).</summary>
    /// <param name="value">A template value.</param>
    /// <returns>The underlying value.</returns>
    public static object? Unwrap(object? value) => value switch
    {
        TrackedList list => list.Source,
        ValueList list => list.Source,
        MapView map => map.Source,
        _ => value,
    };

    /// <summary>Renders a <see cref="MapView"/> the way Scriban renders its own objects (<c>{key: value, …}</c>, keys ordinal).</summary>
    /// <param name="value">The value.</param>
    /// <param name="nested">Whether the value is nested in another (strings are then quoted).</param>
    /// <returns>The text.</returns>
    public override string? ObjectToString(object? value, bool nested = false)
    {
        if (value is not MapView map)
            return base.ObjectToString(value, nested);
        var builder = new System.Text.StringBuilder("{");
        var first = true;
        foreach (var (key, item) in map)
        {
            if (!first)
                builder.Append(", ");
            first = false;
            builder.Append(SqlDialects.IsRegular(key) ? key : base.ObjectToString(key, true)).Append(": ").Append(ObjectToString(item, true));
        }

        return builder.Append('}').ToString();
    }

    /// <inheritdoc/>
    protected override IObjectAccessor? GetMemberAccessorImpl(object target)
    {
        switch (target)
        {
            case MapView:
                return MapAccessor.Instance;
            case KeyValuePair<string, object?>:
                return EntryAccessor.Instance;
            case IScriptObject:
            case string:
            case IList:
                return base.GetMemberAccessorImpl(target);
        }

        var type = target.GetType();
        if (type.IsPrimitive || target is decimal)
            return base.GetMemberAccessorImpl(target);
        return _unit.Run.Catalog.GetAccessor(type);
    }

    /// <inheritdoc/>
    protected override Template CreateTemplate(string templatePath, ScriptNode? callerContext)
    {
        Recorder.Record(_unit.TemplateKey(templatePath));
        var template = _unit.Run.Templates.Get(_unit.Planned.Pack, templatePath, _unit.Planned.Unit.Delimiters);
        _unit.Used(template);
        return template;
    }
}

/// <summary>
/// Resolves <c>include</c> names against the pack folder: a pack-relative path with <c>/</c> separators, no <c>..</c>, no rooted
/// path. The template cache reads the file and refuses anything that resolves outside the pack folder (symbolic links included).
/// </summary>
/// <param name="unit">The unit.</param>
internal sealed class PackTemplateLoader(UnitRun unit) : ITemplateLoader
{
    /// <inheritdoc/>
    public string? GetPath(TemplateContext context, SourceSpan callerSpan, string templateName)
    {
        if (!PackPaths.TryNormalize(templateName, out var path, out var error))
            throw new ScriptRuntimeException(callerSpan, $"Cannot include '{templateName}': {error}");
        return path;
    }

    /// <inheritdoc/>
    public string? Load(TemplateContext context, SourceSpan callerSpan, string templatePath) =>
        unit.Run.Templates.ReadText(unit.Planned.Pack, templatePath);

    /// <inheritdoc/>
    public ValueTask<string?> LoadAsync(TemplateContext context, SourceSpan callerSpan, string templatePath) =>
        ValueTask.FromResult(Load(context, callerSpan, templatePath));
}

/// <summary>
/// The unit's variables (engine-design.md section 8): read-only; reading <c>mapping</c>, <c>mappings</c> or <c>hints</c> records
/// the element's dependencies, since mapping and hint objects carry none of their own.
/// </summary>
/// <param name="recorder">Called when a tracked variable is read.</param>
internal sealed class UnitGlobals(Action recorder) : ScriptObject(0, false)
{
    /// <summary>Variables whose reads record the element's dependencies.</summary>
    public static readonly FrozenSet<string> ElementDerived = FrozenSet.Create(StringComparer.Ordinal, "mapping", "mappings", "hints");

    /// <inheritdoc/>
    public override bool TryGetValue(TemplateContext? context, SourceSpan span, string member, out object? value)
    {
        if (ElementDerived.Contains(member))
            recorder();
        return base.TryGetValue(context, span, member, out value);
    }
}
