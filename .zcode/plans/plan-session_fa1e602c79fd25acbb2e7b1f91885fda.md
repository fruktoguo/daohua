# Buff 效果通道架构重构计划

## 目标与不变量

把「Buff 效果 = 平铺 record 字段」重构为「Buff 效果 = 效果通道（key/value）+ 单点注册表」模型，四处副本（战内 `CombatBuffState` / 跨波 `CombatBuffCarry` / 组队定格 `ActionSustainedBuff` / 战外 `ActiveBuff`）统一为同一效果载荷；引入单位级聚合缓存与折叠面板缓存。用户已裁定：**全量统一四处副本 + 聚合缓存**。

硬性不变量（全程保持）：
- 战斗结算数值**逐位一致**（聚合累加顺序与现有 getter 的列表序完全同序；随机流、边界调度、快照往返不漂移均不动）
- **配表 JSON 零改动**（通道 ConfigKey 与现有键同名：`hpRegen`、`hpRegenPerSecond`、`resistancePoints` 等）
- 触发器管线（OnHit 反应效果 / `CombatEffectProfile`）本次不动——它是健康的固定事件双路径设计
- 战内 Buff 本期**不消费** `StatRatios`（维持现状玩法边界，仅统一载荷形状）

## 一、Domain 核心（效果的单一真源）

**新文件 `src/Domain/Combat/BuffEffects.cs`**：
1. `enum BuffEffectChannel : byte`——14 个存量通道：`HpRegen/MpRegen/HpRegenPostRatio/MpRegenPostRatio`（5s 网格）、`HpRegenPerSecond/ThreatPerSecondMaxHpPoints`（1s 网格）、`AttackSpeedPoints/AttackPower/Accuracy/Critical`（出手与面板）、`DamageBonusPoints/DamageAbsorptionPoints`（伤害乘区）、`ResistancePoints`（双抗乘区，Instance 型不乘层数）、`ThreatPoints`（威胁乘区）。
2. `record BuffEffect(BuffEffectChannel Channel, double Value)`——效果载荷。
3. `static class BuffEffectRegistry`——单点注册表，每通道声明：`ConfigKey`（配表 JSON 键）、`StackMode`（PerStack 值×层数 / Instance 值即总值，唯一 Instance 是 ResistancePoints）、`GrowsWithLevel`（授予时是否乘等级成长，唯一例外 ResistancePoints）、`OutOfCombatPanelKind`（战外面板折算目标：5s 回复 flat / PostRatio / 攻速点 / 免伤点 / None）。**新增效果通道 = 枚举值 + 注册表一行的成本**。
4. `BuffEffectParser.Parse(JsonElement)`——遍历 buff JSON 属性，按 ConfigKey 命中注册表即产出 BuffEffect（应用成长规则）；授予侧从此零散读键。

**`CombatBuffState` 新形状**（`CombatModels.cs`）：`(BuffId, ExpiresAtSeconds, Stacks, Name, ResetOnWave, MissingHitpointsPerStack, Effects: IReadOnlyList<BuffEffect>?, StatRatios: IReadOnlyDictionary<string,double>?)`——删 15 个平铺字段；`MissingHitpointsPerStack` 保留强类型（它是层数派生元数据，非数值通道）。`IsValid` 改为校验 Effects 通道合法、值有限、无重复通道。

**副本统一**：
- `CombatBuffCarry`（`CombatSkillState.cs`）→ `(BuffId, RemainingSeconds, Stacks, Name, MissingHitpointsPerStack, Effects, StatRatios)`
- `ActionSustainedBuff`（`ActionParticipation.cs`）→ `(BuffId, RemainingSeconds, Stacks, Name, TeamWide, Effects, StatRatios)`
- `ActiveBuff`（`PlayerAggregate.cs`）→ `(BuffId, ExpiresAtUnixSeconds, Stacks, Name, SourceItemId, Effects, StatRatios)`（原 6 平铺字段删除，StatRatios 保留）
- `ApplyTo/FromCombatant`、`EcsWorldShard` 战外→战内桥接、组队 `SustainedBuffs` 注入、消耗品三路径构造——全部逐字段复制退化为载荷引用直传（`with` 换到期时刻一行）。

