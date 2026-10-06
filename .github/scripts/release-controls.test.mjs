import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  assertWorkflowPolicy,
  classifySource,
  prepareDraftRelease,
  versionFromTag,
} from "./release-controls.mjs";

const sha = "0123456789abcdef0123456789abcdef01234567";
const repositoryRoot = path.resolve(path.dirname(new URL(import.meta.url).pathname), "../..");

test("release tags accept the complete SemVer 2.0 form with a v prefix", () => {
  for (const [tag, version] of [
    ["v0.0.0", "0.0.0"],
    ["v1.2.3", "1.2.3"],
    ["v1.2.3-rc.1", "1.2.3-rc.1"],
    ["v1.2.3-alpha-beta+build.0042", "1.2.3-alpha-beta+build.0042"],
  ]) {
    assert.equal(versionFromTag(tag), version);
  }
});

test("release tags reject missing prefixes and non-canonical SemVer", () => {
  for (const tag of [
    "1.2.3",
    "v1.2",
    "v01.2.3",
    "v1.02.3",
    "v1.2.03",
    "v1.2.3-01",
    "v1.2.3+",
    "v1.2.3.4",
    "v1.2.3/feature",
  ]) {
    assert.equal(versionFromTag(tag), null, tag);
  }
});

test("pull requests and manual runs get traceable CI versions without upload authority", () => {
  for (const eventName of ["pull_request", "workflow_dispatch"]) {
    assert.deepEqual(
      classifySource({ eventName, ref: "refs/heads/feature", sha, runNumber: "42" }),
      {
        upload: false,
        draftRelease: false,
        version: "0.0.0-ci.42+0123456789ab",
        tag: "",
      });
  }
});

test("main pushes upload without creating a draft release", () => {
  assert.deepEqual(
    classifySource({ eventName: "push", ref: "refs/heads/main", sha, runNumber: "42" }),
    {
      upload: true,
      draftRelease: false,
      version: "0.0.0-main.42+0123456789ab",
      tag: "",
    });
});

test("SemVer tag pushes upload and draft only when their commit is on main", () => {
  assert.deepEqual(
    classifySource(
      { eventName: "push", ref: "refs/tags/v2.3.4-rc.1", sha, runNumber: "42" },
      candidate => candidate === sha),
    {
      upload: true,
      draftRelease: true,
      version: "2.3.4-rc.1",
      tag: "v2.3.4-rc.1",
    });
  assert.throws(
    () => classifySource(
      { eventName: "push", ref: "refs/tags/v2.3.4", sha, runNumber: "42" },
      () => false),
    /tagged-commit-is-not-on-main/u);
});

test("invalid tag candidates and feature-branch pushes fail closed", () => {
  assert.throws(
    () => classifySource(
      { eventName: "push", ref: "refs/tags/v2.3", sha, runNumber: "42" },
      () => true),
    /tag-is-not-v-prefixed-semver/u);
  assert.throws(
    () => classifySource(
      { eventName: "push", ref: "refs/heads/feature", sha, runNumber: "42" },
      () => true),
    /unsupported-push-ref/u);
});

test("release workflow retains the bounded upload and draft permissions", () => {
  const workflow = fs.readFileSync(
    path.join(repositoryRoot, ".github/workflows/release-packages.yml"),
    "utf8");

  assert.doesNotThrow(() => assertWorkflowPolicy(workflow));
  assert.throws(
    () => assertWorkflowPolicy(workflow.replace(
      "if: needs.admit-source.outputs.upload == 'true'",
      "if: github.event_name == 'workflow_dispatch'")),
    /release-workflow-policy-missing/u);
  assert.throws(
    () => assertWorkflowPolicy(workflow.replace("contents: read", "contents: write")),
    /one-write-permission/u);
});

test("new releases are created as drafts with generated notes and allowlisted assets", async t => {
  const fixture = createAssetFixture(t);
  const calls = [];
  const result = await prepareDraftRelease(
    { tag: "v1.2.3", repository: "nullabletype/dot-orbit", assetRoot: fixture, sha },
    mockGitHub(calls, { existing: null }));

  assert.equal(result, "created");
  const create = calls.find(call => call.name === "createRelease");
  assert.ok(create);
  assert.equal(create.release.draft, true);
  assert.equal(create.release.prerelease, false);
  assert.equal(calls.filter(call => call.name === "uploadAsset").length, 4);
});

