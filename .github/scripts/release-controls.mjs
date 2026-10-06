import fs from "node:fs";
import path from "node:path";
import { Readable } from "node:stream";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const semanticVersion = String.raw`(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?`;
const releaseTagPattern = new RegExp(`^v(${semanticVersion})$`, "u");
const expectedAssets = [
  "dot-orbit-linux-x64.tar.gz",
  "dot-orbit-osx-arm64.tar.gz",
  "dot-orbit-osx-x64.tar.gz",
  "dot-orbit-win-x64.zip",
];

export function versionFromTag(tag) {
  return releaseTagPattern.exec(tag)?.[1] ?? null;
}

export function assertWorkflowPolicy(contents) {
  const required = [
    "branches: [main]",
    "- \"v*.*.*\"",
    "needs: admit-source",
    "if: needs.admit-source.outputs.upload == 'true'",
    "path: artifacts/packages/${{ matrix.archive }}",
    "archive: false",
    "DOTORBIT_PACKAGE_SMOKE_VERSION: ${{ needs.admit-source.outputs.version }}",
    "if: needs.admit-source.outputs.draft-release == 'true'",
    "permissions:\n      contents: write",
    "actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1",
    "pattern: dot-orbit-*",
    "skip-decompress: true",
    "run: node .github/scripts/release-controls.mjs draft-release",
  ];
  for (const value of required) {
    if (!contents.includes(value)) throw new Error(`release-workflow-policy-missing:${value}`);
  }
  if ((contents.match(/uses: actions\/upload-artifact@/gu) ?? []).length !== 1) {
    throw new Error("release-workflow-must-have-one-upload-step");
  }
  if ((contents.match(/contents: write/gu) ?? []).length !== 1) {
    throw new Error("release-workflow-must-have-one-write-permission");
  }
  if (/github\.event_name == 'workflow_dispatch'/u.test(contents)) {
    throw new Error("manual-runs-must-not-upload");
  }
}

export function classifySource(source, isOnMain = defaultIsOnMain) {
  assertSource(source);
  const shortSha = source.sha.slice(0, 12);
  const ciVersion = `0.0.0-ci.${source.runNumber}+${shortSha}`;

  if (source.eventName !== "push") {
    return { upload: false, draftRelease: false, version: ciVersion, tag: "" };
  }

  if (source.ref === "refs/heads/main") {
    return {
      upload: true,
      draftRelease: false,
      version: `0.0.0-main.${source.runNumber}+${shortSha}`,
      tag: "",
    };
  }

  if (!source.ref.startsWith("refs/tags/")) {
    throw new Error("unsupported-push-ref");
  }

  const tag = source.ref.slice("refs/tags/".length);
  const version = versionFromTag(tag);
  if (version === null) throw new Error("tag-is-not-v-prefixed-semver");
  if (!isOnMain(source.sha)) throw new Error("tagged-commit-is-not-on-main");
  return { upload: true, draftRelease: true, version, tag };
}

export async function prepareDraftRelease(options, api = createGitHubApi(options)) {
  const version = versionFromTag(options.tag);
  if (version === null) throw new Error("tag-is-not-v-prefixed-semver");
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u.test(options.repository)) {
    throw new Error("invalid-repository");
  }

  const assets = releaseAssets(options.assetRoot);
  const resolvedSha = await api.resolveTagCommit(options.tag);
  if (resolvedSha !== options.sha) throw new Error("release-tag-commit-mismatch");
  const existing = await api.getReleaseByTag(options.tag);
  if (existing !== null && existing.draft !== true) throw new Error("published-release-exists");
  let currentAssets = [];
  if (existing !== null) {
    if (!Number.isSafeInteger(existing.id)) throw new Error("release-response-invalid");
    currentAssets = await api.listAssets(existing.id);
    const names = Array.isArray(currentAssets) ? currentAssets.map(asset => asset.name) : [];
    if (!Array.isArray(currentAssets)
        || currentAssets.some(asset =>
          !Number.isSafeInteger(asset.id) || !expectedAssets.includes(asset.name))
        || new Set(names).size !== names.length) {
      throw new Error("existing-release-assets-not-allowlisted");
    }
  }

  const notes = await api.generateNotes(options.tag);
  if (typeof notes.name !== "string" || notes.name.length === 0 || typeof notes.body !== "string") {
    throw new Error("release-notes-response-invalid");
  }

  const prerelease = version.split("+", 1)[0].includes("-");
  const release = {
    tagName: options.tag,
    name: notes.name,
    body: notes.body,
    draft: true,
    prerelease,
  };
  let releaseId;
  if (existing === null) {
    const created = await api.createRelease(release);
    releaseId = created.id;
  } else {
    await api.updateRelease(existing.id, release);
    releaseId = existing.id;
  }

  if (!Number.isSafeInteger(releaseId)) throw new Error("release-response-invalid");
  for (const asset of currentAssets) {
    await api.deleteAsset(asset.id);
  }
  for (const asset of assets) await api.uploadAsset(releaseId, asset);
  return existing === null ? "created" : "updated";
}

function assertSource(source) {
  if (!/^[0-9a-f]{40}$/u.test(source.sha)) throw new Error("invalid-commit-sha");
  if (!/^[1-9]\d*$/u.test(source.runNumber)) throw new Error("invalid-run-number");
  if (typeof source.eventName !== "string" || typeof source.ref !== "string") {
    throw new Error("invalid-source");
  }
}

function defaultIsOnMain(sha) {
  const result = defaultRun("git", ["merge-base", "--is-ancestor", sha, "origin/main"]);
  if (result.status === 0) return true;
  if (result.status === 1) return false;
  throw commandError("main-ancestry-check-failed", result);
}

