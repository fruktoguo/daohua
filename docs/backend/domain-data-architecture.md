# 道化太初 · 服务端领域数据架构与 YuoCore ECS 规约

本文档定义《道化太初》服务端的领域模型设计、数据类型规范以及与 `YuoCore` 引擎深度结合的架构标准。旨在解决传统贫血模型、巨型单体 State、手写深拷贝地狱以及“伪 ECS”退化问题，建立可测量性能、确定性、高扩展性的商业级数据层。当前处于本地开发/内测阶段，数据模型、快照、Protocol 和部署拓扑采用一次性切换，不设计历史兼容层；任务队列的时间、战斗幂等和快照细节以《任务队列系统与组件设计方案（深化版）》为准。

---

## 一、 核心设计哲学与 YuoCore 架构铁律

根据 YuoCore 吸收 ET 精华后的分布式单写者架构设计，服务端严格遵守以下五大铁律：

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        YuoCore 核心设计铁律                            │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 实体非线程安全：Entity/Component 永远只被当前 Fiber 的 YuoWorld 独占访问 │
│ 2. 并行粒度是 Fiber：多个 Fiber（分片）并行 Tick，Fiber 内部单线程零锁 ECS │
│ 3. 跨 Fiber 边界走 Actor：外部请求/跨服通信投递 Actor 消息，不直接碰 Entity  │
│ 4. 逻辑由唯一过渡 System 驱动：声明式注入组件，执行器只返回确定性结果       │
│ 5. 状态同步与持久化解耦：通过 StateSync 提取脏组件，通过 Persistence 异步落库 │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 二、 四层数据架构体系

严禁在单一的 `Models` 目录中堆砌所有类型，必须按**生命周期与所有权**严格划分为四层：

```text
┌────────────────────────────────────────────────────────────────────────┐
│ 1. 策划静态配置 (GameData 解析) │ 只读不可变、启动加载、FrozenDictionary   │
├────────────────────────────────────────────────────────────────────────┤
│ 2. 运行时领域组件 (YuoComponents) │ 单 Fiber 独占所有权、高频变动、脏标记驱动 │
├────────────────────────────────────────────────────────────────────────┤
│ 3. 纯业务逻辑系统 (YuoSystems)   │ 多泛型注入、确定性单线程 Tick             │
├────────────────────────────────────────────────────────────────────────┤
│ 4. 持久化与快照 (Server 序列化) │ 异步落盘、冷计算传输、不可变流式投影     │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. 第一层：策划静态配置（`src/Domain/GameData/` + `config/game-data/`）
- **职责**：描述技能、物品、怪物、遭遇等静态数值数据。
- **规范**：
  - 配置真源为 `config/game-data/` 下的 JSON 数据文件，由 GameData 解析器族（`DataBudgetResolver` / `GameDataCatalog`）在启动时直读，并按强度预算（Intensity/Budget）投影为 `Domain.Models` 中的只读配置 DTO 与定义载体（如 `AbilityConfig` → `CombatAbilityDefinition`、`ItemConfig` → `ItemDefinition`）；领域层不再保留独立的 `Definitions`/`Configs` 静态目录。
  - 集合一律使用 `FrozenDictionary` / `ImmutableArray`，启动解析后全生命周期只读不可变。
  - 杜绝在配置类中混入运行时可变字段。

### 2. 第二层：运行时领域聚合组件（`Domain.Models.PlayerComponent`）
- **职责**：挂载在 `YuoEntity` 上的权威领域状态载体。
- **规范**：
  - **纯净单体聚合 ECS**：服务端运行时在 `YuoEntity` 上统一挂载单体聚合组件 `PlayerComponent`（内含 `PlayerState`），彻底消除高频 Tick 与落盘时反射拆装多组件的 GC 压力与碎片。
  - **概念与领域子切片**：`PlayerState` 在领域概念与模块化上清晰划分为 7 大子切片（普通 C# 嵌套类）：
    - `Inventory`：背包物品字典（`InventoryState`）；
    - `Queue`：挂机动作队列、循环标记与推进游标（`ActionQueueState`）；
    - `Cultivation`：灵气储备、境界阶位与持续 Buff 列表（`CultivationState`）；
    - `CombatResources`：当前生命、灵力与技能/消耗品冷却（`CombatResourcesState`）；
    - `Stats`：生涯统计数据与灵石持有（`PlayerStatsState`）；
    - `World`：大地图所处节点与寻路状态（`WorldState`）；
    - `DirtySections`：集中追踪当前帧发生变更的子域位掩码。
  - **数值边界明确**：属性、收益、倍率和玩法时长遵循 `AGENTS.md` 的 `double` 规范；ID、版本、序列、事件计数和毫秒阶段游标属于离散技术字段，直接使用整数或强类型表示。

### 3. 第三层：业务推进与离散模拟引擎（`src/Domain/Simulation/SimulationEngine.cs`）
- **职责**：驱动游戏世界状态的离散事件跃迁（DES）、挂机推进与瞬时战斗推演。
- **规范**：
  - **单一真源铁律**：所有挂机时间、产物结算与战斗模拟严格以 `SimulationEngine.AdvanceTo` 为唯一实现，零锁、确定性单线程执行，由玩家所属 Fiber 串行调度。
  - **无副作用与纯函数性**：直接对 `PlayerState` 进行高效内存操作，每次变动自动标记 `DirtySections`。
- **职责**：实现数据库异步安全落盘与跨节点冷计算传输。
- **规范**：
  - **快照不保存运行时引用**：禁止把 Entity、World、Fiber、Timer、Channel、委托和缓存索引写入快照。
  - **当前版本投影式快照**：领域层不设独立 `Snapshots` 目录与 `SnapshotMapper`，序列化链路由服务端承担——`StateSerializer` 在 Fiber 安全点将 `PlayerState` 封装为包含当前 `SnapshotSchemaVersion`、状态版本、`SequenceId`、`LastSimulatedAt` 和 `GameDataFingerprint` 的不可变 `SnapshotEnvelope`（`StateSerializer.cs:43`）后落盘；对外线协议投影由 `StateMapper` 完成。错误版本直接拒绝，不提供历史迁移；不宣称零拷贝，必须先验证载荷独立性。

---

## 三、 强类型 ID 规范（Strongly Typed IDs）

杜绝基本类型偏执（Primitive Obsession），全系统使用 `readonly record struct` 包装业务标识符，提供编译期类型安全与零运行时分配：

```csharp
namespace Domain.Common;

