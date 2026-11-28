using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// A whole-model validation report with each document's own findings, so the next snapshot's report can be worked out from the
/// documents an edit reaches (change-driven-edits-design.md section 4, step 2).
/// </summary>
/// <param name="Report">The report, as <see cref="ModelValidator.ValidateAsync"/> gives it for the same scope.</param>
/// <param name="PerDocument">The findings each active document's built-in rules, extension schemas and suppression checks gave, by
/// element id, before script rules, settings, suppressions and positions; documents without findings are left out.</param>
/// <param name="TextHolders">The documents whose expressions hold text, or cite a constraint that does (<see cref="ModelValidator.HoldsText"/>):
/// text is read by name, so any change may move what it means.</param>
/// <param name="DeferScriptedRows">Whether seeds with a rows script were left unjudged (<see cref="ValidationScope.Previews"/>).</param>
/// <param name="Validated">How many documents the built-in rules ran on.</param>
/// <param name="Incremental">Whether it was worked out from a previous report rather than over every document.</param>
internal sealed record WholeValidation(ValidationReport Report, IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> PerDocument,
    IReadOnlySet<string> TextHolders, bool DeferScriptedRows, int Validated, bool Incremental)
{
    /// <summary>Why a validation given a previous one covered the whole model anyway, or <see langword="null"/>.</summary>
    public string? Fallback { get; init; }
}

/// <summary>A plain element change between two snapshots that an incremental validation can take (<see cref="ModelValidator.PrepareIncremental"/>).</summary>
/// <param name="Changed">The ids of the element documents created, changed or deleted.</param>
/// <param name="OneSided">The changed ids only one of the two snapshots holds (created or deleted).</param>
/// <param name="Renamed">The changed ids whose element's name changed.</param>
/// <param name="FormerTargets">The documents the change reached in the snapshot before it, worked out while that snapshot was at hand, so it
/// need not be kept.</param>
internal sealed record IncrementalChange(IReadOnlySet<string> Changed, IReadOnlySet<string> OneSided, IReadOnlySet<string> Renamed,
    IReadOnlySet<string> FormerTargets);

/// <summary>What an incremental whole-model validation builds on.</summary>
/// <param name="Previous">The whole-model validation of the snapshot before the change, of the same scope.</param>
/// <param name="Change">The change since that snapshot.</param>
internal sealed record IncrementalInput(WholeValidation Previous, IncrementalChange Change);

internal sealed partial class ModelValidator
{
    /// <summary>The most changed documents an incremental validation takes; more validate the whole model.</summary>
    internal const int IncrementalLimit = 512;

    /// <summary>
    /// The kinds whose change an incremental validation takes (Validation/README.md, "Incremental validation"): the rule inventory shows
    /// which documents their changes reach (<see cref="Widen"/>). A change of any other kind (packages, stereotypes, vocabularies,
    /// databases, processes, operations, actors, scenarios, trigger points, rights, constraints, scalar types, views, routines, migrations,
    /// diagrams, custom elements, ...) validates the whole model.
    /// </summary>
    private static bool TakesIncremental(Element element) =>
        element is Entity or Relation or Mapping or Table or ReferenceType or EnumType or ValueObject || (element is Seed seed && seed.RowsFrom?.Script is null);

