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
maquettiste init --skill --check   # in CI: exit 2 when the skill is missing, stale or edited by hand
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
(a symbolic link that leads out of the repository is refused, exit 4).

The skill file belongs to the engine. Right after its front matter it carries a marker line,
`<!-- maquettiste-skill: version=<release>; sha256=<hash> -->` (the hash of the whole file without that line), and a paragraph
saying that the file is generated and rewritten by `init --skill`, and that the repository's own modeling conventions go in
`CONVENTIONS.md` next to it. Its last section tells the agent to read that file after the skill and to let it win where the two
disagree. `init` never writes or deletes `CONVENTIONS.md`. On each run `init --skill` compares the file on disk with what it would
write:

| The file on disk | `init --skill` |
| --- | --- |
| missing | writes it (`created`) |
| the same, line endings aside | keeps it (`kept ... (current)`) |
| untouched: a valid marker line, or the exact bytes an earlier release wrote (every release since 0.1.0 is known) | rewrites it (`updated ... (from the 0.5.4 version to <release>)`) |
| anything else: edited by hand | leaves it alone, writes the new version to `SKILL.md.new` beside it and prints `skill not updated: ... was edited by hand; the new version is in SKILL.md.new (diff them, move your additions to CONVENTIONS.md, then run init --skill --force)`; exit 0 |

`--force` overwrites an edited file. A leftover `SKILL.md.new` is removed once the skill is current (unless it was edited too).
`init --skill --check` writes nothing (not even `.maquettiste/`), prints one line (`skill current`, `skill missing`, `skill stale`
or `skill edited`) and exits 2 unless the skill is current, like `generate --check`. `--check` takes no other option, and `--force`
needs `--skill` or `--agent-setup`. Without init, `claude mcp add maquettiste -- maquettiste mcp`
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
        "export PATH=\"$PATH:/opt/homebrew/bin:/usr/local/bin:$HOME/.docker/bin\"; mkdir -p .maquettiste/.cache; exec docker run -i --rm --user 0:0 -v \"$(pwd -P):/repo\" -w /repo -e MAQUETTISTE_CACHE_DIR=/repo/.maquettiste/.cache/cli -e MAQUETTISTE_WORKSPACE=\"$(git symbolic-ref --short -q HEAD 2>/dev/null || basename \"$(pwd -P)\")\" mattjcowan/maquettiste:<tag> maquettiste mcp 2>>.maquettiste/.cache/mcp.log"
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
- `MAQUETTISTE_WORKSPACE` is the branch git reads in the project folder, else (a detached head, no git) the folder's name; the
  container sees only `/repo`, and `get_project` reports it as `workspace`. Without it the server reads the branch from
  `.git/HEAD`, and for a linked worktree, whose git folder is not mounted, takes the worktree's name. Either way the
  server reads the workspace once, when it starts: restart it after switching branches.
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
        "-e",
        "MAQUETTISTE_WORKSPACE",
        "mattjcowan/maquettiste:<tag>",
        "maquettiste",
        "mcp"
      ]
    }
  }
}
```

There the client finds `docker` on its own `PATH` and keeps the server's stderr in its own log for the server. `-e
MAQUETTISTE_WORKSPACE` passes the variable on when the client's environment sets it; otherwise the server reads the branch from
`.git`. Which form is
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

Checked on Linux (Docker Engine, amd64) with a stdio client over `docker run -i --rm --user ... maquettiste mcp`:
`initialize` in 0.7 s, `tools/list` with 18 tools (image 0.1.0; the published 0.2.0 image lists 24,
the current source 71), `validate` in 55 ms, the container removed on exit. The registration (the arguments exactly,
both forms and both runtimes, the merge, the replacement and the removal of an earlier `mcp.sh`) is covered by the CLI
tests; the `/bin/sh` line was run with a stand-in `docker` reachable only through `$HOME/.docker/bin` from a folder
behind a symbolic link (the real path mounted, stderr in the log). A Mac client started from the Dock over this entry
has not been run **(not verified)**.

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
claude                               # then /mcp shows maquettiste connected with 71 tools
```

A headless check that needs no approval prompt (an explicit `--mcp-config` is trusted):

```sh
claude -p "Call mcp__maquettiste__get_model_index with kind entity and reply with only the entity names." \
  --mcp-config .mcp.json --strict-mcp-config --allowedTools mcp__maquettiste__get_model_index
# Customer, InvoiceLine, Invoice, Payment, Product
```

The skill is in `skills/maquettiste-modeling/` here (without the header, which `init --skill` adds); the server also serves it as
a resource and a prompt, followed by the repository's `CONVENTIONS.md` (below).

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

The read tools (`get_project`, `get_model_index`, `get_model_kinds`, `get_elements`, `get_element`, `get_references`,
`get_resolved_model`, `get_schema`, `get_settings`, `get_database_view`, `validate`, `list_validation_rules`,
`preview_query_sql`, `preview_binding_sql`, `get_materialize_status`, `preview_materialize`, `list_packs`, `get_plan`,
`get_plan_diff`, `preview_unit`, `reference_type_usage`, `tag_usage`, `localization_status`, `list_snapshots`,
`compare_snapshots`, `list_migrations`, `get_ledger_ddl`) are one catalog in the engine (`AgentTools`), shared with the
editor's assistant (docs/user-guide.md, "The assistant"): their names, descriptions, input schemas and answers are the
same in both, and a change to one is a change to the other. The assistant has none of the write tools; it proposes model
changes for the user to review (`propose_changes`) instead.

