using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Text;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Maquettiste.Engine.Rendering;

/// <summary>Marks the renderer's own template-visible view types (<c>pack</c>, <c>unit</c>, <c>hints</c>).</summary>
internal interface ITemplateView;

/// <summary>The <c>pack</c> variable.</summary>
/// <param name="Name">The pack name.</param>
/// <param name="Version">The pack version.</param>
/// <param name="Params">The effective parameters.</param>
internal sealed record PackView(string Name, string Version, MapView Params) : ITemplateView;

/// <summary>The <c>unit</c> variable.</summary>
/// <param name="Id">The unit id in <c>pack.json</c>.</param>
/// <param name="Key">The unit key (<c>&lt;pack&gt;/&lt;unitId&gt;[:&lt;elementId&gt;]</c>).</param>
internal sealed record UnitView(string Id, string Key) : ITemplateView;

/// <summary>The <c>hints</c> variable: <c>generation["*"]</c> merged with <c>generation[pack]</c> (the pack wins).</summary>
/// <param name="Skip">Whether the element is skipped (planned units never see <see langword="true"/>).</param>
/// <param name="Rename">The rename hint.</param>
/// <param name="Variables">Hint variables.</param>
internal sealed record HintsView(bool Skip, string? Rename, MapView Variables) : ITemplateView;

/// <summary>A template-visible CLR property: snake_case name and a compiled getter.</summary>
/// <param name="Name">The snake_case name.</param>
/// <param name="Getter">The getter.</param>
internal sealed record TemplateMember(string Name, Func<object, object?> Getter);

/// <summary>
/// Reflection data for the renderer (one per <see cref="Renderer"/>, so per run; thread-safe): which CLR types templates may read,
/// their snake_case members, enum names, <see cref="RList{T}"/> membership keys and map readers. Only public get-only properties of
/// resolved-model types, schema-diff result types, <see cref="GenerationHints"/> and the renderer's view types are visible
/// (engine-design.md section 9); <c>Dependencies</c>, <c>MembershipKeys</c> and the model's <c>Source</c>, <c>Settings</c> and
/// <c>Diagnostics</c> are hidden, and no method is callable.
/// </summary>
internal sealed class TemplateMemberCatalog
{
    private const string SchemaDiffNamespace = "Maquettiste.Engine.SchemaDiff";

    private readonly ConcurrentDictionary<Type, ViewAccessor?> _accessors = new();
    private readonly ConcurrentDictionary<Type, ValueShape> _shapes = new();
    private readonly ConcurrentDictionary<Type, Func<object, IReadOnlyList<string>>?> _membershipKeys = new();
    private readonly ConcurrentDictionary<Type, (PropertyInfo Key, PropertyInfo Value)?> _pairs = new();
    private readonly ConcurrentDictionary<Type, FrozenDictionary<string, string>> _enumNames = new();

    /// <summary>Whether values of a type expose members to templates.</summary>
    /// <param name="type">The runtime type.</param>
    /// <returns>Whether the type is template-visible.</returns>
    public static bool IsViewType(Type type) =>
        MemberCatalog.IsProxyType(type)
        || typeof(ITemplateView).IsAssignableFrom(type)
        || (type.IsClass && type.IsPublic && !type.IsGenericType && string.Equals(type.Namespace, SchemaDiffNamespace, StringComparison.Ordinal));

