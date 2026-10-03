// Opens a table's editor (every table has one: a table file by its id, a table the model lays out by its key) on a tab, with a
// part picked: the inspector then shows that part alone.
import { openTab, setView } from "@/editors/tabs";
import { tableKeyOf } from "@/search/engine";
import type { EditorStore } from "@/state/store";
import { PART_TAB, type TablePart, type TableTab } from "./tableParts";

export function openTableEditor(
  store: EditorStore,
  database: string,
  key: string,
  options: { tab?: TableTab; part?: TablePart | null; pin?: boolean; explorerItem?: string } = {},
): void {
  const s = store.getState();
  const part = options.part ?? null;
  s.inspectTable(
    { database, key, column: part?.kind === "column" ? part.id : null, part: part && part.kind !== "column" ? part : null },
    options.explorerItem ?? tableKeyOf(database, key),
  );
  const tab = options.tab ?? (part ? PART_TAB[part.kind] : undefined);
  s.updateEditors((e) => openTab(tab ? setView(e, "table", tab) : e, { id: key, kind: "table" }, { pin: options.pin ?? true }));
}
