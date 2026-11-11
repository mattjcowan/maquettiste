using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Operations (next-phase-2-design.md section 4.1). Runs after the processes, since an operation's fired events and each event's
/// <see cref="REvent.FiredBy"/> need the resolved events; <see cref="ROperation.Requires"/> is linked after every other pass
/// (<see cref="LinkOperationRequires"/>). An operation's errors are its own, then those the stereotypes of its effective set contribute
/// (<see cref="OperationErrors"/>), keyed <c>&lt;operationId&gt;.&lt;errorId&gt;</c>. Its guards (next-phase-3-design.md section 3.1)
/// are typed in the operation's scope and linked to the error each reports (<see cref="ResolveGuards"/>).
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

        DrainInStateFills(); // a guard's in_state (object-effects-design.md section 6)
        LinkOperations();

        _operationOrder = [.. _operations.Values.OrderBy(o => o.Package?.QualifiedName ?? "", StringComparer.Ordinal)
            .ThenBy(o => o.Subject is null ? 0 : 1).ThenBy(o => o.Subject?.Name ?? "", StringComparer.Ordinal)
            .ThenBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal)];
        var order = new Dictionary<ROperation, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _operationOrder.Count; i++)
            order[_operationOrder[i]] = i;

        // The derived lists come from the index's references, never from a scan of every operation: a reference to the entity or the
        // event changes its r: key, and an operation appearing or disappearing changes k:operation.
        List<ROperation> Referrers(string id, string field) => [.. Model.ReferencesTo(id)
            .Where(reference => string.Equals(reference.Field, field, StringComparison.Ordinal) && reference.FromId == reference.FromElementId)
            .Select(reference => _operations.GetValueOrDefault(reference.FromElementId))
            .OfType<ROperation>()
            .Distinct()
            .OrderBy(o => order[o])];

        foreach (var entity in _entities.Values.Concat(_promoted.Values).Where(e => e.Id.Length > 0))
            entity.Operations = new RList<ROperation>(Referrers(entity.Id, "subject"), [Keys.Referrers(entity.Id), OperationKind]);

        // An empty fired_by keys on the event's referrers only (processes-operations-design.md section 9): an operation that starts firing the
        // event references it, which changes r:, so k:operation would only render again when an unrelated operation is added. A list that
        // is not empty keeps k:operation, since its order reads the operations' names and subjects.
        foreach (var ev in events.Values)
        {
            var firing = Referrers(ev.Id, "fires");
            ev.FiredBy = new RList<ROperation>(firing, firing.Count == 0 ? [Keys.Referrers(ev.Id)] : [Keys.Referrers(ev.Id), OperationKind]);
        }

        CallersOfOperations();
    }

    // Runs what joins actions' calls and service steps to the operations they name (ResolveRun.Processes.cs, _operationLinks).
    private void LinkOperations()
    {
        foreach (var link in _operationLinks)
            link();
        _operationLinks.Clear();
    }

    // Each operation's called_by and invoked_by (processes-operations-design.md section 9): the actions that call it and the service steps that
    // name it, in the processes' order, then file order. Every action or step that names the operation is in a file that references it, so the
    // lists key on its referrers.
    private void CallersOfOperations()
    {
        var calledBy = new Dictionary<ROperation, List<RAction>>(ReferenceEqualityComparer.Instance);
        var invokedBy = new Dictionary<ROperation, List<RInvoke>>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<RAction>(ReferenceEqualityComparer.Instance);
        foreach (var process in _processOrder)
        {
            foreach (var action in process.Actions)
            {
                if (action.Call is { } call && seen.Add(action))
                    (calledBy.TryGetValue(call.Operation, out var list) ? list : calledBy[call.Operation] = []).Add(action);
            }

            foreach (var invoke in process.Invokes)
            {
                if (invoke.Operation is { } operation)
                    (invokedBy.TryGetValue(operation, out var list) ? list : invokedBy[operation] = []).Add(invoke);
            }
        }

        foreach (var (id, operation) in _operations)
        {
            IReadOnlyList<string> keys = [Keys.Referrers(id)];
            operation.CalledBy = new RList<RAction>(calledBy.GetValueOrDefault(operation) ?? [], keys);
            operation.InvokedBy = new RList<RInvoke>(invokedBy.GetValueOrDefault(operation) ?? [], keys);
        }
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
                    IsFile = returns.File,
                    MediaTypes = [.. returns.MediaTypes],
                    IsList = returns.List,
                    Paging = returns.Paging is { } paging ? ResolutionValues.Kebab(paging) : null,
                    MaxPageSize = returns.MaxPageSize,
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

        // Inherited errors follow the own ones; the list changes with the stereotypes of the effective set and the packages whose defaults
        // reach the operation.
        var errorKeys = new DependencySet(Keys).AddRange(fileKeys);
        AddPackageChain(errorKeys, Model.HolderOf(operation).Package);
        foreach (var applied in AppliedOf(operation))
            errorKeys.Element(applied.Stereotype.Id);
        foreach (var inherited in OperationErrors.Of(Model, operation))
        {
            // Two stereotypes giving an error of one id: the first in effective-set order stays, the second is MQ3026.
            if (_byId.ContainsKey(inherited.Key))
                continue;
            var source = inherited.Member;
            var error = new ROperationError { Operation = r, Code = source.Code ?? source.Name };
            var errorDeps = DepsOf(error);
            var stereotypeId = inherited.From.Stereotype.Id;
            var descriptionOwner = inherited.Redefined.Contains("description", StringComparer.Ordinal) ? operation.Id : stereotypeId;
            FillCommon(error, source, Model.GetDocument(descriptionOwner)?.SidecarText, errorDeps);
            error.Id = inherited.Key;
            errorDeps.Element(operation.Id).Element(stereotypeId);
            error.Package = r.Package;
            error.Input = source.Input is { } input ? r.Input.FirstOrDefault(m => string.Equals(m.Id, input, StringComparison.Ordinal)) : null;
            error.MemberOrigin = new RMemberOrigin
            {
                Source = inherited.From.Source switch
                {
                    StereotypeSource.SchemaDefault => "schema-default",
                    StereotypeSource.PackageDefault => "package-default",
                    _ => "stereotype",
                },
                Stereotype = StereotypeEntryOf(r, inherited.From),
                MemberId = inherited.MemberId,
                Redefined = inherited.Redefined,
            };
            Register(error);
            errors.Add(error);
        }

        r.Errors = new RList<ROperationError>(errors, errorKeys.ToList());
        if (operation.Guards.Count > 0)
            r.Guards = new RList<ROperationGuard>(ResolveGuards(operation, r, errorKeys), fileKeys);

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

    /// <summary>
    /// An operation's guards, in file order: each condition typed at a guard site over the input members and the subject's attributes,
    /// its paths ending at the <see cref="ROperationMember"/>s and the subject's <see cref="RAttribute"/>s, and the error it reports
    /// found among the resolved errors, an inherited one by its keyed id or its stereotype id. A guard depends on its file, the subject
    /// and every base of it, whose attributes and navigations it reads, what its condition names beyond them (an enum, a seed, a
    /// constraint, a relation it reads through and the entity at its far end) and the keys of the errors.
    /// </summary>
    private List<ROperationGuard> ResolveGuards(Operation operation, ROperation r, DependencySet errorKeys)
    {
        var path = Model.GetDocument(operation.Id)?.Path;
        var input = r.Input.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var subject = r.Subject?.Attributes.DistinctBy(a => a.Id, StringComparer.Ordinal).ToDictionary(a => a.Id, StringComparer.Ordinal) ?? [];
        object? Target(ExprMember member) => input.TryGetValue(member.Id, out var m) ? m : AttributeTarget(subject, member.Id);

        var guards = new List<ROperationGuard>(operation.Guards.Count);
        foreach (var source in operation.Guards.DistinctBy(g => g.Id, StringComparer.Ordinal))
        {
            var guard = new ROperationGuard { Operation = r };
            var deps = DepsOf(guard);
            FillCommon(guard, source, null, deps);
            deps.Element(operation.Id);
            if (operation.Subject is { } subjectId)
            {
                deps.Element(subjectId);
                if (EntitySource(subjectId) is { } subjectEntity)
                {
                    foreach (var level in Chain(subjectEntity))
                        deps.Element(level.Id);
                }
            }

            guard.Package = r.Package;
            if (source.Error is { } error)
            {
                deps.AddRange(errorKeys.ToList());
                var own = error.Split(':', '.')[^1];
                guard.Error = r.Errors.FirstOrDefault(e => string.Equals(e.Id, error, StringComparison.Ordinal))
                    ?? r.Errors.FirstOrDefault(e => string.Equals(e.Id.Split('.')[^1], own, StringComparison.Ordinal));
            }

            if (ExprResolution.Type(Model, operation, source.Id) is { } typed)
            {
                var root = new ExprRoot(typed.Tree, typed.Typed, typed.Scope, ExprSiteKind.Guard, ExprResolution.ApiColumns(Model, source.Id, path));
                guard.Condition = ExprResolution.Build(root, Target, EnumMemberOf, InStateFill(null, operation, deps));
                ExprResolution.AddNames(Model, typed.Typed, deps);
            }

            Register(guard);
            guards.Add(guard);
        }

        return guards;
    }

    /// <summary>The operation's resolved entry for an applied stereotype (its annotations list the effective set in order).</summary>
    private RStereotype? StereotypeEntryOf(ROperation r, AppliedStereotype from)
    {
        if (Model.Get<Operation>(r.Id) is not { } operation)
            return null;
        var effective = AppliedOf(operation);
        for (var i = 0; i < effective.Count && i < r.Stereotypes.Count; i++)
        {
            if (effective[i].Equals(from))
                return r.Stereotypes[i];
        }

        return r.Stereotypes.FirstOrDefault(s => string.Equals(s.Key, from.Stereotype.Key, StringComparison.Ordinal));
    }

    /// <summary>A stereotype's own errors, resolved on its definition (<see cref="RStereotypeDefinition.Errors"/>): no operation, no input.</summary>
    private RList<ROperationError> StereotypeErrors(Stereotype stereotype, RStereotypeDefinition definition)
    {
        var sidecar = Model.GetDocument(stereotype.Id)?.SidecarText;
        var errors = new List<ROperationError>(stereotype.Errors.Count);
        foreach (var source in stereotype.Errors.DistinctBy(e => e.Id, StringComparer.Ordinal))
        {
            var error = new ROperationError { Code = source.Code ?? source.Name };
            var deps = DepsOf(error);
            FillCommon(error, source, sidecar, deps);
            deps.Element(stereotype.Id);
            error.Package = definition.Package;
            Register(error);
            errors.Add(error);
        }

        return new RList<ROperationError>(errors, new DependencySet(Keys).Element(stereotype.Id).ToList());
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
            m.IsFile = source.File;
            m.MediaTypes = [.. source.MediaTypes];
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
