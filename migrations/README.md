# migrations/ · 兼容性迁移目录

本目录统一收拢**可选的** PostgreSQL 兼容性迁移脚本，供既有库就地升级使用。

## 硬约束（可整体删除）

- 本目录**随时可整体删除**，不产生任何影响：不参与任何项目编译、不被运行时装配或启动门禁引用、不进入镜像构建上下文。
- 空库启动永远由权威 `src/Server/Clean/PostgresSchema.cs` 的 `PostgresSchema.Sql` 直建当前版本，不经过本目录。
- 代码中禁止实现 migration runner，禁止对 本目录的运行时依赖；迁移只能由人工或运维流程显式执行（如 `psql -f` 或 `docker exec ... psql -f`）。
- 若删除本目录且库版本落后：启动门禁照旧报 schema 版本不匹配，由使用者选择重建库（本地开发默认路径）。

## 命名与书写规约

- 文件名：`NNNN_vX_to_vY.sql`，`NNNN` 为递增序号，`X`/`Y` 为 `platform.schema_version` 版本号。
- 脚本必须自带事务边界（`BEGIN;`/`COMMIT;`），末尾将 `platform.schema_version` 写入目标版本。
- 存量行回填必须给出确定值并满足目标版本的 CHECK/NOT NULL 约束。
- 幂等优先：`ADD COLUMN IF NOT EXISTS`、`CREATE INDEX IF NOT EXISTS` 等。
- 本地开发/内测阶段仍以重建库为默认路径；本目录只是「想保住本地数据时」的可选通道。

## 现有迁移

| 文件 | 覆盖范围 |
| :--- | :--- |
| `0001_v1_to_v2.sql` | `market.orders` 增加 `expires_at`（存量回填 `created_at + 30天`）与过期索引，schema_version 写入 2 |
| `0002_v2_to_v3.sql` | `platform.compute_jobs` 增加 `lease_token`/`next_attempt_at`/`last_error` 与单开任务部分唯一索引 `ux_compute_jobs_open`，schema_version 写入 3 |
