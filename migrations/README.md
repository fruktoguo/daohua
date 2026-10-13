# migrations/ · 兼容性迁移目录

本目录统一收拢**可选的** PostgreSQL 兼容性迁移脚本，供既有库就地升级使用。

## 硬约束（可整体删除）

- 本目录**随时可整体删除**，不产生任何影响：不参与任何项目编译、不被运行时装配或启动门禁引用、不进入镜像构建上下文。
- 空库启动永远由权威 `src/Server/Clean/PostgresSchema.cs` 的 `PostgresSchema.Sql` 直建当前版本，不经过本目录。
- 代码中禁止实现 migration runner，禁止对 本目录的运行时依赖；本地开发启动链（`reset-outdated-players.sh` 经 `dev.sh`）可对本地回环库自动执行，其余场景由人工或运维流程显式执行（如 `psql -f` 或 `docker exec ... psql -f`）。
- 若删除本目录且库版本落后：启动门禁照旧报 schema 版本不匹配，由使用者选择重建库（本地开发默认路径）。

## 本地库升级入口

本地开发库升级请用 `scripts/migrate-local-db.sh`（本地启动链自动调用，也可人工显式执行；运行时与门禁都不引用它）：
它读取 `platform.schema_version`，按序号链式执行本目录中落后的脚本，最后与 `PostgresSchema.Version` 对账。

```bash
scripts/migrate-local-db.sh            # 就地升级本地库
scripts/migrate-local-db.sh --dry-run  # 只打印将要执行的迁移
```

## 命名与书写规约

- 文件名：`NNNN_vX_to_vY.sql`，`NNNN` 为递增序号，`X`/`Y` 为 `platform.schema_version` 版本号（本地库内容版本，涵盖表结构与存档载荷标签）。
- 存档版本联动：`SectionCodec.CurrentSchemaVersion` 升级时 `PostgresSchema.Version` 同步 +1，并在对应版本迁移文件里内嵌 `UPDATE game.player_state_heads / player_state_sections / platform.section_flush_queue SET schema_version` 标签推进。
- 存档标签迁移成立的前提：`schema_version` 不参与任何校验和计算，且 PersistenceJson 反序列化对新增字段的缺失属性按类型默认值处理；若新版本变更了既有字段语义或默认值不再是正确初值，该版本不写迁移，旧存档走 `reset-outdated-players.sh` 的交互确认清零。
- 脚本必须自带事务边界（`BEGIN;`/`COMMIT;`），末尾将 `platform.schema_version` 写入目标版本。
- 存量行回填必须给出确定值并满足目标版本的 CHECK/NOT NULL 约束。
- 幂等优先：`ADD COLUMN IF NOT EXISTS`、`CREATE INDEX IF NOT EXISTS` 等；存档标签的 `UPDATE ... WHERE schema_version = X` 天然幂等。
- 本地开发/内测阶段仍以重建库为默认路径；本目录只是「想保住本地数据时」的可选通道。

## 现有迁移

下表按仓库实际文件列出全部迁移；当前目录有一个**文件序号**缺口（没有 `0009_*.sql`），但版本链没有缺口：`0008_v8_to_v9.sql` 后接实际存在的 `0010_v9_to_v10.sql`。不得按文件序号猜测迁移，必须按文件名中的 `vX_to_vY` 与 `platform.schema_version` 对账。若目标库版本不在下列链路中、链路执行失败，或 v20→v21 旧玩家载荷无法迁移，必须重建/清零，不得假装可升级。

