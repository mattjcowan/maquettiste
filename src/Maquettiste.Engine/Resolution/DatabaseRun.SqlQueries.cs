using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Queries written as SQL text (sql-queries-design.md decisions 3 to 9, 18 and section 9): the texts per dialect, the declared columns
/// as the select list (fields without an expression), <c>reads</c> as <see cref="RQuery.Uses"/> and <c>writes</c> as
/// <see cref="RQuery.Writes"/>, and the checks the light reading of the text allows (<see cref="SqlText"/>): MQ4024, MQ4029, MQ4096 and
/// MQ4098, with the cases of MQ3001, MQ4018, MQ4021 and MQ4040 the declarations add. The engine never parses the SQL.
/// </summary>
internal sealed partial class DatabaseRun
{
    private sealed partial class QueryBuilder
    {
        /// <summary>The verbs of a statement that changes data (MQ4098's second case).</summary>
        private static readonly string[] CommandVerbs = ["INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "TRUNCATE"];

        /// <summary>The verbs of a statement that returns rows (MQ4098's third case).</summary>
        private static readonly string[] RowVerbs = ["SELECT", "VALUES", "TABLE"];

        /// <summary>Resolves a query written as SQL text, after its parameters.</summary>
        private void BuildSqlText()
        {
            query.Form = "sql";
            query.Texts = file.Sql!.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            query.SqlText = QuerySql.SqlFor(query.Texts, run._dialect);
            query.Select = ResolveColumns();
            query.Uses = ResolveDeclared(file.Reads, "reads", tablesOnly: false);
            query.Writes = [.. ResolveDeclared(file.Writes, "writes", tablesOnly: true).OfType<RTable>()];

            if (query.SqlText is null)
                Report("MQ4029", $"Query '{file.Name}' is written as SQL text without a text for {run._dialect} (database '{run._db.Name}') and no \"*\" text.", "/sql");

            // Each text read with its own dialect's rules ("*": the database's), in ordinal key order.
            var scans = new List<(string Key, string Text, SqlTextScan Scan)>();
            foreach (var (key, text) in query.Texts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (TextDialect(key) is { } dialect)
                    scans.Add((key, text, SqlText.Scan(text, dialect)));
            }

            CheckPlaceholders(scans);
            CheckDeclarations(scans);

            // A query with errors has none: they are reported above, and generation stops on them before any template reads it.
            query.Sql = _failed ? null : QuerySql.Render(query).Sql;
        }

        /// <summary>The dialect a text is read with: its own, or the database's for <c>"*"</c>; <see langword="null"/> for no dialect.</summary>
        private Dialect? TextDialect(string key) =>
            SqlDialects.TryParse(key == "*" ? run._dialect : key, out var dialect) ? dialect : null;

        /// <summary>The declared columns as fields without an expression (MQ4018, MQ3001, MQ4040).</summary>
        private List<RQueryField> ResolveColumns()
        {
            var fields = new List<RQueryField>();
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < file.Columns.Count; i++)
            {
                var c = file.Columns[i];
                var pointer = "/columns/" + i.ToString(CultureInfo.InvariantCulture);
                var (keyword, dbType, native) = run.Slot(c.Type, null, null, null, null, deps);
                if (keyword is null && dbType is null)
                    Report("MQ4018", $"Column '{c.Name}' of query '{file.Name}' has type '{c.Type}', which is neither a built-in type nor a database type of database '{run._db.Name}'.", pointer + "/type");
                fields.Add(new RQueryField
                {
                    Name = c.Name,
                    Type = keyword,
                    NativeType = native,
                    CodeType = keyword ?? (dbType is { TypeKind: "domain", Base: { } baseType } ? baseType : "string"),
                    Nullable = c.Nullable,
                    Description = c.Description,
                });

                if (!names.TryAdd(c.Name, i))
                    Report("MQ3001", $"Column name '{c.Name}' is already used at index {names[c.Name].ToString(CultureInfo.InvariantCulture)} of the columns of query '{file.Name}' (ignoring case).", pointer + "/name");
                else if (IsReserved(c.Name))
                    Report("MQ4040", $"Column '{c.Name}' of query '{file.Name}' starts with mq_, which generated code reserves for its own names.", pointer + "/name");
                else if (!properties.TryAdd(Casing.Pascal(c.Name), c.Name))
                    Report("MQ4040", $"Column '{c.Name}' of query '{file.Name}' becomes property '{Casing.Pascal(c.Name)}', as column '{properties[Casing.Pascal(c.Name)]}' does: give it another name.", pointer + "/name");
            }

            return fields;
        }

