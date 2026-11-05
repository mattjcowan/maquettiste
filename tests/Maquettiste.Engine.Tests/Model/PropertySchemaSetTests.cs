using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// The <c>x-ref</c> keyword of custom property schemas (next-phase-1-design.md section 4.1): a well-formed keyword becomes a site
/// of the property, and a malformed or misplaced one is MQ2009 on the extension file, at the keyword or the key at fault.
/// </summary>
public sealed class PropertySchemaSetTests
{
    private const string Path = ".maquettiste/extensions/links.json";

    [Theory]
    // Well formed: no MQ2009, and the property has a site.
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity" } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["entity", "value-object"], "optional": true } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "row", "of": "<CUSTOMER>" } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["operation", "outcome", "error", "member", "custom", "custom:billing-run"] } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["column", "table"], "sameDatabase": true } }""", null)]
    [InlineData("""{ "type": "array", "items": { "x-ref": { "kind": "entity" } } }""", null)]
    [InlineData("""{ "type": "object", "additionalProperties": { "type": "string", "x-ref": { "kind": "entity" } } }""", null)]
    [InlineData("""{ "type": "object", "properties": { "lead": { "type": "string", "x-ref": { "kind": "actor" } } } }""", null)]
    [InlineData("""{ "type": ["string", "null"], "x-ref": { "kind": "entity" } }""", null)]
    // Malformed: the value, an unknown key, kind, custom:<type>, of, sameDatabase, optional.
    [InlineData("""{ "type": "string", "x-ref": "entity" }""", "/properties/p/x-ref")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity", "scope": "billing" } }""", "/properties/p/x-ref/scope")]
    [InlineData("""{ "type": "string", "x-ref": { "optional": true } }""", "/properties/p/x-ref")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "widget" } }""", "/properties/p/x-ref/kind")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": [] } }""", "/properties/p/x-ref/kind")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["entity", "entity"] } }""", "/properties/p/x-ref/kind")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["entity", "custom:Billing_Run"] } }""", "/properties/p/x-ref/kind/1")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity", "of": "$element" } }""", "/properties/p/x-ref/of")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "operation", "of": "$element" } }""", "/properties/p/x-ref/of")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "attribute", "of": "Customer" } }""", "/properties/p/x-ref/of")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "row", "of": "01J92P0V0000000000000000ZZ" } }""", "/properties/p/x-ref/of")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity", "sameDatabase": true } }""", "/properties/p/x-ref/sameDatabase")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "table", "sameDatabase": "yes" } }""", "/properties/p/x-ref/sameDatabase")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity", "optional": 1 } }""", "/properties/p/x-ref/optional")]
    // Misplaced: not on a string, or under an applicator the walk does not follow.
    [InlineData("""{ "type": "integer", "x-ref": { "kind": "entity" } }""", "/properties/p/x-ref")]
    [InlineData("""{ "type": "array", "x-ref": { "kind": "entity" } }""", "/properties/p/x-ref")]
    [InlineData("""{ "type": "array", "items": { "type": "object", "x-ref": { "kind": "entity" } } }""", "/properties/p/items/x-ref")]
    [InlineData("""{ "type": "string", "allOf": [ { "x-ref": { "kind": "entity" } } ] }""", "/properties/p/allOf/0/x-ref")]
    [InlineData("""{ "type": "string", "if": { "x-ref": { "kind": "entity" } } }""", "/properties/p/if/x-ref")]
    [InlineData("""{ "type": "object", "patternProperties": { "^a": { "type": "string", "x-ref": { "kind": "entity" } } } }""", "/properties/p/patternProperties/^a/x-ref")]
    [InlineData("""{ "type": "string", "$defs": { "id": { "type": "string", "x-ref": { "kind": "entity" } } } }""", "/properties/p/$defs/id/x-ref")]
    public void Xref_keywords_parse_and_malformed_ones_are_MQ2009(string schema, string? pointer)
    {
        var b = new ModelBuilder(21);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var properties = "{ \"p\": " + schema.Replace("<CUSTOMER>", customer.Id, StringComparison.Ordinal) + " }";
        var model = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "",
            [Extension(properties)], [], 1);

        var diagnostics = new List<Diagnostic>();
        _ = new ExtensionSet(model, diagnostics);

        var found = diagnostics.Where(d => d.Rule == "MQ2009").ToList();
        if (pointer is null)
        {
            Assert.Empty(found);
            Assert.True(model.PropertySchemas.Sources.Single().Refs.ContainsKey("p"));
            return;
        }

        var problem = Assert.Single(found);
        Assert.Equal((Path, pointer, DiagnosticSeverity.Error), (problem.FilePath, problem.JsonPointer, problem.Severity));
        Assert.StartsWith("x-ref", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_beside_a_typed_additionalProperties_match_without_backtracking()
    {
        // ^(a+)+$ backtracks exponentially on a run of a then b; the link walk matches it in linear time, with no
        // timeout.
        const string properties = """
            { "p": { "type": "object", "patternProperties": { "^(a+)+$": { "type": "string" } },
                     "additionalProperties": { "type": "string", "x-ref": { "kind": "entity" } } } }
            """;
        var node = PropertySchemaSet.Create([Extension(properties)], null).Sources.Single().Refs["p"];

        Assert.NotNull(node.Member(new string('a', 4096) + "b"));
        Assert.Null(node.Member(new string('a', 4096)));
        Assert.NotNull(node.Member("owner"));
    }

    [Fact]
    public void A_pattern_that_needs_backtracking_beside_a_typed_additionalProperties_is_MQ2009()
    {
        const string properties = """
            { "p": { "type": "object", "patternProperties": { "^(?!x-)": { "type": "string" } },
                     "additionalProperties": { "type": "string", "x-ref": { "kind": "entity" } } } }
            """;
        var b = new ModelBuilder(22);
        _ = b.Entity("Customer").Key("id", "uuid");
        var model = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "",
            [Extension(properties)], [], 1);

        var diagnostics = new List<Diagnostic>();
        _ = new ExtensionSet(model, diagnostics);

        var problem = Assert.Single(diagnostics, d => d.Rule == "MQ2009");
        Assert.Equal((Path, "/properties/p/patternProperties/^(?!x-)"), (problem.FilePath, problem.JsonPointer));
        Assert.False(model.PropertySchemas.Sources.Single().Refs.ContainsKey("p"));
    }

    [Fact]
    public void An_unchanged_set_of_files_reuses_the_set_and_marks_translatable_names()
    {
        const string properties = """{ "title": { "type": "string", "x-translatable": true }, "owner": { "type": "string", "x-ref": { "kind": "entity" } } }""";
        var first = PropertySchemaSet.Create([Extension(properties)], null);
        var again = PropertySchemaSet.Create([Extension(properties)], first);
        var changed = PropertySchemaSet.Create([Extension(properties.Replace("entity", "actor", StringComparison.Ordinal))], first);

        Assert.Same(first, again);
        Assert.NotSame(first, changed);
        Assert.NotEqual(first.Hash, changed.Hash);
        Assert.Equal(["title"], first.Sources.Single().Translatable);
        Assert.Equal(["owner"], first.Sources.Single().Refs.Keys);
        Assert.Equal("extension:links", first.Sources.Single().Schema);
        Assert.Equal(PropertySchemaSet.Empty.Hash, PropertySchemaSet.Create([], null).Hash);
    }

    [Fact]
    public void Guards_actions_invokes_gates_and_checks_are_property_hosts()
    {
        var b = new ModelBuilder();
        var (process, state, nested, invoke, inner, guard, action, transition, gate, entity, entityCheck, table, tableCheck, stereotype, stereotypeCheck, operation, operationGuard) =
            (b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId());
        var signer = b.NewId();
        ProcessInvoke Invoke(string id, string name) => new() { Id = id, Name = name, Type = InvokeType.Service };
        var always = new Maquettiste.Engine.Model.Expr { Value = System.Text.Json.JsonDocument.Parse("true").RootElement };
        var built = new Process
        {
            Id = process,
            Name = "Flow",
            States = [new ProcessState { Id = state, Name = "Open", Invoke = [Invoke(invoke, "one")], States = [new ProcessState { Id = nested, Name = "Inner", Invoke = [Invoke(inner, "two")] }] }],
            Guards = [new ProcessGuard { Id = guard, Name = "ready" }],
            Actions = [new ProcessAction { Id = action, Name = "notify" }],
            Transitions = [new ProcessTransition { Id = transition, Source = state, Trigger = TransitionTrigger.Event, Gate = new ProcessGate { Id = gate, Name = "sign", Signers = [signer], Meanings = [] } }],
        };

        Assert.Equal(
            [
                ("", process, "process"),
                ("/states/0", state, "state"),
                ("/states/0/states/0", nested, "state"),
                ("/transitions/0", transition, "transition"),
                ("/guards/0", guard, "guard"),
                ("/actions/0", action, "action"),
                ("/states/0/invoke/0", invoke, "invoke"),
                ("/states/0/states/0/invoke/0", inner, "invoke"),
                ("/transitions/0/gate", gate, "gate"),
            ],
            PropertyHost.Of(built, null).Select(h => (h.Pointer, h.Id, h.KindName)));
        var withCheck = new Entity { Id = entity, Name = "Thing", Checks = [new EntityCheck { Id = entityCheck, Name = "ok", Condition = always }] };
        Assert.Equal(("/checks/0", entityCheck, "check"), PropertyHost.Of(withCheck, null).Select(h => (h.Pointer, h.Id, h.KindName)).Last());
        var withTableCheck = new Table { Id = table, Name = "things", Database = entity, Checks = [new CheckConstraint { Id = tableCheck }] };
        Assert.Equal(("/checks/0", tableCheck, "check"), PropertyHost.Of(withTableCheck, null).Select(h => (h.Pointer, h.Id, h.KindName)).Last());
        var withStereotypeCheck = new Stereotype { Id = stereotype, Key = "kept", Name = "Kept", Checks = [new CheckConstraint { Id = stereotypeCheck }] };
        Assert.Equal(("/checks/0", stereotypeCheck, "check"), PropertyHost.Of(withStereotypeCheck, null).Select(h => (h.Pointer, h.Id, h.KindName)).Last());
        var withGuard = new Operation { Id = operation, Name = "settle", Effect = OperationEffect.Change, Outcomes = [], Guards = [new OperationGuard { Id = operationGuard, Name = "open", Condition = always }] };
        Assert.Equal(("/guards/0", operationGuard, "guard"), PropertyHost.Of(withGuard, null).Select(h => (h.Pointer, h.Id, h.KindName)).Last());
    }

    [Fact]
    public async Task A_package_default_types_the_properties_of_its_members()
    {
        // next-phase-2-design.md section 3.1: a stereotype a package default gives types its members' properties as an own one does:
        // For and Sites take the element's effective keys, so the index records the link and the validator checks the parameters.
        var b = new ModelBuilder(23);
        b.Stereotype("owned").AppliesTo("entity").Parameters("""{ "owner": { "type": "string", "x-ref": { "kind": "entity" } } }""", "owner");
        var billing = b.Package("Billing");
        var customer = b.Entity("Customer").Key("id", "uuid");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid").Property("owner", customer.Id);
        var payment = b.Entity("Payment", billing).Key("id", "uuid");
        var stray = b.Entity("Stray").Key("id", "uuid").Property("owner", customer.Id);
        var model = StereotypeDefaultsTests.Rebuild(b, e => e is Package p && p.Id == billing.Id ? p with { DefaultStereotypes = ["owned"] } : e);

        var element = model.Get<Entity>(invoice.Id)!;
        var keys = model.PropertyKeys(element);
        Assert.Equal(["owned"], keys);
        Assert.Contains(model.PropertySchemas.For(PropertyHost.Of(element, keys).First()), s => s.StereotypeKey == "owned");
        Assert.Equal(["/properties/owner"], model.PropertySchemas.Sites(element, keys).Select(site => site.Pointer));
        Assert.Empty(model.PropertySchemas.Sites(element, null)); // its own list alone types nothing
        Assert.Empty(model.PropertyKeys(model.Get<Entity>(stray.Id)!)!); // outside the package: no default

        Assert.Contains(model.ReferencesTo(customer.Id), r => r is { Field: "owner", Schema: "stereotype:owned" } && r.FromElementId == invoice.Id);
        Assert.DoesNotContain(model.ReferencesTo(customer.Id), r => r.FromElementId == stray.Id);

        var report = await Validation.ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);
        var missing = Assert.Single(report.Diagnostics, d => d.Rule == "MQ2018");
        Assert.Equal((payment.Id, ""), (missing.ElementId, missing.JsonPointer ?? ""));
    }

    [Fact]
    public void Operation_hosts_take_their_kind_names()
    {
        // next-phase-2-design.md section 4.1: an operation's input members, outcomes, the members of each returns and its errors hold
        // properties under their own kind names, so extension files and stereotypes apply to them by those names.
        var b = new ModelBuilder(24);
        var invoice = b.Entity("Invoice").Key("id", "uuid");
        string send = b.NewId(), recipient = b.NewId(), applied = b.NewId(), waiting = b.NewId(), trackingId = b.NewId(), alreadySent = b.NewId();
        b.Add(new Operation
        {
            Id = send, Name = "send", Subject = invoice.Id, Effect = OperationEffect.Change,
            Input = [new OperationMember { Id = recipient, Name = "recipient", Type = new TypeRef { Builtin = "string" } }],
            Outcomes =
            [
                new OperationOutcome { Id = applied, Name = "applied", Returns = new OperationReturns { Entity = invoice.Id } },
                new OperationOutcome
                {
                    Id = waiting, Name = "waiting", Completes = OperationCompletion.Later,
                    Returns = new OperationReturns { Members = [new OperationMember { Id = trackingId, Name = "trackingId", Type = new TypeRef { Builtin = "ulid" } }] },
                },
            ],
            Errors = [new OperationError { Id = alreadySent, Name = "already_sent" }],
        });
        var model = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "",
            [Extension("""{ "audience": { "type": "string" } }""", "api", ["operation-member", "error"])], [], 1);

        var hosts = PropertyHost.Of(model.Get<Operation>(send)!, null).ToList();

        Assert.Equal(
            [
                ("", send, "operation"),
                ("/input/0", recipient, "operation-member"),
                ("/outcomes/0", applied, "outcome"),
                ("/outcomes/1", waiting, "outcome"),
                ("/outcomes/1/returns/members/0", trackingId, "operation-member"),
                ("/errors/0", alreadySent, "error"),
            ],
            hosts.Select(h => (h.Pointer, h.Id, h.KindName)));
        Assert.Equal([false, true, false, false, true, true], hosts.Select(h => model.PropertySchemas.For(h).Any(s => s.Schema == "extension:api")));
    }

    internal static ExtensionDocument Extension(string properties, string name = "links", IReadOnlyList<string>? kinds = null,
        IReadOnlyList<string>? stereotypes = null, string? path = null)
    {
        using var document = JsonDocument.Parse(properties);
        var schema = new ExtensionSchema
        {
            Name = name,
            AppliesTo = new ExtensionTarget { Kinds = kinds ?? ["entity"], Stereotypes = stereotypes ?? [] },
            Properties = document.RootElement.Clone(),
        };
        return new ExtensionDocument(schema, path ?? Path, ContentHash.Of(string.Join("\n", name, string.Join(",", schema.AppliesTo.Kinds),
            string.Join(",", schema.AppliesTo.Stereotypes), properties)));
    }

    // ---- custom kinds (next-phase-2-design.md section 4.2) ----

    private static bool Valid(global::Json.Schema.JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return schema.Evaluate(document.RootElement).IsValid;
    }

    [Fact]
    public void Custom_kind_sources_type_content_and_join_the_hash()
    {
        var setting = CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.SettingKind);
        var without = PropertySchemaSet.Create([], null);
        var set = PropertySchemaSet.Create([], null, null, [setting]);

        var source = Assert.Single(set.ContentSources);
        Assert.Same(source, set.ContentOf("setting"));
        Assert.Null(set.ContentOf("flag"));
        Assert.Equal(("custom-kind:setting", "setting", ".maquettiste/extensions/kinds/setting.json"), (source.Schema, source.CustomType, source.Path));
        Assert.Empty(set.Sources);
        Assert.True(set.HasRefs);
        Assert.Empty(set.Diagnostics);
        Assert.NotEqual(without.Hash, set.Hash);
        Assert.Same(set, PropertySchemaSet.Create([], set, null, [setting]));
        var changed = CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.SettingKind.Replace("\"unit\"", "\"units\"", StringComparison.Ordinal));
        Assert.NotEqual(set.Hash, PropertySchemaSet.Create([], set, null, [changed]).Hash);

        // The content's x-ref sites: field is the top-level key, the holder the element.
        var ids = new ModelBuilder(41);
        var row = ids.NewId();
        var custom = CustomKinds.CustomFixture.Element(ids.NewId(), "due_days", "setting", $$"""{ "code": "{{row}}", "value": 30 }""");
        var site = Assert.Single(set.Sites(custom, null));
        Assert.Equal(("/content/code", "code", row, custom.Id, "custom-kind:setting"), (site.Pointer, site.Field, site.Value, site.Host.Id, site.Source.Schema));

        // The compiled schema is closed and gives each member item a required id.
        var message = PropertySchemaSet.Create([], null, null, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.MessageKind)]).ContentOf("message")!;
        var compiled = message.Compiled.Schema!;
        Assert.True(Valid(compiled, """{ "parts": [ { "id": "01JR6CK00000000000000000P1", "name": "subject" } ] }"""));
        Assert.False(Valid(compiled, """{ "parts": [ { "name": "subject" } ] }"""));
        Assert.False(Valid(compiled, """{ "parts": [ { "id": "not-an-id", "name": "subject" } ] }"""));
        Assert.False(Valid(compiled, """{ "other": 1 }"""));
        Assert.True(message.Members["parts"].Items is { IsMember: true, Named: true });
        Assert.Equal(["text"], message.Members["parts"].Items!.Translatable);
    }

    [Fact]
    public void Custom_kind_sources_never_type_properties()
    {
        var set = PropertySchemaSet.Create([], null, null, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.SettingKind)]);
        var ids = new ModelBuilder(42);
        var row = ids.NewId();
        var custom = CustomKinds.CustomFixture.Element(ids.NewId(), "due_days", "setting") with
        {
            Properties = new Dictionary<string, JsonElement> { ["code"] = JsonSerializer.SerializeToElement(row) },
        };

        Assert.Empty(set.For("custom", []));
        Assert.Empty(set.For("entity", []));
        Assert.Empty(set.ForDefaults("audited"));
        Assert.Empty(set.Sites(custom, null));
    }

    public static TheoryData<string, string?> XMemberRows() => new()
    {
        { """{ "type": "array", "x-member": true, "items": { "type": "object", "properties": { "name": { "type": "string" } } } }""", null },
        { """{ "type": "array", "x-member": "yes", "items": { "type": "object" } }""", "/properties/parts/x-member" },
        { """{ "type": "string", "x-member": true }""", "/properties/parts/x-member" },
        { """{ "type": "array", "x-member": true, "items": { "type": "string" } }""", "/properties/parts/x-member" },
        { """{ "type": "array", "x-member": true, "items": { "type": "object", "properties": { "id": { "type": "string" } } } }""", "/properties/parts/items/properties/id" },
        { """{ "allOf": [ { "type": "array", "x-member": true, "items": { "type": "object" } } ] }""", "/properties/parts/allOf/0/x-member" },
        { """{ "type": "object", "additionalProperties": { "type": "array", "x-member": true, "items": { "type": "object" } } }""", "/properties/parts/additionalProperties/x-member" },
    };

    [Theory]
    [MemberData(nameof(XMemberRows))]
    public void X_member_marks_parse_and_misplaced_ones_are_MQ1018(string schema, string? pointer)
    {
        var kind = CustomKinds.CustomFixture.KindDocument($$"""{ "type": "thing", "properties": { "parts": {{schema}} } }""");

        var set = PropertySchemaSet.Create([], null, null, [kind]);

        var findings = set.Diagnostics.Where(d => d.Rule == "MQ1018").ToList();
        if (pointer is null)
        {
            Assert.Empty(findings);
            Assert.True(set.ContentOf("thing")!.Members["parts"].Items is { IsMember: true, Named: true });
            return;
        }

        var finding = Assert.Single(findings);
        Assert.Equal((pointer, ".maquettiste/extensions/kinds/thing.json"), (finding.JsonPointer, finding.FilePath));
        Assert.Empty(set.ContentOf("thing")!.Members);
    }

    [Fact]
    public void Nested_x_member_arrays_form_one_tree()
    {
        var set = PropertySchemaSet.Create([], null, null, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.LayoutKind)]);

        var sections = set.ContentOf("layout")!.Members["sections"].Items!;
        Assert.True(sections.IsMember);
        Assert.True(sections.Properties!["lines"].Items is { IsMember: true, Named: true });
        Assert.Empty(set.Diagnostics);
    }

    [Fact]
    public void A_type_differing_from_its_file_or_requiring_an_undeclared_key_is_MQ1017()
    {
        var renamed = CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.SettingKind, stem: "settings");
        var missing = CustomKinds.CustomFixture.KindDocument("""{ "type": "flag", "properties": { "on": { "type": "boolean" } }, "required": ["on", "off"] }""");
        var broken = CustomKinds.CustomFixture.KindDocument("""{ "type": "broken", "properties": { "v": { "type": "nonsense" } } }""");

        var set = PropertySchemaSet.Create([], null, null, [renamed, missing, broken]);

        Assert.Null(set.ContentOf("setting"));
        Assert.NotNull(set.ContentOf("flag"));
        Assert.Equal(
            [
                (".maquettiste/extensions/kinds/broken.json", "/properties"),
                (".maquettiste/extensions/kinds/flag.json", "/required/1"),
                (".maquettiste/extensions/kinds/settings.json", "/type"),
            ],
            set.Diagnostics.Where(d => d.Rule == "MQ1017").Select(d => (d.FilePath!, d.JsonPointer!)).Order());
        Assert.Contains("differs from its file name 'settings'", set.Diagnostics.Single(d => d.JsonPointer == "/type").Message, StringComparison.Ordinal);
    }
}
