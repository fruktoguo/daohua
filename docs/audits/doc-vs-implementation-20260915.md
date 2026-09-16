# 道化太初 · 文档与实现一致性深度复核（2026-09-15）

- **审计时间**：2026-09-15 01:47 – 03:30
- **任务编号**：TASK-20260915-03
- **审计基线**：HEAD `82f1665` + 工作区（工作区仅任务登记类文档改动，不属审计对象）
- **审计方式**：六域并行只读核对（A 战斗数值 / B 行动生产 / C 经济坊市 / D 协议前端 / E 持久化运维 / F 世界配表），P0/P1 级新发现由主审逐行复核源码确认
- **性质**：**只读审计**，本报告不包含任何实现改动
- **前序报告**：[doc-vs-implementation-20260914.md](doc-vs-implementation-20260914.md)（69 项）· [-recheck.md](doc-vs-implementation-20260914-recheck.md)（40 成立 / 26 部分成立 / 3 误报）
- **整改背景**：TASK-20260914-09 主体已随 `4e22179` 提交落地（+2183/−305，55 文件：schema/快照 v2、CombatSkillEngine、PlayerAttributeResolver、EncounterWaveSelector、BaseAttributeCatalog、市场过期与卖方税等），文档于 `82f1665` 同步

## 后续澄清：行动产出模型的口径（2026-09-15 用户确认）

以下为 2026-09-15 用户确认的设计口径，优先于本报告 NEW-B-12、B-11、X-01 中「灵力产出」的旧表述：

- **行动产出是通用可配置模型，不限定产出类型**：不同行动可分别声明产出修为、灵力（战斗资源）、物品、Buff 等任意收益；引擎应按行动配表的收益声明逐项结算，而不是按行动类别硬编码单一通道。
- 当前实现把「修炼类 → `qiPerSecond` → 修为 Qi」写死，物品产出仅采集/炼丹/炼器的 `outputs` 承载，灵力与 Buff 无任何行动产出通道——**缺的是通用收益模型，不是"补一个灵力字段"**。
- 文档侧矛盾同样须按此口径重写：`master-design.md:208` 的 10 MP 表述、`sustained-consumable-system.md:136,150` 与 `initial-design.md:28` 的灵力/修为口径，统一改为「行动产出 = 配表声明的收益列表」。
- 据此 NEW-B-12 重新定性为：**通用行动收益模型缺失（功能缺口）**，P0 → P1；实现语义（修为）本身不算错，错在通道被限定死且文档承诺的灵力产出无任何落地路径。

## 0. 判定口径（沿用 9/14 审计）

1. 只审计规约性文档：`docs/design|backend|frontend|ops|world/`、根 `README.md`、`AGENTS.md`、`WATCHDOG.yml`、`docs/README.md`；排除 `docs/tasks/**`、`docs/临时设计讨论/**`、`docs/project-analysis-*.md`、`vendor/YuoCore`（另有专项分析）。
2. 本地开发阶段不做历史兼容要求；旧格式/旧字段缺失不计为不符。
3. 只有当文档以现行规约口吻要求能力或声明事实，而实现缺失、数值不同或事实不成立时记为不符；文档自相矛盾单独归类 X。
4. 每条结论附 file:line 证据；P0/P1 级结论经主审逐行复核。

---

## 1. 总览

### 1.1 69 项历史条目处置结果

| 域 | 条目 | ✅已闭环 | ◐部分闭环 | ⚠️仍存在 | 🔄状态变化 | ⬜维持误报 |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| A 战斗与数值 | 12 | 6 | 2 | 2 | 2 | 0 |
| B 行动与生产 | 11 | 4 | 3 | 3 | 1 | 0 |
| C 经济与坊市 | 11 | 9 | 1 | 1 | 0 | 0 |
| D 协议与前端 | 14 | 3 | 3 | 7 | 0 | 1 |
| E 持久化与运维 | 11 | 9 | 2 | 0 | 0 | 0 |
| F 世界与配表 | 10 | 7 | 1 | 1 | 0 | 1 |
| **合计** | **69** | **38** | **12** | **14** | **3** | **2** |

**整体判断**：整改落地率 **55%（38/69 完整闭环）+ 17%（12 部分闭环）**。市场四件套、行动队列语义、战斗公式/属性/复活/波次框架、运维文档口径已扎实落地；残留集中于 **消耗品执行链、数值基准对齐、协议硬编码、文档索引治理** 四类。

### 1.2 新增偏离统计（整改大改动引入）

| 域 | 新增 | P0 | P1 | P2 |
| :--- | :---: | :---: | :---: | :---: |
| A 战斗与数值 | 6 | 2 | 4 | 0 |
| B 行动与生产 | 3 | 0 | 1 | 1 |
| C 经济与坊市 | 10 | 0 | 1 | 9 |
| D 协议与前端 | 10 | 0 | 4 | 6 |
| E 持久化与运维 | 17 | 0 | 3 | 14 |
| F 世界与配表 | 8 | 0 | 4 | 4 |
| **合计** | **45+1 记录** | **2** | **16** | **26** |

**新增根因归纳**：
1. **「接线了框架、没接数值」**：技能/回复/波次框架全部落地，但数值层与配表/基准脱节（Lv.1 无法施法、品阶倍率缺失、回复周期硬编码 5s）。
2. **「协议下发了一半」**：快照 v2 两端重写只消化了部分字段，`combat` 技能/冷却状态、`world` 预算/怪物生态仍被丢弃；`ProtocolSnapshotMapper` 出现新的硬编码默认值，直接违反 AGENTS.md「禁止脱离配表伪造协议载荷」。
3. **「修了 A 处、漏了 B 处」**：schema v1→v2 只改了一半文档（README/initial-design/domain-data/migration/commercial 同文 v1/v2 并存）；`initial-design.md:215-220` 整段（IMarketLedgerStore/revision CAS/索引待补）未重写。
4. **运维叙事超前于实现**：ComputeNode 链路、audit_events 写入、五角色生产落地、OTel/pgBackRest/水位保护均无实现支撑；smoke 默认口令过不了自身校验。

---

## 2. A 域：战斗与数值

### 2.1 历史条目复审

