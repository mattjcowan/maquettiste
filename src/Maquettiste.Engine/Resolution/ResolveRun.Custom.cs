using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Custom elements (next-phase-2-design.md section 4.2). Runs before <see cref="LinkOperationRequires"/>, so an operation's
/// <c>requires</c> resolves a custom element or a member. Each <see cref="RCustom"/> depends on its file and on its custom kind file
/// (<c>c:&lt;type&gt;</c>); each <see cref="RMember"/> on the same two keys.
/// </summary>
internal sealed partial class ResolveRun
{
    private const string CustomKind = "k:custom";

    private List<RCustom> _customOrder = [];

    /// <summary>The dependency key of a custom kind file: its content hash, or absent when the project declares no such kind.</summary>
    /// <param name="type">The custom kind's type.</param>
    /// <returns>The key.</returns>
    internal static string CustomKindKey(string type) => "c:" + type;

    /// <summary>Resolves every custom element, by (type, name, id).</summary>
    private void ResolveCustomElements()
    {
        // A custom kind is the file whose stem is its type (MQ1017 flags the others).
        var kinds = new Dictionary<string, CustomKind>(StringComparer.Ordinal);
        foreach (var document in Model.CustomKinds)
        {
            if (document.Path.EndsWith("/" + document.Kind.Type + ".json", StringComparison.Ordinal))
                kinds.TryAdd(document.Kind.Type, document.Kind);
        }

        var resolved = new List<RCustom>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var custom in Model.All<Custom>())
        {
            Ct.ThrowIfCancellationRequested();
            if (!seen.Add(custom.Id))
                continue;
            var r = new RCustom();
            BuildCustom(custom, r, kinds.GetValueOrDefault(custom.Type));
            Register(r);
            resolved.Add(r);
        }

        _customOrder = [.. resolved.OrderBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    private void BuildCustom(Custom custom, RCustom r, CustomKind? kind)
    {
        var deps = DepsOf(r);
        FillCommon(r, custom, deps);
        var kindKey = CustomKindKey(custom.Type);
        deps.Add(kindKey);
        r.Package = PackageOf(custom.Package);
        r.Type = custom.Type;
        r.TypeDisplayName = kind?.DisplayName ?? custom.Type;
        if (kind?.PluralName is { } plural)
        {
            r.TypePluralName = plural;
        }
        else
        {
            r.TypePluralName = Inflector.Pluralize(r.TypeDisplayName);
            deps.Add(InflectionKey);
        }

        var roots = Model.PropertySchemas.ContentOf(custom.Type)?.Members ?? System.Collections.Frozen.FrozenDictionary<string, XMemberNode>.Empty;
        string[] fileKeys = [.. new DependencySet(Keys).Element(custom.Id).Add(kindKey).ToList()];
        var members = new List<RMember>();
        var content = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in custom.Content.OrderBy(p => p.Key, StringComparer.Ordinal))
            content[key] = ContentValue(value, roots.GetValueOrDefault(key), key, r, null, members, fileKeys);
        r.Content = content.ToImmutable();
        r.Members = new RList<RMember>(members, fileKeys);
    }

    /// <summary>
    /// A content value as a template value, guided by the custom kind's member tree: an <c>x-member</c> array is a list of members
    /// (registered as found, a member before its nested ones), any other value <see cref="ResolutionValues.Plain(JsonElement)"/>.
    /// </summary>
    private object? ContentValue(JsonElement value, XMemberNode? node, string key, RCustom owner, RMember? parent, List<RMember> members, string[] fileKeys)
    {
        if (node is null)
            return ResolutionValues.Plain(value);
        switch (value.ValueKind)
        {
            case JsonValueKind.Array when node.Items is { IsMember: true } items:
                var list = new List<RMember>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                        continue;
                    var member = new RMember
                    {
                        Id = id.GetString()!,
                        Name = items.Named && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                        Array = key,
                        Owner = owner,
                        Parent = parent,
                    };
                    DepsOf(member).AddRange(fileKeys);
                    members.Add(member);
                    Register(member);
                    var own = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
                    foreach (var property in item.EnumerateObject().Where(p => p.Name != "id").OrderBy(p => p.Name, StringComparer.Ordinal))
                        own[property.Name] = ContentValue(property.Value, items.Properties?.GetValueOrDefault(property.Name), property.Name, owner, member, members, fileKeys);
                    member.Content = own.ToImmutable();
                    list.Add(member);
                }

                return new RList<RMember>(list, fileKeys);
            case JsonValueKind.Array when node.Items is { } items:
                return value.EnumerateArray().Select(item => ContentValue(item, items, key, owner, parent, members, fileKeys)).ToImmutableArray();
            case JsonValueKind.Object when node.Properties is { } properties:
                return value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToImmutableSortedDictionary(p => p.Name, p => ContentValue(p.Value, properties.GetValueOrDefault(p.Name), p.Name, owner, parent, members, fileKeys), StringComparer.Ordinal);
            default:
                return ResolutionValues.Plain(value);
        }
    }
}
