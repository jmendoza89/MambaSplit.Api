CREATE TABLE IF NOT EXISTS users (
    id uuid PRIMARY KEY,
    email character varying(320) NOT NULL,
    password_hash character varying(200) NOT NULL,
    google_sub character varying(255),
    display_name character varying(120) NOT NULL,
    created_at timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS groups (
    id uuid PRIMARY KEY,
    name character varying(200) NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS group_members (
    id uuid PRIMARY KEY,
    group_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role character varying(20) NOT NULL,
    joined_at timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS invites (
    id uuid PRIMARY KEY,
    group_id uuid NOT NULL,
    email character varying(320) NOT NULL,
    token_hash character varying(120) NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS expenses (
    id uuid PRIMARY KEY,
    group_id uuid NOT NULL,
    payer_user_id uuid NOT NULL,
    created_by_user_id uuid NOT NULL,
    description character varying(300) NOT NULL,
    amount_cents bigint NOT NULL,
    reversal_of_expense_id uuid,
    idempotency_key character varying(120),
    idempotency_hash character varying(120),
    created_at timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS expense_splits (
    id uuid PRIMARY KEY,
    expense_id uuid NOT NULL,
    user_id uuid NOT NULL,
    amount_owed_cents bigint NOT NULL
);

CREATE TABLE IF NOT EXISTS refresh_tokens (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL,
    token_hash character varying(120) NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_group_members_group_id_user_id ON group_members (group_id, user_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_invites_token_hash ON invites (token_hash);
CREATE UNIQUE INDEX IF NOT EXISTS ix_expense_splits_expense_id_user_id ON expense_splits (expense_id, user_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_expenses_reversal_of_expense_id ON expenses (reversal_of_expense_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_expenses_group_id_created_by_user_id_idempotency_key ON expenses (group_id, created_by_user_id, idempotency_key);
CREATE UNIQUE INDEX IF NOT EXISTS ix_refresh_tokens_token_hash ON refresh_tokens (token_hash);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_expense_splits_expenses_expense_id'
    ) THEN
        ALTER TABLE expense_splits
            ADD CONSTRAINT fk_expense_splits_expenses_expense_id
                FOREIGN KEY (expense_id) REFERENCES expenses (id) ON DELETE CASCADE;
    END IF;
END $$;
