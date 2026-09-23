# 道化太初 · 文档导航

本目录是《道化太初》全部项目文档的唯一入口，按职能分为四类。新增或移动文档时必须同步更新本索引。

## 目录结构

```text
docs/
├── design/    游戏玩法与数值设计（玩法规则、战斗模型、期望面板、技能规划）
├── backend/   服务端开发与架构规范（数据架构、逻辑开发指南、配置管线、性能基线）
├── frontend/  前端规范（公共组件速查、专项交互设计）
├── ops/       部署与运维（构建、CCR 发布、服务器更新与回滚）
├── world/     世界观与剧情（设定真源、章节剧情、地图设计）
├── future/    未来运营参考（非现行契约的长线规划文档）
├── audits/    一致性审计与深度风险分析报告（只读证据库，非设计真源）
├── archive/   历史归档（废弃设计稿、推演草案、时点性分析快照、排查日志）
├── 临时设计讨论/  临时草稿、脑暴记录与设计推演（非正式真源，定稿后沉淀或归档）
├── tasks/     任务详情（各任务的独立需求分析、设计方案、子任务与时间日志）
└── tasks.md   任务面板与开发进度看板（总览索引、状态看板与路线图）
```

> 另：本目录 [`DESIGN.md`](DESIGN.md) 为**设计理念总集**——各子系统「为什么这样设计」的意图索引层，浓缩顶层哲学与红线并链接回本目录各专题真源，不复制数值与规则细节。

## 任务面板

| 文档 | 定位 |
| --- | --- |
| [tasks.md](tasks.md) | **任务面板与开发进度看板**：项目全局任务总览看板、状态流转索引与开发路线图。 |
| [tasks/](tasks/) | **任务独立详情文档目录**：每个非 Bug 修复任务的独立需求分析、技术方案、子任务拆解与时间线日志。 |


## design/ — 游戏玩法与数值设计

| 文档 | 定位 |
| --- | --- |
| [总纲/](design/总纲/) | **设计总纲 (Master GDD)**：全域最高权威设计规范，17 篇分卷（篇目目录见文件夹 `README.md`）——核心哲学、角色常驻实体、境界数值底座、装备/技能/消耗品、战斗数值与内视、大世界地图遇怪、节点建筑、怪物产出基准、数值量化模型、世界观剧情、UI 交互、传音日志、服务端架构、运维容量、行动统计；原各专题设计文档已全部收编为对应篇正文，`design/总纲.md` 为指引 stub。 |
| [总纲/index.html](design/总纲/index.html) | **总纲·修仙沉浸查看器**：自动读取同文件夹全部篇目（`manifest.json` + `NN-*.md`）拼接渲染为整页，内置暗墨玄石古典美学、大纲导航、实时搜索、数学公式与篇目内页内跳转；HTTP 访问永远读最新，file:// 直开回退内嵌快照（`node scripts/sync-master-viewer.mjs` 同步）并可手动读取文件夹。 |
| [战斗演武画廊/](design/战斗演武画廊/index.html) | **战斗演武画廊**：与正式界面同构的演武沙盘，直链 frontend 真实样式与资源；特效按真实技能 ID 拆分于 `vfx/abilities/`，结算走配表 `castMilliseconds`/`targetType`/`manaCost`，支持单技能点测、全量轮播、敌方演示与 `?selftest=1` 自检。 |

生成链路说明：原 `scripts/generate-combat-benchmark.py` 生成脚本已移除；`总纲/11-数值量化模型集.md`（由原 `player-expected-attributes-model.md` 与 `combat-benchmark-model.md` 合并）与 `config/game-data/expected-attributes.json` 中的数值表为历史生成产物快照，后续修改数值模型时按设计评审口径手工同步维护，不再经脚本重新生成。

## backend/ — 服务端开发与架构规范

| 文档 | 定位 |
| --- | --- |
| [commercial-persistence-architecture.md](backend/commercial-persistence-architecture.md) | **商业级持久化最终架构（清零历史包袱）与 Redis 选型决策**：共享表与 section 快照、资产/市场事务、latest-only 队列、容量/恢复边界，以及 Redis 的不引入条件。该文档是持久化目标方案唯一权威。 |
| [domain-data-architecture.md](backend/domain-data-architecture.md) | 服务端领域数据架构规约：`PlayerAggregate` 聚合 + `PlayerSectionKind` 八分段、`MarkDirty` 脏标记、`SectionCodec` 编解码与强类型 ID。 |
| [logic-development-guide.md](backend/logic-development-guide.md) | 服务端业务逻辑开发指南：基于 `PlayerAggregate` + `PlayerActor` 单写者的命令路由、`SimulationEngine` 推进与 `MarkDirty` 分段标记 SOP 与红线。 |
| [game-data.md](backend/game-data.md) | `config/game-data` 配置架构：权威源、文档拓扑、版本与发布校验。 |
| [performance-baseline.md](backend/performance-baseline.md) | 性能基线现行入口：当前支持的重测场景与参数口径；2026-08-06 历史样本已归档至 `archive/perf-baseline-20260806.md`。 |
| [anti-multi-open.md](backend/anti-multi-open.md) | 防多开模块设计规约：标签页互斥 / 单账号互踢 / 设备指纹配额三道独立开关防线、准入裁决顺序、顶替会话安全拆除链与协议关闭码约定。 |

