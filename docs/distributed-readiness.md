# 分布式演进设计：多主服就绪缺口审计与优雅排空

> 本文回答一个问题：现在这套「一个主服 + 一群主动拨入的计算节点」的结构，将来要变成「多个主服」时，**哪些地方会坏、先修哪个、怎么修**。
> 结论先行：**任务层（冷挂机派发链）几乎天然多写者安全，真正的缺口集中在玩家连接层**。先把"单主服多副本"做稳，再谈真多主服。
> 关联文档：`docs/perf-arch-changelog.md`（本批改动账本）、`docs/ops/deployment.md`（生产部署基线）、`docs/backend/anti-multi-open.md`（防多开设计真源）。

## 0. 现在长什么样

```text
浏览器 --HTTPS/WSS--> Caddy --> daohua API（YuoHira，唯一主服）
                                   |
                                   +--> PostgreSQL 18（唯一 durable 真源：存档/市场/任务/会话/租约）
                                   ^
                                   | 反向拨入 wss://daohua.yuohira.com/internal/compute-link
                                   |（compute-link.v1.3，compute.v2 批次载荷）
                            计算节点组（odin 等 NAT 设备，只拨出不监听）
```

各层"状态放哪"一览：

| 层 | 状态在哪 | 多写者安全性 |
| --- | --- | --- |
| 玩家存档/资产/市场账本 | PostgreSQL（唯一真源） | ✅ 已安全：Serializable + 行锁 + fence + 幂等键 |
| 冷挂机任务 `platform.compute_jobs` | PostgreSQL | ✅ 已安全：`FOR UPDATE SKIP LOCKED` 领取 + `lease_token` 归还 + 租约超时孤儿重取（`PostgresComputeJobRepository.cs:181-218`） |
| 玩家运行时单写者 | `game.player_runtime_leases` | ✅ 已安全：`PostgresPlayerOwnershipRepository` UPSERT 抢占/续租/释放，失败即 fail-closed（`EcsPlayerRuntimeAdapter.cs` 加载时 Acquire、操作前去抖 Renew、卸载 Release） |
| 市场分片写者 | `market.partition_leases` | ✅ 已安全：抢占 + fence token 递增 fencing（`PostgresMarketRepository.cs:165-191`） |
| section 落盘队列 / outbox | `platform.section_flush_queue` / `platform.integration_outbox` | ✅ 已安全：SKIP LOCKED + 行租约 |
| **玩家连接层**（会话/存在感/聊天扇出/设备配额/互踢） | **进程内存** | ❌ 缺口全部在这里 |
| **节点组视图**（ComputeLinkRegistry） | 进程内存 | ⚠️ 按"拨入归属"天然分片可用，缺集群级视图 |

## 1. 关键判断：任务层天然多写者安全，缺口在玩家连接层

把"多主服"拆开看，其实是两类问题：

- **任务层**（谁扫玩家、谁领任务、谁派给节点、谁写回结果）：所有共享状态已经在共享 PostgreSQL 里，领取靠 `SKIP LOCKED`、写回靠版本 CAS 与租约令牌。**两个主服同时跑派发器不会重复领同一行、不会双写同一玩家**——这层在真多主服下基本不用改。
- **玩家连接层**（谁在哪个主服在线、怎么找到他、怎么踢他、聊天怎么送到）：这些状态全部活在进程内存里（六个注册表见 §3）。多主服一上，每个实例只看得见"自己怀里的"在线玩家，跨实例行为会一个个失效。

所以演进顺序不是"先把所有东西都搬出去"，而是：**先把玩家连接层的归属路由和共享视图做出来，任务层只需要补集群视图与一个判死口径**。

## 2. 「节点主动拨入」约束下，多主服拓扑怎么工作

硬约束（见 `docs/tasks/TASK-20260924-08.md` 真实部署拓扑）：odin 这类节点在 NAT 后，**只能节点拨出、主服永远不能反连节点**。这条约束反过来让多主服拓扑变得很简单：

