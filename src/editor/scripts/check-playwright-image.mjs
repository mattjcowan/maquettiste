// The browser jobs of the workflows run in Microsoft's Playwright image (mcr.microsoft.com/playwright:v<version>-noble), whose
// browsers match one Playwright version exactly. This check fails when an image a workflow names is not the version of the
// editor's @playwright/test in package-lock.json, so upgrading Playwright without the image (or the reverse) stops in CI.
//   node scripts/check-playwright-image.mjs
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const editorRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const workflows = path.resolve(editorRoot, "../../.github/workflows");

const lock = JSON.parse(readFileSync(path.join(editorRoot, "package-lock.json"), "utf8"));
const version = lock.packages?.["node_modules/@playwright/test"]?.version;
if (!version) {
  console.error("check-playwright-image: package-lock.json has no node_modules/@playwright/test");
  process.exit(1);
}

const wrong = [];
let seen = 0;
for (const file of readdirSync(workflows)
  .filter((f) => /\.ya?ml$/.test(f))
  .sort()) {
  const text = readFileSync(path.join(workflows, file), "utf8");
  for (const m of text.matchAll(/mcr\.microsoft\.com\/playwright:v([0-9][^\s"'-]*)/g)) {
    seen += 1;
    if (m[1] !== version) wrong.push(`${file}: v${m[1]}`);
  }
}
if (seen === 0) {
  console.error("check-playwright-image: no workflow names mcr.microsoft.com/playwright");
  process.exit(1);
}
if (wrong.length) {
  console.error(`check-playwright-image: @playwright/test is ${version}, but these workflows name another image:\n  ${wrong.join("\n  ")}`);
  process.exit(1);
}
console.log(`check-playwright-image: ${seen} image references, all v${version}.`);
