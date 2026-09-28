using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Maquettiste.Engine.Json;

/// <summary>The outcome of a <see cref="SchemaPrecheck"/> check: definitely valid, definitely invalid, or not decided.</summary>
internal enum PrecheckVerdict : byte
{
    /// <summary>The instance is valid under every reading of the schema: the full evaluator would report no failure.</summary>
    Valid,

    /// <summary>The instance is invalid under every reading of the schema.</summary>
    Invalid,

    /// <summary>The precheck cannot decide; the full evaluator must.</summary>
    Unknown,
}

/// <summary>
/// A fast, allocation-light JSON Schema 2020-12 checker for the subset of keywords the embedded schemas use, which only answers
/// "valid" when the full evaluator (JsonSchema.Net) would also find the document valid. <see cref="SchemaRegistry.Evaluate"/>
/// runs it first and returns no diagnostics when it says <see cref="PrecheckVerdict.Valid"/>; any other verdict runs the full
/// evaluator, whose output (the MQ1002 messages and pointers) is therefore unchanged.
/// </summary>
/// <remarks>
/// Soundness rules, so a verdict never depends on details where evaluators could differ: a keyword outside the supported set makes
/// its schema undecidable (<see cref="PrecheckVerdict.Unknown"/>); <c>pattern</c> is decided only for printable ASCII strings
/// (where every regular expression dialect agrees on the embedded patterns); string lengths are decided only when UTF-16 units,
/// code points and text elements all agree on the comparison; numbers are compared only as 64-bit integer literals; an object that
/// repeats a schema-declared property name is undecidable. <see cref="PrecheckVerdict.Invalid"/> is only used where it is certain
/// too, because <c>oneOf</c>, <c>not</c> and <c>if</c> turn it into validity. Thread-safe after construction; no static state.
/// </remarks>
internal sealed class SchemaPrecheck
{
    private readonly Dictionary<string, Node> _roots;

    private SchemaPrecheck(Dictionary<string, Node> roots) => _roots = roots;

