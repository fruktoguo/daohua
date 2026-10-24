# 运维监控面板设计真源

> 本文档是运维面板的唯一设计真源。任何实现与本文档冲突时，先修订本文档对齐设计意图，再改代码。
> 面板历史形态（独立端口 + 嵌入 HTML 单页）已退役，见文末「退役项」。

## 1. 定位与边界

- 面板是**服务端内置的运维监控与管理后台**，同时支持本地开发与正式生产环境（当 `Ops:Enabled=true` 且进程角色为 `Api` 时装配）。
- 面板不是游戏功能的一部分，不进入玩家协议（`src/Protocol`）与领域模型（`src/Domain`）。
- 面板提供实时监控指标、日志、数据查询与受控运维写操作（踢下线、强制存档、卸载玩家、计算节点排空、系统身份市场挂单/撤单、账号检测策略调整），必须满足第 3 节的「随机路径 + 读写全量密码门禁」安全模型。
- 面板 UI 文案直接使用中文，不走前端 i18n 词典（面板不面向玩家，不参与多语言发布）。

## 2. 拓扑与访问路径

面板并入主 API 端口，通过动态随机路径（或显式配置路径）与密码保护对外提供服务：

| 项 | 值 |
| --- | --- |
| 页面路径 | `/ops-<16位随机hex>`（未配置 `Ops:Path` 时每次进程启动动态随机生成）；`Development` 环境额外兼容 `/ops` 别名以支持 `dev.sh` / `dev-tui.mjs` |
| API 前缀 | `{Ops:Path}/api/*`（由主 API 进程提供；`Development` 环境若主路径非 `/ops` 则同步映射 `/ops/api/*`） |
| 装配条件 | `processRole == ServerProcessRole.Api` 且 `Ops:Enabled` |
| 路径规范 | 必须为 `/ops` 或 `/ops-<字母数字_-组合>`（正则 `^/ops(-[a-zA-Z0-9_-]{1,120})?$`） |

访问链路：

```text
本地开发 → http://127.0.0.1:15172/ops（或随机路径 /ops-<hex>）
              └─ /ops(-*)?/api/* 经 Vite 代理 → http://127.0.0.1:5128
正式生产 → https://daohua.yuohira.com/ops-<hex>
              └─ Caddy 反代 → http://127.0.0.1:15128/ops-<hex>
```

**路径隔离与防探测保障**：

1. 部署层：生产 Caddy `@private` 继续拦截裸 `/ops` 与 `/ops/*` 返回 404，而 `/ops-<hex>` 随机路径经反代进入后端。
2. 服务端门禁：服务端对所有 `/ops`、`/ops/...` 与 `/ops-*` 前缀请求执行路径白名单匹配——凡未命中当前进程激活的 Ops 路径，一律直接返回 HTTP 404（禁止落入 SPA `MapFallbackToFile("index.html")` 吐出页面外壳）。
3. 前端懒加载：`main.tsx` 匹配 `^/ops(-[a-zA-Z0-9_-]+)?(/|$)` 时动态 `import('./ops/OpsApp')`，面板代码独立分包，普通玩家访问主页不下载任何运维面板产物。

## 3. 安全模型

### 3.1 读写全量密码验证（`X-Ops-Token`）

- 面板**所有只读视图（`GET`）与写操作（`POST`）接口**均强制校验请求头 `X-Ops-Token`。
- 服务端使用 `CryptographicOperations.FixedTimeEquals` 恒定时间比对 `X-Ops-Token` 与 `OpsOptions.Token`；缺失或不匹配一律返回 `401 Unauthorized`（`OPS_TOKEN_INVALID`），且**绝不透露**期望密码。
- 移除原仅限回环/私有网段 IP 的来源限制，允许管理员在正式服公网 HTTPS 下凭随机路径与密码完成监控查看和运维操作。
- 配置项 `Ops:Token`（可通过环境变量 `Ops__Token` / `OPS_TOKEN` 注入）：
  - 已配置：直接使用（本地开发默认值写在 `appsettings.Development.json` 为 `daohua-local-ops`；生产可在 `/opt/daohua/daohua.env` 设置 `OPS_TOKEN`）。
  - 未配置：进程启动时生成 16 字节（32 位 hex）加密随机密码，并在启动日志以 `Warning` 打印一次。
