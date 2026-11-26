# 数据库与存档版本变更台账（`vX -> vY`）

本文档是《道化太初》持久化演进的**唯一版本变更登记台账**。凡涉及可能影响存量数据库、玩家存档或旧资产加载的改动，**在任务完成时必须**：
1. 推进版本号并在本文档按 `## vX -> vY` 顺序滚动登记；
2. **同步增量编写当前最新版本的迁移/修复脚本 `migrations/NNNN_v{from}_to_v{to}.sql`**（并更新 `migrations/README.md`），保障本地测试库可就地平滑升级；
3. 在 `prod-release` 正式发布前，通读本台账并交叉对账 `git diff` 与 `migrations/` 链条，**检查是否有遗漏或断链**后再发布。

> 当前线上参考水位（以 `TASK-20261114-23` 发布为准）：`yuohira` 正式服数据库 `platform.schema_version = 54`、玩家八分段存档 `SectionCodec = 43`（`MinReadableSchemaVersion = 41`）。

---

## 一、强制登记与增量脚本触发条件（全部纳入）

凡代码或配置涉及以下**任意一类**改动，必须将 `PostgresSchema.Version` 单调 `+1`（全局统一版本号），同步更新空库权威脚本 `PostgresSchema.Sql`（如有 DDL 变动），在本文档末尾追加一节 `## vX -> vY`，并**在任务完成时增量编写 `migrations/NNNN_v{from}_to_v{to}.sql`**：

1. **数据库结构与权限（DDL / DCL）**
   - 新增/删除/修改 schema、表、列、默认值、`CHECK`/外键/唯一约束、索引、序列；
   - `deploy/postgres-local-roles.sql` 中 `daohua_bootstrap` / `daohua_api` / `daohua_worker` 等最小权限角色的授权变化。
2. **玩家存档与持久化载荷（`SectionCodec` / JSON 检查点）**
   - 玩家八分段（`progression` / `loadout` / `bag` / `combat` / `action_queue` / `duel` / `world` / `settings`）或 `game.player_action_queue`、`social.party_sessions` 等持久 JSON 的字段增删、改名、类型/单位变更；
   - `SectionCodec.CurrentSchemaVersion` 升级规则：仅玩家八分段结构或语义变化时 `+1`；可缺省解码的 additive 新增保持 `MinReadableSchemaVersion` 不抬（旧客户端透明续用），仅**破坏性变更**才将 `MinReadableSchemaVersion` 推进至 `CurrentSchemaVersion` 并要求客户端强更。
3. **影响存量存档加载的配表变更（`config/game-data/**`）**
   - 删除或重命名已持久化引用的 `item.*` / `equipment.*` / `ability.*` / `trait.*` / `action.*` / `forge.*` / `node.*` / `building.*` 等 ID；
   - 修改已有配表 ID 对应的堆叠/消耗语义或关键数值单位，导致老存档反序列化抛 `DomainException` 或结算错乱的改动。

---

## 二、日常开发与发版检查分工

1. **日常开发完成时**：
   - 修改 `src/Server/Clean/PostgresSchema.cs`（`Version` 与 `Sql`）、`src/Domain/Persistence/SectionCodec.cs`、`config/game-data/**` 等源码与配表，并在**本文档**完整填报 `## vX -> vY` 变更条目。
   - **必须增量编写 `migrations/NNNN_v{from}_to_v{to}.sql`**（含幂等 DDL、角色 `GRANT`/`OWNER`、存量回填/配表替换、必要时的存档标签推进以及 `platform.schema_version` 写入），同步更新 [`migrations/README.md`](../../migrations/README.md)，并在本地运行 `scripts/migrate-local-db.sh --dry-run`（或实际执行）验证，确保本地开发/测试库可直接就地升级。
   - 若极个别破坏性变更确实无法通过单步自动 SQL 完成（例如必须离线重编码 Brotli JSON 或涉及不可自动裁决的重置），须在对应条目与 `migrations/README.md` 明确标注不可自动迁移原因（本地走 `scripts/reset-outdated-players.sh` 交互确认 `RESET`）。
2. **正式服发布前（`prod-release` 查漏与收口）**：
   - 发布前在 **阶段 0** 核对生产库实际 `platform.schema_version`（记为 `v_prod`）与代码目标 `PostgresSchema.Version`（记为 `v_target`），**通读本文档 `[v_prod -> ... -> v_target]` 全部章节**并与 `git diff`、`migrations/` 链条交叉校验。
   - **重点检查是否有遗漏或断链**：
     - **正常路径（无遗漏且全链路连续覆盖 `v_prod -> target_ver`）**：复核已有 `migrations/NNNN_vX_to_vY.sql` 内容完整无误（DDL/权限/回填/存档标签齐备），直接走 `release-prod.sh` / `deploy-prod.sh` 链式迁移。
     - **遗漏补齐路线（发现某步缺失脚本或出现断链）**：针对尚未被 `migrations/` 连续覆盖的跨度 `[covered_ver -> target_ver]`，补齐单个或缺失的事务脚本 `migrations/NNNN_v{covered_ver}_to_v{target_ver}.sql`（汇总缺失的 DDL、权限 `GRANT`、回填、配表 ID 替换与存档标签推进），同步更新 [`migrations/README.md`](../../migrations/README.md) 并提交后再构建推送。
     - **停写手动迁移路线（特殊情况）**：对涉及真实资产退款/补偿、需离线重写 Brotli JSON 载荷重算 SHA-256、或需交互式核对的复杂配表删项，分步完成镜像推送与全库备份后，在阶段 2.5 停写窗口手工执行文档指引的 SQL / `.mjs` 脚本并验收版本与标签零不一致，再执行 `deploy-prod.sh`。

---

## 三、变更条目标准模板（复制使用）

