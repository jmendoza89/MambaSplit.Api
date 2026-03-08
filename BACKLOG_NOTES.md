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

## Invite by user (API support)

- Date: 2026-03-08
- Priority: High
- Context: Invite creation is currently email-based.
- Gap: Frontend roadmap requires selecting existing users in-app, not free-form email only.
- Follow-up:
  - Add authenticated user search endpoint for picker support (id, displayName, email).
  - Add non-breaking invite creation endpoint that accepts `userId` and reuses existing invite rules.
