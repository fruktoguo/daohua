# 文档与实现一致性审计 · 2026-09-19

> 任务：TASK-20260919-02。基于当前工作区实况（含未提交的建筑系统、行动统计三账本、防多开与边界驱动调度改动）。
> 每条含【文档主张】【实现证据】【类型】【严重度】。类型：文档超前=文档描述了未实现的能力；文档滞后=实现已变文档未更新；描述错误=文档与实现直接矛盾；文档缺失=实现已落地但无权威设计真源。

## 一、持久化基线与运维

1. **schema 版本口径全线错误**：代码为 v3（`PostgresSchema.cs:7`、`SectionCodec.cs:18`、`PlayerSection.cs:54,74`、`PostgresSchema.cs:332` 写入 `(3, now())`），文档仍写 v2——`commercial-persistence-architecture.md:5,47,172,608,615`、`domain-data-architecture.md:191`、`player-data-migration-and-reconciliation.md:3`、`deployment.md:3,51`、`README.md:70`。0916 审计整改摘要称「schema v3 口径统一」未落地。【描述错误】【高】

2. **`game.node_buildings` 表未登记、无迁移脚本**：`PostgresSchema.cs:128-136` 新建该表，但 `commercial-persistence-architecture.md` 表清单无此表，`migrations/README.md` 未列对应脚本，schema 版本未随新表推进。【文档滞后】【中】

3. **`deployment.md:51` 迁移口径滞后**：只提 `0001_v1_to_v2.sql` 与「v2 为市场订单增加到期时间」，未反映 v3（compute_jobs 租约字段）与 `0002_v2_to_v3.sql`。【文档滞后】【中】

4. **README 目录端点枚举缺 `/api/v1/catalog/buildings`**：`Program.cs:236` 已注册，`README.md:87` 未列。【文档滞后】【中】

5. **ComputeNode 令牌鉴权描述与实现相反**：`README.md:97` 与 `src/ComputeNode/Program.cs:3` 均称「只接受/仅凭 X-Compute-Token」，实际 `TokenValidator.cs:23-27` 兼容 `Authorization: Bearer` 回退。0916 审计 #93 残留。【描述错误】【中】

6. **AGENTS.md 未登记防多开子系统**：`docs/backend/anti-multi-open.md` 已存在并被索引，但 AGENTS §2 目录边界与 §3 架构未提 `src/Server/Clean/AntiMultiOpen/`、`frontend/src/antiMulti/`。【文档滞后】【低】

## 二、设计真源缺口与滞后

7. **建筑系统无权威设计文档**：实现已全量落地（`src/Domain/World/NodeBuildings.cs`、`NodeBuildingRegistry.cs`、`building.build/upgrade/contribute`、`node_buildings` 表、双进度条、`buildableTemplate/buildableList`），但 `总纲.md` 中「建筑」0 命中，`通用地图与遇怪系统.md` 仅把 `buildings` 当场景陈设。【文档缺失】【高】

8. **行动统计三账本无设计文档**：三账本（行动实例/离线会话/每日自然日）仅见于任务文档与 `common-components.md` §24，无 `design/` 权威规约。【文档缺失】【中】

9. **`通用地图与遇怪系统.md` §6 实施步骤引用不存在的文件/类型**：`WorldNodeDefinition`、`MonsterConfig`、`CombatGroupSimulator.cs`、`StateMapper.cs`、`maps.json`、`monsters.json` 全仓 0 命中。【描述错误】【中】

10. **`通用地图与遇怪系统.md` §5.1 面板布局与实现不符**：文档为单一「附近」面板；实现已拆为地图/怪物/人物/建筑四 Tab（`App.tsx:1800-1804`）。§5.2 `MonsterInfoModal` 组件不存在。【文档滞后】【中】

11. **`总纲.html` 第十一篇仍是已废弃 ECS 架构**：`总纲.html:1447-1457` 仍写 `PlayerComponent`/`YuoEntity`/`PlayerShard-N` Fiber；同源 `总纲.md:372` 已改为 `PlayerActor` + `SemaphoreSlim` 聚合单写者。HTML 内嵌独立 markdown 副本未同步。【文档滞后】【中】

12. **`domain-data-architecture.md:44` 引用不存在的 `CombatAbilityDefinition`**：全仓无此类型。0916 审计 #29 声称已修但未修。【描述错误】【中】

13. **逻辑开发指南命令表缺 `building.*`**：`logic-development-guide.md:88-101` 无 `building.build/upgrade/contribute`，实现见 `PlayerRuntime.cs:862-955`。【文档滞后】【中】

14. **`game-data.md` 目录清单缺 `buildings/`**：`config/game-data/buildings/buildings.json` 实际存在；同文启动校验强度清单未随 `DataTool/Program.cs:249-268` 新增的建筑校验更新。【文档滞后】【中】

15. **`domain-data-architecture.md` World 分段/目录清单滞后**：`:165-166` 未列 `World/NodeBuildings.cs`；`:52` `World` 分段字段未含建筑投影三字段。【文档滞后】【低】

## 三、前端 i18n

16. **i18n 死键 15 个**（排除 `game.action.*`/`game.item.*`/`insight.*`/`market.*` 等动态键族后经脚本核对）：`app.avatar.femaleName/femaleDesc/maleName/maleDesc`、`combat.arena.unknownEnemy`、`common.repeatsOnce`、`combat.nearby.monstersHeading/budgetCost/buildingsHeading/noBuildings`（旧），以及本次新增未接线的 `combat.nearby.buildingOwned/buildingDetailAria/buildingLevelLabel/buildingMaterialsHeading/contributeEmpty`。【代码/词典不一致】【中】

## 四、正向确认（非问题）

- `战斗设计.md` §7.4 钩子管线与 `CombatHooks.cs`（`ICombatResolveMutator`/`ICombatDamageMutator`/`ICombatAftermathHook`/`CombatHookOrder`/`NextRoll`）逐项吻合。
- 防多开关闭码 4005-4008 服务端 `AntiMultiOpenConstants.cs` 与前端 `codes.ts` 一致。
- `common-components.md` §24 已登记 `StatsPanel` 三小节结构，与实现一致。
- 0916 审计的 `verify.sh --strict`、冒烟口令、`dev-service.sh` 卷名、`Storage__SchemaVersion` 死配置、PG 角色清单、LoadTests 四场景、ComputeNode 派发链均已落地。

## 统计

| 严重度 | 条数 |
| --- | --- |
| 高 | 2 |
| 中 | 12 |
| 低 | 2 |
| **合计** | **16** |

> 本报告结论已由 TASK-20260919-02 全量整改。
