# 玩家存档版本迁移与容错自愈架构规约

> **文档状态（2026-08-27）**：本文是正式运营后的历史/未来运营参考，不属于当前 clean-room v1 的运行契约。当前内测直接建立 schema v1，不实现旧格式迁移、双读双写、兼容 DTO 或 fallback；唯一现行目标方案见 [`commercial-persistence-architecture.md`](commercial-persistence-architecture.md)。

本文档定义《道化太初》在正式商业运营与本地开发全生命周期中，**玩家存档数据演进、版本链式迁移（Data Migration）与容错自愈（Self-Healing Reconciliation）** 的顶层设计与技术规约。

> **当前阶段约束（硬规则）**：当前项目处于**本地开发与内测阶段**，严格遵守 `AGENTS.md` 的兼容清零铁律。**本地开发阶段不设计、不实现任何历史兼容路径**，旧数据、旧快照、旧数据库 schema 均可直接废弃、改表与清空。本文中的 `IPlayerMigrator`、`PlayerStateReconciler` 与链式迁移图仅描述未来商业化运营可能采用的运维工具，当前代码不注册、不调用，也不作为玩家加载的第二道运行时防线。

---

## 一、 核心设计哲学：双重防线体系

在长线运营的修仙放置与网络游戏中，数值重构、物品迭代、技能重做与副本关卡调整是常态。为了在“配表/结构演进”与“玩家数据安全与稳定性”之间取得完美平衡，系统确立**双重防线体系**：

```text
                                 ┌────────────────────────────────────────────────────────┐
                                 │                   玩家加载快照载荷                     │
                                 └──────────────────────────┬─────────────────────────────┘
                                                            │
                                                            ▼
                                        ┌───────────────────────────────────────┐
                                        │  快照版本 < 当前服务端目标版本 ?       │
                                        └───────┬───────────────────────┬───────┘
                                                │ 是                    │ 否
                                                ▼                       ▼
                        ┌───────────────────────────────────────┐       │
                        │ 【第一道防线】                         │       │
                        │ 链式版本迁移管道 (Migration Pipeline)  │       │
                        │ v1 ──> v2 ──> v3 ... ──> vLatest      │       │
                        │ (负责：资产置换、全额退款、洗点补偿)  │       │
                        └───────────────────┬───────────────────┘       │
                                            │                           │
                                            ▼                           ▼
                                ┌───────────────────────────────────────────────────────┐
                                │ 【第二道防线】                                         │
                                │ 容错自愈清洗器 (PlayerStateReconciler)                 │
                                │ (负责：防呆防崩、剔除遗漏失效 ID、安全回退默认状态)   │
                                └───────────────────────────┬───────────────────────────┘
                                                            │
                                                            ▼
                                ┌───────────────────────────────────────────────────────┐
                                │ 盖上当前最新 GameDataFingerprint 戳并写回数据库       │
                                └───────────────────────────────────────────────────────┘
```

| 防线 | 核心组件 | 触发场景 | 核心职责 |
| :--- | :--- | :--- | :--- |
| **第一道防线** | `IPlayerMigrator` 链式迁移管道 | 生产运营环境、大版本升级 | **业务级资产补偿**：处理配表变更带来的玩家利益保护（如旧道具按比例折算新代币、重构天赋树全额退款、增补全新属性字段）。 |
| **第二道防线** | `PlayerStateReconciler` 容错自愈器 | 本地开发期、内测期、生产兜底 | **系统级高可用防崩**：对快照中的所有外键引用（ItemId、AbilityId、NodeId、EncounterId）进行有效性校验，自动剔除孤立废弃项，保证 100% 正常进入游戏。 |

---

## 二、 第一道防线：正式运营阶段的链式迁移体系

### 1. 快照版本标识规范

在 `SnapshotEnvelope` 中包含两个核心元数据：
- `SnapshotSchemaVersion`：单调递增的正整数（如 `1`, `2`, `3`），代表存档的数据结构与业务语义大版本。
- `GameDataFingerprint`：静态配表全集计算得出的 SHA-256 前 16 位哈希特征码，代表当时配表的世界规则基准。

### 2. 链式迁移接口与管道设计

每个版本跃迁由一个独立的 `IPlayerMigrator` 实现，严格只负责单次跨度（`Version N -> Version N+1`）：

```csharp
public interface IPlayerMigrator
{
    /// <summary>源版本号。</summary>
    int FromVersion { get; }

    /// <summary>目标版本号。</summary>
    int ToVersion { get; }

    /// <summary>执行业务数据转换与资产补偿。</summary>
    Task<PlayerState> MigrateAsync(PlayerState state, GameDataCatalog catalog, CancellationToken cancellationToken);
}
```

