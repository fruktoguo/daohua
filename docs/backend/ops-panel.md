# 本地运维面板设计真源（Development-only）

> 本文档是运维面板的唯一设计真源。任何实现与本文档冲突时，先修订本文档对齐设计意图，再改代码。
> 面板历史形态（独立端口 + 嵌入 HTML 单页）已退役，见文末「退役项」。

## 1. 定位与边界

- 面板是**本地开发运维工具**，只在 `Development` 环境存在；生产环境不装配任何面板路由与中间件。
- 面板不是游戏功能的一部分，不进入玩家协议（`src/Protocol`）与领域模型（`src/Domain`）。
- 面板可以执行**运维写操作**（踢下线、强制存档、卸载玩家、计算节点排空），因此必须满足第 3 节的安全模型。
- 面板 UI 文案直接使用中文，不走前端 i18n 词典（面板不面向玩家，不参与多语言发布）。

## 2. 拓扑与访问路径

面板并入主 API 端口，不再独立监听：

| 项 | 值 |
| --- | --- |
| 页面路径 | `/ops`（前端 React 路由，`Development` 构建期门控） |
| API 前缀 | `/ops/api/*`（由主 API 进程在 5128 提供） |
| 装配条件 | `builder.Environment.IsDevelopment()` 且 `Ops:Enabled` |
| 局域网访问 | 沿用主端口绑定；本地默认 `127.0.0.1` 回环，容器/显式 `ASPNETCORE_URLS` 时随主端口暴露 |

访问链路：

```text
浏览器 → http://127.0.0.1:15172/ops        （Vite 提供页面，仅 Development）
              └─ /ops/api/* 经 Vite 代理 → http://127.0.0.1:5128/ops/api/*
生产      → Caddy @private 已拦截 /ops 与 /ops/*，返回 404（纵深防御）
```

**生产不可达的双重保障**：

1. 服务端：非 `Development` 不装配 `/ops` 路由与令牌中间件。
2. 前端：`import.meta.env.DEV` 门控路由注册，生产构建产物中**不含**面板入口，避免 SPA 兜底把面板页面吐出来。
3. 部署：Caddy `@private path ... /ops /ops/*` 拦截。

## 3. 安全模型

### 3.1 只读视图

只读视图不要求令牌：`Development` 装配判定 + 生产三重拦截已构成边界。

### 3.2 写操作：来源校验 + 一次性令牌

- 写操作要求**回环或私有网段来源**（`OpsNetwork.IsPrivateOrLoopback`），公网来源直接 `403`。这是面板并入主端口后的纵深防御。
- 配置项 `Ops:Token`：
  - 已配置：直接使用（本地开发默认值写在 `appsettings.Development.json`，零摩擦）。
  - 未配置：进程启动时生成随机令牌，并在启动日志以 `Warning` 打印一次（仅 `Development`）。
- 写请求必须携带请求头 `X-Ops-Token`；缺失或不匹配返回 `401`，且**不透露**期望值。- 前端把令牌存在 `sessionStorage`，页面顶部提供令牌输入入口；写操作全部经二次确认弹窗。

### 3.3 写操作二次确认

所有写操作在前端必须经过 `ConfirmDialog` 二次确认，并在确认文案中写明作用对象与不可逆性。高危操作（卸载玩家）使用危险样式。

## 4. 服务端 API

### 4.1 只读视图

