# 任务归档 · 2026-09（2026-09-03 ~ 2026-10-01）

> 本文件由 2026-10-04 任务面板深度归档（TASK-20261004-06）生成：`docs/tasks/` 下 2026-10-01 及更早的任务详情文档已提炼合并至本档并删除原文件，完整原文可从 Git 历史找回。
> 本档共 117 项任务；条目按提出时间从新到旧排列。检索方式：按 `TASK-YYYYMMDD-XX` 编号全文搜索定位。

---

### TASK-20261001-10 竖屏 UI 专项适配：顶栏/行动队列收拢 + 全界面竖屏巡检
- 分类：（原文未标注）
- 时间：提出 2026-10-01 23:55:00｜完成：无（全部主要界面竖屏巡检完成并修复，持续回归中）
- 状态：✅ 全部主要界面竖屏巡检完成并修复；持续回归中
- 摘要：竖屏（390×780 等）顶栏与行动队列收拢：`QueueCenter` 竖屏只渲染待执行图标瓦片、循环/自动移动开关图标化（`Icon` 新增 `loop`/`travel`）；`styles.css` ≤960px 改单行三列，`.realm-progress` 取消 120px 定宽。以 Edge headless + CDP 移动仿真（390/360/320 宽）遍历全部界面，修复：详情页生活行动卡终止按钮独立行、战斗槽位托盘纵向堆叠、敌我血条上下两行、坊市下单弹窗、设置 320px 道号换行、触发器编辑器 tooltip 叠层（`.combat-slot-card` 补 `position:relative`）、「记录」面板补 `.records-open-mobile` 移动端入口、`FloatingPopover` 触屏 click-toggle 跳过业务控件场景、地图工具栏 320px 溢出、功法槽组 360px 换行等。复审/三审修复：`TaijiToggle.tsx` 补 `git add`；行动产出预览补 `buildingEffects` 公共建筑加成与 `baseDurationSeconds/(1+speedRatio)` 耗时折算（对齐服务端 `PrepareTimedActionCore`/`TryGetSustainedConsumable` 口径）；`startViewTransition` `ready`/`finished` 拒绝静默处理；无限循环「计划总量」显示 `∞`；太极开关对齐总纲 §13.1.1（`taiji-drift` 42s 缓旋 + 点击 +540°）。验证：`npx tsc -b`、`npm --prefix frontend run build`、`node scripts/check-frontend-contracts.mjs` 通过。

### TASK-20261001-09 顶栏太极主题快捷开关与圆形扩散换色过渡
- 分类：（原文未标注）
- 时间：提出 2026-10-01 21:58:00｜完成：无（🚀 进行中）
- 状态：🚀 进行中（归档时停滞）
- 摘要：顶栏可旋转太极图快捷切换深浅主题：`game/themeStore.ts` 的 `setTheme(theme, origin?)` 支持扩散圆心、新增 `toggleTheme(origin?)`，经 `document.startViewTransition` 在 `::view-transition-new(root)` 上做 `clip-path: circle()` 从点击点全屏圆形扩散（`cubic-bezier(0.22,1,0.36,1)` ~680ms）；不支持 View Transitions 或 `prefers-reduced-motion` 时直接切换。新组件 `components/TaijiToggle.tsx`：阴阳双色 SVG（阳面浅黄纸 #f8f4e4、阴面玄墨 #12100b，盘底取偏差中间色 #e7dcba/#252017），常态静止（2026-10-02 起取消自转），点击经 `--taiji-spin` 追加 180° 旋转。`App.tsx` 顶栏末尾挂载、`.topbar` 栅格增一列；`SettingsPage` 透传点击坐标；i18n 增加 `app.topbar.themeToLight/themeToDark`；设计文档 §13.1.1 补充。验证：`npm --prefix frontend run build` + 目检。

### TASK-20261001-08 浅色主题按「浅黄纸面 + 印章红」重做
- 分类：（原文未标注）
- 时间：提出 2026-10-01 21:11:25｜完成：无（🚀 进行中）
- 状态：🚀 进行中（归档时停滞）
- 摘要：浅色模式背景去多层径向渐变改平色纸底，配色参考父目录 `daojie-yusheng` 项目（`packages/client/src/styles/tokens.css` 与 `react-ui/styles/foundation.css`）的浅黄纸面 + 印章红体系。`frontend/src/styles/theme.css` 的 `[data-theme="light"]` 覆盖块：面板去金色罩光与浑浊投影；新增 `--seal` 印章红族（`--seal`/`--seal-deep`/`--seal-faint`/`--seal-border`），`--danger` 并入印章红；印章红接管实心主按钮、`::selection`、`:focus-visible`、checkbox accent-color 与全部 `.active/.is-active/.is-selected` 选中态；`theme-color` meta 三处（`index.html`、`public/theme.js`、`game/themeStore.ts`）同步 `#f1ecdc`；设计文档 `13-前端UI设计系统与修仙交互规范.md` §13.1.1 先行更新。只改前端样式层与文档，不动协议、组件结构与深色主题。验证：`npm --prefix frontend run build`（tsc + vite）+ 浅色目检。

### TASK-20261001-07 显示设置深浅主题切换与浅色对比度校准
- 分类：（原文未标注）
- 时间：提出 2026-10-01 18:00:00｜完成 2026-10-01 18:37:34（据进度日志）
- 状态：✅ 已完成
- 摘要：显示设置支持浅色/深色切换、默认浅色：复用 SettingsPage 分段按钮，主题经浏览器本地保存、首屏恢复，根节点主题属性统一控制色彩；浅色采用暖纸白背景、深墨文字、深金深翠修仙色（后按用户反馈调为暖灰纸底并保留原角纹/内描边/渐变层）。桌面 1440×1000 与移动 390×844 实测浅/深切换、选中态与刷新保存；内视六维、坊市 79 物品瓦片、地图、行动弹窗、真实战斗页完成目检与可见文本合成色扫描；首屏缺省/浅色/深色/非法存储/不可用 5 种边界通过。只做前端主题，不改协议、存档或服务器。验证：前端 build 与契约检查通过；仅提交主题增量（保留其他任务改动）；构建仍有既有入口 chunk 超 500kB 提示。

### TASK-20261001-06 命令回执浮动提示差异化与气泡丝滑化
- 分类：（原文未标注）
- 时间：提出 2026-10-01 17:37:51｜完成：无（实施完成，待 UI 目检）
- 状态：🚀 进行中（实施完成，待 UI 目检）
- 摘要：右下角 `Notice` 气泡此前对每条成功命令统一弹「命令已提交。」（服务端 `EcsPlayerRuntimeAdapter.ExecuteOwnedAsync` 回 `PlayerCommandResult(true, "OK", ...)`，前端 `gameClient.ts` 无条件弹泡），且覆盖市场专属提示。改为：服务端 `SuccessNotice` 静态映射按命令差异化文案（`ability.learn`「功法参悟完毕。」、`progression.breakthrough`「突破成功，境界晋升。」等 10 条），高频或界面已自证操作（行动入队/移除/排序、装备替换、`market.place`/`market.cancel`、`simulation.advance` 等）返回空串静默；前端 `.notice` 加淡入+上移入场动效（`transform/opacity` 走合成器），`gameStore` 增加 `noticeSeq` 计数绑定气泡 `key`，同文案重复提示重新计时并重播动画。范围：`src/Server/EcsPlayerRuntimeAdapter.cs`、`frontend/src/game/gameStore.ts`、`frontend/src/App.tsx`、`frontend/src/styles.css`、`docs/frontend/common-components.md`。验证：服务端 `dotnet build` 零告警、前端 `npm run build` 与契约检查通过、Ecs.Tests 56/56 全绿。

### TASK-20261001-05 境界与数值术语体系统一
- 分类：文档治理 / 术语规范 / 前端展示
- 时间：提出 2026-10-01 15:41:11｜完成：未标注（过程日志 2026-10-01 16:00+ 完成）
- 状态：✅ 已完成
- 摘要：用户裁定术语体系并全库统一：境界三层（大境界共 11 个：凡俗(Lv1~18)/练气…渡劫各 12 层/飞升(Lv127)；境界阶段=武玄天+初中后极；小境界=Lv.1~127 每级一名）；数值轴「等阶=T级」（等阶1~12，锚点 1/18/30/42/54/66/78/90/102/114/126/127）；怪物档位「生命层次」（妖兽/精英/妖王，属性倍率 80%/120%/150%）；物品品质八档 Q1凡品~Q8帝品（帝圣灵天地玄黄凡）；废除「阶位/位阶/品阶」等词。改动：`docs/design/总纲/` 19 篇 md 术语统一并经 `node scripts/sync-master-viewer.mjs` 重生成 index.html（19 篇 148936 字）；前端 i18n 与组件注释、`tools/config-editor`、`config/game-data/expected-attributes.json`、`src/` 9 处注释；`calculateRealmInfo()` 补 Lv.127 飞升特例；标识符与协议字段不改。验证：活跃源残留 grep 清零、契约检查通过、`npx tsc --noEmit` 零错误、DataTool `validate config/game-data --strict` 通过（27 文件/163 id）、Domain 构建 0 错误。

### TASK-20261001-04 行动队列、行动执行与战斗完整 ECS 收口
- 分类：（原文未标注）
- 时间：提出 2026-10-01 08:08:18｜完成：未标注（实施记录末条 2026-10-01 08:43:56，验证记录全过）
- 状态：✅ 已完成（据交付清单与验证记录；原文未标状态行）
- 摘要：行动队列/行动执行/战斗的生产实现统一走 YuoCore ECS，规则由组件状态与 Tag System 持有：行动执行规则收拢至 `src/Ecs/Systems/ActionExecutionRules.*`（`ActionClockSystem`/`ActionQueueSystem` 等直接驱动，普通 tick、批量补算、命令推进复用同一规则本体）；战斗收拢至 `CombatActionRules` 与 `CombatEventAdvanceSystem`，`DeterministicCombatEngine` 仅保留纯算法/测试门面；Buff 过期改 `PlayerBuffExpiryInboxComponent` + `PlayerBuffExpirySystem` Tag 派发；生产路径不再从 ECS System 调用 `SimulationEngine`/`DeterministicCombatEngine` 规则编排入口；纯公式、协议 DTO、PostgreSQL、HTTP/WebSocket、认证与持久化适配留在原层。验证：Release 构建 0 警告 0 错误；Domain.Tests 214/214、Ecs.Tests 56/56（含新增行动旅行边界、Buff 过期回归）、Server.Tests 167/167；`scripts/verify.sh` 通过、容量门禁 `gatePassed=true`；YuoCore 子模块固定 `7b36f8002850dbf06e482682ba27d32cf4a0abe6`。

### TASK-20261001-03 · Tag/System 业务统一化第一阶段
- 分类：架构重构 / ECS 规则本体化
- 时间：提出 2026-10-01 00:32:00｜完成 2026-10-01 01:24:10（验证完成时间）
- 状态：✅ 已完成
- 摘要：把 OOP 队列变更与统计副反应收敛到 ECS 同步执行入口：队列变化走 `PlayerActionQueueMutation` + 组件工作区（同一 tag 5 个 System 按准备/恢复/归档/结构提交/战斗释放执行）；统计反应走 `PlayerStatsDelta` + `PlayerStatsInboxComponent`（typed 增量载荷替代 `Action<T>` 回调，3 个 System 分别记行动/离线/每日账本，保持三账本顺序）；战斗相位 6 个既有 System 持有相位规则、共享 `CombatActSystem` 持有技能决策/施放/命中落账；`CombatantState` 成单位能力权威视图。ECS 运行时/适配器测试正式纳入 `scripts/verify.sh`；新增 13 个高价值回归用例。验证：verify.sh Release 0 警告；Domain.Tests 214/214、Ecs.Tests 52/52、Server.Tests 167/167；`capacity-target` `gatePassed=true`（实际 PostgreSQL 5000 账号、30000 sections、1500 latest-only queue upserts）；真实 ECS 一小时采集补算：单分片顺序推进 2000 名玩家 2.097 秒、进程工作集约 114.8 MiB、逐位一致；新进程烟测（端口 15141）通过。源码口径由 22 System/7 tag 变为 30 System/9 tag（自定义 tag System 40.9%→56.7%）。后续切片见 TASK-20261001-02。

### TASK-20261001-02 · YuoCore tag 驱动 System 使用占比与 OOP 业务统一架构适配审计
- 分类：只读架构审计 / ECS 设计
- 时间：提出 2026-10-01 00:09:20｜完成 2026-10-01 00:30:57（审计完成时间）
- 状态：审计已交付；与对应第一阶段功能一并提交
- 摘要：实施前只读审计（YuoCore 指针 `7b36f8002850dbf06e482682ba27d32cf4a0abe6`）：静态统计 22 个游戏 System、7 个自定义业务 tag、20 个命令名——IUpdate 13（59.1%）、战斗相位 tag System 6（27.3%，全部转调引擎方法）、命中能力组件+tag System 3（13.6%）、自定义 tag 合计 9（40.9%）；专属命令 tag System 为 0。关键证据：27 个 JSON 配表中 `hpDrainPoints`/`mpDrainPoints`/`damageReflectPoints` 出现次数均为 0（3 个反应组件未被默认配表启用）；`CombatantComponent.Stats`/`SkillStateComponent`/`BuffComponent` 是平行数据壳；批量快速路径 `AdvanceBulkTimedUntilUnsupported` 绕过 `YuoSystem.Run` 会漏掉仅挂 Update 层的新 tag 反应。给出 6 项统一建议顺序（队列变更、统计回调、资源边界/Buff 到期、战斗相位、装配习得突破、节点/建筑投影）与不宜迁入 System 的边界（WebSocket、PostgreSQL、市场 fence 事务、纯算法等）。登记文档偏差（`ecs-development-guide.md` 若干条目）。纯文档交付，未改业务代码；后续落地见 TASK-20261001-03。

### TASK-20261001-01 行动详情弹窗产出按轮合并显示 + 引气入体配表残留收益清理
- 分类：（原文未标注）
- 时间：提出 2026-10-01 00:01:58｜完成：无（实施完成，待 UI 目检）
- 状态：🚀 进行中（实施完成，待 UI 目检）
- 摘要：行动详情弹窗「产出」区对 `cultivation.draw-qi` 同资源分裂三颗胶囊且单位混用。根因：`config/game-data/actions/cultivation.json` 的 `experience 1/秒` 是灵气池废案残留（TASK-20260930-01 要求移除 `kind:"qi"` 收益却误改名），修为实际入账 20/轮；`mana 1/轮` 与设计口径不符。用户裁定：每轮产出口径修为 10 + 灵力 10，收益一轮完成后一次性入账。修正：配表三处同源（`cultivation.json`、`GameDataCatalog.Default`、`frontend/src/game/ids.ts`）统一为 `experience 10/轮 + mana 10/轮`；前端 `calculateActionYieldPreview` 的 `resourceYields` 按 kind 聚合为单轮总量、胶囊渲染 `×N`（加成后值，1 位小数）；清理 i18n `yieldPerSecond`/`yieldPerRepeat`；同步 `06-持续消耗品` §6.4.1/§6.4.2/§6.7、`logic-development-guide.md`、总纲 index.html 镜像；13 项受影响断言修正。验证：Domain.Tests 214/214、Ecs.Tests 39/39；前端 tsc/build/契约/DataTool strict 通过；`/api/v1/catalog/actions` 实测返回 experience 10/轮 + mana 10/轮；Server.Tests 37 项失败均为既有 PG 环境缺失。

### TASK-20260930-15 · ECS 化彻底化收尾：对齐三版本对比任务书的 ECS 缺口（2026-09-30）
- 分类：服务端架构 / ECS 深化 / 领域模型
- 时间：提出 2026-09-30 23:05:00｜完成：未标注（状态行注明全绿与端到端冒烟通过）
- 状态：✅ 已完成（Domain 214/Ecs 39/Server 167 全绿，端到端冒烟验证通过）
- 摘要：对照三版本对比任务书补齐 ECS 结构性缺口，四阶段交付：①命令去聚合往返——删除 `EcsActionState` 平行类型统一 `QueuedAction`，`PlayerAggregate` 新增 `Wrap` 视图工厂（分段字段直接引用组件 POCO），删除 `CaptureAttributeRevisions`/`ContinueAttributeRevisions` hack；②Update system 工作区组件化——`EcsActionPipeline` 并入 `EcsActionExecutionComponent`，`EcsActionSession` 静态服务承载跨阶段设施；③战斗相位 tag 链——新增 `CombatPhaseDispatch`（ICombatPhaseDispatcher + Ecs/Inline 双路）与 `CombatPhaseSystems` 六相位经 `YuoWorld.RunSystem<tag>(entity)` 派发，`CombatEncounterComponent.PhaseContext` 承载上下文；④MapNode 实体化——`NodePopulationComponent.PresentPlayerEntityIds`、`EcsWorldShard.SyncNodeMembership` 统一维护点、`NodeBuildingComponent` 镜像投影、`IPlayerResourceContext.NodeEncounterIds` 默认接口成员。验证：Domain.Tests 214/214、Ecs.Tests 39/39（含 4 个新节点测试）、Server.Tests 166/167（唯一失败为既有协议漂移）。剩余：`ProtocolAttributesPointsTests.ToSnapshot_InspectedEnemy` 漂移待独立修、300 人容量压测需部署环境。

### TASK-20260930-14 · 本地开发域名入口：Docker 化 Caddy 反代 daohua.localhost（2026-09-30）
- 分类：开发工具链 / 本地开发体验 / 反向代理
- 时间：提出 2026-09-30 23:08:58｜完成 2026-09-30 23:2x
- 状态：✅ 已完成（2026-09-30 23:2x）
- 摘要：本地开发免端口访问：新增 `deploy/caddy-local.Caddyfile`，`http://daohua.localhost`（RFC 6761 自动解析回环，HTTP 80 不引入本地 CA）反代到 `host.docker.internal:{$DAOHUA_FRONTEND_PORT:15172}`；因本机未装 Homebrew 改用 Docker 化 Caddy（`docker run -d --rm --name daohua-local-caddy -p 80:80`，免 sudo），`scripts/dev.sh` 在前端就绪后拉起、Docker 不可用/80 被占用时跳过并提示不阻断，`cleanup` 用 `docker stop` 级联清理；`DAOHUA_LOCAL_PROXY=0` 可显式关闭；本地 Caddyfile 不复制生产 `/ops`、`/api/metrics`、`/readyz` 私有路径拦截（生产仍以 `deploy/daohua.yuohira.com.caddy` 为准）。文档同步 README/AGENTS。实测：`http://daohua.localhost/` 200、`/ops` 200、`/ops/api/overview` 200、`/api/v1/meta` 200，链路 Caddy(80) → Vite(15172) → 后端(5128) 全通。

### TASK-20260930-13 · 运维面板游戏化重写：React 承载 + 全功能运维视图与写操作（2026-09-30）
- 分类：开发工具链 / 服务端运维面板 / 前端重写 / 全栈新功能
- 时间：提出 2026-09-30 22:38:56｜完成：无（🚀 进行中，待面板 UI 目检）
- 状态：🚀 进行中（归档时停滞；服务端/前端改造与旧形态退役已完成）
- 摘要：运维面板由 `OpsPage.html` 嵌入资源单页（独立监听 0.0.0.0:5129、严格只读）重写为 React 页面并入 5128 主端口 `/ops`，写操作用一次性令牌保护、文案硬编码中文。设计真源：[`docs/backend/ops-panel.md`](../../backend/ops-panel.md)。服务端：`OpsOptions` 移除独立端口并新增一次性令牌（未配置时随机生成、不随 ToString 泄露）；`OpsPanel` 扩展为 14 项只读视图 + 4 项令牌保护写操作（额外要求回环/私网来源）；新增 `OpsStorageInsights` 只读查询、`OpsLogBuffer` 类别/关键词过滤；`Program.cs` 移除端口追加与 `UseOpsGate`。Server.Tests 中 Ops 相关 31 项全过（新增 `OpsWriteEndpointTests` 7 项）；实测 11 个只读端点 200、旧端口 5129 关闭、无令牌/错令牌 401。前端：新增 `frontend/src/ops/`（外壳 + 展示原语 + 数据客户端 + 五组视图），复用游戏全局样式与 `Modal`/`ConfirmDialog`；`main.tsx` 按路径分流并以 `import.meta.env.DEV` 门控（生产产物不含面板 chunk）；Vite 新增 `/ops/api` 代理。旧形态退役：`git rm src/Server/Clean/Ops/OpsPage.html`。待办：面板 UI 目检。

### TASK-20260930-12 前后端并行潜在问题排查
- 分类：（原文未标注）
- 时间：提出 2026-09-30 21:00:48｜完成：无（仅完成环境核对与任务登记）
- 状态：🚀 进行中（归档时停滞，三路并行审计未完成）
- 摘要：对当前工作区完整实现（含未提交改动）沿正式前端、协议、服务端宿主、ECS 与 PostgreSQL 真实调用链并行排查潜在问题，交付可复现或有明确代码证据的风险清单（位置/触发条件/影响/验证结果）。规划分工：前端（账号切换、网络重连、快照合并、战斗详情、市场与输入交互）、后端（鉴权、WebSocket 生命周期、市场与持久化失败边界）、跨层（协议一致性、ECS 文档往返、队列与关键业务不变量），最后做 Release 构建、测试、前端构建、契约检查与结论复核。进度仅完成 Git/submodule、工具链、监听端口核对与任务登记。

### TASK-20260930-11 物品类型两级化：主类型 + 子类型配置，丝带显示子类型
- 分类：（原文未标注）
- 时间：提出 2026-09-30 19:09:24｜完成：无（仅需求登记，开始实施）
- 状态：🚀 进行中（归档时停滞，仅需求登记）
- 摘要：物品类型拆两级配置：配表新增 `subCategory`（子类型，可选）字段、`category` 归一主类型——`material`→herb/mineral/material、`consumable`→pill/alchemySupply/explorationSupply、`manual`→manualTome 秘籍/manualFragment 残卷、`currency` 无子类型、`equipment` 子类型即 `slot`。Domain `ItemDefinition` 新增 `SubCategory`，`ParseItem`/`StaticGameCatalogMapper` 全链透传，药品栏门控改判 `SubCategory == 'pill'`；前端 `InventoryItem.category` 更名 `subCategory`，`resolveItemSubCategory` 改显式字段解析（`eqDef.slot ?? def.subCategory ?? fallback`）移除 ID 前缀启发式；左上角丝带统一显示子类型，悬浮详情「品类」行显示「主类型 · 子类型」；坊市一级 Tab `pill` 归并为 `consumable`、子 Tab 按子类型过滤。范围覆盖 `config/game-data/items/*.json`、Domain/Server 映射、前端组件与 i18n、`docs/backend/game-data.md`、`docs/frontend/common-components.md`。

### TASK-20260930-10 本地开发存档版本漂移自愈
- 分类：（原文未标注）
- 时间：提出 2026-09-30 18:45:10｜完成：未标注（v3 定稿实施并实测完成）
- 状态：✅ 已完成（v3 定稿实施并实测；原文未标状态行）
- 摘要：解决本地库旧 `schema_version` 存档反复抛「section schema 版本不受支持」。⚠️ 事故记录：v1「检出即自动清零」未迁移、未备份、未确认直接删除 56 名玩家（含真实账号）游戏域数据且不可恢复（无 base backup，WAL 单独不可 PITR）；v2 曾将存档迁移独立成 `NNNN_save-vX_to_vY.sql` 双轨；v3 用户定稿：单一迁移链 `migrations/NNNN_vX_to_vY.sql`，`platform.schema_version` 升格为「本地库内容版本」，`SectionCodec.CurrentSchemaVersion` 升级时 `PostgresSchema.Version` 同步 +1 并在迁移文件内嵌 `UPDATE ... SET schema_version`；`migrations/0004_v4_to_v5.sql` 示范（`player_seq` + 存档标签 v4→v5）。`scripts/reset-outdated-players.sh` 重构：检测存档行漂移或 platform 落后→自动调用 `migrate-local-db.sh` 迁移链→复检仍漂移才 `/dev/tty` 交互确认（输入 RESET 才重置；拒绝或无 TTY exit 2）；`dev.sh` 处置失败一律中止启动；`migrate-local-db.sh` 补本机 psql fallback；AGENTS.md 规则修订（清零前先迁移、交互确认、禁止无人值守删除）。实测：platform=4 + v4 存档自动走 0004 链一步迁齐（→5）；platform=5 + v3 存档脱钩无 TTY 正确拒绝（exit 2）。

### TASK-20260930-09 坊市挂单弹窗交互修复与盘口列表精简
- 分类：（原文未标注）
- 时间：提出 2026-09-30 18:30:11｜完成：无（实施完成，待 UI 目检）
- 状态：🚀 进行中（实施完成，待 UI 目检）
- 摘要：三处修复：①`QuantitySelector` 新增 `leadingPresets`（输入框左侧「1」档位钮）与 `maxShortcut`（右侧「最大」钮，买按持有灵石/价、卖按持有数量自适应，max=`quantityLimit`），词典加 `market.selector.max`；②修复挂单弹窗输入焦点被打断——根因链为 `MarketPage` 内联 `onClose` 每次渲染新引用 → `MarketOrderModal`(memo) 失效 → `Modal` 的 `useEffect` 反复 cleanup → `previouslyFocused?.focus()` 拉回焦点；修复为 `Modal` effect 内 `onClose` 经 ref 调用、`MarketPage` 改 `useCallback`，并将初始化价格改 `useState` 惰性初始化、删除依赖 `bestAsk/bestBid` 的初始化 effect（行情 3s 轮询不再重置输入）；③`MarketBookPanel` 删笔数列、价格列 `tooltipLabel` 改「单价」、`.market-level` 网格 3 列改 2 列（`MarketLevel.orderCount` 协议字段保留）。验证：`tsc -b` + `vite build` + `check-frontend-contracts.mjs` 通过、`template.csv` 全量重生成。