```markdown
## v{from} -> v{to}

- **变更日期**：YYYY-MM-DD HH:mm:ss
- **关联任务**：`TASK-YYYYMMDD-XX`（简述）
- **数据库版本**：`PostgresSchema.Version` {from} -> {to}
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 未变（保持 {S}） / {S1} -> {S2}；`MinReadableSchemaVersion` 保持 {M} / 升至 {S2}
- **迁移脚本状态**：已完成 `migrations/NNNN_v{from}_to_v{to}.sql`（本地校验通过） / 需停写人工迁移（说明原因）
- **存量处理结论**：无需额外处理 / 仅推进版本号或存档标签 / 需执行 SQL 回填或清理 / 需离线重写存档载荷或人工介入

### 1. 变了什么
- 【库结构/权限】……（写明具体 schema.table.column、约束、索引、role GRANT）
- 【存档载荷】……（写明分段名、字段名、JSON 默认值兼容性；未变则写「无」）
- 【配表/持久化语义】……（写明删改/重命名的 ID 映射表，如 `old.id -> new.id`；未变则写「无」）

### 2. 是否需要处理存量数据
- 说明原因：……（例如：新字段可缺省解码为默认语义 / `NOT NULL` 无默认值必须回填 / 已删配表 ID 会导致 `DomainException` 需替换或回收……）

### 3. 如果要处理，怎么处理
- 【增量迁移脚本 `migrations/NNNN_v{from}_to_v{to}.sql` 要点 / 人工停服执行语句与校验 SQL】：
  ```sql
  -- 给出幂等 DDL、角色 GRANT、存量 UPDATE/替换逻辑、以及必要的校验语句
  ```
- 【离线脚本 / 人工步骤】（若不涉及可写「无」）：……
```

---

## 四、版本演进登记簿

> 按版本升序追加；**v54 为当前线上正式服运行基线，v55 起纳入本台账规范。**

## v54 -> v55

- **变更日期**：2026-11-14 23:42:27
- **关联任务**：`TASK-20261114-24`（机器人账号 `is_bot` 标记与附近玩家/排行榜屏蔽）
- **数据库版本**：`PostgresSchema.Version` 54 -> 55
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 保持 `43`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已有 `migrations/0055_v54_to_v55.sql`（新规切换前的最后一版随提交生成的独立脚本）
- **存量处理结论**：需执行 SQL 加列与存量机器人账号打标（已固化于 `0055` 脚本）

### 1. 变了什么
- 【库结构/权限】`identity.accounts` 新增 `is_bot boolean NOT NULL DEFAULT false` 列；不新增独立索引或额外表；现有 `daohua_api` / `daohua_bootstrap` 对 `identity.accounts` 的权限不变。
- 【存档载荷】无变更（玩家八分段载荷与标签保持 `v43`）。
- 【配表/持久化语义】认证会话透传 `is_bot`，`OnlinePresenceRegistry` 过滤附近机器人、`LeaderboardRuntime` 排行物化与宗门成员统计过滤 `NOT is_bot`。

### 2. 是否需要处理存量数据
- **需要**：线上已存在通过 `DaoHua-MarketBots` 注入的 `bot_%` 市场机器人账号；新注册账号默认 `is_bot = false`，若不把存量 `bot_%` 账号批量回填为 `is_bot = true`，既有机器人仍会出现在全服排行榜、附近玩家列表和宗门成员统计中。

### 3. 如果要处理，怎么处理
- 【合入合并迁移脚本的 SQL 指引】：
  ```sql
  ALTER TABLE identity.accounts
      ADD COLUMN IF NOT EXISTS is_bot boolean NOT NULL DEFAULT false;

  UPDATE identity.accounts
     SET is_bot = true
   WHERE is_bot = false
     AND login_normalized LIKE 'bot\_%' ESCAPE '\';
  ```
- 【离线脚本 / 人工步骤】：无（注意若 `prod` 存在前缀不是 `bot_` 的特殊测试/机器人账号，发版后按实际 `login_normalized` 清单补标 `is_bot = true` 并等待整点榜刷新或重启一次应用）。

---

## v55 -> v56

- **提交/阶段**：邮件系统与双轨优化上线（`TASK-20261115-06`、`TASK-20261116-05` 永不过期支持）
- **玩家分段载荷版本**：`43 -> 43`（无变化：邮件完全旁路 `player_state_sections`，不侵入八分段载荷）
- **迁移脚本状态**：已有 `migrations/0056_v55_to_v56.sql`

### 1. 变了什么
- 在 `PostgresSchema.Sql` 新增 3 张 `social.*` 邮件双轨表与对应索引，并在 `deploy/postgres-local-roles.sql` 向 `daohua_api`、`daohua_worker` 授予 `SELECT, INSERT, UPDATE, DELETE` 权限：
  1. `social.broadcast_mails`（全服群发公告只存 1 行：`broadcast_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY`、`title`、`body`、`sender_name`、`attachments jsonb`、`only_existing_players boolean`、`created_at`、`expires_at timestamptz NULL`、`revoked_at`）；
  2. `social.player_mail_states`（按玩家稀疏建行：`player_id uuid PRIMARY KEY`、`read_ids bigint[]`、`claimed_ids bigint[]`、`deleted_ids bigint[]`、`updated_at`）；
  3. `social.player_mails`（个人定向邮件：`mail_id uuid PRIMARY KEY`、`player_id uuid`、`title`、`body`、`sender_name`、`attachments jsonb`、`has_attachments boolean`、`is_read boolean`、`is_claimed boolean`、`created_at`、`expires_at timestamptz NULL`）。
- 同步更新 `PostgresRetentionWorker`：`mail.claim` 归入 14 天长期保留流水白名单，并定期分批清理 `expires_at IS NOT NULL AND expires_at < now()` 的过期个人邮件与过期群发公告（`expires_at IS NULL` 表示永不过期，不自动清理）。