| 编号 | 状态 | 当前证据 | 备注 |
| :--- | :--- | :--- | :--- |
| A-01 | ✅已闭环 | `combat-design.md:132-140` vs `CombatModels.cs:419-430` 两层抗性+五行减免 | 公式已对齐 |
| A-02 | ✅已闭环 | `CombatModels.cs:404-414` break/parry→命中/暴击链 | 命中/闪避/暴击/破招/拆招落地 |
| A-03 | ◐部分闭环 | `GameDataCatalog.cs:310-313` 解析 ability；`CombatSkillEngine.cs:115-126` CD/吟唱/耗蓝/触发 | 已实现调度，但 Lv.1 蓝量不足以释放（NEW-A-01） |
| A-04 | ◐部分闭环 | `BaseAttributeCatalog.cs:53-68` 读 HpRegen/MpRegen；`SimulationEngine.cs:107-148` 自然回复 | 数值来源已对齐配置，但周期硬编码 5s（NEW-A-06） |
| A-05 | ✅已闭环 | `SimulationEngine.cs:435-451` 死亡读条复活；`PlayerAggregate.cs:456-459` 脱战 1 HP | 两类复活落地 |
| A-06 | ⚠️仍存在 | `combat-benchmark-model.md:29-31` vs `GameDataCatalog.cs:465-498` 无 rank 缩放；`combatConstants.ts:288-299` 仍 1.2/3.0、1.5/10.0 | 品阶倍率未引入 |
| A-07 | 🔄状态变化 | `EncounterWaveSelector.cs:16-60` + `SimulationEngine.cs:470-495` | 抽怪框架落地，波次数量/倍率未对齐基准（NEW-A-02） |
| A-08 | 🔄状态变化 | `PlayerAttributeResolver.cs:103` 攻击间隔 `(1-0.75*progress)/(1+speed)` | 终点 0.25s 一致，未按 ATR 独立乘区（NEW-A-03） |
| A-09 | ✅已闭环 | `CombatModels.cs:420-430` 五行二次减免 | 已落地 |
| A-10 | ✅已闭环 | `PlayerAttributeResolver.cs:48-112` + `BaseAttributeCatalog.cs:42-68` | 装备/六维/境界重算接入战斗 |
| A-11 | ⚠️仍存在 | `CombatSkillEngine.cs:114-130` 仅单目标；全仓无 Threat | 威胁/多目标未实现 |
| A-12 | ✅已闭环 | `Domain.csproj:18-21` 嵌入 + `GameDataCatalog.cs:290-291` 解析 | expected-attributes 已成基础属性真源 |
| X-03 | ⚠️仍存在 | `PlayerAggregate.cs:64-70` 100/100 vs `combatConstants.ts:228-229` 100/50 | 前端基准常量未同步 |
| X-05 | ✅已闭环 | `combat-design.md:219` 已统一「周期与数值分离」 | — |

### 2.2 新增偏离

| 编号 | 描述 | 文档位置 | 实现位置 | 性质 | 严重度 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NEW-A-01 | **Lv.1 玩家无法施放任何技能**：`manaCostRatio`（10~40）×10 后需 100~400+ MP，Lv.1 `MaxManapoints=50`，`CombatSkillEngine.cs:119-120` 判定恒 false，全部技能不可用 | `combat-design.md:146-148`、`combat-benchmark-model.md:53-64`、`physical_abilities.json:11,33,55,77,100` | `CombatSkillEngine.cs:119-120` | 数值不符 | **P0** |
| NEW-A-02 | **怪物波次与品阶倍率未对齐基准**：普通波未按 3 只/80% 属性/300% 生命，精英/Boss 未按 120%/500%、150%/2000% 缩放；前端 `NON_PLAYER_UNIT_TIERS` 仍 1.5/10.0 | `combat-benchmark-model.md:29-31`、`expected-attributes.json:27-32` | `EncounterWaveSelector.cs:42-43`、`GameDataCatalog.cs:465-498`、`combatConstants.ts:288-299` | 数值不符 | **P0** |
| NEW-A-03 | 攻速/暴伤/CDR/吟唱二次成长未落地：攻速未按 ATR×专属攻速独立乘区；`CriticalMultiplier` 硬编码 1.5；技能 CD 未应用境界 CDR | `combat-benchmark-model.md:46-48`、`player-expected-attributes-model.md:94-107` | `PlayerAttributeResolver.cs:103`、`CombatModels.cs:68`、`CombatSkillEngine.cs:163-164` | 数值不符 | P1 |
| NEW-A-04 | 快照 v2 战斗属性硬编码：`style/category/element` 固定 sword/physical/none，`criticalDamageRatio` 固定 1.5，`maxDamage` 只取物理攻击，违反「协议快照映射与配表透传」 | `AGENTS.md`（协议快照映射条款）、`master-design.md:87-89` | `ProtocolSnapshotMapper.cs:193-198` | 协议伪造 | P1 |
| NEW-A-05 | 自动普攻不区分配装流派：`AttackPower` 恒取 `PhysicalAttack`，无法术普攻 2s 吟唱分支 | `combat-benchmark-model.md:47-48`、`master-design.md:88-89` | `PlayerAttributeResolver.cs:31-32`、`CombatSkillEngine.cs:141-142` | 功能缺失 | P1 |
| NEW-A-06 | **自然回复周期硬编码 5 秒**（`SimulationEngine.cs:107,140`），与用户澄清「周期待讨论、不得据此实现固定 5 秒」直接冲突 | `combat-design.md:219`、9/14 审计顶部用户澄清 | `SimulationEngine.cs:107-108,140-148` | 数值不符（违反已确认需求） | P1 |

**A 域文档内部矛盾（新）**：`combat-benchmark-model.md:29-31`（普通波 3 只/80%/300%）与 `generic-map-and-encounter-system.md:189-190`（每波最多 5 只、无普通波数量约束）对波次规模口径不一致；实现与后者对齐。

---

## 3. B 域：行动队列、挂机模拟与生活生产

### 3.1 历史条目复审

