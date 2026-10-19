import assert from "node:assert/strict";
import test from "node:test";
import { gradeSpecification } from "./cases/specification-boundary/specification-grader.mjs";

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


test("Orbit cases fail initially and their scripted references satisfy the graders", async (t) => {
  const { cp, mkdtemp, rm, readFile } = await import("node:fs/promises");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const { execFile } = await import("node:child_process");
  const { promisify } = await import("node:util");
  const exec = promisify(execFile);
  const root = await mkdtemp(join(tmpdir(), "orbit-case-tests-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const env = { ...process.env };
  delete env.NODE_TEST_CONTEXT;
  const destination = join(root, "evaluations/agent-loop");
  await cp(new URL("./", import.meta.url), destination, { recursive: true });
  for (const id of ["specification-boundary", "accessibility-verification"]) {
    const manifest = JSON.parse(await readFile(join(destination, `cases/${id}/case.json`), "utf8"));
    const command = manifest.checks.find((check) => check.type === "command").command;
    await assert.rejects(exec(command[0], command.slice(1), { cwd: root, env }), (error) => error.code === 1);
    const reference = exec(process.execPath, [join(destination, "reference-candidate.mjs")], {
      cwd: root, env: { ...env, AGENT_EVAL_CASE_ID: id },
    });
    reference.child.stdin.end();
    await reference;
    await exec(command[0], command.slice(1), { cwd: root, env });
  }
});
