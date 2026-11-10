using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// Validates a resolved model's records against the resolved-model schema (<see cref="ResolvedContract"/>): every scope's page against
/// the whole schema and against the scope's slice. Tests that build a model for one feature call it, so the schema is proven against
/// what the engine emits for that feature too.
/// </summary>
internal static class ContractCheck
{
    /// <summary>
    /// The one release step for the contract: regenerates <c>schemas/v1/resolved-model.json</c>, <c>docs/resolved-model.md</c> and the
    /// reference app's copies for the version the build has, in one run. Rebuild afterwards so the engine embeds them.
    /// </summary>
    public const string Regenerate =
        "MAQUETTISTE_UPDATE_GOLDEN=1 dotnet test tests/Maquettiste.Engine.Tests -c Release --filter " +
        "FullyQualifiedName~ResolvedContractTests.The_checked_in_schema_and_reference_match_the_record_types";

    /// <summary>
    /// The record definitions only a feature test's model produces, with that test as <c>Class.Method</c> (a test method of this
    /// assembly; <c>ResolvedContractTests</c> checks that each one exists). The test names them in
    /// <see cref="Validate(ResolvedModel, string[])"/>, which fails unless its model produces each one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Credited { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["constraintRecord"] = "ExpressionResolutionTests.Constraints_inline_and_resolve_on_their_own",
        ["constraintParameterRecord"] = "ExpressionResolutionTests.Constraints_inline_and_resolve_on_their_own",
        ["entityCheckRecord"] = "ExpressionResolutionTests.A_binding_check_resolves_on_its_write_table_through_the_field_map",
        ["formerCodeRecord"] = "ReferenceDataResolutionTests.Former_codes_resolve_to_the_replacing_rows",
        ["holdingRecord"] = "ParticipantTests.Each_satisfaction_case_and_the_actor_each_step_acts_as",
        ["scenarioLinkRecord"] = "ScenarioObjectTests.Objects_resolve_and_records_carry_them_only_where_a_scenario_has_them",
        ["memberOriginView"] = "StereotypeMembersTests.Audited_columns_reach_every_table_of_schema_billing_from_one_line",
        ["namedTriggerPointRecord"] = "NamedTriggerPointTests.Named_points_resolve_index_and_key_and_the_last_subject_takes_its_point_on_delete",
        ["triggerPointSubjectRecord"] = "NamedTriggerPointTests.Named_points_resolve_index_and_key_and_the_last_subject_takes_its_point_on_delete",
        ["operationGuardRecord"] = "OperationGuardTests.The_operation_record_lists_its_guards",
        ["referenceBindingView"] = "ReferenceBindingTests.A_bound_types_rows_join_its_tables_seed_rows_first_in_column_terms",
        ["referenceBindingFieldView"] = "ReferenceBindingTests.A_bound_types_rows_join_its_tables_seed_rows_first_in_column_terms",
        ["relationLinkRecord"] = "RelationTypeStorageTests.A_link_table_with_constants_and_an_attribute_column",
        ["relationLinkEndRecord"] = "RelationTypeStorageTests.A_link_table_with_constants_and_an_attribute_column",
        ["relationConstantRecord"] = "RelationTypeStorageTests.A_link_table_with_constants_and_an_attribute_column",
        ["relationAttributeColumnRecord"] = "RelationTypeStorageTests.A_link_table_with_constants_and_an_attribute_column",
        ["relationEndEntityRecord"] = "RelationTypeStorageTests.Each_row_of_the_binding_field_table",
        ["typeCodeRecord"] = "RelationTypeStorageTests.A_link_table_with_constants_and_an_attribute_column",
        ["stereotypeSubjectRecord"] = "StereotypeSubjectResolutionTests.Zero_carriers_give_no_subject_and_the_entry_as_written",
    };

    /// <summary>
    /// <see cref="Validate(ResolvedModel)"/>, then asserts that the model produced each of <paramref name="expected"/>, the
    /// definitions <see cref="Credited"/> credits the calling test with.
    /// </summary>
    /// <param name="model">The resolved model.</param>
    /// <param name="expected">Definition names of <see cref="Credited"/>.</param>
    /// <returns>The definition names the records reached.</returns>
    public static HashSet<string> Validate(ResolvedModel model, params string[] expected)
    {
        var produced = Validate(model);
        foreach (var name in expected)
        {
            Assert.True(Credited.ContainsKey(name), $"{name} is not in ContractCheck.Credited.");
            Assert.True(produced.Contains(name), $"The model no longer produces {name}, which ContractCheck.Credited credits to {Credited[name]}.");
        }

        return produced;
    }
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };
    private static readonly Lazy<JsonObject> Whole = new(() => ResolvedContract.Schema(null));
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(Whole.Value.ToJsonString()));
    private static readonly Lazy<Dictionary<string, JsonSchema>> Slices = new(() =>
        ResolvedRecords.Scopes.Keys.ToDictionary(s => s, s => JsonSchema.FromText(ResolvedContract.Schema(s).ToJsonString()), StringComparer.Ordinal));

    /// <summary>
    /// Asserts that every scope's page validates, and that every field a record holds as null can be null in the reference (an open
    /// value is <c>{}</c>, which accepts anything); returns the definitions the records reached, at any depth.
    /// </summary>
    /// <param name="model">The resolved model.</param>
    /// <returns>The definition names.</returns>
    public static HashSet<string> Validate(ResolvedModel model)
    {
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in ResolvedRecords.ScopeNames)
        {
            var items = ResolvedRecords.Entries(model, scope, null).OrderBy(e => (e.Kind, e.Name, e.Id)).Select(e => e.Project()).ToList();
            var page = JsonSerializer.SerializeToElement(new ResolvedPage(items, null, []), Web);
            var result = Schema.Value.Evaluate(page, Options);
            Assert.True(result.IsValid, $"The {scope} page does not fit resolved-model.json:\n{Errors(result)}\n" +
                $"If only /contract differs, the release changed: regenerate the copies and rebuild ({Regenerate}).");
            if (scope != "all")
            {
                var slice = Slices.Value[scope].Evaluate(page, Options);
                Assert.True(slice.IsValid, $"The {scope} page does not fit its scope's schema:\n{Errors(slice)}");
            }

            foreach (var item in page.GetProperty("items").EnumerateArray())
                Reached(ResolvedContract.DefinitionOf(Whole.Value, item.GetProperty("kind").GetString()!), item, produced);
        }

        return produced;
    }

    private static void Reached(string definition, JsonElement value, HashSet<string> produced)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;
        produced.Add(definition);
        var properties = Whole.Value["$defs"]![definition]!["properties"]!.AsObject();
        foreach (var field in value.EnumerateObject())
        {
            Assert.True(field.Value.ValueKind != JsonValueKind.Null || ResolvedContractReference.CanBeNull(properties[field.Name]!.AsObject()),
                $"{definition}.{field.Name} is null in real output, but the reference says it is never null.");
            Under(properties[field.Name]!, field.Value, produced);
        }
    }

    private static void Under(JsonNode schema, JsonElement value, HashSet<string> produced)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
            Reached(reference["#/$defs/".Length..], value, produced);
        else if (schema["anyOf"] is JsonArray any)
            Under(any[0]!, value, produced);
        else if (value.ValueKind == JsonValueKind.Array && schema["items"] is { } items)
        {
            foreach (var item in value.EnumerateArray())
                Under(items, item, produced);
        }
        else if (value.ValueKind == JsonValueKind.Object && schema["additionalProperties"] is JsonObject element)
        {
            foreach (var entry in value.EnumerateObject())
                Under(element, entry.Value, produced);
        }
    }

    private static string Errors(EvaluationResults results) =>
        string.Join("\n", (results.Details ?? []).Where(d => d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}): {e.Value}")).Take(20));
}
