using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Jint;
using JsEngine = Jint.Engine;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Scripting;

/// <summary>
/// Moves values across the sandbox boundary (engine-design.md section 10): JSON-like data (string, number, bool, null, arrays,
/// plain objects) and resolved objects as proxies both ways. Values entering a script are frozen; resolved objects keep their
/// identity within one engine. Only values that cannot change (resolved-model objects and their immutable collections) are
/// cached; other CLR maps and lists are converted afresh on every call, so a caller that mutates them between calls is seen
/// correctly and they are not kept alive by the engine. Only JSON-like data and model objects can leave a script: a function,
/// symbol, promise, date, <c>Map</c>, <c>Set</c>, class instance or other non-plain object is an error.
/// </summary>
internal sealed class ScriptValues
{
    private readonly MemberCatalog _catalog;
    private readonly Dictionary<object, ObjectInstance> _proxies = new(ReferenceEqualityComparer.Instance);
    private readonly JsValue _freeze;
    private readonly ObjectInstance _objectPrototype;
    private readonly uint _maxArrayLength;
    private readonly int _maxDepth;

    /// <summary>Creates a converter for one engine.</summary>
    /// <param name="engine">The engine.</param>
    /// <param name="state">The engine's call state.</param>
    /// <param name="catalog">The pool's reflection catalog.</param>
    /// <param name="maxDepth">The deepest nesting a value may have.</param>
    /// <param name="maxArrayLength">The longest array a script may return (the engine's <c>MaxArraySize</c>).</param>
    public ScriptValues(JsEngine engine, CallState state, MemberCatalog catalog, int maxDepth, uint maxArrayLength)
    {
        Engine = engine;
        State = state;
        _catalog = catalog;
        _maxDepth = Math.Max(maxDepth, 16);
        _maxArrayLength = maxArrayLength;
        _freeze = engine.Intrinsics.Object.Get("freeze");
        _objectPrototype = engine.Intrinsics.Object.PrototypeObject;
    }

    /// <summary>How many items or keys a conversion loop handles between two checks of the engine's constraints.</summary>
    private const int CheckInterval = 1024;

    /// <summary>The engine.</summary>
    public JsEngine Engine { get; }

    /// <summary>The engine's call state (recorder, token, parameters).</summary>
    public CallState State { get; }

    /// <summary>Converts a CLR value into a frozen script value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The script value.</returns>
    public JsValue ToJs(object? value) => ToJs(value, 0);

    /// <summary>Converts a JSON value into a deeply frozen script value.</summary>
    /// <param name="element">The JSON value.</param>
    /// <returns>The script value.</returns>
    public JsValue FromJson(JsonElement element) => FromJson(element, 0);

    /// <summary>Freezes an object with <c>Object.freeze</c>.</summary>
    /// <param name="value">The object.</param>
    /// <returns>The same object.</returns>
    public JsValue Freeze(JsValue value)
    {
        Engine.Call(_freeze, JsValue.Undefined, [value]);
        return value;
    }

    /// <summary>Creates a frozen plain object.</summary>
    /// <param name="entries">The entries, in order.</param>
    /// <returns>The object.</returns>
    public JsValue FrozenObject(IEnumerable<KeyValuePair<string, JsValue>> entries) => Freeze(JsObject.CreateFromEntries(Engine, entries));

    /// <summary>Creates a frozen array.</summary>
    /// <param name="items">The items.</param>
    /// <returns>The array.</returns>
    public JsValue FrozenArray(JsValue[] items) => Freeze(new JsArray(Engine, items));

    /// <summary>Converts a script value into a JSON-like CLR value.</summary>
    /// <param name="value">The script value.</param>
    /// <param name="what">What the value is, for error messages (for example "helper 'money' result").</param>
    /// <returns>The CLR value.</returns>
    public object? FromJs(JsValue value, string what) => FromJs(value, what, 0);