test("reruns refresh an existing draft and mark prerelease tags", async t => {
  const fixture = createAssetFixture(t);
  const calls = [];
  const result = await prepareDraftRelease(
    { tag: "v1.2.3-rc.1", repository: "nullabletype/dot-orbit", assetRoot: fixture, sha },
    mockGitHub(calls, {
      existing: { id: 73, draft: true },
      assets: [{ id: 11, name: "dot-orbit-win-x64.zip" }],
    }));

  assert.equal(result, "updated");
  const update = calls.find(call => call.name === "updateRelease");
  assert.equal(update?.release.prerelease, true);
  assert.deepEqual(
    calls.filter(call => call.name === "deleteAsset").map(call => call.id),
    [11]);
  assert.equal(calls.filter(call => call.name === "uploadAsset").length, 4);
});

test("reruns reject an unknown existing draft asset", async t => {
  const fixture = createAssetFixture(t);
  const calls = [];

  await assert.rejects(
    prepareDraftRelease(
      { tag: "v1.2.3", repository: "nullabletype/dot-orbit", assetRoot: fixture, sha },
      mockGitHub(calls, {
        existing: { id: 73, draft: true },
        assets: [{ id: 12, name: "notes.txt" }],
      })),
    /existing-release-assets-not-allowlisted/u);
  assert.equal(calls.some(call => [
    "generateNotes",
    "updateRelease",
    "deleteAsset",
    "uploadAsset",
  ].includes(call.name)), false);
});

test("automation refuses to modify an existing published release", async t => {
  const fixture = createAssetFixture(t);
  const calls = [];

  await assert.rejects(
    prepareDraftRelease(
      { tag: "v1.2.3", repository: "nullabletype/dot-orbit", assetRoot: fixture, sha },
      mockGitHub(calls, { existing: { id: 73, draft: false } })),
    /published-release-exists/u);
  assert.equal(calls.some(call => call.name === "generateNotes"), false);
});

test("draft release creation rejects missing or additional payloads", async t => {
  const fixture = createAssetFixture(t);
  fs.writeFileSync(path.join(fixture, "test-results.trx"), "not allowed");

  await assert.rejects(
    prepareDraftRelease(
      { tag: "v1.2.3", repository: "nullabletype/dot-orbit", assetRoot: fixture, sha },
      mockGitHub([], { existing: null })),
    /release-assets-not-allowlisted/u);
});

function createAssetFixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "dot-orbit-release-controls-"));
  t.after(() => fs.rmSync(directory, { recursive: true }));
  for (const name of [
    "dot-orbit-linux-x64.tar.gz",
    "dot-orbit-osx-arm64.tar.gz",
    "dot-orbit-osx-x64.tar.gz",
    "dot-orbit-win-x64.zip",
  ]) {
    fs.writeFileSync(path.join(directory, name), name);
  }
  return directory;
}

function mockGitHub(calls, options) {
  return {
    async resolveTagCommit(tag) {
      calls.push({ name: "resolveTagCommit", tag });
      return sha;
    },
    async getReleaseByTag(tag) {
      calls.push({ name: "getReleaseByTag", tag });
      return options.existing;
    },
    async generateNotes(tag) {
      calls.push({ name: "generateNotes", tag });
      return { name: "1.2.3", body: "Changes since the previous release" };
    },
    async createRelease(release) {
      calls.push({ name: "createRelease", release });
      return { id: 73 };
    },
    async updateRelease(id, release) {
      calls.push({ name: "updateRelease", id, release });
    },
    async listAssets(id) {
      calls.push({ name: "listAssets", id });
      return options.assets ?? [];
    },
    async deleteAsset(id) {
      calls.push({ name: "deleteAsset", id });
    },
    async uploadAsset(id, asset) {
      calls.push({ name: "uploadAsset", id, asset });
    },
  };
}
