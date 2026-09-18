# 道化太初（daohua）全项目潜在问题分析报告

- **生成时间**：2026-08-28 21:30:00
- **任务编号**：[TASK-20260828-08](../../tasks/TASK-20260828-08.md)
- **分析方法**：4 路并行代码审计（服务端宿主 / 领域与协议 / 前端 / 基础设施），全部问题逐文件核实到代码行，不含猜测项。
- **范围**：`src/Server`、`src/ComputeNode`、`src/LoadTests`、`src/Domain`、`src/Protocol`、`tests/Domain.Tests`、`frontend`、Dockerfile、compose、Caddy、`scripts/`、`deploy/`、数据库 schema、备份链路、`docs/ops/deployment.md`。

## 严重度总览

| 严重度 | 数量 | 典型代表 |
| :--- | :--- | :--- |
| 高 | 11 | Lazy 异常缓存致玩家永久变砖、市场分片一次瞬时错误永久停摆、非整数重复次数破坏无限循环不变量、战报日志功能整体失效、备份校验和缺失静默放行 |
| 中 | 25 | 快照跨线程读撕裂、广播 O(N²)、ToDelta 丢失资产增量、限流在反代后退化为单桶、Caddy 未拦截 /healthz |
| 低 | 30+ | 硬编码端口、i18n 散落、OCI 标签不全、epsilon 不统一等 |

---

## 一、服务端宿主（src/Server、src/ComputeNode、src/LoadTests）

### 高严重度

**H1. Lazy 异常缓存导致玩家永久"变砖"** — `src/Server/Clean/PlayerRuntime.cs:58-73`
三个 `ExecuteAsync` 重载用 `ExecutionAndPublication` 模式的 `Lazy<Task<PlayerActor>>` 缓存 actor 创建，但与 `LoadAsync`（第 53 行失败后 `_actors.TryRemove`）不同，创建失败时**不清理失败的 Lazy**。`Lazy<T>` 会缓存工厂抛出的异常：首次 `CreateActorAsync` 因瞬时 DB 故障或所有权丢失失败后，该玩家后续所有 HTTP/WS 命令永远重放同一异常，直到进程重启。修复：失败路径同样从 `_actors` 移除。

**H2. 任意未预期异常将市场分片永久置为不可用** — `src/Server/Clean/MarketRuntime.cs:257-264, 304-311, 372`
兜底 `catch` 一律 `_ready = false` 且无恢复路径。仓储层全部使用 `Serializable` 隔离级别（`PostgresMarketRepository.cs:24,162,205,291`），并发 serialization failure 或 Npgsql 瞬时超时都会落入兜底，一次瞬时错误即让整个分片停止写入、readyz 变 unhealthy，只能重启。`RenewLeaseLoopAsync` 单次续租异常也直接置不可用并退出循环。

**H3. 快照读取不持锁，与单写者并发访问聚合** — `src/Server/Clean/PlayerRuntime.cs:236-253`
`Snapshot()` 被 `LoadAsync`、`AdvanceAllAsync`、WS 握手（`WebSocketRuntime.cs:175`）在 `_gate` 之外调用，而 `ExecuteAsync`/`FlushAsync` 在锁内通过 `ExportState()`、`Balances`、`ConsumeDirtySections()` 变异同一对象。无屏障/锁保护的跨线程读写可能读到撕裂的集合与版本号。

