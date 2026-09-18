# 全面深度风险分析报告（2026-09-19）

> 六路并行只读审计汇总：B1 领域与协议、B2 服务端运行时、B3 ComputeNode/LoadTests/DataTool、B4 正式前端、B5 部署运维与脚本、B6 文档一致性。
> 所有结论均附 `文件:行号` 证据；证据不足处标注「待复核」。本报告为时点性证据库，不作为设计真源。

## 〇、执行摘要

全库共确认 **高风险 10 项、中风险 19 项、低风险 30+ 项**。最关键的三条主线：

1. **ComputeNode 冷挂机链路产出"假补算"**：节点不执行任何领域模拟，仅注入元数据即被服务端落库并推进 `last_simulated_at`，玩家离线窗口内的行动/修为/掉落收益被静默吞掉且不可恢复；同时注入键 `completedActions` 与 progression 真实字段碰撞，把累计完成行动数周期清零。生产默认启用，属持续数据损害。
2. **增量快照可靠性闭环缺失**：服务端 outbound 通道满时静默丢弃 SnapshotDelta 且基线已推进（不重发）；客户端不检测前向 gap，且 delta 解码异步竞态会用陈旧视图合并/整包丢弃——客户端状态可长期静默分叉。
3. **生产拓扑信任链断裂**：`ForwardedHeaders` 仅信任 loopback，容器内看不到 Caddy 的真实来源 IP → 认证限流退化为全站单桶、会话 Cookie 丢 `Secure`、审计 IP 失真；另 `simulation.advance` 接受客户端任意未来时刻可无上限刷取挂机收益。

---

## 一、高风险清单

### H-01【领域/数据损害】ComputeNode 补算为空转桩，`last_simulated_at` 被推进但收益零结算

- 证据：`src/ComputeNode/DeterministicBatchEngine.cs:143-169`（只注入 5 个元数据键，不触碰领域字段）；`src/Server/Clean/ColdCatchUpWorker.cs:91`（`Actions: null`）；`src/Server/Clean/PostgresSectionRepository.cs:578-594`（CAS 后 `last_simulated_at = TargetTime`、`state_version+1`）；`src/Server/Clean/PlayerRuntime.cs:392`（登录补算以 `aggregate.LastSimulatedAt` 起算）。
- 影响：启用链路的全部冷挂机玩家离线收益（行动完成/修为/掉落/旅途）永久丢失，违反"在线/离线/重连三路径结算一致"不变量；按"ComputeNode 无项目引用"约束该缺陷在当前架构下无法产出真结算。
- 建议：修复前将 `ComputeNode:Enabled` 置 false（冷挂机由登录补算兜底）；长期二选一——(a) 服务端落库前用 `SectionCodec.DecodeAggregate` + `SimulationEngine.AdvanceTo` 重放，节点降级为预取/审计；(b) 下沉可执行确定性结算契约到节点。

### H-02【领域/数据损坏】节点注入键 `completedActions` 覆盖 progression 真实业务字段

- 证据：`src/ComputeNode/DeterministicBatchEngine.cs:165,179`（恒注入 0）；`src/Domain/Player/PlayerStateDocument.cs:31-43`（`PlayerProgressionState.CompletedActions` 真实累计字段）；`src/Server/Clean/ServerOptions.cs:240-245`（camelCase + CaseInsensitive 反序列化绑定回字段）。
- 影响：每个 compute job 把玩家累计完成行动数清零，属权威数据损坏，诚实节点也必然触发。
- 建议：元数据改用不可能碰撞的命名空间（如 `__compute.*`）并断言键不存在；或改走 `AdvanceJobResult.Metrics` 回传，不污染领域载荷。

### H-03【安全/完整性】节点回写仅格式校验即落权威表，可注入任意玩家状态或永久卡死存档

