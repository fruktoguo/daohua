# 道化太初

这是《道化太初》的内部开发基线。当前版本按 clean-room 方式从零建立领域、协议、运行时、PostgreSQL schema、前端连接层和部署拓扑；旧存档、旧 API、旧 WebSocket 包、旧表和旧组件模型不属于本版本的运行约束，也没有历史兼容层。

## 最终架构

```text
浏览器
  └─ 同源 HTTPS / DHW1 WebSocket
       └─ daohua API（ASP.NET Core 10）
            ├─ 玩家单写者内存聚合
            ├─ WebSocket 状态投影
            ├─ 资产与市场 PostgreSQL 事务
            ├─ latest-only section 刷盘 Worker
            └─ 可选 ComputeNode 客户端（冷挂机补算）
                 └─ PostgreSQL 18（唯一 durable 真源）
```

核心原则：

- 玩家在线时由单写者持有完整领域聚合；数据库不是每个字段一次一行，而是共享表中的少量稳定 section。
- 玩家状态分为 `progression`、`world`、`combat`、`buffs`、`loadout`、`settings`、`action_queue`、`duel` 八个 section。每个玩家对应共享表中的八行最新值，不建立“每个玩家一张表”。`action_queue` 中的讨伐行动保存搜寻/战斗阶段和确定性战斗标量，`durationSeconds=0` 仅表示由战斗状态机推进，不会被普通计时器丢弃。
- 普通模拟只在内存推进，脏 section 以 15 秒合并窗口写入 `platform.section_flush_queue`；同一玩家/section 永远只有一条待写记录，不保存逐 Tick 历史。
- 资产余额、唯一物品、订单冻结、成交、退款和幂等事务由 PostgreSQL 关系模型负责，并在一个事务内提交。
- WebSocket 帧、心跳、战斗伤害明细和聊天消息不落盘。前端日志只有当前页面内的 100 条内存环形缓冲。
- Redis 不在基线中。选型依据和未来准入门槛见 [`docs/backend/commercial-persistence-architecture.md`](docs/backend/commercial-persistence-architecture.md)。

目标主机按 `2c4g/40g` 预算设计：5000+ 注册账号、100+ 持续网络收发、500+ 峰值收发和 2000+ 冷挂机补算。容量目标是门禁，不是未经实测的性能承诺。

## 目录边界

- `src/Domain`：纯领域模型、确定性模拟、战斗、世界和市场订单簿；不依赖 ASP.NET Core、数据库或浏览器。
- `src/Protocol`：HTTP/WebSocket/ComputeNode 共用的纯数据协议；不暴露领域聚合、运行时所有权或数据库对象。
- `src/Server`：ASP.NET Core 宿主、Cookie 会话、WebSocket、PostgreSQL 适配、市场写者和后台 Worker。
- `src/ComputeNode`：无状态的高延迟冷挂机批处理节点；没有生产数据库权限。
- `src/LoadTests`：独立容量与跨边界负载工具，不作为生产依赖。
- `src/DataTool`：游戏数据 JSON 校验与指纹工具。
- `frontend`：唯一正式 React/Vite 前端。
- `tests/Domain.Tests`、`tests/Server.Tests`：领域、协议、编解码和运行时回归门禁。
- `config/game-data`：版本化游戏数据；编译期内嵌为程序集资源，运行时经 `GameDataCatalog.LoadEmbedded` 加载并校验指纹。
- `tools/`：本地开发辅助工具（`config-editor` 可视化配表编辑器、`combat-simulator.html`）。
- `art/`：美术与图像资源源文件。
- `deploy/`：生产部署资产（`compose.prod.yaml`、systemd 单元、备份/归档/回滚脚本、Caddy 站点配置）。
- `data`：运行数据目录（如本地工具需要），不进 Git、不进镜像。
- `vendor/YuoCore`：固定版本 submodule；基座引用保留待用，本任务不修改其内容。

## 本地运行

环境要求：.NET SDK 10、Node.js 22、npm、Docker Engine/Compose/Buildx。首次克隆后执行：

```bash
git submodule update --init --recursive
npm --prefix frontend ci
```

先启动 PostgreSQL 18（本地使用独立命名卷 `daohua-local-postgres18`，原 PostgreSQL 17 卷保留）：

```bash
docker compose -f compose.yaml up -d db
```

