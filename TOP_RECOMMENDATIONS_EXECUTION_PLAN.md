# Top 13 Recommendations Execution Plan

This document is the operational checklist for closing out the 13 recommendations identified in the repository review.

How to use this file:
1. Work one recommendation at a time.
2. Create a branch per recommendation (or small logical bundle if related).
3. Implement all "Code Changes" and "Validation" steps.
4. Mark the recommendation as done only when all "Close-Out Criteria" are met.

---

## 1) Use Constant-Time Comparison for Admin Token

### Why
`string.Equals` can leak timing information. Admin token checks should use constant-time byte comparison.

### Priority
High (Security)

### Primary Files
- `src/MambaSplit.Api/Controllers/AdminSettlementsController.cs`

### Code Changes
1. Add `using System.Security.Cryptography;`.
2. Replace:
   - `string.Equals(configuredToken, providedToken, StringComparison.Ordinal)`
3. With constant-time compare logic:
   - Convert both strings to UTF8 byte arrays.
   - Early return unauthorized if lengths differ.
   - Use `CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes)`.

Suggested implementation shape:
```csharp
private void ValidateAdminPortalToken(string? providedToken)
{
    var configuredToken = _configuration["app:admin:portalToken"];
    if (string.IsNullOrWhiteSpace(configuredToken))
    {
        throw new AuthorizationException("admin settlement reset is not enabled");
    }

    if (string.IsNullOrEmpty(providedToken))
    {
        throw new AuthorizationException("admin settlement reset");
    }

    var expected = Encoding.UTF8.GetBytes(configuredToken);
    var actual = Encoding.UTF8.GetBytes(providedToken);

    if (expected.Length != actual.Length ||
        !CryptographicOperations.FixedTimeEquals(expected, actual))
    {
        throw new AuthorizationException("admin settlement reset");
    }
}
```

### Validation
- Build: `dotnet build MambaSplit.Api.sln --nologo`
- Test unauthorized with wrong token and no token.
- Test authorized with correct token.

### Close-Out Criteria
- No `string.Equals` token comparison remains in this path.
- Endpoint behavior unchanged except secure comparison.

---

## 2) Remove Sensitive Values from Committed AppSettings

### Why
Current committed settings include local JWT secrets, DB credentials, and email allow-list values.

### Priority
High (Security)

### Primary Files
- `src/MambaSplit.Api/appsettings.Development.json`
- `src/MambaSplit.Api/appsettings.local.json`
- `src/MambaSplit.Api/appsettings.Test.json` (confirm safe test-only values)
- Optional: `README.md` (local setup notes)

### Code Changes
1. Replace committed secrets with placeholders or empty strings.
2. Keep config keys intact so binding does not break.
3. Move actual values to user-secrets or environment variables.

Recommended value style:
- `"secret": ""`
- `"Default": ""`
- `"ApiKey": ""`
- `"InternalAllowedEmails": []`

### Operational Changes
1. Set local values with user-secrets:
   - `dotnet user-secrets set "app:security:jwt:secret" "<local-secret>" --project src/MambaSplit.Api/MambaSplit.Api.csproj`
   - `dotnet user-secrets set "ConnectionStrings:Default" "<local-conn>" --project src/MambaSplit.Api/MambaSplit.Api.csproj`
2. Confirm no secret-like values are committed in tracked JSON files.

### Validation
- App still starts locally with user-secrets/environment vars.
- Build + tests pass.

### Close-Out Criteria
- No real credentials/tokens in tracked settings files.
- Team setup instructions reflect secret injection path.

---

## 3) Add Cleanup for Expired Invites and Refresh Tokens

### Why
Expired auth and invite rows accumulate forever; this creates storage and maintenance debt.

### Priority
High (Operational)

### Primary Files (new/updated)
- `src/MambaSplit.Api/Services/DataCleanupService.cs` (new)
- `src/MambaSplit.Api/Configuration/AppMaintenanceOptions.cs` (new)
- `src/MambaSplit.Api/Program.cs`
- `src/MambaSplit.Api/appsettings.json` (add maintenance config keys)
- `tests/MambaSplit.Api.Tests/Services/DataCleanupServiceTests.cs` (new)

### Code Changes
1. Create `AppMaintenanceOptions` with:
   - `Enabled`
   - `RefreshTokenRetentionDays`
   - `InviteRetentionDays`
   - `RunOnStartup`