### TASK-20260930-08 物品角标细分类目与装备槽位重复角标清理
- 分类：（原文未标注）
- 时间：提出 2026-09-30 18:23:48｜完成：未标注（两阶段实施与验证完成）
- 状态：✅ 已完成（两阶段实施与验证完成；原文未标状态行）
- 摘要：第一阶段：`ids.ts` 新增 `resolveItemSubCategory(itemId, itemDef, eqDef, fallback)` 统一细分解析（`item.tome./item.fragment.` 前缀→manualTome/manualFragment、装备→`eqDef.slot`、其余→`itemDef.category`）；背包 `ItemCard`/`ItemGridPicker`/`ItemUseSlot`/坊市三处/悬浮卡片「品类」行统一走细分；装备面板删除已装备态 `.equipment-slot-badge` 节点与 CSS；词典补 `manualTome`=秘籍、`manualFragment` 改「残卷」及槽位键。第二阶段（两级模型重构）：配表 `category` 归一主类型 + 新增 `subCategory`；Domain `ItemDefinition` 新增 `SubCategory` 与 `DisplaySubCategory`（缺省回落主类型），药品栏门控改判 `DisplaySubCategory == CategoryPill`，`StaticGameCatalogMapper` 下发两字段；前端 `InventoryItem.category` 更名 `subCategory`、坊市一级 Tab `pill`→`consumable` + 消耗品二级 Tab（全部消耗品/丹药/补给耗材）。验证：`tsc -b`、`npm run build`、契约检查、`dotnet build`、Domain.Tests 214 全过；Playwright 实测背包丝带「货币/丹药/衣服/武器/饰品/头部/鞋履/下装/残卷/秘籍/矿石/药材」、`.equipment-slot-badge` 计数为 0。期间 `ids.ts` 曾因并行编辑出现大段重复块，已修复。

### TASK-20260930-07 行动队列循环悬浮说明 + 「自动移动」开关
- 分类：（原文未标注）
- 时间：提出 2026-09-30 18:18:56｜完成：未标注（进度日志注明任务完成）
- 状态：✅ 已完成
- 摘要：「循环」开关用 `FloatingTooltip` 补悬浮说明；新增「自动移动」：`ActionIntent` 新增 `AnchorNodeId`（入队时记录驻留节点，旅途中不登记，恢复/导出/ECS 往返全链保留）；`PlayerSettingsState.AutoTravelEnabled`（默认 false）全链扩展、分段编解码 schema v4→v5；`SimulationEngine.TryStartActionCore` 地点不符分支新增 `TryEnqueueAutoTravel`——开关开启、锚点仍在 `AllowedNodeIds`、`world.travel` 存在且可达时按 `PlanFromPosition` 插队一条 `world.travel` 至队首并挂起原行动（产出 `action.auto-travel` 事件），任一前置不满足回落 `action.blocked`；`IPlayerActionContext.EnqueuePriority` 接口化（`PlayerAggregate` 显式委托、`EcsPlayerActionContext` 组件直写，`incrementVersion: false`）。文档同步总纲 02 §2.4.2、18/19 篇及后端三文档。验证：Release 构建 0 警告；Domain.Tests 214/214（含新增 `AutoTravelTests` 4 用例）、Ecs.Tests 35/35；前端 tsc/build/契约全过；Server.Tests 154/160（6 项失败均与本任务无关）。

### TASK-20260930-06 设置界面游戏化改版
- 分类：（原文未标注）
- 时间：提出 2026-09-30 16:39:53｜完成：未标注（三轮迭代完成）
- 状态：✅ 已完成（三轮迭代完成；原文未标状态行）
- 摘要：设置页重构为经典游戏结构：左侧竖排分类栏（账号/显示）+ 右侧分组设置行（限宽约 640px），≤960px 左栏降级为顶部横向滚动分类条。二轮迭代：删除整体界面缩放（`.zoom-*`、`utils/uiScale.ts` 删除，浏览器缩放接管）；「仪容」独立为「形象」分类页（默认形象更名「女剑修」）；后端 `identity.players` 新增 `player_seq` 自增列（schema v4→v5，`migrations/0004_v4_to_v5.sql`，`auth register/login/session` 下发 `sequenceId`）；新增「账号安全」分组与 `ChangePasswordModal`（POST `/api/v1/auth/change-password`：校验当前密码、Serializable 事务更新 PBKDF2 凭据并吊销其他会话、写 `auth.account.password_changed` 审计事件）。三轮迭代：清理界面中设计/实现层说明性文本（登录页安全脚注、设置页说明段、战力预算行等），规则写入 AGENTS.md §8「UI 文本简练红线」。验证：`AccountAuthTests` 3 用例全过、Playwright 实测（含 480px 移动端）、i18n 模板重新导出 1456 条、构建与契约检查全过。

### TASK-20260930-05 · 战斗单位属性按需投影（观察目标订阅）
- 分类：协议扩展 / 快照带宽优化 / 前端体验
- 时间：提出 2026-09-30 15:35:17｜完成：无（🧪 联调/验证中）
- 状态：🧪 联调/验证中
- 摘要：`expedition.enemies[].attributes` 与 `expedition.duel.opponentAttributes` 由白名单全量投影改为按需投影：协议新增 `WireMessageType.UnitInspect`(24) + `UnitInspectDto{side,index}`（仅 WebSocket 上行火忘帧无回执，`{side:null,index:null}` 清除）；`WebSocketSession` 记连接级观察目标；`ProtocolSnapshotMapper` 全链穿线 `inspectedEnemyIndex`，属性块仅对命中下标投影，`maxHitpoints`/`maxManapoints`/`opponentMax*` 改常驻标量；前端 `gameClient.sendUnitInspect` + HelloAck 后重放、`CombatArena` 用 useEffect 同步 `inspectedActor`。完善为一次性交付+身份缓存：服务端 `_inspectDeliveryPending` 武装位与 `MarkInspectedAttributesDelivered`（编码后目标变更不解除、丢帧不解除自动重投）；前端相同目标去重与 `enemy:{enemyId}`/`duel:{对手名}` 身份缓存（重复点开即显不触发加载态）。玩家自身不变（已有 `WireAttributesChanged` 门控）。验证：ProtocolSnapshotMapperTests 8/8、服务端 build 零告警、前端 tsc/build/契约通过。

### TASK-20260930-04 · 战场单位详情弹窗新增「掉落」Tab
- 分类：前端体验 / 组件扩展
- 时间：提出 2026-09-30 12:23:54｜完成：无（🧪 联调/验证中）
- 状态：🧪 联调/验证中
- 摘要：`CharacterAttributeModal` 在基础/特殊/伤害增减三 Tab 外新增第四个「掉落」Tab，展示敌怪详细掉落信息（复用 TASK-20260930-03 的掉落复合胶囊）。`ids.ts` 新增 `findCombatantInCatalog(encounters, maps, enemyId)`：按遭遇主敌 → 刷怪池 → Boss 列表 → 各节点普通/精英/首领生态顺序查找 `CombatantDefinition`（仅供掉落等静态配表查询，实战数值仍只走 wire 快照）；`CombatAttributePanel` 新增可选 `drops` 属性，渲染 `ItemDropReference` 胶囊列表（复用 `.world-monster-drops-row`），空列表展示「没有掉落物」空态，未提供时维持原三 Tab（内视面板、玩家、斗法镜像不受影响）；i18n 新增 `insight.combat.groupDrops`/`insight.combat.dropsEmpty`；`common-components.md` §25 同步。验证：tsc 零错误、前端 build 与契约校验通过；待 UI 目检。

### TASK-20260930-03 · 物品展示统一为复合胶囊（图标+名称+×数量）
- 分类：前端体验 / 组件收拢
- 时间：提出 2026-09-30 12:06:47｜完成：无（🧪 联调/验证中）
- 状态：🧪 联调/验证中
- 摘要：将斗法暂存区「物品小图标 + 名称 + ×数量」复合胶囊（`ItemReference`）推广统一：行动统计/记录账本「物品得失」小节由 `ItemTileReference` 图标瓦片改为同款胶囊；怪物掉落预览改为 `兽皮 ×1-3（20%）` 形制（图标 + 名称 + `×数量区间` + 括号内概率）。实现：`ItemDisplay.tsx` 的 `ItemReference` 新增 `valueContent` 值区覆写；`ItemDropTile` 改名 `ItemDropReference` 薄封装胶囊；删除失去唯一调用方的 `ItemTileReference`；`reportSections.tsx` 的 `ReportItemsSection` 获得/失去两列改用 `ItemReference`；`CombatEcologyPanel.tsx`/`WorldMapPanel.tsx` 跟随改名；`selection-grid.css` 移除 8 处死锚点类、`expedition.css` 清理迷你瓦片样式；`common-components.md` §6/§24/§28 与 AGENTS.md §8 同步。验证：tsc 零错误、前端 build 与契约校验通过；待 UI 目检。

### TASK-20260930-02 · 手动消耗品批量使用落地
- 分类：全栈新功能 / 设计文档 / 协议扩展 / 前端交互
- 时间：提出 2026-09-30 11:19:47｜完成：无（🚀 进行中）
- 状态：🚀 进行中（归档时停滞；实施记录显示已完成）
- 摘要：手动即时消耗品批量使用：设计边界——槽位消耗品（丹药、符阵）不提供手动使用入口，只装配自动使用槽位；手动即时消耗品（技能书、装备、宝箱类）手动消耗，除装备外支持批量；交互复用 `QuantitySelector` + 二次确认数量弹窗。设计文档先行：`05-技能神通与秘籍残卷体系.md` §5.2 批量研读语义（秘籍 N 本：首本领悟、其余每本 +10000，已习得全量 +10000/本；残卷 N×500 须已习得，同事务原子）、`06-持续消耗品机制与生活制造体系.md` §6.2.4/§6.2.1 边界。服务端 `ability.learn` 支持 `quantity`（缺省 1；有限、≥1、整数校验，INVALID_QUANTITY 拒绝），逐本循环 `GrantAbilityExperience`，`AssetDelta` 按 `-quantity` 扣减；前端秘籍/残卷「参悟」与右键快用进数量弹窗（`ItemQuantityDialog` 新增 `use` 模式），装备维持即点即穿。测试：`EcsAdapterBoundaryTests` 新增批量研读用例（5 本秘籍未习得→领悟 +40000、3 本残卷 +1500、钱包整笔扣减）。验证：Ecs.Tests 35/35、Server.Tests 122/156（34 失败为既有 PG 环境缺失）、前端 build 与契约通过。

### TASK-20260930-01 灵气池废案整链移除与怪物击杀收益单参数化
- 分类：（原文未标注）
- 时间：提出 2026-09-30 00:05:00｜完成 2026-09-30 00:55:00
- 状态：✅ 已完成（2026-09-30 00:55）
- 摘要：灵气池（`Progression.Qi`/`LifetimeQi`）只有入账无玩法消费，整链移除：`ActionYieldKind.Qi`、行动 `kind:"qi"` 收益、怪物 `qiReward`、战报 `qi` 行、wire `qi`/`lifetimeQi`、持久化两字段（schema v4 内直接改形状不留兼容）、前端展示与词条（`Manapoints` 灵力法力池与 UI `tone="qi"` 保留）。怪物击杀收益合并单参数 `experience`（`qiReward`/`experienceReward`/`rootExpReward` 三字段废弃），同一数值多次结算：等额修为（经 `CreditExperience`：瓶颈截断 + 底蕴沉淀/兑换）、等额六维经验总量（默认六维均分 `N/6`，仅已装备内功 `discipline=internal` 按 `rootAttribute` 调整比例）、等额战斗经验；技能每次成功施放 `abilityExp` +1（`CompleteCombatAdvanceEventStream` + 斗法回放两处挂点，新增 `SkillUseGrantsOneAbilityExpPerCast` 测试）。验证：Domain.Tests 210/210、Ecs.Tests 32/32、Server.Tests 156/156、DataTool strict、前端 tsc+build+契约、`scripts/verify.sh` 全绿；总纲 02/06/07/09/17/19 与 `docs/backend/game-data.md` 同步。

### TASK-20260929-04 底蕴/战斗经验双池落地与命名审计
- 分类：（原文未标注）
- 时间：提出 2026-09-29 19:48:00｜完成 2026-09-29 23:24:11
- 状态：✅ 已完成（2026-09-29 23:24）
- 摘要：成长体系四池语义落地：修为（`Experience` 突破计量条，满层截断、突破清零）、六维经验、底蕴（`Foundation` 新增独立池：满层后修为 100% 转底蕴，可消耗底蕴使修为获取最高 3 倍，突破不清空）、战斗经验（`CombatExperience`：按增幅前基础量等额入账）。战斗对抗：`r = clamp(aCE/max(bCE,ε), 0.1, 10)`，r≥1 命中等效 `×min(2, 1+(r-1)/8)`、闪避 `×max(0.5, 1-(r-1)/16)`（r=5→150%/75%，r≥9 封顶 2 倍/0.5 倍），r<1 对 `1/r` 镜像；修正在破招/招架 ×2 之后、掷骰之前应用。实现：`PlayerProgression` 增 `Foundation`/`CombatExperience`/`LifetimeExperience`，`CreditExperience` 单点结算 `min(3·amp, amp+Foundation, max(0, cap-Experience))` 贯通三条入账路径；`CombatantStats.CombatExperience`（怪物按 `RealmProgressionCatalog.CumulativeExpFor(level)` 派生，斗法镜像冻结透传）；schema 升 v4；wire 下发 `foundation`/`combatExperience`/`lifetimeExperience`（历史错位键 `spirit` 更名 `experience`）；新增 `docs/design/总纲/19-名词术语对照表.md`；`ProgressionPoolTests` 18 用例。门禁：`verify.sh` 全绿——Domain.Tests 209/209、Ecs.Tests 32/32、Server.Tests 156/156、DataTool strict、2c4g/40g 容量门禁（真实 PostgreSQL/WAL）通过。

### TASK-20260929-03 斗法暂存区档位桶化与聚合展示
- 分类：（原文未标注）
- 时间：提出 2026-09-29 17:17:00｜完成 2026-09-29 17:31:00
- 状态：✅ 已完成
- 摘要：暂存区「一掉落一记录」随掉落实例线性膨胀（协议载荷/文档体积/全表扫描均 O(n)），改为档位桶化 + 按物品聚合：`DuelSettings` 增 `StagedBucketSeconds`（默认 300，5 分钟桶），同物品同桶合并为一条、每物品最多 4 批；`AcquiredAt` 向上量化到桶尾，入账时刻 = 桶尾 + `stagedSettleSeconds`，押存窗口 20~25 分钟绝不提前落袋（保住「押满 20 分钟可被缴获」下限）。`ActionReportBook.StageCombatItem` 增 `bucketSeconds` 参数；结算收拢为 `ActionReportBook.SettleStagedItems` 单一实现（删除 `PlayerAggregate.SettleStagedItems` 死代码）；暂存区最早到期时刻并入 `PrepareResourceBoundaryCore` 的 `naturalStep`；前端 `StatsPanel` 按 `itemId` 聚合展示。验收反馈后移除逐批结算倒计时、聚合行改 `ItemReference` 胶囊 flex-wrap 排列（17 篇 §17.5 同步为「不向前端展示结算倒计时」）。验证：Domain.Tests 191/191、Ecs.Tests 32/32、前端 build+契约、DataTool strict 全过；Server.Tests 122 通过、34 个因缺 `DAOHUA_MARKET_TEST_CONNECTION` 跳过。

### TASK-20260929-02 通用待选列表收拢：选择器由全屏模态改为贴槽浮层
- 分类：（原文未标注）
- 时间：提出 2026-09-29 11:35:22｜完成：无（🚀 进行中）
- 状态：🚀 进行中（归档时停滞；过程日志显示实现与验收修复已完成）
- 摘要：物品（`ItemGridPicker`）与功法（`AbilityGridPicker`）选择由全屏独立模态改为通用「待选列表」贴槽浮层：`FloatingPopover` 新增 `anchorEl` 外部锚点支持（定位、判外点击排除、滚动跟随以外部锚点为准）；新增 `frontend/src/components/CandidatePicker.tsx`（`anchor` + `options`（`CandidateOption{key,tile,wrapCell,searchText,disabled,ariaLabel}`）、首格空选、`auto-fill` 自动判列（最多 5 列、约 4 行滚动）、候选超 12 才显示搜索框、选中即回调关闭）；两个域选择器收拢为薄适配器；4 个调用点（`AutoUseSlotGroup`/`CombatLoadoutPanel`/`AbilitiesPanel`/`EquipmentPanel`）接线锚点与开关切换，正在设置的槽位加 `is-picking` 高亮；清理孤儿模态样式。修复验收反馈「浮层打开约一秒后突然移动」：入场动画 scale(0.96) 起始帧 `getBoundingClientRect` 返回缩小态矩形，改读未变换的 `offsetWidth`/`offsetHeight` 并补锚点脱离文档关闭保护。验证：`npm run build` 与契约检查通过。

### TASK-20260929-01 背包物品菜单文案简化 + 装备穿戴链路修复
- 分类：（原文未标注）
- 时间：提出 2026-09-29 09:42:53｜完成 2026-09-29 10:34:49
- 状态：✅ 已完成
- 摘要：物品菜单文案简化（`useEquip`→「装备」、`useLearn`→「参悟」、`trade`→「交易」，新增 `equippedBadge`→「已装备」）+ 装备穿戴链路修复。根因：服务端 `loadout.replace` → `ReplaceCombatSlots` → ECS 回写 → 属性解算链路经端到端复现确认正常，真正缺陷在前端 `EquipmentPanel.tsx`——已装备渲染分支要求 `equippedItem && definition`，而 `definition` 取自普通物品目录（`state.items` 不含 `equipment.*`）恒为 `undefined`，已装备槽位恒渲染为空。修复：槽位渲染条件改 `equippedItem && equipmentDef`（名称/品类/品阶/描述以装备目录为准）、`ItemPickerEntry` 新增 `equipmentDefinition`、`RightSidebar` 背包隐藏已穿戴 itemId、`CombatEquipmentDefinition` 补 `description`。衍生漏洞修复：`market.place` 卖出方向无已装备校验——`EcsPlayerRuntimeAdapter` 补 `ITEM_EQUIPPED` 校验、`MarketOrderModal` 禁止提交卖出挂单。验证：Server.Tests 122 通过（34 失败为既有 PG 环境缺失）、前端 build+契约通过、真实 WebSocket 复现 `loadout.replace` 回执 `succeeded: true`；`LoadoutEquipReproTests` 端到端复现测试。

### TASK-20260928-04 · 彻底 ECS 化执行架构设计：ECS 从调用层升级为规则本体层
- 分类：架构设计 / ECS 运行时 / 战斗触发器体系
- 时间：提出 2026-09-28 17:53:00｜完成：无（🚀 进行中）
- 状态：🚀 进行中（归档时停滞；实施已完成，遗留 300 人容量压测与 verify.sh 完整门禁待部署环境）
- 摘要：设计并实施彻底 ECS 化：运行时状态与业务规则本体落在组件与系统上，Domain 退化为纯库。四岔路与用户对齐：组件按反应性拆分（受击触发类 buff 一型一组件、纯属性修饰 buff 合并容器）；战斗内核「内核保留 + 段内触发器化」（`AdvanceTo` 批量窗口调度不动，段内规则经 `RunSystem<T>(entity)` 边界固定点同步派发）；分段落库格式不动、组件↔分段映射层扩展；一次性重构长分支；ComputeNode 统一换装 `EcsWorldShard`。产出 `docs/design/总纲/18-服务端ECS化执行架构与触发器规约.md`（§0~§12）。实施（feat/ecs-rule-core 分支）：组件重划（PlayerResources/PlayerStats/reactive buff 组件族）；SimulationEngine 六文件迁入 `src/Ecs/Engine`、DeterministicCombatEngine/DuelScriptEngine 迁入 `src/Ecs/Combat`；触发器域落地（PendingTriggers + TriggerSink、Inline/Ecs 双 sink 共用 `CombatTriggerMath`、OnHitLeech/ManaLeech/Thorns 标记组件与 `RunSystem<IOnHitTrigger>` 派发、`CombatTriggerSurface` 挂摘管理）；总纲 15 标废（18 继任）。门禁：Domain 190/190、Ecs 32/32、Server 155/155（含 PG）、golden vector 8/8 逐字节。

### TASK-20260928-03 · ECS 业务开发规约落地 + 全仓过期文档刷新
- 分类：文档 / 开发规约
- 时间：提出 2026-09-28 13:35:00｜完成：未标注（✅ 已完成，待提交）
- 状态：✅ 已完成（待提交）
- 摘要：沉淀 ECS 业务开发权威规约并全仓刷新过期文档：新增 `docs/backend/ecs-development-guide.md`（§0~§8 十章规约：分层定位、实体/组件模型、并发所有权铁律、`MutatePlayer` 命令通道契约、管线/批量推进规则、系统编写规范、持久化边界、新功能落地 checklist、验证门禁，全部对齐 `src/Ecs` 现行实现）；挂接 `docs/README.md` 索引、AGENTS.md §7、`logic-development-guide.md` 互链；过期表述清零（`PlayerActor`/`PlayerRuntime` 单写者、`frontend-sim`、`ServerEcs`、`ecs-current`/`ecs-optimized` 后缀等，历史档案保留原样）。过程中发现并修复迁移语义缺口：`IPlayerOwnershipStore`/`player_runtime_leases` 在 ECS 适配器只注册未消费，按 OOP `PlayerRuntime` 语义逐点恢复多实例写保护租约（Acquire/Renew/Release + `PLAYER_OWNERSHIP_LOST` fail-closed + 逐出）。验证：Release 构建 0 警告 0 错误；Domain.Tests 190/190、Ecs.Tests 30/30、Server.Tests 155/155（含 PG）全绿；过期引用终扫零残留。

### TASK-20260928-02：观测埋点编译期零成本化 + YuoCore 内核正式定名
- 分类：性能 / 架构收敛
- 时间：登记 2026-09-28 13:00｜完成：未标注（内核 7b36f80 已本地提交，父仓库随收敛提交）
- 状态：✅ 已完成（据提交记录）
- 摘要：埋点编译期零成本化：`Directory.Build.props` 新增 `DaoHuaObservability` 属性（`-p:DaoHuaObservability=false` 注入 `DAOHUA_OBSERVE_OFF;YUOCORE_TRACE_OFF`）；`RuntimeInstrumentation.cs` 与 `vendor/YuoCore/.../YuoCoreTrace.cs` 双实现（OFF 下 `Measurement`/`ScenarioScope` 的 Dispose 为空，`using var m = Measure(...)` 调用点经内联+死代码消除后零指令）；`Dockerfile` 两个 `dotnet publish` 均加该开关并补 `COPY Directory.Build.props`；验证 OFF 构建 `Domain.dll` 内不存在 `daohua.runtime.duration` 字符串。正式定名：`vendor/YuoCore-optimized` → `vendor/YuoCore`（`git mv` + `.git/modules` 迁移 + worktree/gitdir 指针修正 + `.gitmodules` 更新，上游分支仍为 `ecs-optimized`）；基准变体 `ecs-optimized` → `ecs`；SHA 钉更新 `7b36f80`（`bench-service.sh` `CORE_SHA` + `Bench/Program.cs` `ExpectedYuoCoreSha`）。验证：Release 构建 ON/OFF 均 0 警告 0 错误；Domain.Tests 190/190、ServerEcs.Tests 30/30、Server.Tests 155/155；内核 YuoCore.Game.Tests 10/10、YuoCore.Runtime.Tests 18/18。

### TASK-20260928-01 优化版 ECS 晋升生产核心架构：Server 换血 + 全量业务命令迁移
- 分类：（原文未标注；关联：TASK-20260924-09 三版本实验、TASK-20260927-04 300人收敛、TASK-20260927-08 内核正确性排查）
- 时间：提出 2026-09-28 10:30:00｜完成：未标注（代码/测试/文档全部收口）
- 状态：✅ 已完成（代码/测试/文档全部收口，提交推送见原文）
- 摘要：优化版 YuoCore ECS 从独立对照实验晋升为生产权威运行时：`src/Server` 重写为 ECS-optimized 宿主壳（`YUOCORE_OPTIMIZED` 常量、`YuoCoreRoot=vendor/YuoCore-optimized`），删除 OOP `PlayerRuntime.cs`/`Program.cs`；`EcsPlayerRuntimeAdapter` 补齐 OOP 18 命令完整命令面（`action.enqueue`/`loadout.replace`/`ability.learn`/`progression.breakthrough`/`world.travel`/`building.*`/`market.*`/`item.*`/`player.rename`/`simulation.advance` 等）；新增 `EcsWorldShard.MutatePlayer` 通用变更通道。运行时语义对齐修复：自然边界死循环（double 剩余预算亚 tick 残差→零步长不动点，改按 `min(remaining,needed)` 直接消费）、空闲玩家快照 delta（仅 `simulation.Changed==true` 推送）、`TouchedSections=null`（`DirtyMarks` 按段流水号差集）、`WireAttributesChanged` 恒 false（修订号续写 `prev+(post−baseline)`）。收敛清理：删除 `frontend-sim/`、基线 vendor/YuoCore submodule（794M）、`tests/EcsDifferential/`、`src/ServerEcs/` 并入 `src/Server`，全部变体机制（`YUOCORE_OPTIMIZED`/`EcsBuildVariant`/`YuoCoreRoot` 等）删除、所有 `ProjectReference` 回归普通直引。验证：Domain.Tests 190/190、ServerEcs.Tests 30/30、Server.Tests 155/155（含 PG）、对抗并发 50 客户端零污染、SnapshotDeltaPushTests 15/15。