    private JsValue ToJs(object? value, int depth)
    {
        if (depth > _maxDepth)
            throw new ScriptValueException($"A value passed to a script nests deeper than {_maxDepth} levels.");
        switch (value)
        {
            case null:
                return JsValue.Null;
            case JsValue js:
                return js;
            case string s:
                return new JsString(s);
            case bool b:
                return b ? JsBoolean.True : JsBoolean.False;
            case int i:
                return JsNumber.Create((double)i);
            case long l:
                return JsNumber.Create(l);
            case short or byte or sbyte or ushort or uint:
                return JsNumber.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong ul:
                return JsNumber.Create((double)ul);
            case double d:
                return JsNumber.Create(d);
            case float f:
                return JsNumber.Create(f);
            case decimal m:
                return JsNumber.Create((double)m);
            case char c:
                return new JsString(c);
            case Enum e:
                return new JsString(_catalog.EnumName(e));
            case JsonElement json:
                return FromJson(json, depth);
        }

        if (_proxies.TryGetValue(value, out var existing))
            return existing;

        var type = value.GetType();
        if (value is ResolvedModel || MemberCatalog.IsProxyType(type))
            return Remember(value, new JsModelProxy(this, value, _catalog.GetTable(type)));

        if (value is ScriptObjectMap map)
            return FrozenObject(map.Select(p => KeyValuePair.Create(p.Key, ToJs(p.Value, depth + 1))).ToList());

        if (_catalog.TryReadMap(value, out var entries))
        {
            // CLR maps have no dependable order: keys cross in ordinal order.
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            var obj = FrozenObject(entries.Select(p => KeyValuePair.Create(p.Key, ToJs(p.Value, depth + 1))).ToList());
            return _catalog.IsImmutable(type) ? Remember(value, (ObjectInstance)obj) : obj;
        }

        if (value is IEnumerable enumerable)
        {
            var list = value as IList ?? AsList(value) ?? enumerable.Cast<object?>().ToArray();
            var proxy = new JsListProxy(this, value, list, _catalog.MembershipKeys(value));
            return _catalog.IsImmutable(type) ? Remember(value, proxy) : proxy;
        }

        throw new ScriptValueException($"A value of type {type.Name} cannot be passed to a script.");
    }

    private static IList? AsList(object value) =>
        value is IReadOnlyList<object?> list ? new ReadOnlyListAdapter(list) : null;

    private ObjectInstance Remember(object key, ObjectInstance proxy)
    {
        _proxies[key] = proxy;
        return proxy;
    }

