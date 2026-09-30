# 道化太初 · 服务端业务逻辑开发指南（聚合单写者模型）

本文档作为《道化太初》服务端**业务功能开发、系统扩展与日常编码**的唯一权威指南。系统采用 **YuoCore ECS 运行时（`EcsGameRuntime`/`EcsWorldShard`/`YuoFiber` 分片单写者）+ `PlayerAggregate` 文档往返 + 纯函数离散模拟引擎 `SimulationEngine`** 架构，确保在 2c4g/40g 服务器预算下达成 5000+ 账号、500+ 网络并发与 2000+ 冷补算的高吞吐确定性结算，杜绝任何多套平行实现。

> 生产运行时为 YuoCore ECS：`src/Server` 装配 `vendor/YuoCore`（上游 `ecs-optimized` 分支），业务层经 `EcsPlayerRuntimeAdapter`（`IPlayerRuntime` 实现）进入 `EcsWorldShard.MutatePlayer` 通道——聚合在命令内重建、执行领域操作、整体回写组件。`*Component`/`*System` 命名仅限 `src/Ecs` 与内核内使用；`Task*`/`Executor*` 历史命名仍禁用。OOP/基线对照设施已退役删除。
>
> **ECS 侧开发细节以 [`ecs-development-guide.md`](./ecs-development-guide.md) 为唯一权威**：组件/系统/分片/管线规则、`MutatePlayer` 契约、批量推进资格、脏标记差集、持久化边界与落地 checklist 以该文档为准。

---

## 一、 核心开发哲学：聚合单写者 + 纯函数模拟推进

服务端不使用固定频率轮询，而是通过 **玩家聚合（PlayerAggregate 文档往返）+ ECS 分片 Fiber 串行上下文 + 纯函数推进引擎（SimulationEngine）** 实现高效、零锁、确定性的离散事件推进：

