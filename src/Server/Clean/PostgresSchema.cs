namespace Server.Clean;

/// <summary>schema 一次性建库脚本，不迁移旧库。</summary>
public static class PostgresSchema
{
    /// <summary>当前本地库内容版本（表结构 + 存档载荷标签 + 破档配表同步锚点）；与 platform.schema_version 表中的
    /// 最大版本必须一致。约定：凡改动数据库结构/权限、SectionCodec 存档载荷或影响旧存档加载的配表删改时本常量 +1
    /// （若玩家分段载荷变动则 SectionCodec.CurrentSchemaVersion 同步 +1），在 docs/backend/db-version-changes.md
    /// 登记 vX -&gt; vY 变更说明，并在任务完成时增量编写 migrations/NNNN_vX_to_vY.sql（prod-release 时再检查是否有遗漏）。</summary>
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
    /// v22：duel 分段行动账本载荷新增 combatants 战斗单位统计（可缺省解码），存档标签推进，表结构未变。
    /// v23：actionQueue 分段装配展示载荷落地——参与成员快照增 equipment、斗法回放增 opponentSkills/
    /// opponentEquipment（可缺省解码），存档标签推进，表结构未变。
    /// v24：组队讨伐会话持有唯一权威运行态，新增成员结算收件箱；会话历史不随队伍解散删除。
    /// v25：economy.transactions 增 (created_at, transaction_type) 时间窗索引——流水已至千万级，
    /// 运维与监控按时间窗统计依赖全表扫描超时；存档载荷未变。
    /// v26：market.trades 增 (buyer_id, traded_at)/(seller_id, traded_at) 复合索引——结算领取的
    /// 谓词读原本落在时间尾部索引上，与一切并发成交插入产生 SSI 冲突；存档载荷未变。
    /// v27：section_flush_queue 改为追加式收件箱（queue_id 自增主键、去除 (player_id,section_kind)
    /// 唯一约束）——upsert 共享行被资产事务与 flush worker 的并发 upsert/lease/delete 反复争夺，
    /// 在 Serializable 下持续产生 40001 与同元组死锁；纯 INSERT 入队 + 按 queue_id 租约认领
    /// 使写入方与清理方不再接触同一元组，同 kind 旧版本行由应用侧版本守卫收敛。
    /// v28：新增 market.settlement_cursors 每玩家结算游标——领取原按 trades 时间窗谓词计数，
    /// 与一切同玩家成交插入 SSI 冲突（~2-3/min 持续 40001）；游标行由成交事务 upsert 累加、
    /// 领取事务 FOR UPDATE 清零，行锁互斥替代谓词读，领取可降级 ReadCommitted 且无 SSI 表面。
    /// v29：social.parties 增 pvp_enabled 队伍斗法开关（总纲 §7.10.4）——仅讨伐目标可为 true、
    /// 仅队长可改、随会话创建冻结进参与规格；存档载荷未变。
    /// v30：combat 分段新增战内 Buff 跨波携带态、参与成员快照 CombatBuffs 冻结字段
    /// 与持续消耗品 Buff 全队标记（阵法）——可缺省解码，存档标签推进，表结构未变。
    /// v31：settings 分段新增 offlineSinceAt 离线锚点，player_projection 增 offline_since 投影列——
    /// 离线生命周期判定（逐出/会话离线截止）改读该列，在线时恒为 NULL。
    /// v32：world 分段建筑效果新增 monsterWeakenRatio（封魔阵怪物削弱，可缺省解码）——
    /// 存档标签推进，表结构未变。
    /// v33：新增 market.item_stats 每品种行情统计（卖一/买一价量与当日/当周成交量）——
    /// 由挂单/撤单/到期/成交事务同事务维护，供悬浮行情卡小表直读；存档载荷未变。
    /// v34：settings 分段新增 unlockedAvatars 已解锁形象集合（可缺省解码为 ["player", "player-male"]）——
    /// 存档标签推进，表结构未变。
    /// v35：duel 分段行动账本载荷新增 duelItemsGained 与 duelItemsLost 斗法缴获与损失统计（可缺省解码为 null/[]，表结构未变）——
    /// 存档标签推进，表结构未变。
    /// v36：progression 分段载荷新增 unlockedTraits 特质等级经验养成集合（可缺省解码为 null/[]，表结构未变）——
    /// 存档标签推进，表结构未变。
    /// v37：组队会话由全量托管切为在线钱包/离线储备；旧会话一次性退款并安全散场，玩家分段仍为 v36。
    /// v38：新增 economy.shop_daily_purchases 系统商店每日限购认领行（与经济事务同事务累加）；存档载荷未变。
    /// v39：market.item_stats 增 reference_price 参考价格字段（最近10次成交单价单值滑动平均EMA，单笔购买多次算一次；无成交价回落配表回收价）——存档载荷未变。
    /// v40：loadout 分段载荷新增 consumableTriggers 消耗品触发条件映射（可缺省解码为 null/[]，表结构未变）——
    /// 存档标签推进至 v37，玩家八分段无表结构变化。
    /// v41：progression 分段载荷新增 heavenGate 五行灵根状态（可缺省解码为未开天门），存档标签推进至 v38；表结构未变。
    /// v42：放宽 party_session_inbox.consumed_transaction_id 约束（允许 NULL，解除结算空流水绑定；外键加 ON DELETE SET NULL），ix_section_flush_ready 优化为单列 (next_attempt_at) 恢复 HOT 更新——存档载荷与标签未变。
    /// v43：transaction_lines 外键补齐 ON DELETE CASCADE，支持级联回收；留存清理彻底扫荡 2 小时前战斗掉落/消耗等易失性流水——存档载荷与标签未变。
    /// v44：settings 分段载荷新增组队复活时机策略字段（respawnPolicy/respawnTargetPlayerId，可缺省解码为直接复活）；
    /// 存档标签推进至 v39，表结构未变。
    /// v46：social.parties 增 difficulty 队伍讨伐难度列（0~5，默认 0）；无存档载荷变更。
    /// v47：identity.accounts 增 taptap_openid 列与唯一索引（支持 TapTap 授权登录与一人一号强防御）；无存档载荷变更。
    /// v48：战斗特质载荷标签 v41，新增字段均可缺省解码，无 DDL 变更。
    /// v49：新增宗门、成员、申请邀请与离宗时刻表，启用宗门传音；个人分段载荷不变。
    /// v50：social.chat_messages 增 sender_realm 发送者境界列；social.parties 默认 open_slots 设为 3，个人分段载荷不变。
    /// v51：队伍斗地主（总纲 §21.15）——social.parties 增 doudizhu_stake、party_members 增独立准备标记，
    /// 新增 social.doudizhu_games 权威牌局检查点；个人分段载荷不变。
    /// v53：新增瞬时经济幂等防重表 economy.transient_idempotency；玩家载荷标签保持 v42。
    /// v54：全服排行榜整点快照（game.leaderboard_snapshots / game.leaderboard_ranks）与
    /// game.player_projection 扩列（experience/lifetime_experience/duel_wins/duel_losses/avatar）；
    /// progression 增 duelWins/duelLosses、settings 增 publicLoadoutVisible（均可缺省解码），玩家载荷标签推进至 v43。
    /// v55：identity.accounts 增 is_bot boolean NOT NULL DEFAULT false（标记市场机器人账号，屏蔽附近玩家列表与全服排行榜）；玩家载荷标签保持 v43 不变。
    /// v56：双轨邮件系统（social.broadcast_mails / social.player_mail_states / social.player_mails）；玩家载荷标签保持 v43 不变。
    /// v57：持久化白名单治理与高频落盘根治（新增 game.duel_logs 斗法完整收益日志表、ix_shop_daily_purchases_updated 留存清理索引、状态/队列/临时幂等表 fillfactor 与 autovacuum 调优）；玩家载荷标签保持 v43 不变。
    /// v58：每日签到系统（新增 game.player_daily_checkins 旁路签到状态表）；玩家载荷标签保持 v43 不变。
    /// v59：队列死信收口与热表索引/填满因子加固（section_flush_queue/integration_outbox 增 status 死信态与部分索引、
    /// compute_jobs 增 dead_count 防无限死复循环、补齐 auth_sessions/market.trades/market.orders/settlement_claims/
    /// reservations/party_session_inbox 清理与反查索引及热表 fillfactor/autovacuum 调优）；玩家载荷标签保持 v43 不变。
    /// v60：传音表情包多端同步系统（新增 social.player_stickers 旁路表情包表及软删除墓碑索引，上限 100 个）；玩家载荷标签保持 v43 不变。
    /// v61：战斗单位快照保存天雷/血煞阵盘触发时刻，载荷标签 v44；建立刷盘身份行锁函数及最小 worker 审计权限。
    /// v62：兑换码系统（新增 economy.redeem_batches / economy.redeem_codes / economy.player_redeem_claims 旁路表）；玩家载荷标签保持 v44 不变。
    /// v63：传音消息增加引用快照与点赞版本，新增随消息删除的点赞状态表；玩家载荷标签保持 v44。
    /// v64：筑基期根基系统（progression 分段新增 genjiPoints/genjiUnlocked，可缺省解码），推进玩家载荷标签至 v45；无 DDL 变更。
    /// v65：补齐两处高频扫描部分索引：platform.integration_outbox(delivered_at) WHERE delivered_at IS NOT NULL（留存清理防全表扫描）与
    /// identity.auth_sessions(account_id) WHERE revoked_at IS NULL（改密批量吊销按账号索引）；玩家载荷标签保持 v45 不变。
    /// v66：道友攻略系统与配装付费查阅（新增 social.guide_posts / guide_daily_quotas / guide_post_views / guide_post_likes /
    /// guide_post_unlocks / guide_post_tips / loadout_inspect_grants 表及权限；settings 分段新增 loadoutInspectPrice 可缺省解码，存档标签推进至 v46）。
    /// v67：秘境副本系统全栈上线（social.parties 与 party_seekers 纳入 activity_kind='dungeon'、social.parties 难度放宽至 0..10 并增 dungeon_id/dungeon_repeat_count、
    /// social.party_members 增 assist_mode 助战标记；progression 分段新增 tiliValue/tiliUpdatedAtUnixSeconds、actionQueue 远征新增秘境字段，均可缺省解码，存档标签推进至 v47）。
    /// v68：战斗统计新增紧凑 PVP 斗法记录（duel 分段行动账本新增 duelRecords、actionQueue 斗法回放新增 viewerRecord，均可缺省解码；存档标签推进至 v48，无 DDL 变更）。
    /// v69：符箓与阵法技艺每级提升 1% 对应伤害（CombatantStats 新增 talismanDamageBonusPoints/formationDamageBonusPoints，均可缺省解码为 0；存档标签推进至 v49，无 DDL 变更）。
    /// </summary>
    public const int Version = 69;

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
        updated_at timestamptz NOT NULL,
        taptap_openid varchar(128) NULL,
        is_bot boolean NOT NULL DEFAULT false
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_taptap_openid ON identity.accounts(taptap_openid) WHERE taptap_openid IS NOT NULL;

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
    CREATE INDEX IF NOT EXISTS ix_auth_sessions_account_active ON identity.auth_sessions(account_id) WHERE revoked_at IS NULL;
    CREATE INDEX IF NOT EXISTS ix_auth_sessions_expires ON identity.auth_sessions(expires_at);
    CREATE INDEX IF NOT EXISTS ix_auth_sessions_revoked ON identity.auth_sessions(revoked_at) WHERE revoked_at IS NOT NULL;

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
        experience double precision NOT NULL DEFAULT 0,
        lifetime_experience double precision NOT NULL DEFAULT 0,
        duel_wins double precision NOT NULL DEFAULT 0,
        duel_losses double precision NOT NULL DEFAULT 0,
        avatar varchar(64) NOT NULL DEFAULT 'player',
        current_node_id varchar(128) NULL,
        presence varchar(32) NOT NULL DEFAULT 'offline',
        offline_since timestamptz NULL,
        last_active_at timestamptz NOT NULL,
        projection_version bigint NOT NULL DEFAULT 1,
        updated_at timestamptz NOT NULL
    );

    -- 全服排行榜整点快照（派生读模型，整榜可重建）。
    CREATE TABLE IF NOT EXISTS game.leaderboard_snapshots (
        board_id varchar(64) PRIMARY KEY,
        computed_at timestamptz NOT NULL,
        participant_count integer NOT NULL CHECK (participant_count >= 0),
        payload jsonb NOT NULL
    );

    -- 全服排行榜个人名次投影（仅收录 score > 0 参与者）。
    CREATE TABLE IF NOT EXISTS game.leaderboard_ranks (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        board_id varchar(64) NOT NULL,
        rank integer NOT NULL CHECK (rank > 0),
        score double precision NOT NULL CHECK (score > 0),
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, board_id)
    );
    CREATE INDEX IF NOT EXISTS ix_leaderboard_ranks_board ON game.leaderboard_ranks(board_id, rank);

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

    -- PVP 斗法完整收益日志：仅记录双人斗法参战双方、节点、胜负/认输标识与双方本场得失物品，30 天滚动留存。
    CREATE TABLE IF NOT EXISTS game.duel_logs (
        duel_log_id uuid PRIMARY KEY,
        duel_id varchar(96) NOT NULL,
        initiator_player_id uuid NOT NULL REFERENCES identity.players(player_id) ON DELETE CASCADE,
        initiator_name varchar(64) NOT NULL,
        initiator_power double precision NOT NULL DEFAULT 0,
        target_player_id uuid NOT NULL REFERENCES identity.players(player_id) ON DELETE CASCADE,
        target_name varchar(64) NOT NULL,
        target_power double precision NOT NULL DEFAULT 0,
        node_id varchar(128) NOT NULL,
        winner_player_id uuid NULL,
        outcome varchar(24) NOT NULL CHECK (outcome IN ('initiator_win', 'target_win', 'draw')),
        forfeited boolean NOT NULL DEFAULT false,
        initiator_gains jsonb NOT NULL DEFAULT '[]'::jsonb,
        initiator_losses jsonb NOT NULL DEFAULT '[]'::jsonb,
        target_gains jsonb NOT NULL DEFAULT '[]'::jsonb,
        target_losses jsonb NOT NULL DEFAULT '[]'::jsonb,
        duration_seconds double precision NOT NULL DEFAULT 0,
        occurred_at timestamptz NOT NULL
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_duel_logs_duel_id ON game.duel_logs(duel_id);
    CREATE INDEX IF NOT EXISTS ix_duel_logs_occurred ON game.duel_logs(occurred_at DESC);
    CREATE INDEX IF NOT EXISTS ix_duel_logs_initiator ON game.duel_logs(initiator_player_id, occurred_at DESC);
    CREATE INDEX IF NOT EXISTS ix_duel_logs_target ON game.duel_logs(target_player_id, occurred_at DESC);

    -- 每日签到旁路状态表：每名玩家至多 1 行（零 ECS 与八分段侵入），按 UTC+8 自然日结算连签、周月统计与奖励。
    CREATE TABLE IF NOT EXISTS game.player_daily_checkins (
        player_id uuid PRIMARY KEY REFERENCES identity.players(player_id) ON DELETE CASCADE,
        last_claim_day_index bigint NOT NULL,
        last_claim_date char(10) NOT NULL,
        streak_days integer NOT NULL CHECK (streak_days >= 1),
        total_days integer NOT NULL CHECK (total_days >= 1),
        last_reward_merit integer NOT NULL CHECK (last_reward_merit BETWEEN 10 AND 999),
        week_start_day_index bigint NOT NULL DEFAULT 0,
        week_days integer NOT NULL DEFAULT 0 CHECK (week_days >= 0),
        week_merit integer NOT NULL DEFAULT 0 CHECK (week_merit >= 0),
        week_days_mask smallint NOT NULL DEFAULT 0,
        month_key integer NOT NULL DEFAULT 0,
        month_days integer NOT NULL DEFAULT 0 CHECK (month_days >= 0),
        month_merit integer NOT NULL DEFAULT 0 CHECK (month_merit >= 0),
        last_claimed_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL
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
    CREATE INDEX IF NOT EXISTS ix_transactions_created_at ON economy.transactions(created_at, transaction_type);

    CREATE TABLE IF NOT EXISTS economy.transaction_lines (
        transaction_id uuid NOT NULL REFERENCES economy.transactions(transaction_id) ON DELETE CASCADE,
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
    CREATE INDEX IF NOT EXISTS ix_reservations_terminal_updated ON economy.reservations(updated_at) WHERE status <> 'active';

    -- 系统商店每日限购认领行：与经济事务同事务累加，守卫拒绝即视为超限并整体回滚。
    CREATE TABLE IF NOT EXISTS economy.shop_daily_purchases (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        offer_id varchar(64) NOT NULL,
        day_index bigint NOT NULL,
        quantity integer NOT NULL CHECK (quantity >= 0),
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, offer_id, day_index)
    );
    CREATE INDEX IF NOT EXISTS ix_shop_daily_purchases_updated
        ON economy.shop_daily_purchases(updated_at);

    -- 瞬时经济事务短期幂等防重表：仅承载 PersistLedger=false 的幂等键去重，由留存任务按 2 小时滚动清理。
    CREATE TABLE IF NOT EXISTS economy.transient_idempotency (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        idempotency_key varchar(200) NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        PRIMARY KEY (player_id, idempotency_key)
    );
    CREATE INDEX IF NOT EXISTS ix_transient_idempotency_created_at
        ON economy.transient_idempotency(created_at);

    -- 兑换码批次主表（支持 universal 通用口令码与 single_use 批次单次码）。
    CREATE TABLE IF NOT EXISTS economy.redeem_batches (
        batch_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        title varchar(120) NOT NULL,
        mode varchar(16) NOT NULL CHECK (mode IN ('universal', 'single_use')),
        rewards jsonb NOT NULL,
        max_claims integer NULL CHECK (max_claims IS NULL OR max_claims > 0),
        claimed_count integer NOT NULL DEFAULT 0 CHECK (claimed_count >= 0),
        created_at timestamptz NOT NULL,
        expires_at timestamptz NULL,
        disabled_at timestamptz NULL
    ) WITH (fillfactor = 90);
    CREATE INDEX IF NOT EXISTS ix_redeem_batches_created_at
        ON economy.redeem_batches(created_at DESC, batch_id DESC);

    -- 全服唯一兑换码索引表（通用码 1 行、单次码每码 1 行，共享大写主键防撞码）。
    CREATE TABLE IF NOT EXISTS economy.redeem_codes (
        code_normalized varchar(32) PRIMARY KEY CHECK (char_length(code_normalized) BETWEEN 4 AND 32),
        batch_id bigint NOT NULL REFERENCES economy.redeem_batches(batch_id) ON DELETE CASCADE,
        redeemed_by uuid NULL REFERENCES identity.players(player_id),
        redeemed_at timestamptz NULL
    ) WITH (fillfactor = 90);
    CREATE INDEX IF NOT EXISTS ix_redeem_codes_batch
        ON economy.redeem_codes(batch_id, redeemed_at NULLS FIRST, code_normalized);

    -- 每角色每批次永久核销防重表（极窄主键，不随 30 天经济流水清理，防长期有效口令二次重领）。
    CREATE TABLE IF NOT EXISTS economy.player_redeem_claims (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        batch_id bigint NOT NULL REFERENCES economy.redeem_batches(batch_id) ON DELETE CASCADE,
        code_normalized varchar(32) NOT NULL,
        claimed_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, batch_id)
    );
    CREATE INDEX IF NOT EXISTS ix_player_redeem_claims_batch
        ON economy.player_redeem_claims(batch_id, claimed_at DESC);

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
    CREATE INDEX IF NOT EXISTS ix_market_orders_terminal_updated ON market.orders(updated_at) WHERE status IN ('filled', 'cancelled', 'expired');
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
    CREATE INDEX IF NOT EXISTS ix_market_trades_buyer ON market.trades(buyer_id, traded_at);
    CREATE INDEX IF NOT EXISTS ix_market_trades_seller ON market.trades(seller_id, traded_at);
    CREATE INDEX IF NOT EXISTS ix_market_trades_buy_order ON market.trades(buy_order_id);
    CREATE INDEX IF NOT EXISTS ix_market_trades_sell_order ON market.trades(sell_order_id);

    CREATE TABLE IF NOT EXISTS market.item_stats (
        asset_id varchar(128) NOT NULL,
        variant varchar(128) NOT NULL DEFAULT '',
        reference_price double precision NULL CHECK (reference_price IS NULL OR (reference_price > 0 AND reference_price = reference_price AND reference_price < 'Infinity'::double precision)),
        best_ask_price double precision NULL CHECK (best_ask_price IS NULL OR (best_ask_price > 0 AND best_ask_price = best_ask_price AND best_ask_price < 'Infinity'::double precision)),
        ask_quantity double precision NOT NULL DEFAULT 0 CHECK (ask_quantity >= 0 AND ask_quantity = ask_quantity AND ask_quantity < 'Infinity'::double precision),
        best_bid_price double precision NULL CHECK (best_bid_price IS NULL OR (best_bid_price > 0 AND best_bid_price = best_bid_price AND best_bid_price < 'Infinity'::double precision)),
        bid_quantity double precision NOT NULL DEFAULT 0 CHECK (bid_quantity >= 0 AND bid_quantity = bid_quantity AND bid_quantity < 'Infinity'::double precision),
        day_volume double precision NOT NULL DEFAULT 0 CHECK (day_volume >= 0 AND day_volume = day_volume AND day_volume < 'Infinity'::double precision),
        day_key date NOT NULL,
        week_volume double precision NOT NULL DEFAULT 0 CHECK (week_volume >= 0 AND week_volume = week_volume AND week_volume < 'Infinity'::double precision),
        week_key date NOT NULL,
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (asset_id, variant)
    );

    CREATE TABLE IF NOT EXISTS market.settlement_cursors (
        player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
        pending_count integer NOT NULL DEFAULT 0 CHECK (pending_count >= 0),
        settled_count bigint NOT NULL DEFAULT 0,
        updated_at timestamptz NOT NULL
    );

    CREATE TABLE IF NOT EXISTS market.settlement_claims (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        request_id uuid NOT NULL,
        settled_trade_count integer NOT NULL CHECK (settled_trade_count >= 0),
        claimed_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, request_id)
    );
    CREATE INDEX IF NOT EXISTS ix_settlement_claims_player_time ON market.settlement_claims(player_id, claimed_at DESC);
    CREATE INDEX IF NOT EXISTS ix_settlement_claims_claimed ON market.settlement_claims(claimed_at);

    CREATE TABLE IF NOT EXISTS platform.section_flush_queue (
        queue_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
        status varchar(16) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'dead')),
        attempts integer NOT NULL DEFAULT 0,
        next_attempt_at timestamptz NOT NULL,
        lease_until timestamptz NULL,
        lease_token uuid NULL,
        last_error varchar(512) NULL,
        updated_at timestamptz NOT NULL
    );
    DROP INDEX IF EXISTS platform.ix_section_flush_ready;
    CREATE INDEX IF NOT EXISTS ix_section_flush_ready ON platform.section_flush_queue(next_attempt_at) WHERE status = 'pending';
    CREATE INDEX IF NOT EXISTS ix_section_flush_dead ON platform.section_flush_queue(updated_at) WHERE status = 'dead';
    CREATE INDEX IF NOT EXISTS ix_section_flush_player ON platform.section_flush_queue(player_id, section_kind, state_version DESC);

    CREATE TABLE IF NOT EXISTS platform.integration_outbox (
        outbox_id uuid PRIMARY KEY,
        aggregate_type varchar(64) NOT NULL,
        aggregate_id uuid NOT NULL,
        event_type varchar(96) NOT NULL,
        payload bytea NOT NULL,
        occurred_at timestamptz NOT NULL,
        status varchar(16) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'dead')),
        attempts integer NOT NULL DEFAULT 0,
        visible_at timestamptz NOT NULL,
        lease_until timestamptz NULL,
        lease_token uuid NULL,
        delivered_at timestamptz NULL,
        last_error varchar(512) NULL
    );
    DROP INDEX IF EXISTS platform.ix_outbox_ready;
    CREATE INDEX IF NOT EXISTS ix_outbox_ready ON platform.integration_outbox(visible_at, lease_until) WHERE delivered_at IS NULL AND status = 'pending';
    CREATE INDEX IF NOT EXISTS ix_outbox_dead ON platform.integration_outbox(visible_at) WHERE delivered_at IS NULL AND status = 'dead';
    CREATE INDEX IF NOT EXISTS ix_outbox_delivered ON platform.integration_outbox(delivered_at) WHERE delivered_at IS NOT NULL;

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
        dead_count integer NOT NULL DEFAULT 0 CHECK (dead_count >= 0),
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
    CREATE INDEX IF NOT EXISTS ix_compute_jobs_dead_player ON platform.compute_jobs(player_id, updated_at DESC) WHERE status = 'dead';
    -- 同一玩家最多一个未完结冷挂机任务：pending/leased 期间重复入队走 upsert/忽略，
    -- 防止同一玩家被并发派发到多个计算批次。
    CREATE UNIQUE INDEX IF NOT EXISTS ux_compute_jobs_open
        ON platform.compute_jobs(player_id) WHERE status IN ('pending', 'leased');

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
        -- 队伍档案：招募标签口径（目标类型/目标节点/身家门槛/公开性），不参与行动或战斗结算。
        name varchar(32) NOT NULL DEFAULT '',
        activity_kind varchar(16) NOT NULL DEFAULT 'any'
            CHECK (activity_kind IN ('any', 'combat', 'dungeon', 'exploration', 'cultivation', 'alchemy', 'forging', 'talisman', 'formation', 'enhancement')),
        node_id varchar(128) NOT NULL DEFAULT '',
        min_valuation double precision NOT NULL DEFAULT 0 CHECK (min_valuation >= 0),
        is_public boolean NOT NULL DEFAULT false,
        -- 队伍斗法开关（总纲 §7.10.4）：仅 activity_kind='combat' 可为 true、仅队长可改、
        -- 会话创建时冻结进参与规格；改离讨伐目标由社交层归位 false。
        pvp_enabled boolean NOT NULL DEFAULT false,
        -- 开启槽位（总纲 §21.13）：2~5，默认 3；收窄后不得低于当前队员数（由社交层裁决保证）。
        open_slots smallint NOT NULL DEFAULT 3 CHECK (open_slots BETWEEN 2 AND 5),
        -- 队伍难度：野外讨伐 0~5，追忆副本 0~10（总纲 §7.3 / §30.3），默认 0。
        difficulty smallint NOT NULL DEFAULT 0 CHECK (difficulty BETWEEN 0 AND 10),
        -- 秘境副本目标 ID（activity_kind='dungeon' 时有效，空串表示未指定）。
        dungeon_id varchar(128) NOT NULL DEFAULT '',
        -- 秘境副本连刷次数（1~99 或 -1 刷至体力耗尽，默认 1）。
        dungeon_repeat_count integer NOT NULL DEFAULT 1 CHECK (dungeon_repeat_count = -1 OR (dungeon_repeat_count >= 1 AND dungeon_repeat_count <= 99)),
        doudizhu_stake double precision NOT NULL DEFAULT 10 CHECK (doudizhu_stake >= 0 AND doudizhu_stake < 'Infinity'::double precision),
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
        ready boolean NOT NULL DEFAULT false,
        doudizhu_ready boolean NOT NULL DEFAULT false,
        -- 秘境副本助战标记（总纲 §30.5.3）：助战成员不扣体力不拿收益，照常参战。
        assist_mode boolean NOT NULL DEFAULT false
    );
    CREATE INDEX IF NOT EXISTS ix_party_members_party ON social.party_members(party_id);

    CREATE TABLE IF NOT EXISTS social.doudizhu_games (
        party_id uuid PRIMARY KEY,
        game_id uuid NOT NULL UNIQUE,
        state jsonb NOT NULL,
        state_version bigint NOT NULL DEFAULT 0 CHECK (state_version >= 0),
        status smallint NOT NULL CHECK (status BETWEEN 0 AND 2),
        updated_at timestamptz NOT NULL
    );

    -- 组队讨伐会话（总纲 §21.13）：spec 为冻结输入，runtime_state 为唯一权威运行态。
    -- party_id 仅保留历史引用，队伍解散不能级联删除尚待成员消费的结算证据。
    -- 同一队伍同时至多一个活跃会话：由 ended_at IS NULL 部分唯一索引收敛并发仲裁。
    CREATE TABLE IF NOT EXISTS social.party_sessions (
        session_id varchar(64) PRIMARY KEY,
        party_id uuid NOT NULL,
        spec jsonb NOT NULL,
        started_at timestamptz NOT NULL,
        runtime_state jsonb NULL,
        state_version bigint NOT NULL DEFAULT 0 CHECK (state_version >= 0),
        last_simulated_at timestamptz NULL,
        ended_at timestamptz NULL,
        end_reason varchar(64) NULL,
        CONSTRAINT ck_party_sessions_checkpoint_presence CHECK ((runtime_state IS NULL) = (last_simulated_at IS NULL))
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_party_sessions_active
        ON social.party_sessions(party_id) WHERE ended_at IS NULL;
    -- 冷挂机在 RepeatableRead 下探测活跃会话；GIN 将成员 JSONB 探测保持为索引读，避免全表扫描造成容量退化。
    CREATE INDEX IF NOT EXISTS ix_party_sessions_active_spec
        ON social.party_sessions USING gin (spec jsonb_path_ops) WHERE ended_at IS NULL;

    -- 仅实际结算批次进入收件箱；成员在自身单写者上下文中与资产、分段一并确认。
    CREATE TABLE IF NOT EXISTS social.party_session_inbox (
        session_id varchar(64) NOT NULL REFERENCES social.party_sessions(session_id),
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        sequence bigint NOT NULL CHECK (sequence > 0),
        payload jsonb NOT NULL,
        created_at timestamptz NOT NULL,
        consumed_at timestamptz NULL,
        consumed_transaction_id uuid NULL REFERENCES economy.transactions(transaction_id) ON DELETE SET NULL,
        PRIMARY KEY (session_id, player_id, sequence),
        CONSTRAINT ck_party_session_inbox_consumed CHECK (consumed_at IS NOT NULL OR consumed_transaction_id IS NULL)
    );
    CREATE INDEX IF NOT EXISTS ix_party_session_inbox_pending
        ON social.party_session_inbox(player_id, created_at, session_id, sequence) WHERE consumed_at IS NULL;
    CREATE INDEX IF NOT EXISTS ix_party_session_inbox_consumed_tx
        ON social.party_session_inbox(consumed_transaction_id) WHERE (consumed_transaction_id IS NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_party_session_inbox_consumed_at
        ON social.party_session_inbox(consumed_at) WHERE consumed_at IS NOT NULL;

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
            CHECK (activity_kind IN ('any', 'combat', 'dungeon', 'exploration', 'cultivation', 'alchemy', 'forging', 'talisman', 'formation', 'enhancement')),
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
        channel varchar(16) NOT NULL CHECK (channel IN ('world', 'nearby', 'friend', 'party', 'sect')),
        scope_key varchar(160) NOT NULL,
        sender_id uuid NOT NULL REFERENCES identity.players(player_id),
        sender_name varchar(64) NOT NULL,
        body varchar(200) NOT NULL CHECK (char_length(body) > 0),
        party_ref uuid REFERENCES social.parties(party_id) ON DELETE SET NULL,
        sent_at timestamptz NOT NULL,
        sender_realm integer NOT NULL DEFAULT 1,
        quote_message_id uuid,
        quote_sender_name varchar(64),
        quote_body varchar(200),
        like_version bigint NOT NULL DEFAULT 0 CONSTRAINT chat_messages_like_version_check CHECK (like_version >= 0),
        CONSTRAINT chat_messages_quote_snapshot_check CHECK (
            (quote_message_id IS NULL) = (quote_sender_name IS NULL) AND
            (quote_message_id IS NULL) = (quote_body IS NULL)
        )
    );
    CREATE INDEX IF NOT EXISTS ix_chat_messages_scope
        ON social.chat_messages(channel, scope_key, sent_at DESC, message_id DESC);

    -- 只保存当前点赞状态；父消息裁剪时按主键前缀级联清理，不保留事件历史。
    CREATE TABLE IF NOT EXISTS social.chat_message_likes (
        message_id uuid NOT NULL REFERENCES social.chat_messages(message_id) ON DELETE CASCADE,
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        PRIMARY KEY (message_id, player_id)
    );

    -- 全服群发邮件：全服只落 1 行不可变公告，由服务端共享不可变缓存零副本分发（expires_at IS NULL 表示永不过期）
    CREATE TABLE IF NOT EXISTS social.broadcast_mails (
        broadcast_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        title text NOT NULL,
        body text NOT NULL,
        sender_name text NOT NULL,
        attachments jsonb NOT NULL DEFAULT '[]'::jsonb,
        only_existing_players boolean NOT NULL DEFAULT true,
        created_at timestamptz NOT NULL,
        expires_at timestamptz NULL,
        revoked_at timestamptz NULL
    );
    CREATE INDEX IF NOT EXISTS ix_broadcast_mails_active
        ON social.broadcast_mails(expires_at, created_at DESC)
        WHERE revoked_at IS NULL;

    -- 群发邮件玩家个人交互位图（稀疏按需建行：零交互玩家不落行、不占内存）
    CREATE TABLE IF NOT EXISTS social.player_mail_states (
        player_id uuid PRIMARY KEY,
        read_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
        claimed_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
        deleted_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
        updated_at timestamptz NOT NULL
    );

    -- 个人定向邮件（单人上限 100 封，已读/已领状态内联行内；expires_at IS NULL 表示永不过期）
    CREATE TABLE IF NOT EXISTS social.player_mails (
        mail_id uuid PRIMARY KEY,
        player_id uuid NOT NULL,
        title text NOT NULL,
        body text NOT NULL,
        sender_name text NOT NULL,
        attachments jsonb NOT NULL DEFAULT '[]'::jsonb,
        has_attachments boolean NOT NULL DEFAULT false,
        is_read boolean NOT NULL DEFAULT false,
        is_claimed boolean NOT NULL DEFAULT false,
        created_at timestamptz NOT NULL,
        expires_at timestamptz NULL
    );
    CREATE INDEX IF NOT EXISTS ix_player_mails_player_created
        ON social.player_mails(player_id, created_at DESC);
    CREATE INDEX IF NOT EXISTS ix_player_mails_expires
        ON social.player_mails(expires_at);

    -- 传音自定义表情包多端同步旁路表（单角色活跃上限 100 个，deleted_at 为跨端离线删改墓碑，30 天由清理器回收）。
    CREATE TABLE IF NOT EXISTS social.player_stickers (
        player_id uuid NOT NULL REFERENCES identity.players(player_id) ON DELETE CASCADE,
        sticker_id varchar(64) NOT NULL,
        url varchar(200) NOT NULL CHECK (char_length(url) > 0),
        sha256 char(64) NULL,
        mime varchar(32) NOT NULL DEFAULT '',
        use_count integer NOT NULL DEFAULT 0 CHECK (use_count >= 0),
        last_used_at bigint NOT NULL DEFAULT 0 CHECK (last_used_at >= 0),
        added_at bigint NOT NULL CHECK (added_at >= 0),
        updated_at bigint NOT NULL CHECK (updated_at >= 0),
        needs_reupload boolean NOT NULL DEFAULT false,
        deleted_at bigint NULL CHECK (deleted_at IS NULL OR deleted_at >= 0),
        PRIMARY KEY (player_id, sticker_id)
    );
    CREATE INDEX IF NOT EXISTS ix_player_stickers_player_active
        ON social.player_stickers(player_id, use_count DESC, last_used_at DESC, added_at DESC)
        WHERE deleted_at IS NULL;
    CREATE INDEX IF NOT EXISTS ix_player_stickers_deleted_cleanup
        ON social.player_stickers(deleted_at)
        WHERE deleted_at IS NOT NULL;

    -- 道友攻略主表（个人榜 Top 50 修士每日限发 1 篇，正文上限 10,000 字；deleted_at 非空表示软删除，30 天后物理回收）。
    CREATE TABLE IF NOT EXISTS social.guide_posts (
        post_id uuid PRIMARY KEY,
        author_id uuid NOT NULL REFERENCES identity.players(player_id),
        title varchar(120) NOT NULL CHECK (char_length(btrim(title)) > 0),
        summary varchar(400) NOT NULL DEFAULT '',
        body text NOT NULL CHECK (char_length(btrim(body)) > 0),
        unlock_price double precision NOT NULL DEFAULT 0 CHECK (unlock_price >= 0 AND unlock_price <= 100000),
        view_count integer NOT NULL DEFAULT 0 CHECK (view_count >= 0),
        like_count integer NOT NULL DEFAULT 0 CHECK (like_count >= 0),
        tipper_count integer NOT NULL DEFAULT 0 CHECK (tipper_count >= 0),
        total_reward_stones double precision NOT NULL DEFAULT 0 CHECK (total_reward_stones >= 0),
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        deleted_at timestamptz NULL
    ) WITH (fillfactor = 85);
    CREATE INDEX IF NOT EXISTS ix_guide_posts_active_reward
        ON social.guide_posts(total_reward_stones DESC, tipper_count DESC, created_at DESC, post_id DESC)
        WHERE deleted_at IS NULL;
    CREATE INDEX IF NOT EXISTS ix_guide_posts_active_created
        ON social.guide_posts(created_at DESC, post_id DESC)
        WHERE deleted_at IS NULL;
    CREATE INDEX IF NOT EXISTS ix_guide_posts_author
        ON social.guide_posts(author_id, created_at DESC)
        WHERE deleted_at IS NULL;
    CREATE INDEX IF NOT EXISTS ix_guide_posts_deleted
        ON social.guide_posts(deleted_at)
        WHERE deleted_at IS NOT NULL;

    -- 道友攻略每日发帖额度记录（按北京时间 UTC+8 自然日计，删帖不退还当日额度）。
    CREATE TABLE IF NOT EXISTS social.guide_daily_quotas (
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        post_date date NOT NULL,
        post_count integer NOT NULL DEFAULT 0 CHECK (post_count >= 0),
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (player_id, post_date)
    );
    CREATE INDEX IF NOT EXISTS ix_guide_daily_quotas_updated
        ON social.guide_daily_quotas(updated_at);

    -- 道友攻略独立阅读去重记录（每帖每玩家至多计 1 次阅读）。
    CREATE TABLE IF NOT EXISTS social.guide_post_views (
        post_id uuid NOT NULL REFERENCES social.guide_posts(post_id) ON DELETE CASCADE,
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        viewed_at timestamptz NOT NULL,
        PRIMARY KEY (post_id, player_id)
    );

    -- 道友攻略免费点赞表。
    CREATE TABLE IF NOT EXISTS social.guide_post_likes (
        post_id uuid NOT NULL REFERENCES social.guide_posts(post_id) ON DELETE CASCADE,
        player_id uuid NOT NULL REFERENCES identity.players(player_id),
        created_at timestamptz NOT NULL,
        PRIMARY KEY (post_id, player_id)
    );

    -- 道友攻略付费可见永久解锁表。
    CREATE TABLE IF NOT EXISTS social.guide_post_unlocks (
        post_id uuid NOT NULL REFERENCES social.guide_posts(post_id) ON DELETE CASCADE,
        viewer_id uuid NOT NULL REFERENCES identity.players(player_id),
        paid_stones double precision NOT NULL CHECK (paid_stones > 0),
        author_received_stones double precision NOT NULL CHECK (author_received_stones >= 0),
        unlocked_at timestamptz NOT NULL,
        PRIMARY KEY (post_id, viewer_id)
    );

    -- 道友攻略自由打赏累计表。
    CREATE TABLE IF NOT EXISTS social.guide_post_tips (
        post_id uuid NOT NULL REFERENCES social.guide_posts(post_id) ON DELETE CASCADE,
        tipper_id uuid NOT NULL REFERENCES identity.players(player_id),
        total_tipped_stones double precision NOT NULL CHECK (total_tipped_stones > 0),
        tip_count integer NOT NULL DEFAULT 1 CHECK (tip_count > 0),
        updated_at timestamptz NOT NULL,
        PRIMARY KEY (post_id, tipper_id)
    );

    -- 角色名片配装付费查阅当日授权表（维持至北京时间当日结束零点，即次日 00:00:00+08:00）。
    CREATE TABLE IF NOT EXISTS social.loadout_inspect_grants (
        viewer_id uuid NOT NULL REFERENCES identity.players(player_id),
        target_id uuid NOT NULL REFERENCES identity.players(player_id),
        access_date date NOT NULL,
        paid_stones double precision NOT NULL CHECK (paid_stones > 0),
        target_received_stones double precision NOT NULL CHECK (target_received_stones >= 0),
        expires_at timestamptz NOT NULL,
        created_at timestamptz NOT NULL,
        PRIMARY KEY (viewer_id, target_id, access_date)
    );
    CREATE INDEX IF NOT EXISTS ix_loadout_inspect_grants_expires
        ON social.loadout_inspect_grants(expires_at);

    -- 高频状态覆盖表与瞬时防重队列调优 fillfactor 与 autovacuum，降低页面分裂与膨胀写放大。
    ALTER TABLE game.player_state_heads SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
    ALTER TABLE game.player_state_sections SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
    ALTER TABLE game.player_projection SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
    ALTER TABLE game.player_runtime_leases SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_cost_limit = 1000);
    ALTER TABLE economy.player_asset_balances SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
    ALTER TABLE platform.section_flush_queue SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_cost_limit = 1000);
    ALTER TABLE economy.transient_idempotency SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.05, autovacuum_vacuum_cost_limit = 1000);

    -- 独立 worker 只需要串行化刷盘，不得读写身份表。函数不返回身份字段。
    CREATE OR REPLACE FUNCTION game.lock_player_for_section_flush(uuid) RETURNS boolean
    LANGUAGE sql VOLATILE STRICT SECURITY DEFINER SET search_path = pg_catalog
    AS $lock_player$
        SELECT true FROM identity.players WHERE player_id = $1 FOR NO KEY UPDATE;
    $lock_player$;
    REVOKE ALL ON FUNCTION game.lock_player_for_section_flush(uuid) FROM PUBLIC;
    DO $flush_roles$
    DECLARE identity_owner name := (
        SELECT pg_catalog.pg_get_userbyid(relowner) FROM pg_catalog.pg_class WHERE oid = 'identity.players'::regclass
    );
    BEGIN
        -- 使用身份表属主的权限，不继承偶然以超级用户执行迁移的权限。
        IF identity_owner <> current_user THEN
            EXECUTE pg_catalog.format('ALTER FUNCTION game.lock_player_for_section_flush(uuid) OWNER TO %I', identity_owner);
        END IF;
        IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'daohua_api') THEN
            GRANT EXECUTE ON FUNCTION game.lock_player_for_section_flush(uuid) TO daohua_api;
        END IF;
        IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'daohua_worker') THEN
            GRANT EXECUTE ON FUNCTION game.lock_player_for_section_flush(uuid) TO daohua_worker;
            GRANT INSERT (event_id, actor_id, event_type, target_id, payload, occurred_at),
                SELECT (event_id) ON identity.audit_events TO daohua_worker;
        END IF;
    END;
    $flush_roles$;

    CREATE TABLE IF NOT EXISTS platform.schema_version (
        version integer PRIMARY KEY,
        applied_at timestamptz NOT NULL
    );
    INSERT INTO platform.schema_version(version, applied_at)
    VALUES (19, now()), (20, now()), (21, now()), (22, now()), (23, now()), (24, now()),
        (25, now()), (26, now()), (27, now()), (28, now()), (29, now()), (30, now()), (31, now()), (32, now()), (33, now()), (34, now()), (35, now()), (36, now()), (37, now()), (38, now()),
        (39, now()), (40, now()), (41, now()), (42, now()), (43, now()), (44, now()), (45, now()), (46, now()), (47, now()), (48, now()), (49, now()), (50, now()), (51, now()), (52, now()), (53, now()), (54, now()), (55, now()), (56, now()), (57, now()), (58, now()), (59, now()), (60, now()), (61, now()), (62, now()), (63, now()), (64, now()), (65, now()), (66, now()), (67, now()), (68, now()), (69, now())
    ON CONFLICT (version) DO NOTHING;
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

