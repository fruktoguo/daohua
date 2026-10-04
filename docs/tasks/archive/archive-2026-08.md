# 任务归档 · 2026-08（2026-08-22 ~ 2026-08-28）

> 本文件由 2026-10-04 任务面板深度归档（TASK-20261004-06）生成：`docs/tasks/` 下 2026-10-01 及更早的任务详情文档已提炼合并至本档并删除原文件，完整原文可从 Git 历史找回。
> 本档共 73 项任务；条目按提出时间从新到旧排列。检索方式：按 `TASK-YYYYMMDD-XX` 编号全文搜索定位。

---

### TASK-20260828-08 全项目潜在问题系统性分析文档
- 分类：项目分析 / 文档编纂（非 Bug 修复、非功能开发）
- 时间：提出 2026-08-28 21:08:19｜完成 2026-08-28 21:30:00
- 状态：✅ 已完成
- 摘要：采用 4 路并行 Explore 代理对服务端宿主（`src/Server`/`src/ComputeNode`/`src/LoadTests`）、领域与协议（`src/Domain`/`src/Protocol`）、前端（`frontend`）、基础设施（Dockerfile/compose/Caddy/scripts/deploy/数据库 schema/备份链路）逐文件扫描。产出分析文档 `docs/archive/analysis/project-analysis-20260828.md`，汇总高严重度 11 项、中严重度 25 项、低严重度 30+ 项，另附「核实无问题」清单。纯文档产出，按 Git 规则不自动提交。

### TASK-20260828-07 物品装备Hover详细信息展示与通用Buff附属卡片窗口体系
- 分类：前端重构 / UI设计系统 / 悬浮气泡体系 / 物品装备 / Buff系统
- 时间：提出 2026-08-28 18:16:10｜完成 2026-08-28 18:20:15
- 状态：✅ 已完成
- 摘要：定义 `FloatingPopover` 附属卡片（Aside Card）体系，主卡片与 Buff/特殊机制副窗口解耦：丹药主卡片展示即时恢复/冷却，战斗增益提取为独立 Buff 附属卡片；装备主卡片完整展示 `baseStatRatios`/`flatStats`/`enhancementBonus`/攻速，特殊机制（`specialMechanics`）自动挂载词缀卡片。改造 `frontend/src/components/ItemDisplay.tsx`（`describeItemEffects`/`describeEquipmentEffects` 分离、新增 `resolveItemAsideCards`/`resolveEquipmentAsideCards`、`enhancementLevel` 强化加成），`EquipmentPanel.tsx` 透传 `CombatEquipmentDefinition`，补 `market.aside.pillBuff` 等 i18n 词条，更新 `docs/frontend/common-components.md`。verify.sh / npm build 通过。

### TASK-20260828-06 全方面增强当前系统的健壮性和稳定性
- 分类：（原文未标注）
- 时间：提出 2026-08-28 15:09:23｜完成 2026-08-28 15:26:00
- 状态：✅ 已完成
- 摘要：全方面健壮性/稳定性增强，覆盖领域模拟与经济/市场不变量、服务端运行时/持久化/WebSocket/健康检查边界、前端网络生命周期与快照解析错误恢复；遵循内测基线不保留历史兼容层、不修改 YuoCore submodule。验证：Release 构建 0 警告 0 错误，领域测试 30/30、服务端测试 13/13 通过，前端生产构建与 `bash -n` 脚本语法检查通过。

### TASK-20260828-05 行动卡片属性悬浮提示修复、等级升序排序与丹药品质色彩校准
- 分类：前端体验 / 气泡悬浮系统 / 行动卡片排序 / 品质色彩校准 / 装备与丹药属性
- 时间：提出 2026-08-28 12:54:15｜完成 2026-08-28 12:58:00
- 状态：✅ 已完成
- 摘要：修复三大问题：① `ItemTooltipTarget` 因 `display: contents` 致 `FloatingPopover` 触发器尺寸为 0 气泡不显示（App.tsx 加 `action-card-tooltip-wrapper`）；② 装备产出物未关联 `CombatEquipmentDefinition`、`resolveItemMetadata` 无装备 ID 兜底；③ 丹药增益倍率（`cultivationMultiplier`/`gatheringMultiplier`/`alchemyMultiplier`/`durationSeconds`）未在 `describeItemEffects` 解析。行动卡片 `filteredActions` 按真实等级升序严格排序；校准 `ITEM_META_CORES` 品质映射修复丹药全蓝：一阶凡品 `mundane`、二阶黄阶 `yellow`、三阶玄阶 `mystic`、四阶地阶 `earth`、五阶天阶 `heaven`。verify.sh 通过。

### TASK-20260828-04 炼丹提炼与炼器精炼材料制造子项解耦及默认首Tab导航优化
- 分类：前端体验 / 技艺系统 / 选项卡导航 / 炼丹提炼 / 炼器精炼 / 交互优化
- 时间：提出 2026-08-28 12:48:00｜完成 2026-08-28 12:51:00
- 状态：✅ 已完成
- 摘要：材料制造归属解耦：炼丹命名「提炼」（Extraction，`craft.extract-*`，萃取炼丹灵药基础物），炼器命名「精炼」（Refining，`craft.refine-*`，冶炼百炼铁/桃木晶等锻造灵材）。默认首 Tab：炼丹为「提炼」（后接修炼、斗法），炼器为「精炼」（后接武器/头部/衣服/腿部/鞋履/饰品）。更新 `uiStore.ts`（`AlchemySubTab`/`ForgingSubTab` 新增 `refining`）、`app.zh.ts`（`app.crafting.forgeRefining`）、`App.tsx` 的 `ALCHEMY_SUB_TABS`/`FORGING_SUB_TABS` 与 `CraftingPage` 过滤逻辑。verify.sh 通过。

### TASK-20260828-03 交互行动卡片精简（仅显示名称+右上角LV角标）与横向尺寸紧凑化重构
- 分类：前端体验 / UI重构 / 行动卡片紧凑化 / 角标系统 / 交互精简
- 时间：提出 2026-08-28 12:29:09｜完成 2026-08-28 12:41:00
- 状态：✅ 已完成
- 摘要：`ActionCard` 移除详情/描述/属性徽章/固定图标，仅保留名称 + 复用 `item-corner item-corner--top-right` 与 `UnitNumber` 的右上角胶囊 LV 角标；品阶转等级换算（1阶→1级、2阶→18级、3阶→30级、N阶→`18 + (N - 2) * 12` 级，`resolveActionLevel`）；`.action-cards`/`.action-group-list` 改为 `repeat(4, minmax(0, 1fr))` 4 列网格（移动端 2 列）；`data-rarity` 品质驱动边框/背景微光/文本/角标色彩。涉及 `frontend/src/game/ids.ts`、`App.tsx`、`styles.css`，更新 `docs/frontend/common-components.md`。verify.sh 通过。

### TASK-20260828-02 补齐商业化生产闭环
- 分类：（原文未标注）
- 时间：提出 2026-08-28 00:59:12｜完成 2026-08-28 01:26:27（据进度日志）
- 状态：✅ 已完成
- 摘要：一次性补齐落盘数据库商业化能力：PostgreSQL WAL archive、base backup、PITR 隔离恢复、备份校验与权威流水分区归档工具并接入部署定时器；玩家运行时 PostgreSQL 租约、市场 settlement claim 幂等确认、多实例 fail-closed 边界；Compose PostgreSQL 主版本修正。验证：领域 18/18、服务端 13/13、前端构建、脚本语法通过；本地 PostgreSQL `archive_mode=on`、`archive_timeout=5min` 实际生效；容量门禁 5000 账号、30000 section、1500 latest-only queue upsert、2000 冷挂机通过；新账号登录/DHW1 WebSocket/section 落盘/重启恢复通过。提交 `8be573f`、`67298e6`、`67ac446`。

### TASK-20260828-01 调整新基线账号密码长度策略为 6-24 位
- 分类：（原文未标注）
- 时间：提出 2026-08-28 00:29:47｜完成 2026-08-28 00:42:48（据进度日志）
- 状态：✅ 已完成
- 摘要：clean-room v1 账号密码长度由 12-256 位调整为 6-24 位：服务端注册边界、前端注册表单、烟测脚本、翻译模板统一切换并补下界/上界/越界测试，不增加旧策略兼容分支；删除本地 3 个测试账号后以 `YuoHira` 重新注册。验证：Server.Tests 12/12、Domain.Tests 18/18、前端构建通过；`action.enqueue` 使 stateVersion 1→2、`game.player_action_queue` 持久化 1 条行动、7 个 section 持久化成功；服务重启后仍从 PostgreSQL 恢复 stateVersion=2 与原行动队列。

### TASK-20260827-02 清零历史实现、从零落地商业级运行时与持久化最终基线
- 分类：（原文未标注）
- 时间：提出 2026-08-27 17:21:34｜完成 2026-08-27 21:45:41
- 状态：✅ 已完成
- 摘要：clean-room 从零重写最终基线：新领域模型/新协议/新 Server 运行时/PostgreSQL schema v1/新 Worker/新测试直接落地，旧类名、旧表、旧快照载荷、旧 outbox、旧市场账本 Blob、旧 API/WebSocket 字段一概不保留，禁止双读双写、legacy DTO、fallback、migration runner，不引入 Redis。并行边界 `domain_protocol`/`server_persistence`/`ops_loadtests`。验证：Domain.Tests 18/18、Server.Tests 11/11、前端构建、`scripts/verify.sh`（5000 账号、30000 section、1500 queue upsert、2000 冷挂机容量门禁）、`scripts/smoke-production.mjs`（/healthz、/readyz、DHW1 首包）、docker buildx 双目标镜像（UID/GID 10001、只读根文件系统）全部通过；代码提交 `e53b879`。设计基线：[商业级持久化最终架构](../../backend/commercial-persistence-architecture.md)。远程生产备份恢复与 PITR 演练未在本任务执行，部署前须按 `docs/ops/deployment.md` 单独完成。

### TASK-20260827-01 清零历史包袱的商业级持久化最终架构与 Redis 选型决策文档
- 分类：（原文未标注）
- 时间：提出 2026-08-27 15:36:22｜完成 2026-08-27 21:45:41（据进度日志，clean-room 代码完成后本架构文档成为唯一持久化与 Redis 选型基线）
- 状态：✅ 已完成
- 摘要：纯文档任务：编写清零历史包袱的商业级持久化最终架构正文与 Redis ADR。核心决策：不建「每玩家一张物理表」，共享表按领域/生命周期拆分；在线状态只在内存权威聚合；模拟状态按 section 合并写回（默认不超过 15 秒一次，行动边界/登出/停机强制保存）；资产、订单、冻结、成交、账务流水在 PostgreSQL 同一事务内完成（幂等键+唯一约束硬门禁）；durable outbox 只记录跨进程副作用，不记录每帧消息/日志/心跳/战斗 Tick；基线不部署 Redis，仅在跨实例共享临时状态、连接广播或限流等明确需求且可接受 HA/持久化/监控/备份成本时单独评审。目标 2c4g/40g、5000+ 注册账号、100+ 持续连接、500+ 峰值收发、2000+ 冷挂机补算。实现门禁由 TASK-20260827-02 记录通过。

### TASK-20260825-03 战斗场景纯 HTML 实机画面 Demo
- 分类：（原文未标注）
- 时间：提出 2026-08-25 19:08:32｜完成 2026-08-26 01:20:30
- 状态：✅ 已完成
- 摘要：新增纯 HTML/CSS/原生 JavaScript 单文件战斗场景 Demo（不引入 React/Vite/第三方运行时），复刻 1568×804 战斗截图的黑金暗墨面板、顶部状态栏、左侧导航、中央战场、底部战报日志与右侧储物袋/装备/功法 Tab；复用 `frontend/public/avatars/player.svg` 与 `frontend/public/avatars/wood-spirit.webp` 真实资源（未转 base64）；后续按正式 `CombatArena` 样式与 `avatarStore` 资源惯例复修并完成单文件样式内嵌与特效预览挂载。验证：Chromium 1568×804 下 `readyState=complete`、无 `pageerror`/`console.error`；交互烟测（Tab 切换、属性弹窗、伤害/受击、重置）通过。交付物：`docs/archive/design/battle-scene-demo.html`（已归档）。

