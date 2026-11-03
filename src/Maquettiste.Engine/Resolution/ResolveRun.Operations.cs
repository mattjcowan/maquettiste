using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Operations (next-phase-2-design.md section 4.1). Runs after the processes, since an operation's fired events and each event's
/// <see cref="REvent.FiredBy"/> need the resolved events; <see cref="ROperation.Requires"/> is linked after every other pass
/// (<see cref="LinkOperationRequires"/>). The first cut resolves own errors only.
/// </summary>
internal sealed partial class ResolveRun
{
    private const string OperationKind = "k:operation";

    private readonly Dictionary<string, ROperation> _operations = new(StringComparer.Ordinal);
    private List<ROperation> _operationOrder = [];

    /// <summary>Resolves every operation, then each entity's operations and each event's operations that fire it.</summary>
    private void ResolveOperations()
    {
        var events = new Dictionary<string, REvent>(StringComparer.Ordinal);
        foreach (var lookup in _processLookups.Values)
        {
            foreach (var (id, ev) in lookup.Events)
                events.TryAdd(id, ev);
        }

        foreach (var operation in Model.All<Operation>())
        {
            Ct.ThrowIfCancellationRequested();
            var r = new ROperation();
            if (!_operations.TryAdd(operation.Id, r))
                continue;
            BuildOperation(operation, r, events);
            Register(r);
        }

        _operationOrder = [.. _operations.Values.OrderBy(o => o.Package?.QualifiedName ?? "", StringComparer.Ordinal)
            .ThenBy(o => o.Subject is null ? 0 : 1).ThenBy(o => o.Subject?.Name ?? "", StringComparer.Ordinal)
            .ThenBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal)];
        var order = new Dictionary<ROperation, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _operationOrder.Count; i++)
            order[_operationOrder[i]] = i;

        // The derived lists come from the index's references, never from a scan of every operation: a reference to the entity or the
        // event changes its r: key, and an operation appearing or disappearing changes k:operation.
        RList<ROperation> Referrers(string id, string field) => new(
            Model.ReferencesTo(id)
                .Where(reference => string.Equals(reference.Field, field, StringComparison.Ordinal) && reference.FromId == reference.FromElementId)
                .Select(reference => _operations.GetValueOrDefault(reference.FromElementId))
                .OfType<ROperation>()
                .Distinct()
                .OrderBy(o => order[o]),
            [Keys.Referrers(id), OperationKind]);

        foreach (var entity in _entities.Values.Concat(_promoted.Values).Where(e => e.Id.Length > 0))
            entity.Operations = Referrers(entity.Id, "subject");
        foreach (var ev in events.Values)
            ev.FiredBy = Referrers(ev.Id, "fires");
    }

    private void BuildOperation(Operation operation, ROperation r, Dictionary<string, REvent> events)
    {
        var deps = DepsOf(r);
        FillCommon(r, operation, deps);
        if (operation.Subject is { } subjectId)
        {
            deps.Element(subjectId); // its order and its package fallback read the subject
            if (_entities.TryGetValue(subjectId, out var subject) && subject.Id.Length > 0)
                r.Subject = subject;
        }

        r.Package = PackageOf(operation.Package) ?? r.Subject?.Package;
        r.Level = r.Subject is null ? null : ResolutionValues.Kebab(operation.Level);
        r.Effect = ResolutionValues.Kebab(operation.Effect);
        var fileKeys = new DependencySet(Keys).Element(operation.Id).ToList();

        r.Input = new RList<ROperationMember>(Members(operation.Input, r, null), fileKeys);

        var outcomes = new List<ROperationOutcome>(operation.Outcomes.Count);
        foreach (var source in operation.Outcomes.DistinctBy(o => o.Id, StringComparer.Ordinal))
        {
            var outcome = new ROperationOutcome { Operation = r };
            var outcomeDeps = DepsOf(outcome);
            FillCommon(outcome, source, null, outcomeDeps);
            outcomeDeps.Element(operation.Id);
            outcome.Package = r.Package;
            outcome.Completes = ResolutionValues.Kebab(source.Completes);
            if (source.Returns is { } returns)
            {
                var resolved = new ROperationReturns
                {
                    Members = new RList<ROperationMember>(returns.Members is { } members ? Members(members, r, outcome) : [], fileKeys),
                    IsList = returns.List,
                };
                if (returns.Entity is { } entityId)
                {
                    outcomeDeps.Element(entityId);
                    resolved.Entity = _entities.TryGetValue(entityId, out var entity) && entity.Id.Length > 0 ? entity : null;
                }

                outcome.Returns = resolved;
            }

            Register(outcome);
            outcomes.Add(outcome);
        }

        r.Outcomes = new RList<ROperationOutcome>(outcomes, fileKeys);
        r.DefaultOutcome = (operation.DefaultOutcome is { } named ? outcomes.FirstOrDefault(o => string.Equals(o.Id, named, StringComparison.Ordinal)) : null)
            ?? outcomes.FirstOrDefault();
        if (r.DefaultOutcome is { } chosen)
            chosen.IsDefault = true;

        var errors = new List<ROperationError>(operation.Errors.Count);
        foreach (var source in operation.Errors.DistinctBy(e => e.Id, StringComparer.Ordinal))
        {
            var error = new ROperationError { Operation = r, Code = source.Code ?? source.Name };
            var errorDeps = DepsOf(error);
            FillCommon(error, source, null, errorDeps);
            errorDeps.Element(operation.Id);
            error.Package = r.Package;
            error.Input = source.Input is { } input ? r.Input.FirstOrDefault(m => string.Equals(m.Id, input, StringComparison.Ordinal)) : null;
            Register(error);
            errors.Add(error);
        }

        r.Errors = new RList<ROperationError>(errors, fileKeys);

        // A fired event exists while its process file holds it: the list depends on those files too.
        var firesKeys = new DependencySet(Keys).AddRange(fileKeys);
        var fired = new List<REvent>();
        foreach (var id in operation.Fires)
        {
            if (Model.TryGetEntry(id, out var entry))
                firesKeys.Element(entry.OwnerId);
            if (events.TryGetValue(id, out var ev) && !fired.Contains(ev))
                fired.Add(ev);
        }

        r.Fires = new RList<REvent>(fired, firesKeys.ToList());
    }

    private List<ROperationMember> Members(IReadOnlyList<OperationMember> members, ROperation operation, ROperationOutcome? outcome)
    {
        var list = new List<ROperationMember>(members.Count);
        foreach (var source in members.DistinctBy(m => m.Id, StringComparer.Ordinal))
        {
            var m = new ROperationMember { Operation = operation, Outcome = outcome, Order = list.Count };
            var deps = DepsOf(m);
            FillCommon(m, source, null, deps);
            deps.Element(operation.Id);
            m.Package = operation.Package;
            m.Required = source.Required;
            m.Default = ResolutionValues.Plain(source.Default);
            m.Collection = source.Collection;
            m.Sensitive = source.Sensitive is { } sensitive ? ResolutionValues.Kebab(sensitive) : null;
            RScalarType? scalar = null;
            if (source.Type is { } type)
            {
                m.Type = TypeOf(type, deps, out scalar);
            }
            else if (source.KeyOf is { } entityId)
            {
                deps.Element(entityId);
                if (_entities.TryGetValue(entityId, out var entity) && entity.Id.Length > 0)
                {
                    m.KeyOf = entity;
                    if (entity.Key is { Attributes.Count: 1 } key)
                        m.Type = key.Attributes[0].Type;
                }
            }

            m.Length = source.Length ?? scalar?.Length ?? (source.Type is null ? null : m.Type?.ReferenceType?.Code.Length);
            m.Precision = source.Precision ?? scalar?.Precision;
            m.Scale = source.Scale ?? scalar?.Scale;
            m.Validation = Validation(source.Validation, scalar is null ? null : Model.Get<ScalarType>(scalar.Id)?.Validation);
            Register(m);
            list.Add(m);
        }

        return list;
    }

    /// <summary>
    /// Links each operation's <see cref="ROperation.Requires"/>, after every pass has registered its objects, so an id of any resolved
    /// kind resolves whatever the pass order. Each id's file joins the list's keys: the item appears or disappears with it.
    /// </summary>
    private void LinkOperationRequires()
    {
        foreach (var r in _operationOrder)
        {
            if (Model.Get<Operation>(r.Id) is not { } operation)
                continue;
            var keys = new DependencySet(Keys).Element(operation.Id);
            var required = new List<IResolvedObject>();
            foreach (var id in operation.Requires)
            {
                keys.Element(Model.TryGetEntry(id, out var entry) ? entry.OwnerId : id);
                if (_byId.TryGetValue(id, out var target) && !required.Contains(target))
                    required.Add(target);
            }

            r.Requires = new RList<IResolvedObject>(required, keys.ToList());
        }
    }
}
