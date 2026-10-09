# 道化太初 · ECS 业务开发规约

本文档是**在现行 ECS 运行时上开发业务逻辑的唯一权威规约**。架构总览见 `docs/backend/logic-development-guide.md` 与 `docs/backend/domain-data-architecture.md`；本文只管"怎么在 ECS 上正确写业务"。

## 0. 分层定位速查

| 层 | 位置 | 职责 | 禁止 |
| --- | --- | --- | --- |
| 领域原语 | `src/Domain` | POCO 状态、配表、公式、编解码、聚合、持久化 DTO、触发器契约（`PendingCombatTrigger`/`ICombatTriggerSink` 接口） | ASP.NET/DB/宿主/`YuoEntity` 等运行时对象 |
| ECS 执行 | `src/Ecs` | 组件、系统、分片、行动管线、规则本体；`Systems/ActionExecutionRules.*` 持有行动规则，`Systems/CombatActionRules`/`CombatEventAdvanceSystem` 持有战斗规则；`Engine/SimulationEngine` 与 `Combat/DeterministicCombatEngine` 仅为无世界测试/兼容门面 | DB/网络/宿主引用 |
| 宿主适配 | `src/Server`（`EcsPlayerRuntimeAdapter`、`Program`）| 命令路由、会话、持久化边界 | 直接持有 `PlayerAggregate`/`YuoEntity` |
| 共享边界 | `src/Server.Clean`（编译 `src/Server/Clean/**`）| 仓储、DTO 映射、Worker | 领域规则 |
| 内核 | `vendor/YuoCore`（`ecs-optimized` 分支 submodule）| ECS/Fiber 框架原语 | 本仓任务默认不改 |
| 计算节点 | `src/ComputeNode` | 冷挂机补算：Decode→`EcsWorldShard`→`RunPlayerOnceAsync`→Encode | 生产数据库/宿主引用 |

依赖方向：`Server → src/Ecs + Server.Clean + Domain + Protocol + YuoCore`；`src/Ecs → Domain + YuoCore`；`Domain → YuoCore(Abstractions/Ecs)`。禁止反向与循环。

## 1. 实体与组件模型

### 1.1 实体分类（`EcsWorldShard` 内）

| 实体 | 组件集 | 生命周期 |
| --- | --- | --- |
| 玩家 | `PlayerIdentity`/`Progression`/`World`/`Combat`/`Resources`/`Stats`/`Loadout`/`Buff`/`ActionQueue`/`Inventory`/`Economy`/`Settings`/`Duel`/`PlayerStatsInbox`/`PlayerActionQueueMutation`/`EcsActionExecution`；战中加挂 `Combatant`/`SkillState`/`Buff`/`CombatTriggerInbox`/reactive buff 标记 | `CreatePlayer`/`RemovePlayer` |
| 地图节点 | `MapNode`/`NodeBuilding`/`NodePopulation` | `CreateMapNode` |
| 怪物 | `MonsterIdentity`/`Combatant`/`SkillState`/`Buff`/`DropProfile`/`CombatTriggerInbox`/reactive buff 标记 | `CreateMonster`/遭遇销毁 |
| 遭遇 | `CombatEncounter`/`CombatLog` | `CreateEncounter`/战斗结束销毁 |
| 世界主实体 | `EcsClockComponent`（`WorldMain`） | 分片构造即建 |

`CombatantComponent.State` 持有 `CombatantState` POCO 引用（战斗结算的权威运行态）；`CombatantState.EntityId` 在挂接时回填实体标识，`CombatEncounterState.TriggerSink` 装配实体派发器——两字段均为运行时不序列化。

`CombatantComponent.Stats`/`CombatantId`、`SkillStateComponent` 的技能/冷却/吟唱游标与 `BuffComponent.Active` 均直接读取这一份 `CombatantState`；刷新属性、恢复遭遇或替换单位状态后，组件视图自动跟随。不得另建列表、面板副本或双写同步。

遭遇释放时一并移除玩家的战斗单位、技能/Buff 视图、触发信箱及反应能力标记；下一遭遇重新装配。玩家持久化分段 `PlayerCombat`/`PlayerBuff` 不受此运行时组件生命周期影响。

### 1.2 玩家组件 vs 聚合分段

- ECS 组件是**常驻权威状态**；`PlayerAggregate` 只在命令边界内经 `MutatePlayer` 包装为短命视图，分段与队列直接引用组件中的 POCO，仅回写头部标量；不存在完整快照往返。
- `PlayerSectionKind` 八分段与组件一一对应；`PlayerInventoryComponent`（资产余额镜像）不进聚合——资产真源仍是 `AssetWallet`+`economy.*`。
- 新增持久化玩家状态时：先决定归属 `PlayerSectionKind`，再在对应组件落字段，聚合↔组件映射在 `ApplyAggregateState`/`ExportSnapshotCore` 内维护。