### TASK-20260825-02 全量技能与战斗特效单文件交互演武画廊开发与全特效粒子升级
- 分类：（原文未标注）
- 时间：提出 2026-08-25 00:53:46｜完成 2026-08-25 02:00:00
- 状态：✅ 已完成
- 摘要：3 组 Agent 并行为 `docs/design/战斗演武画廊.html` 实现全量技能真实粒子特效：物理武技群（重斩/连环/嘲讽/重云/贯星）、五行术法群（掌心雷/轰炎/清平乐/雷光引/破阵子）、妖兽绝技群（飞扑撕咬/魔猿开山裂地/伥虎虎煞魔吼/坠魔幽冥咒怨/坠星兽天外陨星/调息引气回元），全部具备独立弹道、刀光、雷电、火海、金盾、陨石、音波粒子；删除多余 `battle-effects-demo.html` 副本，统一收拢至唯一自包含单文件。全局门禁验证与 Git 自动提交完成。

### TASK-20260825-01 全系统代码实现与全量文档不一致性深度审计复查
- 分类：（原文未标注）
- 时间：提出 2026-08-25 00:15:00｜完成 2026-08-25 00:40:00
- 状态：✅ 已完成
- 摘要：5 维并发审计（Domain/GameData/Server/Frontend/Ops），实测 Domain.Tests 185/185、Server.Tests 23/23、前端构建通过。P0：`frontend/src/components/InsightPage.tsx:175` 法抗误读 `waterResistance`；引气入体灵力产出写死 10.0 MP（较设计放大 10 倍）。P1：正交五行抗性双层减伤文档缺失、装备 6 部位命名与权重模型脱节、T2 锻造配方与文档不符（14 件 T2 装备补 2 件对应 T1 装备前置）、ECS 执行器文档严重滞后、PostgreSQL 7 张表 DDL 与租约锁文档缺失。修复落地：法抗改 `magicResistance`；灵力产出统一 10.0 MP/10.0 Exp（引气丹 +30% 为 13.0）；删除 `src/Domain/Components/` 下 7 个休眠死组件与 `ActionTransitionSystem.cs`/`ExecutorAction.cs`/`ActionAdvanceContracts.cs`，确立 `PlayerComponent` + `PlayerState`（7 大概念切片）+ `SimulationEngine.AdvanceTo` 唯一权威真源；补齐 7 表 DDL 与 `pg_try_advisory_lock` Fail-Fast 租约规约；修正 `ACCESS_TOKEN` 强制必填与本地烟测端口 5128。终验 verify.sh：167 个有效领域测试、23 个服务端测试、配置校验、容量目标与前端构建 100% 通过。

### TASK-20260824-32 右侧多Tab栏、装备面板、功法阵列与战斗底栏UI精简与多项修复
- 分类：前端体验 / UI重构 / 交互精简 / 布局优化 / 国际化修复
- 时间：提出 2026-08-24 23:59:29｜完成 2026-08-25 00:04:00（据进度日志）
- 状态：✅ 已完成
- 摘要：`RightSidebar.tsx` 顶部 Tab 纯文字化（移除图标），右侧栏加宽至 `minmax(380px, 440px)` 容纳 5 列物品网格；`EquipmentPanel.tsx` 装备/技艺槽统一 64px×64px，空态仅显示部位/技艺名称（去「+」号与水印）；`AbilitiesPanel.tsx` 第一排内功、第二排术法 `flex-wrap` 折行、已学功法提示精简；战斗底栏 `combat-slot-tray` 改 `auto auto 1fr` 一排装下 4 门功法；补 `app.combatPanel.chooseSpell`/`chooseInternal` i18n。verify.sh 全量通过。

### TASK-20260824-31 物品格子与网格边距紧凑化优化（缩减 Item 间隙与槽位空隙）
- 分类：前端体验 / UI 规范 / 物品格子 / 网格间距优化 / 交互微调
- 时间：提出 2026-08-24 23:20:38｜完成 2026-08-24 23:26:30（据进度日志）
- 状态：✅ 已完成
- 摘要：根因：`--item-slot-size: 79px` 与 `--item-tile-size: 63px` 差值 16px 透明边距叠加网格 `gap: 7px`（遗留 14px），实际空隙高达 23px。修复：slot/tile 统一 64px、外层 padding 归零；`.inventory-grid`/`.item-grid-picker-grid`/`.market-browse-grid` 统一 `gap: 6px`；清理 `styles.css` 遗留覆盖；`expedition.css` 战斗槽同步；`.item-tile-name` 内边距 `6px 3px 4px`，Hover 缩放 2.08→1.22。verify.sh（Release 构建、187 项领域测试、23 项服务端测试、容量门禁、前端构建）全部通过。

### TASK-20260824-30 全系统代码实现、潜在风险与文档一致性全面并发扫描分析
- 分类：（原文未标注）
- 时间：提出 2026-08-24 19:10:00｜完成 2026-08-24 19:20:00
- 状态：✅ 已完成
- 摘要：3 组 Agent 并发审计 + 全量测试（Domain.Tests 186/186、Server.Tests 23/23、前端构建）。关键发现：[P0] `src/Server/Runtime/GameHost.cs` 冷快照 LRU（20000）淘汰未从 `_knownPlayers` 移除 GUID，被淘汰玩家重连时跳过磁盘加载后 `GetState` 抛 `KeyNotFoundException` 形成重连黑洞；[P1] 快照反序列化异常静默回退创建 1 级空白角色；[P1] 角色更名未同步 `accounts` 表 `player_name`；[P1] `deploy/backup.sh` 空数组在 `set -u`/`set -e` 下崩溃中断备份；[P1] `Game.slnx` 与 `scripts/verify.sh` 遗漏 Server.Tests 工程（23 个用例）；[P2] 前端 `types.ts` 声明已废弃的 `pierceChance`/`curseRatio`/`furyRatio`/`weakenRatio`、`CombatLoadout` 缺 `abilityExperiences`；[P2] `README.md` 构建命令、执行器文档、怪物属性与 T2 配方文档过时；[P3] `EffectTooltipTarget`/`ChoiceMenu` 未登记 `docs/frontend/common-components.md`。

### TASK-20260824-29 主界面悬浮岛屿架构与玄门古籍美学重构（交互演示与落地规划）
- 分类：前端重构 / UI设计系统 / 悬浮岛屿架构 / 修仙美学 / 交互动效 / 原型预览
- 时间：提出 2026-08-24 18:34:25｜完成 未完成（归档时 Phase 1 交互 Demo 编写尚未勾选完成，Phase 2 用户验收与 Phase 3 移植待定）
- 状态：🚀 进行中（归档时停滞）
- 摘要：经 `/grill-me` 深度访谈确定重构方案：玄门古籍暗墨金石美学基调 + 模块化悬浮台型（Island Layout）三栏去网格线框化；左侧导航岛 64px 图标微缩 ↔ 220px 古籍卷轴全展切换、右侧战备岛贴边收折三 Tab（储物袋/装备虚影/功法槽位）、底栏日志岛单行走马灯 ↔ 多行历史面板、天机顶岛（浑天太极行动总台、气运灵牌、境界灵池、体征胶囊）；规划编写单文件高保真交互 Demo `docs/design/ui-layout-island-demo.html` 后分三阶段移植正式 `frontend/src/` 并更新 `docs/frontend/common-components.md`、运行 `scripts/verify.sh`。

### TASK-20260824-28 《道化太初》通用 Buff 实现器与词缀修饰器系统落地
- 分类：核心领域模型 / Buff实现器 / 属性解算流水线 / 配置支持 / 协议与前端同步（原文「责任领域」）
- 时间：提出 2026-08-24 10:45:00｜完成 2026-08-24 11:15:00
- 状态：✅ 已完成
- 摘要：构建通用 Buff/Modifier 底层：新建 `src/Domain/Constants/StatKeys.cs` 属性常量池（气血/灵力/物攻/法攻/双抗/命中/闪避/攻速/移速/修为倍率/采集倍率/炼丹倍率等）；`BuffState` 引入通用 `Modifiers` 字典（`GetModifier`/`SetModifier`）并同步 `CombatBuffApplication`；新建 `src/Domain/Simulation/BuffEvaluator.cs` 通用增益解算器，改造 `SimulationEngine` 与 `CombatSimulator`/`CombatGroupSimulator` 从通用 Buff 提取伤害/攻速/双抗/暴击词缀，支持 `StatKeys.MovementSpeed` 移速加成；同步 `Protocol`、`StateMapper` 与 `frontend/src/game/types.ts`。`BuffEvaluatorTests.cs` 单测 + verify.sh（186 单测 + 容量门禁 + 前端构建）一次性全绿。

### TASK-20260824-26 前端历史 Demo/Mock 残留全面排查、纯净修仙化清理与离线兜底真源对齐
- 分类：前端重构 / 历史残留清理 / 数据真源对齐 / 纯净修仙化 / 架构健壮性
- 时间：提出 2026-08-24 09:30:00｜完成 2026-08-24 09:42:00
- 状态：✅ 已完成
- 摘要：清理 `frontend/src/game/ids.ts` 中 `ACTION_DEFINITION_CORES` 早期 Mock 占位（`explore.gather-herbs`、`item.spirit-herb`、`encounter.qi-beast` 等），全量对齐真实 `config/game-data` 权威数据（`explore.gather-ningxue-grass`、`explore.chop-peach-wood`、`explore.mine-dark-iron`、`alchemy.refine-qi-pill`、`alchemy.refine-zhixue-powder`、`encounter.twilight-pass`）；同步 `game.zh.ts` 早期文案（山门试炼、采摘 凝血草、清剿 坠星异境等）；扩充 `VectorCharacter.tsx` 野怪立绘兜底映射（覆盖 rabbit/wolf/ape/tiger/bear/crow/priest 等全部 12 类妖兽及修士）；确认正式 `frontend` 无死代码假数据，与历史参考工程 `frontend-sim` 边界分明。verify.sh 全量通过、前端零警告。

### TASK-20260824-25 《道化太初》角色核心设计文档（Character Core Design Document）权威编纂
- 分类：全项目顶层设计 / 角色核心模型 / 状态连续性 / 属性与生命体征 / 跨系统流转 / ECS与持久化架构（原文「责任领域」）
- 时间：提出 2026-08-24 09:30:00｜完成 2026-08-24 09:48:00
- 状态：✅ 已完成
- 摘要：编纂 `docs/design/角色核心设计.md`：角色是完全常驻的统一实体（Unified Living Entity），生命/灵力/Buff/境界/伤病在时间轴上跨玩法连续穿透。6 大篇章：核心哲学（统一常驻实体、环境作用力、确定性时空连续性）、多维体征与数据模型（精气神、境界底蕴、武道装配、储物与坐标）、状态连续性与跨玩法流转（Buff 实体级统一时间轴衰减、脱战继承与 1 HP 濒死复苏、秘境/PvP 真实入场）、行动队列与身体状态互锁（300s 动作时间池、持续消耗品分摊）、技术架构映射（`PlayerState` 聚合根、`SimulationEngine` 确定性演进、`PlayerShardHost`+`YuoFiber` 单写者、`PlayerPatchDto` 增量同步）、设计红线（禁止影子角色、禁止跨玩法免费重置、全量 `double` 精度）。`docs/README.md` 登记索引。

