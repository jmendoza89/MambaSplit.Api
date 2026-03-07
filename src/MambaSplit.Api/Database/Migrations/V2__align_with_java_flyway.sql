DO $$
BEGIN
    BEGIN
        CREATE EXTENSION IF NOT EXISTS pgcrypto;
    EXCEPTION
        WHEN insufficient_privilege THEN
            RAISE NOTICE 'Skipping pgcrypto extension creation due to insufficient privilege.';
    END;
END $$;

CREATE TABLE IF NOT EXISTS settlements (
    id uuid PRIMARY KEY,
    group_id uuid NOT NULL,
    from_user_id uuid NOT NULL,
    to_user_id uuid NOT NULL,
    amount_cents bigint NOT NULL,
    note character varying(500),
    created_at timestamp with time zone NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_group_members_user ON group_members (user_id);
CREATE INDEX IF NOT EXISTS idx_expenses_group_created ON expenses (group_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_settlements_group_created ON settlements (group_id, created_at DESC);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_expenses_amount_positive'
    ) THEN
        ALTER TABLE expenses
            ADD CONSTRAINT ck_expenses_amount_positive CHECK (amount_cents > 0);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_expense_splits_amount_non_negative'
    ) THEN
        ALTER TABLE expense_splits
            ADD CONSTRAINT ck_expense_splits_amount_non_negative CHECK (amount_owed_cents >= 0);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_settlements_amount_positive'
    ) THEN
        ALTER TABLE settlements
            ADD CONSTRAINT ck_settlements_amount_positive CHECK (amount_cents > 0);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_groups_users_created_by'
    ) THEN
        ALTER TABLE groups
            ADD CONSTRAINT fk_groups_users_created_by
                FOREIGN KEY (created_by) REFERENCES users (id);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_group_members_groups_group_id'
    ) THEN
        ALTER TABLE group_members
            ADD CONSTRAINT fk_group_members_groups_group_id
                FOREIGN KEY (group_id) REFERENCES groups (id) ON DELETE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_group_members_users_user_id'
    ) THEN
        ALTER TABLE group_members
            ADD CONSTRAINT fk_group_members_users_user_id
                FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_invites_groups_group_id'
    ) THEN
        ALTER TABLE invites
            ADD CONSTRAINT fk_invites_groups_group_id
                FOREIGN KEY (group_id) REFERENCES groups (id) ON DELETE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_expenses_groups_group_id'
    ) THEN
        ALTER TABLE expenses
            ADD CONSTRAINT fk_expenses_groups_group_id
                FOREIGN KEY (group_id) REFERENCES groups (id) ON DELETE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_expenses_users_payer_user_id'
    ) THEN
        ALTER TABLE expenses
            ADD CONSTRAINT fk_expenses_users_payer_user_id
                FOREIGN KEY (payer_user_id) REFERENCES users (id);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_expenses_users_created_by_user_id'
    ) THEN
        ALTER TABLE expenses
            ADD CONSTRAINT fk_expenses_users_created_by_user_id
                FOREIGN KEY (created_by_user_id) REFERENCES users (id);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_expense_splits_users_user_id'
    ) THEN
        ALTER TABLE expense_splits
            ADD CONSTRAINT fk_expense_splits_users_user_id
                FOREIGN KEY (user_id) REFERENCES users (id);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_refresh_tokens_users_user_id'
    ) THEN
        ALTER TABLE refresh_tokens
            ADD CONSTRAINT fk_refresh_tokens_users_user_id
                FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_settlements_groups_group_id'
    ) THEN
        ALTER TABLE settlements
            ADD CONSTRAINT fk_settlements_groups_group_id
                FOREIGN KEY (group_id) REFERENCES groups (id) ON DELETE CASCADE;
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_settlements_users_from_user_id'
    ) THEN
        ALTER TABLE settlements
            ADD CONSTRAINT fk_settlements_users_from_user_id
                FOREIGN KEY (from_user_id) REFERENCES users (id);
    END IF;
END $$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_settlements_users_to_user_id'
    ) THEN
        ALTER TABLE settlements
            ADD CONSTRAINT fk_settlements_users_to_user_id
                FOREIGN KEY (to_user_id) REFERENCES users (id);
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS uk_users_email_ci ON users (lower(email));
CREATE UNIQUE INDEX IF NOT EXISTS uk_users_google_sub ON users (google_sub) WHERE google_sub IS NOT NULL;

WITH ranked_invites AS (
    SELECT
        id,
        row_number() OVER (
            PARTITION BY group_id, lower(email)
            ORDER BY created_at DESC, id DESC
        ) AS rn
    FROM invites
)
DELETE FROM invites i
USING ranked_invites r
WHERE i.id = r.id
  AND r.rn > 1;

CREATE UNIQUE INDEX IF NOT EXISTS uk_invites_group_email_ci ON invites (group_id, lower(email));
