# 18 · 服务端 ECS 化执行架构与触发器规约

> 本文档是继任规约：取代总纲 15「聚合运行时规约」中"System 仅作调度壳、Domain 承载全部规则"的分层口径。ECS 从调用层升级为**规则本体层**；Domain 退化为纯库。文档先行，实施序见 §10。

## 0. 目标与红线

**目标**：业务规则本体（推进、结算、触发反应）写在 System 体内，状态写在 Component 上；System↔Domain 静态函数的三段转调结构退役。

**不可动红线**（与现状逐位一致）：

- 边界驱动调度：拍=自然边界跳转，不回到固定帧率。
- `AdvanceTo` 批量窗口推进：一拍消化整段窗口战斗段。任何重构不得以"逐边界一拍"替换它（那是 15 万拍/30h 的已修复缺陷形态）。
- 逐整秒浮点序列、`RemainingRepeats == -1` 无限循环保留、先校验后变更、窗口工作钱包语义。
- `MaxTransitionsPerAdvance` / `MaxEventsPerAdvance` 保险丝语义。
- 单写者：同一玩家全部变更在分片 Fiber 内串行；跨边界不留实体/聚合引用。
- `double` 数值纪律与在线/离线/重连逐位一致。

## 1. 分层反转

```
当前：  System（规则本体）→ Component（状态本体）
        Domain = 纯库：配表类型 / POCO 状态 / 纯公式 / SectionCodec / 协议映射 / DTO
        SimulationEngine/DeterministicCombatEngine = 无世界测试与兼容门面，转发到 ECS System
```

| 层 | 装什么 | 不装什么 |
|---|---|---|
| `src/Domain`（纯库） | `GameDataCatalog`、`ActionDefinition`、`ActiveBuff`/`CombatantState` 等 POCO、属性乘区合成等**无状态公式**、`SectionCodec`、`ProtocolSnapshotMapper`、事件 DTO | 引擎、推进器、状态机的规则方法 |
| `src/Ecs`（规则本体） | 实体、组件、13+N 个 System（含行动实现与战斗边界调度器）、触发器 System 族、`EcsActionPipeline`（退化为请求上下文：窗口/钱包/账本） | 宿主、协议、存储 |
| `src/Server` | 宿主、WebSocket、持久化适配 | 不变 |
| `src/ComputeNode` | **同一 `EcsWorldShard` 执行环境**（§9） | 不变红线：无权威状态、无生产库 |

运行时状态 POCO 物理上仍驻留 `Domain` 程序集（`SectionCodec` 内聚需要），但其中**不得有任何推进规则方法**——状态类是纯数据。

## 2. 组件划分：反应性粒度

拆分原则（用户裁定）：**需要响应事件的独立成组件；纯被读取的数据按内聚域合并**。

### 玩家实体

| 组件 | 内容 | 反应性 | 持久化分段 |
|---|---|---|---|
| `PlayerIdentityComponent` | 版本/脏标记/在线态 | 元数据 | — |
| `PlayerResourcesComponent` | `Hitpoints`/`Mana`/资源游标 | 持久化资源（战斗内外同一份） | Combat |
| `PlayerStatsComponent` | 解析后战斗面板 + 输入修订号 | 被公式读 | 不落库（派生缓存） |
| `PlayerProgressionComponent` | 境界/经验/修为/统计 | 被写入 | Progression |
| `PlayerWorldComponent` | 位置/在途/建筑效果 | 非战斗行动属性域 | World |
| `PlayerActionQueueComponent` | `EcsActionState` 队列 | 调度数据 | ActionQueue |
| `PlayerLoadoutComponent` | 装备/法宝/技能装配 | 被公式读 | Loadout |
| `PassiveBuffsComponent` | **纯修饰 buff 合并容器**（StatRatios/攻速/减伤等） | 被公式读 | Buffs |
| **Reactive Buff 组件族** | 每种触发型 buff 一个组件类：`OnHitLeechComponent`/`ManaLeechComponent`/`ThornsComponent`/`ThreatOnTakenComponent`…参数实时读 Stats.Effects | **主动反应** | Buffs（`ActiveBuff` 扩展） |
| `PlayerInventoryComponent` | 资产条目 | 数据 | 钱包 |
| `PlayerEconomyComponent` | 市场关联 | 数据 | — |
| `PlayerSettingsComponent` | 设置/LoopEnabled/AutoTravelEnabled | 数据 | Settings |
| `PlayerDuelComponent` | 斗法 | 数据 | Duel |
| `EcsActionExecutionComponent` | 请求工作区（窗口/钱包/账本/战斗句柄） | 运行时 | 不落库 |

