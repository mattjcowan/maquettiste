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
messages only; the start line and any error go to stderr. The server stops (exit 0) when the client closes stdin, or on Ctrl+C.

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
`"args": ["tool", "run", "maquettiste", "mcp"]` instead, so the pinned version runs. There is no `--repo`: the client starts
the server in the project folder, the server finds the repository from there, and the file can be committed. Other servers
and members of an existing `.mcp.json` are kept in order; an existing `maquettiste` entry is never replaced; a file that is not
a JSON object (not valid JSON, duplicate keys, or an `mcpServers` that is not an object) is left alone with a hint and exit 0. `init --skill` writes the skill that ships with
the installed version and refreshes it when the tool is upgraded. Both files are setup writes of the engine's path policy
(a symbolic link that leads out of the repository is refused, exit 4). Without init, `claude mcp add maquettiste -- maquettiste mcp`
registers the server for the current user only.

Start `claude` in the repository, approve the project server when asked (or check with `/mcp`), and ask for a model change:
the skill tells the agent to use the `mcp__maquettiste__*` tools and to fall back to file edits only without them.

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
claude                               # then /mcp shows maquettiste connected with 18 tools
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
| `create_element` | createElement | `element` (an id is assigned when absent) | the save result with the new `id` |
| `delete_element` | deleteElement | `id`, `expectedHash`, `resolution` (`refuse` default, or `remove-references`) | the save result |
| `apply_batch` | applyBatch | `operations` (the batch's `operations` array, or the whole `{ "operations": [...] }` body) | the batch result, all or nothing |
| `get_references` | getReferences | `id` | where the element is used |
| `validate` | validate | `elementIds` (optional scope), `includeReferrers`, `includeScriptRules` | the report: diagnostics with rule ids, file, JSON pointer, line and column; counts |
| `get_database_view` | getDatabaseView | `id` of a database | the resolved physical view |
| `list_packs` | (part of getProject) | | pack manifests and their diagnostics |
| `get_settings` | getSettings | | `maquettiste.json`: typed settings, canonical `json`, `hash` |
| `save_settings` | saveSettings | `settings` (whole document), `expectedHash` | the save result |
| `plan` | startPlan, run to completion | `packs`, `force`, `roots` (`all`, `committed`, `built`), `handEdits` (`fail`, `overwrite`, `skip`), `jobs` | `{ outcome, plan }`, the plan without its per-unit list (as a plan job's `planResult`) |
| `get_plan` | getPlan | `planId`, `units` | a stored plan |
| `get_plan_diff` | getPlanDiff | `planId`, `path` | the unified diff text of one planned file |
| `apply_plan` | startApply, run to completion | `planId` | the apply result (as an apply job's `applyResult`) |
| `get_schema` | (none) | `kind`: an element kind (`entity`, `value-object`, ...) or a document (`maquettiste`, `batch`, `pack`, `extension`) | `{ name, file, schema, references, extensions? }`: the JSON schema, the schema files it references (`common.json`) and, for an element kind, the project's extension schemas that apply to it (`name`, `description`, `appliesTo`, `properties`, `required`, `schemaPath`; they constrain the element's `properties`) |

Documents may be passed as JSON objects or as strings holding one. Writes are recorded as `ChangeSource.Cli`.

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
