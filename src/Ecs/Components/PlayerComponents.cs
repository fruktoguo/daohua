using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Components;

/// <summary>玩家身份组件；网络/持久化边界只通过此组件的标识识别实体。</summary>
public sealed class PlayerIdentityComponent : YuoComponent
{
    public Guid PlayerId { get; set; }
    public string AccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long StateVersion { get; set; } = 1;
    public string GameDataFingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public PresenceState Presence { get; set; } = PresenceState.Online;
    /// <summary>组件持久化脏集合跨请求保留，由宿主成功入 durable queue 后确认。</summary>
    public HashSet<PlayerSectionKind> DirtySections { get; } = [];
    /// <summary>分段脏流水号：每次标脏单调递增（已脏分段再次触及也递增），
    /// 与 PlayerAggregateState.DirtySectionMarks 同语义，供命令/推送差集窄化判定。</summary>
    public Dictionary<PlayerSectionKind, long> DirtyMarks { get; } = [];
    public void MarkDirty(PlayerSectionKind section)
    {
        DirtySections.Add(section);
        DirtyMarks[section] = DirtyMarks.TryGetValue(section, out var mark) ? mark + 1 : 1;
    }
}

/// <summary>玩家成长组件，保存行动系统实际修改的数值，而不是缓存聚合对象。</summary>
public sealed class PlayerProgressionComponent : YuoComponent
{
    /// <summary>完整领域成长分段由 ECS 单写者直接持有，保留统计、余数与击杀计数。</summary>
    public PlayerProgression Progression { get; set; } = new();
    public int RealmLevel { get => Progression.RealmLevel; set => Progression.RealmLevel = value; }
    public double Qi { get => Progression.Qi; set => Progression.Qi = value; }
    public double Experience { get => Progression.Experience; set => Progression.Experience = value; }
    public double LifetimeQi { get => Progression.LifetimeQi; set => Progression.LifetimeQi = value; }
    public double CompletedActions { get => Progression.CompletedActions; set => Progression.CompletedActions = value; }
    public double AgeSeconds { get => Progression.AgeSeconds; set => Progression.AgeSeconds = value; }
}

/// <summary>玩家世界位置组件；节点引用对应长期存在的 MapNodeEntity。</summary>
public sealed class PlayerWorldComponent : YuoComponent
{
    /// <summary>完整世界分段，含边上移动与建筑投影字段。</summary>
    public PlayerWorldState World { get; set; } = new();
    public string NodeId { get => World.NodeId; set => World.NodeId = value; }
    public double X { get => World.X; set => World.X = value; }
    public double Y { get => World.Y; set => World.Y = value; }
    public double ExplorationSeed { get => World.ExplorationSeed; set => World.ExplorationSeed = value; }
    /// <summary>最近一次节点公共建筑投影版本；仅用于跳过重复读投影，不进入玩家存档。</summary>
    public long NodeBuildingProjectionVersion { get; set; } = long.MinValue;
}

/// <summary>玩家战斗组件，同时承载 ECS 侧战斗会话与当前资源。</summary>
public sealed class PlayerCombatComponent : YuoComponent
{
    /// <summary>完整战斗分段，包含技能冷却、吟唱和回复余数。</summary>
    public PlayerCombatState Combat { get; set; } = new();
    public double Hitpoints { get => Combat.Hitpoints; set => Combat.Hitpoints = value; }
    public double MaxHitpoints { get => Combat.MaxHitpoints; set => Combat.MaxHitpoints = value; }
    public double Manapoints { get => Combat.Manapoints; set => Combat.Manapoints = value; }
    public double MaxManapoints { get => Combat.MaxManapoints; set => Combat.MaxManapoints = value; }
    public double AttackPower { get => Combat.AttackPower; set => Combat.AttackPower = value; }
    public double Defense { get => Combat.Defense; set => Combat.Defense = value; }
    public CombatEncounterState? Encounter { get; set; }
    public long EncounterEntityId { get; set; }
    public long EncounterSeed { get; set; }
    public CombatOutcome LastOutcome { get; set; } = CombatOutcome.Running;
    public int LastEventCount { get; set; }
}

/// <summary>玩家装配组件。装备解析仍由 Domain 属性解析器负责，ECS 不复制公式。</summary>
public sealed class PlayerLoadoutComponent : YuoComponent
{
    /// <summary>完整装备/功法分段，保留六维、槽位、装备强化与技能经验。</summary>
    public PlayerLoadoutState Loadout { get; set; } = new();
    public List<string> EquippedItemIds { get; } = [];
    public List<string> LearnedAbilityIds { get; } = [];
}

/// <summary>玩家 Buff 组件，时间由 BuffTickSystem 统一处理。</summary>
public sealed class PlayerBuffComponent : YuoComponent
{
    /// <summary>完整战外 Buff 分段，保留版本号与所有 StatRatios。</summary>
    public PlayerBuffState Buffs { get; set; } = new();
    public Domain.Common.VersionedList<ActiveBuff> Active => Buffs.Active;
}

/// <summary>粗粒度行动队列组件；RemainingRepeats=-1 保持无限循环。</summary>
public sealed class PlayerActionQueueComponent : YuoComponent
{
    public List<EcsActionState> Actions { get; } = [];
    public DateTimeOffset LastAdvancedAt { get; set; }
    public DateTimeOffset LastSimulatedAt { get => LastAdvancedAt; set => LastAdvancedAt = value; }
}

