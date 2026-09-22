# 模型分叉审计 2026-09-23（TASK-20260923-02 收尾）

背景：TASK-20260923-02 修正了「掌心雷吟唱 2s 被误建模为法系普攻间隔」的模型分叉。本文档汇总三路并行只读审计（领域层 / 配置与文档 / 前端协议快照）发现的同类问题——**同一行为被分叉成多套不通用模型**。按严重程度排序，行号以 `f07045f` 前后工作区为准。

## 一、高危：会产生实际错误

### 1. 技能触发条件三套互不兼容的求值器（可致模拟崩溃 + 静默失效）

- 服务端准入：`src/Server/Clean/LoadoutCommand.cs:88-105`（`ReadTriggers`）接受 `stun-status`/`blind-status`/`silence-status`、`is-active`/`is-inactive` 等取值。
- 远征求值器：`src/Domain/Combat/CombatSkillEngine.cs:237-268`（`Matches`）对未知取值 `throw InvariantViolation` → 装配合法触发器即可让远征推进永久卡死。
- 斗法镜像求值器：`src/Domain/Combat/DuelModels.cs:191-251`（`DuelTriggerEvaluator`）对未知取值静默返回 false/0 → 同配置在斗法中技能被禁用。
- 语义差异：`buff-stacks` 远征读真实 Buffs、斗法恒 0；`enemy-lowest-hp` 等敌人依赖在远征折叠为威胁最高目标、斗法按真实最低/最高血解析；灵力百分比远征无除零保护（MaxManapoints=0 → NaN，旧审计 M-06 未修）。

**建议**：收敛为单一求值器（`SkillTriggerEvaluator`，参数 `(caster, primaryTarget, allies, opponents, buffProvider)`）；`ReadTriggers` 复用同一校验；斗法镜像传入 Buff 快照。

### 2. Buff/支援技能两条结算路径语义不同，现网配表已踩中

- 玩家路径 `CombatSkillEngine.cs:159-163/201-223`：target∈{self,allAllies} 且零伤害 → `ApplySupport` 写持久 `ActiveBuff`（hpRegen/mpRegen/attackSpeedRatio/durationSeconds）。
- 引擎路径 `CombatModels.cs:744-766`：要求 `effectType=="buff"` 才走 buff 分支，且只读 `damageAbsorptionRatio`/`threatRatio` 写战内永久标量。
- 实锤：`ability.pozhen-melody`（破阵子，tier2 boss 技能）配表**没有 `effectType`** → 怪物放破阵子 = 吟唱 5s 后对玩家打一发 0 伤害普攻，增益永不生效。

**建议**：Buff 结算收敛进 `CastSkill` 单管线，引擎内实现战内 Buff 列表（含过期时刻）；`effectType` 判定统一为「target 为友方且伤害为零」或配表补齐字段。

### 3. 玩家远征技能仍是外挂调度器，与引擎单时间线并存

- 远征玩家：`SimulationEngine.cs:663-682`（`CombatSkillEngine.Advance` + `NextAttackAtSeconds` 顺延）；怪物/斗法镜像：引擎内 `PendingSkillIndex`（`CombatModels.cs:689-718`）。
- 差异：触发再求值粒度（≤5s 边界 vs 每次出手）；同刻结算顺序相反；玩家灵力双池（敌方 `mpDrainRatio` 对远征玩家无效，旧审计 M-01）；事件 DTO 三套形状（`CombatSkillEvent`/`CombatEvent`/`DuelScriptEvent`）；随机流 `side=0` 玩家普攻与技能共用通道导致 roll 相关；`allEnemies`/`maxTargets` 仅玩家路径实现。

**建议**：长期把玩家技能纳入 `PendingSkillIndex` 单时间线（斗法镜像已证明可行），`CombatSkillEngine` 退化为跨战冷却/吟唱持久化薄层；短期先统一灵力池与触发求值器、给斗法技能抽样独立 side 通道。

### 4. config-editor 大面积 schema 与真实 JSON 脱节（保存即产坏数据）

