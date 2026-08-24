# 道化太初 · 通用地图架构、生物（NPC/怪物）与质量预算遇怪系统设计规范

> **版本**：v1.0.0  
> **状态**：设计评审中  
> **编写时间**：2026-08-23  
> **对应任务**：[TASK-20260823-06](../tasks/TASK-20260823-06.md)

---

## 1. 核心设计哲学

### 1.1 摒弃纯战斗地图，确立“通用世界地点”模型
在《道化太初》中，**不存在与大世界割裂的“纯战斗副本”或“纯刷怪地图”**。
所有地点（`WorldNode`）都是世界中的真实通用地理节点（例如：青岚药谷、青云山门、落霞西岭、玄铁矿洞、流云集等）。

每一个世界地点都拥有自己独立的生态聚合体：
1. **行动体系（Actions）**：
   - **生活 / 采集 / 生产行动**（如：采药 `explore.gather-herbs`、挖矿 `explore.mine-ore`、炼丹 `craft.alchemy` 等）；
   - **历练探索行动**（如：后山寻猎 `combat.hunt-qi-beast`、清剿裂谷 `combat.sweep-twilight-valley`）。
2. **生物生态（Creatures）**：分为 **NPC** 与 **怪物（Monsters）** 两个支柱。
3. **地理建筑 / 阵法设施（Buildings）**：如山门防护阵、聚灵泉眼、老井、哨塔等。

```
┌─────────────────────────────────────────────────────────────┐
│                    通用大世界地点 (WorldNode)                  │
│                     例：青岚药谷 (node.herb-vale)            │
├──────────────────────────────┬──────────────────────────────┤
│ 1. 驻留生物 (Creatures)       │ 2. 开放行动 (Actions)         │
│  ├─ NPC 列表 (任务/传功/剧情)  │  ├─ 采集类行动 (采药/挖矿)    │
│  └─ 怪物生态池 (历练遇怪/掉落) │  └─ 历练类行动 (战斗/清剿)    │
├──────────────────────────────┴──────────────────────────────┤
│ 3. 危险度与预算参数 (Hazard / Budget Parameters)              │
│  ├─ 危险度总预算区间 [minBudget, maxBudget] (直接代表危险度)  │
│  ├─ 精英刷新周期 (eliteInterval，如每 5 次遭遇)              │
│  └─ 领主刷新周期 (bossInterval，如每 20 次遭遇)             │
└─────────────────────────────────────────────────────────────┘
```

---

## 2. 生物生态数据模型（NPC 与 怪物）

### 2.1 NPC 数据模型（人物 / 住民）
NPC 代表该地点的常驻居民、散修、执事或向导。
- **当前阶段定位**：数据结构完整规范，界面展示头像与描述，**行为交互暂时留空（预留扩展接口）**。
- **后续扩展职能**：发布委托与支线任务、传授技能功法、提供特定交互对话与好感度。

#### 数据结构定义（`NpcDefinition`）：
```typescript
interface NpcDefinition {
  id: string;              // 唯一标识，如 "npc.herbalist-lin"
  name: string;            // 姓名，如 "采药人林伯"
  title?: string;          // 头衔/称号，如 "药谷向导"
  description: string;     // 角色生平与描述
  avatar?: string;         // NPC 头像资源路径/图标
  effectDescription?: string; // 当前环境职能描述（如"后续可提供采集委托"）
  // 后续扩展字段预留
  questIds?: string[];     // 关联任务列表 (预留)
  teachableAbilityIds?: string[]; // 可传授技能列表 (预留)
}
```

---

### 2.2 怪物数据模型（Monsters / 妖兽）
怪物代表栖息在该地点的野生妖兽、异化生物或敌对修士。
每个怪物除了具备既定的阶位（`Tier`）、层次（`Rank`）与属性权重外，**必须配置单体消耗预算 `budgetCost`（对应灵狐的 Quality 质量点数）**。

#### 数据结构定义（`MonsterConfig`）：
{
  "id": "enemy.demonized-rabbit",
  "name": "魔化野兔",
  "tier": 1,
  "rank": "minion",
  "budgetCost": 15,
  "style": "body",
  "category": "physical",
  "element": "none",
  "weights": {
    "hp": 1.15,
    "ad": 1.0,
    "armor": 0.65,
    "hit": 1.0,
    "eva": 1.0
  },
  "dropTable": [
    {
      "itemId": "item.beast-hide",
      "dropChance": 1.0,
      "minQuantity": 1,
      "maxQuantity": 1
    }
  ],
  "rareDropTable": []
}

