-- V8: Create friend_connections table and backfill existing group co-members

CREATE TABLE IF NOT EXISTS friend_connections (
    id uuid PRIMARY KEY,
    owner_user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    friend_user_id uuid REFERENCES users(id) ON DELETE SET NULL,
    display_name character varying(120) NOT NULL,
    normalized_email character varying(320) NOT NULL,
    original_email character varying(320) NOT NULL,
    status character varying(20) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    connected_at_utc timestamp with time zone,
    last_used_at_utc timestamp with time zone
);

CREATE UNIQUE INDEX ix_friend_connections_owner_email
    ON friend_connections (owner_user_id, normalized_email);

CREATE INDEX ix_friend_connections_owner
    ON friend_connections (owner_user_id);

-- Backfill: create bidirectional Connected friend rows for every pair of users
-- who share at least one group via group_members.
-- connected_at_utc = earliest time both users were in the same group.
-- last_used_at_utc = same as connected_at_utc (no activity data to infer from).
INSERT INTO friend_connections (id, owner_user_id, friend_user_id, display_name, normalized_email, original_email, status, created_at_utc, connected_at_utc, last_used_at_utc)
SELECT
    gen_random_uuid(),
    a.user_id,
    b.user_id,
    ub.display_name,
    LOWER(ub.email),
    ub.email,
    'Connected',
    MIN(GREATEST(a.joined_at, b.joined_at)),
    MIN(GREATEST(a.joined_at, b.joined_at)),
    MIN(GREATEST(a.joined_at, b.joined_at))
FROM group_members a
JOIN group_members b ON a.group_id = b.group_id AND a.user_id < b.user_id
JOIN users ub ON ub.id = b.user_id
GROUP BY a.user_id, b.user_id, ub.display_name, ub.email
ON CONFLICT (owner_user_id, normalized_email) DO NOTHING;

-- Reverse direction (b -> a)
INSERT INTO friend_connections (id, owner_user_id, friend_user_id, display_name, normalized_email, original_email, status, created_at_utc, connected_at_utc, last_used_at_utc)
SELECT
    gen_random_uuid(),
    b.user_id,
    a.user_id,
    ua.display_name,
    LOWER(ua.email),
    ua.email,
    'Connected',
    MIN(GREATEST(a.joined_at, b.joined_at)),
    MIN(GREATEST(a.joined_at, b.joined_at)),
    MIN(GREATEST(a.joined_at, b.joined_at))
FROM group_members a
JOIN group_members b ON a.group_id = b.group_id AND a.user_id < b.user_id
JOIN users ua ON ua.id = a.user_id
GROUP BY a.user_id, b.user_id, ua.display_name, ua.email
ON CONFLICT (owner_user_id, normalized_email) DO NOTHING;
