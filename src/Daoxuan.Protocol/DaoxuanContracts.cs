using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daoxuan.Protocol;

public static class DaoxuanPacketKinds
{
    public const string Hello = "hello";
    public const string Snapshot = "snapshot";
    public const string Delta = "delta";
    public const string Command = "command";
    public const string Ack = "ack";
    public const string Error = "error";
}

public sealed record DaoxuanEnvelope<T>(
    string Kind,
    string RequestId,
    long Sequence,
    T Payload);

public sealed record DaoxuanIncomingPacket(
    string Kind,
    string RequestId,
    long Sequence,
    JsonElement Payload);

public sealed record DaoxuanActionDto(
    string Id,
    string Name,
    string Description,
    string Type,
    int DurationSeconds,
    string ExecutionKind = "timed",
    string? CombatEncounterId = null,
    IReadOnlyDictionary<string, long>? Inputs = null,
    IReadOnlyDictionary<string, long>? Outputs = null,
    IReadOnlyList<string>? AvailableNodeIds = null);

public sealed record DaoxuanQueueEntryDto(
    string EntryId,
    string ActionId,
    string ActionName,
    string Type,
    string Status,
    int ProgressSeconds,
    int DurationSeconds,
    int RemainingRepeats,
    string ExecutionKind = "timed",
    string? TravelDestinationNodeId = null,
    IReadOnlyList<string>? TravelPathEdgeIds = null,
    DaoxuanExpeditionProgressDto? Expedition = null);

public sealed record DaoxuanAutoUseSlotDto(
    string? ItemId,
    int Quantity,
    bool Enabled,
    IReadOnlyList<DaoxuanCombatTriggerDto>? Triggers = null);

public sealed record DaoxuanQueueDto(
    IReadOnlyList<DaoxuanQueueEntryDto> Entries,
    bool LoopEnabled,
    IReadOnlyDictionary<string, IReadOnlyList<DaoxuanAutoUseSlotDto>> AutoUseSlots);

public sealed record DaoxuanQueueProgressDto(
    string EntryId,
    string Status,
    int ProgressSeconds,
    int DurationSeconds,
    DaoxuanExpeditionProgressDto? Expedition = null);

public sealed record DaoxuanExpeditionProgressDto(
    string Phase,
    double Progress,
    int ElapsedSeconds,
    int DurationSeconds,
    double? TravelledDistance = null,
    double? TotalDistance = null,
    long? PlayerHitpoints = null,
    long? PlayerMaxHitpoints = null,
    long? PlayerManapoints = null,
    long? PlayerMaxManapoints = null,
    long? EnemyHitpoints = null,
    long? EnemyMaxHitpoints = null,
    long? EnemyManapoints = null,
    long? EnemyMaxManapoints = null,
    string? EnemyName = null,
    string? Activity = null,
    DaoxuanCombatActionStateDto? PlayerAction = null,
    DaoxuanCombatActionStateDto? EnemyAction = null,
    IReadOnlyList<string>? Activities = null,
    long BattleSequence = 0,
    IReadOnlyDictionary<string, long>? AbilityCooldownMilliseconds = null,
    IReadOnlyList<long>? ConsumableCooldownMilliseconds = null,
    string? EncounterId = null);

public sealed record DaoxuanCombatActionStateDto(
    string Name,
    string Kind,
    long ElapsedMilliseconds,
    long DurationMilliseconds,
    long? CompletesAtUnixMilliseconds = null);

public sealed record DaoxuanCombatResourceDto(
    long Hitpoints,
    long MaxHitpoints,
    long Manapoints,
    long MaxManapoints,
    IReadOnlyDictionary<string, long>? AbilityCooldownMilliseconds = null,
    IReadOnlyList<long>? ConsumableCooldownMilliseconds = null);

public sealed record DaoxuanCombatAttributesDto(
    long MaxHitpoints,
    long MaxManapoints,
    int AttackIntervalMilliseconds,
    double AccuracyRating,
    double MaxDamage,
    double EvasionRating,
    double Armor,
    double WaterResistance,
    double NatureResistance,
    double FireResistance,
    string Style,
    string DamageType,
    double Tenacity,
    double HpRegenPerTenSeconds,
    double MpRegenPerTenSeconds,
    double PenetrationRatio,
    double PierceChance,
    double CurseRatio,
    double FuryRatio,
    double WeakenRatio,
    double LifeStealRatio,
    double ArmorDamageRatio,
    double HpDrainRatio,
    double DamageTakenRatio,
    double CriticalChance,
    double CriticalDamageRatio,
    double CriticalAvoidance,
    double DamageAbsorptionRatio,
    double CriticalRating = 0,
    double CriticalAvoidanceRating = 0);

