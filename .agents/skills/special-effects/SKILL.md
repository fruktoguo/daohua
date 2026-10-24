---
name: special-effects
description: "负责道化太初特殊玩法效果的新增、修改、修复和评审：装备被动、技能附加效果、Buff/Debuff、怪物特性、命中/施法/击杀/死亡/到期反应，以及持续伤害、控制、护盾、汲取和反伤等机制。涉及 specialMechanics、CombatEffectProfile、CombatTriggerHandler、效果叠层、冷却或生命周期时使用。纯视觉粒子/动画/美术不适用；仅做通用数值配平使用 data-designer，效果机制伴随数值变化时两者配合。"
---

# 特殊效果实现

将特殊效果从规则定义落实到配表、共享结算、运行时接入和必要验证。以仓库当前源码和总纲为准，先读取根 `AGENTS.md`；本 Skill 不扩大用户授权范围，也不自动实现未提出的效果。

## 先确定效果语义

阅读相关配表、效果来源与真实调用路径，在任务详情和设计文档中记录本次需要明确的规则：

- 来源与资格：装备、技能、Buff 或怪物；生效对象是谁，何时激活或失效。
- 时机与作用侧：命中前/落账后、攻击方/受击方、施法开始/完成、击杀/死亡/到期；未命中、零伤害、目标已死是否生效。
- 结算口径：原始伤害、减免后伤害或实际扣血量；上限、溢出、资源不足、概率与冷却如何计算。读取现行规则，不能默认它们等价。
- 生命周期：叠加还是覆盖、层数和持续时间如何刷新、驱散/换装/离场如何处理；只有涉及这些机制才新增对应状态。
- 连锁语义：衍生伤害是否再次触发、是否消费次数、与同刻其他效果及死亡裁决的先后关系。

优先从总纲、配表和现有同类效果推导规则。仍有会改变玩家结算结果的歧义时，提出聚焦的澄清并继续不依赖答案的工作；不要擅自创造机制或把设计草案当成已实现能力。

## 选择真实执行入口

| 机制 | 实现入口 | 决策依据 |
| --- | --- | --- |
| 常驻属性、增减伤乘区、速度、治疗增幅 | 现有属性解析和公式/技能结算点 | 无离散反应事件时不创建触发器 |
| Buff/Debuff 携带的数值效果（回复、攻速、攻力、命中、暴击、增减伤、双抗与威胁点数等） | `BuffEffectRegistry` 通道注册表 + `BuffEffectParser` 配表解析 + 结算点读 `BuffTotal`/`EffectiveStats` 聚合（[总纲 07 §7.9.5.3](../../../docs/design/总纲/07-战斗数值与内视规约.md)） | 数值通道模型：新增通道 = 枚举**末尾追加** + 注册表一行 + 结算点接线；禁止回到平铺字段或旁路数值字典 |
| 命中落账后的汲取、反伤及同类反应 | `CombatTriggerHandler` + `IInlineOnHitTrigger`，接入当前实体消费路径 | 当前实际存在的轻量事件只有 `OnHit` |
| 施法、击杀、死亡、到期等新反应 | 按需求新增固定事件接口与专用领域参数，并补生产/消费点 | 总纲里的示例名称不是现成接口 |
| 持续伤害/治疗、周期效果、时限 Buff | 现有战斗自然边界、行动资源边界与 Buff 到期裁决 | 使用模拟时钟和明确的下一边界，不能用墙钟或异步定时器 |
| 护盾、减伤上限、免死、打断等前置裁决 | 对应伤害落账前、施法状态转换或终局裁决点 | 已落账的 `OnHit` 不能代替前置裁决 |

不要引入通用事件总线、运行时回调订阅或可变参数协议。接口按事件固定方法和强类型参数，只扩展本次需要的事件。只在需要新自然边界时调整已有 `Next*At` 调度，核对批量补算资格与 `CanBulkAdvance`；不能为了效果回到每 tick 全量扫描。

## 接入轻量处理流程

