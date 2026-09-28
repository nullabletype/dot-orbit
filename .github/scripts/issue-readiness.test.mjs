import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import {
  contractValue,
  parseIssue,
  planReadiness,
  readIssues,
  reconcile,
  requiredHeadings,
} from "./issue-readiness.mjs";

function body({ blockedBy = "None", openDecisions = "None", omit, blank, extra = "", level = 3 } = {}) {
  const content = new Map([
    ["Readiness contract", contractValue],
    ["User-visible outcome", "A maintainer can rely on the workflow."],
    ["Acceptance criteria", "- [ ] The transition is deterministic."],
    ["Non-goals", "Project automation."],
    ["Domain and decision context", "See docs/agents/issue-tracker.md."],
    ["Known constraints", "No untrusted content is executed."],
    ["Verification", "Run the focused tests."],
    ["Open decisions", openDecisions],
    ["Blocked by", blockedBy],
  ]);
  if (omit) content.delete(omit);
  if (blank) content.set(blank, "   ");
  return [...content].map(([heading, value]) => `${"#".repeat(level)} ${heading}\n\n${value}`).join("\n\n") + extra;
}

function issue(number, issueBody, { isOpen = true, implementationSlice = true, labels = [] } = {}) {
  return {
    number,
    body: issueBody,
    isOpen,
    labels: new Set([...labels, ...(implementationSlice ? ["implementation-slice"] : [])]),
  };
}

function response(json, { link, status = 200 } = {}) {
  return new Response(JSON.stringify(json), {
    status,
    headers: { "content-type": "application/json", ...(link ? { link } : {}) },
  });
}

test("parseIssue accepts the complete contract at level two or three", () => {
  assert.deepEqual(parseIssue(body()), { dependencies: [] });
  assert.deepEqual(parseIssue(body({ level: 2 })), { dependencies: [] });
});

test("parseIssue fails closed for every missing, duplicate, or blank required section", async (t) => {
  for (const heading of requiredHeadings) {
    await t.test(`missing ${heading}`, () => {
      assert.match(parseIssue(body({ omit: heading })).failureReason, /missing-or-duplicate$/);
    });
    await t.test(`blank ${heading}`, () => {
      assert.match(parseIssue(body({ blank: heading })).failureReason, /blank$/);
    });
  }
  assert.equal(
    parseIssue(body({ extra: "\n\n### Blocked by\n\nNone" })).failureReason,
    "section-blocked-by-missing-or-duplicate",
  );
});

test("parseIssue requires the supported contract and resolved decisions", () => {
  assert.equal(
    parseIssue(body().replace(contractValue, "dot-orbit-issue-readiness:v2")).failureReason,
    "readiness-contract-unsupported",
  );
  assert.equal(
    parseIssue(body({ openDecisions: "Choose the API shape." })).failureReason,
    "open-decisions-unresolved",
  );
});

test("parseIssue accepts only sorted, deduplicated local dependency bullets", () => {
  assert.deepEqual(parseIssue(body({ blockedBy: "- #41\n- #7\n- #41" })), { dependencies: [7, 41] });
  for (const malformed of [
    "", "#12", "- nullabletype/dot-orbit#12", "- https://github.com/nullabletype/dot-orbit/issues/12",
    "None\n- #12", "- #0", "- #2147483648", "- #12 dependency",
  ]) {
    assert.match(parseIssue(body({ blockedBy: malformed })).failureReason, /blocked-by/);
  }
  assert.deepEqual(
    parseIssue(body({ extra: "\n\n### Notes\n\nSee #999 and nullabletype/elsewhere#3." })),
    { dependencies: [] },
  );
});

test("planReadiness promotes, blocks, and restores dependency state without touching unrelated labels", () => {
  const promoted = planReadiness([
    issue(10, body({ blockedBy: "- #2\n- #3" }), { labels: ["blocked", "enhancement"] }),
    issue(2, body(), { isOpen: false }),
    issue(3, body(), { isOpen: false }),
  ]);
  assert.deepEqual(promoted, [{
    issueNumber: 10,
    reason: "dependencies-closed",
    deltas: [
      { operation: "add", label: "ready-for-agent" },
      { operation: "remove", label: "blocked" },
    ],
  }]);

  const blocked = planReadiness([
    issue(10, body({ blockedBy: "- #2" }), { labels: ["ready-for-agent", "enhancement"] }),
    issue(2, body(), { labels: ["needs-triage"] }),
  ]);
  assert.deepEqual(blocked[0].deltas, [
    { operation: "add", label: "blocked" },
    { operation: "remove", label: "ready-for-agent" },
  ]);
});

