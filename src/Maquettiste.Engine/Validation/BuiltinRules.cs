using System.Globalization;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The built-in rules (MQ2xxx to MQ4xxx) for one element file. Every diagnostic is reported in the file where the fix belongs,
/// so validating a set of files yields exactly their diagnostics, and validating every file yields the whole model's.
/// Cross-file conflicts (duplicate names, two mappings for one target, a composition child with two owners) are reported on
/// every file after the ordinally first; a scoped run adds the other participants (<see cref="ModelValidator.ConflictPeers"/>).
/// Pure functions of the context.
/// </summary>
internal static partial class BuiltinRules
{
    /// <summary>Runs every built-in rule on one element file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the document to validate.</param>
    public static void Validate(ValidationContext context, Report report)
    {
        var element = report.Document.Element;
        CheckReferences(context, report);
        CheckCommon(context, element, "", element.KindName, report);
        CheckElementName(element, report);
        CheckScopedName(context, report);
        switch (element)
        {
            case Package package:
                CheckPackage(context, package, report);
                break;
            case Entity entity:
                CheckEntity(context, entity, report);
                MappingRules.CheckEntityPlacement(context, entity, report);
                break;
            case ValueObject valueObject:
                CheckValueObject(context, valueObject, report);
                break;
            case ScalarType scalar:
                CheckScalar(context, scalar, report);
                break;
            case EnumType enumType:
                CheckEnum(context, enumType, report);
                break;
            case Relation relation:
                CheckRelation(context, relation, report);
                break;
            case Database database:
                CheckDatabase(context, database, report);
                MappingRules.CheckConventionPackages(database, report);
                break;
            case Table table:
                PhysicalRules.CheckTable(context, table, report);
                break;
            case View view:
                PhysicalRules.CheckView(context, view, report);
                break;
            case Sequence sequence:
                PhysicalRules.CheckSequence(context, sequence, report);
                break;
            case Mapping mapping:
                MappingRules.CheckMapping(context, mapping, report);
                break;
            case TagVocabulary tags:
                CheckTagVocabulary(context, tags, report);
                break;
            case CategoryTree tree:
                CheckCategoryTree(context, tree, report);
                break;
            case Stereotype stereotype:
                CheckStereotype(context, stereotype, report);
                break;
            case ReferenceType referenceType:
                ReferenceDataRules.CheckReferenceType(context, referenceType, report);
                break;
            case Seed seed:
                ReferenceDataRules.CheckSeed(context, report.Document, seed, report);
                break;
        }
    }

    // ---- MQ2001, MQ2002, MQ2005: references ----

    private static void CheckReferences(ValidationContext context, Report report)
    {
        var model = context.Model;
        foreach (var site in context.Walker.Walk(report.Document.Element))
        {
            var reference = site.Reference;
            if (reference.Keyed)
                continue; // physical keys: MQ4007, MQ4008 and the referenced-table check in PhysicalRules
            if (site.DeclaringType == typeof(EntityKey) || site.DeclaringType == typeof(AlternateKey))
                continue; // key attributes: MQ3006
            var isCategory = (site.DeclaringType == typeof(ElementBase) && site.PropertyName == nameof(ElementBase.Category))
                || (site.DeclaringType == typeof(Category) && site.PropertyName == nameof(Category.Parent));
            if (isCategory)
            {
                CheckCategoryReference(context, site, report);
                continue;
            }

            if (!model.TryGetEntry(site.Value, out var entry))
            {
                report.Add("MQ2001", $"'{JsonName(site)}' references '{site.Value}', which does not exist.", site.Pointer, site.FromId);
                continue;
            }

            if (!reference.IsAny && !reference.IndexKinds.Contains(entry.Kind, StringComparer.Ordinal)
                && !reference.Targets.Any(k => KindInfo.Get(k).Name == entry.Kind))
            {
                var expected = string.Join(" or ", reference.Targets.Select(k => KindInfo.Get(k).Name).Concat(reference.IndexKinds));
                report.Add("MQ2002", $"'{JsonName(site)}' references '{site.Value}', {Article(entry.Kind)} {entry.Kind}; expected {Article(expected)} {expected}.", site.Pointer, site.FromId);
            }
        }
    }

