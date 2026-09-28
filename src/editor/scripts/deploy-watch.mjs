#!/usr/bin/env node
// `npm run deploy:watch` (phase2-design.md §5): `vite build --watch --mode host` next to
// `deploy.mjs --watch`, so every rebuild of the SPA (or a change to _functions/) becomes a release on
// the running container within a second or two. Ctrl+C stops both.
import { spawn } from "node:child_process";
import path from "node:path";
import { editorRoot } from "./paths.mjs";

const vite = path.join(editorRoot, "node_modules/vite/bin/vite.js");
const children = [
  spawn(process.execPath, [vite, "build", "--watch", "--mode", "host"], { cwd: editorRoot, stdio: "inherit" }),
  spawn(process.execPath, [path.join(editorRoot, "scripts/deploy.mjs"), "--watch", ...process.argv.slice(2)], { cwd: editorRoot, stdio: "inherit" }),
];
const stop = (code) => {
  for (const child of children) if (child.exitCode === null) child.kill("SIGTERM");
  process.exit(code);
};
for (const child of children) child.on("exit", (code) => stop(code ?? 0));
process.on("SIGINT", () => stop(0));
process.on("SIGTERM", () => stop(0));
