namespace Server.Clean;

/// <summary>schema 一次性建库脚本，不迁移旧库。</summary>
public static class PostgresSchema
{
    /// <summary>当前本地库内容版本（表结构 + 存档载荷标签）；与 platform.schema_version 表中的
    /// 最大版本必须一致。约定：SectionCodec.CurrentSchemaVersion 每次升级时本常量同步 +1，
    /// 存档标签 UPDATE 内嵌在 migrations/NNNN_vX_to_vY.sql 对应版本文件中。</summary>
    /// v14：settings 分段载荷新增 tutorialStep 新手引导里程碑（可缺省解码为 0），存档标签推进，表结构未变。
    /// v13：identity.players 增 invite_code 唯一邀请码，新增 social.player_invitations 邀请关系与里程碑领奖标记；存档载荷未变。
    /// v12：loadout 分段触发条件迁移为 abilityTriggers 按功法 ID 映射（可缺省解码），存档标签推进，表结构未变。
    /// v11：重建 account_risk_events.event_kind 约束纳入 'handshake'（修复开发中间态 v10 库的旧约束），存档载荷未变。
    /// v10：账号注册风控信号、判定事件与设备/身份关联索引。
    /// v9：progression 分段载荷新增 traitIds 特质 ID 集合，可缺省解码，无存档结构变更。
    /// v8：settings 分段载荷新增 queuePaused 队列暂停开关，可缺省解码，无存档结构变更。
    /// v7：social.parties 追加队伍档案列（name/activity_kind/node_id/min_valuation/is_public/updated_at）、
    /// 新增 social.party_seekers 散人池、social.chat_messages 增 party_ref 队伍链接外键；存档载荷未变。
    /// v16：actionQueue 分段远征载荷新增 isPinned 遭遇供给标记（可缺省解码），存档标签推进，表结构未变。
    /// v17：world 分段载荷新增 nodeBuildingUsages 拥挤使用快照（可缺省解码），存档标签推进，表结构未变。
    /// v18：战斗面板语义收敛为纯点数通道（actionQueue/duel 载荷内 CombatantStats 移除六个独立乘区字段、
    /// 新增 criticalDamagePoints，旧字段忽略解码、新字段缺省默认值），存档标签推进，表结构未变。
    /// v19：组队讨伐（总纲 §21.13）——social.parties 增 open_slots 开启槽位（2..5，默认 5），
    /// social.party_members 增 ready 就绪标记，新增 social.party_sessions 持久化会话规格；无存档载荷变更。
    /// v20：actionQueue 分段战斗参与载荷落地——行动项新增 participation 绑定，远征状态新增
    /// selfIndex/allies 盟友镜像与 respawnAtSeconds 倒地截止点（全部可缺省解码），表结构未变。
    /// v21：参与绑定改为组织无关字段并携带真实 ActionId；旧 partySession 载荷不兼容，内测库按清零规则处理。
    public const int Version = 21;

