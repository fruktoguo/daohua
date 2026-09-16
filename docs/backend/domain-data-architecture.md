# 道化太初 · 服务端领域数据架构规约（聚合单写者模型）

本文档定义《道化太初》服务端的领域模型设计、数据类型规范与并发所有权架构。当前实现为 **`PlayerAggregate` 聚合 + `PlayerRuntime`/`PlayerActor` + `SemaphoreSlim` 单写者**模型；`YuoCore`（Ecs/Persistence/Runtime）基座引用在项目文件中保留待用，不构成当前运行时对象模型。历史 ECS/Fiber 推演稿见 `docs/design/ecs-architecture-and-evolution-plan.md`。当前处于本地开发/内测阶段，数据模型、快照、Protocol 和部署拓扑采用一次性切换，不设计历史兼容层。

---

## 一、 核心设计哲学与所有权铁律

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        聚合单写者核心设计铁律                          │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 聚合非线程安全：PlayerAggregate 永远只被其专属 PlayerActor 独占访问   │
│ 2. 并发粒度是玩家：每个在线玩家一个 Actor + SemaphoreSlim，串行零锁      │
│ 3. 跨边界不持引用：命令以 (command, payload) 数据形式进入 Actor 执行     │
│ 4. 逻辑由唯一引擎驱动：SimulationEngine.AdvanceTo 是唯一确定性推进真源   │
│ 5. 状态同步与持久化解耦：MarkDirty 标记分段，latest-only 队列异步落盘    │
└────────────────────────────────────────────────────────────────────────┘
```

- 同一玩家的命令路由、模拟推进与快照导出全部在专属 `PlayerActor` 内串行执行；`PlayerRuntime` 负责 Actor 生命周期与 `game.player_runtime_leases` 所有权租约续期。
- 领域层（`src/Domain`）是纯 C# 程序集，不引用 ASP.NET Core、Npgsql 或 Protocol；持久化与协议适配全部在 `src/Server` 完成。

---

## 二、 四层数据架构体系

```text
┌────────────────────────────────────────────────────────────────────────┐
│ 1. 策划静态配置 (GameData)      │ 内嵌资源、启动加载、只读字典           │
├────────────────────────────────────────────────────────────────────────┤
│ 2. 运行时领域聚合 (Player)      │ PlayerAggregate 七分段、MarkDirty 驱动 │
├────────────────────────────────────────────────────────────────────────┤
│ 3. 纯业务逻辑引擎 (Simulation/Combat/Market) │ 确定性推进与纯函数结算    │
├────────────────────────────────────────────────────────────────────────┤
│ 4. 持久化与协议投影 (Server)    │ SectionCodec、Postgres*、协议映射      │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. 第一层：策划静态配置（`src/Domain/GameData/` + `config/game-data/`）
- **职责**：描述行动、功法、物品、装备、怪物、遭遇、地图等静态数值数据。
- **规范**：
  - 配置真源为 `config/game-data/` 下的 JSON 文件；`Domain.csproj` 将其编译期嵌入为程序集 `EmbeddedResource`，运行时由 `GameDataCatalog.LoadEmbedded` 统一加载、解析并计算数据指纹，**不再直读磁盘**。
  - 解析产物为 `GameDataCatalog` 持有的只读定义集合（`CombatAbilityDefinition`、`ItemDefinition`、怪物/遭遇/地图定义、世界图 `WorldGraph` 等），启动后全生命周期只读不可变。
  - 杜绝在配置类中混入运行时可变字段。

### 2. 第二层：运行时领域聚合（`src/Domain/Player/PlayerAggregate`）
- **职责**：在线玩家的唯一权威可变状态，由专属 `PlayerActor` 单写者持有。
- **规范**：
  - `PlayerAggregate` 为单体聚合，内部按持久化边界划分为 **`PlayerSectionKind` 七分段**：
    - `Progression`（`PlayerProgression`）：境界等级、灵气修为、历史总修为；
    - `World`（`PlayerWorldState`）：所在节点/边、坐标与探索随机数种子；
    - `Combat`（`PlayerCombatState`，含 `PlayerCombatSkillState` 技能冷却/吟唱）：当前生命/灵力、攻防与 `RegenCarrySeconds` 回复余量；
    - `Buffs`（`PlayerBuffState`）：当前生效 Buff 及到期边界；
    - `Loadout`（`PlayerLoadoutState`，含 `AutoUseSlotState`）：装备实例引用、已学功法与自动使用槽；
    - `Settings`（`PlayerSettingsState`）：低频偏好（循环开关 `LoopEnabled`、语言等）；
    - `ActionQueue`（`QueuedAction` 队列）：进行中与排队行动，`RemainingRepeats == -1` 为无限循环。
  - **脏标记驱动**：状态变更通过 `MarkDirty(PlayerSectionKind)` / `Touch` 记录脏分段；`DirtySections` 只读视图供宿主差量刷盘与增量协议投影使用。
  - **资产不进聚合**：灵石、可堆叠物品余额与唯一装备实例统一由 `AssetWallet`（运行时钱包视图）+ `economy.*` 关系表管理，聚合七分段不含 Inventory/Stats 切片。
  - **数值边界明确**：属性、收益、倍率和玩法时长遵循 `AGENTS.md` 的 `double` 规范；ID、版本、序列属于离散技术字段，使用强类型表示。