| 端点 | 内容 | 数据来源 |
| --- | --- | --- |
| `GET /ops/api/overview` | 服务名/版本/环境/进程资源/运行时长/协议与 schema 版本/配表指纹/会话与玩家计数/市场指标 | 现有实现保留 |
| `GET /ops/api/health` | 全量健康检查逐项状态 | `HealthCheckService` |
| `GET /ops/api/players` | 已加载玩家摘要 | `IPlayerRuntime.GetLoadedSnapshotsAsync` |
| `GET /ops/api/players/{playerId}` | 单玩家详情：资产明细、行动队列、战斗/资源状态摘要、最近日志 | 运行时快照 + `GetRecentLogEvents` |
| `GET /ops/api/sessions` | 在线会话列表 | `SessionRegistry` |
| `GET /ops/api/market/book` | 指定品种盘口 | `MarketRuntime.GetBook` |
| `GET /ops/api/market/metrics` | 市场分片、租约就绪、账本计数 | `MarketRuntime.GetMetrics` |
| `GET /ops/api/market/orders` | 指定玩家挂单列表 | `MarketRuntime.GetOrders` |
| `GET /ops/api/market/catalog` | 可挂售物品目录（id/名称/主类型/子类型/回收价）与品类列表，口径 = 非货币 + 可交易 + `sellPrice > 0` | `GameDataCatalog` |
| `GET /ops/api/compute` | 计算节点组、链路计数、任务积压 | `ComputeLinkRegistry` / `ComputeLinkMetrics` / `PostgresComputeJobRepository` |
| `GET /ops/api/storage` | durable outbox 状态分布与积压、玩家 section 版本分布、核心表体量、留存清理计数 | 新增仓储只读查询 |
| `GET /ops/api/scheduler` | 推进边界、玩家推进延迟分布、行动队列长度分布、待触发队列规模 | 运行时快照聚合 |
| `GET /ops/api/config` | 配表版本/指纹、协议版本、schema 版本、脱敏运行时配置摘要 | `GameDataCatalog` / `ServerOptions`（**绝不输出连接串与令牌**） |
| `GET /ops/api/logs` | 日志环形缓冲，支持 `level` / `category` / `q`（关键词）/ `limit` | `OpsLogBuffer`（扩展过滤） |

### 4.2 写操作

| 端点 | 作用 | 实现路径 |
| --- | --- | --- |
| `POST /ops/api/actions/kick` | 踢下线指定玩家会话 | `SessionRegistry` 取出会话并请求关闭（带关闭码与原因） |
| `POST /ops/api/actions/flush` | 强制存档（单玩家或全部） | `IPlayerRuntime.FlushAsync` / `FlushAllAsync(force: true)` |
| `POST /ops/api/actions/unload` | 卸载玩家运行时（不落盘） | `IPlayerRuntime.RemoveAsync(flush: false)` |
| `POST /ops/api/actions/compute-drain` | 排空计算节点（单个或全部） | `ComputeLinkRegistry.BeginDrain` / `DrainAll` |
| `POST /ops/api/actions/market-system-orders` | 以系统身份批量挂售/收购（单个物品 / 按品类 / 全部物品） | 资金注入（`IEconomyTransactionStore`，`system-grant`）→ `MarketRuntime.PlaceAsync` 正常下单链路，即时上架 |
| `POST /ops/api/actions/market-system-cancel` | 撤销系统挂单（指定品种或全部） | `MarketRuntime.GetOrders(系统玩家)` → 逐单 `CancelAsync` |

约束：

- 写操作只经 `IPlayerRuntime` / `SessionRegistry` / `ComputeLinkRegistry` / `MarketRuntime` 的既有边界调用，不新增底层能力。
- 写操作响应必须回执结果与影响对象，禁止静默吞异常。

### 4.3 系统身份市场下单契约

- 系统身份与 `scripts/seed-market-system-listings.sh` 共用固定 UUID（账号 `f0000000-0000-4000-8000-000000000001`、
  玩家 `f0000000-0000-4000-8000-000000000002`，道号「系统坊市」，随机口令不可登录）；下单前以
  `INSERT ... ON CONFLICT DO NOTHING` 确保身份行存在。
- 下单资金链：每单先注入所需资金（卖单注入物品份数、买单注入 `价格 × 数量` 灵石，`system-grant` 流水），
  再走 `MarketRuntime.PlaceAsync`——其持久化事务自带冻结扣减、冻结预留、经济流水与即时撮合，
  余额不足会快速失败，因此注入必须先行。