#### 链式执行调度算法：
```csharp
public sealed class PlayerMigrationPipeline(IEnumerable<IPlayerMigrator> migrators)
{
    public async Task<PlayerState> MigrateToLatestAsync(
        PlayerState state, 
        int currentSchemaVersion, 
        int targetSchemaVersion, 
        GameDataCatalog catalog, 
        CancellationToken cancellationToken)
    {
        var current = state;
        var version = currentSchemaVersion;

        while (version < targetSchemaVersion)
        {
            var migrator = migrators.FirstOrDefault(m => m.FromVersion == version && m.ToVersion == version + 1)
                ?? throw new InvalidOperationException($"找不到从版本 {version} 到 {version + 1} 的迁移器。");

            current = await migrator.MigrateAsync(current, catalog, cancellationToken);
            version = migrator.ToVersion;
        }

        return current;
    }
}
```

### 3. 业务迁移标准操作范式（Best Practices）

1. **物品删除与替代**：禁止直接将旧物品从数据库抹除致使玩家损失。应在 Migrator 中将旧物品转换为等价值的通用代币（如灵石、仙玉）或对应的新道具，并生成系统日志。
2. **天赋/功法重构**：若重构了功法修炼路线，Migrator 应重置该功法等级，并将玩家此前投入的经验与突破材料 100% 返还至背包。
3. **字段增补与默认值**：新增组件或字段时，Migrator 必须赋给符合业务不变量的合法初始值。

---

## 三、 迁移脚本定期修剪与冷批处理机制（Baseline & Archiving）

为防止游戏运营多年后积攒海量历史迁移脚本引发“代码库腐烂（技术债）”，制定以下修剪规约：

### 1. 停服维护全库批处理（Offline Batch Cold Patching）
- 在大版本停机维护窗口期，服务端启动独立的 `DataMigrationWorker` 批处理任务；
- 扫描数据库中的存量 `player_snapshots`，批量运行 Migration Pipeline，将全库离线玩家一次性拉齐到最新版本。

### 2. 基线版本修剪（Migration Baseline）
- 确立 **“支持最近 N 个版本（如最近 10 个发布版本）”** 的保留基线；
- 凡全库已批处理完毕且早于基线版本的旧 `Migrator` 类文件，可直接从代码仓库中**物理删除**；
- 极少数超过 2 年未上线的远古死号，若在基线之外上线，由第二道防线（`PlayerStateReconciler`）进行安全自愈兜底。

---

## 四、 第二道防线：运行时容错自愈体系（Self-Healing Reconciliation）

### 1. 适用阶段与运行场景
- **开发与内测阶段**：作为主控机制全量生效，开发者修改配表无需删库，系统自动清洗失效项并保留合法资产；
- **正式运营阶段**：作为 Migration 之后的兜底保护网，防止策划漏配或迁移遗漏导致的 500 崩溃。

### 2. 各领域模块的清洗规则矩阵

| 领域模块 | 目标字段 | 容错清洗规则（Reconciliation Rule） |
| :--- | :--- | :--- |
| **背包系统** | `Inventory.Items` | 遍历物品字典。若 `ItemId` 不在 `GameDataCatalog.Current.Items` 中，**安全剔除并记录日志**；合法物品完整保留。 |
| **装备系统** | `Combat.Equipment` | 遍历各装备槽位。若装备引用的 `ItemId` 不存在或不再是装备类型，**自动卸下并置空**。 |
| **技能系统** | `Combat.Abilities` & `Combat.ActiveAbilityIds` | 遍历已学技能与快捷栏。若 `AbilityId` 不存在于配表中，**从已学技能列表及技能槽中安全移除**。 |
| **动作队列** | `Queue.Entries` | 遍历队列项。若 `ActionId` 不存在，或战斗动作引用的 `CombatEncounterId` 不存在，**安全出队取消，防止队列推进死锁**。 |
| **战斗状态** | `CombatExpedition` | 若当前正在战斗且遭遇已失效，**安全结算当前收益并脱战复位**。 |
| **世界位置** | `World.CurrentNodeId` | 若玩家所处地图节点已在配表中删除，**安全传送回宗门/出生城镇安全节点**。 |
| **自动使用** | `Queue.AutoUseSlots` | 若绑定的消耗品 `ItemId` 已不存在，**自动解绑置空**。 |
| **生效状态** | `Buffs` | 剔除配表中已不存在的 `BuffId`。 |

### 3. 指纹刷新与持久化规约
1. 自愈完成后，`PlayerState` 的运行时指纹直接刷新为 `GameDataCatalog.Current.Fingerprint`；
2. 触发持久化保存时，以最新指纹将干净数据写回数据库，后续登录无需重复清洗；
3. 清洗过程必须输出清晰的 `ILogger.LogWarning` 结构化日志，保留清洗详情与审计依据。