### TASK-20260824-24 前端历史残留全面排查、加载链路健壮性重构与冷启动全链路优化
- 分类：前端架构 / 稳定性优化 / 历史残留清理 / 加载链路健壮性 / 启动容错 / 自愈机制
- 时间：提出 2026-08-24 09:00:00｜完成 2026-08-24 09:25:00
- 状态：✅ 已完成
- 摘要：修复 `App.tsx` WebSocket 握手成功但快照未就绪（`connection === 'online'` 且 `!hasReceivedSnapshot`）时误闪/卡死 `ConnectionErrorPage` 的时序竞态；`account.ts` 拦截 `'undefined'`/`'null'`/`'[object Object]'` 等损坏本地存储并提供 `resetAllLocalData()` 自愈入口；根级 `ErrorBoundary.tsx` 全局错误边界（仙侠风降级提示、堆栈折叠、一键重试与清缓存自愈）；`gameStore.ts` `resetGameStore` 登出深度重置战斗装配/属性/地图/版本号；`vite.config.ts` 分包（`vendor-react`/`vendor-store`/`index`）消除单 bundle 509KB 警告。verify.sh：182 单测全过、容量门禁通过、前端打包零告警。

### TASK-20260824-23 功法槽位重构为横排动态扩展架构（首位内功+后续术法）与战斗详情战备装配同步升级
- 分类：（原文未标注）
- 时间：提出 2026-08-24 07:25:00｜完成 2026-08-24 07:36:00
- 状态：✅ 已完成
- 摘要：功法槽位由固定 4 竖格重构为 5 槽横排动态体系：槽位 1 标「内功」（专属徽标与金色/紫气流光），槽位 2~5 标「术法 1~4」；`SystemConstants.cs` 的 `AbilitySlotCount` 调至 5 支持动态扩展，`SimulationEngine.cs`/`CombatSimulator.cs`/`CombatGroupSimulator.cs`/`PlayerStateReconciler.cs` 适配；前端 `COMBAT_ABILITY_SLOT_COUNT = 5`，重构 `AbilitiesPanel.tsx` 与 `App.tsx` 战斗详情 `CombatLoadoutPanel` 横排流动布局，补 i18n 与内功高亮光效。182 项领域测试、2c4g/40g 容量门禁与前端构建全量回归通过。

### TASK-20260824-22 右侧主栏多Tab重构（储物袋/装备/功法）与角色虚影六部位装备阵列及技艺工具槽位体系开发
- 分类：（原文未标注）
- 时间：提出 2026-08-24 07:05:00｜完成 2026-08-24 07:22:00
- 状态：✅ 已完成
- 摘要：右侧栏重构为储物袋/装备/功法三 Tab（`uiStore.ts` 引入 `rightColumnTab`）；`EquipmentPanel.tsx` 居中 `CharacterAvatar` 角色虚影（太虚微光+呼吸浮空）+ 左右六部位装备阵列（头部/衣服/腿部 与 武器/饰品/鞋靴）+ 底部 4 个技艺工具预留槽位，点击呼出 `ItemGridPicker` 穿脱、上行协议 `set_combat_loadout`；`AbilitiesPanel.tsx` 4 功法槽（等级/触发器/更换弹窗）与已学功法图鉴；`RightSidebar.tsx` 多 Tab 容器。遵循 `docs/frontend/common-components.md` 复用规范与 `useT()` 国际化规范。全量 Release 构建与 182 项领域测试门禁通过。

### TASK-20260824-21 纯净修仙全景主设计查看器（总纲.html）最新数据源与词法高亮同步升级
- 分类：单文件网页 / 数据源同步 / 视觉体验 / 词法高亮更新（原文「责任领域」）
- 时间：提出 2026-08-24 06:45:00｜完成 2026-08-24 06:55:00
- 状态：✅ 已完成
- 摘要：将 `docs/design/总纲.md` 最新 12 篇全景总纲（真实 10 门技能与 20 种秘籍残卷、5 秒生命/灵力自然回复、无任何 MWI 词缀与狂暴逻辑）同步至 `docs/design/总纲.html` 内嵌 `<script type="text/markdown" id="default-markdown">` 数据源；保留全填充布局、滑入动效、阶梯排版、实时搜索、大纲滚动监听、字号缩放与本地 `.md` 载入特性。无头浏览器 DOM 验证：52 个标题、51 个大纲项、264 个修仙发光胶囊完美渲染，无 MWI/狂暴残留。

### TASK-20260824-20 全库与全量文档彻底拔除“银河奶牛 (MWI)”照抄词缀体系（Curse/Fury/Weaken/Pierce/ModifierRuntime）
- 分类：全栈重构 / 战斗引擎 / 领域模型 / 协议合约 / 词缀纯正化（原文「责任领域」）
- 时间：提出 2026-08-24 06:15:00｜完成 2026-08-24 06:40:00
- 状态：✅ 已完成
- 摘要：彻底移除照抄《银河奶牛 (Milky Way Idle)》的 4 类词缀 `CurseRatio`/`FuryRatio`/`WeakenRatio`/`PierceChance` 与 15 秒滑动窗口 `CombatModifierRuntime` 计时器系统（含 `ApplyCombatModifierEffects`、`GetNextGroupModifierEventTime` 及过期事件调度）；清理范围覆盖 `src/Domain/`（CombatSimulator/CombatGroupSimulator/CombatCatalog/SimulationEngine/DataBudgetResolver 等）、`src/Protocol/Contracts.cs`、`src/Server/Runtime/StateMapper.cs`、`src/DataTool/Program.cs`、领域单测及 `总纲.md`/`总纲.html`/`SKILL.md`/`战斗设计.md` 文档。verify.sh：Release 0 错 0 警、182 项领域测试、配置指纹校验、2c4g/40g 容量门禁、前端编译全过；Git 自动提交。

### TASK-20260824-19 全工程彻底拔除狂暴（Enrage）历史残留代码、事件与数据字段
- 分类：领域模型 / 战斗模拟器 / 协议合约 / 配置解析 / 单测门禁（原文「责任领域」）
- 时间：提出 2026-08-24 06:00:00｜完成 未完成（文档状态「进行中」，8 项子任务均未勾选，进度日志止于 2026-08-24 06:05:00 完成全量代码引用定位）
- 状态：🚀 进行中（归档时停滞）
- 摘要：计划从 C# 服务端、领域模拟器、协议合约、配表工具与单测中彻底拔除「狂暴 (Enrage)」：删除 `CombatFormula.CalculateEnrageStacks`；从 `CombatGroupSimulator.cs`/`CombatSimulator.cs` 移除 `nextEnrageTick` 事件、`GetNextEnrageTick` 方法、`EnrageStacks` 状态与增伤乘区；从 `CombatDefinition.cs`/`CombatModels.cs`/`GameDataModels.cs`/`ItemModels.cs` 移除 `EnrageTimeMilliseconds`/`EnrageTickMilliseconds`/`MaxEnrageStacks`/`EnrageRatioPerStack`/`FuryRatio`；同步精简 `DataBudgetResolver`/`GameDataCatalog`/`CharacterStatResolver`/`DataTool`、`Protocol/Contracts.cs`、`StateMapper.cs` 与 `tests/Domain.Tests/CombatTests.cs` 断言。归档时仅完成引用定位，实施由后续任务接续。

### TASK-20260824-18 全系统架构缺陷全量修复与核心机制（48h离线上限/0.1s最小耗时/增量断层/CAS回滚/心跳/配置自愈）落地
- 分类：全栈治理 / 领域模型 / 调度引擎 / 存储与市场事务 / 前端同步与时钟 / 配表门禁（原文「责任领域」）
- 时间：提出 2026-08-24 03:10:19｜完成 2026-08-24 03:28:00
- 状态：✅ 已完成
- 摘要：落地核心机制：`SystemConstants.cs` 新增 `DefaultMaxOfflineDuration = 48h` 离线截断；非战斗行动单次最小最终耗时 100ms（内部保持真实毫秒推进）；单 Fiber 批次推演最大耗时/步骤限制与超时让出重入队；`CombatCatalog.cs` 装备属性聚合并行稳定排序与浮点等值容差；市场挂单/撤单/结算内存轻量镜像与落库失败安全回滚、CAS 版本冲突 3 次指数退避重试、Durable Outbox 合并缓冲（Coalescing Buffer）与 autovacuum 优化、WebSocket 断开即时销毁实体+30s 恢复宽限、双向 Ping/Pong 心跳；前端 Delta 版本空洞断层主动拦截重拉全量、`estimatedClockSkew` 静态偏差时钟根治 5 秒突变回退抽搐。verify.sh：182 单测 + 5000 玩家容量压测 + 前端构建一次性全绿。

### TASK-20260824-17 全量清理“银河奶牛 (MWI)”外部借称与“狂暴 (Enrage)”冗余机制
- 分类：设计去冗余 / 文档重构 / 纯粹修仙化 / 机制精简（原文「责任领域」）
- 时间：提出 2026-08-24 05:45:00｜完成 2026-08-24 05:55:00
- 状态：✅ 已完成
- 摘要：文档层清理：废除并删除 `docs/design/milky-way-idle-mapping.md` 并从 `docs/README.md` 索引移除；全量设计文档中「银河奶牛式/MWI/参考银河奶牛」等外部借称替换为纯正修仙表述（「动作耗时分摊持续消耗品机制」）；从 `总纲.md`/`总纲.html`/`持续消耗品系统.md`/`initial-design.md`/`SKILL.md` 清除「狂暴/狂暴倒计时/超时狂暴增伤」等机制描述，战斗聚焦抗性减伤、命中闪避、暴击破招、五行生克与仇恨对抗。真实无头浏览器 DOM 检查确认无残留。

### TASK-20260824-16 纠正命中/闪避对抗判定公式，统一为 5 秒生命/灵力完整回复属性体系
- 分类：战斗公式 / 属性规范 / 自然回复 / 文档纠偏与源头修复（原文「责任领域」）
- 时间：提出 2026-08-24 05:25:00｜完成 2026-08-24 05:40:00
- 状态：✅ 已完成
- 摘要：纠正文档两处脱离代码的错误：① 命中/闪避实为「先判定守方闪避」——闪避概率 = 守方闪避/(守方闪避+攻方命中+100.0)，闪避成功完全免伤，未闪避即命中，纠正文档中倒置混淆的「命中概率公式」；② 自然回复统一为每 5 秒（`RegenTickMilliseconds = 5_000`）结算独立完整属性 `HpRegen`/`MpRegen`（基准为上限 2% 保底 1 点），删除 MWI 式 `HpRegenPerTenSeconds` 10 秒周期与冗长拆解公式。修正 `docs/design/战斗设计.md`、`总纲.md`、`总纲.html`、`initial-design.md`、`milky-way-idle-mapping.md` 与 `.agents/skills/data-designer/SKILL.md` 六份文档并无头浏览器验证。

### TASK-20260824-15 废除 AI 杜撰技能文档，全量对齐真实 10 门技能与神通配置体系
- 分类：配置溯源 / 技能体系 / 真实数据对齐 / 架构瘦身 / 纠偏归档（原文「责任领域」）
- 时间：提出 2026-08-24 05:05:00｜完成 2026-08-24 05:15:00
- 状态：✅ 已完成
- 摘要：溯源确认真实生效技能严格只有 10 门——物理系 5 门：T1【重斩】(`heavy-slash`)、T1【连环】(`chain-strike`)、T1【嘲讽】(`taunt`)、T2【重云】(`heavy-clouds`)、T2【贯星】(`star-piercer`)；术法系 5 门：T1【掌心雷】(`palm-lightning`)、T1【轰炎】(`blazing-blast`)、T2【清平乐】(`qingping-melody`)、T2【雷光引】(`lightning-attraction`)、T2【破阵子】(`pozhen-melody`)；每门对应 1 本技能书（首次使用学会，再次使用 +10,000 经验）与 1 种残卷（+500 经验）共 20 种技能道具。废除 AI 杜撰的 `docs/design/early-game-skills-design.md`（虚构 20 门技能与五大流派矩阵），重构 `总纲.md`/`总纲.html` 第四篇对齐真实配置，更新 `docs/README.md` 索引。