```text
                     ┌──> 主服 A（本地节点组：n1,n2）──┐
节点拨 wss://域名/... ──┤                              ├──> 共享 PostgreSQL
（经 Caddy 负载均衡）   └──> 主服 B（本地节点组：n3）   ──┘   platform.compute_jobs
```

1. **节点该连哪个主服？** 随便哪个。节点拨的还是同一个公网域名，由 Caddy/边缘把 WebSocket 升级请求负载均衡到任一健康主服；落在谁身上，就注册进谁的 `ComputeLinkRegistry`，就替谁干活。主服 A 挂了，节点按既有退避重连，边缘把它导到主服 B——**故障转移天然成立，不需要任何主服间协调**。
2. **主服间怎么分活？** 每个主服的 `ComputeDispatcherWorker` 只做一件事：`registry.Acquire()` 拿一个**自己怀里**的节点 → `ClaimDueAsync` 从共享任务表 `SKIP LOCKED` 领一批 → 派给该节点。主服 A 永远派不到主服 B 怀里的节点，但两个主服领的是同一张任务表，谁手上有闲节点谁就领活——**负载在任务表这一层自动均衡**。
3. **主服间必须共享什么？** 只有两样，而且都是弱一致心跳表就够：
   - `platform.server_instances`：实例目录（instance_id、内部地址、版本、心跳）——玩家路由和市场路由都要靠它把 `owner_instance_id` 翻译成"那台机器在哪"；
   - `platform.compute_nodes`：集群节点视图（节点会话、挂在哪个实例、容量）——运维容量视图和 §3-E 的判死口径都靠它。
   **不需要**主服-主服 RPC、服务发现组件或消息队列；低频事件（踢人、聊天扇出）用 PostgreSQL `LISTEN/NOTIFY` 就够。
4. **任务表多写者细节**（`PostgresComputeJobRepository.cs`）：领取是 `FOR UPDATE SKIP LOCKED` 单行抢占，归还/完结都带 `lease_token` 谓词防误伤他人行，断线租约靠 `lease_until` 过期后被任何主服重取——**主服 A 领走的任务即使 A 整个宕机，也只是等租约到期由 B 重派，不会丢**。

## 3. 缺口逐项审计

每项四段：现状（具体类型/文件）、多主服下怎么坏、演进路径、与既有机制的衔接。分期口径：**P0 = 单主服多副本就绪就该做**（滚动重启、主备切换不再丢东西）；**P1 = 真多主服前置**（没有它多主服功能就错）；**P2 = 真多主服运营形态**。

### 总览表

| # | 缺口 | 现状 | 多主服下失效模式 | 分期 |
| --- | --- | --- | --- | --- |
| A | 会话注册表 `SessionRegistry` | 进程内存（`WebSocketRuntime.cs:135`） | 互踢只对本实例生效；同账号可在两个实例各挂一条会话 | P1 |
| B | 在线存在感 `OnlinePresenceRegistry` | 进程内存（`Social/OnlinePresenceRegistry.cs`） | 跨实例玩家互不可见：附近列表、好友在线状态缺一半 | P1 |
| C | 传音 `ChatRuntime` | 收件人按本地 presence 解析（`Social/ChatRuntime.cs:129-147`） | 世界/附近频道跨实例收不到；节流窗按实例翻倍 | P1 |
| D | 设备配额 `DeviceQuotaRegistry` + 准入 | 进程内存（`AntiMultiOpen/DeviceQuotaRegistry.cs`） | 同设备账号配额按实例数翻倍，防线失效 | P1 |
| E | 节点注册表 `ComputeLinkRegistry` | 进程内存，按拨入归属分片（`ComputeLinkRegistry.cs`） | 本身可工作；但 `JOB_TOO_LARGE` 判死只看本实例节点组，可能误杀别实例大节点能接的任务 | P1 |
| F | 节点建筑 `NodeBuildingRegistry` | 进程内存缓存 + 信号量写（`NodeBuildingRegistry.cs:98`） | **材料贡献跨实例丢更新**：玩家背包已扣、行被旧缓存覆盖 | **P0** |
| G | 玩家请求跨实例路由 | 租约表已有 owner_instance_id，无实例目录、无转发 | 请求落到非持有实例：actor 加载失败或等租约过期 | P1 |
| H | 任务生产者全局 leader | `ColdCatchUpWorker` 每实例各扫一遍 | 重复扫描/重复全量加载，靠入队幂等兜底，正确但浪费 | P0（可选） |
| I | 市场跨分片结算 | 分片租约已就绪；`ClaimSettlementAsync` 在 PartitionCount>1 时显式拒绝（`MarketRuntime.cs:235-236`） | 多分片/多实例下结算领取无路由；非分片持有实例直接 503 | P1 |
| J | 节点多上游故障转移 | `ComputeNode:UpstreamUrl` 单值（`ComputeNode/Program.cs:66`） | 单域名下边缘负载均衡已能转移；多域名/多入口才需要上游列表 | P2 |
| K | 集群级容量视图 | `OpsPanel` 单进程只读（Development-only）；`registry.Nodes` 只含本实例节点 | 运维看不到全集群节点/会话/分片归属 | P1 |
| L | 其他顺带项 | ASP.NET 限流、会话缓存、schema bootstrap 并发 | 限流配额按实例翻倍；会话注销缓存跨实例滞后 ≤3s；建表并发竞态偶发 | P2 |

