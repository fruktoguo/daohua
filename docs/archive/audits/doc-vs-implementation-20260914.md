# 道化太初 · 文档与实现一致性审计报告

- **审计时间**：2026-09-14 19:24 – 19:45
- **任务编号**：TASK-20260914-06
- **审计方式**：六域并行只读核对（A 战斗数值 / B 行动生产 / C 经济坊市 / D 协议前端 / E 持久化运维 / F 世界配表），高影响条目由主审逐行复核
- **性质**：**只读审计**，本报告不包含任何实现改动

## 后续澄清：自然回复的配置含义

以下为 2026-09-14 用户确认的需求，优先于本报告中 A-04、D-14、X-02、X-05 及整改建议涉及自然回复的旧表述。原审计代码证据与旧文档摘录保留为历史记录，不作为直接实现指令。

- 基础生命回复是随境界的基础属性表中配置的回复点数；“2%”仅是配表选值依据，参照对应境界的基础生命上限。例如基础生命上限为 1,000 时，基础回复配置为 20 点。
- 这部分调整通过基础属性配置文件实现。运行时代码读取配置的回复属性，不硬编码 `基础生命上限 × 2%`，也不按装备等加成后的最终生命上限计算该基础回复。装备提高最终生命上限不会自动提高这部分回复。
- 生命与灵力分别读取各自配置的回复属性；本次澄清没有规定灵力回复也按 2% 配表，没有新增保底 1 点规则。
- “每五秒”回复周期仍待讨论。不得根据本报告直接确定五秒结算、十秒比率字段或二者换算方案；周期确定后再统一配置单位、结算和显示。

## 0. 审计前提与判定口径

1. **只审计规约性文档**：`docs/design/`、`docs/backend/`、`docs/frontend/`、`docs/ops/`、`docs/world/`、根 `README.md`、`AGENTS.md`、`WATCHDOG.yml`。
   **排除**：`docs/tasks/**`、`docs/tasks.md`、`docs/临时设计讨论/**`、`docs/project-analysis-*.md`（历史记录与草案，非规约）。
2. **排除编译产物**：`frontend/dist`、`node_modules`、`src/**/obj`、`src/**/bin`。
3. **本地开发阶段前提**（AGENTS.md 第 3 条）：**历史兼容性不在要求范围内**。因此「未保留旧数据格式 / 旧协议字段 / 旧迁移路径 / 旧组件模型」**不计为不符**。
4. **判定标准**：只有当文档以**现行规约口吻**要求某项能力或声明某项事实，而实现缺失、数值不同或事实不成立时，才记为不符。
5. **证据标记**：
   - `✓已复核` = 主审亲自读源码/配表逐行确认
   - `◻待复核` = 由分域核对产出，未逐行复核（行号可信，细节可再验）

---

## 1. 总览

| 域 | 条目数 | P0 | P1 | P2 |
| :--- | :---: | :---: | :---: | :---: |
| A 战斗与数值 | 12 | 5 | 4 | 3 |
| B 行动队列与生产 | 11 | 3 | 4 | 4 |
| C 经济与坊市 | 11 | 4 | 3 | 4 |
| D 协议与前端规范 | 14 | 0 | 6 | 8 |
| E 持久化与运维 | 11 | 3 | 4 | 4 |
| F 世界与配表 | 10 | 1 | 3 | 6 |
| **合计（已去重合并）** | **69** | **16** | **24** | **29** |

**性质分布**：功能缺失 38 · 文档过期/命令失效 18 · 协议单边 4 · 配表与实现不一致 5 · 命名不符 4

**根因归纳**（比条目本身更重要）：
1. **提交 `e53b879`（从零落地商业级运行时与持久化基线）是本轮绝大多数不符的源头**。它把旧战斗模拟层（`CombatSimulator` / `CombatGroupSimulator` / `CombatFormula` / `CharacterStatResolver`）整体替换为当前的 `DeterministicCombatEngine`，但配套文档没有同步降级，**回复、复活、暴击、命中对抗、五行、技能、威胁、抽怪波次**等机制连同实现一并消失。
2. **前端存在大量"文档级承诺但协议无来源"的展示面**：`combatAttributes`、`buffs` 名称与倍率、`world` 实体、`progression` 统计字段，服务端或不下发、或只下发极小子集，前端用硬编码默认值填坑（多数为 0 或 1）。
3. **市场子系统是"半落地"状态**：撮合、部分成交、状态机、租约、余额主键都已实现，但**手续费、订单过期、资产实例、Revision CAS、分区表**五项文档明确声明的机制缺失。

---

## 2. A 域：战斗与数值