    /// <summary>How <see cref="TrackingTemplateContext.Wrap"/> treats values of a type (cached per type).</summary>
    /// <param name="type">The runtime type.</param>
    /// <returns>The shape.</returns>
    public ValueShape Shape(Type type) => _shapes.GetOrAdd(type, static t =>
    {
        if (t.IsPrimitive || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)
            || t == typeof(TimeOnly) || t == typeof(TimeSpan) || t == typeof(string))
            return ValueShape.Scalar;
        if (IsViewType(t))
            return ValueShape.View;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(RList<>))
            return ValueShape.ResolvedList;
        if (typeof(IDictionary).IsAssignableFrom(t) || FindPairAccessors(t) is not null)
            return ValueShape.Map;
        if (typeof(IEnumerable).IsAssignableFrom(t))
            return ValueShape.List;
        return ValueShape.Other;
    });

    /// <summary>The accessor of a view type, or <see langword="null"/> when the type is not template-visible.</summary>
    /// <param name="type">The runtime type.</param>
    /// <returns>The accessor.</returns>
    public IObjectAccessor? GetAccessor(Type type) => _accessors.GetOrAdd(type, static t => IsViewType(t) ? new ViewAccessor(BuildMembers(t)) : null);

    /// <summary>The membership keys of an <see cref="RList{T}"/>, or <see langword="null"/> for anything else.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The keys.</returns>
    public IReadOnlyList<string>? MembershipKeys(object value)
    {
        var getter = _membershipKeys.GetOrAdd(value.GetType(), static t =>
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(RList<>))
                return null;
            var property = t.GetProperty(nameof(RList<>.MembershipKeys))!;
            var parameter = Expression.Parameter(typeof(object), "o");
            var body = Expression.Property(Expression.Convert(parameter, t), property);
            return Expression.Lambda<Func<object, IReadOnlyList<string>>>(body, parameter).Compile();
        });
        return getter?.Invoke(value);
    }

    /// <summary>The JSON name of an enum value (its <see cref="JsonStringEnumMemberNameAttribute"/>, else its kebab-case name).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The name.</returns>
    public string EnumName(Enum value)
    {
        var names = _enumNames.GetOrAdd(value.GetType(), static t => t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToFrozenDictionary(
                f => f.Name,
                f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? Casing.Kebab(f.Name),
                StringComparer.Ordinal));
        var name = value.ToString();
        return names.TryGetValue(name, out var mapped) ? mapped : name;
    }

    /// <summary>Reads a string-keyed map (<see cref="IDictionary"/> or any <c>IEnumerable&lt;KeyValuePair&lt;string, T&gt;&gt;</c>).</summary>
    /// <param name="value">The candidate.</param>
    /// <param name="entries">The entries, sorted by key (ordinal).</param>
    /// <returns>Whether the value is such a map.</returns>
    public bool TryReadMap(object value, [NotNullWhen(true)] out List<KeyValuePair<string, object?>>? entries)
    {
        entries = null;
        if (value is IDictionary dictionary)
        {
            var list = new List<KeyValuePair<string, object?>>(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                    return false;
                list.Add(KeyValuePair.Create(key, entry.Value));
            }

            entries = Sorted(list);
            return true;
        }

        var accessors = _pairs.GetOrAdd(value.GetType(), static t => FindPairAccessors(t));
        if (accessors is not { } pair || value is not IEnumerable enumerable)
            return false;
        var items = new List<KeyValuePair<string, object?>>();
        foreach (var item in enumerable)
            items.Add(KeyValuePair.Create((string)pair.Key.GetValue(item)!, pair.Value.GetValue(item)));
        entries = Sorted(items);
        return true;
    }

    private static List<KeyValuePair<string, object?>> Sorted(List<KeyValuePair<string, object?>> entries)
    {
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return entries;
    }

    private static (PropertyInfo Key, PropertyInfo Value)? FindPairAccessors(Type type)
    {
        foreach (var candidate in type.GetInterfaces().Prepend(type))
        {
            if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;
            var item = candidate.GetGenericArguments()[0];
            if (item.IsGenericType && item.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) && item.GetGenericArguments()[0] == typeof(string))
                return (item.GetProperty("Key")!, item.GetProperty("Value")!);
        }

        return null;
    }

    private static FrozenDictionary<string, TemplateMember> BuildMembers(Type type)
    {
        var members = new Dictionary<string, TemplateMember>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true } || IsHidden(type, property.Name))
                continue;
            var parameter = Expression.Parameter(typeof(object), "o");
            var body = Expression.Convert(Expression.Property(Expression.Convert(parameter, type), property), typeof(object));
            var getter = Expression.Lambda<Func<object, object?>>(body, parameter).Compile();
            var name = Casing.Snake(property.Name);
            members.TryAdd(name, new TemplateMember(name, getter));
        }

        return members.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static bool IsHidden(Type type, string property) =>
        property is nameof(IResolvedObject.Dependencies) or "MembershipKeys" or "EqualityContract"
        || (type == typeof(ResolvedModel) && property is nameof(ResolvedModel.Source) or nameof(ResolvedModel.Settings) or nameof(ResolvedModel.Diagnostics));
}

