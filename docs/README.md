# 道化太初 · 文档导航

本目录是《道化太初》全部项目文档的唯一入口，按职能分为四类。新增或移动文档时必须同步更新本索引。

## 目录结构

```text
docs/
├── design/    游戏玩法与数值设计（玩法规则、战斗模型、期望面板、技能规划）
├── backend/   服务端开发与架构规范（数据架构、YuoCore 逻辑开发指南、配置管线、性能基线）
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


## design/ — 游戏玩法与数值设计

| 文档 | 定位 |
| --- | --- |
| [character-core-design.md](design/character-core-design.md) | **角色核心设计文档 (Character Core Design)**：统一常驻修仙者实体哲学、多维生命体征、跨行为状态连续穿透、Buff/Debuff 统一时间轴衰减、1 HP 濒死复苏、动作互锁与服务端 ECS 架构映射。 |
| [master-design.md](design/master-design.md) | **全景主设计总纲 (Master GDD)**：全域最高权威设计总纲，融汇数值底座、装备技能、持续消耗品、战斗引擎、大世界生态、世界观剧情、UI交互系统、服务端ECS架构与运维容量。 |
| [master-design.html](design/master-design.html) | **全景主设计总纲·单文件修仙沉浸查看器**：独立离线单文件网页，无需开发服务，双击即开，内置暗墨玄石古典美学、大纲导航、实时搜索、数学公式与动态 MD 载入。 |
| [initial-design.md](design/initial-design.md) | 系统设计总纲：商业化界面与数值规则、行动队列、复活与战斗边界等核心设计约定。 |
| [combat-design.md](design/combat-design.md) | 战斗数值与内视设计规约：`double` 数值类型架构、六维根基换算、抗性减伤与对抗公式。 |
| [player-expected-attributes-model.md](design/player-expected-attributes-model.md) | 境界阶位体系与基准玩家期望属性量化模型（由脚本生成，勿手改数值表）。 |
| [combat-benchmark-model.md](design/combat-benchmark-model.md) | 战斗基准模拟与关卡产出期望模型：全 12 阶单人与组队效率基准（由脚本生成，勿手改数值表）。 |

生成链路：`scripts/generate-combat-benchmark.py` 动态刷新 `player-expected-attributes-model.md`、`combat-benchmark-model.md` 与 `config/game-data/expected-attributes.json`；修改数值模型应改脚本后重新生成，不直接编辑生成产物中的数值表。

## backend/ — 服务端开发与架构规范

| 文档 | 定位 |
| --- | --- |
| [domain-data-architecture.md](backend/domain-data-architecture.md) | 服务端领域数据架构与 YuoCore ECS 规约：四层架构体系、细粒度组件与强类型 ID。 |
| [logic-development-guide.md](backend/logic-development-guide.md) | 服务端业务逻辑开发指南：基于 YuoCore ECS 的 Component、System、ActorHandler 开发 SOP 与红线。 |
| [game-data.md](backend/game-data.md) | `config/game-data` 配置架构：权威源、文档拓扑、版本与发布校验。 |
| [performance-baseline.md](backend/performance-baseline.md) | 首轮性能基线：在线、离线挂机、市场场景实测数据与相对回归口径。 |

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
| [maps.md](world/maps.md) | 云脊山脉地图节点设定与剧情职能，对齐 `WorldCatalog` 现有节点。 |

## 维护约定

- 新增文档必须归入上述四类之一，并在本索引登记；跨类主题优先拆分归属，不建混合目录。
- 文档间引用一律使用相对路径，不使用 `file:///` 绝对路径。
- 工程协作规则见仓库根目录 `AGENTS.md`；项目总览见根目录 `README.md`。
