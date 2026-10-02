// The release and the workspace under the project name (the customer's request): `v0.5.3 · <workspace>`, the detail in the tooltip.
import { describe, expect, it } from "vitest";
import { productLine, productTooltip } from "@/app/productLine";

const base = { productVersion: "0.5.3", build: "0.5.3-b14a8131cfe39", engineVersion: "1.0.0", formatVersion: 1 };

describe("product line", () => {
  it("shows the release and the workspace, or the release alone", () => {
    expect(productLine({ ...base, workspace: "feature/billing" })).toBe("v0.5.3 · feature/billing");
    expect(productLine({ ...base, workspace: null })).toBe("v0.5.3");
    expect(productLine({ ...base, workspace: "  " })).toBe("v0.5.3");
    expect(productLine(undefined)).toBe("");
    expect(productLine({ engineVersion: "1.0.0" })).toBe(""); // a server that does not say
  });

  it("folds the git status in: the branch once, and the changed count", () => {
    const git = { branch: "feature/billing", head: "abc1234", changedModelFiles: 2 };
    expect(productLine({ ...base, workspace: "feature/billing", branch: "feature/billing", git })).toBe("v0.5.3 · feature/billing (2)");
    expect(productLine({ ...base, workspace: "demo", branch: "feature/billing", git })).toBe("v0.5.3 · demo · feature/billing (2)");
    expect(productLine({ ...base, workspace: null, branch: null, git: { ...git, branch: null } })).toBe("v0.5.3 (2)");
    expect(productTooltip({ ...base, git })).toContain("2 model files changed since the last commit");
  });

  it("puts the build, the engine contract and model format, the branch, worktree and repository folder in the tooltip", () => {
    expect(productTooltip({ ...base, workspace: "demo", branch: "feature/billing", worktree: "billing", repository: "maquettiste" }).split("\n")).toEqual([
      "Maquettiste 0.5.3",
      "Build 0.5.3-b14a8131cfe39",
      "Engine contract 1.0.0, model format 1",
      "Workspace demo",
      "Branch feature/billing",
      "Worktree billing",
      "Repository folder maquettiste",
    ]);
    expect(productTooltip({ ...base, workspace: null, branch: null, worktree: null, repository: null }).split("\n")).toHaveLength(3);
  });
});
