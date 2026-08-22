# 道化太初 · 服务端领域数据架构与 YuoCore ECS 规约

本文档定义《道化太初》服务端的领域模型设计、数据类型规范以及与 `YuoCore` 引擎深度结合的架构标准。旨在解决传统贫血模型、巨型单体 State、手写深拷贝地狱以及“伪 ECS”退化问题，建立高性能、低 GC、确定性、高扩展性的商业级数据层。

---

## 一、 核心设计哲学与 YuoCore 架构铁律

根据 YuoCore 吸收 ET 精华后的分布式单写者架构设计，服务端严格遵守以下五大铁律：

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        YuoCore 核心设计铁律                            │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 实体非线程安全：Entity/Component 永远只被当前 Fiber 的 YuoWorld 独占访问 │
│ 2. 并行粒度是 Fiber：多个 Fiber（分片）并行 Tick，Fiber 内部单线程零锁 ECS │
│ 3. 跨 Fiber 边界走 Actor：外部请求/跨服通信投递 Actor 消息，不直接碰 Entity  │
│ 4. 逻辑由 YuoSystem<T1..Tn> 驱动：声明式自动注入所需组件，消除冗余 Query   │
│ 5. 状态同步与持久化解耦：通过 StateSync 提取脏组件，通过 Persistence 异步落库 │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 二、 四层数据架构体系

严禁在单一的 `Models` 目录中堆砌所有类型，必须按**生命周期与所有权**严格划分为四层：

```text
┌────────────────────────────────────────────────────────────────────────┐
│ 1. 策划静态配置 (Definitions)   │ 只读不可变、启动加载、FrozenDictionary   │
├────────────────────────────────────────────────────────────────────────┤
│ 2. 运行时领域组件 (YuoComponents) │ 单 Fiber 独占所有权、高频变动、脏标记驱动 │
├────────────────────────────────────────────────────────────────────────┤
│ 3. 纯业务逻辑系统 (YuoSystems)   │ 多泛型注入、确定性单线程 Tick             │
├────────────────────────────────────────────────────────────────────────┤
│ 4. 持久化与快照 (Snapshots/DTO)   │ 异步落盘、冷计算传输、不可变流式投影     │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. 第一层：策划静态配置（`src/Domain/Definitions/` & `src/Domain/Configs/`）
- **职责**：描述技能、物品、词条、掉落、地图拓扑等静态数据。
- **规范**：
  - 类型定义使用 `sealed record` 或 `sealed class` + `{ get; init; }`。
  - 集合一律使用 `FrozenDictionary` / `ImmutableArray`，启动解析后全生命周期只读不可变。
  - 杜绝在配置类中混入运行时可变字段。

### 2. 第二层：运行时领域组件（`src/Domain/Components/`）
- **职责**：挂载在 `YuoEntity` 上的细粒度领域数据切片。
- **规范**：
  - 继承自 `YuoCore.Ecs.YuoComponent`。
  - 拆分巨型单体 `PlayerState`，按子域独立拆分：
    - `InventoryComponent`：背包物品字典与存取逻辑。
    - `ActionQueueComponent`：挂机动作队列与推进状态。
    - `CultivationComponent`：灵气、境界与突破状态。
    - `CombatResourcesComponent`：当前生命、灵力与技能冷却。
    - `WorldPositionComponent`：大地图节点与寻路边。
    - `PlayerStatsComponent`：生涯统计数据。
    - `SyncDirtyComponent`：集中追踪组件脏段掩码。
  - **数值类型统一**：遵循 `AGENTS.md` 规则 7，所有数值、属性、时长一律统一使用 `double` 类型，杜绝 `long`/`int`/`double` 混用导致的小数余量补丁。

### 3. 第三层：业务推进与事件系统（`src/Domain/Systems/`）
- **职责**：驱动游戏世界状态的离散事件跃迁（DES）与瞬时计算。
- **规范**：
  - **自定义 SystemTag 事件机制**：定义业务事件标签（如 `IActionComplete : ISystemTag`、`IActionStart`、`IBreakthroughSuccess`）。
  - **到期精准派发**：调度器在 10 秒到期时直接调用 `YuoWorld.RunSystem<IActionComplete>(playerEntity)`，由 YuoWorld 自动匹配并触发所有实现了该 Tag 的多泛型系统（如 `GatheringActionCompleteSystem : YuoSystem<ActionQueueComponent, InventoryComponent>, IActionComplete`）。
  - 单线程无锁执行，由所属 Fiber 驱动。

### 4. 第四层：持久化快照（`src/Domain/Snapshots/` & `src/Server/Persistence/`）
- **职责**：实现数据库异步安全落盘与跨节点冷计算传输。
- **规范**：
  - **彻底废除手写深拷贝 `Clone()`**：禁止在状态类上手写繁琐易漏的深拷贝代码。
  - **投影式快照**：在 Fiber 安全点通过 `SnapshotMapper` 将 `YuoEntity` 组件数据瞬时投影为不可变 `PlayerSnapshot` 记录，或直接流式序列化丢入 `YuoCore.Persistence` 异步队列。

---

## 三、 强类型 ID 规范（Strongly Typed IDs）

杜绝基本类型偏执（Primitive Obsession），全系统使用 `readonly record struct` 包装业务标识符，提供编译期类型安全与零运行时分配：

```csharp
namespace Domain.Common;