/// <summary>How a CLR value reaches templates.</summary>
internal enum ValueShape
{
    /// <summary>Strings, numbers, booleans, dates: as they are.</summary>
    Scalar,

    /// <summary>A template-visible object (resolved-model, schema-diff or view type): as it is, read through <see cref="ViewAccessor"/>.</summary>
    View,

    /// <summary>An <see cref="RList{T}"/>: a <see cref="TrackedList"/>.</summary>
    ResolvedList,

    /// <summary>A string-keyed map: a <see cref="MapView"/>.</summary>
    Map,

    /// <summary>Any other enumerable: a <see cref="ValueList"/>.</summary>
    List,

    /// <summary>Anything else: as it is, with no readable members.</summary>
    Other,
}

/// <summary>
/// Reads the members of a view type. Every read of an <see cref="IResolvedObject"/>'s member (or its member list) records the
/// object's <see cref="IResolvedObject.Dependencies"/> first; values are wrapped for templates (lists tracked, maps sorted, enums
/// as kebab strings). Nothing is writable.
/// </summary>
/// <param name="members">The members by snake_case name.</param>
internal sealed class ViewAccessor(FrozenDictionary<string, TemplateMember> members) : IObjectAccessor
{
    private readonly string[] _names = [.. members.Keys.Order(StringComparer.Ordinal)];

    /// <inheritdoc/>
    public bool HasIndexer => false;

    /// <inheritdoc/>
    public Type? IndexType => null;

    /// <inheritdoc/>
    public int GetMemberCount(TemplateContext context, SourceSpan span, object target)
    {
        TrackingTemplateContext.Of(context).RecordRead(target);
        return members.Count;
    }

    /// <inheritdoc/>
    public IEnumerable<string> GetMembers(TemplateContext context, SourceSpan span, object target)
    {
        TrackingTemplateContext.Of(context).RecordRead(target);
        return _names;
    }

    /// <inheritdoc/>
    public bool HasMember(TemplateContext context, SourceSpan span, object target, string member)
    {
        TrackingTemplateContext.Of(context).RecordRead(target);
        return members.ContainsKey(member);
    }

    /// <inheritdoc/>
    public bool TryGetValue(TemplateContext context, SourceSpan span, object target, string member, out object? value)
    {
        var tracking = TrackingTemplateContext.Of(context);
        tracking.RecordRead(target);
        if (members.TryGetValue(member, out var m))
        {
            value = tracking.Wrap(m.Getter(target));
            return true;
        }

        value = null;
        return false;
    }

    /// <inheritdoc/>
    public bool TrySetValue(TemplateContext context, SourceSpan span, object target, string member, object? value) =>
        throw new ScriptRuntimeException(span, $"The model is read-only: cannot set `{member}`.");

    /// <inheritdoc/>
    public bool TryGetItem(TemplateContext context, SourceSpan span, object target, object index, out object? value)
    {
        value = null;
        return false;
    }

    /// <inheritdoc/>
    public bool TrySetItem(TemplateContext context, SourceSpan span, object target, object index, object? value) =>
        throw new ScriptRuntimeException(span, "The model is read-only.");
}

