using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// The drift of one binding, from files (next-phase-2-design.md section 3.3, decision 5, D30): renamed and retyped columns a field maps,
/// added columns nothing names and removed recorded ids, against the provenance a by-name operation recorded. Each case builds the
/// model twice with the same ids: once to record the provenance, once with the table changed.
/// </summary>
public sealed class BindingDriftTests
{
    private sealed record Fixture(ModelSnapshot Model, Entity Entity, EntityBinding Binding, Table Table)
    {
        public BindingDriftReport Drift() => BindingDrift.Of(Model, Entity, Binding);

        public string ColumnId(string name) => Table.Columns.Single(c => c.Name == name).Id;
    }

    /// <summary>What a binding edit may read: the table's column ids by name, before any change, and the entity's id.</summary>
    private sealed record Parts(TableBuilder Notes, EntityBuilder Note);

    /// <summary>
    /// Table <c>notes</c> (id, body, number, kind) bound to entity <c>Note</c>: id, body and number are fields, kind a constant. The
    /// provenance is recorded from the unchanged table, then <paramref name="change"/> edits the table. <paramref name="binding"/> edits
    /// the binding in both builds; <paramref name="provenance"/> false leaves it without one, and <paramref name="recorded"/> records
    /// another provenance than the table's. <paramref name="audited"/> gives the changed table the stereotype <c>audited</c>, whose
    /// column <c>created_at</c> it then inherits.
    /// </summary>
    private static Fixture Build(Func<Table, Table>? change = null, Func<EntityBinding, Parts, EntityBinding>? binding = null, bool provenance = true,
        Func<Fixture, BindingProvenance>? recorded = null, bool audited = false)
    {
        var before = Make(null, binding, null, audited: false);
        var record = provenance
            ? recorded?.Invoke(before) ?? new BindingProvenance { Columns = BindingDrift.ColumnsOf(before.Model, before.Table).ToDictionary(c => c.Id, c => c.Shape) }
            : null;
        return Make(change, binding, record, audited);
    }

    /// <summary>One build; the same seed gives the same ids every time.</summary>
    private static Fixture Make(Func<Table, Table>? change, Func<EntityBinding, Parts, EntityBinding>? edit, BindingProvenance? provenance, bool audited)
    {
        var b = new ModelBuilder(seed: 47);
        var main = b.Database("main", Dialect.PostgreSql);
        var createdAt = new Column { Id = b.NewId(), Name = "created_at", Type = "datetimeoffset", Nullable = false };
        b.Add(new Stereotype { Id = b.NewId(), Key = "audited", Name = "Audited", AppliesTo = ["table"], Columns = [createdAt] });
        var notes = b.Table("notes", main)
            .Column("id", "uuid", nullable: false)
            .Column("body", "string", length: 400)
            .Column("number", "string", nullable: false, length: 32)
            .Column("kind", "string", nullable: false, length: 8)
            .PrimaryKey("id");
        if (change is not null)
            notes.Edit(change);
        if (audited)
            notes.Stereotype("audited");
        b.Table("others", main).Column("id", "uuid", nullable: false).PrimaryKey("id");
        var note = b.Entity("Note").Key("id", "uuid").Attr("body", "string").Attr("number", "string");
        var binding = new EntityBinding
        {
            Id = b.NewId(),
            Database = main.Id,
            Source = notes.Id,
            Fields =
            [
                new BindingField { Attribute = note.AttrId("id"), Column = notes.ColumnId("id") },
                new BindingField { Attribute = note.AttrId("body"), Column = notes.ColumnId("body") },
                new BindingField { Attribute = note.AttrId("number"), Column = notes.ColumnId("number") },
            ],
            Constants = [new BindingConstant { Column = notes.ColumnId("kind"), Value = JsonSerializer.SerializeToElement("note") }],
        };
        if (edit is not null)
            binding = edit(binding, new Parts(notes, note));
        note.Bind(binding with { Provenance = provenance });
        var model = b.Build();
        var entity = model.Get<Entity>(note.Id)!;
        return new Fixture(model, entity, entity.Bindings.Single(), model.Get<Table>(notes.Id)!);
    }

    private static Column NewColumn(string id, string name, string type, bool? nullable = null) =>
        new() { Id = id, Name = name, Type = type, Nullable = nullable };

