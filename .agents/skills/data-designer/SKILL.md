---
name: data-designer
description: >-
  Use this skill whenever designing, reviewing, modifying, or balancing skills, equipment, items, buffs, monsters, and encounters in the Daoxuan codebase. Enforces the strict 'Intensity (Budget) + Attribute Weights' quantitative methodology, ensuring all numerical values in config files are derived deterministically.
---

# 《道化太初》数值设计规范

## 1. 核心原则

所有战斗数值配表必须有据可查、可推导、可复现。禁止拍脑袋填数。

数值精度统一使用 `double`，百分比以点数存储（1 点 = 1%）。

---

## 2. 可量化属性体系

### 2.1 全部可量化属性只有两类

#### 第一类：12 个基础属性 + 2 个自动回复（共 14 项）

这 14 项是唯一参与预算量化的属性。其基准数值由 `BaseFlatAnchors` 锚点表按境界等级插值决定。

| 编号 | 属性 | 代码字段 | 预算消耗 |
| :--: | :--- | :--- | :--: |
| 1 | 最大生命值 | `BaseHitpoints` | 每 1 点属性值 = **1 点**预算 |
| 2 | 最大灵力值 | `BaseManapoints` | 每 1 点属性值 = **1 点**预算 |
| 3 | 物理攻击 | `BasePhysicalAttack` | 每 1 点属性值 = **1 点**预算 |
| 4 | 术法攻击 | `BaseSpellAttack` | 每 1 点属性值 = **1 点**预算 |
| 5 | 物理护甲 | `BaseArmor` | 每 1 点属性值 = **1 点**预算 |
| 6 | 法术抗性 | `BaseMagicResistance` | 每 1 点属性值 = **1 点**预算 |
| 7 | 命中 | `BaseAccuracy` | 每 1 点属性值 = **1 点**预算 |
| 8 | 闪避 | `BaseEvasion` | 每 1 点属性值 = **1 点**预算 |
| 9 | 暴击 | `BaseCritical` | 每 1 点属性值 = **1 点**预算 |
| 10 | 免爆 | `BaseCriticalAvoidance` | 每 1 点属性值 = **1 点**预算 |
| 11 | 破招 | `BaseBreak` | 每 1 点属性值 = **1 点**预算 |
| 12 | 拆招 | `BaseParry` | 每 1 点属性值 = **1 点**预算 |
| 13 | 生命回复 | `HpRegenPerTenSeconds` | 每 1 点属性值 = **3 点**预算 |
| 14 | 灵力回复 | `MpRegenPerTenSeconds` | 每 1 点属性值 = **3 点**预算 |

> **关键**：12 个基础属性的单位预算消耗相同（1:1），不做差异化换算——属性之间的价值差异已经由 `BaseFlatAnchors` 锚点表的数值量级天然体现（HP 数值远大于 AD，所以 +1 HP 自然比 +1 AD 便宜得多）。

#### 第二类：任意属性的百分比增幅

对上述 14 项基础属性中的任意一项进行百分比增幅加成：

| 增幅类型 | 预算消耗 |
| :--- | :--: |
| 任意属性 +1% 百分比增幅 | **1 点**预算 |

百分比增幅是独立乘区，最终面板 = 基础数值 × (1 + 百分比增幅之和)。

### 2.2 特殊属性（不量化，人工平衡）

以下属性无法用统一的预算公式量化，每次设计时需要人工判断合理性：

- **CD 减免** (`CooldownReductionRatio`)
- **行动加速** (`ActionSpeedReductionRatio`)
- **穿透率** (`PenetrationRatio`)
- **穿透追击** (`PierceChance`)
- **吸血** (`LifeStealRatio`, `HpDrainRatio`)
- **反伤 / 护甲转化伤害** (`ArmorDamageRatio`)
- **诅咒** (`CurseRatio`)
- **狂暴** (`FuryRatio`)
- **削弱** (`WeakenRatio`)
- **韧性** (`Tenacity`)
- **伤害吸收** (`DamageAbsorptionRatio`)
- **暴击伤害** (`CriticalDamageRatio`)
- **控制效果**（眩晕/致盲/沉默的概率与时长）
- **持续伤害** (`DamageOverTimeRatio`)
- **技能强度** (`SkillIntensity`)

设计这些属性时，参考同阶位已有实体的数值范围，确保不出现明显失衡即可。