### A. 会话注册表 `SessionRegistry`（`WebSocketRuntime.cs:135-199`）

- **现状**：`ConcurrentDictionary<playerId, WebSocketSession>`，`AddOrKick` 原子顶替本进程内的旧会话；互踢、慢消费者关闭（4009）、聊天投递（`BroadcastChat`）全部经它解析"这个玩家的 socket 在哪"。
- **失效模式**：同一账号从两个入口登录会分别落在两个实例——两边 `SessionRegistry` 互不知道，`AddOrKick` 各自成立，**互踢语义消失**；随后第二个实例在 `LoadOwnedAsync` 抢 `player_runtime_leases` 失败（`EcsPlayerRuntimeAdapter.cs`，`PlayerOwnershipLostException` fail-closed），Hello 流程半途失败——表现为"第二端登录卡死/被踢，但第一端毫无知觉"。
- **演进路径**：
  - P1：会话表仍留进程内（socket 本来就是本进程资源），但**互踢裁决改为两级**——本地 `AddOrKick` + 查 `player_runtime_leases`/在线会话表拿到持有者实例号，经 `LISTEN/NOTIFY`（如 `player_kick:<playerId>`）通知持有实例按 4006 关闭旧会话；旧会话拆除走既有 `RemoveAsync → ownership.ReleaseAsync`（`EcsPlayerRuntimeAdapter.cs`）**立即释放租约**，新实例随后抢占成功。实例死亡场景兜底仍是 45s 租约过期。
  - 衔接：这套"踢人→释放租约→新实例接管"与 §G 的玩家路由共用实例目录；`WebSocketSession.TeardownCompleted` 的顶替等待（`WebSocketRuntime.cs:419`）语义原样复用，只是把等待对象从本进程旧会话换成"远端会话已拆+租约已释放"。

### B. 在线存在感 `OnlinePresenceRegistry`（`Social/OnlinePresenceRegistry.cs`）

- **现状**：进程内 `_entries`（playerId→道号/节点）+ 节点倒排索引，供世界/附近广播收件人解析、好友与队伍在线标记、`PresenceChanged` 扇出。
- **失效模式**：A 实例的玩家在 B 实例的"附近修士/好友在线状态"里完全隐身；`PresenceChanged` 事件也不跨实例。
- **演进路径**：
  - P1：加 `platform.online_presence` 心跳表（player_id、instance_id、display_name、node_id、entered_at），Hello 准入成功时 upsert、会话拆除时 delete，实例心跳兜底清理孤儿行；`BuildStateAsync` 的在线标记与附近列表改读该表。附近频道成员变更的"受影响玩家集合"（`CollectAffected`）按 instance_id 分组，跨实例部分经 `NOTIFY` 让对端实例刷新自己的会话。
  - 衔接：`game.player_projection.presence` 已经有一条"在线/离线"的持久化口径（刷盘时更新，`PostgresSectionRepository.cs:1085-1098`）——它解决的是"冷挂机该不该算你"这种秒级容忍问题；在线存在感的毫秒级展示需求用专门的心跳表，两者不混用。
  - 低成本过渡：若接受"附近/在线标记按秒级滞后"，P1 前期可直接读 `player_projection.presence`，把精确 presence 表后置。