/// <summary>Members <c>key</c> and <c>value</c> of the entries a map yields in a <c>for</c> loop.</summary>
internal sealed class EntryAccessor : IObjectAccessor
{
    /// <summary>The shared instance (stateless).</summary>
    public static readonly EntryAccessor Instance = new();

    private static readonly string[] Members = ["key", "value"];

    /// <inheritdoc/>
    public bool HasIndexer => false;

    /// <inheritdoc/>
    public Type? IndexType => null;

    /// <inheritdoc/>
    public int GetMemberCount(TemplateContext context, SourceSpan span, object target) => 2;

    /// <inheritdoc/>
    public IEnumerable<string> GetMembers(TemplateContext context, SourceSpan span, object target) => Members;

    /// <inheritdoc/>
    public bool HasMember(TemplateContext context, SourceSpan span, object target, string member) => member is "key" or "value";

    /// <inheritdoc/>
    public bool TryGetValue(TemplateContext context, SourceSpan span, object target, string member, out object? value)
    {
        var entry = (KeyValuePair<string, object?>)target;
        value = member switch
        {
            "key" => entry.Key,
            "value" => entry.Value,
            _ => null,
        };
        return member is "key" or "value";
    }

    /// <inheritdoc/>
    public bool TrySetValue(TemplateContext context, SourceSpan span, object target, string member, object? value) =>
        throw new ScriptRuntimeException(span, "A map entry is read-only.");

    /// <inheritdoc/>
    public bool TryGetItem(TemplateContext context, SourceSpan span, object target, object index, out object? value)
    {
        value = null;
        return false;
    }

    /// <inheritdoc/>
    public bool TrySetItem(TemplateContext context, SourceSpan span, object target, object index, object? value) =>
        throw new ScriptRuntimeException(span, "A map entry is read-only.");
}

/// <summary>Reads a <see cref="MapView"/>: members are its keys, in ordinal order; <c>map["key"]</c> works too.</summary>
internal sealed class MapAccessor : IObjectAccessor
{
    /// <summary>The shared instance (stateless).</summary>
    public static readonly MapAccessor Instance = new();

    /// <inheritdoc/>
    public bool HasIndexer => true;

    /// <inheritdoc/>
    public Type? IndexType => typeof(string);

    /// <inheritdoc/>
    public int GetMemberCount(TemplateContext context, SourceSpan span, object target) => ((MapView)target).Count;

    /// <inheritdoc/>
    public IEnumerable<string> GetMembers(TemplateContext context, SourceSpan span, object target) => ((MapView)target).Keys;

    /// <inheritdoc/>
    public bool HasMember(TemplateContext context, SourceSpan span, object target, string member) => ((MapView)target).ContainsKey(member);

    /// <inheritdoc/>
    public bool TryGetValue(TemplateContext context, SourceSpan span, object target, string member, out object? value) =>
        ((MapView)target).TryGetValue(member, out value);

    /// <inheritdoc/>
    public bool TrySetValue(TemplateContext context, SourceSpan span, object target, string member, object? value) =>
        throw new ScriptRuntimeException(span, $"The map is read-only: cannot set `{member}`.");

    /// <inheritdoc/>
    public bool TryGetItem(TemplateContext context, SourceSpan span, object target, object index, out object? value)
    {
        if (index is string key)
            return ((MapView)target).TryGetValue(key, out value);
        value = null;
        return false;
    }

    /// <inheritdoc/>
    public bool TrySetItem(TemplateContext context, SourceSpan span, object target, object index, object? value) =>
        throw new ScriptRuntimeException(span, "The map is read-only.");
}

/// <summary>
/// A read-only list of resolved objects (an <see cref="RList{T}"/>) as templates see it: enumerating it, indexing it or reading its
/// <c>size</c> records the list's membership keys (engine-design.md section 9), so a unit that iterated <c>model.entities</c> is
/// re-rendered when an entity is added or removed.
/// </summary>
internal sealed class TrackedList : IList, IReadOnlyList<object?>
{
    private readonly IReadOnlyList<IResolvedObject> _items;
    private readonly IReadOnlyList<string> _keys;
    private readonly UnitRecorder _recorder;

