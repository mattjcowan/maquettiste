using System.Collections.Frozen;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// Whether a dry run's change touches a stored read key (next-phase-2-design.md section 6.1, decision 3). It reads only the change's
/// documents and files, the two snapshots' indexes, the reached databases and the listed facts, plus the candidate's name index for a
/// kind a stored <c>n:</c> key names and the change can reach (built once, no resolution). Hashes are compared only for keys the
/// change's documents can reach, and each answer is remembered, so the cost follows the change and the distinct stored keys, not the
/// model. A key kind the table does not list counts as touched.
/// </summary>
internal sealed class ImpactKeys
{
    private readonly ModelSnapshot _before;
    private readonly ModelSnapshot _candidate;
    private readonly FrozenSet<string> _changed;
    private readonly IReadOnlyList<BatchPreviewFile> _files;
    private readonly FrozenSet<string> _reached;
    private readonly FrozenSet<string> _factIds;
    private readonly FrozenSet<string> _factDatabases;
    private readonly bool _settingsWritten;
    private readonly bool _shardWritten;
    private readonly Dictionary<string, bool> _answers = new(StringComparer.Ordinal);
    private FrozenSet<ElementKind>? _createdOrDeleted;
    private FrozenSet<string>? _referenced;
    private FrozenSet<string>? _heldNameKinds;
    private bool _qualifiers;

