using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Columns typed by an enum, a reference type or a scalar type, and the sensitivity a column states (next-phase-2-design.md section 3.2,
/// D22). On a table file: MQ4082 for a <c>typeRef</c> that names no enum, reference type or scalar type or sits on an overlay entry of a
/// projected column; MQ4083 for a typed column whose type or stated facets cannot hold its type's values; MQ4084 for a declared foreign
/// key from a column typed by a reference type that does not point at the type's bound table and its code column; MQ7014 for a literal
/// default that is not one of the type's stored values or codes, or MQ7019 when it is a former code of its reference type (section 3.5,
/// <see cref="FormerCodeProblem"/>, which table seed cells use too); MQ4085 for an overlay entry whose sensitivity differs from the
/// attribute it stores. The columns a designed or imported table inherits from its stereotypes are judged too, with findings where 3.1
/// places member findings (<see cref="MemberRules.PointerOf"/>). On an entity (<see cref="CheckEntity"/>): MQ4085 for a binding field
/// whose attribute and column state different sensitivities, and MQ7014 for a binding constant written into a typed column. Table seed
/// cells (<see cref="TableSeedRules"/>) and a reference type binding's constants (<see cref="ReferenceBindingRules"/>) use
/// <see cref="ValueProblem"/>. Pure functions of the context.
/// </summary>
internal static class ColumnTypeRules
{
    private static readonly HashSet<string> Integers = new(["int16", "int32", "int64"], StringComparer.Ordinal);
    private static readonly HashSet<string> Texts = new(["string", "text"], StringComparer.Ordinal);

    private static string Str(long i) => i.ToString(CultureInfo.InvariantCulture);

    /// <summary>Judges the typed columns, the declared foreign keys from them and the overlay entries' sensitivity of one table file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="table">The table file.</param>
    /// <param name="report">The report.</param>
    public static void CheckTable(ValidationContext context, Table table, Report report)
    {
        var model = context.Model;
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            var pointer = Ptr.At("/columns", i);
            if (column.Attribute is { } key)
            {
                if (column.TypeRef is not null)
                    report.Add("MQ4082", $"Column entry '{Label(column, key)}' overrides a projected column, which its attribute types: remove typeRef, or type the attribute.", pointer + "/typeRef", column.Id);
                CheckOverlaySensitivity(model, column, key, pointer, report);
                continue;
            }

            CheckColumn(context, column, pointer, null, column.Id, report);
        }

        // The columns the table inherits are judged on the table; an overlay's candidate set may hold members its synthesized table skips.
        if (table.Origin != TableOrigin.Synthesized)
        {
            foreach (var member in TableMembers.Of(model, table).Columns)
                CheckColumn(context, member.Member, null, MemberRules.PointerOf(table, member.MemberId, member.From), table.Id, report);
        }

        CheckForeignKeys(context, table, report);
    }

    /// <summary>
    /// MQ4082 (a <c>typeRef</c> naming neither an enum, a reference type nor a scalar type), MQ4083 and MQ7014 (its default) on one designed,
    /// imported, extra or inherited column. An own column's findings go on the key at fault under <paramref name="pointer"/>; an inherited
    /// column's all go at <paramref name="memberPointer"/>.
    /// </summary>
    private static void CheckColumn(ValidationContext context, Column column, string? pointer, string? memberPointer, string elementId, Report report)
    {
        if (column.TypeRef is not { } typeRef)
            return;
        string At(string key) => memberPointer ?? pointer + "/" + key;
        var name = column.Name;
        if (typeRef.Ref is not { } id)
        {
            report.Add("MQ4082", $"Column '{name}' has typeRef '{typeRef.Builtin}', which names no enum, reference type or scalar type: typeRef is {{ \"ref\": id }}, and type holds the built-in keyword.", At("typeRef"), elementId);
            return;
        }

        switch (context.Model.GetDocument(id)?.Element)
        {
            case ValueObject valueObject:
                report.Add("MQ4082", $"Column '{name}' has typeRef naming value object '{valueObject.Name}': a column holds the values of an enum, a reference type or a scalar type. Remove typeRef, or type the attribute.", memberPointer ?? pointer + "/typeRef/ref", elementId);
                return;
            case ReferenceType type:
                if (column.Type is not null && ReferenceBindingRules.CodeFit(type, column) is { } misfit)
                    report.Add("MQ4083", $"Column '{name}' holds codes of reference type '{type.Name}' and cannot hold them: {misfit.Problem}.", At(misfit.Length ? "length" : "type"), elementId);
                break;
            case EnumType enumType:
                if (EnumMisfit(enumType, column) is { } enumMisfit)
                    report.Add("MQ4083", $"Column '{name}' holds values of enum '{enumType.Name}' and cannot hold them: {enumMisfit.Problem}.", At(enumMisfit.Facet), elementId);
                break;
            case ScalarType scalar:
                if (ScalarMisfit(scalar, column) is { } scalarMisfit)
                    report.Add("MQ4083", $"Column '{name}' holds values of scalar type '{scalar.Name}' and cannot hold them: {scalarMisfit.Problem}.", At(scalarMisfit.Facet), elementId);
                return;
            default:
                return; // MQ2001 or MQ2002
        }

        if (column.Default is not { } literal)
            return;
        if (FormerCodeProblem(context, column, literal, null) is { } former)
            report.Add("MQ7019", $"The default of column '{name}' names a former code: {former}.", At("default"), elementId);
        else if (ValueProblem(context, column, literal, null) is { } problem)
            report.Add("MQ7014", $"The default of column '{name}' is not one of the values the column's type holds: {problem}.", At("default"), elementId);
    }

    /// <summary>
    /// Whether a column can hold the values of an enum, a reference type or a scalar type, as MQ4083 judges it (a reference type by
    /// <see cref="ReferenceBindingRules.CodeFit"/>): what a re-sync asks before an attribute keeps its type over a retyped column
    /// (next-phase-2-design.md section 3.3, decision 8). Anything else is not a type a column holds.
    /// </summary>
    /// <param name="type">The enum, reference type or scalar type.</param>
    /// <param name="column">The column entry.</param>
    /// <returns>Whether it fits.</returns>
    internal static bool Holds(Element type, Column column) => type switch
    {
        ReferenceType reference => column.Type is null || ReferenceBindingRules.CodeFit(reference, column) is null,
        EnumType enumType => EnumMisfit(enumType, column) is null,
        ScalarType scalar => ScalarMisfit(scalar, column) is null,
        _ => false,
    };

    /// <summary>Why a column cannot hold an enum's stored values, and the key at fault; <see langword="null"/> when it can.</summary>
    private static (string Problem, string Facet)? EnumMisfit(EnumType enumType, Column column)
    {
        if (column.Type is not { } type)
            return null;
        if (Integers.Contains(type))
        {
            for (var i = 0; i < enumType.Members.Count; i++)
            {
                var value = enumType.Members[i].Value ?? i;
                var outside = type switch
                {
                    "int16" => value is < short.MinValue or > short.MaxValue,
                    "int32" => value is < int.MinValue or > int.MaxValue,
                    _ => false,
                };
                if (outside)
                    return ($"member '{enumType.Members[i].Name}' has value {Str(value)}, outside the range of {type}", "type");
            }

            return null;
        }

        if (Texts.Contains(type))
        {
            var longest = enumType.Members.Select(m => m.Code ?? m.Name).OrderByDescending(c => c.Length).FirstOrDefault();
            return longest is not null && column.Length is { } length && longest.Length > length
                ? ($"its stored code '{longest}' has {Str(longest.Length)} characters, more than its length {Str(length)}", "length")
                : null;
        }

        return ($"an enum is stored as its values in an integer column or its codes in a text column, not in a {type} column", "type");
    }

    /// <summary>Why a column cannot hold a scalar type's values (its base keyword, facets no smaller), and the key at fault.</summary>
    private static (string Problem, string Facet)? ScalarMisfit(ScalarType scalar, Column column)
    {
        if (column.Type is not { } type)
            return null;
        if (!string.Equals(type, scalar.Base, StringComparison.Ordinal))
            return ($"its base is {scalar.Base}, not {type}", "type");
        if (scalar.Length is { } length && column.Length is { } own && own < length)
            return ($"its length {Str(own)} is smaller than the type's {Str(length)}", "length");
        if (scalar.Precision is { } precision && column.Precision is { } ownPrecision && ownPrecision < precision)
            return ($"its precision {Str(ownPrecision)} is smaller than the type's {Str(precision)}", "precision");
        if (scalar.Scale is { } scale && column.Scale is { } ownScale && ownScale < scale)
            return ($"its scale {Str(ownScale)} is smaller than the type's {Str(scale)}", "scale");
        return null;
    }

    /// <summary>
    /// Why a value written into a column is not one of the stored values or codes of the enum or reference type its <c>typeRef</c> names
    /// (MQ7014), or <see langword="null"/>: a column without one, a NULL, or a column of another type takes any. An enum's stored value is a
    /// member's value (else its position), or for a flags enum a combination of them, in an integer column, its code (else its name) in a
    /// text column. A reference type's value is a code of its rows; for a table seed's cell (<paramref name="seed"/>) a row of the type's
    /// seeds must hold it in every environment the seed loads in, and a number or boolean read into a text column stands for its text.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="column">The column written.</param>
    /// <param name="value">The value.</param>
    /// <param name="seed">The table seed whose cell the value is, or <see langword="null"/> for a default or a binding constant.</param>
    /// <returns>The problem, in words.</returns>
    internal static string? ValueProblem(ValidationContext context, Column column, JsonElement value, Seed? seed)
    {
        if (seed is not null)
            value = SeedValue(column, value);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || column.TypeRef?.Ref is not { } id)
            return null;
        var text = column.Type is { } type && Texts.Contains(type);
        switch (context.Model.GetDocument(id)?.Element)
        {
            case ReferenceType referenceType:
                if (ReferenceDataRules.CodeProblem(context, referenceType, value) is { } problem)
                    return $"{problem} (reference type {referenceType.Name})";
                return seed is null ? null : EnvironmentProblem(context, seed, referenceType, value);
            case EnumType enumType when column.Type is { } keyword && Integers.Contains(keyword):
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
                    return $"a value of enum {enumType.Name} in an integer column is a number";
                long all = 0;
                for (var i = 0; i < enumType.Members.Count; i++)
                {
                    var memberValue = enumType.Members[i].Value ?? i;
                    if (memberValue == number)
                        return null;
                    all |= memberValue;
                }

                // A flags enum stores a combination of its members' values.
                if (enumType.Flags && number > 0 && (number & ~all) == 0)
                    return null;
                return $"{Str(number)} is not a value of a member of enum {enumType.Name}{(enumType.Flags ? " nor a combination of them" : "")}";
            case EnumType enumType when text:
                if (value.ValueKind != JsonValueKind.String)
                    return $"a code of enum {enumType.Name} in a text column is a string";
                var code = value.GetString()!;
                return enumType.Members.Any(m => string.Equals(m.Code ?? m.Name, code, StringComparison.Ordinal))
                    ? null
                    : $"'{code}' is not the stored code of a member of enum {enumType.Name}";
            default:
                return null;
        }
    }

    /// <summary>
    /// Why a value written into a column typed by a reference type is a former code of the type rather than one of its codes (MQ7019,
    /// next-phase-2-design.md section 3.5), naming the code to write; <see langword="null"/> for any other value or column. It replaces
    /// MQ7014 for a column's default and a table seed's cell (<paramref name="seed"/>, read as <see cref="ValueProblem"/> reads it).
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="column">The column written.</param>
    /// <param name="value">The value.</param>
    /// <param name="seed">The table seed whose cell the value is, or <see langword="null"/> for a default.</param>
    /// <returns>The problem, in words.</returns>
    internal static string? FormerCodeProblem(ValidationContext context, Column column, JsonElement value, Seed? seed)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || column.TypeRef?.Ref is not { } id
            || context.Model.GetDocument(id)?.Element is not ReferenceType type)
        {
            return null;
        }

        return ReferenceDataRules.FormerCodeProblem(context, type, seed is null ? value : SeedValue(column, value));
    }

    /// <summary>A table seed's cell as a column value: a number or boolean read into a text column stands for its text.</summary>
    private static JsonElement SeedValue(Column column, JsonElement value) =>
        column.Type is { } type && Texts.Contains(type) && value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? JsonSerializer.SerializeToElement(value.GetRawText())
            : value;

    /// <summary>Where a table seed's row is loaded and no seed of the reference type holds the code, in words, or <see langword="null"/>.</summary>
    private static string? EnvironmentProblem(ValidationContext context, Seed seed, ReferenceType type, JsonElement value)
    {
        var key = ReferenceDataIndex.CodeKey(value);
        var holders = new List<Seed>();
        if (context.SeedsOf(type.Id).Any(s => !context.Judges(s)))
            return null; // a scripted seed of the type is outside this save's scope (next-phase-2-design.md section 3.4, decision 17)
        foreach (var typeSeed in context.SeedsOf(type.Id))
        {
            var codeColumn = -1;
            for (var k = 0; k < typeSeed.Columns.Count; k++)
            {
                if (string.Equals(typeSeed.Columns[k], "code", StringComparison.Ordinal))
                    codeColumn = k;
            }

            if (codeColumn >= 0 && context.RowsOf(typeSeed).Any(r => codeColumn < r.Values.Count && string.Equals(ReferenceDataIndex.CodeKey(r.Values[codeColumn]), key, StringComparison.Ordinal)))
                holders.Add(typeSeed);
        }

        return TableSeedRules.MissingIn(seed, holders) is { } missing
            ? $"'{key}' is a code of reference type {type.Name} that its seeds do not hold {missing}, where this row is loaded"
            : null;
    }

    /// <summary>
    /// MQ4084: a declared foreign key that includes a column typed by a reference type points at the table the type's binding names in the
    /// table's database and pairs the column with the binding's code column. A type without a binding there has no table file of rows to
    /// point at, so the key is refused. The table's own keys are judged, and the inherited ones that name a table.
    /// </summary>
    private static void CheckForeignKeys(ValidationContext context, Table table, Report report)
    {
        var model = context.Model;
        var withMembers = MemberRules.WithMembers(model, table);
        for (var i = 0; i < table.ForeignKeys.Count; i++)
        {
            var fk = table.ForeignKeys[i];
            CheckForeignKey(context, table, withMembers, fk.Name ?? fk.Id, fk.Columns, fk.ReferencesTable, fk.ReferencesColumns, Ptr.At("/foreignKeys", i), fk.Id, report);
        }

        if (table.Origin == TableOrigin.Synthesized)
            return;
        foreach (var member in TableMembers.Of(model, table).ForeignKeys)
        {
            if (member.Member.ReferencesTable is { } referencesTable)
            {
                CheckForeignKey(context, table, withMembers, member.Member.Id, member.Member.Columns, referencesTable, member.Member.ReferencesColumns,
                    MemberRules.PointerOf(table, member.MemberId, member.From), table.Id, report);
            }
        }
    }

    private static void CheckForeignKey(ValidationContext context, Table table, Table withMembers, string name, IReadOnlyList<string> columns,
        string referencesTable, IReadOnlyList<string> referencesColumns, string pointer, string elementId, Report report)
    {
        var model = context.Model;
        for (var k = 0; k < columns.Count; k++)
        {
            if (withMembers.Columns.FirstOrDefault(c => string.Equals(c.Id, columns[k], StringComparison.Ordinal)) is not { TypeRef.Ref: { } typeId } column
                || model.Get<ReferenceType>(typeId) is not { } type)
                continue;
            var database = model.Get<Database>(table.Database)?.Name ?? table.Database;
            if (type.Bindings.FirstOrDefault(b => string.Equals(b.Database, table.Database, StringComparison.Ordinal)) is not { } binding)
            {
                report.Add("MQ4084", $"Foreign key '{name}' includes column '{column.Name}', which holds codes of reference type '{type.Name}', and the type has no binding in database '{database}': no table file holds its rows there. Remove the key; packs render the column's own link to the type's rows.", pointer, elementId);
                return;
            }

            var bound = model.Get<Table>(binding.Table) is { } boundFile ? MemberRules.WithMembers(model, boundFile) : null;
            var boundName = bound?.Name ?? binding.Table;
            if (!string.Equals(referencesTable, binding.Table, StringComparison.Ordinal))
            {
                var target = model.Get<Table>(referencesTable)?.Name ?? referencesTable;
                report.Add("MQ4084", $"Foreign key '{name}' includes column '{column.Name}', which holds codes of reference type '{type.Name}', but points at table '{target}': the type's rows live in table '{boundName}' (its binding in database '{database}'). Point the key at '{boundName}', or remove it.", pointer, elementId);
                return;
            }

            if (bound is null || binding.Fields.FirstOrDefault(f => f.Attribute == "code") is not { } codeField
                || ReferenceBindingRules.ColumnOf(bound, codeField.Column) is not { } codeColumn)
                continue; // MQ2001, MQ7117 or MQ7118 on the type
            var referenced = referencesColumns.Count > 0 ? referencesColumns : bound.PrimaryKey?.Columns ?? [];
            if (k >= referenced.Count || string.Equals(referenced[k], codeColumn.Id, StringComparison.Ordinal))
                continue; // a count mismatch is MQ4008's
            var paired = ReferenceBindingRules.ColumnOf(bound, referenced[k])?.Name ?? referenced[k];
            report.Add("MQ4084", $"Foreign key '{name}' pairs column '{column.Name}', which holds codes of reference type '{type.Name}', with column '{paired}' of table '{boundName}', not with the type's code column '{codeColumn.Name}'.", pointer, elementId);
            return;
        }
    }

    /// <summary>MQ4085 on an overlay entry: its own sensitivity differs from the own sensitivity of the attribute its projected column stores.</summary>
    private static void CheckOverlaySensitivity(ModelSnapshot model, Column entry, string key, string pointer, Report report)
    {
        if (entry.Sensitive is not { } own || StoredAttribute(model, key) is not { Sensitive: { } theirs } attribute || theirs == own)
            return;
        report.Add("MQ4085", $"Column entry '{Label(entry, key)}' states sensitive '{Text(own)}', but attribute '{attribute.Name}', which its column stores, states '{Text(theirs)}': state it on one side, and the other shows it.", pointer + "/sensitive", entry.Id);
    }

    /// <summary>
    /// The attribute a projected column of a column key stores: the last segment of a key whose first segment is an attribute (an attribute,
    /// or a member of an embedded value object); <see langword="null"/> for a foreign key column (an end's), <c>id</c>, <c>position</c> and
    /// the other columns that store none.
    /// </summary>
    private static ModelAttribute? StoredAttribute(ModelSnapshot model, string key)
    {
        var segments = key.Split('.');
        if (!model.TryGetEntry(segments[0], out var first) || first.Kind != "attribute"
            || !model.TryGetEntry(segments[^1], out var last) || last.Kind != "attribute" || model.GetDocument(last.OwnerId)?.Element is not { } owner)
            return null;
        foreach (var attribute in ReferenceDataIndex.AttributesOf(owner))
        {
            if (string.Equals(attribute.Id, segments[^1], StringComparison.Ordinal))
                return attribute;
        }

        return null;
    }

    /// <summary>
    /// The rules on an entity's bindings to table files: MQ4085 for a field whose attribute (or value object member) and the column it maps
    /// state different sensitivities, at <c>/bindings/&lt;i&gt;/fields/&lt;j&gt;</c>, and MQ7014 for a constant written into a column typed
    /// by an enum or a reference type, at <c>/bindings/&lt;i&gt;/constants/&lt;j&gt;</c>. The column is looked up in the source table and
    /// in the write table, by id, keyed id or name.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="report">The report.</param>
    public static void CheckEntity(ValidationContext context, Entity entity, Report report)
    {
        for (var i = 0; i < entity.Bindings.Count; i++)
        {
            var binding = entity.Bindings[i];
            var tables = BindingTables(context.Model, binding);
            if (tables.Count == 0)
                continue;
            var pointer = Ptr.At("/bindings", i);
            for (var j = 0; j < binding.Fields.Count; j++)
            {
                var field = binding.Fields[j];
                if (FieldAttribute(context, entity, field.Attribute) is not { Sensitive: { } own } attribute)
                    continue;
                foreach (var column in Columns(tables, field.Column))
                {
                    if (column.Sensitive is not { } theirs || theirs == own)
                        continue;
                    report.Add("MQ4085", $"Attribute '{attribute.Name}' of entity '{entity.Name}' states sensitive '{Text(own)}', but column '{column.Name}', which the binding maps it to, states '{Text(theirs)}': state it on one side, and the other shows it.", Ptr.At(pointer + "/fields", j), binding.Id);
                    break;
                }
            }

            for (var j = 0; j < binding.Constants.Count; j++)
            {
                var constant = binding.Constants[j];
                if (constant.Value is not { } value)
                    continue;
                foreach (var column in Columns(tables, constant.Column))
                {
                    if (ValueProblem(context, column, value, null) is not { } problem)
                        continue;
                    report.Add("MQ7014", $"The constant the binding of entity '{entity.Name}' writes into column '{column.Name}' is not one of the values the column's type holds: {problem}.", Ptr.At(pointer + "/constants", j), binding.Id);
                    break;
                }
            }
        }
    }

    /// <summary>The designed or imported table files a binding reads and writes, with their inherited members, source first.</summary>
    private static List<Table> BindingTables(ModelSnapshot model, EntityBinding binding)
    {
        var tables = new List<Table>(2);
        foreach (var key in new[] { binding.Source, binding.Write?.Table })
        {
            if (key is not null && model.Get<Table>(key) is { Origin: not TableOrigin.Synthesized } file && !tables.Any(t => t.Id == file.Id))
                tables.Add(MemberRules.WithMembers(model, file));
        }

        return tables;
    }

    /// <summary>The column a binding names in each of its tables (by id, keyed id or name; in a write table also by the source column's name).</summary>
    private static IEnumerable<Column> Columns(List<Table> tables, string reference)
    {
        var name = reference;
        foreach (var table in tables)
        {
            if ((ReferenceBindingRules.ColumnOf(table, reference) ?? ReferenceBindingRules.ColumnOf(table, name)) is { } column)
            {
                name = column.Name;
                yield return column;
            }
        }
    }

    /// <summary>The attribute (own, inherited or virtual) or value object member a binding field names; <see langword="null"/> for an end.</summary>
    private static ModelAttribute? FieldAttribute(ValidationContext context, Entity entity, string reference)
    {
        var flat = context.Flatten(entity);
        foreach (var f in flat)
        {
            if (string.Equals(f.Attribute.Id, reference, StringComparison.Ordinal))
                return f.Attribute;
        }

        var dot = reference.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
            return null;
        var ownerId = reference[..dot];
        var memberId = reference[(dot + 1)..];
        var owner = flat.FirstOrDefault(f => string.Equals(f.Attribute.Id, ownerId, StringComparison.Ordinal))?.Attribute;
        return owner is null ? null
            : AttributeRules.Resolve(context.Model, owner.Type).ValueObject?.Attributes.FirstOrDefault(m => string.Equals(m.Id, memberId, StringComparison.Ordinal));
    }

    private static string Label(Column column, string key) => column.Name.Length > 0 ? column.Name : key;

    private static string Text(Sensitivity sensitivity) => Resolution.ResolutionValues.Kebab(sensitivity);
}
