# 性能与分布式架构优化 · 变更日志

> 本文档是 TASK-20260924-08 的变更总账：每一批优化修改的**原因、方向、涉及范围、验证结果与提交号**都记录在这里，供审查用。
> 红线：不改变任何游戏内容——战斗结果、掉落、市场撮合、行动队列语义、快照语义与确定性快进结果保持完全一致。

## 基线

- 起点提交：`8bdf290`（TASK-20260924-06/07 合并提交后的干净工作区）。
- 既有分布式能力：compute-link.v1 计算节点反向拨入（动态接入/断开、任务自动分配、断线批次回退），详见 `docs/tasks/TASK-20260923-05.md`。
- 真实部署拓扑：主服 `YuoHira`（公网，daohua.yuohira.com）；节点 `odin`（无公网 NAT 设备）。**硬约束：节点只能主动拨出，任何依赖主服反向直连的设计一律排除。**

---

## 首轮四路并行审计结论（2026-09-24）

审计域：A=服务端运行时与持久化、B=领域模拟与确定性、C=计算节点链路、D=协议与前端网络层。

### ⚠️ 阻断级发现（先于一切性能项）

| 编号 | 发现 | 证据 | 处置 |
| --- | --- | --- | --- |
| X1 | **ComputeNode 不执行领域模拟**：`DeterministicBatchEngine.Advance` 只做 Base64→Brotli 解压→注入 5 个元数据字段→重编码→SHA256，完全没有 `SimulationEngine.AdvanceTo`；而回写把 `last_simulated_at` 推到目标时刻，玩家上线后补算窗口归零——**离线收益被静默吞掉，且生产默认启用** | `src/ComputeNode/DeterministicBatchEngine.cs:143-169`、`src/Server/Clean/ComputeDispatcherWorker.cs:168-170`、`deploy/compose.prod.yaml:110` | Wave 3 旗舰项：ComputeNode 引用 Domain 做真实补算（契约升 v2）。落地前建议在 YuoHira 临时 `COMPUTE_NODE_ENABLED=false` 止损 |
| X2 | `simulation.advance` 客户端可达且 `targetTime` 取自载荷：恶意客户端一次请求可秒结至多 48h 收益 | `src/Server/Clean/PlayerRuntime.cs:1099-1104` | W1-B：服务端钳制 target ≤ UtcNow（+时钟偏差容忍） |
| X3 | droppable delta 被 `TrySend` 丢弃时服务端基线已前移→客户端永久漂移（快照语义漏洞，慢客户端必现） | `src/Server/Clean/WebSocketRuntime.cs:281-287,637-641` | W2-B：丢弃时回滚基线 + 慢消费者阈值强断 |
| X4 | 批次超时后节点迟到结果被判「协议违规」断开整条链路→该节点其他在飞批次陪葬→弱网恶性循环 | `src/Server/Clean/ComputeLinkEndpoint.cs:125-127` | W1-A：迟到结果降级为告警+丢弃 |
| X5 | `MarketRuntime` 一次瞬时故障即永久停摆（无租约重获路径），须进程重启 | `src/Server/Clean/MarketRuntime.cs:113,523-537` | W2-C：退避重获循环（fail-closed 不变） |
| X6 | `persistence-worker` 角色白跑派发链且与 Api 重复执行冷挂机生产扫描 | `src/Server/Program.cs:135,450` | W2-A：角色化装配/生产者优先级栅栏 |
| X7 | 超限批次（节点隐性 ~4.2MiB 上限或 >32 段）整批判 dead，随后被冷扫描反复再生产再判 dead 的死循环 | `src/ComputeNode/ComputeContract.cs:47-58`、`PostgresComputeJobRepository.cs:298-299` | W2-A：字节预算装配 + INVALID_BATCH 可重试 + hello 协商批次上限 |
| X8 | `_logBuffers` 按「曾加载玩家×200 条」单调增长，actor 卸载不清理 | `src/Server/Clean/PlayerRuntime.cs` `_logBuffers` | W1-B：随 actor 移除清理 |

### 高收益性能项