### 中严重度

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| M1 | Lazy 工厂闭包捕获首个调用者的 CancellationToken，该请求取消会连带失败所有并发等待者 | `PlayerRuntime.cs:51-72,156` |
| M2 | HTTP-only 玩家的 actor 永不驱逐，`_actors` 随登录过的玩家数无界增长 | `PlayerRuntime.cs:40`、`Program.cs:246-251` |
| M3 | 所有权租约丢失后 actor 残留，后续每个命令重复失败，仅 WS 断连才清理 | `PlayerRuntime.cs:261-262` |
| M4 | actor 释放与在途命令竞态：WS 断连 `RemoveAsync(flush:true)` 销毁 actor 时，并发 HTTP 命令 WaitAsync 在已释放信号量上抛 ObjectDisposedException → 500 | `PlayerRuntime.cs:747-752`、`WebSocketRuntime.cs:97` |
| M5 | 每秒 O(N²) 快照广播：N 玩家每秒各触发一次 AdvanceAllAsync，每次对全部 N 会话做 7 个 section 的全量 delta 编码，CPU/分配随在线数平方增长 | `PlayerRuntime.cs:86-98`、`WebSocketRuntime.cs:73`、`ProtocolSnapshotMapper.cs:10-56` |
| M6 | 未鉴权的 `/api/v1/market/book` 可用随机 assetId 无界创建内存 OrderBook（内存 DoS），且 GetOrders 在全局锁内 O(books×orders) 遍历 | `Program.cs:279-300`、`MarketRuntime.cs:12,127-138` |
| M7 | 市场全局信号量跨 DB 事务持有（含多次网络往返），所有市场写操作进程内完全串行 | `MarketRuntime.cs:176-293` |
| M8 | 订单 sequence 仅靠 max+1 生成，schema 无唯一索引；租约交接窗口内两个写者可产生重复 sequence，破坏盘口排序确定性 | `PostgresMarketRepository.cs:379-387`、`PostgresSchema.cs:193-210` |
| M9 | Outbox / compute_jobs 基础设施未接线：全库无 `AppendAsync` 调用方；一旦未来接入而 dispatcher 仍未配置，消息每分钟无限重试且 retention 只删已投递行，表无界膨胀 | `Program.cs:97`、`PostgresOutboxRepository.cs:9-23,145-156`、`PostgresRetentionWorker.cs:25-31` |
| M10 | 反向代理场景限流退化：`KnownProxies` 仅含 Loopback，非本机反代后全体用户共享 12 次/分钟的登录限流桶，单个攻击者可锁死全站登录 | `Program.cs:40-64` |
| M11 | WS 消息帧完全不受限流约束，恶意客户端可打满 `PlayerActor._gate`，阻塞模拟推进与刷盘 | `Program.cs:143`、`WebSocketRuntime.cs:187-241` |
| M12 | 慢客户端阻塞命令结果发送（BoundedChannel Wait）并冻结接收循环，僵尸连接长期占用 session 与 actor | `WebSocketRuntime.cs:232-269` |
| M13 | 模拟奖励每玩家每秒提交 Serializable 事务，多玩家并发下 serialization failure 概率高，失败放大 DB 争用 | `PlayerRuntime.cs:440-458`、`PostgresEconomyRepository.cs:23` |
| M14 | `PlayerRuntimeSnapshotUpdated` 多播中某个订阅者抛异常会中断整个 tick，同秒后续玩家全部跳过模拟推进 | `PlayerRuntime.cs:95-97` |
| M15 | settlement claim 并发在 Serializable 下易冲突，NpgsqlException 未映射为领域错误 → 500 | `PostgresMarketRepository.cs:24-58` |

### 低严重度

- **L1** 复制粘贴的重复校验，`PostgresMarketRepository.cs:192-195` 连续两次相同 PartitionId 判断，后者永不可达。
- **L2** 硬编码端口：`Program.cs:33`（5128）、`ComputeNode/Program.cs:21`（0.0.0.0:8090，绑定所有网卡）。
- **L3** 弱密码策略：最小 6 位、最大 24 位，无复杂度要求（`PostgresAccountRepository.cs:41-42`）。
- **L4** ComputeNode `/healthz`、`/readyz` 未鉴权暴露指纹与批次计数（`ComputeNode/Program.cs:75-94`）。
- **L5** LoadTests 默认连接串含明文开发口令（`LoadTestOptions.cs:45-46`），若镜像误带 appsettings.Development.json 有泄露面。
- **L6** LoadTests 固定尾字节 GUID 与真实玩家 GUID 空间重叠，`EnableSchemaBootstrap` 误开且指向业务库时 COPY 写入伪账号（`PostgresCapacityProbe.cs:334-340`）。
- **L7** `DeterministicBatchEngine` 把引擎自身 bug 的 ArgumentException 静默归类为客户端 `INVALID_SECTION`（`DeterministicBatchEngine.cs:69-72`）。
- **L8** ReceiveLoop 每条消息 `stream.ToArray()` 额外拷贝（`WebSocketRuntime.cs:137`）。
- **L9** 同玩家顶替时新连接在旧连接关闭序列期间被 `PolicyViolation` 拒绝，需客户端自行重试（`WebSocketRuntime.cs:59-63`）。

