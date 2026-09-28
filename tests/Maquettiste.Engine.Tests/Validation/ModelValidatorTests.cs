using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Validator behavior on in-memory models (<see cref="ModelBuilder"/>): rules the schemas already block in files, scope, settings, scripts.</summary>
public sealed class ModelValidatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ValidationReport> Validate(ModelSnapshot model, ValidationScope? scope = null, IScriptSandboxFactory? scripts = null) =>
        ValidationFixture.Validator(scripts: scripts).ValidateAsync(model, scope ?? ValidationScope.All, null, Ct);

    private static ModelBuilder Billing(out EntityBuilder customer, out EntityBuilder invoice)
    {
        var b = new ModelBuilder(seed: 7);
        var billing = b.Package("Billing");
        customer = b.Entity("Customer", billing).Key("id", "uuid", IdentityStrategy.UuidV7).Attr("name", "string", a => a.Length(120).Required());
        invoice = b.Entity("Invoice", billing).Key("id", "uuid").Attr("number", "string", a => a.Length(32).Unique());
        b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "customer", toNavigation: "invoices");
        b.Database("main", Dialect.PostgreSql);
        return b;
    }

    [Fact]
    public async Task A_builder_model_without_problems_is_clean()
    {
        var report = await Validate(Billing(out _, out _).Build());

        Assert.Empty(report.Diagnostics);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public async Task Scalar_base_must_be_builtin_MQ3014()
    {
        var b = new ModelBuilder();
        b.ScalarType("Code", "varchar");

        var d = Assert.Single((await Validate(b.Build())).Diagnostics);
        Assert.Equal(("MQ3014", "/base", DiagnosticSeverity.Error), (d.Rule, d.JsonPointer, d.Severity));
        Assert.NotNull(d.Line);
    }

    [Fact]
    public async Task Names_must_fit_their_kind_MQ3018()
    {
        var b = new ModelBuilder();
        var e = b.Entity("2Bad").Key("id", "uuid").Attr("has space", "string");
        var other = b.Entity("Other").Key("id", "uuid");
        b.Relation("links", e, other, fromRole: "bad-role", toNavigation: "no way");
        b.Add(new Stereotype { Id = b.NewId(), Key = "Not_Kebab", Name = "Weird" });

        var report = await Validate(b.Build());

        var pointers = report.Diagnostics.Where(d => d.Rule == "MQ3018").Select(d => d.JsonPointer).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["/attributes/1/name", "/ends/0/role", "/ends/1/navigation", "/key", "/name"], pointers);
    }

    [Fact]
    public async Task Cardinality_min_outside_0_or_1_is_MQ3010()
    {
        var b = new ModelBuilder();
        var a = b.Entity("A").Key("id", "uuid");
        var c = b.Entity("C").Key("id", "uuid");
        b.Relation("r", a, c).WithEnd(0, end => end with { Min = 2 });

        var d = Assert.Single((await Validate(b.Build())).Diagnostics);
        Assert.Equal(("MQ3010", "/ends/0/min"), (d.Rule, d.JsonPointer));
    }

    [Fact]
    public async Task Schemas_and_sequences_belong_to_the_same_database_MQ2002()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var other = b.Database("other", Dialect.PostgreSql);
        var foreignSchema = other.Schema("reporting");
        var sequenceId = b.NewId();
        b.Add(new Sequence { Id = sequenceId, Name = "other_seq", Database = other.Id });
        b.Add(new Table
        {
            Id = b.NewId(), Name = "t", Database = main.Id, Schema = foreignSchema,
            Columns = [new Column { Id = b.NewId(), Name = "id", Type = "int64", Generated = ColumnGeneration.Sequence, Sequence = sequenceId }],
        });

        var report = await Validate(b.Build());

        Assert.Equal(["/columns/0/sequence", "/schema"], report.Diagnostics.Where(d => d.Rule == "MQ2002").Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Undeclared_tag_is_info_when_the_vocabulary_is_not_strict()
    {
        var b = new ModelBuilder().Tags(false, "billing");
        b.Entity("A").Key("id", "uuid").Tag("billing").Tag("adhoc");

        var d = Assert.Single((await Validate(b.Build())).Diagnostics);
        Assert.Equal(("MQ2006", DiagnosticSeverity.Info), (d.Rule, d.Severity));
    }

    [Fact]
    public async Task Settings_change_severity_and_turn_rules_off_but_never_MQ1xxx()
    {
        var b = new ModelBuilder();
        b.Entity("Keyless").Attr("x", "string");
        b.Entity("Other").Attr("y", "string", a => a.Default("hunter2").Sensitive(Sensitivity.Secret));
        b.Settings(s => s with
        {
            Validation = new ValidationSettings
            {
                Rules = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, string>
                {
                    ["MQ3005"] = "warning",
                    ["MQ3017"] = "off",
                    ["MQ1009"] = "off",
                    ["MQ1004"] = "info",
                    ["MQ1003"] = "info",
                }),
            },
        });
        var built = b.Build();
        var loadDiagnostics = new[]
        {
            RuleCatalog.Create("MQ1009", "second vocabulary", null, ".maquettiste/model/vocabularies/tags-2.json", ""),
            RuleCatalog.Create("MQ1004", "duplicate id", null, ".maquettiste/model/entities/x.json", ""),
            RuleCatalog.Create("MQ1003", "not canonical", null, ".maquettiste/model/entities/y.json", ""),
        };
        var model = ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], [], 1, loadDiagnostics);

        var report = await Validate(model);

        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ3017");
        Assert.All(report.Diagnostics.Where(d => d.Rule == "MQ3005"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Equal(2, report.Diagnostics.Count(d => d.Rule == "MQ3005"));
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(report.Diagnostics, d => d.Rule == "MQ1009").Severity);
        // A file left out of the snapshot keeps its error whatever the settings say; an advisory MQ1xxx can change severity.
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(report.Diagnostics, d => d.Rule == "MQ1004").Severity);
        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(report.Diagnostics, d => d.Rule == "MQ1003").Severity);
    }

    [Fact]
    public async Task Scoped_validation_covers_the_elements_and_their_referrers_only()
    {
        var b = new ModelBuilder();
        var target = b.Entity("Target").Key("id", "uuid").Attr("secret", "string", a => a.Default("hunter2"));
        var referrer = b.Entity("Referrer").Attr("x", "string");        // MQ3005, and references Target through the relation
        b.Relation("points at", referrer, target);
        b.Entity("Bystander").Attr("y", "string");                      // MQ3005, unrelated
        var model = b.Build();
        var targetPath = model.GetDocument(target.Id)!.Path;
        var referrerPath = model.GetDocument(referrer.Id)!.Path;

        var alone = await Validate(model, new ValidationScope([target.Id], IncludeReferrers: false));
        Assert.Equal([targetPath], alone.Diagnostics.Select(d => d.FilePath).Distinct());

        // The relation file references Target and is revalidated (it has no findings); the Referrer entity file does not.
        var withReferrers = await Validate(model, new ValidationScope([target.Id]));
        Assert.Equal([targetPath], withReferrers.Diagnostics.Select(d => d.FilePath).Distinct());

        var bySubElement = await Validate(model, new ValidationScope([target.AttrId("secret")], IncludeReferrers: false));
        Assert.Equal([targetPath], bySubElement.Diagnostics.Select(d => d.FilePath).Distinct());

        var whole = await Validate(model);
        Assert.Contains(whole.Diagnostics, d => d.FilePath == referrerPath);
        Assert.Contains(whole.Diagnostics, d => d.FilePath!.EndsWith("bystander.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scoped_validation_of_a_referenced_element_revalidates_its_referrers()
    {
        var b = new ModelBuilder();
        var target = b.Entity("Target").Key("id", "uuid");
        var referrer = b.Entity("Referrer").Key("id", "uuid").Attr("target", "string");
        b.Relation("points at", referrer, target, toNavigation: "target");   // MQ3009 on the relation file: Referrer.target exists
        var model = b.Build();

        var report = await Validate(model, new ValidationScope([target.Id]));

        var d = Assert.Single(report.Diagnostics);
        Assert.Equal("MQ3009", d.Rule);
        Assert.EndsWith("points-at.json", d.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_diagnostics_are_included_and_scoped()
    {
        var built = Billing(out var customer, out _).Build();
        var customerPath = built.GetDocument(customer.Id)!.Path;
        var load = new[]
        {
            RuleCatalog.Create("MQ1003", "not canonical", customer.Id, customerPath, ""),
            RuleCatalog.Create("MQ1001", "invalid JSON", null, ".maquettiste/model/entities/broken.json", null),
        };
        var model = ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], [], 1, load);

        var whole = await Validate(model);
        Assert.Equal(["MQ1001", "MQ1003"], whole.Diagnostics.Select(d => d.Rule).Order(StringComparer.Ordinal));
        var positioned = Assert.Single(whole.Diagnostics, d => d.Rule == "MQ1003");
        Assert.Equal((1, 1), (positioned.Line, positioned.Column));

        var scoped = await Validate(model, new ValidationScope([customer.Id], IncludeReferrers: false));
        Assert.Equal("MQ1003", Assert.Single(scoped.Diagnostics).Rule);
    }

    [Fact]
    public async Task Validation_is_cancellable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var model = Billing(out _, out _).Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, cts.Token));
    }

    [Fact]
    public async Task Validation_is_cancellable_during_the_script_rules_and_disposes_the_pool()
    {
        var built = Billing(out _, out _).Build();
        var model = ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], [new ScriptSource(".maquettiste/extensions/rules/r.js", "//", "h")], 1);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var scripts = new FakeSandboxFactory(("cancel", _ =>
        {
            Interlocked.Increment(ref calls);
            cts.Cancel();
            return [];
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ValidationFixture.Validator(scripts: scripts, parallelism: 1).ValidateAsync(model, ValidationScope.All, null, cts.Token));
        Assert.Equal(1, calls);
        Assert.Equal((1, 1), (scripts.PoolsCreated, scripts.PoolsDisposed));
    }

    [Fact]
    public async Task Validation_cancelled_after_the_last_file_does_not_return_a_report()
    {
        var b = new ModelBuilder();
        b.Entity("Keyless").Attr("x", "string");   // MQ3005, so the position pass has work
        var model = b.Build();
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnLast(cts, model.Documents.Count);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ValidationFixture.Validator(parallelism: 1).ValidateAsync(model, ValidationScope.All, progress, cts.Token));
    }

    private sealed class CancelOnLast(CancellationTokenSource cts, int total) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value)
        {
            if (value.Done == total)
                cts.Cancel();
        }
    }

    [Fact]
    public async Task A_catastrophic_pattern_neither_throws_nor_depends_on_machine_speed_MQ3019()
    {
        var b = new ModelBuilder();
        b.Entity("A").Key("id", "uuid")
            .Attr("code", "string", a => a.Validation(new AttributeValidation { Pattern = "^(a+)+$" }).Default(new string('a', 64) + "!"))
            .Attr("ok", "string", a => a.Validation(new AttributeValidation { Pattern = "^(a+)+$" }).Default(new string('a', 64)))
            .Attr("pair", "string", a => a.Validation(new AttributeValidation { Pattern = "^(a)\\1$" }).Default("ab"));

        var report = await Validate(b.Build());

        // Linear-time matching: the first default does not match. A backreference cannot run without backtracking, so that
        // default is not checked (and the pattern itself is valid, so no MQ3013).
        var d = Assert.Single(report.Diagnostics);
        Assert.Equal(("MQ3019", "/attributes/1/default"), (d.Rule, d.JsonPointer));
        Assert.Contains("validation.pattern", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ignored_relation_mapping_needs_no_foreign_key_MQ4011()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var order = b.Entity("Order").Key("id", "uuid");
        var places = b.Relation("places", customer, order, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many);
        b.Mapping(db, customer).Table(b.Table("customers", db).Column("id", "uuid").PrimaryKey("id"));
        b.Mapping(db, order).Table(b.Table("orders", db).Column("id", "uuid").PrimaryKey("id"));
        var ignored = b.Mapping(db, places).Ignore();
        Assert.NotNull(ignored);

        Assert.Empty((await Validate(b.Build())).Diagnostics);
    }

    [Fact]
    public async Task Inherited_navigations_collide_with_derived_members_MQ3009()
    {
        var b = new ModelBuilder();
        var root = b.Entity("Root").Key("id", "uuid");
        var derived = b.Entity("Derived").Base(root).Attr("orders", "string");
        var order = b.Entity("Order").Key("id", "uuid");
        b.Relation("has", root, order, fromMax: MaxCardinality.One, toNavigation: "orders");
        var other = b.Entity("Other").Key("id", "uuid");
        b.Relation("links", derived, other, fromMax: MaxCardinality.One, toNavigation: "items");
        b.Relation("links2", root, other, fromMax: MaxCardinality.One, toNavigation: "items");

        var report = await Validate(b.Build());

        var found = report.Diagnostics.Select(d => (d.Rule, File: Path.GetFileName(d.FilePath), d.Message)).ToList();
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f is ("MQ3009", "has.json", _) && f.Message.Contains("attribute 'orders' of derived entity 'Derived'", StringComparison.Ordinal));
        Assert.Contains(found, f => f is ("MQ3009", "links2.json", _) && f.Message.Contains("on derived entity 'Derived'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_column_ids_do_not_throw()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        var columnId = b.NewId();
        var tableId = b.NewId();
        b.Add(new Table
        {
            Id = tableId, Name = "t", Database = db.Id,
            Columns = [new Column { Id = columnId, Name = "a", Type = "int64" }, new Column { Id = columnId, Name = "b", Type = "int64" }],
            PrimaryKey = new PrimaryKey { Columns = [columnId] },
        });
        var fromId = b.NewId();
        b.Add(new Table
        {
            Id = b.NewId(), Name = "u", Database = db.Id,
            Columns = [new Column { Id = fromId, Name = "x", Type = "int64" }, new Column { Id = fromId, Name = "y", Type = "int64" }],
            ForeignKeys = [new ForeignKey { Id = b.NewId(), Columns = [fromId], ReferencesTable = tableId, ReferencesColumns = [columnId] }],
        });

        var report = await Validate(b.Build());

        Assert.False(report.HasErrors);
    }

    [Fact]
    public async Task Progress_is_reported_per_file()
    {
        var model = Billing(out _, out _).Build();
        var updates = new List<ProgressUpdate>();
        var progress = new SynchronousProgress(updates);

        await ValidationFixture.Validator(parallelism: 1).ValidateAsync(model, ValidationScope.All, progress, Ct);

        Assert.Equal(model.Documents.Count, updates.Count);
        Assert.All(updates, u => Assert.Equal(PipelineStage.Validate, u.Stage));
        Assert.Equal(Enumerable.Range(1, updates.Count), updates.Select(u => u.Done));
        Assert.All(updates, u => Assert.Equal(model.Documents.Count, u.Total));
    }

    private sealed class SynchronousProgress(List<ProgressUpdate> updates) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value)
        {
            lock (updates)
                updates.Add(value);
        }
    }

    [Fact]
    public async Task Script_rules_run_per_element_and_the_pool_is_disposed()
    {
        var b = Billing(out _, out _);
        var built = b.Build();
        var model = ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], [new ScriptSource(".maquettiste/extensions/rules/r.js", "//", "h")], 1);
        var seen = new List<string>();
        var scripts = new FakeSandboxFactory(("every", doc =>
        {
            lock (seen)
                seen.Add(doc.Element.Id);
            return [new Diagnostic("custom", DiagnosticSeverity.Info, "seen", null, null, null, null, null)];
        }));

        var report = await Validate(model, scripts: scripts);

        Assert.Equal(model.Documents.Select(d => d.Element.Id).Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        Assert.All(report.Diagnostics, d => Assert.Equal("x/every", d.Rule));
        Assert.Equal((1, 1), (scripts.PoolsCreated, scripts.PoolsDisposed));

        var skipped = await Validate(model, new ValidationScope(IncludeScriptRules: false), scripts);
        Assert.Empty(skipped.Diagnostics);
        Assert.Equal(1, scripts.PoolsCreated);
    }

    [Fact]
    public async Task A_rule_script_that_fails_to_load_is_MQ5002_and_rule_ids_are_not_reported_unknown()
    {
        var b = new ModelBuilder();
        b.Entity("A").Key("id", "uuid").Attr("code", "string", a => a.Validation(new AttributeValidation { Rules = ["checked"] }));
        var built = b.Build();
        var model = ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], [new ScriptSource(".maquettiste/extensions/rules/bad.js", "(", "h")], 1);
        var scripts = new FakeSandboxFactory { CreateFailure = new InvalidOperationException("SyntaxError: Unexpected end of input") };

        var report = await Validate(model, scripts: scripts);

        var d = Assert.Single(report.Diagnostics);
        Assert.Equal(("MQ5002", ".maquettiste/extensions/rules/bad.js"), (d.Rule, d.FilePath));
        Assert.Contains("SyntaxError", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attribute_rule_ids_are_unknown_without_scripts_MQ2007()
    {
        var b = new ModelBuilder();
        b.Entity("A").Key("id", "uuid").Attr("code", "string", a => a.Validation(new AttributeValidation { Rules = ["checked"] }));
        b.ScalarType("Sku", "string").Pattern("^[A-Z]+$");
        b.Add(new ScalarType { Id = b.NewId(), Name = "Checked", Base = "string", Validation = new AttributeValidation { Rules = ["x/checked"] } });

        var report = await Validate(b.Build());

        Assert.Equal(["/attributes/1/validation/rules/0", "/validation/rules/0"], report.Diagnostics.Where(d => d.Rule == "MQ2007").Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Report_counts_and_groups_by_element()
    {
        var b = new ModelBuilder();
        var keyless = b.Entity("Keyless").Attr("x", "string", a => a.Length(5).Default("toolong"));
        var report = await Validate(b.Build());

        Assert.Equal(2, report.Errors);
        var groups = report.ByElement();
        Assert.Equal(2, groups.Count);
        Assert.True(groups.ContainsKey(keyless.Id));
        Assert.True(groups.ContainsKey(keyless.AttrId("x")));
    }

    [Fact]
    public async Task TruncateTo_keeps_counts_and_drops_trailing_diagnostics()
    {
        var b = new ModelBuilder();
        for (var i = 0; i < 40; i++)
            b.Entity("Keyless" + i).Attr("x", "string");
        var report = await Validate(b.Build());
        Assert.Equal(40, report.Errors);

        var truncated = report.TruncateTo(4096);

        Assert.True(truncated.Truncated);
        Assert.Equal(40, truncated.Errors);
        Assert.InRange(truncated.Diagnostics.Count, 1, 39);
        Assert.Equal(report.Diagnostics.Take(truncated.Diagnostics.Count), truncated.Diagnostics);
        Assert.Same(report, report.TruncateTo(10_000_000));
    }

    [Fact]
    public async Task Culture_does_not_change_messages_or_positions()
    {
        var b = new ModelBuilder();
        b.Entity("Iota").Key("id", "uuid").Attr("small", "int16", a => a.Default(-70000)).Attr("rate", "decimal", a => a.Precision(3).Scale(1).Default(12.34));
        var model = b.Build();
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        async Task<string> Run(string culture)
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            return ValidationFixture.ToGolden((await Validate(model)).Diagnostics);
        }

        try
        {
            var invariant = await Run("");
            Assert.Equal(invariant, await Run("tr-TR"));
            Assert.Equal(invariant, await Run("de-DE"));
            Assert.Equal(invariant, await Run("ar-SA"));
            Assert.Contains("MQ3019", invariant, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
