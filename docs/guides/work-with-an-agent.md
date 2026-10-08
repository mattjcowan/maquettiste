# Work with an agent

A coding agent can read and change the model the way you do in the editor. Maquettiste gives it two things: the **agent
server** (`maquettiste mcp`, over the Model Context Protocol), and the **modeling skill**, a file that teaches the agent
the model's conventions. Inside the editor there is also the **assistant**, a chat panel that proposes model changes for
you to review.

All three go through the engine's one write path, the same one the editor uses: the same documents, hashes, validation,
plans and error codes. This guide sets them up for Spoke & Chain and shows what each can and cannot do.

## 1. Register the agent server

In the repository, run one of these:

```sh
maquettiste init --agent-setup     # registers the server and writes the skill
maquettiste init --mcp             # only the server
maquettiste init --skill           # only the skill
```

With only Docker on the machine, register the server as a run of the image, and add the skill in the same step:

```sh
maquettiste init --mcp --docker mattjcowan/maquettiste:<tag> --skill
```

`init --mcp` writes (or merges into) `.mcp.json` at the repository root, the project file that MCP clients read. The entry
starts `maquettiste mcp` in the project folder, which finds the model from there, so the file can be committed for the
whole team. Other servers in an existing `.mcp.json` are kept.

## 2. Start your agent and connect

1. Start your MCP client in the repository.
2. Approve the project server `maquettiste` when the client asks.
3. Check that the client lists the `maquettiste` tools.

If the client reports the server as failed under the Docker form, look at `.maquettiste/.cache/mcp.log` first: the server's
messages go there.

## 3. Add your own conventions

`init --skill` writes `.claude/skills/maquettiste-modeling/SKILL.md`. The file belongs to Maquettiste: `init --skill`
refreshes it when you upgrade, and leaves it alone (writing `SKILL.md.new` beside it) if someone edited it by hand.

Put the cooperative's own rules in `CONVENTIONS.md` in the same folder. `init` never touches it, and agents read it after the
skill, letting it win where the two disagree. For example:

```markdown
- Entity names are singular nouns in English (Member, Bike, RepairOrder).
- Money is stored in cents as int64, with an attribute name ending in Cents.
- Every entity in Rentals and Workshop is bound to the database main.
```

In CI, `maquettiste init --skill --check` writes nothing and exits 2 when the skill is missing, stale or edited.

## 4. Ask for a change

Ask in plain words, for example:

> Add an entity `RepairOrder` to the Workshop domain with a key, a `reportedAt` timestamp and a required relation to
> `Bike`, then validate.

The skill tells the agent to use the server's tools. A typical run reads the project and the model index, reads the
schema of the kind it writes, saves the new element with the hash it read, and runs `validate`. You see the result in the
editor, which reads the same files.

Questions work too: "Which entities reference `Bike`?", or "Why does MQ4047 fire on `Rental`?"

## 5. Let the agent generate

The agent can plan and apply generation as you would on the Generate screen. It runs a plan, reads the diff of the files
it cares about, then applies that plan by its id. A plan whose inputs changed since is refused as stale, and nothing is
written. Ask it to show you the plan's diff before it applies.

## 6. Use the assistant in the editor

The **Assistant** panel is a chat about the model on the right side of the editor. Open it with the top bar's assistant
button or Ctrl+I.

1. Ask: "Add an optional `closedAt` timestamp to `RepairOrder`."
2. The answer arrives as a **proposal card**: a one-line summary and every file it would create, change or delete, each
   expandable to its diff.
3. Press **Apply** to write it, or **Discard** to drop it. Apply is one model batch: validated, one undo step (Ctrl+Z
   takes it back), and seen at once by every other window.
4. If one of the documents changed since the assistant read it, Apply is refused as a conflict. **Ask again** has it read
   them again and propose anew.

Above the message box, **context chips** show what goes along with your message: the workspace, the open element, the
selection and a summary of the current problems. Remove a chip to leave that part out. **Apply small changes without
asking** (off by default) applies a proposal of at most three operations and no deletes as soon as it arrives.

The assistant needs an AI provider, which an administrator sets up in the host, never in Maquettiste. **Settings ›
Assistant** says whether the site has one, and holds the house rules and token budgets.

## What an agent can and cannot do

| | Agent over MCP | Assistant in the editor |
| --- | --- | --- |
| Read the model, references, database views, validation, SQL previews, plans | Yes | Yes, with the same read tools |
| Change the model | Yes, through the element and batch tools | Only as proposals you apply |
| Run and apply generation | Yes (plan, then apply by plan id) | No |
| Write packs, extension files, settings, translations | Yes, through their tools | No |
| Overwrite a file changed since it read it | No: the save is refused as a conflict, with both versions | No: Apply is refused |
| Save a change that introduces model errors | No: refused as invalid, with the diagnostics | No: proposals are checked first |
| Write generated files outside `outputs.allow` | No | No |

Every write names the hash of the version it read, so neither an agent nor the assistant can silently overwrite your
work. Batches are all or nothing. A delete that other elements depend on is refused unless the caller asks for the
references to be cleared or the dependents removed, and a dry run shows that plan first.

!!! tip
    Take a snapshot before you hand an agent a large change: `maquettiste snapshot create "Before the workshop model"`.
    Compare it with the working model as it goes, and restore it if the change goes wrong.

## See also

- [Maquettiste over MCP](../mcp.md): [Running it](../mcp.md#running-it), every [tool](../mcp.md#tools), the
  [semantics](../mcp.md#semantics) of hashes, conflicts and batches, and the
  [conventions resource and prompt](../mcp.md#conventions-resource-and-prompt).
- [The assistant](../user-guide.md#the-assistant): provider setup, house rules, budgets and privacy.
- [The command line](../user-guide.md#the-command-line), for `init --mcp`, `--skill` and `--agent-setup`.
- [Snapshots](../user-guide.md#snapshots).
