// ==============================================================================
// 文件名称: CombatSimulator.cs
// 主要职责: 无帧、可复现的离散事件战斗模拟器入口与单体战斗模拟。
// 核心逻辑 / 定义内容:
//   1. 玩家实时战斗状态（CreatePlayerState）与综合属性面板（CreatePlayerAttributes）构建。
//   2. 单体遭遇战斗推演（RunSingleEncounter）与对决主入口（RunEncounter）。
//   3. 战斗 Buff 运行时构建与生命周期管理。
// ==============================================================================

using Domain.Models;


namespace Domain.Simulation;

/// <summary>
/// 无帧、可复现的战斗事件模拟器。
/// 只在攻击、施法、复活和消耗品触发等边界推进，离线快进不会按秒创建事件。
/// </summary>
public static partial class CombatSimulator
{
    private const int MaxEventsPerRun = 2_000_000;
    private const int MaxResultEvents = 4_096;
    private const int RegenTickMilliseconds = 5_000;
    private const int ConsumableRecoveryTickMilliseconds = 5_000;
    private const int DamageOverTimeTickMilliseconds = 3_000;
    private const long InfiniteTime = long.MaxValue / 4;

    /// <summary>
    /// 根据玩家当前战斗配置、活跃 Buff 及境界修为，构建战斗内部的玩家实时状态。
    /// </summary>
    /// <param name="loadout">玩家战斗配置装配。</param>
    /// <param name="activeBuffs">生效中的 Buff 列表。</param>
    /// <param name="at">当前时间戳。</param>
    /// <param name="hitpoints">指定的初始生命值（为空则默认满血）。</param>
    /// <param name="manapoints">指定的初始法力值（为空则默认满蓝）。</param>
    /// <param name="spirit">玩家持有的修为总点数。</param>
    /// <returns>玩家战斗实时状态对象。</returns>
    public static CombatPlayerState CreatePlayerState(
        CombatLoadout loadout,
        IReadOnlyList<BuffState> activeBuffs,
        DateTimeOffset at,
        long? hitpoints = null,
        long? manapoints = null,
        long spirit = 0)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(activeBuffs);
        var buffs = CreateBuffRuntime(activeBuffs, at);
        var player = BuildPlayerStats(loadout, buffs, 0, spirit);
        return new CombatPlayerState(
            Math.Clamp(hitpoints ?? player.MaxHitpoints, 0, player.MaxHitpoints),
            player.MaxHitpoints,
            Math.Clamp(manapoints ?? player.MaxManapoints, 0, player.MaxManapoints),
            player.MaxManapoints,
            (hitpoints ?? player.MaxHitpoints) <= 0);
    }

    /// <summary>
    /// 根据玩家战斗配置、Buff 及修为，计算并导出对外的角色综合战斗属性面板。
    /// </summary>
    /// <param name="loadout">玩家战斗配置方案。</param>
    /// <param name="activeBuffs">生效中的 Buff 列表。</param>
    /// <param name="at">当前时间戳。</param>
    /// <param name="spirit">玩家持有的修为总点数。</param>
    /// <returns>战斗属性面板状态快照。</returns>
    public static CombatAttributeState CreatePlayerAttributes(
        CombatLoadout loadout,
        IReadOnlyList<BuffState> activeBuffs,
        DateTimeOffset at,
        long spirit = 0)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(activeBuffs);
        var buffs = CreateBuffRuntime(activeBuffs, at);
        var player = BuildPlayerStats(loadout, buffs, 0, spirit);
        return new CombatAttributeState(
            player.MaxHitpoints,
            player.MaxManapoints,
            player.AttackIntervalMilliseconds,
            player.AccuracyRating,
            player.MaxDamage,
            player.EvasionRating,
            player.Armor,
            player.MagicResistance,
            player.MetalResistance,
            player.WoodResistance,
            player.WaterResistance,
            player.FireResistance,
            player.EarthResistance,
            player.Style,
            player.Category,
            player.Element,
            player.Tenacity,
            player.HpRegenPerTenSeconds,
            player.MpRegenPerTenSeconds,
            player.PenetrationRatio,
            player.LifeStealRatio,
            player.ArmorDamageRatio,
            player.HpDrainRatio,
            player.DamageTakenRatio,
            player.CriticalChance,
            player.CriticalDamageRatio,
            player.CriticalAvoidance,
            player.DamageAbsorptionRatio,
            BreakRating: player.BreakRating,
            ParryRating: player.ParryRating,
            SkillIntensity: player.SkillIntensity,
            DynamicResistanceFactorK: player.DynamicResistanceFactorK,
            ActionSpeedReductionRatio: player.ActionSpeedReductionRatio,
            CooldownReductionRatio: player.CooldownReductionRatio,
            CriticalRating: player.CriticalRating,
            CriticalAvoidanceRating: player.CriticalAvoidanceRating);
    }

    /// <summary>
    /// 根据战斗入场固定快照（BattleSnapshot）执行单场战斗模拟推演。
    /// 包含配置指纹一致性校验与确定性重放。
    /// </summary>
    /// <param name="playerId">玩家 GUID。</param>
    /// <param name="snapshot">战斗入场快照。</param>
    /// <param name="duration">推进时长。</param>
    /// <param name="playerSpirit">玩家当前境界修为点数。</param>
    /// <param name="stopAfterFirstVictory">是否在首次胜利或结算时终止。</param>
    /// <returns>战斗运行结算结果。</returns>
    public static CombatRunResult Run(
        Guid playerId,
        BattleSnapshot snapshot,
        TimeSpan duration,
        long playerSpirit = 0,
        bool stopAfterFirstVictory = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.Equals(snapshot.ConfigFingerprint, GameData.GameDataCatalog.Current.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"战斗快照配置指纹不一致: 快照指纹 '{snapshot.ConfigFingerprint}' 与当前配置指纹 '{GameData.GameDataCatalog.Current.Fingerprint}' 不匹配，拒绝继续推进。");
        }

        var initialPlayer = CreatePlayerState(
            snapshot.BattleLoadout,
            snapshot.BattleBuffs,
            snapshot.BattleStartedAt,
            snapshot.InitialHitpoints,
            snapshot.InitialManapoints,
            playerSpirit);

        return Run(
            playerId,
            snapshot.BattleLoadout,
            snapshot.BattleBuffs,
            snapshot.BattleInventory,
            snapshot.EncounterId,
            snapshot.BattleStartedAt,
            duration,
            snapshot.CompletedEncountersBeforeBattle,
            initialPlayer,
            stopAfterFirstOutcome: false,
            stopAfterFirstVictory: stopAfterFirstVictory,
            playerSpirit: playerSpirit,
            initialAbilityCooldowns: snapshot.InitialAbilityCooldowns,
            initialConsumableCooldowns: snapshot.InitialConsumableCooldowns);
    }

    /// <summary>
    /// 执行确定性战斗事件模拟推演。
    /// </summary>
    /// <param name="playerId">玩家唯一 GUID。</param>
    /// <param name="loadout">玩家战斗配置方案。</param>
    /// <param name="activeBuffs">生效中的 Buff 状态列表。</param>
    /// <param name="inventory">背包物品持有字典。</param>
    /// <param name="encounterId">战斗遭遇副本 ID。</param>
    /// <param name="startedAt">模拟起始时间。</param>
    /// <param name="duration">推进持续时长。</param>
    /// <param name="completedEncountersBeforeRun">此前累计通关遭遇次数。</param>
    /// <param name="initialPlayerState">入场初始玩家状态。</param>
    /// <param name="stopAfterFirstOutcome">产生首次胜负结果后是否立即停止推进。</param>
    /// <param name="stopAfterFirstVictory">首次取得胜利后是否立即停止推进。</param>
    /// <param name="playerSpirit">玩家修为总点数。</param>
    /// <param name="initialAbilityCooldowns">初始技能冷却剩余毫秒字典。</param>
    /// <param name="initialConsumableCooldowns">初始消耗品槽位冷却剩余毫秒列表。</param>
    /// <returns>战斗模拟推演执行结果。</returns>
    public static CombatRunResult Run(
        Guid playerId,
        CombatLoadout loadout,
        IReadOnlyList<BuffState> activeBuffs,
        IReadOnlyDictionary<string, long> inventory,
        string encounterId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        long completedEncountersBeforeRun = 0,
        CombatPlayerState? initialPlayerState = null,
        bool stopAfterFirstOutcome = false,
        bool stopAfterFirstVictory = false,
        long playerSpirit = 0,
        IReadOnlyDictionary<string, long>? initialAbilityCooldowns = null,
        IReadOnlyList<long>? initialConsumableCooldowns = null)
    {
        ArgumentNullException.ThrowIfNull(loadout);
        ArgumentNullException.ThrowIfNull(activeBuffs);
        ArgumentNullException.ThrowIfNull(inventory);

        if (!CombatCatalog.TryGetEncounter(encounterId, out var encounter))
        {
            throw new KeyNotFoundException($"未知战斗遭遇: {encounterId}");
        }

        return RunGroupEncounter(
            playerId,
            loadout,
            activeBuffs,
            inventory,
            encounter,
            startedAt,
            duration,
            completedEncountersBeforeRun,
            initialPlayerState,
            stopAfterFirstOutcome,
            stopAfterFirstVictory,
            playerSpirit,
            initialAbilityCooldowns,
            initialConsumableCooldowns);
    }


    private static IReadOnlyDictionary<string, long> CreatePlayerAbilityCooldowns(
        CombatLoadout loadout,
        IReadOnlyList<long> abilityReadyAt,
        long simulatedMilliseconds)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var index = 0; index < loadout.AbilitySlots.Count && index < abilityReadyAt.Count; index++)
        {
            var abilityId = loadout.AbilitySlots[index].AbilityId;
            if (string.IsNullOrWhiteSpace(abilityId)) continue;
            var remaining = Math.Max(0, abilityReadyAt[index] - simulatedMilliseconds);
            if (remaining <= 0) continue;
            result[abilityId] = Math.Max(result.GetValueOrDefault(abilityId), remaining);
        }
        return result;
    }

    private static IReadOnlyList<long> CreatePlayerConsumableCooldowns(
        IReadOnlyList<long> nextConsumableUse,
        long simulatedMilliseconds)
    {
        return nextConsumableUse
            .Select(readyAt => Math.Max(0, readyAt - simulatedMilliseconds))
            .ToArray();
    }

    private static CombatActionState CreateActionState(
        string name,
        CombatActionKind kind,
        long startedAtMilliseconds,
        long completesAtMilliseconds,
        long simulatedMilliseconds)
    {
        var duration = Math.Max(1, completesAtMilliseconds - startedAtMilliseconds);
        var elapsed = Math.Clamp(simulatedMilliseconds - startedAtMilliseconds, 0, duration);
        return new CombatActionState(name, kind, elapsed, duration);
    }

    private static void ApplyConsumables(
        long now,
        CombatLoadout loadout,
        IDictionary<string, long> inventory,
        IDictionary<string, long> consumed,
        Dictionary<string, BuffRuntime> buffs,
        long enemyHitpoints,
        long enemyMaxHitpoints,
        long enemyManapoints,
        long enemyMaxManapoints,
        ref long hitpoints,
        ref long manapoints,
        long maxHitpoints,
        long maxManapoints,
        long[] nextConsumableUse,
        ICollection<RecoveryRuntime> recoveries,
        ref long hitpointsRecoveredFromConsumables,
        ref long manapointsRecoveredFromConsumables,
        IList<string> events,
        CombatControlRuntime selfControl,
        CombatControlRuntime targetControl)
    {
        for (var index = 0; index < loadout.ConsumableSlots.Count; index++)
        {
            var slot = loadout.ConsumableSlots[index];
            if (!slot.Enabled || string.IsNullOrWhiteSpace(slot.ItemId) || now < nextConsumableUse[index]) continue;
            if (!ItemCatalog.TryGet(slot.ItemId, out var item) || item.Consumable is null) continue;
            var configuredTriggers = slot.Triggers ?? [];
            var isFood = item.Category == ItemCategory.Food;
            var isTriggeredAtStart = isFood && item.Consumable.TriggerAtCombatStart && now == 0;
            var areTriggersActive = isTriggeredAtStart || AreTriggersActive(
                configuredTriggers,
                item.Consumable.DefaultCombatTriggers,
                hitpoints,
                maxHitpoints,
                manapoints,
                maxManapoints,
                enemyHitpoints,
                enemyMaxHitpoints,
                enemyManapoints,
                enemyMaxManapoints,
                buffs,
                selfControl,
                targetControl,
                now);

            if (!areTriggersActive)
            {
                continue;
            }

            if (isFood
                && !isTriggeredAtStart
                && configuredTriggers.Count == 0
                && item.Consumable.DefaultCombatTriggers.Count == 0
                && hitpoints > Math.Floor(maxHitpoints * item.Consumable.TriggerHpRatio))
            {
                continue;
            }
            if (TryConsume(
                    slot.ItemId,
                    slot.Quantity,
                    ActionType.Combat,
                    inventory,
                    consumed,
                    buffs,
                    now,
                    maxHitpoints,
                    maxManapoints,
                    ref hitpoints,
                    ref manapoints,
                    recoveries,
                    ref hitpointsRecoveredFromConsumables,
                    ref manapointsRecoveredFromConsumables,
                    events))
            {
                nextConsumableUse[index] = checked(now + Math.Max(1, item.Consumable.CooldownSeconds * 1_000L));
            }
        }
    }

    private static bool TryConsume(
        string itemId,
        int quantity,
        ActionType actionType,
        IDictionary<string, long> inventory,
        IDictionary<string, long> consumed,
        Dictionary<string, BuffRuntime> buffs,
        long now,
        long maxHitpoints,
        long maxManapoints,
        ref long hitpoints,
        ref long manapoints,
        ICollection<RecoveryRuntime> recoveries,
        ref long hitpointsRecoveredFromConsumables,
        ref long manapointsRecoveredFromConsumables,
        IList<string> events)
    {
        if (quantity < 1
            || !ItemCatalog.TryGet(itemId, out var item)
            || item.Consumable is null
            || !item.Consumable.UsableInActionTypes.Contains(actionType)
            || GetAmount(inventory, itemId) < quantity)
        {
            return false;
        }

        inventory[itemId] = GetAmount(inventory, itemId) - quantity;
        consumed[itemId] = checked(GetAmount(consumed, itemId) + quantity);
        var totalHitpointRestore = checked(item.Consumable.HitpointRestore * quantity);
        var totalManapointRestore = checked(item.Consumable.ManapointRestore * quantity);
        if (item.Consumable.RecoverySeconds > 0
            && (totalHitpointRestore > 0 || totalManapointRestore > 0))
        {
            var recoveryDurationMilliseconds = checked(item.Consumable.RecoverySeconds * 1_000L);
            var recoveryTicks = Math.Max(
                1,
                (int)Math.Ceiling(recoveryDurationMilliseconds / (double)ConsumableRecoveryTickMilliseconds));
            recoveries.Add(new RecoveryRuntime(
                checked(now + Math.Min(ConsumableRecoveryTickMilliseconds, recoveryDurationMilliseconds)),
                recoveryTicks,
                totalHitpointRestore,
                totalManapointRestore));
        }
        else
        {
            var restoredHitpoints = Math.Min(
                Math.Max(0, maxHitpoints - hitpoints),
                totalHitpointRestore);
            var restoredManapoints = Math.Min(
                Math.Max(0, maxManapoints - manapoints),
                totalManapointRestore);
            hitpoints = checked(hitpoints + restoredHitpoints);
            manapoints = checked(manapoints + restoredManapoints);
            hitpointsRecoveredFromConsumables = checked(
                hitpointsRecoveredFromConsumables + restoredHitpoints);
            manapointsRecoveredFromConsumables = checked(
                manapointsRecoveredFromConsumables + restoredManapoints);
        }

        foreach (var effect in item.Consumable.Buffs)
        {
            if (!buffs.TryGetValue(effect.BuffId, out var buff))
            {
                buff = new BuffRuntime(
                    effect.BuffId,
                    effect.Name,
                    0,
                    Math.Max(1, effect.MaxStacks),
                    effect.QiGainMultiplier,
                    effect.AttackLevelRatio,
                    effect.DamageRatio,
                    effect.AttackSpeedRatio,
                    itemId,
                    0);
                buffs[effect.BuffId] = buff;
            }

            buff.Stacks = effect.Stackable
                ? Math.Min(buff.MaxStacks, buff.Stacks + 1)
                : 1;
            buff.ExpiresAtMilliseconds = checked(now + Math.Max(1, effect.DurationSeconds * 1_000L));
        }

        if (events.Count < MaxResultEvents)
        {
            events.Add($"使用 {{{{item:{item.Id}|{item.Name}}}}} × {quantity}");
        }

        return true;
    }

    private static Dictionary<string, BuffRuntime> CreateBuffRuntime(
        IReadOnlyList<BuffState> activeBuffs,
        DateTimeOffset startedAt)
    {
        var result = new Dictionary<string, BuffRuntime>(StringComparer.Ordinal);
        foreach (var buff in activeBuffs)
        {
            var remaining = (long)Math.Floor((buff.ExpiresAt - startedAt).TotalMilliseconds);
            if (remaining <= 0) continue;
            result[buff.BuffId] = new BuffRuntime(
                buff.BuffId,
                buff.Name,
                Math.Max(1, buff.Stacks),
                Math.Max(1, buff.Stacks),
                buff.QiGainMultiplier,
                buff.AttackLevelRatio,
                buff.DamageRatio,
                buff.AttackSpeedRatio,
                buff.SourceItemId,
                remaining,
                buff.Modifiers);
        }

        return result;
    }

    private static CombatStats BuildPlayerStats(
        CombatLoadout loadout,
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        long now,
        long spirit = 0)
    {
        var basePool = CombatFormula.CalculateBaseAttributes(spirit);
        var equipment = CombatCatalog.AggregateEquipmentStats(loadout.Equipment);
        var activeBuffList = buffs.Values.Where(buff => buff.ExpiresAtMilliseconds > now).ToList();
        var attackRatio = activeBuffList.Sum(buff => buff.AttackLevelRatio * buff.Stacks)
            + activeBuffList.Sum(buff => buff.GetModifier(Domain.Constants.StatKeys.AccuracyRatio, 0.0) * buff.Stacks)
            + equipment.AccuracyRatio;
        var damageRatio = activeBuffList.Sum(buff => buff.DamageRatio * buff.Stacks)
            + equipment.DamageRatio;
        var attackSpeedRatio = activeBuffList.Sum(buff => buff.AttackSpeedRatio * buff.Stacks)
            + equipment.AttackSpeedRatio;
        var stamina = Math.Max(10, loadout.StaminaLevel);
        var intelligence = Math.Max(10, loadout.IntelligenceLevel);
        var attack = Math.Max(10, loadout.AttackLevel);
        var defense = Math.Max(10, loadout.DefenseLevel);
        var swordsmanship = Math.Max(10, loadout.SwordsmanshipLevel);
        var talisman = Math.Max(10, loadout.TalismanLevel);
        var combatStyle = equipment.Style ?? CombatStyle.Body;

        // 六维根基百分比加成乘区 (每点提供 1% 对应的领域增益)
        var staminaBonus = stamina * 0.01;
        var intelligenceBonus = intelligence * 0.01;
        var defenseBonus = defense * 0.01;
        var attackBonus = attack * 0.01;
        var swordsmanshipBonus = swordsmanship * 0.01;
        var talismanBonus = talisman * 0.01;

        // 1. 生命与灵力 (以当前境界基础池为底座，叠加六维乘区加成)
        var maxHitpoints = (long)Math.Max(1, Math.Ceiling(basePool.BaseHitpoints * (1.0 + staminaBonus) * (1.0 + defenseBonus)));
        var maxManapoints = (long)Math.Max(1, Math.Ceiling(basePool.BaseManapoints * (1.0 + intelligenceBonus) * (1.0 + defenseBonus)));

        // 2. 命中与闪避 (神识 +% 命中乘区，身法 +% 闪避乘区)
        var accuracy = basePool.BaseAccuracy * (1.0 + swordsmanshipBonus) * Math.Max(0.01, 1 + attackRatio);
        var evasion = basePool.BaseEvasion * (1.0 + talismanBonus) * Math.Max(0.01, 1 + equipment.EvasionRatio + attackRatio * 0.5);

        // 3. 攻击力 (根据流派决定物攻/法攻基础池与加成；力道 +% 物攻，神识 +2% 法攻)
        var isMagic = combatStyle == CombatStyle.FiveElements;
        var baseDmg = isMagic ? basePool.BaseSpellAttack : basePool.BasePhysicalAttack;
        var scaledAttack = isMagic
            ? baseDmg * (1.0 + swordsmanshipBonus * 2.0)
            : baseDmg * (1.0 + attackBonus);
        var maxDamage = scaledAttack * Math.Max(0.01, 1.0 + damageRatio);

        // 4. 双抗与五行抗性 (体魄 +% 物理护甲，经脉 +% 术法抗性与五行抗性；装备为加法底座)
        var armor = (basePool.BaseArmor + equipment.Armor) * (1.0 + staminaBonus);
        var magicResistance = (basePool.BaseMagicResistance + equipment.MagicResistance) * (1.0 + intelligenceBonus);
        var metalResistance = (basePool.BaseMagicResistance + equipment.MetalResistance) * (1.0 + intelligenceBonus);
        var woodResistance = (basePool.BaseMagicResistance + equipment.WoodResistance) * (1.0 + intelligenceBonus);
        var waterResistance = (basePool.BaseMagicResistance + equipment.WaterResistance) * (1.0 + intelligenceBonus);
        var fireResistance = (basePool.BaseMagicResistance + equipment.FireResistance) * (1.0 + intelligenceBonus);
        var earthResistance = (basePool.BaseMagicResistance + equipment.EarthResistance) * (1.0 + intelligenceBonus);
        // 5. 破招与拆招 (力道 +% 破招，根骨 +% 化解 + 身法 +% 化解)
        var breakRating = (basePool.BaseBreak + loadout.BreakRating + equipment.BreakRating) * (1.0 + attackBonus);
        var parryRating = (basePool.BaseParry + loadout.ParryRating + equipment.ParryRating) * (1.0 + defenseBonus + talismanBonus);

        // 6. 暴击与免爆 (力道 +% 暴击，身法 +% 免爆；装备百分比按 K=100 折算为点数)
        var criticalRating = Math.Max(0, (basePool.BaseCritical + (loadout.CriticalChance + equipment.CriticalChance) * 100.0) * (1.0 + attackBonus));
        var criticalAvoidanceRating = Math.Max(0, (basePool.BaseCriticalAvoidance + (loadout.CriticalAvoidance + equipment.CriticalAvoidance) * 100.0) * (1.0 + talismanBonus));
        var criticalChance = CombatFormula.CalculateCritContestRate(criticalRating, criticalAvoidanceRating);
        var criticalAvoidance = CombatFormula.CalculateCritContestRate(criticalAvoidanceRating, criticalRating);

        // 7. 回复速率 (每 5 秒基础回复 2% 基础上限，底层属性记录 10 秒比率)
        var baseHpRegenRatio = basePool.BaseHitpoints > 0 ? (basePool.BaseHpRegen * 2.0) / basePool.BaseHitpoints : 0.04;
        var baseMpRegenRatio = basePool.BaseManapoints > 0 ? (basePool.BaseMpRegen * 2.0) / basePool.BaseManapoints : 0.02;
        var hpRegen = Math.Max(0.0, (baseHpRegenRatio + loadout.HpRegenPerTenSeconds) * (1.0 + staminaBonus));
        var mpRegen = Math.Max(0.0, (baseMpRegenRatio + loadout.MpRegenPerTenSeconds) * (1.0 + intelligenceBonus));

        // 8. 攻击间隔
        var baseInterval = equipment.AttackIntervalMilliseconds > 0
            ? equipment.AttackIntervalMilliseconds
            : 1_000;
        var interval = CombatFormula.CalculateAttackIntervalMilliseconds(baseInterval, attackSpeedRatio);

        // 9. 高级机制 (高级机制为独立词条属性体系，动态抗性因子 K 由当前阶位决定)
        var cdr = Math.Clamp(loadout.CooldownReductionRatio + equipment.CooldownReductionRatio, 0, 0.75);
        var atr = Math.Clamp(loadout.ActionSpeedReductionRatio + equipment.ActionSpeedRatio, 0, 0.50);
        var critDamageRatio = Math.Max(0.0, loadout.CriticalDamageRatio + equipment.CriticalDamageRatio);
        var skillIntensity = Math.Max(1.0, loadout.SkillIntensity + equipment.SkillIntensity);
        var kFactor = Math.Max((double)basePool.Tier, loadout.DynamicResistanceFactorK);

        return new CombatStats(
            maxHitpoints,
            maxManapoints,
            interval,
            accuracy,
            maxDamage,
            evasion,
            armor,
            magicResistance,
            metalResistance,
            woodResistance,
            waterResistance,
            fireResistance,
            earthResistance,
            combatStyle,
            equipment.Category ?? DamageCategory.Physical,
            equipment.Element ?? Element.None,
            null,
            Math.Max(0, loadout.Tenacity + equipment.Tenacity),
            hpRegen,
            mpRegen,
            Math.Max(0, loadout.PenetrationRatio + equipment.PenetrationRatio),
            Math.Max(0, loadout.LifeStealRatio + equipment.LifeStealRatio),
            Math.Max(0, loadout.ArmorDamageRatio + equipment.ArmorDamageRatio),
            Math.Max(0, loadout.HpDrainRatio + equipment.HpDrainRatio),
            0,
            criticalChance,
            critDamageRatio,
            criticalAvoidance,
            Math.Clamp(loadout.DamageAbsorptionRatio + equipment.DamageAbsorptionRatio, 0, 1),
            Math.Max(0, breakRating),
            Math.Max(0, parryRating),
            skillIntensity,
            kFactor,
            atr,
            cdr,
            criticalRating,
            criticalAvoidanceRating,
            basePool.BaseHpRegen * (1.0 + staminaBonus),
            basePool.BaseMpRegen * (1.0 + intelligenceBonus));
    }

    private static CombatStats BuildEnemyStats(
        CombatantDefinition definition)
    {
        return new CombatStats(
            definition.MaxHitpoints,
            definition.MaxManapoints,
            definition.AttackIntervalMilliseconds,
            definition.AccuracyRating,
            definition.MaxDamage,
            definition.DefensiveMaxDamage,
            definition.Armor,
            definition.MagicResistance,
            definition.MetalResistance,
            definition.WoodResistance,
            definition.WaterResistance,
            definition.FireResistance,
            definition.EarthResistance,
            definition.Style,
            definition.Category,
            definition.Element,
            definition,
            Math.Max(0, definition.Tenacity),
            Math.Max(0, definition.HpRegenPerTenSeconds),
            Math.Max(0, definition.MpRegenPerTenSeconds),
            0,
            Math.Max(0, definition.LifeStealRatio),
            Math.Max(0, definition.ArmorDamageRatio),
            Math.Max(0, definition.HpDrainRatio),
            0,
            Math.Clamp(definition.CriticalChance, 0, 1),
            Math.Max(0, definition.CriticalDamageRatio),
            Math.Clamp(definition.CriticalAvoidance, 0, 1),
            Math.Clamp(definition.DamageAbsorptionRatio, 0, 1),
            Math.Max(0, definition.BreakRating),
            Math.Max(0, definition.ParryRating),
            Math.Max(1.0, definition.SkillIntensity),
            Math.Max(1.0, definition.DynamicResistanceFactorK),
            Math.Clamp(definition.ActionSpeedReductionRatio, 0, 0.99),
            Math.Clamp(definition.CooldownReductionRatio, 0, 0.99),
            Math.Max(0, definition.CriticalRating > 0 ? definition.CriticalRating : definition.CriticalChance * 100.0),
            Math.Max(0, definition.CriticalAvoidance > 0 ? definition.CriticalAvoidance : 0.0),
            Math.Max(0, definition.HpRegen > 0 ? definition.HpRegen : Math.Max(1.0, Math.Round(definition.MaxHitpoints * 0.02))),
            Math.Max(0, definition.MpRegen > 0 ? definition.MpRegen : (definition.MaxManapoints > 0 ? Math.Max(1.0, Math.Round(definition.MaxManapoints * 0.02)) : 0.0)));
    }

    private static CombatResolution ResolvePlayerAttack(
        CombatStats source,
        CombatStats target,
        CombatAbilityDefinition? ability,
        ref long mana,
        ref DeterministicRandom random)
    {
        if (ability is null || mana < ability.ManaCost)
        {
            return ResolveDamage(source, target, null, ref random);
        }

        mana -= ability.ManaCost;
        return ability.EffectType == CombatAbilityEffectType.Heal
            ? new CombatResolution(
                0,
                Math.Max(0, checked((long)Math.Floor(
                    source.MaxDamage * Math.Max(0, ability.HealingRatio) + ability.FlatHealing))))
            : ResolveDamage(source, target, ability, ref random);
    }

    private static CombatResolution ResolveEnemyAttack(
        CombatStats source,
        CombatStats target,
        CombatAbilityDefinition? ability,
        ref long mana,
        ref DeterministicRandom random)
    {
        if (ability is null || mana < ability.ManaCost)
        {
            return ResolveDamage(source, target, null, ref random);
        }

        mana -= ability.ManaCost;
        return ability.EffectType == CombatAbilityEffectType.Heal
            ? new CombatResolution(
                0,
                Math.Max(0, checked((long)Math.Floor(
                    source.MaxDamage * Math.Max(0, ability.HealingRatio) + ability.FlatHealing))))
            : ResolveDamage(source, target, ability, ref random);
    }

    private static CombatResolution ResolveDamage(
        CombatStats source,
        CombatStats target,
        CombatAbilityDefinition? ability,
        ref DeterministicRandom random)
    {
        // 1. 破招 vs 拆招 对抗判定
        var isBreak = false;
        var isParry = false;
        if (source.BreakRating > target.ParryRating && source.BreakRating > 0)
        {
            var breakRate = CombatFormula.CalculateContestRate(source.BreakRating, target.ParryRating);
            if (random.NextDouble() < breakRate)
            {
                isBreak = true;
            }
        }
        else if (target.ParryRating > source.BreakRating && target.ParryRating > 0)
        {
            var parryRate = CombatFormula.CalculateContestRate(target.ParryRating, source.BreakRating);
            if (random.NextDouble() < parryRate)
            {
                isParry = true;
            }
        }

        // 破招成功：命中与暴击点数翻倍后再进对抗；拆招成功：闪避点数与抗性效果翻倍
        var effectiveHit = source.AccuracyRating * (isBreak ? CombatConstants.BreakSuccessEffectMultiplier : 1.0);
        var effectiveCrit = Math.Max(0.0, source.CriticalRating) * (isBreak ? CombatConstants.BreakSuccessEffectMultiplier : 1.0);
        var effectiveEva = target.EvasionRating * (isParry ? CombatConstants.ParrySuccessEffectMultiplier : 1.0);

        // 2. 命中 vs 闪避 统一对抗判定 (规约 §7.3)
        var hitChance = CombatFormula.CalculateHitContestRate(effectiveHit, effectiveEva);
        if (random.NextDouble() > hitChance)
        {
            return new CombatResolution(0, 0, DidHit: false);
        }

        // 3. 基础伤害与技能强度放大
        var maximum = Math.Max(1, (long)Math.Floor(source.MaxDamage));
        var minimum = Math.Max(1, maximum / 2);
        var rolled = random.NextInt64(minimum, checked(maximum + 1));
        var damageRatio = ability?.DamageRatio ?? 1;
        var flatDamage = ability?.FlatDamage ?? 0;
        var armorDamage = source.Armor * Math.Max(0, source.ArmorDamageRatio + (ability?.ArmorDamageRatio ?? 0));
        var skillIntensity = Math.Max(1.0, source.SkillIntensity);
        var amplifiedDouble = (rolled * Math.Max(0, damageRatio) + flatDamage) * skillIntensity + armorDamage;

        // 4. 第一层：大类防御（物理护甲 vs 术法抗性）减免
        var category = ability?.Category ?? source.Category;
        var element = ability?.Element ?? source.Element;
        var baseResistance = category == DamageCategory.Spell ? target.MagicResistance : target.Armor;
        var effectiveResistance = baseResistance * (isParry ? CombatConstants.ParrySuccessEffectMultiplier : 1.0);
        var penetrationRatio = Math.Max(0, source.PenetrationRatio + (ability?.PenetrationRatio ?? 0));
        var kFactor = Math.Max(1.0, source.DynamicResistanceFactorK);

        var rawDamageLong = amplifiedDouble >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1, (long)Math.Floor(amplifiedDouble));
        var mitigated = CombatFormula.CalculateMitigatedDamageWithResistanceFactor(
            rawDamageLong,
            effectiveResistance,
            penetrationRatio,
            kFactor,
            CombatConstants.DefaultDamageBaseFactor);

        // 5. 第二层：五行属性增伤与五行抗性减免
        var afterElement = (double)mitigated;
        if (element != Element.None)
        {
            var elementResistance = GetElementResistance(target, element);
            var elementMitigation = elementResistance > 0
                ? elementResistance / (elementResistance + 100.0 * kFactor)
                : 0.0;
            afterElement = mitigated * Math.Max(0.0, 1.0 - elementMitigation);
        }

        // 6. 暴击 vs 免爆 统一对抗判定 (规约 §7.3) 与伤害放大
        var criticalChance = CombatFormula.CalculateCritContestRate(effectiveCrit, target.CriticalAvoidanceRating);
        var isCritical = source.CriticalDamageRatio > 0
            && criticalChance > 0
            && random.NextDouble() < criticalChance;
        var critMultiplier = CombatConstants.BaseCriticalDamageMultiplier + source.CriticalDamageRatio;
        var withCrit = isCritical
            ? afterElement * critMultiplier
            : afterElement;

        // 7. 易伤、减伤与伤害吸收
        var withDamageTaken = withCrit * Math.Max(0, 1 + target.DamageTakenRatio);
        var absorbed = CombatFormula.ApplyDamageAbsorption(
            withDamageTaken,
            target.DamageAbsorptionRatio);
        var damage = absorbed >= long.MaxValue
            ? long.MaxValue
            : Math.Max(0, (long)Math.Ceiling(absorbed));

        // 8. 吸血与汲取
        var drainRatio = ability is null
            ? source.LifeStealRatio
            : source.HpDrainRatio + ability.HpDrainRatio;
        var drainHealing = damage > 0 && drainRatio > 0
            ? Math.Max(0, (long)Math.Floor(damage * drainRatio))
            : 0;
        return new CombatResolution(damage, 0, true, drainHealing, isCritical);
    }

    private static double GetElementResistance(CombatStats target, Element element)
    {
        return element switch
        {
            Element.Metal => target.MetalResistance,
            Element.Wood => target.WoodResistance,
            Element.Water => target.WaterResistance,
            Element.Fire => target.FireResistance,
            Element.Earth => target.EarthResistance,
            _ => 0
        };
    }

    private static CombatAbilityDefinition? SelectAbility(
        CombatLoadout loadout,
        IReadOnlyList<CombatAbilityDefinition?> abilities,
        IReadOnlyList<long> readyAt,
        long hitpoints,
        long maxHitpoints,
        long manapoints,
        long maxManapoints,
        long targetHitpoints,
        long targetMaxHitpoints,
        long targetManapoints,
        long targetMaxManapoints,
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        CombatControlRuntime selfControl,
        CombatControlRuntime targetControl,
        long now,
        out int selectedIndex)
    {
        selectedIndex = -1;
        if (selfControl.IsStunned(now) || selfControl.IsSilenced(now)) return null;
        for (var index = 0; index < loadout.AbilitySlots.Count; index++)
        {
            var slot = loadout.AbilitySlots[index];
            var configuredTriggers = slot.Triggers ?? [];
            var ability = abilities[index];
            if (ability is null || now < readyAt[index])
            {
                continue;
            }
            if (manapoints < ability.ManaCost
                || !AreTriggersActive(
                    configuredTriggers,
                    ability.DefaultTriggers,
                    hitpoints,
                    maxHitpoints,
                    manapoints,
                    maxManapoints,
                    targetHitpoints,
                    targetMaxHitpoints,
                    targetManapoints,
                    targetMaxManapoints,
                    buffs,
                    selfControl,
                    targetControl,
                    now)
                || (configuredTriggers.Count == 0
                    && ability.DefaultTriggers.Count == 0
                    && hitpoints > Math.Floor(maxHitpoints * ability.TriggerHpRatio)))
            {
                continue;
            }

            selectedIndex = index;
            return ability;
        }

        return null;
    }

    private static CombatAbilityDefinition? SelectEnemyAbility(
        CombatStats enemy,
        IReadOnlyList<long> readyAt,
        long selfHitpoints,
        long selfMaxHitpoints,
        long manapoints,
        long targetHitpoints,
        long targetMaxHitpoints,
        long targetManapoints,
        long targetMaxManapoints,
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        CombatControlRuntime selfControl,
        CombatControlRuntime targetControl,
        long now,
        out int selectedIndex)
    {
        selectedIndex = -1;
        if (enemy.Definition is null) return null;

        for (var index = 0; index < enemy.Definition.Abilities.Count; index++)
        {
            var ability = enemy.Definition.Abilities[index];
            if (now < readyAt[index]
                || manapoints < ability.ManaCost) continue;

            if (ability.TriggerHpRatio < 1.0 && selfMaxHitpoints > 0)
            {
                var currentHpRatio = selfHitpoints / (double)selfMaxHitpoints;
                if (currentHpRatio > ability.TriggerHpRatio)
                {
                    continue;
                }
            }

            if (!AreTriggersActive(
                    [],
                    ability.DefaultTriggers,
                    selfHitpoints,
                    selfMaxHitpoints,
                    manapoints,
                    enemy.MaxManapoints,
                    targetHitpoints,
                    targetMaxHitpoints,
                    targetManapoints,
                    targetMaxManapoints,
                    buffs,
                    selfControl,
                    targetControl,
                    now)) continue;

            selectedIndex = index;
            return ability;
        }

        return null;
    }

    private static bool AreTriggersActive(
        IReadOnlyList<CombatTrigger>? configured,
        IReadOnlyList<CombatTrigger> defaults,
        long selfHitpoints,
        long selfMaxHitpoints,
        long selfManapoints,
        long selfMaxManapoints,
        long targetHitpoints,
        long targetMaxHitpoints,
        long targetManapoints,
        long targetMaxManapoints,
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        CombatControlRuntime selfControl,
        CombatControlRuntime targetControl,
        long now)
    {
        var triggers = configured is { Count: > 0 } ? configured : defaults;
        if (triggers.Count == 0) return true;

        foreach (var trigger in triggers)
        {
            // 友方极值依赖在当前单人战斗拓扑下解析为玩家自身；敌方极值依赖解析为当前目标。
            // 组队战斗接入后，极值依赖应改为在对应队伍存活成员集合上求值。
            var isSelf = trigger.Dependency is CombatTriggerDependency.Self
                or CombatTriggerDependency.AllyLowestHitpoints
                or CombatTriggerDependency.AllyHighestHitpoints;
            var currentHitpoints = isSelf ? selfHitpoints : targetHitpoints;
            var maxHitpoints = isSelf ? selfMaxHitpoints : targetMaxHitpoints;
            var currentManapoints = isSelf ? selfManapoints : targetManapoints;
            var maxManapoints = isSelf ? selfMaxManapoints : targetMaxManapoints;
            var control = isSelf ? selfControl : targetControl;
            var value = trigger.Condition switch
            {
                CombatTriggerCondition.CurrentHitpoints => currentHitpoints,
                CombatTriggerCondition.CurrentManapoints => currentManapoints,
                CombatTriggerCondition.MissingHitpoints => Math.Max(0, maxHitpoints - currentHitpoints),
                CombatTriggerCondition.MissingManapoints => Math.Max(0, maxManapoints - currentManapoints),
                CombatTriggerCondition.BuffStacks => GetBuffStacks(
                    buffs,
                    trigger.BuffId,
                    now),
                CombatTriggerCondition.CurrentHitpointsPercentage =>
                    CombatFormula.CalculatePercentage(currentHitpoints, maxHitpoints),
                CombatTriggerCondition.MissingHitpointsPercentage =>
                    CombatFormula.CalculatePercentage(Math.Max(0, maxHitpoints - currentHitpoints), maxHitpoints),
                CombatTriggerCondition.CurrentManapointsPercentage =>
                    CombatFormula.CalculatePercentage(currentManapoints, maxManapoints),
                CombatTriggerCondition.MissingManapointsPercentage =>
                    CombatFormula.CalculatePercentage(Math.Max(0, maxManapoints - currentManapoints), maxManapoints),
                CombatTriggerCondition.StunStatus =>
                    control.IsStunned(now) || control.WasStunExpiredAt(now) ? 1 : 0,
                CombatTriggerCondition.BlindStatus =>
                    control.IsBlinded(now) || control.WasBlindExpiredAt(now) ? 1 : 0,
                CombatTriggerCondition.SilenceStatus =>
                    control.IsSilenced(now) || control.WasSilenceExpiredAt(now) ? 1 : 0,
                _ => 0
            };

            var active = value > 0;
            var matches = trigger.Comparator switch
            {
                CombatTriggerComparator.LessThanOrEqual => value <= trigger.Value,
                CombatTriggerComparator.GreaterThanOrEqual => value >= trigger.Value,
                CombatTriggerComparator.LessThan => value < trigger.Value,
                CombatTriggerComparator.GreaterThan => value > trigger.Value,
                CombatTriggerComparator.IsActive => active,
                CombatTriggerComparator.IsInactive => !active,
                _ => false
            };
            if (!matches) return false;
        }

        return true;
    }

    private static long GetBuffStacks(
        IReadOnlyDictionary<string, BuffRuntime> buffs,
        string? buffId,
        long now)
    {
        return !string.IsNullOrWhiteSpace(buffId)
            && buffs.TryGetValue(buffId, out var buff)
            && buff.ExpiresAtMilliseconds > now
            ? buff.Stacks
            : 0;
    }

    private static void AddEvent(ICollection<string> events, string message)
    {
        if (events.Count < MaxResultEvents) events.Add(message);
    }

    private static long GetNextConsumableEventTime(
        long now,
        IReadOnlyList<long> nextConsumableUse)
    {
        var next = InfiniteTime;
        foreach (var timestamp in nextConsumableUse)
        {
            if (timestamp > now) next = Math.Min(next, timestamp);
        }

        return next;
    }

    private static long GetNextRecoveryEventTime(
        long now,
        IReadOnlyList<RecoveryRuntime> recoveries)
    {
        var next = InfiniteTime;
        foreach (var recovery in recoveries)
        {
            if (recovery.NextTickAt >= now) next = Math.Min(next, recovery.NextTickAt);
        }

        return next;
    }


    private static void ApplyControlEffects(
        CombatAbilityDefinition ability,
        CombatControlRuntime target,
        double targetTenacity,
        long now,
        ref DeterministicRandom random,
        IList<string> events)
    {
        if (ability.StunMilliseconds > 0
            && RollControlChance(ability.StunChance, targetTenacity, ref random))
        {
            target.ApplyStun(now, ability.StunMilliseconds);
            AddEvent(events, $"{{{{ability:{ability.Id}|{ability.Name}}}}}：定身 {FormatDuration(ability.StunMilliseconds)}");
        }

        if (ability.BlindMilliseconds > 0
            && RollControlChance(ability.BlindChance, targetTenacity, ref random))
        {
            target.ApplyBlind(now, ability.BlindMilliseconds);
            AddEvent(events, $"{{{{ability:{ability.Id}|{ability.Name}}}}}：致盲 {FormatDuration(ability.BlindMilliseconds)}");
        }

        if (ability.SilenceMilliseconds > 0
            && RollControlChance(ability.SilenceChance, targetTenacity, ref random))
        {
            target.ApplySilence(now, ability.SilenceMilliseconds);
            AddEvent(events, $"{{{{ability:{ability.Id}|{ability.Name}}}}}：沉默 {FormatDuration(ability.SilenceMilliseconds)}");
        }
    }

    private static bool RollControlChance(
        double baseChance,
        double targetTenacity,
        ref DeterministicRandom random)
    {
        return random.NextDouble() < CombatFormula.CalculateControlChance(baseChance, targetTenacity);
    }

    private static string FormatDuration(int milliseconds)
    {
        return $"{milliseconds / 1_000d:0.##}秒";
    }

    private static long CalculateDamageOverTimeTick(
        long totalDamage,
        int totalTicks,
        int currentTick)
    {
        if (totalDamage <= 0 || totalTicks <= 0 || currentTick <= 0) return 0;
        var currentSum = (long)Math.Floor(currentTick * (double)totalDamage / totalTicks);
        var previousSum = (long)Math.Floor((currentTick - 1) * (double)totalDamage / totalTicks);
        return Math.Max(0, currentSum - previousSum);
    }

    private static long ScaleDropQuantity(
        long quantity,
        double quantityMultiplier,
        ref DeterministicRandom random)
    {
        if (quantity <= 0 || quantityMultiplier <= 0) return 0;
        if (quantityMultiplier == 1) return quantity;

        var scaled = quantity * quantityMultiplier;
        if (scaled >= long.MaxValue) return long.MaxValue;
        var whole = (long)Math.Floor(scaled);
        var fraction = scaled - whole;
        if (fraction > 0 && random.NextDouble() < fraction)
        {
            whole = checked(whole + 1);
        }

        return Math.Max(0, whole);
    }

    private static double NormalizeCombatMultiplier(double bonus)
    {
        return !double.IsFinite(bonus)
            ? 1
            : Math.Max(0, 1 + Math.Clamp(bonus, -1, 1_000));
    }

    private static void MergeDrops(
        IDictionary<string, long> destination,
        IReadOnlyDictionary<string, long> source)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = checked(
                (destination.TryGetValue(pair.Key, out var existing) ? existing : 0) + pair.Value);
        }
    }

    private static void AddDrop(
        IDictionary<string, long> destination,
        string itemId,
        long quantity,
        ref long droppedQuantity)
    {
        destination[itemId] = checked(
            (destination.TryGetValue(itemId, out var existing) ? existing : 0) + quantity);
        droppedQuantity = checked(droppedQuantity + quantity);
    }

    private static readonly IReadOnlyDictionary<string, long> EmptyDrops =
        new Dictionary<string, long>(StringComparer.Ordinal);

    private static string FormatDropSummary(
        IReadOnlyDictionary<string, long> regularDrops,
        IReadOnlyDictionary<string, long> rareDrops)
    {
        if (regularDrops.Count == 0 && rareDrops.Count == 0) return string.Empty;

        var parts = new List<string>(regularDrops.Count + rareDrops.Count);
        foreach (var pair in regularDrops)
        {
            if (ItemCatalog.TryGet(pair.Key, out var item))
            {
                parts.Add($"{{{{item:{pair.Key}|{item.Name}|{pair.Value}}}}}");
            }
        }

        foreach (var pair in rareDrops)
        {
            if (ItemCatalog.TryGet(pair.Key, out var item))
            {
                parts.Add($"稀有 {{{{item:{pair.Key}|{item.Name}|{pair.Value}}}}}");
            }
        }

        return parts.Count == 0 ? string.Empty : $"，掉落：{string.Join("、", parts)}";
    }

    private static long TakeRecoveryShare(ref long remaining, int remainingTicks)
    {
        if (remaining <= 0 || remainingTicks <= 0) return 0;
        var share = remaining / remainingTicks;
        if (remaining % remainingTicks != 0) share++;
        remaining -= share;
        return share;
    }

    private static long GetAmount(IReadOnlyDictionary<string, long> amounts, string itemId)
    {
        return amounts.TryGetValue(itemId, out var amount) ? amount : 0;
    }

    private static long GetAmount(IDictionary<string, long> amounts, string itemId)
    {
        return amounts.TryGetValue(itemId, out var amount) ? amount : 0;
    }

    private static ulong CreateSeed(Guid playerId, DateTimeOffset startedAt, string encounterId)
    {
        var bytes = playerId.ToByteArray();
        ulong seed = 1469598103934665603UL;
        foreach (var value in bytes)
        {
            seed ^= value;
            seed *= 1099511628211UL;
        }

        foreach (var value in BitConverter.GetBytes(startedAt.ToUnixTimeMilliseconds()))
        {
            seed ^= value;
            seed *= 1099511628211UL;
        }

        foreach (var value in encounterId)
        {
            seed ^= value;
            seed *= 1099511628211UL;
        }

        return seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    private sealed class BuffRuntime
    {
        public BuffRuntime(
            string buffId,
            string name,
            int stacks,
            int maxStacks,
            double qiGainMultiplier,
            double attackLevelRatio,
            double damageRatio,
            double attackSpeedRatio,
            string? sourceItemId,
            long expiresAtMilliseconds,
            IReadOnlyDictionary<string, double>? modifiers = null)
        {
            BuffId = buffId;
            Name = name;
            Stacks = stacks;
            MaxStacks = maxStacks;
            Modifiers = modifiers is not null
                ? new Dictionary<string, double>(modifiers, StringComparer.Ordinal)
                : new Dictionary<string, double>(StringComparer.Ordinal);
            if (qiGainMultiplier != 1.0) Modifiers[Domain.Constants.StatKeys.QiGain] = qiGainMultiplier;
            if (attackLevelRatio != 0.0) Modifiers[Domain.Constants.StatKeys.AttackLevelRatio] = attackLevelRatio;
            if (damageRatio != 0.0) Modifiers[Domain.Constants.StatKeys.DamageRatio] = damageRatio;
            if (attackSpeedRatio != 0.0) Modifiers[Domain.Constants.StatKeys.AttackSpeedRatio] = attackSpeedRatio;
            SourceItemId = sourceItemId;
            ExpiresAtMilliseconds = expiresAtMilliseconds;
        }

        public string BuffId { get; }
        public string Name { get; }
        public int Stacks { get; set; }
        public int MaxStacks { get; }
        public Dictionary<string, double> Modifiers { get; }
        public string? SourceItemId { get; }
        public long ExpiresAtMilliseconds { get; set; }

        public double GetModifier(string statKey, double defaultValue = 0.0) =>
            Modifiers.TryGetValue(statKey, out var value) ? value : defaultValue;

        public double QiGainMultiplier => GetModifier(Domain.Constants.StatKeys.QiGain, 1.0);
        public double AttackLevelRatio => GetModifier(Domain.Constants.StatKeys.AttackLevelRatio, 0.0);
        public double DamageRatio => GetModifier(Domain.Constants.StatKeys.DamageRatio, 0.0);
        public double AttackSpeedRatio => GetModifier(Domain.Constants.StatKeys.AttackSpeedRatio, 0.0);
    }

    private sealed class CombatControlRuntime
    {
        public long StunUntil { get; private set; }
        public long BlindUntil { get; private set; }
        public long SilenceUntil { get; private set; }
        public long NextExpirationAt { get; private set; } = InfiniteTime;
        private long StunExpiredAt { get; set; }
        private long BlindExpiredAt { get; set; }
        private long SilenceExpiredAt { get; set; }

        public bool IsStunned(long now) => StunUntil > now;
        public bool IsBlinded(long now) => BlindUntil > now;
        public bool IsSilenced(long now) => SilenceUntil > now;
        public bool WasStunExpiredAt(long now) => StunExpiredAt == now;
        public bool WasBlindExpiredAt(long now) => BlindExpiredAt == now;
        public bool WasSilenceExpiredAt(long now) => SilenceExpiredAt == now;

        public void ApplyStun(long now, int durationMilliseconds)
        {
            StunExpiredAt = 0;
            StunUntil = Math.Max(StunUntil, checked(now + Math.Max(1, durationMilliseconds)));
            NextExpirationAt = Math.Min(NextExpirationAt, StunUntil);
        }

        public void ApplyBlind(long now, int durationMilliseconds)
        {
            BlindExpiredAt = 0;
            BlindUntil = Math.Max(BlindUntil, checked(now + Math.Max(1, durationMilliseconds)));
            NextExpirationAt = Math.Min(NextExpirationAt, BlindUntil);
        }

        public void ApplySilence(long now, int durationMilliseconds)
        {
            SilenceExpiredAt = 0;
            SilenceUntil = Math.Max(SilenceUntil, checked(now + Math.Max(1, durationMilliseconds)));
            NextExpirationAt = Math.Min(NextExpirationAt, SilenceUntil);
        }

        public bool Expire(long now)
        {
            if (NextExpirationAt > now) return false;

            var changed = false;
            if (StunUntil > 0 && StunUntil <= now)
            {
                StunExpiredAt = StunUntil;
                StunUntil = 0;
                changed = true;
            }

            if (BlindUntil > 0 && BlindUntil <= now)
            {
                BlindExpiredAt = BlindUntil;
                BlindUntil = 0;
                changed = true;
            }

            if (SilenceUntil > 0 && SilenceUntil <= now)
            {
                SilenceExpiredAt = SilenceUntil;
                SilenceUntil = 0;
                changed = true;
            }

            NextExpirationAt = InfiniteTime;
            if (StunUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, StunUntil);
            if (BlindUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, BlindUntil);
            if (SilenceUntil > now) NextExpirationAt = Math.Min(NextExpirationAt, SilenceUntil);

            return changed;
        }

        public void Clear()
        {
            StunUntil = 0;
            BlindUntil = 0;
            SilenceUntil = 0;
            StunExpiredAt = 0;
            BlindExpiredAt = 0;
            SilenceExpiredAt = 0;
            NextExpirationAt = InfiniteTime;
        }
    }

    private sealed class RecoveryRuntime
    {
        public RecoveryRuntime(
            long nextTickAt,
            int remainingTicks,
            long remainingHitpoints,
            long remainingManapoints)
        {
            NextTickAt = nextTickAt;
            RemainingTicks = remainingTicks;
            RemainingHitpoints = remainingHitpoints;
            RemainingManapoints = remainingManapoints;
        }

        public long NextTickAt { get; set; }
        public int RemainingTicks { get; set; }
        public long RemainingHitpoints { get; set; }
        public long RemainingManapoints { get; set; }
    }

    private sealed record CombatStats(
        long MaxHitpoints,
        long MaxManapoints,
        int AttackIntervalMilliseconds,
        double AccuracyRating,
        double MaxDamage,
        double EvasionRating,
        double Armor,
        double MagicResistance,
        double MetalResistance,
        double WoodResistance,
        double WaterResistance,
        double FireResistance,
        double EarthResistance,
        CombatStyle Style,
        DamageCategory Category,
        Element Element,
        CombatantDefinition? Definition,
        double Tenacity,
        double HpRegenPerTenSeconds,
        double MpRegenPerTenSeconds,
        double PenetrationRatio,
        double LifeStealRatio,
        double ArmorDamageRatio,
        double HpDrainRatio,
        double DamageTakenRatio,
        double CriticalChance,
        double CriticalDamageRatio,
        double CriticalAvoidance,
        double DamageAbsorptionRatio,
        double BreakRating = 0,
        double ParryRating = 0,
        double SkillIntensity = 1.0,
        double DynamicResistanceFactorK = 1.0,
        double ActionSpeedReductionRatio = 0,
        double CooldownReductionRatio = 0,
        double CriticalRating = 0,
        double CriticalAvoidanceRating = 0,
        double HpRegen = 0,
        double MpRegen = 0);

    private readonly record struct CombatResolution(
        long Damage,
        long Healing,
        bool DidHit = false,
        long DrainHealing = 0,
        bool IsCritical = false);

    private struct DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(ulong seed)
        {
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public double NextDouble()
        {
            var value = NextUInt64() >> 11;
            return value / (double)(1UL << 53);
        }

        public long NextInt64(long minimumInclusive, long maximumExclusive)
        {
            if (maximumExclusive <= minimumInclusive) return minimumInclusive;
            var range = checked((ulong)(maximumExclusive - minimumInclusive));
            var value = NextUInt64() % range;
            return checked(minimumInclusive + (long)value);
        }

        private ulong NextUInt64()
        {
            var x = _state;
            x ^= x << 7;
            x ^= x >> 9;
            x ^= x << 8;
            _state = x;
            return x;
        }
    }
}

internal static class CombatReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> source, T value)
    {
        for (var index = 0; index < source.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(source[index], value)) return index;
        }

        return -1;
    }
}
