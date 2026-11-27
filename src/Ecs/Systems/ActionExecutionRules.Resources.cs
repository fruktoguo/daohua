using System.Text.Json;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using SimulationEventTail = DaoHua.Ecs.SimulationEngine.SimulationEventTail;
using LiveCombatBattle = DaoHua.Ecs.SimulationEngine.LiveCombatBattle;

namespace DaoHua.Ecs.Systems;

/// <summary>资源时钟的 System 所属规则：增益到期、即时用品、自然窗口和窗口提交。</summary>
internal static partial class ActionExecutionRules
{
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

    internal static ResourceBoundaryPlan PrepareResourceBoundaryCore(IPlayerResourceContext player,
        GameDataCatalog gameData, AssetWallet wallet, DateTimeOffset computeTarget,
        SimulationEventTail events, LiveCombatBattle? live, out List<AssetDelta> instantDeltas,
        ConsumableScheduleMode consumableSchedule = ConsumableScheduleMode.EventDriven)
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
                changed |= player.ExpireBuffs(nowUnix);
                nextBuffExpiry = double.PositiveInfinity;
                for (var i = 0; i < activeBuffs.Count; i++)
                    if (activeBuffs[i].ExpiresAtUnixSeconds < nextBuffExpiry) nextBuffExpiry = activeBuffs[i].ExpiresAtUnixSeconds;
            }
        }
        // durable 持续消耗品随槽位/钱包状态注入或清除无限期 ActiveBuff：
        // 必须在属性解析前同步，保证回复后段乘区进入本步面板与战内单位。
        changed |= SyncDurableConsumableBuffs(player, gameData, wallet);
        ApplyResourceAttributes(player, gameData);
        // 事件驱动：战外相位（搜寻/赶路/等待）不判定即时消耗品——战斗边界的收尾回调承担全部
        // 战内判定，资源窗口只保留冷却/持续到期的唤醒语义；PeriodicTick 维持现状战外判定。
        // 事件驱动：即时消耗品全部由战斗入口与自然边界收尾回调判定（带存活战态实时读写），
        // 资源窗口只保留冷却/持续到期唤醒语义；PeriodicTick 维持现状战外判定。
        instantDeltas = consumableSchedule == ConsumableScheduleMode.EventDriven
            ? []
            : ActivateInstantConsumables(player, gameData, wallet, now, events, live);
        // 即时消耗品扣件/注入 Buff/冷却回写是真实可见变更：不计 changed 会让资产已落库
        // 但 SimulationResult.Changed=false，推进推送跳过该玩家直到下一条命令。
        changed |= instantDeltas.Count > 0;
        // 真实成员不推进回复余数；不能用被冻结的余数重复切个人资源窗口。
        var waitsForSession = player.Actions.Count > 0 && player.Actions[0].Participation is not null
            && player is not PlayerAggregate { IsParticipationSessionContainer: true };
        var naturalStep = waitsForSession ? double.PositiveInfinity : 5 - player.Resources.RegenCarrySeconds;
        if (double.IsFinite(nextBuffExpiry))
            naturalStep = Math.Min(naturalStep, SecondsUntilUnixBoundary(now, nowUnix, nextBuffExpiry));
        var step = Math.Min((computeTarget - now).TotalSeconds, naturalStep);
        var slots = player.Settings.AutoUseSlots;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Enabled || slot.CooldownUntilUnixSeconds <= nowUnix) continue;
            var untilCooldown = SecondsUntilUnixBoundary(now, nowUnix, slot.CooldownUntilUnixSeconds);
            naturalStep = Math.Min(naturalStep, untilCooldown);
            step = Math.Min(step, untilCooldown);
        }
        if (player.Combat.TalismanCooldowns.Count > 0
            && CombatTraitRules.CanEquipTalismanInAbilitySlot(player.Progression.TraitIds))
        {
            var abilitySlots = player.Loadout.AbilitySlots;
            for (var i = 1; i < abilitySlots.Count; i++)
            {
                if (abilitySlots[i].AbilityId is { } talismanId
                    && player.Combat.TalismanCooldowns.TryGetValue(talismanId, out var talismanCdUntil)
                    && talismanCdUntil > nowUnix)
                {
                    var untilTalismanCd = SecondsUntilUnixBoundary(now, nowUnix, talismanCdUntil);
                    naturalStep = Math.Min(naturalStep, untilTalismanCd);
                    step = Math.Min(step, untilTalismanCd);
                }
            }
        }
        // 暂存区最早到期时刻并入自然边界：模拟窗口正好切在结算点，与 Buff 到期同一调度口径。
        if (player.Duel.ActiveDuelId is null && player.Duel.StagedItems.Count > 0)
        {
            var settleAt = player.Duel.StagedItems[0].AcquiredAtUnixSeconds + gameData.Duel.StagedSettleSeconds;
            if (settleAt > nowUnix)
            {
                var untilStaged = SecondsUntilUnixBoundary(now, nowUnix, settleAt);
                naturalStep = Math.Min(naturalStep, untilStaged);
                step = Math.Min(step, untilStaged);
            }
        }
        // 参与会话权威散场时刻并入自然边界：模拟窗口正好切在散场点，参与行动移除由行动准入在下一拍执行。
        if (player.Actions.Count > 0 && player.Actions[0].Participation is { } session
            && double.IsFinite(session.EndsAtUnixSeconds)
            && session.EndsAtUnixSeconds > nowUnix)
        {
            var untilSessionEnd = SecondsUntilUnixBoundary(now, nowUnix, session.EndsAtUnixSeconds);
            naturalStep = Math.Min(naturalStep, untilSessionEnd);
            step = Math.Min(step, untilSessionEnd);
        }
        return new(step, naturalStep <= step + 1e-9, changed);
    }

    /// <summary>永驻/不可到达边界阈值（如 durable buff 的 double.MaxValue）；低于此值才纳入时钟换算。</summary>
    internal static readonly double MaxUnixBoundarySeconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() / 1000d - 1;

    /// <summary>判断目标 Unix 秒是否代表永不过期或超过 DateTimeOffset 安全上限。</summary>
    internal static bool IsInfiniteOrBeyondMaxUnixBoundary(double targetUnix)
        => !double.IsFinite(targetUnix) || targetUnix >= MaxUnixBoundarySeconds;

    /// <summary>
    /// 计算从 now 到目标 Unix 秒边界需要推进的最小正秒数。
    /// 下游过期、冷却、暂存与散场均按 ToUnixTimeMilliseconds()/1000d 截断比较；
    /// 若 AddSecondsExact 后毫秒截断值仍小于 targetUnix（double ULP 误差或亚毫秒起点），
    /// 推进至首个不小于 targetUnix 的整毫秒，防止 0-tick 死循环或亚毫秒微步空转。
    /// </summary>
    private static double SecondsUntilUnixBoundary(DateTimeOffset now, double nowUnix, double targetUnix)
    {
        if (!double.IsFinite(targetUnix) || targetUnix <= nowUnix) return 0;
        if (targetUnix >= MaxUnixBoundarySeconds) return double.PositiveInfinity;
        var candidate = now.AddSecondsExact(targetUnix - nowUnix);
        while (candidate <= now || candidate.ToUnixTimeMilliseconds() / 1000d < targetUnix)
            candidate = DateTimeOffset.FromUnixTimeMilliseconds(candidate.ToUnixTimeMilliseconds() + 1);
        return (candidate - now).TotalSeconds;
    }

    /// <summary>资源窗口收尾按暂存入账、技能时钟、行动资产、事件、回复的既有顺序执行。</summary>
    public static bool CompleteResourceBoundary(IPlayerResourceContext player, GameDataCatalog gameData,
        AssetWallet wallet, DateTimeOffset windowStart, double stepSeconds,
        ActionMutationBatch core, ActionMutationBatch request)
    {
        var at = windowStart.AddSecondsExact(stepSeconds);
        var stagedItems = SettleResourceItems(player, at.ToUnixTimeMilliseconds() / 1000d,
            gameData.Duel.StagedSettleSeconds);
        // 暂存物品入账写入 Duel 账本与资产差量：不计 changed 会让入账已落库但无推送下发。
        var changed = stagedItems.Length > 0 || core.IdleSkillChanged;
        foreach (var item in stagedItems)
        {
            var itemId = new ItemId(item.ItemId);
            var delta = new AssetDelta(player.PlayerId, new AssetKey(itemId), item.Quantity, "simulation");
            wallet.Apply(delta);
            request.AddAsset(delta);
            request.AddEvent(new SimulationEvent(at, "duel.staged.settled",
                $"暂存物品入账：{gameData.Items.GetValueOrDefault(itemId)?.Name ?? item.ItemId} ×{item.Quantity:0.##}。", null,
                new Dictionary<ItemId, double> { [itemId] = item.Quantity }));
        }
        var coreDeltas = core.ToAssetDeltas(player.PlayerId);
        changed |= coreDeltas.Count > 0;
        foreach (var delta in coreDeltas)
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
        // 会话投影是参与期间的血蓝权威；实时换装只更新展示上限，不能钳制会话资源。
        if (player.Actions.Count > 0 && player.Actions[0].Participation is not null
            && player is not PlayerAggregate { IsParticipationSessionContainer: true }) return attributes;
        // 参与会话期间资源钳制上限取冻结面板：实时换装漂移不进入共享战斗的单位输入。
        var sessionMember = player.Actions.Count > 0
            && player.Actions[0].Participation?.Spec is { } sessionSpec
            && player.Actions[0].Expedition is { SelfIndex: var selfIndex }
            && selfIndex >= 0 && selfIndex < sessionSpec.Members.Count
                ? sessionSpec.Members[selfIndex] : null;
        r.Hitpoints = Math.Min(r.Hitpoints, sessionMember?.Stats.MaxHitpoints ?? r.MaxHitpoints);
        r.Manapoints = Math.Min(r.Manapoints, sessionMember?.Stats.MaxManapoints ?? r.MaxManapoints);
        return attributes;
    }

    internal static DuelStagedItem[] SettleResourceItems(IPlayerResourceContext player,
        double nowUnixSeconds, double settleSeconds)
        => ActionReportBook.SettleStagedItems(player, nowUnixSeconds, settleSeconds);

    /// <summary>暂存结算前清理孤儿斗法预约标记；返回 true 表示清除了异常残留。</summary>
    internal static bool PruneOrphanDuelReservation(IPlayerResourceContext player)
        => ActionReportBook.PruneOrphanDuelReservation(player);

    internal static bool AdvanceIdleSkillResources(IPlayerResourceContext player, double stepSeconds)
    {
        var expedition = player.Actions.Count > 0 ? player.Actions[0].Expedition : null;
        if (player.Actions.Count > 0 && player.Actions[0].Participation is not null
            && player is not PlayerAggregate { IsParticipationSessionContainer: true }) return false;
        if (expedition?.Phase == CombatExpeditionPhase.Combat) return false;
        var changed = false;
        // 参与会话：盟友镜像的跨战技能冷却与本机同口径折算（各成员模拟消费同一冻结起值 → 位级一致）。
        if (expedition?.Allies is { Count: > 0 } allies)
        {
            var updated = new ExpeditionAllyState[allies.Count];
            for (var i = 0; i < allies.Count; i++)
            {
                var ally = allies[i];
                var allySkills = ally.CombatSkills;
                if (allySkills is { } nonEmpty && !nonEmpty.IsEmpty
                    && (nonEmpty.Cooldowns.Count > 0 || nonEmpty.CastingAbilityId is not null))
                {
                    updated[i] = ally with { CombatSkills = nonEmpty.AdvanceIdle(stepSeconds) };
                    changed = true;
                }
                else updated[i] = ally;
            }
            if (changed && player.Actions.Count > 0)
                player.Actions[0].Expedition = expedition! with { Allies = updated };
        }
        var skills = player.Combat.Skills;
        if (skills.Cooldowns.Count == 0 && skills.CastingAbilityId is null) return changed;
        player.Combat.Skills = skills.AdvanceIdle(stepSeconds);
        player.MarkDirty(PlayerSectionKind.Combat);
        return true;
    }

    internal static bool CompleteResourceRegeneration(IPlayerResourceContext player, GameDataCatalog gameData,
        double stepSeconds, bool regenerateAllies = true)
    {
        if (player.Actions.Count > 0 && player.Actions[0].Participation is not null
            && player is not PlayerAggregate { IsParticipationSessionContainer: true }) return false;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.resource.regen");
        player.Resources.RegenCarrySeconds += stepSeconds;
        if (player.Resources.RegenCarrySeconds < 5 - 1d / TimeSpan.TicksPerSecond) return false;
        player.Resources.RegenCarrySeconds = 0;
        var participationSpec = player.Actions.Count > 0 ? player.Actions[0].Participation?.Spec : null;
        var participant = participationSpec is not null
            && player.Actions[0].Expedition is { SelfIndex: var si } && si >= 0 && si < participationSpec.Members.Count
            ? participationSpec.Members[si] : null;
        // 行动参与期间成员回复率取开打冻结面板（各成员模拟消费同一数值 → 位级一致）；
        // 单人路径维持实时属性口径。
        double maxHitpoints, maxManapoints, hpRegen, mpRegen;
        if (participant is not null)
        {
            maxHitpoints = participant.Stats.MaxHitpoints;
            maxManapoints = participant.Stats.MaxManapoints;
            hpRegen = participant.Stats.HpRegen;
            mpRegen = participant.Stats.MpRegen;
        }
        else
        {
            var attributes = ApplyResourceAttributes(player, gameData);
            maxHitpoints = attributes.MaxHitpoints;
            maxManapoints = attributes.MaxManapoints;
            hpRegen = attributes.HpRegen;
            mpRegen = attributes.MpRegen;
        }
        var hitpoints = player.Resources.Hitpoints;
        var manapoints = player.Resources.Manapoints;
        // 行动级战斗统计：讨伐远征存续期间的战外自然回复计入「自动回复」。
        var statsBook = player.Actions.Count > 0 && player.Actions[0].Expedition is not null
            ? player.Duel.ReportFor(player.Actions[0].Id)?.CombatStats : null;
        if (player.Resources.Hitpoints > 0)
            player.Resources.Hitpoints = Math.Min(maxHitpoints, player.Resources.Hitpoints + hpRegen);
        player.Resources.Manapoints = Math.Min(maxManapoints, player.Resources.Manapoints + mpRegen);
        if (player.Resources.Hitpoints > hitpoints)
            statsBook?.RecordHealing("player", player.DisplayName, autoRegen: true,
                player.Resources.Hitpoints - hitpoints);
        if (player.Actions.Count > 0 && player.Actions[0].Expedition is { } expedition
            && (expedition.PlayerHitpoints != player.Resources.Hitpoints || expedition.PlayerManapoints != player.Resources.Manapoints))
            player.Actions[0].Expedition = expedition with
            {
                PlayerHitpoints = player.Resources.Hitpoints,
                PlayerManapoints = player.Resources.Manapoints
            };
        // 盟友镜像回复：只在非战斗相位按冻结面板结算（战斗相位由战斗内状态权威承载）。
        var anyAllyChange = false;
        if (regenerateAllies && participationSpec is not null && player.Actions[0].Expedition is { Phase: not CombatExpeditionPhase.Combat } regenExpedition
            && regenExpedition.Allies is { Count: > 0 } regenAllies)
        {
            var updated = new ExpeditionAllyState[regenAllies.Count];
            for (var i = 0; i < regenAllies.Count; i++)
            {
                var ally = regenAllies[i];
                if (ally.MemberIndex >= participationSpec.Members.Count) { updated[i] = ally; continue; }
                var memberStats = participationSpec.Members[ally.MemberIndex].Stats;
                var allyHp = ally.State.Hitpoints > 0
                    ? Math.Min(memberStats.MaxHitpoints, ally.State.Hitpoints + memberStats.HpRegen)
                    : ally.State.Hitpoints;
                var allyMp = Math.Min(memberStats.MaxManapoints, ally.State.Manapoints + memberStats.MpRegen);
                if (allyHp > ally.State.Hitpoints)
                    statsBook?.RecordHealing($"ally:{ally.MemberIndex}",
                        participationSpec.Members[ally.MemberIndex].DisplayName, autoRegen: true,
                        allyHp - ally.State.Hitpoints);
                if (allyHp != ally.State.Hitpoints || allyMp != ally.State.Manapoints)
                {
                    updated[i] = ally with { State = ally.State with { Hitpoints = allyHp, Manapoints = allyMp } };
                    anyAllyChange = true;
                }
                else updated[i] = ally;
            }
            if (anyAllyChange)
                player.Actions[0].Expedition = player.Actions[0].Expedition! with { Allies = updated };
        }
        // 统计簿有写入时统一标脏：重建账本导出文档并推送 duel 分段。
        if (statsBook is not null) ActionReportBook.FlushCombatStats(player, player.Actions[0].Id);
        if (player.Resources.Hitpoints == hitpoints && player.Resources.Manapoints == manapoints) return anyAllyChange;
        player.MarkDirty(PlayerSectionKind.Combat);
        return true;
    }

    /// <summary>超过离线收益上限后只清理过期状态与推进时钟，不推进任何行动；
    /// emitEvent 仅在本请求实际跨越封顶边界时为 true（起点已越界的请求只推进时钟不发事件）。</summary>
    public static bool ApplyOfflineCap(IPlayerResourceContext player, DateTimeOffset target, ActionMutationBatch request,
        bool emitEvent = true)
    {
        if (player.LastSimulatedAt >= target) return false;
        player.ExpireBuffs(target.ToUnixTimeMilliseconds() / 1000d);
        if (!player.Combat.Skills.IsEmpty)
        {
            player.Combat.Skills = PlayerCombatSkillState.Empty;
            player.MarkDirty(PlayerSectionKind.Combat);
        }
        player.LastSimulatedAt = target;
        if (emitEvent)
            request.AddEvent(new SimulationEvent(target, "offline.capped", "离线补算达到 48 小时上限，超出部分未累积收益。", null));
        return true;
    }

    /// <param name="wakeDelta">边界事实增量（TASK-20261112-15 订阅索引）：非 null 时仅唤醒
    /// 订阅事实已翻转的槽位；null 为全量判定（入场复查/冷却到期唤醒/未追踪路径）。</param>
    internal static List<AssetDelta> ActivateInstantConsumables(IPlayerResourceContext player,
        GameDataCatalog catalog, AssetWallet wallet, DateTimeOffset at, SimulationEventTail events,
        LiveCombatBattle? live = null, CombatEncounterState? liveState = null,
        IReadOnlyDictionary<AssetKey, double>? pendingSpends = null,
        CombatFactDelta? wakeDelta = null)
    {
        RuntimeInstrumentation.Count("consumable.eval_calls");
        if (player.Actions.Count == 0 || player.Resources.Hitpoints <= 0) return [];
        // 参与会话期间不激活即时消耗品：共享剧本的全部输入必须来自开打定格 spec
        // （含检查点恢复与计算节点重放验算）；成员钱包/槽位不在定格公共输入内，
        // 实时扣件会使同一剧本的推演输入不可复现 → 状态分歧。
        if (player.Actions[0].Participation is not null) return [];
        // 无任何启用槽位时整段跳过：免每步 catalog.Actions.Get 与栏域匹配的固定税。
        var slots = player.Settings.AutoUseSlots;
        var anyEnabled = false;
        for (var i = 0; i < slots.Count; i++)
            if (slots[i].Enabled) { anyEnabled = true; break; }
        var activeTraitsForSlotCheck = player.Actions[0].Expedition is { Phase: CombatExpeditionPhase.Combat, PlayerState: { } cps }
            ? cps.Traits.Ids
            : player.Progression.TraitIds;
        var allowAbilityTalismans = CombatTraitRules.CanEquipTalismanInAbilitySlot(activeTraitsForSlotCheck);
        var anyAbilityTalisman = false;
        if (allowAbilityTalismans)
        {
            for (var i = 1; i < player.Loadout.AbilitySlots.Count; i++)
                if (player.Loadout.AbilitySlots[i].AbilityId is { } aid && aid.StartsWith("item.", StringComparison.Ordinal))
                { anyAbilityTalisman = true; break; }
        }
        if (!anyEnabled && !anyAbilityTalisman) return [];
        // 存活战斗态直取：战斗边界收尾回调经 liveState 传入正在推进的遭遇，绕开 TryReuse
        // 的文档时钟守卫——窗口中段行动文档快照天然滞后于内存态，守卫失配只是「尚未回写」
        // 而非真错位；守卫仍保留给跨步复用场景，live 未持会话时退回文档快照路径。
        CombatEncounterState? liveBattle = liveState;
        if (liveBattle is null && live is not null && player.Actions[0] is QueuedAction liveAction
            && liveAction.Expedition is { } liveExpedition
            && live.TryReuse(liveAction, liveExpedition, out var liveEntry))
            liveBattle = liveEntry!.Battle;
        var actionBarId = Domain.GameData.AutoUseBar.For(catalog.Actions.Get(player.Actions[0].Intent.ActionId));
        List<AssetDelta>? deltas = null;
        var now = at.ToUnixTimeMilliseconds() / 1000d;
        // 触发求值上下文惰性构建：仅当存在带条件的消耗品时才装配战斗单位视图。
        CombatantState? evalCaster = null;
        CombatantState? evalTarget = null;
        List<CombatantState>? evalAllies = null;
        List<CombatantState>? evalEnemies = null;
        var totalCandidateCount = slots.Count + (anyAbilityTalisman ? Math.Max(0, player.Loadout.AbilitySlots.Count - 1) : 0);
        for (var i = 0; i < totalCandidateCount; i++)
        {
            var isAbilityTalismanSlot = i >= slots.Count;
            AutoUseSlotState slot = default!;
            string? slotItemId;
            double slotCooldownUntil;
            if (!isAbilityTalismanSlot)
            {
                slot = slots[i];
                if (!slot.Enabled || slot.ItemId is null || actionBarId is null
                    || !string.Equals(slot.ActionType, actionBarId, StringComparison.OrdinalIgnoreCase)
                    || slot.CooldownUntilUnixSeconds > now) continue;
                slotItemId = slot.ItemId;
                slotCooldownUntil = slot.CooldownUntilUnixSeconds;
            }
            else
            {
                if (!string.Equals(actionBarId, Domain.GameData.AutoUseBar.Combat, StringComparison.OrdinalIgnoreCase)) continue;
                var abilitySlotIdx = i - slots.Count + 1;
                slotItemId = player.Loadout.AbilitySlots[abilitySlotIdx].AbilityId;
                if (slotItemId is null || !slotItemId.StartsWith("item.", StringComparison.Ordinal)) continue;
                slotCooldownUntil = player.Combat.TalismanCooldowns.GetValueOrDefault(slotItemId);
                if (slotCooldownUntil > now) continue;
            }
            var id = new ItemId(slotItemId);
            if (!id.IsValid || !catalog.Items.TryGetValue(id, out var item)) continue;
            if (isAbilityTalismanSlot && !string.Equals(item.DisplaySubCategory, Domain.GameData.TechniqueCatalog.Talisman, StringComparison.Ordinal))
                continue;
            if (item.Consumable is not { IsContinuous: false, Durable: false } definition
                || !definition.UsableInBars.Contains(actionBarId!)
                || !catalog.RawItems[id.Value].TryGetProperty("consumable", out var raw)) continue;
            // 消耗品使用条件与技能共用同一套触发判定模型；玩家自定义条件按物品 ID 覆盖配表默认。
            var triggers = Domain.Combat.ConsumableTriggerResolver.Resolve(id.Value, definition,
                player.Loadout.ConsumableTriggers);
            // 订阅索引门控：边界增量未翻转本槽订阅事实时跳过——含「外部视图」兜底位与
            // 符箓灵力门槛通道，保证钱包脏/面板刷新/蓝量跨越仍能唤醒。
            if (wakeDelta is { } wd)
            {
                var mask = Domain.Combat.TriggerWakePlanner.ResolveMask(triggers, selfInEnemies: false)
                    .Union(new Domain.Combat.SlotWakeMask
                    {
                        Facts = Domain.Combat.BattleFact.External,
                        Self = (definition.Strike is not null || definition.ManaCostRatio > 0)
                            ? Domain.Combat.UnitChannel.Mana : 0,
                    });
                if (!mask.WakesOn(wd, liveBattle?.SelfIndex ?? 0)) continue;
            }
            if (triggers.Count > 0)
            {
                if (evalCaster is null)
                {
                    if (liveBattle is { } evalBattle) SyncLiveBattleToEvalDocs(player, evalBattle);
                    else live?.Flush();
                    evalCaster = BuildConsumableEvalUnits(player, catalog, out evalTarget, out evalAllies, out evalEnemies, now);
                }
                var elapsedSec = player.Actions[0].Expedition?.BattleElapsedSeconds ?? 0;
                RuntimeInstrumentation.Count("consumable.trigger_evals");
                if (!Domain.Combat.SkillTriggerEvaluator.MatchesAll(triggers, evalCaster, evalTarget,
                        evalAllies!, evalEnemies!, elapsedSec)) continue;
            }
            var consumed = 0d;
            if (deltas is not null)
                for (var j = 0; j < deltas.Count; j++)
                    if (deltas[j].Asset.ItemId == id) consumed += deltas[j].QuantityDelta;
            if (pendingSpends is not null && pendingSpends.TryGetValue(new AssetKey(id), out var pending))
                consumed += pending;
            if (wallet.Get(new AssetKey(id)) + consumed < 1) continue;
            double Read(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) ? value.GetDouble() : 0;
            var hp = Read(raw, "hitpointRestore");
            var mp = Read(raw, "manapointRestore");
            if (definition.CooldownSeconds <= 0) throw new DomainException(DomainErrorCode.InvalidArgument, "自动即时消耗品必须配置正冷却。");
            if (!raw.TryGetProperty("buffs", out var buffs) && hp == 0 && mp == 0 && definition.Strike is null) continue;
            if (evalCaster is null && (definition.Strike is not null || definition.ManaCostRatio > 0
                || string.Equals(item.DisplaySubCategory, Domain.GameData.TechniqueCatalog.Talisman, StringComparison.Ordinal)))
            {
                if (liveBattle is { } evalBattle) SyncLiveBattleToEvalDocs(player, evalBattle);
                else live?.Flush();
                evalCaster = BuildConsumableEvalUnits(player, catalog, out evalTarget, out evalAllies, out evalEnemies, now);
            }
            var cooldown = evalCaster is not null
                ? DeterministicCombatEngine.ResolveConsumableCooldownSeconds(item, definition, evalCaster.Stats.Traits, evalCaster.Stats.TraitLevels)
                : definition.CooldownSeconds;
            void CommitSlotCooldown()
            {
                if (!isAbilityTalismanSlot)
                {
                    player.Settings.AutoUseSlots[i] = slot with { CooldownUntilUnixSeconds = now + cooldown };
                    player.MarkDirty(PlayerSectionKind.Settings);
                }
                else
                {
                    player.Combat.TalismanCooldowns[id.Value] = now + cooldown;
                }
            }
            if (definition.Strike is { } strike)
            {
                var manaCost = DeterministicCombatEngine.ResolveConsumableManaCost(item, strike.ManaCostRatio, evalCaster!.Stats);
                if (player.Resources.Manapoints < manaCost - 1e-9) continue;
                var isAoE = string.Equals(strike.TargetType, "allEnemies", StringComparison.Ordinal);
                if (isAoE)
                {
                    var anyAliveEnemy = false;
                    for (var e = 0; e < evalEnemies!.Count; e++)
                        if (evalEnemies[e].IsAlive) { anyAliveEnemy = true; break; }
                    if (!anyAliveEnemy) continue;
                }
                else
                {
                    if (evalTarget is null || evalEnemies!.IndexOf(evalTarget) < 0) continue;
                }
                player.Resources.Manapoints = Math.Max(0, player.Resources.Manapoints - manaCost);
                SyncConsumableResourcesToBattle(player, player.Actions[0], liveBattle);
                var hasDirectDamage = strike.ManaDamageRatio > 0 || strike.SpellAttackRatio > 0
                    || strike.PhysicalAttackRatio > 0 || strike.FlatDamage > 0;
                var ranhunStacks = DeterministicCombatEngine.ResolveConsumableRanhunStacks(item, strike, evalCaster.Stats);
                var strikeElement = CombatTraitRules.ResolveDamageElement(evalCaster.Stats.Traits, strike.Element);
                var rawPower = hasDirectDamage
                    ? DeterministicCombatEngine.ResolveConsumableStrikePower(item, strike, evalCaster.EffectiveStats)
                    : 0;
                var strikeStats = evalCaster.EffectiveStats with
                {
                    AttackPower = rawPower,
                    DamageKind = strike.DamageKind,
                    Element = strikeElement,
                    TotalDamageBonusPoints = evalCaster.Stats.TotalDamageBonusPoints + evalCaster.BuffTotal(BuffEffectChannel.DamageBonusPoints)
                };
                var totalDamage = 0d;
                var anyCrit = false;
                var seed = liveBattle?.Seed ?? player.Actions[0].Expedition?.Seed ?? 0;
                var rollBase = (liveBattle?.Player.AttackCount ?? player.Actions[0].Expedition?.PlayerAttackCount ?? 0)
                    + (liveBattle?.Sequence ?? 0);
                for (var eIdx = 0; eIdx < evalEnemies!.Count; eIdx++)
                {
                    var evalFoe = evalEnemies[eIdx];
                    if (!evalFoe.IsAlive) continue;
                    if (!isAoE && !ReferenceEquals(evalFoe, evalTarget)) continue;
                    if (hasDirectDamage)
                    {
                        var liveTargetForFormula = liveBattle is { } lb && eIdx < lb.Enemies.Count ? lb.Enemies[eIdx] : evalFoe;
                        var isCrit = DeterministicCombatEngine.RollTalismanCrit(seed, 0UL, rollBase + eIdx,
                            liveBattle?.Player ?? evalCaster, liveTargetForFormula);
                        if (isCrit) anyCrit = true;
                        var dmg = DeterministicCombatEngine.ResolveStrikeDamage(strikeStats, liveTargetForFormula, isCrit);
                        dmg = ApplyStrikeDamage(player, player.Actions[0], eIdx, dmg, liveBattle, evalCaster, evalFoe, strikeElement);
                        totalDamage += dmg;
                        if (evalFoe.DefinitionId is { } strikeMonsterId
                            && player.Duel.ReportFor(player.Actions[0].Id) is { } strikeReport)
                        {
                            var strikeBook = strikeReport.CombatStats;
                            strikeBook.Register("player", player.DisplayName);
                            strikeBook.Register($"monster:{strikeMonsterId}",
                                catalog.CombatMonsters.TryGetValue(strikeMonsterId, out var strikeMonster)
                                    ? strikeMonster.Name : strikeMonsterId);
                            strikeBook.RecordHit("player", $"monster:{strikeMonsterId}", strike.DamageKind,
                                dmg, Math.Max(0, rawPower - dmg));
                            ActionReportBook.FlushCombatStats(player, player.Actions[0].Id);
                        }
                    }
                    if (ranhunStacks > 0 || strike.OnHitBuff is { ValueKind: JsonValueKind.Object })
                    {
                        ApplyStrikeTargetEffects(player.Actions[0], eIdx, ranhunStacks, strike.OnHitBuff,
                            item.Id.Value, liveBattle, evalCaster, evalFoe);
                    }
                    else if (liveBattle is { } notifyBattle && eIdx < notifyBattle.Enemies.Count)
                    {
                        CombatActSystem.NotifyUnitAttacked(notifyBattle, notifyBattle.Player, notifyBattle.Enemies[eIdx], notifyBattle.ElapsedSeconds);
                    }
                }
                CommitSlotCooldown();
                player.MarkDirty(PlayerSectionKind.Combat);
                player.MarkDirty(PlayerSectionKind.ActionQueue);
                (deltas ??= []).Add(new AssetDelta(player.PlayerId, new AssetKey(id), -1, "auto-use"));
                evalCaster = null;
                player.RecordStats(player.Actions[0].Id, now, new PlayerStatsDelta { LostItem = id, LostQuantity = 1 });
                var strikeMsg = hasDirectDamage
                    ? $"自动使用 {item.Name}，造成 {totalDamage:0.##} 点伤害{(anyCrit ? "（暴击）" : "")}。"
                    : ranhunStacks > 0
                        ? $"自动使用 {item.Name}，施加 {ranhunStacks:0.##} 层燃烧。"
                        : $"自动使用 {item.Name}。";
                events.Add(new SimulationEvent(at, "auto-use.activated", strikeMsg,
                    ItemDeltas: new Dictionary<ItemId, double> { [id] = -1 }));
                continue;
            }
            if (definition.ManaCostRatio > 0)
            {
                var selfManaCost = DeterministicCombatEngine.ResolveConsumableManaCost(item, definition.ManaCostRatio, evalCaster!.Stats);
                if (player.Resources.Manapoints < selfManaCost - 1e-9) continue;
                player.Resources.Manapoints = Math.Max(0, player.Resources.Manapoints - selfManaCost);
            }
            var hitpointsBeforePotion = player.Resources.Hitpoints;
            player.Resources.Hitpoints = Math.Min(player.Resources.MaxHitpoints, player.Resources.Hitpoints + hp);
            player.Resources.Manapoints = Math.Min(player.Resources.MaxManapoints, player.Resources.Manapoints + mp);
            if (player.Resources.Hitpoints > hitpointsBeforePotion)
            {
                var headAction = player.Actions[0];
                player.EnsureActionReport(headAction.Id,
                    catalog.Actions.Get(headAction.Intent.ActionId).Id,
                    catalog.Actions.Get(headAction.Intent.ActionId).Name, at);
                if (player.Duel.ReportFor(headAction.Id) is { } healReport)
                {
                    healReport.CombatStats.RecordHealing("player", player.DisplayName, autoRegen: false,
                        player.Resources.Hitpoints - hitpointsBeforePotion);
                    ActionReportBook.FlushCombatStats(player, headAction.Id);
                }
            }
            SyncConsumableResourcesToBattle(player, player.Actions[0], liveBattle);
            if (buffs.ValueKind == JsonValueKind.Array)
            {
                foreach (var buff in buffs.EnumerateArray())
                {
                    var buffId = buff.GetProperty("buffId").GetString()!;
                    var proposedActiveExp = now + Read(buff, "durationSeconds");
                    var existingActive = player.Buffs.Active.FirstOrDefault(current => current.BuffId == buffId);
                    var mergedActiveExp = existingActive is not null && !IsInfiniteOrBeyondMaxUnixBoundary(existingActive.ExpiresAtUnixSeconds)
                        ? Math.Max(existingActive.ExpiresAtUnixSeconds, proposedActiveExp)
                        : proposedActiveExp;
                    player.Buffs.Active.RemoveAll(current => current.BuffId == buffId);
                    player.Buffs.Active.Add(new(buffId, mergedActiveExp, 1,
                        buff.GetProperty("name").GetString()!, id.Value,
                        Effects: BuffEffectParser.Parse(buff, 1.0)));
                }
                if (player.Actions[0] is QueuedAction headQueued
                    && headQueued.Expedition is { Phase: CombatExpeditionPhase.Combat } battleExpedition)
                {
                    var battleBuffs = new List<CombatBuffState>();
                    foreach (var buff in buffs.EnumerateArray())
                    {
                        var duration = Read(buff, "durationSeconds");
                        if (duration <= 0) continue;
                        battleBuffs.Add(new CombatBuffState(buff.GetProperty("buffId").GetString()!,
                            battleExpedition.BattleElapsedSeconds + duration, 1,
                            buff.GetProperty("name").GetString()!, ResetOnWave: true,
                            Effects: BuffEffectParser.Parse(buff, 1.0)));
                    }
                    if (battleBuffs.Count > 0)
                    {
                        if (liveBattle is { } buffBattle)
                        {
                            var liveUnit = buffBattle.Player;
                            foreach (var battleBuff in battleBuffs)
                            {
                                var proposedExp = buffBattle.ElapsedSeconds + (battleBuff.ExpiresAtSeconds - battleExpedition.BattleElapsedSeconds);
                                var existingBattleBuff = liveUnit.Buffs.FirstOrDefault(b => b.BuffId == battleBuff.BuffId);
                                liveUnit.SetBuff(battleBuff with
                                {
                                    ExpiresAtSeconds = CombatTraitRules.MergeBuffExpiry(existingBattleBuff?.ExpiresAtSeconds, proposedExp)
                                });
                            }
                        }
                        if (battleExpedition.PlayerState is { } playerState)
                        {
                            var merged = playerState.Buffs is { Count: > 0 } existing ? existing.ToList() : [];
                            foreach (var battleBuff in battleBuffs)
                            {
                                var prev = merged.FirstOrDefault(current => current.BuffId == battleBuff.BuffId);
                                var mergedExp = CombatTraitRules.MergeBuffExpiry(prev?.ExpiresAtSeconds, battleBuff.ExpiresAtSeconds);
                                merged.RemoveAll(current => current.BuffId == battleBuff.BuffId);
                                merged.Add(battleBuff with { ExpiresAtSeconds = mergedExp });
                            }
                            headQueued.Expedition = battleExpedition with
                            {
                                PlayerState = playerState with { Buffs = merged }
                            };
                            player.MarkDirty(PlayerSectionKind.ActionQueue);
                        }
                    }
                }
            }
            CommitSlotCooldown();
            player.MarkDirty(PlayerSectionKind.Buffs);
            player.MarkDirty(PlayerSectionKind.Combat);
            (deltas ??= []).Add(new AssetDelta(player.PlayerId, new AssetKey(id), -1, "auto-use"));
            evalCaster = null;
            player.RecordStats(player.Actions[0].Id, now, new PlayerStatsDelta { LostItem = id, LostQuantity = 1 });
            events.Add(new SimulationEvent(at, "auto-use.activated", $"自动使用 {item.Name}。", ItemDeltas: new Dictionary<ItemId, double> { [id] = -1 }));
        }
        if (deltas is { Count: > 0 }) RuntimeInstrumentation.Count("consumable.activations", deltas.Count);
        return deltas ?? [];
    }

    /// <summary>
    /// 为消耗品触发判定装配求值单位视图：施放方为当前玩家快照；战斗进行中敌方取
    /// 当前波存活单位，「目标」对象按当前有效威胁最高者确定性解析（非随机抽取）。
    /// 敌方单位必须同步应用行动难度缩放、建筑削弱与怪物阶层（Rank），保证妖王/精英与百分比条件精确求值。
    /// </summary>
    internal static CombatantState BuildConsumableEvalUnits(IPlayerResourceContext player, GameDataCatalog catalog,
        out CombatantState? target, out List<CombatantState> allies, out List<CombatantState> enemies,
        double? nowUnix = null)
    {
        // 战斗进行中沿用入战定格特质（与 RestoreCombatBattle / RefreshStats 同口径），防止战中换特质导致雷击符转火与燃魂口径错位。
        var expedition = player.Actions[0].Expedition;
        var casterAttributes = expedition is { Phase: CombatExpeditionPhase.Combat, PlayerState: { } combatPlayerState }
            ? PlayerAttributeResolver.ResolveForCombat(player, catalog, combatPlayerState.Traits)
            : player.ResolveAttributes(catalog);
        var caster = new CombatantState("player", casterAttributes.ToCombatant(player.Progression.CombatExperience))
        {
            Hitpoints = player.Resources.Hitpoints,
            Manapoints = player.Resources.Manapoints
        };
        var activeBuffs = player.Buffs.Active;
        caster.ExternalBuffStacks = buffId => SumUnmergedActiveBuffStacks(caster, activeBuffs, buffId, nowUnix);
        if (player.Actions[0].Expedition?.PlayerState?.Buffs is { } battleBuffs) caster.ReplaceBuffs(battleBuffs);
        allies = [caster];
        enemies = [];
        // 求值单位与战内口径一致：先按行动难度缩放，再按当前节点封魔阵类建筑投影折减，并保留怪物 Rank。
        var weaken = player.World.EffectiveNodeBuildingEffects.MonsterWeakenRatio;
        var difficulty = player.Actions[0].Participation?.Spec?.Difficulty ?? player.Actions[0].Intent.Difficulty;
        if (player.Actions[0].Expedition is { Phase: CombatExpeditionPhase.Combat, Enemies: { Count: > 0 } waveEnemies })
            for (var i = 0; i < waveEnemies.Count; i++)
            {
                var enemy = waveEnemies[i];
                if (!catalog.CombatMonsters.TryGetValue(enemy.EnemyId, out var monster)) continue;
                var scaledStats = CombatDifficultyRules.ScaleMonsterStats(monster.Stats, difficulty);
                var unit = new CombatantState($"enemy:{i}", NodeBuildingRules.WeakenMonsterStats(scaledStats, weaken))
                {
                    Hitpoints = enemy.State.Hitpoints,
                    Manapoints = enemy.State.Manapoints,
                    Threat = enemy.State.Threat,
                    ThreatBonusPoints = enemy.State.ThreatBonusPoints,
                    // 求值单位同样携带怪物定义 ID 与阶层：打击记账按 monster:{defId} 归并，is-boss/is-elite/has-boss/has-elite 依赖 Rank。
                    DefinitionId = enemy.EnemyId,
                    Rank = monster.Rank
                };
                if (enemy.State.Buffs is { Count: > 0 } buffs) unit.ReplaceBuffs(buffs);
                enemies.Add(unit);
            }
        // 目标 = 存活敌中有效威胁最高者（MaxBy 语义：严格更大才替换，平局保留先见者）。
        CombatantState? best = null;
        for (var i = 0; i < enemies.Count; i++)
            if (enemies[i].IsAlive && (best is null || enemies[i].EffectiveThreat > best.EffectiveThreat))
                best = enemies[i];
        target = best;
        return caster;
    }

    /// <summary>战外 Buff 层数合计：注入战斗单位 ExternalBuffStacks 的求值体，无 LINQ 分配。</summary>
    internal static double SumActiveBuffStacks(VersionedList<ActiveBuff> actives, string? buffId)
    {
        var total = 0d;
        for (var i = 0; i < actives.Count; i++)
            if (buffId is null || actives[i].BuffId == buffId) total += actives[i].Stacks;
        return total;
    }

    /// <summary>
    /// 战外 Buff 层数求值（去重并可选过期过滤）：跳过已存在于战内单位 Buff 列表中的同名即时药水副本，
    /// 并在提供当前墙钟时忽略已到期的战外条目，防止 buff-stacks 在战内被双倍计入或到期后残留。
    /// </summary>
    internal static double SumUnmergedActiveBuffStacks(CombatantState unit, IReadOnlyList<ActiveBuff> actives,
        string? buffId, double? nowUnix = null)
    {
        var total = 0d;
        var inBattle = unit.Buffs;
        for (var i = 0; i < actives.Count; i++)
        {
            var entry = actives[i];
            var entryId = entry.BuffId;
            if (buffId is not null && entryId != buffId) continue;
            if (nowUnix is { } now && entry.ExpiresAtUnixSeconds <= now) continue;
            var duplicated = false;
            for (var j = 0; j < inBattle.Count; j++)
            {
                var curId = inBattle[j].BuffId;
                if (curId == entryId
                    || (curId.Length > entryId.Length && curId[entryId.Length] == ':'
                        && curId.StartsWith(entryId, StringComparison.Ordinal)))
                {
                    duplicated = true;
                    break;
                }
            }
            if (!duplicated) total += entry.Stacks;
        }
        return total;
    }

    /// <summary>
    /// 触发求值前把存活战斗态压回行动文档与资源池镜像：判定一律读行动文档快照与战外
    /// 资源池，窗口中段两者都是上一个结算边界的旧值——不压回会让自身血蓝百分比、敌方
    /// 存活与开战秒数条件全部按窗口起点判定，即时消耗品效果也无法写回存活单位。
    /// </summary>
    private static void SyncLiveBattleToEvalDocs(IPlayerResourceContext player, CombatEncounterState battle)
    {
        player.Resources.Hitpoints = battle.Player.Hitpoints;
        player.Resources.Manapoints = battle.Player.Manapoints;
        if (player.Actions[0].Expedition is { Phase: CombatExpeditionPhase.Combat } expedition)
            player.Actions[0].Expedition = CombatActionRules.CaptureCombatState(battle, expedition);
    }

    /// <summary>
    /// 消耗品改写战外资源池（雷击符扣蓝、药品回血回蓝）后，把数值压回战斗内单位与
    /// 行动文档镜像：战斗步以战内单位为权威回写战外资源池，不同步会把消耗品效果覆盖丢失。
    /// 有存活战斗态时直接改内存单位；否则改写文档标量镜像与玩家完整快照。
    /// </summary>
    internal static void SyncConsumableResourcesToBattle(IPlayerResourceContext player,
        IPlayerActionState action, CombatEncounterState? liveBattle)
    {
        if (liveBattle is { } live)
        {
            var caster = live.Player;
            caster.Hitpoints = Math.Clamp(player.Resources.Hitpoints, 0, caster.Stats.MaxHitpoints);
            caster.Manapoints = Math.Clamp(player.Resources.Manapoints, 0, caster.Stats.MaxManapoints);
            return;
        }
        if (action.Expedition is not { Phase: CombatExpeditionPhase.Combat } snap) return;
        // 战外资源可能因会话结算等路径暂超当前上限；与 live 路径同口径钳制后再落文档镜像，
        // 否则恢复校验按模板上限判损坏，实体将无法装载。
        var clampedHitpoints = Math.Clamp(player.Resources.Hitpoints, 0, player.Resources.MaxHitpoints);
        var clampedManapoints = Math.Clamp(player.Resources.Manapoints, 0, player.Resources.MaxManapoints);
        action.Expedition = snap with
        {
            PlayerHitpoints = clampedHitpoints,
            PlayerManapoints = clampedManapoints,
            PlayerState = snap.PlayerState is { } snapshot
                ? snapshot with
                {
                    Hitpoints = clampedHitpoints,
                    Manapoints = clampedManapoints
                }
                : snap.PlayerState
        };
    }

    /// <summary>
    /// 雷击符伤害落地：优先写回复用的内存战斗会话，否则改写行动文档中的
    /// 波次快照；全灭时同步 PlayerWon 结局，保证恢复校验（玩家赢且满血）一致。
    /// </summary>
    internal static double ApplyStrikeDamage(IPlayerResourceContext player, IPlayerActionState action,
        int targetIndex, double damage, CombatEncounterState? liveBattle, CombatantState attacker, CombatantState target, CombatElement element)
    {
        if (liveBattle is { } live && targetIndex < live.Enemies.Count)
        {
            var unit = live.Enemies[targetIndex];
            var settled = CombatDamageSettlement.Apply(live, live.Player, unit, damage);
            CombatTraitReactions.ApplyRanhun(live.Player, unit, element, settled.Actual, live.ElapsedSeconds);
            if (!live.Enemies.Any(static e => e.IsAlive))
            {
                live.Outcome = CombatOutcome.PlayerWon;
                List<CombatEvent>? dummyEvents = null;
                CombatTraitReactions.SettleWaveEndShields(live, ref dummyEvents);
            }
            return settled.Damage;
        }
        if (action.Expedition is not { Phase: CombatExpeditionPhase.Combat } snap
            || snap.Enemies is not { } waveEnemies || targetIndex >= waveEnemies.Count) return 0;
        var updated = waveEnemies.ToArray();
        var enemy = updated[targetIndex];
        var result = CombatDamageSettlement.Apply(null, attacker, target, damage);
        CombatTraitReactions.ApplyRanhun(attacker, target, element, result.Actual, snap.BattleElapsedSeconds);
        damage = result.Damage;
        updated[targetIndex] = enemy with
        {
            State = enemy.State with { Hitpoints = target.Hitpoints, Shield = target.Shield, Buffs = target.Buffs.ToArray() }
        };
        var allDead = true;
        for (var i = 0; i < updated.Length; i++)
            if (updated[i].State.Hitpoints > 0) { allDead = false; break; }
        action.Expedition = snap with
        {
            Enemies = updated,
            Outcome = allDead ? CombatOutcome.PlayerWon : snap.Outcome
        };
        return damage;
    }

    /// <summary>即时打击消耗品附带效果（怒焰符燃烧叠层、定神符麻痹等）落账至存活战斗单位或文档快照。</summary>
    private static void ApplyStrikeTargetEffects(IPlayerActionState action, int targetIndex, double ranhunStacks,
        JsonElement? onHitBuff, string itemId, CombatEncounterState? liveBattle,
        CombatantState evalCaster, CombatantState evalTarget)
    {
        if (liveBattle is { } live && targetIndex < live.Enemies.Count)
        {
            var unit = live.Enemies[targetIndex];
            if (ranhunStacks > 0 && unit.IsAlive)
                CombatTraitReactions.AddRanhunStacks(live.Player, unit, ranhunStacks, live.ElapsedSeconds, live);
            if (onHitBuff is { ValueKind: JsonValueKind.Object } spec && unit.IsAlive)
                CombatActSystem.GrantBuff(spec, live.Player, unit, live.ElapsedSeconds, 1.0, itemId, live, includeCasterSkillBonus: false);
            CombatActSystem.NotifyUnitAttacked(live, live.Player, unit, live.ElapsedSeconds);
            return;
        }
        if (action.Expedition is not { Phase: CombatExpeditionPhase.Combat } snap
            || snap.Enemies is not { } waveEnemies || targetIndex >= waveEnemies.Count) return;
        if (ranhunStacks > 0 && evalTarget.IsAlive)
            CombatTraitReactions.AddRanhunStacks(evalCaster, evalTarget, ranhunStacks, snap.BattleElapsedSeconds);
        if (onHitBuff is { ValueKind: JsonValueKind.Object } snapSpec && evalTarget.IsAlive)
            CombatActSystem.GrantBuff(snapSpec, evalCaster, evalTarget, snap.BattleElapsedSeconds, 1.0, itemId, null, includeCasterSkillBonus: false);
        var updated = waveEnemies.ToArray();
        var enemy = updated[targetIndex];
        updated[targetIndex] = enemy with
        {
            State = enemy.State with { Buffs = evalTarget.Buffs.ToArray() }
        };
        action.Expedition = snap with { Enemies = updated };
    }

    /// <summary>
    /// durable 持续消耗品（聚灵阵盘等）的战外同步：槽位启用、行动域匹配且钱包持有时
    /// 把配表 buffs 注入 ActiveBuff（+∞ 过期随槽位驻留，随 5 秒回复边界生效）；
    /// 条件失效时该物品注入的 Buff 立即清除。durable 不消耗物品、不耗竭时长。
    /// </summary>
    /// <summary>durable 注入 buff 的唯一标记：永不自然过期（注入写 MaxValue 时刻，Strict JSON 下 +∞ 不可落库）。</summary>
    private static bool IsDurableManagedBuff(ActiveBuff buff)
        => !double.IsFinite(buff.ExpiresAtUnixSeconds) || buff.ExpiresAtUnixSeconds >= double.MaxValue;

    internal static bool SyncDurableConsumableBuffs(IPlayerResourceContext player, GameDataCatalog gameData,
        AssetWallet wallet)
    {
        var slots = player.Settings.AutoUseSlots;
        var activeBuffs = player.Buffs.Active;
        // 无 durable 槽位且无 durable 注入残留时整段跳过：避免每资源边界分配期望清单。
        var managed = false;
        for (var i = 0; i < activeBuffs.Count; i++)
            if (IsDurableManagedBuff(activeBuffs[i])) { managed = true; break; }
        for (var i = 0; !managed && i < slots.Count; i++)
        {
            var text = slots[i].ItemId;
            if (text is not null && new ItemId(text) is { IsValid: true } probe
                && gameData.Items.TryGetValue(probe, out var probeItem)
                && probeItem.Consumable is { Durable: true }) managed = true;
        }
        if (!managed) return false;
        var changed = false;
        var actionBarId = player.Actions.Count > 0
            ? Domain.GameData.AutoUseBar.For(gameData.Actions.Get(player.Actions[0].Intent.ActionId))
            : null;
        var desired = new List<ActiveBuff>();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.ItemId is not { } text || new ItemId(text) is not { IsValid: true } id
                || !gameData.Items.TryGetValue(id, out var item) || item.Consumable is not { Durable: true }) continue;
            if (actionBarId is not { } barId || !slot.Enabled
                || !item.Consumable.UsableInBars.Contains(barId)
                || !string.Equals(slot.ActionType, barId, StringComparison.OrdinalIgnoreCase)
                || wallet.Get(new AssetKey(item.Id)) < 1
                || !gameData.RawItems[id.Value].TryGetProperty("consumable", out var raw)
                || !raw.TryGetProperty("buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array) continue;
            foreach (var buff in buffs.EnumerateArray())
            {
                var buffId = buff.GetProperty("buffId").GetString()!;
                // durable buff 用 double.MaxValue 而非 +∞：Strict JSON 下 +∞ 不可序列化，MaxValue 永不触发过期且可落库。
                desired.Add(new ActiveBuff(buffId, double.MaxValue, 1,
                    buff.TryGetProperty("name", out var nameEl) ? nameEl.GetString()! : item.Name,
                    item.Id.Value, Effects: BuffEffectParser.Parse(buff, 1.0)));
            }
        }
        // durable 注入按标记全量接管：凡不在期望集合内的永不自然过期 buff 一律清除，
        // 覆盖撤槽、配表去 durable、物品失效等 SourceItemId 已不可追的全部残留路径。
        for (var i = activeBuffs.Count - 1; i >= 0; i--)
        {
            var current = activeBuffs[i];
            if (!IsDurableManagedBuff(current)) continue;
            var kept = false;
            for (var j = 0; j < desired.Count; j++)
                if (desired[j].BuffId == current.BuffId && desired[j].SourceItemId == current.SourceItemId) { kept = true; break; }
            if (!kept)
            {
                activeBuffs.RemoveAt(i);
                player.MarkDirty(PlayerSectionKind.Buffs);
                changed = true;
            }
        }
        // 再补入/更新期望的注入：同 buffId + SourceItemId 且字段全等则跳过。
        foreach (var want in desired)
        {
            var existing = -1;
            for (var i = 0; i < activeBuffs.Count; i++)
                if (activeBuffs[i].BuffId == want.BuffId && activeBuffs[i].SourceItemId == want.SourceItemId)
                { existing = i; break; }
            if (existing >= 0)
            {
                var cur = activeBuffs[existing];
                if (cur.ExpiresAtUnixSeconds == want.ExpiresAtUnixSeconds && cur.Stacks == want.Stacks
                    && cur.Name == want.Name && DurableEffectsEqual(cur.Effects, want.Effects)) continue;
            }
            activeBuffs.RemoveAll(b => b.BuffId == want.BuffId);
            activeBuffs.Add(want);
            player.MarkDirty(PlayerSectionKind.Buffs);
            changed = true;
        }
        return changed;
    }

    /// <summary>durable 期望条目的效果载荷相等比较（BuffEffect 为值 record；列表按序逐一比较）。</summary>
    private static bool DurableEffectsEqual(IReadOnlyList<BuffEffect>? left, IReadOnlyList<BuffEffect>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (left[i] != right[i]) return false;
        return true;
    }
}