public readonly record struct PlayerId(Guid Value)
{
    public static PlayerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct ItemId(string Value)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
    public static implicit operator ItemId(string value) => new(value);
}

public readonly record struct AbilityId(string Value);
public readonly record struct EncounterId(string Value);
```

---

## 四、 撮合与纯函数计算规范（`src/Domain/Market/` & `src/Domain/Simulation/`）

对于市场撮合、伤害结算等瞬时计算逻辑：
- 严格遵循 **Functional Core, Imperative Shell** 模式。
- 输入与输出模型一律采用不可变 `record` / `readonly record struct`。
- 状态演进通过 C# 原生 `with` 表达式完成，保证 100% 确定性与零副作用：

```csharp
public sealed record MarketOrder(
    string OrderId,
    PlayerId OwnerId,
    MarketInstrument Instrument,
    MarketSide Side,
    double Price,
    double Quantity,
    double FilledQuantity,
    MarketOrderStatus Status
)
{
    public MarketOrder WithFill(double filledAmount) => this with
    {
        FilledQuantity = FilledQuantity + filledAmount,
        Status = (FilledQuantity + filledAmount >= Quantity) 
            ? MarketOrderStatus.Filled 
            : MarketOrderStatus.PartiallyFilled
    };
}
```

---

## 五、 事务模型与跨边界一致性规约

在分布式挂机与交易系统中，严禁采用外部多重分布式大锁（如同时锁玩家 Fiber + 锁市场 Partition），必须严格采用 **YuoCore Actor 消息事务与 Saga 补偿模式**：

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        跨边界事务流转（以挂单为例）                    │
├────────────────────────────────────────────────────────────────────────┤
│ 1. 玩家 Fiber (Local Tx)   ───> 校验并冻结物品 (FreezeItem)            │
│                                  │                                     │
│                                  ▼ (Actor Request)                     │
│ 2. 市场 Actor (Partition)   ───> 撮合挂单 (Place & Match)              │
│                                  │                                     │
│                                  ▼ (Actor Response: Ok / Fail)         │
│ 3. 玩家 Fiber (Callback)   ───> 成功: 扣除冻结 / 失败: 解冻补偿回滚     │
└────────────────────────────────────────────────────────────────────────┘
```

