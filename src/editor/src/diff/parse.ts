// Unified diff parsing for the in-house viewer (PD16).
export interface DiffLine {
  type: "context" | "add" | "del";
  text: string;
  oldNo: number | null;
  newNo: number | null;
}

export interface DiffHunk {
  header: string;
  oldStart: number;
  oldCount: number;
  newStart: number;
  newCount: number;
  lines: DiffLine[];
}

export interface ParsedDiff {
  oldPath: string | null;
  newPath: string | null;
  hunks: DiffHunk[];
  additions: number;
  deletions: number;
}

const HUNK = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/;

export function parseUnifiedDiff(text: string): ParsedDiff {
  const result: ParsedDiff = { oldPath: null, newPath: null, hunks: [], additions: 0, deletions: 0 };
  let hunk: DiffHunk | null = null;
  let oldNo = 0;
  let newNo = 0;
  const lines = text.split("\n");
  if (lines[lines.length - 1] === "") lines.pop();
  for (const line of lines) {
    if (!hunk && line.startsWith("--- ")) {
      result.oldPath = line.slice(4).replace(/^a\//, "");
      continue;
    }
    if (!hunk && line.startsWith("+++ ")) {
      result.newPath = line.slice(4).replace(/^b\//, "");
      continue;
    }
    const m = HUNK.exec(line);
    if (m) {
      hunk = {
        header: line,
        oldStart: Number(m[1]),
        oldCount: m[2] === undefined ? 1 : Number(m[2]),
        newStart: Number(m[3]),
        newCount: m[4] === undefined ? 1 : Number(m[4]),
        lines: [],
      };
      oldNo = hunk.oldStart;
      newNo = hunk.newStart;
      result.hunks.push(hunk);
      continue;
    }
    if (!hunk) continue;
    if (line.startsWith("\\")) continue; // "\ No newline at end of file"
    const mark = line[0];
    const body = line.slice(1);
    if (mark === "+") {
      hunk.lines.push({ type: "add", text: body, oldNo: null, newNo: newNo++ });
      result.additions++;
    } else if (mark === "-") {
      hunk.lines.push({ type: "del", text: body, oldNo: oldNo++, newNo: null });
      result.deletions++;
    } else {
      hunk.lines.push({ type: "context", text: body, oldNo: oldNo++, newNo: newNo++ });
    }
  }
  return result;
}

export type InlineRow = { kind: "hunk"; text: string } | { kind: "line"; line: DiffLine };
export type SplitRow = { kind: "hunk"; text: string } | { kind: "pair"; left: DiffLine | null; right: DiffLine | null };

export function inlineRows(diff: ParsedDiff): InlineRow[] {
  return diff.hunks.flatMap((h) => [{ kind: "hunk" as const, text: h.header }, ...h.lines.map((line) => ({ kind: "line" as const, line }))]);
}

/** Side by side: deletions paired with the additions that follow them. */
export function splitRows(diff: ParsedDiff): SplitRow[] {
  const rows: SplitRow[] = [];
  for (const h of diff.hunks) {
    rows.push({ kind: "hunk", text: h.header });
    let i = 0;
    while (i < h.lines.length) {
      const line = h.lines[i];
      if (line.type === "context") {
        rows.push({ kind: "pair", left: line, right: line });
        i++;
        continue;
      }
      const dels: DiffLine[] = [];
      const adds: DiffLine[] = [];
      while (i < h.lines.length && h.lines[i].type === "del") dels.push(h.lines[i++]);
      while (i < h.lines.length && h.lines[i].type === "add") adds.push(h.lines[i++]);
      for (let k = 0; k < Math.max(dels.length, adds.length); k++) rows.push({ kind: "pair", left: dels[k] ?? null, right: adds[k] ?? null });
    }
  }
  return rows;
}
