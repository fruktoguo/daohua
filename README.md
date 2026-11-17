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
- 单人高频模拟在内存推进；按 10 分钟检查点或关键生命周期边界，将余额、脏分段及推进时刻原子提交。`platform.section_flush_queue` 是追加式待写队列，不是逐 Tick 历史；未被更新有效状态覆盖的死信保留为恢复证据。
- 资产余额、唯一物品、订单冻结、成交、退款和幂等事务由 PostgreSQL 关系模型负责，并在一个事务内提交。
- WebSocket 帧、心跳和单人战斗 Tick 不持久化；聊天仅保存受限的作用域历史（`social.chat_messages`），不归档传输帧。
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
- `vendor/YuoCore`：固定版本 submodule；生产运行时内核，由 `src/Ecs` 承载游戏实体、系统和模拟。

## 本地运行

环境要求：.NET SDK 10、Node.js 22、npm、Docker Engine/Compose/Buildx。首次克隆后执行：

```bash
git submodule update --init --recursive
npm --prefix frontend ci
```

可选私有扩展模块（反多开等，闭源子系统的兄弟仓库 `DaoHua-Private`）：克隆到本仓库同级目录即自动拼装为完整版（`dotnet build`/`dev.sh`/`verify.sh` 自动探测；前端挂载由 `scripts/sync-private-modules.mjs` 完成），缺失时构建纯开源版。详见 `docs/backend/private-modules.md`。

```bash
git clone <DaoHua-Private 地址> ../DaoHua-Private
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

默认监听 `http://127.0.0.1:5128`。当前源码由 `PostgresSchema.Sql` 为全新库直建 **数据库 v61**，八分段载荷标签为 **v44**，客户端最低读者为 **v41**；三个版本含义不同。运行时不执行历史迁移，也不以旧标签继续加载。已有可保留的数据库按 [`migrations/README.md`](migrations/README.md) 在受控停写窗口迁移；`0061_v60_to_v61.sql` 更新标签、刷盘权限，不改写历史载荷。相同载荷版本下，配表指纹变化不要求清库。

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
scripts/verify.sh --with-postgres -- dotnet test tests/Server.Tests/Server.Tests.csproj -c Release --no-build --disable-build-servers --nologo
npm --prefix frontend run build
dotnet run --project src/DataTool/DataTool.csproj -c Release -- validate config/game-data --strict
```

统一入口：

```bash
scripts/verify.sh              # 日常快速门禁（默认，跳过 Compose 与 PostgresIntegration）
scripts/verify.sh --full       # 全量交付门禁（合并/发布前必跑，含 PG 集成与 Compose）
scripts/verify.sh --backend    # 仅后端面（可叠加 --full）
scripts/verify.sh --frontend   # 仅前端面
```

`--full` 为每轮集成测试自动供给新的回环 PostgreSQL 18，一次性库凭随机库名、独占所有者和服务端标识领取，退出后回收；不复用本地开发库。分级只服务日常迭代，不能替代交付前的全量门禁。

容量场景：

```bash
scripts/capacity-target.sh --link-regression
`scripts/capacity-target.sh --link-regression` 仅在本机运行，创建随机回环端口的独立 tmpfs PostgreSQL，不接受外部数据库、代理或已有服务地址，结束只回收本次容器。它验证 5000 账号存储、100/500 并发认证业务收发，以及 2000 名一小时冷挂机玩家经计算节点回写的整条链路；报告绑定本轮 Server/ComputeNode 产物 SHA-256。任一目标未通过即非零退出；估算不替代实测。固定 `2c4g/40g` 目标另行验收，本地链路绿灯不代表生产硬件达标。

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


## 历史性能基线

OOP、ECS-current、ECS-optimized 三版本对照是历史实验，不是当前生产运行模式。实验条件和报告见 [`TASK-20260924-09`](docs/tasks/archive/archive-2026-09.md)；现在的交付口径为单内核 ECS 与独立容量门禁。

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
- 应用日志、WAL、队列和备份均有硬上限，禁止把 WebSocket 帧或单人战斗 Tick 变成持久化日志。

## 开发阶段兼容边界

本版本是内部开发阶段的唯一新基线。不会新增旧数据迁移器、双读双写、旧 DTO、legacy flag、旧字段回填、旧 API 别名或旧协议 fallback。需要改变 schema、快照或部署拓扑时，直接同步代码、测试、文档和全新本地数据库。

组队讨伐由独立会话权威推进一次，成员只消费个人结算；任一成员在线实时推进，非耐久战斗消耗品按合法边界使用真实可用钱包，缺物不停战、购入后下一合法边界续用。全员离线后将相关可用库存划为不可售储备，按小时批算且本批新购入物品不倒灌已经推演的时间；成员重连或散场时退回未耗储备，并保留持续效果和冷却时钟。会话检查点与结算收件箱落 PostgreSQL，重启从检查点恢复；队伍解散保留未消费结算。规则与验收见 [总纲 §21.13](docs/design/总纲/21-同道与组队系统.md#2113-同图共战行动参与讨伐)。
