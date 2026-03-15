# MambaSplit Workspace Agent Guidance

## Scope
- This file applies to all repositories under `C:\MambaSplit`.
- Repository-level `AGENTS.md` files may add stricter or more specific rules.
- When rules conflict, the closest `AGENTS.md` to the target repo wins.

## Multi-Repo Lockstep Policy
- This policy is governed in lockstep across three files:
  - C:\MambaSplit\MambaSplit.Api\AGENTS.md
  - C:\MambaSplit\agent-templates\AGENTS.md
  - C:\MambaSplit\mambasplit-web\AGENTS.md
- Any policy-level AGENTS change must be applied consistently to all three files unless explicitly scoped otherwise.
- If a conflict exists between the three files, stop and report:
  1. conflicting section
  2. proposed canonical wording
  3. files requiring reconciliation

## Workflow Ownership Model
- AGENTS.md defines policy requirements and invariants.
- Custom agents implement operational runbooks.
- Do not duplicate long procedural runbooks in AGENTS.md when the same logic is owned by a custom agent.
- Required workflow outcomes:
  1. Branch creation from issue metadata must enforce naming guardrails and base from develop.
  2. Commits on issue-numbered feature, bugfix, hotfix, and chore branches must include Refs #<issue-number> in commit body.
  3. PRs from working branches must target develop and follow required title and description conventions.
  4. Finalization flow must include commit, sync, push, and PR creation evidence.
- Agent responsibilities are defined in the Custom Agent Catalog section below.

## Issue Linking Rules
- Linking commits/PRs to issues does not require a GitHub Action workflow by default.
- Any commit made on `feature/*`, `bugfix/*`, `hotfix/*`, or `chore/*` branches that include an issue number in the branch name must include `Refs #<issue-number>` in the commit body.
- GitHub automatically links when commit bodies or PR descriptions include references like `#123`.
- GitHub automatically closes issues when PR descriptions include keywords such as `Closes #123`, `Fixes #123`, or `Resolves #123` and the PR is merged into the default branch.
- If strict enforcement is needed, add a separate workflow or branch rule that fails PRs missing required issue references.
- Labels can be used for triage/automation, but labels do not replace required PR title conventions or issue references.

## Branch Strategy and Protection
- Every repository should use a two-base-branch model:
  - `main`: production-ready history.
  - `develop`: integration branch for ongoing work.
- Feature, bugfix, hotfix, and chore branches are created from `develop`.
- Merge flow:
  1. Feature branch -> `develop` via PR.
  2. `develop` -> `main` via PR.
- `main` must not accept direct pushes.
- `main` should only receive PRs whose source branch is `develop`.

### Required Workflow Template: `.github/workflows/restrict-main-merges.yml`
Use this template in each repo:

```yaml
name: Restrict Main Merges

on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, reopened, edited, ready_for_review]

permissions:
  contents: read

jobs:
  allow-only-develop-into-main:
    runs-on: ubuntu-latest
    steps:
      - name: Validate source branch for main PRs
        run: |
          echo "Base branch: ${{ github.base_ref }}"
          echo "Head branch: ${{ github.head_ref }}"
          if [ "${{ github.base_ref }}" = "main" ] && [ "${{ github.head_ref }}" != "develop" ]; then
            echo "Only PRs from 'develop' into 'main' are allowed."
            exit 1
          fi
```

Branch protection requirements for `main` should also include:
- Require pull request before merging.
- Require status checks to pass, including `Restrict Main Merges / allow-only-develop-into-main`.
- Restrict who can push to `main` (or disable direct pushes entirely).

## Failure Behavior
- On any conflict or failure, stop immediately.
- Report the exact failed command and error.
- Do not auto-resolve conflicts or retry with alternate strategies.

## Branch Naming Guardrails
- Branches created by `feature_start` must match:
  `^(feature|bugfix|hotfix|chore)/[0-9]+-[a-z0-9]+(?:-[a-z0-9]+)*$`
- `<issue-number>` must be numeric.
- Slug must be lowercase letters, numbers, hyphens only.

## Custom Agent Catalog
- Combined workflow agent:
  - feature-workflow-manager
- Review agent:
  - risk-first-pr-reviewer
- Frontend visual implementation agent:
  - ui-visual-implementer
