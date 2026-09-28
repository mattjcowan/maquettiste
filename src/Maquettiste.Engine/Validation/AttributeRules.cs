using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>The effective type of an attribute or column, for facet (MQ3013) and literal default (MQ3019) checks.</summary>
/// <param name="Keyword">The effective built-in keyword (a scalar type's base), or <see langword="null"/>.</param>
/// <param name="Enum">The referenced enum, when the type is one.</param>
/// <param name="ValueObject">The referenced value object, when the type is one.</param>
/// <param name="Scalar">The referenced custom scalar type, when the type is one.</param>
internal sealed record EffectiveType(string? Keyword, EnumType? Enum, ValueObject? ValueObject, ScalarType? Scalar)
{
    /// <summary>A short description for messages.</summary>
    public string Describe() =>
        Enum is not null ? "enum " + Enum.Name
        : ValueObject is not null ? "value object " + ValueObject.Name
        : Scalar is not null ? "scalar type " + Scalar.Name + " (" + (Keyword ?? Scalar.Base) + ")"
        : Keyword ?? "unknown type";
}

/// <summary>
/// Rules on attribute values: facets (MQ3013), literal defaults (MQ3019, D4), credential-looking defaults (MQ3017, S19) and
/// validation rule ids (MQ2007). Pure functions of the model.
/// </summary>
internal static partial class AttributeRules
{
    /// <summary>The named default expressions a literal default is likely meant to be (D4).</summary>
    private static readonly string[] KnownExpressions = ["now", "today", "new-uuid", "new-ulid"];

    private static bool IsInteger(string k) => k is "int16" or "int32" or "int64";

    private static bool IsNumeric(string k) => IsInteger(k) || k is "decimal" or "float" or "double";

    private static bool IsTemporal(string k) => k is "date" or "time" or "datetime" or "datetimeoffset" or "duration";

    /// <summary>Resolves the effective type of a type reference.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="type">The type reference.</param>
    /// <returns>The effective type.</returns>
    public static EffectiveType Resolve(ModelSnapshot model, TypeRef type)
    {
        if (type.Ref is not { } id)
            return new EffectiveType(BuiltinTypes.IsBuiltin(type.Builtin) ? type.Builtin : null, null, null, null);
        if (model.Get<EnumType>(id) is { } e)
            return new EffectiveType(null, e, null, null);
        if (model.Get<ValueObject>(id) is { } v)
            return new EffectiveType(null, null, v, null);
        if (model.Get<ScalarType>(id) is { } s)
            return new EffectiveType(BuiltinTypes.IsBuiltin(s.Base) ? s.Base : null, null, null, s);
        return new EffectiveType(null, null, null, null);
    }

    /// <summary>Checks one attribute of an entity, value object, relation or stereotype.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="attribute">The attribute.</param>
    /// <param name="pointer">The attribute's pointer.</param>
    /// <param name="report">The report.</param>
    public static void Check(ValidationContext context, ModelAttribute attribute, string pointer, Report report)
    {
        var id = attribute.Id;
        if (attribute.Type.Ref is null && !BuiltinTypes.IsBuiltin(attribute.Type.Builtin))
        {
            report.Add("MQ3013", $"Attribute '{attribute.Name}' has the unknown type keyword '{attribute.Type.Builtin}'.", pointer + "/type", id);
            return;
        }

        var type = Resolve(context.Model, attribute.Type);
        CheckFacets(type, attribute.Length, attribute.Precision, attribute.Scale, attribute.Validation, "Attribute '" + attribute.Name + "'", pointer, id, report);

        if (attribute.Default is { } literal)
        {
            var reason = DefaultMismatch(type, attribute, literal);
            if (reason is not null)
            {
                var hint = literal.ValueKind == JsonValueKind.String && KnownExpressions.Contains(literal.GetString(), StringComparer.Ordinal)
                    ? $" '{literal.GetString()}' is a named expression: use defaultExpression instead of default."
                    : "";
                report.Add("MQ3019", $"The default of attribute '{attribute.Name}' does not fit {type.Describe()}{(attribute.Collection ? " (collection)" : "")}: {reason}.{hint}", pointer + "/default", id);
            }

            if (Credentials.LooksLikeCredential(attribute.Name, literal, attribute.Sensitive) is { } why)
                report.Add("MQ3017", $"The default of attribute '{attribute.Name}' looks like a credential ({why}); secrets never belong in the model.", pointer + "/default", id);
        }

        CheckRuleIds(context, attribute.Validation, pointer + "/validation/rules", id, report);
    }