---

## 二、领域与协议（src/Domain、src/Protocol、tests/Domain.Tests）

### 高严重度

**D1. 非整数 RepeatCount 把有限行动变成事实无限行动，且 RemainingRepeats 被减成负数** — `src/Domain/Player/PlayerAggregate.cs:206-208`、`src/Domain/Simulation/SimulationEngine.cs:135,150`
Enqueue 校验只要求 `RepeatCount` 在 [1, 1_000_000]，未要求整数。如 `2.5`：2.5→1.5→0.5→再减为 **-0.5**，`== 0` 永不成立，永不 `RemoveAction`，且不再递减——等价于 -1 无限循环并违反"非 -1 不得为负"的不变量。协议层 `ActionIntentRequest.RepeatCount` 是 `double`（`src/Protocol/Contracts.cs:182`），前端可直接传小数。恢复路径（`PlayerAggregate.cs:149-150`）同样放行小数。缺 `RepeatCount % 1 == 0` 校验与测试。

**D2. 模拟奖励的浮点累加结果依赖 AdvanceTo 的调用切分方式** — `src/Domain/Simulation/SimulationEngine.cs:106-117`
`Qi += QiPerSecond * step` 按每次推进的 step 分段累加，"1 次 AdvanceTo(10s)" 与 "10 次 AdvanceTo(1s)" 因浮点舍入可能得到不同结果。对"在线逐秒推进 + ComputeNode 离线补算"混合架构，同一玩家状态会随调度切分不同而分叉，属真实的确定性破坏点。`ProgressSeconds` 累加同理（第 66 行）。建议定长切片或按整周期结算。

**D3. 字典键 round-trip 不对称** — `src/Protocol/Contracts.cs:132-138,184`
`DictionaryKeyPolicy = CamelCase` 只在**写**时生效，读时不转换。客户端发 `{"maxPower":1}` 服务端得到键 `maxPower`，但服务端序列化含 `MaxPower` 的字典再回传后键变 `maxPower`，幂等/重放对账时键不一致。ProtocolTests 无字典键 round-trip 测试。

**D4. 测试缺口：无限循环不变量与多行动队列** — `tests/Domain.Tests`
`InfiniteActionKeepsQueueEntryAfterCompletedRounds` 只推进 17 秒单行动；缺"-1 行动后面排有限行动 + 数小时推进"、非整数 RepeatCount、两个以上排队行动、`MoveAction`/`RemoveAction`/`Breakthrough`/`TravelTo`/`ConfigureAutoUse` 的全部覆盖（后五者在整个测试目录零覆盖）。

### 中严重度

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| D5 | "-1 无限 + 配表热更后 duration 变 0" 的存量行动每次推进都打满 MaxTransitions 抛 InvariantViolation，玩家永久卡死且无恢复路径（Restore 不复核 catalog） | `SimulationEngine.cs:58-61`、`PlayerStateDocument.cs:147-150` |
| D6 | Domain 项目引用 YuoCore.Ecs / YuoCore.Abstractions 但全项目零使用，是"Domain 不得依赖 ECS"约束的隐患入口，建议移除 | `src/Domain/Domain.csproj` |
| D7 | `ProtocolSnapshotMapper.ToDelta` 构造 delta 时**丢弃 Assets**，`ChangedAssets` 恒为 null，客户端走增量路径永远收不到资产变更（实打实的字段缺失 bug） | `src/Server/Clean/ProtocolSnapshotMapper.cs:58-62` |
| D8 | ProtocolSnapshotMapper 的 catalog 参数可空，null 时回退硬编码地图与乘数，错误数据静默下发；`tests/Server.Tests/ProtocolSnapshotMapperTests.cs:18` 只传 2 参固化了违规用法 | `ProtocolSnapshotMapper.cs:10,77-108` |
| D9 | `AssetWallet.Apply` 对 `QuantityDelta == 0` 抛 InvalidArgument（走 Debit(0) 被 ValidateQuantity 拒绝），零增量本应是 no-op | `src/Domain/Economy/AssetModels.cs:70-75` |
| D10 | OrderBook.Snapshot 同价位层求和依赖 Dictionary 遍历顺序，double 求和不满足结合律，不同进程/插入历史可能得到不同层级数量 | `src/Domain/Market/OrderBook.cs:143-155` |

