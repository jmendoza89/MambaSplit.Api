# Backlog Notes

## Expense payer attribution flexibility

- Date: 2026-03-07
- Context: The API currently allows any group member to create an expense with `payerUserId` set to another member.
- Decision for now: Keep this behavior enabled.
- Reason: This can support "enter expense on behalf of another member" workflows (similar to Splitwise).
- Follow-up: Revisit with explicit product rules for permissions, audit trail, and UI messaging so this remains intentional and safe.

## Settlements and payment tracking

- Date: 2026-03-07
- Priority: High
- Context: Current balances are derived from expense ledger entries only.
- Gap: There is no first-class "payment/settlement" event to represent debts that were actually paid between members.
- Follow-up: Add settlement transactions (and reversals) so balances reflect real-world repayment, not just expense allocation.
- Backlog (Admin Portal): expose settlement reset/reversal management UI in a dedicated admin portal only. Do not surface delete/reset controls in the group member frontend.

## Invite by user (API support)

- Date: 2026-03-08
- Priority: High
- Context: Invite creation is currently email-based.
- Gap: Frontend roadmap requires selecting existing users in-app, not free-form email only.
- Follow-up:
  - Add authenticated user search endpoint for picker support (id, displayName, email).
  - Add non-breaking invite creation endpoint that accepts `userId` and reuses existing invite rules.

## Settlement auto-allocation (deferred)

- Date: 2026-03-10
- Priority: High
- Context: Current settlement flow supports cash settlement entries, but does not allocate payment against split-level debt rows.
- Gap: Without allocation, the system cannot answer "which exact debt rows were paid by this settlement" or support robust partial debt coverage tracking.
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
