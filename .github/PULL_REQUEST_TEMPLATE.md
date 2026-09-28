## Outcome

What user-visible or architectural outcome does this change deliver?

## Issue

Closes #

## Acceptance criteria and evidence

| Acceptance criterion | Evidence |
| --- | --- |
| | |

## Verification

- Canonical gate: `dotnet run --project tools/DotOrbit.Verification/DotOrbit.Verification.csproj -p:RestoreLockedMode=true -- --evidence --expected-sha <full-commit-sha>`
- Result:
- Manual checks:
- Tested commit:

## Accessibility impact

Describe changed user actions, keyboard operation, accessible names/roles/states, focus order, announcements, and non-colour cues. Write "None" when the change has no accessibility surface.

## Dependency-baseline impact

List changed dependencies, SDKs, runtimes, actions, runner images, or packaging tools and the dated upstream support/security evidence. Write "None" when the baseline is unchanged.

## Independent review

- Reviewer:
- Status: not requested / requested / completed
- Findings and resolution:

## Residual risk

What remains uncertain, unreviewed, or deliberately out of scope?