    /// <summary>Compiles the schema documents (file name → root element), resolving <c>$ref</c> between them.</summary>
    /// <param name="documents">The schema documents.</param>
    /// <returns>The precheck.</returns>
    public static SchemaPrecheck Build(IReadOnlyDictionary<string, JsonElement> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var compiler = new Compiler(documents);
        var roots = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var file in documents.Keys)
            roots[file] = compiler.Compile(file, "");
        compiler.ResolveReferences();
        return new SchemaPrecheck(roots);
    }

    /// <summary>Checks a document against a schema file.</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="instance">The document.</param>
    /// <returns>The verdict; <see cref="PrecheckVerdict.Unknown"/> for an unknown schema file.</returns>
    public PrecheckVerdict Check(string fileName, JsonElement instance) =>
        _roots.TryGetValue(fileName, out var root) ? Evaluate(root, instance) : PrecheckVerdict.Unknown;

    private static PrecheckVerdict Combine(PrecheckVerdict current, PrecheckVerdict next) =>
        current == PrecheckVerdict.Invalid || next == PrecheckVerdict.Invalid ? PrecheckVerdict.Invalid
        : current == PrecheckVerdict.Unknown || next == PrecheckVerdict.Unknown ? PrecheckVerdict.Unknown
        : PrecheckVerdict.Valid;

    private static PrecheckVerdict Evaluate(Node node, JsonElement instance)
    {
        if (node.Constant is { } constant)
            return constant ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid;
        if (node.Unsupported)
            return PrecheckVerdict.Unknown;

        // Cheap, decisive keywords first: an Invalid here settles the node.
        var verdict = PrecheckVerdict.Valid;
        if (node.Types != TypeMask.None)
        {
            verdict = CheckType(node.Types, instance);
            if (verdict == PrecheckVerdict.Invalid)
                return verdict;
        }

        if (node.Enum is { } values)
        {
            verdict = Combine(verdict, CheckEnum(values, instance));
            if (verdict == PrecheckVerdict.Invalid)
                return verdict;
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                verdict = Combine(verdict, CheckObject(node, instance));
                break;
            case JsonValueKind.Array:
                verdict = Combine(verdict, CheckArray(node, instance));
                break;
            case JsonValueKind.String:
                verdict = Combine(verdict, CheckString(node, instance));
                break;
            case JsonValueKind.Number:
                verdict = Combine(verdict, CheckNumber(node, instance));
                break;
        }

        if (verdict == PrecheckVerdict.Invalid)
            return verdict;

        if (node.Ref is { } target)
        {
            verdict = Combine(verdict, Evaluate(target, instance));
            if (verdict == PrecheckVerdict.Invalid)
                return verdict;
        }

        if (node.AllOf is { } allOf)
        {
            foreach (var branch in allOf)
            {
                verdict = Combine(verdict, Evaluate(branch, instance));
                if (verdict == PrecheckVerdict.Invalid)
                    return verdict;
            }
        }

        if (node.AnyOf is { } anyOf)
        {
            var any = PrecheckVerdict.Invalid;
            foreach (var branch in anyOf)
            {
                var result = Evaluate(branch, instance);
                if (result == PrecheckVerdict.Valid)
                {
                    any = PrecheckVerdict.Valid;
                    break;
                }

                if (result == PrecheckVerdict.Unknown)
                    any = PrecheckVerdict.Unknown;
            }

            verdict = Combine(verdict, any);
            if (verdict == PrecheckVerdict.Invalid)
                return verdict;
        }

        if (node.OneOf is { } oneOf)
        {
            int valid = 0, unknown = 0;
            foreach (var branch in oneOf)
            {
                switch (Evaluate(branch, instance))
                {
                    case PrecheckVerdict.Valid: valid++; break;
                    case PrecheckVerdict.Unknown: unknown++; break;
                }
            }

            var one = unknown > 0 ? (valid > 1 ? PrecheckVerdict.Invalid : PrecheckVerdict.Unknown)
                : valid == 1 ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid;
            verdict = Combine(verdict, one);
            if (verdict == PrecheckVerdict.Invalid)
                return verdict;
        }

        if (node.Not is { } not)
        {
            var inner = Evaluate(not, instance);
            verdict = Combine(verdict, inner switch
            {
                PrecheckVerdict.Valid => PrecheckVerdict.Invalid,
                PrecheckVerdict.Invalid => PrecheckVerdict.Valid,
                _ => PrecheckVerdict.Unknown,
            });
            if (verdict == PrecheckVerdict.Invalid)
                return verdict;
        }

        if (node.If is { } condition)
        {
            var branch = Evaluate(condition, instance) switch
            {
                PrecheckVerdict.Valid => node.Then is { } then ? Evaluate(then, instance) : PrecheckVerdict.Valid,
                PrecheckVerdict.Invalid => node.Else is { } otherwise ? Evaluate(otherwise, instance) : PrecheckVerdict.Valid,
                _ => PrecheckVerdict.Unknown,
            };
            verdict = Combine(verdict, branch);
        }

        return verdict;
    }

    private static PrecheckVerdict CheckType(TypeMask types, JsonElement instance)
    {
        switch (instance.ValueKind)
        {
            case JsonValueKind.Object: return Has(types, TypeMask.Object);
            case JsonValueKind.Array: return Has(types, TypeMask.Array);
            case JsonValueKind.String: return Has(types, TypeMask.String);
            case JsonValueKind.True or JsonValueKind.False: return Has(types, TypeMask.Boolean);
            case JsonValueKind.Null: return Has(types, TypeMask.Null);
            case JsonValueKind.Number:
                if ((types & TypeMask.Number) != 0)
                    return PrecheckVerdict.Valid;
                if ((types & TypeMask.Integer) == 0)
                    return PrecheckVerdict.Invalid;
                // Only a plain integer literal is certainly an integer; 1.0 or 1e3 are left to the full evaluator.
                return IsIntegerLiteral(instance) ? PrecheckVerdict.Valid : PrecheckVerdict.Unknown;
            default:
                return PrecheckVerdict.Unknown;
        }

        static PrecheckVerdict Has(TypeMask types, TypeMask type) => (types & type) != 0 ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid;
    }

    private static bool IsIntegerLiteral(JsonElement number)
    {
        if (!number.TryGetInt64(out _))
            return false;
        foreach (var b in JsonMarshal.GetRawUtf8Value(number))
        {
            if (b is (byte)'.' or (byte)'e' or (byte)'E')
                return false;
        }

        return true;
    }

    private static PrecheckVerdict CheckEnum(JsonElement[] values, JsonElement instance)
    {
        var result = PrecheckVerdict.Invalid;
        foreach (var value in values)
        {
            switch (Same(value, instance))
            {
                case PrecheckVerdict.Valid: return PrecheckVerdict.Valid;
                case PrecheckVerdict.Unknown: result = PrecheckVerdict.Unknown; break;
            }
        }

        return result;
    }

    /// <summary>JSON equality: Valid when certainly equal, Invalid when certainly different, Unknown otherwise (numbers that are not both integer literals).</summary>
    private static PrecheckVerdict Same(JsonElement a, JsonElement b)
    {
        var kindA = a.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValueKind.True : a.ValueKind;
        var kindB = b.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValueKind.True : b.ValueKind;
        if (kindA != kindB)
            return PrecheckVerdict.Invalid;
        switch (a.ValueKind)
        {
            case JsonValueKind.Null:
                return PrecheckVerdict.Valid;
            case JsonValueKind.True or JsonValueKind.False:
                return a.ValueKind == b.ValueKind ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid;
            case JsonValueKind.String:
                return StringsEqual(a, b) ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid;
            case JsonValueKind.Number:
                return IsIntegerLiteral(a) && IsIntegerLiteral(b)
                    ? (a.GetInt64() == b.GetInt64() ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid)
                    : PrecheckVerdict.Unknown;
            case JsonValueKind.Array:
            {
                if (a.GetArrayLength() != b.GetArrayLength())
                    return PrecheckVerdict.Invalid;
                var result = PrecheckVerdict.Valid;
                using var left = a.EnumerateArray();
                using var right = b.EnumerateArray();
                while (left.MoveNext() && right.MoveNext())
                {
                    var item = Same(left.Current, right.Current);
                    if (item == PrecheckVerdict.Invalid)
                        return item;
                    if (item == PrecheckVerdict.Unknown)
                        result = item;
                }

                return result;
            }

            default:
                return PrecheckVerdict.Unknown; // objects: not needed by the embedded schemas' enum and const values
        }
    }

    private static bool StringsEqual(JsonElement a, JsonElement b)
    {
        var rawA = JsonMarshal.GetRawUtf8Value(a);
        var rawB = JsonMarshal.GetRawUtf8Value(b);
        if (rawA.IndexOf((byte)'\\') < 0 && rawB.IndexOf((byte)'\\') < 0)
            return rawA.SequenceEqual(rawB);
        return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
    }

    private static PrecheckVerdict CheckObject(Node node, JsonElement instance)
    {
        var verdict = PrecheckVerdict.Valid;
        if (node.Required is { } required)
        {
            foreach (var name in required)
            {
                if (!instance.TryGetProperty((ReadOnlySpan<byte>)name, out _))
                    return PrecheckVerdict.Invalid;
            }
        }

        if (node.DependentRequired is { } dependent)
        {
            foreach (var (name, names) in dependent)
            {
                if (!instance.TryGetProperty(name, out _))
                    continue;
                foreach (var other in names)
                {
                    if (!instance.TryGetProperty(other, out _))
                        return PrecheckVerdict.Invalid;
                }
            }
        }

        var properties = node.Properties;
        var needWalk = properties is not null || node.Additional is not null || node.PropertyNames is not null
            || node.MinProperties is not null || node.MaxProperties is not null;
        if (!needWalk)
            return verdict;

        var declared = properties?.Length ?? 0;
        Span<bool> seen = declared <= 64 ? stackalloc bool[declared] : new bool[declared];
        var count = 0;
        var additionalFailed = false;
        foreach (var property in instance.EnumerateObject())
        {
            count++;
            var index = -1;
            for (var i = 0; i < declared; i++)
            {
                if (property.NameEquals(properties![i].Utf8Name))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                if (seen[index])
                    return PrecheckVerdict.Unknown; // a repeated property name: evaluators may differ
                seen[index] = true;
                verdict = Combine(verdict, Evaluate(properties![index].Schema, property.Value));
            }
            else if (node.Additional is { } additional)
            {
                var result = Evaluate(additional, property.Value);
                if (result == PrecheckVerdict.Invalid)
                    additionalFailed = true;
                else
                    verdict = Combine(verdict, result);
            }

            if (node.PropertyNames is { } names)
                verdict = Combine(verdict, CheckName(names, property));
            if (verdict == PrecheckVerdict.Invalid)
                return verdict;
        }

        if (additionalFailed || node.MinProperties is not null || node.MaxProperties is not null)
        {
            // Exact only when no property name repeats (the loader's parser refuses repeats; other callers may not).
            if (HasRepeatedNames(instance))
                return PrecheckVerdict.Unknown;
            if (additionalFailed)
                return PrecheckVerdict.Invalid;
            if (count < node.MinProperties || count > node.MaxProperties)
                return PrecheckVerdict.Invalid;
        }

        return verdict;
    }

    private static bool HasRepeatedNames(JsonElement instance)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in instance.EnumerateObject())
        {
            if (!names.Add(property.Name))
                return true;
        }

        return false;
    }

    /// <summary>Checks a property name against a <c>propertyNames</c> schema; only string keywords are decided.</summary>
    private static PrecheckVerdict CheckName(Node node, JsonProperty property)
    {
        if (node.Constant is { } constant)
            return constant ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid;
        if (!node.NameCheckable)
            return PrecheckVerdict.Unknown;
        var name = property.Name;
        var verdict = node.Types == TypeMask.None || (node.Types & TypeMask.String) != 0 ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid;
        if (node.Enum is { } values)
        {
            var found = PrecheckVerdict.Invalid;
            foreach (var value in values)
            {
                if (value.ValueKind == JsonValueKind.String && value.ValueEquals(name))
                {
                    found = PrecheckVerdict.Valid;
                    break;
                }
            }

            verdict = Combine(verdict, found);
        }

        verdict = Combine(verdict, CheckText(node, name));
        if (node.Ref is { } target)
            verdict = Combine(verdict, CheckName(target, property));
        return verdict;
    }

    private static PrecheckVerdict CheckArray(Node node, JsonElement instance)
    {
        var verdict = PrecheckVerdict.Valid;
        var length = instance.GetArrayLength();
        if (length < node.MinItems || length > node.MaxItems)
            return PrecheckVerdict.Invalid;

        if (node.PrefixItems is not null || node.Items is not null)
        {
            var index = 0;
            foreach (var item in instance.EnumerateArray())
            {
                var schema = node.PrefixItems is { } prefix && index < prefix.Length ? prefix[index] : node.Items;
                index++;
                if (schema is null)
                    continue;
                verdict = Combine(verdict, Evaluate(schema, item));
                if (verdict == PrecheckVerdict.Invalid)
                    return verdict;
            }
        }

        if (node.UniqueItems && length > 1)
            verdict = Combine(verdict, CheckUnique(instance));
        return verdict;
    }

    private static PrecheckVerdict CheckUnique(JsonElement instance)
    {
        var length = instance.GetArrayLength();
        var result = PrecheckVerdict.Valid;
        for (var i = 0; i < length; i++)
        {
            for (var j = i + 1; j < length; j++)
            {
                switch (Same(instance[i], instance[j]))
                {
                    case PrecheckVerdict.Valid: return PrecheckVerdict.Invalid; // two equal items
                    case PrecheckVerdict.Unknown: result = PrecheckVerdict.Unknown; break;
                }
            }
        }

        return result;
    }

    private static PrecheckVerdict CheckString(Node node, JsonElement instance)
    {
        if (node.MinLength is null && node.MaxLength is null && node.Pattern is null)
            return PrecheckVerdict.Valid;
        var raw = JsonMarshal.GetRawUtf8Value(instance);
        raw = raw.Length >= 2 && raw[0] == (byte)'"' && raw[^1] == (byte)'"' ? raw[1..^1] : raw; // the raw value keeps its quotes
        if (raw.IndexOf((byte)'\\') < 0 && System.Text.Ascii.IsValid(raw))
        {
            // Plain ASCII without escapes: the byte count is the length under every definition, and patterns can be matched on the
            // ASCII text directly.
            var length = raw.Length;
            if (length < node.MinLength || length > node.MaxLength)
                return PrecheckVerdict.Invalid;
            if (node.Pattern is null)
                return PrecheckVerdict.Valid;
            if (!IsPrintableAscii(raw))
                return PrecheckVerdict.Unknown;
            Span<char> chars = length <= 256 ? stackalloc char[length] : new char[length];
            System.Text.Ascii.ToUtf16(raw, chars, out _);
            return node.Pattern.IsMatch(chars) ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid;
        }

        return CheckText(node, instance.GetString()!);
    }

    /// <summary>Length and pattern of a string of any content.</summary>
    private static PrecheckVerdict CheckText(Node node, string text)
    {
        var verdict = PrecheckVerdict.Valid;
        if (node.MinLength is not null || node.MaxLength is not null)
        {
            // Evaluators count UTF-16 units, code points or text elements; decide only when all three agree.
            var units = text.Length;
            var points = CodePoints(text);
            var elements = units == points && IsAscii(text) ? units : new StringInfo(text).LengthInTextElements;
            var fits = 0;
            foreach (var length in (ReadOnlySpan<int>)[units, points, elements])
            {
                if (!(length < node.MinLength) && !(length > node.MaxLength))
                    fits++;
            }

            verdict = fits == 3 ? PrecheckVerdict.Valid : fits == 0 ? PrecheckVerdict.Invalid : PrecheckVerdict.Unknown;
            if (verdict == PrecheckVerdict.Invalid)
                return verdict;
        }

        if (node.Pattern is { } pattern)
        {
            if (!IsPrintableAscii(text))
                return PrecheckVerdict.Unknown;
            verdict = Combine(verdict, pattern.IsMatch(text) ? PrecheckVerdict.Valid : PrecheckVerdict.Invalid);
        }

        return verdict;
    }

    private static int CodePoints(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            count++;
        }

        return count;
    }

    private static bool IsAscii(string text) => System.Text.Ascii.IsValid(text);

    private static bool IsPrintableAscii(ReadOnlySpan<byte> raw)
    {
        foreach (var b in raw)
        {
            if (b is < 0x20 or > 0x7E)
                return false;
        }

        return true;
    }

    private static bool IsPrintableAscii(string text)
    {
        foreach (var c in text)
        {
            if (c is < ' ' or > '~')
                return false;
        }

        return true;
    }

    private static PrecheckVerdict CheckNumber(Node node, JsonElement instance)
    {
        if (node.Minimum is null && node.ExclusiveMinimum is null && node.Maximum is null && node.ExclusiveMaximum is null)
            return PrecheckVerdict.Valid;
        if (!IsIntegerLiteral(instance))
            return PrecheckVerdict.Unknown;
        var value = instance.GetInt64();
        if (value < node.Minimum || value <= node.ExclusiveMinimum || value > node.Maximum || value >= node.ExclusiveMaximum)
            return PrecheckVerdict.Invalid;
        return PrecheckVerdict.Valid;
    }

    [Flags]
    private enum TypeMask : byte
    {
        None = 0,
        Object = 1,
        Array = 2,
        String = 4,
        Boolean = 8,
        Null = 16,
        Number = 32,
        Integer = 64,
    }

    private sealed record NamedSchema(string Name, byte[] Utf8Name, Node Schema);

    private sealed class Node
    {
        public bool? Constant;
        public bool Unsupported;
        public bool NameCheckable;
        public TypeMask Types;
        public JsonElement[]? Enum;
        public Node? Ref;
        public string? RefFile;
        public string? RefPointer;
        public int? MinLength;
        public int? MaxLength;
        public Regex? Pattern;
        public long? Minimum;
        public long? ExclusiveMinimum;
        public long? Maximum;
        public long? ExclusiveMaximum;
        public int? MinItems;
        public int? MaxItems;
        public bool UniqueItems;
        public Node? Items;
        public Node[]? PrefixItems;
        public int? MinProperties;
        public int? MaxProperties;
        public byte[][]? Required;
        public (string Name, string[] Names)[]? DependentRequired;
        public NamedSchema[]? Properties;
        public Node? Additional;
        public Node? PropertyNames;
        public Node[]? AllOf;
        public Node[]? AnyOf;
        public Node[]? OneOf;
        public Node? Not;
        public Node? If;
        public Node? Then;
        public Node? Else;
    }

    private sealed class Compiler(IReadOnlyDictionary<string, JsonElement> documents)
    {
        /// <summary>Keywords that never affect validity (annotations and containers).</summary>
        private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
        {
            "$schema", "$comment", "$defs", "definitions", "title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly",
        };

        /// <summary>Keywords a property-name schema can be decided on.</summary>
        private static readonly HashSet<string> NameKeywords = new(StringComparer.Ordinal)
        {
            "type", "enum", "const", "minLength", "maxLength", "pattern", "$ref",
        };

        private readonly Dictionary<string, Node> _byPointer = new(StringComparer.Ordinal);
        private readonly List<Node> _references = [];

        public Node Compile(string file, string pointer)
        {
            var key = file + "#" + pointer;
            if (_byPointer.TryGetValue(key, out var existing))
                return existing;
            var node = new Node();
            _byPointer[key] = node; // registered first, so recursive references terminate
            if (!documents.TryGetValue(file, out var root) || !TryResolve(root, pointer, out var element))
            {
                node.Unsupported = true;
                return node;
            }

            Fill(node, file, pointer, element);
            return node;
        }

        public void ResolveReferences()
        {
            // References compile their targets lazily; resolve until no new ones appear.
            for (var i = 0; i < _references.Count; i++)
            {
                var node = _references[i];
                node.Ref = Compile(node.RefFile!, node.RefPointer!);
            }

            foreach (var node in _byPointer.Values)
                node.NameCheckable = !node.Unsupported && IsNameCheckable(node, []);
        }

        private bool IsNameCheckable(Node node, HashSet<Node> visiting)
        {
            if (node.Constant is not null)
                return true;
            if (node.Unsupported || !visiting.Add(node))
                return false;
            var simple = node.Items is null && node.PrefixItems is null && node.Properties is null && node.Additional is null
                && node.PropertyNames is null && node.AllOf is null && node.AnyOf is null && node.OneOf is null && node.Not is null
                && node.If is null && node.Required is null && node.DependentRequired is null && node.MinItems is null
                && node.MaxItems is null && !node.UniqueItems && node.MinProperties is null && node.MaxProperties is null
                && node.Minimum is null && node.ExclusiveMinimum is null && node.Maximum is null && node.ExclusiveMaximum is null
                && (node.Enum is null || node.Enum.All(e => e.ValueKind == JsonValueKind.String));
            return simple && (node.Ref is null || IsNameCheckable(node.Ref, visiting));
        }

        private void Fill(Node node, string file, string pointer, JsonElement schema)
        {
            if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                node.Constant = schema.ValueKind == JsonValueKind.True;
                return;
            }

            if (schema.ValueKind != JsonValueKind.Object)
            {
                node.Unsupported = true;
                return;
            }

            foreach (var keyword in schema.EnumerateObject())
            {
                var value = keyword.Value;
                var at = pointer + "/" + JsonPointer.Escape(keyword.Name);
                var ok = keyword.Name switch
                {
                    "type" => TryTypes(value, out node.Types),
                    "enum" => node.Enum is null && TryValues(value, out node.Enum), // enum with const: left to the full evaluator
                    "const" => node.Enum is null && TrySet(out node.Enum, [value.Clone()]),
                    "$ref" => TryReference(node, file, value),
                    "minLength" => TryInt(value, out node.MinLength),
                    "maxLength" => TryInt(value, out node.MaxLength),
                    "pattern" => TryPattern(value, out node.Pattern),
                    "minimum" => TryLong(value, out node.Minimum),
                    "exclusiveMinimum" => TryLong(value, out node.ExclusiveMinimum),
                    "maximum" => TryLong(value, out node.Maximum),
                    "exclusiveMaximum" => TryLong(value, out node.ExclusiveMaximum),
                    "minItems" => TryInt(value, out node.MinItems),
                    "maxItems" => TryInt(value, out node.MaxItems),
                    "uniqueItems" => TryBool(value, out node.UniqueItems),
                    "items" => TrySet(out node.Items, Compile(file, at)),
                    "prefixItems" => TryList(file, at, value, out node.PrefixItems),
                    "minProperties" => TryInt(value, out node.MinProperties),
                    "maxProperties" => TryInt(value, out node.MaxProperties),
                    "required" => TryUtf8Strings(value, out node.Required),
                    "dependentRequired" => TryDependentRequired(value, out node.DependentRequired),
                    "properties" => TryProperties(file, at, value, out node.Properties),
                    "additionalProperties" => TrySet(out node.Additional, Compile(file, at)),
                    "propertyNames" => TrySet(out node.PropertyNames, Compile(file, at)),
                    "allOf" => TryList(file, at, value, out node.AllOf),
                    "anyOf" => TryList(file, at, value, out node.AnyOf),
                    "oneOf" => TryList(file, at, value, out node.OneOf),
                    "not" => TrySet(out node.Not, Compile(file, at)),
                    "if" => TrySet(out node.If, Compile(file, at)),
                    "then" => TrySet(out node.Then, Compile(file, at)),
                    "else" => TrySet(out node.Else, Compile(file, at)),
                    var name => Annotations.Contains(name) || name.StartsWith("x-", StringComparison.Ordinal),
                };
                if (!ok)
                    node.Unsupported = true;
            }

            if (node.Items is not null && schema.TryGetProperty("items", out var items) && items.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
                node.Unsupported = true; // the pre-2020-12 array form
        }

        private bool TryReference(Node node, string file, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String)
                return false;
            var reference = value.GetString()!;
            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            var target = hash < 0 ? reference : reference[..hash];
            var pointer = hash < 0 ? "" : reference[(hash + 1)..];
            if (target.Contains('/', StringComparison.Ordinal) || target.Contains(':', StringComparison.Ordinal) || (pointer.Length > 0 && pointer[0] != '/'))
                return false; // only same-folder files and JSON pointers, as the embedded schemas use
            node.RefFile = target.Length == 0 ? file : target;
            node.RefPointer = pointer;
            _references.Add(node);
            return true;
        }

        private List<Node> CompileAll(string file, string at, JsonElement array)
        {
            var list = new List<Node>();
            var i = 0;
            foreach (var _ in array.EnumerateArray())
                list.Add(Compile(file, at + "/" + (i++).ToString(CultureInfo.InvariantCulture)));
            return list;
        }

        private bool TryList(string file, string at, JsonElement value, out Node[]? nodes)
        {
            nodes = null;
            if (value.ValueKind != JsonValueKind.Array)
                return false;
            nodes = [.. CompileAll(file, at, value)];
            return true;
        }

        private bool TryProperties(string file, string at, JsonElement value, out NamedSchema[]? properties)
        {
            properties = null;
            if (value.ValueKind != JsonValueKind.Object)
                return false;
            var list = new List<NamedSchema>();
            foreach (var property in value.EnumerateObject())
                list.Add(new NamedSchema(property.Name, Encoding.UTF8.GetBytes(property.Name), Compile(file, at + "/" + JsonPointer.Escape(property.Name))));
            properties = [.. list];
            return true;
        }

        private static bool TrySet<T>(out T target, T value)
        {
            target = value;
            return true;
        }

        private static bool TryTypes(JsonElement value, out TypeMask types)
        {
            types = TypeMask.None;
            if (value.ValueKind == JsonValueKind.String)
                return TryType(value.GetString()!, ref types);
            if (value.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !TryType(item.GetString()!, ref types))
                    return false;
            }

            return types != TypeMask.None;
        }

        private static bool TryType(string name, ref TypeMask types)
        {
            TypeMask type = name switch
            {
                "object" => TypeMask.Object,
                "array" => TypeMask.Array,
                "string" => TypeMask.String,
                "boolean" => TypeMask.Boolean,
                "null" => TypeMask.Null,
                "number" => TypeMask.Number,
                "integer" => TypeMask.Integer,
                _ => TypeMask.None,
            };
            types |= type;
            return type != TypeMask.None;
        }

        private static bool TryValues(JsonElement value, out JsonElement[]? values)
        {
            values = null;
            if (value.ValueKind != JsonValueKind.Array)
                return false;
            values = [.. value.EnumerateArray().Select(e => e.Clone())];
            return true;
        }

        private static bool TryStrings(JsonElement value, out string[]? strings)
        {
            strings = null;
            if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                return false;
            strings = [.. value.EnumerateArray().Select(e => e.GetString()!)];
            return true;
        }

        private static bool TryUtf8Strings(JsonElement value, out byte[][]? strings)
        {
            strings = null;
            if (!TryStrings(value, out var names))
                return false;
            strings = [.. names!.Select(Encoding.UTF8.GetBytes)];
            return true;
        }

        private static bool TryDependentRequired(JsonElement value, out (string, string[])[]? dependent)
        {
            dependent = null;
            if (value.ValueKind != JsonValueKind.Object)
                return false;
            var list = new List<(string, string[])>();
            foreach (var property in value.EnumerateObject())
            {
                if (!TryStrings(property.Value, out var names))
                    return false;
                list.Add((property.Name, names!));
            }

            dependent = [.. list];
            return true;
        }

        private static bool TryInt(JsonElement value, out int? result)
        {
            result = null;
            if (value.ValueKind != JsonValueKind.Number || !IsIntegerLiteral(value) || !value.TryGetInt32(out var i) || i < 0)
                return false;
            result = i;
            return true;
        }

        private static bool TryLong(JsonElement value, out long? result)
        {
            result = null;
            if (value.ValueKind != JsonValueKind.Number || !IsIntegerLiteral(value))
                return false;
            result = value.GetInt64();
            return true;
        }

        private static bool TryBool(JsonElement value, out bool result)
        {
            result = value.ValueKind == JsonValueKind.True;
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }

        private static bool TryPattern(JsonElement value, out Regex? pattern)
        {
            pattern = null;
            if (value.ValueKind != JsonValueKind.String)
                return false;
            try
            {
                // Matched only against printable ASCII text, where .NET and ECMA-262 semantics agree for the embedded patterns.
                pattern = new Regex(value.GetString()!, RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryResolve(JsonElement root, string pointer, out JsonElement element)
        {
            element = root;
            if (pointer.Length == 0)
                return true;
            if (pointer[0] != '/')
                return false;
            foreach (var raw in pointer[1..].Split('/'))
            {
                var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var child))
                    element = child;
                else if (element.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && index < element.GetArrayLength())
                    element = element[index];
                else
                    return false;
            }

            return true;
        }
    }
}
