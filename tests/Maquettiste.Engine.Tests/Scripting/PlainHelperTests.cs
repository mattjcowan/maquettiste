using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Scripting;

/// <summary>
/// Plain helpers (next-phase-1-design.md section 5.5): <c>maquettiste.helper(name, fn, { plain: true })</c> takes frozen plain
/// copies of its arguments, bounded by member hops (a resolved object argument at hop 0, the resolved objects its members hold at
/// hop 1, stubs <c>{ id, kind }</c> from hop 2), instead of proxies.
/// </summary>
public sealed class PlainHelperTests
{
    /// <summary>
    /// Walks a value as the plain copy bounds it: an object with a string <c>id</c> and <c>kind</c> is a resolved object, whose
    /// members are one hop further, and from hop 2 only its id and kind are kept; lists and other objects add no hop. Keys are sorted
    /// and functions left out, so a proxy and a plain copy of the same object give the same text.
    /// </summary>
    private const string Walker = """
        function walk(v, hop) {
          if (v === null || typeof v !== 'object') return v;
          if (Array.isArray(v) || (typeof v.length === 'number' && typeof v.map === 'function' && !('id' in v)))
            return Array.prototype.map.call(v, x => walk(x, hop));
          const resolved = typeof v.id === 'string' && typeof v.kind === 'string';
          if (resolved && hop >= 2) return { id: v.id, kind: v.kind };
          const out = {};
          for (const k of Object.keys(v).sort()) {
            const x = v[k];
            if (typeof x !== 'function') out[k] = walk(x, resolved ? hop + 1 : hop);
          }
          return out;
        }
        """;