test("planReadiness is idempotent and leaves manual, closed, unrelated, and legacy issues untouched", () => {
  const dependency = issue(2, body(), { labels: ["needs-triage"] });
  assert.deepEqual(planReadiness([
    issue(10, body({ blockedBy: "- #2" }), { labels: ["blocked", "enhancement"] }),
    dependency,
  ])[0].deltas, []);

  for (const label of ["needs-triage", "needs-info", "ready-for-human", "wontfix"]) {
    assert.deepEqual(planReadiness([
      issue(10, body({ blockedBy: "- #2" }), { labels: ["ready-for-agent", label] }),
      dependency,
    ]), []);
  }
  assert.deepEqual(planReadiness([issue(10, body(), { labels: ["enhancement"] })]), []);
  assert.deepEqual(planReadiness([issue(10, body(), { isOpen: false, labels: ["blocked"] })]), []);
  assert.deepEqual(planReadiness([
    issue(10, body(), { implementationSlice: false, labels: ["ready-for-agent"] }),
  ]), []);
});

test("planReadiness sends malformed managed issues back to needs-triage", () => {
  const plan = planReadiness([
    issue(10, body({ openDecisions: "Choose a format." }), { labels: ["ready-for-agent", "enhancement"] }),
  ])[0];
  assert.equal(plan.reason, "open-decisions-unresolved");
  assert.deepEqual(plan.deltas, [
    { operation: "add", label: "needs-triage" },
    { operation: "remove", label: "ready-for-agent" },
  ]);
});

test("planReadiness fails closed for missing, self, direct, transitive, and reached cycles", () => {
  assert.equal(
    planReadiness([issue(10, body({ blockedBy: "- #404" }), { labels: ["blocked"] })])[0].reason,
    "dependency-not-found:404",
  );
  assert.equal(
    planReadiness([issue(10, body({ blockedBy: "- #10" }), { labels: ["blocked"] })])[0].reason,
    "dependency-self-reference",
  );
  assert.match(planReadiness([
    issue(10, body({ blockedBy: "- #20" }), { labels: ["blocked"] }),
    issue(20, body({ blockedBy: "- #10" }), { labels: ["needs-triage"] }),
  ])[0].reason, /^dependency-cycle:/);
  assert.match(planReadiness([
    issue(10, body({ blockedBy: "- #20" }), { labels: ["ready-for-agent"] }),
    issue(20, body({ blockedBy: "- #30" }), { labels: ["needs-triage"] }),
    issue(30, body({ blockedBy: "- #10" }), { labels: ["needs-info"] }),
  ])[0].reason, /^dependency-cycle:/);
  assert.equal(planReadiness([
    issue(10, body({ blockedBy: "- #20" }), { labels: ["blocked"] }),
    issue(20, body({ blockedBy: "- #30" }), { labels: ["needs-triage"] }),
    issue(30, body({ blockedBy: "- #20" }), { labels: ["needs-info"] }),
  ])[0].reason, "dependency-cycle:20,30,20");
});

test("readIssues follows pagination, normalises bodies, and excludes pull requests", async () => {
  const requests = [];
  const pages = [
    response([
      { number: 1, body: "first", state: "open", labels: [{ name: "blocked" }] },
      { number: 99, body: "pull request", state: "open", labels: [], pull_request: { url: "ignored" } },
    ], { link: '<https://api.github.test/repos/owner/repo/issues?state=all&per_page=100&page=2>; rel="next"' }),
    response([{ number: 2, body: null, state: "closed", labels: [{ name: "done" }] }]),
  ];
  const issues = await readIssues({
    apiUrl: "https://api.github.test/",
    repository: "owner/repo",
    token: "token",
    fetchImpl: async (url) => { requests.push(String(url)); return pages.shift(); },
  });
  assert.deepEqual(issues.map((item) => item.number), [1, 2]);
  assert.equal(issues[1].body, "");
  assert.equal(issues[1].isOpen, false);
  assert.deepEqual(requests, [
    "https://api.github.test/repos/owner/repo/issues?state=all&per_page=100&page=1",
    "https://api.github.test/repos/owner/repo/issues?state=all&per_page=100&page=2",
  ]);
});