本地直接启动 API：

```bash
Storage__ConnectionString='Host=127.0.0.1;Port=5432;Database=daohua;Username=daohua;Password=daohua-local-dev;Maximum Pool Size=32;Timeout=5;Command Timeout=30' \
Storage__EnableSchemaBootstrap=true \
dotnet run --project src/Server/Server.csproj
```

默认监听 `http://127.0.0.1:5128`。首次使用新数据库时由 `PostgresSchema.Sql` 直建 **schema v21**（`PostgresSchema.Version = 21`），启动时校验版本并拒绝旧 schema。本项目不实现运行时 migration runner；既有库的跨版本就地升级由人工显式执行 `migrations/` 目录中的独立 SQL 脚本（当前覆盖 v1→v21，详见 [`migrations/README.md`](migrations/README.md)）。其中 v20→v21 的参与载荷不能无损迁移，旧玩家行必须由 `reset-outdated-players.sh` 交互确认清零；任何版本链缺口或无法迁移的库均不得假装升级，应在确认数据无需保留后重建/清零。本目录可整体删除，全新初始化行为不变。

同一 section schema 下，游戏数据指纹变化不会要求清库：玩家加载时会自动采用当前指纹并异步写回。schema 版本变化通常要求迁移或重新初始化；其中 v20→v21 的旧参与载荷不能无损迁移，必须按迁移说明由 `reset-outdated-players.sh` 交互确认清零。

启动正式前端：

```bash
npm --prefix frontend run dev
```

也可以使用 `scripts/dev.sh` 一键启动前后端。终端中显示分区式 TUI 控制台：顶部显示阶段和运行时间，中部显示后端/前端状态卡，底部显示实时日志。按 `a` 查看全部、`s` 查看启动日志、`b` 查看后端、`f` 查看前端，按 `r` 刷新，按 `q` 或 `Ctrl+C` 停止服务；非交互终端自动退回普通日志输出。

Vite 默认监听 `http://127.0.0.1:15172`，把 `/api`、`/ops/api` 和 `/ws` 代理到 `127.0.0.1:5128`。浏览器认证使用服务端 `HttpOnly` Cookie；前端 localStorage 只保存非敏感的账号显示信息和 UI 偏好，不保存 token、玩家快照、日志或消息历史。

`scripts/dev.sh` 会在前端就绪后额外拉起本地域名入口：Docker 化的 Caddy（`deploy/caddy-local.Caddyfile`）绑定 80 端口，把 **`http://daohua.localhost`** 反代到前端 dev server，本地开发不必再记端口（面板在 `http://daohua.localhost/ops`）。`*.localhost` 由浏览器按 RFC 6761 自动解析到回环地址，无需修改 `/etc/hosts`；容器端口转发由 Docker Desktop 处理，不需要 `sudo`。Docker 不可用或 80 端口被占用时该步骤只提示并跳过，不影响 `127.0.0.1:15172` 直连；设 `DAOHUA_LOCAL_PROXY=0` 可显式关闭。

设置 → 显示支持浅色与深色主题，默认浅色；选择保存在当前浏览器，并在首屏恢复。主题配色统一见 `frontend/src/styles/theme.css`，布局、游戏状态与图像保持原有设计。

## HTTP 与 WebSocket

当前 HTTP 前缀为 `/api/v1`：

- `/api/v1/meta`：协议、schema 和游戏数据指纹。
- `/api/v1/catalog/actions`、`/api/v1/catalog/items`、`/api/v1/catalog/abilities`、`/api/v1/catalog/base-attributes`、`/api/v1/catalog/equipment`、`/api/v1/catalog/maps`、`/api/v1/catalog/encounters`、`/api/v1/catalog/buildings`：带 ETag（游戏数据指纹）的只读目录。
- `/api/v1/auth/register`、`/api/v1/auth/login`、`/api/v1/auth/logout`、`/api/v1/auth/session`：账号和会话；内测密码长度为 6-24 个字符。
- `/api/v1/players/{playerId}`：授权后的完整玩家快照。
- `/api/v1/players/{playerId}/commands`：带 `requestId`、`expectedStateVersion` 的通用命令入口。
- `/api/v1/market/book`、`/api/v1/market/metrics`、`/api/v1/players/{playerId}/market/orders`：市场读模型。
- `POST /api/v1/market/orders`、`DELETE /api/v1/market/orders/{orderId}`：授权后的市场挂单与撤单写入口。

