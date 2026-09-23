# 道化太初 · 商业级持久化最终架构（清零历史包袱）与 Redis 选型决策

> **文档状态**：目标架构基线（2026-08-27）。
>
> **权威性**：本文件定义本次一次性 clean-room 重写采用的持久化、恢复、容量和中间件边界。现有代码、旧物理表、旧快照载荷、旧 outbox、旧市场账本、旧 API/WebSocket 字段和旧部署拓扑均不是本方案的兼容约束；实现直接建立当前 **schema v4**（`PostgresSchema.Version = 4`）、清空本地内测数据并删除旧模型。
>
> **重要说明**：本文是从业务不变量、故障模型、容量预算和运维成本重新推导出的方案。若最终选出的 PostgreSQL、.NET 或单写者模型与当前代码碰巧相同，只代表它们在约束下仍是最优解，不代表继承了旧表、旧载荷或旧调用链。项目既定的层级边界（`Domain` 纯领域、`Protocol` 纯协议、`Server` 宿主、`ComputeNode` 只做冷挂机补算）属于产品硬约束，不是历史兼容层。

---

## 一、最终结论

### 1. 一句话结论

采用“**应用内单写者热状态 + PostgreSQL 唯一权威持久化 + 分区快照 + 资产/市场关系模型 + latest-only 刷盘队列 + 事务 Outbox + 远端归档**”的架构；基线部署**不安装 Redis**。

### 2. 明确禁止的三种形态

| 形态 | 结论 | 原因 |
| --- | --- | --- |
| 每个玩家一张物理表 | 禁止 | 表数量、DDL、锁、vacuum、备份和运维成本随玩家数增长，无法形成稳定的共享索引与分区策略。 |
| 一个玩家一行、每次变化都重写完整巨型 Blob | 禁止作为最终形态 | 任意小变化都会放大序列化、网络、WAL、TOAST 和写放大；查询、局部更新和演进边界都很差。 |
| Redis 作为玩家、资产、市场或可靠队列的真源 | 禁止 | 会把关键业务拆成两套状态系统，无法用一个事务同时证明资产、订单、流水和副作用的一致性。 |

### 3. 允许的形态

- 玩家数据使用共享表；一名玩家对应若干行 section，而不是一张物理表。
- 在线玩家完整聚合保存在所属分片的内存中，数据库保存可恢复的 section 状态。
- 高频变化只更新内存和 WebSocket；按合并后的 section 定时写回，不记录逐 Tick 历史。
- 资产、冻结、订单、成交、幂等键和外部副作用在 PostgreSQL 同一事务内提交。
- 所有可重建数据（盘口投影、排行榜投影、目录缓存、短期存在态）都不能反过来成为权威真相。

---

## 二、目标、边界与非目标

### 1. 必须满足的目标

| 目标 | 设计要求 |
| --- | --- |
| 数据正确性 | 资产不能双花；订单冻结与释放可证明；成交和流水不可篡改；重复请求幂等。 |
| 在线性能 | Tick 和普通状态变更不等待数据库；普通命令 P95 < 100ms、P99 < 250ms；关键资产事务 P95 < 150ms、P99 < 500ms。 |
| 恢复能力 | 进程崩溃后可从最近 section 快照和待执行行动重建；资产/市场提交点 RPO 为 0；纯模拟状态 RPO 不超过 15 秒。 |
| 容量 | 在 `2c4g/40g` 主机上支持 5000+ 注册账号、100+ 持续收发、500+ 峰值收发和 2000+ 冷挂机补算。 |
| 硬盘 | 不保存 WebSocket 帧、聊天消息、心跳、战斗 Tick、逐请求日志或每次状态变化历史；本地日志、WAL、队列和归档都有硬上限。 |
| 扩展性 | section、资产、市场和外部副作用有独立边界；未来可以增加 section 或独立读模型，不需要重写整张玩家表。 |
| 兼容性 | 当前内测直接采用 schema v4 和新协议；不实现旧数据双读、双写、legacy DTO、fallback 或兼容分支；既有库的就地升级仅由 `migrations/` 独立 SQL 脚本人工显式执行。 |

### 2. 明确不在本次持久化范围的内容

- 不把运行时 Entity、Fiber、Timer、Channel、委托、缓存索引或网络连接写进存档。
- 不把“日志系统”误当作游戏状态系统。
- 不用 Redis、Kafka、RabbitMQ、Elasticsearch 或本地 Loki/Prometheus 组成额外关键路径。
- 不承诺将所有历史游戏行为逐事件重放；纯模拟通过时间戳、行动意图和确定性规则快进。
- 不把远程 ComputeNode 当作数据库副本或第二个权威服务。

---

## 三、从零推导的技术栈