### 3. 第三层：业务推进与结算引擎（`src/Domain/Simulation/`、`Combat/`、`Market/`）
- **职责**：驱动离散时间推进、战斗结算与市场撮合。
- **规范**：
  - **单一真源铁律**：所有挂机时间、产物结算与战斗模拟严格以 `SimulationEngine.AdvanceTo` 为唯一实现，确定性、由玩家专属 Actor 串行调用。
  - `CombatSkillEngine` 负责技能施放/冷却/耗蓝结算，`EncounterWaveSelector` 按遭遇预算确定性抽选波次，`PlayerAttributeResolver` 解算面板属性。
  - `OrderBook`/`MarketPartitioner` 提供纯函数撮合与分片路由；市场写者边界在宿主 `MarketRuntime`。

### 4. 第四层：持久化与协议投影（`src/Server/Clean/`）
- **职责**：数据库异步落盘、协议快照/增量映射与冷计算传输。
- **规范**：
  - **快照不保存运行时引用**：禁止把 Actor、信号量、连接、缓存索引写入载荷。
  - **Section 编解码**：`SectionCodec`（Server 侧静态适配器）将分段载荷序列化为稳定 JSON 并计算 SHA-256；原始字节 ≥ 512B 且压缩有效时自适应使用 Brotli（落库 `codec` 为 `json` 或 `br-json`）。
  - **持久化记录**：`PlayerStateHead`（提交头：state_version、schema_version、last_simulated_at、game_data_fingerprint、checksum）+ `PlayerSectionSnapshot`（分段最新行：section_version、codec、raw/stored bytes、checksum），对应 `game.player_state_heads` / `game.player_state_sections`；待写载荷经 `platform.section_flush_queue` latest-only 合并落盘。
  - **协议投影**：`ProtocolSnapshotMapper` 将聚合状态映射为快照/增量 DTO，必须显式透传当前激活的 `GameDataCatalog`，禁止脱离配表伪造协议载荷。
  - schema 版本不一致直接拒绝载入，不提供历史迁移。

---

## 三、 强类型 ID 规范（Strongly Typed IDs）

杜绝基本类型偏执，全系统使用 `readonly record struct` 包装业务标识符（`src/Domain/Common/Identifiers.cs`），提供编译期类型安全与零运行时分配：

```csharp
namespace Domain.Common;

public readonly record struct PlayerId(Guid Value) : IIdentifier;
public readonly record struct AccountId(Guid Value) : IIdentifier;
public readonly record struct ItemId(string Value) : IIdentifier;
public readonly record struct AbilityId(string Value) : IIdentifier;
public readonly record struct ActionId(string Value) : IIdentifier;
public readonly record struct OrderId(Guid Value) : IIdentifier;
public readonly record struct IdempotencyKey(string Value) : IIdentifier;
```

---

## 四、 撮合与纯函数计算规范（`src/Domain/Market/` & `src/Domain/Simulation/`）

对于市场撮合、伤害结算等瞬时计算逻辑：
- 严格遵循 **Functional Core, Imperative Shell** 模式。
- 输入与输出模型一律采用不可变 `record` / `readonly record struct`。
- 订单簿演进保持确定性：相同输入序列在任何进程/重放中产生相同撮合结果。

---

## 五、 事务模型与跨边界一致性规约

