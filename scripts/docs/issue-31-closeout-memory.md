# Issue 31 Closeout Memory

Last assessed: 2026-05-08
Branch: `bugfix/31-fix-unbounded-loads-friend-queries-settlement-scan`
GitHub issue: https://github.com/jmendoza89/MambaSplit.Api/issues/31

## Current Source Of Truth

The live GitHub issue body currently calls for 25-item pages, not 50-item pages:

- Initial group details: 25 newest expenses and 25 newest settlements.
- Expense pagination endpoint: `GET /api/v1/groups/{groupId}/expenses?before={isoTimestamp}&limit=25`.
- Settlement detail endpoint: `GET /api/v1/groups/{groupId}/settlements/{settlementId}`.
- Friend list summaries should be batched instead of computed sequentially inside the friend loop.
- Per-group friend balances should use grouped dictionaries instead of repeated filters.
- Settlement creation should scope already-linked expense lookup to the current group.

Older local planning notes mention 50-item pages. Treat those notes as stale unless the GitHub issue is edited again.

## Issue Item Status

1. `GroupService.GetGroupDetailsAsync` - Done, pending runtime/perf proof.
   - Uses `PageSize = 25`.
   - Applies `AsNoTracking()` to read paths.
   - Counts full-history expenses and settlements.
   - Queries the newest 25 expenses and newest 25 settlements in the database.
   - Loads splits only for returned page expenses.
   - Keeps full-history summary totals and member balances via aggregate queries.
   - Returns `HasMoreExpenses` and `HasMoreSettlements`.
   - Omits `expenseIds` from initial settlement rows.
   - Targeted regression test passed for summary/balances beyond the initial page.

2. New expense pagination endpoint - Not done.
   - No controller action exists for `GET /api/v1/groups/{groupId}/expenses?before=...&limit=25`.
   - No service method was found for a paged group expense response.
   - Still needs implementation, membership enforcement, stable cursor behavior, response contract, and focused tests.

3. New settlement detail endpoint - Partially done.
   - `SettlementService.GetSettlementAsync` exists.
   - Membership is enforced with `RequireMemberAsync(settlement.GroupId, actorUserId, ct)`.
   - Response includes linked `expenseIds`.
   - Implemented route is `GET /api/v1/settlements/{settlementId}`.
   - Live issue asks for `GET /api/v1/groups/{groupId}/settlements/{settlementId}`.
   - Need either add the group-scoped route or update the issue if the current global route is intentional.
   - Frontend direction: group details should show only the most recent 5 settlement rows, with "Load more" for older settlement rows.
   - Each settlement row should have a "Load" action for settled expense details instead of expanding preloaded expenses.
   - Do not let settlement detail reintroduce unbounded expense loading; if a settlement can contain many expenses, its linked expense details must also be bounded or paginated.

4. `FriendService.ListForUserAsync` batching - Done, pending full-suite/runtime proof.
   - Uses `ComputeBatchSummariesAsync` for connected friends.
   - Avoids calling `ComputeSummaryAsync` inside the friend loop.
   - Existing `FriendServiceTests` passed in the targeted slice.

5. `FriendService.ComputePerGroupBalancesAsync` dictionary grouping - Done, pending full-suite/runtime proof.
   - Groups expenses by `GroupId` before per-group balance iteration.
   - Groups settlements by `GroupId` before per-group balance iteration.
   - Existing `FriendServiceTests` passed in the targeted slice.

6. `SettlementService.CreateSettlementAsync` current-group link scope - Done, pending full-suite/runtime proof.
   - Scopes already-linked expense lookup through current-group expense IDs instead of scanning links without group context.
   - Existing `SettlementIntegrityIntegrationTests` passed in the targeted slice.

## Remaining Closeout Gaps

- Item 2 is still open.
- Item 3 needs route-contract reconciliation and a bounded settled-expense detail contract.
- TODO: Reconcile stale documentation and GitHub issue wording so the implemented API contract, local docs, and issue body match before closing issue #31.
- Runtime/perf verification remains incomplete.
  - Need RAM group proof that initial details returns `25 / 600` and `hasMoreExpenses = true`.
  - Need before/after payload size and frontend render timing from the perf harness.
  - Need separate API and DB memory captures.

- Current working tree has an existing local modification in `scripts/start-local.ps1`.
  - It adds `-WithTestDatabase` and gates `mambasplit_test` setup/env var behind that switch.
  - Do not accidentally discard this change.

## Verification Run In This Assessment

Command:

```powershell
dotnet test "tests\MambaSplit.Api.Tests\MambaSplit.Api.Tests.csproj" --filter "FullyQualifiedName~GroupDetailsSummaryAndBalances_AreCorrectBeyond50Expenses|FullyQualifiedName~FriendServiceTests|FullyQualifiedName~SettlementIntegrityIntegrationTests" --nologo
```

Result:

- Passed: 29
- Failed: 0
- Skipped: 0

This validates the targeted group-details pagination regression plus existing friend and settlement integrity coverage. It does not prove the missing expense pagination endpoint or the RAM/perf checklist.

## Recommended Closeout Order

1. Add or reconcile the group-scoped settlement detail route.
2. Implement the missing expense pagination endpoint and focused tests.
3. Run the targeted test slice again.
4. Run the full test suite if local Postgres/test provider setup is ready.
5. Capture the RAM group API/frontend measurements required by the issue.
6. Update issue #31 with exact evidence and close only after the endpoint and measurement gaps are resolved.