    /// <summary>空库初始化用的权威 DDL；幂等（IF NOT EXISTS），仅限全新建库，不承担就地升级。</summary>
    public const string Sql = """
    CREATE SCHEMA IF NOT EXISTS identity;
    CREATE SCHEMA IF NOT EXISTS game;
    CREATE SCHEMA IF NOT EXISTS economy;
    CREATE SCHEMA IF NOT EXISTS market;
    CREATE SCHEMA IF NOT EXISTS social;
    CREATE SCHEMA IF NOT EXISTS platform;

    CREATE TABLE IF NOT EXISTS identity.accounts (
        account_id uuid PRIMARY KEY,
        login_normalized varchar(128) NOT NULL UNIQUE,
        password_hash bytea NOT NULL,
        password_salt bytea NOT NULL,
        password_iterations integer NOT NULL CHECK (password_iterations >= 100000),
        status smallint NOT NULL DEFAULT 1 CHECK (status IN (0, 1, 2)),
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL
    );

    CREATE TABLE IF NOT EXISTS identity.players (
        player_id uuid PRIMARY KEY,
        player_seq bigint GENERATED ALWAYS AS IDENTITY,
        account_id uuid NOT NULL REFERENCES identity.accounts(account_id),
        display_name varchar(64) NOT NULL,
        display_name_normalized varchar(128) NOT NULL UNIQUE,
        -- 默认值为易测种子/迁移回补兜底：正式注册路径仍由服务端显式写入随机码。
        invite_code varchar(16) NOT NULL
            DEFAULT upper(substr(md5(random()::text || clock_timestamp()::text), 1, 10)),
        created_at timestamptz NOT NULL,
        last_seen_at timestamptz NOT NULL,
        status smallint NOT NULL DEFAULT 1 CHECK (status IN (0, 1, 2))
    );
    CREATE INDEX IF NOT EXISTS ix_players_account ON identity.players(account_id);
    -- 专属邀请码全局唯一：注册时生成，撞码按唯一冲突重试新码。
    CREATE UNIQUE INDEX IF NOT EXISTS ux_players_invite_code ON identity.players(invite_code);

    CREATE TABLE IF NOT EXISTS identity.auth_sessions (
        session_id uuid PRIMARY KEY,
        account_id uuid NOT NULL REFERENCES identity.accounts(account_id),
        token_hash bytea NOT NULL UNIQUE,
        issued_at timestamptz NOT NULL,
        expires_at timestamptz NOT NULL,
        last_used_at timestamptz NOT NULL,
        revoked_at timestamptz NULL
    );
    CREATE INDEX IF NOT EXISTS ix_auth_sessions_active ON identity.auth_sessions(token_hash, expires_at) WHERE revoked_at IS NULL;

    CREATE TABLE IF NOT EXISTS identity.audit_events (
        event_id uuid PRIMARY KEY,
        actor_id uuid NULL,
        event_type varchar(96) NOT NULL,
        target_id uuid NULL,
        payload jsonb NOT NULL DEFAULT '{}'::jsonb,
        occurred_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_audit_events_occurred ON identity.audit_events(occurred_at DESC);

    CREATE TABLE IF NOT EXISTS identity.account_risk_signals (
        signal_id uuid PRIMARY KEY,
        account_id uuid NOT NULL REFERENCES identity.accounts(account_id) ON DELETE CASCADE,
        signal_kind varchar(32) NOT NULL,
        signal_hash char(64) NOT NULL,
        first_seen_at timestamptz NOT NULL,
        last_seen_at timestamptz NOT NULL,
        hit_count bigint NOT NULL DEFAULT 1 CHECK (hit_count > 0),
        UNIQUE (account_id, signal_kind, signal_hash)
    );
    CREATE INDEX IF NOT EXISTS ix_account_risk_signal_lookup
        ON identity.account_risk_signals(signal_kind, signal_hash, last_seen_at DESC);
    CREATE INDEX IF NOT EXISTS ix_account_risk_signal_account
        ON identity.account_risk_signals(account_id, last_seen_at DESC);

    CREATE TABLE IF NOT EXISTS identity.account_risk_events (
        event_id uuid PRIMARY KEY,
        account_id uuid NULL REFERENCES identity.accounts(account_id) ON DELETE SET NULL,
        decision varchar(16) NOT NULL CHECK (decision IN ('allow', 'review', 'deny')),
        error_code varchar(64) NULL,
        signals jsonb NOT NULL DEFAULT '[]'::jsonb,
        risk_score integer NOT NULL DEFAULT 0 CHECK (risk_score >= 0 AND risk_score <= 100),
        reasons jsonb NOT NULL DEFAULT '[]'::jsonb,
        event_kind varchar(16) NOT NULL DEFAULT 'register' CHECK (event_kind IN ('register', 'login', 'handshake')),
        occurred_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_account_risk_events_occurred
        ON identity.account_risk_events(occurred_at DESC);
    CREATE INDEX IF NOT EXISTS ix_account_risk_events_account
        ON identity.account_risk_events(account_id, occurred_at DESC);

    CREATE TABLE IF NOT EXISTS platform.account_risk_policy (
        policy_id smallint PRIMARY KEY CHECK (policy_id = 1),
        payload jsonb NOT NULL,
        updated_at timestamptz NOT NULL
    );
    CREATE TABLE IF NOT EXISTS platform.account_risk_key (
        key_id smallint PRIMARY KEY CHECK (key_id = 1),
        key_bytes bytea NOT NULL CHECK (octet_length(key_bytes) = 32)
    );

    CREATE TABLE IF NOT EXISTS identity.data_protection_keys (
        key_id uuid PRIMARY KEY,
        friendly_name varchar(256) NULL,
        xml_payload text NOT NULL,
        created_at timestamptz NOT NULL,
        expires_at timestamptz NULL,
        CHECK (octet_length(xml_payload) <= 1048576),
        CHECK (expires_at IS NULL OR expires_at >= created_at)
    );
    CREATE INDEX IF NOT EXISTS ix_data_protection_keys_created ON identity.data_protection_keys(created_at);
    CREATE INDEX IF NOT EXISTS ix_data_protection_keys_expiry ON identity.data_protection_keys(expires_at);

    CREATE TABLE IF NOT EXISTS game.player_state_heads (
        player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        state_version bigint NOT NULL CHECK (state_version > 0),
        schema_version integer NOT NULL CHECK (schema_version > 0),
        last_simulated_at timestamptz NOT NULL,
        game_data_fingerprint varchar(128) NOT NULL,
        checksum char(64) NOT NULL,
        updated_at timestamptz NOT NULL
    );

    CREATE TABLE IF NOT EXISTS game.player_state_sections (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        section_kind varchar(32) NOT NULL,
        section_version bigint NOT NULL CHECK (section_version > 0),
        schema_version integer NOT NULL CHECK (schema_version > 0),
        codec varchar(32) NOT NULL,
        payload bytea NOT NULL,
        raw_bytes integer NOT NULL CHECK (raw_bytes > 0),
        stored_bytes integer NOT NULL CHECK (stored_bytes > 0),
        checksum char(64) NOT NULL,
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, section_kind)
    );

    CREATE TABLE IF NOT EXISTS game.player_action_queue (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        slot smallint NOT NULL CHECK (slot >= 0 AND slot < 256),
        action_id varchar(128) NOT NULL,
        action_payload bytea NOT NULL,
        action_version bigint NOT NULL CHECK (action_version > 0),
        status varchar(32) NOT NULL,
        next_due_at timestamptz NULL,
        idempotency_key varchar(200) NOT NULL,
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, slot)
    );
    CREATE INDEX IF NOT EXISTS ix_player_actions_due ON game.player_action_queue(next_due_at) WHERE next_due_at IS NOT NULL;

    CREATE TABLE IF NOT EXISTS game.player_runtime_leases (
        player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        owner_instance_id uuid NOT NULL,
        lease_until timestamptz NOT NULL,
        renewed_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_player_runtime_leases_expiry ON game.player_runtime_leases(lease_until);

    CREATE TABLE IF NOT EXISTS game.player_projection (
        player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        realm_rank double precision NOT NULL DEFAULT 0,
        current_node_id varchar(128) NULL,
        presence varchar(32) NOT NULL DEFAULT 'offline',
        last_active_at timestamptz NOT NULL,
        projection_version bigint NOT NULL DEFAULT 1,
        updated_at timestamptz NOT NULL
    );

    CREATE TABLE IF NOT EXISTS game.node_buildings (
        node_id varchar(128) NOT NULL,
        building_id varchar(128) NOT NULL,
        level integer NOT NULL DEFAULT 0 CHECK (level >= 0),
        target_level integer NOT NULL DEFAULT 0 CHECK (target_level >= 0),
        started_at_unix_seconds double precision NOT NULL DEFAULT 0,
        contributed_materials jsonb NOT NULL DEFAULT '{}'::jsonb,
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (node_id, building_id)
    );

    CREATE TABLE IF NOT EXISTS economy.player_asset_balances (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        asset_id varchar(128) NOT NULL,
        variant varchar(128) NOT NULL DEFAULT '',
        quantity double precision NOT NULL CHECK (quantity >= 0 AND quantity = quantity),
        version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, asset_id, variant)
    );

    CREATE TABLE IF NOT EXISTS economy.player_asset_instances (
        instance_id uuid PRIMARY KEY,
        owner_player_id uuid NOT NULL REFERENCES identity.players(player_id),
        definition_id varchar(128) NOT NULL,
        state jsonb NOT NULL DEFAULT '{}'::jsonb,
        version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_asset_instances_owner ON economy.player_asset_instances(owner_player_id);

    CREATE TABLE IF NOT EXISTS economy.transactions (
        transaction_id uuid PRIMARY KEY,
        idempotency_key varchar(200) NOT NULL UNIQUE,
        request_hash char(64) NOT NULL,
        transaction_type varchar(96) NOT NULL,
        actor_id uuid NOT NULL,
        result_payload bytea NOT NULL DEFAULT ''::bytea,
        created_at timestamptz NOT NULL
    );

    CREATE TABLE IF NOT EXISTS economy.transaction_lines (
        transaction_id uuid NOT NULL REFERENCES economy.transactions(transaction_id),
        line_no smallint NOT NULL CHECK (line_no >= 0),
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        asset_id varchar(128) NOT NULL,
        variant varchar(128) NOT NULL DEFAULT '',
        instance_id uuid NULL,
        delta double precision NOT NULL CHECK (delta = delta),
        PRIMARY KEY (transaction_id, line_no)
    );
    CREATE INDEX IF NOT EXISTS ix_transaction_lines_player ON economy.transaction_lines(player_id, asset_id, variant);

    CREATE TABLE IF NOT EXISTS economy.reservations (
        reservation_id uuid PRIMARY KEY,
        order_id uuid NOT NULL UNIQUE,
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        asset_id varchar(128) NOT NULL,
        variant varchar(128) NOT NULL DEFAULT '',
        instance_id uuid NULL REFERENCES economy.player_asset_instances(instance_id),
        side varchar(8) NOT NULL CHECK (side IN ('buy', 'sell')),
        quantity double precision NOT NULL CHECK (quantity >= 0 AND quantity = quantity AND quantity < 'Infinity'::double precision),
        reserved_value double precision NOT NULL CHECK (reserved_value >= 0),
        status varchar(32) NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_reservations_player ON economy.reservations(player_id, status);

    CREATE TABLE IF NOT EXISTS market.partition_leases (
        partition_id integer PRIMARY KEY CHECK (partition_id >= 0),
        owner_instance_id uuid NOT NULL,
        fence_token bigint NOT NULL CHECK (fence_token > 0),
        expires_at timestamptz NOT NULL,
        renewed_at timestamptz NOT NULL
    );

    CREATE TABLE IF NOT EXISTS market.orders (
        order_id uuid PRIMARY KEY,
        partition_id integer NOT NULL CHECK (partition_id >= 0),
        owner_player_id uuid NOT NULL REFERENCES identity.players(player_id),
        side varchar(8) NOT NULL CHECK (side IN ('buy', 'sell')),
        asset_id varchar(128) NOT NULL,
        variant varchar(128) NOT NULL DEFAULT '',
        price double precision NOT NULL CHECK (price > 0 AND price = price AND price < 'Infinity'::double precision),
        quantity double precision NOT NULL CHECK (quantity > 0 AND quantity = quantity AND quantity < 'Infinity'::double precision),
        filled_quantity double precision NOT NULL DEFAULT 0 CHECK (filled_quantity >= 0 AND filled_quantity = filled_quantity AND filled_quantity <= quantity),
        status varchar(24) NOT NULL CHECK (status IN ('active', 'partially_filled', 'filled', 'cancelled', 'expired')),
        version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
        sequence bigint NOT NULL,
        idempotency_key varchar(200) NOT NULL UNIQUE,
        created_at timestamptz NOT NULL,
        expires_at timestamptz NOT NULL CHECK (expires_at > created_at),
        updated_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_market_orders_book ON market.orders(partition_id, asset_id, variant, status, side, price, sequence);
    CREATE INDEX IF NOT EXISTS ix_market_orders_expiration ON market.orders(partition_id, expires_at) WHERE status IN ('active', 'partially_filled');
    CREATE INDEX IF NOT EXISTS ix_market_orders_owner ON market.orders(owner_player_id, status);
    CREATE UNIQUE INDEX IF NOT EXISTS ux_market_orders_partition_sequence ON market.orders(partition_id, sequence);

    CREATE TABLE IF NOT EXISTS market.trades (
        trade_id uuid PRIMARY KEY,
        partition_id integer NOT NULL,
        buy_order_id uuid NOT NULL REFERENCES market.orders(order_id),
        sell_order_id uuid NOT NULL REFERENCES market.orders(order_id),
        buyer_id uuid NOT NULL REFERENCES identity.players(player_id),
        seller_id uuid NOT NULL REFERENCES identity.players(player_id),
        asset_id varchar(128) NOT NULL,
        variant varchar(128) NOT NULL DEFAULT '',
        price double precision NOT NULL CHECK (price > 0 AND price = price AND price < 'Infinity'::double precision),
        quantity double precision NOT NULL CHECK (quantity > 0 AND quantity = quantity AND quantity < 'Infinity'::double precision),
        seller_tax double precision NOT NULL CHECK (seller_tax >= 0 AND seller_tax = seller_tax AND seller_tax < 'Infinity'::double precision),
        traded_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_market_trades_time ON market.trades(traded_at DESC);
    CREATE INDEX IF NOT EXISTS ix_market_trades_partition ON market.trades(partition_id, traded_at DESC);

    CREATE TABLE IF NOT EXISTS market.settlement_claims (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        request_id uuid NOT NULL,
        settled_trade_count integer NOT NULL CHECK (settled_trade_count >= 0),
        claimed_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, request_id)
    );
    CREATE INDEX IF NOT EXISTS ix_settlement_claims_player_time ON market.settlement_claims(player_id, claimed_at DESC);

    CREATE TABLE IF NOT EXISTS platform.section_flush_queue (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        section_kind varchar(32) NOT NULL,
        state_version bigint NOT NULL CHECK (state_version > 0),
        schema_version integer NOT NULL CHECK (schema_version > 0),
        codec varchar(32) NOT NULL,
        payload bytea NOT NULL,
        raw_bytes integer NOT NULL CHECK (raw_bytes > 0),
        checksum char(64) NOT NULL,
        game_data_fingerprint varchar(128) NOT NULL DEFAULT '',
        last_simulated_at timestamptz NULL,
        attempts integer NOT NULL DEFAULT 0,
        next_attempt_at timestamptz NOT NULL,
        lease_until timestamptz NULL,
        lease_token uuid NULL,
        last_error varchar(512) NULL,
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, section_kind)
    );
    CREATE INDEX IF NOT EXISTS ix_section_flush_ready ON platform.section_flush_queue(next_attempt_at, lease_until);

    CREATE TABLE IF NOT EXISTS platform.integration_outbox (
        outbox_id uuid PRIMARY KEY,
        aggregate_type varchar(64) NOT NULL,
        aggregate_id uuid NOT NULL,
        event_type varchar(96) NOT NULL,
        payload bytea NOT NULL,
        occurred_at timestamptz NOT NULL,
        attempts integer NOT NULL DEFAULT 0,
        visible_at timestamptz NOT NULL,
        lease_until timestamptz NULL,
        lease_token uuid NULL,
        delivered_at timestamptz NULL,
        last_error varchar(512) NULL
    );
    CREATE INDEX IF NOT EXISTS ix_outbox_ready ON platform.integration_outbox(visible_at, lease_until) WHERE delivered_at IS NULL;

    CREATE TABLE IF NOT EXISTS platform.command_receipts (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        request_id uuid NOT NULL,
        command varchar(96) NOT NULL,
        payload_hash char(64) NOT NULL CHECK (length(payload_hash) = 64),
        result_payload bytea NOT NULL,
        state_version bigint NOT NULL CHECK (state_version >= 0),
        created_at timestamptz NOT NULL,
        expires_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, request_id)
    );
    CREATE INDEX IF NOT EXISTS ix_command_receipts_expiry ON platform.command_receipts(expires_at);

    CREATE TABLE IF NOT EXISTS platform.compute_jobs (
        job_id uuid PRIMARY KEY,
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        target_time timestamptz NOT NULL,
        status varchar(24) NOT NULL,
        input_payload bytea NOT NULL,
        output_payload bytea NULL,
        lease_until timestamptz NULL,
        lease_token uuid NULL,
        next_attempt_at timestamptz NULL,
        last_error varchar(512) NULL,
        attempts integer NOT NULL DEFAULT 0,
        updated_at timestamptz NOT NULL,
        created_at timestamptz NOT NULL
    );
    -- 领取谓词（pending 到期 + leased 孤儿回收）拆为两个部分索引，与 ClaimDueAsync 分支一一对应；
    -- 旧的 (status, target_time) 组合索引不再使用（内测库直建/重建，无迁移包袱）。
    DROP INDEX IF EXISTS platform.ix_compute_jobs_ready;
    CREATE INDEX IF NOT EXISTS ix_compute_jobs_pending_due
        ON platform.compute_jobs(target_time) WHERE status = 'pending';
    CREATE INDEX IF NOT EXISTS ix_compute_jobs_lease_orphan
        ON platform.compute_jobs(lease_until) WHERE status = 'leased';
    CREATE INDEX IF NOT EXISTS ix_compute_jobs_retention ON platform.compute_jobs(status, updated_at);
    -- 同一玩家最多一个未完结冷挂机任务：pending/leased 期间重复入队走 upsert/忽略，
    -- 防止同一玩家被并发派发到多个计算批次。
    CREATE UNIQUE INDEX IF NOT EXISTS ux_compute_jobs_open
        ON platform.compute_jobs(player_id) WHERE status IN ('pending', 'leased');

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
        -- 队伍档案：招募标签口径（目标类型/目标节点/身价门槛/公开性），不参与行动或战斗结算。
        name varchar(32) NOT NULL DEFAULT '',
        activity_kind varchar(16) NOT NULL DEFAULT 'any'
            CHECK (activity_kind IN ('any', 'combat', 'exploration', 'cultivation', 'alchemy', 'forging', 'talisman', 'formation', 'enhancement')),
        node_id varchar(128) NOT NULL DEFAULT '',
        min_valuation double precision NOT NULL DEFAULT 0 CHECK (min_valuation >= 0),
        is_public boolean NOT NULL DEFAULT false,
        -- 开启槽位（总纲 §21.13）：2~5，默认 5；收窄后不得低于当前队员数（由社交层裁决保证）。
        open_slots smallint NOT NULL DEFAULT 5 CHECK (open_slots BETWEEN 2 AND 5),
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL DEFAULT now()
    );
    -- 大厅只读视图按公开性与目标过滤。
    CREATE INDEX IF NOT EXISTS ix_parties_lobby ON social.parties(is_public, activity_kind, node_id)
        WHERE is_public;

    -- 一名玩家最多属于一个队伍：player_id 直接作主键，天然互斥。
    CREATE TABLE IF NOT EXISTS social.party_members (
        player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        party_id uuid NOT NULL REFERENCES social.parties(party_id) ON DELETE CASCADE,
        joined_at timestamptz NOT NULL,
        -- 组队讨伐就绪标记（总纲 §21.13）：成员自开关，离队/被踢即随成员行删除。
        ready boolean NOT NULL DEFAULT false
    );
    CREATE INDEX IF NOT EXISTS ix_party_members_party ON social.party_members(party_id);

    -- 组队讨伐会话（总纲 §21.13）：spec 为冻结的共享剧本规格（成员快照/遭遇/种子），
    -- session_id 为社交层生成的文本标识（party:{partyId}:{unixSec}:{rand}），非 uuid。
    -- 同一队伍同时至多一个活跃会话：由 ended_at IS NULL 部分唯一索引收敛并发仲裁。
    CREATE TABLE IF NOT EXISTS social.party_sessions (
        session_id varchar(64) PRIMARY KEY,
        party_id uuid NOT NULL REFERENCES social.parties(party_id) ON DELETE CASCADE,
        spec jsonb NOT NULL,
        started_at timestamptz NOT NULL,
        ended_at timestamptz NULL,
        end_reason varchar(64) NULL
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_party_sessions_active
        ON social.party_sessions(party_id) WHERE ended_at IS NULL;

    -- 待处理的队伍邀请：每名玩家最多一条（被邀请人作主键），接受/拒绝/过期后清除。
    CREATE TABLE IF NOT EXISTS social.party_invitations (
        invitee_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        party_id uuid NOT NULL REFERENCES social.parties(party_id) ON DELETE CASCADE,
        inviter_id uuid NOT NULL REFERENCES identity.players(player_id),
        created_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_party_invitations_party ON social.party_invitations(party_id);

    -- 「匹配中」散人登记：player_id 主键保证一人一条；入队或撤下即删。
    CREATE TABLE IF NOT EXISTS social.party_seekers (
        player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        activity_kind varchar(16) NOT NULL DEFAULT 'any'
            CHECK (activity_kind IN ('any', 'combat', 'exploration', 'cultivation', 'alchemy', 'forging', 'talisman', 'formation', 'enhancement')),
        node_id varchar(128) NOT NULL DEFAULT '',
        min_valuation double precision NOT NULL DEFAULT 0 CHECK (min_valuation >= 0),
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_party_seekers_goal ON social.party_seekers(activity_kind, node_id);

    -- 注册邀请关系：invitee_id 主键（一人终身至多一条），里程碑奖励按行级领取标记幂等发放。
    CREATE TABLE IF NOT EXISTS social.player_invitations (
        invitee_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        inviter_id uuid NOT NULL REFERENCES identity.players(player_id),
        invitation_code varchar(16) NOT NULL,
        highest_realm_level integer NOT NULL DEFAULT 1,
        qi_rewarded boolean NOT NULL DEFAULT false,
        foundation_rewarded boolean NOT NULL DEFAULT false,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        CHECK (inviter_id <> invitee_id)
    );
    CREATE INDEX IF NOT EXISTS ix_player_invitations_inviter ON social.player_invitations(inviter_id);

    -- 传音消息：每个 (channel, scope_key) 作用域只保留最近 100 条，写入时同事务裁剪。
    -- party_ref 为队伍卡片外键：队伍解散后置空，消息正文仍保留为普通文本。
    CREATE TABLE IF NOT EXISTS social.chat_messages (
        message_id uuid PRIMARY KEY,
        channel varchar(16) NOT NULL CHECK (channel IN ('world', 'nearby', 'friend', 'party')),
        scope_key varchar(160) NOT NULL,
        sender_id uuid NOT NULL REFERENCES identity.players(player_id),
        sender_name varchar(64) NOT NULL,
        body varchar(200) NOT NULL CHECK (char_length(body) > 0),
        party_ref uuid REFERENCES social.parties(party_id) ON DELETE SET NULL,
        sent_at timestamptz NOT NULL
    );
    CREATE INDEX IF NOT EXISTS ix_chat_messages_scope
        ON social.chat_messages(channel, scope_key, sent_at DESC, message_id DESC);

    CREATE TABLE IF NOT EXISTS platform.schema_version (
        version integer PRIMARY KEY,
        applied_at timestamptz NOT NULL
    );
    INSERT INTO platform.schema_version(version, applied_at)
    VALUES (19, now()), (20, now()), (21, now()) ON CONFLICT (version) DO NOTHING;
    """;
}