| 层 | 最终选择 | 选择理由与边界 |
| --- | --- | --- |
| 操作系统 | Ubuntu Server 24.04 LTS，linux/amd64 | 长期安全更新、容器生态成熟；目标镜像不携带运行数据。 |
| Web/宿主 | .NET 10 / ASP.NET Core 10 / C# | 使用现代 WebApplication、内置健康检查、限流、ProblemDetails 和 Hosted Service；业务逻辑不放在 Endpoint 中。 |
| 运行时所有权 | `PlayerAggregate` 聚合 + `PlayerRuntime`/`PlayerActor` + `SemaphoreSlim` 单写者 | 同一玩家的命令与模拟推进在专属 Actor 内串行执行；`YuoCore` 基座引用保留待用，当前不构成运行时对象模型。 |
| 在线并发模型 | 每个玩家一个单写者执行上下文，跨边界不直接持有聚合引用 | Tick 路径零数据库往返；同一玩家和同一市场分片只有一个逻辑写者。 |
| 主数据库 | PostgreSQL 18.x（固定受支持的最新补丁版） | 事务、唯一约束、外键、行锁、分区、WAL、PITR 和成熟备份工具同时满足权威数据与小规模运维需求。 |
| 数据访问 | Npgsql 10.x；热路径使用显式 SQL、批处理和 prepared statement | SQL、锁顺序、SKIP LOCKED、版本 CAS 和查询计划可审计；不让 ORM 在战斗/资产热路径隐式生成写放大。 |
| 身份管理 | ASP.NET Core Authentication；需要完整账号生命周期时使用 Identity + PostgreSQL | 身份和权限使用独立 schema；游戏状态不依赖登录会话缓存。 |
| 公共 HTTP | JSON + OpenAPI + ProblemDetails，/api/v1 | 浏览器、运营工具和第三方接入易调试；不暴露 Entity 或数据库模型。 |
| 浏览器实时通道 | WebSocket + DHW1 二进制 envelope + JSON payload | 固定 36 字节头、明确协议版本和单调序列；只传当前状态和增量，不保存帧历史。Protobuf 编码位保留但当前浏览器不启用。 |
| 内部服务通道 | 内部 HTTP + `X-Compute-Token` | 仅用于 Server ↔ ComputeNode 等受控服务；ComputeNode 没有生产数据库写权限。需要 mTLS 时在独立网络边界启用，不把证书逻辑塞入冷挂机节点。 |
| 前端 | React 19 + Vite 7 + TypeScript 5.9 | 正式入口只有 frontend；静态资源由同源 ASP.NET/Caddy 提供。 |
| 反向代理 | Caddy 2.x | HTTPS、WebSocket、压缩、安全响应头和 debug/metrics 路径拦截。 |
| 观测 | OpenTelemetry .NET + OTLP 发送到远端平台 | 本机只保留极小错误日志；指标、追踪和长期检索不占用 40GB 数据盘。 |
| 备份 | pgBackRest + PostgreSQL WAL 归档到 COS/S3 兼容对象存储 | 本机不堆积多份完整 dump；支持 PITR 和定期恢复演练。 |
| 压缩 | 应用层 Brotli（小载荷不压缩，默认 Fastest） | 使用 BCL 能力，不增加额外 native 依赖；记录原始/压缩大小和校验值。 |
| 基线中间件 | **不部署 Redis**，也不部署 Kafka/RabbitMQ/Elastic | 当前负载没有不可替代的跨实例临时状态需求；减少内存、磁盘、故障域和备份链路。 |

---

## 四、总体拓扑与权威边界

~~~text
浏览器
  │ HTTPS / WebSocket
  ▼
Caddy
  │ 同源转发
  ▼
daohua-api（ASP.NET Core）
  ├─ 玩家分片运行时（单写者内存聚合）
  ├─ WebSocket 状态投影（仅内存）
  ├─ 资产/市场事务服务（Npgsql）
  ├─ latest-only section 刷盘提交器
  └─ 内部 HTTP/Token 客户端 ─► ComputeNode（只做确定性冷挂机补算）
              │
              ▼
        PostgreSQL 主库
          ├─ identity
          ├─ game
          ├─ economy
          ├─ market
          └─ platform
              │ WAL / pgBackRest
              ▼
        远端 COS/S3（备份与冷归档）

OTel/结构化日志 ───────────► 远端观测平台
~~~

### 权威性规则

1. Server 是行动、战斗、市场和资产结算的唯一权威来源。
2. 内存聚合是在线期间的即时权威；它必须能由 PostgreSQL 快照和确定性补算恢复。
3. PostgreSQL 是跨进程、跨重启和资产审计的唯一 durable 真源。
4. 盘口缓存、排行榜、HTTP ETag、目录缓存和 Redis（若未来引入）都只能是派生数据。
5. ComputeNode 只返回确定性补算结果，不能写生产数据库，不能处理在线连接、鉴权或市场提交。

---

## 五、数据库逻辑分区与共享表模型

所有表都是共享表，按业务域和生命周期组织；**不存在“每个玩家一张表”**。高频变化表只保留当前值或最新待写值，追加历史表按时间分区并定期归档。

### 1. identity：账号、玩家身份与安全审计

| 表 | 关键字段 | 约束与用途 |
| --- | --- | --- |
| identity.accounts | account_id, login_normalized, password_hash, status, created_at | 登录名唯一；不把玩家运行态塞进账号表。 |
| identity.players | player_id, account_id, display_name_normalized, created_at, last_seen_at | account_id 外键；道号唯一索引；允许一个账号未来拥有多个角色。 |
| identity.auth_sessions | session_id, account_id, expires_at, revoked_at, last_used_at | 只保存可撤销会话元数据和哈希，不保存访问令牌明文。 |
| identity.data_protection_keys | key_id, xml_payload, created_at, expires_at | 供多实例 Cookie/Data Protection 使用；访问权限限制在 API/Worker 数据库角色，磁盘与备份由基础设施加密。 |
| identity.audit_events | event_id, actor_id, event_type, target_id, payload, occurred_at | 普通表（非分区）；由登录、改名等安全敏感操作与管理员/资产争议事件稀疏写入；retention 按保留期小批量清理。 |

### 2. game：可恢复玩家状态

| 表 | 关键字段 | 约束与用途 |
| --- | --- | --- |
| game.player_state_heads | player_id, state_version, schema_version, last_simulated_at, game_data_fingerprint, checksum, updated_at | 每个玩家一行；保存当前提交头和恢复元数据，不保存巨型载荷。 |
| game.player_state_sections | player_id, section_kind, section_version, schema_version, codec, payload, raw_bytes, stored_bytes, checksum, updated_at | 主键 (player_id, section_kind)；每个 section 一行；只保留最新版本。 |
| game.player_action_queue | player_id, slot, action_id, action_payload, action_version, status, next_due_at, idempotency_key | 只保存当前待执行/进行中的行动；完成后删除或转为最新状态，不记录每个 Tick。 |
| game.player_runtime_leases | player_id, owner_instance_id, lease_until, renewed_at | 玩家运行时所有权租约：标识哪个宿主实例持有该玩家的 `PlayerActor` 单写者；命令执行前续租校验，防止多实例并发写同一聚合。 |
| game.player_projection | player_id, realm_rank, current_node_id, presence, last_active_at, projection_version | 供在线状态展示、玩家列表、运营检索与冷挂机筛选的可重建读模型投影；由 section 刷盘提交事务（`PostgresSectionRepository.ApplyBatchAsync`）顺带 `INSERT ... ON CONFLICT (player_id) DO UPDATE` 维护，不参与玩家真源提交，可随时从 section 重建。presence 由 settings section 解码而来：玩家卸载/断线路径先把聚合 presence 落为 offline 再强刷，使投影可区分在线与可冷挂机玩家。 |
| game.node_buildings | node_id, building_id, level, target_level, started_at_unix_seconds, contributed_materials, updated_at | 节点级**公共建筑**权威注册表（主键 `(node_id, building_id)`）：无建造者概念，同节点同种仅一座，全民可发起建造/填充材料/升级；材料进度以 jsonb 记录逐项已交数量，建造进度按 `min(材料%, 已耗时/本级工期)` 惰性推算，无需后台 tick。运行期由 `NodeBuildingRegistry` 进程内缓存 + 单一信号量串行化，玩家聚合内的建筑投影仅为最近已知副本（供离线/冷挂机补算沿用），不构成真源。 |

