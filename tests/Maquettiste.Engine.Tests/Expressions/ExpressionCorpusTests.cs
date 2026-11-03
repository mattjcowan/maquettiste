using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>
/// The differential corpus (next-phase-3-design.md section 3.1, task H5): every case of <c>tests/fixtures/expressions/corpus.json</c> is
/// typed, lowered to PostgreSQL and to JavaScript, and run over its rows in each mode; both lowerings must give the corpus's answers.
/// PostgreSQL runs in the container <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c> names, in a session whose time zone is not UTC.
/// </summary>
public sealed partial class ExpressionCorpusTests
{
    /// <summary>A session time zone away from UTC, with a change of daylight saving time inside the corpus's dates.</summary>
    private const string SessionTimeZone = "Europe/Paris";

    [Fact]
    public void JavaScript_gives_every_answer_of_the_corpus()
    {
        var cases = Corpus.Load();
        var functions = cases.SelectMany(c => c.Modes.Select(m => KeyValuePair.Create(c.Key(m), c.JavaScript[m])));
        using var evaluator = ExprEvaluator.Create(functions, ct: TestContext.Current.CancellationToken);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            for (var r = 0; r < c.Rows.Count; r++)
            {
                using var row = JsonDocument.Parse(c.JsonRow(r));
                foreach (var mode in c.Modes)
                {
                    var answer = evaluator.Evaluate(c.Key(mode), row.RootElement, TestContext.Current.CancellationToken);
                    Expect(failures, c, r, mode, answer as bool?, answer is null or bool);
                }
            }
        }

        Assert.True(failures.Count == 0, "JavaScript disagrees with the corpus:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task PostgreSql_gives_every_answer_of_the_corpus()
    {
        var container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_POSTGRES_CONTAINER");
        if (string.IsNullOrEmpty(container))
            Assert.Skip("Name a PostgreSQL container in MAQUETTISTE_TEST_POSTGRES_CONTAINER to run the corpus on it.");
        var cases = Corpus.Load();
        var script = new StringBuilder("SET client_encoding = 'UTF8';\nSET TIME ZONE '" + SessionTimeZone + "';\n");
        foreach (var c in cases)
            c.WriteSql(script);

        var database = "mq_corpus_" + Guid.NewGuid().ToString("N")[..8];
        await Psql(container, "postgres", "CREATE DATABASE " + database + ";", stopOnError: true);
        string output;
        try
        {
            output = await Psql(container, database, script.ToString(), stopOnError: false);
        }
        finally
        {
            await Psql(container, "postgres", "DROP DATABASE IF EXISTS " + database + ";", stopOnError: true);
        }

        // Only a check refusing a row may fail; any other error is a lowering PostgreSQL does not accept.
        var errors = output.Split('\n').Where(l => l.Contains("ERROR:", StringComparison.Ordinal)
            && !l.Contains("violates check constraint", StringComparison.Ordinal)).ToList();
        Assert.True(errors.Count == 0, "PostgreSQL refused the lowered SQL:\n" + string.Join("\n", errors) + "\n--- output ---\n" + output);

        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var parts = line.Split('|');
            if (parts.Length == 4 && parts[0] is "v" or "g")
                answers[parts[0] + "|" + parts[1] + "|" + parts[2]] = parts[3];
            else if (parts.Length == 3 && parts[0] == "k")
                accepted.Add(parts[1] + "|" + parts[2]);
        }

        var failures = new List<string>();
        foreach (var c in cases)
        {
            for (var r = 0; r < c.Rows.Count; r++)
            {
                var row = (r + 1).ToString(CultureInfo.InvariantCulture);
                foreach (var mode in c.Modes)
                {
                    if (mode == ExprSiteKind.Check)
                    {
                        Expect(failures, c, r, mode, accepted.Contains(c.Index + "|" + row), true);
                        continue;
                    }

                    var found = answers.TryGetValue((mode == ExprSiteKind.Value ? "v" : "g") + "|" + c.Index + "|" + row, out var text);
                    bool? answer = text switch { "true" => true, "false" => false, _ => null };
                    Expect(failures, c, r, mode, answer, found && text is "true" or "false" or "null");
                }
            }
        }

        Assert.True(failures.Count == 0, "PostgreSQL disagrees with the corpus:\n" + string.Join("\n", failures) + "\n--- output ---\n" + output);
    }