2. Implement cleanup logic:
   - Delete `refresh_tokens` where `expires_at < now - retention` OR revoked long ago.
   - Delete `invites` where `expires_at < now - retention`.
3. Add hosted service (or startup task) that runs cleanup periodically (ex: daily).
4. Register service in `Program.cs` only when enabled.

Example cleanup SQL path in EF:
```csharp
await _db.Database.ExecuteSqlInterpolatedAsync(
    $@"delete from refresh_tokens where expires_at < {cutoff}", ct);
```

### Validation
- Unit tests for cutoffs and deletion selection.
- Integration test inserting expired + active rows, then running cleanup.

### Close-Out Criteria
- Expired rows are purged predictably.
- Cleanup has configuration flags and safe defaults.

---

## 4) Replace Case-Insensitive Query Pattern That Defeats Indexes

### Why
`ToLower()` on DB columns in `WHERE` clauses can prevent index usage.

### Priority
Medium-High (Performance)

### Primary Files
- `src/MambaSplit.Api/Services/AuthService.cs`
- `src/MambaSplit.Api/Services/GroupService.cs`
- `src/MambaSplit.Api/Controllers/UsersController.cs`
- `src/MambaSplit.Api/Database/Migrations/*` (new migration likely needed)

### Code Changes
Option A (Recommended): use PostgreSQL `citext` for email columns.
1. Add migration converting relevant columns to `citext`:
   - `users.email`
   - `invites.email`
2. Ensure extension exists:
   - `CREATE EXTENSION IF NOT EXISTS citext;`
3. Update predicates to direct equality/contains without `ToLower` transformations.

Option B: use functional indexes and keep text columns.
1. Add indexes like `lower(email)`.
2. Query with translated functions consistently.

### Validation
- Explain analyze targeted queries before/after.
- Confirm no behavior regression for mixed-case inputs.

### Close-Out Criteria
- No `u.Email.ToLower()` style in query filters for indexed lookups.
- Query plan uses indexes for core auth/invite lookups.

---

## 5) Add Pagination for List Endpoints

### Why
Hard-coded `Take(50)` is not enough for long-term scale and not API-consumer friendly.

### Priority
Medium (Scalability + API Quality)

### Primary Files
- `src/MambaSplit.Api/Controllers/GroupController.cs`
- `src/MambaSplit.Api/Controllers/InviteController.cs`
- `src/MambaSplit.Api/Controllers/UsersController.cs`
- `src/MambaSplit.Api/Controllers/SettlementsController.cs`
- `src/MambaSplit.Api/Services/GroupService.cs`
- `src/MambaSplit.Api/Services/SettlementService.cs`
- `README.md` (high-level API behavior update)
- Tests under `tests/MambaSplit.Api.Tests/Integration/`

### Code Changes
1. Introduce request query params:
   - `page` (>=1)
   - `pageSize` (1..100)
2. Return metadata:
   - `page`, `pageSize`, `totalCount`, `hasNextPage`.
3. Apply `Skip((page-1)*pageSize).Take(pageSize)` in service queries.
4. Keep deterministic ordering for stable pages.

### Validation
- Integration tests for first page, middle page, and boundary conditions.
- Verify backward compatibility: default values preserve current behavior for callers not passing pagination params.

### Close-Out Criteria
- All major list endpoints support explicit pagination and metadata.

---

## 6) Archive or Remove Stale Prompt/Issue Artifacts

### Why
Some prompt files are historical and can confuse current workflow.

### Priority
Medium (Repository Hygiene)

### Candidate Files to Clean Up
- `prompts/backend-agent-prompt/issue.md`
- `prompts/backend-agent-prompt/implementation.md`

### Keep (Still Useful)
- `prompts/backend-agent-prompt/backend-agent.prompt.md` (if still used)

### Cleanup Actions
Option A (preferred): move historical files to archive folder.
1. Create `prompts/archive/`.
2. Move old issue and implementation notes there.

Option B: delete files if confirmed obsolete.

### Validation
- Ensure no scripts or docs still reference old paths.
- Run search:
  - `rg "prompts/backend-agent-prompt/(issue|implementation)\.md"`

### Close-Out Criteria
- Active prompt directory contains only currently used templates.

---

## 7) Remove or Wire Unused Email Templates

### Why
Unused templates create confusion and maintenance burden.

### Priority
Medium (Repository Hygiene)

