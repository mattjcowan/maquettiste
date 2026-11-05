using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Cli.Commands;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The tools of <c>maquettiste mcp</c> (docs/mcp.md): thin wrappers over <see cref="ModelStore"/> and <see cref="GenerationService"/>
/// with the semantics and JSON bodies of the editor API operations (docs/api/openapi.yaml), so an agent and the editor see one model.
/// </summary>
/// <remarks>
/// A tool's success is one text block holding the operation's JSON body, serialized with <see cref="JsonSerializerDefaults.Web"/> like
/// the API's. A failure is a tool error (<see cref="CallToolResult.IsError"/>) whose text is a problem object: the API's
/// <c>code</c>, <c>status</c> and <c>title</c>, plus the body the API returns with that status (diagnostics, the disk version,
/// referrers, stale units). Every read starts with a stat rescan of the model folder, so edits made behind the server (by the agent's
/// own file tools, git or the editor) are seen. Writes are recorded as <see cref="ChangeSource.Cli"/>. Cancellation of a request
/// cancels the engine call; a cancelled request gets no result, as the protocol requires.
/// </remarks>
/// <param name="store">The model store.</param>
/// <param name="generation">The generation service over <paramref name="store"/>.</param>
/// <param name="repoRoot">The repo root, for the project name when the settings have none.</param>
/// <param name="log">Where an internal failure is reported, one line each (standard error, never the protocol stream).</param>
/// <param name="workspace">What the checkout is called, which <c>get_project</c> reports (<see cref="Functions.WorkspaceDetection"/>);
/// <see langword="null"/> means nothing is known.</param>
internal sealed partial class ModelTools(ModelStore store, GenerationService generation, string repoRoot, TextWriter log, WorkspaceInfo? workspace = null)
{
    private readonly TextWriter _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly ModelStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly GenerationService _generation = generation ?? throw new ArgumentNullException(nameof(generation));

    /// <summary>
    /// The read tools shared with the editor's assistant (<see cref="AgentTools"/>): <see cref="McpServerSetup"/> lists them beside this
    /// class's own tools, with their schemas from the one catalog, so the two cannot drift.
    /// </summary>
    public AgentTools Catalog { get; } = new(store, generation, new AgentToolsOptions
    {
        RepoRoot = repoRoot ?? throw new ArgumentNullException(nameof(repoRoot)),
        Workspace = workspace ?? WorkspaceInfo.None,
        Mode = "local",
        AllowFolderBaselines = true,
        LogFailure = async (tool, failure, ct) =>
        {
            await log.WriteLineAsync($"maquettiste mcp: {tool} failed: {failure}".AsMemory(), ct).ConfigureAwait(false);
            await log.FlushAsync(ct).ConfigureAwait(false);
        },
    });