| 编号 | 项 | 一句话 | 波次 |
| --- | --- | --- | --- |
| P-lease | 租约续期写放大 | 每 tick/命令/flush 一次 `player_runtime_leases` UPDATE（500 在线≈500+ 写/s）→ actor 内按 lease/3 去抖 | W1-B |
| P-delta | delta 全量重建 | 每次推送对全部 8 段做 JSON+SHA256+Base64 再 diff → 用 `MarkDirty` 差集只编码触及段 | W2-B |
| P-dispatch | 派发器单线程 | 全节点组任一时刻只有 1 个在飞批次，节点并发容量闲置 → 按节点容量并行在飞 | W2-A |
| P-writeback | 回写逐任务事务 | 64 job×~8 section ≈ 512+ 串行 SQL RT → `unnest`/NpgsqlBatch 合批 + 小并发 | W1-C |
| P-attr | 属性解析每步全量 | `PlayerAttributeResolver.Apply` 每 ≤5s 步全量 Resolve（Loadout JsonElement 克隆+3 字典）→ 修订号缓存 | W2-D |
| P-battle | 战斗相位全量往返 | 每步 Restore→AdvanceTo→ToExpeditionState 整场重建 → live battle 跨步复用 | W3 |
| P-events | 补算事件流无界 | 48h 补算可产十万级事件，消费方只取尾 200 → 有界尾窗+丢弃计数 | W1-D |
| P-nbreg | NodeBuildingRegistry 全局锁 | 每 tick 每玩家取全局 `_gate`+`ToArray`，写路径锁内跑串行事务 → 不可变快照+版本戳 | W1-B |
| P-econ | 经济提交 RT 数 | 每 delta `FOR UPDATE`+UPSERT 2RT、lines 逐行插、提交后全量重读钱包 → RETURNING 喂回 | W1-C |
| P-mkt | 市场分片大锁+整簿克隆×2+到期全扫 | 到期改最小堆、消除双向克隆 | W2-C |
| P-sweep | 推进/刷盘串行 sweep+每拍全量回滚快照 | sweep 有界并行；回滚缩脏段/惰性重建 | W2-B/W3 |
| P-social | 社交扇出 6 串行查询/人 + presence 锁内全扫 | 合批单连接 + 节点倒排索引 | W2-E |
| P-hello | WS 握手 ~10+ DB RT 串行 | 并行化 + NpgsqlBatch | W2-E |
| P-wire | 分段 JSON→Base64→JSON 双重编码(+33%) | codec=json-inline 或 gzip（契约演进） | W3 |
| P-coldscan | 冷扫描先全量加载再按 head 丢弃 | 扫描 SQL 直接 JOIN heads 过滤 | W1-C |
| P-world | world 段每次全地图 JsonElement 扫描+全投影重建 | 节点静态投影缓存 | W2-B |
| P-micro | Domain 外层步固定税/LINQ 微分配（目标选择/遇怪/MinBy/事件格式化等） | 见审计 B P5-P11 | W1-D |
| P-orderbook | OrderBook 每次下单全簿扫描+排序 | 价格有序结构，撮合次序逐位保持 | W2-C |
| P-front | 前端每段新建 TextDecoder/手写 atob/localStorage 每帧全量写 | 共享解码器/签名比对+debounce | W1-E |

### 分布式就绪缺口（演进目标，部分落地部分留档）

- 玩家请求路由（L7 sticky/转发）、进程内注册表（Session/Presence/DeviceQuota/Chat）共享化、NodeBuildingRegistry 多实例写安全、市场跨分片结算与路由、节点→主服单点拨入与集群级容量视图、任务级结果帧、per-node 凭证、优雅排空、链路背压与积压指标、端到端压测口径。→ 详细清单存于本任务审计档案，落地节奏见波次计划。
- **跨节点逐位一致硬性前置**：统一 `Math.Pow/Cbrt` 等超越函数口径或固定运行时/架构（odin=arm64）；Dictionary 导出序按键 Ordinal 固定；契约单源（消除 Server/ComputeNode 双份手写副本）。

### 波次计划