- 配置项 `Ops:Path`（可通过环境变量 `Ops__Path` / `OPS_PATH` 注入）：
  - 未配置：进程启动时自动生成 `/ops-<16位hex>` 随机路径。
  - 每次启动启用面板时，服务端以 `Warning` 日志输出当前激活的访问路径。

### 3.2 前端独立密码登录门禁

- 访问面板随机链接时，若 `sessionStorage` 中无密码或密码未通过服务端验证，`OpsApp` 只渲染居中的**密码登录门禁视图**，不发起任何后台监控轮询请求。
- 用户输入密码提交后，前端请求 `{basePath}/api/overview` 验证密码有效性：验证通过后保存至 `sessionStorage`（关闭标签页即失效，绝不写 `localStorage`）并进入监控主界面；若运行期间任一接口返回 `401`，自动清除失效密码并退回密码登录门禁视图。
- 顶部状态条提供「退出锁定」按钮，点击即清空 `sessionStorage` 密码并立即回到登录门禁页。

### 3.3 写操作二次确认

所有写操作在前端必须经过 `ConfirmDialog` 二次确认，并在确认文案中写明作用对象与不可逆性。高危操作（卸载玩家）使用危险样式。写操作执行后由服务端记录 `Ops.Audit` 审计日志（含来源地址与操作目标，绝不记录密码明文）。

## 4. 服务端 API

### 4.1 只读视图

| 端点 | 内容 | 数据来源 |
| --- | --- | --- |
| `GET /ops/api/account-abuse` | 动态账号检测策略、判定统计、近期事件与脱敏关联 | `PostgresAccountRiskRepository` |
| `GET /ops/api/overview` | 服务名/版本/环境/进程资源/运行时长/协议与 schema 版本/配表指纹/会话与玩家计数/市场指标 | 现有实现保留 |
| `GET /ops/api/health` | 全量健康检查逐项状态 | `HealthCheckService` |
| `GET /ops/api/players` | 已加载玩家摘要 | `IPlayerRuntime.GetLoadedSnapshotsAsync` |
| `GET /ops/api/players/{playerId}` | 单玩家详情：资产明细、行动队列、战斗/资源状态摘要、最近日志 | 运行时快照 + `GetRecentLogEvents` |
| `GET /ops/api/sessions` | 在线会话列表 | `SessionRegistry` |
| `GET /ops/api/market/book` | 指定品种盘口 | `MarketRuntime.GetBook` |
| `GET /ops/api/market/metrics` | 市场分片、租约就绪、账本计数 | `MarketRuntime.GetMetrics` |
| `GET /ops/api/market/orders` | 指定玩家挂单列表 | `MarketRuntime.GetOrders` |
| `GET /ops/api/market/catalog` | 可挂售品种目录（id/名称/主类型/子类型/缺省价）与品类列表：物品口径 = 非货币 + 可交易 + `sellPrice > 0`，装备全量纳入（category=`equipment`、subCategory=槽位、缺省价=锻造材料成本价） | `GameDataCatalog` |
| `GET /ops/api/compute` | 计算节点组、链路计数、任务积压 | `ComputeLinkRegistry` / `ComputeLinkMetrics` / `PostgresComputeJobRepository` |
| `GET /ops/api/storage` | durable outbox 状态分布与积压、玩家 section 版本分布、核心表体量、留存清理计数 | 新增仓储只读查询 |
| `GET /ops/api/scheduler` | 推进边界、玩家推进延迟分布、行动队列长度分布、待触发队列规模 | 运行时快照聚合 |
| `GET /ops/api/config` | 配表版本/指纹、协议版本、schema 版本、脱敏运行时配置摘要 | `GameDataCatalog` / `ServerOptions`（**绝不输出连接串与令牌**） |
| `GET /ops/api/logs` | 日志环形缓冲，支持 `level` / `category` / `q`（关键词）/ `limit` | `OpsLogBuffer`（扩展过滤） |

### 4.2 写操作

