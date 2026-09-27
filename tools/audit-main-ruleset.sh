#!/usr/bin/env bash
set -euo pipefail

repository="${1:-nullabletype/dot-orbit}"
if [[ ! "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
  echo 'Usage: tools/audit-main-ruleset.sh [owner/repository]' >&2
  exit 2
fi

for command in gh jq; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "Missing required command: $command" >&2
    exit 2
  fi
done

# This endpoint returns rules actually enforced on main, including inherited rules.
effective_rules="$(gh api "repos/$repository/rules/branches/main?per_page=100")"
candidate_ids="$(jq -r --arg repository "$repository" '
  .[]
  | select(.ruleset_source_type == "Repository" and .ruleset_source == $repository)
  | .ruleset_id
' <<< "$effective_rules" | sort -u)"

if [[ -z "$candidate_ids" ]]; then
  echo "FAIL: no repository ruleset is effective on $repository/main" >&2
  exit 1
fi
if [[ "$candidate_ids" == *$'\n'* ]]; then
  echo "FAIL: multiple repository rulesets are effective on $repository/main; inspect every bypass and required check" >&2
  exit 1
fi

while IFS= read -r ruleset_id; do
  [[ -n "$ruleset_id" ]] || continue
  ruleset="$(gh api "repos/$repository/rulesets/$ruleset_id")"

  if jq -e --arg repository "$repository" '
    .source_type == "Repository"
    and .source == $repository
    and .target == "branch"
    and .enforcement == "active"
    and ((.conditions.ref_name.include | sort) == ["refs/heads/main"])
    and (((.conditions.ref_name.exclude // []) | sort) == [])
    and (.bypass_actors == [])
    and ([.rules[].type] | index("pull_request") != null)
    and ([.rules[].type] | index("deletion") != null)
    and ([.rules[].type] | index("non_fast_forward") != null)
    and (any(.rules[]; .type == "pull_request"
      and .parameters.required_review_thread_resolution == true
      and .parameters.required_approving_review_count == 0
      and ([.parameters.allowed_merge_methods[]] | sort)
        == (["merge", "squash", "rebase"] | sort)))
    and (any(.rules[]; .type == "required_status_checks"
      and .parameters.strict_required_status_checks_policy == true
      and .parameters.do_not_enforce_on_create == false
      and ([.parameters.required_status_checks[] | {context, integration_id}] | sort_by(.context))
        == ([
          {context: "ubuntu-24.04", integration_id: 15368},
          {context: "windows-2025", integration_id: 15368},
          {context: "macos-26", integration_id: 15368}
        ] | sort_by(.context))))
  ' <<< "$ruleset" >/dev/null; then
    echo "PASS: active main ruleset $ruleset_id requires the Build matrix, pull requests and resolved conversations; deletion and force pushes are blocked; no bypass is configured"
    exit 0
  fi
done <<< "$candidate_ids"

echo 'FAIL: no effective repository ruleset satisfies the main protection policy' >&2
echo 'The audit needs ruleset read access that exposes bypass_actors; rerun with repository administration access if that field is omitted.' >&2
exit 1
