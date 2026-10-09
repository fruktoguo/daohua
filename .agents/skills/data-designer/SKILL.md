---
name: data-designer
description: >-
  凡在代码库中设计、评审、修改或配平技能、装备、物品、Buff、怪物与遭遇战数值时使用本技能。强制执行「强度（预算）+ 属性权重」量化方法论，确保配表中的所有数值均可被确定性推导。
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
| 13 | 生命回复 (每5秒) | `HpRegen` | 每 1 点属性值 = **4 点**预算 |
| 14 | 灵力回复 (每5秒) | `MpRegen` | 每 1 点属性值 = **4 点**预算 |

> **关键**：12 个基础属性的单位预算消耗相同（1:1），不做差异化换算——属性之间的价值差异已经由 `BaseFlatAnchors` 锚点表的数值量级天然体现（生命值数值远大于物理攻击，所以 +1 生命值自然比 +1 物理攻击便宜得多）。

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
- **吸血** (`LifeStealRatio`, `HpDrainRatio`)
- **反伤 / 护甲转化伤害** (`ArmorDamageRatio`)
- **韧性** (`Tenacity`)
- **伤害吸收** (`DamageAbsorptionRatio`)
- **暴击伤害** (`CriticalDamagePoints` / `criticalDamageBonusPoints`)
- **控制效果**（眩晕/致盲/沉默的概率与时长）
- **持续伤害** (`DamageOverTimeRatio`)
- **技能增幅点数** (`SkillBonusPoints`)

设计这些属性时，参考同阶位已有实体的数值范围，确保不出现明显失衡即可。

---

## 3. 阶位单件装备基准预算表 (源自 expected-attributes.json 权威基准)

单件白板装备的基准预算直接对应 `expected-attributes.json` 中的 `pieceBasePercentage`：

| 阶位 | 代表境界 | 代表等级 | 单件白板基准预算 $B_T$ | 期望强化 |
| :--: | :--- | :--: | :--: | :--: |
| 1 | 凡俗 [武] 凡胎 | Lv 1 | **28.0 点** (前期加强) | +3 级 |
| 2 | 凡俗 [天] 叩仙门 | Lv 18 | **40.0 点** (前期加强) | +5 级 |
| 3 | 练气 [极] 半步筑基 | Lv 30 | **40.0 点** (40%) | +7 级 |
| 4 | 筑基 [极] 半步金丹 | Lv 42 | **60.0 点** (60%) | +9 级 |
| 5 | 金丹 [极] 半步元婴 | Lv 54 | **80.0 点** (80%) | +11 级 |
| 6 | 元婴 [极] 半步化神 | Lv 66 | **100.0 点** (100%) | +13 级 |
| 7 | 化神 [极] 半步炼虚 | Lv 78 | **140.0 点** (140%) | +15 级 |
| 8 | 炼虚 [极] 半步合体 | Lv 90 | **180.0 点** (180%) | +17 级 |
| 9 | 合体 [极] 半步大乘 | Lv 102 | **240.0 点** (240%) | +19 级 |
| 10 | 大乘 [极] 半步渡劫 | Lv 114 | **300.0 点** (300%) | +20 级 |
| 11 | 渡劫 [极] 半步飞升 | Lv 126 | **400.0 点** (400%) | +20 级 |
| 12 | 飞升 [极] 飞升真仙 | Lv 127 | **500.0 点** (500%) | +20 级 |

