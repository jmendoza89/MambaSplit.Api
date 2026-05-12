# Frontend Pagination and Settlement Lazy-Load - Implementation Plan

Relates to: MambaSplit.Api #30 and #31
See also: [frontend-perf-harness-plan.md](frontend-perf-harness-plan.md) for baseline and verification metrics.

These frontend changes depend on the API contract from issue #31.

## 1. API Contract Expected From #31

Use one default page size across the API and frontend: **50**.

### Group Details

Current response returns all expenses and all settlements.

New response:

```jsonc
{
  "group": { },
  "me": { },
  "members": [],
  "expenses": [ /* 50 newest expenses */ ],
  "hasMoreExpenses": true,
  "settlements": [ /* 50 newest settlements, no expenseIds */ ],
  "hasMoreSettlements": false,
  "settlementSuggestions": [],
  "summary": {
    "expenseCount": 600,
    "totalExpenseAmountCents": 19387540,
    "settlementCount": 0,
    "totalSettlementAmountCents": 0
  }
}
```

Required behavior:

- `expenses` is capped at 50 and sorted newest first.
- `summary.expenseCount` remains the full group total.
- `hasMoreExpenses` is true when more than 50 expenses exist.
- `settlements` is capped at 50 and sorted newest first.
- `summary.settlementCount` remains the full settlement total.
- `hasMoreSettlements` is true when more than 50 settlements exist.
- Initial `SettlementInfoDto` omits `expenseIds`; linked expenses are loaded on demand.

### Paginated Expenses

```http
GET /api/v1/groups/{groupId}/expenses?before={isoTimestamp}&limit=50
```

Returns:

```jsonc
{
  "expenses": [],
  "hasMore": true
}
```

Cursor rule: use the `createdAt` of the oldest loaded expense as `before`. The API should use a stable secondary sort, such as `createdAt DESC, id DESC`, if duplicate timestamps are possible.

### Settlement Detail

```http
GET /api/v1/groups/{groupId}/settlements/{settlementId}
```

Returns the selected settlement plus the full linked expenses needed by the expanded row:

```jsonc
{
  "id": "...",
  "groupId": "...",
  "fromUserId": "...",
  "fromUserName": "...",
  "toUserId": "...",
  "toUserName": "...",
  "amountCents": 15000,
  "note": null,
  "settledAt": "2025-06-01T14:30:00Z",
  "expenses": []
}
```

The detail endpoint must enforce group membership and ensure the settlement belongs to the requested group.

## 2. Expense Pagination UX

Initial group open shows the 50 newest expenses. Older expenses are loaded by an explicit button at the bottom of the list.

```ts
interface ExpensePaginationState {
  expenses: ExpenseInfo[];
  hasMore: boolean;
  isLoadingMore: boolean;
  oldestCursor: string | null;
}
```

Behavior:

1. Initialize from `details.expenses` and `details.hasMoreExpenses`.
2. Hide the button when `hasMore` is false.
3. Disable the button and show loading state while fetching.
4. Append returned expenses, preserving newest-to-oldest order.
5. Update `oldestCursor` from the oldest appended item.
6. Keep errors inline near the button with a retry path.

Do not use infinite scroll for this pass.

## 3. Settlement Lazy-Load UX

Initial group open shows the 50 newest settlement rows without linked expense details. A row fetches linked expenses only on first expand, then caches the result in memory.

```ts
type SettlementDetailState =
  | { status: "idle" }
  | { status: "loading" }
  | { status: "loaded"; expenses: ExpenseInfo[] }
  | { status: "error"; message: string };
```

Behavior:

1. Settlement row shows amount, date, from/to names, and note from the initial payload.
2. Expand starts `fetchSettlementDetail(groupId, settlementId)` only when state is `idle` or retrying after `error`.
3. Loading and error states render inside the expanded area.
4. Loaded state is reused when collapsing and expanding again.
5. If a settlement has many linked expenses, render the linked expense list compactly and avoid adding those expenses to the main expense pagination state.

## 4. API Client Additions

```ts
export async function fetchMoreExpenses(
  groupId: string,
  before: string,
  limit = 50,
): Promise<{ expenses: ExpenseInfo[]; hasMore: boolean }> {
  const res = await apiClient.get(`/groups/${groupId}/expenses`, {
    params: { before, limit },
  });
  return res.data;
}

export async function fetchSettlementDetail(
  groupId: string,
  settlementId: string,
): Promise<SettlementDetail> {
  const res = await apiClient.get(
    `/groups/${groupId}/settlements/${settlementId}`,
  );
  return res.data;
}
```

## 5. Type Updates

```ts
interface SettlementInfoDto {
  id: string;
  groupId: string;
  fromUserId: string;
  fromUserName: string;
  toUserId: string;
  toUserName: string;
  amountCents: number;
  note: string | null;
  settledAt: string;
}

interface SettlementDetail extends SettlementInfoDto {
  expenses: ExpenseInfo[];
}

interface GroupDetailsDto {
  group: GroupInfo;
  me: MeInfo;
  members: MemberInfo[];
  expenses: ExpenseInfo[];
  hasMoreExpenses: boolean;
  settlements: SettlementInfoDto[];
  hasMoreSettlements: boolean;
  settlementSuggestions: SettlementSuggestion[];
  summary: Summary;
}
```

## 6. Implementation Order

1. Update frontend types after API #31 lands.
2. Update the group details API client type mapping.
3. Add `fetchMoreExpenses()` and `fetchSettlementDetail()`.
4. Remove any initial-render dependency on `SettlementInfoDto.expenseIds`.
5. Add settlement expand/collapse with lazy detail fetch and cache.
6. Add expense pagination state and "Load more expenses".
7. Verify with the dev perf overlay: RAM group should show `50 / 600`, smaller payload, and `hasMoreExpenses = true`.

## 7. Files to Create or Modify in mambasplit-web

| File | Action |
|---|---|
| `src/types/group.ts` | Update group and settlement response types |
| `src/api/groups.ts` | Add paginated expense and settlement detail calls |
| `src/pages/GroupPage.tsx` or equivalent | Own expense pagination and settlement detail state |
| `src/components/group/GroupExpenseList.tsx` | Add explicit load-more control |
| `src/components/group/SettlementRow.tsx` | Add expand/collapse and lazy detail rendering |

## 8. Verification

- Initial RAM group load renders 50 expenses, not 600.
- "Load more expenses" appends older expenses in the correct order.
- Settlement rows no longer require `expenseIds` in the initial payload.
- Expanding a settlement fetches linked expenses once and reuses cached detail.
- The perf overlay shows lower payload size and lower group open render time after #31.