### TASK-20260824-14 主设计查看器视口进入动效改造为从右向左渐显浮现（Right-to-Left Scroll Reveal）
- 分类：前端动效 / CSS 动画 / 视口渐显 / 用户交互体验（原文「责任领域」）
- 时间：提出 2026-08-24 04:50:00｜完成 2026-08-24 04:58:00
- 状态：✅ 已完成
- 摘要：`docs/design/总纲.html` 的 `.reveal-item` 视口渐显动效由「自下而上」改为从右向左滑入：初始 `transform: translateX(36px)`、`opacity: 0`、`filter: blur(4px)`，进入视口归位 `translateX(0)`、`opacity: 1`、`blur(0)`，缓动曲线 `cubic-bezier(0.16, 1, 0.3, 1)`。无头浏览器验证 CSS 规则与运行时类名生效。

### TASK-20260824-13 主设计查看器平滑滚动特效、层次化段落缩进与视口进入渐显动效（Scroll Reveal）开发
- 分类：前端动效 / 视口渐显 / 滚动特效 / 古籍排版层次 / 体验升级（原文「责任领域」）
- 时间：提出 2026-08-24 04:30:00｜完成 2026-08-24 04:45:00
- 状态：✅ 已完成
- 摘要：`docs/design/总纲.html` 升级：基于 `IntersectionObserver` 的视口渐显动效（`opacity: 0 -> 1`、`translateY(24px) -> 0`、`blur(3px) -> 0`，首屏元素立即可见、新内容级联交错显现）；`.p-lead`/`.p-h2-sub`/`.p-h3-sub` 阶梯式层次段落缩进与古籍排版体系；细金流光进度条与滚动条微光。无头浏览器滚动模拟验证：首屏 9 项立即可见，119 个块级元素随滚动触发 `.revealed` 渐显上浮。

### TASK-20260824-12 全系统技术栈、架构与核心设计代码多 Agent 并发审计
- 分类：（原文未标注）
- 时间：提出 2026-08-24 02:53:33｜完成 2026-08-24 03:02:00（审计完成时间）
- 状态：✅ 已完成
- 摘要：4 组专职 Agent（`domain-dev`/`ops-architect`/`frontend-dev`/`data-balancer`）并发审计，输出 8 项 P0/P1 缺陷矩阵：① 市场落盘 CAS 冲突（`MarketLedgerConcurrencyException`）无捕获回滚致内存-数据库状态分裂/丢资产双花（`GameHost.cs:L717-L733`、`PostgresStores.cs:L840-L865`）；② Durable Outbox 无快照合并与 autovacuum 致表膨胀 WAL 放大；③ 前端 Delta 增量缺版本连续性校验（`gameStore.ts:L293-L296`）丢包引发幽灵状态；④ `CombatArena.tsx` 时钟偏移阈值随挂机翻转致行动条 5s 突变回退 0%；⑤ 弱网 2000ms 启发式失效无限重连且定时器泄露并发 WS；⑥ `MaxTransitionsPerAdvance = 2,000,000` 极端离线单 Fiber 阻塞 Shard；⑦ 寻灵香/地火引 `durationSeconds` 错填致持续消耗品 6 秒被当即时药水扣除（消耗暴增 20 倍）；⑧ `GameDataCatalog.Validate` 漏检行动 `DurationSeconds <= 0`（0 会退化为 1ms 碎片推进百万次空转）。

### TASK-20260824-11 单文件主设计查看器左右全填充流式布局与全景修仙胶囊词法高亮重构
- 分类：前端视觉设计 / 单文件 HTML / 修仙词法解析 / 胶囊组件化 / 宽屏响应式（原文「责任领域」）
- 时间：提出 2026-08-24 04:10:00｜完成 2026-08-24 04:25:00
- 状态：✅ 已完成
- 摘要：`docs/design/总纲.html` 移除 `max-width: 1040px` 居中限制，改为左右全填充流式布局（`width: 100%`、左右内边距 40px），12 阶数值表/技能矩阵横向舒展；实现全景修仙词法高亮引擎：境界阶位胶囊（T1~T12、凡胎→飞升真仙）、Q1~Q8 八阶品质胶囊、流派徽章（物理系/法术系/剑修/刀修/符箓/五行/炼体）、战斗属性胶囊（HP/MP/AD/AP/护甲/法抗/暴击/破招/拆招）与数值百分比胶囊（`+20%`、`1.4x`、`6.0x` 等）。实测渲染 315 个发光胶囊（含 84 个境界徽章、151 个数值百分比）。

### TASK-20260824-10 独立修仙风格全景主设计文档单文件查看器（Master Design Viewer）开发
- 分类：设计可视化 / 独立单文件网页 / 前端视觉体系 / 修仙美学交互（原文「责任领域」）
- 时间：提出 2026-08-24 03:45:00｜完成 2026-08-24 04:00:00
- 状态：✅ 已完成
- 摘要：开发 `docs/design/总纲.html` 单文件离线查看器（`file://` 双击直开，零服务器依赖）：内嵌 `docs/design/总纲.md` 全文并支持本地文件选择/拖拽/URL 参数加载任意 Markdown；暗墨玄石美学（`#0e0c07` 背景、暗金折角 `.daohua-floating-corner`、五行色调高亮、楷体/宋体标题）；左侧大纲目录树（折叠/进度高亮/平滑滚动）、全局模糊搜索、字号缩放、表格/LaTeX/Mermaid 渲染、代码复制、Callout 卷轴框、阶位胶囊徽章、双端自适应。`docs/README.md` 登记入口。无头浏览器 `file://` 验证：50 标题/49 目录项/5 大表格解析渲染正常。

### TASK-20260824-09 《道化太初》总纲（Master GDD）权威编纂与全域设计整合
- 分类：全项目顶层设计 / 数值底座 / 战斗系统 / 剧情世界观 / 前端UI / 服务端架构 / 运维容量（原文「责任领域」）
- 时间：提出 2026-08-24 03:20:00｜完成 2026-08-24 03:40:00
- 状态：✅ 已完成
- 摘要：编纂集大成权威总纲 `docs/design/总纲.md`（12 篇全景体系）：服务端权威长线放置、DES 离散事件推进、全量 `double` 精度；12 阶四阶段境界与 8 品稀有度、`BaseFlatAnchors` 基础数值池与四独立乘区、双流派对偶配点；装备 6 槽位 6.0x 严格守恒、T1~T12 白板预算与品质乘区、+20 强化曲线、T1~T2 制式 4 套 28 件；五大流派 20 门神通与技能书/残卷体系；300s 动作分摊持续消耗品（透支免单与切槽惩罚）、19 种基础原料/6 种加工材料/4 种丹药；动态抗性非线性平方穿甲、三大对抗除法概率、仇恨威胁累积、1 HP 脱战复苏；`[minBudget, maxBudget]` 质量预算遇怪、5 波精英/20 波领主周期；怪物倍率（普通 80% 全属性/300% HP、精英 120%/500% HP、领主 150%/2000% HP）；七章主线剧情与 10 大碎片链；`FloatingPopover`/`RealmBadge`/`PillLabel`/`UnitNumber` 前端规范；四层架构、单 Fiber 单写者、Saga 市场事务；2c4g/40g 容量目标（5000 账号/500 在线/2000 挂机补算）。`docs/README.md` 登记索引。

### TASK-20260824-08 全量设计文档冲突系统性梳理与权威真源对齐重构
- 分类：全项目设计文档 / 数值模型 / 战斗系统 / 架构规范 / 世界观系统（原文「责任领域」）
- 时间：提出 2026-08-24 03:00:00｜完成 2026-08-24 03:15:00
- 状态：✅ 已完成
- 摘要：消除 8 处设计冲突与版本断层：装备预算统一两段式 6.0x 乘区模型（武器 1.4x、衣 1.4x、头/腿/鞋/饰 各 0.8x，T1 28 点/T2 40 点白板基准）；战斗减伤统一动态 K 非线性平方穿甲公式与对抗除法概率；怪物统一怪我同构与单场地图预算池（普通 300%/精英 500%/领主 2000% HP）；废除 60s Buff 叠层，统一 300s 动作分摊持续消耗品（+30% 收益，引气入体产出 10 修为、1 灵力）；死亡脱战给 1 HP 重伤复苏、读条 180s 正式复活回满；剧情文案「太初无规矩」与游戏 UI/数值全 12 阶阶位解耦；开发指南代码示例重构为 YuoCore `ActionTransitionSystem` 唯一过渡系统 + 静态 `Executor*`。更新 `player-expected-attributes-model.md`、`SKILL.md`、`战斗设计.md`、`initial-design.md`、`logic-development-guide.md`、`player-data-migration-and-reconciliation.md`、`lore.md`、`maps.md` 并交叉复查。

### TASK-20260824-07 修炼行动（引气入体）前端产出与动态加成解算实时预览重构
- 分类：前端重构 / 交互体验 / 数值解算 / UI 规范 / 国际化同步
- 时间：提出 2026-08-24 02:40:00｜完成 2026-08-24 02:46:00
- 状态：✅ 已完成
- 摘要：新增 `frontend/src/game/actionCalculations.ts` 动态加成解算纯函数：综合乘率 = 丹药 × 世界 × Buff（引气丹 1.3、通脉丹 1.5、`world.cultivationMultiplier` 如青岚山谷 1.2、`buff.qiGainMultiplier`）；修炼基础产出 10.0 修为/1.0 灵力，底蕴化生消耗 `min(foundationExp, Exp × 2)`，境界圆满待突破时修为 100% 注入道基底蕴；重构 `ActionCard`/`ActionModal` 展示加成后产出（如 `13 修为 / 1 灵力 (+30%)`）与微光加成胶囊标签，移除「每次 +1」硬编码旧文案，更新 `app.zh.ts`/`template.csv`。验证：Release 构建、180 项领域测试、容量门禁、数据校验与前端构建全过。

### TASK-20260824-06 配置编辑器现代化扁平化交互与 UI 重构 (Modern Flat UI & Linear Design)
- 分类：（原文未标注）
- 时间：提出 2026-08-24 02:29:05｜完成 2026-08-24 02:34:00
- 状态：✅ 已完成
- 摘要：`tools/config-editor/` 全面现代化扁平化重构：Zinc-950 底色/Zinc-900 模块卡片/Zinc-800 分界线暗色体系与冷青/Indigo/Sky 强调色；输入控件（`NumberInput`/`EnumSelect`/`BooleanSelect`）扁平圆角+Focus 环、布尔升级 Segment Control；外键选择器（`ReferenceSelect`/`MultiReferenceSelect`）升级 Raycast 级 Popover 搜索弹层与 Tag 胶囊；`Sidebar`/`Header` 现代扁平布局与呼吸灯；9 大专属编辑器面板卡片化；`GlobalValidator`/`RawJsonModal` 毛玻璃蒙层与结构化错误跳转。verify.sh 100% 通过。

### TASK-20260824-05 游戏配置系统多文件多目录模块化重构
- 分类：（原文未标注）
- 时间：提出 2026-08-24 01:20:38｜完成 2026-08-24 01:26:00
- 状态：✅ 已完成
- 摘要：`config/game-data` 由单类型单 JSON 大文件拆分为 7 大子目录（`items/`、`equipment/`、`abilities/`、`monsters/`、`encounters/`、`maps/`、`actions/`）共 23 个模块化 JSON 文件；重构 `GameDataCatalog.cs` 支持外部目录与内嵌程序集资源双向递归扫描、单对象/对象数组自动展开、基于确定性相对路径排序计算配置指纹；同步更新 `Domain.csproj`、`ComputeNode.csproj`、`DataTool`（`export-defaults`/`validate` 多目录支持）、`tools/config-editor`（按 `_sourceFile` 分组写回）与 `scripts/verify.sh`。verify.sh：Release 构建、180 个领域单测、2c4g/40g 容量门禁、前端构建 100% 通过。