### 3. economy：资产与不可变账本

| 表 | 关键字段 | 约束与用途 |
| --- | --- | --- |
| economy.player_asset_balances | player_id, asset_id, variant, quantity, version | 可堆叠资源的当前余额；主键 (player_id, asset_id, variant)；数量为 double precision，领域层统一做非负和精度边界校验。 |
| economy.player_asset_instances | instance_id, owner_player_id, definition_id, state, version | 装备、唯一道具等不可堆叠实例；所有权转移必须在资产事务内完成。 |
| economy.transactions | transaction_id, idempotency_key, transaction_type, actor_id, created_at | 幂等键唯一；一笔业务结算只有一个提交点。 |
| economy.transaction_lines | transaction_id, line_no, player_id, asset_id, instance_id, delta | 主键 (transaction_id, line_no)；记录每个余额/实例的增减，禁止更新历史行。 |
| economy.reservations | reservation_id, order_id, player_id, asset_id, instance_id, quantity, status | 订单冻结的权威记录；取消、成交和过期都必须通过状态机变更。 |

> 游戏内资产按项目数值规约使用 double。如果未来接入真实货币充值，支付金额必须在独立支付域使用整数最小货币单位，不得复用游戏资产的浮点字段。

### 4. market：订单、分片租约与成交

| 表 | 关键字段 | 约束与用途 |
| --- | --- | --- |
| market.orders | order_id, partition_id, owner_player_id, side, asset_id, variant, price, quantity, filled_quantity, status, version, sequence, expires_at | 活跃订单规范化存储；盘口索引为 (partition_id, asset_id, variant, status, side, price, sequence)，另有活动订单到期索引；不保存整本订单簿 Blob。 |
| market.partition_leases | partition_id, owner_instance_id, fence_token, expires_at, renewed_at | 每个市场分片单写者租约；每次写入校验 fence token，旧实例即使网络恢复也不能继续写。 |
| market.trades | trade_id, partition_id, buy_order_id, sell_order_id, buyer_id, seller_id, asset_id, price, quantity, seller_tax, traded_at | 追加不可变流水；当前热表是普通表，按月分区属于归档表方案，不能把热表描述为已分区。 |

盘口内存投影可以在启动时从 market.orders 重建；投影损坏或清空不影响资产真相。只有订单冻结、成交、退款和手续费等事务成功后，内存盘口才推进对应版本。

2026-09-14 实现：每笔成交向卖方收取成交总额的 2%，以 `double` 点数存入 `seller_tax`，卖方收入为总额减税额，不取整。订单创建后 30 天到期，`expires_at` 持久化；启动恢复、每 10 秒维护与挂单/撤单前清理到期单。到期时以冻结记录实际余量退回灵石或物品，订单置 `expired`、冻结释放、退款流水在同一事务提交。退款失败保持原订单和冻结；重复处理使用绑定玩家与订单的幂等键。

成交立即入账；领取只保存 `market.settlement_claims` 确认游标，不再次转账。`deploy/market-ledger-archive.sh` 维护按月 RANGE 分区的归档表，热表与归档表应区分描述。

该结构使用 schema v4，一次性初始化新库；不提供旧版本（v1/v2/v3）的 migration、默认到期列或旧格式回填。部署到已有远程数据库前须独立完成备份与获准的数据替换，不能直接启动旧库。恢复归档时数据库 schema 与应用版本必须一致，启动会拒绝旧 schema；当前热表不因该变更转成分区表。v3 相对 v2 增加 `platform.compute_jobs` 租约字段（`lease_token`/`next_attempt_at`/`last_error` 与单开任务部分唯一索引 `ux_compute_jobs_open`，见 `migrations/0002_v2_to_v3.sql`）以及节点公共建筑表 `game.node_buildings`；v4 相对 v3 新增 `social` schema 与好友/队伍/队伍邀请/传音消息六张表（见 `migrations/0003_v3_to_v4.sql`）。

唯一装备实例交易尚未实现：现有 `economy.player_asset_instances` 与 reservation 的 `instance_id` 仅为 DDL 设计，当前交易按可堆叠资产和 variant 结算，不能宣称已支持唯一装备冻结、转移和退款。

### 5. platform：持久化工作与外部副作用

| 表 | 关键字段 | 约束与用途 |
| --- | --- | --- |
| platform.section_flush_queue | player_id, section_kind, state_version, payload, codec, attempts, next_attempt_at, lease_until, updated_at | 主键 (player_id, section_kind)；UPSERT 覆盖旧待写值，绝不积累高频历史。 |
| platform.integration_outbox | outbox_id, aggregate_type, aggregate_id, event_type, payload, occurred_at, attempts, visible_at, lease_until, lease_token, delivered_at, last_error | 只承载需要可靠投递的外部副作用（当前由注册、市场成交等业务事务同库追加生产）；`OutboxWorker` 按租约批量领取、投递成功标记 `delivered_at`、失败退回并按 `visible_at` 重试。 |
| platform.command_receipts | player_id, request_id, command, payload_hash, result_payload, state_version, created_at, expires_at | 持久化命令幂等回执：`(player_id, request_id)` 唯一，重放相同请求直接返回已存结果；由独立的 `CommandReceiptCleanupWorker` 按 `expires_at` 清理，不归 retention worker。 |
| platform.compute_jobs | job_id, player_id, target_time, status, input_payload, output_payload, lease_until, lease_token, next_attempt_at, last_error, attempts, updated_at | 冷挂机任务状态；status 取 pending/leased/succeeded/dead；同一玩家最多一个未完结任务由部分唯一索引 ux_compute_jobs_open 保证；成功任务短留，dead 任务保留诊断窗口，终态行由 retention worker 按 status+updated_at 清理。 |

