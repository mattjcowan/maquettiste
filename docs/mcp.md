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
the current source 77), `validate` in 55 ms, the container removed on exit. The registration (the arguments exactly,
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
claude                               # then /mcp shows maquettiste connected with 77 tools
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
`get_resolved_model`, `get_calculated`, `get_schema`, `get_settings`, `get_database_view`, `validate`,
`list_validation_rules`, `preview_query_sql`, `preview_binding_sql`, `preview_expression`, `get_materialize_status`,
`preview_materialize`,
`get_binding_drift`, `list_packs`, `get_plan`, `get_plan_diff`, `preview_unit`, `reference_type_usage`,
`preview_seed_rows`, `tag_usage`, `localization_status`, `list_snapshots`, `compare_snapshots`, `list_migrations`,
`get_ledger_ddl`) are one catalog in the engine (`AgentTools`), shared with the editor's assistant (docs/user-guide.md,
"The assistant"): their names, descriptions, input schemas and answers are the same in both, and a change to one is a
change to the other. The assistant has none of the write tools; it proposes model changes for the user to review
(`propose_changes`) instead.

| Tool | API operation | Arguments | Returns |
| --- | --- | --- | --- |
| `get_project` | getProject | `sections` (optional: `settings`, `packs`, `extensions`) | a summary: name, `productVersion` (the release, such as `0.5.3`) and `build` beside `engineVersion` (the engine contract, not the release) and `formatVersion`, `workspace` (`MAQUETTISTE_WORKSPACE`, else the git branch, else the worktree name) with `branch`, `worktree` and `repository`, `mode` (`local`), `settingsHash`, databases, `packs` (`{ name, version, description, units }`), `packDiagnosticCounts` (`{ errors, warnings, info }`), `extensions` (their names), `customKinds` (the type keys of the custom kinds the project declares, ordinal) and `sections` (always `extensions`, `packs`, `settings`); each section asked for adds its full members: `settings` the typed settings, `packs` `packManifests` and `packDiagnostics`, `extensions` `extensionSchemas`; an unknown section is `bad-request` |
| `get_model_index` | getModelIndex | `kind`, `package` (id or name), `tag`, `category`, `stereotype`, `query` (name contains, ignoring case), all optional, AND; `cursor`, `limit` (1 to 1000) | element summaries (operation rows add `subject` and `effect`, custom element rows `type` and `memberCount`); `kind` takes `custom` for every custom element and `custom:<type>` for one custom kind; with `cursor` or `limit`, one page `{ items, next }` ordered by kind, name and id |
| `get_model_kinds` | getModelKinds | `by` (`kind` default, or `package`) | `{ total, kinds: [{ kind, count }], packages }`, custom elements counted as one `custom:<type>` row per custom kind; with `package`, per package `{ package, name, count, kinds }`, the elements in no package first |
| `get_element` | getElement | `id` | the document: `json` (canonical), `hash`, `path`, the typed element, the sidecar text |
| `get_elements` | getElements | `ids` (element or sub-element ids, at most 1000), the index filters, `fields` (top-level members to keep), `cursor`, `limit` (default 100, at most 1000) | `{ items: [{ id, kind, path, hash, json }], next, missing }`: the canonical documents in pages, ordered by kind, name and id; `kind` takes `custom` and `custom:<type>` as `get_model_index` does |
| `get_resolved_model` | getResolvedModel | `scope` (`all` default, `packages`, `entities`, `relations`, `enums`, `value-objects`, `scalar-types`, `reference-types`, `seeds`, `processes`, `actors`, `scenarios`, `operations`, `custom-elements`, `constraints`, `databases`, `tables`, `routines`, `database-types`, `sql-objects`, `queries`, `migrations`), `database` (an id), `cursor`, `limit` | `{ items, next, diagnostics }`: what templates read, as flat records with `id`, `kind` and `name` (other objects by id; a value the file leaves out is null, never empty text); an entity attribute without a description of its own shows the comment of the column a binding maps it to, named by `descriptionColumn`; `databases` and `tables` give the database view's tables, inherited members with their `memberOrigin` and default stereotypes included; `migrations` lists open migration facts by database name, then applied order, whose `from` and `to` carry a value map's `pluralName`; a reference type lists its `formerCodes` (`code`, `replacedBy`, the row's id); `operations` gives each operation its input members (`type`, `keyOf`, or `file` with `mediaTypes`), outcomes (`isDefault`, `completes`, `returns` with `members`, `entity` or `file`, `mediaTypes`, `list`, `paging` and `maxPageSize`), errors (`code`, `input`, and for an error a stereotype contributes its keyed id `<operationId>.<errorId>` and `memberOrigin`; null for an own error), and `fires` and `requires` by id; `custom-elements` gives each custom element its `type`, `package`, `content` as written and `members` (`id`, `name`, `array`, `parent`, `pointer`); `constraints` gives each named constraint its typed `parameters`, `condition` and `message`, and an entity's `checks`, a derived attribute's `value` and a process or operation guard's `condition` are written in the same expression language, each `{ text, type, nullable, tree }` (the text printed from current names, the tree of ids the file holds), keys a model without expressions does not have; each page also carries `calculated`: the calculated values of its records and the sub-objects they list, by site (the object's id, or `<holder id>/<attribute id>` for an attribute its holder does not own) then name, a value that fails left out with its diagnostic in `diagnostics`; a model with errors returns no items and the errors, unless its only errors are pending migration facts (MQ4101), which still return items |
| `get_calculated` | (none) | `ids` (1 to 100 element or sub-element ids), `names` (optional: only these calculated values) | `{ items, missing, declarations, diagnostics }`: one item `{ id, kind, holder, name, calculated }` per resolved object an id names (an attribute held by several entities gives one item per holder, `holder` its id), `calculated` the values of the names its kind declares, evaluated for those objects and whatever they read only; `declarations` what the calculation files declare for the items' kinds (`name`, `kinds`, `schema`, `description`); a value that fails (MQ5010, MQ5011, MQ5013) is left out with its diagnostic; a model with errors returns no items and the errors |
| `save_element` | saveElement | `id`, `element` (whole document), `expectedHash` | the save result (new `hash`, changes, and `facts`: the migration facts the save recorded, `{ id, database, fact, state, missing, summary, path }`; a pending fact's MQ4101 is among the diagnostics and the save still goes through) |
| `patch_element` | (CLI `model apply` with an update) | `id` (an id, not a qualified name), `patch` (an RFC 6902 array of operations, or a string holding one), `expectedHash`; all required | the save result as `save_element` answers it (`hash`, changes, diagnostics, `facts`), plus `ids` (`{ names, minted }`, as `apply_batch`); see "Patches" under Semantics; custom elements as under `apply_batch` |
| `create_element` | createElement | `element` (an id is assigned when absent) | the save result with the new `id` and its `facts`; write a database with `byConvention` (`none`, `packages` or `all`): without it a database with no `packages` takes every entity (the rule from before 0.3.0); a custom element (kind `custom`) names its custom kind in `type` and holds the declared fields under `content` (`get_schema custom:<type>`), and the engine mints the ids of its members |
| `delete_element` | deleteElement, getDeletePlan | `id`, `expectedHash` (not with `dryRun`), `resolution` (`refuse` default, `remove-references` or `delete-dependents`), `dryRun` (`true`: write nothing and return the plan; the resolution then defaults to `delete-dependents`) | the save result with its `facts` (a dropped table or column records a `drop`, cascades included); with `dryRun` the delete plan `{ ids, resolution, outcome, deletes: [{ id, kind, name, path, because }], clears: [{ id, kind, name, pointer, field, target, because }], removes: [{ id, kind, name, pointer, what, subId, subKind, because }], refused: [{ id, kind, name, pointer, why, rule }], settings: [{ pointer, what }], warnings: [{ message, pack, unit }] }`. A custom property value an `x-ref` types is cleared by `remove-references` when optional and by `delete-dependents` either way (its holder stays); a schema declaring the property that would then fail (`required`, `minItems`) refuses with MQ2001. A pack parameter value naming the element refuses a plain delete (MQ2001 in `maquettiste.json`, `id` null in `refused`); the resolutions remove it in the same change (`settings`) as for properties, unless the parameter is required. A stereotype whose members tables inherit: a plain delete lists its carriers, the `redefines` and `excludes` entries that name its members and every keyed use `<tableKey>.<memberId>` of an inherited copy (`refused` names the copy); `remove-references` clears its key from `stereotypes`, `defaultStereotypes` and `excludeStereotypes` lists and the `excludes` entries, removes its `redefines` entries and drops the seed columns that name an inherited copy (in `removes`, sub-kind `column`), and refuses every other keyed use, a binding field included; `delete-dependents` also removes those uses, as it removes a deleted column's |
| `apply_batch` | applyBatch | `operations` (the batch's `operations` array, or the whole `{ "operations": [...] }` body); besides create, update, upsert and delete, the database schema operations `add-schema`, `rename-schema`, `remove-schema` (with `target` to move what lives there, `default` when removing the default) and `set-default-schema`; the process operations `sync-enum` (`id` a lifecycle process, optional `entity` the subject whose enum to sync, else its one enum binding: its bound enum's members become the root-level states in order; a removal of a member still used by a default, allowed values, a seed cell or a scenario value is refused with MQ9019; change the uses first), `sync-codes` (`id` a lifecycle process, `entity` one of its subjects, `attribute` the attribute that holds the state, optional `stateCodes` from state ids to codes: a state keeps a code it has, else takes a matching code from the attribute's type, and the rest take `stateCodes`; it binds the attribute in the form the file has, creates no rows, and a dry run lists what it would write and which states are unmatched), `use-subjects` (`elementIds`: each process in the one-subject form lists its subject in `subjects` instead; nothing else converts a file), `set-lifecycle` (`id` an entity, `target` a process, both sides in one change; no `target` clears it), `set-initial` (`id` a process or compound state, `target` a direct child) and `refresh-scenario` (`id` a scenario: its steps' `expect` and its `outcome` are rewritten from the engine's replay; refused when the batch also writes the scenario or its process, or when the replay stops early); the tag operations `retag` (`tags`, optional `name` to rename the one tag, optional `package` for a domain's vocabulary: the vocabulary and every use it governs, see `tag_usage`) and `tags-to-property` (`prefix`, `name` a custom property, optional `package` and `elementIds`: the tags that start with `prefix` move into the property of each element and sub-element holding them, converted by the schema that declares it, one suffix for a string, number, integer, boolean or `enum`, all of them for a string array, the one id the suffix names for an `x-ref`; the tags leave their holders and, without `elementIds`, the vocabulary, unless something without properties such as a relation end still uses them; no matching tag is MQ1002, a holder whose property no schema declares, whose suffix does not fit or that holds another value is MQ5001); `names-to-ids` (optional `elementIds`: every custom property value an `x-ref` types that is still a name becomes the one id it finds, in those elements or in every document with `x-ref` sites; a value that finds nothing or several is MQ2010 at the value, nothing to convert writes nothing); the materialize operations `materialize-tables` (`database`, `entities`, optional `schema`), `materialize-entities` (`database`, `tables`, `package`), `materialize-attributes` (`database`, `entities`, for one entity optional `columns`: an attribute per unnamed column of its binding's source) and `materialize-columns` (`database`, `entities`, for one entity optional `attributes`: a table column per unmapped attribute), refused with MQ4055 for what is already bound or has nothing to add (preview them with `preview_materialize`), each recording the provenance of the binding it writes; the binding operations `map-by-name` (`database`, `entities`: every attribute a binding leaves unmapped goes to the free column of the same loose name, case and every character but letters and digits ignored, and the binding records provenance) and `resync-binding` (`database`, `entities`, for one entity optional `columns`, optional `keepNames`: each binding comes into step with its table file in one batch; an attribute that followed its column's old name takes the new one unless `keepNames`, a retyped column retypes its attribute unless its type still fits, unmapped attributes map by name, an attribute is added per added column, the fields of removed columns go and the attributes stay, and provenance is recorded again), both refused with MQ4089 (an entity with no binding to the database, a source they cannot read, misused `columns`, nothing to map or re-sync; see `get_binding_drift`); the fold `fold-into-stereotype` (`id` the stereotype, exactly one of `tables` (designed or imported tables) and `schema` (a schema id: every table file of its database in that schema), optional `redefine` and `excludeMissing`, both false by default): each table carries the stereotype, its own columns that match a member column by name and its own keys, indexes and checks that match a member by shape leave its file, and every reference to them (keys, indexes, seed columns, binding fields, query columns) names the inherited copy `<tableId>.<memberId>`; a facet that differs becomes a `redefines` entry with `redefine` (type facets never do), a member the table lacks joins `excludes` with `excludeMissing`, and a table that matches nothing is skipped; anything else (a type difference, annotations the member lacks, a synthesized table, a table that excludes the stereotype, another operation of the batch that writes the stereotype or a target) is one MQ4077 per refused table and nothing is written; the write records one `rekey` fact per database (its column moves, then the index, unique, foreign-key and check ids it moved) and the next migration has no statement for it; the reference-type refactors `rename-code` (`id` a reference type, `code`, `to`: the row takes the new code, every default, `allowedValues` entry and seed cell that names the old one follows, and the type lists the old code in `formerCodes` with the row), `retire-code` (`id`, `code`, `to` a live code: the row and its translations go, its uses, the `x-ref` values and former codes that named it move to `to`'s row, and the old code becomes a former code of that row), `merge-reference-types` (`id` the type that goes, `target`, optional `codes` from its codes to the target's: every reference to the type moves to the target, a code the target has maps to its row, the other rows move with their ids, values and translations, a seed named as a target seed joins it, and the type and its seeds left without rows are deleted) and `rename-reference-type` (`id`, `name`): one per type per batch, refused with MQ7020 and the reason (an unknown, scripted, taken or former code, mismatched code types, a binding, a field the target lacks), each recording one `value-map` fact per database that stores the type's codes (a type rename: per database without a binding of the type), and the item of the type it names (the merged type's delete for a merge) carries `usage` (`useCount`, the first 200 `uses` by element id then pointer, `rowsMoved`, `rowsRemoved`, `translationsRemoved`). Every id field takes an id, a qualified name such as `@table:billing.notes` or a placeholder declared with `as` (see "Batches by name"), and the engine mints every missing id; `dryRun` (`true`: write nothing) | the batch result, all or nothing, with `ids` (`{ names, minted }`: each name and placeholder to its id, and each minted `{ operation, pointer, kind, id, name, placeholder }`) and the migration facts the batch recorded in `facts` (its items carry none); with `dryRun` `{ outcome, items, files: [{ path, action, id, kind, name, beforeHash, diff }], diagnosticsAdded, diagnosticsRemoved, ids, facts, impact }`: a unified diff per file, the diagnostics the change adds and removes over the elements it touches, the facts it would record (a fold's `rekey` among them; `null` when the change reaches every database), and when it would save, `impact: { referrers: [{ target, change, fromKind, fromName, reference }], referrersTotal, databases: [{ id, name }], packs: [{ pack, state, units: [{ key, change, keys, outputs, filtered }], unitsTotal }] }`, how far the change reaches (see "Impact" below); read it before applying; a custom element (`kind: custom`, `type`, `content`) gets the ids of members sent without one, minted by the engine and listed in `ids.minted`, and values name them as `@custom:<type>.<name>` and `@member:<type>.<name>.<member>`, a package path before the name when needed |
| `simulate_process` | simulateProcess | `process` (id, name or model path), `steps` (scenario steps without `expect`; ids optional), `start` (`{ context, at }`), `objects` and `subject` (the scenario's cast, as in a scenario), `scenario` (its start and steps run first), `document` (an unsaved draft; invalid is `invalid` with diagnostics), `from` | `{ processHash, trace, configuration, context, enabled, pending, timers, gates, final, clock, diagnostics }`; nothing is kept between calls: send the whole input list each time |
| `record_scenario` | recordScenario | `process`, `name`, `steps`, `start`, `objects`, `subject`, `outcome` (the replay's when absent), `dryRun` | `{ id, element, hash, applied, diagnostics }`: the scenario with every step's `expect` and the outcome filled from the engine's replay, saved unless `dryRun` |
| `verify_scenarios` | verifyScenarios | `process`, `scenarios` (ids or names; all when absent) | `{ process, results: [{ scenario, name, passed, steps, failure }], passed }`; `failure` is `{ step, rule, message, expected, actual }` |
| `export_process` | exportProcess | `process`, `format` (`xstate`) | the XState v5 machine config text (canonical key order; the model data without an XState home under `meta.maquettiste`), then, when there are any, a second block `{ diagnostics }` (MQ9404, MQ9406) |
| `import_process` | importProcess | `config` (object or string), `package`, `name`, `use`, `subject` for a new process, or `into` (id or name) and `expectedHash`; `apply` (false: a dry run) | `{ document, diagnostics, created, removed, applied, id, hash }`; the diagnostics are MQ9401 to MQ9405 (and, when applied, the save's findings), and an error among them (MQ9403, MQ9405) or a changed `into` refuses the apply |
| `sync_enum_from_process` | syncEnumFromProcess | `process` (a lifecycle), `entity` (the subject whose enum to sync; absent means its one enum binding), `expectedHash`, `apply` (false: a dry run) | `{ process, enum, added, removed, reordered, refused, applied, diagnostics }`; a member still in use is refused, never removed |
| `get_references` | getReferences | `id` | where the element is used (a row also lists the former codes whose `replacedBy` names it); a custom property value an `x-ref` types is a row too, with `schema` (`extension:<name>` or `stereotype:<key>`) and `optional` (true when `remove-references` may clear it) |
| `localization_status` | getLocalizationStatus | | `defaultLocale`, `declared`, and per translated locale its `chain` and per shard `expected`, `translated`, `missing`, `stale`; the counts include the texts of `x-translatable` custom properties and the `x-translatable` content texts of custom elements and members |
| `get_translations` | getTranslations | `locale`, `owner` or `shard` (optional), `missing` (the entries that need work, 200 per page), `cursor` | `{ entries, cursor }`: per field (`displayName`, `pluralName`, `label`, `description`, `properties/<name>` for an `x-translatable` custom property, or `content/<name>` for an `x-translatable` content key of a custom element or a member) the `source`, `translation`, `effective` text, `state` (`translated`, `missing`, `stale`, `fallback`), `shard` and `shardHash` |
| `set_translations` | putTranslations | `locale`, `entries` (`[{ id, field, value, confirm? }]`, `field` a standard field, `properties/<name>` or `content/<name>`, `value` null removes), `expected` (`{ "<shard path>": "<shardHash>" }`) | `{ outcome, hashes, diagnostics }`; a changed shard is `conflict`, an unknown id or field (a property or content key no schema marks `x-translatable` among them) `invalid` |
| `create_seed` | (CLI `seed new`) | `type`: a reference type id | the seed id: a new seed named after the type with the columns `code`, `label` and `description` and no rows, or the seed the type already has (nothing changes). `create_element` of a seed for a reference type without `columns` gets the same three |
| `export_seed_csv` | exportSeedCsv | `id` of a seed, `bom` (byte order mark and CRLF), `locales` (adds `@label:<locale>` and `@description:<locale>`) | the CSV text: `@id`, then `@code`, `@label`, `@description` for a reference type, then attribute names and end roles, or a table seed's column names; a foreign-key entry is headed by the foreign key's name (its id when it has none) and holds the referenced row's natural key, a composite one as JSON array text; a seed with a rows script exports the rows its script returns with their derived `@id`, and a script that fails is `invalid` with its MQ7127 |
| `import_seed_csv` | importSeedCsv | `id`, `csv`, `mode` (`merge` default, or `replace`), `apply` (false: a dry run), `expectedHash` | the preview: `added`, `changed` (before and after), `removed`, `blocked` (rows other seeds reference, kept), `ignoredHeaders`, `applied`, `hash`, `diagnostics`; a seed with a rows script takes no import (`bad-request`); a line whose `@code` is a former code of the type is skipped and listed with MQ7019 |
| `preview_seed_rows` | (none) | `seed` (an id), `database` (an id or name, optional), `cursor`, `limit` (1 to 1000, default 100) | one page of one seed's rows `{ seed, script, database, rows: [{ id, key, values, refs }], total, next, diagnostics }`. Without `database`: the rows as the seed writes them (inline or in its CSV rows file) or as its rows script returns them, `values` by the seed's column names as written, `key` the row key in seed terms (empty when the target has none), a scripted row's `id` derived from the seed and its key. With `database`: the rows that database receives from the seed in database terms, `values` by column name with stable ids and the values foreign-key entries fill, `key` the row key there, and `refs` per foreign-key entry `{ foreignKey, table, key, found, unknown }` (`unknown`: the columns whose referenced value the database fills). Only the seed's script and the scripts of the seeds its entries name run; `diagnostics` holds their findings (MQ7116, MQ7127, MQ7128, MQ5005), or the errors that keep the model from resolving. An unknown seed is `not-found`; an unknown database or cursor is `bad-request` |
| `reference_type_usage` | getReferenceTypeUsage | `id` of a reference type | `usages`: attribute, owner, domain, collection, required and the effective storage per database; `columns`: the designed, imported, extra and inherited columns whose `typeRef` names the type (projected columns are covered by their attributes), each with `table`, `column` (a keyed id when inherited), `database`, `domain` (the table's package), `required`, `storage` (the effective choice in that database, null for template-defined) and `boundTable` (the table of the type's binding there, or null), ordinal by table id then column id |
| `tag_usage` | getTagUsage | `package` (a domain; the global vocabulary without it), `tag`, `cursor`, `limit` (1 to 1000), all optional | a summary, `{ package, vocabulary, strict, tags, next }`, each tag `{ tag, declared, uses, examples }` in key order: the tags the vocabulary declares and the undeclared ones used in its scope, with their uses there and up to five names of the elements that hold them; the whole list unless `limit` or `cursor` pages it. With `tag`: `{ package, tag, declared, uses, elements, next }`, the ids of the elements that hold it in pages, in id order (a tag the scope neither declares nor uses is `not-found`). Remove or rename tags everywhere with `apply_batch` `retag` |
| `validate` | validate | `elementIds` (optional scope), `includeReferrers`, `includeScriptRules` (default `true`: the script rules of `extensions/rules/` run, their findings are `x/<id>`; declared rules (`extensions/rules/*.json`) run on the resolved model, their findings are `x/<id>` too, MQ5014 to MQ5017 on the declaration files; whole-model rules, `scope: "model"`, run only without `elementIds`; on a model without errors the calculated values are evaluated and resolved rules, `scope: "resolved"`, run on each object of their kinds, MQ5010 to MQ5013 for values that fail, and with `elementIds` only what falls on those elements is reported), `against` (a model snapshot id, or a folder holding another copy of the project, relative to the server's current directory; the editor's assistant takes snapshot ids only: compares the whole model with that baseline, without `elementIds`) | the report: diagnostics with rule ids, file, JSON pointer, line and column; counts; `suppressed`, the findings an element's `suppressions` accepted (`{ finding, reason, elementId, jsonPointer }`, the pointer `/suppressions/<i>`), not counted; a whole-model report whose model resolves warns MQ5020 for an entry that matched nothing. With `against`, a change since the baseline that no migration fact explains is MQ4102, a stale fact MQ4103, baseline objects whose owners have errors, or whose files did not load, are skipped with MQ4109; a value that names nothing is `bad-request` |
| `list_validation_rules` | listValidationRules | `family` (such as `MQ41xx`), `ids` (exact rule ids), `query` (text in the id or description, ignoring case), `cursor`, `limit` (1 to 1000, default 100), all optional, AND | none given: a summary, `{ total, families: [{ family, familyLabel, rules, first, last }] }` in id order; else one page `{ items, next }` of full entries in id order: `id`, `defaultSeverity`, `description`, `family` (the hundreds group, such as `MQ72xx`) and `familyLabel`, `canBeOff` (false for MQ1xxx), `quickFix` (the batch operation that fixes a finding, when the rule has one); override a severity with `validation.rules` through `save_settings` |
| `get_database_view` | getDatabaseView | `id` of a database | the resolved physical view: the tables of the entities mapped to it (by its `byConvention` setting and its `packages`, or one by one by mapping elements, less the ignored ones), with columns, keys, indexes and foreign keys, views, sequences, `routines` (parameters and result with native types, language, body, `dependsOn`), `types` (database types with `isCreated` and `nativeName`), `objects` (SQL objects with `objectKind`, `phase`, `dependsOn` and body), `queries` (parameters, sources, the select list with each field's inferred type, the trees as the file writes them, collections with their correlation keys, `sql` for the database's dialect and `uses`) and `schemas` (`name`, `isDefault`, `isDeclared`). The database, each schema, table, column, view, sequence, routine, database type, SQL object and query carries the annotations of its own file or entry: `displayName`, `pluralName`, `description`, `stereotypes` (keys), `tags`, `category` (id), `properties` (merged with the stereotypes' defaults) and `generation`; a synthesized table without an overlay, or a synthesized column without an overlay entry, has none, whatever its entity or attribute carries, except the `stereotypes` a table's schema and package defaults give it, with their default `properties`. Columns, keys, foreign keys and indexes a stereotype contributes follow the table's own, keyed `<tableKey>.<memberId>`, and carry `memberOrigin` (`source`: `stereotype`, `schema-default` or `package-default`; `stereotype`; `memberId`; `redefined`, the facets the table's `redefines` entry states), null on own members: write that keyed id to name one, and change it on the stereotype or with the table's `redefines` and `excludes`. The database also gives `quoting`, `maxIdentifierLength`, `byConvention` and `packages`; a column its `default`, `sequenceId`, `dbTypeId` (the database type its native type names), `collation`, `comment`, `computedStored`, `typeRef` (the enum, reference type or scalar type its own entry names, as an attribute's `type` record; null on a projected column, whose attribute types it) and `sensitive` (`pii` or `secret`). A new database with nothing mapped has no tables |
| `preview_binding_sql` | getBindingSql | `entity` (id), `binding` (the binding's id, `entity.bindings[i].id`), `dialect` (the binding's database's when absent), `placeholder` (`@` default, `:` or `$`) | `{ preview: { entityId, bindingId, database, dialect, select, selectByKey, insert, update, delete }, diagnostics }`: each statement `{ sql, parameters }`, or null when the binding does not have it (a read-only binding has no insert, update or delete; no key fields, no selectByKey). `select` reads every row with the constants as a filter (and without soft-deleted rows; a query source is a derived table), `insert` writes the fields and constants without the columns the database fills and returns generated keys (`RETURNING`, `OUTPUT INSERTED`, or `SELECT LAST_INSERT_ID()` on MySQL), `update` sets the updatable fields by key with the constants in the where clause, `delete` deletes by key or runs the soft-delete update. Parameters are named after the fields; `$` numbers them after a query source's own. `preview` is null on a model with errors; an unknown binding is `not-found` (`not-a-binding`) |
| `preview_expression` | previewExpression | `element` (the element, or the check, guard or derived attribute, that holds the expression), `at` (the stored expression's pointer, `/checks/0/condition`; optional when the element holds one), or `text` with `site` (`check`, `value`, `guard`, or the pointer of one of the element's sites, whose scope and mode the text takes), `database` (an id or name: the binding an entity's expression lowers through), `values` (the row by member name; a process guard's payload under `event`, an operation's subject under `subject`) | `{ preview: { text, type, nullable, javascript, sql, sqlNote, answer }, diagnostics }`: the text printed from current names, the type (`bool`, `int32`, `decimal`, `enum <id>`…), the JavaScript function over a row that calls the expression runtime as `mq`, the PostgreSQL text through the one table the site reads (a table's own columns; an entity's check or value through its binding's field map), else `sql` null and `sqlNote` saying why (a guard or a constraint reads no table; another dialect is MQ4056), and with `values` the `answer` under the site's mode (a check passes on true or unknown, a guard holds on true only, a value answers itself, a decimal as exact text; `now()` reads the sandbox's fixed clock, 2000-01-01T00:00:00Z); `preview` is null when the text does not parse or the expression does not type (MQ3101 to MQ3113); it reads that element and what its expression names, never validates or resolves the model, and writes nothing; an unknown element is `not-found`, a request with both `at` and `text`, an unknown site or database is `bad-request` |
| `get_materialize_status` | getMaterializeStatus | `database` (id) | `{ database, databaseName, entities: [{ id, name, package, projected, table }], sources: [{ id, kind, name, schema, origin, boundBy: [{ entity, entityName, binding, constants }] }] }`: the entities with no binding to the database (`projected`: the database still projects a table for it, by convention or a mapping element), and its designed and imported tables and its views with the entities bound to each (none means no entity reads it; several are told apart by their constants) |
| `preview_materialize` | previewMaterialize | `database`, `op` (`materialize-tables` with `entities` and an optional `schema` id; `materialize-entities` with `tables` and `package`; `materialize-attributes` with `entities` and, for one entity, optional `columns`; `materialize-columns` with `entities` and, for one entity, optional `attributes`; `map-by-name` with `entities`; `resync-binding` with `entities`, for one entity optional `columns`, and `keepNames`) | `{ operation, database, valid, creates, updates, deletes, notes, diagnostics }`, each change `{ id, kind, name, path, because, element }` (`element` the document that would be written); nothing is written. A materialize refusal (an entity or table already bound, an entity in an inheritance hierarchy, a name taken) is MQ4055 in `diagnostics`, and the binding rules MQ4044 to MQ4054 come from resolving the model after the change; a binding operation's refusal is MQ4089, and its binding rules (with MQ4087 and MQ4088) come from resolving the named database alone, never the whole model. Apply it with `apply_batch` |
| `get_binding_drift` | (none) | `database` (id or name), `entities` (ids), `all` (default false: only bindings with something to report), `cursor`, `limit` (1 to 1000, default 100), all optional | `{ bindings, next }`, each binding `{ entity, entityName, binding, database, databaseName, source, sourceName, hasProvenance, stale, added: [{ column, name, status }], renamed: [{ column, name, attribute, attributeName }], retyped: [...], removed: [column ids] }` by entity name, entity id, database name and database id: what changed in its table file since its provenance was recorded, from files only. `added` lists the columns not recorded that nothing in the binding names, with what fills them (`unaccounted`, `identity`, `default`, `computed`; without provenance only `unaccounted`, MQ4047's); `renamed` and `retyped` are mapped columns whose name or type changed (MQ4087; a change of case or separators is not a rename); `removed` lists recorded ids the table no longer has; `stale` is provenance that does not describe the source (MQ4088). Pass `next` as `cursor` until it is null. Catch up with `apply_batch` `resync-binding`. An unknown database, entity or cursor is `bad-request` |
| `preview_query_sql` | getQuerySql | `id` of a query, `dialect` (`postgresql`, `sqlserver`, `mysql`, `sqlite`, `oracle`; the database's when absent), `placeholder` (`@` default, `:` or `$`), `lists` (`expand` default, or `any`) | `{ preview: { id, name, database, dialect, sql, parameters, collections: [{ name, sql, parameters, keys }] }, diagnostics }`: the query's statement and one per collection, each with the parameters it names in first-appearance order; a list parameter is `IN @name` (for a data access library that expands lists) or, with `lists: any`, `= ANY(@name)` on `postgresql`; a collection's statement takes the parent rows' key values as `mq_keys0` and returns each row's as `mq_key0`, matching the parent's `keys[i].parentField`; `preview` is null on a model with errors; an `sql` expression without a text for the dialect is MQ4029; not a query is `not-found` (`not-a-query`) |
| `list_packs` | (part of getProject) | | pack manifests (with `uses`), `usedBy` per pack, and their diagnostics (MQ6042 to MQ6046 for uses problems) |
| `get_settings` | getSettings | | `maquettiste.json`: typed settings, canonical `json`, `hash` |
| `save_settings` | saveSettings | `settings` (whole document), `expectedHash` | the save result |
| `format_model` | formatModel | `paths` (repo-relative model files; default every model file) | `{ formatted, skipped, refused, total }`: the files rewritten in canonical form (MQ1003 then goes away; `maquettiste.json` also loses the retired `commit` flag, MQ1010); content does not change; a path that is not a model file fails with `bad-request` and nothing is written; custom kind files (`extensions/kinds/<type>.json`) are model files, other extension files are not |
| `plan` | startPlan, run to completion | `packs`, `units` (`<pack>/<unit>`, or a unit id only one run pack has), `elements` (element ids, never names; a synthesized table's id is `<entityId>@<databaseId>`), `force`, `handEdits` (`fail`, `overwrite`, `skip`), `jobs`, `against` (a model snapshot id, or a folder holding another copy of the project, relative to the server's current directory: the schema diff runs from that baseline instead of the committed physical snapshots, which the apply leaves alone) | `{ outcome, plan }`, the plan without its per-unit list (as a plan job's `planResult`), with `counts` of every file kind and `unitsRendered` / `unitsSkipped` by reason; files left alone because they are identical (`unchanged`) or were not rendered (`not-rendered`) are only counted. The plan stores `against` (a folder as its full path); a value that names nothing is `bad-request`. `units` and `elements` limit the plan (values of one OR, the two AND; see "Scoped generation" under Semantics): it holds the selected units only, the plan stores both, and its apply writes only their files |
| `get_plan` | getPlan | `planId`, `units` | a stored plan with every file the packs produce, each with what apply does to it (`added`, `modified`, `deleted`; `unchanged`, `not-rendered` and `kept` are left alone; the hand-edit kinds); with `units` true, every unit with its pack, unit, template, element, `reason`, `causes` and `skipped` |
| `get_plan_diff` | getPlanDiff | `planId`, `path` | the unified diff text of one planned file |
| `apply_plan` | startApply, run to completion | `planId` | the apply result (as an apply job's `applyResult`); a plan made `against` a baseline opens it again (`stale` when it changed, `bad-request` when it no longer opens) |
| `generate` | (CLI `generate`) | `packs`, `units`, `elements`, `against`, `force`, `handEdits`, `jobs` as for `plan`; `noWait` (`true`: `busy` at once when another run holds the run lock) | renders and writes in one run, storing no plan: `{ outcome, unitsRendered, unitsSkipped, filesWritten, filesDeleted, packs: [{ pack, counts, files: [{ path, kind }], more }], diagnostics, moreDiagnostics }`. Each pack that changed something has its files counted by kind (only the kinds that occur), its first 50 paths in path order and `more` for the rest; files left alone as `unchanged` or `not-rendered` are not listed. At most 100 diagnostics, errors first, and `moreDiagnostics` counts the others. No run id or timings. Any other outcome is a tool error with the same fields: `invalid` (units without errors may have written their files), `conflicts`, `busy`, `failed`; a baseline that names nothing is `bad-request` |
| `get_pack` | getPack | `pack` | pack.json as a document with its `hash` (for `save_pack`), the parameters (default, project value, schema), every file with its hash, role and users (`include:<pack>:<path>` for a partial another pack includes), `usedBy` (the packs and `maquettiste.json` whose `uses` name it), and the diagnostics |
| `save_pack` | savePack | `pack`, `document` (the whole pack.json), `expectedHash` | the write result; written in canonical form; a schema failure (MQ6001, MQ6021) writes nothing |
| `save_pack_settings` | savePackSettings | `pack`, `settings` (`enabled`, `output`, `parameters`), `expectedHash` (the settings hash) | the settings save result; only `packs.<pack>` of `maquettiste.json` changes |
| `new_pack` | createPack | `name`, `from` (`empty`, a built-in starter, or a pack of this project), `library` (default `false`: a pack without units, `partials/header.scriban`, a `helpers.js` and `tests/helpers.test.js`; `from` must be `empty`) | the new pack's folder under `.maquettiste/templates/`: `empty` adds an example case under `tests/example/`, and a copy of a pack leaves its `tests/` out |
| `list_pack_files` | listPackFiles | `pack` | each file's path, size, hash, role and users (`unit:<id>`, `companion:<id>`, `include:<path>`); a file under the pack's `tests/` folder has the role `test` |
| `read_pack_file` | getPackFile | `pack`, `path` | the text and hash of one template, partial, script or other file |
| `write_pack_file` | putPackFile | `pack`, `path`, `text`, `expectedHash` (`new` creates) | the write result; a template that does not parse is saved and its MQ6003 diagnostics returned; a file under `tests/` (a test case or a helper test) is written without being parsed; pack.json is refused (use `save_pack`) |
| `move_pack_file` | movePackFile | `pack`, `from`, `to`, `expectedHash`, `updateUnits`, `expectedPackHash` | the move result; `updateUnits` rewrites the units and scripts that name the file; refused while a template includes it, here or in a pack that uses this one |
| `delete_pack_file` | deletePackFile | `pack`, `path`, `expectedHash` | the delete result; refused while a unit names the file or a template includes it, here or in a pack that uses this one |
| `delete_pack` | deletePack | `pack`, `expectedHash` (the pack.json hash from `get_pack`) | the removal result: `files` deleted from `.maquettiste/templates/<pack>/`, the `packs.<pack>` settings entry removed (`settingsHash`), and `untracked`, the generated files the pack's manifests recorded, which stay on disk and are no longer tracked; `referenced` while a pack or `maquettiste.json` uses the pack |
| `rename_pack` | renamePack | `pack`, `name` (the new name: lowercase letters and digits separated by single hyphens), `expectedHash` (the pack.json hash from `get_pack`), `updateHints` (default `true`) | the rename result: `from`, `to`, the new pack.json `hash`, `files` moved from `.maquettiste/templates/<pack>/` to `.maquettiste/templates/<name>/`, the `packs.<pack>` settings entry moved to `packs.<name>` with its values (`settingsHash`), `tracked`, the generated files whose manifest entries moved so they stay tracked, `hints`, the elements whose generation hints name the old pack, and `hintsUpdated`, the ones moved to the new name in one model batch after the rename (`updateHints`, default `true`; a refused batch leaves the rename in place and adds a warning); `usesUpdated`, the `pack.json` files and `maquettiste.json` whose `uses` entries now name the new pack, and `textReferences`, the template and script files whose text names the old pack (not edited); a name that is taken or not a pack name is `invalid` and changes nothing |
| `list_extension_files` | listExtensionFiles | | the files of `.maquettiste/extensions/`: custom property schemas (`<name>.json`, kind `schema`), script rules (`rules/<name>.js`, kind `rule`), rule declaration files (`rules/<name>.json`, kind `declared-rules`), seeds' rows scripts (`seeds/<name>.js`, kind `seed-rows`), custom kinds (`kinds/<type>.json`, kind `custom-kind`) and calculation files (`calculated/<name>.js`, kind `calculated`), each with size, hash and what is wrong with it on its own (MQ5004 for an invalid schema, MQ5002 or MQ5003 for a rule script or calculation file that does not load, MQ7127 for a rows script that does not load or does not call `maquettiste.rows` exactly once, with line and column, MQ5005 for a script that declares `let` or `var` at its top level), and for a rule script its `rules`: `{ id, severity, wholeModel, resolved }`, `wholeModel` true for a rule registered with `scope: "model"`, `resolved` for one registered with `scope: "resolved"`; a calculation file lists its `calculations` (`name`, `kinds`, `schema`, `description`), never evaluated; a custom kind file lists MQ1001 or MQ1002 when it does not parse or pass `custom-kind.json`, and its own findings (MQ1017, MQ1018, MQ2009, MQ7212); a rule declaration file lists MQ5014 when it does not fit `declared-rules.json`, MQ5015 and MQ5016 for its rules, and its `rules`, each with `resolved` true (it runs on the resolved model); no rows script runs |
| `read_extension_file` | getExtensionFile | `path` (under `extensions/`) | the text, hash and kind of one schema, rule script, rule declaration file, rows script, custom kind or calculation file |
| `write_extension_file` | putExtensionFile | `path`, `text`, `expectedHash` (`new` creates) | the write result; a schema that is not JSON or fails `extension.json` is `invalid` (MQ5004) and nothing is written, a valid one is written in canonical form (`text` is what was written); a rule script is written as sent and loaded alone in the sandbox, its syntax error or registration without an id returned in `diagnostics` (MQ5002 with line and column), as is the warning MQ5005 for a top-level `let` or `var`; a rule declaration file (`rules/<name>.json`) must be JSON, is written in canonical form and checked alone: MQ5014, MQ5015 against the other declarations and the rule scripts, and MQ5016, with pointers, none of which refuses the write; a rows script is written as sent and loaded alone, never run, and one that does not load or does not register its rows exactly once comes back as MQ7127; a calculation file is written as sent and loaded alone, never evaluated, and MQ5002, MQ5003 and MQ5005 come back at once; a custom kind that is not JSON or fails `custom-kind.json` is `invalid` (MQ1001, MQ1002), a valid one is written in canonical form and answers with its own findings and the MQ3033 findings of the elements of its type (at most 50, then a line with the count), which never refuse it |
| `move_extension_file` | moveExtensionFile | `from`, `to`, `expectedHash` | the move result; the target must not exist and keeps the kind (`<name>.json`, `rules/<name>.js`, `rules/<name>.json`, `seeds/<name>.js`, `kinds/<type>.json` or `calculated/<name>.js`); a custom kind that custom elements use is refused as a delete is; a seed whose `rowsFrom.script` named the old path reports it missing (MQ7116) until it names the new one |
| `delete_extension_file` | deleteExtensionFile | `path`, `expectedHash` | the delete result; a custom kind that custom elements use is `invalid`, with one MQ1019 per element that would lose its kind (at most 50, then a line with the count) |
| `get_template_context` | getTemplateContext | `pack`, `unit` | what the unit's templates can use: the globals, the members of the model and of the scope's records, the helpers; an `each custom:<type>` unit's alias is `custom`, with the custom kind's declared content keys under `custom.content`; the element's members list `calculated.<name>` for every calculated value the scope's kind declares |
| `preview_unit` | previewTemplate | `pack`, `unit`, `elementId`, `overlay` (path to unsaved text), `unitOverride` | each rendered file's output path and text (a copy unit's binary file has `binary: true` and empty text), the diagnostics and the keys the render read; nothing is written; an element outside the unit's scope (none for an `each` unit, another kind, one its selector does not return, any for a `model` unit) renders nothing and returns MQ6026, which names the kind the template expects; only that unit is rendered, for that element, and no unit is planned; a unit whose one render covers the whole model (`for: model`), a database or a locale renders all of it, so ask for it when its output is what you need |
| `unit_paths` | unitPaths | `pack`, `unit`, `elementIds` (at most 20), `limit` | without `elementIds`: how many elements the unit plans (`count`) and the elements themselves (`elements`: id, name, kind, up to `limit`, default 200), rendering nothing; with `elementIds`: those elements rendered for their output paths, with their root and whether the writer allows them, each path naming its element (`elementName`: an element's name, a table as `customers (billing)`, a locale's tag) and `elementKind`; more than 20 is `bad-request`. `wide` is true for a unit whose one render covers the whole model, a database or a locale: its paths come with `preview_unit`. MQ6020 also flags an output pattern with no code span over several elements |
| `get_pack_exports` | getPackExports | `pack`, `elementIds` (at most 20) | the names the pack exports per element (its units' `exports`, merged per element; an `each` export as a map by item id), evaluated now from the model; without `elementIds`, the pack's model-scope record; a failed record has null values and its diagnostics |
| `test_pack` | testPack | `pack` (absent: every pack with a `tests/` folder), `cases` (absent: every case and then the helper tests), `update` (default false), `maxDiffLines` (1 to 200, default 40) | `{ packs, passed, failed, updated }`, each pack `{ pack, cases, tests, diagnostics }`: a case `{ name, description, outcome, files, diagnostics }` (`passed`, `failed`, `updated`, `error`; `files` the paths `added`, `missing` or `differs`, with a unified diff) and a helper test `{ file, name, outcome, message, line, column }`. Each case runs as a fresh project in a temporary folder and its output must equal `tests/<case>/expected/` byte for byte (MQ6051); a failing helper test is MQ6052, a malformed case or test file MQ6053. `update` rewrites each run case's `expected/` from its output, and nothing else in the project; an unknown pack is `not-found` |
| `get_pack_outputs` | getPackOutputs | `pack` | the files the pack's manifest records: path, unit, element, root, mode (`overwrite`, `regions`, `once`, `block`) and state on disk (intact, edited, missing; a block by its lines) |
| `explain_unit` | getPlanUnit, explainUnit | `planId` and `key` (a unit of a stored plan), or `pack`, `unit`, `elementId` (any unit and element) | the reason (`new`, `forced`, `inputs`, `outputs`, `unchanged`, or why it does not run: `pack-disabled`, `not-selected`, `scope`, `filter`, `skip-hint`, `selector`, ...; a rendered block unit whose target file is missing without `createFile` answers `target-missing`), the causes and a one-sentence summary |
| `list_snapshots` | listSnapshots | | the model's snapshots (zip archives under `.maquettiste/model-snapshots/`), newest first: `id`, `name`, `description`, `author`, `createdUtc`, `origin` (`user`, or `before-restore` for a restore's safety snapshot), `published`, `includesPacks`, `modelHash`, `files`, `elements`, `kinds`, `size` |
| `compare_snapshots` | compareSnapshots, compareSnapshotElement | `from` (`working`, a snapshot id, or in `maquettiste mcp` a folder that holds another copy of the project; the editor's assistant answers `bad-request` for a folder), `to` (default `working`), `fieldChanges` (default `false`), `format` (`json`, the default, or `markdown`), `offset`, `limit` (1 to 5000, default 500, or 50 with `fieldChanges` or `markdown`); or `id` (an element id) | `{ from, to, added, removed, changed, kinds, packages: [{ id, name, added, removed, changed }], elements: [{ id, kind, name, change, path, previousName, previousPath, package, database, label, fields, fieldsTruncated }], next, files: [{ path, change }], filesTruncated, packsCompared }`, always grouped by package (packages by qualified name, "(no package)", then the migration facts of each database, labelled like `rename column billing.invoices.number`); with `fieldChanges` each changed element's field changes (at most 50), each naming its sub-element (`subId`, `subKind`, `subName`, `subPointer`); with `format` `markdown`, `{ markdown, next }`, the page as `maquettiste model diff --format markdown` prints it; with `id`, that element's two documents (`before`, `after`) and the fields that differ as JSON pointers (`fields`, at most 500) with their sub-element, or with `format` `markdown` `{ markdown, next }` with `next` always null |
| `create_snapshot` | createSnapshot | `name`, `description`, `includePacks` (default `false`) | the snapshot (`id` is the kebab-case name and the UTC time, `<slug>-<yyyymmdd-hhmmss>`) |
| `restore_snapshot` | restoreSnapshot | `id`, `includePacks` (default `false`; only when the snapshot holds them) | `{ outcome, snapshot, safety, written, deleted, packsRestored, undo, elementsChanged, elementsDeleted, diagnostics }`: the working model becomes the snapshot's after a safety snapshot of it (`safety`; restore that one to undo); `run-locked` while a generation run holds the run lock. Destructive: read `compare_snapshots` with `from` `working` and `to` the id first |
| `list_migrations` | (none) | `database` (name or id), `cursor`, `limit` (1 to 1000, default 100) | the open migration facts in applied order (by database name, then the order their `after` links give, ties by id), one page: `{ facts: [{ id, database, databaseName, order, fact, state, stale, missing, summary, object, after, path, hash }], total, next }`; pass `next` as `cursor` until it is null. `state` is `pending`, `complete`, `held` or `released`; `missing` names the intent a pending fact lacks; `stale` marks a fact `fold_migrations` removes (MQ4103). A value map's `summary` names its type, codes and holders (`maps PaymentTerm codes net30 -> net-30 in 1 column`), or its two names for a type rename (`renames reference type PaymentTerm to PaymentCondition`). An unknown database or cursor is `bad-request` |
| `fold_migrations` | (none) | `database` (name or id), `against` (a model snapshot id, or a folder holding another copy of the project, relative to the server's current directory), `dryRun` | `{ outcome, joined: [{ kept, removed, fact }], removedStale, refused: [{ facts, reason }], files, diagnostics }`: the facts the baseline lacks (`against`'s, else those each database's committed physical snapshot lists as rendered, else pending facts only) lose their stale ones, and consecutive facts on one object join into the last one (renames keep their intermediate names in `via`; a widening joins its neighbour; two pending `change-type` facts join; a rename then a drop join into the drop). Two conversions, or a join that would order facts in a cycle, are refused and stay. `files` (with `dryRun`) lists each file with its text before and after. `conflict` when a fact file changed, `invalid` when the model has errors outside the migration rules, `bad-request` for an unknown database or baseline |
| `get_ledger_ddl` | (none) | `database` (name or id; optional when the model has one PostgreSQL database), `grantRead` (a role) | `{ contractVersion, database, dialect, schema, ddl, grant }`: the migration ledger's DDL for one PostgreSQL database (docs/ledger.md), three tables in the schema `migrations.ledgerSchema` names (default `migration_ledger`), idempotent and without transaction control, so a migrator runs it in its own transaction; `grant` is the read grant for `grantRead`, else null. The engine never reads a ledger. No PostgreSQL database, an unknown or other database, several PostgreSQL databases and none named, or an empty `grantRead` is `bad-request` |
| `get_schema` | (none) | `kind`: an element kind (`entity`, `value-object`, ...), `custom:<type>` for a custom kind the project declares, or a document (`maquettiste`, `batch`, `pack`, `extension`, `custom-kind`, `declared-rules`) | `{ name, file, schema, references, extensions?, stereotypeParameters? }`: the JSON schema, the schema files it references (`common.json`) and, for an element kind, the project's extension schemas that apply to it (`name`, `description`, `appliesTo`, `properties`, `required`, `schemaPath`; they constrain the element's `properties`) and the typed parameters of the stereotypes that may apply to it (`[{ stereotype, parameters: { properties, required } }]`, by key; they constrain the `properties` of the elements that carry the stereotype, implied ones included); for `custom:<type>`, `custom.json` with `customKind` (the declaration and its `path`) and `contentSchema` (the compiled schema of `content`, the `id` the engine owns on each member item) |

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

The same tools write a seed's rows script (`extensions/seeds/<name>.js`, kind `seed-rows`), which calls
`maquettiste.rows((model, seed) => [...])` exactly once and returns plain objects keyed by the seed's column names. A
seed names it with `rowsFrom: { "script": "seeds/<name>.js" }`. A write loads the script alone and never runs it: a
script that does not load, or registers rows other than once, is MQ7127 in the answer. Nothing runs it until something
reads its rows: `preview_seed_rows` for that seed, a unit that reads them, `validate` or a save of the seed. Read the
rows with `preview_seed_rows`, then `validate` for the row rules.

The process tools mirror the process operations of the editor API (phase-3-design.md section 4.4) and the `maquettiste process`
verbs. A process argument is an id, a name or a model path. Simulation keeps no state on the server: an agent holds the input list,
calls `simulate_process` with all of it, reads `enabled` for what can happen next, and turns a run it likes into a scenario with
`record_scenario`. `verify_scenarios` replays scenarios the way `validate` does (MQ9301 to MQ9306 and MQ9502 to MQ9507).
A library process (`use: library`) has no states, so these tools refuse it; create one with `create_element` or `apply_batch`.
`import_process` and `sync_enum_from_process` are dry runs unless `apply` is true. The process kinds `process`, `actor` and
`scenario` work with every element tool, `get_model_index` (`kind`) and `get_schema`; `list_validation_rules` lists the MQ9xxx
rules with their `quickFix` operation where one exists.

The reference data and localization tools are `localization_status`, `get_translations`, `set_translations`,
`create_seed`, `export_seed_csv`, `import_seed_csv`, `preview_seed_rows` and `reference_type_usage`; the kinds
`reference-type` and `seed` work with every element tool, `get_model_index` (`kind`) and `get_schema`. A table seed may
name a foreign key of its target in `columns` and give the referenced row's natural key in its cells, a `uuid` column
may take `stableId`, and a seed may compute its rows with a rows script (`rowsFrom: { "script": "seeds/<name>.js" }`,
written with `write_extension_file`); `preview_seed_rows` shows one seed's rows as written or scripted, or in a
database's terms. docs/guides/reference-data-and-seeds.md walks through all three.

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
(`source`: a table, a view or a query of the database), its `constants` (a filter on every read and a value on every
insert, several allowed, which is how several entities share one table through an `entity_type` column), its `fields`
(attribute id, `attributeId.memberId` or a to-one relation end id, and the source column it reads, by key or name), the
`columns` it accounts for without a field (`ignored`, `database`, `computed`), where it writes (`write`: `{ "table": id
}` or `"none"`; the source when it is a table) and how it deletes (`delete`: `"key"`, `{ "soft": { "column", "value" }
}` or `"none"`). An entity with a binding to a database is never projected into it. Bind entities explicitly: create the
binding with `save_element`, or let `apply_batch` write it with `materialize-tables` (a designed table with the shape
the entity's projection has, and the binding) or `materialize-entities` (an entity bound to each table), or give a bound
entity attributes for its source's columns with `materialize-attributes` (an attribute per column nothing in the binding
names, or per column named, mapped to it), or give its table columns for its unmapped attributes with
`materialize-columns` (shaped as the projection would make them); `get_materialize_status` says what is left to bind and
`preview_materialize` shows a plan first. The by-name operations record the binding's provenance (each column of its
table file as two short hashes, name and type), so `get_binding_drift` lists what changed in the table since, `validate`
reports a mapped column renamed or retyped (MQ4087) and provenance that does not describe the source (MQ4088), and
`resync-binding` catches up in one batch; `map-by-name` maps unmapped attributes to free columns of the same loose name.
`get_database_view` lists on each table, view and query its `boundBy` (the entities that read or write it, with their
constants); `get_resolved_model` gives each entity record its resolved `bindings`; `preview_binding_sql` renders a
binding's five statements for any dialect. `validate` reports MQ4044 to MQ4054 for them (MQ4047, a column nothing
accounts for, is the "a mapping for every column" warning), and MQ4087 and MQ4088 for provenance. An attribute with no
description of its own shows the comment of the column a binding maps it to, and its resolved record names that column
in `descriptionColumn`. Deleting a table, view, query or database a binding names refuses with the entity among the
referrers; with `remove-references` or `delete-dependents` the binding is removed and the entity stays.

Reference types and seeds are elements: `get_element`, `create_element`, `save_element` and `apply_batch` handle them. Translations
live in locale shards, not in element files: read them with `get_translations`, then pass the `shardHash` values you read as
`expected` to `set_translations`, which writes every entry in one atomic save (reference-types-seeds-localization.md section 3.9).
`import_seed_csv` previews unless `apply` is true; applying saves the seed with `expectedHash` and then the translations of the
`@label:<locale>` and `@description:<locale>` columns.

Change a code that databases store through a refactor, never by editing the cell: `apply_batch` with `rename-code`,
`retire-code`, `merge-reference-types` or `rename-reference-type`, a `dryRun` first. The dry run's item for the type
carries `usage` (what the refactor rewrites, rows moved and removed, translations removed) and the batch's `facts` the
`value-map` facts the write would record, one per database that stores the codes (`null` when the change reaches every
database). The old code stays in the type's `formerCodes`; `import_seed_csv` skips a row whose `@code` is one
(MQ7019), and `list_migrations` summarizes each value map (`maps PaymentTerm codes net30 -> net-30 in 1 column`). A
plain save that changes a kept row's code, or a type's name, records no value map and carries MQ4114.

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
- **Expressions as text.** Wherever the expression language takes a tree (a table, column or entity check's `condition`, a
  derived attribute's `derived.value`, a process or operation guard's `condition`, a constraint's `condition`), `save_element`,
  `create_element`, `patch_element` and `apply_batch` accept `{ "text": "issuedOn <= dueOn" }`: the engine parses it in that
  site's scope and stores the tree of ids, so a rename never breaks it; text that does not parse refuses the write with MQ3101
  at its pointer. A file edited by hand that holds text is read with MQ3108, and the batch operation `text-to-trees`
  (optional `elementIds`) converts it. Try text first with `preview_expression`, which reads one element and writes nothing.
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

**Create or update by name.** `{ "op": "upsert", "id": "@custom:policy.Billing.keeps_lines", "element": { ... } }` creates
the element when the model has none of that name, and replaces its whole document when it has one. A script that refreshes
elements from another source sends the same batch on every run, and a run that changes nothing writes nothing. `ids.names`
lists the name with its id. An upsert never takes over another element or moves one, so it refuses:

- An `id` that is not one of the names the element will have (MQ1015). The element's kind, package and name must all agree
  with it: `@custom:policy.Billing.keeps_lines` needs type `policy`, the package Billing and the name `keeps_lines`. A schema
  the same batch adds is not seen yet, so add it in an earlier batch.
- A found element whose longest name would change (MQ1015). A move is an `update`.
- A seed (MQ1015): a seed belongs to its target, and its rows can live in a file a whole document would not carry. Use
  `create`, `update` or a seed import.
- A name that finds several elements, on the model or after the batch (MQ1013). Its longest form names one, so use that.
- A document that replaces a part of an element that exists: it leaves out a part's id and has a part with none (MQ1014),
  because every run would replace the part. `get_element` and the first run's `ids.minted` give the ids, and a script can
  give its parts fixed ids of its own. A part added with no id gets one, and the next run must send it.
- A name given twice in one batch (MQ1014), and an `expectedHash` for a name that finds nothing (MQ1012).

No `expectedHash` is needed: an update takes the file's hash as it was loaded, or the one you give. An assistant proposal
takes `create` and `update`, not `upsert`, since it says exactly what it changes.

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

#### Impact

A dry run that would save also answers `impact`, how far the change reaches beyond its own files:

- `referrers`: the references, on the model before the change, to what it alters or deletes. The targets are the
  deleted elements and sub-elements (`change: deleted`) and those whose own fields changed (`changed`); an element
  whose only change is in a sub-element is not a target, so renaming `Invoice.number` lists the table overlay and the
  query that name the attribute, not the relations that name `Invoice`. References held by the documents the batch
  itself writes or deletes are left out: they are in `files`. Created elements have none.
- `databases`: every database the change can reach, chosen by ids alone, as for `facts`; a description change
  reaches its database too, since it can change a comment. `facts` keeps its meaning (`null` when the change reaches
  every database).
- `packs`: per enabled pack, in pack order, with `state` `known` (its unit states were read), `none` (never generated
  here: generate once to list files) or `invalid` (the pack does not load). Each unit has a `change`: `inputs` (one of
  the keys it read when it last rendered is touched; `keys` lists up to five of them and `outputs` the files its state
  records), `deleted` (its element goes; the next generate treats its files as orphans) or `added` (a created element,
  or a created attribute for `each attribute`, enters its `for` scope; `outputs` is empty because paths are known only
  after rendering, and `filtered: true` says its `where` filter was not evaluated). A changed element, or an attribute
  it declares, is `added` too for every unit of its scope with no state for it: a `where` filter or a skip hint left it
  out, and the change may let it in. It is `filtered` when the unit has a `where` or the element's hints skipped the
  pack before the change. A skip hint for the pack drops an added unit. A `where` that reads other elements (a
  package's name, a mapping, a script that follows references) is not followed here: when the change is to those
  other elements, plan finds the units it admits. Selector units (`select ...`) and the tables only resolution makes
  are not listed for created elements: plan finds them. Other units already stale from earlier edits are not listed.

A unit is touched when one of its stored read keys is:

| Key | Touched when |
| --- | --- |
| `e:<id>` | the document holding the id (in either model) is written or deleted and its dependency hash differs |
| `k:<kind>` | the change creates or deletes an element of that kind |
| `r:<id>` | a written or deleted document references the id in either model and the hash of its referrers differs |
| `n:<kind>:<form>` | the change writes or deletes a document holding entries of the kind, or a package, database, process or reference type their qualified forms read, and the ids the form finds differ |
| `n:table:<text>`, `d:<database>` | the change reaches a database (`d:` that one) |
| `l:<locale>:<owner>` | the change writes the owner's document or a translation shard and the owner's entries differ |
| `s:<section>` | the change writes `maquettiste.json` and the section differs |
| `k:migration`, `r:<database>` | the save records, or may record, facts in that database |
| `t:<pack>/<path>`, `cv:<kind>` | never: a batch writes no pack file and no calculation script |
| `x:<path>`, `c:<type>` | the batch writes that file under `extensions/` with other content |
| `ix:...`, `rt:...` | always (the rule view's keys) |
| `px:<pack>/<id>`, `px:<pack>` | a key the pack's exporting units read for their stored exports is touched |
| any other kind | always |

Every list is ordered (referrers by target, referring element and pointer; units by key; databases by name, then id)
and bounded (200 referrers, 200 units per pack, five keys per unit), so two dry runs of one batch on one model answer
the same bytes. Nothing is resolved, planned or rendered: the impact comes from the dry run's own models and each
pack's stored unit states, so a model with thousands of tables answers as fast as a small one. It names the files
whose units would render again, not whether their bytes change: apply, then `plan` with `elements` or `units` to see
them. A reach-based key (`d:`, `n:table:` and the facts row) can list a unit the plan then skips.

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
