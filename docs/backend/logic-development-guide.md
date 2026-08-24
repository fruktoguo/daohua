# 道化太初 · 服务端业务逻辑开发指南与纯净聚合 ECS 规约

本文档作为《道化太初》服务端**业务功能开发、系统扩展与日常编码**的唯一权威指南。系统严格采用**单体聚合 ECS 实体（PlayerComponent）+ 纯函数离散模拟引擎（SimulationEngine）**架构，确保在 2c4g/40g 服务器预算下达成 5000+ 账号、500+ 网络并发与 2000+ 冷补算的高吞吐确定性结算，杜绝任何多套平行实现。

---

## 一、 核心开发哲学：纯净聚合 ECS + 纯函数模拟推进

在 YuoCore 架构下，服务端不使用固定频率轮询，而是通过 **实体聚合组件 (PlayerComponent) + 纯函数推进引擎 (SimulationEngine)** 实现高效、零锁、确定性的离散事件推进：

```text
┌────────────────────────────────────────────────────────────────────────┐
│               【纯净聚合 ECS 与纯函数离散模拟流转模型】                 │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 实体与权威状态（Imperative Entity State）                           │
│    YuoEntity 上挂载单体聚合组件 PlayerComponent（内含 PlayerState）      │
│    在概念与领域边界上严密划分为 7 大子切片（普通 C# 嵌套类）：          │
│    ├── Inventory ────> 背包物品持有与耐久（Dictionary<string, double>）│
│    ├── Cultivation ──> 灵气储备、境界层级与 Buff 列表                   │
│    ├── CombatResources > 当前生命、灵力与技能/丹药冷却                 │
│    ├── Queue ────────> 动作队列、循环开关与阶段进度游标                 │
│    ├── Stats ────────> 生涯统计、战斗胜负与灵石持有                     │
│    ├── World ────────> 大地图节点、寻路与遭遇状态                       │
│    └── DirtySections ─> 脏标记位掩码（驱动增量同步与落盘）               │
│                                                                        │
│ 2. 纯函数离散模拟引擎（Functional Core · 静态无副作用纯函数）           │
│    SimulationEngine.AdvanceTo(PlayerState state, DateTimeOffset now)   │
│    ├── 权威真源：挂机动作（打坐/采药/炼丹/赶路）与战斗推进的唯一实现   │
│    ├── 零锁零反射：直接通过 state.Inventory 等指针偏移极速推进        │
│    └── 100% 确定性：在线 1 秒 1 次与离线 48 小时补算执行同一套纯逻辑   │
│                                                                        │
│ 3. 宿主路由与增量推流（Host Command & Delta Sync）                     │
│    GameHost.HandleCommandAsync                                         │
│    ├── 在玩家专属 Fiber（单线程分片）内执行命令校验与应用               │
│    ├── 每次变更标记 state.DirtySections                                │
│    └── WebSocket 仅将变动切片打包为 Delta 增量推流给前端                │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 二、 实体聚合组件与领域状态规范

为了彻底消除每秒 Tick 与频繁落盘快照时反射拆装多组件带来的 GC 压力与性能损耗，运行时统一将状态聚合挂载于实体的 `PlayerComponent`：

```csharp
namespace Domain.Models;

using YuoCore.Ecs;

/// <summary>
/// 挂载于 YuoEntity 上的玩家聚合组件（服务端运行时权威状态载体）。
/// </summary>
public sealed class PlayerComponent : YuoComponent
{
    public PlayerState State { get; set; } = new();
}
```

### `PlayerState` 内部 7 大领域切片职责：
1. **`Queue`（动作队列 `ActionQueueState`）**：记录当前挂机条目（`Current`）、等待队列（`Entries`）、剩余重复次数、阶段耗时与循环开关（`LoopEnabled`）。
2. **`Inventory`（储物背包 `InventoryState`）**：字典结构存储物品持有量（`Items`），支持资产原子扣减与增量产出。
3. **`Cultivation`（修炼与增益 `CultivationState`）**：记录当前修为（`Qi`）、已消耗修为（`Exp`）、境界阶位与当前生效的持续 Buff 列表。
4. **`CombatResources`（战斗资源 `CombatResourcesState`）**：记录当前生命值（`Hitpoints`）、灵力值（`Manapoints`）、技能 CD 字典与消耗品 CD 列表。
5. **`Stats`（生涯统计 `PlayerStatsState`）**：记录灵石数量（`Spirit`）、打坐次数、采集数、炼丹数、战斗胜负场次与击杀统计。
6. **`World`（世界位置 `WorldState`）**：记录角色所处的大地图节点 ID、移动目标点与遭遇战备状态。
7. **`DirtySections`（增量脏标记 `DirtySections`）**：按位掩码（`Inventory | Stats | Combat | Buffs | Queue | Events`），集中追踪当前 Tick 发生变化的子域，供 WebSocket 增量协议（Delta）精准推流。

---

## 三、 动作推进与业务逻辑：唯一权威 `SimulationEngine`

全工程**只有一套**动作与战斗推进实现——`src/Domain/Simulation/SimulationEngine.cs`。严禁创建任何第二套执行器或过渡系统：

### 核心推进流程（`SimulationEngine.AdvanceTo`）：
1. **时间推进校验**：检查 `now >= state.LastSimulatedAt`，计算时间差量；
2. **动作推进循环**：
   - 若队列中有进行中的动作（打坐、采集、炼丹、赶路），按时间片推进进度；
   - 到达耗时边界时结算产出（如引气入体 10s 获得 10.0 修为与 10.0 灵力）；
   - 扣减剩余次数，若全部完成且开启循环则克隆入队，否则出队；
3. **战斗遭遇推进**：若处于历练/遭遇状态，调用 `CombatSimulator` / `CombatGroupSimulator` 执行确定性战斗回合推演与 5 秒自然回复；
4. **脏标记标记**：变更时更新 `state.DirtySections` 并记录事件日志。

---

## 四、 指令处理与网络流转规范

客户端发送 WebSocket 命令时，由 `GameHost.HandleCommandAsync` 集中路由并在玩家 Fiber 内串行处理：

```csharp
// 指令处理规范（先校验、后修改、即时标记脏）：
// 1. 根据 PlayerId 定位 PlayerShardHost (Fiber)；
// 2. 在 Fiber 串行上下文中获取 playerEntity.GetComponent<PlayerComponent>().State；
// 3. 校验材料充足性、境界条件与位置合法性；
// 4. 执行状态修改，标记 state.DirtySections |= DirtySections.Inventory；
// 5. 生成命令回执 CommandAck 或通过 WebSocket 推送增量 Delta。
```

---

## 五、 编码铁律与禁忌

1. **数值统一使用 `double`**：所有属性、倍率、收益率和时长一律使用 `double` 精度，杜绝整除截断错误；
2. **单一真源铁律**：所有时间与产出结算严格以 `SimulationEngine` 为唯一真源，禁止编写任何第二套执行器或调度系统；
3. **禁止跨 Fiber 直接持有 Entity**：跨分片通信必须走 Actor 消息或 Coordinator 协调；
4. **禁止过度防御与静默吞异常**：内部状态机与不变量被破坏时快速失败，保留清晰错误日志。