实时通道是固定版本 `DHW1 / protocol.v1` 二进制信封，包含协议版本、消息类型、请求 ID、单调序列号、编码标识和载荷长度。当前浏览器载荷使用 JSON；PostgreSQL section 使用 JSON，载荷超过阈值时使用 Brotli `br-json`。未知版本、编码、长度、序列或文本帧直接拒绝，不尝试解析旧包格式。

## 冷挂机计算节点

`ComputeNode` 只接受带 `X-Compute-Token` 的内部批处理 HTTP 请求，或反向拨入主服的内部 WebSocket 链路；它验证契约版本、配置指纹、任务数量和载荷大小后执行无状态确定性补算。它不接受公网业务流量，不处理账号、在线连接、市场写入，也不持有 PostgreSQL 凭据。令牌只提供节点鉴权，不提供传输加密。远程部署必须使用 `https`/`wss`；生产 Compose 的内部 HTTP/WS 仅在隔离容器网络并显式启用 `AllowInsecureLoopback` 时允许，不能暴露到公网或不可信网络。

```bash
ComputeNode__Token='<内部随机令牌>' \
ComputeNode__Urls=http://127.0.0.1:5129 \
dotnet run --project src/ComputeNode/ComputeNode.csproj
```

正式部署时，ComputeNode 位于隔离网络，反向拨入主服 `/internal/compute-link`；跨主机链路必须使用 `https`/`wss` 或受控加密隧道，并单独验证证书/隧道边界。`compute.v3` 任务冻结同一事务快照中的身份、提交头、全集分段、队列与钱包版本；节点返回候选结果后，主服以独立 ECS 世界重演，逐字节校验状态与资产增量。源基线、钱包、运行时所有权及任务租约全部有效才原子写入可信资产、分段和任务完成；合法上线/源漂移丢弃，不信任节点自报校验和。这里的本地 HTTP 示例不代表生产跨主机传输配置。

## 构建、测试与容量门禁

最小完整门禁：

```bash
dotnet build Game.slnx -c Release --disable-build-servers --nologo
dotnet test tests/Domain.Tests/Domain.Tests.csproj -c Release --no-build --disable-build-servers --nologo
dotnet test tests/Server.Tests/Server.Tests.csproj -c Release --no-build --disable-build-servers --nologo
npm --prefix frontend run build
dotnet run --project src/DataTool/DataTool.csproj -c Release -- validate config/game-data --strict
```

统一入口：

```bash
scripts/verify.sh
```

容量场景：

```bash
dotnet run --project src/LoadTests/LoadTests.csproj -c Release -- \
  --scenario capacity-target --require-database
```
`scripts/capacity-target.sh` 缺省创建随机回环端口的独立 tmpfs PostgreSQL，结束只清理本次容器，不操作已有开发库或数据卷；显式外部库使用 `CAPACITY_REAL_DATABASE_URL` 并提供 `CAPACITY_POSTGRES_CONTAINER` 资源证据。门禁真实运行5000账号存储、100/500认证业务收发与2000名一小时欠债玩家的 ComputeNode → 主服独立重演 → 原子写回。目标集合全部握手、业务回执与 `Applied` 终态、Server/节点/PG资源证据和预算必须同时满足；任一缺失、dead、超时或超预算均非零，`syntheticEstimate` 永不替代。容量门禁不执行远程发布或生产恢复，报告也不是未经实测的生产性能承诺。

压测工具支持的场景：`capacity-target`（容量综合门禁）、`websocket`（真实 WS 连接与协议握手）、`http-read`（只读 HTTP 端点）、`compute`（本地冷挂机补算）、`postgres`（PostgreSQL/WAL 真实探测）。各场景参数语义以 `src/LoadTests/LoadTestOptions.cs` 为准。

单独运行真实 WebSocket 或 HTTP 读压测：

```bash
dotnet run --project src/LoadTests/LoadTests.csproj -c Release -- \
  --scenario websocket --players 100 --duration-seconds 30 \
  --require-server --url ws://127.0.0.1:5128/ws

dotnet run --project src/LoadTests/LoadTests.csproj -c Release -- \
  --scenario http-read --players 500 --duration-seconds 30 \
  --url http://127.0.0.1:5128
```

