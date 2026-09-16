# 文档与实现一致性审计 · 2026-09-16

> 任务：TASK-20260916-01。七路并行只读审计，基于当前工作区实况（含未提交改动）。
> 每条含【文档主张】【实现证据】【类型】【严重度】。类型：文档超前=文档描述了未实现的能力；文档滞后=实现已变文档未更新；描述错误=文档与实现直接矛盾。

## 一、规约与顶层文档（AGENTS.md / README / docs/README）

1. **AGENTS.md §7 命名规约指向已废弃 ECS 架构**：规约列举 `Task*`/`Executor*`/`*Component`/`*System` 类型（`TaskGathering`、`ActionQueueComponent`、`ActionExecutionSystem` 等）在 src/ 全仓 0 命中；实际为 `PlayerAggregate`/`SimulationEngine`/`PlayerRuntime` 聚合模型。ecs-architecture、character-core、domain-data-architecture、logic-development-guide、临时草案中同类描述同样全部过期（`PlayerComponent`/`YuoEntity`/`YuoFiber`/`PlayerShardHost`/`StateSerializer`/`StateMapper`/`GameHost`/`DirtySections` 位掩码均不存在）。【描述错误】【高】

2. **YuoCore 为死依赖**：`Domain.csproj:14`、`Server.csproj:15` 引用 `YuoCore.Ecs`/`Persistence`/`Runtime`，但 src/ 无任何 `using YuoCore` 或 `Entity`/`Fiber` 实际使用；AGENTS §7「跨 Fiber 不得持有 Entity」规则作用于不存在的对象模型。【描述错误】【中】

3. **AGENTS §2 依赖图四条边不实**：`Protocol<-Domain`、`Domain<-LoadTests/ComputeNode/DataTool` 均不存在——Domain.csproj 无 Protocol 引用；LoadTests 仅引用 Protocol+Npgsql；ComputeNode/DataTool 零 ProjectReference。仅 `Server→Domain/Protocol` 成立。【描述错误】【中】

4. **AGENTS §3「客户端本地 UUID、尚未形成商业账号鉴权」已过时**：已实现完整账号鉴权（`/api/v1/auth/register|login|logout|session`，`Program.cs:230-275`；PBKDF2 + HttpOnly Cookie 会话 + auth 限流）。安全口径描述与实现相反。【文档滞后】【高】

5. **主服务→ComputeNode 派发链未实现**：AGENTS §3/README/commercial-persistence §四·§十 描述的「内部 HTTP 批处理接口被主服务调用」在 src/Server 无任何 HttpClient/X-Compute-Token/advance-batch 调用；`platform.compute_jobs` 表仅有 DDL 与 retention 删除、无写入方；compose 注入的 `ComputeNode__Endpoints/Token/Enabled` 为死配置。冷挂机补算实为进程内 `SimulationEngine.AdvanceTo`（`PlayerRuntime.cs:815`）。ComputeNode 服务本身存在但端到端未接线。【文档超前】【高】

6. **AGENTS §2 / README 目录边界清单缺项**：`src/DataTool`、`tests/Server.Tests`、`tools/`（config-editor 完整 Vite 应用 + combat-simulator.html）、`art/`、`deploy/` 未登记。【文档滞后】【中】

7. **未登记脚本 + dev-service.sh 卷名陈旧**：`check-frontend-contracts.mjs`（被 tests/Server.Tests 实际调用）、`config-editor.sh`、`dev.sh`、`dev-hot.mjs`、`dev-service.sh`、`optimize-svg.mjs` 文档未提；`dev-service.sh:24` 硬编码生产卷名 `daohua_daohua-postgres`，与本地卷 `daohua-local-postgres18`（compose.yaml:225）不符，会在本地创建孤儿卷。【文档滞后+配置陈旧】【中】

8. **docs/README.md 自称「唯一入口」但索引不全**：缺 `audits/`、`临时设计讨论/`、`project-analysis-*.md`、`battle-effects-gallery.html`；`:42` 仍保留已删除的 `ui-layout-island-demo.html` 条目；`:78` 描述 maps.md 为「云脊山脉」「WorldCatalog」（实际青云仙山、该类型 0 命中）。【文档滞后】【中】