### TASK-20260927-08 优化版 ECS 内核框架级正确性深度验证
- 分类：（原文未标注）
- 时间：提出（原文未标注）｜完成（原文未标注；文末含 2026-09-28 追加的生产级存档回放验证）
- 状态：（原文未标注）
- 摘要：对优化版 YuoCore ECS 做框架级正确性验证：基线非绝对 oracle、差分差异双向归属判定，基线 `vendor/YuoCore`(main) 不动；三线并行=静态语义审计（`main..ecs-optimized` 全 diff）+ 内核不变量测试 `tests/YuoCore.Ecs.Tests` 76 例（含 6 个 `// BUG:` 红例）+ 双内核差分台 `tests/EcsDifferential`（20 seed × 6000 ops）。优化版修复独有 4 缺陷（`_activeEntities` 快照不感知销毁、`EntityIndex` Dispose 中段释放致槽位互踩、`RunEntity` 无身份校验致差分 3918 幽灵回调、成员集残留死实体压实越界）与基线共有 7 缺陷（`DestroyComponent` 缓冲回放 `IDestroy` 被 `IsUsable` 吞掉、`[SingleComponent]` 二次 `SetComponent` 分裂视图、`systemsOfBaseComponent` 不清理致重复派发、已释放父实体 NRE、Dispose 重入、`ComponentStore.Set` 负索引、`world.OnDestroy` 退出 NRE）。差分残差全部有归属无未决项：exception 24 ops 全为基线侧 NRE（优化版 0 异常）、ghost-component 65、lifecycle-destroy ~60、callback-set ~9k、state ~9-12k、id-drift ~100k 级联、order-only/query/capability ~80。验证：`YuoCore.Ecs.Tests` 76/76 全绿（含原红例转绿 + 新增 4 回归）、Game.Tests 10/Runtime.Tests 18/StateSync.Tests 110 全过、daohua `tests/ServerEcs.Tests` 30/30、差分台优化版 0 异常且 stderr 无 `Game Exit NRE`（基线 13 seed 有）。提交：`vendor/YuoCore-optimized` ecs-optimized `1171ffd`→`79cdaeb`、`scripts/bench-service.sh` `CORE_SHA` 钉 `79cdaebf70ce2c59c33e08adae9f40fdd75e983c`、`tests/EcsDifferential/Ledger.cs` 增 `DIFF_EX_STACK=1` 排查辅助。遗留两版一致的共享语义：孤儿组件瞬态滞留成员集、对已释放实体 `SetComponent` 字典侧僵尸写入、迭代序差异（无契约）。2026-09-28 追加：真实玩家 `13c49fce`（尐妖）持久化分段全量回放 4 天离线补算复现「ECS 自然边界推进超过最大转换数」，根因为战斗自然边界被拆成逐出手边界外层 Tick 耗尽请求级转换预算（~10 边界/5s 窗口 × 上万窗口 > 100_000）；修复 `NextBoundaryAt` 战斗分支返回窗口战斗段终点、`AdvanceCombatEncounter` 单次 `AdvanceTo` 整段结算，事件序列/随机流/死亡复活时间戳逐位等价，回归 `CombatRestoreReproTests`（战斗中快照恢复 + 30h 连续狩猎补算 + 确定性双跑），全量门禁 190/190、32/32、155/155 通过，提交 `f458fda`。

### TASK-20260927-07 优化版 ECS 完整消融实验
- 分类：（原文未标注）
- 时间：提出 2026-09-27 22:12｜完成（原文未标注；时间线止于次日凌晨 07:4x 规范沉淀）
- 状态：（原文未标注）
- 摘要：在 `src/Ecs`/`src/ServerEcs` 的 `#if YUOCORE_OPTIMIZED` 分叉点安装运行时消融开关（`src/Ecs/EcsAblation.cs` 静态开关类，进程启动读一次环境变量、默认全开零开销；`scripts/bench-ablation.sh` 驱动，compare `Command Timeout=300`），量化 TASK-20260927-04 中 67s→12s 的逐项归因。50 人 × 12h offline-login 结果：opt-full Gathering 3.44s（1.31x OOP）/Crafting 5.24s（1.77x）/Combat 9.22s（2.19x，bulk 不适用），ecs-current 对应 10.62/9.92/16.72s（3.08x/1.89x/1.81x opt-full），compare 全部 exact。归因分解：ecs-current→opt-full 差距中纯内核收益 Gathering 14%/Crafting 20%/Combat 36%（300p Gathering 对照 35%），宿主优化占 86%/80%/64%；宿主单项边际 Gathering bulk +4.46s 最大，300p 呈超加性交互（bulk+active 同关 46.33s ≫ 单关之和）。限定：内核收益占比随实体密度上升（50p≈14~36%、300p≈35%）、绝对值不可跨会话拼表、深度埋点放大 ~13 倍（kernel-only 127s 且 10/50 连接超时，证实深埋点数字不能当真实性能）、Combat 300p 初次 mismatch 全是 compare `Command Timeout=30` 超时非结算不一致。结论：内核与宿主约 1:2~1:4 分担收益，纯换内核 300p 仍剩 3.5x OOP；宿主 bulk/active/cache 三项强交互需组合解读；Combat 是最大残余差距（2.19x）；消融开关保留为常驻设施、后续可直接复用归因。规范沉淀：YuoCore 两分支新增 `docs/yuocore-ecs-kernel-rules.md` 并挂接两仓 `AGENTS.md`（main 提交 `0566109`、ecs-optimized 提交 `be78766`），`bench-service.sh` 三处 `CORE_SHA` 钉同步更新。

### TASK-20260927-06 — 优化版 ECS 残留字典查找稠密化
- 分类：（原文未标注）
- 时间：提出 2026-09-27 21:10:00｜完成 2026-09-27 21:35:36
- 状态：（原文未标注）
- 摘要：将优化版 ECS 内核仍走 `Dictionary<Type,...>` 的残留派发路径收敛为稠密 id 数组直查（收益预期毫秒级，属一致性清理而非性能修复）：`LifecycleIndex.cs` 新增 `_systemsByTagId` 稠密表，`Get.cs` 三个 helper（`GetSystemOfTag`/`GetSystemOfComponent`/`GetSystemOfBaseComponent`）统一改稠密实现（一次收敛覆盖全部调用点与同型残留路径），`Function.cs` 注册/清理点位配对同步索引，`RunSystem.cs`/`StaticFunction.cs` 残留 `TryGetValue` 改直查；索引表持有与 MultiMap 内部同一 `List` 实例保证顺序与内容不变。提交 `vendor/YuoCore-optimized` 本地 `4e648ec`（未推送）。验证：内核 Release 构建 0 警告，YuoCore.Game.Tests 10/10、Runtime.Tests 18/18、StateSync.Tests 110/110、daohua ServerEcs.Tests（optimized 变体）30/30。实测变更派发面总计 ~130ms/12.2s（300 人离线 12h 回放），本项预期毫秒级，未来高频实体生灭模型才具可测收益。

### TASK-20260927-05 — YuoCore references 参考引用目录落地 + C# 开源 ECS 框架调研
- 分类：（原文未标注）
- 时间：提出 2026-09-27 18:57:17｜完成 2026-09-27 19:00:00
- 状态：（原文未标注）
- 摘要：在 `vendor/YuoCore` 落地参考引用目录：上游 `.gitignore` 已内置 `references/` 忽略规则（`vendor/YuoCore-optimized` 同样内置），直接创建 `vendor/YuoCore/references/` 即完成需求、无需修改已跟踪文件，目录内放 `references/README.md`（用途、约定与调研清单）。已克隆：`references/ET`（121M，GitHub 不可达期间改用作者官方 Gitee 镜像 `gitee.com/egametang/ET`，镜像停留在约 ET8 时代、HEAD 2023-03，ET10 待 GitHub 恢复重取）、`references/Arch`（3.6M）、`references/Friflo.Engine.ECS`（7.3M）、`references/DefaultEcs`（5.4M）浅克隆。调研：主流高性能框架 Friflo.Engine.ECS、Arch（~1.7k stars，archetype+chunk）、fennecs、Frent、TinyEcs；经典框架 Entitas（~7.6k stars）、DefaultEcs、Leopotam.EcsLite、Morpeh、Svelto.ECS；绑定/小众 Flecs.NET、HypEcs、RelEcs、Myriad.ECS、StaticEcs；对比基准 `Doraku/Ecs.CSharp.Benchmark`、`friflo/ECS.CSharp.Benchmark-common-use-cases`。

### TASK-20260927-04：300 人离线上线结算性能收敛
- 分类：（原文未标注）
- 时间：提出 2026-09-27 09:10:00｜完成 2026-09-27 11:13:24
- 状态：✅ 已完成
- 摘要：固定 300 人、离线 12 小时、真实 WebSocket Hello 上线结算为主性能场景，目标优化 ECS 登录结算墙钟进入 OOP 3 倍以内。基线 100 人 Gathering：OOP 7.1s/当前 ECS 287.5s/优化 ECS 144.8s，锁定 `EcsPlayerRuntimeAdapter` 跨玩家共享 `_loadGate` 为首要串行点。优化：移除全局加载门、缓存请求内组件、合并单玩家无战斗计时行动的自然窗口；事务仓储增加 `40001/40P01` 整笔 Serializable 重试（新连接、新事务、固定业务时间、有限抖动退避）并补同一余额热点 8 笔并发集成测试（制造/战斗重测跑满 300/300）。最终关闭深埋点 300 人 × 12h 并发 4：Gathering OOP 9.184s/当前 ECS 67.073s/优化 ECS 12.207s（1.33 倍）、Crafting 12.633/67.928/12.045s（0.95 倍）、Combat 15.042/123.469/23.250s（1.55 倍），三场景均 300/300、3000 角色持久化比对 exact，全部进入 3 倍目标内。深埋点样本：优化 ECS `core.simulation` 41.532s、`ecs.bulk_advance` 300 次/41.396s、`ecs.system_tick` 300 次/89.211ms；当前 ECS 全量深度观测被观测器开销拖至不可接受长等待，标记观测失败、不混入倍率。回归：ECS 领域/宿主测试 30/30、经济集成测试 7/7。

### TASK-20260927-03：ECS 优化版实现对比原版审查
- 分类：（原文未标注）
- 时间：提出 2026-09-27 08:53:33｜完成：未完成（🚀 进行中，归档时停滞）
- 状态：🚀 进行中（归档时停滞）
- 摘要：只读审查优化版 ECS（`vendor/YuoCore-optimized` 分支 `ecs-optimized` + `src/ServerEcsOptimized` + `YUOCORE_OPTIMIZED` 构建变体）相对原版（`vendor/YuoCore` 基线 + `src/ServerEcs`）的实现差异，不修改实现代码，结论对照总纲 15.3 与已登记基准；关联 TASK-20260924-09、TASK-20260926-01、TASK-20260927-01。确认三版本拓扑：OOP=`src/Server`（PlayerAggregate+Actor 单写者）、ECS-current=`src/ServerEcs`+基线 YuoCore `f0b78f5`、ECS-optimized=`src/ServerEcsOptimized`（链接同一 `src/ServerEcs` 源文件）+优化 YuoCore `68e4b63`，B/C 共用 `src/Ecs` 游戏核心，变体仅经 `YuoCoreRoot`/`EcsBuildVariant`/`YUOCORE_OPTIMIZED` 编译常量分流。已完成 4/5 子任务：核对提交链与 diff 范围（基线 `452b3a1`→optimized `68e4b63`）、逐文件审查内核差异（组件存储/掩码/系统查询计划/稠密实体集合/生命周期索引/Fiber 邮箱/活动实体查询）、变体接线方式审查、基准数字汇总；未完成：输出对比报告并标注风险/遗留点。

### TASK-20260927-02：离线 12 小时上线结算基准
- 分类：（原文未标注）
- 时间：提出 2026-09-27 08:20:00｜完成：未完成（🧪 验证中）
- 状态：🧪 验证中
- 摘要：新增 `offline-login` 基准模式（默认入口 `scripts/bench-offline-login.sh`）：同一角色 manifest、同一套游戏数据与独立数据库，玩家未加载期间把服务端确定性业务时钟前移 12 小时，随后经真实 WebSocket Hello 上线由 `runtime.LoadAsync` 触发离线结算，不发送登录后 `simulation.advance`；Hello、运行时加载补算、初始快照与上线登记纳入登录结算测量窗口。100 人 Gathering 三版本完整样本（`artifacts/bench/20260927T003531Z`）：登录墙钟 OOP 7,145.4 ms/当前 ECS 287,495.0 ms/优化 ECS 144,785.3 ms，p95 968.9/38,527.3/16,809.4 ms，均 `measured`、`connected_players=100`；优化 ECS 相对当前 ECS 墙钟约降 49.6%，但仍约为 OOP 的 20.3 倍。OOP 500 人补跑仅 480/500 会话（HTTP/WS 500）标 `failed` 不纳入比较，1000 人因本机 PostgreSQL/服务资源不足未声称完成。验收口径：每规模必须报告成功人数、统一目标时刻 `StartAt+12h`、初始快照 `LastSimulatedAt` 等于目标时刻、三版本终态 exact、失败样本不得伪装成性能结果。

### TASK-20260927-01：优化版 ECS 活动实体查询
- 分类：（原文未标注）
- 时间：提出 2026-09-27 04:58:51｜完成 2026-09-27 05:38:05
- 状态：✅ 已完成
- 摘要：解决优化版 ECS 玩家级 Tick 每个 System 全量玩家实体扫描问题：优化版 YuoCore 新增活动实体查询（保留全量更新、稳定执行顺序、延迟增删与异常隔离，提交 `68e4b63094ca9d3672e597763e43d3bcb105806f`），共用 ECS 游戏代码按构建变体调用，玩家级 Tick 候选含玩家及其战斗 Encounter；当前 ECS 保持全量扫描基线，OOP 业务规则不变。100 人采集 1h 无埋点：current/optimized 墙钟 8905/4467 ms（-49.8%）、CPU 21604/7032 ms（-67.5%）、RTT p95 2063/412 ms（-80.0%）、分配 331.0/346.7 MB（+4.7%），深埋点扫描数 54,327,540→2,161,000、Fiber Tick 两版均 216,100 次，终态 exact；优化 ECS 相对 OOP 仍 1.54x 墙钟、2.42x CPU、1.72x 分配、3.66x p95，未满足分配下降门槛。10 人制造/战斗：优化版相对当前版墙钟约 -8.4%/-8.1%、CPU 约 -29.2%/-14.1%。1000 人采集 OOP 建连仅 994/1000（PostgreSQL `53300 too many clients`）不计入性能样本；500 人采集补跑两组（深埋点 shard=64 并发 8 / 关闭埋点默认 shard=4 并发 8）三版 500/500、跨库终态 exact，关闭埋点 current/optimized 全流程墙钟 55.58/39.24s、命令窗口 18.84/2.43s、CPU 76.38/32.36s、分配 2.51/2.60 GB（详见 TASK-20260926-01 及 `artifacts/bench/active-query-20260927-500p/`）。门禁：`scripts/verify.sh` 完整通过（Domain 190/190、Server 181/181、真实 PostgreSQL 容量门禁、前端构建）、`scripts/bench-verify.sh` provenance + ECS 集成 29/29、优化版 YuoCore 全量测试通过。

### TASK-20260926-01：ECS 深度性能埋点与一小时回放报告
- 分类：（原文未标注）
- 时间：提出 2026-09-26 19:10:07｜完成：未完成（🧪 联调/验证中）
- 状态：🧪 联调/验证中
- 摘要：为 ECS 高消耗路径建深度性能埋点与三版本一小时回放报告（归属 TASK-20260924-09 深度观测阶段）：报告层新增 `mean_ms` 与 `server_counters` 单版/三版长表 CSV；ECS 宿主与游戏系统增加所有权门、Fiber 投递与 Tick、活动执行、快照导出、实体生命周期深度阶段；YuoCore 两套内核经 `YuoCoreTrace.Configure(Begin, End, Count)` 接入共享观测桥，最终内核锁定当前 `f0b78f5`、优化 `11a7f47`。单人三场景（采集/制造/战斗各回放业务一小时）九份报告全 `measured`、跨库终态 `exact`（`artifacts/bench/deep-20260927-final-1p/`），关闭观测九份亦 exact；10 人并发采集三版 10/10、exact：关闭埋点 OOP/current/optimized 墙钟 974/1567/1433 ms、CPU 622/1410/1266 ms、分配 21.6/32.1/30.8 MB，两版 ECS 各 21,610 次 Fiber Tick、821,180 次 System 实体扫描；开启深埋点 current/optimized 墙钟 3822/5121 ms、分配 648/869 MB，观测开销显著、只用于定位阶段不能当无扰动基准。10 人制造/战斗并发 10 出现 PostgreSQL `40001` 标 `failed` 不纳入，并发上限 1 后各 10/10、exact（`deep-20260927-final-10p-serial/`）：制造两版 ECS 各 19,210 次 Fiber Tick、691,560 次扫描，战斗各 36,730 次 Tick、1,145,552 次扫描。目标规模由 1000 降为 500 后完成两组有效对照：深度开启（ECS shard=64、并发 8）OOP/当前/优化全流程墙钟 37.04/383.00/219.20s、命令窗口 1.93/345.37/183.04s、CPU 33.01/1598.84/787.19s、分配 2.14/57.91/27.49 GB；深度关闭（默认 shard=4、并发 8）36.48/55.58/39.24s、命令窗口 0.79/18.84/2.43s、CPU 27.22/76.38/32.36s、分配 1.88/2.51/2.60 GB、RTT p95 16.47/609.09/121.74 ms；关闭埋点优化版相对当前 ECS 全流程 -29.4%、命令窗口 -87.1%、CPU -57.6%、RTT p95 -80.0%、分配 +3.8%，相对 OOP 墙钟 +7.6%、CPU +18.9%、分配 +38.3%。深度组 System 实体扫描 94,392,480→10,805,000（-88.6%）、`GetComponent` 337,248,376→68,437,500（-79.7%）；修复基准收尾两处伪差（模拟时间晚于系统时间的 durable 队列行未被普通刷盘认领、OOP WebSocket 卸载在最终刷盘后追加 Progression 行），压测收尾改为最多 8 并发。门禁：`verify.sh` 独立测试库完整通过（Domain 190/190、Server 181/181、容量门禁 `gatePassed=true`、前端构建）、`bench-verify.sh` provenance + ECS 集成 29/29、当前 YuoCore.sln 全量通过、优化版 563 项全过。

### TASK-20260925-01 · 普攻与受击的克制型特效变体
- 分类：（原文未标注）
- 时间：提出 2026-09-25 14:04:26｜完成 2026-09-26 02:05:09
- 状态：✅ 已完成
- 摘要：为普攻与受击增加克制型特效变体：四种普攻轮廓（掠斩、直刺、回锋、震击）与三种局部受击反馈（裂光、震纹、碎星），每个单位交替播放；普攻粒子上限 8，不用全屏爆闪或震屏，保留现有战斗节奏/状态/布局，设计真源见总纲第十三篇 §13.3。共享脚本负责普攻视觉，正式受击由 ArenaActor 气血变化独立驱动；Demo 保留沙盘结算并移除同一次命中重复受击；画廊控制台用既有按钮样式提供七种命名预览，环境普攻按出手单位轮换。并行分工：主 Agent 实现普攻及画廊入口，子 Agent 负责受击路径与共享样式。验证：前端生产构建通过（220 modules）、画廊 JS 语法与 diff 检查通过、浏览器逐项播放/连续受击/节点清理/自检 17/17 通过、控制台无错误。


### TASK-20260924-09 三套真实 OOP/ECS 服务与 YuoCore 内核对比基准
- 分类：架构实验 / ECS 运行时 / 性能基准 / 多 Agent 并行
- 时间：提出 2026-09-24 19:47:18｜完成 无（🚀 进行中，归档时停滞；进度日志延续至 2026-09-27 03:46 深度观测阶段）
- 状态：🚀 进行中（归档时停滞）
- 摘要：建立三套可独立构建/启动的服务做同协议、同游戏数据、同 schema 语义对比：A=现行 OOP（`PlayerRuntime`/`PlayerActor`/`SimulationEngine`）基准，B=基线 YuoCore ECS（`YuoFiber`/`YuoWorld`/`YuoEntity`），C=optimized YuoCore ECS（仅换 `vendor/YuoCore-optimized` 框架程序集，禁止 OOP 套壳冒充）；独立进程/端口 5128/5129/5130 与三库 `daohua_bench_oop`/`daohua_bench_ecs_current`/`daohua_bench_ecs_optimized` 隔离，结果要求快照/资源/背包/战斗日志/掉落/随机数顺序/无限行动 `RemainingRepeats == -1` 逐位一致；用户已放宽「优化 ECS 必须胜过 OOP」，保留实测目标（相对 ECS-current 核心 p95 −20%、每玩家分配 −20%、GC 暂停不增 10%+）。optimized 内核已有 8 个优化提交（`29570fd`~`35a834f`：ComponentTypeRegistry、稀疏/稠密存储、查询计划、Fiber 邮箱池化等）；框架形状基线 10,000 实体/20 tick/1 shard：optimized 387,490 entity-tick/s vs baseline 84,875（4 shard 403,545 vs 232,719）。落地：`DeterministicCombatEngine.NextBoundarySeconds`/`AdvanceOneBoundary` 边界原语、共享 `ActionAdvanceSession`→最终接口 `IPlayerActionState`/`IPlayerResourceContext`/`EcsActionPipeline`/`EcsPlayerActionContext`、`PrepareCombatEntry` 共享入场原语、`EcsPlayerRuntimeAdapter` 宿主边界、`BenchmarkTelemetry` 观测链；修复 OOP 下线版本覆盖 bug（`MarkOfflineAndFlushAsync` 未同步 Actor 逻辑版本致 duel v7 拒绝 v8 报告）与非法 presence 改显式 InvalidArgument。实测：三场景（采集 37s/制造 49s/战斗 30s）state/event/asset 哈希 OOP 提取前后逐位一致；短三版本真实 HTTP/WS/PG 回放（25128/25129/25130）`measurement_status=measured`、三库终态指纹逐位一致；10+10+10 并发短回放 `exactMatch=true`（optimized 相对 current 核心模拟 −10.6%/−1.8%/−1.7%，分配未降）；1000 人采集（pool32/并发32）：OOP 墙钟 133113ms/核心 43.6ms、current 130112ms/1292.9ms、optimized 128369ms/853.5ms（optimized 相对 current 核心 −34%、p95 −54%，仍慢于 OOP）；瓶颈诊断为 ECS 宿主单一全局 `_ownerGate`、`YuoFiber` Tick 串行与 system 全实体扫描；OOP 1000 人 15 分钟在线实测 899802/899802 命令 errors=0（RTT p50/p95/p99/max 0.260/4.201/6.517/969.554ms）；分片 `ServerOptions:Ecs:ShardCount`（shard=4）受控回放：optimized shard4 相对 current shard4 核心模拟 −58.2%、owner_wait −43.0%、数据库耗时 −77.1%、RTT p95 −96.4%；离线 1 小时补算（采集/制造/战斗 10 人）三库 `exactMatch=true`，核心模拟 OOP/current/optimized = 61.17/613.51/363.94ms、66.53/554.09/323.93ms、188.39/1085.90/742.50ms；逐阶段计时扩展按总纲 15.3.3 落地 `.stages.csv`/`.counters.csv`（42 行 7 阶段可比）。制造 1000 人受本机 PG `max_connections=50` 建连上限阻塞（pool128 报 53300）；15/60 分钟长稳、3000 人长稳、离线 8/24/48 小时、WS 终态快照哈希均未完成，不能据此宣称采用门槛达成。详细阶段与观测开关对照见 TASK-20260926-01.md。