    private static void CheckCategoryReference(ValidationContext context, ReferenceSite site, Report report)
    {
        var model = context.Model;
        if (!model.TryGetEntry(site.Value, out var entry))
        {
            report.Add("MQ2005", $"Category '{site.Value}' does not exist in the category tree.", site.Pointer, site.FromId);
        }
        else if (entry.Kind != "category")
        {
            report.Add("MQ2005", $"'{site.Value}' is {Article(entry.Kind)} {entry.Kind}, not a category.", site.Pointer, site.FromId);
        }
        else if (model.CategoryTrees.FirstOrDefault(t => t.Id == entry.OwnerId) is not { } tree)
        {
            report.Add("MQ2005", $"Category '{site.Value}' belongs to a category tree that is ignored (MQ1009).", site.Pointer, site.FromId);
        }
        else if (!model.VocabularyChain(VocabularyScope(report.Document.Element)).Contains(tree.Package))
        {
            report.Add("MQ2008", $"Category '{site.Value}' is declared in the category tree of {ScopeName(model, tree.Package)}, outside this element's domain chain.", site.Pointer, site.FromId);
        }
    }

    private static string JsonName(ReferenceSite site)
    {
        if (site.DeclaringType == typeof(ReferenceType) && site.PropertyName == nameof(ReferenceType.Storage))
            return "storage";
        var segments = Ptr.Split(site.Pointer);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return segments[i];
        }