- `CombatTriggerEditor`：写 `{condition,thresholdRatio,action}`，服务端要求 `dependency/condition/comparator/value`（`config.ts:76-82` vs `LoadoutCommand.cs:94-100`）。
- `DropTableEditor`：写 `chance/minAmount/maxAmount`，真实键 `dropChance/minQuantity/maxQuantity`（`DropTableEditor.tsx:28` vs `GameDataCatalog.cs:771-777`）——打开现有配置字段全空、保存丢掉落。
- `AbilitiesEditor`：编辑幻影字段 `manaCostRatio`（真实为 `manaCost`）、`stun*`/`blind*`/`silence*`/`damageOverTime*`/`penetrationRatio`/`hpDrainRatio` 等无消费方；缺 `manaCost/threatGain/threatRatio/rootAttribute/discipline/buff/maxTargets`。
- `MonstersEditor`：面板直写 `maxHitpoints/armor/accuracyRating` 等引擎不读字段（真实模型为 level+rank+weights 派生）；收益键 `experience/spiritMin/spiritMax` 应为 `experienceReward/qiReward/rootExpReward`；`enrageTimeMilliseconds`/`rareDropTable` 无消费方。
- `EncountersEditor`：`enrage*`/`experiencePerWin`/遭遇级 `dropTable`/`eliteInterval` 均无消费或旧名。
- 地图类型 `npcs/buildings` 声明为 `string[]`（真实为对象数组）、边 `distance` 应为 `durationSeconds`、`ActionConfig.outputs` 应为 `yields`。

**建议**：编辑器 schema 按 `ValidateAbility`/`ParseMonsterDefinitions`/`ParseEncounterDefinitions` 实际消费集重写；DataTool 增加未消费键告警。

### 5. `tools/combat-simulator.html` 是第三套脱钩战斗模型

硬编码 `BASE_TIERS`（T1 hp310/ad17.5，真实锚点 hp100/ad10）、固定 `p_ap=ad*2.0`（锚点比值 0.5~1.2）、攻速线性减免（实现为倒数除区）、幻影技能参数（220%AD/12s 等配表不存在）、固定 3 怪波次、不建模灵力与敌方出手。结论必然偏离真实引擎。

**建议**：重写为消费 `GameDataCatalog`/`DeterministicCombatEngine` 的同构模拟器，或显著标注「非权威演示工具」并从基准文档摘除其产物。

### 6. `combat.attributes` 硬编码占位 + 前端错公式估算

- `ProtocolSnapshotMapper.cs:217-229`：`criticalDamageRatio=1.5` 硬编码（真值 `1.5+3.5p`，Lv127≈5.0）；`criticalChance` 公式与引擎不符；`style/category/element/tenacity/penetrationRatio` 等恒为占位；`SpellAttack/BreakPower/Parry/SkillIntensity/CastTimeFactor/CooldownFactor/ThreatRatio` 不下发。
- 前端 `CombatAttributePanel.tsx:85-113` 因此本地估算，且**剑术/符箓根骨维度搞反**（服务端术法=符箓、闪避=剑术，前端互换）。

**建议**：wire 补真值字段，删前端估算分支；`criticalDamageRatio` 下发 `a.CriticalMultiplier`。

## 二、中危：不一致/数值偏差

| # | 问题 | 位置 |
| --- | --- | --- |
| 7 | 玩家 vs 怪物成长曲线两处复制（progress/CastTimeFactor/CooldownFactor/SkillIntensity/Crit×/BasicAttackRatio 各写一遍）；穿透字段无生产者（死机制）；等级上限 127 四个事实源 | `PlayerAttributeResolver.cs:83-177` vs `GameDataCatalog.cs:738-831` |
| 8 | 怪物技能配表不经 `ValidateAbility` 校验；`triggerHpRatio` 与 `Triggers` 双轨（怪物 `defaultTriggers` 被忽略，治疗型怪无法表达「奶队友」） | `GameDataCatalog.cs:778-784`、`CombatModels.cs:700-702` |
| 9 | 快照同一状态三种表达：`CombatantSnapshot` vs `ExpeditionEnemyState` vs 玩家 `PlayerCombatSkillState`（冷却按 ID 剩余秒 vs 按下标绝对时刻） | `CombatModels.cs:227-237/431-447`、`CombatSkillEngine.cs:8-22` |
| 10 | 掉落判定与数量共用同一 roll（低掉率恒产最小量，旧审计 M-02 未修） | `SimulationEngine.cs:1003-1006` |
| 11 | `expected-attributes.json` 约 3400 行运行时死数据含旧公式：`1.05^enhancement`（实现为线性 1+0.02n）、`Root%=Level*0.03`、`magicStyle 2.0x AP 必中`、幻影基准技能表 | `expected-attributes.json:212-347` |
| 12 | 文档残留：`combat-benchmark-model.md:14-15/73-89` 流派幻影套件；`player-expected-attributes-model.md:40-42` 2.0x AP 必中；`K=ceil(level/12)` 与 `TierAt` 锚点公式两处矛盾（`combat-design.md:133`） | docs/design/ |
| 13 | buffs wire 占位字段（qiGainMultiplier 等恒 1）被前端收益预览真实消费——死分支 | `ProtocolSnapshotMapper.cs:165-171`、`actionCalculations.ts:168-192` |
| 14 | 装备 `specialMechanics.healingBonusRatio` 配表声明但引擎零消费（素心琴治疗加成失效） | `weapons.json:57-59` vs `PlayerAttributeResolver.cs:100-106` |