### C. 传音 `ChatRuntime`（`Social/ChatRuntime.cs`）

- **现状**：作用域解析（世界空串/附近=节点/好友=双方 ID 升序/队伍=partyId）→ 落库 `social.chat_messages`（每作用域留 100 条）→ 收件人解析走本地 presence → `BroadcastChat` 只投本实例会话（`WebSocketRuntime.cs:629-644`）。节流窗 `_rateWindows` 也是进程内（每玩家每频道 3 条/秒）。
- **失效模式**：世界频道、附近频道都变成"本实例频道"——两个主服的玩家在同一个地图节点上互相看不见发言；节流配额 ×实例数。
- **演进路径**：
  - P1：消息落库不变；**扇出改为"事件广播 + 各实例就地过滤"**——发言实例在落库后向 `NOTIFY chat_fanout` 发一条轻量事件（channel、scopeKey、senderId、messageId）；每个实例收到后用自己的 presence 解析**本实例收件人**并投递（世界→本地全部会话；附近→本地该节点会话；好友/队伍→成员列表查 PG 后过滤本地在线）。这样**不需要跨实例收件人清单**，每实例只投递自己怀里的 socket。
  - 节流窗：发言频率限制是防刷辅助线，不是安全边界；多实例下可接受 ×N，或随 presence 表落成后改查"近 1 秒该玩家全集群发言数"。
  - 衔接：消息真源本来就在 PG，历史回放不受多实例影响；`PresenceChanged` 的社交刷新扇出（`SocialRuntime.NotifyAsync`）同理由 NOTIFY 事件扩展到全实例。

### D. 设备配额 `DeviceQuotaRegistry` + 准入（`AntiMultiOpen/DeviceQuotaRegistry.cs`、`AntiMultiOpen/AntiMultiOpenAdmission.cs`）

- **现状**：进程内 `fingerprint → {playerId: refcount}`，`TryEnter/Leave` 以 socket 生命周期为真值，无需心跳。
- **失效模式**：同一设备指纹在两个实例上各记各的，"同设备同时在线账号数上限"实际放大为 上限×实例数。
- **演进路径**：
  - P1：`platform.device_sessions`（fingerprint、player_id、instance_id、session_id、entered_at，配合实例心跳清理孤儿行）；`TryEnter` 改为"事务内 `SELECT COUNT(DISTINCT player_id)` + 插入"，仍保持"先配额后顶替"的裁决顺序（`AntiMultiOpenAdmission.cs:46-64`）；`Leave` 改为按行删除。
  - 衔接：与 §A 共用同一个"会话登记"动作——设备会话行和 §B 的 presence 行可以在同一个事务/同一组 INSERT-DELETE 里落地，不增加额外往返。

### E. 计算节点注册表 `ComputeLinkRegistry`（`ComputeLinkRegistry.cs`）

- **现状**：拨入即注册、断开即注销，`Acquire()` 挑 `Available` 最大的节点原子占槽；在飞批次经 `Pending` 表等待结果帧。这是**唯一一个"进程内反而正确"的注册表**——socket 在哪个实例，节点就属于哪个实例，不需要共享。
- **失效模式**：派发本身无缺口（见 §2）。真正的问题是一个细节：`ComputeDispatcherWorker` 判死超限任务时，`groupBudget` 取的是**本实例可见节点组**的最大批次预算（`ComputeDispatcherWorker.cs:198-199`）。多主服下 A 实例挂的都是小节点、B 实例挂着大节点时，A 会把"其实 B 能接"的大任务直接判 dead，形成跨实例视野误杀。
- **演进路径**：
  - P0 过渡：多副本部署时让节点组批次预算趋同（同一 `MaxBatchBytes` 配置），误杀面归零；
  - P1：`groupBudget` 改从 `platform.compute_nodes` 集群视图取全集群最大承接能力（该表本来就要为 §K 建，顺带修判死口径）。
  - 衔接：drain 落地后 `Acquire()` 已按 `Draining` 过滤（见 §4）；节点在实例间迁移就是普通断线重连，`Pending` 在飞批次按租约语义退回，多主服下由新落点实例的派发器接手。