9. **README 压测命令不可运行**：`--scenario websocket/http-read` 两命令必抛「未知场景」——LoadTests 只剩 `capacity-target`（`LoadTestOptions.cs:55-60`）；performance-baseline.md §重测入口同样失实（其 §0 声称用正式 Simulation/PacketCodec，实为 `ComputeLoadScenario` 合成哈希循环，且 LoadTests 无 Domain 引用，`PacketCodec` 类型不存在，存储合成仅 6 段缺 `action_queue`）。【描述错误】【高】

10. **README/AGENTS 冒烟命令开箱即失败**：`smoke-production.mjs` 默认口令长 33 字符，被自身 `password.length>24` 校验直接 throw（`:99-102`），文档命令未注入 `SMOKE_PASSWORD`。【描述错误】【高】

11. **README「首次建库创建 schema v1」滞后**：`PostgresSchema.Version=2`（`PostgresSchema.cs:9`），建库写 v2、启动校验 v2。commercial-persistence 内 v1/v2 口径自相矛盾；initial-design、domain-data-architecture 亦有 v1 残留。【文档滞后】【中】

12. **`Storage__SchemaVersion: "1"` 为死配置且值滞后**：compose.yaml:95,203、compose.prod.yaml:93,191 均写死，但 `ServerOptions.From` 无该键绑定（版本硬编码）。【描述错误/配置残留】【低】

13. **README catalog 端点枚举不全**：只列 actions/items，实际还有 `/abilities`、`/base-attributes`、`/equipment`、`/maps`、`/encounters`（`Program.cs:157-228`）。【文档滞后】【低】

14. **「生产同构容器验证」名不副实**：根 compose.yaml 以 `ASPNETCORE_ENVIRONMENT: Development` 运行（:91），生产同构实为 `deploy/compose.prod.yaml`。【描述不准确】【低】

15. **verify.sh 未传 `--strict`**：README「最小完整门禁」列 `DataTool validate --strict`，`scripts/verify.sh:35` 调用时未带（差异仅在 manifest.json 缺失时是否报错）。【描述不准确】【低】

16. **AGENTS 细节笔误**：§3 `daohua / `` / DAOHUA` 中间形态为空反引号（应为 `DaoHua`，如程序集名 `DaoHua.ComputeNode`）；§7 提到 `AdvanceBy` 方法全仓不存在（仅 `AdvanceTo`）。【文档笔误】【低】

## 二、后端文档（docs/backend/）

### commercial-persistence-architecture.md

17. **拓扑/故障表 ComputeNode 组件超前**（同第 5 条合并）。【文档超前】【高】

18. **数据库表清单缺两表**：`game.player_runtime_leases`（PostgresSchema.cs:114-120）与 `platform.command_receipts`（:285-296）均有真实读写但未登记。【文档滞后】【中】

19. **`identity.audit_events` 描述失实**：文档称「按月分区」，实际普通表无 PARTITION BY（:54-62）；且除 retention 删除外全仓无写入者——「记录事件」从未发生。【描述错误】【中】

20. **`platform.integration_outbox` 无生产者无下游**：`AppendAsync` 全仓零调用；默认 `UnconfiguredIntegrationEventDispatcher` 只记日志返回 false，OutboxWorker 会把事件 release 回 pending。管道存在但既无水源也无下游。【文档超前】【中】

21. **`game.player_projection` 表无任何读写者**（仅 DDL，:122）。【文档超前】【低】

22. **schema v1/v2 口径自相矛盾**（同第 11 条，文档内 :5,47,598,605 称 v1、:171 称 v2）。【文档滞后】【中】

23. **表名 `item_instances` 错误**：实际 `economy.player_asset_instances`（:142）。【描述错误】【低】

24. **PG 角色清单不符**：文档列 api_rw/worker_rw/archive_rw/metrics_ro/migration_admin，实际 `daohua_bootstrap/api/worker/archive/metrics`（postgres-local-roles.sql:7），无 migration_admin。【描述错误】【低】

