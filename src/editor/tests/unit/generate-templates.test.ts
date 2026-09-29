// The Templates tab's pure parts (generation-ui.md 3.3): the file tree, languages, the units a file feeds, the
// overlay, the diagnostics of a file, and the preview debounce.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cn } from "@/lib/cn";
import {
  diagnosticsFor,
  fileTree,
  languageOf,
  overlayOf,
  PreviewScheduler,
  unitsForFile,
  type Diagnostic,
  type PackFileInfo,
} from "@/workspaces/generate/templatesModel";

const file = (path: string, role: PackFileInfo["role"], usedBy: string[] = []): PackFileInfo => ({ path, role, usedBy, size: 1, hash: "h" });

const FILES: PackFileInfo[] = [
  file("pack.json", "manifest"),
  file("table.scriban", "template", ["unit:table"]),
  file("schema.scriban", "template", ["unit:schema"]),
  file("_columns.scriban", "partial", ["include:table.scriban"]),
  file("partials/_type.scriban", "partial", ["include:_columns.scriban"]),
  file("helpers.js", "script"),
  file("README.md", "other"),
];

describe("file tree", () => {
  it("lists folders first, then files, without pack.json", () => {
    expect(fileTree(FILES).map((r) => `${"  ".repeat(r.depth)}${r.name}${r.folder ? "/" : ""}`)).toEqual([
      "partials/",
      "  _type.scriban",
      "README.md",
      "_columns.scriban",
      "helpers.js",
      "schema.scriban",
      "table.scriban",
    ]);
  });

  it("picks the editor language from the extension", () => {
    expect(["a.scriban", "b.sbn", "helpers.js", "x.json", "README.md", "t.sql", "m.cs", "notes.txt"].map(languageOf)).toEqual([
      "scriban",
      "scriban",
      "javascript",
      "json",
      "markdown",
      "sql",
      "csharp",
      "plaintext",
    ]);
  });
});

describe("units a file feeds", () => {
  const units = ["table", "schema"];
  it("follows includes to the units", () => {
    expect(unitsForFile(FILES, units, "table.scriban")).toEqual(["table"]);
    expect(unitsForFile(FILES, units, "_columns.scriban")).toEqual(["table"]);
    expect(unitsForFile(FILES, units, "partials/_type.scriban")).toEqual(["table"]);
  });
  it("gives every unit for a script and none for an unused file", () => {
    expect(unitsForFile(FILES, units, "helpers.js")).toEqual(["schema", "table"]);
    expect(unitsForFile(FILES, units, "README.md")).toEqual([]);
    expect(unitsForFile(FILES, units, "missing.scriban")).toEqual([]);
  });
  it("survives an include cycle", () => {
    const cyclic = [file("a.scriban", "partial", ["include:b.scriban"]), file("b.scriban", "template", ["include:a.scriban", "unit:u"])];
    expect(unitsForFile(cyclic, ["u"], "a.scriban")).toEqual(["u"]);
  });
});

describe("overlay and diagnostics", () => {
  it("sends only unsaved text, never pack.json", () => {
    expect(overlayOf({})).toBeNull();
    expect(overlayOf({ "a.scriban": { text: "x", base: "x", hash: "1" } })).toBeNull();
    expect(
      overlayOf({
        "b.scriban": { text: "new", base: "old", hash: "1" },
        "pack.json": { text: "{}", base: "{ }", hash: "2" },
        "a.scriban": { text: "y", base: "x", hash: "3" },
      }),
    ).toEqual({ "a.scriban": "y", "b.scriban": "new" });
  });

  it("keeps the diagnostics located in the file", () => {
    const d = (filePath: string | null): Diagnostic => ({
      rule: "MQ6006",
      severity: "error",
      message: "m",
      elementId: null,
      filePath,
      jsonPointer: null,
      line: 1,
      column: 1,
    });
    const all = [
      d(".maquettiste/templates/sql-ddl/table.scriban"),
      d(".maquettiste/templates/sql-ddl/_columns.scriban"),
      d(null),
      d(".maquettiste/templates/other/table.scriban"),
    ];
    expect(diagnosticsFor(all, "sql-ddl", "table.scriban")).toEqual([all[0]]);
  });
});

describe("preview debounce", () => {
  afterEach(() => vi.useRealTimers());

  it("renders once, 300 ms after the last change, with the latest text", async () => {
    vi.useFakeTimers();
    const run = vi.fn((req: string) => Promise.resolve(`rendered ${req}`));
    const results: string[] = [];
    const s = new PreviewScheduler<string, string>(run, (r) => r.ok && results.push(r.value), 300);
    s.schedule("a");
    await vi.advanceTimersByTimeAsync(200);
    s.schedule("ab");
    await vi.advanceTimersByTimeAsync(299);
    expect(run).not.toHaveBeenCalled();
    s.schedule("abc");
    await vi.advanceTimersByTimeAsync(300);
    expect(run).toHaveBeenCalledTimes(1);
    expect(run.mock.calls[0][0]).toBe("abc");
    expect(results).toEqual(["rendered abc"]);
    s.dispose();
  });

  it("aborts the render in flight and drops its late answer", async () => {
    vi.useFakeTimers();
    const pending: { req: string; signal: AbortSignal; resolve(v: string): void }[] = [];
    const run = (req: string, signal: AbortSignal) => new Promise<string>((resolve) => pending.push({ req, signal, resolve }));
    const results: string[] = [];
    const s = new PreviewScheduler<string, string>(run, (r, req) => r.ok && results.push(`${req}:${r.value}`), 300);
    s.schedule("one");
    await vi.advanceTimersByTimeAsync(300);
    s.schedule("two");
    await vi.advanceTimersByTimeAsync(300);
    expect(pending.map((p) => [p.req, p.signal.aborted])).toEqual([
      ["one", true],
      ["two", false],
    ]);
    pending[1].resolve("B");
    pending[0].resolve("A");
    await vi.runAllTimersAsync();
    expect(results).toEqual(["two:B"]);
    s.dispose();
  });

  it("stops after dispose", async () => {
    vi.useFakeTimers();
    const run = vi.fn(() => Promise.resolve("x"));
    const s = new PreviewScheduler<string, string>(run, () => undefined, 300);
    s.schedule("a");
    s.dispose();
    await vi.advanceTimersByTimeAsync(1000);
    expect(run).not.toHaveBeenCalled();
  });
});

describe("class merging", () => {
  it("keeps a colour next to a theme font size (the primary Save button's text)", () => {
    expect(cn("bg-accent text-accent-foreground", "h-6 px-2 text-12")).toBe("bg-accent text-accent-foreground h-6 px-2 text-12");
    expect(cn("text-11", "text-12")).toBe("text-12");
  });
});

describe("line diff for Compare", () => {
  it("marks removed and added lines around the common ones", async () => {
    const { lineDiff } = await import("@/workspaces/generate/templatesModel");
    expect(lineDiff("a\nb\nc", "a\nx\nc").map((l) => l.op + l.text)).toEqual([" a", "-b", "+x", " c"]);
  });
});
