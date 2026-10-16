import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { setupWorkspace } from "./setup-workspace.mjs";

const script = join(dirname(fileURLToPath(import.meta.url)), "setup-workspace.mjs");

async function fixture(t, name = "checkout with spaces") {
  const parent = await mkdtemp(join(tmpdir(), "dot-orbit-setup-"));
  t.after(() => rm(parent, { recursive: true, force: true }));
  const root = join(parent, name);
  await mkdir(join(root, "artifacts", "sample-workspace"), { recursive: true });
  await writeFile(join(root, "DotOrbit.slnx"), "fixture");
  // Linked worktrees have a .git file, not a .git directory.
  await writeFile(join(root, ".git"), "gitdir: fixture-metadata");
  const sample = join(root, "artifacts", "sample-workspace", "workspace.orb");
  return { root, sample, companion: `${sample}.recovery-state.json` };
}

function runner(f, { restore = 0, generation = 0, create = true } = {}) {
  const calls = [];
  return {
    calls,
    run: async (command, args, options) => {
      calls.push({ command, args, options });
      assert.equal(command, "dotnet");
      assert.equal(options.cwd, f.root);
      if (args[0] === "restore") return restore;
      if (generation === 0 && create) await writeFile(f.sample, "synthetic sample");
      return generation;
    },
  };
}

test("sets up two independent worktree directories, including paths with spaces", async (t) => {
  const first = await fixture(t);
  const second = await fixture(t, "second checkout");
  for (const f of [first, second]) {
    const process = runner(f);
    assert.equal(await setupWorkspace({ root: f.root, run: process.run }), "generated");
    assert.deepEqual(process.calls.map(({ args }) => args), [
      ["restore", "DotOrbit.slnx", "--locked-mode", "--configfile", "NuGet.Config"],
      ["run", "--project", "tools/DotOrbit.SampleWorkspace/DotOrbit.SampleWorkspace.csproj",
        "--no-restore", "-p:RestoreLockedMode=true"],
    ]);
  }
  await writeFile(first.sample, "first checkout edits");
  assert.equal(await readFile(second.sample, "utf8"), "synthetic sample");
});

test("repeated setup preserves existing sample and recovery companion byte-for-byte", async (t) => {
  const f = await fixture(t);
  const bytes = Buffer.from([0, 255, 1, 123, 10]);
  await writeFile(f.sample, bytes);
  await writeFile(f.companion, "existing recovery state");
  const process = runner(f);
  for (let count = 0; count < 2; count++) {
    assert.equal(await setupWorkspace({ root: f.root, run: process.run }), "preserved");
  }
  assert.equal(process.calls.length, 2);
  assert.ok(process.calls.every(({ args }) => args[0] === "restore"));
  assert.deepEqual(await readFile(f.sample), bytes);
  assert.equal(await readFile(f.companion, "utf8"), "existing recovery state");
});

test("refuses an orphan recovery companion before starting processes", async (t) => {
  const f = await fixture(t);
  await writeFile(f.companion, "preserve me");
  const process = runner(f);
  await assert.rejects(setupWorkspace({ root: f.root, run: process.run }),
    /sample-recovery-companion-without-workspace/);
  assert.equal(process.calls.length, 0);
  assert.equal(await readFile(f.companion, "utf8"), "preserve me");
});

test("restore failure prevents sample generation", async (t) => {
  const f = await fixture(t);
  const process = runner(f, { restore: 1 });
  await assert.rejects(setupWorkspace({ root: f.root, run: process.run }), /locked-restore-failed/);
  assert.equal(process.calls.length, 1);
  await assert.rejects(readFile(f.sample), { code: "ENOENT" });
});

test("generation failure and missing output cannot report success", async (t) => {
  for (const options of [{ generation: 1 }, { create: false }]) {
    const f = await fixture(t);
    const process = runner(f, options);
    await assert.rejects(setupWorkspace({ root: f.root, run: process.run }),
      options.generation ? /sample-generation-failed/ : /sample-not-created/);
  }
});

test("rejects invalid checkout and non-file sample paths without running processes", async (t) => {
  const f = await fixture(t);
  const process = runner(f);
  await assert.rejects(setupWorkspace({ root: dirname(f.root), run: process.run }), /checkout-not-found/);
  await mkdir(f.sample);
  await assert.rejects(setupWorkspace({ root: f.root, run: process.run }), /sample-path-not-regular-file/);
  assert.equal(process.calls.length, 0);
});

test("help and invalid CLI arguments are safe from another working directory", () => {
  const help = spawnSync(process.execPath, [script, "--help"], { cwd: tmpdir(), encoding: "utf8" });
  assert.equal(help.status, 0);
  assert.match(help.stdout, /Usage:/);
  const invalid = spawnSync(process.execPath, [script, "--reset"], { cwd: tmpdir(), encoding: "utf8" });
  assert.equal(invalid.status, 2);
  assert.match(invalid.stderr, /invalid-arguments/);
  assert.equal(resolve(script), script);
});
