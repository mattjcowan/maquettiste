using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Scripting;

/// <summary>A function member a proxy exposes besides the properties.</summary>
internal enum ProxyFunction
{
    /// <summary>A plain property.</summary>
    None,

    /// <summary><c>hasStereotype(key)</c> on annotated objects (conceptual objects, tables, views, sequences) and process nodes.</summary>
    HasStereotype,

    /// <summary><c>hasTag(key)</c> on annotated objects (conceptual objects, tables, views, sequences).</summary>
    HasTag,

    /// <summary><c>find(id)</c> on the model.</summary>
    Find,
}

/// <summary>One script-visible member of a CLR type.</summary>
/// <param name="Name">The camelCase name.</param>
/// <param name="Property">The property, when the member is one.</param>
/// <param name="Function">The function kind, when the member is a function.</param>
internal sealed record ProxyMember(string Name, PropertyInfo? Property, ProxyFunction Function);

/// <summary>The script-visible members of one CLR type, ordinal by name.</summary>
internal sealed class MemberTable
{
    private readonly FrozenDictionary<string, int> _index;

    /// <summary>Creates a table.</summary>
    /// <param name="members">The members.</param>
    public MemberTable(IEnumerable<ProxyMember> members)
    {
        Members = [.. members.OrderBy(m => m.Name, StringComparer.Ordinal)];
        _index = Members.Select((m, i) => KeyValuePair.Create(m.Name, i)).ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The members, ordinal by name.</summary>
    public IReadOnlyList<ProxyMember> Members { get; }

    /// <summary>Finds a member.</summary>
    /// <param name="name">The script name.</param>
    /// <param name="index">The member index.</param>
    /// <returns>Whether the member exists.</returns>
    public bool TryGetIndex(string name, out int index) => _index.TryGetValue(name, out index);
}

/// <summary>
/// Reflection data shared by the sandboxes of one pool (thread-safe): which CLR types cross as proxies, their members, the
/// membership keys of <see cref="RList{T}"/> and the key/value accessors of dictionaries.
/// </summary>
internal sealed class MemberCatalog
{
    private const string ResolutionNamespace = "Maquettiste.Engine.Resolution";

    private readonly ConcurrentDictionary<Type, MemberTable> _tables = new();
    private readonly ConcurrentDictionary<Type, PropertyInfo?> _membershipKeys = new();
    private readonly ConcurrentDictionary<Type, (PropertyInfo Key, PropertyInfo Value)?> _pairs = new();
    private readonly ConcurrentDictionary<Type, FrozenDictionary<string, string>> _enumNames = new();
    private readonly ConcurrentDictionary<Type, bool> _immutable = new();

    /// <summary>Whether values of a type cross into scripts as read-only proxies.</summary>
    /// <param name="type">The runtime type.</param>
    /// <returns><see langword="true"/> for resolved-model types and their helper types.</returns>
    public static bool IsProxyType(Type type) =>
        type == typeof(GenerationHints)
        || (type.IsClass && type.IsPublic && !type.IsGenericType && string.Equals(type.Namespace, ResolutionNamespace, StringComparison.Ordinal)
            && !typeof(IEnumerable).IsAssignableFrom(type));

    /// <summary>
    /// Whether values of a collection type can never change, so their script view can be cached for the engine's lifetime:
    /// <see cref="RList{T}"/>, frozen collections and <c>System.Collections.Immutable</c> reference types. Anything else (a
    /// <see cref="List{T}"/>, a <see cref="Dictionary{TKey, TValue}"/>, a template engine's own arrays and objects) may be
    /// mutated between calls and is converted afresh each time.
    /// </summary>
    /// <param name="type">The runtime type.</param>
    /// <returns><see langword="true"/> for immutable collection types.</returns>
    public bool IsImmutable(Type type) => _immutable.GetOrAdd(type, static t => ComputeImmutable(t));