#### 典型预算消耗梯度基准：
| 怪物层次 (`Rank`) | 消耗预算 (`budgetCost`) | 典型代表 | 战力定位 |
| :--- | :--- | :--- | :--- |
| **普通杂鱼小怪 (Minion - Low)** | 10 ~ 15 点 | 魔化野兔、溪边幼龟 | 最基础练手怪，单体弱，常成群出现 |
| **强力小怪 (Minion - High)** | 20 ~ 30 点 | 木灵、火灵、通臂魔猿 | 具备初级法术或特定抗性 |
| **精英怪 (Elite)** | 40 ~ 60 点 | 刀煞尸煞、黑煞猿首 | 拥有 5 倍生命与强力技能，5 波遭遇一次 |
| **关底领主 (Boss)** | 100 ~ 200 点 | 伥鬼妖虎、坠星异兽 | 拥有 20 倍生命，关底大怪，20 波遭遇一次 |

---

## 3. 地图 (Maps) 与遭遇 (Encounters) 解耦架构

### 3.1 架构解耦设计理念
系统将**世界地理拓扑（Maps）**与**战斗遭遇生态（Encounters）**完全解耦，分别存储于 `config/game-data/maps/` 与 `config/game-data/encounters/`：
1. **地图配置 (`maps/`) 聚焦地理拓扑与设施**：定义大世界节点坐标（`x`, `y`）、背景设定、开放的生活/采集行动（`availableActionIds`）、场景常驻建筑（`buildings`）与 NPC，并通过 `encounterIds` 弱引用战斗遭遇；
2. **遭遇配置 (`encounters/`) 聚焦战力预算与怪群生成**：定义遭遇的质量预算区间 `[minBudget, maxBudget]`、怪群随机抽取权重表（`randomSpawns`）、精英怪池（`eliteEnemyIds`）与领主怪池（`bossEnemyIds`）；
3. **解耦优势**：同一套战斗遭遇与怪物生态可被多个地理节点复用，地图节点亦可独立挂载多种遭遇副本，数值调整与地图绘制互不干扰。

### 3.2 地图生灵生态原则（特色化分布）
- **按地理风貌分布 1~3 种怪物**：同阶（如 T1）怪物不需要全部挤在同一张地图内。每个地图节点根据其地理环境、五行属性与剧情风貌分配 1~3 种契合的怪物即可（灵活可配，不强制多怪）。
  - **青岚药谷 (`node.herb-vale`)**：草木繁茂之地，主要栖息【木灵】（或木灵 + 偶尔出没的野兔）；
  - **落霞西岭 (`node.sunset-ridge`)**：开阔山岭，主要出没【魔化野兔】与【重甲蛮熊】；
  - **暮影关 (`node.twilight-pass`)**：阴煞险关，主要出没【火灵】、【通臂魔猿】与关底【伥鬼妖虎】；
  - **澄心灵泉 (`node.spirit-spring`)**：静修灵泉，可仅出没水系生灵或作为清修少怪之地。

### 3.3 地图与遭遇配置结构示例

```json
[
  {
    "id": "map.qingyun-mountains",
    "name": "青云山脉",
    "description": "灵脉、山谷和古道交错的入门地域。",
    "nodes": [
      {
        "id": "node.herb-vale",
        "name": "青岚药谷",
        "description": "低阶灵草沿溪谷生长，是初入修行者最稳定的采集与历练之地。",
        "x": 13,
        "y": 63,
        "cultivationMultiplier": 1.0,
        "minBudget": 20,
        "maxBudget": 50,
        "eliteInterval": 5,
        "bossInterval": 20,
        "availableActionIds": [
          "explore.gather-herbs",
          "combat.hunt-qi-beast"
        ],
        "monsters": [
          { "monsterId": "enemy.wood-spirit", "weight": 1 }
        ],
        "elitePool": [
          { "monsterId": "enemy.demon-ape", "weight": 1 }
        ],
        "bossPool": [
          { "monsterId": "enemy.chang-tiger", "weight": 1 }
        ],
        "npcs": [
          {
            "id": "npc.herbalist-lin",
            "name": "采药人林伯",
            "title": "老药农",
            "description": "熟悉药谷草木灵气流动的老采药人。",
            "effectDescription": "后续可发布药草搜集委托"
          }
        ],
        "buildings": []
      }
    ]
  }
]
```