    /// <summary>Creates a view.</summary>
    /// <param name="source">The <see cref="RList{T}"/>.</param>
    /// <param name="items">The same list, typed as resolved objects.</param>
    /// <param name="keys">Its membership keys.</param>
    /// <param name="recorder">The unit's recorder.</param>
    public TrackedList(object source, IReadOnlyList<IResolvedObject> items, IReadOnlyList<string> keys, UnitRecorder recorder)
    {
        Source = source;
        _items = items;
        _keys = keys;
        _recorder = recorder;
    }

    /// <summary>The underlying <see cref="RList{T}"/> (what scripts receive).</summary>
    public object Source { get; }

    /// <inheritdoc cref="IReadOnlyCollection{T}.Count"/>
    public int Count
    {
        get
        {
            Touch();
            return _items.Count;
        }
    }

    /// <inheritdoc/>
    public bool IsFixedSize => true;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool IsSynchronized => false;

    /// <inheritdoc/>
    public object SyncRoot => this;

    /// <inheritdoc cref="IReadOnlyList{T}.this"/>
    public object? this[int index]
    {
        get
        {
            Touch();
            return _items[index];
        }
        set => throw ReadOnly();
    }

    /// <inheritdoc/>
    public IEnumerator<object?> GetEnumerator()
    {
        Touch();
        return _items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Contains(object? value)
    {
        Touch();
        return _items.Contains(value as IResolvedObject);
    }

    /// <inheritdoc/>
    public int IndexOf(object? value)
    {
        Touch();
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], value))
                return i;
        }

        return -1;
    }

    /// <inheritdoc/>
    public void CopyTo(Array array, int index)
    {
        Touch();
        foreach (var item in _items)
            array.SetValue(item, index++);
    }

    /// <inheritdoc/>
    public int Add(object? value) => throw ReadOnly();

    /// <inheritdoc/>
    public void Clear() => throw ReadOnly();

    /// <inheritdoc/>
    public void Insert(int index, object? value) => throw ReadOnly();

    /// <inheritdoc/>
    public void Remove(object? value) => throw ReadOnly();

    /// <inheritdoc/>
    public void RemoveAt(int index) => throw ReadOnly();

    private void Touch() => _recorder.RecordAll(_keys);

    private static NotSupportedException ReadOnly() => new("Model lists are read-only.");
}

/// <summary>A read-only list of plain values; items are wrapped for templates when read.</summary>
internal sealed class ValueList : IList, IReadOnlyList<object?>
{
    private readonly object?[] _items;
    private readonly TrackingTemplateContext _context;

    /// <summary>Creates a view.</summary>
    /// <param name="source">The original list (what scripts receive).</param>
    /// <param name="items">Its items.</param>
    /// <param name="context">The unit's context (for wrapping).</param>
    public ValueList(object source, object?[] items, TrackingTemplateContext context)
    {
        Source = source;
        _items = items;
        _context = context;
    }

    /// <summary>The underlying list.</summary>
    public object Source { get; }

    /// <inheritdoc cref="IReadOnlyCollection{T}.Count"/>
    public int Count => _items.Length;

    /// <inheritdoc/>
    public bool IsFixedSize => true;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool IsSynchronized => false;

    /// <inheritdoc/>
    public object SyncRoot => this;

    /// <inheritdoc cref="IReadOnlyList{T}.this"/>
    public object? this[int index]
    {
        get => _context.Wrap(_items[index]);
        set => throw new NotSupportedException("The list is read-only.");
    }