public sealed record DaoxuanInventoryItemDto(
    string ItemId,
    string Name,
    string Category,
    long Amount,
    string Tone,
    string Glyph,
    int Tier = 1,
    string Description = "",
    bool IsTradable = true);

public sealed record DaoxuanItemDefinitionDto(
    string ItemId,
    string Name,
    string Description,
    string Kind,
    string Category,
    int Tier,
    long SellPrice,
    bool IsTradable,
    IReadOnlyList<string> UsableActionTypes,
    int CooldownSeconds = 0,
    int RecoverySeconds = 0,
    long HitpointRestore = 0,
    long ManapointRestore = 0,
    double TriggerHpRatio = 0.6,
    bool TriggerAtCombatStart = false,
    IReadOnlyList<DaoxuanCombatTriggerDto>? DefaultCombatTriggers = null,
    IReadOnlyList<DaoxuanItemBuffEffectDto>? BuffEffects = null);

public sealed record DaoxuanItemBuffEffectDto(
    string Name,
    int DurationSeconds,
    int MaxStacks,
    double QiGainMultiplier,
    double AttackLevelRatio,
    double DamageRatio,
    double AttackSpeedRatio,
    double GatheringOutputMultiplier,
    double AlchemyOutputMultiplier);

public sealed record DaoxuanCombatTriggerDto(
    string Dependency,
    string Condition,
    string Comparator,
    double Value = 0,
    string? BuffId = null);

public sealed record DaoxuanBuffDto(
    string BuffId,
    string Name,
    int Stacks,
    double QiGainMultiplier,
    long ExpiresAtUnixSeconds,
    double AttackLevelRatio = 0,
    double DamageRatio = 0,
    double AttackSpeedRatio = 0,
    double GatheringOutputMultiplier = 1,
    double AlchemyOutputMultiplier = 1,
    string? SourceItemId = null);

public sealed record DaoxuanStatsDto(
    long Qi,
    long Spirit,
    long HerbsCollected,
    long PillsCrafted,
    long CultivationCycles,
    long CompletedActions,
    long BlockedActions,
    long CombatWins = 0,
    long CombatLosses = 0,
    double CombatExperience = 0,
    long CombatDamageDealt = 0,
    long CombatDamageTaken = 0,
    long CombatEnemiesDefeated = 0,
    long CombatPlayersDefeated = 0,
    double FoundationExp = 0);

public sealed record DaoxuanCombatAbilitySlotDto(
    string? AbilityId,
    int Level,
    IReadOnlyList<DaoxuanCombatTriggerDto>? Triggers = null);

public sealed record DaoxuanCombatAbilityDto(
    string AbilityId,
    string Name,
    string Style,
    string DamageType,
    int CooldownMilliseconds,
    int CastMilliseconds,
    long ManaCost,
    double DamageRatio,
    long FlatDamage,
    double PenetrationRatio,
    double TriggerHpRatio,
    string EffectType = "damage",
    double HealingRatio = 0,
    long FlatHealing = 0,
    IReadOnlyList<DaoxuanCombatTriggerDto>? DefaultTriggers = null,
    double DamageOverTimeRatio = 0,
    int DamageOverTimeMilliseconds = 0,
    int DamageOverTimeTickMilliseconds = 3000,
    double StunChance = 0,
    int StunMilliseconds = 0,
    double BlindChance = 0,
    int BlindMilliseconds = 0,
    double SilenceChance = 0,
    int SilenceMilliseconds = 0,
    double DamageRatioLevelBonus = 0,
    double FlatDamageLevelBonus = 0,
    double HealingRatioLevelBonus = 0,
    double FlatHealingLevelBonus = 0,
    string TargetType = "enemy",
    double PierceChance = 0,
    double ArmorDamageRatio = 0,
    double HpDrainRatio = 0);

