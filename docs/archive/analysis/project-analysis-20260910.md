# 道化太初（daohua）开发进度与潜在问题深度分析报告

- **生成时间**：2026-09-10 18:35:11
- **任务编号**：TASK-20260910-01
- **分析基线**：提交 `9303050`（2026-09-09 17:14），工作树干净
- **方法**：五路并行代码核查（服务端运行时 / 领域与协议 / 前端 / 基础设施与文档 / 玩法完成度），叠加真实门禁实测；所有结论均回到当前代码行核实，并修正了并行核查中的两处误判。
- **对照基线**：`docs/project-analysis-20260828.md`（2026-08-28 首次全项目审计）

---

## 一、进度总览

| 维度 | 实测结果 |
| :--- | :--- |
| 提交规模 | 267 次提交，首个提交 2026-08-11，开发周期约 1 个月 |
| 最近提交 | `9303050` feat: 支持战斗遭遇远征阶段状态快照与确定性推进（2026-09-09） |
| 工作树 | 干净（无未提交改动），子模块 `vendor/YuoCore` 固定在 `3a7b1e3`，无漂移 |
| Release 构建 | ✅ 成功，**0 Warning / 0 Error** |
| Domain.Tests | ✅ 59/59 通过 |
| Server.Tests | ✅ 29/29 通过 |
| 任务看板 | 88 个任务详情文档；仅 1 个任务在进行中（TASK-20260824-29 主界面悬浮岛屿重构） |

**阶段判断**：项目已越过「框架搭建」阶段，进入**架构骨架完成、玩法逐个填充、生产化收尾**的中间阶段。上一次审计（08-28）列出的 11 项高危问题，绝大多数已在 `21af63a 收敛全项目审计问题` 及后续提交中真实修复（见第六节）。当前风险重心已从「代码正确性」转移到**玩法深度缺口**与**生产运维安全面**。

---

## 二、核心玩法完成度矩阵

| 玩法 | 完成度 | 证据与关键缺口 |
| :--- | :--- | :--- |
| 战斗 | ~90% | `SimulationEngine.AdvanceCombatAction` 完整状态机（搜寻→遭遇→确定性战斗→奖励→再搜寻），种子确定性；缺口见「高危 P1」 |
| 市场 | ~85% | `OrderBook` / `MarketRuntime` / `PostgresMarketRepository` + 前端 `MarketPage` 全套 |
| 采集 | ~80% | 走通用 Timed 计时 + 产出（`CompleteRepeat`） |
| 修炼 | ~60% | `Breakthrough()` 存在，但 `cultivation.json` 仅 11 行，境界升级逻辑单薄 |
| 装备 | ~55% | `ReplaceLoadout` 真改状态；锻造产出仅资产数字，**无品质/强化生成** |
| 功法 | ~50% | 配表 `manuals.json`(332 行)、`abilities`(229 行) 已读入，但技能对战斗的实际加成链路未验证 |
| 炼丹 | ~50% | `alchemy.json` 存在，但仅通用 Timed 产物品，**无成功率/品质/失败惩罚**专属逻辑 |
| 炼器 | ~45% | `forging.json`(408 行) 逻辑同炼丹；且配置 `"type":"alchemy"` **语义错误** |
| 旅行 | ~40% | `TravelTo` 仅 `set NodeId/X/Y`，**无路径/耗时/节点解锁** |
| Buff | **空壳 ~0%** | 领域无任何 buff 代码、配置无 buff 目录、前端无面板——完全缺失 |

**结构性结论**：`SimulationEngine` 主循环对非战斗行动只有「计时 + 持续奖励 + 完成奖励」，`definition.Kind` 仅用于 `MarkDirty` 脏区标记，**没有任何按 ActionKind 的专属确定性分支**。炼丹/炼器/旅行/功法效果目前都是「配表齐全但代码未实现」的断层。

---

## 三、潜在问题（按严重度）

### 高危