市场与资产事务**不使用跨边界 Actor 消息或 Saga 补偿**，而是「市场本地单写者 + PostgreSQL 同步事务」：

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        挂单事务流转（现行实现）                        │
├────────────────────────────────────────────────────────────────────────┤
│ 1. PlayerActor 校验请求 ──► MarketRuntime（该分片唯一写者）            │
│ 2. 单个 PostgreSQL Serializable 事务内：                               │
│      校验 partition_leases fence ──► 冻结资产 ──► 落订单/成交/流水     │
│ 3. 同步提交后返回结果；失败整体回滚，无补偿分支                         │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. 玩家聚合内原子性
- `PlayerActor` 串行执行天然保证聚合内原子性；跨聚合的资产变更通过 `AssetWallet` + `PostgresEconomyRepository` 在单事务中提交，失败时恢复内存快照证据。

### 2. 市场分片写者
- 每个 `partition_id` 只有一个逻辑写者；租约通过 `market.partition_leases` 行与 `fence_token` 管理，续租失败或 fence 不匹配立即拒绝写入。
- 市场事务使用 Serializable 隔离、分片租约行锁与 fence 校验；订单、冻结、余额和流水在同一事务原子提交。订单 `version` 是修订记录，不是独立的 CAS 比较入口。
- 订单簿是内存投影；重启时由活跃订单与未完成冻结重建。

### 3. 持久化原子性
- 玩家 section 使用版本条件更新 + latest-only 刷盘队列；命令幂等经 `platform.command_receipts` 回执去重；需要可靠外部副作用时在同一事务追加 `platform.integration_outbox`。

---

## 六、 代码目录拓扑（现行）

```text
src/Domain/
├── Common/                  # 基础值对象与强类型 ID
│   ├── Identifiers.cs       # PlayerId, ItemId, AbilityId, OrderId...
│   └── DomainErrors.cs      # DomainException / DomainErrorCode
│
├── GameData/                # 配置真源（内嵌资源 LoadEmbedded）
│   ├── GameDataCatalog.cs
│   ├── BaseAttributeCatalog.cs
│   └── CombatCatalogModels.cs
│
├── Player/                  # 玩家聚合与分段状态
│   ├── PlayerAggregate.cs   # 聚合根 + 各 PlayerXxxState 分段
│   ├── PlayerSection.cs     # PlayerSectionKind / PlayerStateHead / PlayerSectionSnapshot / SectionFlushItem
│   ├── PlayerLoadout.cs
│   ├── PlayerDuel.cs        # 斗法分段：遭遇计数/冷却/暂存区/战报账本/预约-挂载-弃战
│   └── PlayerStateDocument.cs # 分段持久化文档与恢复校验
│
├── Simulation/              # 唯一确定性推进真源
│   └── SimulationEngine.cs
│
├── Combat/                  # 战斗结算与面板解算
│   ├── CombatModels.cs
│   ├── DuelModels.cs        # 斗法剧本推演（DuelScriptEngine）与单视角回放状态
│   ├── CombatSkillEngine.cs
│   ├── EncounterWaveSelector.cs
│   └── PlayerAttributeResolver.cs
│
├── World/                   # 地图、节点与世界图
│   └── WorldModels.cs
│
├── Market/                  # 交易子域（盘口、撮合、分片）
│   ├── MarketModels.cs
│   ├── OrderBook.cs
│   └── MarketPartitioner.cs
│
└── Economy/                 # 资产钱包与账本模型
    └── AssetModels.cs       # AssetWallet / AssetBalance / AssetKey
```

> `src/Domain/Constants/`、`src/Domain/Models/` 为历史遗留空目录，不属于现行拓扑。

宿主侧（`src/Server/Clean/`）：`PlayerRuntime`（内含 `PlayerActor`）、`MarketRuntime`、`WebSocketRuntime`、`ProtocolSnapshotMapper`、`SectionCodec`、`Postgres*Repository`、`*Worker`/`*HostedService`。

---

## 七、持久化权威边界

本文件只定义领域模型、聚合所有权和确定性模拟边界，不再定义具体数据库表、快照载荷、队列或备份拓扑。

持久化的唯一目标方案、物理表模型、section 快照、资产/市场事务、latest-only 刷盘队列、Outbox、容量预算、恢复策略以及 Redis 选型，以《商业级持久化最终架构（清零历史包袱）与 Redis 选型决策》为唯一权威：

- [commercial-persistence-architecture.md](commercial-persistence-architecture.md)

当前持久化基线为 **schema v2**（`PostgresSchema.Sql` 直建）；不保留旧表、旧载荷、双读双写或兼容适配。

领域层仍只负责生成可持久化的纯数据投影；数据库连接、事务、压缩、重试、备份和 Redis（若未来经过门槛引入）均属于 Server 基础设施边界。
