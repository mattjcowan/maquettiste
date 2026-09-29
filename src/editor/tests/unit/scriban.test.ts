// The Scriban Monarch grammar, run through Monaco's own tokenizer (generation-ui.md 3.3).
import { describe, expect, it, vi } from "vitest";

// Monaco's theme service asks matchMedia at load; jsdom has none.
vi.hoisted(() => {
  window.matchMedia ??= (query: string) =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener() {},
      removeEventListener() {},
      addListener() {},
      removeListener() {},
      dispatchEvent: () => false,
    }) as unknown as MediaQueryList;
});
import * as monaco from "monaco-editor/editor/editor.api";
import { registerScriban, SCRIBAN } from "@/code/scriban";

registerScriban(monaco);

/** Each line as [text, token] pairs, whitespace-only and empty-token pieces dropped. */
function tokens(text: string): [string, string][][] {
  const lines = text.split("\n");
  return monaco.editor
    .tokenize(text, SCRIBAN)
    .map((line, i) =>
      line.map((t, j) => [lines[i].slice(t.offset, line[j + 1]?.offset ?? lines[i].length), t.type] as [string, string]).filter(([s]) => s.trim() !== ""),
    );
}

const types = (text: string, piece: string): string[] =>
  tokens(text)
    .flat()
    .filter(([s]) => s.trim() === piece)
    .map(([, t]) => t);

describe("scriban tokenizer", () => {
  it("leaves text outside the blocks plain", () => {
    const [line] = tokens("CREATE TABLE {{ table.name }} (");
    expect(line[0]).toEqual(["CREATE TABLE ", ""]);
    expect(line.at(-1)).toEqual([" (", ""]);
  });

  it("marks the delimiters, including the trimming forms", () => {
    expect(types("a {{- x -}} b", "{{-")).toEqual(["delimiter.code.scriban"]);
    expect(types("a {{- x -}} b", "-}}")).toEqual(["delimiter.code.scriban"]);
    expect(types("{{~ x ~}}", "~}}")).toEqual(["delimiter.code.scriban"]);
  });

  it("colours keywords, identifiers, strings and numbers", () => {
    const text = '{{ for c in table.columns; if c.nullable == false; "NOT NULL"; end; 42; end }}';
    expect(types(text, "for")).toEqual(["keyword.scriban"]);
    expect(types(text, "in")).toEqual(["keyword.scriban"]);
    expect(types(text, "false")).toEqual(["keyword.scriban"]);
    expect(types(text, "c")).toContain("identifier.scriban");
    expect(
      tokens(text)
        .flat()
        .find(([s]) => s.includes("NOT NULL"))?.[1],
    ).toBe("string.scriban");
    expect(types(text, "42")).toEqual(["number.scriban"]);
    expect(types(text, "==")).toEqual(["operator.scriban"]);
  });

  it("colours a pipe and the function after it, and built-in function objects", () => {
    const text = "{{ name | string.upcase }} {{ x | snake }} {{ string.size name }}";
    expect(types(text, "|")).toEqual(["operator.pipe.scriban", "operator.pipe.scriban"]);
    expect(types(text, "upcase")).toEqual(["function.scriban"]);
    expect(types(text, "snake")).toEqual(["function.scriban"]);
    expect(types(text, "string")).toEqual(["predefined.scriban", "predefined.scriban"]);
  });

  it("colours line and block comments and keeps text after the block plain", () => {
    expect(types("{{ # a note }} tail", "# a note")).toEqual(["comment.scriban"]);
    expect(types("{{ # a note }} tail", "tail")).toEqual([""]);
    const block = tokens("{{ ## one\ntwo ## x }}");
    expect(block[1][0]).toEqual(["two ##", "comment.scriban"]);
    expect(types("{{ ## one\ntwo ## x }}", "x")).toEqual(["identifier.scriban"]);
  });

  it("keeps a raw escape block as text", () => {
    expect(types("{%{ {{ not code }} }%}", "{{ not code }}")).toEqual(["string.raw.scriban"]);
  });

  it("registers once", () => {
    registerScriban(monaco);
    expect(monaco.languages.getLanguages().filter((l) => l.id === SCRIBAN)).toHaveLength(1);
  });
});