### TASK-20260924-08 性能与架构持续优化（分布式部署准备）
- 分类：（原文未标注）
- 时间：提出 2026-09-24 06:09:16｜完成 无（原文未标整体完成状态；W3~W8 各波次已完成，末条 2026-09-24 17:25:48 YuoCore 只读审计）
- 状态：（原文未标注；各波次均记录已完成，归档时无整体收口标记）
- 摘要：深度并行优化性能与架构且不改变任何游戏内容（战斗结果/掉落/市场撮合/队列/快照语义不变），为分布式部署扫清横向扩展障碍；真实拓扑为主服务器 `YuoHira`（daohua.yuohira.com）+ 计算节点 `odin`（无公网 NAT 设备，只能 compute-link.v1 反向拨入，禁止主服反向直连节点设计）；全部修改统一记录在 `docs/perf-arch-changelog.md`。已完成波次：W3-A（f0d1ecf）ComputeNode 真补算——契约升级 compute.v2 + compute-link.v1.2，`SectionCodec` 单源迁至 Domain.Persistence，节点经 `DeterministicBatchEngine` 跑真实 `SimulationEngine.AdvanceTo`（离线口径不撮合斗法），主服同事务 section CAS + 资产入账 + 任务完结，golden vector 逐字节一致；W3-C（f0d1ecf）经济提交往返 2K+N+2 降至 K+2（余额单语句守卫 upsert+RETURNING、流水 unnest 合批、分段级检查点回滚），Domain.Tests 181/181、Server.Tests 151/151；W4（c8876bf）`docs/distributed-readiness.md` 落成（结论：任务层 SKIP LOCKED 天然多写者安全，缺口集中在玩家连接层）+ drain 落地 compute-link.v1.3（主服停机 DrainAll 12s 排空窗口、节点停机 10s 窗口），Server.Tests 168/168；W5-A（8d1b2c0）协议分段载荷瘦身——`PlayerSectionDto` 由 `payloadBase64` 改直嵌 `data`，前端在信封原始字节上直接哈希，分段 DTO −25%、全量快照 −24.9%；W5-B（c8876bf）compute-link 观测面（`ComputeLinkMetrics`/`ComputeLinkNodeView`/`GetBacklogAsync`/`ComputeLinkHealthCheck` + `/ops/api/compute`）与真实链路压测基线（32 玩家/2 节点：17.4 jobs/s、任务时延 p50 443ms p95 1882ms、断线租约释放 566ms），顺手修 SSI 40001/40P01 冲突烧 attempts 与派发器空转自旋两 bug；W6-B（6f8e24c）建筑贡献写路径改 FOR UPDATE 行锁短事务 + `ColdCatchUpWorker` advisory lock 去重，Server.Tests 170/170；W7（9b2fd7a）命令回执 snapshotForm 双形态（成功走脏段差集增量编码 8 段→通常 1~3 段），Server.Tests 174/174；W8-A（e8b34a6）落盘 sweep 有界并行 ≤8，Server.Tests 179/179；W8-B（be32fdf）节点上游 URL 列表故障转移 + 每节点独立令牌映射（`ComputeNode:Tokens`）；末轮 YuoCore 非传统 ECS 只读审计确认 DaoHua 未实例化 `YuoWorld`/`YuoEntity`/`YuoFiber`，并完成 10,000 实体/100 组件/100 系统 1/4 shard Release 基准，本轮无代码改动。

### TASK-20260924-07 斗法无对手静默 + 怪物掉落明细展示
- 分类：（原文未标注）
- 时间：提出 2026-09-24 05:52:00｜完成 2026-09-24 06:05:30（服务端落地；前端配套 06:01:10）
- 状态：✅ 已完成（Domain.Tests 152/152、Server.Tests 101/101、前端 build 与契约校验全绿）
- 摘要：斗法无可匹配对手时前端不再闪现「斗法感应中…」占位敌方卡（根因：搜寻完成 `chargesBefore>0` 进入 `DuelPending` 相位广播一帧，`CombatArena` 对该相位渲染 `visual:null` 占位敌人卡）、删除 `SimulationEngine` 进入 DuelPending 时的 `duel.seeking` 日志，撮合双方 Attach 成功后各发系统日志「斗法遭遇：{对手名} 与你狭路相逢。」（LogChannels.System，走 PlayerLogBuffer 支持重连回放）；掉落展示结构化——`drops` 由 `string[]` 升级为 `[{itemId,dropChance,minQuantity,maxQuantity}]`（`StaticGameCatalogMapper.ResolveMonster` 与 `ProtocolSnapshotMapper.ToNearbyMonster` 两处投影同步），确认实际掉落语义为先 roll 掉不掉（dropChance）再 roll 数量，并修复数量抽样 `Floor(min+(max-min)*roll)` 使 max 永不可达的缺陷（改 `Floor(min+(max-min+1)*roll)` + Clamp 闭区间 [min,max]）；前端新增 `ItemDropTile`（38px 迷你瓦片 + ×数量角标 + 概率小字）接入 `CombatEcologyPanel`/`WorldMapPanel`，`isDuelPhase` 收窄为 `duel`、删除 `combat.duel.sensing` 词条；测试更新 `DuelSimulationTests` 断言、新增 `RangedDropQuantitySamplesWithinInclusiveBounds` 区间采样用例、`check-frontend-contracts.mjs` 补 drops 结构断言；设计文档 07/08 篇补「无候选对手静默回退」语义并同步 index.html。

### TASK-20260924-06 木灵/火灵灵力上限收拢至 10 点
- 分类：（原文未标注）
- 时间：提出 2026-09-24 05:19:00｜完成 2026-09-24（DataTool strict + Domain.Tests 全绿，待验收）
- 状态：✅ 实现完成（待验收）
- 摘要：木灵（`enemy.wood-spirit`）与火灵（`enemy.fire-spirit`）灵力上限由约 57/62 点收拢为 10 点量级，限制术法技能施放次数、解决 T1 术法小怪过强；怪物属性唯一推导链 `MaxManapoints = 锚点Mp(等级) × 品阶倍率(minion=0.8) × weights.mp`（`GameDataCatalog.ParseMonsterDefinitions`，`GameDataCatalog.cs:847`），配表无固定值覆写字段故调 `weights.mp` 为唯一手段；锚点插值 Lv1 mp=50 → Lv18 mp=90 线性下木灵 Lv3=54.706、火灵 Lv5=59.412；权重按用户裁定取整：木灵 `weights.mp` 1.3→0.2（终值 8.75）、火灵 1.3→0.3（终值 14.26，保住 1 发轰炎）；实际效果：木灵掌心雷（4 灵力/发、0CD 填充）最多施放 2 次后回落普攻，火灵轰炎（10 灵力/发、24s CD）全场仅 1 次，怪物战内无灵力回复（`MpRegen` 默认 0）蓝尽不可逆；其余维度（`budgetCost` 15/18、掉落表、技能槽、modifiers 五行减伤）不动；仅改 `config/game-data/monsters/tier1_minions.json`；验证 DataTool strict 通过（fingerprint `45a1ccc38d1e2c6c`）、Domain.Tests 151/151；首轮暴露 2 个持久化快照夹具失效（`StuckCombatReproTests`/`SimulationScheduleTests` 木灵快照 `Manapoints` 52.89/50 超新上限被 `ApplySnapshot` 快速失败），按「已消耗一发掌心雷」语义改写为 4.752941176470588（=8.75−4）保持线上卡死复现场景不变。

### TASK-20260924-05 战斗消耗品分组与使用条件模型
- 分类：（原文未标注）
- 时间：提出 2026-09-24 04:58:41｜完成 2026-09-24（全链路实现完成，验证通过）
- 状态：✅ 已完成（验证通过）
- 摘要：战斗域消耗品分双组栏位：药品栏 ×3（仅药品类即时消耗品）+ 通用栏 ×3（任意战斗消耗品），共 6 槽；通用栏支持直接消耗与持续消耗两模式，持续消耗按战斗存活时钟扣减（战斗进行中与死亡等待期间扣，寻敌/波间搜寻/退出战斗不扣）；技能与消耗品共用「对象-类型-比对」三元组触发条件模型（对象=自身/目标/友方/敌方，类型按对象过滤含集合对象群体聚合类型，比对=数值大小等于/Boolean 是否），所有行动出手前先威胁判定锁定目标。实现：领域 `AutoUseSlotState`+`Group`（potion/general，每组 `MaxSlotIndex=2`）、`ItemDefinition`+`Category`、`ConsumableDefinition`+`Triggers`（`GameDataCatalog` 逐条 `ValidateTrigger`）、`SkillTriggerEvaluator` 加集合对象 ally/enemy 与 alive-count/dead-count/equal 条件并按矩阵拒绝越界组合；`SimulationEngine` 即时消耗品共享触发求值（「目标」=存活敌方有效威胁最高者，不消耗随机流）、`RechargeCombatSustainedSlots` 入场补能、`InjectSustainedCombatBuffs` 注入、`DrainCombatSustainedSlots` 按战斗实际推进秒数扣减（含躺尸等待）；协议 `queue.auto_use.configure`+`group` 参数、`ProtocolSnapshotMapper` 冷却数组扩为 6；前端 `AutoUseSlot`+`group`、战斗面板两组栏位、药品栏拾取过滤 `category=pill && !isContinuous`；config-editor `CombatTriggerEditor` 接入；配表 `item.zhixue-powder` triggers=[self current-hp-percentage <= 60]、`item.shunqi-pill` triggers=[self current-mana-percentage <= 50]；文档真源为总纲 06 篇 §6.2.3 与 07 篇 §7.8.3，并在 `AGENTS.md` §4 落地「文档先行」规则。验证：`dotnet build src/Server` 0 警告、Domain.Tests 151/151（新增 `CombatConsumableTests` 5 项）、Server.Tests 88 通过（13 失败为缺 `DAOHUA_MARKET_TEST_CONNECTION` 环境门禁）、DataTool strict（fingerprint `e2b78cc5d8de6e3b`）、frontend build 与契约检查全过。

### TASK-20260924-04 讨伐弹窗节点生态化重构
- 分类：（原文未标注）
- 时间：提出 2026-09-24 04:37:34｜完成 2026-09-24（门禁全过；待漱玉溪 UI 手测收口）
- 状态：✅ 实现完成（待漱玉溪 UI 手测收口）
- 摘要：讨伐行动弹窗（`ActionModal`）主体改为当前地点生态怪物图标网格（悬停简要信息 + 单击详细信息），修复遭遇错配根因——前端按 `action.combatEncounterId`（固定 `encounter.twilight-pass`）渲染 `CombatEncounterPanel` 而服务端 `SimulationEngine.ResolveCombatEncounter` 实际按玩家当前节点 `encounterIds` 确定性抽取；生态数据直接取静态目录 `maps[].nodes[].monsters/eliteEnemies/bossEnemies`（`StaticGameCatalogMapper.ToNode` 已预解析为 `CombatantDefinition` 全字段）；行动显示名按当前驻留地点本地化（新增 `frontend/src/game/ids.ts` 的 `localizeActionDisplayText(text, world, maps)`，行进中 `nodeId=null` 不替换），覆盖行动卡/弹窗/队列名/战斗区显示名；新建 `frontend/src/components/CombatEcologyPanel.tsx` 替换并删除 `CombatEncounterPanel.tsx`，交互复用 `FloatingPopover` 双层嵌套 + `FloatingInfoCard`；斗法区 4 条规则描述收进 hover 信息卡；搜寻时长修正：`searchMilliseconds` 默认 5000→20000、`InitialSearch` 首搜固定读条不吃 `SearchDelayFactor` 乘区（防重开刷场）、`useSmoothProgress` 进度文本 duration 改 `formatNumber(maxDecimals:1, round)` 消除 4.9999…s 噪音；文档同步 `docs/frontend/common-components.md`、总纲 08 篇 §8.3/§8.5、`game-data.md`，重跑 `scripts/sync-master-viewer.mjs`。验证：frontend build、Domain.Tests 145/145、Server.Tests 88 通过（13 失败为环境门禁）、契约检查、DataTool strict（fingerprint `aa3bc0aaf06a887d`）全过；`CombatEncounterPanel` 及旧样式全仓零匹配、孤儿 i18n 键清理；漱玉溪生态核对 `node.shuyu-brook.encounterIds=[encounter.qi-beast]`。

### TASK-20260924-03 设计文档收编总纲
- 分类：（原文未标注）
- 时间：提出 2026-09-24 02:10:00｜完成 2026-09-24 02:40:00
- 状态：✅ 已完成
- 摘要：将 `docs/design/` 下散落的专题设计文档全部合并进主设计文档（总纲文件夹），`design/` 最终仅保留 `总纲.md`（指引 stub）、`总纲/`（17 篇 + 查看器）、`战斗演武画廊/`（前端 vfx 资产真源）；合并映射：角色核心设计→02、持续消耗品系统+原第五章材料清单→06、战斗设计→07、通用地图与遇怪系统→08、节点公共建筑系统→09、数值量化模型集→11、传音频道与日志分组→14、行动统计系统→17；原 06/07/13 篇摘要内容被对应专题全文覆盖直接以专题为篇正文；篇内小节统一重编号为 `篇号.节号`，`§` 交叉引用与全仓 `.md` 链接、锚点同步重映射；README/stub/索引/代码注释/查看器品牌与快照同步，HTTP 渲染验证通过。

### TASK-20260924-02 战斗数值与属性体系：文档修订 + 点数属性全链路实现
- 分类：（原文未标注）
- 时间：提出 2026-09-24 02:06:00（用户裁定）｜完成 2026-09-24（全链路落地，门禁通过）
- 状态：✅ 已完成（门禁通过）
- 摘要：用户裁定总纲 06 篇 + `战斗设计.md` 为唯一正确实现目标，术语统一为「化解」，恢复增免伤/速度/缩减点数属性体系、废除五行抗性误植、面板恢复点数口径（100 点 = 100%）。领域：废除五行抗性字段（`MetalResistance` 等 5 项）与抗性第二层减免；新增点数属性——增伤 Linear（`PhysicalDamageBonusPoints`/`SpellDamageBonusPoints`/五行 ×5/`TotalDamageBonusPoints`/`BasicAttackBonusPoints`/`SkillBonusPoints`）、减伤 NonLinear（物理/法术/五行 ×5，总减伤沿用 `DamageAbsorptionPoints`）、特殊（`AttackSpeedPoints`/`ActionSpeedPoints`/`CooldownReductionPoints`，`ThreatRatio` 更名 `ThreatBonusPoints`）；`CombatEffectProfile` 四项效果改点数存储；伤害结算链 `ResolveAttack` 按战斗设计 §7.1：`raw²/(raw + RES×K + 1)` 大类抗性平方穿甲（化解×2 照旧）× Linear 增伤乘区 × NonLinear 减伤乘区 × 暴击 × NonLinear 总减伤，普攻 vs 技能增幅按 `abilityId == null` 判定；时序结算：普攻读条 = `AttackIntervalSeconds` × NonLinear(攻速) × NonLinear(行动速度)，技能吟唱/冷却各乘对应 NonLinear。配表：装备 `specialMechanics` 键全量点数化（`threatPoints`/`hpDrainPoints` 等 + 19 项新键）、Buff 键点数化（pills/abilities 配表值 ×100）、怪物 `weights` 仅留固定属性权重 + 新增 `modifiers` 点数对象、建筑 `combatDamageReductionRatio` 维持比率键解析端 ×100。协议：`CombatAttributes` wire 全量点数化 40 键，移除全部比率字段不做 `/100` 伪装；前端 `CombatAttributePanel` 恢复三 Tab 设计结构（基础/特殊/伤害增减 8 条目成对显示增伤 Linear/减伤 NonLinear）、`types.ts`/`snapshot.ts` 白名单同步 40 键、`ItemDisplay` 覆盖全 26 个新点数键、i18n combat 域重写；config-editor 全套编辑器点数化。验证：`dotnet build Game.slnx` 0 警告、Domain.Tests 145/145（新增 `CombatAttributePointsTests` 6 用例）、Server.Tests 新增 `ProtocolAttributesPointsTests` 通过（其余失败为既有 `DAOHUA_MARKET_TEST_CONNECTION` 环境门禁）、前端 tsc+vite build、契约检查、DataTool strict、config-editor tsc 全过。

### TASK-20260924-01 总纲拆分为 13 篇文件夹 + 查看器自动拼接与页内跳转
- 分类：（原文未标注）
- 时间：提出 2026-09-24 00:20:00｜完成 2026-09-24 00:45:00
- 状态：✅ 已完成
- 摘要：将 `docs/design/总纲.md`（13 篇）按 `# 第N篇` 边界拆分到 `docs/design/总纲/` 文件夹（01~13 篇目 + README 目录 + manifest.json，文首标题与目录页落入 README.md，篇内指向 design/ 兄弟文档的相对链接统一加 `../` 前缀）；查看器迁入文件夹为 `index.html`：HTTP 访问经 `fetch('./manifest.json')` 逐篇读取并以 `<!-- FILE:文件名 -->` 哨兵拼接渲染（永远读最新），`file://` 直开回退内嵌快照（由同步脚本生成）并提供「选择文件夹」（`webkitdirectory`）与拖入文件夹通道；链接分流：`#锚点` 与指向已加载篇目的链接改页内 `scrollIntoView`（GitHub 风格 slug 映射，slugMap/fileMap + data-doc-link 点击委托），外部 http 与文件夹外 md 保持新开页（原链接一律 target=_blank 为新开页根因）；`scripts/sync-master-viewer.mjs` 扫描篇目重写 manifest + 内嵌快照一键同步；`总纲.md` 保留为指引 stub 全仓既有引用零破坏，docs/README 与 DESIGN.md 真源链接直连篇目文件。验证：内联 JS `node --check` 通过；Edge headless HTTP 渲染 51 标题/57 目录项「已自动读取文件夹 13 篇（实时）」，file:// 快照回退正常；docs 全树链接校验 0 断链。

### TASK-20260923-12 design/ 文档全量中文命名
- 分类：（原文未标注）
- 时间：提出 2026-09-23 23:55:00｜完成 2026-09-24 00:05:00
- 状态：✅ 已完成
- 摘要：`docs/design/` 下 11 个文件/目录全部改为中文文件名（短名口径，取各文档自身中文标题）：总纲.md/总纲.html/角色核心设计.md/战斗设计.md/数值量化模型集.md/通用地图与遇怪系统.md/节点公共建筑系统.md/持续消耗品系统.md/行动统计系统.md/传音频道与日志分组.md/战斗演武画廊/；关键风险处置：`frontend/src/vfx/index.ts` 经 `import.meta.glob('../../../docs/design/战斗演武画廊/vfx/*.js')` 真实引用画廊脚本须同步改 glob 并以 `npm --prefix frontend run build` 验证、`总纲.html` 自包含副本内嵌文本含兄弟文档链接一并改写、`config/game-data/expected-attributes.json`/`CombatCatalogModels.cs`/`GameLogPanel.tsx`/`vite.config.ts`/`engine.ts` 注释均含文档名引用；全仓文本文件全局替换 basename token（排除 .git/node_modules/bin/obj/dist，历史任务与审计文档同步改写保活链接），覆盖 68 个命中文件；docs 全树链接校验 0 断链、frontend build 通过（中文路径 glob 正常）。

### TASK-20260923-11 文档目录合并优化
- 分类：（原文未标注）
- 时间：提出 2026-09-23 23:15:00｜完成 2026-09-23 23:25:00（23:50 追加清理）
- 状态：✅ 已完成
- 摘要：`docs/` 目录合并优化：`design/player-expected-attributes-model.md` 与 `design/combat-benchmark-model.md`（同族「历史生成数值快照」，期望属性为上游、战斗基准为下游）合并为 `design/数值量化模型集.md`（标题层级降一级、文首声明合并来源与快照维护口径）并删除两个原文件；`audits/` 中 doc-vs-implementation 0914/0914-recheck/0915/0916 四轮已结题审计归档至 `archive/audits/`（audits/ 保留 0919 审计、deep-risk-analysis-20260919、model-fork-audit-20260923 三份现行证据）；追加清理：`battle-scene-demo.html` 归档至 `archive/design/`（被演武画廊取代）、删除 3 个 `.DS_Store`；活跃文档引用修正覆盖 `docs/README.md`/`战斗设计.md`/`角色核心设计.md`/`backend/game-data.md`/`world/lore.md`/`DESIGN.md`/`tasks.md`，顺带修复 9 处历史断链；`tasks/` 每任务独立建档机制与 `战斗演武画廊/`（内部 `../../../frontend` 相对路径钉死层级）不动；全树相对链接校验 0 断链。

### TASK-20260923-10 设计理念总集沉淀（docs/DESIGN.md）
- 分类：（原文未标注）
- 时间：提出 2026-09-23 23:09:18｜完成 2026-09-23 23:12:00
- 状态：✅ 已完成
- 摘要：新建 `docs/DESIGN.md` 设计理念总集：不是数值规约与实现细则的堆积，而是集中各子系统「为什么这样设计」的意图、哲学与红线，按系统分十六篇 + 速查表，每节固定结构为「一句话理念 → 核心设计意图 → 红线/不变量 → 真源文档链接」；总集只做意图层索引不复制数值表与规则细节，与专题文档冲突时以专题文档为准（文首声明）；理念素材取自总纲第一篇（五条铁律）、`角色核心设计.md`（统一常驻实体哲学 + 设计红线）、`战斗设计.md` §3.1/§9（怪我同构、世界常驻）、`通用地图与遇怪系统.md` §1、`持续消耗品系统.md` §1、`节点公共建筑系统.md` §1、`world/README.md`（碎片化叙事红线）、backend 聚合单写者与持久化架构决策；初置仓库根目录经用户指正移至 `docs/` 根目录；`docs/README.md` 索引登记、相对链接全部校验有效。

### TASK-20260923-09 战斗团灭分流模型（原地复活 / 团灭即败）
- 分类：（原文未标注）
- 时间：提出 2026-09-23 21:38:30｜完成 2026-09-23 22:50:00
- 状态：✅ 已完成（门禁验证通过）
- 摘要：修复「怪物把玩家击杀后怪物也消失」不符设计意图（根因：`4e22179` 为落地死亡复活规则把战败路径从 `action.Status = Blocked` 改为 `CreateSearchState` + `Respawning` 拆场），用户裁定团灭处理通用化抽象为两条正式分支 `onPartyWipe = respawn | defeat`：respawn（野外 PvE 缺省）战斗不终结——玩家原地躺尸读条、怪物停手但回血/CD/Buff 到期照走、读条满起身续打同一波残局波次不重置；defeat（斗法/副本/爬塔）`Outcome=EnemyWon` 终局信号、善后是模式级策略不进引擎；逐条拍板语义含：起身仅补满 HP（MP/技能 CD/自动消耗品 CD 时钟照走）、阵亡瞬间双方吟唱全打断且未完成吟唱退还预扣灵力不进 CD、躺尸窗口敌方全灭照常 PlayerWon 结算掉落与轮次、阵亡计数 `totalDefeats`/`deaths`+1 发 `combat.lost`/`combat.respawned`、不做死循环兜底、战内躺尸同 §9.3 气血锁 0 灵力按 5s 网格照回。实现：`CombatEncounterDefinition` 新增 `OnPartyWipe` 枚举（缺省 respawn，`DataTool` 词汇校验）、`CombatEncounterState` 携带 `PlayerRespawnSeconds`/`PlayerRespawnAtSeconds`、`DeterministicCombatEngine.AdvanceTo` 倒地窗口（`InterruptCasts` 打断全体吟唱、边界集合改为 Buff/回复网格 + 复活截止点、怪物出手边界全部跳过、起身仅补满 HP 后双方重新 `DecideInitialActions`）、`ApplyBuffTick` 阵亡单位 HP 不回 MP 照回、`CombatExpeditionState` 增持久化字段与 `IsValid` 不变量、`SimulationEngine` 经 `InBattleRespawnSeconds` 分流；协议 expedition DTO 增 `playerRespawnAtSeconds`/`playerRespawnDurationSeconds`；前端 CombatArena 玩家阵亡置灰 + `kind:'respawn'` 复活读条、倒地窗口敌方行动条不显示；config-editor 增 `PARTY_WIPE_RULES` 枚举；新增 `CombatPartyWipeTests` 等三组测试。文档落点：`docs/design/战斗设计.md` §7.2/§9.3/§9.4（团灭分流规约 + 复活结算总表）、总纲 §6.4、`docs/backend/game-data.md` §3。验证：全仓 build 0 警告、Domain.Tests 139/139（含 7 个团灭分支用例）、Server.Tests 87/100（13 失败为 `DAOHUA_MARKET_TEST_CONNECTION` 环境门禁）、frontend build、config-editor tsc、契约检查、DataTool strict 全过。

### TASK-20260923-08 演武粒子特效接入正式前端（模块化 VFX 加载器）
- 分类：（原文未标注）
- 时间：提出 2026-09-23 08:10:00｜完成 无（🧪 联调/验证中，归档时停滞；末条 2026-09-23 19:00 单一真源重构完成）
- 状态：🧪 联调/验证中（归档时停滞）
- 摘要：将 `docs/design/战斗演武画廊/` 已验证的粒子特效模块化接入正式前端真实战斗页面，`CombatArena` 不硬编码任何技能演出逻辑、新增技能只需新增一个模块文件：`frontend/src/vfx/` 下 `engine.ts`（`VfxEngine` overlay 坐标系/粒子/弹道/光束/注册表/样式注入/dispose）、`feedback.ts`（普攻命中/施法法阵/通用爆发兜底）、`abilities/*.ts` ×10（每真实功法一模块自含专属样式）、`index.ts`（`import.meta.glob` eager 自动注册 + `createCombatVfx(shell)` 工厂按 `targetType` 从真实 DOM 解析目标）、`useCombatVfx.ts`（React 桥：订阅 `castingAbilityId`/敌方 `pendingAbilityId+nextAttackAtSeconds`/斗法 `events[].abilityId+atSeconds`/双方普攻时刻，signature 防重调度）、`styles/vfx.css`；CombatArena 最小侵入接入（shell ref + overlay + 一行 hook，零技能逻辑进组件）；与画廊版差异：沙箱结算回调全删（真实结算由服务端快照驱动，特效脚本只做纯粒子演出）、时钟域 `fireAt = serverNow + remaining`；2026-09-23 19:00 单一真源重构：技能脚本统一收拢至画廊 `vfx/`（队列模块格式 `window.DaohuaVFXModules.push(fn)`，无 import/export），前端 `import.meta.glob` eager 引入同一批文件 + drain 注册，`VfxEngine` 扩展为 DaohuaVFX 门面（结算类方法 noop），删除前端 10 个 TS 副本与 feedback.ts，`vite.config.ts` 加 `server.fs.allow:['..']`。验证：`tsc --noEmit` 零错误、`vite build` 通过、bundle 确认模块打包、运行时冒烟页 8/8、画廊 selftest 回归 17/17、共享真源冒烟 9/9。前置 TASK-20260923-07。

