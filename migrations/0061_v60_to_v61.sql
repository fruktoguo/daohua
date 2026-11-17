-- 0061_v60_to_v61.sql
-- v60 -> v61：阵盘上次触发时刻可缺省解码，推进 v43 -> v44 标签；另收敛 worker 权限。
-- 必须停写并备份后执行；不重写 JSON/Brotli、校验和或其他检查点。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 60 AND current_version IS DISTINCT FROM 61 THEN
        RAISE EXCEPTION '0061 requires database schema v60 (or v61 on retry), found %', current_version;
    END IF;
END $$;

UPDATE game.player_state_heads SET schema_version = 44 WHERE schema_version = 43;
UPDATE game.player_state_sections SET schema_version = 44 WHERE schema_version = 43;
UPDATE platform.section_flush_queue SET schema_version = 44 WHERE schema_version = 43;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM game.player_state_heads WHERE schema_version <> 44)
        OR EXISTS (SELECT 1 FROM game.player_state_sections WHERE schema_version <> 44)
        OR EXISTS (SELECT 1 FROM platform.section_flush_queue WHERE schema_version <> 44) THEN
        RAISE EXCEPTION '0061 found unexpected player save schema labels; inspect before retrying';
    END IF;
END $$;

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

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (61, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