### 战斗单位实体（玩家战斗单位 + 怪物共用结构）

`CombatantComponent`（HP/MP/出手游标）、`SkillStateComponent`、`PassiveBuffsComponent`（同款容器）、**Reactive Buff 组件族复用同款**、`MonsterIdentityComponent`/`DropProfileComponent`（怪物侧）。

### 遭遇 / 节点 / World

`CombatEncounterComponent`（状态+事件缓冲+触发队列）、`CombatLogComponent`；`MapNodeComponent`/`NodeBuildingComponent`/`EncounterProfileComponent`/`NodePopulationComponent`；`EcsClockComponent`（World.Main）。

## 3. System 拓扑

### Update 序列（骨架不变，本体化）

| Order | System | 本体职责（逻辑从 Domain 搬入体内） |
|---|---|---|
| 100 | `ActionClockSystem` | 资源窗口开闭、帧目标（调度语义） |
| 200 | `ActionQueueSystem` | 队首准入：地点白名单、材料前置、Blocked 轮转/出队 |
| 300 | `GatheringActionSystem` | 采集实现本体：时长公式、逐秒产出、轮结算 |
| 310 | `CraftingActionSystem` | 制造实现本体 |
| 320 | `ActionBoundarySystem` | 其余计时行动实现本体（修炼乘区等） |
| 330 | `TravelActionSystem` | 旅行实现本体 |
| 400 | `TimedActionYieldSystem` | 计时轮结算：入账、材料扣除、repeats、出队 |
| 550 | `CombatActionBoundarySystem` | 搜寻/复活阶段推进与遭遇附着 |
| 600 | `CombatEventAdvanceSystem` | `AdvanceTo` 整段窗口推进（含段内触发器派发） |
| 610 | `CombatTargetSystem` | 主目标实体索引 |
| 650 | `CombatEventSystem` | `LastEvents`→`CombatLogComponent` |
| 700 | `CombatActionYieldSystem` | 战斗段结算：预算折算、死亡/掉落/胜负、终局释放实体 |
| 800 | `PlayerClockCommitSystem` | 窗口末提交：游标/版本/脏标记/资源回复落账 |

### Trigger 域（不占 Update 序，`RunSystem<T>(entity)` 点名）

由战斗边界循环在固定点同步派发（§4）。有实体宿主时每类触发器一个 ECS tag；无实体宿主的内联路径使用 §4.1 的轻量处理器，不进入 YuoCore Tag 注册：

```
IOnHitTrigger    OnHitLeechSystem / ManaLeechSystem / ThornsSystem / ThreatOnTakenSystem …
IOnKillTrigger   OnKillBuffSystem / OnKillLootSystem …
IOnCastTrigger   OnCastInterruptSystem / OnCastBonusSystem …
IOnExpireTrigger OnExpireExplodeSystem …
IOnDeathTrigger  OnDeathReviveSystem …
```

内核机制现成：`YuoWorld.RunSystem<T>(entity)`（`YuoCore.Ecs/YuoWorld/RunSystem.cs`）——稠密 tagId 直查 + `system.m_Run(entity)` 同步执行，一次普通方法调用，无 tick 延迟。**此域 System 不进入 Update 序列，不参与每拍扫描。**

