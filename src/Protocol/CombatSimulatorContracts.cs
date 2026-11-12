using System.Text.Json;

namespace Protocol.Simulator;

/// <summary>战斗预演契约的版本常量；格式变化时单调递增，浏览器/服务端按此拒绝失配载荷。</summary>
public static class SimulatorContractVersions
{
    public const int Profile = 1;
    public const int Environment = 1;
    public const int Request = 1;
    public const int Result = 1;
}

/// <summary>特质进度条目（id/level/exp）。</summary>
public sealed record SimulatorTraitProgressDto(string Id, int Level, int Exp);

/// <summary>五行灵根确认值。</summary>
public sealed record SimulatorLinggenDto(int Metal, int Wood, int Water, int Fire, int Earth);

/// <summary>六维根骨累计经验。</summary>
public sealed record SimulatorCoreAttributesDto(
    double StaminaExp, double MeridiansExp, double AttackExp,
    double BoneExp, double AgilityExp, double ConsciousnessExp);

/// <summary>功法装配槽位。</summary>
public sealed record SimulatorAbilitySlotDto(string? AbilityId, double Level);

/// <summary>装备槽位。</summary>
public sealed record SimulatorEquipmentSlotDto(string ItemId, double EnhancementLevel);

/// <summary>技能冷却条目。</summary>
public sealed record SimulatorSkillCooldownDto(string AbilityId, double RemainingSeconds);

/// <summary>跨战技能状态。</summary>
public sealed record SimulatorCombatSkillsDto(
    IReadOnlyList<SimulatorSkillCooldownDto> Cooldowns,
    string? CastingAbilityId,
    double CastRemainingSeconds,
    double CastCount);

/// <summary>Buff 效果载荷条目：通道整型值 + 每层基础值。</summary>
public sealed record SimulatorBuffEffectDto(int Channel, double Value);

/// <summary>战外 Buff 完整时钟。</summary>
public sealed record SimulatorActiveBuffDto(
    string BuffId,
    double ExpiresAtUnixSeconds,
    double Stacks = 1,
    string Name = "",
    string? SourceItemId = null,
    IReadOnlyList<SimulatorBuffEffectDto>? Effects = null,
    IReadOnlyDictionary<string, double>? StatRatios = null);

/// <summary>战内 Buff 跨波携带条目。</summary>
public sealed record SimulatorCombatBuffCarryDto(
    string BuffId,
    double RemainingSeconds,
    double Stacks = 1,
    string Name = "",
    double MissingHitpointsPerStack = 0,
    IReadOnlyList<SimulatorBuffEffectDto>? Effects = null,
    IReadOnlyDictionary<string, double>? StatRatios = null);

/// <summary>自动使用槽完整时钟。</summary>
public sealed record SimulatorAutoUseSlotDto(
    string ActionType,
    int SlotIndex,
    string? ItemId,
    double Quantity,
    bool Enabled,
    double RemainingDurationSeconds = 0,
    double CooldownUntilUnixSeconds = 0,
    string Group = "general");

/// <summary>普通钱包资产余额。</summary>
public sealed record SimulatorAssetDto(string ItemId, string Variant, double Quantity);

