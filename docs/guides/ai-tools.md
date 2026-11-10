# Use Maquettiste with your AI tool

Maquettiste gives an agent two things. The first is the MCP server, `maquettiste mcp`. The setup files give no folder, so
the server must start in the project folder; then it works in every git worktree. VS Code documents that it starts servers
in the workspace folder, and Claude Code was tried end to end. The other tools' documentation does not say which folder a
server starts in: after setup, ask the agent to call `get_project` and check that it reports the right repository and
worktree. The second is the modeling skill, a `SKILL.md` file that teaches the agent the
model's conventions. Commit the setup files, so every clone and every worktree has them.

This page shows the setup for each tool. For what an agent can do once it is connected, see
[Work with an agent](work-with-an-agent.md).

## Quick setup

Run this in the repository, with the tools your team uses:

```sh
maquettiste init --agents claude,codex,cursor
```

`--agents` takes tool names, separated by commas. It writes each named tool's files. `--agents all` writes the files for
every supported tool. `*` is not accepted, because shells expand it.

`--agent-setup` writes the Claude Code files, the same files as `--agents claude`. It also refreshes the other copies of the
skill that Maquettiste owns.

Name the tools your team uses. `all` is for trying it out: some tools read more than one of these files, and may then list
the server twice.

| Tool | Name for `--agents` | Files written |
| --- | --- | --- |
| Claude Code | `claude` | `.mcp.json`, `.claude/skills/` |
| Codex | `codex` | `.codex/config.toml`, `.agents/skills/` |
| Cursor | `cursor` | `.cursor/mcp.json`, `.agents/skills/` |
| GitHub Copilot | `copilot` | `.vscode/mcp.json`, `.github/mcp.json`, `.agents/skills/` |
| Grok Build | `grok` | `.mcp.json`, `.claude/skills/` |
| Kiro | `kiro` | `.kiro/settings/mcp.json`, `.kiro/skills/` |
| JetBrains Junie | `junie` | `.junie/mcp/mcp.json` |
| Windsurf (now Devin) | `devin` | `.devin/mcp_config.json`, `.agents/skills/` |
| Amp | `amp` | `.amp/settings.json`, `.agents/skills/` |

The tools below that are not in the table have no file that `init` writes. Each section says what to do instead.

Every entry runs `maquettiste mcp`. With only Docker on the machine, use the Docker form in
[the agent server page](../mcp.md) instead. If the repository pins the tool in a local tool manifest, the command is
`dotnet tool run maquettiste mcp`. On Windows, the Docker form uses `${PWD}`, which was checked with Claude Code only; other
tools may not expand it.

## Claude Code

Project file `.mcp.json`:

```json
{ "mcpServers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"] } } }
```

- Skills: `.claude/skills/`.
- Instructions: `CLAUDE.md`. Claude Code reads `AGENTS.md` only when there is no `CLAUDE.md`. A `CLAUDE.md` can pull it
  in with `@AGENTS.md`.
- Approval: it asks once for each project server. `claude mcp reset-project-choices` resets the answers. The `-p` mode
  and the SDK load project servers without asking.

## Codex and the ChatGPT desktop app

Project file `.codex/config.toml`. The CLI, the IDE extension and the ChatGPT desktop app share it.

```toml
[mcp_servers.maquettiste]
command = "maquettiste"
args = ["mcp"]
```

- Skills: `.agents/skills/`.
- Instructions: `AGENTS.md`.
- Trust: Codex reads the project file only for trusted projects.

## GitHub Copilot

Copilot has several surfaces, and they read different files.

**VS Code.** Project file `.vscode/mcp.json`:

```json
{ "servers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"] } } }
```

VS Code can also read a `.mcp.json` at the workspace root, in the `mcpServers` shape. Workspace servers follow Workspace
Trust. `MCP: Reset Trust` clears the answers.

**Copilot CLI.** Project file `.github/mcp.json` or `.mcp.json`. It does not read `.vscode/mcp.json`.
`init --agents copilot` writes `.github/mcp.json`, with a `tools` list as GitHub's examples show:

```json
{ "mcpServers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"], "tools": ["*"] } } }
```

When the repository root also has a `.mcp.json` (the Claude Code and Grok file), Copilot CLI reads `.mcp.json` first. If it
does not list Maquettiste's tools, add `"tools": ["*"]` to that entry. The CLI
loads project servers only after you trust the folder. Without trust it skips them and says nothing.

**Copilot cloud agent.** No file in the repository. Paste the JSON in the repository settings, under Copilot, MCP servers:

```json
{ "mcpServers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"], "tools": ["*"] } } }
```

The `maquettiste` command must exist on the cloud runner, so add an install step to the agent's setup workflow. Once
configured, the agent runs the tools without asking.

