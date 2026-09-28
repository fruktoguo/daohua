namespace Domain.Simulation;

/// <summary>资源系统选出的一个自然窗口，普通行动可在该窗口内继续切分单轮边界。</summary>
public readonly record struct ResourceBoundaryPlan(double StepSeconds, bool ReachesNaturalBoundary, bool Changed);

public static partial class SimulationEngine
{
    /// <summary>只推进一次搜寻读条，不创建战斗实体；返回实际消耗的秒数。</summary>
    public static double AdvanceCombatSearchPhase(IPlayerActionState action, double availableSeconds)
    {
        var expedition = action.Expedition
            ?? throw new DomainException(DomainErrorCode.InvariantViolation, "战斗搜寻状态缺失。");
        var remaining = Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds);
        var step = Math.Min(availableSeconds, remaining);
        if (step <= 0 && !expedition.DuelSeekFailed)
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗搜寻阶段没有可推进的时间。");
        action.Expedition = expedition with { PhaseElapsedSeconds = expedition.PhaseElapsedSeconds + step };
        action.ProgressSeconds = action.Expedition.PhaseElapsedSeconds;
        return step;
    }

    /// <summary>清理到期增益并激活即时用品，选择回复、Buff、用品冷却与请求末端的最近窗口。</summary>
    public static ResourceBoundaryPlan PrepareResourceBoundary(IPlayerResourceContext player,
        GameDataCatalog gameData, AssetWallet wallet, DateTimeOffset computeTarget, ActionMutationBatch request)
    {
        request.EventTail ??= new();
        var boundary = PrepareResourceBoundaryCore(player, gameData, wallet, computeTarget,
            request.EventTail, null, out var instantDeltas);
        foreach (var delta in instantDeltas)
        {
            wallet.Apply(delta);
            request.AddAsset(delta);
        }
        return boundary;
    }

    private static ResourceBoundaryPlan PrepareResourceBoundaryCore(IPlayerResourceContext player,
        GameDataCatalog gameData, AssetWallet wallet, DateTimeOffset computeTarget,
        SimulationEventTail events, LiveCombatBattle? live, out List<AssetDelta> instantDeltas)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.resource.prepare");
        var now = player.LastSimulatedAt;
        var nowUnix = now.ToUnixTimeMilliseconds() / 1000d;
        var nextBuffExpiry = double.PositiveInfinity;
        var changed = false;
        var activeBuffs = player.Buffs.Active;
        if (activeBuffs.Count > 0)
        {
            for (var i = 0; i < activeBuffs.Count; i++)
                if (activeBuffs[i].ExpiresAtUnixSeconds < nextBuffExpiry) nextBuffExpiry = activeBuffs[i].ExpiresAtUnixSeconds;
            if (nextBuffExpiry <= nowUnix)
            {
                var expired = activeBuffs.RemoveAll(buff => buff.ExpiresAtUnixSeconds <= nowUnix);
                if (expired > 0) { player.MarkDirty(PlayerSectionKind.Buffs); changed = true; }
                nextBuffExpiry = double.PositiveInfinity;
                for (var i = 0; i < activeBuffs.Count; i++)
                    if (activeBuffs[i].ExpiresAtUnixSeconds < nextBuffExpiry) nextBuffExpiry = activeBuffs[i].ExpiresAtUnixSeconds;
            }
        }
        ApplyResourceAttributes(player, gameData);
        instantDeltas = ActivateInstantConsumables(player, gameData, wallet, now, events, live);
        var untilRegen = 5 - player.Resources.RegenCarrySeconds;
        var naturalStep = untilRegen;
        if (double.IsFinite(nextBuffExpiry)) naturalStep = Math.Min(naturalStep, nextBuffExpiry - nowUnix);
        var step = Math.Min((computeTarget - now).TotalSeconds, naturalStep);
        var slots = player.Settings.AutoUseSlots;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Enabled) continue;
            var untilCooldown = slot.CooldownUntilUnixSeconds - nowUnix;
            if (untilCooldown > 1e-9)
            {
                naturalStep = Math.Min(naturalStep, untilCooldown);
                step = Math.Min(step, untilCooldown);
            }
        }
        return new(step, naturalStep <= step + 1e-9, changed);
    }

    /// <summary>资源窗口收尾按暂存入账、技能时钟、行动资产、事件、回复的既有顺序执行。</summary>
    public static bool CompleteResourceBoundary(IPlayerResourceContext player, GameDataCatalog gameData,
        AssetWallet wallet, DateTimeOffset windowStart, double stepSeconds,
        ActionMutationBatch core, ActionMutationBatch request)
    {
        var at = windowStart.AddSeconds(stepSeconds);
        foreach (var item in SettleResourceItems(player, at.ToUnixTimeMilliseconds() / 1000d,
            gameData.Duel.StagedSettleSeconds))
        {
            var itemId = new ItemId(item.ItemId);
            var delta = new AssetDelta(player.PlayerId, new AssetKey(itemId), item.Quantity, "simulation");
            wallet.Apply(delta);
            request.AddAsset(delta);
            request.AddEvent(new SimulationEvent(at, "duel.staged-settled",
                $"暂存物品入账：{gameData.Items.GetValueOrDefault(itemId)?.Name ?? item.ItemId} ×{item.Quantity:0.##}。", null,
                new Dictionary<ItemId, double> { [itemId] = item.Quantity }));
        }
        var changed = AdvanceIdleSkillResources(player, stepSeconds);
        foreach (var delta in core.ToAssetDeltas(player.PlayerId))
        {
            wallet.Apply(delta);
            request.AddAsset(delta);
        }
        request.MergeEvents(core);
        request.ActionCount += core.ActionCount;
        request.RepeatCount += core.RepeatCount;
        changed |= CompleteResourceRegeneration(player, gameData, stepSeconds);
        return changed;
    }

    /// <summary>应用属性解析结果；分段由调用方直接持有，面板夹取顺序与聚合入口一致。</summary>
    public static PlayerAttributes ApplyResourceAttributes(IPlayerResourceContext player, GameDataCatalog gameData)
    {
        var attributes = player.ResolveAttributes(gameData);
        var r = player.Resources;
        var c = player.Combat;
        if (r.MaxHitpoints != attributes.MaxHitpoints || r.MaxManapoints != attributes.MaxManapoints
            || c.AttackPower != attributes.PhysicalAttack || c.Defense != attributes.Armor)
            player.MarkDirty(PlayerSectionKind.Combat);
        r.MaxHitpoints = attributes.MaxHitpoints;
        r.MaxManapoints = attributes.MaxManapoints;
        c.AttackPower = attributes.PhysicalAttack;
        c.Defense = attributes.Armor;
        r.Hitpoints = Math.Min(r.Hitpoints, r.MaxHitpoints);
        r.Manapoints = Math.Min(r.Manapoints, r.MaxManapoints);
        return attributes;
    }

    private static DuelStagedItem[] SettleResourceItems(IPlayerResourceContext player,
        double nowUnixSeconds, double settleSeconds)
    {
        if (player.Duel.ActiveDuelId is not null || player.Duel.StagedItems.Count == 0) return [];
        var settled = player.Duel.StagedItems.Where(item => item.AcquiredAtUnixSeconds + settleSeconds <= nowUnixSeconds).ToArray();
        if (settled.Length == 0) return [];
        foreach (var item in settled) player.Duel.StagedItems.Remove(item);
        player.MarkDirty(PlayerSectionKind.Duel);
        return settled;
    }

    private static bool AdvanceIdleSkillResources(IPlayerResourceContext player, double stepSeconds)
    {
        if (player.Actions.Count > 0 && player.Actions[0].Expedition?.Phase == CombatExpeditionPhase.Combat) return false;
        var skills = player.Combat.Skills;
        if (skills.Cooldowns.Count == 0 && skills.CastingAbilityId is null) return false;
        player.Combat.Skills = skills.AdvanceIdle(stepSeconds);
        player.MarkDirty(PlayerSectionKind.Combat);
        return true;
    }

    private static bool CompleteResourceRegeneration(IPlayerResourceContext player, GameDataCatalog gameData, double stepSeconds)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.resource.regen");
        player.Resources.RegenCarrySeconds += stepSeconds;
        if (player.Resources.RegenCarrySeconds < 5 - 1e-9) return false;
        player.Resources.RegenCarrySeconds = 0;
        var attributes = ApplyResourceAttributes(player, gameData);
        var hitpoints = player.Resources.Hitpoints;
        var manapoints = player.Resources.Manapoints;
        if (player.Resources.Hitpoints > 0)
            player.Resources.Hitpoints = Math.Min(attributes.MaxHitpoints, player.Resources.Hitpoints + attributes.HpRegen);
        player.Resources.Manapoints = Math.Min(attributes.MaxManapoints, player.Resources.Manapoints + attributes.MpRegen);
        if (player.Actions.Count > 0 && player.Actions[0].Expedition is { } expedition
            && (expedition.PlayerHitpoints != player.Resources.Hitpoints || expedition.PlayerManapoints != player.Resources.Manapoints))
            player.Actions[0].Expedition = expedition with
            {
                PlayerHitpoints = player.Resources.Hitpoints,
                PlayerManapoints = player.Resources.Manapoints
            };
        if (player.Resources.Hitpoints == hitpoints && player.Resources.Manapoints == manapoints) return false;
        player.MarkDirty(PlayerSectionKind.Combat);
        return true;
    }

    /// <summary>超过48小时收益上限后只清理过期状态与推进时钟，不推进任何行动。</summary>
    public static bool ApplyOfflineCap(IPlayerResourceContext player, DateTimeOffset target, ActionMutationBatch request)
    {
        if (player.LastSimulatedAt >= target) return false;
        var expired = player.Buffs.Active.RemoveAll(buff => buff.ExpiresAtUnixSeconds <= target.ToUnixTimeMilliseconds() / 1000d);
        if (expired > 0) player.MarkDirty(PlayerSectionKind.Buffs);
        if (!player.Combat.Skills.IsEmpty)
        {
            player.Combat.Skills = PlayerCombatSkillState.Empty;
            player.MarkDirty(PlayerSectionKind.Combat);
        }
        player.LastSimulatedAt = target;
        request.AddEvent(new SimulationEvent(target, "offline.capped", "离线补算达到 48 小时上限，超出部分未累积收益。", null));
        return true;
    }
}