    /// <summary>Creates the test for one dry run.</summary>
    /// <param name="before">The model before the change.</param>
    /// <param name="candidate">The candidate model.</param>
    /// <param name="changed">The ids of the elements the change created, updated, deleted or rewrote.</param>
    /// <param name="files">The files the dry run would write or delete.</param>
    /// <param name="reached">The ids of the databases the change can reach.</param>
    /// <param name="facts">The facts the save would record, or <see langword="null"/> when it reaches every database (it may record
    /// facts in every reached database).</param>
    public ImpactKeys(ModelSnapshot before, ModelSnapshot candidate, IEnumerable<string> changed, IReadOnlyList<BatchPreviewFile> files,
        IEnumerable<string> reached, IReadOnlyList<RecordedFact>? facts)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(reached);
        _before = before;
        _candidate = candidate;
        _changed = changed.ToFrozenSet(StringComparer.Ordinal);
        _files = files;
        _reached = reached.ToFrozenSet(StringComparer.Ordinal);
        _factIds = (facts ?? []).Select(f => f.Id).ToFrozenSet(StringComparer.Ordinal);
        _factDatabases = facts is null ? _reached : facts.Select(f => f.Database).ToFrozenSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Id is not null)
                continue;
            if (IsSettings(file.Path))
                _settingsWritten = true;
            else if (!file.Path.Contains("extensions/", StringComparison.Ordinal))
                _shardWritten = true; // a translation shard or sidecar
        }
    }

    /// <summary>
    /// Whether a <c>px:</c> key (the rest after <c>px:</c>) is touched: the generation side answers from the pack's stored exports
    /// (<see cref="Pipeline.UnitState.Exports"/>); without it every such key counts as touched.
    /// </summary>
    public Func<string, bool>? Exports { get; init; }

    /// <summary>Whether the change touches a read key; remembered per key.</summary>
    /// <param name="key">A dependency key.</param>
    /// <returns><see langword="true"/> when touched.</returns>
    public bool IsTouched(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_answers.TryGetValue(key, out var answer))
            return answer;
        _answers[key] = false; // a px: key whose exports read it back is not touched through itself
        answer = Compute(key);
        _answers[key] = answer;
        return answer;
    }

    private bool Compute(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return true;
        var rest = key[(colon + 1)..];
        return key[..colon] switch
        {
            "e" => ElementTouched(rest),
            "k" => KindSetTouched(rest),
            "r" => ReferrersTouched(rest),
            "n" => NameTouched(rest),
            "l" => LocaleTouched(rest),
            "s" => _settingsWritten && !string.Equals(DependencyHasher.SettingsHashOf(_before.Settings, rest),
                DependencyHasher.SettingsHashOf(_candidate.Settings, rest), StringComparison.Ordinal),
            "d" => _reached.Contains(rest),
            // A batch writes no pack file, and calculation files are scripts, which a batch does not write.
            "t" or "cv" => false,
            "x" => Written(p => p == "extensions/" + rest || p.EndsWith("/extensions/" + rest, StringComparison.Ordinal)),
            "c" => Written(p => p == "extensions/kinds/" + rest + ".json" || p.EndsWith("/extensions/kinds/" + rest + ".json", StringComparison.Ordinal)),
            // The rule view's keys (scripted rows, section 3.4) count as touched.
            "ix" or "rt" => true,
            "px" => Exports?.Invoke(rest) ?? true,
            "cs" => Changes(e => e is Entity or Package or Stereotype)
                && !string.Equals(Processes.SubjectCoverage.CarrierSetHash(_before, rest), Processes.SubjectCoverage.CarrierSetHash(_candidate, rest), StringComparison.Ordinal),
            "sn" => Changes(e => e is Process)
                && !string.Equals(Processes.SubjectCoverage.NamingHash(_before), Processes.SubjectCoverage.NamingHash(_candidate), StringComparison.Ordinal),
            _ => true,
        };
    }

    /// <summary>Whether the change creates, updates or deletes an element that matches, in either model.</summary>
    private bool Changes(Func<Element, bool> matches) =>
        _changed.Any(id => (_before.Get<Element>(id) is { } then && matches(then)) || (_candidate.Get<Element>(id) is { } now && matches(now)));

    /// <summary><c>e:&lt;id&gt;</c>: the document holding the id (in either model) is written or deleted and its dependency hash differs,
    /// absent counting; a fact the save would record is written.</summary>
    private bool ElementTouched(string id)
    {
        if (_factIds.Contains(id))
            return true;
        var then = _before.GetDocument(id);
        var now = _candidate.GetDocument(id);
        if (!(then is not null && _changed.Contains(then.Element.Id)) && !(now is not null && _changed.Contains(now.Element.Id)))
            return false;
        return !string.Equals(then?.DependencyHash ?? DependencyHasher.Absent, now?.DependencyHash ?? DependencyHasher.Absent, StringComparison.Ordinal);
    }

    /// <summary><c>k:&lt;kind&gt;</c>: the change creates or deletes an element of the kind; <c>k:migration</c> also when the save
    /// records, or may record, facts.</summary>
    private bool KindSetTouched(string name)
    {
        if (!KindInfo.TryGet(name, out var info))
            return false;
        if (info.Kind == ElementKind.Migration && _factDatabases.Count > 0)
            return true;
        _createdOrDeleted ??= _changed.Select(id => (Before: _before.Get<Element>(id), After: _candidate.Get<Element>(id)))
            .Where(p => p.Before is null != p.After is null).Select(p => (p.After ?? p.Before)!.Kind).ToFrozenSet();
        return _createdOrDeleted.Contains(info.Kind)
            && !string.Equals(_before.KindSetHash(info.Kind), _candidate.KindSetHash(info.Kind), StringComparison.Ordinal);
    }

    /// <summary><c>r:&lt;id&gt;</c>: a written or deleted document references the id in either model and its referrers' hash differs;
    /// <c>r:&lt;database id&gt;</c> also when the save records, or may record, facts in that database.</summary>
    private bool ReferrersTouched(string id)
    {
        if (_factDatabases.Contains(id))
            return true;
        _referenced ??= _changed.SelectMany(c => _before.ReferencesFrom(c).Concat(_candidate.ReferencesFrom(c))).Select(r => r.ToId)
            .ToFrozenSet(StringComparer.Ordinal);
        return _referenced.Contains(id) && !string.Equals(_before.ReferrersHash(id), _candidate.ReferrersHash(id), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>n:table:&lt;text&gt;</c>: the change reaches a database. <c>n:&lt;kind&gt;:&lt;form&gt;</c>: the change writes or deletes a
    /// document that holds entries of the kind, or one their qualified forms read (a package, a database, a process, a reference type),
    /// and the ids the form finds differ.
    /// </summary>
    private bool NameTouched(string rest)
    {
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && string.Equals(rest[..colon], "table", StringComparison.Ordinal))
            return _reached.Count > 0;
        if (colon <= 0 || !NameIndex.NamedKinds.Contains(rest[..colon]))
            return false;
        var kind = rest[..colon];
        if (_heldNameKinds is null)
        {
            var held = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in _changed)
            {
                foreach (var model in new[] { _before, _candidate })
                {
                    if (model.Get<Element>(id) is not { } element)
                        continue;
                    _qualifiers |= element is Package or Database or Process or ReferenceType;
                    foreach (var entry in NameIndex.Of(element, model.Get<Element>, null, model.PropertySchemas))
                        held.Add(entry.Kind);
                }
            }

            _heldNameKinds = held.ToFrozenSet(StringComparer.Ordinal);
        }

        if (!_qualifiers && !_heldNameKinds.Contains(kind))
            return false;
        var form = rest[(colon + 1)..];
        return !Found(_before, kind, form).SequenceEqual(Found(_candidate, kind, form), StringComparer.Ordinal);

        static IEnumerable<string> Found(ModelSnapshot model, string kind, string form) =>
            model.Names(kind).Find(form).Select(e => e.Id).Order(StringComparer.Ordinal);
    }

    /// <summary><c>l:&lt;locale&gt;:&lt;owner&gt;</c>: the change writes or deletes the owner's document or a shard, and the owner's
    /// hash differs.</summary>
    private bool LocaleTouched(string rest)
    {
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return false;
        var (locale, owner) = (rest[..colon], rest[(colon + 1)..]);
        var ownerWritten = (_before.GetDocument(owner) is { } then && _changed.Contains(then.Element.Id))
            || (_candidate.GetDocument(owner) is { } now && _changed.Contains(now.Element.Id));
        return (ownerWritten || _shardWritten)
            && !string.Equals(_before.Localization.OwnerHash(locale, owner), _candidate.Localization.OwnerHash(locale, owner), StringComparison.Ordinal);
    }

    /// <summary>Whether the dry run writes or deletes a file whose path matches, with other content.</summary>
    private bool Written(Func<string, bool> matches) =>
        _files.Any(f => matches(f.Path) && !string.Equals(f.Before, f.After, StringComparison.Ordinal));

    private static bool IsSettings(string path) =>
        path == ModelPaths.SettingsFile || path.EndsWith("/" + ModelPaths.SettingsFile, StringComparison.Ordinal);
}
