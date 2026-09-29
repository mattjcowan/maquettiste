// The raw JSON text an index row array was parsed from, kept beside the array until the search client hands it to the
// worker (explorer-redesign.md 4.3, "Search worker handoff"): posting the text is one string copy on the main thread,
// where encoding the parsed rows walks every row. Taken once; an array with no text (patched, or from a test) is encoded.
const texts = new WeakMap<readonly unknown[], string>();

export function rememberIndexText(rows: readonly unknown[], text: string): void {
  texts.set(rows, text);
}

/** The text the rows were parsed from, once; undefined when unknown or already taken. */
export function takeIndexText(rows: readonly unknown[]): string | undefined {
  const text = texts.get(rows);
  texts.delete(rows);
  return text;
}