主服务 → ComputeNode 冷挂机派发链已实现，链路为：

1. `ColdCatchUpWorker` 周期性扫描 `game.player_projection`（presence=offline、last_active_at 落在 48h 封顶窗口内、已过断线宽限、无存活运行时租约、无未完结任务），经 `IPlayerStateRepository.LoadAsync` 读取最新有效 section（含 durable 队列叠加），编码为 compute.v1 `AdvanceJobRequest` JSON 作为 input_payload 写入 `platform.compute_jobs`；job_id 由玩家与目标时刻确定性派生，重复入队走 upsert 去重。
2. 计算节点经 `compute-link.v1` **反向拨入**：节点主动 WebSocket 连接 `/internal/compute-link`（`X-Compute-Token` 鉴权 + hello 握手声明并发容量与配表指纹），握手通过即注册进 `ComputeLinkRegistry` 动态节点组；连接存活即在线，断开即热拔出。节点位于 NAT/防火墙后也可工作（odin 类设备只需求出方向连通），无需隧道或静态端点配置。
3. `ComputeDispatcherWorker` 先从注册表取剩余容量最大的在线节点（无节点则不领取，任务留 pending 不消耗 attempts），再以 `FOR UPDATE SKIP LOCKED` 批量领取到期任务（含租约过期的孤儿 leased 行），组装 `AdvanceBatchRequest` 经节点链路下发并等待同批次结果帧。
4. 每个 `AdvanceJobResult` 成功时经 `PostgresSectionRepository.ApplyComputeResultAsync` 落库：先锁提交头并要求 `state_version == SourceStateVersion`，玩家已上线或版本漂移则整体丢弃结果并记日志，绝不覆盖在线写者；随后更新 section 与提交头（同一加锁顺序，与刷盘 worker 无死锁环）。
5. 失败语义：节点返回 Retryable 失败或响应缺失按指数退避重试；载荷非法、输出污染等不可重试错误直接置 `dead`；节点断线或批次超时按租约令牌整批退回 pending；领取次数超过 `ComputeNode:MaxAttempts` 一律 dead。`ComputeNode:Enabled/Token` 未配置时派发器与扫描器安全停用，任务保留 pending，主服务不做内联补算；hello 配表指纹不匹配的节点被拒绝注册，防止旧版本节点按旧口径算新任务。

### 6. social：好友、队伍与传音

| 表 | 关键字段 | 约束与用途 |
| --- | --- | --- |
| social.friend_requests | request_id, requester_id, target_id, status, created_at, updated_at | 好友申请状态机（pending/accepted/rejected/cancelled）；`(requester_id, target_id)` 的部分唯一索引只约束 pending，防止同向重复申请堆积。 |
| social.friendships | player_id, friend_id, created_at | 双向好友关系（接受申请时同事务写两行，解除时删两行）；主键 `(player_id, friend_id)`，禁止自友。 |
| social.parties | party_id, leader_id, created_at | 队伍与队长；成员全部离开后由调用方删除空队记录。 |
| social.party_members | player_id, party_id, joined_at | 一名玩家最多属于一个队伍：`player_id` 直接作主键天然互斥；队伍删除时级联清理。 |
| social.party_invitations | invitee_id, party_id, inviter_id, created_at | 待处理队伍邀请，每名玩家最多一条（`invitee_id` 作主键）；接受/拒绝后清除，队伍删除时级联清理。 |
| social.chat_messages | message_id, channel, scope_key, sender_id, sender_name, body, sent_at | 传音消息（channel ∈ world/nearby/friend/party）；写入时同事务裁剪，**每个 `(channel, scope_key)` 作用域只保留最近 100 条**，无历史归档与分页接口。 |

作用域键口径：世界为空串（全局共享 100 条）、附近为地图节点 ID、好友为双方玩家 ID 升序拼接、队伍为队伍 ID。聊天与社交**不进入玩家聚合**，由独立单例运行时（`ChatRuntime` / `SocialRuntime`）串行处理，避免跨玩家写者锁互相阻塞。

---

## 六、玩家状态如何保存：完整聚合、分区落盘

### 1. 运行时不是“拆成很多互相抢锁的对象”

在线时，玩家由所属分片的单写者持有一个完整领域聚合。领域内部可以有清晰的概念切片，但不把每个字段都变成数据库表：

- progression：境界、熟练度和长期进度；
- world：位置、探索游标和世界状态；
- combat：当前战斗阶段、生命/灵力和固定战斗快照；
- buffs：当前生效 Buff 及其时间边界；
- loadout：装备实例引用、功法和快捷装配；
- settings：低频偏好；
- action_queue：当前行动队列，持久化到 game.player_action_queue；
- 资产余额、唯一装备实例和市场冻结**不放进上述 Blob**，统一由 economy 关系表管理。

因此“玩家数据是否一起保存”的答案是：**语义上由一个聚合统一提交，物理上按稳定边界分成少量共享表和 section 行**。不是每次变化都把全部玩家数据重写，也不是让每个小字段拥有自己的表。

### 2. Section 设计规则

1. section 数量控制在 6–8 个；只有独立生命周期、独立脏频率或独立恢复边界才拆分。
2. 每行只保留当前版本；不保留 section 历史链。
3. 当前实现 payload 使用 JSON；超过最小阈值才使用 Brotli `br-json` 压缩。载荷附带 schema_version、state_version、大小和 SHA-256 校验；DHW1 的 Protobuf codec 位只作为未来协议位保留。
4. section 载荷不包含运行时引用、数据库连接、缓存、日志和网络对象。
5. 新 schema 直接定义新基线；本地开发不读取旧版本、不双写旧版本、不提供 fallback。

### 3. 提交头与一致性

game.player_state_heads.state_version 是玩家的持久化提交头。一次刷盘批次包含一个单调递增的 state_version 和全部脏 section：

