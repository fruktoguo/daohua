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

默认监听 `http://127.0.0.1:5128`。首次使用新数据库时由 `PostgresSchema.Sql` 直建 **schema v3**（`PostgresSchema.Version = 3`），启动时校验版本并拒绝旧 schema。本项目不实现运行时 migration runner；既有库的跨版本就地升级由人工显式执行 `migrations/` 目录中的独立 SQL 脚本（该目录可整体删除，全新初始化行为不变）。若本地 schema 与当前基线不一致，应在确认数据无需保留后重新初始化本地数据库。

同一 section schema 下，游戏数据指纹变化不会要求清库：玩家加载时会自动采用当前指纹并异步写回；只有 schema 版本变化才需要重新初始化本地数据库。

启动正式前端：

```bash
npm --prefix frontend run dev
```

Vite 默认监听 `http://127.0.0.1:15172`，把 `/api` 和 `/ws` 代理到 `127.0.0.1:5128`。浏览器认证使用服务端 `HttpOnly` Cookie；前端 localStorage 只保存非敏感的账号显示信息和 UI 偏好，不保存 token、玩家快照、日志或消息历史。

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

`ComputeNode` 只接受带 `X-Compute-Token` 的内部批处理 HTTP 请求，验证契约版本、配置指纹、任务数量和载荷大小后执行无状态确定性补算。它不接受公网业务流量，不处理账号、在线连接、市场写入，也不持有 PostgreSQL 凭据。

```bash
ComputeNode__Token='<内部随机令牌>' \
ComputeNode__Urls=http://127.0.0.1:5129 \
dotnet run --project src/ComputeNode/ComputeNode.csproj
```

正式部署时，ComputeNode 应放在独立网络边界；主服务只把可验证的批处理载荷发送给它，结果仍由主服务校验后写回 PostgreSQL。

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

`compute` 场景的 `--players` 表示补算玩家数，时间预算按 2000 人/30 秒线性缩放；`postgres` 场景在临时 schema 中探测，数据库不可达即报告失败。

`scripts/smoke-production.mjs` 验证健康、就绪、同源静态入口（容器模式）和 WebSocket 首帧。直接从源码启动 API 时如果没有 `wwwroot`，可用 `SMOKE_REQUIRE_STATIC=0` 只验证 API/WS 边界：

```bash
SMOKE_REQUIRE_STATIC=0 node scripts/smoke-production.mjs http://127.0.0.1:5128
```

## Docker 与生产部署

生产镜像由多阶段 Dockerfile 构建，只包含发布产物和前端 `dist`，以固定 UID `10001`、只读根文件系统和 `/tmp` tmpfs 运行。根 `compose.yaml` 是本地开发编排（app 以 `Development` 运行），本地容器化验证：

```bash
docker compose -f compose.yaml up -d --build
node scripts/smoke-production.mjs http://127.0.0.1:5128
docker compose -f compose.yaml down
```

生产同构拓扑以 `deploy/compose.prod.yaml` 为准（`Production` 环境、独立 `compute-node` 与 `persistence-worker` 服务、端口 `127.0.0.1:15128`，要求预先构建的不可变镜像）。

正式 CCR 发布、服务器更新、备份、Caddy、回滚和持久卷规则见 [`docs/ops/deployment.md`](docs/ops/deployment.md)。完整持久化与 Redis ADR 见 [`docs/backend/commercial-persistence-architecture.md`](docs/backend/commercial-persistence-architecture.md)。

生产发布前必须确认：

- 工作区已提交且通过 Release 构建、领域/服务端测试、前端构建和容量门禁；
- Server 与 ComputeNode 使用 `linux/amd64` 不可变镜像标签，并保留 `prod` 移动标签；
- PostgreSQL 卷 `daohua_daohua-postgres` 未被替换，备份归档和 SHA-256 校验可恢复；
- Caddy 配置已 `caddy validate`，公网只暴露 HTTPS、HTTP API 和 WebSocket；
- 应用日志、WAL、队列和备份均有硬上限，禁止把 WebSocket/聊天/战斗 Tick 变成持久化日志。

## 开发阶段兼容边界

本版本是内部开发阶段的唯一新基线。不会新增旧数据迁移器、双读双写、旧 DTO、legacy flag、旧字段回填、旧 API 别名或旧协议 fallback。需要改变 schema、快照或部署拓扑时，直接同步代码、测试、文档和全新本地数据库。
