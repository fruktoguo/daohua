# 文档与实现一致性审计复核

- 任务：TASK-20260914-07。
- 基线：2026-09-14，HEAD `7f315fd31563738e603c4acbe2609ab8d3f231ae` 加现有未提交工作区。
- 原报告：[doc-vs-implementation-20260914.md](doc-vs-implementation-20260914.md)。
- 方法：逐项读取文档、代码和调用点；A/B、C、D/F 三个只读子任务并行，主任务核对 E、关键反例及报告统计。
- 判定：**成立**表示差异有证据，不等于必须按文档改代码；**部分成立**表示需修正范围、因果、数值或处置；**误报**表示所述矛盾不成立。
- 边界：未连接生产服务器、未检查线上数据库角色、未运行游戏或故障注入；本报告不证明生产故障正在发生，也不证明实际数据损失。
- 复核完成时间：2026-09-14 20:13:12。
- 69个唯一编号完整覆盖：成立40项、部分成立26项、误报3项（D-09、F-08、F-09）；“部分成立”不是已修复，“误报”也不代表周边实现没有其他缺陷。

## 后续澄清：自然回复的配置含义

以下为 2026-09-14 用户确认的需求，优先于本报告 A-04、D-14、X-02、X-05 的历史口径分析，不改变原审计时的代码证据。

- 基础生命回复点数由随境界的基础属性表配置。“2%”仅用于配表选值，参照对应境界的基础生命上限；例如基础生命上限 1,000，对应基础回复配置 20 点。
- 该数值调整通过基础属性配置文件实现，代码读取配置的回复属性，不硬编码 `基础生命上限 × 2%`，也不根据最终生命上限重新计算。装备提高最终生命上限不会自动提高这部分基础回复。
- 生命与灵力分别读取各自配置的回复属性；本次没有确认灵力也按 2% 配表，也没有新增保底 1 点规则。
- “每五秒”周期仍待讨论。原报告及复核中的五秒/十秒换算仅记录旧文档表述，不能据此确定结算周期、协议字段或实现方案。

## 重要纠正

1. `/readyz` 已检查市场运行时就绪状态，schema 版本在启动时校验。原 E-09 的“只做 SELECT 1”漏读了宿主注册与启动链。
2. 装备不提供攻击间隔属性不等于人物不能有攻击间隔。D-09 应撤销，不能用移除装备属性的提交作为取消人物攻速成长的依据。
3. Caddy 模板漏拦 `/api/v1/metrics` 和 `/api/v1/market/metrics` 属实，但两者已要求身份认证，不能描述为未认证公开接口。
4. 热表不是分区表属实，但仓库已有 RANGE 归档分区脚本。不能声称完全没有按月分区能力。
5. 预算推导怪物属性本身不是配表缺失；需要核对领域计算与静态目录映射是否一致。未被消费的前端常量不能代表服务端怪物倍率。
6. 修炼收益写入 `Progression.Qi/LifetimeQi`，不是战斗 `Combat.Manapoints`。原报告混淆了资源含义。
7. 原报告列出的 `npm ci` 禁令不在当前 AGENTS.md 中；当前规则明确提供 `npm ci` 和 `scripts/verify.sh`。本轮不执行完整门禁的原因是只读审计，而非所谓项目红线。

## A：战斗与数值

