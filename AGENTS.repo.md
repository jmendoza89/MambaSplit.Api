## Agent Cheat Sheet

Use natural language by default. Name the agent explicitly only when you want to force a specific workflow.

### Default Rule
- If the request is general and unambiguous, Codex can usually pick the right approach without you naming an agent.
- If you want a specific runbook, say `Use <agent-name> ...` in your prompt.

### Best-Fit Agents For This Repo
- `feature-workflow-manager`
  - Use for branch and PR workflow tasks.
  - Example prompts:
    - `Use feature-workflow-manager to start issue 123`
    - `Use feature-workflow-manager to commit and sync my changes`
    - `Use feature-workflow-manager to finalize this branch`
- `risk-first-pr-reviewer`
  - Use for findings-first code review, regression checks, and merge risk analysis.
  - Example prompts:
    - `Use risk-first-pr-reviewer to review these changes`
    - `Use risk-first-pr-reviewer to look for regressions`
- `csharp-dotnet-janitor`
  - Use for .NET cleanup, modernization, warning reduction, tech debt, and test coverage improvements.
  - Example prompts:
    - `Use csharp-dotnet-janitor to clean up these warnings`
    - `Use csharp-dotnet-janitor to modernize this service`
- `email-template-designer`
  - Use for transactional email templates, copy updates, and template styling within the API email system.
  - Example prompts:
    - `Use email-template-designer to add a new transactional email`
    - `Use email-template-designer to redesign this existing template`
- `4.1 Beast Mode v3.1`
  - Use for broad end-to-end coding tasks when no narrower agent is a better fit.
  - Example prompts:
    - `Use 4.1 Beast Mode v3.1 to handle this task end to end`

### Sometimes Relevant
- `ui-visual-implementer`
  - Use only if the task touches frontend-facing assets or rendered UI owned by this repo.
- `expert-react-frontend-engineer`
  - Use only if this repo contains React frontend code for the task at hand.

## Project Quick Start

### Build and Test Commands
- Preferred local run: `./scripts/start-local.ps1`
- Run API without Docker checks: `./scripts/start-local.ps1 -SkipDocker`
- Run API directly: `dotnet run --project src/MambaSplit.Api/MambaSplit.Api.csproj`
- Run all tests: `dotnet test MambaSplit.Api.sln --nologo`
- Export OpenAPI snapshot: `./scripts/export-openapi.ps1 -ApiBaseUrl http://localhost:8080 -Timestamped`

### Architecture Boundaries
- `src/MambaSplit.Api/Controllers/`: HTTP endpoints and request/response shaping only.
- `src/MambaSplit.Api/Services/`: Business rules and orchestration.
- `src/MambaSplit.Api/Data/` and `src/MambaSplit.Api/Domain/`: EF Core context and entities.
- `src/MambaSplit.Api/Database/Migrations/`: Manual SQL migrations (`V{version}__{description}.sql`).
- `tests/MambaSplit.Api.Tests/Integration/`: API behavior and flow-level tests.
- `tests/MambaSplit.Api.Tests/Services/` and `tests/MambaSplit.Api.Tests/TestSupport/`: service tests and shared test setup.

### Project Conventions
- Keep controllers thin; enforce business rules in services.
- Prefer async service methods and pass `CancellationToken` through call chains when supported.
- Use custom domain exceptions (`BusinessException` family) for expected business failures; let middleware map them to error contract.
- Keep validation in data annotations/custom attributes and avoid duplicating simple validation logic in controllers.
- For schema changes, add a new migration file; do not modify already-applied migration versions.
- Preserve settlement invariants and authorization behavior unless explicitly requested; these are high regression-risk paths.

### Environment and Runtime Pitfalls
- `./scripts/start-local.ps1` can fail if Docker daemon access is unavailable; use `-SkipDocker` when DB is already running.
- Startup fails when `app:security:jwt:secret` or `ConnectionStrings:Default` is missing/invalid.
- Migration-dependent changes require `app:database:runMigrationsOnStartup=true` (or an already migrated schema).
- Local defaults use API port `8080` and DB port `5432`; check for conflicts before assuming app-level failures.

### High-Value Reference Files
- `src/MambaSplit.Api/Program.cs`
- `src/MambaSplit.Api/Middleware/ApiExceptionMiddleware.cs`
- `src/MambaSplit.Api/Data/AppDbContext.cs`
- `src/MambaSplit.Api/Controllers/AuthController.cs`
- `src/MambaSplit.Api/Services/AuthService.cs`
- `tests/MambaSplit.Api.Tests/Integration/CustomWebApplicationFactory.cs`
- `tests/MambaSplit.Api.Tests/Integration/FlowIntegrationTests.cs`