    /// <summary>The options bodies are written with: <see cref="JsonSerializerDefaults.Web"/> and nothing else, as the API's.</summary>
    public JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Saves an element if its file still has the expected hash (saveElement).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="element">The whole document.</param>
    /// <param name="expectedHash">The hash the caller read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result.</returns>
    [McpServerTool(Name = "save_element", Title = "Save element", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Replaces an element with a whole document if the file still has expectedHash. A conflict writes nothing and returns the disk version (current, hash) and the submitted document; an invalid document returns the diagnostics. A rename moves the file in the same save. facts lists the migration facts the save recorded in the same write (a renamed, retyped or dropped table, column or sequence, a column made not null): {id, database, fact, state, missing, summary, path}; a pending one's MQ4101 is in diagnostics: complete it with save_element (conversion, dataCheck, acknowledge or value) or hold it. A custom element (kind custom, under model/custom/<type>/) names its custom kind in type and holds the declared fields under content, as get_schema custom:<type> describes; the engine mints the id of each member (an item of an x-member array) sent without one, and values may name them as @custom:<type>.<name> and @member:<type>.<name>.<member>, a package path before the name when needed (@custom:setting.Billing.due_days). Expressions: wherever the expression language takes a tree (a table, column or entity check's condition, a derived attribute's derived.value, a process or operation guard's condition, a constraint's condition), {\"text\":\"issuedOn <= dueOn\"} is accepted and stored as a tree of ids, parsed in that site's scope, so renames follow it; text that does not parse refuses the write with MQ3101 at its pointer. Try text first with preview_expression.")]
    public Task<CallToolResult> SaveElement(
        [Description("The element id; the id in the document must be the same; required.")] string? id = null,
        [Description("The whole element document (the json from get_element, edited); required.")] JsonElement? element = null,
        [Description("The hash returned by get_element (or by the last save); required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: read the element with get_element first.");
        if (DocumentBytes(element) is not { } body)
            return BadRequest("element is required: a JSON object (the whole element document).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _store.SaveAsync(id, body, expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, element);
    }, ct);

    /// <summary>Patches an element if its file still has the expected hash (next-phase-1-design.md section 5.2).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="patch">The RFC 6902 patch: an array, or a string holding one.</param>
    /// <param name="expectedHash">The hash the caller read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result with the batch's ids and facts.</returns>
    [McpServerTool(Name = "patch_element", Title = "Patch element", Destructive = true, OpenWorld = false)]
    [Description("Changes part of an element with an RFC 6902 JSON Patch instead of the whole document, if its file still has expectedHash. The patch is an array of operations applied in order to the file document as get_element returns it in json ($schema included), all or nothing: {\"op\":\"add\",\"path\":\"/columns/-\",\"value\":{...}}, {\"op\":\"remove\",\"path\":\"/attributes/2\"}, {\"op\":\"replace\",\"path\":\"/description\",\"value\":\"...\"}, {\"op\":\"move\",\"from\":...,\"path\":...}, {\"op\":\"copy\",\"from\":...,\"path\":...} and {\"op\":\"test\",\"path\":...,\"value\":...} (numbers compare by value, so 1 and 1.0 are equal). The result is saved like save_element: conflict check, validation, renames and migration facts. Values may name other elements as apply_batch does (@table:billing.notes), and the engine mints the id of a new sub-element sent without one (keep the ids of the ones you move or re-send: dropping one and adding one with the same name and no id is MQ1014). An unknown op, a path or from that does not resolve, an index out of range, a failed test or a change to /id or /kind is invalid with MQ1016 at /<operation index>, and nothing is written. The answer is save_element's (hash, changes, diagnostics, facts: the migration facts the save recorded) plus ids ({names, minted}, as apply_batch). A conflict returns the disk version (current, hash) and the submitted patch. A custom element (kind custom, under model/custom/<type>/) names its custom kind in type and holds the declared fields under content, as get_schema custom:<type> describes; the engine mints the id of each member (an item of an x-member array) sent without one, and values may name them as @custom:<type>.<name> and @member:<type>.<name>.<member>, a package path before the name when needed (@custom:setting.Billing.due_days).")]
    public Task<CallToolResult> PatchElement(
        [Description("The element id (an id, not a qualified name); required.")] string? id = null,
        [Description("The RFC 6902 patch: an array of operations, or a string holding one; required.")] JsonElement? patch = null,
        [Description("The hash returned by get_element (or by the last save); required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: read the element with get_element first.");
        // Any JSON value goes to the engine, which refuses a patch that is not an array of operations (MQ1016 at "").
        var text = patch?.ValueKind switch
        {
            null or JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => patch.Value.GetString(),
            _ => patch.Value.GetRawText(),
        };
        if (text is null)
            return BadRequest("patch is required: an array of RFC 6902 operations, or a string holding one.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _store.PatchAsync(id, Encoding.UTF8.GetBytes(text), expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false);
        var answer = Node(result.Items[0] with { Facts = result.Outcome == SaveOutcome.Saved ? result.Facts : null })!;
        answer["ids"] = JsonSerializer.SerializeToNode(result.Ids, JsonOptions);
        if (result.Outcome == SaveOutcome.Conflict)
            answer["submitted"] = Submitted(text);
        return FromOutcome(result.Outcome, answer, null);
    }, ct);

    /// <summary>Creates an element (createElement).</summary>
    /// <param name="element">The document.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result.</returns>
    [McpServerTool(Name = "create_element", Title = "Create element", Destructive = false, OpenWorld = false)]
    [Description("Creates an element from a whole document; an id is assigned when the document has none. Returns the id, the hash, what changed and the migration facts the write recorded (facts, as save_element), or the diagnostics. A custom element (kind custom, under model/custom/<type>/) names its custom kind in type and holds the declared fields under content, as get_schema custom:<type> describes; the engine mints the id of each member (an item of an x-member array) sent without one, and values may name them as @custom:<type>.<name> and @member:<type>.<name>.<member>, a package path before the name when needed (@custom:setting.Billing.due_days). Expressions: wherever the expression language takes a tree (a table, column or entity check's condition, a derived attribute's derived.value, a process or operation guard's condition, a constraint's condition), {\"text\":\"issuedOn <= dueOn\"} is accepted and stored as a tree of ids, parsed in that site's scope, so renames follow it; text that does not parse refuses the write with MQ3101 at its pointer. Try text first with preview_expression.")]
    public Task<CallToolResult> CreateElement(
        [Description("The element document, for example {\"kind\":\"entity\",\"name\":\"Coupon\",\"package\":\"<package id>\",\"attributes\":[...]}. Routines, database types, SQL objects and queries belong to a database: {\"kind\":\"routine\",\"name\":\"invoice_total\",\"database\":\"<database id>\",\"body\":{\"postgresql\":\"...\"}}; a query is a JSON tree, {\"kind\":\"query\",\"name\":\"OpenInvoices\",\"database\":\"<database id>\",\"entity\":\"<entity id>\",\"from\":{\"source\":\"<table id or key>\",\"alias\":\"i\"},\"select\":[{\"attribute\":\"<attribute id>\",\"expression\":{\"column\":\"i.id\"}}]}. Use get_schema for the fields of a kind; required.")] JsonElement? element = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (DocumentBytes(element) is not { } body)
            return BadRequest("element is required: a JSON object (the whole element document).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _store.CreateAsync(body, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }, ct);

    /// <summary>Deletes an element (deleteElement), or with <paramref name="dryRun"/> says what the delete would do (getDeletePlan).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="expectedHash">The hash the caller read.</param>
    /// <param name="resolution">What to do with references.</param>
    /// <param name="dryRun">Return the delete plan and write nothing.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The delete result, or the plan.</returns>
    [McpServerTool(Name = "delete_element", Title = "Delete element", Destructive = true, OpenWorld = false)]
    [Description("Deletes an element if its file still has expectedHash. While other elements reference it, the delete is refused (code referenced, with the referrers) unless resolution says otherwise: remove-references clears optional references (a required one makes the delete invalid, with an MQ2001 diagnostic naming it); delete-dependents also resolves required references in the same change, removing the part of the referrer that needs the element (an attribute whose type is deleted, a diagram member, a key) or deleting the referrer with its own dependents (the tables, views, sequences, routines, database types, SQL objects, queries and mappings of a database; the relations, mappings and overlays of an entity; a query that reads a deleted table, view or entity, deleted whole), and removes a deleted database's databases.<name> conventions. A custom property value an x-ref types is a reference too: remove-references clears it when its x-ref says optional (else refuses), delete-dependents also clears a required one and keeps the element that holds it, and either refuses (MQ2001 naming the property and the schema) when a schema declaring the property would then fail (it is required, or an array drops below minItems). A pack parameter value in maquettiste.json that names the element (or a sub-element it takes along) refuses a plain delete (MQ2001 at the value, no element id); remove-references removes an optional one and delete-dependents any in the same change (listed under settings), unless the pack's parameterSchema requires the parameter. facts lists the migration facts the delete recorded (a dropped table, column or sequence; cascades included), as save_element. With dryRun true nothing is written and no expectedHash is needed: the result is the plan, {ids, resolution, outcome, deletes:[{id,kind,name,path,because}], clears:[{id,kind,name,pointer,field,target,because}], removes:[{id,kind,name,pointer,what,subId,subKind,because}], refused:[{id,kind,name,pointer,why,rule}], settings:[{pointer,what}], warnings:[{message,pack,unit}]}; read it before deleting with delete-dependents.")]
    public Task<CallToolResult> DeleteElement(
        [Description("The element id; required.")] string? id = null,
        [Description("The hash returned by get_element; required unless dryRun.")] string? expectedHash = null,
        [Description("refuse (default), remove-references or delete-dependents.")] string? resolution = null,
        [Description("true: return what the delete would do (with resolution, default delete-dependents) and write nothing.")] bool? dryRun = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        DeleteResolution mode;
        switch (resolution)
        {
            case null or "":
                mode = dryRun == true ? DeleteResolution.DeleteDependents : DeleteResolution.Refuse;
                break;
            case "refuse":
                mode = DeleteResolution.Refuse;
                break;
            case "remove-references":
                mode = DeleteResolution.RemoveReferences;
                break;
            case "delete-dependents":
                mode = DeleteResolution.DeleteDependents;
                break;
            default:
                return BadRequest($"resolution must be 'refuse', 'remove-references' or 'delete-dependents', not '{resolution}'.");
        }

        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (dryRun == true)
        {
            if (await _store.GetElementAsync(id, ct).ConfigureAwait(false) is not { } document || document.Element.Id != id)
                return NotFound("element", id);
            return Ok(await _store.GetDeletePlanAsync([id], mode, ct).ConfigureAwait(false));
        }

        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: read the element with get_element first.");
        var result = await _store.DeleteAsync(id, expectedHash, mode, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }, ct);

    /// <summary>Applies creates, updates and deletes all or nothing (applyBatch).</summary>
    /// <param name="operations">The operations.</param>
    /// <param name="dryRun">Answer what the batch would do and write nothing.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The batch result, or the dry run's answer.</returns>
    [McpServerTool(Name = "apply_batch", Title = "Apply batch", Destructive = true, OpenWorld = false)]
    [Description("Applies several operations all or nothing. Each operation is {\"op\":\"create\",\"element\":{...}}, {\"op\":\"update\",\"id\":...,\"expectedHash\":...,\"element\":{...}}, {\"op\":\"upsert\",\"id\":\"@kind:path\",\"element\":{...}} (creates the element when the name is new, else replaces it, no expectedHash needed; never a seed, and the name must be one the element will have; an element that exists sends its parts' ids) or {\"op\":\"delete\",\"id\":...,\"expectedHash\":...,\"resolution\":\"refuse|remove-references|delete-dependents\"} (resolution as in delete_element; default refuse). Database schemas: {\"op\":\"add-schema\",\"id\":<database>,\"name\":...}, {\"op\":\"rename-schema\",\"id\":<database>,\"schema\":<schema id>,\"name\":...}, {\"op\":\"set-default-schema\",\"id\":<database>,\"schema\":...} and {\"op\":\"remove-schema\",\"id\":<database>,\"schema\":...,\"target\":<schema id to move its tables to>,\"default\":<new default when removing the default>}; a remove that would strand tables is refused with MQ4015 listing them. Materialize (entities own their bindings; preview with preview_materialize): {\"op\":\"materialize-tables\",\"database\":<database>,\"entities\":[...],\"schema\":<optional schema id>} writes a designed table per entity with its projection's shape and binds the entity to it, {\"op\":\"materialize-entities\",\"database\":<database>,\"tables\":[<tables or views>],\"package\":<package>} writes an entity per table bound to it, {\"op\":\"materialize-attributes\",\"database\":<database>,\"entities\":[...],\"columns\":[<optional, one entity: column keys or names>]} adds to each bound entity an attribute per column of its binding's source that nothing in the binding names (or per column named), mapped to it, {\"op\":\"materialize-columns\",\"database\":<database>,\"entities\":[...],\"attributes\":[<optional, one entity: attribute ids>]} adds to each bound entity's table a column per attribute its binding leaves unmapped, shaped as the projection would make it, mapped to it; an entity or table already bound is refused with MQ4055, as is materialize-attributes for an entity with no binding, a query source or a column already mapped, and materialize-columns for a source that is not a table file or an attribute already mapped. Every materialize operation that writes a binding's fields records the binding's provenance: what its table file's columns looked like (see get_binding_drift). Bindings (preview with preview_materialize): {\"op\":\"map-by-name\",\"database\":<database>,\"entities\":[...]} maps every attribute a binding leaves unmapped to a free column of the same loose name (case and every character but letters and digits ignored; attributeMember for a value object member, a to-one navigation's name plus Id) and records provenance; {\"op\":\"resync-binding\",\"database\":<database>,\"entities\":[...],\"columns\":[<optional, one entity: column keys or names>],\"keepNames\":<default false>} brings each binding into step with its table file in one batch: an attribute that followed its column's old name takes the new one (keepNames keeps every name), a retyped column retypes its attribute unless its type still fits, unmapped attributes map by name, an attribute is added per added column, the fields of removed columns go (attributes stay), and provenance is recorded again; attribute ids never change. Both are refused with MQ4089 for an entity with no binding to the database, a source they cannot read, misused columns, or nothing to map or re-sync. Fold into a stereotype (adopt a stereotype's members on tables that already hold them): {\"op\":\"fold-into-stereotype\",\"id\":<stereotype>,\"tables\":[<designed or imported tables>] or \"schema\":<schema id: every table file of its database in it>,\"redefine\":<default false>,\"excludeMissing\":<default false>} makes each table carry the stereotype; own columns that match a member column by name, and own keys, indexes and checks that match a member by shape, leave the table file, and every reference to them (keys, indexes, seeds, binding fields, queries) names the inherited copy's keyed id <tableId>.<memberId>. A facet that differs (nullable, a default, a comment, a collation, a constraint's name, a primary key's columns, a foreign key's actions) becomes a redefines entry with redefine; a member the table lacks joins excludes with excludeMissing; a table that matches nothing is skipped. A type difference, another difference without redefine, annotations the member lacks, a synthesized table (materialize it first), a table that excludes the stereotype, or another operation of the batch writing the stereotype or a target refuses with one MQ4077 per table, and nothing is written. The write records one rekey fact per database (its column moves, then the index, unique, foreign-key and check ids it moved) and no database step. Processes: {\"op\":\"sync-enum\",\"id\":<lifecycle process>} makes the bound enum's members the process's root-level states in order (kept members keep their ids, codes and descriptions; removing a member a default, allowed values, a seed cell or a scenario value still uses is refused with MQ9019: change those uses first), {\"op\":\"set-lifecycle\",\"id\":<entity>,\"target\":<process>} binds the entity and the process to each other (omit target to clear the lifecycle), {\"op\":\"set-initial\",\"id\":<process or compound state>,\"target\":<direct child state>}, {\"op\":\"refresh-scenario\",\"id\":<scenario>} (rewrites its expectations and outcome from the engine's replay). Tags (see tag_usage): {\"op\":\"retag\",\"tags\":[...],\"name\":<new key, renames the one tag>,\"package\":<domain, else the global vocabulary>} removes the tags (or renames one) in that vocabulary and in every use it governs; {\"op\":\"tags-to-property\",\"prefix\":<tag prefix such as status:>,\"name\":<custom property>,\"package\":<domain, else the global vocabulary>,\"elementIds\":[<optional: only these elements>]} moves the tags that start with prefix into that property of each element and sub-element holding them, converted by the schema that declares it (a string takes one suffix, a string array all of them in order, an integer, number or boolean one, an enum one member, an x-ref the one id the suffix names); the tags leave their holders and, without elementIds, their declarations leave the vocabulary unless something without properties (a relation end) still uses them; no matching tag is MQ1002, and each holder whose property no schema declares, whose suffix does not fit or that already holds another value is one MQ5001. Typed links: {\"op\":\"names-to-ids\",\"elementIds\":[<optional: only these elements>]} turns every custom property value an x-ref types that is still a name into the one id it finds (elements by name or longer qualified form, stereotypes by key, within the of scope sub-elements by name, columns by physical name and rows by code), in those elements or in every document with x-ref sites, stereotype defaults included; a value that finds nothing or several is one MQ2010 at its file and pointer, and nothing to convert writes nothing. Reference-type refactors (codes are the code's text; one refactor per type per batch): {\"op\":\"rename-code\",\"id\":<reference type>,\"code\":<live code>,\"to\":<new code>} gives the row the new code and rewrites every default, allowedValues entry, seed cell, typed column default, table seed cell and binding constant that named the old one; {\"op\":\"retire-code\",\"id\":<reference type>,\"code\":<live code>,\"to\":<another live code>} removes the code's row and its translations, rewrites its uses to the other code and the ids that named the row (x-ref values, former codes) to the other row; {\"op\":\"merge-reference-types\",\"id\":<type merged away>,\"target\":<type merged into>,\"codes\":{<optional: code of id -> live code of target>}} maps each code the target holds to it, the codes map others, moves every other row into the target with its id, values and translations, retargets the seeds (a seed with the name of a target seed joins it), points every reference to the type at the target and deletes it; {\"op\":\"rename-reference-type\",\"id\":<reference type>,\"name\":<new name>} renames it, its file and its seed folder following. Each keeps the old code as a former code ({code, replacedBy: the row that holds its value now}), which imports, validation and generated code still understand, and its item answers usage: {useCount, uses (the first 200, by element and pointer), rowsMoved, rowsRemoved, translationsRemoved}, in a dry run and on apply. A refusal (an unknown or scripted code, a code taken or former, a code that does not fit, mismatched code types, a binding or scripted rows on a merged type, a field the target lacks, seeds of one name that differ in environments or apply, a second refactor of a type) is MQ7020 at the operation. An element may appear in one operation only. When one fails nothing is written and the result says which and why. Names instead of ids: every id field (id, database, package, schema, target, default, the items of entities, tables, attributes and elementIds, the keys of expectedHashes, and the references inside an element) may hold a qualified name, @kind:path with the name as stored (@table:billing.notes, @column:billing.notes.body, @entity:Billing.Invoice, @attribute:Invoice.total; a longer form qualifies an ambiguous name), or a placeholder: \"as\":\"$pm\" on a create or update, or on a sub-element of its element, names it for the whole batch, so later and earlier operations can write \"$pm\" where its id goes. An update's or delete's id names an element that exists before the batch; every other name sees the model as the earlier operations leave it. The engine mints every missing id (a create's element, attributes, columns, members, rows...), so send no ids for new things; an update that drops a sub-element and adds one without an id under the same parent with the same name is refused (MQ1014): keep the ids of sub-elements you re-send. A name that finds nothing is MQ1012, one that finds several is MQ1013 (listing each candidate), a misplaced placeholder or as is MQ1014, a malformed name is MQ1015. The answer adds ids: {names: {\"<name or placeholder>\": id}, minted: [{operation, pointer, kind, id, name, placeholder}]}. facts on the batch answer lists the migration facts the batch recorded (its items carry none), as save_element. dryRun true writes nothing and answers {outcome, items, files: [{path, action, id, kind, name, beforeHash, diff}], diagnosticsAdded, diagnosticsRemoved, ids, facts}: what an apply would answer, a unified diff per file, the diagnostics the change would add and remove over the elements it touches, the ids it would mint (minted again on apply), and the facts it would record (a fold's rekey among them; null when the change reaches every database); it also answers impact: {referrers: [{target, change, fromKind, fromName, reference}], referrersTotal, databases: [{id, name}], packs: [{pack, state: known|none|invalid, units: [{key, change: inputs|deleted|added, keys, outputs, filtered}], unitsTotal}]}, the elements that point at what the batch changes or deletes, the databases it can reach and per pack the units whose stored inputs it touches with the files they would write again (nothing is rendered: plan with elements or units after applying shows the bytes); read it before applying. A custom element (kind custom, under model/custom/<type>/) names its custom kind in type and holds the declared fields under content, as get_schema custom:<type> describes; the engine mints the id of each member (an item of an x-member array) sent without one, and values may name them as @custom:<type>.<name> and @member:<type>.<name>.<member>, a package path before the name when needed (@custom:setting.Billing.due_days). Expressions: wherever the expression language takes a tree (a table, column or entity check's condition, a derived attribute's derived.value, a process or operation guard's condition, a constraint's condition), {\"text\":\"issuedOn <= dueOn\"} is accepted and stored as a tree of ids, parsed in that site's scope, so renames follow it; text that does not parse refuses the write with MQ3101 at its pointer. {\"op\":\"text-to-trees\",\"elementIds\":[<optional: only these elements>]} turns expressions files hold as text (MQ3108) into trees, in those elements or in every document; text that does not parse refuses it with MQ3101 at its file and pointer. Try text first with preview_expression.")]
    public Task<CallToolResult> ApplyBatch(
        [Description("The operations, applied in order; required.")] JsonElement? operations = null,
        [Description("true: write nothing and answer what the batch would do (files with diffs, the diagnostics added and removed, ids, facts, impact). Default false.")] bool? dryRun = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (operations is not { ValueKind: JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.String } ops)
            return BadRequest("operations is required: an array of operations (or a batch document with an operations array).");
        var parsed = _store.ParseBatch(BatchOutput.Document(ops.ValueKind == JsonValueKind.String ? ops.GetString() ?? string.Empty : ops.GetRawText()));
        if (parsed.Batch is null)
            return Problem("invalid", 422, "The batch is not valid.", body: Node(parsed));
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (dryRun == true)
        {
            var preview = await _generation.PreviewBatchAsync(parsed.Batch, ct).ConfigureAwait(false);
            return FromOutcome(preview.Outcome, BatchOutput.Preview(preview), null);
        }

        var result = await _store.ApplyBatchAsync(parsed.Batch, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }, ct);

    /// <summary>Translation entries of one locale (getTranslations).</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="owner">An owner element id.</param>
    /// <param name="shard">A shard repo path.</param>
    /// <param name="missing">Only the entries that need work, paged.</param>
    /// <param name="cursor">The cursor of the page.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entries.</returns>
    [McpServerTool(Name = "get_translations", Title = "Get translations", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Translation entries of one locale: id, owner, field (displayName, pluralName, label, description, properties/<name> for the text of an x-translatable custom property, or content/<name> for an x-translatable content key of a custom element or a member), source text, translation, effective text, state (translated, missing, stale, fallback), shard and shardHash (pass the hashes as expected to set_translations). Filter by owner or shard; missing=true pages the entries that need work, 200 at a time, with a cursor.")]
    public Task<CallToolResult> GetTranslations(
        [Description("A declared locale other than the default, for example fr; required.")] string? locale = null,
        [Description("Only the entries of this element and its sub-elements (seed rows belong to their seed).")] string? owner = null,
        [Description("Only the entries of this shard (its repo path).")] string? shard = null,
        [Description("Only the entries that are missing, fallback or stale, paged.")] bool missing = false,
        [Description("The cursor from the previous page.")] string? cursor = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(locale))
            return BadRequest("locale is required.");
        if (!await _store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return Problem("not-found", 404, $"'{locale}' is not a declared locale other than the default.");
        return Ok(await _store.GetTranslationRowsAsync(locale, owner, shard, missing, cursor, ct).ConfigureAwait(false));
    }, ct);

    /// <summary>Writes translations of one locale (putTranslations).</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="entries">The edits.</param>
    /// <param name="expected">Shard path to the hash read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    [McpServerTool(Name = "set_translations", Title = "Set translations", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Writes, removes (value null) or confirms (confirm true: the default text changed but the translation still holds) translations of one locale in one atomic save. entries: [{\"id\",\"field\",\"value\",\"confirm\"}], field being displayName, pluralName, label, description, properties/<name> (an x-translatable custom property) or content/<name> (an x-translatable content key of a custom element or a member, whose id is the member's own); expected: {\"<shard path>\": \"<shardHash from get_translations>\"}. A changed shard is a conflict (nothing written); an unknown id or field is invalid.")]
    public Task<CallToolResult> SetTranslations(
        [Description("A declared locale other than the default; required.")] string? locale = null,
        [Description("The edits: [{id, field, value (text or null), confirm?}]; required.")] JsonElement? entries = null,
        [Description("Shard repo path to the shardHash read with get_translations.")] JsonElement? expected = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(locale))
            return BadRequest("locale is required.");
        if (entries is not { ValueKind: JsonValueKind.Array } list)
            return BadRequest("entries is required: an array of {id, field, value, confirm?}.");
        if (!await _store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return Problem("not-found", 404, $"'{locale}' is not a declared locale other than the default.");
        var edits = new List<TranslationEdit>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var id) || !entry.TryGetProperty("field", out var field)
                || id.ValueKind != JsonValueKind.String || field.ValueKind != JsonValueKind.String)
                return BadRequest("Each entry needs an id and a field.");
            var value = entry.TryGetProperty("value", out var v) ? v : default;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined))
                return BadRequest($"The value of {id.GetString()}/{field.GetString()} must be a string or null.");
            var confirm = entry.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True;
            edits.Add(new TranslationEdit(id.GetString()!, field.GetString()!, value.ValueKind == JsonValueKind.String ? value.GetString() : null, confirm));
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (expected is { ValueKind: JsonValueKind.Object } map)
        {
            foreach (var property in map.EnumerateObject())
                hashes[property.Name] = property.Value.GetString() ?? "";
        }

        var result = await _store.SaveTranslationsAsync(locale, edits, hashes, ChangeSource.Cli, ct).ConfigureAwait(false);
        var body = new { outcome = result.Outcome switch { SaveOutcome.Saved => "saved", SaveOutcome.Conflict => "conflict", _ => "invalid" }, hashes = result.ShardHashes, diagnostics = result.Diagnostics };
        return result.Outcome switch
        {
            SaveOutcome.Saved => Ok(body),
            SaveOutcome.Conflict => Problem("conflict", 409, "A shard changed since it was read; nothing was written. hashes holds the current hashes.", body: Node(body)),
            _ => Problem("invalid", 422, "The translations are not valid; nothing was written. See diagnostics.", body: Node(body)),
        };
    }, ct);

    /// <summary>A seed's rows as CSV (exportSeedCsv).</summary>
    /// <param name="id">The seed id.</param>
    /// <param name="bom">Byte order mark and CRLF.</param>
    /// <param name="locales">Locales whose label and description columns are added.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The CSV text.</returns>
    [McpServerTool(Name = "export_seed_csv", Title = "Export seed CSV", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A seed's rows as CSV text: @id, then @code, @label, @description for a reference type, then attribute names and end roles, or a table seed's column names and, for a foreign-key entry, the foreign key's name (its id when it has none) with the referenced row's natural key as the cell (a composite key as JSON array text), then @label:<locale> and @description:<locale> for each requested locale. A seed whose rows a script computes (rowsFrom.script) exports the rows the script returns, with their derived @id (the same on every call); a script that fails is invalid with its MQ7127 in diagnostics.")]
    public Task<CallToolResult> ExportSeedCsv(
        [Description("The seed id; required.")] string? id = null,
        [Description("Adds a byte order mark and CRLF line ends, for spreadsheet programs.")] bool bom = false,
        [Description("Translated locales whose label and description columns are added.")] string[]? locales = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        string? text;
        try
        {
            text = await _store.ExportSeedCsvAsync(id, bom, locales ?? [], ct).ConfigureAwait(false);
        }
        catch (Engine.Scripting.ScriptErrorException ex)
        {
            return Problem("invalid", 422, "The seed's rows script fails, so it has no rows to export. See diagnostics.",
                body: new JsonObject { ["diagnostics"] = JsonSerializer.SerializeToNode(new[] { ex.Diagnostic }, JsonOptions) });
        }

        return text is null ? NotFound("seed", id) : Text(text, isError: false);
    }, ct);

    /// <summary>Previews or applies a CSV import into a seed (importSeedCsv).</summary>
    /// <param name="id">The seed id.</param>
    /// <param name="csv">The CSV text.</param>
    /// <param name="mode">merge or replace.</param>
    /// <param name="apply">Applies instead of previewing.</param>
    /// <param name="expectedHash">The seed hash read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The preview.</returns>
    [McpServerTool(Name = "import_seed_csv", Title = "Import seed CSV", Destructive = true, OpenWorld = false)]
    [Description("Imports CSV rows into a seed: rows match by @id, else by @code for a reference type, else are new. mode merge (default) updates and adds; replace also removes rows the file leaves out, except rows other seeds reference (blocked). A foreign-key column (headed by the foreign key's name) takes the referenced row's natural key, a composite one as JSON array text. A dry run (the default) returns the preview (added, changed with before and after, removed, blocked, ignoredHeaders); apply true writes it with expectedHash. A seed whose rows a script computes (rowsFrom.script) takes no import: bad-request. A line whose @code is a former code of the reference type is skipped and listed in the preview's diagnostics with MQ7019, and the row that replaced the code counts as kept for replace.")]
    public Task<CallToolResult> ImportSeedCsv(
        [Description("The seed id; required.")] string? id = null,
        [Description("The CSV text, with a header line; required.")] string? csv = null,
        [Description("merge (default) or replace.")] string? mode = null,
        [Description("true writes the import; false or absent only previews it.")] bool apply = false,
        [Description("The seed's hash from get_element; a changed seed is a conflict.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        if (csv is null)
            return BadRequest("csv is required.");
        if (mode is not (null or "" or "merge" or "replace"))
            return BadRequest($"mode must be merge or replace, not '{mode}'.");
        ImportResult? result;
        try
        {
            result = await _store.ImportSeedCsvAsync(id, csv, mode == "replace", !apply, expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }

        return result is null ? NotFound("seed", id) : FromOutcome(result.Outcome, result.Preview, null);
    }, ct);

    /// <summary>Creates a reference type's seed with the code, label and description columns.</summary>
    /// <param name="type">The reference type id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result.</returns>
    [McpServerTool(Name = "create_seed", Title = "Create seed", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Creates the seed of a reference type that has none, named after the type, with the columns code, label and description and no rows (then import_seed_csv or save_element adds rows). Returns the seed id; a type that has a seed returns that seed's id and changes nothing. A seed created with create_element for a reference type without columns gets the same three.")]
    public Task<CallToolResult> CreateSeed([Description("The reference type id; required.")] string? type = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(type))
            return BadRequest("type is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _store.CreateSeedAsync(type, ChangeSource.Cli, ct).ConfigureAwait(false);
        return result is null ? NotFound("reference type", type) : FromOutcome(result.Outcome, result, null);
    }, ct);

    /// <summary>Saves <c>maquettiste.json</c> (saveSettings).</summary>
    /// <param name="settings">The whole settings document.</param>
    /// <param name="expectedHash">The hash the caller read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result.</returns>
    [McpServerTool(Name = "save_settings", Title = "Save project settings", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Replaces the project settings with a whole document if the file still has expectedHash. A conflict writes nothing and returns the disk version; an invalid document returns the diagnostics.")]
    public Task<CallToolResult> SaveSettings(
        [Description("The whole settings document (the json from get_settings, edited); required.")] JsonElement? settings = null,
        [Description("The hash returned by get_settings; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: read the settings with get_settings first.");
        if (DocumentBytes(settings) is not { } body)
            return BadRequest("settings is required: a JSON object (the whole maquettiste.json document).");
        var result = await _store.SaveSettingsAsync(body, expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, settings);
    }, ct);

    /// <summary>Rewrites model files in canonical form (formatModel).</summary>
    /// <param name="paths">Repo-relative model files; absent for every one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The files rewritten and skipped.</returns>
    [McpServerTool(Name = "format_model", Title = "Rewrite model files in canonical form", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rewrites model files in canonical form, as maquettiste format does, so their MQ1003 warnings go away (maquettiste.json also loses the retired commit flag, MQ1010). Content does not change; a file already canonical is untouched and a file that does not pass its schema is listed in skipped. A path that is not a model file is refused and nothing is written. Custom kind files (extensions/kinds/<type>.json) are rewritten too; other files under extensions/ are not.")]
    public Task<CallToolResult> FormatModel(
        [Description("Repo-relative model files (.maquettiste/maquettiste.json, .maquettiste/model/...); default: every model file.")] string[]? paths = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        var result = await _store.FormatAsync(paths, ChangeSource.Cli, ct).ConfigureAwait(false);
        return result.Refused.Count > 0
            ? BadRequest("Only model files can be formatted (maquettiste.json, model/**/*.json): " + string.Join(", ", result.Refused) + ".")
            : Ok(result);
    }, ct);

    /// <summary>Plans generation (startPlan, run to completion).</summary>
    /// <param name="packs">Pack names.</param>
    /// <param name="units">Units to run: <c>pack/unit</c> or a bare unit id.</param>
    /// <param name="elements">Element ids whose units run.</param>
    /// <param name="force">Whether to ignore the unit cache.</param>
    /// <param name="handEdits">A hand-edit policy.</param>
    /// <param name="jobs">Parallelism.</param>
    /// <param name="against">A baseline model to diff the schema from: a model snapshot id or a folder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan result, without the per-unit list.</returns>
    [McpServerTool(Name = "plan", Title = "Plan generation", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Plans generation without touching the repository: renders what changed and stores the plan. Returns the outcome and the plan (id, packs, file changes with their actions, counts of every kind, units rendered and skipped by reason, diagnostics). Files identical to the disk (unchanged) or not rendered because their inputs did not change (not-rendered) are only counted; get_plan lists every file. Read diffs with get_plan_diff, then apply with apply_plan. units and elements limit the plan to some units (values of one OR, the two AND): the apply writes only their files and leaves every other file and unit as it is, and still deletes the files those units no longer produce; an unknown or ambiguous unit is MQ6032 (invalid), a selected unit that reads the schema diff (sql-ddl/migration) is MQ6033 (invalid: migrations render only in a run without units and elements), a filter that selects nothing is the warning MQ6034. against diffs the schema (migrations) from a baseline model for this plan instead of the committed physical snapshots, which the apply then leaves as they are; apply_plan opens the same baseline again, and a baseline changed in between makes the plan stale.")]
    public Task<CallToolResult> Plan(
        [Description("Only these packs (default: every enabled pack).")] string[]? packs = null,
        [Description("Only these units: \"<pack>/<unit>\" (sql-ddl/schema), or a unit id that exactly one run pack has (default: every unit).")] string[]? units = null,
        [Description("Only the units of these elements, by id, never by name (a synthesized table's id is <entityId>@<databaseId>); units that run once for the whole model never match (default: every element).")] string[]? elements = null,
        [Description("Render every unit, ignoring the unit cache.")] bool force = false,
        [Description("fail, overwrite or skip: overrides the project's hand-edit policy.")] string? handEdits = null,
        [Description("Parallelism for this run (at least 1).")] int? jobs = null,
        [Description("A baseline model to diff the schema from: a model snapshot id (list_snapshots) or a folder that holds another copy of the project (relative to the server's current directory).")] string? against = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (jobs is < 1)
            return BadRequest("jobs must be at least 1.");
        if (!TryHandEdits(handEdits, out var policy))
            return BadRequest($"handEdits must be fail, overwrite or skip, not '{handEdits}'.");
        if (ScopeProblem(units, elements) is { } scope)
            return BadRequest(scope);

        var request = new GenerationRequest
        {
            Mode = GenerationMode.Apply,
            Packs = packs is { Length: > 0 } ? packs : null,
            Units = Scope(units),
            Elements = Scope(elements),
            Force = force,
            Jobs = jobs,
            HandEdits = policy,
            IncludeDiffs = false,
            StageBarriers = false,
            Lock = LockMode.Wait,
            // A relative folder is resolved against the server's current directory; the plan stores the full path (or the id).
            Against = against is { Length: > 0 }
                ? ModelBaseline.RequestText(Snapshots, against, Environment.CurrentDirectory)
                : null,
        };
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        PlanResult result;
        try
        {
            result = await _generation.PlanAsync(request, null, ct).ConfigureAwait(false);
        }
        catch (BadBaselineException ex)
        {
            return BadRequest(ex.Message); // only the baseline's own errors: any other argument error is not relabelled
        }

        // Files the apply leaves as they are because they are identical or were not rendered are only counted (counts), so the
        // answer stays small on a large model; get_plan lists every file.
        var summary = result with
        {
            Plan = result.Plan is null ? null : result.Plan with
            {
                Units = [],
                Changes = [.. result.Plan.Changes.Where(c => c.Kind is not (FileChangeKind.Unchanged or FileChangeKind.NotRendered))],
            },
        };
        if (summary.Plan is null)
            return FromRun(summary.Outcome, summary, ct);
        return Ok(summary);
    }, ct);

    /// <summary>Applies a stored plan (startApply, run to completion).</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The apply result.</returns>
    [McpServerTool(Name = "apply_plan", Title = "Apply plan", Destructive = true, OpenWorld = false)]
    [Description("Writes a stored plan's files without rendering again. Refused with code stale, writing nothing, when the model, the templates or a planned file changed since the plan (plan again). Returns the files written and deleted.")]
    public Task<CallToolResult> ApplyPlan([Description("The plan id returned by plan; required.")] string? planId = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (!IsUlid(planId))
            return BadRequest("planId must be a plan id (an uppercase ULID).");
        if (await _generation.GetPlanAsync(planId, ct).ConfigureAwait(false) is not { } plan)
            return NotFound("plan", planId);
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        ApplyResult result;
        try
        {
            result = await _generation.ApplyAsync(planId, null, ct).ConfigureAwait(false);
        }
        catch (BadBaselineException ex)
        {
            // A plan made against a baseline opens it again; one that no longer opens (a deleted snapshot) is the caller's to fix.
            return BadRequest("The plan's baseline no longer opens: " + ex.Message);
        }

        return result.Outcome == RunOutcome.Succeeded ? Ok(result) : FromRun(result.Outcome, result, ct);
    }, ct);

    /// <summary>Reads a <c>handEdits</c> argument: empty or absent is the project's policy.</summary>
    /// <param name="value">The argument.</param>
    /// <param name="policy">The policy, or <see langword="null"/> for the project's.</param>
    /// <returns><see langword="false"/> for a value that is not <c>fail</c>, <c>overwrite</c> or <c>skip</c>.</returns>
    private static bool TryHandEdits(string? value, out HandEditPolicy? policy)
    {
        policy = value switch
        {
            null or "" => null,
            "fail" => HandEditPolicy.Fail,
            "overwrite" => HandEditPolicy.Overwrite,
            "skip" => HandEditPolicy.Skip,
            _ => null,
        };
        return value is null or "" or "fail" or "overwrite" or "skip";
    }

    /// <summary>The problem with the <c>units</c> and <c>elements</c> filters of a scoped run, or <see langword="null"/>.</summary>
    /// <param name="units">The unit values.</param>
    /// <param name="elements">The element ids.</param>
    /// <returns>The detail of the <c>bad-request</c>, or <see langword="null"/>.</returns>
    private static string? ScopeProblem(string[]? units, string[]? elements) =>
        units?.Any(string.IsNullOrWhiteSpace) == true ? "units takes \"<pack>/<unit>\" or a unit id; an empty value names nothing."
        : elements?.Any(string.IsNullOrWhiteSpace) == true ? "elements takes element ids; an empty value names nothing."
        : null;

    /// <summary>A scoped run's filter values, each once in the order given; empty or absent is no filter.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The filter, or <see langword="null"/>.</returns>
    private static string[]? Scope(string[]? values) => values is { Length: > 0 } ? [.. values.Distinct(StringComparer.Ordinal)] : null;

    private CallToolResult Ok<T>(T value) => Text(JsonSerializer.Serialize(value, JsonOptions), isError: false);

    private static CallToolResult Text(string text, bool isError) => new() { Content = [new TextContentBlock { Text = text }], IsError = isError ? true : null };

    private JsonObject? Node<T>(T value) => JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject;

    /// <summary>A problem: the API's code, status and title, an optional detail, and the fields of the API's body for that status.</summary>
    private CallToolResult Problem(string code, int status, string title, string? detail = null, JsonObject? body = null)
    {
        var problem = new JsonObject { ["code"] = code, ["status"] = status, ["title"] = title };
        if (detail is not null)
            problem["detail"] = detail;
        if (body is not null)
        {
            foreach (var (name, value) in body.ToList())
            {
                if (!problem.ContainsKey(name))
                {
                    body.Remove(name);
                    problem[name] = value;
                }
            }
        }

        return Text(problem.ToJsonString(JsonOptions), isError: true);
    }

    /// <summary>The <c>bad-request</c> problem for an argument the server's binder would reject (see <see cref="McpServerSetup.ArgumentProblem"/>).</summary>
    /// <param name="detail">Which argument and why.</param>
    /// <returns>The tool error.</returns>
    internal CallToolResult ArgumentError(string detail) => BadRequest(detail);

    private CallToolResult BadRequest(string detail) => Problem("bad-request", 400, "The request is not valid.", detail);

    private CallToolResult NotFound(string what, string? id, string code = "not-found") => Problem(code, 404, $"No {what} has the id {id}.");

    /// <summary>A save, create, delete or batch result: the body when saved, else the problem for the outcome with the body's fields.</summary>
    private CallToolResult FromOutcome<T>(SaveOutcome outcome, T result, JsonElement? submitted)
    {
        if (outcome == SaveOutcome.Saved)
            return Ok(result);
        var body = Node(result);
        if (outcome == SaveOutcome.Conflict && submitted is { } mine && body is not null)
            body["submitted"] = JsonNode.Parse(mine.ValueKind == JsonValueKind.String ? mine.GetString() ?? "null" : mine.GetRawText());
        return outcome switch
        {
            SaveOutcome.Conflict => Problem("conflict", 409, "The file changed on disk since it was read; nothing was written. current and hash are the disk version; submitted is yours.", body: body),
            SaveOutcome.Invalid => Problem("invalid", 422, "The change is not valid; nothing was written. See diagnostics.", body: body),
            SaveOutcome.NotFound => Problem("not-found", 404, "No such element.", body: body),
            SaveOutcome.Referenced => Problem("referenced", 409, "Other elements reference this element; nothing was deleted. See referrers, or pass resolution remove-references or delete-dependents (delete_element with dryRun shows what each does).", body: body),
            _ => Problem("internal", 500, "Unexpected outcome.", body: body),
        };
    }

    /// <summary>
    /// A plan, apply or generate run that did not succeed: the problem for the run outcome with the result's fields. A run that writes
    /// as it renders (<c>generate</c>) passes its own <paramref name="invalidTitle"/>, since the units without errors may have written.
    /// </summary>
    private CallToolResult FromRun<T>(RunOutcome outcome, T result, CancellationToken ct, string? invalidTitle = null)
    {
        if (outcome == RunOutcome.Cancelled)
            ct.ThrowIfCancellationRequested();
        var body = Node(result);
        return outcome switch
        {
            RunOutcome.Stale => Problem("stale", 409, "The plan is stale: its inputs or planned files changed since it was made; nothing was written. Plan again.", body: body),
            RunOutcome.Invalid => Problem("invalid", 422, invalidTitle ?? "The model or the templates have errors; nothing was written. See diagnostics.", body: body),
            RunOutcome.Conflicts => Problem("conflicts", 409, "Generated files were edited by hand; see the changes and the hand-edit policy.", body: body),
            RunOutcome.Drift => Problem("drift", 409, "Generated files are stale, missing or orphaned.", body: body),
            RunOutcome.Busy => Problem("busy", 503, "Another generation run holds the run lock.", body: body),
            RunOutcome.Cancelled => Problem("cancelled", 409, "The run was cancelled.", body: body),
            _ => Problem("failed", 500, "The run failed.", body: body),
        };
    }

    /// <summary>Runs a tool body and turns an exception into a problem; a cancelled request stays cancelled.</summary>
    /// <remarks>
    /// Only the tool's own argument checks answer <c>bad-request</c>; an exception from the engine is the server's failure, so it
    /// is <c>internal</c> and one line goes to the log (as the API's handler logs it).
    /// </remarks>
    private async Task<CallToolResult> GuardAsync(Func<Task<CallToolResult>> body, CancellationToken ct, [CallerMemberName] string tool = "")
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Problem("model-unavailable", 503, "The model folder cannot be read.", e.Message);
        }
#pragma warning disable CA1031 // A tool never crashes the server: any other failure is the internal problem with its message.
        catch (Exception e)
#pragma warning restore CA1031
        {
            var name = typeof(ModelTools).GetMethod(tool)?.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? tool;
            await _log.WriteLineAsync($"maquettiste mcp: {name} failed: {e.GetType().FullName}: {e.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            await _log.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return Problem("internal", 500, "The server failed to answer this request.", e.Message);
        }
    }

    /// <summary>A submitted text as JSON when it parses, else as the string it is.</summary>
    private static JsonNode? Submitted(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>The UTF-8 bytes of a document argument: a JSON object, or a string holding one.</summary>
    private static byte[]? DocumentBytes(JsonElement? document)
    {
        var text = document?.ValueKind switch
        {
            JsonValueKind.Object => document.Value.GetRawText(),
            JsonValueKind.String => document.Value.GetString(),
            _ => null,
        };
        return text is null || !text.TrimStart().StartsWith('{') ? null : Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Whether a value is an uppercase ULID (26 Crockford base-32 characters).</summary>
    private static bool IsUlid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value)
    {
        if (value is not { Length: 26 })
            return false;
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'A' and <= 'Z' && c is not ('I' or 'L' or 'O' or 'U')))
                return false;
        }

        return true;
    }
}
