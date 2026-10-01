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
messages only; the start line and any error go to stderr. Before it serves, the server refreshes the JSON schemas in
`.maquettiste/.schema/v1/` when they differ from the ones this version ships (as `init` and the editor's start do) and says so in one
line on stderr; a folder it cannot write is reported there and the server starts anyway. The server stops (exit 0) when the client closes stdin, after answering
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
and members of an existing `.mcp.json` are kept in order; an existing `maquettiste` entry is never replaced (the Docker form below replaces it); a file that is not
a JSON object (not valid JSON, duplicate keys, or an `mcpServers` that is not an object) is left alone with a hint and exit 0. `init --skill` writes the skill that ships with
the installed version and refreshes it when the tool is upgraded. Both files are setup writes of the engine's path policy
(a symbolic link that leads out of the repository is refused, exit 4). Without init, `claude mcp add maquettiste -- maquettiste mcp`
registers the server for the current user only.

Start `claude` in the repository, approve the project server when asked (or check with `/mcp`), and ask for a model change:
the skill tells the agent to use the `mcp__maquettiste__*` tools and to fall back to file edits only without them.

### From the Docker image (no .NET on the machine)

The image `mattjcowan/maquettiste` carries the CLI (`/usr/local/bin/maquettiste`), so a machine with only Docker can run the
server. `init` registers it:

```sh
maquettiste init --mcp --docker mattjcowan/maquettiste:<tag>     # add --skill for the modeling skill
# created .mcp.json (server maquettiste: /bin/sh -c export PATH="$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin"; mkdir -p .maquettiste/.cache; exec docker run ... maquettiste mcp 2>>.maquettiste/.cache/mcp.log)
```

`.mcp.json` then holds the container command as a project server, started through `/bin/sh` with one command line:

```json
{
  "mcpServers": {
    "maquettiste": {
      "type": "stdio",
      "command": "/bin/sh",
      "args": [
        "-c",
        "export PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin\"; mkdir -p .maquettiste/.cache; exec docker run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli mattjcowan/maquettiste:<tag> maquettiste mcp 2>>.maquettiste/.cache/mcp.log"
      ]
    }
  }
}
```

Nothing else is written to the repository: no script. The line, read by the shell the client starts in the project folder:

- `export PATH=...` adds the places the container command usually lives (`/opt/homebrew/bin`, `/usr/local/bin`,
  `$HOME/.docker/bin`) after the client's own `PATH`. On the Mac a client started from the desktop (the Dock, Finder, an IDE
  opened from there) does not inherit the shell's `PATH`, so a bare `docker` is not found; with these it is.
- `mkdir -p .maquettiste/.cache` makes the cache folder that holds the log, so the redirect works in a fresh clone.
- `exec` replaces the shell with the container command, so the client talks to it directly and stops it by closing stdin.
- `$(pwd -P)` is the real path of the folder the client starts the server in, the project folder, so the mount works when the
  repository sits behind a symbolic link, and the file holds no absolute path and works for everyone who clones the
  repository. There is no `--repo`: the server finds the repository under `/repo`. The line has no `${...}`, which the client
  would expand itself; the shell expands `$PATH`, `$HOME` and `$(pwd -P)`.
- `-i` keeps stdin open (the JSON-RPC stream); there is no `-t`, a terminal would mix control characters into stdout.
- `--user 0:0` starts the container as root so the image's entrypoint can pick the user: it repairs what an earlier run left
  owned by another user in `.maquettiste/` and the output roots (an editor run as root, for example; one line on stderr,
  `repaired N files owned by another user under <path>`), then runs the server as the owner of the folder, which is you, so
  the model files it writes are yours: under Docker on Linux and on the Mac. docker/README.md "File ownership" has the whole
  rule.
- The index and plan cache lives in `.maquettiste/.cache/cli`, which `init` keeps out of git, so it survives the container of
  each session.
- Stdout carries the protocol only. The server's messages (its start line, errors) and the container command's own (a pull, a
  daemon that is not running) go to stderr, which the line appends to `.maquettiste/.cache/mcp.log`, ignored by git with the
  rest of the cache folder; look there first when the client reports the server as failed.