### 关键配置字段解析：
- **`minBudget` ~ `maxBudget`（危险度 / 总预算区间）**：**地图没有单独的危险度评级，危险度本身即为 `[minBudget, maxBudget]` 预算区间**（对应灵狐中的 `[minQuality, maxQuality]`）。单次遇怪时系统在 `[minBudget, maxBudget]` 之间随机生成本场怪群的战力总预算，预算区间的高低直接体现了该地图遭遇战的危险程度与怪物强度规模。
- **`eliteInterval`（精英周期）**：每进行 N 次战斗必定触发一次精英遇怪（默认 5）。
- **`bossInterval`（Boss 周期）**：每进行 N 次战斗必定触发一次关底领主 Boss 遇怪（默认 20）。
- **`monsters`（本地普通怪池）**：当前地图栖息的 1~3 种普通怪物及权重。
- **`elitePool`（本地精英怪池）**：触发精英波次时抽取的精英阵容。
- **`bossPool`（本地 Boss 怪池）**：触发领主波次时抽取的 Boss 阵容。

---

## 4. 遇怪与怪群生成算法规范

服务端在玩家完成每次寻怪倒计时（`Search` 阶段）后，调用确定性怪群生成算法：

```mermaid
flowchart TD
    Start([寻怪倒计时结束]) --> CheckCount[读取当前地点累计历练完成波次 N]
    
    CheckCount --> IsBoss{(N + 1) % bossInterval == 0 ?}
    IsBoss -- 是 (如第20/40场) --> SpawnBoss[从 bossPool 抽取 1 只 Boss<br/>总预算分配 maxBudget]
    
    IsBoss -- 否 --> IsElite{(N + 1) % eliteInterval == 0 ?}
    IsElite -- 是 (如第5/10/15场) --> SpawnElite[从 elitePool 抽取 1 只精英怪<br/>扣除该精英 budgetCost]
    IsElite -- 否 --> NormalRoll[在 [minBudget, maxBudget] 随机生成总预算 remainBudget]
    
    SpawnElite --> DeductElite[剩余预算 remainBudget 继续用于抽取小怪护从]
    DeductElite --> LoopExtract
    NormalRoll --> LoopExtract
    
    subgraph LoopExtract [质量预算扣减循环 (灵狐 Quality 算法)]
        Init[初始化 failCount = 0, spawnedList = []] --> CheckLoop{failCount < 3 且<br/>remainBudget > 0 且<br/>数量 < 上限(如5只)?}
        CheckLoop -- 是 --> RollMonster[从普通怪池按 weight 轮盘抽取候选怪物]
        RollMonster --> CheckCost{candidate.budgetCost <= remainBudget ?}
        CheckCost -- 是 (能塞下) --> AddMonster[将怪物加入 spawnedList<br/>remainBudget -= candidate.budgetCost<br/>failCount = 0]
        CheckCost -- 否 (超预算) --> AddFail[failCount++]
        AddMonster --> CheckLoop
        AddFail --> CheckLoop
    end
    
    CheckLoop -- 否 (循环结束) --> CheckEmpty{spawnedList 是否为空?}
    CheckEmpty -- 是 (保底) --> SafetyFallback[保底塞入 1 只消耗预算最低的普通小怪]
    CheckEmpty -- 否 --> Finalize[生成 GroupEnemyRuntime 怪群列表]
    SafetyFallback --> Finalize
    SpawnBoss --> Finalize
    
    Finalize --> BattleSnapshot[构建 BattleSnapshot 封存入场快照，进入 Combat 战斗]
```

### 算法执行示例：

#### 示例 A：单怪特色地图（如青岚药谷，仅栖息木灵）
- **配置**：`minBudget: 20`, `maxBudget: 60`, `monsters: [{ monsterId: "enemy.wood-spirit", weight: 1 }]`（木灵 `budgetCost: 25`）
- **常规波次**：
  - 随机本场总预算 `remainBudget = 55`；
  - 第 1 次抽取：抽中【木灵】（`cost 25 <= 55`），加入阵容，`remainBudget = 30`；
  - 第 2 次抽取：抽中【木灵】（`cost 25 <= 30`），加入阵容，`remainBudget = 5`；
  - 第 3~5 次抽取：抽中【木灵】（`cost 25 > 5`），连续 3 次超预算，循环终止；
  - **本场阵容**：`[木灵, 木灵]`（根据预算生成 2 只木灵同场）。

#### 示例 B：多怪复合地图（如落霞西岭，栖息野兔与蛮熊）
- **配置**：`minBudget: 30`, `maxBudget: 80`, `monsters: [{ monsterId: "enemy.demonized-rabbit", weight: 5 }, { monsterId: "enemy.armored-bear", weight: 2 }]`
  - 魔化野兔（`cost: 15`）、重甲蛮熊（`cost: 35`）
