# 道化太初 · 服务端业务逻辑开发指南

本文档作为《道化太初》服务端**业务功能开发、系统扩展与日常编码**的唯一权威指南。所有服务端业务逻辑（如采集、打坐修炼、动作队列、历练、炼丹等）必须严格遵循本文档规定的 **YuoCore 原生 ECS + 自定义 SystemTag 离散事件驱动架构**。

---

## 一、 核心开发思想：ECS 组件化 + SystemTag 离散事件驱动

在 YuoCore 架构下，挂机游戏**严禁每帧轮询（No-Polling）**，而是通过 **数据组件 (Component) + 自定义标签 (SystemTag) + 事件驱动系统 (YuoSystem)** 实现极致优雅与高性能：

```text
┌────────────────────────────────────────────────────────────────────────┐
│                   YuoCore 原生 ECS 业务流转模型                        │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 数据组件 (Component) ──> 挂载在 PlayerEntity 上（纯数据载体）        │
│                             - ActionQueueComponent (队列状态与时间戳)  │
│                             - InventoryComponent   (背包资产)          │
│                                                                        │
│ 2. 调度触发 (Schedule)  ──> 10秒后到期，调度器精准唤醒执行：            │
│                             YuoWorld.RunSystem<IActionComplete>(entity)│
│                                                                        │
│ 3. 标签系统 (YuoSystem) ──> 实现了 IActionComplete 的系统自动匹配执行： │
│                             GatheringActionCompleteSystem.Run(...)     │
│                             - 结算产物入包、扣减次数、流转下一动作     │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 二、 典范案例：采集系统（Gathering）全流程实现

以【灵草采集行动】为例，完整演示 YuoCore 原生 ECS 的开发标准：

### 步骤 1：定义业务事件标签（`ISystemTag`）
在 `Domain/Common/` 或 `Domain/Systems/` 中定义领域事件标签：

```csharp
namespace Domain.Common;

using YuoCore.Ecs;

/// <summary>
/// 行动/动作完成事件标签（当动作耗时到达，触发该标签下的所有完成系统）。
/// </summary>
public interface IActionComplete : ISystemTag { }

/// <summary>
/// 行动/动作启动事件标签（动作入队开始执行时触发）。
/// </summary>
public interface IActionStart : ISystemTag { }

/// <summary>
/// 境界突破成功事件标签。
/// </summary>
public interface IBreakthroughSuccess : ISystemTag { }
```

---

### 步骤 2：定义实体数据组件（`YuoComponent`）
在 `src/Domain/Components/` 下创建纯数据组件，**严禁在组件中堆砌业务方法**：

```csharp
namespace Domain.Components;

using YuoCore.Ecs;
using Domain.Common;

/// <summary>
/// 动作队列中的单个执行条目（纯数据载体）。
/// </summary>
public sealed class QueuedActionItem
{
    public Guid EntryId { get; set; } = Guid.NewGuid();
    public ActionType ActionType { get; set; } = ActionType.Exploration;
    public string ActionId { get; set; } = string.Empty;
    public ItemId TargetItemId { get; set; }
    public int RemainingRepeats { get; set; } = 1;
    public double DurationSeconds { get; set; } = 10.0;
    /// <summary>本轮动作开始绝对时间戳</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>本轮动作完成截止时间戳</summary>
    public DateTimeOffset DueAt { get; set; }
}

/// <summary>
/// 玩家动作队列组件：挂载在 PlayerEntity 上。
/// </summary>
public sealed class ActionQueueComponent : YuoComponent
{
    public List<QueuedActionItem> Entries { get; } = [];
    public bool LoopEnabled { get; set; } = true;

    /// <summary>获取当前正在执行的动作项</summary>
    public QueuedActionItem? Current => Entries.Count > 0 ? Entries[0] : null;

    /// <summary>获取当前动作的截止时间</summary>
    public DateTimeOffset? GetCurrentDueTime() => Current?.DueAt;
}

/// <summary>
/// 玩家背包组件：挂载在 PlayerEntity 上。
/// </summary>
public sealed class InventoryComponent : YuoComponent
{
    public Dictionary<ItemId, double> Items { get; } = new();

