# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MambaSplit API is a shared-expense backend built with ASP.NET Core 8, EF Core (Npgsql), and PostgreSQL. It exposes JWT-authenticated REST endpoints for groups, invites, expenses (equal/exact splits), settlements, friends, and transactional email.

## Commands

Preferred local run (starts Postgres via Docker, checks ports, launches Swagger):

```powershell
./scripts/start-local.ps1
```

- `-SkipDocker` — DB already running, skip Docker checks.
- `-WithTestDatabase` — also provision the `mambasplit_test` database.
- `-Background` — run detached, logs written to `logs/`.

Run directly without the helper script:

```powershell
docker compose up -d db
dotnet run --project src/MambaSplit.Api/MambaSplit.Api.csproj
```

Run all tests (requires a reachable Postgres instance; integration tests create/drop a real per-test schema — nothing is mocked):

```powershell
dotnet test MambaSplit.Api.sln --nologo
```

Run a single test class or method:

```powershell
dotnet test MambaSplit.Api.sln --filter "FullyQualifiedName~SettlementIntegrityIntegrationTests"
dotnet test MambaSplit.Api.sln --filter "FullyQualifiedName~SettlementIntegrityIntegrationTests.SomeTestMethod"
```

Test DB connection defaults to `Host=localhost;Port=5432;Database=mambasplit_test;Username=mambasplit;Password=mambasplit`; override with the `MAMBASPLIT_TEST_POSTGRES_CONNECTION` env var (this is what CI sets).

Export a versioned OpenAPI snapshot:

```powershell
./scripts/export-openapi.ps1 -ApiBaseUrl "http://localhost:8080" -OutputPath "docs/openapi/openapi-v1.json" -Timestamped
```

## Architecture

**Layering**: `Controllers/` are thin — HTTP shaping only. Business rules and orchestration live in `Services/`. Controllers call one service; services own transactions and invariants.

**No dedicated DTO folder**: request/response records are declared inline at the bottom of each controller file (e.g. `ExpenseController.cs`, `SettlementsController.cs`), not under `Contracts/`. `Contracts/` holds only the shared `ErrorResponse` shape used by the global error contract.

**Error handling**: `Middleware/ApiExceptionMiddleware.cs` is the single translation point from exceptions to the JSON error contract (`{ code, message, timestamp }`). Throw the domain exceptions in `Exceptions/` (`ValidationException`, `AuthenticationException`, `AuthorizationException`, `ResourceNotFoundException`, `ConflictException` — all derive from `BusinessException`, which carries `StatusCode`/`ErrorCode`) from services instead of returning error status codes from controllers. `DbUpdateException` is caught and mapped to 409 `DATA_INTEGRITY_VIOLATION` automatically; unhandled exceptions map to 500 `DATA_ACCESS_ERROR`. Model-validation failures (data annotations) are intercepted in `Program.cs` and reshaped into the same `VALIDATION_FAILED` contract — don't duplicate that formatting in controllers.

**Auth**: JWT bearer, `sub` claim as user id (`ClaimsPrincipal.UserId()` / `.UserEmail()` extensions in `Extensions/PrincipalExtensions.cs`). `Program.cs` sets a fallback authorization policy requiring an authenticated user on every endpoint by default — use `[AllowAnonymous]` explicitly for public routes (auth endpoints, `/health`, internal email webhook). `JwtService`/`TokenCodec` in `Security/` issue/validate access + refresh tokens; refresh tokens are persisted (`RefreshTokenEntity`) and hashed.

**Data**: `Data/AppDbContext.cs` is the single EF Core context; all relationships/unique indexes are configured in `OnModelCreating` (no separate `IEntityTypeConfiguration` classes). `Data/DatabaseMigrationRunner.cs` applies raw SQL migrations on startup when `app:database:runMigrationsOnStartup` is true, tracked in `public.schema_history`.

**Migrations**: hand-written SQL in `src/MambaSplit.Api/Database/Migrations/`, named `V{n}__{description}.sql`, applied in order and never rewritten once applied — add a new `Vn+1` file for schema changes.

**Settlements model (current, load-bearing invariants — high regression risk)**:
- Settlement creation requires explicit `expenseIds`; the authenticated actor must equal `fromUserId` (no on-behalf creation).
- Each expense can link to at most one settlement (`SettlementExpenseEntity`, unique index on `ExpenseId`).
- Settlement amount must match the computed outstanding pair balance for the selected expenses.
- Split-level settlement allocation (e.g. FIFO auto-allocation across `expense_splits`) is **not implemented** — settlements link at the expense-header level only. Don't assume split-level allocation exists when reading or extending settlement code.

**Expenses**: deletions are reversal-based (an expense is closed by inserting a reversal row via `ReversalOfExpenseId`, not a hard delete). Creation supports an idempotency key, unique per `(GroupId, CreatedByUserId, IdempotencyKey)`.