1. 玩家专属 `PlayerActor` 单写者在安全点生成独立载荷；
2. 将每个脏 section UPSERT 到 platform.section_flush_queue；
3. Worker 按玩家聚合领取队列行；
4. 在一个 PostgreSQL 事务中写入 game.player_state_sections、更新 game.player_state_heads；列表投影只作为后续可重建读模型，不参与玩家真源提交；
5. 只有当队列中的版本仍等于已应用版本时，才删除队列行；更新期间产生的新版本不会被旧 Worker 删除。

未变化的 section 可以保持较早的 section_version，因为它在逻辑上仍是同一份有效状态。一次领域命令同时改变多个 section 时，它们被同一批次提交，避免跨 section 的半提交状态。

### 4. 刷盘频率与强制边界

| 场景 | 保存策略 | 数据损失上限 |
| --- | --- | --- |
| 普通在线模拟 | 同一玩家同一 section 最多每 15 秒合并写一次 | 纯模拟状态不超过 15 秒 |
| 行动完成且产生资产 | 资产/流水立即事务提交；相关 section 进入同一 latest-only 队列安全点 | 资产 RPO 为 0；纯模拟 section 仍按 15 秒窗口 |
| 下单、撤单、成交、领取 | 订单、冻结、余额和流水同一事务；确有外部副作用时再同事务追加 outbox | RPO 为 0 |
| 登出、连接迁移、维护 | 先完成强制 section flush，再释放玩家所有权 | 正常关闭不丢脏状态 |
| 进程崩溃 | 从最近 section、行动队列和 last_simulated_at 确定性补算 | 只承担约定的 15 秒模拟 RPO |
| 数据库不可用 | 关键资产路径 fail-closed；普通模拟进入有界降级，队列超龄则停止新登录/风险操作 | 不静默吞掉持久化失败 |

### 5. 恢复流程

1. 读取 game.player_state_heads，验证 schema、指纹、校验和。
2. 读取全部 section 最新行和当前行动队列。
3. 读取资产余额、唯一实例和未完成冻结。
4. 以 last_simulated_at 为起点调用唯一确定性 AdvanceTo，补算到当前时间。
5. 若发现版本、校验或领域不变量不成立，拒绝载入并保留结构化错误证据；当前本地阶段直接修正/重置数据，不增加旧格式兼容代码。

---

## 七、资产、市场与幂等事务

### 1. 一个业务提交点

下列动作必须在**同一个 PostgreSQL 事务**内完成：

- 检查并写入 idempotency_key；
- 锁定并校验玩家资产余额或唯一实例；
- 创建/释放订单冻结；
- 更新订单状态和成交数量；
- 写入不可变 economy.transactions 与 transaction_lines；
- 写入 market.trades（如有成交）；
- 写入 platform.integration_outbox（如需要外部副作用）；
- 更新需要立即可见的玩家投影和状态版本。

事务提交成功后才向客户端确认成功；超时或连接中断时由幂等键查询最终结果，不能依赖客户端重试次数猜测状态。

### 2. 市场分片写者

- 每个 partition_id 只有一个逻辑写者。
- 租约通过 PostgreSQL 行和 fence_token 管理；续租失败或 fence 不匹配立即拒绝写入。
- 市场事务使用 Serializable 隔离和分片租约行锁与 fence 校验；成交锁定双方订单与冻结，余额变更和流水同事务落库。现有 `version` 记录修订次数，不是额外的 revision CAS 条件。并发序列化冲突由既有有界重试处理。
- 订单簿是内存投影；重启时由活跃订单和未完成冻结重建。
- 不用 Redis/Redlock 保护资产。分布式锁即使存在，也不能替代数据库约束和事务。

### 3. 幂等键规范

所有会改变资产、订单、任务或外部副作用的请求都必须携带服务端生成或校验的幂等键。唯一约束按业务域分组：

~~~text
identity:<account>:<operation-id>
economy:<player-id>:<client-command-id>
market:<player-id>:<order-command-id>
integration:<aggregate-id>:<event-id>
~~~

幂等记录只保留到业务结果可安全查询的期限；不把每个普通读取请求写入数据库。

---

## 八、队列、日志和消息：只保存必须保存的东西

### 1. Section 刷盘队列不是事件日志

platform.section_flush_queue 是 latest-only 状态队列：

- 同一 (player_id, section_kind) 只有一行；
- 新版本覆盖旧待写值；
- Worker 使用 FOR UPDATE SKIP LOCKED 分批领取；
- 写成功后按版本条件删除；
- 失败保留当前载荷、重试次数、下次时间和错误摘要；
- 队列长度和最老年龄有硬门禁。

因此 10 分钟内玩家产生 1000 次战斗变化，数据库不会得到 1000 条快照日志，而只会得到每个脏 section 的最新待写状态。

### 2. Outbox 只记录外部可靠副作用

允许进入 platform.integration_outbox 的例子：

- 支付平台回调；
- 邮件或站内信服务调用；
- 跨服务账户通知；
- 必须重试且不能丢失的管理动作。

禁止进入 Outbox 的例子：

- 每个 WebSocket delta；
- 每个战斗 Tick、伤害数字或心跳；
- 普通 HTTP 访问日志；
- 仅用于前端展示的滚动消息；
- 已经能从 section 和时间戳重建的模拟中间态。

### 3. 日志与消息留存

| 数据 | 本地策略 | 远端策略 |
| --- | --- | --- |
| 应用结构化日志 | 仅错误、警告和采样信息；单机总量硬上限约 100MB | OTLP/日志平台按环境保留 7–30 天 |
| 审计事件 | 安全、管理员和资产争议事件；热留 30 天 | 压缩归档 365 天或按合规要求 |
| WebSocket 消息 | 只在前端内存环形缓冲保留最近 100 条用于诊断 | 不归档 |
| 聊天/滚动消息 | 本项目基线不持久化 | 若未来成为玩法，再单独设计容量与合规策略 |
| 战斗 Tick/伤害事件 | 不持久化 | 由确定性状态重算 |
| Trace | 本地不落盘；正常采样 1%，错误/慢请求 100% | 远端短期保留 |
| Metrics | 不写本地数据库 | 远端指标平台 |

