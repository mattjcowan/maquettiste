using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Model;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Operations.OperationFixture;

namespace Maquettiste.Engine.Tests.Operations;

/// <summary>
/// Shared errors from stereotypes (next-phase-2-design.md section 4.1, task E5): a stereotype's errors reach every operation of its
/// effective set by keyed id, through section 3.1's contributed-members mechanism, less what the operation excludes and with the facets
/// its redefinitions state.
/// </summary>
public sealed class OperationInheritanceTests
{
    private const string CommonErrors = "01JR5NF0000000000000000001";
    private const string NotFound = "01JR5NF0000000000000000002";
    private const string StaleVersion = "01JR5NF0000000000000000003";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Rules = ["MQ2001", "MQ2011", "MQ2027", "MQ2028", "MQ3026", "MQ3027", "MQ3029"];

    /// <summary>A model with <c>common-errors</c> (not_found, stale_version with code <c>stale</c>) and Invoice.</summary>
    private sealed class Scene
    {
        public Scene(int seed, Func<Stereotype, Stereotype>? change = null)
        {
            B = new ModelBuilder(seed);
            Invoice = B.Entity("Invoice").Key("id", "uuid");
            var stereotype = new Stereotype
            {
                Id = CommonErrors, Key = "common-errors", Name = "CommonErrors", AppliesTo = ["operation"],
                Errors = [new OperationError { Id = NotFound, Name = "not_found" }, new OperationError { Id = StaleVersion, Name = "stale_version", Code = "stale" }],
            };
            B.Add(change is null ? stereotype : change(stereotype));
        }

        public ModelBuilder B { get; }

        public EntityBuilder Invoice { get; }

        public string Id() => B.NewId();

        public Operation Add(string name, Func<Operation, Operation> change)
        {
            var operation = change(new Operation
            {
                Id = Id(), Name = name, Subject = Invoice.Id, Effect = OperationEffect.Change, Stereotypes = ["common-errors"],
                Outcomes = [new OperationOutcome { Id = Id(), Name = "applied" }],
            });
            B.Add(operation);
            return operation;
        }

        public async Task<List<(string Rule, string Pointer)>> FindingsAsync(string on, params ExtensionDocument[] extensions)
        {
            var model = ModelSnapshot.Create(B.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", extensions, [], 1);
            var report = await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);
            var ids = Maquettiste.Engine.Validation.ModelValidator.SubElementIds(model.Get<Element>(on)!).Prepend(on).ToHashSet(StringComparer.Ordinal);
            return [.. report.Diagnostics.Where(d => Rules.Contains(d.Rule) && d.ElementId is { } id && ids.Contains(id)).Select(d => (d.Rule, d.JsonPointer ?? ""))];
        }
    }

    [Fact]
    public void A_stereotype_contributes_errors_with_keyed_ids()
    {
        var s = new Scene(51);
        var alreadySent = s.Id();
        var send = s.Add("send", o => o with { Errors = [new OperationError { Id = alreadySent, Name = "already_sent" }] });
        var package = new Package { Id = s.Id(), Name = "Ops", DefaultStereotypes = ["common-errors"] };
        s.B.Add(package);
        var ping = s.Add("ping", o => o with { Package = package.Id, Stereotypes = [] });

        var model = ResolutionKit.Resolve(s.B);

        var resolved = model.Operations.Single(o => o.Id == send.Id);
        Assert.Equal([("already_sent", alreadySent, "already_sent"), ("not_found", send.Id + "." + NotFound, "not_found"), ("stale_version", send.Id + "." + StaleVersion, "stale")],
            resolved.Errors.Select(e => (e.Name, e.Id, e.Code)));
        var notFound = resolved.Errors[1];
        Assert.Equal(("stereotype", "common-errors", NotFound, 0), (notFound.MemberOrigin.Source, notFound.MemberOrigin.Stereotype?.Key, notFound.MemberOrigin.MemberId, notFound.MemberOrigin.Redefined.Count));
        Assert.Same(resolved, notFound.Operation);
        Assert.Same(notFound, model.Find(send.Id + "." + NotFound));
        Assert.Equal("own", resolved.Errors[0].MemberOrigin.Source);
        Assert.Contains("e:" + CommonErrors, notFound.Dependencies);
        Assert.Contains("e:" + CommonErrors, resolved.Errors.MembershipKeys);

        // A package default reaches an operation, and its errors say so.
        var pinged = model.Operations.Single(o => o.Id == ping.Id);
        Assert.Equal([("package-default", ping.Id + "." + NotFound), ("package-default", ping.Id + "." + StaleVersion)],
            pinged.Errors.Select(e => (e.MemberOrigin.Source, e.Id)));

        // The definition lists its errors by their own ids, with no operation.
        var definition = model.Stereotypes.Single(d => d.Key == "common-errors");
        Assert.Equal([(NotFound, "not_found", (object?)null), (StaleVersion, "stale", null)], definition.Errors.Select(e => (e.Id, e.Code, (object?)e.Operation)));
        Assert.Same(definition.Errors[0], model.Find(NotFound));
    }

