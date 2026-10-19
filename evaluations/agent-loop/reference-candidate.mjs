import { writeFile } from "node:fs/promises";

const caseId = process.env.AGENT_EVAL_CASE_ID;
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

Follow CONTEXT.md and the local-only first-release boundary in the product brief and ADRs. This slice stays inside the existing single context and storage boundary.

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
};

if (!solutions[caseId]) throw new Error(`unknown-case:${caseId}`);
process.stdin.resume();
await solutions[caseId]();
