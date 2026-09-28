# 道化太初 · ECS 业务开发规约

本文档是**在现行 ECS 运行时上开发业务逻辑的唯一权威规约**。架构总览见 `docs/backend/logic-development-guide.md` 与 `docs/backend/domain-data-architecture.md`；本文只管"怎么在 ECS 上正确写业务"。

## 0. 分层定位速查

| 层 | 位置 | 职责 | 禁止 |
| --- | --- | --- | --- |
| 领域原语 | `src/Domain` | 规则、结算、确定性模拟、聚合、配表 | ASP.NET/DB/宿主/ECS 类型 |
| ECS 运行时 | `src/Ecs` | 组件、系统、分片、行动管线、门面 | 业务规则实现、DB/网络 |
| 宿主适配 | `src/Server`（`EcsPlayerRuntimeAdapter`、`Program`）| 命令路由、会话、持久化边界 | 直接持有 `PlayerAggregate`/`YuoEntity` |
| 共享边界 | `src/Server.Clean`（编译 `src/Server/Clean/**`）| 仓储、DTO 映射、Worker | 领域规则 |
| 内核 | `vendor/YuoCore`（`ecs-optimized` 分支 submodule）| ECS/Fiber 框架原语 | 本仓任务默认不改 |

依赖方向：`Server → src/Ecs + Server.Clean + Domain + Protocol + YuoCore`；`src/Ecs → Domain + YuoCore`；`Domain → YuoCore(Abstractions/Ecs)`。禁止反向与循环。

## 1. 实体与组件模型

### 1.1 实体分类（`EcsWorldShard` 内）

| 实体 | 组件集 | 生命周期 |
| --- | --- | --- |
| 玩家 | `PlayerIdentity`/`Progression`/`World`/`Combat`/`Loadout`/`Buff`/`ActionQueue`/`Inventory`/`Economy`/`Settings`/`Duel`/`EcsActionExecution` | `CreatePlayer`/`RemovePlayer` |
| 地图节点 | `MapNode`/`NodeBuilding`/`NodePopulation` | `CreateMapNode` |
| 怪物 | `MonsterIdentity`/`Combatant`/`SkillState`/`Buff`/`DropProfile` | `CreateMonster` |
| 遭遇 | `CombatEncounter`/`CombatLog` | `CreateEncounter`/战斗结束销毁 |
| 世界主实体 | `EcsClockComponent`（`WorldMain`） | 分片构造即建 |

### 1.2 玩家组件 vs 聚合分段

- ECS 组件是**常驻权威状态**；`PlayerAggregate` 只在命令边界内经 `MutatePlayer` 重建、执行、回写（文档往返）。
- `PlayerSectionKind` 八分段与组件一一对应；`PlayerInventoryComponent`（资产余额镜像）不进聚合——资产真源仍是 `AssetWallet`+`economy.*`。
- 新增持久化玩家状态时：先决定归属 `PlayerSectionKind`，再在对应组件落字段，聚合↔组件映射在 `ApplyAggregateState`/`ExportSnapshotCore` 内维护。

## 2. 并发与所有权铁律

1. **分片单写者**：玩家按 `PlayerId` 稳定哈希路由到唯一 `EcsWorldShard`；该分片的一切变更必须经 `ExecuteOwned`/`ExecutePlayerOwned` 在其 `YuoFiber` 内执行。**禁止在 Fiber 外读写组件。**
2. **跨边界不持引用**：`EcsPlayerRuntimeAdapter`、WS/HTTP 层、其它分片不得缓存 `YuoEntity`/`YuoComponent`/`PlayerAggregate` 引用；跨边界一律传数据（DTO、快照、值）。
3. **玩家级 gate**：适配器对同一 `playerId` 的命令与推进以 `SemaphoreSlim` 串行；分片所有权 gate 为兜底，不得依赖 Fiber 调度巧合。
4. **多实例租约闸门**：`IPlayerOwnershipStore`/`game.player_runtime_leases` 是跨实例写保护——加载 `Acquire`、命令/推进/刷盘前去抖 `Renew`（lease/3）、卸载 `Release`；丢失即 `PlayerOwnershipLostException` fail-closed 并逐出实体。新增"加载玩家/遍历已加载玩家"的路径都必须走同一闸门，不得绕过。
5. **活动实体过滤**：逐玩家 Tick 前 `World.SetActiveEntities(玩家+遭遇)`、`finally` 清除；新系统 `Run` 里必须先看 `EcsSystemContext.ShouldUpdate(entity)` 闸门，不得假定全量扫描。
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

- **文档往返丢版本计数是既有语义**：`MutatePlayer` 内部已按 `prev + Δ` 续写属性输入修订（`ContinueAttributeRevisions`），`WireAttributesChanged` 判定与 `AttributeRevision` 依赖它——**不要绕过该通道手写回写**。
- **脏标记用 `MarkDirty`/`DirtySectionMarks` 流水号差集**，不是脏集合差集：已脏分段再次被触及也必须计入 `TouchedSections`（回执窄化依赖）。
- **资产副作用**（物品/灵石/装备实例）走 `AssetWallet` + `economy.*` 事务通道，`SyncAssetBalances`/`SyncWalletAfterCommit` 对齐内存镜像；聚合内**永不**存资产。
- **幂等回执**：写命令必须接 `requestId` 幂等检查；`ShouldPersistReceipt` 语义与原聚合运行时一致（回执仅在携带脏段/状态变化时落库）。
- **决斗/遭遇副作用**：需要弃战路由时用 `destroyEncounters: true` + `RouteDuelForfeit`，不得手写销毁。
- **不确定性的新命令一律复用 `MutatePlayer`**；只有纯队列/状态操作（入队、移除、presence）才允许直接走 ECS 原语（`EnqueueAction`/`RemoveAction`/`SetPresence`）。
- 新增命令必须同步：`ExecuteAsync` 路由 → 协议 DTO（`src/Protocol`）→ 前端契约检查（`check-frontend-contracts.mjs`）→ `Ecs.Tests`/`Server.Tests` 回归。