public readonly record struct PlayerId(Guid Value)
{
    public static PlayerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct ItemId(string Value)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
    public static implicit operator ItemId(string value) => new(value);
}

public readonly record struct AbilityId(string Value);
public readonly record struct EncounterId(string Value);
```

---

## 四、 撮合与纯函数计算规范（`src/Domain/Market/` & `src/Domain/Combat/`）

对于市场撮合、伤害结算等瞬时计算逻辑：
- 严格遵循 **Functional Core, Imperative Shell** 模式。
- 输入与输出模型一律采用不可变 `record` / `readonly record struct`。
- 状态演进通过 C# 原生 `with` 表达式完成，保证 100% 确定性与零副作用：

```csharp
public sealed record MarketOrder(
    string OrderId,
    PlayerId OwnerId,
    MarketInstrument Instrument,
    MarketSide Side,
    double Price,
    double Quantity,
    double FilledQuantity,
    MarketOrderStatus Status
)
{
    public MarketOrder WithFill(double filledAmount) => this with
    {
        FilledQuantity = FilledQuantity + filledAmount,
        Status = (FilledQuantity + filledAmount >= Quantity) 
            ? MarketOrderStatus.Filled 
            : MarketOrderStatus.PartiallyFilled
    };
}
```

---

## 五、 事务模型与跨边界一致性规约

在分布式挂机与交易系统中，严禁采用外部多重分布式大锁（如同时锁玩家 Fiber + 锁市场 Partition），必须严格采用 **YuoCore Actor 消息事务与 Saga 补偿模式**：

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        跨边界事务流转（以挂单为例）                    │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 玩家 Fiber (Local Tx)   ───> 校验并冻结物品 (FreezeItem)            │
│                                  │                                     │
│                                  ▼ (Actor Request)                     │
│ 2. 市场 Actor (Partition)   ───> 撮合挂单 (Place & Match)              │
│                                  │                                     │
│                                  ▼ (Actor Response: Ok / Fail)         │
│ 3. 玩家 Fiber (Callback)   ───> 成功: 扣除冻结 / 失败: 解冻补偿回滚     │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. 本地 Fiber 事务（In-Fiber Atomicity）
- 单个 Fiber 内部由单线程顺序执行，天然保证局部原子性。
- 组件属性变更采用**先校验、后应用**原则；复杂流转支持 Snapshot Token 回滚，杜绝脏半态。

### 2. 跨分片/跨服务事务（Saga / Actor Compensation）
- **挂单流程**：玩家所属 Fiber 仅负责自身资产的预扣/冻结，通过 `YuoActor` 向负责该品种的 `MarketPartitionActor` 投递撮合指令。
- **撤单与结算**：由市场 Actor 发送结算结果给对应玩家 Actor，异步入账，杜绝跨进程分布式锁。

### 3. 持久化原子性（Durable Outbox & Version CAS）
- 玩家快照与市场账本持久化利用 PostgreSQL Revision CAS 与 Durable Outbox，保证崩溃恢复不丢单、不重放。

---

## 六、 推荐代码目录拓扑

```text
src/Domain/
├── Common/                  # 基础值对象与强类型 ID
│   ├── Identifiers.cs       # PlayerId, ItemId, AbilityId...
│   └── DirtySections.cs     # 状态脏标记掩码枚举
│
├── Configs/                 # 策划静态只读数据（启动即固定）
│   ├── AbilityDefinition.cs
│   ├── ItemDefinition.cs
│   ├── MonsterDefinition.cs
│   ├── MapDefinition.cs
│   └── GameDataCatalog.cs
│
├── Components/              # 运行时高频实体组件（YuoComponent，单 Fiber 独占）
│   ├── InventoryComponent.cs
│   ├── ActionQueueComponent.cs
│   ├── CultivationComponent.cs
│   ├── CombatResourcesComponent.cs
│   ├── WorldPositionComponent.cs
│   ├── PlayerStatsComponent.cs
│   └── SyncDirtyComponent.cs
│
├── Systems/                 # 核心业务推进逻辑（YuoSystem<T1, T2...>）
│   ├── ActionQueueSystem.cs
│   ├── CultivationSystem.cs
│   ├── CombatSystem.cs
│   └── TravelSystem.cs
│
├── Snapshots/               # 持久化与快照只读模型
│   ├── PlayerSnapshot.cs
│   └── SnapshotMapper.cs
│
├── Combat/                  # 战斗子域（触发器、伤害计算、回合上下文）
│   ├── CombatContext.cs
│   ├── CombatTriggers.cs
│   └── CombatEvents.cs
│
└── Market/                  # 交易子域（盘口、撮合引擎、订单账本）
    ├── MarketInstrument.cs
    ├── MarketOrder.cs
    └── MarketMatchResult.cs
```