    [Fact]
    public void A_new_column_nothing_names_is_added()
    {
        var f = Build(t => t with { Columns = [.. t.Columns, NewColumn("01K6DRF0000000000000000001", "due_on", "date")] });

        var drift = f.Drift();

        Assert.Equal(DriftSource.TableFile, drift.Source);
        Assert.True(drift.HasProvenance);
        Assert.False(drift.Stale);
        Assert.Equal([new DriftAddedColumn("01K6DRF0000000000000000001", "due_on", "unaccounted")], drift.Added);
        Assert.Empty(drift.Renamed);
        Assert.Empty(drift.Retyped);
        Assert.Empty(drift.Removed);
        Assert.True(drift.HasDrift);
        Assert.False(Build().Drift().HasDrift);
    }

    [Fact]
    public void A_new_column_with_a_default_is_added_too()
    {
        var f = Build(t => t with
        {
            Columns =
            [
                .. t.Columns,
                NewColumn("01K6DRF0000000000000000001", "created_on", "date", nullable: false) with { DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_DATE" } },
                NewColumn("01K6DRF0000000000000000002", "flag", "bool", nullable: false) with { Default = JsonSerializer.SerializeToElement(false) },
                NewColumn("01K6DRF0000000000000000003", "seq", "int64", nullable: false) with { Generated = ColumnGeneration.Identity },
                NewColumn("01K6DRF0000000000000000004", "total", "decimal") with { Computed = "1 + 1" },
            ],
        });

        var drift = f.Drift();

        Assert.Equal(
            [("created_on", "default"), ("flag", "default"), ("seq", "identity"), ("total", "computed")],
            drift.Added.Select(a => (a.Name, a.Status)));
    }

    [Fact]
    public void A_renamed_mapped_column_is_renamed()
    {
        var f = Build(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "number" ? c with { Name = "invoice_number" } : c)] });

        var drift = f.Drift();

        var renamed = Assert.Single(drift.Renamed);
        Assert.Equal((f.ColumnId("invoice_number"), "invoice_number", f.Entity.Attributes.Single(a => a.Name == "number").Id, "number", 2),
            (renamed.Column, renamed.Name, renamed.Attribute, renamed.AttributeName, renamed.Field));
        Assert.Equal(ColumnShapeHash.TypePartOf(renamed.Recorded), ColumnShapeHash.TypePartOf(renamed.Current));
        Assert.Empty(drift.Retyped);
        Assert.Empty(drift.Added);
        Assert.Empty(drift.Removed);
    }

    [Fact]
    public void A_case_only_rename_is_not_drift()
    {
        var f = Build(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "number" ? c with { Name = "Number" } : c)] });

        Assert.False(f.Drift().HasDrift);
    }

    [Fact]
    public void A_retyped_mapped_column_is_retyped()
    {
        var f = Build(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "number" ? c with { Length = 64 } : c)] });

        var drift = f.Drift();

        var retyped = Assert.Single(drift.Retyped);
        Assert.Equal(("number", "number"), (retyped.Name, retyped.AttributeName));
        Assert.Empty(drift.Renamed);

        // Renamed and retyped at once: the column is in both lists.
        var both = Build(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "number" ? c with { Name = "invoice_no", Nullable = true } : c)] }).Drift();
        Assert.Equal(["invoice_no"], both.Renamed.Select(r => r.Name));
        Assert.Equal(["invoice_no"], both.Retyped.Select(r => r.Name));
    }

    [Fact]
    public void A_field_naming_its_column_by_name_is_followed()
    {
        // The field names its column by physical name, in another case: the resolver finds it loosely, and so does drift.
        static EntityBinding ByName(EntityBinding binding, Parts p) =>
            binding with { Fields = [.. binding.Fields.Select(x => x.Column == p.Notes.ColumnId("number") ? x with { Column = "NUMBER" } : x)] };

        var f = Build(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "number" ? c with { Type = "text", Length = null } : c)] }, ByName);

        var drift = f.Drift();

        var retyped = Assert.Single(drift.Retyped);
        Assert.Equal((f.ColumnId("number"), 2), (retyped.Column, retyped.Field));
        Assert.Empty(drift.Added);
        Assert.Equal(2, BindingDrift.Find(drift.Columns, "number"));
        Assert.Equal(2, BindingDrift.Find(drift.Columns, "Number"));
        Assert.Equal(-1, BindingDrift.Find(drift.Columns, "missing"));
    }

    [Fact]
    public void A_changed_constant_column_is_not_drift()
    {
        var f = Build(t => t with { Columns = [.. t.Columns.Select(c => c.Name == "kind" ? c with { Name = "note_kind", Length = 16 } : c)] });

        Assert.False(f.Drift().HasDrift);
    }

    [Fact]
    public void A_recorded_column_gone_is_removed()
    {
        string? body = null;
        var f = Build(t =>
        {
            body = t.Columns.Single(c => c.Name == "body").Id;
            return t with { Columns = [.. t.Columns.Where(c => c.Name != "body")] };
        });

        var drift = f.Drift();

        Assert.Equal([body!], drift.Removed);
        Assert.Empty(drift.Renamed);
        Assert.Empty(drift.Added);
    }

    [Fact]
    public void Provenance_of_another_table_is_stale()
    {
        var f = Build(
            t => t with { Columns = [.. t.Columns, NewColumn("01K6DRF0000000000000000001", "due_on", "date"), NewColumn("01K6DRF0000000000000000002", "seen_on", "date", nullable: false) with { DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_DATE" } }] },
            recorded: before =>
            {
                var others = before.Model.All<Table>().Single(t => t.Name == "others");
                return new BindingProvenance { Columns = BindingDrift.ColumnsOf(before.Model, others).ToDictionary(c => c.Id, c => c.Shape) };
            });

        var drift = f.Drift();

        Assert.True(drift.HasProvenance);
        Assert.True(drift.Stale);
        Assert.False(drift.Reads);
        Assert.True(drift.HasDrift);
        // Read as having none: only what nothing accounts for, nothing removed.
        Assert.Equal(["due_on"], drift.Added.Select(a => a.Name));
        Assert.Empty(drift.Removed);

        // Provenance on a binding whose source is not a table file (here the entity's own projected table) is stale too; a source
        // that is nothing of the database is the resolver's MQ4044, not drift.
        var projected = Build(binding: (b, p) => b with { Source = p.Note.Id }).Drift();
        Assert.Equal((DriftSource.NotTableFile, true, true), (projected.Source, projected.Stale, projected.HasDrift));
        Assert.Empty(projected.Columns);
        var nothing = Build(binding: (b, _) => b with { Source = b.Database }).Drift();
        Assert.Equal((DriftSource.Unresolved, false, false), (nothing.Source, nothing.Stale, nothing.HasDrift));
    }

    [Fact]
    public void A_binding_without_provenance_reports_only_unaccounted_columns()
    {
        var f = Build(
            t => t with
            {
                Columns =
                [
                    .. t.Columns.Select(c => c.Name == "number" ? c with { Name = "invoice_number" } : c),
                    NewColumn("01K6DRF0000000000000000001", "due_on", "date"),
                    NewColumn("01K6DRF0000000000000000002", "seen_on", "date", nullable: false) with { DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_DATE" } },
                ],
            },
            provenance: false);

        var drift = f.Drift();

        Assert.False(drift.HasProvenance);
        Assert.False(drift.Stale);
        Assert.Equal([new DriftAddedColumn("01K6DRF0000000000000000001", "due_on", "unaccounted")], drift.Added);
        Assert.Empty(drift.Renamed);
        Assert.Empty(drift.Retyped);
        Assert.Empty(drift.Removed);
    }

    [Fact]
    public void Lists_follow_the_source_column_order()
    {
        // Own entries in file order, then inherited members; removed ids in ordinal order.
        string[] gone = [];
        var f = Build(t =>
        {
            gone = [.. t.Columns.Where(c => c.Name is "body" or "id").Select(c => c.Id).Order(StringComparer.Ordinal)];
            return t with
            {
                Columns =
                [
                    NewColumn("01K6DRF0000000000000000009", "zeta", "date"),
                    .. t.Columns.Where(c => c.Name is not ("body" or "id")).Select(c => c.Name == "number" ? c with { Name = "invoice_number", Length = 40 } : c),
                    NewColumn("01K6DRF0000000000000000001", "alpha", "date"),
                ],
            };
        }, audited: true);

        var drift = f.Drift();

        Assert.Equal(["zeta", "invoice_number", "kind", "alpha", "created_at"], drift.Columns.Select(c => c.Name));
        Assert.Equal([false, false, false, false, true], drift.Columns.Select(c => c.Inherited));
        Assert.Equal(["zeta", "alpha", "created_at"], drift.Added.Select(a => a.Name));
        Assert.Equal(drift.Columns[^1].Id, drift.Added[^1].Column);
        Assert.StartsWith(f.Table.Id + ".", drift.Added[^1].Column, StringComparison.Ordinal);
        Assert.Equal(gone, drift.Removed);
        Assert.Equal(["invoice_number"], drift.Renamed.Select(r => r.Name));
        Assert.Equal(["invoice_number"], drift.Retyped.Select(r => r.Name));
    }
}