**On Windows**, which has no `/bin/sh`, `init` writes the container command itself, with `${PWD}`, which the MCP client
expands when it starts the server to the project folder:

```json
{
  "mcpServers": {
    "maquettiste": {
      "type": "stdio",
      "command": "docker",
      "args": [
        "run",
        "-i",
        "--rm",
        "--user",
        "0:0",
        "-v",
        "${PWD}:/repo",
        "-w",
        "/repo",
        "-e",
        "MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli",
        "mattjcowan/maquettiste:<tag>",
        "maquettiste",
        "mcp"
      ]
    }
  }
}
```

There the client finds `docker` on its own `PATH` and keeps the server's stderr in its own log for the server. Which form is
written depends on the system `init` runs on; a file committed from one system and re-run on the other gets that system's form.

With Podman, `--runtime podman` writes `exec podman run` in the line (`"command": "podman"` on Windows) with the same arguments:

```sh
maquettiste init --mcp --docker mattjcowan/maquettiste:<tag> --runtime podman
```

Under rootless Podman root in the container is already you outside it; the entrypoint sees that and stays root, so the files
are yours without `--userns=keep-id`.

A re-run with another tag or runtime (`init --docker mattjcowan/maquettiste:<new tag>`; `--docker` implies `--mcp`) replaces the
`maquettiste` entry in place, whichever form it has and keeps every other server and member of `.mcp.json`; the same command again keeps the file as
it is. Earlier versions wrote a wrapper script, `mcp.sh`, at the repository root and registered `./mcp.sh`; a re-run of
`init --docker` replaces that entry, removes the script and says so (`removed mcp.sh ...`). Only a script that carries the
marker those versions wrote on its second line is removed; any other `mcp.sh` is left alone.

Without `init`, the same command works for `claude mcp add`, registered for this folder and the current user only (the shell
expands `$PWD` once, when the command runs):

```sh
claude mcp add maquettiste -- docker run -i --rm --user 0:0 -v "$PWD:/repo" -w /repo \
  -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli mattjcowan/maquettiste:<tag> maquettiste mcp
```