权威数据真源：[`config/game-data/expected-attributes.json`](file:///home/yuohira/game/xiuxian/config/game-data/expected-attributes.json)

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

代码位置：[`DataBudgetResolver.ResolveAbility()`](file:///home/yuohira/game/daohua/src/Domain/GameData/DataBudgetResolver.cs#L186-L305)

### 4.5 现有技能参考

查看当前所有技能定义：[`abilities.json`](file:///home/yuohira/game/daohua/config/game-data/abilities.json)

---

## 5. 装备设计模型

### 5.1 核心设计理念

1. **显式直接配置**：取消“黑盒权重反向生成属性”机制。装备属性采用两段式显式配置，所见即所得。
2. **正向预算审计定位**：预算系统作为**设计辅助与校验工具**。根据填写的实际属性加成正向计算总消耗预算，对比阶位目标基准 $B_T$，自动识别严重偏离（超标或亏模）的装备。

### 5.2 装备两段式数据模型

装备属性明确分为两大区间：
- **基础 14 属性百分比增幅区间 (`BaseStatRatios`)**：正常情况下装备所赋予的 14 项基础属性百分比加成（1% = 1.0 点预算）。
- **额外全属性固定值与特殊机制区间 (`FlatAndSpecialStats`)**：
  - 固定值（如 +100 生命、+2 护甲）直接填写，不走反推。
  - 特殊机制（穿透、攻速、CD 减免、吸血、反伤等）直接配置，不走预算分摊。

### 5.3 品质乘区与部位权重

| 品质 | 名称 | 倍率 $Q_{\text{mult}}$ |
| :--: | :--: | :--: |
| Q1 | 凡品 | 1.00× |
| Q2 | 灵品 | 1.15× |
| Q3 | 玄品 | 1.35× |
| Q4 | 地品 | 1.60× |
| Q5 | 天品 | 1.90× |
| Q6 | 仙品 | 2.30× |
| Q7 | 神品 | 2.80× |
| Q8 | 混沌 | 3.50× |

**部位预算权重（6 件套总和 6.0× 严格守恒）**：
- **武器**：**1.4×**（倾斜攻击，T1=40点 / T2=56点）
  - *重装刀系*：攻击+护甲双加成，附带 +200%(T1)/+300%(T2) 额外威胁值。
  - *辅助琴系*：专精治疗与回蓝（无需命中），附带 +50% 额外治疗加成，总预算减少 25%（T1=30点 / T2=42点）。
- **衣服**：**1.4×**（倾斜防护，T1=40点 / T2=56点）
- **头 / 腿 / 鞋 / 饰**：**0.8×**（各扣减一点，T1=22点 / T2=32点）
  - *自动回复收拢*：生命/灵力回复主要集中于饰品（1点回复消耗 4 点预算），前期防具不引入破招拆招。
- **总和验算**：$1.4 + 1.4 + 0.8 \times 4 = 6.0\times$（全套总预算严格等于 $6 \times B_T$）

### 5.4 强化成长模型

强化不占用配表预算，而是按统一规则自动成长：

$$\text{强化总倍率} = 1.05^{\text{强化等级}} \quad (\text{每级指数提升 } \times 1.05)$$

强化至 +20 满级时 = 总属性约为白板的 2.653×（+165%）。

### 5.5 预算正向审计公式

$$\text{实际消耗预算 } B_{\text{actual}} = \sum (\text{基础 14 属性百分比点数} \times 1.0) + \dots$$
$$\text{目标预算 } B_{\text{target}} = B_T \times Q_{\text{mult}}$$
$$\text{偏差率 } \Delta = \frac{B_{\text{actual}} - B_{\text{target}}}{B_{\text{target}}} \times 100\%$$
- $|\Delta| \le 20\%$：平衡正常（绿色）
- $\Delta > +20\%$：装备超标（预警）
- $\Delta < -20\%$：装备亏模（建议补齐）

---

## 6. 消耗品与 Buff 设计模型

### 6.1 消耗品

- 瞬时恢复药预算：$B = \text{Tier} \times 15.0$
- 恢复量按基础属性预算换算（HP/MP 恢复属于基础属性，1:1 预算）

### 6.2 Buff

Buff 提供的百分比增幅同样遵循 1 点预算 = 1% 增幅的规则。持续时长和叠加层数属于特殊属性，人工平衡。

### 6.3 物品配表最简原则

配表严禁平铺或硬编码无意义的默认参数（如 `0`、`false`、`1.0`、空数组 `[]`）：
- **功法秘籍与残卷 (`category: manual` / `item.tome.*` / `item.fragment.*`)**：
  - 底层由 `SimulationEngine.UseItem` 独立处理技能领悟与经验结算。
  - 其 `consumable` 块**仅需声明 `usableInActionTypes`**，严禁填写 `hitpointRestore`、`cooldownSeconds`、`buffs` 等冗余丹药占位字段。
- **丹药与增益消耗品 (`category: pill` 等)**：
  - 仅需显式配置非默认的生效属性（如生效的 `durationSeconds`、`cultivationMultiplier`、`buffs` 或 `cooldownSeconds`）。
  - 未配置的属性在领域层由 `DataBudgetResolver` 自动赋予精准默认值，禁止在 JSON 中全量平铺默认字段。

代码位置：[`DataBudgetResolver.ResolveItem()`](file:///home/yuohira/game/daohua/src/Domain/GameData/DataBudgetResolver.cs)

---

## 7. 怪物设计模型

怪物属性 = 目标等级**期望玩家面板** × 层级倍率 × 倾向权重

> **底座口径**：基准玩家属性 = `expected-attributes.json` 的 `levelExpected[].finalExpected`（(基础数值池 + 六维固定值 `rootFlat`) × (1+六维% `rootMultiplier`) × 装备乘区 × Buff 乘区），按怪物 `level` 取每级锚点（Lv1~127 逐级求解）；**不是** `baseAttributeAnchors` 裸底座。误用裸底座会让高阶怪物攻防全面掉队（T12 妖王对玩家近零伤害）。六维每点同时给固定值与百分比（固定值：生命 +12、灵力 +7、术攻 +2、其余 +1；百分比：映射属性 +1%、神识术攻 +2%），期望建模为各属性 `ExpectPoints(Level)` 点：`3×Level`（全线性，一级境界对应三点，Lv18 收敛 54、Lv127 收敛 381）。六维升级成本侧由 `RealmProgressionCatalog` 派生的 381 点成本表结算（每维 127 级×3 点；第 p 点 ≈ 跨入 ⌈p/3⌉ 级的一格修为条，相邻档线性插值），单维满培养累计 ≈ 3× 境界累计修为——期望线是满配基准，自然击杀 1:1 入账的节奏低于期望，缺口靠内功 `rootExpMultipliers` 与效率通道补。

| 怪物层级 | 全属性倍率 | HP 倍率 |
| :--- | :--: | :--: |
| 普通妖修 (Minion) | 0.8× | 3.0× |
| 精英大妖 (Elite) | 1.2× | 5.0× |
| 妖王首领 (Boss) | 1.5× | 20.0× |

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

- 基础属性锚点表：[`BaseFlatAnchors`](file:///home/yuohira/game/daohua/src/Domain/Simulation/CombatFormula.cs#L250-L264)
- 玩家面板构建：[`BuildPlayerStats()`](file:///home/yuohira/game/daohua/src/Domain/Simulation/CombatSimulator.cs#L1200-L1320)
- 技能伤害计算：[`ResolveCombat()`](file:///home/yuohira/game/daohua/src/Domain/Simulation/CombatSimulator.cs#L1440-L1506)
- 预算解析器：[`DataBudgetResolver`](file:///home/yuohira/game/daohua/src/Domain/GameData/DataBudgetResolver.cs)

### 8.2 配置文件位置

- 技能：[`config/game-data/abilities.json`](file:///home/yuohira/game/daohua/config/game-data/abilities.json)
- 技能数据模型：[`CombatAbilityDefinition`](file:///home/yuohira/game/daohua/src/Domain/Models/CombatModels.cs#L100-L135)
- 装备数据模型：[`CombatEquipmentDefinition`](file:///home/yuohira/game/daohua/src/Domain/Models/CombatModels.cs#L247-L255)
- 战斗面板模型：[`CombatAttributeState`](file:///home/yuohira/game/daohua/src/Domain/Models/CombatModels.cs#L317-L351)

---

## 9. 速查总结

```
可量化预算体系（只有这些参与预算计算）：
├── 12 个基础属性值：每 1 点 = 1 点预算
├── 2 个自动回复（生命/灵力）：每 1 点 = 4 点预算
└── 任意属性百分比增幅：每 1% = 1 点预算

特殊属性（不量化，人工平衡）：
└── CD、攻速、穿透、吸血、反伤、控制、韧性、暴伤等

技能预算 = 阶位基准预算 × 类型倍率 + 耗蓝补偿 + 吟唱补偿
装备预算 = 阶位基准预算 × 品质乘区 × 部位权重 (武器/衣服 1.4x, 头/腿/鞋/饰 0.8x)
