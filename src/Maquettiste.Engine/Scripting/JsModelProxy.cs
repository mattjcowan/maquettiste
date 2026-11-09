using System.Collections;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Scripting;

/// <summary>
/// A read-only JavaScript view of a resolved-model object (or one of its helper objects, or the <see cref="ResolvedModel"/>)
/// (engine-design.md section 10). Properties appear in camelCase; <c>Set</c>, <c>DefineOwnProperty</c> and <c>Delete</c> refuse,
/// so a strict-mode write throws a <c>TypeError</c>. Every member read (and key enumeration) of an <see cref="IResolvedObject"/>
/// records its <see cref="IResolvedObject.Dependencies"/> into the current call's <c>IReadRecorder</c>, like the Scriban accessor.
/// </summary>
internal sealed class JsModelProxy : ObjectInstance
{
    private readonly ScriptValues _values;
    private readonly MemberTable _table;
    private readonly JsValue?[] _cache;

    /// <summary>Creates a proxy.</summary>
    /// <param name="values">The sandbox's value converter.</param>
    /// <param name="target">The CLR object.</param>
    /// <param name="table">The object's script-visible members.</param>
    internal JsModelProxy(ScriptValues values, object target, MemberTable table)
        : base(values.Engine)
    {
        _values = values;
        _table = table;
        _cache = new JsValue?[table.Members.Count];
        Target = target;
        Prototype = values.Engine.Intrinsics.Object.PrototypeObject;
        base.PreventExtensions();
    }

    /// <summary>The CLR object.</summary>
    public object Target { get; }

    /// <inheritdoc/>
    public override object ToObject() => Target;

    /// <inheritdoc/>
    public override JsValue Get(JsValue property, JsValue receiver) =>
        TryFind(property, out var index) ? Read(index) : base.Get(property, receiver);

    /// <inheritdoc/>
    public override PropertyDescriptor GetOwnProperty(JsValue property) =>
        TryFind(property, out var index) ? new PropertyDescriptor(Read(index), PropertyFlag.OnlyEnumerable) : PropertyDescriptor.Undefined;

    /// <inheritdoc/>
    protected override bool TryGetOwnPropertyValue(JsValue property, JsValue receiver, out JsValue value)
    {
        if (TryFind(property, out var index))
        {
            value = Read(index);
            return true;
        }

        value = Undefined;
        return false;
    }

    /// <inheritdoc/>
    protected override OwnPropertyProbe ProbeOwnProperty(JsValue property)
    {
        if (!TryFind(property, out _))
            return OwnPropertyProbe.Missing;
        RecordSelf();
        return OwnPropertyProbe.Enumerable;
    }

    /// <inheritdoc/>
    public override bool HasProperty(JsValue property)
    {
        if (TryFind(property, out _))
        {
            RecordSelf();
            return true;
        }

        return base.HasProperty(property);
    }