### F. 节点建筑 `NodeBuildingRegistry`（`NodeBuildingRegistry.cs:98-324`）——P0 必修

- **现状**：`game.node_buildings` 整行 upsert；进程内 `_gate` 信号量串行化建造/升级/贡献/惰性完工，读路径走 Volatile 快照。贡献材料是"锁内读旧行→按剩余需求封顶→合并→整行写回"的 read-modify-write。
- **失效模式**：**真数据丢失**。两个玩家分别在两个实例上向同一座建筑捐材料：A 实例扣背包→按自己缓存写回 +10 木；B 实例缓存里还是旧行→扣背包→写回 +5 石，**把 A 刚写进去的 10 木整个覆盖掉**。玩家背包已扣、建筑材料却丢了。完工结算双实例重复执行尚属幂等（`FinalizeIfComplete` 结果确定），但贡献合并是硬错误。
- **演进路径**：
  - P0：把贡献合并下沉进 PG 事务——`SELECT ... FOR UPDATE` 锁行→应用层按剩余需求封顶→写回，或改 jsonb 原子合并；建筑写低频（建造/升级/捐材料/完工），直接走行锁事务即可，不需要分片或租约。`NodeBuildingRegistry` 降级为"读缓存 + 写后失效"，快照版本戳改以行 `updated_at`/版本列为准，跨实例读靠短 TTL 或 NOTIFY 失效。
  - 衔接：玩家背包扣减仍走 `commitDebitAsync` 在**同一条事务**内提交（与 §G 玩家聚合的 `PostgresEconomyRepository` 同事务边界一致），保证"扣材料"与"记贡献"同生同灭。

### G. 玩家请求跨实例路由——多主服的"主缺口"

- **现状**：`EcsPlayerRuntimeAdapter` 内存 `_loaded` + `player_runtime_leases` 租约表（owner_instance_id + lease_until）。租约保证"同一玩家全局只有一个写者"（抢占失败 `PlayerOwnershipLostException`，续租丢失 `PLAYER_OWNERSHIP_LOST` 后逐出实体并 fail-closed），但**租约表里只有"谁持有"，没有"持有者在哪、怎么把请求送过去"**。斗法撮合直接遍历 `_loaded` 已加载玩家，跨实例寻求者互相不可见。
- **失效模式**：玩家 WS 落到非持有实例 B → B 在 Hello 加载时抢租约失败 → 连接建不起来；HTTP `POST /api/v1/players/{id}/commands` 落到 B 同理失败。玩家唯一出路是等 A 的租约过期（最长 45s）或 A 的会话拆除释放。
- **演进路径**：
  - P1 第一步（便宜）：**实例目录 `platform.server_instances`**（instance_id、内部地址、版本、心跳、role）。有了它，`owner_instance_id` 才能翻译成可路由目标。
  - P1 第二步（二选一或叠加）：
    - **边缘粘滞**：Caddy `reverse_proxy` 多 upstream + `lb_policy cookie`（或以 `daohua_session` 做一致性哈希），同一会话永远落同一实例——WebSocket 与 HTTP 天然同路，应用零改动；实例死亡时亲和失效自动换实例，配合"会话拆除即释放租约"语义，迁移最快可秒级完成。
    - **实例内转发**：任一实例接受连接，Hello 时查租约持有者，非本实例则经内部链路转发（hairpin）或回 409 让客户端重连——应用改动大、时延多一跳，仅在粘性不可靠时补。
  - 推荐：**先边缘粘滞**（P0 部署层就能备），实例目录为踢人/市场路由必需；转发留作后路。
  - 衔接：斗法撮合随路由一并解决——寻求者仍按"已加载玩家"撮合，但候选集改从 `platform.online_presence`（§B）取跨实例在线寻求者，剧本挂载仍回各自持有实例执行；撮合本质是低频玩法操作，允许先限"同实例撮合"再放开。