## 2. 并发与所有权铁律

1. **分片单写者**：玩家按 `PlayerId` 稳定哈希路由到唯一 `EcsWorldShard`；该分片的一切变更必须经 `ExecuteOwned`/`ExecutePlayerOwned` 在其 `YuoFiber` 内执行。**禁止在 Fiber 外读写组件。**
2. **跨边界不持引用**：`EcsPlayerRuntimeAdapter`、WS/HTTP 层、其它分片不得缓存 `YuoEntity`/`YuoComponent`/`PlayerAggregate` 引用；跨边界一律传数据（DTO、快照、值）。
3. **玩家级 gate**：适配器对同一 `playerId` 的命令与推进以 `SemaphoreSlim` 串行；分片所有权 gate 为兜底，不得依赖 Fiber 调度巧合。批次取得 gate 后必须重新确认玩家仍已加载且目标时刻晚于当前时钟；只能推进实际持有 gate 的玩家，不能用全世界 Tick 包含本批开始后新加载的实体。装载失败必须清理部分建立的缓存/实体并归还已知未隔离的租约，提交结局未知的 fenced 玩家例外保留恢复证据。
4. **多实例租约闸门**：`IPlayerOwnershipStore`/`game.player_runtime_leases` 是跨实例写保护——加载 `Acquire`、命令/推进/刷盘前去抖 `Renew`（lease/3）、卸载 `Release`；丢失即 `PlayerOwnershipLostException` fail-closed 并逐出实体。新增"加载玩家/遍历已加载玩家"的路径都必须走同一闸门，不得绕过。
5. **活动实体过滤**：逐玩家 Tick 前 `World.SetActiveEntities(玩家+遭遇)`、`finally` 清除；新增 `IUpdate` 系统必须遵守 `ShouldUpdate` 或战斗活动闸门。显式指定实体的同步业务 tag 只消费当前负载，不能套用 Update 的跳过闸门，否则命令和批量路径会漏执行。
6. **`EcsClockComponent` 三开关**：`ActivePlayerId`（当前活动玩家）、`SkipWorldUpdate`（本 tick 是否整体跳过世界系统）、`SkipWorldUpdateEntities`（批量管线计数）——语义由 shard 管理，业务代码只读不改。

## 3. 业务命令开发（`MutatePlayer` 通道）

命令路由在 `src/Server/EcsPlayerRuntimeAdapter.cs` `ExecuteAsync` 的 `switch (command)`。新增命令的标准形态：

```csharp
case "biz.someCommand":
    var touched = _runtime.MutatePlayer(playerId, aggregate =>
    {
        // 1) 领域校验（快速失败，异常向上抛）
        // 2) 领域操作：直接调 Domain 原语 / 聚合方法
        // 3) 需要钱包副作用时同步 wallet 并在事务内提交
        aggregate.MarkDirty(PlayerSectionKind.Xxx); // 精确到段
    });
    // touched = 本次变更新增脏标记差集 → 回执 delta 窄化编码
```

规则：

- **属性输入修订号随组件状态存活**：`MutatePlayer` 视图直接修改组件持有的分段；版本化集合与输入修订号不重建、不回退。`WireAttributesChanged` 与 `AttributeRevision` 依赖其单调性，不得绕过所有权通道手写头字段。
- **脏标记用 `MarkDirty`/`DirtySectionMarks` 流水号差集**，不是脏集合差集：已脏分段再次被触及也必须计入 `TouchedSections`（回执窄化依赖）。
- **资产副作用**（物品/灵石/装备实例）走 `AssetWallet` + `economy.*` 事务通道；消耗/奖励来源分段、模拟时钟与命令回执必须随资产事务原子写入 durable 队列。纯模拟时钟推进也必须留下 durable 状态，不依赖事后脏龄刷盘。提交只编码实际脏分段，禁止先全量编码再过滤造成无关分段热路径分配；该优化不改变 durable 提交频率、原子性或业务时钟。
- **失败恢复**：请求前冻结快照与 DirtySections 集合；确定未提交时恢复该快照和集合，DirtySectionMarks 流水号始终单调不回退。提交结果未知必须以原幂等键核验，仍不明确则 fail-closed，不能盲目回滚后继续写。成功事务仅确认真正写入的脏段。
- `SyncAssetBalances`/`SyncWalletAfterCommit` 对齐内存镜像；聚合内**永不**存资产。
- **幂等回执**：写命令必须接 `requestId` 幂等检查；`ShouldPersistReceipt` 语义与原聚合运行时一致（回执仅在携带脏段/状态变化时落库）。
- **决斗/遭遇副作用**：需要弃战路由时用 `destroyEncounters: true` + `RouteDuelForfeit`，不得手写销毁。
- **不确定性的新命令一律复用 `MutatePlayer`**；只有纯队列/状态操作（入队、移除、presence）才允许直接走 ECS 原语（`EnqueueAction`/`RemoveAction`/`SetPresence`）。
- 新增命令必须同步：`ExecuteAsync` 路由 → 协议 DTO（`src/Protocol`）→ 前端契约检查（`check-frontend-contracts.mjs`）→ `Ecs.Tests`/`Server.Tests` 回归。