| 文件 | 覆盖范围 |
| :--- | :--- |
| `0001_v1_to_v2.sql` | `market.orders` 增加到期时间与过期索引，schema_version 写入 2 |
| `0002_v2_to_v3.sql` | `platform.compute_jobs` 增加租约字段与未完结任务唯一索引，schema_version 写入 3 |
| `0003_v3_to_v4.sql` | 新增 `social` schema 及好友、队伍、邀请、传音表，schema_version 写入 4 |
| `0004_v4_to_v5.sql` | `identity.players` 增加 `player_seq`；推进 settings/ActionIntent 存档标签至 5 |
| `0005_v5_to_v6.sql` | 推进 loadout、ActionIntent、LoadoutDocument 存档标签至 6 |
| `0006_v6_to_v7.sql` | 扩展队伍档案、散人池和聊天队伍链接字段，schema_version 写入 7 |
| `0007_v7_to_v8.sql` | 推进 settings `queuePaused` 存档标签至 8 |
| `0008_v8_to_v9.sql` | 推进 progression `traitIds` 存档标签至 9 |
| `0010_v9_to_v10.sql` | 新增账号风控信号/事件表与索引，schema_version 写入 10 |
| `0011_v10_to_v11.sql` | 修复账号风控事件约束，schema_version 写入 11 |
| `0012_v11_to_v12.sql` | 推进 loadout `abilityTriggers` 存档标签至 12 |
| `0013_v12_to_v13.sql` | 新增邀请码与玩家邀请关系，schema_version 写入 13 |
| `0014_v13_to_v14.sql` | 推进 settings `tutorialStep` 存档标签至 14 |
| `0015_v14_to_v15.sql` | 推进 settings `tutorialRewardsStep` 存档标签至 15 |
| `0016_v15_to_v16.sql` | 推进 actionQueue `isPinned` 存档标签至 16 |
| `0017_v16_to_v17.sql` | 推进 world `nodeBuildingUsages` 存档标签至 17 |
| `0018_v17_to_v18.sql` | 推进战斗属性点数通道与 `criticalDamagePoints` 存档标签至 18 |
| `0019_v18_to_v19.sql` | 新增组队讨伐持久化字段与会话表，schema_version 写入 19 |
| `0020_v19_to_v20.sql` | 推进 actionQueue 组队讨伐参与载荷标签至 20 |
| `0021_v20_to_v21.sql` | 仅推进库级版本至 21；旧 partySession/party.expedition 玩家载荷不能无损迁移，须由 `reset-outdated-players.sh` 交互确认清零 |
| `0022_v21_to_v22.sql` | 推进 duel 分段行动账本 `combatants` 战斗单位统计标签至 22（可缺省解码，表结构未变） |
| `0023_v22_to_v23.sql` | 推进 actionQueue 装配展示载荷标签至 23：参与成员快照增 `equipment`、斗法回放增 `opponentSkills`/`opponentEquipment`（可缺省解码，表结构未变） |
| `0024_v23_to_v24.sql` | 组队讨伐会话唯一权威运行态与成员结算收件箱（`social.party_sessions`/`party_session_inbox`），schema_version 写入 24 |
| `0025_v24_to_v25.sql` | `economy.transactions` 增 (created_at, transaction_type) 时间窗索引，schema_version 写入 25 |
| `0026_v25_to_v26.sql` | `market.trades` 增 (buyer_id, traded_at)/(seller_id, traded_at) 复合索引，schema_version 写入 26 |
| `0027_v26_to_v27.sql` | `section_flush_queue` 改追加式收件箱（queue_id 自增主键），schema_version 写入 27 |
| `0028_v27_to_v28.sql` | 新增 `market.settlement_cursors` 每玩家结算游标，schema_version 写入 28 |
| `0029_v28_to_v29.sql` | `social.parties` 增 `pvp_enabled` 队伍斗法开关（§7.10.4），schema_version 写入 29 |
| `0030_v29_to_v30.sql` | 推进战内 Buff 跨波携带态 `combatBuffs`、组队讨伐参与载荷与阵法全队标记存档标签至 30（可缺省解码，表结构未变） |
| `0031_v30_to_v31.sql` | `game.player_projection` 增 `offline_since` 离线锚点投影列（存量离线行按提交头补锚），settings 分段增 `offlineSinceAt`（可缺省解码），schema_version 写入 31 |
| `0032_v31_to_v32.sql` | world 分段建筑效果增 `monsterWeakenRatio`（封魔阵怪物削弱，可缺省解码）、组队会话规格增 `MonsterWeakenRatio`，表结构未变，schema_version 写入 32 |

> 说明：节点公共建筑表 `game.node_buildings` 属于 v3 权威 DDL 的一部分，由 `PostgresSchema.Sql` 的 `CREATE TABLE IF NOT EXISTS` 在启用 schema bootstrap 的进程启动时自动补齐，因此不单独提供迁移脚本。仅在既有 v3 库且**全部**进程都关闭 schema bootstrap 时，才需要人工执行等价 `CREATE TABLE IF NOT EXISTS`（结构见 `src/Server/Clean/PostgresSchema.cs`）。