| 编号 | 不符点 | 文档位置 | 实现位置 | 性质 |
| :--- | :--- | :--- | :--- | :--- |
| A-01 | 减伤公式：文档为「两层抗性 + RawDamage 平方」体系，实现只有单一双曲减伤 | `战斗设计.md:132-141`、`总纲.md:222` | `CombatModels.cs:301-302` `mitigation = 100/(100+Defense)` | 数值不符 |
| A-02 | 命中/闪避/暴击/破招/拆招对抗全部缺失，实现为确定性比较 | `战斗设计.md:153-164` | `CombatModels.cs:300` `hit = Accuracy >= Evasion`；`CombatEvent` 无暴击字段 | 功能缺失 |
| A-03 | 技能（神通）与灵力消耗零实现；`ability.*` 配表未进入领域目录 | `总纲.md:165-178`、`战斗设计.md:147` | `GameDataCatalog.cs:271-292` 仅识别 `item./equipment./map./encounter./enemy.`，**无 `ability.` 分支**；`PlayerAggregate.cs:68` Manapoints 零扣减 `✓已复核` | 功能缺失 |
| A-04 | 每 5 秒生命/灵力自然回复零实现（含死亡回蓝） | `战斗设计.md:212-214,227`、`总纲.md:253-256` | 全仓 `Regen` 仅 `StaticGameCatalogMapper.cs:266-267` DTO 透传，**零消费点** `✓已复核` | 功能缺失 || A-05 | 死亡与复活规则零实现 | `战斗设计.md:233-234`、`总纲.md:258-260` | `PlayerRespawnSeconds` 全仓**仅 1 处定义**（`CombatCatalogModels.cs:33`），零消费；战败仅置 `Blocked` `✓已复核` | 功能缺失 |
| A-06 | 怪物品阶倍率：文档 80%/300%、120%/500%、150%/2000% | `战斗设计.md:72-74`、`总纲.md:292-294` | `combatConstants.ts:288-301` normal 1.0/1.0、elite 1.2/3.0、boss 1.5/10.0 `✓已复核` | 数值不符 |
| A-07 | 出怪：文档为预算区间抽怪 + 每 5 波精英 / 每 20 波 Boss；实现仅按权重抽 1 只 | `combat-benchmark-model.md:29-31`、`总纲.md:270-285`、`通用地图与遇怪系统.md:172-208` | `SimulationEngine.cs:459-476` `TrySelectMonster`；`MinBudget/MaxBudget/EliteEnemyIds/BossEnemyIds/BattlesPerBoss` 除解析外零消费 `✓已复核` | 功能缺失 |
| A-08 | 攻速/攻击间隔硬编码 2.0 秒；文档为 1.000s→0.250s 成长 | `combat-benchmark-model.md:46,53`、`战斗设计.md:88-92` | `SimulationEngine.cs:307,342` `CombatantStats.Create(..., 2)` `✓已复核` | 数值不符 |
| A-09 | 五行属性与五行抗性零实现 | `战斗设计.md:137-141` | `CombatantStats`（`CombatModels.cs:29-36`）无元素/抗性字段；仅 DTO 透传 | 功能缺失 |
| A-10 | 装备属性未参与战斗数值；玩家 AttackPower/Defense 恒为默认 10 | `player-expected-attributes-model.md:44-56`、`总纲.md:96-111` | `PlayerAggregate.cs:72-74` 默认值，全仓**无装备/六维/境界重算点** `✓已复核` | 功能缺失 |
| A-11 | 威胁（仇恨）累积与目标选择零实现，固定 1v1 | `战斗设计.md:170-178` | 全仓 `Threat/仇恨/Aggro` 0 命中 `◻待复核` | 功能缺失 |
| A-12 | `expected-attributes.json` 被声明为数值单点真源，实际服务端零引用 | `player-expected-attributes-model.md:115` | 服务端 0 引用（仅 docs 与 `tools/config-editor`）；基准值散落在代码常量 `◻待复核` | 文档过期 |

---

### A-04 补充：该机制在文档中的**全部**规约位置（用于整改时的口径对齐）

| 位置 | 规约内容 |
| :--- | :--- |
| `docs/design/战斗设计.md:203-237` | **§9 权威规约主源**：9.1 世界常驻不重置；**9.2 每 5 秒自然回复 + 协议 10 秒字段 1:2 换算**；9.3 死亡时灵力仍回复；9.4 两类复活表 |
| `docs/design/总纲.md:251-260` | **§6.4 总纲**：同 5 秒口径 + 死亡回蓝 + 两类复活（180s 读条满状态 / 脱战 1 HP） |
| `docs/design/总纲.html:1354-1363` | 上述总纲的内嵌 HTML 副本（改口径时须同步，否则前端帮助页与规约分叉） |
| `docs/design/角色核心设计.md:75` | 属性表：脱战不自动回满，依靠**自然回复属性**/疗伤丹药/调息恢复 |
| `docs/design/角色核心设计.md:183` | **队列全受阻的「安全空转期」也应持续享受生命与灵力自然回复**（`RegenerateResources`）——与审计中「空闲不在模拟覆盖范围」的缺口直接对应 |
| `docs/design/角色核心设计.md:258` | 红线：严禁跨玩法免费重置气血/灵力/冷却，「一切恢复必须有法可循（丹药、打坐、技能或自然回复属性）」 |
| `docs/design/player-expected-attributes-model.md:37` | 数值预算：**每 1 点生命/灵力回复 = 4.0 点装备预算** |
| `docs/design/ecs-architecture-and-evolution-plan.md:52` | 实现方式建议：回复为**每秒小数推进**，用 `Remainder` 累加器保证整数面板显示与零精度丢失 |
| `docs/backend/logic-development-guide.md:81` | 后端流程：战斗遭遇推进须同时执行「5 秒自然回复」（引用了已删除的 `CombatSimulator`/`CombatGroupSimulator`） |
| `docs/design/initial-design.md:16` | 复活读条 180 秒满生命满灵力，并重置技能与物品冷却 |

