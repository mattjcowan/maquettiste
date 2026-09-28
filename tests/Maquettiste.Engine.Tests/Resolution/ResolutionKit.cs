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
                .Append(" limit=").Append(V(db.MaxIdentifierLength)).Append(" schemas=").Append(string.Join(',', db.Schemas.Select(s => s.Name))).Append('\n');
            foreach (var t in db.Tables)
            {
                sb.Append("  table ").Append(t.Schema is null ? "" : t.Schema + ".").Append(t.Name).Append(" key=").Append(L(t.Key)).Append(' ').Append(t.Origin)
                    .Append(t.Entity is { } te ? " entity=" + te.Name : "").Append(t.Relation is { } tr ? " relation=" + tr.Name : "")
                    .Append(t.IsJunction ? " junction" : "").Append(t.IsLookup ? " lookup" : "").Append('\n');
                foreach (var c in t.Columns)
                {
                    sb.Append("    ").Append(c.Position).Append(' ').Append(c.Name).Append(' ').Append(c.Type)
                        .Append(c.Length is { } l ? "(" + l + ")" : "").Append(c.Precision is { } pr ? $"[{pr},{V(c.Scale)}]" : "")
                        .Append(" native=").Append(c.NativeType).Append(c.Nullable ? " null" : " not-null")
                        .Append(c.IsPrimaryKey ? " pk" : "").Append(c.IsForeignKey ? " fk" : "").Append(c.IsDiscriminator ? " discriminator" : "")
                        .Append(c.Identity ? " identity" : "").Append(c.Sequence is { } s ? " seq=" + s.Name : "")
                        .Append(c.Default is { } d ? " default=" + V(d) : "").Append(" key=").Append(L(c.Key)).Append('\n');
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
                foreach (var row in t.LookupRows)
                    sb.Append("    row ").Append(row.Id).Append(' ').Append(row.Code).Append(' ').Append(row.Name).Append('\n');
            }

            foreach (var s in db.Sequences)
                sb.Append("  sequence ").Append(s.Name).Append(' ').Append(s.Type).Append(" native=").Append(s.NativeType).Append(" key=").Append(L(s.Id)).Append('\n');
            foreach (var v in db.Views)
                sb.Append("  view ").Append(v.Name).Append(": ").Append(v.Body).Append('\n');
        }

        foreach (var d in model.Diagnostics)
            sb.Append("diagnostic ").Append(d.Rule).Append(' ').Append(d.Severity).Append(' ').Append(L(d.Message)).Append('\n');
        return sb.ToString();
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