1. 在 `src/Ecs` 新增具体处理器，继承 `CombatTriggerHandler`，实现对应事件接口；当前命中签名是 `void OnHit(PendingCombatTrigger trigger)`。
2. 使用 `[CombatTriggerOrder(顺序号)]` 并提供公共无参构造函数。注册表只收集 `DaoHua.Ecs` 程序集的非抽象、非开放泛型子类；只实现接口而不继承基类不会被收集。
3. 处理器实例全局缓存并跨战斗复用，必须无战斗状态。冷却、次数、层数、蓄能与到期时间放在所属单位、遭遇或玩家状态中；不得把它们写入处理器实例/静态字段，也不得缓存玩家或战斗上下文引用。相位/计数型状态可由 Buff 自身的 `Stacks` 与效果载荷承载：事件处理器在结算后经 `SetBuff` 换档，聚合缓存随变更自动失效，无需独立计数字段。
4. 当前每次攻击结算按攻击方、受击方顺序各派发一条 pending trigger，未命中也会派发。处理器先用 `ReferenceEquals(trigger.Unit, trigger.Context.Attacker/Target)` 明确所属侧，再按 `Hit`、伤害、存活资格及效果参数执行，避免未命中误触发或结算两次。
5. 排序为顺序号升序，再按处理器全名 `StringComparer.Ordinal` 排序。会相互影响的效果分配明确的不同顺序号；实体侧 `SystemOrder` 必须表达同一先后关系，不能依赖 ECS 同序号时的排序结果。
6. 既有事件下新增轻量处理器不修改派发器的效果列表。当前注册表发现测试断言了四个具体处理器的数量与有序类型清单；新增效果时同步更新该发现断言，保留扫描范围与顺序门禁。
7. 新事件同时补齐：领域契约、固定方法签名、`CombatTriggerKind`、强类型缓存数组、实际生产点、同步消费点及所需的实体派发映射。不要只有接口而没有入口，也不要复用不适合的命中上下文。

反射只用于启动收集；热路径继续调用强类型接口数组，不使用 `MethodInfo.Invoke`、动态查找方法或 `Dictionary<Type>` 派发。轻量接口不继承 `ISystemTag`，处理器不持有 `YuoEntity`、`YuoWorld` 或宿主服务。未来实际启用 AOT/裁剪时，再处理扫描类型元数据保留或源生成注册；当前不预建第二套注册机制。

## 共享结算与数据闭环

当前实体路径仍通过 `EcsCombatTriggerSink` 的 ECS Tag System 执行，内联路径通过 `InlineCombatTriggerSink` 执行；两者互斥消费。新增轻量处理器不会自动进入实体路径，必须追踪本次效果的全部真实使用入口。

- 规则本体只写一次，复用现有 `CombatTriggerMath` 或职责相符的共享实现。当前两条路径都使用的效果同步补齐轻量处理器、薄 ECS System、能力标记与 `CombatTriggerSurface.Sync`；各入口读取同一效果参数，不能复制结算逻辑。用户明确要求统一派发架构时先改设计并收拢入口，不为旧调用方式另留兼容分支。
- 装备 `specialMechanics`、怪物配表与 Buff 参数沿既有属性解析进入 `CombatEffectProfile` 或对应领域状态。只有同族反应参数才扩展该参数包，不能把所有效果强塞进它。同步配表读取、边界校验、属性缓存失效和实体能力标记刷新，防止参数已变而能力标记仍过期。
- 战内 Buff 视图为 `IReadOnlyList`，唯一合法变更通道是 `SetBuff`/`AddBuff`/`RemoveBuffsWhere`/`ReplaceBuffs` 受控变更器，变更即失效单位聚合缓存与 `EffectiveStats` 折叠缓存；四处 Buff 载体（战内/跨波/组队定格/战外）共享同一效果通道载荷，跨边界只传 `Effects` 引用，不得复制平铺字段或另建数值副本。
- 汲取、反伤等现有事件通过 `CombatHitContext.Emit` 记录衍生事件，由 sink 的 `Settle` 统一回填与并入；不要在效果中再 drain 同一队列或重新结算主伤害。当前反伤直接落账，不重新入队触发反伤；连锁效果必须按需求定义终止规则。
- 当前 `OnHit` 在每次攻击伤害结算后立即 drain，包括未命中条目，具体效果自行检查生效资格；之后才裁决死亡/胜负。同刻多单位出手时不能推迟到边界末；新事件同样要规定精确消费点和事件顺序。
- 概率效果使用 `CombatHitContext.NextRoll()` 或当前事件的确定性随机通道，记录消费顺序；不使用 `Random`、`DateTime.Now`、`Task.Delay` 或异步回调推进结算。
- 有状态效果核对快照恢复、文档往返和分段脏标记。持久化规则状态写入对应分段；EntityId、pending 队列、sink、注册表实例等运行时引用不序列化。确实改变存档格式时同步 `SectionCodec`/`PostgresSchema` 版本与对应门禁，遵守仓库本地迁移和清零规则。
- 涉及强度、概率、时间或收益的配表数值时同时使用 [data-designer](../data-designer/SKILL.md)，推导预算与权重。现行数值统一为 `double`，百分比采用点数；公式曲线由效果语义决定，不复制旧示例中的历史字段或单位。
- 效果影响客户端可见状态、日志或物品描述时同步实际协议与呈现；需要新增界面时复用现有公共组件和词条。只展示玩家需要的信息，不将内部机制说明作为 UI 文案。