| 编号 | 状态 | 当前证据 | 备注 |
| :--- | :--- | :--- | :--- |
| B-01 | ✅已闭环 | `SimulationEngine.cs:263-272` 同时校验 `availableHere` 与 `hasInputs`，材料不足置 `Blocked` | 不再写负库存/回滚 |
| B-02 | ⚠️仍存在 | `GameDataCatalog.cs:421-428` 仅解析 `usableInActionTypes/durationSeconds/isContinuous/*Multiplier`；`cooldownSeconds/buffs/hpRegen/mpRegen/hitpointRestore` 零命中 | 立即消耗品 Buff/冷却/即时回复仍无执行链路 |
| B-03 | ◐部分闭环 | `PlayerAggregate.cs:642-648` 配置入口仍只校验长度/数量；`SimulationEngine.cs:1024-1037` 运行时有库存与目录校验 | 配置入口缺所有权校验，运行时兜底 |
| B-04 | ✅已闭环 | `PlayerRuntime.cs:499-501` 入队校验 + `SimulationEngine.cs:261-262` 运行时校验 | 地点双校验落地 |
| B-05 | ✅已闭环 | `SimulationEngine.cs:320-321` × `CultivationMultiplier(:1115-1121)` | 地点倍率入收益 |
| B-06 | ◐部分闭环 | `SimulationEngine.cs:310-327` 普通动作已接 `PrepareSustainedAutoUse`；`:285-287` 战斗、`:291-293` 移动分支仍未接 | 当前配表无战斗持续药，暂无实际回归 |
| B-07 | ✅已闭环 | `SimulationEngine.cs:238-242` 非循环 Blocked 直接 `RemoveAction` | 队首滞留修复 |
| B-08 | ◐部分闭环 | `SimulationEngine.cs:1075` 仍按基础耗时扣减；无 SpeedBonus 模型 | 当前无速度加成，不产生数值错误 |
| B-09 | ⚠️仍存在 | `PlayerAggregate.cs:363` / `PlayerStateDocument.cs:324` 上限 1,000,000 vs `initial-design.md:58` 1..1000 | — |
| B-10 | ⚠️仍存在 | `pills.json:29-36,52-59`（60s CD/30s Buff、12HP/8MP、5%）vs `master-design.md:209-210`（30/60s、2HP/1MP、100%） | 数值/CD/持续期全不符 |
| B-11 | 🔄状态变化 | `cultivation.json:8` qiPerSecond:1 → `SimulationEngine.cs:929-940` 每轮 +10 `Progression.Qi`，不写 `Combat.Manapoints` | 实现已固定为修为语义，但灵力产出缺失（NEW-B-12）<br>**整改回填（TASK-20260915-06）**：已落地通用 `yields` 模型；引气入体现为 `qi 1/秒 + mana 1/轮 + experience 10/轮`，灵力正确入账 `Combat.Manapoints` |
| X-01 | ✅已闭环 | 文档矛盾已解：`master-design.md:208` 已由 10 MP 修正为 1 MP→1.3 MP，与 `sustained-consumable-system.md:136,150`（1/轮）及配置一致 | 随 TASK-20260915-06 整改 |

### 3.2 新增偏离

| 编号 | 描述 | 文档位置 | 实现位置 | 性质 | 严重度 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NEW-B-12 | **通用行动收益模型缺失**：实现把「修炼→qiPerSecond→修为」写死为唯一产出通道；按用户确认口径，行动产出应为配表声明的收益列表（修为/灵力/物品/Buff 等），灵力与 Buff 当前无任何行动产出路径 | `initial-design.md:28`、`sustained-consumable-system.md:136,150`、`master-design.md:208` | `SimulationEngine.cs:929-940`、`GameDataCatalog.cs`（cultivation 仅 qiPerSecond 单字段） | 功能缺失 | **P1**（按澄清后口径重新定级，原 P0）<br>**整改回填（TASK-20260915-06）**：已实现 `ActionYield`/`yields` 通用收益声明模型（qi/mana/hitpoints/experience/item/buff × second/repeat），全量配表迁移，`/catalog/actions` 透传 |
| NEW-B-13 | 装配槽数量未按设计约束：`LoadoutCommand` 允许装备/功法各 16 项，设计为 6 装备部位+4 功法槽位 | `character-core-design.md:103,187` | `LoadoutCommand.cs:31-32,39-69` | 配表不符 | P2 |
| NEW-B-14 | （记录，非偏离）`-1` 无限循环不变量成立；`PlayerStateDocument.cs:321-325` 对非 -1 负值直接丢弃属防御性处理 | AGENTS.md 第 7 节 | `PlayerStateDocument.cs:317-326` | — | — |

---

## 4. C 域：经济坊市与持久化架构

### 4.1 历史条目复审

| 编号 | 状态 | 当前证据 | 备注 |
| :--- | :--- | :--- | :--- |
| C-01 | ✅已闭环 | `MarketRuntime.cs:237` 2% 税；`PostgresMarketRepository.cs:446-457,488` + `PostgresSchema.cs:227` seller_tax 落库；文档 `commercial-persistence-architecture.md:167` 与前端文案一致 | 残留：前端预估 `Math.ceil` 取整口径（NEW-C-02） |
| C-02 | ✅已闭环 | `OrderBook.cs:94,164-184`；`PostgresMarketRepository.cs:411`；schema `:208,212`；维护入口 `MarketRuntime.cs:79,188,287,408-428`；退款同事务 `:289-374` | `initial-design.md:220`「需补充索引」表述过期（索引已建） |
| C-03 | ◐部分闭环 | 主规约已改（`commercial:264`、`domain-data:158`）；残留 `initial-design.md:215`、`performance-baseline.md:79,183` 仍写 revision CAS | — |
| C-04 | ✅已闭环（一致性） | 实现仍零读写，但文档 `commercial:173` 已声明「实例交易尚未实现，仅 DDL 设计」 | 表名笔误见 NEW-C-05 |
| C-05 | ✅已闭环 | 文档 `:169` 改为「领取只保存确认游标」；实现 `PostgresMarketRepository.cs:19-83` | 残留 `:228` 口径张力（NEW-C-10） |
| C-06 | ✅已闭环 | 文档 `:163` 改为「热表普通表+归档分区方案」；`market-ledger-archive.sh:26-48,76-86` | — |
| C-07 | ✅已闭环 | 文档不再规定固定锁顺序；实现租约行锁→订单排序锁→余额排序锁 | — |
| C-08 | ✅已闭环 | `domain-data-architecture.md:198-201` 与 `src/Domain/Market/` 实际一致 | 同文件其他清单仍过期（NEW-E-12） |
| C-09 | ✅已闭环 | `domain-data-architecture.md:57` 已改 economy 权威+余额投影 | — |
| C-10 | ✅已闭环 | 文档 `:161` 与 `PostgresSchema.cs:211-212` 一致 | — |
| C-11 | ⚠️仍存在 | `PersistenceContracts.cs:275` 实为 `IMarketTransactionStore`；`initial-design.md:215,220`、`performance-baseline.md:183` 仍写 `IMarketLedgerStore` | 整段过期 |

