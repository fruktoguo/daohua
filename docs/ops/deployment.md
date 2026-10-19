# daohua 生产构建与部署

本文描述当前生产基线。旧端口、旧 API、旧共享访问令牌、旧存档和旧数据库对象均不属于部署契约；空库启动永远由权威 `PostgresSchema.Sql` 直建当前数据库 schema v37，玩家八分段载荷仍为 v36。本地版本漂移先尝试独立迁移脚本，无法迁移时须交互确认后才允许清零；无 TTY 则中止，不自动删除玩家存档。既有库如需保留数据就地升级，可人工执行仓库根目录 `migrations/` 目录下的独立迁移脚本（命名 `NNNN_vX_to_vY.sql`，详见 `migrations/README.md`）；v36→v37 涉及旧托管退款，须停服备份后显式执行，本地启动链拒绝自动应用。该目录满足可整体删除约束——不参与编译、不被运行时装配或启动门禁引用、不进入镜像构建上下文。

## 拓扑与边界

```text
浏览器 -- HTTPS/WebSocket --> Caddy -- 127.0.0.1:15128 --> daohua API
                                                   |
                                                   +--> PostgreSQL 18（唯一 durable 真源）
                                                   <-- ComputeNode（反向 compute-link / Token，冷挂机）
```

API 使用 `daohua_session` HttpOnly Cookie 认证，服务端从会话声明取得 `player_id`；客户端不能在 URL 或请求体中声明另一个玩家取得权限。内测账号密码长度为 6-24 个字符。ComputeNode 仅接受 `X-Compute-Token` 头（无 `Authorization` 回退）；节点不连接生产数据库、不处理账号和市场写入。

目标主机按 `2c4g/40g` 设计：5000+ 账号、100+ 持续连接、500+ 峰值收发和 2000+ 冷挂机补算。容量门禁见 `scripts/capacity-target.sh` 与 `src/LoadTests`。

API 的 GC 采用渐进式驻留策略（`src/Server/Server.csproj`）：`DynamicAdaptationMode=1` 自适应堆尺寸随真实负载伸缩，`ConserveMemory=7` 偏置小堆段勤收集使堆贴住存活数据，`HeapCount=4` 封顶按核堆数量（大内存开发机上默认十余个堆各自囤段是虚胖主因）。不设 `HeapHardLimit` 硬顶，本地运行与发布镜像使用同一设置；不强制 GC、不改容量采样窗口或通过阈值，实际工作集仍由容量门禁验收（768 MiB 预算不变）。

## 环境与凭据

- Ubuntu Server 24.04 LTS、linux/amd64。
- .NET SDK 10、Node.js 22、npm、Docker Engine/Compose/Buildx。
- 服务器 SSH 别名 `yuohira`，生产目录 `/opt/daohua`，Compose 项目名 `daohua`。
- `/opt/daohua/daohua.env` 保存 `DB_BOOTSTRAP_USER`/`DB_BOOTSTRAP_PASSWORD`（仅初始化与受控备份）、`DB_APP_USER`/`DB_APP_PASSWORD`、`DB_WORKER_USER`/`DB_WORKER_PASSWORD`、`COMPUTE_TOKEN`、`APP_IMAGE`、`COMPUTE_IMAGE`、`WAL_ARCHIVE_DIR` 等部署变量；业务运行账号不得具有超级用户或 bootstrap 权限。不保存玩家 token、快照或日志。`WAL_ARCHIVE_DIR` 默认 `/opt/daohua/wal-archive`；部署脚本以 `install -d -m 0700 -o 70 -g 70` 创建目录，压缩归档权限为 0600。
- CCR 登录只允许 `docker login --password-stdin`，不得把 auth 配置写入仓库或镜像。

首次克隆：

```bash
git clone --recurse-submodules <仓库地址>
cd daohua
git submodule update --init --recursive
npm --prefix frontend ci
```

## 本地运行与验证

启动 PostgreSQL 18（本地使用独立卷 `daohua-local-postgres18`，不修改原卷）：

```bash
docker compose -f compose.yaml up -d db
```

直接运行 API：