    /// <inheritdoc/>
    public IEnumerator<object?> GetEnumerator()
    {
        foreach (var item in _items)
            yield return _context.Wrap(item);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Contains(object? value) => IndexOf(value) >= 0;

    /// <inheritdoc/>
    public int IndexOf(object? value)
    {
        for (var i = 0; i < _items.Length; i++)
        {
            if (Equals(_items[i], value) || Equals(this[i], value))
                return i;
        }

        return -1;
    }

    /// <inheritdoc/>
    public void CopyTo(Array array, int index)
    {
        foreach (var item in this)
            array.SetValue(item, index++);
    }

    /// <inheritdoc/>
    public int Add(object? value) => throw new NotSupportedException("The list is read-only.");

    /// <inheritdoc/>
    public void Clear() => throw new NotSupportedException("The list is read-only.");

    /// <inheritdoc/>
    public void Insert(int index, object? value) => throw new NotSupportedException("The list is read-only.");

    /// <inheritdoc/>
    public void Remove(object? value) => throw new NotSupportedException("The list is read-only.");

    /// <inheritdoc/>
    public void RemoveAt(int index) => throw new NotSupportedException("The list is read-only.");
}

/// <summary>
/// A read-only string-keyed map as templates see it: keys in ordinal order (never a dictionary's own enumeration order), values
/// wrapped when read, an optional hook that records a dependency per key read (<c>schema_diff</c> records <c>d:&lt;id&gt;</c>), and an
/// optional hook for reads of the key set as a whole (enumeration, size, key or value listing).
/// </summary>
internal sealed class MapView : IDictionary<string, object?>, IReadOnlyDictionary<string, object?>
{
    private readonly string[] _keys;
    private readonly object?[] _values;
    private readonly FrozenDictionary<string, int> _index;
    private readonly TrackingTemplateContext? _context;
    private readonly Action<string>? _onRead;
    private readonly Action? _onKeySet;

    /// <summary>Creates a view.</summary>
    /// <param name="source">The original map (what scripts receive).</param>
    /// <param name="entries">Entries sorted by key (ordinal), unique keys.</param>
    /// <param name="context">The unit's context (for wrapping); <see langword="null"/> leaves values as they are.</param>
    /// <param name="onRead">Called with each key read.</param>
    /// <param name="onKeySet">Called when the key set as a whole is read (enumeration, size, key or value listing).</param>
    public MapView(object source, IReadOnlyList<KeyValuePair<string, object?>> entries, TrackingTemplateContext? context, Action<string>? onRead = null,
        Action? onKeySet = null)
    {
        Source = source;
        _keys = [.. entries.Select(e => e.Key)];
        _values = [.. entries.Select(e => e.Value)];
        _index = _keys.Select((k, i) => KeyValuePair.Create(k, i)).ToFrozenDictionary(StringComparer.Ordinal);
        _context = context;
        _onRead = onRead;
        _onKeySet = onKeySet;
    }

    /// <summary>The underlying map.</summary>
    public object Source { get; }

    /// <inheritdoc cref="IReadOnlyCollection{T}.Count"/>
    public int Count
    {
        get
        {
            TouchAll();
            return _keys.Length;
        }
    }

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Keys"/>
    public IReadOnlyList<string> Keys
    {
        get
        {
            TouchAll();
            return _keys;
        }
    }

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.Values"/>
    public IReadOnlyList<object?> Values
    {
        get
        {
            TouchAll();
            return [.. _values.Select(Wrap)];
        }
    }

    ICollection<string> IDictionary<string, object?>.Keys => Keys.ToArray();

    ICollection<object?> IDictionary<string, object?>.Values => Values.ToArray();

    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => Keys;

    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values => Values;

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.this"/>
    public object? this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        set => throw new NotSupportedException("The map is read-only.");
    }

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.ContainsKey"/>
    public bool ContainsKey(string key)
    {
        _onRead?.Invoke(key);
        return _index.ContainsKey(key);
    }