/// <summary>
/// 在宿主构建认证与请求管线之前完成当前版本 schema 的建立/校验。
/// Data Protection 的 key ring 依赖同一张 PostgreSQL 表，因此不能把首次建库
/// 延后到 HostedService 启动阶段，否则首个请求可能先触发 42P01。
/// </summary>
public static class PostgresSchemaBootstrap
{
    /// <summary>启动时执行建库（可选）并最终校验版本；版本不符立即抛错终止启动。</summary>
    /// <param name="dataSource">目标数据源。</param>
    /// <param name="enableBootstrap">为 true 时先执行权威 DDL 再校验；false 时仅校验既有库版本。</param>
    /// <param name="cancellationToken">取消令牌，启动阶段通常带超时。</param>
    public static async Task EnsureAsync(
        NpgsqlDataSource dataSource,
        bool enableBootstrap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (enableBootstrap)
        {
            // 已有库必须先通过版本门禁，避免 IF NOT EXISTS 把旧表误标为当前结构。
            await using (var existing = new NpgsqlCommand("SELECT to_regclass('platform.schema_version') IS NOT NULL", connection))
            {
                if (await existing.ExecuteScalarAsync(cancellationToken) is true)
                    await VerifyAsync(connection, cancellationToken);
            }
            await using var command = new NpgsqlCommand(PostgresSchema.Sql, connection)
            {
                CommandTimeout = 60
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await VerifyAsync(connection, cancellationToken);
    }

    /// <summary>校验 platform.schema_version 最新版本等于 PostgresSchema.Version，不符即 fail-fast。</summary>
    public static async Task VerifyAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand("""
            SELECT version
            FROM platform.schema_version
            ORDER BY version DESC
            LIMIT 1
            """, connection)
        {
            CommandTimeout = 15
        };
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not int version || version != PostgresSchema.Version)
            throw new InvalidOperationException($"PostgreSQL schema 版本不匹配，要求 v{PostgresSchema.Version}。请重建数据库、执行 migrations/ 目录中的升级脚本（本地库可运行 scripts/migrate-local-db.sh 或由 dev.sh 启动链自动执行）。");
    }
}

/// <summary>启动期 schema 版本复检：在 HostedService 阶段二次确认库版本可用，异常时阻止服务进入就绪。</summary>
public sealed class PostgresSchemaHostedService(
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<PostgresSchemaHostedService> logger) : IHostedService
{
    /// <summary>连接数据库并校验 schema 版本；版本不符抛出异常使启动失败。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await PostgresSchemaBootstrap.VerifyAsync(connection, cancellationToken);
        if (!options.EnableSchemaBootstrap)
            logger.LogInformation("PostgreSQL schema bootstrap 已关闭，已校验 schema v{Version}。", PostgresSchema.Version);
        else
            logger.LogInformation("PostgreSQL schema v{Version} 已就绪。", PostgresSchema.Version);
    }

    /// <summary>无资源需要停止时清理。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