function releaseAssets(assetRoot) {
  const entries = fs.readdirSync(assetRoot, { withFileTypes: true });
  const actual = entries.map(entry => entry.name).sort();
  if (entries.some(entry => !entry.isFile())
      || actual.length !== expectedAssets.length
      || actual.some((name, index) => name !== expectedAssets[index])) {
    throw new Error("release-assets-not-allowlisted");
  }
  return actual.map(name => path.join(assetRoot, name));
}

function commandError(reason, result) {
  const detail = result.stderr.trim().split("\n").at(-1);
  return new Error(detail ? `${reason}:${detail}` : reason);
}

function defaultRun(command, arguments_) {
  const result = spawnSync(command, arguments_, { encoding: "utf8" });
  return {
    status: result.status ?? 1,
    stdout: result.stdout ?? "",
    stderr: result.stderr ?? result.error?.message ?? "",
  };
}

function createGitHubApi(options) {
  const token = process.env.GITHUB_TOKEN;
  if (!token) throw new Error("github-token-is-required");
  const apiRoot = `https://api.github.com/repos/${options.repository}`;

  async function request(url, requestOptions = {}, allowNotFound = false) {
    const response = await fetch(url, {
      ...requestOptions,
      headers: {
        Accept: "application/vnd.github+json",
        Authorization: `Bearer ${token}`,
        "X-GitHub-Api-Version": "2022-11-28",
        ...requestOptions.headers,
      },
    });
    if (allowNotFound && response.status === 404) return null;
    if (!response.ok) throw new Error(`github-api-request-failed:${response.status}`);
    if (response.status === 204) return null;
    return response.json();
  }

  return {
    async resolveTagCommit(tag) {
      let object = (await request(`${apiRoot}/git/ref/tags/${encodeURIComponent(tag)}`)).object;
      for (let depth = 0; object?.type === "tag" && depth < 8; depth++) {
        object = (await request(`${apiRoot}/git/tags/${object.sha}`)).object;
      }
      if (object?.type !== "commit" || !/^[0-9a-f]{40}$/u.test(object.sha)) {
        throw new Error("release-tag-does-not-resolve-to-commit");
      }
      return object.sha;
    },
    getReleaseByTag: tag => request(
      `${apiRoot}/releases/tags/${encodeURIComponent(tag)}`,
      {},
      true),
    generateNotes: tag => request(`${apiRoot}/releases/generate-notes`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ tag_name: tag }),
    }),
    createRelease: release => request(`${apiRoot}/releases`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(releasePayload(release)),
    }),
    updateRelease: (id, release) => request(`${apiRoot}/releases/${id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(releasePayload(release)),
    }),
    listAssets: id => request(`${apiRoot}/releases/${id}/assets?per_page=100`),
    deleteAsset: id => request(`${apiRoot}/releases/assets/${id}`, { method: "DELETE" }),
    async uploadAsset(id, assetPath) {
      const stat = fs.statSync(assetPath);
      const name = path.basename(assetPath);
      await request(
        `https://uploads.github.com/repos/${options.repository}/releases/${id}/assets?name=${encodeURIComponent(name)}`,
        {
          method: "POST",
          headers: {
            "Content-Length": String(stat.size),
            "Content-Type": name.endsWith(".zip") ? "application/zip" : "application/gzip",
          },
          body: Readable.toWeb(fs.createReadStream(assetPath)),
          duplex: "half",
        });
    },
  };
}

function releasePayload(release) {
  return {
    tag_name: release.tagName,
    name: release.name,
    body: release.body,
    draft: release.draft,
    prerelease: release.prerelease,
  };
}

function writeOutputs(outputPath, classification) {
  fs.appendFileSync(
    outputPath,
    [
      `upload=${classification.upload}`,
      `draft-release=${classification.draftRelease}`,
      `version=${classification.version}`,
      "",
    ].join("\n"));
}

async function runCommand(arguments_) {
  const [command] = arguments_;
  if (command === "classify") {
    const classification = classifySource({
      eventName: process.env.GITHUB_EVENT_NAME,
      ref: process.env.GITHUB_REF,
      sha: process.env.GITHUB_SHA,
      runNumber: process.env.GITHUB_RUN_NUMBER,
    });
    if (!process.env.GITHUB_OUTPUT) throw new Error("github-output-is-required");
    writeOutputs(process.env.GITHUB_OUTPUT, classification);
    console.log(
      `release-controls: upload=${classification.upload} draft-release=${classification.draftRelease} version=${classification.version}`);
    return;
  }
  if (command === "draft-release") {
    const result = await prepareDraftRelease({
      tag: process.env.GITHUB_REF_NAME,
      repository: process.env.GITHUB_REPOSITORY,
      assetRoot: process.env.RELEASE_ASSET_ROOT,
      sha: process.env.GITHUB_SHA,
    });
    console.log(`release-controls: draft-release=${result}`);
    return;
  }
  if (command === "workflow-policy") {
    const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
    assertWorkflowPolicy(fs.readFileSync(
      path.join(repositoryRoot, ".github/workflows/release-packages.yml"),
      "utf8"));
    console.log("release-controls: workflow-policy=passed");
    return;
  }
  throw new Error("Usage: node .github/scripts/release-controls.mjs <classify|draft-release|workflow-policy>");
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    await runCommand(process.argv.slice(2));
  } catch (error) {
    console.error(`release-controls: result=failed reason=${error.message}`);
    process.exitCode = 1;
  }
}