### TASK-20260923-07 战斗演武画廊原型深度重建
- 分类：（原文未标注）
- 时间：提出 2026-09-23 06:38（需求分析与旧原型诊断完成并开工）｜完成 2026-09-23 07:05
- 状态：✅ 已完成（重建完成并全部验证通过）
- 摘要：深度重建 `docs/design/战斗演武画廊.html` 程序化特效原型：旧原型诊断出特效全体不可见（脚本生成 `fx-*`/`vfx-node`/`dmg-float`/`arena-action-fx` 等 24 个关键 selector 在样式块中 0 条对应规则，全部渲染为空 div）、界面与真实不符（自造 gallery-header + 双列 + 3v3 假阵型，真实 `CombatArena` 为己方仅 1 号位玩家 + 8 空槽/敌方按 ENEMY_DEPTH_SLOTS [1,4,7,2,5,8,3,6,9] 纵深铺位）、数据失真（怪物技能虚构，真实为全单位共用同一套 ability.* 管线）、单文件 1.5MB 不可维护；重建为多文件工程 `docs/design/战斗演武画廊/`：`index.html` 完整复刻真实 App 骨架且样式直接 `<link>` 引用 `frontend/src/{styles,selection-grid,expedition,floating-popover}.css` 保证永不漂移、头像与功法图标相对路径引用 `frontend/public/avatars/*` 与 `frontend/src/assets/items/ability.*.webp` 真实资源；`vfx/core.js` 特效引擎（overlay/跳字层坐标系、粒子工厂、弹道投射物、震屏、注册表 `DaohuaVFX.register(abilityId, handler)`）、`vfx/catalog.js` 真实功法目录镜像（10 个 `ability.*` 的 castMilliseconds/manaCost/targetType/系数来自 `config/game-data/abilities/*.json`）、`vfx/feedback.js` 通用反馈组、`vfx/abilities/<id>.js` ×10 按真实技能 ID 拆分、`sandbox.js` 沙盘状态机（按真实 castMilliseconds 驱动行动条、targetType 目标解析、扣灵力/伤害治疗结算/Buff 方块）、`gallery.js` 装配层 + 右栏「演武」控制台；URL 钩子 `?fx=<abilityId>`/`?auto=1`/`?side=enemy`/`?selftest=1`。验证：`node --check` 14 脚本全过；Edge headless（Chromium 153）`?selftest=1` 自检 17/17 通过（含伤害结算 450→379、自动清理 0 残留、行动条复位）；http 与 file:// 双协议均过；旧单文件已删除，`docs/README.md` 索引指向新目录。

### TASK-20260923-06 全量项目文档优化点并行分析
- 分类：文档治理 / 只读分析（非 Bug 修复，不产生代码改动）
- 时间：提出 2026-09-23 05:50:38｜完成 2026-09-23（六路审计全部完成）
- 状态：✅ 分析完成，待用户裁定整改范围
- 摘要：六路并行只读审计（A=design+world、B=backend、C=frontend、D=ops、E=audits/archive/future/临时、F=索引+任务面板+根文档），合计约 110 条带证据发现。最高优先（文档与实现硬冲突）：`game-data.md` rootAttribute 六维枚举整列写错（intelligence/defense/swordsmanship/talisman 不存在，实为 stamina/meridians/attack/bone/agility/consciousness）；`common-components.md` §11 `formatDuration`/`formatPercent`、§25 `CombatAttributePanelModel` 整套函数不存在与「服务端终值直渲」架构相反；`domain-data-architecture.md` 与 `logic-development-guide.md` 将 `AutoUseSlotState` 误归 Loadout（实为 Settings）；`deploy/postgres-local-roles.sql` 未覆盖 schema v4 `social` schema（按文档流程部署后社交功能必坏）；`deep-risk-analysis-20260919` H-01~H-09、M-11 高危项仍 open 且 H-01/02/03 被 compute-link.v1 原样继承。compute-link.v1 口径漂移四处（`AGENTS.md:53`、`README.md:96-106`、`deployment.md`、`commercial-persistence-architecture.md` §三仍写「主服→节点内部 HTTP」，实际已切节点反向 WS 拨入 `/internal/compute-link`）。索引与结构治理：`docs/README.md` 漏登 3 处、死链 15 处、tasks.md 面板失真（进行中表混 22 行纯完成态 + 10 行待验收、孤儿 `TASK-20260915-05.md`、TASK-20260915-03 重号）、schema 版本口径混乱（根 README 仍 v3 实 v4 等）、7 份审计报告缺 superseded 横幅。需裁定的口径分歧：持续消耗品扣减按「行动基础耗时」而非加速后实际耗时、即时消耗品「触发器规则（生命<50%）」未实现（现行按冷却周期自动使用）、掌心雷/雷光引 element 总纲写火系配表为 wood、`AGENTS.md` §15（纯文档不自动提交）与 §17（提交后才归档）规则缝隙致文档类任务永不闭环。

### TASK-20260923-05 计算节点反向拨入链路（compute-link.v1）
- 分类：（原文未标注）
- 时间：提出 2026-09-23 03:10:00｜完成 无（🚀 进行中，归档时停滞；本文档仅记录设计落地与登记）
- 状态：🚀 进行中（归档时停滞）
- 摘要：计算节点平台改为通用动态模型：节点通过指定网址/IP 主动连接主服务器（类 P2P 反向拨入，odin 在 NAT 后免隧道直连），节点组动态增删（热插拔），主服按在线节点动态分配任务、节点全部离线时主服仍正常工作。设计（compute-link.v1）：服务端新增 `/internal/compute-link` WebSocket 端点（`X-Compute-Token` 鉴权 + hello 握手 nodeName/maxConcurrentBatches/gameDataFingerprint）；`ComputeNodeRegistry` 在线节点注册表按剩余并发容量挑节点、节点断开即在飞批次整批退回 pending（沿用 compute_jobs 租约/退避语义）；`ComputeDispatcherWorker` 改造：claim 到期任务 → 注册表取节点 → 经 WS 推批次 → 结果按 SourceStateVersion CAS 落库、无节点时任务保持 pending；ComputeNode 新增客户端模式（`ComputeNode:UpstreamUrl` + 既有 Token，ClientWebSocket 重连退避拨出，批次执行复用 `DeterministicBatchEngine`）；hello 指纹不匹配拒绝注册防旧版本节点吃新配表任务；静态 `ComputeNode:Endpoints` HTTP 轮询路径移除（内测无兼容包袱），节点本地 HTTP `/internal/v1/compute/advance-batch` 保留为独立调试口；`compose.prod.yaml` 的 compute-node 改 upstream 拨入（loopback），odin 拨 `wss://daohua.yuohira.com/internal/compute-link` + 生产 COMPUTE_TOKEN。

### TASK-20260923-04 通用战斗语义收拢与模型分叉全量修复
- 分类：（原文未标注）
- 时间：提出 2026-09-23 02:30:00｜完成 2026-09-23（四路并行 + 六维命名收敛完成）
- 状态：✅ 已完成（验证通过，留两项复审遗留）
- 摘要：用户裁定不存在 Boss 专属技能、所有属性/技能/装备/效果对所有单位完全通用，同一行为（技能施放/触发条件/Buff 结算/属性结算）不得因玩家/怪物/斗法镜像身份分叉成多套实现，基于 `docs/audits/model-fork-audit-20260923.md` 三路审计结论全量修复；另裁定「剑术/符箓」为六维根基误命名（代码 `Swordsmanship`/`Talisman` 语义对应设计文档「身法」与「神识」且前端内视页映射接反）。五路并行：A 领域层——触发器求值器收敛为单一实现、`CombatSkillEngine` 并入 `DeterministicCombatEngine`、战内 Buff 统一管线（破阵子失效修复）、玩家远征技能并入引擎 `PendingSkillIndex` 单时间线、怪物技能走同一触发模型+校验、成长曲线单点化、灵力单池、事件/RNG 口径统一、死代码清理（TravelEngine、穿透字段等）；B 协议与前端——属性面板下发真值（爆伤/术攻/破招架招/吟唱与冷却系数等）、删前端估算分支、buffs/移动/战报冗余通道收敛、相位命名统一、时钟域统一、敌方 wire 白名单、Hello 指纹回传；C 配置编辑器——DropTable/Abilities/Monsters/Encounters/Triggers/地图 schema 全部对齐真实消费集消除幻影字段；D 文档与数据——combat-simulator 标注非权威、expected-attributes 旧公式与幻影基准修正、设计文档残留旧口径清理；E 六维命名收敛——Swordsmanship→Agility(身法)、Talisman→Consciousness(神识)、Intelligence→Meridians(经脉)、Defense→Bone(根骨) 全链路重命名 + 前端接反映射修正。验证：Domain.Tests 134/134、Server.Tests 86/13（13 为缺 `DAOHUA_MARKET_TEST_CONNECTION` 的 PG 集成测试）、frontend/config-editor build、DataTool 校验通过；遗留：expected-attributes.json tiers 段幻影技能名、物理功法 rootAttribute 现归身法/法术归经脉/神识的归属合理性待数值策划复审。

### TASK-20260923-03 odin 辅助计算运维文档夹 + daohua.yuohira.com 云端部署
- 分类：（原文未标注）
- 时间：提出 2026-09-23 00:16:41｜完成 2026-09-23（云端已上线，DNS/HTTPS/烟测全部验证通过）
- 状态：✅ 已完成（云端已上线，DNS/HTTPS/烟测全部验证通过）
- 摘要：两线交付。odin 侧（AYN Odin 2 掌机，Termux + PRoot Ubuntu，SSH `odin` 8022 每连接 `--kill-on-exit` proot）：.NET Server GC 在 proot 下无法预留 256GiB VA → `DOTNET_gcServer=0`；dockerd 自 2026-08-20 崩溃不可用；落地形态为 Termux 层 `~/daohua_compute_watchdog.sh` 守护（flock 单例 + 崩溃 10s 重启）经 `~/ssh_ubuntu_wrapper.sh` 随 SSH 连接拉起，节点 `/root/daohua-compute/`（本地 linux-arm64 自包含构建）`0.0.0.0:8090` 健康验证通过；本仓库新增 `docs/ops/odin/`（README 设备档案/坑/操作手册、watchdog 参考副本、`redeploy-compute.sh` 一键重发布）。云端侧（`yuohira` = 43.155.140.184）：CCR 凭据两台机器均缺失 → 改走服务器本地构建（rsync 源码按 .dockerignore 排除，原生 amd64 构建 tag `sha-a1f7a2d13a88`/`prod`）；移除旧 `xiuxian` compose 项目（上一代部署占 15128 端口，卷保留）；`/opt/daohua/` 就位 compose.prod.yaml + daohua.env（随机 DB_PASSWORD/COMPUTE_TOKEN）+ 备份脚本 + Caddy 站点模板，db/compute/app 全部 healthy；Caddy 安装 `daohua.yuohira.com → 127.0.0.1:15128`，启用 `daohua-db-backup`/`base-backup`/`ledger-archive`/`wal-archive` 四个 timer；仓库侧域名 `deploy/xx.yuohira.com.caddy` → `deploy/daohua.yuohira.com.caddy` 并同步 `deploy-prod.sh`/`install-ccr-auth.sh`/`install-caddy.sh`/`appsettings.Production.json`/`AGENTS.md`。DNS/HTTPS 收尾：Cloudflare A 记录解析正确、Let's Encrypt HTTP-01 证书签发成功、服务器 loopback 全量烟测全绿（指纹 `507b324ac0add206`）、公网前端/healthz/meta/登录 Cookie/WS 升级全部正常（`/readyz` 公网 404 为 Caddy `@private` 拦截设计行为）；修复烟测脚本公网模式 `SMOKE_REQUIRE_READYZ=0`（断言 `/readyz` ≥400）与 `deploy-prod.sh` 双层烟测，顺带修复 `openWebSocket` 不识别 `https:` 致公网 WS 走明文 TCP 的缺陷。遗留：旧 `/etc/caddy/sites/xiuxian.caddy` 仍在会把旧域名指到新应用、odin 节点接云端主服需经 frpc/cloudflared 暴露 8090 且令牌需换正式值、CCR 凭据补齐后才能启用 `install-ccr-auth.sh --confirm` 与自动更新 timer。

### TASK-20260923-02 普攻/吟唱统一动作时间线模型修正
- 分类：（原文未标注）
- 时间：提出 2026-09-23 00:08:00｜完成 2026-09-23（实现完成 + 深度并行审计完成）
- 状态：✅ 已完成（领域测试 124 全绿；审计发现的分叉修复另行立项）
- 摘要：修正「法术系填充技能（掌心雷）的 2s 吟唱时长」被错误建模为「法术系单位的普攻间隔 = 2s」（致木灵等术法怪面板攻击间隔显示 2s）以及「灵爆」这一不存在的技能概念（文档中「法术普攻 = 灵爆 2.0s」为错误设定）。正确设计：普通攻击全单位统一基准 1.0s 吃行动速度（ATR）与攻击速度双乘区、伤害 100% 物理攻击力（AD）物理伤害法系完全相同；掌心雷为普通技能槽位 0CD 填充技、吟唱 2.0s 只吃行动速度、装配后因 0CD 永远可放而自然完全顶替普攻；单动作时间线——每次出手要么普攻要么放技能，不存在并行双轨。实施：普攻统一 interval = 1.0s ×(1-ATR)×(1-攻速)、倍率 100%→200% AD、删除全部 spell 分支（`GameDataCatalog.ParseMonsterDefinitions`/`PlayerAttributeResolver` 旧口径清除）；战斗引擎改单动作时间线（`PendingSkillIndex` 吟唱占位 + 逐槽冷却循环 triggerHpRatio + cooldownMilliseconds，移除 `UsedSkillMask` 一次性语义）；玩家吟唱期间压制普攻 tick；`ExpeditionEnemyState`/`CombatantSnapshot` 增吟唱与冷却持久化字段；前端敌方行动条支持吟唱展示；`combat-benchmark-model.md` 灵爆/法术普攻表述修正。验证：领域测试 124 全绿；兜底残留修正 `PlayerAttributeResolver` 无配表兜底 interval 2→1（该修正随并行提交混入 `80e936c feat(social)`，内容已在主干仅提交归属有误）。深度并行审计：三路只读审计（领域/配置文档/前端协议快照）发现 6 项高危分叉——触发器三套求值器、Buff 技能双管线致破阵子失效、玩家远征技能外挂调度、config-editor schema 大面积脱节、combat-simulator 第三套脱钩模型、属性面板硬编码占位，结论落档 `docs/audits/model-fork-audit-20260923.md`，修复另行立项（后续由 TASK-20260923-04 承接）。

### TASK-20260923-01 储物袋面板分组展示
- 分类：（原文未标注）
- 时间：提出 2026-09-23 00:03:58｜完成 2026-09-23（build 通过，待验收）
- 状态：✅ 实现完成，待验收
- 摘要：储物袋（背包）面板弃用顶部 Tab，改为同一滚动视图中按小标题分段陈列物品、一段排完后另起新段，固定五组：重要（货币/特殊物品）、消耗品（战斗消耗与持续消耗品全部归入）、装备法宝、功法、材料。实现：`frontend/src/game/ids.ts` 新增 `InventoryGroupKey`/`INVENTORY_GROUP_ORDER`/`resolveInventoryGroup(itemId, items, equipment)`——以物品目录 `ItemDefinition.kind`/`category` 键与装备目录命中为准，`itemId` 前缀（`equipment.*`/`item.tome.*`/`item.fragment.*`/`currency.*`）作目录未加载兜底，空组不渲染、组内保持服务端资产顺序；`InventoryPanel`（RightSidebar.tsx）按组渲染小标题 + 原 `inventory-grid` 瓦片网格；词条收拢 `app.inventoryPanel.group.*`；样式新增 `.inventory-group*`（沿用 abilities 分区小标题视觉语言）。验证：`npm --prefix frontend run build`（含 i18n 导出 + tsc + vite）2026-09-23 通过。


### TASK-20260920-03 日志分组重构与聊天（传音）系统全量落地
- 分类：未标注（设计真源：[docs/design/传音频道与日志分组.md](../../design/总纲/14-传音频道与日志分组.md)）
- 时间：提出 2026-09-20 00:14:34｜完成 无（归档时 🚀 进行中，实施与验证记录均止于 2026-09-20 当日）
- 状态：🚀 进行中（归档时停滞）
- 摘要：游戏内日志拆 8 分组（综合合并系统/好友/队伍三源），聊天系统全量落地（世界/附近/好友/队伍真实可用，宗门仅占位符），聊天记录落 PostgreSQL 每频道作用域保留最近 100 条。
  协议：`LogEventDto.Channel`、`WireMessageType` 18-23（`ChatSend`/`ChatMessage`/`ChatHistory`/`SocialAction`/`SocialState`/`ChatHistoryRequest`）与聊天/社交 DTO；服务端 schema v4 新增 `social` schema 六表（friend_requests/friendships/parties/party_members/party_invitations/chat_messages），新增 `src/Server/Clean/Social/`（`ChatRuntime`/`SocialRuntime`/`PostgresChatRepository`/`OnlinePresenceRegistry` 等）；前端日志面板重构 8 分组 Tab，新增 `chatService`/`socialStore`/`LogFeed.tsx`/`ChatPanel.tsx`，`NearbyPanel` 人物 Tab 接入真实同节点修士（加好友/邀请组队）。
  验证：Release 构建 0 错 0 警、Domain.Tests 120/120、Server.Tests 99/99（含新增 `ChatAndSocialTests` 13 项）、前端 build、日志测试 12/12 通过；附带抽出 `tests/Server.Tests/PostgresTestSchema.cs`（advisory lock + 退避重试）修复集成测试并发建库竞态（23505/42P01）。
  追加：明确「数据库不自动迁移」为刻意设计（启动门禁只做空库直建+版本校验），本地 `daohua` 库就地升级 v4（41 账号/41 存档保留），新增 `scripts/migrate-local-db.sh` 链式迁移入口（模拟 v4/v3/v2 三种起始状态均收敛到 v4）。遗留：`MarketPersistenceTests` 复用库残留挂单致断言失败（全新库首次运行通过）；4 个临时验证库（daohua_test/daohua_test_fresh/daohua_test_clean/daohua_test_clean2）需人工清理。

### TASK-20260920-02 地图目标地点怪物悬浮详情卡
- 分类：未标注
- 时间：提出 2026-09-20 00:08:35｜完成 无（原文无独立状态行；进度日志记录 2026-09-20 实现与验证通过）
- 状态：✅ 实现完成（据进度日志，验证通过）
- 摘要：世界地图「栖息生灵」怪物胶囊新增 Hover 悬浮详情卡（名称/境界等级/品阶/掉落物/击败经验/生命灵力双攻双防）。
  修复既有类型失配：`WorldNodeDefinition.monsters` 原声明为 `NearbyMonsterDefinition[]`（`monsterId` 键恒 undefined），改 `CombatantDefinition[]`；`StaticGameCatalogMapper.ResolveMonster` 补 level/tier/budgetCost/drops/qiReward/rootExpReward，`maxManapoints`/`magicResistance`/五行抗性/`experience` 改从 `CombatMonsterDefinition` 解算结果取值（原从配表 JSON 顶层误读恒 0）。
  `WorldMapPanel` 新增 `MapMonsterPill`（`FloatingPopover`+`FloatingInfoCard`，位阶徽记妖兽/精英/妖王），双攻双防口径复用 `resolveEnemyCombatAttributeModel`，境界名复用 `calculateRealmInfo(0, level)`；`combat.map` 新增 4 词条。
  验证：`dotnet build src/Server` 0 警 0 错、`npx tsc --noEmit`/`npm run build`/`check-frontend-contracts.mjs` 通过；追加掉落改 `ItemTileReference` 图标瓦片（锚点类 `world-monster-drop-anchor` 入 `selection-grid.css` 共享 `:is()` 组），「物品/装备/材料/消耗品/功法技能一律图标化展示」写入 `AGENTS.md` §8 与 `common-components.md` §6。

### TASK-20260920-01 移除地图节点纯采集提示建筑
- 分类：未标注
- 时间：提出 2026-09-20 00:04:50｜完成 无（原文状态：实现完成，待验收）
- 状态：✅ 实现完成，待验收
- 摘要：移除地图节点内嵌 `buildings` 中 effect 为「X 采集点/采摘点/采伐点/开采点」的纯提示条目共 7 处：`qingyun_mountain.json` 4 处（building.shuyu-stones/building.herb-rack/building.peach-bridge/building.mining-shed）、`xingyun_realm.json` 3 处（building.carrion-monument/building.scorched-branches/building.copper-chasm），均连同 `buildings` 属性一并移除；保留古井/古槐（叙事）、茶棚（灵市）、山门牌坊/道庐/石台（修炼与试炼）、界碑（边界）、血坛/骨堆/星核（巢穴）。
  地图节点内嵌 `buildings` 为纯展示实体（`ProtocolSnapshotMapper.ReadEntities` 透传，前端 NearbyPanel/WorldMapPanel 消费），与玩家可建造的 `NodeBuildingRegistry` 建筑无关。
  验证：`dotnet run --project src/DataTool -- validate config/game-data --strict` 通过（gamedata.v1，27 文件，163 ID，无引用断裂）。

### TASK-20260919-08 战场单位详情弹窗重构：复用内视斗法属性面板，角色/敌怪统一口径
- 分类：未标注
- 时间：提出 2026-09-19 21:03:09｜完成 2026-09-19 21:10:00
- 状态：✅ 实现完成，待验收
- 摘要：斗法战场 `CharacterAttributeModal` 口径漂移修复：新增共享组件 `frontend/src/components/CombatAttributePanel.tsx`（`CombatAttributePanelModel` 归一化模型 + `resolvePlayerCombatAttributeModel`/`resolveEnemyCombatAttributeModel` 解析函数），角色/敌怪走同一组件同一字段口径，渲染内视斗法同款三组胶囊分区（基础/特殊/增免伤与五行，复用 `.insight-sub-tabs` 二级导航），词条统一 `insight.combat.*`；移除角色面板错误的六维根基行与手写 `combat.attr.*`/`combat.special.*` 散乱行。
  `InsightPage` 斗法 Tab 改用共享面板；`CombatArena` 弹窗改为头部（立绘+道号+阵营徽章）+`CombatAttributePanel`；删除失效 i18n 键（`combat.attrs.base|special|effects`、`combat.special.*` 整块等，`hp/mp/seconds` 与六维名仍被复用保留）与失效样式。
  取舍：敌怪五行抗性点数配表全为 0 不展示；攻击间隔/控制抗性/吸血/汲蓝等旧字段移除（出手间隔由行动条承载）；掉落率/稀有度属经济属性一并移除。
  验证：`npm run build`（i18n CSV 重生成 + tsc -b + vite）通过，删除键经强类型 TranslationKey 校验无残留。

### TASK-20260919-07 功法面板修正：移除秘籍物品列表、已习得功法分内功/术法两组、功法 discipline 槽位互斥
- 分类：未标注
- 时间：提出 2026-09-19 19:10:05｜完成 无（原文状态：实现完成，待验收；二轮/三轮迭代同日完成）
- 状态：✅ 实现完成，待验收
- 摘要：配表 10 门功法新增 `discipline` 字段（`internal`：嘲讽/清平乐/破阵子 3 门非伤害辅助；`spell`：其余 7 门）；`CombatSkillEngine.ValidateAbility` 校验取值（缺省视为 spell），`LoadoutCommand.ApplyValidated` 槽位双向互斥（0 号位只收 internal、其余位只收 spell，违例抛 `DomainException`）。
  前端：移除功法面板秘籍/残卷物品列表（由背包物品菜单承接）；已习得功法按内功心法/神通术法两组瓦片网格渲染；新增 `normalizeAbilitySlots`/`assignAbilityToSlot`（重复装配改移动语义，触发条件随迁）与 `sendLoadoutReplace` 唯一发送入口（功法面板/战斗底栏/装备面板/背包 4 个发送点收口，修复后两者绕过归一化致存量非法装配让换装命令被整体拒绝）；取消槽位 Lv 输入框改静态参悟等级徽标（「技能 lv 一直 1」根因：`slot.level` 从不自动跟随且调低纯负收益，存量 1 级槽位任意一次装配保存后自愈）。
  二轮：配表补 `description` 招式描述、掌心雷/雷光引 `element` 改 wood（雷属木）；`describeAbility` 重构（效果句+伤害/治疗构成胶囊，修复 `targetType` wire camelCase 与前端 kebab 不匹配致 allEnemies 恒回退「当前敌人」、无 cooldownMilliseconds 显示 NaN）；三轮：构成公式移入主描述句，`ValueText` 新增 `[[ ]]` 组标记整段渲染为完整胶囊。
  验证：Release 构建 0 错 0 警、PlayerRuntimeTests 17/17（二轮 19/19，含新增 `LoadoutReplaceEnforcesInternalSpellSlotDiscipline`）、DataTool --strict、前端 build 均通过。

