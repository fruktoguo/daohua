-- 仅用于本地 schema 初始化完成后的权限收敛；用数据库管理员执行。
-- 权限组不持有登录凭据，由本地独立登录角色继承。
BEGIN;
DO $roles$
DECLARE role_name text;
BEGIN
    FOREACH role_name IN ARRAY ARRAY['daohua_bootstrap','daohua_api','daohua_worker','daohua_archive','daohua_metrics'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = role_name) THEN
            EXECUTE format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION', role_name);
        END IF;
    END LOOP;
END
$roles$;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA identity, game, economy, market, platform TO daohua_api, daohua_worker;
GRANT SELECT ON platform.schema_version TO daohua_api, daohua_worker;
-- API 当前内置刷盘、outbox 和 retention worker，所以同时需要其运行权限。
GRANT SELECT, INSERT, UPDATE, DELETE ON identity.accounts, identity.players,
    identity.auth_sessions, identity.audit_events, identity.data_protection_keys,
    identity.account_risk_signals, identity.account_risk_events, platform.account_risk_policy, platform.account_risk_key,
    game.player_state_heads, game.player_state_sections, game.player_action_queue,
    game.player_runtime_leases, game.player_projection,
    economy.player_asset_balances, economy.player_asset_instances, economy.transactions,
    economy.transaction_lines, economy.reservations, market.partition_leases,
    market.orders, market.trades, market.settlement_claims, platform.section_flush_queue,
    platform.integration_outbox, platform.command_receipts, platform.compute_jobs TO daohua_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON game.player_state_heads, game.player_state_sections,
    game.player_action_queue, game.player_projection, platform.section_flush_queue,
    platform.integration_outbox TO daohua_worker;
GRANT SELECT, DELETE ON identity.auth_sessions, identity.audit_events, identity.data_protection_keys, identity.account_risk_events,
    platform.compute_jobs, platform.command_receipts, market.settlement_claims TO daohua_worker;
-- metrics 只读取 PostgreSQL 统计，不获得账号、密钥、玩家载荷或账本的 SELECT。
GRANT pg_read_all_stats TO daohua_metrics;
-- 归档脚本需要创建月分区；仅开放两个归档所在 schema 及对应热表。
GRANT USAGE, CREATE ON SCHEMA identity, market TO daohua_archive;
GRANT SELECT, DELETE ON identity.audit_events, market.trades TO daohua_archive;
-- schema 所有权由不可登录的 bootstrap 组持有；运行角色没有 DDL 权限。
DO $owners$
DECLARE object_record record;
BEGIN
    FOR object_record IN SELECT schemaname, tablename FROM pg_tables
        WHERE schemaname IN ('identity','game','economy','market','platform')
            AND tablename NOT LIKE '%\_archive%' ESCAPE '\' LOOP
        EXECUTE format('ALTER TABLE %I.%I OWNER TO daohua_bootstrap', object_record.schemaname, object_record.tablename);
    END LOOP;
END
$owners$;
ALTER SCHEMA identity OWNER TO daohua_bootstrap;
ALTER SCHEMA game OWNER TO daohua_bootstrap;
ALTER SCHEMA economy OWNER TO daohua_bootstrap;
ALTER SCHEMA market OWNER TO daohua_bootstrap;
ALTER SCHEMA platform OWNER TO daohua_bootstrap;
COMMIT;