### 2. 是否需要处理存量数据
- **无需迁移存量玩家数据**：全服首次新增三张空表与索引，存量玩家存档（`player_state_heads` / `player_state_sections`）与既有账号数据零改动、零回填；唯一需确保应用连接角色对新增表持有完整 DML 与 `social` 序列权限（已固化在 `migrations/0056_v55_to_v56.sql`）。

### 3. 如果要处理，怎么处理
- 【合入合并迁移脚本的 SQL 指引】（已收拢至 `migrations/0056_v55_to_v56.sql`）：
  ```sql
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

  CREATE TABLE IF NOT EXISTS social.player_mail_states (
      player_id uuid PRIMARY KEY,
      read_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
      claimed_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
      deleted_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
      updated_at timestamptz NOT NULL
  );

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

  DO $$
  BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_bootstrap') THEN
          EXECUTE 'ALTER TABLE social.broadcast_mails OWNER TO daohua_bootstrap';
          EXECUTE 'ALTER TABLE social.player_mail_states OWNER TO daohua_bootstrap';
          EXECUTE 'ALTER TABLE social.player_mails OWNER TO daohua_bootstrap';
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_api') THEN
          EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON social.broadcast_mails, social.player_mail_states, social.player_mails TO daohua_api';
          EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA social TO daohua_api';
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_worker') THEN
          EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON social.broadcast_mails, social.player_mail_states, social.player_mails TO daohua_worker';
          EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA social TO daohua_worker';
      END IF;
  END $$;
  ```
- 【离线脚本 / 人工步骤】：无。

---

## v56 -> v57

- **提交/阶段**：持久化白名单规范与高频落盘根治（`TASK-20261116-06`）
- **玩家分段载荷版本**：`43 -> 43`（无变化：八分段结构与编码格式不变）
- **迁移脚本状态**：已有 `migrations/0057_v56_to_v57.sql`

### 1. 变了什么
- **新增结构**：
  1. `game.duel_logs`（PVP 斗法完整收益日志表）：记录 `duel_log_id`、`duel_id`（唯一索引保证同一场斗法幂等落库）、`initiator_player_id` / `target_player_id`（外键 `ON DELETE CASCADE`）、`initiator_name` / `target_name`、`initiator_power` / `target_power`、`node_id`、`winner_player_id`、`outcome`（`initiator_win` / `target_win` / `draw`）、`forfeited`、`initiator_gains jsonb` / `initiator_losses jsonb` / `target_gains jsonb` / `target_losses jsonb`、`duration_seconds`、`occurred_at`；并建 `ix_duel_logs_occurred`、`ix_duel_logs_initiator`、`ix_duel_logs_target` 与 `ux_duel_logs_duel_id`。
  2. `economy.shop_daily_purchases` 新增时间索引 `ix_shop_daily_purchases_updated(updated_at)`，支撑 30 天超期限购流水清理。
  3. 高频更新状态/队列/临时幂等表设置存储与清理参数（`player_state_heads` / `player_state_sections` `fillfactor = 80`，`section_flush_queue` / `transient_idempotency` `fillfactor = 70` 及更积极的 `autovacuum` 阈值），抑制 HOT 更新膨胀。
- **权限矩阵**：`deploy/postgres-local-roles.sql` 向 `daohua_api` 与 `daohua_worker` 授予 `game.duel_logs` 读写删权限。
- **行为与留存策略**：
  - 高频单人模拟/行动（采集、制造、强化、秘术、PVE、`ability.learn`、`item.use`、`heaven_gate`、`item.discard`、`item.sell`、`tutorial.skip`）统一 `PersistLedger = false`，不再写入 `economy.transactions`；在线 `AdvanceAllAsync` 在内存缓冲资产增量，于关键边界（登入补算、下线、停机、库交互前对账、斗法结束）或每 **10 分钟**（`OnlineCheckpointIntervalSeconds = 600`）原子刷盘。
  - `PostgresRetentionWorker` 改为每 **5 分钟**多批清理：`economy.transient_idempotency` 保留 **2 小时**，非白名单 `economy.transactions` 即刻排空，5 类白名单账本/日志（含 `market.trades`、`market.orders` 已完结无引用行、`game.duel_logs`、`identity.audit_events` 等）统一保留 **30 天**，已完结冷挂机任务 `platform.compute_jobs` 保留 **2 天**。

### 2. 是否需要处理存量数据
- **表结构与历史冗余流水清理**：
  - 既有库需要创建 `game.duel_logs`、索引与 `ix_shop_daily_purchases_updated`，并授予 `daohua_api` / `daohua_worker` 权限。
  - 存量历史上写入的 `simulation.reward`、`item.discard`、`item.sell`、`ability.learn`、`item.use`、`heaven_gate` 等非白名单 `economy.transactions` 行将在升级后由 `PostgresRetentionWorker` 每 5 分钟自动分批清理（级联清理 `economy.transaction_lines`），无需全表锁删；`market.trades_archive_*` / `audit_events_archive_*` 超 30 天历史归档分区由 `market-ledger-archive.sh` 自动清理。
  - 存量玩家八分段载荷保持 `v43`，无需转换或重置玩家存档。

### 3. 如果要处理，怎么处理
- 【合入合并迁移脚本的 SQL 指引】：
  ```sql
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

  CREATE INDEX IF NOT EXISTS ix_shop_daily_purchases_updated
      ON economy.shop_daily_purchases(updated_at);

  ALTER TABLE game.player_state_heads SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
  ALTER TABLE game.player_state_sections SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
  ALTER TABLE platform.section_flush_queue SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_cost_limit = 1000);
  ALTER TABLE economy.transient_idempotency SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.05, autovacuum_vacuum_cost_limit = 1000);

  DO $$
  BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_bootstrap') THEN
          EXECUTE 'ALTER TABLE game.duel_logs OWNER TO daohua_bootstrap';
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_api') THEN
          EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON game.duel_logs TO daohua_api';
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_worker') THEN
          EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON game.duel_logs TO daohua_worker';
      END IF;
  END $$;
  ```
