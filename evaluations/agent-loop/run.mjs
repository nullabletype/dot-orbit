#!/usr/bin/env node
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runEvaluation } from "./harness.mjs";

function usage() {
  return `Usage:
  node evaluations/agent-loop/run.mjs \\
    --candidate <executable> [--candidate-arg <argument> ...] \\
    --candidate-identity <non-sensitive label> \\
    --results <absolute path outside the repository> \\
    [--trials <count>] [--max-corrections <count>] \\
    [--reviewer <executable> --reviewer-arg <argument> ...]

The candidate and optional reviewer receive their prompt on stdin and run with
the isolated case worktree as their current directory. Raw process output is
discarded and is never written to the result record.`;
}

export function parseArguments(args) {
  const options = { candidateArgs: [], reviewerArgs: [], trials: 2, maxCorrections: 1 };
  const values = [...args];
  while (values.length > 0) {
    const option = values.shift();
    if (option === "--help") return { help: true };
    const value = values.shift();
    if (value === undefined) throw new Error(`missing-value:${option}`);
    if (option === "--candidate") options.candidateCommand = value;
    else if (option === "--candidate-arg") options.candidateArgs.push(value);
    else if (option === "--candidate-identity") options.candidateIdentity = value;
    else if (option === "--results") options.resultPath = value;
    else if (option === "--trials") options.trials = Number(value);
    else if (option === "--max-corrections") options.maxCorrections = Number(value);
    else if (option === "--reviewer") options.reviewerCommand = value;
    else if (option === "--reviewer-arg") options.reviewerArgs.push(value);
    else throw new Error(`unknown-option:${option}`);
  }
  for (const field of ["candidateCommand", "candidateIdentity", "resultPath"]) {
    if (!options[field]) throw new Error(`missing-option:${field}`);
  }
  if (!Number.isInteger(options.trials) || options.trials < 2 || options.trials > 10) {
    throw new Error("trials:expected-2-to-10");
  }
  if (!Number.isInteger(options.maxCorrections) || options.maxCorrections < 0 || options.maxCorrections > 3) {
    throw new Error("max-corrections:expected-0-to-3");
  }
  return options;
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  if (options.help) {
    console.log(usage());
    return;
  }
  const scriptDirectory = dirname(fileURLToPath(import.meta.url));
  const repoRoot = resolve(scriptDirectory, "../..");
  const result = await runEvaluation({
    ...options,
    repoRoot,
    casesRoot: resolve(scriptDirectory, "cases"),
    timeoutMilliseconds: 600_000,
  });
  console.log(`agent-evaluation: result=${result.aggregate.successfulRuns === result.aggregate.totalRuns ? "passed" : "failed"} runs=${result.aggregate.successfulRuns}/${result.aggregate.totalRuns} first-pass=${result.aggregate.firstPassGateRate.toFixed(3)} results=${resolve(options.resultPath)}`);
  if (result.aggregate.successfulRuns !== result.aggregate.totalRuns) process.exitCode = 1;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(`agent-evaluation: result=failed reason=${error.message}`);
    process.exitCode = 1;
  });
}
