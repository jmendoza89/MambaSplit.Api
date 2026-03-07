# AGENTS Instructions (Project)

Project-level instructions for coding agents in `mambasplit-dotnet-api`.

## Parent Instructions

Use parent/base instructions from:

- `C:\MambaSplit\AGENTS`
- `C:\MambaSplit\AGENTS.md`

## Project Rules

- Keep controllers thin; keep business logic in `Services/`.
- Preserve existing API contracts and error behavior unless intentionally changing them.
- Prefer additive, non-breaking changes.

## Database Changes

- Use SQL migrations in `src/MambaSplit.Api/Database/Migrations` with `V{version}__{description}.sql` naming.
- Never edit already-applied versions in shared environments; add a new higher version.

How to add future DB changes

Add new file under src/MambaSplit.Api/Database/Migrations named like V2__add_xyz.sql.
On app startup, it will apply automatically and record in schema_history.

## Validation

- Run tests after changes:
  - `dotnet test MambaSplit.Api.sln --nologo`