- 【离线脚本 / 人工步骤】：若生产此前运行历史大量积累导致 `economy.transactions` 存在数百万旧非白名单流水，可在发版低峰观察 `PostgresRetentionWorker` 5 分钟批次自然排空并触发一次 `VACUUM (ANALYZE) economy.transactions, economy.transaction_lines;` 回收物理页。

---

## v57 -> v58

- **变更日期**：2026-11-16 18:52:00
- **关联任务**：`TASK-20261116-09`（每日签到系统与高密度独立弹窗）
- **数据库版本**：`PostgresSchema.Version` 57 -> 58
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 保持 `43`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已有 `migrations/0058_v57_to_v58.sql`
- **存量处理结论**：新增独立空表与角色授权，存量玩家存档与账号数据无需回填

### 1. 变了什么
- 【库结构/权限】在 `PostgresSchema.Sql` 新增 `game.player_daily_checkins`（`player_id uuid PRIMARY KEY REFERENCES identity.players(player_id) ON DELETE CASCADE`、`last_claim_day_index bigint`、`last_claim_date char(10)`、`streak_days integer`、`total_days integer`、`last_reward_merit integer`、`week_start_day_index bigint`、`week_days integer`、`week_merit integer`、`week_days_mask smallint`、`month_key integer`、`month_days integer`、`month_merit integer`、`last_claimed_at timestamptz`、`updated_at timestamptz`），并在 `deploy/postgres-local-roles.sql` 向 `daohua_api`、`daohua_worker` 授予 `SELECT, INSERT, UPDATE, DELETE` 权限。
- 【存档载荷】无变更（签到状态独立于玩家八分段，保持 `v43`）。
- 【配表/持久化语义】签到领奖发放 `item.gongde-bound`（绑定功德），通过 `PostgresEconomyRepository.CommitCoreAsync`（`PersistLedger = false` + `economy.transient_idempotency`）与 `game.player_daily_checkins` 在同一 `Serializable` 事务内原子提交。

### 2. 是否需要处理存量数据
- **无需回填存量玩家数据**：首次上线新表为空表，存量玩家首次访问或登录时天然处于「未签到（可领取）」初始态；发版前只需执行幂等建表与权限授予。

### 3. 如果要处理，怎么处理
- 【合入合并迁移脚本的 SQL 指引】：
  ```sql
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

  DO $$
  BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_bootstrap') THEN
          EXECUTE 'ALTER TABLE game.player_daily_checkins OWNER TO daohua_bootstrap';
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_api') THEN
          EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON game.player_daily_checkins TO daohua_api';
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_worker') THEN
          EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON game.player_daily_checkins TO daohua_worker';
      END IF;
  END $$;
  ```
- 【离线脚本 / 人工步骤】：无。

---

## v58 -> v59

- **变更日期**：2026-11-17 03:30:00
- **关联任务**：全仓持久化死信收口、锁序对齐、索引补齐与留存短事务拆分（热修复加固批次四 / `TASK-20261117-02`）
- **数据库版本**：`PostgresSchema.Version` 58 -> 59
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 保持 `43`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0059_v58_to_v59.sql`
- **存量处理结论**：幂等加列、重建部分索引与参数调优，存量行与玩家存档保持不变

### 1. 变了什么
- 【库结构/权限】
  1. `platform.section_flush_queue` 与 `platform.integration_outbox` 新增 `status varchar(16) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'dead'))`，将就绪索引收窄为 `WHERE status = 'pending'` 并新增死信清理索引（`ix_section_flush_dead`、`ix_outbox_dead`）。
  2. `platform.compute_jobs` 新增 `dead_count integer NOT NULL DEFAULT 0 CHECK (dead_count >= 0)` 与 `ix_compute_jobs_dead_player`（`WHERE status = 'dead'`），超过 5 次终态判死后进入 24 小时冷却闸门，防止坏玩家状态无限循环重演。
  3. 补齐清理与关联反查索引：`identity.auth_sessions(expires_at)` / `(revoked_at) WHERE revoked_at IS NOT NULL`、`economy.reservations(updated_at) WHERE status <> 'active'`、`market.orders(updated_at) WHERE status IN ('filled','cancelled','expired')`、`market.trades(buy_order_id)` / `(sell_order_id)`、`market.settlement_claims(claimed_at)`、`social.party_session_inbox(consumed_at) WHERE consumed_at IS NOT NULL`。
  4. 为高频写入表 `game.player_projection`、`game.player_runtime_leases`、`economy.player_asset_balances` 配置 `fillfactor` 与 `autovacuum` 阈值；无新增表或角色权限变更。
- 【存档载荷】无变更（玩家八分段结构与标签保持 `v43`）。
- 【配表/持久化语义】`PostgresRetentionRepository.PruneAsync` 改按逐表独立短事务分批清理并纳入过期运行时租约/7 天死信；`PostgresSectionRepository` 统一先锁 `identity.players` 并在冷挂机写入前桥接未落盘 `section_flush_queue` pending 行；`PostgresPlayerOwnershipRepository.RenewAsync` 增加 `lease_until > now()` 守卫。

### 2. 是否需要处理存量数据
- **无需回填或重置存量存档**：新增列均带有安全默认值 `'pending'` / `0`，存量表数据直接生效，不改变任何既有表语义，玩家八分段维持 `v43`。

### 3. 如果要处理，怎么处理
- 【合入合并迁移脚本的 SQL 指引】：见 [`migrations/0059_v58_to_v59.sql`](../../migrations/0059_v58_to_v59.sql)（单事务内幂等加列、加约束、重建索引、设置 `fillfactor`/`autovacuum` 并写入 `platform.schema_version = 59`）。
- 【离线脚本 / 人工步骤】：无。

---

## v59 -> v60

- **变更日期**：2026-11-17 04:45:00
- **关联任务**：`TASK-20261117-03`（表情包上限 100 个、多端同步与自动上传重传；与 `v0.3.12` 合流顺延自原草案 `v59`）
- **数据库版本**：`PostgresSchema.Version` 59 -> 60
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 保持 `43`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0060_v59_to_v60.sql`
- **存量处理结论**：新增独立旁路空表与角色授权，存量玩家存档与账号数据无需回填