    [Fact]
    public void The_corpus_reaches_every_operator_and_function()
    {
        var cases = Corpus.Load();
        var ops = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
            Walk(c.Tree, ops, calls);
        Assert.Empty(ExprFunctions.Operators.Keys.Except(ops).Order(StringComparer.Ordinal));
        Assert.Empty(ExprFunctions.Functions.Keys.Except(calls).Order(StringComparer.Ordinal));
        Assert.Contains(cases, c => c.Text.Contains("now()", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Text.Contains("satisfies", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Text.Contains("case when", StringComparison.Ordinal));
    }

    private static void Walk(Expr e, HashSet<string> ops, HashSet<string> calls)
    {
        if (e.Op is { } op)
            ops.Add(op);
        if (e.Call is { } call)
            calls.Add(call);
        foreach (var arg in e.Args ?? [])
            Walk(arg, ops, calls);
        foreach (var when in e.Case ?? [])
        {
            Walk(when.When, ops, calls);
            Walk(when.Then, ops, calls);
        }

        if (e.Else is { } otherwise)
            Walk(otherwise, ops, calls);
        foreach (var argument in e.With ?? [])
            Walk(argument.Value, ops, calls);
    }

    /// <summary>Compares one answer with the corpus: the raw answer at a value site, not false for a check, true for a guard.</summary>
    private static void Expect(List<string> failures, Case c, int row, ExprSiteKind mode, bool? answer, bool wellFormed)
    {
        var raw = c.Rows[row].Expect;
        bool? expected = mode switch
        {
            ExprSiteKind.Check => raw != false,
            ExprSiteKind.Guard => raw == true,
            _ => raw,
        };
        if (!wellFormed || answer != expected)
        {
            failures.Add($"{c.Name} [{mode}] row {row + 1} {c.Rows[row].Values.GetRawText()}: expected {Show(expected)}, got "
                + (wellFormed ? Show(answer) : "no answer") + "\n    " + c.Text + "\n    js:  " + c.JavaScript[mode]
                + (c.Sql.TryGetValue(mode, out var sql) ? "\n    sql: " + sql : ""));
        }
    }

    private static string Show(bool? b) => b switch { true => "true", false => "false", null => "null" };

    private static async Task<string> Psql(string container, string database, string script, bool stopOnError)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "exec", "-i", container, "psql", "-U", "postgres", "-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=" + (stopOnError ? "1" : "0"), "-d", database })
            start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(cts.Token);
        var output = await stdout + "\n" + await stderr;
        Assert.True(!stopOnError || process.ExitCode == 0, "psql failed on " + container + ":\n" + output);
        return output;
    }

    /// <summary>One row of a case: its values by member name (nested for payloads and relations) and its three-valued answer.</summary>
    private sealed record CaseRow(JsonElement Values, bool? Expect);

    /// <summary>A column of a case: its member, its physical name and type, and how an enum or code is stored.</summary>
    private sealed record CaseColumn(string Key, string Column, string SqlType, ExprMember Member, ExprStoredForm Form);

    /// <summary>A case, typed and lowered once per mode.</summary>
    private sealed class Case
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Text { get; init; }
        public required bool AtTable { get; init; }
        public required IReadOnlyList<CaseColumn> Columns { get; init; }
        public required IReadOnlyList<(string Name, IReadOnlyList<CaseColumn> Columns)> Relations { get; init; }
        public required IReadOnlyList<CaseRow> Rows { get; init; }
        public Expr Tree { get; set; } = new();
        public Dictionary<ExprSiteKind, string> JavaScript { get; } = [];
        public Dictionary<ExprSiteKind, string> Sql { get; } = [];

        /// <summary>A table case runs as a value, a check and a guard; a guard case as a guard.</summary>
        public IReadOnlyList<ExprSiteKind> Modes => AtTable ? [ExprSiteKind.Value, ExprSiteKind.Check, ExprSiteKind.Guard] : [ExprSiteKind.Guard];

        public string Table => "corpus_" + Index.ToString(CultureInfo.InvariantCulture);

        public string Key(ExprSiteKind mode) => Index.ToString(CultureInfo.InvariantCulture) + ":" + mode;

        /// <summary>The row JavaScript reads: by column name at a table, as written (nested) otherwise.</summary>
        public string JsonRow(int row)
        {
            var values = Rows[row].Values;
            if (!AtTable)
                return values.GetRawText();
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var column in Columns)
                {
                    writer.WritePropertyName(column.Column);
                    if (Read(values, column.Key) is { } value)
                        value.WriteTo(writer);
                    else
                        writer.WriteNullValue();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>Writes the case's tables, rows and queries: <c>v|case|row|answer</c> and <c>g|…</c> lines, <c>k|case|row</c> per row its CHECK accepts.</summary>
        public void WriteSql(StringBuilder sql)
        {
            var columns = Columns.Select(c => (Column: c.Column, SqlType: c.SqlType)).Concat(Relations.Select(r => (Column: Snake(r.Name) + "_row", SqlType: "integer"))).ToList();
            sql.Append("CREATE TABLE ").Append(Table).Append(" (mq_row integer PRIMARY KEY")
                .Append(string.Concat(columns.Select(c => ", " + c.Column + " " + c.SqlType))).Append(");\n");
            foreach (var (name, related) in Relations)
            {
                sql.Append("CREATE TABLE ").Append(Table).Append('_').Append(Snake(name)).Append(" (mq_row integer PRIMARY KEY")
                    .Append(string.Concat(related.Select(c => ", " + c.Column + " " + c.SqlType))).Append(");\n");
            }

            for (var r = 0; r < Rows.Count; r++)
            {
                var values = Rows[r].Values;
                var row = (r + 1).ToString(CultureInfo.InvariantCulture);
                var literals = Columns.Select(c => Literal(Read(values, c.Key), c.SqlType))
                    .Concat(Relations.Select(rel => Read(values, rel.Name) is { ValueKind: JsonValueKind.Object } ? row : "NULL"));
                sql.Append("INSERT INTO ").Append(Table).Append(" (mq_row").Append(string.Concat(columns.Select(c => ", " + c.Column)))
                    .Append(") VALUES (").Append(row).Append(string.Concat(literals.Select(l => ", " + l))).Append(");\n");
                foreach (var (name, related) in Relations)
                {
                    if (Read(values, name) is not { ValueKind: JsonValueKind.Object } target)
                        continue;
                    sql.Append("INSERT INTO ").Append(Table).Append('_').Append(Snake(name)).Append(" (mq_row")
                        .Append(string.Concat(related.Select(c => ", " + c.Column))).Append(") VALUES (").Append(row)
                        .Append(string.Concat(related.Select(c => ", " + Literal(Read(target, c.Key), c.SqlType)))).Append(");\n");
                }
            }

            foreach (var (mode, tag) in new[] { (ExprSiteKind.Value, "v"), (ExprSiteKind.Guard, "g") })
            {
                if (Sql.TryGetValue(mode, out var text))
                {
                    sql.Append("SELECT '").Append(tag).Append('|').Append(Index).Append("|' || mq_row || '|' || coalesce(CAST((").Append(text)
                        .Append(") AS text), 'null') FROM ").Append(Table).Append(" ORDER BY mq_row;\n");
                }
            }

            if (Sql.TryGetValue(ExprSiteKind.Check, out var check))
            {
                var checkedTable = Table + "_checked";
                sql.Append("CREATE TABLE ").Append(checkedTable).Append(" (LIKE ").Append(Table).Append(");\n")
                    .Append("ALTER TABLE ").Append(checkedTable).Append(" ADD CONSTRAINT ").Append(checkedTable).Append("_ck CHECK (").Append(check).Append(");\n");
                for (var r = 1; r <= Rows.Count; r++)
                    sql.Append("INSERT INTO ").Append(checkedTable).Append(" SELECT * FROM ").Append(Table).Append(" WHERE mq_row = ").Append(r).Append(";\n");
                sql.Append("SELECT 'k|").Append(Index).Append("|' || mq_row FROM ").Append(checkedTable).Append(" ORDER BY mq_row;\n");
            }
        }

        private static JsonElement? Read(JsonElement values, string key)
        {
            var current = values;
            foreach (var segment in key.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                    return null;
            }

            return current.ValueKind == JsonValueKind.Null ? null : current;
        }

        /// <summary>A value as a SQL literal: a missing value or JSON document is NULL, a number keeps its digits.</summary>
        private static string Literal(JsonElement? value, string sqlType)
        {
            if (value is not { } v)
                return "NULL";
            if (sqlType == "jsonb")
                return Quote(v.GetRawText()) + "::jsonb";
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "TRUE",
                JsonValueKind.False => "FALSE",
                JsonValueKind.String => Quote(v.GetString()!),
                _ => throw new InvalidOperationException("A column value is " + v.ValueKind + "."),
            };
        }

        private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Reads the corpus and lowers each case.</summary>
    private static class Corpus
    {
        private static readonly Lazy<IReadOnlyList<Case>> Cases = new(Read);

        public static IReadOnlyList<Case> Load() => Cases.Value;

        private static IReadOnlyList<Case> Read()
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Fixtures.Path("expressions", "corpus.json")));
            var root = doc.RootElement;
            var next = 0;
            string NewId() => "01JC0RPVS" + (++next).ToString("D17", CultureInfo.InvariantCulture);

            // Enums and reference types: their ids, and the literal each member or code lowers to in each stored form.
            var sets = new List<(string Id, string Name, bool Code, List<(string Id, string Name)> Members)>();
            var literals = new Dictionary<string, (long Integer, string Text)>(StringComparer.Ordinal);
            foreach (var e in root.GetProperty("enums").EnumerateArray())
            {
                var members = new List<(string Id, string Name)>();
                var position = 0;
                foreach (var m in e.GetProperty("members").EnumerateArray())
                {
                    var id = NewId();
                    var name = m.GetProperty("name").GetString()!;
                    members.Add((id, name));
                    literals[id] = (m.TryGetProperty("value", out var value) ? value.GetInt64() : position,
                        m.TryGetProperty("code", out var code) ? code.GetString()! : name);
                    position++;
                }

                sets.Add((NewId(), e.GetProperty("name").GetString()!, false, members));
            }

            foreach (var r in root.GetProperty("referenceTypes").EnumerateArray())
            {
                var codes = new List<(string Id, string Name)>();
                foreach (var code in r.GetProperty("codes").EnumerateArray())
                {
                    var id = NewId();
                    codes.Add((id, code.GetString()!));
                    literals[id] = (0, code.GetString()!);
                }

                sets.Add((NewId(), r.GetProperty("name").GetString()!, true, codes));
            }

            ExprScope NewScope(ExprSiteKind site) => Sets(new ExprScope(site) { ValueType = site == ExprSiteKind.Value ? ExprType.Bool : null });
            ExprScope Sets(ExprScope scope)
            {
                foreach (var (id, name, code, members) in sets)
                {
                    if (code)
                        scope.AddReferenceType(id, name, members);
                    else
                        scope.AddEnum(id, name, members);
                }

                return scope;
            }

            var constraints = new List<ExprConstraint>();
            foreach (var c in root.GetProperty("constraints").EnumerateArray())
            {
                var parameters = c.GetProperty("parameters").EnumerateObject()
                    .Select(p => Column(p.Name, p.Value.GetString()!, NewId, sets).Member).ToList();
                var scope = NewScope(ExprSiteKind.Check);
                foreach (var parameter in parameters)
                    scope.Root.Add(parameter);
                constraints.Add(new ExprConstraint(NewId(), c.GetProperty("name").GetString()!, parameters, Parse(c.GetProperty("condition").GetString()!, scope)));
            }

            var cases = new List<Case>();
            foreach (var c in root.GetProperty("cases").EnumerateArray())
            {
                var columns = c.GetProperty("columns").EnumerateObject().Select(p => Column(p.Name, p.Value.GetString()!, NewId, sets)).ToList();
                var relations = c.TryGetProperty("relations", out var rels)
                    ? rels.EnumerateObject().Select(r => (r.Name, (IReadOnlyList<CaseColumn>)r.Value.EnumerateObject()
                        .Select(p => Column(p.Name, p.Value.GetString()!, NewId, sets)).ToList())).ToList()
                    : [];
                var navigations = relations.Select(r =>
                {
                    var target = new ExprContainer();
                    foreach (var column in r.Item2)
                        target.Add(column.Member);
                    return new ExprNavigation(NewId(), r.Name, target, ToOne: true, Optional: true);
                }).ToList();
                var test = new Case
                {
                    Index = cases.Count + 1,
                    Name = c.GetProperty("name").GetString()!,
                    Text = c.GetProperty("text").GetString()!,
                    AtTable = !(c.TryGetProperty("site", out var site) && site.GetString() == "guard"),
                    Columns = columns,
                    Relations = relations,
                    Rows = c.GetProperty("rows").EnumerateArray()
                        .Select(r => new CaseRow(r[0].Clone(), r[1].ValueKind == JsonValueKind.Null ? null : r[1].GetBoolean())).ToList(),
                };
                foreach (var mode in test.Modes)
                {
                    var scope = NewScope(mode);
                    foreach (var column in columns)
                        scope.Root.Add(column.Member);
                    foreach (var navigation in navigations)
                        scope.Root.Add(navigation);
                    foreach (var constraint in constraints)
                        scope.AddConstraint(constraint);
                    var tree = Parse(test.Text, scope);
                    test.Tree = tree;
                    var problems = new List<Diagnostic>();
                    var typed = ExprTyper.Type(tree, scope, problems);
                    Assert.True(typed is not null && problems.Count == 0, test.Name + ": " + string.Join("; ", problems.Select(p => p.Rule + " " + p.Message)));
                    var mapping = Mapping(test, literals);
                    test.JavaScript[mode] = ExprLowering.ToJavaScript(typed!, mode, mapping);
                    var sql = ExprLowering.ToPostgreSql(typed!, mapping, mode, problems);
                    Assert.True(sql is not null && problems.Count == 0, test.Name + ": " + string.Join("; ", problems.Select(p => p.Rule + " " + p.Message)));
                    test.Sql[mode] = sql!;
                }

                cases.Add(test);
            }

            return cases;
        }

        private static Expr Parse(string text, ExprScope scope)
        {
            var parsed = ExprText.Parse(text, scope);
            Assert.True(parsed.Succeeded, text + ": " + parsed.Problem?.Message);
            return parsed.Tree!;
        }

        /// <summary>The case's columns as both lowerings read them: snake-case columns, stored forms, relations as scalar subqueries.</summary>
        private static ExprColumns Mapping(Case c, Dictionary<string, (long Integer, string Text)> literals) => new()
        {
            Physical = c.AtTable,
            Ref = (id, form) => form == ExprStoredForm.Integer ? literals[id].Integer : literals[id].Text,
            Column = t =>
            {
                if (t.Through.Count > 0)
                {
                    var relation = Snake(t.Through[0].Name);
                    var target = c.Relations.Single(r => r.Name == t.Through[0].Name).Columns.Single(col => col.Member.Id == t.Member!.Id);
                    return new ExprColumn(target.Column)
                    {
                        Sql = "(SELECT r." + target.Column + " FROM " + c.Table + "_" + relation + " r WHERE r.mq_row = " + relation + "_row)",
                        FixedLength = target.Member.FixedLength,
                        Scale = target.Member.Scale,
                        Form = target.Form,
                    };
                }

                var column = c.Columns.Single(col => col.Member.Id == t.Member!.Id);
                return new ExprColumn(column.Column) { FixedLength = column.Member.FixedLength, Scale = column.Member.Scale, Form = column.Form };
            },
        };

        /// <summary>A column from its corpus type: <c>decimal(12,2)</c>, <c>char(5)</c>, <c>enum:Name:integer</c>, <c>code:Name</c>, a trailing <c>?</c>.</summary>
        private static CaseColumn Column(string key, string spec, Func<string> newId, List<(string Id, string Name, bool Code, List<(string Id, string Name)> Members)> sets)
        {
            var nullable = spec.EndsWith('?');
            spec = spec.TrimEnd('?');
            var dot = key.LastIndexOf('.');
            var (prefix, name) = dot < 0 ? ((string?)null, key) : (key[..dot], key[(dot + 1)..]);
            var parts = spec.Split(':');
            var size = SizeOf().Match(parts[0]);
            var keyword = size.Success ? size.Groups[1].Value : parts[0];
            var form = ExprStoredForm.Text;
            ExprType type;
            string sqlType;
            switch (keyword)
            {
                case "enum":
                    type = ExprType.Enum(sets.Single(s => s.Name == parts[1]).Id);
                    form = parts[2] == "integer" ? ExprStoredForm.Integer : ExprStoredForm.Text;
                    sqlType = form == ExprStoredForm.Integer ? "integer" : "text";
                    break;
                case "code":
                    type = ExprType.Code(sets.Single(s => s.Name == parts[1]).Id);
                    sqlType = "text";
                    break;
                case "char":
                    type = ExprType.String;
                    sqlType = parts[0];
                    break;
                default:
                    type = ExprType.FromBuiltin(keyword);
                    sqlType = keyword switch
                    {
                        "bool" => "boolean",
                        "int16" => "smallint",
                        "int32" => "integer",
                        "int64" => "bigint",
                        "decimal" => size.Success ? "numeric(" + size.Groups[2].Value + ")" : "numeric",
                        "double" => "double precision",
                        "string" => "text",
                        "datetime" => "timestamp",
                        "datetimeoffset" => "timestamptz",
                        "json" => "jsonb",
                        _ => keyword,
                    };
                    break;
            }

            int? scale = keyword == "decimal" && size.Success ? int.Parse(size.Groups[2].Value.Split(',')[1], CultureInfo.InvariantCulture) : null;
            var member = new ExprMember(newId(), name, type, nullable) { Prefix = prefix, FixedLength = keyword == "char", Scale = scale };
            return new CaseColumn(key, Snake(key), sqlType, member, form);
        }
    }

    private static string Snake(string name) => Upper().Replace(name.Replace('.', '_'), m => "_" + m.Value.ToLowerInvariant());

    [GeneratedRegex("^([a-z0-9]+)\\(([0-9, ]+)\\)$")]
    private static partial Regex SizeOf();

    [GeneratedRegex("[A-Z]")]
    private static partial Regex Upper();
}
