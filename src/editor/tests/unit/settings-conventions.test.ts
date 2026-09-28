// The conventions draft after a settings conflict: only the keys changed here move onto the disk version.
import { describe, expect, it } from "vitest";
import type { SettingsJson } from "@/api/types";
import { rebaseConventions } from "@/workspaces/settings/conventions";

const s = (v: unknown) => v as SettingsJson;

describe("rebaseConventions", () => {
  it("keeps the other writer's changes and re-applies only ours", () => {
    const base = s({ name: "billing", conventions: { tableCase: "snake", pluralize: true }, databases: { main: { schema: "dbo" } } });
    const mine = s({ name: "billing", conventions: { tableCase: "pascal", pluralize: true }, databases: { main: {} } });
    const disk = s({ name: "billing2", conventions: { tableCase: "snake", pluralize: false }, databases: { main: { schema: "dbo", idType: "guid" } } });
    expect(rebaseConventions(base, mine, disk)).toEqual({
      name: "billing2",
      conventions: { tableCase: "pascal", pluralize: false },
      databases: { main: { idType: "guid" } },
    });
  });

  it("drops empty groups", () => {
    const base = s({ conventions: { tableCase: "snake" } });
    const mine = s({});
    const disk = s({ conventions: { tableCase: "snake" } });
    expect(rebaseConventions(base, mine, disk)).toEqual({});
  });
});
