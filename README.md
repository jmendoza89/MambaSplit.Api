# MambaSplit .NET API

Shared-expense backend API built with ASP.NET Core 8, EF Core, and PostgreSQL.

## Core Features

- JWT auth: signup, login, refresh, logout
- Google sign-in
- Groups: create, list, details, delete (owner)
- Invites: create, list pending, accept, cancel
- Expenses: equal/exact split, idempotency key, reversal-based delete
- Consistent validation/error responses

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