### H. 任务生产者全局 leader——其实没那么紧急

- **现状**：`ColdCatchUpWorker` 每分钟扫 `player_projection JOIN player_state_heads`，筛掉在线（`player_runtime_leases` 有效租约）与已有未完结任务的玩家，做全量加载后 `EnqueueAsync` 入队。入队有确定性 `job_id` + 部分唯一索引 `ux_compute_jobs_open` 兜底（`PostgresComputeJobRepository.cs:88-163`）。
- **失效模式**：两个实例各扫一遍、各做一遍全量 LoadAsync——**重复劳动但不产生重复任务**（同 job_id 必有一方 `SkippedExisting`/`Refreshed`）。扫描 SQL 的 `NOT EXISTS player_runtime_leases` 条件已经是共享 PG 口径，天然不会把别实例在线玩家当离线玩家派出去。
- **演进路径**：P0 可选——`pg_try_advisory_lock` 抢一把"冷扫描"咨询锁，拿不到就本轮跳过，一行改动消掉重复扫描；不做也能正确运行，只是浪费 IO。其他后台 worker 的多副本口径已经安全（`section_flush_queue`/`integration_outbox` 都是 SKIP LOCKED；retention/receipt 清理是幂等 DELETE）。

### I. 市场跨分片结算（`MarketRuntime.cs`、`PostgresMarketRepository.cs`）

- **现状**：分片写者租约 + fence 已完整（启动抢占、10s 续租、失效自动重获重建，`MarketRuntime.cs:559-677`）；但 `Market:PartitionId` 是**静态配置**，且 `ClaimSettlementAsync` 在 `PartitionCount != 1` 时直接拒绝（`MarketRuntime.cs:235-236`）。`/api/v1/market/book` 对非本实例品种回 `WRONG_PARTITION`。
- **失效模式**：多实例下每个实例必须静态绑定不同分片——配置错一个就抢租约失败、`StartAsync` 40 次重试后抛异常拖垮整个进程（`MarketRuntime.cs:76-102`）；玩家请求落到不持有该品种的实例只能拿到 409/503；结算领取在分片数>1 时全灭。
- **演进路径**：
  - P1：分片认领改**动态**——实例启动/监管循环中对 `partition_leases` 里空闲分片逐个 `AcquireLeaseAsync`，抢到谁算谁（租约机制本来就支持）；`StartAsync` 抢不到租约不应致命，进入与写路径一致的"降级-重获"模式即可（机制已有，只差启动路径复用）。
  - P1：市场请求路由——`MarketPartitioner.GetPartition` 是确定性算法，任意实例都能算出品种属于哪个分片；再查 `partition_leases.owner_instance_id` + 实例目录转发，或让客户端按 `WRONG_PARTITION` 回执里的实例地址重定向。
  - P1：结算领取——`market.settlement_claims` 由成交分片写入，领取请求必须路由到该分片持有者，或改"玩家结算聚合表"让任意实例可领取。当前显式拒绝是安全兜底，演进时先保住这个 fail-closed 再开路由。
  - 衔接：市场租约 fencing 已经是多写者安全的标杆实现，多主服下**市场是唯一"本来就按多实例设计"的子系统**，缺的只是请求路由层。

### J. 节点多上游故障转移（`ComputeNode/UpstreamLinkWorker.cs`、`ComputeNode/Program.cs:66-81`）

- **现状**：`ComputeNode:UpstreamUrl` 单值 + 指数退避重连（1s→30s ±20% 抖动，健康会话断开后退避复位）。
- **失效模式**：单一公网域名下，边缘负载均衡已经解决"连到哪个主服"——节点重连自然落活实例，**单域名拓扑不需要节点侧改动**。只有将来出现多个入口域名/多线路（比如跨地域主服各自独立公网）时，单值 UpstreamUrl 才成为硬缺口。
- **演进路径**：P2——`ComputeNode:UpstreamUrls` 列表（轮询/优先级），配合 §4 的 drain 帧做"主服主动让节点换上游"；无公网约束不变，仍只拨出。

### K. 集群级容量视图