    [Fact]
    public async Task Excludes_drops_one_and_a_redefinition_states_only_its_facets()
    {
        var s = new Scene(52);
        var reason = s.Id();
        var voided = s.Add("void", o => o with
        {
            Input = [new OperationMember { Id = reason, Name = "reason", Type = new TypeRef { Builtin = "string" } }],
            Excludes = [StaleVersion],
            Redefines = [new OperationErrorRedefinition { Member = NotFound, Input = reason, Description = new Description { Text = "No such invoice." } }],
        });

        Assert.Empty(await s.FindingsAsync(voided.Id));
        var model = ResolutionKit.Resolve(s.B);
        var error = Assert.Single(model.Operations.Single(o => o.Id == voided.Id).Errors);
        Assert.Equal((voided.Id + "." + NotFound, "not_found", "reason", "No such invoice."), (error.Id, error.Code, error.Input?.Name, error.Description));
        Assert.Equal(["description", "input"], error.MemberOrigin.Redefined);
        Assert.Equal("not_found", error.DisplayName);
    }

    [Fact]
    public async Task Redefines_and_excludes_entries_that_name_no_inherited_error_are_MQ2027_and_MQ2028()
    {
        var s = new Scene(53);
        var stray = s.Id();
        var broken = s.Add("void", o => o with
        {
            Redefines = [new OperationErrorRedefinition { Member = stray, DisplayName = "Gone" }, new OperationErrorRedefinition { Member = NotFound, DisplayName = "Missing" }],
            Excludes = [NotFound, stray],
        });
        var clean = s.Add("send", o => o with { Excludes = [StaleVersion] });

        Assert.Empty(await s.FindingsAsync(clean.Id));
        Assert.Equal(
            [("MQ2001", "/excludes/1"), ("MQ2001", "/redefines/0/member"), ("MQ2027", "/redefines/0/member"), ("MQ2028", "/excludes/0"), ("MQ2028", "/excludes/1")],
            (await s.FindingsAsync(broken.Id)).Order());
    }

    [Fact]
    public async Task A_redefinition_input_naming_another_operations_member_is_MQ3027()
    {
        var s = new Scene(54, st => st with { Errors = [.. st.Errors.Select(e => e.Id == NotFound ? e with { Input = NotFound } : e)] });
        var recipient = s.Id();
        var send = s.Add("send", o => o with { Input = [new OperationMember { Id = recipient, Name = "recipient", Type = new TypeRef { Builtin = "string" } }] });
        var voided = s.Add("void", o => o with { Redefines = [new OperationErrorRedefinition { Member = NotFound, Input = recipient }] });
        var own = s.Add("archive", o => o with
        {
            Input = [new OperationMember { Id = s.Id(), Name = "note", Type = new TypeRef { Builtin = "string" } }],
            Redefines = [new OperationErrorRedefinition { Member = NotFound, Input = send.Input[0].Id }],
        });

        Assert.Equal([("MQ3027", "/redefines/0/input")], await s.FindingsAsync(voided.Id));
        Assert.Equal([("MQ3027", "/redefines/0/input")], await s.FindingsAsync(own.Id));
        // A stereotype's error points at no input member.
        Assert.Contains(("MQ3027", "/errors/0/input"), await s.FindingsAsync(CommonErrors));
    }