## frontend/ — 前端规范

| 文档 | 定位 |
| --- | --- |
| [common-components.md](frontend/common-components.md) | 前端公共组件与统一模板速查手册。**任何前端开发前必须先查阅**；收拢新的统一模板必须登记到该手册。 |
| [floating-popover.md](frontend/floating-popover.md) | 悬浮提示、信息卡片与浮层菜单专项设计规范。 |

## ops/ — 部署与运维

| 文档 | 定位 |
| --- | --- |
| [deployment.md](ops/deployment.md) | 生产构建与部署：目标架构、CCR 不可变标签、服务器自动更新、持久卷、备份与回滚。 |

## future/ — 未来运营参考

| 文档 | 定位 |
| --- | --- |
| [player-data-migration-and-reconciliation.md](future/player-data-migration-and-reconciliation.md) | 玩家存档版本迁移与容错自愈架构规约：链式数据迁移管道（商业化长线规划）与 `PlayerStateReconciler` 运行时容错自愈的双重防线体系。**未来运营参考，非现行契约**。 |

## audits/ — 一致性审计与风险分析（只读证据库）

| 文档 | 定位 |
| --- | --- |
| [deep-risk-analysis-20260919.md](audits/deep-risk-analysis-20260919.md) | **最新**：六路并行深度风险分析总报告（高10/中19/低23，含证据行号与处置优先级）。 |
| [doc-vs-implementation-20260919.md](audits/doc-vs-implementation-20260919.md) | 文档与实现一致性审计（2026-09-19，现行口径）。 |
| [model-fork-audit-20260923.md](audits/model-fork-audit-20260923.md) | 普攻/吟唱统一时间线模型分叉审计（2026-09-23）。 |

> 已结题的 0914 / 0914-recheck / 0915 / 0916 四轮一致性审计归档于 [`archive/audits/`](archive/audits/)。

## archive/ — 历史归档

| 文档 | 定位 |
| --- | --- |
| [analysis/](archive/analysis/) | 时点性项目分析快照：`project-analysis-20260828.md`、`project-analysis-20260910.md`、`project-analysis-20260915.md`，供追溯演进脉络。 |
| [design/](archive/design/) | 废弃/被取代的设计稿：`initial-design.md`（v0.1 历史底稿，内容已由 总纲 吸收）、`ecs-architecture-and-evolution-plan.md`（ECS/Fiber 推演稿，已被聚合模型取代）、`early-game-skills-design.md`（废弃 stub，真源为 `config/game-data/abilities/`）、`milky-way-idle-mapping.md`（外部借称清理记录，已废除）、`battle-scene-demo.html`（早期战斗场景静态演示，已被 战斗演武画廊 演武沙盘取代）。 |
| [discussions/](archive/discussions/) | 已沉淀或废弃的临时设计草案：`任务队列系统与组件设计草案.md`、`全量物品重构与T1-T2设计草案.md`。 |
| [audits/](archive/audits/) | 已结题的文档与实现一致性审计旧轮次：`doc-vs-implementation-20260914.md`、`doc-vs-implementation-20260914-recheck.md`、`doc-vs-implementation-20260915.md`、`doc-vs-implementation-20260916.md`，时点性证据仅供追溯。 |
| [perf-baseline-20260806.md](archive/perf-baseline-20260806.md) | 首轮性能基线历史样本：已删除场景（online/idle-offline 等）与已删除端点的原始数据，仅供追溯。 |
| [BUGLOOP-20260829.md](archive/BUGLOOP-20260829.md) | 排查循环日志（历史调试记录）。 |

## 临时设计讨论/ — 草案与推演记录

| 文档 | 定位 |
| --- | --- |
| [临时设计讨论/](临时设计讨论/) | 开发讨论中的临时草稿、脑暴记录与设计推演；定稿后沉淀至 design/backend/frontend，废弃后移入 `archive/discussions/`。当前仅含目录 README。 |

## world/ — 世界观与剧情

| 文档 | 定位 |
| --- | --- |
| [README.md](world/README.md) | 剧情与设定总览：定位、创作原则与叙事红线（世界与故事真源入口）。 |
| [lore.md](world/lore.md) | 世界观设定：太初之世的时代背景、势力与炁序（区分对外口径与内部设定）。 |
| [story.md](world/story.md) | 七章主线剧情设计。 |
| [maps.md](world/maps.md) | 青云仙山与星陨异境两张地图的节点设定与剧情职能，对齐 `GameDataCatalog` 消费的 `config/game-data/maps/` 配表节点。 |

## 维护约定

- 新增文档必须归入上述目录之一，并在本索引登记；跨类主题优先拆分归属，不建混合目录。
- 文档间引用一律使用相对路径，不使用 `file:///` 绝对路径。
- 工程协作规则见仓库根目录 `AGENTS.md`；项目总览见根目录 `README.md`。