| 波次 | 内容 | 状态 |
| --- | --- | --- |
| W1 | A:compute-link 韧性批 / B:PlayerRuntime 热路径+NodeBuildingRegistry / C:持久化批量化 / D:Domain 微优化批 / E:前端解码减负 | ✅ 全部落地（5 批已提交） |
| W2 | A:派发并行化+批次字节预算+契约 v1.1 / B:脏段 delta+基线回滚+world 缓存 / C:市场恢复+到期堆+OrderBook / D:属性缓存+导出缓存 / E:社交扇出+握手并行+广播编一次 | ✅ 全部落地（5 批已提交） |
| W3 | 旗舰：ComputeNode 接入真实 Domain 模拟（compute.v2：节点 Restore→AdvanceTo→导出+AssetDeltas 回传，SectionCodec 迁 Domain 单源，跨进程 golden vector）；B:live battle 跨步复用；C:经济提交瘦身+回滚窄化 | 🚀 进行中（A/B/C 并行实施） |
| W4 | 观测面（compute 指标/拒绝日志/压测口径）、多主服就绪文档化（路由/共享在线态/排空协议）、收尾审计 | 待启动 |

---

## 变更记录

（按时间顺序追加，每批一条：日期 / 提交号 / 标题 / 原因 / 方向 / 范围 / 验证）

### 2026-09-24 · `561ce55` · W2-B 快照增量脏段窄化与背压基线修正

- **原因**（审计 P-delta/P-world/X3 + D 域 5/13）：每次推送对全部 8 段做 JSON+SHA256+Base64 再 diff——变更检测成本与实际变更解耦，是 500 连接下推进循环 CPU 头号瓶颈；`TrySend` 丢弃时基线已前移 → 慢客户端永久漂移（X3 快照语义漏洞）；world 段每次全地图 JsonElement 扫描+全投影重建；BroadcastChat 逐收件人重复序列化同载荷。
- **方向**：`PlayerAggregate.DirtySectionMarks` 流水号差集（推进/命令前后差分，不受 `ConsumeDirtySections` 影响）→ `BuildDelta` 只编码触及段，checksum diff 保留兜底裁决；combat 段候选由 `WireAttributesChanged` 精确判定（借属性修订号缓存 `ReferenceEquals`，旁路时安全退化为总变）；`CommitPushBaseline` 只在入队成功后提交（丢弃即基线不动，下拍自愈重发），持续不排空按 `4009 slow consumer` 强断；world 段 `ConditionalWeakTable` 静态投影索引；BroadcastChat 载荷单次序列化共享；握手 `WhenAll` 并行。
- **范围**：`PlayerRuntime`、`ProtocolSnapshotMapper`、`WebSocketRuntime` + `SnapshotDeltaPushTests`（11 用例）/ `AdversarialConcurrencyTests`。
- **验证**：Domain.Tests 172/172；Server.Tests 136/136；前端 build；DataTool strict。
- **取舍**：`CommandResult` 仍携带全量快照（改增量风险中、收益集中于高频命令路径）——保留语义稳定，后续评估；未知差集路径（幂等重放/AttachDuel 等）一律 `TouchedSections=null` 全段候选保守正确。

### 2026-09-24 · `0ab4777` · W2-D 属性解析修订号缓存与导出短路

- **原因**（审计 P-attr/P7）：`PlayerAttributeResolver.Apply` 每 ≤5s 外层步全量重算（Loadout JsonElement 克隆+3 字典+逐装备 `TryGetProperty`），48h 补算≈34560 步×1~2 次，是 Domain 最大 CPU+GC 来源；`ExportDuel`/`Loadout.Export` 无缓存每次全量重建（Duel 因 RecordStats 几乎每拍都脏）。
- **方向**：新增 `VersionedList`/`VersionedDictionary`（写入即递增 `Version`、结构体枚举器零分配、枚举序与插入序一致）；`AttributeInputRevision` 合成六大输入源版本（RealmLevel/World 建筑投影/Buffs.Active/EquippedItemIds/Equipment/CoreAttributes），`Resolve` 同配表实例+同修订号命中直返；`ExportDuel` 三级短路（结构修订+账本引用+逐账本脏扫描），`Loadout.Export` 装配修订短路——触发器仅真变更时克隆，导出字节逐位相同。`Combat` 面板兜底路径旁路缓存逐次重算（专项测试锁定）。
- **范围**：`Domain/Common/VersionedCollections.cs`（新）、`PlayerAggregate`、`PlayerLoadout`、`PlayerDuel`、`PlayerStateDocument`、`PlayerAttributeResolver`、`SimulationEngine`（签名适配）+ `AttributeRevisionCacheTests`（11 用例：两路径逐字段+字节相等、扰动全覆盖、兜底旁路）。
- **验证**：Domain/Server Release 0 警告；Domain.Tests 170/170。
- **附带**：`PlayerAggregate.DirtySectionMarks` 流水号由本文件承载（W2-B 增量窄化共用机制，测试在 `PlayerAggregateTests`）。