    /// <inheritdoc/>
    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.Empty | Types.String | Types.Symbol)
    {
        RecordSelf();
        var keys = new List<JsValue>(_table.Members.Count);
        if ((types & Types.String) != 0)
        {
            foreach (var member in _table.Members)
                keys.Add(new JsString(member.Name));
        }

        return keys;
    }

    /// <inheritdoc/>
    public override IEnumerable<KeyValuePair<JsValue, PropertyDescriptor>> GetOwnProperties()
    {
        for (var i = 0; i < _table.Members.Count; i++)
            yield return new KeyValuePair<JsValue, PropertyDescriptor>(new JsString(_table.Members[i].Name), new PropertyDescriptor(Read(i), PropertyFlag.OnlyEnumerable));
    }

    /// <inheritdoc/>
    public override bool Set(JsValue property, JsValue value, JsValue receiver) => false;

    /// <inheritdoc/>
    public override bool DefineOwnProperty(JsValue property, PropertyDescriptor desc) => false;

    /// <inheritdoc/>
    public override bool Delete(JsValue property) => false;

    /// <inheritdoc/>
    public override bool PreventExtensions() => true;

    /// <inheritdoc/>
    public override void RemoveOwnProperty(JsValue property)
    {
    }

    private bool TryFind(JsValue property, out int index)
    {
        if (property is JsString)
            return _table.TryGetIndex(property.ToString(), out index);
        index = -1;
        return false;
    }

    private void RecordSelf()
    {
        if (Target is IResolvedObject resolved)
            _values.State.Record(resolved.Dependencies);
        else if (Target is ITrackedValue { Dependencies.Count: > 0 } tracked)
            _values.State.Record(tracked.Dependencies);
    }

    private JsValue Read(int index)
    {
        RecordSelf();
        if (_table.Members[index].ReadKey is { } key)
            _values.State.Record(key);
        var cached = _cache[index];
        if (cached is not null)
            return cached;
        var member = _table.Members[index];
        var value = member.Function switch
        {
            // An object's calculated values (next-phase-2-design.md section 5.5): their own proxy, which knows the object's kind even when
            // the project declares none, so an undeclared read is MQ5012 and records what its kind declares.
            ProxyFunction.None when member.Property!.PropertyType == typeof(RCalculated) && Target is RAnnotated annotated =>
                new JsCalculatedProxy(_values, RCalculated.Of(annotated)),
            ProxyFunction.None => _values.ToJs(member.Property!.GetValue(Target)),
            _ => CreateFunction(member),
        };
        _cache[index] = value;
        return value;
    }

    private ClrFunction CreateFunction(ProxyMember member) => member.Function switch
    {
        ProxyFunction.HasStereotype => new ClrFunction(Engine, member.Name, (_, args) =>
        {
            RecordSelf();
            var key = StringArgument(args);
            var has = Target is RProcessNode node ? node.HasStereotype(key) : ((RAnnotated)Target).HasStereotype(key);
            return has ? JsBoolean.True : JsBoolean.False;
        }, 1, PropertyFlag.Configurable),
        ProxyFunction.HasTag => new ClrFunction(Engine, member.Name, (_, args) =>
        {
            RecordSelf();
            return ((RAnnotated)Target).HasTag(StringArgument(args)) ? JsBoolean.True : JsBoolean.False;
        }, 1, PropertyFlag.Configurable),
        ProxyFunction.Find => new ClrFunction(Engine, member.Name, (_, args) =>
        {
            var id = StringArgument(args);
            var found = ((ResolvedModel)Target).Find(id);
            if (found is null)
            {
                // Creating that element later must re-render the unit (engine-design.md section 9, `lookup`).
                _values.State.Record("e:" + id);
                return Null;
            }

            _values.State.Record(found.Dependencies);
            return _values.ToJs(found);
        }, 1, PropertyFlag.Configurable),
        _ => throw new InvalidOperationException("Unknown proxy function."),
    };

    private static string StringArgument(JsValue[] args) =>
        args.Length > 0 && args[0] is JsString s ? s.ToString() : args.Length > 0 ? TypeConverter.ToString(args[0]) : "";
}

/// <summary>
/// A frozen, array-like JavaScript view of a CLR list (engine-design.md section 10). Its prototype is <c>Array.prototype</c>, so
/// the non-mutating array methods (<c>map</c>, <c>filter</c>, <c>find</c>, iteration) work; <c>Array.isArray</c> is
/// <see langword="false"/>. Reading its length or an item of an <see cref="RList{T}"/> records the list's
/// <see cref="RList{T}.MembershipKeys"/>.
/// </summary>
/// <summary>
/// An object's calculated values as scripts see them (next-phase-2-design.md section 5.5): a frozen object whose own keys are the names
/// the object's kind declares, ordinal. Reading a key evaluates the value (or reuses it) and records its keys into the call's recorder;
/// <c>"name" in calculated</c> and the key list evaluate nothing and record what the kind declares (<c>cv:&lt;kind&gt;</c>). Reading an
/// undeclared name is a <c>TypeError</c> that carries MQ5012. A read of a failed value throws, and fails the call even when the script
/// catches the error.
/// </summary>
internal sealed class JsCalculatedProxy : ObjectInstance
{
    private readonly ScriptValues _values;
    private readonly RCalculated _view;
    private readonly Dictionary<string, JsValue> _read = new(StringComparer.Ordinal);

    internal JsCalculatedProxy(ScriptValues values, RCalculated view)
        : base(values.Engine)
    {
        _values = values;
        _view = view;
        Prototype = values.Engine.Intrinsics.Object.PrototypeObject;
        base.PreventExtensions();
    }

