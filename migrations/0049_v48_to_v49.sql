-- v48 -> v49：新增宗门、成员、申请邀请与离宗时间，不改变玩家分段载荷。
BEGIN;
    -- 宗门名称全局唯一，成员主键保证一人一宗；解散行保留离线退出锚点。
    CREATE TABLE IF NOT EXISTS social.zongmen (
        zongmen_id uuid PRIMARY KEY,
        name varchar(64) NOT NULL,
        name_normalized varchar(128) NOT NULL UNIQUE,
        prefix varchar(4) NOT NULL,
        leader_id uuid NOT NULL REFERENCES identity.players(player_id),
        anchor_node_id varchar(128) NOT NULL,
        created_at timestamptz NOT NULL,
        disbanded_at timestamptz
    );
    CREATE TABLE IF NOT EXISTS social.zongmen_members (
        player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        zongmen_id uuid NOT NULL REFERENCES social.zongmen(zongmen_id),
        role integer NOT NULL CHECK (role BETWEEN 0 AND 6),
        joined_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_zongmen_members_group ON social.zongmen_members(zongmen_id);
    CREATE TABLE IF NOT EXISTS social.zongmen_requests (
        zongmen_id uuid NOT NULL REFERENCES social.zongmen(zongmen_id),
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        kind varchar(16) NOT NULL CHECK (kind IN ('application','invitation')),
        created_at timestamptz NOT NULL,
        PRIMARY KEY(zongmen_id,player_id)
    );
    CREATE INDEX IF NOT EXISTS ix_zongmen_requests_player ON social.zongmen_requests(player_id);
    CREATE TABLE IF NOT EXISTS social.zongmen_departures (
        zongmen_id uuid NOT NULL REFERENCES social.zongmen(zongmen_id),
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        left_at timestamptz NOT NULL,
        anchor_node_id varchar(128) NOT NULL,
        PRIMARY KEY(zongmen_id,player_id,left_at)
    );

ALTER TABLE social.chat_messages DROP CONSTRAINT chat_messages_channel_check;
ALTER TABLE social.chat_messages ADD CONSTRAINT chat_messages_channel_check CHECK (channel IN ('world','nearby','friend','party','sect'));
INSERT INTO platform.schema_version(version, applied_at) VALUES (49, now()) ON CONFLICT (version) DO NOTHING;
COMMIT;