### 低严重度

- **D11** `OrderBook.Restore` 把 BookVersion 与订单 Sequence 两个独立计数器强行比较，长期运行后合法历史数据会触发异常（`OrderBook.cs:43`）。
- **D12** Domain 内部 `Guid.NewGuid()` 生成行动实例 ID 未注入，破坏强确定性重放（`PlayerAggregate.cs:227`）。
- **D13** `OrderBook.Snapshot` 默认时间戳取 `DateTimeOffset.UtcNow`，领域投影内嵌时钟（`OrderBook.cs:156`）。
- **D14** epsilon 不统一：Simulation 用 `1e-9`，Market/Wallet 用 `1e-12`，极端数量级下边界判定不一致。
- **D15** `DeterministicCombatEngine` 的 seed 参数完全未使用（死参数误导调用者）；`CombatEncounterState.Sequence` 用 double 存整数计数（`CombatModels.cs:63-85`）。
- **D16** `GameDataCatalog.Parse` 对重复 item id 抛 ArgumentException 而非 DomainException，错误体系不一致；指纹依赖文档枚举顺序，公开 `Parse` 传入顺序不同产生不同 Fingerprint（`GameDataCatalog.cs:162-183`）。
- **D17** WireEnvelopeCodec 强制版本相等、无版本协商写路径（`Contracts.cs:72-106`，单版本下暂不致命）；`TryDeserializeJson` 把合法 JSON `null` 载荷当失败（`:114-127`）；double 未设 NumberHandling，非有限值在序列化出口爆炸而非入口被拒。
- **D18** 命名冲突陷阱：Domain `SectionCodec`（枚举）与 Server 编解码类同名，易在 Protocol 层误引（`PlayerSection.cs:15-19`）。
- **D19** `PlayerProgression.CompletedActions` 用 double 存纯计数，long 更合适（`PlayerAggregate.cs:16`）。
- **D20** 合法配置（duration=0 + RepeatCount=1_000_000）可触发 MaxTransitionsPerAdvance，被误判为不变量违规（`SimulationEngine.cs:51-61`）。
- **D21** 测试缺口补充：OrderBook 的 Expire/分层/部分成交后 Cancel/自成交跳过无测试；AssetWallet.Restore、AssetDelta.Apply、MarketPartitioner、WorldGraph 不可达路径、CombatE Draw 分支均零覆盖。

### 依赖方向结论

- Protocol 零项目引用，合规；Domain 未引用 ASP.NET Core/数据库/浏览器程序集，但携带无用的 YuoCore 引用（D6）。

---

## 三、前端（frontend）

### 高严重度

**F1. 战报日志永远不会被填充（日志功能整体失效）** — `frontend/src/logging/gameLogService.ts:119-137`
`ingestSnapshot`/`ingestBatch`/`activate` 没有任何生产者，协议 `WireMessageType`（`protocol.ts:13-30`）也无任何 Log 消息类型，`GameLogPanel` 的 `latest` 恒为空数组——日志面板永远空白。

**F2. CombatLoadoutPanel 违反 Hooks 规则，存在崩溃风险** — `frontend/src/App.tsx:1411,1441-1444`
`if (!combat) return null` 早退位于两个 `useMemo` 之前。`combat` 在 undefined 与对象之间切换时 hook 数量变化，React 直接抛错。

**F3. 协议解析错误触发"断开→重连→再解析失败"无限循环** — `frontend/src/network/gameClient.ts:285-292,116-124,200`
未知消息 `throw` → catch 中 `requestSnapshotOrResync()` → 主动 `socket.close(1000)` → close 事件 `scheduleReconnect()`。服务端持续发送一条客户端无法解析的包时，客户端无限重复"连接即报错即断开"，同时每次 `setNotice` 弹提示刷屏。