### TASK-20260824-04 独立游戏配置编辑器开发与全量配置独立解析支持
- 分类：（原文未标注）
- 时间：提出 2026-08-24 01:10:00｜完成 2026-08-24 01:26:00
- 状态：✅ 已完成
- 摘要：新建独立配置编辑器 `tools/config-editor/`（React 19 + TypeScript + Tailwind CSS，Vite Node.js 中间件直读写磁盘 `config/game-data` 25+ 个配置文件，自动 `.backup/` 变更快照）：非数值字段全下拉化（枚举单/多选、跨表外键 ID 搜索选择器、14 维属性字典配置器、布尔下拉）；9 大配置文件（manifest/actions/items/abilities/equipment/monsters/encounters/maps/expected-attributes）专属独立解析与编辑表单；`scripts/config-editor.sh` 一键启动 + 前端实时死链检测 + .NET `DataTool` 校验。验证：编辑器构建 0 错 0 警（55 模块）；`GET /api/config/all` 读取 25 个配置文件（49 行动、52 道具、10 神通、28 装备、12 妖兽、4 遭遇、2 大地图）；verify.sh 全过（180 领域单测、容量门禁）。

### TASK-20260824-03 全量游戏数据配置文件最简化重构（全面去除冗余默认值与平铺 0/空集合）
- 分类：架构重构 / 游戏数据精简 / 数据解算引擎 / CLI 工具优化 / 配表瘦身
- 时间：提出 2026-08-24 01:45:00｜完成 2026-08-24 02:02:00
- 状态：✅ 已完成
- 摘要：全量精简 `config/game-data/` 下 7 个 JSON 文件（`abilities.json` 冗余度 54.4%、`actions.json` 28.2%、`encounters.json` 19.0% 等），剔除零值/false/空集合/默认平铺；强化 `AbilityConfig`/`CombatantConfig`/`CombatEncounterConfig`/`WorldNodeDefinition`/`ActionDefinition`/`ItemConfig`/`EquipmentConfig` DTO 与 `DataBudgetResolver` 各 Resolve 方法可空缺省兜底，解算行为 100% 不变；`DataTool` 实现全类型 Lean 导出转换器（`ToActionConfig`/`ToAbilityConfig`/`ToEquipmentConfig`/`ToCombatantConfig`/`ToEncounterConfig`/`ToMapConfig`/`ToItemConfig`）杜绝重导垃圾字段。verify.sh：180 领域测试、数据校验、容量门禁、前端构建全过。

### TASK-20260824-02 物品配置瘦身重构与序列化缺省优化（消除功法秘籍/消耗品冗余占位字段）
- 分类：架构重构 / 游戏数据精简 / 数据解算引擎 / CLI 工具优化 / 数值规范
- 时间：提出 2026-08-24 01:18:00｜完成 2026-08-24 01:36:00
- 状态：✅ 已完成
- 摘要：`config/game-data/items.json` 中 20 种功法秘籍（`item.tome.*`）与残卷（`item.fragment.*`）仅保留 `usableInActionTypes`（底层 `SimulationEngine.UseItem` 走功法特化逻辑，不消费丹药参数），丹药/持续消耗品剥离 0/1.0/空列表默认值，文件体积 27.6KB→16.6KB（瘦身 40%）；`ConsumableConfig`/`BuffEffectConfig` 可选字段统一可空缺省，`DataBudgetResolver` 统一解算兜底；`DataTool` 引入 `ToItemConfig`/`ToConsumableConfig`/`ToBuffEffectConfig` 与 `JsonIgnoreCondition.WhenWritingNull` 根治冗余字段再生；`.agents/skills/data-designer/SKILL.md` 补充「6.3 物品配表最简原则」。180 领域测试、数据校验、容量门禁、前端构建全过。

### TASK-20260824-01 物品 Hover 提示卡片移除来源与产出，全量物品材料精简为纯背景信息描述
- 分类：前端交互 / UI 优化 / 游戏数据重构 / 世界观文本精简
- 时间：提出 2026-08-24 00:56:07｜完成 2026-08-24 01:03:00
- 状态：✅ 已完成
- 摘要：`frontend/src/components/ItemDisplay.tsx` 的 `ItemTooltipTarget` 彻底移除 Hover 气泡顶部来源副标题（`subtitle`）、底部产出标签（`stats` 中 `market.tooltip.source` 项）与附属用途气泡（`asideCards`），底部仅保留品类/等阶/持有等真实属性；`config/game-data/items.json`、`equipment.json` 与 `src/Domain/GameData/GameDataCatalog.cs` 中全部 80 种物品/材料/装备/丹药/秘籍/残卷描述替换为简短纯背景修仙设定，剔除「用于...」「可用于制作...」等功能性表述；同步 `game.zh.ts`、`market.zh.ts`、`ids.ts`、`ItemGridPicker.tsx`、`MarketPage.tsx`、`MarketBrowseGrid.tsx`、`MarketOrderModal.tsx` 并重生成 `frontend/public/locales/template.csv`。verify.sh：180 项领域测试全绿、容量门禁达标、前端构建通过。


### TASK-20260823-14 大地图目标地点赶路弹窗确认与右侧生态展示结构化重构
- 分类：前端交互 / 地图系统 / 体验优化 / UI 重构
- 时间：提出 2026-08-23 23:08:00｜完成 2026-08-23 23:25:00
- 状态：✅ 已完成
- 摘要：
  - 地图右侧详情面板新增「启程前往」交互按钮，点击弹出 `TravelConfirmModal` 二次确认弹窗，展示起点、目标地点、最短路径预估耗时（秒）与途经节点路线链，提供「立即启程/暂缓」操作。
  - 在 `WorldMapPanel.tsx` 实现轻量单源 Dijkstra 最短路径与耗时预估算法 `planShortestTravel`，支持驻留节点与路段中途双重场景。
  - 右侧生态展示由粗糙行动列表重构为三大结构化卡片：🌿 产出资源（晨露、乌铁矿、凝血草、桃木等）、🐾 栖息生灵（普通/精英/领主，如魔化野兔、木灵、铁甲熊、伥虎）、🏛️ 地标与设施（百年古槐、村中古井、漱玉跳岩、绝顶道庐等），适配 `expedition.css` 并同步 i18n。
  - 验证：`scripts/verify.sh` 全量通过（182 项测试全绿、容量门禁达标、前端构建成功）。

### TASK-20260823-13 世界大地图架构、T1-T2区域节点网络与全生态深度设计
- 分类：未单列
- 时间：提出 2026-08-23 22:40:00｜完成 2026-08-23 23:05:00
- 状态：✅ 已完成
- 摘要：
  - 依据通用大世界设计哲学（`docs/design/通用地图与遇怪系统.md`）与 T1-T2 物品/怪物生态草案，确立世界节点（`WorldNode`）网络化拓扑：拒绝割裂刷怪本，每节点聚合生活采集 + 历练遇怪 + 修炼环境 + NPC 交互 + 阵法建筑。
  - T1 定调「凡俗仙山 · 青云境」（祥和俗世仙山，草庐设于云巅绝顶俯瞰红尘），T2 定调「异象险途 · 叩仙门」（天地异象、星煞侵蚀、古修残蜕与异化妖魔）；行动命名全面直白化（如 `收集晨露`、`挖掘乌铁矿`、`引气入体`）。
  - 落地 `config/game-data/maps.json`（T1 青云仙山 8 节点 + T2 星陨异境 7 节点）、`actions.json`、`encounters.json`、`IdConstants.cs` 与 `NearbyPanel.tsx`；移除所有无用 NPC 配置及前端占位卡片。
  - 验证：`scripts/verify.sh` 全量通过（182 项测试全绿、容量门禁达标、前端构建成功）。

### TASK-20260823-12 移除灵气独立道具，重构技艺导航与炼丹/炼器多级选项卡体系
- 分类：未单列
- 时间：提出 未记录（建档 2026-08-23 22:38:00）｜完成 未单列（末次进度 2026-08-23 22:52:00）
- 状态：✅ 已完成（原文未单列状态字段，据全部子任务勾选与门禁通过判定）
- 摘要：
  - 彻底移除 `resource.qi` 灵气独立道具（修为 `FoundationExp` 与灵力 `Qi` 已作为角色属性独立维护）：清理 `config/game-data/items.json`、`actions.json` 引气入体产出、`IdConstants.cs`、`ids.ts`、`game.zh.ts`、`CombatArena.tsx`。
  - 侧边栏「炼丹」重构为「技艺」可展开/收起二级导航（炼丹、炼器），移动端底部导航同步适配；新建 `CraftingPage` 组件，集成顶部子选项卡与多分类过滤：炼丹分类（修炼、斗法），炼器分类（武器、头部、衣服、腿部、鞋履、饰品）。
  - 打磨侧边栏折叠/展开动画与激活态指示、暗墨胶囊子选项卡高亮、响应式与移动端布局；适配 AutoUse 消耗品槽位与配方模态弹窗。
  - 验证：前端 Vite 生产构建与 TypeScript 检查 100% 通过。

### TASK-20260823-11 伤害大类（物理/术法）与五行属性（金木水火土）双正交伤害模型重构
- 分类：架构重构 / 领域模型 / 战斗系统 / 数值与五行规则
- 时间：提出 2026-08-23 22:20:00｜完成 2026-08-23 22:50:00
- 状态：✅ 已完成
- 摘要：
  - 废弃单一扁平 `DamageType`（Physical/Water/Nature/Fire）枚举，新增双正交枚举：`DamageCategory`（`Physical=0`/`Spell=1`，决定物攻/法攻加成底座并对抗护甲 Armor 或法抗 MagicResistance）与 `Element`（`None`/`Metal`/`Wood`/`Water`/`Fire`/`Earth`，决定攻方五行增伤与守方五行抗性）。
  - 普攻默认固定纯物理（`DamageCategory.Physical, Element.None`），技能伤害完全由技能自身配置决定；`abilities.json`、`monsters.json`、`equipment.json` 统一声明 `category` 与 `element`。
  - 重构 `CombatSimulator.cs` 四层伤害结算流水线：大类基础数值 → 大类防御减免（物理穿透 vs 护甲、法术穿透 vs 法抗）→ 五行增减伤 → 暴击/免爆/易伤/伤害吸收。
  - 同步更新协议 DTO、StateMapper、前端 types/组件/i18n；验证：Release 构建、182 项领域单测与 `scripts/verify.sh` 门禁通过。

### TASK-20260823-10 全面清理角色流派/伤害属性历史冗余、新号硬编码与历史兼容层
- 分类：架构重构 / 领域模型 / 战斗系统 / 历史包袱清理
- 时间：提出 2026-08-23 21:55:00｜完成 2026-08-23 22:15:00
- 状态：✅ 已完成
- 摘要：
  - 从领域模型 `CombatLoadout`、协议 DTO `CombatLoadoutDto` 与前端类型中移除历史遗留的 `Style` 与 `DamageType` 字段：普攻伤害属性由穿戴武器 `equipment.DamageType` 决定（无武器兜底物理 `Physical`），技能伤害 100% 由 `ability.DamageType` 决定，攻击形态由武器流派 `equipment.Style` 决定（无武器为体修 `Body`）；重构 `CombatSimulator` 为武器/技能纯粹驱动。
  - 清理 `SimulationEngine.CreateDefaultState` 写死的新手 3 行动队列（采集凝血草/炼制引气丹/吐纳引气）与引气丹自动使用槽，新号初始化为空动作队列、空自动使用槽的干净状态。
  - 清理 `IdConstants.cs` 中长弓/灵枪/重盾/灵茶/云锦糖等历史别名与过渡兼容重载，更新 LoadTests 与测试用例等全部引用点。
  - 验证：180 项领域测试与 2c4g/40g 容量门禁 100% 通过，前端构建正常无报错。