    [Fact]
    public async Task An_inherited_and_an_own_error_sharing_a_code_are_MQ3026_at_the_stereotype_entry()
    {
        var s = new Scene(55);
        var stamped = new Stereotype { Id = s.Id(), Key = "stamped", Name = "Stamped", AppliesTo = ["operation"] };
        s.B.Add(stamped);
        var send = s.Add("send", o => o with
        {
            Stereotypes = ["stamped", "common-errors"],
            Errors = [new OperationError { Id = s.Id(), Name = "not_found" }, new OperationError { Id = s.Id(), Name = "too_old", Code = "stale" }],
        });
        var redefined = s.Add("void", o => o with
        {
            Errors = [new OperationError { Id = s.Id(), Name = "missing", Code = "not_found" }],
            Redefines = [new OperationErrorRedefinition { Member = NotFound, DisplayName = "Not found" }],
        });
        var excluded = s.Add("archive", o => o with { Errors = [new OperationError { Id = s.Id(), Name = "not_found" }], Excludes = [NotFound] });

        Assert.Equal([("MQ3026", "/stereotypes/1"), ("MQ3026", "/stereotypes/1")], await s.FindingsAsync(send.Id));
        Assert.Equal([("MQ3026", "/redefines/0")], await s.FindingsAsync(redefined.Id));
        Assert.Empty(await s.FindingsAsync(excluded.Id));
    }

    [Fact]
    public async Task Two_stereotypes_giving_an_error_of_one_id_are_MQ3026_and_the_first_stays()
    {
        var s = new Scene(58);
        // tenant-errors repeats common-errors' not_found id with another name and code: no name or code clash hides it.
        s.B.Add(new Stereotype
        {
            Id = s.Id(), Key = "tenant-errors", Name = "TenantErrors", AppliesTo = ["operation"],
            Errors = [new OperationError { Id = NotFound, Name = "tenant_missing" }],
        });
        var send = s.Add("send", o => o with { Stereotypes = ["common-errors", "tenant-errors"] });

        Assert.Equal([("MQ3026", "/stereotypes/1")], await s.FindingsAsync(send.Id));
        var resolved = ResolutionKit.Resolve(s.B).Operations.Single(o => o.Id == send.Id);
        Assert.Equal([("not_found", "common-errors"), ("stale", "common-errors")], resolved.Errors.Select(e => (e.Code, e.MemberOrigin.Stereotype?.Key)));
    }

    [Fact]
    public async Task MQ3029_a_stereotype_with_errors_applies_to_operations()
    {
        var wrong = new Scene(56, st => st with { AppliesTo = ["entity"] });
        var any = new Scene(57, st => st with { AppliesTo = [] });

        Assert.Equal([("MQ3029", "/appliesTo")], await wrong.FindingsAsync(CommonErrors));
        Assert.Empty(await any.FindingsAsync(CommonErrors));
        Assert.Empty(await new Scene(58).FindingsAsync(CommonErrors));
    }

    [Fact]
    public async Task A_property_xref_to_an_inherited_error_takes_the_keyed_id_and_one_not_inherited_is_MQ2001()
    {
        var s = new Scene(59);
        var sendId = s.Id();
        var send = s.Add("send", o => o with
        {
            Id = sendId,
            Excludes = [StaleVersion],
            Properties = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["keyed"] = J(sendId + "." + NotFound),
                ["excluded"] = J(sendId + "." + StaleVersion),
                ["bare"] = J(NotFound),
                ["scoped"] = J(sendId + "." + NotFound),
            },
        });
        var extension = PropertySchemaSetTests.Extension("""
            { "keyed": { "type": "string", "x-ref": { "kind": "error" } },
              "excluded": { "type": "string", "x-ref": { "kind": "error" } },
              "bare": { "type": "string", "x-ref": { "kind": "error", "of": "$element" } },
              "scoped": { "type": "string", "x-ref": { "kind": "error", "of": "$element" } } }
            """, kinds: ["operation"]);

        var found = await s.FindingsAsync(send.Id, extension);