- **现状**：`OpsPanel`（Development-only，单进程视图）、`SessionRegistry.Count`、`registry.Nodes`、各 `Worker` 日志——全是本实例口径。`/api/v1/metrics` 同理（`Program.cs:450-459`）。
- **演进路径**：P1——`platform.server_instances` + `platform.compute_nodes` 两张心跳表：实例启动写入、~10s 心跳刷新、停止时删除 + 孤儿行按心跳过期清理。`compute_nodes` 行由持有该节点链路的实例在 register/unregister/容量变化时 upsert。**读侧零锁直接读 PG**，OpsPanel/运维脚本查表即得全集群在线玩家、会话、节点容量、市场分片归属、各实例版本指纹。
- 衔接：§E 的判死口径、§G 的玩家路由、§I 的市场路由都消费同一份目录，建一次表三处受益。

### L. 其他顺带项

| 项 | 现状 | 多主服下 | 处理 |
| --- | --- | --- | --- |
| ASP.NET 限流 | 进程内固定窗（auth 12/min/IP、command 令牌桶 120/60s/玩家，`Program.cs:61-83`） | 配额 ×实例数 | P2：接受或改 PG/边缘限流 |
| 会话认证缓存 | 3s 内存缓存，登出/改名本地失效 | 跨实例注销最长滞后 ~3s | P2：可接受，或用 NOTIFY 失效 |
| schema bootstrap | `PostgresSchema.EnsureAsync` 启动建表 | 两实例同时首启可能并发 DDL 竞态 | P0 部署纪律：首启单实例建表；或 bootstrap 只开一个实例 |
| `/readyz` 市场检查 | 含市场租约就绪 | 无分片实例会 unready | P1：动态认领落地后，"未持分片"应算 ready-but-degraded 而非 unready |

## 4. 优雅排空（drain）设计

> 本次已落地协议与双侧行为（compute-link.v1.3）；多主服扩展语义在 §4.4。

### 4.1 三种排空场景

1. **节点排空（节点发起）**：odin 重部署/息屏维护前，节点告诉主服"别派新活了"→ 主服立刻停派 → 节点把手头在飞批次跑完、结果回传 → 主动断链。**收益**：在飞批次不再被白白杀掉重算，任务不用等租约超时（默认 2 分钟）。
2. **主服排空（主服发起）**：主服停机/滚动升级 → 给所有在线节点发"我要走了"→ 派发器给在飞批次一段有界窗口照常收结果落库 → 窗口外按既有孤儿租约语义退回。**收益**：CCR 每次发版不再丢弃正在飞行的批次计算（原来直接孤儿化、等租约过期再重派）。
3. **节点转移上游（多主服扩展）**：drain 帧即"这个上游要走了"的显式信号 → 节点完成后主动断链快速重连（单主服落回同一域名=新实例；多主服经边缘落到别的实例）。

### 4.2 协议帧（compute-link.v1.3，已落地）

只加两帧、不带 ack——排空完成由"节点主动关链"表达，底层正确性永远由任务租约兜底：

| type | 方向 | 字段 | 语义 |
| --- | --- | --- | --- |
| `draining` | 节点 → 主服 | `reason?` | "我在排空：停止派新批次；在飞结果照常回收；我随后关链" |
| `drain` | 主服 → 节点 | `reason?` | "我在排空/即将下线：不再会有新批次；完成在飞后请断开重连" |

- 版本协商照旧 hello 精确匹配：v1.2 节点拨 v1.3 主服握手被拒，需同步升级（与 v1.1→v1.2 同一策略）。
- 反向帧（节点发 `drain`、主服发 `draining`）按既有"未知/不接受帧类型"判协议违规断开。
- 帧是**提示语义不是正确性来源**：丢了 drain 帧=退化为现状（断线→租约超时重派），不会错。

### 4.3 双侧行为时序（已落地）