    /// <summary>Checks that validation rule ids name registered JavaScript rules (MQ2007).</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="validation">The validation block.</param>
    /// <param name="pointer">The pointer of the <c>rules</c> array.</param>
    /// <param name="elementId">The element or sub-element id.</param>
    /// <param name="report">The report.</param>
    public static void CheckRuleIds(ValidationContext context, AttributeValidation? validation, string pointer, string elementId, Report report)
    {
        if (validation is null || context.RuleNames is not { } names)
            return;
        for (var i = 0; i < validation.Rules.Count; i++)
        {
            var rule = validation.Rules[i];
            var bare = rule.StartsWith("x/", StringComparison.Ordinal) ? rule[2..] : rule;
            if (!names.Contains(bare))
                report.Add("MQ2007", $"Validation names the rule '{rule}', which no script in extensions/rules registers.", Ptr.At(pointer, i), elementId);
        }
    }

    /// <summary>Checks size facets and validation constraints against an effective type (MQ3013).</summary>
    /// <param name="type">The effective type.</param>
    /// <param name="length">The length facet.</param>
    /// <param name="precision">The precision facet.</param>
    /// <param name="scale">The scale facet.</param>
    /// <param name="validation">The validation block.</param>
    /// <param name="subject">The subject for messages, such as <c>Attribute 'total'</c>.</param>
    /// <param name="pointer">The pointer of the object holding the facets.</param>
    /// <param name="elementId">The element or sub-element id.</param>
    /// <param name="report">The report.</param>
    public static void CheckFacets(EffectiveType type, int? length, int? precision, int? scale, AttributeValidation? validation,
        string subject, string pointer, string elementId, Report report)
    {
        var k = type.Keyword;
        var described = type.Describe();
        if (length is not null && k is not ("string" or "text" or "binary"))
            report.Add("MQ3013", $"{subject}: length is not valid for {described} (only string, text and binary).", pointer + "/length", elementId);
        if (precision is not null && k is not ("decimal" or "time" or "datetime" or "datetimeoffset"))
            report.Add("MQ3013", $"{subject}: precision is not valid for {described} (only decimal and time types).", pointer + "/precision", elementId);
        if (scale is not null && k is not "decimal")
            report.Add("MQ3013", $"{subject}: scale is not valid for {described} (only decimal).", pointer + "/scale", elementId);
        var effectivePrecision = precision ?? type.Scalar?.Precision;
        var effectiveScale = scale ?? type.Scalar?.Scale;
        if (k == "decimal" && (precision is not null || scale is not null) && effectivePrecision is { } p && effectiveScale is { } s && s > p)
        {
            report.Add("MQ3013", string.Create(CultureInfo.InvariantCulture, $"{subject}: scale {s} is greater than precision {p}."),
                pointer + (scale is not null ? "/scale" : "/precision"), elementId);
        }

        if (validation is null)
            return;
        var v = pointer + "/validation";
        if (validation.Pattern is { } pattern)
        {
            if (k is not ("string" or "text"))
                report.Add("MQ3013", $"{subject}: validation.pattern is not valid for {described} (only string and text).", v + "/pattern", elementId);
            else if (!IsValidPattern(pattern))
                report.Add("MQ3013", $"{subject}: validation.pattern is not a valid regular expression.", v + "/pattern", elementId);
        }

        CheckBound(validation.Min, "min", k, described, subject, v, elementId, report);
        CheckBound(validation.Max, "max", k, described, subject, v, elementId, report);
        if (validation.Min is { ValueKind: JsonValueKind.Number } min && validation.Max is { ValueKind: JsonValueKind.Number } max
            && min.GetDouble() > max.GetDouble())
        {
            report.Add("MQ3013", $"{subject}: validation.min is greater than validation.max.", v + "/min", elementId);
        }

        for (var i = 0; i < validation.AllowedValues.Count; i++)
        {
            if (k is not null && Fits(k, validation.AllowedValues[i], null, null, null) is { } reason)
                report.Add("MQ3013", $"{subject}: allowed value {i.ToString(CultureInfo.InvariantCulture)} does not fit {described}: {reason}.", Ptr.At(v + "/allowedValues", i), elementId);
        }
    }

