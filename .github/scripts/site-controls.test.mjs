import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { validateSite } from "./site-controls.mjs";

function createSite(overrides = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "dot-orbit-site-"));
  const files = {
    ".nojekyll": "",
    "CNAME": "dot-orbit.app\n",
    "index.html": '<!doctype html><html><head><link href="shared.css"></head><body><a href="https://github.com/nullabletype/dot-orbit/releases">Download</a><a href="#product">Product</a><img src="orbit-mark.svg"><section id="product"></section></body></html>',
    "orbit-mark.svg": "<svg></svg>",
    "screenshots/ai-light-current.png": "png",
    "screenshots/categories-light-current.png": "png",
    "screenshots/projects-dark-current.png": "png",
    "screenshots/settings-dark-current.png": "png",
    "screenshots/today-dark-current.png": "png",
    "shared.css": "body {}",
    ...overrides,
  };
  for (const [name, contents] of Object.entries(files)) {
    if (contents === null) continue;
    const target = path.join(root, name);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, contents);
  }
  return root;
}

test("accepts the production site contract", t => {
  const root = createSite();
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.doesNotThrow(() => validateSite(root));
});

test("rejects a missing local asset", t => {
  const root = createSite({ "orbit-mark.svg": null });
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.throws(() => validateSite(root), /missing required file: orbit-mark\.svg.*does not exist/u);
});

test("rejects a local reference outside the deployable site", t => {
  const root = createSite({
    "index.html": '<!doctype html><html><head></head><body><a href="https://github.com/nullabletype/dot-orbit/releases">Download</a><img src="../private.png"></body></html>',
  });
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.throws(() => validateSite(root), /escapes the site root/u);
});

test("rejects an incorrect custom domain or preview URL", t => {
  const root = createSite({
    "CNAME": "example.com\n",
    "index.html": '<!doctype html><html><head></head><body><a href="https://github.com/nullabletype/dot-orbit/releases">Download</a><a href="http://localhost:4173/?preview=4">Preview</a></body></html>',
  });
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.throws(() => validateSite(root), /CNAME must contain only dot-orbit\.app.*local preview URLs/u);
});

test("rejects a file outside the explicit Pages bundle allowlist", t => {
  const root = createSite({ "notes.txt": "not for deployment" });
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.throws(() => validateSite(root), /unapproved file in site bundle: notes\.txt/u);
});

test("rejects a broken local fragment", t => {
  const root = createSite({
    "index.html": '<!doctype html><html><head></head><body><a href="https://github.com/nullabletype/dot-orbit/releases">Download</a><a href="#missing">Missing</a></body></html>',
  });
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.throws(() => validateSite(root), /local fragment does not exist in index\.html: #missing/u);
});

test("rejects a missing asset referenced by CSS", t => {
  const root = createSite({ "shared.css": "body { background: url('missing.png'); }" });
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.throws(() => validateSite(root), /local reference does not exist: missing\.png/u);
});