### 2026-09-24 · `a802096` · W2-C 市场恢复力与撮合索引化

- **原因**（审计 X5/P-mkt/P-orderbook）：任意非预期异常 `_ready=false` 即永久停摆须重启（40001 瞬态也降级→重试撞 EnsureReady 的矛盾曾实测出现）；分片大锁下每次 place/cancel 先全扫到期订单+整盘口克隆×2+逐笔到期独立事务；`OrderBook` 撮合每单全簿扫描+候选排序。
- **方向**：续租循环升监管循环——瞬时错误透出 `WithTransientRetryAsync` 自愈不降级，真失 fence/schema 错走指数退避重获（AcquireLease→`_gate` 内 RestoreBooks 重建→到期结算→`_ready=true`）；OrderBook 三派生索引（ask 价序/bid 价序/到期序）+ `OrderBookJournal` 撤销日志：撮合前缀枚举、到期队首终止、失败 `Undo` 逆序回放；到期落库 `ExpireBatchAsync` 单事务合批。
- **范围**：`MarketRuntime`、`PostgresMarketRepository`、`Domain/Market/OrderBook.cs` + 测试（`MarketMatchingEquivalenceTests` 400 步随机对照 3 用例、`LeaseLossTriggersFailClosedThenAutoRecovery`）。
- **验证**：Domain/Server Release 0 警告；Domain.Tests 170/170；Server.Tests 125/125（实测降级→重获→恢复 ~2s）。
- **取舍**：终态订单在 `_orders` 累积（与 DB 同量级，恢复重建只装活动单），硬上限留后续。

### 2026-09-24 · `312d11d` · W2-A 派发并行化与批次字节预算（compute-link.v1.1）

- **原因**（审计 P-dispatch/X6/X7）：派发器全局单线程串行（Acquire→claim→发批→等响应+落库→下一轮），整个节点组任一时刻仅 1 个在飞批次，节点 `MaxConcurrentBatches` 完全闲置——2000+ 补算吞吐的直接瓶颈；批次只数任务数不看字节 + 节点估算 bug（×3 实为 ~4.2MiB 上限）导致超限批次整批 dead→冷扫描再生产→再 dead 的死循环；persistence-worker 角色白跑派发链并重复生产任务。
- **方向**：`Acquire()` 改原子占槽（节点 `_inFlightSlots` CAS），全局在飞上限=Σ节点 Capacity、单节点≤64，最闲优先；`CapacitySignal` 唤醒防睡满轮询；出站写补 10s 超时；装批按 `Σbase64长度×3/4` 解码字节预算贪心打包（`ComputeNode:BatchMaxBytes` 默认 8MiB/上限 12MiB），超组预算单任务 `JOB_TOO_LARGE` dead、装不下零退避回 pending；hello 追加 `maxJobsPerBatch/maxBatchBytes` 能力协商，链路版本直升 **v1.1**；批次级违规 retryable、任务级违规 non-retryable 不连坐；`AddComputeDispatch` 仅 Api 装配；节点 Kestrel 调试口放宽到 24MiB 对齐帧体。
- **契约治理**：Server/ComputeNode 双份手写副本保持（统一代价大），新增 `ComputeLinkContractAlignmentTests` 逐字断言字段集/常量/版本/type 判别/估算公式。
- **范围**：`ComputeDispatcherWorker`、`ComputeLink{Registry,Contracts,Endpoint}`、`ComputeDispatchContract`、`ServerOptions`、`Server/Program.cs`、`ComputeNode/{ComputeContract,ComputeLinkContracts,UpstreamLinkWorker,Program}.cs` + 测试（新增 7 用例）。
- **验证**：Server/ComputeNode Release 0 警告；Server.Tests 124/124（全新库）；Domain.Tests 156/156。
- **取舍**：批次违规未做拆半重发（retryable+预算装批已够）；「介于节点预算与组预算之间」的任务靠 maxAttempts 兜底（Acquire 先 claim 才知大小，字节感知暂不做）。

