import { spawn } from "node:child_process";
import { lstat } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const checkoutRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

export function usage() {
  return `Usage: node tools/setup-workspace.mjs [--help]

Run locked restore and generate this checkout's sample workspace only when absent.
Existing sample data is preserved. An orphan recovery companion requires manual
resolution. Run the script from the intended checkout; no branch or Git changes
are made. Requires the repository-pinned .NET SDK and Node.js environment.`;
}

async function entryKind(path) {
  try {
    const info = await lstat(path);
    return info.isFile() ? "file" : "other";
  } catch (error) {
    if (error.code === "ENOENT") return "absent";
    throw error;
  }
}

function runProcess(command, args, { cwd }) {
  return new Promise((resolveResult, reject) => {
    const child = spawn(command, args, { cwd, stdio: "inherit", shell: false });
    child.once("error", reject);
    child.once("exit", (code) => resolveResult(code ?? 1));
  });
}

// The root and process boundary are explicit so tests can prove checkout isolation
// and failure propagation without restoring dependencies or touching real data.
export async function setupWorkspace({ root = checkoutRoot, run = runProcess } = {}) {
  root = resolve(root);
  if (await entryKind(join(root, "DotOrbit.slnx")) !== "file") {
    throw new Error("checkout-not-found");
  }
  const sample = join(root, "artifacts", "sample-workspace", "workspace.orb");
  const companion = `${sample}.recovery-state.json`;
  const sampleKind = await entryKind(sample);
  const companionKind = await entryKind(companion);
  if (sampleKind === "other" || companionKind === "other") {
    throw new Error("sample-path-not-regular-file");
  }
  if (sampleKind === "absent" && companionKind !== "absent") {
    throw new Error("sample-recovery-companion-without-workspace");
  }
  const restore = await run("dotnet", [
    "restore", "DotOrbit.slnx", "--locked-mode", "--configfile", "NuGet.Config",
  ], { cwd: root });
  if (restore !== 0) throw new Error("locked-restore-failed");

  if (sampleKind === "file") return "preserved";
  const generation = await run("dotnet", [
    "run", "--project", "tools/DotOrbit.SampleWorkspace/DotOrbit.SampleWorkspace.csproj",
    "--no-restore", "-p:RestoreLockedMode=true",
  ], { cwd: root });
  if (generation !== 0) throw new Error("sample-generation-failed");
  if (await entryKind(sample) !== "file") throw new Error("sample-not-created");
  return "generated";
}

export async function main(args) {
  if (args.length === 1 && args[0] === "--help") {
    console.log(usage());
    return 0;
  }
  if (args.length !== 0) {
    console.error("workspace-setup: result=failed reason=invalid-arguments");
    return 2;
  }
  try {
    const sample = await setupWorkspace();
    console.log(`workspace-setup: result=passed sample=${sample}`);
    return 0;
  } catch (error) {
    const knownReasons = new Set([
      "checkout-not-found", "sample-path-not-regular-file",
      "sample-recovery-companion-without-workspace", "locked-restore-failed",
      "sample-generation-failed", "sample-not-created",
    ]);
    const reason = knownReasons.has(error.message) ? error.message : "setup-error";
    console.error(`workspace-setup: result=failed reason=${reason}`);
    return 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = await main(process.argv.slice(2));
}
