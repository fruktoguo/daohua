# 道化太初 · 服务端业务逻辑开发指南

本文档作为《道化太初》服务端**业务功能开发、系统扩展与日常编码**的实现指南。任务队列的时间语义、战斗幂等、当前快照格式和一次性切换顺序以《任务队列系统与组件设计方案（深化版）》为唯一权威；本文只补充 YuoCore 的 Fiber、SystemTag 和组件接入边界，不再定义第二套队列推进器。

> **本地开发兼容清零**：当前不承担任何历史数据、旧快照、旧协议或旧组件的兼容。本文保留的旧代码片段只用于解释 YuoCore 的注入形态，不能作为兼容入口、过渡实现或新代码模板；新实现直接替换旧模型，禁止双读双写、legacy fallback 和迁移适配器。

---

## 一、 核心开发思想：ECS 组件化 + SystemTag 离散事件驱动

在 YuoCore 架构下，挂机游戏不使用固定频率逐秒轮询，而是通过 **数据组件 (Component) + 唯一过渡标签 (SystemTag) + 状态推导截止时间** 实现离散事件推进：

> **队列推进红线**：本文件中的旧示例用于说明 YuoCore 的组件注入形式，不是新的结算实现合同。实际队列必须以 `SimulationEngine.AdvanceTo` 为唯一时间/结算权威，由唯一的 `ActionTransitionSystem` 应用结果；不得照抄示例再创建第二套 `IActionComplete` 分发路径。

```text
┌────────────────────────────────────────────────────────────────────────┐
│                   YuoCore 原生 ECS 业务流转模型                        │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 数据组件 (Component) ──> 挂载在 PlayerEntity 上（纯数据载体）        │
│                             - ActionQueueComponent (队列状态与时间戳)  │
│                             - InventoryComponent   (背包资产)          │
│                                                                        │
│ 2. 调度触发 (Schedule)  ──> 10秒后到期，调度器精准唤醒执行：            │
│                             YuoWorld.RunSystem<IActionTransitionDue>(entity)│
│                                                                        │
│ 3. 唯一过渡系统 (YuoSystem) ──> 读取组件、调用 Executor、原子应用结果： │
│                             ActionTransitionSystem.Run(...)            │
│                             - 统一处理产物、次数、阶段和队列流转       │
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
/// 行动阶段到期标签。只有 ActionTransitionSystem 拥有队列推进权。
/// </summary>
public interface IActionTransitionDue : ISystemTag { }

/// <summary>
/// 行动启动只用于校验或产生命令，不得在此处单独扣除资产。
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

### 步骤 3：唯一过渡系统（`ActionTransitionSystem`）

所有行动到期推进统一由 `ActionTransitionSystem` 驱动，不再为各个子系统编写独立的推进 System：

```csharp
namespace Domain.Systems;

using YuoCore.Ecs;
using Domain.Components;
using Domain.Common;
using Domain.Executors;

/// <summary>
/// 全局唯一行动过渡系统：
/// 监听 IActionTransitionDue 标签，统一调用静态执行器 Executor* 并原子应用变更差量。
/// </summary>
public sealed class ActionTransitionSystem : 
    YuoSystem<ActionQueueComponent, InventoryComponent, CultivationComponent, CombatResourcesComponent, PlayerStatsComponent>, 
    IActionTransitionDue
{
    public override void Run(
        ActionQueueComponent queue, 
        InventoryComponent inventory, 
        CultivationComponent cultivation,
        CombatResourcesComponent resources,
        PlayerStatsComponent stats)
    {
        var current = queue.Current;
        if (current is null) return;

        // 1. 构建纯执行上下文
        var context = new ActionExecutionContext(
            PlayerEntity: queue.Entity,
            CurrentEntry: current,
            Inventory: inventory,
            Cultivation: cultivation,
            Resources: resources,
            Stats: stats
        );

        // 2. 调用对应纯静态执行器
        var delta = ActionExecutorRegistry.Execute(context);

        // 3. 原子应用变更差量并流转队列
        delta.Apply(inventory, cultivation, resources, stats);
        queue.AdvanceNext(delta.NextStatus);

        // 4. 集中标记脏区段
        queue.Entity.GetComponent<SyncDirtyComponent>()?.Mark(delta.AffectedSections);
    }
}
```

---

### 步骤 4：到期调度与事件派发（仅作反例对照）

旧方案曾在服务端调度器中直接派发多个完成系统。现在只允许派发单一的阶段到期入口：

```csharp
// 调度器在状态推导的截止时间唤醒：
YuoWorld.RunSystem<IActionTransitionDue>(playerEntity);

// ActionTransitionSystem 统一调用对应 Executor，并原子应用结果。
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

## 三、 四大核心业务系统的 ECS + SystemTag 依赖说明

以下代码用于展示组件依赖如何拆分，不能直接作为队列结算实现。采集、修炼、炼丹和战斗必须由同一个 `ActionTransitionSystem` 统一协调，执行器只能返回纯结果。