- 证据：`src/Server/Clean/ComputeDispatchContract.cs:146-190`（`ToSectionWrite` 只查 kind 白名单/codec/Base64/≤4MB/checksum 形状）；`src/Server/Clean/ComputeDispatcherWorker.cs:226-243`（CAS 用节点回报的 `SourceStateVersion`，未核对与请求一致）；`src/Domain/Player/PlayerStateDocument.cs:266-421`（Restore 在下次加载才抛 `DomainException` → 玩家永久无法登录）。
- 影响：被控节点/令牌泄漏/`http://` MITM（`ServerOptions.cs:157-159` 允许 http）可伪造任意玩家进度（境界/资产/队列）或构造破坏 Restore 不变量的载荷造成持久单账号 DoS。"节点不持有权威状态"的边界在写入路径上实际不存在。
- 建议：落库前对每个 section 做领域解码+不变量校验（失败即 dead 不留写）；核对 `SourceStateVersion`/`TargetStateVersion==source+1`/section 集合一致；节点输出按"不可信提案"处理；远程部署强制 https/mTLS。

### H-04【经济作弊】`simulation.advance` 接受客户端任意未来时刻，可小步快进无限刷挂机收益

- 证据：`src/Server/Clean/PlayerRuntime.cs:1021-1026`（直接解析 `payload.targetTime` 无钳制）；`src/Domain/Simulation/SimulationEngine.cs:104,188-196`（>48h 才截断，小步进全额结算并跳时钟）。
- 影响：客户端每次 `targetTime=now+Δ` 反复调用即可预支无限收益；跳远未来后 `target<from` 还造成周期性推进报错（自我 DoS）。
- 建议：服务端钳制 `target = Math.Min(target, UtcNow)` 或直接忽略客户端值恒用 UtcNow。

### H-05【生产可用性/安全】ForwardedHeaders 仅信任 loopback → 限流全站单桶 + Cookie 丢 Secure + IP 失真

- 证据：`src/Server/Program.cs:84-88`（`KnownProxies` 仅 `IPAddress.Loopback`）；`:64-72`（auth 限流分区键=RemoteIpAddress）；`:482-493`（`Secure = Request.IsHttps`）；`deploy/compose.prod.yaml`（Caddy→docker 网桥 SNAT）。
- 影响：(a) 所有客户端共享 12 次/分 auth 限流桶，正常峰值即全员 429；(b) `IsHttps=false` → 会话 Cookie 生产环境不带 Secure；(c) 审计 IP 全为网关地址。
- 建议：`KnownNetworks` 加 docker 网桥网段或网关 IP 入 `KnownProxies`；补契约测试防回归。

### H-06【DoS】WebSocket 无 Hello 握手超时，pre-Hello 连接不受会话互斥约束

- 证据：`src/Server/Clean/WebSocketRuntime.cs:183-208`（accept 即建 session+订阅+64KB 缓冲，无握手截止）；`:331-339`（admission 仅 Hello 帧到达才执行）。
- 影响：单个合法账号可挂起上千连接耗尽内存/FD。
- 建议：accept 后启动握手倒计时（5-10s 未 Hello 即策略码关闭）；未握手连接设全局/单账号上限。

### H-07【资源放大】仅经 HTTP 路径加载的 PlayerActor 永不卸载

- 证据：`src/Server/Program.cs:273-288,318-345`（register/commands 均 `LoadAsync` 驻留）；`src/Server/Clean/PlayerRuntime.cs:331-339`（`RemoveAsync` 仅 WS 断开触发）；`:392-394`（创建即 Online）。
- 影响：纯 API/爬虫路径让 actor 永久驻留：presence 停留 Online 不被冷挂机回收、每拍持续 lease UPDATE 写放大、`_actors` 线性增长。
- 建议：无 WS 绑定的 actor 加空闲 TTL 驱逐（卸载前强刷）；注册路径不预载 actor。

### H-08【前端/状态一致性】SnapshotDelta 丢帧无 gap 检测 → 客户端状态静默分叉

