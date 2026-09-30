// Which output lines a template line produced, and back (generation-ui.md 3.3, step 6), pure. The template renderer
// does not report output positions, so this is a best-effort text match, and the UI says so: a template line's literal
// text (what lies outside its `{{ }}` blocks) must appear, in order, on the output line. A line of code only, or whose
// literal text is too short to tell lines apart, matches nothing.

/** The literal pieces of a template line: the text outside `{{ }}` (and `{%{ }%}` raw blocks kept as text), trimmed. */
export function literalPieces(line: string): string[] {
  const pieces: string[] = [];
  let rest = line;
  while (rest.length) {
    const open = rest.indexOf("{{");
    if (open < 0) {
      pieces.push(rest);
      break;
    }
    pieces.push(rest.slice(0, open));
    const close = rest.indexOf("}}", open + 2);
    if (close < 0) break;
    rest = rest.slice(close + 2);
  }
  return pieces.map((p) => p.trim()).filter((p) => p.length > 0);
}

/** A template line takes part only when its literal text has at least this many characters that are not white space or punctuation. */
const MIN_SIGNAL = 3;

function signal(pieces: string[]): number {
  return pieces.join("").replace(/[\s\p{P}\p{S}]/gu, "").length;
}

function containsInOrder(line: string, pieces: string[]): boolean {
  let at = 0;
  for (const p of pieces) {
    const found = line.indexOf(p, at);
    if (found < 0) return false;
    at = found + p.length;
  }
  return true;
}

export interface LineMap {
  /** 1-based template line to the 1-based output lines it matched. */
  toOutput: Map<number, number[]>;
  /** 1-based output line to the 1-based template lines that match it. */
  toTemplate: Map<number, number[]>;
}

/** Matches every template line against every output line (both 1-based). */
export function matchLines(template: string, output: string): LineMap {
  const toOutput = new Map<number, number[]>();
  const toTemplate = new Map<number, number[]>();
  const outLines = output.split("\n");
  template.split("\n").forEach((line, i) => {
    const pieces = literalPieces(line);
    if (!pieces.length || signal(pieces) < MIN_SIGNAL) return;
    outLines.forEach((out, j) => {
      if (!containsInOrder(out, pieces)) return;
      toOutput.set(i + 1, [...(toOutput.get(i + 1) ?? []), j + 1]);
      toTemplate.set(j + 1, [...(toTemplate.get(j + 1) ?? []), i + 1]);
    });
  });
  return { toOutput, toTemplate };
}