```text
┌────────────────────────────────────────────────────────────────────────┐
│               四大修仙子系统与 Executor 映射关系                       │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 采集探索 (Gathering)   ──> ExecutorGathering                       │
│ 2. 打坐修炼 (Cultivation) ──> ExecutorCultivation                     │
│ 3. 炼丹制药 (Alchemy)     ──> ExecutorAlchemy                         │
│ 4. 降妖历练 (Combat)      ──> ExecutorCombat                          │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. 打坐修炼执行器（`ExecutorCultivation`）
- **职责**：纯函数式结算修炼经验与灵力产出
- **输入输出**：接收只读上下文，返回 `ActionDelta`

```csharp
public static class ExecutorCultivation
{
    public static ActionDelta Execute(ActionExecutionContext ctx)
    {
        var delta = new ActionDelta();
        
        // 基础产出：10.0 修为 (FoundationExp), 1.0 灵力 (Qi)
        var expGain = 10.0;
        var qiGain = 1.0;

        // 结算持续消耗品加成 (如引气丹 +30%)
        if (ctx.HasActiveSustainedConsumable("item.qi-gathering-pill"))
        {
            expGain *= 1.30;
            qiGain *= 1.30;
        }

        delta.AddFoundationExp(expGain);
        delta.AddQi(qiGain);
        delta.AddCultivationCycles(1.0);
        delta.AffectedSections |= DirtySections.Cultivation | DirtySections.Stats | DirtySections.Queue;

        return delta;
    }
}
```

---

### 2. 炼丹制药执行器（`ExecutorAlchemy`）
- **职责**：校验配方材料并原子生成丹药产物

```csharp
public static class ExecutorAlchemy
{
    public static ActionDelta Execute(ActionExecutionContext ctx)
    {
        var delta = new ActionDelta();
        var recipe = ctx.GetRecipe(ctx.CurrentEntry.ActionId);

        // 扣除材料，产出丹药
        foreach (var input in recipe.Inputs)
        {
            delta.ConsumeItem(input.ItemId, input.Quantity);
        }
        foreach (var output in recipe.Outputs)
        {
            delta.ProduceItem(output.ItemId, output.Quantity);
        }

        delta.AffectedSections |= DirtySections.Inventory | DirtySections.Stats | DirtySections.Queue;
        return delta;
    }
}
```

---

### 3. 降妖历练执行器（`ExecutorCombat`）
- **职责**：确定性模拟战斗遭遇、计算胜负、掉落与战败脱战

```csharp
public static class ExecutorCombat
{
    public static ActionDelta Execute(ActionExecutionContext ctx)
    {
        var delta = new ActionDelta();
        var battleOutcome = CombatSimulator.ResolveEncounter(ctx);

        if (battleOutcome.Victory)
        {
            delta.AddCombatWins(1.0);
            foreach (var drop in battleOutcome.Drops)
            {
                delta.ProduceItem(drop.ItemId, drop.Quantity);
            }
        }
        else
        {
            delta.AddCombatLosses(1.0);
            // 战败脱战复苏：生命置为 1.0 HP，保留已回复灵力
            delta.SetHitpoints(1.0);
        }

        delta.AffectedSections |= DirtySections.Combat | DirtySections.Inventory | DirtySections.Stats | DirtySections.Queue;
        return delta;
    }
}
```

---

### 4. 采集探索执行器（`ExecutorGathering`）
- **职责**：结算草药/矿物采集产物与寻灵香加成

```csharp
public static class ExecutorGathering
{
    public static ActionDelta Execute(ActionExecutionContext ctx)
    {
        var delta = new ActionDelta();
        var harvestItemId = ctx.CurrentEntry.TargetItemId;
        var yieldAmount = 3.0; // 基础产出 3 株

        delta.ProduceItem(harvestItemId, yieldAmount);
        delta.AffectedSections |= DirtySections.Inventory | DirtySections.Stats | DirtySections.Queue;
        return delta;
    }
}
```

---

## 四、 离线快进补算边界（Fast-Forward，不可复制示例）

> 下方的 `dueTime + RunSystem` 示例仅用于指出旧形态，不能作为实现。正式快进必须调用 `SimulationEngine.AdvanceTo(state, target)`，使用 `LastSimulatedAt`、普通进度和战斗阶段游标跨越多个边界，并在达到转换预算时可恢复地继续。

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
            YuoWorld.RunSystem<IActionTransitionDue>(playerEntity);
        }
    }
}
```

---

## 五、 YuoCore SystemTag 与事件体系设计规约

任务队列只保留一个到期入口。以下表格中的 `IActionComplete` 仅用于标识旧代码形态，新增代码不得继续实现它；应使用深化方案中的 `IActionTransitionDue` 并由 `ActionTransitionSystem` 统一协调。

| 业务场景 | 推荐 SystemTag 接口 | 触发方式 |
| :--- | :--- | :--- |
| **行动阶段到期** | `IActionTransitionDue` | 调度器根据状态推导的截止时间唤醒，由 `ActionTransitionSystem` 统一推进 |
| **行动启动** | `IActionStart` | 玩家提交新动作时调用 `YuoWorld.RunSystem<IActionStart>(entity)` |
| **灵气蓄满可突破** | `IBreakthroughReady` | 修炼满灵气时触发，通知前端高亮突破按钮 |
| **突破境界成功** | `IBreakthroughSuccess` | 突破成功后触发，刷新六维根基属性与解锁新地图/功能 |
| **战斗波次完成** | `ICombatEncounterComplete` | 遭遇击杀或战败时触发 |
| **天材地宝发现** | `IRareItemFound` | 采集触发稀有掉落时广播或记录生涯成就 |

---

## 六、 核心开发红线（开发禁忌）

1. **严禁创建第二套队列推进权威**：
   - `SimulationEngine.AdvanceTo` 定义时间和结算语义；`ActionTransitionSystem` 只能作为 Fiber/ECS 外观调用它或等价的纯过渡结果。
2. **严禁在组件中写业务逻辑**：
   - `ActionQueueComponent` 只存队列运行态；产物、次数、阶段和循环由唯一过渡系统原子应用。
3. **严禁每帧 Update 轮询**：
   - 挂机业务依靠状态推导的截止时间触发 `YuoWorld.RunSystem<IActionTransitionDue>`；`DueAt` 只是调度投影，不能成为快进权威。
4. **数值全域统一使用 `double`**：
   - 收益率、数量、时长计算均保持 `double` 精度。