### TASK-20260919-06 统计/记录面板物品得失改为背包式图标瓦片
- 分类：未标注
- 时间：提出 2026-09-19 18:32:11｜完成 无（归档时 🚀 进行中，构建验证已通过）
- 状态：🚀 进行中（归档时停滞）
- 摘要：`ItemDisplay.tsx` 新增导出组件 `ItemTileReference`（仅凭 itemId+amount 自动解析物品/装备目录与静态元数据，渲染 `ItemTooltipTarget`+`ItemTile`：图标整块、品质边框、右下数量角标、左上品类丝带、右上等阶、悬浮信息卡片）；`reportSections.tsx` 的 `ReportItemsSection` 获得/失去两列改用之，锚点类 `battle-report-item-anchor` 入 `selection-grid.css` 共享 `:is()` 选择器组；`.battle-report-items` 改 `--item-slot-size` 网格。
  二次调整（用户验收反馈）：`repeat(auto-fill)` 网格在不定宽弹性列塌缩致纵向单列，改 flex 横排换行；`ReportBlock` 小节顺序改为行动名称→行动信息→物品得失→获取经验；三小节标题全部移除只留内容，删 `expTitle`/`itemsTitle`/`infoTitle` 三条 i18n 死键。斗法暂存区保留 `ItemReference` 胶囊（每条附到期倒计时）。
  验证：`npm run build`（tsc + vite）2026-09-19 两次通过，无类型错误。

### TASK-20260919-05 储物袋物品交互：左键操作菜单 + 右键直接使用 + 丢弃/系统回收服务端命令
- 分类：未标注
- 时间：提出 2026-09-19 18:31:19｜完成 无（原文状态：实现完成，待验收）
- 状态：✅ 实现完成，待验收
- 摘要：新增 `frontend/src/components/InventoryItemCell.tsx`：左键经 `FloatingPopover`（trigger="click"）弹操作菜单，条目按物品类型动态裁剪（使用：秘籍/残卷发 `ability.learn`、装备发 `loadout.replace`；交易：跳坊市选中标的；回收：`sellPrice>0` 且非 `currency.*` 走 `item.sell`；丢弃：`item.discard`，已装备禁用），右键直接使用；丢弃/回收共用 `ItemQuantityDialog`。
  服务端 `PlayerRuntime` 新增 `item.discard`/`item.sell`：必须 `requestId` 幂等；已穿戴装备拒绝（`ITEM_EQUIPPED`）；sell 仅 `SellPrice>0` 普通物品（装备与 `currency.*` 拒绝，`ITEM_NOT_SELLABLE`）；`economy.CommitAsync` 单事务扣物品+贷记灵石（`sellPrice×quantity`），幂等键 `item.discard|item.sell:{playerId}:{requestId}`，`ShouldPersistReceipt` 纳入两条新命令。
  取舍：不新增 `item.use` 命令，使用语义分流既有权威路径；「出售」为系统回收，玩家间交易走坊市 `market.place`。
  验证：Release 构建 0 错 0 警、PlayerRuntimeTests 16/16（新增 6 项：原子扣减、超库存/不存在/零数量拒绝、2.5 单价×4=10 灵石同事务入账、同 requestId 重放返回 `IDEMPOTENT_REPLAY` 不重复入账、已装备拒绝等）、前端 build 通过。

### TASK-20260919-04 全面深度并行分析优化与文档收敛
- 分类：未标注
- 时间：提出 2026-09-19 06:01:02｜完成 无（原文无状态行；进度日志记录阶段 A-D 全部完成、分批原子提交 5 笔）
- 状态：✅ 已完成（据进度日志；代码类风险遗留待后续迭代）
- 摘要：六路并行只读深度分析（B1 Domain+Protocol、B2 Server、B3 ComputeNode/LoadTests/DataTool、B4 frontend、B5 deploy/scripts/compose/Caddy、B6 docs 盘点），产出风险总报告 `docs/audits/deep-risk-analysis-20260919.md`（高 10/中 19/低 23，附文件行号证据）。
  文档收敛：口径修复（duel 第八分段全库漏登补齐、deployment Bearer 回退失实删除、generic-map 字段真名与波次上限 3 修正、总纲生产卷名更正等）；新建 `docs/archive/`（design/discussions/analysis/perf/BUGLOOP）与 `docs/future/` 归拢，performance-baseline 拆分，docs/README.md 索引重写零死链，tasks.md 修重号/死链/孤儿行。
  提交：`8222c18` 登记、`9bcccb3` 报告、`0ee52aa` 口径、`8e1963a` 归拢等共 5 笔原子中文提交，未卷入用户未提交改动。
  遗留：H-01/H-02/H-03 ComputeNode 链路在生产默认启用下持续吞没离线收益并清零 `completedActions`（建议立即 `ComputeNode:Enabled=false` 止血）；H-04 `simulation.advance` 未钳制客户端 targetTime（单行可修）；H-05 ForwardedHeaders 信任网段待真实环境核对；H-08/H-09 增量快照丢帧与解码竞态需前后端协同修复。

### TASK-20260919-03 控制台降噪与本地运维信息面板（2026-09-19）
- 分类：开发体验 / 日志治理 / 服务端运维面板 / Development-only 端点
- 时间：提出 2026-09-19 03:56:25｜完成 2026-09-19 04:06
- 状态：✅ 已完成（2026-09-19 04:06）
- 摘要：控制台降噪：`appsettings.Development.json` 将 `Microsoft.AspNetCore` 提升为 Warning（屏蔽 `WebRootPath was not found` 等基础设施 info 噪声）；`MarketRuntime.StartAsync` 租约重试最多 39 行重复告警收敛为 1 行（首次 Warning 后续 Debug）。
  新增 Development-only 运维面板（`Ops:Enabled`+`Environment.IsDevelopment()` 双门禁，默认 `0.0.0.0:5129`、路径 `/ops`，局域网可访问）：`/ops/api/overview|health|players|sessions|market/book|logs` 端点 + 自包含 HTML 页；`OpsLogBuffer` 有界环形缓冲（`ILoggerProvider`）仅启用时注册；端口门禁中间件仅允许私网/回环来源，主端口访问 `/ops` 返回 404；生产不注册任何 Ops 端点，`deploy/xx.yuohira.com.caddy` 追加 `/ops/*` 拦截兜底。
  修复面板路由重复注册的 `AmbiguousMatchException`（单路由覆盖 `/ops` 与 `/ops/`）；后续迭代补全中文化与日志按等级分组 Tab。
  验证：Release/Debug 构建 0 警 0 错、Server.Tests 非集成 69 项（含新增 `OpsPanelTests` 21 项）、Domain.Tests 120 项全绿；隔离烟测（独立临时库+独立端口）面板端点与 404 门禁全部符合预期。

### TASK-20260919-02 文档与实现一致性审计整改（2026-09-19）
- 分类：文档治理 / 一致性审计 / 配置与端点口径 / 设计真源补写 / i18n 清理
- 时间：提出 2026-09-19 03:49:24｜完成 2026-09-19 03:57（整改完成并门禁验证）
- 状态：✅ 已完成
- 摘要：以 `docs/audits/doc-vs-implementation-20260919.md` 列出的 16 项不一致为清单全部整改：schema v2→v3 五份文档全局对齐（commercial-persistence-architecture/domain-data-architecture/player-data-migration-and-reconciliation/deployment/README）并登记 `game.node_buildings` 表与 `0002_v2_to_v3.sql` 迁移口径；README 补 `/api/v1/catalog/buildings`；`TokenValidator` 去掉未文档化的 `Authorization: Bearer` 回退（只接受 X-Compute-Token）。
  设计真源：新增 `design/节点公共建筑系统.md`、`design/行动统计系统.md` 并在 `总纲.md`（7.3 节+第十三篇）与 `docs/README.md` 登记；重写 `通用地图与遇怪系统.md` §5.1/§5.2/§6；`domain-data-architecture.md` 移除不存在的 `CombatAbilityDefinition`、补 `NodeBuildings.cs` 与 World 分段字段；`game-data.md` 补 `buildings/` 校验清单；`总纲.html` 全量同步。
  i18n：清理 15 个死键（`combat.nearby.*` 10、`app.avatar.*` 4、`common.repeatsOnce`），重生成 `template.csv`（1408→1393 条）。
  验证：`scripts/verify.sh` 通过、Release 0 警告、Domain.Tests 120/120、Server.Tests 48/57（9 项为缺 `DAOHUA_MARKET_TEST_CONNECTION` 的既有环境前置）、前端 build、DataTool --strict、契约检查通过。

### TASK-20260919-01 行动统计系统（战报泛化 + 三账本 + 刷新自动进详情）
- 分类：全栈新功能 / 领域账本 / 协议对齐 / 前端改版
- 时间：提出 2026-09-19 03:11:06｜完成 2026-09-19（验证结果同日落盘）
- 状态：✅ 已完成（2026-09-19）
- 摘要：三账本落地：`PlayerDuelState.ActionReports`（`Dictionary<Guid, PlayerBattleReport>` 按行动实例记账，轮转出队首不清零、出队才归档）/`OfflineReport`（离线会话重置）/`DailyReport`（UTC+8 自然日 rollover，含 DayIndex）；`PlayerBattleReport` 扩展 `ItemsLost`/`AbilityExp`/`LevelUps`/`Cycles`/`Encounters`/`NormalKills/EliteKills/BossKills`/`Deaths`；`PlayerAggregate.RecordStats` 同一记账 apply 三账本，`EnsureActionReport`（Blocked 不建账）/`ArchiveReportForAction` 挂 `RemoveAction`/travel 去重/`CompleteOrRequeueHeadAction` 三处出口；交易不计入统计。
  `SimulationEngine` 全记账点（ApplyContinuousReward/CompleteRepeat/ApplyCombatRewards/DistributeRootExp/波次与分级击杀/自动与即时消耗品扣件）；`PlayerRuntime` 把 `SetPresence(Online)` 移到首次推进后使重连补算收益归离线账本；前端 `BattleReportPanel` 经 `git mv` 重构为 `StatsPanel.tsx` 五分区（当前行动/离线挂机/今日统计/斗法暂存/近期战报），首快照且队列非空自动进详情 Tab（仅首次加载触发一次）。
  持久化：`PlayerDuelStateDocument` 增 `ActiveReports`/`OfflineReport`/`DailyReport`（离线/每日 ReportId 固定 `"offline"`/`"daily"`），导出兼容旧 `CurrentReport`。
  验证：Domain/Server 构建 0 警 0 错、Domain.Tests 120/120（含新增 `ActionReportTests` 8 用例：轮转续记+出队归档、UTC+8 跨日 rollover、导出恢复往返等）、前端 build、契约检查通过；Server.Tests 48 过/9 失败均为缺 `DAOHUA_MARKET_TEST_CONNECTION` 既有前置。

### TASK-20260918-06 建筑系统完整落地（公共建造/双进度/贡献/升级/效果接线）
- 分类：全栈新功能 / 领域模型 / 共享持久化 / 协议对齐 / 数值平衡
- 时间：提出 2026-09-18 20:10:00｜完成 无（原文状态：实现完成，待验收；2026-09-18 23:55 验收反馈迭代完成）
- 状态：✅ 实现完成，待验收
- 摘要：设计定稿（用户六轮问答）：纯公共设施（无建造者、同节点同种仅一座、效果对同地所有玩家生效）；节点级共享注册表 `NodeBuildingRegistry`（进程内缓存+单一信号量串行化+惰性完工落库）+ `PostgresNodeBuildingRepository`（`game.node_buildings` 表，(node_id,building_id) 主键 upsert，材料 jsonb）。
  双进度条：建造进度 = min(材料%, 已耗时/本级工期)，工期 `600×(1+0.2(N-1))` 秒（51 级=110 分钟）、材料需求 ×1.1^(N-1) 指数成长，无等级上限，升级期间维持旧级效果、未建成不生效；`building.build`/`building.upgrade`/`building.contribute` 三命令（contribute 在注册表锁内校验钱包+原子扣减防并发负库存）。
  效果接线：`src/Domain/World/NodeBuildings.cs` 的 `BuildingEffectSet` 7 项效果，行动耗时 ÷(1+speedRatio)、收益乘区 ×(1+outputRatio)、建筑回血并入 hpRegenFlat、减伤并入 absorption（[0,1] 上限）；`PlayerWorldState` 增 `NodeBuildings` 与跨节点投影守卫 `EffectiveNodeBuildingEffects`（防旧节点效果泄漏）；快照 `world.nodeBuildings`/`world.buildableBuildingIds`（旧 `playerBuildings` 移除）。
  验收迭代：怪物/人物/建筑三分类从「附近」子 Tab 上移为地图页顶部 Tab（`MapSubTab` 扩为 map|monsters|people|buildings）；工期条改有效工时显示（封顶于材料进度）；6 建筑补齐 group/modifiers/materials，15 个地图节点全配 `buildableTemplate:"all"`。
  验证：Server 构建 0 警 0 错、Domain.Tests 111/111（新增 9 个建筑规则测试）、前端 build、契约检查、DataTool --strict（27 文件 163 ID，指纹 19cbdf2732fa4d68）通过。

### TASK-20260918-05 前端数值取整、附近面板 Tab 拆分与建筑功能基础落地
- 分类：前端体验 / 全栈新功能 / 协议对齐 / 配表
- 时间：提出 2026-09-18 19:05:00｜完成 无（原文状态：进行中（实现完成，待验收））
- 状态：🚀 进行中（实现完成，待验收）
- 摘要：展示层取整：战斗属性/气血灵力/怪物面板/行动预览收益 `formatNumber(v, { maxDecimals: 0 })`，百分比 `Math.round(ratio*100)`，攻击间隔整数秒，`PercentValue.format()` 默认 0 位小数——仅展示层，领域精度不变。
  附近面板拆怪物（普通+精英+首领）/人物（NPC+附近玩家）/建筑三段 Tab（本地 useState）；`.nearby-layout` 改弹性列 + `.nearby-scroll` 滚动区（flex:1; min-height:0; overflow-y:auto）解决底部裁剪。
  建筑基础全栈闭环：`config/game-data/buildings/buildings.json` 初版 6 建筑（采葛营帐/携行丹灶/聚灵蒲团/淬火铁砧/护阵石龛/养脉泉眼，境界门槛 1~43）；`BuildingDefinition` 入 `GameDataCatalog`、`PlayerWorldState.Buildings` 持久化、`BuildBuilding` 聚合去重、`building.build` 命令（定义/在途/地点/境界/重复五重校验）、`/api/v1/catalog/buildings` 端点、world 分段 `playerBuildings` 输出；前端加号卡+候选弹窗按「此地可建+已解锁+未建同类」过滤；DataTool 增 `availableNodeIds` 节点引用校验。建筑效果数值接线留待后续讨论。
  验证：Server 构建 0 警 0 错、Domain.Tests 102/102、前端 build、契约检查、DataTool --strict（27 文件 163 ID，指纹 4e220d35770c3837）通过。

### TASK-20260918-04 战斗反应钩子管线（接口 + 上下文传参 + 特性排序）与吸血/吸蓝/反伤落地
- 分类：领域战斗 / 结算管线 / 新机制
- 时间：提出 2026-09-18 18:45:03｜完成 2026-09-18
- 状态：✅ 已完成（2026-09-18）
- 摘要：新增 `src/Domain/Combat/CombatHooks.cs`：`CombatEffectProfile` 效果强度参数包（挂 `CombatantStats.Effects`，后续加效果只加字段不改签名）、`CombatHitContext` 单次攻击结算上下文（含 `NextRoll` 确定性随机与 `DerivedEvents`）、`CombatHookOrderAttribute` 排序特性（同值按类型全名字典序兜底保证跨进程确定性）、三阶段接口 `ICombatResolveMutator`/`ICombatDamageMutator`/`ICombatAftermathHook`、`CombatHookPipeline` 静态反射扫描冻结有序数组（运行期零反射）。
  `ResolveAttack` 为唯一插桩点（普攻/玩家技能/怪物技能/斗法镜像汇流），衍生伤害直接落账不走管线防递归；实装 `HpDrainHook`(100)/`ManaDrainHook`(110)/`DamageReflectHook`(200)。
  数据通路：装备 `specialMechanics` 三字段与怪物配表 `hpDrainRatio`/`mpDrainRatio`/`damageReflectRatio` → `CombatantStats.Effects`；`ProtocolSnapshotMapper` 玩家 `hpDrainRatio` 从硬编码 0 改真实结算值；`tools/config-editor` 补字段；`战斗设计.md` 新增 §7.4。
  验证：Domain/Server Release 构建 0 警 0 错、Domain.Tests 102/102（含新增 `CombatHookTests` 6 项：吸血回血、吸蓝转移、反伤落账、衍生伤害不递归、确定性一致）、Server.Tests 57/57。

### TASK-20260918-02 战斗事件时序对齐与敌阵纵深布局
- 分类：运行时调度 / 前端体验 / 布局调整
- 时间：提出 2026-09-18 16:19:24｜完成 无（原文状态行 🚀 进行中；「完成情况」小节记录 2026-09-18 已实现并验证）
- 状态：🚀 进行中（归档时停滞；完成情况小节记录实现与验证已完成）
- 摘要：根因：事件统一在每秒 `AdvanceAllAsync` 批量推送，出手时刻 T 的事件晚 0~1 秒到达而行动条按绝对截止时间渲染，伤害/动画/行动条永远错拍。改为边界驱动调度：`SimulationSchedule.NextVisibleBoundaryUtc` 从聚合状态推导下一可见事件墙钟（玩家/敌人出手、吟唱冷却、相位切换、斗法剧本事件、行动完成、Buff/自动使用冷却取最小），`PlayerActor` 每次成功命令后锁内刷新边界（volatile ticks 无锁读取），`PlayerSimulationHostedService` 改 `Task.Delay(clamp(min(boundary)-now, 50ms, 1s))` 边界即醒即推、无边界 1 秒兜底；推进失败清空边界防亚秒空转；不新增协议字段。
  敌阵改纵深铺位 `[1,4,7,2,5,8,3,6,9]`（前中→中中→后中→前上…），单敌仍前排居中。
  验证：Domain.Tests 96/96（含新增 `SimulationScheduleTests` 5 项）、Server.Tests 57/57（专用测试库清库后全过）、前端 build 与契约检查通过。

### TASK-20260918-01 全项目「契约 + 意图」两级中文注释补齐
- 分类：未标注
- 时间：提出 2026-09-18 05:43:18｜完成 无（原文状态：✅ 已完成，未记录完成时刻）
- 状态：✅ 已完成
- 摘要：8 路并行 subagent（文件集互不相交：Domain 23、Server 持久化族 17、Server 其余 20、Protocol+ComputeNode+DataTool 27、LoadTests 16、前端组件 37、前端其余 37、tests 30）为全项目补两级注释：契约层 public/internal 类型与方法 XML 文档注释（C# `///`、前端 `/** */`），意图层仅为「看代码推不出原因」的实现写 why 注释；排除 vendor/YuoCore、生成代码、migrations、tools/ 等。
  首轮后复查发现 B1（Server 持久化族）漏约 255 处占位符，恢复该 agent 补完 14 个文件清零。
  验证：`dotnet build Game.slnx` 0 警 0 错、前端 `tsc -b` 通过、Domain.Tests 91/91；diff 纯净性脚本校验全部只含注释行变更（ALL_CLEAN_COMMENT_ONLY），用户未提交改动未回退；占位符残留 grep（「执行 .* 操作」「参数。」「返回处理结果。」）在 src/tests/frontend 全量清零。

### TASK-20260917-05 战斗显示回归修复与战报弹窗化
- 分类：Bug 修复 / 前端协议映射 / UI 改版
- 时间：提出 2026-09-17 19:45:00｜完成 无（原文状态：✅ 已完成，未记录完成时刻）
- 状态：✅ 已完成
- 摘要：根因：多敌同场战斗重构（提交 8198a9f/a9f6325）后服务端 `expedition` 载荷改为裸领域 `CombatExpeditionState`，不再下发 `playerMaxHitpoints`/`enemyMaxHitpoints`/`enemyName`/`playerAction`/`enemyAction` 等展示派生字段，前端仍消费旧字段致血量兜底 1、行动条恒 null；`mapExpeditionDuel` 字段名与领域 `DuelPlaybackState` 序列化名不一致（isInitiator/outcome vs viewerIsInitiator/viewerOutcome）。
  修复：`mapExpedition` 透传 `battleElapsedSeconds`/`playerNextAttackAtSeconds`，`mapExpeditionDuel` 修正字段名并解析对手血量与剧本事件；`CombatActionProgress` 双方总血量（敌方=enemies 当前血量和/配表 maxHitpoints 和）；`CombatArena` 由 `nextAttackAtSeconds - battleElapsedSeconds` 与攻速合成行动条；战报改详情头部右上角按钮 + `Modal` 弹窗（新增 `ghost-button`/`battle-report-modal` 样式）。
  附带修复技能冷却/吟唱断源：`ToCombatSection` 补发 `skills.cooldowns`/`castingAbilityId`/`castRemainingMilliseconds` 与 `consumableCooldownMilliseconds`。
  验证：`npm run build`、`check-frontend-contracts.mjs`、`dotnet build Server`、Server.Tests 57/57 全部通过。

### TASK-20260917-04 独立防多开模块落地（标签互斥 / 单账号互踢 / 设备配额三开关）
- 分类：未标注（前置评审：TASK-20260917-03；设计真源：[../backend/anti-multi-open.md](../../backend/anti-multi-open.md)）
- 时间：提出 2026-09-17 19:20:00｜完成 无（原文状态：✅ 已完成（含端到端冒烟），未记录完成时刻）
- 状态：✅ 已完成（含端到端冒烟）
- 摘要：新建 `src/Server/Clean/AntiMultiOpen/`（`AntiMultiOpenOptions` 配置节、`AntiMultiOpenConstants` 关闭码 4005-4008、`DeviceQuotaRegistry` 指纹→(PlayerId→引用计数)、`AntiMultiOpenAdmission` Hello 期统一裁决）+ 前端 `frontend/src/antiMulti/`（`tabGuard.ts` BroadcastChannel 竞逐 bornAt+tabId 决胜 + localStorage 租约降级、`deviceFingerprint.ts` SHA-256/FNV 降级、`MultiTabBlockedPage.tsx`）；三道防线独立开关，不引入 Redis（进程内注册表）。
  协议：`HelloRequest.DeviceFingerprint`、`HelloResponse.AntiMultiOpen`/`AntiMultiOpenConfigDto`；`WebSocketRuntime` 改顶替语义（`SessionRegistry.AddOrKick`、准入推迟至 Hello、踢出后等 `TeardownCompleted` 再加载 Actor、finally 按 `Release` 返回值决定 `RemoveAsync` 防误删新会话 Actor），顺带根治刷新竞态「会话过期」误杀。
  冒烟修复：顶替连接只收到 1006——`linked.Cancel()` 中止阻塞接收会把 socket 置 Aborted 致后续 `CloseAsync` 无效，改为取消接收循环之前先发送自定义关闭帧。
  验证：Release 构建 0 错 0 警、AntiMultiOpenTests 8/8、全量 Server.Tests 48 过（9 失败为既有 PG 环境前置）、前端 build 通过；端到端冒烟：互踢旧连接收 `SESSION_KICKED` 以 4005 关闭、同指纹第二账号收 `DEVICE_QUOTA_EXCEEDED`+4006 拒绝、不同指纹正常放行。

### TASK-20260917-03 防多开方案评审（多标签互斥 / 单账号互踢 / 设备指纹并发限额）
- 分类：未标注
- 时间：提出 2026-09-17 19:14:49｜完成 无（原文状态：✅ 评审完成，仅分析未改动运行代码）
- 状态：✅ 评审完成（仅分析，未改动运行代码）
- 摘要：只读评审外部「防多开」两阶段方案（BroadcastChannel 标签互斥 + Redis 单账号互踢；设备指纹 + Redis ZSET 并发限额）对照本仓实现：`SessionRegistry`（进程内 `ConcurrentDictionary`）已有单连接但为「拒绝新连接」语义（`PolicyViolation`/1008 关新 socket），与踢旧方向相反；全项目无 Redis（TASK-20260827-01 ADR 基线不部署），必须进程内实现；`HelloRequest` 无指纹字段需扩协议。
  关键发现：陈旧会话误杀重连（刷新竞态下新连接吃 1008 永久停连显示「会话过期」）；踢旧语义引入 actor 误删竞态（`HandleAsync` 的 finally 无条件 `RemoveAsync`）；BroadcastChannel 守卫无法在资源下载前阻断；外部方案 localStorage 降级方向颠倒（storage 事件只在其他标签页触发）、双页近同时打开互判竞态、`window.stop()` 不终止 rAF/定时器/WebSocket；指纹定位为「提高成本」非「身份证明」（iOS/Safari 掩蔽 `UNMASKED_RENDERER_WEBGL`、可伪造），建议指纹+IP 组合。
  落地建议：`SessionRegistry.TryAddOrKick` 原子互踢 + 独立关闭码 4005（区别于 1008/4001-4004）、进程内 `DeviceRegistry` 配额（socket 生命周期为真值，无需心跳/TTL）、拒绝路径握手早期返回明确 `ErrorDto`。结论附真实文件行号证据（`WebSocketRuntime.cs:120-124`、`gameClient.ts:219`、`Contracts.cs:326`）。