---

## 3. 阶位基准预算表

每个阶位有一个固定的**基准预算**，所有同阶位实体的总预算都应以此为参考基础。

| 阶位 | 代表境界 | 代表等级 | 基准预算 $B_T$ |
| :--: | :--- | :--: | :--: |
| 1 | 凡俗 \[武\] 凡胎 | Lv 1 | 10.0 |
| 2 | 凡俗 \[天\] 叩仙门 | Lv 18 | 25.0 |
| 3 | 练气 \[极\] 半步筑基 | Lv 30 | 60.0 |
| 4 | 筑基 \[极\] 半步金丹 | Lv 42 | 130.0 |
| 5 | 金丹 \[极\] 半步元婴 | Lv 54 | 250.0 |
| 6 | 元婴 \[极\] 半步化神 | Lv 66 | 450.0 |
| 7 | 化神 \[极\] 半步炼虚 | Lv 78 | 750.0 |
| 8 | 炼虚 \[极\] 半步合体 | Lv 90 | 1,150.0 |
| 9 | 合体 \[极\] 半步大乘 | Lv 102 | 1,700.0 |
| 10 | 大乘 \[极\] 半步渡劫 | Lv 114 | 2,450.0 |
| 11 | 渡劫 \[极\] 半步飞升 | Lv 126 | 3,400.0 |
| 12 | 飞升 \[极\] 飞升真仙 | Lv 127 | 4,600.0 |