| 编号 | 判定 | 当前证据与修正 |
| --- | --- | --- |
| A-01 | 成立 | `src/Domain/Combat/CombatModels.cs:300-302` 为单层双曲减伤，与 `docs/design/combat-design.md:132-140` 的平方伤害和五行二次减伤不同。 |
| A-02 | 部分成立 | 已有 Accuracy/Evasion 阈值比较，缺少文档的概率命中、暴击、破拆招；不能写命中闪避全部零实现。证据同 A-01，规约 `combat-design.md:153-164`。 |
| A-03 | 成立 | `src/Domain/GameData/GameDataCatalog.cs:264-292` 不解析 ability；`CombatModels.cs:276-284` 只有普攻，未发现领域耗蓝执行链。 |
| A-04 | 成立 | `src/Domain/Simulation/SimulationEngine.cs:104,359` 只推进行动并写战斗掉血；`CombatModels.cs:264-287` 只有攻击时钟，没有回复执行。回复周期的具体冲突见下文 X 复核。 |
| A-05 | 成立 | `SimulationEngine.cs:310-318,385-393` 战败/死亡进入 Blocked，无复活阶段；`CombatCatalogModels.cs:33` PlayerRespawnSeconds 无消费。 |
| A-06 | 部分成立 | 前端 NON_PLAYER_UNIT_TIERS 与文档数值不同，但全前端只有定义无消费。实际 `GameDataCatalog.cs:423-434` 按预算和权重计算，不读 rank；不能把前端常量当真实倍率。 |
| A-07 | 成立 | `SimulationEngine.cs:465-474` 加权选一只；`GameDataCatalog.cs:467-471` 预算与精英/Boss字段只解析，没有对应推进。 |
| A-08 | 成立 | `SimulationEngine.cs:307,342` 玩家固定2秒，怪物 `GameDataCatalog.cs:432` 默认2秒，与基准文档1到0.25秒不同。移除装备攻击间隔不否定角色攻速成长。 |
| A-09 | 成立 | `CombatModels.cs:29-36` 无五行抗性；DamageKind 进入事件但伤害公式没有按物理/法术分抗。 |
| A-10 | 部分成立 | `src/Domain/Player/PlayerAggregate.cs:595-601` 装备只改 Loadout，没有属性重算；新玩家攻防默认10，但 `PlayerStateDocument.cs:277-278` 可以从状态恢复其他值，“恒10”不严谨。 |
| A-11 | 成立 | `CombatModels.cs:268-284` 固定 Player/Enemy 两目标，src 无 Threat/Aggro/仇恨执行。 |
| A-12 | 部分成立 | expected-attributes 没有作为数值模型消费，但 `src/Domain/Domain.csproj:18` 嵌入 JSON，`GameDataCatalog.cs:303-315` 加载资源并参与目录处理；“零引用”应改为“未解析消费为属性真源”。 |

## B：行动与生产

| 编号 | 判定 | 当前证据与修正 |
| --- | --- | --- |
| B-01 | 成立 | `SimulationEngine.cs:788-790` 无材料前置检查；`PlayerRuntime.cs:714-729` 提交整段模拟资产，`PostgresEconomyRepository.cs:75-78` 拒绝负余额，`PlayerRuntime.cs:765-771` 恢复模拟。队列语义错误属实，但并非已经写入负库存。 |
| B-02 | 成立 | `GameDataCatalog.cs:367-385` 未解析 instant/buff/CD/回复执行所需语义；无领域 Buff 创建执行链。 |
| B-03 | 部分成立 | `PlayerRuntime.cs:474-482` 到 `PlayerAggregate.cs:607-614` 配置入口只作基础校验；但 `SimulationEngine.cs:848-850,910-915` 持续物品实际使用检查库存、目录、类型，不能称全程无保护。 |
| B-04 | 成立 | `PlayerRuntime.cs:424-430` 入队只检目录，AllowedNodeIds 在开始和入队没有位置限制消费。 |
| B-05 | 成立 | 地点倍率仅经 `StaticGameCatalogMapper.cs:211`、`ProtocolSnapshotMapper.cs:260` 投影；`SimulationEngine.cs:744,776` 收益只乘自动槽倍率。配置存在非1地点倍率。 |
| B-06 | 部分成立 | `SimulationEngine.cs:135-147` 绕过 `:165` 自动槽属实，但当前持续物品无战斗/移动适用项，战斗丹药为 instant，GetMultiplier 对 Combat/Travel 默认1（`GameDataCatalog.cs:63-68`）。不能只移动调用顺序解决，也不能说已有战斗持续药发生回归。 |
| B-07 | 成立 | `SimulationEngine.cs:109-122` 非循环 Blocked 直接 break 留在队首，与 `initial-design.md:59` 移除规则不同。 |
| B-08 | 部分成立 | SpeedBonus/ActualDuration 模型未落地；当前 `SimulationEngine.cs:149,174-179,888` 实际耗时等于基础耗时，扣基础时间没有违反现有实际时长。不能说已有加速效果却扣错。 |
| B-09 | 成立 | `PlayerAggregate.cs:335` 上限百万，`initial-design.md:58` 上限千。 |
| B-10 | 成立 | `config/game-data/items/pills.json:29,34-36,52,57-59` 为60秒CD/30秒Buff、12HP/8MP及5%其他属性；`master-design.md:209-210` 为30秒CD/60秒持续、2HP/1MP及100%回复。连CD和持续期也不同，不能称已验证可玩。 |
| B-11 | 部分成立 | 文档1与10冲突属实；`initial-design.md:28,53` 是1，master第208行是10MP。实际 `SimulationEngine.cs:751-752` 每轮增加10 Progression.Qi/LifetimeQi，不增加 Combat.Manapoints，应先修正资源语义描述。 |