    private static void CheckBound(JsonElement? bound, string name, string? k, string described, string subject, string pointer, string elementId, Report report)
    {
        if (bound is not { } value)
            return;
        var ok = k is not null && (IsNumeric(k) ? value.ValueKind == JsonValueKind.Number : IsTemporal(k) && value.ValueKind == JsonValueKind.String && Fits(k, value, null, null, null) is null);
        if (!ok)
        {
            var expected = k is not null && IsNumeric(k) ? "a number" : k is not null && IsTemporal(k) ? "an ISO 8601 " + k : "not applicable";
            report.Add("MQ3013", $"{subject}: validation.{name} is not valid for {described} ({expected}).", pointer + "/" + name, elementId);
        }
    }

    /// <summary>
    /// Whether a string matches a <c>validation.pattern</c>, deterministically and in linear time: the pattern runs with the
    /// non-backtracking engine, so a catastrophic pattern cannot stall or time out validation. <see langword="null"/> when the
    /// pattern cannot be checked that way (invalid, which MQ3013 reports, or a backreference or lookaround the non-backtracking
    /// engine does not support), and the default is then not checked against it.
    /// </summary>
    /// <param name="value">The string.</param>
    /// <param name="pattern">The pattern.</param>
    /// <returns>Whether it matches, or <see langword="null"/> when unknown.</returns>
    internal static bool? MatchesPattern(string value, string pattern)
    {
        if (!IsValidPattern(pattern))
            return null;
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(10)).IsMatch(value);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static bool IsValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Returns why a literal default does not fit an attribute, or <see langword="null"/> when it fits.</summary>
    /// <param name="type">The effective type.</param>
    /// <param name="attribute">The attribute (collection, facets, validation, required).</param>
    /// <param name="literal">The literal.</param>
    /// <returns>The reason.</returns>
    public static string? DefaultMismatch(EffectiveType type, ModelAttribute attribute, JsonElement literal)
    {
        if (literal.ValueKind == JsonValueKind.Null)
            return attribute.Required ? "null on a required attribute" : null;
        if (attribute.Collection)
        {
            if (literal.ValueKind != JsonValueKind.Array)
                return "a collection default must be an array";
            var i = 0;
            foreach (var item in literal.EnumerateArray())
            {
                if (ItemMismatch(type, attribute, item) is { } reason)
                    return "item " + i.ToString(CultureInfo.InvariantCulture) + ": " + reason;
                i++;
            }

            return null;
        }

        return ItemMismatch(type, attribute, literal);
    }

    private static string? ItemMismatch(EffectiveType type, ModelAttribute attribute, JsonElement literal)
    {
        if (type.ValueObject is not null)
            return literal.ValueKind == JsonValueKind.Object ? null : "a value object default must be an object";
        if (type.Enum is { } e)
            return EnumMismatch(e, literal);
        if (type.Keyword is not { } k)
            return null; // the type does not resolve; MQ2001 reports it
        var length = attribute.Length ?? type.Scalar?.Length;
        var precision = attribute.Precision ?? type.Scalar?.Precision;
        var scale = attribute.Scale ?? type.Scalar?.Scale;
        if (Fits(k, literal, length, precision, scale) is { } reason)
            return reason;
        foreach (var validation in (ReadOnlySpan<AttributeValidation?>)[type.Scalar?.Validation, attribute.Validation])
        {
            if (validation is null)
                continue;
            if (IsNumeric(k) && literal.ValueKind == JsonValueKind.Number)
            {
                var value = literal.GetDouble();
                if (validation.Min is { ValueKind: JsonValueKind.Number } min && value < min.GetDouble())
                    return "the number is below validation.min";
                if (validation.Max is { ValueKind: JsonValueKind.Number } max && value > max.GetDouble())
                    return "the number is above validation.max";
            }

            if (validation.AllowedValues.Count > 0 && !validation.AllowedValues.Any(a => JsonElement.DeepEquals(a, literal)))
                return "the value is not one of validation.allowedValues";
            if (validation.Pattern is { } pattern && k is "string" or "text" && literal.ValueKind == JsonValueKind.String
                && MatchesPattern(literal.GetString()!, pattern) == false)
            {
                return "the string does not match validation.pattern";
            }
        }

        return null;
    }