**P1. 远征战败后行动永久 `Blocked`，队列静默冻结（最新提交引入）**
`SimulationEngine.cs:347` 战败时置 `action.Status = ActionQueueStatus.Blocked` 并 `return`；主循环 `SimulationEngine.cs:106-107` 遇到 `Blocked` 直接 `break`（不仅跳过该行动，**后续排队行动全部停摆**）。全仓 `Blocked` 仅 3 处引用（定义 1 + 判定 1 + 赋值 1），**无任何服务端自动解锁路径**。玩家只能通过前端 `action.remove`（`CombatArena.tsx:1426/1445`、`App.tsx:482`）手动移除该行动才能恢复。
> 与项目红线「行动队列不变量、-1 无限循环不得自动出队」同类的可恢复性缺口：不变量本身未破坏，但缺乏失败降级/提示，属用户可见的静默卡死。

**P2. 冷挂机计算节点 `ComputeNode` 完全未接线**
`src/ComputeNode` 是完整服务（`DeterministicBatchEngine` / `ComputeContract` / `TokenValidator`），`Protocol/Contracts.cs:392` 也预留了 `ComputeAdvanceRequestDto/ResponseDto`。但对 `src/Server` 全目录检索 `ComputeNode|ComputeClient|X-Compute-Token|ComputeAdvance` —— **零命中**。主服务离线补算在 `PlayerRuntime.cs` 内进程直调 `SimulationEngine.AdvanceTo`。
> 后果：AGENTS.md 规划的高延迟冷挂机补算能力无法在目标架构（2c4g，2000+ 冷挂机）上验证，容量门禁中的相关项缺少真实链路支撑。

**P3. 市场盘口接口未鉴权 + 盘口创建无上限（内存 DoS）**
`Program.cs:289` 的 `/api/v1/market` 路由组未挂 `RequireAuthorization`，其中 `mapGet("/book")`（`:290`）对任意合法 `assetId` 经 `MarketRuntime.GetBook` 走 `_books.GetOrAdd(...)` 创建空盘口。相邻 `/metrics`（`:312-316`）与 `/orders`（`:317`）均已鉴权，**唯独 book 公开**。攻击者可枚举品种耗尽内存。

**P4. 生产入口安全面缺口（Caddy 层）**
`deploy/xx.yuohira.com.caddy`：未拦截公网 `/healthz`（返回 `status` 与 `version`，构成版本泄露面）；无速率限制与请求体大小限制（登录/注册接口可被爆破）。

**P5. 发布与备份链路的生产风险**
- `deploy/ccr-auto-update.sh:88` 以 `--force-recreate` 直接替换，健康失败再 force-recreate 回滚，**存在真实停机窗口**，且与 `docs/ops/deployment.md:112` 声称的「候选镜像先健康预热、失败保持当前实例」不符。
- `deploy/market-ledger-archive.sh:54-101` 首次运行为**单 DO 块单事务**全量归档，无分批提交、无 advisory lock，WAL 暴涨，与 `DB_MEM_LIMIT: 1g` 冲突风险高。
- `deploy/base-backup.sh:12` 物理备份锁与 `ccr-auto-update` 锁不同，**物理备份与自动更新不互斥**。
- `compose.yaml:6`（PG17）与 `deploy/compose.prod.yaml:5`（PG18）**主版本漂移**。

### 中危