## C：市场与经济

| 编号 | 判定 | 当前证据与修正 |
| --- | --- | --- |
| C-01 | 成立 | `src/Server/Clean/MarketRuntime.cs:225-236` 传税额 0；`PostgresMarketRepository.cs:429-441` 按 gross-SellerTax 入账。2% 证据在 `performance-baseline.md:166` 和 `market.zh.ts:78`，commercial 文档第165行只提手续费。 |
| C-02 | 成立 | `src/Domain/Market/OrderBook.cs:84-95` 新订单到期时间为 null，`MarketRuntime.cs:442-456` 恢复同样如此；`OrderBook.cs:164` 的 Expire 无调用，schema 无到期列。原新建路径行号错误。 |
| C-03 | 部分成立 | 订单行锁和版本自增见 `PostgresMarketRepository.cs:480,493,512-518`，确实没有 expected-version CAS；但 `:203,362-372` 有 Serializable 事务及租约行锁/fence，不能据此判定并发不安全，也不能反向声称所有并发场景都安全。 |
| C-04 | 成立 | 实例表只有 `PostgresSchema.cs:136,145,175` DDL/引用；`PostgresMarketRepository.cs:224-227` 的 reservation instance_id 恒 NULL。是未落地的实例能力，是否必做取决于唯一装备交易要求。 |
| C-05 | 部分成立 | `PostgresMarketRepository.cs:19-83` 领取保存确认游标，但 `:426-447` 成交已同事务更新冻结、双方资产和流水；没有证据证明漏发钱或缺失结算原子性。 |
| C-06 | 部分成立 | `PostgresSchema.cs:214-229` 热表普通表与文档不符；`deploy/market-ledger-archive.sh:26-48,76-77` 已有归档 RANGE 月分区。 |
| C-07 | 部分成立 | `commercial-persistence-architecture.md:256` 锁顺序与 `PostgresMarketRepository.cs:204,206,221,241,434,518` 不同，但 `:367` 租约行锁先串行同分片写入；未证明死锁，不能当作已复现并发 Bug。 |
| C-08 | 成立 | `domain-data-architecture.md:199-203` 的 MarketEngine/MarketPricePolicy/MarketCoordinator 文件已不存在；当前市场目录为 MarketModels/OrderBook/MarketPartitioner。 |
| C-09 | 成立 | `domain-data-architecture.md:57` Stats 切片持灵石过期；`PlayerAggregate.cs:274-280` 为当前七个切片，`PostgresAssetRepository.cs:14-17` 从 economy 余额表读取。运行时仍有余额投影。 |
| C-10 | 成立 | 文档 `commercial-persistence-architecture.md:161` 索引与 `PostgresSchema.cs:210` 的 sequence 等字段不同；只证明文档过期，不证明现有索引性能有问题。 |
| C-11 | 成立 | `initial-design.md:215,220` 旧接口名失效；当前 `PersistenceContracts.cs:273` 为 IMarketTransactionStore，`PostgresMarketRepository.cs:10` 实现它。 |

## D：协议与前端

