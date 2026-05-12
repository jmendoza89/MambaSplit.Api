# MambaSplit Email Notification Audit (April 2026)

## Template Inventory
 
| Template Key         | Files Present                | Token Contract                                                                 | BuildTokens Validation |
|--------------------- |-----------------------------|-------------------------------------------------------------------------------|-----------------------|
| `invite`             | ✅ .html / .txt / .subject   | `groupName`, `inviterName`, `inviteToken` (required); `inviteLink`, `inviteExpiresInText`, `inviteExpiresAtTooltip` (derived) | ✅ Enforced           |
| `invite-declined`    | ✅ .html / .txt / .subject   | `groupName`, `inviteeName`, `inviteeEmail`, `declinedAtDisplay` (required); `groupLink` derived from `groupId`              | ✅ Enforced           |
| `settlement`         | ✅ .html / .txt / .subject   | `groupName`, `payerName`, `receiverName`, `amountDisplay`, `settledAtDisplay`, `expenseCountText`, `noteText` (required); `groupLink` derived from `groupId` | ✅ Enforced           |
| `welcome`            | ✅ .html / .txt / .subject   | `firstName`, `appLink`                                                        | ❌ No validation      |
| `release-announcement`| ✅ .html / .txt / .subject  | Unknown (no wiring found)                                                      | ❌ No validation      |
| `release-v1.2.0`     | ✅ .html / .txt / .subject   | `firstName`, `appLink`, `screenshotMain`, `screenshotGroup`                    | ❌ No validation      |

---

## Wiring Status

| Template Key         | Wired to a Service or Controller | Call Site Description |
|---------------------|----------------------------------|----------------------|
| `invite`            | ✅ **Wired**                      | `GroupService.SendInviteEmailAsync` — fires when a group invite is sent |
| `invite-declined`   | ✅ **Wired**                      | `GroupService.SendInviteDeclinedEmailAsync` — fires when an invitee declines |
| `settlement`        | ✅ **Wired**                      | `SettlementService` — fires when a settlement is recorded |
| `release-v1.2.0`    | ✅ **Wired (internal only)**       | `InternalEmailController.SendReleaseV120` — admin blast to all users |
| `welcome`           | ❌ **NOT WIRED**                  | Template exists, integration tests exist, but `AuthService.SignupAsync` never calls `TransactionalEmailService` |
| `release-announcement`| ❌ **NOT WIRED**                 | Template exists but no code calls `SendTemplateAsync("release-announcement", ...)` |

---

## Gap Analysis

### Gap 1 — Welcome Email (confirmed missing)
- `AuthService.SignupAsync` creates the user and returns without sending any email.
- The `welcome` template is complete (all 3 files exist, integration tests validate render output), but it is never called.
- The same gap applies to `AuthenticateGoogleAsync` when it creates a **new** Google-linked user — that code path also bypasses any welcome email.

### Gap 2 — `release-announcement` Template Is Dead Code
- The template triplet exists and was likely a predecessor to `release-v1.2.0`.
- No service or controller references it. It should either be wired up as the canonical release blast template (replacing `release-v1.2.0`) or removed.

### Gap 3 — No Expense Notification
- `ExpenseService` has zero email references.
- When a member adds an expense that splits the cost across group members, no one gets notified. This is a high-value notification for a bill-splitting app.

### Gap 4 — No "You Were Added to a Group" Email
- When an existing user accepts an invite (or is added directly), there is no email confirming group membership.
- The invite email covers the outreach, but there is no confirmation to the new member.

### Gap 5 — No Settlement Request / Reminder
- There is a settlement confirmation email (`settlement`), but no email for requesting that someone pay up or reminding them of an outstanding balance.
- Groups with outstanding debts have no automated nudge path.

### Gap 6 — No Password Reset / Account Security Email
- `AuthService` supports `ChangePasswordAsync`, but there is no email for:
  - Password reset request (forgot password flow — the endpoint itself may not exist, but the email infra would need to support it)
  - Password changed confirmation

---

## Recommended New Templates

| Template Key         | Trigger                                                      | Priority |
|---------------------|--------------------------------------------------------------|----------|
| `welcome`           | Wire existing template to `AuthService.SignupAsync` and new Google user path | **High** (template already built, just needs wiring) |
| `expense-added`     | New expense created in a group — notify all split participants except the payer | High     |
| `group-joined`      | User accepts invite and joins a group                        | Medium   |
| `password-reset`    | User requests password reset (requires forgot-password endpoint) | Medium   |
| `password-changed`  | User successfully changes their password                     | Medium   |
| `settlement-reminder`| Triggered manually or on schedule when a balance is outstanding | Low      |

---

## Summary

- **3 templates wired and active**: `invite`, `invite-declined`, `settlement`
- **1 template wired but admin-only**: `release-v1.2.0`
- **1 template complete but unwired**: `welcome` — the most urgent gap
- **1 template orphaned**: `release-announcement`
- **Most impactful missing templates**: welcome (on signup), expense-added notification, and a password reset flow
