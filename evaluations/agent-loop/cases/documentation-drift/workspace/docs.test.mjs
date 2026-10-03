import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { help } from "./cli.mjs";

test("operator documentation matches the executable option and default", async () => {
  const readme = await readFile(new URL("README.md", import.meta.url), "utf8");
  assert.match(help(), /--output <path>/);
  assert.match(readme, /--output \.\/review\.json/);
  assert.match(readme, /default: `\.\/orbit-report\.json`/);
  assert.doesNotMatch(readme, /--export/);
});