**`CombatantState` 聚合缓存**（`CombatModels.cs`）：
- `double[] _buffTotals`（长度 = 通道数）+ `_hasRegen5s/_hasTick1s` bool + `CombatantStats? _effectiveStatsCache` 脏标记缓存。
- `RebuildBuffTotals()`：一次遍历 Buffs 按 StackMode 累加（列表序，与现有 getter 逐位同序）。
- 失效点（全部在既有函数内加一行重算）：`GrantBuff` 授予/替换、`CombatBuffTickSystem` 到期剔除、`SyncDerivedStacks` 层数重算、`PlayerCombatBuffState.ApplyTo` 跨波注入、`ApplySnapshot` 快照恢复、`ApplyBattleStartTraits` 开战授予、战外 Buff 注入点、`RefreshStats` 换面板。
- 9 个逐通道 getter（`BuffAttackPower` 等）删除，结算点改读 `unit.BuffTotal(channel)`；`EffectiveStats` 缓存化（`Buffs.Count == 0` 保持直接返回 `Stats` 的零成本快路径）；静态 `SumBuffThreatPoints/ComputeEffectiveThreat` 保留遍历版供协议投影跨段复用。

**`PlayerAttributeResolver` 战外折算**（315-326 行）：逐字段折算改为通道循环，按注册表 `OutOfCombatPanelKind` 路由（新增通道战外自动折算）。

**`SkillTriggerEvaluator.ValidateAbilityBuff`**：数值键白名单从注册表派生 ∪ 生命周期键（`durationSeconds/maxStacks/stackPolicy/resetOnWave/missingHitpointsPerStack/spellAttackStackPercent/resistancePerStack/resistanceStackRatio/buffId/name`）。

**`CombatTraitRules`** 隐身 buff 构造改通道载荷（`Effects: [(ThreatPoints, -100)]`）。

## 二、Ecs 接线

- `CombatActSystem.GrantBuff`：24 键逐个 `AbilityNumber` 读取 → `BuffEffectParser` + 生命周期键直读；`InitialStacks` 不动。
- 结算点改读聚合：`ResolveHit`（增伤/免伤/攻力经缓存面板与 totals）、`DecideNextAction`（攻速）、`CombatBuffTickSystem`（5s/1s 网格读数组）、`NextBuffBoundary`（regenPending/threatTickPending 读 `_hasRegen5s/_hasTick1s`）、`EffectiveThreat`。
- 消耗品三路径（`ActionExecutionRules.Resources.cs` 即时/durable + `CombatActionRules.cs` 单人持续）与组队会话注入（`ActionParticipationSessionEngine.cs`）：改走统一载荷构造；durable 全等比较（713-721 行）改为 Effects 载荷比较。
- 行动产出 `ActionBuffGrant`（`GameDataCatalog.cs`）：modifiers 键经注册表解析进 Effects。

## 三、协议与前端

**协议**（`ProtocolSnapshotMapper.cs` 三处：战内 `ToCombatBuffsDto` 两重载 + 战外 buffs section）统一为：
```
{ buffId, name, stacks, expiresAtSeconds, effects: [{channel: "hpRegen", value: 1.0}], statRatios: {...} }
```
通道名用稳定字符串（与配表键同名）。协议信封无变化，前端同步解析逻辑（符合协议强对齐红线：同批提交 + 补往返断言）。

**前端**：
- `game/types.ts`：`BuffEffectChannel` 字符串字面量联合 + `BUFF_CHANNEL_REGISTRY`（channel → {i18n 词条键, 曲线 linear/nonLinear, tone, glyph, perStack 折算}）单一注册表，吞并 `STAT_RATIO_LABEL_KEYS`。
- `network/snapshot.ts`：`mapCombatBuffs`/`mapBuffs` 改解析 effects 数组（**顺带修复现状 5 个字段漏解析的既有缺陷**）。
- `ItemDisplay.tsx`：`extractBuffBonusItems` 约 20 分支 if-else → 通道循环查注册表；`describeBuffBonusItems` 同步。
- `CombatArena.tsx`：`ArenaBuffSquare` tone/glyph 判定、`combatBuffToBuff` 透传 effects。
- `EffectTooltip.tsx`/`game/format.ts`：等级缩放对通道数组逐项乘。
- i18n：通道词条按 `market.effect.badge<Channel>` 约定登记；清理失效旧键（`check-i18n-keys` 门禁校验）。
- 模拟器 `combatSimulatorCore.ts`：`SimBuff` 通道化（当前技能 Buff 面为休眠代码，风险低），对齐服务端通道全集。