### Candidate Unused Files
- `src/MambaSplit.Api/Templates/welcome.html`
- `src/MambaSplit.Api/Templates/welcome.subject.txt`
- `src/MambaSplit.Api/Templates/welcome.txt`
- `src/MambaSplit.Api/Templates/release-announcement.html`
- `src/MambaSplit.Api/Templates/release-announcement.subject.txt`
- `src/MambaSplit.Api/Templates/release-announcement.txt`
- `src/MambaSplit.Api/Templates/release-announcement-assets/screenshotMain.png`
- `src/MambaSplit.Api/Templates/release-announcement-assets/screenshotGroup.png`

### Decision Required
1. If templates are intended for future use, add TODO ownership and roadmap note.
2. If not needed, delete them.
3. If needed now, wire them from service/controller and add tests.

### Validation
- `rg "welcome|release-announcement" src/MambaSplit.Api/**/*.cs`
- Confirm either references exist (if kept) or files removed (if unused).

### Close-Out Criteria
- No orphaned template assets remain without explicit ownership.

---

## 8) Update Deferred Settlement Backlog Notes

### Why
Backlog review date/status is stale relative to current date and implementation state.

### Priority
Medium (Documentation Accuracy)

### Primary Files
- `BACKLOG_NOTES.md`

### Code/Doc Changes
1. Update:
   - Review date
   - Current status summary
2. Confirm checklist states still match implementation.
3. Add an "owner" and "next review date" field.

### Validation
- Cross-check against current service/controller behavior.
- Ensure no contradictory statements remain.

### Close-Out Criteria
- Backlog note is current, actionable, and consistent with codebase.

---

## 9) Centralize Duplicate GUID Parsing Helpers

### Why
`ParseGuid` logic is duplicated in multiple controllers.

### Priority
Low-Medium (Code Quality)

### Primary Files
- `src/MambaSplit.Api/Controllers/GroupController.cs`
- `src/MambaSplit.Api/Controllers/ExpenseController.cs`
- `src/MambaSplit.Api/Controllers/SettlementsController.cs`
- `src/MambaSplit.Api/Controllers/InviteController.cs`
- `src/MambaSplit.Api/Controllers/AdminSettlementsController.cs`
- `src/MambaSplit.Api/Extensions/` (new helper)

### Code Changes
1. Add shared parser utility, for example:
   - `Extensions/RouteParsingExtensions.cs`
   - `public static Guid ParseRequiredGuid(string value, string fieldName)`
2. Remove duplicated private methods from controllers.
3. Use one consistent validation message format.

### Validation
- Build + controller tests.
- Verify same status code/message on invalid UUID inputs.

### Close-Out Criteria
- Single source of truth for GUID route parsing logic.

---

## 10) Fix Formatting Defect in AppDbContext Declaration

### Why
`public class   AppDbContext` has accidental extra spaces.

### Priority
Low (Cosmetic)

### Primary Files
- `src/MambaSplit.Api/Data/AppDbContext.cs`

### Code Changes
1. Replace:
   - `public class   AppDbContext : DbContext`
2. With:
   - `public class AppDbContext : DbContext`

### Validation
- Build solution.

### Close-Out Criteria
- No spacing anomalies in class declaration.

---

## 11) Move `MeController` Direct DB Logic into Service Layer

### Why
Current controller accesses EF directly; project convention prefers business/data logic in services.

### Priority
Low-Medium (Architecture Consistency)

### Primary Files (new/updated)
- `src/MambaSplit.Api/Controllers/MeController.cs`
- `src/MambaSplit.Api/Services/MeService.cs` (new)
- `src/MambaSplit.Api/Program.cs` (DI registration)
- Tests in `tests/MambaSplit.Api.Tests/Services/MeServiceTests.cs` (new)

### Code Changes
1. Create `MeService` methods:
   - `GetMeAsync(userId, ct)`
   - `UpdateDisplayNameAsync(userId, displayName, ct)`
   - `ChangePasswordAsync(...)` (or keep using `AuthService` but orchestrate in service)
2. Controller delegates to service.
3. Remove `AppDbContext` dependency from controller constructor.

### Validation
- Unit tests for me-profile logic and invite composition.
- Integration tests for `/api/v1/me` and `/api/v1/me` patch endpoint.

### Close-Out Criteria
- Controller is thin; service owns profile read/update orchestration.

---

## 12) Resolve No-Op Delegated Payer Policy Function

### Why
`EnforceDelegatedPayerPolicy` currently performs no checks and may confuse future maintainers.

### Priority
Low-Medium (Clarity + Future Safety)

