using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Validation of the members a stereotype contributes and of a designed table's links to them (next-phase-2-design.md section 3.1,
/// D8): MQ2024 to MQ2026 on the stereotype, MQ2027, MQ2028 and MQ4078 on a designed table's <c>redefines</c> and <c>excludes</c>, MQ4003
/// between own and inherited column names, and MQ4008 with the keyed form for a bare member id.
/// </summary>
public sealed class StereotypeMemberRuleTests
{
    private static readonly string[] Rules = ["MQ2001", "MQ2024", "MQ2025", "MQ2026", "MQ2027", "MQ2028", "MQ4003", "MQ4008", "MQ4078"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The audited stereotype of the design's example (created_at with a default, updated_at, an index on created_at) and a designed
    /// table notes that carries it, each edited by the test.
    /// </summary>
    private sealed record Fixture(ModelSnapshot Model, string Audited, string Notes, string Created, string Updated, string Index, string Body);

    private static Fixture Build(Func<Stereotype, Stereotype>? editAudited = null, Func<Table, Table>? editNotes = null)
    {
        var b = new ModelBuilder(seed: 71);
        var main = b.Database("main", Dialect.PostgreSql);
        string created = b.NewId(), updated = b.NewId(), index = b.NewId();
        var audited = new Stereotype
        {
            Id = b.NewId(), Key = "audited", Name = "Audited", AppliesTo = ["entity", "table", "column"],
            Columns =
            [
                new Column { Id = created, Name = "created_at", Type = "datetimeoffset", Nullable = false, DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_TIMESTAMP" } },
                new Column { Id = updated, Name = "updated_at", Type = "datetimeoffset" },
            ],
            Indexes = [new TableIndex { Id = index, Columns = [new IndexColumn { Column = created }] }],
        };
        b.Add((editAudited ?? (s => s))(audited));
        var notes = b.Table("notes", main).Column("id", "uuid", nullable: false).Column("body", "text").PrimaryKey("id").Stereotype("audited")
            .Edit(editNotes ?? (t => t));
        var model = b.Build();
        return new Fixture(model, audited.Id, notes.Id, created, updated, index, notes.ColumnId("body"));
    }

    // ---- MQ2024 ----

    [Fact]
    public async Task Members_that_do_not_fit_are_MQ2024()
    {
        var f = Build(s => s with
        {
            AppliesTo = ["entity", "column"],
            Columns = [s.Columns[0] with { Attribute = s.Columns[1].Id }, s.Columns[1] with { Sequence = "01K6SMR0000000000000000008" }],
            PrimaryKey = new PrimaryKey { Id = "01K6SMR0000000000000000001", Columns = [s.Columns[0].Id], Name = "pk_audited" },
            Uniques = [new UniqueConstraint { Id = "01K6SMR0000000000000000002", Columns = [s.Columns[1].Id], Name = "uq_audited" }],
            Checks = [new CheckConstraint { Id = "01K6SMR0000000000000000003", Name = "ck_audited", Expression = new Dictionary<string, string> { ["*"] = "updated_at >= created_at" } }],
            Indexes = [s.Indexes[0] with { Name = "ix_audited" }],
        });

        var found = Stereotype(await Findings(f.Model), f);

        Assert.Equal(
            [
                ("MQ2024", "/appliesTo"),
                ("MQ2024", "/checks/0/name"),
                ("MQ2024", "/columns/0/attribute"),
                ("MQ2024", "/columns/1/sequence"),
                ("MQ2024", "/indexes/0/name"),
                ("MQ2024", "/primaryKey/name"),
                ("MQ2024", "/uniques/0/name"),
            ],
            found.Where(d => d.Rule == "MQ2024").Select(d => (d.Rule, d.JsonPointer!)).Order());
        Assert.All(found.Where(d => d.Rule == "MQ2024"), d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("does not apply to tables, so its members reach no table", found.First(d => d.JsonPointer == "/appliesTo").Message, StringComparison.Ordinal);
        Assert.Contains("every table that carries it would share", found.First(d => d.JsonPointer == "/primaryKey/name").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_that_fit_are_clean()
    {
        var f = Build(s => s with
        {
            PrimaryKey = new PrimaryKey { Id = "01K6SMR0000000000000000001", Columns = [s.Columns[0].Id] },
            Uniques = [new UniqueConstraint { Id = "01K6SMR0000000000000000002", Columns = [s.Columns[1].Id] }],
            Checks = [new CheckConstraint { Id = "01K6SMR0000000000000000003", Column = s.Columns[1].Id, Expression = new Dictionary<string, string> { ["*"] = "updated_at >= created_at" } }],
        }, t => t with { PrimaryKey = null });

        Assert.Empty(await Findings(f.Model));
    }

    // ---- MQ2025 ----

    [Fact]
    public async Task A_member_naming_a_column_the_stereotype_does_not_declare_is_MQ2025()
    {
        var f = Build(s => s with
        {
            Uniques = [new UniqueConstraint { Id = "01K6SMR0000000000000000002", Columns = [s.Columns[0].Id, "01K6SMR0000000000000000009"] }],
            Checks = [new CheckConstraint { Id = "01K6SMR0000000000000000003", Column = "01K6SMR0000000000000000009", Expression = new Dictionary<string, string> { ["*"] = "true" } }],
            Indexes = [s.Indexes[0] with { Include = ["01K6SMR0000000000000000009"] }],
        });

        var found = Stereotype(await Findings(f.Model), f);

        Assert.Equal(
            [("MQ2025", "/checks/0/column"), ("MQ2025", "/indexes/0/include/0"), ("MQ2025", "/uniques/0/columns/1")],
            found.Select(d => (d.Rule, d.JsonPointer!)).Order());
        Assert.Contains("names column '01K6SMR0000000000000000009', which the stereotype does not declare", found[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_naming_their_own_columns_are_clean()
    {
        var f = Build(s => s with
        {
            Uniques = [new UniqueConstraint { Id = "01K6SMR0000000000000000002", Columns = [s.Columns[0].Id, s.Columns[1].Id] }],
            Indexes = [s.Indexes[0] with { Include = [s.Columns[1].Id] }],
        });

        Assert.Empty(await Findings(f.Model));
    }

    // ---- MQ2026 ----

    private static StereotypeParameters Parameters(string json, params string[] required) =>
        new() { Properties = JsonDocument.Parse(json).RootElement.Clone(), Required = required };

    private const string ParentTable = """{ "parentTable": { "type": "string", "x-ref": { "kind": "table", "sameDatabase": true } } }""";

    private static Func<Stereotype, Stereotype> WithForeignKeys(StereotypeParameters? parameters, params MemberForeignKey[] keys) => s => s with
    {
        AppliesTo = ["table"],
        Columns = [.. s.Columns, new Column { Id = "01K6SMR00000000000000000C3", Name = "parent_id", Type = "uuid", Nullable = false }],
        ForeignKeys = keys,
        Parameters = parameters,
    };

    private static MemberForeignKey Key(string id, string? table = null, string? parameter = null, params string[] referencesColumns) =>
        new() { Id = id, Columns = ["01K6SMR00000000000000000C3"], ReferencesTable = table, ReferencesParameter = parameter, ReferencesColumns = referencesColumns };

    [Fact]
    public async Task An_invalid_contributed_foreign_key_target_is_MQ2026()
    {
        var f = Build(WithForeignKeys(
            Parameters("""
                { "parentTable": { "type": "string", "x-ref": { "kind": "table", "sameDatabase": true } },
                  "loose": { "type": "string", "x-ref": { "kind": "table" } },
                  "optional": { "type": "string", "x-ref": { "kind": "table", "sameDatabase": true } } }
                """, "parentTable", "loose"),
            Key("01K6SMR00000000000000000F1", "01K6SMR00000000000000000T9", "parentTable"),   // both
            Key("01K6SMR00000000000000000F2"),                                                // neither
            Key("01K6SMR00000000000000000F3", parameter: "missing"),                          // not declared
            Key("01K6SMR00000000000000000F4", parameter: "loose"),                            // not sameDatabase
            Key("01K6SMR00000000000000000F5", parameter: "optional"),                         // not required
            Key("01K6SMR00000000000000000F6", null, "parentTable", "01K6SMR00000000000000000C9"), // columns beside a parameter
            Key("01K6SMR00000000000000000F7", "01K6SMR00000000000000000T9")),                 // a table that does not exist
            t => t with { Properties = Props(("parentTable", t.Id)) });

        var found = Stereotype(await Findings(f.Model), f);

        Assert.Equal(
            [
                ("MQ2026", "/foreignKeys/0/referencesParameter"),
                ("MQ2026", "/foreignKeys/1"),
                ("MQ2026", "/foreignKeys/2/referencesParameter"),
                ("MQ2026", "/foreignKeys/3/referencesParameter"),
                ("MQ2026", "/foreignKeys/4/referencesParameter"),
                ("MQ2026", "/foreignKeys/5/referencesColumns"),
                ("MQ2001", "/foreignKeys/6/referencesTable"),
            ],
            found.Select(d => (d.Rule, d.JsonPointer!)));
        Assert.Contains("sets both referencesTable and referencesParameter", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("has no target", found[1].Message, StringComparison.Ordinal);
        Assert.Contains("which the stereotype's parameters do not declare", found[2].Message, StringComparison.Ordinal);
        Assert.Contains("which is not typed", found[3].Message, StringComparison.Ordinal);
        Assert.Contains("which is not in required", found[4].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parameter_or_fixed_target_is_clean()
    {
        var f = Build(WithForeignKeys(Parameters(ParentTable, "parentTable"),
                Key("01K6SMR00000000000000000F1", parameter: "parentTable")),
            t => t with { Properties = Props(("parentTable", t.Id)) });
        Assert.Empty(await Findings(f.Model));

        // A fixed table, with its referenced columns.
        var b = new ModelBuilder(seed: 72);
        var main = b.Database("main", Dialect.PostgreSql);
        var customers = b.Table("customers", main).Column("id", "uuid", nullable: false).PrimaryKey("id");
        b.Add(new Stereotype
        {
            Id = b.NewId(), Key = "owned", Name = "Owned", AppliesTo = ["table"],
            Columns = [new Column { Id = "01K6SMR00000000000000000C3", Name = "owner_id", Type = "uuid" }],
            ForeignKeys = [Key("01K6SMR00000000000000000F1", customers.Id, null, customers.ColumnId("id"))],
        });
        b.Table("notes", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Stereotype("owned");
        Assert.Empty(await Findings(b.Build()));
    }

    // ---- MQ2027 ----

    [Fact]
    public async Task An_invalid_redefinition_is_MQ2027()
    {
        var f = Build();
        f = Build(editNotes: t => t with
        {
            Redefines =
            [
                new TableRedefinition { Member = "01K6SMR0000000000000000009", Nullable = true },
                new TableRedefinition { Member = f.Created, Nullable = true },
                new TableRedefinition { Member = f.Created, Comment = "twice" },
                new TableRedefinition { Member = f.Updated },
                new TableRedefinition { Member = f.Index, Nullable = true },
            ],
        });

        // An id that names nothing is also MQ2001, from the reference check.
        var found = Table(await Findings(f.Model), f).Where(d => d.Rule == "MQ2027").ToList();

        Assert.Equal(
            [
                ("MQ2027", "/redefines/0/member"),
                ("MQ2027", "/redefines/2/member"),
                ("MQ2027", "/redefines/3/member"),
                ("MQ2027", "/redefines/4/nullable"),
            ],
            found.Select(d => (d.Rule, d.JsonPointer!)));
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("which is not a member the table inherits", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("is redefined twice", found[1].Message, StringComparison.Ordinal);
        Assert.Contains("states no facet", found[2].Message, StringComparison.Ordinal);
        Assert.Contains($"Redefinition of index '{f.Index}' states 'nullable'", found[3].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redefinition_of_an_inherited_facet_is_clean()
    {
        var f = Build();
        f = Build(editNotes: t => t with
        {
            Redefines = [new TableRedefinition { Member = f.Created, Nullable = true }, new TableRedefinition { Member = f.Index, Where = "created_at > now()" }],
        });

        Assert.Empty(await Findings(f.Model));
    }

    // ---- MQ2028 ----

    [Fact]
    public async Task An_invalid_exclusion_is_MQ2028()
    {
        var f = Build();
        f = Build(editNotes: t => t with
        {
            Redefines = [new TableRedefinition { Member = f.Updated, Comment = "kept" }],
            Excludes = ["01K6SMR0000000000000000009", f.Updated, f.Created],
        });

        var found = Table(await Findings(f.Model), f).Where(d => d.Rule == "MQ2028").ToList();

        Assert.Equal([("MQ2028", "/excludes/0"), ("MQ2028", "/excludes/1"), ("MQ2028", "/excludes/2")], found.Select(d => (d.Rule, d.JsonPointer!)));
        Assert.Contains("which is not a member the table inherits", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("is both excluded and redefined", found[1].Message, StringComparison.Ordinal);
        Assert.Contains($"leaves inherited index '{f.Index}' naming it; exclude that member too", found[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excluding_a_column_with_the_members_that_use_it_is_clean()
    {
        var f = Build();
        f = Build(editNotes: t => t with { Excludes = [f.Created, f.Index] });

        Assert.Empty(await Findings(f.Model));
    }

    // ---- MQ4078 ----

    [Fact]
    public async Task A_redefinition_equal_to_the_inherited_value_is_MQ4078()
    {
        var f = Build();
        f = Build(editNotes: t => t with
        {
            Redefines =
            [
                new TableRedefinition { Member = f.Created, Nullable = false, DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_TIMESTAMP" } },
                new TableRedefinition { Member = f.Updated, Nullable = true, Comment = "changed" },
            ],
        });

        var found = Table(await Findings(f.Model), f);

        Assert.Equal(
            [("MQ4078", "/redefines/0/defaultSql"), ("MQ4078", "/redefines/0/nullable"), ("MQ4078", "/redefines/1/nullable")],
            found.Select(d => (d.Rule, d.JsonPointer!)).Order());
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains("equal to the value it inherits", found[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redefinition_that_changes_its_facets_is_clean()
    {
        var f = Build();
        f = Build(editNotes: t => t with
        {
            Redefines = [new TableRedefinition { Member = f.Created, Nullable = true, DefaultSql = new Dictionary<string, string> { ["*"] = "now()" } }],
        });

        Assert.Empty(await Findings(f.Model));
    }

    // ---- MQ4003 and MQ4008 ----

    [Fact]
    public async Task An_own_column_named_like_an_inherited_one_is_MQ4003_at_the_stereotype_entry()
    {
        var f = Build(editNotes: t => t with { Columns = [.. t.Columns, new Column { Id = "01K6SMR00000000000000000C8", Name = "UPDATED_AT", Type = "datetime" }] });

        var found = Table(await Findings(f.Model), f);

        var d = Assert.Single(found);
        Assert.Equal(("MQ4003", "/stereotypes/0"), (d.Rule, d.JsonPointer!));
        Assert.Contains("Column name 'updated_at' that stereotype 'audited' gives this table is already used by the column at index 2", d.Message, StringComparison.Ordinal);

        // Excluding the member clears it.
        f = Build(editNotes: t => t with { Columns = [.. t.Columns, new Column { Id = "01K6SMR00000000000000000C8", Name = "updated_at", Type = "datetime" }], Excludes = [f.Updated] });
        Assert.Empty(await Findings(f.Model));
    }

    [Fact]
    public async Task A_bare_member_id_in_an_index_is_MQ4008_with_the_keyed_form()
    {
        var f = Build();
        var keyed = f.Notes + "." + f.Created;
        f = Build(editNotes: t => t with
        {
            Indexes =
            [
                new TableIndex { Id = "01K6SMR00000000000000000X2", Columns = [new IndexColumn { Column = f.Created }] },
                new TableIndex { Id = "01K6SMR00000000000000000X3", Columns = [new IndexColumn { Column = keyed }, new IndexColumn { Column = t.Columns[1].Id }] },
            ],
            Uniques = [new UniqueConstraint { Id = "01K6SMR00000000000000000N2", Columns = [keyed, f.Notes + "." + f.Updated] }],
        });

        var found = Table(await Findings(f.Model), f);

        // The keyed ids resolve; the bare member id is told the keyed form.
        var d = Assert.Single(found);
        Assert.Equal(("MQ4008", "01K6SMR00000000000000000X2", "/indexes/0/columns/0/column"), (d.Rule, d.ElementId!, d.JsonPointer!));
        Assert.Contains($"It is the column 'created_at' the table inherits from stereotype 'audited': write '{keyed}'.", d.Message, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static System.Collections.Immutable.ImmutableDictionary<string, JsonElement> Props(params (string Key, string Value)[] values) =>
        System.Collections.Immutable.ImmutableDictionary.CreateRange(StringComparer.Ordinal,
            values.Select(v => new KeyValuePair<string, JsonElement>(v.Key, JsonSerializer.SerializeToElement(v.Value))));

    private static List<Diagnostic> Stereotype(IEnumerable<Diagnostic> found, Fixture f) =>
        [.. found.Where(d => d.ElementId == f.Audited || f.Model.TryGetEntry(d.ElementId!, out var e) && e.OwnerId == f.Audited)];

    private static List<Diagnostic> Table(IEnumerable<Diagnostic> found, Fixture f) =>
        [.. found.Where(d => d.ElementId == f.Notes || f.Model.TryGetEntry(d.ElementId!, out var e) && e.OwnerId == f.Notes)];

    private static async Task<IReadOnlyList<Diagnostic>> Findings(ModelSnapshot model)
    {
        var report = await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5001" or "MQ5004");
        return [.. report.Diagnostics.Where(d => Rules.Contains(d.Rule))];
    }
}