- **常规波次**：
  - 随机本场总预算 `remainBudget = 65`；
  - 第 1 次抽取：抽中【重甲蛮熊】（`cost 35 <= 65`），加入阵容，`remainBudget = 30`；
  - 第 2 次抽取：抽中【魔化野兔】（`cost 15 <= 30`），加入阵容，`remainBudget = 15`；
  - 第 3 次抽取：抽中【魔化野兔】（`cost 15 <= 15`），加入阵容，`remainBudget = 0`；
  - 预算耗尽，循环终止；
  - **本场阵容**：`[重甲蛮熊, 魔化野兔, 魔化野兔]`（共 3 只怪）。

---

## 5. 前端界面与交互设计

前端主要涉及「地图/附近（`map`）」面板与「历练（`exploration`）」面板的展示与交互联动。

### 5.1 地图 / 附近面板（World Map & Nearby Display）
当玩家停留在某个地图节点时，面板直观展示该地点的全景信息：

```
┌─────────────────────────────────────────────────────────────┐
│ 📍 青岚药谷                                  [ 危险度: 30 ~ 60 ]│
│ 低阶灵草沿溪谷生长，是初入修行者最稳定的采集与历练之地。      │
│ 修炼增益: 1.0x    预算池: 30 ~ 60                           │
├─────────────────────────────────────────────────────────────┤
│ 👥 常驻人物 (NPC)                                           │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ 🧑‍🌾 采药人林伯 (老药农)                                  │ │
│ │ 熟悉药谷路径的老采药人。 (后续可提供采集委托)           │ │
│ └─────────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────┤
│ 🐾 栖息生灵 (Monsters)                  [ 5战遇精英 / 20战遇领主 ]│
│ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐       │
│ │ [立绘] 魔化野兔│ │ [立绘] 木灵   │ │ [立绘] 伥鬼妖虎│       │
│ │ 预算消耗: 15  │ │ 预算消耗: 25  │ │ 关底领主 BOSS │       │
│ │ 掉落: 聚气兽核│ │ 掉落: 聚气兽核│ │ 掉落: 妖虎内丹│       │
│ └───────────────┘ └───────────────┘ └───────────────┘       │
│ (点击怪物卡片可弹出查看气血、攻防、抗性与技能详情)          │
├─────────────────────────────────────────────────────────────┤
│ ⚡ 本地可选行动                                             │
│ [ 🌿 采集中品草药 ]                [ ⚔️ 药谷后山历练 ]       │
└─────────────────────────────────────────────────────────────┘
```

### 5.2 怪物详情查看弹窗（`MonsterInfoModal`）
- 点击地图上的怪物卡片，通过复用统一 Modal 弹出展示该怪物的详细数值：
  - 阶位与层次（如 `一阶 · 小怪` / `一阶 · 精英` / `一阶 · 领主`）；
  - 基础气血（如 `318 HP`）、法力、物理/法术攻击、护甲、抗性；
  - 携带的被动与主动法术技能；
  - 概率掉落物与数量。

### 5.3 历练交互流程闭环
1. **点击历练行动**：点击【药谷后山历练】，弹出行动配置弹窗（`ActionModal`）；
2. **选择挂机轮次**：可设置挑战 1 次、10 次、100 次或点击 `∞` 无限历练；
3. **确认进入战斗**：点击【开始历练】，系统立即向队列首位插入该历练任务，并**平滑自动跳转至「详情」战斗界面（`CombatArena`）**，实时推进寻怪与波次战斗。

---

## 6. 实施落地步骤规划

1. **配置层数据结构升级**：
   - 在 `src/Domain/GameData/` 中扩展 `WorldNodeDefinition`，补齐 `MinBudget`、`MaxBudget`、`EliteInterval`、`BossInterval`、`ElitePool`、`BossPool`；
   - 在 `MonsterConfig` 中增加 `BudgetCost` 字段；
   - 更新 `config/game-data/maps.json` 与 `monsters.json`。
2. **领域层怪群模拟器与生成器升级**：
   - 在 `src/Domain/Simulation/CombatGroupSimulator.cs` 中实现上述 `CreateEnemyGroup` 质量预算扣减算法。
3. **协议层与服务端映射更新**：
   - 在 `src/Protocol/` 中扩充地点节点 DTO，暴露 `minBudget`、`maxBudget`、`monsters` 列表（含名称、预算、掉落、立绘）、`npcs` 列表；
   - 更新 `src/Server/Runtime/StateMapper.cs`。
4. **前端地图与附近界面渲染升级**：
   - 在 `frontend/src/components/NearbyPanel.tsx` 中渲染 NPC 卡片、怪物卡片池（含点击查看怪物数值详情）；
   - 对齐公共组件文档规范。
5. **门禁与测试**：
   - 编写确定性怪群预算抽取单测；
   - 运行 `scripts/verify.sh` 确保 100% 通过。
