// The raw JSON text an index row array was parsed from, and the bytes it was decoded from when there were any, kept beside
// the array until the search client hands them to the worker (explorer-redesign.md 4.3 and 4.5, "Search worker handoff").
// The bytes go over as a transferable (no copy on the main thread) and the worker parses them itself; with text only, the
// text is posted in slices. Taken once; an array with neither (patched, or from a test) is encoded.
export interface IndexText {
  text: string;
  bytes?: ArrayBuffer;
}

const texts = new WeakMap<readonly unknown[], IndexText>();
/** Called as soon as an index is parsed (the search client registers it), so the worker parses while the tree paints. */
let sink: ((rows: readonly unknown[]) => void) | null = null;

export function rememberIndexText(rows: readonly unknown[], text: string, bytes?: ArrayBuffer): void {
  texts.set(rows, { text, bytes });
  sink?.(rows);
}

/** The text (and bytes) the rows were parsed from, once; undefined when unknown or already taken. */
export function takeIndexText(rows: readonly unknown[]): IndexText | undefined {
  const text = texts.get(rows);
  texts.delete(rows);
  return text;
}

/** Registers the function told of every parsed index (null removes it). */
export function onIndexText(next: ((rows: readonly unknown[]) => void) | null): void {
  sink = next;
}