负载信箱：`CombatTriggerInboxComponent` 是实体派发期间向触发器 System 传参的唯一通道——`CombatTriggerSurface.Attach` 挂接战斗单位时常驻该组件；派发前 `EcsCombatTriggerSink` 写入 `inbox.Trigger = trigger`，`RunSystem` 同步执行后立即清回 null，非派发期间恒为 null。实体缺失或信箱缺失属不变量违例，快速失败。

## 4. 战斗内核：批量调度器 + 段内触发器

`AdvanceTo` 的边界循环保留批量形态；段内规则按边界类型拆解，在**固定序列点**派发触发器：

```
AdvanceTo(state, windowTarget):
  while boundary := NextNaturalBoundary(state) <= windowTarget:
      ResolveBoundary(boundary)               // 出手/技能/回复 本体结算
      DrainTriggers(encounter.PendingTriggers) // ← 固定派发点
          foreach trigger in 入队顺序:
              有实体宿主：RunSystem<trigger.Tag>(unitEntity)
              无实体宿主：InlineTriggerPipeline.Dispatch(trigger)
      UpdateOutcome()                          // 死亡/胜负永远在触发器结算之后
```

序列契约（确定性红线）：

- 触发器消费点固定在"本边界结算后、下一边界取数前"；`UpdateOutcome` 永远在触发器结算之后——**OnHitHeal 可以救活自己**，这是"挨打回血顶不顶得住下一刀"语义的正确位置。
- 命中反应（OnHit）的派发粒度精确化为**每次命中结算后立即 drain**：同一自然边界内可能有多个单位先后出手，若推迟到边界末统一消费，衍生伤害（反伤致死）的落账时序会相对后续出手错位——"命中后立即消费"是保持旧内联结算时序逐位等价的唯一合法点位，同时也是"边界结算后、下一边界取数前"的更严格实现。
- 触发器内消耗 RNG 只能走 `encounter` 固定随机流，消费顺序 = `PendingTriggers` 入队顺序。
- 触发器不得改变边界时刻表（只可改数值与队列内容）；新增边界只能走正常 `Next*At` 计算。

### 4.1 轻量内联触发器处理流程

无实体战斗（纯领域推进、斗法剧本）不需要 `YuoSystem`、组件标记或 `YuoWorld.RunSystem`。这条路径使用 `CombatTriggerHandler` 基类和按事件固定签名的接口（如 `IInlineOnHitTrigger.OnHit(PendingCombatTrigger)`）：

- 启动时只扫描 `DaoHua.Ecs` 程序集中的 `CombatTriggerHandler` 非抽象子类，实例化一次并按 `CombatTriggerOrder` 排序；不扫描测试程序集，也不把轻量接口继承为 `ISystemTag`。
- 每种事件缓存为强类型接口数组。热路径只按 `CombatTriggerKind` 选择数组并进行接口调用，不使用 `MethodInfo.Invoke`、`Dictionary<Type>` 或运行时反射。
- 同一事件类型按顺序号升序执行；顺序号相同时按处理器全名 `StringComparer.Ordinal` 二次排序，消除 `GetTypes()` 返回顺序差异。
- 处理器只接收 `PendingCombatTrigger` 或事件专用领域上下文，不能持有 `YuoEntity`、`YuoWorld` 或宿主服务引用。
- `InlineCombatTriggerSink` 和 `EcsCombatTriggerSink` 是互斥消费路径；一条 pending trigger 只能交给其中一个 sink，防止同一效果重复结算。两条路径继续共用 `CombatTriggerMath` 与 `Settle` 语义。
- 新增既有事件下的效果只需新增处理器类；新增事件种类仍需定义事件接口、参数记录和 `CombatTriggerKind` 派发槽位。当前仓库没有 AOT/裁剪发布约束；未来启用时必须为扫描类型保留元数据，或将注册表替换为源生成实现。
- 双路径同效果落地固定为四件套：内联 `CombatTriggerHandler`（`CombatTriggerOrder`）+ 薄 `YuoSystem`（`SystemOrder` 同一先后关系）+ 能力标记组件（`CombatTriggerSurface.Sync` 按 `Stats.Effects`/`Stats.Traits` 挂摘）+ `CombatTriggerMath` 共享本体。
- 特质载体：`CombatantStats.Traits`（`trait.*` ID 集合，面板字段，快照重建不带快照负担）。条件触发型特质按 ID 判定（如 `trait.bukandaowo` → `CombatEncounterState.Create` 开战授予 `buff.yinshen`）；数值型特质经 `CombatTraitRules` 折算进 `Stats.Effects` 点数通道（如 `trait.liweifanfou` → `ThreatOnTakenPoints = 100`）。
- 随机通道分配（目标抽取族）：0~15 核心结算保留、3 玩家目标、16 单体兜底、17 AoE 多目标、**18 敌方盟友目标（出手）、19 敌方盟友目标（决策）**、**20+chainIndex 连锁打击目标**（`chainOnKillCount` 击杀连锁的逐次追加抽选，命中未死即终止）、64+ 触发器专属。敌方出手与决策在 `PlayerAllies` 上按 `EffectiveThreat` 轮盘抽取——单存活成员 `SelectTarget` 提前返回不掷点，单盟友行为与固定目标逐位等价。

