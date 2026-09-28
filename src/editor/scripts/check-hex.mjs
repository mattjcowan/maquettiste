// Fails when a color hex literal appears in src/ outside src/design/tokens.css (phase2-design.md 4.7).
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "../src");
const allowed = new Set([path.join(root, "design/tokens.css")]);
const skip = [path.join(root, "mocks/fixture"), path.join(root, "mocks/recorded"), path.join(root, "mocks/openapi.json"), path.join(root, "api/schema.d.ts")];
const pattern = /(?<![\w&/-])#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b/g;
const found = [];

function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (skip.some((s) => full.startsWith(s))) continue;
    if (entry.isDirectory()) walk(full);
    else if (/\.(css|tsx?)$/.test(entry.name) && !allowed.has(full)) {
      fs.readFileSync(full, "utf8")
        .split("\n")
        .forEach((line, i) => {
          if (/^\s*(\/\/|\*)/.test(line)) return;
          for (const m of line.matchAll(pattern)) found.push(`${path.relative(process.cwd(), full)}:${i + 1}: ${m[0]}`);
        });
    }
  }
}

walk(root);
if (found.length) {
  console.error(`Color literals outside tokens.css:\n${found.join("\n")}`);
  process.exit(1);
}
console.log("check-hex: no color literals outside tokens.css.");