    public double Get(ItemId id) => Items.TryGetValue(id, out var qty) ? qty : 0.0;

    public void Add(ItemId id, double delta)
    {
        if (Math.Abs(delta) < 1e-9) return;
        var next = Get(id) + delta;
        if (next < 0) throw new InvalidOperationException($"物品 {id} 扣减溢出");
        if (next == 0) Items.Remove(id);
        else Items[id] = next;
    }
}
```

---

### 步骤 3：编写业务完成系统（`YuoSystem<T1, T2...>, IActionComplete`）
在 `src/Domain/Systems/` 下编写系统，声明关心的组件组合并实现 `IActionComplete`：

```csharp
namespace Domain.Systems;

using YuoCore.Ecs;
using Domain.Components;
using Domain.Common;

/// <summary>
/// 采集行动完成结算系统：
/// 当触发 IActionComplete 标签时，YuoWorld 自动为拥有 [ActionQueueComponent, InventoryComponent] 的实体调用本系统。
/// </summary>
public sealed class GatheringActionCompleteSystem : 
    YuoSystem<ActionQueueComponent, InventoryComponent>, 
    IActionComplete
{
    public override void Run(ActionQueueComponent queue, InventoryComponent inventory)
    {
        var current = queue.Current;
        // 过滤：本系统只处理探索采集类动作
        if (current is null || current.ActionType != ActionType.Exploration) return;

        // 1. 采集产物结算（例如采集产出 5 株灵草）
        var harvestItemId = current.TargetItemId.IsValid ? current.TargetItemId : new ItemId("item.herb.lingcao");
        inventory.Add(harvestItemId, 5.0);

        // 2. 推进动作循环次数
        if (current.RemainingRepeats > 0)
        {
            current.RemainingRepeats--;
        }

        // 3. 判断是否需要出队或流转到下一项
        var now = DateTimeOffset.UtcNow;
        if (current.RemainingRepeats == 0)
        {
            queue.Entries.RemoveAt(0);
            if (queue.LoopEnabled)
            {
                // 循环开启，重新重置并放到队尾
                current.RemainingRepeats = 1;
                queue.Entries.Add(current);
            }
        }

        // 4. 若队列还有后续动作，计算下一轮截止时间
        if (queue.Current is { } next)
        {
            next.StartedAt = now;
            next.DueAt = now.AddSeconds(next.DurationSeconds);
        }

        // 5. 标记脏数据，通知网络同步
        queue.Entity.GetComponent<SyncDirtyComponent>()?.Mark(DirtySections.Inventory | DirtySections.Queue);
    }
}
```

---

### 步骤 4：到期调度与事件派发（`YuoWorld.RunSystem<T>`）

在服务端调度器中，当检测到 10 秒到期时间到达时，**只需调用一行 YuoWorld 派发代码**：

```csharp
// 调度器在 10 秒到期时唤醒，触发所有实现了 IActionComplete 的系统：
YuoWorld.RunSystem<IActionComplete>(playerEntity);

// YuoWorld 会自动依次执行：
// - GatheringActionCompleteSystem（结算采集并流转）
// - CultivationActionCompleteSystem（若是打坐则结算灵气）
// - AlchemyActionCompleteSystem（若是炼丹则结算丹药）
```

---

### 步骤 5：客户端交互与动作启动（`YuoActorHandler`）

客户端发送【开始采集】指令时，在玩家 Fiber 内安全装配数据并触发 `IActionStart`：

```csharp
namespace Server.Handlers;

using YuoCore.Actor;
using YuoCore.Ecs;
using Domain.Components;
using Domain.Common;
using Protocol;