- 证据：`src/Server/Clean/WebSocketRuntime.cs:258-259`（构建 delta 即推进基线）；`:470-477`（`TrySend` 满即静默丢弃且已消耗 sequence）；`frontend/src/network/gameClient.ts:295-296`（仅过滤 `sequence<=`，不查前向 gap）。
- 影响：被丢的 section/asset 变更永远不再补发，资产/队列/战斗状态可长期偏离权威且无提示；战斗高产出期正是高发期。
- 建议：服务端对 SnapshotDelta 丢帧 fail-fast 关连接（客户端经重连获全量）；或基线推进推迟到投递成功后；客户端对承载状态的帧做 gap 检测 resync（注意 LogBatch 丢帧共用序列空间，需区分）。

### H-09【前端/状态一致性】delta 解码异步竞态：陈旧视图合并 + 版本整包丢弃

- 证据：`frontend/src/network/gameClient.ts:313-318`（捕获 `current` 后 `await decode` 再 `applyPatch`）；`frontend/src/network/snapshot.ts:938,959`（以陈旧视图合并 inventory/queue）；`frontend/src/game/gameStore.ts:369`（`patch.version<=state.version` 整包丢弃）。
- 影响：相邻 delta 交错 → 后到的 patch 基于旧视图合并回滚先到的变更；或乱序应用后先到的被整包丢弃。与 H-08 同类的静默分叉。
- 建议：串行化 delta 解码+应用链；或把合并基座移入 `applyPatch` 用 `get()` 实时状态。

### H-10【部署】公网 smoke 与 Caddy `/readyz` 拦截自相矛盾，`deploy-prod.sh` 验收必然失败

- 证据：`deploy/xx.yuohira.com.caddy:10`（`/readyz` 公网 404）；`scripts/smoke-production.mjs:26-27`（`/readyz` 必过项）；`scripts/deploy-prod.sh:247-261`、`scripts/install-ccr-auth.sh:99-101`（公网跑该烟测，失败即非零退出）。
- 影响：每次发布最后一步报错退出，真实发布结果与验收口径脱敏。
- 建议：改烟测——`/readyz`/`/api/metrics`/`/api/debug/*`/`/ops` 改为"必须 404"正向拦截断言，健康检查只依赖 `/healthz`。

---

## 二、中风险清单

