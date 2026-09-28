// A unified diff (--- a/, +++ b/, hunks with three lines of context), as GetPlanDiffAsync returns.
function lines(text: string): string[] {
  if (text === "") return [];
  const parts = text.split("\n");
  if (parts[parts.length - 1] === "") parts.pop();
  return parts;
}

type Op = { kind: " " | "-" | "+"; text: string; a: number; b: number };

/** Line operations from an LCS table; files here are small (generated code). */
function operations(a: string[], b: string[]): Op[] {
  const n = a.length;
  const m = b.length;
  const table: Uint32Array[] = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--) table[i][j] = a[i] === b[j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);
  const ops: Op[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) ops.push({ kind: " ", text: a[i++], a: i, b: ++j });
    else if (table[i + 1][j] >= table[i][j + 1]) ops.push({ kind: "-", text: a[i++], a: i, b: j });
    else ops.push({ kind: "+", text: b[j++], a: i, b: j });
  }
  while (i < n) ops.push({ kind: "-", text: a[i++], a: i, b: j });
  while (j < m) ops.push({ kind: "+", text: b[j++], a: i, b: j });
  return ops;
}

export function unifiedDiff(path: string, before: string, after: string, context = 3): string {
  if (before === after) return "";
  const a = lines(before);
  const b = lines(after);
  const ops = operations(a, b);
  const changed = ops.map((o, i) => (o.kind !== " " ? i : -1)).filter((i) => i >= 0);
  const hunks: [number, number][] = [];
  for (const i of changed) {
    const start = Math.max(0, i - context);
    const end = Math.min(ops.length - 1, i + context);
    const last = hunks[hunks.length - 1];
    if (last && start <= last[1] + 1) last[1] = Math.max(last[1], end);
    else hunks.push([start, end]);
  }
  const out = [`--- a/${path}`, `+++ b/${path}`];
  for (const [start, end] of hunks) {
    const slice = ops.slice(start, end + 1);
    let aStart = 0;
    let bStart = 0;
    let aCount = 0;
    let bCount = 0;
    // Line numbers before the hunk: count operations up to `start`.
    for (const o of ops.slice(0, start)) {
      if (o.kind !== "+") aStart++;
      if (o.kind !== "-") bStart++;
    }
    for (const o of slice) {
      if (o.kind !== "+") aCount++;
      if (o.kind !== "-") bCount++;
    }
    out.push(`@@ -${aCount ? aStart + 1 : aStart},${aCount} +${bCount ? bStart + 1 : bStart},${bCount} @@`);
    for (const o of slice) out.push(o.kind + o.text);
  }
  return out.join("\n") + "\n";
}