        /// <summary>
        /// The <c>reads</c> or <c>writes</c> entries resolved, in file order (MQ4021 for one that is not of the database). The query keys
        /// on each entry's element only, not on what the object depends on: its output does not change with them (decision 18).
        /// </summary>
        private List<IResolvedObject> ResolveDeclared(IReadOnlyList<string> references, string key, bool tablesOnly)
        {
            var resolved = new List<IResolvedObject>();
            for (var i = 0; i < references.Count; i++)
            {
                var reference = references[i];
                IResolvedObject? target = null;
                var hit = sources.TryGetValue(reference, out var found) ? found
                    : sources.TryGetValue(reference + "@" + run._db.Id, out found) && found.Table is not null ? found
                    : default;
                if (hit.Table is { } t)
                {
                    target = t.Table;
                    if (t.Table.FileId is { } fileId)
                        deps.Element(fileId);
                }
                else if (!tablesOnly)
                {
                    target = hit.View
                        ?? (routines.TryGetValue(reference, out var routine) ? routine
                        : run._sequences.TryGetValue(reference, out var sequence) ? sequence
                        : run._dbTypes.TryGetValue(reference, out var dbType) ? dbType
                        : (IResolvedObject?)null);
                }

                if (target is null)
                {
                    Report("MQ4021", $"Query '{file.Name}' {(tablesOnly ? "writes" : "reads")} '{reference}', which {WhatIsDeclared(reference, tablesOnly)}.", "/" + key + "/" + i.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                deps.Element(reference.Split('@')[0]);
                query.Declared = [.. query.Declared, (reference, target)];
                if (!resolved.Contains(target))
                    resolved.Add(target);
            }

            return resolved;
        }

        /// <summary>What a <c>reads</c> or <c>writes</c> entry that resolves to nothing it may name is, for MQ4021.</summary>
        private string WhatIsDeclared(string reference, bool tablesOnly)
        {
            var allowed = tablesOnly ? "a table" : "a table, view, routine, sequence or database type";
            var element = run._run.Model.Get<Element>(reference.Split('@')[0]);
            var other = element switch
            {
                Table t => t.Database,
                View v => v.Database,
                Routine r => r.Database,
                Sequence s => s.Database,
                DatabaseType d => d.Database,
                _ => null,
            };
            if (other is not null && other != run._db.Id)
                return $"is {element!.KindName} '{element.Name}' of database '{run._run.Model.Get<Database>(other)?.Name ?? other}', not of '{run._db.Name}'";
            if (element is Entity entity)
                return $"is entity '{entity.Name}', which has no table in database '{run._db.Name}'";
            if (element is not null)
                return $"is {element.KindName} '{element.Name}', not {allowed}";
            return $"is no {(tablesOnly ? "table" : "table, view, routine, sequence or database type")} of database '{run._db.Name}'";
        }

        /// <summary>
        /// MQ4024 for a placeholder that names no parameter (with its line and column in the text), and MQ4096 for a parameter that
        /// appears in none of the texts, or not in each.
        /// </summary>
        private void CheckPlaceholders(List<(string Key, string Text, SqlTextScan Scan)> scans)
        {
            var used = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (key, text, scan) in scans)
            {
                var named = new HashSet<string>(StringComparer.Ordinal);
                foreach (var placeholder in scan.Placeholders)
                {
                    named.Add(placeholder.Name);
                    if (_parameters.ContainsKey(placeholder.Name))
                        continue;
                    var (line, column) = SqlText.Position(text, placeholder.Offset);
                    Report("MQ4024", $"Query '{file.Name}': ':{placeholder.Name}' (line {line.ToString(CultureInfo.InvariantCulture)}, column {column.ToString(CultureInfo.InvariantCulture)} of the {key} text) names no parameter.", "/sql/" + Pointer(key));
                }

                used[key] = named;
            }

            if (scans.Count == 0)
                return;
            for (var i = 0; i < file.Parameters.Count; i++)
            {
                var name = file.Parameters[i].Name;
                var missing = scans.Where(s => !used[s.Key].Contains(name)).Select(s => s.Key).ToList();
                if (missing.Count == 0)
                    continue;
                Report("MQ4096", missing.Count == scans.Count
                    ? $"Parameter '{name}' of query '{file.Name}' appears as :{name} in none of its texts."
                    : $"Parameter '{name}' of query '{file.Name}' does not appear as :{name} in its {Words(missing)} {(missing.Count == 1 ? "text" : "texts")}.",
                    "/parameters/" + i.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// MQ4098: the text and the declarations disagree, by name and verb only (decision 6): a declared table or view whose name
        /// appears in none of the texts (ignoring case); a statement that changes data with no <c>writes</c>; one that returns rows
        /// with no <c>columns</c>.
        /// </summary>
        private void CheckDeclarations(List<(string Key, string Text, SqlTextScan Scan)> scans)
        {
            if (scans.Count == 0)
                return;
            NamesAppear(file.Reads, "reads");
            NamesAppear(file.Writes, "writes");
            foreach (var (key, _, scan) in scans)
            {
                if (scan.Verb is not { } verb)
                    continue;
                if (file.Writes.Count == 0 && CommandVerbs.Contains(verb, StringComparer.Ordinal))
                    Report("MQ4098", $"The {key} text of query '{file.Name}' is {Article(verb)} {verb} statement{(scan.FirstWord == "WITH" ? " (after its WITH)" : "")}, but the query declares no table it writes: list them in writes.", "/sql/" + Pointer(key));
                else if (file.Columns.Count == 0 && RowVerbs.Contains(verb, StringComparer.Ordinal))
                    Report("MQ4098", $"The {key} text of query '{file.Name}' is {Article(verb)} {verb} statement{(scan.FirstWord == "WITH" ? " (after its WITH)" : "")}, but the query declares no columns, so it would be generated as a command that returns the number of rows changed: declare its columns.", "/sql/" + Pointer(key));
            }

            void NamesAppear(IReadOnlyList<string> references, string key)
            {
                for (var i = 0; i < references.Count; i++)
                {
                    var hit = sources.TryGetValue(references[i], out var found) ? found
                        : sources.TryGetValue(references[i] + "@" + run._db.Id, out found) && found.Table is not null ? found
                        : default;
                    var (kind, name) = hit.Table is { } t ? ("table", t.Table.Name) : hit.View is { } v ? ("view", v.Name) : (null, null);
                    if (name is null || scans.Any(s => s.Scan.Mentions(name)))
                        continue;
                    Report("MQ4098", $"Query '{file.Name}' {(key == "reads" ? "reads" : "writes")} {kind} '{name}', whose name appears in none of its texts: was it renamed, or declared by mistake?", "/" + key + "/" + i.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>A text's key as a JSON pointer segment (<c>"*"</c> needs no escape).</summary>
        private static string Pointer(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

        private static string Article(string verb) => verb is "INSERT" or "UPDATE" ? "an" : "a";

        private static string Words(List<string> keys) =>
            keys.Count == 1 ? keys[0] : string.Join(", ", keys.Take(keys.Count - 1)) + " and " + keys[^1];
    }
}