    private static string? EnumMismatch(EnumType e, JsonElement literal)
    {
        switch (literal.ValueKind)
        {
            case JsonValueKind.String:
                var text = literal.GetString();
                foreach (var member in e.Members)
                {
                    if (member.Name == text || member.Code == text)
                        return null;
                }

                return "not a member name or code of " + e.Name;
            case JsonValueKind.Number when literal.TryGetInt64(out var number):
                long all = 0;
                foreach (var member in e.Members)
                {
                    if (member.Value == number)
                        return null;
                    all |= member.Value ?? 0;
                }

                return e.Flags && (number & ~all) == 0 ? null : "not a member value of " + e.Name;
            default:
                return "an enum default must be a member name, code or value";
        }
    }

    /// <summary>Returns why a JSON value does not fit a built-in keyword, or <see langword="null"/> when it fits.</summary>
    /// <param name="keyword">The keyword.</param>
    /// <param name="value">The value.</param>
    /// <param name="length">The length facet, checked for strings.</param>
    /// <param name="precision">The precision facet, checked for decimals.</param>
    /// <param name="scale">The scale facet, checked for decimals.</param>
    /// <returns>The reason.</returns>
    public static string? Fits(string keyword, JsonElement value, int? length, int? precision, int? scale)
    {
        switch (keyword)
        {
            case "json":
                return null;
            case "bool":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "expected true or false";
            case "int16" or "int32" or "int64":
                if (value.ValueKind != JsonValueKind.Number)
                    return value.ValueKind == JsonValueKind.String ? "a string on an integer type" : "expected an integer";
                if (!value.TryGetInt64(out var n))
                    return value.TryGetDecimal(out var d) && decimal.Truncate(d) != d ? "expected an integer" : "the number is out of range for " + keyword;
                var (lo, hi) = keyword switch
                {
                    "int16" => ((long)short.MinValue, (long)short.MaxValue),
                    "int32" => ((long)int.MinValue, (long)int.MaxValue),
                    _ => (long.MinValue, long.MaxValue),
                };
                return n < lo || n > hi ? "the number is out of range for " + keyword : null;
            case "decimal" or "float" or "double":
                if (value.ValueKind != JsonValueKind.Number)
                    return value.ValueKind == JsonValueKind.String ? "a string on a numeric type" : "expected a number";
                if (keyword == "float" && value.TryGetDouble(out var f) && Math.Abs(f) > float.MaxValue)
                    return "the number is out of range for float";
                if (keyword == "decimal")
                {
                    if (!value.TryGetDecimal(out _))
                        return "the number is out of range for decimal";
                    var digits = DecimalDigits(value.GetRawText());
                    if (scale is { } sc && digits.Fraction > sc)
                        return string.Create(CultureInfo.InvariantCulture, $"more than {sc} decimal places");
                    if (precision is { } pr && digits.Integer > pr - (scale ?? 0))
                        return string.Create(CultureInfo.InvariantCulture, $"the number is out of range for decimal({pr},{scale ?? 0})");
                }

                return null;
            case "string" or "text":
                if (value.ValueKind != JsonValueKind.String)
                    return "expected a string";
                return length is { } max && value.GetString()!.EnumerateRunes().Count() > max
                    ? string.Create(CultureInfo.InvariantCulture, $"the string is longer than length {max}")
                    : null;
            case "uuid":
                return value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out _) ? null : "expected a UUID string (8-4-4-4-12 hex digits)";
            case "ulid":
                return value.ValueKind == JsonValueKind.String && IdFormat.IsValid(value.GetString()) ? null : "expected a ULID string (26 Crockford base32 characters)";
            case "binary":
                return value.ValueKind == JsonValueKind.String && IsBase64(value.GetString()!) ? null : "expected a base64 string";
            case "date" or "time" or "datetime" or "datetimeoffset" or "duration":
                if (value.ValueKind != JsonValueKind.String)
                    return "expected an ISO 8601 " + keyword + " string";
                return IsTemporal(keyword, value.GetString()!) ? null : "not an ISO 8601 " + keyword;
            default:
                return null;
        }
    }

    private static (int Integer, int Fraction) DecimalDigits(string raw)
    {
        var text = raw.TrimStart('-');
        var exponent = 0;
        var e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            _ = int.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var integer = (dot < 0 ? text : text[..dot]).TrimStart('0');
        var fraction = dot < 0 ? "" : text[(dot + 1)..].TrimEnd('0');
        var integerDigits = Math.Max(0, integer.Length + exponent);
        var fractionDigits = Math.Max(0, fraction.Length - exponent);
        return (integerDigits, fractionDigits);
    }

    private static bool IsBase64(string text)
    {
        var buffer = new byte[(text.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(text, buffer, out _);
    }

    private static bool IsTemporal(string keyword, string text)
    {
        var invariant = CultureInfo.InvariantCulture;
        switch (keyword)
        {
            case "date":
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", invariant, DateTimeStyles.None, out _);
            case "time":
                return TimeShape().IsMatch(text) && TimeOnly.TryParse(text, invariant, DateTimeStyles.None, out _);
            case "datetime":
                return DateTimeShape().IsMatch(text)
                    && DateTime.TryParse(text, invariant, DateTimeStyles.RoundtripKind, out _);
            case "datetimeoffset":
                return DateTimeOffsetShape().IsMatch(text)
                    && DateTimeOffset.TryParse(text, invariant, DateTimeStyles.RoundtripKind, out _);
            default:
                return DurationShape().IsMatch(text);
        }
    }

    [GeneratedRegex(@"^\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeShape();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?Z?$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeShape();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeOffsetShape();

    [GeneratedRegex(@"^-?P(?!$)(\d+Y)?(\d+M)?(\d+W)?(\d+D)?(T(?=\d)(\d+H)?(\d+M)?(\d+(\.\d+)?S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationShape();
}

/// <summary>Heuristics for defaults that look like credentials (MQ3017; SPEC section 19: secrets never belong in the model).</summary>
internal static partial class Credentials
{
    /// <summary>Returns why a default looks like a credential, or <see langword="null"/>.</summary>
    /// <param name="name">The attribute or column name.</param>
    /// <param name="value">The default literal.</param>
    /// <param name="sensitivity">The attribute's sensitivity, when known.</param>
    /// <returns>The reason.</returns>
    public static string? LooksLikeCredential(string name, JsonElement value, Sensitivity? sensitivity)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
            return null;
        if (PrivateKey().IsMatch(text))
            return "a private key block";
        if (AwsAccessKey().IsMatch(text))
            return "an AWS access key id";
        if (TokenPrefix().IsMatch(text))
            return "a token with a well-known provider prefix";
        if (Jwt().IsMatch(text))
            return "a JSON web token";
        if (ConnectionPassword().IsMatch(text))
            return "a connection string with a password";
        if (UrlCredentials().IsMatch(text))
            return "a URL with a user name and password";
        if (sensitivity == Sensitivity.Secret)
            return "a non-empty default on a secret attribute";
        if (SecretName().IsMatch(name) && !Placeholder().IsMatch(text))
            return "a non-empty default on an attribute named like a secret";
        return null;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|sk_(live|test)_[A-Za-z0-9]{16,}|sk-[A-Za-z0-9_-]{20,}|AIza[0-9A-Za-z_-]{35})", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPrefix();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(^|;)\s*(password|pwd)\s*=\s*[^;\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionPassword();

    [GeneratedRegex(@"[a-z][a-z0-9+.-]*://[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(password|passwd|pwd|secret|token|api_?key|apikey|access_?key|private_?key|credential|connection_?string|conn_?str)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"^(\*+|x+|changeme|change-me|<[^>]*>|\$\{[^}]*\}|%[^%]*%|\{\{[^}]*\}\})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();
}
