# Releases

What each version added, newest first. Every version is a tag on the repository, an image
(`mattjcowan/maquettiste:<version>`) and the `Maquettiste.Cli` and `Maquettiste.Engine` packages of the same number. The
[getting started](getting-started.md) page and the [update steps](getting-started.md#updating) say how to move to a new one.

## 0.10.0 (2026-10-07)

- **Storage both ways.** From an entity's Storage tab, create the attributes for a table's unmapped columns, or the columns for
  an entity's unmapped attributes, in one step. The field map labels where each mapping comes from.
- **Project properties.** Key and value pairs on Settings, General, which templates read as `project.properties.<key>`. The
  csharp-dapper pack takes its namespace from `baseNamespace` when its own parameter is empty.
- **Tags across the model.** Settings, Tags lists the tags in use that are not declared, declares them in one click, renames a
  tag everywhere and removes tags from every element that has them. Tags have a color picker.
- The browser tab shows the project's name, and editors on different ports of one machine keep their own sign-in.

## 0.9.0 (2026-10-06)

- PostgreSQL DDL features as typed fields, edited in the editor: temporal keys, exclusion constraints, partitioning, storage
  parameters.
- A foreign key can set only some of its columns on delete.

## 0.8.0 (2026-10-05)

- Model snapshots: take one, open the model as of it, compare two, restore, export and import.
- The inspector edits only the picked column.

## 0.7.0 to 0.7.2 (2026-10-03 to 2026-10-05)

- The assistant: a sidebar that reads the model and proposes changes, which you review before they are saved.
- Template previews are bounded to what is on screen; relation ends are indexed.
- Quick edits survive while a table is being stored.

## 0.6.0 and 0.6.1 (2026-10-03)

- Databases are designed on their own; entities bind to tables, views and queries, and materialize either way.
- A plan lists every file with what Apply will do to it, and offers force and the hand-edit choice.
- Every element has a property bag. The modeling skill refreshes without erasing a team's conventions.
- The DDL covers every dialect, and entities stay out of the generated DDL unless mapped.

## 0.5.0 to 0.5.5 (2026-09-30 to 2026-10-02)

- Processes: lifecycles and orchestrations as statecharts, drawn, laid out and simulated in the editor, with actors and
  scenarios. The engine interprets, verifies, imports and exports them, and the packs generate code and documentation.
- Queries over tables that fill entities; routines, database types and SQL objects; tables, views and sequences created from
  the editor.
- Extensions edited in the editor; pack renames everywhere; delete with dependents.
- The agent server registered in `.mcp.json` with no script in the repository.
- Generation writes every root, and ignore files as managed blocks.

## 0.1.0 to 0.4.0 (2026-09-29)

- The engine, the command line, the example packs and the editor: domains, entities, relations, enums, databases, reference
  data with seeds, localization, and the agent server.
- Generation visible and configurable in the editor; explicit database mapping; entity seeds edited in the editor; branding.
