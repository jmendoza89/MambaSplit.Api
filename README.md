# MambaSplit .NET API

Shared-expense backend API built with ASP.NET Core 8, EF Core, and PostgreSQL.

## Core Features

- JWT auth: signup, login, refresh, logout
- Google sign-in
- Groups: create, list, details, delete (owner)
- Invites: create, list pending, accept, cancel
- Expenses: equal/exact split, idempotency key, reversal-based delete
- Settlements: create/list/get with expense-level linkage and amount validation against selected expenses
- Transactional email: SMTP2GO-backed invite emails with Cloudflare DNS (SPF/DKIM/DMARC); internal send endpoint for testing/admin use
- Consistent validation/error responses

## Settlements (Current Behavior)

- Settlement creation requires explicit expense IDs.
- Authenticated actor must match fromUserId (payer); on-behalf settlement creation is forbidden.
- Each expense can be linked to at most one settlement.
- Settlement amount must match the computed outstanding pair balance for selected expenses.
- Group and user views return settlement records with linked expense IDs.

Current model note:
- Settlements are linked at expense-header level using settlement_expenses.
- Split-level settlement allocation (for example, settlement_split_allocations and FIFO auto-allocation across split rows) is not implemented yet.

## API Reference

- Source of truth for request/response contracts is OpenAPI/Swagger when public docs are enabled.
- Local docs URL: `/swagger` (enabled for local/dev/test/development environments).
- Local OpenAPI JSON URL: `/swagger/v1/swagger.json`.
- Keep this README as a high-level guide and operational reference.
- Do not duplicate full endpoint contracts here; add concise examples only for high-risk flows.

Export a versioned OpenAPI snapshot file into the repo:

```powershell
./scripts/export-openapi.ps1 -ApiBaseUrl "http://localhost:8080" -OutputPath "docs/openapi/openapi-v1.json" -Timestamped
```

## Error Contract

- API errors return a consistent JSON shape:

```json
{
	"code": "VALIDATION_FAILED",
	"message": "expenseIds: The field ExpenseIds must be a string or array type with a minimum length of '1'.",
	"timestamp": "2026-03-15T20:00:00.0000000Z"
}
```

- Common error codes:
	- `VALIDATION_FAILED` (400)
	- `AUTHENTICATION_FAILED` (401)
	- `FORBIDDEN` (403)
	- `RESOURCE_NOT_FOUND` (404)
	- `CONFLICT` (409)
	- `DATA_INTEGRITY_VIOLATION` (409)

## Compatibility and Changes

- Backward-compatible changes:
	- Adding optional response fields.
	- Adding new endpoints.
- Potential breaking changes:
	- Tightened authorization rules.
	- Required request field changes.
	- Validation semantics that can change status/message behavior.
- Current breaking-rule notes:
	- Settlement create requires `expenseIds` and it must be non-empty.
	- Authenticated actor must equal settlement `fromUserId`.

## Stack

- .NET 8 / ASP.NET Core
- EF Core + Npgsql
- PostgreSQL
- xUnit integration/service tests

## Project Structure

- `src/MambaSplit.Api/Controllers/` API controllers
- `src/MambaSplit.Api/Services/` business logic
- `src/MambaSplit.Api/Data/` EF context + migration runner
- `src/MambaSplit.Api/Domain/` entities
- `src/MambaSplit.Api/Security/` JWT/token utilities
- `src/MambaSplit.Api/Contracts/` API DTOs/contracts
- `src/MambaSplit.Api/Middleware/` HTTP middleware
- `src/MambaSplit.Api/Extensions/` shared extension methods
- `src/MambaSplit.Api/Validation/` custom validation attributes
- `src/MambaSplit.Api/Database/Migrations/` SQL migrations
- `tests/MambaSplit.Api.Tests/` tests

## Local Run

1. Start DB:

```powershell
docker compose up -d db
```

2. Run API:

```powershell
dotnet run --project src/MambaSplit.Api/MambaSplit.Api.csproj
```

Optional helper:

```powershell
./scripts/start-local.ps1
```

Run in background and write logs under `logs/`:

```powershell
./scripts/start-local.ps1 -Background
```

## Config Keys

- `ConnectionStrings:Default`
- `app:security:jwt:issuer`
- `app:security:jwt:secret`
- `app:database:runMigrationsOnStartup`
- `app:cors:origins`

## Database Migrations

- Migrations live in `src/MambaSplit.Api/Database/Migrations`
- Naming: `V{version}__{description}.sql`
- Applied on startup (if enabled) and tracked in `public.schema_history`

How to add future DB changes

Add new file under src/MambaSplit.Api/Database/Migrations named like V2__add_xyz.sql.
On app startup, it will apply automatically and record in schema_history.

## Test

```powershell
dotnet test MambaSplit.Api.sln --nologo
```

## Contributor Notes

- Keep controllers thin, put rules in services.
- Add new migration files for schema changes; do not rewrite applied versions.