    private static bool ComputeImmutable(Type type)
    {
        if (type.IsValueType)
            return false;
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType)
                continue;
            var definition = current.GetGenericTypeDefinition();
            if (definition == typeof(RList<>) || definition == typeof(FrozenDictionary<,>) || definition == typeof(FrozenSet<>))
                return true;
        }

        return string.Equals(type.Namespace, "System.Collections.Immutable", StringComparison.Ordinal);
    }

    /// <summary>The members of a proxy type.</summary>
    /// <param name="type">The runtime type.</param>
    /// <returns>The table.</returns>
    public MemberTable GetTable(Type type) => _tables.GetOrAdd(type, static t => BuildTable(t));

    /// <summary>The membership keys of an <see cref="RList{T}"/>, or <see langword="null"/> for any other list.</summary>
    /// <param name="list">The list.</param>
    /// <returns>The keys.</returns>
    public IReadOnlyList<string>? MembershipKeys(object list)
    {
        var property = _membershipKeys.GetOrAdd(list.GetType(), static t =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(RList<>) ? t.GetProperty("MembershipKeys") : null);
        return property?.GetValue(list) as IReadOnlyList<string>;
    }

    /// <summary>Reads a string-keyed map (any <c>IEnumerable&lt;KeyValuePair&lt;string, T&gt;&gt;</c> or <see cref="IDictionary"/>).</summary>
    /// <param name="value">The candidate.</param>
    /// <param name="entries">The entries, in the map's enumeration order.</param>
    /// <returns>Whether the value is such a map.</returns>
    public bool TryReadMap(object value, out List<KeyValuePair<string, object?>> entries)
    {
        entries = [];
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                    return false;
                entries.Add(KeyValuePair.Create(key, entry.Value));
            }

            return true;
        }

        var accessors = _pairs.GetOrAdd(value.GetType(), static t => FindPairAccessors(t));
        if (accessors is not { } pair || value is not IEnumerable enumerable)
            return false;
        foreach (var item in enumerable)
            entries.Add(KeyValuePair.Create((string)pair.Key.GetValue(item)!, pair.Value.GetValue(item)));
        return true;
    }

    /// <summary>The JSON name of an enum value (its <see cref="JsonStringEnumMemberNameAttribute"/>, else its kebab-case name).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The name.</returns>
    public string EnumName(Enum value)
    {
        var names = _enumNames.GetOrAdd(value.GetType(), static t => t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToFrozenDictionary(
                f => f.Name,
                f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? Kebab(f.Name),
                StringComparer.Ordinal));
        var name = value.ToString();
        return names.TryGetValue(name, out var mapped) ? mapped : name;
    }

    private static MemberTable BuildTable(Type type)
    {
        var members = new List<ProxyMember>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true })
                continue;
            if (IsHidden(type, property.Name))
                continue;
            members.Add(new ProxyMember(Camel(property.Name), property, ProxyFunction.None));
        }

        if (typeof(RAnnotated).IsAssignableFrom(type))
        {
            members.Add(new ProxyMember("hasStereotype", null, ProxyFunction.HasStereotype));
            members.Add(new ProxyMember("hasTag", null, ProxyFunction.HasTag));
        }
        else if (typeof(RProcessNode).IsAssignableFrom(type))
        {
            members.Add(new ProxyMember("hasStereotype", null, ProxyFunction.HasStereotype));
        }

        if (type == typeof(ResolvedModel))
            members.Add(new ProxyMember("find", null, ProxyFunction.Find));
        return new MemberTable(members);
    }

    private static bool IsHidden(Type type, string property) =>
        string.Equals(property, nameof(IResolvedObject.Dependencies), StringComparison.Ordinal)
        || (type == typeof(ResolvedModel) && property is nameof(ResolvedModel.Source) or nameof(ResolvedModel.Settings) or nameof(ResolvedModel.Diagnostics));

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

    /// <summary>camelCase of a PascalCase CLR name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The camelCase name.</returns>
    public static string Camel(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : string.Concat(char.ToLowerInvariant(name[0]).ToString(), name.AsSpan(1));

    private static string Kebab(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