### 1. 变了什么
- 【库结构/权限】在 `PostgresSchema.Sql` 新增 `social.player_stickers`（`player_id uuid NOT NULL REFERENCES identity.players(player_id) ON DELETE CASCADE`、`sticker_id varchar(64) NOT NULL`、`url varchar(200) NOT NULL`、`sha256 char(64) NULL`、`mime varchar(32) NOT NULL DEFAULT ''`、`use_count integer NOT NULL DEFAULT 0`、`last_used_at bigint NOT NULL DEFAULT 0`、`added_at bigint NOT NULL`、`updated_at bigint NOT NULL`、`needs_reupload boolean NOT NULL DEFAULT false`、`deleted_at bigint NULL`、`PRIMARY KEY (player_id, sticker_id)`）及索引 `ix_player_stickers_player_active` / `ix_player_stickers_deleted_cleanup`，并在 `deploy/postgres-local-roles.sql` 向 `daohua_api`、`daohua_worker` 授予 `SELECT, INSERT, UPDATE, DELETE` 权限。
- 【清理策略】`PostgresRetentionWorker` 按独立短事务定期清理 `deleted_at IS NOT NULL` 超过 30 天的软删除墓碑行。
- 【存档载荷】无变更（表情包同步完全旁路玩家八分段与 ECS，保持 `v43`）。
- 【配表/持久化语义】单角色活跃表情包上限为 `100` 个（服务端按 `player_id` 事务建议锁串行校验）。

### 2. 是否需要处理存量数据
- **无需回填存量玩家数据**：首次上线新表为空表，存量玩家原存于浏览器本地的旧表情包会在首次登录同步时自动认领合并至云端。

### 3. 如果要处理，怎么处理
- 【合入合并迁移脚本的 SQL 指引】：见 [`migrations/0060_v59_to_v60.sql`](../../migrations/0060_v59_to_v60.sql)（建表、建索引、`OWNER`/`GRANT` 与写入 `platform.schema_version = 60`）：
  ```sql
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

  DO $$
  BEGIN
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_bootstrap') THEN
          EXECUTE 'ALTER TABLE social.player_stickers OWNER TO daohua_bootstrap';
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_api') THEN
          EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON social.player_stickers TO daohua_api';
      END IF;
      IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_worker') THEN
          EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON social.player_stickers TO daohua_worker';
      END IF;
  END $$;
  ```
- 【离线脚本 / 人工步骤】：无。

## v60 -> v61

- **变更日期**：2026-11-17（America/New_York）
- **关联任务**：`TASK-20261117-14`（审查来源 `TASK-20261117-13` A4、B1、B3：阵盘内置冷却、死信、身份行锁与 worker 权限）
- **数据库版本**：`PostgresSchema.Version` 60 -> 61
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 43 -> 44；`MinReadableSchemaVersion` 保持 41
- **迁移脚本状态**：`migrations/0061_v60_to_v61.sql` 已用独立 PostgreSQL 18（基线 v60 DDL）完成 dry-run、执行和重执行，并核对三处标签与原始载荷、校验和。未在任何现有库运行。
- **存量处理结论**：只推进存档标签，不修改检查点、钱包或校验和，不清库。

### 1. 变了什么

- 【库结构/权限】新增 `game.lock_player_for_section_flush(uuid)`，只锁定指定身份行，不返回身份字段；固定 `search_path`、撤销 `PUBLIC` 执行，向 API/worker 授予执行。worker 另获 outbox 审计所需的指定列插入和 `event_id` 读取权限。身份、命令回执、租约、账本等留存只由 API 执行，不为 worker 扩权。无表或列变化；空库登记 schema v61。
- 【存档载荷】`action_queue` 远征状态及共享战斗检查点的战斗单位快照新增 `lastTianleiTriggerAtSeconds`、`lastXueshaTriggerAtSeconds`，各自使用战斗时钟秒。缺省或 null 表示无触发记录。
- 【配表/持久化语义】不改阵盘触发概率、冷却长度或结算顺序。

### 2. 是否需要处理存量数据

- v43 历史快照没有触发时刻，不能回推迁移瞬间尚未耗尽的内置冷却。此类在途战斗首次从旧快照恢复时仍按旧行为视为未记录；之后新快照保留真实触发时刻。战外存档无行为变化。
- 新字段可缺省解码，不重写 JSON、Brotli 字节或 SHA-256；持久队列与组队会话检查点无单独玩家载荷标签。

### 3. 如果要处理，怎么处理

- 停止所有写者、备份全库后，从 v60 应用独立 SQL；脚本在同一事务内将提交头、落盘分段及全部刷盘收件箱的 v43 标签改为 v44，校验没有其他标签残留后，部署刷盘加锁函数及上述权限再登记 v61；仅为已存在的权限组授权。检测到非预期标签即失败，不盲贴标签。
- 用隔离库验证迁移、重执行和标签零漂移，再由匹配版本的主服/worker/计算节点接管；旧镜像不能直接接管 v61 库。如需回退，须另行批准恢复停写前备份或前向修复，不只回滚镜像。

## v61 -> v62