    /// <summary>
    /// A PostgreSQL database <c>main</c> with the synthesized table <c>customers</c> of entity <c>Customer</c> (columns <c>id</c>,
    /// <c>email</c> and <c>name</c>, each with its attribute) and a designed table <c>notes</c>.
    /// </summary>
    private static ResolvedModel Model()
    {
        var b = new ModelBuilder(seed: 559);
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("email", "string", a => a.Length(254)).Attr("name", "string").Tag("core");
        var main = b.Database("main", Dialect.PostgreSql);
        b.Add(new Table
        {
            Id = b.NewId(), Database = main.Id, Origin = TableOrigin.Synthesized, Entity = customer.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = customer.AttrId("email"), DisplayName = "E-mail" }],
        });
        b.Table("notes", main).Column("id", "int64", nullable: false).Column("body", "text").PrimaryKey("id");
        return ResolutionKit.Resolve(b);
    }

    private static object? Call(IScriptSandboxPool pool, string name, IReadRecorder? reads, params object?[] args)
    {
        using var lease = pool.Rent();
        return lease.Sandbox.CallHelper(name, args, Scripts.Ctx(reads));
    }

    [Fact]
    public void A_plain_helper_sees_the_proxy_values_for_two_hops_and_stubs_beyond()
    {
        using var pool = Scripts.Pool(Walker + """
            maquettiste.helper('proxied', (x) => JSON.stringify(walk(x, 0)));
            maquettiste.helper('plain', (x) => JSON.stringify(walk(x, 0)), { plain: true });
            maquettiste.helper('stubs', (t) => JSON.stringify([Object.keys(t.columns[0].attribute), t.columns[0].attribute, t.database.tables.map(s => Object.keys(s).join())]), { plain: true });
            maquettiste.helper('shared', (t, again) => [t === again, t.columns[0].table === t.columns[1].table, t.columns[0].table === t].join(), { plain: true });
            """);
        var model = Model();
        var customers = model.Db("main").Table("customers");

        // Every member a proxy read gives at hops 0 and 1 is in the copy, with the same value; hop-2 objects are stubs either way.
        var text = (string)Call(pool, "plain", null, customers)!;
        Assert.Equal(Call(pool, "proxied", null, customers), text);
        Assert.Contains("\"name\":\"customers\"", text, StringComparison.Ordinal);
        Assert.Contains("\"displayName\":\"E-mail\"", text, StringComparison.Ordinal); // a column, at hop 1
        Assert.Contains("\"dialect\":\"postgresql\"", text, StringComparison.Ordinal); // the database, at hop 1
        Assert.Equal(Call(pool, "proxied", null, model.Entity("Customer")), Call(pool, "plain", null, model.Entity("Customer")));
        Assert.Equal(Call(pool, "proxied", null, (object)model.Db("main").Tables.ToList()), Call(pool, "plain", null, (object)model.Db("main").Tables.ToList()));

        // table.columns[0].attribute is a stub with only its id and kind, and so is every table the hop-1 database lists.
        var attribute = customers.Columns[0].Attribute!;
        Assert.Equal(
            $$"""[["id","kind"],{"id":"{{attribute.Id}}","kind":"attribute"},["id,kind","id,kind"]]""",
            Call(pool, "stubs", null, customers));

        // One copy per object and hop in a call: the table given twice is one object, and the columns' table is one hop-2 stub.
        Assert.Equal("true,true,false", Call(pool, "shared", null, customers, customers));
    }

    [Fact]
    public void A_plain_helper_records_every_copied_object_as_read()
    {
        using var pool = Scripts.Pool("maquettiste.helper('nothing', (t) => 0, { plain: true });");
        var model = Model();
        var main = model.Db("main");
        var customers = main.Table("customers");
        var reads = new ListRecorder();

        Call(pool, "nothing", reads, customers);

        // The helper reads nothing, yet everything copied at hops 0 and 1 is recorded: the table, its columns, its entity and its
        // database, and the lists copied with them.
        var recorded = reads.Keys.ToHashSet(StringComparer.Ordinal);
        IResolvedObject[] copied = [customers, .. customers.Columns, customers.Entity!, main];
        foreach (var item in copied)
            Assert.Superset(item.Dependencies.ToHashSet(StringComparer.Ordinal), recorded);
        Assert.Superset(customers.Columns.MembershipKeys.ToHashSet(StringComparer.Ordinal), recorded);
        Assert.Superset(main.Tables.MembershipKeys.ToHashSet(StringComparer.Ordinal), recorded);

        // A stub records nothing: in the hand-built model, invoice is only a stub of the package's entity list (hop 2), so its key
        // is not recorded while the list's is.
        var f = new ResolvedFixture();
        var fixtureReads = new ListRecorder();
        Call(pool, "nothing", fixtureReads, f.Customer);
        Assert.Equal(["e:CUSTOMER", "e:PKG", "k:entity", "m:customer-attributes", "r:CUSTOMER"], fixtureReads.Keys.Distinct().Order(StringComparer.Ordinal));
        Assert.DoesNotContain("e:INVOICE", fixtureReads.Keys);
    }

    [Fact]
    public void A_plain_copy_has_no_proxy_functions_and_refuses_the_model()
    {
        using var pool = Scripts.Pool("""
            const shape = (t) => [typeof t.hasStereotype, typeof t.hasTag, Object.isFrozen(t), Array.isArray(t.columns), Object.isFrozen(t.columns), t.entity.tags.join()].join();
            maquettiste.helper('plain', shape, { plain: true });
            maquettiste.helper('proxied', shape);
            maquettiste.helper('count', (m) => m.entities.length, { plain: true });
            maquettiste.helper('write', (t) => { t.name = 'other'; return t.name; }, { plain: true });
            """);
        var model = Model();
        var customers = model.Db("main").Table("customers");

        Assert.Equal("undefined,undefined,true,true,true,core", Call(pool, "plain", null, customers));
        Assert.StartsWith("function,function,", (string)Call(pool, "proxied", null, customers)!, StringComparison.Ordinal);
        var write = Assert.Throws<ScriptErrorException>(() => Call(pool, "write", null, customers));
        Assert.Contains("TypeError", write.Diagnostic.Message, StringComparison.Ordinal);

        // The model's copy would be the whole model: a TypeError, so the helper fails with MQ6016, as a list holding it does.
        var error = Assert.Throws<ScriptErrorException>(() => Call(pool, "count", null, model));
        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Contains("TypeError", error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("whole model", error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Throws<ScriptErrorException>(() => Call(pool, "count", null, (object)new object?[] { customers, model }));
        Assert.Equal(1L, Call(pool, "count", null, (object)new Dictionary<string, object?> { ["entities"] = model.Entities.ToList() }));
    }

    [Theory]
    [InlineData("{ plain: true, deep: true }", "unknown option 'deep'")]
    [InlineData("{ plain: 'yes' }", "plain must be true or false")]
    [InlineData("'plain'", "options must be an object")]
    [InlineData("[true]", "options must be an object")]
    [InlineData("null", "options must be an object")]
    public void An_unknown_helper_option_is_a_load_error(string options, string expected)
    {
        var code = $"maquettiste.helper('x', () => 1, {options});";
        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool(code));

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Contains(expected, error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
        Assert.Equal(1, error.Diagnostic.Line);

        // Without options, or with plain false, a helper takes proxies as before.
        using var pool = Scripts.Pool("maquettiste.helper('a', (x) => typeof x.hasTag);\nmaquettiste.helper('b', (x) => typeof x.hasTag, { plain: false });");
        var customer = Model().Entity("Customer");
        Assert.Equal<(object?, object?)>(("function", "function"), (Call(pool, "a", null, customer), Call(pool, "b", null, customer)));
    }
}