### 2026-09-24 · `a884b15` · W2-E 社交扇出与握手合批

- **原因**（审计 P-social/P-hello + A 域 10/13）：一次换节点/上下线 → 旧+新节点全部在线玩家各跑 6 次串行 SQL；presence 变更在全局锁内全扫+排序；每条聊天消息同事务做 LIMIT 100 裁剪 DELETE；会话认证每请求一次三表 JOIN。
- **方向**：`BuildStateAsync` 改 `LoadStateSnapshotAsync`（7 条 SQL 一个 `NpgsqlBatch` 单往返，队伍头/成员用子查询消依赖往返）；presence 加 `_nodeIndex` 倒排（CollectAffected 变两节点集合 Union，排序移出锁）；`NotifyAsync` 收件人去重+≤4 并行构建；聊天裁剪按 `(channel,scopeKey)` 每 16 写摊销一次（超限量≤15 条，100 条上限最终成立）；`ValidateSessionAsync` 加 3s token_hash 正缓存，`RevokeSessionAsync`/`RenamePlayerAsync` 主动失效（吊销即时性不依赖 TTL）。
- **范围**：`Social/{SocialRuntime,SocialContracts,OnlinePresenceRegistry,PostgresSocialRepository,PostgresChatRepository}`、`PostgresAccountRepository` + `ChatAndSocialTests`（裁剪口径重写+快照装配新用例）。
- **验证**：Server Release 0 警告；Server.Tests 117/117；Domain.Tests 156/156。
- **遗留**：WS 握手链本身的并行化（BuildStateAsync 与快照编码/日志回放 WhenAll）需改 `WebSocketRuntime`，随 W2-B 批次接线。

### 2026-09-24 · `bf4e7b4` · W1-C 持久化层批量化

- **原因**（审计 P-writeback/P-coldscan + C 域 3/5/12）：批次结果回写 = 每 job 独立事务 × 每 section 一条 upsert（64×8≈512+ 串行 RT）；`output_payload` 整存含 base64 的结果 JSON 存储 ×2.3；冷扫描先全量加载再按 head 丢弃；claim 谓词与索引不匹配；整批退避一刀切。
- **方向**：section upsert 改 `unnest` 单语句合批 + `RETURNING` 保留逐段 applied 判定（head FOR UPDATE→CAS→批量 upsert 锁序不变）；同 job 的 section 写+Complete 并入同事务、多 job `Parallel.ForEachAsync(4)`；`output_payload` 改存 `ComputeJobOutputSummary`；扫描 SQL `JOIN player_state_heads` 按 `last_simulated_at` 窗口过滤；`compute_jobs` 换 `(target_time) WHERE pending` / `(lease_until) WHERE leased` 部分索引；`ReleaseLease` 按 attempts 分组逐 job 退避。
- **附带**：6 个真实连库测试类收进 `PostgresIntegration` collection 串行，消除与市场 Serializable 事务的 SSI 谓词冲突偶发失败。
- **范围**：`Postgres{Section,ComputeJob}Repository`、`PostgresSchema`、`ColdCatchUpWorker`、`ComputeDispatcherWorker` + 测试（新增 `SectionRepositoryTests` 4 用例等）。
- **验证**：Server Release 0 警告；Server.Tests 116/116（全新库）；Domain.Tests 152/152。
- **遗留**：经济提交 RETURNING 喂钱包（A#7）本批未覆盖，移入 W2。

### 2026-09-24 · `a7b211f` · W1-B PlayerRuntime 热路径减负