- **变更日期**：2026-11-18 19:58:00
- **关联任务**：`TASK-20261118-03`（兑换码系统设计与全栈实装）
- **数据库版本**：`PostgresSchema.Version` 61 -> 62
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 未变（保持 `44`）；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0062_v61_to_v62.sql`（纯新增旁路表与角色授权，幂等安全）
- **存量处理结论**：无需处理存量玩家数据，仅建新表、授权 `daohua_api` 并登记 `schema_version = 62`

### 1. 变了什么
- 【库结构/权限】在 `economy` schema 下新增三张旁路表：`economy.redeem_batches`（批次主表，支持 `universal` 通用口令码与 `single_use` 批次单次码）、`economy.redeem_codes`（全服唯一归一化口令码池）、`economy.player_redeem_claims`（每玩家每批次永久防重记录），配套创建批次时间序、按批查码与核销明细索引；仅向 `daohua_api` 授予三张表的 DML 权限及 `redeem_batches_batch_id_seq` 的 `USAGE, SELECT`（独立 `daohua_worker` 不接触兑换码表，保持最小权限）。
- 【存档载荷】无变更（玩家八分段载荷与标签保持 `v44`）。
- 【配表/持久化语义】兑换码入账复用 `economy.transactions`（`transaction_type = 'ops.redeem'`，已被 `PostgresRetentionWorker` 的 `LIKE 'ops.%'` 白名单规则纳入 30 天留存清理），玩家已兑记录 `economy.player_redeem_claims` 永久留存防重复领取。

### 2. 是否需要处理存量数据
- **不需要**：三张新表初始为空，不涉及任何既有玩家八分段、钱包余额或市场挂单的结构改动，在线/离线存量存档零影响。

### 3. 如果要处理，怎么处理
- 【增量迁移脚本 `migrations/0062_v61_to_v62.sql` 要点】：
  ```sql
  CREATE TABLE IF NOT EXISTS economy.redeem_batches (...);
  CREATE TABLE IF NOT EXISTS economy.redeem_codes (...);
  CREATE TABLE IF NOT EXISTS economy.player_redeem_claims (...);
  GRANT SELECT, INSERT, UPDATE, DELETE
      ON economy.redeem_batches, economy.redeem_codes, economy.player_redeem_claims
      TO daohua_api;
  GRANT USAGE, SELECT ON SEQUENCE economy.redeem_batches_batch_id_seq TO daohua_api;
  INSERT INTO platform.schema_version(version, applied_at)
      VALUES (62, now()) ON CONFLICT (version) DO NOTHING;
  ```
- 【离线脚本 / 人工步骤】：无。

## v62 -> v63

- **变更日期**：2026-11-18 16:46:44
- **关联任务**：`TASK-20261118-04`（传音气泡交互与移动端分享）
- **数据库版本**：`PostgresSchema.Version` 62 -> 63
- **存档载荷标签**：保持 `44`，最低可读标签不变。
- **迁移脚本**：`migrations/0063_v62_to_v63.sql`，支持目标版本重试，不依赖运行时迁移器。

### 1. 变了什么

- `social.chat_messages` 增三列可空引用快照（原消息 ID、发送者、原始正文）和默认 0 的 `like_version`。引用不外键关联原消息，因此原消息裁剪不影响已有引用。
- `social.chat_message_likes` 按消息/玩家唯一，只保留当前点赞状态，随原消息级联删除。同一消息的点赞设置在父消息行锁内判定幂等并推进版本，历史在同一查询快照内得到计数、版本和查看者状态。仅为 `daohua_api` 授予新表查询、插入和删除权限。

### 2. 是否需要处理存量数据

- 不需要。旧消息引用为空、点赞为零、版本为 0；不改消息正文、队伍链接、玩家载荷或标签。

### 3. 如果要处理，怎么处理

- 备份后在停写窗口执行独立迁移，确认 `platform.schema_version` 为 63 且三处玩家载荷标签仍为 44，再启动匹配版本。空库由 `PostgresSchema.Sql` 直接初始化为 v63。回退不能只更换旧镜像，须经单独授权恢复备份或前向修复。

## v63 -> v64

- **变更日期**：2026-11-19 02:50:00
- **关联任务**：`TASK-20261119-01`（筑基期专属事件「根基」设计与全栈实装）
- **数据库版本**：`PostgresSchema.Version` 63 -> 64
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` `44 -> 45`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0064_v63_to_v64.sql`（支持目标版本幂等重试）
- **存量处理结论**：仅推进三处存档标签 `44 -> 45` 并登记 `schema_version = 64`，不修改载荷字节或校验和

### 1. 变了什么
- 【库结构/权限】无 DDL、索引或角色权限变更；`PostgresSchema.Sql` 空库初始化登记 `schema_version = 64`。
- 【存档载荷】玩家八分段 `progression`（`PlayerProgressionState`）新增 `genjiPoints: int = 0`（已凝练根基点数）与 `genjiUnlocked: bool = false`（根基常驻解锁标记），均为 additive 可缺省解码字段。
- 【配表/持久化语义】无已持久化配表 ID 删改；已处于 `Lv.31+` 的存量角色加载后由 `GenjiRules.IsUnlocked` 自动判定为已解锁根基，处于 `Lv.30` 满修为的存量角色同样立即可见并可凝练根基。

### 2. 是否需要处理存量数据
- **仅需推进存档标签**：旧 `v44` 载荷缺省反序列化 `genjiPoints = 0, genjiUnlocked = false` 完全符合尚未凝练根基的初始业务语义，无需重写 JSON/Brotli 字节或重算 `head.checksum`（`ComputeHeadChecksum` 不含 `schema_version`）。

### 3. 如果要处理，怎么处理
- 【增量迁移脚本 `migrations/0064_v63_to_v64.sql` 要点】：
  ```sql
  UPDATE game.player_state_heads SET schema_version = 45 WHERE schema_version = 44;
  UPDATE game.player_state_sections SET schema_version = 45 WHERE schema_version = 44;
  UPDATE platform.section_flush_queue SET schema_version = 45 WHERE schema_version = 44;
  -- 校验无非 45 标签残留后写入版本 64
  INSERT INTO platform.schema_version(version, applied_at)
      VALUES (64, now()) ON CONFLICT (version) DO NOTHING;
  ```
- 【离线脚本 / 人工步骤】：无。

## v64 -> v65

- **变更日期**：2026-11-22 01:18:00
- **关联任务**：全仓静态审查性能与索引加固（`P2-2`）
- **数据库版本**：`PostgresSchema.Version` 64 -> 65
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 保持 `45`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0065_v64_to_v65.sql`（支持目标版本幂等重试）
- **存量处理结论**：仅补齐两个幂等部分索引并登记 `schema_version = 65`，无需改写存量行或玩家存档载荷

