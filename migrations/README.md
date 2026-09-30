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

| 文件 | 覆盖范围 |
| :--- | :--- |
| `0001_v1_to_v2.sql` | `market.orders` 增加 `expires_at`（存量回填 `created_at + 30天`）与过期索引，schema_version 写入 2 |
| `0002_v2_to_v3.sql` | `platform.compute_jobs` 增加 `lease_token`/`next_attempt_at`/`last_error` 与单开任务部分唯一索引 `ux_compute_jobs_open`，schema_version 写入 3 |
| `0003_v3_to_v4.sql` | 新增 `social` schema 与 `friend_requests`/`friendships`/`parties`/`party_members`/`party_invitations`/`chat_messages` 六张表（传音频道与好友、组队社交），schema_version 写入 4 |
| `0004_v4_to_v5.sql` | `identity.players` 增加 `player_seq` 自增序号列（存量由序列回填）；并存档载荷 v4→v5：settings 新增 `autoTravelEnabled`、ActionIntent 新增 `anchorNodeId` 均可缺省解码，推进三张存档表的 `schema_version` 标签；platform schema_version 写入 5 |

> 说明：节点公共建筑表 `game.node_buildings` 属于 v3 权威 DDL 的一部分，由 `PostgresSchema.Sql` 的 `CREATE TABLE IF NOT EXISTS` 在启用 schema bootstrap 的进程启动时自动补齐，因此不单独提供迁移脚本。仅在既有 v3 库且**全部**进程都关闭 schema bootstrap 时，才需要人工执行等价 `CREATE TABLE IF NOT EXISTS`（结构见 `src/Server/Clean/PostgresSchema.cs`）。
