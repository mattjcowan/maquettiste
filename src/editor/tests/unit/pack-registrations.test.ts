// The mock's pack read and template context list what a pack's own scripts register (helpers, selectors, filters, transforms),
// as the server does, so completion offers a pack's helpers beside the built-ins.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";

describe("pack script registrations (mock)", () => {
  it("lists each registration by kind then name, and adds the pack's helpers to the template context", () => {
    const backend = new MockBackend();
    const pack = backend.packs.names()[0]!;
    const unit = backend.model.packs.find((p) => p.name === pack)!.units[0]!.id;
    expect(backend.packs.get(pack)!.registrations).toEqual([]);

    const script = [
      'maquettiste.transform("shape", (e) => e);',
      "maquettiste.helper('zeta_case', (s) => s);",
      'maquettiste.filter("only_named", (e) => !!e.name);',
      'maquettiste.selector("databases", (m) => m.databases);',
      'maquettiste.helper("alpha_case", (s) => s);',
    ].join("\n");
    expect(backend.packs.writeFile(pack, "helpers.js", script, null).status).toBeLessThan(300);

    const registrations = backend.packs.get(pack)!.registrations;
    expect(registrations.map((r) => `${r.kind}:${r.name}`)).toEqual([
      "helper:alpha_case",
      "helper:zeta_case",
      "selector:databases",
      "filter:only_named",
      "transform:shape",
    ]);
    expect(registrations.every((r) => r.declaredIn === "helpers.js")).toBe(true);

    const context = backend.packAuthoring.context(pack, unit)!;
    expect(context.registrations).toEqual(registrations);
    expect(context.helpers).toContain("alpha_case");
    expect(context.helpers).toContain("zeta_case");
    expect(context.helpers).not.toContain("only_named");
    expect(context.helpers).toEqual([...context.helpers].sort());
  });
});