### 1. 变了什么
- 【库结构/权限】新增 `platform.ix_outbox_delivered`（`platform.integration_outbox(delivered_at) WHERE delivered_at IS NOT NULL`）与 `identity.ix_auth_sessions_account_active`（`identity.auth_sessions(account_id) WHERE revoked_at IS NULL`）；角色权限无变更。
- 【存档载荷】无变更（保持 `v45`）。
- 【配表/持久化语义】无变更。

### 2. 是否需要处理存量数据
- **无需改写业务数据**：索引建立自动覆盖现有数据行，纯增量 DDL。

### 3. 如果要处理，怎么处理
- 【增量迁移脚本 `migrations/0065_v64_to_v65.sql` 要点】：
  ```sql
  CREATE INDEX IF NOT EXISTS ix_outbox_delivered
      ON platform.integration_outbox(delivered_at)
      WHERE delivered_at IS NOT NULL;

  CREATE INDEX IF NOT EXISTS ix_auth_sessions_account_active
      ON identity.auth_sessions(account_id)
      WHERE revoked_at IS NULL;

  INSERT INTO platform.schema_version(version, applied_at)
      VALUES (65, now()) ON CONFLICT (version) DO NOTHING;
  ```
- 【离线脚本 / 人工步骤】：无。

## v65 -> v66

- **变更日期**：2026-11-24 20:12:00
- **关联任务**：`TASK-20261124-02`（道友攻略板块与配装付费查阅系统）
- **数据库版本**：`PostgresSchema.Version` 65 -> 66
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` `45 -> 46`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0066_v65_to_v66.sql`（支持目标版本幂等重试）
- **存量处理结论**：幂等创建 7 张 `social.*` 表并授权 `daohua_api`，推进三处玩家存档标签 `45 -> 46`，登记 `schema_version = 66`

### 1. 变了什么
- 【库结构/权限】在 `social` schema 下新增 7 张表及索引：`social.guide_posts`、`social.guide_daily_quotas`、`social.guide_post_views`、`social.guide_post_likes`、`social.guide_post_unlocks`、`social.guide_post_tips`、`social.loadout_inspect_grants`；在 `deploy/postgres-local-roles.sql` 与迁移脚本中将属主归于 `daohua_bootstrap` 并向 `daohua_api` 授予 `SELECT, INSERT, UPDATE, DELETE`；`PostgresRetentionWorker` 将 `'guide.reward'`、`'privacy.inspect'` 纳入 30 天白名单账本留存，并定期清理超期授权/每日额度/软删除帖子。
- 【存档载荷】玩家八分段 `settings`（`PlayerSettingsState` / `SettingsDocument`）新增 `loadoutInspectPrice: double = 0`（非公开配装时的付费查阅价格，additive 可缺省解码为 `0` 即完全隐藏）。
- 【配表/持久化语义】无配表 ID 删改。

### 2. 是否需要处理存量数据
- **仅需建表授权并推进存档标签**：旧 `v45` 存档载荷不含 `loadoutInspectPrice` 时缺省解码为 `0`，原本公开的角色维持公开，原本隐藏的角色维持完全隐藏，无需重写 JSON/Brotli 载荷或校验和。

### 3. 如果要处理，怎么处理
- 【增量迁移脚本 `migrations/0066_v65_to_v66.sql` 要点】：
  ```sql
  -- 1) CREATE TABLE IF NOT EXISTS social.guide_posts / guide_daily_quotas / guide_post_views /
  --    guide_post_likes / guide_post_unlocks / guide_post_tips / loadout_inspect_grants 及索引；
  -- 2) ALTER TABLE OWNER TO daohua_bootstrap + GRANT SELECT, INSERT, UPDATE, DELETE TO daohua_api；
  -- 3) 推进存档标签：
  UPDATE game.player_state_heads SET schema_version = 46 WHERE schema_version = 45;
  UPDATE game.player_state_sections SET schema_version = 46 WHERE schema_version = 45;
  UPDATE platform.section_flush_queue SET schema_version = 46 WHERE schema_version = 45;
  INSERT INTO platform.schema_version(version, applied_at)
      VALUES (66, now()) ON CONFLICT (version) DO NOTHING;
  ```
- 【离线脚本 / 人工步骤】：无。


## v66 -> v67

- **变更日期**：2026-11-25 17:45:00
- **关联任务**：`TASK-20261125-01`（追忆副本系统全栈实现）
- **数据库版本**：`PostgresSchema.Version` 66 -> 67
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` `46 -> 47`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0067_v66_to_v67.sql`（支持目标版本幂等重试）
- **存量处理结论**：幂等升级 `social.parties`、`social.party_members`、`social.party_seekers` 约束与列，推进三处玩家存档标签 `46 -> 47`，登记 `schema_version = 67`

### 1. 变了什么
- 【库结构/权限】
  - `social.parties`：`activity_kind` CHECK 新增 `'dungeon'`，`difficulty` CHECK 由 `0..5` 放宽为 `0..10`（虚境 3 档 + 现世 8 阶共 11 档），新增列 `dungeon_id varchar(128) NOT NULL DEFAULT ''` 与 `dungeon_repeat_count integer NOT NULL DEFAULT 1 CHECK (dungeon_repeat_count = -1 OR (dungeon_repeat_count >= 1 AND dungeon_repeat_count <= 99))`；
  - `social.party_members`：新增列 `assist_mode boolean NOT NULL DEFAULT false`（追忆副本助战标记）；
  - `social.party_seekers`：`activity_kind` CHECK 新增 `'dungeon'`。角色权限无变更（既有表授权自动覆盖新列）。
