---
description: Feature-branch lifecycle automation — start from a GitHub issue, commit & sync, finalize a PR into develop, or prepare a develop→main release PR.
argument-hint: start <issue#> | commit | finalize [next-issue#] | release
---

Requested action: $ARGUMENTS

Resolve $ARGUMENTS to one of the four `feature-workflow-manager` flows:
- `start <issue#>` → **feature_start** for that GitHub issue number.
- `commit` (also accept `commit-sync`, `sync`) → **feature_commit** on the current branch.
- `finalize [next-issue#]` → **feature_finalize** on the current branch; if a next issue number is supplied, chain into **feature_start** for it once finalize completes.
- `release` (also accept `prepare-release`) → **prepare_release** (develop → main).

If $ARGUMENTS is empty or doesn't clearly match one of these, ask the user which flow they want instead of guessing.

Delegate the resolved flow to the `feature-workflow-manager` subagent via the Agent tool (`subagent_type: feature-workflow-manager`), passing the specific issue number(s) and any other relevant context from $ARGUMENTS. Do not run the git/gh lifecycle steps directly in this context — the subagent owns the guardrails in `.claude/agents/feature-workflow-manager.md` (branch naming pattern, `Refs #<issue-number>` in commit bodies, no direct pushes to `develop`/`main`, polling CI to green before reporting finalize done, no force-push/destructive commands unless explicitly requested).

Once the subagent finishes, relay its report back to the user concisely (branch created, commit hash + push result, PR link + CI status, or release PR link, as applicable).
