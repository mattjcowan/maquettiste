using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Reference type bindings (2026-10-11): a reference type names the table file that holds its rows in a database. Its rows join the
/// database's seed tables, count for the foreign keys of table seeds (MQ7111) and their row keys (MQ7112), order the seeds that name
/// them, and the binding's own rules (MQ7117 to MQ7120) check the table, the field map and the key. A type without one is unchanged.
/// </summary>
public sealed class ReferenceBindingTests
{
    private const string Terms = "01K7RTB0000000000000000001", TermCode = "01K7RTB0000000000000000002", TermName = "01K7RTB0000000000000000003";
    private const string TermDays = "01K7RTB0000000000000000004", TermNote = "01K7RTB0000000000000000005";
    private const string Accounts = "01K7RTB0000000000000000010", AccountId = "01K7RTB0000000000000000011", AccountTerm = "01K7RTB0000000000000000012";
    private const string AccountTermKey = "01K7RTB0000000000000000013";
    private const string PaymentTerm = "01K7RTB0000000000000000020", PaymentTermCode = "01K7RTB0000000000000000021", PaymentTermLabel = "01K7RTB0000000000000000022";
    private const string PaymentTermDays = "01K7RTB0000000000000000023", PaymentTermBinding = "01K7RTB0000000000000000024";
    private const string TermSeed = "01K7RTB0000000000000000030", ExtraSeed = "01K7RTB0000000000000000040", AccountSeed = "01K7RTB0000000000000000050";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static async Task<SaveResult> CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return result;
    }

    /// <summary>Writes a file straight to disk and loads it (a save is refused while it adds errors).</summary>
    private static async Task WriteAsync(EditorRepo r, string relative, string json)
    {
        var path = r.Repo.PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonNode.Parse(json)!.ToJsonString());
        await r.Store.RefreshAsync([path], Ct);
    }

    /// <summary>Overwrites an element's file on disk and loads it.</summary>
    private static async Task RewriteAsync(EditorRepo r, string id, string json)
    {
        var path = r.Repo.PathOf((await r.Store.GetElementAsync(id, Ct))!.Path);
        File.WriteAllText(path, JsonNode.Parse(json)!.ToJsonString());
        await r.Store.RefreshAsync([path], Ct);
    }

    private static string TypeJson(string bindings) => $$"""
        {"kind":"reference-type","id":"{{PaymentTerm}}","name":"PaymentTerm",
         "code":{"id":"{{PaymentTermCode}}","length":10},"label":{"id":"{{PaymentTermLabel}}","length":40},
         "attributes":[{"id":"{{PaymentTermDays}}","name":"dueDays","type":"int32"}],
         "bindings":{{bindings}}}
        """;

    private static readonly string Binding = $$"""
        [{"id":"{{PaymentTermBinding}}","database":"{{EditorRepo.MainDatabaseId}}","table":"{{Terms}}",
          "fields":[{"attribute":"code","column":"{{TermCode}}"},{"attribute":"label","column":"name"},{"attribute":"{{PaymentTermDays}}","column":"{{TermDays}}"}]}]
        """;

    /// <summary>
    /// A payment terms table bound to the PaymentTerm type, whose seed holds net30 and net60; a demo seed of the table adds net90;
    /// an accounts table's foreign key names a term.
    /// </summary>
    private static async Task<EditorRepo> RepoAsync()
    {
        var r = EditorRepo.Create(packs: false);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Terms}}","name":"payment_terms","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{TermCode}}","name":"code","type":"string","length":10,"nullable":false},
              {"id":"{{TermName}}","name":"name","type":"string","length":40,"nullable":false},
              {"id":"{{TermDays}}","name":"due_days","type":"int32"},
              {"id":"{{TermNote}}","name":"note","type":"string","length":100}],
             "primaryKey":{"columns":["{{TermCode}}"]} }
            """);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Accounts}}","name":"accounts","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{AccountId}}","name":"id","type":"int32","nullable":false},
              {"id":"{{AccountTerm}}","name":"payment_term","type":"string","length":10,"nullable":false}],
             "primaryKey":{"columns":["{{AccountId}}"]},
             "foreignKeys":[{"id":"{{AccountTermKey}}","name":"fk_accounts_payment_term","columns":["{{AccountTerm}}"],"referencesTable":"{{Terms}}"}]}
            """);
        await CreateAsync(r, TypeJson(Binding));
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{TermSeed}}","name":"terms","target":"{{PaymentTerm}}","columns":["code","label","{{PaymentTermDays}}"],
             "rows":[{"id":"01K7RTB0000000000000000031","values":["net30","Net 30 days",30]},{"id":"01K7RTB0000000000000000032","values":["net60","Net 60 days",60]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{ExtraSeed}}","name":"extraTerms","target":"{{Terms}}","columns":["{{TermCode}}","{{TermName}}"],
             "environments":["demo"],
             "rows":[{"id":"01K7RTB0000000000000000041","values":["net90","Net 90 days"]}]}
            """);
        return r;
    }

    private static async Task<ResolvedModel> ResolvedAsync(EditorRepo r) =>
        (await r.Store.ResolvedAsync(await r.Store.GetSnapshotAsync(Ct), Ct)).Model!;

    [Fact]
    public async Task A_bound_types_rows_join_its_tables_seed_rows_first_in_column_terms()
    {
        await using var r = await RepoAsync();
        var model = await ResolvedAsync(r);
        var type = model.ReferenceTypes.Single(t => t.Name == "PaymentTerm");
        var binding = type.Bindings["main"];
        Assert.Equal(("payment_terms", "code"), (binding.Table.Name, binding.CodeColumn!.Name));
        Assert.Equal(["code:name=code", "label:name=name", "dueDays:name=due_days"], binding.Fields.Select(f => f.Name + ":name=" + f.Column.Name));
        Assert.Equal([binding], model.Databases.Single(d => d.Name == "main").Tables.Single(t => t.Name == "payment_terms").ReferenceBindings);

        var terms = model.Databases.Single(d => d.Name == "main").SeedTables.Single(t => t.Table.Name == "payment_terms");
        Assert.Equal(["terms", "extraTerms"], terms.Seeds.Select(s => s.Name));
        Assert.Equal(["list:net30:Net 30 days:30", "list:net60:Net 60 days:60", "table:net90:Net 90 days:"],
            terms.Rows.Select(x => $"{x.Source}:{x.Values["code"]}:{x.Values["name"]}:{x.Values.GetValueOrDefault("due_days")}"));
        Assert.Equal(["code", "name", "due_days"], terms.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task A_foreign_key_into_a_bound_table_names_the_types_rows_and_orders_after_them()
    {
        await using var r = await RepoAsync();
        // By name the accounts come before the terms; their foreign key names a row of the type.
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{AccountSeed}}","name":"accounts","target":"{{Accounts}}","columns":["{{AccountId}}","{{AccountTerm}}"],
             "rows":[{"id":"01K7RTB0000000000000000051","values":[1,"net30"]}]}
            """);
        var model = await ResolvedAsync(r);
        var order = model.SeedsInOrder.Select(s => s.Name).ToList();
        Assert.True(order.IndexOf("terms") < order.IndexOf("accounts"), string.Join(", ", order));

        // net90 is a row of the demo seed only, so a row for every environment cannot name it; net15 is nowhere.
        await RewriteAsync(r, AccountSeed, $$"""
            {"kind":"seed","id":"{{AccountSeed}}","name":"accounts","target":"{{Accounts}}","columns":["{{AccountId}}","{{AccountTerm}}"],
             "rows":[{"id":"01K7RTB0000000000000000051","values":[1,"net30"]},{"id":"01K7RTB0000000000000000052","values":[2,"net90"]},
               {"id":"01K7RTB0000000000000000053","values":[3,"net15"]}]}
            """);
        var rules = (await r.Store.ValidateAsync(new ValidationScope([AccountSeed]), Ct)).Diagnostics.Where(d => d.Rule == "MQ7111").ToList();
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, d => d.Message.StartsWith("Row 01K7RTB0000000000000000052:", StringComparison.Ordinal) && d.Message.Contains("in every environment", StringComparison.Ordinal));
        Assert.Contains(rules, d => d.Message.StartsWith("Row 01K7RTB0000000000000000053:", StringComparison.Ordinal) && d.Message.Contains("or of the reference types bound to it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_table_seed_row_cannot_repeat_a_bound_types_code_where_both_are_loaded()
    {
        await using var r = await RepoAsync();
        await WriteAsync(r, ".maquettiste/model/seeds/payment-terms/more.json", $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01K7RTB0000000000000000060","name":"moreTerms","target":"{{Terms}}",
             "columns":["{{TermCode}}","{{TermName}}"],"environments":["demo"],
             "rows":[{"id":"01K7RTB0000000000000000061","values":["net60","Sixty"]},{"id":"01K7RTB0000000000000000062","values":["net120","Net 120"]}]}
            """);

        var rules = (await r.Store.ValidateAsync(new ValidationScope(["01K7RTB0000000000000000060"]), Ct)).Diagnostics.Where(d => d.Rule == "MQ7112").ToList();
        var repeat = Assert.Single(rules);
        Assert.StartsWith("Row 01K7RTB0000000000000000061 repeats the row key of a row of reference type 'PaymentTerm' (seed 'terms')", repeat.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binding_rules_check_the_table_the_fields_the_key_and_the_columns_left_unfilled()
    {
        await using var r = await RepoAsync();
        // A second binding to the same database; an unknown field and column; no code; the label in a column too short for it.
        await RewriteAsync(r, PaymentTerm, TypeJson($$"""
            [{"id":"01K7RTB0000000000000000070","database":"{{EditorRepo.MainDatabaseId}}","table":"{{Terms}}",
              "fields":[{"attribute":"label","column":"{{TermCode}}"},{"attribute":"colour","column":"{{TermDays}}"},{"attribute":"description","column":"missing"}]},
             {"id":"01K7RTB0000000000000000071","database":"{{EditorRepo.MainDatabaseId}}","table":"{{Accounts}}","fields":[]}]
            """));

        var report = await r.Store.ValidateAsync(new ValidationScope([PaymentTerm]), Ct);
        string Messages(string rule) => string.Join(" | ", report.Diagnostics.Where(d => d.Rule == rule).Select(d => d.Message));
        Assert.Contains("has a second binding to one database", Messages("MQ7117"), StringComparison.Ordinal);
        Assert.Contains("'colour' is not a field of reference type 'PaymentTerm'", Messages("MQ7118"), StringComparison.Ordinal);
        Assert.Contains("'missing' is not a column of table 'payment_terms'", Messages("MQ7118"), StringComparison.Ordinal);
        Assert.Contains("maps no column to code", Messages("MQ7118"), StringComparison.Ordinal);
        Assert.Contains("labels of up to 40 characters, more than its length 10", Messages("MQ7118"), StringComparison.Ordinal);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ7119"); // no code column: the key is not checked
        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ7118" && d.JsonPointer == "/bindings/0/fields/1/attribute");

        // The code mapped to a column that is no key, the name column left unfilled.
        await RewriteAsync(r, PaymentTerm, TypeJson($$"""
            [{"id":"01K7RTB0000000000000000070","database":"{{EditorRepo.MainDatabaseId}}","table":"{{Terms}}",
              "fields":[{"attribute":"code","column":"{{TermNote}}"}]}]
            """));
        report = await r.Store.ValidateAsync(new ValidationScope([PaymentTerm]), Ct);
        Assert.Contains("No primary key or unique constraint of table 'payment_terms' is made of its code column 'note'", Messages("MQ7119"), StringComparison.Ordinal);
        Assert.Contains("fills no value for column 'name'", Messages("MQ7120"), StringComparison.Ordinal);
        Assert.Contains("fills no value for column 'code'", Messages("MQ7120"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_the_bound_table_removes_the_binding_and_keeps_the_type()
    {
        await using var r = await RepoAsync();
        var plan = await r.Store.GetDeletePlanAsync([Terms], DeleteResolution.RemoveReferences, Ct);
        var binding = Assert.Single(plan.Removes, x => x.Id == PaymentTerm);
        Assert.Equal(("/bindings/0", "binding"), (binding.Pointer, binding.SubKind));
        Assert.Contains("the reference type stays", binding.Because, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Deletes, d => d.Id == PaymentTerm);
    }

    [Fact]
    public async Task A_type_without_a_binding_keeps_its_rows_out_of_the_seed_tables()
    {
        await using var r = await RepoAsync();
        var current = (await r.Store.GetElementAsync(PaymentTerm, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        node.Remove("bindings");
        // The accounts have no seed here, so nothing names the type's rows and the save adds no error.
        var saved = await r.Store.SaveAsync(PaymentTerm, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);

        var model = await ResolvedAsync(r);
        Assert.Empty(model.ReferenceTypes.Single(t => t.Name == "PaymentTerm").Bindings);
        var terms = model.Databases.Single(d => d.Name == "main").SeedTables.Single(t => t.Table.Name == "payment_terms");
        Assert.Equal(["table"], terms.Rows.Select(x => x.Source).Distinct());
    }

    [Fact]
    public async Task Removing_a_binding_or_a_seed_whose_rows_a_foreign_key_names_is_refused()
    {
        await using var r = await RepoAsync();
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{AccountSeed}}","name":"accounts","target":"{{Accounts}}","columns":["{{AccountId}}","{{AccountTerm}}"],
             "rows":[{"id":"01K7RTB0000000000000000051","values":[1,"net30"]}]}
            """);

        var current = (await r.Store.GetElementAsync(PaymentTerm, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        node.Remove("bindings");
        var saved = await r.Store.SaveAsync(PaymentTerm, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, saved.Outcome);
        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ7111" && d.Message.StartsWith("Row 01K7RTB0000000000000000051:", StringComparison.Ordinal));

        var seed = (await r.Store.GetElementAsync(TermSeed, Ct))!;
        var deleted = await r.Store.DeleteAsync(TermSeed, seed.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, deleted.Outcome);
        Assert.Contains(deleted.Diagnostics, d => d.Rule == "MQ7111");
    }

    private static string Id(int n) => "01K7RTC" + n.ToString("D19", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A kinds table seeded with the kind 'payment', and a terms table whose kind column is a foreign key into it, bound to the Term
    /// type with the given kind source (a constant, or the type's kind field); the type's seed holds the given rows.
    /// </summary>
    private static async Task<EditorRepo> KindsAsync(string constants, string kindField, string rows, string kindsKey = "")
    {
        var r = EditorRepo.Create(packs: false);
        var main = EditorRepo.MainDatabaseId;
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Id(1)}}","name":"kinds","database":"{{main}}","columns":[
              {"id":"{{Id(2)}}","name":"code","type":"string","length":10,"nullable":false}],
             "primaryKey":{"columns":["{{Id(2)}}"]} }
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{Id(3)}}","name":"zzKinds","target":"{{Id(1)}}","columns":["{{Id(2)}}"],
             "rows":[{"id":"{{Id(4)}}","values":["payment"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Id(10)}}","name":"terms","database":"{{main}}","columns":[
              {"id":"{{Id(11)}}","name":"code","type":"string","length":10,"nullable":false},
              {"id":"{{Id(12)}}","name":"name","type":"string","length":40,"nullable":false},
              {"id":"{{Id(13)}}","name":"kind","type":"string","length":10,"nullable":false}],
             "primaryKey":{"columns":["{{Id(11)}}"{{kindsKey}}]},
             "foreignKeys":[{"id":"{{Id(14)}}","name":"fk_terms_kind","columns":["{{Id(13)}}"],"referencesTable":"{{Id(1)}}"}]}
            """);
        var kind = kindField.Length > 0 ? $$""",{"attribute":"{{Id(23)}}","column":"{{Id(13)}}"}""" : "";
        await CreateAsync(r, $$"""
            {"kind":"reference-type","id":"{{Id(20)}}","name":"Term","code":{"id":"{{Id(21)}}","length":10},"label":{"id":"{{Id(22)}}","length":40},
             "attributes":[{"id":"{{Id(23)}}","name":"kind","type":"string","length":10}],
             "bindings":[{"id":"{{Id(24)}}","database":"{{main}}","table":"{{Id(10)}}",
               "fields":[{"attribute":"code","column":"{{Id(11)}}"},{"attribute":"label","column":"{{Id(12)}}"}{{kind}}],
               "constants":{{constants}}}]}
            """);
        await WriteAsync(r, ".maquettiste/model/seeds/terms/terms.json", $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{Id(30)}}","name":"aTerms","target":"{{Id(20)}}",
             "columns":["code","label"{{(kindField.Length > 0 ? $",\"{Id(23)}\"" : "")}}],"rows":{{rows}}}
            """);
        return r;
    }

    [Fact]
    public async Task A_bound_types_seed_comes_after_the_seeds_its_tables_foreign_keys_name_and_must_name_seeded_rows()
    {
        var constant = $$"""[{"column":"{{Id(13)}}","value":"payment"}]""";
        await using (var r = await KindsAsync(constant, "", $$"""[{"id":"{{Id(31)}}","values":["net30","Net 30 days"]}]"""))
        {
            Assert.Empty((await r.Store.ValidateAsync(new ValidationScope(), Ct)).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message));
            var order = (await ResolvedAsync(r)).SeedsInOrder.Select(s => s.Name).ToList();
            Assert.True(order.IndexOf("zzKinds") < order.IndexOf("aTerms"), string.Join(", ", order));
        }

        // A constant that names no kind: once, on the type's constants.
        await using (var r = await KindsAsync($$"""[{"column":"{{Id(13)}}","value":"nope"}]""", "", $$"""[{"id":"{{Id(31)}}","values":["net30","Net 30 days"]}]"""))
        {
            var rule = Assert.Single((await r.Store.ValidateAsync(new ValidationScope(), Ct)).Diagnostics, d => d.Rule == "MQ7111");
            Assert.Equal(("/bindings/0/constants", Id(24)), (rule.JsonPointer, rule.ElementId));
            Assert.Contains("foreign key 'fk_terms_kind' names no row of the seeds of table 'kinds'", rule.Message, StringComparison.Ordinal);
        }

        // A field that fills the foreign key orders the seed too; then each seed row whose kind is not seeded is reported.
        await using (var r = await KindsAsync("[]", "kind", $$"""[{"id":"{{Id(31)}}","values":["net30","Net 30 days","payment"]}]"""))
        {
            var order = (await ResolvedAsync(r)).SeedsInOrder.Select(s => s.Name).ToList();
            Assert.True(order.IndexOf("zzKinds") < order.IndexOf("aTerms"), string.Join(", ", order));

            await WriteAsync(r, ".maquettiste/model/seeds/terms/terms.json", $$"""
                {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{Id(30)}}","name":"aTerms","target":"{{Id(20)}}",
                 "columns":["code","label","{{Id(23)}}"],
                 "rows":[{"id":"{{Id(31)}}","values":["net30","Net 30 days","payment"]},{"id":"{{Id(32)}}","values":["net60","Net 60 days","other"]}]}
                """);
            var rule = Assert.Single((await r.Store.ValidateAsync(new ValidationScope(), Ct)).Diagnostics, d => d.Rule == "MQ7111");
            Assert.Equal(("/rows/1", Id(32)), (rule.JsonPointer, rule.ElementId));
            Assert.StartsWith($"Row {Id(32)}: through the binding to table 'terms', foreign key 'fk_terms_kind' names no row", rule.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_table_only_a_type_fills_is_keyed_by_the_code_unique_when_the_binding_cannot_fill_its_primary_key()
    {
        await using var r = EditorRepo.Create(packs: false);
        var main = EditorRepo.MainDatabaseId;
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Id(40)}}","name":"kinds","database":"{{main}}","columns":[
              {"id":"{{Id(41)}}","name":"id","type":"int32","nullable":false,"generated":"identity"},
              {"id":"{{Id(42)}}","name":"code","type":"string","length":10,"nullable":false},
              {"id":"{{Id(43)}}","name":"name","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{Id(41)}}"]},
             "uniques":[{"id":"{{Id(44)}}","name":"uq_kinds_code","columns":["{{Id(42)}}"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"reference-type","id":"{{Id(50)}}","name":"Kind","code":{"id":"{{Id(51)}}","length":10},"label":{"id":"{{Id(52)}}","length":40},
             "bindings":[{"id":"{{Id(53)}}","database":"{{main}}","table":"{{Id(40)}}","fields":[{"attribute":"code","column":"{{Id(42)}}"},{"attribute":"label","column":"{{Id(43)}}"}]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{Id(60)}}","name":"kinds","target":"{{Id(50)}}","columns":["code","label"],
             "rows":[{"id":"{{Id(61)}}","values":["alpha","Alpha"]}]}
            """);

        var table = (await ResolvedAsync(r)).Databases.Single(d => d.Id == main).SeedTables.Single(t => t.Table.Name == "kinds");
        Assert.Equal(["code"], table.KeyColumns.Select(c => c.Name));
        Assert.Equal(["alpha"], table.Rows.Single().Key.Select(v => v?.ToString()));
    }

    [Fact]
    public async Task Two_types_sharing_a_table_may_not_put_rows_of_the_same_key_there()
    {
        // The terms table is keyed by the code alone; a second type bound to it with another kind holds net30 too.
        var constant = $$"""[{"column":"{{Id(13)}}","value":"payment"}]""";
        await using var r = await KindsAsync(constant, "", $$"""[{"id":"{{Id(31)}}","values":["net30","Net 30 days"]}]""");
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{Id(5)}}","name":"moreKinds","target":"{{Id(1)}}","columns":["{{Id(2)}}"],
             "rows":[{"id":"{{Id(6)}}","values":["delivery"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"reference-type","id":"{{Id(70)}}","name":"Window","code":{"id":"{{Id(71)}}","length":10},"label":{"id":"{{Id(72)}}","length":40},
             "bindings":[{"id":"{{Id(73)}}","database":"{{EditorRepo.MainDatabaseId}}","table":"{{Id(10)}}",
               "fields":[{"attribute":"code","column":"{{Id(11)}}"},{"attribute":"label","column":"{{Id(12)}}"}],
               "constants":[{"column":"{{Id(13)}}","value":"delivery"}]}]}
            """);
        await WriteAsync(r, ".maquettiste/model/seeds/windows/windows.json", $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{Id(80)}}","name":"windows","target":"{{Id(70)}}",
             "columns":["code","label"],"rows":[{"id":"{{Id(81)}}","values":["net30","Thirty"]},{"id":"{{Id(82)}}","values":["day2","Two days"]}]}
            """);

        var rules = (await r.Store.ValidateAsync(new ValidationScope(), Ct)).Diagnostics.Where(d => d.Rule == "MQ7119").ToList();
        var shared = Assert.Single(rules);
        Assert.Equal(Id(73), shared.ElementId); // the later type by name, on its binding
        Assert.Contains("put 1 row(s) of the same key (code) in table 'terms', the first \"net30\"", shared.Message, StringComparison.Ordinal);

        // With the kind in the key the two types' rows no longer meet.
        await RewriteAsync(r, Id(10), $$"""
            {"kind":"table","id":"{{Id(10)}}","name":"terms","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{Id(11)}}","name":"code","type":"string","length":10,"nullable":false},
              {"id":"{{Id(12)}}","name":"name","type":"string","length":40,"nullable":false},
              {"id":"{{Id(13)}}","name":"kind","type":"string","length":10,"nullable":false}],
             "primaryKey":{"columns":["{{Id(11)}}","{{Id(13)}}"]},
             "foreignKeys":[{"id":"{{Id(14)}}","name":"fk_terms_kind","columns":["{{Id(13)}}"],"referencesTable":"{{Id(1)}}"}]}
            """);
        Assert.DoesNotContain((await r.Store.ValidateAsync(new ValidationScope(), Ct)).Diagnostics, d => d.Rule == "MQ7119");
    }
}
