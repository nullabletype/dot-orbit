import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const directory = new URL("./", import.meta.url);

test("manifest, lockfile, action, and review match the supplied supported baseline", async () => {
  const [manifest, lockfile, action, catalog, review] = await Promise.all([
    readFile(new URL("package.json", directory), "utf8").then(JSON.parse),
    readFile(new URL("package-lock.json", directory), "utf8").then(JSON.parse),
    readFile(new URL("action.yml", directory), "utf8"),
    readFile(new URL("support-catalog.json", directory), "utf8").then(JSON.parse),
    readFile(new URL("review.md", directory), "utf8"),
  ]);
  const version = catalog.package.supportedVersion;
  assert.equal(manifest.dependencies[catalog.package.name], version);
  assert.equal(lockfile.packages[""].dependencies[catalog.package.name], version);
  assert.equal(lockfile.packages[`node_modules/${catalog.package.name}`].version, version);
  assert.match(action, new RegExp(`uses: ${catalog.action.name}@${catalog.action.supportedCommit}`));
  assert.match(review, new RegExp(catalog.checkedAt));
  assert.match(review, new RegExp(catalog.nextReview));
  assert.match(review, /supported/i);
});
