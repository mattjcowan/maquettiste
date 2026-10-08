# Generate code

Generation reads the model and writes text files from it: SQL scripts, classes, documentation, anything a template
describes. The rules live in **packs**, folders of templates under `.maquettiste/templates/`, which your team owns and
changes like any other code. This guide plans and applies Spoke & Chain's first generation, reads why each file renders,
sets up a CI check, adds project properties that templates read, and makes a small pack of its own.

## A few words first

| Word | Meaning |
| --- | --- |
| Pack | A folder of templates and one `pack.json`. `maquettiste init` installs the `sql-ddl` starter; `csharp-dapper`, `seed-data` and `process-docs` are other example packs. |
| Unit | One line of a pack's work list: a template, which elements it runs for (the scope), and the output path. |
| Output base | The folder a pack's paths start from (`packs.<pack>.output` in `maquettiste.json`). |
| `outputs.allow` | The only paths generation may write under. Anything else is refused before a file is written. |
| Plan | A dry run: it renders what needs rendering and compares it with the disk. Nothing is written until you apply it. |

## 1. Make a plan

1. On the rail, pick **Generate**. The **Plan** tab shows.
2. The toolbar's **Packs** button (it reads **Packs · 1 of 1** with only the starter) chooses which packs to plan. Leave
   `sql-ddl` ticked.
3. Click **Plan**.

One line sums up the plan, such as `Plan ready: 14 files to write (14 added)`. Above the table, one line per pack says
what it will do. The table lists **every file** the packs produce, grouped by unit, each with what Apply will do to it:
added, modified, deleted, identical, not re-rendered, or **yours** (a file written once, such as a migration, that
generation never overwrites).

The chips above the table filter it: **All**, **To write**, **Identical**, **Not re-rendered**, **Yours** and **Edited
by hand**.

!!! note
    When the model has errors, the plan stops and says `Plan stopped: 2 model errors, see Problems`. Fix them first.

## 2. Read why a file renders

1. Click a file, such as the table script for `bikes`. Its diff opens below.
2. On the right, **Why this file** names the pack, unit, template, element and output path, the reason and every cause.
3. The **Why** column gives the reason in one line: "New: no recorded state from an earlier run" the first time, "Bike
   (entity) changed" or "Template table.scriban changed" after an edit.

![The Generate screen after Plan: the plan summary by pack, cause and output root, the files grouped by unit, and Why this file for the selected file, with its diff below](../images/generate-light.png#only-light)
![The Generate screen after Plan: the plan summary by pack, cause and output root, the files grouped by unit, and Why this file for the selected file, with its diff below](../images/generate-dark.png#only-dark)

Generation is incremental. A unit whose inputs did not change is skipped, and its files show as **not re-rendered**.
**Unchanged units** lists them, and **Why not?** on one says what it read. **By cause** counts the files each cause
writes; a cause that names something you can edit links to it.

## 3. Apply the plan

Click **Apply plan**. It writes exactly the planned files under the allowed roots. Run `git status`: the new scripts are
in `db/`.

Change something small in the model, such as the length of `Member.email`, then plan again. Only the files that read it
are re-rendered.

## 4. Generate from a terminal

The command line runs the same engine:

```sh
maquettiste generate            # render and write; one line per file (A added, M modified, D deleted)
maquettiste generate --watch    # regenerate on every change under .maquettiste/, until Ctrl+C
```

A generated file that someone edited by hand stops the run (exit 3), so a hand edit is never lost silently.

## 5. Guard the outputs in CI

`generate --check` renders every output in memory, writes nothing, and exits 2 when any file would be added, changed or
deleted. Add it to your CI pipeline, next to `validate`:

```sh
maquettiste validate
maquettiste generate --check
```

With only Docker on the build machine, run the same commands from the image:

```sh
docker run --rm --user 0:0 -v "$PWD:/repo" -w /repo mattjcowan/maquettiste:<tag> maquettiste generate --check
```

`--check` can only guard files you commit. For outputs you do not commit, run `maquettiste generate` and then compare the
working tree (`git diff --exit-code`). Commit `.maquettiste/manifest/` with the model either way: it records what
generation wrote.

## 6. Add project properties

Project properties are the project's own values that every pack's templates can read, such as a base namespace or the
company's name.

1. Open **Settings › General**.
2. Under **Properties**, click **Add property**. Set the key `baseNamespace` to `SpokeAndChain`.
3. Add a second property, `cooperativeName`, set to `Spoke & Chain`.
4. Click **Save**.

![Settings, General: the project name and a baseNamespace property that the templates read as project.properties.baseNamespace](../images/settings-general-light.png#only-light)
![Settings, General: the project name and a baseNamespace property that the templates read as project.properties.baseNamespace](../images/settings-general-dark.png#only-dark)

A template reads them as `project.properties.<key>`:

```text
namespace {{ project.properties.baseNamespace }}.Model
```

The `csharp-dapper` pack, for instance, takes `baseNamespace` as its root namespace when its own `namespace` parameter is
unset. A key is letters, digits and `_`, not starting with a digit. Changing a property re-renders only the units whose
templates read `project`.

## 7. Make your own pack from a starter

Spoke & Chain wants one short Markdown sheet per entity for the volunteers' handbook.

1. Allow the folder first. Add `{ "path": "docs/model" }` to `outputs.allow` in `.maquettiste/maquettiste.json` (the
   Settings screen shows this list read-only).
2. In the **Generate** explorer, click **+** (or **New pack…** in the command palette).
3. Name it `entity-sheets`. **Start from**: **Empty pack**, which gives one each-entity unit and its template. **Copy
   of** a pack of this project is the other choice.
4. The pack opens as a tab beside **Plan**. In its header, set **Output base** to `docs/model`.
5. On **Units**, change the unit's output path to `{{ kebab entity.name }}.md`.
6. On **Templates**, open `entity.scriban` and make it:

   ```text
   # {{ entity.name }}

   Part of the {{ project.properties.cooperativeName }} model.

   {{~ for attribute in entity.attributes ~}}
   - {{ attribute.name }}
   {{~ end ~}}
   ```

7. The preview on the right renders the unit for one element as you type: pick **Member** to see its sheet. A preview
   writes nothing, and renders that one element only.
8. **Save** (Ctrl+S), then go back to **Plan**, tick the new pack, **Plan** and **Apply plan**.

From a terminal, `maquettiste pack new entity-sheets --from empty` makes the same pack (`--from sql-ddl` or
`--from csharp-dapper` copies a starter). The copy is yours: the example packs are not updated under you.

## See also

- [Generation: how the model becomes files](../user-guide.md#generation-how-the-model-becomes-files): templates, units,
  scopes, output patterns, parameters and write modes.
- [How the plan explains itself](../user-guide.md#how-the-plan-explains-itself) and
  [Change a template and see the result](../user-guide.md#change-a-template-and-see-the-result).
- [Make your own pack from a starter](../user-guide.md#make-your-own-pack-from-a-starter),
  [outputs.allow: what generation may touch](../user-guide.md#outputsallow-what-generation-may-touch) and
  [Packs in the editor](../user-guide.md#packs-in-the-editor-what-generation-does).
- [Writing a pack](../packs/index.md) and the pack pages: [sql-ddl](../packs/sql-ddl.md),
  [csharp-dapper](../packs/csharp-dapper.md), [seed-data](../packs/seed-data.md), [process-docs](../packs/process-docs.md).
- [The command line](../user-guide.md#the-command-line) for every `generate` option and exit code.