- 幂等口径：订单幂等键 `system-ops:{batchId}:{side}:{assetId}`（batchId 为每次请求服务端生成），
  撤单幂等键沿用仓储规范格式 `market-cancel:{系统玩家}:{orderId}`（`CloseOrderAsync` 强制校验）；
  逐物品结果如实回执，部分失败不回滚已成功项。
- 默认值：价格默认回收价（`sellPrice`），数量默认 1 亿份；两者均可显式指定。
  「全部物品」与种子脚本同口径 = `sellPrice > 0` 的非货币可交易物品。

## 5. 前端承载与信息架构

### 5.1 承载方式

- `frontend/src/main.tsx` 按 `location.pathname` 分流：`/ops` 前缀渲染 `OpsApp`，其余渲染游戏 `App`。
- 面板入口组件 `frontend/src/ops/OpsApp.tsx`，仅在 `import.meta.env.DEV` 下可被解析到（生产构建不注册）。
- 数据访问复用 `network/apiClient.ts` 的 `requestJson` 口径，不另起一套请求栈。

### 5.2 信息架构（重设计）

- **顶部状态条**：服务名、环境、版本、运行时长、会话/玩家计数、令牌状态、自动刷新开关。
- **左侧竖排分类导航**（对齐游戏内面板导航观感）：

| 分组 | 视图 |
| --- | --- |
| 总览 | 概览、健康 |
| 运行 | 调度、计算、日志 |
| 数据 | 玩家、会话、存储 |
| 交易 | 市场盘口、市场指标与挂单、系统下单 |
| 系统 | 配置、运维操作 |

- **内容区**：分组卡片 + 数据表格；表格沿用游戏前端的表格与卡片视觉。

### 5.3 样式与组件复用约定

- 面板**复用游戏全局样式**（`frontend/src/styles.css` 及 `styles/*.css`），不新增独立配色体系；旧面板的灰蓝「程序员控制台」配色随旧页面一并退役。
- 必须复用公共组件：`ConfirmDialog`（二次确认）、`Modal`（详情弹层）、`FloatingPopover`（悬浮说明）、`PillLabel`/`ValueText`（数值与状态胶囊）、`UnitNumber`（大数）。
- 新增的面板专用样式只允许补充**游戏主题之外的结构类**（导航布局、卡片网格），颜色与控件外观一律取自既有主题变量（`--gold`/`--bg`/`--text` 等）。
- 新增可复用组件必须登记到 `docs/frontend/common-components.md`。

## 6. 与 dev.sh 的契约

- `dev.sh` 不再检查独立运维端口；改为在后端就绪后探测 `http://127.0.0.1:5128/ops/api/overview`，失败则显式告警（不阻断启动）。
- 启动横幅改为打印 `运维面板: http://127.0.0.1:15172/ops`（页面由前端 dev server 提供）。

## 7. 退役项

以下旧形态在本阶段直接废弃，不保留兼容路径：

| 退役项 | 说明 |
| --- | --- |
| `Ops:Port` / `Ops:ListenAddress` | 独立运维端口不再存在 |
| `OpsPanel.UseOpsGate` 端口门禁 | 并入主端口后无「非面板端口」概念，改为环境装配判定 |
| `src/Server/Clean/Ops/OpsPage.html` | 嵌入 HTML 单页退役，页面改由前端 React 承载 |
| `OpsPanel` 只读端点中的 `Server.Ops.OpsPage.html` 嵌入资源装配 | 随页面退役 |
| 灰蓝控制台配色 | 统一改用游戏黑金主题 |

## 8. 验证要求

- `dotnet build`（Release）与 `tests/Server.Tests` 通过；面板新增端点须有边界测试（令牌校验、生产不装配）。
- 前端 `tsc -b` + `vite build` 通过；`scripts/check-frontend-contracts.mjs` 通过。
- 手工验证：
  - `http://127.0.0.1:15172/ops` 可打开面板且为游戏黑金主题；
  - 无令牌的写操作返回 401，带正确令牌的写操作生效；
  - 生产构建产物中不存在 `/ops` 面板入口。