test("a pull request dependency is excluded and therefore fails closed as missing", async () => {
  const issues = await readIssues({
    apiUrl: "https://api.github.test",
    repository: "owner/repo",
    token: "token",
    fetchImpl: async () => response([
      { number: 10, body: body({ blockedBy: "- #99" }), state: "open", labels: [{ name: "blocked" }, { name: "implementation-slice" }] },
      { number: 99, body: "pull request", state: "open", labels: [], pull_request: { url: "ignored" } },
    ]),
  });
  assert.equal(planReadiness(issues)[0].reason, "dependency-not-found:99");
});

test("reconcile dry-run reports content-free deltas and makes no writes", async () => {
  const sensitive = "private-task-content";
  const requests = [];
  const messages = [];
  await reconcile({
    apiUrl: "https://api.github.test",
    repository: "owner/repo",
    token: "token",
    dryRun: true,
    fetchImpl: async (url, options = {}) => {
      requests.push({ url: String(url), method: options.method ?? "GET" });
      return response([{
        number: 8,
        body: body().replace("A maintainer can rely on the workflow.", sensitive),
        state: "open",
        labels: [{ name: "blocked" }, { name: "implementation-slice" }, { name: "enhancement" }],
      }]);
    },
    log: (message) => messages.push(message),
  });
  assert.deepEqual(messages, ["issue-readiness: issue=#8 reason=dependencies-closed labels=+ready-for-agent,-blocked dry-run=true"]);
  assert.equal(messages[0].includes(sensitive), false);
  assert.deepEqual(requests.map((request) => request.method), ["GET"]);
});

test("reconcile targets label endpoints and adds the replacement before removing the old state", async () => {
  const requests = [];
  await reconcile({
    apiUrl: "https://api.github.test",
    repository: "owner/repo",
    token: "token",
    dryRun: false,
    fetchImpl: async (url, options = {}) => {
      requests.push({ url: String(url), method: options.method ?? "GET", body: options.body });
      return requests.length === 1
        ? response([{ number: 8, body: body(), state: "open", labels: [{ name: "blocked" }, { name: "implementation-slice" }] }])
        : response({});
    },
    log: () => {},
  });
  assert.deepEqual(requests.slice(1), [
    {
      url: "https://api.github.test/repos/owner/repo/issues/8/labels",
      method: "POST",
      body: '{"labels":["ready-for-agent"]}',
    },
    {
      url: "https://api.github.test/repos/owner/repo/issues/8/labels/blocked",
      method: "DELETE",
      body: undefined,
    },
  ]);
});

test("repository contracts keep structured intake, handoff, review, and least-privilege workflow wiring", async () => {
  const [issueForm, handoff, pullRequest, workflow, build] = await Promise.all([
    readFile(".github/ISSUE_TEMPLATE/agent-ready.yml", "utf8"),
    readFile(".github/HANDOFF_TEMPLATE.md", "utf8"),
    readFile(".github/PULL_REQUEST_TEMPLATE.md", "utf8"),
    readFile(".github/workflows/issue-readiness.yml", "utf8"),
    readFile(".github/workflows/build.yml", "utf8"),
  ]);
  assert.match(issueForm, /labels:\n  - needs-triage\n  - implementation-slice/);
  assert.equal(issueForm.includes("  - ready-for-agent"), false);
  for (const heading of requiredHeadings) assert.ok(issueForm.includes(`label: ${heading}`));
  for (const field of ["Branch:", "Current commit SHA:", "Changed files", "Completed", "Remaining", "Next safe action", "Review state:"]) {
    assert.ok(handoff.includes(field));
  }
  for (const field of ["Acceptance criteria and evidence", "Accessibility impact", "Dependency-baseline impact", "Independent review"]) {
    assert.ok(pullRequest.includes(field));
  }
  assert.match(workflow, /permissions: \{\}/);
  assert.match(workflow, /dry-run:[\s\S]*issues: read/);
  assert.match(workflow, /reconcile:[\s\S]*issues: write/);
  assert.equal(workflow.includes("github.event.issue.body"), false);
  assert.equal(workflow.includes("DotOrbit.IssueReadiness"), false);
  assert.match(build, /if: matrix\.os == 'ubuntu-24\.04'[\s\S]*node --test \.github\/scripts\/issue-readiness\.test\.mjs/);
});
