# 游戏数据配置与数值规约架构

## 1. 目录结构与权威源

正式游戏数据位于 `config/game-data`，运行时以 JSON 为唯一权威源（Single Source of Truth）：

- **`expected-attributes.json`**：**全境界阶位数值底座、标准期望面板、特殊属性与战斗模拟产出基准**。
- `manifest.json`：结构版本和内容版本。
- `items/`：物品、消耗品、丹药、秘籍与残卷（`materials.json`, `pills.json`, `manuals.json`）。
- `equipment/`：武器、防具与饰品（`weapons.json`, `armors.json`, `accessories.json`）。
- `abilities/`：物理武技与术法神通（`physical_abilities.json`, `spell_abilities.json`）。
- `monsters/`：各阶位普通妖兽、精英与首领（`tier1_minions.json`, `tier1_elites_bosses.json`, `tier2_minions.json`, `tier2_elites_bosses.json`）。
- `encounters/`：各区域遭遇生态与怪物波次（`tier1_encounters.json`, `tier2_encounters.json`）。
- `maps/`：世界大地图与节点网络（`qingyun_mountain.json`, `xingyun_realm.json`）。
- `actions/`：玩家修炼、采集、精炼、炼丹、锻造与历练行动（`cultivation.json`, `gathering.json`, `crafting.json`, `alchemy.json`, `forging.json`, `combat.json`, `world.json`）。

服务启动时经 `GameDataCatalog.LoadEmbedded` 一次性加载内嵌资源中的全部文件并构建不可变目录快照，结算不读取磁盘。当前校验强度分三类如实记录：

- **阻止启动**：不兼容的结构版本、必需字段缺失或非法（如空 id、空怪物池、非正耗时/预算）、无效的 `type`/`kind`/`per` 枚举；
- **静默处理（不报错）**：重复 ID 后者覆盖前者、地图邻接表追加不去重；
- **尚未校验**：`dropChance`/`minQuantity`/`maxQuantity` 无范围校验，`eliteEnemyIds`/`bossEnemyIds`/`availableActionIds`/`encounterIds` 未做跨文件引用校验。配表评审须人工核对这三类风险。

---

## 2. 顶层设计与数值规约文档拓扑

```mermaid
flowchart TD
    Config["单点真实数据源 (Single Source of Truth)<br>config/game-data/expected-attributes.json"]
    
    Doc1["【玩家与装备模型】<br>player-expected-attributes-model.md<br>(12阶位+8品质 / 4大乘区 / 基础池与最终期望)"]
    Doc2["【战斗与产出基准】<br>combat-benchmark-model.md<br>(关卡怪物标准 / 动态抗性 / 二次曲线 / 单人与3人队效率比)"]
    
    Domain["领域模型与确定性模拟<br>src/Domain"]
    Server["服务启动装配与聚合运行时<br>src/Server"]
    
    Config --> Doc1 & Doc2
    Config --> Domain & Server
```

- **玩家期望属性与装备预算模型**：详见 [`player-expected-attributes-model.md`](../design/player-expected-attributes-model.md)；
- **战斗基准模拟与产出期望模型**：详见 [`combat-benchmark-model.md`](../design/combat-benchmark-model.md)；
- **前端公共组件与模板规范**：详见 [`common-components.md`](../frontend/common-components.md)。

---

## 3. 战斗节奏与属性规则

当前标准战斗节奏为：
- `searchMilliseconds`：遭遇搜寻阶段耗时，默认 5000ms（5.0 秒），字段原样消费；
- `respawnMilliseconds`：波次间刷怪间隔，默认 5000ms（5.0 秒），字段原样消费；寻敌速度成长与阶位曲线属于数值模型规划项（见 `combat-benchmark-model.md`），落地后以该文档口径为准，当前结算按配置原值执行；
- `playerRespawnMilliseconds`：玩家死亡后的复活时间，默认 180000ms（180 秒）。玩家死亡不会完成当前波次，只有当前波怪物全灭才计入一次战斗通关。

技能灵力消耗以 `manaCost` 字段直接点数声明（如 `manaCost: 10` 即消耗 10 点灵力），不再使用比例换算字段。

配表全面采用**极简 JSON 缺省风格**：未启用的特殊属性或未调整的数值字段无需显式写 `0`，由 `GameDataCatalog` 解析器统一赋予确定性的默认值（如 `0.0` 或基础倍率 `1.0`）。服务端不会把缺省或零值解释成隐藏效果，客户端也不会显示零值特殊属性。

---

## 4. 行动收益声明（`yields`）

`actions/` 下的行动通过 `yields` 数组逐项声明产出收益。**收益类型与行动类别（`type`）完全解耦**：修炼行动可以产出灵力或 Buff，采集行动也可以产出修为或经验；同一行动允许声明多条收益。旧字段 `qiPerSecond`、`experiencePerRepeat`、`outputs` 已废弃，不存在类别默认收益。

```json
"yields": [
  { "kind": "qi", "per": "second", "amount": 1 },
  { "kind": "mana", "per": "repeat", "amount": 1 },
  { "kind": "experience", "per": "repeat", "amount": 10 },
  { "kind": "item", "per": "repeat", "id": "item.ningxue-grass", "amount": 3 },
  { "kind": "buff", "per": "repeat", "id": "buff.x", "name": "凝神", "amount": 1,
    "durationSeconds": 60, "modifiers": { "hpRegen": 2 } }
]
```

- `kind`：`qi`（修为）｜`mana`（灵力，入账 `Combat.Manapoints`）｜`hitpoints`（气血）｜`experience`（历练经验）｜`item`（物品）｜`buff`（增益授权）。
- `per`：`second`（按推进整秒逐秒结算，离线与在线位级一致）｜`repeat`（单轮完成时结算）。`item` 与 `buff` 仅允许 `repeat`。
- `amount`：正有限数；`buff` 的 `amount` 表示叠加层数。
- `item` 需携带合法 `id`；`buff` 需携带 `id`、`name`、`durationSeconds`（>0）与可选 `modifiers`（`hpRegen`/`mpRegen`/`attackSpeedRatio`/`damageAbsorptionRatio`）。资源类收益禁止携带实体标识字段。
- 灵力与气血收益按当前属性上限截断；持续消耗品倍率作用于数值型收益，Buff 授权不受影响。
- 输入材料仍由 `inputs` 声明，在单轮完成时扣除。

---

## 5. 版本与发布

修改任何游戏数据时必须同步提升 `manifest.json` 的 `contentVersion`，并运行：

```bash
dotnet run --project src/DataTool/DataTool.csproj -- \
  validate config/game-data
scripts/verify.sh
```

`/healthz`/`/readyz` 只返回存活与依赖就绪状态，不携带配置指纹；内容版本与游戏数据指纹由 `/api/v1/meta` 及各 `/api/v1/catalog/*` 端点的 `ETag` 提供。发布系统应把镜像版本、Git revision、游戏数据版本和配置指纹作为同一批次审计。

---

## 6. 角色表现

角色与怪物使用统一的表现描述：

```json
{
  "renderer": "text",
  "source": "修",
  "poster": null
}
```

- `text`：直接显示文本。
- `image`：`source` 为图片 URL 或同源静态资源路径。
- `live2d`、`model3d`：为后续渲染器保留；当前客户端优先显示 `poster`，没有海报时回退到文本。

协议只传渲染器类型与资源地址，不把 Live2D 或 3D 引擎对象写入领域模型。