### 中严重度

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| F4 | 心跳只有 Ping 没有 Pong/超时检测，TCP 半开时连接一直显示 online，UI 不更新也不重连 | `gameClient.ts:54-64,278` |
| F5 | `pendingRequests` 无超时清理，服务端漏回回执时 requestId 永久残留（慢泄漏） | `gameClient.ts:42,147` |
| F6 | `mapStats` 把 `spirit` 和 `foundationExp` 都赋成同一字段 `row.experience`，两个语义不同的字段取同一来源，下游 foundation 加速/消耗展示用错基数 | `snapshot.ts:202-203` |
| F7 | CombatArena 用硬编码中文做协议逻辑匹配（`'待机'`、`'灵击'`、`replace(/回气$/)`、`startsWith('赶往')` 解析目的地），服务端改文案即失效 | `CombatArena.tsx:272-289,906` |
| F8 | 多处 i18n 硬编码中文违反词典规范：BuffBar 描述串、WorldMapPanel（资源/怪物/地点/精/魁/前往按钮）、GameLogPanel、SettingsPage、ErrorBoundary、format.ts 伤害与五行名称等 | `App.tsx:525-535,1634`、`WorldMapPanel.tsx:760-885`、`GameLogPanel.tsx:28-114`、`format.ts:109-177` 等 |
| F9 | 目录加载静默吞错：items/equipment 请求 `.catch(() => [])`，且空结果被静默忽略，UI 用陈旧/空目录无从察觉 | `apiClient.ts:28-29`、`App.tsx:1876-1881` |
| F10 | WorldMapPanel rAF 循环永久全速运行（页签隐藏不停），每帧做 `nodes.find`；MapPage 用 `hidden` 常驻挂载 | `WorldMapPanel.tsx:185-323`、`App.tsx:1914` |
| F11 | 滚轮缩放：React 根节点 wheel 为 passive，`preventDefault` 不生效导致页面同时滚动；`setZoom` updater 内调用 `setPan` 属非纯 updater，StrictMode 下双调用 | `WorldMapPanel.tsx:503-521` |
| F12 | CharacterAvatar 候选索引不随 actorId 重置，上一个敌人把索引耗尽后新敌人直接落入矢量兜底，立绘显示错误 | `CharacterAvatar.tsx:73-91`、`CombatArena.tsx:1382` |

### 低严重度

- **F13** ArenaBuffSquare 环形倒计时基准只增不减，buff 刷新为更短时长时比例失真（`CombatArena.tsx:361-366`）。
- **F14** CombatArena 渲染期直接改写 `enemyRef.current`，敌方不可见时保留上一个敌人的血量/名字（`CombatArena.tsx:1229-1242`）。
- **F15** UnitNumber 的 `maxDigits` 是无效透传，`FormatUnitOptions` 根本无此字段，"最多 6 位数字"契约未实现（`UnitNumber.tsx`、`format.ts:231-240`）。
- **F16** SettingsPage 冷却倒计时每秒重建 interval（`SettingsPage.tsx:74-80`）。
- **F17** avatarStore 的 `setAvatarId` 是空实现，设置页"选择形象"点击后选中态都不更新，属死功能（`avatarStore.ts:106-110`）。
- **F18** Buff 剩余时间不随时间流逝刷新，`serverTime` 只在快照同步时更新，两次同步间剩余秒数完全静止（`App.tsx:524`、`CombatArena.tsx:359,427`）。
- **F19** MarketOrderForm 买量上限与价格提交的口径不一致，价格非法时按 vendorPrice 估算上限，提交按归一化价格校验（`MarketOrderForm.tsx:103`）。
- **F20** ErrorBoundary `setTimeout` 未在卸载时清理（`ErrorBoundary.tsx:75`）。
- **F21** `VITE_API_URL` 与 `VITE_WS_URL` 互相独立，只配 HTTP 跨域地址忘配 WS 时出现 Cookie 会话一半通一半不通的难排查故障（`apiClient.ts:8-11`、`gameClient.ts:21-25`）。
- **F22** 少量 `as unknown as` 双断言：`uiScale.ts:35`、`FloatingPopover.tsx:220`（SSR 分支 null 当 HTMLElement 用会 NPE）、`ItemDisplay.tsx:520,543`。
- **F23** 全局 Notice 将协议错误与普通提示同质化且 2.6s 即消失，重要错误（协议断层、会话过期）容易被完全错过（`App.tsx:1831-1837`、`gameClient.ts:290`）。

### 前端核实无问题的方面