- 【存档载荷】
  - `progression`（`PlayerProgressionState`）新增 `tiliValue: int = 0`、`tiliUpdatedAtUnixSeconds: double = 0`（均为 additive 可缺省解码；旧存档解码得 `tiliUpdatedAtUnixSeconds = 0` 时，首次查询自动按首日初始额度 `240` 点激活）；
  - `actionQueue`（`CombatExpeditionState`）新增 `dungeonId: string? = null`、`dungeonTotalWaves: int? = null`、`dungeonEnergyCharged: bool = false`（均为 additive 可缺省解码）。
- 【配表/持久化语义】新增 `config/game-data/dungeons/dungeons.json`、4 种秘境专属特殊材料、4 件秘境专属装备与 4 条锻造配方，未删改任何既有配表 ID。

### 2. 是否需要处理存量数据
- **仅需升级表约束/加列并推进存档标签**：旧 `v46` 存档缺失体力锚点时按 `InitialEnergy = 240` 自动初始化，既有行动与队伍数据保持默认语义，无需重写存量 JSON/Brotli 载荷或校验和。

### 3. 如果要处理，怎么处理
- 【增量迁移脚本 `migrations/0067_v66_to_v67.sql` 要点】：
  - 动态移除旧 `social.parties` / `social.party_seekers` 的 `activity_kind`/`difficulty` CHECK 约束并重建新约束，幂等 `ADD COLUMN IF NOT EXISTS` 补齐 `dungeon_id`/`dungeon_repeat_count`/`assist_mode`；
  - 推进 `game.player_state_heads`、`game.player_state_sections`、`platform.section_flush_queue` 的 `schema_version` 由 `46 -> 47`；
  - `INSERT INTO platform.schema_version(version, applied_at) VALUES (67, now()) ON CONFLICT (version) DO NOTHING`。
- 【离线脚本 / 人工步骤】：无。


## v67 -> v68

- **变更日期**：2026-11-25 22:10:00
- **关联任务**：`TASK-20261125-02`（战斗统计新增紧凑 PVP 斗法记录）
- **数据库版本**：`PostgresSchema.Version` 67 -> 68
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` `47 -> 48`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0068_v67_to_v68.sql`（支持目标版本幂等重试）
- **存量处理结论**：无 DDL/权限变更，仅推进三处玩家存档标签 `47 -> 48` 并登记 `schema_version = 68`

### 1. 变了什么
- 【库结构/权限】无。
- 【存档载荷】
  - `duel`（`PlayerBattleReportDocument`）新增 `duelRecords: DuelBattleRecordDocument[]? = null`（当前行动内最近 20 场 PVP 斗法紧凑记录，additive 可缺省解码为 `null`）；
  - `actionQueue`（`DuelPlaybackState`）新增 `viewerRecord: DuelBattleRecordDocument? = null`（进行中斗法视角紧凑记录，additive 可缺省解码为 `null`）。
- 【配表/持久化语义】无。

### 2. 是否需要处理存量数据
- **仅需推进存档标签**：旧 `v47` 存档缺失 `duelRecords` / `viewerRecord` 时缺省解码为 `null`，无需重写存量 JSON/Brotli 载荷或校验和。

### 3. 如果要处理，怎么处理
- 【增量迁移脚本 `migrations/0068_v67_to_v68.sql` 要点】：
  - 推进 `game.player_state_heads`、`game.player_state_sections`、`platform.section_flush_queue` 的 `schema_version` 由 `47 -> 48`；
  - `INSERT INTO platform.schema_version(version, applied_at) VALUES (68, now()) ON CONFLICT (version) DO NOTHING`。
- 【离线脚本 / 人工步骤】：无。


## v68 -> v69

- **变更日期**：2026-11-26 18:43:00
- **关联任务**：`TASK-20261126-07`（符箓与阵法技艺每级提升 1% 对应伤害）
- **数据库版本**：`PostgresSchema.Version` 68 -> 69
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` `48 -> 49`；`MinReadableSchemaVersion` 保持 `41`
- **迁移脚本状态**：已完成 `migrations/0069_v68_to_v69.sql`（支持目标版本幂等重试）
- **存量处理结论**：无 DDL/权限变更，仅推进三处玩家存档标签 `48 -> 49` 并登记 `schema_version = 69`

### 1. 变了什么
- 【库结构/权限】无。
- 【存档载荷】
  - `CombatantStats`（随 `actionQueue` 远征/参与成员与 `duel` 斗法镜像快照序列化）新增 `talismanDamageBonusPoints: double = 0`（符箓伤害增幅点数）与 `formationDamageBonusPoints: double = 0`（阵法伤害增幅点数），均为 additive 可缺省解码字段；在线与补算重算属性时自动由 `loadout.techniqueExperiences` 实时派生。
- 【配表/持久化语义】无配表 ID 删改；`DataTool` 属性点数授权词汇表新增 `talismanDamageBonusPoints`、`formationDamageBonusPoints`。

### 2. 是否需要处理存量数据
- **仅需推进存档标签**：旧 `v48` 存档缺失上述两字段时缺省解码为 `0`，玩家恢复在线或下次属性解析即按既有 `techniqueExperiences` 自动生效，无需重写存量 JSON/Brotli 载荷或校验和。

### 3. 如果要处理，怎么处理
- 【增量迁移脚本 `migrations/0069_v68_to_v69.sql` 要点】：
  - 推进 `game.player_state_heads`、`game.player_state_sections`、`platform.section_flush_queue` 的 `schema_version` 由 `48 -> 49`；
  - `INSERT INTO platform.schema_version(version, applied_at) VALUES (69, now()) ON CONFLICT (version) DO NOTHING`。
- 【离线脚本 / 人工步骤】：无。
