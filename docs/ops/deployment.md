# daohua 生产构建与部署

本文只描述本次 clean-room 重写后的生产基线。旧端口、旧 API、旧共享访问令牌、旧存档和旧数据库对象均不属于部署契约；本地 schema 不一致时直接重新初始化，不运行历史 migration。

## 拓扑与边界

```text
浏览器 -- HTTPS/WebSocket --> Caddy -- 127.0.0.1:15128 --> daohua API
                                                   |
                                                   +--> PostgreSQL 18（唯一 durable 真源）
                                                   +--> ComputeNode（内部 HTTP/Token，冷挂机）
```

API 使用 `daohua_session` HttpOnly Cookie 认证，服务端从会话声明取得 `player_id`；客户端不能在 URL 或请求体中声明另一个玩家取得权限。内测账号密码长度为 6-24 个字符。ComputeNode 只接受 `X-Compute-Token`，不连接生产数据库、不处理账号和市场写入。

目标主机按 `2c4g/40g` 设计：5000+ 账号、100+ 持续连接、500+ 峰值收发和 2000+ 冷挂机补算。容量门禁见 `scripts/verify.sh` 与 `src/LoadTests`。

## 环境与凭据

- Ubuntu Server 24.04 LTS、linux/amd64。
- .NET SDK 10、Node.js 22、npm、Docker Engine/Compose/Buildx。
- 服务器 SSH 别名 `yuohira`，生产目录 `/opt/daohua`，Compose 项目名 `daohua`。
- `/opt/daohua/daohua.env` 只保存 `DB_PASSWORD`、`COMPUTE_TOKEN`、`APP_IMAGE`、`COMPUTE_IMAGE`、`WAL_ARCHIVE_DIR` 等部署变量；不保存玩家 token、快照或日志。`WAL_ARCHIVE_DIR` 默认 `/opt/daohua/wal-archive`，必须位于独立持久磁盘并限制为 root/0600。
- CCR 登录只允许 `docker login --password-stdin`，不得把 auth 配置写入仓库或镜像。

首次克隆：

```bash
git clone --recurse-submodules <仓库地址>
cd daohua
git submodule update --init --recursive
npm --prefix frontend ci
```

## 本地运行与验证

启动 PostgreSQL（只使用显式卷 `daohua_daohua-postgres`）：

```bash
docker compose -f compose.yaml up -d db
```

直接运行 API：

```bash
Storage__ConnectionString='Host=127.0.0.1;Port=5432;Database=daohua;Username=daohua;Password=daohua-local-dev;Maximum Pool Size=32;Timeout=5;Command Timeout=30' \
Storage__EnableSchemaBootstrap=true \
dotnet run --project src/Server/Server.csproj --no-launch-profile
```

API 默认监听 `127.0.0.1:5128`。启动时先完成 schema v1 建立/校验，再初始化 Data Protection key ring；不会在首个请求中触发建表竞态。

启动正式前端：

```bash
npm --prefix frontend run dev
```

Vite 固定监听 `127.0.0.1:15172`，将 `/api`、`/ws` 代理到 API。前端只在 localStorage 保存非敏感账号显示信息和 UI 偏好；快照、WebSocket 帧、日志和消息不落浏览器磁盘。

最小门禁：

```bash
scripts/verify.sh
```

直接源码烟测且没有静态 `wwwroot` 时：

```bash
SMOKE_REQUIRE_STATIC=0 node scripts/smoke-production.mjs http://127.0.0.1:5128
```

容器同构烟测：

```bash
docker compose -f compose.yaml up -d --build
node scripts/smoke-production.mjs http://127.0.0.1:5128
docker compose -f compose.yaml down
```

不要使用 `docker compose down --volumes`，除非明确要删除本地数据库。

## CCR 构建与发布

生产镜像是多阶段 `linux/amd64` 构建，运行层只有 .NET 发布产物和前端 `dist`，固定 UID/GID `10001`、只读根文件系统、`/tmp` tmpfs，不包含源码、SDK、`node_modules`、`data` 或测试产物。

```bash
scripts/verify.sh
scripts/docker-build-prod.sh --push
```

脚本推送不可变 `sha-<commit前12位>` 与移动 `prod` 标签。完成容器验收后可使用：

```bash
scripts/docker-build-prod.sh --push-existing
```

日常发布拒绝 dirty worktree；`--allow-dirty` 只用于首次引导或明确排障。

## 首次部署与更新

```bash
scripts/deploy-prod.sh \
  --image ccr.ccs.tencentyun.com/yuohira/daohua:sha-<commit前12位> \
  --compute-image ccr.ccs.tencentyun.com/yuohira/daohua:compute-sha-<commit前12位>
```

脚本会只读检查远端 Docker/Compose/权限，校验 `DB_PASSWORD` 与 `COMPUTE_TOKEN`，安装 `/opt/daohua/compose.prod.yaml`、备份脚本和 systemd timer，拉取成对镜像，验证 `/healthz`、`/readyz` 后才替换应用。应用端口只绑定 `127.0.0.1:15128`，公网入口由 `/etc/caddy/sites/daohua.caddy` 提供 HTTPS、压缩、安全头和 WebSocket 转发。

