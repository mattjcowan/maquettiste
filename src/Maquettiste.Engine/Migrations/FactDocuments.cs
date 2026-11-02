using System.Globalization;
using System.Text;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Migrations;

/// <summary>
/// The files of the facts a write records (next-phase-1-design.md section 3.2): where a new fact goes, its canonical bytes and the
/// one-line summary its answer carries. A recorded fact has no name, so it is placed as the change planner places a create: in its
/// database's <c>migrations/open</c> folder (<see cref="ModelPaths.ConventionalFolder"/>) as <c>&lt;lowercase id&gt;.json</c>
/// (<see cref="ModelPaths.FileName"/>; ids never collide). An updated fact keeps its file.
/// </summary>
internal static class FactDocuments
{
    /// <summary>The schema file that lays a fact out.</summary>
    public const string SchemaFile = "migration.json";

    /// <summary>The model-relative path of a new fact.</summary>
    /// <param name="fact">The fact.</param>
    /// <param name="databaseFolder">Returns the model-relative folder of a database id, or <see langword="null"/> when unknown.</param>
    /// <returns>The path, such as <c>model/databases/main/migrations/open/01jbf3r5q7w0x2y4z6a8b0c2d4.json</c>.</returns>
    public static string PathOf(Migration fact, Func<string, string?> databaseFolder)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return ModelPaths.Join(ModelPaths.ConventionalFolder(fact, databaseFolder), ModelPaths.FileName(fact, suffixed: false));
    }

    /// <summary>A fact's canonical bytes.</summary>
    /// <param name="json">The canonical writer.</param>
    /// <param name="fact">The fact.</param>
    /// <param name="modelPath">Its model-relative path (the <c>$schema</c> reference is computed from it).</param>
    /// <returns>The bytes.</returns>
    public static byte[] Bytes(ICanonicalJson json, Migration fact, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(fact);
        return json.Serialize(fact, SchemaFile, modelPath);
    }

    /// <summary>
    /// A one-line summary of a fact, by the names it records: <c>rename column billing.notes.text to body</c>,
    /// <c>change-type column billing.invoices.number text to varchar(20)</c> (a reference column whose storage changed adds it, as in
    /// <c>varchar(10) (check) to varchar(10) (bound table)</c>), <c>drop table billing.notes</c>,
    /// <c>rekey 1 table, 7 columns</c>. A value map names its type, codes and holders (next-phase-2-design.md section 3.5):
    /// <c>maps PaymentTerm codes net30 -&gt; net-30 in 1 column</c>, a merge's <c>maps CreditTerm codes n90 -&gt; net60 into PaymentTerm
    /// in 2 columns</c>, and a type rename <c>renames reference type PaymentTerm to PaymentCondition</c>.
    /// </summary>
    /// <param name="fact">The fact.</param>
    /// <returns>The summary.</returns>
    public static string Summary(Migration fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.Fact == MigrationFact.ValueMap)
            return ValueMapSummary(fact);
        var sb = new StringBuilder(ResolutionValues.Kebab(fact.Fact));
        if (fact.Fact == MigrationFact.Rekey)
        {
            var parts = fact.Keys.GroupBy(k => k.Kind, StringComparer.Ordinal).OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + g.Key + (g.Count() == 1 ? "" : g.Key == "index" ? "es" : "s"));
            return sb.Append(' ').Append(string.Join(", ", parts)).ToString();
        }

        if (fact.Object is { } o)
            sb.Append(' ').Append(o.Kind).Append(' ').Append(Name(o));
        else if (fact.Objects.Count > 0)
            sb.Append(' ').Append(string.Join(", ", fact.Objects.Select(x => x.Kind + " " + Name(x))));
        switch (fact.Fact)
        {
            case MigrationFact.Rename when fact.To is { } to:
                if (to.Schema is { } schema && !string.Equals(schema, fact.From?.Schema, StringComparison.Ordinal))
                    sb.Append(" to ").Append(schema).Append('.').Append(to.Name);
                else
                    sb.Append(" to ").Append(to.Name);
                break;
            case MigrationFact.ChangeType when fact is { From: { } from, To: { } target }:
                var reference = !Widening.SameReference(from, target);
                sb.Append(' ').Append(Type(from, reference)).Append(" to ").Append(Type(target, reference));
                break;
        }

        return sb.ToString();
    }

    /// <summary>A value map's summary: its type's codes and the columns and collections that hold them, or the type's rename.</summary>
    private static string ValueMapSummary(Migration fact)
    {
        var to = fact.To?.Name ?? fact.From?.Name ?? fact.ReferenceType ?? "";
        var from = fact.From?.Name ?? to;
        var renamed = !string.Equals(from, to, StringComparison.Ordinal);
        if (fact.Values.Count == 0 && renamed)
            return "renames reference type " + from + " to " + to;
        var sb = new StringBuilder("maps ").Append(from).Append(" codes ");
        sb.Append(fact.Values.Count == 0 ? "(none)" : string.Join(", ", fact.Values.Select(v => v.From + " -> " + v.To)));
        if (renamed)
            sb.Append(" into ").Append(to);
        sb.Append(" in ").Append(Count(fact.Objects.Count, "column"));
        if (fact.Collections.Count > 0)
            sb.Append(" and ").Append(Count(fact.Collections.Count, "collection"));
        return sb.ToString();

        static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
    }

    private static int Rank(string kind) => kind switch { "table" => 0, "column" => 1, "sequence" => 2, _ => 3 };

    /// <summary>An object's qualified name at the time: schema, table (for a member) and name, else its key.</summary>
    private static string Name(MigrationObject o)
    {
        var parts = new List<string>(3);
        if (o.Schema is { Length: > 0 } schema)
            parts.Add(schema);
        if (o.TableName is { Length: > 0 } table && FactKeys.MemberKinds.Contains(o.Kind))
            parts.Add(table);
        parts.Add(o.Name ?? o.Key);
        return string.Join('.', parts);
    }

    /// <summary>A side's type; when a reference column's storage changed, followed by how it stores its codes.</summary>
    private static string Type(MigrationState state, bool reference)
    {
        var storage = !reference ? "" : " (" + (state.ReferenceTable is not null ? "bound table" : state.Strategy ?? "template-defined") + ")";
        if (state.NativeType is { Length: > 0 } native)
            return native + storage;
        var facets = new[] { state.Length, state.Precision, state.Scale }.Where(f => f.HasValue).Select(f => f!.Value.ToString(CultureInfo.InvariantCulture)).ToList();
        return (state.Type ?? "?") + (facets.Count > 0 ? "(" + string.Join(", ", facets) + ")" : "") + storage;
    }
}
