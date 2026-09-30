# Maquettiste over MCP

`maquettiste mcp` serves a repository's model to coding agents over the [Model Context Protocol](https://modelcontextprotocol.io)
on stdin and stdout (SPEC Section 18, "Agents"). It runs in-process over the engine: one `ModelStore` and one `GenerationService`
for the repository, the same write path the editor uses. An agent and the editor therefore see one model: the same documents,
hashes, validation, plans and error codes as the editor API (`docs/api/openapi.yaml`).

## Running it

```sh
maquettiste mcp                          # the repo found the way generate finds it
maquettiste --repo path/to/repo mcp      # an explicit repo
maquettiste --repo path/to/repo --cache-dir /tmp/mq-cache mcp
```

The repository is `--repo`, else the nearest ancestor of the current directory holding `.maquettiste/maquettiste.json`. The index and
plan cache is `--cache-dir`, else `$MAQUETTISTE_CACHE_DIR`, else the user cache folder, so the server shares plans and unit state with
`maquettiste generate` on the same repo. Without a model the command exits 1 with a message on stderr. Stdout carries JSON-RPC
messages only; the start line and any error go to stderr. The server stops (exit 0) when the client closes stdin, after answering
every request it read before the end (so `echo '<request>' | maquettiste mcp` prints the answer), or on Ctrl+C.

## Claude Code setup

### In a repository that uses Maquettiste

With the tool installed (`dotnet tool install -g Maquettiste.Cli`, or as a local tool in `.config/dotnet-tools.json`):

```sh
maquettiste init --agent-setup     # both of the following
maquettiste init --mcp             # .mcp.json: registers the server
maquettiste init --skill           # .claude/skills/maquettiste-modeling/SKILL.md: the modeling skill
```

`init --mcp` writes (or merges into) the project-scoped `.mcp.json` that Claude Code and other MCP clients read:

```json
{
  "mcpServers": {
    "maquettiste": {
      "type": "stdio",
      "command": "maquettiste",
      "args": [
        "mcp"
      ]
    }
  }
}
```

When the repository's local tool manifest lists the `maquettiste` command, the entry is `"command": "dotnet"`,
`"args": ["tool", "run", "maquettiste", "mcp"]` instead, so the pinned version runs. Every entry `init` writes names its transport, `"type": "stdio"`; an entry an older version wrote without it is kept as it is, so delete that entry and run `init --mcp` again to get the current form. There is no `--repo`: the client starts
the server in the project folder, the server finds the repository from there, and the file can be committed. Other servers
and members of an existing `.mcp.json` are kept in order; an existing `maquettiste` entry is never replaced; a file that is not
a JSON object (not valid JSON, duplicate keys, or an `mcpServers` that is not an object) is left alone with a hint and exit 0. `init --skill` writes the skill that ships with
the installed version and refreshes it when the tool is upgraded. Both files are setup writes of the engine's path policy
(a symbolic link that leads out of the repository is refused, exit 4). Without init, `claude mcp add maquettiste -- maquettiste mcp`
registers the server for the current user only.

Start `claude` in the repository, approve the project server when asked (or check with `/mcp`), and ask for a model change:
the skill tells the agent to use the `mcp__maquettiste__*` tools and to fall back to file edits only without them.

### From the Docker image (no .NET on the machine)

The image `mattjcowan/maquettiste` carries the CLI (`/usr/local/bin/maquettiste`), so a machine with only Docker can run the
server. `init` writes the setup for it:

```sh
maquettiste init --mcp --docker mattjcowan/maquettiste:<tag>     # add --skill for the modeling skill
# created mcp.sh (runs maquettiste mcp in mattjcowan/maquettiste:<tag>; log in .maquettiste/.cache/mcp.log)
# created .mcp.json (server maquettiste: ./mcp.sh)
```

`.mcp.json` then registers the wrapper as a project server:

```json
{
  "mcpServers": {
    "maquettiste": {
      "type": "stdio",
      "command": "./mcp.sh",
      "args": []
    }
  }
}
```

and `mcp.sh` (executable, safe to commit) runs, from its own folder:

```sh
docker run -i --rm --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo \
  -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli mattjcowan/maquettiste:<tag> maquettiste mcp 2>> .maquettiste/.cache/mcp.log
```

- `-i` keeps stdin open (the JSON-RPC stream); there is no `-t`, a terminal would mix control characters into stdout.
- `--user` runs the server as you, so the model files it writes are yours: right under Docker, on Linux and on the Mac.
  Under rootless Podman (`MAQUETTISTE_DOCKER=podman`), root in the container is already you and any other uid cannot write
  the mounted folder: remove `--user "$(id -u):$(id -g)"` from `mcp.sh`, or replace it with `--userns=keep-id`. A later
  `init --docker` rewrites the script, so repeat the edit after one.