桥接（Domain 不碰实体的解法）：战斗调度器整体迁入 `src/Ecs/Combat/`，操作的就是组件化状态，`RunSystem` 直接调用无分层冲突；`CombatantState` 增加运行时字段 `EntityId`（`AttachCombat` 时设置，不序列化）。

`DeterministicCombatEngine` 的归宿：边界调度骨架迁入 `src/Ecs/Combat/BattleScheduler`；伤害/技能/回复公式留在 Domain 作纯函数，被 boundary handler 调用。`Domain.Tests` 中的 `AdvanceOneBoundary` 直测迁移到 `Ecs.Tests`。

### 4.2 特殊效果实现工作流

装备被动、技能附加效果、Buff/Debuff、怪物特性及其他玩法特殊效果的新增、修改、修复和评审，统一使用仓库级 [special-effects Skill](../../../.agents/skills/special-effects/SKILL.md)。效果数值与预算由 [data-designer Skill](../../../.agents/skills/data-designer/SKILL.md) 配合处理；纯视觉特效不属于此工作流。

效果先明确触发时机、作用侧、结算口径、叠层与生命周期，再选择属性公式、固定事件接口或已有计时边界。轻量处理器实例全局复用，必须无战斗状态；冷却、次数、层数和到期时间由单位/遭遇/玩家状态持有。当前实际事件只有 `OnHit`，其他事件按具体需求补齐生产点与消费点；实体路径与内联路径继续共享效果本体，并用启用实际效果的场景验证等价性。

## 5. 行动实现本体化样板

以采集为例的目标形态（对照现行三段转调）：

```csharp
// GatheringActionSystem（order 300）体内直接持有规则：
//   准入检查 → 时长公式 → 逐秒产出循环 → 轮结算
// 组件直接读写：player.GetComponent<PlayerActionQueueComponent>() 等
// 账本仍走 pipeline 请求上下文（ActionMutationBatch），不变
```

`EcsActionPipeline` 退化保留的职责：窗口游标/剩余预算/工作钱包/账本事件尾/战斗句柄——它是"请求上下文"，不再是"逻辑转发层"。

## 6. 持久化：分段不动，映射层扩展

- 8 个 `PlayerSectionKind` 落库格式主体不动；`ExportSnapshot`/`SectionCodec` 契约不变。
- 组件↔分段映射层扩展：新组件映射进既有分段（`PlayerResourcesComponent`→Combat；reactive buff→Buffs）。
- `ActiveBuff` 记录扩展触发器字段（`TriggerKind`/`Params`）——**挂钩点**：首个"buff 定义携带触发参数"的特性落地时同步扩展，届时 Buffs 分段格式升版并同步 `PostgresSchema` 与 DataTool 校验；当前 reactive 参数经 `CombatantStats.Effects` 链路承载（装备 flatStats 点数键/怪物配表），无独立 wire 需求。

