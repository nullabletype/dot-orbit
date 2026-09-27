#!/usr/bin/env bash
set -euo pipefail

# Supply only read-only API responses; never call the installed GitHub CLI.
gh() {
  [[ "$1" == api ]] || return 2
  case "$2" in
    */rules/branches/main\?*)
      if [[ "$AUDIT_CASE" == no-effective-ruleset ]]; then
        printf '%s\n' '[]'
      elif [[ "$AUDIT_CASE" == multiple-rulesets ]]; then
        printf '%s\n' '[{"type":"pull_request","ruleset_source_type":"Repository","ruleset_source":"nullabletype/dot-orbit","ruleset_id":7},{"type":"deletion","ruleset_source_type":"Repository","ruleset_source":"nullabletype/dot-orbit","ruleset_id":8}]'
      else
        printf '%s\n' '[{"type":"pull_request","ruleset_source_type":"Repository","ruleset_source":"nullabletype/dot-orbit","ruleset_id":7}]'
      fi
      ;;
    */rulesets/7)
      jq -n --arg case "$AUDIT_CASE" '{
        source_type: "Repository",
        source: "nullabletype/dot-orbit",
        target: (if $case == "wrong-target" then "tag" else "branch" end),
        enforcement: (if $case == "inactive" then "disabled" else "active" end),
        conditions: {ref_name: {
          include: (if $case == "wrong-ref" then ["~ALL"] else ["refs/heads/main"] end),
          exclude: []
        }},
        bypass_actors: (if $case == "bypass" then [{actor_type: "RepositoryRole", actor_id: 5, bypass_mode: "always"}] else [] end),
        rules: ([
          {type: "pull_request", parameters: {
            required_review_thread_resolution: ($case != "unresolved"),
            required_approving_review_count: (if $case == "approval" then 1 else 0 end),
            allowed_merge_methods: (if $case == "missing-merge-method" then ["squash"] else ["merge", "squash", "rebase"] end)
          }},
          {type: "deletion"},
          {type: "non_fast_forward"},
          {type: "required_status_checks", parameters: {
            strict_required_status_checks_policy: ($case != "non-strict"),
            do_not_enforce_on_create: false,
            required_status_checks: (
            ["ubuntu-24.04", "windows-2025", "macos-26"]
            | if $case == "missing-check" then .[:2] else . end
            | map({context: ., integration_id: (if $case == "wrong-source" then 0 else 15368 end)})
          )}}
        ]
        | if $case == "missing-deletion" then map(select(.type != "deletion")) else . end
        | if $case == "missing-force-push-block" then map(select(.type != "non_fast_forward")) else . end)
      }'
      ;;
    *) return 2 ;;
  esac
}
export -f gh

for test_case in \
  valid \
  no-effective-ruleset \
  multiple-rulesets \
  wrong-target \
  inactive \
  wrong-ref \
  bypass \
  unresolved \
  approval \
  missing-check \
  wrong-source \
  non-strict \
  missing-merge-method \
  missing-deletion \
  missing-force-push-block; do
  export AUDIT_CASE="$test_case"
  if bash tools/audit-main-ruleset.sh >/dev/null 2>&1; then
    actual=0
  else
    actual=$?
  fi
  expected=1
  [[ "$test_case" == valid ]] && expected=0
  if [[ "$actual" -ne "$expected" ]]; then
    echo "FAIL: $test_case returned $actual, expected $expected" >&2
    exit 1
  fi
  echo "PASS: $test_case"
done