- No `--repo` and no absolute path: the script mounts its own folder, and the server finds the repository there. The file works
  for everyone who clones the repository.
- Stdout carries the protocol only; the server's messages (its start line, errors) are appended to `.maquettiste/.cache/mcp.log`,
  which `init` keeps out of git. Look there first when the client reports the server as failed.
- An MCP client may start the script with a short `PATH` (an app started from the Dock); the script looks for `docker` on the
  `PATH`, then in the usual install folders (`/usr/local/bin`, `/opt/homebrew/bin`, `~/.docker/bin`, the Docker app bundle).
  `MAQUETTISTE_DOCKER` names it explicitly; `MAQUETTISTE_IMAGE` overrides the image without editing the file.
- The index and plan cache lives in `.maquettiste/.cache/cli`, so it survives the container of each session.
- A re-run with another tag (`init --docker mattjcowan/maquettiste:<new tag>`; `--docker` implies `--mcp`) refreshes the script
  and keeps the entry. A `mcp.sh` that `init` did not write is never replaced, and an existing `maquettiste` entry in `.mcp.json` is
  kept (remove it to switch forms).

Without the script, the same command works for `claude mcp add` (for the current user only):

```sh
claude mcp add maquettiste -- docker run -i --rm --user "$(id -u):$(id -g)" -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste mcp
```

Checked on Linux (Docker Engine, amd64) with a stdio client over `docker run -i --rm --user ... maquettiste mcp`: `initialize`
in 0.7 s, `tools/list` with 18 tools (image 0.1.0; the published 0.2.0 image lists 24, the current source 40), `validate` in 55 ms, the container removed on exit. The wrapper's shape (arguments, stderr
to the log, stdout untouched) is covered by the CLI tests.

### In this repository

The root `.mcp.json` runs the CLI from source against `tmp/billing`, the gitignored copy of the billing fixture that
`docker/dev-billing.sh` serves in the editor, so an agent and the editor work on the same model:

```json
{ "mcpServers": { "maquettiste": { "type": "stdio", "command": "dotnet",
  "args": ["run", "--project", "src/Maquettiste.Cli", "-c", "Release", "--", "--repo", "tmp/billing", "mcp"] } } }
```

Create the copy first with `docker/dev-billing.sh`, or without Docker, then build once so the first start is quick
(`dotnet run` prints nothing on stdout, so the JSON-RPC stream stays clean):

```sh
mkdir -p tmp/billing && cp -r tests/fixtures/models/billing/.maquettiste tmp/billing/
dotnet build src/Maquettiste.Cli -c Release
claude                               # then /mcp shows maquettiste connected with 40 tools
```

A headless check that needs no approval prompt (an explicit `--mcp-config` is trusted):

```sh
claude -p "Call mcp__maquettiste__get_model_index with kind entity and reply with only the entity names." \
  --mcp-config .mcp.json --strict-mcp-config --allowedTools mcp__maquettiste__get_model_index
# Customer, InvoiceLine, Invoice, Payment, Product
```

The skill is in `skills/maquettiste-modeling/` here; the server also serves it as a resource and a prompt (below).

### Tried end to end

`tests/Maquettiste.Cli.Tests/AgentSetupTests.cs` drives the server with the SDK's MCP client exactly as `init --mcp`
registers it (the registered arguments only, started in the repository folder) over a temp copy of the billing fixture:
it lists the tools, finds `Invoice` with `get_model_index`, reads it with `get_element`, renames the attribute `total` to
`amountDue` with `save_element` and the hash it read, checks the returned diagnostics, sees the old hash refused as a
`conflict`, runs `validate` (0 errors), then `plan` and `get_plan_diff` on `src/Generated/Invoice.g.cs`, which shows the new
name while nothing is written to the repository.

## Tools

Every tool is a thin wrapper over one engine call, named after the API operation it mirrors. A success is one text block holding
the operation's JSON body, serialized like the API's (`JsonSerializerDefaults.Web`: camelCase, enums as their JSON names).

