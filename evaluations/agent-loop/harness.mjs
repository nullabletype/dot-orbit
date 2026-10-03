import { createHash } from "node:crypto";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";

export const resultSchema = "dot-orbit-agent-evaluation:v1";

function safeRelativePath(value, field) {
  if (typeof value !== "string" || value.length === 0 || isAbsolute(value)) {
    throw new Error(`${field}:relative-path-required`);
  }
  const normal = value.replaceAll("\\", "/");
  if (normal === ".." || normal.startsWith("../") || normal.includes("/../")) {
    throw new Error(`${field}:path-escape`);
  }
  return normal;
}

function checkDefinition(check, index) {
  if (!check || typeof check !== "object" || typeof check.id !== "string") {
    throw new Error(`checks[${index}]:invalid`);
  }
  if (!Number.isInteger(check.points) || check.points <= 0) {
    throw new Error(`checks[${index}]:positive-points-required`);
  }
  if (check.type === "file-contains" || check.type === "file-excludes") {
    safeRelativePath(check.path, `checks[${index}].path`);
    if (!Array.isArray(check.fragments) || check.fragments.length === 0
      || check.fragments.some((fragment) => typeof fragment !== "string" || fragment.length === 0)) {
      throw new Error(`checks[${index}]:fragments-required`);
    }
  } else if (check.type === "command") {
    if (!Array.isArray(check.command) || check.command.length === 0
      || check.command.some((part) => typeof part !== "string" || part.length === 0)) {
      throw new Error(`checks[${index}]:command-required`);
    }
  } else if (check.type !== "scope") {
    throw new Error(`checks[${index}]:unsupported-type:${check.type}`);
  }
}

export function validateCase(definition) {
  for (const field of ["id", "category", "title", "prompt"]) {
    if (typeof definition?.[field] !== "string" || definition[field].trim().length === 0) {
      throw new Error(`${field}:required`);
    }
  }
  if (definition.schemaVersion !== 1) throw new Error("schemaVersion:unsupported");
  if (!definition.startingState || typeof definition.startingState.description !== "string"
    || !Array.isArray(definition.startingState.files) || definition.startingState.files.length === 0) {
    throw new Error("startingState:invalid");
  }
  definition.startingState.files.forEach((path, index) => safeRelativePath(path, `startingState.files[${index}]`));
  if (!Array.isArray(definition.allowedPaths) || definition.allowedPaths.length === 0) {
    throw new Error("allowedPaths:required");
  }
  definition.allowedPaths.forEach((path, index) => safeRelativePath(path, `allowedPaths[${index}]`));
  if (!Array.isArray(definition.expectedObservableResult) || definition.expectedObservableResult.length === 0
    || definition.expectedObservableResult.some((item) => typeof item !== "string" || item.length === 0)) {
    throw new Error("expectedObservableResult:required");
  }
  if (!Array.isArray(definition.checks) || definition.checks.length === 0) throw new Error("checks:required");
  definition.checks.forEach(checkDefinition);
  const ids = new Set(definition.checks.map((check) => check.id));
  if (ids.size !== definition.checks.length) throw new Error("checks:duplicate-id");
  const total = definition.checks.reduce((sum, check) => sum + check.points, 0);
  if (!definition.rubric || definition.rubric.totalPoints !== total
    || !Number.isInteger(definition.rubric.passPoints)
    || definition.rubric.passPoints <= 0 || definition.rubric.passPoints > total) {
    throw new Error("rubric:invalid");
  }
  return definition;
}

export async function loadCases(casesRoot) {
  const entries = await readdir(casesRoot, { withFileTypes: true });
  const definitions = [];
  for (const entry of entries.filter((item) => item.isDirectory()).sort((left, right) => left.name.localeCompare(right.name))) {
    const manifestPath = join(casesRoot, entry.name, "case.json");
    const definition = validateCase(JSON.parse(await readFile(manifestPath, "utf8")));
    if (definition.id !== entry.name) throw new Error(`${entry.name}:id-directory-mismatch`);
    definitions.push({ ...definition, manifestPath });
  }
  if (definitions.length < 5 || definitions.length > 10) throw new Error("case-count:expected-5-to-10");
  return definitions;
}

