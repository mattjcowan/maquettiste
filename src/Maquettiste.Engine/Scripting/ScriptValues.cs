using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jint;
using JsEngine = Jint.Engine;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Maquettiste.Engine.Diagnostics;
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

    /// <summary>
    /// The diagnostic of the first failed calculated value the running call read, or <see langword="null"/>: the call fails with it
    /// even when its script caught the error (next-phase-2-design.md section 5.5).
    /// </summary>
    public Diagnostic? FailedRead { get; private set; }

    private readonly List<JsValue> _undeclared = [];

    /// <summary>Starts a top-level call: no failed or undeclared read yet.</summary>
    public void BeginCall()
    {
        FailedRead = null;
        _undeclared.Clear();
    }

    /// <summary>Ends a top-level call.</summary>
    public void EndCall() => BeginCall();

    /// <summary>Notes a failed calculated value the call read (the first one wins).</summary>
    /// <param name="diagnostic">The value's diagnostic.</param>
    public void NoteFailedRead(Diagnostic diagnostic) => FailedRead ??= diagnostic;

    /// <summary>Notes the error object of a read of an undeclared calculated value (MQ5012 when it ends the call).</summary>
    /// <param name="error">The error object.</param>
    public void NoteUndeclared(JsValue error) => _undeclared.Add(error);

    /// <summary>Whether an error object is that of a read of an undeclared calculated value in the running call.</summary>
    /// <param name="error">The error object.</param>
    /// <returns><see langword="true"/> for such a read.</returns>
    public bool IsUndeclaredRead(JsValue error) => _undeclared.Exists(e => ReferenceEquals(e, error));

    /// <summary>
    /// Takes the memory baseline of the running call again after a read of a calculated value: Jint measures memory by what the thread
    /// allocates, and a nested value is evaluated on this thread, so its allocations must not count against the reader.
    /// </summary>
    public void ResetMemoryBaseline() => Engine.Constraints.Find<Jint.Constraints.MemoryLimitConstraint>()?.Reset();

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
        if (value is RCalculated calculated)
            return Remember(value, new JsCalculatedProxy(this, calculated));
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

    /// <summary>
    /// Converts the arguments of one call to a plain helper (<c>maquettiste.helper(name, fn, { plain: true })</c>,
    /// next-phase-1-design.md section 5.5) into frozen plain copies, bounded by member hops. A resolved object given as an argument,
    /// or as an item of an argument list or map, is at hop 0; its copy holds every member a proxy lists, without the functions. A
    /// resolved object a member holds, directly or inside a list or map, is copied at the next hop (lists, maps and the model's helper
    /// objects add none); at hop 2 it becomes the stub <c>{ id, kind }</c>. Scalars, strings, enum names and null copy as
    /// <see cref="ToJs(object?)"/> converts them. One memo keyed by (object, hop) serves the whole call, so a record shared by two
    /// arguments is one copy at a hop. Every resolved object copied at hop 0 or 1 records its dependencies, and every copied
    /// <see cref="RList{T}"/> its membership keys; a stub records nothing.
    /// </summary>
    /// <param name="values">The arguments.</param>
    /// <returns>The copies, in order.</returns>
    /// <exception cref="JavaScriptException">An argument holds the <see cref="ResolvedModel"/> (a <c>TypeError</c>).</exception>
    public JsValue[] ToPlainJs(IReadOnlyList<object?> values)
    {
        var copier = new PlainCopier(this);
        var result = new JsValue[values.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = copier.Copy(values[i], 0, 0);
        return result;
    }

    private static IList? AsList(object value) =>
        value is IReadOnlyList<object?> list ? new ReadOnlyListAdapter(list) : null;

    /// <summary>The plain copies of one call (<see cref="ToPlainJs"/>).</summary>
    private sealed class PlainCopier(ScriptValues owner)
    {
        /// <summary>The hop at which a resolved object is copied as its stub.</summary>
        private const int StubHop = 2;

        private readonly Dictionary<(object Value, int Hop), JsValue> _memo = new(MemoComparer.Instance);
        private int _copied;

        public JsValue Copy(object? value, int hop, int depth)
        {
            if (depth > owner._maxDepth)
                throw new ScriptValueException($"A value passed to a script nests deeper than {owner._maxDepth} levels.");
            switch (value)
            {
                case null or JsValue or string or bool or char or Enum or JsonElement:
                case int or long or short or byte or sbyte or ushort or uint or ulong or double or float or decimal:
                    return owner.ToJs(value, depth);
                case ResolvedModel:
                    throw new JavaScriptException(owner.Engine.Intrinsics.TypeError,
                        "A plain helper cannot take the model, whose copy would be the whole model: pass the list it needs (model.entities, database.tables).");
            }

            if (_memo.TryGetValue((value, hop), out var done))
                return done;
            if (_copied++ % CheckInterval == 0)
                owner.Engine.Constraints.Check();
            var type = value.GetType();
            if (MemberCatalog.IsProxyType(type))
                return CopyObject(value, type, hop, depth);

            JsValue copy;
            if (value is ScriptObjectMap map)
            {
                copy = owner.FrozenObject(map.Select(p => KeyValuePair.Create(p.Key, Copy(p.Value, hop, depth + 1))).ToList());
            }
            else if (owner._catalog.TryReadMap(value, out var entries))
            {
                entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                copy = owner.FrozenObject(entries.Select(p => KeyValuePair.Create(p.Key, Copy(p.Value, hop, depth + 1))).ToList());
            }
            else if (value is IEnumerable enumerable)
            {
                if (owner._catalog.MembershipKeys(value) is { } keys)
                    owner.State.Record(keys);
                var items = new List<JsValue>();
                foreach (var item in enumerable)
                    items.Add(Copy(item, hop, depth + 1));
                copy = owner.FrozenArray([.. items]);
            }
            else
            {
                throw new ScriptValueException($"A value of type {type.Name} cannot be passed to a script.");
            }

            _memo[(value, hop)] = copy;
            return copy;
        }

        /// <summary>A resolved object (or a helper object of the model) at a hop: its members, or its stub from hop 2.</summary>
        private JsValue CopyObject(object value, Type type, int hop, int depth)
        {
            var next = hop;
            if (value is IResolvedObject resolved)
            {
                if (hop >= StubHop)
                {
                    var stub = owner.FrozenObject([KeyValuePair.Create("id", (JsValue)new JsString(resolved.Id)), KeyValuePair.Create("kind", (JsValue)new JsString(resolved.Kind))]);
                    _memo[(value, hop)] = stub;
                    return stub;
                }

                owner.State.Record(resolved.Dependencies);
                next = hop + 1;
            }

            // Kept before the members are copied, so a cycle of helper objects (a category's parent and children) closes on it.
            var copy = new JsObject(owner.Engine);
            _memo[(value, hop)] = copy;
            foreach (var member in owner._catalog.GetTable(type).Members)
            {
                if (member.Function != ProxyFunction.None)
                    continue;
                if (member.Property!.PropertyType == typeof(RCalculated))
                {
                    // Every calculated value of an object at hop 0, none deeper: a plain copy never evaluates a whole neighbourhood.
                    if (hop == 0 && value is RAnnotated annotated)
                        copy.FastSetProperty(member.Name, new PropertyDescriptor(CopyCalculated(RCalculated.Of(annotated), next, depth + 1), PropertyFlag.ConfigurableEnumerableWritable));
                    continue;
                }

                copy.FastSetProperty(member.Name, new PropertyDescriptor(Copy(member.Property!.GetValue(value), next, depth + 1), PropertyFlag.ConfigurableEnumerableWritable));
            }

            return owner.Freeze(copy);
        }

        /// <summary>The frozen object of an object's calculated values, every declared name in ordinal order.</summary>
        private JsValue CopyCalculated(RCalculated calculated, int hop, int depth)
        {
            var entries = new List<KeyValuePair<string, JsValue>>();
            foreach (var name in calculated.ReadNames(owner.State.Reads))
            {
                object? value;
                try
                {
                    value = calculated.Read(name, owner.State.Reads, owner.State.Token);
                }
                finally
                {
                    owner.ResetMemoryBaseline();
                }

                entries.Add(KeyValuePair.Create(name, Copy(value, hop, depth + 1)));
            }

            return owner.FrozenObject(entries);
        }
    }

    /// <summary>Compares (object, hop) pairs by the object's identity.</summary>
    private sealed class MemoComparer : IEqualityComparer<(object Value, int Hop)>
    {
        public static readonly MemoComparer Instance = new();

        public bool Equals((object Value, int Hop) x, (object Value, int Hop) y) => ReferenceEquals(x.Value, y.Value) && x.Hop == y.Hop;

        public int GetHashCode((object Value, int Hop) obj) => HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Value), obj.Hop);
    }

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