/// <summary>
/// 战斗预演角色档案：实战同源所需的最小白名单快照。种子/long 一律十进制字符串。
/// AbilityTriggers/ConsumableTriggers 保留三态：键缺失=配表默认，空数组=显式无条件可用。
/// </summary>
public sealed record SimulatorProfileDto(
    int FormatVersion,
    string GameDataFingerprint,
    string? SourcePlayerId,
    string DisplayName,
    string Avatar,
    double CapturedAtUnixSeconds,
    int RealmLevel,
    double Experience,
    double Foundation,
    double CombatExperience,
    double LifetimeExperience,
    double AgeSeconds,
    double CompletedActions,
    IReadOnlyList<string>? TraitIds,
    IReadOnlyList<SimulatorTraitProgressDto>? UnlockedTraits,
    SimulatorLinggenDto? ConfirmedLinggen,
    bool HeavenGateEntered,
    SimulatorCoreAttributesDto CoreAttributes,
    IReadOnlyList<SimulatorAbilitySlotDto> AbilitySlots,
    IReadOnlyDictionary<string, double>? AbilityExperiences,
    IReadOnlyList<SimulatorEquipmentSlotDto> Equipment,
    IReadOnlyDictionary<string, double>? TechniqueExperiences,
    IReadOnlyDictionary<string, JsonElement[]>? AbilityTriggers,
    IReadOnlyDictionary<string, JsonElement[]>? ConsumableTriggers,
    double Hitpoints,
    double MaxHitpoints,
    double Manapoints,
    double MaxManapoints,
    double RegenCarrySeconds,
    SimulatorCombatSkillsDto CombatSkills,
    IReadOnlyList<SimulatorCombatBuffCarryDto> CombatBuffCarry,
    IReadOnlyList<SimulatorActiveBuffDto> Buffs,
    IReadOnlyList<SimulatorAutoUseSlotDto> CombatAutoUseSlots,
    IReadOnlyList<SimulatorAssetDto> Assets,
    int RespawnPolicy,
    string? RespawnTargetPlayerId);

/// <summary>单座节点建筑快照。</summary>
public sealed record SimulatorNodeBuildingDto(
    string BuildingId,
    int Level,
    int TargetLevel,
    double StartedAtUnixSeconds,
    IReadOnlyDictionary<string, double>? ContributedMaterials = null);

/// <summary>节点建筑拥挤使用三元组。</summary>
public sealed record SimulatorNodeBuildingUsageDto(
    string BuildingId, double ActiveUsers, int Capacity, double Efficiency);

/// <summary>节点建筑汇总效果集（与领域 BuildingEffectSet 字段一一对应）。</summary>
public sealed record SimulatorBuildingEffectsDto(
    double ExplorationSpeedRatio = 0,
    double ExplorationOutputRatio = 0,
    double AlchemySpeedRatio = 0,
    double AlchemyOutputRatio = 0,
    double CultivationOutputRatio = 0,
    double CombatHitpointRegen = 0,
    double CombatDamageReductionRatio = 0,
    double MonsterWeakenRatio = 0,
    double GatheringSpeedRatio = 0,
    double MiningSpeedRatio = 0,
    double ForgingSpeedRatio = 0,
    double TalismanSpeedRatio = 0,
    double FormationSpeedRatio = 0,
    double EnhancementSpeedRatio = 0,
    double CombatManapointRegen = 0);

/// <summary>目标节点环境快照条目；无建筑节点 Buildings/Usages 为已知空数组。</summary>
public sealed record SimulatorEnvironmentNodeDto(
    string NodeId,
    IReadOnlyList<SimulatorNodeBuildingDto> Buildings,
    SimulatorBuildingEffectsDto Effects,
    IReadOnlyList<SimulatorNodeBuildingUsageDto> Usages);

/// <summary>战斗预演环境快照：捕获时刻冻结的全部可访问节点建筑状态。</summary>
public sealed record SimulatorEnvironmentDto(
    int FormatVersion,
    string GameDataFingerprint,
    double CapturedAtUnixSeconds,
    IReadOnlyList<SimulatorEnvironmentNodeDto> Nodes);

/// <summary>战斗预演请求：1~5 人档案 + 目标节点环境快照 + 显式种子。</summary>
public sealed record SimulatorRunRequestDto(
    int FormatVersion,
    string NodeId,
    int Difficulty,
    double DurationSeconds,
    string Seed,
    IReadOnlyList<SimulatorProfileDto> Members,
    SimulatorEnvironmentDto Environment);