### 1. 本地 Fiber 事务（In-Fiber Atomicity）
- 单个 Fiber 内部由单线程顺序执行，天然保证局部原子性。
- 组件属性变更采用**先校验、后应用**原则；复杂流转支持 Snapshot Token 回滚，杜绝脏半态。

### 2. 跨分片/跨服务事务（Saga / Actor Compensation）
- **挂单流程**：玩家所属 Fiber 仅负责自身资产的预扣/冻结，通过 `YuoActor` 向负责该品种的 `MarketPartitionActor` 投递撮合指令。
- **撤单与结算**：由市场 Actor 发送结算结果给对应玩家 Actor，异步入账，杜绝跨进程分布式锁。

### 3. 持久化原子性（Durable Outbox & Version CAS）
- 玩家快照与市场账本持久化利用 PostgreSQL Revision CAS 与 Durable Outbox，保证崩溃恢复不丢单、不重放。

---

## 六、 推荐代码目录拓扑

```text
src/Domain/
├── Constants/               # 全局常量、境界经验与战斗属性底座
│   ├── CombatConstants.cs
│   ├── ExperienceConstants.cs
│   ├── IdConstants.cs
│   ├── StatKeys.cs
│   └── SystemConstants.cs
│
├── Common/                  # 基础值对象与强类型 ID
│   └── Identifiers.cs       # PlayerId, ItemId, AbilityId...
│
├── GameData/                # 配置真源解析器族（直读 config/game-data）
│   ├── GameDataCatalog.cs
│   ├── DataBudgetResolver.cs
│   └── CharacterStatResolver.cs
│
├── Models/                  # 运行时实体组件与聚合状态模型
│   ├── Models.cs            # PlayerComponent, PlayerState（7 大领域切片）
│   ├── CombatModels.cs      # 战斗属性与状态
│   ├── ItemModels.cs        # 物品与装备定义
│   ├── GameDataModels.cs    # 静态配置 DTO
│   ├── MarketModels.cs      # 市场订单与账本
│   ├── WorldModels.cs       # 地图与遭遇定义
│   └── Enums.cs             # 正交五行、伤害分类与槽位枚举
│
├── Simulation/              # 纯函数模拟核心（唯一确定性推进真源）
│   ├── SimulationEngine.cs  # 全局离散推进与挂机结算总枢纽
│   ├── CombatSimulator.cs / CombatGroupSimulator.cs # 战斗模拟推演
│   ├── CombatFormula.cs / CombatCatalog.cs          # 战斗数学公式
│   ├── ActionCatalog.cs / ItemCatalog.cs            # 动作与物品目录
│   ├── BuffEvaluator.cs / WorldCatalog.cs           # Buff 与世界地图寻路
│   └── PlayerStateReconciler.cs                     # 状态对齐与重构
│
└── Market/                  # 交易子域（盘口、撮合引擎、订单账本）
    ├── MarketEngine.cs
    ├── OrderBook.cs
    ├── MarketPartitioner.cs
    ├── MarketPricePolicy.cs
    └── MarketCoordinator.cs
```
---

## 七、 PostgreSQL 权威存储层与物理表结构规约

### 1. 会话级咨询锁独占租约机制（Fail-Fast Advisory Lock）

服务端市场与写者租约严格遵守 `pg_try_advisory_lock` 立即失败（Fail-Fast Try-Lock）语义（`src/Server/Infrastructure/PostgresStores.cs:746-767`）：
- **即时决断**：写者实例启动时直接尝试获取会话级咨询锁 `SELECT pg_try_advisory_lock(@Key)`，若未抢到则立即抛出 `InvalidOperationException` 快速退出进程；
- **无等待/无重试**：全仓杜绝内部 30 秒轮询等待与任何本地文件锁。多实例安全由外层编排生命周期保证（`dev-hot.mjs` 先停后起、Compose 先停旧容器再起新容器），咨询锁随数据库连接断开自动释放。

### 2. PostgreSQL 7 张核心表物理结构与 DDL