### TASK-20260823-09 全量物品体系重构与设计规范
- 分类：数值重构 / 物品体系 / 经济循环 / 玩法设计
- 时间：提出 2026-08-23 18:31:14｜完成 2026-08-23 21:40:00
- 状态：✅ 已完成
- 摘要：
  - 建立 T1~T12 全局物品分类分阶框架（基础材料/消耗品/装备/特殊资产四大类），打通「采集/历练产出 → 炼丹/炼器 → 消耗/穿戴 → 挑战更高阶地图」产销闭环；关联设计文档：[`docs/临时设计讨论/全量物品重构与T1-T2设计草案.md`](../../archive/discussions/全量物品重构与T1-T2设计草案.md)、[`docs/design/持续消耗品系统.md`](../../design/总纲/06-持续消耗品机制与生活制造体系.md)、[`docs/design/milky-way-idle-mapping.md`](../../archive/design/milky-way-idle-mapping.md)、`.agents/skills/data-designer/SKILL.md`。
  - 装备模型重构：14 基础属性百分比区间（`BaseStatRatios`）+ 额外固定值/特殊机制区间（`FlatAndSpecialStats`）；放弃黑盒反推改用正向预算审计。关键数值：T1 装备总预算 28 点、T2 40 点；部位权重武器/衣服 1.4×、其余 0.8×（T1 全身严格 168 点、T2 严格 240 点）；琴特化奶妈武器（自带回蓝 + 50% 额外治疗，总预算 -25%）；自动回复收拢至饰品（1 点回复耗 4 预算）。
  - 丹药与材料：引气丹持续 300s +30% 修炼、通脉丹持续 300s +50%、止血散 CD60s 回 100% HP、顺气丸 CD60s 回 100% MP；确立 12 种 T1-T2 怪物专属掉落材料与 19 种材料【品阶 Tier × 8 阶品质 Quality】矩阵（彻底废除数字 Level 字段，T1 统一 Lv1/Q1 白、T2 统一 Lv18/Q2 绿）；制式装备配方标准化（武器/衣服 7 个主材、其余 5 个，怪物/特殊材料严格 1 个）；制造 T2 装备需消耗 2 件对应 T1 装备熔炼升阶；掉落概率阶梯：普通 100%/稀有 10%/特殊 1%/完整技能书 0.1%/技能残卷 2%；技能书首用解锁（0 经验）再用得 10000 经验、残卷得 500 经验；新增 `ExperienceConstants` 通用 1~200 级经验表。
  - 落地 81 种物品/材料/装备配置、12 种怪物掉落表、49 种行动配方（`items.json`/`equipment.json`/`actions.json`/`monsters.json`）；验证：180 项领域测试与 `verify.sh` 全流程通过并提交 Git。

### TASK-20260823-08 银河奶牛式持续消耗品机制与引气数值设计
- 分类：机制设计 / 数值重构 / 领域模型 / 消耗品系统
- 时间：提出 2026-08-23 17:41:58｜完成 2026-08-23 17:54:15
- 状态：✅ 已完成
- 摘要：
  - 消耗品分流：立即消耗品（瞬发效果 + 独立 CD）与持续消耗品（按动作实际执行耗时扣减槽位剩余有效时间，如引气丹 300s 完成 10s 引气扣 10s、加速为 5s 只扣 5s）；槽位状态持久保留、透支免单（剩余不足单次耗时时消耗完即免单）、中途切换惩罚（主动切换/卸下扣 1 件不退剩余时间）。
  - 引气入体（`cultivation.draw-qi`）单次基础耗时 10 秒，产出重构为 10 修为（`FoundationExp=10.0`）+ 1 灵力（`Qi/Manapoints=1.0`）；引气丹（`item.qi-gathering-pill`）单件持续 300 秒、不可叠层、非 Buff 计时器、效果 +30% 修炼效果（修为与灵力均 ×1.30）。
  - 落地：更新 `actions.json`/`items.json`，扩展 `AutoUseSlot` 领域模型与 DTO（新增 `RemainingDurationSeconds`）及 `ItemDefinition`/`ConsumableDefinition`，重构 `ExecutorAction`、`SimulationEngine`、`ActionTransitionSystem` 实现确定性扣减；设计文档沉淀至 [`docs/design/持续消耗品系统.md`](../../design/总纲/06-持续消耗品机制与生活制造体系.md) 与 [`docs/design/milky-way-idle-mapping.md`](../../archive/design/milky-way-idle-mapping.md)。
  - 验证：175 项领域单测与 `scripts/verify.sh` 全局门禁 100% 通过（容量门禁、Release 构建通过）。

### TASK-20260823-07 玩家存档版本迁移规约与开发期容错自愈系统
- 分类：架构设计 / 领域自愈 / 数据持久化 / 存档迁移规范
- 时间：提出 2026-08-23 16:17:43｜完成 2026-08-23 16:23:10
- 状态：✅ 已完成
- 摘要：
  - 背景痛点：配表指纹（`GameDataFingerprint`）频繁变动导致快照反序列化直接抛 `InvalidDataException`，须手动清库。落地开发/内测期自愈清洗器 `PlayerStateReconciler`：指纹不一致时宽容反序列化并自动清洗——剔除失效物品、卸下失效装备、清理失效技能与快捷栏、失效 ActionId 任务安全出队、失效遭遇安全结算脱战、失效地图节点自动回出生城镇、失效丹药自动解绑；保留道号/账号/境界/修为灵气/灵石/合法物品装备技能等，清洗后输出 Warning 审计日志并更新指纹写回。
  - 同时固化正式运营阶段《玩家存档版本迁移与容错自愈架构规约》（链式迁移管道、停服冷批处理、基线版本修剪、双重防线），见 [`docs/backend/player-data-migration-and-reconciliation.md`](../../future/player-data-migration-and-reconciliation.md) 与 [`docs/backend/domain-data-architecture.md`](../../backend/domain-data-architecture.md)。
  - 集成于 `StateSerializer.DeserializeAndReconcile` 与 `GameHost.EnsurePlayerAsync`/`GetState`；新增 `PlayerStateReconcilerTests` 与 `StateSerializerEnvelopeTests` 覆盖失效条目剔除与合法资产保留。
  - 验证：172 项领域单测、23 项服务端单测、Release 构建、容量目标门禁与前端打包全部 100% 通过。

### TASK-20260823-06 通用地图架构、生物（NPC/怪物）与预算遇怪机制设计
- 分类：未单列
- 时间：提出 2026-08-23 03:47:32｜完成 2026-08-23 04:32:30
- 状态：✅ 已完成
- 摘要：
  - 确立「去纯战斗副本」原则：所有地图均为通用世界地点，聚合 NPC（任务/传授技能，行为结构预留）与怪物（历练遇怪）两类生物；地图无独立危险度评级字段，危险度即 `[minBudget, maxBudget]` 质量预算区间，融合灵狐（Linghu）Quality 预算扣减刷怪算法（循环抽取扣减 `remainBudget`，连续 3 次超限停止），可配 `eliteInterval: 5`、`bossInterval: 20`。
  - 配置升级：`monsters.json` 为所有怪物增加 `budgetCost`，T1 等级梯队 1-12 级（野兔 Lv1、木灵 Lv3、火灵 Lv5、魔猿 Lv8、铁甲熊 Lv10、伥虎 Lv12 Boss），T2 平滑承接 Lv14~24；`encounters.json`/`maps.json` 配置稀疏特色生态（药谷仅木灵、落霞西岭配野兔与铁甲熊）与预算区间；设计规范沉淀至 `docs/design/通用地图与遇怪系统.md`。
  - 领域层 `Domain.Simulation.CombatGroupSimulator` 实现精英波次（每 5 场）/Boss 波次（每 20 场）判定与预算区间随机 + 轮盘加权抽取扣减；协议扩展 `NearbyMonsterDto` 下发生态怪物池与危险度区间；前端 `NearbyPanel.tsx` 渲染危险度、怪物卡片（头像/品阶徽章/等级/预算）与 Hover 详情浮层。
  - 验证：167 项领域单测 100% 通过，前端编译打包、容量目标与 Release 全量构建通过。

### TASK-20260823-05 战斗怪物显示与数值修复，历练/地图/详情架构重构
- 分类：Bug修复 / 数值平衡 / 架构重构 / 前端交互
- 时间：提出 2026-08-23 02:59:32｜完成 2026-08-23 03:08:45
- 状态：✅ 已完成
- 摘要：
  - 修复怪物与地图名称混淆：`StateMapper.cs` 的 `ToExpeditionProgress` 改从 `activeEnemy?.Name ?? configuredEncounter.Enemy.Name` 取具体怪物名（而非遭遇名），并补充 `EnemyId` 字段下发 `ExpeditionProgressDto`，前端按 `demonized-rabbit`/`wood-spirit` 等精确加载立绘。
  - 数值修复：`CharacterStatResolver.cs` 校准 `UnitRank` 生命乘区——Boss (1.50, 6.00)、Leader (1.30, 3.50)、Elite (1.15, 2.00)、Normal/Summon/Pet (1.00, 1.00)、Minion (0.75, 0.50)（一阶魔化野兔降至约 45~50 HP、攻击约 6~8，符合 1 级玩家单挑预期）；`encounters.json` 的 `encounter.qi-beast` 改为 `maxSpawnCount: 1`、`maxTotalStrength: 25`，消除 1 级玩家（约 121 HP）被近 1000 HP 敌群秒杀问题。
  - 前端重构：`uiStore.ts` 主 Tab 增设 `'map'`（`TabId` 扩展），拆分 `MapPage`（地图/附近子 Tab）、`ExplorationPage`（历练行动 + `ActionModal`）与 `CombatDetailsPage`（`CombatArena` + `CombatLoadoutPanel`）；确认战斗后经 `setActiveTab('combat')` 自动跳转详情；同步 `app.zh.ts`/`game.zh.ts` 词典。
  - 验证：`scripts/verify.sh` 全流程门禁（构建、167 单测、容量压测、Vite 构建）100% 通过。

### TASK-20260823-04 大地图交互系统重构与行动切换体验修复
- 分类：前端体验 / 地图交互 / Bug修复 / 视觉动效
- 时间：提出 2026-08-23 02:44:30｜完成 2026-08-23 02:46:40
- 状态：✅ 已完成
- 摘要：
  - 修复行动完成自动跳详情 Bug：移除 `ExpeditionPage` 中 `activeEntryId` 变动时强制 `setActiveSubTab('duel')` 的劫持逻辑，保持用户当前子标签选择。
  - 大地图引入 1200×800 虚拟像素坐标系，SVG 单位精确对应 1 CSS 像素，距离文字以带背景微型胶囊 `<g>` 渲染，消除 `preserveAspectRatio="none"` + `viewBox="0 0 100 100"` 造成的文字拉伸变形；连线 Hitbox 拓宽为 32px 透明拾取层（`stroke-width: 32px; pointer-events: stroke`），Hover 触发金色流光高亮与距离胶囊显现。
  - Stage 容器以 Pointer 事件（Down/Move/Up/Cancel）+ `setPointerCapture` 实现平滑拖拽漫游（移动 <4px 判定点击节点，≥4px 判定拖拽），并按视口自动计算 `pan = { x: stageWidth/2 - playerPx, y: stageHeight/2 - playerPy }` 使玩家位置居中，提供定位/缩放微调/重置视口浮动控制栏。
  - 玩家位置标记由带「修」字旋转菱形改为翡翠/青玉色调向下箭头（发光核心 + 脉冲波纹 + 悬浮呼吸动效），停驻时指向节点、旅行时尖端指示行进轨迹。