**历史口径冲突记录（已被顶部用户澄清取代，不作为实现方案）**：原审计将文档概括为「每 5 秒 · 上限 2% 保底 1 点」，而
前端展示常量 `frontend/src/game/combatConstants.ts:240-241` 是固定 `hpRegen 2.0 / mpRegen 1.0` 点、
`:823-824` 又回到 2% 上限；丹药 `config/game-data/items/pills.json` 的 `hpRegen:12`/`mpRegen:8` 被
`frontend/src/components/ItemDisplay.tsx:422,425` 渲染为「**每秒**」；协议侧另一半口径在
`战斗设计.md:216-223`（10 秒比率 4%/2%）。这些旧表述不能直接决定整改公式：基础回复读取境界基础属性配置，2% 仅用于生命回复配表，周期仍待讨论。

---

## 3. B 域：行动队列、挂机模拟与生活生产

| 编号 | 不符点 | 文档位置 | 实现位置 | 性质 |
| :--- | :--- | :--- | :--- | :--- |
| B-01 | 材料不足应进入 `Blocked` 并跳过一次；实现直接写负库存增量 → 资产事务抛错回滚 | `initial-design.md:57,59` | `SimulationEngine.cs:788-790` 无条件写输入负 delta；全流程无输入校验 `✓已复核` | 功能缺失 |
| B-02 | 立即消耗品的 Buff / 冷却 / 即时回复值零实现 | `持续消耗品系统.md:41-45` | `GameDataCatalog.cs` `ParseItem` 内 `buff/hitpointRestore/cooldown` **0 命中** `✓已复核`；`ActiveBuff`(`PlayerAggregate.cs:92`) 无构造点 | 功能缺失 |
| B-03 | 自动使用槽不校验物品合法性与库存 | `initial-design.md:17` | `PlayerAggregate.cs:607-632` 仅校验槽位索引与字符串长度 `✓已复核` | 功能缺失 |
| B-04 | 行动的地点限制（`availableNodeIds`）在入队与开始时均未校验 | `initial-design.md:158` | `AllowedNodeIds` 仅投影给前端；`PlayerRuntime.cs:424-432` 与模拟引擎均无校验 `◻待复核` | 功能缺失 |
| B-05 | 地点修炼倍率未参与服务端收益结算 | `initial-design.md:159` | `cultivationMultiplier` 仅经 `StaticGameCatalogMapper.cs:211`、`ProtocolSnapshotMapper.cs:260` 供前端 `◻待复核` | 功能缺失 |
| B-06 | **战斗与移动行动的持续消耗品永不激活**（`continue` 早于自动使用结算） | `持续消耗品系统.md:61` | `SimulationEngine.cs:135-148` 在 `:165 PrepareSustainedAutoUse` 之前 `continue` `✓已复核` | 功能缺失 |
| B-07 | 非循环队列中受阻行动应被移除；实现只 `break`，行动永久滞留队首 | `initial-design.md:59` | `SimulationEngine.cs:109-123` `✓已复核` | 功能缺失 |
| B-08 | 持续消耗品应按实际耗时扣减（`ActualDuration = Base / (1+SpeedBonus)`）；实现按基础耗时 | `持续消耗品系统.md:88-92,110` | `SimulationEngine.cs:888` 直接减 `definition.DurationSeconds`；全仓无 SpeedBonus 概念 `✓已复核` | 功能缺失 |
| B-09 | 队列重复次数上限：文档 `1..1000`，实现 `1..1_000_000` | `initial-design.md:58` | `PlayerAggregate.cs:335` `> 1_000_000`（`PlayerStateDocument.cs:309` 同值）`✓已复核` | 数值不符 |
| B-10 | 止血散/顺气丸效果数值与文档完全不同 | `总纲.md:209-210` | `pills.json:35-36` `hpRegen:12`+`damageAbsorptionRatio:0.05`；`:58-59` `mpRegen:8`+`attackSpeedRatio:0.05` `✓已复核` | 数值不符 |
| B-11 | 引气入体单轮灵力产出文档内部矛盾（1 vs 10） | `持续消耗品系统.md:136,150` vs `initial-design.md:28` vs `总纲.md:208` | `cultivation.json:8 qiPerSecond:1`，10 秒整秒累加 → 每轮 10 灵力 `✓已复核` | 文档自相矛盾 |

---

## 4. C 域：经济与坊市

