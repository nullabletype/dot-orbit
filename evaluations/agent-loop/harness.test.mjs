import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { evaluateChecks, runEvaluation, runProcess, summarize, validateCase, versionInputs } from "./harness.mjs";
import { gradeSpecification } from "./cases/specification-boundary/specification-grader.mjs";

const exec = promisify(execFile);

function definition(overrides = {}) {
  return {
    schemaVersion: 1,
    id: "sample",
    category: "bounded-code-change",
    title: "Sample case",
    startingState: { description: "A safe synthetic fixture.", files: ["workspace/value.txt"] },
    allowedPaths: ["workspace/value.txt"],
    prompt: "Change workspace/value.txt to done.",
    expectedObservableResult: ["The value is done."],
    checks: [
      { id: "content", type: "file-contains", path: "workspace/value.txt", fragments: ["done"], points: 80 },
      { id: "scope", type: "scope", points: 20 },
    ],
    rubric: { totalPoints: 100, passPoints: 100 },
    ...overrides,
  };
}

async function initializeRepository(root) {
  await exec("git", ["init", "-q"], { cwd: root });
  await exec("git", ["config", "user.name", "Agent Evaluation Test"], { cwd: root });
  await exec("git", ["config", "user.email", "agent-evaluation@example.invalid"], { cwd: root });
}

test("validateCase rejects path escapes and unsupported graders", () => {
  assert.throws(
    () => validateCase(definition({ allowedPaths: ["../outside"] })),
    /path-escape/,
  );
  assert.throws(
    () => validateCase(definition({
      checks: [{ id: "unsafe", type: "shell", points: 100 }],
      rubric: { totalPoints: 100, passPoints: 100 },
    })),
    /unsupported-type/,
  );
  assert.throws(
    () => validateCase(definition({
      checks: [{ id: "content", type: "file-contains", path: "workspace/value.txt", fragments: ["done"], caseSensitive: "no", points: 100 }],
      rubric: { totalPoints: 100, passPoints: 100 },
    })),
    /case-sensitive-boolean-required/,
  );
});

test("evaluateChecks grades observable content and unrelated churn", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "dot-orbit-eval-grade-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  await initializeRepository(root);
  await mkdir(join(root, "workspace"));
  await writeFile(join(root, "workspace/value.txt"), "todo\n");
  await exec("git", ["add", "."], { cwd: root });
  await exec("git", ["commit", "-qm", "fixture"], { cwd: root });
  const base = (await exec("git", ["rev-parse", "HEAD"], { cwd: root })).stdout.trim();

  await writeFile(join(root, "workspace/value.txt"), "done\n");
  let grade = await evaluateChecks(validateCase(definition()), root, base);
  assert.equal(grade.passed, true);
  assert.equal(grade.unrelatedFileChurn, 0);

  await writeFile(join(root, "outside.txt"), "unrelated\n");
  grade = await evaluateChecks(validateCase(definition()), root, base);
  assert.equal(grade.passed, false);
  assert.equal(grade.unrelatedFileChurn, 1);
});

test("summarize records every required aggregate metric", () => {
  assert.deepEqual(summarize([
    { passed: true, firstPassPassed: true, correctionCycles: 0, unrelatedFileChurn: 0, elapsedMilliseconds: 10, toolFailures: 0, independentReview: "approved" },
    { passed: false, firstPassPassed: false, correctionCycles: 1, unrelatedFileChurn: 2, elapsedMilliseconds: 25, toolFailures: 1, independentReview: "unavailable" },
  ]), {
    totalRuns: 2,
    successfulRuns: 1,
    repeatedTrialSuccessRate: 0.5,
    firstPassGateRate: 0.5,
    correctionCycles: 1,
    unrelatedFileChurn: 2,
    elapsedMilliseconds: 35,
    toolFailures: 1,
    independentReview: { approved: 1, rejected: 0, unavailable: 1, errored: 0 },
  });
});

test("runProcess force-terminates a child that ignores graceful timeout", async () => {
  const startedAt = Date.now();
  const result = await runProcess(process.execPath, [
    "-e",
    "process.on('SIGTERM',()=>{}); setInterval(()=>{}, 1000);",
  ], {
    timeoutMilliseconds: 50,
    terminationGraceMilliseconds: 100,
  });
  assert.equal(result.timedOut, true);
  assert.ok(Date.now() - startedAt < 2_000);
});

test("specification grader rejects empty or keyword-only sections", () => {
  const headings = [
    "Readiness contract", "User-visible outcome", "Acceptance criteria", "Non-goals",
    "Domain and decision context", "Known constraints", "Verification", "Open decisions", "Blocked by",
  ];
  const empty = headings.map((heading) => `## ${heading}\n`).join("\n");
  assert.equal(gradeSpecification(empty), false);

  const keywordOnly = headings.map((heading) => `## ${heading}\n\nlocal-only encrypted single context accounts sync cloud`).join("\n\n");
  assert.equal(gradeSpecification(keywordOnly), false);

  const misplaced = headings.map((heading) => {
    if (heading === "Readiness contract") return `## ${heading}\n\ndot-orbit-issue-readiness:v1`;
    if (heading === "Open decisions" || heading === "Blocked by") return `## ${heading}\n\nNone`;
    if (["Acceptance criteria", "Non-goals", "Verification"].includes(heading)) {
      return `## ${heading}\n\n- alpha\n- beta\n- gamma`;
    }
    if (heading === "User-visible outcome") {
      return `## ${heading}\n\nlocal-only encrypted single context accounts sync cloud task save CONTEXT.md diagnostics plaintext test reopen gate`;
    }
    return `## ${heading}\n\ncontent`;
  }).join("\n\n");
  assert.equal(gradeSpecification(misplaced), false);
});