### 4.2 新增偏离

| 编号 | 描述 | 文档位置 | 实现位置 | 性质 | 严重度 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NEW-C-01 | **订单到期时间不下发**：`MarketOrderDto` 无 expiresAt，前端 `apiClient.ts:207` 用 `expiresAt: row.createdAt` 顶替，「到期日」实际渲染下单日期（真实到期 +30 天） | `commercial-persistence-architecture.md:167` | `Contracts.cs:358-369`、`MarketDtoMapper.cs:8-27`、`apiClient.ts:207`、`MarketOwnOrders.tsx:58-59` | 协议单边 | **P1** |
| NEW-C-02 | 前端挂单预估税 `Math.ceil(×0.02)` 向上取整，后端不取整——非整数税额时预估偏高 | `commercial:167` | `MarketOrderModal.tsx:123` vs `MarketRuntime.cs:237` | 数值不符（展示） | P2 |
| NEW-C-03 | schema 版本口径全面残留 v1：`README.md:67`、`domain-data-architecture.md:213`、`initial-design.md:7`、`player-data-migration-and-reconciliation.md:3`、`commercial:5,47,598,605`；compose 的 `Storage__SchemaVersion:"1"` 为死配置键 | 上述各处 | `PostgresSchema.cs:6` Version=2 | 文档过期+配置残留 | P2 |
| NEW-C-04 | `identity.audit_events` 行仍写「按月分区」（热表实为普通表），且全 src 无写入方 | `commercial:134` | `PostgresSchema.cs:48-56`；写入 0 命中 | 文档过期+功能缺失 | P2 |
| NEW-C-05 | 文档 `:173` 写 `item_instances`，实为 `economy.player_asset_instances`，与同文 `:150` 自相矛盾 | `commercial:173` vs `:150` | `PostgresSchema.cs:136` | 文档内部错误 | P2 |
| NEW-C-06 | 表清单缺 `game.player_runtime_leases`、`platform.command_receipts` | `commercial:140-143,179-181` | `PostgresSchema.cs:108-114,279-290` | 文档过期 | P2 |
| NEW-C-07 | `platform.compute_jobs` 零生产/消费；文档拓扑画出 ComputeNode 客户端，`src/Server` 对 ComputeNode 零命中；compose `ComputeNode__*` 为死配置 | `commercial:96,181`、`README.md:35,94-102` | `PostgresSchema.cs:292-305`、`PostgresRetentionWorker.cs:47-54` | 功能缺失/文档过期 | P2 |
| NEW-C-08 | 五角色方案仅落地骨架：本地五 NOLOGIN 组已建（`postgres-local-roles.sql:7`），但登录角色创建/GRANT 全手工，生产仍单 `daohua` 账号贯穿全链路 | `commercial:557` | `postgres-local-roles.sql:3-52`、`compose.prod.yaml:91,189`、`market-ledger-archive.sh:23` | 部分落地 | P2 |
| NEW-C-09 | `domain-data-architecture.md` §五仍规约「挂单/撤单/结算必须 YuoCore Actor 消息事务+Saga 补偿」，实现为同进程 MarketRuntime+单事务，且与 commercial §七.1 单事务规约互相冲突 | `domain-data-architecture.md:133-155` | `MarketRuntime.cs:179-327`；`MarketPartitionActor` 0 命中 | 文档过期（兼跨文档矛盾） | P2 |
| NEW-C-10 | 刷盘表 `:228` 仍把「领取」列入「订单/冻结/余额/流水同一事务」行，与 `:169`「领取只保存游标」口径张力 | `commercial:228` vs `:169` | `PostgresMarketRepository.cs:19-83` | 文档残留 | P2 |

**C 域文档内部矛盾（新）**：
- X-C-1：`commercial-persistence-architecture.md` 同文 v1（:5,47,598,605）与 v2（:171）并存。
- X-C-2：同文 `:150` `economy.player_asset_instances` vs `:173` `item_instances`。
- X-C-3：同文 `:169`「领取只保存游标」vs `:228`「领取列入同一事务」。
- 跨文档：`domain-data-architecture.md` §五 Actor/Saga 模式 vs `commercial` §七.1 单事务模式冲突（即 NEW-C-09）。

---

## 5. D 域：网络协议与前端规范

### 5.1 历史条目复审

