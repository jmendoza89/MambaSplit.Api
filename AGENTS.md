# MambaSplit Workspace Agent Guidance

## Scope
- This file applies to all repositories under `C:\MambaSplit`.
- Repository-level `AGENTS.md` files may add stricter or more specific rules.
- When rules conflict, the closest `AGENTS.md` to the target repo wins.

## Standard Workflow Keywords
- `feature_commit`:
  1. Show `git status --short`.
  2. If there are no changes, stop and report "nothing to commit".
  3. Run `git add .`.
  4. Create a concise commit message based on the staged diff.
  5. Detect current branch with `git branch --show-current`.
  6. If the current branch matches `^(feature|bugfix|hotfix|chore)/([0-9]+)-`, commit with the generated message and include `Refs #<captured-issue-number>` in the commit body.
  7. If the current branch does not match that pattern, commit with the generated message.
  8. Run `git pull --rebase origin <current-branch>`.
  9. Run `git push origin <current-branch>`.
  10. Report commit hash and push result.
- `feature_start <issue-number>`:
  1. Validate `<issue-number>` is numeric.
  2. Fetch issue metadata (title + labels) from GitHub for this repo.
  3. Slugify issue title to lowercase kebab-case.
  4. Determine branch prefix from labels using this precedence:
     - if label includes `hotfix` -> prefix `hotfix`
     - else if label includes `bugfix` or `bug` -> prefix `bugfix`
     - else if label includes `chore` -> prefix `chore`
     - else -> prefix `feature`
  5. Build branch name as `<prefix>/<issue-number>-<slug>`.
  6. Run `git fetch origin`.
  7. Ensure local `develop` matches `origin/develop`.
  8. Create and checkout new branch from updated `develop` with generated name.
  9. Report issue number, title, labels, and created branch name.
  10. Ensure commits on this branch include issue references in commit bodies, for example: `Refs #<issue-number>`.
  11. Ensure the PR from this branch to `develop` includes an issue-closing keyword in the description, for example: `Closes #<issue-number>`.
- `feature_finalize [next-issue-number]`:
  1. Run `feature_commit`.
  2. Detect current branch name.
  3. Update remote `develop` from remote current branch with fast-forward only:
     - Use `git push origin origin/<current-branch>:develop` only if safe (fast-forward).
  4. If `[next-issue-number]` is provided, run `feature_start <next-issue-number>` to create and checkout the next branch; otherwise skip this step.

## Issue Linking Rules
- Linking commits/PRs to issues does not require a GitHub Action workflow by default.
- Any commit made on `feature/*`, `bugfix/*`, `hotfix/*`, or `chore/*` branches that include an issue number in the branch name must include `Refs #<issue-number>` in the commit body.
- GitHub automatically links when commit bodies or PR descriptions include references like `#123`.
- GitHub automatically closes issues when PR descriptions include keywords such as `Closes #123`, `Fixes #123`, or `Resolves #123` and the PR is merged into the default branch.
- If strict enforcement is needed, add a separate workflow or branch rule that fails PRs missing required issue references.

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

## Startup and Test Defaults
- Prefer project-provided scripts if present.
- For Java projects, prefer wrapper commands (`mvnw`, `gradlew`) over global installs.
- For local infra, verify Docker daemon before running compose commands.

## Safety Rules
- Never run destructive git commands unless explicitly requested.
- Stop and ask before force-push.
- On merge/rebase conflicts, stop and report conflicted files.

## Communication
- Keep responses concise and action-oriented.
- Report what changed, what was verified, and what could not be verified.

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