| 编号 | 不符点 | 文档位置 | 实现位置 | 性质 |
| :--- | :--- | :--- | :--- | :--- |
| C-01 | **卖方 2% 手续费未实现**，成交恒按零税入账 | `commercial-persistence-architecture.md:165`、`performance-baseline.md:166` | `MarketRuntime.cs:235` 构造 `MarketFillInput` 时 `SellerTax` 实参为 `0d` `✓已复核`；前端 `market.zh.ts:78,105` 仍显示「2% 卖方税」 | 功能缺失 |
| C-02 | **30 天订单过期未落地** | `initial-design.md:220` | `MarketRuntime.cs:94,456` `ExpiresAt = null`；`OrderBook.cs:164 Expire()` **零调用** `✓已复核`；`market.orders` 无 `expires_at` 列（`PostgresSchema.cs:193-209`）`✓已复核`；前端仍渲染到期时间 | 功能缺失 |
| C-03 | **Revision CAS 未实现**，实为行锁 + 版本自增 | `domain-data-architecture.md:158`、`commercial-persistence-architecture.md:68` | `PostgresMarketRepository.cs` / `PostgresEconomyRepository.cs` 均为 `SELECT … FOR UPDATE` + `version = version + 1`，无 `WHERE version = ?` 比对；`revision` 0 命中 `✓已复核` | 文档过期 |
| C-04 | 唯一装备/实例表 `economy.player_asset_instances` 零读写 | `commercial-persistence-architecture.md:150` | 全仓仅 `PostgresSchema.cs:136,145,175`（建表+索引+外键），**无 INSERT/SELECT** `✓已复核` | 功能缺失 |
| C-05 | 结算领取语义：文档要求「领取 = 订单/冻结/余额/流水同一事务」，实现只写领取游标 | `commercial-persistence-architecture.md:220` | `PostgresMarketRepository.cs:19-84` `ClaimSettlementAsync` 仅 INSERT 游标；`:15-18` 注释明确「claim 不是再次发钱」 `◻待复核` | 文档过期 |
| C-06 | `market.trades` 声称按月 RANGE 分区，实为普通表 | `commercial-persistence-architecture.md:163` | `PostgresSchema.cs:214-229` 普通 `CREATE TABLE` + 两个普通索引 `✓已复核` | 功能缺失 |
| C-07 | 下单事务锁顺序与文档规定的固定顺序不符（余额先于对手订单行加锁） | `commercial-persistence-architecture.md:256` | `PostgresMarketRepository.cs:204` 租约 → `:221` 余额 → `:518` 对手订单行 `FOR UPDATE` `◻待复核` | 文档过期 |
| C-08 | 文档列出的市场子域文件不存在 | `domain-data-architecture.md:198-203` | `src/Domain/Market/` 仅 `MarketModels.cs`/`OrderBook.cs`/`MarketPartitioner.cs`，文档所列 3 文件 0 命中 `✓已复核` | 文档过期 |
| C-09 | 「`Stats` 切片持灵石」过期，货币只在 `economy` 表 | `domain-data-architecture.md:57` | `PlayerAggregate.cs:250-255` 无 Stats/Inventory 切片；货币唯一来源 `economy.player_asset_balances` `✓已复核` | 文档过期 |
| C-10 | `market.orders` 索引描述与建表不符（文档含 `created_at`，实际为 `sequence` 等） | `commercial-persistence-architecture.md:161` | `PostgresSchema.cs:210` `(partition_id, asset_id, variant, status, side, price, sequence)` `✓已复核` | 文档过期 |
| C-11 | 市场账本接口命名不符（文档 `IMarketLedgerStore`，实为 `IMarketTransactionStore`） | `initial-design.md:215,220` | `PersistenceContracts.cs:273`；`IMarketLedgerStore` 0 命中 `✓已复核` | 命名不符 |

---

## 5. D 域：网络协议与前端规范

| 编号 | 不符点 | 文档位置 | 实现位置 | 性质 |
| :--- | :--- | :--- | :--- | :--- |
| D-01 | 战斗属性面板 9 项恒为 0（服务端 combat 分段只有 6 字段） | `AGENTS.md:136`（协议强对齐） | `PlayerStateDocument.cs:47-53` 六字段 vs `snapshot.ts:384-413` 硬编码 0；`CombatArena.tsx:1308-1324` 直接渲染 `✓已复核` | 协议单边 |
| D-02 | Buff 名称与全部增益倍率服务端不下发，界面回落默认值 | `PlayerAggregate.cs:92`（仅 3 字段） | `snapshot.ts:466-478` 读 9 个不存在字段；`App.tsx:516-526`、`CombatArena.tsx:1081` 依赖之 `◻待复核` | 协议单边 |
| D-03 | `world` 分段下发的实体数组被前端整体丢弃 | `ProtocolSnapshotMapper.cs:261-271` | `snapshot.ts:452-455` 全部硬编码 `[]` `◻待复核` | 协议单边 |
| D-04 | `progression` 统计字段前端解析、服务端不存在（恒 0） | `PlayerStateDocument.cs:29-35`（6 字段） | `snapshot.ts:364-373` 读 9 个不存在字段 `◻待复核` | 协议单边 |
| D-05 | 装备/功法面板等级与六维由前端常量伪造（恒 1、triggers 空） | `common-components.md:244`、`AGENTS.md:115` | `snapshot.ts:424-435` 硬编码 `level: 1` `✓已复核` | 功能缺失 |
| D-06 | **多语言能力文档承诺但实际不可达**（语言白名单只有一个，切换函数首行即返回） | `common-components.md:169,174` | `i18n/index.ts:13` `LOCALES = ['zh-CN']`；`:171-172` `setLocale` 首行 `if (!isLocale(locale)) return` `✓已复核` | 功能缺失 |
| D-07 | 增量 `lastSimulatedAt` 存在"缺失即沿用旧锚点"兜底，与契约「必须下发」表述冲突 | `Contracts.cs:300-305`、`AGENTS.md:42` | `snapshot.ts:583` 用 `optionalString` 容错 `✓已复核` | 实现超出文档 |
| D-08 | 文档两处列出 `FloatingMenu` 组件，实际不存在（仅注释提及） | `common-components.md:21`、`floating-popover.md:48` | `FloatingPopover.tsx` 导出 `FloatingMenuItem/Divider/Group`，无 `FloatingMenu` `✓已复核` | 命名不符 |
| D-09 | 「攻击间隔已彻底移除」不成立，仍在战斗面板渲染 | `common-components.md:16` | `CombatArena.tsx:1318`、`CombatEncounterPanel.tsx:73` 仍渲染；`snapshot.ts:387` 保留字段 `◻待复核` | 文档过期 |
| D-10 | 文档指路选择器 `.item-badge-rarity-*` 不存在 | `common-components.md:104` | 全仓 0 命中；品质色由 `.item-tile[data-rarity]`（`selection-grid.css:154-197`）`◻待复核` | 文档过期 |
| D-11 | 文档指路选择器 `.trigger-rule-row` 不存在 | `common-components.md:281` | 0 命中；实际为 `.combat-trigger-card/-list/-fields` `◻待复核` | 文档过期 |
| D-12 | 文档指路 `styles.css` 中的 `.connecting-screen`/`.error-screen` 无样式定义 | `common-components.md:213` | 类名被 3 处使用（`ConnectingPage.tsx:24,74`、`ErrorBoundary.tsx:88`），但 `frontend/src/styles/**` 0 命中，实际只有 `.status-screen` `✓已复核` | 文档过期 |
| D-13 | 已收拢的公共组件未登记（`AbilityGridPicker`，及 5 个 market 组件未按条目登记路径） | `common-components.md:108-116` | `AbilityGridPicker.tsx:11-28` 为通用网格选择器 `✓已复核` | 文档过期 |
| D-14 | 丹药回复周期口径冲突：i18n 写「每 5 秒」，组件写「每秒」 | `insight.zh.ts:83,125`、`总纲.md:253-256` | `ItemDisplay.tsx:422,425` 输出「每秒气血回复」`✓已复核` | 命名不符 |

