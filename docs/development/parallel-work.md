# Parallel issue work

Use two independent implementing chats initially. Each owns one issue, a dedicated branch, and a separate directory. Git worktrees are the default; separate clones are also supported. A branch or a new chat by itself does not isolate files.

## Start two chats

1. Choose two independent `ready-for-agent` issues. Check their declared dependencies and likely overlap in components, schema, shared interfaces, and documentation. Sequence dependent or substantially overlapping changes. The human assigns one issue to each chat; ownership follows [`issue-tracker.md`](../agents/issue-tracker.md).
2. In Codex, start the first chat in **Worktree** mode from the agreed current base, normally `origin/main`. Start the second chat in another worktree from that same base. Do not include unrelated uncommitted changes. Each agent confirms its actual directory, base SHA, and dedicated `codex/` branch before editing; create the branch if Codex starts detached. Keep the existing local checkout available for human review and integration.
3. Give each chat this prompt, substituting its own issue number:

   > Implement issue #123 following the repository workflow. Use this chat's separate worktree and a dedicated branch. Confirm ownership, run the checkout setup, and preserve other chats' work. Do not merge.

4. In each checkout, run:

   ```sh
   node tools/setup-workspace.mjs
   ```

   The command resolves its own checkout, runs locked restore, and generates the local sample when absent. It preserves an existing sample and its recovery companion and refuses an orphan companion. It does not install runtimes, copy ignored files or credentials, change branches, fetch, or replace configuration. Install the repository-pinned prerequisites through the existing development environment before running it. A failed setup stops implementation until resolved. Run `--help` for usage.
5. Record ownership, restate the issue's acceptance criteria and non-goals, and implement each issue independently. Keep verification and handoff evidence with that issue and its exact tested commit.

Separate clones use the same steps, each with its own branch based on the agreed remote base. Fetch and update remote references in each clone. For worktrees, objects and branch references are shared: use unique branches, and avoid repository-wide configuration changes, pruning, resets, or operations on another chat's branch. A worktree is file isolation, not a security sandbox.

## Builds and local state

Run setup, builds, and tests in the owning checkout. Keep build output, sample databases, recovery companions, and temporary outputs checkout-local or uniquely allocated. Generate samples separately; do not share or symlink writable app data between chats, and do not copy a real workspace into development directories.

Launch a desktop build for human review from its owning checkout:

```sh
dotnet run --project src/DotOrbit.Desktop/DotOrbit.Desktop.csproj -- --sample-workspace
```

Identify the issue, branch, commit, and checkout alias when presenting it. Review one app at a time when two instances would be confusing; stop only the instance owned by that chat. Keep the existing UI approval and accessibility requirements. See [`sample-workspace.md`](sample-workspace.md) for sample details.

Concurrent read-only investigation or review can share a checkout with its designated writer. Every additional implementing sub-agent needs a separate directory and branch. The parent coordinates bounded subtasks; it does not give multiple agents ownership of the same implementation.

## Integrate and finish

1. Each chat completes the existing [`agent-loop.md`](agent-loop.md) verification and publication process. Human approval is still required for merging, tags, and releases.
2. The human, or an explicitly designated coordinating chat, chooses merge order. After a related PR merges, update the remaining branch against the agreed current base before final integration. Resolve conflicts, inspect semantic interactions even when Git reports none, and rerun affected checks plus the required publication gate on the updated commit. Earlier evidence does not verify a changed commit.
3. For a shared interface or dependency discovered during implementation, record the overlap in both issues and return the sequencing decision to the coordinator. Resume dependent implementation only after its required contract is agreed and available; do not silently absorb another issue.
4. On pause or reassignment, fill in [the handoff template](../../.github/HANDOFF_TEMPLATE.md) and update the ownership record. Preserve absolute directories privately and publish only non-sensitive checkout aliases.
5. After completion, preserve commits and any needed untracked or ignored files, close the owning review app, and release the claim. Archive Codex-managed worktrees through Codex. Remove manually managed worktrees or clones only after confirming that no other chat uses them and that all needed work and local data are preserved. Do not force-remove a dirty directory or assume ignored samples are included in a Git commit or archive.

## Verify workflow changes

Run `node --test tools/setup-workspace.test.mjs` for the setup boundaries. Material changes to these instructions, setup orchestration, or the handoff template also follow [`evaluations/agent-loop/README.md`](../../evaluations/agent-loop/README.md): reference candidate first, then a real agent baseline with at least two trials, from the clean evaluated commit. The canonical application gate remains unchanged.
