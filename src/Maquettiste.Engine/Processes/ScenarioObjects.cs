using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// A scenario's or a simulation's objects as the interpreter sees them (lifecycle-design.md decision 22): the subject's row, and the
/// checks of the values its objects give.
/// </summary>
internal static class ScenarioObjects
{
    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();
    private static readonly JsonElement NoObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// The subject's row: the attributes of its entity and its bases by name (the most derived declaration of a name wins), each the value
    /// given, else the declaring attribute's default, else null; then each link nested by its end's navigation name, an object for a to-one
    /// end (the first link wins) and a list in link order for a to-many one. A link whose end has no navigation, whose object is not one of
    /// the objects, or whose object is already on the path is left out. An empty object when there is no subject.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="objects">The objects.</param>
    /// <param name="subject">The id of the subject among them.</param>
    /// <returns>The row.</returns>
    public static JsonElement Row(ModelSnapshot model, IReadOnlyList<ScenarioObject> objects, string? subject)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(objects);
        if (subject is null || objects.Count == 0)
            return NoObject;
        var byId = new Dictionary<string, ScenarioObject>(StringComparer.Ordinal);
        foreach (var obj in objects)
            byId.TryAdd(obj.Id, obj);
        return byId.TryGetValue(subject, out var root) ? RowOf(model, root, byId, new HashSet<string>(StringComparer.Ordinal)) : NoObject;
    }

    /// <summary>Why a value an object gives is bad (a key that is not an attribute of its entity or its bases, or a value that does not
    /// fit its attribute), with the value's pointer under the object, or <see langword="null"/>. An object whose entity is not in the model
    /// is left to MQ2001.</summary>
    /// <param name="model">The model.</param>
    /// <param name="objects">The objects.</param>
    /// <param name="fits">Why a value does not fit an attribute, or <see langword="null"/>.</param>
    /// <returns>The first problem: its message and its pointer.</returns>
    public static (string Message, string Pointer)? Check(ModelSnapshot model, IReadOnlyList<ScenarioObject> objects, Func<ModelAttribute, JsonElement, string?> fits)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(fits);
        for (var i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            if (model.Get<Entity>(obj.Entity) is not { } entity)
                continue;
            var attributes = new Dictionary<string, ModelAttribute>(StringComparer.Ordinal);
            foreach (var attribute in Attributes(model, entity))
                attributes.TryAdd(attribute.Id, attribute);
            var pointer = "/objects/" + i.ToString(CultureInfo.InvariantCulture) + "/values";
            foreach (var (id, value) in obj.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!attributes.TryGetValue(id, out var attribute))
                    return ($"Object '{obj.Name}' gives a value for '{id}', which is not an attribute of entity '{entity.Name}' or its bases; remove it or name one of the entity's attributes.", pointer);
                if (fits(attribute, value) is { } why)
                    return ($"Object '{obj.Name}': the value of '{attribute.Name}' does not fit its type ({why}); give a value of the attribute's type.", pointer);
            }
        }

        return null;
    }

    /// <summary>The attributes of an entity and its bases, most derived first, each base once.</summary>
    /// <param name="model">The model.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The attributes.</returns>
    internal static IEnumerable<ModelAttribute> Attributes(ModelSnapshot model, Entity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null && seen.Add(current.Id); current = current.Base is { } b ? model.Get<Entity>(b) : null)
        {
            foreach (var attribute in current.Attributes)
                yield return attribute;
        }
    }

    private static JsonElement RowOf(ModelSnapshot model, ScenarioObject obj, Dictionary<string, ScenarioObject> byId, HashSet<string> path)
    {
        path.Add(obj.Id);
        var row = new SortedDictionary<string, object>(StringComparer.Ordinal);
        if (model.Get<Entity>(obj.Entity) is { } entity)
        {
            foreach (var attribute in Attributes(model, entity))
                row.TryAdd(attribute.Name, obj.Values.TryGetValue(attribute.Id, out var given) ? given : attribute.Default ?? Null);
        }

        var many = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var link in obj.Links)
        {
            if (End(model, link.End) is not { Navigation.Length: > 0 } end || !byId.TryGetValue(link.Object, out var target) || path.Contains(target.Id))
                continue;
            if (end.Max == MaxCardinality.One)
            {
                if (!row.ContainsKey(end.Navigation))
                    row[end.Navigation] = RowOf(model, target, byId, path);
                continue;
            }

            if (!many.TryGetValue(end.Navigation, out var list))
            {
                if (row.ContainsKey(end.Navigation))
                    continue;
                many[end.Navigation] = list = [];
                row[end.Navigation] = list;
            }

            list.Add(RowOf(model, target, byId, path));
        }

        path.Remove(obj.Id);
        return JsonSerializer.SerializeToElement(row);
    }

    private static RelationEnd? End(ModelSnapshot model, string endId) =>
        model.TryGetEntry(endId, out var entry) && model.Get<Relation>(entry.OwnerId) is { } relation
            ? relation.Ends.FirstOrDefault(e => string.Equals(e.Id, endId, StringComparison.Ordinal))
            : null;
}