## 四、存档版本与 DataTool

- `SectionCodec.CurrentSchemaVersion` 39→40 + 版本史注释；`PostgresSchema.Version` 44→45；新增 `migrations/0045_v44_to_v45.sql`（schema_version 标签推进）。
- 效果载荷形状变化使旧分段不可无损解码（JSON 在 Brotli 载荷内，SQL 无法重写）→ 走既定 `reset-outdated-players.sh` 链：迁移不可用 → 交互确认清零玩家游戏域数据（保留账号/角色名）。组队会话表瞬态 JSON 同理，内测口径可接受并在文档说明。
- `DataTool` `AbilityBuffKeys/ConsumableBuffKeys` 白名单手工同步（DataTool 独立无项目引用，注册表注释注明同步义务）。

## 五、测试（改写 + 最小新增）

改写字段断言为通道断言（语义断言保留）：`CombatSuiGangBuffTests`、`CombatGanluBuffTests`（1s 网格按层回血语义锚点）、`CombatConsumableTests`（5s tick 流转）、`CombatPartyExpeditionTests`、`CombatPartyWipeTests`（DoT 构造）、`ActionParticipationSessionTests`、`EcsStateRoundTripTests`（Buffs 分段种子）。

新增三个（各防一种独立回归）：
1. 通道聚合等价性——四副本共享载荷下，聚合缓存结果 == 逐条遍历基准（防缓存实现错误）。
2. 聚合失效点场景——授予/到期剔除/层数重算/快照恢复/跨波注入后 totals 与面板正确（防 stale 缓存）。
3. `buff-stacks` 运行时求值含 `ExternalBuffStacks`/perCaster 前缀合并（补现状无直接单测的缺口）。

保留安全网：`CombatRestoreReproTests` 确定性双跑、`EcsAdapterBoundaryTests`、`StuckCombatReproTests`、`DuelSimulationTests`、前端契约脚本文案断言。

## 六、文档先行（阶段 0，与代码同批提交）

- `docs/design/总纲/07-战斗数值与内视规约.md`：§7.9.5/7.9.5.1/7.9.5.2 重写为通道模型 + 存量字段→通道映射表 + Buff 协议字段对照表（补 §7.11.2 缺口）；§7.7.1/§7.7.5/§7.8.1 引用表述同步。
- `18-服务端ECS化执行架构与触发器规约.md`：效果通道统一口径；`05/06` 篇交叉引用同步（配表键名不变需明确写出）。
- `docs/tasks.md` 登记 + `docs/tasks/TASK-20261024-01-Buff效果通道架构重构.md` 详情文档（含时间节点与进度日志）。

## 七、执行顺序与验证

主 Agent 串行执行（链路高度耦合，不派多 Agent 写代码）：阶段 0 文档与任务登记 → 阶段 1 Domain 核心 → 阶段 2 Ecs 接线 → 阶段 3 协议+前端 → 阶段 4 存档版本+DataTool+测试 → 阶段 5 全量门禁。

每阶段验证：`dotnet build` + 对应套件 `dotnet test`；最终门禁：`scripts/verify.sh --full`（三套测试 + DataTool strict + i18n + 前端单测/构建 + Compose）+ `capacity-target.sh`（Buff 载荷进 action_queue 分段，effects 数组比平铺字段略增 JSON 体积，属容量相关变更必须跑）。功能代码过全部门禁后自动提交（中文 Conventional Commits，文档与代码同批）；push 需另行许可。

## 风险与缓解

- **浮点逐位漂移**：聚合累加保持列表序（与现有 getter 同序）；`CombatRestoreReproTests` 双跑 + 碎罡/甘露既有语义断言兜底。
- **缓存 stale**：失效点全部为既有函数内的封闭集合；新增失效点场景测试。
- **协议前后端漂移**：契约脚本不覆盖 buffs 字段（调研确认的敞口），本次同批提交并新增快照往返断言补上。