`websocket` 场景的 `--players` 表示并发连接数，`--concurrency` 只限制同时握手的连接数；`/ws` 端点要求 Cookie 会话，`--require-server` 会先为每个连接注册 `loadtest-ws-*` 压测账号（注册接口限流为每源 IP 12 次/分钟，超出部分自动降级为 `authMode=anonymous` 的 Upgrade 探测）。`http-read` 场景的实际并发由 `--concurrency` 决定，`--players` 仅记录在报告中。

单独运行计算补算或数据库探测：

```bash
dotnet run --project src/LoadTests/LoadTests.csproj -c Release -- \
  --scenario compute --players 2000

dotnet run --project src/LoadTests/LoadTests.csproj -c Release -- \
  --scenario postgres --database 'Host=127.0.0.1;Port=5432;Database=daohua;Username=daohua;Password=daohua-local-dev'
```

`compute` 场景的 `--players` 表示补算玩家数，时间预算按 2000 人/30 秒线性缩放；`postgres` 场景在临时 schema 中探测，数据库不可达即报告失败。上述容量/数据库场景均为本地或受控环境验证，不能替代远程生产容量验收、发布验收或恢复演练。

`scripts/smoke-production.mjs` 验证健康、就绪、同源静态入口（容器模式）和 WebSocket 首帧。直接从源码启动 API 时如果没有 `wwwroot`，可用 `SMOKE_REQUIRE_STATIC=0` 只验证 API/WS 边界。当前工作区未执行远程发布、远程烟测或生产恢复；这些结果不能从本地门禁推断。

```bash
SMOKE_REQUIRE_STATIC=0 node scripts/smoke-production.mjs http://127.0.0.1:5128
```


## 本地三版本基准控制面

`scripts/bench-matrix.sh --manifest <角色清单> --seed-players <清单角色数> --players 10 --hours 1 --scenarios Gathering,Crafting,Combat` 使用同一清单分别重置三套本地实验库并运行真实服务；`--concurrency` 可固定同时执行的玩家命令数。`DAOHUA_BENCH_OBSERVABILITY=false` 可在同负载关闭深度观测，用于判断埋点自身的开销。`--hours 1` 是业务时钟回放一小时，实际墙钟运行时间另见报告。

OOP、ECS-current 和 ECS-optimized 实验共享开发控制面，只在 `Development` API 宿主显式配置 `DAOHUA_BENCH_MODE=replay|online` 与 `DAOHUA_BENCH_VARIANT=oop|ecs-current|ecs-optimized` 时装配。数据库必须是本机对应的 `daohua_bench_oop`、`daohua_bench_ecs_current`、`daohua_bench_ecs_optimized`，请求必须来自本机回环地址；控制面拒绝浏览器 Origin 和转发来源。