```bash
Storage__ConnectionString='Host=127.0.0.1;Port=5432;Database=daohua;Username=daohua;Password=daohua-local-dev;Maximum Pool Size=32;Timeout=5;Command Timeout=30' \
Storage__EnableSchemaBootstrap=true \
dotnet run --project src/Server/Server.csproj --no-launch-profile
```

API 默认监听 `127.0.0.1:5128`。启动时先完成数据库 schema v37 建立/校验，再初始化 Data Protection key ring；不会在首个请求中触发建表竞态。空库由 `PostgresSchema.Sql` 直建，玩家分段载荷标签保持 v36。本地版本漂移由 `scripts/reset-outdated-players.sh` 先调用 `scripts/migrate-local-db.sh`，按 `platform.schema_version` 链式执行可自动应用的独立 SQL；v36→v37 被明确拦截，须停服、全库备份、等待有效租约过期后人工应用。迁移失败直接中止，不进入清零；迁移成功后仍有不可迁移的玩家载荷时，必须在控制台输入 `RESET` 才允许重置，无 TTY 则中止。迁移目录可整体删除，运行时与空库启动无任何依赖。已有远程数据的替换须先备份并单独授权，恢复归档必须使用匹配 schema 的应用版本。

启动正式前端：

```bash
npm --prefix frontend run dev
```

Vite 固定监听 `127.0.0.1:15172`，将 `/api`、`/ws` 代理到 API。前端只在 localStorage 保存非敏感账号显示信息和 UI 偏好；快照、WebSocket 帧、日志和消息不落浏览器磁盘。

最小门禁：

```bash
scripts/verify.sh
```

该入口包含 Release 构建、Domain/ECS/Server 三套测试、配表校验、Compose 配置校验与前端构建。Server 的 PostgreSQL 集成测试使用 `DAOHUA_MARKET_TEST_CONNECTION` 指向的独立测试库：verify.sh 在本地库可达时自动供给 `daohua_market_test` 并导出该变量，也可预设变量指向自己的专用库（须与日常开发库隔离）。容量门禁不在合并必跑内，容量相关变更与发布前执行 `scripts/capacity-target.sh`。

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
  --image ccr.ccs.tencentyun.com/yuohira/daohua@sha256:<Server digest> \
  --compute-image ccr.ccs.tencentyun.com/yuohira/daohua-compute@sha256:<ComputeNode digest>