/// <summary>启动期标签巡检：EnsureAsync 已完成 schema 版本门禁，HostedService 仅做就绪日志与非阻塞旧标签告警。</summary>
public sealed class PostgresSchemaHostedService(
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<PostgresSchemaHostedService> logger) : IHostedService
{
    /// <summary>记录 schema 就绪状态并巡检是否存在落后当前解码器版本的存档行。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!options.EnableSchemaBootstrap)
            logger.LogInformation("PostgreSQL schema bootstrap 已关闭，已校验 schema v{Version}。", PostgresSchema.Version);
        else
            logger.LogInformation("PostgreSQL schema v{Version} 已就绪。", PostgresSchema.Version);

        // 存档标签非阻塞巡检：不阻断启动（避免个别离线玩家的脏数据拖死全服），
        // 但若有偏离当前解码器版本的行，必须在启动日志明确告警并给出修复 SQL。
        await WarnStaleSectionLabelsAsync(connection, cancellationToken);
    }

    private async Task WarnStaleSectionLabelsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var expected = global::Domain.Persistence.SectionCodec.CurrentSchemaVersion;
            await using var command = new NpgsqlCommand($"""
                SELECT
                    (SELECT count(*) FROM game.player_state_heads WHERE schema_version <> {expected}),
                    (SELECT count(*) FROM game.player_state_sections WHERE schema_version <> {expected}),
                    (SELECT count(*) FROM platform.section_flush_queue WHERE schema_version <> {expected});
                """, connection)
            {
                CommandTimeout = 10
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var heads = reader.GetInt64(0);
                var sections = reader.GetInt64(1);
                var queue = reader.GetInt64(2);
                if (heads + sections + queue > 0)
                {
                    logger.LogWarning(
                        "检测到存档标签与当前解码器版本 (v{Expected}) 不一致：heads={Heads} sections={Sections} queue={Queue}；这些玩家加载时会抛异常。若系手动迁移遗留，请停写后推进标签：" +
                        $"UPDATE game.player_state_sections SET schema_version = {expected} WHERE schema_version <> {expected}; " +
                        $"UPDATE game.player_state_heads SET schema_version = {expected} WHERE schema_version <> {expected}; " +
                        $"UPDATE platform.section_flush_queue SET schema_version = {expected} WHERE schema_version <> {expected};",
                        expected, heads, sections, queue);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 巡检仅用于运维预警，查询失败不阻止服务正常启动。
            logger.LogDebug(exception, "启动期存档标签巡检跳过。");
        }
    }

    /// <summary>无资源需要停止时清理。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
