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
        instantDeltas = ActivateInstantConsumables(player, gameData, wallet, now, events, live);
        // 即时消耗品扣件/注入 Buff/冷却回写是真实可见变更：不计 changed 会让资产已落库
        // 但 SimulationResult.Changed=false，推进推送跳过该玩家直到下一条命令。
        changed |= instantDeltas.Count > 0;
        // 真实成员不推进回复余数；不能用被冻结的余数重复切个人资源窗口。
        var waitsForSession = player.Actions.Count > 0 && player.Actions[0].Participation is not null
            && player is not PlayerAggregate { IsParticipationSessionContainer: true };
        var naturalStep = waitsForSession ? double.PositiveInfinity : 5 - player.Resources.RegenCarrySeconds;
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
        // 暂存区最早到期时刻并入自然边界：模拟窗口正好切在结算点，与 Buff 到期同一调度口径。
        if (player.Duel.ActiveDuelId is null && player.Duel.StagedItems.Count > 0)
        {
            var untilStaged = player.Duel.StagedItems[0].AcquiredAtUnixSeconds
                + gameData.Duel.StagedSettleSeconds - nowUnix;
            if (untilStaged > 1e-9)
            {
                naturalStep = Math.Min(naturalStep, untilStaged);
                step = Math.Min(step, untilStaged);
            }
        }
        // 参与会话权威散场时刻并入自然边界：模拟窗口正好切在散场点，参与行动移除由行动准入在下一拍执行。
        if (player.Actions.Count > 0 && player.Actions[0].Participation is { } session
            && double.IsFinite(session.EndsAtUnixSeconds))
        {
            var untilSessionEnd = session.EndsAtUnixSeconds - nowUnix;
            if (untilSessionEnd > 1e-9)
            {
                naturalStep = Math.Min(naturalStep, untilSessionEnd);
                step = Math.Min(step, untilSessionEnd);
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
        var stagedItems = SettleResourceItems(player, at.ToUnixTimeMilliseconds() / 1000d,
            gameData.Duel.StagedSettleSeconds);
        // 暂存物品入账写入 Duel 账本与资产差量：不计 changed 会让入账已落库但无推送下发。
        var changed = stagedItems.Length > 0;
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
        changed |= AdvanceIdleSkillResources(player, stepSeconds);
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

    internal static bool AdvanceIdleSkillResources(IPlayerResourceContext player, double stepSeconds)
    {
        if (player.Actions.Count > 0 && player.Actions[0].Participation is not null
            && player is not PlayerAggregate { IsParticipationSessionContainer: true }) return false;
        var expedition = player.Actions.Count > 0 ? player.Actions[0].Expedition : null;
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

    internal static List<AssetDelta> ActivateInstantConsumables(IPlayerResourceContext player,
        GameDataCatalog catalog, AssetWallet wallet, DateTimeOffset at, SimulationEventTail events,
        LiveCombatBattle? live = null)
    {
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
        if (!anyEnabled) return [];
        var actionBarId = Domain.GameData.AutoUseBar.For(catalog.Actions.Get(player.Actions[0].Intent.ActionId));
        List<AssetDelta>? deltas = null;
        var now = at.ToUnixTimeMilliseconds() / 1000d;
        // 触发求值上下文惰性构建：仅当存在带条件的消耗品时才装配战斗单位视图。
        CombatantState? evalCaster = null;
        CombatantState? evalTarget = null;
        List<CombatantState>? evalAllies = null;
        List<CombatantState>? evalEnemies = null;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Enabled || slot.ItemId is null || actionBarId is null
                || !string.Equals(slot.ActionType, actionBarId, StringComparison.OrdinalIgnoreCase)
                || slot.CooldownUntilUnixSeconds > now) continue;
            var id = new ItemId(slot.ItemId);
            var item = catalog.Items[id];
            if (item.Consumable is not { IsContinuous: false, Durable: false } definition
                || !definition.UsableInBars.Contains(actionBarId)
                || !catalog.RawItems[id.Value].TryGetProperty("consumable", out var raw)) continue;
            // 消耗品使用条件与技能共用同一套触发判定模型；求值失败一律跳过本槽。
            if (definition.Triggers is { Count: > 0 } triggers)
            {
                if (evalCaster is null)
                {
                    // 触发器求值读取行动文档中的敌人快照：复用会话需先把内存态全量回写。
                    live?.Flush();
                    evalCaster = BuildConsumableEvalUnits(player, catalog, out evalTarget, out evalAllies, out evalEnemies);
                }
                if (!Domain.Combat.SkillTriggerEvaluator.MatchesAll(triggers, evalCaster, evalTarget,
                        evalAllies!, evalEnemies!)) continue;
            }
            // 已记账扣件汇总按原列表顺序逐条累加（与 LINQ Where+Sum 同序，位级一致）。
            var consumed = 0d;
            if (deltas is not null)
                for (var j = 0; j < deltas.Count; j++)
                    if (deltas[j].Asset.ItemId == id) consumed += deltas[j].QuantityDelta;
            if (wallet.Get(new AssetKey(id)) + consumed < 1) continue;
            double Read(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) ? value.GetDouble() : 0;
            var hp = Read(raw, "hitpointRestore");
            var mp = Read(raw, "manapointRestore");
            var cooldown = definition.CooldownSeconds;
            if (!raw.TryGetProperty("buffs", out var buffs) && hp == 0 && mp == 0 && definition.Strike is null) continue;
            if (cooldown <= 0) throw new DomainException(DomainErrorCode.InvalidArgument, "自动即时消耗品必须配置正冷却。");
            if (definition.Strike is { } strike)
            {
                // 雷击符打击（总纲 §20.2）：扣施放者灵力后按 Spell 链对当前主要敌人造成一次
                // 无命中/暴击掷点的伤害；灵力不足或无存活目标则本次不激活。
                if (evalCaster is null)
                {
                    live?.Flush();
                    evalCaster = BuildConsumableEvalUnits(player, catalog, out evalTarget, out evalAllies, out evalEnemies);
                }
                var manaCost = strike.ManaCostRatio * evalCaster.Stats.MaxManapoints;
                if (evalTarget is null || player.Resources.Manapoints < manaCost - 1e-9) continue;
                var targetIndex = evalEnemies!.IndexOf(evalTarget);
                if (targetIndex < 0) continue;
                // 打击威力 = (灵力倍率×灵力上限 + 术攻倍率×术攻) ×(1+技能增幅点数/100)，
                // 增幅并入原始威力乘位，与技能命中口径一致（总纲 §7.7.1）。
                var strikeStats = evalCaster.Stats with
                {
                    AttackPower = (strike.ManaDamageRatio * evalCaster.Stats.MaxManapoints
                        + strike.SpellAttackRatio * evalCaster.Stats.SpellAttack)
                        * PercentValue.Multiplier(evalCaster.Stats.SkillBonusPoints, PercentCurveMode.Linear),
                    DamageKind = CombatDamageKind.Spell,
                    Element = strike.Element
                };
                var damage = DeterministicCombatEngine.ResolveStrikeDamage(strikeStats, evalTarget);
                player.Resources.Manapoints = Math.Max(0, player.Resources.Manapoints - manaCost);
                SyncConsumableResourcesToBattle(player, player.Actions[0], live);
                ApplyStrikeDamage(player, player.Actions[0], targetIndex, damage, live);
                // 雷击符打击计入行动统计簿：本机术法输出；目标怪按类型桶计术法承伤与减免（原始威力 − 实收）。
                if (evalTarget!.DefinitionId is { } strikeMonsterId
                    && player.Duel.ReportFor(player.Actions[0].Id) is { } strikeReport)
                {
                    var strikeBook = strikeReport.CombatStats;
                    strikeBook.Register("player", player.DisplayName);
                    strikeBook.Register($"monster:{strikeMonsterId}",
                        catalog.CombatMonsters.TryGetValue(strikeMonsterId, out var strikeMonster)
                            ? strikeMonster.Name : strikeMonsterId);
                    strikeBook.RecordHit("player", $"monster:{strikeMonsterId}", CombatDamageKind.Spell,
                        damage, Math.Max(0, strikeStats.AttackPower - damage));
                    ActionReportBook.FlushCombatStats(player, player.Actions[0].Id);
                }
                player.Settings.AutoUseSlots[i] = slot with { CooldownUntilUnixSeconds = now + cooldown };
                player.MarkDirty(PlayerSectionKind.Settings);
                player.MarkDirty(PlayerSectionKind.Combat);
                player.MarkDirty(PlayerSectionKind.ActionQueue);
                (deltas ??= []).Add(new AssetDelta(player.PlayerId, new AssetKey(id), -1, "auto-use"));
                // 即时消耗品扣件计入队首行动账本「失去」物品。
                player.RecordStats(player.Actions[0].Id, now, new PlayerStatsDelta { LostItem = id, LostQuantity = 1 });
                events.Add(new SimulationEvent(at, "auto-use.activated", $"自动使用 {item.Name}，造成 {damage:0.##} 点伤害。",
                    ItemDeltas: new Dictionary<ItemId, double> { [id] = -1 }));
                continue;
            }
            var hitpointsBeforePotion = player.Resources.Hitpoints;
            player.Resources.Hitpoints = Math.Min(player.Resources.MaxHitpoints, player.Resources.Hitpoints + hp);
            player.Resources.Manapoints = Math.Min(player.Resources.MaxManapoints, player.Resources.Manapoints + mp);
            // 药品瞬回计「其他治疗」输出。即时消耗品只限战斗行动域；激活可能发生在行动首步的
            // T0 资源边界（先于远征初始化），此时按队首行动口径补建账本再入账。
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
            // 战斗中即时药品同样要把恢复量压回战内单位/文档镜像，否则下一战斗步以战内值覆盖战外资源池。
            SyncConsumableResourcesToBattle(player, player.Actions[0], live);
            if (buffs.ValueKind == JsonValueKind.Array)
            {
                foreach (var buff in buffs.EnumerateArray())
                {
                    var buffId = buff.GetProperty("buffId").GetString()!;
                    player.Buffs.Active.RemoveAll(current => current.BuffId == buffId);
                    player.Buffs.Active.Add(new(buffId, now + Read(buff, "durationSeconds"), 1,
                        buff.GetProperty("name").GetString()!, id.Value, Read(buff, "hpRegen"), Read(buff, "mpRegen"),
                        Read(buff, "attackSpeedPoints"), Read(buff, "damageAbsorptionPoints"), null,
                        Read(buff, "hpRegenPostRatio"), Read(buff, "mpRegenPostRatio")));
                }
                // 战内同口径注入（与组队会话 EvaluateInstant 一致）：战内回复/免伤只读单位 Buff 列表，
                // 仅写战外 ActiveBuff 会让药效在下一步战斗快照重建时丢失——表现为「药已扣件却不回血、
                // 角色身上也没有 Buff 图标」。到期时刻按战斗时钟落账，文档镜像与存活会话同时写入：
                // 会话复用时走内存单位，跨推进重建时走 PlayerState 快照。
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
                            Read(buff, "hpRegen"), Read(buff, "mpRegen"), Read(buff, "attackSpeedPoints"),
                            Read(buff, "damageAbsorptionPoints"), 0, Read(buff, "hpRegenPostRatio"),
                            Read(buff, "mpRegenPostRatio"), 0, 0, buff.GetProperty("name").GetString()!,
                            ResetOnWave: true));
                    }
                    if (battleBuffs.Count > 0)
                    {
                        if (live is not null && live.TryReuse(headQueued, battleExpedition, out var liveEntry)
                            && liveEntry is { } liveSession)
                        {
                            var liveUnit = liveSession.Battle.Player;
                            foreach (var battleBuff in battleBuffs)
                            {
                                liveUnit.Buffs.RemoveAll(current => current.BuffId == battleBuff.BuffId);
                                liveUnit.Buffs.Add(battleBuff with { ExpiresAtSeconds = liveSession.Battle.ElapsedSeconds
                                    + (battleBuff.ExpiresAtSeconds - battleExpedition.BattleElapsedSeconds) });
                            }
                        }
                        if (battleExpedition.PlayerState is { } playerState)
                        {
                            var merged = playerState.Buffs is { Count: > 0 } existing ? existing.ToList() : [];
                            foreach (var battleBuff in battleBuffs)
                            {
                                merged.RemoveAll(current => current.BuffId == battleBuff.BuffId);
                                merged.Add(battleBuff);
                            }
                            headQueued.Expedition = battleExpedition with
                            {
                                PlayerState = playerState with { Buffs = merged }
                            };
                            // 战内 Buff 快照随行动队列分段下发：不标脏会让前端等到下一步战斗推进才看到图标。
                            player.MarkDirty(PlayerSectionKind.ActionQueue);
                        }
                    }
                }
            }
            player.Settings.AutoUseSlots[i] = slot with { CooldownUntilUnixSeconds = now + cooldown };
            player.MarkDirty(PlayerSectionKind.Settings);
            player.MarkDirty(PlayerSectionKind.Buffs);
            player.MarkDirty(PlayerSectionKind.Combat);
            (deltas ??= []).Add(new AssetDelta(player.PlayerId, new AssetKey(id), -1, "auto-use"));
            // 即时消耗品扣件计入队首行动账本「失去」物品。
            player.RecordStats(player.Actions[0].Id, now, new PlayerStatsDelta { LostItem = id, LostQuantity = 1 });
            events.Add(new SimulationEvent(at, "auto-use.activated", $"自动使用 {item.Name}。", ItemDeltas: new Dictionary<ItemId, double> { [id] = -1 }));
        }
        return deltas ?? [];
    }

    /// <summary>
    /// 为消耗品触发判定装配求值单位视图：施放方为当前玩家快照；战斗进行中敌方取
    /// 当前波存活单位，「目标」对象按当前有效威胁最高者确定性解析（非随机抽取）。
    /// </summary>
    internal static CombatantState BuildConsumableEvalUnits(IPlayerResourceContext player, GameDataCatalog catalog,
        out CombatantState? target, out List<CombatantState> allies, out List<CombatantState> enemies)
    {
        var caster = new CombatantState("player", player.ResolveAttributes(catalog).ToCombatant(player.Progression.CombatExperience))
        {
            Hitpoints = player.Resources.Hitpoints,
            Manapoints = player.Resources.Manapoints
        };
        var activeBuffs = player.Buffs.Active;
        caster.ExternalBuffStacks = buffId => SumActiveBuffStacks(activeBuffs, buffId);
        allies = [caster];
        enemies = [];
        // 求值单位与战内口径一致：封魔阵类建筑削弱按当前节点投影同步折减怪物面板。
        var weaken = player.World.EffectiveNodeBuildingEffects.MonsterWeakenRatio;
        if (player.Actions[0].Expedition is { Phase: CombatExpeditionPhase.Combat, Enemies: { Count: > 0 } waveEnemies })
            for (var i = 0; i < waveEnemies.Count; i++)
            {
                var enemy = waveEnemies[i];
                if (!catalog.CombatMonsters.TryGetValue(enemy.EnemyId, out var monster)) continue;
                var unit = new CombatantState($"enemy:{i}", NodeBuildingRules.WeakenMonsterStats(monster.Stats, weaken))
                {
                    Hitpoints = enemy.State.Hitpoints,
                    Manapoints = enemy.State.Manapoints,
                    Threat = enemy.State.Threat,
                    ThreatBonusPoints = enemy.State.ThreatBonusPoints,
                    // 求值单位同样携带怪物定义 ID：打击记账按 monster:{defId} 类型桶归并。
                    DefinitionId = enemy.EnemyId
                };
                if (enemy.State.Buffs is { Count: > 0 } buffs) unit.Buffs.AddRange(buffs);
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
    /// 消耗品改写战外资源池（雷击符扣蓝、药品回血回蓝）后，把数值压回战斗内单位与
    /// 行动文档镜像：战斗步以战内单位为权威回写战外资源池，不同步会把消耗品效果覆盖丢失。
    /// 复用会话命中时改内存战斗态；否则改写文档标量镜像与玩家完整快照。
    /// </summary>
    internal static void SyncConsumableResourcesToBattle(IPlayerResourceContext player,
        IPlayerActionState action, LiveCombatBattle? live)
    {
        if (live is not null && action is QueuedAction queued && action.Expedition is { } expedition
            && live.TryReuse(queued, expedition, out var session) && session is { } liveSession)
        {
            var caster = liveSession.Battle.Player;
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
    internal static void ApplyStrikeDamage(IPlayerResourceContext player, IPlayerActionState action,
        int targetIndex, double damage, LiveCombatBattle? live)
    {
        if (live is not null && action is QueuedAction queued && action.Expedition is { } expedition
            && live.TryReuse(queued, expedition, out var session)
            && session is { } liveSession && targetIndex < liveSession.Battle.Enemies.Count)
        {
            var unit = liveSession.Battle.Enemies[targetIndex];
            unit.Hitpoints = Math.Max(0, unit.Hitpoints - damage);
            if (!liveSession.Battle.Enemies.Any(static e => e.IsAlive))
                liveSession.Battle.Outcome = CombatOutcome.PlayerWon;
            return;
        }
        if (action.Expedition is not { Phase: CombatExpeditionPhase.Combat } snap
            || snap.Enemies is not { } waveEnemies || targetIndex >= waveEnemies.Count) return;
        var updated = waveEnemies.ToArray();
        var enemy = updated[targetIndex];
        updated[targetIndex] = enemy with
        {
            State = enemy.State with { Hitpoints = Math.Max(0, enemy.State.Hitpoints - damage) }
        };
        var allDead = true;
        for (var i = 0; i < updated.Length; i++)
            if (updated[i].State.Hitpoints > 0) { allDead = false; break; }
        action.Expedition = snap with
        {
            Enemies = updated,
            Outcome = allDead ? CombatOutcome.PlayerWon : snap.Outcome
        };
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
            double Read(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) ? v.GetDouble() : 0;
            foreach (var buff in buffs.EnumerateArray())
            {
                var buffId = buff.GetProperty("buffId").GetString()!;
                // durable buff 用 double.MaxValue 而非 +∞：Strict JSON 下 +∞ 不可序列化，MaxValue 永不触发过期且可落库。
                desired.Add(new ActiveBuff(buffId, double.MaxValue, 1,
                    buff.TryGetProperty("name", out var nameEl) ? nameEl.GetString()! : item.Name,
                    item.Id.Value, Read(buff, "hpRegen"), Read(buff, "mpRegen"),
                    Read(buff, "attackSpeedPoints"), Read(buff, "damageAbsorptionPoints"), null,
                    Read(buff, "hpRegenPostRatio"), Read(buff, "mpRegenPostRatio")));
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
                    && cur.Name == want.Name && cur.HpRegen == want.HpRegen && cur.MpRegen == want.MpRegen
                    && cur.AttackSpeedPoints == want.AttackSpeedPoints
                    && cur.DamageAbsorptionPoints == want.DamageAbsorptionPoints
                    && cur.HpRegenPostRatio == want.HpRegenPostRatio
                    && cur.MpRegenPostRatio == want.MpRegenPostRatio) continue;
            }
            activeBuffs.RemoveAll(b => b.BuffId == want.BuffId);
            activeBuffs.Add(want);
            player.MarkDirty(PlayerSectionKind.Buffs);
            changed = true;
        }
        return changed;
    }
}
