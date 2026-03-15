# Backlog Notes

## Settlement auto-allocation (deferred)

- Date: 2026-03-10
- Priority: High
- Implementation status review date: 2026-03-15
- Status summary: Deferred design; not implemented in current backend.
- Context: Current settlement flow supports cash settlement entries, but does not allocate payment against split-level debt rows.
- Gap: Without allocation, the system cannot answer "which exact debt rows were paid by this settlement" or support robust partial debt coverage tracking.

### Current implementation status (verified)

- [x] `settlements` payment header exists.
- [x] Settlement to expense-header links exist via `settlement_expenses`.
- [x] Settlement creation currently requires `expenseIds` and rejects empty lists.
- [x] Settlement amount is validated against selected expenses' computed pair balance.
- [x] One expense can be linked to only one settlement (unique on `settlement_expenses.expense_id`).
- [ ] `settlement_split_allocations` table exists.
- [ ] Split-level allocation records exist for each settlement payment.
- [ ] FIFO auto-allocation across eligible split rows exists.
- [ ] Invariant enforcement at split-allocation level exists.
- [ ] API accepts minimal pair + amount payload without explicit `expenseIds`.
- [ ] Projection endpoints for pair remaining owed and settlement allocation breakdown exist.

### Notes on model mismatch

- Current model settles at expense-header level (`settlement_expenses`), not split-debt-row level.
- Current balance updates are pair/net based; there is no split-allocation ledger.
- Proposed data model:
  - Keep `settlements` as the payment header record.
  - Add `settlement_split_allocations`:
    - `id uuid pk`
    - `settlement_id uuid not null fk -> settlements(id) on delete cascade`
    - `expense_split_id uuid not null fk -> expense_splits(id) on delete cascade`
    - `amount_cents bigint not null check (amount_cents > 0)`
  - Add unique/indexing to prevent duplicate ambiguity and support query performance:
    - index on `(settlement_id)`
    - index on `(expense_split_id)`
- Allocation algorithm (backend auto mode):
  - Input: `groupId`, `fromUserId` (debtor), `toUserId` (creditor), `amountCents`.
  - Fetch candidate split debts where:
    - split belongs to expenses in the group,
    - split.user_id = `fromUserId`,
    - expense.payer_user_id = `toUserId`,
    - remaining split debt > 0 after prior allocations.
  - Order deterministically (oldest expense first, then split id).
  - Allocate settlement amount FIFO across rows until amount is exhausted.
- Required invariants (single transaction):
  - `sum(allocations.amount_cents) == settlements.amount_cents`
  - allocation per split must not exceed split remaining debt
  - reject settlement if amount exceeds total remaining pair debt (or cap by policy)
  - no negative or zero allocations
- Rollout notes:
  - Keep API request minimal (no per-split allocation payload required from frontend).
  - Frontend can continue sending only pair + amount.
  - Add response projection endpoint(s) for "remaining owed by pair" and settlement breakdown details.