## 三、低危：清理项

- 死代码：`WorldModels.cs:307-373` `TravelEngine` 零引用；前端 `queueProgress` 通道、`ExpeditionActionProgress` 组件、`ACTION_DEFINITION_CORES` 兜底目录、`travel` 对象双通道、`CurrentReport` 冗余字段。
- 相位命名错位：wire 发 `respawn`，`getCombatPhaseLabel`（App.tsx:231）只认 `death` → 复活相位落到 default 文案（真实显示 bug）。
- 斗法对手灵力条硬编码 `1/1`（CombatArena.tsx:1318-1320）；敌方 maxHp 靠前端本地配表反查且 Hello 指纹恒 null（放弃防漂移）。
- `completesAtUnixMilliseconds` 服务端/客户端双时钟域混用（CombatArena.tsx:1140 vs 1396）。
- 玩家气血双通道（combat vs expedition 分段跨帧错位）。
- XorShift `NextSeed`、JSON 读取助手、技能等级系数、经验封顶各两份实现。
- `generic-map-and-encounter-system.md:174`「1v1 逐只推进」声明已过时（多敌同场已落地）。
- `ToAbilityDto`/`StaticGameCatalogMapper` 投影幻影字段（`penetrationRatio`/`triggerHpRatio`/`triggerAtCombatStart`/`recoverySeconds`/`tenacity` 等）。

## 四、修复优先级建议

1. 立即：#1 触发器求值器收敛（可卡死）、#2 破阵子失效 + effectType 判定、#4 编辑器 schema、#5 模拟器标注、#3 灵力双池/触发粒度。
2. 近期：#6 wire 属性真值、#8 怪物技能校验、#11/#12 文档与死数据拆分。
3. 随协议改版一次性收敛：#9 快照归一、#13 buffs wire、行动条统一「当前动作」三元组、清理项全组。

## 五、本次修复关联说明

- `PlayerAttributeResolver.cs:87` 无配表兜底 `interval=2` 已修正为 `1`（审计中确认的旧模型残留，随本次一并落地）。
- 「灵爆」概念全仓仅存于本任务记录与历史审计文档（预期保留）。

## 六、整改状态（TASK-20260923-04 分组立项）

本审计发现已按边界拆分为五个并行修复分组，各组归属如下：

| 分组 | 文件范围 | 覆盖条目 | 归属 |
| --- | --- | --- | --- |
| **A · 领域引擎** | `src/Domain/`、`src/Server/` 领域侧 | #1 触发器求值器收敛、#2 Buff/支援技能单管线（破阵子 `effectType` 失效）、#3 玩家技能纳入 `PendingSkillIndex` 单时间线/灵力双池、#7 成长曲线双份复制、#8 怪物技能校验与 `defaultTriggers` 双轨、#9 快照归一、#10 掉落 roll 拆分、#14 `healingBonusRatio` 零消费 | 领域分组任务 |
| **B · 协议与前端** | `src/Protocol`、`ProtocolSnapshotMapper`、`frontend/` | #6 `combat.attributes` 真值下发与前端估算删除、#13 buffs wire 占位字段、相位命名 `respawn`/`death` 错位、斗法灵力条硬编码、双时钟域混用、前端死代码清理 | 协议前端分组任务 |
| **C · 配置编辑器** | `tools/config-editor/` | #4 编辑器 schema 与真实 JSON 脱节重写、DataTool 未消费键告警 | 编辑器分组任务 |
| **D · 文档与数据** | `docs/design/`、`tools/combat-simulator.html`、`expected-attributes.json` 死数据段 | #5 模拟器「非权威演示工具」标注、#11 死数据陈旧公式（`1.05^强化`、`Root%=Level*0.03`、幻影流派描述）、#12 文档幻影技能/旧口径清理、`generic-map-and-encounter-system.md:174`「1v1 逐只推进」过时声明 | **本任务（TASK-20260923-04 文档与数据部分）** |
| **E · 六维命名收敛** | `Swordsmanship`/`Talisman` 等六维字段命名 | #12 关联：代码字段名与中文语义错位（Swordsmanship=身法、Talisman=神识），命名归一由集成阶段统一处理，文档侧已按身法/神识口径书写 | 集成阶段统一处理 |

> D 组已落地：模拟器横幅与旧口径文案、benchmark/expected/master-design 三份文档幻影描述、`K = ceil(level/12)` 两处修正为 `TierAt` 锚点计数、`expected-attributes.json` 死数据段落（`rules.*` 与 `benchmarkCombatSkillsModel`）对齐真实实现；运行时消费键 `baseAttributeAnchors`/`realmProgression` 数值未动。
