import test from "node:test";
import assert from "node:assert/strict";

import {
  assertNuGetPolicy,
  assertSupportReviewCurrent,
  assertActiveRunnerLabels,
  latestActionRelease,
  latestStable,
  nodeSupportPhase,
} from "./security-controls.mjs";

const approvedConfig = `<?xml version="1.0" encoding="utf-8"?>
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

test("NuGet policy accepts the single approved mapped package and audit source", () => {
  assert.doesNotThrow(() => assertNuGetPolicy(approvedConfig));
});

test("NuGet policy rejects an additional inherited or checked-in source", () => {
  const changed = approvedConfig.replace(
    "  </packageSources>",
    "    <add key=\"unapproved\" value=\"https://packages.example.invalid/v3/index.json\" />\n  </packageSources>");

  assert.throws(() => assertNuGetPolicy(changed), /only the approved nuget\.org/u);
});

test("NuGet policy rejects a changed package-source mapping", () => {
  assert.throws(
    () => assertNuGetPolicy(approvedConfig.replace('<package pattern="*" />', '<package pattern="Microsoft.*" />')),
    /only the approved nuget\.org/u);
});

test("Support review accepts its deadline and fails after it", () => {
  const baseline = "Checked: 2026-09-29\nReview by: 2026-10-29\n";

  assert.doesNotThrow(() => assertSupportReviewCurrent(baseline, new Date("2026-10-29T12:00:00Z")));
  assert.throws(
    () => assertSupportReviewCurrent(baseline, new Date("2026-10-30T00:00:00Z")),
    /expired on 2026-10-29/u);
});

test("Support review rejects missing, duplicate, and impossible deadlines", () => {
  assert.throws(() => assertSupportReviewCurrent("Checked: 2026-09-29\n"), /exactly one/u);
  assert.throws(
    () => assertSupportReviewCurrent("Review by: 2026-10-29\nReview by: 2026-11-29\n"),
    /exactly one/u);
  assert.throws(() => assertSupportReviewCurrent("Review by: 2026-02-31\n"), /invalid/u);
});

test("Latest stable selection ignores prerelease versions", () => {
  assert.equal(latestStable(["13.0.0-preview.2", "12.1.4", "12.1.3"]), "12.1.4");
});

test("Action drift selection detects a newer major independent of API ordering", () => {
  assert.equal(
    latestActionRelease([
      { tag_name: "v7.4.0" },
      { tag_name: "codeql-bundle-v9.0.0" },
      { tag_name: "v8.0.0" },
      { tag_name: "v7.5.0" },
    ]),
    "v8.0.0");
});

test("Node support phase changes at the official maintenance and end dates", () => {
  const schedule = { lts: "2025-10-28", maintenance: "2026-10-20", end: "2028-04-30" };

  assert.equal(nodeSupportPhase(schedule, new Date("2026-09-29T00:00:00Z")), "active-lts");
  assert.equal(nodeSupportPhase(schedule, new Date("2026-10-20T00:00:00Z")), "maintenance");
  assert.equal(nodeSupportPhase(schedule, new Date("2028-04-30T00:00:00Z")), "end-of-life");
});

test("Runner check accepts active table rows and rejects deprecated rows or link definitions", () => {
  const active = "## Available Images\n| Image | Architecture | YAML Label |\n| Ubuntu | x64 | `ubuntu-24.04` |\n### Label scheme\n";
  const deprecated = "## Available Images\n| Image [![deprecated](https://img.shields.io/badge/deprecated-E5534B)] | x64 | `ubuntu-24.04` |\n### Label scheme\n";
  const linkOnly = "## Available Images\n| Image | Architecture | YAML Label |\n### Label scheme\n[ubuntu-24.04]: example\n";

  assert.doesNotThrow(() => assertActiveRunnerLabels(active, ["ubuntu-24.04"]));
  assert.throws(() => assertActiveRunnerLabels(deprecated, ["ubuntu-24.04"]), /not an active supported/u);
  assert.throws(() => assertActiveRunnerLabels(linkOnly, ["ubuntu-24.04"]), /not an active supported/u);
});