---

## 6. E 域：持久化、容量与运维

| 编号 | 不符点 | 文档位置 | 实现位置 | 性质 |
| :--- | :--- | :--- | :--- | :--- |
| E-01 | 性能基线文档给出的 7 个压测场景命令全部失效 | `performance-baseline.md:37,73,96,116,162,177,190` | `LoadTests/Program.cs:23-28`、`LoadTestOptions.cs:34-43` 仅支持 `capacity-target/postgres/compute/websocket/http-read`，其余抛「未知场景」`✓已复核` | 命令失效 |
| E-02 | 首次部署命令的 ComputeNode 镜像名错误 | `ops/deployment.md:104-105` | `scripts/docker-build-prod.sh:10,77,80` 产出 `daohua-compute:<sha>`，从不生成 `daohua:compute-sha-*` `✓已复核` | 命令失效 |
| E-03 | `/api/debug/fast-forward` 端点不存在 | `performance-baseline.md:145` | `src/Server` 无 `api/debug` 或 `fast-forward` 命中 `✓已复核` | 文档过期 |
| E-04 | `identity.audit_events` 声称按月分区，实为普通表 | `commercial-persistence-architecture.md:134` | `PostgresSchema.cs:48-56` 普通表 + 普通索引，全仓无 `PARTITION BY` `✓已复核` | 文档过期 |
| E-05 | `Runtime:PlayerShardCount` 玩家分片配置键不存在 | `AGENTS.md:205` | 全仓 0 命中；`ServerOptions.cs` 无该键 `✓已复核` | 文档过期 |
| E-06 | 声称使用 PostgreSQL advisory lock，实际为 `market.partition_leases` + fence token | `AGENTS.md:46`、`domain-data-architecture.md:158` | 全仓 `advisory/pg_advisory` **0 命中**；租约表在 `PostgresSchema.cs:185-191` `✓已复核` | 文档过期 |
| E-07 | 本地库主版本与文档不符（文档 18，本地 17） | `ops/deployment.md:10`、`README.md:16` | `compose.yaml:6` `postgres:17-alpine`（生产 `compose.prod.yaml:5` 为 18）`✓已复核` | 配置不符 |
| E-08 | Caddy 拦截路径与真实端点不匹配（拦截失效） | `ops/deployment.md:147`、`AGENTS.md`（必须拦截 metrics） | 真实端点为 `/api/v1/metrics`（`Program.cs:360`）与市场组 `/metrics`（`:312`），`deploy/xx.yuohira.com.caddy:10` 只拦 `/api/metrics` `✓已复核` | 配置不符 |
| E-09 | `/readyz` 未校验 schema 与市场租约（只做 `SELECT 1`） | `ops/deployment.md:146` | `PostgresHealthCheck.cs:14` 仅 `SELECT 1`；租约校验只在 `MarketHealthCheck.cs:9` `◻待复核` | 功能缺失 |
| E-10 | `/healthz` 未暴露版本 | `AGENTS.md:172` | `Program.cs:430-442` 仅返回 `status/service/checks`（`releaseVersion` 只用于 `/api/v1/meta`）`✓已复核` | 功能缺失 |
| E-11 | 数据库角色未按方案拆分（应为 5 类专用账号） | `commercial-persistence-architecture.md:549` | `deploy/compose.prod.yaml:10-12` 全链路单一 `daohua` 账号 `✓已复核` | 功能缺失 |

