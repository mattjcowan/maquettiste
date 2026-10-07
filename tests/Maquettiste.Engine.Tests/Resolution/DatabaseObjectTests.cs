using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Planning;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Routines, database types and SQL objects (added 2026-10-01, erratum E40): resolved like views, a column typed by a database type
/// through its <c>nativeType</c>, the database view's lists, the validation rules MQ4017 to MQ4020, the <c>each routine</c>,
/// <c>each database type</c> and <c>each sql object</c> scopes, and the schema diff of the three kinds.
/// </summary>
public sealed class DatabaseObjectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImmutableDictionary<string, string> Sql(string dialect, string text) => ImmutableDictionary<string, string>.Empty.Add(dialect, text);

    /// <summary>
    /// A PostgreSQL database <c>main</c> (schema <c>crm</c>) and a SQLite database <c>local</c>, each with a domain
    /// <c>email_address</c> that the customer's email column uses (by id on main, by name on local), a function <c>customer_count</c>
    /// taking the domain and a filter, a composite <c>address</c>, and a trigger that depends on the function.
    /// </summary>
    private static ModelBuilder Build(out Ids ids, Func<Routine, Ids, Routine>? changeRoutine = null, Func<DatabaseType, DatabaseType>? changeType = null,
        bool withTrigger = true)
    {
        var b = new ModelBuilder(seed: 91);
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("email", "string", a => a.Length(254));
        var main = b.Database("main", Dialect.PostgreSql);
        var crm = main.Schema("crm");
        var local = b.Database("local", Dialect.Sqlite);
        ids = new Ids(main.Id, local.Id, b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId());
        var domain = new DatabaseType
        {
            Id = ids.MainType, Name = "email_address", Database = main.Id, Schema = crm, TypeKind = DatabaseTypeKind.Domain, Base = "string", Length = 254,
            Check = "VALUE LIKE '%@%'", Tags = ["contact"],
        };
        b.Add(changeType?.Invoke(domain) ?? domain);
        b.Add(new DatabaseType
        {
            Id = ids.Address, Name = "address", Database = main.Id, TypeKind = DatabaseTypeKind.Composite,
            Fields = [new DatabaseTypeField { Name = "street", Type = "string", Length = 200 }, new DatabaseTypeField { Name = "contact", Type = ids.MainType }],
        });
        b.Add(new DatabaseType { Id = ids.LocalType, Name = "email_address", Database = local.Id, TypeKind = DatabaseTypeKind.Domain, Base = "string", Length = 254 });
        b.Add(new Table
        {
            Id = b.NewId(), Database = main.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = customer.AttrId("email"), NativeType = ids.MainType }],
        });
        b.Add(new Table
        {
            Id = b.NewId(), Database = local.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = customer.AttrId("email"), NativeType = "email_address" }],
        });
        var routine = new Routine
        {
            Id = ids.Routine, Name = "customer_count", Database = main.Id, Schema = crm,
            Parameters =
            [
                new RoutineParameter { Name = "domain_part", Type = ids.MainType },
                new RoutineParameter { Name = "since", Type = "date", Default = "NULL" },
                new RoutineParameter { Name = "total", Type = "int64", Mode = ParameterMode.Out },
            ],
            Returns = new RoutineReturns { Table = [new RoutineColumn { Name = "n", Type = "int64", Nullable = false }] },
            Body = Sql("postgresql", "select count(*) from crm.customers"),
            Stereotypes = [],
        };
        b.Add(changeRoutine?.Invoke(routine, ids) ?? routine);
        if (withTrigger)
        {
            b.Add(new SqlObject
            {
                Id = ids.Trigger, Name = "customers_audit", Database = main.Id, ObjectKind = "trigger", DependsOn = [ids.Routine],
                Body = Sql("*", "create trigger customers_audit after insert on crm.customers execute function crm.customer_count();"),
            });
        }

        b.Add(new SqlObject { Id = ids.Extension, Name = "citext", Database = main.Id, ObjectKind = "extension", Phase = SqlObjectPhase.Before, Body = Sql("postgresql", "create extension if not exists citext;") });
        return b;
    }

    private sealed record Ids(string Main, string Local, string MainType, string Address, string LocalType, string Routine, string Trigger, string Extension);

    [Fact]
    public void Routines_types_and_objects_resolve_with_native_types_and_dependencies()
    {
        var model = ResolutionKit.Resolve(Build(out var ids));
        var main = model.Db("main");

        var domain = main.Types.Single(t => t.Name == "email_address");
        Assert.Equal("domain", domain.TypeKind);
        Assert.Equal("varchar(254)", domain.BaseNativeType);
        Assert.True(domain.IsCreated);
        Assert.Equal("crm.email_address", domain.NativeName);
        Assert.Equal(["contact"], domain.Tags);
        var address = main.Types.Single(t => t.Name == "address");
        Assert.Equal([domain], address.DependsOn);
        Assert.Same(domain, address.Fields[1].DbType);
        Assert.Equal("crm.email_address", address.Fields[1].NativeType);
        Assert.Equal("varchar(200)", address.Fields[0].NativeType);

        var routine = Assert.Single(main.Routines);
        Assert.Equal("crm", routine.Schema);
        Assert.Equal("function", routine.RoutineKind);
        Assert.Equal("plpgsql", routine.Language);
        Assert.True(routine.HasBody);
        Assert.Equal(["crm.email_address", "date", "bigint"], routine.Parameters.Select(p => p.NativeType));
        Assert.Same(domain, routine.Parameters[0].DbType);
        Assert.Null(routine.Parameters[0].Type);
        Assert.Equal(["in", "in", "out"], routine.Parameters.Select(p => p.Mode));
        Assert.Equal("bigint", Assert.Single(routine.Returns!.Table!).NativeType);
        Assert.Contains("e:" + ids.MainType, routine.Dependencies);

        var trigger = main.Objects.Single(o => o.Name == "customers_audit");
        Assert.Equal("after", trigger.Phase);
        Assert.Equal([routine], trigger.DependsOn);
        Assert.Equal("before", main.Objects.Single(o => o.Name == "citext").Phase);

        // The column names the type by id on PostgreSQL and by name on SQLite, where the domain is stored as its base.
        var email = main.Tables.Single(t => t.Name == "customers").Columns.Single(c => c.Name == "email");
        Assert.Same(domain, email.DbType);
        Assert.Equal("crm.email_address", email.NativeType);
        var local = model.Db("local");
        var localType = Assert.Single(local.Types);
        Assert.False(localType.IsCreated);
        Assert.Equal("text", localType.NativeName);
        var localEmail = local.Tables.Single(t => t.Name == "customers").Columns.Single(c => c.Name == "email");
        Assert.Same(localType, localEmail.DbType);
        Assert.Equal("text", localEmail.NativeType);

        // The schema lists them under their schema, and the database view projects every one.
        Assert.Equal([routine], main.Schemas.Single(s => s.Name == "crm").Routines);
        var view = DatabaseViews.From(main);
        Assert.Equal(["customer_count"], view.Routines.Select(r => r.Name));
        Assert.Equal(ids.MainType, view.Routines[0].Parameters[0].DbTypeId);
        Assert.Equal(["email_address", "address"], view.Types.Select(t => t.Name)); // by schema (crm, public), then name
        Assert.Equal([ids.Routine], view.Objects.Single(o => o.Name == "customers_audit").DependsOn);
        Assert.Equal(ids.MainType, view.Tables.Single(t => t.Name == "customers").Columns.Single(c => c.Name == "email").DbTypeId);
    }

    [Fact]
    public async Task A_clean_model_has_no_findings_and_each_rule_reports_its_problem()
    {
        var clean = await Validate(Build(out _).Build());
        Assert.DoesNotContain(clean.Diagnostics, d => d.Rule.StartsWith("MQ4", StringComparison.Ordinal) || d.Rule.StartsWith("MQ2", StringComparison.Ordinal));

        // MQ4017: no body for PostgreSQL; MQ4018: a parameter typed by an unknown type and by another database's type.
        var report = await Validate(Build(out var ids, (r, x) => r with
        {
            Body = Sql("sqlserver", "select 1"),
            Parameters = [new RoutineParameter { Name = "a", Type = "money" }, new RoutineParameter { Name = "b", Type = x.LocalType }],
        }).Build());
        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ4017" && d.Severity == DiagnosticSeverity.Warning && d.ElementId == ids.Routine);
        Assert.Equal(2, report.Diagnostics.Count(d => d.Rule == "MQ4018" && d.ElementId == ids.Routine));

        // MQ4017 for a domain without a base or definition.
        var incomplete = await Validate(Build(out ids, changeType: t => t with { Base = null }).Build());
        Assert.Contains(incomplete.Diagnostics, d => d.Rule == "MQ4017" && d.ElementId == ids.MainType);

        // MQ4020: the trigger depends on the routine and the routine on the trigger.
        var cycle = await Validate(Build(out ids, (r, x) => r with { DependsOn = [x.Trigger] }).Build());
        Assert.Equal(new[] { ids.Routine, ids.Trigger }.Order(StringComparer.Ordinal),
            cycle.Diagnostics.Where(d => d.Rule == "MQ4020").Select(d => d.ElementId!).Order(StringComparer.Ordinal));

        // MQ4019: a dependsOn entry of another database.
        var other = await Validate(Build(out ids, (r, x) => r with { DependsOn = [x.LocalType] }).Build());
        Assert.Contains(other.Diagnostics, d => d.Rule == "MQ4019" && d.ElementId == ids.Routine);
    }

    [Fact]
    public async Task A_column_that_names_a_database_type_is_not_an_unknown_native_type_but_another_databases_is_an_error()
    {
        var b = new ModelBuilder(seed: 92);
        var db = b.Database("main", Dialect.PostgreSql);
        var other = b.Database("other", Dialect.PostgreSql);
        var mine = b.NewId();
        var theirs = b.NewId();
        b.Add(new DatabaseType { Id = mine, Name = "money_amount", Database = db.Id, TypeKind = DatabaseTypeKind.Domain, Base = "decimal" });
        b.Add(new DatabaseType { Id = theirs, Name = "other_amount", Database = other.Id, TypeKind = DatabaseTypeKind.Domain, Base = "decimal" });
        var table = b.NewId();
        b.Add(new Table
        {
            Id = table, Name = "ledger", Database = db.Id,
            Columns =
            [
                new Column { Id = b.NewId(), Name = "a", Type = "decimal", NativeType = "money_amount" },
                new Column { Id = b.NewId(), Name = "b", Type = "decimal", NativeType = theirs },
                new Column { Id = b.NewId(), Name = "c", Type = "decimal", NativeType = "not_a_type" },
            ],
        });

        var report = await Validate(b.Build());

        Assert.Equal(["/columns/2/nativeType"], report.Diagnostics.Where(d => d.Rule == "MQ4006").Select(d => d.JsonPointer));
        Assert.Equal(["/columns/1/nativeType"], report.Diagnostics.Where(d => d.Rule == "MQ4019").Select(d => d.JsonPointer));
    }

    [Fact]
    public async Task Duplicate_names_of_one_kind_in_a_schema_are_MQ3001()
    {
        var b = Build(out var ids);
        b.Add(new Routine { Id = b.NewId(), Name = "Customer_Count", Database = ids.Main, Schema = null, Body = Sql("*", "select 1") });
        b.Add(new Routine { Id = b.NewId(), Name = "customer_count", Database = ids.Main, Schema = null, Body = Sql("*", "select 1") });

        var report = await Validate(b.Build());

        Assert.Single(report.Diagnostics, d => d.Rule == "MQ3001");
    }

    [Fact]
    public async Task Each_routine_database_type_and_sql_object_plan_one_unit_per_object()
    {
        var model = ResolutionKit.Resolve(Build(out _));
        var pack = PlanningKit.Pack("p",
        [
            PlanningKit.Unit("routine", "each routine"),
            PlanningKit.Unit("type", "each database type"),
            PlanningKit.Unit("object", "each sql object"),
            PlanningKit.Unit("local-type", "each database type", new UnitWhere { Database = "local" }),
            PlanningKit.Unit("tagged", "each database type", new UnitWhere { Tags = ["contact"] }),
        ]);

        var plan = await new UnitPlanner(PlanningKit.Options).PlanAsync(model, new PackSet([pack], []), new ScriptSandboxFactory(), null, Ct);

        Assert.Empty(plan.Diagnostics);
        IReadOnlyList<string> Names(string unit) => [.. plan.Units.Where(u => u.Unit.Id == unit).Select(u => GenerationService.NameOf(u.Element!)!).Order(StringComparer.Ordinal)];
        Assert.Equal(["crm.customer_count (main)"], Names("routine"));
        Assert.Equal(["address (main)", "crm.email_address (main)", "email_address (local)"], Names("type"));
        Assert.Equal(["citext (main)", "customers_audit (main)"], Names("object"));
        Assert.Equal(["email_address (local)"], Names("local-type"));
        Assert.Equal(["crm.email_address (main)"], Names("tagged"));
    }

    [Fact]
    public void A_foreign_key_column_follows_the_database_type_of_the_key_it_references()
    {
        var b = new ModelBuilder(seed: 92);
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("name", "string");
        var order = b.Entity("Order").Key("id", "uuid");
        var places = b.Relation("places", customer, order, fromMax: MaxCardinality.One, fromMin: 1, fromRole: "customer", toRole: "order");
        var main = b.Database("main", Dialect.PostgreSql);
        var typeId = b.NewId();
        b.Add(new DatabaseType { Id = typeId, Name = "customer_key", Database = main.Id, TypeKind = DatabaseTypeKind.Domain, Base = "uuid" });
        var keyTable = b.NewId();
        b.Add(new Table
        {
            Id = keyTable, Database = main.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = customer.AttrId("id"), NativeType = typeId }],
        });
        var db = ResolutionKit.Resolve(b).Db("main");

        var domain = Assert.Single(db.Types);
        var key = db.Table("customers").Column("id");
        Assert.Same(domain, key.DbType);
        var fk = db.Table("orders").Column("customer_id");
        Assert.Same(domain, fk.DbType);
        Assert.Equal(domain.NativeName, fk.NativeType);
        Assert.Contains("e:" + typeId, db.Table("orders").Dependencies);

        // An overlay that pins the foreign key column's own type keeps the physical side free (MQ4005 compares the two).
        b.Add(new Table
        {
            Id = b.NewId(), Database = main.Id, Origin = TableOrigin.Synthesized, Entity = order.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = places.EndIds[0] + "." + customer.AttrId("id"), NativeType = "uuid" }],
        });
        var pinned = ResolutionKit.Resolve(b).Db("main").Table("orders").Column("customer_id");
        Assert.Null(pinned.DbType);
        Assert.Equal("uuid", pinned.NativeType);
    }

    [Fact]
    public void The_schema_diff_reports_added_changed_and_dropped_definitions_with_their_kinds()
    {
        var differ = new SchemaDiffer();
        var before = differ.Capture(ResolutionKit.Resolve(Build(out _)).Db("main"), 1);
        Assert.Equal(["customer_count"], before.Routines.Select(r => r.Name));
        Assert.Equal("function", before.Routines[0].Kind);
        Assert.Equal(["address", "email_address"], before.Types.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, before.Objects.Count);

        var first = differ.Diff(null, ResolutionKit.Resolve(Build(out _)).Db("main"));
        Assert.All(first.Routines.Concat(first.Types).Concat(first.Objects), c => Assert.Equal(ChangeKind.Added, c.Kind));

        var changed = ResolutionKit.Resolve(Build(out var ids,
            (r, _) => r with { RoutineKind = RoutineKind.Procedure, Returns = null },
            t => t with { Name = "contact_email" },
            withTrigger: false)).Db("main");
        var diff = differ.Diff(before, changed);

        Assert.False(diff.IsEmpty);
        var routine = Assert.Single(diff.Routines);
        Assert.Equal((ChangeKind.Altered, "function", "procedure"), (routine.Kind, routine.OldKind, routine.NewKind));
        Assert.Contains(routine.Changes, c => c.Property == "definition");
        var type = diff.Types.Single(t => t.Key == ids.MainType);
        Assert.Equal((ChangeKind.Renamed, "email_address", "contact_email", "crm"), (type.Kind, type.OldName, type.NewName, type.OldSchema));
        var dropped = Assert.Single(diff.Objects);
        Assert.Equal((ChangeKind.Dropped, "trigger", "customers_audit"), (dropped.Kind, dropped.OldKind, dropped.OldName));

        var same = differ.Diff(differ.Capture(changed, 2), changed);
        Assert.True(same.IsEmpty);
    }

    [Fact]
    public async Task A_routine_resolves_its_volatility_and_settings_and_the_rules_check_them()
    {
        var plain = ResolutionKit.Resolve(Build(out _)).Db("main");
        Assert.Equal(("volatile", 0), (plain.Routines[0].Volatility, plain.Routines[0].Settings.Count));
        var deterministic = ResolutionKit.Resolve(Build(out _, (r, _) => r with { Deterministic = true })).Db("main");
        Assert.Equal("immutable", deterministic.Routines[0].Volatility);
        // A routine without them keeps the definition text it had: deterministic implies immutable, so nothing is added.
        var differ = new SchemaDiffer();
        Assert.DoesNotContain("volatility", differ.Capture(deterministic, 1).Routines[0].Definition, StringComparison.Ordinal);

        var pinned = ResolutionKit.Resolve(Build(out _, (r, _) => r with
        {
            Volatility = RoutineVolatility.Stable, Security = RoutineSecurity.Definer,
            Settings = new Dictionary<string, string> { ["work_mem"] = "'64MB'", ["search_path"] = "crm, pg_temp" },
        })).Db("main");
        var routine = pinned.Routines[0];
        Assert.Equal("stable", routine.Volatility);
        Assert.Equal(["search_path = crm, pg_temp", "work_mem = '64MB'"], routine.Settings.Select(x => x.Name + " = " + x.Value));
        var definition = differ.Capture(pinned, 1).Routines[0].Definition;
        Assert.Contains("volatility: stable\n", definition, StringComparison.Ordinal);
        Assert.Contains("set: search_path = crm, pg_temp\n", definition, StringComparison.Ordinal);

        async Task<string[]> Rules(Func<Routine, Ids, Routine> change) =>
            [.. (await Validate(Build(out _, change).Build())).Diagnostics.Where(d => d.Rule is "MQ4062" or "MQ4063").Select(d => d.Rule + " " + d.JsonPointer)];
        Assert.Equal(["MQ4062 /volatility"], await Rules((r, _) => r with { Deterministic = true, Volatility = RoutineVolatility.Stable }));
        Assert.Equal(["MQ4062 /volatility"], await Rules((r, _) => r with { RoutineKind = RoutineKind.Procedure, Returns = null, Volatility = RoutineVolatility.Stable }));
        Assert.Equal(["MQ4063 /security"], await Rules((r, _) => r with { Security = RoutineSecurity.Definer }));
        Assert.Empty(await Rules((r, _) => r with { Security = RoutineSecurity.Definer, Settings = new Dictionary<string, string> { ["search_path"] = "crm, pg_temp" } }));
    }

    [Fact]
    public async Task A_routine_volatility_or_setting_on_SQL_Server_is_left_out_with_a_warning()
    {
        var b = new ModelBuilder(seed: 92);
        var ms = b.Database("ms", Dialect.SqlServer);
        b.Add(new Routine
        {
            Id = b.NewId(), Name = "f", Database = ms.Id, Returns = new RoutineReturns { Type = "int32" }, Body = Sql("*", "RETURN 1"),
            Deterministic = true, Volatility = RoutineVolatility.Immutable, Settings = new Dictionary<string, string> { ["search_path"] = "dbo" },
        });
        b.Add(new Routine { Id = b.NewId(), Name = "g", Database = ms.Id, Returns = new RoutineReturns { Type = "int32" }, Body = Sql("*", "RETURN 1"), Deterministic = true });

        var found = (await Validate(b.Build())).Diagnostics.Where(d => d.Rule == "MQ4056").Select(d => d.JsonPointer).Order(StringComparer.Ordinal);
        Assert.Equal(["/settings", "/volatility"], found);
    }

    private static Task<ValidationReport> Validate(ModelSnapshot model) =>
        ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);
}
