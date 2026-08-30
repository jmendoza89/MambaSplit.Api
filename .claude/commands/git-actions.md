---
description: Generate policy-compliant git branch names, commit messages, and PR artifacts, or run a risk-first review of a PR/diff.
argument-hint: [branch_name|commit_message|pr_body|review_short|review_detailed] <context>
---

Requested action and context: $ARGUMENTS

For multi-step branch/commit/PR lifecycle work (start an issue branch, commit+sync, finalize a PR, prepare a release), delegate to the `feature-workflow-manager` subagent instead of doing it inline here.

Guidelines:
- Apply the workspace branch naming guardrails:
  - Pattern: `^(feature|bugfix|hotfix|chore)/[0-9]+-[a-z0-9]+(?:-[a-z0-9]+)*$`.
  - Issue number must be numeric and included in the branch name.
- Commits on `feature/*`, `bugfix/*`, `hotfix/*`, or `chore/*` branches must include `Refs #<issue-number>` in the commit body.
- PRs merging into `main` must originate from `develop` (enforce in PR description and checklist).
- For `commit_message` and `pr_title` produce a short, descriptive summary (<=72 chars where possible).
- For `pr_body` include: Motivation, What changed, Testing done, Risks/regressions, Migration notes (if any), and a short checklist (e.g., `Refs #<issue-number>`, `Targets: develop`, `Tests: unit/integration`).
- For reviews (`review_short`, `review_detailed`) prioritize risk: correctness, security, data integrity, and public API stability. Provide actionable findings and minimal repro steps when possible.

Examples:
- Generate branch name:
  - Input: `action: branch_name`, `context: { type: "feature", issue: 42, title: "Add auto-rebalance when new member joins" }`
  - Output: `feature/42-auto-rebalance-when-new-member-joins`

- Create commit message + body:
  - Input: `action: commit_message`, `context: { summary: "add auto rebalance logic", issue: 42 }`
  - Output (commit message): `Auto-rebalance unsettled expenses when member joins (Refs #42)`
  - Output (commit body): include `Refs #42` and short bullet list of changes.

- Create PR title + body:
  - Input: `action: pr_body`, `context: { title, diff, issue: 42, target: "develop" }`
  - Output: full PR body following the checklist above; include `Targets: develop` and `Refs #42`.

- Risk-first PR review:
  - Input: `action: review_detailed`, `context: { pr_link or diff }`
  - Output: prioritized findings (Critical → High → Medium → Low), clear reproduction or code pointers, suggested fixes, and whether the PR should be merged to `develop` or requires changes.

Notes and enforcement:
- When generating branch/commit/PR artifacts, validate and correct names to match the guardrails. If an input cannot be transformed to a compliant value, return a short explanation and a suggested compliant alternative.
- Keep outputs concise and machine-friendly where possible (one-line branch name, JSON-like metadata for automation).