25. **WebSocket envelope 字段清单缺 `codec`**（WireEnvelope 36 字节头含 codec）。【描述错误】【低】

26. **PostgresRetentionWorker 清理清单滞后**：漏 `market.settlement_claims`（六类非五类，含 `Storage:SettlementClaimRetentionDays` 键）；且「过期命令回执」实由独立 `CommandReceiptCleanupWorker` 清理，worker 归属不准。【文档滞后】【低】

### domain-data-architecture.md

27. **YuoCore 基座全文过期**（合并第 1 条：`PlayerComponent`/`YuoActor`/`MarketPartitionActor`/Inventory·Stats 切片均不存在；切片实为 `PlayerSectionKind` 七项，资产走 `AssetWallet`+economy 表）。【文档滞后】【高】

28. **市场事务方案与实现相反**：文档严禁「外部多重分布式大锁」、要求 YuoCore Actor 消息事务+Saga；实现恰为 `MarketRuntime` 本地单写者 + PostgreSQL Serializable + `market.partition_leases` fence + 同步提交（MarketRuntime.cs:438,455）。`MarketPartitionActor` 不存在。【描述错误】【高】

29. **配置解析类型名错误 + 加载方式不符**：`DataBudgetResolver`/`CharacterStatResolver`/`AbilityConfig`/`CombatAbilityDefinition`/`ItemConfig` 均不存在；实为 `GameDataCatalog`+`PlayerAttributeResolver`+`ItemDefinition`；且 JSON 编译期内嵌为 EmbeddedResource，运行期 `LoadEmbedded` 读程序集而非「直读磁盘」。【文档滞后】【中】

30. **快照链路类型名错误**：`StateSerializer`/`SnapshotEnvelope`/`StateMapper` 不存在；实为 `SectionCodec` + `ProtocolSnapshotMapper` + `PlayerStateHead`/`PlayerSectionSnapshot`。【文档滞后】【中】

31. **推荐目录拓扑过期**：`Constants/`、`Models/` 不存在；Simulation 下所列文件除 SimulationEngine.cs 外全部不存在。【文档滞后】【中】

### game-data.md

32. **`respawnMilliseconds`/`searchMilliseconds` 描述错误**：文档称 search 默认 20s、respawn 随阶位二次曲线+寻敌速度缩减+夹取；实际 search 默认 5000ms（GameDataCatalog.cs:664），无任何「寻敌速度」属性/曲线/夹取逻辑，字段原样消费。【描述错误】【中】

33. **启动校验强度夸大**：文档称「任意重复 ID、未知引用、非法掉落概率、无效连线都阻止启动」；实际遭遇重复 ID 静默覆盖、地图 List 追加不去重、`dropChance`/`minQuantity`/`maxQuantity` 无范围校验、`eliteEnemyIds`/`bossEnemyIds`/`availableActionIds`/`encounterIds` 未做引用校验。【描述错误】【中】

34. **`/healthz`/`/readyz` 返回配置指纹失实**：健康响应仅 status/service/version/checks（Program.cs:477-490）；指纹在 `/api/v1/meta` 与目录 ETag。【描述错误】【低】

35. **相对链接失效**：`./player-expected-attributes-model.md`、`./combat-benchmark-model.md` 实际在 docs/design/。【描述错误】【低】

### logic-development-guide.md

36. **全文开发基座类型不存在**（合并第 1 条：照该文档编码必然编译失败）。【文档滞后】【高】

37. **切片字段归属错误**：`LoopEnabled` 实在 `Settings` 非 `Queue`；无 `Stats`/`Inventory` 切片，灵石走 economy 钱包。【描述错误】【中】

38. **引气入体收益数值错误**：文档「10s 获 10.0 修为与 10.0 灵力」；实际 qi 1/秒（10s→10）、mana 1/轮（每轮仅 1.0）。【描述错误】【低】

### performance-baseline.md

39. **场景清单与示例命令失实**（合并第 9 条）。【描述错误】【高】

40. **capacity-target 实现描述失实**（合并第 9 条后半）。【描述错误】【中】

### player-data-migration-and-reconciliation.md