## 4. 推进与管线

### 4.1 请求级管线（`EcsActionPipeline`）

一次玩家推进 = `BeginRequest(entity, catalog, target)` → Fiber Tick 内按边界驱动系统顺序：`ActionClock(100) → ActionQueue(200) → Gathering/Crafting → Boundary/Travel → TimedYield → CombatBoundary → CombatYield → PlayerClockCommit`。管线游标 `_cursor` 即玩家的业务时钟；规则实现由对应 ECS System、`ActionExecutionRules.*` 和 `CombatActionRules` 持有，`EcsActionPipeline` 只提供工作区和提交边界。

### 4.2 不变量（违反即 `DomainException`，不得静默）

- `NextBoundaryAt` 单调不退；批量推进 `BulkStepCount > MaxTransitionsPerAdvance` 即失败。
- **亚 tick 残差**：`DateTimeOffset` 是 100ns 量化，`double` 余量转时间戳可能零进展——步长按 `min(remaining, needed)` 逻辑量消费，`frameTarget >= windowEnd` 时亚 tick 残差强制归零。新增推进路径必须遵守同一口径，禁止从量化时间戳反复反推剩余预算。
- `RemainingRepeats == -1` 无限循环绝不出队。
- 随机数顺序、快照导出顺序、脏段语义与聚合运行时逐位一致。

### 4.3 批量推进资格（`CanBulkAdvance`）

仅"无战斗的计时行动"（Exploration/Alchemy，`ExecutionKind.Timed`，无存活遭遇、非战斗中）允许合并自然窗口直推；战斗/旅行/跨类型行动必须回到完整系统顺序。新增行动类型若在 `Timed` 家族，先评估是否可安全 bulk：凡会改随机数序列或边界次序的均不可。

## 5. 系统编写规则

```csharp
[SystemOrder(NNN)]   // 固定同一 tag 的订阅者顺序；跨 tag 的顺序由调用调度器决定
public sealed class MySystem : YuoSystem<MyComponent>, IUpdate
{
    public override void Run(MyComponent comp)
    {
        if (!EcsSystemContext.ShouldUpdate(comp.Entity)) return;          // 活动集/SkipWorldUpdate 闸门
        using var m = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.MySystem"); // 编译期可剔除
        // ... comp.Entity.GetComponent<OtherComponent>()
    }
}
```

- Update 系统当前以一个主组件参与调度；需按实体能力选择的同步 tag System 可声明多个泛型组件，只有组件交集完整的实体参与。跨组件读取经 `Entity.GetComponent<T>`（稠密索引直查）。
- 系统内**禁止** `Dictionary<Type,…>` 派发、反射扫描、每实体分配；迭代期增删组件走内核待决缓冲（`AddComponent`/`RemoveComponent` 已安全），不得直接改 `Entities` 集合。
- 内核 `CloseSystemTry=false` 时隔离异常；道化分片显式设置 `CloseSystemTry=true`，System 异常必须向上传递给请求失败和事务恢复路径。业务不变量失败应抛 `DomainException`，不允许吞掉。
- 新阶段名同步 `BenchStageReports.Known`；观测埋点必须走 `RuntimeInstrumentation`/`YuoCoreTrace`（`DAOHUA_OBSERVE_OFF`/`YUOCORE_TRACE_OFF` 下零指令），禁止自造计时器。

## 5.5 战斗触发器域（`RunSystem<tag>` 段内派发，不进 Update 序列）

战斗命中等结算点产出的反应效果走**触发器派发**，不走每拍扫描：