| 编号 | 判定 | 当前证据与修正 |
| --- | --- | --- |
| D-01 | 部分成立 | `src/Domain/Player/PlayerStateDocument.cs:47` 仅六字段；`frontend/src/network/snapshot.ts:387` 补大量常量。`CombatArena.tsx:1309` 主要零值是命中、闪避、六种抗性共八项，原九项口径不明。更直接的问题是快照投影攻击间隔1秒，而 `SimulationEngine.cs:342` 实际2秒。 |
| D-02 | 成立 | `PlayerAggregate.cs:92` ActiveBuff 仅 ID/到期/层数；`snapshot.ts:467` 却读名称、倍率、来源、modifiers 并补默认值。 |
| D-03 | 成立 | `src/Server/Clean/ProtocolSnapshotMapper.cs:261` 下发行动、遭遇、NPC、建筑，`snapshot.ts:452` 丢为空数组；`gameStore.ts:327,382` 未从目录补回。 |
| D-04 | 成立 | `PlayerStateDocument.cs:29` 只有六个 progression 字段；`snapshot.ts:364` 所读十个统计字段中仅 completedActions 有来源，其余九个缺失。 |
| D-05 | 部分成立 | `snapshot.ts:425` 六维1、功法level=1/triggers=[] 属实；但 `CombatArena.tsx:1302` 六维显示至少10，不能说面板恒1。 |
| D-06 | 成立 | `common-components.md:174` 声称放外语CSV即可扩展；`i18n/index.ts:13,143,171` 仅允许 zh-CN，其他语言加载前被拒绝。 |
| D-07 | 成立 | `src/Protocol/Contracts.cs:297` 要求增量时刻；`snapshot.ts:582` 明确为“服务端未升级”沿用旧锚点，是当前禁止的历史兼容路径。不能仅改文档就视为消除规约问题。 |
| D-08 | 成立 | `common-components.md:21` 列 FloatingMenu；`FloatingPopover.tsx:904,913,943,947` 只有 Item/Divider/Group，没有该导出。 |
| D-09 | 误报 | `common-components.md:16` 限定装备映射与悬浮展示，人物攻击间隔展示不违反它。 |
| D-10 | 成立 | `common-components.md:104` 指路的 `.item-badge-rarity-*` 无定义，为索引过期。 |
| D-11 | 成立 | `common-components.md:281` 的 `.trigger-rule-row` 无定义，为索引过期。 |
| D-12 | 成立 | `common-components.md:213` 两个状态样式名无定义，实际有 `styles.css:7057` 的 `.status-screen`；不能扩张为整页无样式。 |
| D-13 | 部分成立 | `AbilityGridPicker.tsx:9` 对应公共选择器未登记路径；但五个市场业务子组件不必然属于公共模板，不能一概判违规。 |
| D-14 | 部分成立 | `ItemDisplay.tsx:422,425,485,486` 写丹药每秒，`insight.zh.ts:83,85` 写自然回复每5秒；需明确 Buff 与自然回复的同属性语义后统一，不能只凭两种标签直接选择整改周期。 |

## F：世界与配表

| 编号 | 判定 | 当前证据与修正 |
| --- | --- | --- |
| F-01 | 部分成立 | `NearbyPanel.tsx:88` 危险度无数据来源，`snapshot.ts:438` 不保留预算；但 `generic-map-and-encounter-system.md:110-113` 明确预算归 encounters，与自身节点示例冲突。不能直接要求 maps 新增字段。 |
| F-02 | 部分成立 | `tier1_minions.json:9,37` 预算10/15，`tier1_elites_bosses.json:67` 为45，数字差异存在；文档 `:219-232` 为算法示例，未确立其为现行配表真源。 |
| F-03 | 部分成立 | `tier1_elites_bosses.json:4,60,127` 与文档代表/示例名称不同；属文档用词漂移，不是玩法能力缺失。 |
| F-04 | 部分成立 | 节点ID示例与现行配置不同；`docs/world/maps.md:3-4` 已指定当前八节点基准和未来储备，不应直接补齐旧示例节点。 |
| F-05 | 成立 | `maps.md:15-18` 四组当前主线坐标与 `config/game-data/maps/qingyun_mountain.json:65,87,109,131` 不同。 |
| F-06 | 部分成立 | `maps.md:52,66` 称两旧节点已存在，但与其当前八节点基准矛盾；是旧事实未清理，不能据此判配表必须新增节点。 |
| F-07 | 成立 | `config/game-data/maps/xingyun_realm.json:3-4` 存在第二张地图，design/world 未覆盖该ID或名称。 |
| F-08 | 误报 | 同名遭遇跨七节点复用，符合 `generic-map-and-encounter-system.md:113` 可跨节点复用的约定；同名不能证明挂载错位。 |
| F-09 | 误报 | 文档 `generic-map-and-encounter-system.md:67-85` 本就采用预算/权重，缺显式属性不是缺配表。另有真实缺陷：`GameDataCatalog.cs:429-434` 战斗按权重推导，`StaticGameCatalogMapper.cs:245-251` 忽略权重。魔化野兔实际HP115/攻3/甲0.65，静态目录100/10/0（`tier1_minions.json:9-15`）。应修正映射一致性，不应凭此新增冗余数值字段。 |
| F-10 | 部分成立 | `combat.json:28-35` 七节点许可与节点反向列表不完全相同；文档 `:111` 允许行动与遭遇分开挂载，应先明确谁是权威。原报告把 tea-house 归入星陨错误，它在 `qingyun_mountain.json:128`，不在该战斗许可中。 |