| # | 域 | 问题 | 证据 | 建议 |
| --- | --- | --- | --- | --- |
| M-01 | 领域 | 玩家灵力双池不同步：耗蓝扣聚合池、吸蓝写战斗单位池且每步被覆盖 → `mpDrainRatio` 失效 | `SimulationEngine.cs:664,677,945`；`CombatHooks.cs:173-189`；`CombatSkillEngine.cs:116-120` | 战后回写 `battleState.Player.Manapoints` 或明确废弃玩家侧吸蓝 |
| M-02 | 领域 | 掉落判定与数量共用同一 roll → 低掉率物品恒产最小量 | `SimulationEngine.cs:988-991` | 判定与数量分独立随机通道，或文档化语义 |
| M-03 | 领域 | `ActiveBuff` 恢复零校验：NaN 过期时间/比率可永久卡死（入 H-链） | `PlayerStateDocument.cs:358`；`PlayerAggregate.cs:128-131` | `ActiveBuff.IsValid` + 恢复逐项校验+自愈丢弃 |
| M-04 | 领域 | `NormalizeYields` 未校验 buff `statRatios` 有限性：`1e400→Infinity` 可入配表 | `GameDataCatalog.cs:647-654,248-255` | 校验 statRatios 键值有限性 |
| M-05 | 领域 | `AssetWallet` 零余额品种永久驻留 + `Restore` 不校验 `Variant` | `AssetModels.cs:134-139,119` | 归零移除（确认 tombstone 语义）；Restore 对齐 Apply 校验 |
| M-06 | 领域 | `Matches` 灵力百分比 `maxMana=0` 除零 → NaN → 触发器静默失效 | `CombatSkillEngine.cs:241-252`（对照 `DuelModels.cs:221-245` 有防护） | 统一 `Math.Max(1e-9, maxMana)` |
| M-07 | 领域 | 确定性推进异常无熔断 → 玩家 1Hz 永久重试卡死 | `PlayerRuntime.cs:1157-1184`；`SimulationEngine.cs:307,641,713` | 连续失败计数熔断+隔离+人工通道；缺失引用按自愈规约降级 |
| M-08 | 服务端 | 旧 actor 异步释放租约误删新 actor 租约行（ownerInstanceId 为实例级） | `PlayerRuntime.cs:408-430,1616-1625`；`PostgresPlayerOwnershipRepository.cs:58-64` | 租约行引入 actor 级 token/fence |
| M-09 | 服务端 | 建筑贡献材料扣减与建筑状态落库非原子 → 扣款成功贡献丢失 | `NodeBuildingRegistry.cs:231-239`；`PlayerRuntime.cs:940-968` | 并入经济事务或写 durable 补偿证据行 |
| M-10 | 服务端 | 登录账号枚举时序侧信道（无账号不跑 PBKDF2） | `PostgresAccountRepository.cs:179-204` | 未命中对固定盐跑同参数 PBKDF2 |
| M-11 | 服务端 | `/api/v1/market/book` 未认证且任意品种 `GetOrAdd` → 无界内存 | `Program.cs:360-381`；`MarketRuntime.cs:166-174` | 改 `TryGetValue`+品种白名单+限流 |
| M-12 | 服务端 | durable 队列与 outbox 无死信终态 → 毒载荷永久重试 | `PostgresSectionRepository.cs:961-983`；`PostgresOutboxRepository.cs:118-132` | 加 attempts 上限→dead 终态+backlog 健康指标 |
| M-13 | 服务端 | Flush/Advance 全体循环单 actor 异常中断整批 | `PlayerRuntime.cs:252-292` | per-actor try/catch 节流日志 |
| M-14 | 服务端 | `ApplyComputeResultAsync` 不感知 runtime 租约（TOCTOU 窗口） | `PostgresSectionRepository.cs:518-535` | apply 事务内断言无活 lease |
| M-15 | 节点 | 派发器响应校验不全+响应体无上限 | `ComputeDispatcherWorker.cs:207-235,77` | 逐项核对+`MaxResponseContentBufferSize` |
| M-16 | 节点 | "可选"节点实为启动硬依赖+默认启用 | `compose.prod.yaml:84-85`（depends_on healthy）；`ServerOptions.cs:114` | 去掉硬依赖或默认关 |
| M-17 | 压测 | LoadTests 可对目标环境写真实脏数据且无清理；compute 场景不覆盖真实链路 | `WebSocketScenario.cs:199-256`；`ComputeLoadScenario.cs:81-131` | 默认仅 loopback+`--allow-external`；补真实链路场景 |
| M-18 | 工具 | DataTool 校验缺口：5 个子目录外不校验、对象根静默跳过、未知参数静默忽略 | `src/DataTool/Program.cs:23,131-268,301,304` | strict 下未知即错；必填字段存在性校验 |
| M-19 | 运维 | WAL 归档白名单过严拒 `.backup`/`.history` → PITR 断时间线；每分钟 prod 自动更新绕过受控发布；生产全程超管连接；`daohua_api` 缺 `node_buildings` 授权；备份验证无 timer+无异地；连接数临界 48>47；市场归档单大事务 | `deploy/postgres-wal-archive.sh:11-16`；`deploy/daohua-ccr-auto-update.timer:6`；`deploy/compose.prod.yaml:91,188`；`deploy/postgres-local-roles.sql:18-25`；`deploy/verify-backup.sh`（无 timer）；`compose.prod.yaml:17,91,188`；`deploy/market-ledger-archive.sh:54-101` | 分项修复见正文；M-19 为运维面合辑 |

---