    /// <summary>
    /// A whole-model validation that keeps each document's findings (<see cref="WholeValidation"/>). With <paramref name="incremental"/>,
    /// whose previous report has the same scope, the built-in rules run only on the documents the change reaches
    /// (<see cref="IncrementalTargets"/>), and every other document keeps its previous findings; script rules (through their result
    /// cache), the model-level checks, settings, suppressions, tag collapse, positions and order are those of a whole run, so the report
    /// is the one <see cref="ValidateAsync"/> gives for the same scope. Otherwise every document is validated.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="deferScriptedRows">Whether seeds with a rows script are left unjudged (<see cref="ValidationScope.Previews"/>).</param>
    /// <param name="incremental">The previous whole-model validation and the change since, or <see langword="null"/>.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The validation.</returns>
    internal async Task<WholeValidation> ValidateWholeAsync(ModelSnapshot model, bool deferScriptedRows, IncrementalInput? incremental,
        IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var scope = deferScriptedRows ? ValidationScope.Previews : ValidationScope.All;
        string? fallback = null;
        if (incremental is not null && incremental.Previous.DeferScriptedRows != deferScriptedRows)
            (incremental, fallback) = (null, "a previous validation of the other variant");
        // A rows script reads the model (model.withStereotype, names, ...), so any edit may change a scripted seed's rows and findings: the
        // variant that judges scripted seeds validates the whole model while any seed has one (previews leave them unjudged).
        if (incremental is not null && !deferScriptedRows && model.All<Seed>().Any(static s => s.RowsFrom?.Script is not null))
            (incremental, fallback) = (null, "a seed with a rows script");
        var run = await RunAsync(model, scope, incremental, collect: true, progress, ct).ConfigureAwait(false);
        return new WholeValidation(run.Report, run.PerDocument ?? new Dictionary<string, IReadOnlyList<Diagnostic>>(),
            run.TextHolders ?? new HashSet<string>(), deferScriptedRows, run.Validated, incremental is not null) { Fallback = fallback };
    }

    /// <summary>
    /// The change from <paramref name="before"/> to <paramref name="after"/> as an incremental validation takes it, or <see langword="null"/>
    /// when it needs a whole-model validation (<see cref="IncrementalFallback"/>). Works out the documents the change reached in
    /// <paramref name="before"/>, so the caller can let that snapshot go.
    /// </summary>
    /// <param name="before">The snapshot the previous report validated.</param>
    /// <param name="after">The snapshot to validate.</param>
    /// <param name="changed">The ids of the element documents created, changed or deleted between them.</param>
    /// <param name="reason">Why it needs a whole-model validation, when it does.</param>
    /// <returns>The change, or <see langword="null"/>.</returns>
    internal static IncrementalChange? PrepareIncremental(ModelSnapshot before, ModelSnapshot after, IReadOnlySet<string> changed, out string? reason)
    {
        reason = IncrementalFallback(before, after, changed);
        if (reason is not null)
            return null;
        var oneSided = new HashSet<string>(StringComparer.Ordinal);
        var renamed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in changed)
        {
            var was = ElementOf(before, id);
            var now = ElementOf(after, id);
            if (was is null || now is null)
                oneSided.Add(id);
            else if (!string.Equals(was.Name, now.Name, StringComparison.Ordinal))
                renamed.Add(id);
        }

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var then = changed.Where(id => ElementOf(before, id) is not null).Order(StringComparer.Ordinal).ToList();
        if (then.Count > 0)
        {
            var formerActive = ActiveDocuments(before);
            var former = new ValidationContext(before, formerActive, SelectionWalker, null);
            Reach(former, formerActive, then, oneSided, renamed, reached);
        }

