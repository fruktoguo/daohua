-- v36 -> v37：一次性结束旧全量托管会话，退款未耗余量，并切换会话 JSON 为钱包/离线储备格式。
-- 必须停止 API、worker 和计算节点并完成全库备份后显式执行；不改玩家八分段载荷及 v36 标签。
BEGIN;

LOCK TABLE platform.schema_version, social.party_sessions, social.party_session_inbox,
    economy.transactions, economy.transaction_lines, economy.player_asset_balances IN EXCLUSIVE MODE;

DO $migration$
DECLARE
    current_version integer;
    session_row record;
    member_row record;
    refund_row record;
    document jsonb;
    frozen_spec jsonb;
    member_document jsonb;
    expedition jsonb;
    ally_document jsonb;
    clocks jsonb;
    refunds jsonb;
    transaction_id uuid;
    idempotency_key text;
    checkpoint_at timestamptz;
    checkpoint_seconds double precision;
    next_sequence bigint;
    line_number integer;
    member_index integer;
BEGIN
    SELECT max(version) INTO current_version FROM platform.schema_version;
    IF current_version = 37 THEN RETURN; END IF;
    IF current_version IS DISTINCT FROM 36 THEN
        RAISE EXCEPTION '0037 requires schema v36, found %', current_version;
    END IF;
    IF EXISTS (SELECT 1 FROM game.player_runtime_leases WHERE lease_until > now())
        OR EXISTS (SELECT 1 FROM market.partition_leases WHERE expires_at > now())
        OR EXISTS (SELECT 1 FROM platform.compute_jobs WHERE status = 'leased' AND lease_until > now()) THEN
        RAISE EXCEPTION 'Stop all services and wait for player, market and compute leases to expire before 0037';
    END IF;

    FOR session_row IN SELECT * FROM social.party_sessions ORDER BY session_id FOR UPDATE LOOP
        frozen_spec := session_row.spec;
        frozen_spec := jsonb_set(frozen_spec, '{members}', (
            SELECT jsonb_agg(CASE WHEN jsonb_typeof(member->'consumableSlots') = 'array'
                THEN jsonb_set(member, '{consumableSlots}', (
                    SELECT coalesce(jsonb_agg(slot - 'escrowQuantity' ORDER BY ordinal), '[]'::jsonb)
                    FROM jsonb_array_elements(member->'consumableSlots') WITH ORDINALITY AS slots(slot, ordinal)))
                ELSE member END ORDER BY ordinal)
            FROM jsonb_array_elements(frozen_spec->'members') WITH ORDINALITY AS members(member, ordinal)));

        IF session_row.runtime_state IS NULL THEN
            -- 初始化提交先于成员绑定和托管扣款；无检查点的组织占位行没有可退资产或成员结算。
            UPDATE social.party_sessions SET spec = frozen_spec,
                ended_at = coalesce(ended_at, started_at),
                end_reason = coalesce(end_reason, 'consumable-wallet-migration')
            WHERE session_id = session_row.session_id;
            CONTINUE;
        END IF;

        document := session_row.runtime_state;
        checkpoint_seconds := (document->>'advancedAtUnixSeconds')::double precision;
        checkpoint_at := session_row.last_simulated_at;
        IF checkpoint_seconds IS NULL OR checkpoint_at IS NULL OR document->'expedition' IS NULL
            OR jsonb_typeof(document->'expedition') <> 'object'
            OR jsonb_typeof(document->'spec'->'members') <> 'array' THEN
            RAISE EXCEPTION 'Invalid old checkpoint, preserve evidence and repair before migration: %', session_row.session_id;
        END IF;
        SELECT coalesce(jsonb_agg(slot - 'remainingQuantity' ORDER BY ordinal), '[]'::jsonb)
            INTO clocks FROM jsonb_array_elements(coalesce(nullif(document->'consumableLedger', 'null'::jsonb), '[]'::jsonb))
            WITH ORDINALITY AS slots(slot, ordinal);

        IF session_row.ended_at IS NULL THEN
            IF EXISTS (SELECT 1
                FROM jsonb_array_elements(coalesce(nullif(document->'consumableLedger', 'null'::jsonb), '[]'::jsonb)) AS rows(slot)
                WHERE (slot->>'memberIndex')::integer IS NULL
                    OR (slot->>'memberIndex')::integer < 0
                    OR (slot->>'memberIndex')::integer >= jsonb_array_length(frozen_spec->'members')
                    OR nullif(slot->>'itemId', '') IS NULL
                    OR length(slot->>'itemId') > 128
                    OR (slot->>'remainingQuantity')::double precision IS NULL
                    OR (slot->>'remainingQuantity')::double precision < 0
                    OR (slot->>'remainingQuantity')::double precision >= 'Infinity'::double precision) THEN
                RAISE EXCEPTION 'Invalid escrow ledger, preserve evidence before migration: %', session_row.session_id;
            END IF;
            -- 旧槽行是共享池的分片，不是余额镜像；按成员/物品归并一次，包含 durable 旧锁定的一件。
            SELECT coalesce(jsonb_agg(jsonb_build_object('memberIndex', member_index_value,
                'itemId', item_id, 'quantity', quantity) ORDER BY member_index_value, item_id), '[]'::jsonb)
            INTO refunds FROM (
                SELECT (slot->>'memberIndex')::integer AS member_index_value, slot->>'itemId' AS item_id,
                    sum((slot->>'remainingQuantity')::double precision) AS quantity
                FROM jsonb_array_elements(coalesce(nullif(document->'consumableLedger', 'null'::jsonb), '[]'::jsonb)) AS slots(slot)
                GROUP BY (slot->>'memberIndex')::integer, slot->>'itemId'
            ) AS amounts WHERE quantity > 0;
            IF EXISTS (SELECT 1 FROM jsonb_array_elements(refunds) AS rows(refund)
                WHERE (refund->>'memberIndex')::integer < 0
                    OR (refund->>'memberIndex')::integer >= jsonb_array_length(frozen_spec->'members')
                    OR (refund->>'quantity')::double precision >= 'Infinity'::double precision) THEN
                RAISE EXCEPTION 'Invalid escrow refund in session %', session_row.session_id;
            END IF;
            IF jsonb_array_length(refunds) > 0 THEN
                transaction_id := gen_random_uuid();
                idempotency_key := 'migration.v37.refund:' || session_row.session_id;
                INSERT INTO economy.transactions(transaction_id, idempotency_key, request_hash,
                    transaction_type, actor_id, result_payload, created_at)
                VALUES(transaction_id, idempotency_key,
                    encode(sha256(convert_to(refunds::text, 'UTF8')), 'hex'), 'migration.participation-refund',
                    (frozen_spec->'members'->0->>'playerId')::uuid, convert_to(refunds::text, 'UTF8'), now());
                line_number := 0;
                FOR refund_row IN SELECT value AS refund FROM jsonb_array_elements(refunds) LOOP
                    member_index := (refund_row.refund->>'memberIndex')::integer;
                    INSERT INTO economy.player_asset_balances(player_id, asset_id, variant, quantity, version, updated_at)
                    VALUES((frozen_spec->'members'->member_index->>'playerId')::uuid,
                        refund_row.refund->>'itemId', '', (refund_row.refund->>'quantity')::double precision, 1, now())
                    ON CONFLICT (player_id, asset_id, variant) DO UPDATE
                    SET quantity = economy.player_asset_balances.quantity + EXCLUDED.quantity,
                        version = economy.player_asset_balances.version + 1, updated_at = EXCLUDED.updated_at;
                    INSERT INTO economy.transaction_lines(transaction_id, line_no, player_id, asset_id, variant, delta)
                    VALUES(transaction_id, line_number, (frozen_spec->'members'->member_index->>'playerId')::uuid,
                        refund_row.refund->>'itemId', '', (refund_row.refund->>'quantity')::double precision);
                    line_number := line_number + 1;
                END LOOP;
            END IF;

            expedition := document->'expedition';
            expedition := expedition || jsonb_build_object('playerHitpoints',
                CASE WHEN (expedition->>'playerHitpoints')::double precision <= 0 THEN 1
                    ELSE (expedition->>'playerHitpoints')::double precision END, 'playerRespawnAtSeconds', NULL);
            IF jsonb_typeof(expedition->'playerState') = 'object' THEN
                expedition := jsonb_set(expedition, '{playerState}', (expedition->'playerState') ||
                    jsonb_build_object('hitpoints', CASE WHEN (expedition->'playerState'->>'hitpoints')::double precision <= 0
                        THEN 1 ELSE (expedition->'playerState'->>'hitpoints')::double precision END, 'respawnAtSeconds', NULL));
            END IF;
            IF jsonb_typeof(expedition->'allies') = 'array' THEN
                expedition := jsonb_set(expedition, '{allies}', (
                    SELECT coalesce(jsonb_agg(jsonb_set(ally, '{state}', (ally->'state') || jsonb_build_object('hitpoints',
                        CASE WHEN (ally->'state'->>'hitpoints')::double precision <= 0 THEN 1
                            ELSE (ally->'state'->>'hitpoints')::double precision END, 'respawnAtSeconds', NULL))
                        ORDER BY ordinal), '[]'::jsonb)
                    FROM jsonb_array_elements(expedition->'allies') WITH ORDINALITY AS allies(ally, ordinal)));
            END IF;
            next_sequence := (document->>'sequence')::bigint + 1;
            document := document || jsonb_build_object('sequence', next_sequence, 'expedition', expedition,
                'endsAtUnixSeconds', checkpoint_seconds);
            FOR member_row IN SELECT value AS member, (ordinality - 1)::integer AS index
                FROM jsonb_array_elements(frozen_spec->'members') WITH ORDINALITY LOOP
                member_index := member_row.index;
                member_document := member_row.member;
                SELECT value INTO ally_document FROM jsonb_array_elements(coalesce(nullif(expedition->'allies', 'null'::jsonb), '[]'::jsonb))
                    WHERE (value->>'memberIndex')::integer = member_index;
                INSERT INTO social.party_session_inbox(session_id, player_id, sequence, payload, created_at)
                VALUES(session_row.session_id, (member_document->>'playerId')::uuid, next_sequence,
                    jsonb_build_object('sessionId', session_row.session_id, 'sequence', next_sequence,
                        'playerId', jsonb_build_object('value', member_document->>'playerId'),
                        'from', checkpoint_at, 'to', checkpoint_at,
                        'hitpoints', CASE WHEN member_index = 0 THEN expedition->'playerHitpoints' ELSE ally_document->'state'->'hitpoints' END,
                        'manapoints', CASE WHEN member_index = 0 THEN expedition->'playerManapoints' ELSE ally_document->'state'->'manapoints' END,
                        'regenCarrySeconds', document->'memberRegenCarrySeconds'->member_index,
                        'skills', CASE WHEN member_index = 0 THEN document->'skills'
                            ELSE coalesce(nullif(ally_document->'combatSkills', 'null'::jsonb), '{"cooldowns":[],"castingAbilityId":null,"castRemainingSeconds":0,"castCount":0}'::jsonb) END,
                        'buffs', CASE WHEN member_index = 0 THEN document->'buffs'
                            ELSE coalesce(nullif(ally_document->'combatBuffs', 'null'::jsonb), '{"buffs":[]}'::jsonb) END,
                        'expedition', NULL, 'progressSeconds', 0, 'completedRepeats', document->'completedRepeats',
                        'stats', '[]'::jsonb, 'assetDeltas', '[]'::jsonb, 'events', '[]'::jsonb, 'combatStats', NULL,
                        'endsAtUnixSeconds', checkpoint_seconds, 'consumableSlots', (
                            SELECT coalesce(jsonb_agg(clock), '[]'::jsonb) FROM jsonb_array_elements(clocks) AS rows(clock)
                            WHERE (clock->>'memberIndex')::integer = member_index)), checkpoint_at);
            END LOOP;
        END IF;

        -- 只补齐待消费投影的新时钟字段，既有奖励与旧退款 delta 不改写、不重复发放。
        UPDATE social.party_session_inbox AS inbox
        SET payload = jsonb_set(inbox.payload, '{consumableSlots}', (
            SELECT coalesce(jsonb_agg(clock ORDER BY ordinal), '[]'::jsonb)
            FROM jsonb_array_elements(clocks) WITH ORDINALITY AS rows(clock, ordinal)
            WHERE (clock->>'memberIndex')::integer = member.ordinality - 1))
        FROM jsonb_array_elements(frozen_spec->'members') WITH ORDINALITY AS member(value, ordinality)
        WHERE inbox.session_id = session_row.session_id AND inbox.consumed_at IS NULL
            AND inbox.player_id = (member.value->>'playerId')::uuid;
        document := document || jsonb_build_object('spec', frozen_spec, 'consumableLedger', clocks,
            'inventoryMode', 0, 'modeSinceUnixSeconds', checkpoint_seconds,
            'nextConsumableEvaluationAtUnixSeconds', checkpoint_seconds + 5,
            'offlineBatchBoundaryUnixSeconds', checkpoint_seconds, 'offlineReserves', '[]'::jsonb);
        UPDATE social.party_sessions SET spec = frozen_spec, runtime_state = document,
            state_version = state_version + 1, ended_at = coalesce(ended_at, checkpoint_at),
            end_reason = coalesce(end_reason, 'consumable-wallet-migration')
        WHERE session_id = session_row.session_id;
    END LOOP;

    INSERT INTO platform.schema_version(version, applied_at) VALUES (37, now());
END;
$migration$;

COMMIT;
