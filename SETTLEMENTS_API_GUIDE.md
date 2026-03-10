# Settlement API - Implementation Guide

## Summary

The Settlement API endpoints enable users to record payments between group members. This aligns with the frontend UI shown in the submission form where users can record who paid whom and for how much.

## Frontend to Backend Contract

### Frontend Settlement Form
From the UI screenshot, the settlement form captures:
- **From User**: The person making the payment (e.g., Julio)
- **To User**: The person receiving the payment (e.g., Julio C. Mendoza)
- **Amount**: In dollars and cents (e.g., $53.00)
- **Date**: When the settlement occurred (e.g., 03/09/2026)
- **Note**: Optional description

### Backend Settlement Table
All settlements are stored in the `settlements` table with this structure:

```sql
CREATE TABLE settlements (
    id uuid PRIMARY KEY,                    -- Unique settlement ID
    group_id uuid NOT NULL,                 -- Which group this settlement belongs to
    from_user_id uuid NOT NULL,             -- User paying
    to_user_id uuid NOT NULL,               -- User receiving payment
    amount_cents bigint NOT NULL,           -- Amount in cents (5300 = $53.00)
    note character varying(500),            -- Optional note/description
    created_at timestamp with time zone NOT NULL  -- When settlement was created
);
```

## API Endpoints

### 1. Create Settlement
**POST** `/api/v1/groups/{groupId}/settlements`

Creates a new settlement (payment) in a group.

#### Request
```json
{
  "fromUserId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "toUserId": "d4c85f64-5717-4562-b3fc-2c963f66afa7",
  "amountCents": 5300,
  "note": "Coffee meeting payment"
}
```

#### Response (201 Created)
```json
{
  "settlementId": "7fa95f64-5717-4562-b3fc-2c963f66afa8"
}
```

#### Status Codes
- `201 Created` - Settlement created successfully
- `400 Bad Request` - Invalid amount, users the same, or validation error
- `404 Not Found` - Group or user not found
- `401 Unauthorized` - User not authenticated

---

### 2. List Group Settlements
**GET** `/api/v1/groups/{groupId}/settlements`

Lists all settlements for a group, most recent first. User must be a member of the group.

#### Response
```json
{
  "settlements": [
    {
      "id": "7fa95f64-5717-4562-b3fc-2c963f66afa8",
      "fromUserId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "fromUserName": "Julio",
      "toUserId": "d4c85f64-5717-4562-b3fc-2c963f66afa7",
      "toUserName": "Julio C. Mendoza",
      "amountCents": 5300,
      "note": "Reimbursement for lunch",
      "createdAt": "2026-03-09T18:30:45.123456Z"
    },
    {
      "id": "6fa85f54-4606-3451-a2eb-1b852e55af97",
      "fromUserId": "d4c85f64-5717-4562-b3fc-2c963f66afa7",
      "fromUserName": "Julio C. Mendoza",
      "toUserId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "toUserName": "Julio",
      "amountCents": 2000,
      "note": "Venmo for gas",
      "createdAt": "2026-03-08T14:15:32.654321Z"
    }
  ]
}
```

---

### 3. Get Settlement Details
**GET** `/api/v1/settlements/{settlementId}`

Retrieves details of a specific settlement.

#### Response
```json
{
  "id": "7fa95f64-5717-4562-b3fc-2c963f66afa8",
  "fromUserId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fromUserName": "Julio",
  "toUserId": "d4c85f64-5717-4562-b3fc-2c963f66afa7",
  "toUserName": "Julio C. Mendoza",
  "amountCents": 5300,
  "note": "Reimbursement for lunch",
  "createdAt": "2026-03-09T18:30:45.123456Z"
}
```

---

### 4. List User Settlements
**GET** `/api/v1/users/{userId}/settlements`

Lists all settlements involving a specific user across all groups.

#### Response
```json
{
  "settlements": [
    {
      "id": "7fa95f64-5717-4562-b3fc-2c963f66afa8",
      "fromUserId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "fromUserName": "Julio",
      "toUserId": "d4c85f64-5717-4562-b3fc-2c963f66afa7",
      "toUserName": "Julio C. Mendoza",
      "amountCents": 5300,
      "note": "Reimbursement for lunch",
      "createdAt": "2026-03-09T18:30:45.123456Z"
    }
  ]
}
```

---

## Example Settlement Database Entries

### Sample Group: "LoveNest"
Group ID: `550e8400-e29b-41d4-a716-446655440000`

### Sample Users
| User ID | Display Name | Email |
|---------|---|---|
| `3fa85f64-5717-4562-b3fc-2c963f66afa6` | Julio | julio@example.com |
| `d4c85f64-5717-4562-b3fc-2c963f66afa7` | Julio C. Mendoza | julioC@example.com |
| `a1b2c3d4-e5f6-4a5b-9c8d-7e6f5a4b3c2d` | Alice Johnson | alice@example.com |
| `b2c3d4e5-f6a7-5b6c-ad9e-8f7g6b5c4d3e` | Bob Smith | bob@example.com |