```

脚本会只读检查远端 Docker/Compose/权限，校验 `DB_BOOTSTRAP_USER`/`DB_BOOTSTRAP_PASSWORD`（仅数据库初始化）、`DB_APP_USER`/`DB_APP_PASSWORD`、`DB_WORKER_USER`/`DB_WORKER_PASSWORD` 与 `COMPUTE_TOKEN`，安装 `/opt/daohua/compose.prod.yaml`、备份脚本和 systemd timer，拉取成对 digest 镜像后调用 `ccr-auto-update.sh`：`compose up --force-recreate` 先替换应用与计算节点容器，再依次等待容器 healthy 与回环 `/readyz`；`/healthz` 由镜像 HEALTHCHECK 与 Compose 健康检查覆盖，`deploy-prod.sh` 只直接 curl `/readyz`。应用端口只绑定 `127.0.0.1:15128`，公网入口由 `/etc/caddy/sites/daohua.caddy` 提供 HTTPS、压缩、安全头和 WebSocket 转发。

部署同时启用 `daohua-db-backup.timer`（每小时逻辑备份）、`daohua-base-backup.timer`（每日物理基线）、`daohua-ledger-archive.timer`（每日流水分区归档）和 `daohua-wal-archive.timer`（每小时 WAL 巡检/轮转）。任一定时器失败都会保留 systemd journal 与原始归档文件，不能静默跳过。

`daohua-ccr-auto-update.timer` 每分钟检查 `prod`：Server 与 ComputeNode 镜像 ID 均未变化时不重建；替换前先把当前运行镜像标记为 `daohua-rollback:app-previous` / `daohua-rollback:compute-previous`，随后 `compose up --force-recreate` 先替换再 `wait_healthy`/`wait_ready`；任一环节失败即经回滚标签恢复上一版本，并把失败的 `app_id|compute_id` 组合 marker 写入 `/opt/daohua/failed-image.id`，CCR 出现新版本前不再重试该候选。成功后在线客户端通过 Cookie 会话和 WebSocket 自动重连恢复。

查看状态：

```bash
ssh yuohira 'sudo systemctl status daohua-ccr-auto-update.timer --no-pager'
ssh yuohira 'sudo docker inspect --format "{{.Config.Image}} {{.Image}} {{.State.Health.Status}}" daohua-app'
ssh yuohira 'curl -fsS http://127.0.0.1:15128/healthz'
ssh yuohira 'curl -fsS http://127.0.0.1:15128/readyz'
```

回滚前先停止自动更新，再部署已经验收过的不可变标签；确认健康后再恢复 timer。不得删除或重建 `daohua_daohua-postgres` 卷。

## 备份、恢复与硬盘控制

当前备份链路同时保留三类证据：`backup.sh` 每小时生成 custom dump（`pg_restore --list` + SHA-256，hourly 24 / daily 7 / weekly 4）；`base-backup.sh` 每日生成带 `backup_manifest` 的物理基线；PostgreSQL `archive_mode=on`、`archive_timeout=300s` 通过 `postgres-wal-archive.sh` 将 WAL 段、时间线历史 `.history` 和备份历史 `.backup` 压缩后原子写入 `${WAL_ARCHIVE_DIR}/<原名>.gz`。重试须确认解压内容一致；PITR 的 `restore_command` 使用 `gzip -dc` 还原原始字节。`wal-archive.sh` 每小时巡检权限与 gzip 完整性并默认保留 8 天归档。禁止手动删除 `pg_wal`：积压必须恢复归档后由 PostgreSQL 自行回收。备份长期归档应转移到 COS/S3 兼容对象存储，本地只保留恢复窗口内的最小副本。

验证入口为 `/opt/daohua/verify-backup.sh`：它会校验最新 custom dump、物理备份 `backup_manifest`（`pg_verifybackup`）和 WAL 文件权限/完整性。追加 `VERIFY_BACKUP_ISOLATED_RESTORE=1` 时启动隔离恢复实例并检查 ready 与最小业务 SQL；否则仅能报告格式/manifest通过。隔离实例使用 `DB_BOOTSTRAP_USER`/`DB_BOOTSTRAP_PASSWORD`，库名与运行中数据库的 `POSTGRES_DB` 一致；恢复载荷挂载于 `/var/lib/postgresql/data` 时必须显式指定同路径的 `PGDATA`，不允许 PostgreSQL 18 默认新建 `18/docker` 空库冒充恢复。PITR 恢复只能在隔离目录执行：

```bash
sudo /opt/daohua/pitr-restore.sh \
  --base /opt/daohua/backups/base/daohua-postgres-base-<UTC>.tar.gz \
  --target-time 2026-08-28T00:00:00Z \
  --output /tmp/daohua-pitr-<编号> --confirm
