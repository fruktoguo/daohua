-- 0003_v3_to_v4.sql
-- v3 → v4 兼容迁移：新增 social schema 与好友/队伍/队伍邀请/传音消息四类表，
-- 承载传音频道（世界/附近/好友/队伍）与好友、组队社交关系。
-- 适用对象：platform.schema_version 最新行为 3 的既有库；执行后启动门禁（要求 v4）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v4。

BEGIN;

CREATE SCHEMA IF NOT EXISTS social;

CREATE TABLE IF NOT EXISTS social.friend_requests (
    request_id uuid PRIMARY KEY,
    requester_id uuid NOT NULL REFERENCES identity.players(player_id),
    target_id uuid NOT NULL REFERENCES identity.players(player_id),
    status varchar(16) NOT NULL CHECK (status IN ('pending', 'accepted', 'rejected', 'cancelled')),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CHECK (requester_id <> target_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_friend_requests_pending
    ON social.friend_requests(requester_id, target_id) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS ix_friend_requests_target ON social.friend_requests(target_id, status);

CREATE TABLE IF NOT EXISTS social.friendships (
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    friend_id uuid NOT NULL REFERENCES identity.players(player_id),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (player_id, friend_id),
    CHECK (player_id <> friend_id)
);

CREATE TABLE IF NOT EXISTS social.parties (
    party_id uuid PRIMARY KEY,
    leader_id uuid NOT NULL REFERENCES identity.players(player_id),
    created_at timestamptz NOT NULL
);

-- 一名玩家最多属于一个队伍：player_id 直接作主键，天然互斥。
CREATE TABLE IF NOT EXISTS social.party_members (
    player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
    party_id uuid NOT NULL REFERENCES social.parties(party_id) ON DELETE CASCADE,
    joined_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_party_members_party ON social.party_members(party_id);

-- 待处理的队伍邀请：每名玩家最多一条（被邀请人作主键），接受/拒绝/过期后清除。
CREATE TABLE IF NOT EXISTS social.party_invitations (
    invitee_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
    party_id uuid NOT NULL REFERENCES social.parties(party_id) ON DELETE CASCADE,
    inviter_id uuid NOT NULL REFERENCES identity.players(player_id),
    created_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_party_invitations_party ON social.party_invitations(party_id);

-- 传音消息：每个 (channel, scope_key) 作用域只保留最近 100 条，写入时同事务裁剪。
CREATE TABLE IF NOT EXISTS social.chat_messages (
    message_id uuid PRIMARY KEY,
    channel varchar(16) NOT NULL CHECK (channel IN ('world', 'nearby', 'friend', 'party')),
    scope_key varchar(160) NOT NULL,
    sender_id uuid NOT NULL REFERENCES identity.players(player_id),
    sender_name varchar(64) NOT NULL,
    body varchar(200) NOT NULL CHECK (char_length(body) > 0),
    sent_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_chat_messages_scope
    ON social.chat_messages(channel, scope_key, sent_at DESC, message_id DESC);

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (4, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