## 7. 确定性与并发守则

- RNG：段内一切随机消费走 `encounter` 种子流；触发器固定顺序消费。
- 同刻排序：`SystemOrder` 管 Update 序列；`PendingTriggers` 队列序管段内派发——两套都是确定性序列，无字典序。
- 数值顺序：先校验后变更、逐整秒累加、账本按实际入账量记录——搬迁时逐函数比对。
- 所有权不变：Fiber 单写者；跨实体只走 `entityId` 间接寻址，不留实体引用跨越方法边界。
- `YuoCore` 内核红线：新派发面接稠密索引（`RunSystem` 已是），不改生命周期派发语义。

## 8. ComputeNode 统一执行环境

`ComputeNode` 引用 `DaoHua.Ecs`（`src/Ecs` 只引 Domain+YuoCore，无宿主依赖，不违红线），job 流换装：

```
DecodeAggregate → shard.CreatePlayer(state) → shard.RunPlayerOnceAsync(target)
              → ExportSnapshot → EncodeAggregate（脏分段）
```

收益：一致性**由构造保证**（同一代码路径），不再依赖双跑验证；ECS 层新增机制（触发器、实体级玩法）自动被补算继承。代价：job 起 World/Fiber 的固定开销（相对补算体量可忽略，可跨 job 复用 shard）；`AGENTS.md` 依赖图同步为 `ComputeNode -> Domain + Ecs`；golden vector 全量重验。

## 9. 依赖方向（目标态）

```
Server   -> Ecs + Domain + Protocol + YuoCore + Npgsql
Ecs      -> Domain + YuoCore(Abstractions/Ecs/Runtime)
Domain   -> YuoCore.Abstractions（纯库；POCO 状态+配表+公式+编解码）
ComputeNode -> Ecs + Domain（ECS 执行环境，无宿主/存储）
```

## 10. 完整收口记录

组件、行动队列与执行规则、战斗边界与相位、Tag 派发、ComputeNode 共用执行环境和文档规约已经按本文目标落地。`SimulationEngine`/`DeterministicCombatEngine` 保留的公开方法仅用于无世界测试与兼容调用，生产 ECS 路径不从门面进入规则编排。验证记录统一登记在 `docs/tasks/archive/archive-2026-09.md`。

## 11. 验证门禁

- 全部现有测试套件逐位绿（Domain/Ecs/Server/YuoCore）。
- 确定性：`CombatRestoreReproTests` 双跑 + golden vector + 真实存档回放。
- 性能：300 人基准与批量快进路径不回退；`AdvanceTo` 批量粒度不动。
- 保险丝：`MaxTransitionsPerAdvance`/`MaxEventsPerAdvance` 语义与诊断输出保留。

## 12. 风险清单

| 风险 | 对策 |
|---|---|
| 一次性重构中间态全红不可分步 | 长分支纪律 + 实施序内部"每步可编译"约束 |
| 搬迁中的顺序漂移（账本/RNG/逐秒） | golden vector + 双跑回归守门；逐函数对表 |
| Reactive buff 序列化升版 | 内测期允许破坏；schema/备份文档同步 |
| 触发器热路径开销 | `RunSystem` 已是稠密直查；预算=每触发一次 O(1) dispatch，不进 tick |
| 总纲 15 语义冲突 | 本文档明确继任关系，15 号标注"由 18 号取代" |

---

**本规约生效即视总纲 15 中"System 仅调度壳"口径作废；聚合运行时相关分段持久化与单写者语义仍然有效（§6/§7 保留承接）。**

## 13. 完整 ECS 收口结果（2026-10-01）

