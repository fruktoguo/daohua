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

    internal static DuelStagedItem[] SettleResourceItems(IPlayerResourceContext player,
        double nowUnixSeconds, double settleSeconds)
        => ActionReportBook.SettleStagedItems(player, nowUnixSeconds, settleSeconds);

    internal static bool AdvanceIdleSkillResources(IPlayerResourceContext player, double stepSeconds)
    {
        if (player.Actions.Count > 0 && player.Actions[0].Expedition?.Phase == CombatExpeditionPhase.Combat) return false;
        var skills = player.Combat.Skills;
        if (skills.Cooldowns.Count == 0 && skills.CastingAbilityId is null) return false;
        player.Combat.Skills = skills.AdvanceIdle(stepSeconds);
        player.MarkDirty(PlayerSectionKind.Combat);
        return true;
    }

    internal static bool CompleteResourceRegeneration(IPlayerResourceContext player, GameDataCatalog gameData, double stepSeconds)
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
        player.ExpireBuffs(target.ToUnixTimeMilliseconds() / 1000d);
        if (!player.Combat.Skills.IsEmpty)
        {
            player.Combat.Skills = PlayerCombatSkillState.Empty;
            player.MarkDirty(PlayerSectionKind.Combat);
        }
        player.LastSimulatedAt = target;
        request.AddEvent(new SimulationEvent(target, "offline.capped", "离线补算达到 48 小时上限，超出部分未累积收益。", null));
        return true;
    }

    internal static List<AssetDelta> ActivateInstantConsumables(IPlayerResourceContext player,
        GameDataCatalog catalog, AssetWallet wallet, DateTimeOffset at, SimulationEventTail events,
        LiveCombatBattle? live = null)
    {
        if (player.Actions.Count == 0 || player.Resources.Hitpoints <= 0) return [];
        // 无任何启用槽位时整段跳过：免每步 catalog.Actions.Get 与 MatchesActionType 固定税。
        var slots = player.Settings.AutoUseSlots;
        var anyEnabled = false;
        for (var i = 0; i < slots.Count; i++)
            if (slots[i].Enabled) { anyEnabled = true; break; }
        if (!anyEnabled) return [];
        var kind = catalog.Actions.Get(player.Actions[0].Intent.ActionId).Kind;
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
            if (!slot.Enabled || slot.ItemId is null || !MatchesActionType(slot.ActionType, kind)
                || slot.CooldownUntilUnixSeconds > now) continue;
            var id = new ItemId(slot.ItemId);
            var item = catalog.Items[id];
            if (item.Consumable is not { IsContinuous: false } definition
                || !definition.UsableInActionKinds.Contains(kind)
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
            var cooldown = Read(raw, "cooldownSeconds");
            if (!raw.TryGetProperty("buffs", out var buffs) && hp == 0 && mp == 0) continue;
            if (cooldown <= 0) throw new DomainException(DomainErrorCode.InvalidArgument, "自动即时消耗品必须配置正冷却。");
            player.Resources.Hitpoints = Math.Min(player.Resources.MaxHitpoints, player.Resources.Hitpoints + hp);
            player.Resources.Manapoints = Math.Min(player.Resources.MaxManapoints, player.Resources.Manapoints + mp);
            if (buffs.ValueKind == JsonValueKind.Array)
                foreach (var buff in buffs.EnumerateArray())
                {
                    var buffId = buff.GetProperty("buffId").GetString()!;
                    player.Buffs.Active.RemoveAll(current => current.BuffId == buffId);
                    player.Buffs.Active.Add(new(buffId, now + Read(buff, "durationSeconds"), 1,
                        buff.GetProperty("name").GetString()!, id.Value, Read(buff, "hpRegen"), Read(buff, "mpRegen"),
                        Read(buff, "attackSpeedPoints"), Read(buff, "damageAbsorptionPoints")));
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
        if (player.Actions[0].Expedition is { Phase: CombatExpeditionPhase.Combat, Enemies: { Count: > 0 } waveEnemies })
            for (var i = 0; i < waveEnemies.Count; i++)
            {
                var enemy = waveEnemies[i];
                if (!catalog.CombatMonsters.TryGetValue(enemy.EnemyId, out var monster)) continue;
                var unit = new CombatantState($"enemy:{i}", monster.Stats)
                {
                    Hitpoints = enemy.State.Hitpoints,
                    Manapoints = enemy.State.Manapoints,
                    Threat = enemy.State.Threat,
                    ThreatBonusPoints = enemy.State.ThreatBonusPoints
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
}
