ALTER TABLE invites
    ADD COLUMN IF NOT EXISTS sent_by_user_id uuid;

UPDATE invites i
SET sent_by_user_id = g.created_by
FROM groups g
WHERE i.group_id = g.id
  AND i.sent_by_user_id IS NULL;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM invites
        WHERE sent_by_user_id IS NULL
    ) THEN
        RAISE EXCEPTION 'Unable to backfill invites.sent_by_user_id for one or more rows.';
    END IF;
END $$;

ALTER TABLE invites
    ALTER COLUMN sent_by_user_id SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_invites_users_sent_by_user_id'
    ) THEN
        ALTER TABLE invites
            ADD CONSTRAINT fk_invites_users_sent_by_user_id
                FOREIGN KEY (sent_by_user_id) REFERENCES users (id) ON DELETE CASCADE;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_invites_group_sender_created_desc
    ON invites (group_id, sent_by_user_id, created_at DESC);

DROP TABLE IF EXISTS public.flyway_schema_history;
