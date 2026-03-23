# Implementation Spec — Full code required (file-by-file, every line)

Purpose: This document is the authoritative implementation spec. Another agent will use this to produce the exact code changes. It must include all filenames, exact contents, EF migration code, tests, and configuration snippets. Default DB provider: Postgres.

Output format required from implementing agent:
- `implementation_patch.diff` — a unified diff patch.
- `files/` folder listing each file added/modified with full contents.
- `migrations/` folder with EF migration files.
- `tests/` folder with unit + integration tests.
- `verification_steps.md` with exact commands to run tests and manual verification steps.
- `implementation.md` — a single authoritative file that lists, line-by-line, the exact code changes and the filenames to edit. This file is the primary instruction set for applying the patch.

Required code artifacts (explicit list — implementer must produce these exact items). Tailor this list to the requested feature; example generic set below:
1) Controllers/<Feature>Controller.cs — new or modified controller(s) exposing the requested routes.
2) Services/<Feature>Service.cs — orchestration/business logic for the feature.
3) Interfaces in `Services/` (e.g., `I<Feature>Service`) to enable unit testing and DI.
4) Data/Entities/*.cs — any new EF entities required for persistence.
5) Data/AppDbContext.cs — register new `DbSet<>` entries and apply entity configuration.
6) Database/Migrations/*** — EF migration(s) for any schema changes (Postgres-compatible).
7) Configuration/*Options.cs — typed options for any configuration needed by the feature.
8) appsettings.* example snippet (non-secret) showing expected config keys.
9) Tests: Unit tests for service logic (mock dependencies), Integration tests that run against a Postgres test DB (or Sqlite in-memory when appropriate) and any required fakes/mocks.
10) Authorization/Policy updates or Middleware changes if the feature requires special access rules.
11) README snippet with setup, secrets, and testing instructions.

Security and secrets:
- Do not store secrets in source; reference environment variables or KeyVault.
- Implementation must use typed `IOptions<TOptions>` and validate at startup.

Verification steps (to be filled by implementer):
- `dotnet test` commands and expected results
- Example `curl` or `Invoke-RestMethod` calls demonstrating success and error cases

Time estimate: Implementer should provide an estimate in hours and complexity rating.

Notes for implementer agent: produce the full contents exactly — every single line — and include a single `.diff` patch and separate files for easy review. Ensure code compiles on .NET 8 (project uses net8.0). Follow repository style and naming conventions. Use Postgres-compatible EF migrations and queries.