export async function runProcess(command, args, options = {}) {
  const startedAt = Date.now();
  return await new Promise((resolvePromise) => {
    let settled = false;
    let outputBytes = 0;
    let timedOut = false;
    let timer;
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: options.env ?? process.env,
      shell: false,
      stdio: [options.stdin === undefined ? "ignore" : "pipe", "pipe", "pipe"],
    });
    const finish = (value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolvePromise({ ...value, elapsedMilliseconds: Date.now() - startedAt, outputBytes, timedOut });
    };
    child.stdout.on("data", (chunk) => { outputBytes += chunk.length; });
    child.stderr.on("data", (chunk) => { outputBytes += chunk.length; });
    child.on("error", (error) => finish({ exitCode: null, errorCode: error.code ?? "spawn-error" }));
    child.on("close", (code, signal) => finish({ exitCode: code, signal: signal ?? null }));
    if (options.stdin !== undefined) child.stdin.end(options.stdin);
    timer = setTimeout(() => {
      timedOut = true;
      child.kill("SIGTERM");
    }, options.timeoutMilliseconds ?? 600_000);
  });
}

async function git(repoRoot, args, options = {}) {
  const result = await runProcess("git", args, { cwd: repoRoot, ...options });
  if (result.exitCode !== 0) throw new Error(`git-${args[0]}-failed:${result.errorCode ?? result.exitCode}`);
  return result;
}