```text
ResolveAttack 落账
  → encounter.PendingTriggers 入队（攻击方/受击方各一条）
  → sink.Drain：ECS 路径 RunSystem<IOnHitTrigger>(unitEntity)；无实体路径内联数学
  → 主事件灵力回填 + 衍生事件按序并入
（UpdateOutcome 永远在触发器结算之后 → OnHitHeal 可救活自己）
```

- **双路径同数学**：`CombatTriggerMath` 是唯一实现；`InlineCombatTriggerSink`（斗法/纯领域测试）与 `EcsCombatTriggerSink`（实体派发）参数同源——实体路径读 `Stats.Effects` 与 reactive 标记组件挂摘一致，内联直读 `Stats.Effects`。
- **Reactive buff 组件是能力标记**：`OnHitLeech`/`ManaLeech`/`Thorns` 不带参数，挂摘由 `CombatTriggerSurface.Attach/Sync` 按 `Stats.Effects` 管理（遭遇挂接 + `RefreshStats` 后同步）；参数永无双写。
- **新增触发器 checklist**：`CombatTriggerKind` 枚举 → `PendingCombatTrigger` 生产点（引擎结算点）→ `IOnXxxTrigger` tag + tag 映射（`TagTypeOf`）→ reactive 组件 + `YuoSystem<CombatantComponent, Marker>, IOnXxxTrigger` + `SystemOrder`（对齐内联手工序）→ `CombatTriggerMath` 数学 + `InlineCombatTriggerSink.Dispatch` 同序分支 → `CombatTriggerSurface.Sync` 挂摘条件 → parity/金色测试双路径验证。
- **RNG 与顺序**：触发器随机数走 `context.NextRoll()`（通道 64+，与核心结算 0~15 隔离）；消费顺序 = 入队顺序 + `SystemOrder`，无字典序。
- **负载传递**：`CombatTriggerInboxComponent.Trigger` 是派发期间唯一通道，派发后清 null；触发器不得改变边界时刻表（只可改数值与队列内容）。

## 5.6 结算统计（同步 tag）

行动、战斗、消耗品和成长规则在完成结算后提交 `PlayerStatsDelta`，只携带实际入账量。禁止传入任意账本修改委托，禁止由统计 System 再次计算或发放奖励。

```text
结算完成 → IPlayerStatsSink.Record
  → PlayerStatsInboxComponent 借用增量和请求上下文
  → RunSystem<IRecordPlayerStats>(玩家实体)
  → ActionStatsSystem(100) → OfflineStatsSystem(200) → DailyStatsSystem(300)
  → finally 清空信箱引用
```

- 行动账本只有在已建账时写入；离线账本仅 `Presence == Offline` 写入；每日账本先按 UTC+8 自然日切桶，再写入。
- 三账本按同一源集合顺序累加，最后标记 `Duel` 脏段。命令视图、普通推进和批量推进均走同一同步 sink。
- 独立 POCO 聚合使用 `ActionReportBook` 的内联记账入口；字段增量算法只有 `PlayerStatsDelta.ApplyTo` 一份。新统计字段必须同步补齐增量与账本序列化契约。
- 信箱是运行时工作状态，不进入存档；不得保留奖励集合或命令上下文至下次 tick。

## 5.7 战斗相位规则本体

`CombatEventAdvanceSystem.AdvanceTo` 负责自然边界选择、固定相位顺序和批量循环。Buff 到期/回复、复活、玩家出手、敌方出手、倒地打断与终局裁决由六个相位 System 的共享实现持有；实体派发和无世界测试门面调用同一份 ECS 实现。`DeterministicCombatEngine.AdvanceTo` 只做转发，不得重新实现调度。

玩家与怪物出手 System 共用 `CombatActSystem` 的技能起手、施放、普攻入账及命中反应规则。目标选择、随机抽样和纯伤害公式可由 System 调用引擎算法；不能把整个相位再交给引擎修改状态。

同刻顺序继续由边界调度器明确决定：Buff → 复活 → 玩家 → 按索引逐敌 → 倒地 → 终局。一次命中必须立即处理反应，再决定后续单位是否能出手。`PhaseContext` 只在同步派发期间挂在遭遇组件上，返回或异常时均清空；不得改成逐相位外层 tick。

## 5.8 队列变更（同步 tag）

`PlayerActionQueueMutation` 表达入队、优先插队、取消、调序、轮转和轮完成。命令视图与行动上下文均提交同一负载，不得直接增删 `Actions` 或另写次数裁决。

```text
PlayerActionQueueMutationComponent.Work.Begin
  → RunSystem<IPlayerActionQueueMutation>(玩家实体)
  → Prepare(100) → Recovery(110) → ReportArchive(120) → Apply(130) → CombatRelease(140)
  → finally Work.End
```