代码位置：[`DaoxuanDataBudgetResolver.GetTierBaseBudget()`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/GameData/DaoxuanDataBudgetResolver.cs#L123-L138)

---

## 4. 技能设计模型

### 4.1 核心思路

技能的总消耗预算 = **参考强度 × 倍率**。

参考强度就是同阶位的基准预算 $B_T$。倍率根据技能类型决定：

| 技能类型 | 倍率 $K$ | 说明 |
| :--- | :--: | :--- |
| 平 A / 填充技（0 CD） | 0.5 | 高频施放，单次弱 |
| 核心单体技（CD 6~12s） | 1.0 | 标准参考值 |
| 长 CD 大招（CD 15~20s） | 1.5 | 单次强力 |
| 群攻 AoE | 1.2 | 单目标伤害需除以 $\sqrt{N}$ |

$$B_{\text{skill}} = B_T \times K$$

### 4.2 预算修正

| 条件 | 修正 |
| :--- | :--- |
| 每 10 点灵力消耗 | +0.8 预算 |
| 吟唱时间 ≥ 2.0s | ×1.15 总预算 |

### 4.3 技能预算分配

将总预算按权重分配给各属性效果（所有权重之和 = 1.0）：

- **伤害倍率** (`DamageRatio`)：1.0 预算 = +0.025 倍率（基底 1.0）
- **固定伤害** (`FlatDamage`)：1.0 预算 = +4.2 固定伤害
- **治疗倍率** (`HealingRatio`)：1.0 预算 = +0.015 倍率
- **固定治疗** (`FlatHealing`)：1.0 预算 = +5.0 治疗量
- **持续伤害** (`DamageOverTimeRatio`)：1.0 预算 = +0.02 倍率
- **控制 / 穿透 / 吸血等特殊效果**：按人工平衡分配，参考现有技能

### 4.4 等级成长自动推导

若未显式指定：

- `DamageRatioLevelBonus` = `DamageRatio × 0.006`
- `FlatDamageLevelBonus` = `FlatDamage × 0.01`
- 治疗同理

代码位置：[`DaoxuanDataBudgetResolver.ResolveAbility()`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/GameData/DaoxuanDataBudgetResolver.cs#L186-L305)

### 4.5 现有技能参考

查看当前所有技能定义：[`abilities.json`](file:///home/yuohira/game/daoxuan/config/game-data/abilities.json)

---

## 5. 装备设计模型

### 5.1 预算计算

$$B_{\text{equip}} = B_T \times Q_{\text{mult}} \times S_{\text{weight}} \times 5.0$$

- $Q_{\text{mult}}$：品质乘区（见 §5.2）
- $S_{\text{weight}}$：槽位权重（武器 20%，甲 20%，头 15%，腿 15%，戒 15%，链 15%）

### 5.2 品质乘区

| 品质 | 名称 | 倍率 |
| :--: | :--: | :--: |
| Q1 | 凡品 | 1.00× |
| Q2 | 灵品 | 1.15× |
| Q3 | 玄品 | 1.35× |
| Q4 | 地品 | 1.60× |
| Q5 | 天品 | 1.90× |
| Q6 | 仙品 | 2.30× |
| Q7 | 神品 | 2.80× |
| Q8 | 混沌 | 3.50× |

### 5.3 装备属性类型

装备提供的属性全部是**百分比增幅**（`DaoxuanEquipmentCombatStats` 中的字段），每个增幅属性消耗 1 点预算/1% 增幅。

装备也可以提供特殊属性（穿透、吸血等），这些不走预算量化，人工分配。

### 5.4 强化成长

$$\text{每级强化增量} = \frac{\text{基础属性}}{50.0}$$

强化至 +20 满级时 = 总属性约 +40%。

代码位置：[`DaoxuanDataBudgetResolver.ResolveEquipment()`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/GameData/DaoxuanDataBudgetResolver.cs#L311-L365)

---

## 6. 消耗品与 Buff 设计模型

### 6.1 消耗品

- 瞬时恢复药预算：$B = \text{Tier} \times 15.0$
- 恢复量按基础属性预算换算（HP/MP 恢复属于基础属性，1:1 预算）

### 6.2 Buff

Buff 提供的百分比增幅同样遵循 1 点预算 = 1% 增幅的规则。持续时长和叠加层数属于特殊属性，人工平衡。

代码位置：[`DaoxuanDataBudgetResolver.ResolveItem()`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/GameData/DaoxuanDataBudgetResolver.cs#L370-L417)

---

## 7. 怪物设计模型

怪物属性 = 目标境界基准玩家属性 × 层级倍率 × 倾向权重

| 怪物层级 | 全属性倍率 | HP 倍率 |
| :--- | :--: | :--: |
| 普通妖修 | 1.0× | 1.0× |
| 精英大妖 | 1.2× | 3.0~3.6× |
| 妖王首领 | 1.5× | 10.0~14.0× |

怪物的特殊属性（控制、穿透等）人工配置，参考玩家对应阶位的抗性水平。

---

## 8. 设计工作流程

```
1. 确定阶位和定位 → 2. 查表获得基准预算
    → 3. 根据类型乘以倍率得到总预算
    → 4. 分配可量化属性（基础 / 百分比增幅）
    → 5. 分配特殊属性（人工平衡，参考同类）
    → 6. 写入配置文件
    → 7. 运行测试验证
```

### 8.1 关键代码参考

- 基础属性锚点表：[`BaseFlatAnchors`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/Simulation/DaoxuanCombatFormula.cs#L250-L264)
- 玩家面板构建：[`BuildPlayerStats()`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/Simulation/DaoxuanCombatSimulator.cs#L1200-L1320)
- 技能伤害计算：[`ResolveCombat()`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/Simulation/DaoxuanCombatSimulator.cs#L1440-L1506)
- 预算解析器：[`DaoxuanDataBudgetResolver`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/GameData/DaoxuanDataBudgetResolver.cs)

### 8.2 配置文件位置

- 技能：[`config/game-data/abilities.json`](file:///home/yuohira/game/daoxuan/config/game-data/abilities.json)
- 技能数据模型：[`DaoxuanCombatAbilityDefinition`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/Models/DaoxuanCombatModels.cs#L100-L135)
- 装备数据模型：[`DaoxuanCombatEquipmentDefinition`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/Models/DaoxuanCombatModels.cs#L247-L255)
- 战斗面板模型：[`DaoxuanCombatAttributeState`](file:///home/yuohira/game/daoxuan/src/Daoxuan.Domain/Models/DaoxuanCombatModels.cs#L317-L351)

---

## 9. 速查总结

```
可量化预算体系（只有这些参与预算计算）：
├── 12 个基础属性值：每 1 点 = 1 点预算
├── 2 个自动回复：每 1 点 = 3 点预算
└── 任意属性百分比增幅：每 1% = 1 点预算

特殊属性（不量化，人工平衡）：
└── CD、攻速、穿透、吸血、反伤、控制、韧性、暴伤等

技能预算 = 阶位基准预算 × 类型倍率 + 耗蓝补偿 + 吟唱补偿
装备预算 = 阶位基准预算 × 品质乘区 × 槽位权重 × 5.0
```