- WS 重连有指数退避（500ms→8s）与去重守卫、乱序旧包按 sequence 丢弃、snapshot/delta 有 version 单调校验与断层全量重拉。
- 各动画 rAF/Animation/timeout 清理完整；apiClient 有 15s 超时与 AbortSignal；MarketPage 轮询有 visibilityState 休眠与 inFlight 去重。
- 数字格式化（NaN/Infinity/负数/进位）正确；无 `any`/`@ts-ignore`；未发现已收拢公共组件的重复实现。

---

## 四、基础设施与数据层（Docker、Caddy、scripts、deploy、备份链路）

### 高严重度

**I1. 备份校验和缺失时静默跳过校验** — `deploy/verify-backup.sh:24-26,34-36`
sha 文件不存在时直接跳过 SHA-256 校验继续通过"备份门禁"。手工拷贝的备份或 sha 误删时，门禁在未验证完整性的情况下放行。应改为缺 sha 即失败。

**I2. 更新失败存在真实停机窗口，文档与实现不符** — `deploy/ccr-auto-update.sh:88-95`
实现是 `compose up --detach --force-recreate app compute-node` 直接替换 → 健康检查失败 → 再 force-recreate 回滚；失败期间服务已下线，且回滚造成第二段停机。而 `docs/ops/deployment.md` 声称"候选镜像先健康预热，失败时保持当前实例"——文档与实际行为实质性不符。

**I3. 本地 PostgreSQL 17 与生产 PostgreSQL 18 主版本漂移** — `compose.yaml:6` vs `deploy/compose.prod.yaml:5`
文档声明生产为 PostgreSQL 18；备份/恢复脚本硬编码 `postgres:18-alpine`。若 17 卷数据被 18 镜像启动会被 PG_VERSION 检查拒绝，且开发/生产行为可能存在差异。

### 中严重度

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| I4 | `dotnet restore` 未加 `--locked-mode` 且无 NuGet lock 文件，构建不可重现 | `Dockerfile:24-25` |
| I5 | `deploy-prod.sh` 只校验 `COMPUTE_TOKEN`，完全不校验 `DB_PASSWORD`，文档却声称两者都校验 | `scripts/deploy-prod.sh:156-163` |
| I6 | `ccr-auto-update.sh` 存在幽灵变量 `APP_IMAGE_FILE` 死分支，回退链路未经真实演练 | `deploy/ccr-auto-update.sh:44` |
| I7 | daily/weekly 备份是单点触发复制，00 点档失败或被更新锁阻塞则当天/当周无归档且不补齐 | `deploy/backup.sh:78-85` |
| I8 | 逻辑备份与物理备份使用不同锁，物理备份不与自动更新互斥，03:20/03:40 归档任务也可能重叠，2c4g 机器 IO 压力叠加 | `deploy/backup.sh:28`、`base-backup.sh:16` |
| I9 | market-ledger-archive 首次运行为单事务全量归档（90 天窗口逐月 DELETE+RETURNING+INSERT），长事务/WAL 暴涨，与 `DB_MEM_LIMIT: 1g` 冲突风险高，无分批提交也无 advisory lock | `deploy/market-ledger-archive.sh:38-96` |
| I10 | Caddy 未拦截公网 `/healthz`，烟测证实其返回 status 与 version，构成版本信息泄露面 | `deploy/xx.yuohira.com.caddy:10-11` |
| I11 | 公网入口无速率限制/请求体大小限制，登录/注册接口可爆破 | `deploy/xx.yuohira.com.caddy` 全文 |
| I12 | 烟测默认密码硬编码在仓库（`SMOKE_PASSWORD` 兜底值），且默认允许公网注册 `smoke_probe` 账号，可被抢先占用 | `scripts/smoke-production.mjs:100-101` |
| I13 | PostgreSQL 密码以明文出现在容器环境变量（`docker inspect` 可读）；生产镜像 `prod` 未固定 digest 且 timer 每分钟无条件 pull | `deploy/compose.prod.yaml:91,189`、`ccr-auto-update.timer:6` |

### 低严重度