async function gitText(repoRoot, args) {
  return await new Promise((resolvePromise, reject) => {
    const child = spawn("git", args, { cwd: repoRoot, shell: false, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.resume();
    child.on("error", reject);
    child.on("close", (code) => code === 0 ? resolvePromise(stdout.trim()) : reject(new Error(`git-${args[0]}-failed:${code}`)));
  });
}

function allowed(path, allowedPaths) {
  return allowedPaths.some((candidate) => {
    const normalized = candidate.replaceAll("\\", "/");
    return normalized.endsWith("/") ? path.startsWith(normalized) : path === normalized;
  });
}

export async function changedPaths(worktree, baseRef = "HEAD") {
  const [tracked, untracked] = await Promise.all([
    gitText(worktree, ["diff", "--name-only", baseRef]),
    gitText(worktree, ["ls-files", "--others", "--exclude-standard"]),
  ]);
  return [...new Set([...tracked.split("\n"), ...untracked.split("\n")].filter(Boolean))].sort();
}

export async function evaluateChecks(definition, worktree, baseRef = "HEAD") {
  const paths = await changedPaths(worktree, baseRef);
  const results = [];
  for (const check of definition.checks) {
    let passed = false;
    if (check.type === "scope") {
      passed = paths.length > 0 && paths.every((path) => allowed(path, definition.allowedPaths));
    } else if (check.type === "file-contains" || check.type === "file-excludes") {
      let content;
      try {
        content = await readFile(resolve(worktree, check.path), "utf8");
      } catch {
        content = undefined;
      }
      passed = content !== undefined && (check.type === "file-contains"
        ? check.fragments.every((fragment) => content.includes(fragment))
        : check.fragments.every((fragment) => !content.includes(fragment)));
    } else if (check.type === "command") {
      const [command, ...args] = check.command;
      const result = await runProcess(command, args, { cwd: worktree, timeoutMilliseconds: check.timeoutMilliseconds ?? 120_000 });
      passed = result.exitCode === 0;
    }
    results.push({ id: check.id, passed, points: passed ? check.points : 0, maximumPoints: check.points });
  }
  const points = results.reduce((sum, check) => sum + check.points, 0);
  const unrelatedPaths = paths.filter((path) => !allowed(path, definition.allowedPaths));
  return {
    passed: points >= definition.rubric.passPoints,
    points,
    maximumPoints: definition.rubric.totalPoints,
    checks: results,
    changedPathCount: paths.length,
    unrelatedFileChurn: unrelatedPaths.length,
  };
}

export function summarize(runs) {
  const totalRuns = runs.length;
  const successfulRuns = runs.filter((run) => run.passed).length;
  const firstPassRuns = runs.filter((run) => run.firstPassPassed).length;
  return {
    totalRuns,
    successfulRuns,
    repeatedTrialSuccessRate: totalRuns === 0 ? 0 : successfulRuns / totalRuns,
    firstPassGateRate: totalRuns === 0 ? 0 : firstPassRuns / totalRuns,
    correctionCycles: runs.reduce((sum, run) => sum + run.correctionCycles, 0),
    unrelatedFileChurn: runs.reduce((sum, run) => sum + run.unrelatedFileChurn, 0),
    elapsedMilliseconds: runs.reduce((sum, run) => sum + run.elapsedMilliseconds, 0),
    toolFailures: runs.reduce((sum, run) => sum + run.toolFailures, 0),
    independentReview: {
      approved: runs.filter((run) => run.independentReview === "approved").length,
      rejected: runs.filter((run) => run.independentReview === "rejected").length,
      unavailable: runs.filter((run) => run.independentReview === "unavailable").length,
      errored: runs.filter((run) => run.independentReview === "errored").length,
    },
  };
}

async function fingerprint(paths) {
  const hash = createHash("sha256");
  for (const path of [...paths].sort()) {
    hash.update(path);
    hash.update("\0");
    hash.update(await readFile(path));
    hash.update("\0");
  }
  return hash.digest("hex");
}

function correctionPrompt(definition, failedChecks, cycle) {
  return `${definition.prompt}\n\nCorrection cycle ${cycle}: deterministic checks still failing: ${failedChecks.join(", ")}. Reinspect only the allowed scope, correct the result, and rerun the relevant local checks.`;
}

async function invokeCandidate({ candidateCommand, candidateArgs, definition, worktree, prompt, timeoutMilliseconds }) {
  return await runProcess(candidateCommand, candidateArgs, {
    cwd: worktree,
    stdin: prompt,
    timeoutMilliseconds,
    env: {
      ...process.env,
      DOT_ORBIT_EVAL_CASE_ID: definition.id,
      DOT_ORBIT_EVAL_WORKTREE: worktree,
      DOT_ORBIT_EVAL_ALLOWED_PATHS: definition.allowedPaths.join(":"),
    },
  });
}

async function reviewRun({ reviewerCommand, reviewerArgs, definition, worktree, timeoutMilliseconds }) {
  if (!reviewerCommand) return "unavailable";
  const prompt = `Independently review case ${definition.id}. Inspect the uncommitted diff in the current worktree against evaluations/agent-loop/cases/${definition.id}/case.json. Exit 0 only when the observable result and deterministic checks are sufficient; otherwise exit non-zero. Do not edit files.`;
  const result = await runProcess(reviewerCommand, reviewerArgs, {
    cwd: worktree,
    stdin: prompt,
    timeoutMilliseconds,
    env: { ...process.env, DOT_ORBIT_EVAL_CASE_ID: definition.id, DOT_ORBIT_EVAL_WORKTREE: worktree },
  });
  if (result.exitCode === 0) return "approved";
  return result.exitCode === null || result.timedOut ? "errored" : "rejected";
}

export async function runEvaluation(options) {
  const repoRoot = resolve(options.repoRoot);
  const casesRoot = resolve(options.casesRoot);
  const resultPath = resolve(options.resultPath);
  const before = {
    head: await gitText(repoRoot, ["rev-parse", "HEAD"]),
    branch: await gitText(repoRoot, ["branch", "--show-current"]),
    status: await gitText(repoRoot, ["status", "--porcelain"]),
  };
  if (before.status) throw new Error("source-worktree-must-be-clean");
  if (resultPath === repoRoot || resultPath.startsWith(`${repoRoot}${sep}`)) {
    throw new Error("result-path-must-be-outside-source-worktree");
  }

  const definitions = await loadCases(casesRoot);
  const instructionPaths = [
    join(repoRoot, "AGENTS.md"),
    join(repoRoot, "docs/development/agent-loop.md"),
    join(repoRoot, "docs/development/definition-of-done.md"),
  ];
  const harnessPaths = [new URL(import.meta.url).pathname, ...definitions.map((definition) => definition.manifestPath)];
  const root = await mkdtemp(join(tmpdir(), "dot-orbit-agent-eval-"));
  const runs = [];
  try {
    for (let trial = 1; trial <= options.trials; trial += 1) {
      for (const definition of definitions) {
        const worktree = join(root, `trial-${trial}`, definition.id);
        await mkdir(dirname(worktree), { recursive: true });
        await git(repoRoot, ["worktree", "add", "--detach", worktree, before.head], { timeoutMilliseconds: 120_000 });
        const startedAt = Date.now();
        let toolFailures = 0;
        let correctionCycles = 0;
        let candidate = await invokeCandidate({ ...options, definition, worktree, prompt: definition.prompt });
        if (candidate.exitCode !== 0) toolFailures += 1;
        let grade = await evaluateChecks(definition, worktree, before.head);
        const firstPassPassed = grade.passed;
        while (!grade.passed && correctionCycles < options.maxCorrections) {
          correctionCycles += 1;
          const failedChecks = grade.checks.filter((check) => !check.passed).map((check) => check.id);
          candidate = await invokeCandidate({
            ...options,
            definition,
            worktree,
            prompt: correctionPrompt(definition, failedChecks, correctionCycles),
          });
          if (candidate.exitCode !== 0) toolFailures += 1;
          grade = await evaluateChecks(definition, worktree, before.head);
        }
        const independentReview = await reviewRun({ ...options, definition, worktree });
        runs.push({
          caseId: definition.id,
          category: definition.category,
          trial,
          passed: grade.passed,
          firstPassPassed,
          points: grade.points,
          maximumPoints: grade.maximumPoints,
          checks: grade.checks,
          correctionCycles,
          unrelatedFileChurn: grade.unrelatedFileChurn,
          changedPathCount: grade.changedPathCount,
          elapsedMilliseconds: Date.now() - startedAt,
          toolFailures,
          candidateExitCode: candidate.exitCode,
          candidateTimedOut: candidate.timedOut,
          candidateOutputBytesDiscarded: candidate.outputBytes,
          independentReview,
        });
        await git(repoRoot, ["worktree", "remove", "--force", worktree], { timeoutMilliseconds: 120_000 });
      }
    }
  } finally {
    await rm(root, { recursive: true, force: true });
    await git(repoRoot, ["worktree", "prune"]);
  }

  const after = {
    head: await gitText(repoRoot, ["rev-parse", "HEAD"]),
    branch: await gitText(repoRoot, ["branch", "--show-current"]),
    status: await gitText(repoRoot, ["status", "--porcelain"]),
  };
  if (after.head !== before.head || after.branch !== before.branch || after.status !== before.status) {
    throw new Error("source-worktree-mutated");
  }

  const result = {
    schema: resultSchema,
    recordedAt: new Date().toISOString(),
    sourceCommit: before.head,
    instructionVersion: await fingerprint(instructionPaths),
    harnessVersion: await fingerprint(harnessPaths),
    configuration: {
      trials: options.trials,
      maximumCorrectionCycles: options.maxCorrections,
      caseCount: definitions.length,
      independentReviewerConfigured: Boolean(options.reviewerCommand),
      candidateIdentity: options.candidateIdentity,
    },
    aggregate: summarize(runs),
    runs,
    limitations: [
      "Candidate stdout and stderr are discarded; toolFailures counts failed, timed-out, or unspawnable candidate invocations rather than internal tool-call errors.",
      options.reviewerCommand
        ? "Independent review is process-exit based; review prose and raw traces are not retained."
        : "No independent reviewer was configured; every run records independentReview as unavailable.",
      "The suite is a small regression signal for repository-agent behaviour, not a product-quality or model leaderboard score.",
    ],
  };
  await mkdir(dirname(resultPath), { recursive: true });
  await writeFile(resultPath, `${JSON.stringify(result, null, 2)}\n`, { flag: "wx" });
  return result;
}
