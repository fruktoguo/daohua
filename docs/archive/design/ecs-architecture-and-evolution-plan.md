# 《道化太初》架构复查审计与演进反思录

> **文档版本**：v2.0.0 务实反思与真源定调版  
> **更新时间**：2026-08-24  
> **所属体系**：系统架构真源复查 / 摒弃过度设计与教条主义 / 架构现状与务实路线  
> **适用范围**：`src/Domain` 核心领域、`src/Server` 运行时宿主、`src/Protocol` 协议 DTO
>
> **文档状态（2026-09-16 标注）**：本文是**历史架构推演稿**。文中 `PlayerState`/`PlayerComponent`/`YuoEntity`/`PlayerShardHost`/`CombatResourceState` 等类型属于旧对象模型；**现行实现为 `PlayerAggregate` 聚合 + `PlayerRuntime`/`PlayerActor` + `SemaphoreSlim` 单写者模型**（`YuoCore` 基座引用保留待用，不构成运行时对象模型），现行架构规约以 [`../backend/domain-data-architecture.md`](../../backend/domain-data-architecture.md) 为准。文中类型名按下表对照阅读，结论性判断仍然有效。

### 现状对照表（历史类型名 → 现行实现）

| 文中概念 | 现行对应 | 状态 |
| --- | --- | --- |
| `PlayerState` 聚合根（7 大子切片） | `PlayerAggregate` + `PlayerSectionKind` 七分段（Progression/World/Combat/Buffs/Loadout/Settings/ActionQueue） | ✅ 已落地（分段边界重划，资产走 `AssetWallet`+economy） |
| `PlayerComponent` 挂载 `YuoEntity` | `PlayerRuntime` 内的 `PlayerActor` 持有 `PlayerAggregate`，`SemaphoreSlim` 串行 | ✅ 已落地（聚合单写者，非 ECS） |
| `PlayerShardHost` Fiber 调度 | `PlayerRuntime` Actor 生命周期 + `game.player_runtime_leases` 租约 | ✅ 已落地 |
| 纯函数快照推进（非 Tick 轮询） | `SimulationEngine.AdvanceTo` 离散推进 | ✅ 已落地 |
| `CombatResourceState` long+Remainder 血蓝 | `PlayerCombatState` 全 `double` + `RegenCarrySeconds` 回复余量 | ✅ 已落地（口径改为全 double） |
| `BuffState` 字段支撑丹药词缀 | `PlayerBuffState` + 持续消耗品机制 | ✅ 已落地 |
| `CanStartAction` 体征前置校验（Hitpoints>0 等） | 行动入队前置门槛 | ⏳ 规划中（本文§2 仍为准入设计意图） |

---

## 1. 架构全面复查与反思：为什么之前的多项提议是“教条主义幻觉”？

经过深入排查全库代码、运行链路与实测压测数据，**严谨确认：之前提出的“推倒重构四大项”绝大部分属于脱离业务现实的教条主义推演和过度设计（Premature Over-Engineering）**。

下面逐项进行客观、透明的真源剖析：

---

### 1.1 剖析一：关于“拆毁 `PlayerState` 全面换成 7 个散装 ECS 组件”

- **真实代码现状**：
  - `PlayerState`（`src/Domain/Models/Models.cs`）在结构上**原本就已经完全是粗粒度模块化的聚合体**：
    - `Inventory`（管理整个背包与物品字典）
    - `Buffs`（管理全局增减益列表）
    - `Queue`（管理行动序列与自动吃药槽位）
    - `Combat`（管理战备装配、功法槽与装备）
    - `CombatResources`（管理生命、灵力与冷却）
    - `World`（管理地图坐标与旅行路线）
    - `Stats`（管理修为、统计与资产）
  - 在 `src/Server/Runtime/GameHost.cs` 中，在线玩家实体 `YuoEntity` 挂载 `PlayerComponent` 持有该 `PlayerState`，所有读写严格在单个 `PlayerShardHost` Fiber 上串行推进。
- **为何之前提议是“幻觉/折腾”**：
  - 把 `PlayerState` 的各个子对象强行改成继承 `YuoComponent`、用 `entity.AddComponent<T>()` 挂载，除了能满足某种“纯粹 ECS”的形式主义强迫症之外，**在运行时性能、业务语义、代码可读性上没有任何实质收益**，反而会带来序列化重构、DTO 映射重构和大量代码改动风险。