        return site.PropertyName;
    }

    /// <summary>Returns <c>a</c> or <c>an</c> for a noun.</summary>
    /// <param name="noun">The noun.</param>
    /// <returns>The article.</returns>
    public static string Article(string noun) => noun.Length > 0 && "aeiou".Contains(noun[0], StringComparison.Ordinal) ? "an" : "a";

    // ---- MQ2003, MQ2004, MQ2006: stereotypes and tags on any element or sub-element ----

    /// <summary>Checks the stereotypes and tags of an element or sub-element.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="item">The element or sub-element.</param>
    /// <param name="pointer">Its pointer.</param>
    /// <param name="kindName">Its kind name for <c>appliesTo</c> (element kind, <c>attribute</c>, <c>enum-member</c>, <c>column</c>…).</param>
    /// <param name="report">The report.</param>
    public static void CheckCommon(ValidationContext context, ElementBase item, string pointer, string kindName, Report report)
    {
        var model = context.Model;
        for (var i = 0; i < item.Stereotypes.Count; i++)
        {
            var key = item.Stereotypes[i];
            var at = Ptr.At(pointer + "/stereotypes", i);
            if (model.GetStereotype(key) is not { } stereotype)
                report.Add("MQ2003", $"Stereotype '{key}' is not defined in model/vocabularies/stereotypes.", at, item.Id);
            else if (stereotype.AppliesTo.Count > 0 && !stereotype.AppliesTo.Contains(kindName, StringComparer.Ordinal))
                report.Add("MQ2004", $"Stereotype '{key}' applies to {string.Join(", ", stereotype.AppliesTo)}, not to {Article(kindName)} {kindName}.", at, item.Id);
        }

        if (item.Tags.Count > 0 && model.TagVocabularies.Any())
        {
            // The vocabularies on the element's domain chain (explorer-redesign.md section 1.11): its domain, each enclosing one, global.
            var chain = model.VocabularyChain(VocabularyScope(report.Document.Element)).Select(model.TagVocabularyOf).OfType<TagVocabulary>().ToList();
            var strict = chain.Any(v => v.Strict);
            for (var i = 0; i < item.Tags.Count; i++)
            {
                var tag = item.Tags[i];
                if (chain.Any(v => v.Definitions.Any(d => d.Key == tag)))
                    continue;
                var at = Ptr.At(pointer + "/tags", i);
                if (model.TagVocabularies.FirstOrDefault(v => v.Definitions.Any(d => d.Key == tag)) is { } outside)
                    report.Add("MQ2008", $"Tag '{tag}' is declared only in the tag vocabulary of {ScopeName(model, outside.Package)}, outside this element's domain chain.", at, item.Id);
                else if (chain.Count > 0)
                    report.Add("MQ2006", strict ? DiagnosticSeverity.Error : DiagnosticSeverity.Info, $"Tag '{tag}' is not declared in the tag vocabulary{(chain.Count > 1 ? " of any domain on its chain" : "")}{(strict ? ", which is strict" : "")}.", at, item.Id);
            }
        }
    }

    /// <summary>
    /// The package whose vocabulary chain an element sees: a package itself, a vocabulary's package, else the element's package
    /// (<see langword="null"/> for elements outside the domains, which see only the global vocabularies).
    /// </summary>
    internal static string? VocabularyScope(Element element) => element switch
    {
        Package package => package.Id,
        _ => ModelIndexer.PackageOf(element),
    };

    private static string ScopeName(ModelSnapshot model, string? packageId) =>
        packageId is null ? "the model (global)" : $"domain '{(model.GetDocument(packageId)?.Element.Name is { Length: > 0 } name ? name : packageId)}'";

    // ---- MQ3021: a domain vocabulary repeats a tag key or category name of the global vocabulary or an enclosing domain's ----

    private static void CheckVocabularyClash<T>(ModelSnapshot model, string? package, IReadOnlyList<T> items, Func<T, string> key, string collection,
        string member, string noun, Func<string?, IEnumerable<string>> declaredIn, Func<T, string?> itemId, Report report)
    {
        if (package is null || items.Count == 0)
            return;
        var enclosing = model.VocabularyChain(package).Skip(1).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var value = key(items[i]);
            foreach (var scope in enclosing)
            {
                if (!declaredIn(scope).Contains(value, StringComparer.Ordinal))
                    continue;
                report.Add("MQ3021", $"{noun} '{value}' is already declared by {ScopeName(model, scope)}; a domain vocabulary cannot redeclare it.",
                    Ptr.At(collection, i) + member, itemId(items[i]));
                break;
            }
        }
    }

    // ---- MQ3018: names ----

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabKey();

    /// <summary>Whether a name is a conceptual identifier (<c>^[A-Za-z_][A-Za-z0-9_]*$</c>).</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsIdentifier(string name) => Identifier().IsMatch(name);

    /// <summary>Whether a name is a label: 1 to 256 characters without control characters.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsLabel(string name) => name.Length is > 0 and <= 256 && !name.Any(char.IsControl);

    /// <summary>Checks a conceptual identifier (MQ3018).</summary>
    /// <param name="name">The name.</param>
    /// <param name="what">What is named, for the message.</param>
    /// <param name="pointer">The pointer of the name.</param>
    /// <param name="elementId">The element or sub-element id.</param>
    /// <param name="report">The report.</param>
    public static void CheckIdentifier(string name, string what, string pointer, string elementId, Report report)
    {
        if (!IsIdentifier(name))
            report.Add("MQ3018", $"'{name}' is not a valid {what} name: use letters, digits and underscores, not starting with a digit.", pointer, elementId);
    }

    /// <summary>Checks a label (MQ3018).</summary>
    /// <param name="name">The name.</param>
    /// <param name="what">What is named, for the message.</param>
    /// <param name="pointer">The pointer of the name.</param>
    /// <param name="elementId">The element or sub-element id.</param>
    /// <param name="report">The report.</param>
    public static void CheckLabel(string name, string what, string pointer, string elementId, Report report)
    {
        if (!IsLabel(name))
            report.Add("MQ3018", $"'{name}' is not a valid {what} name: use 1 to 256 characters without control characters.", pointer, elementId);
    }

    private static void CheckElementName(Element element, Report report)
    {
        switch (element)
        {
            case Package or Entity or ValueObject or ScalarType or EnumType:
                CheckIdentifier(element.Name, element.KindName, "/name", element.Id, report);
                break;
            case Table { Origin: TableOrigin.Synthesized, Name.Length: 0 }:
                break; // an overlay keeps the conventional name
            case Stereotype stereotype:
                CheckLabel(stereotype.Name, "stereotype", "/name", element.Id, report);
                if (!KebabKey().IsMatch(stereotype.Key))
                    report.Add("MQ3018", $"'{stereotype.Key}' is not a valid stereotype key: use lower-case kebab-case.", "/key", element.Id);
                break;
            default:
                CheckLabel(element.Name, element.KindName, "/name", element.Id, report);
                break;
        }
    }

    // ---- MQ3001: duplicate names in scope ----

    private static void CheckScopedName(ValidationContext context, Report report)
    {
        foreach (var (key, space) in ValidationContext.NameKeys(report.Document.Element))
        {
            var first = context.FirstWithName(key);
            if (first != report.Document)
            {
                var (what, pointer) = report.Document.Element is Stereotype s ? ("Key '" + s.Key + "'", "/key") : ("Name '" + report.Document.Element.Name + "'", "/name");
                report.Add("MQ3001", $"{what} is already used by {first.Path} among {space}.", pointer);
            }
        }
    }

    private static void CheckDuplicates<T>(IReadOnlyList<T> items, Func<T, string?> name, string listPointer, string namePointer, string what,
        Func<T, string?> id, Report report, StringComparer? comparer = null, string rule = "MQ3001")
    {
        var seen = new Dictionary<string, int>(comparer ?? StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (name(items[i]) is not { Length: > 0 } n)
                continue;
            if (!seen.TryAdd(n, i))
            {
                report.Add(rule, string.Create(CultureInfo.InvariantCulture, $"{what} '{n}' is already used at index {seen[n]}."),
                    Ptr.At(listPointer, i) + namePointer, id(items[i]));
            }
        }
    }

    // ---- packages ----

    private static void CheckPackage(ValidationContext context, Package package, Report report)
    {
        // MQ3003: walk the parent chain; report when it comes back to this package.
        var seen = new HashSet<string>(StringComparer.Ordinal) { package.Id };
        for (var current = package.Parent; current is not null;)
        {
            if (current == package.Id)
            {
                report.Add("MQ3003", $"Package '{package.Name}' is its own ancestor through 'parent'.", "/parent");
                return;
            }

            if (!seen.Add(current) || context.Model.Get<Package>(current) is not { } parent)
                return;
            current = parent.Parent;
        }
    }

    // ---- entities ----

    private static void CheckEntity(ValidationContext context, Entity entity, Report report)
    {
        var model = context.Model;

        // MQ3002: inheritance cycle through 'base'.
        var inCycle = false;
        var seen = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
        for (var current = entity.Base; current is not null;)
        {
            if (current == entity.Id)
            {
                report.Add("MQ3002", $"Entity '{entity.Name}' inherits from itself through 'base'.", "/base");
                inCycle = true;
                break;
            }

            if (!seen.Add(current) || model.Get<Entity>(current) is not { } baseEntity)
                break;
            current = baseEntity.Base;
        }

        // MQ3005: a concrete root entity needs a key.
        if (entity.Key is null && !entity.Abstract && entity.Base is null)
            report.Add("MQ3005", $"Entity '{entity.Name}' has no key; add 'key', or make it abstract or derived.", "");

        var flat = context.Flatten(entity);
        var attributeIds = flat.Select(a => a.Attribute.Id).ToHashSet(StringComparer.Ordinal);

        // MQ3006: key attributes must be attributes of the entity (own, inherited or virtual).
        if (entity.Key is { } key)
            CheckKeyAttributes(key.Attributes, "/key/attributes", "key", entity, attributeIds, report);
        for (var k = 0; k < entity.AlternateKeys.Count; k++)
        {
            var alternate = entity.AlternateKeys[k];
            CheckKeyAttributes(alternate.Attributes, Ptr.At("/alternateKeys", k) + "/attributes", "alternate key '" + alternate.Name + "'", entity, attributeIds, report);
        }

        CheckDuplicates(entity.AlternateKeys, a => a.Name, "/alternateKeys", "/name", "Alternate key name", a => a.Id, report);

        // MQ3007: duplicate attribute names, inherited and virtual included (skipped inside an inheritance cycle).
        if (!inCycle)
            CheckFlatDuplicates(entity, flat, report);

        CheckAttributes(context, entity.Attributes, report);
    }

    private static void CheckKeyAttributes(IReadOnlyList<string> ids, string pointer, string what, Entity entity, HashSet<string> attributeIds, Report report)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (!attributeIds.Contains(ids[i]))
                report.Add("MQ3006", $"The {what} of entity '{entity.Name}' names '{ids[i]}', which is not one of its attributes.", Ptr.At(pointer, i));
        }
    }

    private static void CheckFlatDuplicates(ElementBase owner, IReadOnlyList<FlatAttribute> flat, Report report)
    {
        var seen = new Dictionary<string, FlatAttribute>(StringComparer.Ordinal);
        foreach (var item in flat)
        {
            var name = item.Attribute.Name;
            if (name.Length == 0)
                continue;
            if (!seen.TryGetValue(name, out var first))
            {
                seen[name] = item;
                continue;
            }

            if (item.Source == AttributeSource.Inherited)
                continue; // reported on the base entity
            var from = first.Source switch
            {
                AttributeSource.Inherited => "an inherited attribute",
                AttributeSource.Virtual => "a virtual attribute of stereotype '" + first.From + "'",
                _ => "another attribute",
            };
            if (item.Source == AttributeSource.Own)
            {
                report.Add("MQ3007", $"Attribute name '{name}' is already used by {from}.", Ptr.At("/attributes", item.OwnIndex), item.Attribute.Id);
            }
            else
            {
                var index = IndexOf(owner.Stereotypes, item.From);
                report.Add("MQ3007", $"Stereotype '{item.From}' adds attribute '{name}', which is already used by {from}.", Ptr.At("/stereotypes", index), owner.Id);
            }
        }
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return i;
        }

        return 0;
    }

    private static void CheckAttributes(ValidationContext context, IReadOnlyList<ModelAttribute> attributes, Report report)
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            var pointer = Ptr.At("/attributes", i);
            CheckCommon(context, attribute, pointer, "attribute", report);
            CheckIdentifier(attribute.Name, "attribute", pointer + "/name", attribute.Id, report);
            AttributeRules.Check(context, attribute, pointer, report);
        }
    }

    // ---- value objects, scalar types, enums ----

    private static void CheckValueObject(ValidationContext context, ValueObject valueObject, Report report)
    {
        var flat = valueObject.Attributes.Select((a, i) => new FlatAttribute(a, AttributeSource.Own, valueObject.Id, i))
            .Concat(context.VirtualAttributes(valueObject)).ToList();
        CheckFlatDuplicates(valueObject, flat, report);
        CheckAttributes(context, valueObject.Attributes, report);

        // MQ3015: a containment edge that leads back to this value object.
        for (var i = 0; i < valueObject.Attributes.Count; i++)
        {
            var attribute = valueObject.Attributes[i];
            if (attribute.Type.Ref is { } target && context.Model.Get<ValueObject>(target) is { } contained
                && context.ValueObjectReaches(contained.Id, valueObject.Id))
            {
                report.Add("MQ3015", $"Attribute '{attribute.Name}' contains value object '{contained.Name}', which contains '{valueObject.Name}' again.",
                    Ptr.At("/attributes", i) + "/type/ref", attribute.Id);
            }
        }
    }

    private static void CheckScalar(ValidationContext context, ScalarType scalar, Report report)
    {
        if (!BuiltinTypes.IsBuiltin(scalar.Base))
        {
            report.Add("MQ3014", $"Scalar type '{scalar.Name}' has base '{scalar.Base}', which is not a built-in type ({string.Join(", ", BuiltinTypes.All)}).", "/base");
            return;
        }

        var type = new EffectiveType(scalar.Base, null, null, null);
        AttributeRules.CheckFacets(type, scalar.Length, scalar.Precision, scalar.Scale, scalar.Validation, "Scalar type '" + scalar.Name + "'", "", scalar.Id, report);
        AttributeRules.CheckRuleIds(context, scalar.Validation, "/validation/rules", scalar.Id, report);
    }

    private static void CheckEnum(ValidationContext context, EnumType enumType, Report report)
    {
        for (var i = 0; i < enumType.Members.Count; i++)
        {
            var member = enumType.Members[i];
            var pointer = Ptr.At("/members", i);
            CheckCommon(context, member, pointer, "enum-member", report);
            CheckIdentifier(member.Name, "enum member", pointer + "/name", member.Id, report);
            if (enumType.Flags && member.Value is { } value && value != 0 && (value < 0 || (value & (value - 1)) != 0))
            {
                report.Add("MQ3012", string.Create(CultureInfo.InvariantCulture, $"Member '{member.Name}' of flags enum '{enumType.Name}' has value {value}, which is not a power of two."),
                    pointer + "/value", member.Id);
            }
        }

        CheckDuplicates(enumType.Members, m => m.Name, "/members", "/name", "Member name", m => m.Id, report, rule: "MQ3012");
        CheckDuplicates(enumType.Members, m => m.Code, "/members", "/code", "Member code", m => m.Id, report, rule: "MQ3012");
    }

    // ---- relations ----

    private static void CheckRelation(ValidationContext context, Relation relation, Report report)
    {
        var model = context.Model;
        var ends = relation.Ends;

        // MQ3008: two ends, or three or more for n-ary.
        if (relation.RelationKind == RelationKind.NAry ? ends.Count < 3 : ends.Count != 2)
        {
            var expected = relation.RelationKind == RelationKind.NAry ? "three or more ends" : "exactly two ends (use n-ary for more)";
            report.Add("MQ3008", string.Create(CultureInfo.InvariantCulture, $"Relation '{relation.Name}' is {KindText(relation.RelationKind)} with {ends.Count} ends; it needs {expected}."), "/ends");
        }

        CheckDuplicates(ends, e => e.Role, "/ends", "/role", "Role", e => e.Id, report);

        for (var i = 0; i < ends.Count; i++)
        {
            var end = ends[i];
            var pointer = Ptr.At("/ends", i);
            CheckIdentifier(end.Role, "role", pointer + "/role", end.Id, report);
            if (end.Navigation.Length > 0)
                CheckIdentifier(end.Navigation, "navigation", pointer + "/navigation", end.Id, report);

            // MQ3010: cardinality.
            if (end.Min is < 0 or > 1)
                report.Add("MQ3010", string.Create(CultureInfo.InvariantCulture, $"End '{end.Role}' has min {end.Min}; min is 0 or 1."), pointer + "/min", end.Id);
            if (end.Ordered && end.Max == MaxCardinality.One)
                report.Add("MQ3010", $"End '{end.Role}' is ordered but has max 1; only a collection end (max \"*\") keeps a position.", pointer + "/ordered", end.Id);

            // MQ3011: set-null on a required end.
            if (end.OnDelete == ReferentialIntent.SetNull && end.Min == 1)
                report.Add("MQ3011", $"End '{end.Role}' is required (min 1), so deleting it cannot set the other ends' references to null.", pointer + "/onDelete", end.Id);

            // MQ3009: navigation collisions on the entities that carry it.
            if (end.Navigation.Length > 0)
            {
                foreach (var holderId in ValidationContext.NavigationHolders(relation, i).Distinct(StringComparer.Ordinal))
                    CheckNavigation(context, relation, i, holderId, report);
            }
        }

        if (relation.RelationKind == RelationKind.Composition && ends.Count == 2)
        {
            if (ValidationContext.CompositionEnds(relation) is not { } composition)
            {
                report.Add("MQ3010", $"Composition '{relation.Name}' has no owner end: one end needs max 1.", "/ends");
            }
            else
            {
                // MQ3016: the child is part of another composition too.
                var child = ends[composition.Child].Entity;
                var owners = context.CompositionsOwning(child);
                if (owners.Length > 1 && owners[0] != report.Document)
                {
                    var childName = model.Get<Entity>(child)?.Name ?? child;
                    report.Add("MQ3016", $"Entity '{childName}' is already owned through composition {owners[0].Path}; a composition child has one owner.",
                        Ptr.At("/ends", composition.Child) + "/entity", ends[composition.Child].Id);
                }
            }
        }

        var flat = relation.Attributes.Select((a, i) => new FlatAttribute(a, AttributeSource.Own, relation.Id, i))
            .Concat(context.VirtualAttributes(relation)).ToList();
        CheckFlatDuplicates(relation, flat, report);
        CheckAttributes(context, relation.Attributes, report);
        MappingRules.CheckRelationBindings(context, relation, report);
    }

    private static string KindText(RelationKind kind) => kind switch
    {
        RelationKind.Aggregation => "an aggregation",
        RelationKind.Composition => "a composition",
        RelationKind.NAry => "n-ary",
        _ => "an association",
    };

    private static void CheckNavigation(ValidationContext context, Relation relation, int endIndex, string holderId, Report report)
    {
        if (context.Model.Get<Entity>(holderId) is not { } holder)
            return;
        var end = relation.Ends[endIndex];
        var name = end.Navigation;
        var pointer = Ptr.At("/ends", endIndex) + "/navigation";
        if (name == holder.Name)
        {
            report.Add("MQ3009", $"Navigation '{name}' on entity '{holder.Name}' has the entity's own name.", pointer, end.Id);
            return;
        }

        // Attributes: the holder's flattened list (inherited and virtual included), then its descendants', which inherit the navigation.
        foreach (var attribute in context.Flatten(holder))
        {
            if (attribute.Attribute.Name == name)
            {
                report.Add("MQ3009", $"Navigation '{name}' collides with attribute '{name}' of entity '{holder.Name}'.", pointer, end.Id);
                return;
            }
        }

        var ancestors = context.Ancestors(holder).ToList();
        var descendants = context.Descendants(holderId).ToList();
        foreach (var descendant in descendants)
        {
            foreach (var attribute in context.Flatten(descendant))
            {
                if (attribute.Attribute.Name == name)
                {
                    report.Add("MQ3009", $"Navigation '{name}' on entity '{holder.Name}' collides with attribute '{name}' of derived entity '{descendant.Name}', which inherits the navigation.", pointer, end.Id);
                    return;
                }
            }
        }

        // Navigations with the same name on the holder, its ancestors (inherited) and its descendants (which inherit this one):
        // the first in (relation path, end) order keeps the name.
        NavigationSite? first = null;
        Entity? firstHolder = null;
        foreach (var member in ancestors.Prepend(holder).Concat(descendants))
        {
            foreach (var site in context.NavigationsOn(member.Id))
            {
                if (site.Name == name && (first is null || Compare(site, first) < 0))
                    (first, firstHolder) = (site, member);
            }
        }

        if (first is null || (first.Document == report.Document && first.EndIndex == endIndex))
            return; // this navigation is the first with the name
        var other = first.Document == report.Document ? "another end of this relation" : first.Document.Path;
        var where = firstHolder!.Id == holder.Id ? ""
            : ancestors.Any(a => a.Id == firstHolder.Id) ? $" on base entity '{firstHolder.Name}'"
            : $" on derived entity '{firstHolder.Name}'";
        report.Add("MQ3009", $"Navigation '{name}' on entity '{holder.Name}' is already generated{where} by {other}.", pointer, end.Id);
    }

    private static int Compare(NavigationSite a, NavigationSite b)
    {
        var c = string.CompareOrdinal(a.Document.Path, b.Document.Path);
        return c != 0 ? c : a.EndIndex.CompareTo(b.EndIndex);
    }

    // ---- databases ----

    private static void CheckDatabase(ValidationContext context, Database database, Report report)
    {
        CheckDuplicates(database.Schemas, s => s.Name, "/schemas", "/name", "Schema name", s => s.Id, report);
        var limit = DialectInfo.IdentifierLimit(database);
        for (var i = 0; i < database.Schemas.Count; i++)
        {
            var schema = database.Schemas[i];
            var pointer = Ptr.At("/schemas", i);
            CheckCommon(context, schema, pointer, "schema", report);
            CheckLabel(schema.Name, "schema", pointer + "/name", schema.Id, report);
            PhysicalRules.CheckIdentifierLength(database, limit, schema.Name, "Schema", pointer + "/name", schema.Id, report);
        }
    }

    // ---- vocabularies ----

    private static void CheckTagVocabulary(ValidationContext context, TagVocabulary tags, Report report)
    {
        CheckDuplicates(tags.Definitions, d => d.Key, "/definitions", "/key", "Tag", _ => null, report);
        var model = context.Model;
        CheckVocabularyClash(model, tags.Package, tags.Definitions, d => d.Key, "/definitions", "/key", "Tag",
            scope => model.TagVocabularyOf(scope)?.Definitions.Select(d => d.Key) ?? [], _ => null, report);
    }

    private static void CheckCategoryTree(ValidationContext context, CategoryTree tree, Report report)
    {
        var byId = new Dictionary<string, Category>(StringComparer.Ordinal);
        foreach (var category in tree.Categories)
            byId.TryAdd(category.Id, category);

        var siblings = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tree.Categories.Count; i++)
        {
            var category = tree.Categories[i];
            var pointer = Ptr.At("/categories", i);
            CheckCommon(context, category, pointer, "category", report);
            CheckLabel(category.Name, "category", pointer + "/name", category.Id, report);

            // MQ3001: sibling names are unique.
            if (category.Name.Length > 0 && !siblings.TryAdd(category.Parent + "|" + category.Name, i))
            {
                report.Add("MQ3001", string.Create(CultureInfo.InvariantCulture, $"Category name '{category.Name}' is already used by a sibling at index {siblings[category.Parent + "|" + category.Name]}."),
                    pointer + "/name", category.Id);
            }

            // MQ3004: the parent chain comes back to this category.
            var seen = new HashSet<string>(StringComparer.Ordinal) { category.Id };
            for (var current = category.Parent; current is not null;)
            {
                if (current == category.Id)
                {
                    report.Add("MQ3004", $"Category '{category.Name}' is its own ancestor through 'parent'.", pointer + "/parent", category.Id);
                    break;
                }

                if (!seen.Add(current) || !byId.TryGetValue(current, out var parent))
                    break;
                current = parent.Parent;
            }
        }
    
        var model = context.Model;
        CheckVocabularyClash(model, tree.Package, tree.Categories, c => c.Name, "/categories", "/name", "Category",
            scope => model.CategoryTreeOf(scope)?.Categories.Select(c => c.Name) ?? [], c => c.Id, report);
    }

    private static void CheckStereotype(ValidationContext context, Stereotype stereotype, Report report)
    {
        var flat = stereotype.Attributes.Select((a, i) => new FlatAttribute(a, AttributeSource.Own, stereotype.Id, i)).ToList();
        CheckFlatDuplicates(stereotype, flat, report);
        CheckAttributes(context, stereotype.Attributes, report);
    }
}