### TASK-20260823-03 基于 planar-art 规范设计并生成全部 T1 怪物视觉资产与折面规范卡
- 分类：美术资产 / 视觉系统 / 怪物设计 / AI 提示词工程
- 时间：提出 2026-08-23 01:45:54｜完成 2026-08-23 01:49:30
- 状态：✅ 已完成
- 摘要：
  - 按 planar-art（黑白单彩极简折面）美术规范，为 `config/game-data/monsters.json` 全部 6 个 T1（练气期/1阶）怪物设计折面结构卡、色彩拓扑与中英文提示词，并实际生成全套 2D 实体图形。
  - 设计矩阵：魔化野兔（Minion，G 贴地+C 紧凑蓄力，妖异暗紫，Brace+Anchor）、木灵（Minion，V 直立+T 挺拔长轴，碧玉翠绿，Core+Keel）、火灵（Minion，W 悬浮推力+R 放射破风，炽烈朱红，Fan+Sweep）、魔猿（Elite，V 直立拳撑+C 宽厚重肩，血煞赤红，Core+Brace）、铁甲熊（Elite，G 贴地+D 穹顶重装，玄金琥珀，Plane+Brace）、伥虎（Boss，G 贴地低伏+S 流线杀意，幽冥青蓝，Split+Keel）。
  - 交付：6 个怪物标准化两段式英文 Prompt 与中文结构分解，6 张符合 96px 可读性、硬边切角与 C 纸板装裱标准的 1:1 图像资产。

### TASK-20260823-02 主角立绘全面替换为单一本命矢量图（移除历史 WebP、收拢道貌体系为纯净矢量立绘）
- 分类：未单列
- 时间：提出 2026-08-23 01:34:28｜完成 2026-08-23 01:41:10
- 状态：✅ 已完成
- 摘要：
  - 将优化后的 30.9KB 仙侠女性剑修矢量图写入 `frontend/public/avatars/player.svg` 作为唯一默认主角立绘，删除历史 `frontend/public/avatars/female.webp` 与 `male.webp`。
  - 前端收拢：`avatarStore.ts` 废弃 `female | male` 多选项并定义默认资源路径常量 `/avatars/player.svg`（保留 `getPlayerAvatarSrc()`/`useAvatarStore`）；`VectorCharacter.tsx` 统一呈现；`InsightPage.tsx` 移除头像点击切换男女与旋转图标；`CombatArena.tsx` 统一使用 `DEFAULT_PLAYER_AVATAR_SRC`；`app.zh.ts` 增加 `defaultName`/`defaultDesc`。
  - 验证：`npm --prefix frontend run build` 与 `scripts/verify.sh` 全局门禁（Release 构建、167 领域单测、数据校验、容量门禁）100% 通过。

### TASK-20260823-01 创建专职设计与预期对照 Agent (design)
- 分类：工程协作 / Agent规范 / 团队研发效能 / 设计预期审计
- 时间：提出 2026-08-23 00:26:57｜完成 2026-08-23 00:29:40
- 状态：✅ 已完成
- 摘要：
  - 为防设计漂移（Design Drift），在 `.agents/agents/design/agent.md` 创建极简命名的专职 Agent `design`（命名经用户确认采用最短方案），核心职责：设计意图沉淀、代码与 UI 对照审计（Gap 分析）、防设计偏离预警、设计文档权威归档。
  - 与既有 5 大专职 Agent（`domain-dev`/`frontend-dev`/`data-balancer`/`qa-tester`/`ops-architect`）协同，形成「设计预期 → 研发落地 → 质量门禁 → 运维发布」完整闭环。

### TASK-20260822-15 前端国际化架构重构（中文 TS 内置类型源 + 外语 CSV 动态按需加载）
- 分类：前端架构 / 国际化体系 / 模块重构 / 开发体验优化
- 时间：提出 2026-08-22 23:12:05｜完成 2026-08-22 23:15:00
- 状态：✅ 已完成
- 摘要：
  - 中文保留 `frontend/src/i18n/domains/*.zh.ts` 与 `locales/zh-CN.ts` 作为 `Messages`/`TranslationKey` 编译期强类型推导基准并零延迟首屏内置；外语包全量转 CSV 静态资源（导出 956 条全量英文翻译至 `frontend/public/locales/en-US.csv` 与 `template.csv`），删除冗余 `*.en.ts` 文件。
  - `frontend/src/i18n/index.ts` 实现零依赖轻量 RFC 4180 CSV 解析器，切换语言时异步 fetch + 内存缓存 + Zustand 响应式重渲染，未命中或加载中无缝回退中文树状词典。
  - 新增 `scripts/export-i18n-csv.mjs` 一键导出/增量合并/覆盖率检查（`package.json` 增加 `i18n:export` 脚本），同步更新 `docs/frontend/common-components.md` 的 i18n 规范。
  - 验证：`scripts/verify.sh` 全量门禁（Release 构建、领域测试、数据校验、2c4g/40g 容量目标、Vite 生产构建）通过。

### TASK-20260822-14 玩家形象升级（女修士立绘默认 + 男修士可选 + 图像抠图与替换）
- 分类：前端视觉与交互 / 角色形象系统 / 抠图与资产管线
- 时间：提出 2026-08-22 22:48:49｜完成 2026-08-22 23:09:40
- 状态：✅ 已完成
- 摘要：
  - 将 SVG 矢量立绘替换为真实图像：对用户提供的男女剑修图做高精度透明抠图（泛洪连通域背景识别、边缘抗锯齿与去噪，保留白描外边框），按用户要求以 Lanczos 高阶重采样导出 256×256 纯 WebP（`female.webp`/`male.webp`，约 30KB）放入 `frontend/public/avatars/`。
  - 默认形象为女剑修，男修士可选；`avatarStore`（`useAvatarStore`/`localStorage`，键 `daohua.player.avatar`）持久化本地偏好；`InsightPage`（悟道面板快捷换装）、`CombatArena`、`VectorCharacter`、`SettingsPage`（道貌仪容视觉化选择卡片）全面接入并同步 i18n。
  - 验证：`npm --prefix frontend run build` 通过（耗时 5.67s，0 错误 0 告警）。

### TASK-20260822-13 服务端领域数据架构与事务机制基于 YuoCore 原生重构方案
- 分类：架构重构 / 领域模型 / YuoCore 原生集成 / 事务模型
- 时间：提出 2026-08-22 19:51:13｜完成 2026-08-23 00:58:27
- 状态：✅ 已完成
- 摘要：
  - 基于 YuoCore 哲学（Fiber 独占单线程 World、Actor 跨边界、差量同步、异步 Persistence）重构四层数据体系：强类型 ID（`ItemId`/`AbilityId`/`PlayerId`）、Definitions 只读配置层（`sealed record` + `FrozenDictionary`）、`SimulationEngine.AdvanceTo(target)` + `LastSimulatedAt` 唯一时间权威（保留 shard PriorityQueue + generation 调度，Fiber mailbox 只投递命令）、显式 `SnapshotEnvelope` 快照（含 `SnapshotSchemaVersion`/`GameDataFingerprint`/`PlayerStateVersion`，旧快照直接拒绝不迁移）+ PostgreSQL 单调版本 CAS durable outbox、挂单/撤单改 Actor 消息请求-应答与 Saga 补偿；命名规约：业务前缀法（`Task*`/`Executor*`）+ ECS 后缀法（`*Component`/`*System`）。关联规约：[`docs/backend/domain-data-architecture.md`](../../backend/domain-data-architecture.md)、[`docs/backend/logic-development-guide.md`](../../backend/logic-development-guide.md)、[`docs/临时设计讨论/任务队列系统与组件设计草案.md`](../../archive/discussions/任务队列系统与组件设计草案.md)。
  - 关键决策：废除固定周期定时轮询，改自适应 Channel 零轮询事件驱动；达到 `MaxTransitionsPerAdvance` 时保留已消费 cursor 返回可续算状态（修正原「达到保护上限仍无条件写入目标时间」的跳跃缺陷）；战斗引入 `BattleSnapshot` 固定入场快照与 `Applied*` 幂等累计字段（`AppliedConsumedItems`/`AppliedWins`/`AppliedDamageDealt`/`AppliedExperience` 等 11 项）防重放/重启重复结算；落地薄 ECS 纯数据组件（`ActionQueueComponent`/`InventoryComponent`/`CultivationComponent`/`CombatResourcesComponent`/`PlayerStatsComponent`/`SyncDirtyComponent`）与唯一 `ActionTransitionSystem`（修复调度误传 `LastSimulatedAt` 致推进为 0、补齐 `stats.CultivationCycles` 累加与 `DirtySections.World` 脏段标记）；硬约束：本地开发阶段兼容清零、禁双轨/Fallback/过度防御、测试最小充分。
  - 验证：`Domain.Tests` 167 项、`Server.Tests` 22 项（含 `PostgresSnapshotCasTests`/`StateSerializerEnvelopeTests`）全部通过，Release 构建 0 警告 0 错误，DataTool 配置指纹 `04beb1d434d651c9`，容量门禁实测（5000 玩家账号存储、720,000 次状态跃迁、1781.009ms 补算、errors 0，均在 2c4g/40g 预算内），前端构建通过（自动导出 957 条 i18n 词条模板）。

### TASK-20260822-12 全量非储存字符串强类型化与零 GC 优化全面落地
- 分类：架构重构 / 类型系统 / 性能优化 / 协议精简
- 时间：提出 2026-08-22 19:14:23｜完成 2026-08-22 19:30:45
- 状态：✅ 已完成
- 摘要：
  - 基于 TASK-20260822-10 扫描成果全面落地强类型化：新增 `CombatActionKind`（Attack/Cast/Control/Wait/Respawn）、`EquipmentSlot` + `[Flags] EquipmentSlotMask`（0-GC 单指令位运算穿戴冲突检测）、`UnitRank`、`ItemCategory`、`NearbyEntityKind`、`VisualRendererKind`、`MarketResultCode`、`ExpeditionPhase` 等领域枚举。
  - `CombatSimulator.cs`/`CombatGroupSimulator.cs` 全量迁移 `CombatActionKind`；装备槽校验迁移为位掩码；`OrderBook.cs`/`MarketEngine.cs`/`MarketCoordinator.cs` 统一返回 `MarketResultCode`；`StateMapper.cs` 强类型分发，`Enums.cs` 实现零堆分配自定义多语言 JSON 转换器（同时支持中文与 kebab-case/camelCase）；前端 `types.ts` 全面收拢联合类型并修正 `InsightPage.tsx`/`CombatArena.tsx`/`App.tsx` 的类型与分支比对。
  - 验证：135 项领域单测 100% 绿灯，容量门禁（5000 账号 / 500 峰值命令包 / 2000 冷挂机）与前端 `tsc`+`vite build` 全部通过。

### TASK-20260822-11 初始化项目级专职 Agent 模板规范与配置
- 分类：工程协作 / Agent规范 / 团队研发效能
- 时间：提出 2026-08-22 18:44:23｜完成 2026-08-22 18:50:00
- 状态：✅ 已完成
- 摘要：
  - 依据 `AGENTS.md` 多 Agent 并行开发规则，在 `.agents/agents/` 下构建 5 大专职 Agent 配置：`domain-dev`（领域/ECS 纯度、确定性、double 精度、测试覆盖）、`frontend-dev`（React 19、公共组件收拢、TypeScript 严格检查、同源通信）、`data-balancer`（定量预算论、属性权重、技能/装备/怪物平衡）、`qa-tester`（领域单测、LoadTests 压测、5000 账号容量门禁）、`ops-architect`（Docker 多阶段构建、非 root 安全、PostgreSQL CAS 账本与备份）。
  - 验证：`scripts/verify.sh` 全量通过（Release 构建、135 项单测、数据配置校验、2c4g/40g 容量压测、前端打包）。