| Tool | API operation | Arguments | Returns |
| --- | --- | --- | --- |
| `get_project` | getProject | | name, versions, settings and `settingsHash`, databases, packs, pack diagnostics, extensions (`mode` is `local`, `git` is null) |
| `get_model_index` | getModelIndex | `kind`, `package` (id or name), `tag`, `category`, `stereotype`, `query` (name contains, ignoring case), all optional, AND | element summaries |
| `get_element` | getElement | `id` | the document: `json` (canonical), `hash`, `path`, the typed element, the sidecar text |
| `save_element` | saveElement | `id`, `element` (whole document), `expectedHash` | the save result (new `hash`, changes) |
| `create_element` | createElement | `element` (an id is assigned when absent) | the save result with the new `id`; write a database with `byConvention` (`none`, `packages` or `all`): without it a database with no `packages` takes every entity (the rule from before 0.3.0) |
| `delete_element` | deleteElement | `id`, `expectedHash`, `resolution` (`refuse` default, or `remove-references`) | the save result |
| `apply_batch` | applyBatch | `operations` (the batch's `operations` array, or the whole `{ "operations": [...] }` body); besides create, update and delete, the database schema operations `add-schema`, `rename-schema`, `remove-schema` (with `target` to move what lives there, `default` when removing the default) and `set-default-schema` | the batch result, all or nothing |
| `get_references` | getReferences | `id` | where the element is used |
| `localization_status` | getLocalizationStatus | | `defaultLocale`, `declared`, and per translated locale its `chain` and per shard `expected`, `translated`, `missing`, `stale` |
| `get_translations` | getTranslations | `locale`, `owner` or `shard` (optional), `missing` (the entries that need work, 200 per page), `cursor` | `{ entries, cursor }`: per field the `source`, `translation`, `effective` text, `state` (`translated`, `missing`, `stale`, `fallback`), `shard` and `shardHash` |
| `set_translations` | putTranslations | `locale`, `entries` (`[{ id, field, value, confirm? }]`, `value` null removes), `expected` (`{ "<shard path>": "<shardHash>" }`) | `{ outcome, hashes, diagnostics }`; a changed shard is `conflict`, an unknown id or field `invalid` |
| `create_seed` | (CLI `seed new`) | `type`: a reference type id | the seed id: a new seed named after the type with the columns `code`, `label` and `description` and no rows, or the seed the type already has (nothing changes). `create_element` of a seed for a reference type without `columns` gets the same three |
| `export_seed_csv` | exportSeedCsv | `id` of a seed, `bom` (byte order mark and CRLF), `locales` (adds `@label:<locale>` and `@description:<locale>`) | the CSV text: `@id`, then `@code`, `@label`, `@description` for a reference type, then attribute names and end roles |
| `import_seed_csv` | importSeedCsv | `id`, `csv`, `mode` (`merge` default, or `replace`), `apply` (false: a dry run), `expectedHash` | the preview: `added`, `changed` (before and after), `removed`, `blocked` (rows other seeds reference, kept), `ignoredHeaders`, `applied`, `hash` |
| `reference_type_usage` | getReferenceTypeUsage | `id` of a reference type | `usages`: attribute, owner, domain, collection, required and the effective storage per database |
| `validate` | validate | `elementIds` (optional scope), `includeReferrers`, `includeScriptRules` | the report: diagnostics with rule ids, file, JSON pointer, line and column; counts |
| `list_validation_rules` | listValidationRules | | the built-in rules, ordered by id: `id`, `defaultSeverity`, `description`, `family` (the hundreds group, such as `MQ72xx`) and `familyLabel`, `canBeOff` (false for MQ1xxx); override a severity with `validation.rules` through `save_settings` |
| `get_database_view` | getDatabaseView | `id` of a database | the resolved physical view: the tables of the entities mapped to it (by its `byConvention` setting and its `packages`, or one by one by mapping elements, less the ignored ones), with columns, keys, indexes and foreign keys, views and sequences; a new database with nothing mapped has no tables |
| `list_packs` | (part of getProject) | | pack manifests and their diagnostics |
| `get_settings` | getSettings | | `maquettiste.json`: typed settings, canonical `json`, `hash` |
| `save_settings` | saveSettings | `settings` (whole document), `expectedHash` | the save result |
| `plan` | startPlan, run to completion | `packs`, `force`, `roots` (`all`, `committed`, `built`), `handEdits` (`fail`, `overwrite`, `skip`), `jobs` | `{ outcome, plan }`, the plan without its per-unit list (as a plan job's `planResult`) |
| `get_plan` | getPlan | `planId`, `units` | a stored plan; with `units` true, every unit with its pack, unit, template, element, `reason`, `causes` and `skipped` |
| `get_plan_diff` | getPlanDiff | `planId`, `path` | the unified diff text of one planned file |
| `apply_plan` | startApply, run to completion | `planId` | the apply result (as an apply job's `applyResult`) |
| `get_pack` | getPack | `pack` | pack.json as a document with its `hash` (for `save_pack`), the parameters (default, project value, schema), every file with its hash, role and users, and the diagnostics |
| `save_pack` | savePack | `pack`, `document` (the whole pack.json), `expectedHash` | the write result; written in canonical form; a schema failure (MQ6001, MQ6021) writes nothing |
| `save_pack_settings` | savePackSettings | `pack`, `settings` (`enabled`, `output`, `parameters`), `expectedHash` (the settings hash) | the settings save result; only `packs.<pack>` of `maquettiste.json` changes |
| `new_pack` | createPack | `name`, `from` (`empty`, a built-in starter, or a pack of this project) | the new pack's folder under `.maquettiste/templates/` |
| `list_pack_files` | listPackFiles | `pack` | each file's path, size, hash, role and users (`unit:<id>`, `companion:<id>`, `include:<path>`) |
| `read_pack_file` | getPackFile | `pack`, `path` | the text and hash of one template, partial, script or other file |
| `write_pack_file` | putPackFile | `pack`, `path`, `text`, `expectedHash` (`new` creates) | the write result; a template that does not parse is saved and its MQ6003 diagnostics returned; pack.json is refused (use `save_pack`) |
| `move_pack_file` | movePackFile | `pack`, `from`, `to`, `expectedHash`, `updateUnits`, `expectedPackHash` | the move result; `updateUnits` rewrites the units and scripts that name the file |
| `delete_pack_file` | deletePackFile | `pack`, `path`, `expectedHash` | the delete result; refused while a unit names the file or a template includes it |
| `get_template_context` | getTemplateContext | `pack`, `unit` | what the unit's templates can use: the globals, the members of the model and of the scope's records, the helpers |
| `preview_unit` | previewTemplate | `pack`, `unit`, `elementId`, `overlay` (path to unsaved text), `unitOverride` | each rendered file's output path and text, the diagnostics and the keys the render read; nothing is written; an element outside the unit's scope (none for an `each` unit, another kind, one its selector does not return, any for a `model` unit) renders nothing and returns MQ6026, which names the kind the template expects |
| `unit_paths` | unitPaths | `pack`, `unit`, `elementIds`, `limit` | how many elements the unit covers and the output paths it renders, with their root and whether the writer allows them |
| `get_pack_outputs` | getPackOutputs | `pack` | the files the pack's manifests record: path, unit, element, root, mode and state on disk (intact, edited, missing) |
| `explain_unit` | getPlanUnit, explainUnit | `planId` and `key` (a unit of a stored plan), or `pack`, `unit`, `elementId` (any unit and element) | the reason (`new`, `forced`, `inputs`, `outputs`, `unchanged`, or why it does not run: `pack-disabled`, `not-selected`, `scope`, `filter`, `skip-hint`, `selector`, `root-not-selected`, ...), the causes and a one-sentence summary |
| `get_schema` | (none) | `kind`: an element kind (`entity`, `value-object`, ...) or a document (`maquettiste`, `batch`, `pack`, `extension`) | `{ name, file, schema, references, extensions? }`: the JSON schema, the schema files it references (`common.json`) and, for an element kind, the project's extension schemas that apply to it (`name`, `description`, `appliesTo`, `properties`, `required`, `schemaPath`; they constrain the element's `properties`) |

Documents may be passed as JSON objects or as strings holding one. Writes are recorded as `ChangeSource.Cli`.

The pack tools let a client write a pack end to end, as the editor's Generate screen does: `new_pack`, add a unit with
`save_pack`, write the template with `write_pack_file`, render it on an element with `preview_unit` (pass the unsaved text
as `overlay` to try it before saving), fix the diagnostics, check where the files land with `unit_paths`, then `plan`,
`get_plan` with `units` true, and `explain_unit` for the reason each unit renders or does not. The files are the ones under
`.maquettiste/templates/<pack>/` that the command line, the editor and git all see.

The reference data and localization tools are `localization_status`, `get_translations`, `set_translations`,
`create_seed`, `export_seed_csv`, `import_seed_csv` and `reference_type_usage`; the kinds `reference-type` and `seed` work with every
element tool, `get_model_index` (`kind`) and `get_schema`.

Reference types and seeds are elements: `get_element`, `create_element`, `save_element` and `apply_batch` handle them. Translations
live in locale shards, not in element files: read them with `get_translations`, then pass the `shardHash` values you read as
`expected` to `set_translations`, which writes every entry in one atomic save (reference-types-seeds-localization.md section 3.9).
`import_seed_csv` previews unless `apply` is true; applying saves the seed with `expectedHash` and then the translations of the
`@label:<locale>` and `@description:<locale>` columns.

### Semantics

- **Hashes.** `get_element` and `get_settings` return the file's `hash` (the API's ETag). `save_element`, `delete_element`,
  `save_settings` and batch updates and deletes take it as `expectedHash` (the API's `If-Match`). `save_element`,
  `delete_element` and `save_settings` without it fail with `precondition-required`; a batch operation without it fails with
  `invalid` (rule MQ1002 on `/operations/<n>`), like applyBatch's 422.
- **Conflicts never overwrite.** When the file changed since it was read (by the editor, git, or the agent's own file edits), the
  save writes nothing and fails with `conflict`: `current` and `hash` are the disk version and `submitted` is the document the caller
  sent, so both versions are in hand to merge and retry with the new hash.
- **Referenced deletes.** `delete_element` refuses while other elements reference the element (`referenced`, with `referrers`),
  unless `resolution` is `remove-references`, which clears optional references; a required reference then makes the delete `invalid`.
  Owning references do not count: deleting an entity, relation or reference type deletes its seeds and every translation of its
  nodes in the same save, and only references from other elements (including cells of other seeds that name its rows) refuse it.
- **Batches** are all or nothing: when one operation fails nothing is written, and `items` says which failed and why.
- **Validation on write.** A save, create, delete or batch is validated on the candidate model; only errors the change introduces
  refuse it (`invalid`, with `diagnostics`).
- **Plan then apply.** `plan` renders and stores a plan without touching the repository; `get_plan_diff` shows one file;
  `apply_plan` writes exactly the planned bytes, or fails with `stale` (`staleUnits`, `stalePaths`) writing nothing when the model,
  the templates or a planned file changed since. Generation runs one at a time: the run lock is shared with every other process on
  the repo (a second run waits).
- **Fresh reads.** Every model read (and every element write) starts with a stat rescan of the model folder, so files edited
  behind the server are seen; settings reads and saves check the settings file on disk; `get_plan` and `get_plan_diff` read the
  stored plan.
- **Arguments.** The input schemas list the required arguments. A missing one, or one of the wrong JSON type, is answered with
  `bad-request` and a `detail` naming the argument (`kind must be a string, not a number.`), never the SDK's generic error.
- **Cancellation.** A `notifications/cancelled` for a running call cancels the engine call (a cancelled plan stores nothing and
  releases the run lock); per the protocol the cancelled call gets no result. Note: the C# SDK's client (2.2.0) does not send the
  notification when a call's token is cancelled; it only stops waiting.

### Errors

A failure is a tool error (`isError: true`), never a crash of the server. Its text is a problem object: the API's `code`, `status`
and `title`, an optional `detail`, and the fields of the body the API returns with that status:

| `code` | `status` | When | Extra fields |
| --- | --- | --- | --- |
| `bad-request` | 400 | a missing, wrongly typed or malformed argument, found by the tool's own checks | `detail` |
| `precondition-required` | 428 | no `expectedHash` | |
| `not-found`, `not-a-database` | 404 | no such element, plan, planned file or schema | `detail` for schemas (the known names) |
| `conflict` | 409 | the file changed since it was read | `current`, `hash`, `submitted` (saves), `items` (batches) |
| `referenced` | 409 | a delete refused by references | `referrers` |
| `invalid` | 422 | the change, batch, model or templates have errors | `diagnostics` (and `items` for batches) |
| `stale` | 409 | a plan's inputs changed | `staleUnits`, `stalePaths` |
| `conflicts`, `drift`, `busy`, `failed` | 409, 409, 503, 500 | a run outcome other than success | the run result |
| `model-unavailable` | 503 | the model folder cannot be read | `detail` |
| `internal` | 500 | anything else, including an exception from the engine; one line `maquettiste mcp: <tool> failed: <type>: <message>` goes to stderr | `detail` (the message) |

## Conventions resource and prompt

The modeling conventions (`skills/maquettiste-modeling/SKILL.md`, embedded in the CLI, without its front matter) are served as the
resource `maquettiste://conventions` (`text/markdown`) and as the prompt `modeling-conventions`, so any MCP client can read them.
The server's instructions summarize the workflow.

## Notes

- The SDK is `ModelContextProtocol` 2.2.0 (pinned in `Directory.Packages.props`), used without a generic host: `McpServer.Create`
  over a `StreamServerTransport` on the process's raw stdin and stdout.
- The order of `tools/list` is the SDK's (not sorted).
- Plan and apply run to completion inside the call instead of going through the editor's job queue; their bodies are the job's
  `planResult` and `applyResult`. Progress notifications are not sent yet.