| # | 问题 | 位置 |
| :--- | :--- | :--- |
| P6 | 反代后限流退化：`KnownProxies` 仅 `Loopback`，全体用户共享登录限流桶，单攻击者可锁死全站登录 | `Program.cs:63` |
| P7 | WebSocket 消息帧不受限流约束；慢客户端阻塞接收循环（有界通道满时 `Wait`） | `Program.cs:370`、`WebSocketRuntime.cs:31,358` |
| P8 | `AdvanceAllAsync` 多播 `SnapshotUpdated` 无 try/catch，任一订阅者抛异常中断整 tick 剩余玩家推进 | `PlayerRuntime.cs:154-155` |
| P9 | 模拟奖励每玩家每秒提交 `Serializable` 事务，N 玩家/秒 = N 个串行化事务 | `PlayerRuntime.cs:582`、`PostgresEconomyRepository.cs:23` |
| P10 | 纯 HTTP 玩家 actor 永不驱逐（仅 WS 断连调用 `RemoveAsync`），`_actors` 随登录玩家数无界增长 | `PlayerRuntime.cs:166` |
| P11 | `Outbox` / `compute_jobs` 已装配 Worker 但**无生产者**（全仓无 `AppendAsync` 调用方）；未来若接入 dispatcher 未配置，消息会无限重试且表无界膨胀 | `Program.cs:94-97` |
| P12 | 领域层 `Guid.NewGuid()` 生成行动实例 ID，未注入，破坏强确定性重放 | `PlayerAggregate.cs:351` |
| P13 | `Domain.csproj:14` 仍 `ProjectReference YuoCore.Ecs` 但源码零使用，违反「Domain 不得依赖 ECS」约束入口 | `src/Domain/Domain.csproj` |
| P14 | 协议 `WireEnvelopeCodec` 强制版本相等无协商写路径；`TryDeserializeJson` 把合法 JSON `null` 当失败；double 未设 `NumberHandling`（非有限值在序列化出口爆炸而非入口被拒） | `Contracts.cs:110,201` |
| P15 | `ProtocolSnapshotMapper` 的 catalog 参数可空，null 时回退硬编码地图，错误数据静默下发 | `ProtocolSnapshotMapper.cs` |
| P16 | 前端 `CombatArena` 用硬编码中文做**协议逻辑匹配**（`'待机'`、`/回气$/`、`'灵击'`、`startsWith('赶往')`），服务端改文案即失效 | `CombatArena.tsx:272/286/339/901` |
| P17 | 前端目录加载静默吞错：`items/equipment` 请求 `.catch(()=>[])`，空结果被忽略，UI 用陈旧/空目录无从察觉 | `apiClient.ts:28-29` |
| P18 | 心跳只有 Ping 无 Pong/超时检测，TCP 半开时连接仍显示 online，不重连 | `gameClient.ts:74-84` |
| P19 | 全局 Notice 把协议错误与普通提示同质化且 2.6s 消失，重要错误（协议断层、会话过期）易被完全错过 | `App.tsx:1823-1834` |
| P20 | `ErrorBoundary` `setTimeout` 未在卸载时清理；`OrderBook.Snapshot` 内嵌 `DateTimeOffset.UtcNow`；`OrderBook.Restore` 混淆 `BookVersion` 与订单 `Sequence` | `ErrorBoundary.tsx:75`、`OrderBook.cs:235,67` |

### 低危（择要）

- epsilon 不统一：Simulation 用 `1e-9`，Market/Wallet 用 `1e-12`。
- `GameDataCatalog.Parse` 重复 id 抛 `ArgumentException` 而非 `DomainException`，错误体系不一致。
- `ProtocolSnapshotMapper.DiffAssets` 用精确 `double` 相等比较资产数量，浮点尾差可能误判。
- `ComputeNode` `/healthz`、`/readyz` 未鉴权暴露指纹（`ComputeNode/Program.cs:75,85`）。
- `Dockerfile:24` `dotnet restore` 无 `--locked-mode` 且无 NuGet lock 文件，构建不可重现。
- `Dockerfile:49-53` OCI 标签缺 `source`/`licenses`/`url`/`vendor`。
- `pitr-restore.sh:64-70` 恢复临时实例无 mem/cpu 限制、无自动清理，2c4g 机器可能 OOM。
- 测试缺口：`World`/`Travel`、炼丹炼器、装备品质、功法效果、`OrderBook` 部分成交后 Cancel / 自成交跳过 等基本零覆盖。

---

## 四、文档与实现不一致

