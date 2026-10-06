import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const expectedNuGetConfig = `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <auditSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </auditSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
`;

export function assertNuGetPolicy(contents) {
  if (contents.replaceAll("\r\n", "\n") !== expectedNuGetConfig) {
    throw new Error("NuGet.Config must contain only the approved nuget.org package and audit source policy.");
  }
}

export function assertSupportReviewCurrent(contents, today = new Date()) {
  const matches = [...contents.matchAll(/^Review by: (\d{4}-\d{2}-\d{2})$/gmu)];
  if (matches.length !== 1) throw new Error("The dependency baseline must contain exactly one Review by date.");

  const date = matches[0][1];
  const reviewBy = new Date(`${date}T23:59:59Z`);
  if (Number.isNaN(reviewBy.valueOf()) || reviewBy.toISOString().slice(0, 10) !== date) {
    throw new Error(`The dependency support baseline has an invalid Review by date: ${date}.`);
  }
  if (today > reviewBy) {
    throw new Error(`The dependency support baseline expired on ${date}; re-check upstream support and security evidence.`);
  }
}

export function latestStable(versions) {
  return versions.find(version => !version.includes("-"));
}

export function latestActionRelease(releases) {
  return releases
    .map(release => ({ release, match: /^v(\d+)\.(\d+)\.(\d+)$/u.exec(release.tag_name) }))
    .filter(candidate => candidate.match !== null)
    .sort((left, right) => {
      for (let index = 1; index <= 3; index++) {
        const difference = Number(right.match[index]) - Number(left.match[index]);
        if (difference !== 0) return difference;
      }
      return 0;
    })[0]?.release.tag_name;
}

export function nodeSupportPhase(schedule, today = new Date()) {
  if (today >= new Date(`${schedule.end}T00:00:00Z`)) return "end-of-life";
  if (today >= new Date(`${schedule.maintenance}T00:00:00Z`)) return "maintenance";
  if (today >= new Date(`${schedule.lts}T00:00:00Z`)) return "active-lts";
  return "current";
}

export function assertActiveRunnerLabels(catalogue, labels) {
  const start = catalogue.indexOf("## Available Images");
  const end = catalogue.indexOf("### Label scheme", start);
  if (start < 0 || end < 0) throw new Error("runner image catalogue has no available-images table");
  const rows = catalogue.slice(start, end).split("\n").filter(line => line.startsWith("|"));
  for (const label of labels) {
    const row = rows.find(line => line.includes(`\`${label}\``));
    if (row === undefined || /badge\/(?:deprecated|preview)-/u.test(row)) {
      throw new Error(`runner image ${label} is not an active supported catalogue row`);
    }
  }
}

function githubHeaders() {
  const headers = { Accept: "application/vnd.github+json", "User-Agent": "dot-orbit-security-check" };
  if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  return headers;
}

async function readJson(url, headers = {}) {
  const response = await fetch(url, { headers });
  if (!response.ok) throw new Error(`${url} returned HTTP ${response.status}`);
  return response.json();
}

async function assertSupportDriftAbsent() {
  const dotnet = await readJson("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json");
  const expectedDotnet = { "latest-sdk": "10.0.401", "latest-runtime": "10.0.12" };
  for (const [field, expected] of Object.entries(expectedDotnet)) {
    if (dotnet[field] !== expected) throw new Error(`.NET ${field} changed from ${expected} to ${dotnet[field]}`);
  }
  if (dotnet["support-phase"] !== "active"
      || new Date(`${dotnet["eol-date"]}T00:00:00Z`) <= new Date()) {
    throw new Error(`.NET 10 support changed to ${dotnet["support-phase"]} with EOL ${dotnet["eol-date"]}`);
  }

  const node = await readJson("https://nodejs.org/download/release/index.json");
  const currentNode = node.find(release => release.version.startsWith("v24.") && release.lts);
  if (currentNode?.version !== "v24.21.0") {
    throw new Error(`Node 24 LTS changed from v24.21.0 to ${currentNode?.version ?? "unknown"}`);
  }
  const nodeSchedule = await readJson("https://raw.githubusercontent.com/nodejs/Release/main/schedule.json");
  const nodePhase = nodeSupportPhase(nodeSchedule.v24);
  if (nodePhase !== "active-lts") throw new Error(`Node 24 support phase changed to ${nodePhase}`);

  const packages = new Map([
    ["avalonia.desktop", "12.1.3"],
    ["avalonia.fonts.inter", "12.1.3"],
    ["avalonia.headless.xunit", "12.1.3"],
    ["avalonia.themes.fluent", "12.1.3"],
    ["microsoft.data.sqlite.core", "10.0.12"],
    ["sqlite3mc.pclraw.bundle", "2.4.0"],
    ["xunit.v3", "4.0.1"],
  ]);
  for (const [packageId, expected] of packages) {
    const index = await readJson(`https://api.nuget.org/v3-flatcontainer/${packageId}/index.json`);
    const current = latestStable([...index.versions].reverse());
    if (current !== expected) throw new Error(`${packageId} latest stable changed from ${expected} to ${current}`);
  }

  const actions = [
    ["actions/checkout", "v7.0.1"],
    ["actions/setup-dotnet", "v6.0.0"],
    ["actions/setup-node", "v7.0.0"],
    ["actions/upload-artifact", "v7.0.1"],
    ["actions/download-artifact", "v8.0.1"],
    ["github/codeql-action", "v4.38.2"],
  ];
  for (const [repository, expected] of actions) {
    const releases = await readJson(
      `https://api.github.com/repos/${repository}/releases?per_page=20`,
      githubHeaders());
    const current = latestActionRelease(releases);
    if (current !== expected) throw new Error(`${repository} release changed from ${expected} to ${current ?? "unknown"}`);
  }

  const runnerCatalogue = await fetch("https://raw.githubusercontent.com/actions/runner-images/main/README.md");
  if (!runnerCatalogue.ok) throw new Error(`runner image catalogue returned HTTP ${runnerCatalogue.status}`);
  assertActiveRunnerLabels(
    await runnerCatalogue.text(),
    ["ubuntu-24.04", "windows-2025", "macos-26", "macos-26-intel"]);
}

async function run(arguments_) {
  const [command, suppliedPath] = arguments_;
  const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
  if (command === "nuget-policy") {
    assertNuGetPolicy(fs.readFileSync(suppliedPath ?? path.join(repositoryRoot, "NuGet.Config"), "utf8"));
    console.log("security-controls: nuget-policy=passed");
    return;
  }
  if (command === "support-review") {
    assertSupportReviewCurrent(fs.readFileSync(
      suppliedPath ?? path.join(repositoryRoot, "docs/development/dependency-baseline.md"), "utf8"));
    console.log("security-controls: support-review=passed");
    return;
  }
  if (command === "support-drift") {
    await assertSupportDriftAbsent();
    console.log("security-controls: support-drift=passed");
    return;
  }
  throw new Error("Usage: node .github/scripts/security-controls.mjs <nuget-policy|support-review|support-drift> [path]");
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    await run(process.argv.slice(2));
  } catch (error) {
    console.error(`security-controls: result=failed reason=${error.message}`);
    process.exitCode = 1;
  }
}