本次收口已经完成。生产运行时中，行动队列、行动执行和战斗的状态变更与调度均由 `src/Ecs` 的组件、阶段 System 和 Tag System 持有；`EcsActionPipeline` 只保留请求工作区、窗口游标、账本和提交边界，不再承载规则转发。

### 生产 ECS 规则归属

- `ActionExecutionRules.Pipeline/Resources/Timed/Travel` 与 `ActionClockSystem`、`ActionQueueSystem`、采集/制造/计时/旅行/结算 System 共同持有队列准入、资源边界、行动推进、产出、重复次数和 `-1` 无限循环语义。
- `CombatActionRules` 持有搜寻、复活、入场、持续用品、斗法回放、胜负、掉落、统计和战斗收尾；`CombatEventAdvanceSystem` 持有自然边界、批量推进和固定相位派发；`CombatSystems` 持有战斗实体推进、目标索引、日志和实体释放。
- Buff 到期通过 `PlayerBuffExpiryInboxComponent` 与 `IExpirePlayerBuffs` Tag System 在资源边界同步裁决，真实删除才产生 Buffs 脏标记。
- `DeterministicCombatEngine` 与 `SimulationEngine` 的旧公开入口只作为无世界测试/兼容门面，内部转发到上述 ECS System；Server、ComputeNode 和生产玩家运行时不从这些门面进入规则编排。

### 明确保留在 ECS 之外的边界

纯属性/伤害/目标数学、配表选择器和编解码继续作为无副作用算法；PostgreSQL、快照分段、资产事务、HTTP/WebSocket、认证、协议 DTO、ComputeNode 任务和宿主生命周期继续由 Domain/Server/Protocol/持久化适配层负责。它们通过 ECS 工作区和提交边界交互，不复制行动或战斗规则。

普通推进、批量补算和命令路径现在共用同一套 ECS System 规则入口，验证覆盖终态、账本、脏标记、事件顺序和 `-1` 无限循环语义；无需再按阶段保留未迁移的行动或战斗编排。

## 11. 可信冷补算与原子提交边界（TASK-20261008-10）

- 在线、节点和主服验证复用同一 ECS 确定性执行路径；验证使用独立世界，不改变在线宿主默认世界或静态调度开关。
- 节点仅提供候选结果。主服冻结一致快照中的身份、有效提交头、全集分段、耐久队列与资产余额版本，独立重演后严格比较分段集合、原始 JSON 字节、版本、实际模拟时钟与资产增量；传输 checksum 和节点自报 metrics 不构成可信证明。重复分段或额外字段结果必须拒绝。
- 接受结果时，同一数据库事务校验完整源基线、冷玩家状态、运行时所有权、钱包版本与任务租约；section/游标、可信资产增量与任务完成必须同生同灭。租约完结 CAS 失败必须回滚全部写入，不能仅忽略失败完成动作。
- 学习、跳过引导、模拟结算采用同一状态/经济事务核心；失败恢复遵循 ECS 开发指南的请求前脏集合与单调流水号纪律。公共建筑真源及原子性见总纲09。
- 主服本地执行（§14.4）复用同一独立重演产物直接写回，不再二次比较；写回仍走本节同一事务边界（租约、完整源基线、冷玩家状态、钱包版本、section CAS、资产入账、任务完结同生同灭）。

## 14. 玩家三态生命周期执行规约（TASK-20261011-05）

总纲01 §1.2 第 5 条三态主旨的服务端执行口径。

### 14.1 离线锚点与在线状态

- 聚合持有离线锚点 `OfflineSinceAt`，随 settings 分段与 `Presence` 一同持久化：转离线时写入**断线时刻**，转在线时清空。
- **只有连接生命周期改变在线状态**：WebSocket 握手经 `AttachConnectionAsync` 转在线；断线宽限到期卸载时转离线并以断线时刻为锚点。社交强拉、HTTP 快照、注册装载等非连接装载一律不改在线状态，实体以离线身份常驻或补算。
- 实体创建（运行时装载、冷算执行器、主服验证三路共用 `EcsWorldShard.CreatePlayer`）统一规范化：持久化为在线必为崩溃残留（装载即意味本进程刚取得所有权），按离线处理、锚点取 `LastSimulatedAt`；离线但缺锚点同样取 `LastSimulatedAt`；规范化标脏落盘，三路结果逐位一致。
- 读模型 `game.player_projection.offline_since` 随 settings 分段投影；离线时长的一切判定（会话离线截止、48h 逐出、冷扫描）只读该列。`last_active_at` 不参与生命周期判定。