部署同时启用 `daohua-db-backup.timer`（每小时逻辑备份）、`daohua-base-backup.timer`（每日物理基线）、`daohua-ledger-archive.timer`（每日流水分区归档）和 `daohua-wal-archive.timer`（每小时 WAL 巡检/轮转）。任一定时器失败都会保留 systemd journal 与原始归档文件，不能静默跳过。

`daohua-ccr-auto-update.timer` 每分钟检查 `prod`：镜像 ID 未变化时不重建；候选镜像先健康预热，失败时保持当前实例并记录失败镜像 ID；成功后优雅替换，在线客户端通过 Cookie 会话和 WebSocket 自动重连恢复。

查看状态：

```bash
ssh yuohira 'sudo systemctl status daohua-ccr-auto-update.timer --no-pager'
ssh yuohira 'sudo docker inspect --format "{{.Config.Image}} {{.Image}} {{.State.Health.Status}}" daohua-app'
ssh yuohira 'curl -fsS http://127.0.0.1:15128/healthz'
ssh yuohira 'curl -fsS http://127.0.0.1:15128/readyz'
```

回滚前先停止自动更新，再部署已经验收过的不可变标签；确认健康后再恢复 timer。不得删除或重建 `daohua_daohua-postgres` 卷。

## 备份、恢复与硬盘控制

当前备份链路同时保留三类证据：`backup.sh` 每小时生成 custom dump（`pg_restore --list` + SHA-256，hourly 24 / daily 7 / weekly 4）；`base-backup.sh` 每日生成带 `backup_manifest` 的物理基线；PostgreSQL `archive_mode=on`、`archive_timeout=300s` 通过 `postgres-wal-archive.sh` 将 WAL 原子写入 `${WAL_ARCHIVE_DIR}`。`wal-archive.sh` 每小时巡检并默认保留 8 天 WAL。备份长期归档应转移到 COS/S3 兼容对象存储，本地只保留恢复窗口内的最小副本。

验证入口为 `/opt/daohua/verify-backup.sh`：它会校验最新 custom dump、物理备份 `backup_manifest`（`pg_verifybackup`）和 WAL 文件权限/完整性。PITR 恢复只能在隔离目录执行：

```bash
sudo /opt/daohua/pitr-restore.sh \
  --base /opt/daohua/backups/base/daohua-postgres-base-<UTC>.tar.gz \
  --target-time 2026-08-28T00:00:00Z \
  --output /tmp/daohua-pitr-<编号> --confirm
```

脚本默认只准备恢复目录并暂停在目标时间；需要临时实例时追加 `--start`（`127.0.0.1:15432`），完成核对后手工 `docker rm -f daohua-pitr-restore`。任何生产恢复都必须单独获得明确许可，恢复前先制作当前状态备份、停止应用并验证目标归档。

市场成交与审计属于长期权威流水，不由在线 retention worker 删除：`market-ledger-archive.sh` 每日把超过 90 天的 `market.trades` 和 `identity.audit_events` 按 UTC 月份在事务内移动到 `market.trades_archive_YYYYMM` / `identity.audit_events_archive_YYYYMM` 分区表；归档分区可独立 `pg_dump --table` 后上传对象存储，再按合规保留期删除。生产 `Storage__AuditRetentionDays=3650` 仅作最终兜底，不能替代分区归档。

服务端每小时执行 `PostgresRetentionWorker`，小批量清理已投递 outbox、过期会话、旧审计事件、已结束冷挂机任务和过期 Data Protection key；每 10 分钟清理过期命令回执。它不会删除余额、订单、成交或未投递 outbox。Docker `json-file` 日志按 Compose 上限轮转，WebSocket/聊天/战斗 Tick 不写数据库。

## 健康与安全边界

- `/healthz` 只反映进程存活；`/readyz` 反映 PostgreSQL、schema 和市场分片租约是否可用，不泄漏连接串、token、堆栈或玩家数据。
- `/api/debug/*`、`/api/metrics` 由生产 Caddy 拦截；内部指标走受控链路。
- CORS 只允许配置的完整来源；正式前端与 API 同源，不使用任意来源加凭据。
- ComputeNode 仅内部网络可达，令牌通过环境变量注入；节点镜像不包含 PostgreSQL 凭据。
- 当前主实例具备单写者玩家运行时和市场分片租约边界，但不宣称跨进程玩家无停机迁移；扩容前必须增加玩家分片路由和连接排空方案。

## 兼容边界

本部署基线是一次性重写后的唯一版本。不存在旧 schema migration、双读双写、legacy DTO、旧 API 别名、旧 WebSocket fallback 或共享 `ACCESS_TOKEN`。schema、协议或部署拓扑改变时，直接同步代码、测试、文档并重新初始化本地数据库。