        Assert.Equal([("MQ2001", "/properties/excluded"), ("MQ2011", "/properties/bare")], found.Order());
    }

    [Fact]
    public async Task A_contributed_errors_code_change_edits_no_operation_file()
    {
        await using var s = await OpenAsync();
        await CreateAsync(s, StereotypeJson());
        var carried = await SaveAsync(s, Send, n => n["stereotypes"] = new JsonArray("common-errors"));
        Assert.Equal(SaveOutcome.Saved, carried.Outcome);

        var changed = await SaveAsync(s, CommonErrors, n => n["errors"]![0]!["code"] = "missing");

        Assert.Equal(SaveOutcome.Saved, changed.Outcome);
        Assert.Equal([CommonErrors], changed.Changes!.Changed.Select(c => c.Id));
        var model = ResolutionKit.Resolve(s.Model);
        Assert.Equal("missing", model.Operations.Single().Errors.Single(e => e.Id == Send + "." + NotFound).Code);
    }

    [Fact]
    public async Task Removing_a_contributed_error_drops_the_entries_that_name_it()
    {
        await using var s = await OpenAsync();
        await CreateAsync(s, StereotypeJson());
        var carried = await SaveAsync(s, Send, n =>
        {
            n["stereotypes"] = new JsonArray("common-errors");
            n["excludes"] = new JsonArray(StaleVersion);
            n["redefines"] = new JsonArray(new JsonObject { ["member"] = NotFound, ["displayName"] = "Not found" });
        });
        Assert.Equal(SaveOutcome.Saved, carried.Outcome);

        var removed = await SaveAsync(s, CommonErrors, n => n["errors"] = new JsonArray());

        Assert.Equal(SaveOutcome.Saved, removed.Outcome);
        Assert.Equal([Send, CommonErrors], removed.Changes!.Changed.Select(c => c.Id).Order(StringComparer.Ordinal));
        var send = s.Model.Get<Operation>(Send)!;
        Assert.Empty(send.Redefines);
        Assert.Empty(send.Excludes);
    }

    [Fact]
    public async Task Removing_a_contributed_error_clears_an_optional_keyed_property_and_a_required_one_refuses_the_save()
    {
        // As for a table's inherited column: a required x-ref value naming the keyed id refuses the save with MQ2001, so the user
        // removes it first; an optional one is cleared in the same save.
        await using var s = await OpenAsync();
        await CreateAsync(s, StereotypeJson());
        var extension = await s.Store.WriteExtensionFileAsync("answers.json", """
            { "name": "answers", "appliesTo": { "kinds": ["operation"] },
              "properties": {
                "usual": { "type": "string", "x-ref": { "kind": "error", "of": "$element", "optional": true } },
                "worst": { "type": "string", "x-ref": { "kind": "error", "of": "$element" } } } }
            """, null, Ct);
        Assert.Equal(SaveOutcome.Saved, extension.Outcome);
        var keyed = Send + "." + NotFound;
        var carried = await SaveAsync(s, Send, n =>
        {
            n["stereotypes"] = new JsonArray("common-errors");
            n["properties"] = new JsonObject { ["usual"] = keyed, ["worst"] = keyed };
        });
        Assert.Equal(SaveOutcome.Saved, carried.Outcome);

        var refused = await SaveAsync(s, CommonErrors, n => n["errors"]!.AsArray().RemoveAt(0));

        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Equal([("MQ2001", Send, "/properties/worst")], refused.Diagnostics.Select(d => (d.Rule, d.ElementId, d.JsonPointer)));
        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(s, Send, n => n["properties"]!.AsObject().Remove("worst"))).Outcome);

        var removed = await SaveAsync(s, CommonErrors, n => n["errors"]!.AsArray().RemoveAt(0));

        Assert.Equal(SaveOutcome.Saved, removed.Outcome);
        Assert.Equal([Send, CommonErrors], removed.Changes!.Changed.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Empty(s.Model.Get<Operation>(Send)!.Properties);
    }

    private static System.Text.Json.JsonElement J(string value) => System.Text.Json.JsonSerializer.SerializeToElement(value);

    private static string StereotypeJson() => $$"""
        { "kind": "stereotype", "id": "{{CommonErrors}}", "key": "common-errors", "name": "CommonErrors", "appliesTo": ["operation"],
          "errors": [ { "id": "{{NotFound}}", "name": "not_found" }, { "id": "{{StaleVersion}}", "name": "stale_version" } ] }
        """;

    private static async Task<SaveResult> SaveAsync(Store.BillingStore s, string id, Action<JsonObject> edit)
    {
        var document = s.Model.GetDocument(id)!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        return await s.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
    }
}