    public override object ToObject() => _view;

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (property is not JsString text)
            return base.Get(property, receiver);
        var name = text.ToString();
        if (IsDeclared(name))
            return Read(name);
        // Members every object inherits (toString, valueOf) and toJSON, which JSON.stringify asks for, read as on a plain object.
        if (string.Equals(name, "toJSON", StringComparison.Ordinal) || Prototype?.HasProperty(property) == true)
            return base.Get(property, receiver);
        var names = _view.ReadNames(_values.State.Reads);
        var kind = _view.Owner?.Kind ?? "this object";
        var declared = names.Count == 0 ? $"{kind} declares no calculated value" : $"the names {kind} declares are {string.Join(", ", names)}";
        var error = new JavaScriptException(Engine.Intrinsics.TypeError,
            $"The calculated value '{name}' is not declared for {kind} ({declared}); test for it with \"{name}\" in element.calculated.");
        _values.NoteUndeclared(error.Error);
        throw error;
    }

    public override PropertyDescriptor GetOwnProperty(JsValue property) =>
        property is JsString text && IsDeclared(text.ToString())
            ? new PropertyDescriptor(Read(text.ToString()), PropertyFlag.OnlyEnumerable)
            : PropertyDescriptor.Undefined;

    protected override bool TryGetOwnPropertyValue(JsValue property, JsValue receiver, out JsValue value)
    {
        if (property is JsString text && IsDeclared(text.ToString()))
        {
            value = Read(text.ToString());
            return true;
        }

        value = Undefined;
        return false;
    }

    protected override OwnPropertyProbe ProbeOwnProperty(JsValue property) =>
        property is JsString text && IsDeclared(text.ToString()) ? OwnPropertyProbe.Enumerable : OwnPropertyProbe.Missing;

    public override bool HasProperty(JsValue property) =>
        (property is JsString text && IsDeclared(text.ToString())) || base.HasProperty(property);

    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.Empty | Types.String | Types.Symbol)
    {
        var names = _view.ReadNames(_values.State.Reads);
        var keys = new List<JsValue>(names.Count);
        if ((types & Types.String) != 0)
        {
            foreach (var name in names)
                keys.Add(new JsString(name));
        }

        return keys;
    }

    public override IEnumerable<KeyValuePair<JsValue, PropertyDescriptor>> GetOwnProperties()
    {
        foreach (var name in _view.ReadNames(_values.State.Reads))
            yield return new KeyValuePair<JsValue, PropertyDescriptor>(new JsString(name), new PropertyDescriptor(Read(name), PropertyFlag.OnlyEnumerable));
    }

    public override bool Set(JsValue property, JsValue value, JsValue receiver) => false;

    public override bool DefineOwnProperty(JsValue property, PropertyDescriptor desc) => false;

    public override bool Delete(JsValue property) => false;

    public override bool PreventExtensions() => true;

    public override void RemoveOwnProperty(JsValue property)
    {
    }

    /// <summary>Whether the object's kind declares a name; records <c>cv:&lt;kind&gt;</c>, evaluates nothing.</summary>
    private bool IsDeclared(string name) => _view.Declares(name, _values.State.Reads);

    private JsValue Read(string name)
    {
        object? value;
        try
        {
            value = _view.Read(name, _values.State.Reads, _values.State.Token);
        }
        catch (CalculatedValueException e)
        {
            _values.NoteFailedRead(e.Diagnostic);
            throw new JavaScriptException(Engine.Intrinsics.Error, e.Diagnostic.Message);
        }
        finally
        {
            _values.ResetMemoryBaseline();
        }

        if (_read.TryGetValue(name, out var done))
            return done;
        var converted = _values.ToJs(value);
        _read[name] = converted;
        return converted;
    }
}

internal sealed class JsListProxy : ArrayLikeObject
{
    private readonly ScriptValues _values;
    private readonly IList _list;
    private readonly IReadOnlyList<string>? _membershipKeys;
    private readonly JsValue?[] _items;

    /// <summary>Creates a list view.</summary>
    /// <param name="values">The sandbox's value converter.</param>
    /// <param name="source">The CLR list as given.</param>
    /// <param name="list">An indexable view of <paramref name="source"/>.</param>
    /// <param name="membershipKeys">The list's membership keys, when it is an <see cref="RList{T}"/>.</param>
    internal JsListProxy(ScriptValues values, object source, IList list, IReadOnlyList<string>? membershipKeys)
        : base(values.Engine)
    {
        _values = values;
        _list = list;
        _membershipKeys = membershipKeys;
        _items = new JsValue?[list.Count];
        Source = source;
        Prototype = values.Engine.Intrinsics.Array.PrototypeObject;
        base.PreventExtensions();
    }

    /// <summary>The CLR list.</summary>
    public object Source { get; }

    /// <inheritdoc/>
    public override uint Length
    {
        get
        {
            Record();
            return (uint)_items.Length;
        }
    }

    /// <inheritdoc/>
    public override object ToObject() => Source;

    /// <inheritdoc/>
    public override bool TryGetIndex(uint index, out JsValue value)
    {
        Record();
        if (index >= (uint)_items.Length)
        {
            value = Undefined;
            return false;
        }

        value = _items[index] ??= _values.ToJs(_list[(int)index]);
        return true;
    }

    /// <inheritdoc/>
    public override bool Set(JsValue property, JsValue value, JsValue receiver) => false;

    /// <inheritdoc/>
    public override bool PreventExtensions() => true;

    private void Record()
    {
        if (_membershipKeys is not null)
            _values.State.Record(_membershipKeys);
    }
}