| Tool | API operation | Arguments | Returns |
| --- | --- | --- | --- |
| `get_project` | getProject | `sections` (optional: `settings`, `packs`, `extensions`) | a summary: name, `productVersion` (the release, such as `0.5.3`) and `build` beside `engineVersion` (the engine contract, not the release) and `formatVersion`, `workspace` (`MAQUETTISTE_WORKSPACE`, else the git branch, else the worktree name) with `branch`, `worktree` and `repository`, `mode` (`local`), `settingsHash`, databases, `packs` (`{ name, version, description, units }`), `packDiagnosticCounts` (`{ errors, warnings, info }`), `extensions` (their names) and `sections` (always `extensions`, `packs`, `settings`); each section asked for adds its full members: `settings` the typed settings, `packs` `packManifests` and `packDiagnostics`, `extensions` `extensionSchemas`; an unknown section is `bad-request` |
| `get_model_index` | getModelIndex | `kind`, `package` (id or name), `tag`, `category`, `stereotype`, `query` (name contains, ignoring case), all optional, AND; `cursor`, `limit` (1 to 1000) | element summaries; with `cursor` or `limit`, one page `{ items, next }` ordered by kind, name and id |
| `get_model_kinds` | getModelKinds | `by` (`kind` default, or `package`) | `{ total, kinds: [{ kind, count }], packages }`; with `package`, per package `{ package, name, count, kinds }`, the elements in no package first |
| `get_element` | getElement | `id` | the document: `json` (canonical), `hash`, `path`, the typed element, the sidecar text |
| `get_elements` | getElements | `ids` (element or sub-element ids, at most 1000), the index filters, `fields` (top-level members to keep), `cursor`, `limit` (default 100, at most 1000) | `{ items: [{ id, kind, path, hash, json }], next, missing }`: the canonical documents in pages, ordered by kind, name and id |
| `get_resolved_model` | getResolvedModel | `scope` (`all` default, `packages`, `entities`, `relations`, `enums`, `value-objects`, `scalar-types`, `reference-types`, `seeds`, `processes`, `actors`, `scenarios`, `databases`, `tables`, `routines`, `database-types`, `sql-objects`, `queries`, `migrations`), `database` (an id), `cursor`, `limit` | `{ items, next, diagnostics }`: what templates read, as flat records with `id`, `kind` and `name` (other objects by id; a value the file leaves out is null, never empty text); `migrations` lists open migration facts by database name, then applied order; a model with errors returns no items and the errors, unless its only errors are pending migration facts (MQ4101), which still return items |
| `save_element` | saveElement | `id`, `element` (whole document), `expectedHash` | the save result (new `hash`, changes, and `facts`: the migration facts the save recorded, `{ id, database, fact, state, missing, summary, path }`; a pending fact's MQ4101 is among the diagnostics and the save still goes through) |
| `patch_element` | (CLI `model apply` with an update) | `id` (an id, not a qualified name), `patch` (an RFC 6902 array of operations, or a string holding one), `expectedHash`; all required | the save result as `save_element` answers it (`hash`, changes, diagnostics, `facts`), plus `ids` (`{ names, minted }`, as `apply_batch`); see "Patches" under Semantics |
| `create_element` | createElement | `element` (an id is assigned when absent) | the save result with the new `id` and its `facts`; write a database with `byConvention` (`none`, `packages` or `all`): without it a database with no `packages` takes every entity (the rule from before 0.3.0) |
| `delete_element` | deleteElement, getDeletePlan | `id`, `expectedHash` (not with `dryRun`), `resolution` (`refuse` default, `remove-references` or `delete-dependents`), `dryRun` (`true`: write nothing and return the plan; the resolution then defaults to `delete-dependents`) | the save result with its `facts` (a dropped table or column records a `drop`, cascades included); with `dryRun` the delete plan `{ ids, resolution, outcome, deletes: [{ id, kind, name, path, because }], clears: [{ id, kind, name, pointer, field, target, because }], removes: [{ id, kind, name, pointer, what, subId, subKind, because }], refused: [{ id, kind, name, pointer, why, rule }], settings: [{ pointer, what }], warnings: [{ message, pack, unit }] }`. A custom property value an `x-ref` types is cleared by `remove-references` when optional and by `delete-dependents` either way (its holder stays); a schema declaring the property that would then fail (`required`, `minItems`) refuses with MQ2001. A pack parameter value naming the element refuses a plain delete (MQ2001 in `maquettiste.json`, `id` null in `refused`); the resolutions remove it in the same change (`settings`) as for properties, unless the parameter is required |
| `apply_batch` | applyBatch | `operations` (the batch's `operations` array, or the whole `{ "operations": [...] }` body); besides create, update and delete, the database schema operations `add-schema`, `rename-schema`, `remove-schema` (with `target` to move what lives there, `default` when removing the default) and `set-default-schema`; the process operations `sync-enum` (`id` a lifecycle process: its bound enum's members become the root-level states in order; a removal of a member still used by a default, allowed values, a seed cell or a scenario value is refused with MQ9019; change the uses first), `set-lifecycle` (`id` an entity, `target` a process, both sides in one change; no `target` clears it), `set-initial` (`id` a process or compound state, `target` a direct child) and `refresh-scenario` (`id` a scenario: its steps' `expect` and its `outcome` are rewritten from the engine's replay; refused when the batch also writes the scenario or its process, or when the replay stops early); the tag operations `retag` (`tags`, optional `name` to rename the one tag, optional `package` for a domain's vocabulary: the vocabulary and every use it governs, see `tag_usage`) and `tags-to-property` (`prefix`, `name` a custom property, optional `package` and `elementIds`: the tags that start with `prefix` move into the property of each element and sub-element holding them, converted by the schema that declares it, one suffix for a string, number, integer, boolean or `enum`, all of them for a string array, the one id the suffix names for an `x-ref`; the tags leave their holders and, without `elementIds`, the vocabulary, unless something without properties such as a relation end still uses them; no matching tag is MQ1002, a holder whose property no schema declares, whose suffix does not fit or that holds another value is MQ5001); `names-to-ids` (optional `elementIds`: every custom property value an `x-ref` types that is still a name becomes the one id it finds, in those elements or in every document with `x-ref` sites; a value that finds nothing or several is MQ2010 at the value, nothing to convert writes nothing); the materialize operations `materialize-tables` (`database`, `entities`, optional `schema`), `materialize-entities` (`database`, `tables`, `package`), `materialize-attributes` (`database`, `entities`, for one entity optional `columns`: an attribute per unnamed column of its binding's source) and `materialize-columns` (`database`, `entities`, for one entity optional `attributes`: a table column per unmapped attribute), refused with MQ4055 for what is already bound or has nothing to add (preview them with `preview_materialize`). Every id field takes an id, a qualified name such as `@table:billing.notes` or a placeholder declared with `as` (see "Batches by name"), and the engine mints every missing id; `dryRun` (`true`: write nothing) | the batch result, all or nothing, with `ids` (`{ names, minted }`: each name and placeholder to its id, and each minted `{ operation, pointer, kind, id, name, placeholder }`) and the migration facts the batch recorded in `facts` (its items carry none); with `dryRun` `{ outcome, items, files: [{ path, action, id, kind, name, beforeHash, diff }], diagnosticsAdded, diagnosticsRemoved, ids, facts }`: a unified diff per file, the diagnostics the change adds and removes over the elements it touches, and the facts it would record (`null` when the change reaches every database) |
| `simulate_process` | simulateProcess | `process` (id, name or model path), `steps` (scenario steps without `expect`; ids optional), `start` (`{ context, at }`), `scenario` (its start and steps run first), `document` (an unsaved draft; invalid is `invalid` with diagnostics), `from` | `{ processHash, trace, configuration, context, enabled, pending, timers, gates, final, clock, diagnostics }`; nothing is kept between calls: send the whole input list each time |
| `record_scenario` | recordScenario | `process`, `name`, `steps`, `start`, `outcome` (the replay's when absent), `dryRun` | `{ id, element, hash, applied, diagnostics }`: the scenario with every step's `expect` and the outcome filled from the engine's replay, saved unless `dryRun` |
| `verify_scenarios` | verifyScenarios | `process`, `scenarios` (ids or names; all when absent) | `{ process, results: [{ scenario, name, passed, steps, failure }], passed }`; `failure` is `{ step, rule, message, expected, actual }` |
| `export_process` | exportProcess | `process`, `format` (`xstate`) | the XState v5 machine config text (canonical key order; the model data without an XState home under `meta.maquettiste`), then, when there are any, a second block `{ diagnostics }` (MQ9404, MQ9406) |
| `import_process` | importProcess | `config` (object or string), `package`, `name`, `use`, `subject` for a new process, or `into` (id or name) and `expectedHash`; `apply` (false: a dry run) | `{ document, diagnostics, created, removed, applied, id, hash }`; the diagnostics are MQ9401 to MQ9405 (and, when applied, the save's findings), and an error among them (MQ9403, MQ9405) or a changed `into` refuses the apply |
| `sync_enum_from_process` | syncEnumFromProcess | `process` (a lifecycle), `expectedHash`, `apply` (false: a dry run) | `{ process, enum, added, removed, reordered, refused, applied, diagnostics }`; a member still in use is refused, never removed |
| `get_references` | getReferences | `id` | where the element is used; a custom property value an `x-ref` types is a row too, with `schema` (`extension:<name>` or `stereotype:<key>`) and `optional` (true when `remove-references` may clear it) |
| `localization_status` | getLocalizationStatus | | `defaultLocale`, `declared`, and per translated locale its `chain` and per shard `expected`, `translated`, `missing`, `stale`; the counts include the texts of `x-translatable` custom properties |
| `get_translations` | getTranslations | `locale`, `owner` or `shard` (optional), `missing` (the entries that need work, 200 per page), `cursor` | `{ entries, cursor }`: per field (`displayName`, `pluralName`, `label`, `description`, or `properties/<name>` for an `x-translatable` custom property) the `source`, `translation`, `effective` text, `state` (`translated`, `missing`, `stale`, `fallback`), `shard` and `shardHash` |
| `set_translations` | putTranslations | `locale`, `entries` (`[{ id, field, value, confirm? }]`, `field` a standard field or `properties/<name>`, `value` null removes), `expected` (`{ "<shard path>": "<shardHash>" }`) | `{ outcome, hashes, diagnostics }`; a changed shard is `conflict`, an unknown id or field (a property no schema marks `x-translatable` among them) `invalid` |
| `create_seed` | (CLI `seed new`) | `type`: a reference type id | the seed id: a new seed named after the type with the columns `code`, `label` and `description` and no rows, or the seed the type already has (nothing changes). `create_element` of a seed for a reference type without `columns` gets the same three |
| `export_seed_csv` | exportSeedCsv | `id` of a seed, `bom` (byte order mark and CRLF), `locales` (adds `@label:<locale>` and `@description:<locale>`) | the CSV text: `@id`, then `@code`, `@label`, `@description` for a reference type, then attribute names and end roles |
| `import_seed_csv` | importSeedCsv | `id`, `csv`, `mode` (`merge` default, or `replace`), `apply` (false: a dry run), `expectedHash` | the preview: `added`, `changed` (before and after), `removed`, `blocked` (rows other seeds reference, kept), `ignoredHeaders`, `applied`, `hash` |
| `reference_type_usage` | getReferenceTypeUsage | `id` of a reference type | `usages`: attribute, owner, domain, collection, required and the effective storage per database |
| `tag_usage` | getTagUsage | `package` (a domain; the global vocabulary without it), `tag`, `cursor`, `limit` (1 to 1000), all optional | a summary, `{ package, vocabulary, strict, tags, next }`, each tag `{ tag, declared, uses, examples }` in key order: the tags the vocabulary declares and the undeclared ones used in its scope, with their uses there and up to five names of the elements that hold them; the whole list unless `limit` or `cursor` pages it. With `tag`: `{ package, tag, declared, uses, elements, next }`, the ids of the elements that hold it in pages, in id order (a tag the scope neither declares nor uses is `not-found`). Remove or rename tags everywhere with `apply_batch` `retag` |
| `validate` | validate | `elementIds` (optional scope), `includeReferrers`, `includeScriptRules` (default `true`: the script rules of `extensions/rules/` run, their findings are `x/<id>`; whole-model rules, `scope: "model"`, run only without `elementIds`), `against` (a model snapshot id, or a folder holding another copy of the project, relative to the server's current directory; the editor's assistant takes snapshot ids only: compares the whole model with that baseline, without `elementIds`) | the report: diagnostics with rule ids, file, JSON pointer, line and column; counts. With `against`, a change since the baseline that no migration fact explains is MQ4102, a stale fact MQ4103, baseline objects whose owners have errors, or whose files did not load, are skipped with MQ4109; a value that names nothing is `bad-request` |
| `list_validation_rules` | listValidationRules | `family` (such as `MQ41xx`), `ids` (exact rule ids), `query` (text in the id or description, ignoring case), `cursor`, `limit` (1 to 1000, default 100), all optional, AND | none given: a summary, `{ total, families: [{ family, familyLabel, rules, first, last }] }` in id order; else one page `{ items, next }` of full entries in id order: `id`, `defaultSeverity`, `description`, `family` (the hundreds group, such as `MQ72xx`) and `familyLabel`, `canBeOff` (false for MQ1xxx), `quickFix` (the batch operation that fixes a finding, when the rule has one); override a severity with `validation.rules` through `save_settings` |
| `get_database_view` | getDatabaseView | `id` of a database | the resolved physical view: the tables of the entities mapped to it (by its `byConvention` setting and its `packages`, or one by one by mapping elements, less the ignored ones), with columns, keys, indexes and foreign keys, views, sequences, `routines` (parameters and result with native types, language, body, `dependsOn`), `types` (database types with `isCreated` and `nativeName`), `objects` (SQL objects with `objectKind`, `phase`, `dependsOn` and body), `queries` (parameters, sources, the select list with each field's inferred type, the trees as the file writes them, collections with their correlation keys, `sql` for the database's dialect and `uses`) and `schemas` (`name`, `isDefault`, `isDeclared`). The database, each schema, table, column, view, sequence, routine, database type, SQL object and query carries the annotations of its own file or entry: `displayName`, `pluralName`, `description`, `stereotypes` (keys), `tags`, `category` (id), `properties` (merged with the stereotypes' defaults) and `generation`; a synthesized table without an overlay, or a synthesized column without an overlay entry, has none, whatever its entity or attribute carries. The database also gives `quoting`, `maxIdentifierLength`, `byConvention` and `packages`; a column its `default`, `sequenceId`, `dbTypeId` (the database type its native type names), `collation`, `comment` and `computedStored`. A new database with nothing mapped has no tables |
| `preview_binding_sql` | getBindingSql | `entity` (id), `binding` (the binding's id, `entity.bindings[i].id`), `dialect` (the binding's database's when absent), `placeholder` (`@` default, `:` or `$`) | `{ preview: { entityId, bindingId, database, dialect, select, selectByKey, insert, update, delete }, diagnostics }`: each statement `{ sql, parameters }`, or null when the binding does not have it (a read-only binding has no insert, update or delete; no key fields, no selectByKey). `select` reads every row with the constants as a filter (and without soft-deleted rows; a query source is a derived table), `insert` writes the fields and constants without the columns the database fills and returns generated keys (`RETURNING`, `OUTPUT INSERTED`, or `SELECT LAST_INSERT_ID()` on MySQL), `update` sets the updatable fields by key with the constants in the where clause, `delete` deletes by key or runs the soft-delete update. Parameters are named after the fields; `$` numbers them after a query source's own. `preview` is null on a model with errors; an unknown binding is `not-found` (`not-a-binding`) |
| `get_materialize_status` | getMaterializeStatus | `database` (id) | `{ database, databaseName, entities: [{ id, name, package, projected, table }], sources: [{ id, kind, name, schema, origin, boundBy: [{ entity, entityName, binding, constants }] }] }`: the entities with no binding to the database (`projected`: the database still projects a table for it, by convention or a mapping element), and its designed and imported tables and its views with the entities bound to each (none means no entity reads it; several are told apart by their constants) |
| `preview_materialize` | previewMaterialize | `database`, `op` (`materialize-tables` with `entities` and an optional `schema` id; `materialize-entities` with `tables` and `package`; `materialize-attributes` with `entities` and, for one entity, optional `columns`; `materialize-columns` with `entities` and, for one entity, optional `attributes`) | `{ operation, database, valid, creates, updates, deletes, notes, diagnostics }`, each change `{ id, kind, name, path, because, element }` (`element` the document that would be written); nothing is written. A refusal (an entity or table already bound, an entity in an inheritance hierarchy, a name taken) is MQ4055 in `diagnostics`; the binding rules MQ4044 to MQ4054 come from resolving the model after the change. Apply it with `apply_batch` |
| `preview_query_sql` | getQuerySql | `id` of a query, `dialect` (`postgresql`, `sqlserver`, `mysql`, `sqlite`, `oracle`; the database's when absent), `placeholder` (`@` default, `:` or `$`), `lists` (`expand` default, or `any`) | `{ preview: { id, name, database, dialect, sql, parameters, collections: [{ name, sql, parameters, keys }] }, diagnostics }`: the query's statement and one per collection, each with the parameters it names in first-appearance order; a list parameter is `IN @name` (for a data access library that expands lists) or, with `lists: any`, `= ANY(@name)` on `postgresql`; a collection's statement takes the parent rows' key values as `mq_keys0` and returns each row's as `mq_key0`, matching the parent's `keys[i].parentField`; `preview` is null on a model with errors; an `sql` expression without a text for the dialect is MQ4029; not a query is `not-found` (`not-a-query`) |
| `list_packs` | (part of getProject) | | pack manifests and their diagnostics |
| `get_settings` | getSettings | | `maquettiste.json`: typed settings, canonical `json`, `hash` |
| `save_settings` | saveSettings | `settings` (whole document), `expectedHash` | the save result |
| `format_model` | formatModel | `paths` (repo-relative model files; default every model file) | `{ formatted, skipped, refused, total }`: the files rewritten in canonical form (MQ1003 then goes away; `maquettiste.json` also loses the retired `commit` flag, MQ1010); content does not change; a path that is not a model file fails with `bad-request` and nothing is written |
| `plan` | startPlan, run to completion | `packs`, `units` (`<pack>/<unit>`, or a unit id only one run pack has), `elements` (element ids, never names; a synthesized table's id is `<entityId>@<databaseId>`), `force`, `handEdits` (`fail`, `overwrite`, `skip`), `jobs`, `against` (a model snapshot id, or a folder holding another copy of the project, relative to the server's current directory: the schema diff runs from that baseline instead of the committed physical snapshots, which the apply leaves alone) | `{ outcome, plan }`, the plan without its per-unit list (as a plan job's `planResult`), with `counts` of every file kind and `unitsRendered` / `unitsSkipped` by reason; files left alone because they are identical (`unchanged`) or were not rendered (`not-rendered`) are only counted. The plan stores `against` (a folder as its full path); a value that names nothing is `bad-request`. `units` and `elements` limit the plan (values of one OR, the two AND; see "Scoped generation" under Semantics): it holds the selected units only, the plan stores both, and its apply writes only their files |
| `get_plan` | getPlan | `planId`, `units` | a stored plan with every file the packs produce, each with what apply does to it (`added`, `modified`, `deleted`; `unchanged`, `not-rendered` and `kept` are left alone; the hand-edit kinds); with `units` true, every unit with its pack, unit, template, element, `reason`, `causes` and `skipped` |
| `get_plan_diff` | getPlanDiff | `planId`, `path` | the unified diff text of one planned file |
| `apply_plan` | startApply, run to completion | `planId` | the apply result (as an apply job's `applyResult`); a plan made `against` a baseline opens it again (`stale` when it changed, `bad-request` when it no longer opens) |
| `generate` | (CLI `generate`) | `packs`, `units`, `elements`, `against`, `force`, `handEdits`, `jobs` as for `plan`; `noWait` (`true`: `busy` at once when another run holds the run lock) | renders and writes in one run, storing no plan: `{ outcome, unitsRendered, unitsSkipped, filesWritten, filesDeleted, packs: [{ pack, counts, files: [{ path, kind }], more }], diagnostics, moreDiagnostics }`. Each pack that changed something has its files counted by kind (only the kinds that occur), its first 50 paths in path order and `more` for the rest; files left alone as `unchanged` or `not-rendered` are not listed. At most 100 diagnostics, errors first, and `moreDiagnostics` counts the others. No run id or timings. Any other outcome is a tool error with the same fields: `invalid` (units without errors may have written their files), `conflicts`, `busy`, `failed`; a baseline that names nothing is `bad-request` |
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
| `list_extension_files` | listExtensionFiles | | the files of `.maquettiste/extensions/`: custom property schemas (`<name>.json`, kind `schema`) and script rules (`rules/<name>.js`, kind `rule`), each with size, hash and what is wrong with it on its own (MQ5004 for an invalid schema, MQ5002 or MQ5003 for a rule script that does not load, with line and column, MQ5005 for one that declares `let` or `var` at its top level), and for a rule script its `rules`: `{ id, severity, wholeModel }`, `wholeModel` true for a rule registered with `scope: "model"` |
| `read_extension_file` | getExtensionFile | `path` (under `extensions/`) | the text and hash of one schema or rule script |
| `write_extension_file` | putExtensionFile | `path`, `text`, `expectedHash` (`new` creates) | the write result; a schema that is not JSON or fails `extension.json` is `invalid` (MQ5004) and nothing is written, a valid one is written in canonical form (`text` is what was written); a rule script is written as sent and loaded alone in the sandbox, its syntax error or registration without an id returned in `diagnostics` (MQ5002 with line and column), as is the warning MQ5005 for a top-level `let` or `var` |
| `move_extension_file` | moveExtensionFile | `from`, `to`, `expectedHash` | the move result; the target must not exist and keeps the kind (`<name>.json` or `rules/<name>.js`) |
| `delete_extension_file` | deleteExtensionFile | `path`, `expectedHash` | the delete result |
| `get_template_context` | getTemplateContext | `pack`, `unit` | what the unit's templates can use: the globals, the members of the model and of the scope's records, the helpers |
| `preview_unit` | previewTemplate | `pack`, `unit`, `elementId`, `overlay` (path to unsaved text), `unitOverride` | each rendered file's output path and text (a copy unit's binary file has `binary: true` and empty text), the diagnostics and the keys the render read; nothing is written; an element outside the unit's scope (none for an `each` unit, another kind, one its selector does not return, any for a `model` unit) renders nothing and returns MQ6026, which names the kind the template expects; only that unit is rendered, for that element, and no unit is planned; a unit whose one render covers the whole model (`for: model`), a database or a locale renders all of it, so ask for it when its output is what you need |
| `unit_paths` | unitPaths | `pack`, `unit`, `elementIds` (at most 20), `limit` | without `elementIds`: how many elements the unit plans (`count`) and the elements themselves (`elements`: id, name, kind, up to `limit`, default 200), rendering nothing; with `elementIds`: those elements rendered for their output paths, with their root and whether the writer allows them, each path naming its element (`elementName`: an element's name, a table as `customers (billing)`, a locale's tag) and `elementKind`; more than 20 is `bad-request`. `wide` is true for a unit whose one render covers the whole model, a database or a locale: its paths come with `preview_unit`. MQ6020 also flags an output pattern with no code span over several elements |
| `get_pack_outputs` | getPackOutputs | `pack` | the files the pack's manifest records: path, unit, element, root, mode (`overwrite`, `regions`, `once`, `block`) and state on disk (intact, edited, missing; a block by its lines) |
| `explain_unit` | getPlanUnit, explainUnit | `planId` and `key` (a unit of a stored plan), or `pack`, `unit`, `elementId` (any unit and element) | the reason (`new`, `forced`, `inputs`, `outputs`, `unchanged`, or why it does not run: `pack-disabled`, `not-selected`, `scope`, `filter`, `skip-hint`, `selector`, ...; a rendered block unit whose target file is missing without `createFile` answers `target-missing`), the causes and a one-sentence summary |
| `list_snapshots` | listSnapshots | | the model's snapshots (zip archives under `.maquettiste/model-snapshots/`), newest first: `id`, `name`, `description`, `author`, `createdUtc`, `origin` (`user`, or `before-restore` for a restore's safety snapshot), `published`, `includesPacks`, `modelHash`, `files`, `elements`, `kinds`, `size` |
| `compare_snapshots` | compareSnapshots, compareSnapshotElement | `from` (a snapshot id or `working`), `to` (default `working`), `offset`, `limit` (1 to 5000, default 500); or `id` (an element id) | `{ from, to, added, removed, changed, kinds, elements: [{ id, kind, name, change, path, previousName, previousPath }], next, files: [{ path, change }], filesTruncated, packsCompared }`; with `id`, that element's two documents (`before`, `after`) and the fields that differ as JSON pointers (`fields`, at most 500) |
| `create_snapshot` | createSnapshot | `name`, `description`, `includePacks` (default `false`) | the snapshot (`id` is the kebab-case name and the UTC time, `<slug>-<yyyymmdd-hhmmss>`) |
| `restore_snapshot` | restoreSnapshot | `id`, `includePacks` (default `false`; only when the snapshot holds them) | `{ outcome, snapshot, safety, written, deleted, packsRestored, undo, elementsChanged, elementsDeleted, diagnostics }`: the working model becomes the snapshot's after a safety snapshot of it (`safety`; restore that one to undo); `run-locked` while a generation run holds the run lock. Destructive: read `compare_snapshots` with `from` `working` and `to` the id first |
| `list_migrations` | (none) | `database` (name or id), `cursor`, `limit` (1 to 1000, default 100) | the open migration facts in applied order (by database name, then the order their `after` links give, ties by id), one page: `{ facts: [{ id, database, databaseName, order, fact, state, stale, missing, summary, object, after, path, hash }], total, next }`; pass `next` as `cursor` until it is null. `state` is `pending`, `complete`, `held` or `released`; `missing` names the intent a pending fact lacks; `stale` marks a fact `fold_migrations` removes (MQ4103). An unknown database or cursor is `bad-request` |
| `fold_migrations` | (none) | `database` (name or id), `against` (a model snapshot id, or a folder holding another copy of the project, relative to the server's current directory), `dryRun` | `{ outcome, joined: [{ kept, removed, fact }], removedStale, refused: [{ facts, reason }], files, diagnostics }`: the facts the baseline lacks (`against`'s, else those each database's committed physical snapshot lists as rendered, else pending facts only) lose their stale ones, and consecutive facts on one object join into the last one (renames keep their intermediate names in `via`; a widening joins its neighbour; two pending `change-type` facts join; a rename then a drop join into the drop). Two conversions, or a join that would order facts in a cycle, are refused and stay. `files` (with `dryRun`) lists each file with its text before and after. `conflict` when a fact file changed, `invalid` when the model has errors outside the migration rules, `bad-request` for an unknown database or baseline |
| `get_ledger_ddl` | (none) | `database` (name or id; optional when the model has one PostgreSQL database), `grantRead` (a role) | `{ contractVersion, database, dialect, schema, ddl, grant }`: the migration ledger's DDL for one PostgreSQL database (docs/ledger.md), three tables in the schema `migrations.ledgerSchema` names (default `migration_ledger`), idempotent and without transaction control, so a migrator runs it in its own transaction; `grant` is the read grant for `grantRead`, else null. The engine never reads a ledger. No PostgreSQL database, an unknown or other database, several PostgreSQL databases and none named, or an empty `grantRead` is `bad-request` |
| `get_schema` | (none) | `kind`: an element kind (`entity`, `value-object`, ...) or a document (`maquettiste`, `batch`, `pack`, `extension`) | `{ name, file, schema, references, extensions?, stereotypeParameters? }`: the JSON schema, the schema files it references (`common.json`) and, for an element kind, the project's extension schemas that apply to it (`name`, `description`, `appliesTo`, `properties`, `required`, `schemaPath`; they constrain the element's `properties`) and the typed parameters of the stereotypes that may apply to it (`[{ stereotype, parameters: { properties, required } }]`, by key; they constrain the `properties` of the elements that carry the stereotype, implied ones included) |

Documents may be passed as JSON objects or as strings holding one. Writes are recorded as `ChangeSource.Cli`.

The pack tools let a client write a pack end to end, as the editor's Generate screen does: `new_pack`, add a unit with
`save_pack`, write the template with `write_pack_file`, render it on an element with `preview_unit` (pass the unsaved text
as `overlay` to try it before saving), fix the diagnostics, check where the files land with `unit_paths` (list the scope, then ask for a few elements' paths), then `plan`,
`get_plan` with `units` true, and `explain_unit` for the reason each unit renders or does not. The files are the ones under
`.maquettiste/templates/<pack>/` that the command line, the editor and git all see.

The extension file tools edit the model's own extension points the way the editor's Extensions screen does: custom
property schemas (`extensions/<name>.json`, the properties the inspector shows for a kind or a stereotype) and script
rules (`extensions/rules/<name>.js`, each registering
`maquettiste.rule({ id, severity, kinds, check(element, model, report) })`, or a whole-model rule with `scope: "model"`
and `check(model, report)`, whose `report` names the `element` of each finding). Write a rule with
`write_extension_file`, read its syntax check in the answer, then run `validate`: script rules run by default
(`includeScriptRules`), their findings are `x/<id>`, and a rule file that does not load is MQ5002 on that file with line
and column while the other rule files keep running. A whole-model rule runs once per `validate` without `elementIds`,
never on a save. A rule's `model` also finds documents by stereotype, tag, own property key or qualified name
(`withStereotype`, `withTag`, `withProperty`, `byQualifiedName`). Each rule file runs in its own scope with the globals
frozen first, so a rule cannot call a function of another file; a file that declares `let` or `var` at its top level is
MQ5005 and its results are never kept. The server keeps each rule's result per element in memory and runs it again only
when what it read changed. For CI, `limits.scriptTimeoutMs: 0` turns the scripts' wall clock off so findings do not
depend on the machine's load; the statement limit still bounds every call. docs/user-guide.md, "Extending the model and
the generation", has the contract.

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

Routines, database types and SQL objects (added 2026-10-01) are the kinds `routine`, `database-type` and `sql-object`, each
belonging to a database (`database`, and a `schema` id), in `model/databases/<db>/routines/`, `types/` and `objects/`. They work
with every element tool, `get_model_index` and `get_elements` (`kind`, and index rows carry `database` as views do),
`get_schema` and `apply_batch`; `get_database_view` lists them, and `get_resolved_model` reads them in the database records or
one at a time with the scopes `routines`, `database-types` and `sql-objects` (records `{ id, kind, name, database, routine }`,
`databaseType` or `sqlObject`). A routine's parameter and result types and a composite's field types are a built-in keyword or
the id of a database type of the same database; a column uses a database type by naming its id or name in `nativeType`.
`validate` reports MQ4017 to MQ4020 for them.

Queries (added 2026-10-02) are the kind `query`, one file per query in `model/databases/<db>/queries/`, each belonging to a
database (`database`, no schema): a query over the database's tables and views written as JSON trees (`from`, `joins`,
`select`, `where`, `groupBy`, `having`, `orderBy`, `paging`, `collections`), with an `entity` as its result shape or, without
one, its select list. Column references are `alias.<column key>` (the `key` the database view lists for a column: an attribute
id, a value object member's attribute path, a designed column's id), so the reference index sees them; a physical name is
accepted too. They work with every element tool, `get_model_index` and `get_elements` (`kind: "query"`), `get_schema` and
`apply_batch`; `get_database_view` lists them with their SQL, `get_resolved_model` reads them in the database records or one at
a time with the scope `queries` (records `{ id, kind, name, database, query }`), and `preview_query_sql` renders one for any
dialect. `validate` reports MQ4021 to MQ4043 for them (the resolver checks them, where the columns are known). Deleting a table,
view, entity or attribute a query reads refuses with the query among the referrers; with `delete-dependents` the query is
deleted whole.

Bindings (added 2026-10-02, erratum E43) live in the entity's file, `bindings`, one per database: where the entity reads
(`source`: a table, a view or a query of the database), its `constants` (a filter on every read and a value on every insert,
several allowed, which is how several entities share one table through an `entity_type` column), its `fields` (attribute id,
`attributeId.memberId` or a to-one relation end id, and the source column it reads, by key or name), the `columns` it accounts
for without a field (`ignored`, `database`, `computed`), where it writes (`write`: `{ "table": id }` or `"none"`; the source
when it is a table) and how it deletes (`delete`: `"key"`, `{ "soft": { "column", "value" } }` or `"none"`). An entity with a
binding to a database is never projected into it. Bind entities explicitly: create the binding with `save_element`, or let
`apply_batch` write it with `materialize-tables` (a designed table with the shape the entity's projection has, and the binding)
or `materialize-entities` (an entity bound to each table), or give a bound entity attributes for its source's columns with
`materialize-attributes` (an attribute per column nothing in the binding names, or per column named, mapped to it), or give its
table columns for its unmapped attributes with `materialize-columns` (shaped as the projection would make them); `get_materialize_status` says what is left to bind and
`preview_materialize` shows a plan first. `get_database_view` lists on each table, view and query its `boundBy` (the entities
that read or write it, with their constants); `get_resolved_model` gives each entity record its resolved `bindings`;
`preview_binding_sql` renders a binding's five statements for any dialect. `validate` reports MQ4044 to MQ4054 for them
(MQ4047, a column nothing accounts for, is the "a mapping for every column" warning). Deleting a table, view, query or
database a binding names refuses with the entity among the referrers; with `remove-references` or `delete-dependents` the
binding is removed and the entity stays.

Reference types and seeds are elements: `get_element`, `create_element`, `save_element` and `apply_batch` handle them. Translations
live in locale shards, not in element files: read them with `get_translations`, then pass the `shardHash` values you read as
`expected` to `set_translations`, which writes every entry in one atomic save (reference-types-seeds-localization.md section 3.9).
`import_seed_csv` previews unless `apply` is true; applying saves the seed with `expectedHash` and then the translations of the
`@label:<locale>` and `@description:<locale>` columns.

### Semantics

- **Hashes.** `get_element` and `get_settings` return the file's `hash` (the API's ETag). `save_element`,
  `patch_element`, `delete_element`, `save_settings` and batch updates and deletes take it as `expectedHash` (the API's
  `If-Match`). `save_element`, `patch_element`, `delete_element` and `save_settings` without it fail with
  `precondition-required`; a batch operation without it fails with `invalid` (rule MQ1002 on `/operations/<n>`), like
  applyBatch's 422.
- **Conflicts never overwrite.** When the file changed since it was read (by the editor, git, or the agent's own file
  edits), the save writes nothing and fails with `conflict`: `current` and `hash` are the disk version and `submitted`
  is the document (or the patch) the caller sent, so both versions are in hand to merge and retry with the new hash.
- **Patches.** `patch_element` changes part of one element without sending the whole document: `patch` is an RFC 6902
  JSON Patch, an array of `add`, `remove`, `replace`, `move`, `copy` and `test` operations with RFC 6901 paths, applied
  in order to the file document `get_element` returns in `json` (`$schema` included). The operations apply all or
  nothing, and the result is saved like a one-operation `apply_batch` update with `expectedHash`: the same conflict
  check, validation, renames and migration facts (the answer's `facts`). The `id` is an id, never a qualified name, so a
  name is `not-found`. Values inside the patch may name other elements as a batch does, and a new sub-element sent
  without an id gets a minted one, listed in `ids`; keep the ids of sub-elements you move or re-send, as the "keep its
  id" refusal (MQ1014) applies. Numbers in `test` compare by value, so `1` and `1.0` are equal. An unknown `op`, a
  missing member, a `path` or `from` that does not resolve, an array index out of range, a `test` that fails, or a
  change to `/id` or `/kind` is `invalid` with one MQ1016 whose pointer is `/<operation index>` in the patch (`""` when
  the patch is not an array of operations, such as an object or a number), and nothing is written. Every other
  diagnostic points into the document, as `save_element`'s do: a name in the patch that does not resolve (MQ1012 to
  MQ1015) or a value that does not read (MQ1002) is reported at its place in the patched document, never in the batch
  the patch is saved as. Example, on billing: `{ "id": "<notes table id>", "expectedHash": "<hash>", "patch": [ { "op":
  "test", "path": "/columns/3/name", "value": "body" }, { "op": "replace", "path": "/columns/3/name", "value": "content"
  } ] }` renames the column and records its rename fact.
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
  `apply_plan` writes exactly the planned bytes, or fails with `stale` (`staleUnits`, `stalePaths`) writing nothing when
  the model, the templates, a planned file or the plan's `against` baseline changed since. Generation runs one at a
  time: the run lock is shared with every other process on the repo (a second run waits).
- **Generate in one call.** `generate` renders and writes in one run under the run lock, as `maquettiste generate`
  does, so it renders once, nothing can change in between, and no plan is stored. Its answer is a summary by pack. Use
  `plan`, `get_plan_diff` and `apply_plan` when someone should read the files before they are written.
- **Scoped generation.** `units` and `elements` on `plan` and `generate` limit a run to some units. A unit value is
  `<pack>/<unit>` or a unit id that exactly one run pack has; an element value is an id, never a name, and never selects
  a unit that runs once for the whole model. Values of one argument OR, the two arguments AND. The run writes only the
  selected units' files and still deletes the files they no longer produce, so `elements: ["<deleted id>"]` removes
  that element's outputs; every other file, manifest entry and unit state stays as the last run left it. A scoped run
  never advances the schema snapshots, so a selected unit that reads the schema diff (`sql-ddl/migration`, which a
  database id selects with every other `each database` unit) fails with MQ6033 and keeps its files; regenerate one
  database's schema with `units: ["sql-ddl/schema"], elements: ["<database id>"]`. A unit value naming a pack the run
  lacks, a unit no run pack has or a unit id several run packs have is MQ6032; both errors make the run `invalid`. A
  filter that selects no unit and no earlier output is the warning MQ6034 and generates nothing. A scoped plan stores
  its filters, and `apply_plan` plans again with them, so the units it left out never make it stale.
- **Fresh reads.** Every model read (and every element write) starts with a stat rescan of the model folder, so files edited
  behind the server are seen; settings reads and saves check the settings file on disk; `get_plan` and `get_plan_diff` read the
  stored plan.
- **Arguments.** The input schemas list the required arguments. A missing one, or one of the wrong JSON type, is answered with
  `bad-request` and a `detail` naming the argument (`kind must be a string, not a number.`), never the SDK's generic error.
- **Cancellation.** A `notifications/cancelled` for a running call cancels the engine call (a cancelled plan stores nothing and
  releases the run lock); per the protocol the cancelled call gets no result. Note: the C# SDK's client (2.2.0) does not send the
  notification when a call's token is cancelled; it only stops waiting.

### Batches by name

`apply_batch` and `maquettiste model apply` take names where ids go, so an agent can build a change without reading or
writing ids. Files stay id-based: the engine turns every name into an id before it plans the batch, and mints every id a
new element or sub-element needs. An update or delete still takes `expectedHash`: a name changes how the target is
written, not the conflict rule.

**Where a name may stand.** In the id fields of an operation (`id`, `database`, `package`, `schema`, `target`,
`default`, the items of `entities`, `tables`, `attributes` and `elementIds`, the keys of `expectedHashes`) and wherever
an element refers to another element or sub-element: the sites `get_references` lists (typed and keyed references, type
references, id-keyed maps such as `storage`, custom properties an `x-ref` types). Free text, seed cells, `columns` (keys
or physical names) and other physical names are data and are never rewritten. A composite `attributeId.memberId` item of
`attributes` stays ids, and a migration fact's keys and `after` take ids only.

**Grammar.** A string at a name site that starts with `@` or `$` is a name; no id starts with either.

```text
reference   = "@" kind ":" path
placeholder = "$" ( ALPHA / "_" ) *( ALPHA / DIGIT / "_" / "-" )
path        = segment *( "." segment )
segment     = 1*( any char but "." DQUOTE white-space ) / DQUOTE *( any char but DQUOTE / 2DQUOTE ) DQUOTE
```

A segment holding a dot or a space is quoted, a quote inside doubled. Names compare ordinally with the names as written
in the files, never with resolved ones (naming conventions and prefixes do not apply), so a lookup never resolves the
model.

**Forms.** Each kind registers the forms below, and a name finds what is registered under exactly that form. A bare name
that two elements share is ambiguous (MQ1013): a longer form qualifies it, and when every form is shared, the id is the
way. A `<package path>` is the full path from a root package or any trailing part of it, so `@entity:Catalog.Product`
finds Product in `Billing.Catalog`. A form with an empty segment (no stored name, no schema and no default schema) is
not registered.

| Kind | Forms |
| --- | --- |
| `package` | `Name`, or the path from a root package or a trailing part of it: `@package:Billing.Catalog` |
| `entity`, `value-object`, `scalar-type`, `enum`, `process`, `relation`, `diagram` | `Name`, `<package path>.Name`: `@entity:Billing.Invoice` |
| `database`, `mapping`, `reference-type`, `seed`, `actor` | `name`, plus `<package path>.name` when the file states a package: `@database:main`, `@seed:Billing.currencies` |
| `stereotype` | the key: `@stereotype:audited` |
| `table`, `view`, `sequence`, `routine`, `database-type`, `sql-object` | `name`, `schema.name`, `database.schema.name` (the schema, else the database's `defaultSchema`): `@table:billing.notes` |
| `query` | `name`, `database.name` |
| `scenario` | `name`, `<process form>.name` |
| `attribute` | `<owner form>.name` for an entity, value object, relation, reference type or stereotype: `@attribute:Invoice.total`, `@attribute:settles.allocated`, `@attribute:audited.createdAt`; a process's context `<process form>.name`; an event's payload `<process form>.<event>.name`; a gate's audit attributes `<process form>.<gate>.name` |
| `column` | `<table form>.name`: `@column:billing.notes.body` |
| `schema` | `name`, `database.name`: `@schema:billing` |
| `enum-member`, `event`, `guard`, `action`, `invoke`, `gate` | `<enum or process form>.name` |
| `meaning` | `<process form>.<gate>.name` |
| `state` | `<process form>.<state path>`, parent states first |
| `end` | `<relation form>.<role, else navigation>` |
| `reference-field` | `<reference type>.code`, `<reference type>.label` |
| `row` | rows of reference-type seeds: `<reference type>.<code>` and `<seed name>.<code>`; integer codes in invariant digits |
| `binding` | `<entity or reference type form>.<database name>` |
| `key` | `<table form>.<name>` (unique, foreign key, check, index), `<entity form>.<name>` (alternate key) |
| `category` | names from the tree's root, in any tree: `@category:Finance.Receivables`; in a package's tree, also `<package path>.<category path>` |
| `tag-vocabulary`, `category-tree`, `migration`, `transition`, `step`, rows of table seeds, unnamed keys | none: an id or a placeholder |

**Placeholders and order.** `"as": "$name"` on a create or an update names its element for the whole batch; on an object
inside the element whose schema requires an `id` (an attribute, a column, a row) it names that sub-element. Placeholders
reach forward and back, so two new tables can reference each other. An update's or delete's `id` names an element that
exists before the batch, by id or qualified name, never by placeholder. Every other name sees the model as the earlier
operations leave it, with the operation's own element as it will be written, so a primary key names its own new column.
A form that goes through an element renamed earlier in the batch (a package path, a database, a schema, a table, an
owner) keeps its old qualifier for the elements the batch does not touch: use the id or a second batch. Names see the
creates, updates and deletes before them, not what schema, materialize, process and tag operations produce.

**Minted ids.** Every object whose schema requires an `id` gets one when it has none: a create's element (its `id`, else
the operation's `id`, else a new id) and each sub-element below it. The order is fixed: operations in order; in an
element, the root first, then keys in schema order, array items by index, an object before its children. The answer's
`ids.minted` lists each `{ operation, pointer, kind, id, name, placeholder }`, where `name` is the longest qualified
form (`@column:main.billing.payment_method.code`), ready to paste into a later batch; `ids.names` maps each name and
placeholder the batch used to its id. A dry run mints too, and an apply mints again: take the ids from the apply's
answer.

**Keep its id.** A sub-element without an id in an update is new: the engine never matches by name. An update that drops
a sub-element and adds one without an id under the same parent and property with the same name (`name`, or a
reference-type seed row's `code` cell) is refused with MQ1014, because the drop would also remove the seed columns and
cells that name the old id. Send the existing id to keep it.

| Rule | When |
| --- | --- |
| MQ1012 | A name or placeholder finds nothing: no element or sub-element of that kind has the name (an update's or delete's target on the model as loaded, any other name on the model as the earlier operations leave it), or no operation declares the placeholder. |
| MQ1013 | A name finds more than one element or sub-element; the message lists each candidate's longest form and id. Use a longer form, or the id when every form is shared. |
| MQ1014 | An identity is misused: a placeholder declared twice; `as` on a delete, a schema, materialize, process or tag operation, or on an object that is not a sub-element; two `expectedHashes` keys (or two keys of an id-keyed map) for one element; or the "keep its id" case above. |
| MQ1015 | A name is malformed or misplaced: not `@kind:path` or `$name`, an unknown kind or one without names, an empty or unclosed quoted segment, a name as a create's id (declare a placeholder with `as`), or a placeholder where the element must exist before the batch (an update's or delete's id, an `expectedHashes` key). |

Any of them makes its operations `invalid`, with pointers under `/operations/<n>`, and nothing is written. One batch on
the billing model creates a table with two columns and its primary key, a reference type bound to it and the type's
seed, without one id:

```json
{ "operations": [
  { "op": "create", "as": "$pm", "element": { "kind": "table", "name": "payment_method",
      "database": "@database:main", "schema": "@schema:billing",
      "columns": [ { "name": "code", "type": "string", "length": 16, "nullable": false },
                   { "name": "label", "type": "string", "length": 64, "nullable": false } ],
      "primaryKey": { "columns": [ "@column:billing.payment_method.code" ] } } },
  { "op": "create", "as": "$type", "element": { "kind": "reference-type", "name": "PaymentMethod",
      "code": { "length": 16 }, "label": { "length": 64 },
      "bindings": [ { "database": "@database:main", "table": "$pm",
                      "fields": [ { "attribute": "code", "column": "code" },
                                  { "attribute": "label", "column": "label" } ] } ] } },
  { "op": "create", "element": { "kind": "seed", "name": "PaymentMethods", "target": "$type",
      "columns": [ "code", "label" ],
      "rows": [ { "values": [ "card", "Card" ] }, { "values": [ "transfer", "Bank transfer" ] } ] } }
] }
```

With `"dryRun": true` the answer shows the three files as diffs, the ten ids it would mint and the diagnostics the
change adds or removes, and writes nothing; `maquettiste model apply payment-methods.json --dry-run` prints the same.

### Seeds by name

A seed's `columns`, its `key` and its row cells may be given by name too, in `create_element`, `save_element`,
`patch_element` and `apply_batch` (and the editor's save and batch endpoints). The file still stores ids and one cell
per column. The engine works in a fixed order:

1. In a batch, the names and placeholders above resolve first, at `target`, `columns` and `key` as at any reference
   site: `"@column:billing.currency.code"` or `"$code"` becomes the column's id.
2. Then a bare name left in `columns` or `key` resolves among the seed's target: a table's columns by name (and for
   `key` its unique constraints), an entity's attributes (its own and inherited) and the roles of its to-one relation
   ends, a relation's end roles and attributes, a reference type's attributes. These are the names the seed CSV headers
   use. A bare name never starts with `@` or `$`, so the two steps never claim the same entry. Outside a batch only this
   step runs. In a batch the target may be a table an earlier operation creates.

`code`, `label` and `description` are keywords only when the target is a reference type. For a table, an entity or a
relation they are names like any other, so a table column named `code` is stored as its id.

A row may give its cells as a `values` object instead of an array: `{ "values": { "code": "EUR", "label": "Euro" } }`.
Its keys are the `columns` entries as the body writes them; a column left out is null. A key that is no entry is
MQ1002 at `/rows/<i>/values/<key>`. It is not added as a column, because that would shift the cells of every other row.

A name that finds nothing, or more than one child of the target, is MQ1002 at `/columns/<i>` or `/key`, naming the
target and each candidate's id; nothing is written. A seed on the billing model, with its table created in the same
batch:

```json
{ "operations": [
  { "op": "create", "as": "$cur", "element": { "kind": "table", "name": "currency", "database": "@database:main",
      "columns": [ { "name": "code", "type": "string", "length": 3, "nullable": false },
                   { "name": "label", "type": "string", "length": 40, "nullable": false } ],
      "primaryKey": { "columns": [ "@column:billing.currency.code" ] } } },
  { "op": "create", "element": { "kind": "seed", "name": "currencies", "target": "$cur", "columns": [ "code", "label" ],
      "rows": [ { "values": { "code": "EUR", "label": "Euro" } }, { "values": [ "USD", "US dollar" ] } ] } }
] }
```

Templates find the same seed by name with `seed_named "currencies"` (or `"Billing.currencies"` when its file states a
package) and one of its rows with `seed_row`; see the helpers in the pack-writing guide (`packs/README.md`).

### Errors

A failure is a tool error (`isError: true`), never a crash of the server. Its text is a problem object: the API's `code`, `status`
and `title`, an optional `detail`, and the fields of the body the API returns with that status:

| `code` | `status` | When | Extra fields |
| --- | --- | --- | --- |
| `bad-request` | 400 | a missing, wrongly typed or malformed argument, found by the tool's own checks | `detail` |
| `precondition-required` | 428 | no `expectedHash` | |
| `not-found`, `not-a-database` | 404 | no such element, plan, planned file or schema | `detail` for schemas (the known names) |
| `conflict` | 409 | the file changed since it was read | `current`, `hash`, `submitted` (saves and patches), `items` (batches) |
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

Three tools answer a summary by default, so an agent's first calls stay small, and give the full content on request:

- `get_project` names the packs with their unit counts, counts the pack diagnostics by severity and names the
  extensions. `sections` adds the typed settings (`settings`), the pack manifests and their diagnostics (`packs`) or the
  extension schemas (`extensions`).
- `list_validation_rules` with no argument answers the rule families with their counts and first and last ids. `family`,
  `ids` or `query` answer the full entries that match, in pages.
- `tag_usage` answers each tag with its use count and up to five element names, without element ids. `tag` answers the
  ids of the elements that hold one tag, in pages.

```json
list_validation_rules {}
{ "total": ..., "families": [
  { "family": "MQ10xx", "familyLabel": "Model files", "rules": 16, "first": "MQ1001", "last": "MQ1016" }, ... ] }

list_validation_rules { "family": "MQ41xx", "limit": 2 }
{ "items": [ { "id": "MQ4101", "defaultSeverity": "error", "description": "...", "family": "MQ41xx", ... }, ... ],
  "next": "..." }

tag_usage { "tag": "billing" }
{ "package": null, "tag": "billing", "declared": true, "uses": 3,
  "elements": [ "01J92P0V0ETQKXXP951CMMNHH3", ... ], "next": null }
```

Every paged read orders its rows by kind, then name, then id (ordinal), so the order is stable; the rule pages go by
rule id, the tag pages by tag key or, with `tag`, by element id. Pass the `next` of a page as `cursor` to get the
following one, until `next` is null; a cursor is opaque and encodes the last position read. A change to the model
between two pages does not fail the read: the next page starts after that position in the changed model, so an element
renamed in between may be read twice or not at all, and every other element is read once. The editor API serves the same
pages (`GET /api/model/elements`, `/api/model/resolved`, `/api/model/kinds`, and `/api/model/index` with `limit` or
`cursor`, whose next page comes in a `Link` header), and so does the command line (`maquettiste model export` and `model
stats`, in docs/user-guide.md, "The command line"). The examples below run on the billing fixture.

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
      "sequences": [ { "name": "invoice_number_seq", ... } ],
      "routines": [], "types": [], "objects": [],
      "queries": [ { "name": "FindCustomersWithIssuedInvoices", "sql": "SELECT c.id AS id, ...", ... }, ... ] }
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

The modeling conventions (`skills/maquettiste-modeling/SKILL.md`, embedded in the engine, without its front matter; the editor's
assistant puts the same text in its system prompt) are served as the
resource `maquettiste://conventions` (`text/markdown`) and as the prompt `modeling-conventions`, so any MCP client can read them.
When the repository has `.claude/skills/maquettiste-modeling/CONVENTIONS.md`, both end with it: a `---` separator, the heading
`# This repository's conventions (CONVENTIONS.md)` and the file's text, read again on every request, so an MCP client sees what the
skill file tells an agent to read. At most its first 64 KB are included, with a note saying so when the file is longer.
The server's instructions summarize the workflow.

## Notes

- The SDK is `ModelContextProtocol` 2.2.0 (pinned in `Directory.Packages.props`), used without a generic host: `McpServer.Create`
  over a `StreamServerTransport` on the process's raw stdin and stdout.
- The order of `tools/list` is the SDK's (not sorted).
- Plan and apply run to completion inside the call instead of going through the editor's job queue; their bodies are the job's
  `planResult` and `applyResult`. Progress notifications are not sent yet.
