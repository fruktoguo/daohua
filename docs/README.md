# 道化太初 · 文档导航

本目录是《道化太初》全部项目文档的唯一入口，按职能分为四类。新增或移动文档时必须同步更新本索引。

## 目录结构

```text
docs/
├── design/    游戏与数值设计（玩法、战斗、数值模型、数据架构、性能基线）
├── frontend/  前端规范（公共组件速查、专项交互设计）
├── ops/       部署与运维（构建、CCR 发布、服务器更新与回滚）
├── world/     世界观与剧情（设定真源、章节剧情、地图设计）
├── tasks/     任务详情（各任务的独立需求分析、设计方案、子任务与时间日志）
└── tasks.md   任务面板与开发进度看板（总览索引、状态看板与路线图）
```

## 任务面板

| 文档 | 定位 |
| --- | --- |
| [tasks.md](tasks.md) | **任务面板与开发进度看板**：项目全局任务总览看板、状态流转索引与开发路线图。 |
| [tasks/](tasks/) | **任务独立详情文档目录**：每个非 Bug 修复任务的独立需求分析、技术方案、子任务拆解与时间线日志。 |


## design/ — 游戏与数值设计

| 文档 | 定位 |
| --- | --- |
| [initial-design.md](design/initial-design.md) | 系统设计总纲：商业化界面与数值规则、行动队列、复活与战斗边界等核心设计约定。 |
| [combat-design.md](design/combat-design.md) | 战斗数值与内视设计规约：`double` 数值类型架构、六维根基换算、抗性减伤与对抗公式。 |
| [player-expected-attributes-model.md](design/player-expected-attributes-model.md) | 境界阶位体系与基准玩家期望属性量化模型（由脚本生成，勿手改数值表）。 |
| [combat-benchmark-model.md](design/combat-benchmark-model.md) | 战斗基准模拟与关卡产出期望模型：全 12 阶单人与组队效率基准（由脚本生成，勿手改数值表）。 |
| [early-game-skills-design.md](design/early-game-skills-design.md) | 前期技能设计规约：五大流派定位、前期技能清单规划、数值模型与获取途径。 |
| [game-data.md](design/game-data.md) | `config/game-data` 配置架构：权威源、文档拓扑、版本与发布校验。 |
| [performance-baseline.md](design/performance-baseline.md) | 首轮性能基线：在线、离线挂机、市场场景实测数据与相对回归口径。 |
| [milky-way-idle-mapping.md](design/milky-way-idle-mapping.md) | Milky Way Idle 公开玩法/数值映射与排除边界（仅作参考，不复制其内容）。 |

生成链路：`scripts/generate-combat-benchmark.py` 动态刷新 `player-expected-attributes-model.md`、`combat-benchmark-model.md` 与 `config/game-data/expected-attributes.json`；修改数值模型应改脚本后重新生成，不直接编辑生成产物中的数值表。

## frontend/ — 前端规范

| 文档 | 定位 |
| --- | --- |
| [common-components.md](frontend/common-components.md) | 前端公共组件与统一模板速查手册。**任何前端开发前必须先查阅**；收拢新的统一模板必须登记到该手册。 |
| [floating-popover.md](frontend/floating-popover.md) | 悬浮提示、信息卡片与浮层菜单专项设计规范。 |

## ops/ — 部署与运维

| 文档 | 定位 |
| --- | --- |
| [deployment.md](ops/deployment.md) | 生产构建与部署：目标架构、CCR 不可变标签、服务器自动更新、持久卷、备份与回滚。 |

## world/ — 世界观与剧情

| 文档 | 定位 |
| --- | --- |
| [README.md](world/README.md) | 剧情与设定总览：定位、创作原则与叙事红线（世界与故事真源入口）。 |
| [lore.md](world/lore.md) | 世界观设定：太初之世的时代背景、势力与炁序（区分对外口径与内部设定）。 |
| [story.md](world/story.md) | 七章主线剧情设计。 |
| [maps.md](world/maps.md) | 云脊山脉地图节点设定与剧情职能，对齐 `DaoHuaWorldCatalog` 现有节点。 |

## 维护约定

- 新增文档必须归入上述四类之一，并在本索引登记；跨类主题优先拆分归属，不建混合目录。
- 文档间引用一律使用相对路径，不使用 `file:///` 绝对路径。
- 工程协作规则见仓库根目录 `AGENTS.md`；项目总览见根目录 `README.md`。