41. **内部自相矛盾**：§四.2 称 `PlayerStateReconciler` 「全量生效、无需删库」，同文档第 7 行称「当前代码不注册、不调用」；该类型及 `IPlayerMigrator`/`DataMigrationWorker` 均不存在。【描述错误】【中】

42. **引用已删除模型**：`SnapshotEnvelope`/`player_snapshots`/`GameDataCatalog.Current`/`Queue.AutoUseSlots` 等字段矩阵对应旧模型（有「未来参考」免责，降级）。【文档滞后】【低】

## 三、设计文档 · 战斗与数值（docs/design/ + config/game-data）

43. **怪物「与玩家同构倍率」未实现**：文档称普通 80% 属性+300% HP、精英 120%+500%、Boss 150%+2000%；实现为 `budgetCost×10×weights` 独立推导（GameDataCatalog.cs:617-622），无 rank 缩放；expected-attributes.json `monsterWaveRules` 为不被消费的描述文本。【描述错误】【高】

44. **怪物 `skills` 配表零消费**：配表给怪物配了技能（tier1/2 JSON），领域层不消费——敌方只有普攻（SimulationEngine.cs:388-390），skills 仅透传前端展示。【文档超前】【高】

45. **怪物权重键名错位（死键）**：解析器读 `mr`/`breakPt`/`ad`/顶层 `*Resistance`（GameDataCatalog.cs:637-645），配表写 `mres`/`break`/`ap`/`mp`/`weights.*Resistance`——后果：怪物 MagicResistance 恒 0、BreakPower 恒 0（永不破招）、法系怪 attackPower 漏乘 ap 权重、五行抗性恒 0；`level`/`strength`/`maxTotalStrength`/`rank` 字段零消费（`"tier"` 被解析但配置写 `level`→怪物 ResistanceScale 恒 1）。【描述错误】【高】

46. **动态抗性 K 值 T2 断档**：文档 K=Tier（T2=2.0）；实现 `level<=18?1:min(12,2+ceil((level-18)/12))`（PlayerAttributeResolver.cs:111）——K=2 永远无法产生，整个 T2 按 K=1 结算；T3~T12 映射恰好正确。【描述错误】【中】

47. **成长乘区未实现**：benchmark 文档 CDR 0→75%、ATR 0→50%、暴伤 150%→500%、平A增伤、技能强度 1.0→4.3x、寻怪 5→2.5s——实现 CDR/ATR 对 CD/吟唱完全不生效（cast/cooldown 用配表原值），CriticalMultiplier 恒 1.5，无技能强度乘区，搜寻时长恒为遭遇固定配置（未配→恒 5s）。【文档超前】【高】

48. **专属普攻分支不存在**：文档「物理强化平A 0.25s/200%AD」「法术普攻灵爆受 ATR」；实现普攻为单条曲线，AttackPower 恒取 PhysicalAttack，无法术普攻分支。【文档超前】【中】

49. **威胁（Threat）/嘲讽/多目标未实现**：全仓 Threat 零命中，战斗严格 1v1 逐只续战；装备 `specialMechanics.threatRatio`/`healingBonusRatio`、`ability.taunt` 无结算路径；`allEnemies`/`allAllies` 技能通过校验但只结算单目标（友方仅自身）。【文档超前】【中】

50. **技能耗蓝文档与生效值差 10 倍**：文档表「灵力消耗 10.0 MP」等点数；结算为 `manaCostRatio×10`（CombatSkillEngine.cs:119）——重斩实扣 100 MP；且 Lv1 MaxMP≈60，除掌心雷（40）外 T1 全部技能不可施放。文档公式、配表、基准三方不自洽。【描述错误】【高】

51. **「10 门技能」实际可装配 8 门**：嘲讽/破阵子 `damageRatio=0` 且无 flat，触发 `ratio+flat<=0` 校验必抛，装配入口拒绝（测试已锁定此行为）。【描述错误】【中】

52. **基准技能套件与真实配表全面错位**：benchmark 的 220% CD12s×2/180% 3怪/90% 5怪等与真实技能倍率/CD/目标数/消耗无一吻合（掌心雷除外）。【描述错误】【中】