- When these agents are available, prefer them over ad-hoc execution for their scope.
- Agents must respect all safety, issue-linking, branch strategy, and failure behavior rules defined in this file.

## Startup and Test Defaults
- Prefer project-provided scripts if present.
- For Java projects, prefer wrapper commands (`mvnw`, `gradlew`) over global installs.
- For local infra, verify Docker daemon before running compose commands.
- Before committing in consuming repositories, run `./scripts/sync-agents.ps1` to sync `.github/agents` from `agent-templates/agents`.

## Safety Rules
- Never run destructive git commands unless explicitly requested.
- Stop and ask before force-push.
- On merge/rebase conflicts, stop and report conflicted files.

## Communication
- Keep responses concise and action-oriented.
- Report what changed, what was verified, and what could not be verified.
- For workflow and review tasks, use the designated custom agent when available and report policy compliance explicitly.

## Coding Philosophy

### Keep Implementation Simple
- ALWAYS choose the simplest solution that solves the problem.
- AVOID over-engineering or adding unnecessary abstractions.
- PREFER straightforward code over clever/complex implementations.
- RULE OF THUMB: If you're adding more than 3 classes for a simple feature, you're probably overthinking it.

### Ask Questions Before Building Complex Solutions
- STOP and ask clarifying questions if you're about to write 100+ lines of code.
- ASK before creating:
  - Multiple new files.
  - New classes.
  - Architectural changes.
- CONFIRM REQUIREMENTS before implementing complex patterns (factories, strategies, builders, etc.).
- REMEMBER: More questions = better outcome. Err on the side of asking too much rather than assuming.

### Documentation: Only When Explicitly Requested
- DO NOT create README.md, documentation files, or guides unless specifically asked.
- DO NOT write extensive comments explaining obvious code.
- ONLY add documentation or comments for:
  - Public APIs.
  - Complex business logic.
- NOTE: When users say "build X," they mean code only, not code plus documentation.

## Documentation Style

### Use Arabic Numerals for All Numbered Lists
- When creating numbered lists in documentation, ALWAYS use Arabic numerals (1, 2, 3, etc.).
- DO NOT use Roman numerals (I, II, III, IV, etc.) for any numbered lists.
- This applies to:
  - Procedure steps.
  - Enumerated items.
  - Sequential instructions.
  - Any ordered list.
- DO NOT convert bullet points to numbered lists unless the ordering is meaningful.
- KEEP bullet points (-, *) for unordered lists where sequence doesn't matter.

### Function and Stored Procedure Parameters
- When documenting stored procedures, SQL queries, or function parameters, ALWAYS use Arabic numerals (1, 2, 3, etc.).
- DO NOT use Roman numerals (I, II, III, etc.) or ordered lists.
- DO NOT use the word "Parameter" in the list.
- FORMAT: "1:", "2:", "3:", etc.
- For output parameters, add (OUT) after the number: "1 (OUT):"
- Example:
  ```
  1 (OUT): Return code
  2: security_id
  3: trader
  ```

## Shell Environment (Windows)

### PowerShell Requirements
- Commands provided must be executed in PowerShell on Windows (not cmd.exe).
- Use PowerShell-compatible syntax:
  - Use `Set-Location` or `cd` (do not use /d).
  - Quote paths with spaces/special characters: `Set-Location "C:\path with spaces"`.
  - Use `;` to chain commands (do not use `&&`).
  - Use `>` or `Out-File` for redirection.

### PowerShell File Operations
Correct approach:
```powershell
Get-Content "C:\full\path\to\file.java" | Select-Object -First 50
Get-Content "C:\full\path\to\file.java" | Select-Object -Skip 50 -First 100
```

Avoid these approaches:
- Do NOT use `&&` operator (not valid in PowerShell):
  ```powershell
  cd C:\path && dir /b
  ```
- Do NOT use `/b` switch with semicolon in complex paths (parsing issues):
  ```powershell
  cd C:\Long\path\with\many\segments; dir /b
  ```

Why the correct approach works:
- `Get-Content` with quoted full paths avoids path parsing issues.
- `Select-Object -First N` safely reads the first N lines.
- `Select-Object -Skip N -First M` safely reads specific line ranges.
- No directory navigation is needed; direct file access is simpler and more reliable.