public sealed class StartGatheringCommandHandler : YuoActorHandler<StartGatheringRequest, StartGatheringResponse>
{
    protected override async Task<StartGatheringResponse> Handle(
        YuoEntity playerEntity, 
        StartGatheringRequest request)
    {
        var queue = playerEntity.GetComponent<ActionQueueComponent>();

        var now = DateTimeOffset.UtcNow;
        var actionItem = new QueuedActionItem
        {
            ActionType = ActionType.Exploration,
            ActionId = request.ActionId,
            TargetItemId = new ItemId(request.ItemId),
            RemainingRepeats = request.Repeats,
            DurationSeconds = 10.0,
            StartedAt = now,
            DueAt = now.AddSeconds(10.0)
        };

        queue.Entries.Add(actionItem);
        playerEntity.GetComponent<SyncDirtyComponent>()?.Mark(DirtySections.Queue);

        // 触发行动开始事件
        YuoWorld.RunSystem<IActionStart>(playerEntity);

        return new StartGatheringResponse(Success: true, Message: "开始采集灵草。");
    }
}
```

---

## 三、 四大核心业务系统的 ECS + SystemTag 标准实现

```text
┌────────────────────────────────────────────────────────────────────────┐
│               四大修仙子系统与 IActionComplete 映射关系                 │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 采集探索 (Gathering)   ──> GatheringActionCompleteSystem            │
│ 2. 打坐修炼 (Cultivation) ──> CultivationActionCompleteSystem          │
│ 3. 炼丹制药 (Alchemy)     ──> AlchemyActionCompleteSystem              │
│ 4. 降妖历练 (Combat)      ──> CombatExpeditionCompleteSystem           │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. 打坐修炼系统（Cultivation）
- **触发标签**：`IActionComplete`
- **关注组件**：`[ActionQueueComponent, CultivationComponent, PlayerStatsComponent]`

```csharp
public sealed class CultivationActionCompleteSystem : 
    YuoSystem<ActionQueueComponent, CultivationComponent, PlayerStatsComponent>, 
    IActionComplete
{
    public override void Run(ActionQueueComponent queue, CultivationComponent cultivation, PlayerStatsComponent stats)
    {
        var current = queue.Current;
        if (current is null || current.ActionType != ActionType.Cultivation) return;

        // 1. 结算灵气增益
        var baseQiGain = 10.0; // 从 Catalog 读取
        cultivation.Qi = Math.Min(cultivation.QiCapacity, cultivation.Qi + baseQiGain);
        stats.CultivationCycles++;

        // 2. 检查是否蓄满灵气可突破
        if (cultivation.Qi >= cultivation.QiCapacity)
        {
            cultivation.IsPendingBreakthrough = true;
            // 触发可突破事件标签
            YuoWorld.RunSystem<IBreakthroughReady>(queue.Entity);
        }

        // 3. 队列流转
        queue.AdvanceNext(DateTimeOffset.UtcNow);
        queue.Entity.GetComponent<SyncDirtyComponent>()?.Mark(DirtySections.Cultivation | DirtySections.Stats | DirtySections.Queue);
    }
}
```

---

### 2. 炼丹制药系统（Alchemy）
- **触发标签**：`IActionStart`（入队扣材料） + `IActionComplete`（出丹结算）
- **关注组件**：`[ActionQueueComponent, InventoryComponent, PlayerStatsComponent]`

```csharp
public sealed class AlchemyActionCompleteSystem : 
    YuoSystem<ActionQueueComponent, InventoryComponent, PlayerStatsComponent>, 
    IActionComplete
{
    public override void Run(ActionQueueComponent queue, InventoryComponent inventory, PlayerStatsComponent stats)
    {
        var current = queue.Current;
        if (current is null || current.ActionType != ActionType.Alchemy) return;

        // 1. 产出丹药入包
        var pillItemId = current.TargetItemId;
        inventory.Add(pillItemId, 1.0);
        stats.PillsCrafted++;

        // 2. 队列流转（检查下一炉材料是否充足）
        queue.AdvanceNext(DateTimeOffset.UtcNow);
        queue.Entity.GetComponent<SyncDirtyComponent>()?.Mark(DirtySections.Inventory | DirtySections.Stats | DirtySections.Queue);
    }
}
```

---

### 3. 降妖历练系统（Combat Expedition）
- **触发标签**：`IActionComplete`
- **关注组件**：`[ActionQueueComponent, CombatResourcesComponent, InventoryComponent, PlayerStatsComponent]`