53. **5 秒回复周期越过文档边界**：文档明确「待讨论周期方案，当前不得据此实现固定 5 秒」；实现硬编码 5s（SimulationEngine.cs:107,140），前端词条已写死「每5秒自然回复」。【描述错误】【中】

54. **expected-attributes 文档「未落地」声明过时**：hpRegen/mpRegen 锚点已配齐并被 PlayerAttributeResolver 消费——文档自称的「尚未消费」已不成立。【文档滞后】【中】

55. **四乘区与六维成长未实现**：文档 `BaseFlat×(1+六维%)×(1+装备%)×(1+Buff%)`；实现仅 3 区 `(value×(1+Root)+flat)×(1+ratios)`，Buff 乘区只覆盖 hpRegen/mpRegen/攻速/伤害吸收；且六维 `PlayerCoreAttributes` 恒为默认 10、无任何成长路径，§4.2 finalExpected 表不可达；六维字段名文档（体魄/经脉/根骨…）与代码（Stamina/Intelligence…）不同。【文档超前】【高】

56. **统一百分比类型未实现 + DamageAbsorptionRatio 无消费**：文档 Linear/NonLinear 双模式+负向软着陆；实现 baseStatRatios 一律线性 `(1+ratios)`，无软着陆；`DamageAbsorptionRatio` 解算了却在 ResolveAttack 无消费——「免伤」Buff 实际不减伤。【文档超前】【中】

57. **波次构成三方不一致**：benchmark/expected-attributes 固定「普通3只、精英1+2、Boss1+2」；实现按预算抽取上限 5 只（EncounterWaveSelector.cs:42），tier1 山门试炼 maxBudget 80<boss 100 → Boss 波只有 1 只；前端 `NON_PLAYER_UNIT_TIERS` HP 倍率又是一套口径。实现与 generic-map 预算模型一致，与 benchmark/前端常量不一致。【描述错误】【中】

58. **benchmark 数值表非引擎产出**：§5-7 效率表由独立 `scripts/generate-combat-benchmark.py` 生成，其模型与 C# 引擎也不一致（怪物按玩家面板百分比、连续回复而非 5s 跳、暴伤成长、cost 公式不同）——当前引擎不具备复现这些表格的机制。【文档超前】【中】

59. **生平计数无实现**：文档 normalKills/eliteKills/bossKills/playersDefeated/cultivationSeconds 等；领域层无对应字段，前端 `combatWins/combatLosses/cultivationCycles` 无服务端来源，`InsightPage` 以 `??0` 兜底。【文档超前】【中】

60. **强化公式口径冲突**：expected-attributes.json 自述 `1.05^EnhancementLevel`；实现 `1+enhancement×0.02` 线性且同时乘 flat 与 ratios（master-design「+2%/级」与实现一致，expected-attributes 公式陈旧）。【描述错误】【中】

61. **`combatSimulationBaseline` 残留块**：expected-attributes.json 每 tier 的旧版数据与文档表格自相矛盾（T2 CDR 10.1 vs 1.4 等），无代码消费。【描述错误】【低】

62. **`CalculateManaCost` 方法不存在**（内联于 CombatSkillEngine.cs:119）；普攻「引导施法 1.0s」实为 t=0 首击无前摇；装备槽命名 weapon/armor/head/legs/boots/trinket vs 文档 Body/Accessory，`two-hand` 为死分支。【描述错误】【低】

63. **复活副作用未文档化**：读条复活除满血满蓝外还清空全部技能冷却/吟唱并重置自动消耗品冷却（SimulationEngine.cs:447-449）。【文档滞后】【低】

64. **止血散/顺气丸数值不符**：master §5.2 称止血散「生命回复+2+100%增幅」、顺气丸「灵力回复+1+100%增幅」（CD 60/30s）；pills.json 实为 hpRegen12+5%减伤（30s/60sCD）、mpRegen8+5%攻速。【描述错误】【中】

65. **死亡期间灵力回复文档冲突**：character-core §3.3 称「灵力保持不变不回复」；实现死亡期间 MP 持续回复（外层循环仅 HP>0 才加血），master §6.4/initial-design 已更新为新口径——character-core 未同步。【文档滞后】【中】

