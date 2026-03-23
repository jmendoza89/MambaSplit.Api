# Issue: Implement backend endpoint for transactional email send

## Title
Implement transactional email send endpoint with SMTP2GO + Cloudflare DNS auth

## Summary
Add an internal API endpoint to send transactional emails from the API using SMTP2GO with Cloudflare DNS authentication. The endpoint should be accessible only to internal services (auth via API key / internal role), validate payloads, enqueue/send email, record audit logs, and return clear error responses.

## Acceptance Criteria
- New POST `/internal/email/send` endpoint implemented in `InternalEmailController` or a new controller under `Controllers/InternalEmailController.cs`.
- Endpoint requires internal API key or `InternalSender` role; unauthorized requests return 401/403.
- Request schema validated (to/from/subject/html/text/templateId/mergeData). Invalid payloads return 400 with error details.
- Sends email via SMTP2GO using configured credentials; retries on transient failures (3 retries exponential backoff).
- Audit stored in `EmailAudit` table (or existing audit table) with status, request payload, and provider response.
- Database migration file included for any new table(s) and reversible via EF migrations.
- Unit tests and integration tests added covering validation, authorization, success, and failure paths.
- No secrets committed; configuration uses `appsettings.*` and secrets (environment or KeyVault). Document where to set secrets.

## Implementation notes
-- Follow repository rules and conventions; do not include secrets in commits.
- Provide complete file-level diffs in unified patch format and also full file contents for reviewers.
- Provide migration SQL or EF migration code.

## Suggested labels
- backend, api, email, migration, tests, internal

## How to create the GitHub issue (local)
Using `gh` CLI:

```powershell
cd c:\MambaSplit\MambaSplit.Api
gh issue create --title "Implement transactional email send endpoint with SMTP2GO + Cloudflare DNS auth" --body-file prompts\backend-agent-prompt\issue.md --label backend --label api --assignee @your-github-handle
```

(Or open the issue in the web UI and paste the contents.)

---

(When ready, the implementing agent should produce the implementation artifacts described in the implementation spec.)