实现中的 `PostgresRetentionWorker` 每小时以小批量清理六类数据：已投递 outbox、过期/已撤销会话、旧审计事件、已结束冷挂机任务、过期 Data Protection key、过期 `market.settlement_claims` 领取游标。过期命令回执 `platform.command_receipts` 由独立的 `CommandReceiptCleanupWorker` 按 `expires_at` 清理，不归属 retention worker。它不删除余额、订单、成交或未投递 outbox；`market.trades` 作为权威业务流水按备份/远端归档流程保留，不能把成交流水当作普通日志随意清空。

---

## 九、硬盘、内存和数据库容量门禁

### 1. 40GB 本地盘预算

| 项目 | 硬上限/目标 | 处理原则 |
| --- | ---: | --- |
| PostgreSQL 数据与索引 | ≤ 12GB | 超过 60% 预警；优先归档流水和清理派生数据，不删除权威当前状态。 |
| PostgreSQL WAL | ≤ 2GB | 设置 checkpoint、归档告警和保留上限；WAL 归档失败时提高告警等级。 |
| 备份暂存 | ≤ 1GB | 本地只保留正在上传/校验的少量文件，长期备份在 COS/S3。 |
| Docker 镜像与层 | ≤ 6GB | 定期清理未引用层；镜像不得携带 data、快照或源码。 |
| 应用/容器日志 | ≤ 100MB | 超限立即轮转并告警，禁止无限增长。 |
| 临时文件与压缩工作区 | ≤ 1GB | 任务完成后删除；失败保留最小诊断信息。 |
| 系统与安全余量 | ≥ 18GB | 任何清理策略都不得侵占这部分余量。 |

磁盘占用达到 60% 预警、75% 严重、85% 保护模式。保护模式下暂停非关键归档和新批量任务，但不删除权威快照、资产余额、订单或流水。

### 2. PostgreSQL 基线参数方向

实际值必须用目标镜像和真实数据压测确认，不能把示例值当成容量承诺：

~~~text
max_connections = 50
shared_buffers = 512MB
effective_cache_size = 2GB
work_mem = 4MB
maintenance_work_mem = 128MB
fsync = on
full_page_writes = on
synchronous_commit = on
wal_compression = on
checkpoint_timeout = 15min
checkpoint_completion_target = 0.9
max_wal_size = 1GB
autovacuum = on
~~~

高频队列表和按月流水表分别设置合理的 autovacuum 参数；不要用全局激进 vacuum 把 CPU 和 WAL 打满。

### 3. 写放大控制

核心控制量是：

~~~text
section 写入量
  = 脏玩家数 × 平均脏 section 数 ÷ 合并间隔

可靠事件写入量
  = 资产/市场/外部副作用的真实业务次数

禁止项
  = 逐 Tick 快照 + 逐消息日志 + Redis AOF 镜像 + 重复双写
~~~

在 500 名峰值在线玩家、15 秒 section 合并间隔下，写入量由“脏 section 数”决定，而不是由网络消息总数决定。最终门禁必须在真实 PostgreSQL、WAL 和 Worker 上测量，不以纯内存基准冒充生产容量。

---

## 十、故障处理与恢复策略

| 故障 | 系统行为 | 不能做的事 |
| --- | --- | --- |
| PostgreSQL 短暂不可用 | 资产/市场写入立即失败并可重试；普通模拟进入有界内存队列；队列超龄后停止新登录和高风险操作 | 不能假装成功、丢掉错误或继续无限积压。 |
| Section Worker 崩溃 | 队列行保留，另一 Worker 按 lease 到期后重新领取；版本条件防止旧 Worker 覆盖新状态 | 不能直接删除失败队列。 |
| API 进程崩溃 | 从最近提交头、section、行动队列和时间戳恢复；资产以事务结果为准 | 不能依赖进程内缓存作为唯一存档。 |
| 市场写者失联 | 租约到期后新实例取得更高 fence；旧实例写入被拒绝 | 不能只靠客户端或 Redis 锁判断所有权。 |
| 磁盘接近满 | 进入容量保护模式，暂停非关键写放大任务并报警 | 不能删除权威状态换空间。 |
| WAL/备份归档失败 | /readyz 反映降级，保留本地最小恢复窗口并立即报警 | 不能把“备份脚本执行过”当成可恢复证明。 |
| ComputeNode 不可用 | 在线服务继续运行；冷挂机任务保留 pending 按退避等待节点恢复，主服务不做内联补算 | 不能让远端节点直接写生产数据库。 |

每季度至少进行一次 PITR 恢复演练，并记录：目标时间点、恢复耗时、丢失窗口、校验行数、应用重连和市场重建结果。

---

## 十一、为什么最终基线不用 Redis

### 1. 决策不是“Redis 不商业”，而是“当前没有不可替代职责”

Redis 本身是成熟的商业级组件，适合做跨实例缓存、短 TTL 状态、限流计数、存在态和非权威广播。当前基线不部署 Redis，是因为本项目已经有更直接的路径：

- 在线玩家热状态在所属单写者内存中，少一次网络 RTT 和一次序列化；
- PostgreSQL 同时提供资产事务、唯一约束、行锁、WAL、备份和 PITR；
- 单实例阶段不需要跨实例广播、共享限流计数或共享会话缓存；
- 盘口和目录可以由内存投影、ETag、压缩和 PostgreSQL 索引承担；
- 40GB 磁盘和 4GiB 内存更需要留给应用、PostgreSQL、WAL、系统和故障余量。

“把常见中间件全装上”不是商业级；每个组件都应有不可替代的职责、独立的故障恢复方案和可验证的 SLO 收益。

### 2. Redis 会复制一份热状态并增加跳数

如果玩家状态同时存在应用内存、Redis 和 PostgreSQL，就会形成：

~~~text
应用聚合
  ├─ Redis 序列化副本
  └─ PostgreSQL 持久副本
~~~

每次读写都可能多出网络调用、序列化和版本协调；三份状态发生分叉时，必须再定义谁是真相、谁回源、谁修复。对于当前 100+ 持续连接、500+ 峰值收发和 5000+ 账号规模，没有测量证据表明这次跳转能降低尾延迟。