### Settlement Records

#### Settlement 1: Julio pays Julio C. Mendoza
```sql
INSERT INTO settlements (id, group_id, from_user_id, to_user_id, amount_cents, note, created_at)
VALUES (
  '7fa95f64-5717-4562-b3fc-2c963f66afa8',
  '550e8400-e29b-41d4-a716-446655440000',
  '3fa85f64-5717-4562-b3fc-2c963f66afa6',      -- from: Julio
  'd4c85f64-5717-4562-b3fc-2c963f66afa7',      -- to: Julio C. Mendoza
  5300,                                         -- $53.00
  'Reimbursement for lunch',
  '2026-03-09T18:30:45.123456Z'
);
```

**Frontend Data:**
- From: Julio (J)
- To: Julio C. Mendoza (JC)
- Amount: $53.00
- Date: 03/09/2026
- Note: "Reimbursement for lunch"

---

#### Settlement 2: Julio C. Mendoza pays Julio (Reversal)
```sql
INSERT INTO settlements (id, group_id, from_user_id, to_user_id, amount_cents, note, created_at)
VALUES (
  '6fa85f54-4606-3451-a2eb-1b852e55af97',
  '550e8400-e29b-41d4-a716-446655440000',
  'd4c85f64-5717-4562-b3fc-2c963f66afa7',      -- from: Julio C. Mendoza
  '3fa85f64-5717-4562-b3fc-2c963f66afa6',      -- to: Julio
  2000,                                         -- $20.00
  'Gas money reimbursement',
  '2026-03-08T14:15:32.654321Z'
);
```

---

#### Settlement 3: Alice pays Bob
```sql
INSERT INTO settlements (id, group_id, from_user_id, to_user_id, amount_cents, note, created_at)
VALUES (
  '9cb76f78-88e2-5d6f-c41a-5d7e4f9a8b0c',
  '550e8400-e29b-41d4-a716-446655440000',
  'a1b2c3d4-e5f6-4a5b-9c8d-7e6f5a4b3c2d',     -- from: Alice Johnson
  'b2c3d4e5-f6a7-5b6c-ad9e-8f7g6b5c4d3e',     -- to: Bob Smith
  7500,                                         -- $75.00
  'Concert tickets reimbursement',
  '2026-03-07T10:00:00.000000Z'
);
```

---

## API Contract Summary

### Request Models

#### CreateSettlementRequest
```csharp
public class CreateSettlementRequest
{
    /// <summary>User ID paying</summary>
    public string FromUserId { get; set; }
    
    /// <summary>User ID receiving payment</summary>
    public string ToUserId { get; set; }
    
    /// <summary>Amount in cents (e.g., 5300 = $53.00)</summary>
    public long AmountCents { get; set; }

    /// <summary>Expense rows that will be marked settled by this settlement</summary>
    public List<string> ExpenseIds { get; set; }
    
    /// <summary>Optional note (max 500 characters)</summary>
    public string? Note { get; set; }
}
```

### Response Models

#### SettlementDetailsResponse
```csharp
public class SettlementDetailsResponse
{
    public string Id { get; set; }
    public string GroupId { get; set; }
    public string FromUserId { get; set; }
    public string FromUserName { get; set; }
    public string ToUserId { get; set; }
    public string ToUserName { get; set; }
    public long AmountCents { get; set; }
    public string? Note { get; set; }
    public string SettledAt { get; set; }  // ISO 8601 format
    public List<string> ExpenseIds { get; set; }
}
```

#### CreateSettlementResponse
```csharp
public class CreateSettlementResponse
{
    public string SettlementId { get; set; }
}
```

#### ListSettlementsResponse
```csharp
public class ListSettlementsResponse
{
    public List<SettlementDetailsResponse> Settlements { get; set; }
}
```

## Business Rules

1. **Amount Validation**: Amount must be greater than 0
2. **User Validation**: From and To users cannot be the same user
3. **Group Membership**: Both users must be members of the group
4. **Note Length**: Optional note cannot exceed 500 characters
5. **Expense Selection Required**: At least one expense ID is required
6. **Strict Amount Match**: `amountCents` must equal the sum of selected expenses
7. **Immutability In Group Frontend**: Settlements are create/list only in the normal group UI
8. **Non-exclusive**: Recording settlements does NOT close the group or affect expense tracking

## Frontend Integration Notes

1. **Currency Format**: The API uses cents (long) to avoid floating-point precision issues
   - $53.00 = 5300 cents
   - $0.01 = 1 cent

2. **User Names**: The response includes `fromUserName` and `toUserName` so you don't need a separate user lookup

3. **Timestamps**: All timestamps are in ISO 8601 format (e.g., "2026-03-09T18:30:45.123456Z")

4. **URL Paths**: All IDs in the URL and request body are string representations of UUIDs

## Admin Backlog Note

- Future admin-portal-only endpoint exists for settlement reset:
  - `DELETE /api/v1/admin/groups/{groupId}/settlements`
  - Required header: `X-Admin-Portal-Token`
- Do not implement this action in the regular group/member frontend.
