# Issue 31 Items 2 and 3 Closeout Plan

Last updated: 2026-05-08

## Goal

Close out the remaining issue 31 pagination gaps without moving the original unbounded-load problem into settlement details.

The implementation should support:

- Main expense pagination for `Recent Expenses`.
- Settlement row pagination for `Settled Expense Groups`.
- On-demand, paginated loading of expenses linked to one settlement.
- Updated docs and GitHub issue wording that match the final metadata-first contract.

## Backend Changes

### 1. Expense Pagination Endpoint

Add:

```http
GET /api/v1/groups/{groupId}/expenses?before={isoTimestamp}&limit=25
```

Behavior:

- Require the caller to be a group member.
- Default `limit` to `25`.
- Cap `limit` at `25`.
- Query expenses with `AsNoTracking()`.
- Filter by `GroupId`.
- When `before` is provided, return expenses older than that cursor.
- Sort newest-to-oldest with a stable order:
  - `CreatedAt DESC`
  - `Id DESC`
- Fetch `limit + 1` rows to compute `hasMoreExpenses`.
- Return only the requested page.
- Load splits and settlement links only for the returned page expenses.

Response:

```json
{
  "expenses": [],
  "hasMoreExpenses": true,
  "nextBefore": "2026-05-08T12:34:56.7890000Z"
}
```

Notes:

- `nextBefore` should be the `createdAt` value from the oldest returned expense.
- If no more rows exist, `hasMoreExpenses` is `false` and `nextBefore` can be `null`.
- This endpoint closes item 2.

### 2. Settlement Row Pagination Endpoint

Add or update:

```http
GET /api/v1/groups/{groupId}/settlements?before={isoTimestamp}&limit=5
```

Behavior:

- Require the caller to be a group member.
- Default `limit` to `5`.
- Cap `limit` at `5`.
- Query settlements with `AsNoTracking()`.
- Filter by `GroupId`.
- When `before` is provided, return settlements older than that cursor.
- Sort newest-to-oldest with a stable order:
  - `CreatedAt DESC`
  - `Id DESC`
- Fetch `limit + 1` rows to compute `hasMoreSettlements`.
- Return settlement metadata only.
- Do not return linked `expenseIds` from the row list.

Response:

```json
{
  "settlements": [
    {
      "id": "settlement-id",
      "groupId": "group-id",
      "fromUserId": "from-user-id",
      "fromUserName": "Julio",
      "toUserId": "to-user-id",
      "toUserName": "Julio C. Mendoza",
      "amountCents": 5250,
      "note": null,
      "settledAt": "2026-04-04T12:34:56.7890000Z",
      "expenseCount": 2
    }
  ],
  "hasMoreSettlements": true,
  "nextBefore": "2026-04-04T12:34:56.7890000Z"
}
```

Notes:

- `expenseCount` replaces the old need to ship `expenseIds` in row data.
- Initial group details should return the newest `5` settlement metadata rows and `hasMoreSettlements`.
- This becomes the row list behind `Settled Expense Groups`.

### 3. Settlement Expense Detail Pagination Endpoint

Add:

```http
GET /api/v1/groups/{groupId}/settlements/{settlementId}/expenses?before={isoTimestamp}&limit=25
```

Behavior:

- Require the caller to be a group member of `groupId`.
- Require the settlement to belong to `groupId`.
- Default `limit` to `25`.
- Cap `limit` at `25`.
- Return settlement metadata plus one bounded page of linked expenses.
- Query linked expenses through `SettlementExpenses`.
- Sort linked expenses newest-to-oldest with stable ordering:
  - `Expense.CreatedAt DESC`
  - `Expense.Id DESC`
- Fetch `limit + 1` linked expenses to compute `hasMoreExpenses`.
- Load splits only for returned expenses.

Response:

```json
{
  "settlement": {
    "id": "settlement-id",
    "groupId": "group-id",
    "fromUserId": "from-user-id",
    "fromUserName": "Julio",
    "toUserId": "to-user-id",
    "toUserName": "Julio C. Mendoza",
    "amountCents": 5250,
    "note": null,
    "settledAt": "2026-04-04T12:34:56.7890000Z",
    "expenseCount": 600
  },
  "expenses": [],
  "hasMoreExpenses": true,
  "nextBefore": "2026-03-30T12:34:56.7890000Z"
}
```

Notes:

- This replaces the older item 3 idea of returning all linked `expenseIds`.
- If one settlement has 600 linked expenses, the first click loads 25, not 600.
- Additional linked expenses are loaded through the same endpoint with `before`.
- This closes item 3 with a safer production contract.

### 4. Group Details Contract Update

Update `GroupService.GetGroupDetailsAsync` so initial group details returns:

- Newest `25` expenses.
- Newest `5` settlement metadata rows.
- `hasMoreExpenses`.
- `hasMoreSettlements`.
- Full-history summary counts and totals.
- Full-history member balances.

Do not include linked `expenseIds` in initial settlement rows.

### 5. Backend Tests

Add focused tests for:

- Expense pagination returns only the requested page.
- Expense pagination enforces membership.
- Expense pagination does not repeat the cursor row.
- Expense pagination returns splits and settlement IDs only for page expenses.
- Settlement row pagination returns metadata only.
- Settlement row pagination returns `expenseCount`.
- Settlement row pagination enforces membership.
- Settlement expense detail pagination enforces membership and group ownership.
- Settlement expense detail pagination returns only a bounded page when a settlement has more than 25 linked expenses.
- `hasMoreExpenses` and `hasMoreSettlements` flip to `false` on final pages.

## Frontend Changes

### 1. API Client

Add API helpers:

```js
groupsApi.listExpenses(groupId, { before, limit })
groupsApi.listSettlements(groupId, { before, limit })
groupsApi.listSettlementExpenses(groupId, settlementId, { before, limit })
```

Use query parameters only when values are present.

### 2. Group Controller State

Track pagination state separately:

- `hasMoreExpenses`
- `expensesNextBefore`
- `expensesPageLoading`
- `expensesPageError`
- `hasMoreSettlements`
- `settlementsNextBefore`
- `settlementsPageLoading`
- `settlementsPageError`
- settlement-expense page state keyed by `settlementId`

On initial group details load:

- Store returned expenses.
- Store returned settlement metadata rows.
- Initialize cursors from the oldest returned row in each list.
- Initialize `hasMoreExpenses` and `hasMoreSettlements` from the API response.

### 3. Recent Expenses UI

In `Recent Expenses`:

- Render the current expense list as today.
- Add a bottom button when `hasMoreExpenses` is true:
  - `Load older expenses`
- On click:
  - Fetch the next expense page.
  - Append rows.
  - Deduplicate by expense `id`.
  - Update `expensesNextBefore`.
  - Update `hasMoreExpenses`.

### 4. Settled Expense Groups UI

Change the current settled expense group behavior:

- Group details starts with only the newest `5` settlement metadata rows.
- Each row displays:
  - from user
  - to user
  - amount
  - settled date
  - expense count
- Replace `Expand` with `Load`.
- Add a section-level `Load more settlements` button when `hasMoreSettlements` is true.

On `Load` for a settlement row:

- Fetch:

```http
GET /api/v1/groups/{groupId}/settlements/{settlementId}/expenses?limit=25
```

- Render the linked expense rows under that settlement.
- If `hasMoreExpenses` is true for that settlement, show:
  - `Load more expenses`
- On additional clicks, fetch the next page for that settlement and append.

### 5. Frontend Tests

Add or update tests for:

- Initial group view shows only returned settlement rows.
- `Load older expenses` appends rows without replacing current expenses.
- `Load more settlements` appends settlement metadata rows.
- Settlement row `Load` fetches linked expenses only for that settlement.
- Settlement row with many linked expenses shows `Load more expenses`.
- Loading and error states do not block unrelated group actions.

## Documentation And GitHub Issue Cleanup

Before closing issue 31:

- Update stale local docs that still mention 50-item pages.
- Update stale local docs that describe settlement detail as returning all linked `expenseIds`.
- Update the GitHub issue body to reflect:
  - 25-item main expense pages.
  - 5-item settlement row pages.
  - settlement metadata rows with `expenseCount`.
  - paginated settlement linked expense details.
- Verify the implemented route names match the issue body exactly.

## Closeout Verification

Run targeted backend tests for the new endpoint contracts.

Run the frontend test slice that covers group pagination behavior.

Capture runtime/perf evidence for the RAM group:

- Initial group details returns `25 / 600` expenses.
- Initial group details returns only `5` settlement rows when more exist.
- `hasMoreExpenses` is true when more expenses exist.
- `hasMoreSettlements` is true when more settlement rows exist.
- Loading a settlement with many linked expenses returns only the first bounded page.
- Payload size and frontend render timing improve against the saved baseline.