### TASK-20260917-01 斗法（PVP）与战斗收益战报
- 分类：全栈新功能（领域模型 / 匹配运行时 / 协议对齐 / 前端面板 / 配表数值）
- 时间：提出 2026-09-17 04:20:00｜完成 无（原文状态行 🚀 进行中（设计定稿，开始实施）；进度日志记录 06:20 全栈落地完成）
- 状态：🚀 进行中（归档时停滞；进度日志记录全栈落地与验证已完成）
- 摘要：设计定案：斗法勾选行动 → 每场战斗入口概率 roll 增遭遇计数（`DuelEncounterChancePerBattle=0.005`，期望约每 2 小时一次）→ 计数>0 进入 `DuelPending` 由运行时异步撮合（实力=六维等级总和，区间 `[0.7P, 1.3P]`，候选空不消耗计数）→ 一次权威解算 `DuelScriptEngine.Resolve` + 双方脚本回放（`MaxDuelSeconds=180` 超时判 Draw）；中途结束行动/actor 卸载直接判负；roll 由远征种子派生保证离线补算位级一致。
  结算：暂存区 20 分钟（`StagingWindowSeconds=1200`）内战利品不入钱包，被击败归胜方（胜方所得直接入钱包防链式风险）；败方冷却 3600s；斗法期爆率 +20%（`DropBonusPoints=20`，×1.2 cap 1.0）；击杀奖励按配表 `qiReward`/`experienceReward`/`rootExpReward`（先统一填 10 禁止写死），六维经验按功法 `rootAttribute` 均分、无功法六维均分；战报 `PlayerBattleReport` 服务端保留 10 条，前端 localStorage 按 reportId 去重 cap 50。
  落地：`PlayerCoreAttributes` 改存六维经验（`RootAttributeCurve.Level = floor(cbrt(exp/100))+1` 与前端曲线一致）；`PlayerSectionKind.Duel=8`、schema v2→v3；`DuelRuntime` 三段撮合（reserve→script→attach）与弃战路由；新增 `duel.charge`/`duel.pending`/`duel.matched`/`duel.won`/`duel.lost`/`duel.draw`/`duel.forfeited`/`duel.staged.settled` 事件码；配表新增 `duel.json`。
  验证：Domain 89/89、Server 35/35（含 PostgreSQL 集成库）、前端 build + 契约检查、DataTool --strict 通过；附带修复并发任务遗留编译错误（MarketRuntime Volatile/+=、ProtocolSnapshotMapper 条件表达式等）。

### TASK-20260916-02 审计不一致点全量整改
- 分类：未标注
- 时间：提出 2026-09-16 21:45:04｜完成 无（原文无状态行；进度日志记录 2026-09-17 收尾、全量门禁通过）
- 状态：✅ 已完成（据进度日志，2026-09-17 收尾）
- 摘要：对 TASK-20260916-01 审计输出的全部不一致项按用户裁定「文档超前项全部按文档实现」整改，六分域并行：战斗体系（多敌人同场、威胁/嘲讽、怪物技能槽与阈值技、K=Tier、CDR/ATR/暴伤/技能强度成长乘区、Buff 第四乘区、DamageAbsorption 消费、PercentValue 双模式、波次 1+2 结构、manaCost 点数化、支援技能可装配 10 门全可用）。
  玩家成长（境界经验表 realmProgression 入配表、突破瓶颈+心魔+寿元 lifespanYearsByTier、技能经验/等级曲线、秘籍复读 +10000/残卷 +500、48h 离线封顶、CanStartAction 体征/空间/材料门槛、自动槽 3×4 限界）；世界生态（星陨异境跨图边可达、节点 encounterIds 确定性接入实战遭遇、NearbyPanel 生态+NPC 投影、DataTool 世界图连通性门禁）。
  服务端（ComputeNode 冷挂机派发链 compute_jobs 生产/领取/CAS 回写/冷扫描 worker、outbox→audit_events 审计归档、改名查重+24h 冷却+唯一索引、市场 expiresAt/成交指标真实化）；斗法（实时镜像斗法、战报面板、±30% 匹配、1 小时冷却）；LoadTests websocket/http-read/compute/postgres 场景齐备；运维与文档（schema v3 口径统一、ECS 过期描述重写为聚合架构、索引与死配置清理）。
  验证：Release 构建 0 警告、领域 89/89、服务端 49/49（含市场/冷挂机/学习持久化集成测试）、DataTool --strict、capacity-target、前端构建、真实 Server 的 websocket/http-read 冒烟全绿；按领域拆分提交。