---

## 7. F 域：世界、地图与配表

| 编号 | 不符点 | 文档位置 | 实现位置 | 性质 |
| :--- | :--- | :--- | :--- | :--- |
| F-01 | 文档规定 `minBudget/maxBudget/eliteInterval/bossInterval` 为 **`WorldNode` 字段**，配表节点上**完全没有这些字段** → 前端「危险度」恒不显示 | `通用地图与遇怪系统.md:33-35,138-141,172-177`、`总纲.md:270` | `config/game-data/maps/*.json` 全节点 **0 命中** `✓已复核`；预算实际写在 `encounters/*.json`；`NearbyPanel.tsx:88` 因 `world.maxBudget == null` 恒 false | 配表缺失 |
| F-02 | 怪物 `budgetCost` 与文档不符（≥3 例） | `通用地图与遇怪系统.md:222,232` | 木灵 15（文档 25）、魔化野兔 10（文档 15）、铁甲熊 45（文档「重甲蛮熊 35」）`✓已复核` | 配表不符 |
| F-03 | 怪物中文名不符（3 例） | `通用地图与遇怪系统.md:103,118-119,264` | `铁甲熊` vs 重甲蛮熊、`魔猿` vs 通臂魔猿、`伥虎` vs 伥鬼妖虎 `✓已复核` | 命名不符 |
| F-04 | 节点 ID 命名不符 + 文档节点不存在 | `通用地图与遇怪系统.md:117-120,132` | `node.herb-vale` vs 配表 `node.herb-valley`；`node.twilight-pass`、`node.spirit-spring` 配表 0 命中 `✓已复核` | 命名不符 |
| F-05 | `maps.md` 节点坐标与配表不符（4 例） | `world/maps.md:15-18` | 百草谷 (10,50)→(10,45)、落霞桃林 (38,55)→(38,78)、清石水滩 (35,75)→(35,92)、望云茶肆 (45,40)→(45,48) `✓已复核` | 文档过期 |
| F-06 | `maps.md` 声称已存在的节点在配表中不存在 | `world/maps.md:52,66` | `node.stone-vein`、`node.ancient-terrace` config 0 命中 `✓已复核` | 配表缺失 |
| F-07 | `map.starfall-domain`（星陨异境，7 节点全套 T2）在设计文档中**完全未提** | `world/maps.md:3`、`通用地图与遇怪系统.md:111-113` | `maps/xingyun_realm.json:3-4` 存在；design/world 文档 0 命中 `✓已复核` | 文档缺失 |
| F-08 | 遭遇名与节点重名且挂载错位 | `encounters/tier2_encounters.json:4` | 「坠星天坑」遭遇名与 `xingyun_realm.json:141` 节点同名，却挂给另外 6 个节点 `✓已复核` | 命名不符 |
| F-09 | **怪物配表根本没有属性字段**：`maxHitpoints`/`hpRegenPerTenSeconds`/`mpRegenPerTenSeconds` 全部缺失，属性完全由 `budgetCost` × 权重推导 | `战斗设计.md`（怪物属性表）、`StaticGameCatalogMapper.cs:245,266-267` 均按这些字段读取 | 配表实际字段仅 `budgetCost/category/dropTable/element/id/level/name/rank/skills/style/tier/visual/weights` `✓已复核` | 配表缺失 |
| F-10 | 星陨异境节点与战斗行动的双向声明不一致 | `combat.json` `combat.sweep-twilight-valley.availableNodeIds` 含 `node.astral-rift` | `xingyun_realm.json` 中 `node.astral-rift` 与 `node.tea-house` 的 `availableActionIds` 为 `null` `✓已复核` | 配表不一致 |

---

## 8. 文档内部自相矛盾（非"文档 vs 实现"，但同源）

| 编号 | 矛盾内容 | 位置 |
| :--- | :--- | :--- |
| X-01 | 引气入体单轮灵力产出：`1 灵力` vs `10 灵力` vs `10 MP → 13 MP` | `持续消耗品系统.md:136,150` vs `initial-design.md:28` vs `总纲.md:208` |
| X-02 | 丹药回复周期：设计文档统一「每 5 秒」，前端组件文案为「每秒」 | `总纲.md:253-256` vs `ItemDisplay.tsx:422,425` |
| X-03 | 玩家基准上限：领域默认 HP/MP = 100/100，前端基准 100/50 | `PlayerAggregate.cs:66-70` vs `combatConstants.ts:228-241` |
| X-04 | 世界观命名体系（云来集 / 青岚药谷 / 落霞西岭…）与主线配表命名体系（云溪村 / 百草谷 / 落霞桃林…）并存，`maps.md` 仅以"未来储备"一句带过 | `world/lore.md:33-41`、`world/story.md:16-22` vs `maps/qingyun_mountain.json` |
| X-05 | `战斗设计.md` 表述「每 10 秒比率字段」与「底层每 5 秒结算」并存，协议字段名与周期语义分离 | `战斗设计.md:216-222` |
| X-06 | 反例（已对齐，供对照）：`maps.md` 声称以 `map.qingyun-mountains` 8 节点为权威基准，实际配表也是 8 节点 7 条边，此点一致 | — |

