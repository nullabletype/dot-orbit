# Main branch protection

Issue #29 divides the merge boundary between checked-in workflows and live GitHub settings. The workflow files control run cancellation, job timeouts, and `GITHUB_TOKEN` permissions. A repository ruleset in GitHub Settings controls who can change `main` and which checks must pass. Changing this document or the workflows alone does not protect the branch.

## Workflow controls

- `Build` and `Release packages` cancel an older run of the same workflow for the same pull request, or for the same ref on push/manual dispatch. The workflow name in the concurrency group keeps the two workflows independent.
- Each `Build` matrix job has a 60-minute limit; each `Release packages` matrix job has a 45-minute limit. The packaged desktop smoke step retains its shorter two-minute limit.
- Both workflows grant only `contents: read` to `GITHUB_TOKEN`. The manual release-package upload uses the checked-in Release archive and does not require a write permission. See [GitHub artifact policy](github-artifact-policy.md).

## Live ruleset for `main`

Configure one active repository branch ruleset targeting `refs/heads/main` with:

- Require a pull request before merging, with zero required approving reviews while independent review is unavailable.
- Permit the repository's existing merge, squash, and rebase methods in the pull-request rule.
- Require all code review conversations to be resolved.
- Require these three stable check names from the `Build` matrix: `ubuntu-24.04`, `windows-2025`, and `macos-26`. Select GitHub Actions as the source for each check, require the branch to be up to date before merging, and enforce the checks on branch creation. Do not require `Release packages`: its path-filtered pull-request workflow does not run on every change.
- Restrict branch deletion and force pushes.
- No bypass actors. Ordinary and administrator merges must satisfy the same rules. An emergency requires the repository owner to make an explicit temporary ruleset edit, record the reason and affected change, then restore the ruleset immediately and rerun the audit. There is no standing emergency bypass.

Do not add a required human approval count until that review policy is decided. This ruleset does not configure automatic merging or release/tag protection.

## Rollout and verification

1. Merge the workflow controls first and confirm that the three `Build` check names appear on a fresh pull request. A required context must be observed before it is selected in the ruleset.
2. Keep the existing classic branch protection while configuring the active ruleset in repository Settings. Its current pull-request requirement has zero approvals but does not require conversation resolution or checks and does not enforce administrators. Select the observed GitHub Actions checks in the new ruleset. Confirm a fully green pull request can merge using the existing merge methods under the zero-approval policy.
3. Run the read-only audit from a checkout with GitHub CLI authentication, `jq`, and repository administration visibility:

   ```sh
   bash tools/audit-main-ruleset.sh
   ```

   The audit reads the effective rules for `main` and then fetches the applicable repository ruleset to check its exact `main` target, active state, GitHub Actions-owned required contexts, strict review rule, deletion/force-push rules, and empty bypass list. GitHub may omit `bypass_actors` for callers without ruleset write visibility; the audit fails closed in that case. It changes no settings.
4. Use the effective-rules API evidence from the audit to verify that pull requests, required checks, deletion protection, and non-fast-forward protection apply to `main`. Confirm conversation resolution with an unresolved thread on a disposable pull request, then resolve it and confirm the fully green pull request is mergeable. Never test deletion, force-push, or direct-push rejection against live `main`: a configuration mistake would make that probe destructive. If end-to-end rejection behaviour needs separate proof, reproduce the policy on a disposable repository or temporary test branch and remove that test configuration afterwards. Only after ruleset parity and effective enforcement are verified, remove the redundant classic protection and rerun the audit. Start a superseding run for one pull request and confirm the older run is cancelled. Check the job timeout values in the workflow and a live run's settings; waiting for an hour solely to prove a timeout is unnecessary.

Rerun the audit after any ruleset edit and during periodic repository maintenance. Review the workflow files and check names at the same time: the API audit checks the live ruleset, while the workflow files are the source for concurrency, timeout, and token permissions. If a check name changes, observe its replacement on a pull request before changing the required list, then update this document and the audit together.