```sql
-- 1. 账号与道号索引表
CREATE TABLE IF NOT EXISTS accounts (
    player_id UUID PRIMARY KEY,
    account VARCHAR(32) NOT NULL,
    account_normalized VARCHAR(32) NOT NULL,
    password_hash VARCHAR(128) NOT NULL,
    salt VARCHAR(48) NOT NULL,
    character_name VARCHAR(32) NOT NULL,
    character_name_normalized VARCHAR(32) NOT NULL,
    sequence_id BIGINT GENERATED BY DEFAULT AS IDENTITY UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (account_normalized),
    UNIQUE (character_name_normalized)
);

-- 2. 玩家状态快照表（Brotli 高压缩 + 结构化元数据）
CREATE TABLE IF NOT EXISTS player_snapshots (
    kind VARCHAR(32) NOT NULL,
    id VARCHAR(64) NOT NULL,
    payload_encoding VARCHAR(16) NOT NULL DEFAULT 'br',
    payload BYTEA NOT NULL,
    payload_bytes INT NOT NULL,
    compressed_bytes INT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    player_id UUID,
    account VARCHAR(32),
    character_name VARCHAR(32),
    sequence_id BIGINT,
    version BIGINT,
    presence_mode SMALLINT,
    last_simulated_at TIMESTAMPTZ,
    PRIMARY KEY (kind, id)
) WITH (autovacuum_vacuum_scale_factor = 0.05);

CREATE INDEX IF NOT EXISTS ix_player_snapshots_player_id ON player_snapshots (player_id) WHERE player_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_player_snapshots_idle_backlog ON player_snapshots (presence_mode, last_simulated_at) WHERE kind = 'player';

-- 3. 市场账本表（Revision 乐观并发 CAS 控制）
CREATE TABLE IF NOT EXISTS market_ledgers (
    partition_id INT NOT NULL,
    partition_count INT NOT NULL,
    algorithm VARCHAR(64) NOT NULL,
    revision BIGINT NOT NULL,
    payload_encoding VARCHAR(16) NOT NULL DEFAULT 'br',
    payload BYTEA NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    writer_instance VARCHAR(160) NOT NULL,
    PRIMARY KEY (partition_id, partition_count, algorithm)
) WITH (autovacuum_vacuum_scale_factor = 0.05);

-- 4. 持久化出件箱（针对高频写入定制激进 autovacuum）
CREATE TABLE IF NOT EXISTS persistence_outbox (
    sequence BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    command_kind SMALLINT NOT NULL,
    snapshot_kind VARCHAR(32),
    snapshot_id VARCHAR(64),
    event_stream VARCHAR(96),
    event_type VARCHAR(64),
    payload BYTEA NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
) WITH (autovacuum_vacuum_scale_factor = 0.01, autovacuum_vacuum_cost_limit = 1000);

-- 5. 出件箱消费水位确认表
CREATE TABLE IF NOT EXISTS persistence_outbox_ack (
    id SMALLINT PRIMARY KEY CHECK (id = 1),
    acknowledged_through BIGINT NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 6. 冷挂机分布式计算作业表
CREATE TABLE IF NOT EXISTS distributed_compute_jobs (
    job_id UUID PRIMARY KEY,
    player_id UUID NOT NULL,
    assigned_node_id VARCHAR(80),
    status SMALLINT NOT NULL DEFAULT 0,
    target_time TIMESTAMPTZ NOT NULL,
    input_payload BYTEA NOT NULL,
    output_payload BYTEA,
    error_code VARCHAR(64),
    error_message VARCHAR(512),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_distributed_compute_jobs_status ON distributed_compute_jobs (status, created_at);

-- 7. 市场交易流水表（按日 RANGE 分区 + 14 天滚动清理）
CREATE TABLE IF NOT EXISTS market_trades (
    trade_id BIGINT GENERATED ALWAYS AS IDENTITY,
    buyer_id UUID NOT NULL,
    seller_id UUID NOT NULL,
    item_id VARCHAR(32) NOT NULL,
    enhancement_level SMALLINT NOT NULL DEFAULT 0,
    price BIGINT NOT NULL,
    quantity BIGINT NOT NULL,
    gross_amount BIGINT NOT NULL,
    tax_amount BIGINT NOT NULL DEFAULT 0,
    traded_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (trade_id, traded_at)
) PARTITION BY RANGE (traded_at);
```

### 3. 透明 Brotli 载荷压缩策略（PostgresPayloadCodec）

为达成 2c4g/40g 预算下 5000+ 账号存储容量目标：
- 状态快照与市场账本载荷超过 256 字节时，自动执行 Brotli 压缩（通常可实现 70%~85% 压缩率）；
- 压缩后体积未缩小时自动回退为 `raw` 二进制直存；
- `player_snapshots` 表中显式记录 `payload_bytes`（解压原尺寸）与 `compressed_bytes`（实际压缩尺寸），兼顾容量审计与无损零膨胀。
```
