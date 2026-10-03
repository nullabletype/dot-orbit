import { readFile, writeFile } from "node:fs/promises";

const caseId = process.env.DOT_ORBIT_EVAL_CASE_ID;
const root = `evaluations/agent-loop/cases/${caseId}/workspace`;

const solutions = {
  "specification-boundary": async () => writeFile(`${root}/issue.md`, `# Add quick task capture

## Readiness contract

dot-orbit-issue-readiness:v1

## User-visible outcome

People can capture a task without leaving the current view and find it in the local workspace.

## Acceptance criteria

- [ ] A non-blank task title can be saved from the current view.
- [ ] The task is persisted in the encrypted local-only workspace.
- [ ] The task belongs to the existing single context and appears in manual order.

## Non-goals

- Accounts
- Device sync
- Cloud storage or hosted task processing

## Domain and decision context

Follow CONTEXT.md and the local-only first-release boundary in the product brief and ADRs. This slice adds no new context or storage boundary.

## Known constraints

Task content stays encrypted at rest and diagnostics contain no plaintext task content.

## Verification

- Run the focused task-capture interaction tests.
- Reopen the encrypted workspace and assert the saved title and order.
- Run the canonical repository gate from a clean commit.

## Open decisions

None

## Blocked by

None
`),
  "bounded-ordering": async () => writeFile(`${root}/order.mjs`, `export function moveItem(values, sourceIndex, destinationIndex) {
  const result = [...values];
  if (!Number.isInteger(sourceIndex) || sourceIndex < 0 || sourceIndex >= result.length) return result;
  const [value] = result.splice(sourceIndex, 1);
  const target = Math.max(0, Math.min(result.length, destinationIndex));
  result.splice(target, 0, value);
  return result;
}
`),
  "regression-date-validation": async () => {
    await writeFile(`${root}/date.mjs`, `export function parseDate(value) {
  const match = /^(\\d{4})-(\\d{2})-(\\d{2})$/.exec(value);
  if (!match) return undefined;
  const [, year, month, day] = match.map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day
    ? value
    : undefined;
}
`);
    await writeFile(`${root}/date.test.mjs`, `import assert from "node:assert/strict";
import test from "node:test";
import { parseDate } from "./date.mjs";

test("accepts canonical and leap-year persisted dates", () => {
  assert.equal(parseDate("2026-10-03"), "2026-10-03");
  assert.equal(parseDate("2024-02-29"), "2024-02-29");
});

test("rejects malformed and impossible dates", () => {
  assert.equal(parseDate("03/10/2026"), undefined);
  assert.equal(parseDate(""), undefined);
  assert.equal(parseDate("2026-02-29"), undefined);
  assert.equal(parseDate("2026-02-31"), undefined);
});
`);
  },
  "documentation-drift": async () => writeFile(`${root}/README.md`, `# Orbit report fixture

Create a report with:

\`\`\`sh
node cli.mjs --output ./review.json
\`\`\`

\`--output\` writes to the default: \`./orbit-report.json\` when no path is supplied.
`),
  "accessibility-verification": async () => {
    await writeFile(`${root}/RecoveryPanel.axaml`, `<UserControl xmlns="https://github.com/avaloniaui">
  <StackPanel>
    <TextBlock Text="Recovery needs attention" />
    <Button AutomationProperties.Name="Retry recovery" IsTabStop="True" Content="↻" Command="{Binding RetryCommand}" />
    <TextBlock Text="Recovery failed" AutomationProperties.LiveSetting="Assertive" />
  </StackPanel>
</UserControl>
`);
    await writeFile(`${root}/verification.md`, `# Verification

The deterministic check proves that the retry action has an accessible name, remains in keyboard order, and exposes failure state as text with a live-setting cue.

Rendered focus order and visual presentation still require human review. Full assistive technology announcement behaviour remains release verification.
`);
  },
  "dependency-security-review": async () => {
    const catalog = JSON.parse(await readFile(`${root}/support-catalog.json`, "utf8"));
    const version = catalog.package.supportedVersion;
    await writeFile(`${root}/package.json`, `${JSON.stringify({ name: "orbit-evaluation-fixture", private: true, dependencies: { "safe-json": version } }, null, 2)}\n`);
    await writeFile(`${root}/package-lock.json`, `${JSON.stringify({
      name: "orbit-evaluation-fixture",
      lockfileVersion: 3,
      requires: true,
      packages: {
        "": { dependencies: { "safe-json": version } },
        "node_modules/safe-json": { version },
      },
    }, null, 2)}\n`);
    await writeFile(`${root}/action.yml`, `name: Synthetic check
on: workflow_dispatch
jobs:
  check:
    runs-on: ubuntu-24.04
    steps:
      - uses: ${catalog.action.name}@${catalog.action.supportedCommit}
`);
    await writeFile(`${root}/review.md`, `# Dependency review

Checked 2026-10-03 against support-catalog.json. The package and action are supported and reproducibly pinned. No network lookup or credential was used.

Next review: ${catalog.nextReview}.
`);
  },
};

if (!solutions[caseId]) throw new Error(`unknown-case:${caseId}`);
process.stdin.resume();
await solutions[caseId]();
