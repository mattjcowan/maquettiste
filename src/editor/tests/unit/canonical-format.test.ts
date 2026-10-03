// Rewriting model files in canonical form (problems/canonical.ts, POST /api/model/format): the Settings count, the notice, and
// the mock's legacy project whose maquettiste.json still sets the retired `commit` flag.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { formatNotice, hasCanonicalFix, nonCanonicalFiles } from "@/problems/canonical";

const SETTINGS = ".maquettiste/maquettiste.json";

describe("canonical form", () => {
  it("offers the fix on MQ1003 and MQ1010 findings located in a file, and counts each file once", () => {
    const d = (rule: string, filePath: string | null) => ({ rule, filePath });
    expect(hasCanonicalFix(d("MQ1003", SETTINGS))).toBe(true);
    expect(hasCanonicalFix(d("MQ1010", SETTINGS))).toBe(true);
    expect(hasCanonicalFix(d("MQ1003", null))).toBe(false);
    expect(hasCanonicalFix(d("MQ1002", SETTINGS))).toBe(false);
    expect(nonCanonicalFiles([d("MQ1010", SETTINGS), d("MQ1003", ".maquettiste/model/a.json"), d("MQ1003", SETTINGS)])).toEqual([
      ".maquettiste/maquettiste.json",
      ".maquettiste/model/a.json",
    ]);
  });
  it("says what was rewritten and what was left", () => {
    expect(formatNotice({ formatted: [SETTINGS], skipped: [] })).toEqual({ message: `Rewrote ${SETTINGS} in canonical form.`, level: "info" });
    expect(formatNotice({ formatted: ["a", "b"], skipped: [] }).message).toBe("Rewrote 2 model files in canonical form.");
    expect(formatNotice({ formatted: [], skipped: [] }).message).toBe("Every model file was already in canonical form.");
    expect(formatNotice({ formatted: [], skipped: ["x.json"] })).toEqual({
      message: "x.json was left as it is: fix its other problems first (it does not pass its schema).",
      level: "error",
    });
  });
  it("rewrites the legacy settings without commit, leaves canonical files alone and refuses a path outside the model", () => {
    const backend = new MockBackend({ scenarios: ["legacy"] });
    const rules = () => backend.model.validate().diagnostics.filter((x) => x.rule === "MQ1003" || x.rule === "MQ1010");
    expect(rules().map((x) => `${x.rule} ${x.filePath}`)).toContain(`MQ1010 ${SETTINGS}`);
    expect(rules().filter((x) => x.rule === "MQ1003")).toHaveLength(2);
    const before = backend.model.settingsHash;

    expect(backend.model.format(["src/Generated/x.cs"]).refused).toEqual(["src/Generated/x.cs"]);
    const result = backend.model.format([SETTINGS]);
    expect(result.formatted).toEqual([SETTINGS]);
    expect(JSON.stringify(backend.model.settingsJson)).not.toContain("commit");
    expect(backend.model.settingsHash).not.toBe(before);
    expect(rules().map((x) => x.rule)).toEqual(["MQ1003"]);

    // Every file: the element is rewritten, the settings (already canonical now) are not.
    expect(backend.model.format().formatted).toHaveLength(1);
    expect(backend.model.format().formatted).toEqual([]);
    expect(rules()).toEqual([]);
  });
});