- **原因**（审计 P-lease/X2/X8 + A 域 9/2）：每次推进/命令/flush 都对 `player_runtime_leases` 发 UPDATE（500 在线≈500+ 写/s）；`_logBuffers` 按曾加载玩家数单调增长；`simulation.advance` 客户端可传未来 `targetTime` 秒结 48h 收益；`Snapshot()` 每拍序列化全部行动 Intent 仅供运维计数；`NodeBuildingRegistry` 全局 `_gate` 让每玩家每 tick 串行过锁。
- **方向**：actor 内 `_lastLeaseRenewedUtc` 按租约/3（15s）去抖，失败仍 fail-closed；actor 移除三路径（RemoveAsync/创建失败/EvictOwnershipLost）同步清日志缓冲；`targetTime` 钳制 `UtcNow+30s`；`PlayerRuntimeSnapshot.Actions` 改 `QueuedActionCount`（OpsPanel 改计数消费，协议/持久化字段零变化）；注册表改不可变快照 `Volatile.Write` 整体替换 + 单调版本戳替代 LINQ 签名，读路径零锁、到期才进 gate 惰性结算。
- **范围**：`PlayerRuntime`、`NodeBuildingRegistry`、`OpsPanel` + 测试（新增 `NodeBuildingRegistryTests`）。
- **验证**：Server Release 0 警告；Server.Tests 116/116；Domain.Tests 152/152。

### 2026-09-24 · `17f62f4` · W1-A compute-link 链路韧性批

- **原因**（审计 X4/X7 前置项 + C 域清单）：批次超时后节点迟到结果被判协议违规断开整条 WS → 该节点其他在飞批次陪葬的重连恶性循环；双侧未设 `KeepAliveTimeout` 半开连接发现靠 TCP 超时（分钟级）；节点会话结束后在飞批次继续空算、闸位泄漏；重连退避永不复位（死代码）；token 先比长度有侧信道；调试口独立闸可挤爆节点算力且默认绑 0.0.0.0；watchdog 探测串不自匹配防护失效。
- **方向**：迟到结果改节流 Warn+丢帧；`KeepAliveInterval=20s/KeepAliveTimeout=60s` 双侧显式；节点会话级 linked CTS 取消在飞批次；退避按「会话存活≥60s 才复位」；token SHA256+FixedTimeEquals；四处拒绝路径节流 Warn（不含 token 本体）；`SemaphoreSlim` 入 DI 单例两侧共用；调试口默认 127.0.0.1（odin 显式 `DAOHUA_COMPUTE_URLS` 不受影响）；watchdog 改 `DaoHua[.]ComputeNode`。
- **附带修正**：节点侧指纹漂移异常原是 `_ =` 丢弃任务的未观测异常（注释声称的断链重握手从不发生）→ 改 Warn+`session.Cancel()` 真实落实。
- **范围**：`ComputeLinkEndpoint.cs`、`Server/Program.cs`、`ComputeNode/{Program,UpstreamLinkWorker}.cs`、odin watchdog/README、新增 `ComputeLinkEndpointTests`（4 个 Kestrel E2E）。
- **验证**：Server/ComputeNode Release 0 警告；Server.Tests 105/105（全新 PG 库）；Domain.Tests 152/152。

### 2026-09-24 · `6616a3c` · W1-E 前端解码与本地存储减负

- **原因**：快照分段每段新建 `TextDecoder`+手写 atob 循环、信封 `bytes.slice` 二次拷贝（高频微分配）；战斗中 duel 分段每次击杀随 delta 下发 → `battleReportStore` 每次全量 `JSON.parse`+`localStorage.setItem`（≤50 条）主线程同步磁盘写。
- **方向**：模块级共享 `TextDecoder`；`Uint8Array.fromBase64`（ES2025 Baseline）优先、旧运行时探测回退 atob；`slice→subarray` 零拷贝；战报历史改 per-player 内存镜像 + `reportId:endedAt` 签名比对跳过未变合并 + 5s 防抖写 + `visibilitychange→hidden` 立即落盘。
- **范围**：`frontend/src/network/{snapshot,protocol}.ts`、`frontend/src/game/battleReportStore.ts`。协议语义与展示零变更。
- **验证**：`npm run build`（含 tsc）零错误；`check-frontend-contracts.mjs` 通过。