### TASK-20260916-01 全量文档与实现不一致点再审计
- 分类：未标注
- 时间：提出 2026-09-16 17:51:55｜完成 无（原文无状态行，进度仅记录「登记，启动并行审计」）
- 状态：🚀 进行中（归档时停滞；原文无完成记录，审计输出由 TASK-20260916-02 承接整改）
- 摘要：只读交叉审计（不修改实现代码）：七路并行只读 Subagent 分域核对——①AGENTS.md/README/migrations 规约 vs 仓库实况；②docs/backend/* vs Domain/Server/Protocol/PostgreSQL schema；③docs/frontend/* + i18n 闭环 + 协议信封前后端对齐；④design 战斗数值类 vs 领域战斗实现与配表；⑤design 架构玩法类 vs 实现；⑥docs/ops/deployment.md vs Dockerfile/compose/deploy/scripts；⑦docs/world、临时设计讨论与既有审计报告（docs/audits/20260915）条目有效性复核。
  产出为统一不一致清单，标注文档出处、实现证据与严重度。

### TASK-20260915-06 通用行动收益模型（yields）落地
- 分类：未标注（来源：审计报告 `docs/audits/doc-vs-implementation-20260915.md` NEW-B-12（P1））
- 时间：提出 2026-09-15 16:35:00｜完成 无（原文状态行 🚀 进行中；进度日志 17:05 记录最终门禁通过、任务完成待提交）
- 状态：🚀 进行中（归档时停滞；进度日志记录已完成待提交）
- 摘要：行动收益改为每行动声明 `yields` 列表：`kind` 六类（qi/mana/hitpoints/experience/item/buff）× `per` 两态（second 持续速率/repeat 单轮结算）；item/buff 仅允许 repeat，资源类禁止携带 id/name/durationSeconds/modifiers；旧字段 `qiPerSecond`/`experiencePerRepeat`/`outputs` 一次性废弃迁移（46 条行动全部转换，引气入体补 mana 1/轮）。
  `GameDataCatalog` 新增 `ActionYield`/`ActionYieldKind`/`ActionYieldPeriod`/`BuffGrantSpec`；`SimulationEngine.ApplyContinuousReward`/`CompleteRepeat` 按秒/按轮统一结算（灵力/生命 clamp 到 `PlayerAttributeResolver` 上限、Buff 授权同 Id 刷新到期并叠加层数、`autoUseMultiplier` 不乘 Buff）；`/api/v1/catalog/actions` 改发 `yields`；前端 types/ids（47 条内置行动迁移）/App/CombatArena/WorldMapPanel 产出渲染/i18n 同步。
  验证：Release 全量 0 警告、Domain.Tests 76/76（新增 4 个收益不变量测试：灵力入账不串位+位级一致、混合收益分通道、非法声明拒绝、JSON 往返+未知类型拒绝）、Server.Tests 33 过 2 败（既有环境缺失）、前端 build 通过；文档同步 `总纲.md`/`持续消耗品系统.md`/`game-data.md`，审计报告 B-11/NEW-B-12/X-01 回填整改状态。

### TASK-20260915-05 文档与实现一致性深度复核（整改后复审）
- 分类：文档治理 / 交叉审计 / 多Agent并行 / 只读分析
- 时间：提出 2026-09-15 01:47:04｜完成 2026-09-15 03:30（审计报告落盘，任务闭环）
- 状态：✅ 已完成
- 摘要：六路并行只读审计（A 战斗数值/B 行动生产/C 经济坊市/D 协议前端/E 持久化运维/F 世界配表）+ 主审逐行复核 P0/P1：复审 69 项历史不符点在 HEAD `82f1665` 的处置状态（38 已闭环/12 部分闭环/14 仍存在/3 状态变化/2 维持误报，落地率 55%+17%）；扫描整改大改动（+2183/−305，55 文件）引入的新增偏离 45 条（3 P0/15 P1/26 P2）+1 条记录，归并 6 大跨域同源根因（schema v1 残留、ComputeNode 未接线、audit_events 死管道、角色拆分半落地、快照 v2 伪造字段、生态展示断链）。
  三大 P0：技能耗蓝 ×10 与 Lv.1 MP=50 脱节（无法施法）、引气入体修为/灵力未分流、波次数量与品阶倍率未对齐基准。
  审计报告：[docs/audits/doc-vs-implementation-20260915.md](../../archive/audits/doc-vs-implementation-20260915.md)。原登记编号 -03 被并行「YuoCore 潜在风险逐项修复」任务覆盖详情文件，故改编号 -05 重新建档。

### TASK-20260915-04 全量 C# 源码添加中等详细度中文 XML 注释（多 Agent 并行）
- 分类：未标注
- 时间：提出 2026-09-15 03:03:51｜完成 2026-09-15 03:09:18
- 状态：✅ 已完成
- 摘要：6 路并行 subagent（Domain 约 19 文件、Protocol+DataTool 约 9、Server 约 28、Compute+Load 约 16、Domain.Tests 约 17、Server.Tests 约 12）为 `src/` 与 `tests/` 全部 `.cs` 文件补中文 XML 文档注释（`///`，中等详细度），不侵入 `vendor/YuoCore` submodule；仅补注释不改业务逻辑、签名、行为或数据。
  实际结果：共改动 86 个 `.cs` 文件，新增约 2461 行中文 XML 注释，删除 6 行（多为格式化导致的单行枚举展开）。
  验证：`dotnet build` 全解决方案 0 Warning/0 Error；`git diff --stat` 确认改动仅涉及注释；未触碰前端源码、配置数据或业务逻辑，用户既有未提交改动保持原状。

### TASK-20260915-01 全面并行项目分析
- 分类：项目分析 / 多 Agent 并行只读审计
- 时间：提出 2026-09-15 01:04:36｜完成 2026-09-15（报告已落盘；Git 提交随工作区统一裁定）
- 状态：✅ 已完成（2026-09-15，报告已落盘）
- 摘要：五路并行只读分析（A Domain+Protocol+config/game-data、B Server+ComputeNode+DataTool+LoadTests、C frontend、D tests+scripts+deploy+Dockerfile+compose、E docs 与未提交改动），背景为工作区 109 个未提交文件（73 个已跟踪 +1335/−422，主体为 TASK-20260914-09 整改进行中改动），分析全程只读。
  产出汇总报告：`docs/archive/analysis/project-analysis-20260915.md`。
  结论：代码正确性风险经三轮审计明显收敛；当前最大风险是工程卫生（109 项混合未提交改动、schema/快照 v2 双断点）与未接线链路（ComputeNode 假补算、outbox/audit 死管道、YuoCore 悬空引用）；六项高风险（H1-H6）与分路清单详见报告 §2/§3，建议行动顺序见 §5。


### TASK-20260914-09 审计问题完整整改
- 分类：原文未标注
- 时间：提出 2026-09-14 21:05:16｜完成 无（进行中）
- 状态：🚀 进行中（归档时停滞）
- 摘要：按 69 项复核结论与推荐方案实施完整整改（撤销误报、修复真实缺陷、补齐设计闭环、更新过期文档），基线 HEAD `7f315fd31563738e603c4acbe2609ab8d3f231ae`。
  2026-09-15 00:31 续作（接手中断会话）：严格 Release 构建 0 警告 0 错误；修复空闲标脏——`SimulationEngine` 回复余量（`RegenCarrySeconds`）与无冷却空闲技能推进不再每步 `MarkDirty`，仅回复实际改变生命/灵力才 bump `StateVersion`（空队列满状态玩家不再每 5 秒收到无意义增量）；协议对齐——玩家快照 head/section `SchemaVersion` 1→2（`PlayerStateHead`、`PlayerSectionSnapshot`、`SectionCodec`），与前端 `snapshot.ts` v2 期望对齐（战斗分段新增回复余量、技能状态、已学功法，旧 v1 本地存档按清零规则拒绝）；波次接入——`EncounterWaveSelector` 正式接入 `SimulationEngine.AdvanceCombatAction` 替换单只加权选取：按预算+种子确定性生成整波（1~5 只）、同波击杀后无缝续战（`combat.wave.next`）、每 5 波精英、每 `BattlesPerBoss` 波 Boss，删除 `TrySelectMonster`，新增回归 `CombatWaveFightsMonstersBackToBackAndCountsOneRepeatPerWave`，`QueueLoopTests` 妖皇巢穴配表预算修正为 100。
  门禁：Domain 83/83、Server 39/39（含市场与功法学习真 PostgreSQL 集成）、`npm run build` 通过（webp 90/90 无内联）。
  遗留：文档逐项闭环 69 项处置、浏览器/容器/容量验收、diff 审查与提交未做。

### TASK-20260914-08 自然回复配表语义澄清
- 分类：原文未标注
- 时间：提出 2026-09-14 20:24:22｜完成 2026-09-14 20:27:33（文档同步完成）
- 状态：文档调整完成，纯文档待提交（不修改配置或代码，不自动提交）
- 摘要：用户澄清：回复基础值通过基础属性配置文件调整，「2%」指对应境界基础生命上限的配表参考值，不是运行时代码公式。
  同步战斗规约、总纲及 HTML 副本、基础属性模型、初版设计、角色/ECS 及后端流程说明，审计报告加入「用户澄清优先」说明，标注原审计建议已被取代。
  未定事项：回复结算周期仍待讨论；不将生命 2% 的配表参考扩展为灵力固定比例。总纲一致性检查与 `git diff --check` 通过，仅文档不运行功能构建或测试。

### TASK-20260914-07 审计报告逐项复核
- 分类：原文未标注
- 时间：提出 2026-09-14 20:05:51｜完成 2026-09-14 20:13:12
- 状态：复核已完成，纯文档待提交（依纯文档禁自动提交规则保留工作区）
- 摘要：复查 `docs/audits/doc-vs-implementation-20260914.md` 的 69 项 A-F 结论、X 类补充及报告的优先级、归因和验证边界（不执行整改建议）。
  复核结果：40 项成立、26 项部分成立、3 项误报（readyz 注册链、归档分区、前端攻击间隔为误报）；编号完整性和 `git diff --check` 通过，未运行功能测试或生产验证。
  交付 [独立复核报告](../../archive/audits/doc-vs-implementation-20260914-recheck.md)。

### TASK-20260914-06 文档与实际实现全面一致性审计
- 分类：文档治理 / 交叉审计 / 设计与实现一致性 / 需求追溯
- 时间：提出 2026-09-14 19:24:09｜完成 2026-09-14 19:45:00
- 状态：✅ 已完成（只读审计，未修改任何实现代码）
- 摘要：全面分析文档与实际实现不符之处；审计范围覆盖 `docs/design/`、`docs/backend/`、`docs/frontend/`、`docs/ops/`、`docs/world/` 及根 `README.md`/`AGENTS.md`/`WATCHDOG.yml`，排除 `docs/tasks/TASK-*.md`、看板、临时设计讨论与历史分析快照。
  前提：本地开发阶段历史兼容性不在要求范围内，「实现未保留旧格式」不计为不符。
  A-F 六域（战斗与数值/行动队列与生产/经济与坊市/协议与前端/持久化与运维/世界与配表）并行只读核对，高影响条目抽样复核文件行号与行为，产出审计报告 `docs/audit-doc-vs-implementation-20260914.md`。

### TASK-20260914-05 角色行动条倒计时与右侧行动卡片显示不一致修复
- 分类：Bug修复 / 前端时钟校准 / 协议增量载荷 / 行动进度显示
- 时间：提出 2026-09-14 18:57:00｜完成 2026-09-14 19:45:00
- 状态：✅ 已完成
- 摘要：根因：`PlayerSnapshotDeltaDto` 不携带 `LastSimulatedAt`，增量帧无权威时刻，前端沿用上一次全量快照的旧锚点，时钟偏差以 -1 秒/秒线性累积（页面停留 45.7 秒后本轮真实剩余 4.3 秒被显示成 50.0 秒，行动条填充每秒被误判进入新周期而重置）。
  修复：协议层 `PlayerSnapshotDeltaDto` 新增 `LastSimulatedAt`（`src/Protocol/Contracts.cs`），`ProtocolSnapshotMapper.ToDelta` 与 `WebSocketRuntime.BuildDelta` 随增量帧下发，并清理仅被测试引用的兼容旧签名重载；前端 `protocol.ts`/`snapshot.ts` 按 `lastSimulatedAt` 重建时钟锚点（字段缺失时沿用旧锚点不中断解码）；`ActionCountdownBadge` 补 `Math.min(duration, …)` 上界；`CombatArena` 改用队列专属锚点 `lastQueueSyncPerformanceTime ?? lastSyncPerformanceTime`。
  验证：Release 构建 0 警告 0 错误、Server.Tests 35/35（新增增量帧 `lastSimulatedAt` 编解码往返断言）、Domain.Tests 73/73、前端生产构建通过；生效需重启服务端进程并刷新页面。

### TASK-20260914-04 坊市分类Tab、检索条件与列表滚动位置跨界面状态持久化与恢复
- 分类：前端交互体验 / 状态管理 / 视图持久化 / 滚动位置记忆 (Scroll Restoration)
- 时间：提出 2026-09-14 17:18:45｜完成 2026-09-14 17:21:20
- 状态：✅ 已完成
- 摘要：解决坊市 `MarketPage` 切详情/切主面板后一级 Tab、二级子 Tab、搜索词、滚动位置全部重置的问题（`MarketBrowseGrid` 局部 `useState` 随条件渲染卸载、`App.tsx` 条件渲染整页销毁）。
  方案：基于 Zustand 新建 `frontend/src/game/marketStore.ts`（`useMarketStore`：primaryTab/subCategory/query/scrollTop/activeTab/selectedItemId，状态内存常驻）；零重绘滚动恢复——组件不订阅高频 `scrollTop`，`onScroll` 直接 `useMarketStore.getState().setScrollTop(...)` 写入（滚动过程 0 次 React 重渲染），挂载时 `useLayoutEffect` 瞬时对齐，配合 `content-visibility: auto` 与 `contain-intrinsic-size`；主动切换分类或搜索时显式重置滚动至顶部；返回列表时清除 `selectedItemId` 其余状态完整保留。
  验证：`npm --prefix frontend run build` 与 `scripts/verify.sh` 通过。

### TASK-20260914-03 坊市标的完整列表滚动卡顿性能重构与视口渲染优化
- 分类：前端性能调优 / 渲染管线重构 / 滚动帧率优化 / 悬浮气泡系统 / 现代CSS引擎
- 时间：提出 2026-09-14 17:04:21｜完成 2026-09-14 17:12:30
- 状态：✅ 已完成
- 摘要：坊市「全部」Tab 80+ 标的列表滚动掉帧至约 20 FPS。四层组合优化：
  ① `FloatingPopover`/`ItemTooltipTarget` 懒求值——未展开时不再执行 `describeItemEffects` 等解析与 JSX 构建（原每格 20+ 节点、全表约 2000 个未展示节点），未展开直接返回 `null`；
  ② 滚动抑制——滚动时容器加 `is-scrolling` 类并 `pointer-events: none`（停止 100ms 后解除），杜绝滚动期 `mouseenter` 风暴与强制同步回流，`openDelay` 60ms→150ms；
  ③ `.market-browse-tooltip` 启用 `content-visibility: auto` + `contain-intrinsic-size`，滚动容器追加 `contain: paint`，浏览器原生跳过视口外约 70% 节点的布局与光栅化；
  ④ `inventory` 预转 `Map<string, number>` O(1) 索引 + 单卡片 `memo(MarketBrowseCardItem)`（按 itemId/selected/amount 比对）。
  验证：前端构建零错误零警告、`scripts/verify.sh` 全量通过；80+ 件标的滚动 FPS 锁定满帧，悬停气泡懒计算内容完整。

### TASK-20260914-02 坊市界面交互优化（移除状态行、网格行高拉伸修复、材料/装备多级Tab重构、功法书技能悬浮详情）
- 分类：前端重构 / 坊市系统 / UI交互规范 / 悬浮气泡体系
- 时间：提出 2026-09-14 07:32:36｜完成 2026-09-14 07:59:45
- 状态：✅ 已完成
- 摘要：① 移除坊市状态行（「共 51 种可交易物品」「灵石 0」），错误/同步中状态内联至 Header 右侧操作区；
  ② 修复网格纵向拉伸：`.market-browse-grid` 增加 `align-content: start`、`align-items: start`、`grid-auto-rows: var(--item-slot-size)`（每行 64px、间距 6px）；
  ③ 分类重构：一级「全部/材料/装备/丹药/功法」+ 二级子 Tab（材料：矿石/药材/通用材料；装备：武器/防具/饰品；功法：秘籍全本/招式残卷），从全局 store 接入 `state.equipment` 映射为 `MarketInstrument` 交易标的；
  ④ `ItemDisplay.tsx` 新增 `resolveManualAsideCard` 功法秘籍副气泡（匹配 `item.tome.*`/`item.fragment.*`，检索 `CombatAbilityDefinition`，`describeAbility(ability, 1)` 构造卡片）；
  ⑤ i18n：`market.zh.ts` 补齐词条并同步 `template.csv`（共 1331 条中文词条），更新 `docs/frontend/common-components.md`。
  验证：`scripts/verify.sh` 全栈门禁通过。

### TASK-20260914-01 装备属性悬浮面板色彩对齐与攻击间隔移除
- 分类：原文未标注
- 时间：提出 2026-09-14 03:32:33｜完成 2026-09-14 03:38:20
- 状态：✅ 已完成
- 摘要：移除装备「攻击间隔」：服务端 `StaticGameCatalogMapper.cs` 与配表 `weapons.json` 删除 `attackIntervalMilliseconds`（原后端默认兜底 2400 导致所有装备含防具饰品错误显示「攻击间隔 2.40 秒」；修仙战斗规则角色攻击间隔基础 2 秒、装备不带攻速词条），前端 `ids.ts`/`ItemDisplay.tsx` 移除展示。
  色彩对齐：`ItemDisplay.tsx` 建立 `STAT_TONE_MAP`，属性加成条目严格对应内视面板 `PillTone` 修仙色系（物理攻击 crimson、术法攻击 indigo、生命系 emerald、灵力系 cyan、护甲 bronze、术法抗性 violet、命中 sky、闪避/施法攻速 teal、暴击 gold、免暴/韧性 slate、破招 amber、化解 silver、五行金木水火土各对应色），复用公共组件 `PillBonusLabel`。
  验证：dotnet test 68 领域 + 31 服务端全部通过、前端生产构建与 `scripts/verify.sh` 全量通过；更新 `docs/frontend/common-components.md`。

### TASK-20260913-07 图标编码改为「降色 256 + 无损 WebP」：体积 -37%，画质反而更好
- 分类：美术资产 / 图像编码 / 切图管线 / 前端体积
- 时间：提出 2026-09-13 19:45:05｜完成 2026-09-13 20:05:00
- 状态：🧪 联调/验证中（已换入并通过构建与目视核对，尚未提交 Git）
- 摘要：90 张 64×64 图标从 WebP q90（267.2 KB）改为「降色 256 + 无损 WebP」：168.1 KB（-37.1%），可见像素 RGB 中位 35.15 dB（原 31.02 dB，反升 4.13 dB）。
  横扫编码档位（以无损 PNG 为基准、PSNR 只在 alpha>8 可见像素上算）三结论：「降色+无损」全局最优同时打赢体积和画质；「降色+有损」是最差组合（两次独立损失，181.4 KB 段 RGB 仅 27.58 dB）；保留原 alpha 不值得（300.9 KB，多 79%）。
  查明现状真正毛病：有损 WebP 往全透明像素填任意 RGB，导致边缘半透明像素偏色、轮廓发白、整图褪色发灰；降色+无损无此问题。
  改动：切图工具新增 `--web-palette N`（FASTOCTREE 量化，不设默认，与 `--web-lossless` 组合）；90 张全部重出（图集来源 64 张走 `art/sheets.json`，遗留 26 张从 `art/legacy_128/` 补正方形 LANCZOS 缩 64），扩展名仍 `.webp`，注册表/`vite.config.ts`/组件零改动；旧版备份 `/tmp/items_web_q90_backup/`。
  验证：文件名逐一相同、构建通过（536ms）、入口 JS 367.60 kB 不变、`data:image` 0 次、10 倍逐像素目视新档更接近无损原图。

### TASK-20260913-06 图标资产收敛为「原图 + 64」单档，切图工具默认不再产出母版
- 分类：美术资产 / 切图管线 / 工程目录规范 / 构建上下文
- 时间：提出 2026-09-13 18:49:10｜完成 2026-09-13 19:02:00
- 状态：🧪 联调/验证中（清理与工具改动已完成并验证，尚未提交 Git）
- 摘要：运行时只保留 64×64 一档，非运行时只留原始分辨率。经用户裁定只删 256 母版（与 TASK-20260913-02「工程保留 256png」旧约定冲突，属不可逆决策）。
  清理：删除 `art/master_256`（64 张 4.3MB，可由 `art/sheets.json` 逐字节重建）、`art/_build` 与 /tmp 下 32px/64px 备份及对比页（32px 回退路径刻意消失）；`art/` 只剩 `source/`（原图 8.6MB）、`legacy_128/`（26 张全网唯一高清副本 208KB）、`sheets.json`、`icon_ids.json`。
  工具：`slice_grid_sheet.py` 的 `--master-size` 默认值 256→0（不出母版，需要时显式传参），新增 `--master-size 0` 与 `--web-size 0` 同时成立的报错校验，预览总览跟随实际产出层级。
  不改任何 CSS/JS、不改运行时尺寸（90 张 64×64 WebP q90 不变）。
  验证：frontend/src 全量图片 90 个全部 64×64 RGBA 无残留、构建通过、`git submodule status` 3a7b1e3 无漂移、工具默认行为与参数校验实测通过。

### TASK-20260913-05 运行时图标尺寸定为 64×64（含与最近邻渲染的兼容性实测）
- 分类：美术资产 / 图标尺寸 / 前端渲染
- 时间：提出 2026-09-13 18:24:44｜完成 2026-09-13 18:30:00
- 状态：🧪 联调/验证中（工作区改动已完成并通过构建与真实浏览器验证，尚未提交 Git）
- 摘要：运行时图标 32px→64×64 WebP q90（合计 267.2 KB，32px 时 110.5 KB）；新一批 64 张用固化规格 `slice_grid_sheet.py art/sheets.json --pad 8 --master-size 256 --web-size 64` 重出，遗留 26 张从 `art/legacy_128/` 补正方形 LANCZOS 缩 64；未改任何 CSS/JS。
  关键实测：绘制区固定 52×52 CSS px，DPR1 缩小 0.8125×、DPR2 放大 1.625×（同源反向缩放），四象限对照（最近邻/双线性 × DPR1/DPR2）结论 64×64 与 `image-rendering: pixelated` 兼容，无需按 DPR 加 media query、不回退 -04 的改动（DPR1 未走样因 0.8125× 只跳过约 19% 像素且图标以大色块为主）。
  验证：构建通过（546ms）、`dist/assets/*.webp` 90 张全部 64×64、入口 JS 367.60 kB（gzip 106.67 kB）与 32px 完全一致（图标全外链）；更新 `docs/frontend/common-components.md` §6；128px 情形的 pixelated 评估仍未做。

### TASK-20260913-04 图标渲染关闭线性插值（改最近邻，恢复像素质感）
- 分类：前端样式 / 图像渲染 / 视觉体验
- 时间：提出 2026-09-13 18:07:50｜完成 2026-09-13 18:10:00
- 状态：🧪 联调/验证中（工作区改动已完成并通过构建与真实浏览器验证，尚未提交 Git）
- 摘要：三处图标 `<img>` 加 `image-rendering: pixelated`（未新增类名、未改 DOM/JS）：`.item-tile-icon`（`styles/selection-grid.css`，32px→52×52 放大 1.625×，主要目标）、`.item-reference-icon`（`styles.css`，32→16 整数缩小 2×）、`.daohua-floating-card-title-icon`（`styles/floating-popover.css`，32→24 缩小 1.33×）；已 grep 确认三处各只定义一处、不受 CSS 导入顺序影响。
  真实浏览器 A/B 对照（本机 Edge + 构建产物 CSS + 真实组件 DOM，DPR2，36 张零破图）：瓦片 3× 放大下 A 组硬边像素块明显优于 B 组双线性（即用户所说「糊」），行内 16px、浮窗 24px 缩小场景无走样破碎，三处全部保留。
  风险提示：源图升回 ≥52px 绘制区（如 128px）会转为缩小场景需重新评估（128px 评估仍未做，已在 CSS 注释、公共组件文档与本文档三处提示）；回退只需删除三处声明。

### TASK-20260913-03 64 张图标运行时资源接入前端（WebP 落盘 + 注册表扩展 + 战技图标渲染）
- 分类：原文未标注
- 时间：提出 2026-09-13 00:55:41｜完成 2026-09-13 01:16:00
- 状态：✅ 已完成
- 摘要：按名称（非顺序）把切图产物匹配到配表 ID，64/64 全部唯一命中（equipment. 28、item. 25、ability. 10、currency. 1），落盘 `frontend/src/assets/items/<资源ID>.webp`；全量配表扫描出现 34 处配方/实体同名歧义（如 `forge.qingfeng-sword` vs `equipment.qingfeng-sword`），判定图标只归属实体、排除 `actions/` 配方。
  `itemIcons.ts` 扩展为 png+webp 双 glob、`registerIconModules()` 统一去后缀、同名 `.webp` 优先；战技面板是唯一不走 `itemId` 的例外，显式接线 4 处（`AbilitiesPanel.tsx` 装配槽与已学功法列表、`AbilityGridPicker.tsx`、`App.tsx` 战斗面板战技槽位）。
  构建修正：58 张图标因小于 Vite 4KB 内联阈值被 base64 塞进入口 JS，`vite.config.ts` 给 `assetsInlineLimit` 传函数对 `assets/items/` 禁止内联——入口 JS 572.21→367.55 kB、gzip 260.81→106.65 kB（减约 154 kB gzip）。
  固化 `art/sheets.json`（切图规格）与 `art/icon_ids.json`（名称→ID 映射 64 条），母版可逐字节复现（cmp 64 相同）。
  验证：构建退出码 0、`data:image` 0 次、真实浏览器像素核对（离线核对页 + Edge）全部断言通过。
  注：第六节 32px 定稿结论已被 TASK-20260913-05/-06 覆盖（全项目只保留 64 一档，`art/master_256/` 已删除）。

### TASK-20260913-02 美术原图与工程母版归置到项目根目录 art/
- 分类：原文未标注
- 时间：提出 2026-09-13 00:45:19｜完成 2026-09-13 00:53:00
- 状态：✅ 已完成
- 摘要：根目录新建 `art/` 分两层：`source/`（3 张当前使用 AI 原图 1374×1145/1932×884/1764×908 + `旧版/` 3 张早一版素材表，与 `~/Downloads` 原件逐一 cmp 逐字节一致）、`master_256/`（64 张 256×256 全彩 RGBA 无损 PNG 母版，26/28/10 张）。
  ⚠️ 后续变更：`master_256/` 已于 2026-09-13 19:02 按 TASK-20260913-06 删除，并新增 `sheets.json`/`icon_ids.json`/`legacy_128/`，现行结构以 TASK-20260913-06 为准。
  配套：`.dockerignore` 新增 `art`（减小发送给 daemon 的构建上下文约 13MB）、`.gitattributes` 新增 `*.png binary`/`*.webp binary`（杜绝行尾转换损坏）；`art/` 纳入 Git（美术源属代码资产，不适用 `data/` 排除规则）。
  遗留：TASK-20260911-02 那批物品图标的原图已不在本机无法归档，用户补回后放入 `art/source/` 即可。

### TASK-20260913-01 三张图标表网格裁切（资源 / 装备 / 战技）
- 分类：原文未标注
- 时间：提出 2026-09-13 00:20:10｜完成 2026-09-13 00:30:00（追加需求「两级资产」00:38:00）
- 状态：✅ 已完成
- 摘要：三张 AI 图标表按整齐网格等分裁切（不逐图标抠图）：图1 1374×1145 6×5 取前 26 格、图2 1659×948 7×4 全 28 格、图3 1983×793 5×2 全 10 格，导出 1:1 正方形 PNG 保留透明留白。
  关键问题：源图排布间距不均匀（图1 行中心间距 227.5/224.5/236/201，第 4/5 行几乎无空隙），朴素等分切到图标 6~13px；最终方案为内容间隙定位（alpha>32 掩膜行列投影取图标带、粘连带在中间低谷再切）+ 空隙中点分界线 + 方格边长统一（244/242/396 = 最大边长 + 2×8px）+ 越界 alpha 置 0 屏蔽 + 透明补齐。
  两级资产管线：工程母版 256 PNG 无损（4.3MB）+ 网页版 64 WebP q90（178KB，单张 2.8KB），同一全分辨率方格一次性 LANCZOS 重采样；画质口径：整图 PSNR 会因透明像素被填任意 RGB 失真（荒谬的 23dB），正确分通道量——alpha 57~62dB、可见像素 RGB 中位 30.8dB。
  验证：64 张最小留白边距 7px 无一切到、母版 alpha 极值恰为 0/255、两级文件名逐一配对；脚本固化为 `~/.workbuddy/skills/icon-sheet-grid-slice/scripts/slice_grid_sheet.py`。
  归档位置：原图 `art/source/`、母版 `art/master_256/`（见 TASK-20260913-02）；网页版 64 WebP 当时未入库，后续由 TASK-20260913-03 落实。

### TASK-20260911-02 26 种物品图标资产切图与前端图标化渲染
- 分类：原文未标注
- 时间：提出 2026-09-11 01:14:43｜完成 2026-09-11 01:24:00
- 状态：✅ 已完成
- 摘要：26 格物品图标大图（6 消耗品对应 `config/game-data/items/pills.json` + 20 秘籍/残卷对应 `manuals.json`）切图：白底识别（min(R,G,B)>=228）+ 外边界 BFS 洪水填充（只挖与画布连通的白底，不误挖奶白书页）、行/列投影切分（阈值 60/20，防止残卷碎片粘连误并）、羽化消除 JPEG 白描边、长边 128px 量化 256 色 PNG 26 张合计 167KB。
  前端：新增 `frontend/src/game/itemIcons.ts`，`import.meta.glob('../assets/items/*.png', { eager: true, query: '?url' })` 构建期登记，导出 `resolveItemIconUrl`/`hasItemIcon`——「有图加载图、无图回退文本」由文件存在性决定，适用于全部物品展示位。
  接入：`ItemTile`（新增 itemId 属性，命中时 `<img class="item-tile-icon">` 整块替换文本）、`ItemIcon`（新增行内组件）、`ItemReference`、`FloatingInfoCard` titleIcon、`MarketOwnOrders`；样式 `.item-tile-icon` 绝对定位铺满瓦片（避免与丝带/角标争抢网格行高）等三处。
  验证：构建通过、glob 映射含全部 26 键、真实浏览器像素核对通过（改用离线核对页，避免抢占用户唯一 WebSocket 连接被 1008 拒绝）。

### TASK-20260911-01 持续消耗品倒计时遮罩与药效激活失效修复
- 分类：原文未标注
- 时间：提出 2026-09-11 00:03:19｜完成 2026-09-11 00:12:00（后续按用户反馈迭代至 01:37:00）
- 状态：✅ 已完成
- 摘要：两个独立缺陷叠加造成「改了什么都没反应」：
  ① 前端倒计时遮罩同时挂 `combat-ability-cooldown` 与 `item-slot-duration-overlay`，后者被后加载的 `expedition.css` 级联覆盖（仅 box-shadow 幸存），改为遮罩类独占；扇形约定按反馈校正为起点 12 点方向、边界顺时针扫过、暗区代表剩余时长（`conic-gradient(from 0turn, transparent calc((1 - var(--cooldown-ratio)) * 1turn), rgba(8,15,17,0.86) 0)`）；外圈淡金虚线弧经多轮反馈改为沿扇形范围流动——`repeating-conic-gradient`（10deg 实 + 8deg 虚）+ 注册为 `<angle>` 的 `--ring-phase`，两层嵌套遮罩取交集保证虚线绝不越出扇形（不用 `mask-composite: intersect`，防不支持时退化成并集），流速最终 12s/圈（实测 30.1deg/s，由初版 3s 两轮放慢各一倍）；蒙层浓度按反馈 0.86→0.5；顺带修复 `AbilitiesPanel` 技能冷却蒙层从未透传 `--cooldown-ratio`（缺省 1 导致整块瓦片全暗）并删除无消费者的 `--duration-sweep`/`--cooling`。
  ② 领域缺陷：`SimulationEngine` 周期起点判据 `ProgressSeconds <= eps && SimulationCarrySeconds <= eps` 中 `SimulationCarrySeconds` 全仓库无重置点，离线补算后变非零（实测 0.6965839999875751）导致持续消耗品（引气丹/通脉丹/寻灵香/地火引）永久无法在动作开始扣件激活；改为只依赖 `ProgressSeconds`，保留「本轮已结算整秒收益后中途补货不得追溯激活」语义；回归用例改写为 `SustainedConsumableDoesNotActivateAfterCycleHasAwardedSeconds`（与相位无关的等价判据）。
  证据链：`game.player_state_sections` 的 `progression.simulationCarrySeconds`、`action_queue`（br-json）队头 `cultivation.draw-qi`、库存 `item.qi-gathering-pill = 86`、CSS 产物字节偏移定位；临时探针实测 carry=0 扣件、carry 非零不扣。
  验证：Domain.Tests 61/61、Server.Tests 29/29、前端构建多次通过。

### TASK-20260910-02 持续消耗品扇形倒计时与低阶物品可读性修复
- 分类：原文未标注
- 时间：提出 2026-09-10 22:24:57｜完成 2026-09-10 22:47:38
- 状态：✅ 已完成
- 摘要：持续消耗品在动作开始时扣一件并写入槽位剩余持续时间、动作完成按实际耗时扣减、切换/卸下清空剩余时间；前端以剩余时间驱动扇形旋转填充，低阶物品采用高对比描边、数字和进度光晕。
  核查确认领域层已有开始扣件、完成扣时、切换清零语义（「核查领域扣除、切换和中断边界」子任务未勾选），主要缺口在前端计时覆盖层与低阶颜色对比度；完成扇形覆盖层、倒计时高亮与凡品银白色阶调整。
  验证：前端构建与领域测试通过，补充空闲保留、恢复后扣时回归后领域测试 60/60；默认端口被占用时改用 15173 启动 Vite；Playwright 模块导入失败，未宣称截图验证通过。

### TASK-20260910-01 开发进度与潜在问题深度分析
- 分类：原文未标注
- 时间：提出 2026-09-10 18:35:11｜完成 2026-09-10 18:40:00
- 状态：✅ 已完成
- 摘要：在提交 `9303050` 基线上交叉核查开发进度、玩法完成度与潜在问题，对照 2026-08-28 首次审计报告验证修复情况；五路并行核查（服务端运行时/领域与协议/前端/基础设施与文档/玩法完成度）+ 真实门禁实测（Release 构建 0/0、Domain 59/59、Server 29/29），关键结论回到源码逐行复核并修正并行核查误判（WS 鉴权、远征 phase 解码）。
  关键结论：高风险——远征战败 `Blocked` 静默冻结队列（最新提交引入）、`ComputeNode` 未接线、市场 `/book` 未鉴权、Caddy 安全面、发布/备份链路风险；玩法断层——Buff 空壳，炼丹/炼器/旅行/装备品质专属逻辑缺失（配置齐全但代码未实现）；上次审计多项高危已真实修复，结构性与依赖方向规约整体合规。
  交付物：[docs/archive/analysis/project-analysis-20260910.md](../../archive/analysis/project-analysis-20260910.md)。

### TASK-20260905-02 妖兽试炼行动按地图命名与历练列表过滤优化
- 分类：原文未标注
- 时间：提出 2026-09-05 19:52:23｜完成 2026-09-05 20:10:04
- 状态：✅ 已完成
- 摘要：历练页面只展示可执行的历练/战斗行动：`world.travel` 已配置 `hiddenFromCatalog: true` 但前端 `ExplorationPage` 仅按类型筛选，改为服务端目录字段 + 前端 `executionKind` 双重规则排除出历练列表。
  妖兽试炼行动按地图/节点差异化命名：青云仙山节点共用 `combat.hunt-qi-beast`、星陨异境共用 `combat.sweep-twilight-valley`，统一「讨伐」前缀（「讨伐 青云仙山」「讨伐 星陨异境」）并补国际化词条；保持行动 ID、遭遇绑定和队列执行语义稳定。
  验证：领域测试 47/47、服务端测试 25/25、前端生产构建与 JSON 配置校验全部通过；功能代码已提交 `397d7d4 fix: 优化妖兽试炼行动展示与历练列表`。

### TASK-20260905-01 全项目审计问题收敛修复
- 分类：原文未标注
- 时间：提出 2026-09-05 18:17:41｜完成 2026-09-05 18:23:00
- 状态：✅ 已完成
- 摘要：依据当前代码重新核实并修复领域、协议、服务端、前端、测试与基础设施中仍然成立的高风险问题，三路并行：领域/协议/服务端（行动重复次数、确定性、快照 delta、PlayerRuntime、MarketRuntime、WebSocket）、前端（协议客户端恢复、Hooks/状态生命周期、日志接线、i18n）、基础设施/测试（备份校验、部署脚本、Docker/Compose、验证脚本与跨边界 smoke）。
  确认历史报告中的多项问题当前已在基线内修复，仅补充资产移除 delta 回归。
  验证：Release 构建、Domain.Tests 47/47、Server.Tests 25/25、游戏数据校验、真实 PostgreSQL 容量门禁和前端生产构建全部通过。

### TASK-20260904-01 游戏日志完全重复项合并与乘数胶囊展示
- 分类：前端体验 / 日志系统 / 重复折叠 / 修仙美学胶囊 / 性能与排版优化
- 时间：提出 2026-09-04 17:13:55｜完成 2026-09-04 17:19:00（19:16:30 补服务端协议层折叠）
- 状态：✅ 已完成
- 摘要：完全相同日志连续折叠去重：以 `template` 为唯一判定依据、仅合并连续相同日志（中间插入不同日志即开启新条目，保证因果时序）；`GameLogEntry` 扩展 `segments`/`count`/`latestTimestampMs`，追加日志与末尾 template 一致时 `count += 1` 不占新缓冲槽位（500 次挂机采集仅占 1 槽，前端环形缓冲 100 条不再被刷空）；`GameLogPanel` 在 `count > 1` 时渲染 `× N` 乘数胶囊（暗金微光边框，Tooltip 显示「连续发生 N 次 (首发/末次)」）；`gameLogService.test.mjs` 4 项单测通过。
  追加（响应「200 条合并后只占一条槽位」关注）：协议层 `LogEventDto` 增加 `Count = 1` 字段、服务端 `PlayerLogBuffer` 连续相同模板就地累加、`SimulationLogMapper.ToLogEvents` 批次内折叠产生单条带计数 DTO（网络流量与 GC 显著降低）、前端 `VISIBLE_LOG_LIMIT` 调整为 200 并透传累加；新增单测覆盖「200 条相同日志合并后仅占 1 条槽位并完整保留历史记录」。
  验证：`scripts/verify.sh` 全量回归门禁通过。

### TASK-20260903-04 本地开发脚本启动时数据指纹不一致交互自愈（清空重置 / 忽略刷平）
- 分类：开发工具 / 启动脚本 / 数据自愈与重置交互 / PostgreSQL 指纹同步
- 时间：提出 2026-09-03 21:35:22｜完成 2026-09-03 21:42:30
- 状态：✅ 已完成
- 摘要：配表更新导致 `GameDataFingerprint`（SHA-256）变动、旧存档触发 `SectionCodec.DecodeAggregate` 保护性拦截（`InvalidDataException: 游戏数据指纹不一致`）使 WebSocket 无法连入；`scripts/dev.sh` 启动后端前自动比对 `config/game-data/**/*.json` 指纹（Node.js 递归按排序计算 SHA-256 前 16 位，与 `GameDataCatalog.LoadEmbedded()` 严格一致）与 `game.player_state_heads` 存量指纹，不一致时交互菜单：[1] 直接清空（`TRUNCATE identity.accounts, platform.section_flush_queue, platform.command_receipts, platform.integration_outbox CASCADE`）、[2] 忽略刷平（UPDATE 存档与队列指纹，保留等级背包）、[3] 保持原样、[0] 退出启动。
  支持 `DEV_DB_ACTION` 环境变量非交互静默传入（wipe/align/skip）兼容 CI；`printf '%s\n'` 防破折号误判、`< /dev/tty` 保证交互读键。
  实测：清空/刷平（指纹刷至 `cf10e522609193dd` 后握手正常）/指纹一致零干扰启动均通过；`scripts/verify.sh` 全量通过（构建 0 错误 0 警告、领域 47/47、服务端 22/22、容量门禁、前端构建）。

### TASK-20260903-03 全系统性能深度优化与潜在隐患排查治理（Teamwork 多 Agent 协同）
- 分类：全系统性能优化 / 潜在隐患排查 / 生产代码落地 / Teamwork 多 Agent 协同 / 门禁验证
- 时间：提出 2026-09-03 01:36:33｜完成 2026-09-03 02:02:16
- 状态：✅ 已完成
- 摘要：经 `teamwork_preview` 多 Agent 子系统（对话 ID `7e14f122-8e8a-4b5d-9411-bc1e921eaf1d`）三路并行治理：M1 Server & Protocol 优化与隐患修复、M2 Domain 领域调度与内存分配极致优化、M3 Frontend 渲染性能调优与组件收拢。
  M3 亮点：`WorldMapPanel.tsx` 大地图移动插值零 Re-render 化（DOM Ref 直操 left/top，消除每 16ms 的 942 行大组件全树重刷，hidden/后台暂停 RAF）；`TopBar`/`EquipmentPanel` 细化 Zustand selector 仅订阅 `stats.spirit`/`stats.realmLevel`；`CombatArena` 战斗属性计算惰性化（收敛至 `inspectedActor !== null` 时计算）；`ids.ts` 建立快照级 `ITEM_MAP`/`ABILITY_MAP`/`ENCOUNTER_MAP`/`EQUIPMENT_MAP` O(1) 字典消除 `.find()` 线性遍历；`CraftingPage` 排序等级预计算；前端构建 510ms、TS 0 错误。
  独立胜利审计专员（`teamwork_preview_victory_auditor`）完成全链路时间线核对、作弊排查与门禁实机复核，裁决 VICTORY CONFIRMED；领域测试 47/47、服务端测试 22/22、容量目标基准压测与前端构建全部通过。

### TASK-20260903-02 全量添加中等详细度中文注释（前后端全覆盖）
- 分类：代码工程 / 可读性与维护性 / 领域与服务端注释 / 前端组件与网络注释
- 时间：提出 2026-09-03 01:09:46｜完成 2026-09-03 01:23:00
- 状态：✅ 已完成
- 摘要：按 AGENTS.md 第 1/6 条对前后端全量添加中等详细度中文注释，五批次执行：Batch1 领域核心（`Domain/Player|Simulation|Combat|World`，Domain.Tests 44/44）、Batch2 经济/配表/协议（`AssetModels`、`MarketModels`、`OrderBook`、`MarketPartitioner`、`GameDataCatalog`、`Protocol/Contracts.cs`，44/44）、Batch3 服务端运行时与持久化（`Server/Clean/*`、`Program.cs`、`ComputeNode/*`，.NET 构建全绿）、Batch4 前端核心网络与全局状态（`network/*`、`game/*`、`uiStore.ts`，构建成功）、Batch5 前端 UI 组件与业务面板。
  注释基准：类/接口级说明分层职责与所有权模型，方法级说明调用契约与异常语义，核心分支解释业务不变量（-1 无限重复、double 精度、CAS 乐观锁重试、脏段收集）与性能取舍（零闭包/低GC），前端说明精准订阅与 memo 隔离意图；杜绝复述语法的机械注释。

### TASK-20260903-01 全系统潜在性能问题深度分析与逐项优化
- 分类：性能深度分析 / 领域与运行时优化 / 前后端性能调优 / 内存与GC优化
- 时间：提出 2026-09-03 00:38:24｜完成 2026-09-03 00:48:00（进度日志末条 00:58:30）
- 状态：✅ 已完成
- 摘要：15 项热路径性能瓶颈全部优化完成、13 次中文 Git 提交落地，严格保持行为基线与确定性不变量、零 GC 降堆分配：
  `PlayerAggregate.ConsumeDirtySections` 零闭包排序（`a399ab8`）、`SimulationEngine.AdvanceTo` 延迟分配 deltas/events（`a399ab8`）、`ProtocolSnapshotMapper`/`SectionCodec` Span 编码 + stackalloc 校验和（`a399ab8`）、前端 `GameLogPanel`/`NearbyPanel` 精准 selector + memo（`002af61`）、`OrderBook` 单趟聚合消除 GroupBy 多趟分配（`f3e699f`）、`DeterministicCombatEngine` 事件列表延迟分配（`95fb2ad`）、`WireEnvelopeCodec`/`WebSocketRuntime` 新增 `EncodeJson<T>` 单次缓冲区直出信封帧（`9007b14`）、`WorldGraph.Plan` 建图时维护有序出边消除寻路循环排序（`4174b82`）、`PlayerStateDocument` 导出/恢复循环直构（`09887a2`）、前端形象 URL 模块级 Map 缓存（`04c2289`）、`SimulationLogMapper` 就地排序 + AppendFormat（`84f654f`）、`PlayerRuntime.FlushAsync` 刷盘直通复用（`126d733`）、`PostgresSectionRepository`/`PostgresOutboxRepository` 引入 `NpgsqlBatch` 单次往返流水线（`ccd9261`）、`EconomyTransaction` 单趟校验收集（`9d1e3e6`）、`formatUnitNumber` 正整数快路径（`09212c0`）。
  领域、服务端与前端构建门禁全绿通过。