```

脚本默认只准备恢复目录并暂停在目标时间；需要临时实例时追加 `--start`（`127.0.0.1:15432`），完成核对后手工 `docker rm -f daohua-pitr-restore`。任何生产恢复都必须单独获得明确许可，恢复前先制作当前状态备份、停止应用并验证目标归档。

市场成交与审计属于长期权威流水，不由在线 retention worker 删除：`market-ledger-archive.sh` 每日把超过 90 天的 `market.trades` 和 `identity.audit_events` 按 UTC 月份在事务内移动到 `market.trades_archive_YYYYMM` / `identity.audit_events_archive_YYYYMM` 分区表；归档分区可独立 `pg_dump --table` 后上传对象存储，再按合规保留期删除。生产 `Storage__AuditRetentionDays=3650` 仅作最终兜底，不能替代分区归档。

服务端每小时执行 `PostgresRetentionWorker`，小批量清理七类数据：已投递 outbox、过期会话、旧审计事件、账号检测事件、已结束冷挂机任务、过期 Data Protection key，以及超过 `Storage:SettlementClaimRetentionDays`（默认 30 天）的 `market.settlement_claims`。过期命令回执不属于该 worker，由独立的 `CommandReceiptCleanupWorker` 每 10 分钟清理。两类 worker 都不会删除余额、订单、成交或未投递 outbox。Docker `json-file` 日志按 Compose 上限轮转，WebSocket/聊天/战斗 Tick 不写数据库。

## 健康与安全边界

- `/healthz` 只反映进程存活；`/readyz` 反映 PostgreSQL、schema 和市场分片租约是否可用，不泄漏连接串、token、堆栈或玩家数据。
- `/api/debug/*`、`/api/metrics` 与裸 `/ops`、`/ops/*` 由生产 Caddy 拦截；后台监控面板通过动态随机路径 `/ops-<16位hex>`（未配置 `OPS_PATH` 时每次进程启动自动生成并输出至容器日志）与全量 `X-Ops-Token` 密码门禁（可通过 `OPS_TOKEN` 配置，未配置时启动随机生成）对外提供监控与运维服务，服务端对任何未命中当前激活随机路径的 `/ops*` 请求直接返回 404。
- CORS 只允许配置的完整来源；正式前端与 API 同源，不使用任意来源加凭据。
- ComputeNode 仅内部网络可达，令牌通过环境变量注入；节点镜像不包含 PostgreSQL 凭据。
- 当前主实例具备单写者玩家运行时和市场分片租约边界，但不宣称跨进程玩家无停机迁移；扩容前必须增加玩家分片路由和连接排空方案。计算链路侧已具备优雅排空（compute-link.v1.3 `drain`/`draining` 帧）：主服停机给在飞批次 12 秒收尾窗口并通知节点换上游，节点停机先上报停派再排空在飞——滚动升级/CCR 自动更新不再丢弃正在计算的批次。多主服就绪缺口与分期见 `docs/distributed-readiness.md`。

## 兼容边界

本部署基线是一次性重写后的唯一版本。空库永远由权威 `PostgresSchema.Sql` 直建当前 schema；唯一例外是 `migrations/` 兼容性迁移目录，仅供既有库由人工或运维流程显式执行就地升级，可整体删除且无任何运行时依赖。除此之外不存在双读双写、legacy DTO、旧 API 别名、旧 WebSocket fallback 或共享 `ACCESS_TOKEN`。schema、协议或部署拓扑改变时，直接同步代码、测试、文档并重新初始化本地数据库。

## 本地 PostgreSQL 18 与权限验收

本地 `compose.yaml` 使用 PostgreSQL 18 和 `daohua-local-postgres18` 新卷。旧 `daohua_daohua-postgres` 卷保留，生产卷名不变；切换前先停止本地旧数据库容器，确认没有仍依赖它的开发进程。新卷不恢复历史开发数据，也不添加旧 schema 兼容层。

本地首次以初始化账号启动服务完成 schema 创建后，停止 API，执行 `scripts/init-local-db-roles.sh`。该脚本只能连接固定的 `daohua-local-db`，创建下列不可登录权限组：

| 权限组 | 实际用途 |
| --- | --- |
| `daohua_bootstrap` | schema 和表所有者，仅初始化/结构维护使用 |
| `daohua_api` | API、市场事务与当前内置后台 worker 的 DML，无 schema DDL 权限 |
| `daohua_worker` | section 刷盘、outbox 投递和 retention，不读取账号密码或资产余额 |
| `daohua_archive` | 只读删两个热流水表，在对应 schema 创建归档月分区 |
| `daohua_metrics` | PostgreSQL 统计视图，不授予业务表读取权限 |

用本地管理员通过交互式 `psql` 创建独立登录角色，并 `GRANT daohua_api TO <本地API登录角色>`，密码通过 `\password` 交互设置；worker 等进程按对应组授权。应用连接串使用该登录角色，并设置 `Storage__EnableSchemaBootstrap=false`。不要给运行账号授予 bootstrap 组。现有 Compose 的开发管理员连接仅供初次建库与普通本地开发；权限验收必须使用独立角色覆盖连接串，不能把默认管理员连接当作权限隔离已启用的证据。

该方案未修改生产登录账号、密码、表所有权或部署配置。生产切换须先备份、明确授权，再使用对应登录角色运行及验证。归档组应以自身角色首次创建归档表；已有归档表的所有权处理属于单独受控操作。

`/healthz` 返回 `status`、`service`、`version` 和检查状态；`/readyz` 检查 PostgreSQL 可达性、schema 版本和市场分片租约（API 市场运行时就绪状态）。响应不返回内部异常或连接串。Caddy 模板拦截 `/api/metrics`、`/api/v1/metrics`、`/api/v1/market/metrics`、`/api/debug/*` 和 `/readyz`；接口自身的认证仍保留。

基础属性目录 `GET /api/v1/catalog/base-attributes` 返回当前配表境界锚点，复用目录 ETag 与缓存策略；前端不得自行按生命上限计算回复。

技能目录 `GET /api/v1/catalog/abilities` 返回当前技能配表及前端协议字段；`manaCost` 已按战斗规约换算为实际灵力点数，前端直接显示，不再次乘倍率。默认触发条件与装配协议统一使用 kebab-case。目录包含设计配置，无伤害、治疗等已实现效果的技能不能装配；技能吟唱、冷却与施放计数属于战斗持久化状态。

## 账号检测与反小号（schema v10）

账号检测策略由 `platform.account_risk_policy` 保存，不再读取 `AntiMultiOpen:*` 启动配置。注册、登录和 Hello 使用当前策略；`GET /api/v1/auth/client-policy` 仅返回标签互斥开关，前端每 30 秒更新。注册/登录请求新增 `riskSignals`（设备 ID、配置摘要、网络类型摘要、浏览器指纹），Hello 新增 `deviceId` 与 `deviceConfigHash`。

默认稳定设备只注册一个账号、关联最多两个账号；配置/IP/网段/指纹/成功注册频率参与风险评分。自然人身份无法仅凭浏览器确定。Development 运维面板新增“账号检测”，策略写入仍需令牌、私有来源和确认；生产 Ops 访问边界保持关闭。规则与模块独立关闭语义见 [`anti-multi-open.md`](../backend/anti-multi-open.md)。

数据库备份必须包括 `identity.account_risk_signals`、`identity.account_risk_events`、`platform.account_risk_policy`、`platform.account_risk_key` 与现有 Data Protection key。丢失 HMAC 密钥会破坏历史关联匹配；设备 Cookie 依赖 Data Protection key，二者不得独立清空。风险事件按独立 `Storage:AccountRiskEventRetentionDays`（默认 30 天、范围 1..365）小批量清理，每小时批次为普通清理批次的 16 倍且不超过 10000，稳定关联和风险密钥保留。API/worker 角色授权已同步 `deploy/postgres-local-roles.sql`。本任务未执行任何远端迁移或发布；生产升级 schema v10 须另行授权、先备份并显式应用独立迁移脚本。

## 组队权威会话与恢复（当前数据库 schema v37）

- `social.party_sessions` 同时保存冻结输入、权威战斗检查点、推进时刻与版本；`social.party_session_inbox` 保存各成员待消费结算。两者必须随全库备份/恢复，不可仅恢复玩家分段。
- 检查点与成员结算一次事务交付；成员消费时，资产、分段与消费回执一次事务提交。重启恢复会话，不再无条件标记 `server.restart` 散场。
- 队伍解散不会删除会话或未消费结算；离线成员下次装载先消费，再恢复个人行动。个人 ComputeNode 冷挂机扫描排除活跃会话成员与未消费收件箱。
- 本地 v23→v24 必须先停止全部服务，确认 `game.player_runtime_leases` 无有效租约，再人工执行 `node migrations/0024_v23_to_v24.mjs`；工具通过 `docker exec psql` 在单事务内校验并转换 `game.player_state_sections` 与 `platform.section_flush_queue` 的 Brotli/raw checksum，移除旧 participation 行动、按 Remove 规则激活原队列下一首，并把最后已结算远征血蓝折回 combat（倒地固定 1 血、不回蓝）。它随后 CAS 校验版本/checksum 后执行同一 `0024_v23_to_v24.sql`；失败自动回滚，不会清除角色、队伍、资产或经验。默认容器/用户/库为 `daohua-local-db`/`daohua`/`daohua`，可用 `DB_CONTAINER`、`DB_USER`、`DB_NAME` 覆盖。该 `.mjs` 仅供一次性离线手工运行，不参与启动链、编译或镜像；SQL 单独遇到旧 v23 `action_queue` 会拒绝并提示此路径。
- 检查点提交无法确认时保留会话与版本证据并拒绝继续写；成员资产提交无法确认沿用玩家 fence，不能靠重启或删除收件箱消除错误。
- v36→v37 切换在线钱包/全员离线储备：停止 API、worker、计算节点并完成全库备份，等待玩家、市场和计算任务有效租约过期后，显式执行 `migrations/0037_v36_to_v37.sql`。脚本在同一事务内按最后已提交检查点结束旧活跃会话，把旧 `ConsumableLedger` 按成员/物品归并后的未耗余量（含 durable 旧锁定量）退回普通钱包，同步递增余额版本并写入不可变经济流水；已结束会话的待消费奖励与退款不重复发放。每名已绑定成员追加连续序号的终局 inbox，保留原奖励和消耗品时钟，正常装载时消费并退出旧参与绑定；无检查点的未初始化组织占位行仅标记结束。全部会话检查点转换为新格式，不在运行时保留旧托管兼容层。脚本只推进数据库内容版本至 37，玩家八分段载荷及其 schema 标签仍为 36；整笔失败回滚，v37 重执行不退款。恢复必须同时恢复资产余额、经济流水、会话与 inbox，禁止只回放玩家分段或只回滚脚本。脚本不进入启动链或镜像，实际生产执行仍须另行授权。

## 本地计算节点异地灾备与存储瘦身

云服务器硬件规格为 2C4G/40G，主服磁盘空间受限；而本地计算节点（内网服务器/个人主机）具有大容量磁盘，通过「本地主动拉取 (Pull over SSH/rsync) + 主服热备缩容」实现自动异地灾备：

1. **网络与安全原则**：
   - **出站拉取，零穿透依赖**：本地计算节点位于内网 NAT 后，无需公网 IP 或反向穿透，主动通过 SSH 访问主服 `/opt/daohua/backups/`；
   - **零信任内网**：云端主服不存放任何连入本地内网的凭证，彻底杜绝逆向渗透风险；
   - **不侵入游戏计算链路**：大文件备份通过独立 rsync 传输，不走 `compute-link` WebSocket，避免游戏批次阻塞与主服内存抖动。

2. **计算节点一键部署**：
   在计算节点上执行一键安装脚本：
   ```bash
   sudo bash deploy/install-backup-sync.sh --remote-host yuohira --local-dir /data/daohua-backups --now
   ```
   脚本自动配置 `/etc/daohua-backup-sync.env`、安装 `/opt/daohua/backup-pull-sync.sh` 并激活 systemd timer（每小时第 15 分钟自动拉取，主服整点 :00 生成备份，错峰对账）。

3. **主服存储瘦身配置**：
   主服 `/opt/daohua/daohua.env` 支持环境变量控制留存：
   - `BACKUP_HOURLY_KEEP=6`：主服仅保留最近 6 小时热备；
   - `BACKUP_DAILY_KEEP=3`：主服仅保留最近 3 天天级快照；
   - `BASE_BACKUP_RETENTION_COUNT=1`：主服仅保留最新 1 份物理全备；
   - 超出部分由主服自动老化修剪，本地计算节点不带 `--delete` 增量留存，充当无限期全量历史灾备池。

4. **双层完整性校验**：
   每次拉取后，本地脚本自动扫描提取 `*.sha256` 独立比对，确保传输 100% 完整无损。

5. **方式二：ComputeNode 原生内置备份归档（推荐，免脚本零运维）**：
   计算节点已原生内置 `BackupArchiveWorker` 后台托管服务。任何新计算节点（无论 Windows、Linux、macOS 还是 Android PRoot）无需编写任何脚本或配置 crontab/systemd，只需在启动时配置本地存储路径即可自动启用：
   - **环境变量配置**：
     ```bash
     export DAOHUA_BACKUP_STORAGE_DIR=/data/daohua-backups
     ```
   - **自适应行为**：
     - 自动复用上游 `DAOHUA_COMPUTE_UPSTREAM_URLS` 推导主服 443 HTTPS 安全凭据通道（`https://.../internal/backups/`）；
     - 每小时静默流式下载增量备份，自动执行 SHA-256 全量对账；
     - 状态在 `/healthz` 端点自动暴露（`backupArchive.verifiedCount`、`backupArchive.lastSyncCompletedAt` 等）。