- **最终定论**：**保持现状不变**。现有的 `PlayerState` 聚合根在单个 `PlayerComponent` 下运行良好，天然满足了“角色作为单一常驻实体”的核心设计哲学。

---

### 1.2 剖析二：关于“战斗必须改成 Tick 增量步进状态机”

- **真实代码现状**：
  - 服务端调度（`SimulationHostedService.cs`）根本不是每 100ms 盲目轮询，而是由 `GetNextCombatTransitionAt` 计算离散事件截止时间（通常 1.5s ~ 2.5s 一次），Fiber 精确异步休眠。
  - 纯 C# 内存中跑完一次 10 秒纯函数战斗仅需 **几微秒**。5000 账号 + 500 在线 + 2000 挂机 1 小时测试总耗时仅 **378 毫秒**。
- **为何之前提议是“幻觉/折腾”**：
  - 脱离业务真实调度模型，用传统 60FPS 动作游戏的重放开销（$O(N^2)$）进行脱靶推演。强行引入内存增量状态机会导致离线与在线出现双轨逻辑分歧、破坏秒级自愈与零存储负担优势。
- **最终定论**：**坚决保留纯函数快照推进机制**。

---

### 1.3 剖析三：关于“消灭 `long` + `Remainder` 浮点累加补偿”

- **真实代码现状**：
  - `CombatResourceState` 中使用 `long Hitpoints/Manapoints` 记录当前血蓝，使用 `double HitpointRemainder/ManapointRemainder` 记录小数累加进位。
- **为何之前提议是“小题大做”**：
  - 在 RPG / MMO 游戏中，面板气血通常需要显示为整数（如 100/100 HP，掉血 -15），自然回复数值允许小数；基础值由基础属性表提供，不由上限按固定比例生成。具体结算周期仍待讨论，以 [战斗规约 §7.9.2](../../design/总纲/07-战斗数值与内视规约.md#792-自然回复属性由基础属性表配置) 为准。`Remainder` 累加器是游戏行业经过验证的成熟方案，既保证了界面整数显示的直观性，又保证了浮点累加零精度丢失。
- **最终定论**：**保持现状不动**，现有的 `Remainder` 进位机制稳定可靠，无需为形式主义做无谓改动。

---

### 1.4 剖析四：关于“提前构建复杂的通用 `StatModifier` 三态词缀流水线”

- **真实代码现状**：
  - 当前游戏数据（`config/game-data/items/pills.json`）共有引气丹、止血散、顺气丸、通脉丹等丹药，覆盖了修为加成、炼丹加成、采集加成、战斗生命回复与灵力回复。
  - `BuffState` 中的字段完全能够精准支撑当前所有丹药与增益效果。
- **为何之前提议是“过早抽象”**：
  - 在当前配置体系完全能够满足业务需求的情况下，提前设计一套复杂的通用动态修饰器流水线（`StatPipeline`）属于典型的**过早抽象（Premature Abstraction）**。
- **最终定论**：**按需扩展**。未来策划新增全新维度词缀（如特定免疫、地形减伤）时，再按需扩展字段或增加对应处理器即可。

---

## 2. 真实需关注的轻量优化点（收敛范围）

在剔除所有虚构的“架构危机”后，全系统实际需要关注的仅有以下一项轻量业务逻辑：

1. **`CanStartAction` 的体征状态前置友好性**：
   - 现状：当前 `CanStartAction` 仅校验了地图节点和原料输入，0 HP 玩家进入战斗会在第 0 回合判定失败并脱战复活至 1 HP；
   - 优化：当未来业务明确要求“重伤禁止探索”时，在 `CanStartAction` 中加入 `Hitpoints > 0` 检查，使动作直接在队列中显示为 `Blocked`，UI 表现更清晰。

---

## 3. 架构准则与总结

1. **拒绝无痛呻吟的架构推倒**：任何重构提议必须能指出当前生产或运行时的具体崩溃证据、性能瓶颈数据或无法实现的业务阻碍，禁止基于泛化理论盲目推演。
2. **坚持“够用且健壮”的极简工程美学**：当前 `PlayerState` + 纯函数 `SimulationEngine` + 单 Fiber 串行所有权在 2c4g 下展现了极高的运行效率和代码纯粹度，应当作为基准坚定维护。