/// <summary>战斗单位统计行（与 CombatUnitStatDocument 字段一一对应）。</summary>
public sealed record SimulatorCombatUnitStatDto(
    string UnitKey,
    int Kind,
    string DisplayName,
    double DamageDealtPhysical,
    double DamageDealtSpell,
    double DamageTakenPhysical,
    double DamageTakenSpell,
    double DamageMitigatedPhysical,
    double DamageMitigatedSpell,
    double HealingRegen,
    double HealingOther,
    double DamageDealtTrue = 0,
    double DamageTakenTrue = 0);

/// <summary>当前波次敌方单位快照（用于一致性比较与战场展示）。</summary>
public sealed record SimulatorExpeditionEnemyDto(
    string EnemyId,
    double Hitpoints,
    double Manapoints);

/// <summary>推演事件日志条目。</summary>
public sealed record SimulatorEventDto(
    double AtUnixSeconds,
    string Code,
    string Message,
    string? ActionId = null,
    IReadOnlyDictionary<string, double>? ItemDeltas = null);

/// <summary>战报物品条目。</summary>
public sealed record SimulatorReportItemDto(string ItemId, string Variant, double Quantity);

/// <summary>六维经验收益条目。</summary>
public sealed record SimulatorRootExpDto(
    double Stamina, double Meridians, double Attack,
    double Bone, double Agility, double Consciousness);

/// <summary>单个成员的推演终态与收益结算。</summary>
public sealed record SimulatorMemberResultDto(
    int MemberIndex,
    string PlayerId,
    string DisplayName,
    double Hitpoints,
    double MaxHitpoints,
    double Manapoints,
    double MaxManapoints,
    double RegenCarrySeconds,
    SimulatorCombatSkillsDto CombatSkills,
    IReadOnlyList<SimulatorCombatBuffCarryDto> CombatBuffCarry,
    double CompletedRepeats,
    int WaveNumber,
    string Phase,
    double Experience,
    double Foundation,
    double CombatExperience,
    double LifetimeExperience,
    SimulatorRootExpDto RootExp,
    double Cycles,
    double Encounters,
    double NormalKills,
    double EliteKills,
    double BossKills,
    double Deaths,
    IReadOnlyList<SimulatorReportItemDto> Items,
    IReadOnlyList<SimulatorReportItemDto> ItemsLost,
    IReadOnlyList<SimulatorAssetDto> AssetDeltas,
    IReadOnlyList<SimulatorAssetDto> RemainingConsumableBalances,
    IReadOnlyList<SimulatorAutoUseSlotDto> CombatAutoUseSlots,
    IReadOnlyList<SimulatorCombatUnitStatDto> CombatStats,
    IReadOnlyList<SimulatorExpeditionEnemyDto> Enemies,
    IReadOnlyList<SimulatorEventDto> Events,
    long DroppedEventCount = 0);

/// <summary>一次推演结果：地点×难度×种子下各成员终态与收益。</summary>
public sealed record SimulatorRunResultDto(
    int FormatVersion,
    string NodeId,
    int Difficulty,
    string Seed,
    double RequestedDurationSeconds,
    double SimulatedSeconds,
    IReadOnlyList<SimulatorMemberResultDto> Members);

/// <summary>属性预览请求：档案 + 目标节点（建筑效果折入一次）。</summary>
public sealed record SimulatorResolveRequestDto(
    int FormatVersion,
    string NodeId,
    IReadOnlyList<SimulatorProfileDto> Members,
    SimulatorEnvironmentDto Environment);

/// <summary>单个成员的真实属性解析结果（JsonElement 承载 PlayerAttributes 全字段投影）。</summary>
public sealed record SimulatorResolvedProfileDto(
    int MemberIndex,
    string PlayerId,
    string DisplayName,
    JsonElement Attributes,
    JsonElement CombatantStats);

/// <summary>属性预览结果。</summary>
public sealed record SimulatorResolveResultDto(
    int FormatVersion,
    string NodeId,
    IReadOnlyList<SimulatorResolvedProfileDto> Members);