---

## 9. 复核后剔除的条目（避免误报进入整改清单）

| 原报告条目 | 剔除原因 |
| :--- | :--- |
| 「`frontend/src/game/ids.ts` 装备兜底清单缺 12 件（仅 16 条）」 | **误报**。逐 ID 比对：config 28 件、`ids.ts` 28 件，**双向差集均为空** `✓已复核` |
| 「`ids.ts` 兜底配方与 `alchemy.json` 不符（`morning-dew:1`、`ningxue-grass:2`）」 | **误报**。`ids.ts` 内不存在 `morning-dew`/`ningxue-grass`/`herb-essence` 任何字段 `✓已复核`；且 `alchemy.json` 配方与 `总纲.md:208-210` 完全一致 |
| 「`world/lore.md` 节点名在配表中大面积不存在」 | **降级为 X-04**。`maps.md` 已显式声明这些是"未来世界演进储备"，不属于规约与实现不符 |
| 「`src/Domain` 依赖违反架构边界」 | **误报**。`Domain.csproj` 实际仅引用 `YuoCore.Abstractions` + `YuoCore.Ecs`，依赖方向合规 `✓已复核`（仅"依赖图未列 Abstractions"可作 P2 修正，已并入备注） |

---

## 10. 已核对确认一致（无需修改，避免重复怀疑）

- `WireMessageType` 1–17 枚举与前端 `protocol.ts` 逐项一致
- 信封 36 字节报头、魔数、端序、7 个 section 名、`PresenceState`/`ActionQueueStatus` 序号一致
- CSS 导入顺序、悬浮卡宽度与延迟参数、`z-index`、三处 `image-rendering: pixelated` 与规范一致
- `template.csv` 对代码中 712 个 `t()` 字面量键**零缺登记**
- `uiStore`/`gameStore`/`marketStore` 无空函数体虚空方法
- `common-components.md` 中所有源码路径均真实存在（无 404，除 D-08 组件名）
- 容量数字（5000/100/500/2000）与 `CapacityTargetScenario.cs:5-14` 完全一致
- `scripts/verify.sh`、`docker-build-prod.sh`、`smoke-production.mjs`、`capacity-target.sh`、`deploy/` 下 service/timer 全部存在
- 28 件装备的 T1/T2 锻造配方、10 门技能 CD/耗蓝/倍率、6 种加工主料、4 种丹药配方与文档逐条一致
- 饰品 T1/T2 预算 22/32 点与 `accessories.json` 一致
- 市场撮合的价格优先/时间优先、部分成交、状态机、余额主键、分区单写者租约、reservation 托管均已落地

---

## 11. 建议的整改优先级

**第一批（P0，直接影响玩法闭环或安全声明）**
1. `C-01` 手续费、`C-02` 订单过期、`C-03` Revision CAS、`C-04` 资产实例 —— 市场四件套，直接决定经济系统是否可上线
2. `A-04` 自然回复 + `A-05` 复活 —— 玩家可感知的血蓝循环，且已有配表与文档双份规约
3. `A-03` 技能/灵力消耗 + `A-10` 装备属性接入 —— 没有这两项，战斗只有平A且不吃装备
4. `E-08` Caddy 拦截失效 + `E-11` 数据库角色未拆分 —— 安全边界声明不成立
5. `B-01` 材料不足未入 `Blocked` + `B-06` 战斗/移动自动使用槽失效 —— 队列语义与丹药机制的实际断点

**第二批（P1，界面与协议对齐）**
- `D-01`~`D-05` 协议单边字段：要么服务端补齐下发，要么前端删除伪造默认值（AGENTS.md 明确禁止凭空填默认值）
- `E-01`/`E-02`/`E-03` 失效命令与端点：让文档命令可执行，或从文档删除

**第三批（P2，文档治理）**
- 全部"文档过期/命名不符/清单未同步"条目：统一以「实现为准」修订文档，并给 `AGENTS.md` 的 E-05/E-06 两条失实声明加"规划中"标注或删除
- 建议同时建立**文档-实现双向校验门禁**：至少覆盖 `AGENTS.md` 中可机检的声明（配置键、端点、脚本路径、镜像标签规则）

---

## 12. 可执行性分级（本轮补充：哪些可以直接实现）

> 判定依据：配表字段是否已齐备、是否需要新的产品/数值决策、是否触及生产或外部资源。
> 已核实前提：`pills.json` 的 `consumable` 含 `buffs/cooldownSeconds`；`abilities/*.json` 含
> `cooldownMilliseconds/castMilliseconds/manaCostRatio/damageRatio/flatDamage/defaultTriggers/targetType`；
> `encounters/*.json` 含 `minBudget/maxBudget/randomSpawns(strength)/maxTotalStrength/eliteEnemyIds/bossEnemyIds/battlesPerBoss`
> —— **这三块配表均已齐备，属"实现缺失"而非"设计缺失"**。

### 12.1 可直接实现（无需额外决策）