## 4. 推进与管线

### 4.1 请求级管线（`EcsActionPipeline`）

一次玩家推进 = `BeginRequest(entity, catalog, target)` → Fiber Tick 内按边界驱动系统顺序：`ActionClock(100) → ActionQueue(200) → Gathering/Crafting → Boundary/Travel → TimedYield → CombatBoundary → CombatYield → PlayerClockCommit`。管线游标 `_cursor` 即玩家的业务时钟。

### 4.2 不变量（违反即 `DomainException`，不得静默）

- `NextBoundaryAt` 单调不退；批量推进 `BulkStepCount > MaxTransitionsPerAdvance` 即失败。
- **亚 tick 残差**：`DateTimeOffset` 是 100ns 量化，`double` 余量转时间戳可能零进展——步长按 `min(remaining, needed)` 逻辑量消费，`frameTarget >= windowEnd` 时亚 tick 残差强制归零。新增推进路径必须遵守同一口径，禁止从量化时间戳反复反推剩余预算。
- `RemainingRepeats == -1` 无限循环绝不出队。
- 随机数顺序、快照导出顺序、脏段语义与聚合运行时逐位一致。

### 4.3 批量推进资格（`CanBulkAdvance`）

仅"无战斗的计时行动"（Exploration/Alchemy，`ExecutionKind.Timed`，无存活遭遇、非战斗中）允许合并自然窗口直推；战斗/旅行/跨类型行动必须回到完整系统顺序。新增行动类型若在 `Timed` 家族，先评估是否可安全 bulk：凡会改随机数序列或边界次序的均不可。

## 5. 系统编写规则

```csharp
[SystemOrder(NNN)]   // 固定执行顺序，注册顺序 = 声明顺序约束
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

- 一个系统**只挂一个主组件**；跨组件访问经 `Entity.GetComponent<T>`（稠密索引直查）。
- 系统内**禁止** `Dictionary<Type,…>` 派发、反射扫描、每实体分配；迭代期增删组件走内核待决缓冲（`AddComponent`/`RemoveComponent` 已安全），不得直接改 `Entities` 集合。
- 异常按实体隔离（`CloseSystemTry` 模式下写 stderr 并跳过该实体）；业务不变量失败应抛 `DomainException`，不允许吞掉。
- 新阶段名同步 `BenchStageReports.Known`；观测埋点必须走 `RuntimeInstrumentation`/`YuoCoreTrace`（`DAOHUA_OBSERVE_OFF`/`YUOCORE_TRACE_OFF` 下零指令），禁止自造计时器。

## 6. 持久化与快照边界

- `ExportSnapshot`/`RestorePlayer`/`PlayerStateDocument` 是唯一序列化通道；新增持久化字段必须落 section 编码 + 文档字段 + 测试金色样本。
- `StateVersion`、`DirtyMarks`、`AttributeRevision`、版本化列表计数在文档往返中按规约续写——改动 `ApplyAggregateState`/`MutatePlayer`/`RemoveAction` 时必须保住该语义。
- 脏段写回失败要保留可恢复证据；`AcknowledgeDirtySections`/`RestoreDirtySections` 只调集合，**不回退流水号**。
- `SnapshotUpdated` 推送仅在 `SimulationResult.Changed` 为真时发出；空闲玩家不得推 delta。

## 7. 新功能落地 checklist

| 场景 | 步骤 |
| --- | --- |
| 新命令 | 协议 DTO → `ExecuteAsync` case（`MutatePlayer`）→ 幂等/脏段差集 → 前端契约 → `Ecs.Tests`+`Server.Tests` |
| 新行动类型 | `config/game-data` 配表 + `ActionKind`/`ExecutionKind` → 管线边界接入 → bulk 资格评估 → parity/金色测试 |
| 新玩家持久化状态 | `PlayerSectionKind` 归属 → 组件字段 → 聚合映射双向 → `MarkDirty` 点 → 快照编码 → roundtrip 测试 |
| 新系统 | 组件归属 → `YuoSystem<T>` + `SystemOrder` + `ShouldUpdate` → 埋点 → `Ecs.Tests` 系统级用例 |
| 新实体类别 | 组件组合 + `CreateXxx`/`RemoveXxx` 分片 API + 生命周期测试 |
| 性能改动 | 先测 baseline（`bench-verify.sh`/`bench-matrix.sh`），结论必须附数据；热路径改动跑 `Ecs.Tests` 全量 |

## 8. 验证门禁

- `scripts/verify.sh`：最小完整门禁（构建+三测试套件+DataTool+compose）。
- `tests/Ecs.Tests`：ECS 运行时/适配器边界回归（30+ 用例）；`tests/Domain.Tests`：领域纯逻辑；`tests/Server.Tests`：宿主+PG 集成（需 `DAOHUA_MARKET_TEST_CONNECTION`）。
- 内核改动（`vendor/YuoCore`）先读其 `AGENTS.md` 规范链；内核测试 `YuoCore.Game.Tests`/`Runtime.Tests` 必须全绿；submodule 本地提交不推送，父仓只更新 gitlink + `CORE_SHA` 钉。