public sealed record DaoxuanEquipmentCombatStatsDto(
    double AccuracyRatio = 0,
    double DamageRatio = 0,
    double AttackSpeedRatio = 0,
    double EvasionRatio = 0,
    double Armor = 0,
    double WaterResistance = 0,
    double NatureResistance = 0,
    double FireResistance = 0,
    double Tenacity = 0,
    double PenetrationRatio = 0,
    double PierceChance = 0,
    double CurseRatio = 0,
    double FuryRatio = 0,
    double WeakenRatio = 0,
    double LifeStealRatio = 0,
    double ArmorDamageRatio = 0,
    double HpDrainRatio = 0,
    double CriticalChance = 0,
    double CriticalDamageRatio = 0,
    double CriticalAvoidance = 0,
    double DamageAbsorptionRatio = 0,
    int AttackIntervalMilliseconds = 0,
    string? Style = null,
    string? DamageType = null);

public sealed record DaoxuanCombatEquipmentDto(
    string EquipmentId,
    string Name,
    string Slot,
    int Tier,
    DaoxuanEquipmentCombatStatsDto Stats,
    DaoxuanEquipmentCombatStatsDto EnhancementBonus);

public sealed record DaoxuanCharacterVisualDto(
    string Renderer,
    string Source,
    string? Poster = null);

public sealed record DaoxuanCombatantDto(
    string EnemyId,
    string Name,
    DaoxuanCharacterVisualDto Visual,
    string Style,
    string DamageType,
    long MaxHitpoints,
    long MaxManapoints,
    int AttackIntervalMilliseconds,
    double AccuracyRating,
    double EvasionRating,
    long MaxDamage,
    double Armor,
    double WaterResistance,
    double NatureResistance,
    double FireResistance,
    double Tenacity,
    double PierceChance,
    double CurseRatio,
    double FuryRatio,
    double WeakenRatio,
    double LifeStealRatio,
    double ArmorDamageRatio,
    double HpDrainRatio,
    double CriticalChance,
    double CriticalDamageRatio,
    double CriticalAvoidance,
    double DamageAbsorptionRatio,
    double HpRegenPerTenSeconds,
    double MpRegenPerTenSeconds,
    int EnrageTimeMilliseconds,
    long Experience,
    IReadOnlyList<string> AbilityIds);

public sealed record DaoxuanCombatSpawnDto(
    DaoxuanCombatantDto Enemy,
    double Weight,
    int Strength);

public sealed record DaoxuanCombatEncounterDto(
    string EncounterId,
    string Name,
    DaoxuanCombatantDto Enemy,
    IReadOnlyList<DaoxuanCombatSpawnDto> RandomSpawns,
    int MaxSpawnCount,
    int MaxTotalStrength,
    IReadOnlyList<DaoxuanCombatantDto> BossEnemies,
    int BattlesPerBoss,
    int SearchMilliseconds,
    int RespawnMilliseconds,
    int PlayerRespawnMilliseconds);

public sealed record DaoxuanEquippedItemDto(
    string ItemId,
    int EnhancementLevel = 0);

public sealed record DaoxuanConsumableSlotDto(
    string? ItemId,
    int Quantity,
    bool Enabled,
    IReadOnlyList<DaoxuanCombatTriggerDto>? Triggers = null);

public sealed record DaoxuanCombatLoadoutDto(
    string Style,
    string DamageType,
    int StaminaLevel,
    int IntelligenceLevel,
    int AttackLevel,
    int DefenseLevel,
    int SwordsmanshipLevel,
    int TalismanLevel,
    int FiveElementsLevel,
    IReadOnlyList<DaoxuanCombatAbilitySlotDto> AbilitySlots,
    IReadOnlyList<DaoxuanConsumableSlotDto> ConsumableSlots,
    double CombatDropRate = 0,
    double CombatDropQuantity = 0,
    double CombatRareFind = 0,
    double Tenacity = 0,
    double SearchSpeedRatio = 0,
    IReadOnlyList<DaoxuanEquippedItemDto>? Equipment = null,
    IReadOnlyList<string>? LearnedAbilityIds = null);

public sealed record DaoxuanWorldNodeDto(
    string NodeId,
    string Name,
    string Description,
    double X,
    double Y,
    double CultivationMultiplier,
    IReadOnlyList<string> AvailableActionIds,
    IReadOnlyList<string> EncounterIds,
    IReadOnlyList<DaoxuanNearbyEntityDto> Npcs,
    IReadOnlyList<DaoxuanNearbyEntityDto> Buildings);

public sealed record DaoxuanWorldEdgeDto(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    int DurationSeconds);

