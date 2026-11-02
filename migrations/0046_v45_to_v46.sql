-- 0046_v45_to_v46.sql
-- v45 -> v46 兼容迁移：
--   social.parties 追加 difficulty 队伍讨伐难度列（0~5，默认 0）。

BEGIN;

ALTER TABLE social.parties ADD COLUMN IF NOT EXISTS difficulty smallint NOT NULL DEFAULT 0 CHECK (difficulty BETWEEN 0 AND 5);

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (46, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