## E：持久化与运维

| 编号 | 判定 | 当前证据与修正 |
| --- | --- | --- |
| E-01 | 成立 | `docs/backend/performance-baseline.md:38,74,97,117,163,178,191` 的七个场景不在 `src/LoadTests/Program.cs:21` 和 `LoadTestOptions.cs:34` 的支持集合。该文档首部注明 2026-08-06 历史样本，应标注旧命令不可重跑，不应抹去历史测量结果。 |
| E-02 | 成立 | `docs/ops/deployment.md:105` 写 `daohua:compute-sha-*`；`scripts/docker-build-prod.sh:10,77,80` 默认产物为独立仓库 `daohua-compute:<release-tag>`。 |
| E-03 | 部分成立 | `src/Server` 当前无该 debug 端点；`performance-baseline.md:145` 是历史测试叙述，不能据此要求恢复端点。应区分历史证据与当前可运行命令。 |
| E-04 | 部分成立 | `PostgresSchema.cs:48` 的 `identity.audit_events` 是普通热表；但 `deploy/market-ledger-archive.sh:41,48,77` 创建 RANGE 归档父表及月分区。原“全仓无 PARTITION BY”错误。 |
| E-05 | 成立 | `AGENTS.md:205` 的 `Runtime:PlayerShardCount` 在当前 `src/config/deploy` 无实现；`src/Server/Clean/ServerOptions.cs:7,43` 未声明或绑定该键。 |
| E-06 | 成立 | 当前源码无 advisory lock 调用，市场通过 `market.partition_leases` 与 fence 工作。`MarketRuntime.cs:49,70,384` 明确获取及续租；规约的锁机制描述失实，不表示缺少单写者保护。 |
| E-07 | 成立 | `compose.yaml:6` 为 PostgreSQL 17；`deploy/compose.prod.yaml:5` 为 18，README 架构图声明 18。本地与生产主版本确实不同；没有据此操作任何数据卷。 |
| E-08 | 部分成立 | `deploy/xx.yuohira.com.caddy:10` 只拦旧 metrics 路径。实际完整路径由 `Program.cs:289,312,360` 确定，两个端点均 `RequireAuthorization()`。这是仓库模板的边界缺口，未验证远端 Caddy 当前内容。 |
| E-09 | 部分成立 | `Program.cs:98-104` 注册 postgres/market 为 ready，`:130-133` 选择 ready；`MarketHealthCheck.cs:9` 读取 `runtime.IsReady`。`PostgresSchema.cs:340-368` 启动时验证 schema 版本。周期探针没有重查 schema，市场检查是本地状态而非每请求访问租约表；原“无租约检查”应撤销。 |
| E-10 | 成立 | `Program.cs:430-442` 健康响应只有 status/service/checks；版本在 `:120,148` 的 meta 接口，未满足 AGENTS.md 对 healthz 版本字段的要求。 |
| E-11 | 成立 | `deploy/compose.prod.yaml:11,91,189` 给数据库初始化、API、Worker 使用同一 daohua 账号；部署目录无专用角色创建授权脚本，与 `commercial-persistence-architecture.md:549` 不符。仅确认模板，未检查线上角色；本地脚本实现不需要等待生产凭据。 |