- **I14** `.dockerignore` 未排除 `.agents/`、`AGENTS.md`、`deploy/`、`scripts/` 等，无 secret 泄露但构建上下文冗余。
- **I15** OCI 标签缺 `source`/`licenses`/`url`/`vendor`（`Dockerfile:49-53`）。
- **I16** 本地与生产 compose 固定同名卷 `daohua_daohua-postgres`，同宿主机误用另一份 compose 会共享数据卷。
- **I17** `max_connections=50` vs 连接池总和 48（app 32 + worker 16），启用 worker profile 后无告警余量（`compose.prod.yaml:36`）。
- **I18** 备份校验失败但已改名的归档不清理，可能被 verify 脚本的 `sort | tail -1` 选中；备份与数据同机同盘无异地副本。
- **I19** `deploy-prod.sh` 远端只 curl `/readyz`，文档声称验证 `/healthz`、`/readyz`，口径略不符（healthz 仅靠容器 HEALTHCHECK 间接覆盖）。
- **I20** `docker-build-prod.sh` dirty 检查包含 untracked 文件（`.DS_Store`、`temp/` 也阻断），`--allow-dirty` 全有或全无（`:88-96`）。
- **I21** `verify.sh:77` DB_PASSWORD 缺省回落开发口令，本地 5432 跑其他 PostgreSQL 时会用错凭据（有 `--require-database` 兜底）。
- **I22** `pitr-restore.sh:96-100` 恢复临时实例无资源限制、无自动清理，2c4g 生产机与生产 DB 并存可能 OOM。
- **I23** `docker-build-prod.sh` 允许 build-arg 覆盖 `NPM_CONFIG_REGISTRY`，无来源校验（`Dockerfile:9-11`）。

### 基础设施核实无问题的方面

- 端口全部绑定 `127.0.0.1`；容器 `read_only`、`cap_drop: ALL`、非 root UID 10001；无 `latest` 标签；平台强制 `linux/amd64`；镜像有防运行数据进层的 `test ! -d /app/data` 断言。
- 数据库 schema 的 trades/audit_events 列集与归档脚本一致，索引覆盖主要查询路径，lease CAS 使用正确；**代码中实际无 advisory lock**（文档描述与实现此处需注意一致性）。
- 全部 SQL 参数化，无注入；Caddy 反探测拦截、HSTS/CSP、WebSocket、点文件拦截均正确；WAL 归档脚本的保留/原子写入逻辑正确。

---

## 五、跨领域综合风险（优先修复顺序建议）

按"对玩家的实际伤害 × 触发概率"排序：

1. **D1 非整数 RepeatCount**（可由前端直接触发，玩家卡死/事实无限行动）+ 补 D4 测试。
2. **H1 Lazy 异常缓存**（一次瞬时 DB 抖动 → 玩家永久不可用直到重启）。
3. **H2 市场分片一次瞬时错误永久停摆**（内测期随时可触发）。
4. **F1 战报日志功能整体失效**（用户可见功能完全不工作）。
5. **F2 Hooks 违规崩溃**、**F3 协议错误重连死循环**。
6. **D7 ToDelta 丢失资产增量**（增量路径资产永不同步，依赖全量快照兜底）。
7. **H3 快照跨线程读撕裂** + **M3/M4 actor 生命周期竞态**。
8. **D2 奖励累加对推进切分敏感**（混合结算架构下状态分叉）。
9. **M5 广播 O(N²)**（在线数上升后 CPU 先崩）。
10. **M10 代理后限流退化** + **I10 /healthz 泄露** + **I11 登录可爆破**（安全面）。
11. **I1 备份校验静默放行**、**I2 更新停机窗口与文档不符**、**I3 PG 版本漂移**。
12. **D6 移除 Domain 无用 YuoCore 引用**、**F8/F7 i18n 与硬编码中文匹配**、其余中低项按模块顺手清理。

## 六、本次分析核实无问题的高风险面（供对照）

- 健康端点不泄露异常/连接串；SQL 全参数化；密码 PBKDF2-SHA512 600k 迭代 + 固定时间比较，token 仅存哈希。
- 余额变更统一 `FOR UPDATE` + 幂等流水 + 排序加锁防死锁；section flush 队列 `FOR UPDATE SKIP LOCKED` + 指数退避 + 租约正确。
- 数值类型 double 规约整体合规（未发现 float/decimal 混入）；依赖方向 Protocol 合规；前端无 `any`/`@ts-ignore`、公共组件无重复实现。
- 部署容器安全基线（非 root、只读根、无 latest、端口回环绑定）全部达标。