### 2026-09-24 · `8629e2d` · W1-D Domain 事件尾窗与微优化批

- **原因**（审计 P-events + P-micro）：`SimulationResult.Events` 随推进时长无界增长——48h 离线补算可产十万级 `SimulationEvent`（消费方只取尾 200 条），领域层先行全额驻留；外层每步固定扫描税（Buff/冷却/自动使用/暂存）与 LINQ 微分配（`Where/ToArray/Sum/MinBy/MaxBy/OrderBy/Prepend/Count(pred)`）在 2000+ 玩家补算规模下放大为 GC 与 CPU 热点。
- **方向**：
  - 领域出口改 `SimulationEventTail` 有界尾窗（保底 256、水位 512 整段摊销折叠），`SimulationResult` 新增 `DroppedEventCount`（Dropped+Count==产出总数）；全部事件路径（战斗合并/行动完成/阻塞/自愈移除/自动消耗品/斗法/世界/子阶段合并）统一走尾窗；战斗合并流按尾窗容量做 `keepFrom` 门控——注定被裁的帧跳过名称解析与消息构造，阵亡/起身衍生帧仍按战斗时钟交错落日志（会计路径不受影响）。
  - 外层步短路：无 Buff 跳过扫描、无启用自动使用槽整段跳过、`AdvanceIdle` 仅在确有冷却/吟唱时执行、无暂存物品跳过结算（SettleStagedItems 的 Duel 脏标记语义不变）；Buff 到期单趟扫描+移除后重算最早到期。
  - 目标选择去分配：`SelectTarget`/`SelectTargets` 不再物化存活列表，直接在 opponents 上按出现序扫描；Sequential Elimination 的已命中集改用引用比较（`List.Remove` 等价语义），Roll/seed/side/channel 与累加顺序逐位不变。
  - 遭遇选择物化缓存：`RandomSpawns` 经 `ConditionalWeakTable` 拆为并行数组（仅缓存 encounter 自有字段，兼容手工构造配表）；monsters 字典解析与全部校验逐次执行保留异常时机；轮盘/fallback/special 强度查找全部展开（strengthOf 字典覆盖语义以反向扫描复刻）。
  - 机械去 LINQ：`Actions.All→AllActionsBlocked`、转换循环内 `catalog.Get` 按 actionId 缓存、`CombatSustainedSlots` 迭代器→`TryGetCombatSustainedSlot` 索引扫描、`waveNames/NameOf` 惰性化（仅确实落日志帧才解析怪物名）、`MinBy/MaxBy` 按「严格比较替换+先见者平局」语义复刻、`ExternalBuffStacks` 求值体无枚举器、`Prepend` 拆两段、Buff 聚合属性改循环。
  - `PlayerCombatSkillState`：`NextBoundarySeconds`/`AdvanceIdle`/`IsValid`/`ApplyTo` 去 LINQ（空冷却返回 +∞、保留剩余>seconds+1e-9 项、吟唱清除语义不变）。
- **范围**：`src/Domain/Simulation/SimulationEngine.cs`、`src/Domain/Combat/{CombatModels,EncounterWaveSelector,SkillTriggerEvaluator,CombatSkillState}.cs`、新增 `tests/Domain.Tests/SimulationEventTailTests.cs`（4 用例：水位边界/缩放/分段一致/上限内有界）。`SimulationLogMapper`/`PlayerRuntime` 消费接口不变（Events 仍为 IReadOnlyList）。
- **验证**：Domain Release 0 警告；Domain.Tests 156/156；Server Release 0 警告；SimulationLogMapperTests+PlayerRuntimeTests 27/27。位级确定性回归（整段 vs 逐秒切分、资源回复、战斗/掉落序列）全绿。
- **红线复核**：随机序列调用次数与顺序不变（Roll 调用点/参数逐位一致）；double 累加顺序不变（Sum→同序循环、轮盘 ticket 递减序一致）；事件产生顺序不变（尾窗只裁最旧段，保留段为连续后缀）；`RemainingRepeats == -1` 无限循环不出队不变量未触碰。