test("version inputs cover repository guidance and every non-baseline suite input", () => {
  const selected = versionInputs([
    "AGENTS.md",
    "CONTEXT.md",
    "README.md",
    "docs/product/brief.md",
    "docs/design/style-guide.md",
    "docs/adr/0001-example.md",
    ".github/HANDOFF_TEMPLATE.md",
    ".github/ISSUE_TEMPLATE/agent-ready.yml",
    ".github/workflows/build.yml",
    "evaluations/agent-loop/harness.mjs",
    "evaluations/agent-loop/cases/sample/workspace/verification.md",
    "evaluations/agent-loop/baselines/old.json",
  ]);
  assert.deepEqual(selected.instructionRelativePaths, [
    "AGENTS.md",
    "CONTEXT.md",
    "README.md",
    "docs/product/brief.md",
    "docs/design/style-guide.md",
    "docs/adr/0001-example.md",
    ".github/HANDOFF_TEMPLATE.md",
    ".github/ISSUE_TEMPLATE/agent-ready.yml",
    "evaluations/agent-loop/cases/sample/workspace/verification.md",
  ]);
  assert.deepEqual(selected.harnessRelativePaths, [
    ".github/workflows/build.yml",
    "evaluations/agent-loop/harness.mjs",
    "evaluations/agent-loop/cases/sample/workspace/verification.md",
  ]);
});

test("runEvaluation uses detached worktrees and leaves the source checkout unchanged", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "dot-orbit-eval-source-"));
  const resultPath = join(tmpdir(), `dot-orbit-eval-result-${process.pid}-${Date.now()}.json`);
  t.after(async () => {
    await rm(root, { recursive: true, force: true });
    await rm(resultPath, { force: true });
  });
  await initializeRepository(root);
  await mkdir(join(root, "docs/development"), { recursive: true });
  await writeFile(join(root, "AGENTS.md"), "# Test instructions\n");
  await writeFile(join(root, "docs/development/agent-loop.md"), "# Loop\n");
  await writeFile(join(root, "docs/development/definition-of-done.md"), "# Done\n");
  const casesRoot = join(root, "evaluations/agent-loop/cases");
  await mkdir(join(root, "evaluations/agent-loop"), { recursive: true });
  await writeFile(join(root, "evaluations/agent-loop/harness.mjs"), "// harness identity fixture\n");
  for (let index = 1; index <= 5; index += 1) {
    const id = `case-${index}`;
    const caseRoot = join(casesRoot, id);
    await mkdir(join(caseRoot, "workspace"), { recursive: true });
    const item = definition({
      id,
      startingState: { description: "A safe synthetic fixture.", files: [`evaluations/agent-loop/cases/${id}/workspace/value.txt`] },
      allowedPaths: [`evaluations/agent-loop/cases/${id}/workspace/value.txt`],
      checks: [
        { id: "content", type: "file-contains", path: `evaluations/agent-loop/cases/${id}/workspace/value.txt`, fragments: ["done"], points: 80 },
        { id: "scope", type: "scope", points: 20 },
      ],
    });
    await writeFile(join(caseRoot, "case.json"), `${JSON.stringify(item)}\n`);
    await writeFile(join(caseRoot, "workspace/value.txt"), "todo\n");
  }
  await exec("git", ["add", "."], { cwd: root });
  await exec("git", ["commit", "-qm", "evaluation source"], { cwd: root });
  const beforeHead = (await exec("git", ["rev-parse", "HEAD"], { cwd: root })).stdout.trim();
  const candidate = `const fs=require('node:fs'); const p=process.env.DOT_ORBIT_EVAL_ALLOWED_PATHS; fs.writeFileSync(p, 'done\\n'); process.stdin.resume();`;

  const result = await runEvaluation({
    repoRoot: root,
    casesRoot,
    resultPath,
    candidateCommand: process.execPath,
    candidateArgs: ["-e", candidate],
    candidateIdentity: "test-candidate",
    trials: 2,
    maxCorrections: 0,
    timeoutMilliseconds: 30_000,
  });

  assert.equal(result.aggregate.totalRuns, 10);
  assert.equal(result.aggregate.successfulRuns, 10);
  assert.equal((await exec("git", ["status", "--porcelain"], { cwd: root })).stdout, "");
  assert.equal((await exec("git", ["rev-parse", "HEAD"], { cwd: root })).stdout.trim(), beforeHead);
  assert.equal(JSON.parse(await readFile(resultPath, "utf8")).schema, "dot-orbit-agent-evaluation:v1");
});