public sealed record DaoxuanWorldMapDto(
    string MapId,
    string Name,
    string Description,
    int BackgroundSeed,
    IReadOnlyList<DaoxuanWorldNodeDto> Nodes,
    IReadOnlyList<DaoxuanWorldEdgeDto> Edges);

public sealed record DaoxuanNearbyEntityDto(
    string EntityId,
    string Name,
    string Kind,
    string Description,
    string Effect);

public sealed record DaoxuanWorldStateDto(
    string MapId,
    string? NodeId,
    string? EdgeId,
    double EdgeProgress,
    double X,
    double Y,
    string LocationName,
    double CultivationMultiplier,
    IReadOnlyList<string> AvailableActionIds,
    IReadOnlyList<string> EncounterIds,
    IReadOnlyList<DaoxuanNearbyEntityDto> Npcs,
    IReadOnlyList<DaoxuanNearbyEntityDto> Buildings);

public sealed record DaoxuanLogEventDto(
    long Sequence,
    long TimestampUnixMilliseconds,
    string Template);

public sealed record DaoxuanPlayerSnapshotDto(
    string PlayerId,
    string Name,
    long Version,
    long ServerTimeUnixSeconds,
    string PresenceMode,
    DaoxuanQueueDto Queue,
    IReadOnlyList<DaoxuanInventoryItemDto> Inventory,
    IReadOnlyList<DaoxuanBuffDto> Buffs,
    DaoxuanStatsDto Stats,
    IReadOnlyList<string> Events,
    IReadOnlyList<DaoxuanLogEventDto>? LogEvents = null,
    DaoxuanCombatLoadoutDto? Combat = null,
    DaoxuanWorldStateDto? World = null,
    DaoxuanCombatResourceDto? CombatResources = null,
    DaoxuanCombatAttributesDto? CombatAttributes = null,
    DaoxuanCharacterVisualDto? Visual = null,
    long? ServerTimeUnixMilliseconds = null,
    bool IsPendingBreakthrough = false,
    string? Account = null,
    long SequenceId = 0,
    long CreatedAtUnixSeconds = 0,
    long RenameCooldownSeconds = 0);

public sealed record DaoxuanPlayerPatchDto(
    string PlayerId,
    long Version,
    long ServerTimeUnixSeconds,
    string? PresenceMode = null,
    DaoxuanQueueDto? Queue = null,
    IReadOnlyList<DaoxuanInventoryItemDto>? Inventory = null,
    IReadOnlyList<DaoxuanBuffDto>? Buffs = null,
    DaoxuanStatsDto? Stats = null,
    IReadOnlyList<string>? Events = null,
    DaoxuanQueueProgressDto? QueueProgress = null,
    IReadOnlyList<DaoxuanLogEventDto>? EventBatch = null,
    DaoxuanCombatLoadoutDto? Combat = null,
    DaoxuanWorldStateDto? World = null,
    DaoxuanCombatResourceDto? CombatResources = null,
    DaoxuanCombatAttributesDto? CombatAttributes = null,
    DaoxuanCharacterVisualDto? Visual = null,
    long? ServerTimeUnixMilliseconds = null,
    bool? IsPendingBreakthrough = null,
    string? Name = null,
    string? Account = null,
    long? SequenceId = null,
    long? CreatedAtUnixSeconds = null,
    long? RenameCooldownSeconds = null);

public sealed record DaoxuanAckDto(
    bool Success,
    string Code,
    string Message,
    long ServerTimeUnixSeconds);

public sealed record DaoxuanErrorDto(string Code, string Message);

public sealed record HelloRequest(string? Name = null);
public sealed record RenameRequest(string Name);
public sealed record EnqueueActionRequest(string ActionId, int Repeats = 1);
public sealed record StartCombatRequest(string ActionId, int Repeats = 1);
public sealed record RemoveActionRequest(string EntryId);
public sealed record MoveActionRequest(string EntryId, int Direction);
public sealed record TravelToRequest(string DestinationNodeId);
public sealed record SetAutoUseRequest(
    string ActionType,
    string? ItemId,
    int Quantity = 1,
    bool Enabled = true,
    int SlotIndex = 0,
    IReadOnlyList<DaoxuanCombatTriggerDto>? Triggers = null);