### Primary Files
- `src/MambaSplit.Api/Services/ExpenseService.cs`
- Potentially `README.md` (policy note)

### Code Changes
Choose one explicit path:
1. Keep open policy intentionally:
   - Remove method and inline comment near call sites stating policy is intentionally permissive.
2. Enforce strict policy:
   - Require `actorUserId == payerUserId`; otherwise throw `AuthorizationException`.
3. Make policy configurable:
   - Add options flag `AllowDelegatedPayer`.

### Validation
- Add unit/integration tests for chosen policy.
- Confirm error contract if policy denies action.

### Close-Out Criteria
- Policy is explicit and test-covered; no misleading no-op remains.

---

## 13) Expand Test Coverage for Core Services and Utility Paths

### Why
Critical business flows are covered in integration tests, but many service/utility classes lack focused unit tests.

### Priority
Medium (Regression Prevention)

### Priority Coverage Targets
1. `src/MambaSplit.Api/Services/GroupService.cs`
2. `src/MambaSplit.Api/Services/ExpenseService.cs`
3. `src/MambaSplit.Api/Services/SettlementService.cs`
4. `src/MambaSplit.Api/Services/GroupMembershipService.cs`
5. `src/MambaSplit.Api/Services/EqualSplitCalculator.cs`
6. `src/MambaSplit.Api/Middleware/ApiExceptionMiddleware.cs`
7. `src/MambaSplit.Api/Extensions/PrincipalExtensions.cs`
8. `src/MambaSplit.Api/Security/TokenCodec.cs`
9. `src/MambaSplit.Api/Data/DatabaseMigrationRunner.cs`

### Test Files to Add
- `tests/MambaSplit.Api.Tests/Services/GroupServiceTests.cs`
- `tests/MambaSplit.Api.Tests/Services/ExpenseServiceTests.cs`
- `tests/MambaSplit.Api.Tests/Services/SettlementServiceTests.cs`
- `tests/MambaSplit.Api.Tests/Services/GroupMembershipServiceTests.cs`
- `tests/MambaSplit.Api.Tests/Services/EqualSplitCalculatorTests.cs`
- `tests/MambaSplit.Api.Tests/Middleware/ApiExceptionMiddlewareTests.cs`
- `tests/MambaSplit.Api.Tests/Extensions/PrincipalExtensionsTests.cs`
- `tests/MambaSplit.Api.Tests/Security/TokenCodecTests.cs`
- `tests/MambaSplit.Api.Tests/Data/DatabaseMigrationRunnerTests.cs`

### Test Cases (minimum)
1. Happy path + validation failures per service method.
2. Authorization failures and expected error code mapping.
3. Overflow/edge behavior in amount math paths.
4. Idempotency conflict and replay behavior in expenses.
5. Settlement integrity conflict handling.
6. Migration checksum mismatch detection.

### Validation
- `dotnet test MambaSplit.Api.sln --nologo`

### Close-Out Criteria
- New tests materially increase confidence in core business and edge-case behavior.

---

## Suggested Execution Order

1. Recommendation 1 (admin token compare)
2. Recommendation 2 (remove secrets)
3. Recommendation 3 (cleanup job)
4. Recommendation 4 (case-insensitive query/index improvements)
5. Recommendation 5 (pagination)
6. Recommendation 12 (delegated payer policy clarity)
7. Recommendation 11 (Me service extraction)
8. Recommendation 9 (centralized GUID parser)
9. Recommendation 13 (test expansion)
10. Recommendation 8 (backlog refresh)
11. Recommendation 6 (stale prompts)
12. Recommendation 7 (unused templates)
13. Recommendation 10 (formatting cleanup)

---

## Branch / PR Template Per Recommendation

For each recommendation PR include:
1. Scope summary.
2. Files changed.
3. Risk assessment.
4. Test evidence (commands + results).
5. Rollback notes.

Suggested branch naming:
- `chore/<issue>-admin-token-fixed-time-compare`
- `chore/<issue>-config-secret-scrub`
- `feature/<issue>-data-cleanup-service`

Commit body requirement reminder (repo policy):
- Include `Refs #<issue-number>` in commit body on issue-numbered feature/bugfix/hotfix/chore branches.

---

## Global Completion Checklist

- [ ] All 13 recommendations implemented or explicitly rejected with rationale.
- [ ] Security-sensitive changes reviewed.
- [ ] Integration tests pass.
- [ ] Documentation and backlog notes updated.
- [ ] No stale artifacts remain without ownership.