| 编号 | 状态 | 当前证据 | 备注 |
| :--- | :--- | :--- | :--- |
| D-01 | ◐部分闭环 | `ProtocolSnapshotMapper.cs:181-200` 已下发 28 项；五行抗性仍置 0（`PlayerAttributeResolver.cs:109-111`）且前端渲染 | 不再「9 项恒 0」，元素抗性等仍 0 |
| D-02 | ◐部分闭环 | Buff 下发 7 字段但 `qiGainMultiplier/attackLevelRatio/damageRatio/*Multiplier` 硬编码（`ProtocolSnapshotMapper.cs:156-161`） | 部分倍率无真实来源 |
| D-03 | ◐部分闭环 | 实体数组已对齐（`ProtocolSnapshotMapper.cs:261-319`）；`snapshot.ts:477-496` 仍丢弃预算/怪物/精英/Boss 字段 | 见 NEW-D-02 |
| D-04 | ✅已闭环 | `snapshot.ts:361-370` 仅读真实存在的 6 字段 | — |
| D-05 | ✅已闭环 | `ProtocolSnapshotMapper.cs:168-179` 下发真实 coreAttributes/abilitySlots/equipment | 无 level:1 硬编码 |
| D-06 | ⚠️仍存在 | `LOCALES` 动态扫描但仅 zh-CN（`public/locales/` 仅 template.csv）；`setLocale` 仍早退 | 机制已通，内容缺失 |
| D-07 | ✅已闭环 | `Contracts.cs:397-402` 非空 + `snapshot.ts:622-623` 缺失即抛错 | 旧锚点兜底已删 |
| D-08 | ⚠️仍存在 | `common-components.md:21` 列 `FloatingMenu`；`FloatingPopover.tsx:903-959` 无该导出 | — |
| D-09 | ⬜维持误报 | 人物攻速有 `PlayerAttributeResolver.cs:107` 真实来源 | 装备规则不涉及人物攻速 |
| D-10 | ⚠️仍存在 | `.item-badge-rarity-*` 全仓 0 命中 | 索引过期 |
| D-11 | ⚠️仍存在 | `.trigger-rule-row` 0 命中，实际 `.combat-trigger-editor` 等 | 索引过期 |
| D-12 | ⚠️仍存在 | `.connecting-screen`/`.error-screen` 无样式定义，实际 `.status-screen`（`styles.css:7057`） | — |
| D-13 | ⚠️仍存在 | `AbilityGridPicker` 仅旁注；market/*、CombatEncounterPanel、NearbyPanel、WorldMapPanel 等未登记 | AGENTS.md:148-151 要求登记 |
| D-14 | ⚠️仍存在 | `ItemDisplay.tsx:422,425,485,486`「每秒」vs `insight.zh.ts:83,85,125,127`「每5秒」 | 周期语义未统一 |

### 5.2 新增偏离

| 编号 | 描述 | 文档位置 | 实现位置 | 性质 | 严重度 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NEW-D-01 | **快照 v2 战斗分段未下发技能/冷却状态**：`SectionCodec.cs:17` 注释宣称 v2「加入回复余量、技能状态与已学功法」，但 `ProtocolSnapshotMapper.cs:181-200` 只发 hitpoints/manapoints/attributes；`snapshot.ts:378-405` 未读 skills/冷却字段，`types.ts:375-382,551-555` 已定义无来源 | `SectionCodec.cs:17` | `ProtocolSnapshotMapper.cs:181-200`、`snapshot.ts:378-405` | 协议单边 | **P1** |
| NEW-D-02 | world 分段预算/怪物生态字段前端不解析：服务端下发（`ProtocolSnapshotMapper.cs:313-319`）但 `snapshot.ts:477-496` 全部丢弃，危险度与怪物网格无法显示 | `types.ts:845-852` | `snapshot.ts:477-496` | 协议单边 | P2 |
| NEW-D-03 | `combat.attributes` 硬编码：`style/category/element` 固定 sword/physical/none；`tenacity/penetration/lifeSteal` 等固定 0；`criticalDamageRatio=1.5` 非配表来源 | `AGENTS.md:130` | `ProtocolSnapshotMapper.cs:193-197` | 协议单边/硬编码 | P1 |
| NEW-D-04 | Buff 分片倍率硬编码 1/0（`ProtocolSnapshotMapper.cs:159-161`），`ActiveBuff` 无对应字段 | `PlayerAggregate.cs:96-98` | `ProtocolSnapshotMapper.cs:159-161` | 协议单边/硬编码 | P1 |
| NEW-D-05 | 消耗品槽位 `triggers` 写死空数组（`ProtocolSnapshotMapper.cs:177`） | `PlayerAggregate.cs:131-138` | `ProtocolSnapshotMapper.cs:177` | 功能缺失/硬编码 | P2 |
| NEW-D-06 | 背包物品 `isTradable` 硬编码 `true`（`snapshot.ts:535`），不读任何交易标识 | `AGENTS.md:130` | `snapshot.ts:535` | 硬编码 | P2 |
| NEW-D-07 | `check-frontend-contracts.mjs` 仅 4 项断言（world/lastSimulatedAt/CSV/baseAttributes），未覆盖 WireMessageType、完整 v2 快照字段、组件登记，与「协议信封强对齐与往返门禁」不一致 | `AGENTS.md:136` | `check-frontend-contracts.mjs:1-54` | 测试覆盖缺口 | P2 |
| NEW-D-08 | 公共组件漏登记普遍：market/* 5+ 组件、AbilityGridPicker、CombatEncounterPanel、NearbyPanel、WorldMapPanel、InsightPage、SettingsPage 等 | `AGENTS.md:148-151` | `common-components.md` vs `frontend/src/components/**` | 文档过期 | P2 |
| NEW-D-09 | **动态 i18n 键脱离 template.csv 闭环**：`t(\`...\`)` 模板字符串拼键共 24 处（App.tsx、InsightPage.tsx、ids.ts、format.ts 等），静态 CSV 闭环无法覆盖，存在运行时缺键风险 | `AGENTS.md:147` | frontend/src 24 处模板字符串 | 验证缺口 | P1 |
| NEW-D-10 | 仍无真实外语包：`public/locales/` 仅 template.csv，多语言切换实质不可达 | `common-components.md:174` | `i18n/index.ts:15,184` | 功能缺失 | P2 |

i18n 抽样 16/16 静态键命中 template.csv；动态模板字符串拼键（24 处）不在闭环内。

---

## 6. E 域：持久化、容量与运维 + 根文档

### 6.1 历史条目复审

| 编号 | 状态 | 当前证据 | 备注 |
| :--- | :--- | :--- | :--- |
| E-01 | ✅已闭环 | `performance-baseline.md:10-21` 加「当前重测入口」；`LoadTests/Program.cs:21-28`、`LoadTestOptions.cs:34-58` | 旧命令保留为历史证据，符合口径 |
| E-02 | ✅已闭环 | `deployment.md:103-105`（`daohua:sha-*`+`daohua-compute:sha-*`）vs `docker-build-prod.sh:9-10,78-81` | — |
| E-03 | ✅已闭环 | 文档标注历史端点已删除；`Program.cs` 无 `/api/debug/*` | — |
| E-04 | ◐部分闭环 | 归档 RANGE 分区脚本真实存在；残留：`commercial:134` 仍写热表「按月分区」 | 见 NEW-C-04/NEW-E-09 |
| E-05 | ✅已闭环 | `AGENTS.md:205` 已声明无该配置键 | — |
| E-06 | ✅已闭环 | 文档已改租约+fence；`advisory` 0 命中 | — |
| E-07 | ✅已闭环 | 本地/生产均 PG18（`compose.yaml:5-6`、`compose.prod.yaml:5`） | — |
| E-08 | ✅已闭环 | `xx.yuohira.com.caddy:10-11` 拦截全部指标/debug/readyz 路径；两指标端点 `RequireAuthorization()` | `/healthz` 公开是有意设计（非敏感） |
| E-09 | ✅已闭环 | readyz 挂 postgres+market；schema v2 启动期校验 | 措辞残留见 X-E-03 |
| E-10 | ✅已闭环 | `Program.cs:121,462-474` healthz 输出 version；Dockerfile BUILD_VERSION→OCI→程序集 | — |
| E-11 | ◐部分闭环 | 本地五 NOLOGIN 组+脚本接线；生产仍单一 `daohua` 超级用户贯穿全链路 | 见 NEW-C-08/NEW-E-06 |

### 6.2 新增偏离

| 编号 | 描述 | 文档位置 | 实现位置 | 性质 | 严重度 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NEW-E-01 | **所有文档化 smoke 调用开箱即失败**：默认口令 33 字符过不了脚本自身 `>24` 校验直接 throw；且口令硬编码入库；`deploy-prod.sh:251` 等调用点未注入 `SMOKE_PASSWORD` | `AGENTS.md:100`、`README.md:144,153`、`deployment.md:70,77` | `smoke-production.mjs:99-100`、`deploy-prod.sh:251`、`install-ccr-auth.sh:101`、`dev-service.sh:82,90,105` | 命令失效+硬编码凭据 | **P1** |
| NEW-E-02 | ComputeNode 全链路未接线：文档称「被主服务调用」，实际 `src/Server` 对 ComputeNode/advance-batch/X-Compute-Token 零命中；`compute_jobs` 无生产者；compose `ComputeNode__*` 死配置 | `AGENTS.md:47`、`README.md:15,94,102`、`deployment.md:11`、`commercial:96,118` | `src/ComputeNode/Program.cs:96`；Server 内 0 命中 | 功能缺失 | **P1** |
| NEW-E-03 | `capacity-target` 被描述为「使用正式 Simulation 与 PacketCodec」，实际 LoadTests 无 Domain 引用，冷挂机段是合成 `Mix()` 哈希循环，`PacketCodec` 类型已不存在（现名 WireEnvelope/WireCodec）；存储估算缺 `action_queue` section | `performance-baseline.md:32` | `LoadTests.csproj:15`、`ComputeLoadScenario.cs:49-97`、`CapacityTargetScenario.cs:26-30,211-219` | 文档失实 | **P1** |
| NEW-E-04 | `dev-service.sh` 硬编码旧卷名 `daohua_daohua-postgres`（生产卷名），本地误建孤儿卷；实际本地卷 `daohua-local-postgres18` | `deployment.md:158`、`README.md:53` | `dev-service.sh:24,79,87` vs `compose.yaml:223-225` | 配置不符 | P2 |
| NEW-E-05 | schema v1 残留（同 NEW-C-03）：README/domain-data/commercial 多处 | 同 NEW-C-03 | `PostgresSchema.cs:6` | 文档过期 | P2 |
| NEW-E-06 | 角色命名与商业文档不符（要求 api_rw/worker_rw/archive_rw/metrics_ro/migration_admin，落地 daohua_bootstrap/api/worker/archive/metrics）；生产未建独立登录角色 | `commercial:557` | `postgres-local-roles.sql:7`、`compose.prod.yaml:11,91,189` | 命名/配置不符 | P2 |
| NEW-E-07 | 运维配套声明未落地：OpenTelemetry+OTLP、pgBackRest/COS/S3、60/75/85% 水位保护、「归档失败→readyz 降级」均无实现 | `commercial:75,76,105,345,396,397,603` | `Server.csproj:29`、`base-backup.sh`、`wal-archive.sh`、`Program.cs:99-105` | 功能缺失/文档过期 | P2 |
| NEW-E-08 | 「HTTP、WebSocket 和管理入口都必须经过身份认证」与实现不符：`/api/v1/market/book`、`/api/v1/catalog/*`（7 个）、`/api/v1/meta`、register/login 均公开 | `commercial:553` | `Program.cs:303-324,146-213` | 声明与实现不符 | P2 |
| NEW-E-09 | `identity.audit_events` 死管道：仅 DDL+retention 删除，无任何 INSERT | `commercial:134`、`deployment.md:140` | `PostgresSchema.cs:48-56`、`PostgresRetentionWorker.cs:42-46` | 功能缺失 | P2 |
| NEW-E-10 | 「/healthz 和 /readyz 会返回内容版本与配置指纹」不成立：健康端点仅 status/service/version/checks | `game-data.md:64` | `Program.cs:146-154` vs `:462-474` | 文档过期 | P2 |
| NEW-E-11 | `docs/README.md` 索引不完整：缺 `audits/`、`临时设计讨论/`、根级 5 份分析文档、`battle-effects-gallery.html`；`:42` 保留已删除的 `ui-layout-island-demo.html` | `docs/README.md:3,7-16,42` | 文件系统实际清单 | 文档过期 | P2 |
| NEW-E-12 | `domain-data-architecture.md` 大面积过期：引用已删除类型（DataBudgetResolver/CharacterStatResolver/StateSerializer/SnapshotEnvelope/YuoActor/MarketPartitionActor），目录树列不存在的 Constants/、CombatSimulator 等 | `domain-data-architecture.md:44,70,154-155,164-202` | 实际 `src/Domain/` 仅 19 文件，上述类型 0 命中 | 文档过期 | P2 |
| NEW-E-13 | README HTTP 端点清单不全：漏 abilities/base-attributes/equipment/maps/encounters、`/api/v1/metrics`、market orders POST/DELETE | `README.md:83-88` | `Program.cs:156-213,325-382` | 文档过期 | P2 |
| NEW-E-14 | `Storage__SchemaVersion:"1"` 死配置且值误导（ServerOptions 无绑定，真实版本硬编码 2） | `deployment.md:51`（写 v2） | `compose.yaml:95`、`compose.prod.yaml:93,191` | 配置不符 | P2 |
| NEW-E-15 | 「生产同构容器验证」名不副实：本地 compose app 为 Development 环境，生产为 Production | `AGENTS.md:96`、`README.md:71` | `compose.yaml:91` vs `compose.prod.yaml:89` | 配置不符 | P2 |
| NEW-E-16 | WAL 归档目录权限口径不符：文档要求 root/0600，实际 0700、属主 uid 70（WAL 文件本身确为 0600） | `deployment.md:23` | `deploy-prod.sh:178`、`postgres-wal-archive.sh:37`、`wal-archive.sh:54` | 配置不符 | P2 |
| NEW-E-17 | `AGENTS.md:40` 存在空代码段残留（`daohua` / `` / `DAOHUA`） | `AGENTS.md:40` | — | 文档瑕疵 | P2 |

**E 域文档内部矛盾**：
- X-E-01：`commercial` 同文 v1（:5,47,598,605）vs v2（:171）。
- X-E-02：`README.md:67`「schema v1」 vs `deployment.md:51`「schema v2」。
- X-E-03：`deployment.md:146` 称 readyz 反映 schema 可用性 vs `:174`「schema 启动期校验」（探针不每次重查）。
- X-E-04：`market-ledger-archive.sh:3` 注释「每个月份在同一事务内」vs 实现所有月份单事务（:54-101）。

---

## 6. F 域：世界、地图与配表

### 6.1 历史条目复审

| 编号 | 状态 | 当前证据 | 备注 |
| :--- | :--- | :--- | :--- |
| F-01 | ⚠️仍存在（形态变化） | 文档口径已改「预算归遭遇」（`generic-map:119`、`maps.md:36`），但前端危险度仍无数据来源：`ProtocolSnapshotMapper.cs:307-313` 节点 minBudget 恒 null、monsters 恒 []；`snapshot.ts:477-495` 不解析 | 见 NEW-F-04/NEW-D-02 |
| F-02 | ✅已闭环 | `generic-map:99` 明示「算法教学示例，以 config 为准」 | — |
| F-03 | ✅已闭环 | 同上免责声明；配表名自行成立 | — |
| F-04 | ✅已闭环 | `generic-map:119` + `maps.md:3-4` 八节点权威基准 | — |
| F-05 | ✅已闭环 | `maps.md:13-20` 八组坐标与 `qingyun_mountain.json` 全部一致 | — |
| F-06 | ✅已闭环 | `maps.md:70,84` 明示两旧节点不在当前配置 | — |
| F-07 | ✅已闭环 | `maps.md:22-34` 星陨异境章节，7 节点坐标与配表全一致 | — |
| F-08 | ⬜维持误报 | 同名遭遇跨节点复用符合 `generic-map:113-115` | — |
| F-09 | ✅已闭环 | `StaticGameCatalogMapper.cs:245-251` 已改为透传 `catalog.CombatMonsters[id].Stats`（与权重推导同源） | 复核发现的真实不一致已修复 |
| F-10 | ◐部分闭环 | 双向一致要求已撤回（`maps.md:36`）；但 `node.astral-rift` 仍零 `availableActionIds` | 见 NEW-F-08 |
| X-04 | ⬜维持（非缺陷） | `maps.md:38-40` 已声明历史剧情为未来储备 | — |

### 6.2 新增偏离

| 编号 | 描述 | 文档位置 | 实现位置 | 性质 | 严重度 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NEW-F-01 | **星陨异境整图不可达**：两图无跨图边/传送实体；`WorldGraph.Plan` 跨图无路径；前端 `WorldMapPanel.tsx:380` 只渲染当前 `world.mapId`、无切图入口——T2 全部行动/遭遇/怪物/T2 锻造材料链不可达 | `maps.md:22-34`、`initial-design.md:156` | 两图 edges 均图内（实测）；`WorldModels.cs:169`；`PlayerRuntime.cs:587-619` | 功能缺失/死内容 | **P1** |
| NEW-F-02 | 节点 `encounterIds` 纯展示化，与行动 `combatEncounterId` 双轨脱钩：战斗永远用行动钉死的遭遇（青云全图=encounter.twilight-pass），`encounter.qi-beast`/`encounter.sunset-ridge` 永不进入战斗；节点展示生态与实际战斗怪物不一致 | `generic-map:113,117-124` | `SimulationEngine.cs:419-420,470`；`encounterIds` 仅 `StaticGameCatalogMapper.cs:195-203`、`ProtocolSnapshotMapper.cs:304` 消费 | 配表不一致/功能缺失 | **P1** |
| NEW-F-03 | **怪物 weights 键名与解析器错位成死键**：解析器读 `ad/mr/breakPt`（`GameDataCatalog.cs:441,460,464`），配表实际写 `ap`（5 只法术怪）、`mres`（9 只）、`break`（5 只）；`mp` 与 `woodResistance/fireResistance` 无消费点——法术怪攻击回退 budget×1.0，全体怪物 MR/破招/元素抗性恒 0 | `generic-map:79-85` | `GameDataCatalog.cs:440-468` vs `monsters/*.json` | 配表不一致 | **P1** |
| NEW-F-04 | 「栖息生灵/危险度」数据链路全断：快照 world 段 monsters/eliteEnemies/bossEnemies 恒 []；DTO 发 `enemyId` 但前端类型要 `monsterId`，缺 level/tier/budgetCost/drops | `generic-map:255-288` | `ProtocolSnapshotMapper.cs:311-313`、`snapshot.ts:477-495`、`StaticGameCatalogMapper.cs:235-271` vs `types.ts:769-782`、`WorldMapPanel.tsx:832-843` | 协议单边+命名不符 | **P1** |
| NEW-F-05 | `world.travel` 的 `type:"exploration"` 使其 Kind=Exploration 而非 Travel；Travel 分支对 JSON 载入行动永不命中，赶路被按 exploration 匹配自动使用槽 | — | `world.json:6`、`GameDataCatalog.cs:20,128,413-419`、`SimulationEngine.cs:1105-1113` | 配表不一致 | P2 |
| NEW-F-06 | `currency.spirit-stone` 被 `item.` 前缀过滤静默丢弃，完全不进目录，却是市场货币 | `common-components.md:75` | `materials.json:216-222` vs `GameDataCatalog.cs:276-305`、`PostgresMarketRepository.cs:12` | 配表缺失 | P2 |
| NEW-F-07 | 无消费者/默认值不一致字段：遭遇 `randomSpawns[].strength`、`maxTotalStrength` 解析后零消费；`StaticGameCatalogMapper.cs:189` 复活默认 5000ms vs `GameDataCatalog.cs:489` 领域默认 180000ms（差 36 倍）；mapper `maxSpawnCount` 默认与选择器硬上限 5 不符 | `generic-map:189` | `CombatCatalogModels.cs:22,30`、`EncounterWaveSelector.cs:8-52`、`StaticGameCatalogMapper.cs:183-189` | 配表字段无消费+默认值不一致 | P2 |
| NEW-F-08 | `node.astral-rift`（星陨入口）`availableActionIds` 整体缺失，行动许可列它但节点入口零行动；青云 4 节点与星陨 3 节点同模式未反向登记 | `maps.md:36` | `xingyun_realm.json:8-26`、`combat.json:10-18,28-36` | 配表不一致 | P2 |

**F 域文档内部矛盾（新）**：X-07——`master-design.md:270-283` 仍以「节点三支柱」口径把 `[minBudget,maxBudget]/eliteInterval/bossInterval/monsters/elitePool/bossPool` 描述为 WorldNode 属性；而 `generic-map-and-encounter-system.md:99,113-119,189` 整改后明确「预算属于遭遇、精英/Boss 走 eliteEnemyIds/bossEnemyIds/battlesPerBoss」。两份规约对同一机制给出不同模型归属与字段名。

---

## 7. 跨域同源问题汇总（同一根因多处表现）

| 根因 | 涉及条目 |
| :--- | :--- |
| schema v1→v2 文档口径未同步 | NEW-C-03 = NEW-E-05 = X-E-01/X-E-02（README/initial-design/domain-data/migration/commercial 同文 v1/v2 并存 + compose 死键 `Storage__SchemaVersion:"1"`） |
| ComputeNode 链路未接线 | NEW-C-07 ≈ NEW-E-02（文档拓扑、compute_jobs 死表、compose 死配置） |
| `identity.audit_events` 死管道 | NEW-C-04 = NEW-E-09（仅 DDL+删除，无写入方） |
| 数据库角色拆分半落地 | NEW-C-08 ≈ NEW-E-06（本地五 NOLOGIN 组骨架，生产仍单超级用户，命名与文档不符） |
| 快照 v2 残留伪造/未解析字段 | NEW-D-01/02/03/04/05 ≈ NEW-A-04（ProtocolSnapshotMapper 硬编码 style/element/criticalDamageRatio/Buff 倍率/triggers；snapshot.ts 丢弃 world 预算与怪物生态） |
| 危险度/生态展示断链 | NEW-F-04 = NEW-D-02 = F-01 残留面（DTO 字段名错位 + 前端不解析） |

---

## 8. 与 9/14 审计的闭环对照

| 维度 | 9/14 状态 | 本轮状态 |
| :--- | :--- | :--- |
| 市场四件套（C-01/02/03/04） | 全部缺失 | 税/过期/退款/分区口径落地；实例表按文档声明为「设计预留」，一致性闭环 |
| 行动队列语义（B-01/04/05/07） | 全部缺失 | 全部闭环（Blocked、地点双校验、倍率、出队） |
| 战斗核心（A-01/02/05/09/10/12） | 全部缺失 | 全部闭环（两层减伤、命中/暴击/破拆招、双复活、五行、装备/六维重算） |
| 消耗品执行链（B-02/B-10） | 缺失 | **仍完全缺失**（本轮最高优先级残留） |
| 协议单边（D-01~D-05） | 大面积伪造 | 大幅收敛，但技能/冷却状态、Buff 倍率、world 生态字段仍单边或硬编码 |
| 运维文档（E-01~E-10） | 命令/端点/版本大面积失效 | 基本闭环；仅 E-04 残留措辞 |
| 世界配表（F-01~F-07） | 文档侧全部过期 | 文档侧全部闭环；暴露实现侧新缺口（死图、双轨脱钩、键名错位） |

## 9. 建议整改优先级

**第一批（P0，玩法断点）**
1. NEW-A-01 技能耗蓝 ×10 与 Lv.1 MP=50 脱节（玩家无法施法——先拍板 `manaCostRatio` 语义或配表数值）
2. NEW-A-02 波次数量与品阶倍率对齐 `combat-benchmark-model.md`（需先解决 X-07：两份规约的波次模型冲突）

**第二批（P1，用户可见错误与安全声明）**
- NEW-E-01 smoke 默认口令（33 字符过不了自身校验 + 硬编码入库；改必填 env 并同步所有调用点）
- NEW-C-01 订单到期日显示错误（下发 expiresAt 或删除前端伪造）
- NEW-B-12 通用行动收益模型（按澄清口径：行动配表声明收益列表，支持修为/灵力/物品/Buff 等）
- NEW-F-01 星陨异境可达性（跨图边/传送/切图入口，或明确标注未启用）
- NEW-F-03 怪物 weights 键名对齐（`mres→mr`、`break→breakPt`、`ap` 消费）
- NEW-A-06 回复周期 5s 硬编码与「周期待讨论」澄清冲突（先改文档或先确认周期）
- NEW-D-03/04/09、NEW-E-02/03 协议硬编码、动态 i18n 键、capacity-target 叙事修正

**第三批（P2，文档治理批量）**
- schema v1 残留（README:67、initial-design:7、domain-data:213、migration:3、commercial 同文 v1/v2 并存）
- `initial-design.md:215-220` 整段重写（IMarketLedgerStore/本地账本/revision CAS/索引待补）
- `domain-data-architecture.md` 大面积过期类型与目录树重写
- `docs/README.md` 索引补齐（audits/、临时设计讨论/、根级分析文档；删除 ui-layout-island-demo 条目）
- common-components.md 失效选择器（D-10/11/12）与漏登组件（D-13/NEW-D-08）
- compose 死配置键（`Storage__SchemaVersion`、`ComputeNode__*`）、dev-service.sh 卷名、本地 Development 环境口径

## 10. 审计边界

- 只读核对源码、配表、文档与 Git 历史（HEAD `82f1665`）；未运行测试/构建/容器/压测，未连接生产。
- P0/P1 结论经主审逐行复核源码确认；P2 条目由分域核对产出，行号可信。
- `vendor/YuoCore` 不在本轮范围（另有 TASK-20260915-02 专项分析）。
- `.agents/agents/ops-architect/agent.md:18` 的 revision CAS 残留在正式审计范围外，仅提示。
