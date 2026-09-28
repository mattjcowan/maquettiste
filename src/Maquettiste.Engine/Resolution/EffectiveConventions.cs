using System.Text;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Naming and storage conventions with every value set (engine-design.md section 2.4): built-in default ← project
/// <c>conventions</c> ← <c>databases.&lt;name&gt;</c>.
/// </summary>
internal sealed record EffectiveConventions
{
    public CaseStyle TableCase { get; init; } = CaseStyle.Snake;
    public bool PluralTables { get; init; } = true;
    public CaseStyle ColumnCase { get; init; } = CaseStyle.Snake;
    public string TableName { get; init; } = "{entity}";
    public string KeyColumn { get; init; } = "{attribute}";
    public string ForeignKeyColumn { get; init; } = "{role}_{key}";
    public string JunctionTable { get; init; } = "{entity1}_{entity2}";
    public string ChildTable { get; init; } = "{entity}_{attribute}";
    public string ValueObjectColumn { get; init; } = "{attribute}_{member}";
    public string OrderColumn { get; init; } = "position";
    public string DiscriminatorColumn { get; init; } = "discriminator";
    public string PrimaryKeyName { get; init; } = "pk_{table}";
    public string ForeignKeyName { get; init; } = "fk_{table}_{columns}";
    public string UniqueName { get; init; } = "uq_{table}_{columns}";
    public string IndexName { get; init; } = "ix_{table}_{columns}";
    public string CheckName { get; init; } = "ck_{table}_{name}";
    public string SequenceName { get; init; } = "{table}_seq";
    public int DefaultStringLength { get; init; } = 255;
    public int DecimalPrecision { get; init; } = 18;
    public int DecimalScale { get; init; } = 2;
    public int DatetimePrecision { get; init; } = 6;
    public StorageKind EnumStorage { get; init; } = StorageKind.Int;
    public StorageKind ValueObjectStorage { get; init; } = StorageKind.Embedded;
    public StorageKind ValueObjectCollectionStorage { get; init; } = StorageKind.Table;
    public RelationShape RelationsWithAttributes { get; init; } = RelationShape.Junction;
    public InheritanceStrategy Inheritance { get; init; } = InheritanceStrategy.Tph;

    /// <summary>The effective conventions for a database.</summary>
    public static EffectiveConventions For(ProjectSettings settings, string? databaseName)
    {
        var effective = new EffectiveConventions().Over(settings.Conventions);
        if (databaseName is not null && settings.Databases.TryGetValue(databaseName, out var database))
            effective = effective.Over(database);
        return effective;
    }

    /// <summary>Whether any convention set in the settings (project or database) promotes relations with attributes.</summary>
    public static bool AnyPromotes(ProjectSettings settings) =>
        settings.Conventions.RelationsWithAttributes == RelationShape.Promoted
        || settings.Databases.Values.Any(c => c.RelationsWithAttributes == RelationShape.Promoted);

    private EffectiveConventions Over(Conventions? c) => c is null ? this : this with
    {
        TableCase = c.TableCase ?? TableCase,
        PluralTables = c.PluralTables ?? PluralTables,
        ColumnCase = c.ColumnCase ?? ColumnCase,
        TableName = c.TableName ?? TableName,
        KeyColumn = c.KeyColumn ?? KeyColumn,
        ForeignKeyColumn = c.ForeignKeyColumn ?? ForeignKeyColumn,
        JunctionTable = c.JunctionTable ?? JunctionTable,
        ChildTable = c.ChildTable ?? ChildTable,
        ValueObjectColumn = c.ValueObjectColumn ?? ValueObjectColumn,
        OrderColumn = c.OrderColumn ?? OrderColumn,
        DiscriminatorColumn = c.DiscriminatorColumn ?? DiscriminatorColumn,
        PrimaryKeyName = c.PrimaryKeyName ?? PrimaryKeyName,
        ForeignKeyName = c.ForeignKeyName ?? ForeignKeyName,
        UniqueName = c.UniqueName ?? UniqueName,
        IndexName = c.IndexName ?? IndexName,
        CheckName = c.CheckName ?? CheckName,
        SequenceName = c.SequenceName ?? SequenceName,
        DefaultStringLength = c.DefaultStringLength ?? DefaultStringLength,
        DecimalPrecision = c.DecimalPrecision ?? DecimalPrecision,
        DecimalScale = c.DecimalScale ?? DecimalScale,
        DatetimePrecision = c.DatetimePrecision ?? DatetimePrecision,
        EnumStorage = c.EnumStorage ?? EnumStorage,
        ValueObjectStorage = c.ValueObjectStorage ?? ValueObjectStorage,
        ValueObjectCollectionStorage = c.ValueObjectCollectionStorage ?? ValueObjectCollectionStorage,
        RelationsWithAttributes = c.RelationsWithAttributes ?? RelationsWithAttributes,
        Inheritance = c.Inheritance ?? Inheritance,
    };
}

/// <summary>
/// Renders name patterns (engine-design.md section 2.4): tokens are substituted, the result is split into words and re-cased;
/// with plural tables <c>{entity}</c> is pluralized (its last word, or the element's explicit plural name). <see cref="CaseStyle.Preserve"/> keeps the text as written.
/// </summary>
internal static class NamePattern
{
    /// <summary>Renders a pattern.</summary>
    /// <param name="pattern">The pattern, for example <c>fk_{table}_{columns}</c>.</param>
    /// <param name="tokens">Token values by token name (without braces); a token without a value renders as written.</param>
    /// <param name="style">The case style.</param>
    /// <param name="pluralize">The inflector when <c>{entity}</c> is pluralized, else <see langword="null"/>.</param>
    /// <param name="entityPlural">
    /// The element's explicit <c>pluralName</c>, which wins over the inflector when <c>{entity}</c> is pluralized (SPEC Section 5
    /// "pluralName (optional override)").
    /// </param>
    /// <returns>The name.</returns>
    public static string Render(string pattern, IReadOnlyDictionary<string, string> tokens, CaseStyle style, Inflector? pluralize = null,
        string? entityPlural = null)
    {
        var words = new List<string>();
        var raw = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var open = pattern.IndexOf('{', i);
            var close = open < 0 ? -1 : pattern.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                AddLiteral(pattern[i..], words, raw);
                break;
            }

            AddLiteral(pattern[i..open], words, raw);
            var token = pattern[(open + 1)..close];
            if (tokens.TryGetValue(token, out var value))
            {
                if (pluralize is not null && string.Equals(token, "entity", StringComparison.Ordinal) && value.Length > 0)
                {
                    // The whole value goes through the inflector (it inflects the last word), so whole-name overrides such as
                    // "SalesPerson": "SalesStaff" reach table names too.
                    value = entityPlural is { Length: > 0 } ? entityPlural : pluralize.Pluralize(value);
                }

                words.AddRange(Casing.Words(value));
                raw.Append(value);
            }
            else
            {
                AddLiteral(pattern[open..(close + 1)], words, raw);
            }

            i = close + 1;
        }

        return style == CaseStyle.Preserve ? raw.ToString() : Casing.Join(words, style);
    }

    private static void AddLiteral(string text, List<string> words, StringBuilder raw)
    {
        words.AddRange(Casing.Words(text));
        raw.Append(text);
    }
}