## 最小充分验证

先明确每个测试防止的独立回归，沿现有测试扩展：

1. 用启用真实效果的场景验证关键语义边界，例如作用侧、一次结算、资源上限、死亡先后或连锁终止；仅测试未启用效果不能证明接入成功。
2. 效果跨实体/内联路径时，比较终态资源、事件顺序及必要状态；有随机或持续状态时再覆盖窗口拆分推进与快照恢复，确认在线、补算和恢复一致。
3. 配表字段新增或解析改变时验证数据到实际效果的链路；真正涉及持久化/协议时才补对应往返门禁。不要为每个 getter、转发壳或同一规则的排列组合重复加测。

常用入口（按修改范围选择，不默认做全量长压测）：

```bash
dotnet test tests/Ecs.Tests/Ecs.Tests.csproj -c Release
dotnet test tests/Domain.Tests/Domain.Tests.csproj -c Release
dotnet build src/Ecs/Ecs.csproj -c Release
```

涉及宿主装配、协议、数据校验或前端时追加 `AGENTS.md` 要求的对应门禁。只汇报实际运行结果、效果行为和剩余限制，不能用编译成功代替语义验证。

## 按需读取源码

从本 Skill 目录按相对路径打开，先读本次涉及的入口，避免全仓无目的扫描。

| 需要确认的问题 | 权威入口 |
| --- | --- |
| 触发器设计与工作流 | [总纲 18](../../../docs/design/总纲/18-服务端ECS化执行架构与触发器规约.md)、[ECS 开发规约](../../../docs/backend/ecs-development-guide.md) |
| 继承收集、固定接口、顺序与现有处理器 | [InlineCombatTriggerPipeline.cs](../../../src/Ecs/Combat/InlineCombatTriggerPipeline.cs) |
| 双路径派发、能力标记同步与事件收尾 | [CombatTriggerDispatch.cs](../../../src/Ecs/Combat/CombatTriggerDispatch.cs)、[OnHitTriggerSystems.cs](../../../src/Ecs/Combat/OnHitTriggerSystems.cs) |
| 参数契约、事件上下文与随机通道 | [CombatHooks.cs](../../../src/Domain/Combat/CombatHooks.cs) |
| 共享效果本体与命中生产点 | [CombatTriggerMath.cs](../../../src/Ecs/Combat/CombatTriggerMath.cs)、[CombatActSystem.cs](../../../src/Ecs/Systems/CombatActSystem.cs) |
| 装备/怪物参数进入实际面板 | [PlayerAttributeResolver.cs](../../../src/Domain/Combat/PlayerAttributeResolver.cs)、[GameDataCatalog.cs](../../../src/Domain/GameData/GameDataCatalog.cs) |
| Buff 数值通道注册表、配表解析与聚合缓存 | [BuffEffects.cs](../../../src/Domain/Combat/BuffEffects.cs)、[总纲 07 §7.9.5.3](../../../docs/design/总纲/07-战斗数值与内视规约.md)、`CombatModels.cs` 聚合缓存段（`BuffTotal`/`EffectiveStats`） |
| 玩家 Buff 到期裁决 | [PlayerBuffExpirySystem.cs](../../../src/Ecs/Systems/PlayerBuffExpirySystem.cs)、[PlayerBuffExpiryDispatch.cs](../../../src/Ecs/PlayerBuffExpiryDispatch.cs) |
| 注册发现、结算时序、双路径与恢复回归 | [CombatTriggerPipelineTests.cs](../../../tests/Ecs.Tests/CombatTriggerPipelineTests.cs)、[CombatPhaseStateTests.cs](../../../tests/Ecs.Tests/CombatPhaseStateTests.cs)、[EcsCombatParityTests.cs](../../../tests/Ecs.Tests/EcsCombatParityTests.cs)、[CombatRestoreReproTests.cs](../../../tests/Ecs.Tests/CombatRestoreReproTests.cs)、[CombatHookTests.cs](../../../tests/Domain.Tests/CombatHookTests.cs) |