| 组 | 条目 | 说明 |
| :--- | :--- | :--- |
| 纯文档修订（零代码风险） | E-01、E-02、E-03、E-04、E-05、E-06、C-05、C-08、C-09、C-10、C-11、D-07、D-08、D-09、D-10、D-11、D-12、D-13、F-06、F-07 | 以实现为准改写文档；修正失效命令/镜像名/端点/配置键声明；补齐未登记组件与地图 |
| 小改（单次构建可验） | B-03 自动槽校验合法性、B-04 地点校验、B-05 修炼倍率入收益、B-06 战斗/移动自动槽顺序、B-07 受阻行动出队、F-10 节点与行动双向声明、E-08 Caddy 拦截 `/api/v1/metrics`、E-09 `/readyz` 校验 schema、E-10 `/healthz` 暴露版本 | 改动面小、语义明确，均有配表或文档唯一答案 |
| 中等功能（配表已齐，可跑测试验证） | A-07 预算抽怪 + 精英/Boss 波次、B-01 材料不足入 `Blocked`、B-02 战斗丹药 buff/冷却/回复、A-10 装备属性接入战斗数值 | 需要的字段配表全有，属"接线"而非"设计" |
| 中大型功能（需分阶段；回复口径已澄清） | A-04 读取配置的自然回复（周期待讨论）、A-05 死亡与两类复活、A-03 技能（CD/吟唱/耗蓝/倍率/触发） | 原“配表完整、规约明确”的判断不适用于 A-04 周期；回复数值由基础属性配置提供，具体边界确认后再实施 |

### 12.2 需要你先拍板一个口径（一句话即可开工）

| 条目 | 待决策点 | 建议 |
| :--- | :--- | :--- |
| A-04 | 基础回复数值来源已澄清；周期待讨论 | 基础生命回复在境界基础属性表中配置，2% 仅为配表选值依据；代码读取回复属性。原“每 5 秒、上限 2% 保底 1 点、协议保留 10 秒比率”建议已撤回，不推定灵力采用相同比例 |
| A-01 | 减伤公式：文档两层抗性体系 vs 现状单一双曲 | 保持现状 + 先改文档，或按文档重写（会影响全部数值校准） |
| A-02 | 是否恢复随机命中/暴击/破招拆招 | 影响确定性回放与既有测试，需明确 |
| A-06 | 怪物品阶倍率以哪边为准 | 以配表/前端现状为准改文档，或按文档 80%/300% 改配表 |
| A-08 | 攻速成长 | **注意**：`fe450ab` 刚"移除装备攻击间隔属性"，与文档攻速成长冲突，建议维持现状并改文档 |
| B-08 | 是否引入 SpeedBonus / 实际耗时体系 | 现状无该概念，建议降级文档 |
| B-09 | 队列重复上限取 1000 还是 100 万 | 建议以现状 100 万改文档（本地开发期更宽松） |
| B-10 | 丹药数值以哪边为准 | 配表 `hpRegen:12/mpRegen:8` 已是可玩值，建议改文档 |
| F-02/F-03/F-04/F-05 | 配表与文档的数值/命名冲突 | 建议统一「以配表为准、改文档」 |
| F-09 | 怪物属性来源 | 维持"由 `budgetCost` 推导"并改文档，或补配表显式属性字段 |
| C-03 | 是否改造成 Revision CAS | 现状 `FOR UPDATE` 已保证正确性，改造收益主要是架构对齐 |
| D-01~D-04 | 协议单边字段：服务端补齐 vs 前端删除伪造值 | 建议先"前端删除伪造默认值"，再随 A-09/A-10 落地逐步补齐下发 |
| E-07 | 本地库版本 17 与文档/生产 18 不一致 | 统一为 18（改 compose）或统一为 17（改文档） |

### 12.3 我不能独立完成（需外部资源、权限或新产品语义）

| 条目 | 阻塞原因 |
| :--- | :--- |
| C-04 `player_asset_instances` 读写 | 需要先定义"装备是否为唯一可交易实例"的产品语义，属新功能设计 |
| A-11 威胁累积与多目标选靶 | 当前战斗为 1v1 模型，多目标/队伍战需先确立战斗形态 |
| D-06 多语言 | 框架可改通，但除 `zh-CN` 外没有其它语言的词条内容 |
| E-11 生产数据库角色拆分 | 涉及生产库凭据与在线改角色，须你在服务器侧授权与执行；我只出脚本与文档 |

### 12.4 我的验证能力边界（承诺范围）

- **能**：`dotnet build -c Release`、`dotnet test`（Domain.Tests / Server.Tests）、`npm --prefix frontend run build`、本地 PostgreSQL 只读核对（`docker exec -i daohua-local-db psql`）、前端真实浏览器像素验收（Edge + playwright-core）。
- **不能**：`scripts/verify.sh`（其 `npm ci` 会删除整个 `node_modules`，已列为本项目红线）、生产部署与镜像推送、任何需要生产凭据的操作。

### 12.5 建议的第一批（低风险、可当天闭环）

1. **E-08 + E-09 + E-10**：安全与可观测性修复（Caddy 拦截、`/readyz`、`/healthz`）
2. **B-03 + B-04 + B-05 + B-06 + B-07**：队列语义与挂机收益一整组（同一处代码面，一次改完一次验）
3. **A-07**：预算抽怪 + 精英/Boss 波次（配表已齐，战斗体验提升最直接）
4. **文档批量修订**（12.1 第一组 20 条）：一次提交清理全部失效命令与过期声明
