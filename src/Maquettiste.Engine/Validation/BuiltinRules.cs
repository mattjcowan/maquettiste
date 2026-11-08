using System.Globalization;
using System.Text.Json;
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
                ColumnTypeRules.CheckEntity(context, entity, report);
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
                ColumnTypeRules.CheckTable(context, table, report);
                break;
            case View view:
                PhysicalRules.CheckView(context, view, report);
                break;
            case Sequence sequence:
                PhysicalRules.CheckSequence(context, sequence, report);
                break;
            case Routine routine:
                DatabaseObjectRules.CheckRoutine(context, routine, report);
                break;
            case DatabaseType databaseType:
                DatabaseObjectRules.CheckDatabaseType(context, databaseType, report);
                break;
            case SqlObject sqlObject:
                DatabaseObjectRules.CheckSqlObject(context, sqlObject, report);
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
            case Diagram diagram:
                CheckDiagram(diagram, report);
                break;
            case Scenario scenario:
                // Objects are named <scenario>.<object> (lifecycle-design.md decision 22), so a name is one object's.
                CheckDuplicates(scenario.Objects, o => o.Name, "/objects", "/name", "Object name", o => o.Id, report);
                break;
        }

        VerbRules.Check(context, element, report);
        RightRules.Check(context, element, report);
        TriggerRules.Check(context, element, report);
        ApprovalRules.Check(context, element, report);
    }

    // ---- MQ3022: a package diagram without a package ----

    private static void CheckDiagram(Diagram diagram, Report report)
    {
        if (diagram.Membership == DiagramMembership.Package && string.IsNullOrEmpty(diagram.Package))
            report.Add("MQ3022", $"Diagram '{diagram.Name}' follows a package (membership 'package') but names none; set its package, or set membership to 'explicit'.",
                "/membership");
    }

    // ---- MQ2001, MQ2002, MQ2005, MQ2010, MQ2011: references ----

    private static void CheckReferences(ValidationContext context, Report report)
    {
        var model = context.Model;
        HashSet<(string Rule, string Pointer, string Message)>? xrefFindings = null;
        foreach (var site in context.Walker.Walk(report.Document.Element, model.PropertySchemas, model.PropertyKeys(report.Document.Element)))
        {
            if (site.XRef is { } xref)
            {
                CheckXRef(context, xref, report, xrefFindings ??= []);
                continue;
            }

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
                var expected = string.Join(" or ", reference.Targets.Select(k => KindInfo.Get(k).Name).Concat(reference.IndexKinds.Select(KindWord)).Distinct(StringComparer.Ordinal));
                var found = KindWord(entry.Kind);
                report.Add("MQ2002", $"'{JsonName(site)}' references '{site.Value}', {Article(found)} {found}; expected {Article(expected)} {expected}.", site.Pointer, site.FromId);
            }
        }
    }

    /// <summary>
    /// One value an <c>x-ref</c> types (next-phase-1-design.md section 4.1), judged per schema: MQ2010 when it is a name or other
    /// text, else MQ2001 for each id that does not exist and MQ2002 when the id (the last of a keyed id) is of another kind (a custom
    /// element counts as <c>custom</c> and as <c>custom:&lt;its type&gt;</c>, next-phase-2-design.md section 4.2), else MQ2011 when it
    /// is out of scope. A finding two schemas share is reported once. A custom element's content values are sites like any other.
    /// </summary>
    private static void CheckXRef(ValidationContext context, XRefSite site, Report report, HashSet<(string Rule, string Pointer, string Message)> seen)
    {
        var model = context.Model;
        var xref = site.Ref;
        var kinds = string.Join(" or ", xref.Kinds);
        void Add(string rule, string message)
        {
            if (seen.Add((rule, site.Pointer, message)))
                report.Add(rule, message, site.Pointer, site.Host.Id);
        }

        if (XRefIds(site.Value) is not { } ids)
        {
            Add("MQ2010", $"'{site.Field}' holds '{site.Value}', which is a name or other text, not an id: its schema ({site.Source.Schema}) types it "
                + $"as the id of {Article(kinds)} {kinds}. Write the id, or run the batch operation names-to-ids "
                + "({\"op\":\"names-to-ids\"}), which turns each such name into the one id it finds.");
            return;
        }

        var dangling = false;
        foreach (var id in ids)
        {
            if (model.TryGetEntry(id, out _))
                continue;
            var note = ReservedKindsNote(xref);
            Add("MQ2001", $"'{site.Field}' references '{id}', which does not exist." + (note.Length > 0 ? $" ({note}.)" : ""));
            dangling = true;
        }

        if (dangling || !model.TryGetEntry(ids[^1], out var entry))
            return;
        // A keyed id <operationId>.<errorId> names an error the operation inherits from a stereotype (next-phase-2-design.md section 4.1);
        // one the operation does not inherit names nothing.
        if (ids.Length == 2 && entry.Kind == "error" && model.Get<Stereotype>(entry.OwnerId) is { } errorOwner && model.Get<Operation>(ids[0]) is { } holder
            && !OperationErrors.Of(model, holder).Any(e => string.Equals(e.Key, site.Value, StringComparison.Ordinal)))
        {
            Add("MQ2001", $"'{site.Field}' references '{site.Value}', which does not exist: operation {holder.Name} does not inherit error '{ids[1]}' of stereotype '{errorOwner.Key}'"
                + (holder.Excludes.Contains(ids[1], StringComparer.Ordinal) ? " (it excludes the error)." : "."));
            return;
        }

        var customType = entry.Kind == "custom" ? model.Get<Custom>(entry.Id)?.Type : null;
        if (!xref.Takes(entry.Kind, customType))
        {
            var found = customType is null ? entry.Kind : XRef.CustomPrefix + customType;
            Add("MQ2002", $"'{site.Field}' references '{ids[^1]}', {Article(found)} {found}; its schema ({site.Source.Schema}) expects {Article(kinds)} {kinds}.");
            return;
        }

        if (XRefScopeProblem(context, report.Document.Element, site, ids, entry) is { } problem)
            Add("MQ2011", $"'{site.Field}' references '{ids[^1]}', {problem}.");
    }

    /// <summary>
    /// The note an MQ2001 message adds when an <c>x-ref</c> allows reserved kinds, whose values all dangle until the
    /// kinds arrive, or <c>""</c> when it allows none.
    /// </summary>
    /// <param name="xref">The keyword.</param>
    /// <returns>The note, without brackets or a final full stop.</returns>
    internal static string ReservedKindsNote(XRef xref)
    {
        var later = xref.Kinds.Where(XRef.IsReserved).ToList();
        return later.Count switch
        {
            0 => "",
            1 => $"{later[0]} arrives in a later phase, so no value of that kind exists yet",
            _ => $"{string.Join(", ", later)} arrive in a later phase, so no value of those kinds exists yet",
        };
    }

    /// <summary>
    /// Why an <c>x-ref</c>'s element <c>of</c> is malformed (MQ2009), or <see langword="null"/>: it names no element of the model, or, with
    /// kind <c>member</c>, no custom element (next-phase-2-design.md section 4.2).
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="xref">The keyword.</param>
    /// <returns>The message.</returns>
    internal static string? OfProblem(ModelSnapshot model, XRef xref)
    {
        if (xref.Of is not { } of || of == XRef.ElementScope)
            return null;
        if (!(model.TryGetEntry(of, out var entry) && entry.OwnerId == of))
            return $"x-ref of names {of}, which is no element of the model.";
        if (xref.Kinds.Contains("member", StringComparer.Ordinal) && entry.Kind != "custom")
            return $"x-ref of names {of}, {Article(entry.Kind)} {entry.Kind}: with kind member, of names the custom element whose members the value takes.";
        return null;
    }

    /// <summary>The ids of an <c>x-ref</c> value: one id, or the ids a keyed id joins with <c>.</c>; <see langword="null"/> for text.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The ids, in order, or <see langword="null"/>.</returns>
    internal static string[]? XRefIds(string value)
    {
        if (IdFormat.IsValid(value))
            return [value];
        if (!value.Contains('.', StringComparison.Ordinal))
            return null;
        var segments = value.Split('.');
        return segments.All(IdFormat.IsValid) ? segments : null;
    }

    /// <summary>Why an <c>x-ref</c> target is out of scope (MQ2011), or <see langword="null"/>.</summary>
    private static string? XRefScopeProblem(ValidationContext context, Element element, XRefSite site, string[] ids, IndexEntry entry)
    {
        var xref = site.Ref;
        var model = context.Model;
        if (xref.Of == XRef.ElementScope)
        {
            if (site.Default)
                return "but a stereotype default cannot use of \"$element\": a default serves many elements, so \"this element\" names none";
            if (element is Table table && entry.Kind is "column" or "key" && model.Get<Stereotype>(entry.OwnerId) is { } stereotype)
            {
                // A column or key a stereotype gives a table is the table's member only by its keyed id (next-phase-2-design.md 3.1).
                if (InheritedMemberScopeProblem(model, table, stereotype, ids) is { } inherited)
                    return inherited;
            }
            else if (element is Operation operation && entry.Kind == "error" && model.Get<Stereotype>(entry.OwnerId) is { } errorStereotype)
            {
                // An error a stereotype gives an operation is the operation's only by its keyed id (next-phase-2-design.md section 4.1).
                var keyed = ContributedMembers.KeyOf(operation.Id, ids[^1]);
                var inherits = OperationErrors.Of(model, operation).Any(e => string.Equals(e.Key, keyed, StringComparison.Ordinal));
                var what = $"an error of stereotype '{errorStereotype.Key}'";
                if (ids.Length == 1)
                {
                    return inherits
                        ? $"{what}; of \"$element\" takes an error an operation inherits by its keyed id: write '{keyed}'"
                        : $"{what} that operation {operation.Name} does not inherit";
                }

                if (!string.Equals(ids[0], operation.Id, StringComparison.Ordinal))
                    return $"{what} as {Describe(model, ids[0])} inherits it; of \"$element\" takes an error of operation {operation.Name}, by its keyed id '{keyed}'";
            }
            else
            {
                // The host's top-level element, its base chain and the stereotypes it carries own the members in scope.
                var scope = new HashSet<string>(StringComparer.Ordinal) { element.Id };
                if (element is Entity entity)
                    scope.UnionWith(context.Ancestors(entity).Select(a => a.Id));
                scope.UnionWith(context.StereotypesOf(element).Select(s => s.Id));
                if (!scope.Contains(entry.OwnerId))
                    return $"a member of {Describe(model, entry.OwnerId)}; of \"$element\" takes a member of {element.KindName} {element.Name}, its base entities or its stereotypes";
            }
        }
        else if (xref.Of is { } listId)
        {
            // A member is in scope when the custom element of names owns it (next-phase-2-design.md section 4.2).
            if (entry.Kind == "member")
            {
                if (entry.OwnerId != listId)
                    return $"a member of {Describe(model, entry.OwnerId)}, not of {Describe(model, listId)} (of {listId})";
            }
            else if (entry.Kind != "row" || context.SeedOfRow(entry.Id) is not { } seed || seed.Target != listId)
            {
                return $"which is not a row of a seed of {Describe(model, listId)} (of {listId})";
            }
        }

        if (!xref.SameDatabase)
            return null;
        if (site.Default)
            return "but a stereotype default cannot use sameDatabase: a default serves many elements, which need not share a database";
        if (XRefDatabaseOf(element) is not { } hostDatabase)
            return $"but {element.KindName} {element.Name} has no single database for sameDatabase to compare with";
        // A keyed id of an inherited member is in its table's database, not in the stereotype's (which has none).
        var targetDatabase = ids.Length > 1 && model.Get<Table>(ids[0]) is { } keyedTable ? keyedTable.Database
            : model.Get<Element>(entry.OwnerId) is { } owner ? XRefDatabaseOf(owner) : null;
        return targetDatabase == hostDatabase
            ? null
            : $"which is in {(targetDatabase is null ? "no database" : Describe(model, targetDatabase))}, not in {Describe(model, hostDatabase)} (sameDatabase)";
    }

    /// <summary>
    /// Why a column or key of a stereotype is out of the <c>of: "$element"</c> scope of a table (next-phase-2-design.md section 3.1), or
    /// <see langword="null"/>: the table reaches a member it inherits by its keyed id <c>&lt;tableId&gt;.&lt;memberId&gt;</c> only. A bare
    /// member id, another table's keyed id and a member the table does not inherit (or excludes) are out of scope. An overlay's inherited
    /// members are keyed by the synthesized table key, which holds <c>@</c> and so is no value an <c>x-ref</c> takes (MQ2010).
    /// </summary>
    private static string? InheritedMemberScopeProblem(ModelSnapshot model, Table table, Stereotype stereotype, string[] ids)
    {
        var memberId = ids[^1];
        var tableKey = TableMembers.TableKeyOf(table) ?? table.Id;
        var keyed = ContributedMembers.KeyOf(tableKey, memberId);
        var what = $"a member of stereotype '{stereotype.Key}'";
        if (ids.Length == 1)
        {
            // The keyed id is worth suggesting only when the table inherits the member; else it is out of scope too.
            if (NotInherited() is { } problem)
                return problem;
            return table.Origin == TableOrigin.Synthesized
                ? $"{what}; of \"$element\" takes a member a table inherits by its keyed id, and the keyed id of an overlay's inherited member ('{keyed}') holds the synthesized table key, which an x-ref value cannot hold"
                : $"{what}; of \"$element\" takes a member a table inherits by its keyed id: write '{keyed}'";
        }

        if (ids.Length != 2 || !string.Equals(ids[0], tableKey, StringComparison.Ordinal))
            return $"{what} as {Describe(model, ids[0])} inherits it; of \"$element\" takes a member of table {table.Name}, by its keyed id '{keyed}'";
        return NotInherited();

        string? NotInherited()
        {
            var set = TableMembers.Of(model, table);
            bool Has<T>(IReadOnlyList<InheritedMember<T>> members) where T : class => members.Any(m => string.Equals(m.Key, keyed, StringComparison.Ordinal));
            return Has(set.Columns) || Has(set.PrimaryKeys) || Has(set.Uniques) || Has(set.ForeignKeys) || Has(set.Indexes) || Has(set.Checks)
                ? null
                : $"{what} that table {table.Name} does not inherit{(set.Inheritable.ContainsKey(memberId) ? " (it excludes the member)" : "")}";
        }
    }

    /// <summary>
    /// The one database of an element for <c>sameDatabase</c>: a database itself, the database of a physical object, mapping or
    /// migration fact, or the database of an entity's bindings when they all name one; <see langword="null"/> otherwise. The batch
    /// operation <c>names-to-ids</c> narrows <c>sameDatabase</c> candidates with it.
    /// </summary>
    /// <param name="element">The element that holds the value, or the target's top-level element.</param>
    /// <returns>The database id, or <see langword="null"/>.</returns>
    internal static string? XRefDatabaseOf(Element element) => element switch
    {
        Database d => d.Id,
        Table t => t.Database,
        View v => v.Database,
        Sequence q => q.Database,
        Routine r => r.Database,
        DatabaseType t => t.Database,
        SqlObject o => o.Database,
        Query q => q.Database,
        Mapping m => m.Database,
        Migration f => f.Database,
        Entity { Bindings.Count: > 0 } e when e.Bindings.Select(b => b.Database).Distinct(StringComparer.Ordinal).Count() == 1 => e.Bindings[0].Database,
        _ => null,
    };

    private static string Describe(ModelSnapshot model, string id) => model.Get<Element>(id) is { } e ? $"{e.KindName} {e.Name}" : id;

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

    // An index kind as MQ2002 words it: an entry of a lifecycle's trigger points is a trigger point (trigger-points-design.md section 9).
    private static string KindWord(string kind) => kind is "process-trigger-point" or "trigger-point" ? "trigger point" : kind;

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
        if (pointer.Length == 0 && item is Element element && ReferenceEquals(element, report.Document.Element))
            CheckElementStereotypes(context, element, kindName, report);
        else
            CheckStereotypes(context, item.Id, item.Stereotypes, pointer, kindName, report);

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
    /// Checks the stereotypes of a sub-element or a process node (MQ2003, MQ2004, MQ2013 to MQ2015) on its effective set
    /// (<see cref="ModelSnapshot.EffectiveStereotypes(IReadOnlyList{string})"/>: implied first, then own; next-phase-1-design.md section 4.2). An unknown own
    /// key is MQ2003 at its entry; a stereotype that does not apply to the kind is MQ2004 at its entry (for an implied one, the entry
    /// of the stereotype that implies it). Two of one group (MQ2013), a missing required one (MQ2014) and two that conflict (MQ2015)
    /// are reported at the later stereotype's entry. A top-level element is judged on its whole effective set, schema and package
    /// defaults included (<see cref="CheckCommon"/> runs <c>CheckElementStereotypes</c> for it).
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="id">The id of the item.</param>
    /// <param name="stereotypes">Its stereotype keys.</param>
    /// <param name="pointer">Its pointer.</param>
    /// <param name="kindName">Its kind name for <c>appliesTo</c>.</param>
    /// <param name="report">The report.</param>
    public static void CheckStereotypes(ValidationContext context, string? id, IReadOnlyList<string> stereotypes, string pointer, string kindName, Report report)
    {
        if (stereotypes.Count == 0)
            return;
        CheckUnknownKeys(context, stereotypes, pointer + "/stereotypes", id, report);
        JudgeEffectiveSet(context.Model, context.Model.EffectiveStereotypes(stereotypes), applied => Ptr.At(pointer + "/stereotypes", applied.Entry), id, kindName, report);
    }

    /// <summary>
    /// Checks the stereotypes of a top-level element on its whole effective set (next-phase-2-design.md section 3.1): its own list, the
    /// stereotypes they imply, and the defaults of its database schema and packages (<see cref="ModelSnapshot.EffectiveStereotypes(Element)"/>).
    /// MQ2003 for an unknown key in <c>stereotypes</c> or <c>excludeStereotypes</c>; MQ2004 as for any item (a default that does not apply
    /// to the kind never reaches it, so a default never makes MQ2004); MQ2013 to MQ2015 judge the whole set, and a finding about a default
    /// names the schema or package that gives it and how to exclude it, at <c>/stereotypes</c> (the file root without a list); MQ2029 for an
    /// exclusion of an own or implied stereotype, MQ2030 for one no default gives.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="element">The element.</param>
    /// <param name="kindName">Its kind name for <c>appliesTo</c>.</param>
    /// <param name="report">The report.</param>
    private static void CheckElementStereotypes(ValidationContext context, Element element, string kindName, Report report)
    {
        var model = context.Model;
        var excludes = element.ExcludeStereotypes ?? [];
        if (element.Stereotypes.Count == 0 && excludes.Count == 0 && !model.StereotypeDefaults.HasDefaults)
            return;
        CheckUnknownKeys(context, element.Stereotypes, "/stereotypes", element.Id, report);
        var holder = model.HolderOf(element);
        var listPointer = element.Stereotypes.Count > 0 ? "/stereotypes" : "";
        JudgeEffectiveSet(model, model.EffectiveStereotypes(holder), applied => applied.Entry >= 0 ? Ptr.At("/stereotypes", applied.Entry) : listPointer,
            element.Id, kindName, report);
        if (excludes.Count == 0)
            return;

        // MQ2029: only defaults can be excluded, never an own stereotype or one an own stereotype implies.
        var own = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in element.Stereotypes)
            own.TryAdd(key, null);
        foreach (var key in element.Stereotypes)
        {
            foreach (var implied in model.GetStereotype(key)?.Implies ?? [])
                own.TryAdd(implied, key);
        }

        // MQ2030: the keys the defaults give the element before any exclusion (kinds they do not apply to give nothing).
        var given = model.EffectiveStereotypes(holder with { Own = [], Excludes = [] }).Select(s => s.Stereotype.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < excludes.Count; i++)
        {
            var key = excludes[i];
            var at = Ptr.At("/excludeStereotypes", i);
            if (model.GetStereotype(key) is null)
                report.Add("MQ2003", $"Stereotype '{key}' is not defined in model/vocabularies/stereotypes.", at, element.Id);
            else if (own.TryGetValue(key, out var by))
            {
                report.Add("MQ2029", by is null
                    ? $"'excludeStereotypes' names '{key}', which the {kindName} lists in 'stereotypes'; only a schema or package default can be excluded. Remove it from one list."
                    : $"'excludeStereotypes' names '{key}', which '{by}' in 'stereotypes' implies; only a schema or package default can be excluded. Remove it from one list.",
                    at, element.Id);
            }
            else if (!given.Contains(key))
            {
                report.Add("MQ2030", $"'excludeStereotypes' names '{key}', which no schema or package default gives this {kindName}, so it excludes nothing; remove the entry.",
                    at, element.Id);
            }
        }
    }

    /// <summary>MQ2003 for each key of a stereotype key list that names no stereotype, at its entry.</summary>
    private static void CheckUnknownKeys(ValidationContext context, IReadOnlyList<string> keys, string listPointer, string? id, Report report)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (context.Model.GetStereotype(keys[i]) is null)
                report.Add("MQ2003", $"Stereotype '{keys[i]}' is not defined in model/vocabularies/stereotypes.", Ptr.At(listPointer, i), id);
        }
    }

    /// <summary>
    /// A database schema entry's or a package's <c>defaultStereotypes</c> (next-phase-2-design.md section 3.1): MQ2003 for an unknown key
    /// at its entry, and MQ2031 when the list, with the stereotypes its entries imply, holds two of one group or two that conflict, at
    /// the later entry (for an implied one, the entry that implies it).
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="keys">The list.</param>
    /// <param name="listPointer">The list's pointer.</param>
    /// <param name="id">The id of the schema entry or package.</param>
    /// <param name="report">The report.</param>
    private static void CheckDefaultList(ValidationContext context, IReadOnlyList<string> keys, string listPointer, string id, Report report)
    {
        if (keys.Count == 0)
            return;
        CheckUnknownKeys(context, keys, listPointer, id, report);
        var layer = StereotypeDefaults.Layer(keys, context.Model.GetStereotype);
        var groups = new Dictionary<string, AppliedStereotype>(StringComparer.Ordinal);
        for (var i = 0; i < layer.Count; i++)
        {
            var applied = layer[i];
            var stereotype = applied.Stereotype;
            var at = Ptr.At(listPointer, applied.Entry);
            if (stereotype.Group is { } group && !groups.TryAdd(group, applied))
            {
                report.Add("MQ2031", $"'defaultStereotypes' gives {Named(applied)} and {Named(groups[group])}, both in group '{group}'; an element carries at most one stereotype of a group. Fix the list.",
                    at, id);
            }

            for (var j = 0; j < i; j++)
            {
                var earlier = layer[j];
                if (stereotype.Conflicts.Contains(earlier.Stereotype.Key, StringComparer.Ordinal)
                    || earlier.Stereotype.Conflicts.Contains(stereotype.Key, StringComparer.Ordinal))
                {
                    report.Add("MQ2031", $"'defaultStereotypes' gives {Named(earlier)} and {Named(applied)}, which conflict. Fix the list.", at, id);
                }
            }
        }

        static string Named(AppliedStereotype applied) =>
            applied.ImpliedBy is { } by ? $"'{applied.Stereotype.Key}' (implied by '{by}')" : $"'{applied.Stereotype.Key}'";
    }

    /// <summary>
    /// MQ2004 and MQ2013 to MQ2015 on an effective set, each at the pointer <paramref name="at"/> gives the stereotype concerned (the
    /// later one for a pair). A default stereotype is named with the schema or package that gives it, and a finding it takes part in
    /// says how to leave it out (<c>excludeStereotypes</c>). The resolver also judges a table without a file with it, on its entity or
    /// relation (next-phase-2-design.md section 3.1).
    /// </summary>
    internal static void JudgeEffectiveSet(ModelSnapshot model, IReadOnlyList<AppliedStereotype> effective, Func<AppliedStereotype, string> at, string? id,
        string kindName, Report report)
    {
        string At(AppliedStereotype applied) => at(applied);
        string Name(AppliedStereotype applied)
        {
            var from = applied.DefaultHolder is { } holder ? "a default of " + DefaultHolderName(model, applied.Source, holder) : null;
            return (applied.ImpliedBy, from) switch
            {
                ({ } by, { } source) => $"'{applied.Stereotype.Key}' (implied by '{by}', {source})",
                ({ } by, null) => $"'{applied.Stereotype.Key}' (implied by '{by}')",
                (null, { } source) => $"'{applied.Stereotype.Key}' ({source})",
                _ => $"'{applied.Stereotype.Key}'",
            };
        }

        static string Exclusion(params AppliedStereotype[] pair) =>
            pair.FirstOrDefault(p => p.DefaultHolder is not null) is { } fromDefault
                ? $" To leave the default out of this element, add '{fromDefault.Stereotype.Key}' to its excludeStereotypes."
                : "";

        var keys = effective.Select(s => s.Stereotype.Key).ToHashSet(StringComparer.Ordinal);
        var groups = new Dictionary<string, AppliedStereotype>(StringComparer.Ordinal);
        for (var i = 0; i < effective.Count; i++)
        {
            var applied = effective[i];
            var stereotype = applied.Stereotype;
            if (stereotype.AppliesTo.Count > 0 && !stereotype.AppliesTo.Contains(kindName, StringComparer.Ordinal))
                report.Add("MQ2004", $"Stereotype {Name(applied)} applies to {string.Join(", ", stereotype.AppliesTo)}, not to {Article(kindName)} {kindName}.", At(applied), id);
            if (stereotype.Group is { } group && !groups.TryAdd(group, applied))
            {
                report.Add("MQ2013", $"Stereotype {Name(applied)} is in group '{group}', and {Article(kindName)} {kindName} carries at most one stereotype of a group: {Name(groups[group])} is in it too."
                    + Exclusion(applied, groups[group]), At(applied), id);
            }

            foreach (var required in stereotype.Requires)
            {
                if (!keys.Contains(required))
                    report.Add("MQ2014", $"Stereotype {Name(applied)} requires stereotype '{required}', which the {kindName} does not carry." + Exclusion(applied), At(applied), id);
            }

            for (var j = 0; j < i; j++)
            {
                var earlier = effective[j];
                if (stereotype.Conflicts.Contains(earlier.Stereotype.Key, StringComparer.Ordinal)
                    || earlier.Stereotype.Conflicts.Contains(stereotype.Key, StringComparer.Ordinal))
                {
                    report.Add("MQ2015", $"Stereotypes {Name(earlier)} and {Name(applied)} conflict: {Article(kindName)} {kindName} carries one or the other."
                        + Exclusion(applied, earlier), At(applied), id);
                }
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

    /// <summary>How a message names the holder of a default stereotype: <c>schema 'billing'</c> or <c>package 'Billing'</c>.</summary>
    internal static string DefaultHolderName(ModelSnapshot model, StereotypeSource source, string holderId)
    {
        if (source == StereotypeSource.SchemaDefault)
        {
            var schema = model.TryGetEntry(holderId, out var entry) && model.Get<Database>(entry.OwnerId) is { } database
                ? database.Schemas.FirstOrDefault(s => string.Equals(s.Id, holderId, StringComparison.Ordinal))
                : null;
            return $"schema '{(schema is { Name.Length: > 0 } ? schema.Name : holderId)}'";
        }

        return $"package '{(model.Get<Package>(holderId) is { Name.Length: > 0 } package ? package.Name : holderId)}'";
    }

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
            case Package or Entity or ValueObject or ScalarType or EnumType or Process:
                CheckIdentifier(element.Name, element.KindName, "/name", element.Id, report);
                break;
            case Table { Origin: TableOrigin.Synthesized, Name.Length: 0 }:
                break; // an overlay keeps the conventional name
            case Migration { Name.Length: 0 }:
                break; // a recorded fact has no name (next-phase-1-design.md section 3.2)
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
        CheckDefaultList(context, package.DefaultStereotypes, "/defaultStereotypes", package.Id, report);

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
            CheckFlatDuplicates(context, entity, flat, report);

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

    private static void CheckFlatDuplicates(ValidationContext context, ElementBase owner, IReadOnlyList<FlatAttribute> flat, Report report)
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
                report.Add("MQ3007", $"Stereotype '{item.From}' adds attribute '{name}', which is already used by {from}.", StereotypePointer(context, owner, item.From), owner.Id);
            }
        }
    }

    /// <summary>
    /// The pointer of a stereotype an element carries: its own entry (the first entry for an implied one, as phase 1 points), or, for
    /// a schema or package default, the <c>stereotypes</c> list, or the file root when the file has none (next-phase-2-design.md
    /// section 3.1).
    /// </summary>
    private static string StereotypePointer(ValidationContext context, ElementBase owner, string key)
    {
        if (owner is Element element && !owner.Stereotypes.Contains(key, StringComparer.Ordinal)
            && context.Model.EffectiveStereotypes(element).FirstOrDefault(s => s.Stereotype.Key == key) is { Entry: < 0 })
        {
            return owner.Stereotypes.Count > 0 ? "/stereotypes" : "";
        }

        return Ptr.At("/stereotypes", IndexOf(owner.Stereotypes, key));
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
        CheckFlatDuplicates(context, valueObject, flat, report);
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
                // several-types: wave 2 — a child end with several types checks each covered entity (decision 9); an owner end with several
                // types counts as one owner.
                var child = ends[composition.Child].SingleEntity;
                var owners = child is null ? [] : context.CompositionsOwning(child);
                if (child is not null && owners.Length > 1 && owners[0] != report.Document)
                {
                    var childName = model.Get<Entity>(child)?.Name ?? child;
                    report.Add("MQ3016", $"Entity '{childName}' is already owned through composition {owners[0].Path}; a composition child has one owner.",
                        Ptr.At("/ends", composition.Child) + "/entity", ends[composition.Child].Id);
                }
            }
        }

        var flat = relation.Attributes.Select((a, i) => new FlatAttribute(a, AttributeSource.Own, relation.Id, i))
            .Concat(context.VirtualAttributes(relation)).ToList();
        CheckFlatDuplicates(context, relation, flat, report);
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
            CheckDefaultList(context, schema.DefaultStereotypes, pointer + "/defaultStereotypes", schema.Id, report);
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
        CheckFlatDuplicates(context, stereotype, flat, report);
        CheckAttributes(context, stereotype.Attributes, report);
        PhysicalRules.CheckStereotypeStorage(stereotype, report);
        CheckStereotypeLinks(context.Model, stereotype, report);
        CheckStereotypeParameters(context.Model, stereotype, report);
        CheckStereotypeMembers(context.Model, stereotype, report);
    }

    /// <summary>
    /// The members a stereotype contributes to tables (next-phase-2-design.md section 3.1): MQ2024 for members on a stereotype that does
    /// not apply to tables, a column with <c>attribute</c> or <c>sequence</c>, and a key, index or check with a <c>name</c> (every carrier
    /// would share it); MQ2025 for a key, index or check naming a column the stereotype does not declare; MQ2026 for a foreign key's
    /// target; MQ2001 for a fixed <c>referencesTable</c> that is neither a table file nor a synthesized table key.
    /// </summary>
    private static void CheckStereotypeMembers(ModelSnapshot model, Stereotype stereotype, Report report)
    {
        var s = stereotype;
        var lists = new List<string>();
        if (s.Columns.Count > 0)
            lists.Add("columns");
        if (s.PrimaryKey is not null)
            lists.Add("primaryKey");
        if (s.Uniques.Count > 0)
            lists.Add("uniques");
        if (s.ForeignKeys.Count > 0)
            lists.Add("foreignKeys");
        if (s.Checks.Count > 0)
            lists.Add("checks");
        if (s.Indexes.Count > 0)
            lists.Add("indexes");
        if (lists.Count == 0)
            return;
        if (s.AppliesTo.Count > 0 && !s.AppliesTo.Contains("table", StringComparer.Ordinal))
        {
            report.Add("MQ2024", $"Stereotype '{s.Key}' declares {string.Join(", ", lists)} but does not apply to tables, so its members reach no table: add table to appliesTo, or remove the members.",
                "/appliesTo");
        }

        var own = new HashSet<string>(s.Columns.Select(c => c.Id).Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
        for (var i = 0; i < s.Columns.Count; i++)
        {
            var column = s.Columns[i];
            var pointer = Ptr.At("/columns", i);
            if (column.Attribute is not null)
            {
                report.Add("MQ2024", $"Column '{column.Name}' of stereotype '{s.Key}' sets attribute, which only a synthesized table's overlay column has: a contributed column stores no attribute (the stereotype's attributes serve entities).",
                    pointer + "/attribute", column.Id);
            }

            if (column.Sequence is not null)
            {
                report.Add("MQ2024", $"Column '{column.Name}' of stereotype '{s.Key}' sets sequence: a sequence belongs to one database, and a stereotype's column reaches tables of any database. Use a default or an identity instead.",
                    pointer + "/sequence", column.Id);
            }

            PhysicalRules.CheckStableId(column, pointer, $"Column '{column.Name}' of stereotype '{s.Key}'", report); // next-phase-2-design.md section 3.4
        }

        void Named(string? name, string pointer, string what, string? id)
        {
            if (name is not null)
                report.Add("MQ2024", $"The {what} of stereotype '{s.Key}' sets a name, which every table that carries it would share: names come from the naming conventions per table, and a table may redefine one. Remove the name.", pointer, id);
        }

        void Columns(IReadOnlyList<string?> columns, string pointer, string what, string? id, string suffix = "")
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i] is { } column && !own.Contains(column))
                    report.Add("MQ2025", NotDeclared(what, column), Ptr.At(pointer, i) + suffix, id);
            }
        }

        string NotDeclared(string what, string column) =>
            $"The {what} of stereotype '{s.Key}' names column '{column}', which the stereotype does not declare: a member names only its own stereotype's columns.";

        if (s.PrimaryKey is { } pk)
        {
            Named(pk.Name, "/primaryKey/name", "primary key", pk.Id);
            Columns(pk.Columns, "/primaryKey/columns", "primary key", pk.Id);
        }

        for (var i = 0; i < s.Uniques.Count; i++)
        {
            var unique = s.Uniques[i];
            Named(unique.Name, Ptr.At("/uniques", i) + "/name", "unique constraint", unique.Id);
            Columns(unique.Columns, Ptr.At("/uniques", i) + "/columns", "unique constraint", unique.Id);
        }

        for (var i = 0; i < s.ForeignKeys.Count; i++)
        {
            var fk = s.ForeignKeys[i];
            var pointer = Ptr.At("/foreignKeys", i);
            Columns(fk.Columns, pointer + "/columns", "foreign key", fk.Id);
            Columns(fk.OnDeleteColumns, pointer + "/onDeleteColumns", "foreign key", fk.Id);
            CheckContributedTarget(model, s, fk, pointer, report);
        }

        for (var i = 0; i < s.Checks.Count; i++)
        {
            var check = s.Checks[i];
            Named(check.Name, Ptr.At("/checks", i) + "/name", "check constraint", check.Id);
            if (check.Column is { } column && !own.Contains(column))
                report.Add("MQ2025", NotDeclared("check constraint", column), Ptr.At("/checks", i) + "/column", check.Id);
        }

        for (var i = 0; i < s.Indexes.Count; i++)
        {
            var index = s.Indexes[i];
            var pointer = Ptr.At("/indexes", i);
            Named(index.Name, pointer + "/name", "index", index.Id);
            Columns(index.Columns.Select(c => c.Column).ToList(), pointer + "/columns", "index", index.Id, "/column");
            Columns(index.Include, pointer + "/include", "index", index.Id);
        }
    }

    /// <summary>
    /// A contributed foreign key's target (MQ2026): exactly one of <c>referencesTable</c> (MQ2001 when it names no table) and
    /// <c>referencesParameter</c>, a required string parameter typed <c>x-ref: { kind: table, sameDatabase: true }</c>, so each carrier
    /// names a table file of its own database; no <c>referencesColumns</c> beside a parameter, whose table differs per carrier.
    /// </summary>
    private static void CheckContributedTarget(ModelSnapshot model, Stereotype s, MemberForeignKey fk, string pointer, Report report)
    {
        var fixedTable = !string.IsNullOrEmpty(fk.ReferencesTable);
        var parameter = fk.ReferencesParameter is { Length: > 0 } name ? name : null;
        if (fixedTable == (parameter is not null))
        {
            report.Add("MQ2026", fixedTable
                    ? $"Foreign key '{fk.Id}' of stereotype '{s.Key}' sets both referencesTable and referencesParameter: keep one target."
                    : $"Foreign key '{fk.Id}' of stereotype '{s.Key}' has no target: set referencesTable (one table) or referencesParameter (a parameter each carrier gives).",
                pointer + (fixedTable ? "/referencesParameter" : ""), fk.Id);
            return;
        }

        if (parameter is null)
        {
            if (model.Get<Table>(fk.ReferencesTable!) is null && !PhysicalRules.IsSynthesizedTableKey(model, fk.ReferencesTable!))
                report.Add("MQ2001", $"Foreign key '{fk.Id}' of stereotype '{s.Key}' references table '{fk.ReferencesTable}', which is neither a table file nor a synthesized table key.", pointer + "/referencesTable", fk.Id);
            return;
        }

        if (ParameterTargetProblem(s, parameter) is { } problem)
            report.Add("MQ2026", $"Foreign key '{fk.Id}' of stereotype '{s.Key}' takes its target from parameter '{parameter}', {problem}.", pointer + "/referencesParameter", fk.Id);
        if (fk.ReferencesColumns.Count > 0)
        {
            report.Add("MQ2026", $"Foreign key '{fk.Id}' of stereotype '{s.Key}' lists referencesColumns beside referencesParameter: each carrier names its own table, so the key references that table's primary key. Remove referencesColumns.",
                pointer + "/referencesColumns", fk.Id);
        }
    }

    /// <summary>Why a stereotype parameter cannot name a contributed foreign key's target, or <see langword="null"/>.</summary>
    private static string? ParameterTargetProblem(Stereotype s, string name)
    {
        const string Shape = "{\"type\": \"string\", \"x-ref\": {\"kind\": \"table\", \"sameDatabase\": true}}";
        if (s.Parameters is not { Properties.ValueKind: JsonValueKind.Object } parameters || !parameters.Properties.TryGetProperty(name, out var schema))
            return $"which the stereotype's parameters do not declare: declare it as {Shape} and list it in required";
        var isString = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() == "string";
        var (refs, _, _) = PropertySchemaSet.Read(parameters.Properties, "/parameters/properties", static (_, _) => { });
        var xref = refs.TryGetValue(name, out var node) ? node.Ref : null;
        if (!isString || xref is not { SameDatabase: true, Kinds: [ "table" ] })
            return $"which is not typed {Shape}";
        return parameters.Required.Contains(name, StringComparer.Ordinal) ? null : "which is not in required: every carrier must name its table";
    }

    /// <summary>
    /// The links of a stereotype (next-phase-1-design.md section 4.2): MQ2016 for a <c>requires</c>, <c>conflicts</c> or
    /// <c>implies</c> entry that names an unknown key or the stereotype itself, a conflict with a stereotype it requires or implies, and
    /// an implied stereotype of its own group; MQ2017 for an implied stereotype that implies others (implication is one level, which
    /// also refuses cycles).
    /// </summary>
    private static void CheckStereotypeLinks(ModelSnapshot model, Stereotype stereotype, Report report)
    {
        foreach (var (field, keys) in new[] { ("requires", stereotype.Requires), ("conflicts", stereotype.Conflicts), ("implies", stereotype.Implies) })
        {
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                var at = Ptr.At("/" + field, i);
                if (key == stereotype.Key)
                {
                    report.Add("MQ2016", $"'{field}' names the stereotype itself ('{key}').", at);
                    continue;
                }

                if (model.GetStereotype(key) is not { } other)
                {
                    report.Add("MQ2016", $"'{field}' names stereotype '{key}', which is not defined in model/vocabularies/stereotypes.", at);
                    continue;
                }

                switch (field)
                {
                    case "conflicts" when stereotype.Requires.Contains(key, StringComparer.Ordinal) || stereotype.Implies.Contains(key, StringComparer.Ordinal):
                        report.Add("MQ2016",
                            $"'conflicts' names '{key}', which the stereotype also {(stereotype.Implies.Contains(key, StringComparer.Ordinal) ? "implies" : "requires")}: no element could carry it.",
                            at);
                        break;
                    case "implies":
                        if (stereotype.Group is { } group && other.Group == group)
                            report.Add("MQ2016", $"'implies' names '{key}', which is in the stereotype's own group '{group}': no element could carry both.", at);
                        if (other.Implies.Count > 0)
                        {
                            report.Add("MQ2017",
                                $"'implies' names '{key}', which implies {string.Join(", ", other.Implies.Select(k => "'" + k + "'"))}: implication is one level, so imply each directly.",
                                at);
                        }

                        break;
                }
            }
        }
    }

    /// <summary>
    /// The typed parameters of a stereotype (next-phase-1-design.md section 4.2): MQ2019 when <c>parameters</c> does not compile or
    /// <c>required</c> names an undeclared parameter, MQ2009 for a malformed or misplaced <c>x-ref</c> in it (and an <c>of</c> that
    /// names no element), MQ7212 and MQ7213 for a misplaced <c>x-translatable</c>, and MQ2018 for a default that fails its own property
    /// schema. Defaults are checked without <c>required</c>:
    /// a stereotype may leave a required parameter to its hosts, whose merged values are judged (<see cref="ExtensionSet"/>).
    /// </summary>
    private static void CheckStereotypeParameters(ModelSnapshot model, Stereotype stereotype, Report report)
    {
        if (stereotype.Parameters is not { } parameters)
            return;
        var document = report.Document;
        var source = model.PropertySchemas.SourceOf(document.Path) is { } own && own.StereotypeKey == stereotype.Key && own.Hash == document.Hash
            ? own
            : PropertySchemaSource.OfStereotype(stereotype, parameters, document.Path, document.Hash);
        PropertySchemaSet.Read(parameters.Properties, "/parameters/properties", (pointer, message) => report.Add("MQ2009", message, pointer));
        foreach (var (rule, pointer, message) in PropertySchemaSet.TranslatableFindings(parameters.Properties, "/parameters/properties", stereotype.AppliesTo))
            report.Add(rule, message, pointer); // MQ7212, MQ7213 (next-phase-1-design.md section 4.4)
        foreach (var (pointer, xref) in source.Keywords)
        {
            if (OfProblem(model, xref) is { } problem)
                report.Add("MQ2009", problem, pointer + "/of");
        }

        for (var i = 0; i < parameters.Required.Count; i++)
        {
            if (parameters.Properties.ValueKind != JsonValueKind.Object || !parameters.Properties.TryGetProperty(parameters.Required[i], out _))
                report.Add("MQ2019", $"'required' names '{parameters.Required[i]}', which 'properties' does not declare.", Ptr.At("/parameters/required", i));
        }

        if (source.CompiledWithoutRequired is not { Schema: { } lenient })
        {
            report.Add("MQ2019", $"'parameters' is not a valid JSON Schema: {source.CompiledWithoutRequired.Error}", "/parameters/properties");
            return;
        }

        if (stereotype.DefaultProperties.Count == 0)
            return;
        var defaults = JsonSerializer.SerializeToElement(new SortedDictionary<string, JsonElement>(stereotype.DefaultProperties.ToDictionary(), StringComparer.Ordinal));
        var results = lenient.Evaluate(defaults, DefaultsEvaluation);
        if (results.IsValid)
            return;
        var any = false;
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is null || ExtensionSet.InsideDiscardedBranch(detail))
                continue;
            foreach (var (keyword, message) in detail.Errors.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (ExtensionSet.ApplicatorKeywords.Contains(keyword))
                    continue;
                var location = detail.InstanceLocation.ToString();
                report.Add("MQ2018", $"The default {location.TrimStart('/')} fails its parameter schema: {(keyword.Length == 0 ? message : keyword + ": " + message)}",
                    "/defaultProperties" + location);
                any = true;
            }
        }

        if (!any)
            report.Add("MQ2018", "The defaults do not match their parameter schemas.", "/defaultProperties");
    }

    private static readonly global::Json.Schema.EvaluationOptions DefaultsEvaluation =
        new() { OutputFormat = global::Json.Schema.OutputFormat.List, Culture = CultureInfo.InvariantCulture };
}