| 端点 | 作用 | 实现路径 |
| --- | --- | --- |
| `POST /ops/api/account-abuse/policy` | 更新模块开关、设备必填、额度与软风险处置，提交数据库审计 | `PostgresAccountRiskRepository.ReplacePolicyAsync` |
| `POST /ops/api/actions/kick` | 踢下线指定玩家会话 | `SessionRegistry` 取出会话并请求关闭（带关闭码与原因） |
| `POST /ops/api/actions/flush` | 强制存档（单玩家或全部） | `IPlayerRuntime.FlushAsync` / `FlushAllAsync(force: true)` |
| `POST /ops/api/actions/unload` | 卸载玩家运行时（不落盘） | `IPlayerRuntime.RemoveAsync(flush: false)` |
| `POST /ops/api/actions/compute-drain` | 排空计算节点（单个或全部） | `ComputeLinkRegistry.BeginDrain` / `DrainAll` |
| `POST /ops/api/actions/market-system-orders` | 以系统身份批量挂售/收购（单个品种 / 按品类 / 全部品种：可回收物品 + 装备） | 资金注入（`IEconomyTransactionStore`，`system-grant`）→ `MarketRuntime.PlaceAsync` 正常下单链路，即时上架 |
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
  逐品种结果如实回执，部分失败不回滚已成功项。
- 原位替换：坊市「同一玩家同一标的至多一笔活动挂单」（见 `docs/backend/domain-data-architecture.md` §5.2），
  系统身份同样受约束；每个品种下单前先 `MarketRuntime.GetActiveOrder(系统玩家, 标的)` 命中既有活动单即
  `CancelAsync` 撤销，因此重复点「系统挂售/收购」得到的是原位替换而非叠加新单。
- 默认值：物品价格默认回收价（`sellPrice`），装备默认锻造材料成本价（`actions/*.json` 中产出的配方
  inputs 按物品 `sellPrice`×数量求和、`equipment.*` 投入递归展开），数量默认 1 亿份；两者均可显式指定。
  装备只挂基础变体（variant 恒为空串），+N 强化簿不在系统下单范围。
  「全部品种」与种子脚本同口径 = `sellPrice > 0` 的非货币可交易物品 + 全部装备；
  种子脚本落库价口径略有不同：物品 = `sellPrice` / 2；装备另挂 `+10`/`+20` 变体簿，
  全部变体档统一按材料成本价。

## 5. 前端承载与信息架构

### 5.1 承载方式

- `frontend/src/main.tsx` 按 `location.pathname` 分流：匹配 `^/ops(-[a-zA-Z0-9_-]+)?(/|$)` 前缀时动态 `import('./ops/OpsApp')` 渲染运维面板，其余渲染游戏 `App`。
- `opsClient.ts` 自动从当前 URL 提取面板基础路径（`/ops` 或 `/ops-<随机后缀>`），所有只读与写操作请求统一访问 `{basePath}/api/*` 并携带 `X-Ops-Token` 请求头。
- 未认证状态先渲染独立密码登录门禁页，验证通过后才挂载监控视图与定时轮询。

### 5.2 信息架构（重设计）

- **密码登录门禁页**：标题、当前随机入口路径标识、密码输入框、错误提示与进入按钮。
- **顶部状态条**：服务名、环境、版本、运行时长、会话/玩家计数、自动刷新开关、已验证状态胶囊与退出锁定按钮。
- **左侧竖排分类导航**（对齐游戏内面板导航观感）：

| 分组 | 视图 |
| --- | --- |
| 总览 | 概览、健康 |
| 运行 | 调度、计算、日志 |
| 数据 | 玩家、会话、存储、账号检测 |
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

## 9. 账号检测页

账号检测页复用现有 OpsCard、OpsTable、OpsStatGrid 与 ConfirmDialog，不新增公共模板。设备 ID 注册额度和账号关联额度属于硬限制；设备配置、IP、网段、浏览器指纹、成功注册频率属于软评分，只有软模块编辑权重与命中阈值。策略修改沿用令牌、私有来源校验与确认弹窗，草稿不会被自动刷新覆盖。

统计口径是风险判定次数；事件区分注册、登录和连接握手，拒绝登录不会签发会话。只显示账号 UUID 与 HMAC 摘要前缀，不显示口令、Cookie、完整 IP 或原始设备字段。详情与动态生效边界见 [`anti-multi-open.md`](anti-multi-open.md)。
