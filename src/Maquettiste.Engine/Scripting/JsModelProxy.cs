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
    }

    private JsValue Read(int index)
    {
        RecordSelf();
        var cached = _cache[index];
        if (cached is not null)
            return cached;
        var member = _table.Members[index];
        var value = member.Function switch
        {
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
            var has = Target is RProcessNode node ? node.HasStereotype(key) : ((RElement)Target).HasStereotype(key);
            return has ? JsBoolean.True : JsBoolean.False;
        }, 1, PropertyFlag.Configurable),
        ProxyFunction.HasTag => new ClrFunction(Engine, member.Name, (_, args) =>
        {
            RecordSelf();
            return ((RElement)Target).HasTag(StringArgument(args)) ? JsBoolean.True : JsBoolean.False;
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