### 14.2 收益封顶

- 离线收益上限为 Domain 常量 `PlayerLifecycleRules.OfflineIdleLimit`（48h），不可配置。
- 单次推进的实际计算终点 = `min(目标, 请求起点 + 48h 保险丝, OfflineSinceAt + 48h)`；在线玩家无锚点时只受保险丝约束。滚动批算任意切分的累计收益与一次性补算逐位一致，且不越过锚点 + 48h。
- 超出边界的尾段只清理过期态并推进时钟；`offline.capped` 事件只在跨越边界的那次请求（请求起点 ≤ 边界）产生一次。
- 会话引擎的单次推进 48h 仅为保险丝；会话的离线边界由名册离线成员最早的 `offline_since + 48h` 裁决（§21.13.2）。

### 14.3 在线宽限

- 拥有会话的连接断开后进入宽限（`OfflineIdle:GraceSeconds`，默认 300）：实体常驻、在线位保留、按在线实时推进；宽限内重连直接复用实体，不重载不补算。
- 宽限到期且无新连接：注销在线位，`RemoveAsync(flush, offlineSince: 断线时刻)` 落盘离线态并归还运行时租约。新握手先取消或等待在途到期收尾，再装载。
- 优雅停机时立即执行全部在途宽限的到期收尾（锚点仍为各自断线时刻）。

### 14.4 离线挂机批算

- 冷扫描对象：投影离线、无存活运行时租约、无活跃会话绑定与待消费结算、且「距上次结算 ≥ 批算间隔（`OfflineIdle:BatchIntervalMinutes`，默认 60）」或「已到封顶边界仍有欠债」的玩家。批目标 = `min(当前, OfflineSinceAt + 48h)`；扫描与执行不受计算节点配置门控。
- 主服本地执行器常驻消费同一 `compute_jobs` 队列（单路串行，与验证器共用一个独立 ECS 世界）；计算节点派发只领取 pending 超过 `ComputeNode:OverflowAfterSeconds`（默认 300）的积压任务，结果仍经 §11 全量独立重演。
- 回线抢占：玩家装载取得运行时租约后，在途任务写回因租约/版本漂移归档丢弃；装载路径即时补算到当前。

### 14.5 彻底离线

- 到达 `OfflineSinceAt + 48h` 时冷扫描入队终末批（目标 = 边界），补齐断线到边界的全部收益；此后玩家停止被扫描。
- 看门狗按 `offline_since + 48h` 逐出队伍（队长移交、空队清理、会话仲裁 `member-offline-timeout`）。
- 队列隐式暂停：登录时推进请求起点已在边界，直接封顶推进时钟到当前，不产收益、不再补算，队列随后照常续跑。

### 14.6 组队会话

- 活跃会话只要名册中有成员在线（含宽限期），离线成员实体常驻装载并在批推进中实时消费结算；会话散场或名册全员离线后卸载常驻实体。
- 名册全员离线的会话按批算间隔推进到当前，推进后逐成员经所有权通道强拉消费结算再卸载；会话推进不得越过名册离线成员最早的离线边界。
- 散场后未装载成员同样强拉消费终局结算，保证其持久化状态与个人冷算资格及时恢复。

### 14.7 已知边界

- 服务器非优雅崩溃时，在线玩家的投影在线标记残留到其下次被装载（届时按 §14.1 规范化）。
- 宽限期内实体与租约留在原实例；多实例部署下换实例重连需等宽限到期。