### 3. Redis 事务不等于关系数据库事务

资产扣减、订单冻结、成交、退款、幂等键、流水和 Outbox 需要同一提交点。Redis MULTI/EXEC 可以让命令序列连续执行，但不提供 PostgreSQL 的外键、唯一约束、跨表查询和事务回滚语义；把规则塞进 Lua 又会引入第二套脚本运行时和审计面。

最危险的是双写：

~~~text
Redis 成功，PostgreSQL 失败
PostgreSQL 成功，Redis 失败
请求超时，但一端已经提交
进程崩溃后无法判断哪一端是真相
~~~

本方案把业务状态、资产流水和必要 Outbox 放在同一个 PostgreSQL 事务内，避免把一致性问题变成分布式协调问题。

### 4. Redis 持久化不能替代 PostgreSQL 的 WAL/PITR

Redis 官方文档说明：

- RDB 是时间点快照，快照间的写入可能丢失；
- AOF everysec 在故障时可能损失约一秒写入，always 则把 fsync 成本放到写路径；
- RDB/AOF rewrite 需要 fork、Copy-on-Write 和临时空间；
- AOF 还需要周期性 rewrite。

如果把高频快照或消息写入 AOF，就会得到第二条磁盘写放大链路；如果只开 RDB，又不能提供资产结算需要的零丢失提交点。Redis 的持久化选项是缓存/内存数据库的运行取舍，不是对 PostgreSQL 事务备份的替代。

### 5. 4GiB 内存和 40GB 磁盘不适合堆叠第二个状态数据库

Redis 的内存不止 payload：

- key/value 对象和哈希表；
- 过期字典；
- allocator 碎片；
- 复制缓冲；
- AOF/RDB 缓冲；
- fork 期间的 Copy-on-Write 增量。

当 maxmemory 到达上限时，noeviction 会拒绝新写入，其他策略会淘汰 key。权威玩家状态、资产、订单和可靠任务既不能被淘汰，也不能因为缓存满而拒绝提交，因此它们本来就不应该放进 Redis。

### 6. Redis 消息模型不匹配可靠结算

Pub/Sub 是 at-most-once：订阅者断线或处理失败后消息不会补发。Streams 能提供消费组和保留，但如果业务状态在 PostgreSQL、消息在 Redis，仍然存在跨系统双写窗口，无法替代同库事务 Outbox。

section 刷盘使用 latest-only 队列即可：

- 新状态覆盖旧待写状态；
- Worker 用 FOR UPDATE SKIP LOCKED 领取；
- 失败保留当前版本；
- 成功按版本删除；
- 不产生逐变化日志洪水。

### 7. Redis 锁不适合保护资产真相

租约锁受网络延迟、进程暂停、锁过期和故障切换时序影响。市场写入真正需要的是“写者边界 + fence token + 数据事务 + 版本校验”，这些都由 PostgreSQL 在权威数据旁边完成，证明路径比跨 Redis/PostgreSQL 更短。

因此不使用 Redis/Redlock 保护：

- 玩家资产；
- 市场分片；
- 订单冻结；
- 账本流水；
- 持久化队列。

### 8. 职责矩阵

| 职责 | 最终归属 | Redis 结论 |
| --- | --- | --- |
| 在线玩家运行态 | 应用进程单写者内存 | 不需要共享副本 |
| 玩家 section 快照 | PostgreSQL | 需要 WAL、CAS、备份 |
| 资产余额/实例/冻结 | PostgreSQL | 必须同事务 |
| 订单/成交 | PostgreSQL + 内存盘口投影 | Redis 不能成为账本 |
| 外部可靠事件 | PostgreSQL transactional outbox | Pub/Sub 不可靠 |
| 刷盘任务 | PostgreSQL latest-only queue | 不需要 Stream 历史 |
| WebSocket 消息 | 内存 | 不保存帧 |
| 目录/盘口读缓存 | 进程内不可变缓存、ETag、PostgreSQL 索引 | 当前规模无收益证明 |
| 单实例限流 | ASP.NET Core 内置限流 | 不需要共享计数器 |
| 账号/授权 | Authentication + PostgreSQL | Redis 不是真源 |
| 跨实例存在态/广播 | 当前没有 | 未来可评审 Redis |

---

## 十二、未来何时允许引入 Redis

“不用 Redis”不是永久禁止，而是把准入条件写成可测量的工程门槛。以下条件必须同时满足，不能因为“以后可能用到”提前安装：

1. PostgreSQL 已完成索引、SQL、批量、连接池、分区和 autovacuum 优化，但真实目标压测连续多轮仍显示：
   - 数据库 CPU 持续超过约 70%；或
   - 连接池利用率持续超过约 80%；或
   - 普通命令 P95/P99 超过既定 SLO；或
   - 高读低写查询的数据库 CPU 已成为明确瓶颈。
2. 服务扩展到多个 API 实例后，确实出现跨实例的临时协调、存在态、限流或广播需求，且应用内存和 PostgreSQL 无法以可接受尾延迟承担。
3. 目标查询真实命中率稳定超过 90%–95%，并且能定义 TTL、版本键、失效、回源、击穿和热 key 策略。
4. app、PostgreSQL、WAL 和操作系统压测后仍有至少约 1GiB 内存安全余量；Redis 设硬 maxmemory，所有缓存 key 都有 TTL。
5. Redis 全部数据被清空时，系统仍能从 PostgreSQL 重建；Redis 不可用时请求可以回源或降级，不影响资产提交。
6. 已准备独立运维方案：TLS/ACL、连接超时、碎片告警、容量告警、故障降级、恢复演练和独立故障域。

若满足门槛，优先使用托管或独立节点 Redis，不要在 2c4g 主机上和 PostgreSQL 强行共置。引入范围限于：

- 派生市场/排行榜读缓存；
- 分布式限流计数；
- 短 TTL 会话存在提示；
- 非可靠失效通知；
- 可完全回源的临时协调。

所有 key 必须带命名空间、用途、schema 版本、业务标识和 TTL。Redis 通知只能作为失效提示，消费者必须允许漏通知并从 PostgreSQL 重建。

