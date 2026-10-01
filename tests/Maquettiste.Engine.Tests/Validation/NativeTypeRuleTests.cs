using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Native types on table columns: MQ4006 (warning) for a plain name the dialect does not know, MQ4016 (info, once per type and
/// database with a column count) for a quoted or qualified name the database defines; quotes and schema prefixes are stripped
/// before the lookup, and the model's reference types and enums, the embedded dialect maps and <c>typeMaps</c> count as known.
/// </summary>
public sealed class NativeTypeRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<Diagnostic>> NativeTypeFindings(ModelSnapshot model, ValidationScope? scope = null) =>
        [.. (await ValidationFixture.Validator().ValidateAsync(model, scope ?? ValidationScope.All, null, Ct)).Diagnostics
            .Where(d => d.Rule is "MQ4006" or "MQ4016")];

    private static ReferenceType ReferenceType(ModelBuilder b, string name) => new()
    {
        Id = b.NewId(),
        Name = name,
        Code = new ReferenceCode { Id = b.NewId() },
        Label = new ReferenceLabel { Id = b.NewId() },
    };

    [Theory]
    [InlineData("\"public\".\"unit_of_measure\"", "unit_of_measure", "public.unit_of_measure", true)]
    [InlineData("\"public\".\"unit_of_measure\"[]", "unit_of_measure", "public.unit_of_measure", true)]
    [InlineData("[dbo].[Money Type]", "money type", "dbo.money type", true)]
    [InlineData("`app`.`Flag`", "flag", "app.flag", true)]
    [InlineData("pg_catalog.int4", "int4", "pg_catalog.int4", true)]
    [InlineData("\"varchar\"(20)", "varchar", "varchar", true)]
    [InlineData("\"odd\"\"name\"", "odd\"name", "odd\"name", true)]
    [InlineData("numeric(10,2)", "numeric", "numeric", false)]
    [InlineData("int4[3]", "int4", "int4", false)]
    [InlineData("timestamp(3) with time zone", "timestamp with time zone", "timestamp with time zone", false)]
    public void Parse_strips_quotes_prefixes_arguments_and_arrays(string nativeType, string baseName, string qualified, bool quotedOrQualified)
    {
        var parsed = DialectInfo.Parse(nativeType);

        Assert.Equal(baseName, parsed.BaseName);
        Assert.Equal(qualified, string.Join('.', parsed.Parts));
        Assert.Equal(quotedOrQualified, parsed.IsQuotedOrQualified);
    }

    [Fact]
    public async Task A_quoted_or_qualified_native_type_of_the_dialect_is_known()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        b.Table("orders", db).Column("id", "int32", nativeType: "pg_catalog.int4").Column("note", "string", nativeType: "\"public\".\"citext\"")
            .Column("code", "string", nativeType: "\"varchar\"(20)");

        Assert.Empty(await NativeTypeFindings(b.Build()));
    }

    [Fact]
    public async Task Reference_types_and_enums_of_the_model_are_known_native_types()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        b.Add(ReferenceType(b, "UnitOfMeasure"));
        b.Enum("OrderStatus").Member("Open").Member("Closed");
        b.Table("orders", db)
            .Column("unit", "string", nativeType: "\"public\".\"unit_of_measure\"")
            .Column("unit_t", "string", nativeType: "unit_of_measure_t")
            .Column("unit_kebab", "string", nativeType: "\"unit-of-measure\"")
            .Column("status", "string", nativeType: "order_status")
            .Column("status_t", "string", nativeType: "\"sales\".\"order_status_t\"");

        Assert.Empty(await NativeTypeFindings(b.Build()));
    }

    [Fact]
    public async Task Type_map_values_of_the_project_are_known_native_types()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        b.Table("paths", db).Column("path", "string", nativeType: "ledger_path");
        Assert.Equal("MQ4006", Assert.Single(await NativeTypeFindings(b.Build())).Rule);

        b.Settings(s => s with { TypeMaps = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["postgresql"] = new Dictionary<string, string> { ["text"] = "ledger_path" } } });
        Assert.Empty(await NativeTypeFindings(b.Build()));
    }

    [Fact]
    public void The_embedded_dialect_maps_extend_the_known_native_types()
    {
        var known = DialectInfo.KnownBaseNames(Dialect.Oracle, new Dictionary<string, string> { ["text"] = "nclob_ext" });
        Assert.Contains("nclob_ext", (IReadOnlySet<string>)known);
        Assert.Contains("varchar2", (IReadOnlySet<string>)known);
        foreach (var dialect in new[] { Dialect.PostgreSql, Dialect.SqlServer, Dialect.MySql, Dialect.Oracle })
        {
            foreach (var value in Engine.Resolution.DialectTypeMaps.Default(dialect).Values)
                Assert.True(DialectInfo.IsKnownNativeType(dialect, value, null), $"{dialect}: {value}");
        }
    }

    /// <summary>The documented built-in types each dialect's fixed list gained on 2026-10-01 (the types a column may name).</summary>
    public static TheoryData<Dialect, string[]> AddedNativeTypes => new()
    {
        {
            Dialect.PostgreSql,
            [
                "pg_lsn", "pg_snapshot", "txid_snapshot", "xid", "xid8", "cid", "tid", "regclass", "regcollation", "regconfig", "regdictionary",
                "regnamespace", "regoper", "regoperator", "regproc", "regprocedure", "regrole", "regtype", "jsonpath", "int4multirange",
                "int8multirange", "nummultirange", "tsmultirange", "tstzmultirange", "datemultirange", "ltree", "lquery", "ltxtquery", "cube",
                "earth", "geometry", "geography",
            ]
        },
        {
            Dialect.SqlServer,
            [
                "vector(3)", "sysname", "integer", "dec(10,2)", "double precision", "character(4)", "character varying(20)", "char varying(20)",
                "binary varying(16)", "national character(4)", "national char(4)", "national character varying(20)", "national char varying(20)",
                "national text",
            ]
        },
        {
            Dialect.MySql,
            [
                "geomcollection", "vector(3)", "uuid", "inet4", "inet6", "nchar(4)", "nvarchar(20)", "national char(4)", "national character(4)",
                "national varchar(20)", "national character varying(20)", "national char varying(20)", "nchar varchar(20)", "character(4)",
                "character varying(20)", "char varying(20)", "long", "long varchar", "long varbinary", "int1", "int2", "int3", "int4", "int8",
                "middleint", "float4", "float8",
            ]
        },
        {
            Dialect.Oracle,
            [
                "vector(3, float32)", "dec(10,2)", "character(4)", "character varying(20)", "char varying(20)", "national character(4)",
                "national char(4)", "national character varying(20)", "national char varying(20)", "nchar varying(20)", "mdsys.sdo_geometry",
                "sdo_topo_geometry", "sdo_georaster", "sys.anydata", "anytype", "anydataset", "uritype", "dburitype", "xdburitype", "httpuritype",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(AddedNativeTypes))]
    public void The_documented_built_in_types_of_each_dialect_are_known(Dialect dialect, string[] nativeTypes)
    {
        foreach (var nativeType in nativeTypes)
            Assert.True(DialectInfo.IsKnownNativeType(dialect, nativeType, null), $"{dialect}: {nativeType}");
    }

    [Fact]
    public async Task A_pg_lsn_column_on_postgresql_draws_nothing()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        b.Table("replication", db).Column("position", "binary", nativeType: "pg_lsn").Column("snapshot", "string", nativeType: "pg_snapshot")
            .Column("owner", "string", nativeType: "regrole").Column("spans", "string", nativeType: "int4multirange");

        Assert.Empty(await NativeTypeFindings(b.Build()));
    }

    [Fact]
    public async Task Native_types_a_custom_type_declares_are_known_for_their_dialect()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        var other = b.Database("reporting", Dialect.SqlServer);
        b.Table("ledger", db).Column("position", "binary", nativeType: "ledger_position");
        b.Table("ledger", other).Column("position", "binary", nativeType: "ledger_position");
        Assert.Equal(2, (await NativeTypeFindings(b.Build())).Count(d => d.Rule == "MQ4006"));

        b.ScalarType("LedgerPosition", "binary").NativeType("postgresql", "ledger_position");
        var finding = Assert.Single(await NativeTypeFindings(b.Build()));
        Assert.Equal("MQ4006", finding.Rule);
        Assert.Contains("sqlserver", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_type_revisits_the_tables_whose_native_types_carry_one_of_its_native_types()
    {
        var b = new ModelBuilder();
        var db = b.Database("billing", Dialect.PostgreSql);
        var position = b.ScalarType("LedgerPosition", "binary").NativeType("postgresql", "ledger_position(8)");
        var ledger = b.Table("ledger", db).Column("position", "binary", nativeType: "\"public\".\"ledger_position\"");
        b.Table("plain", db).Column("id", "int64");
        var model = b.Build();
        var context = new ValidationContext(model, ModelValidator.ActiveDocuments(model), new ReferenceWalker(), null);

        Assert.Equal([ledger.Id], context.NativeTypePeers(model.Get<ScalarType>(position.Id)!).Select(d => d.Element.Id));
    }

    [Fact]
    public async Task A_plain_unknown_native_type_is_a_warning_per_column_MQ4006()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        b.Table("orders", db).Column("a", "string", nativeType: "nvarchar(10)").Column("b", "string", nativeType: "nvarchar(20)");

        var findings = await NativeTypeFindings(b.Build());

        Assert.Equal(2, findings.Count);
        Assert.All(findings, d => Assert.Equal(("MQ4006", DiagnosticSeverity.Warning), (d.Rule, d.Severity)));
    }

    [Fact]
    public async Task A_user_defined_type_is_reported_once_per_type_with_its_column_count_MQ4016()
    {
        var b = new ModelBuilder();
        var db = b.Database("billing", Dialect.PostgreSql);
        var invoices = b.Table("invoices", db)
            .Column("unit", "string", nativeType: "\"public\".\"unit_of_measure\"")
            .Column("price_unit", "string", nativeType: "\"unit_of_measure\"")
            .Column("amount", "decimal", nativeType: "audit.money_t");
        b.Table("orders", db)
            .Column("unit", "string", nativeType: "public.unit_of_measure[]")
            .Column("base_unit", "string", nativeType: "\"public\".\"unit_of_measure\"");
        var model = b.Build();

        var findings = await NativeTypeFindings(model);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, d => Assert.Equal(("MQ4016", DiagnosticSeverity.Info), (d.Rule, d.Severity)));
        var unit = Assert.Single(findings, d => d.Message.Contains("unit_of_measure", StringComparison.Ordinal));
        Assert.Equal(model.GetDocument(invoices.Id)!.Path, unit.FilePath);
        Assert.Equal(("/columns/0/nativeType", invoices.ColumnId("unit")), (unit.JsonPointer, unit.ElementId));
        Assert.Equal("Native type '\"public\".\"unit_of_measure\"' names a type the database defines, which the postgresql type map cannot check; "
            + "it is used by 4 columns in 2 tables of database 'billing'.", unit.Message);
        var money = Assert.Single(findings, d => d.Message.Contains("money_t", StringComparison.Ordinal));
        Assert.EndsWith("it is used by 1 column in 1 table of database 'billing'.", money.Message, StringComparison.Ordinal);
        Assert.Equal("/columns/2/nativeType", money.JsonPointer);
    }

    [Fact]
    public async Task A_user_defined_type_counts_per_database()
    {
        var b = new ModelBuilder();
        var main = b.Database("main", Dialect.PostgreSql);
        var archive = b.Database("archive", Dialect.PostgreSql);
        b.Table("orders", main).Column("unit", "string", nativeType: "\"public\".\"unit_of_measure\"");
        b.Table("orders", archive).Column("unit", "string", nativeType: "\"public\".\"unit_of_measure\"");

        var findings = await NativeTypeFindings(b.Build());

        Assert.Equal(2, findings.Count);
        Assert.All(findings, d => Assert.Contains("1 column in 1 table", d.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sqlite_accepts_any_native_type()
    {
        var b = new ModelBuilder();
        var db = b.Database("local", Dialect.Sqlite);
        b.Table("orders", db).Column("unit", "string", nativeType: "\"main\".\"unit_of_measure\"").Column("x", "string", nativeType: "whatever");

        Assert.Empty(await NativeTypeFindings(b.Build()));
    }

    [Fact]
    public async Task Validating_any_table_that_uses_a_user_defined_type_reports_it_MQ4016()
    {
        var b = new ModelBuilder();
        var db = b.Database("billing", Dialect.PostgreSql);
        var first = b.Table("invoices", db).Column("unit", "string", nativeType: "\"public\".\"unit_of_measure\"");
        var second = b.Table("orders", db).Column("unit", "string", nativeType: "\"public\".\"unit_of_measure\"");
        var model = b.Build();

        var whole = Assert.Single(await NativeTypeFindings(model));
        foreach (var id in new[] { first.Id, second.Id })
        {
            foreach (var includeReferrers in new[] { true, false })
                Assert.Contains(whole, await NativeTypeFindings(model, new ValidationScope([id], includeReferrers)));
        }
    }

    [Fact]
    public void A_reference_type_or_enum_revisits_the_tables_whose_native_types_carry_its_name()
    {
        var b = new ModelBuilder();
        var db = b.Database("billing", Dialect.PostgreSql);
        var unit = ReferenceType(b, "UnitOfMeasure");
        b.Add(unit);
        var status = b.Enum("OrderStatus").Member("Open");
        var orders = b.Table("orders", db).Column("unit", "string", nativeType: "\"public\".\"unit_of_measure_t\"");
        var invoices = b.Table("invoices", db).Column("status", "string", nativeType: "order_status");
        b.Table("plain", db).Column("id", "int64");
        var model = b.Build();
        var context = new ValidationContext(model, ModelValidator.ActiveDocuments(model), new ReferenceWalker(), null);

        Assert.Equal([orders.Id], context.NativeTypePeers(unit).Select(d => d.Element.Id));
        Assert.Equal([invoices.Id], context.NativeTypePeers(model.Get<EnumType>(status.Id)!).Select(d => d.Element.Id));
    }
}