## 报告结构与整改建议复核

- Git 历史确认 `e53b879` 删除 CombatSimulator、CombatGroupSimulator、CombatFormula、CharacterStatResolver，并新增简化 CombatModels。父提交存在回复、命中、暴击公式，故这是重要架构断点；但没有逐项历史归因支撑“绝大多数69条均由其引起”。

- A-F 编号数量确为 69，但 A-04/A-05 被写在同一 Markdown 行，破坏表格渲染。
- 原明细性质实际为：功能缺失 25、文档过期 17、命名不符 6、数值不符 5、协议单边 4、配表缺失 3、命令失效 2、配置不符 2，以及文档自相矛盾、实现超出文档、配表不符、文档缺失、配表不一致各 1。与总览“功能缺失38”等不相符；没有明确的重分类映射。
- 原总览的 P0/P1/P2 未逐条标注，不能追溯；不得将其当作已验证的 16 项紧急事故。
- B-11/X-01、D-14/X-02 有重复；X-03 比较代码常量、X-02 比较文案代码，不是纯文档内部矛盾；X-06 明确一致，不能算矛盾。
- A-04 同时列入“无需决策”与“需拍板”；原复核指出“每5秒2%”与“每10秒4%”的算术换算本身不构成冲突，MP 旧文档另有比率差异。此分析仅针对历史表述；当前需求为读取配置回复点数，生命的 2% 是配表依据，周期待讨论，不能把换算写入运行时方案。
- “字段齐备”不能证明机制只需接线；应先明确资源、事件时间边界、状态保存与前端语义。报告没有为“配表已是可玩值”提供平衡或实测证据。
- “以实现为准改全部文档”不是审计结论。历史记录可注明过期，现行玩法要求需要确认是否保留；两者不能混为一谈。
- §9/§10 的无问题清单不作为本次逐项复核的全部验收范围；没有重新验证全部词条、像素样式或运行时容量，不能沿用“无需修改”作为新的验收结论。

## X 补充复核

| 编号 | 结论 |
| --- | --- |
| X-01 | 与 B-11 重复；保留文档数字冲突，纠正 Qi 与 MP 的混淆。 |
| X-02 | 与 D-14 重复；需要确认丹药 Buff 与自然回复是否共用同一单位，不能仅凭不同标签断言。 |
| X-03 | `PlayerAggregate.cs:64-70` 初始100/100，`combatConstants.ts:228-229` 基准100/50，不一致成立；实际快照读服务端血蓝，不代表全部UI都显示50。 |
| X-04 | 命名并存属实，但 `maps.md:3-4` 已区分当前基准与未来储备，不是独立Bug。 |
| X-05 | 历史证据：`combat-design.md:214` MP每5秒2%，`:217` MP每10秒2%，相差一倍；前端基准100HP/50MP的2/1点仅在该基准恰为2%。后续用户已澄清回复读取基础属性配置，生命2%仅是配表依据，不能由该基准推导灵力固定比例，也不能据旧字段确定五秒或十秒周期。 |
| X-06 | 当前八节点一致；本轮未重新以图论脚本验证七条边，不作新的完整验收声明。 |

## 验证与 Git 边界

- 已检查 .NET 10.0.400、Node.js v22.14.0、npm 10.9.2、Docker Buildx v0.36.1。
- YuoCore submodule 为 `3a7b1e3e1cf9d883f1e71f2ad374f18b139bc938`，状态无缺失或相对主仓库固定提交漂移。
- 只读核对源码、文档、Git 历史与配置；不运行生产探测、容器构建、功能测试或容量门禁，不声称这些验证通过。
- 仅新增复核报告和任务详情、更新任务索引；保留用户已有改动。纯文档按项目规则不自动提交。
- 已通过69项编号唯一性/完整性检查与 `git diff --check`；这些仅为文档检查，不替代功能测试。