66. **节点生态字段双轨错位**：master §7.1/generic-map 示例把 `monsters`/`minBudget,maxBudget`/`eliteInterval`/`bossInterval` 放节点；实现预算与怪池在 encounters/*.json，节点仅 actionIds/encounterIds/buildings/npcs/cultivationMultiplier。后果：`ProtocolSnapshotMapper.cs:313-319` 从 node 读这些字段恒 null/空数组；`eliteInterval` 无配置字段，代码硬编码 `waveNumber%5`；实战永远走行动钉死的 `combatEncounterId`（全图=twilight-pass），节点 `encounterIds`（qi-beast/sunset-ridge）仅展示用、永不进入战斗。【描述错误】【中】

67. **generic-map 流程图残留旧算法**：failCount<3 重抽+保底 1 只小怪；实现为「可容纳候选筛选」且候选为空直接抛 `DomainException`（无保底）。【描述错误】【低】

68. **技能经验/残卷/等级成长未实现**：文档技能书复用+10000 经验、残卷+500、`DamageRatioLevelBonus` 成长；实现 `LearnAbility` 对已习得直接抛错，`item.fragment.*` 无使用命令（仅接 `item.tome.`），`PlayerAbilitySlot.Level` 存在但结算不读。【文档超前】【中】

69. **离线补算 48h 上限未实现**：全 src 无 MaxOffline/FromHours(48)/目标截断；`AdvanceTo` 按 UtcNow 全量推进（且设计文档本身未声明该约束，仅任务/指南文档提及）。【文档超前】【中】

70. **CanStartAction 前置校验缺口**：文档含 `Hitpoints>0` 与灵力门槛；实现入队仅校验节点与材料，0HP 在战斗生成时才 Blocked，无灵力门槛字段。【文档超前】【中】

71. **突破瓶颈/心魔/寿元未实现**：`Breakthrough()` 仅 RealmLevel+1 清零修为经验，无门槛校验、无瓶颈状态、无 Lifespan/Age 字段。【文档超前】【中】

72. **聚合内 Inventory/Spirit 已外移**：character-core 将物品字典与灵石列为聚合组件；实现走 `AssetWallet`+`PostgresEconomyRepository`，聚合七分段无 Inventory/Stats。【文档滞后】【中】

73. **BattleSnapshot/秘境/PvP 未实现**（0 命中；当前为 `CombatExpeditionState`）。【文档超前】【低】

74. **持续消耗品字段名不符 + 触发器未实现**：文档 `ConsumableType.Continuous`/`Stackable`/`Triggers`/`CultivationEfficiencyMultiplier`；实现 `isContinuous`/`cultivationMultiplier` 等，无 Stackable/Triggers（协议 `triggers` 硬编码空数组）。`ActualDuration=Base/(1+SpeedBonus)` 无对应实现（恒扣基础耗时，当前无速度加成故无数值误差）。离线批处理为逐秒切片而非文档公式（结果等价）。【字段名不一致+意图未实现】【低】

75. **自动使用槽边界不一致**：前端 4 类×3 槽；服务端 `slotIndex 0..15` 可写但 4~15 不入前端展示。【边界不一致】【低】

76. **无限循环上限文档过期**：initial-design「1..1000」；实现 `1_000_000`。【文档滞后】【低】

77. **initial-design 多处历史残留**：「死亡不结算失败、立即回到同一波」与实际（结算失败→重生→波次重置为 1）相反；§6 delta 区段/事件缓冲/端点名过期；§9「IMarketLedgerStore 内存账本、鉴权/数据库未完成」已过期（实为 `IMarketTransactionStore`+PG+Cookie 鉴权）；「采集草药→3 灵草」实为凝血草。【文档滞后】【中】

## 四、前端 / i18n / 协议投影

78. **WorldMapPanel 面板框架文本硬编码**：`资源/怪物/精/魁/地点/前往该地点/秒` 等约 10 处绕过 i18n；对应 `combat.map.*` 16 个词典键全部死键（词典先行收拢、组件未接线）。违反 common-components §12 与 i18n 闭环红线。【高】

79. **NearbyPanel「栖息生灵/危险度」端到端恒空**：服务端从 node 读不存在的字段→恒 null/空数组；前端 snapshot.ts:477-496 不解码这 7 个字段；NearbyPanel 消费端永远渲染 noMonsters——死 UI+死 wire 字段。附带 `combat.nearby.npcsHeading` 等 3 键死键（面板无 NPC 区块）。【高】

80. **目录 API 怪物类型漂移**：服务端 `ResolveMonster` 输出 `enemyId`/`visual{}`/`rank` 等；前端 `NearbyMonsterDefinition` 声明 `monsterId`（必填）/`level`/`tier`/`budgetCost`/`drops`/`visualSource`——`key={monster.monsterId}` 恒 undefined，多字段注定取空。【高】

81. **改名功能三层口径互斥**：文档宣称 2~12 字符+正则+查重+24h 冷却；UI maxLength=12 vs validate 1~64 自相矛盾，`app.rename.hint` 承诺查重与冷却；服务端仅校验非空+≤64，`renameCooldownSeconds` 前端硬编码 0→冷却徽章 UI 不可达+2 死键。【高】

82. **市场 DTO 字段伪造**：`expiresAt` 用 `createdAt` 冒充（领域/表列真实存在但 wire 无该字段）→「{date} 到期」展示的是创建时间；`tradedQuantity/tradedGrossAmount` 硬填 0→「累计成交 0」恒显。【中】

83. **系统性 i18n 硬编码**：App.tsx（aria-label、'妖兽'兜底、'功法/物品'、buff 描述 5 处——对应 `app.buff.*` 键已存在且闲置）、actionCalculations.ts（sourceName/tag/description 多处）、format.ts（DAMAGE_CATEGORY_NAMES 与 `combat.encounter.*` 重复定义）、ItemDisplay.tsx（恢复/冷却/STAT label 字典——`market.effect.*` 4 键闲置）、GameLogPanel、SettingsPage、ErrorBoundary、FloatingPopover 默认标题等约 20+ 处。【中】

84. **i18n 死键约 45 个（抽样 190 键≈24%）**：combat.map.* 16、common.* 6、app.buff.* 5、combat.nearby.* 4、combat.log.* 4、market.effect.* 7 等；另有跨域重复键 `common.repeatsOnce/repeatsCount` vs `app.queue.*` 值完全相同，违反 common-components §12。附注：`t()` 签名含 `(string & {})`，TS 无法兜底键存在性，缺键静默回退显示键名。【中】

85. **common-components.md 登记簿不一致**：漏登 `AbilityGridPicker`（两处复用）与 `CatalogPicker` 子导出 `PickerTrigger`/`ChoiceMenu`；§10 登记 `useSmoothProgress` 实为 `useSmoothProgressRef`；`CombatTriggerEditor` 重复登记（§9+§22）；`floating-popover.md` 将不存在的 `FloatingMenu` 列为独立组件（实际仅 Item/Group/Divider）；`FloatingInfoCardProps.asideCards` 为未使用的误导性死 prop；历史审计指出的 3 处失效选择器仍在（:104,213,281）。【中】

86. **快照/UI 伪造默认值**：`isPendingBreakthrough:false`、`renameCooldownSeconds:0` 硬编码（服务端无字段）；`combatDropRate/combatDropQuantity/combatRareFind/searchSpeedRatio` wire 不存在→CombatArena 四行恒显 0。与 AGENTS「不得凭空填默认值」红线存在张力。【低】

87. **协议 WireMessageType 1-17 前后端一致**（正向确认项，非问题）；`combat.*`/`market.*` 等域键接线良好。【一致】

## 五、运维部署（docs/ops + Dockerfile/compose/deploy/scripts）

88. **migrations/ 目录与「无迁移」口径冲突**：deployment.md 三处+README 称「不运行历史 migration」，仓库已有 `migrations/0001_v1_to_v2.sql` 且 AGENTS §3 已列为唯一例外——deployment/README 未同步。【文档滞后】【中】

89. **`/api/debug/*` 规则悬空**：src/ 无任何 debug 端点（含 Development 分支），AGENTS「只能在 Development 存在」与 deployment 的 Caddy 拦截依赖作用于空集（Caddy 拦截本身已配置）。【文档超前（悬空规则）】【低】

90. **WAL_ARCHIVE_DIR 权限描述不准**：文档「目录 root/0600」；实际目录 `install -d -m 0700 -o 70 -g 70`（postgres 属主），0600 是目录内 WAL 文件权限。【描述错误】【低】

91. **`/readyz` 描述两处自相矛盾**：deployment.md:146 称反映「PostgreSQL、schema、市场租约」，:174 写法正确；实际仅 SELECT 1+租约检查，schema 版本只在启动期校验。【描述错误】【低】

92. **更新顺序/预热描述与实际相反**：文档「拉取候选镜像验证健康后才替换」「预热失败保持当前实例」；实际 `compose up --force-recreate` 先替换再 wait_healthy/wait_ready，靠 rollback 标签回滚；deploy-prod.sh 全程只 curl /readyz 未 curl /healthz；失败记录的是 `app_id|compute_id` marker 非单镜像 ID。【描述错误】【低】

93. **`X-Compute-Token` 非唯一凭证**：文档/AGENTS 称只接受该头；实际 `TokenValidator.cs:23-27` 还接受 `Authorization: Bearer` 回退。【描述错误】【低】

## 六、世界观与草案（docs/world + docs/临时设计讨论）

94. **NPC 文档有配表无**：lore/maps 将脚店掌柜/沈娘/林伯/守山弟子/静修道人/灵市牙人标注「现有 NPC」；两个地图 JSON 节点均无 `npcs` 数组（投影通道存在但无数据源）。【中】

95. **「炁/气」术语分歧**：世界文档统一用「炁」（引炁入体/引炁丹/灵草）；配表仍为「引气入体/引气丹/凝血草」（文档已自注为待执行改名建议）。【低】

96. **星陨异境不可达→T2 全链死内容**：maps.md 称星陨异境为已存在地图且不得当待新增处理；但两图 edges 全部图内、无跨图边/传送实体，`WorldGraph.Plan` 跨图直接 NotFound、前端无切图入口——T2 行动/遭遇/怪物/锻造材料链全部不可达。【高】

97. **节点 encounterIds 双轨脱钩**（合并第 66 条战斗侧后果）。【高】

98. **`node.astral-rift` 无 `availableActionIds`**：`combat.sweep-twilight-valley` 的 availableNodeIds 覆盖 7 节点但节点侧未反向登记入口。【低】

99. **世界文档覆盖缺口**：配表 12 种怪物/6 种消耗品/28 件装备/T2 锻造链在世界四篇中均无对应描述（仅泛称）；story.md 七章主线对应节点/任务/物品零落地（已标注草案性质，属治理项）。【治理项】【低】

100. **临时草案时效状态**：`任务队列系统与组件设计草案` 自述未进入实现，但其「当前实现」小节（PlayerShardHost/StateSerializer/cap 缺陷形态）已与代码脱节；`全量物品重构与T1-T2设计草案` 大部分已落地（19 原料/6 加工/28 装备/7-5-1-2 配方规则），唯引气丹配方（精华+灵核）被定稿（凝血草×3）取代——建议分别标注「已落地归档」「设计推演记录」。【治理项】【低】

## 七、历史审计复核摘要（docs/audits/20260915）

- 上次报告 135 项中：**仍存在 85 项**（含本报告第 43-53、55-57、66、78-86、88-93、96 等条目的前身），**已修复 44 项**（含 B-02 即时消耗品链路、NEW-B-12 yields 模型），**已过时/误报 6 项**。
- 本次新增确认项：改名三层口径（#81）、市场 DTO 伪造（#82）、怪物权重死键（#45）、K 值断档（#46）、波次三方不一致（#57）、星陨不可达（#96）等。

## 统计

| 严重度 | 条数（去重后） |
| --- | --- |
| 高 | 20 |
| 中 | 43 |
| 低/治理 | 37 |
| **合计** | **100** |

> 注：多条目为跨域合并计数；同一根因（如 ECS 基座过期横跨 5 份文档）按影响面拆分统计。