**Email**: `IEmailSender` (`Smtp2GoEmailSender`, HTTP-based) + `IEmailTemplateRenderer` (`FileEmailTemplateRenderer`, reads templates from `Templates/`) are composed by `TransactionalEmailService`. `InternalEmailController` exposes an internal/admin send endpoint for testing — guard changes here since it's unauthenticated-by-default surface aside from its own token check.

**Docs**: Swagger/OpenAPI (`/swagger`, `/swagger/v1/swagger.json`) is only wired up when `ASPNETCORE_ENVIRONMENT` is `local`, `dev`, `test`, or `development` (see `IsPublicDocsEnabled` in `Program.cs`) — it will not be present in Staging/Production.

## Testing

- `tests/MambaSplit.Api.Tests/Integration/` — full-stack tests through `CustomWebApplicationFactory` (wraps `WebApplicationFactory<Program>`), which points the app at a real Postgres schema created/dropped per test run via `TestSupport/PostgresTestDatabase.cs` (schema name `test_{guid}`, generated from `AppDbContext.GenerateCreateScript()` — migrations are not replayed in tests). There is no mocked-DB test path; a live Postgres is a hard prerequisite for `Integration/` and any `Services/` test that touches EF.
- `tests/MambaSplit.Api.Tests/Services/` — narrower service-level tests.
- `tests/MambaSplit.Api.Tests/TestSupport/` — shared fixtures (`AuthTestContext`, `FriendTestContext`, `PostgresTestDatabase`).
- CI (`.github/workflows/ci.yml`) runs against a `postgres:16` service container and sets `ASPNETCORE_ENVIRONMENT=Test`, `APP__SECURITY__JWT__SECRET`, `CONNECTIONSTRINGS__DEFAULT`, and `MAMBASPLIT_TEST_POSTGRES_CONNECTION`.

## Conventions and workflow

- Keep controllers thin; put rules in services (see Architecture above).
- Prefer async service methods; pass `CancellationToken` through call chains where the signature already supports it.
- New schema change → new migration file; never edit an already-applied `V{n}` migration.
- Branch model: `main` (production) and `develop` (integration). Feature/bugfix/hotfix/chore branches are cut from `develop` and PR back into `develop`; `develop` promotes to `main` via PR. `main` only accepts PRs whose source branch is `develop` (enforced by `.github/workflows/restrict-main-merges.yml`) — never push directly to `main`.
- `develop`→`main` release merges must use an explicit commit message — subject `REL_v<version>`, body `: release <short description>` — instead of GitHub's default "Merge pull request #N ..." text. Railway's deploy history displays this exact commit message as the deployment's label with no separate rename option, so getting it wrong means amending + force-pushing `main` and re-tagging to fix after the fact. Version bumps the minor version (`vX.Y.0`) for every release by default, regardless of size.
- Branch naming: `^(feature|bugfix|hotfix|chore)/[0-9]+-[a-z0-9]+(?:-[a-z0-9]+)*$`.
- Commits on issue-numbered branches must include `Refs #<issue-number>` in the commit body.
- PR titles are enforced by `.github/workflows/semantic-pr-title.yml` (conventional-commit style: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `perf`, `ci`, `build`, `revert`; scope optional).
- This repo is one of three kept in lockstep policy-wise with `../agent-templates` and `../mambasplit-web` (see `AGENTS.repo.md`); avoid making cross-cutting workflow-policy changes here in isolation.
- The `pre-commit` git hook (`.githooks/pre-commit`) runs `scripts/sync-agents.ps1` to sync `.github/agents/` from the shared `agent-templates` repo — don't hand-edit `.github/agents/*.agent.md` expecting it to stick.

## Subagents and commands (this tool)

`.github/agents/*.agent.md` and `.github/prompts/*.prompt.md` are Copilot/Codex-specific formats synced from `agent-templates` (see above) — leave them to that sync flow. This tool instead has native equivalents, hand-maintained here (not synced):

- `.claude/agents/feature-workflow-manager.md`, `csharp-dotnet-janitor.md`, `email-template-designer.md`, `ui-visual-implementer.md` — subagents mirroring the current `.github/agents/*.agent.md` catalog; invoke via the Agent tool or by describing the task naturally.
- `.claude/commands/git-actions.md` — `/git-actions`, mirrors `.github/prompts/git-actions.prompt.md` for generating policy-compliant branch/commit/PR text.
- `.claude/commands/feature-workflow.md` — `/feature-workflow start|commit|finalize|release`, thin wrapper that delegates to the `feature-workflow-manager` subagent instead of duplicating its logic.
- The built-in `commit-push` and `release-pr` skills already cover simple commit+push and the develop→main release PR case; reach for `feature-workflow-manager` when you need the fuller issue→branch→PR lifecycle or its guardrails.

If the `.github/agents/*.agent.md` catalog changes, update these files to match by hand.
