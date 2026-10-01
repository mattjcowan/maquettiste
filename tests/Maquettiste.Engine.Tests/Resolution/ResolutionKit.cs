using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>Resolves builder models and renders resolved models as readable text (ids replaced by names) for golden files.</summary>
internal static partial class ResolutionKit
{
    public static EngineOptions Options { get; } = new() { RepoRoot = "/repo", CacheDirectory = "/cache" };

    public static ResolvedModel Resolve(ModelSnapshot model) =>
        new ModelResolver(Options).ResolveAsync(model, null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    public static ResolvedModel Resolve(ModelBuilder builder) => Resolve(builder.Build());

    public static RDatabase Db(this ResolvedModel model, string name) => model.Databases.Single(d => d.Name == name);

    public static RTable Table(this RDatabase db, string name) =>
        db.Tables.SingleOrDefault(t => t.Name == name) ?? throw new InvalidOperationException(
            $"No table '{name}' in {db.Name}; tables: {string.Join(", ", db.Tables.Select(t => t.Name))}");

    public static RColumn Column(this RTable table, string name) =>
        table.Columns.SingleOrDefault(c => c.Name == name) ?? throw new InvalidOperationException(
            $"No column '{name}' in {table.Name}; columns: {string.Join(", ", table.Columns.Select(c => c.Name))}");

    public static REntity Entity(this ResolvedModel model, string name) => model.Entities.Single(e => e.Name == name);

    public static RRelation Relation(this ResolvedModel model, string name) => model.Relations.Single(r => r.Name == name);

    public static IReadOnlyList<string> Names(this IEnumerable<RColumn> columns) => [.. columns.Select(c => c.Name)];

    /// <summary>A text rendering of the whole resolved model, with every id replaced by a readable label.</summary>
    public static string Dump(ResolvedModel model)
    {
        var labels = Labels(model.Source);
        var sb = new StringBuilder();
        string L(string? value) => value is null ? "-" : Relabel(value, labels);
        string V(object? value) => value switch
        {
            null => "-",
            string s => "'" + s + "'",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        foreach (var package in model.Packages)
            sb.Append("package ").Append(package.QualifiedName).Append('\n');
        foreach (var e in model.Entities)
        {
            sb.Append("entity ").Append(e.Name).Append(" plural=").Append(e.PluralName).Append(" package=").Append(e.Package?.QualifiedName ?? "-")
                .Append(" abstract=").Append(V(e.IsAbstract)).Append(" base=").Append(e.Base?.Name ?? "-").Append(" promoted=").Append(V(e.IsPromoted))
                .Append(" stereotypes=").Append(string.Join(',', e.Stereotypes.Select(s => s.Key))).Append('\n');
            if (e.Key is { } key)
                sb.Append("  key ").Append(string.Join(',', key.Attributes.Select(a => a.Name))).Append(' ').Append(key.Strategy)
                    .Append(string.Concat(key.Sequences.Select(s => $" seq[{s.Key}]={s.Value.Name}"))).Append('\n');
            foreach (var a in e.Attributes)
            {
                sb.Append("  attr ").Append(a.Order).Append(' ').Append(a.Name).Append(": ").Append(a.Type.Kind).Append(' ').Append(a.Type.Name)
                    .Append(a.Required ? " required" : "").Append(a.Collection ? " collection" : "").Append(a.IsInherited ? " inherited" : "")
                    .Append(a.IsVirtual ? " virtual(" + a.FromStereotype!.Key + ")" : "").Append(a.Length is { } len ? " length=" + len : "")
                    .Append('\n');
            }

            foreach (var n in e.Navigations)
                sb.Append("  nav ").Append(n.Name).Append(" -> ").Append(n.Target.Name).Append(n.IsCollection ? "[]" : "").Append(" via ")
                    .Append(n.Relation.Name).Append('\n');
            foreach (var (db, m) in e.Mappings)
            {
                sb.Append("  mapping ").Append(db).Append(" table=").Append(m.Table.Name).Append(" inheritance=").Append(m.Inheritance ?? "-")
                    .Append(" discriminator=").Append(m.DiscriminatorColumn?.Name ?? "-").Append('=').Append(V(m.DiscriminatorValue)).Append('\n');
                foreach (var c in m.Columns)
                    sb.Append("    ").Append(L(c.AttributePath)).Append(" -> ").Append(c.Column.Table.Name).Append('.').Append(c.Column.Name).Append('\n');
                foreach (var (nav, path) in m.Joins)
                    sb.Append("    join ").Append(nav).Append(": ").Append(Join(path)).Append('\n');
            }
        }

        foreach (var r in model.Relations)
        {
            sb.Append("relation ").Append(r.Name).Append(' ').Append(r.Cardinality).Append(' ').Append(r.RelationKind).Append(" ends=")
                .Append(string.Join(',', r.Ends.Select(e => $"{e.Role}:{e.Entity.Name}[{e.Min}..{e.Max}]"))).Append('\n');
            foreach (var (db, m) in r.Mappings)
            {
                sb.Append("  mapping ").Append(db).Append(' ').Append(m.Shape)
                    .Append(m.ForeignKey is { } fk ? " fk=" + fk.Name : "")
                    .Append(m.JunctionTable is { } j ? " junction=" + j.Name : "")
                    .Append(m.PromotedEntity is { } p ? " promoted=" + p.Name : "").Append('\n');
            }
        }

        foreach (var db in model.Databases)
        {
            sb.Append("database ").Append(db.Name).Append(' ').Append(db.Dialect).Append(" default-schema=").Append(db.DefaultSchema ?? "-")
                .Append(" limit=").Append(V(db.MaxIdentifierLength)).Append(" schemas=")
                .Append(string.Join(',', db.Schemas.Select(s => Annotations(s, V) is { Length: > 0 } a ? s.Name + "[" + a.TrimStart() + "]" : s.Name)))
                .Append(Annotations(db, V)).Append('\n');
            foreach (var t in db.Tables)
            {
                sb.Append("  table ").Append(t.Schema is null ? "" : t.Schema + ".").Append(t.Name).Append(" key=").Append(L(t.Key)).Append(' ').Append(t.Origin)
                    .Append(t.Entity is { } te ? " entity=" + te.Name : "").Append(t.Relation is { } tr ? " relation=" + tr.Name : "")
                    .Append(t.IsJunction ? " junction" : "").Append(Annotations(t, V)).Append('\n');
                foreach (var c in t.Columns)
                {
                    sb.Append("    ").Append(c.Position).Append(' ').Append(c.Name).Append(' ').Append(c.Type)
                        .Append(c.Length is { } l ? "(" + l + ")" : "").Append(c.Precision is { } pr ? $"[{pr},{V(c.Scale)}]" : "")
                        .Append(" native=").Append(c.NativeType).Append(c.Nullable ? " null" : " not-null")
                        .Append(c.IsPrimaryKey ? " pk" : "").Append(c.IsForeignKey ? " fk" : "").Append(c.IsDiscriminator ? " discriminator" : "")
                        .Append(c.Identity ? " identity" : "").Append(c.Sequence is { } s ? " seq=" + s.Name : "")
                        .Append(c.Default is { } d ? " default=" + V(d) : "").Append(" key=").Append(L(c.Key)).Append(Annotations(c, V)).Append('\n');
                }

                if (t.PrimaryKey is { } pk)
                    sb.Append("    pk ").Append(pk.Name).Append(" (").Append(string.Join(',', pk.Columns.Names())).Append(")\n");
                foreach (var fk in t.ForeignKeys)
                    sb.Append("    fk ").Append(fk.Name).Append(" (").Append(string.Join(',', fk.Columns.Names())).Append(") -> ").Append(fk.ReferencedTable.Name)
                        .Append(" (").Append(string.Join(',', fk.ReferencedColumns.Names())).Append(") on-delete=").Append(fk.OnDelete)
                        .Append(fk.Relation is { } fr ? " relation=" + fr.Name : "").Append(fk.End is { } fe ? " end=" + fe.Role : "").Append('\n');
                foreach (var u in t.Uniques)
                    sb.Append("    unique ").Append(u.Name).Append(" (").Append(string.Join(',', u.Columns.Names())).Append(")\n");
                foreach (var i in t.Indexes)
                    sb.Append("    index ").Append(i.Name).Append(i.Unique ? " unique" : "").Append(" (")
                        .Append(string.Join(',', i.Columns.Select(c => c.Column.Name + (c.Descending ? " desc" : "")))).Append(")\n");
                foreach (var ck in t.Checks)
                    sb.Append("    check ").Append(ck.Name).Append(' ').Append(ck.Expression).Append('\n');
            }

            foreach (var s in db.Sequences)
                sb.Append("  sequence ").Append(s.Name).Append(' ').Append(s.Type).Append(" native=").Append(s.NativeType).Append(" key=").Append(L(s.Id))
                    .Append(Annotations(s, V)).Append('\n');
            foreach (var v in db.Views)
                sb.Append("  view ").Append(v.Name).Append(Annotations(v, V)).Append(": ").Append(v.Body).Append('\n');
        }

        DumpProcesses(model, sb, L, V);
        foreach (var d in model.Diagnostics)
            sb.Append("diagnostic ").Append(d.Rule).Append(' ').Append(d.Severity).Append(' ').Append(L(d.Message)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The annotations of a physical object, each only when set (a database, schema, table, column, view or sequence without a file or
    /// an entry of its own has none, so the lines of unannotated objects read as before).
    /// </summary>
    private static string Annotations(RAnnotated o, Func<object?, string> v)
    {
        var sb = new StringBuilder();
        if (o.DisplayName.Length > 0)
            sb.Append(" display=").Append(v(o.DisplayName));
        if (o.PluralName.Length > 0)
            sb.Append(" plural=").Append(v(o.PluralName));
        if (o.Description is { } description)
            sb.Append(" description=").Append(v(description));
        if (o.Stereotypes.Count > 0)
            sb.Append(" stereotypes=").Append(string.Join(',', o.Stereotypes.Select(s => s.Key)));
        if (o.Tags.Count > 0)
            sb.Append(" tags=").Append(string.Join(',', o.Tags));
        if (o.Category is { } category)
            sb.Append(" category=").Append(category.Path);
        if (o.Properties.Count > 0)
            sb.Append(" properties={").Append(string.Join(',', o.Properties.Select(p => p.Key + "=" + v(p.Value)))).Append('}');
        if (o.Generation.Count > 0)
            sb.Append(" generation=").Append(string.Join(',', o.Generation.Keys));
        return sb.ToString();
    }

    /// <summary>Processes, actors and scenarios (nothing for a model without them, so older goldens are unchanged).</summary>
    private static void DumpProcesses(ResolvedModel model, StringBuilder sb, Func<string?, string> l, Func<object?, string> v)
    {
        string Names<T>(IEnumerable<T> items, Func<T, string> name) => items.Any() ? string.Join(',', items.Select(name)) : "-";
        string Keys(IEnumerable<string> keys) => string.Join(' ', keys.Select(k => l(k)));
        string Map(IReadOnlyDictionary<string, object?> map) => "{" + string.Join(',', map.Select(p => p.Key + "=" + Value(p.Value))) + "}";
        string Value(object? value) => value is not string && value is IEnumerable<object?> list ? "[" + string.Join(',', list.Select(Value)) + "]" : v(value);
        string Path(RState? state) => state?.Path ?? "-";

        foreach (var p in model.Processes)
        {
            sb.Append("process ").Append(p.Name).Append(" display='").Append(p.DisplayName).Append("' package=").Append(p.Package?.QualifiedName ?? "-")
                .Append(" use=").Append(p.Use).Append(" subject=").Append(p.Subject?.Name ?? "-").Append(" bound=").Append(p.BoundAttribute?.Name ?? "-")
                .Append(" enum=").Append(p.BoundEnum?.Name ?? "-").Append(" initial=").Append(Path(p.Initial)).Append('\n');
            sb.Append("  keys ").Append(Keys(p.Dependencies)).Append('\n');
            foreach (var a in p.Context)
                sb.Append("  context ").Append(a.Order).Append(' ').Append(a.Name).Append(": ").Append(a.Type.Kind).Append(' ').Append(a.Type.Name)
                    .Append(" default=").Append(v(a.Default)).Append('\n');
            foreach (var e in p.Events)
                sb.Append("  event ").Append(e.Name).Append(" actors=").Append(Names(e.Actors, x => x.Name)).Append(" payload=")
                    .Append(Names(e.Payload, x => x.Name + ":" + x.Type.Name + (x.Required ? "!" : ""))).Append(" transitions=").Append(e.Transitions.Count).Append('\n');
            foreach (var g in p.Guards)
                sb.Append("  guard ").Append(g.Name).Append(g.IsStub ? " stub" : " expr=" + g.Expression).Append(" used-by=").Append(Names(g.UsedBy, t => t.Label)).Append('\n');
            foreach (var a in p.Actions)
                sb.Append("  action ").Append(a.Name).Append(a.IsStub ? " stub" : " expr=" + a.Expression).Append(" raises=").Append(Names(a.Raises, x => x.Name))
                    .Append(" used-by=").Append(Names(a.UsedBy, n => n switch { RTransition t => "t(" + t.Label + ")", RState st => "s(" + st.Path + ")", _ => n.Kind })).Append('\n');
            sb.Append("  roots ").Append(Names(p.States, x => x.Name)).Append(" atomic=").Append(Names(p.AtomicStates, x => x.Path))
                .Append(" bound=").Append(Names(p.BoundStates, x => x.Name + (x.BoundMember is { } m ? "=" + m.Name : ""))).Append('\n');
            foreach (var s in p.AllStates)
            {
                sb.Append("  state ").Append(new string(' ', 2 * (s.Depth - 1))).Append(s.Path).Append(' ').Append(s.Type).Append(" depth=").Append(s.Depth)
                    .Append(" parent=").Append(Path(s.Parent)).Append(" children=").Append(Names(s.Children, x => x.Name))
                    .Append(s.Initial is { } i ? " initial=" + i.Name : "").Append(s.History is { } h ? " history=" + h + " default=" + Path(s.DefaultTarget) : "")
                    .Append(s.IsFinal ? " final" : "").Append(s.IsAtomic ? " leaf" : "").Append(s.RegionIndex is { } r ? " region=" + r : "")
                    .Append(s.Entry.Count > 0 ? " entry=" + Names(s.Entry, x => x.Name) : "").Append(s.Exit.Count > 0 ? " exit=" + Names(s.Exit, x => x.Name) : "")
                    .Append(s.Invoke.Count > 0 ? " invoke=" + Names(s.Invoke, x => x.Name) : "")
                    .Append(" out=").Append(s.TransitionsOut.Count).Append('\n');
            }

            foreach (var t in p.Transitions)
            {
                sb.Append("  transition '").Append(t.Label).Append("' ").Append(t.Source.Path).Append(" -> ").Append(t.IsTargetless ? "(targetless)" : Names(t.Targets, x => x.Path))
                    .Append(" trigger=").Append(t.Trigger).Append(t.Event is { } e ? " event=" + e.Name : "").Append(t.After is { } a ? " after=" + a + " ms=" + v(t.AfterMs) : "")
                    .Append(t.Invoke is { } iv ? " invoke=" + iv.Name : "").Append(t.Guard is { } g ? " guard=" + g.Name : "")
                    .Append(t.Actions.Count > 0 ? " actions=" + Names(t.Actions, x => x.Name) : "").Append(t.External ? " external" : "")
                    .Append(t.DisplayName != t.Label ? " display='" + t.DisplayName + "'" : "").Append(t.Gate is { } gate ? " gate=" + gate.Name : "").Append('\n');
            }

            foreach (var g in p.Gates)
            {
                sb.Append("  gate ").Append(g.Name).Append(" on '").Append(g.Transition.Label).Append("' required=").Append(g.Required)
                    .Append(" signers=").Append(Names(g.Signers, x => x.Name)).Append(" required-actors=").Append(Names(g.RequiredActors, x => x.Name))
                    .Append(" repeat=").Append(v(g.AllowRepeatSigner)).Append(" reason=").Append(v(g.ReasonRequired))
                    .Append(" meanings=").Append(Names(g.Meanings, x => x.Name + "('" + x.DisplayName + "')")).Append('\n');
                sb.Append("    audit ").Append(string.Join(' ', g.Audit.Select(f => f.Name + ":" + f.Type + (f.Required ? "!" : "") + (f.Values.Count > 0 ? "[" + string.Join('|', f.Values) + "]" : "")
                    + (f.Attribute is not null ? "(attribute)" : "")))).Append('\n');
            }

            foreach (var i in p.Invokes)
                sb.Append("  invoke ").Append(i.Name).Append(' ').Append(i.Type).Append(" state=").Append(i.State.Path).Append(" process=").Append(i.Process?.Name ?? "-")
                    .Append(" actors=").Append(Names(i.Actors, x => x.Name)).Append('\n');
            sb.Append("  actors ").Append(Names(p.Actors, x => x.Name)).Append(" membership ").Append(Keys(p.Actors.MembershipKeys)).Append('\n');
            sb.Append("  scenarios ").Append(Names(p.Scenarios, x => x.Name)).Append(" membership ").Append(Keys(p.Scenarios.MembershipKeys)).Append('\n');
        }

        foreach (var a in model.Actors)
        {
            sb.Append("actor ").Append(a.Name).Append(' ').Append(a.Type).Append(" package=").Append(a.Package?.QualifiedName ?? "-")
                .Append(" stereotypes=").Append(Names(a.Stereotypes, x => x.Key)).Append(" processes=").Append(Names(a.Processes, x => x.Name))
                .Append(" events=").Append(Names(a.Events, x => x.Name)).Append(" gates=").Append(Names(a.Gates, x => x.Name)).Append('\n');
            sb.Append("  keys ").Append(Keys(a.Dependencies)).Append(" lists ").Append(Keys(a.Processes.MembershipKeys)).Append('\n');
        }

        foreach (var sc in model.Scenarios)
        {
            sb.Append("scenario ").Append(sc.Name).Append(" process=").Append(sc.Process.Name).Append(" package=").Append(sc.Package?.QualifiedName ?? "-")
                .Append(" outcome=").Append(sc.Outcome).Append(" at=").Append(sc.Start.At).Append(" context=").Append(Map(sc.Start.Context)).Append('\n');
            sb.Append("  keys ").Append(Keys(sc.Dependencies)).Append('\n');
            foreach (var st in sc.Steps)
            {
                sb.Append("  step ").Append(st.Index).Append(' ').Append(st.Input).Append(st.Event is { } e ? " event=" + e.Name : "")
                    .Append(st.Invoke is { } i ? " invoke=" + i.Name : "").Append(st.After is { } a ? " after=" + a + " ms=" + v(st.AfterMs) : "")
                    .Append(st.Actor is { } actor ? " actor=" + actor.Name : "").Append(st.Signer is { } s ? " signer=" + s : "")
                    .Append(st.Meaning is { } m ? " meaning=" + m.Name : "").Append(st.Reason is { } r ? " reason='" + r + "'" : "")
                    .Append(st.Payload.Count > 0 ? " payload=" + Map(st.Payload) : "").Append(st.Assume.Count > 0 ? " assume=" + Map(st.Assume) : "");
                if (st.Expect is { } x)
                    sb.Append(" expect accepted=").Append(v(x.Accepted)).Append(" states=").Append(Names(x.StatePaths, y => y))
                        .Append(x.Context.Count > 0 ? " context=" + Map(x.Context) : "");
                sb.Append('\n');
            }
        }
    }

    private static string Join(RJoinPath path) => string.Join(" ; ", path.Steps.Select(s =>
        $"{s.FromTable.Name}({string.Join(',', s.FromColumns.Names())}) -> {s.ToTable.Name}({string.Join(',', s.ToColumns.Names())}){(s.ViaJunction ? " via-junction" : "")}"));

    /// <summary>Labels for every element and sub-element id: names, qualified by their owner.</summary>
    public static Dictionary<string, string> Labels(ModelSnapshot model)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in model.Documents)
        {
            var e = document.Element;
            labels[e.Id] = e.Name.Length > 0 ? e.Name.Replace(' ', '_') : e.KindName;
            switch (e)
            {
                case Entity entity:
                    foreach (var a in entity.Attributes)
                        labels[a.Id] = entity.Name + ":" + a.Name;
                    break;
                case ValueObject vo:
                    foreach (var a in vo.Attributes)
                        labels[a.Id] = vo.Name + ":" + a.Name;
                    break;
                case Stereotype st:
                    foreach (var a in st.Attributes)
                        labels[a.Id] = st.Key + ":" + a.Name;
                    break;
                case Relation relation:
                    foreach (var end in relation.Ends)
                        labels[end.Id] = "end:" + end.Role;
                    foreach (var a in relation.Attributes)
                        labels[a.Id] = relation.Name.Replace(' ', '_') + ":" + a.Name;
                    break;
                case EnumType en:
                    foreach (var m in en.Members)
                        labels[m.Id] = en.Name + ":" + m.Name;
                    break;
                case Table table:
                    foreach (var c in table.Columns)
                        labels[c.Id] = "col:" + (c.Name.Length > 0 ? c.Name : c.Attribute ?? c.Id);
                    break;
            }
        }

        return labels;
    }

    private static string Relabel(string value, Dictionary<string, string> labels) =>
        UlidPattern().Replace(value, m => labels.TryGetValue(m.Value, out var label) ? "<" + label + ">" : m.Value);

    [GeneratedRegex("[0-7][0-9A-HJKMNP-TV-Z]{25}")]
    private static partial Regex UlidPattern();
}