All surfaces: skills in `.github/skills/`, `.claude/skills/` or `.agents/skills/` (which surface reads which folder is not
confirmed, so check the tool's documentation). Instructions: `.github/copilot-instructions.md` and `AGENTS.md`.

## Cursor

Project file `.cursor/mcp.json`:

```json
{ "mcpServers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"] } } }
```

- Skills: `.agents/skills/`, `.cursor/skills/` and `.claude/skills/`.
- Instructions: `AGENTS.md` and `.cursor/rules/`.
- Approval: the tool's documentation does not describe a step for project servers. Tool calls ask by default.

## Grok Build

Project file `.grok/config.toml`:

```toml
[mcp_servers.maquettiste]
command = "maquettiste"
args = ["mcp"]
```

Grok Build also reads a project `.mcp.json` and `.cursor/mcp.json`, so the file `init` writes is enough.

- Skills: `.grok/skills/`. It also reads Claude Code skills.
- Instructions: `AGENTS.md` and `CLAUDE.md`.
- Approval: no step for project MCP servers is documented.

## Windsurf, now Devin

Windsurf is now Devin Local and Devin Desktop. Project file `.devin/mcp_config.json`:

```json
{ "mcpServers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"] } } }
```

A `.devin/mcp_config.local.json` is meant for personal settings and is kept out of git.

- Skills: `.devin/skills/` and `.agents/skills/`. The older `.windsurf/skills/` still works.
- Instructions: `AGENTS.md` and `.devin/rules/`.
- Approval: no project trust step is documented. Tool calls ask by default.

## Kiro

Project file `.kiro/settings/mcp.json`:

```json
{ "mcpServers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"] } } }
```

- Skills: `.kiro/skills/`. Kiro does not read the other skill folders.
- Instructions: `.kiro/steering/` and `AGENTS.md`.
- Approval: none documented.

## JetBrains Junie

Project file `.junie/mcp/mcp.json`, read by Junie CLI:

```json
{ "mcpServers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"] } } }
```

- Skills: the tool's documentation does not describe skill folders. Check it.
- Instructions: `.junie/AGENTS.md`, then `AGENTS.md`.
- Approval: none documented.

## Amp

Project file `.amp/settings.json`:

```json
{ "amp.mcpServers": { "maquettiste": { "type": "stdio", "command": "maquettiste", "args": ["mcp"] } } }
```

- Skills: `.agents/skills/` and `.claude/skills/`.
- Instructions: `AGENTS.md`.
- Approval: a workspace server must be approved once with `amp mcp approve maquettiste`. Servers in your user settings need
  no approval.

## OpenCode

`init` does not write this one. Add it to `opencode.json` in the project. The tool's documentation shows this shape, with
the command as one list. Versions differ, so check the documentation of the version you run.

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": { "servers": { "maquettiste": { "type": "local", "command": ["maquettiste", "mcp"] } } }
}
```

- Skills: `.opencode/skills/`, `.claude/skills/` and `.agents/skills/`.
- Instructions: `AGENTS.md`, with `CLAUDE.md` as a fallback.
- Approval: none. It connects at once.

## Antigravity

Antigravity is the successor of Gemini CLI. Its project file is `.agents/mcp_config.json`. The tool's migration page shows
only a remote example, so the shape for a local server below is a guess. Check the tool's documentation.

```json
{ "mcpServers": { "maquettiste": { "command": "maquettiste", "args": ["mcp"] } } }
```

- Skills: `.agents/skills/`.
- Instructions: `GEMINI.md` and `AGENTS.md`.
- Approval: not documented.

## Zed

Zed has no project file for MCP servers that its documentation describes. Add the server to your user `settings.json`:

```json
{ "context_servers": { "maquettiste": { "command": "maquettiste", "args": ["mcp"], "env": {} } } }
```

- Skills: `.agents/skills/`, read for trusted folders only.
- Instructions: check the tool's documentation.
- Trust: see [Worktrees and trust](#worktrees-and-trust).

## Cline

Cline has no project file that its documentation describes. Add the server to its global `cline_mcp_settings.json`, from
the MCP servers screen:

```json
{ "mcpServers": { "maquettiste": { "command": "maquettiste", "args": ["mcp"], "disabled": false, "autoApprove": [] } } }
```

- Skills: `.cline/skills/`, `.clinerules/skills/` and `.claude/skills/`.
- Instructions: `.clinerules/` and `AGENTS.md`.

Because the server starts in the folder the project is open in, one global entry serves every worktree. Check the tool's
documentation for the working folder it uses.

## JetBrains AI Assistant

No file name is documented. In the IDE settings, open the MCP servers screen, add a server, paste this, and set the level
to "current project". The dialog has a working folder field, which can stay at the project folder.

```json
{ "mcpServers": { "maquettiste": { "command": "maquettiste", "args": ["mcp"] } } }
```

Where the assistant reads skills and instructions is not documented. Check the tool's documentation.

## ChatGPT on the web

ChatGPT on the web cannot start a program on your machine. It needs a server at a URL. Maquettiste does not offer one yet,
so it does not work there. The ChatGPT desktop app is different: it uses the Codex setup above.

## Worktrees and trust

A committed file is in every worktree. What can differ is the question each tool asks about a new folder.

| Tool | What happens in a new worktree |
| --- | --- |
| Claude Code | Asks to approve the project server. An approval in user settings (`enabledMcpjsonServers`) or `claude mcp add --scope user` covers every folder. |
| Codex | Reads the project file only for a trusted project. How trust applies to worktrees is not confirmed. Check the tool's documentation. |
| Copilot CLI | Asks to trust the folder. Skips project servers silently until you do. |
| Copilot in VS Code | Follows Workspace Trust, which is per folder. |
| Zed | Starts every folder as restricted. It ignores project settings and project servers until you trust it. Trust lasts per folder, and trusting a parent folder covers the folders below it. Put your worktrees under one parent and trust that. |
| Amp | Asks you to approve a workspace server with `amp mcp approve`. |

Cursor, Grok Build, Devin, Kiro, Junie, OpenCode and Cline document no trust step. Tool calls may still ask, in the tools
that do that by default.

The agent can tell which copy it works on. `get_project` reports the worktree's name, so an agent started in a worktree
knows which model it serves.

## Instructions file

Many tools read `AGENTS.md`. Maquettiste never writes it, because it is yours. Paste this into your own `AGENTS.md` (or
`CLAUDE.md`):

```markdown
## Model

