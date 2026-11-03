using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Rendering;

/// <summary>
/// The <c>type_of &lt;attr|column|type|keyword&gt; "&lt;target&gt;"</c> helper (engine-design.md sections 8 and 9).
/// <para>A <b>pack target</b> is a <c>types/&lt;target&gt;.json</c> type map of the unit's pack (keyword, enum, value-object or
/// scalar-type name → language type, plus the optional <c>"nullable"</c> and <c>"collection"</c> patterns that wrap <c>{type}</c>).
/// Built-in keywords must be in the map; an enum, value object or scalar type without an entry maps to its name (a scalar type
/// falls back to its base keyword's entry). A collection attribute takes the <c>collection</c> pattern; otherwise an attribute
/// that is not required, or a nullable column, takes the <c>nullable</c> pattern. Reading a pack map records
/// <c>t:&lt;pack&gt;/types/&lt;target&gt;.json</c>.</para>
/// <para>An <b>operation member</b> (next-phase-2-design.md section 4.1) maps as an attribute does. A <c>keyOf</c> member maps through
/// its entity's single key attribute, with that attribute's facets; a composite key, or none, is MQ6006. A <c>file</c> member takes the
/// map's <c>file</c> entry, MQ6006 when the map has none or the target is a SQL dialect.</para>
/// <para>A <b>dialect target</b> (<c>postgresql</c>, <c>sqlserver</c>, <c>mysql</c>, <c>sqlite</c>, <c>oracle</c>, with the aliases
/// of <see cref="SqlDialects.TryParse"/>) needs no file: a column of a database in that dialect yields its resolved native type;
/// anything else goes through the dialect's type map (<c>typeMaps.&lt;dialect&gt;</c> overrides applied, recording
/// <c>s:typeMaps</c>) with the value's facets, missing facets taking the conventions' defaults (<c>s:conventions</c>); a value of a
/// custom type that declares a native type for the dialect takes that one instead. Enums map as <c>int32</c> and value objects as
/// <c>json</c>. Pack maps win over dialect names.</para>
/// </summary>
internal static class TypeOf
{
    /// <summary>Maps a value to a type name for a target.</summary>
    /// <param name="context">The unit's context.</param>
    /// <param name="value">An attribute, operation member, column, view column, type, element or keyword.</param>
    /// <param name="target">The target name.</param>
    /// <returns>The type name.</returns>
    public static string Map(TrackingTemplateContext context, object? value, string target)
    {
        if (target.Length == 0)
            throw new RenderHelperException("MQ6006", "`type_of` needs a target (a pack type map name or a SQL dialect).");
        var pack = context.Unit.Planned.Pack;
        if (pack.TypeMaps.TryGetValue(target, out var map))
        {
            context.Recorder.Record("t:" + pack.Name + "/types/" + target + ".json");
            return Language(context, value, target, map);
        }

        if (SqlDialects.TryParse(target, out var dialect))
            return DialectType(context, value, dialect);
        throw new RenderHelperException("MQ6006", $"`type_of`: unknown target '{target}': the pack has no types/{target}.json and it is not a SQL dialect.");
    }

    private static string Language(TrackingTemplateContext context, object? value, string target, IReadOnlyDictionary<string, string> map)
    {
        switch (value)
        {
            case RAttribute attribute:
                {
                    context.Recorder.RecordObject(attribute);
                    var type = ForType(context, attribute.Type, target, map);
                    if (attribute.Collection)
                        return Wrap(map, "collection", type);
                    return attribute.Required ? type : Wrap(map, "nullable", type);
                }

            case ROperationMember member:
                {
                    context.Recorder.RecordObject(member);
                    var type = member.IsFile
                        ? map.TryGetValue("file", out var file) ? file
                            : throw new RenderHelperException("MQ6006", $"`type_of`: member '{member.Name}' is a file, and types/{target}.json has no 'file' entry.")
                        : member.KeyOf is { } keyed
                        ? ForType(context, KeyAttribute(context, member, keyed).Type, target, map)
                        : ForType(context, MemberType(member), target, map);
                    if (member.Collection)
                        return Wrap(map, "collection", type);
                    return member.Required ? type : Wrap(map, "nullable", type);
                }

            case RColumn column:
                {
                    context.Recorder.RecordObject(column);
                    var type = column.Type == "reference" && column.ReferenceType is { } reference
                        ? Reference(map, target, reference, column.Strategy, true)
                        : Keyword(column.Type, target, map);
                    return column.Nullable ? Wrap(map, "nullable", type) : type;
                }

            case RViewColumn viewColumn:
                {
                    var type = Keyword(viewColumn.Type ?? "string", target, map);
                    return viewColumn.Nullable ? Wrap(map, "nullable", type) : type;
                }

            case RType type:
                return ForType(context, type, target, map);
            case REnum or RValueObject or RScalarType:
                {
                    var element = (RElement)value;
                    context.Recorder.RecordObject(element);
                    if (map.TryGetValue(element.Name, out var named))
                        return named;
                    return value is RScalarType scalar ? Keyword(scalar.Base, target, map) : element.Name;
                }

            case string keyword:
                return Keyword(keyword, target, map);
            default:
                throw new RenderHelperException("MQ6006", $"`type_of` takes an attribute, operation member, column, type or keyword, not a {TemplateValues.TypeName(value)}.");
        }
    }