Checked on Linux (Docker Engine, amd64) with a stdio client over `docker run -i --rm --user ... maquettiste mcp`: `initialize`
in 0.7 s, `tools/list` with 18 tools (image 0.1.0; the published 0.2.0 image lists 24, the current source 51), `validate` in 55 ms,
the container removed on exit. The registration (the arguments exactly, both forms and both runtimes, the merge, the replacement and the
removal of an earlier `mcp.sh`) is covered by the CLI tests; the `/bin/sh` line was run with a stand-in `docker` reachable only
through `$HOME/.docker/bin` from a folder behind a symbolic link (the real path mounted, stderr in the log). A Mac client started
from the Dock over this entry has not been run **(not verified)**.

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
claude                               # then /mcp shows maquettiste connected with 51 tools
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
| `get_model_index` | getModelIndex | `kind`, `package` (id or name), `tag`, `category`, `stereotype`, `query` (name contains, ignoring case), all optional, AND; `cursor`, `limit` (1 to 1000) | element summaries; with `cursor` or `limit`, one page `{ items, next }` ordered by kind, name and id |
| `get_model_kinds` | getModelKinds | `by` (`kind` default, or `package`) | `{ total, kinds: [{ kind, count }], packages }`; with `package`, per package `{ package, name, count, kinds }`, the elements in no package first |
| `get_element` | getElement | `id` | the document: `json` (canonical), `hash`, `path`, the typed element, the sidecar text |
| `get_elements` | getElements | `ids` (element or sub-element ids, at most 1000), the index filters, `fields` (top-level members to keep), `cursor`, `limit` (default 100, at most 1000) | `{ items: [{ id, kind, path, hash, json }], next, missing }`: the canonical documents in pages, ordered by kind, name and id |
| `get_resolved_model` | getResolvedModel | `scope` (`all` default, `packages`, `entities`, `relations`, `enums`, `value-objects`, `scalar-types`, `reference-types`, `seeds`, `processes`, `actors`, `scenarios`, `databases`, `tables`), `database` (an id), `cursor`, `limit` | `{ items, next, diagnostics }`: what templates read, as flat records with `id`, `kind` and `name` (other objects by id); a model with errors returns no items and the errors |
| `save_element` | saveElement | `id`, `element` (whole document), `expectedHash` | the save result (new `hash`, changes) |
| `create_element` | createElement | `element` (an id is assigned when absent) | the save result with the new `id`; write a database with `byConvention` (`none`, `packages` or `all`): without it a database with no `packages` takes every entity (the rule from before 0.3.0) |
| `delete_element` | deleteElement, getDeletePlan | `id`, `expectedHash` (not with `dryRun`), `resolution` (`refuse` default, `remove-references` or `delete-dependents`), `dryRun` (`true`: write nothing and return the plan; the resolution then defaults to `delete-dependents`) | the save result; with `dryRun` the delete plan `{ ids, resolution, outcome, deletes: [{ id, kind, name, path, because }], clears: [{ id, kind, name, pointer, field, target, because }], removes: [{ id, kind, name, pointer, what, subId, subKind, because }], refused: [{ id, kind, name, pointer, why, rule }], settings: [{ pointer, what }], warnings: [{ message, pack, unit }] }` |
| `apply_batch` | applyBatch | `operations` (the batch's `operations` array, or the whole `{ "operations": [...] }` body); besides create, update and delete, the database schema operations `add-schema`, `rename-schema`, `remove-schema` (with `target` to move what lives there, `default` when removing the default) and `set-default-schema`; the process operations `sync-enum` (`id` a lifecycle process: its bound enum's members become the root-level states in order; a removal of a member still used by a default, allowed values, a seed cell or a scenario value is refused with MQ9019; change the uses first), `set-lifecycle` (`id` an entity, `target` a process, both sides in one change; no `target` clears it), `set-initial` (`id` a process or compound state, `target` a direct child) and `refresh-scenario` (`id` a scenario: its steps' `expect` and its `outcome` are rewritten from the engine's replay; refused when the batch also writes the scenario or its process, or when the replay stops early) | the batch result, all or nothing |
| `simulate_process` | simulateProcess | `process` (id, name or model path), `steps` (scenario steps without `expect`; ids optional), `start` (`{ context, at }`), `scenario` (its start and steps run first), `document` (an unsaved draft; invalid is `invalid` with diagnostics), `from` | `{ processHash, trace, configuration, context, enabled, pending, timers, gates, final, clock, diagnostics }`; nothing is kept between calls: send the whole input list each time |
| `record_scenario` | recordScenario | `process`, `name`, `steps`, `start`, `outcome` (the replay's when absent), `dryRun` | `{ id, element, hash, applied, diagnostics }`: the scenario with every step's `expect` and the outcome filled from the engine's replay, saved unless `dryRun` |
| `verify_scenarios` | verifyScenarios | `process`, `scenarios` (ids or names; all when absent) | `{ process, results: [{ scenario, name, passed, steps, failure }], passed }`; `failure` is `{ step, rule, message, expected, actual }` |
| `export_process` | exportProcess | `process`, `format` (`xstate`) | the XState v5 machine config text (canonical key order; the model data without an XState home under `meta.maquettiste`), then, when there are any, a second block `{ diagnostics }` (MQ9404, MQ9406) |
| `import_process` | importProcess | `config` (object or string), `package`, `name`, `use`, `subject` for a new process, or `into` (id or name) and `expectedHash`; `apply` (false: a dry run) | `{ document, diagnostics, created, removed, applied, id, hash }`; the diagnostics are MQ9401 to MQ9405 (and, when applied, the save's findings), and an error among them (MQ9403, MQ9405) or a changed `into` refuses the apply |
| `sync_enum_from_process` | syncEnumFromProcess | `process` (a lifecycle), `expectedHash`, `apply` (false: a dry run) | `{ process, enum, added, removed, reordered, refused, applied, diagnostics }`; a member still in use is refused, never removed |
| `get_references` | getReferences | `id` | where the element is used |
| `localization_status` | getLocalizationStatus | | `defaultLocale`, `declared`, and per translated locale its `chain` and per shard `expected`, `translated`, `missing`, `stale` |
| `get_translations` | getTranslations | `locale`, `owner` or `shard` (optional), `missing` (the entries that need work, 200 per page), `cursor` | `{ entries, cursor }`: per field the `source`, `translation`, `effective` text, `state` (`translated`, `missing`, `stale`, `fallback`), `shard` and `shardHash` |
| `set_translations` | putTranslations | `locale`, `entries` (`[{ id, field, value, confirm? }]`, `value` null removes), `expected` (`{ "<shard path>": "<shardHash>" }`) | `{ outcome, hashes, diagnostics }`; a changed shard is `conflict`, an unknown id or field `invalid` |
| `create_seed` | (CLI `seed new`) | `type`: a reference type id | the seed id: a new seed named after the type with the columns `code`, `label` and `description` and no rows, or the seed the type already has (nothing changes). `create_element` of a seed for a reference type without `columns` gets the same three |
| `export_seed_csv` | exportSeedCsv | `id` of a seed, `bom` (byte order mark and CRLF), `locales` (adds `@label:<locale>` and `@description:<locale>`) | the CSV text: `@id`, then `@code`, `@label`, `@description` for a reference type, then attribute names and end roles |
| `import_seed_csv` | importSeedCsv | `id`, `csv`, `mode` (`merge` default, or `replace`), `apply` (false: a dry run), `expectedHash` | the preview: `added`, `changed` (before and after), `removed`, `blocked` (rows other seeds reference, kept), `ignoredHeaders`, `applied`, `hash` |
| `reference_type_usage` | getReferenceTypeUsage | `id` of a reference type | `usages`: attribute, owner, domain, collection, required and the effective storage per database |
| `validate` | validate | `elementIds` (optional scope), `includeReferrers`, `includeScriptRules` | the report: diagnostics with rule ids, file, JSON pointer, line and column; counts |
| `list_validation_rules` | listValidationRules | | the built-in rules, ordered by id: `id`, `defaultSeverity`, `description`, `family` (the hundreds group, such as `MQ72xx`) and `familyLabel`, `canBeOff` (false for MQ1xxx), `quickFix` (the batch operation that fixes a finding, when the rule has one); override a severity with `validation.rules` through `save_settings` |
| `get_database_view` | getDatabaseView | `id` of a database | the resolved physical view: the tables of the entities mapped to it (by its `byConvention` setting and its `packages`, or one by one by mapping elements, less the ignored ones), with columns, keys, indexes and foreign keys, views, sequences and `schemas` (`name`, `isDefault`, `isDeclared`). The database, each schema, table, column, view and sequence carries the annotations of its own file or entry: `displayName`, `pluralName`, `description`, `stereotypes` (keys), `tags`, `category` (id), `properties` (merged with the stereotypes' defaults) and `generation`; a synthesized table without an overlay, or a synthesized column without an overlay entry, has none, whatever its entity or attribute carries. The database also gives `quoting`, `maxIdentifierLength`, `byConvention` and `packages`; a column its `default`, `sequenceId`, `collation`, `comment` and `computedStored`. A new database with nothing mapped has no tables |
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
| `delete_pack` | deletePack | `pack`, `expectedHash` (the pack.json hash from `get_pack`) | the removal result: `files` deleted from `.maquettiste/templates/<pack>/`, the `packs.<pack>` settings entry removed (`settingsHash`), and `untracked`, the generated files the pack's manifests recorded, which stay on disk and are no longer tracked |
| `rename_pack` | renamePack | `pack`, `name` (the new name: lowercase letters and digits separated by single hyphens), `expectedHash` (the pack.json hash from `get_pack`), `updateHints` (default `true`) | the rename result: `from`, `to`, the new pack.json `hash`, `files` moved from `.maquettiste/templates/<pack>/` to `.maquettiste/templates/<name>/`, the `packs.<pack>` settings entry moved to `packs.<name>` with its values (`settingsHash`), `tracked`, the generated files whose manifest entries moved so they stay tracked, `hints`, the elements whose generation hints name the old pack, and `hintsUpdated`, the ones moved to the new name in one model batch after the rename (`updateHints`, default `true`; a refused batch leaves the rename in place and adds a warning); a name that is taken or not a pack name is `invalid` and changes nothing |
| `get_template_context` | getTemplateContext | `pack`, `unit` | what the unit's templates can use: the globals, the members of the model and of the scope's records, the helpers |
| `preview_unit` | previewTemplate | `pack`, `unit`, `elementId`, `overlay` (path to unsaved text), `unitOverride` | each rendered file's output path and text, the diagnostics and the keys the render read; nothing is written; an element outside the unit's scope (none for an `each` unit, another kind, one its selector does not return, any for a `model` unit) renders nothing and returns MQ6026, which names the kind the template expects |
| `unit_paths` | unitPaths | `pack`, `unit`, `elementIds`, `limit` | how many elements the unit covers and the output paths it renders, with their root and whether the writer allows them; each path names its element (`elementName`: an element's name, a table as `customers (billing)`, a locale's tag) and `elementKind` |
| `get_pack_outputs` | getPackOutputs | `pack` | the files the pack's manifests record: path, unit, element, root, mode and state on disk (intact, edited, missing) |
| `explain_unit` | getPlanUnit, explainUnit | `planId` and `key` (a unit of a stored plan), or `pack`, `unit`, `elementId` (any unit and element) | the reason (`new`, `forced`, `inputs`, `outputs`, `unchanged`, or why it does not run: `pack-disabled`, `not-selected`, `scope`, `filter`, `skip-hint`, `selector`, `root-not-selected`, ...), the causes and a one-sentence summary |
| `get_schema` | (none) | `kind`: an element kind (`entity`, `value-object`, ...) or a document (`maquettiste`, `batch`, `pack`, `extension`) | `{ name, file, schema, references, extensions? }`: the JSON schema, the schema files it references (`common.json`) and, for an element kind, the project's extension schemas that apply to it (`name`, `description`, `appliesTo`, `properties`, `required`, `schemaPath`; they constrain the element's `properties`) |

Documents may be passed as JSON objects or as strings holding one. Writes are recorded as `ChangeSource.Cli`.

The pack tools let a client write a pack end to end, as the editor's Generate screen does: `new_pack`, add a unit with
`save_pack`, write the template with `write_pack_file`, render it on an element with `preview_unit` (pass the unsaved text
as `overlay` to try it before saving), fix the diagnostics, check where the files land with `unit_paths`, then `plan`,
`get_plan` with `units` true, and `explain_unit` for the reason each unit renders or does not. The files are the ones under
`.maquettiste/templates/<pack>/` that the command line, the editor and git all see.

The process tools mirror the process operations of the editor API (phase-3-design.md section 4.4) and the `maquettiste process`
verbs. A process argument is an id, a name or a model path. Simulation keeps no state on the server: an agent holds the input list,
calls `simulate_process` with all of it, reads `enabled` for what can happen next, and turns a run it likes into a scenario with
`record_scenario`. `verify_scenarios` replays scenarios the way `validate` does (MQ9301 to MQ9306 and MQ9502 to MQ9507).
`import_process` and `sync_enum_from_process` are dry runs unless `apply` is true. The process kinds `process`, `actor` and
`scenario` work with every element tool, `get_model_index` (`kind`) and `get_schema`; `list_validation_rules` lists the MQ9xxx
rules with their `quickFix` operation where one exists.

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
  unless `resolution` is `remove-references`, which clears optional references (a required reference then makes the delete
  `invalid`, with a readable MQ2001 first), or `delete-dependents`, which also removes the part of a referrer that needs the
  element (an attribute whose type is deleted, a diagram member, a key) or deletes the referrer with its own dependents (a
  database's tables, views, sequences and mappings; an entity's relations, mappings and overlays), in one change. `dryRun: true`
  returns that plan without writing; a batch delete operation takes the same `resolution`.
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

## Reading a large model

The index, the bulk read and the resolved read answer different questions:

- `get_model_index` finds elements: one short row per element (id, kind, name, package, tags, category, stereotypes, hash,
  path). Without paging arguments it returns the whole list, which stays small (a few hundred bytes a row); with `limit` or
  `cursor` it returns pages.
- `get_elements` reads documents in bulk: the canonical JSON of each element, as `get_element` returns it, in pages of up to
  1000, trimmed to the members you name in `fields`. Use it to copy, transform or analyze the model as written.
- `get_resolved_model` reads what generation sees: types resolved through scalar types and value objects, the base entity's
  attributes on each derived entity, keys, the tables and columns each entity maps to, the database views and the processes'
  statecharts, as flat records. Use it to feed another system the model as templates read it.
- `get_model_kinds` says what there is to iterate: each kind with its count, per package with `by: "package"`.

Every paged read orders its rows by kind, then name, then id (ordinal), so the order is stable. Pass the `next` of a page as
`cursor` to get the following one, until `next` is null; a cursor is opaque and encodes the last position read. A change to the
model between two pages does not fail the read: the next page starts after that position in the changed model, so an element
renamed in between may be read twice or not at all, and every other element is read once. The editor API serves the same
pages (`GET /api/model/elements`, `/api/model/resolved`, `/api/model/kinds`, and `/api/model/index` with `limit` or `cursor`,
whose next page comes in a `Link` header), and so does the command line (`maquettiste model export` and `model stats`, in
docs/user-guide.md, "The command line"). The examples below run on the billing fixture.

### Every entity's documents, trimmed

Count first, then page through the entities, keeping only the members you need:

```json
get_model_kinds {}
{ "total": 26, "kinds": [ { "kind": "category-tree", "count": 1 }, { "kind": "database", "count": 1 }, { "kind": "entity", "count": 5 }, ... ], "packages": null }

get_elements { "kind": "entity", "fields": ["name", "attributes"], "limit": 2 }
{
  "items": [
    { "id": "01J92P0V0ETQKXXP951CMMNHH3", "kind": "entity", "path": ".maquettiste/model/entities/customer.json",
      "hash": "9e5da84d083cffdcec1a6b4969a4281f1cbf2b64957bde9621616f813ea27dcf",
      "json": { "kind": "entity", "id": "01J92P0V0ETQKXXP951CMMNHH3", "name": "Customer",
                "attributes": [ { "id": "01J92P0V0KGPC29TQQG8R57EBM", "name": "id", "type": "uuid", "required": true }, ... ] } },
    { "id": "01J92P0V0FJ23CGSNKM7P1W5V7", "kind": "entity", "path": ".maquettiste/model/entities/invoice.json", ... }
  ],
  "next": "cDEAZW50aXR5AEludm9pY2UAMDFKOTJQMFYwRkoyM0NHU05LTTdQMVc1Vjc",
  "missing": []
}

get_elements { "kind": "entity", "fields": ["name", "attributes"], "limit": 2, "cursor": "cDEAZW50aXR5AEludm9pY2UAMDFKOTJQMFYwRkoyM0NHU05LTTdQMVc1Vjc" }
{ "items": [ { ... "InvoiceLine" ... }, { ... "Payment" ... } ], "next": "cDEAZW50aXR5AFBheW1lbnQAMDFKOTJQMFYwSEVHU0M2TVc5MkNTVDVLQTY", "missing": [] }
```

and once more for `Product`, whose page has `"next": null`. `id` and `kind` are always kept; leave `fields` out for whole
documents. The `hash` of each item is the one `save_element` takes as `expectedHash`, so a client can read in bulk and write back
one element at a time. The same over HTTP and from the command line:

```sh
curl -H "Authorization: Bearer $MAQUETTISTE_EDITOR_TOKEN" \
  'http://maquettiste.localhost:8080/api/model/elements?kind=entity&fields=name,attributes&limit=1000'
maquettiste model export --kind entity --fields name,attributes --format ndjson > entities.ndjson
```

### The resolved databases

```json
get_resolved_model { "scope": "databases" }
{
  "items": [
    { "id": "01J92P0V1QRN2181XM2ZWE02W4", "kind": "database", "name": "main", "dialect": "postgresql", "version": "16", "defaultSchema": "billing",
      "tables": [
        { "key": "01J92P0V0ETQKXXP951CMMNHH3@01J92P0V1QRN2181XM2ZWE02W4", "name": "customers", "schema": "billing", "origin": "synthesized",
          "entityId": "01J92P0V0ETQKXXP951CMMNHH3",
          "columns": [ { "key": "01J92P0V0KGPC29TQQG8R57EBM", "name": "id", "type": "uuid", "nativeType": "uuid", "nullable": false,
                         "attributeId": "01J92P0V0KGPC29TQQG8R57EBM", "isPrimaryKey": true, "position": 0, ... }, ... ],
          "primaryKey": { "name": "pk_customers", "columns": ["01J92P0V0KGPC29TQQG8R57EBM"] }, "foreignKeys": [], "indexes": [], ... },
        ... "invoice_lines", "invoices", "payment_invoice", "payments", "products" ...
      ],
      "views": [ { "name": "outstanding_invoices", ... } ],
      "sequences": [ { "name": "invoice_number_seq", ... } ] }
  ],
  "next": null,
  "diagnostics": []
}
```

Each database record is the database view of `get_database_view` with `id`, `kind` and `name`. A database of thousands of tables
is one large record; read it a table at a time instead with `{ "scope": "tables", "database": "01J92P0V1QRN2181XM2ZWE02W4",
"limit": 500 }`, whose records are `{ id, kind: "table", name, database, table }`. To join tables to entities, read
`{ "scope": "entities", "database": "01J92P0V1QRN2181XM2ZWE02W4" }`: each entity record's `mappings` names its table (`table`, the
table's `key`) and the column of each attribute path. When the model has errors, `items` is empty and `diagnostics` holds the
errors; fix them (`validate` lists them with positions) and read again. From the command line:
`maquettiste model export --resolved --scope databases --database main > databases.json`.

### Everything of one package

```json
get_model_kinds { "by": "package" }
{ "total": 26, "kinds": [ ... ],
  "packages": [
    { "package": null, "name": null, "count": 12, "kinds": [ ... ] },
    { "package": "01J92P0V01KDRN8GX5PGYCNKSX", "name": "Billing", "count": 13,
      "kinds": [ { "kind": "diagram", "count": 1 }, { "kind": "entity", "count": 4 }, { "kind": "enum", "count": 1 }, { "kind": "package", "count": 1 },
                 { "kind": "relation", "count": 4 }, { "kind": "scalar-type", "count": 1 }, { "kind": "value-object", "count": 1 } ] },
    { "package": "01J92P0V025DRFTXKMS240G2NG", "name": "Catalog", "count": 1, "kinds": [ { "kind": "entity", "count": 1 } ] } ] }

get_elements { "package": "Billing", "limit": 1000 }
{ "items": [ ... 13 documents: the diagram, Customer, Invoice, InvoiceLine, Payment, InvoiceStatus, the package Catalog,
             the relations contains, places, refers to and settles, EmailAddress, Money ... ], "next": null, "missing": [] }
```

`package` takes the package's id or its name and matches the elements directly in it; a child package (here `Catalog`, inside
`Billing`) is one of them, so read its members with `{ "package": "Catalog" }` in turn, or walk the tree from the `children` of
the package records of `get_resolved_model { "scope": "packages" }`. Add `kind` to narrow it (`{ "package": "Billing", "kind":
"relation" }`), or call `get_resolved_model` and keep the records whose `package` is the package's id.

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