## 三、低风险清单（节选重点）

| # | 问题 | 证据/说明 |
| --- | --- | --- |
| L-01 | `/api/v1/metrics`、`/api/v1/market/metrics` 全环境装配仅需登录态 | `Program.cs:382-386,430-439`；生产靠 Caddy 拦截兜底，绕过拓扑即泄露内部计数 |
| L-02 | `market/orders` 非法参数 `ArgumentException` 逃逸为 500（应 400） | `Program.cs:387-414` |
| L-03 | `_logBuffers`/`_recentRequests` 随 actor 卸载不清除 | `PlayerRuntime.cs:58,390` |
| L-04 | 命令回执 TTL 仅 30 分钟，超窗重放可二次执行低频写命令 | `PlayerRuntime.cs:781` |
| L-05 | `/ws` 无 Origin 校验（依赖 SameSite=Lax 兜底） | `WebSocketRuntime.cs:163-183` |
| L-06 | 每在线玩家每秒约 2 次 lease UPDATE 写放大 | `PlayerRuntime.cs:630,1228` |
| L-07 | 节点令牌无失败限流、端点允许 http | `TokenValidator.cs`；`ServerOptions.cs:157-159` |
| L-08 | `ToWorldSection` 用墙钟投影 → world 段 checksum 恒变、增量推送空转 | `ProtocolSnapshotMapper.cs:355` |
| L-09 | 前端 duel 分段 delta 每次触发同步 localStorage 写（热路径 I/O） | `gameStore.ts:371`；`battleReportStore.ts:92-105` |
| L-10 | 前端终态掉线仅 2.6s 提示、无重登引导；Hello 前 sendCommand 露原始错误码 | `App.tsx:1951-1955`；`gameClient.ts:171-175,236-248` |
| L-11 | 前端标签互斥开关 on→off→on 单向生效；localStorage 不可用时守卫静默失效 | `antiMulti/index.ts:39-46`；`tabGuard.ts:130-137` |
| L-12 | 前端错误文案硬编码中文绕过 i18n；RenameModal 草稿残留 | `apiClient.ts:104` 等；`RenameModal.tsx:26` |
| L-13 | Caddy 拦截边角：`/api/debug` 精确路径与大小写变体可绕过 | `xx.yuohira.com.caddy:10` |
| L-14 | `/healthz` 公网未拦截泄露版本号；smoke 默认口令硬编码注册 `smoke_probe` | `smoke-production.mjs:102-103` |
| L-15 | 构建不可完全重现：无 `--locked-mode`/`packages.lock.json`、PG 镜像未 pin digest | `Dockerfile:24-25`；`compose.prod.yaml:5` |
| L-16 | `daohua.env` 权限未校验且 `.gitignore` 不覆盖 `*.env` | `deploy-prod.sh:155-173`；`.gitignore:28-30` |
| L-17 | macOS 兼容缺口：`ss` 无回退、`base64 --decode` BSD 差异 | `dev-service.sh:44`；`install-ccr-auth.sh:64` |
| L-18 | `ccr-auto-update.sh` 硬编码 15128、不重建 worker、每分钟两次 pull | `ccr-auto-update.sh:78,86-96` |
| L-19 | `persistence-worker` 无健康检查；`market-ledger-archive` DDL 与 PostgresSchema 手工同步漂移面 | `compose.prod.yaml:174-210` |
| L-20 | 领域：`MutateDamage` NaN 透传致死不快速失败；攻速无上界→事件爆炸；持续消耗品按未折算时长扣；`DamageReflectRatio>1` 与同亡归属；手续费 `*0.02` 魔法数；`CompleteOrRequeueHeadAction` 死代码；`ComputeAdvanceRequestDto` 疑似死协议 | `CombatModels.cs:795-797`；`PlayerAttributeResolver.cs:157`；`SimulationEngine.cs:1602`；`CombatHooks.cs:191-208`；`MarketRuntime.cs:306`；`PlayerAggregate.cs:596`；`Contracts.cs:577-606` |
| L-21 | 配表静默默认值容忍笔误（未知 type→Exploration、坐标缺省 0、DuelSettings 回退） | `GameDataCatalog.cs:719-726,911-977`；`DuelModels.cs:29-55` |
| L-22 | 指纹跨平台排序隐患：Windows `\` 分隔符可致节点指纹不一致 → 全任务 409（待复核） | `GameDataFingerprint.cs:51-53`；`DataTool/Program.cs:49-51` |
| L-23 | Ops 面板 Dev 绑 0.0.0.0 无认证（LAN 可读玩家数据）；会话无批量撤销 | `appsettings.Development.json:8-13`；`PostgresAccountRepository` |

## 四、确认无问题（重点核查结论）

- **Domain 确定性**：全目录无 `DateTime.Now/UtcNow`/`new Random`/`async` 泄漏；SplitMix64 纯函数随机；钩子管线按 Order+类型名冻结序、吸血/吸蓝/反伤无递归双计；`RemainingRepeats=-1` 全链路语义正确（入队/恢复/完成/阻塞边界均无自动出队漏洞）；推进有界（10 万迁移上限+48h 封顶）。
- **玩家 8 分段（含 Duel）全链路**：`PlayerSectionKind` ↔ `SectionCodec` ↔ `ProtocolSnapshotMapper` ↔ schema v3 一一对应无丢段；快照映射 5 个调用点全部显式透传 `GameDataCatalog`。
- **协议信封前后端严格对齐**：`WireMessageType` 1-17 逐值一致；36B 信封头/魔数 DHW1/版本/1MB 上限/Guid 混合端序全部正确。
- **服务端并发与持久化**：Actor 单写者无聚合外泄；市场 Serializable+fence+幂等键+行锁排序完整；section durable 队列 latest-only+lease+SKIP LOCKED+头校验和；写回失败恢复证据链完整。
- **认证**：PBKDF2-SHA512 600k+固定时间比较+token SHA256 入库+HttpOnly/SameSite=Lax；防多开配额/顶替/拆除链无竞态。
- **SQL/注入/XSS**：全部参数化；前端零 `dangerouslySetInnerHTML`/`innerHTML`/`eval`。
- **部署加固**：容器 read_only+cap_drop+no-new-privileges+非 root+限额+健康检查；密钥全部环境注入无明文默认值；端口仅 `127.0.0.1:15128`；`migrations/` 合规可整体删除；备份脚本 flock+原子 rename+SHA256+三层轮转完整。
- **ComputeNode 边界本身健全**：固定时间令牌比较、缺令牌 fail-fast、Brotli 炸弹防护、批处理契约校验、无 DB 凭据——问题只在"不算东西"。

## 五、处置建议优先级

1. **立即止血（建议本迭代）**：`ComputeNode:Enabled=false`（H-01/H-02/H-03 止血）；`simulation.advance` 服务端钳制 UtcNow（H-04 单行修复）；Hello 握手超时（H-06）；`/market/book` 不建书（M-11）。
2. **下个迭代**：ForwardedHeaders 信任网段（H-05）；actor TTL（H-07）；SnapshotDelta 丢帧 fail-fast+客户端 gap 检测（H-08/H-09）；compute 回写领域校验（H-03 根治）；节点注入键命名空间隔离（H-02 根治）。
3. **可靠性补强**：租约 actor 级 fence（M-08）；建筑贡献原子化（M-09）；durable 队列死信终态（M-12）；per-actor 异常隔离（M-13）；推进失败熔断（M-07）。
4. **运维面**：smoke/readyz 口径统一（H-10）；WAL 白名单扩展；生产角色最小权限+`node_buildings` 授权补丁；备份验证 timer；发布通道设计裁定（每分钟 prod 自动更新是否保留）。
5. **文档口径**：本次已同步修复 duel 八分段全线漏登等过期口径（见提交记录）。