```text
┌────────────────────────────────────────────────────────────────────────┐
│               【聚合单写者与纯函数离散模拟流转模型】                    │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 聚合与权威状态（Imperative Aggregate State）                        │
│    PlayerAggregate 内含 PlayerSectionKind 八分段状态：                  │
│    ├── Progression ──> 境界等级、灵气修为、历史总修为                   │
│    ├── World ────────> 大地图节点/边位置、坐标与探索种子                │
│    ├── Combat ───────> 生命/灵力、攻防、RegenCarrySeconds、技能状态     │
│    ├── Buffs ────────> 生效 Buff 与到期边界                            │
│    ├── Loadout ──────> 装备实例引用、已学功法、自动使用槽               │
│    ├── Settings ─────> 循环/自动移动开关 LoopEnabled/AutoTravelEnabled、语言等低频偏好 │
│    ├── ActionQueue ──> 行动队列（RemainingRepeats == -1 为无限循环）    │
│    └── Duel ─────────> 斗法遭遇计数、冷却、暂存区与战报账本             │
│    ※ 资产（灵石/物品余额/装备实例）不在聚合内，走 AssetWallet+economy   │
│                                                                        │
│ 2. 纯函数离散模拟引擎（Functional Core · 确定性推进）                   │
│    SimulationEngine.AdvanceTo(aggregate, now)                          │
│    ├── 权威真源：挂机行动（引气/采药/炼丹/赶路）与战斗推进的唯一实现   │
│    ├── 零锁：由玩家所属 ECS 分片 Fiber 串行调用                         │
│    └── 100% 确定性：在线秒级推进与离线 48 小时补算执行同一套纯逻辑     │
│                                                                        │
│ 3. 宿主路由与增量推流（Host Command & Delta Sync）                     │
│    EcsPlayerRuntimeAdapter.ExecuteAsync(playerId, command, payload,..) │
│    ├── 玩家级 SemaphoreSlim 串行 → 分片 Fiber 内 MutatePlayer 执行     │
│    ├── 聚合重建→领域操作→回写组件→脏段差集（DirtyMarks 流水号）        │
│    └── WebSocket 仅将变动分段打包为增量推流给前端                       │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 二、 聚合分段职责规范

`PlayerAggregate`（`src/Domain/Player/PlayerAggregate.cs`）按 `PlayerSectionKind` 八分段组织状态，**分段归属必须准确**：

1. **`Progression`（`PlayerProgression`）**：境界阶位、当前修为、历史总修为。
2. **`World`（`PlayerWorldState`）**：所在节点/边 ID、坐标、行进进度与探索随机数种子。
3. **`Combat`（`PlayerCombatState`）**：当前生命/灵力、攻防面板、`RegenCarrySeconds` 回复余量、`PlayerCombatSkillState`（技能冷却与吟唱）。
4. **`Buffs`（`PlayerBuffState`）**：当前生效 Buff 及到期边界。
5. **`Loadout`（`PlayerLoadoutState`）**：已装备物品实例引用、已学功法槽、自动使用槽（`AutoUseSlotState`）。
6. **`Settings`（`PlayerSettingsState`）**：低频偏好——**循环开关 `LoopEnabled` 与自动移动开关 `AutoTravelEnabled` 在 Settings，不在 ActionQueue**；语言、通知等。
7. **`ActionQueue`（`QueuedAction` 列表）**：进行中与排队行动、进度、剩余重复次数、远征/移动子状态。
8. **`Duel`（`PlayerDuelState`）**：斗法遭遇计数、冷却、暂存区与战报账本；记账点口径见 `docs/design/行动统计系统.md`。

**资产边界**：灵石、可堆叠物品余额、唯一装备实例一律由 `AssetWallet` + `economy.*` 关系表承载，不得塞进聚合分段；聚合内不保留 Inventory/Stats 切片。

---

## 三、 动作推进与业务逻辑：唯一权威 `SimulationEngine`

全工程**只有一套**动作与战斗推进实现——`src/Domain/Simulation/SimulationEngine.cs`。严禁创建任何第二套执行器或调度系统：

### 核心推进流程（`SimulationEngine.AdvanceTo`）：
1. **时间推进校验**：`now >= aggregate.LastSimulatedAt`，计算差量；
2. **行动推进循环**：
   - 队列头部行动按时间片推进；
   - 到达耗时边界结算产出——引气入体（`cultivation.draw-qi`，10 秒/轮）每轮结算为 **qi 1/秒（10s 得 10.0 修为）+ mana 1/轮（+1.0 灵力）+ experience 10/轮（+10.0 功法/历练经验）**；
   - 扣减剩余次数；`RemainingRepeats == -1`（无限循环）绝不出队，只有正数递减至 0、显式取消或前置条件断绝才移出队列；
3. **战斗遭遇推进**：战斗行动由战斗状态机与 `CombatSkillEngine` 逐只交战推进；自然回复当前按 5 秒基线周期整额结算（`RegenCarrySeconds` 携带不足一个周期的余量），回复量由基础属性表 hpRegen/mpRegen 锚点提供；
4. **脏标记**：变更时调用 `MarkDirty(PlayerSectionKind.Xxx)` 记录脏分段。

---

## 四、 指令处理与网络流转规范

客户端命令经 `/api/v1/players/{playerId}/commands` 或 WebSocket 进入 `EcsPlayerRuntimeAdapter.ExecuteAsync`（`IPlayerRuntime` 实现），在玩家级 `SemaphoreSlim` 串行后进入所属 `EcsWorldShard` 的 Fiber 内路由执行：

```csharp
// 指令处理规范（先校验、后修改、即时标记脏）：
// 1. 按 playerId 进入玩家级 gate，续租 game.player_runtime_leases；
// 2. 先验 requestId 幂等回执与 expectedStateVersion；
// 3. switch (command) 路由：需要聚合的走 MutatePlayer（组件状态→PlayerAggregate
//    →领域操作→回写→脏段差集），纯队列/状态操作直接走 ECS 原语；
// 4. 钱包/市场/建筑等副作用走原事务通道（economy 提交 + 回滚语义不变）；
// 5. 返回 PlayerCommandResult；增量经 ProtocolSnapshotMapper 推流，
//    命令回执按 snapshotForm 携带 delta（精确触及差集）或 snapshot（兜底）。
```

现行命令名（以 `EcsPlayerRuntimeAdapter.ExecuteAsync` 路由表为准）：

| 命令 | 职责 |
| --- | --- |
| `action.enqueue` / `action.remove` | 行动入队（RepeatCount 1..1,000,000 或 -1 无限）/移除 |
| `queue.configure` / `queue.move` | 循环与自动移动开关（写 Settings）/队列排序 |
| `queue.auto_use.configure` | 自动使用槽配置（4 类 × 3 槽展示口径） |
| `loadout.replace` | 装备/功法/消耗品装配变更 |
| `ability.learn` | 功法秘籍/残卷研读（`item.tome.*`/`item.fragment.*`，`quantity` 支持批量逐本结算，幂等键必需） |
| `progression.breakthrough` | 境界突破 |
| `world.travel` | 世界节点移动（自动先推进模拟再规划路径） |
| `building.build` / `building.upgrade` / `building.contribute` | 节点公共建筑发起建造/发起升级/提交材料（注册表锁内校验钱包并原子扣减） |
| `market.place` / `market.cancel` / `market.claim` | 挂单/撤单/结算领取 |
| `player.rename` / `player.presence` | 改名/在线状态 |
| `simulation.advance` | 内部推进命令（补算/心跳路径使用） |

---

## 五、 编码铁律与禁忌

1. **数值统一使用 `double`**：所有属性、倍率、收益率和时长一律使用 `double` 精度，杜绝整除截断错误；
2. **单一真源铁律**：所有时间与产出结算严格以 `SimulationEngine` 为唯一真源，禁止编写任何第二套执行器或调度系统；
3. **禁止跨边界持有聚合引用**：跨玩家/跨实例交互必须以数据形式（命令、DTO、事务结果）穿越边界，不得直接持有 `PlayerAggregate`；
4. **脏标记精确到分段**：修改哪段就 `MarkDirty` 哪段，禁止图省事全量标脏造成写放大；
5. **禁止过度防御与静默吞异常**：聚合内不变量被破坏时快速失败，保留清晰错误日志与证据。