    private static string ForType(TrackingTemplateContext context, RType type, string target, IReadOnlyDictionary<string, string> map)
    {
        switch (type.Kind)
        {
            case "enum" when type.Enum is { } e:
                context.Recorder.RecordObject(e);
                return map.TryGetValue(e.Name, out var enumType) ? enumType : e.Name;
            case "value-object" when type.ValueObject is { } vo:
                context.Recorder.RecordObject(vo);
                return map.TryGetValue(vo.Name, out var voType) ? voType : vo.Name;
            case "reference" when type.ReferenceType is { } reference:
                context.Recorder.RecordObject(reference);
                return Reference(map, target, reference, null, false);
            case "scalar" when type.Scalar is { } scalar:
                context.Recorder.RecordObject(scalar);
                return map.TryGetValue(scalar.Name, out var scalarType) ? scalarType : Keyword(type.Builtin ?? scalar.Base, target, map);
            default:
                return Keyword(type.Builtin ?? type.Name, target, map);
        }
    }

    /// <summary>
    /// A reference-typed value (reference-types-seeds-localization.md section 1.4): the map's <c>reference:&lt;strategy&gt;</c> entry
    /// (a column in its database), else <c>reference:*</c>, else the code's logical type. An entry may use <c>{type}</c> (the code's
    /// mapped type, when the map has one) and <c>{name}</c> (the reference type's name).
    /// </summary>
    private static string Reference(IReadOnlyDictionary<string, string> map, string target, RReferenceType reference, string? strategy, bool column)
    {
        var codeType = reference.Code.Type;
        string? entry = null;
        if (column && strategy is not null)
            map.TryGetValue("reference:" + strategy, out entry);
        if (entry is null)
            map.TryGetValue("reference:*", out entry);
        if (entry is null)
            return Keyword(codeType, target, map);
        var mapped = map.TryGetValue(codeType, out var code) ? code : codeType;
        return entry.Replace("{type}", mapped, StringComparison.Ordinal).Replace("{name}", reference.Name, StringComparison.Ordinal);
    }

    private static string Keyword(string keyword, string target, IReadOnlyDictionary<string, string> map) =>
        map.TryGetValue(keyword, out var mapped)
            ? mapped
            : throw new RenderHelperException("MQ6006", $"`type_of`: types/{target}.json has no entry for '{keyword}'.");

    private static string Wrap(IReadOnlyDictionary<string, string> map, string pattern, string type) =>
        map.TryGetValue(pattern, out var p) ? p.Replace("{type}", type, StringComparison.Ordinal) : type;