```text
节点排空（odin 停机/重部署）：
  节点 stoppingToken → 出站队列入 draining 帧 → 主服标记 node.Draining
      → registry.Acquire() 立即跳过该节点（Available=0）
  节点继续在飞批次 → 结果照常回传（Pending 等待方正常了结）
      → 在飞清空 + 出站冲刷后 session.Cancel → 关链 → 主服 Unregister（剩余等待方按既有断线路径了结）

主服排空（停机/滚动升级）：
  ComputeDispatcherWorker 停机收尾 → registry.DrainAll：
      全部节点标 Draining + Outbound 投递 drain 帧（WebSocket 在托管服务停止阶段仍存活，帧能送达）
  在飞派发任务改用独立 _dispatchCts（不再随 stoppingToken 立即取消）
      → 有界窗口（12s）内节点结果照常 commit 落库
      → 窗口耗尽 _dispatchCts.Cancel → 未完结租约按孤儿语义到期重取（= 今天的行为兜底）
  节点收 drain → 跑完在飞 → 主动关链 → 退避复位立即重连（落到新实例/新主服）
```

### 4.4 实施清单：现在做 vs 多主服做

| 项 | 内容 | 分期 |
| --- | --- | --- |
| 协议帧 `drain`/`draining` | 双侧契约 + codec + 对齐门禁 | ✅ 本次已落地 |
| `ComputeLinkNode.Draining` + `Acquire` 过滤 | 排空节点立即停派 | ✅ 本次已落地 |
| 端点 `draining` 帧处理 | 标记 + 唤醒派发循环 | ✅ 本次已落地 |
| `registry.DrainAll` + 派发器排空窗口 | 停机时通知节点 + 在飞批次有界窗口照常落库 | ✅ 本次已落地 |
| 节点 `drain` 帧行为 | 完成在飞 → 主动断链 → 退避复位重连 | ✅ 本次已落地 |
| 节点停机 `draining` | 停机时先告知主服再排空在飞 | ✅ 本次已落地 |
| 玩家连接层 drain | 主服停机时给在线玩家发通知帧 + actor 卸载即释放租约（`RemoveAsync` 路径已天然释放），客户端重连迁移 | P1（玩家层 drain 属于路由配套，不归本次） |
| 节点多上游列表 + drain 携带转移 hint（redirect/reconnectAfter） | 多入口拓扑才需要 | P2 |
| drain 时联动 `platform.compute_nodes` 集群视图摘除 | 视图表落地后顺带做 | P1 |

## 5. 演进顺序总表

| 阶段 | 目标 | 内容 |
| --- | --- | --- |
| **P0 单主服多副本就绪** | 滚动重启/主备切换不丢东西、可灰度 | ✅ drain 帧与双侧排空（本次）；NodeBuildingRegistry 写下沉 PG 事务（修贡献丢更新）；ColdCatchUpWorker 可选咨询锁；部署侧多副本 compose + Caddy 多 upstream + `lb_policy cookie` 粘滞预案 |
| **P1 真多主服前置** | 多实例功能正确 | `platform.server_instances` + `platform.compute_nodes` 心跳表；玩家路由（粘滞优先、实例目录必备）；互踢/存在感/聊天扇出/设备配额跨实例化；市场动态分片认领 + 请求路由 + 跨分片结算领取；斗法跨实例撮合；集群容量视图；判死口径接集群视图 |
| **P2 多主服运营形态** | 体验与容量 | 节点多上游列表；限流跨实例口径；会话缓存 NOTIFY 失效；drain 转移 hint；集群容量视图进运维面板 |

## 6. 明确不做的事

- **不引入新基础设施**：不引 Redis/etcd/Kafka/服务发现——共享 PG + `LISTEN/NOTIFY` + 既有租约族（玩家、市场、任务）足够覆盖当前规模目标（2c4g/40g、500+ 在线）。
- **不让节点监听任何端口承担生产职责**：所有"主服找节点"的需求都必须通过节点已建立的 compute-link 下发（drain 帧即范例）；节点本地 HTTP 口永远是调试口。
- **不为了多主服改变游戏语义**：战斗结果、掉落、撮合、队列、快照语义不因路由/分片改变；玩家单写者永远是"聚合 + ECS 分片单写者 + 租约"三层。
- **不做双写/灰度读兼容层**：按 AGENTS.md 本地开发阶段规则，结构演进直接切换，不留并行期。