public sealed record UseItemRequest(string ItemId, string ActionType, int Quantity = 1);
public sealed record SetLoopRequest(bool Enabled);
public sealed record SetPresenceRequest(string Mode);
public sealed record SetCombatLoadoutRequest(
    string? Style = null,
    string? DamageType = null,
    IReadOnlyList<DaoxuanCombatAbilitySlotDto>? AbilitySlots = null,
    IReadOnlyList<DaoxuanConsumableSlotDto>? ConsumableSlots = null,
    // 旧版客户端字段：仅用于兼容反序列化，服务端按先 foodSlots 后 drinkSlots 合并进 ConsumableSlots。
    IReadOnlyList<DaoxuanConsumableSlotDto>? FoodSlots = null,
    IReadOnlyList<DaoxuanConsumableSlotDto>? DrinkSlots = null,
    double? CombatDropRate = null,
    double? CombatDropQuantity = null,
    double? CombatRareFind = null,
    double? Tenacity = null,
    double? SearchSpeedRatio = null,
    IReadOnlyList<DaoxuanEquippedItemDto>? Equipment = null);

public sealed record DaoxuanMarketLevelDto(long Price, long Quantity, int OrderCount);

public sealed record DaoxuanMarketBookDto(
    string ItemId,
    int EnhancementLevel,
    IReadOnlyList<DaoxuanMarketLevelDto> Asks,
    IReadOnlyList<DaoxuanMarketLevelDto> Bids,
    long ActiveOrderCount,
    long Sequence);

public sealed record DaoxuanMarketMetricsDto(
    int ShardCount,
    long ActiveOrderCount,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

public sealed record DaoxuanMarketOrderDto(
    string OrderId,
    string ItemId,
    int EnhancementLevel,
    string Side,
    long Price,
    long OriginalQuantity,
    long RemainingQuantity,
    long FilledQuantity,
    string Status,
    long Sequence,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ExpiresAt = default);

public sealed record DaoxuanMarketFillDto(
    string TakerOrderId,
    string MakerOrderId,
    string BuyerId,
    string SellerId,
    string ItemId,
    int EnhancementLevel,
    long Price,
    long Quantity,
    long GrossAmount,
    long SellerProceeds,
    long SellerTax);

public sealed record DaoxuanMarketCommandResponseDto(
    bool Success,
    string Code,
    string Message,
    DaoxuanMarketOrderDto? Order,
    IReadOnlyList<DaoxuanMarketFillDto> Fills,
    DaoxuanPlayerSnapshotDto? Snapshot);

public sealed record DaoxuanMarketPlaceRequest(
    string OrderId,
    string ItemId,
    string Side,
    long Price,
    long Quantity,
    int EnhancementLevel = 0);

public sealed record DaoxuanMarketCancelRequest(string OrderId);

public static class DaoxuanPacketCodec
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };

    public static byte[] Encode<T>(string kind, string requestId, long sequence, T payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new DaoxuanEnvelope<T>(kind, requestId, sequence, payload),
            JsonOptions);
    }

    public static bool TryDecode(ReadOnlyMemory<byte> bytes, out DaoxuanIncomingPacket packet)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement)
                || !root.TryGetProperty("payload", out var payloadElement))
            {
                packet = default!;
                return false;
            }

            packet = new DaoxuanIncomingPacket(
                kindElement.GetString() ?? string.Empty,
                root.TryGetProperty("requestId", out var requestIdElement)
                    ? requestIdElement.GetString() ?? string.Empty
                    : string.Empty,
                root.TryGetProperty("sequence", out var sequenceElement) && sequenceElement.TryGetInt64(out var sequence)
                    ? sequence
                    : 0,
                payloadElement.Clone());
            return true;
        }
        catch (JsonException)
        {
            packet = default!;
            return false;
        }
    }

    public static byte[] EncodeCommand<T>(string requestId, string command, T payload)
    {
        return Encode(DaoxuanPacketKinds.Command, requestId, 0, new { command, payload });
    }

    public static byte[] EncodeHello(string requestId, HelloRequest payload)
    {
        return Encode(DaoxuanPacketKinds.Hello, requestId, 0, payload);
    }
}

public sealed record RegisterAccountRequest(
    string Account,
    string Password,
    string? CharacterName = null);

public sealed record LoginAccountRequest(
    string Account,
    string Password);

public sealed record AuthResponse(
    bool Success,
    string? Account,
    string? CharacterName,
    string? PlayerId,
    long SequenceId,
    string? Message = null);