- `PlayerActionQueueRules` 持有唯一的参数校验/归一化、结构变更和轮完成裁决；阶段 System 分别承担最低气血恢复、账本归档、队首激活与版本/脏段提交、命令遭遇释放。
- 普通入队先保持 `Pending`，由行动准备激活；优先插队归一为一次，复制参数、归档同类旧实例并立即成为队首。`EnqueueAction` 复用同一规则，不能另留宽松校验。
- `RecordCompletedRound` 只递减正数重复；`FinishActionRound` 只有剩余次数为 0 才离队或循环。无限重复不得因轮完成出队，有限循环继续使用原实例和账本。
- 命令切换队首立即释放旧遭遇；正在推进的战斗会话继续在既有 Yield 清理点释放，不能提前销毁尚需消费结果的状态。
- 独立聚合内联执行同一阶段和规则；工作区按需建立并复用，返回后清空借用状态，不进入协议或存档。

## 6. 持久化与快照边界

- `ExportSnapshot`/`RestorePlayer`/`PlayerStateDocument` 是唯一序列化通道；新增持久化字段必须落 section 编码 + 文档字段 + 测试金色样本。
- `StateVersion`、`DirtyMarks`、`AttributeRevision` 与版本化列表计数随组件状态单调存活；快照恢复、视图命令和队列操作必须保住该语义。
- 脏段写回失败要保留可恢复证据；`AcknowledgeDirtySections`/`RestoreDirtySections` 只调集合，**不回退流水号**。
- `SnapshotUpdated` 推送仅在 `SimulationResult.Changed` 为真时发出；空闲玩家不得推 delta。

## 7. 新功能落地 checklist

| 场景 | 步骤 |
| --- | --- |
| 新命令 | 协议 DTO → `ExecuteAsync` case（`MutatePlayer`）→ 幂等/脏段差集 → 前端契约 → `Ecs.Tests`+`Server.Tests` |
| 新行动类型 | `config/game-data` 配表 + `ActionKind`/`ExecutionKind` → 管线边界接入 → bulk 资格评估 → parity/金色测试 |
| 新玩家持久化状态 | `PlayerSectionKind` 归属 → 组件字段 → 聚合映射双向 → `MarkDirty` 点 → 快照编码 → roundtrip 测试 |
| 新系统 | 组件归属 → `YuoSystem<T>` + `SystemOrder` + `ShouldUpdate` → 埋点 → `Ecs.Tests` 系统级用例 |
| 新战斗触发器 | `CombatTriggerKind` + 引擎入队点 → tag + `TagTypeOf` → reactive 组件 + System + `CombatTriggerMath` + 内联分支 → `CombatTriggerSurface.Sync` → 双路径 parity |
| 新实体类别 | 组件组合 + `CreateXxx`/`RemoveXxx` 分片 API + 生命周期测试 |
| 性能改动 | 先测 baseline（`bench-verify.sh`/`bench-matrix.sh`），结论必须附数据；热路径改动跑 `Ecs.Tests` 全量 |

## 8. 验证门禁

- `scripts/verify.sh`：最小完整门禁（构建+三测试套件+DataTool+compose）。
- `tests/Ecs.Tests`：ECS 运行时/适配器边界回归（30+ 用例）；`tests/Domain.Tests`：领域纯逻辑；`tests/Server.Tests`：宿主+PG 集成（需 `DAOHUA_MARKET_TEST_CONNECTION`）。
- 内核改动（`vendor/YuoCore`）先读其 `AGENTS.md` 规范链；内核测试 `YuoCore.Game.Tests`/`Runtime.Tests` 必须全绿；submodule 本地提交不推送，父仓只更新 gitlink + `CORE_SHA` 钉。

## 9. 行动参与会话（TASK-20261009-05）

- `ActionParticipationSessionEngine` 是独立会话的唯一战斗推进入口，持有冻结名册与当前战斗检查点；成员行动管线遇到参与引用时等待，不恢复/推进同场遭遇，也不重复计算战间回复。
- 会话与玩家分片各自串行；会话不得同步获取成员 gate。推进检查点和成员收件箱先 durable，再由 `EcsPlayerRuntimeAdapter` 在玩家门内消费个人增量。
- 消费只通过 `ApplyActionParticipationSettlement`，同序号不重奖；资产、玩家分段与收件箱回执原子提交。保留事件时刻、原账本统计入口与个人装配/设置。
- 会话独立于队长/队伍生命周期；服务重启从检查点恢复，成员加载必须先消费结束结算再推进后续个人行动。
