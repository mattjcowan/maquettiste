// The release and the workspace under the project name in the top bar (`v0.5.3 · feature/billing`), so two editors running
// side by side on different ports can be told apart; the tooltip carries the detail.
import type { ProjectInfo } from "@/api/types";

type Versions = Pick<ProjectInfo, "productVersion" | "build" | "engineVersion" | "formatVersion" | "workspace" | "branch" | "worktree" | "repository" | "git">;

/**
 * `v<release> · <workspace> (<n>)`: the release, the workspace, the branch when it is not the workspace already, and in
 * parentheses the count of model files changed since the last commit when the server reads git (the tooltip spells it out).
 * Empty when the server does not say.
 */
export function productLine(project: Partial<Versions> | undefined): string {
  if (!project?.productVersion) return "";
  const parts = [`v${project.productVersion}`];
  const workspace = project.workspace?.trim();
  if (workspace) parts.push(workspace);
  const branch = project.branch ?? project.git?.branch ?? null;
  if (branch && branch !== workspace) parts.push(branch);
  const line = parts.join(" · ");
  return project.git ? `${line} (${project.git.changedModelFiles})` : line;
}

/** One line per fact: the build, the engine contract and model format, then the branch, worktree and repository folder when known. */
export function productTooltip(project: Partial<Versions> | undefined): string {
  if (!project?.productVersion) return "";
  const lines = [
    `Maquettiste ${project.productVersion}`,
    `Build ${project.build ?? project.productVersion}`,
    `Engine contract ${project.engineVersion ?? "?"}, model format ${project.formatVersion ?? "?"}`,
  ];
  if (project.workspace) lines.push(`Workspace ${project.workspace}`);
  if (project.branch) lines.push(`Branch ${project.branch}`);
  if (project.worktree) lines.push(`Worktree ${project.worktree}`);
  if (project.repository) lines.push(`Repository folder ${project.repository}`);
  if (project.git) lines.push(`${project.git.changedModelFiles} model files changed since the last commit`);
  return lines.join("\n");
}