### 永久禁止的 Redis 职责

- 玩家权威快照；
- 货币、物品余额和冻结；
- 订单、成交和不可变流水；
- 幂等记录；
- transactional outbox；
- 不可丢失的计算任务；
- 账号、授权和审计真相；
- 唯一市场写者的最终锁定依据。

---

## 十三、安全、协议与部署边界

### 1. 认证与授权

- HTTP、WebSocket 和管理入口都必须经过服务端身份认证与授权；客户端不能自行声明 player_id 取得权限。
- 内测账号密码长度统一限制为 6-24 个字符；密码仅以 PBKDF2 哈希、随机 salt 和迭代次数入库，绝不保存明文。
- Cookie 使用 HttpOnly、Secure、合适的 SameSite 策略；WebSocket 握手继承认证上下文。
- ComputeNode 使用内部 HTTP `X-Compute-Token` 和网络隔离；没有 PostgreSQL 凭据。生产若增加 mTLS，属于边界层增强，不改变节点无状态契约。
- PostgreSQL 角色按 `deploy/postgres-local-roles.sql` 拆分为 `daohua_bootstrap`（schema 所有者、不可登录）、`daohua_api`、`daohua_worker`、`daohua_archive`、`daohua_metrics` 五个 NOLOGIN 权限组，由本地独立登录角色继承；不存在 migration_admin，schema 变更只由 bootstrap 路径或人工迁移脚本完成。

### 2. 协议版本

WebSocket envelope 固定包含：

~~~text
protocol_version
message_type
codec
request_id
sequence
payload
~~~

section 载荷固定包含：

~~~text
schema_version
state_version
codec
raw_bytes
stored_bytes
checksum
payload
~~~

当前内测直接发布新协议和新 schema；未知版本拒绝，不添加旧字段回填或双轨 DTO。

### 3. 容器

- 多阶段构建，运行镜像不包含源码、SDK、Node、测试产物和 data。
- 固定非 root UID/GID，根文件系统只读，仅 /tmp 可写。
- 应用容器不挂载业务数据卷；唯一持久卷为 PostgreSQL 数据。
- Caddy 只暴露 HTTPS、HTTP API 和 WebSocket；生产拦截 debug/metrics。

---

## 十四、一次性落地清单（不是兼容迁移）

这是本次 clean-room 重写的一次性实施顺序，不保留旧路径：

1. 冻结本文档的 schema v4、section 编号、协议字段和事务不变量。
2. 新建 identity/game/economy/market/social/platform schema 与索引，清空本地旧数据。
3. 删除旧的整玩家 Blob、旧市场账本 Blob、旧 outbox 适配和旧协议对象；实现新的 section/资产/市场/queue/outbox 适配。
4. 在 `PlayerActor` 单写者安全点接入 section 编码和 latest-only queue；Worker 以版本条件提交和删除。
5. 实现资产/市场同事务、幂等键、fence token、审计和错误证据。
6. 接入 PostgreSQL WAL/pgBackRest/COS 归档，完成 PITR 和磁盘保护演练。
7. 运行领域确定性测试、数据库集成测试、崩溃恢复、重复请求、并发成交、队列堆积、磁盘接近满和 capacity-target 门禁。
8. 只有在上述门禁通过后，才把 schema v4 视为当前唯一生产基线；旧实现不再参与运行。

---

## 十五、验收门禁

至少通过以下真实边界验证后，才可称为商业级目标达成：

- 500 WebSocket 连接、100 持续收发、500 峰值收发；
- 5000 账号存储估算和随机读写；
- 2000 冷挂机一小时确定性补算；
- 300 次/秒代表性状态变化下，section 合并后 PostgreSQL/WAL/Worker 仍在预算内；
- 重复下单、重复成交回调、网络超时重试不产生重复资产；
- API 进程崩溃、Worker 崩溃、数据库重启后恢复结果与在线推进一致；
- 市场分片旧 fence 写入被拒绝，新写者可安全接管；
- 队列最老年龄 < 5 秒，失败时保留证据；
- 数据盘 < 70%，WAL < 2GB，日志 < 100MB；
- 备份校验和、PITR、恢复后订单/余额/流水计数一致；
- Redis 未部署时所有关键路径仍能独立运行；未来若启用 Redis，拔掉 Redis 只影响派生加速，不影响权威提交。

---

## 十六、Redis 决策记录（ADR）

**决策**：NO-REDIS-IN-BASELINE

**状态**：接受。

**理由**：当前规模下，Redis 没有不可替代职责；引入它会增加第二套状态系统、双写一致性、持久化写放大、内存竞争、运维和恢复成本。PostgreSQL + 应用内存 + latest-only 队列已覆盖权威数据、在线性能和小硬盘目标。

**重新评审触发器**：多 API 实例的跨实例临时协调、经过优化仍无法满足 SLO 的高读查询、可证明的高缓存命中率，以及足够的内存/独立运维预算。

**不可改变的约束**：Redis 即使未来引入，也只能是可丢失、可重建的派生层，不能成为玩家、资产、市场、幂等、Outbox、账号或审计真源。

---

## 十七、参考资料

- [PostgreSQL：Continuous Archiving and Point-in-Time Recovery](https://www.postgresql.org/docs/current/continuous-archiving.html)
- [PostgreSQL：Table Partitioning](https://www.postgresql.org/docs/current/ddl-partitioning.html)
- [Npgsql：Performance and Batching](https://www.npgsql.org/doc/performance.html)
- [ASP.NET Core：Hosted Services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)
- [ASP.NET Core：gRPC](https://learn.microsoft.com/en-us/aspnet/core/grpc/)
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/instrumentation/)
- [Redis：Persistence（RDB、AOF、rewrite）](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/)
- [Redis：Transactions（MULTI、EXEC、WATCH）](https://redis.io/docs/latest/develop/using-commands/transactions/)
- [Redis：Pub/Sub（at-most-once）](https://redis.io/docs/latest/develop/pubsub/)
- [Redis：Key Eviction（maxmemory 与淘汰）](https://redis.io/docs/latest/develop/reference/eviction/)
- [Redis：Streams](https://redis.io/docs/latest/develop/data-types/streams/)