    private JsValue FromJson(JsonElement element, int depth)
    {
        if (depth > _maxDepth)
            throw new ScriptValueException($"A JSON value nests deeper than {_maxDepth} levels.");
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var entries = new List<KeyValuePair<string, JsValue>>();
                foreach (var property in element.EnumerateObject())
                    entries.Add(KeyValuePair.Create(property.Name, FromJson(property.Value, depth + 1)));
                return FrozenObject(entries);
            case JsonValueKind.Array:
                var items = new JsValue[element.GetArrayLength()];
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    items[index++] = FromJson(item, depth + 1);
                return FrozenArray(items);
            case JsonValueKind.String:
                return new JsString(element.GetString()!);
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? JsNumber.Create(l) : JsNumber.Create(element.GetDouble());
            case JsonValueKind.True:
                return JsBoolean.True;
            case JsonValueKind.False:
                return JsBoolean.False;
            default:
                return JsValue.Null;
        }
    }

    private object? FromJs(JsValue value, string what, int depth)
    {
        if (depth > _maxDepth)
            throw new ScriptValueException($"The {what} nests deeper than {_maxDepth} levels (or is circular).");
        switch (value.Type)
        {
            case Types.Undefined:
            case Types.Null:
            case Types.Empty:
                return null;
            case Types.Boolean:
                return TypeConverter.ToBoolean(value);
            case Types.String:
                return value.ToString();
            case Types.Number:
                return Number(TypeConverter.ToNumber(value));
            case Types.Symbol:
                throw new ScriptValueException($"The {what} is a symbol; only JSON-like values and model objects can leave a script.");
            case Types.BigInt:
                throw new ScriptValueException($"The {what} is a BigInt; only JSON-like values and model objects can leave a script.");
        }

        switch (value)
        {
            case JsModelProxy proxy:
                return proxy.Target;
            case JsListProxy list:
                return list.Source;
            case Function:
                throw new ScriptValueException($"The {what} is a function; only JSON-like values and model objects can leave a script.");
            case JsDate:
                throw new ScriptValueException($"The {what} is a Date; return date.toISOString() instead.");
            case ArrayInstance array:
                var length = ArrayLength(array, what);
                var items = new object?[length];
                for (var i = 0U; i < length; i++)
                {
                    if (i % CheckInterval == 0)
                        Engine.Constraints.Check();
                    items[i] = FromJs(array.Get(JsNumber.Create(i)), what, depth + 1);
                }

                return Array.AsReadOnly(items);
            case ObjectInstance obj when obj.Prototype is null || ReferenceEquals(obj.Prototype, _objectPrototype):
                var map = new ScriptObjectMap();
                var count = 0;
                foreach (var key in obj.GetOwnPropertyKeys(Types.String))
                {
                    if (count++ % CheckInterval == 0)
                        Engine.Constraints.Check();
                    var descriptor = obj.GetOwnProperty(key);
                    if (descriptor == Jint.Runtime.Descriptors.PropertyDescriptor.Undefined || !descriptor.Enumerable)
                        continue;
                    map.Add(key.ToString(), FromJs(obj.Get(key), what, depth + 1));
                }

                return map;
            case ObjectInstance other:
                throw new ScriptValueException(NotPlain(other, what));
            default:
                throw new ScriptValueException($"The {what} cannot leave a script.");
        }
    }

    /// <summary>
    /// The length of an array (or array-like) leaving a script, refused before the host allocates anything when it is longer
    /// than the engine's <c>MaxArraySize</c>: setting <c>length</c> on an empty array costs the script nothing, but copying it
    /// would cost the host eight bytes per element.
    /// </summary>
    /// <param name="array">The array.</param>
    /// <param name="what">What the value is, for the error message.</param>
    /// <returns>The length.</returns>
    /// <exception cref="MemoryLimitExceededException">The array is too long.</exception>
    public uint ArrayLength(ObjectInstance array, string what)
    {
        var length = TypeConverter.ToLength(array.Get("length"));
        if (length > _maxArrayLength)
            throw new MemoryLimitExceededException($"The {what} is an array of {length} elements, more than the {_maxArrayLength} allowed.");
        return (uint)length;
    }

    /// <summary>The error message for an object that is neither plain, an array nor a model object.</summary>
    private static string NotPlain(ObjectInstance value, string what)
    {
        string? name = null;
        for (var prototype = value.Prototype; prototype is not null && name is null; prototype = prototype.Prototype)
        {
            var constructor = prototype.GetOwnProperty("constructor");
            if (constructor != Jint.Runtime.Descriptors.PropertyDescriptor.Undefined && constructor.Value is Function function
                && function.GetOwnProperty("name").Value is JsString functionName && functionName.Length > 0)
                name = functionName.ToString();
        }

        return name switch
        {
            "Promise" => $"The {what} is a promise; asynchronous scripts are not supported.",
            "Map" => $"The {what} is a Map; convert it with Object.fromEntries(map) first.",
            "Set" => $"The {what} is a Set; convert it with [...set] first.",
            null => $"The {what} is an object that is not a plain object; only JSON-like values and model objects can leave a script.",
            _ => $"The {what} is an instance of {name}; only plain objects, arrays, JSON-like values and model objects can leave a script.",
        };
    }

    private static object Number(double d)
    {
        const double maxSafe = 9007199254740991d;
        if (!double.IsFinite(d) || Math.Floor(d) != d || Math.Abs(d) > maxSafe)
            return d;
        return (long)d;
    }

    /// <summary>Makes a read-only list of references indexable as <see cref="IList"/>.</summary>
    private sealed class ReadOnlyListAdapter(IReadOnlyList<object?> list) : IList
    {
        public object? this[int index] { get => list[index]; set => throw new NotSupportedException(); }

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public int Count => list.Count;

        public bool IsSynchronized => false;

        public object SyncRoot => list;

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value) => list.Contains(value);

        public void CopyTo(Array array, int index)
        {
            for (var i = 0; i < list.Count; i++)
                array.SetValue(list[i], index + i);
        }

        public IEnumerator GetEnumerator() => list.GetEnumerator();

        public int IndexOf(object? value)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (Equals(list[i], value))
                    return i;
            }

            return -1;
        }

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}

/// <summary>
/// A plain object returned by a script: a read-only string-keyed map that keeps the object's JavaScript property order
/// (integer-like keys ascending, then insertion order), which the same script always reproduces.
/// </summary>
internal sealed class ScriptObjectMap : IReadOnlyDictionary<string, object?>
{
    private readonly List<KeyValuePair<string, object?>> _entries = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public object? this[string key] => _entries[_index[key]].Value;

    /// <inheritdoc/>
    public IEnumerable<string> Keys => _entries.Select(e => e.Key);

    /// <inheritdoc/>
    public IEnumerable<object?> Values => _entries.Select(e => e.Value);

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _index.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            value = _entries[i].Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(string key, object? value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            _entries[i] = KeyValuePair.Create(key, value);
            return;
        }

        _index[key] = _entries.Count;
        _entries.Add(KeyValuePair.Create(key, value));
    }
}