1. **`AGENTS.md:46`** 称「市场账本使用 PostgreSQL revision CAS 与 advisory lock」，但全仓无 `pg_advisory` 实现——文档误导，与 P5 的归档脚本缺 lock 互为印证。
2. **`docs/ops/deployment.md:108,112`** 声称更新时「校验 `/healthz` + `/readyz`」「候选镜像先健康预热」，与 `deploy-prod.sh:237`（仅 curl `/readyz`）和 `ccr-auto-update.sh:88`（force-recreate）的实际行为不符。
3. **`deployment.md:108` / `AGENTS.md:210`** 引用 `/etc/caddy/sites/daohua.caddy`，实际部署文件为 `deploy/xx.yuohira.com.caddy`，路径误导。

---

## 五、优先修复建议

按「对玩家的实际伤害 × 触发概率 × 修复成本」排序：

1. **P1 远征 Blocked 静默冻结**——最新提交引入，战斗中随时可触发；建议战败时自动降级（如自动重试/自动出队/置为可恢复的 Paused 并加事件提示），并补「-1 无限行动 + 战败」的确定性测试。
2. **P3 市场 book 未鉴权**——加 `RequireAuthorization`，并将盘口创建限定在已存在/已订阅品种集合，加数量上限。
3. **P4 + P5 生产安全与发布**——Caddy 拦截 `/healthz`、加限流与体积限制；修正更新停机窗口或据实修订文档；归档脚本改分批 + advisory lock；统一本地/生产 PG 主版本。
4. **P2 ComputeNode 接线**——把离线补算切到 ComputeNode 批处理契约，才能真正验证 2000+ 冷挂机容量门禁。
5. **P6/P7/P8 运行时健壮性**——限流键改用真实客户端 IP、WS 纳入限流、多播加异常隔离。
6. **P12/P13 确定性收口**——领域内移除 `Guid.NewGuid()`（改为注入/确定性派生），删除 Domain 对 `YuoCore.Ecs` 的无用引用。
7. **P16/P17 前端协议解耦**——协议逻辑匹配改为结构化字段（executionKind/phase 枚举）而非中文文案；目录加载失败可见化。
8. **玩法深度**——优先补 Buff 系统与炼丹/炼器成功率品质、旅行路径耗时，否则「配表齐全但无逻辑」会持续扩大断层。

---

## 六、本次核实确认「已修复 / 无问题」的面（供对照）

- **上次审计高危项多数已修复**：Lazy 异常缓存（H1）、快照跨线程撕裂（H3）、广播 O(N²)（M5）、非整数 RepeatCount（D1）、奖励累加切分敏感（D2）、字典键 round-trip（D3）、`ToDelta` 丢资产（D7）、零增量异常（D9）、`snapshot.ts` mapStats 语义错（F6）、Hooks 违规（F2）、协议错误重连死循环（F3）、`pendingRequests` 无超时（F5）、rAF 永久循环（F10）、滚轮 passive（F11）、角色候选索引（F12）、备份校验静默放行（I1）、`deploy-prod.sh` 不校验 DB_PASSWORD（I5）、幽灵变量（I6）——均已确认修复。
- **WebSocket 鉴权实际有效**：`/ws` 虽未在路由层挂 `RequireAuthorization`，但 `WebSocketRuntime.cs:90-92` 在握手内校验 `IsAuthenticated` 与 `player_id` claim，未通过返回 401。**不构成鉴权缺口**（注：前一版并行核查曾误判此项）。
- **远征 phase 解码与后端一致**：后端 `CombatExpeditionPhase` 为 0/1/2（`CombatModels.cs:194-202`），前端 `mapExpeditionPhase` 同时兼容数字与字符串形式（`snapshot.ts:166-187`），**当前无解码缺陷**，仅对新增枚举值缺乏前向兼容回退。
- 依赖方向 `Protocol -> Domain -> Server` 合规；数值 `double` 规约整体合规；SQL 全参数化；密码 `PBKDF2-SHA512` 600k 迭代 + 固定时间比较；部署容器（非 root UID 10001、只读根、仅 `/tmp`、`linux/amd64`、`prod` 移动标签）全部达标；`frontend-sim/` 未被正式前端引用，符合「禁止作生产入口」规则。
