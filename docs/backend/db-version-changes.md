# 数据库与存档版本变更台账（`vX -> vY`）

本文档是《道化太初》持久化演进的**唯一版本变更登记台账**。自 `v55` 起，**日常开发不再强制手写单次 `migrations/NNNN_vX_to_vY.sql` 过渡脚本**；所有可能影响存量数据库、玩家存档或旧资产加载的改动，一律在本文件按 `## vX -> vY` 顺序滚动登记，供 `prod-release` 发布前统一评估并编写合并迁移脚本或执行生产人工迁移。

> 当前线上参考水位（以 `TASK-20261114-23` 发布为准）：`yuohira` 正式服数据库 `platform.schema_version = 54`、玩家八分段存档 `SectionCodec = 43`（`MinReadableSchemaVersion = 41`）。

---

## 一、强制登记触发条件（全部纳入）

凡代码或配置涉及以下**任意一类**改动，必须将 `PostgresSchema.Version` 单调 `+1`（全局统一版本号），同步更新空库权威脚本 `PostgresSchema.Sql`（如有 DDL 变动），并在本文档末尾追加一节 `## vX -> vY`：

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

## 二、日常开发与发版迁移分工

1. **日常开发期**：
   - 只改 `src/Server/Clean/PostgresSchema.cs`（`Version` 与 `Sql`）、`src/Domain/Persistence/SectionCodec.cs`、`config/game-data/**` 等源码与配表，并在**本文档**完整填报 `## vX -> vY` 变更条目。
   - **不强制手写 `migrations/NNNN_vX_to_vY.sql`**；本地开发库若版本落后且无过渡脚本，按「兼容清零」默认路径由 `scripts/reset-outdated-players.sh` 控制台输入 `RESET` 重建玩家域数据（保留账号）。
   - 如个别高危结构改造确需本地保留数据调试或提前验证复杂转换逻辑，允许自愿提前在 `migrations/` 编写脚本并在对应条目中标注「已有脚本」。
2. **正式服发布期（`prod-release`）**：
   - 发布前在 **阶段 0** 核对生产库实际 `platform.schema_version`（记为 `v_prod`）与代码目标 `PostgresSchema.Version`（记为 `v_target`），**通读本文档 `[v_prod -> ... -> v_target]` 全部章节**并与 `git diff` 交叉校验。
   - **两条收口路线**（二选一，均须先获用户确认）：
     - **路线 A · 统一编写合并迁移脚本**：针对尚未被 `migrations/` 连续覆盖的跨度 `[covered_ver -> target_ver]`，一次性编写单个合并事务脚本 `migrations/NNNN_v{covered_ver}_to_v{target_ver}.sql`（汇总全部 DDL、权限 `GRANT`、回填、配表 ID 替换与存档标签推进），同步更新 [`migrations/README.md`](../../migrations/README.md) 并提交后再构建推送，交由 `deploy-prod.sh` 自动闸门执行。
     - **路线 B · prod 时手动迁移**：对涉及真实资产退款/补偿、需离线重写 Brotli JSON 载荷重算 SHA-256、或需交互式核对的复杂配表删项，分步完成镜像推送与全库备份后，在阶段 2.5 停写窗口手工执行文档指引的 SQL / `.mjs` 脚本并验收版本与标签零不一致，再执行 `deploy-prod.sh`。

---

## 三、变更条目标准模板（复制使用）

```markdown
## v{from} -> v{to}

- **变更日期**：YYYY-MM-DD HH:mm:ss
- **关联任务**：`TASK-YYYYMMDD-XX`（简述）
- **数据库版本**：`PostgresSchema.Version` {from} -> {to}
- **存档载荷标签**：`SectionCodec.CurrentSchemaVersion` 未变（保持 {S}） / {S1} -> {S2}；`MinReadableSchemaVersion` 保持 {M} / 升至 {S2}
- **迁移脚本状态**：暂未编写（留待 `prod-release` 统一合写/手迁） / 已有 `migrations/NNNN_v{from}_to_v{to}.sql`
- **存量处理结论**：无需额外处理 / 仅推进版本号或存档标签 / 需执行 SQL 回填或清理 / 需离线重写存档载荷或人工介入

### 1. 变了什么
- 【库结构/权限】……（写明具体 schema.table.column、约束、索引、role GRANT）
- 【存档载荷】……（写明分段名、字段名、JSON 默认值兼容性；未变则写「无」）
- 【配表/持久化语义】……（写明删改/重命名的 ID 映射表，如 `old.id -> new.id`；未变则写「无」）

### 2. 是否需要处理存量数据
- 说明原因：……（例如：新字段可缺省解码为默认语义 / `NOT NULL` 无默认值必须回填 / 已删配表 ID 会导致 `DomainException` 需替换或回收……）

### 3. 如果要处理，怎么处理
- 【合入合并迁移脚本的 SQL 指引】（或人工停服执行的明确语句与校验 SQL）：
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