        return new IncrementalChange(changed, oneSided, renamed, reached);
    }

    /// <summary>
    /// Why the change from <paramref name="before"/> to <paramref name="after"/> needs a whole-model validation, or <see langword="null"/>
    /// when an incremental one gives the same report: only element documents changed (the same settings, extension schemas and custom
    /// property schemas, custom kinds, rule, rows and calculation scripts, rule declarations, library packs and locale shards), no
    /// document id is held twice or a vocabulary ignored (MQ1004, MQ1009), every changed element is of a kind an incremental validation
    /// takes (<see cref="TakesIncremental"/>) and changed in a way it takes (<see cref="ChangeFallback"/>), and no more than
    /// <see cref="IncrementalLimit"/> documents changed.
    /// </summary>
    /// <param name="before">The snapshot the previous report validated.</param>
    /// <param name="after">The snapshot to validate.</param>
    /// <param name="changed">The ids of the element documents created, changed or deleted between them.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    internal static string? IncrementalFallback(ModelSnapshot before, ModelSnapshot after, IReadOnlyCollection<string> changed)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(changed);
        if (changed.Count > IncrementalLimit)
            return "too many changed documents";
        if (!string.Equals(before.SettingsHash, after.SettingsHash, StringComparison.Ordinal))
            return "settings";
        if (!string.Equals(before.PropertySchemas.Hash, after.PropertySchemas.Hash, StringComparison.Ordinal)
            || !Same(before.Extensions, after.Extensions, e => e.Path, e => e.Hash))
            return "extension schemas";
        if (!Same(before.CustomKinds, after.CustomKinds, k => k.Path, k => k.Hash))
            return "custom kinds";
        if (!Same(before.RuleScripts, after.RuleScripts, s => s.Path, s => s.Hash) || !Same(before.DeclaredRules, after.DeclaredRules, d => d.Path, d => d.Hash))
            return "rule scripts or declarations";
        if (!Same(before.SeedScripts, after.SeedScripts, s => s.Path, s => s.Hash))
            return "rows scripts";
        if (!Same(before.CalculationScripts, after.CalculationScripts, s => s.Path, s => s.Hash))
            return "calculation scripts";
        if (!Same(before.ScriptLibraries, after.ScriptLibraries, l => l.Pack, l => l.Hash))
            return "library packs";
        if (!Same(before.LocaleShards, after.LocaleShards, s => s.Path, s => s.DependencyHash))
            return "locale shards";
        if (HoldsIgnoredDocuments(before) || HoldsIgnoredDocuments(after))
            return "duplicate ids or ignored vocabularies";
        var entities = false;
        foreach (var id in changed)
        {
            // An id that names a part on one side (an attribute, a column) is no plain element change; nor is one whose element changed type.
            if (IsPart(before, id) || IsPart(after, id))
                return "an id that is a part on one side";
            var was = ElementOf(before, id);
            var now = ElementOf(after, id);
            if (was is not null && now is not null && was.GetType() != now.GetType())
                return "an element whose kind changed";
            if ((was is not null && !TakesIncremental(was)) || (now is not null && !TakesIncremental(now)))
                return "a changed " + (now ?? was)!.KindName;
            if (ChangeFallback(before, after, id, was, now) is { } reason)
                return reason;
            entities |= was is Entity || now is Entity;
        }

        // The first revisions block of the model, or the last, changes what every scenario replays (RunRevisions).
        if (entities && Revisions.RevisionCoverage.Of(before).Any != Revisions.RevisionCoverage.Of(after).Any)
            return "the model's first or last revisions block";
        return null;

        static bool HoldsIgnoredDocuments(ModelSnapshot model) => model.LoadDiagnostics.Any(d => d.Rule is "MQ1004" or "MQ1009");
    }

    /// <summary>
    /// Why one element's change needs a whole-model validation although its kind takes an incremental one, or <see langword="null"/>:
    /// <list type="bullet">
    /// <item>an element created or deleted while another document named it or one of its parts (a dangling reference on the snapshot
    /// without it): what reads through that name (inherited and navigated attributes, a type's kind) reaches beyond its referrers;</item>
    /// <item>an entity whose base, stereotypes or package changed: base chains, carriers and package defaults decide flattened attributes,
    /// lifecycle and trigger-point coverage, operation and process scopes and several-types ends (MQ3006, MQ3009, MQ3116, MQ8101, MQ8102,
    /// MQ9903, the expression typer) of documents that do not reference it;</item>
    /// <item>an entity created or deleted while it has derived entities or an end with several types exists (coverage of the ends and of
    /// derived lineages);</item>
    /// <item>a relation with an end of several types (what it covers is read by carriers and expressions that do not name it).</item>
    /// </list>
    /// </summary>
    private static string? ChangeFallback(ModelSnapshot before, ModelSnapshot after, string id, Element? was, Element? now)
    {
        if (was is null && now is not null && Dangling(before, after.GetDocument(id)!))
            return "an element created where references already named it";
        if (now is null && was is not null && Dangling(after, before.GetDocument(id)!))
            return "an element deleted while references still name it";
        switch (was, now)
        {
            case (Entity a, Entity b):
                if (!string.Equals(a.Base, b.Base, StringComparison.Ordinal))
                    return "an entity's base";
                if (!a.Stereotypes.SequenceEqual(b.Stereotypes))
                    return "an entity's stereotypes";
                if (!string.Equals(a.Package, b.Package, StringComparison.Ordinal))
                    return "an entity's package";
                break;
            case (Entity a, null) when HasDerived(before, a.Id) || EndCoverage.Of(before).Any:
                return "an entity deleted with derived entities or ends of several types";
            case (null, Entity b) when HasDerived(after, b.Id) || EndCoverage.Of(after).Any:
                return "an entity created with derived entities or ends of several types";
        }

        if ((was is Relation r1 && EndCoverage.HasSeveralTypes(r1)) || (now is Relation r2 && EndCoverage.HasSeveralTypes(r2)))
            return "a relation with an end of several types";
        return null;

        static bool HasDerived(ModelSnapshot model, string id) => model.All<Entity>().Any(e => string.Equals(e.Base, id, StringComparison.Ordinal));

        // Whether another document of the snapshot without the element names it or a part of it.
        static bool Dangling(ModelSnapshot without, ElementDocument document)
        {
            foreach (var (held, _) in DocumentReader.Scan(document.Json).Ids)
            {
                if (without.ReferencesTo(held).Any(r => !string.Equals(r.FromElementId, document.Element.Id, StringComparison.Ordinal)))
                    return true;
            }

            return without.ReferencesTo(document.Element.Id).Any(r => !string.Equals(r.FromElementId, document.Element.Id, StringComparison.Ordinal));
        }
    }

    /// <summary>The element whose own document an id names, or <see langword="null"/> (<see cref="ModelSnapshot.GetDocument"/> also answers for a part's id with its owner).</summary>
    private static Element? ElementOf(ModelSnapshot model, string id) =>
        model.GetDocument(id) is { } document && string.Equals(document.Element.Id, id, StringComparison.Ordinal) ? document.Element : null;

    /// <summary>Whether an id names a part of another element's document (an attribute, a column, a row) in a snapshot.</summary>
    private static bool IsPart(ModelSnapshot model, string id) =>
        model.TryGetEntry(id, out var entry) && !string.Equals(entry.OwnerId, id, StringComparison.Ordinal);

    /// <summary>Whether two lists hold the same items in the same order, by key and hash.</summary>
    private static bool Same<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, string> key, Func<T, string> hash)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(key(a[i]), key(b[i]), StringComparison.Ordinal) || !string.Equals(hash(a[i]), hash(b[i]), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The documents whose findings a change can alter, on the new snapshot: those the change reached before it
    /// (<see cref="IncrementalChange.FormerTargets"/>) with those it reaches now (<see cref="Reach"/>), every document whose
    /// expressions hold text (they read names, which any change may move; with the uses of a named right and the citers of a constraint
    /// that holds it), every scenario when a process is among them (the replay reads the processes it runs, MQ93xx, MQ95xx), and every
    /// migration fact when one is (the stale facts of a database are judged together, MQ4103).
    /// </summary>
    /// <param name="after">The new snapshot's context.</param>
    /// <param name="active">The new snapshot's active documents.</param>
    /// <param name="input">The previous validation and the change.</param>
    /// <returns>The element ids.</returns>
    private static HashSet<string> IncrementalTargets(ValidationContext after, List<ElementDocument> active, IncrementalInput input)
    {
        var model = after.Model;
        var change = input.Change;
        var reached = new HashSet<string>(change.FormerTargets, StringComparer.Ordinal);
        var now = change.Changed.Where(id => ElementOf(model, id) is not null).Order(StringComparer.Ordinal).ToList();
        if (now.Count > 0)
            Reach(after, active, now, change.OneSided, change.Renamed, reached);

        var citers = new Stack<string>(input.Previous.TextHolders);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (citers.Count > 0)
        {
            var id = citers.Pop();
            if (!visited.Add(id) || model.GetDocument(id) is not { } holder)
                continue;
            reached.Add(id);
            if (holder.Element is Constraint or Right)
            {
                foreach (var reference in model.ReferencesTo(id))
                    citers.Push(reference.FromElementId);
            }
        }

        if (reached.Any(id => model.GetDocument(id)?.Element is Process))
            AddAll<Scenario>(model, reached);
        if (reached.Any(id => model.GetDocument(id)?.Element is Migration))
            AddAll<Migration>(model, reached);
        return reached;
    }

    /// <summary>
    /// The documents a change reaches on one snapshot: the targets of a validation of the changed elements it holds with their referrers
    /// (<see cref="SelectTargets"/>: the changed documents, every document that references them or a sub-element, and their conflict peers),
    /// widened with what the rule inventory found beyond them (<see cref="Widen"/>).
    /// </summary>
    private static void Reach(ValidationContext context, List<ElementDocument> active, List<string> changed, IReadOnlySet<string> oneSided,
        IReadOnlySet<string> renamed, HashSet<string> reached)
    {
        foreach (var doc in SelectTargets(context, active, new ValidationScope(changed, IncludeReferrers: true)))
            reached.Add(doc.Element.Id);
        foreach (var id in changed)
        {
            if (ElementOf(context.Model, id) is not null && context.Model.GetDocument(id) is { } doc)
                Widen(context, doc, oneSided.Contains(id), renamed.Contains(id), reached);
        }
    }

    /// <summary>
    /// The documents a changed document reaches beyond its referrers and conflict peers, from the rule inventory (Validation/README.md,
    /// "Incremental validation"), on the snapshot of <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The snapshot's context.</param>
    /// <param name="document">The changed document, as this snapshot holds it.</param>
    /// <param name="oneSided">Whether the other snapshot does not hold it (created or deleted).</param>
    /// <param name="renamed">Whether its name changed.</param>
    /// <param name="reached">The documents reached so far, added to.</param>
    private static void Widen(ValidationContext context, ElementDocument document, bool oneSided, bool renamed, HashSet<string> reached)
    {
        var model = context.Model;
        var element = document.Element;

        // Every part the document holds, not only those SubElementIds lists (seed rows, codes, bindings, keys, checks, members): their
        // referrers read them (MQ2001, MQ2002, MQ2011, MQ2022, MQ2020).
        var held = DocumentReader.Scan(document.Json).Ids.Select(p => p.Id).Prepend(element.Id).Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in held)
            AddReferrers(model, id, reached);

        // A named right's condition is typed at each lifecycle that uses it (rights-design.md decision 7): what it names reaches them.
        foreach (var id in held)
        {
            foreach (var reference in model.ReferencesTo(id))
            {
                if (model.GetDocument(reference.FromElementId)?.Element is Right right)
                    AddReferrers(model, right.Id, reached);
            }
        }

        switch (element)
        {
            case Entity entity:
                // MQ4009 on a relation end that HasDerived reads, on the base's own file.
                if (entity.Base is { } baseId)
                    reached.Add(baseId);

                // The relations that name the entity: their mappings read its bindings (MQ4009, BoundTable) and their seeds its name (MQ7009).
                foreach (var relationId in model.ReferencesTo(entity.Id).Select(r => r.FromElementId).Distinct(StringComparer.Ordinal).ToList())
                {
                    if (model.GetDocument(relationId) is not { Element: Relation relation })
                        continue;
                    foreach (var id in SubElementIds(relation).Prepend(relation.Id))
                    {
                        foreach (var reference in model.ReferencesTo(id))
                        {
                            if (model.GetDocument(reference.FromElementId)?.Element is Mapping or Seed)
                                reached.Add(reference.FromElementId);
                        }
                    }
                }

                // Seeds read their target's flattened attributes and keys through its bases (MQ7003, MQ7004, MQ7102).
                foreach (var seed in context.SeedsOf(entity.Id))
                    reached.Add(seed.Id);
                foreach (var descendant in context.Descendants(entity.Id))
                {
                    foreach (var seed in context.SeedsOf(descendant.Id))
                        reached.Add(seed.Id);
                }

                // Lifecycle, trigger-point and operation coverage of entities (MQ3116, MQ8101 to MQ8107), and names in their messages; MQ9201
                // names a lifecycle's subject on the other entities that name the lifecycle.
                if (oneSided || renamed)
                {
                    foreach (var processId in model.ReferencesTo(entity.Id).Select(r => r.FromElementId).Distinct(StringComparer.Ordinal).ToList())
                    {
                        if (model.GetDocument(processId)?.Element is not Process)
                            continue;
                        foreach (var reference in model.ReferencesTo(processId))
                        {
                            if (model.GetDocument(reference.FromElementId)?.Element is Entity)
                                reached.Add(reference.FromElementId);
                        }
                    }

                    AddAll<Process>(model, reached);
                    AddAll<TriggerPoint>(model, reached);
                    AddAll<Operation>(model, reached);
                    AddAll<Scenario>(model, reached);
                }

                break;

            case Seed seed:
                switch (model.GetDocument(seed.Target)?.Element)
                {
                    case Table table:
                        reached.Add(table.Id); // MQ7125 reads the seeds of the table
                        // Seeds fill foreign keys through the natural rows of tables any number of keys away (TableSeedRules.FilledValue).
                        ReferencingSeeds(context, table.Id, reached);
                        break;
                    case Entity target:
                        // MQ7113 on the reference types and table seeds of the tables the entity is bound to.
                        reached.UnionWith(BoundColumnPeers(model, target));
                        break;
                    case ReferenceType type when type.FormerCodes.Count > 0:
                        // MQ7019 names the code a former code's replacing row holds, on the holders of the type and the seeds that write it.
                        AddReferrers(model, type.Id, reached);
                        TypedSeeds(model, type.Id, reached);
                        break;
                }

                // A row cycle of three or more seeds (MQ7103) reaches every seed on it.
                if (seed.Rows.Any(r => context.IsCyclicRow(r.Id)))
                {
                    foreach (var (_, seeds) in context.AllSeeds)
                    {
                        foreach (var other in seeds)
                        {
                            if (other.Rows.Any(r => context.IsCyclicRow(r.Id)))
                                reached.Add(other.Id);
                        }
                    }
                }

                break;

            case ReferenceType type:
                foreach (var binding in type.Bindings)
                {
                    // MQ7125 on the table, and on seeds any number of foreign keys away whose natural rows come through the bound type.
                    reached.Add(binding.Table);
                    ReferencingSeeds(context, binding.Table, reached);
                }

                TypedSeeds(model, type.Id, reached);
                TypeReaders(model, oneSided, reached);
                break;

            case EnumType enumType:
                TypedSeeds(model, enumType.Id, reached);
                TypeReaders(model, oneSided, reached);
                break;

            case ValueObject valueObject:
                // Value objects that contain it at any depth (MQ3015 on a containment cycle).
                var containers = new Stack<string>([valueObject.Id]);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (containers.Count > 0)
                {
                    var id = containers.Pop();
                    if (!seen.Add(id))
                        continue;
                    foreach (var other in model.All<ValueObject>())
                    {
                        if (other.Attributes.Any(a => string.Equals(a.Type.Ref, id, StringComparison.Ordinal)))
                        {
                            reached.Add(other.Id);
                            containers.Push(other.Id);
                        }
                    }
                }

                TypeReaders(model, oneSided, reached);
                break;

            case Table table:
                // MQ4016: every holder of a user-defined native type the table uses, so its first holder is judged again.
                if (model.Get<Database>(table.Database) is { } database)
                {
                    foreach (var column in table.Columns)
                    {
                        if (column.NativeType is { } native && context.ClassifyNativeType(database.Dialect, native, out var parsed) == NativeTypeStatus.UserDefined)
                        {
                            foreach (var use in context.UserDefinedTypeUses(database.Id, ValidationContext.UserDefinedTypeKey(database, parsed)))
                                reached.Add(use.Document.Element.Id);
                        }
                    }
                }

                // Seeds fill foreign keys through the natural rows of tables any number of keys away (TableSeedRules.FilledValue).
                ReferencingSeeds(context, table.Id, reached);

                // MQ4084 names the table a reference type is bound to, on tables whose columns hold its codes.
                foreach (var (type, _) in context.BoundTypesOf(table.Id))
                    reached.UnionWith(BoundColumnPeers(model, type));

                // Pending migration facts are judged against the resolved tables of their database (MQ4103).
                AddAll<Migration>(model, reached);
                break;
        }
    }

    /// <summary>The seeds of a table and of every table whose foreign keys reference it, at any depth.</summary>
    private static void ReferencingSeeds(ValidationContext context, string tableId, HashSet<string> reached)
    {
        var model = context.Model;
        var referencing = new Stack<string>([tableId]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (referencing.Count > 0)
        {
            var id = referencing.Pop();
            if (!visited.Add(id))
                continue;
            foreach (var seed in context.SeedsOf(id))
                reached.Add(seed.Id);
            foreach (var other in model.All<Table>())
            {
                if (other.ForeignKeys.Any(fk => string.Equals(fk.ReferencesTable, id, StringComparison.Ordinal)))
                    referencing.Push(other.Id);
            }
        }
    }

    /// <summary>The seeds whose columns hold values of attributes typed by a type (MQ7004, MQ7019): the seeds that name a part of its referrers.</summary>
    private static void TypedSeeds(ModelSnapshot model, string typeId, HashSet<string> reached)
    {
        foreach (var holderId in model.ReferencesTo(typeId).Select(r => r.FromElementId).Distinct(StringComparer.Ordinal).ToList())
        {
            if (model.GetDocument(holderId) is not { } holder)
                continue;
            foreach (var (id, _) in DocumentReader.Scan(holder.Json).Ids)
            {
                foreach (var reference in model.ReferencesTo(id))
                {
                    if (model.GetDocument(reference.FromElementId)?.Element is Seed)
                        reached.Add(reference.FromElementId);
                }
            }
        }
    }

    /// <summary>
    /// What reads an enum, a reference type or a value object without naming it: lifecycles binding an enum through members typed by it
    /// (MQ9204), scenario values (MQ9305) and pending facts (MQ4103); when it is created or deleted, the copy blocks (MQ3046, MQ3048) and the
    /// operations and processes whose members it types (MQ9025, MQ9026).
    /// </summary>
    private static void TypeReaders(ModelSnapshot model, bool oneSided, HashSet<string> reached)
    {
        AddAll<Process>(model, reached);
        AddAll<Scenario>(model, reached);
        AddAll<Migration>(model, reached);
        if (!oneSided)
            return;
        AddAll<Operation>(model, reached);
        foreach (var entity in model.All<Entity>())
        {
            if (entity.Copy is not null)
                reached.Add(entity.Id);
        }
    }

    private static void AddReferrers(ModelSnapshot model, string id, HashSet<string> reached)
    {
        foreach (var reference in model.ReferencesTo(id))
            reached.Add(reference.FromElementId);
    }

    private static void AddAll<T>(ModelSnapshot model, HashSet<string> reached)
        where T : Element
    {
        foreach (var element in model.All<T>())
            reached.Add(element.Id);
    }

    /// <summary>
    /// Whether an element's expressions hold text, or cite a constraint whose condition does (directly or through other constraints), or
    /// inherit a check that does: text is read by name (MQ3108 and the typer's findings), so any change may move what it means.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    internal static bool HoldsText(ModelSnapshot model, Element element)
    {
        // The kinds ExpressionRules.Validate checks (its first lines; a named trigger point's sites are checked by TriggerRules, which
        // types no text): keep the two lists together.
        if (element is not (Table or Stereotype or Entity or Operation or Process or Constraint or Right))
            return false;
        var pending = new Stack<Expr>();
        if (element is Right right)
        {
            if (right.Condition is { } condition)
                pending.Push(condition);
        }
        else
        {
            foreach (var site in ExprSites.Of(model, element))
            {
                if (site.Tree is { } tree)
                    pending.Push(tree);
            }
        }

        if (element is Constraint constraint)
            pending.Push(constraint.Condition);
        if (element is Entity entity)
        {
            var chain = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
            for (var b = entity.Base is { } first ? model.Get<Entity>(first) : null; b is not null && chain.Add(b.Id); b = b.Base is { } next ? model.Get<Entity>(next) : null)
            {
                foreach (var check in b.Checks)
                    pending.Push(check.Condition);
            }
        }

        var cited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node.Text is not null)
                return true;
            if (node.Satisfies is { } id && cited.Add(id) && model.Get<Constraint>(id) is { } next)
                pending.Push(next.Condition);
            foreach (var arg in node.Args ?? [])
                pending.Push(arg);
            foreach (var branch in node.Case ?? [])
            {
                pending.Push(branch.When);
                pending.Push(branch.Then);
            }

            if (node.Else is { } otherwise)
                pending.Push(otherwise);
            foreach (var argument in node.With ?? [])
                pending.Push(argument.Value);
        }

        return false;
    }
}