This repository has a Maquettiste model. Use the `maquettiste` MCP server and the maquettiste-modeling skill to read and
change it; do not edit files in `.maquettiste/` by hand. When the server is not running, use `maquettiste model apply`
to change the model and `maquettiste validate` to check it.
```

## Checked on 2026-11-10

Checked against each tool's documentation on 2026-11-10; these tools change often. If a file name here no longer works,
the tool's own page wins.

- [Claude Code: MCP](https://code.claude.com/docs/en/mcp), [skills](https://code.claude.com/docs/en/skills),
  [memory](https://code.claude.com/docs/en/memory), [worktrees](https://code.claude.com/docs/en/worktrees)
- [Codex: MCP](https://learn.chatgpt.com/docs/extend/mcp?surface=cli),
  [skills](https://learn.chatgpt.com/docs/build-skills),
  [AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
- [GitHub Copilot in VS Code: MCP servers](https://code.visualstudio.com/docs/copilot/customization/mcp-servers),
  [MCP configuration](https://code.visualstudio.com/docs/agents/reference/mcp-configuration),
  [skills](https://code.visualstudio.com/docs/copilot/customization/agent-skills)
- [Copilot CLI: MCP servers](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers),
  [cloud agent: MCP](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/coding-agent/extend-coding-agent-with-mcp),
  [skills](https://docs.github.com/en/copilot/concepts/agents/about-agent-skills)
- [Cursor: MCP](https://cursor.com/docs/context/mcp), [skills](https://cursor.com/docs/context/skills),
  [rules](https://cursor.com/docs/context/rules)
- [Grok Build: MCP servers](https://docs.x.ai/build/features/mcp-servers),
  [skills](https://docs.x.ai/build/features/skills-plugins-marketplaces)
- [Devin (was Windsurf): MCP](https://docs.devin.ai/desktop/cascade/mcp),
  [skills](https://docs.devin.ai/desktop/cascade/skills)
- [Kiro: MCP](https://kiro.dev/docs/mcp/configuration/), [skills](https://kiro.dev/docs/skills/),
  [steering](https://kiro.dev/docs/steering/)
- [Junie: MCP](https://junie.jetbrains.com/docs/junie-cli-mcp-configuration.html),
  [guidelines](https://junie.jetbrains.com/docs/guidelines-and-memory.html)
- [Amp: MCP](https://ampcode.com/docs/customize/mcp), [skills](https://ampcode.com/manual/agent-skills.md),
  [AGENTS.md](https://ampcode.com/docs/customize/agents-md)
- [OpenCode: MCP servers](https://opencode.ai/docs/mcp-servers/), [skills](https://opencode.ai/docs/skills/),
  [rules](https://opencode.ai/docs/rules/)
- [Antigravity: Gemini CLI migration](https://antigravity.google/docs/cli/gcli-migration)
- [Zed: MCP](https://zed.dev/docs/ai/mcp), [skills](https://zed.dev/docs/ai/skills),
  [worktree trust](https://zed.dev/docs/worktree-trust)
- [Cline: MCP servers](https://docs.cline.bot/mcp/configuring-mcp-servers),
  [skills](https://docs.cline.bot/features/skills)
- [JetBrains AI Assistant: MCP](https://www.jetbrains.com/help/ai-assistant/mcp.html)
