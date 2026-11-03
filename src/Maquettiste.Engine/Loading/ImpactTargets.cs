using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// The targets and referrers of a dry run's impact (next-phase-2-design.md section 6.1, decision 7). The targets are the deleted
/// elements and sub-elements, and those whose own fields changed: an object is compared with its id-bearing child objects reduced to
/// their ids, so a renamed attribute is a target and its entity is not. Created elements have none. The referrers are each target's
/// references on the model before the change, minus those held by the documents the batch writes or deletes (they are in the dry
/// run's files). Only the changed documents are walked.
/// </summary>
internal static class ImpactTargets
{
    /// <summary>The change of a target whose own fields changed.</summary>
    public const string Changed = "changed";

    /// <summary>The change of a deleted target.</summary>
    public const string Deleted = "deleted";

    /// <summary>The targets of a change, ordinal by id, each with <see cref="Changed"/> or <see cref="Deleted"/>.</summary>
    /// <param name="before">The model before the change.</param>
    /// <param name="candidate">The candidate model.</param>
    /// <param name="changed">The ids of the elements the change created, updated, deleted or rewrote.</param>
    /// <returns>The targets.</returns>
    public static SortedDictionary<string, string> Of(ModelSnapshot before, ModelSnapshot candidate, IEnumerable<string> changed)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(changed);
        var targets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var walked = new Dictionary<ElementDocument, Dictionary<string, JsonElement>>(ReferenceEqualityComparer.Instance);
        Dictionary<string, JsonElement> ObjectsOf(ElementDocument document)
        {
            if (!walked.TryGetValue(document, out var objects))
            {
                walked[document] = objects = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                Collect(document.Json, objects);
            }

            return objects;
        }

        foreach (var id in changed)
        {
            if (before.Get<Element>(id) is null || before.GetDocument(id) is not { } then)
                continue; // created: no referrers
            var now = candidate.Get<Element>(id) is null ? null : candidate.GetDocument(id);
            var after = now is null ? null : ObjectsOf(now);
            foreach (var (objectId, old) in ObjectsOf(then))
            {
                if (!before.TryGetEntry(objectId, out var entry) || !string.Equals(entry.OwnerId, id, StringComparison.Ordinal))
                    continue;
                JsonElement current;
                if (after is not null && after.TryGetValue(objectId, out var found))
                {
                    current = found;
                }
                else if (candidate.TryGetEntry(objectId, out _) && candidate.GetDocument(objectId) is { } moved
                    && ObjectsOf(moved).TryGetValue(objectId, out found))
                {
                    current = found; // moved to another document
                }
                else
                {
                    targets[objectId] = Deleted;
                    continue;
                }

                if (!SameOwnFields(old, current, true))
                    targets[objectId] = Changed;
            }
        }

        return targets;
    }

    /// <summary>
    /// The referrers of a change's targets on the model before it, by target, referring element and pointer, every one of them (the
    /// caller caps the list).
    /// </summary>
    /// <param name="before">The model before the change.</param>
    /// <param name="candidate">The candidate model.</param>
    /// <param name="changed">The ids of the elements the change created, updated, deleted or rewrote.</param>
    /// <param name="excluded">The element ids whose documents the dry run writes or deletes: their references are left out.</param>
    /// <returns>The referrers.</returns>
    public static List<ImpactReferrer> Referrers(ModelSnapshot before, ModelSnapshot candidate, IEnumerable<string> changed, IReadOnlySet<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        var referrers = new List<ImpactReferrer>();
        foreach (var (target, change) in Of(before, candidate, changed))
        {
            foreach (var reference in before.ReferencesTo(target)
                .OrderBy(r => r.FromElementId, StringComparer.Ordinal).ThenBy(r => r.JsonPointer, StringComparer.Ordinal))
            {
                if (excluded.Contains(reference.FromElementId) || before.Get<Element>(reference.FromElementId) is not { } from)
                    continue;
                referrers.Add(new ImpactReferrer(target, change, from.KindName, from.Name.Length > 0 ? from.Name : null, reference));
            }
        }

        return referrers;
    }

    /// <summary>Every object of a document with a string <c>id</c>, the root included, by id (the first one wins).</summary>
    private static void Collect(JsonElement value, Dictionary<string, JsonElement> objects)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (IdOf(value) is { } id)
                    objects.TryAdd(id, value);
                foreach (var property in value.EnumerateObject())
                    Collect(property.Value, objects);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    Collect(item, objects);
                break;
        }
    }

    private static string? IdOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    /// <summary>
    /// Whether two objects have the same own fields: below the root, an object with an id counts as its id alone (a renamed attribute
    /// changes the attribute, not its entity); every other value compares in full, properties by name.
    /// </summary>
    private static bool SameOwnFields(JsonElement a, JsonElement b, bool root)
    {
        if (!root)
        {
            var idA = IdOf(a);
            var idB = IdOf(b);
            if (idA is not null || idB is not null)
                return string.Equals(idA, idB, StringComparison.Ordinal);
        }

        if (a.ValueKind != b.ValueKind)
            return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var count = 0;
                foreach (var property in a.EnumerateObject())
                {
                    count++;
                    if (!b.TryGetProperty(property.Name, out var other) || !SameOwnFields(property.Value, other, false))
                        return false;
                }

                foreach (var _ in b.EnumerateObject())
                    count--;
                return count == 0;
            }

            case JsonValueKind.Array:
            {
                if (a.GetArrayLength() != b.GetArrayLength())
                    return false;
                using var left = a.EnumerateArray();
                using var right = b.EnumerateArray();
                while (left.MoveNext() && right.MoveNext())
                {
                    if (!SameOwnFields(left.Current, right.Current, false))
                        return false;
                }

                return true;
            }

            case JsonValueKind.String:
                return a.ValueEquals(b.GetString());
            default:
                return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
        }
    }
}