```csharp
public sealed class CombatExpeditionCompleteSystem : 
    YuoSystem<ActionQueueComponent, CombatResourcesComponent, InventoryComponent, PlayerStatsComponent>, 
    IActionComplete
{
    public override void Run(
        ActionQueueComponent queue, 
        CombatResourcesComponent resources, 
        InventoryComponent inventory, 
        PlayerStatsComponent stats)
    {
        var current = queue.Current;
        if (current is null || current.ActionType != ActionType.Combat) return;

        // 1. 调用确定性战斗结算核心（纯函数计算本场遭遇胜负与掉落）
        var battleOutcome = CombatSimulationCore.ResolveEncounter(queue.Entity, current.CombatEncounterId);

        // 2. 应用战斗结果
        if (battleOutcome.Victory)
        {
            stats.CombatWins++;
            foreach (var drop in battleOutcome.Drops)
            {
                inventory.Add(drop.ItemId, drop.Quantity);
            }
        }
        else
        {
            stats.CombatLosses++;
            // 战败脱战复活，气血恢复至 1
            resources.Hitpoints = 1.0;
        }

        // 3. 队列流转
        queue.AdvanceNext(DateTimeOffset.UtcNow);
        queue.Entity.GetComponent<SyncDirtyComponent>()?.Mark(DirtySections.Combat | DirtySections.Inventory | DirtySections.Stats | DirtySections.Queue);
    }
}
```

---

## 四、 离线快进补算引擎（Fast-Forward Engine）

当离线玩家重新上线，或高延迟冷计算节点进行补算时，**不需要每秒循环**，而是直接基于时间优先队列进行**离散事件飞跃（Time Leap）**：

```csharp
public static class FastForwardEngine
{
    /// <summary>
    /// 将玩家实体状态从上次模拟时间一次性确定性快进到当前时间（秒级补算数万秒）
    /// </summary>
    public static void FastForwardTo(YuoEntity playerEntity, DateTimeOffset targetTime)
    {
        var queue = playerEntity.GetComponent<ActionQueueComponent>();
        
        while (true)
        {
            var dueTime = queue.GetCurrentDueTime();
            // 如果队列空了，或者下一个截止时间在目标时间之后，快进结束
            if (dueTime is null || dueTime > targetTime)
            {
                break;
            }

            // 时间直接飞跃到到期时刻，触发完成系统！
            YuoWorld.RunSystem<IActionComplete>(playerEntity);
        }
    }
}
```

---

## 五、 YuoCore SystemTag 与事件体系设计规约

| 业务场景 | 推荐 SystemTag 接口 | 触发方式 |
| :--- | :--- | :--- |
| **行动完成** | `IActionComplete` | 10秒到期由调度器调用 `YuoWorld.RunSystem<IActionComplete>(entity)` |
| **行动启动** | `IActionStart` | 玩家提交新动作时调用 `YuoWorld.RunSystem<IActionStart>(entity)` |
| **灵气蓄满可突破** | `IBreakthroughReady` | 修炼满灵气时触发，通知前端高亮突破按钮 |
| **突破境界成功** | `IBreakthroughSuccess` | 突破成功后触发，刷新六维根基属性与解锁新地图/功能 |
| **战斗波次完成** | `ICombatEncounterComplete` | 遭遇击杀或战败时触发 |
| **天材地宝发现** | `IRareItemFound` | 采集触发稀有掉落时广播或记录生涯成就 |

---

## 六、 核心开发红线（开发禁忌）

1. **严禁在 System 中写死所有分支（If-Else 违背 OCP）**：
   - 采集、炼丹、修炼分别写为独立的 `GatheringActionCompleteSystem`、`AlchemyActionCompleteSystem`、`CultivationActionCompleteSystem`，各自实现 `IActionComplete`，互不干扰。
2. **严禁在组件中写业务逻辑**：
   - `ActionQueueComponent` 只存队列和时间戳，产物结算与次数扣减必须由对应的 System 执行。
3. **严禁每帧 Update 轮询**：
   - 挂机业务全部依靠 `DueAt` 到期触发 `YuoWorld.RunSystem<IActionComplete>`。
4. **数值全域统一使用 `double`**：
   - 收益率、数量、时长计算均保持 `double` 精度。