### TASK-20260822-10 全量扫描所有非储存的字符串变量与强类型重构可行性分析
- 分类：架构优化 / 类型系统 / 代码扫描与分析
- 时间：提出 2026-08-22 18:42:38｜完成 2026-08-22 18:45:50
- 状态：✅ 已完成
- 摘要：
  - 3 个并行扫描 Agent（Domain / Protocol & Frontend / Server & Infra）完成全量非储存字符串扫描，输出分类可行性矩阵与三期重构路线图；核心收益预估：WebSocket 包头由字符串变 1 字节枚举、高频 Delta 包体可缩减 30%~45%、战斗模拟主循环 0 字符串 GC。
  - 关键结论：`Envelope.Kind`/`IncomingPacket.Kind`、客户端 `command`（14 种指令）、30+ 处硬编码错误码（建议 `ProtocolErrorCode : ushort`）、战斗触发器三要素 `CombatTriggerDto.Dependency/Condition/Comparator`（单条 Trigger JSON 约 80 字节可降至约 15 字节）、`ExpeditionProgressDto.Phase` 等协议字段枚举化；领域侧 `CombatActionState.Kind`（"attack"/"cast"/"control"/"wait"/"respawn"）、装备槽位（`HashSet<string>` 校验改 `[Flags] EquipmentSlotMask` 位运算）、属性权重字典 Key（30+ 字符串别名改 `StatAttributeKey` + 数组下标 O(1)）、`UnitRank`、`ItemCategory`、`NearbyEntityKind`、`VisualRendererKind`、`MarketResultCode` 枚举化；基础设施热路径零 GC（`AccessControl` 令牌比对 `stackalloc byte[128]`、`HttpCaching` ETag `ReadOnlySpan<char>` 解析、`AccountStore` 密码哈希 Span 切片、`PostgresStores` 载荷编码枚举化）；远期 Phase 3 规划 Strongly-Typed IDs（`ItemId`/`ActionId`/`AbilityId`/`EncounterId`/`NodeId` 等 `readonly record struct` 包装）。
  - 路线图：Phase 1 零破坏高性价比核心收拢 → Phase 2 协议紧凑化与数值解析强类型化 → Phase 3 强类型 ID。

### TASK-20260822-09 增加 Constants 常量目录并全面收拢领域静态常量
- 分类：架构规范 / 目录重构 / 领域常量体系
- 时间：提出 2026-08-22 18:31:45｜完成 2026-08-22 18:37:05
- 状态：✅ 已完成
- 摘要：
  - 新建 `src/Domain/Constants/` 作为领域静态常量唯一根目录：`CombatConstants.cs`（战斗数值、基础属性锚点、境界突破经验表、六维根基折算系数、公式平滑常数）、`IdConstants.cs`（`ActionIds`/`ItemIds`/`BuffIds`/`CombatEncounterIds`/`WorldMapIds`/`WorldNodeIds`）、`SystemConstants.cs`（`CombatLimits`/`SimulationConstants`/`MarketConstants`）。
  - `Models/` 纯粹化：仅保留数据结构、`Enums.cs` 枚举与 `PercentValue` 等值对象（`PercentValue`/`NonPlayerUnitTier` 收纳至 `CombatModels.cs`）；清理原 `Ids.cs` 与 `Simulation/CombatConstants.cs`，全局更新 Domain/Server/LoadTests/测试的命名空间引用。
  - 验证：135 项单测、容量门禁与前端 Vite 构建全数通过。

### TASK-20260822-08 所有 C# 源码文件增加顶部功能描述注释 (多 Agent 并行)
- 分类：工程规范 / 代码文档 / 注释规范
- 时间：提出 2026-08-22 17:37:31｜完成 2026-08-22 17:40:35
- 状态：✅ 已完成
- 摘要：
  - 5 个并发 Subagent 为 `src/` 与 `tests/` 下全部 63 个 `.cs` 文件补充统一格式的顶部中文功能描述注释（文件名称/主要职责/核心逻辑要点，置于 `using` 之前），严禁修改 `vendor/YuoCore`。
  - 分工：Domain Models/GameData（7+3 文件）、Market/Simulation（5+9）、Protocol/ComputeNode/DataTool/Server.Infrastructure（1+1+1+8）、Server.Runtime/Program（10+1）、LoadTests/Domain.Tests（8+9）。
  - 验证：`scripts/verify.sh` 门禁通过（Release 编译、135 项领域测试、游戏数据校验、2c4g/40g 容量目标压测、前端构建）。

### TASK-20260822-07 所有运维与开发脚本增加顶部职责说明中文注释
- 分类：工程规范 / 运维开发工具 / 注释规范
- 时间：提出 2026-08-22 17:05:35｜完成 2026-08-22 17:07:50
- 状态：✅ 已完成
- 摘要：
  - 为 `scripts/` 与 `deploy/` 下全部 12 个脚本（Bash/Node.js ESM/Python：`verify.sh`、`dev.sh`、`dev-hot.mjs`、`dev-service.sh`、`docker-build-prod.sh`、`generate-combat-benchmark.py`、`install-ccr-auth.sh`、`smoke-production.mjs`、`deploy-prod.sh`、`backup.sh`、`ccr-auto-update.sh`、`install-caddy.sh`）添加顶部中文职责说明注释（职责步骤、参数/环境变量依赖、使用场景），保持 Shebang 位于第一行。
  - 验证：`bash -n`、`node --check`、`python3 -m py_compile` 语法检查与 `scripts/verify.sh` 全量测试/构建/容量门禁全部通过。

### TASK-20260822-06 多 Agent 并行开发规则与 Tasks 详情独立分档规范落地
- 分类：工程协作 / 规则规范 / 文档架构重构
- 时间：提出 2026-08-22 16:11:44｜完成 2026-08-22 16:13:45
- 状态：✅ 已完成
- 摘要：
  - 在 `AGENTS.md` 写入两条强制规则：任务可拆解、模块相对独立且无写冲突时优先多 Agent（Subagents）并行开发；任务详情统一存放 `docs/tasks/TASK-YYYYMMDD-XX.md` 独立文档，主看板 `docs/tasks.md` 仅保留概览、状态流转、归档总表与路线图。
  - 创建 `docs/tasks/` 目录并迁移历史任务 TASK-20260822-01~05 至独立详情文档，重构主看板为轻量索引，同步 `docs/README.md` 目录导航。

### TASK-20260822-05 数据类型与模型类全面收拢至统一 Models 目录
- 分类：架构规范 / 目录重构 / 领域模型
- 时间：提出 2026-08-22 16:11:40｜完成 2026-08-22 16:17:30
- 状态：✅ 已完成
- 摘要：
  - 新建 `src/Domain/Models/GameDataModels.cs` 收拢全部配置/快照/推导数据类型（`ActionConfig`、`ItemConfig`、`ConsumableConfig`、`BuffEffectConfig`、`EquipmentConfig`、`AbilityConfig`、`CombatantConfig`、`MonsterSkillConfig`、`CombatEncounterConfig`、`CombatSpawnConfig`、`MapConfig`、`MapNodeConfig`、`GameDataSnapshot`、`GameDataManifest`、`CharacterStatRequest`、`ResolvedCharacterStats` 及 `BudgetAttributeWeight`/`BudgetModelDefinition`/`BudgetExpectedAttributes`/`FinalExpectedPlayerAttributes` 等预算模型）；`MarketModels.cs` 由 `src/Domain/Market/` 迁移至 `src/Domain/Models/`。
  - `GameData/`、`Market/`、`Simulation/` 仅保留解析器、调度器、引擎、目录服务等纯业务逻辑类；统一修正 Server 与 Domain 各引用命名空间，保持向后兼容。
  - 验证：135 项单测、容量门禁与前端 Vite 构建全数通过。

### TASK-20260822-04 抽取通用角色数值解析类（供怪物、NPC与召唤物复用）
- 分类：领域模型 / 数值系统 / 架构重构
- 时间：提出 2026-08-22 16:05:12｜完成 2026-08-22 16:09:00
- 状态：✅ 已完成
- 摘要：
  - 抽取通用角色数值解析类 `CharacterStatResolver`（含 `CharacterStatRequest`/`ResolvedCharacterStats` DTO），供怪物、后续 NPC 与召唤物（Summons/Minions/Pets/Puppets）统一复用；入参标准化：等级（Level）、品阶（Tier）、生命层次（Rank：minion/elite/boss/leader/summon 等）、流派与类型（Style/DamageType）、权重字典（Weights）、显式覆盖项与向下取整开关（`Floor` 默认 `true`，可传 `floor: false` 保留 double 精度）。
  - 关键规则：14 项基础属性权重缺省默认全 `1.0`（空权重字典/空配置也能完整解算全部 14 项基准数值）；`DataBudgetResolver.ResolveCombatant` 重构接入该解析类；新增 `CharacterStatResolverTests.cs`。
  - 验证：135 项领域单测、2c4g/40g 容量门禁与前端 Vite 构建通过。

### TASK-20260822-03 全项目多模块增加中等详细度中文注释
- 分类：工程文档 / 代码规范 / 注释重构
- 时间：提出 2026-08-22 15:44:55｜完成 2026-08-22 15:53:50
- 状态：✅ 已完成
- 摘要：
  - 5 个 Subagent 并行为全项目补充中等详细度中文注释（覆盖战斗模拟、快照持久化、市场撮合、状态管理、WebSocket 通信等核心算法流程），严禁改变任何业务逻辑或类型签名：Domain+Protocol 23 文件、Server/ComputeNode/DataTool 21 文件、领域单测与 LoadTests 16 文件、前端核心/网络/状态/日志 17 文件、React UI 组件与 Market 子组件 30 文件。
  - 验证：`scripts/verify.sh` 全局通过（Release 构建、122 领域单测、2c4g/40g 容量门禁、Vite 生产构建）。

### TASK-20260822-02 历史包袱、过渡兼容代码与双轨机制全面清理
- 分类：架构重构 / 协议与领域优化 / 运维基础设施
- 时间：提出 2026-08-22 15:04:30｜完成 2026-08-22 15:19:40
- 状态：✅ 已完成
- 摘要：
  - 3 个独立 Agent 并行执行第二、三、四阶段清理：①快照瘦身、协议规范化与持久化层死代码清理（`Protocol`/`Server/Runtime`/`Server/Infrastructure`/`Domain/Models`/`Domain/Simulation`/`frontend/src` 的 game/logging/network）；②战斗单怪/多怪双轨收拢、过时公式清理与六维属性对齐（`CombatSimulator.cs`/`CombatGroupSimulator.cs`/`CombatFormula.cs`/`GameData.cs`/`encounters.json`/`monsters.json`）；③部署编排与配置更新、历史冗余文件规整（`compose.yaml`、`appsettings.Production.json`、`docker-build-prod.sh`、`ccr-auto-update.sh`、根目录 HTML 原型、`frontend-sim/`、`artifacts/`）。
  - 验证：`scripts/verify.sh` 全局通过（Release 构建、122 领域单测、2c4g/40g 容量门禁、Vite 生产构建）。

### TASK-20260822-01 任务面板与需求追踪机制建立
- 分类：工程协作 / 文档与规则规范
- 时间：提出 2026-08-22 15:01:20｜完成 2026-08-22 15:02:45
- 状态：✅ 已完成
- 摘要：
  - 建立 `docs/tasks.md` 任务面板，用于记录并时刻更新计划开发方向与实际进度；在 `AGENTS.md` 写入强制规则：所有非 Bug 修复类任务必须先分析提示词与需求写入任务文档，开发中持续更新任务列表与进度状态并精确记录时间；同步 `docs/README.md` 目录索引。