    /// <inheritdoc cref="IReadOnlyDictionary{TKey, TValue}.TryGetValue"/>
    public bool TryGetValue(string key, out object? value)
    {
        _onRead?.Invoke(key);
        if (_index.TryGetValue(key, out var i))
        {
            value = Wrap(_values[i]);
            return true;
        }

        value = null;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        TouchAll();
        for (var i = 0; i < _keys.Length; i++)
            yield return KeyValuePair.Create(_keys[i], Wrap(_values[i]));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Contains(KeyValuePair<string, object?> item) => TryGetValue(item.Key, out var value) && Equals(value, item.Value);

    /// <inheritdoc/>
    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
    {
        foreach (var entry in this)
            array[arrayIndex++] = entry;
    }

    /// <inheritdoc/>
    public void Add(string key, object? value) => throw new NotSupportedException("The map is read-only.");

    /// <inheritdoc/>
    public void Add(KeyValuePair<string, object?> item) => throw new NotSupportedException("The map is read-only.");

    /// <inheritdoc/>
    public void Clear() => throw new NotSupportedException("The map is read-only.");

    /// <inheritdoc/>
    public bool Remove(string key) => throw new NotSupportedException("The map is read-only.");

    /// <inheritdoc/>
    public bool Remove(KeyValuePair<string, object?> item) => throw new NotSupportedException("The map is read-only.");

    /// <summary>The raw entries, sorted by key (for <c>json</c>).</summary>
    /// <returns>The entries.</returns>
    public IEnumerable<KeyValuePair<string, object?>> RawEntries()
    {
        TouchAll();
        for (var i = 0; i < _keys.Length; i++)
            yield return KeyValuePair.Create(_keys[i], _values[i]);
    }

    private object? Wrap(object? value) => _context is null ? value : _context.Wrap(value);

    private void TouchAll()
    {
        _onKeySet?.Invoke();
        if (_onRead is null)
            return;
        foreach (var key in _keys)
            _onRead(key);
    }
}

/// <summary>Records the dependency keys one unit reads (single-threaded; engine-design.md section 4.1 <see cref="IReadRecorder"/>).</summary>
internal sealed class UnitRecorder : IReadRecorder
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly HashSet<object> _objects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _keyLists = new(ReferenceEqualityComparer.Instance);

    /// <inheritdoc/>
    public void Record(string dependencyKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(dependencyKey);
        _keys.Add(dependencyKey);
    }

    /// <summary>Records every key of a list (once per list instance).</summary>
    /// <param name="keys">The keys.</param>
    public void RecordAll(IReadOnlyList<string> keys)
    {
        if (!_keyLists.Add(keys))
            return;
        foreach (var key in keys)
            _keys.Add(key);
    }

    /// <summary>Records an object's dependencies (once per object).</summary>
    /// <param name="value">The resolved object.</param>
    public void RecordObject(IResolvedObject value)
    {
        if (_objects.Add(value))
            RecordAll(value.Dependencies);
    }

    /// <summary>The recorded keys, ordinal.</summary>
    /// <returns>The keys.</returns>
    public IReadOnlyList<string> SortedKeys() => [.. _keys.Order(StringComparer.Ordinal)];
}

/// <summary>Small value helpers shared by the renderer's parts.</summary>
internal static class TemplateValues
{
    /// <summary>A short type name for messages (the resolved kind for model objects).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The name.</returns>
    public static string TypeName(object? value) => value switch
    {
        null => "null",
        IResolvedObject r => r.Kind,
        TrackedList or ValueList or ScriptArray => "list",
        MapView or ScriptObject => "map",
        ITemplateView => value.GetType().Name.Replace("View", "", StringComparison.Ordinal).ToLowerInvariant(),
        _ => value.GetType().Name,
    };

    /// <summary>Plain CLR value of a JSON element (numbers: <c>long</c>, else <c>double</c>).</summary>
    /// <param name="value">The element.</param>
    /// <returns>The value.</returns>
    public static object? Plain(JsonElement value) => ResolutionValues.Plain(value);
}