- `replay` 必须设置 ISO-8601 `DAOHUA_BENCH_SIMULATION_TARGET`，禁用模拟/玩家周期刷盘与冷挂机后台，由控制端显式推进及 flush，采用确定性模拟事务ID。
- `online` 使用系统业务时钟，禁止设置固定目标；保留在线模拟和周期刷盘，按实际墙钟 duration/concurrency/interval 运行。两种模式均不启用冷挂机派发，租约与会话过期始终使用系统时钟。
- `POST /api/debug/bench/session` 接收 `{ "playerId": "<已种入基准账号的UUID>" }`，签发真实 PostgreSQL session 与 HttpOnly Cookie；正式登录限流保持原值。`simulation-clock`、`flush`、`metrics` 端点要求该正式会话。
- `DAOHUA_BENCH_OBSERVABILITY=true|false` 控制业务 Meter，默认 `true`。监听器在数据库 bootstrap 之前创建。`GET /api/debug/bench/metrics` 返回 `measurements`（阶段与场景键、count/totalMs/固定桶/分位）和 `counters`；Bench将测量窗口桶差与事件差写入报告 `server_measurements` / `server_counters`。
- 现已观测 OOP/ECS 的 `core.simulation`、`snapshot.export`、`queue.actor_wait` 与玩家载入/卸载；ECS 还观测 `ecs.system_tick`、游戏 System 活动调用、真实实体创建/销毁及 `yuocore.*` 内核路径（Fiber 邮箱、World 分派、System 扫描、组件热路径）。报告把内核阶段归一化为 `ecs.core.*`；仅两版真实边界一致的阶段可比较，优化版专属内部步骤单列。样本为实际调用，缺失阶段不填零，累计分位不得相减。请求 RTT 单独报告；在线结束时钟不同，跨库终态严格比较使用独立 replay。
- 单版 `bench run`/`probe` 除 JSON 和总览 CSV 外，写出同名 `.stages.csv` 与 `.counters.csv`：阶段行含层级、状态、调用次数、累计耗时、单次均值及 p50/p95/p99/max；计数行记录邮箱、组件、系统等事件次数。`domain.*` 是 OOP/ECS 共用领域原语，`oop.logic.*`、`ecs.system.run.*` 与 `ecs.core.*` 分别解释实现和内核路径；父子计时和并发累计耗时不能相加为墙钟。`not_observed`、`not_applicable`、`unavailable` 的统计字段留空。
- `dotnet run --project src/Bench -- report --report 'artifacts/bench/<同条件三版报告通配符>.json' --comparison-output artifacts/bench/gathering.comparison.json` 生成阶段与事件计数的横向对比 JSON/CSV，并为旧版输入报告补齐缺失的 `.stages.csv`、`.counters.csv`（不覆盖已有文件）。输入必须是 OOP、ECS-current、ECS-optimized 各一份，且场景、人数、manifest SHA-256、游戏数据指纹、模式、目标时长、并发和成功命令数一致；条件不符仍输出明细，但拒绝计算倍率并返回非零状态。同一 `stage|scenario`、调用边界和调用次数相同的共享阶段可计算三版倍率；两套 ECS 共有阶段只计算优化版/当前版；`all` 运维阶段只并排展示，`count_mismatch` 不计算倍率。
- 报告尚未记录 ECS `shardCount`，计算倍率前需外部核对两套 ECS 服务的实际分片数。`instrumentation_listener_ms` 是服务端 Meter 监听器窗口耗时下界；完整埋点成本要用相同负载的观测开/关独立重复实验确认。

该实验尚未替代正式服务。完整设计、验证记录与剩余门禁见 [`TASK-20260924-09`](docs/tasks/archive/archive-2026-09.md)。

## Docker 与生产部署

生产镜像由多阶段 Dockerfile 构建，只包含发布产物和前端 `dist`，以固定 UID `10001`、只读根文件系统和 `/tmp` tmpfs 运行。根 `compose.yaml` 是本地开发编排（app 以 `Development` 运行），本地容器化验证：

```bash
docker compose -f compose.yaml up -d --build
node scripts/smoke-production.mjs http://127.0.0.1:5128
docker compose -f compose.yaml down
```

生产同构拓扑以 `deploy/compose.prod.yaml` 为准（`Production` 环境、独立 `compute-node` 与 `persistence-worker` 服务、端口 `127.0.0.1:15128`，要求预先构建并按不可变 **digest** 固定的镜像；移动 `prod` 标签不得作为生产运行引用）。

正式 CCR 发布、服务器更新、备份、Caddy、回滚和持久卷规则见 [`docs/ops/deployment.md`](docs/ops/deployment.md)。完整持久化与 Redis ADR 见 [`docs/backend/commercial-persistence-architecture.md`](docs/backend/commercial-persistence-architecture.md)。

生产发布前必须确认：

- 工作区已提交且通过 Release 构建、领域/服务端测试、前端构建和适用的容量门禁；
- Server 与 ComputeNode 使用 `linux/amd64`、不可变 digest 固定的镜像引用（构建时可保留 `sha-<commit>` 标签，但生产 Compose 必须解析并固定 `@sha256:...`），`prod` 仅作移动发布指针；
- PostgreSQL 卷 `daohua_daohua-postgres` 未被替换，备份归档和 SHA-256 校验可恢复；
- Caddy 配置已 `caddy validate`，公网只暴露 HTTPS、HTTP API 和 WebSocket；
- 应用日志、WAL、队列和备份均有硬上限，禁止把 WebSocket/聊天/战斗 Tick 变成持久化日志。

## 开发阶段兼容边界

本版本是内部开发阶段的唯一新基线。不会新增旧数据迁移器、双读双写、旧 DTO、legacy flag、旧字段回填、旧 API 别名或旧协议 fallback。需要改变 schema、快照或部署拓扑时，直接同步代码、测试、文档和全新本地数据库。