    private static string DialectType(TrackingTemplateContext context, object? value, Dialect dialect)
    {
        var run = context.Unit.Run;
        string keyword;
        int? length = null, precision = null, scale = null;
        string? databaseName = null;
        RScalarType? custom = null;
        switch (value)
        {
            case RColumn column:
                context.Recorder.RecordObject(column);
                if (column.Table?.Database is { } db && SqlDialects.TryParse(db.Dialect, out var own) && own == dialect && column.NativeType.Length > 0)
                    return column.NativeType;
                keyword = column.Type == "reference" && column.CodeType is { } codeType ? codeType : column.Type;
                (length, precision, scale) = (column.Length, column.Precision, column.Scale);
                databaseName = column.Table?.Database?.Name;
                break;
            case RAttribute attribute:
                context.Recorder.RecordObject(attribute);
                keyword = KeywordOf(attribute.Type);
                (length, precision, scale) = (attribute.Length, attribute.Precision, attribute.Scale);
                custom = attribute.Type.Scalar;
                break;
            case ROperationMember { IsFile: true } file:
                context.Recorder.RecordObject(file);
                throw new RenderHelperException("MQ6006", $"`type_of`: member '{file.Name}' is a file, which has no {DialectTypeMaps.Name(dialect)} type; map it with a pack type map's 'file' entry.");
            case ROperationMember member when member.KeyOf is { } keyed:
                {
                    context.Recorder.RecordObject(member);
                    var key = KeyAttribute(context, member, keyed);
                    keyword = KeywordOf(key.Type);
                    (length, precision, scale) = (key.Length, key.Precision, key.Scale);
                    custom = key.Type.Scalar;
                    break;
                }

            case ROperationMember member:
                context.Recorder.RecordObject(member);
                keyword = KeywordOf(MemberType(member));
                (length, precision, scale) = (member.Length, member.Precision, member.Scale);
                custom = member.Type?.Scalar;
                break;
            case RViewColumn viewColumn:
                keyword = viewColumn.Type ?? "string";
                break;
            case RType type:
                keyword = KeywordOf(type);
                if (type.Scalar is { } typeScalar)
                {
                    context.Recorder.RecordObject(typeScalar);
                    (length, precision, scale) = (typeScalar.Length, typeScalar.Precision, typeScalar.Scale);
                    custom = typeScalar;
                }

                break;
            case RScalarType scalar:
                context.Recorder.RecordObject(scalar);
                keyword = scalar.Base;
                (length, precision, scale) = (scalar.Length, scalar.Precision, scalar.Scale);
                custom = scalar;
                break;
            case string text:
                keyword = text;
                break;
            default:
                throw new RenderHelperException("MQ6006", $"`type_of` takes an attribute, operation member, column, type or keyword, not a {TemplateValues.TypeName(value)}.");
        }

        context.Recorder.Record("s:typeMaps");
        context.Recorder.Record("s:conventions");
        var map = DialectTypeMaps.Effective(dialect, run.Context.Model.Settings);
        var conventions = EffectiveConventions.For(run.Context.Model.Settings, databaseName);
        if (custom is not null && custom.NativeTypes.TryGetValue(DialectTypeMaps.Name(dialect), out var pattern))
            return DialectTypeMaps.RenderPattern(pattern, keyword, length, precision, scale, conventions);
        if (!map.ContainsKey(keyword))
            throw new RenderHelperException("MQ6006", $"`type_of`: the {DialectTypeMaps.Name(dialect)} type map has no entry for '{keyword}'.");
        return DialectTypeMaps.Render(map, keyword, length, precision, scale, conventions);
    }

    /// <summary>
    /// The key attribute a <c>keyOf</c> member holds the value of: its entity's single key attribute. A composite key, or none, is
    /// MQ6006, since one member holds one value; a template maps <c>member.key_of.key.attributes</c> one by one instead.
    /// </summary>
    private static RAttribute KeyAttribute(TrackingTemplateContext context, ROperationMember member, REntity entity)
    {
        context.Recorder.RecordObject(entity);
        if (entity.Key is not { Attributes.Count: 1 } key)
        {
            var count = entity.Key?.Attributes.Count ?? 0;
            throw new RenderHelperException("MQ6006",
                $"`type_of`: member '{member.Name}' holds a key of '{entity.Name}', whose key has {count} attributes, not one: map member.key_of.key.attributes one by one.");
        }

        var attribute = key.Attributes[0];
        context.Recorder.RecordObject(attribute);
        return attribute;
    }

    /// <summary>The type of a typed member; a member without one did not resolve its type and is MQ6006.</summary>
    private static RType MemberType(ROperationMember member) =>
        member.Type ?? throw new RenderHelperException("MQ6006", $"`type_of`: member '{member.Name}' has no type.");

    private static string KeywordOf(RType type) => type.Kind switch
    {
        "enum" => "int32",
        "value-object" => "json",
        _ => type.Builtin ?? type.Name,
    };
}