public sealed class PlayerInventoryComponent : YuoComponent
{
    /// <summary>资产键保留变体后缀，避免装备词缀或品质在 ECS 往返中被折叠。</summary>
    public Dictionary<AssetKey, double> Items { get; } = [];
}

public sealed class PlayerEconomyComponent : YuoComponent
{
    public double SpiritStones { get; set; }
    public long AssetVersion { get; set; }
}

public sealed class PlayerSettingsComponent : YuoComponent
{
    public PlayerSettingsState Settings { get; set; } = new();
}

public sealed class PlayerDuelComponent : YuoComponent
{
    public PlayerDuelState Duel { get; set; } = new();

    public PlayerDuelStateDocument Document
    {
        get
        {
            var actions = Entity.GetComponent<PlayerActionQueueComponent>()!.Actions;
            return PlayerDuelStateCodec.Export(Duel, actions.Count == 0 ? null : actions[0].Id);
        }
        set => PlayerDuelStateCodec.Restore(Duel, value);
    }
}

public sealed class EcsActionState : IPlayerActionState
{
    public EcsActionState(Guid id, ActionIntent intent)
    {
        Id = id;
        Intent = intent;
        ActionId = intent.ActionId;
        RemainingRepeats = intent.RepeatCount;
    }

    public Guid Id { get; }
    public ActionId ActionId { get; }
    public ActionIntent Intent { get; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public ActionQueueStatus Status { get; set; } = ActionQueueStatus.Running;
    public double RemainingRepeats { get; set; }
    public double CompletedRepeats { get; set; }
    public double ProgressSeconds { get; set; }
    public CombatExpeditionState? Expedition { get; set; }
    public TravelActionState? Travel { get; set; }
}


/// <summary>怪物与玩家共享的战斗单位组件。</summary>
public sealed class CombatantComponent : YuoComponent
{
    public string CombatantId { get; set; } = "";
    public CombatantStats Stats { get; set; } = CombatantStats.Create(1, 1, 0, 1);
    public CombatantState? State { get; set; }
    public bool IsPlayer { get; set; }
}

public sealed class MonsterIdentityComponent : YuoComponent
{
    public string MonsterDefinitionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class SkillStateComponent : YuoComponent
{
    public List<SkillSlot> Skills { get; } = [];
    public double[] CooldownUntil { get; set; } = [];
}

public sealed class BuffComponent : YuoComponent
{
    public List<CombatBuffState> Active { get; } = [];
}

public sealed class DropProfileComponent : YuoComponent
{
    public IReadOnlyList<CombatDropDefinition> Drops { get; set; } = [];
}

public sealed class MapNodeComponent : YuoComponent
{
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public IReadOnlyList<string> Neighbors { get; set; } = [];
}

public sealed class NodeBuildingComponent : YuoComponent
{
    public BuildingEffectSet Effects { get; set; } = BuildingEffectSet.Empty;
}

public sealed class EncounterProfileComponent : YuoComponent
{
    public IReadOnlyList<string> EncounterIds { get; set; } = [];
}

public sealed class NodePopulationComponent : YuoComponent
{
    public int ActiveMonsterCount { get; set; }
    public int Capacity { get; set; } = 100;
}

/// <summary>世界级时钟与只读配表目录；挂在 YuoWorld.Main，系统按固定边界推进。</summary>
public sealed class CombatEncounterComponent : YuoComponent
{
    public string EncounterId { get; set; } = "";
    public long PlayerEntityId { get; set; }
    public IReadOnlyList<long> EnemyEntityIds { get; set; } = [];
    public CombatEncounterState State { get; set; } = null!;
    public long Seed { get; set; }
    /// <summary>本场战斗相对时钟的起点；用于把宿主 DateTimeOffset 的 100ns 量化与领域 double 边界对齐。</summary>
    public DateTimeOffset StartedAt { get; set; }
    public long PrimaryTargetEntityId { get; set; }
    public long AttributeRevision { get; set; }
    public List<CombatEvent> LastEvents { get; } = [];
}

public sealed class CombatLogComponent : YuoComponent
{
    public List<CombatEvent> Events { get; } = [];
}

public sealed class EcsClockComponent : YuoComponent
{
    public GameDataCatalog Catalog { get; set; } = GameDataCatalog.BuiltIn;
    public DateTimeOffset Current { get; set; }
    public double DeltaSeconds { get; set; }
    public bool PreparationOnly { get; set; }
    public DateTimeOffset Target { get; set; }
    /// <summary>当前请求的硬上界；战斗事件不得因 DateTime 量化越过该 double 目标。</summary>
    public DateTimeOffset RequestTarget { get; set; }
    public Guid? ActivePlayerId { get; set; }
    /// <summary>请求提交阶段只消费 Fiber mailbox，不再次运行行动系统。</summary>
    public bool SkipWorldUpdate { get; set; }
    public int SkipWorldUpdateEntities { get; set; }
    /// <summary>由 Fiber 内的行动阶段调用；宿主线程不得直接改玩家组件。</summary>
    public Func<YuoEntity, CombatEncounterState, DateTimeOffset, CombatEncounterComponent>? AttachCombatBattle { get; set; }
    public Action<YuoEntity>? ReleaseCombatBattle { get; set; }
}
